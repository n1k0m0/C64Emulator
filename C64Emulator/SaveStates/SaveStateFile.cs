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
        private const int Version = 1;
        private const string Extension = ".c64sav";

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
                ValidateHeader(reader);
                long ticksUtc = reader.ReadInt64();
                int screenshotWidth = reader.ReadInt32();
                int screenshotHeight = reader.ReadInt32();
                uint[] pixels = BinaryStateIO.ReadUIntArray(reader);

                return new SaveStateMetadata
                {
                    Path = path,
                    CreatedLocalTime = new DateTime(ticksUtc, DateTimeKind.Utc).ToLocalTime(),
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
                ValidateHeader(reader);
                reader.ReadInt64();
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
        private static void ValidateHeader(BinaryReader reader)
        {
            string magic = reader.ReadString();
            int version = reader.ReadInt32();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal) || version != Version)
            {
                throw new InvalidDataException("Unsupported savestate format.");
            }
        }
    }
}
