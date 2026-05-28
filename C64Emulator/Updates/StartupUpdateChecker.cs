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
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace C64Emulator.Updates
{
    /// <summary>
    /// Checks GitHub Releases in the background and offers a newer installer.
    /// </summary>
    public static class StartupUpdateChecker
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/n1k0m0/C64Emulator/releases/latest";
        private const string UserAgent = "C64Emulator-UpdateChecker";
        private const int StartupDelayMilliseconds = 2500;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Starts the non-blocking startup update check.
        /// </summary>
        public static void CheckForUpdatesOnStartup()
        {
            Thread thread = new Thread(RunUpdateCheckThread);
            thread.IsBackground = true;
            thread.Name = "C64 update checker";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// Runs the async checker on an STA thread so WinForms dialogs are safe.
        /// </summary>
        private static void RunUpdateCheckThread()
        {
            try
            {
                Thread.Sleep(StartupDelayMilliseconds);
                CheckForUpdatesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Startup update checks must never prevent the emulator from running.
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Fetches release metadata, compares versions, and prompts for download.
        /// </summary>
        private static async Task CheckForUpdatesAsync()
        {
            GitHubReleaseInfo release = await FetchLatestReleaseAsync();
            if (release == null || release.Version == null || string.IsNullOrWhiteSpace(release.InstallerDownloadUrl))
            {
                return;
            }

            Version currentVersion = GetCurrentApplicationVersion();
            if (CompareVersions(release.Version, currentVersion) <= 0)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                "Eine neue Version des C64 Emulators ist verfuegbar.\r\n\r\n" +
                "Installiert: " + FormatVersion(currentVersion) + "\r\n" +
                "Verfuegbar: " + FormatVersion(release.Version) + "\r\n\r\n" +
                "Moechtest du das Setup jetzt herunterladen und starten?",
                "C64 Emulator Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                string installerPath = await DownloadInstallerAsync(release);
                if (string.IsNullOrWhiteSpace(installerPath))
                {
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show(
                    "Das Update konnte nicht heruntergeladen oder gestartet werden.\r\n\r\n" + ex.Message,
                    "C64 Emulator Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Downloads and parses the latest GitHub release metadata.
        /// </summary>
        private static async Task<GitHubReleaseInfo> FetchLatestReleaseAsync()
        {
            using (var httpClient = CreateHttpClient())
            using (HttpResponseMessage response = await httpClient.GetAsync(LatestReleaseApiUrl))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync();
                return ParseReleaseInfo(json);
            }
        }

        /// <summary>
        /// Parses the minimal fields needed from a GitHub release JSON response.
        /// </summary>
        private static GitHubReleaseInfo ParseReleaseInfo(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                string tagName = ReadString(root, "tag_name");
                Version version = ParseVersion(tagName);
                string htmlUrl = ReadString(root, "html_url");
                string installerName = string.Empty;
                string installerDownloadUrl = string.Empty;

                if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string name = ReadString(asset, "name");
                        if (!IsWindowsSetupAsset(name))
                        {
                            continue;
                        }

                        installerName = name;
                        installerDownloadUrl = ReadString(asset, "browser_download_url");
                        break;
                    }
                }

                return new GitHubReleaseInfo
                {
                    Version = version,
                    HtmlUrl = htmlUrl,
                    InstallerName = installerName,
                    InstallerDownloadUrl = installerDownloadUrl
                };
            }
        }

        /// <summary>
        /// Downloads the selected setup executable into a temp update directory.
        /// </summary>
        private static async Task<string> DownloadInstallerAsync(GitHubReleaseInfo release)
        {
            string updateDirectory = Path.Combine(Path.GetTempPath(), "C64Emulator", "updates");
            Directory.CreateDirectory(updateDirectory);

            string fileName = string.IsNullOrWhiteSpace(release.InstallerName)
                ? "C64Emulator-" + FormatVersion(release.Version) + "-win-x64-setup.exe"
                : Path.GetFileName(release.InstallerName);
            string installerPath = Path.Combine(updateDirectory, fileName);
            string tempPath = installerPath + ".download";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using (var httpClient = CreateHttpClient())
            using (HttpResponseMessage response = await httpClient.GetAsync(release.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (Stream source = await response.Content.ReadAsStreamAsync())
                using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(target);
                }
            }

            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }

            File.Move(tempPath, installerPath);
            return installerPath;
        }

        /// <summary>
        /// Creates a GitHub-friendly HTTP client.
        /// </summary>
        private static HttpClient CreateHttpClient()
        {
            var httpClient = new HttpClient();
            httpClient.Timeout = RequestTimeout;
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return httpClient;
        }

        /// <summary>
        /// Reads the current assembly version used for release comparison.
        /// </summary>
        private static Version GetCurrentApplicationVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version ?? new Version(0, 0, 0);
        }

        /// <summary>
        /// Parses a tag such as v0.3.2 into a version object.
        /// </summary>
        private static Version ParseVersion(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return null;
            }

            string normalized = tagName.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(1);
            }

            Version version;
            return Version.TryParse(normalized, out version) ? version : null;
        }

        /// <summary>
        /// Compares version components while treating missing build/revision as zero.
        /// </summary>
        private static int CompareVersions(Version left, Version right)
        {
            for (int index = 0; index < 4; index++)
            {
                int leftPart = GetVersionPart(left, index);
                int rightPart = GetVersionPart(right, index);
                if (leftPart != rightPart)
                {
                    return leftPart.CompareTo(rightPart);
                }
            }

            return 0;
        }

        /// <summary>
        /// Gets one version component with undefined values mapped to zero.
        /// </summary>
        private static int GetVersionPart(Version version, int index)
        {
            if (version == null)
            {
                return 0;
            }

            switch (index)
            {
                case 0:
                    return Math.Max(0, version.Major);
                case 1:
                    return Math.Max(0, version.Minor);
                case 2:
                    return Math.Max(0, version.Build);
                default:
                    return Math.Max(0, version.Revision);
            }
        }

        /// <summary>
        /// Formats versions without the trailing assembly revision when it is zero.
        /// </summary>
        private static string FormatVersion(Version version)
        {
            if (version == null)
            {
                return "0.0.0";
            }

            int major = Math.Max(0, version.Major);
            int minor = Math.Max(0, version.Minor);
            int build = Math.Max(0, version.Build);
            int revision = Math.Max(0, version.Revision);
            return revision == 0
                ? major + "." + minor + "." + build
                : major + "." + minor + "." + build + "." + revision;
        }

        /// <summary>
        /// Reads a string property from a JSON object.
        /// </summary>
        private static string ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : string.Empty;
        }

        /// <summary>
        /// Checks whether an asset name looks like the Windows x64 setup package.
        /// </summary>
        private static bool IsWindowsSetupAsset(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Holds the release metadata needed by the startup checker.
        /// </summary>
        private sealed class GitHubReleaseInfo
        {
            public Version Version { get; set; }

            public string HtmlUrl { get; set; }

            public string InstallerName { get; set; }

            public string InstallerDownloadUrl { get; set; }
        }
    }
}
