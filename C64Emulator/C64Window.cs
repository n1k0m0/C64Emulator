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
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using C64Emulator.Core;
using OpenTK.Input;
using SharpPixels;

namespace C64Emulator
{
    /// <summary>
    /// Represents the c64 window component.
    /// </summary>
    public sealed class C64Window : SharpPixelsWindow
    {
        /// <summary>
        /// Lists the supported confirmation action values.
        /// </summary>
        private enum ConfirmationAction
        {
            None,
            Reset
        }

        private const int MaxCycleBatch = 2048;
        private const int NormalCycleBatch = 512;
        private const long WaitCycleQuantum = 256;
        private const double SleepThresholdSeconds = 0.002;
        private const double SleepSafetyMarginSeconds = 0.0005;
        private const float VolumeStep = 0.05f;
        private const float NoiseStep = 0.05f;
        private const int AudioOverlayItemCount = 8;
        private const int AudioOverlayVisibleRows = 4;
        private const int AudioOverlayRowSpacing = 36;
        private const int MediaBrowserVisibleRows = 9;
        private const int SaveOverlayVisibleRows = 8;
        private const double DriveFooterVisibleHoldSeconds = 1.2;
        private const double DriveFooterFadeOutSeconds = 0.6;
        private const double TurboToastHoldSeconds = 0.85;
        private const double TurboToastFadeSeconds = 0.35;
        private static readonly Dictionary<char, byte[]> OverlayFont = CreateOverlayFont();
        private readonly C64Model _model;
        private C64System _system;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private CancellationTokenSource _emulationCancellation;
        private Stopwatch _emulationStopwatch = new Stopwatch();
        private double _cyclesPerStopwatchTick;
        private double _secondsPerCycle;
        private uint[] _frameSnapshot;

        private Task _emulationTask;
        private long _emulationBaseCycle;
        private long _lastRenderedCycle;
        private volatile bool _audioOverlayVisible;
        private bool _mediaBrowserVisible;
        private bool _resetConfirmVisible;
        private volatile bool _saveOverlayVisible;
        private volatile bool _turboMode;
        private volatile bool _turboTimingResetPending;
        private bool _windowFullscreen;
        private bool _resetConfirmYesSelected = true;
        private ConfirmationAction _confirmationAction;
        private int _audioOverlaySelection;
        private int _audioOverlayScroll;
        private int _mediaBrowserSelection;
        private int _mediaBrowserScroll;
        private int _mediaBrowserTargetDrive = 8;
        private int _saveOverlaySelection;
        private int _saveOverlayScroll;
        private string _mediaBrowserCurrentDirectory;
        private List<MediaBrowserEntry> _mediaBrowserEntries = new List<MediaBrowserEntry>();
        private List<SaveStateMetadata> _saveStateEntries = new List<SaveStateMetadata>();
        private string _overlayStatusText = "READY";
        private string _saveOverlayStatusText = "F12 SAVE MENU";
        private string _turboToastText = string.Empty;
        private double _smoothedMhz;
        private double _driveFooterVisibility;
        private double _driveFooterIdleSeconds;
        private double _driveFooterPulsePhase;
        private double _turboToastSecondsRemaining;

        /// <summary>
        /// Handles the c64 window operation.
        /// </summary>
        public C64Window(C64Model model, string title) : base(model.VisibleWidth, model.VisibleHeight, title)
        {
            _model = model;
            InitializeSystem();
            StartEmulation();
        }

        /// <summary>
        /// Handles the initialize system operation.
        /// </summary>
        private void InitializeSystem()
        {
            CreateSystemInstance();
            _emulationBaseCycle = 0;
            _lastRenderedCycle = 0;
            _smoothedMhz = 0.0;
            _turboMode = false;
            _turboTimingResetPending = false;
            _windowFullscreen = false;
            _confirmationAction = ConfirmationAction.None;
            _resetConfirmVisible = false;
            _saveOverlayVisible = false;
            _turboToastSecondsRemaining = 0.0;
        }

        /// <summary>
        /// Creates a fresh emulated machine while keeping the host window state intact.
        /// </summary>
        private void CreateSystemInstance()
        {
            _system = new C64System(_model);
            // Standard disk loads now run over the drive-side IEC state
            // machine without the low-level CPU IEC hooks enabled.
            _system.EnableKernalIecHooks = false;
            _cyclesPerStopwatchTick = _model.CpuHz / Stopwatch.Frequency;
            _secondsPerCycle = 1.0 / _model.CpuHz;

            int framePixelCount = _model.VisibleWidth * _model.VisibleHeight;
            if (_frameSnapshot == null || _frameSnapshot.Length != framePixelCount)
            {
                _frameSnapshot = new uint[framePixelCount];
            }
        }

