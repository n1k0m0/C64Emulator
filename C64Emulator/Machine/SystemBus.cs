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
        private bool _externalIrqAsserted;
        private bool _externalNmiAsserted;
        private byte _lastCpuBusValue;
        private byte _lastVicBusValue;
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
            _baLow = false;
            _aecLow = false;
            _cpuPhi2CanAccess = true;
            _vicPhi2CanAccess = false;
            Owner = BusOwner.Cpu;
        }

        /// <summary>
        /// Loads roms.
        /// </summary>
        public void LoadRoms(string basePath)
        {
            var combinedRom = File.ReadAllBytes(Path.Combine(basePath, "c64-basic-kernal.bin"));
            Array.Copy(combinedRom, 0x0000, _basicRom, 0x0000, 0x2000);
            Array.Copy(combinedRom, 0x2000, _kernalRom, 0x0000, 0x2000);

            var charRom = File.ReadAllBytes(Path.Combine(basePath, "c64-character.bin"));
            Array.Copy(charRom, 0, _charRom, 0, Math.Min(_charRom.Length, charRom.Length));
        }

        /// <summary>
        /// Reads reset vector.
        /// </summary>
        public ushort ReadResetVector()
        {
            var lo = _kernalRom[0x1FFC];
            var hi = _kernalRom[0x1FFD];
            return (ushort)(lo | (hi << 8));
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

            value = _ram[address];
            _lastCpuBusValue = value;
            return value;
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
                return;
            }

            if (address >= 0xD000 && address <= 0xDFFF && IsIoVisible())
            {
                WriteIo(address, value);
                return;
            }

            _ram[address] = value;
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

            _ioRam[address - 0xD000] = value;
        }

        /// <summary>
        /// Returns whether basic rom visible is true.
        /// </summary>
        private bool IsBasicRomVisible()
        {
            byte port = GetProcessorPortValue();
            return (port & 0x03) == 0x03;
        }

        /// <summary>
        /// Returns whether kernal rom visible is true.
        /// </summary>
        private bool IsKernalRomVisible()
        {
            byte port = GetProcessorPortValue();
            return (port & 0x02) != 0;
        }

        /// <summary>
        /// Returns whether io visible is true.
        /// </summary>
        private bool IsIoVisible()
        {
            byte port = GetProcessorPortValue();
            bool loramOrHiram = (port & 0x03) != 0;
            bool charen = (port & 0x04) != 0;
            return loramOrHiram && charen;
        }

        /// <summary>
        /// Returns whether character rom visible to cpu is true.
        /// </summary>
        private bool IsCharacterRomVisibleToCpu()
        {
            byte port = GetProcessorPortValue();
            bool loramOrHiram = (port & 0x03) != 0;
            bool charen = (port & 0x04) != 0;
            return loramOrHiram && !charen;
        }

        /// <summary>
        /// Gets the processor port value value.
        /// </summary>
        private byte GetProcessorPortValue()
        {
            byte dataDirection = _ram[0];
            byte portData = _ram[1];
            return (byte)((portData & dataDirection) | (~dataDirection & 0x17));
        }
    }
}
