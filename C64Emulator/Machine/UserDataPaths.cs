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
    /// Provides per-user application data paths for emulator-owned files.
    /// </summary>
    public static class UserDataPaths
    {
        /// <summary>
        /// Gets the per-user emulator data directory.
        /// </summary>
        public static string GetBaseDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = AppDomain.CurrentDomain.BaseDirectory;
            }

            return Path.Combine(appData, "C64Emulator");
        }

        /// <summary>
        /// Gets the per-user ROM directory used by the GUI downloader.
        /// </summary>
        public static string GetRomDirectory()
        {
            return Path.Combine(GetBaseDirectory(), "roms");
        }

        /// <summary>
        /// Gets the per-user savestate directory.
        /// </summary>
        public static string GetSaveDirectory()
        {
            return Path.Combine(GetBaseDirectory(), "saves");
        }

        /// <summary>
        /// Gets the per-user media directory for user-owned PRG and D64 files.
        /// </summary>
        public static string GetMediaDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = GetBaseDirectory();
            }

            return Path.Combine(documents, "C64Emulator");
        }

        /// <summary>
        /// Gets the per-user settings file path.
        /// </summary>
        public static string GetSettingsPath()
        {
            return Path.Combine(GetBaseDirectory(), "settings.json");
        }
    }
}
