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
    /// Reads and writes complete C64 savestate files.
    /// </summary>
    internal static class SaveStateFile
    {
        private const string Magic = "C64EMU-SAVE";
        private const int Version = 2;
        private const string Extension = ".c64sav";
        private const string UnknownMediaDirectory = "Unknown";

        /// <summary>
        /// Creates a unique savestate path inside a directory.
        /// </summary>
        public static string CreateSavePath(string saveDirectory)
        {
            Directory.CreateDirectory(saveDirectory);
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = System.IO.Path.Combine(saveDirectory, "save-" + timestamp + Extension);
            int suffix = 2;
            while (File.Exists(path))
            {
                path = System.IO.Path.Combine(saveDirectory, "save-" + timestamp + "-" + suffix + Extension);
                suffix++;
            }

            return path;
        }

        /// <summary>
        /// Gets the savestate directory for a mounted media item.
        /// </summary>
        public static string GetSaveDirectoryForMedia(string baseSaveDirectory, MountedMediaInfo mediaInfo)
        {
            return System.IO.Path.Combine(baseSaveDirectory, GetMediaDirectoryName(mediaInfo));
        }

        /// <summary>
        /// Gets a filesystem-safe directory name for a mounted media item.
        /// </summary>
        public static string GetMediaDirectoryName(MountedMediaInfo mediaInfo)
        {
            string name = null;
            if (mediaInfo != null && mediaInfo.HasMedia)
            {
                if (!string.IsNullOrWhiteSpace(mediaInfo.HostPath))
                {
                    name = System.IO.Path.GetFileNameWithoutExtension(mediaInfo.HostPath);
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = mediaInfo.DisplayName;
                }
            }

            return SanitizeDirectoryName(name);
        }

        /// <summary>
        /// Writes a complete savestate with screenshot metadata.
        /// </summary>
        public static void Write(string path, C64System system, uint[] screenshotPixels, int screenshotWidth, int screenshotHeight)
        {
            if (system == null)
            {
                throw new ArgumentNullException(nameof(system));
            }

            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(DateTime.UtcNow.Ticks);
                WriteMountedMediaInfo(writer, system.MountedMedia);
                writer.Write(screenshotWidth);
                writer.Write(screenshotHeight);
                BinaryStateIO.WriteUIntArray(writer, screenshotPixels);
                system.SaveState(writer);
            }
        }

        /// <summary>
        /// Reads only menu metadata and the stored screenshot.
        /// </summary>
        public static SaveStateMetadata ReadMetadata(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                int version = ValidateHeader(reader);
                long ticksUtc = reader.ReadInt64();
                MountedMediaInfo mediaInfo = version >= 2 ? ReadMountedMediaInfo(reader) : MountedMediaInfo.None;
                int screenshotWidth = reader.ReadInt32();
                int screenshotHeight = reader.ReadInt32();
                uint[] pixels = BinaryStateIO.ReadUIntArray(reader);

                return new SaveStateMetadata
                {
                    Path = path,
                    CreatedLocalTime = new DateTime(ticksUtc, DateTimeKind.Utc).ToLocalTime(),
                    MediaKind = mediaInfo.Kind,
                    MediaShortLabel = mediaInfo.ShortLabel,
                    MediaDisplayName = mediaInfo.DisplayName,
                    MediaHostPath = mediaInfo.HostPath,
                    ScreenshotWidth = screenshotWidth,
                    ScreenshotHeight = screenshotHeight,
                    ScreenshotPixels = pixels
                };
            }
        }

        /// <summary>
        /// Loads a complete savestate into an existing system instance.
        /// </summary>
        public static void Load(string path, C64System system)
        {
            if (system == null)
            {
                throw new ArgumentNullException(nameof(system));
            }

            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                int version = ValidateHeader(reader);
                reader.ReadInt64();
                if (version >= 2)
                {
                    ReadMountedMediaInfo(reader);
                }

                reader.ReadInt32();
                reader.ReadInt32();
                BinaryStateIO.ReadUIntArray(reader);
                system.LoadState(reader);
            }
        }

        /// <summary>
        /// Gets the savestate search pattern.
        /// </summary>
        public static string SearchPattern
        {
            get { return "*" + Extension; }
        }

        /// <summary>
        /// Validates the file header.
        /// </summary>
        private static int ValidateHeader(BinaryReader reader)
        {
            string magic = reader.ReadString();
            int version = reader.ReadInt32();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal) || version < 1 || version > Version)
            {
                throw new InvalidDataException("Unsupported savestate format.");
            }

            return version;
        }

        /// <summary>
        /// Writes mounted media metadata.
        /// </summary>
        private static void WriteMountedMediaInfo(BinaryWriter writer, MountedMediaInfo mediaInfo)
        {
            mediaInfo = mediaInfo ?? MountedMediaInfo.None;
            writer.Write((int)mediaInfo.Kind);
            BinaryStateIO.WriteString(writer, mediaInfo.ShortLabel);
            BinaryStateIO.WriteString(writer, mediaInfo.DisplayName);
            BinaryStateIO.WriteString(writer, mediaInfo.HostPath);
        }

        /// <summary>
        /// Reads mounted media metadata.
        /// </summary>
        private static MountedMediaInfo ReadMountedMediaInfo(BinaryReader reader)
        {
            var kind = (MountedMediaKind)reader.ReadInt32();
            string shortLabel = BinaryStateIO.ReadString(reader) ?? "NONE";
            string displayName = BinaryStateIO.ReadString(reader) ?? string.Empty;
            string hostPath = BinaryStateIO.ReadString(reader) ?? string.Empty;
            return new MountedMediaInfo(kind, shortLabel, displayName, hostPath);
        }

        /// <summary>
        /// Sanitizes a mounted media display name for use as a directory name.
        /// </summary>
        private static string SanitizeDirectoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return UnknownMediaDirectory;
            }

            char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            for (int index = 0; index < name.Length; index++)
            {
                char value = name[index];
                bool invalid = false;
                for (int invalidIndex = 0; invalidIndex < invalidChars.Length; invalidIndex++)
                {
                    if (value == invalidChars[invalidIndex])
                    {
                        invalid = true;
                        break;
                    }
                }

                builder.Append(invalid || char.IsControl(value) ? '_' : value);
            }

            string sanitized = builder.ToString().Trim().Trim('.');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = UnknownMediaDirectory;
            }

            const int maximumLength = 80;
            if (sanitized.Length > maximumLength)
            {
                sanitized = sanitized.Substring(0, maximumLength).Trim().Trim('.');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? UnknownMediaDirectory : sanitized;
        }
    }
}