        /// <summary>
        /// Starts emulation.
        /// </summary>
        private void StartEmulation()
        {
            _emulationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            ResetEmulationTiming();
            CancellationToken cancellationToken = _emulationCancellation.Token;
            _emulationTask = Task.Run(() => EmulationLoop(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Stops emulation.
        /// </summary>
        private void StopEmulation()
        {
            if (_emulationCancellation != null)
            {
                _emulationCancellation.Cancel();
            }

            if (_emulationTask != null)
            {
                try
                {
                    _emulationTask.Wait(1000);
                }
                catch (AggregateException)
                {
                }
            }

            if (_emulationCancellation != null)
            {
                _emulationCancellation.Dispose();
                _emulationCancellation = null;
            }

            _emulationTask = null;
            _emulationBaseCycle = 0;
            _emulationStopwatch.Reset();
        }

        /// <summary>
        /// Handles the reset emulation timing operation.
        /// </summary>
        private void ResetEmulationTiming()
        {
            long currentCycle = 0;
            if (_system != null)
            {
                lock (_system.SyncRoot)
                {
                    currentCycle = _system.Timing.GlobalCycle;
                }
            }

            _emulationBaseCycle = currentCycle;
            _lastRenderedCycle = currentCycle;
            _smoothedMhz = 0.0;
            _emulationStopwatch.Restart();
        }

        /// <summary>
        /// Handles the emulation loop operation.
        /// </summary>
        private void EmulationLoop(CancellationToken cancellationToken)
        {
            try
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                }
                catch
                {
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_saveOverlayVisible || _audioOverlayVisible)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    if (_turboMode)
                    {
                        _system.RunCycles(MaxCycleBatch);
                        Thread.Yield();
                        continue;
                    }

                    if (_turboTimingResetPending)
                    {
                        _turboTimingResetPending = false;
                        ResetEmulationTiming();
                        continue;
                    }

                    long currentCycle = _system.Timing.GlobalCycle;
                    long desiredCycle = GetDesiredCycle();
                    long cyclesBehind = desiredCycle - currentCycle;
                    if (cyclesBehind <= 0)
                    {
                        WaitUntilNextCycle(cancellationToken, currentCycle);
                        continue;
                    }

                    int batchSize = (int)Math.Min(cyclesBehind, NormalCycleBatch);
                    _system.RunCycles(batchSize);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Releases resources owned by the component.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shutdown.Cancel();
                StopEmulation();
                if (_system != null)
                {
                    _system.Dispose();
                }
                _shutdown.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Handles the on user update operation.
        /// </summary>
        public override void OnUserUpdate(double time)
        {
            long currentCycle;
            ushort currentPc;
            byte currentOpcode;
            float sidMasterVolume;
            float sidNoiseLevel;
            SidChipModel sidChipModel;
            JoystickPort joystickPort;
            MountedMediaInfo mountedMediaInfo;
            bool drive8Mounted;
            bool drive8LedOn;
            bool drive8Active;
            bool drive9Mounted;
            bool drive9LedOn;
            bool drive9Active;
            bool drive10Mounted;
            bool drive10LedOn;
            bool drive10Active;
            bool drive11Mounted;
            bool drive11LedOn;
            bool drive11Active;
            lock (_system.SyncRoot)
            {
                currentCycle = _system.Timing.GlobalCycle;
                currentPc = _system.Cpu.PC;
                currentOpcode = _system.Cpu.CurrentOpcode;
                sidMasterVolume = _system.SidMasterVolume;
                sidNoiseLevel = _system.SidNoiseLevel;
                sidChipModel = _system.CurrentSidChipModel;
                joystickPort = _system.CurrentJoystickPort;
                mountedMediaInfo = _system.MountedMedia;
                drive8Mounted = _system.IsDriveMounted(8);
                drive8LedOn = _system.IsDriveLedOn(8);
                drive8Active = _system.IsDriveActive(8);
                drive9Mounted = _system.IsDriveMounted(9);
                drive9LedOn = _system.IsDriveLedOn(9);
                drive9Active = _system.IsDriveActive(9);
                drive10Mounted = _system.IsDriveMounted(10);
                drive10LedOn = _system.IsDriveLedOn(10);
                drive10Active = _system.IsDriveActive(10);
                drive11Mounted = _system.IsDriveMounted(11);
                drive11LedOn = _system.IsDriveLedOn(11);
                drive11Active = _system.IsDriveActive(11);
                Array.Copy(_system.FrameBuffer.Pixels, _frameSnapshot, _frameSnapshot.Length);
            }

            UpdateDriveFooterState(time,
                drive8Mounted || drive9Mounted || drive10Mounted || drive11Mounted,
                drive8Active || drive9Active || drive10Active || drive11Active);
            UpdateTurboToastState(time);
            DrawFrame(_frameSnapshot, _system.Model.VisibleWidth, _system.Model.VisibleHeight);
            DrawDriveFooter(
                _system.Model.VisibleWidth,
                _system.Model.VisibleHeight,
                drive8Mounted, drive8LedOn, drive8Active,
                drive9Mounted, drive9LedOn, drive9Active,
                drive10Mounted, drive10LedOn, drive10Active,
                drive11Mounted, drive11LedOn, drive11Active);

            if (_saveOverlayVisible)
            {
                DrawSaveOverlay();
            }
            else if (_audioOverlayVisible)
            {
                DrawAudioOverlay(sidMasterVolume, sidNoiseLevel, sidChipModel, joystickPort, mountedMediaInfo);
            }
            else
            {
                DrawTurboToast();
            }

            long cyclesThisFrame = currentCycle - _lastRenderedCycle;
            _lastRenderedCycle = currentCycle;
            if (time > 0)
            {
                double instantMhz = (cyclesThisFrame / time) / 1000000.0;
                double alpha = 1.0 - Math.Exp(-time / 0.25);
                _smoothedMhz = _smoothedMhz <= 0.0 ? instantMhz : _smoothedMhz + ((instantMhz - _smoothedMhz) * alpha);
            }

            Title = string.Format(
                "C64 Emulator {0}{1:F2} MHz - PC:{2:X4} OPC:{3:X2} - FPS:{4}",
                _turboMode ? "TURBO " : string.Empty,
                _smoothedMhz,
                currentPc,
                currentOpcode,
                FPS);
        }

        /// <summary>
        /// Handles the on user initialize operation.
        /// </summary>
        public override void OnUserInitialize()
        {
        }

        /// <summary>
        /// Handles the on user mouse move operation.
        /// </summary>
        public override void OnUserMouseMove(int x, int y)
        {
        }

        /// <summary>
        /// Handles the on user mouse click operation.
        /// </summary>
        public override void OnUserMouseClick(int x, int y, MouseState mouseState)
        {
        }

        /// <summary>
        /// Handles the on user key down operation.
        /// </summary>
        public override void OnUserKeyDown(SharpPixels.KeyEventArgs keyEventArgs)
        {
            if (keyEventArgs.Key == Key.F12)
            {
                ToggleSaveOverlay();
                return;
            }

            if (_saveOverlayVisible)
            {
                HandleSaveOverlayKeyDown(keyEventArgs.Key);
                return;
            }

            if (keyEventArgs.Key == Key.F10)
            {
                ToggleAudioOverlay();
                return;
            }

            if (keyEventArgs.Key == Key.F9)
            {
                ToggleTurboMode();
                return;
            }

            if (keyEventArgs.Key == Key.F11)
            {
                ToggleDisplayMode();
                return;
            }

            if (_resetConfirmVisible && HandleResetConfirmKeyDown(keyEventArgs.Key))
            {
                return;
            }

            if (_mediaBrowserVisible && HandleMediaBrowserKeyDown(keyEventArgs.Key))
            {
                return;
            }

            if (_audioOverlayVisible && HandleAudioOverlayKeyDown(keyEventArgs.Key))
            {
                return;
            }

            _system.KeyDown(keyEventArgs.Key);
        }

        /// <summary>
        /// Handles the on user key press operation.
        /// </summary>
        public override void OnUserKeyPress(SharpPixels.KeyEventArgs keyEventArgs)
        {
        }

        /// <summary>
        /// Handles the on user key up operation.
        /// </summary>
        public override void OnUserKeyUp(SharpPixels.KeyEventArgs keyEventArgs)
        {
            if (keyEventArgs.Key == Key.F12 || _saveOverlayVisible)
            {
                return;
            }

            if (keyEventArgs.Key == Key.F10 || keyEventArgs.Key == Key.F9 || keyEventArgs.Key == Key.F11)
            {
                return;
            }

            if (_resetConfirmVisible && CapturesResetConfirmKey(keyEventArgs.Key))
            {
                return;
            }

            if (_mediaBrowserVisible && CapturesMediaBrowserKey(keyEventArgs.Key))
            {
                return;
            }

            if (_audioOverlayVisible && CapturesAudioOverlayKey(keyEventArgs.Key))
            {
                return;
            }

            _system.KeyUp(keyEventArgs.Key);
        }

        /// <summary>
        /// Toggles the savestate overlay.
        /// </summary>
        private void ToggleSaveOverlay()
        {
            if (_saveOverlayVisible)
            {
                CloseSaveOverlay();
                return;
            }

            OpenSaveOverlay();
        }

        /// <summary>
        /// Toggles the settings overlay and pauses or resumes emulation timing.
        /// </summary>
        private void ToggleAudioOverlay()
        {
            if (_audioOverlayVisible)
            {
                CloseAudioOverlay();
            }
            else
            {
                OpenAudioOverlay();
            }
        }

        /// <summary>
        /// Opens the settings overlay and pauses the emulation loop.
        /// </summary>
        private void OpenAudioOverlay()
        {
            _audioOverlayVisible = true;
            ClampAudioOverlayScroll();
        }

        /// <summary>
        /// Closes the settings overlay and resumes normal timing.
        /// </summary>
        private void CloseAudioOverlay()
        {
            _audioOverlayVisible = false;
            _mediaBrowserVisible = false;
            _resetConfirmVisible = false;
            ResetEmulationTiming();
        }

        /// <summary>
        /// Opens the savestate overlay and pauses the emulation loop.
        /// </summary>
        private void OpenSaveOverlay()
        {
            _audioOverlayVisible = false;
            _mediaBrowserVisible = false;
            _resetConfirmVisible = false;
            _saveOverlayStatusText = "PAUSED";
            _saveOverlayVisible = true;
            ReloadSaveStateEntries();
        }

        /// <summary>
        /// Closes the savestate overlay and resumes normal timing.
        /// </summary>
        private void CloseSaveOverlay()
        {
            _saveOverlayVisible = false;
            _saveOverlayStatusText = "F12 SAVE MENU";
            ResetEmulationTiming();
        }

        /// <summary>
        /// Handles savestate overlay key input.
        /// </summary>
        private bool HandleSaveOverlayKeyDown(Key key)
        {
            switch (key)
            {
                case Key.Up:
                    MoveSaveOverlaySelection(-1);
                    return true;
                case Key.Down:
                    MoveSaveOverlaySelection(1);
                    return true;
                case Key.F5:
                case Key.S:
                    CreateSaveState();
                    return true;
                case Key.Enter:
                case Key.L:
                    LoadSelectedSaveState();
                    return true;
                case Key.Delete:
                case Key.BackSpace:
                    DeleteSelectedSaveState();
                    return true;
                case Key.Escape:
                    CloseSaveOverlay();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Creates a complete savestate file from the current machine state.
        /// </summary>
        private void CreateSaveState()
        {
            try
            {
                string saveDirectory = GetSaveDirectory();
                string savePath = SaveStateFile.CreateSavePath(saveDirectory);
                uint[] screenshotPixels = new uint[_frameSnapshot.Length];
                Array.Copy(_frameSnapshot, screenshotPixels, screenshotPixels.Length);

                SaveStateFile.Write(savePath, _system, screenshotPixels, _system.Model.VisibleWidth, _system.Model.VisibleHeight);
                ReloadSaveStateEntries();
                SelectSaveStatePath(savePath);
                _saveOverlayStatusText = "SAVED " + Path.GetFileName(savePath).ToUpperInvariant();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _saveOverlayStatusText = "SAVE FAILED";
            }
        }

        /// <summary>
        /// Loads the currently selected savestate.
        /// </summary>
        private void LoadSelectedSaveState()
        {
            if (_saveStateEntries.Count == 0)
            {
                _saveOverlayStatusText = "NO SAVE SELECTED";
                return;
            }

            try
            {
                SaveStateMetadata entry = _saveStateEntries[_saveOverlaySelection];
                SaveStateFile.Load(entry.Path, _system);
                lock (_system.SyncRoot)
                {
                    Array.Copy(_system.FrameBuffer.Pixels, _frameSnapshot, _frameSnapshot.Length);
                }

                _saveOverlayVisible = false;
                _overlayStatusText = "SAVE LOADED";
                _saveOverlayStatusText = "SAVE LOADED";
                ResetEmulationTiming();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _saveOverlayStatusText = "LOAD FAILED";
            }
        }

        /// <summary>
        /// Deletes the currently selected savestate file.
        /// </summary>
        private void DeleteSelectedSaveState()
        {
            if (_saveStateEntries.Count == 0)
            {
                _saveOverlayStatusText = "NO SAVE SELECTED";
                return;
            }

            try
            {
                SaveStateMetadata entry = _saveStateEntries[_saveOverlaySelection];
                File.Delete(entry.Path);
                _saveOverlayStatusText = "SAVE DELETED";
                ReloadSaveStateEntries();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _saveOverlayStatusText = "DELETE FAILED";
            }
        }

        /// <summary>
        /// Reloads savestate metadata from the host saves directory.
        /// </summary>
        private void ReloadSaveStateEntries()
        {
            var entries = new List<SaveStateMetadata>();
            try
            {
                string saveDirectory = GetSaveDirectory();
                Directory.CreateDirectory(saveDirectory);
                string[] files = Directory.GetFiles(saveDirectory, SaveStateFile.SearchPattern, SearchOption.TopDirectoryOnly);
                foreach (string file in files)
                {
                    try
                    {
                        entries.Add(SaveStateFile.ReadMetadata(file));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }

                entries.Sort((left, right) => right.CreatedLocalTime.CompareTo(left.CreatedLocalTime));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _saveOverlayStatusText = "SAVE DIR FAILED";
            }

            _saveStateEntries = entries;
            if (_saveOverlaySelection >= _saveStateEntries.Count)
            {
                _saveOverlaySelection = Math.Max(0, _saveStateEntries.Count - 1);
            }

            if (_saveOverlaySelection < 0)
            {
                _saveOverlaySelection = 0;
            }

            ClampSaveOverlayScroll();
        }

        /// <summary>
        /// Selects a savestate path in the overlay list.
        /// </summary>
        private void SelectSaveStatePath(string path)
        {
            for (int index = 0; index < _saveStateEntries.Count; index++)
            {
                if (string.Equals(_saveStateEntries[index].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _saveOverlaySelection = index;
                    ClampSaveOverlayScroll();
                    return;
                }
            }
        }

        /// <summary>
        /// Moves the savestate overlay list selection.
        /// </summary>
        private void MoveSaveOverlaySelection(int delta)
        {
            if (_saveStateEntries.Count == 0)
            {
                return;
            }

            _saveOverlaySelection += delta;
            if (_saveOverlaySelection < 0)
            {
                _saveOverlaySelection = 0;
            }

            if (_saveOverlaySelection >= _saveStateEntries.Count)
            {
                _saveOverlaySelection = _saveStateEntries.Count - 1;
            }

            ClampSaveOverlayScroll();
        }

        /// <summary>
        /// Keeps the savestate overlay scroll position around the current selection.
        /// </summary>
        private void ClampSaveOverlayScroll()
        {
            if (_saveOverlaySelection < _saveOverlayScroll)
            {
                _saveOverlayScroll = _saveOverlaySelection;
            }

            int maxVisibleStart = Math.Max(0, _saveStateEntries.Count - SaveOverlayVisibleRows);
            int visibleEnd = _saveOverlayScroll + SaveOverlayVisibleRows - 1;
            if (_saveOverlaySelection > visibleEnd)
            {
                _saveOverlayScroll = _saveOverlaySelection - (SaveOverlayVisibleRows - 1);
            }

            if (_saveOverlayScroll > maxVisibleStart)
            {
                _saveOverlayScroll = maxVisibleStart;
            }

            if (_saveOverlayScroll < 0)
            {
                _saveOverlayScroll = 0;
            }
        }

        /// <summary>
        /// Gets the savestate directory next to the emulator executable.
        /// </summary>
        private static string GetSaveDirectory()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");
        }

        /// <summary>
        /// Handles the toggle turbo mode operation.
        /// </summary>
        private void ToggleTurboMode()
        {
            bool enableTurbo = !_turboMode;
            _turboMode = enableTurbo;
            if (enableTurbo)
            {
                _turboTimingResetPending = false;
            }
            else
            {
                _turboTimingResetPending = true;
            }

            _overlayStatusText = _turboMode ? "TURBO ON" : "TURBO OFF";
            ShowTurboToast(_overlayStatusText);
        }

        /// <summary>
        /// Handles the show turbo toast operation.
        /// </summary>
        private void ShowTurboToast(string text)
        {
            _turboToastText = text;
            _turboToastSecondsRemaining = TurboToastHoldSeconds + TurboToastFadeSeconds;
        }

        /// <summary>
        /// Handles the toggle display mode operation.
        /// </summary>
        private void ToggleDisplayMode()
        {
            ToggleFullscreen();
            _windowFullscreen = !_windowFullscreen;
            _overlayStatusText = _windowFullscreen ? "FULLSCREEN" : "WINDOW MODE";
        }

        /// <summary>
        /// Draws frame.
        /// </summary>
        private void DrawFrame(uint[] pixels, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            uint borderArgb = pixels[0];
            byte borderRed = (byte)((borderArgb >> 16) & 0xFF);
            byte borderGreen = (byte)((borderArgb >> 8) & 0xFF);
            byte borderBlue = (byte)(borderArgb & 0xFF);

            int integerScaleX = PixelsWidth / width;
            int integerScaleY = PixelsHeight / height;
            int integerScale = Math.Min(integerScaleX, integerScaleY);
            if (integerScale >= 1)
            {
                int scaledWidth = width * integerScale;
                int scaledHeight = height * integerScale;
                int offsetX = (PixelsWidth - scaledWidth) / 2;
                int offsetY = (PixelsHeight - scaledHeight) / 2;
                DrawFrameMargins(offsetX, offsetY, scaledWidth, scaledHeight, borderRed, borderGreen, borderBlue);
                DrawArgbPixelsScaled(pixels, width, height, offsetX, offsetY, integerScale);
                return;
            }

            DrawArgbPixelsStretched(pixels, width, height);
        }

        /// <summary>
        /// Fills only the letterbox/pillarbox margins that are outside the scaled C64 frame.
        /// </summary>
        private void DrawFrameMargins(int frameX, int frameY, int frameWidth, int frameHeight, byte red, byte green, byte blue)
        {
            if (frameY > 0)
            {
                DrawFilledRectangle(0, 0, PixelsWidth, frameY, red, green, blue);
                DrawFilledRectangle(0, frameY + frameHeight, PixelsWidth, PixelsHeight - frameY - frameHeight, red, green, blue);
            }

            if (frameX > 0)
            {
                DrawFilledRectangle(0, frameY, frameX, frameHeight, red, green, blue);
                DrawFilledRectangle(frameX + frameWidth, frameY, PixelsWidth - frameX - frameWidth, frameHeight, red, green, blue);
            }
        }

        /// <summary>
        /// Updates drive footer state.
        /// </summary>
        private void UpdateDriveFooterState(double time, bool anyDriveMounted, bool anyDriveActive)
        {
            bool shouldShow = anyDriveMounted || _driveFooterVisibility > 0.0;
            if (!shouldShow)
            {
                _driveFooterIdleSeconds = 0.0;
                _driveFooterVisibility = 0.0;
                return;
            }

            if (anyDriveActive)
            {
                _driveFooterIdleSeconds = 0.0;
                _driveFooterVisibility = 1.0;
                _driveFooterPulsePhase += time * 10.0;
                return;
            }

            _driveFooterPulsePhase += time * 2.0;
            _driveFooterIdleSeconds += time;
            if (_driveFooterIdleSeconds <= DriveFooterVisibleHoldSeconds)
            {
                _driveFooterVisibility = 1.0;
                return;
            }

            double fadeProgress = (_driveFooterIdleSeconds - DriveFooterVisibleHoldSeconds) / DriveFooterFadeOutSeconds;
            if (fadeProgress >= 1.0)
            {
                _driveFooterVisibility = 0.0;
            }
            else
            {
                _driveFooterVisibility = 1.0 - fadeProgress;
            }
        }

        /// <summary>
        /// Updates turbo toast state.
        /// </summary>
        private void UpdateTurboToastState(double time)
        {
            if (_turboToastSecondsRemaining <= 0.0)
            {
                _turboToastSecondsRemaining = 0.0;
                return;
            }

            _turboToastSecondsRemaining -= Math.Max(0.0, time);
            if (_turboToastSecondsRemaining < 0.0)
            {
                _turboToastSecondsRemaining = 0.0;
            }
        }

        /// <summary>
        /// Draws turbo toast.
        /// </summary>
        private void DrawTurboToast()
        {
            if (_turboToastSecondsRemaining <= 0.0 || string.IsNullOrWhiteSpace(_turboToastText))
            {
                return;
            }

            double alphaFactor = _turboToastSecondsRemaining > TurboToastFadeSeconds
                ? 1.0
                : _turboToastSecondsRemaining / TurboToastFadeSeconds;
            byte panelAlpha = ClampToByte((int)Math.Round(220.0 * alphaFactor));
            byte frameRed = ClampToByte((int)Math.Round(182.0 * alphaFactor));
            byte frameGreen = ClampToByte((int)Math.Round(214.0 * alphaFactor));
            byte frameBlue = ClampToByte((int)Math.Round(108.0 * alphaFactor));
            byte textRed = ClampToByte((int)Math.Round(240.0 * alphaFactor));
            byte textGreen = ClampToByte((int)Math.Round(248.0 * alphaFactor));
            byte textBlue = ClampToByte((int)Math.Round(255.0 * alphaFactor));

            int width = 120;
            int height = 34;
            int x = (PixelsWidth - width) / 2;
            int y = 18;
            DrawFilledRectangleWithAlpha(x, y, width, height, 8, 10, 18, panelAlpha);
            DrawLine(x, y, x + width - 1, y, frameRed, frameGreen, frameBlue);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, frameRed, frameGreen, frameBlue);
            DrawLine(x, y, x, y + height - 1, frameRed, frameGreen, frameBlue);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, frameRed, frameGreen, frameBlue);
            DrawOverlayText(x + 16, y + 10, _turboToastText, 2, textRed, textGreen, textBlue);
        }

        /// <summary>
        /// Draws the savestate overlay.
        /// </summary>
        private void DrawSaveOverlay()
        {
            int overlayWidth = Math.Min(PixelsWidth - 24, 360);
            int overlayHeight = Math.Min(PixelsHeight - 28, 240);
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = (PixelsHeight - overlayHeight) / 2;

            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 8, 10, 18, 232);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 182, 214, 108);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 182, 214, 108);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 182, 214, 108);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 182, 214, 108);

