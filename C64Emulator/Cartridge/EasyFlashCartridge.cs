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
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Lists the CPU-visible memory configurations an EasyFlash can drive.
    /// </summary>
    public enum EasyFlashMemoryMode
    {
        Off,
        EightKilobyte,
        SixteenKilobyte,
        Ultimax
    }

    /// <summary>
    /// Emulates the EasyFlash cartridge memory, bank registers, I/O RAM, and flash writes.
    /// </summary>
    public sealed class EasyFlashCartridge
    {
        private const int CartridgeTypeEasyFlash = 32;
        private const int HeaderLength = 0x40;
        private const int ChipHeaderLength = 0x10;
        private const int BankCount = 64;
        private const int BankSize = 0x2000;
        private const int ChipSize = BankCount * BankSize;
        private const int IoRamSize = 0x100;
        private const int FlashSectorSize = 0x10000;
        private const byte ManufacturerId = 0x01;
        private const byte DeviceIdAm29F040 = 0xA4;
        private static readonly Encoding Ascii = Encoding.ASCII;

        private readonly byte[] _roml = CreateErasedBuffer(ChipSize);
        private readonly byte[] _romh = CreateErasedBuffer(ChipSize);
        private readonly byte[] _ioRam = new byte[IoRamSize];

        private bool _enabled = true;
        private byte _bank;
        private byte _control = 0x05;
        private byte _headerExromLine = 0x01;
        private byte _headerGameLine;
        private string _displayName = "EASYFLASH";
        private string _sourcePath = string.Empty;
        private bool _dirty;
        private FlashCommandState _romlCommandState;
        private FlashCommandState _romhCommandState;

        /// <summary>
        /// Initializes a new blank EasyFlash cartridge.
        /// </summary>
        public EasyFlashCartridge()
        {
            Fill(_ioRam, 0x00);
        }

        /// <summary>
        /// Gets or sets whether the inserted cartridge currently drives the expansion port.
        /// </summary>
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>
        /// Gets whether flash contents changed since the last load/save operation.
        /// </summary>
        public bool IsDirty
        {
            get { return _dirty; }
        }

        /// <summary>
        /// Gets the user-facing cartridge name.
        /// </summary>
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(_displayName) ? "EASYFLASH" : _displayName; }
        }

        /// <summary>
        /// Gets the file path this cartridge was loaded from or saved to.
        /// </summary>
        public string SourcePath
        {
            get { return _sourcePath ?? string.Empty; }
        }

        /// <summary>
        /// Gets the selected EasyFlash bank.
        /// </summary>
        public int Bank
        {
            get { return _bank & 0x3F; }
        }

        /// <summary>
        /// Gets the raw EasyFlash control register value last written to $DE02.
        /// </summary>
        public byte Control
        {
            get { return _control; }
        }

        /// <summary>
        /// Gets the currently selected memory mode.
        /// </summary>
        public EasyFlashMemoryMode MemoryMode
        {
            get { return GetMemoryMode(); }
        }

        /// <summary>
        /// Creates an empty editable EasyFlash image.
        /// </summary>
        public static EasyFlashCartridge CreateBlank(string displayName)
        {
            var cartridge = new EasyFlashCartridge();
            cartridge._displayName = string.IsNullOrWhiteSpace(displayName) ? "EASYFLASH" : displayName.Trim();
            cartridge.Reset();
            return cartridge;
        }

        /// <summary>
        /// Creates a deep copy for emulator power-cycle recreation.
        /// </summary>
        public EasyFlashCartridge Clone()
        {
            var clone = new EasyFlashCartridge
            {
                _enabled = _enabled,
                _bank = _bank,
                _control = _control,
                _headerExromLine = _headerExromLine,
                _headerGameLine = _headerGameLine,
                _displayName = _displayName,
                _sourcePath = _sourcePath,
                _dirty = _dirty,
                _romlCommandState = FlashCommandState.Read,
                _romhCommandState = FlashCommandState.Read
            };

            Buffer.BlockCopy(_roml, 0, clone._roml, 0, _roml.Length);
            Buffer.BlockCopy(_romh, 0, clone._romh, 0, _romh.Length);
            Buffer.BlockCopy(_ioRam, 0, clone._ioRam, 0, _ioRam.Length);
            return clone;
        }

        /// <summary>
        /// Loads an EasyFlash CRT image from disk.
        /// </summary>
        public static EasyFlashCartridge LoadCrt(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("EasyFlash CRT file not found.", path);
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length < HeaderLength || !HasSignature(data, 0, "C64 CARTRIDGE   "))
            {
                throw new InvalidDataException("Invalid CRT header.");
            }

            int headerLength = ReadInt32BigEndian(data, 0x10);
            int cartridgeType = ReadUInt16BigEndian(data, 0x16);
            if (headerLength < HeaderLength || headerLength > data.Length)
            {
                throw new InvalidDataException("Invalid CRT header length.");
            }

            if (cartridgeType != CartridgeTypeEasyFlash)
            {
                throw new InvalidDataException("CRT is not an EasyFlash cartridge.");
            }

            var cartridge = new EasyFlashCartridge
            {
                _headerExromLine = data[0x18],
                _headerGameLine = data[0x19],
                _displayName = DecodeName(data, 0x20, 0x20),
                _sourcePath = Path.GetFullPath(path)
            };

            Fill(cartridge._roml, 0xFF);
            Fill(cartridge._romh, 0xFF);

            int offset = headerLength;
            while (offset + ChipHeaderLength <= data.Length)
            {
                if (!HasSignature(data, offset, "CHIP"))
                {
                    throw new InvalidDataException("Invalid CRT CHIP packet.");
                }

                int packetLength = ReadInt32BigEndian(data, offset + 4);
                int chipType = ReadUInt16BigEndian(data, offset + 8);
                int bank = ReadUInt16BigEndian(data, offset + 10);
                int loadAddress = ReadUInt16BigEndian(data, offset + 12);
                int imageSize = ReadUInt16BigEndian(data, offset + 14);

                if (packetLength < ChipHeaderLength ||
                    offset + packetLength > data.Length ||
                    imageSize < 0 ||
                    imageSize > packetLength - ChipHeaderLength)
                {
                    throw new InvalidDataException("Invalid CRT CHIP length.");
                }

                if ((chipType == 0 || chipType == 2) && bank >= 0 && bank < BankCount)
                {
                    CopyChipData(cartridge, bank, loadAddress, data, offset + ChipHeaderLength, imageSize);
                }

                offset += packetLength;
            }

            cartridge.Reset();
            cartridge._dirty = false;
            return cartridge;
        }

        /// <summary>
        /// Resets volatile cartridge registers while preserving flash and I/O RAM contents.
        /// </summary>
        public void Reset()
        {
            _bank = 0;
            _control = 0x05;
            _romlCommandState = FlashCommandState.Read;
            _romhCommandState = FlashCommandState.Read;
        }

        /// <summary>
        /// Tries to read a CPU-visible EasyFlash ROM byte.
        /// </summary>
        public bool TryRead(ushort address, byte processorPort, out byte value)
        {
            byte[] chip;
            int chipAddress;
            if (TryResolveMappedChip(address, processorPort, out chip, out chipAddress))
            {
                value = ReadFlashByte(chip, ReferenceEquals(chip, _roml) ? _romlCommandState : _romhCommandState, chipAddress);
                return true;
            }

            value = 0xFF;
            return false;
        }

        /// <summary>
        /// Tries to consume a CPU write to a mapped EasyFlash ROM area.
        /// </summary>
        public bool TryWrite(ushort address, byte processorPort, byte value)
        {
            byte[] chip;
            int chipAddress;
            if (!TryResolveMappedChip(address, processorPort, out chip, out chipAddress))
            {
                return false;
            }

            if (ReferenceEquals(chip, _roml))
            {
                WriteFlashCommand(_roml, ref _romlCommandState, chipAddress, value);
            }
            else
            {
                WriteFlashCommand(_romh, ref _romhCommandState, chipAddress, value);
            }

            return true;
        }

        /// <summary>
        /// Tries to read from EasyFlash I/O space.
        /// </summary>
        public bool TryReadIo(ushort address, out byte value)
        {
            if (!_enabled)
            {
                value = 0xFF;
                return false;
            }

            if (address >= 0xDF00 && address <= 0xDFFF)
            {
                value = _ioRam[address & 0x00FF];
                return true;
            }

            value = 0xFF;
            return false;
        }

        /// <summary>
        /// Tries to write to EasyFlash I/O space.
        /// </summary>
        public bool TryWriteIo(ushort address, byte value)
        {
            if (!_enabled)
            {
                return false;
            }

            if (address == 0xDE00)
            {
                _bank = (byte)(value & 0x3F);
                return true;
            }

            if (address == 0xDE02)
            {
                _control = value;
                return true;
            }

            if (address >= 0xDF00 && address <= 0xDFFF)
            {
                _ioRam[address & 0x00FF] = value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the EasyFlash reset vector when the cartridge should boot first.
        /// </summary>
        public bool TryReadResetVector(out ushort resetVector)
        {
            if (!_enabled)
            {
                resetVector = 0;
                return false;
            }

            int offset = 0x1FFC;
            resetVector = (ushort)(_romh[offset] | (_romh[offset + 1] << 8));
            return resetVector != 0x0000 && resetVector != 0xFFFF;
        }

        /// <summary>
        /// Saves the current flash contents as an EasyFlash CRT image.
        /// </summary>
        public void SaveCrt(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A target path is required.", nameof(path));
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer);
                for (int bank = 0; bank < BankCount; bank++)
                {
                    WriteChipPacket(writer, _roml, bank, 0x8000);
                    WriteChipPacket(writer, _romh, bank, 0xA000);
                }
            }

            _sourcePath = Path.GetFullPath(path);
            _dirty = false;
        }

        /// <summary>
        /// Saves the cartridge back to the source CRT file.
        /// </summary>
        public void SaveToSource()
        {
            if (string.IsNullOrWhiteSpace(_sourcePath))
            {
                throw new InvalidOperationException("EasyFlash has no source path.");
            }

            SaveCrt(_sourcePath);
        }

        /// <summary>
        /// Resolves the active EasyFlash memory configuration from $DE02.
        /// </summary>
        private EasyFlashMemoryMode GetMemoryMode()
        {
            if (!_enabled)
            {
                return EasyFlashMemoryMode.Off;
            }

            int mxg = _control & 0x07;
            switch (mxg)
            {
                case 0x04:
                    return EasyFlashMemoryMode.Off;
                case 0x05:
                    return EasyFlashMemoryMode.Ultimax;
                case 0x06:
                    return EasyFlashMemoryMode.EightKilobyte;
                case 0x07:
                    return EasyFlashMemoryMode.SixteenKilobyte;
                default:
                    // M=0 means GAME comes from the boot jumper. The emulator models the
                    // usual Boot position, which is enough for EasyFlash startup code.
                    return (_control & 0x02) != 0
                        ? EasyFlashMemoryMode.SixteenKilobyte
                        : EasyFlashMemoryMode.Ultimax;
            }
        }

        /// <summary>
        /// Resolves a CPU address to ROML/ROMH and a physical flash address.
        /// </summary>
        private bool TryResolveMappedChip(ushort address, byte processorPort, out byte[] chip, out int chipAddress)
        {
            EasyFlashMemoryMode mode = GetMemoryMode();
            int bankBase = Bank * BankSize;

            if (mode != EasyFlashMemoryMode.Off &&
                address >= 0x8000 &&
                address <= 0x9FFF &&
                (mode == EasyFlashMemoryMode.Ultimax || IsRomlVisible(processorPort)))
            {
                chip = _roml;
                chipAddress = bankBase + (address - 0x8000);
                return true;
            }

            if (mode == EasyFlashMemoryMode.SixteenKilobyte &&
                address >= 0xA000 &&
                address <= 0xBFFF &&
                IsRomhAtA000Visible(processorPort))
            {
                chip = _romh;
                chipAddress = bankBase + (address - 0xA000);
                return true;
            }

            if (mode == EasyFlashMemoryMode.Ultimax &&
                address >= 0xE000 &&
                address <= 0xFFFF)
            {
                chip = _romh;
                chipAddress = bankBase + (address - 0xE000);
                return true;
            }

            chip = null;
            chipAddress = 0;
            return false;
        }

        /// <summary>
        /// Returns whether the low cartridge window should be visible for this CPU port.
        /// </summary>
        private static bool IsRomlVisible(byte processorPort)
        {
            return (processorPort & 0x03) == 0x03;
        }

        /// <summary>
        /// Returns whether the high cartridge window should replace BASIC space.
        /// </summary>
        private static bool IsRomhAtA000Visible(byte processorPort)
        {
            return (processorPort & 0x02) != 0;
        }

        /// <summary>
        /// Reads a flash byte, including the simple autoselect manufacturer/device mode.
        /// </summary>
        private static byte ReadFlashByte(byte[] chip, FlashCommandState commandState, int address)
        {
            if (commandState == FlashCommandState.Autoselect)
            {
                switch (address & 0x00FF)
                {
                    case 0:
                        return ManufacturerId;
                    case 1:
                        return DeviceIdAm29F040;
                    case 2:
                        return 0x00;
                }
            }

            return chip[address & (ChipSize - 1)];
        }

        /// <summary>
        /// Applies the small subset of AM29F040 command sequences used by EasyAPI/EasyProg.
        /// </summary>
        private void WriteFlashCommand(byte[] chip, ref FlashCommandState commandState, int address, byte value)
        {
            address &= ChipSize - 1;
            switch (commandState)
            {
                case FlashCommandState.Read:
                    if (IsFirstUnlockAddress(address) && value == 0xAA)
                    {
                        commandState = FlashCommandState.Unlock1;
                    }
                    else if (value == 0xF0)
                    {
                        commandState = FlashCommandState.Read;
                    }
                    break;
                case FlashCommandState.Unlock1:
                    commandState = IsSecondUnlockAddress(address) && value == 0x55
                        ? FlashCommandState.Unlock2
                        : FlashCommandState.Read;
                    break;
                case FlashCommandState.Unlock2:
                    if (IsFirstUnlockAddress(address) && value == 0xA0)
                    {
                        commandState = FlashCommandState.Program;
                    }
                    else if (IsFirstUnlockAddress(address) && value == 0x80)
                    {
                        commandState = FlashCommandState.EraseUnlock1;
                    }
                    else if (IsFirstUnlockAddress(address) && value == 0x90)
                    {
                        commandState = FlashCommandState.Autoselect;
                    }
                    else
                    {
                        commandState = FlashCommandState.Read;
                    }
                    break;
                case FlashCommandState.Program:
                    chip[address] = (byte)(chip[address] & value);
                    _dirty = true;
                    commandState = FlashCommandState.Read;
                    break;
                case FlashCommandState.EraseUnlock1:
                    commandState = IsFirstUnlockAddress(address) && value == 0xAA
                        ? FlashCommandState.EraseUnlock2
                        : FlashCommandState.Read;
                    break;
                case FlashCommandState.EraseUnlock2:
                    commandState = IsSecondUnlockAddress(address) && value == 0x55
                        ? FlashCommandState.EraseCommand
                        : FlashCommandState.Read;
                    break;
                case FlashCommandState.EraseCommand:
                    if (IsFirstUnlockAddress(address) && value == 0x10)
                    {
                        Fill(chip, 0xFF);
                        _dirty = true;
                    }
                    else if (value == 0x30)
                    {
                        EraseSector(chip, address);
                        _dirty = true;
                    }

                    commandState = FlashCommandState.Read;
                    break;
                case FlashCommandState.Autoselect:
                    if (value == 0xF0)
                    {
                        commandState = FlashCommandState.Read;
                    }
                    break;
            }
        }

        /// <summary>
        /// Returns whether a write targets the first flash unlock address.
        /// </summary>
        private static bool IsFirstUnlockAddress(int address)
        {
            int low16 = address & 0xFFFF;
            return low16 == 0x5555 || low16 == 0x0555;
        }

        /// <summary>
        /// Returns whether a write targets the second flash unlock address.
        /// </summary>
        private static bool IsSecondUnlockAddress(int address)
        {
            int low16 = address & 0xFFFF;
            return low16 == 0x2AAA || low16 == 0x0AAA || low16 == 0x02AA;
        }

        /// <summary>
        /// Erases one 64 KiB flash sector.
        /// </summary>
        private static void EraseSector(byte[] chip, int address)
        {
            int start = address & ~(FlashSectorSize - 1);
            int end = Math.Min(chip.Length, start + FlashSectorSize);
            for (int index = start; index < end; index++)
            {
                chip[index] = 0xFF;
            }
        }

        /// <summary>
        /// Copies a CHIP packet into ROML/ROMH storage.
        /// </summary>
        private static void CopyChipData(EasyFlashCartridge cartridge, int bank, int loadAddress, byte[] data, int dataOffset, int imageSize)
        {
            int remaining = imageSize;
            int sourceOffset = dataOffset;
            int currentLoadAddress = loadAddress;
            while (remaining > 0)
            {
                int chunk = Math.Min(BankSize, remaining);
                byte[] target = ResolveChipTarget(cartridge, currentLoadAddress);
                if (target != null)
                {
                    int bankOffset = bank * BankSize;
                    int chipOffset = bankOffset + ((currentLoadAddress & 0x1FFF) % BankSize);
                    int copyLength = Math.Min(chunk, target.Length - chipOffset);
                    if (copyLength > 0)
                    {
                        Buffer.BlockCopy(data, sourceOffset, target, chipOffset, copyLength);
                    }
                }

                remaining -= chunk;
                sourceOffset += chunk;
                currentLoadAddress += chunk;
            }
        }

        /// <summary>
        /// Resolves a CHIP packet load address to ROML or ROMH.
        /// </summary>
        private static byte[] ResolveChipTarget(EasyFlashCartridge cartridge, int loadAddress)
        {
            if (loadAddress >= 0x8000 && loadAddress <= 0x9FFF)
            {
                return cartridge._roml;
            }

            if ((loadAddress >= 0xA000 && loadAddress <= 0xBFFF) ||
                (loadAddress >= 0xE000 && loadAddress <= 0xFFFF))
            {
                return cartridge._romh;
            }

            return null;
        }

        /// <summary>
        /// Writes the CRT file header.
        /// </summary>
        private void WriteHeader(BinaryWriter writer)
        {
            byte[] header = new byte[HeaderLength];
            Fill(header, 0x00);
            WriteAscii(header, 0, "C64 CARTRIDGE   ", 16);
            WriteInt32BigEndian(header, 0x10, HeaderLength);
            WriteUInt16BigEndian(header, 0x14, 0x0100);
            WriteUInt16BigEndian(header, 0x16, CartridgeTypeEasyFlash);
            header[0x18] = _headerExromLine;
            header[0x19] = _headerGameLine;
            WriteAscii(header, 0x20, DisplayName, 0x20);
            writer.Write(header);
        }

        /// <summary>
        /// Writes one 8 KiB CRT CHIP packet.
        /// </summary>
        private static void WriteChipPacket(BinaryWriter writer, byte[] chip, int bank, int loadAddress)
        {
            byte[] header = new byte[ChipHeaderLength];
            WriteAscii(header, 0, "CHIP", 4);
            WriteInt32BigEndian(header, 4, ChipHeaderLength + BankSize);
            WriteUInt16BigEndian(header, 8, 2);
            WriteUInt16BigEndian(header, 10, bank);
            WriteUInt16BigEndian(header, 12, loadAddress);
            WriteUInt16BigEndian(header, 14, BankSize);
            writer.Write(header);
            writer.Write(chip, bank * BankSize, BankSize);
        }

        /// <summary>
        /// Creates an erased flash buffer.
        /// </summary>
        private static byte[] CreateErasedBuffer(int length)
        {
            var buffer = new byte[length];
            Fill(buffer, 0xFF);
            return buffer;
        }

        /// <summary>
        /// Fills a byte array with one value.
        /// </summary>
        private static void Fill(byte[] buffer, byte value)
        {
            if (buffer == null)
            {
                return;
            }

            for (int index = 0; index < buffer.Length; index++)
            {
                buffer[index] = value;
            }
        }

        /// <summary>
        /// Decodes the fixed-size CRT name field.
        /// </summary>
        private static string DecodeName(byte[] data, int offset, int length)
        {
            string name = Ascii.GetString(data, offset, length).TrimEnd('\0', ' ');
            return string.IsNullOrWhiteSpace(name) ? "EASYFLASH" : name;
        }

        /// <summary>
        /// Writes an ASCII field padded with zero bytes.
        /// </summary>
        private static void WriteAscii(byte[] target, int offset, string value, int length)
        {
            byte[] bytes = Ascii.GetBytes(value ?? string.Empty);
            int copyLength = Math.Min(length, bytes.Length);
            Buffer.BlockCopy(bytes, 0, target, offset, copyLength);
        }

        /// <summary>
        /// Checks an ASCII signature inside a byte array.
        /// </summary>
        private static bool HasSignature(byte[] data, int offset, string signature)
        {
            byte[] bytes = Ascii.GetBytes(signature);
            if (offset < 0 || offset + bytes.Length > data.Length)
            {
                return false;
            }

            for (int index = 0; index < bytes.Length; index++)
            {
                if (data[offset + index] != bytes[index])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads a big-endian 16-bit integer.
        /// </summary>
        private static int ReadUInt16BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Reads a big-endian 32-bit integer.
        /// </summary>
        private static int ReadInt32BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3];
        }

        /// <summary>
        /// Writes a big-endian 16-bit integer.
        /// </summary>
        private static void WriteUInt16BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 8) & 0xFF);
            data[offset + 1] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Writes a big-endian 32-bit integer.
        /// </summary>
        private static void WriteInt32BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 24) & 0xFF);
            data[offset + 1] = (byte)((value >> 16) & 0xFF);
            data[offset + 2] = (byte)((value >> 8) & 0xFF);
            data[offset + 3] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Lists the supported flash command parser states.
        /// </summary>
        private enum FlashCommandState
        {
            Read,
            Unlock1,
            Unlock2,
            Program,
            EraseUnlock1,
            EraseUnlock2,
            EraseCommand,
            Autoselect
        }
    }
}
