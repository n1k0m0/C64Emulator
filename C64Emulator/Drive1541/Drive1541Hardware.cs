/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Hosts the drive-side 6502, VIA chips, ROM, and custom loader execution.
    /// </summary>
    public sealed class Drive1541Hardware
    {
        private const int RomBootWarmupCycles = 20000000;
        private const int SynchronousRomBootProbeCycles = 250000;
        private const int CustomReceivePreambleResyncCycles = 96;
        /// <summary>
        /// Stores execute context state.
        /// </summary>
        public readonly struct ExecuteContext
        {
            /// <summary>
            /// Initializes a new ExecuteContext instance.
            /// </summary>
            public ExecuteContext(byte accumulator, byte x, byte y, byte stackPointer, byte status)
            {
                Accumulator = accumulator;
                X = x;
                Y = y;
                StackPointer = stackPointer;
                Status = status;
            }

            /// <summary>
            /// Gets the 6502 accumulator value.
            /// </summary>
            public byte Accumulator { get; }

            /// <summary>
            /// Gets the 6502 X register value.
            /// </summary>
            public byte X { get; }

            /// <summary>
            /// Gets the 6502 Y register value.
            /// </summary>
            public byte Y { get; }

            /// <summary>
            /// Gets the 6502 stack pointer value.
            /// </summary>
            public byte StackPointer { get; }

            /// <summary>
            /// Gets the status byte.
            /// </summary>
            public byte Status { get; }
        }

        private static readonly ExecuteContext MemoryExecuteContext = new ExecuteContext(0x45, 0x00, 0x00, 0xFD, 0x24);
        private readonly Drive1541Bus _bus;
        private readonly Cpu6510 _cpu;
        private bool _customCodeActive;
        private int _bootCyclesRemaining;
        private bool _diskMounted;
        private int _customReceivePreambleCycles;
        private bool _runCpuContinuously;

        /// <summary>
        /// Handles the drive1541 hardware operation.
        /// </summary>
        public Drive1541Hardware(IecBusPort iecPort, int deviceNumber)
        {
            _bus = new Drive1541Bus(iecPort, deviceNumber);
            _cpu = new Cpu6510(_bus);
            LoadAvailableRomHalves();
            Reset();
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            _bus.Reset();
            bool hasFullRom = _bus.HasLowerRomLoaded && _bus.HasUpperRomLoaded;
            _cpu.Reset(hasFullRom ? _bus.ReadResetVector() : (ushort)0x0000);
            _bus.SerialOutputsEnabled = false;
            _customCodeActive = false;
            _bootCyclesRemaining = 0;
            _customReceivePreambleCycles = 0;
        }

        /// <summary>
        /// Stops custom code.
        /// </summary>
        public void StopCustomCode()
        {
            _bus.SerialOutputsEnabled = false;
            _customCodeActive = false;
            _customReceivePreambleCycles = 0;
        }

        /// <summary>
        /// Mounts disk.
        /// </summary>
        public void MountDisk(D64Image image)
        {
            _bus.MountDisk(image);
            _diskMounted = image != null;
            if (image != null && !_customCodeActive && HasBootableRom)
            {
                BeginRomBoot();
            }
        }

        /// <summary>
        /// Ejects disk.
        /// </summary>
        public void EjectDisk()
        {
            _bus.EjectDisk();
            _diskMounted = false;
        }

        /// <summary>
        /// Writes the complete drive hardware state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_customCodeActive);
            writer.Write(_bootCyclesRemaining);
            writer.Write(_diskMounted);
            writer.Write(_customReceivePreambleCycles);
            _bus.SaveState(writer);
            _cpu.SaveState(writer);
        }

        /// <summary>
        /// Restores the complete drive hardware state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader, D64Image mountedImage)
        {
            _customCodeActive = reader.ReadBoolean();
            _bootCyclesRemaining = reader.ReadInt32();
            _diskMounted = reader.ReadBoolean();
            _customReceivePreambleCycles = reader.ReadInt32();
            _bus.LoadState(reader, mountedImage);
            _cpu.LoadState(reader);
        }

        /// <summary>
        /// Handles the upload memory operation.
        /// </summary>
        public void UploadMemory(ushort address, byte[] bytes)
        {
            if (bytes == null)
            {
                return;
            }

            for (int index = 0; index < bytes.Length; index++)
            {
                _bus.WriteRam((ushort)(address + index), bytes[index]);
            }
        }

        /// <summary>
        /// Writes memory.
        /// </summary>
        public void WriteMemory(ushort address, byte value)
        {
            _bus.CpuWrite(address, value);
        }

        /// <summary>
        /// Reads memory.
        /// </summary>
        public byte ReadMemory(ushort address)
        {
            return _bus.CpuRead(address);
        }

        /// <summary>
        /// Executes at.
        /// </summary>
        public void ExecuteAt(ushort address)
        {
            ExecuteAt(address, MemoryExecuteContext);
        }

        /// <summary>
        /// Executes at.
        /// </summary>
        public void ExecuteAt(ushort address, ExecuteContext context)
        {
            _bus.PrepareForCustomCodeStart();
            _cpu.A = context.Accumulator;
            _cpu.X = context.X;
            _cpu.Y = context.Y;
            _cpu.SP = context.StackPointer;
            _cpu.SR = context.Status;
            _cpu.PC = address;
            _customCodeActive = true;
            _bootCyclesRemaining = 0;
            _customReceivePreambleCycles = 0;
        }

        /// <summary>
        /// Begins rom boot.
        /// </summary>
        public void BeginRomBoot()
        {
            if (!HasBootableRom)
            {
                return;
            }

            _cpu.Reset(_bus.ReadResetVector());
            _bus.SerialOutputsEnabled = false;
            _customCodeActive = false;
            _bootCyclesRemaining = RomBootWarmupCycles;
            _customReceivePreambleCycles = 0;

            for (int cycle = 0; cycle < SynchronousRomBootProbeCycles && _bootCyclesRemaining > 0; cycle++)
            {
                Tick();
            }
        }

        /// <summary>
        /// Executes user command.
        /// </summary>
        public void ExecuteUserCommand(byte command)
        {
            ushort address = ResolveUserCommandAddress(command);
            if (address == 0)
            {
                return;
            }

            byte y = ResolveUserCommandEntryY(command);
            byte accumulator = (byte)(address >> 8);
            ExecuteAt(address, new ExecuteContext(accumulator, 0x00, y, 0xFD, 0x24));
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            if (_runCpuContinuously || _customCodeActive || _bootCyclesRemaining > 0 || _bus.SerialOutputsEnabled)
            {
                _bus.Tick();
                if (_bus.ConsumeSoPulse())
                {
                    _cpu.AssertSo();
                }

                // Keep custom fastloader code on its own timing path. The old
                // Maniac-specific resync could falsely trigger during the data
                // phase and restart the uploaded receiver while the C64 side
                // was still reading a byte.
                // ResyncManiacReceiveIfNeeded();
                _cpu.Tick();
                if (!_customCodeActive && _bootCyclesRemaining > 0)
                {
                    if (_bus.HasSerialRomInitialization)
                    {
                        CompleteRomBoot();
                    }
                    else
                    {
                        _bootCyclesRemaining--;
                        if (_bootCyclesRemaining <= 0)
                        {
                            CompleteRomBoot();
                        }
                    }
                }
            }
        }

        public bool RunCpuContinuously
        {
            get { return _runCpuContinuously; }
            set { _runCpuContinuously = value; }
        }

        public bool HasCustomCodeActive
        {
            get { return _customCodeActive; }
        }

        public bool IsBooting
        {
            get { return _bootCyclesRemaining > 0; }
        }

        public ushort ProgramCounter
        {
            get { return _cpu.PC; }
        }

        public Cpu6510 Cpu
        {
            get { return _cpu; }
        }

        public Drive1541Bus Bus
        {
            get { return _bus; }
        }

        public bool HasBootableRom
        {
            get { return _bus.HasLowerRomLoaded && _bus.HasUpperRomLoaded; }
        }

        public bool HasSerialRomInitialization
        {
            get { return _bus.HasSerialRomInitialization; }
        }

        public bool IsSerialTransportActive
        {
            get
            {
                return _bus.SerialOutputsEnabled &&
                    (_customCodeActive || (HasBootableRom && _bus.HasSerialRomInitialization));
            }
        }

        public bool IsLedOn
        {
            get { return _bus.IsDiskLedOn; }
        }

        public bool IsMotorOn
        {
            get { return _bus.IsDiskMotorOn; }
        }

        /// <summary>
        /// Loads available rom halves.
        /// </summary>
        private void LoadAvailableRomHalves()
        {
            string lower = RomPathResolver.FindFirstExisting(new[]
            {
                "1541-c000-rom.bin",
                "1541-c000.325302-01.bin"
            });

            string upper = RomPathResolver.FindFirstExisting(new[]
            {
                "1541-e000-rom.bin",
                "1541-e000.901229-01.bin",
                "1541-e000.901229-05.bin",
                "1541-e000.901229-03.bin",
                "1540-e000.325303-01.bin"
            });

            _bus.TryLoadRomHalves(lower, upper);
        }

        /// <summary>
        /// Handles the resolve user command address operation.
        /// </summary>
        private static ushort ResolveUserCommandAddress(byte command)
        {
            switch (char.ToUpperInvariant((char)command))
            {
                case '3':
                case 'C':
                    return 0x0500;
                case '4':
                case 'D':
                    return 0x0503;
                case '5':
                case 'E':
                    return 0x0506;
                case '6':
                case 'F':
                    return 0x0509;
                case '7':
                case 'G':
                    return 0x050C;
                case '8':
                case 'H':
                    return 0x050F;
                default:
                    return 0x0000;
            }
        }

        /// <summary>
        /// Handles the resolve user command entry y operation.
        /// </summary>
        private static byte ResolveUserCommandEntryY(byte command)
        {
            // Real 1541 loader stubs commonly key off the USER command slot
            // using 0x20/0x30/0x40... rather than the raw ASCII digit. Maniac
            // Mansion's uploaded drive code does exactly that via
            //   CPY #$20 / #$30 / #$40
            // for U3/U4/U5. Mirror that entry contract here so uploaded drive
            // code sees the same slot selector that it would receive from the
            // ROM user-command dispatcher.
            switch (char.ToUpperInvariant((char)command))
            {
                case '3':
                case 'C':
                    return 0x20;
                case '4':
                case 'D':
                    return 0x30;
                case '5':
                case 'E':
                    return 0x40;
                case '6':
                case 'F':
                    return 0x50;
                case '7':
                case 'G':
                    return 0x60;
                case '8':
                case 'H':
                    return 0x70;
                default:
                    return 0x00;
            }
        }

        /// <summary>
        /// Handles the resync maniac receive if needed operation.
        /// </summary>
        private void ResyncManiacReceiveIfNeeded()
        {
            if (!_customCodeActive || !_bus.HasManiacMansionLoaderStub)
            {
                _customReceivePreambleCycles = 0;
                return;
            }

            bool stuckInReceiveClockLowWait =
                _cpu.PC >= 0x06B3 &&
                _cpu.PC <= 0x06B8 &&
                _cpu.X != 0x04;

            bool commandPreamble =
                _bus.IsC64DataLineLow &&
                !_bus.IsC64ClockLineLow;

            if (!stuckInReceiveClockLowWait || !commandPreamble)
            {
                _customReceivePreambleCycles = 0;
                return;
            }

            _customReceivePreambleCycles++;
            if (_customReceivePreambleCycles < CustomReceivePreambleResyncCycles)
            {
                return;
            }

            if (_cpu.State != CpuState.FetchOpcode)
            {
                return;
            }

            // Maniac Mansion's uploaded 1541 code can arrive mid-byte after
            // U3 handoff. A sustained C64 DATA-low/CLOCK-high preamble marks
            // the next command frame, so restart the receiver on byte 0.
            DiscardManiacReceiveReturnAddressIfPresent();
            _bus.WriteRam(0x0006, 0x00);
            _cpu.PC = 0x0626;
            _customReceivePreambleCycles = 0;
        }

        /// <summary>
        /// Handles the discard maniac receive return address if present operation.
        /// </summary>
        private void DiscardManiacReceiveReturnAddressIfPresent()
        {
            int stackPointer = _cpu.SP;
            int lowAddress = 0x0100 | ((stackPointer + 1) & 0xFF);
            int highAddress = 0x0100 | ((stackPointer + 2) & 0xFF);
            byte returnLow = _bus.ReadRam((ushort)lowAddress);
            byte returnHigh = _bus.ReadRam((ushort)highAddress);

            // The resync can only happen while the drive is inside the byte
            // receiver subroutine at $06AE. Throw away that pending return so
            // the restarted command receiver still returns to its caller once.
            if (returnHigh == 0x06 &&
                (returnLow == 0x43 || returnLow == 0x48 || returnLow == 0x4D))
            {
                _cpu.SP = (byte)(stackPointer + 2);
            }
        }

        /// <summary>
        /// Handles the complete rom boot operation.
        /// </summary>
        private void CompleteRomBoot()
        {
            _bootCyclesRemaining = 0;
            _bus.SerialOutputsEnabled = true;
            _customReceivePreambleCycles = 0;
        }
    }
}
