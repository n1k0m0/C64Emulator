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
    /// Migrates flat savestate folders into per-game subdirectories.
    /// </summary>
    internal static class SaveStateMigration
    {
        /// <summary>
        /// Migrates top-level savestates in the given save directory.
        /// </summary>
        public static int MigrateFlatSaves(string saveDirectory, TextWriter log)
        {
            if (log == null)
            {
                log = TextWriter.Null;
            }

            saveDirectory = Path.GetFullPath(saveDirectory);
            Directory.CreateDirectory(saveDirectory);

            string[] files = Directory.GetFiles(saveDirectory, SaveStateFile.SearchPattern, SearchOption.AllDirectories);
            int moved = 0;
            log.WriteLine("SAVE MIGRATION");
            log.WriteLine("Directory=" + saveDirectory);
            log.WriteLine("Found=" + files.Length);

            foreach (string file in files)
            {
                try
                {
                    MountedMediaInfo mediaInfo = ResolveMediaInfo(file, log);
                    string targetDirectory = SaveStateFile.GetSaveDirectoryForMedia(saveDirectory, mediaInfo);
                    Directory.CreateDirectory(targetDirectory);

                    string preferredTargetPath = Path.Combine(targetDirectory, Path.GetFileName(file));
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(preferredTargetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        log.WriteLine("SKIP " + file);
                        continue;
                    }

                    string targetPath = GetAvailableTargetPath(preferredTargetPath);
                    File.Move(file, targetPath);
                    moved++;
                    log.WriteLine("MOVE " + file + " -> " + targetPath);
                }
                catch (Exception ex)
                {
                    log.WriteLine("FAILED " + file);
                    log.WriteLine(ex.Message);
                }
            }

            log.WriteLine("Moved=" + moved);
            RemoveEmptyDirectories(saveDirectory, log);
            return moved;
        }

        /// <summary>
        /// Resolves mounted media information from quick metadata or from a full state load.
        /// </summary>
        private static MountedMediaInfo ResolveMediaInfo(string savePath, TextWriter log)
        {
            SaveStateMetadata metadata = SaveStateFile.ReadMetadata(savePath);
            if (!string.IsNullOrWhiteSpace(metadata.MediaDisplayName) || !string.IsNullOrWhiteSpace(metadata.MediaHostPath))
            {
                return new MountedMediaInfo(metadata.MediaKind, metadata.MediaShortLabel, metadata.MediaDisplayName, metadata.MediaHostPath);
            }

            try
            {
                using (var system = new C64System(C64Model.Pal))
                {
                    SaveStateFile.Load(savePath, system);
                    return system.MountedMedia ?? MountedMediaInfo.None;
                }
            }
            catch (Exception ex)
            {
                log.WriteLine("WARN could not inspect mounted media for " + savePath + ": " + ex.Message);
                return MountedMediaInfo.None;
            }
        }

        /// <summary>
        /// Returns a target path that does not overwrite an existing save.
        /// </summary>
        private static string GetAvailableTargetPath(string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                return targetPath;
            }

            string directory = Path.GetDirectoryName(targetPath);
            string name = Path.GetFileNameWithoutExtension(targetPath);
            string extension = Path.GetExtension(targetPath);
            int suffix = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, name + "-" + suffix + extension);
                suffix++;
            }
            while (File.Exists(candidate));

            return candidate;
        }

        /// <summary>
        /// Removes empty directories left behind by a migration pass.
        /// </summary>
        private static void RemoveEmptyDirectories(string saveDirectory, TextWriter log)
        {
            string[] directories = Directory.GetDirectories(saveDirectory, "*", SearchOption.AllDirectories);
            Array.Sort(directories, (left, right) => right.Length.CompareTo(left.Length));
            foreach (string directory in directories)
            {
                try
                {
                    if (Directory.GetFileSystemEntries(directory).Length == 0)
                    {
                        Directory.Delete(directory);
                        log.WriteLine("RMDIR " + directory);
                    }
                }
                catch (Exception ex)
                {
                    log.WriteLine("WARN could not remove directory " + directory + ": " + ex.Message);
                }
            }
        }
    }
}
