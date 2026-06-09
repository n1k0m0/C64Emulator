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
    /// Lists the supported bus owner values.
    /// </summary>
    public enum BusOwner
    {
        None,
        Cpu,
        Vic
    }

    /// <summary>
    /// Represents the system bus component.
    /// </summary>
    public sealed class SystemBus : ICpuBus
    {
        private readonly byte[] _ram = new byte[65536];
        private readonly byte[] _basicRom = new byte[8192];
        private readonly byte[] _kernalRom = new byte[8192];
        private readonly byte[] _charRom = new byte[4096];
        private readonly byte[] _colorRam = new byte[1024];
        private readonly byte[] _ioRam = new byte[4096];

        private Vic2 _vic;
        private Cia1 _cia1;
        private Cia2 _cia2;
        private Sid _sid;
        private EasyFlashCartridge _easyFlash;
        private ReuExpansion _reu;
        private bool _externalIrqAsserted;
        private bool _externalNmiAsserted;
        private byte _lastCpuBusValue;
        private byte _lastVicBusValue;
        private byte _processorPortInputLatch;
        private bool _baLow;
        private bool _aecLow;
        private bool _cpuPhi2CanAccess;
        private bool _vicPhi2CanAccess;

        /// <summary>
        /// Gets the current bus owner.
        /// </summary>
        public BusOwner Owner { get; private set; }

        public bool CpuCanAccess
        {
            get { return _cpuPhi2CanAccess; }
        }

        public bool VicCanAccess
        {
            get { return _vicPhi2CanAccess; }
        }

        public bool BaLow
        {
            get { return _baLow; }
        }

        public bool AecLow
        {
            get { return _aecLow; }
        }

        /// <summary>
        /// Handles the connect operation.
        /// </summary>
        public void Connect(Vic2 vic, Cia1 cia1, Cia2 cia2, Sid sid)
        {
            _vic = vic;
            _cia1 = cia1;
            _cia2 = cia2;
            _sid = sid;
        }

        /// <summary>
        /// Sets the owner value.
        /// </summary>
        public void SetOwner(BusOwner owner)
        {
            switch (owner)
            {
                case BusOwner.Vic:
                    SetPhi2BusState(true, true, false, true);
                    break;
                case BusOwner.Cpu:
                    SetPhi2BusState(false, false, true, false);
                    break;
                default:
                    SetPhi2BusState(true, false, false, false);
                    break;
            }
        }

        /// <summary>
        /// Sets the phi2 bus state value.
        /// </summary>
        public void SetPhi2BusState(bool baLow, bool aecLow, bool cpuCanAccess, bool vicCanAccess)
        {
            _baLow = baLow;
            _aecLow = aecLow;
            _cpuPhi2CanAccess = cpuCanAccess;
            _vicPhi2CanAccess = vicCanAccess;

            if (vicCanAccess)
            {
                Owner = BusOwner.Vic;
            }
            else if (cpuCanAccess)
            {
                Owner = BusOwner.Cpu;
            }
            else
            {
                Owner = BusOwner.None;
            }
        }

        /// <summary>
        /// Handles the initialize memory operation.
        /// </summary>
        public void InitializeMemory()
        {
            Array.Clear(_ram, 0, _ram.Length);
            Array.Clear(_basicRom, 0, _basicRom.Length);
            Array.Clear(_kernalRom, 0, _kernalRom.Length);
            Array.Clear(_charRom, 0, _charRom.Length);
            Array.Clear(_colorRam, 0, _colorRam.Length);
            Array.Clear(_ioRam, 0, _ioRam.Length);

            _ram[0] = 0x2F;
            _ram[1] = 0x37;
            _externalIrqAsserted = false;
            _externalNmiAsserted = false;
            _lastCpuBusValue = 0xFF;
            _lastVicBusValue = 0xFF;
            _processorPortInputLatch = _ram[1];
            _baLow = false;
            _aecLow = false;
            _cpuPhi2CanAccess = true;
            _vicPhi2CanAccess = false;
            Owner = BusOwner.Cpu;
            if (_easyFlash != null)
            {
                _easyFlash.Reset();
            }

            if (_reu != null)
            {
                _reu.Reset();
            }
        }

        /// <summary>
        /// Loads roms.
        /// </summary>
        public void LoadRoms(string basePath)
        {
            var combinedRom = File.ReadAllBytes(RomPathResolver.ResolveRequired("c64-basic-kernal.bin", basePath));
            Array.Copy(combinedRom, 0x0000, _basicRom, 0x0000, 0x2000);
            Array.Copy(combinedRom, 0x2000, _kernalRom, 0x0000, 0x2000);

            var charRom = File.ReadAllBytes(RomPathResolver.ResolveRequired("c64-character.bin", basePath));
            Array.Copy(charRom, 0, _charRom, 0, Math.Min(_charRom.Length, charRom.Length));
        }

        /// <summary>
        /// Reads reset vector.
        /// </summary>
        public ushort ReadResetVector()
        {
            if (_easyFlash != null && _easyFlash.TryReadResetVector(out ushort cartridgeResetVector))
            {
                return cartridgeResetVector;
            }

            var lo = _kernalRom[0x1FFC];
            var hi = _kernalRom[0x1FFD];
            return (ushort)(lo | (hi << 8));
        }

        /// <summary>
        /// Inserts an EasyFlash cartridge into the expansion port.
        /// </summary>
        public void InsertEasyFlash(EasyFlashCartridge cartridge)
        {
            _easyFlash = cartridge;
            if (_easyFlash != null)
            {
                _easyFlash.Reset();
            }
        }

        /// <summary>
        /// Removes the inserted EasyFlash cartridge.
        /// </summary>
        public void EjectEasyFlash()
        {
            _easyFlash = null;
        }

        /// <summary>
        /// Gets the inserted EasyFlash cartridge, if any.
        /// </summary>
        public EasyFlashCartridge EasyFlash
        {
            get { return _easyFlash; }
        }

        /// <summary>
        /// Gets or sets whether the inserted EasyFlash currently drives the bus.
        /// </summary>
        public bool EasyFlashEnabled
        {
            get { return _easyFlash != null && _easyFlash.Enabled; }
            set
            {
                if (_easyFlash != null)
                {
                    _easyFlash.Enabled = value;
                }
            }
        }

        /// <summary>
        /// Configures or creates the RAM Expansion Unit attached to the expansion port.
        /// </summary>
        public void ConfigureReu(bool enabled, ReuMemorySize size)
        {
            if (_reu == null)
            {
                _reu = new ReuExpansion(size);
            }
            else
            {
                _reu.ConfigureSize(size);
            }

            _reu.Enabled = enabled;
        }

        /// <summary>
        /// Inserts a previously captured REU instance.
        /// </summary>
        public void InsertReu(ReuExpansion reu)
        {
            _reu = reu;
        }

        /// <summary>
        /// Gets the configured REU, if any.
        /// </summary>
        public ReuExpansion Reu
        {
            get { return _reu; }
        }

        /// <summary>
        /// Gets whether the REU currently appears on the expansion port.
        /// </summary>
        public bool ReuEnabled
        {
            get { return _reu != null && _reu.Enabled; }
            set
            {
                if (_reu == null)
                {
                    _reu = new ReuExpansion();
                }

                _reu.Enabled = value;
            }
        }

        /// <summary>
        /// Gets whether the REU currently owns CPU bus cycles for DMA.
        /// </summary>
        public bool IsReuDmaActive
        {
            get { return _reu != null && _reu.IsDmaActive; }
        }

        /// <summary>
        /// Runs one available REU DMA cycle.
        /// </summary>
        public void TickReuDmaCycle()
        {
            if (_reu == null)
            {
                return;
            }

            _reu.TickDmaCycle(ReadC64ForReu, WriteC64ForReu);
        }

        /// <summary>
        /// Writes the complete system bus state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            StateSerializer.WriteObjectFields(writer, this, "_vic", "_cia1", "_cia2", "_sid");
        }

        /// <summary>
        /// Restores the complete system bus state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            StateSerializer.ReadObjectFields(reader, this, "_vic", "_cia1", "_cia2", "_sid");
        }

        /// <summary>
        /// Handles the cpu read operation.
        /// </summary>
        public byte CpuRead(ushort address)
        {
            byte value;
            if (address == 0 || address == 1)
            {
                if (address == 1)
                {
                    value = GetProcessorPortValue();
                    _lastCpuBusValue = value;
                    return value;
                }

                value = _ram[0];
                _lastCpuBusValue = value;
                return value;
            }

            if (_easyFlash != null && _easyFlash.TryRead(address, GetProcessorPortValue(), out value))
            {
                _lastCpuBusValue = value;
                return value;
            }

            if (address >= 0xA000 && address <= 0xBFFF && IsBasicRomVisible())
            {
                value = _basicRom[address - 0xA000];
                _lastCpuBusValue = value;
                return value;
            }

            if (address >= 0xD000 && address <= 0xDFFF)
            {
                if (IsIoVisible())
                {
                    value = ReadIo(address);
                    _lastCpuBusValue = value;
                    return value;
                }

                if (IsCharacterRomVisibleToCpu())
                {
                    value = _charRom[address - 0xD000];
                    _lastCpuBusValue = value;
                    return value;
                }
            }

            if (address >= 0xE000 && address <= 0xFFFF && IsKernalRomVisible())
            {
                value = _kernalRom[address - 0xE000];
                _lastCpuBusValue = value;
                return value;
            }

            if (IsUltimaxUnmappedCpuRead(address))
            {
                value = 0xFF;
                _lastCpuBusValue = value;
                return value;
            }

            value = _ram[address];
            _lastCpuBusValue = value;
            return value;
        }

        /// <summary>
        /// Reads the CPU-visible value without mutating bus latches or I/O registers.
        /// </summary>
        public byte PeekCpuRead(ushort address)
        {
            if (address == 0)
            {
                return _ram[0];
            }

            if (address == 1)
            {
                return GetProcessorPortValue();
            }

            if (_easyFlash != null && _easyFlash.TryRead(address, GetProcessorPortValue(), out byte cartridgeValue))
            {
                return cartridgeValue;
            }

            if (address >= 0xA000 && address <= 0xBFFF && IsBasicRomVisible())
            {
                return _basicRom[address - 0xA000];
            }

            if (address >= 0xD000 && address <= 0xDFFF)
            {
                if (IsIoVisible())
                {
                    return PeekIo(address);
                }

                if (IsCharacterRomVisibleToCpu())
                {
                    return _charRom[address - 0xD000];
                }
            }

            if (address >= 0xE000 && address <= 0xFFFF && IsKernalRomVisible())
            {
                return _kernalRom[address - 0xE000];
            }

            if (IsUltimaxUnmappedCpuRead(address))
            {
                return 0xFF;
            }

            return _ram[address];
        }

        /// <summary>
        /// Handles the cpu write operation.
        /// </summary>
        public void CpuWrite(ushort address, byte value)
        {
            _lastCpuBusValue = value;
            if (address == 0 || address == 1)
            {
                _ram[address] = value;
                UpdateProcessorPortInputLatch();
                return;
            }

            if (address >= 0xD000 && address <= 0xDFFF && IsIoVisible())
            {
                WriteIo(address, value);
                return;
            }

            if (_easyFlash != null)
            {
                // EasyFlash flash chips observe writes to visible ROML/ROMH for command
                // sequences, but normal C64 writes still update the RAM underneath unless
                // the current Ultimax map disconnects that internal RAM range.
                _easyFlash.TryWrite(address, GetProcessorPortValue(), value);
            }

            if (IsUltimaxUnmappedCpuWrite(address))
            {
                return;
            }

            _ram[address] = value;
            if (_vic != null)
            {
                _vic.NotifyCpuMemoryWrite(address, value);
            }

            if (address == 0xFF00 && _reu != null)
            {
                _reu.TriggerFf00(GetProcessorPortValue());
            }
        }

        /// <summary>
        /// Writes ram.
        /// </summary>
        public void WriteRam(ushort address, byte value)
        {
            _ram[address] = value;
        }

        /// <summary>
        /// Reads ram.
        /// </summary>
        public byte ReadRam(ushort address)
        {
            return _ram[address];
        }

        public byte ProcessorPortValue
        {
            get { return GetProcessorPortValue(); }
        }

        public bool IsIoAreaVisible
        {
            get { return IsIoVisible(); }
        }

        /// <summary>
        /// Handles the vic read operation.
        /// </summary>
        public byte VicRead(ushort vicAddress)
        {
            ushort bankBase = GetVicBankBase();
            ushort absoluteAddress = (ushort)(bankBase | (vicAddress & 0x3FFF));
            return VicReadAbsolute(absoluteAddress);
        }

        /// <summary>
        /// Handles the vic read absolute operation.
        /// </summary>
        public byte VicReadAbsolute(ushort absoluteAddress)
        {
            ushort bankBase = (ushort)(absoluteAddress & 0xC000);
            int bankOffset = absoluteAddress - bankBase;

            if ((bankBase == 0x0000 || bankBase == 0x8000) && bankOffset >= 0x1000 && bankOffset <= 0x1FFF)
            {
                byte charValue = _charRom[bankOffset - 0x1000];
                _lastVicBusValue = charValue;
                return charValue;
            }

            byte value = _ram[absoluteAddress];
            _lastVicBusValue = value;
            return value;
        }

        /// <summary>
        /// Reads color ram.
        /// </summary>
        public byte ReadColorRam(ushort offset)
        {
            return (byte)(_colorRam[offset & 0x03FF] & 0x0F);
        }

        /// <summary>
        /// Writes color ram.
        /// </summary>
        public void WriteColorRam(ushort offset, byte value)
        {
            _colorRam[offset & 0x03FF] = (byte)(value & 0x0F);
        }

        /// <summary>
        /// Gets the vic bank base value.
        /// </summary>
        public ushort GetVicBankBase()
        {
            int bankSelect = _cia2 != null ? _cia2.VicBankSelect : 0;
            return (ushort)((3 - bankSelect) * 0x4000);
        }

        /// <summary>
        /// Returns whether irq asserted is true.
        /// </summary>
        public bool IsIrqAsserted()
        {
            return _externalIrqAsserted ||
                (_reu != null && _reu.IsIrqAsserted) ||
                (_vic != null && _vic.IsIrqAsserted()) ||
                (_cia1 != null && _cia1.IsIrqAsserted());
        }

        /// <summary>
        /// Returns whether nmi asserted is true.
        /// </summary>
        public bool IsNmiAsserted()
        {
            return _externalNmiAsserted || (_cia2 != null && _cia2.IsNmiAsserted());
        }

        /// <summary>
        /// Sets the external irq value.
        /// </summary>
        public void SetExternalIrq(bool asserted)
        {
            _externalIrqAsserted = asserted;
        }

        /// <summary>
        /// Sets the external nmi value.
        /// </summary>
        public void SetExternalNmi(bool asserted)
        {
            _externalNmiAsserted = asserted;
        }

        public byte LastCpuBusValue
        {
            get { return _lastCpuBusValue; }
        }

        public byte LastVicBusValue
        {
            get { return _lastVicBusValue; }
        }

        /// <summary>
        /// Reads io.
        /// </summary>
        private byte ReadIo(ushort address)
        {
            if (address >= 0xD000 && address <= 0xD3FF && _vic != null)
            {
                return _vic.Read((ushort)(address & 0x003F));
            }

            if (address >= 0xD400 && address <= 0xD7FF && _sid != null)
            {
                return _sid.Read((ushort)(address & 0x001F));
            }

            if (address >= 0xD800 && address <= 0xDBFF)
            {
                return ReadColorRam((ushort)(address - 0xD800));
            }

            if (address >= 0xDC00 && address <= 0xDCFF && _cia1 != null)
            {
                return _cia1.Read((ushort)(address & 0x000F));
            }

            if (address >= 0xDD00 && address <= 0xDDFF && _cia2 != null)
            {
                return _cia2.Read((ushort)(address & 0x000F));
            }

            if (address >= 0xDF00 && address <= 0xDFFF && _reu != null && _reu.TryReadIo(address, out byte reuValue))
            {
                return reuValue;
            }

            if (address >= 0xDE00 && address <= 0xDFFF && _easyFlash != null && _easyFlash.TryReadIo(address, out byte cartridgeValue))
            {
                return cartridgeValue;
            }

            return _ioRam[address - 0xD000];
        }

        /// <summary>
        /// Peeks io without triggering read side effects.
        /// </summary>
        private byte PeekIo(ushort address)
        {
            if (address >= 0xD800 && address <= 0xDBFF)
            {
                return ReadColorRam((ushort)(address - 0xD800));
            }

            if (address >= 0xDF00 && address <= 0xDFFF && _reu != null && _reu.TryReadIo(address, out byte reuValue))
            {
                return reuValue;
            }

            if (address >= 0xDE00 && address <= 0xDFFF && _easyFlash != null && _easyFlash.TryReadIo(address, out byte cartridgeValue))
            {
                return cartridgeValue;
            }

            return _ioRam[address - 0xD000];
        }

        /// <summary>
        /// Writes io.
        /// </summary>
        private void WriteIo(ushort address, byte value)
        {
            if (address >= 0xD000 && address <= 0xD3FF && _vic != null)
            {
                _vic.Write((ushort)(address & 0x003F), value);
                return;
            }

            if (address >= 0xD400 && address <= 0xD7FF && _sid != null)
            {
                _sid.Write((ushort)(address & 0x001F), value);
                return;
            }

            if (address >= 0xD800 && address <= 0xDBFF)
            {
                WriteColorRam((ushort)(address - 0xD800), value);
                return;
            }

            if (address >= 0xDC00 && address <= 0xDCFF && _cia1 != null)
            {
                _cia1.Write((ushort)(address & 0x000F), value);
                return;
            }

            if (address >= 0xDD00 && address <= 0xDDFF && _cia2 != null)
            {
                _cia2.Write((ushort)(address & 0x000F), value);
                return;
            }

            if (address >= 0xDF00 && address <= 0xDFFF && _reu != null && _reu.TryWriteIo(address, value, GetProcessorPortValue()))
            {
                return;
            }

            if (address >= 0xDE00 && address <= 0xDFFF && _easyFlash != null && _easyFlash.TryWriteIo(address, value))
            {
                return;
            }

            _ioRam[address - 0xD000] = value;
        }

        /// <summary>
        /// Reads the C64-side memory space for a REC DMA cycle.
        /// </summary>
        private byte ReadC64ForReu(ushort address, byte processorPort)
        {
            if (address == 0)
            {
                return _ram[0];
            }

            if (address == 1)
            {
                return GetProcessorPortValue();
            }

            if (_easyFlash != null && _easyFlash.TryRead(address, processorPort, out byte cartridgeValue))
            {
                return cartridgeValue;
            }

            if (address >= 0xA000 && address <= 0xBFFF && IsBasicRomVisible(processorPort))
            {
                return _basicRom[address - 0xA000];
            }

            if (address >= 0xD000 && address <= 0xDFFF)
            {
                if (IsIoVisible(processorPort))
                {
                    return ReadIoForReu(address);
                }

                if (IsCharacterRomVisibleToCpu(processorPort))
                {
                    return _charRom[address - 0xD000];
                }
            }

            if (address >= 0xE000 && address <= 0xFFFF && IsKernalRomVisible(processorPort))
            {
                return _kernalRom[address - 0xE000];
            }

            if (IsUltimaxUnmappedCpuRead(address))
            {
                return 0xFF;
            }

            return _ram[address];
        }

        /// <summary>
        /// Writes the C64-side memory space for a REC DMA cycle.
        /// </summary>
        private void WriteC64ForReu(ushort address, byte value, byte processorPort)
        {
            if (address == 0 || address == 1)
            {
                _ram[address] = value;
                UpdateProcessorPortInputLatch();
                return;
            }

            if (address >= 0xD000 && address <= 0xDFFF && IsIoVisible(processorPort))
            {
                WriteIoForReu(address, value);
                return;
            }

            if (_easyFlash != null)
            {
                _easyFlash.TryWrite(address, processorPort, value);
            }

            if (IsUltimaxUnmappedCpuWrite(address))
            {
                return;
            }

            _ram[address] = value;
            if (_vic != null)
            {
                _vic.NotifyCpuMemoryWrite(address, value);
            }
        }

        /// <summary>
        /// Reads I/O for a REC DMA cycle while the REC itself is switched out.
        /// </summary>
        private byte ReadIoForReu(ushort address)
        {
            if (address >= 0xD000 && address <= 0xD3FF && _vic != null)
            {
                return _vic.Read((ushort)(address & 0x003F));
            }

            if (address >= 0xD400 && address <= 0xD7FF && _sid != null)
            {
                return _sid.Read((ushort)(address & 0x001F));
            }

            if (address >= 0xD800 && address <= 0xDBFF)
            {
                return ReadColorRam((ushort)(address - 0xD800));
            }

            if (address >= 0xDC00 && address <= 0xDCFF && _cia1 != null)
            {
                return _cia1.Read((ushort)(address & 0x000F));
            }

            if (address >= 0xDD00 && address <= 0xDDFF && _cia2 != null)
            {
                return _cia2.Read((ushort)(address & 0x000F));
            }

            if (address >= 0xDF00 && address <= 0xDFFF && _reu != null && _reu.Enabled)
            {
                return 0xFF;
            }

            if (address >= 0xDE00 && address <= 0xDFFF && _easyFlash != null && _easyFlash.TryReadIo(address, out byte cartridgeValue))
            {
                return cartridgeValue;
            }

            return _ioRam[address - 0xD000];
        }

        /// <summary>
        /// Writes I/O for a REC DMA cycle while the REC itself is switched out.
        /// </summary>
        private void WriteIoForReu(ushort address, byte value)
        {
            if (address >= 0xD000 && address <= 0xD3FF && _vic != null)
            {
                _vic.Write((ushort)(address & 0x003F), value);
                return;
            }

            if (address >= 0xD400 && address <= 0xD7FF && _sid != null)
            {
                _sid.Write((ushort)(address & 0x001F), value);
                return;
            }

            if (address >= 0xD800 && address <= 0xDBFF)
            {
                WriteColorRam((ushort)(address - 0xD800), value);
                return;
            }

            if (address >= 0xDC00 && address <= 0xDCFF && _cia1 != null)
            {
                _cia1.Write((ushort)(address & 0x000F), value);
                return;
            }

            if (address >= 0xDD00 && address <= 0xDDFF && _cia2 != null)
            {
                _cia2.Write((ushort)(address & 0x000F), value);
                return;
            }

            if (address >= 0xDF00 && address <= 0xDFFF && _reu != null && _reu.Enabled)
            {
                return;
            }

            if (address >= 0xDE00 && address <= 0xDFFF && _easyFlash != null && _easyFlash.TryWriteIo(address, value))
            {
                return;
            }

            _ioRam[address - 0xD000] = value;
        }

        /// <summary>
        /// Returns whether basic rom visible is true.
        /// </summary>
        private bool IsBasicRomVisible()
        {
            if (_easyFlash != null &&
                (_easyFlash.MemoryMode == EasyFlashMemoryMode.SixteenKilobyte ||
                _easyFlash.MemoryMode == EasyFlashMemoryMode.Ultimax))
            {
                return false;
            }

            byte port = GetProcessorPortValue();
            return IsBasicRomVisible(port);
        }

        /// <summary>
        /// Returns whether BASIC ROM is visible for a captured processor port value.
        /// </summary>
        private static bool IsBasicRomVisible(byte processorPort)
        {
            return (processorPort & 0x03) == 0x03;
        }

        /// <summary>
        /// Returns whether kernal rom visible is true.
        /// </summary>
        private bool IsKernalRomVisible()
        {
            if (_easyFlash != null && _easyFlash.MemoryMode == EasyFlashMemoryMode.Ultimax)
            {
                return false;
            }

            byte port = GetProcessorPortValue();
            return IsKernalRomVisible(port);
        }

        /// <summary>
        /// Returns whether KERNAL ROM is visible for a captured processor port value.
        /// </summary>
        private static bool IsKernalRomVisible(byte processorPort)
        {
            return (processorPort & 0x02) != 0;
        }

        /// <summary>
        /// Returns whether io visible is true.
        /// </summary>
        private bool IsIoVisible()
        {
            if (_easyFlash != null && _easyFlash.MemoryMode == EasyFlashMemoryMode.Ultimax)
            {
                return true;
            }

            byte port = GetProcessorPortValue();
            return IsIoVisible(port);
        }

        /// <summary>
        /// Returns whether I/O is visible for a captured processor port value.
        /// </summary>
        private static bool IsIoVisible(byte processorPort)
        {
            bool loramOrHiram = (processorPort & 0x03) != 0;
            bool charen = (processorPort & 0x04) != 0;
            return loramOrHiram && charen;
        }

        /// <summary>
        /// Returns whether character rom visible to cpu is true.
        /// </summary>
        private bool IsCharacterRomVisibleToCpu()
        {
            if (_easyFlash != null && _easyFlash.MemoryMode == EasyFlashMemoryMode.Ultimax)
            {
                return false;
            }

            byte port = GetProcessorPortValue();
            return IsCharacterRomVisibleToCpu(port);
        }

        /// <summary>
        /// Returns whether character ROM is visible for a captured processor port value.
        /// </summary>
        private static bool IsCharacterRomVisibleToCpu(byte processorPort)
        {
            bool loramOrHiram = (processorPort & 0x03) != 0;
            bool charen = (processorPort & 0x04) != 0;
            return loramOrHiram && !charen;
        }

        /// <summary>
        /// Returns whether Ultimax mode leaves a CPU read without internal memory.
        /// </summary>
        private bool IsUltimaxUnmappedCpuRead(ushort address)
        {
            if (_easyFlash == null || _easyFlash.MemoryMode != EasyFlashMemoryMode.Ultimax)
            {
                return false;
            }

            return (address >= 0x1000 && address <= 0x7FFF) ||
                (address >= 0xA000 && address <= 0xCFFF);
        }

        /// <summary>
        /// Returns whether Ultimax mode leaves a CPU write without internal RAM.
        /// </summary>
        private bool IsUltimaxUnmappedCpuWrite(ushort address)
        {
            if (_easyFlash == null || _easyFlash.MemoryMode != EasyFlashMemoryMode.Ultimax)
            {
                return false;
            }

            return (address >= 0x1000 && address <= 0x7FFF) ||
                (address >= 0xA000 && address <= 0xCFFF) ||
                (address >= 0xE000 && address <= 0xFFFF);
        }

        /// <summary>
        /// Gets the processor port value value.
        /// </summary>
        private byte GetProcessorPortValue()
        {
            byte dataDirection = _ram[0];
            byte portData = _ram[1];
            byte floatingInputs = (byte)(0x17 | (_processorPortInputLatch & 0xC0));
            return (byte)((portData & dataDirection) | (floatingInputs & ~dataDirection));
        }

        /// <summary>
        /// Remembers the last level driven by CPU-port output bits.
        /// </summary>
        private void UpdateProcessorPortInputLatch()
        {
            byte dataDirection = _ram[0];
            byte portData = _ram[1];
            _processorPortInputLatch = (byte)((_processorPortInputLatch & ~dataDirection) | (portData & dataDirection));
        }
    }
}
