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
        private const int HeaderSyncBytes = 5;
        private const int HeaderGapBytes = 9;
        private const int DataSyncBytes = 5;
        private const int EncodedHeaderBlockBytes = 10;
        private const int EncodedDataBlockBytes = 325;
        private const int TrackStepSkewNumerator = 100;
        private const int TrackStepSkewDenominator = 270;
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
        public string DiskName { get; private set; }

        /// <summary>
        /// Gets the disk identifier.
        /// </summary>
        public string DiskId { get; private set; }

        /// <summary>
        /// Handles the load operation.
        /// </summary>
        public static D64Image Load(string path)
        {
            byte[] allBytes = ReadAllBytesShared(path);
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
        /// Reads a disk image without taking an exclusive host-file lock.
        /// </summary>
        private static byte[] ReadAllBytesShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        /// <summary>
        /// Recreates a D64 image from bytes stored inside a savestate.
        /// </summary>
        public static D64Image LoadState(BinaryReader reader)
        {
            string hostPath = BinaryStateIO.ReadString(reader);
            int trackCount = reader.ReadInt32();
            byte[] data = BinaryStateIO.ReadByteArray(reader);
            byte[] errorInfo = BinaryStateIO.ReadByteArray(reader);
            if (data == null)
            {
                throw new InvalidDataException("Savestate disk image is missing data.");
            }

            return new D64Image(hostPath, data, errorInfo, trackCount);
        }

        /// <summary>
        /// Writes all disk image bytes into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            BinaryStateIO.WriteString(writer, HostPath);
            writer.Write(TrackCount);
            BinaryStateIO.WriteByteArray(writer, _data);
            BinaryStateIO.WriteByteArray(writer, _errorInfo);
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

        private sealed class SequentialFileSpec
        {
            public string Name;
            public byte Type; // 0 = infer from an existing file; 1 = SEQ, 2 = PRG, 3 = USR.
            public char Mode;
            public bool Replace;
            public string Canonical => (Replace ? "@" : "") + "0:" + Name + "," +
                (Type == 1 ? "S" : Type == 2 ? "P" : "U") + "," + Mode;
        }

        private static bool TryParseSequentialSpec(string filename, byte defaultType, char defaultMode,
            out SequentialFileSpec spec, out string statusText)
        {
            spec = null;
            statusText = "30, SYNTAX ERROR,00,00";
            string request = (filename ?? string.Empty).Trim().ToUpperInvariant();
            bool replace = request.StartsWith("@", StringComparison.Ordinal);
            if (replace) request = request.Substring(1);
            string[] parts = request.Split(',');
            if (parts.Length > 3) return false;
            string name = parts[0];
            int colon = name.IndexOf(':');
            if (colon >= 0)
            {
                if (colon > 0 && name.Substring(0, colon) != "0")
                { statusText = "74, DRIVE NOT READY,00,00"; return false; }
                name = name.Substring(colon + 1);
            }
            if (name.Length == 0) { statusText = "34, SYNTAX ERROR,00,00"; return false; }
            if (name.Length > 16) { statusText = "33, SYNTAX ERROR,00,00"; return false; }
            byte type = defaultType;
            char mode = defaultMode;
            bool explicitType = false;
            byte ParseType(string value) => value switch
            {
                "S" or "SEQ" or "SEQUENTIAL" => 1,
                "P" or "PRG" or "PROGRAM" => 2,
                "U" or "USR" or "USER" => 3,
                _ => 0
            };
            char ParseMode(string value) => value switch
            {
                "R" or "READ" => 'R',
                "W" or "WRITE" => 'W',
                "A" or "APPEND" => 'A',
                _ => '\0'
            };
            if (parts.Length >= 2)
            {
                string option = parts[1].Trim();
                if (parts.Length == 2 && ParseMode(option) != '\0') mode = ParseMode(option);
                else if (option.Length > 0)
                {
                    type = ParseType(option);
                    if (type == 0) { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                    explicitType = true;
                }
            }
            if (parts.Length == 3)
            {
                mode = ParseMode(parts[2].Trim());
                if (mode == '\0') return false;
            }
            if (mode == 'A' && !explicitType) type = 0;
            if (replace && mode != 'W') return false;
            foreach (char c in name)
            {
                if (c < 0x20 || c > 0x7E || c == ':' || c == '"' ||
                    (mode == 'W' && (c == '*' || c == '?')))
                { statusText = "33, SYNTAX ERROR,00,00"; return false; }
            }
            spec = new SequentialFileSpec { Name = name, Type = type, Mode = mode, Replace = replace };
            statusText = "00, OK,00,00";
            return true;
        }

        private static byte SequentialEntryType(D64DirectoryEntry entry) => entry.FileTypeName switch
        {
            "SEQ" => 1, "PRG" => 2, "USR" => 3, _ => 0
        };

        private static bool MatchesSequentialName(string name, string pattern)
        {
            // Commodore '*' accepts the remaining suffix; '?' accepts one character.
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '*') return true;
                if (i >= name.Length || (pattern[i] != '?' && pattern[i] != name[i])) return false;
            }
            return name.Length == pattern.Length;
        }

        private bool TryFindSequentialRead(string filename, out D64DirectoryEntry found, out string statusText)
        {
            found = null;
            if (!TryParseSequentialSpec(filename, 0, 'R', out SequentialFileSpec spec, out statusText)) return false;
            if (spec.Mode != 'R' || spec.Replace)
            { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
            statusText = "62, FILE NOT FOUND,00,00";
            foreach (D64DirectoryEntry entry in _entries)
            {
                if (!MatchesSequentialName(entry.NormalizedName, spec.Name)) continue;
                byte type = SequentialEntryType(entry);
                if (type == 0 || (spec.Type != 0 && spec.Type != type))
                { statusText = "64, FILE TYPE MISMATCH,00,00"; continue; }
                if (!entry.Closed) { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                if (!TryGetSectorOffset(entry.StartTrack, entry.StartSector, out _) || entry.Blocks > SectorCount)
                { statusText = "71, DIR ERROR,00,00"; return false; }
                found = entry;
                statusText = "00, OK,00,00";
                return true;
            }
            return false;
        }

        /// <summary>Opens a SEQ, PRG or USR byte stream, with optional type/read qualifiers.</summary>
        public bool TryOpenSequentialFile(string filename, out SequentialFileReader reader)
            => TryOpenSequentialFile(filename, out reader, out _);

        public bool TryOpenSequentialFile(string filename, out SequentialFileReader reader, out string statusText)
        {
            reader = null;
            if (!TryFindSequentialRead(filename, out D64DirectoryEntry entry, out statusText)) return false;
            reader = new SequentialFileReader(this, entry.StartTrack, entry.StartSector);
            return true;
        }

        /// <summary>Resolves a readable sequential file without consuming its data.</summary>
        public bool TryResolveSequentialFileStart(string filename, out int startTrack, out int startSector, out int blocks)
        {
            startTrack = startSector = blocks = 0;
            if (!TryFindSequentialRead(filename, out D64DirectoryEntry entry, out _)) return false;
            startTrack = entry.StartTrack; startSector = entry.StartSector; blocks = entry.Blocks;
            return true;
        }

        /// <summary>
        /// Validates an OPEN for write/append without changing disk data. The returned
        /// specification fixes inferred types and wildcard append targets until CLOSE.
        /// </summary>
        public bool TryPrepareSequentialWrite(string filename, byte defaultType,
            out string canonicalSpecification, out string statusText)
        {
            canonicalSpecification = null;
            if (!TryParseSequentialSpec(filename, defaultType, 'W', out SequentialFileSpec spec, out statusText)) return false;
            if (spec.Mode != 'W' && spec.Mode != 'A')
            { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
            if (IsReadOnly || (!string.IsNullOrWhiteSpace(HostPath) && File.Exists(HostPath) && new FileInfo(HostPath).IsReadOnly))
            { statusText = "26, WRITE PROTECT ON,00,00"; return false; }
            D64DirectoryEntry existing = null;
            foreach (D64DirectoryEntry entry in _entries)
            {
                bool match = spec.Mode == 'A' ? MatchesSequentialName(entry.NormalizedName, spec.Name) : entry.NormalizedName == spec.Name;
                if (match) { existing = entry; break; }
            }
            if (existing == null)
            {
                if (spec.Mode == 'A') { statusText = "62, FILE NOT FOUND,00,00"; return false; }
                if (spec.Type < 1 || spec.Type > 3) { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
            }
            else
            {
                if (spec.Mode == 'W' && !spec.Replace) { statusText = "63, FILE EXISTS,00,00"; return false; }
                if (!existing.Closed) { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                if (existing.Locked) { statusText = "26, WRITE PROTECT ON,00,00"; return false; }
                byte type = SequentialEntryType(existing);
                if (type == 0 || (spec.Type != 0 && spec.Type != type))
                { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                spec.Name = existing.NormalizedName;
                spec.Type = type;
            }
            canonicalSpecification = spec.Canonical;
            statusText = "00, OK,00,00";
            return true;
        }

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
        /// Attempts to get the rotated GCR stream byte position at the start of a sector record.
        /// </summary>
        public bool TryGetSectorStreamStartOffset(int track, int sector, out int byteOffset)
        {
            byteOffset = 0;
            if (!TryGetSectorIndex(track, sector, out _))
            {
                return false;
            }

            int unrotatedOffset = 0;
            int sectorsPerTrack = GetSectorsPerTrack(track);
            int postDataGapSize = GetPostDataGapSize(track);
            foreach (int formattedSector in BuildFormattedSectorOrder(sectorsPerTrack))
            {
                TryGetSectorErrorCode(track, formattedSector, out byte errorCode);
                int headerBytes = errorCode == 0x02 || errorCode == 0x03 || errorCode == 0x0F
                    ? 10
                    : HeaderSyncBytes + EncodedHeaderBlockBytes;
                int dataBytes = errorCode == 0x03 || errorCode == 0x0F
                    ? 10
                    : DataSyncBytes + EncodedDataBlockBytes;
                int sectorStart = unrotatedOffset;
                int sectorEnd = sectorStart + headerBytes + HeaderGapBytes + dataBytes + postDataGapSize;

                if (formattedSector == sector)
                {
                    int streamLength = GetTrackSizeBytes(track);
                    byteOffset = (sectorStart + GetD64TrackSkewOffsetBytes(track)) % streamLength;
                    return true;
                }

                unrotatedOffset = sectorEnd;
            }

            return false;
        }

        /// <summary>
        /// Attempts to get the rotated GCR stream byte position at the start of the next physically formatted sector.
        /// </summary>
        public bool TryGetNextSectorStreamStartOffset(int track, int sector, out int byteOffset)
        {
            byteOffset = 0;
            if (!TryGetNextPhysicalSector(track, sector, out int nextSector))
            {
                return false;
            }

            return TryGetSectorStreamStartOffset(track, nextSector, out byteOffset);
        }

        /// <summary>
        /// Attempts to get the sector number that follows a sector in the physical formatted order.
        /// </summary>
        public bool TryGetNextPhysicalSector(int track, int sector, out int nextSector)
        {
            nextSector = 0;
            if (!TryGetSectorIndex(track, sector, out _))
            {
                return false;
            }

            int sectorsPerTrack = GetSectorsPerTrack(track);
            int previousSector = -1;
            int firstSector = -1;
            foreach (int formattedSector in BuildFormattedSectorOrder(sectorsPerTrack))
            {
                if (firstSector < 0)
                {
                    firstSector = formattedSector;
                }

                if (previousSector == sector)
                {
                    nextSector = formattedSector;
                    return true;
                }

                previousSector = formattedSector;
            }

            if (previousSector == sector && firstSector >= 0)
            {
                nextSector = firstSector;
                return true;
            }

            return false;
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

        /// <summary>Saves a BASIC/PRG payload including its two-byte load address.</summary>
        public bool TrySavePrg(string filename, byte[] programBytes, out string statusText)
        {
            statusText = "30, SYNTAX ERROR,00,00";
            if (programBytes == null || programBytes.Length < 2) return false;
            if (!TryParseSequentialSpec(filename, 2, 'W', out SequentialFileSpec spec, out statusText)) return false;
            if (spec.Type != 2 || spec.Mode != 'W')
            { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
            return TryWriteSequentialFile(spec.Canonical, programBytes, out statusText);
        }

        /// <summary>
        /// Creates, explicitly replaces, or appends a SEQ/PRG/USR byte stream.
        /// Changes to file data, directory, BAM and host file commit together.
        /// Empty streams are valid; PRG headers are not added or removed here.
        /// </summary>
        public bool TryWriteSequentialFile(string filename, byte[] payload, out string statusText)
        {
            if (!TryPrepareSequentialWrite(filename, 1, out string canonical, out statusText)) return false;
            if (payload == null) { statusText = "30, SYNTAX ERROR,00,00"; return false; }
            TryParseSequentialSpec(canonical, 1, 'W', out SequentialFileSpec request, out _);
            if (string.IsNullOrWhiteSpace(HostPath))
            { statusText = "74, DRIVE NOT READY,00,00"; return false; }
            string name = request.Name;
            bool replace = request.Replace;
            byte[] work = (byte[])_data.Clone();
            if (!TryGetSectorOffset(18, 0, out int bam))
            { statusText = "74, DRIVE NOT READY,00,00"; return false; }
            bool IsFree(int track, int sector)
            {
                int at = bam + track * 4;
                return (work[at + 1 + sector / 8] & (1 << (sector % 8))) != 0;
            }
            void Mark(int track, int sector, bool free)
            {
                int at = bam + track * 4, maskAt = at + 1 + sector / 8;
                byte mask = (byte)(1 << (sector % 8));
                bool wasFree = (work[maskAt] & mask) != 0;
                if (free == wasFree) return;
                if (free) { work[maskAt] |= mask; work[at]++; }
                else { work[maskAt] &= (byte)~mask; if (work[at] > 0) work[at]--; }
            }
            int dirTrack = 18, dirSector = 1, lastDirectory = -1, freeEntry = -1, existing = -1;
            var visited = new HashSet<int>();
            while (dirTrack != 0)
            {
                if (dirTrack != 18 || !visited.Add((dirTrack << 8) | dirSector) ||
                    !TryGetSectorOffset(dirTrack, dirSector, out int at))
                { statusText = "71, DIR ERROR,00,00"; return false; }
                lastDirectory = at;
                for (int index = 0; index < 8; index++)
                {
                    int entry = at + index * 32;
                    if ((work[entry + 2] & 7) == 0)
                    { if (freeEntry < 0) freeEntry = entry; }
                    else if (DecodePetsciiName(work, entry + 5, 16).ToUpperInvariant() == name)
                        existing = entry;
                }
                dirTrack = work[at]; dirSector = work[at + 1];
            }
            int target = freeEntry;
            var previousBytes = new List<byte>();
            var previousSectors = new List<(int Track, int Sector)>();
            if (existing < 0 && request.Mode == 'A')
            { statusText = "62, FILE NOT FOUND,00,00"; return false; }
            if (existing >= 0)
            {
                if (!replace && request.Mode != 'A') { statusText = "63, FILE EXISTS,00,00"; return false; }
                if ((work[existing + 2] & 0x80) == 0)
                { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                if ((work[existing + 2] & 0x40) != 0)
                { statusText = "26, WRITE PROTECT ON,00,00"; return false; }
                if ((work[existing + 2] & 7) != request.Type)
                { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                target = existing;
                int track = work[existing + 3], sector = work[existing + 4];
                var oldChain = new HashSet<int>();
                while (track != 0)
                {
                    if (track == 18 || track > 35 || !oldChain.Add((track << 8) | sector) ||
                        !TryGetSectorOffset(track, sector, out int at))
                    { statusText = "71, DIR ERROR,00,00"; return false; }
                    int used = work[at] == 0 ? work[at + 1] - 1 : 254;
                    if (used < 0 || used > 254) { statusText = "71, DIR ERROR,00,00"; return false; }
                    if (request.Mode == 'A')
                        for (int i = 0; i < used; i++) previousBytes.Add(work[at + 2 + i]);
                    previousSectors.Add((track, sector));
                    track = work[at]; sector = work[at + 1];
                }
            }
            if (request.Mode == 'A' && payload.Length == 0)
            { statusText = "00, OK,00,00"; return true; }
            if ((long)previousBytes.Count + payload.Length > 664L * 254)
            { statusText = "72, DISK FULL,00,00"; return false; }
            byte[] fileBytes = new byte[previousBytes.Count + payload.Length];
            previousBytes.CopyTo(fileBytes, 0);
            Array.Copy(payload, 0, fileBytes, previousBytes.Count, payload.Length);
            foreach (var sector in previousSectors) Mark(sector.Track, sector.Sector, true);
            if (target < 0)
            {
                int sector = -1;
                for (int candidate = 2; candidate < GetSectorsPerTrack(18); candidate++)
                    if (IsFree(18, candidate) && !visited.Contains((18 << 8) | candidate))
                    { sector = candidate; break; }
                if (sector < 0 || lastDirectory < 0)
                { statusText = "72, DISK FULL,00,00"; return false; }
                TryGetSectorOffset(18, sector, out target);
                Array.Clear(work, target, SectorSize);
                work[target + 1] = 0xFF;
                work[lastDirectory] = 18; work[lastDirectory + 1] = (byte)sector;
                Mark(18, sector, false);
            }

            int count = Math.Max(1, (fileBytes.Length + 253) / 254);
            var chain = new List<(int Track, int Sector)>();
            // Allocate data away from the directory track. Explicit replacement
            // may reuse its old sectors because all changes are still in work.
            for (int pass = 0; pass < 2 && chain.Count < count; pass++)
            {
                int begin = pass == 0 ? 17 : 19, end = pass == 0 ? 1 : Math.Min(35, TrackCount);
                for (int track = begin; pass == 0 ? track >= end : track <= end; track += pass == 0 ? -1 : 1)
                {
                    for (int sector = 0; sector < GetSectorsPerTrack(track) && chain.Count < count; sector++)
                    {
                        if (!IsFree(track, sector)) continue;
                        chain.Add((track, sector)); Mark(track, sector, false);
                    }
                    if (chain.Count == count) break;
                }
            }
            if (chain.Count != count) { statusText = "72, DISK FULL,00,00"; return false; }
            int copied = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                TryGetSectorOffset(chain[i].Track, chain[i].Sector, out int at);
                Array.Clear(work, at, SectorSize);
                int take = Math.Min(254, fileBytes.Length - copied);
                if (i + 1 < chain.Count)
                { work[at] = (byte)chain[i + 1].Track; work[at + 1] = (byte)chain[i + 1].Sector; }
                else { work[at] = 0; work[at + 1] = (byte)(take + 1); }
                Array.Copy(fileBytes, copied, work, at + 2, take); copied += take;
            }
            Array.Clear(work, target + 2, 30);
            work[target + 2] = (byte)(0x80 | request.Type);
            work[target + 3] = (byte)chain[0].Track; work[target + 4] = (byte)chain[0].Sector;
            for (int i = 0; i < 16; i++) work[target + 5 + i] = 0xA0;
            Encoding.ASCII.GetBytes(name).CopyTo(work, target + 5);
            work[target + 30] = (byte)count; work[target + 31] = (byte)(count >> 8);

            return CommitCandidate(work, out statusText);
        }

        // Functional DOS operations. Every mutating request builds a separate
        // candidate image and publishes it only after the host write succeeds.
        private bool CanPersist(out string statusText)
        {
            statusText = "00, OK,00,00";
            if (string.IsNullOrWhiteSpace(HostPath))
            { statusText = "74, DRIVE NOT READY,00,00"; return false; }
            if (IsReadOnly || (File.Exists(HostPath) && new FileInfo(HostPath).IsReadOnly))
            { statusText = "26, WRITE PROTECT ON,00,00"; return false; }
            return true;
        }

        private bool CommitCandidate(byte[] work, out string statusText, byte[] errorInfo = null)
        {
            if (!CanPersist(out statusText)) return false;
            byte[] errors = errorInfo ?? _errorInfo;
            string temporary = HostPath + ".dos-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(work, 0, work.Length);
                    if (errors != null) stream.Write(errors, 0, errors.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, HostPath, true);
            }
            catch (IOException) { statusText = "25, WRITE ERROR,00,00"; return false; }
            catch (UnauthorizedAccessException) { statusText = "26, WRITE PROTECT ON,00,00"; return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Array.Copy(work, _data, work.Length);
            if (errors != null && _errorInfo != null) Array.Copy(errors, _errorInfo, errors.Length);
            RefreshDirectory();
            statusText = "00, OK,00,00";
            return true;
        }

        public void RefreshDirectory()
        {
            _trackStreamCache.Clear();
            _entries.Clear(); _entries.AddRange(ReadDirectoryEntries());
            DiskName = ReadDiskName(); DiskId = ReadDiskId();
        }

        private bool CheckDosSectorReadable(int track, int sector, out string statusText)
        {
            statusText = "71, DIR ERROR,00,00";
            if (!TryGetSectorErrorCode(track, sector, out byte error) || error == 1) return true;
            int code = error switch { 2 => 20, 3 => 21, 4 => 22, 5 => 23, 7 => 25, 8 => 26, 9 => 27, 10 => 28, 11 => 29, 15 => 74, _ => 66 };
            string message = code switch { 25 or 28 => "WRITE ERROR", 26 => "WRITE PROTECT ON", 29 => "DISK ID MISMATCH", 74 => "DRIVE NOT READY", 66 => "ILLEGAL TRACK OR SECTOR", _ => "READ ERROR" };
            statusText = $"{code:00}, {message},{track:00},{sector:00}"; return false;
        }

        private bool TryDirectoryLayout(byte[] image, out List<int> entries,
            out List<(int Track, int Sector)> sectors, out string statusText)
        {
            entries = new List<int>(); sectors = new List<(int, int)>();
            statusText = "71, DIR ERROR,00,00";
            int track = 18, sector = 1;
            var visited = new HashSet<int>();
            while (track != 0)
            {
                if (track != 18 || sector == 0 || !TryGetSectorOffset(track, sector, out int at) ||
                    !visited.Add((track << 8) | sector)) return false;
                if (!CheckDosSectorReadable(track, sector, out statusText)) return false;
                sectors.Add((track, sector));
                for (int slot = 0; slot < 8; slot++) entries.Add(at + slot * 32);
                track = image[at]; sector = image[at + 1];
            }
            statusText = "00, OK,00,00";
            return sectors.Count > 0;
        }

        private bool BamIsFree(byte[] image, int track, int sector)
        {
            TryGetSectorOffset(18, 0, out int bam);
            return (image[bam + track * 4 + 1 + sector / 8] & (1 << (sector & 7))) != 0;
        }

        private void BamMark(byte[] image, int track, int sector, bool free)
        {
            TryGetSectorOffset(18, 0, out int bam);
            int at = bam + track * 4, bits = at + 1 + sector / 8;
            byte mask = (byte)(1 << (sector & 7));
            if (free) image[bits] |= mask; else image[bits] &= (byte)~mask;
            int count = 0;
            for (int s = 0; s < GetSectorsPerTrack(track); s++)
                if ((image[at + 1 + s / 8] & (1 << (s & 7))) != 0) count++;
            image[at] = (byte)count;
        }

        private string EntryName(byte[] image, int entry) => DecodePetsciiName(image, entry + 5, 16).ToUpperInvariant();

        private bool TryCollectFile(byte[] image, int entry, out byte[] bytes,
            out List<(int Track, int Sector)> allSectors, out string statusText)
        {
            bytes = Array.Empty<byte>(); allSectors = new List<(int, int)>();
            statusText = "71, DIR ERROR,00,00";
            int type = image[entry + 2] & 7;
            if (type < 1 || type > 4) return false;
            var dataSectors = new List<(int Track, int Sector)>();
            var visited = new HashSet<int>(); var payload = new List<byte>();
            int track = image[entry + 3], sector = image[entry + 4];
            if (track == 0) return false;
            while (track != 0)
            {
                if (track == 18 || track > 35 || !TryGetSectorOffset(track, sector, out int at) ||
                    !visited.Add((track << 8) | sector)) return false;
                if (!CheckDosSectorReadable(track, sector, out statusText)) return false;
                dataSectors.Add((track, sector));
                int used = image[at] == 0 ? image[at + 1] - 1 : 254;
                if (used < 0 || used > 254) return false;
                for (int i = 0; i < used; i++) payload.Add(image[at + 2 + i]);
                track = image[at]; sector = image[at + 1];
            }
            allSectors.AddRange(dataSectors);
            if (type == 4)
            {
                int length = image[entry + 23];
                if (length < 1 || length > 254 || payload.Count == 0 || payload.Count % length != 0) return false;
                if (dataSectors.Count > 720 || payload.Count / length > 65535) return false;
                var sides = new List<(int Track, int Sector)>();
                track = image[entry + 21]; sector = image[entry + 22];
                if (track == 0) return false;
                while (track != 0)
                {
                    if (track == 18 || track > 35 || sides.Count >= 6 ||
                        !TryGetSectorOffset(track, sector, out int at) || !visited.Add((track << 8) | sector)) return false;
                    if (!CheckDosSectorReadable(track, sector, out statusText)) return false;
                    if (image[at + 2] != sides.Count || image[at + 3] != length) { statusText = "71, DIR ERROR,00,00"; return false; }
                    sides.Add((track, sector));
                    track = image[at]; sector = image[at + 1];
                }
                if (sides.Count != (dataSectors.Count + 119) / 120) return false;
                for (int side = 0; side < sides.Count; side++)
                {
                    TryGetSectorOffset(sides[side].Track, sides[side].Sector, out int at);
                    int count = Math.Min(120, dataSectors.Count - side * 120);
                    if (side == sides.Count - 1 && image[at + 1] != 15 + 2 * count) return false;
                    // Unused index slots are not part of the file and may contain
                    // stale bytes on images produced by other DOS implementations.
                    for (int i = 0; i < sides.Count; i++)
                    {
                        int t = sides[i].Track, s = sides[i].Sector;
                        if (image[at + 4 + i * 2] != t || image[at + 5 + i * 2] != s) return false;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        int index = side * 120 + i;
                        int t = dataSectors[index].Track, s = dataSectors[index].Sector;
                        if (image[at + 16 + i * 2] != t || image[at + 17 + i * 2] != s) return false;
                    }
                }
                allSectors.AddRange(sides);
            }
            bytes = payload.ToArray();
            statusText = "00, OK,00,00";
            return true;
        }

        private bool TryOwnership(byte[] image, List<int> entries,
            List<(int Track, int Sector)> directory, out Dictionary<int, List<(int Track, int Sector)>> files,
            out HashSet<int> occupied, out string statusText)
        {
            files = new Dictionary<int, List<(int, int)>>(); occupied = new HashSet<int> { 18 << 8 };
            foreach (var s in directory) occupied.Add((s.Track << 8) | s.Sector);
            foreach (int entry in entries)
            {
                if ((image[entry + 2] & 7) == 0 || (image[entry + 2] & 0x80) == 0) continue;
                if (!TryCollectFile(image, entry, out _, out var chain, out statusText)) return false;
                foreach (var s in chain)
                    if (!occupied.Add((s.Track << 8) | s.Sector))
                    { statusText = "71, DIR ERROR,00,00"; return false; }
                files[entry] = chain;
            }
            statusText = "00, OK,00,00";
            return true;
        }

        private static bool TryDosName(string input, bool patterns, out string name, out string statusText)
        {
            name = (input ?? string.Empty).Trim().ToUpperInvariant();
            statusText = "33, SYNTAX ERROR,00,00";
            int colon = name.IndexOf(':');
            if (colon >= 0)
            {
                if (colon > 0 && name.Substring(0, colon) != "0")
                { statusText = "74, DRIVE NOT READY,00,00"; return false; }
                name = name.Substring(colon + 1);
            }
            if (name.Length == 0) { statusText = "34, SYNTAX ERROR,00,00"; return false; }
            if (name.Length > 16) return false;
            foreach (char c in name)
                if (c < 0x20 || c > 0x7E || c == '"' || c == ':' || c == ',' || c == '=' ||
                    (!patterns && (c == '*' || c == '?'))) return false;
            statusText = "00, OK,00,00";
            return true;
        }

        public sealed class RelativeFileData
        {
            public string Name { get; internal set; }
            public int RecordLength { get; internal set; }
            public byte[] Bytes { get; internal set; }
            public bool Locked { get; internal set; }
            public int RecordCount => Bytes.Length / RecordLength;
        }

        /// <summary>True for an explicit L/REL specification or a matching existing REL.</summary>
        public bool IsRelativeSpecification(string specification)
        {
            string request = specification ?? string.Empty;
            int comma = request.IndexOf(',');
            if (comma >= 0)
            {
                string kind = request.Substring(comma + 1).Split(',')[0].Trim().ToUpperInvariant();
                return kind == "L" || kind == "REL";
            }
            request = request.TrimStart('@');
            if (!TryDosName(request, true, out string name, out _)) return false;
            foreach (var entry in _entries)
                if (entry.FileTypeName == "REL" && MatchesSequentialName(entry.NormalizedName, name)) return true;
            return false;
        }

        /// <summary>OPEN L takes a binary record-length byte, not a decimal field.</summary>
        public bool TryOpenRelativeFile(string specification, out RelativeFileData file, out string statusText)
        {
            file = null;
            string request = specification ?? string.Empty;
            if (request.StartsWith("@", StringComparison.Ordinal)) request = request.Substring(1);
            int comma = request.IndexOf(','); int length = 0;
            string rawName = comma < 0 ? request : request.Substring(0, comma);
            if (comma >= 0)
            {
                int next = request.IndexOf(',', comma + 1);
                string type = (next < 0 ? request.Substring(comma + 1) : request.Substring(comma + 1, next - comma - 1)).Trim().ToUpperInvariant();
                if (type != "L" && type != "REL")
                { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                if (next >= 0)
                {
                    if (request.Length != next + 2 || request[next + 1] < 1 || request[next + 1] > 254)
                    { statusText = "51, OVERFLOW IN RECORD,00,00"; return false; }
                    length = request[next + 1];
                }
            }
            if (!TryDosName(rawName, true, out string name, out statusText)) return false;
            if (!TryDirectoryLayout(_data, out var entries, out _, out statusText)) return false;
            foreach (int entry in entries)
            {
                if ((_data[entry + 2] & 7) == 0 || !MatchesSequentialName(EntryName(_data, entry), name)) continue;
                if ((_data[entry + 2] & 7) != 4 || (length != 0 && length != _data[entry + 23]))
                { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                if ((_data[entry + 2] & 0x80) == 0)
                { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                if (!TryCollectFile(_data, entry, out byte[] bytes, out _, out statusText)) return false;
                file = new RelativeFileData { Name = EntryName(_data, entry), RecordLength = _data[entry + 23],
                    Bytes = bytes, Locked = (_data[entry + 2] & 0x40) != 0 };
                return true;
            }
            if (length == 0) { statusText = "62, FILE NOT FOUND,00,00"; return false; }
            if (!TryDosName(name, false, out name, out statusText)) return false;
            byte[] empty = new byte[length]; empty[0] = 0xFF;
            if (!StoreDosFile(name, 4, length, empty, false, out statusText)) return false;
            file = new RelativeFileData { Name = name, RecordLength = length, Bytes = empty };
            return true;
        }

        /// <summary>Writes fixed-length records and real REL side-sector index blocks atomically.</summary>
        public bool TryWriteRelativeFile(string name, int recordLength, byte[] bytes, out string statusText)
        {
            if (!TryDosName(name, false, out name, out statusText)) return false;
            if (recordLength < 1 || recordLength > 254 || bytes == null || bytes.Length == 0 || bytes.Length % recordLength != 0)
            { statusText = "51, OVERFLOW IN RECORD,00,00"; return false; }
            return StoreDosFile(name, 4, recordLength, bytes, true, out statusText);
        }

        private bool StoreDosFile(string name, int type, int recordLength, byte[] bytes,
            bool replace, out string statusText)
        {
            if (!CanPersist(out statusText)) return false;
            if (type == 4 && ((long)bytes.Length > 720 * 254 || bytes.Length / recordLength > 65535))
            { statusText = "52, FILE TOO LARGE,00,00"; return false; }
            if (bytes.Length > 664 * 254) { statusText = "72, DISK FULL,00,00"; return false; }
            byte[] work = (byte[])_data.Clone();
            if (!TryDirectoryLayout(work, out var entries, out var directories, out statusText) ||
                !TryOwnership(work, entries, directories, out var chains, out var occupied, out statusText)) return false;
            int target = -1, freeEntry = -1;
            foreach (int entry in entries)
            {
                if ((work[entry + 2] & 7) == 0) { if (freeEntry < 0) freeEntry = entry; continue; }
                if (EntryName(work, entry) == name) { target = entry; break; }
            }
            if (target >= 0)
            {
                if (!replace) { statusText = "63, FILE EXISTS,00,00"; return false; }
                if ((work[target + 2] & 7) != type || (type == 4 && work[target + 23] != recordLength))
                { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                if ((work[target + 2] & 0x80) == 0)
                { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                if ((work[target + 2] & 0x40) != 0)
                { statusText = "26, WRITE PROTECT ON,00,00"; return false; }
                foreach (var s in chains[target])
                { BamMark(work, s.Track, s.Sector, true); occupied.Remove((s.Track << 8) | s.Sector); }
            }
            else
            {
                if (replace) { statusText = "62, FILE NOT FOUND,00,00"; return false; }
                target = freeEntry;
                if (target < 0)
                {
                    int next = -1;
                    for (int s = 2; s < GetSectorsPerTrack(18); s++)
                        if (BamIsFree(work, 18, s) && !occupied.Contains((18 << 8) | s)) { next = s; break; }
                    if (next < 0) { statusText = "72, DISK FULL,00,00"; return false; }
                    TryGetSectorOffset(18, next, out target);
                    var last = directories[directories.Count - 1];
                    TryGetSectorOffset(last.Track, last.Sector, out int previous);
                    work[previous] = 18; work[previous + 1] = (byte)next;
                    Array.Clear(work, target, 256); work[target + 1] = 255;
                    BamMark(work, 18, next, false); occupied.Add((18 << 8) | next);
                }
            }
            int dataCount = Math.Max(1, (bytes.Length + 253) / 254);
            int sideCount = type == 4 ? (dataCount + 119) / 120 : 0;
            int required = dataCount + sideCount;
            var blocks = new List<(int Track, int Sector)>();
            for (int pass = 0; pass < 2 && blocks.Count < required; pass++)
            {
                int begin = pass == 0 ? 17 : 19, end = pass == 0 ? 1 : 35;
                for (int track = begin; pass == 0 ? track >= end : track <= end; track += pass == 0 ? -1 : 1)
                {
                    for (int sector = 0; sector < GetSectorsPerTrack(track) && blocks.Count < required; sector++)
                    {
                        if (!BamIsFree(work, track, sector) || occupied.Contains((track << 8) | sector)) continue;
                        blocks.Add((track, sector)); BamMark(work, track, sector, false);
                    }
                    if (blocks.Count == required) break;
                }
            }
            if (blocks.Count != required) { statusText = "72, DISK FULL,00,00"; return false; }
            int copied = 0;
            for (int i = 0; i < dataCount; i++)
            {
                TryGetSectorOffset(blocks[i].Track, blocks[i].Sector, out int at);
                Array.Clear(work, at, 256);
                int take = Math.Min(254, bytes.Length - copied);
                if (i + 1 < dataCount) { work[at] = (byte)blocks[i + 1].Track; work[at + 1] = (byte)blocks[i + 1].Sector; }
                else work[at + 1] = (byte)(take + 1);
                Array.Copy(bytes, copied, work, at + 2, take); copied += take;
            }
            for (int i = 0; i < sideCount; i++)
            {
                TryGetSectorOffset(blocks[dataCount + i].Track, blocks[dataCount + i].Sector, out int at);
                Array.Clear(work, at, 256);
                int count = Math.Min(120, dataCount - i * 120);
                if (i + 1 < sideCount)
                { work[at] = (byte)blocks[dataCount + i + 1].Track; work[at + 1] = (byte)blocks[dataCount + i + 1].Sector; }
                else work[at + 1] = (byte)(15 + count * 2);
                work[at + 2] = (byte)i; work[at + 3] = (byte)recordLength;
                for (int s = 0; s < sideCount; s++)
                { work[at + 4 + s * 2] = (byte)blocks[dataCount + s].Track; work[at + 5 + s * 2] = (byte)blocks[dataCount + s].Sector; }
                for (int d = 0; d < count; d++)
                { work[at + 16 + d * 2] = (byte)blocks[i * 120 + d].Track; work[at + 17 + d * 2] = (byte)blocks[i * 120 + d].Sector; }
            }
            Array.Clear(work, target + 2, 30);
            work[target + 2] = (byte)(0x80 | type);
            work[target + 3] = (byte)blocks[0].Track; work[target + 4] = (byte)blocks[0].Sector;
            for (int i = 0; i < 16; i++) work[target + 5 + i] = 0xA0;
            Encoding.ASCII.GetBytes(name).CopyTo(work, target + 5);
            if (type == 4)
            {
                work[target + 21] = (byte)blocks[dataCount].Track; work[target + 22] = (byte)blocks[dataCount].Sector;
                work[target + 23] = (byte)recordLength;
            }
            work[target + 30] = (byte)required; work[target + 31] = (byte)(required >> 8);
            return CommitCandidate(work, out statusText);
        }

        public bool TryRenameFile(string destination, string source, out string statusText)
        {
            if (!CanPersist(out statusText) || !TryDosName(destination, false, out string name, out statusText) ||
                !TryDosName(source, false, out string old, out statusText)) return false;
            byte[] work = (byte[])_data.Clone();
            if (!TryDirectoryLayout(work, out var entries, out _, out statusText)) return false;
            int target = -1;
            foreach (int entry in entries)
            {
                if ((work[entry + 2] & 7) == 0) continue;
                if (EntryName(work, entry) == name) { statusText = "63, FILE EXISTS,00,00"; return false; }
                if (EntryName(work, entry) == old) target = entry;
            }
            if (target < 0) { statusText = "62, FILE NOT FOUND,00,00"; return false; }
            if ((work[target + 2] & 0x80) == 0) { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
            for (int i = 0; i < 16; i++) work[target + 5 + i] = 0xA0;
            Encoding.ASCII.GetBytes(name).CopyTo(work, target + 5);
            return CommitCandidate(work, out statusText);
        }

        public bool TryScratchFiles(string[] specifications, out int scratched, out string statusText)
        {
            scratched = 0;
            if (!CanPersist(out statusText)) return false;
            if (specifications == null || specifications.Length == 0)
            { statusText = "34, SYNTAX ERROR,00,00"; return false; }
            var patterns = new List<(string Name, int Type)>();
            foreach (string item in specifications)
            {
                string text = item; int type = 0, equals = text.IndexOf('=');
                if (equals >= 0)
                {
                    string qualifier = text.Substring(equals + 1).Trim().ToUpperInvariant();
                    type = qualifier switch { "S" or "SEQ" => 1, "P" or "PRG" => 2, "U" or "USR" => 3, "L" or "REL" => 4, _ => -1 };
                    if (type < 0) { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                    text = text.Substring(0, equals);
                }
                if (!TryDosName(text, true, out string pattern, out statusText)) return false;
                patterns.Add((pattern, type));
            }
            byte[] work = (byte[])_data.Clone();
            if (!TryDirectoryLayout(work, out var entries, out var directory, out statusText) ||
                !TryOwnership(work, entries, directory, out var chains, out _, out statusText)) return false;
            foreach (int entry in entries)
            {
                int type = work[entry + 2] & 7;
                if (type == 0 || (work[entry + 2] & 0x40) != 0) continue;
                bool match = false;
                foreach (var pattern in patterns)
                    if ((pattern.Type == 0 || pattern.Type == type) && MatchesSequentialName(EntryName(work, entry), pattern.Name)) { match = true; break; }
                if (!match) continue;
                if (!chains.TryGetValue(entry, out var sectors))
                { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                foreach (var s in sectors) BamMark(work, s.Track, s.Sector, true);
                work[entry + 2] = 0; scratched++;
            }
            if (scratched > 0 && !CommitCandidate(work, out statusText)) { scratched = 0; return false; }
            statusText = $"01, FILES SCRATCHED,{scratched:00},00";
            return true;
        }

        public bool TryCopyFiles(string destination, string[] sources, out string statusText)
        {
            if (!CanPersist(out statusText) || !TryDosName(destination, false, out string name, out statusText)) return false;
            if (sources == null || sources.Length < 1 || sources.Length > 4)
            { statusText = "30, SYNTAX ERROR,00,00"; return false; }
            if (!TryDirectoryLayout(_data, out var entries, out _, out statusText)) return false;
            int type = 0, length = 0; var bytes = new List<byte>();
            foreach (string source in sources)
            {
                if (!TryDosName(source, true, out string old, out statusText)) return false;
                int found = -1;
                foreach (int entry in entries)
                    if ((_data[entry + 2] & 7) != 0 && MatchesSequentialName(EntryName(_data, entry), old)) { found = entry; break; }
                if (found < 0) { statusText = "62, FILE NOT FOUND,00,00"; return false; }
                if ((_data[found + 2] & 0x80) == 0) { statusText = "60, WRITE FILE OPEN,00,00"; return false; }
                int sourceType = _data[found + 2] & 7, sourceLength = sourceType == 4 ? _data[found + 23] : 0;
                if (type != 0 && (type != sourceType || length != sourceLength))
                { statusText = "64, FILE TYPE MISMATCH,00,00"; return false; }
                type = sourceType; length = sourceLength;
                if (!TryCollectFile(_data, found, out byte[] part, out _, out statusText)) return false;
                bytes.AddRange(part);
            }
            return StoreDosFile(name, type, length, bytes.ToArray(), false, out statusText);
        }

        public bool TryFormat(string diskName, string diskId, out string statusText)
        {
            if (!CanPersist(out statusText) || !TryDosName(diskName, false, out string name, out statusText)) return false;
            if (diskId != null && (diskId.Length != 2 || diskId[0] < 0x20 || diskId[0] > 0x7E || diskId[1] < 0x20 || diskId[1] > 0x7E))
            { statusText = "33, SYNTAX ERROR,00,00"; return false; }
            bool full = diskId != null;
            byte[] work = (byte[])_data.Clone();
            if (full) Array.Clear(work, 0, 683 * 256);
            TryGetSectorOffset(18, 0, out int bam); TryGetSectorOffset(18, 1, out int directory);
            string id = diskId ?? DiskId;
            if (id.Length < 2) id = id.PadRight(2, '0');
            Array.Clear(work, bam, 256); Array.Clear(work, directory, 256);
            work[bam] = 18; work[bam + 1] = 1; work[bam + 2] = 0x41; work[directory + 1] = 0xFF;
            for (int track = 1; track <= 35; track++)
                for (int sector = 0; sector < GetSectorsPerTrack(track); sector++) BamMark(work, track, sector, true);
            BamMark(work, 18, 0, false); BamMark(work, 18, 1, false);
            for (int i = 0x90; i <= 0xAA; i++) work[bam + i] = 0xA0;
            Encoding.ASCII.GetBytes(name).CopyTo(work, bam + 0x90);
            work[bam + 0xA2] = (byte)id[0]; work[bam + 0xA3] = (byte)id[1];
            work[bam + 0xA5] = (byte)'2'; work[bam + 0xA6] = (byte)'A';
            byte[] errors = _errorInfo == null ? null : (byte[])_errorInfo.Clone();
            if (full && errors != null) Array.Fill(errors, (byte)1, 0, 683);
            return CommitCandidate(work, out statusText, errors);
        }

        public bool TryValidate(out string statusText)
        {
            if (!CanPersist(out statusText)) return false;
            byte[] work = (byte[])_data.Clone();
            if (!TryDirectoryLayout(work, out var entries, out var directory, out statusText) ||
                !TryOwnership(work, entries, directory, out var chains, out var occupied, out statusText)) return false;
            TryGetSectorOffset(18, 0, out int bam);
            Array.Clear(work, bam + 4, 35 * 4);
            for (int track = 1; track <= 35; track++)
                for (int sector = 0; sector < GetSectorsPerTrack(track); sector++)
                    if (!occupied.Contains((track << 8) | sector)) BamMark(work, track, sector, true);
            foreach (int entry in entries)
            {
                if ((work[entry + 2] & 7) == 0) continue;
                if ((work[entry + 2] & 0x80) == 0) { work[entry + 2] = 0; continue; }
                int count = chains[entry].Count; work[entry + 30] = (byte)count; work[entry + 31] = (byte)(count >> 8);
            }
            return CommitCandidate(work, out statusText);
        }

        public bool TryWriteSectorPersistent(int track, int sector, byte[] bytes, out string statusText)
        {
            if (!CanPersist(out statusText)) return false;
            if (bytes == null || bytes.Length != 256 || !TryGetSectorOffset(track, sector, out int at))
            { statusText = $"66, ILLEGAL TRACK OR SECTOR,{track:00},{sector:00}"; return false; }
            byte[] work = (byte[])_data.Clone(); Array.Copy(bytes, 0, work, at, 256);
            return CommitCandidate(work, out statusText);
        }

        public bool TryChangeAllocation(int track, int sector, bool allocate, out string statusText)
        {
            if (!CanPersist(out statusText)) return false;
            if (track < 1 || track > 35 || !TryGetSectorOffset(track, sector, out _))
            { statusText = $"66, ILLEGAL TRACK OR SECTOR,{track:00},{sector:00}"; return false; }
            if (allocate && !BamIsFree(_data, track, sector))
            {
                int nextTrack = 0, nextSector = 0;
                for (int t = track; t <= 35 && nextTrack == 0; t++)
                    for (int s = t == track ? sector + 1 : 0; s < GetSectorsPerTrack(t); s++)
                        if (BamIsFree(_data, t, s)) { nextTrack = t; nextSector = s; break; }
                statusText = $"65, NO BLOCK,{nextTrack:00},{nextSector:00}"; return false;
            }
            byte[] work = (byte[])_data.Clone(); BamMark(work, track, sector, !allocate);
            return CommitCandidate(work, out statusText);
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
            private int _lastReadTrack;
            private int _lastReadSector;
            private int _previousReadTrack;
            private int _previousReadSector;
            private bool _lastReadSectorIsFinal;
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
                _lastReadTrack = 0;
                _lastReadSector = 0;
                _previousReadTrack = 0;
                _previousReadSector = 0;
                _lastReadSectorIsFinal = false;
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

                        _previousReadTrack = _lastReadTrack;
                        _previousReadSector = _lastReadSector;
                        _lastReadTrack = _track;
                        _lastReadSector = _sector;
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

                        _lastReadSectorIsFinal = nextTrack == 0;
                        _lastSectorLength = _lastReadSectorIsFinal ? Math.Max(0, Math.Min(254, nextSector - 1)) : 254;
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
                _lastReadSectorIsFinal = false;
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

            public int LastReadTrack
            {
                get { return _lastReadTrack; }
            }

            public int LastReadSector
            {
                get { return _lastReadSector; }
            }

            public int DosContextTrack
            {
                get
                {
                    return _lastReadSectorIsFinal && _previousReadTrack > 0
                        ? _previousReadTrack
                        : _lastReadTrack;
                }
            }

            public int DosContextSector
            {
                get
                {
                    if (_lastReadSectorIsFinal &&
                        _previousReadTrack > 0 &&
                        _image.TryGetNextPhysicalSector(_previousReadTrack, _previousReadSector, out int nextSector))
                    {
                        return nextSector;
                    }

                    return _lastReadSector;
                }
            }

            /// <summary>
            /// Writes the current sequential reader position into a savestate stream.
            /// </summary>
            public void SaveState(BinaryWriter writer)
            {
                BinaryStateIO.WriteIntSet(writer, _visitedSectors);
                writer.Write(_track);
                writer.Write(_sector);
                BinaryStateIO.WriteByteArray(writer, _sectorBytes);
                writer.Write(_index);
                writer.Write(_lastSectorLength);
                writer.Write(_finished);
                writer.Write(_bytesRead);
                writer.Write(_aborted);
            }

            /// <summary>
            /// Restores a sequential reader position from a savestate stream.
            /// </summary>
            public static SequentialFileReader LoadState(D64Image image, BinaryReader reader)
            {
                var result = new SequentialFileReader(image, 0, 0);
                HashSet<int> visitedSectors = BinaryStateIO.ReadIntSet(reader);
                result._visitedSectors.Clear();
                if (visitedSectors != null)
                {
                    foreach (int sectorKey in visitedSectors)
                    {
                        result._visitedSectors.Add(sectorKey);
                    }
                }

                result._track = reader.ReadInt32();
                result._sector = reader.ReadInt32();
                result._sectorBytes = BinaryStateIO.ReadByteArray(reader);
                result._index = reader.ReadInt32();
                result._lastSectorLength = reader.ReadInt32();
                result._finished = reader.ReadBoolean();
                result._bytesRead = reader.ReadInt32();
                result._aborted = reader.ReadBoolean();
                return result;
            }

            /// <summary>
            /// Skips a serialized sequential reader state.
            /// </summary>
            public static void SkipState(BinaryReader reader)
            {
                BinaryStateIO.ReadIntSet(reader);
                reader.ReadInt32();
                reader.ReadInt32();
                BinaryStateIO.ReadByteArray(reader);
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadBoolean();
                reader.ReadInt32();
                reader.ReadBoolean();
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

            foreach (int sector in BuildFormattedSectorOrder(sectorsPerTrack))
            {
                TryGetSectorErrorCode(track, sector, out byte errorCode);

                if (errorCode == 0x02 || errorCode == 0x03 || errorCode == 0x0F)
                {
                    AddFill(stream, 10, 0x55);
                }
                else
                {
                    AddSync(stream, HeaderSyncBytes);
                    AddGcrEncoded(stream, BuildHeaderBlock(track, sector, diskId1, diskId2, errorCode));
                }

                AddFill(stream, HeaderGapBytes, 0x55);

                if (errorCode == 0x03 || errorCode == 0x0F)
                {
                    AddFill(stream, 10, 0x55);
                }
                else
                {
                    AddSync(stream, DataSyncBytes);
                    AddGcrEncoded(stream, BuildDataBlock(track, sector, errorCode));
                }

                AddFill(stream, postDataGapSize, 0x55);
            }

            int trackSizeBytes = GetTrackSizeBytes(track);
            while (stream.Count < trackSizeBytes)
            {
                stream.Add(0x55);
            }

            return RotateTrackStream(stream.ToArray(), GetD64TrackSkewOffsetBytes(track));
        }

        /// <summary>
        /// Builds the sector order of a normally formatted 1541 track.
        /// </summary>
        private static IEnumerable<int> BuildFormattedSectorOrder(int sectorsPerTrack)
        {
            bool[] used = new bool[sectorsPerTrack];
            int sector = 0;
            for (int count = 0; count < sectorsPerTrack; count++)
            {
                while (used[sector])
                {
                    sector = (sector + 1) % sectorsPerTrack;
                }

                used[sector] = true;
                yield return sector;
                sector = (sector + 10) % sectorsPerTrack;
            }
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
        /// Rotates a synthesized D64 track to mimic the missing angular track alignment of a DOS-formatted disk.
        /// </summary>
        private static byte[] RotateTrackStream(byte[] trackBytes, int offset)
        {
            if (trackBytes == null || trackBytes.Length == 0)
            {
                return trackBytes;
            }

            offset %= trackBytes.Length;
            if (offset <= 0)
            {
                return trackBytes;
            }

            byte[] rotated = new byte[trackBytes.Length];
            Array.Copy(trackBytes, 0, rotated, offset, trackBytes.Length - offset);
            Array.Copy(trackBytes, trackBytes.Length - offset, rotated, 0, offset);
            return rotated;
        }

        /// <summary>
        /// Gets the synthetic per-track angular offset for a D64-derived GCR stream.
        /// </summary>
        private static int GetD64TrackSkewOffsetBytes(int targetTrack)
        {
            long trackOffset = 0;
            for (int track = 1; track <= targetTrack; track++)
            {
                int trackSizeBytes = GetTrackSizeBytes(track);
                trackOffset += GetNominalWrittenTrackBytes(track) - GetPostDataGapSize(track);
                trackOffset += (trackSizeBytes * TrackStepSkewNumerator) / TrackStepSkewDenominator;
                trackOffset %= trackSizeBytes;
            }

            return (int)trackOffset;
        }

        /// <summary>
        /// Gets the number of bytes written by a normal formatted track before the remaining tail gap.
        /// </summary>
        private static int GetNominalWrittenTrackBytes(int track)
        {
            return GetSectorsPerTrack(track) *
                (HeaderSyncBytes + EncodedHeaderBlockBytes + HeaderGapBytes + DataSyncBytes + EncodedDataBlockBytes + GetPostDataGapSize(track));
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
