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
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using C64Emulator.Core;

namespace C64Emulator
{
    /// <summary>
    /// Checks for required ROM files on GUI startup and optionally downloads missing files.
    /// </summary>
    internal static class RomStartupDownloader
    {
        private static readonly RequiredRom[] RequiredRoms =
        {
            new RequiredRom(
                "C64 BASIC/KERNAL",
                "c64-basic-kernal.bin",
                new[] { "c64-basic-kernal.bin" },
                "64c.251913-01.bin",
                "https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/c64/64c.251913-01.bin",
                "64E40E09124FC452AE97C83A880B82C912C4F7F74A1156C76963E4FF3717DE13"),
            new RequiredRom(
                "C64 CHARACTER",
                "c64-character.bin",
                new[] { "c64-character.bin" },
                "characters.901225-01.bin",
                "https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/c64/characters.901225-01.bin",
                "FD0D53B8480E86163AC98998976C72CC58D5DD8EB824ED7B829774E74213B420"),
            new RequiredRom(
                "1541 LOWER",
                "1541-c000-rom.bin",
                new[] { "1541-c000-rom.bin", "1541-c000.325302-01.bin" },
                "1541-c000.325302-01.bin",
                "https://www.zimmers.net/anonftp/pub/cbm/firmware/drives/new/1541/1541-c000.325302-01.bin",
                "6FA7B07AFF92DA66B0A28A52BB3C82FFE310AB0FAD2CC473B40137A8D299C7E5"),
            new RequiredRom(
                "1541 UPPER",
                "1541-e000-rom.bin",
                new[] { "1541-e000-rom.bin", "1541-e000.901229-01.bin", "1541-e000.901229-05.bin", "1541-e000.901229-03.bin", "1540-e000.325303-01.bin" },
                "1541-e000.901229-01.bin",
                "https://www.zimmers.net/anonftp/pub/cbm/firmware/drives/new/1541/1541-e000.901229-01.bin",
                "1B216F85C6FDD91B91BFD256AFFD9661D79FA411441A57D728D113ECF5B5451B")
        };

