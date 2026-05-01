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
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Represents a D64 disk image and exposes Commodore DOS sector and file access helpers.
    /// </summary>
    public sealed class D64Image
    {
        private const int SectorSize = 256;
        private static readonly byte[] GcrEncodeTable =
        {
            0x0A, 0x0B, 0x12, 0x13,
            0x0E, 0x0F, 0x16, 0x17,
            0x09, 0x19, 0x1A, 0x1B,
            0x0D, 0x1D, 0x1E, 0x15
        };
        private static readonly int[] TrackStartSectorOffsets = BuildTrackStartSectorOffsets();
        private readonly byte[] _data;
        private readonly byte[] _errorInfo;
        private readonly List<D64DirectoryEntry> _entries;
        private readonly Dictionary<int, byte[]> _trackStreamCache = new Dictionary<int, byte[]>();

        /// <summary>
        /// Initializes a new D64Image instance.
        /// </summary>
        private D64Image(string hostPath, byte[] data, byte[] errorInfo, int trackCount)
        {
            HostPath = hostPath;
            _data = data;
            _errorInfo = errorInfo;
            TrackCount = trackCount;
            IsReadOnly = !string.IsNullOrWhiteSpace(hostPath) &&
                File.Exists(hostPath) &&
                /// <summary>
                /// Handles the file info operation.
                /// </summary>
                new FileInfo(hostPath).IsReadOnly;
            _entries = ReadDirectoryEntries();
            DiskName = ReadDiskName();
            DiskId = ReadDiskId();
        }

        /// <summary>
        /// Gets the host filesystem path.
        /// </summary>
        public string HostPath { get; }

        /// <summary>
        /// Gets the number of tracks in the disk image.
        /// </summary>
        public int TrackCount { get; }

        /// <summary>
        /// Gets whether the mounted media is read-only.
        /// </summary>
        public bool IsReadOnly { get; }

        public bool HasErrorInfo
        {
            get { return _errorInfo != null; }
        }

        private int SectorCount
        {
            get { return _data.Length / SectorSize; }
        }

        /// <summary>
        /// Gets the PETSCII disk name.
        /// </summary>
        public string DiskName { get; }

        /// <summary>
        /// Gets the disk identifier.
        /// </summary>
        public string DiskId { get; }

        /// <summary>
        /// Handles the load operation.
        /// </summary>
        public static D64Image Load(string path)
        {
            byte[] allBytes = File.ReadAllBytes(path);
            int trackCount;
            int sectorCount;
            bool hasErrorInfo;

            switch (allBytes.Length)
            {
                case 174848:
                    trackCount = 35;
                    sectorCount = 683;
                    hasErrorInfo = false;
                    break;
                case 175531:
                    trackCount = 35;
                    sectorCount = 683;
                    hasErrorInfo = true;
                    break;
                case 196608:
                    trackCount = 40;
                    sectorCount = 768;
                    hasErrorInfo = false;
                    break;
                case 197376:
                    trackCount = 40;
                    sectorCount = 768;
                    hasErrorInfo = true;
                    break;
                case 205312:
                    trackCount = 42;
                    sectorCount = 802;
                    hasErrorInfo = false;
                    break;
                case 206114:
                    trackCount = 42;
                    sectorCount = 802;
                    hasErrorInfo = true;
                    break;
                default:
                    throw new InvalidDataException("Unsupported D64 size.");
            }

            int dataSize = sectorCount * SectorSize;
            byte[] data = new byte[dataSize];
            Array.Copy(allBytes, 0, data, 0, dataSize);

            byte[] errorInfo = null;
            if (hasErrorInfo)
            {
                errorInfo = new byte[sectorCount];
                Array.Copy(allBytes, dataSize, errorInfo, 0, sectorCount);
            }

            return new D64Image(path, data, errorInfo, trackCount);
        }

        /// <summary>
        /// Attempts to resolve load and reports whether it succeeded.
        /// </summary>
        public bool TryResolveLoad(string filename, out MediaLoadData loadData)
        {
            loadData = null;
            string normalizedRequest = NormalizeFilename(filename);
            if (normalizedRequest.Length == 0)
            {
                normalizedRequest = "*";
            }

            if (normalizedRequest.StartsWith("$", StringComparison.Ordinal))
            {
                loadData = new MediaLoadData("$", BuildDirectoryListingProgram(), true);
                return true;
            }

            foreach (D64DirectoryEntry entry in _entries)
            {
                if (!IsLoadablePrgEntry(entry) || !Matches(entry.NormalizedName, normalizedRequest))
                {
                    continue;
                }

                byte[] programBytes = ReadFile(entry);
                if (programBytes == null || programBytes.Length < 2)
                {
                    continue;
                }

                loadData = new MediaLoadData(entry.FileName, programBytes, false);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to build directory listing and reports whether it succeeded.
        /// </summary>
        public bool TryBuildDirectoryListing(out byte[] listingProgram)
        {
            listingProgram = BuildDirectoryListingProgram();
            return true;
        }

        /// <summary>
        /// Attempts to open sequential file and reports whether it succeeded.
        /// </summary>
        public bool TryOpenSequentialFile(string filename, out SequentialFileReader reader)
        {
            reader = null;

            string normalizedRequest = NormalizeFilename(filename);
            if (normalizedRequest.Length == 0)
            {
                normalizedRequest = "*";
            }

            foreach (D64DirectoryEntry entry in _entries)
            {
                if (!IsLoadablePrgEntry(entry) || !Matches(entry.NormalizedName, normalizedRequest))
                {
                    continue;
                }

                reader = new SequentialFileReader(this, entry.StartTrack, entry.StartSector);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether loadable prg entry is true.
        /// </summary>
        private bool IsLoadablePrgEntry(D64DirectoryEntry entry)
        {
            if (entry == null || !entry.IsPrg)
            {
                return false;
            }

            if (!TryGetSectorOffset(entry.StartTrack, entry.StartSector, out int _))
            {
                return false;
            }

            // Protected/custom disks often have non-DOS data on later sides.
            // Those sectors can decode to random PRG-looking entries with
            // impossible block counts. Standard LOAD must not chase those
            // bogus chains; custom loaders still read raw sectors directly.
            return entry.Blocks == 0 || entry.Blocks <= SectorCount;
        }

        /// <summary>
        /// Attempts to get track stream and reports whether it succeeded.
        /// </summary>
        public bool TryGetTrackStream(int halfTrack, out byte[] trackBytes)
        {
            trackBytes = null;

            int track = HalfTrackToTrack(halfTrack);
            if (track < 1 || track > TrackCount)
            {
                return false;
            }

            if (!_trackStreamCache.TryGetValue(track, out trackBytes))
            {
                trackBytes = BuildTrackStream(track);
                _trackStreamCache[track] = trackBytes;
            }

            return true;
        }

        /// <summary>
        /// Attempts to read sector and reports whether it succeeded.
        /// </summary>
        public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
        {
            sectorBytes = null;
            if (!TryGetSectorOffset(track, sector, out int offset))
            {
                return false;
            }

            sectorBytes = ReadSectorByOffset(offset);
            return true;
        }

        /// <summary>
        /// Attempts to write sector and reports whether it succeeded.
        /// </summary>
        public bool TryWriteSector(int track, int sector, byte[] sectorBytes)
        {
            if (IsReadOnly)
            {
                return false;
            }

            if (sectorBytes == null || sectorBytes.Length < SectorSize)
            {
                return false;
            }

            if (!TryGetSectorOffset(track, sector, out int offset))
            {
                return false;
            }

            Array.Copy(sectorBytes, 0, _data, offset, SectorSize);
            _trackStreamCache.Remove(track);
            return true;
        }

        /// <summary>
        /// Attempts to get sector error code and reports whether it succeeded.
        /// </summary>
        public bool TryGetSectorErrorCode(int track, int sector, out byte errorCode)
        {
            errorCode = 0x01;
            if (!TryGetSectorIndex(track, sector, out int sectorIndex))
            {
                return false;
            }

            if (_errorInfo == null || sectorIndex < 0 || sectorIndex >= _errorInfo.Length)
            {
                errorCode = 0x01;
                return true;
            }

            byte raw = _errorInfo[sectorIndex];
            errorCode = raw == 0x00 ? (byte)0x01 : raw;
            return true;
        }

        /// <summary>
        /// Attempts to allocate sector and reports whether it succeeded.
        /// </summary>
        public bool TryAllocateSector(int track, int sector)
        {
            if (IsReadOnly)
            {
                return false;
            }

            return UpdateBamSectorBit(track, sector, false);
        }

        /// <summary>
        /// Attempts to free sector and reports whether it succeeded.
        /// </summary>
        public bool TryFreeSector(int track, int sector)
        {
            if (IsReadOnly)
            {
                return false;
            }

            return UpdateBamSectorBit(track, sector, true);
        }

        /// <summary>
        /// Gets the track length bytes value.
        /// </summary>
        public int GetTrackLengthBytes(int halfTrack)
        {
            byte[] trackBytes;
            return TryGetTrackStream(halfTrack, out trackBytes) && trackBytes != null
                ? trackBytes.Length
                : 0;
        }

        /// <summary>
        /// Reads directory entries.
        /// </summary>
        private List<D64DirectoryEntry> ReadDirectoryEntries()
        {
            var entries = new List<D64DirectoryEntry>();
            var visited = new HashSet<int>();
            int track = 18;
            int sector = 1;

            while (track != 0)
            {
                int key = (track << 8) | sector;
                if (!visited.Add(key))
                {
                    break;
                }

                if (!TryReadSector(track, sector, out byte[] sectorBytes) || sectorBytes == null)
                {
                    break;
                }

                int nextTrack = sectorBytes[0];
                int nextSector = sectorBytes[1];
                for (int entryIndex = 0; entryIndex < 8; entryIndex++)
                {
                    int entryOffset = entryIndex * 32;
                    byte fileType = sectorBytes[entryOffset + 2];
                    int typeCode = fileType & 0x07;
                    if (typeCode == 0)
                    {
                        continue;
                    }

                    int startTrack = sectorBytes[entryOffset + 3];
                    int startSector = sectorBytes[entryOffset + 4];
                    string fileName = DecodePetsciiName(sectorBytes, entryOffset + 5, 16);
                    int blocks = sectorBytes[entryOffset + 30] | (sectorBytes[entryOffset + 31] << 8);

                    entries.Add(new D64DirectoryEntry(
                        fileName,
                        NormalizeFilename(fileName),
                        typeCode == 2,
                        GetFileTypeName(typeCode),
                        startTrack,
                        startSector,
                        blocks,
                        (fileType & 0x80) != 0,
                        (fileType & 0x40) != 0));
                }

                track = nextTrack;
                sector = nextSector;
            }

            return entries;
        }

        /// <summary>
        /// Builds directory listing program.
        /// </summary>
        private byte[] BuildDirectoryListingProgram()
        {
            var lines = new List<BasicDirectoryLine>();
            string title = "\"" + (string.IsNullOrWhiteSpace(DiskName) ? Path.GetFileNameWithoutExtension(HostPath) : DiskName) + "\"";
            if (!string.IsNullOrWhiteSpace(DiskId))
            {
                title += " " + DiskId;
            }

            lines.Add(new BasicDirectoryLine(0, title));
            foreach (D64DirectoryEntry entry in _entries)
            {
                lines.Add(new BasicDirectoryLine(entry.Blocks, "\"" + entry.FileName + "\" " + entry.FileTypeName));
            }

            lines.Add(new BasicDirectoryLine(GetFreeBlocks(), "BLOCKS FREE."));

            var bytes = new List<byte>();
            bytes.Add(0x01);
            bytes.Add(0x08);

            ushort lineAddress = 0x0801;
            for (int index = 0; index < lines.Count; index++)
            {
                byte[] lineTextBytes = Encoding.ASCII.GetBytes(lines[index].Text);
                bool isLastLine = index == lines.Count - 1;
                ushort nextLineAddress = isLastLine ? (ushort)0x0000 : (ushort)(lineAddress + 4 + lineTextBytes.Length + 1);

                bytes.Add((byte)(nextLineAddress & 0xFF));
                bytes.Add((byte)(nextLineAddress >> 8));
                bytes.Add((byte)(lines[index].LineNumber & 0xFF));
                bytes.Add((byte)(lines[index].LineNumber >> 8));
                bytes.AddRange(lineTextBytes);
                bytes.Add(0x00);

                lineAddress = nextLineAddress;
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// Gets the free blocks value.
        /// </summary>
        private int GetFreeBlocks()
        {
            byte[] bam = ReadSector(18, 0);
            int freeBlocks = 0;
            int tracksToRead = Math.Min(TrackCount, 42);
            for (int track = 1; track <= tracksToRead; track++)
            {
                int offset = 4 + ((track - 1) * 4);
                if (offset >= bam.Length)
                {
                    break;
                }

                freeBlocks += bam[offset];
            }

            return freeBlocks;
        }

        /// <summary>
        /// Reads disk name.
        /// </summary>
        private string ReadDiskName()
        {
            byte[] bam = ReadSector(18, 0);
            return DecodePetsciiName(bam, 0x90, 16);
        }

        /// <summary>
        /// Reads disk id.
        /// </summary>
        private string ReadDiskId()
        {
            byte[] bam = ReadSector(18, 0);
            return DecodePetsciiName(bam, 0xA2, 2);
        }

        /// <summary>
        /// Reads file.
        /// </summary>
        private byte[] ReadFile(D64DirectoryEntry entry)
        {
            var result = new List<byte>();
            var visited = new HashSet<int>();
            int track = entry.StartTrack;
            int sector = entry.StartSector;

            while (track != 0)
            {
                int key = (track << 8) | sector;
                if (!visited.Add(key))
                {
                    break;
                }

                if (!TryReadSector(track, sector, out byte[] sectorBytes) || sectorBytes == null)
                {
                    break;
                }

                int nextTrack = sectorBytes[0];
                int nextSector = sectorBytes[1];

                if (nextTrack == 0)
                {
                    int usedBytes = Math.Max(0, Math.Min(254, nextSector - 1));
                    for (int index = 0; index < usedBytes; index++)
                    {
                        result.Add(sectorBytes[2 + index]);
                    }

                    break;
                }

                for (int index = 2; index < SectorSize; index++)
                {
                    result.Add(sectorBytes[index]);
                }

                track = nextTrack;
                sector = nextSector;
            }

            return result.ToArray();
        }

        /// <summary>
        /// Reads sector.
        /// </summary>
        private byte[] ReadSector(int track, int sector)
        {
            if (!TryGetSectorOffset(track, sector, out int offset))
            {
                throw new InvalidDataException("Invalid D64 track or sector.");
            }
            return ReadSectorByOffset(offset);
        }

        /// <summary>
        /// Reads sector by offset.
        /// </summary>
        private byte[] ReadSectorByOffset(int offset)
        {
            var buffer = new byte[SectorSize];
            Array.Copy(_data, offset, buffer, 0, SectorSize);
            return buffer;
        }

        /// <summary>
        /// Updates bam sector bit.
        /// </summary>
        private bool UpdateBamSectorBit(int track, int sector, bool free)
        {
            if (track < 1 || track > TrackCount)
            {
                return false;
            }

            int sectorsPerTrack = GetSectorsPerTrack(track);
            if (sector < 0 || sector >= sectorsPerTrack)
            {
                return false;
            }

            byte[] bam = ReadSector(18, 0);
            int offset = 4 + ((track - 1) * 4);
            if (offset + 3 >= bam.Length)
            {
                return false;
            }

            int maskByteIndex = offset + 1 + (sector / 8);
            byte mask = (byte)(1 << (sector % 8));
            bool wasFree = (bam[maskByteIndex] & mask) != 0;

            if (free)
            {
                if (!wasFree)
                {
                    bam[maskByteIndex] |= mask;
                    bam[offset] = (byte)Math.Min(255, bam[offset] + 1);
                }
            }
            else
            {
                if (wasFree)
                {
                    bam[maskByteIndex] &= (byte)~mask;
                    bam[offset] = (byte)Math.Max(0, bam[offset] - 1);
                }
            }

            return TryWriteSector(18, 0, bam);
        }

        /// <summary>
        /// Gets the sectors per track value.
        /// </summary>
        private static int GetSectorsPerTrack(int track)
        {
            if (track <= 17)
            {
                return 21;
            }

            if (track <= 24)
            {
                return 19;
            }

            if (track <= 30)
            {
                return 18;
            }

            return 17;
        }

        /// <summary>
        /// Handles the matches operation.
        /// </summary>
        private static bool Matches(string entryName, string requestedName)
        {
            if (requestedName == "*")
            {
                return true;
            }

            int wildcardIndex = requestedName.IndexOf('*');
            if (wildcardIndex >= 0)
            {
                string prefix = requestedName.Substring(0, wildcardIndex);
                return entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(entryName, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return entryName.StartsWith(requestedName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Handles the normalize filename operation.
        /// </summary>
        private static string NormalizeFilename(string filename)
        {
            string cleaned = (filename ?? string.Empty).Trim().Trim('"').ToUpperInvariant();
            int colonIndex = cleaned.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < cleaned.Length - 1)
            {
                cleaned = cleaned.Substring(colonIndex + 1);
            }

            return cleaned;
        }

        /// <summary>
        /// Decodes petscii name.
        /// </summary>
        private static string DecodePetsciiName(byte[] bytes, int offset, int length)
        {
            var builder = new StringBuilder(length);
            for (int index = 0; index < length; index++)
            {
                byte value = bytes[offset + index];
                if (value == 0x00 || value == 0xA0)
                {
                    break;
                }

                if (value >= 0x20 && value <= 0x7E)
                {
                    builder.Append((char)value);
                }
                else if (value >= 0xC1 && value <= 0xDA)
                {
                    builder.Append((char)(value - 0x80));
                }
                else
                {
                    builder.Append(' ');
                }
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// Gets the file type name value.
        /// </summary>
        private static string GetFileTypeName(int typeCode)
        {
            switch (typeCode)
            {
                case 1:
                    return "SEQ";
                case 2:
                    return "PRG";
                case 3:
                    return "USR";
                case 4:
                    return "REL";
                default:
                    return "DEL";
            }
        }

        /// <summary>
        /// Represents the basic directory line component.
        /// </summary>
        private sealed class BasicDirectoryLine
        {
            /// <summary>
            /// Initializes a new BasicDirectoryLine instance.
            /// </summary>
            public BasicDirectoryLine(int lineNumber, string text)
            {
                LineNumber = Math.Max(0, Math.Min(65535, lineNumber));
                Text = text ?? string.Empty;
            }

            /// <summary>
            /// Gets the BASIC directory line number.
            /// </summary>
            public int LineNumber { get; }

            /// <summary>
            /// Gets the rendered directory line text.
            /// </summary>
            public string Text { get; }
        }

        /// <summary>
        /// Represents the d64 directory entry component.
        /// </summary>
        private sealed class D64DirectoryEntry
        {
            /// <summary>
            /// Initializes a new D64DirectoryEntry instance.
            /// </summary>
            public D64DirectoryEntry(string fileName, string normalizedName, bool isPrg, string fileTypeName, int startTrack, int startSector, int blocks, bool closed, bool locked)
            {
                FileName = fileName;
                NormalizedName = normalizedName;
                IsPrg = isPrg;
                FileTypeName = fileTypeName;
                StartTrack = startTrack;
                StartSector = startSector;
                Blocks = blocks;
                Closed = closed;
                Locked = locked;
            }

            /// <summary>
            /// Gets the CBM-DOS filename.
            /// </summary>
            public string FileName { get; }

            /// <summary>
            /// Gets the normalized filename used for matching.
            /// </summary>
            public string NormalizedName { get; }

            /// <summary>
            /// Gets whether the directory entry is a PRG file.
            /// </summary>
            public bool IsPrg { get; }

            /// <summary>
            /// Gets the CBM-DOS file type name.
            /// </summary>
            public string FileTypeName { get; }

            /// <summary>
            /// Gets the first track of the file chain.
            /// </summary>
            public int StartTrack { get; }

            /// <summary>
            /// Gets the first sector of the file chain.
            /// </summary>
            public int StartSector { get; }

            /// <summary>
            /// Gets the directory block count.
            /// </summary>
            public int Blocks { get; }

            /// <summary>
            /// Gets whether the directory entry is closed.
            /// </summary>
            public bool Closed { get; }

            /// <summary>
            /// Gets whether the directory entry is locked.
            /// </summary>
            public bool Locked { get; }
        }

        /// <summary>
        /// Represents the sequential file reader component.
        /// </summary>
        public sealed class SequentialFileReader
        {
            private readonly D64Image _image;
            private readonly HashSet<int> _visitedSectors = new HashSet<int>();
            private int _track;
            private int _sector;
            private byte[] _sectorBytes;
            private int _index;
            private int _lastSectorLength;
            private bool _finished;
            private int _bytesRead;
            private bool _aborted;

            /// <summary>
            /// Initializes a new SequentialFileReader instance.
            /// </summary>
            internal SequentialFileReader(D64Image image, int startTrack, int startSector)
            {
                _image = image;
                _track = startTrack;
                _sector = startSector;
                _sectorBytes = null;
                _index = 2;
                _lastSectorLength = -1;
                _finished = false;
                _bytesRead = 0;
                _aborted = false;
            }

            /// <summary>
            /// Attempts to read byte and reports whether it succeeded.
            /// </summary>
            public bool TryReadByte(out byte value)
            {
                value = 0x00;
                while (!_finished)
                {
                    if (_sectorBytes == null)
                    {
                        if (_track == 0)
                        {
                            _finished = true;
                            return false;
                        }

                        int key = (_track << 8) | _sector;
                        if (!_visitedSectors.Add(key) ||
                            _visitedSectors.Count > _image.SectorCount ||
                            !_image.TryReadSector(_track, _sector, out _sectorBytes) ||
                            _sectorBytes == null)
                        {
                            AbortRead();
                            return false;
                        }

                        int nextTrack = _sectorBytes[0];
                        int nextSector = _sectorBytes[1];

                        if (nextTrack != 0)
                        {
                            int nextKey = (nextTrack << 8) | nextSector;
                            if (_visitedSectors.Contains(nextKey) ||
                                !_image.TryGetSectorOffset(nextTrack, nextSector, out int _))
                            {
                                // Corrupt directory/file chains occasionally
                                // loop or point outside the D64. Treat the
                                // current sector as the last full data sector
                                // so the IEC sender can still mark EOI instead
                                // of leaving the C64 waiting for another byte.
                                nextTrack = 0;
                                nextSector = 255;
                            }
                        }

                        _lastSectorLength = nextTrack == 0 ? Math.Max(0, Math.Min(254, nextSector - 1)) : 254;
                        _track = nextTrack;
                        _sector = nextSector;
                        _index = 2;
                    }

                    int endIndex = 2 + _lastSectorLength;
                    if (_index < endIndex)
                    {
                        value = _sectorBytes[_index++];
                        _bytesRead++;
                        if (_index >= endIndex)
                        {
                            _sectorBytes = null;
                            if (_track == 0)
                            {
                                _finished = true;
                            }
                        }

                        return true;
                    }

                    _sectorBytes = null;
                    if (_track == 0)
                    {
                        _finished = true;
                        return false;
                    }
                }

                return false;
            }

            /// <summary>
            /// Handles the abort read operation.
            /// </summary>
            private void AbortRead()
            {
                _aborted = true;
                _finished = true;
                _sectorBytes = null;
                _lastSectorLength = 0;
                _track = 0;
                _sector = 0;
                _index = 2;
            }

            public bool IsFinished
            {
                get { return _finished; }
            }

            public bool Aborted
            {
                get { return _aborted; }
            }

            public int BytesRead
            {
                get { return _bytesRead; }
            }

            /// <summary>
            /// Reads all bytes.
            /// </summary>
            public byte[] ReadAllBytes()
            {
                var bytes = new List<byte>(4096);
                byte value;
                while (TryReadByte(out value))
                {
                    bytes.Add(value);
                }

                return bytes.ToArray();
            }
        }

        /// <summary>
        /// Builds track stream.
        /// </summary>
        private byte[] BuildTrackStream(int track)
        {
            var stream = new List<byte>(8192);
            byte[] bam = ReadSector(18, 0);
            byte diskId1 = bam[0xA2];
            byte diskId2 = bam[0xA3];
            int sectorsPerTrack = GetSectorsPerTrack(track);
            int postDataGapSize = GetPostDataGapSize(track);

            for (int sector = 0; sector < sectorsPerTrack; sector++)
            {
                TryGetSectorErrorCode(track, sector, out byte errorCode);

                if (errorCode == 0x02 || errorCode == 0x03 || errorCode == 0x0F)
                {
                    AddFill(stream, 10, 0x55);
                }
                else
                {
                    AddSync(stream, 5);
                    AddGcrEncoded(stream, BuildHeaderBlock(track, sector, diskId1, diskId2, errorCode));
                }

                AddFill(stream, 9, 0x55);

                if (errorCode == 0x03 || errorCode == 0x0F)
                {
                    AddFill(stream, 10, 0x55);
                }
                else
                {
                    AddSync(stream, 5);
                    AddGcrEncoded(stream, BuildDataBlock(track, sector, errorCode));
                }

                AddFill(stream, postDataGapSize, 0x55);
            }

            int trackSizeBytes = GetTrackSizeBytes(track);
            while (stream.Count < trackSizeBytes)
            {
                stream.Add(0x55);
            }

            return stream.ToArray();
        }

        /// <summary>
        /// Builds header block.
        /// </summary>
        private byte[] BuildHeaderBlock(int track, int sector, byte diskId1, byte diskId2, byte errorCode)
        {
            byte checksum = (byte)(sector ^ track ^ diskId1 ^ diskId2);
            if (errorCode == 0x09)
            {
                checksum ^= 0xFF;
            }

            if (errorCode == 0x0B)
            {
                diskId1 ^= 0xFF;
            }

            return new byte[]
            {
                0x08,
                checksum,
                (byte)sector,
                (byte)track,
                diskId2,
                diskId1,
                0x0F,
                0x0F
            };
        }

        /// <summary>
        /// Builds data block.
        /// </summary>
        private byte[] BuildDataBlock(int track, int sector, byte errorCode)
        {
            byte[] sectorBytes = ReadSector(track, sector);
            var block = new byte[260];
            block[0] = errorCode == 0x04 ? (byte)0x00 : (byte)0x07;
            Array.Copy(sectorBytes, 0, block, 1, SectorSize);

            byte checksum = 0x00;
            for (int index = 0; index < SectorSize; index++)
            {
                checksum ^= sectorBytes[index];
            }

            if (errorCode == 0x05)
            {
                checksum ^= 0xFF;
            }

            block[257] = checksum;
            block[258] = 0x00;
            block[259] = 0x00;
            return block;
        }

        /// <summary>
        /// Attempts to get sector offset and reports whether it succeeded.
        /// </summary>
        private bool TryGetSectorOffset(int track, int sector, out int offset)
        {
            offset = 0;
            if (!TryGetSectorIndex(track, sector, out int sectorIndex))
            {
                return false;
            }

            offset = sectorIndex * SectorSize;
            return true;
        }

        /// <summary>
        /// Attempts to get sector index and reports whether it succeeded.
        /// </summary>
        private bool TryGetSectorIndex(int track, int sector, out int sectorIndex)
        {
            sectorIndex = -1;
            if (track < 1 || track > TrackCount || track > 42)
            {
                return false;
            }

            int sectorsPerTrack = GetSectorsPerTrack(track);
            if (sector < 0 || sector >= sectorsPerTrack)
            {
                return false;
            }

            sectorIndex = TrackStartSectorOffsets[track - 1] + sector;
            return sectorIndex >= 0 && sectorIndex < (_data.Length / SectorSize);
        }

        /// <summary>
        /// Builds track start sector offsets.
        /// </summary>
        private static int[] BuildTrackStartSectorOffsets()
        {
            int[] offsets = new int[42];
            int sectorCount = 0;
            for (int track = 1; track <= 42; track++)
            {
                offsets[track - 1] = sectorCount;
                sectorCount += GetSectorsPerTrack(track);
            }

            return offsets;
        }

        /// <summary>
        /// Handles the add sync operation.
        /// </summary>
        private static void AddSync(List<byte> stream, int byteCount)
        {
            for (int index = 0; index < byteCount; index++)
            {
                stream.Add(0xFF);
            }
        }

        /// <summary>
        /// Handles the add fill operation.
        /// </summary>
        private static void AddFill(List<byte> stream, int byteCount, byte value)
        {
            for (int index = 0; index < byteCount; index++)
            {
                stream.Add(value);
            }
        }

        /// <summary>
        /// Handles the add gcr encoded operation.
        /// </summary>
        private static void AddGcrEncoded(List<byte> stream, byte[] rawBytes)
        {
            for (int offset = 0; offset < rawBytes.Length; offset += 4)
            {
                ulong bits = 0;
                for (int index = 0; index < 4; index++)
                {
                    byte value = rawBytes[offset + index];
                    bits = (bits << 5) | GcrEncodeTable[(value >> 4) & 0x0F];
                    bits = (bits << 5) | GcrEncodeTable[value & 0x0F];
                }

                for (int shift = 32; shift >= 0; shift -= 8)
                {
                    stream.Add((byte)((bits >> shift) & 0xFF));
                }
            }
        }

        /// <summary>
        /// Handles the half track to track operation.
        /// </summary>
        private static int HalfTrackToTrack(int halfTrack)
        {
            if (halfTrack < 0)
            {
                return 1;
            }

            return (halfTrack / 2) + 1;
        }

        /// <summary>
        /// Gets the post data gap size value.
        /// </summary>
        private static int GetPostDataGapSize(int track)
        {
            if (track <= 17)
            {
                return 8;
            }

            if (track <= 24)
            {
                return 17;
            }

            if (track <= 30)
            {
                return 12;
            }

            return 9;
        }

        /// <summary>
        /// Gets the track size bytes value.
        /// </summary>
        private static int GetTrackSizeBytes(int track)
        {
            if (track <= 17)
            {
                return 7692;
            }

            if (track <= 24)
            {
                return 7142;
            }

            if (track <= 30)
            {
                return 6666;
            }

            return 6250;
        }
    }
}