            DrawOverlayText(overlayX + 14, overlayY + 12, "SAVE STATES", 2, 240, 248, 255);
            DrawOverlayText(overlayX + 14, overlayY + 31, "S/F5 SAVE  ENTER/L LOAD  DEL DELETE  F12/ESC CLOSE", 1, 192, 210, 225);
            DrawOverlayText(overlayX + 14, overlayY + 43, "STATUS " + FormatOverlayValue(_saveOverlayStatusText, 45), 1, 182, 214, 108);

            int listX = overlayX + 14;
            int listY = overlayY + 62;
            int listWidth = Math.Max(120, overlayWidth - 156);
            DrawFilledRectangleWithAlpha(listX - 4, listY - 4, listWidth + 8, 112, 20, 24, 38, 210);

            if (_saveStateEntries.Count == 0)
            {
                DrawOverlayText(listX, listY + 12, "NO SAVES YET", 2, 255, 243, 168);
                DrawOverlayText(listX, listY + 34, "PRESS S TO CREATE ONE", 1, 232, 238, 244);
            }
            else
            {
                for (int row = 0; row < SaveOverlayVisibleRows; row++)
                {
                    int index = _saveOverlayScroll + row;
                    if (index >= _saveStateEntries.Count)
                    {
                        break;
                    }

                    SaveStateMetadata entry = _saveStateEntries[index];
                    bool selected = index == _saveOverlaySelection;
                    byte red = selected ? (byte)255 : (byte)232;
                    byte green = selected ? (byte)243 : (byte)238;
                    byte blue = selected ? (byte)168 : (byte)244;
                    string listText = FormatSaveListEntry(entry, Math.Max(10, (listWidth / 6) - 3));
                    DrawOverlayText(listX, listY + (row * 13), (selected ? "> " : "  ") + listText, 1, red, green, blue);
                }
            }