        /// <summary>
        /// Ensures all ROM files required by the emulator are present before the GUI starts.
        /// </summary>
        public static bool EnsureRequiredRoms()
        {
            List<RequiredRom> missingRoms = GetMissingRoms();
            if (missingRoms.Count == 0)
            {
                return true;
            }

            string targetDirectory = RomPathResolver.GetUserRomDirectory();
            string missingList = string.Join(Environment.NewLine, missingRoms.ConvertAll(rom => "  - " + rom.TargetFileName + " (" + rom.DisplayName + ")"));
            DialogResult result = MessageBox.Show(
                "The C64 Emulator requires these ROM files before it can start:" +
                Environment.NewLine + Environment.NewLine +
                missingList +
                Environment.NewLine + Environment.NewLine +
                "Downloaded ROMs will be saved to:" +
                Environment.NewLine +
                targetDirectory +
                Environment.NewLine + Environment.NewLine +
                "Do you want to download the missing files from zimmers.net now?" +
                Environment.NewLine + Environment.NewLine +
                "Please only download and use these ROM files if you are allowed to do so.",
                "Missing C64 ROM files",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The ROM directory could not be created:" +
                    Environment.NewLine +
                    targetDirectory +
                    Environment.NewLine + Environment.NewLine +
                    ex.Message,
                    "ROM download failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            using (var downloadForm = new RomDownloadForm(missingRoms, targetDirectory))
            {
                DialogResult downloadResult = downloadForm.ShowDialog();
                if (downloadResult != DialogResult.OK)
                {
                    return false;
                }
            }

            if (RomPathResolver.HasCompleteRomSet())
            {
                return true;
            }

            MessageBox.Show(
                "The download finished, but the emulator still cannot find all required ROM files." +
                Environment.NewLine + Environment.NewLine +
                RomPathResolver.BuildStatusReport(),
                "ROM files still missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        private static List<RequiredRom> GetMissingRoms()
        {
            var missing = new List<RequiredRom>();
            foreach (RequiredRom rom in RequiredRoms)
            {
                if (RomPathResolver.FindFirstExisting(rom.CompatibleFileNames) == null)
                {
                    missing.Add(rom);
                }
            }

            return missing;
        }

        private sealed class RequiredRom
        {
            public RequiredRom(string displayName, string targetFileName, string[] compatibleFileNames, string sourceFileName, string sourceUrl, string sha256)
            {
                DisplayName = displayName;
                TargetFileName = targetFileName;
                CompatibleFileNames = compatibleFileNames;
                SourceFileName = sourceFileName;
                SourceUrl = sourceUrl;
                Sha256 = sha256;
            }

            public string DisplayName { get; }

            public string TargetFileName { get; }

            public string[] CompatibleFileNames { get; }

            public string SourceFileName { get; }

            public string SourceUrl { get; }

            public string Sha256 { get; }
        }

        private sealed class RomDownloadForm : Form
        {
            private readonly IReadOnlyList<RequiredRom> _roms;
            private readonly string _targetDirectory;
            private readonly Dictionary<RequiredRom, ProgressBar> _progressBars = new Dictionary<RequiredRom, ProgressBar>();
            private readonly Dictionary<RequiredRom, Label> _statusLabels = new Dictionary<RequiredRom, Label>();
            private CancellationTokenSource _cancellation;
            private Label _summaryLabel;
            private Button _actionButton;
            private Button _cancelButton;
            private bool _allowClose;
            private bool _downloadRunning;
            private bool _downloadSucceeded;

            public RomDownloadForm(IReadOnlyList<RequiredRom> roms, string targetDirectory)
            {
                _roms = roms;
                _targetDirectory = targetDirectory;
                ErrorMessage = string.Empty;
                InitializeComponent();
            }

            public string ErrorMessage { get; private set; }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                StartDownloadAttempt();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_cancellation != null)
                    {
                        _cancellation.Dispose();
                    }
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
                Text = "Downloading C64 ROM files";
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                ClientSize = new Size(660, 154 + (_roms.Count * 48));

                var introLabel = new Label
                {
                    AutoSize = false,
                    Text = "Downloading missing ROM files from zimmers.net and saving them under the emulator file names in:",
                    Location = new Point(12, 12),
                    Size = new Size(ClientSize.Width - 24, 16)
                };
                Controls.Add(introLabel);

                var pathLabel = new Label
                {
                    AutoSize = false,
                    Text = _targetDirectory,
                    Location = new Point(12, 30),
                    Size = new Size(ClientSize.Width - 24, 18)
                };
                Controls.Add(pathLabel);

                int y = 62;
                foreach (RequiredRom rom in _roms)
                {
                    var nameLabel = new Label
                    {
                        AutoSize = false,
                        Text = rom.DisplayName + ": " + rom.SourceFileName + " -> " + rom.TargetFileName,
                        Location = new Point(12, y),
                        Size = new Size(ClientSize.Width - 24, 16)
                    };
                    Controls.Add(nameLabel);

                    var progressBar = new ProgressBar
                    {
                        Location = new Point(12, y + 18),
                        Size = new Size(380, 18),
                        Minimum = 0,
                        Maximum = 100,
                        Value = 0
                    };
                    Controls.Add(progressBar);
                    _progressBars[rom] = progressBar;

                    var statusLabel = new Label
                    {
                        AutoSize = false,
                        Text = "Waiting",
                        Location = new Point(402, y + 18),
                        Size = new Size(ClientSize.Width - 414, 18)
                    };
                    Controls.Add(statusLabel);
                    _statusLabels[rom] = statusLabel;

                    y += 48;
                }

                _summaryLabel = new Label
                {
                    AutoSize = false,
                    Text = "Preparing download.",
                    Location = new Point(12, y + 4),
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
                _summaryLabel.Text = "Downloading ROM files. Please wait.";
                _actionButton.Text = "Downloading";
                _actionButton.Enabled = false;
                _cancelButton.Text = "Cancel";
                _cancelButton.Enabled = true;

                foreach (RequiredRom rom in _roms)
                {
                    ProgressBar progressBar = _progressBars[rom];
                    progressBar.Style = ProgressBarStyle.Blocks;
                    progressBar.Value = 0;
                    _statusLabels[rom].Text = "Waiting";
                }

                try
                {
                    Directory.CreateDirectory(_targetDirectory);
                    using (var httpClient = new HttpClient())
                    {
                        httpClient.Timeout = TimeSpan.FromSeconds(60);
                        foreach (RequiredRom rom in _roms)
                        {
                            await DownloadRomAsync(httpClient, rom, _cancellation.Token).ConfigureAwait(true);
                        }
                    }

                    _downloadRunning = false;
                    _downloadSucceeded = true;
                    _summaryLabel.Text = "All ROM files were downloaded, verified, and saved successfully. Click Continue to start the emulator.";
                    _actionButton.Text = "Continue";
                    _actionButton.Enabled = true;
                    _cancelButton.Enabled = false;
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    ErrorMessage = "The ROM download was cancelled.";
                    _downloadRunning = false;
                    _downloadSucceeded = false;
                    _summaryLabel.Text = ErrorMessage + " Click Retry to start again or Cancel to exit the emulator.";
                    _actionButton.Text = "Retry";
                    _actionButton.Enabled = true;
                    _cancelButton.Text = "Cancel";
                    _cancelButton.Enabled = true;
                }
                catch (Exception ex)
                {
                    ErrorMessage = ex.Message;
                    _downloadRunning = false;
                    _downloadSucceeded = false;
                    _summaryLabel.Text = "Download failed: " + ex.Message + " Click Retry to try again or Cancel to exit the emulator.";
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

            private async Task DownloadRomAsync(HttpClient httpClient, RequiredRom rom, CancellationToken cancellationToken)
            {
                ProgressBar progressBar = _progressBars[rom];
                Label statusLabel = _statusLabels[rom];
                string tempPath = Path.Combine(_targetDirectory, rom.SourceFileName + ".download");
                string targetPath = Path.Combine(_targetDirectory, rom.TargetFileName);

                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                statusLabel.Text = "Connecting";
                using (HttpResponseMessage response = await httpClient.GetAsync(rom.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true))
                {
                    response.EnsureSuccessStatusCode();
                    long? contentLength = response.Content.Headers.ContentLength;
                    if (!contentLength.HasValue || contentLength.Value <= 0)
                    {
                        progressBar.Style = ProgressBarStyle.Marquee;
                    }

                    statusLabel.Text = "Downloading";
                    using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
                    using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buffer = new byte[81920];
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
                            if (contentLength.HasValue && contentLength.Value > 0)
                            {
                                int percent = (int)Math.Min(100, (totalRead * 100L) / contentLength.Value);
                                progressBar.Value = percent;
                                statusLabel.Text = percent + "%";
                            }
                        }
                    }
                }

                progressBar.Style = ProgressBarStyle.Blocks;
                progressBar.Value = 100;
                statusLabel.Text = "Verifying";
                string actualHash = ComputeSha256(tempPath);
                if (!string.Equals(actualHash, rom.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new InvalidDataException("Downloaded ROM hash mismatch for " + rom.SourceFileName + ".");
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
                statusLabel.Text = "Saved";
            }

            private static string ComputeSha256(string path)
            {
                using (SHA256 sha256 = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", string.Empty);
                }
            }
        }
    }
}
