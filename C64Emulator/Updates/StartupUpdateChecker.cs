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
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        private const int DownloadBufferSize = 81920;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Starts the non-blocking startup update check.
        /// </summary>
        /// <param name="requestApplicationShutdown">
        /// Optional callback used after the external launch helper has been started so
        /// the emulator releases its files before Setup starts replacing them.
        /// </param>
        public static void CheckForUpdatesOnStartup(Action requestApplicationShutdown = null)
        {
            Thread thread = new Thread(() => RunUpdateCheckThread(requestApplicationShutdown));
            thread.IsBackground = true;
            thread.Name = "C64 update checker";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// Runs the async checker on an STA thread so WinForms dialogs are safe.
        /// </summary>
        /// <param name="requestApplicationShutdown">Callback to close the emulator after scheduling installer launch.</param>
        private static void RunUpdateCheckThread(Action requestApplicationShutdown)
        {
            try
            {
                Thread.Sleep(StartupDelayMilliseconds);
                CheckForUpdatesAsync(requestApplicationShutdown).GetAwaiter().GetResult();
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
        /// <param name="requestApplicationShutdown">Callback to close the emulator after scheduling installer launch.</param>
        private static async Task CheckForUpdatesAsync(Action requestApplicationShutdown)
        {
            GitHubReleaseInfo release = await FetchLatestReleaseAsync();
            if (release == null || release.Version == null || string.IsNullOrWhiteSpace(release.InstallerDownloadUrl))
            {
                return;
            }

            Version currentVersion = GetCurrentApplicationVersion();
            bool newerVersionAvailable = CompareVersions(release.Version, currentVersion) > 0;
            if (!newerVersionAvailable)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                "A new version is available on GitHub.\r\n\r\n" +
                "Installed: " + FormatVersion(currentVersion) + "\r\n" +
                "Available: " + FormatVersion(release.Version) + "\r\n\r\n" +
                "Do you want to download and install it now?",
                "C64 Emulator Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                string installerPath = DownloadInstallerWithProgress(release);
                if (string.IsNullOrWhiteSpace(installerPath))
                {
                    return;
                }

                LaunchInstaller(installerPath, requestApplicationShutdown);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show(
                    "The update could not be downloaded or started.\r\n\r\n" + ex.Message,
                    "C64 Emulator Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Shows the setup download window and returns the downloaded installer path.
        /// </summary>
        /// <param name="release">Release metadata with installer asset information.</param>
        /// <returns>Downloaded setup path, or null when the user cancels.</returns>
        private static string DownloadInstallerWithProgress(GitHubReleaseInfo release)
        {
            using (var downloadForm = new SetupDownloadForm(release))
            {
                DialogResult result = downloadForm.ShowDialog();
                return result == DialogResult.OK ? downloadForm.InstallerPath : null;
            }
        }

        /// <summary>
        /// Starts the installer directly when no frontend callback exists, or schedules
        /// it through an external helper after the emulator process has exited.
        /// </summary>
        /// <param name="installerPath">Downloaded setup executable path.</param>
        /// <param name="requestApplicationShutdown">Optional frontend shutdown callback.</param>
        private static void LaunchInstaller(string installerPath, Action requestApplicationShutdown)
        {
            if (requestApplicationShutdown == null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });
                return;
            }

            StartInstallerAfterCurrentProcessExit(installerPath);
            RequestApplicationShutdown(requestApplicationShutdown);
        }

        /// <summary>
        /// Uses a small PowerShell helper to wait until this process releases its
        /// installed files before launching Setup.
        /// </summary>
        /// <param name="installerPath">Downloaded setup executable path.</param>
        private static void StartInstallerAfterCurrentProcessExit(string installerPath)
        {
            int processId = Process.GetCurrentProcess().Id;
            string command =
                "$process = Get-Process -Id " + processId.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue; " +
                "if ($process) { Wait-Process -Id " + processId.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue; }; " +
                "Start-Process -FilePath " + QuotePowerShellString(installerPath) + ";";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        /// <summary>
        /// Requests a clean application shutdown without letting callback failures
        /// turn a successful installer launch into an update error.
        /// </summary>
        /// <param name="requestApplicationShutdown">Optional frontend shutdown callback.</param>
        private static void RequestApplicationShutdown(Action requestApplicationShutdown)
        {
            if (requestApplicationShutdown == null)
            {
                return;
            }

            try
            {
                requestApplicationShutdown();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
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
                string installerDigest = string.Empty;

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
                        installerDigest = ReadString(asset, "digest");
                        break;
                    }
                }

                return new GitHubReleaseInfo
                {
                    Version = version,
                    HtmlUrl = htmlUrl,
                    InstallerName = installerName,
                    InstallerDownloadUrl = installerDownloadUrl,
                    InstallerDigest = NormalizeSha256Digest(installerDigest)
                };
            }
        }

        /// <summary>
        /// Creates a GitHub-friendly HTTP client for short metadata requests.
        /// </summary>
        private static HttpClient CreateHttpClient()
        {
            return CreateHttpClient(RequestTimeout);
        }

        /// <summary>
        /// Creates a GitHub-friendly HTTP client.
        /// </summary>
        /// <param name="timeout">Request timeout to apply.</param>
        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var httpClient = new HttpClient();
            httpClient.Timeout = timeout;
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
        /// Quotes a string literal for the tiny PowerShell launch helper.
        /// </summary>
        /// <param name="value">String value to quote.</param>
        /// <returns>Single-quoted PowerShell string literal.</returns>
        private static string QuotePowerShellString(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
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
        /// Builds the local setup file name for a release asset.
        /// </summary>
        /// <param name="release">Release metadata.</param>
        /// <returns>Safe setup file name.</returns>
        private static string CreateInstallerFileName(GitHubReleaseInfo release)
        {
            return string.IsNullOrWhiteSpace(release.InstallerName)
                ? "C64Emulator-" + FormatVersion(release.Version) + "-win-x64-setup.exe"
                : Path.GetFileName(release.InstallerName);
        }

        /// <summary>
        /// Normalizes a GitHub asset digest such as sha256:abcd into just the hash.
        /// </summary>
        /// <param name="digest">Raw GitHub release asset digest.</param>
        /// <returns>Uppercase SHA-256 hash, or an empty string when unavailable.</returns>
        private static string NormalizeSha256Digest(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest))
            {
                return string.Empty;
            }

            string normalized = digest.Trim();
            const string Prefix = "sha256:";
            if (normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(Prefix.Length);
            }

            normalized = normalized.Trim().Replace("-", string.Empty).ToUpperInvariant();
            return normalized.Length == 64 && IsHexString(normalized) ? normalized : string.Empty;
        }

        /// <summary>
        /// Checks whether a string contains only hexadecimal characters.
        /// </summary>
        /// <param name="value">Value to inspect.</param>
        /// <returns>True when the string is hexadecimal.</returns>
        private static bool IsHexString(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool isHex =
                    (character >= '0' && character <= '9') ||
                    (character >= 'A' && character <= 'F') ||
                    (character >= 'a' && character <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Computes the SHA-256 hash of a downloaded file.
        /// </summary>
        /// <param name="path">File to hash.</param>
        /// <returns>Uppercase hash without separators.</returns>
        private static string ComputeSha256(string path)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        /// <summary>
        /// Formats a byte count for the setup download window.
        /// </summary>
        /// <param name="bytes">Number of bytes.</param>
        /// <returns>Human readable byte count.</returns>
        private static string FormatByteCount(long bytes)
        {
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int unitIndex = 0;
            while (value >= 1024.0 && unitIndex < units.Length - 1)
            {
                value /= 1024.0;
                unitIndex++;
            }

            string format = unitIndex == 0 ? "0" : "0.0";
            return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }

        /// <summary>
        /// Small modal progress window used while the GitHub setup asset downloads.
        /// </summary>
        private sealed class SetupDownloadForm : Form
        {
            private readonly GitHubReleaseInfo _release;
            private readonly string _updateDirectory;
            private readonly string _installerFileName;
            private readonly string _tempPath;
            private CancellationTokenSource _cancellation;
            private ProgressBar _progressBar;
            private Label _statusLabel;
            private Label _sizeLabel;
            private Label _summaryLabel;
            private Button _actionButton;
            private Button _cancelButton;
            private bool _allowClose;
            private bool _downloadRunning;
            private bool _downloadSucceeded;

            public SetupDownloadForm(GitHubReleaseInfo release)
            {
                _release = release;
                _updateDirectory = Path.Combine(Path.GetTempPath(), "C64Emulator", "updates");
                _installerFileName = CreateInstallerFileName(release);
                InstallerPath = Path.Combine(_updateDirectory, _installerFileName);
                _tempPath = InstallerPath + ".download";
                ErrorMessage = string.Empty;
                InitializeComponent();
            }

            public string InstallerPath { get; private set; }

            public string ErrorMessage { get; private set; }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                StartDownloadAttempt();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _cancellation != null)
                {
                    _cancellation.Dispose();
                }

                base.Dispose(disposing);
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (_downloadRunning)
                {
                    if (_cancellation != null && !_cancellation.IsCancellationRequested)
                    {
                        _actionButton.Enabled = false;
                        _cancelButton.Enabled = false;
                        _cancellation.Cancel();
                    }

                    e.Cancel = true;
                    base.OnFormClosing(e);
                    return;
                }

                if (!_allowClose)
                {
                    DialogResult = _downloadSucceeded ? DialogResult.OK : DialogResult.Cancel;
                    _allowClose = true;
                }

                base.OnFormClosing(e);
            }

            private void InitializeComponent()
            {
                Text = "C64 Emulator Update";
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                TopMost = true;
                ClientSize = new Size(660, 214);

                var introLabel = new Label
                {
                    AutoSize = false,
                    Text = "Downloading the C64 Emulator setup from GitHub Releases:",
                    Location = new Point(12, 12),
                    Size = new Size(ClientSize.Width - 24, 18)
                };
                Controls.Add(introLabel);

                var nameLabel = new Label
                {
                    AutoSize = false,
                    Text = _installerFileName,
                    Location = new Point(12, 34),
                    Size = new Size(ClientSize.Width - 24, 18)
                };
                Controls.Add(nameLabel);

                var pathLabel = new Label
                {
                    AutoSize = false,
                    Text = InstallerPath,
                    Location = new Point(12, 56),
                    Size = new Size(ClientSize.Width - 24, 18)
                };
                Controls.Add(pathLabel);

                _progressBar = new ProgressBar
                {
                    Location = new Point(12, 88),
                    Size = new Size(420, 20),
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0
                };
                Controls.Add(_progressBar);

                _statusLabel = new Label
                {
                    AutoSize = false,
                    Text = "Waiting",
                    Location = new Point(444, 89),
                    Size = new Size(ClientSize.Width - 456, 18)
                };
                Controls.Add(_statusLabel);

                _sizeLabel = new Label
                {
                    AutoSize = false,
                    Text = string.Empty,
                    Location = new Point(12, 116),
                    Size = new Size(ClientSize.Width - 24, 18)
                };
                Controls.Add(_sizeLabel);

                _summaryLabel = new Label
                {
                    AutoSize = false,
                    Text = "Preparing download.",
                    Location = new Point(12, 142),
                    Size = new Size(ClientSize.Width - 24, 32)
                };
                Controls.Add(_summaryLabel);

                _actionButton = new Button
                {
                    Text = "Downloading",
                    Enabled = false,
                    Location = new Point(ClientSize.Width - 184, ClientSize.Height - 36),
                    Size = new Size(80, 24)
                };
                _actionButton.Click += (sender, args) =>
                {
                    if (_downloadSucceeded)
                    {
                        _allowClose = true;
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }

                    StartDownloadAttempt();
                };
                Controls.Add(_actionButton);

                _cancelButton = new Button
                {
                    Text = "Cancel",
                    Location = new Point(ClientSize.Width - 92, ClientSize.Height - 36),
                    Size = new Size(80, 24)
                };
                _cancelButton.Click += (sender, args) =>
                {
                    if (_downloadRunning)
                    {
                        _actionButton.Enabled = false;
                        _cancelButton.Enabled = false;
                        if (_cancellation != null)
                        {
                            _cancellation.Cancel();
                        }

                        return;
                    }

                    _allowClose = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                };
                Controls.Add(_cancelButton);
            }

            private async void StartDownloadAttempt()
            {
                ResetCancellation();
                ErrorMessage = string.Empty;
                _downloadRunning = true;
                _downloadSucceeded = false;
                _allowClose = false;
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.Value = 0;
                _statusLabel.Text = "Connecting";
                _sizeLabel.Text = string.Empty;
                _summaryLabel.Text = "Downloading setup. Please wait.";
                _actionButton.Text = "Downloading";
                _actionButton.Enabled = false;
                _cancelButton.Text = "Cancel";
                _cancelButton.Enabled = true;

                try
                {
                    Directory.CreateDirectory(_updateDirectory);
                    InstallerPath = await DownloadInstallerAsync(_cancellation.Token).ConfigureAwait(true);
                    _downloadRunning = false;
                    _downloadSucceeded = true;
                    _progressBar.Style = ProgressBarStyle.Blocks;
                    _progressBar.Value = 100;
                    _statusLabel.Text = "Ready";
                    _summaryLabel.Text = "Setup downloaded successfully. Click Install to close the emulator and start Setup.";
                    _actionButton.Text = "Install";
                    _actionButton.Enabled = true;
                    _cancelButton.Enabled = false;
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    DeleteTempFile();
                    ErrorMessage = "The setup download was cancelled.";
                    _downloadRunning = false;
                    _downloadSucceeded = false;
                    _progressBar.Style = ProgressBarStyle.Blocks;
                    _statusLabel.Text = "Cancelled";
                    _summaryLabel.Text = ErrorMessage + " Click Retry to start again or Cancel to continue without updating.";
                    _actionButton.Text = "Retry";
                    _actionButton.Enabled = true;
                    _cancelButton.Text = "Cancel";
                    _cancelButton.Enabled = true;
                }
                catch (Exception ex)
                {
                    DeleteTempFile();
                    ErrorMessage = ex.Message;
                    _downloadRunning = false;
                    _downloadSucceeded = false;
                    _progressBar.Style = ProgressBarStyle.Blocks;
                    _statusLabel.Text = "Failed";
                    _summaryLabel.Text = "Download failed: " + ex.Message + " Click Retry to try again or Cancel to continue without updating.";
                    _actionButton.Text = "Retry";
                    _actionButton.Enabled = true;
                    _cancelButton.Text = "Cancel";
                    _cancelButton.Enabled = true;
                }
            }

            private void ResetCancellation()
            {
                if (_cancellation != null)
                {
                    _cancellation.Dispose();
                }

                _cancellation = new CancellationTokenSource();
            }

            private async Task<string> DownloadInstallerAsync(CancellationToken cancellationToken)
            {
                DeleteTempFile();

                using (var httpClient = CreateHttpClient(DownloadTimeout))
                using (HttpResponseMessage response = await httpClient.GetAsync(_release.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true))
                {
                    response.EnsureSuccessStatusCode();
                    long? contentLength = response.Content.Headers.ContentLength;
                    if (!contentLength.HasValue || contentLength.Value <= 0)
                    {
                        _progressBar.Style = ProgressBarStyle.Marquee;
                    }

                    _statusLabel.Text = "Downloading";
                    using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
                    using (var output = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
                    {
                        byte[] buffer = new byte[DownloadBufferSize];
                        long totalRead = 0;
                        while (true)
                        {
                            int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(true);
                            if (read == 0)
                            {
                                break;
                            }

                            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(true);
                            totalRead += read;
                            UpdateDownloadProgress(totalRead, contentLength);
                        }
                    }
                }

                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.Value = 100;
                if (!string.IsNullOrWhiteSpace(_release.InstallerDigest))
                {
                    _statusLabel.Text = "Verifying";
                    string actualHash = ComputeSha256(_tempPath);
                    if (!string.Equals(actualHash, _release.InstallerDigest, StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteTempFile();
                        throw new InvalidDataException("Downloaded setup hash mismatch.");
                    }
                }

                _statusLabel.Text = "Saving";
                if (File.Exists(InstallerPath))
                {
                    File.Delete(InstallerPath);
                }

                File.Move(_tempPath, InstallerPath);
                _sizeLabel.Text = "Saved to " + InstallerPath;
                return InstallerPath;
            }

            private void UpdateDownloadProgress(long totalRead, long? contentLength)
            {
                if (contentLength.HasValue && contentLength.Value > 0)
                {
                    int percent = (int)Math.Min(100, (totalRead * 100L) / contentLength.Value);
                    _progressBar.Value = percent;
                    _statusLabel.Text = percent + "%";
                    _sizeLabel.Text = FormatByteCount(totalRead) + " / " + FormatByteCount(contentLength.Value);
                    return;
                }

                _sizeLabel.Text = FormatByteCount(totalRead);
            }

            private void DeleteTempFile()
            {
                try
                {
                    if (File.Exists(_tempPath))
                    {
                        File.Delete(_tempPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
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

            public string InstallerDigest { get; set; }
        }
    }
}