            int thumbnailX = overlayX + overlayWidth - 130;
            int thumbnailY = overlayY + 68;
            int thumbnailWidth = 112;
            int thumbnailHeight = 80;
            DrawLine(thumbnailX - 1, thumbnailY - 1, thumbnailX + thumbnailWidth, thumbnailY - 1, 115, 142, 196);
            DrawLine(thumbnailX - 1, thumbnailY + thumbnailHeight, thumbnailX + thumbnailWidth, thumbnailY + thumbnailHeight, 115, 142, 196);
            DrawLine(thumbnailX - 1, thumbnailY - 1, thumbnailX - 1, thumbnailY + thumbnailHeight, 115, 142, 196);
            DrawLine(thumbnailX + thumbnailWidth, thumbnailY - 1, thumbnailX + thumbnailWidth, thumbnailY + thumbnailHeight, 115, 142, 196);

            if (_saveStateEntries.Count > 0)
            {
                SaveStateMetadata selectedEntry = _saveStateEntries[_saveOverlaySelection];
                DrawArgbPixelsToRectangle(
                    selectedEntry.ScreenshotPixels,
                    selectedEntry.ScreenshotWidth,
                    selectedEntry.ScreenshotHeight,
                    thumbnailX,
                    thumbnailY,
                    thumbnailWidth,
                    thumbnailHeight);

                DrawOverlayText(thumbnailX, thumbnailY + thumbnailHeight + 8, selectedEntry.CreatedLocalTime.ToString("yyyy-MM-dd"), 1, 232, 238, 244);
                DrawOverlayText(thumbnailX, thumbnailY + thumbnailHeight + 19, selectedEntry.CreatedLocalTime.ToString("HH:mm:ss"), 1, 232, 238, 244);
                DrawOverlayText(thumbnailX, thumbnailY + thumbnailHeight + 31, FormatOverlayValue(Path.GetFileName(selectedEntry.Path), 18), 1, 182, 214, 108);
            }
            else
            {
                DrawFilledRectangle(thumbnailX, thumbnailY, thumbnailWidth, thumbnailHeight, 24, 24, 32);
                DrawOverlayText(thumbnailX + 20, thumbnailY + 34, "NO IMAGE", 1, 115, 142, 196);
            }
        }

        /// <summary>
        /// Draws ARGB pixels into an arbitrary rectangle using point-sampled stretching.
        /// </summary>
        private void DrawArgbPixelsToRectangle(uint[] sourcePixels, int sourceWidth, int sourceHeight, int x, int y, int width, int height)
        {
            if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0)
            {
                DrawFilledRectangle(x, y, Math.Max(0, width), Math.Max(0, height), 24, 24, 32);
                return;
            }

            int requiredPixels = sourceWidth * sourceHeight;
            if (sourcePixels.Length < requiredPixels)
            {
                DrawFilledRectangle(x, y, width, height, 24, 24, 32);
                return;
            }

            GetPreviewCropRectangle(sourcePixels, sourceWidth, sourceHeight, out int cropX, out int cropY, out int cropWidth, out int cropHeight);

