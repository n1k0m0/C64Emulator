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
    /// Maps 1541 CPU memory, ROM, RAM, VIA registers, and disk job state.
    /// </summary>
    public sealed class Drive1541Bus : ICpuBus
    {
        private readonly byte[] _ram = new byte[0x0800];
        private readonly byte[] _rom = new byte[0x4000];
        private readonly DriveVia6522 _serialVia = new DriveVia6522();
        private readonly DriveVia6522 _diskVia = new DriveVia6522();
        private readonly Drive1541Mechanism _mechanism = new Drive1541Mechanism();
        private readonly IecBusPort _iecPort;
        private readonly byte _deviceSelectorBits;
        private bool _piAtn;
        private bool _piData;
        private bool _piClock;
        private bool _viaAtna;
        private bool _viaData;
        private bool _viaClock;
        private bool _dataSetToOut;
        private bool _clockSetToOut;
        private bool _atnaDataSetToOut;
        private bool _lowerRomLoaded;
        private bool _upperRomLoaded;
        private bool _serialOutputsEnabled;
        private bool _deferSerialOutputsUntilPortWrite;
        private string _lastJobQueueDebug = "-";

        /// <summary>
        /// Initializes a new Drive1541Bus instance.
        /// </summary>
        public Drive1541Bus(IecBusPort iecPort, int deviceNumber)
        {
            _iecPort = iecPort;
            _deviceSelectorBits = (byte)Math.Max(0, Math.Min(3, deviceNumber - 8));
            _serialVia.PortBReadProvider = ReadSerialPortB;
            _serialVia.PortBWritten = WriteSerialPortB;
            _diskVia.PortAReadProvider = ReadDiskPortA;
            _diskVia.PortAWritten = WriteDiskPortA;
            _diskVia.PortBReadProvider = ReadDiskPortB;
            _diskVia.PortBWritten = WriteDiskPortB;
            _iecPort.RegisterLineChangeListener(HandleIecLineChanged);
        }

        public bool CpuCanAccess
        {
            get { return true; }
        }

        /// <summary>
        /// Returns whether irq asserted is true.
        /// </summary>
        public bool IsIrqAsserted()
        {
            return _diskVia.IsIrqAsserted || _serialVia.IsIrqAsserted;
        }

        /// <summary>
        /// Returns whether nmi asserted is true.
        /// </summary>
        public bool IsNmiAsserted()
        {
            return false;
        }

        /// <summary>
        /// Handles the cpu read operation.
        /// </summary>
        public byte CpuRead(ushort address)
        {
            if (address < 0x1800)
            {
                return _ram[address & 0x07FF];
            }

            if (address >= 0x1800 && address <= 0x1BFF)
            {
                return _serialVia.Read((ushort)(address & 0x000F));
            }

            if (address >= 0x1C00 && address <= 0x1FFF)
            {
                return _diskVia.Read((ushort)(address & 0x000F));
            }

            if (address >= 0x8000)
            {
                return _rom[address & 0x3FFF];
            }

            return (byte)(address >> 8);
        }

        /// <summary>
        /// Handles the cpu write operation.
        /// </summary>
        public void CpuWrite(ushort address, byte value)
        {
            if (address < 0x1800)
            {
                _ram[address & 0x07FF] = value;
                return;
            }

            if (address >= 0x1800 && address <= 0x1BFF)
            {
                _serialVia.Write((ushort)(address & 0x000F), value);
                return;
            }

            if (address >= 0x1C00 && address <= 0x1FFF)
            {
                _diskVia.Write((ushort)(address & 0x000F), value);
            }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_ram, 0, _ram.Length);
            _diskVia.Reset();
            _serialVia.Reset();
            _mechanism.Reset();
            _piAtn = false;
            _piData = false;
            _piClock = false;
            _viaAtna = false;
            _viaData = false;
            _viaClock = false;
            _dataSetToOut = false;
            _clockSetToOut = false;
            _atnaDataSetToOut = false;
            _serialOutputsEnabled = false;
            _deferSerialOutputsUntilPortWrite = false;
            _lastJobQueueDebug = "-";
            UpdateSerialInputs();
        }

        /// <summary>
        /// Mounts disk.
        /// </summary>
        public void MountDisk(D64Image image)
        {
            _mechanism.MountDisk(image);
        }

        /// <summary>
        /// Ejects disk.
        /// </summary>
        public void EjectDisk()
        {
            _mechanism.EjectDisk();
        }

        /// <summary>
        /// Writes the complete 1541 bus state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            StateSerializer.WriteObjectFields(writer, this, "_iecPort", "_serialVia", "_diskVia", "_mechanism");
            _serialVia.SaveState(writer);
            _diskVia.SaveState(writer);
            _mechanism.SaveState(writer);
        }

        /// <summary>
        /// Restores the complete 1541 bus state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader, D64Image mountedImage)
        {
            StateSerializer.ReadObjectFields(reader, this, "_iecPort", "_serialVia", "_diskVia", "_mechanism");
            _serialVia.LoadState(reader);
            _diskVia.LoadState(reader);
            _mechanism.LoadState(reader, mountedImage);
            RefreshSerialLineLevels(true);
            ApplySerialOutputs();
        }

        /// <summary>
        /// Writes ram.
        /// </summary>
        public void WriteRam(ushort address, byte value)
        {
            if (address < 0x1800)
            {
                _ram[address & 0x07FF] = value;
            }
        }

        /// <summary>
        /// Reads ram.
        /// </summary>
        public byte ReadRam(ushort address)
        {
            return address < 0x1800 ? _ram[address & 0x07FF] : (byte)0x00;
        }

        /// <summary>
        /// Loads rom.
        /// </summary>
        public void LoadRom(byte[] romBytes)
        {
            Array.Clear(_rom, 0, _rom.Length);
            if (romBytes != null)
            {
                Array.Copy(romBytes, 0, _rom, 0, Math.Min(_rom.Length, romBytes.Length));
                _lowerRomLoaded = romBytes.Length >= 0x2000;
                _upperRomLoaded = romBytes.Length >= 0x4000;
            }
            else
            {
                _lowerRomLoaded = false;
                _upperRomLoaded = false;
            }
        }

        /// <summary>
        /// Loads rom halves.
        /// </summary>
        public void LoadRomHalves(byte[] lowerRomBytes, byte[] upperRomBytes)
        {
            Array.Clear(_rom, 0, _rom.Length);

            _lowerRomLoaded = lowerRomBytes != null && lowerRomBytes.Length >= 0x2000;
            _upperRomLoaded = upperRomBytes != null && upperRomBytes.Length >= 0x2000;

            if (_lowerRomLoaded)
            {
                Array.Copy(lowerRomBytes, 0, _rom, 0x0000, 0x2000);
            }

            if (_upperRomLoaded)
            {
                Array.Copy(upperRomBytes, 0, _rom, 0x2000, 0x2000);
            }
        }

        /// <summary>
        /// Attempts to load rom from file and reports whether it succeeded.
        /// </summary>
        public bool TryLoadRomFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            LoadRom(File.ReadAllBytes(path));
            return true;
        }

        /// <summary>
        /// Attempts to load rom halves and reports whether it succeeded.
        /// </summary>
        public bool TryLoadRomHalves(string lowerPath, string upperPath)
        {
            byte[] lowerRom = null;
            byte[] upperRom = null;

            if (!string.IsNullOrWhiteSpace(lowerPath) && File.Exists(lowerPath))
            {
                lowerRom = File.ReadAllBytes(lowerPath);
            }

            if (!string.IsNullOrWhiteSpace(upperPath) && File.Exists(upperPath))
            {
                upperRom = File.ReadAllBytes(upperPath);
            }

            LoadRomHalves(lowerRom, upperRom);
            return _lowerRomLoaded || _upperRomLoaded;
        }

        /// <summary>
        /// Reads reset vector.
        /// </summary>
        public ushort ReadResetVector()
        {
            if (!_upperRomLoaded)
            {
                return 0x0000;
            }

            int offset = 0x3FFC;
            return (ushort)(_rom[offset] | (_rom[offset + 1] << 8));
        }

        public bool HasLowerRomLoaded
        {
            get { return _lowerRomLoaded; }
        }

        public bool HasUpperRomLoaded
        {
            get { return _upperRomLoaded; }
        }

        public bool HasSerialRomInitialization
        {
            get
            {
                return _serialVia.PortBDirection != 0x00 ||
                    _serialVia.PortADirection != 0x00 ||
                    _serialVia.PeripheralControlRegister != 0x00 ||
                    _serialVia.AuxiliaryControlRegister != 0x00;
            }
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            UpdateSerialInputs();
            _mechanism.Tick(_diskVia);
            ServiceDosJobQueue();
            _diskVia.Tick();
            _serialVia.Tick();
            UpdateSerialInputs();
        }

        /// <summary>
        /// Handles the consume so pulse operation.
        /// </summary>
        public bool ConsumeSoPulse()
        {
            return _mechanism.ConsumeSoPulse();
        }

        public bool IsDiskLedOn
        {
            get { return _mechanism.LedOn; }
        }

        public bool IsDiskMotorOn
        {
            get { return _mechanism.MotorOn; }
        }

        /// <summary>
        /// Gets the serial debug info value.
        /// </summary>
        public string GetSerialDebugInfo()
        {
            return string.Format(
                "orb={0:X2} ddrb={1:X2} pcr={2:X2} acr={3:X2} ifr={4:X2} ier={5:X2} viaAtna={6} viaData={7} viaClock={8} piAtn={9} piData={10} piClock={11} dataOut={12} clockOut={13} atnaDataOut={14} serialOutEn={15}",
                _serialVia.PortBOutput,
                _serialVia.PortBDirection,
                _serialVia.PeripheralControlRegister,
                _serialVia.AuxiliaryControlRegister,
                _serialVia.InterruptFlags,
                _serialVia.InterruptEnable,
                _viaAtna,
                _viaData,
                _viaClock,
                _piAtn,
                _piData,
                _piClock,
                _dataSetToOut,
                _clockSetToOut,
                _atnaDataSetToOut,
                _serialOutputsEnabled);
        }

        /// <summary>
        /// Gets the disk debug info value.
        /// </summary>
        public string GetDiskDebugInfo()
        {
            return string.Format(
                "diskA={0:X2}/{1:X2} diskB={2:X2}/{3:X2} motor={4} led={5} htrk={6}",
                _diskVia.PortAOutput,
                _diskVia.PortADirection,
                _diskVia.PortBOutput,
                _diskVia.PortBDirection,
                _mechanism.MotorOn,
                _mechanism.LedOn,
                _mechanism.CurrentHalfTrack) + " job=" + _lastJobQueueDebug;
        }

        public int CurrentHalfTrack
        {
            get { return _mechanism.CurrentHalfTrack; }
        }

        public bool IsC64DataLineLow
        {
            get { return _iecPort.IsOwnerDrivingLineLow("C64", IecBusLine.Data); }
        }

        public bool IsC64ClockLineLow
        {
            get { return _iecPort.IsOwnerDrivingLineLow("C64", IecBusLine.Clock); }
        }

        public bool HasManiacMansionLoaderStub
        {
            get
            {
                return _ram[0x0500] == 0x20 &&
                    _ram[0x0501] == 0x5D &&
                    _ram[0x0502] == 0x06 &&
                    _ram[0x0626] == 0xA9 &&
                    _ram[0x0627] == 0x08 &&
                    _ram[0x06AE] == 0xA2 &&
                    _ram[0x06AF] == 0x04;
            }
        }

        public bool SerialOutputsEnabled
        {
            get { return _serialOutputsEnabled; }
            set
            {
                if (_serialOutputsEnabled == value)
                {
                    return;
                }

                _serialOutputsEnabled = value;
                _deferSerialOutputsUntilPortWrite = false;
                if (!_serialOutputsEnabled)
                {
                    _iecPort.SetLineLow(IecBusLine.Data, false);
                    _iecPort.SetLineLow(IecBusLine.Clock, false);
                    RefreshSerialLineLevels(true);
                    return;
                }

                RefreshSerialLineLevels(true);
                PulseActiveAtnToSerialVia();
            }
        }

        /// <summary>
        /// Handles the prepare for custom code start operation.
        /// </summary>
        public void PrepareForCustomCodeStart()
        {
            _serialOutputsEnabled = false;
            _deferSerialOutputsUntilPortWrite = true;
            // Keep the serial VIA state intact when custom drive code takes
            // over. Fast loaders upload their own code on top of the already
            // initialised DOS/ROM environment and expect timer/PCR/DDR state
            // as well as the current port image to survive the handover. We
            // only release the external IEC lines here and defer re-enabling
            // them until the custom code performs its first explicit port
            // write.
            _iecPort.SetLineLow(IecBusLine.Data, false);
            _iecPort.SetLineLow(IecBusLine.Clock, false);
            // The command-channel phase often ends with ATN still asserted.
            // Sync CA1 to that level without generating a fresh IRQ edge,
            // otherwise the uploaded loader hits CLI and immediately vectors
            // back into the ROM ATN handler instead of staying in its own
            // serial routine.
            RefreshSerialLineLevels(false);
            _serialVia.SetCa1LevelSilently(_piAtn);
            // Keep the current VIA interrupt enables and timer setup intact.
            // Uploaded fastloaders are installed on top of the already running
            // DOS environment and often rely on the serial VIA continuing to
            // generate IRQs once they execute CLI. Clearing IER/IFR here left
            // Maniac Mansion's drive code spinning forever after U3 because
            // its handshake state byte was only advanced from the IRQ side.
            // The custom code is responsible for masking or acknowledging
            // interrupts explicitly if it wants a different serial state.
        }

        /// <summary>
        /// Reads serial port b.
        /// </summary>
        private byte ReadSerialPortB(byte orb, byte ddrb)
        {
            RefreshSerialLineLevels(false);
            byte value = 0x00;

            // On a real 1541 the IEC inputs are inverted before they reach the
            // serial VIA. That means asserted/low IEC DATA/CLOCK/ATN appear as
            // logical 1 on PB0/PB2/PB7.
            if (_piData)
            {
                value |= 0x01;
            }

            // Just like Pi1541 models it, the 1541 VIA output pins read back
            // as pulled high once the corresponding pin direction is switched
            // to input. Custom drive code polls these bits directly.
            if ((ddrb & 0x02) == 0)
            {
                value |= 0x02;
            }

            if (_piClock)
            {
                value |= 0x04;
            }

            if ((ddrb & 0x08) == 0)
            {
                value |= 0x08;
            }

            if ((ddrb & 0x10) == 0)
            {
                value |= 0x10;
            }

            if (_piAtn)
            {
                value |= 0x80;
            }

            if ((_deviceSelectorBits & 0x01) != 0)
            {
                value |= 0x20;
            }

            if ((_deviceSelectorBits & 0x02) != 0)
            {
                value |= 0x40;
            }

            return value;
        }

        /// <summary>
        /// Writes serial port b.
        /// </summary>
        private void WriteSerialPortB(byte orb, byte ddrb)
        {
            _viaAtna = (orb & 0x10) != 0;
            _viaData = (orb & 0x02) != 0;
            _viaClock = (orb & 0x08) != 0;
            RecomputeAtnaDataSetToOut(ddrb);
            // Pi1541/VICE both model the 1541 serial VIA in a slightly odd
            // but hardware-faithful way: once PB1/PB3 are switched to input
            // the port still reads them back as asserted/high and the serial
            // gate logic continues from that port image. Fastloaders like
            // Maniac Mansion rely on this while handing off from the command
            // channel to their uploaded drive code. Keep PB4 special-cased
            // separately via RecomputeAtnaDataSetToOut().
            if ((ddrb & 0x02) == 0)
            {
                _viaData = true;
            }

            if ((ddrb & 0x08) == 0)
            {
                _viaClock = true;
            }

            // Match Pi1541's emulation of the 1541 serial VIA: PB1/PB3 are
            // the logical "assert IEC DATA/CLOCK" signals after the external
            // inverter/open-collector stage has been accounted for. A high
            // VIA port image therefore means "pull the IEC line low" here.
            _dataSetToOut = (ddrb & 0x02) != 0 && _viaData;
            _clockSetToOut = (ddrb & 0x08) != 0 && _viaClock;
            if (_deferSerialOutputsUntilPortWrite)
            {
                _serialOutputsEnabled = true;
                _deferSerialOutputsUntilPortWrite = false;
            }
            ApplySerialOutputs();
            UpdateSerialInputs();
        }

        /// <summary>
        /// Updates serial inputs.
        /// </summary>
        private void UpdateSerialInputs()
        {
            RefreshSerialLineLevels(true);
            ApplySerialOutputs();
        }

        /// <summary>
        /// Handles iec line changed.
        /// </summary>
        private void HandleIecLineChanged(IecBusLine line, bool isLow)
        {
            switch (line)
            {
                case IecBusLine.Atn:
                    _piAtn = isLow;
                    UpdateSerialViaAtnInput();
                    RecomputeAtnaDataSetToOut(_serialVia.PortBDirection);
                    ApplySerialOutputs();
                    UpdateObservedDataLine();
                    UpdateObservedClockLine();
                    break;

                case IecBusLine.Data:
                    UpdateObservedDataLine();
                    break;

                case IecBusLine.Clock:
                    UpdateObservedClockLine();
                    break;
            }
        }

        /// <summary>
        /// Handles the refresh serial line levels operation.
        /// </summary>
        private void RefreshSerialLineLevels(bool notifyCa1)
        {
            _piAtn = _iecPort.IsLineLow(IecBusLine.Atn);
            if (notifyCa1)
            {
                UpdateSerialViaAtnInput();
            }

            RecomputeAtnaDataSetToOut(_serialVia.PortBDirection);
            if ((_serialVia.PortBDirection & 0x10) == 0)
            {
                // Pi1541 and real hardware both stop the ATN/ATNA XOR path
                // from actively pulling DATA low once PB4 (ATNA) is switched
                // to input. Maniac Mansion's uploaded loader relies on this
                // transition while it hands control from the command channel
                // to its custom serial routine.
                _atnaDataSetToOut = false;
            }
            ApplySerialOutputs();
            UpdateObservedDataLine();
            UpdateObservedClockLine();
        }

        /// <summary>
        /// Handles the recompute atna data set to out operation.
        /// </summary>
        private void RecomputeAtnaDataSetToOut(byte ddrb)
        {
            // Match Pi1541 / real 1541 wiring: the ATN/ATNA XOR path can only
            // pull IEC DATA low while PB4 (ATNA) is configured as an output.
            // As soon as PB4 becomes an input the XOR path must release DATA.
            // Maniac Mansion explicitly relies on that transition when it
            // hands over from the command channel into uploaded drive code.
            if ((ddrb & 0x10) == 0)
            {
                _atnaDataSetToOut = false;
                return;
            }

            _atnaDataSetToOut = _viaAtna != _piAtn;
        }

        /// <summary>
        /// Applies serial outputs.
        /// </summary>
        private void ApplySerialOutputs()
        {
            if (!_serialOutputsEnabled)
            {
                _iecPort.SetLines(clockLow: false, dataLow: false);
                return;
            }

            _iecPort.SetLines(
                clockLow: _clockSetToOut,
                dataLow: _dataSetToOut || _atnaDataSetToOut);
        }

        /// <summary>
        /// Updates observed data line.
        /// </summary>
        private void UpdateObservedDataLine()
        {
            // Match Pi1541/VICE readback behavior: once the drive is actively
            // pulling DATA low (either directly or through the ATN/ATNA XOR
            // path), PB0 reads back as logically asserted/high.
            _piData = (_atnaDataSetToOut || _dataSetToOut)
                ? true
                : _iecPort.IsLineLow(IecBusLine.Data);
        }

        /// <summary>
        /// Updates observed clock line.
        /// </summary>
        private void UpdateObservedClockLine()
        {
            // Likewise, a drive that is currently pulling CLOCK low sees the
            // corresponding VIA input bit asserted/high on readback.
            _piClock = _clockSetToOut
                ? true
                : _iecPort.IsLineLow(IecBusLine.Clock);
        }

        /// <summary>
        /// Pulses active atn to serial via.
        /// </summary>
        private void PulseActiveAtnToSerialVia()
        {
            bool atnLow = _iecPort.IsLineLow(IecBusLine.Atn);
            _piAtn = atnLow;

            if (!atnLow)
            {
                _serialVia.SetCa1Level(false);
                return;
            }

            // If the 1541 ROM finishes initializing its serial VIA while ATN is
            // already asserted, the normal edge can be missed entirely. We
            // synthesize the current ATN assertion as a fresh CA1 transition so
            // the ROM serial state machine wakes up just like it would on
            // hardware after an in-flight device attention.
            _serialVia.SetCa1Level(false);
            _serialVia.SetCa1Level(true);
        }

        /// <summary>
        /// Updates serial via atn input.
        /// </summary>
        private void UpdateSerialViaAtnInput()
        {
            // Pi1541 models CA1 with the same logical ATN sense as PB7:
            // asserted IEC ATN (bus low) appears as a logical 1 on the 1541
            // side. The ROM programs VIA1 CA1 for positive-edge interrupts,
            // so feeding the already-inverted "ATN asserted" sense directly
            // into CA1 makes it wake up on ATN assertion, not on release.
            _serialVia.SetCa1Level(_piAtn);
        }

        /// <summary>
        /// Reads disk port a.
        /// </summary>
        private byte ReadDiskPortA(byte ora, byte ddra)
        {
            return _mechanism.ReadViaPortA(ora, ddra);
        }

        /// <summary>
        /// Handles the service dos job queue operation.
        /// </summary>
        private void ServiceDosJobQueue()
        {
            // The 1541 DOS job queue is used by many uploaded drive-side
            // fastloaders. Buffer/job slot 4 maps to job byte $0004,
            // track/sector bytes $000E/$000F and buffer $0700-$07FF.
            byte job = _ram[0x0004];
            if ((job & 0x80) == 0)
            {
                return;
            }

            int command = job & 0xF0;
            int track = _ram[0x000E];
            int sector = _ram[0x000F];
            bool ok = false;

            switch (command)
            {
                case 0x80: // READ
                    ok = TryReadJobSector(track, sector);
                    break;

                case 0x90: // WRITE
                    ok = TryWriteJobSector(track, sector);
                    break;

                case 0xA0: // VERIFY
                case 0xB0: // SEEK
                    ok = _mechanism.TryReadSector(track, sector, out _);
                    break;

                case 0xC0: // BUMP/RESTORE class jobs complete without a data buffer.
                    ok = true;
                    break;
            }

            _ram[0x0004] = ok ? (byte)0x01 : (byte)0x02;
            _lastJobQueueDebug = string.Format("{0:X2}@{1}/{2}:{3}", job, track, sector, ok ? "OK" : "ERR");
        }

        /// <summary>
        /// Attempts to read job sector and reports whether it succeeded.
        /// </summary>
        private bool TryReadJobSector(int track, int sector)
        {
            byte[] sectorBytes;
            if (!_mechanism.TryReadSector(track, sector, out sectorBytes) || sectorBytes == null || sectorBytes.Length < 256)
            {
                return false;
            }

            Array.Copy(sectorBytes, 0, _ram, 0x0700, 256);
            return true;
        }

        /// <summary>
        /// Attempts to write job sector and reports whether it succeeded.
        /// </summary>
        private bool TryWriteJobSector(int track, int sector)
        {
            var sectorBytes = new byte[256];
            Array.Copy(_ram, 0x0700, sectorBytes, 0, 256);
            return _mechanism.TryWriteSector(track, sector, sectorBytes);
        }

        /// <summary>
        /// Writes disk port a.
        /// </summary>
        private void WriteDiskPortA(byte ora, byte ddra)
        {
            _mechanism.WriteViaPortA(ora, ddra);
        }

        /// <summary>
        /// Reads disk port b.
        /// </summary>
        private byte ReadDiskPortB(byte orb, byte ddrb)
        {
            return _mechanism.ReadViaPortB(orb, ddrb);
        }

        /// <summary>
        /// Writes disk port b.
        /// </summary>
        private void WriteDiskPortB(byte orb, byte ddrb)
        {
            _mechanism.ApplyViaPortB(orb, ddrb);
        }
    }
}
