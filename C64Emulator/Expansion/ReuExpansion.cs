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

namespace C64Emulator.Core
{
    /// <summary>
    /// Lists supported RAM Expansion Unit capacities.
    /// </summary>
    public enum ReuMemorySize
    {
        K128 = 128 * 1024,
        K256 = 256 * 1024,
        K512 = 512 * 1024,
        M1 = 1024 * 1024,
        M2 = 2 * 1024 * 1024,
        M4 = 4 * 1024 * 1024,
        M8 = 8 * 1024 * 1024,
        M16 = 16 * 1024 * 1024
    }

    /// <summary>
    /// Emulates a Commodore 17xx-style REU and its MOS 8726 REC register set.
    /// </summary>
    public sealed class ReuExpansion
    {
        private const int RegisterMirrorMask = 0x001F;
        private const int RegisterCount = 0x0B;
        private const byte StatusInterruptPending = 0x80;
        private const byte StatusEndOfBlock = 0x40;
        private const byte StatusVerifyFault = 0x20;
        private const byte CommandExecute = 0x80;
        private const byte CommandAutoload = 0x20;
        private const byte CommandImmediate = 0x10;
        private const byte CommandTransferTypeMask = 0x03;
        private const byte InterruptEnable = 0x80;
        private const byte InterruptEndOfBlock = 0x40;
        private const byte InterruptVerifyFault = 0x20;
        private const byte AddressControlFixC64 = 0x80;
        private const byte AddressControlFixReu = 0x40;
        private const int FullLength = 0x10000;

        private byte[] _ram;
        private bool _enabled;
        private byte _status;
        private byte _command = CommandImmediate;
        private ushort _c64Address;
        private int _reuAddress;
        private ushort _transferLength = 0xFFFF;
        private byte _interruptMask = 0x1F;
        private byte _addressControl = 0x3F;
        private ushort _autoloadC64Address;
        private int _autoloadReuAddress;
        private ushort _autoloadTransferLength = 0xFFFF;
        private bool _waitingForFf00Trigger;
        private bool _dmaActive;
        private bool _swapWritePhase;
        private byte _swapC64Value;
        private byte _swapReuValue;
        private byte _activeCommand;
        private byte _activeProcessorPort;
        private ushort _activeC64Address;
        private int _activeReuAddress;
        private int _activeRemaining;

        /// <summary>
        /// Initializes a new REU with the common 512 KB capacity.
        /// </summary>
        public ReuExpansion()
            : this(ReuMemorySize.K512)
        {
        }

        /// <summary>
        /// Initializes a new REU with the requested capacity.
        /// </summary>
        public ReuExpansion(ReuMemorySize size)
        {
            _ram = new byte[(int)NormalizeSize(size)];
            Reset();
        }

        /// <summary>
        /// Gets the supported sizes in the order shown by the settings menu.
        /// </summary>
        public static ReuMemorySize[] GetSupportedSizes()
        {
            return new[]
            {
                ReuMemorySize.K128,
                ReuMemorySize.K256,
                ReuMemorySize.K512,
                ReuMemorySize.M1,
                ReuMemorySize.M2,
                ReuMemorySize.M4,
                ReuMemorySize.M8,
                ReuMemorySize.M16
            };
        }

        /// <summary>
        /// Creates a deep copy of the REU including RAM contents and register state.
        /// </summary>
        public ReuExpansion Clone()
        {
            var clone = new ReuExpansion((ReuMemorySize)SizeBytes)
            {
                _enabled = _enabled,
                _status = _status,
                _command = _command,
                _c64Address = _c64Address,
                _reuAddress = _reuAddress,
                _transferLength = _transferLength,
                _interruptMask = _interruptMask,
                _addressControl = _addressControl,
                _autoloadC64Address = _autoloadC64Address,
                _autoloadReuAddress = _autoloadReuAddress,
                _autoloadTransferLength = _autoloadTransferLength,
                _waitingForFf00Trigger = _waitingForFf00Trigger,
                _dmaActive = _dmaActive,
                _swapWritePhase = _swapWritePhase,
                _swapC64Value = _swapC64Value,
                _swapReuValue = _swapReuValue,
                _activeCommand = _activeCommand,
                _activeProcessorPort = _activeProcessorPort,
                _activeC64Address = _activeC64Address,
                _activeReuAddress = _activeReuAddress,
                _activeRemaining = _activeRemaining
            };

            Buffer.BlockCopy(_ram, 0, clone._ram, 0, _ram.Length);
            return clone;
        }

