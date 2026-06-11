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
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace C64Emulator.Core
{
    /// <summary>
    /// Contains persistent user settings for the emulator frontend.
    /// </summary>
    public sealed class EmulatorSettings
    {
        public int Version { get; set; } = 1;

        public float SidMasterVolume { get; set; } = 1.0f;

        public float SidNoiseLevel { get; set; } = 0.55f;

        public string SidChipModel { get; set; } = "Mos6581";

        public string JoystickPort { get; set; } = "Port2";

        public string HostKeyboardLayout { get; set; } = "En";

        public string VideoFilterMode { get; set; } = "Sharp";

        public bool VideoZoomEnabled { get; set; }

        public string ResetMode { get; set; } = "Warm";

        public bool TurboMode { get; set; }

        public bool Fullscreen { get; set; }

        public bool GamepadEnabled { get; set; } = true;

        public bool EnableLoadHack { get; set; }

        public bool ForceSoftwareIecTransport { get; set; } = true;

        public bool EnableInputInjection { get; set; }

        public bool DriveOverlayEnabled { get; set; } = true;

        public bool EasyFlashEnabled { get; set; } = true;

        public string EasyFlashImagePath { get; set; } = string.Empty;

        public bool ReuEnabled { get; set; }

        public string ReuSize { get; set; } = "K512";

        public int MediaBrowserTargetDrive { get; set; } = 8;

        public string MediaBrowserDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the network transport mode shown in the network menu.
        /// </summary>
        public string NetworkTransportMode { get; set; } = "Lan";

        /// <summary>
        /// Gets or sets the TCP port shown in the server section of the network menu.
        /// </summary>
        public int NetworkServerPort { get; set; } = 6464;

        /// <summary>
        /// Gets or sets the optional server password remembered for the network menu.
        /// </summary>
        public string NetworkServerPassword { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the host name/IP shown in the client section of the network menu.
        /// </summary>
        public string NetworkClientHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the TCP port used when joining a remote session.
        /// </summary>
        public int NetworkClientPort { get; set; } = 6464;

        /// <summary>
        /// Gets or sets the TCP/TLS port used when joining via a relay server.
        /// </summary>
        public int NetworkRelayPort { get; set; } = 6465;

        /// <summary>
        /// Gets or sets the relay connection id used to match server and clients.
        /// </summary>
        public string NetworkConnectionId { get; set; } = "c64";

        /// <summary>
        /// Gets or sets the optional client password sent during the C64Net handshake.
        /// </summary>
        public string NetworkClientPassword { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the player name announced to the host.
        /// </summary>
        public string NetworkPlayerName { get; set; } = "player";

        /// <summary>
        /// Gets or sets the requested client role name persisted from the network menu.
        /// </summary>
        public string NetworkRequestedRole { get; set; } = "Player";
    }

    /// <summary>
    /// Loads and saves emulator settings from the per-user AppData directory.
    /// </summary>
    public static class EmulatorSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        /// <summary>
        /// Loads settings from disk or returns defaults when no valid settings file exists.
        /// </summary>
        public static EmulatorSettings Load()
        {
            string path = UserDataPaths.GetSettingsPath();
            try
            {
                if (!File.Exists(path))
                {
                    return new EmulatorSettings();
                }

                string json = File.ReadAllText(path);
                EmulatorSettings settings = JsonSerializer.Deserialize<EmulatorSettings>(json, JsonOptions);
                return settings ?? new EmulatorSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return new EmulatorSettings();
            }
        }

        /// <summary>
        /// Saves settings to disk.
        /// </summary>
        public static void Save(EmulatorSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            string path = UserDataPaths.GetSettingsPath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
        }
    }
}