            for (int targetY = 0; targetY < height; targetY++)
            {
                int sourceY = cropY + Math.Min(cropHeight - 1, ((targetY * cropHeight) + (height / 2)) / height);
                int sourceRow = sourceY * sourceWidth;
                for (int targetX = 0; targetX < width; targetX++)
                {
                    int sourceX = cropX + Math.Min(cropWidth - 1, ((targetX * cropWidth) + (width / 2)) / width);
                    uint argb = sourcePixels[sourceRow + sourceX];
                    DrawPixel(
                        x + targetX,
                        y + targetY,
                        (byte)((argb >> 16) & 0xFF),
                        (byte)((argb >> 8) & 0xFF),
                        (byte)(argb & 0xFF));
                }
            }
        }

        /// <summary>
        /// Finds the non-border area of a savestate preview frame.
        /// </summary>
        private static void GetPreviewCropRectangle(uint[] sourcePixels, int sourceWidth, int sourceHeight, out int cropX, out int cropY, out int cropWidth, out int cropHeight)
        {
            uint borderColor = sourcePixels[0];
            int left = sourceWidth;
            int top = sourceHeight;
            int right = -1;
            int bottom = -1;

            for (int y = 0; y < sourceHeight; y++)
            {
                int row = y * sourceWidth;
                for (int x = 0; x < sourceWidth; x++)
                {
                    if (IsPreviewContentPixel(sourcePixels[row + x], borderColor))
                    {
                        if (x < left)
                        {
                            left = x;
                        }

                        if (x > right)
                        {
                            right = x;
                        }

                        if (y < top)
                        {
                            top = y;
                        }

                        if (y > bottom)
                        {
                            bottom = y;
                        }
                    }
                }
            }

            if (right < left || bottom < top)
            {
                cropX = 0;
                cropY = 0;
                cropWidth = sourceWidth;
                cropHeight = sourceHeight;
                return;
            }

            int detectedWidth = right - left + 1;
            int detectedHeight = bottom - top + 1;
            int detectedArea = detectedWidth * detectedHeight;
            int minimumUsefulArea = (sourceWidth * sourceHeight) / 4;

            if (detectedArea < minimumUsefulArea)
            {
                cropX = 0;
                cropY = 0;
                cropWidth = sourceWidth;
                cropHeight = sourceHeight;
                return;
            }

            cropX = left;
            cropY = top;
            cropWidth = detectedWidth;
            cropHeight = detectedHeight;
        }

        /// <summary>
        /// Returns whether the pixel differs from the surrounding border color.
        /// </summary>
        private static bool IsPreviewContentPixel(uint argb, uint borderColor)
        {
            int redDifference = Math.Abs((int)((argb >> 16) & 0xFF) - (int)((borderColor >> 16) & 0xFF));
            int greenDifference = Math.Abs((int)((argb >> 8) & 0xFF) - (int)((borderColor >> 8) & 0xFF));
            int blueDifference = Math.Abs((int)(argb & 0xFF) - (int)(borderColor & 0xFF));
            return redDifference > 2 || greenDifference > 2 || blueDifference > 2;
        }

        /// <summary>
        /// Formats a savestate row for the overlay list.
        /// </summary>
        private static string FormatSaveListEntry(SaveStateMetadata entry, int maxLength)
        {
            string text = entry.CreatedLocalTime.ToString("MM-dd HH:mm:ss") + " " + Path.GetFileNameWithoutExtension(entry.Path);
            return FormatOverlayValue(text, maxLength);
        }

        /// <summary>
        /// Draws drive footer.
        /// </summary>
        private void DrawDriveFooter(
            int sourceWidth,
            int sourceHeight,
            bool drive8Mounted,
            bool drive8LedOn,
            bool drive8Active,
            bool drive9Mounted,
            bool drive9LedOn,
            bool drive9Active,
            bool drive10Mounted,
            bool drive10LedOn,
            bool drive10Active,
            bool drive11Mounted,
            bool drive11LedOn,
            bool drive11Active)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || _driveFooterVisibility <= 0.01)
            {
                return;
            }

            int integerScaleX = PixelsWidth / sourceWidth;
            int integerScaleY = PixelsHeight / sourceHeight;
            int integerScale = Math.Min(integerScaleX, integerScaleY);
            int scaledWidth = integerScale >= 1 ? sourceWidth * integerScale : PixelsWidth;
            int scaledHeight = integerScale >= 1 ? sourceHeight * integerScale : PixelsHeight;
            int offsetX = (PixelsWidth - scaledWidth) / 2;
            int offsetY = (PixelsHeight - scaledHeight) / 2;

            int footerWidth = 248;
            int footerHeight = 20;
            int footerX = offsetX + ((scaledWidth - footerWidth) / 2);
            int footerY = offsetY + scaledHeight + 8;
            if (footerY + footerHeight > PixelsHeight - 4)
            {
                footerY = PixelsHeight - footerHeight - 4;
            }

            byte alpha = ScaleAlpha(220);
            (byte frameRed, byte frameGreen, byte frameBlue) = ScaleColor(115, 142, 196);
            (byte textRed, byte textGreen, byte textBlue) = ScaleColor(192, 210, 225);

            DrawFilledRectangleWithAlpha(footerX, footerY, footerWidth, footerHeight, 8, 10, 18, alpha);
            DrawLine(footerX, footerY, footerX + footerWidth - 1, footerY, frameRed, frameGreen, frameBlue);
            DrawLine(footerX, footerY + footerHeight - 1, footerX + footerWidth - 1, footerY + footerHeight - 1, frameRed, frameGreen, frameBlue);
            DrawLine(footerX, footerY, footerX, footerY + footerHeight - 1, frameRed, frameGreen, frameBlue);
            DrawLine(footerX + footerWidth - 1, footerY, footerX + footerWidth - 1, footerY + footerHeight - 1, frameRed, frameGreen, frameBlue);

            DrawOverlayText(footerX + 10, footerY + 6, "DRIVES", 1, textRed, textGreen, textBlue);
            DrawDriveLed(footerX + 68, footerY + 5, "8", drive8Mounted, drive8LedOn, drive8Active);
            DrawDriveLed(footerX + 108, footerY + 5, "9", drive9Mounted, drive9LedOn, drive9Active);
            DrawDriveLed(footerX + 148, footerY + 5, "10", drive10Mounted, drive10LedOn, drive10Active);
            DrawDriveLed(footerX + 192, footerY + 5, "11", drive11Mounted, drive11LedOn, drive11Active);
        }

        /// <summary>
        /// Draws drive led.
        /// </summary>
        private void DrawDriveLed(int x, int y, string label, bool mounted, bool ledOn, bool active)
        {
            (byte labelRed, byte labelGreen, byte labelBlue) = ScaleColor(232, 238, 244);
            DrawOverlayText(x, y, label, 1, labelRed, labelGreen, labelBlue);

            (byte bodyRed, byte bodyGreen, byte bodyBlue) = mounted
                ? ScaleColor(42, 44, 52)
                : ScaleColor(24, 24, 24);
            DrawFilledRectangle(x + 14, y + 1, 12, 8, bodyRed, bodyGreen, bodyBlue);

            byte ledRed;
            byte ledGreen;
            byte ledBlue;
            if (ledOn)
            {
                ledRed = 255;
                ledGreen = 72;
                ledBlue = 48;
            }
            else if (mounted)
            {
                ledRed = 88;
                ledGreen = 28;
                ledBlue = 20;
            }
            else
            {
                ledRed = 36;
                ledGreen = 36;
                ledBlue = 36;
            }

            DrawFilledRectangle(x + 17, y + 3, 6, 4, ledRed, ledGreen, ledBlue);

            DrawDriveActivityBar(x + 28, y + 3, active);
        }

        /// <summary>
        /// Draws drive activity bar.
        /// </summary>
        private void DrawDriveActivityBar(int x, int y, bool active)
        {
            int activeSegments = 0;
            if (active)
            {
                activeSegments = 1 + (int)((Math.Sin(_driveFooterPulsePhase) * 0.5 + 0.5) * 3.99);
            }

            for (int index = 0; index < 4; index++)
            {
                bool segmentOn = index < activeSegments;
                (byte red, byte green, byte blue) = segmentOn
                    ? ScaleColor(182, 214, 108)
                    : ScaleColor(38, 44, 34);
                DrawFilledRectangle(x + (index * 3), y, 2, 4, red, green, blue);
            }
        }

        /// <summary>
        /// Handles the scale alpha operation.
        /// </summary>
        private byte ScaleAlpha(int alpha)
        {
            return ClampToByte((int)Math.Round(alpha * _driveFooterVisibility));
        }

        /// <summary>
        /// Handles the private operation.
        /// </summary>
        private (byte red, byte green, byte blue) ScaleColor(byte red, byte green, byte blue)
        {
            return (
                ClampToByte((int)Math.Round(red * _driveFooterVisibility)),
                ClampToByte((int)Math.Round(green * _driveFooterVisibility)),
                ClampToByte((int)Math.Round(blue * _driveFooterVisibility)));
        }

        /// <summary>
        /// Handles the clamp to byte operation.
        /// </summary>
        private static byte ClampToByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 255)
            {
                return 255;
            }

            return (byte)value;
        }

        /// <summary>
        /// Handles audio overlay key down.
        /// </summary>
        private bool HandleAudioOverlayKeyDown(Key key)
        {
            switch (key)
            {
                case Key.Up:
                    MoveAudioOverlaySelection(-1);
                    return true;
                case Key.Down:
                    MoveAudioOverlaySelection(1);
                    return true;
                case Key.Left:
                case Key.Minus:
                    AdjustAudioSetting(-1);
                    return true;
                case Key.Right:
                case Key.Plus:
                    AdjustAudioSetting(1);
                    return true;
                case Key.Enter:
                    if (_audioOverlaySelection == 4)
                    {
                        OpenMediaBrowser();
                    }
                    else if (_audioOverlaySelection == 5)
                    {
                        ToggleDisplayMode();
                    }
                    else if (_audioOverlaySelection == 6)
                    {
                        ToggleTurboMode();
                    }
                    else if (_audioOverlaySelection == 7)
                    {
                        OpenResetConfirmation();
                    }
                    else
                    {
                        CloseAudioOverlay();
                    }
                    return true;
                case Key.Escape:
                    CloseAudioOverlay();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles the captures audio overlay key operation.
        /// </summary>
        private bool CapturesAudioOverlayKey(Key key)
        {
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.Plus:
                case Key.Minus:
                case Key.Enter:
                case Key.Escape:
                case Key.BackSpace:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles the adjust audio setting operation.
        /// </summary>
        private void AdjustAudioSetting(int direction)
        {
            if (_audioOverlaySelection == 0)
            {
                _system.SetSidMasterVolume(_system.SidMasterVolume + (direction * VolumeStep));
                return;
            }

            if (_audioOverlaySelection == 1)
            {
                _system.SetSidNoiseLevel(_system.SidNoiseLevel + (direction * NoiseStep));
                return;
            }

            if (_audioOverlaySelection == 2)
            {
                _system.SetSidChipModel(_system.CurrentSidChipModel == SidChipModel.Mos6581 ? SidChipModel.Mos8580 : SidChipModel.Mos6581);
                return;
            }

            if (_audioOverlaySelection == 3)
            {
                _system.SetJoystickPort(GetNextJoystickPort(_system.CurrentJoystickPort));
                _overlayStatusText = "JOYSTICK " + FormatJoystickPort(_system.CurrentJoystickPort);
                return;
            }

            if (_audioOverlaySelection == 4)
            {
                if (direction >= 0)
                {
                    OpenMediaBrowser();
                    return;
                }

                _overlayStatusText = _system.EjectMedia();
                return;
            }

            if (_audioOverlaySelection == 5)
            {
                ToggleDisplayMode();
                return;
            }

            if (_audioOverlaySelection == 6)
            {
                ToggleTurboMode();
                return;
            }

            if (_audioOverlaySelection == 7)
            {
                if (direction >= 0)
                {
                    OpenResetConfirmation();
                }

                return;
            }

        }

        /// <summary>
        /// Handles reset confirm key down.
        /// </summary>
        private bool HandleResetConfirmKeyDown(Key key)
        {
            switch (key)
            {
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    _resetConfirmYesSelected = !_resetConfirmYesSelected;
                    return true;
                case Key.Enter:
                    if (_resetConfirmYesSelected)
                    {
                        ExecuteConfirmationAction();
                    }
                    else
                    {
                        _overlayStatusText = GetConfirmationCanceledStatusText();
                        ClearConfirmationState();
                    }
                    return true;
                case Key.Escape:
                case Key.BackSpace:
                    _overlayStatusText = GetConfirmationCanceledStatusText();
                    ClearConfirmationState();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles the captures reset confirm key operation.
        /// </summary>
        private bool CapturesResetConfirmKey(Key key)
        {
            switch (key)
            {
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                case Key.Enter:
                case Key.Escape:
                case Key.BackSpace:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles media browser key down.
        /// </summary>
        private bool HandleMediaBrowserKeyDown(Key key)
        {
            switch (key)
            {
                case Key.Up:
                    MoveMediaBrowserSelection(-1);
                    return true;
                case Key.Down:
                    MoveMediaBrowserSelection(1);
                    return true;
                case Key.Left:
                    _mediaBrowserTargetDrive = Math.Max(8, _mediaBrowserTargetDrive - 1);
                    return true;
                case Key.Right:
                    _mediaBrowserTargetDrive = Math.Min(11, _mediaBrowserTargetDrive + 1);
                    return true;
                case Key.Number8:
                    _mediaBrowserTargetDrive = 8;
                    return true;
                case Key.Number9:
                    _mediaBrowserTargetDrive = 9;
                    return true;
                case Key.Number0:
                    _mediaBrowserTargetDrive = 10;
                    return true;
                case Key.Number1:
                    _mediaBrowserTargetDrive = 11;
                    return true;
                case Key.BackSpace:
                    NavigateMediaBrowserUp();
                    return true;
                case Key.Enter:
                    ActivateMediaBrowserSelection();
                    return true;
                case Key.Escape:
                    _mediaBrowserVisible = false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Draws audio overlay.
        /// </summary>
        private void DrawAudioOverlay(float sidMasterVolume, float sidNoiseLevel, SidChipModel sidChipModel, JoystickPort joystickPort, MountedMediaInfo mountedMediaInfo)
        {
            int overlayWidth = 280;
            int overlayHeight = 236;
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = 22;

            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 12, 16, 28, 215);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 182, 214, 108);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 182, 214, 108);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 182, 214, 108);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 182, 214, 108);

            DrawOverlayText(overlayX + 16, overlayY + 14, "SETTINGS", 2, 240, 248, 255);
            DrawOverlayText(overlayX + 16, overlayY + 32, "F9 TURBO  F10 CLOSE  F11 FULLSCREEN", 1, 192, 210, 225);
            DrawAudioOverlayMenu(overlayX + 18, overlayY + 52, overlayWidth - 36, sidMasterVolume, sidNoiseLevel, sidChipModel, joystickPort, mountedMediaInfo);
            DrawOverlayText(overlayX + 18, overlayY + 212, "MOUNTED " + FormatOverlayValue(mountedMediaInfo.DisplayName, 24), 1, 232, 238, 244);
            DrawOverlayText(overlayX + 18, overlayY + 222, "STATUS  " + FormatOverlayValue(_overlayStatusText, 24), 1, 182, 214, 108);

            if (_mediaBrowserVisible)
            {
                DrawMediaBrowserOverlay(overlayX + 12, overlayY + 44, overlayWidth - 24, 158);
            }

            if (_resetConfirmVisible)
            {
                DrawResetConfirmOverlay(overlayX + 42, overlayY + 80, 196, 84, mountedMediaInfo.HasMedia);
            }
        }

        /// <summary>
        /// Draws audio overlay menu.
        /// </summary>
        private void DrawAudioOverlayMenu(int x, int y, int width, float sidMasterVolume, float sidNoiseLevel, SidChipModel sidChipModel, JoystickPort joystickPort, MountedMediaInfo mountedMediaInfo)
        {
            ClampAudioOverlayScroll();

            for (int row = 0; row < AudioOverlayVisibleRows; row++)
            {
                int menuIndex = _audioOverlayScroll + row;
                if (menuIndex >= AudioOverlayItemCount)
                {
                    break;
                }

                int itemY = y + (row * AudioOverlayRowSpacing);
                DrawAudioOverlayMenuItem(menuIndex, x, itemY, sidMasterVolume, sidNoiseLevel, sidChipModel, joystickPort, mountedMediaInfo);
            }

            if (AudioOverlayItemCount > AudioOverlayVisibleRows)
            {
                DrawAudioOverlayScrollBar(x + width - 6, y + 2, (AudioOverlayVisibleRows * AudioOverlayRowSpacing) - 8);
            }
        }

        /// <summary>
        /// Draws audio overlay menu item.
        /// </summary>
        private void DrawAudioOverlayMenuItem(int menuIndex, int x, int y, float sidMasterVolume, float sidNoiseLevel, SidChipModel sidChipModel, JoystickPort joystickPort, MountedMediaInfo mountedMediaInfo)
        {
            switch (menuIndex)
            {
                case 0:
                    DrawOverlayItem(x, y, "MASTER VOLUME", sidMasterVolume / 1.5f, FormatHostVolume(sidMasterVolume), "LOW", "HIGH", _audioOverlaySelection == menuIndex);
                    break;
                case 1:
                    DrawOverlayItem(x, y, "NOISE LEVEL", sidNoiseLevel, FormatPercent(sidNoiseLevel), "SOFT", "HARSH", _audioOverlaySelection == menuIndex);
                    break;
                case 2:
                    DrawOverlayItem(x, y, "SID MODEL", sidChipModel == SidChipModel.Mos8580 ? 1.0f : 0.0f, sidChipModel == SidChipModel.Mos6581 ? "6581" : "8580", "6581", "8580", _audioOverlaySelection == menuIndex);
                    break;
                case 3:
                    DrawOverlayItem(x, y, "JOYSTICK", GetJoystickPortFill(joystickPort), FormatJoystickPort(joystickPort), "PORT 1", "BOTH", _audioOverlaySelection == menuIndex);
                    break;
                case 4:
                    DrawOverlayActionItem(x, y, "MEDIA", mountedMediaInfo.HasMedia ? mountedMediaInfo.ShortLabel : "BROWSE", _audioOverlaySelection == menuIndex);
                    break;
                case 5:
                    DrawOverlayItem(x, y, "DISPLAY", _windowFullscreen ? 1.0f : 0.0f, _windowFullscreen ? "FULLSCREEN" : "WINDOW", "WINDOW", "FULL", _audioOverlaySelection == menuIndex);
                    break;
                case 6:
                    DrawOverlayItem(x, y, "TURBO", _turboMode ? 1.0f : 0.0f, _turboMode ? "ON" : "OFF", "OFF", "MAX", _audioOverlaySelection == menuIndex);
                    break;
                case 7:
                    DrawOverlayActionItem(x, y, "RESET", "YES/NO", _audioOverlaySelection == menuIndex);
                    break;
            }
        }

        /// <summary>
        /// Draws audio overlay scroll bar.
        /// </summary>
        private void DrawAudioOverlayScrollBar(int x, int y, int height)
        {
            DrawFilledRectangle(x, y, 3, height, 36, 46, 62);

            int maxScroll = Math.Max(1, AudioOverlayItemCount - AudioOverlayVisibleRows);
            int thumbHeight = Math.Max(20, (height * AudioOverlayVisibleRows) / AudioOverlayItemCount);
            int thumbTravel = Math.Max(0, height - thumbHeight);
            int thumbY = y + ((_audioOverlayScroll * thumbTravel) / maxScroll);
            DrawFilledRectangle(x, thumbY, 3, thumbHeight, 182, 214, 108);
        }

        /// <summary>
        /// Gets the next joystick port value.
        /// </summary>
        private static JoystickPort GetNextJoystickPort(JoystickPort joystickPort)
        {
            switch (joystickPort)
            {
                case JoystickPort.Port2:
                    return JoystickPort.Port1;
                case JoystickPort.Port1:
                    return JoystickPort.Both;
                default:
                    return JoystickPort.Port2;
            }
        }

        /// <summary>
        /// Formats joystick port.
        /// </summary>
        private static string FormatJoystickPort(JoystickPort joystickPort)
        {
            switch (joystickPort)
            {
                case JoystickPort.Port1:
                    return "PORT 1";
                case JoystickPort.Both:
                    return "BOTH";
                default:
                    return "PORT 2";
            }
        }

        /// <summary>
        /// Gets the joystick port fill value.
        /// </summary>
        private static float GetJoystickPortFill(JoystickPort joystickPort)
        {
            switch (joystickPort)
            {
                case JoystickPort.Port1:
                    return 0.0f;
                case JoystickPort.Both:
                    return 0.5f;
                default:
                    return 1.0f;
            }
        }

        /// <summary>
        /// Gets the media fill value.
        /// </summary>
        private static float GetMediaFill(MountedMediaInfo mountedMediaInfo)
        {
            if (mountedMediaInfo == null || !mountedMediaInfo.HasMedia)
            {
                return 0.0f;
            }

            return mountedMediaInfo.Kind == MountedMediaKind.D64 ? 0.5f : 1.0f;
        }

        /// <summary>
        /// Formats overlay value.
        /// </summary>
        private static string FormatOverlayValue(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "NONE";
            }

            string upper = value.ToUpperInvariant();
            if (upper.Length <= maxLength)
            {
                return upper;
            }

            return upper.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        /// <summary>
        /// Opens media browser.
        /// </summary>
        private void OpenMediaBrowser()
        {
            try
            {
                _resetConfirmVisible = false;
                _mediaBrowserCurrentDirectory = ResolveInitialMediaBrowserDirectory();
                _mediaBrowserSelection = 0;
                _mediaBrowserScroll = 0;
                ReloadMediaBrowserEntries();
                _mediaBrowserVisible = true;
                _overlayStatusText = "BROWSER READY";
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _mediaBrowserVisible = false;
                _overlayStatusText = "BROWSER FAILED";
            }
        }

        /// <summary>
        /// Opens reset confirmation.
        /// </summary>
        private void OpenResetConfirmation()
        {
            OpenConfirmation(ConfirmationAction.Reset);
        }

        /// <summary>
        /// Opens confirmation.
        /// </summary>
        private void OpenConfirmation(ConfirmationAction confirmationAction)
        {
            _mediaBrowserVisible = false;
            _resetConfirmVisible = true;
            _resetConfirmYesSelected = true;
            _confirmationAction = confirmationAction;
            _overlayStatusText = "CONFIRM RESET";
        }

        /// <summary>
        /// Handles the clear confirmation state operation.
        /// </summary>
        private void ClearConfirmationState()
        {
            _resetConfirmVisible = false;
            _resetConfirmYesSelected = true;
            _confirmationAction = ConfirmationAction.None;
        }

        /// <summary>
        /// Gets the confirmation canceled status text value.
        /// </summary>
        private string GetConfirmationCanceledStatusText()
        {
            return "RESET CANCELED";
        }

        /// <summary>
        /// Executes confirmation action.
        /// </summary>
        private void ExecuteConfirmationAction()
        {
            ConfirmationAction confirmationAction = _confirmationAction;
            ClearConfirmationState();

            switch (confirmationAction)
            {
                case ConfirmationAction.Reset:
                    PerformSystemReset();
                    break;
                default:
                    _overlayStatusText = "NO ACTION";
                    break;
            }
        }

        /// <summary>
        /// Handles the perform system reset operation.
        /// </summary>
        private void PerformSystemReset()
        {
            StopEmulation();

            MountedMediaInfo mountedMedia = _system.MountedMedia;
            Dictionary<int, string> mountedDrivePaths = _system.GetMountedDriveHostPaths();
            float sidMasterVolume = _system.SidMasterVolume;
            float sidNoiseLevel = _system.SidNoiseLevel;
            SidChipModel sidChipModel = _system.CurrentSidChipModel;
            JoystickPort joystickPort = _system.CurrentJoystickPort;
            bool enableKernalIecHooks = _system.EnableKernalIecHooks;

            C64System oldSystem = _system;
            oldSystem.Dispose();
            CreateSystemInstance();
            _system.SetSidMasterVolume(sidMasterVolume);
            _system.SetSidNoiseLevel(sidNoiseLevel);
            _system.SetSidChipModel(sidChipModel);
            _system.SetJoystickPort(joystickPort);
            _system.EnableKernalIecHooks = enableKernalIecHooks;

            _turboMode = false;
            _turboTimingResetPending = false;
            _smoothedMhz = 0.0;
            _driveFooterVisibility = 0.0;
            _driveFooterIdleSeconds = 0.0;
            _driveFooterPulsePhase = 0.0;
            _turboToastSecondsRemaining = 0.0;
            _overlayStatusText = ReloadMountedMediaAfterPowerCycle(mountedMedia, mountedDrivePaths);
            StartEmulation();
        }

        /// <summary>
        /// Reloads host-side media into a freshly created machine after a power-cycle reset.
        /// </summary>
        private string ReloadMountedMediaAfterPowerCycle(
            MountedMediaInfo mountedMedia,
            Dictionary<int, string> mountedDrivePaths)
        {
            string statusText = "RESET COMPLETE";
            if (mountedDrivePaths != null)
            {
                foreach (KeyValuePair<int, string> mountedDrivePath in mountedDrivePaths)
                {
                    if (string.IsNullOrWhiteSpace(mountedDrivePath.Value) || !File.Exists(mountedDrivePath.Value))
                    {
                        statusText = "RESET MEDIA FAILED";
                        continue;
                    }

                    string mountStatus = _system.MountMedia(mountedDrivePath.Value, mountedDrivePath.Key);
                    statusText = mountStatus.StartsWith("DISK MOUNTED", StringComparison.OrdinalIgnoreCase)
                        ? "RESET MEDIA RELOADED"
                        : mountStatus;
                }
            }

            if (mountedMedia != null &&
                mountedMedia.Kind == MountedMediaKind.Prg &&
                !string.IsNullOrWhiteSpace(mountedMedia.HostPath) &&
                File.Exists(mountedMedia.HostPath))
            {
                string mountStatus = _system.MountMedia(mountedMedia.HostPath);
                statusText = string.Equals(mountStatus, "PRG LOADED", StringComparison.OrdinalIgnoreCase)
                    ? "RESET MEDIA RELOADED"
                    : mountStatus;
            }

            return statusText;
        }

        /// <summary>
        /// Handles the resolve initial media browser directory operation.
        /// </summary>
        private string ResolveInitialMediaBrowserDirectory()
        {
            MountedMediaInfo mountedMedia = _system.MountedMedia;
            if (mountedMedia != null && !string.IsNullOrWhiteSpace(mountedMedia.HostPath))
            {
                try
                {
                    if (Directory.Exists(mountedMedia.HostPath))
                    {
                        return mountedMedia.HostPath;
                    }

                    if (File.Exists(mountedMedia.HostPath))
                    {
                        string mountedDirectory = Path.GetDirectoryName(mountedMedia.HostPath);
                        if (!string.IsNullOrWhiteSpace(mountedDirectory) && Directory.Exists(mountedDirectory))
                        {
                            return mountedDirectory;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }

            string currentDirectory = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            {
                return currentDirectory;
            }

            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsDirectory) && Directory.Exists(documentsDirectory))
            {
                return documentsDirectory;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// Handles the reload media browser entries operation.
        /// </summary>
        private void ReloadMediaBrowserEntries()
        {
            var entries = new List<MediaBrowserEntry>();
            try
            {
                if (!string.IsNullOrWhiteSpace(_mediaBrowserCurrentDirectory))
                {
                    string parent = Directory.GetParent(_mediaBrowserCurrentDirectory) != null
                        ? Directory.GetParent(_mediaBrowserCurrentDirectory).FullName
                        : null;
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        entries.Add(new MediaBrowserEntry(parent, "UP", true, true));
                    }

                    string[] directories = Directory.GetDirectories(_mediaBrowserCurrentDirectory, "*", SearchOption.TopDirectoryOnly);
                    Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                    foreach (string directory in directories)
                    {
                        entries.Add(new MediaBrowserEntry(directory, "DIR " + Path.GetFileName(directory), true, false));
                    }

                    var files = new List<string>();
                    files.AddRange(Directory.GetFiles(_mediaBrowserCurrentDirectory, "*.prg", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(_mediaBrowserCurrentDirectory, "*.d64", SearchOption.TopDirectoryOnly));
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in files)
                    {
                        string extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                        string fileName = Path.GetFileName(file).ToUpperInvariant();
                        entries.Add(new MediaBrowserEntry(file, extension + " " + fileName, false, false));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _overlayStatusText = "BROWSER FAILED";
            }

            _mediaBrowserEntries = entries;
            if (_mediaBrowserEntries.Count == 0)
            {
                _mediaBrowserEntries.Add(new MediaBrowserEntry(string.Empty, "NO PRG OR D64 FILES", false, false));
            }

            _mediaBrowserSelection = Math.Max(0, Math.Min(_mediaBrowserSelection, _mediaBrowserEntries.Count - 1));
            ClampMediaBrowserScroll();
        }

        /// <summary>
        /// Handles the move media browser selection operation.
        /// </summary>
        private void MoveMediaBrowserSelection(int delta)
        {
            if (_mediaBrowserEntries.Count == 0)
            {
                return;
            }

            _mediaBrowserSelection += delta;
            if (_mediaBrowserSelection < 0)
            {
                _mediaBrowserSelection = 0;
            }

            if (_mediaBrowserSelection >= _mediaBrowserEntries.Count)
            {
                _mediaBrowserSelection = _mediaBrowserEntries.Count - 1;
            }

            ClampMediaBrowserScroll();
        }

        /// <summary>
        /// Handles the move audio overlay selection operation.
        /// </summary>
        private void MoveAudioOverlaySelection(int delta)
        {
            _audioOverlaySelection += delta;
            if (_audioOverlaySelection < 0)
            {
                _audioOverlaySelection = 0;
            }

            if (_audioOverlaySelection >= AudioOverlayItemCount)
            {
                _audioOverlaySelection = AudioOverlayItemCount - 1;
            }

            ClampAudioOverlayScroll();
        }

        /// <summary>
        /// Handles the navigate media browser up operation.
        /// </summary>
        private void NavigateMediaBrowserUp()
        {
            if (!string.IsNullOrWhiteSpace(_mediaBrowserCurrentDirectory))
            {
                DirectoryInfo parent = Directory.GetParent(_mediaBrowserCurrentDirectory);
                if (parent != null)
                {
                    _mediaBrowserCurrentDirectory = parent.FullName;
                    _mediaBrowserSelection = 0;
                    _mediaBrowserScroll = 0;
                    ReloadMediaBrowserEntries();
                    return;
                }
            }

            _mediaBrowserVisible = false;
        }

        /// <summary>
        /// Handles the activate media browser selection operation.
        /// </summary>
        private void ActivateMediaBrowserSelection()
        {
            if (_mediaBrowserEntries.Count == 0)
            {
                return;
            }

            MediaBrowserEntry entry = _mediaBrowserEntries[_mediaBrowserSelection];
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                return;
            }

            if (entry.IsDirectory)
            {
                _mediaBrowserCurrentDirectory = entry.Path;
                _mediaBrowserSelection = 0;
                _mediaBrowserScroll = 0;
                ReloadMediaBrowserEntries();
                return;
            }

            _overlayStatusText = _system.MountMedia(entry.Path, _mediaBrowserTargetDrive);
            _mediaBrowserVisible = false;
        }

        /// <summary>
        /// Handles the clamp media browser scroll operation.
        /// </summary>
        private void ClampMediaBrowserScroll()
        {
            if (_mediaBrowserSelection < _mediaBrowserScroll)
            {
                _mediaBrowserScroll = _mediaBrowserSelection;
            }

            int maxVisibleStart = Math.Max(0, _mediaBrowserEntries.Count - MediaBrowserVisibleRows);
            int visibleEnd = _mediaBrowserScroll + MediaBrowserVisibleRows - 1;
            if (_mediaBrowserSelection > visibleEnd)
            {
                _mediaBrowserScroll = _mediaBrowserSelection - (MediaBrowserVisibleRows - 1);
            }

            if (_mediaBrowserScroll > maxVisibleStart)
            {
                _mediaBrowserScroll = maxVisibleStart;
            }
        }

        /// <summary>
        /// Handles the clamp audio overlay scroll operation.
        /// </summary>
        private void ClampAudioOverlayScroll()
        {
            if (_audioOverlaySelection < _audioOverlayScroll)
            {
                _audioOverlayScroll = _audioOverlaySelection;
            }

            int maxVisibleStart = Math.Max(0, AudioOverlayItemCount - AudioOverlayVisibleRows);
            int visibleEnd = _audioOverlayScroll + AudioOverlayVisibleRows - 1;
            if (_audioOverlaySelection > visibleEnd)
            {
                _audioOverlayScroll = _audioOverlaySelection - (AudioOverlayVisibleRows - 1);
            }

            if (_audioOverlayScroll > maxVisibleStart)
            {
                _audioOverlayScroll = maxVisibleStart;
            }

            if (_audioOverlayScroll < 0)
            {
                _audioOverlayScroll = 0;
            }
        }

        /// <summary>
        /// Handles the captures media browser key operation.
        /// </summary>
        private bool CapturesMediaBrowserKey(Key key)
        {
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.Enter:
                case Key.Escape:
                case Key.BackSpace:
                case Key.Number0:
                case Key.Number1:
                case Key.Number8:
                case Key.Number9:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Draws media browser overlay.
        /// </summary>
        private void DrawMediaBrowserOverlay(int x, int y, int width, int height)
        {
            DrawFilledRectangleWithAlpha(x, y, width, height, 8, 10, 18, 230);
            DrawLine(x, y, x + width - 1, y, 115, 142, 196);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 115, 142, 196);
            DrawLine(x, y, x, y + height - 1, 115, 142, 196);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, 115, 142, 196);

            DrawOverlayText(x + 10, y + 8, "MEDIA BROWSER", 1, 240, 248, 255);
            DrawOverlayText(x + 10, y + 18, "ENTER MOUNT  LEFT/RIGHT OR 8/9/0/1 DRIVE", 1, 192, 210, 225);
            DrawOverlayText(x + 10, y + 30, "TARGET DRIVE " + _mediaBrowserTargetDrive, 1, 182, 214, 108);
            DrawOverlayText(x + 10, y + 42, "DIR " + FormatOverlayPath(_mediaBrowserCurrentDirectory, 35), 1, 182, 214, 108);

            int rowY = y + 58;
            for (int row = 0; row < MediaBrowserVisibleRows; row++)
            {
                int index = _mediaBrowserScroll + row;
                if (index >= _mediaBrowserEntries.Count)
                {
                    break;
                }

                MediaBrowserEntry entry = _mediaBrowserEntries[index];
                bool selected = index == _mediaBrowserSelection;
                byte red = selected ? (byte)255 : (byte)232;
                byte green = selected ? (byte)243 : (byte)238;
                byte blue = selected ? (byte)168 : (byte)244;
                DrawOverlayText(x + 10, rowY + (row * 11), (selected ? "> " : "  ") + FormatOverlayValue(entry.DisplayName, 37), 1, red, green, blue);
            }
        }

        /// <summary>
        /// Draws reset confirm overlay.
        /// </summary>
        private void DrawResetConfirmOverlay(int x, int y, int width, int height, bool hasMountedMedia)
        {
            DrawFilledRectangleWithAlpha(x, y, width, height, 8, 10, 18, 238);
            DrawLine(x, y, x + width - 1, y, 255, 243, 168);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 255, 243, 168);
            DrawLine(x, y, x, y + height - 1, 255, 243, 168);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, 255, 243, 168);

            string title;
            string infoLine1;
            string infoLine2;
            title = "RESET C64";
            infoLine1 = hasMountedMedia ? "MOUNTED MEDIA RELOADS" : "NO MEDIA INSERTED";
            infoLine2 = "SYSTEM RESTARTS";

            DrawOverlayText(x + 14, y + 10, title, 2, 255, 243, 168);
            DrawOverlayText(x + 14, y + 30, infoLine1, 1, 232, 238, 244);
            DrawOverlayText(x + 14, y + 40, infoLine2, 1, 232, 238, 244);
            DrawOverlayText(x + 14, y + 54, _resetConfirmYesSelected ? "> YES     NO" : "  YES   > NO", 2, 240, 248, 255);
            DrawOverlayText(x + 14, y + 72, "ENTER ACCEPT  ESC CANCEL", 1, 192, 210, 225);
        }

        /// <summary>
        /// Formats overlay path.
        /// </summary>
        private static string FormatOverlayPath(string path, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "NONE";
            }

            string normalized = path.Replace('\\', '/').ToUpperInvariant();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return "..." + normalized.Substring(normalized.Length - Math.Max(0, maxLength - 3));
        }

        /// <summary>
        /// Draws overlay item.
        /// </summary>
        private void DrawOverlayItem(int x, int y, string label, float normalizedValue, string valueText, string minimumLabel, string maximumLabel, bool selected)
        {
            byte labelRed = selected ? (byte)255 : (byte)210;
            byte labelGreen = selected ? (byte)243 : (byte)220;
            byte labelBlue = selected ? (byte)168 : (byte)210;
            byte barRed = selected ? (byte)182 : (byte)110;
            byte barGreen = selected ? (byte)214 : (byte)130;
            byte barBlue = selected ? (byte)108 : (byte)160;

            DrawOverlayText(x, y, (selected ? "> " : "  ") + label, 2, labelRed, labelGreen, labelBlue);
            DrawOverlayText(x + 192, y, valueText, 2, 240, 248, 255);

            int barX = x + 6;
            int barY = y + 18;
            int barWidth = 188;
            int fillWidth = (int)(Math.Max(0.0f, Math.Min(1.0f, normalizedValue)) * barWidth);
            DrawFilledRectangle(barX, barY, barWidth, 6, 36, 46, 62);
            DrawFilledRectangle(barX, barY, fillWidth, 6, barRed, barGreen, barBlue);
        }

        /// <summary>
        /// Draws overlay action item.
        /// </summary>
        private void DrawOverlayActionItem(int x, int y, string label, string valueText, bool selected)
        {
            byte labelRed = selected ? (byte)255 : (byte)210;
            byte labelGreen = selected ? (byte)243 : (byte)220;
            byte labelBlue = selected ? (byte)168 : (byte)210;

            DrawOverlayText(x, y, (selected ? "> " : "  ") + label, 2, labelRed, labelGreen, labelBlue);
            DrawOverlayText(x + 150, y, valueText, 2, 240, 248, 255);
            DrawFilledRectangle(x + 6, y + 18, 188, 6, 36, 46, 62);
        }

        /// <summary>
        /// Formats percent.
        /// </summary>
        private static string FormatPercent(float normalizedValue)
        {
            int percent = (int)Math.Round(Math.Max(0.0f, Math.Min(1.0f, normalizedValue)) * 100.0f);
            return percent.ToString() + "%";
        }

        /// <summary>
        /// Formats host volume.
        /// </summary>
        private static string FormatHostVolume(float volume)
        {
            int percent = (int)Math.Round(Math.Max(0.0f, Math.Min(1.5f, volume)) * 100.0f);
            return percent.ToString() + "%";
        }

        /// <summary>
        /// Gets the desired cycle value.
        /// </summary>
        private long GetDesiredCycle()
        {
            return _emulationBaseCycle + (long)(_emulationStopwatch.ElapsedTicks * _cyclesPerStopwatchTick);
        }

        /// <summary>
        /// Handles the wait until next cycle operation.
        /// </summary>
        private void WaitUntilNextCycle(CancellationToken cancellationToken, long currentCycle)
        {
            long nextCycle = currentCycle + WaitCycleQuantum;
            while (!cancellationToken.IsCancellationRequested)
            {
                double desiredCycles = _emulationBaseCycle + (_emulationStopwatch.ElapsedTicks * _cyclesPerStopwatchTick);
                double remainingCycles = nextCycle - desiredCycles;
                if (remainingCycles <= 0.0)
                {
                    return;
                }

                double remainingSeconds = remainingCycles * _secondsPerCycle;
                if (remainingSeconds >= SleepThresholdSeconds)
                {
                    int sleepMilliseconds = (int)((remainingSeconds - SleepSafetyMarginSeconds) * 1000.0);
                    if (sleepMilliseconds > 0)
                    {
                        Thread.Sleep(sleepMilliseconds);
                        continue;
                    }
                }

                if (remainingSeconds >= 0.00025)
                {
                    Thread.Sleep(0);
                    continue;
                }

                Thread.SpinWait(32);
            }
        }

        /// <summary>
        /// Draws overlay text.
        /// </summary>
        private void DrawOverlayText(int x, int y, string text, int scale, byte red, byte green, byte blue)
        {
            int cursorX = x;
            foreach (char rawCharacter in text)
            {
                char character = char.ToUpperInvariant(rawCharacter);
                byte[] glyph;
                if (!OverlayFont.TryGetValue(character, out glyph))
                {
                    glyph = OverlayFont[' '];
                }

                for (int row = 0; row < glyph.Length; row++)
                {
                    byte bits = glyph[row];
                    for (int column = 0; column < 5; column++)
                    {
                        if ((bits & (1 << (4 - column))) == 0)
                        {
                            continue;
                        }

                        DrawFilledRectangle(cursorX + (column * scale), y + (row * scale), scale, scale, red, green, blue);
                    }
                }

                cursorX += (6 * scale);
            }
        }

        /// <summary>
        /// Gets the overlay text width value.
        /// </summary>
        private static int GetOverlayTextWidth(string text, int scale)
        {
            return text.Length * 6 * scale;
        }

        /// <summary>
        /// Creates overlay font.
        /// </summary>
        private static Dictionary<char, byte[]> CreateOverlayFont()
        {
            return new Dictionary<char, byte[]>
            {
                [' '] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                ['%'] = new byte[] { 0x19, 0x19, 0x02, 0x04, 0x08, 0x13, 0x13 },
                ['+'] = new byte[] { 0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00 },
                ['-'] = new byte[] { 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00 },
                ['/'] = new byte[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0x00, 0x00 },
                ['0'] = new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E },
                ['1'] = new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E },
                ['2'] = new byte[] { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F },
                ['3'] = new byte[] { 0x1E, 0x01, 0x01, 0x06, 0x01, 0x01, 0x1E },
                ['4'] = new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 },
                ['5'] = new byte[] { 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E },
                ['6'] = new byte[] { 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E },
                ['7'] = new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 },
                ['8'] = new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E },
                ['9'] = new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C },
                ['>'] = new byte[] { 0x10, 0x08, 0x04, 0x02, 0x04, 0x08, 0x10 },
                ['A'] = new byte[] { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
                ['B'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E },
                ['C'] = new byte[] { 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E },
                ['D'] = new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E },
                ['E'] = new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F },
                ['F'] = new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 },
                ['G'] = new byte[] { 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0E },
                ['H'] = new byte[] { 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
                ['I'] = new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F },
                ['J'] = new byte[] { 0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0E },
                ['K'] = new byte[] { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 },
                ['L'] = new byte[] { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F },
                ['M'] = new byte[] { 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11 },
                ['N'] = new byte[] { 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11 },
                ['O'] = new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
                ['P'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 },
                ['Q'] = new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D },
                ['R'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11 },
                ['S'] = new byte[] { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E },
                ['T'] = new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 },
                ['U'] = new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
                ['V'] = new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04 },
                ['W'] = new byte[] { 0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A },
                ['X'] = new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11 },
                ['Y'] = new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04 },
                ['Z'] = new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F },
                ['.'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C },
                [':'] = new byte[] { 0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00 },
                ['_'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F }
            };
        }

        /// <summary>
        /// Represents the media browser entry component.
        /// </summary>
        private sealed class MediaBrowserEntry
        {
            /// <summary>
            /// Initializes a new MediaBrowserEntry instance.
            /// </summary>
            public MediaBrowserEntry(string path, string displayName, bool isDirectory, bool isParent)
            {
                Path = path;
                DisplayName = displayName ?? string.Empty;
                IsDirectory = isDirectory;
                IsParent = isParent;
            }

            /// <summary>
            /// Gets the host filesystem path.
            /// </summary>
            public string Path { get; }

            /// <summary>
            /// Gets the user-facing display name.
            /// </summary>
            public string DisplayName { get; }

            /// <summary>
            /// Gets whether the entry represents a directory.
            /// </summary>
            public bool IsDirectory { get; }

            /// <summary>
            /// Gets whether the entry navigates to the parent directory.
            /// </summary>
            public bool IsParent { get; }
        }
    }
}