        /// <summary>
        /// Gets or sets whether the REU currently appears at I/O2.
        /// </summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (!_enabled)
                {
                    _dmaActive = false;
                    _waitingForFf00Trigger = false;
                    _swapWritePhase = false;
                }
            }
        }

        /// <summary>
        /// Gets the configured REU capacity in bytes.
        /// </summary>
        public int SizeBytes
        {
            get { return _ram != null ? _ram.Length : (int)ReuMemorySize.K512; }
        }

        /// <summary>
        /// Gets the configured REU capacity as an enum value.
        /// </summary>
        public ReuMemorySize Size
        {
            get { return (ReuMemorySize)SizeBytes; }
        }

        /// <summary>
        /// Gets whether a REC DMA operation currently owns CPU bus cycles.
        /// </summary>
        public bool IsDmaActive
        {
            get { return _enabled && _dmaActive; }
        }

        /// <summary>
        /// Gets whether the REC currently asserts the host IRQ line.
        /// </summary>
        public bool IsIrqAsserted
        {
            get { return _enabled && (_status & StatusInterruptPending) != 0; }
        }

        /// <summary>
        /// Gets the visible REC status byte without clearing read-sensitive bits.
        /// </summary>
        public byte Status
        {
            get { return ComposeStatusForRead(); }
        }

        /// <summary>
        /// Gets the CPU base address register.
        /// </summary>
        public ushort C64Address
        {
            get { return _c64Address; }
        }

        /// <summary>
        /// Gets the REU base address register.
        /// </summary>
        public int ReuAddress
        {
            get { return _reuAddress & 0xFFFFFF; }
        }

        /// <summary>
        /// Gets the transfer length register.
        /// </summary>
        public ushort TransferLength
        {
            get { return _transferLength; }
        }

        /// <summary>
        /// Resizes the REU RAM while preserving the overlapping low-address contents.
        /// </summary>
        public void ConfigureSize(ReuMemorySize size)
        {
            int newSize = (int)NormalizeSize(size);
            if (_ram != null && _ram.Length == newSize)
            {
                return;
            }

            byte[] oldRam = _ram;
            _ram = new byte[newSize];
            if (oldRam != null)
            {
                Buffer.BlockCopy(oldRam, 0, _ram, 0, Math.Min(oldRam.Length, _ram.Length));
            }

            _reuAddress &= 0xFFFFFF;
            _autoloadReuAddress &= 0xFFFFFF;
            _activeReuAddress &= 0xFFFFFF;
            UpdateInterruptPending();
        }

        /// <summary>
        /// Resets REC registers and pending DMA while preserving REU RAM contents.
        /// </summary>
        public void Reset()
        {
            _status = GetSizeStatusBit();
            _command = CommandImmediate;
            _c64Address = 0;
            _reuAddress = 0;
            _transferLength = 0xFFFF;
            _interruptMask = 0x1F;
            _addressControl = 0x3F;
            _autoloadC64Address = 0;
            _autoloadReuAddress = 0;
            _autoloadTransferLength = 0xFFFF;
            _waitingForFf00Trigger = false;
            _dmaActive = false;
            _swapWritePhase = false;
            _activeCommand = 0;
            _activeProcessorPort = 0x37;
            _activeC64Address = 0;
            _activeReuAddress = 0;
            _activeRemaining = 0;
        }

        /// <summary>
        /// Tries to read a REC register or mirrored unconnected register.
        /// </summary>
        public bool TryReadIo(ushort address, out byte value)
        {
            if (!_enabled || address < 0xDF00 || address > 0xDFFF)
            {
                value = 0xFF;
                return false;
            }

            int register = address & RegisterMirrorMask;
            value = ReadRegister(register);
            return true;
        }

        /// <summary>
        /// Tries to write a REC register or mirrored unconnected register.
        /// </summary>
        public bool TryWriteIo(ushort address, byte value, byte processorPort)
        {
            if (!_enabled || address < 0xDF00 || address > 0xDFFF)
            {
                return false;
            }

            int register = address & RegisterMirrorMask;
            WriteRegister(register, value, processorPort);
            return true;
        }

        /// <summary>
        /// Starts a delayed transfer after the CPU writes to $FF00.
        /// </summary>
        public void TriggerFf00(byte processorPort)
        {
            if (!_enabled || !_waitingForFf00Trigger)
            {
                return;
            }

            _waitingForFf00Trigger = false;
            _command = (byte)((_command | CommandImmediate) & 0x3F);
            StartDma(processorPort);
        }

        /// <summary>
        /// Executes one available REC DMA bus cycle.
        /// </summary>
        public void TickDmaCycle(Func<ushort, byte, byte> readC64, Action<ushort, byte, byte> writeC64)
        {
            if (!_enabled || !_dmaActive || readC64 == null || writeC64 == null)
            {
                return;
            }

            if ((_activeCommand & CommandTransferTypeMask) == 0x02)
            {
                TickSwapCycle(readC64, writeC64);
                return;
            }

            byte c64Value = readC64(_activeC64Address, _activeProcessorPort);
            byte reuValue = ReadReu(_activeReuAddress);
            switch (_activeCommand & CommandTransferTypeMask)
            {
                case 0x00:
                    WriteReu(_activeReuAddress, c64Value);
                    CompleteTransferredByte(false);
                    break;
                case 0x01:
                    writeC64(_activeC64Address, reuValue, _activeProcessorPort);
                    CompleteTransferredByte(false);
                    break;
                case 0x03:
                    CompleteTransferredByte(c64Value != reuValue);
                    break;
            }
        }

        /// <summary>
        /// Formats the current size for status text and menus.
        /// </summary>
        public static string FormatSize(ReuMemorySize size)
        {
            switch (NormalizeSize(size))
            {
                case ReuMemorySize.K128:
                    return "128 KB";
                case ReuMemorySize.K256:
                    return "256 KB";
                case ReuMemorySize.K512:
                    return "512 KB";
                case ReuMemorySize.M1:
                    return "1 MB";
                case ReuMemorySize.M2:
                    return "2 MB";
                case ReuMemorySize.M4:
                    return "4 MB";
                case ReuMemorySize.M8:
                    return "8 MB";
                case ReuMemorySize.M16:
                    return "16 MB";
                default:
                    return "512 KB";
            }
        }

        /// <summary>
        /// Executes the first half or second half of a swap byte.
        /// </summary>
        private void TickSwapCycle(Func<ushort, byte, byte> readC64, Action<ushort, byte, byte> writeC64)
        {
            if (!_swapWritePhase)
            {
                _swapC64Value = readC64(_activeC64Address, _activeProcessorPort);
                _swapReuValue = ReadReu(_activeReuAddress);
                _swapWritePhase = true;
                return;
            }

            writeC64(_activeC64Address, _swapReuValue, _activeProcessorPort);
            WriteReu(_activeReuAddress, _swapC64Value);
            _swapWritePhase = false;
            CompleteTransferredByte(false);
        }

        /// <summary>
        /// Reads a REC register by its mirrored five-bit register number.
        /// </summary>
        private byte ReadRegister(int register)
        {
            if (register >= RegisterCount)
            {
                return 0xFF;
            }

            switch (register)
            {
                case 0x00:
                    byte status = ComposeStatusForRead();
                    _status = (byte)(_status & ~(StatusInterruptPending | StatusEndOfBlock | StatusVerifyFault));
                    _status = (byte)((_status & 0xE0) | GetSizeStatusBit());
                    return status;
                case 0x01:
                    return (byte)(_command & 0x3F);
                case 0x02:
                    return (byte)(_c64Address & 0xFF);
                case 0x03:
                    return (byte)(_c64Address >> 8);
                case 0x04:
                    return (byte)(_reuAddress & 0xFF);
                case 0x05:
                    return (byte)((_reuAddress >> 8) & 0xFF);
                case 0x06:
                    return ComposeReuBankForRead();
                case 0x07:
                    return (byte)(_transferLength & 0xFF);
                case 0x08:
                    return (byte)(_transferLength >> 8);
                case 0x09:
                    return (byte)(_interruptMask | 0x1F);
                case 0x0A:
                    return (byte)(_addressControl | 0x3F);
                default:
                    return 0xFF;
            }
        }

        /// <summary>
        /// Writes a REC register by its mirrored five-bit register number.
        /// </summary>
        private void WriteRegister(int register, byte value, byte processorPort)
        {
            if (register >= RegisterCount)
            {
                return;
            }

            switch (register)
            {
                case 0x01:
                    _command = (byte)(value & 0x3F);
                    if ((value & CommandExecute) != 0)
                    {
                        if ((_command & CommandImmediate) != 0)
                        {
                            StartDma(processorPort);
                        }
                        else
                        {
                            _waitingForFf00Trigger = true;
                        }
                    }

                    break;
                case 0x02:
                    _c64Address = (ushort)((_c64Address & 0xFF00) | value);
                    _autoloadC64Address = _c64Address;
                    break;
                case 0x03:
                    _c64Address = (ushort)((_c64Address & 0x00FF) | (value << 8));
                    _autoloadC64Address = _c64Address;
                    break;
                case 0x04:
                    _reuAddress = (_reuAddress & 0xFFFF00) | value;
                    _autoloadReuAddress = _reuAddress;
                    break;
                case 0x05:
                    _reuAddress = (_reuAddress & 0xFF00FF) | (value << 8);
                    _autoloadReuAddress = _reuAddress;
                    break;
                case 0x06:
                    _reuAddress = (_reuAddress & 0x00FFFF) | (value << 16);
                    _reuAddress &= 0xFFFFFF;
                    _autoloadReuAddress = _reuAddress;
                    break;
                case 0x07:
                    _transferLength = (ushort)((_transferLength & 0xFF00) | value);
                    _autoloadTransferLength = _transferLength;
                    break;
                case 0x08:
                    _transferLength = (ushort)((_transferLength & 0x00FF) | (value << 8));
                    _autoloadTransferLength = _transferLength;
                    break;
                case 0x09:
                    _interruptMask = (byte)(0x1F | (value & 0xE0));
                    UpdateInterruptPending();
                    break;
                case 0x0A:
                    _addressControl = (byte)(0x3F | (value & 0xC0));
                    break;
            }
        }

        /// <summary>
        /// Starts a DMA transfer from the currently visible or autoloaded register set.
        /// </summary>
        private void StartDma(byte processorPort)
        {
            if ((_command & CommandAutoload) != 0)
            {
                _activeC64Address = _autoloadC64Address;
                _activeReuAddress = _autoloadReuAddress & 0xFFFFFF;
                _activeRemaining = _autoloadTransferLength == 0 ? FullLength : _autoloadTransferLength;
            }
            else
            {
                _activeC64Address = _c64Address;
                _activeReuAddress = _reuAddress & 0xFFFFFF;
                _activeRemaining = _transferLength == 0 ? FullLength : _transferLength;
            }

            _activeCommand = _command;
            _activeProcessorPort = processorPort;
            _swapWritePhase = false;
            _status = GetSizeStatusBit();
            _dmaActive = _activeRemaining > 0;
            UpdateInterruptPending();
        }

        /// <summary>
        /// Finishes one transferred byte and advances/finalizes visible registers.
        /// </summary>
        private void CompleteTransferredByte(bool verifyFault)
        {
            bool fixC64 = (_addressControl & AddressControlFixC64) != 0;
            bool fixReu = (_addressControl & AddressControlFixReu) != 0;
            if (!fixC64)
            {
                _activeC64Address++;
            }

            if (!fixReu)
            {
                _activeReuAddress = (_activeReuAddress + 1) & 0xFFFFFF;
            }

            _activeRemaining--;

            if (verifyFault)
            {
                FinishDma(true);
                return;
            }

            if (_activeRemaining <= 0)
            {
                FinishDma(false);
            }
        }

        /// <summary>
        /// Commits the end of a DMA command into the visible REC registers.
        /// </summary>
        private void FinishDma(bool verifyFault)
        {
            _dmaActive = false;
            _swapWritePhase = false;
            _command = (byte)(_command & 0x3F);

            if ((_activeCommand & CommandAutoload) != 0)
            {
                _c64Address = _autoloadC64Address;
                _reuAddress = _autoloadReuAddress & 0xFFFFFF;
                _transferLength = _autoloadTransferLength;
            }
            else
            {
                _c64Address = _activeC64Address;
                _reuAddress = _activeReuAddress & 0xFFFFFF;
                _transferLength = verifyFault
                    ? (ushort)Math.Max(1, Math.Min(0xFFFF, _activeRemaining))
                    : (ushort)0x0001;
            }

            _status = (byte)(GetSizeStatusBit() | (verifyFault ? StatusVerifyFault : StatusEndOfBlock));
            UpdateInterruptPending();
        }

        /// <summary>
        /// Reads a byte from wrapped REU RAM.
        /// </summary>
        private byte ReadReu(int address)
        {
            return _ram[WrapReuAddress(address)];
        }

        /// <summary>
        /// Writes a byte to wrapped REU RAM.
        /// </summary>
        private void WriteReu(int address, byte value)
        {
            _ram[WrapReuAddress(address)] = value;
        }

        /// <summary>
        /// Maps a 24-bit REC address into the installed RAM capacity.
        /// </summary>
        private int WrapReuAddress(int address)
        {
            int size = SizeBytes;
            return size <= 0 ? 0 : (address & 0xFFFFFF) % size;
        }

        /// <summary>
        /// Returns a read value for the status register including the size jumper bit.
        /// </summary>
        private byte ComposeStatusForRead()
        {
            return (byte)((_status & 0xE0) | GetSizeStatusBit());
        }

        /// <summary>
        /// Returns the historical REC size status bit for original 256/512 KB style RAMs.
        /// </summary>
        private byte GetSizeStatusBit()
        {
            return SizeBytes >= (int)ReuMemorySize.K256 ? (byte)0x10 : (byte)0x00;
        }

        /// <summary>
        /// Returns the bank register value with unused upper address bits reading high.
        /// </summary>
        private byte ComposeReuBankForRead()
        {
            int addressBits = GetAddressBits(SizeBytes);
            int validBankBits = Math.Max(0, addressBits - 16);
            if (validBankBits >= 8)
            {
                return (byte)((_reuAddress >> 16) & 0xFF);
            }

            int mask = validBankBits == 0 ? 0 : (1 << validBankBits) - 1;
            int bank = (_reuAddress >> 16) & mask;
            return (byte)(bank | (~mask & 0xFF));
        }

        /// <summary>
        /// Updates the interrupt-pending bit according to status and mask registers.
        /// </summary>
        private void UpdateInterruptPending()
        {
            bool endInterrupt = (_status & StatusEndOfBlock) != 0 &&
                (_interruptMask & (InterruptEnable | InterruptEndOfBlock)) == (InterruptEnable | InterruptEndOfBlock);
            bool faultInterrupt = (_status & StatusVerifyFault) != 0 &&
                (_interruptMask & (InterruptEnable | InterruptVerifyFault)) == (InterruptEnable | InterruptVerifyFault);

            if (endInterrupt || faultInterrupt)
            {
                _status = (byte)(_status | StatusInterruptPending);
            }
            else
            {
                _status = (byte)(_status & ~StatusInterruptPending);
            }
        }

        /// <summary>
        /// Normalizes an arbitrary enum value to one of the supported capacities.
        /// </summary>
        private static ReuMemorySize NormalizeSize(ReuMemorySize size)
        {
            switch (size)
            {
                case ReuMemorySize.K128:
                case ReuMemorySize.K256:
                case ReuMemorySize.K512:
                case ReuMemorySize.M1:
                case ReuMemorySize.M2:
                case ReuMemorySize.M4:
                case ReuMemorySize.M8:
                case ReuMemorySize.M16:
                    return size;
                default:
                    return ReuMemorySize.K512;
            }
        }

        /// <summary>
        /// Calculates how many address bits are needed for a power-of-two capacity.
        /// </summary>
        private static int GetAddressBits(int size)
        {
            int bits = 0;
            int value = Math.Max(1, size - 1);
            while (value > 0)
            {
                bits++;
                value >>= 1;
            }

            return bits;
        }
    }
}
