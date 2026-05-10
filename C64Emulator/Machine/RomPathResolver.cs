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
    /// Resolves required ROM files from stable application and workspace locations.
    /// </summary>
    public static class RomPathResolver
    {
        /// <summary>
        /// Resolves a required ROM file and throws a diagnostic exception when it is missing.
        /// </summary>
        public static string ResolveRequired(string fileName, string explicitBasePath = null)
        {
            string path = FindFirstExisting(new[] { fileName }, explicitBasePath);
            if (path != null)
            {
                return path;
            }

            throw new FileNotFoundException(BuildMissingRomMessage(fileName, explicitBasePath));
        }

        /// <summary>
        /// Resolves the first available ROM file from a set of compatible names.
        /// </summary>
        public static string FindFirstExisting(IEnumerable<string> fileNames, string explicitBasePath = null)
        {
            foreach (string directory in GetSearchDirectories(explicitBasePath))
            {
                foreach (string fileName in fileNames)
                {
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Builds a human-readable ROM status report for diagnostics and CLI checks.
        /// </summary>
        public static string BuildStatusReport(string explicitBasePath = null)
        {
            var builder = new StringBuilder();
            builder.AppendLine("ROM STATUS");
            AppendRomStatus(builder, "C64 BASIC/KERNAL", new[] { "c64-basic-kernal.bin" }, explicitBasePath);
            AppendRomStatus(builder, "C64 CHARACTER", new[] { "c64-character.bin" }, explicitBasePath);
            AppendRomStatus(builder, "1541 LOWER", new[] { "1541-c000-rom.bin", "1541-c000.325302-01.bin" }, explicitBasePath);
            AppendRomStatus(builder, "1541 UPPER", new[]
            {
                "1541-e000-rom.bin",
                "1541-e000.901229-01.bin",
                "1541-e000.901229-05.bin",
                "1541-e000.901229-03.bin",
                "1540-e000.325303-01.bin"
            }, explicitBasePath);

            builder.AppendLine("Search paths:");
            foreach (string directory in GetSearchDirectories(explicitBasePath))
            {
                builder.AppendLine("  " + directory);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Returns true when all ROMs required for a full C64 and 1541 setup are available.
        /// </summary>
        public static bool HasCompleteRomSet(string explicitBasePath = null)
        {
            return FindFirstExisting(new[] { "c64-basic-kernal.bin" }, explicitBasePath) != null &&
                FindFirstExisting(new[] { "c64-character.bin" }, explicitBasePath) != null &&
                FindFirstExisting(new[] { "1541-c000-rom.bin", "1541-c000.325302-01.bin" }, explicitBasePath) != null &&
                FindFirstExisting(new[]
                {
                    "1541-e000-rom.bin",
                    "1541-e000.901229-01.bin",
                    "1541-e000.901229-05.bin",
                    "1541-e000.901229-03.bin",
                    "1540-e000.325303-01.bin"
                }, explicitBasePath) != null;
        }

        /// <summary>
        /// Gets stable ROM search directories in priority order.
        /// </summary>
        public static IReadOnlyList<string> GetSearchDirectories(string explicitBasePath = null)
        {
            var directories = new List<string>();
            AddDirectory(directories, explicitBasePath);
            AddDirectory(directories, GetUserRomDirectory(), false);
            AddDirectory(directories, AppDomain.CurrentDomain.BaseDirectory);
            AddDirectory(directories, Directory.GetCurrentDirectory());
            AddDirectory(directories, Path.Combine(Directory.GetCurrentDirectory(), "C64Emulator"));
            AddDirectory(directories, Path.GetDirectoryName(Directory.GetCurrentDirectory()));
            AddDirectory(directories, Path.Combine(Path.GetDirectoryName(Directory.GetCurrentDirectory()) ?? string.Empty, "C64Emulator"));
            return directories;
        }

        /// <summary>
        /// Gets the per-user ROM directory used by the GUI downloader.
        /// </summary>
        public static string GetUserRomDirectory()
        {
            return UserDataPaths.GetRomDirectory();
        }

        private static void AppendRomStatus(StringBuilder builder, string label, IEnumerable<string> fileNames, string explicitBasePath)
        {
            string path = FindFirstExisting(fileNames, explicitBasePath);
            builder.Append(label);
            builder.Append(": ");
            builder.AppendLine(path == null ? "MISSING" : path);
        }

        private static string BuildMissingRomMessage(string fileName, string explicitBasePath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Required C64 ROM file is missing: " + fileName);
            builder.AppendLine("Search paths:");
            foreach (string directory in GetSearchDirectories(explicitBasePath))
            {
                builder.AppendLine("  " + directory);
            }

            builder.AppendLine("Run with --check-roms to print the complete ROM status.");
            return builder.ToString();
        }

        private static void AddDirectory(List<string> directories, string directory, bool requireExistingDirectory = true)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch
            {
                return;
            }

            if (requireExistingDirectory && !Directory.Exists(fullPath))
            {
                return;
            }

            for (int index = 0; index < directories.Count; index++)
            {
                if (string.Equals(directories[index], fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            directories.Add(fullPath);
        }
    }
}
