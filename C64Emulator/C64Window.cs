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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using C64Emulator.Core;
using C64Emulator.Network;
using OpenTK.Input;
using SharpPixels;
using Hat = OpenTK.Windowing.Common.Input.Hat;
using JoystickState = OpenTK.Windowing.GraphicsLibraryFramework.JoystickState;

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

        /// <summary>
        /// Lists the supported presentation filters.
        /// </summary>
        private enum VideoFilterMode
        {
            Sharp,
            Crt,
            Tv
        }

        /// <summary>
        /// Lists the optional source-image upscalers applied before the presentation filter.
        /// </summary>
        private enum VideoUpscaleMode
        {
            None,
            Scale2x,
            Scale3x,
            Hq2x,
            Hq3x,
            Hq4x
        }

        /// <summary>
        /// Lists the frontend render-loop frame caps.
        /// </summary>
        private enum RenderFrameLimitMode
        {
            Hz60,
            Hz120,
            Unlimited
        }

        /// <summary>
        /// Lists the supported reset behaviors.
        /// </summary>
        private enum ResetMode
        {
            Warm,
            Reload,
            Power
        }

        /// <summary>
        /// Lists the network session state owned by the frontend.
        /// </summary>
        /// <remarks>
        /// Local means the normal emulator is running. Host means this instance still
        /// runs the emulator and streams it. Client means local emulation is stopped and
        /// the window only presents the remote stream plus local UI overlays.
        /// </remarks>
        private enum NetworkSessionMode
        {
            Local,
            Host,
            Client
        }

        /// <summary>
        /// Lists the network action to retry after a user accepts a changed TLS pin.
        /// </summary>
        private enum NetworkTlsRetryAction
        {
            None,
            StartServer,
            StartClient
        }

        /// <summary>
        /// Lists the top-level F10 menu entries.
        /// </summary>
        private enum MainMenuItem
        {
            Settings = 0,
            Network = 1,
            Media = 2,
            Reset = 3
        }

        private enum ControllerMapAction
        {
            Up = 0,
            Down = 1,
            Left = 2,
            Right = 3,
            Fire = 4,
            MenuSelect = 5,
            MenuBack = 6,
            MainMenu = 7,
            Turbo = 8,
            SaveStates = 9
        }

        private const int MaxCycleBatch = 2048;
        private const int NormalCycleBatch = 512;
        private const long WaitCycleQuantum = 256;
        private const double SleepThresholdSeconds = 0.002;
        private const double SleepSafetyMarginSeconds = 0.0005;
        private const int MainMenuItemCount = 4;
        private const float VolumeStep = 0.05f;
        private const float NoiseStep = 0.05f;
        private const int AudioOverlayItemCount = 23;
        private const int AudioOverlayVisibleRows = 4;
        private const int AudioOverlayRowSpacing = 36;
        private const int ControllerMapActionCount = 10;
        private const float GamepadAxisDeadZone = 0.45f;
        private const string DefaultGamepadUpBindings = "Button2;Axis4-";
        private const string DefaultGamepadDownBindings = "Axis4+";
        private const string DefaultGamepadLeftBindings = "Axis0-";
        private const string DefaultGamepadRightBindings = "Axis0+";
        private const string DefaultGamepadFireBindings = "Button1";
        private const string DefaultGamepadMenuSelectBindings = "Button1";
        private const string DefaultGamepadMenuBackBindings = "Button2";
        private const string DefaultGamepadMainMenuBindings = "Button7;Button9";
        private const string DefaultGamepadTurboBindings = "";
        private const string DefaultGamepadSaveStatesBindings = "Button6;Button8";
        private const int ControllerMappingVisibleRows = 7;
        private const int ControllerMappingRowSpacing = 14;
        private const double ControllerActionInitialRepeatSeconds = 0.35;
        private const double ControllerActionRepeatSeconds = 0.075;
        private const int StandardC64ContentLeft = 41;
        private const int StandardC64ContentTop = 37;
        private const int StandardC64ContentWidth = 320;
        private const int StandardC64ContentHeight = 200;
        // The network overlay is a single flat menu split visually into server rows and
        // client rows. The constants keep selection/wrapping independent of labels.
        private const int NetworkOverlayItemCount = 17;
        private const int NetworkOverlayServerItemCount = 9;
        private const int NetworkOverlayVisibleClientRows = 2;
        // Remote clients resend unchanged joystick state periodically so the host can
        // recover from a dropped packet or a stale state after a quick reconnect.
        private const double RemoteInputRefreshSeconds = 0.05;
        private const int MediaBrowserListTopOffset = 58;
        private const int MediaBrowserListBottomPadding = 10;
        private const int MediaBrowserRowSpacing = 11;
        private const int SaveOverlayVisibleRows = 12;
        private const double DriveFooterVisibleHoldSeconds = 1.2;
        private const double DriveFooterFadeOutSeconds = 0.6;
        private const double TurboToastHoldSeconds = 0.85;
        private const double TurboToastFadeSeconds = 0.35;
        private const int Mouse1351CounterMask = 0x3F;
        private const int Mouse1351MaxDeltaPerEvent = 31;
        private const int Mouse1351MovementScale = 2;
        private static readonly Dictionary<char, byte[]> OverlayFont = CreateOverlayFont();
        private readonly C64Model _model;
        private C64System _system;
        private readonly EmulatorSettings _loadedSettings;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private CancellationTokenSource _emulationCancellation;
        private Stopwatch _emulationStopwatch = new Stopwatch();
        private double _cyclesPerStopwatchTick;
        private double _secondsPerCycle;
        private uint[] _frameSnapshot;
        private int _closeRequested;

        private Task _emulationTask;
        private long _emulationBaseCycle;
        private long _lastRenderedCycle;
        private volatile bool _mainMenuVisible;
        private volatile bool _audioOverlayVisible;
        private bool _mediaBrowserVisible;
        private bool _resetConfirmVisible;
        private bool _networkOverlayVisible;
        private bool _controllerMappingVisible;
        private bool _controllerMappingLearning;
        private bool _controllerLearningBaselineReleased;
        private bool _networkTlsCertificatePromptVisible;
        private volatile bool _saveOverlayVisible;
        private volatile bool _turboMode;
        private volatile bool _turboTimingResetPending;
        private bool _windowFullscreen;
        private bool _gamepadEnabled = true;
        private bool _gamepadConnected;
        private bool _controllerGameInputSuppressedUntilRelease;
        private bool _driveOverlayEnabled = true;
        private bool _debugOverlayVisible;
        private bool _resetConfirmYesSelected = true;
        private ConfirmationAction _confirmationAction;
        private RenderFrameLimitMode _renderFrameLimitMode = RenderFrameLimitMode.Unlimited;
        private VideoFilterMode _videoFilterMode = VideoFilterMode.Sharp;
        private VideoUpscaleMode _videoUpscaleMode = VideoUpscaleMode.None;
        private ResetMode _resetMode = ResetMode.Warm;
        private NetworkSessionMode _networkMode = NetworkSessionMode.Local;
        private Mouse1351Port _mouse1351Port = Mouse1351Port.Off;
        private bool _videoZoomEnabled;
        private byte _lastGamepadJoystickState = 0x1F;
        private byte _lastMouse1351JoystickState = 0x1F;
        private int _mouse1351XCounter = 0x20;
        private int _mouse1351YCounter = 0x20;
        private int _lastMouseX;
        private int _lastMouseY;
        private bool _hasLastMousePosition;
        private bool _mouse1351Captured;
        // Remote client input is kept separate by source and combined with active-low
        // AND semantics before it is sent to the host.
        private byte _remoteKeyboardJoystickState = 0xFF;
        private byte _remoteGamepadJoystickState = 0xFF;
        private byte _lastRemoteJoystickState = 0xFF;
        private readonly HashSet<Key> _localAltCursorKeys = new HashSet<Key>();
        private readonly HashSet<Key> _remoteAltCursorKeys = new HashSet<Key>();
        private readonly HashSet<string> _controllerLearningBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _controllerSuppressedUiBindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool[] _controllerActionWasDown = new bool[ControllerMapActionCount];
        private readonly long[] _controllerActionNextRepeatTicks = new long[ControllerMapActionCount];
        private List<string> _gamepadUpBindings = ParseGamepadBindings(DefaultGamepadUpBindings, DefaultGamepadUpBindings);
        private List<string> _gamepadDownBindings = ParseGamepadBindings(DefaultGamepadDownBindings, DefaultGamepadDownBindings);
        private List<string> _gamepadLeftBindings = ParseGamepadBindings(DefaultGamepadLeftBindings, DefaultGamepadLeftBindings);
        private List<string> _gamepadRightBindings = ParseGamepadBindings(DefaultGamepadRightBindings, DefaultGamepadRightBindings);
        private List<string> _gamepadFireBindings = ParseGamepadBindings(DefaultGamepadFireBindings, DefaultGamepadFireBindings);
        private List<string> _gamepadMenuSelectBindings = ParseGamepadBindings(DefaultGamepadMenuSelectBindings, DefaultGamepadMenuSelectBindings);
        private List<string> _gamepadMenuBackBindings = ParseGamepadBindings(DefaultGamepadMenuBackBindings, DefaultGamepadMenuBackBindings);
        private List<string> _gamepadMainMenuBindings = ParseGamepadBindings(DefaultGamepadMainMenuBindings, DefaultGamepadMainMenuBindings);
        private List<string> _gamepadTurboBindings = ParseGamepadBindings(DefaultGamepadTurboBindings, DefaultGamepadTurboBindings);
        private List<string> _gamepadSaveStatesBindings = ParseGamepadBindings(DefaultGamepadSaveStatesBindings, DefaultGamepadSaveStatesBindings);
        private int _mainMenuSelection;
        private int _audioOverlaySelection;
        private int _audioOverlayScroll;
        private int _controllerMappingSelection;
        private int _controllerMappingScroll;
        private int _networkOverlaySelection;
        private int _networkSelectedClientIndex;
        private NetworkTlsRetryAction _pendingNetworkTlsRetryAction;
        private C64NetTlsPinChange _pendingNetworkTlsPinChange;
        private C64NetTransportMode _networkTransportMode = C64NetTransportMode.Lan;
        private int _networkServerPort = C64NetProtocol.DefaultPort;
        private int _networkClientPort = C64NetProtocol.DefaultPort;
        private int _networkRelayPort = C64NetProtocol.DefaultRelayPort;
        private int _mediaBrowserSelection;
        private int _mediaBrowserScroll;
        private int _mediaBrowserTargetDrive = 8;
        private char _mediaBrowserLastJumpLetter;
        private string _mediaBrowserLastDirectory;
        private string _networkHost = "127.0.0.1";
        private string _networkConnectionId = "c64";
        private string _networkServerPassword = string.Empty;
        private string _networkClientPassword = string.Empty;
        private string _networkPlayerName = "player";
        private string _networkStatusText = "LOCAL";
        private string _networkStatusToastText = string.Empty;
        // HostOverlayStatus is a persistent popup on remote clients, not just a toast.
        // It explains why the streamed C64 image is paused while the host is in a menu.
        private string _networkHostOverlayStatusText = string.Empty;
        private string _lastBroadcastNetworkHostOverlayStatus = string.Empty;
        private long _networkStatusToastUntilTicks;
        private int _saveOverlaySelection;
        private int _saveOverlayScroll;
        private bool _saveDeleteConfirmVisible;
        private bool _saveDeleteConfirmYesSelected = true;
        private string _pendingDeleteSavePath;
        private string _mediaBrowserCurrentDirectory;
        private string _saveOverlayCurrentDirectory;
        private List<MediaBrowserEntry> _mediaBrowserEntries = new List<MediaBrowserEntry>();
        private List<SaveOverlayEntry> _saveOverlayEntries = new List<SaveOverlayEntry>();
        private string _lastLoadedSaveStatePath;
        private string _overlayStatusText = "READY";
        private string _saveOverlayStatusText = "F12 SAVE MENU";
        private string _turboToastText = string.Empty;
        private C64NetClientRole _networkRequestedRole = C64NetClientRole.Player;
        private C64NetServer _networkServer;
        private C64NetClient _networkClient;
        // Network callbacks run on worker threads. These locks protect frame snapshots,
        // client snapshots, and host broadcast copies crossing into the render thread.
        private readonly object _networkFrameSync = new object();
        private readonly object _networkClientsSync = new object();
        private readonly object _networkBroadcastSync = new object();
        private uint[] _networkFramePixels;
        private uint[] _networkPendingFramePixels;
        // Reused host-side buffer: each completed frame is copied once and then encoded
        // once by the server before being shared with all clients.
        private uint[] _networkBroadcastFrameSnapshot;
        private int _networkFrameWidth;
        private int _networkFrameHeight;
        private long _networkFrameId;
        private int _networkPendingFrameWidth;
        private int _networkPendingFrameHeight;
        private long _networkPendingFrameId;
        private long _lastDisplayedNetworkFrameId;
        private readonly HashSet<Key> _networkAppliedKeyboardKeys = new HashSet<Key>();
        // Frame id bookkeeping prevents sending the same completed PAL frame repeatedly
        // when the renderer runs faster than the emulated C64 frame rate.
        private long _lastBroadcastNetworkCompletedFrameId = -1;
        private int _networkFramesReceivedCounter;
        private int _networkReceiveFps;
        private long _networkReceiveFpsWindowStartTicks;
        private long _networkReceiveFpsNextTicks;
        private int _networkFramesSentCounter;
        private int _networkSendFps;
        private long _networkSendFpsWindowStartTicks;
        private long _networkSendFpsNextTicks;
        private double _networkSendKilobytesPerSecond;
        private double _networkReceiveKilobytesPerSecond;
        private long _networkTrafficWindowStartTicks;
        private long _networkTrafficNextTicks;
        private long _networkTrafficLastBytesSent;
        private long _networkTrafficLastBytesReceived;
        private List<C64NetClientSnapshot> _networkClients = new List<C64NetClientSnapshot>();
        private double _smoothedMhz;
        private double _driveFooterVisibility;
        private double _driveFooterIdleSeconds;
        private double _driveFooterPulsePhase;
        private double _turboToastSecondsRemaining;
        private double _remoteInputRefreshAccumulator;

        /// <summary>
        /// Handles the c64 window operation.
        /// </summary>
        public C64Window(C64Model model, string title) : base(model.VisibleWidth, model.VisibleHeight, title)
        {
            _model = model;
            _loadedSettings = EmulatorSettingsStore.Load();
            InitializeSystem();
            ApplyLoadedSettings(_loadedSettings);
            SaveSettings();
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
            _system.AudioBytesGenerated += HandleSystemAudioBytesGenerated;
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

            if (_networkBroadcastFrameSnapshot == null || _networkBroadcastFrameSnapshot.Length != framePixelCount)
            {
                // The server broadcasts completed frames from this reusable buffer to
                // avoid allocating a new host copy every render update.
                _networkBroadcastFrameSnapshot = new uint[framePixelCount];
            }
        }

        /// <summary>
        /// Applies user settings loaded from the AppData settings file.
        /// </summary>
        private void ApplyLoadedSettings(EmulatorSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(settings.EasyFlashImagePath) && File.Exists(settings.EasyFlashImagePath))
            {
                _system.MountMedia(settings.EasyFlashImagePath);
                _system.SetEasyFlashEnabled(settings.EasyFlashEnabled);
            }

            _system.SetReuSize(ParseEnum(settings.ReuSize, ReuMemorySize.K512));
            _system.SetReuEnabled(settings.ReuEnabled);

            _system.SetSidMasterVolume(settings.SidMasterVolume);
            _system.SetSidNoiseLevel(settings.SidNoiseLevel);
            _system.SetSidChipModel(ParseEnum(settings.SidChipModel, SidChipModel.Mos6581));
            _system.SetJoystickPort(ParseEnum(settings.JoystickPort, JoystickPort.Port2));
            _system.SetHostKeyboardLayout(ParseEnum(settings.HostKeyboardLayout, HostKeyboardLayout.En));
            _mouse1351Port = ParseEnum(settings.Mouse1351Port, Mouse1351Port.Off);
            ApplyMouse1351StateToSystem();
            _system.EnableLoadHack = settings.EnableLoadHack;
            _system.ForceSoftwareIecTransport = settings.ForceSoftwareIecTransport;
            _system.EnableInputInjection = settings.EnableInputInjection;

            _renderFrameLimitMode = ParseEnum(settings.RenderFrameLimitMode, RenderFrameLimitMode.Unlimited);
            _videoFilterMode = ParseEnum(settings.VideoFilterMode, VideoFilterMode.Sharp);
            _videoUpscaleMode = ParseEnum(settings.VideoUpscaleMode, VideoUpscaleMode.None);
            _videoZoomEnabled = settings.VideoZoomEnabled;
            _resetMode = ParseEnum(settings.ResetMode, ResetMode.Warm);
            _turboMode = settings.TurboMode;
            _windowFullscreen = settings.Fullscreen;
            _gamepadEnabled = settings.GamepadEnabled;
            _gamepadUpBindings = ParseGamepadBindings(settings.GamepadUpBindings, DefaultGamepadUpBindings);
            _gamepadDownBindings = ParseGamepadBindings(settings.GamepadDownBindings, DefaultGamepadDownBindings);
            _gamepadLeftBindings = ParseGamepadBindings(settings.GamepadLeftBindings, DefaultGamepadLeftBindings);
            _gamepadRightBindings = ParseGamepadBindings(settings.GamepadRightBindings, DefaultGamepadRightBindings);
            _gamepadFireBindings = ParseGamepadBindings(settings.GamepadFireBindings, DefaultGamepadFireBindings);
            _gamepadMenuSelectBindings = ParseGamepadBindings(settings.GamepadMenuSelectBindings, DefaultGamepadMenuSelectBindings);
            _gamepadMenuBackBindings = ParseGamepadBindings(settings.GamepadMenuBackBindings, DefaultGamepadMenuBackBindings);
            _gamepadMainMenuBindings = ParseGamepadBindings(settings.GamepadMainMenuBindings, DefaultGamepadMainMenuBindings);
            _gamepadTurboBindings = ParseGamepadBindings(settings.GamepadTurboBindings, DefaultGamepadTurboBindings);
            _gamepadSaveStatesBindings = ParseGamepadBindings(settings.GamepadSaveStatesBindings, DefaultGamepadSaveStatesBindings);
            _driveOverlayEnabled = settings.DriveOverlayEnabled;
            _mediaBrowserTargetDrive = ClampInt(settings.MediaBrowserTargetDrive, 8, 11);
            _mediaBrowserLastDirectory = NormalizeExistingDirectory(settings.MediaBrowserDirectory);
            // Network UI values are user preferences. Active sessions themselves are not
            // restored automatically because joining/hosting has external side effects.
            _networkTransportMode = ParseEnum(settings.NetworkTransportMode, C64NetTransportMode.Lan);
            _networkServerPort = ClampNetworkPort(settings.NetworkServerPort <= 0 ? C64NetProtocol.DefaultPort : settings.NetworkServerPort);
            _networkClientPort = ClampNetworkPort(settings.NetworkClientPort <= 0 ? C64NetProtocol.DefaultPort : settings.NetworkClientPort);
            _networkRelayPort = ClampNetworkPort(settings.NetworkRelayPort <= 0 ? C64NetProtocol.DefaultRelayPort : settings.NetworkRelayPort);
            _networkHost = string.IsNullOrWhiteSpace(settings.NetworkClientHost) ? "127.0.0.1" : settings.NetworkClientHost.Trim();
            _networkConnectionId = NormalizeNetworkConnectionId(settings.NetworkConnectionId);
            _networkServerPassword = settings.NetworkServerPassword ?? string.Empty;
            _networkClientPassword = settings.NetworkClientPassword ?? string.Empty;
            _networkPlayerName = NormalizeNetworkPlayerName(settings.NetworkPlayerName);
            _networkRequestedRole = ParseEnum(settings.NetworkRequestedRole, C64NetClientRole.Player);

            if (!_gamepadEnabled)
            {
                _lastGamepadJoystickState = 0x1F;
                _gamepadConnected = false;
                ResetControllerActionEdges();
                _system.SetGamepadJoystickState(0x1F);
            }
        }

        /// <summary>
        /// Saves the current host-facing emulator settings to AppData.
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                EmulatorSettingsStore.Save(CreateSettingsSnapshot());
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Creates a settings snapshot without including mounted media paths.
        /// </summary>
        private EmulatorSettings CreateSettingsSnapshot()
        {
            return new EmulatorSettings
            {
                Version = 1,
                SidMasterVolume = _system.SidMasterVolume,
                SidNoiseLevel = _system.SidNoiseLevel,
                SidChipModel = _system.CurrentSidChipModel.ToString(),
                JoystickPort = _system.CurrentJoystickPort.ToString(),
                HostKeyboardLayout = _system.CurrentHostKeyboardLayout.ToString(),
                Mouse1351Port = _mouse1351Port.ToString(),
                RenderFrameLimitMode = _renderFrameLimitMode.ToString(),
                VideoFilterMode = _videoFilterMode.ToString(),
                VideoUpscaleMode = _videoUpscaleMode.ToString(),
                VideoZoomEnabled = _videoZoomEnabled,
                ResetMode = _resetMode.ToString(),
                TurboMode = _turboMode,
                Fullscreen = _windowFullscreen,
                GamepadEnabled = _gamepadEnabled,
                GamepadUpBindings = SerializeGamepadBindings(_gamepadUpBindings),
                GamepadDownBindings = SerializeGamepadBindings(_gamepadDownBindings),
                GamepadLeftBindings = SerializeGamepadBindings(_gamepadLeftBindings),
                GamepadRightBindings = SerializeGamepadBindings(_gamepadRightBindings),
                GamepadFireBindings = SerializeGamepadBindings(_gamepadFireBindings),
                GamepadMenuSelectBindings = SerializeGamepadBindings(_gamepadMenuSelectBindings),
                GamepadMenuBackBindings = SerializeGamepadBindings(_gamepadMenuBackBindings),
                GamepadMainMenuBindings = SerializeGamepadBindings(_gamepadMainMenuBindings),
                GamepadTurboBindings = SerializeGamepadBindings(_gamepadTurboBindings),
                GamepadSaveStatesBindings = SerializeGamepadBindings(_gamepadSaveStatesBindings),
                EnableLoadHack = _system.EnableLoadHack,
                ForceSoftwareIecTransport = _system.ForceSoftwareIecTransport,
                EnableInputInjection = _system.EnableInputInjection,
                DriveOverlayEnabled = _driveOverlayEnabled,
                EasyFlashEnabled = _system.EasyFlashEnabled,
                EasyFlashImagePath = _system.EasyFlashImagePath,
                ReuEnabled = _system.ReuEnabled,
                ReuSize = _system.ReuSize.ToString(),
                MediaBrowserTargetDrive = _mediaBrowserTargetDrive,
                MediaBrowserDirectory = _mediaBrowserLastDirectory ?? string.Empty,
                // Store the network menu fields so host/client setup survives app restarts.
                // Passwords are intentionally plain settings today, matching the simple
                // local emulator settings model.
                NetworkTransportMode = _networkTransportMode.ToString(),
                NetworkServerPort = _networkServerPort,
                NetworkServerPassword = _networkServerPassword ?? string.Empty,
                NetworkClientHost = _networkHost ?? string.Empty,
                NetworkClientPort = _networkClientPort,
                NetworkRelayPort = _networkRelayPort,
                NetworkConnectionId = NormalizeNetworkConnectionId(_networkConnectionId),
                NetworkClientPassword = _networkClientPassword ?? string.Empty,
                NetworkPlayerName = NormalizeNetworkPlayerName(_networkPlayerName),
                NetworkRequestedRole = _networkRequestedRole.ToString()
            };
        }

        private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
            where TEnum : struct
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            TEnum parsed;
            return Enum.TryParse(value, true, out parsed) ? parsed : fallback;
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
                    if (_mainMenuVisible || _networkOverlayVisible || _saveOverlayVisible || _audioOverlayVisible || _mediaBrowserVisible || _resetConfirmVisible)
                    {
                        // All emulator menus pause simulation. The network menu follows
                        // the same rule so the host can safely grant/kick clients without
                        // the game continuing underneath.
                        Thread.Sleep(5);
                        continue;
                    }

                    if (_turboMode)
                    {
                        _system.RunCycles(MaxCycleBatch);
                        // In turbo mode the emulation loop may outrun rendering, so check
                        // for completed frames here as well as in OnUserUpdate.
                        BroadcastNetworkCompletedFrameIfReady();
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
                    BroadcastNetworkCompletedFrameIfReady();
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
                StopNetworkClientSession();
                StopNetworkServer();
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
        /// Requests the OpenTK window to close on its own render thread.
        /// </summary>
        public void RequestClose()
        {
            Interlocked.Exchange(ref _closeRequested, 1);
        }

        /// <summary>
        /// Handles the on user update operation.
        /// </summary>
        public override void OnUserUpdate(double time)
        {
            if (Interlocked.Exchange(ref _closeRequested, 0) != 0)
            {
                Close();
                return;
            }

            PollGamepadInput();
            PollMouse1351Input();
            if (_networkMode == NetworkSessionMode.Client)
            {
                // Remote clients do not advance _system. They only draw the latest
                // network frame, local overlays, and local presentation filters.
                DrawRemoteClientFrame(time);
                return;
            }

            // Host mode folds client joystick states into CIA1 before the local frame is
            // drawn, so the emulated program sees remote input like physical joystick bits.
            ApplyNetworkServerJoystickInput();

            long currentCycle;
            ushort currentPc;
            byte currentOpcode;
            float sidMasterVolume;
            float sidNoiseLevel;
            SidChipModel sidChipModel;
            JoystickPort joystickPort;
            MountedMediaInfo mountedMediaInfo;
            bool enableLoadHack;
            bool forceSoftwareIecTransport;
            bool enableInputInjection;
            VicTiming timing;
            string memoryDebugInfo = string.Empty;
            string cpuDebugInfo = string.Empty;
            string ciaDebugInfo = string.Empty;
            string sidDebugInfo = string.Empty;
            string iecDebugInfo = string.Empty;
            string drive8DebugInfo = string.Empty;
            string pendingStatusText = string.Empty;
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
                timing = _system.Timing;
                currentCycle = timing.GlobalCycle;
                currentPc = _system.Cpu.PC;
                currentOpcode = _system.Cpu.CurrentOpcode;
                sidMasterVolume = _system.SidMasterVolume;
                sidNoiseLevel = _system.SidNoiseLevel;
                sidChipModel = _system.CurrentSidChipModel;
                joystickPort = _system.CurrentJoystickPort;
                mountedMediaInfo = _system.MountedMedia;
                enableLoadHack = _system.EnableLoadHack;
                forceSoftwareIecTransport = _system.ForceSoftwareIecTransport;
                enableInputInjection = _system.EnableInputInjection;
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
                if (_debugOverlayVisible)
                {
                    memoryDebugInfo = _system.GetMemoryConfigDebugInfo();
                    cpuDebugInfo = _system.GetCpuDebugInfo();
                    ciaDebugInfo = _system.GetCiaDebugInfo();
                    sidDebugInfo = _system.GetSidDebugInfo();
                    iecDebugInfo = _system.GetIecDebugInfo();
                    drive8DebugInfo = _system.GetDriveDebugInfo(8);
                }

                Array.Copy(_system.FrameBuffer.CompletedPixels, _frameSnapshot, _frameSnapshot.Length);
                pendingStatusText = _system.ConsumeStatusText();
            }

            if (!string.IsNullOrWhiteSpace(pendingStatusText))
            {
                _overlayStatusText = pendingStatusText;
                ShowTurboToast(pendingStatusText);
            }

            if (_driveOverlayEnabled)
            {
                UpdateDriveFooterState(time,
                    drive8Mounted || drive9Mounted || drive10Mounted || drive11Mounted,
                    drive8Active || drive9Active || drive10Active || drive11Active);
            }
            else
            {
                HideDriveFooter();
            }

            UpdateTurboToastState(time);
            DrawFrame(_frameSnapshot, _system.Model.VisibleWidth, _system.Model.VisibleHeight);
            // Rendering can run at a different cadence than emulation. The frame id check
            // inside this method keeps the network stream at completed C64-frame cadence.
            BroadcastNetworkCompletedFrameIfReady();
            BroadcastNetworkHostOverlayStatus();
            DrawDriveFooter(
                _system.Model.VisibleWidth,
                _system.Model.VisibleHeight,
                drive8Mounted, drive8LedOn, drive8Active,
                drive9Mounted, drive9LedOn, drive9Active,
                drive10Mounted, drive10LedOn, drive10Active,
                drive11Mounted, drive11LedOn, drive11Active);

            if (_mainMenuVisible)
            {
                DrawMainMenuOverlay();
            }
            else if (_networkOverlayVisible)
            {
                DrawNetworkOverlay();
            }
            else if (_saveOverlayVisible)
            {
                DrawSaveOverlay();
            }
            else if (_mediaBrowserVisible)
            {
                DrawStandaloneMediaBrowserOverlay();
            }
            else if (_audioOverlayVisible)
            {
                DrawAudioOverlay(sidMasterVolume, sidNoiseLevel, sidChipModel, joystickPort, mountedMediaInfo, enableLoadHack, forceSoftwareIecTransport, enableInputInjection);
            }
            else
            {
                DrawTurboToast();
            }

            if (_debugOverlayVisible && !_mainMenuVisible && !_networkOverlayVisible && !_saveOverlayVisible && !_mediaBrowserVisible && !_audioOverlayVisible)
            {
                DrawDebugOverlay(timing, currentPc, currentOpcode, memoryDebugInfo, cpuDebugInfo, ciaDebugInfo, sidDebugInfo, iecDebugInfo, drive8DebugInfo);
            }

            DrawNetworkStatusToast();
            UpdateNetworkSendFps();
            UpdateNetworkTrafficRates();

            if (_networkMode == NetworkSessionMode.Host)
            {
                // Host title intentionally shows network send FPS and render FPS, but not
                // program counter/debug data, so users can see stream health at a glance.
                Title = string.Format(
                    "C64 Emulator SERVER - NET:{0} FPS PING:{1} RENDER:{2}",
                    _networkSendFps,
                    FormatNetworkLatency(GetNetworkDisplayLatencyMilliseconds()),
                    FPS);
            }
            else
            {
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
        }

        /// <summary>
        /// Handles the on user initialize operation.
        /// </summary>
        public override void OnUserInitialize()
        {
            if (_windowFullscreen)
            {
                _windowFullscreen = false;
                ToggleDisplayMode(false);
            }

            ApplyRenderFrameLimit();
            UpdateMouse1351CaptureState();
        }

        /// <summary>
        /// Handles the on user mouse move operation.
        /// </summary>
        public override void OnUserMouseMove(int x, int y)
        {
            HandleMouse1351Move(x, y, 0, 0, false);
        }

        /// <summary>
        /// Handles the on user mouse move operation including relative movement.
        /// </summary>
        public override void OnUserMouseMove(int x, int y, int deltaX, int deltaY)
        {
            HandleMouse1351Move(x, y, deltaX, deltaY, true);
        }

        /// <summary>
        /// Converts host mouse move events into 1351 movement when mouse emulation is active.
        /// </summary>
        private void HandleMouse1351Move(int x, int y, int deltaX, int deltaY, bool hasEventDelta)
        {
            if (!IsMouse1351InputActive())
            {
                _hasLastMousePosition = false;
                return;
            }

            if (hasEventDelta)
            {
                _lastMouseX = x;
                _lastMouseY = y;
                _hasLastMousePosition = true;
                ApplyMouse1351Movement(deltaX, deltaY);
                return;
            }

            if (!_hasLastMousePosition)
            {
                _lastMouseX = x;
                _lastMouseY = y;
                _hasLastMousePosition = true;
                return;
            }

            int fallbackDeltaX = x - _lastMouseX;
            int fallbackDeltaY = y - _lastMouseY;
            _lastMouseX = x;
            _lastMouseY = y;
            ApplyMouse1351Movement(fallbackDeltaX, fallbackDeltaY);
        }

        /// <summary>
        /// Handles the on user mouse click operation.
        /// </summary>
        public override void OnUserMouseClick(int x, int y, MouseState mouseState)
        {
            if (IsMouse1351InputActive())
            {
                PollMouse1351ButtonState();
            }
        }

        /// <summary>
        /// Handles media files dropped onto the emulator window.
        /// </summary>
        public override void OnUserFileDrop(string[] fileNames)
        {
            if (_networkMode == NetworkSessionMode.Client)
            {
                // While connected as a client, media ownership belongs to the host. Local
                // drops are blocked instead of silently changing an unused local machine.
                ShowNetworkStatus("REMOTE SESSION ACTIVE");
                return;
            }

            MountDroppedMedia(fileNames);
        }

        /// <summary>
        /// Handles the on user key down operation.
        /// </summary>
        public override void OnUserKeyDown(SharpPixels.KeyEventArgs keyEventArgs)
        {
            if (keyEventArgs.Key == Key.F12)
            {
                if (_networkMode == NetworkSessionMode.Client)
                {
                    ToggleRemoteSaveOverlay();
                }
                else
                {
                    ToggleSaveOverlay();
                }

                return;
            }

            if (_saveOverlayVisible)
            {
                if (_networkMode == NetworkSessionMode.Client)
                {
                    HandleRemoteSaveOverlayKeyDown(keyEventArgs.Key);
                }
                else
                {
                    HandleSaveOverlayKeyDown(keyEventArgs.Key);
                }

                return;
            }

            if (keyEventArgs.Key == Key.F10)
            {
                ToggleMainMenu();
                return;
            }

            if (keyEventArgs.Key == Key.F9)
            {
                if (_networkMode == NetworkSessionMode.Client)
                {
                    ShowNetworkStatus("TURBO DISABLED REMOTE");
                }
                else
                {
                    ToggleTurboMode();
                }

                return;
            }

            if (keyEventArgs.Key == Key.F11)
            {
                ToggleDisplayMode();
                return;
            }

            if (_mainMenuVisible)
            {
                HandleMainMenuKeyDown(keyEventArgs.Key);
                return;
            }

            if (_networkOverlayVisible)
            {
                // The network overlay captures text input for host/port/password fields.
                HandleNetworkOverlayKeyDown(keyEventArgs.Key, keyEventArgs.Modifiers);
                return;
            }

            if (_networkMode == NetworkSessionMode.Client)
            {
                // Remote mode routes keys to local-only remote controls or upstream
                // input. It must not feed the stopped local C64 instance.
                HandleRemoteClientKeyDown(keyEventArgs);
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

            if (_controllerMappingVisible && HandleControllerMappingOverlayKeyDown(keyEventArgs.Key))
            {
                return;
            }

            if (_audioOverlayVisible && HandleAudioOverlayKeyDown(keyEventArgs.Key))
            {
                return;
            }

            if (HandleLocalAltCursorKeyDown(keyEventArgs.Key, keyEventArgs.Modifiers))
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
        /// Handles Alt+arrow as a local C64 cursor key chord.
        /// </summary>
        private bool HandleLocalAltCursorKeyDown(Key key, KeyModifiers modifiers)
        {
            if (!IsAltCursorChord(key, modifiers))
            {
                return false;
            }

            ReleaseLocalAltModifierKeys();
            if (_localAltCursorKeys.Add(key))
            {
                _system.KeyboardKeyDown(key);
            }

            return true;
        }

        /// <summary>
        /// Releases a local C64 cursor key chord if it was started with Alt+arrow.
        /// </summary>
        private bool HandleLocalAltCursorKeyUp(Key key)
        {
            if (_localAltCursorKeys.Remove(key))
            {
                _system.KeyboardKeyUp(key);
                return true;
            }

            if (IsAltKey(key))
            {
                ReleaseLocalAltCursorKeys();
            }

            return false;
        }

        private void ReleaseLocalAltCursorKeys()
        {
            foreach (Key key in _localAltCursorKeys.ToArray())
            {
                _system.KeyboardKeyUp(key);
            }

            _localAltCursorKeys.Clear();
        }

        private void ReleaseLocalAltModifierKeys()
        {
            _system.KeyUp(Key.AltLeft);
            _system.KeyUp(Key.LAlt);
        }

        /// <summary>
        /// Handles the on user key up operation.
        /// </summary>
        public override void OnUserKeyUp(SharpPixels.KeyEventArgs keyEventArgs)
        {
            if (_networkMode == NetworkSessionMode.Client)
            {
                if (HandleRemoteAltCursorKeyUp(keyEventArgs.Key))
                {
                    return;
                }
            }
            else if (HandleLocalAltCursorKeyUp(keyEventArgs.Key))
            {
                return;
            }

            if (_networkOverlayVisible || _mainMenuVisible)
            {
                // Ignore releases for keys captured by emulator menus so they do not
                // leak as C64 keyboard/joystick input after the menu closes.
                return;
            }

            if (_networkMode == NetworkSessionMode.Client)
            {
                if (!_networkOverlayVisible && !_saveOverlayVisible && !_audioOverlayVisible)
                {
                    if (HandleRemoteAltCursorKeyUp(keyEventArgs.Key))
                    {
                        return;
                    }

                    SendRemoteClientKeyboardKey(keyEventArgs.Key, false);
                    HandleRemoteClientJoystickKey(keyEventArgs.Key, false);
                }

                return;
            }

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

            if (_controllerMappingVisible && CapturesControllerMappingOverlayKey(keyEventArgs.Key))
            {
                return;
            }

            if (_audioOverlayVisible && CapturesAudioOverlayKey(keyEventArgs.Key))
            {
                return;
            }

            if (HandleLocalAltCursorKeyUp(keyEventArgs.Key))
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
        /// Toggles the read-only remote savestate overlay.
        /// </summary>
        private void ToggleRemoteSaveOverlay()
        {
            if (_saveOverlayVisible)
            {
                CloseSaveOverlay();
                return;
            }

            OpenRemoteSaveOverlay();
        }

        /// <summary>
        /// Opens the read-only savestate notice used while connected as a client.
        /// </summary>
        private void OpenRemoteSaveOverlay()
        {
            _mainMenuVisible = false;
            _audioOverlayVisible = false;
            _networkOverlayVisible = false;
            _mediaBrowserVisible = false;
            _resetConfirmVisible = false;
            _saveDeleteConfirmVisible = false;
            _pendingDeleteSavePath = null;
            _saveOverlayVisible = true;
            _saveOverlayStatusText = "REMOTE SESSION";
        }

        /// <summary>
        /// Handles key input in the read-only remote savestate notice.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <returns>True because the overlay consumes every key.</returns>
        private bool HandleRemoteSaveOverlayKeyDown(Key key)
        {
            if (key == Key.Escape)
            {
                CloseSaveOverlay();
            }

            return true;
        }

        /// <summary>
        /// Toggles the top-level F10 emulator menu.
        /// </summary>
        private void ToggleMainMenu()
        {
            if (_mainMenuVisible || _audioOverlayVisible || _networkOverlayVisible || _mediaBrowserVisible || _resetConfirmVisible)
            {
                CloseMainMenuFamily();
                return;
            }

            OpenMainMenu();
        }

        /// <summary>
        /// Returns from a submenu to the F10 overview without resuming emulation.
        /// </summary>
        private void ReturnToMainMenuFromSubmenu()
        {
            _mainMenuVisible = true;
            _networkOverlayVisible = false;
            _audioOverlayVisible = false;
            _saveOverlayVisible = false;
            _mediaBrowserVisible = false;
            _controllerMappingVisible = false;
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _resetConfirmVisible = false;
            _saveDeleteConfirmVisible = false;
            _pendingDeleteSavePath = null;
        }

        /// <summary>
        /// Opens the top-level emulator menu.
        /// </summary>
        private void OpenMainMenu()
        {
            _mainMenuVisible = true;
            _networkOverlayVisible = false;
            _audioOverlayVisible = false;
            _saveOverlayVisible = false;
            _mediaBrowserVisible = false;
            _controllerMappingVisible = false;
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _resetConfirmVisible = false;
            _saveDeleteConfirmVisible = false;
            _overlayStatusText = "MAIN MENU";
        }

        /// <summary>
        /// Closes the F10 menu and any submenu reached from it.
        /// </summary>
        private void CloseMainMenuFamily()
        {
            _mainMenuVisible = false;
            _networkOverlayVisible = false;
            _audioOverlayVisible = false;
            _mediaBrowserVisible = false;
            _controllerMappingVisible = false;
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _resetConfirmVisible = false;
            if (_networkMode != NetworkSessionMode.Client)
            {
                ResetEmulationTiming();
            }
        }

        /// <summary>
        /// Handles key input in the top-level F10 menu.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <returns>True when the menu consumed the key.</returns>
        private bool HandleMainMenuKeyDown(Key key)
        {
            if (_resetConfirmVisible)
            {
                return HandleResetConfirmKeyDown(key);
            }

            switch (key)
            {
                case Key.Up:
                    MoveMainMenuSelection(-1);
                    return true;
                case Key.Down:
                    MoveMainMenuSelection(1);
                    return true;
                case Key.Left:
                case Key.Minus:
                case Key.Right:
                case Key.Plus:
                case Key.Enter:
                    ActivateMainMenuItem();
                    return true;
                case Key.Escape:
                    CloseMainMenuFamily();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Moves the selected top-level menu entry with wraparound.
        /// </summary>
        /// <param name="delta">Selection delta.</param>
        private void MoveMainMenuSelection(int delta)
        {
            _mainMenuSelection += delta;
            if (_mainMenuSelection < 0)
            {
                _mainMenuSelection = MainMenuItemCount - 1;
            }

            if (_mainMenuSelection >= MainMenuItemCount)
            {
                _mainMenuSelection = 0;
            }
        }

        /// <summary>
        /// Opens or toggles the selected top-level menu entry.
        /// </summary>
        private void ActivateMainMenuItem()
        {
            MainMenuItem selectedItem = (MainMenuItem)_mainMenuSelection;
            if (!IsMainMenuItemEnabled(selectedItem))
            {
                ShowNetworkStatus("MENU ITEM DISABLED");
                return;
            }

            switch (selectedItem)
            {
                case MainMenuItem.Settings:
                    OpenAudioOverlay();
                    return;
                case MainMenuItem.Network:
                    _mainMenuVisible = false;
                    _networkOverlayVisible = false;
                    ToggleNetworkOverlay();
                    return;
                case MainMenuItem.Media:
                    _mainMenuVisible = false;
                    _networkOverlayVisible = false;
                    _audioOverlayVisible = false;
                    _saveOverlayVisible = false;
                    OpenMediaBrowser();
                    return;
                case MainMenuItem.Reset:
                    OpenResetConfirmation();
                    return;
            }
        }

        /// <summary>
        /// Checks whether a top-level menu item is available in the current mode.
        /// </summary>
        /// <param name="item">Top-level menu item.</param>
        /// <returns>True when the item can be activated.</returns>
        private bool IsMainMenuItemEnabled(MainMenuItem item)
        {
            if (_networkMode != NetworkSessionMode.Client)
            {
                return true;
            }

            // Remote clients can manage local presentation and network leave/filter
            // options, but media and drive ownership stay with the host.
                return item == MainMenuItem.Settings || item == MainMenuItem.Network;
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
            _mainMenuVisible = false;
            _networkOverlayVisible = false;
            _saveOverlayVisible = false;
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
            _controllerMappingVisible = false;
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _resetConfirmVisible = false;
            ResetEmulationTiming();
        }

        /// <summary>
        /// Toggles the network overlay and coordinates pause/resume behavior.
        /// </summary>
        private void ToggleNetworkOverlay()
        {
            _networkOverlayVisible = !_networkOverlayVisible;
            if (_networkOverlayVisible)
            {
                // Only one modal emulator overlay is visible at a time. Closing the
                // others avoids ambiguous key routing between menus.
                _mainMenuVisible = false;
                _audioOverlayVisible = false;
                _saveOverlayVisible = false;
                _mediaBrowserVisible = false;
                _resetConfirmVisible = false;
                _saveDeleteConfirmVisible = false;
                ClampNetworkSelectedClientIndex();
                if (!IsNetworkMenuRowEnabled(_networkOverlaySelection))
                {
                    // If the current mode disables the previously selected row, land on
                    // the primary action for that mode.
                    _networkOverlaySelection = _networkMode == NetworkSessionMode.Client ? 14 : 4;
                }
            }
            else if (_networkMode != NetworkSessionMode.Client)
            {
                // Remote clients have no running local emulation timer to resume.
                ResetEmulationTiming();
            }
        }

        /// <summary>
        /// Opens the savestate overlay and pauses the emulation loop.
        /// </summary>
        private void OpenSaveOverlay()
        {
            _mainMenuVisible = false;
            _audioOverlayVisible = false;
            _networkOverlayVisible = false;
            _mediaBrowserVisible = false;
            _resetConfirmVisible = false;
            _saveDeleteConfirmVisible = false;
            _saveOverlayStatusText = "PAUSED";
            _saveOverlayVisible = true;
            _saveOverlayCurrentDirectory = GetSaveDirectory();
            ReloadSaveStateEntries();
            if (!string.IsNullOrWhiteSpace(_lastLoadedSaveStatePath) && File.Exists(_lastLoadedSaveStatePath))
            {
                SelectSaveStatePath(_lastLoadedSaveStatePath);
            }
        }

        /// <summary>
        /// Closes the savestate overlay and resumes normal timing.
        /// </summary>
        private void CloseSaveOverlay()
        {
            _saveOverlayVisible = false;
            _saveDeleteConfirmVisible = false;
            _pendingDeleteSavePath = null;
            _saveOverlayStatusText = "F12 SAVE MENU";
            ResetEmulationTiming();
        }

        /// <summary>
        /// Handles savestate overlay key input.
        /// </summary>
        private bool HandleSaveOverlayKeyDown(Key key)
        {
            if (_saveDeleteConfirmVisible)
            {
                return HandleSaveDeleteConfirmKeyDown(key);
            }

            switch (key)
            {
                case Key.Up:
                    MoveSaveOverlaySelection(-1);
                    return true;
                case Key.Down:
                    MoveSaveOverlaySelection(1);
                    return true;
                case Key.PageUp:
                    MoveSaveOverlaySelection(-SaveOverlayVisibleRows);
                    return true;
                case Key.PageDown:
                    MoveSaveOverlaySelection(SaveOverlayVisibleRows);
                    return true;
                case Key.F5:
                case Key.S:
                    CreateSaveState();
                    return true;
                case Key.Enter:
                case Key.L:
                    ActivateSelectedSaveOverlayEntry();
                    return true;
                case Key.Delete:
                    OpenSaveDeleteConfirmation();
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
                string saveDirectory = SaveStateFile.GetSaveDirectoryForMedia(GetSaveDirectory(), _system.MountedMedia);
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
        /// Activates the selected savestate overlay entry.
        /// </summary>
        private void ActivateSelectedSaveOverlayEntry()
        {
            if (_saveOverlayEntries.Count == 0)
            {
                _saveOverlayStatusText = "NO SAVE SELECTED";
                return;
            }

            SaveOverlayEntry selectedEntry = _saveOverlayEntries[_saveOverlaySelection];
            if (selectedEntry.IsDirectory)
            {
                _saveOverlayCurrentDirectory = selectedEntry.Path;
                _saveOverlaySelection = 0;
                _saveOverlayScroll = 0;
                ReloadSaveStateEntries();
                _saveOverlayStatusText = selectedEntry.IsParent ? "UP" : "OPEN " + FormatOverlayValue(selectedEntry.DisplayName, 32);
                return;
            }

            LoadSelectedSaveState(selectedEntry.Metadata);
        }

        /// <summary>
        /// Opens a yes/no confirmation before deleting a savestate.
        /// </summary>
        private void OpenSaveDeleteConfirmation()
        {
            if (_saveOverlayEntries.Count == 0)
            {
                _saveOverlayStatusText = "NO SAVE SELECTED";
                return;
            }

            SaveOverlayEntry selectedEntry = _saveOverlayEntries[_saveOverlaySelection];
            if (selectedEntry.IsDirectory || selectedEntry.Metadata == null)
            {
                _saveOverlayStatusText = "SELECT A SAVE";
                return;
            }

            _pendingDeleteSavePath = selectedEntry.Metadata.Path;
            _saveDeleteConfirmYesSelected = false;
            _saveDeleteConfirmVisible = true;
            _saveOverlayStatusText = "CONFIRM DELETE";
        }

        /// <summary>
        /// Handles keys in the savestate delete confirmation popup.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <returns>True when consumed.</returns>
        private bool HandleSaveDeleteConfirmKeyDown(Key key)
        {
            switch (key)
            {
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    _saveDeleteConfirmYesSelected = !_saveDeleteConfirmYesSelected;
                    return true;
                case Key.Enter:
                    if (_saveDeleteConfirmYesSelected)
                    {
                        DeleteSelectedSaveStateConfirmed();
                    }
                    else
                    {
                        CancelSaveDeleteConfirmation();
                    }

                    return true;
                case Key.Escape:
                    CancelSaveDeleteConfirmation();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Cancels the pending savestate delete operation.
        /// </summary>
        private void CancelSaveDeleteConfirmation()
        {
            _saveDeleteConfirmVisible = false;
            _pendingDeleteSavePath = null;
            _saveOverlayStatusText = "DELETE CANCELED";
        }

        /// <summary>
        /// Loads the given savestate.
        /// </summary>
        private void LoadSelectedSaveState(SaveStateMetadata entry)
        {
            if (entry == null)
            {
                _saveOverlayStatusText = "NO SAVE SELECTED";
                return;
            }

            try
            {
                SaveStateFile.Load(entry.Path, _system);
                ApplyMouse1351StateToSystem();
                lock (_system.SyncRoot)
                {
                    Array.Copy(_system.FrameBuffer.CompletedPixels, _frameSnapshot, _frameSnapshot.Length);
                }

                _lastLoadedSaveStatePath = entry.Path;
                _overlayStatusText = "SAVE LOADED";
                _saveOverlayStatusText = "SAVE LOADED";
                CloseSaveOverlay();
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
        private void DeleteSelectedSaveStateConfirmed()
        {
            if (string.IsNullOrWhiteSpace(_pendingDeleteSavePath))
            {
                _saveDeleteConfirmVisible = false;
                _saveOverlayStatusText = "NO SAVE SELECTED";
                return;
            }

            try
            {
                string path = _pendingDeleteSavePath;
                string deletedSaveDirectory = Path.GetDirectoryName(path);
                File.Delete(path);
                if (string.Equals(_lastLoadedSaveStatePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _lastLoadedSaveStatePath = null;
                }

                bool deletedDirectory = TryDeleteEmptySaveSubdirectory(deletedSaveDirectory);
                _saveDeleteConfirmVisible = false;
                _pendingDeleteSavePath = null;
                _saveOverlayStatusText = deletedDirectory ? "SAVE DIR DELETED" : "SAVE DELETED";
                ReloadSaveStateEntries();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _saveDeleteConfirmVisible = false;
                _pendingDeleteSavePath = null;
                _saveOverlayStatusText = "DELETE FAILED";
            }
        }

        /// <summary>
        /// Deletes an empty savestate subdirectory while preserving the savestate root.
        /// </summary>
        /// <param name="directory">Directory that contained a deleted savestate file.</param>
        /// <returns>True when the empty subdirectory was removed.</returns>
        private bool TryDeleteEmptySaveSubdirectory(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return false;
                }

                string rootDirectory = Path.GetFullPath(GetSaveDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string candidateDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(candidateDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase) ||
                    !IsPathInsideDirectory(candidateDirectory, rootDirectory) ||
                    Directory.EnumerateFileSystemEntries(candidateDirectory).Any())
                {
                    return false;
                }

                string parentDirectory = Path.GetDirectoryName(candidateDirectory);
                Directory.Delete(candidateDirectory);
                string currentDirectory = string.IsNullOrWhiteSpace(_saveOverlayCurrentDirectory)
                    ? string.Empty
                    : Path.GetFullPath(_saveOverlayCurrentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(currentDirectory, candidateDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _saveOverlayCurrentDirectory = !string.IsNullOrWhiteSpace(parentDirectory) && IsPathInsideOrEqual(parentDirectory, rootDirectory)
                        ? parentDirectory
                        : rootDirectory;
                    _saveOverlaySelection = 0;
                    _saveOverlayScroll = 0;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }

        /// <summary>
        /// Checks whether a path is strictly inside a root directory.
        /// </summary>
        private static bool IsPathInsideDirectory(string path, string rootDirectory)
        {
            return path.StartsWith(rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(rootDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks whether a path is the root directory or inside it.
        /// </summary>
        private static bool IsPathInsideOrEqual(string path, string rootDirectory)
        {
            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                IsPathInsideDirectory(normalizedPath, normalizedRoot);
        }

        /// <summary>
        /// Reloads savestate metadata from the host saves directory.
        /// </summary>
        private void ReloadSaveStateEntries()
        {
            var entries = new List<SaveStateMetadata>();
            var overlayEntries = new List<SaveOverlayEntry>();
            try
            {
                string saveDirectory = GetSaveDirectory();
                Directory.CreateDirectory(saveDirectory);
                if (string.IsNullOrWhiteSpace(_saveOverlayCurrentDirectory) || !Directory.Exists(_saveOverlayCurrentDirectory))
                {
                    _saveOverlayCurrentDirectory = saveDirectory;
                }

                string currentDirectory = Path.GetFullPath(_saveOverlayCurrentDirectory);
                string rootDirectory = Path.GetFullPath(saveDirectory);
                if (!currentDirectory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    currentDirectory = rootDirectory;
                    _saveOverlayCurrentDirectory = rootDirectory;
                }

                DirectoryInfo parent = Directory.GetParent(currentDirectory);
                if (parent != null && !string.Equals(currentDirectory.TrimEnd(Path.DirectorySeparatorChar), rootDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    overlayEntries.Add(new SaveOverlayEntry(parent.FullName, "UP", true, true, null));
                }

                string[] directories = Directory.GetDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    overlayEntries.Add(new SaveOverlayEntry(directory, "DIR " + Path.GetFileName(directory).ToUpperInvariant(), true, false, null));
                }

                string[] files = Directory.GetFiles(currentDirectory, SaveStateFile.SearchPattern, SearchOption.TopDirectoryOnly);
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
                foreach (SaveStateMetadata entry in entries)
                {
                    overlayEntries.Add(new SaveOverlayEntry(entry.Path, FormatSaveListEntry(entry, 80), false, false, entry));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _saveOverlayStatusText = "SAVE DIR FAILED";
            }

            _saveOverlayEntries = overlayEntries;
            if (_saveOverlaySelection >= _saveOverlayEntries.Count)
            {
                _saveOverlaySelection = Math.Max(0, _saveOverlayEntries.Count - 1);
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
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                _saveOverlayCurrentDirectory = directory;
                ReloadSaveStateEntries();
            }

            for (int index = 0; index < _saveOverlayEntries.Count; index++)
            {
                if (!_saveOverlayEntries[index].IsDirectory &&
                    string.Equals(_saveOverlayEntries[index].Path, path, StringComparison.OrdinalIgnoreCase))
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
            if (_saveOverlayEntries.Count == 0)
            {
                return;
            }

            _saveOverlaySelection += delta;
            if (_saveOverlaySelection < 0)
            {
                _saveOverlaySelection = 0;
            }

            if (_saveOverlaySelection >= _saveOverlayEntries.Count)
            {
                _saveOverlaySelection = _saveOverlayEntries.Count - 1;
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

            int maxVisibleStart = Math.Max(0, _saveOverlayEntries.Count - SaveOverlayVisibleRows);
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
        /// Gets the per-user savestate directory.
        /// </summary>
        private static string GetSaveDirectory()
        {
            string saveDirectory = UserDataPaths.GetSaveDirectory();
            Directory.CreateDirectory(saveDirectory);
            return saveDirectory;
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
            SaveSettings();
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
        private void ToggleDisplayMode(bool saveSettings = true)
        {
            ToggleFullscreen();
            _windowFullscreen = !_windowFullscreen;
            _overlayStatusText = _windowFullscreen ? "FULLSCREEN" : "WINDOW MODE";
            if (saveSettings)
            {
                SaveSettings();
            }
        }

        /// <summary>
        /// Cycles the frontend render-loop frame cap.
        /// </summary>
        private void CycleRenderFrameLimit()
        {
            switch (_renderFrameLimitMode)
            {
                case RenderFrameLimitMode.Hz60:
                    _renderFrameLimitMode = RenderFrameLimitMode.Hz120;
                    break;
                case RenderFrameLimitMode.Hz120:
                    _renderFrameLimitMode = RenderFrameLimitMode.Unlimited;
                    break;
                default:
                    _renderFrameLimitMode = RenderFrameLimitMode.Hz60;
                    break;
            }

            ApplyRenderFrameLimit();
            _overlayStatusText = "RENDER FPS " + FormatRenderFrameLimit(_renderFrameLimitMode);
            SaveSettings();
        }

        /// <summary>
        /// Applies the selected frontend render-loop frame cap to the SharpPixels window.
        /// </summary>
        private void ApplyRenderFrameLimit()
        {
            IsEventDriven = false;
            UpdateFrequency = GetRenderFrameFrequency(_renderFrameLimitMode);
        }

        /// <summary>
        /// Converts a render cap mode to the OpenTK update frequency value.
        /// </summary>
        private static double GetRenderFrameFrequency(RenderFrameLimitMode mode)
        {
            switch (mode)
            {
                case RenderFrameLimitMode.Hz60:
                    return 60.0;
                case RenderFrameLimitMode.Hz120:
                    return 120.0;
                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// Toggles the live developer debug overlay.
        /// </summary>
        private void ToggleDebugOverlay()
        {
            _debugOverlayVisible = !_debugOverlayVisible;
            _overlayStatusText = _debugOverlayVisible ? "DEBUG OVERLAY ON" : "DEBUG OVERLAY OFF";
            ShowTurboToast(_overlayStatusText);
        }

        /// <summary>
        /// Toggles the drive footer overlay.
        /// </summary>
        private void ToggleDriveOverlay()
        {
            _driveOverlayEnabled = !_driveOverlayEnabled;
            if (!_driveOverlayEnabled)
            {
                HideDriveFooter();
            }
            else
            {
                _driveFooterIdleSeconds = 0.0;
                _driveFooterVisibility = 1.0;
            }

            _overlayStatusText = _driveOverlayEnabled ? "DRIVE OVERLAY ON" : "DRIVE OVERLAY OFF";
            ShowTurboToast(_overlayStatusText);
            SaveSettings();
        }

        /// <summary>
        /// Toggles the host gamepad joystick input.
        /// </summary>
        private void ToggleGamepadInput()
        {
            _gamepadEnabled = !_gamepadEnabled;
            if (!_gamepadEnabled)
            {
                _lastGamepadJoystickState = 0x1F;
                _gamepadConnected = false;
                _system.SetGamepadJoystickState(0x1F);
            }

            _overlayStatusText = _gamepadEnabled ? "GAMEPAD ON" : "GAMEPAD OFF";
            SaveSettings();
        }

        private void OpenControllerMappingOverlay()
        {
            _controllerMappingVisible = true;
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _controllerMappingSelection = ClampInt(_controllerMappingSelection, 0, ControllerMapActionCount - 1);
            ClampControllerMappingScroll();
            _overlayStatusText = "CONTROLLER CONFIG";
        }

        private void CloseControllerMappingOverlay()
        {
            _controllerMappingVisible = false;
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _overlayStatusText = "SETTINGS";
        }

        private bool HandleControllerMappingOverlayKeyDown(Key key)
        {
            switch (key)
            {
                case Key.Up:
                    MoveControllerMappingSelection(-1);
                    return true;
                case Key.Down:
                    MoveControllerMappingSelection(1);
                    return true;
                case Key.Enter:
                    BeginControllerMappingLearning();
                    return true;
                case Key.Delete:
                    ResetSelectedControllerBinding();
                    return true;
                case Key.BackSpace:
                    ClearSelectedControllerBinding();
                    return true;
                case Key.Escape:
                    CloseControllerMappingOverlay();
                    return true;
                default:
                    return true;
            }
        }

        private static bool CapturesControllerMappingOverlayKey(Key key)
        {
            return true;
        }

        private void MoveControllerMappingSelection(int delta)
        {
            _controllerMappingSelection += delta;
            if (_controllerMappingSelection < 0)
            {
                _controllerMappingSelection = ControllerMapActionCount - 1;
            }

            if (_controllerMappingSelection >= ControllerMapActionCount)
            {
                _controllerMappingSelection = 0;
            }

            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            ClampControllerMappingScroll();
        }

        private void ClampControllerMappingScroll()
        {
            int maxScroll = Math.Max(0, ControllerMapActionCount - ControllerMappingVisibleRows);
            if (_controllerMappingSelection < _controllerMappingScroll)
            {
                _controllerMappingScroll = _controllerMappingSelection;
            }

            if (_controllerMappingSelection >= _controllerMappingScroll + ControllerMappingVisibleRows)
            {
                _controllerMappingScroll = _controllerMappingSelection - ControllerMappingVisibleRows + 1;
            }

            if (_controllerMappingScroll < 0)
            {
                _controllerMappingScroll = 0;
            }

            if (_controllerMappingScroll > maxScroll)
            {
                _controllerMappingScroll = maxScroll;
            }
        }

        private void BeginControllerMappingLearning()
        {
            _controllerMappingLearning = true;
            _controllerLearningBaseline.Clear();
            foreach (string binding in CaptureActiveControllerInputs())
            {
                _controllerLearningBaseline.Add(binding);
            }

            _controllerLearningBaselineReleased = _controllerLearningBaseline.Count == 0;
            _overlayStatusText = "ADD " + FormatControllerMapAction(GetSelectedControllerMapAction());
        }

        private void ResetSelectedControllerBinding()
        {
            ControllerMapAction action = GetSelectedControllerMapAction();
            SetControllerBindings(action, ParseGamepadBindings(GetDefaultControllerBindings(action), GetDefaultControllerBindings(action)));
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _overlayStatusText = FormatControllerMapAction(action) + " DEFAULT";
            SaveSettings();
        }

        private void ClearSelectedControllerBinding()
        {
            ControllerMapAction action = GetSelectedControllerMapAction();
            SetControllerBindings(action, new List<string>(), false);
            _controllerMappingLearning = false;
            _controllerLearningBaselineReleased = true;
            _controllerLearningBaseline.Clear();
            _overlayStatusText = FormatControllerMapAction(action) + " CLEARED";
            SaveSettings();
        }

        private void UpdateControllerMappingLearning()
        {
            if (!_controllerMappingLearning)
            {
                return;
            }

            List<string> activeBindings = CaptureActiveControllerInputs();
            if (!_controllerLearningBaselineReleased)
            {
                bool baselineStillPressed = activeBindings.Any(binding => _controllerLearningBaseline.Contains(binding));
                if (baselineStillPressed)
                {
                    return;
                }

                _controllerLearningBaselineReleased = true;
            }

            foreach (string binding in activeBindings)
            {
                ControllerMapAction action = GetSelectedControllerMapAction();
                SuppressControllerUiBindingUntilRelease(binding);
                if (!AddControllerBinding(action, binding))
                {
                    _controllerMappingLearning = false;
                    _controllerLearningBaselineReleased = true;
                    _controllerLearningBaseline.Clear();
                    _overlayStatusText = FormatControllerMapAction(action) + " ALREADY SET";
                    return;
                }

                _controllerMappingLearning = false;
                _controllerLearningBaselineReleased = true;
                _controllerLearningBaseline.Clear();
                _overlayStatusText = FormatControllerMapAction(action) + " ADD " + FormatGamepadBinding(binding);
                SaveSettings();
                return;
            }
        }

        /// <summary>
        /// Temporarily suppresses a freshly learned controller input for menu actions.
        /// </summary>
        private void SuppressControllerUiBindingUntilRelease(string binding)
        {
            if (!string.IsNullOrWhiteSpace(binding))
            {
                _controllerSuppressedUiBindings.Add(binding.Trim());
            }
        }

        private ControllerMapAction GetSelectedControllerMapAction()
        {
            return (ControllerMapAction)ClampInt(_controllerMappingSelection, 0, ControllerMapActionCount - 1);
        }

        /// <summary>
        /// Gets the mutable binding list for one C64 joystick line.
        /// </summary>
        private List<string> GetControllerBindings(ControllerMapAction action)
        {
            switch (action)
            {
                case ControllerMapAction.Up:
                    return _gamepadUpBindings;
                case ControllerMapAction.Down:
                    return _gamepadDownBindings;
                case ControllerMapAction.Left:
                    return _gamepadLeftBindings;
                case ControllerMapAction.Right:
                    return _gamepadRightBindings;
                case ControllerMapAction.Fire:
                    return _gamepadFireBindings;
                case ControllerMapAction.MenuSelect:
                    return _gamepadMenuSelectBindings;
                case ControllerMapAction.MenuBack:
                    return _gamepadMenuBackBindings;
                case ControllerMapAction.MainMenu:
                    return _gamepadMainMenuBindings;
                case ControllerMapAction.Turbo:
                    return _gamepadTurboBindings;
                default:
                    return _gamepadSaveStatesBindings;
            }
        }

        /// <summary>
        /// Replaces the binding list for one C64 joystick line.
        /// </summary>
        private void SetControllerBindings(ControllerMapAction action, List<string> bindings)
        {
            SetControllerBindings(action, bindings, true);
        }

        /// <summary>
        /// Replaces the binding list for one controller action.
        /// </summary>
        private void SetControllerBindings(ControllerMapAction action, List<string> bindings, bool restoreDefaultWhenEmpty)
        {
            List<string> normalizedBindings = bindings == null
                ? new List<string>()
                : bindings
                    .Where(binding => !string.IsNullOrWhiteSpace(binding))
                    .Select(binding => binding.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (normalizedBindings.Count == 0 && restoreDefaultWhenEmpty)
            {
                normalizedBindings = ParseGamepadBindings(GetDefaultControllerBindings(action), GetDefaultControllerBindings(action));
            }

            switch (action)
            {
                case ControllerMapAction.Up:
                    _gamepadUpBindings = normalizedBindings;
                    break;
                case ControllerMapAction.Down:
                    _gamepadDownBindings = normalizedBindings;
                    break;
                case ControllerMapAction.Left:
                    _gamepadLeftBindings = normalizedBindings;
                    break;
                case ControllerMapAction.Right:
                    _gamepadRightBindings = normalizedBindings;
                    break;
                case ControllerMapAction.Fire:
                    _gamepadFireBindings = normalizedBindings;
                    break;
                case ControllerMapAction.MenuSelect:
                    _gamepadMenuSelectBindings = normalizedBindings;
                    break;
                case ControllerMapAction.MenuBack:
                    _gamepadMenuBackBindings = normalizedBindings;
                    break;
                case ControllerMapAction.MainMenu:
                    _gamepadMainMenuBindings = normalizedBindings;
                    break;
                case ControllerMapAction.Turbo:
                    _gamepadTurboBindings = normalizedBindings;
                    break;
                default:
                    _gamepadSaveStatesBindings = normalizedBindings;
                    break;
            }
        }

        /// <summary>
        /// Adds one binding token to the selected controller action.
        /// </summary>
        private bool AddControllerBinding(ControllerMapAction action, string binding)
        {
            if (string.IsNullOrWhiteSpace(binding))
            {
                return false;
            }

            List<string> bindings = new List<string>(GetControllerBindings(action));
            if (bindings.Contains(binding, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            bindings.Add(binding.Trim());
            SetControllerBindings(action, bindings, false);
            return true;
        }

        /// <summary>
        /// Gets the default binding string for one C64 joystick line.
        /// </summary>
        private static string GetDefaultControllerBindings(ControllerMapAction action)
        {
            switch (action)
            {
                case ControllerMapAction.Up:
                    return DefaultGamepadUpBindings;
                case ControllerMapAction.Down:
                    return DefaultGamepadDownBindings;
                case ControllerMapAction.Left:
                    return DefaultGamepadLeftBindings;
                case ControllerMapAction.Right:
                    return DefaultGamepadRightBindings;
                case ControllerMapAction.Fire:
                    return DefaultGamepadFireBindings;
                case ControllerMapAction.MenuSelect:
                    return DefaultGamepadMenuSelectBindings;
                case ControllerMapAction.MenuBack:
                    return DefaultGamepadMenuBackBindings;
                case ControllerMapAction.MainMenu:
                    return DefaultGamepadMainMenuBindings;
                case ControllerMapAction.Turbo:
                    return DefaultGamepadTurboBindings;
                default:
                    return DefaultGamepadSaveStatesBindings;
            }
        }

        /// <summary>
        /// Formats one C64 joystick line for overlay text.
        /// </summary>
        private static string FormatControllerMapAction(ControllerMapAction action)
        {
            switch (action)
            {
                case ControllerMapAction.Up:
                    return "UP";
                case ControllerMapAction.Down:
                    return "DOWN";
                case ControllerMapAction.Left:
                    return "LEFT";
                case ControllerMapAction.Right:
                    return "RIGHT";
                case ControllerMapAction.Fire:
                    return "FIRE";
                case ControllerMapAction.MenuSelect:
                    return "MENU SELECT";
                case ControllerMapAction.MenuBack:
                    return "MENU BACK";
                case ControllerMapAction.MainMenu:
                    return "MAIN MENU";
                case ControllerMapAction.Turbo:
                    return "TURBO";
                default:
                    return "SAVE STATES";
            }
        }

        /// <summary>
        /// Cycles the video presentation filter.
        /// </summary>
        private void CycleVideoFilter()
        {
            switch (_videoFilterMode)
            {
                case VideoFilterMode.Sharp:
                    _videoFilterMode = VideoFilterMode.Crt;
                    break;
                case VideoFilterMode.Crt:
                    _videoFilterMode = VideoFilterMode.Tv;
                    break;
                default:
                    _videoFilterMode = VideoFilterMode.Sharp;
                    break;
            }

            _overlayStatusText = "FILTER " + FormatVideoFilter(_videoFilterMode);
            SaveSettings();
        }

        /// <summary>
        /// Cycles the optional GPU-side source upscaler.
        /// </summary>
        private void CycleVideoUpscale()
        {
            switch (_videoUpscaleMode)
            {
                case VideoUpscaleMode.None:
                    _videoUpscaleMode = VideoUpscaleMode.Scale2x;
                    break;
                case VideoUpscaleMode.Scale2x:
                    _videoUpscaleMode = VideoUpscaleMode.Scale3x;
                    break;
                case VideoUpscaleMode.Scale3x:
                    _videoUpscaleMode = VideoUpscaleMode.Hq2x;
                    break;
                case VideoUpscaleMode.Hq2x:
                    _videoUpscaleMode = VideoUpscaleMode.Hq3x;
                    break;
                case VideoUpscaleMode.Hq3x:
                    _videoUpscaleMode = VideoUpscaleMode.Hq4x;
                    break;
                default:
                    _videoUpscaleMode = VideoUpscaleMode.None;
                    break;
            }

            _overlayStatusText = "UPSCALE " + FormatVideoUpscale(_videoUpscaleMode);
            SaveSettings();
        }

        /// <summary>
        /// Toggles the local crop/scale presentation zoom.
        /// </summary>
        private void ToggleVideoZoom()
        {
            _videoZoomEnabled = !_videoZoomEnabled;
            _overlayStatusText = _videoZoomEnabled ? "VIDEO ZOOM ON" : "VIDEO ZOOM OFF";
            SaveSettings();
        }

        /// <summary>
        /// Cycles the reset behavior used by the reset action.
        /// </summary>
        private void CycleResetMode()
        {
            switch (_resetMode)
            {
                case ResetMode.Warm:
                    _resetMode = ResetMode.Reload;
                    break;
                case ResetMode.Reload:
                    _resetMode = ResetMode.Power;
                    break;
                default:
                    _resetMode = ResetMode.Warm;
                    break;
            }

            _overlayStatusText = "RESET " + FormatResetMode(_resetMode);
            SaveSettings();
        }

        /// <summary>
        /// Polls the first active host joystick/gamepad and mirrors it into the active C64 joystick port.
        /// </summary>
        private void PollGamepadInput()
        {
            if (_controllerMappingLearning)
            {
                UpdateControllerMappingLearning();
                return;
            }

            bool controllerUiActive = PollControllerUiActions();
            bool menuVisible = IsControllerMenuContextActive();
            if (controllerUiActive || menuVisible)
            {
                _controllerGameInputSuppressedUntilRelease = true;
            }

            if (_networkMode == NetworkSessionMode.Client)
            {
                if (controllerUiActive || menuVisible)
                {
                    _remoteGamepadJoystickState = 0xFF;
                    SendRemoteClientJoystickInput(false, 0.0);
                    return;
                }

                if (ShouldKeepControllerGameInputSuppressed())
                {
                    _remoteGamepadJoystickState = 0xFF;
                    SendRemoteClientJoystickInput(false, 0.0);
                    return;
                }

                PollRemoteClientGamepadInput();
                return;
            }

            if (!_gamepadEnabled)
            {
                return;
            }

            if (controllerUiActive || menuVisible)
            {
                ReleaseLocalGamepadJoystickState();
                return;
            }

            if (ShouldKeepControllerGameInputSuppressed())
            {
                ReleaseLocalGamepadJoystickState();
                return;
            }

            byte joystickState = 0x1F;
            bool connected = false;
            for (int index = 0; index < JoystickStates.Count; index++)
            {
                JoystickState joystick = JoystickStates[index];
                if (joystick == null)
                {
                    continue;
                }

                if (joystick.AxisCount <= 0 && joystick.ButtonCount <= 0 && joystick.HatCount <= 0)
                {
                    continue;
                }

                connected = true;
                try
                {
                    joystickState = ReadJoystickState(joystick);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    continue;
                }

                if ((joystickState & 0x1F) != 0x1F)
                {
                    break;
                }
            }

            _gamepadConnected = connected;
            if (!connected)
            {
                joystickState = 0x1F;
            }

            if (joystickState != _lastGamepadJoystickState)
            {
                _lastGamepadJoystickState = joystickState;
                _system.SetGamepadJoystickState(joystickState);
            }
        }

        /// <summary>
        /// Releases the local gamepad joystick lines while an emulator menu owns input.
        /// </summary>
        private void ReleaseLocalGamepadJoystickState()
        {
            _lastGamepadJoystickState = 0x1F;
            _system.SetGamepadJoystickState(0x1F);
        }

        /// <summary>
        /// Polls host mouse buttons and keeps the window cursor captured for 1351 mode.
        /// </summary>
        private void PollMouse1351Input()
        {
            UpdateMouse1351CaptureState();
            if (!IsMouse1351InputActive())
            {
                _hasLastMousePosition = false;
                if (_lastMouse1351JoystickState != 0x1F)
                {
                    _lastMouse1351JoystickState = 0x1F;
                    ApplyMouse1351StateToSystem();
                }

                return;
            }

            PollMouse1351ButtonState();
        }

        /// <summary>
        /// Reads the current host mouse buttons and mirrors them to the 1351 control port.
        /// </summary>
        private void PollMouse1351ButtonState()
        {
            byte joystickState = ReadMouse1351JoystickState();
            if (joystickState == _lastMouse1351JoystickState)
            {
                return;
            }

            _lastMouse1351JoystickState = joystickState;
            ApplyMouse1351StateToSystem();
        }

        /// <summary>
        /// Returns true when host mouse movement should reach the emulated C64.
        /// </summary>
        private bool IsMouse1351InputActive()
        {
            return _mouse1351Port != Mouse1351Port.Off
                && _networkMode != NetworkSessionMode.Client
                && !IsAnyEmulatorMenuVisible();
        }

        /// <summary>
        /// Captures or releases the OS cursor according to the active 1351 mode.
        /// </summary>
        private void UpdateMouse1351CaptureState()
        {
            bool shouldCapture = IsMouse1351InputActive();
            if (shouldCapture == _mouse1351Captured)
            {
                return;
            }

            _mouse1351Captured = shouldCapture;
            _hasLastMousePosition = false;
            try
            {
                CursorState = shouldCapture
                    ? OpenTK.Windowing.Common.CursorState.Grabbed
                    : OpenTK.Windowing.Common.CursorState.Hidden;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Checks whether an emulator overlay currently owns mouse/game input.
        /// </summary>
        private bool IsAnyEmulatorMenuVisible()
        {
            return _mainMenuVisible
                || _audioOverlayVisible
                || _mediaBrowserVisible
                || _resetConfirmVisible
                || _networkOverlayVisible
                || _controllerMappingVisible
                || _networkTlsCertificatePromptVisible
                || _saveOverlayVisible;
        }

        /// <summary>
        /// Converts host mouse deltas into middle-aligned 6-bit Commodore 1351 counters.
        /// </summary>
        private void ApplyMouse1351Movement(int deltaX, int deltaY)
        {
            if (deltaX == 0 && deltaY == 0)
            {
                return;
            }

            int scaledX = ClampInt(deltaX * Mouse1351MovementScale, -Mouse1351MaxDeltaPerEvent, Mouse1351MaxDeltaPerEvent);
            int scaledY = ClampInt(deltaY * Mouse1351MovementScale, -Mouse1351MaxDeltaPerEvent, Mouse1351MaxDeltaPerEvent);
            if (scaledX == 0 && scaledY == 0)
            {
                return;
            }

            _mouse1351XCounter = (_mouse1351XCounter + scaledX) & Mouse1351CounterMask;
            // A real 1351 reports positive POTY movement when the mouse moves up.
            _mouse1351YCounter = (_mouse1351YCounter - scaledY) & Mouse1351CounterMask;
            ApplyMouse1351StateToSystem();
        }

        /// <summary>
        /// Reads the 1351 button lines from the current host mouse state.
        /// </summary>
        private byte ReadMouse1351JoystickState()
        {
            byte state = 0x1F;
            try
            {
                OpenTK.Windowing.GraphicsLibraryFramework.MouseState mouseState = MouseState;
                if (mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left))
                {
                    state = (byte)(state & ~0x10);
                }

                if (mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right))
                {
                    state = (byte)(state & ~0x01);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return state;
        }

        /// <summary>
        /// Pushes the current 1351 POT counters and button lines into the emulated chips.
        /// </summary>
        private void ApplyMouse1351StateToSystem()
        {
            if (_system == null)
            {
                return;
            }

            if (_mouse1351Port == Mouse1351Port.Off)
            {
                _system.SetMouse1351State(Mouse1351Port.Off, 0xFF, 0xFF, 0x1F);
                return;
            }

            _system.SetMouse1351State(
                _mouse1351Port,
                EncodeMouse1351Counter(_mouse1351XCounter),
                EncodeMouse1351Counter(_mouse1351YCounter),
                _lastMouse1351JoystickState);
        }

        /// <summary>
        /// Encodes a 6-bit 1351 counter into the middle-aligned SID POT register layout.
        /// </summary>
        private static byte EncodeMouse1351Counter(int counter)
        {
            return (byte)((counter & Mouse1351CounterMask) << 1);
        }

        /// <summary>
        /// Keeps game-facing controller input neutral until all menu-era inputs are released.
        /// </summary>
        private bool ShouldKeepControllerGameInputSuppressed()
        {
            if (!_controllerGameInputSuppressedUntilRelease)
            {
                return false;
            }

            if (CaptureActiveControllerInputs().Count == 0)
            {
                _controllerGameInputSuppressedUntilRelease = false;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Polls configured controller bindings that act like emulator/menu keys.
        /// </summary>
        /// <returns>True when a controller UI action was triggered this frame.</returns>
        private bool PollControllerUiActions()
        {
            if (!_gamepadEnabled)
            {
                ResetControllerActionEdges();
                return false;
            }

            List<string> activeBindings = CaptureActiveControllerInputs();
            List<string> uiBindings = FilterSuppressedControllerUiBindings(activeBindings);
            long nowTicks = DateTime.UtcNow.Ticks;

            if (ShouldTriggerControllerAction(ControllerMapAction.SaveStates, uiBindings, nowTicks, false))
            {
                if (_networkMode == NetworkSessionMode.Client)
                {
                    ToggleRemoteSaveOverlay();
                }
                else
                {
                    ToggleSaveOverlay();
                }

                return true;
            }

            bool mainMenuTriggered = ShouldTriggerControllerAction(ControllerMapAction.MainMenu, uiBindings, nowTicks, false);
            if (!_saveOverlayVisible && mainMenuTriggered)
            {
                ToggleMainMenu();
                return true;
            }

            bool turboTriggered = ShouldTriggerControllerAction(ControllerMapAction.Turbo, uiBindings, nowTicks, false);
            if (!_saveOverlayVisible && turboTriggered)
            {
                if (_networkMode == NetworkSessionMode.Client)
                {
                    ShowNetworkStatus("TURBO DISABLED REMOTE");
                }
                else
                {
                    ToggleTurboMode();
                }

                return true;
            }

            if (!IsControllerMenuContextActive())
            {
                return false;
            }

            if (ShouldTriggerControllerAction(ControllerMapAction.MenuBack, uiBindings, nowTicks, false))
            {
                DispatchControllerMenuKey(Key.Escape);
                return true;
            }

            if (ShouldTriggerControllerAction(ControllerMapAction.MenuSelect, uiBindings, nowTicks, false))
            {
                DispatchControllerMenuKey(Key.Enter);
                return true;
            }

            if (ShouldTriggerControllerAction(ControllerMapAction.Up, uiBindings, nowTicks, true))
            {
                DispatchControllerMenuKey(Key.Up);
                return true;
            }

            if (ShouldTriggerControllerAction(ControllerMapAction.Down, uiBindings, nowTicks, true))
            {
                DispatchControllerMenuKey(Key.Down);
                return true;
            }

            if (ShouldTriggerControllerAction(ControllerMapAction.Left, uiBindings, nowTicks, true))
            {
                DispatchControllerMenuKey(Key.Left);
                return true;
            }

            if (ShouldTriggerControllerAction(ControllerMapAction.Right, uiBindings, nowTicks, true))
            {
                DispatchControllerMenuKey(Key.Right);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes freshly learned controller inputs from UI processing until released.
        /// </summary>
        private List<string> FilterSuppressedControllerUiBindings(List<string> activeBindings)
        {
            if (_controllerSuppressedUiBindings.Count == 0)
            {
                return activeBindings;
            }

            _controllerSuppressedUiBindings.RemoveWhere(binding => !activeBindings.Contains(binding, StringComparer.OrdinalIgnoreCase));
            if (_controllerSuppressedUiBindings.Count == 0)
            {
                return activeBindings;
            }

            return activeBindings
                .Where(binding => !_controllerSuppressedUiBindings.Contains(binding))
                .ToList();
        }

        /// <summary>
        /// Checks one controller action with edge detection and optional menu repeat.
        /// </summary>
        private bool ShouldTriggerControllerAction(ControllerMapAction action, List<string> activeBindings, long nowTicks, bool repeat)
        {
            int actionIndex = (int)action;
            bool pressed = IsControllerActionPressed(action, activeBindings);
            if (!pressed)
            {
                _controllerActionWasDown[actionIndex] = false;
                _controllerActionNextRepeatTicks[actionIndex] = 0;
                return false;
            }

            if (!_controllerActionWasDown[actionIndex])
            {
                _controllerActionWasDown[actionIndex] = true;
                _controllerActionNextRepeatTicks[actionIndex] = nowTicks + SecondsToTicks(ControllerActionInitialRepeatSeconds);
                return true;
            }

            if (repeat && nowTicks >= _controllerActionNextRepeatTicks[actionIndex])
            {
                _controllerActionNextRepeatTicks[actionIndex] = nowTicks + SecondsToTicks(ControllerActionRepeatSeconds);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Clears debounced controller UI state when gamepad support is disabled.
        /// </summary>
        private void ResetControllerActionEdges()
        {
            Array.Clear(_controllerActionWasDown, 0, _controllerActionWasDown.Length);
            Array.Clear(_controllerActionNextRepeatTicks, 0, _controllerActionNextRepeatTicks.Length);
            _controllerSuppressedUiBindings.Clear();
            _controllerGameInputSuppressedUntilRelease = false;
        }

        /// <summary>
        /// Converts a duration in seconds to DateTime ticks.
        /// </summary>
        private static long SecondsToTicks(double seconds)
        {
            return (long)(seconds * TimeSpan.TicksPerSecond);
        }

        /// <summary>
        /// Checks whether any currently active controller input matches one action.
        /// </summary>
        private bool IsControllerActionPressed(ControllerMapAction action, List<string> activeBindings)
        {
            if (activeBindings == null || activeBindings.Count == 0)
            {
                return false;
            }

            List<string> configuredBindings = GetControllerBindings(action);
            if (configuredBindings == null || configuredBindings.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < activeBindings.Count; index++)
            {
                if (configuredBindings.Contains(activeBindings[index], StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true when an emulator overlay currently owns input.
        /// </summary>
        private bool IsControllerMenuContextActive()
        {
            return _mainMenuVisible ||
                _networkOverlayVisible ||
                _saveOverlayVisible ||
                _audioOverlayVisible ||
                _mediaBrowserVisible ||
                _controllerMappingVisible ||
                _resetConfirmVisible;
        }

        /// <summary>
        /// Routes a synthetic menu key to the overlay that is currently active.
        /// </summary>
        private void DispatchControllerMenuKey(Key key)
        {
            if (_saveOverlayVisible)
            {
                if (_networkMode == NetworkSessionMode.Client)
                {
                    HandleRemoteSaveOverlayKeyDown(key);
                }
                else
                {
                    HandleSaveOverlayKeyDown(key);
                }

                return;
            }

            if (_mainMenuVisible)
            {
                HandleMainMenuKeyDown(key);
                return;
            }

            if (_networkOverlayVisible)
            {
                HandleNetworkOverlayKeyDown(key, (KeyModifiers)0);
                return;
            }

            if (_resetConfirmVisible)
            {
                HandleResetConfirmKeyDown(key);
                return;
            }

            if (_mediaBrowserVisible)
            {
                HandleMediaBrowserKeyDown(key);
                return;
            }

            if (_controllerMappingVisible)
            {
                HandleControllerMappingOverlayKeyDown(key);
                return;
            }

            if (_audioOverlayVisible)
            {
                HandleAudioOverlayKeyDown(key);
            }
        }

        /// <summary>
        /// Reads a C64 active-low joystick state from an OpenTK joystick snapshot.
        /// </summary>
        private byte ReadJoystickState(JoystickState joystick)
        {
            byte state = 0x1F;
            if (IsAnyGamepadBindingActive(joystick, _gamepadUpBindings))
            {
                state = (byte)(state & ~0x01);
            }

            if (IsAnyGamepadBindingActive(joystick, _gamepadDownBindings))
            {
                state = (byte)(state & ~0x02);
            }

            if (IsAnyGamepadBindingActive(joystick, _gamepadLeftBindings))
            {
                state = (byte)(state & ~0x04);
            }

            if (IsAnyGamepadBindingActive(joystick, _gamepadRightBindings))
            {
                state = (byte)(state & ~0x08);
            }

            if (IsAnyGamepadBindingActive(joystick, _gamepadFireBindings))
            {
                state = (byte)(state & ~0x10);
            }

            return state;
        }

        private static List<string> ParseGamepadBindings(string value, string fallback)
        {
            string source = string.IsNullOrWhiteSpace(value) ? fallback : value;
            var bindings = new List<string>();
            string[] parts = source.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                string binding = parts[index].Trim();
                if (!string.IsNullOrWhiteSpace(binding))
                {
                    bindings.Add(binding);
                }
            }

            if (bindings.Count == 0 && !string.Equals(source, fallback, StringComparison.OrdinalIgnoreCase))
            {
                return ParseGamepadBindings(fallback, fallback);
            }

            return bindings;
        }

        private static string SerializeGamepadBindings(List<string> bindings)
        {
            return bindings == null || bindings.Count == 0 ? string.Empty : string.Join(";", bindings);
        }

        /// <summary>
        /// Formats all bindings for one overlay row.
        /// </summary>
        private static string FormatGamepadBindings(List<string> bindings)
        {
            if (bindings == null || bindings.Count == 0)
            {
                return "NONE";
            }

            return string.Join(" / ", bindings.Select(FormatGamepadBinding));
        }

        /// <summary>
        /// Formats one controller binding token for readable overlay output.
        /// </summary>
        private static string FormatGamepadBinding(string binding)
        {
            if (TryParseButtonBinding(binding, out int buttonIndex))
            {
                return "BUTTON " + buttonIndex.ToString(CultureInfo.InvariantCulture);
            }

            if (TryParseAxisBinding(binding, out int axisIndex, out int axisDirection))
            {
                return "AXIS " + axisIndex.ToString(CultureInfo.InvariantCulture) + " " + (axisDirection < 0 ? "-" : "+");
            }

            if (TryParseHatBinding(binding, out int hatIndex, out string hatDirection))
            {
                return "HAT " + hatIndex.ToString(CultureInfo.InvariantCulture) + " " + hatDirection.ToUpperInvariant();
            }

            return FormatOverlayValue(binding, 18);
        }

        private static bool IsAnyGamepadBindingActive(JoystickState joystick, List<string> bindings)
        {
            if (bindings == null)
            {
                return false;
            }

            for (int index = 0; index < bindings.Count; index++)
            {
                if (IsGamepadBindingActive(joystick, bindings[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsGamepadBindingActive(JoystickState joystick, string binding)
        {
            if (joystick == null || string.IsNullOrWhiteSpace(binding))
            {
                return false;
            }

            if (TryParseButtonBinding(binding, out int buttonIndex))
            {
                return IsJoystickButtonDown(joystick, buttonIndex);
            }

            if (TryParseAxisBinding(binding, out int axisIndex, out int axisDirection))
            {
                if (axisIndex < 0 || axisIndex >= joystick.AxisCount)
                {
                    return false;
                }

                float value = joystick.GetAxis(axisIndex);
                return axisDirection < 0 ? value < -GamepadAxisDeadZone : value > GamepadAxisDeadZone;
            }

            if (TryParseHatBinding(binding, out int hatIndex, out string hatDirection))
            {
                if (hatIndex < 0 || hatIndex >= joystick.HatCount)
                {
                    return false;
                }

                return IsHatDirectionActive(joystick.GetHat(hatIndex), hatDirection);
            }

            return false;
        }

        private List<string> CaptureActiveControllerInputs()
        {
            var inputs = new List<string>();
            for (int index = 0; index < JoystickStates.Count; index++)
            {
                JoystickState joystick = JoystickStates[index];
                if (joystick == null)
                {
                    continue;
                }

                AddActiveControllerInputs(joystick, inputs);
            }

            return inputs;
        }

        private static void AddActiveControllerInputs(JoystickState joystick, List<string> inputs)
        {
            if (joystick == null || inputs == null)
            {
                return;
            }

            for (int axis = 0; axis < joystick.AxisCount; axis++)
            {
                float value = joystick.GetAxis(axis);
                if (value < -GamepadAxisDeadZone)
                {
                    AddUniqueBinding(inputs, "Axis" + axis + "-");
                }
                else if (value > GamepadAxisDeadZone)
                {
                    AddUniqueBinding(inputs, "Axis" + axis + "+");
                }
            }

            for (int hatIndex = 0; hatIndex < joystick.HatCount; hatIndex++)
            {
                Hat hat = joystick.GetHat(hatIndex);
                if (IsHatUp(hat))
                {
                    AddUniqueBinding(inputs, "Hat" + hatIndex + "Up");
                }

                if (IsHatDown(hat))
                {
                    AddUniqueBinding(inputs, "Hat" + hatIndex + "Down");
                }

                if (IsHatLeft(hat))
                {
                    AddUniqueBinding(inputs, "Hat" + hatIndex + "Left");
                }

                if (IsHatRight(hat))
                {
                    AddUniqueBinding(inputs, "Hat" + hatIndex + "Right");
                }
            }

            for (int button = 0; button < joystick.ButtonCount; button++)
            {
                if (joystick.IsButtonDown(button))
                {
                    AddUniqueBinding(inputs, "Button" + button);
                }
            }
        }

        private static void AddUniqueBinding(List<string> inputs, string binding)
        {
            if (!inputs.Contains(binding, StringComparer.OrdinalIgnoreCase))
            {
                inputs.Add(binding);
            }
        }

        /// <summary>
        /// Parses a controller button binding such as Button0.
        /// </summary>
        private static bool TryParseButtonBinding(string binding, out int buttonIndex)
        {
            buttonIndex = 0;
            const string prefix = "Button";
            if (string.IsNullOrWhiteSpace(binding) || !binding.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string numberText = binding.Substring(prefix.Length);
            return int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out buttonIndex) && buttonIndex >= 0;
        }

        /// <summary>
        /// Parses a controller axis binding such as Axis1- or Axis0+.
        /// </summary>
        private static bool TryParseAxisBinding(string binding, out int axisIndex, out int axisDirection)
        {
            axisIndex = 0;
            axisDirection = 0;
            const string prefix = "Axis";
            if (string.IsNullOrWhiteSpace(binding) || !binding.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            char directionCharacter = binding[binding.Length - 1];
            if (directionCharacter == '-')
            {
                axisDirection = -1;
            }
            else if (directionCharacter == '+')
            {
                axisDirection = 1;
            }
            else
            {
                return false;
            }

            string numberText = binding.Substring(prefix.Length, binding.Length - prefix.Length - 1);
            return int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out axisIndex) && axisIndex >= 0;
        }

        /// <summary>
        /// Parses a controller hat binding such as Hat0Up.
        /// </summary>
        private static bool TryParseHatBinding(string binding, out int hatIndex, out string hatDirection)
        {
            hatIndex = 0;
            hatDirection = string.Empty;
            const string prefix = "Hat";
            if (string.IsNullOrWhiteSpace(binding) || !binding.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int directionStart = prefix.Length;
            while (directionStart < binding.Length && char.IsDigit(binding[directionStart]))
            {
                directionStart++;
            }

            if (directionStart == prefix.Length || directionStart >= binding.Length)
            {
                return false;
            }

            string numberText = binding.Substring(prefix.Length, directionStart - prefix.Length);
            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hatIndex) || hatIndex < 0)
            {
                return false;
            }

            hatDirection = binding.Substring(directionStart);
            return string.Equals(hatDirection, "Up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hatDirection, "Down", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hatDirection, "Left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hatDirection, "Right", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsJoystickButtonDown(JoystickState joystick, int index)
        {
            return index >= 0 && index < joystick.ButtonCount && joystick.IsButtonDown(index);
        }

        /// <summary>
        /// Checks whether a hat state contains the requested direction.
        /// </summary>
        private static bool IsHatDirectionActive(Hat hat, string direction)
        {
            if (string.Equals(direction, "Up", StringComparison.OrdinalIgnoreCase))
            {
                return IsHatUp(hat);
            }

            if (string.Equals(direction, "Down", StringComparison.OrdinalIgnoreCase))
            {
                return IsHatDown(hat);
            }

            if (string.Equals(direction, "Left", StringComparison.OrdinalIgnoreCase))
            {
                return IsHatLeft(hat);
            }

            if (string.Equals(direction, "Right", StringComparison.OrdinalIgnoreCase))
            {
                return IsHatRight(hat);
            }

            return false;
        }

        private static bool IsHatUp(Hat hat)
        {
            return hat == Hat.Up || hat == Hat.LeftUp || hat == Hat.RightUp;
        }

        private static bool IsHatDown(Hat hat)
        {
            return hat == Hat.Down || hat == Hat.LeftDown || hat == Hat.RightDown;
        }

        private static bool IsHatLeft(Hat hat)
        {
            return hat == Hat.Left || hat == Hat.LeftUp || hat == Hat.LeftDown;
        }

        private static bool IsHatRight(Hat hat)
        {
            return hat == Hat.Right || hat == Hat.RightUp || hat == Hat.RightDown;
        }

        /// <summary>
        /// Broadcasts generated SID audio to connected network clients.
        /// </summary>
        /// <param name="buffer">PCM bytes emitted by the SID.</param>
        /// <param name="count">Number of valid bytes in <paramref name="buffer"/>.</param>
        private void HandleSystemAudioBytesGenerated(byte[] buffer, int count)
        {
            C64NetServer server = _networkServer;
            if (server != null && server.IsRunning && count > 0)
            {
                // Audio is generated by the emulation thread; the server makes its own
                // bounded per-client queues from this immutable chunk.
                server.BroadcastAudio(buffer, count);
            }
        }

        /// <summary>
        /// Applies the current aggregate client joystick state to the emulated machine.
        /// </summary>
        private void ApplyNetworkServerJoystickInput()
        {
            C64NetServer server = _networkServer;
            if (server == null || !server.IsRunning)
            {
                if (_networkMode == NetworkSessionMode.Host)
                {
                    // If the server stopped asynchronously, release all remote input
                    // lines and fall back to local mode.
                    _system.ClearNetworkJoystickState();
                    _system.ClearNetworkKeyboardState();
                    _networkAppliedKeyboardKeys.Clear();
                    _networkMode = NetworkSessionMode.Local;
                }

                return;
            }

            // The server aggregates clients by granted permission, not by requested role.
            _system.SetNetworkJoystickState(JoystickPort.Port1, server.GetAggregatedJoystickState(C64NetJoystickPermission.Port1));
            _system.SetNetworkJoystickState(JoystickPort.Port2, server.GetAggregatedJoystickState(C64NetJoystickPermission.Port2));
            ApplyNetworkServerKeyboardInput(server);
        }

        /// <summary>
        /// Applies the aggregate permitted remote keyboard state to the host machine.
        /// </summary>
        /// <param name="server">Running network server that owns client input snapshots.</param>
        private void ApplyNetworkServerKeyboardInput(C64NetServer server)
        {
            HashSet<Key> pressedKeys = server.GetAggregatedKeyboardKeys();
            var previousKeys = new List<Key>(_networkAppliedKeyboardKeys);
            for (int index = 0; index < previousKeys.Count; index++)
            {
                Key key = previousKeys[index];
                if (!pressedKeys.Contains(key))
                {
                    _system.SetNetworkKeyState(key, false);
                }
            }

            foreach (Key key in pressedKeys)
            {
                if (!_networkAppliedKeyboardKeys.Contains(key))
                {
                    _system.SetNetworkKeyState(key, true);
                }
            }

            _networkAppliedKeyboardKeys.Clear();
            foreach (Key key in pressedKeys)
            {
                _networkAppliedKeyboardKeys.Add(key);
            }
        }

        /// <summary>
        /// Broadcasts each completed C64 video frame once.
        /// </summary>
        private void BroadcastNetworkCompletedFrameIfReady()
        {
            C64NetServer server = _networkServer;
            if (server == null || !server.IsRunning || _system == null || _networkBroadcastFrameSnapshot == null)
            {
                return;
            }

            lock (_networkBroadcastSync)
            {
                long completedFrameId;
                lock (_system.SyncRoot)
                {
                    completedFrameId = _system.FrameBuffer.CompletedFrameId;
                    if (completedFrameId == _lastBroadcastNetworkCompletedFrameId)
                    {
                        // The render loop can execute many times per C64 frame. Do not
                        // re-send identical completed framebuffer snapshots.
                        return;
                    }

                    // Copy while holding the machine lock so the VIC cannot mutate the
                    // frame buffer halfway through the network snapshot.
                    Array.Copy(_system.FrameBuffer.CompletedPixels, _networkBroadcastFrameSnapshot, _networkBroadcastFrameSnapshot.Length);
                }

                _lastBroadcastNetworkCompletedFrameId = completedFrameId;
                server.BroadcastVideoFrame(_networkBroadcastFrameSnapshot, _system.Model.VisibleWidth, _system.Model.VisibleHeight);
                Interlocked.Increment(ref _networkFramesSentCounter);
            }
        }

        /// <summary>
        /// Tells remote clients whether the host is currently inside a local menu.
        /// </summary>
        private void BroadcastNetworkHostOverlayStatus()
        {
            C64NetServer server = _networkServer;
            if (server == null || !server.IsRunning)
            {
                _lastBroadcastNetworkHostOverlayStatus = string.Empty;
                return;
            }

            string status = GetNetworkHostOverlayStatus();
            if (string.Equals(status, _lastBroadcastNetworkHostOverlayStatus, StringComparison.Ordinal))
            {
                // The server also de-duplicates, but skipping here avoids needless calls
                // from the render path.
                return;
            }

            _lastBroadcastNetworkHostOverlayStatus = status;
            server.SetHostOverlayStatus(status);
        }

        /// <summary>
        /// Returns the status popup text that should be shown to remote clients.
        /// </summary>
        /// <returns>Host menu status text, or an empty string when the host is live.</returns>
        private string GetNetworkHostOverlayStatus()
        {
            if (_resetConfirmVisible)
            {
                return "SERVER RESET PROMPT";
            }

            if (_mediaBrowserVisible)
            {
                return "SERVER IN MEDIA MENU";
            }

            if (_networkOverlayVisible)
            {
                return "SERVER IN NETWORK MENU";
            }

            if (_saveOverlayVisible)
            {
                return "SERVER IN SAVE MENU";
            }

            if (_mainMenuVisible)
            {
                return "SERVER IN MAIN MENU";
            }

            if (_audioOverlayVisible)
            {
                return "SERVER IN SETTINGS MENU";
            }

            return string.Empty;
        }

        /// <summary>
        /// Draws and services the remote-client presentation path.
        /// </summary>
        /// <param name="time">Elapsed render time in seconds.</param>
        private void DrawRemoteClientFrame(double time)
        {
            if (_networkClient == null || !_networkClient.IsConnected)
            {
                // If the background client detects a disconnect, the render path performs
                // visible cleanup and leaves the user on a clear status screen.
                StopNetworkClientSession();
                DrawFilledRectangle(0, 0, PixelsWidth, PixelsHeight, 0, 0, 0);
                DrawOverlayText(28, (PixelsHeight / 2) - 10, "REMOTE SESSION CLOSED", 1, 240, 248, 255);
                return;
            }

            SendRemoteClientJoystickInput(false, time);
            _networkClient.PollLatency();
            UpdateNetworkReceiveFps();
            UpdateNetworkTrafficRates();

            uint[] pixels;
            int width;
            int height;
            long frameId;
            lock (_networkFrameSync)
            {
                // Promote at most one received frame per render. The receive thread writes
                // only the pending buffer; the render thread owns the displayed buffer.
                if (_networkPendingFramePixels != null && _networkPendingFrameId != _networkFrameId)
                {
                    uint[] oldDisplayedPixels = _networkFramePixels;
                    _networkFramePixels = _networkPendingFramePixels;
                    _networkPendingFramePixels = oldDisplayedPixels;
                    _networkFrameWidth = _networkPendingFrameWidth;
                    _networkFrameHeight = _networkPendingFrameHeight;
                    _networkFrameId = _networkPendingFrameId;
                    _networkPendingFrameId = _networkFrameId;
                }

                pixels = _networkFramePixels;
                width = _networkFrameWidth;
                height = _networkFrameHeight;
                frameId = _networkFrameId;
            }

            if (pixels != null && width > 0 && height > 0)
            {
                // DrawFrame applies the client's local SHARP/CRT/TV filter to the raw
                // server framebuffer.
                DrawFrame(pixels, width, height);
                _lastDisplayedNetworkFrameId = frameId;
            }
            else
            {
                DrawFilledRectangle(0, 0, PixelsWidth, PixelsHeight, 0, 0, 0);
                DrawOverlayText(24, (PixelsHeight / 2) - 10, "WAITING FOR SERVER FRAME", 1, 240, 248, 255);
            }

            if (_mainMenuVisible)
            {
                DrawMainMenuOverlay();
            }
            else if (_networkOverlayVisible)
            {
                DrawNetworkOverlay();
            }
            else if (_saveOverlayVisible)
            {
                DrawSaveOverlay();
            }
            else if (_mediaBrowserVisible)
            {
                DrawStandaloneMediaBrowserOverlay();
            }
            else if (_audioOverlayVisible)
            {
                // In remote mode the settings overlay is mostly read-only, but display,
                // filter, and zoom controls still apply to the local presentation.
                DrawAudioOverlay(
                    _system.SidMasterVolume,
                    _system.SidNoiseLevel,
                    _system.CurrentSidChipModel,
                    _system.CurrentJoystickPort,
                    _system.MountedMedia,
                    _system.EnableLoadHack,
                    _system.ForceSoftwareIecTransport,
                    _system.EnableInputInjection);
            }

            DrawNetworkStatusToast();
            DrawNetworkHostOverlayPopup();

            Title = string.Format(
                "C64 Emulator REMOTE {0} - NET:{1} FPS PING:{2} RENDER:{3}",
                FormatNetworkJoystickRight(_networkClient != null ? _networkClient.Permission : C64NetJoystickPermission.Observer),
                _networkReceiveFps,
                FormatNetworkLatency(GetNetworkDisplayLatencyMilliseconds()),
                FPS);
        }

        /// <summary>
        /// Updates the visible receive FPS counter from decoded network frames.
        /// </summary>
        private void UpdateNetworkReceiveFps()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (_networkReceiveFpsNextTicks <= 0)
            {
                _networkReceiveFpsWindowStartTicks = nowTicks;
                _networkReceiveFpsNextTicks = nowTicks + TimeSpan.TicksPerSecond;
                return;
            }

            if (nowTicks < _networkReceiveFpsNextTicks)
            {
                return;
            }

            // Reset the counter each one-second window so the title reflects recent
            // network delivery instead of a lifetime average.
            long elapsedTicks = Math.Max(TimeSpan.TicksPerMillisecond, nowTicks - _networkReceiveFpsWindowStartTicks);
            int frameCount = Interlocked.Exchange(ref _networkFramesReceivedCounter, 0);
            _networkReceiveFps = (int)Math.Round((frameCount * (double)TimeSpan.TicksPerSecond) / elapsedTicks);
            _networkReceiveFpsWindowStartTicks = nowTicks;
            _networkReceiveFpsNextTicks = nowTicks + TimeSpan.TicksPerSecond;
        }

        /// <summary>
        /// Updates the visible send FPS counter from host frame broadcasts.
        /// </summary>
        private void UpdateNetworkSendFps()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (_networkSendFpsNextTicks <= 0)
            {
                _networkSendFpsWindowStartTicks = nowTicks;
                _networkSendFpsNextTicks = nowTicks + TimeSpan.TicksPerSecond;
                return;
            }

            if (nowTicks < _networkSendFpsNextTicks)
            {
                return;
            }

            // This measures completed C64 frames sent to the server transport, not the
            // window renderer FPS.
            long elapsedTicks = Math.Max(TimeSpan.TicksPerMillisecond, nowTicks - _networkSendFpsWindowStartTicks);
            int frameCount = Interlocked.Exchange(ref _networkFramesSentCounter, 0);
            _networkSendFps = (int)Math.Round((frameCount * (double)TimeSpan.TicksPerSecond) / elapsedTicks);
            _networkSendFpsWindowStartTicks = nowTicks;
            _networkSendFpsNextTicks = nowTicks + TimeSpan.TicksPerSecond;
        }

        /// <summary>
        /// Updates the visible network throughput counters from transport byte totals.
        /// </summary>
        private void UpdateNetworkTrafficRates()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long bytesSent = GetNetworkTransportBytesSent();
            long bytesReceived = GetNetworkTransportBytesReceived();
            if (_networkTrafficNextTicks <= 0 ||
                bytesSent < _networkTrafficLastBytesSent ||
                bytesReceived < _networkTrafficLastBytesReceived)
            {
                ResetNetworkTrafficCounters(bytesSent, bytesReceived, nowTicks);
                return;
            }

            if (nowTicks < _networkTrafficNextTicks)
            {
                return;
            }

            long elapsedTicks = Math.Max(TimeSpan.TicksPerMillisecond, nowTicks - _networkTrafficWindowStartTicks);
            double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
            _networkSendKilobytesPerSecond = Math.Max(0.0, (bytesSent - _networkTrafficLastBytesSent) / 1024.0 / elapsedSeconds);
            _networkReceiveKilobytesPerSecond = Math.Max(0.0, (bytesReceived - _networkTrafficLastBytesReceived) / 1024.0 / elapsedSeconds);
            _networkTrafficLastBytesSent = bytesSent;
            _networkTrafficLastBytesReceived = bytesReceived;
            _networkTrafficWindowStartTicks = nowTicks;
            _networkTrafficNextTicks = nowTicks + TimeSpan.TicksPerSecond;
        }

        /// <summary>
        /// Resets throughput windows to the current transport byte totals.
        /// </summary>
        /// <param name="bytesSent">Current total bytes sent.</param>
        /// <param name="bytesReceived">Current total bytes received.</param>
        /// <param name="nowTicks">Current UTC ticks.</param>
        private void ResetNetworkTrafficCounters(long bytesSent, long bytesReceived, long nowTicks)
        {
            _networkSendKilobytesPerSecond = 0.0;
            _networkReceiveKilobytesPerSecond = 0.0;
            _networkTrafficLastBytesSent = Math.Max(0, bytesSent);
            _networkTrafficLastBytesReceived = Math.Max(0, bytesReceived);
            _networkTrafficWindowStartTicks = nowTicks;
            _networkTrafficNextTicks = nowTicks + TimeSpan.TicksPerSecond;
        }

        /// <summary>
        /// Resets throughput windows without reading a transport.
        /// </summary>
        private void ResetNetworkTrafficCounters()
        {
            ResetNetworkTrafficCounters(GetNetworkTransportBytesSent(), GetNetworkTransportBytesReceived(), DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Reads the active host/client transport's total bytes sent.
        /// </summary>
        /// <returns>Total bytes sent by the current network role.</returns>
        private long GetNetworkTransportBytesSent()
        {
            if (_networkMode == NetworkSessionMode.Host && _networkServer != null)
            {
                return _networkServer.BytesSent;
            }

            if (_networkMode == NetworkSessionMode.Client && _networkClient != null)
            {
                return _networkClient.BytesSent;
            }

            return 0;
        }

        /// <summary>
        /// Reads the active host/client transport's total bytes received.
        /// </summary>
        /// <returns>Total bytes received by the current network role.</returns>
        private long GetNetworkTransportBytesReceived()
        {
            if (_networkMode == NetworkSessionMode.Host && _networkServer != null)
            {
                return _networkServer.BytesReceived;
            }

            if (_networkMode == NetworkSessionMode.Client && _networkClient != null)
            {
                return _networkClient.BytesReceived;
            }

            return 0;
        }

        /// <summary>
        /// Starts the host-side network server.
        /// </summary>
        private void StartNetworkServer()
        {
            if (_networkMode == NetworkSessionMode.Client)
            {
                // Hosting and joining are mutually exclusive because only one side owns
                // the active C64 simulation at a time.
                StopNetworkClientSession();
            }

            if (_networkServer == null)
            {
                _networkServer = new C64NetServer(_system.Model.VisibleWidth, _system.Model.VisibleHeight);
                _networkServer.StatusChanged += HandleNetworkStatusChanged;
            }

            try
            {
                if (_networkTransportMode == C64NetTransportMode.Relay)
                {
                    _networkServer.StartRelay(_networkHost, _networkRelayPort, NormalizeNetworkConnectionId(_networkConnectionId), _networkServerPassword);
                }
                else
                {
                    _networkServer.Start(_networkServerPort, _networkServerPassword);
                    _networkServerPort = _networkServer.Port;
                }

                _networkMode = NetworkSessionMode.Host;
                // Reset network counters at session start so the title bar starts from
                // a meaningful fresh one-second window.
                _lastBroadcastNetworkHostOverlayStatus = string.Empty;
                _lastBroadcastNetworkCompletedFrameId = -1;
                Interlocked.Exchange(ref _networkFramesSentCounter, 0);
                _networkSendFps = 0;
                _networkSendFpsWindowStartTicks = DateTime.UtcNow.Ticks;
                _networkSendFpsNextTicks = _networkSendFpsWindowStartTicks + TimeSpan.TicksPerSecond;
                ResetNetworkTrafficCounters();
                ShowNetworkStatus(_networkTransportMode == C64NetTransportMode.Relay ? "RELAY SERVER READY" : "TLS SERVER LISTENING");
                _system.LocalAudioEnabled = true;
                SaveSettings();
            }
            catch (C64NetTlsException ex)
            {
                Debug.WriteLine(ex);
                if (ex.IsCertificateChanged)
                {
                    ShowNetworkTlsCertificateChanged(ex.PinChange, NetworkTlsRetryAction.StartServer);
                }
                else
                {
                    ShowNetworkStatus(FormatNetworkStartFailure(ex));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                ShowNetworkStatus(FormatNetworkStartFailure(ex));
            }
        }

        /// <summary>
        /// Formats server start failures without hiding useful relay/TLS diagnostics.
        /// </summary>
        /// <param name="exception">Exception thrown by the network start path.</param>
        /// <returns>Short overlay status text.</returns>
        private static string FormatNetworkStartFailure(Exception exception)
        {
            if (exception != null && !string.IsNullOrWhiteSpace(exception.Message))
            {
                string message = exception.Message.Trim().ToUpperInvariant();
                return message.Length <= 44 ? message : message.Substring(0, 44);
            }

            return "SERVER START FAILED";
        }

        /// <summary>
        /// Opens the explicit warning prompt for a changed pinned TLS certificate.
        /// </summary>
        /// <param name="pinChange">Old/new certificate-pin details from the TLS layer.</param>
        /// <param name="retryAction">Network action to retry if the user accepts the new pin.</param>
        private void ShowNetworkTlsCertificateChanged(C64NetTlsPinChange pinChange, NetworkTlsRetryAction retryAction)
        {
            if (pinChange == null)
            {
                ShowNetworkStatus("TLS CERT CHANGED");
                return;
            }

            _pendingNetworkTlsPinChange = pinChange;
            _pendingNetworkTlsRetryAction = retryAction;
            _networkTlsCertificatePromptVisible = true;
            _networkOverlayVisible = true;
            ShowNetworkStatus("TLS CERT CHANGED");
        }

        /// <summary>
        /// Clears the changed-certificate prompt state.
        /// </summary>
        private void ClearNetworkTlsCertificatePrompt()
        {
            _networkTlsCertificatePromptVisible = false;
            _pendingNetworkTlsPinChange = null;
            _pendingNetworkTlsRetryAction = NetworkTlsRetryAction.None;
        }

        /// <summary>
        /// Accepts the newly presented certificate pin and retries the interrupted action.
        /// </summary>
        private void AcceptNetworkTlsCertificateChange()
        {
            C64NetTlsPinChange pinChange = _pendingNetworkTlsPinChange;
            NetworkTlsRetryAction retryAction = _pendingNetworkTlsRetryAction;
            if (pinChange == null)
            {
                ClearNetworkTlsCertificatePrompt();
                ShowNetworkStatus("TLS PIN ABORTED");
                return;
            }

            C64NetTls.ReplaceTrustedServerCertificate(pinChange.Host, pinChange.Port, pinChange.NewFingerprint);
            ClearNetworkTlsCertificatePrompt();
            ShowNetworkStatus("TLS PIN REPLACED");
            if (retryAction == NetworkTlsRetryAction.StartServer)
            {
                StartNetworkServer();
            }
            else if (retryAction == NetworkTlsRetryAction.StartClient)
            {
                StartNetworkClientSession();
            }
        }

        /// <summary>
        /// Aborts the current changed-certificate prompt without changing the stored pin.
        /// </summary>
        private void AbortNetworkTlsCertificateChange()
        {
            ClearNetworkTlsCertificatePrompt();
            ShowNetworkStatus("TLS PIN UNCHANGED");
        }

        /// <summary>
        /// Stops the host-side network server.
        /// </summary>
        private void StopNetworkServer()
        {
            if (_networkServer != null)
            {
                _networkServer.Stop();
            }

            if (_system != null)
            {
                // Clear remote input as soon as the host stops, otherwise the last
                // active-low state could remain latched in CIA1.
                _system.ClearNetworkJoystickState();
                _system.ClearNetworkKeyboardState();
            }

            _networkAppliedKeyboardKeys.Clear();

            lock (_networkClientsSync)
            {
                _networkClients.Clear();
            }

            if (_networkMode == NetworkSessionMode.Host)
            {
                _networkMode = NetworkSessionMode.Local;
            }

            // Reset counters and cached state so a later server session starts cleanly.
            _lastBroadcastNetworkHostOverlayStatus = string.Empty;
            _lastBroadcastNetworkCompletedFrameId = -1;
            Interlocked.Exchange(ref _networkFramesSentCounter, 0);
            _networkSendFps = 0;
            _networkSendFpsWindowStartTicks = 0;
            _networkSendFpsNextTicks = 0;
            ResetNetworkTrafficCounters(0, 0, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Starts a remote-client session and pauses local emulation.
        /// </summary>
        private void StartNetworkClientSession()
        {
            if (_networkMode == NetworkSessionMode.Host)
            {
                // Joining a remote host means this window stops owning the authoritative
                // C64, so an existing local server must be stopped first.
                StopNetworkServer();
            }

            if (_networkClient == null)
            {
                _networkClient = new C64NetClient();
                _networkClient.FrameReceived += HandleNetworkFrameReceived;
                _networkClient.ClientListReceived += HandleNetworkClientListReceived;
                _networkClient.HostOverlayStatusChanged += HandleNetworkHostOverlayStatusChanged;
                _networkClient.StatusChanged += HandleNetworkStatusChanged;
            }

            string status;
            bool connected;
            try
            {
                connected = _networkTransportMode == C64NetTransportMode.Relay
                    ? _networkClient.ConnectRelay(_networkHost, _networkRelayPort, NormalizeNetworkConnectionId(_networkConnectionId), _networkClientPassword, _networkRequestedRole, NormalizeNetworkPlayerName(_networkPlayerName), out status)
                    : _networkClient.Connect(_networkHost, _networkClientPort, _networkClientPassword, _networkRequestedRole, NormalizeNetworkPlayerName(_networkPlayerName), out status);
            }
            catch (C64NetTlsException ex)
            {
                Debug.WriteLine(ex);
                if (ex.IsCertificateChanged)
                {
                    ShowNetworkTlsCertificateChanged(ex.PinChange, NetworkTlsRetryAction.StartClient);
                    return;
                }

                ShowNetworkStatus(ex.Message);
                return;
            }

            if (!connected)
            {
                ShowNetworkStatus(status);
                return;
            }

            // Once connected, the local emulator is parked. The remote host provides the
            // C64 video/audio stream and receives joystick input from this window.
            StopEmulation();
            _system.LocalAudioEnabled = false;
            _networkMode = NetworkSessionMode.Client;
            _remoteKeyboardJoystickState = 0xFF;
            _remoteGamepadJoystickState = 0xFF;
            _lastRemoteJoystickState = 0xFF;
            _remoteAltCursorKeys.Clear();
            _networkHostOverlayStatusText = string.Empty;
            Interlocked.Exchange(ref _networkFramesReceivedCounter, 0);
            _networkReceiveFps = 0;
            _networkReceiveFpsWindowStartTicks = DateTime.UtcNow.Ticks;
            _networkReceiveFpsNextTicks = _networkReceiveFpsWindowStartTicks + TimeSpan.TicksPerSecond;
            ResetNetworkTrafficCounters();
            ShowNetworkStatus(status);
            SaveSettings();
        }

        /// <summary>
        /// Leaves a remote-client session and resumes local emulation.
        /// </summary>
        private void StopNetworkClientSession()
        {
            if (_networkClient != null)
            {
                _networkClient.Disconnect();
            }

            lock (_networkFrameSync)
            {
                // Drop remote frame references so the next local/remote session cannot
                // accidentally draw a stale host image.
                _networkFramePixels = null;
                _networkPendingFramePixels = null;
                _networkFrameWidth = 0;
                _networkFrameHeight = 0;
                _networkFrameId = 0;
                _networkPendingFrameWidth = 0;
                _networkPendingFrameHeight = 0;
                _networkPendingFrameId = 0;
            }

            lock (_networkClientsSync)
            {
                _networkClients.Clear();
            }

            _networkHostOverlayStatusText = string.Empty;
            _remoteAltCursorKeys.Clear();
            Interlocked.Exchange(ref _networkFramesReceivedCounter, 0);
            _networkReceiveFps = 0;
            _networkReceiveFpsWindowStartTicks = 0;
            _networkReceiveFpsNextTicks = 0;
            ResetNetworkTrafficCounters(0, 0, DateTime.UtcNow.Ticks);

            if (_system != null)
            {
                // Local audio was disabled while remote audio played through C64NetClient.
                _system.LocalAudioEnabled = true;
            }

            if (_networkMode == NetworkSessionMode.Client)
            {
                _networkMode = NetworkSessionMode.Local;
                if (!_shutdown.IsCancellationRequested && _emulationTask == null)
                {
                    // Re-enter normal standalone emulation after leaving a remote session.
                    StartEmulation();
                }
            }
        }

        /// <summary>
        /// Stores the newest remote frame received by the client transport.
        /// </summary>
        /// <param name="frame">Decoded remote frame.</param>
        private void HandleNetworkFrameReceived(C64NetVideoFrame frame)
        {
            if (frame == null || frame.Pixels == null)
            {
                return;
            }

            lock (_networkFrameSync)
            {
                // Copy into pending storage. The render thread swaps this into display
                // storage, so the receive thread never overwrites pixels being drawn.
                int pixelCount = frame.Width * frame.Height;
                if (_networkPendingFramePixels == null || _networkPendingFramePixels.Length != pixelCount)
                {
                    _networkPendingFramePixels = new uint[pixelCount];
                }

                Array.Copy(frame.Pixels, _networkPendingFramePixels, pixelCount);
                _networkPendingFrameWidth = frame.Width;
                _networkPendingFrameHeight = frame.Height;
                _networkPendingFrameId = frame.FrameId;
            }

            Interlocked.Increment(ref _networkFramesReceivedCounter);
        }

        /// <summary>
        /// Stores the newest client list received from the server or local host.
        /// </summary>
        /// <param name="clients">Client snapshots from the transport layer.</param>
        private void HandleNetworkClientListReceived(List<C64NetClientSnapshot> clients)
        {
            lock (_networkClientsSync)
            {
                _networkClients = clients ?? new List<C64NetClientSnapshot>();
                ClampNetworkSelectedClientIndex();
            }
        }

        /// <summary>
        /// Stores the persistent popup text describing the host's current menu.
        /// </summary>
        /// <param name="status">Host overlay status text.</param>
        private void HandleNetworkHostOverlayStatusChanged(string status)
        {
            _networkHostOverlayStatusText = status ?? string.Empty;
        }

        /// <summary>
        /// Converts transport status callbacks into overlay status toasts.
        /// </summary>
        /// <param name="status">Status text from server or client transport.</param>
        private void HandleNetworkStatusChanged(string status)
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                ShowNetworkStatus(status);
            }
        }

        /// <summary>
        /// Shows a network status line and short toast.
        /// </summary>
        /// <param name="status">Status text to display.</param>
        private void ShowNetworkStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            _networkStatusText = status;
            _networkStatusToastText = status;
            // Toasts are time-based so they can be drawn from both local and remote
            // render paths without owning a separate UI animation object.
            Interlocked.Exchange(ref _networkStatusToastUntilTicks, DateTime.UtcNow.AddSeconds(3.0).Ticks);
        }

        /// <summary>
        /// Handles key input while the window is connected to a remote host.
        /// </summary>
        /// <param name="keyEventArgs">Raw key event from the frontend.</param>
        private void HandleRemoteClientKeyDown(SharpPixels.KeyEventArgs keyEventArgs)
        {
            Key key = keyEventArgs.Key;
            if (key == Key.F12)
            {
                // The save overlay remains available as a read-only remote status surface,
                // but it cannot load/save local C64 state while the remote host owns state.
                _mainMenuVisible = false;
                _networkOverlayVisible = false;
                _audioOverlayVisible = false;
                _mediaBrowserVisible = false;
                _resetConfirmVisible = false;
                _saveOverlayVisible = !_saveOverlayVisible;
                _saveOverlayStatusText = "REMOTE SESSION";
                return;
            }

            if (_saveOverlayVisible)
            {
                if (key == Key.Escape || key == Key.F12)
                {
                    CloseSaveOverlay();
                }

                return;
            }

            if (key == Key.F10)
            {
                ToggleMainMenu();
                return;
            }

            if (_mainMenuVisible)
            {
                HandleMainMenuKeyDown(key);
                return;
            }

            if (_controllerMappingVisible)
            {
                HandleControllerMappingOverlayKeyDown(key);
                return;
            }

            if (_audioOverlayVisible)
            {
                HandleAudioOverlayKeyDown(key);
                return;
            }

            if (key == Key.F11)
            {
                ToggleDisplayMode();
                return;
            }

            if (key == Key.F9)
            {
                // Turbo is server-controlled because only the host runs emulation.
                ShowNetworkStatus("TURBO DISABLED REMOTE");
                return;
            }

            if (HandleRemoteAltCursorKeyDown(key, keyEventArgs.Modifiers))
            {
                return;
            }

            SendRemoteClientKeyboardKey(key, true);
            HandleRemoteClientJoystickKey(key, true);
        }

        /// <summary>
        /// Sends a remote C64 keyboard event to the host when the key is not local UI.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="pressed">True for key down, false for key up.</param>
        private void SendRemoteClientKeyboardKey(Key key, bool pressed)
        {
            C64NetClient client = _networkClient;
            if (client == null || !client.IsConnected || IsRemoteClientKeyboardReservedKey(key))
            {
                return;
            }

            client.SendKeyboardKey(_system.MapHostKeyboardLayoutKey(key), pressed);
        }

        /// <summary>
        /// Handles Alt+arrow as a remote C64 cursor key chord.
        /// </summary>
        private bool HandleRemoteAltCursorKeyDown(Key key, KeyModifiers modifiers)
        {
            if (!IsAltCursorChord(key, modifiers))
            {
                return false;
            }

            ReleaseRemoteAltModifierKeys();
            if (_remoteAltCursorKeys.Add(key))
            {
                C64NetClient client = _networkClient;
                if (client != null && client.IsConnected)
                {
                    client.SendKeyboardKey(_system.MapHostKeyboardLayoutKey(key), true);
                }
            }

            return true;
        }

        /// <summary>
        /// Releases a remote C64 cursor key chord if it was started with Alt+arrow.
        /// </summary>
        private bool HandleRemoteAltCursorKeyUp(Key key)
        {
            if (_remoteAltCursorKeys.Remove(key))
            {
                C64NetClient client = _networkClient;
                if (client != null && client.IsConnected)
                {
                    client.SendKeyboardKey(_system.MapHostKeyboardLayoutKey(key), false);
                }

                return true;
            }

            if (IsAltKey(key))
            {
                ReleaseRemoteAltCursorKeys();
            }

            return false;
        }

        private void ReleaseRemoteAltCursorKeys()
        {
            foreach (Key key in _remoteAltCursorKeys.ToArray())
            {
                C64NetClient client = _networkClient;
                if (client != null && client.IsConnected)
                {
                    client.SendKeyboardKey(_system.MapHostKeyboardLayoutKey(key), false);
                }
            }

            _remoteAltCursorKeys.Clear();
        }

        private void ReleaseRemoteAltModifierKeys()
        {
            SendRemoteClientKeyboardKey(Key.AltLeft, false);
            SendRemoteClientKeyboardKey(Key.LAlt, false);
        }

        private static bool IsAltCursorChord(Key key, KeyModifiers modifiers)
        {
            return IsArrowKey(key) && (modifiers & KeyModifiers.Alt) == KeyModifiers.Alt;
        }

        private static bool IsAltKey(Key key)
        {
            return key == Key.AltLeft || key == Key.LAlt;
        }

        /// <summary>
        /// Checks whether a key is reserved for the client window instead of the C64.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <returns>True for emulator control keys.</returns>
        private bool IsRemoteClientKeyboardReservedKey(Key key)
        {
            byte joystickMask;
            if (TryGetJoystickMask(key, out joystickMask))
            {
                // Match the local CIA behavior: cursor keys and left control are
                // joystick controls, not simultaneous C64 keyboard-matrix keys. Sending
                // left control as a keyboard key can pull matrix row 2 low and look like
                // joystick-port-1 left to games that poll both ports.
                return true;
            }

            return key == Key.F9 || key == Key.F10 || key == Key.F11 || key == Key.F12;
        }

        /// <summary>
        /// Polls local gamepads while connected as a remote client.
        /// </summary>
        private void PollRemoteClientGamepadInput()
        {
            if (!_gamepadEnabled)
            {
                // Gamepad disabled means neutral remote gamepad state, while keyboard
                // joystick keys may still contribute.
                _remoteGamepadJoystickState = 0xFF;
                SendRemoteClientJoystickInput(false, 0.0);
                return;
            }

            byte joystickState = 0x1F;
            bool connected = false;
            for (int index = 0; index < JoystickStates.Count; index++)
            {
                JoystickState joystick = JoystickStates[index];
                if (joystick == null)
                {
                    continue;
                }

                if (joystick.AxisCount <= 0 && joystick.ButtonCount <= 0 && joystick.HatCount <= 0)
                {
                    continue;
                }

                connected = true;
                try
                {
                    joystickState = ReadJoystickState(joystick);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    continue;
                }

                if ((joystickState & 0x1F) != 0x1F)
                {
                    // Prefer the first pad that is actively pressed this frame.
                    break;
                }
            }

            _gamepadConnected = connected;
            _remoteGamepadJoystickState = connected ? joystickState : (byte)0xFF;
            SendRemoteClientJoystickInput(false, 0.0);
        }

        /// <summary>
        /// Updates the remote keyboard joystick state for one key press/release.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="pressed">True for key down, false for key up.</param>
        private void HandleRemoteClientJoystickKey(Key key, bool pressed)
        {
            byte mask;
            if (!TryGetJoystickMask(key, out mask))
            {
                return;
            }

            if (pressed)
            {
                // C64 joystick lines are active-low: clearing a bit means pressed.
                _remoteKeyboardJoystickState = (byte)(_remoteKeyboardJoystickState & ~mask);
            }
            else
            {
                // Releasing a key returns that joystick line high.
                _remoteKeyboardJoystickState = (byte)(_remoteKeyboardJoystickState | mask);
            }

            SendRemoteClientJoystickInput(true, 0.0);
        }

        /// <summary>
        /// Combines remote keyboard/gamepad state and sends it to the host when needed.
        /// </summary>
        /// <param name="force">True to send regardless of change detection.</param>
        /// <param name="elapsedSeconds">Elapsed render time used for periodic refresh.</param>
        private void SendRemoteClientJoystickInput(bool force, double elapsedSeconds)
        {
            C64NetClient client = _networkClient;
            if (client == null || !client.IsConnected)
            {
                return;
            }

            _remoteInputRefreshAccumulator += elapsedSeconds;
            bool refresh = _remoteInputRefreshAccumulator >= RemoteInputRefreshSeconds;
            if (refresh)
            {
                _remoteInputRefreshAccumulator = 0.0;
            }

            // Both sources use active-low bits, so AND merges simultaneous keyboard and
            // gamepad presses exactly like multiple low lines on the C64 joystick port.
            byte state = (byte)((_remoteKeyboardJoystickState & _remoteGamepadJoystickState) | 0xE0);
            client.SendJoystickState(state, force || refresh || state != _lastRemoteJoystickState);
            _lastRemoteJoystickState = state;
        }

        /// <summary>
        /// Maps frontend keys to C64 joystick active-low bit masks.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="mask">Output bit mask for up/down/left/right/fire.</param>
        /// <returns>True when the key represents a joystick control.</returns>
        private static bool TryGetJoystickMask(Key key, out byte mask)
        {
            if (key == Key.Up)
            {
                mask = 0x01;
                return true;
            }

            if (key == Key.Down)
            {
                mask = 0x02;
                return true;
            }

            if (key == Key.Left)
            {
                mask = 0x04;
                return true;
            }

            if (key == Key.Right)
            {
                mask = 0x08;
                return true;
            }

            if (key == Key.ControlLeft || key == Key.LControl)
            {
                mask = 0x10;
                return true;
            }

            mask = 0;
            return false;
        }

        private static bool IsArrowKey(Key key)
        {
            return key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right;
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

            int zoomCropX;
            int zoomCropY;
            int zoomCropWidth;
            int zoomCropHeight;
            if (_videoZoomEnabled && TryGetVideoZoomCrop(width, height, out zoomCropX, out zoomCropY, out zoomCropWidth, out zoomCropHeight))
            {
                if (DrawGpuPresentedFrame(pixels, width, height, zoomCropX, zoomCropY, zoomCropWidth, zoomCropHeight, borderRed, borderGreen, borderBlue))
                {
                    return;
                }

                DrawZoomedFrame(pixels, width, height, zoomCropX, zoomCropY, zoomCropWidth, zoomCropHeight, borderRed, borderGreen, borderBlue);
                return;
            }

            int integerScaleX = PixelsWidth / width;
            int integerScaleY = PixelsHeight / height;
            int integerScale = Math.Min(integerScaleX, integerScaleY);
            if (integerScale >= 1)
            {
                int scaledWidth = width * integerScale;
                int scaledHeight = height * integerScale;
                int offsetX = (PixelsWidth - scaledWidth) / 2;
                int offsetY = (PixelsHeight - scaledHeight) / 2;
                if (DrawGpuPresentedFrame(pixels, width, height, 0, 0, width, height, offsetX, offsetY, scaledWidth, scaledHeight, borderRed, borderGreen, borderBlue))
                {
                    return;
                }

                DrawFrameMargins(offsetX, offsetY, scaledWidth, scaledHeight, borderRed, borderGreen, borderBlue);
                if (_videoFilterMode == VideoFilterMode.Tv)
                {
                    DrawArgbPixelsScaledTv(pixels, width, height, offsetX, offsetY, integerScale);
                }
                else if (_videoFilterMode == VideoFilterMode.Crt)
                {
                    DrawArgbPixelsScaledCrt(pixels, width, height, offsetX, offsetY, integerScale);
                }
                else
                {
                    DrawArgbPixelsScaled(pixels, width, height, offsetX, offsetY, integerScale);
                }

                return;
            }

            if (DrawGpuPresentedFrame(pixels, width, height, 0, 0, width, height, 0, 0, PixelsWidth, PixelsHeight, borderRed, borderGreen, borderBlue))
            {
                return;
            }

            if (_videoFilterMode == VideoFilterMode.Tv)
            {
                DrawArgbPixelsStretchedTv(pixels, width, height);
            }
            else if (_videoFilterMode == VideoFilterMode.Crt)
            {
                DrawArgbPixelsStretchedCrt(pixels, width, height);
            }
            else
            {
                DrawArgbPixelsStretched(pixels, width, height);
            }
        }

        /// <summary>
        /// Presents a cropped C64 frame through SharpPixels' GPU source renderer using the standard frame destination.
        /// </summary>
        private bool DrawGpuPresentedFrame(uint[] pixels, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight, byte borderRed, byte borderGreen, byte borderBlue)
        {
            int integerScaleX = PixelsWidth / width;
            int integerScaleY = PixelsHeight / height;
            int integerScale = Math.Min(integerScaleX, integerScaleY);
            if (integerScale >= 1)
            {
                int scaledWidth = width * integerScale;
                int scaledHeight = height * integerScale;
                int offsetX = (PixelsWidth - scaledWidth) / 2;
                int offsetY = (PixelsHeight - scaledHeight) / 2;
                return DrawGpuPresentedFrame(pixels, width, height, cropX, cropY, cropWidth, cropHeight, offsetX, offsetY, scaledWidth, scaledHeight, borderRed, borderGreen, borderBlue);
            }

            return DrawGpuPresentedFrame(pixels, width, height, cropX, cropY, cropWidth, cropHeight, 0, 0, PixelsWidth, PixelsHeight, borderRed, borderGreen, borderBlue);
        }

        /// <summary>
        /// Presents a cropped C64 frame through SharpPixels' GPU source renderer.
        /// </summary>
        private bool DrawGpuPresentedFrame(uint[] pixels, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight, int targetX, int targetY, int targetWidth, int targetHeight, byte borderRed, byte borderGreen, byte borderBlue)
        {
            return DrawArgbPixelsGpuPresented(
                pixels,
                width,
                height,
                cropX,
                cropY,
                cropWidth,
                cropHeight,
                targetX,
                targetY,
                targetWidth,
                targetHeight,
                GetGpuFilterMode(_videoFilterMode),
                GetGpuUpscaleMode(_videoUpscaleMode),
                GetGpuUpscaleFactor(_videoUpscaleMode),
                borderRed,
                borderGreen,
                borderBlue);
        }

        /// <summary>
        /// Maps the emulator's video filter setting to the SharpPixels presentation shader mode.
        /// </summary>
        private static SharpPixelsGpuFilterMode GetGpuFilterMode(VideoFilterMode videoFilterMode)
        {
            if (videoFilterMode == VideoFilterMode.Crt)
            {
                return SharpPixelsGpuFilterMode.Crt;
            }

            if (videoFilterMode == VideoFilterMode.Tv)
            {
                return SharpPixelsGpuFilterMode.Tv;
            }

            return SharpPixelsGpuFilterMode.Sharp;
        }

        /// <summary>
        /// Maps the emulator's video upscale setting to the SharpPixels presentation shader mode.
        /// </summary>
        private static SharpPixelsGpuUpscaleMode GetGpuUpscaleMode(VideoUpscaleMode videoUpscaleMode)
        {
            switch (videoUpscaleMode)
            {
                case VideoUpscaleMode.Scale2x:
                    return SharpPixelsGpuUpscaleMode.Scale2x;
                case VideoUpscaleMode.Scale3x:
                    return SharpPixelsGpuUpscaleMode.Scale3x;
                case VideoUpscaleMode.Hq2x:
                    return SharpPixelsGpuUpscaleMode.Hq2x;
                case VideoUpscaleMode.Hq3x:
                    return SharpPixelsGpuUpscaleMode.Hq3x;
                case VideoUpscaleMode.Hq4x:
                    return SharpPixelsGpuUpscaleMode.Hq4x;
                default:
                    return SharpPixelsGpuUpscaleMode.None;
            }
        }

        /// <summary>
        /// Gets the integer scale factor represented by an upscale setting.
        /// </summary>
        private static int GetGpuUpscaleFactor(VideoUpscaleMode videoUpscaleMode)
        {
            switch (videoUpscaleMode)
            {
                case VideoUpscaleMode.Scale2x:
                case VideoUpscaleMode.Hq2x:
                    return 2;
                case VideoUpscaleMode.Scale3x:
                case VideoUpscaleMode.Hq3x:
                    return 3;
                case VideoUpscaleMode.Hq4x:
                    return 4;
                default:
                    return 1;
            }
        }

        /// <summary>
        /// Draws the C64 screen area without the surrounding border.
        /// </summary>
        private void DrawZoomedFrame(uint[] pixels, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight, byte borderRed, byte borderGreen, byte borderBlue)
        {
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
                DrawCroppedFrameToRectangle(pixels, width, height, cropX, cropY, cropWidth, cropHeight, offsetX, offsetY, scaledWidth, scaledHeight);
                return;
            }

            DrawCroppedFrameToRectangle(pixels, width, height, cropX, cropY, cropWidth, cropHeight, 0, 0, PixelsWidth, PixelsHeight);
        }

        /// <summary>
        /// Draws a cropped source area through the active local presentation filter.
        /// </summary>
        private void DrawCroppedFrameToRectangle(uint[] pixels, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight, int targetX, int targetY, int targetWidth, int targetHeight)
        {
            if (_videoFilterMode == VideoFilterMode.Tv)
            {
                DrawArgbPixelsCroppedToRectangleTv(pixels, width, height, cropX, cropY, cropWidth, cropHeight, targetX, targetY, targetWidth, targetHeight);
                return;
            }

            if (_videoFilterMode == VideoFilterMode.Crt)
            {
                DrawArgbPixelsCroppedToRectangleCrt(pixels, width, height, cropX, cropY, cropWidth, cropHeight, targetX, targetY, targetWidth, targetHeight);
                return;
            }

            DrawArgbPixelsCroppedStretched(pixels, width, height, cropX, cropY, cropWidth, cropHeight, targetX, targetY, targetWidth, targetHeight);
        }

        /// <summary>
        /// Returns the standard 40x25 C64 content crop when it fits inside the frame.
        /// </summary>
        private static bool TryGetVideoZoomCrop(int width, int height, out int cropX, out int cropY, out int cropWidth, out int cropHeight)
        {
            cropWidth = Math.Min(StandardC64ContentWidth, width);
            cropHeight = Math.Min(StandardC64ContentHeight, height);
            cropX = ClampInt(width == 403 ? StandardC64ContentLeft : (width - cropWidth) / 2, 0, Math.Max(0, width - cropWidth));
            cropY = ClampInt(height == 284 ? StandardC64ContentTop : (height - cropHeight) / 2, 0, Math.Max(0, height - cropHeight));
            return cropWidth > 0 && cropHeight > 0;
        }

        /// <summary>
        /// Draws a cropped ARGB source rectangle with the CRT filter.
        /// </summary>
        private void DrawArgbPixelsCroppedToRectangleCrt(uint[] pixels, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight, int targetX, int targetY, int targetWidth, int targetHeight)
        {
            if (pixels == null || pixels.Length < width * height || targetWidth <= 0 || targetHeight <= 0)
            {
                return;
            }

            for (int targetYOffset = 0; targetYOffset < targetHeight; targetYOffset++)
            {
                int scaledSourceY = (int)(((long)targetYOffset * cropHeight * 256) / targetHeight);
                int sourceY = cropY + (scaledSourceY >> 8);
                int yFraction = scaledSourceY & 0xFF;
                int outputY = targetY + targetYOffset;
                bool scanline = (targetYOffset & 1) != 0;
                for (int targetXOffset = 0; targetXOffset < targetWidth; targetXOffset++)
                {
                    int scaledSourceX = (int)(((long)targetXOffset * cropWidth * 256) / targetWidth);
                    int sourceX = cropX + (scaledSourceX >> 8);
                    int xFraction = scaledSourceX & 0xFF;
                    DrawCrtPixel(targetX + targetXOffset, outputY, SampleCrtSourcePixel(pixels, sourceY, sourceX, xFraction, yFraction, width, height, cropX, cropY, cropWidth, cropHeight), scanline);
                }
            }
        }

        /// <summary>
        /// Draws a cropped ARGB source rectangle with the TV filter.
        /// </summary>
        private void DrawArgbPixelsCroppedToRectangleTv(uint[] pixels, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight, int targetX, int targetY, int targetWidth, int targetHeight)
        {
            if (pixels == null || pixels.Length < width * height || targetWidth <= 0 || targetHeight <= 0)
            {
                return;
            }

            for (int targetYOffset = 0; targetYOffset < targetHeight; targetYOffset++)
            {
                int scaledSourceY = (int)(((long)targetYOffset * cropHeight * 256) / targetHeight);
                int sourceY = cropY + (scaledSourceY >> 8);
                int yFraction = scaledSourceY & 0xFF;
                int outputY = targetY + targetYOffset;
                bool scanline = (targetYOffset & 1) != 0;
                for (int targetXOffset = 0; targetXOffset < targetWidth; targetXOffset++)
                {
                    int scaledSourceX = (int)(((long)targetXOffset * cropWidth * 256) / targetWidth);
                    int sourceX = cropX + (scaledSourceX >> 8);
                    int xFraction = scaledSourceX & 0xFF;
                    DrawTvPixel(targetX + targetXOffset, outputY, SampleTvSourcePixel(pixels, sourceY, sourceX, xFraction, yFraction, width, height, cropX, cropY, cropWidth, cropHeight), scanline);
                }
            }
        }

        /// <summary>
        /// Draws ARGB pixels with a lightweight CRT-style phosphor blur and scanline pass.
        /// </summary>
        private void DrawArgbPixelsScaledCrt(uint[] pixels, int width, int height, int offsetX, int offsetY, int scale)
        {
            if (pixels == null || pixels.Length < width * height || scale <= 0)
            {
                return;
            }

            int scaledWidth = width * scale;
            int scaledHeight = height * scale;
            for (int targetYOffset = 0; targetYOffset < scaledHeight; targetYOffset++)
            {
                int sourceY = targetYOffset / scale;
                int yFraction = ((targetYOffset % scale) * 256) / scale;
                int targetY = offsetY + targetYOffset;
                bool scanline = (targetYOffset & 1) != 0;
                for (int targetXOffset = 0; targetXOffset < scaledWidth; targetXOffset++)
                {
                    int sourceX = targetXOffset / scale;
                    int xFraction = ((targetXOffset % scale) * 256) / scale;
                    DrawCrtPixel(offsetX + targetXOffset, targetY, SampleCrtSourcePixel(pixels, sourceY, sourceX, xFraction, yFraction, width, height), scanline);
                }
            }
        }

        /// <summary>
        /// Draws stretched ARGB pixels with the CRT-style filter.
        /// </summary>
        private void DrawArgbPixelsStretchedCrt(uint[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length < width * height)
            {
                return;
            }

            for (int y = 0; y < PixelsHeight; y++)
            {
                int scaledSourceY = (int)(((long)y * height * 256) / PixelsHeight);
                int sourceY = scaledSourceY >> 8;
                int yFraction = scaledSourceY & 0xFF;
                bool scanline = (y & 1) != 0;
                for (int x = 0; x < PixelsWidth; x++)
                {
                    int scaledSourceX = (int)(((long)x * width * 256) / PixelsWidth);
                    int sourceX = scaledSourceX >> 8;
                    int xFraction = scaledSourceX & 0xFF;
                    DrawCrtPixel(x, y, SampleCrtSourcePixel(pixels, sourceY, sourceX, xFraction, yFraction, width, height), scanline);
                }
            }
        }

        /// <summary>
        /// Draws ARGB pixels with a soft television grille and heavier raster blur.
        /// </summary>
        private void DrawArgbPixelsScaledTv(uint[] pixels, int width, int height, int offsetX, int offsetY, int scale)
        {
            if (pixels == null || pixels.Length < width * height || scale <= 0)
            {
                return;
            }

            int scaledWidth = width * scale;
            int scaledHeight = height * scale;
            for (int targetYOffset = 0; targetYOffset < scaledHeight; targetYOffset++)
            {
                int sourceY = targetYOffset / scale;
                int yFraction = ((targetYOffset % scale) * 256) / scale;
                int targetY = offsetY + targetYOffset;
                bool scanline = (targetYOffset & 1) != 0;
                for (int targetXOffset = 0; targetXOffset < scaledWidth; targetXOffset++)
                {
                    int sourceX = targetXOffset / scale;
                    int xFraction = ((targetXOffset % scale) * 256) / scale;
                    DrawTvPixel(offsetX + targetXOffset, targetY, SampleTvSourcePixel(pixels, sourceY, sourceX, xFraction, yFraction, width, height), scanline);
                }
            }
        }

        /// <summary>
        /// Draws stretched ARGB pixels with the television grille filter.
        /// </summary>
        private void DrawArgbPixelsStretchedTv(uint[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length < width * height)
            {
                return;
            }

            for (int y = 0; y < PixelsHeight; y++)
            {
                int scaledSourceY = (int)(((long)y * height * 256) / PixelsHeight);
                int sourceY = scaledSourceY >> 8;
                int yFraction = scaledSourceY & 0xFF;
                bool scanline = (y & 1) != 0;
                for (int x = 0; x < PixelsWidth; x++)
                {
                    int scaledSourceX = (int)(((long)x * width * 256) / PixelsWidth);
                    int sourceX = scaledSourceX >> 8;
                    int xFraction = scaledSourceX & 0xFF;
                    DrawTvPixel(x, y, SampleTvSourcePixel(pixels, sourceY, sourceX, xFraction, yFraction, width, height), scanline);
                }
            }
        }

        /// <summary>
        /// Blends a source pixel with its horizontal neighbours for a soft composite-video feel.
        /// </summary>
        private static uint BlendCrtSourcePixel(uint[] pixels, int sourceRow, int sourceX, int width)
        {
            uint center = pixels[sourceRow + sourceX];
            uint left = pixels[sourceRow + Math.Max(0, sourceX - 1)];
            uint right = pixels[sourceRow + Math.Min(width - 1, sourceX + 1)];
            int red = ((((int)(center >> 16) & 0xFF) * 6) + ((int)(left >> 16) & 0xFF) + ((int)(right >> 16) & 0xFF)) >> 3;
            int green = ((((int)(center >> 8) & 0xFF) * 6) + ((int)(left >> 8) & 0xFF) + ((int)(right >> 8) & 0xFF)) >> 3;
            int blue = (((int)(center & 0xFF) * 6) + (int)(left & 0xFF) + (int)(right & 0xFF)) >> 3;
            return (uint)((red << 16) | (green << 8) | blue);
        }

        /// <summary>
        /// Samples the CRT-filtered source using bilinear interpolation.
        /// </summary>
        private static uint SampleCrtSourcePixel(uint[] pixels, int sourceY, int sourceX, int xFraction, int yFraction, int width, int height)
        {
            int clampedY = ClampInt(sourceY, 0, height - 1);
            int nextY = ClampInt(sourceY + 1, 0, height - 1);
            int clampedX = ClampInt(sourceX, 0, width - 1);
            int nextX = ClampInt(sourceX + 1, 0, width - 1);

            uint topLeft = BlendCrtSourcePixel(pixels, clampedY * width, clampedX, width);
            uint topRight = BlendCrtSourcePixel(pixels, clampedY * width, nextX, width);
            uint bottomLeft = BlendCrtSourcePixel(pixels, nextY * width, clampedX, width);
            uint bottomRight = BlendCrtSourcePixel(pixels, nextY * width, nextX, width);
            uint top = LerpRgb(topLeft, topRight, xFraction);
            uint bottom = LerpRgb(bottomLeft, bottomRight, xFraction);
            uint interpolated = LerpRgb(top, bottom, yFraction);
            return LerpRgb(topLeft, interpolated, 48);
        }

        /// <summary>
        /// Samples the CRT-filtered source while clamping all taps to a crop rectangle.
        /// </summary>
        private static uint SampleCrtSourcePixel(uint[] pixels, int sourceY, int sourceX, int xFraction, int yFraction, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight)
        {
            int minX = ClampInt(cropX, 0, width - 1);
            int minY = ClampInt(cropY, 0, height - 1);
            int maxX = ClampInt(cropX + cropWidth - 1, minX, width - 1);
            int maxY = ClampInt(cropY + cropHeight - 1, minY, height - 1);
            int clampedY = ClampInt(sourceY, minY, maxY);
            int nextY = ClampInt(sourceY + 1, minY, maxY);
            int clampedX = ClampInt(sourceX, minX, maxX);
            int nextX = ClampInt(sourceX + 1, minX, maxX);

            uint topLeft = BlendCrtSourcePixel(pixels, clampedY * width, clampedX, width, minX, maxX);
            uint topRight = BlendCrtSourcePixel(pixels, clampedY * width, nextX, width, minX, maxX);
            uint bottomLeft = BlendCrtSourcePixel(pixels, nextY * width, clampedX, width, minX, maxX);
            uint bottomRight = BlendCrtSourcePixel(pixels, nextY * width, nextX, width, minX, maxX);
            uint top = LerpRgb(topLeft, topRight, xFraction);
            uint bottom = LerpRgb(bottomLeft, bottomRight, xFraction);
            uint interpolated = LerpRgb(top, bottom, yFraction);
            return LerpRgb(topLeft, interpolated, 48);
        }

        /// <summary>
        /// Blends a source pixel with horizontal neighbours clamped to a crop rectangle.
        /// </summary>
        private static uint BlendCrtSourcePixel(uint[] pixels, int sourceRow, int sourceX, int width, int minX, int maxX)
        {
            uint center = pixels[sourceRow + sourceX];
            uint left = pixels[sourceRow + ClampInt(sourceX - 1, minX, maxX)];
            uint right = pixels[sourceRow + ClampInt(sourceX + 1, minX, maxX)];
            int red = ((((int)(center >> 16) & 0xFF) * 6) + ((int)(left >> 16) & 0xFF) + ((int)(right >> 16) & 0xFF)) >> 3;
            int green = ((((int)(center >> 8) & 0xFF) * 6) + ((int)(left >> 8) & 0xFF) + ((int)(right >> 8) & 0xFF)) >> 3;
            int blue = (((int)(center & 0xFF) * 6) + (int)(left & 0xFF) + (int)(right & 0xFF)) >> 3;
            return (uint)((red << 16) | (green << 8) | blue);
        }

        /// <summary>
        /// Blends a source pixel with horizontal and vertical neighbours for a softer TV tube look.
        /// </summary>
        private static uint BlendTvSourcePixel(uint[] pixels, int sourceY, int sourceX, int width, int height)
        {
            int sourceRow = sourceY * width;
            int leftX = Math.Max(0, sourceX - 1);
            int rightX = Math.Min(width - 1, sourceX + 1);
            int upperRow = Math.Max(0, sourceY - 1) * width;
            int lowerRow = Math.Min(height - 1, sourceY + 1) * width;

            uint center = pixels[sourceRow + sourceX];
            uint left = pixels[sourceRow + leftX];
            uint right = pixels[sourceRow + rightX];
            uint upper = pixels[upperRow + sourceX];
            uint lower = pixels[lowerRow + sourceX];

            const int totalWeight = 20;
            int red = ((((int)(center >> 16) & 0xFF) * 10) +
                       (((int)(left >> 16) & 0xFF) * 3) +
                       (((int)(right >> 16) & 0xFF) * 3) +
                       (((int)(upper >> 16) & 0xFF) * 2) +
                       (((int)(lower >> 16) & 0xFF) * 2)) / totalWeight;
            int green = ((((int)(center >> 8) & 0xFF) * 10) +
                         (((int)(left >> 8) & 0xFF) * 3) +
                         (((int)(right >> 8) & 0xFF) * 3) +
                         (((int)(upper >> 8) & 0xFF) * 2) +
                         (((int)(lower >> 8) & 0xFF) * 2)) / totalWeight;
            int blue = (((int)(center & 0xFF) * 10) +
                        ((int)(left & 0xFF) * 3) +
                        ((int)(right & 0xFF) * 3) +
                        ((int)(upper & 0xFF) * 2) +
                        ((int)(lower & 0xFF) * 2)) / totalWeight;

            return (uint)((red << 16) | (green << 8) | blue);
        }

        /// <summary>
        /// Samples the TV-filtered source using bilinear interpolation.
        /// </summary>
        private static uint SampleTvSourcePixel(uint[] pixels, int sourceY, int sourceX, int xFraction, int yFraction, int width, int height)
        {
            int clampedY = ClampInt(sourceY, 0, height - 1);
            int nextY = ClampInt(sourceY + 1, 0, height - 1);
            int clampedX = ClampInt(sourceX, 0, width - 1);
            int nextX = ClampInt(sourceX + 1, 0, width - 1);

            uint topLeft = BlendTvSourcePixel(pixels, clampedY, clampedX, width, height);
            uint topRight = BlendTvSourcePixel(pixels, clampedY, nextX, width, height);
            uint bottomLeft = BlendTvSourcePixel(pixels, nextY, clampedX, width, height);
            uint bottomRight = BlendTvSourcePixel(pixels, nextY, nextX, width, height);
            uint top = LerpRgb(topLeft, topRight, xFraction);
            uint bottom = LerpRgb(bottomLeft, bottomRight, xFraction);
            uint interpolated = LerpRgb(top, bottom, yFraction);
            return LerpRgb(topLeft, interpolated, 64);
        }

        /// <summary>
        /// Samples the TV-filtered source while clamping all taps to a crop rectangle.
        /// </summary>
        private static uint SampleTvSourcePixel(uint[] pixels, int sourceY, int sourceX, int xFraction, int yFraction, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight)
        {
            int minX = ClampInt(cropX, 0, width - 1);
            int minY = ClampInt(cropY, 0, height - 1);
            int maxX = ClampInt(cropX + cropWidth - 1, minX, width - 1);
            int maxY = ClampInt(cropY + cropHeight - 1, minY, height - 1);
            int clampedY = ClampInt(sourceY, minY, maxY);
            int nextY = ClampInt(sourceY + 1, minY, maxY);
            int clampedX = ClampInt(sourceX, minX, maxX);
            int nextX = ClampInt(sourceX + 1, minX, maxX);

            uint topLeft = BlendTvSourcePixel(pixels, clampedY, clampedX, width, height, minX, minY, maxX, maxY);
            uint topRight = BlendTvSourcePixel(pixels, clampedY, nextX, width, height, minX, minY, maxX, maxY);
            uint bottomLeft = BlendTvSourcePixel(pixels, nextY, clampedX, width, height, minX, minY, maxX, maxY);
            uint bottomRight = BlendTvSourcePixel(pixels, nextY, nextX, width, height, minX, minY, maxX, maxY);
            uint top = LerpRgb(topLeft, topRight, xFraction);
            uint bottom = LerpRgb(bottomLeft, bottomRight, xFraction);
            uint interpolated = LerpRgb(top, bottom, yFraction);
            return LerpRgb(topLeft, interpolated, 64);
        }

        /// <summary>
        /// Blends a TV source pixel with neighbours clamped to a crop rectangle.
        /// </summary>
        private static uint BlendTvSourcePixel(uint[] pixels, int sourceY, int sourceX, int width, int height, int minX, int minY, int maxX, int maxY)
        {
            int sourceRow = sourceY * width;
            int leftX = ClampInt(sourceX - 1, minX, maxX);
            int rightX = ClampInt(sourceX + 1, minX, maxX);
            int upperRow = ClampInt(sourceY - 1, minY, maxY) * width;
            int lowerRow = ClampInt(sourceY + 1, minY, maxY) * width;

            uint center = pixels[sourceRow + sourceX];
            uint left = pixels[sourceRow + leftX];
            uint right = pixels[sourceRow + rightX];
            uint upper = pixels[upperRow + sourceX];
            uint lower = pixels[lowerRow + sourceX];

            const int totalWeight = 20;
            int red = ((((int)(center >> 16) & 0xFF) * 10) +
                       (((int)(left >> 16) & 0xFF) * 3) +
                       (((int)(right >> 16) & 0xFF) * 3) +
                       (((int)(upper >> 16) & 0xFF) * 2) +
                       (((int)(lower >> 16) & 0xFF) * 2)) / totalWeight;
            int green = ((((int)(center >> 8) & 0xFF) * 10) +
                         (((int)(left >> 8) & 0xFF) * 3) +
                         (((int)(right >> 8) & 0xFF) * 3) +
                         (((int)(upper >> 8) & 0xFF) * 2) +
                         (((int)(lower >> 8) & 0xFF) * 2)) / totalWeight;
            int blue = (((int)(center & 0xFF) * 10) +
                        ((int)(left & 0xFF) * 3) +
                        ((int)(right & 0xFF) * 3) +
                        ((int)(upper & 0xFF) * 2) +
                        ((int)(lower & 0xFF) * 2)) / totalWeight;

            return (uint)((red << 16) | (green << 8) | blue);
        }

        /// <summary>
        /// Linearly interpolates two RGB colors with an 8-bit fraction.
        /// </summary>
        private static uint LerpRgb(uint left, uint right, int fraction)
        {
            int inverse = 256 - fraction;
            int red = ((((int)(left >> 16) & 0xFF) * inverse) + (((int)(right >> 16) & 0xFF) * fraction)) >> 8;
            int green = ((((int)(left >> 8) & 0xFF) * inverse) + (((int)(right >> 8) & 0xFF) * fraction)) >> 8;
            int blue = (((int)(left & 0xFF) * inverse) + ((int)(right & 0xFF) * fraction)) >> 8;
            return (uint)((red << 16) | (green << 8) | blue);
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        /// <summary>
        /// Draws one filtered output pixel.
        /// </summary>
        private void DrawCrtPixel(int x, int y, uint rgb, bool scanline)
        {
            int multiplier = scanline ? 94 : 102;
            byte red = ClampToByte((((int)(rgb >> 16) & 0xFF) * multiplier) / 100);
            byte green = ClampToByte((((int)(rgb >> 8) & 0xFF) * multiplier) / 100);
            byte blue = ClampToByte(((int)(rgb & 0xFF) * multiplier) / 100);
            DrawPixel(x, y, red, green, blue);
        }

        /// <summary>
        /// Draws one output pixel with a coarse RGB mask and softened scanline contrast.
        /// </summary>
        private void DrawTvPixel(int x, int y, uint rgb, bool scanline)
        {
            int baseMultiplier = scanline ? 93 : 101;
            int red = (((int)(rgb >> 16) & 0xFF) * baseMultiplier) / 100;
            int green = (((int)(rgb >> 8) & 0xFF) * baseMultiplier) / 100;
            int blue = ((int)(rgb & 0xFF) * baseMultiplier) / 100;

            switch (x % 3)
            {
                case 0:
                    red = (red * 103) / 100;
                    green = (green * 98) / 100;
                    blue = (blue * 99) / 100;
                    break;
                case 1:
                    red = (red * 99) / 100;
                    green = (green * 102) / 100;
                    blue = (blue * 99) / 100;
                    break;
                default:
                    red = (red * 98) / 100;
                    green = (green * 99) / 100;
                    blue = (blue * 103) / 100;
                    break;
            }

            if ((y & 3) == 3)
            {
                red = (red * 99) / 100;
                green = (green * 99) / 100;
                blue = (blue * 99) / 100;
            }

            DrawPixel(x, y, ClampToByte(red), ClampToByte(green), ClampToByte(blue));
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
        /// Hides the drive footer immediately.
        /// </summary>
        private void HideDriveFooter()
        {
            _driveFooterIdleSeconds = 0.0;
            _driveFooterVisibility = 0.0;
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

            int maxToastCharacters = Math.Max(1, (PixelsWidth - 52) / 12);
            string toastText = FormatOverlayValue(_turboToastText, maxToastCharacters);
            int width = Math.Min(PixelsWidth - 20, Math.Max(120, GetOverlayTextWidth(toastText, 2) + 32));
            int height = 34;
            int x = (PixelsWidth - width) / 2;
            int y = 18;
            DrawFilledRectangleWithAlpha(x, y, width, height, 8, 10, 18, panelAlpha);
            DrawLine(x, y, x + width - 1, y, frameRed, frameGreen, frameBlue);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, frameRed, frameGreen, frameBlue);
            DrawLine(x, y, x, y + height - 1, frameRed, frameGreen, frameBlue);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, frameRed, frameGreen, frameBlue);
            DrawOverlayText(x + 16, y + 10, toastText, 2, textRed, textGreen, textBlue);
        }

        /// <summary>
        /// Draws a compact live developer overlay.
        /// </summary>
        private void DrawDebugOverlay(
            VicTiming timing,
            ushort currentPc,
            byte currentOpcode,
            string memoryDebugInfo,
            string cpuDebugInfo,
            string ciaDebugInfo,
            string sidDebugInfo,
            string iecDebugInfo,
            string drive8DebugInfo)
        {
            int overlayX = 6;
            int overlayY = 6;
            int overlayWidth = Math.Min(PixelsWidth - 12, 390);
            int overlayHeight = 102;
            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 4, 8, 12, 210);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 115, 142, 196);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 115, 142, 196);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 115, 142, 196);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 115, 142, 196);

            DrawOverlayText(overlayX + 8, overlayY + 7, "DEBUG", 1, 255, 243, 168);
            DrawOverlayText(overlayX + 8, overlayY + 19, string.Format("CYC {0} RASTER {1}:{2} BA {3}/{4}",
                timing.GlobalCycle,
                timing.RasterLine,
                timing.CycleInLine,
                timing.BusRequestPending ? "P" : "-",
                timing.CpuBlocked ? "BLK" : "RUN"), 1, 232, 238, 244);
            DrawOverlayText(overlayX + 8, overlayY + 31, string.Format("PC {0:X4} OP {1:X2} BAD {2} PHI {3}/{4}",
                currentPc,
                currentOpcode,
                timing.BadLine ? "Y" : "N",
                timing.Phi1Action,
                timing.Phi2Action), 1, 232, 238, 244);
            DrawOverlayText(overlayX + 8, overlayY + 43, "MEM " + FormatOverlayValue(memoryDebugInfo, 56), 1, 182, 214, 108);
            DrawOverlayText(overlayX + 8, overlayY + 55, "CPU " + FormatOverlayValue(cpuDebugInfo, 56), 1, 182, 214, 108);
            DrawOverlayText(overlayX + 8, overlayY + 67, "CIA " + FormatOverlayValue(ciaDebugInfo, 56), 1, 192, 210, 225);
            DrawOverlayText(overlayX + 8, overlayY + 79, "SID " + FormatOverlayValue(sidDebugInfo, 56), 1, 192, 210, 225);
            DrawOverlayText(overlayX + 8, overlayY + 91, "IEC " + FormatOverlayValue(iecDebugInfo + " " + drive8DebugInfo, 56), 1, 192, 210, 225);
        }

        /// <summary>
        /// Draws the savestate overlay.
        /// </summary>
        private void DrawSaveOverlay()
        {
            if (_networkMode == NetworkSessionMode.Client)
            {
                DrawRemoteSaveOverlay();
                return;
            }

            int overlayWidth = Math.Min(PixelsWidth - 12, 376);
            int overlayHeight = Math.Min(PixelsHeight - 12, 292);
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = (PixelsHeight - overlayHeight) / 2;

            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 8, 10, 18, 232);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 182, 214, 108);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 182, 214, 108);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 182, 214, 108);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 182, 214, 108);

            DrawOverlayText(overlayX + 14, overlayY + 12, "SAVE STATES", 2, 240, 248, 255);
            DrawOverlayText(overlayX + 14, overlayY + 31, "ENTER OPEN/LOAD  BACK UP  S/F5 SAVE  DEL DELETE", 1, 192, 210, 225);
            DrawOverlayText(overlayX + 14, overlayY + 43, "STATUS " + FormatOverlayValue(_saveOverlayStatusText, 45), 1, 182, 214, 108);

            int listX = overlayX + 14;
            int listY = overlayY + 62;
            int listWidth = Math.Max(120, overlayWidth - 156);
            int listHeight = (SaveOverlayVisibleRows * 13) + 8;
            DrawFilledRectangleWithAlpha(listX - 4, listY - 4, listWidth + 8, listHeight, 20, 24, 38, 210);

            if (_saveOverlayEntries.Count == 0)
            {
                DrawOverlayText(listX, listY + 12, "NO SAVES HERE", 2, 255, 243, 168);
                DrawOverlayText(listX, listY + 34, "PRESS S TO CREATE ONE", 1, 232, 238, 244);
            }
            else
            {
                for (int row = 0; row < SaveOverlayVisibleRows; row++)
                {
                    int index = _saveOverlayScroll + row;
                    if (index >= _saveOverlayEntries.Count)
                    {
                        break;
                    }

                    SaveOverlayEntry entry = _saveOverlayEntries[index];
                    bool selected = index == _saveOverlaySelection;
                    byte red = selected ? (byte)255 : (byte)232;
                    byte green = selected ? (byte)243 : (byte)238;
                    byte blue = selected ? (byte)168 : (byte)244;
                    string listText = FormatOverlayValue(entry.DisplayName, Math.Max(10, (listWidth / 6) - 3));
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

            SaveOverlayEntry selectedOverlayEntry = _saveOverlayEntries.Count > 0 ? _saveOverlayEntries[_saveOverlaySelection] : null;
            if (selectedOverlayEntry != null && !selectedOverlayEntry.IsDirectory && selectedOverlayEntry.Metadata != null)
            {
                SaveStateMetadata selectedEntry = selectedOverlayEntry.Metadata;
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
                DrawOverlayText(thumbnailX, thumbnailY + thumbnailHeight + 31, FormatOverlayValue(GetSaveStateDisplayMediaName(selectedEntry), 18), 1, 182, 214, 108);
            }
            else
            {
                DrawFilledRectangle(thumbnailX, thumbnailY, thumbnailWidth, thumbnailHeight, 24, 24, 32);
                DrawOverlayText(thumbnailX + 20, thumbnailY + 34, selectedOverlayEntry != null && selectedOverlayEntry.IsDirectory ? "FOLDER" : "NO IMAGE", 1, 115, 142, 196);
            }

            if (_saveDeleteConfirmVisible)
            {
                DrawSaveDeleteConfirmOverlay(overlayX + 62, overlayY + 94, 252, 82);
            }
        }

        /// <summary>
        /// Draws the savestate delete confirmation popup.
        /// </summary>
        /// <param name="x">Popup x coordinate.</param>
        /// <param name="y">Popup y coordinate.</param>
        /// <param name="width">Popup width.</param>
        /// <param name="height">Popup height.</param>
        private void DrawSaveDeleteConfirmOverlay(int x, int y, int width, int height)
        {
            DrawFilledRectangleWithAlpha(x, y, width, height, 20, 24, 38, 242);
            DrawLine(x, y, x + width - 1, y, 255, 243, 168);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 255, 243, 168);
            DrawLine(x, y, x, y + height - 1, 255, 243, 168);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, 255, 243, 168);

            DrawOverlayText(x + 14, y + 10, "DELETE SAVE?", 2, 255, 243, 168);
            DrawOverlayText(x + 14, y + 34, FormatOverlayValue(Path.GetFileName(_pendingDeleteSavePath), 30), 1, 232, 238, 244);
            DrawOverlayText(x + 14, y + 50, _saveDeleteConfirmYesSelected ? "> YES     NO" : "  YES   > NO", 2, 240, 248, 255);
            DrawOverlayText(x + 14, y + 68, "ENTER ACCEPT  ESC CANCEL", 1, 192, 210, 225);
        }

        private void DrawRemoteSaveOverlay()
        {
            int overlayWidth = Math.Min(PixelsWidth - 18, 330);
            int overlayHeight = 112;
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = (PixelsHeight - overlayHeight) / 2;

            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 8, 10, 18, 232);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 105, 112, 122);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 105, 112, 122);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 105, 112, 122);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 105, 112, 122);

            DrawOverlayText(overlayX + 14, overlayY + 12, "SAVE STATES", 2, 130, 136, 146);
            DrawOverlayText(overlayX + 14, overlayY + 36, "UNAVAILABLE IN REMOTE SESSION", 1, 130, 136, 146);
            DrawOverlayText(overlayX + 14, overlayY + 52, "SERVER OWNS THE C64 STATE", 1, 130, 136, 146);
            DrawOverlayText(overlayX + 14, overlayY + 84, "ESC OR F12 CLOSE", 1, 192, 210, 225);
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
            string mediaName = GetSaveStateDisplayMediaName(entry);
            string text = entry.CreatedLocalTime.ToString("MM-dd HH:mm") + " " + mediaName;
            return FormatOverlayValue(text, maxLength);
        }

        /// <summary>
        /// Gets the best available game/media name for a savestate row.
        /// </summary>
        private static string GetSaveStateDisplayMediaName(SaveStateMetadata entry)
        {
            if (entry == null)
            {
                return "UNKNOWN";
            }

            if (!string.IsNullOrWhiteSpace(entry.MediaHostPath))
            {
                return Path.GetFileNameWithoutExtension(entry.MediaHostPath).ToUpperInvariant();
            }

            if (!string.IsNullOrWhiteSpace(entry.MediaDisplayName))
            {
                return entry.MediaDisplayName.ToUpperInvariant();
            }

            try
            {
                string directoryName = Path.GetFileName(Path.GetDirectoryName(entry.Path));
                if (!string.IsNullOrWhiteSpace(directoryName) &&
                    !string.Equals(directoryName, "saves", StringComparison.OrdinalIgnoreCase))
                {
                    return directoryName.ToUpperInvariant();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return Path.GetFileNameWithoutExtension(entry.Path).ToUpperInvariant();
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
        /// Handles key input captured by the network overlay.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="modifiers">Keyboard modifiers used for editable fields.</param>
        /// <returns>True when the overlay consumed the key.</returns>
        private bool HandleNetworkOverlayKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_networkTlsCertificatePromptVisible)
            {
                return HandleNetworkTlsCertificatePromptKeyDown(key);
            }

            switch (key)
            {
                case Key.Up:
                    MoveNetworkOverlaySelection(-1);
                    return true;
                case Key.Down:
                    MoveNetworkOverlaySelection(1);
                    return true;
                case Key.Left:
                case Key.Minus:
                    AdjustNetworkMenu(-1);
                    return true;
                case Key.Right:
                case Key.Plus:
                    AdjustNetworkMenu(1);
                    return true;
                case Key.Enter:
                    ActivateNetworkMenuItem();
                    return true;
                case Key.Escape:
                    ReturnToMainMenuFromSubmenu();
                    return true;
            }

            return TryApplyNetworkTextKey(key, modifiers);
        }

        /// <summary>
        /// Handles the modal changed-certificate warning prompt inside the network menu.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <returns>True because the modal prompt consumes all network-menu input.</returns>
        private bool HandleNetworkTlsCertificatePromptKeyDown(Key key)
        {
            if (key == Key.Enter)
            {
                AcceptNetworkTlsCertificateChange();
                return true;
            }

            if (key == Key.Escape)
            {
                AbortNetworkTlsCertificateChange();
                return true;
            }

            return true;
        }

        /// <summary>
        /// Moves the selected network menu row with wraparound.
        /// </summary>
        /// <param name="delta">Selection movement, usually -1 or +1.</param>
        private void MoveNetworkOverlaySelection(int delta)
        {
            _networkOverlaySelection += delta;
            if (_networkOverlaySelection < 0)
            {
                _networkOverlaySelection = NetworkOverlayItemCount - 1;
            }

            if (_networkOverlaySelection >= NetworkOverlayItemCount)
            {
                _networkOverlaySelection = 0;
            }
        }

        /// <summary>
        /// Adjusts or activates the currently selected network menu row.
        /// </summary>
        /// <param name="direction">Adjustment direction from left/right style input.</param>
        private void AdjustNetworkMenu(int direction)
        {
            if (!IsNetworkMenuRowEnabled(_networkOverlaySelection))
            {
                ShowNetworkStatus("NETWORK ITEM DISABLED");
                return;
            }

            switch (_networkOverlaySelection)
            {
                case 0:
                    _networkTransportMode = _networkTransportMode == C64NetTransportMode.Relay
                        ? C64NetTransportMode.Lan
                        : C64NetTransportMode.Relay;
                    SaveSettings();
                    break;
                case 1:
                    // Port fields support both step adjustment and direct digit editing.
                    _networkServerPort = ClampNetworkPort(_networkServerPort + direction);
                    SaveSettings();
                    break;
                case 4:
                    if (direction != 0)
                    {
                        ToggleNetworkServer();
                    }

                    break;
                case 5:
                    // SELECT CLIENT cycles through connected clients.
                    MoveSelectedNetworkClient(direction);
                    break;
                case 6:
                    CycleSelectedNetworkClientPermission();
                    break;
                case 7:
                    ToggleSelectedNetworkClientKeyboard();
                    break;
                case 8:
                    // KICK CLIENT uses the same selected client cursor.
                    MoveSelectedNetworkClient(direction);
                    break;
                case 11:
                    if (_networkTransportMode == C64NetTransportMode.Relay)
                    {
                        _networkRelayPort = ClampNetworkPort(_networkRelayPort + direction);
                    }
                    else
                    {
                        _networkClientPort = ClampNetworkPort(_networkClientPort + direction);
                    }

                    SaveSettings();
                    break;
                case 13:
                    // Role is only the requested join role. Host permissions remain
                    // authoritative after connection.
                    _networkRequestedRole = _networkRequestedRole == C64NetClientRole.Observer
                        ? C64NetClientRole.Player
                        : C64NetClientRole.Observer;
                    SaveSettings();
                    break;
                case 14:
                    if (direction != 0)
                    {
                        ToggleNetworkClientSession();
                    }

                    break;
                case 15:
                    // Video filter is local presentation even during a remote session.
                    CycleVideoFilter();
                    break;
                case 16:
                    // Upscaling is a local presentation choice and does not affect the
                    // server stream or other connected clients.
                    CycleVideoUpscale();
                    break;
            }
        }

        /// <summary>
        /// Activates the selected network menu row with Enter.
        /// </summary>
        private void ActivateNetworkMenuItem()
        {
            if (!IsNetworkMenuRowEnabled(_networkOverlaySelection))
            {
                ShowNetworkStatus("NETWORK ITEM DISABLED");
                return;
            }

            switch (_networkOverlaySelection)
            {
                case 0:
                    _networkTransportMode = _networkTransportMode == C64NetTransportMode.Relay
                        ? C64NetTransportMode.Lan
                        : C64NetTransportMode.Relay;
                    SaveSettings();
                    break;
                case 4:
                    ToggleNetworkServer();
                    break;
                case 5:
                    MoveSelectedNetworkClient(1);
                    break;
                case 6:
                    CycleSelectedNetworkClientPermission();
                    break;
                case 7:
                    ToggleSelectedNetworkClientKeyboard();
                    break;
                case 8:
                    KickSelectedNetworkClient();
                    break;
                case 13:
                    _networkRequestedRole = _networkRequestedRole == C64NetClientRole.Observer
                        ? C64NetClientRole.Player
                        : C64NetClientRole.Observer;
                    SaveSettings();
                    break;
                case 14:
                    ToggleNetworkClientSession();
                    break;
                case 15:
                    CycleVideoFilter();
                    break;
                case 16:
                    CycleVideoUpscale();
                    break;
            }
        }

        /// <summary>
        /// Starts or stops the local host server from the network menu.
        /// </summary>
        private void ToggleNetworkServer()
        {
            C64NetServer server = _networkServer;
            if (server != null && server.IsRunning)
            {
                StopNetworkServer();
                ShowNetworkStatus("SERVER STOPPED");
                return;
            }

            StartNetworkServer();
        }

        /// <summary>
        /// Joins or leaves a remote host session from the network menu.
        /// </summary>
        private void ToggleNetworkClientSession()
        {
            if (_networkMode == NetworkSessionMode.Client)
            {
                StopNetworkClientSession();
                ShowNetworkStatus("CLIENT LEFT");
                return;
            }

            StartNetworkClientSession();
        }

        /// <summary>
        /// Applies editable text/numeric input to the selected network menu field.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="modifiers">Keyboard modifiers.</param>
        /// <returns>True when the key changed or was handled by an editable field.</returns>
        private bool TryApplyNetworkTextKey(Key key, KeyModifiers modifiers)
        {
            if (!IsNetworkMenuRowEnabled(_networkOverlaySelection))
            {
                return false;
            }

            bool serverPortField = _networkOverlaySelection == 1;
            bool clientPortField = _networkOverlaySelection == 11;
            if (serverPortField || clientPortField)
            {
                // Ports are edited as numbers directly inside the pixel overlay. A blank
                // port is represented by 0 and shown as TYPE PORT.
                if (key == Key.BackSpace)
                {
                    int port = (serverPortField ? _networkServerPort : GetNetworkMenuTargetPort()) / 10;
                    if (port <= 0)
                    {
                        port = serverPortField || _networkTransportMode == C64NetTransportMode.Lan
                            ? C64NetProtocol.DefaultPort
                            : C64NetProtocol.DefaultRelayPort;
                    }

                    SetNetworkMenuPort(serverPortField, port);
                    return true;
                }

                if (key == Key.Delete)
                {
                    SetNetworkMenuPort(serverPortField, 0);
                    return true;
                }

                int digit;
                if (TryGetDigit(key, out digit))
                {
                    int currentPort = serverPortField ? _networkServerPort : GetNetworkMenuTargetPort();
                    SetNetworkMenuPort(serverPortField, ClampNetworkPort((currentPort * 10) + digit));
                    return true;
                }

                return false;
            }

            bool serverPasswordField = _networkOverlaySelection == 2;
            bool connectionIdField = _networkOverlaySelection == 3;
            bool playerNameField = _networkOverlaySelection == 9;
            bool hostField = _networkOverlaySelection == 10;
            bool clientPasswordField = _networkOverlaySelection == 12;
            if (!serverPasswordField && !connectionIdField && !playerNameField && !hostField && !clientPasswordField)
            {
                return false;
            }

            if (key == Key.BackSpace)
            {
                // Text editing is intentionally simple: ASCII-like keys, backspace, and
                // delete are enough for host names, player names, and passwords.
                if (serverPasswordField && _networkServerPassword.Length > 0)
                {
                    _networkServerPassword = _networkServerPassword.Substring(0, _networkServerPassword.Length - 1);
                    SaveSettings();
                }
                else if (connectionIdField && _networkConnectionId.Length > 0)
                {
                    _networkConnectionId = _networkConnectionId.Substring(0, _networkConnectionId.Length - 1);
                    SaveSettings();
                }
                else if (playerNameField && _networkPlayerName.Length > 0)
                {
                    _networkPlayerName = _networkPlayerName.Substring(0, _networkPlayerName.Length - 1);
                    SaveSettings();
                }
                else if (hostField && _networkHost.Length > 0)
                {
                    _networkHost = _networkHost.Substring(0, _networkHost.Length - 1);
                    SaveSettings();
                }
                else if (clientPasswordField && _networkClientPassword.Length > 0)
                {
                    _networkClientPassword = _networkClientPassword.Substring(0, _networkClientPassword.Length - 1);
                    SaveSettings();
                }

                return true;
            }

            if (key == Key.Delete)
            {
                if (serverPasswordField)
                {
                    _networkServerPassword = string.Empty;
                }
                else if (connectionIdField)
                {
                    _networkConnectionId = string.Empty;
                }
                else if (playerNameField)
                {
                    _networkPlayerName = string.Empty;
                }
                else if (hostField)
                {
                    _networkHost = string.Empty;
                }
                else
                {
                    _networkClientPassword = string.Empty;
                }

                SaveSettings();
                return true;
            }

            char character;
            if (!TryMapNetworkTextKey(_system.MapHostKeyboardLayoutKey(key), modifiers, out character))
            {
                return false;
            }

            if (serverPasswordField)
            {
                if (_networkServerPassword.Length < 32)
                {
                    // Passwords are stored in full but rendered as asterisks in the menu.
                    _networkServerPassword += character;
                    SaveSettings();
                }
            }
            else if (connectionIdField && IsNetworkConnectionIdCharacter(character) && _networkConnectionId.Length < 48)
            {
                _networkConnectionId += char.ToLowerInvariant(character);
                SaveSettings();
            }
            else if (playerNameField && IsNetworkPlayerNameCharacter(character) && _networkPlayerName.Length < 24)
            {
                _networkPlayerName += character;
                SaveSettings();
            }
            else if (hostField && IsNetworkHostCharacter(character) && _networkHost.Length < 64)
            {
                _networkHost += char.ToLowerInvariant(character);
                SaveSettings();
            }
            else if (clientPasswordField)
            {
                if (_networkClientPassword.Length < 32)
                {
                    _networkClientPassword += character;
                    SaveSettings();
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the currently edited target port from the client/relay menu row.
        /// </summary>
        /// <returns>LAN client port in LAN mode, relay TLS port in Relay mode.</returns>
        private int GetNetworkMenuTargetPort()
        {
            return _networkTransportMode == C64NetTransportMode.Relay
                ? _networkRelayPort
                : _networkClientPort;
        }

        /// <summary>
        /// Stores a clamped server/client or relay port and persists the menu setting.
        /// </summary>
        /// <param name="serverPortField">True for the server port, false for client/relay port.</param>
        /// <param name="port">Requested TCP port.</param>
        private void SetNetworkMenuPort(bool serverPortField, int port)
        {
            if (serverPortField)
            {
                _networkServerPort = ClampNetworkPort(port);
            }
            else if (_networkTransportMode == C64NetTransportMode.Relay)
            {
                _networkRelayPort = ClampNetworkPort(port);
            }
            else
            {
                _networkClientPort = ClampNetworkPort(port);
            }

            SaveSettings();
        }

        /// <summary>
        /// Moves the selected client row in the host client list.
        /// </summary>
        /// <param name="delta">Movement direction.</param>
        private void MoveSelectedNetworkClient(int delta)
        {
            List<C64NetClientSnapshot> clients = GetNetworkClientSnapshots();
            if (clients.Count == 0)
            {
                _networkSelectedClientIndex = 0;
                return;
            }

            _networkSelectedClientIndex += delta;
            if (_networkSelectedClientIndex < 0)
            {
                _networkSelectedClientIndex = clients.Count - 1;
            }

            if (_networkSelectedClientIndex >= clients.Count)
            {
                _networkSelectedClientIndex = 0;
            }
        }

        /// <summary>
        /// Cycles joystick rights for the currently selected remote client.
        /// </summary>
        private void CycleSelectedNetworkClientPermission()
        {
            C64NetServer server = _networkServer;
            if (server == null || !server.IsRunning)
            {
                return;
            }

            C64NetClientSnapshot client = GetSelectedNetworkClient();
            if (client != null)
            {
                server.CycleClientPermission(client.ClientId);
            }
        }

        /// <summary>
        /// Toggles keyboard rights for the currently selected remote client.
        /// </summary>
        private void ToggleSelectedNetworkClientKeyboard()
        {
            C64NetServer server = _networkServer;
            if (server == null || !server.IsRunning)
            {
                return;
            }

            C64NetClientSnapshot client = GetSelectedNetworkClient();
            if (client != null)
            {
                server.SetClientKeyboardEnabled(client.ClientId, !client.KeyboardEnabled);
            }
        }

        /// <summary>
        /// Kicks the currently selected remote client from the current host session.
        /// </summary>
        private void KickSelectedNetworkClient()
        {
            C64NetServer server = _networkServer;
            if (server == null || !server.IsRunning)
            {
                return;
            }

            C64NetClientSnapshot client = GetSelectedNetworkClient();
            if (client != null)
            {
                server.KickClient(client.ClientId);
            }
        }

        /// <summary>
        /// Gets the current client snapshots from the server or cached remote list.
        /// </summary>
        /// <returns>Stable snapshot list for rendering and selection.</returns>
        private List<C64NetClientSnapshot> GetNetworkClientSnapshots()
        {
            C64NetServer server = _networkServer;
            if (server != null && server.IsRunning)
            {
                // Host mode reads directly from the server so the menu is current even if
                // no client-list broadcast has been processed by this window.
                List<C64NetClientSnapshot> serverClients = server.GetClientSnapshots();
                lock (_networkClientsSync)
                {
                    _networkClients = serverClients;
                }

                ClampNetworkSelectedClientIndex();
                return serverClients;
            }

            lock (_networkClientsSync)
            {
                // Remote clients use the latest ClientList message received from the host.
                var clients = new List<C64NetClientSnapshot>(_networkClients);
                ClampNetworkSelectedClientIndex();
                return clients;
            }
        }

        /// <summary>
        /// Gets the currently selected client snapshot, if any.
        /// </summary>
        /// <returns>Selected snapshot or null when the list is empty.</returns>
        private C64NetClientSnapshot GetSelectedNetworkClient()
        {
            List<C64NetClientSnapshot> clients = GetNetworkClientSnapshots();
            if (clients.Count == 0)
            {
                return null;
            }

            int index = Math.Max(0, Math.Min(_networkSelectedClientIndex, clients.Count - 1));
            return clients[index];
        }

        /// <summary>
        /// Keeps the selected client index valid after list changes.
        /// </summary>
        private void ClampNetworkSelectedClientIndex()
        {
            int count = _networkClients == null ? 0 : _networkClients.Count;
            if (count <= 0)
            {
                _networkSelectedClientIndex = 0;
                return;
            }

            if (_networkSelectedClientIndex >= count)
            {
                _networkSelectedClientIndex = count - 1;
            }

            if (_networkSelectedClientIndex < 0)
            {
                _networkSelectedClientIndex = 0;
            }
        }

        /// <summary>
        /// Clamps a TCP port to the valid user-editable range.
        /// </summary>
        /// <param name="port">Requested port.</param>
        /// <returns>Value between 0 and 65535. Zero means the UI field is empty.</returns>
        private static int ClampNetworkPort(int port)
        {
            if (port < 0)
            {
                return 0;
            }

            if (port > 65535)
            {
                return 65535;
            }

            return port;
        }

        /// <summary>
        /// Maps number-row keys to decimal digits.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="digit">Decoded digit.</param>
        /// <returns>True when the key is a number-row digit.</returns>
        private static bool TryGetDigit(Key key, out int digit)
        {
            switch (key)
            {
                case Key.Number0:
                    digit = 0;
                    return true;
                case Key.Number1:
                    digit = 1;
                    return true;
                case Key.Number2:
                    digit = 2;
                    return true;
                case Key.Number3:
                    digit = 3;
                    return true;
                case Key.Number4:
                    digit = 4;
                    return true;
                case Key.Number5:
                    digit = 5;
                    return true;
                case Key.Number6:
                    digit = 6;
                    return true;
                case Key.Number7:
                    digit = 7;
                    return true;
                case Key.Number8:
                    digit = 8;
                    return true;
                case Key.Number9:
                    digit = 9;
                    return true;
                default:
                    digit = 0;
                    return false;
            }
        }

        /// <summary>
        /// Maps allowed overlay text-entry keys to characters.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="modifiers">Keyboard modifiers.</param>
        /// <param name="character">Decoded character.</param>
        /// <returns>True when the key can contribute text to a network field.</returns>
        private static bool TryMapNetworkTextKey(Key key, KeyModifiers modifiers, out char character)
        {
            bool shifted = (modifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
            if (key >= Key.A && key <= Key.Z)
            {
                // The overlay text input does not use OS text composition; it maps the
                // small key set needed by the network menu directly.
                character = (char)((shifted ? 'A' : 'a') + ((int)key - (int)Key.A));
                return true;
            }

            int digit;
            if (TryGetDigit(key, out digit))
            {
                character = (char)('0' + digit);
                return true;
            }

            switch (key)
            {
                case Key.Period:
                    character = '.';
                    return true;
                case Key.Minus:
                    character = '-';
                    return true;
                case Key.Plus:
                    character = '+';
                    return true;
                case Key.BackSlash:
                    character = '\\';
                    return true;
                case Key.Slash:
                    character = '/';
                    return true;
                case Key.Space:
                    character = ' ';
                    return true;
                default:
                    character = '\0';
                    return false;
            }
        }

        /// <summary>
        /// Tests whether a character is valid for the host/address field.
        /// </summary>
        /// <param name="character">Character to test.</param>
        /// <returns>True for simple DNS/IP host characters.</returns>
        private static bool IsNetworkHostCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '.' || character == '-';
        }

        /// <summary>
        /// Tests whether a character is valid for the player name field.
        /// </summary>
        /// <param name="character">Character to test.</param>
        /// <returns>True when the character is accepted in player names.</returns>
        private static bool IsNetworkPlayerNameCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == ' ' || character == '-' || character == '.' || character == '_';
        }

        /// <summary>
        /// Tests whether a character is valid for a relay connection id.
        /// </summary>
        /// <param name="character">Character to test.</param>
        /// <returns>True for compact identifier characters.</returns>
        private static bool IsNetworkConnectionIdCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '-' || character == '.' || character == '_';
        }

        /// <summary>
        /// Normalizes a relay connection id for storage and relay registration.
        /// </summary>
        /// <param name="connectionId">Raw menu value.</param>
        /// <returns>Trimmed lower-case id, defaulting to c64.</returns>
        private static string NormalizeNetworkConnectionId(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return "c64";
            }

            string trimmed = connectionId.Trim().ToLowerInvariant();
            return trimmed.Length <= 48 ? trimmed : trimmed.Substring(0, 48);
        }

        /// <summary>
        /// Normalizes a player name for storage and handshake use.
        /// </summary>
        /// <param name="name">Raw menu/player name.</param>
        /// <returns>Trimmed name, defaulting to player and capped at 24 characters.</returns>
        private static string NormalizeNetworkPlayerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "player";
            }

            string trimmed = name.Trim();
            return trimmed.Length <= 24 ? trimmed : trimmed.Substring(0, 24);
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
                case Key.PageUp:
                    MoveAudioOverlaySelection(-AudioOverlayVisibleRows);
                    return true;
                case Key.PageDown:
                    MoveAudioOverlaySelection(AudioOverlayVisibleRows);
                    return true;
                case Key.Left:
                case Key.Minus:
                    if (!CanAdjustAudioOverlayItem(_audioOverlaySelection))
                    {
                        // Remote clients may change only local presentation settings. SID,
                        // media, reset, and joystick source are owned by the server.
                        ShowNetworkStatus("SETTING SERVER CONTROLLED");
                        return true;
                    }

                    AdjustAudioSetting(-1);
                    return true;
                case Key.Right:
                case Key.Plus:
                    if (!CanAdjustAudioOverlayItem(_audioOverlaySelection))
                    {
                        // Keep the feedback in the network toast so it is visible in both
                        // local and remote presentation paths.
                        ShowNetworkStatus("SETTING SERVER CONTROLLED");
                        return true;
                    }

                    AdjustAudioSetting(1);
                    return true;
                case Key.Enter:
                    if (!CanAdjustAudioOverlayItem(_audioOverlaySelection))
                    {
                        // Enter on a server-controlled item is blocked for the same reason
                        // as left/right adjustment.
                        ShowNetworkStatus("SETTING SERVER CONTROLLED");
                        return true;
                    }

                    if (_audioOverlaySelection == 12)
                    {
                        OpenControllerMappingOverlay();
                        return true;
                    }

                    AdjustAudioSetting(1);
                    return true;
                case Key.Escape:
                    ReturnToMainMenuFromSubmenu();
                    return true;
                default:
                    return true;
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
                case Key.PageUp:
                case Key.PageDown:
                case Key.Left:
                case Key.Right:
                case Key.Plus:
                case Key.Minus:
                case Key.Enter:
                case Key.Escape:
                    return true;
                default:
                    return true;
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
                SaveSettings();
                return;
            }

            if (_audioOverlaySelection == 1)
            {
                _system.SetSidNoiseLevel(_system.SidNoiseLevel + (direction * NoiseStep));
                SaveSettings();
                return;
            }

            if (_audioOverlaySelection == 2)
            {
                _system.SetSidChipModel(_system.CurrentSidChipModel == SidChipModel.Mos6581 ? SidChipModel.Mos8580 : SidChipModel.Mos6581);
                SaveSettings();
                return;
            }

            if (_audioOverlaySelection == 3)
            {
                _system.SetJoystickPort(GetNextJoystickPort(_system.CurrentJoystickPort));
                _overlayStatusText = "JOYSTICK " + FormatJoystickPort(_system.CurrentJoystickPort);
                SaveSettings();
                return;
            }

            if (_audioOverlaySelection == 4)
            {
                ToggleHostKeyboardLayout();
                return;
            }

            if (_audioOverlaySelection == 5)
            {
                CycleMouse1351Port();
                return;
            }

            if (_audioOverlaySelection == 6)
            {
                ToggleDisplayMode();
                return;
            }

            if (_audioOverlaySelection == 7)
            {
                CycleRenderFrameLimit();
                return;
            }

            if (_audioOverlaySelection == 8)
            {
                CycleVideoFilter();
                return;
            }

            if (_audioOverlaySelection == 9)
            {
                CycleVideoUpscale();
                return;
            }

            if (_audioOverlaySelection == 10)
            {
                ToggleVideoZoom();
                return;
            }

            if (_audioOverlaySelection == 11)
            {
                ToggleTurboMode();
                return;
            }

            if (_audioOverlaySelection == 12)
            {
                ToggleGamepadInput();
                return;
            }

            if (_audioOverlaySelection == 13)
            {
                ToggleLoadHack();
                return;
            }

            if (_audioOverlaySelection == 14)
            {
                ToggleSoftwareIecTransport();
                return;
            }

            if (_audioOverlaySelection == 15)
            {
                ToggleInputInjection();
                return;
            }

            if (_audioOverlaySelection == 16)
            {
                CycleResetMode();
                return;
            }

            if (_audioOverlaySelection == 17)
            {
                ToggleDriveOverlay();
                return;
            }

            if (_audioOverlaySelection == 18)
            {
                ToggleEasyFlash();
                return;
            }

            if (_audioOverlaySelection == 19)
            {
                SaveEasyFlash();
                return;
            }

            if (_audioOverlaySelection == 20)
            {
                EjectEasyFlash();
                return;
            }

            if (_audioOverlaySelection == 21)
            {
                ToggleReu();
                return;
            }

            if (_audioOverlaySelection == 22)
            {
                CycleReuSize(direction);
                return;
            }
        }

        /// <summary>
        /// Toggles the direct KERNAL LOAD compatibility path.
        /// </summary>
        private void ToggleLoadHack()
        {
            _system.EnableLoadHack = !_system.EnableLoadHack;
            _overlayStatusText = _system.EnableLoadHack ? "LOAD HACK ON" : "LOAD HACK OFF";
            SaveSettings();
        }

        /// <summary>
        /// Toggles the high-level software IEC transport for standard DOS traffic.
        /// </summary>
        private void ToggleSoftwareIecTransport()
        {
            _system.ForceSoftwareIecTransport = !_system.ForceSoftwareIecTransport;
            _overlayStatusText = _system.ForceSoftwareIecTransport ? "IEC SW ON" : "IEC SW OFF";
            SaveSettings();
        }

        /// <summary>
        /// Toggles host-side input injection for known intro polling loops.
        /// </summary>
        private void ToggleInputInjection()
        {
            _system.EnableInputInjection = !_system.EnableInputInjection;
            _overlayStatusText = _system.EnableInputInjection ? "INPUT INJECT ON" : "INPUT INJECT OFF";
            SaveSettings();
        }

        /// <summary>
        /// Enables or disables the inserted EasyFlash cartridge.
        /// </summary>
        private void ToggleEasyFlash()
        {
            if (!_system.IsEasyFlashInserted)
            {
                _overlayStatusText = "NO EASYFLASH";
                return;
            }

            _overlayStatusText = _system.SetEasyFlashEnabled(!_system.EasyFlashEnabled);
            SaveSettings();
            ResetEmulationTiming();
        }

        /// <summary>
        /// Saves the editable EasyFlash image back to its CRT file.
        /// </summary>
        private void SaveEasyFlash()
        {
            _overlayStatusText = _system.SaveEasyFlash();
            SaveSettings();
        }

        /// <summary>
        /// Ejects the inserted EasyFlash cartridge.
        /// </summary>
        private void EjectEasyFlash()
        {
            _overlayStatusText = _system.EjectEasyFlash();
            SaveSettings();
            ResetEmulationTiming();
        }

        /// <summary>
        /// Enables or disables the RAM Expansion Unit.
        /// </summary>
        private void ToggleReu()
        {
            _overlayStatusText = _system.SetReuEnabled(!_system.ReuEnabled);
            SaveSettings();
            ResetEmulationTiming();
        }

        /// <summary>
        /// Cycles the configured REU capacity.
        /// </summary>
        private void CycleReuSize(int direction)
        {
            _overlayStatusText = _system.SetReuSize(GetNextReuSize(_system.ReuSize, direction));
            SaveSettings();
            ResetEmulationTiming();
        }

        /// <summary>
        /// Toggles the frontend host keyboard layout.
        /// </summary>
        private void ToggleHostKeyboardLayout()
        {
            HostKeyboardLayout nextLayout = _system.CurrentHostKeyboardLayout == HostKeyboardLayout.Ger
                ? HostKeyboardLayout.En
                : HostKeyboardLayout.Ger;
            _system.SetHostKeyboardLayout(nextLayout);
            _overlayStatusText = "KEYBOARD " + FormatHostKeyboardLayout(nextLayout);
            SaveSettings();
        }

        /// <summary>
        /// Cycles the Commodore 1351 mouse emulation target port.
        /// </summary>
        private void CycleMouse1351Port()
        {
            switch (_mouse1351Port)
            {
                case Mouse1351Port.Off:
                    _mouse1351Port = Mouse1351Port.Port1;
                    break;
                case Mouse1351Port.Port1:
                    _mouse1351Port = Mouse1351Port.Port2;
                    break;
                default:
                    _mouse1351Port = Mouse1351Port.Off;
                    break;
            }

            _hasLastMousePosition = false;
            _lastMouse1351JoystickState = 0x1F;
            ApplyMouse1351StateToSystem();
            UpdateMouse1351CaptureState();
            _overlayStatusText = "1351 MOUSE " + FormatMouse1351Port(_mouse1351Port);
            SaveSettings();
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
                    _overlayStatusText = GetConfirmationCanceledStatusText();
                    ClearConfirmationState();
                    return true;
                default:
                    return true;
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
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles media browser key down.
        /// </summary>
        private bool HandleMediaBrowserKeyDown(Key key)
        {
            char jumpLetter;
            if (TryGetMediaBrowserJumpLetter(_system.MapHostKeyboardLayoutKey(key), out jumpLetter))
            {
                JumpMediaBrowserToLetter(jumpLetter);
                return true;
            }

            switch (key)
            {
                case Key.Up:
                    MoveMediaBrowserSelection(-1);
                    return true;
                case Key.Down:
                    MoveMediaBrowserSelection(1);
                    return true;
                case Key.PageUp:
                    MoveMediaBrowserSelection(-GetStandaloneMediaBrowserVisibleRows());
                    return true;
                case Key.PageDown:
                    MoveMediaBrowserSelection(GetStandaloneMediaBrowserVisibleRows());
                    return true;
                case Key.Left:
                    _mediaBrowserTargetDrive = Math.Max(8, _mediaBrowserTargetDrive - 1);
                    SaveSettings();
                    return true;
                case Key.Right:
                    _mediaBrowserTargetDrive = Math.Min(11, _mediaBrowserTargetDrive + 1);
                    SaveSettings();
                    return true;
                case Key.Number8:
                    _mediaBrowserTargetDrive = 8;
                    SaveSettings();
                    return true;
                case Key.Number9:
                    _mediaBrowserTargetDrive = 9;
                    SaveSettings();
                    return true;
                case Key.Number0:
                    _mediaBrowserTargetDrive = 10;
                    SaveSettings();
                    return true;
                case Key.Number1:
                    _mediaBrowserTargetDrive = 11;
                    SaveSettings();
                    return true;
                case Key.Enter:
                    ActivateMediaBrowserSelection();
                    return true;
                case Key.Escape:
                    ReturnToMainMenuFromSubmenu();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Draws the top-level F10 emulator menu.
        /// </summary>
        private void DrawMainMenuOverlay()
        {
            int overlayWidth = 294;
            int overlayHeight = 158;
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = (PixelsHeight - overlayHeight) / 2;

            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 10, 14, 24, 228);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 108, 214, 182);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 108, 214, 182);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 108, 214, 182);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 108, 214, 182);

            DrawOverlayText(overlayX + 16, overlayY + 14, "MAIN MENU", 2, 240, 248, 255);
            DrawOverlayText(overlayX + 16, overlayY + 34, "ENTER OPEN/CHANGE  ESC CLOSE", 1, 192, 210, 225);

            int itemX = overlayX + 26;
            int itemY = overlayY + 56;
            for (int index = 0; index < MainMenuItemCount; index++)
            {
                bool selected = _mainMenuSelection == index;
                bool enabled = IsMainMenuItemEnabled((MainMenuItem)index);
                byte red = enabled ? (selected ? (byte)255 : (byte)192) : (byte)100;
                byte green = enabled ? (selected ? (byte)243 : (byte)210) : (byte)108;
                byte blue = enabled ? (selected ? (byte)168 : (byte)225) : (byte)118;
                DrawOverlayText(itemX, itemY + (index * 18), (selected ? "> " : "  ") + GetMainMenuLabel((MainMenuItem)index), 2, red, green, blue);
                if (index == (int)MainMenuItem.Media)
                {
                    DrawOverlayText(itemX + 128, itemY + (index * 18) + 4, FormatOverlayValue(FormatMainMenuMediaValue(), 20), 1, red, green, blue);
                }

                if (index == (int)MainMenuItem.Reset)
                {
                    DrawOverlayText(itemX + 176, itemY + (index * 18), FormatResetMode(_resetMode), 2, red, green, blue);
                }
            }

            if (_resetConfirmVisible)
            {
                DrawResetConfirmOverlay(overlayX + 49, overlayY + 62, 196, 84, _system.MountedMedia.HasMedia);
            }
        }

        /// <summary>
        /// Gets a top-level F10 menu label.
        /// </summary>
        /// <param name="item">Menu item.</param>
        /// <returns>Display label.</returns>
        private static string GetMainMenuLabel(MainMenuItem item)
        {
            switch (item)
            {
                case MainMenuItem.Settings:
                    return "SETTINGS";
                case MainMenuItem.Network:
                    return "NETWORK";
                case MainMenuItem.Media:
                    return "MEDIA";
                case MainMenuItem.Reset:
                    return "RESET";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Formats the currently mounted media for the compact main menu value column.
        /// </summary>
        /// <returns>Mounted media label.</returns>
        private string FormatMainMenuMediaValue()
        {
            Dictionary<int, string> mountedDrivePaths = _system.GetMountedDriveHostPaths();
            List<KeyValuePair<int, string>> mountedDisks = mountedDrivePaths
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .OrderBy(entry => entry.Key)
                .ToList();

            if (mountedDisks.Count > 0)
            {
                KeyValuePair<int, string> firstDisk = mountedDisks[0];
                string diskName = Path.GetFileNameWithoutExtension(firstDisk.Value);
                if (string.IsNullOrWhiteSpace(diskName))
                {
                    diskName = Path.GetFileName(firstDisk.Value);
                }

                string diskText = "D" + firstDisk.Key.ToString(CultureInfo.InvariantCulture) + " " + diskName;
                if (mountedDisks.Count > 1)
                {
                    diskText += " +" + (mountedDisks.Count - 1).ToString(CultureInfo.InvariantCulture);
                }

                return diskText;
            }

            MountedMediaInfo mountedMediaInfo = _system.MountedMedia;
            if (mountedMediaInfo == null || !mountedMediaInfo.HasMedia)
            {
                return "NO MEDIA";
            }

            string mediaName = !string.IsNullOrWhiteSpace(mountedMediaInfo.DisplayName)
                ? mountedMediaInfo.DisplayName
                : Path.GetFileNameWithoutExtension(mountedMediaInfo.HostPath);
            string mediaLabel = string.IsNullOrWhiteSpace(mountedMediaInfo.ShortLabel)
                ? mountedMediaInfo.Kind.ToString()
                : mountedMediaInfo.ShortLabel;
            return string.IsNullOrWhiteSpace(mediaName) ? mediaLabel : mediaLabel + " " + mediaName;
        }

        /// <summary>
        /// Draws the network overlay.
        /// </summary>
        private void DrawNetworkOverlay()
        {
            UpdateNetworkTrafficRates();
            List<C64NetClientSnapshot> clients = GetNetworkClientSnapshots();
            int overlayWidth = 386;
            int overlayHeight = Math.Min(PixelsHeight - 8, 276);
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = (PixelsHeight - overlayHeight) / 2;

            DrawFilledRectangleWithAlpha(overlayX, overlayY, overlayWidth, overlayHeight, 10, 14, 24, 228);
            DrawLine(overlayX, overlayY, overlayX + overlayWidth - 1, overlayY, 108, 214, 182);
            DrawLine(overlayX, overlayY + overlayHeight - 1, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 108, 214, 182);
            DrawLine(overlayX, overlayY, overlayX, overlayY + overlayHeight - 1, 108, 214, 182);
            DrawLine(overlayX + overlayWidth - 1, overlayY, overlayX + overlayWidth - 1, overlayY + overlayHeight - 1, 108, 214, 182);

            DrawOverlayText(overlayX + 14, overlayY + 11, "NETWORK", 2, 240, 248, 255);
            DrawOverlayText(overlayX + 14, overlayY + 29, "ESC BACK  ENTER ACT  DEL CLEAR  TYPE EDIT", 1, 192, 210, 225);
            DrawOverlayText(overlayX + 248, overlayY + 13, FormatNetworkMode(_networkMode), 1, 108, 214, 182);

            int menuX = overlayX + 14;
            int menuY = overlayY + 45;
            for (int index = 0; index < NetworkOverlayItemCount; index++)
            {
                // Rows are always drawn in fixed positions so disabled server/client
                // sections stay visible and explain the current mode.
                DrawNetworkMenuRow(menuX, GetNetworkMenuRowY(menuY, index), index);
            }

            // Server controls live above the separator; client controls live below it.
            int separatorY = menuY + (NetworkOverlayServerItemCount * 8) + 3;
            DrawLine(menuX, separatorY, overlayX + overlayWidth - 16, separatorY, 70, 96, 112);

            int clientsY = overlayY + overlayHeight - 85;
            DrawOverlayText(menuX, clientsY, "CLIENTS " + clients.Count, 1, 108, 214, 182);
            if (clients.Count == 0)
            {
                DrawOverlayText(menuX, clientsY + 13, "NO REMOTE CLIENTS", 1, 192, 210, 225);
            }
            else
            {
                // Keep the visible list short enough to fit inside the C64 overlay while
                // still allowing the selected client to move through all connections.
                int firstClient = Math.Max(0, Math.Min(_networkSelectedClientIndex, Math.Max(0, clients.Count - NetworkOverlayVisibleClientRows)));
                for (int row = 0; row < NetworkOverlayVisibleClientRows && firstClient + row < clients.Count; row++)
                {
                    C64NetClientSnapshot client = clients[firstClient + row];
                    bool selected = firstClient + row == _networkSelectedClientIndex;
                    byte red = selected ? (byte)255 : (byte)232;
                    byte green = selected ? (byte)243 : (byte)238;
                    byte blue = selected ? (byte)168 : (byte)244;
                    string text = string.Format(
                        "{0}{1} {2} {3} {4} {5}",
                        selected ? "> " : "  ",
                        client.ClientId,
                        FormatOverlayValue(FormatNetworkClientDisplay(client), 15),
                        FormatNetworkJoystickRight(client.Permission),
                        FormatNetworkKeyboardRightLong(client.KeyboardEnabled),
                        FormatNetworkLatencyCompact(client.LatencyMilliseconds));
                    DrawOverlayText(menuX, clientsY + 13 + (row * 12), FormatOverlayValue(text, 58), 1, red, green, blue);
                }
            }

            DrawOverlayText(menuX, overlayY + overlayHeight - 39, FormatNetworkFingerprintText(), 1, 192, 210, 225);
            DrawOverlayText(menuX, overlayY + overlayHeight - 27, FormatNetworkTrafficText(), 1, 192, 210, 225);
            DrawOverlayText(menuX, overlayY + overlayHeight - 15, "STATUS " + FormatOverlayValue(_networkStatusText, 45), 1, 108, 214, 182);
            DrawNetworkTlsCertificatePrompt(overlayX, overlayY, overlayWidth, overlayHeight);
        }

        /// <summary>
        /// Draws the modal warning for a changed pinned TLS certificate.
        /// </summary>
        /// <param name="parentX">Network overlay x coordinate.</param>
        /// <param name="parentY">Network overlay y coordinate.</param>
        /// <param name="parentWidth">Network overlay width.</param>
        /// <param name="parentHeight">Network overlay height.</param>
        private void DrawNetworkTlsCertificatePrompt(int parentX, int parentY, int parentWidth, int parentHeight)
        {
            if (!_networkTlsCertificatePromptVisible || _pendingNetworkTlsPinChange == null)
            {
                return;
            }

            C64NetTlsPinChange pinChange = _pendingNetworkTlsPinChange;
            int width = Math.Min(parentWidth - 36, 344);
            int height = 88;
            int x = parentX + ((parentWidth - width) / 2);
            int y = parentY + ((parentHeight - height) / 2);

            DrawFilledRectangleWithAlpha(x, y, width, height, 20, 10, 12, 244);
            DrawLine(x, y, x + width - 1, y, 255, 243, 168);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 255, 243, 168);
            DrawLine(x, y, x, y + height - 1, 255, 243, 168);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, 255, 243, 168);

            DrawOverlayText(x + 12, y + 10, "WARNING! TLS SERVER CERTIFICATE CHANGED", 1, 255, 243, 168);
            DrawOverlayText(x + 12, y + 25, "HOST " + FormatOverlayValue(pinChange.Host + ":" + pinChange.Port.ToString(CultureInfo.InvariantCulture), 46), 1, 240, 248, 255);
            DrawOverlayText(x + 12, y + 40, "FROM OLD ID " + pinChange.OldShortFingerprint, 1, 240, 210, 210);
            DrawOverlayText(x + 12, y + 53, "TO   NEW ID " + pinChange.NewShortFingerprint, 1, 210, 255, 220);
            DrawOverlayText(x + 12, y + 70, "ENTER REPLACE PIN   ESC ABORT", 1, 108, 214, 182);
        }

        /// <summary>
        /// Draws a short-lived network status toast.
        /// </summary>
        private void DrawNetworkStatusToast()
        {
            long untilTicks = Interlocked.Read(ref _networkStatusToastUntilTicks);
            if (untilTicks <= DateTime.UtcNow.Ticks || string.IsNullOrWhiteSpace(_networkStatusToastText))
            {
                return;
            }

            string text = FormatOverlayValue(_networkStatusToastText, 36);
            int width = Math.Min(PixelsWidth - 20, Math.Max(160, (text.Length * 6) + 24));
            int height = 28;
            int x = (PixelsWidth - width) / 2;
            int y = PixelsHeight - height - 12;
            DrawFilledRectangleWithAlpha(x, y, width, height, 8, 10, 18, 225);
            DrawLine(x, y, x + width - 1, y, 108, 214, 182);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 108, 214, 182);
            DrawOverlayText(x + 12, y + 10, text, 1, 240, 248, 255);
        }

        /// <summary>
        /// Draws the persistent remote-client popup that mirrors the host menu state.
        /// </summary>
        private void DrawNetworkHostOverlayPopup()
        {
            if (_networkMode != NetworkSessionMode.Client || string.IsNullOrWhiteSpace(_networkHostOverlayStatusText))
            {
                return;
            }

            string text = FormatOverlayValue(_networkHostOverlayStatusText, 38);
            int width = Math.Min(PixelsWidth - 20, Math.Max(178, (text.Length * 6) + 24));
            int height = 28;
            int x = (PixelsWidth - width) / 2;
            int y = 12;
            DrawFilledRectangleWithAlpha(x, y, width, height, 18, 20, 32, 232);
            DrawLine(x, y, x + width - 1, y, 255, 243, 168);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 255, 243, 168);
            DrawOverlayText(x + 12, y + 10, text, 1, 255, 243, 168);
        }

        /// <summary>
        /// Draws one row of the network menu.
        /// </summary>
        /// <param name="x">Menu label x coordinate.</param>
        /// <param name="y">Menu row y coordinate.</param>
        /// <param name="index">Network menu row index.</param>
        private void DrawNetworkMenuRow(int x, int y, int index)
        {
            bool selected = _networkOverlaySelection == index;
            bool enabled = IsNetworkMenuRowEnabled(index);
            // Disabled rows stay readable but subdued, which makes host/client mode
            // restrictions visible without hiding configuration context.
            byte labelRed = enabled ? (selected ? (byte)255 : (byte)192) : (byte)100;
            byte labelGreen = enabled ? (selected ? (byte)243 : (byte)210) : (byte)108;
            byte labelBlue = enabled ? (selected ? (byte)168 : (byte)225) : (byte)118;
            DrawOverlayText(x, y, (selected ? "> " : "  ") + GetNetworkMenuLabel(index), 1, labelRed, labelGreen, labelBlue);
            byte valueRed = enabled ? (byte)240 : (byte)118;
            byte valueGreen = enabled ? (byte)248 : (byte)124;
            byte valueBlue = enabled ? (byte)255 : (byte)132;
            DrawOverlayText(x + 150, y, FormatOverlayValue(GetNetworkMenuValue(index), 28), 1, valueRed, valueGreen, valueBlue);
        }

        /// <summary>
        /// Computes the vertical position for a network menu row.
        /// </summary>
        /// <param name="menuY">Top y coordinate of the first row.</param>
        /// <param name="index">Network menu row index.</param>
        /// <returns>Pixel y coordinate for the row.</returns>
        private static int GetNetworkMenuRowY(int menuY, int index)
        {
            int extraGap = index >= NetworkOverlayServerItemCount ? 10 : 0;
            return menuY + (index * 8) + extraGap;
        }

        /// <summary>
        /// Checks whether a network menu row can be edited in the current mode.
        /// </summary>
        /// <param name="index">Network menu row index.</param>
        /// <returns>True when the row is active.</returns>
        private bool IsNetworkMenuRowEnabled(int index)
        {
            if (_networkMode == NetworkSessionMode.Client)
            {
                // Remote clients may leave and change local presentation options, but
                // server-owned emulator/session settings are locked.
                return index == 14 || index == 15 || index == 16;
            }

            if (_networkMode == NetworkSessionMode.Host)
            {
                // While hosting, server controls and local presentation options remain
                // available; joining another host from the same instance is disabled.
                return index <= 8 || index == 15 || index == 16;
            }

            return true;
        }

        /// <summary>
        /// Gets the fixed label for a network menu row.
        /// </summary>
        /// <param name="index">Network menu row index.</param>
        /// <returns>Overlay label text.</returns>
        private string GetNetworkMenuLabel(int index)
        {
            switch (index)
            {
                case 0:
                    return "MODE";
                case 1:
                    return "SERVER PORT";
                case 2:
                    return "SERVER PASSWORD";
                case 3:
                    return "CONNECTION ID";
                case 4:
                    return "SERVER";
                case 5:
                    return "SELECT CLIENT";
                case 6:
                    return "CLIENT RIGHT";
                case 7:
                    return "KEYBOARD RIGHT";
                case 8:
                    return "KICK CLIENT";
                case 9:
                    return "PLAYER NAME";
                case 10:
                    return _networkTransportMode == C64NetTransportMode.Relay ? "RELAY HOST" : "CLIENT HOST";
                case 11:
                    return _networkTransportMode == C64NetTransportMode.Relay ? "RELAY PORT" : "CLIENT PORT";
                case 12:
                    return "CLIENT PASSWORD";
                case 13:
                    return "CLIENT ROLE";
                case 14:
                    return "CLIENT";
                case 15:
                    return "VIDEO FILTER";
                case 16:
                    return "VIDEO UPSCALE";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Gets the current display value for a network menu row.
        /// </summary>
        /// <param name="index">Network menu row index.</param>
        /// <returns>Overlay value text.</returns>
        private string GetNetworkMenuValue(int index)
        {
            C64NetClientSnapshot client;
            switch (index)
            {
                case 0:
                    return _networkTransportMode == C64NetTransportMode.Relay ? "RELAY" : "LAN";
                case 1:
                    return _networkServerPort <= 0 ? "TYPE PORT" : _networkServerPort.ToString();
                case 2:
                    return FormatNetworkPassword(_networkServerPassword);
                case 3:
                    return NormalizeNetworkConnectionId(_networkConnectionId);
                case 4:
                    return _networkServer != null && _networkServer.IsRunning ? "STOP SERVER" : "START SERVER";
                case 5:
                    client = GetSelectedNetworkClient();
                    return client == null ? "NONE" : "#" + client.ClientId + " " + FormatNetworkClientDisplay(client);
                case 6:
                    client = GetSelectedNetworkClient();
                    return client == null ? "NONE" : FormatNetworkJoystickRight(client.Permission);
                case 7:
                    client = GetSelectedNetworkClient();
                    return client == null ? "NONE" : FormatNetworkKeyboardRightLong(client.KeyboardEnabled);
                case 8:
                    client = GetSelectedNetworkClient();
                    return client == null ? "NONE" : FormatNetworkClientDisplay(client);
                case 9:
                    return NormalizeNetworkPlayerName(_networkPlayerName);
                case 10:
                    return string.IsNullOrWhiteSpace(_networkHost) ? "TYPE HOST" : _networkHost;
                case 11:
                    int targetPort = GetNetworkMenuTargetPort();
                    return targetPort <= 0 ? "TYPE PORT" : targetPort.ToString();
                case 12:
                    return FormatNetworkPassword(_networkClientPassword);
                case 13:
                    return _networkRequestedRole == C64NetClientRole.Observer ? "OBSERVER" : "PLAYER";
                case 14:
                    return _networkMode == NetworkSessionMode.Client ? "CLIENT LEAVE" : "CLIENT JOIN";
                case 15:
                    return FormatVideoFilter(_videoFilterMode);
                case 16:
                    return FormatVideoUpscale(_videoUpscaleMode);
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Checks whether an F10 settings row remains locally adjustable in remote mode.
        /// </summary>
        /// <param name="menuIndex">Settings menu row index.</param>
        /// <returns>True when the current mode allows local adjustment.</returns>
        private bool CanAdjustAudioOverlayItem(int menuIndex)
        {
            if (_networkMode != NetworkSessionMode.Client)
            {
                return true;
            }

            // Remote clients can control only local display/presentation choices. The
            // server owns emulation-affecting settings such as SID, reset, and turbo.
            return menuIndex == 4 || menuIndex == 6 || menuIndex == 7 || menuIndex == 8 || menuIndex == 9 || menuIndex == 10 || menuIndex == 12;
        }

        /// <summary>
        /// Formats a password for overlay display without exposing its characters.
        /// </summary>
        /// <param name="password">Raw password value.</param>
        /// <returns>NONE for empty passwords, otherwise asterisks.</returns>
        private static string FormatNetworkPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "NONE";
            }

            return new string('*', Math.Min(27, password.Length));
        }

        /// <summary>
        /// Formats a client as address plus player name for the host client list.
        /// </summary>
        /// <param name="client">Client snapshot to format.</param>
        /// <returns>Overlay-friendly client display text.</returns>
        private static string FormatNetworkClientDisplay(C64NetClientSnapshot client)
        {
            if (client == null)
            {
                return "NONE";
            }

            string address = !string.IsNullOrWhiteSpace(client.RemoteAddress)
                ? client.RemoteAddress
                : (!string.IsNullOrWhiteSpace(client.RemoteEndpoint) ? client.RemoteEndpoint : "UNKNOWN");
            string name = string.IsNullOrWhiteSpace(client.Name) ? "player" : client.Name.Trim();
            return address + " - " + name;
        }

        /// <summary>
        /// Formats the current network mode for the overlay header.
        /// </summary>
        /// <param name="mode">Current network session mode.</param>
        /// <returns>LOCAL, HOST, or CLIENT.</returns>
        private static string FormatNetworkMode(NetworkSessionMode mode)
        {
            switch (mode)
            {
                case NetworkSessionMode.Host:
                    return "HOST";
                case NetworkSessionMode.Client:
                    return "CLIENT";
                default:
                    return "LOCAL";
            }
        }

        /// <summary>
        /// Formats the active network throughput footer for the network overlay.
        /// </summary>
        /// <returns>Short send/receive throughput string.</returns>
        private string FormatNetworkTrafficText()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "TLS SEND {0} KB/S  REC {1} KB/S  PING {2}",
                FormatNetworkRate(_networkSendKilobytesPerSecond),
                FormatNetworkRate(_networkReceiveKilobytesPerSecond),
                FormatNetworkLatency(GetNetworkDisplayLatencyMilliseconds()));
        }

        /// <summary>
        /// Gets the latency value that should be shown for the current network mode.
        /// </summary>
        /// <returns>Latency in milliseconds, or -1 when no sample is available.</returns>
        private int GetNetworkDisplayLatencyMilliseconds()
        {
            if (_networkMode == NetworkSessionMode.Client && _networkClient != null)
            {
                return _networkClient.LatencyMilliseconds;
            }

            if (_networkMode == NetworkSessionMode.Host && _networkServer != null && _networkServer.IsRunning)
            {
                return _networkServer.GetAverageLatencyMilliseconds();
            }

            return -1;
        }

        /// <summary>
        /// Formats the visible TLS certificate fingerprint line for the network overlay.
        /// </summary>
        /// <returns>Short TLS fingerprint text for host or client context.</returns>
        private string FormatNetworkFingerprintText()
        {
            if (_networkMode == NetworkSessionMode.Client && _networkClient != null)
            {
                if (_networkClient.TransportMode == C64NetTransportMode.Relay)
                {
                    return "RELAY FINGERPRINT " + _networkClient.RelayFingerprint + " E2E " + _networkClient.RelaySessionFingerprint;
                }

                return "TLS SERVER FINGERPRINT " + _networkClient.ServerCertificateFingerprint;
            }

            if (_networkMode == NetworkSessionMode.Host && _networkServer != null && _networkServer.IsRelayMode)
            {
                return "RELAY FINGERPRINT " + _networkServer.RelayFingerprint + " E2E ACTIVE";
            }

            if (_networkTransportMode == C64NetTransportMode.Relay)
            {
                return "RELAY TRUST FINGERPRINT " + C64NetTls.GetTrustedServerShortFingerprint(_networkHost, _networkRelayPort);
            }

            return "TLS HOST FINGERPRINT " + C64NetTls.GetServerCertificateShortFingerprint();
        }

        /// <summary>
        /// Formats a kilobytes-per-second value for the pixel overlay.
        /// </summary>
        /// <param name="kilobytesPerSecond">Throughput in 1024-byte kilobytes per second.</param>
        /// <returns>Compact one-decimal or integer rate string.</returns>
        private static string FormatNetworkRate(double kilobytesPerSecond)
        {
            if (kilobytesPerSecond < 0.05)
            {
                return "0.0";
            }

            if (kilobytesPerSecond < 100.0)
            {
                return kilobytesPerSecond.ToString("0.0", CultureInfo.InvariantCulture);
            }

            return kilobytesPerSecond.ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a latency value for title bars and overlay footer text.
        /// </summary>
        /// <param name="latencyMilliseconds">Latency in milliseconds, or -1 when unknown.</param>
        /// <returns>Overlay-friendly latency text.</returns>
        private static string FormatNetworkLatency(int latencyMilliseconds)
        {
            return latencyMilliseconds < 0
                ? "-- MS"
                : Math.Min(9999, latencyMilliseconds).ToString(CultureInfo.InvariantCulture) + " MS";
        }

        /// <summary>
        /// Formats a compact latency value for client-list rows.
        /// </summary>
        /// <param name="latencyMilliseconds">Latency in milliseconds, or -1 when unknown.</param>
        /// <returns>Short latency text.</returns>
        private static string FormatNetworkLatencyCompact(int latencyMilliseconds)
        {
            return latencyMilliseconds < 0
                ? "--MS"
                : Math.Min(9999, latencyMilliseconds).ToString(CultureInfo.InvariantCulture) + "MS";
        }

        /// <summary>
        /// Formats joystick permission for menus and title bars.
        /// </summary>
        /// <param name="permission">Permission value.</param>
        /// <returns>Overlay-friendly permission text.</returns>
        private static string FormatNetworkPermission(C64NetJoystickPermission permission)
        {
            switch (permission)
            {
                case C64NetJoystickPermission.Port1:
                    return "PORT 1";
                case C64NetJoystickPermission.Port2:
                    return "PORT 2";
                case C64NetJoystickPermission.Both:
                    return "BOTH";
                default:
                    return "OBSERVER";
            }
        }

        /// <summary>
        /// Formats the actual remote joystick right granted by the host.
        /// </summary>
        /// <param name="permission">Permission value.</param>
        /// <returns>Readable joystick-right label.</returns>
        private static string FormatNetworkJoystickRight(C64NetJoystickPermission permission)
        {
            switch (permission)
            {
                case C64NetJoystickPermission.Port1:
                    return "JOYSTICK 1";
                case C64NetJoystickPermission.Port2:
                    return "JOYSTICK 2";
                case C64NetJoystickPermission.Both:
                    return "BOTH JOYSTICKS";
                default:
                    return "NO JOYSTICK";
            }
        }

        /// <summary>
        /// Formats keyboard input rights for the host network menu.
        /// </summary>
        /// <param name="enabled">True when remote keyboard input reaches the host.</param>
        /// <returns>ON or OFF.</returns>
        private static string FormatNetworkKeyboardRight(bool enabled)
        {
            return enabled ? "ON" : "OFF";
        }

        /// <summary>
        /// Formats keyboard input rights for readable client-list style output.
        /// </summary>
        /// <param name="enabled">True when keyboard input is allowed.</param>
        /// <returns>Readable keyboard-right label.</returns>
        private static string FormatNetworkKeyboardRightLong(bool enabled)
        {
            return enabled ? "KEYBOARD" : "NO KEYBOARD";
        }

        private void DrawAudioOverlay(float sidMasterVolume, float sidNoiseLevel, SidChipModel sidChipModel, JoystickPort joystickPort, MountedMediaInfo mountedMediaInfo, bool enableLoadHack, bool forceSoftwareIecTransport, bool enableInputInjection)
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
            DrawOverlayText(overlayX + 16, overlayY + 32, "ENTER CHANGE  ESC BACK  F11 FULL", 1, 192, 210, 225);
            DrawAudioOverlayMenu(overlayX + 18, overlayY + 52, overlayWidth - 36, sidMasterVolume, sidNoiseLevel, sidChipModel, joystickPort, mountedMediaInfo, enableLoadHack, forceSoftwareIecTransport, enableInputInjection);
            DrawOverlayText(overlayX + 18, overlayY + 212, "MOUNTED " + FormatOverlayValue(mountedMediaInfo.DisplayName, 24), 1, 232, 238, 244);
            DrawOverlayText(overlayX + 18, overlayY + 222, "STATUS  " + FormatOverlayValue(_overlayStatusText, 24), 1, 182, 214, 108);

            if (_mediaBrowserVisible)
            {
                DrawMediaBrowserOverlay(overlayX + 12, overlayY + 44, overlayWidth - 24, 158);
            }

            if (_controllerMappingVisible)
            {
                DrawControllerMappingOverlay(overlayX + 12, overlayY + 44, overlayWidth - 24, 158);
            }

            if (_resetConfirmVisible)
            {
                DrawResetConfirmOverlay(overlayX + 42, overlayY + 80, 196, 84, mountedMediaInfo.HasMedia);
            }
        }

        /// <summary>
        /// Draws audio overlay menu.
        /// </summary>
        private void DrawAudioOverlayMenu(int x, int y, int width, float sidMasterVolume, float sidNoiseLevel, SidChipModel sidChipModel, JoystickPort joystickPort, MountedMediaInfo mountedMediaInfo, bool enableLoadHack, bool forceSoftwareIecTransport, bool enableInputInjection)
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
                DrawAudioOverlayMenuItem(menuIndex, x, itemY, sidMasterVolume, sidNoiseLevel, sidChipModel, joystickPort, mountedMediaInfo, enableLoadHack, forceSoftwareIecTransport, enableInputInjection);
            }

            if (AudioOverlayItemCount > AudioOverlayVisibleRows)
            {
                DrawAudioOverlayScrollBar(x + width - 6, y + 2, (AudioOverlayVisibleRows * AudioOverlayRowSpacing) - 8);
            }
        }

        /// <summary>
        /// Draws audio overlay menu item.
        /// </summary>
        private void DrawAudioOverlayMenuItem(int menuIndex, int x, int y, float sidMasterVolume, float sidNoiseLevel, SidChipModel sidChipModel, JoystickPort joystickPort, MountedMediaInfo mountedMediaInfo, bool enableLoadHack, bool forceSoftwareIecTransport, bool enableInputInjection)
        {
            bool enabled = CanAdjustAudioOverlayItem(menuIndex);
            switch (menuIndex)
            {
                case 0:
                    DrawOverlayItem(x, y, "MASTER VOLUME", sidMasterVolume / 1.5f, FormatHostVolume(sidMasterVolume), "LOW", "HIGH", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 1:
                    DrawOverlayItem(x, y, "NOISE LEVEL", sidNoiseLevel, FormatPercent(sidNoiseLevel), "SOFT", "HARSH", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 2:
                    DrawOverlayItem(x, y, "SID MODEL", sidChipModel == SidChipModel.Mos8580 ? 1.0f : 0.0f, sidChipModel == SidChipModel.Mos6581 ? "6581" : "8580", "6581", "8580", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 3:
                    if (_networkMode == NetworkSessionMode.Client && _networkClient != null)
                    {
                        // In a remote session the host grants joystick rights, so the
                        // settings menu shows the permission as read-only context.
                        DrawOverlayItem(x, y, "JOYSTICK", 0.0f, FormatNetworkPermission(_networkClient.Permission), "SERVER", "HOST", _audioOverlaySelection == menuIndex, false);
                    }
                    else
                    {
                        DrawOverlayItem(x, y, "JOYSTICK", GetJoystickPortFill(joystickPort), FormatJoystickPort(joystickPort), "PORT 1", "BOTH", _audioOverlaySelection == menuIndex, enabled);
                    }

                    break;
                case 4:
                    DrawOverlayItem(x, y, "KEYBOARD", GetHostKeyboardLayoutFill(_system.CurrentHostKeyboardLayout), FormatHostKeyboardLayout(_system.CurrentHostKeyboardLayout), "EN", "GER", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 5:
                    DrawOverlayItem(x, y, "1351 MOUSE", GetMouse1351PortFill(_mouse1351Port), FormatMouse1351Port(_mouse1351Port), "OFF", "PORT 2", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 6:
                    DrawOverlayItem(x, y, "DISPLAY", _windowFullscreen ? 1.0f : 0.0f, _windowFullscreen ? "FULLSCREEN" : "WINDOW", "WINDOW", "FULL", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 7:
                    DrawOverlayItem(x, y, "RENDER FPS", GetRenderFrameLimitFill(_renderFrameLimitMode), FormatRenderFrameLimit(_renderFrameLimitMode), "60 HZ", "UNLIMIT", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 8:
                    DrawOverlayItem(x, y, "VIDEO FILTER", GetVideoFilterFill(_videoFilterMode), FormatVideoFilter(_videoFilterMode), "SHARP", "TV", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 9:
                    DrawOverlayItem(x, y, "VIDEO UPSCALE", GetVideoUpscaleFill(_videoUpscaleMode), FormatVideoUpscale(_videoUpscaleMode), "NONE", "HQ4X", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 10:
                    DrawOverlayItem(x, y, "VIDEO ZOOM", _videoZoomEnabled ? 1.0f : 0.0f, _videoZoomEnabled ? "ON" : "OFF", "OFF", "ON", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 11:
                    DrawOverlayItem(x, y, "TURBO", _turboMode ? 1.0f : 0.0f, _turboMode ? "ON" : "OFF", "OFF", "MAX", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 12:
                    DrawOverlayItem(x, y, "GAMEPAD", GetGamepadFill(), FormatGamepadState(), "OFF", "ACTIVE", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 13:
                    DrawOverlayItem(x, y, "LOAD HACK", enableLoadHack ? 1.0f : 0.0f, enableLoadHack ? "ON" : "OFF", "OFF", "ON", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 14:
                    DrawOverlayItem(x, y, "IEC SOFTWARE", forceSoftwareIecTransport ? 1.0f : 0.0f, forceSoftwareIecTransport ? "ON" : "OFF", "OFF", "ON", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 15:
                    DrawOverlayItem(x, y, "INPUT INJECT", enableInputInjection ? 1.0f : 0.0f, enableInputInjection ? "ON" : "OFF", "OFF", "ON", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 16:
                    DrawOverlayItem(x, y, "RESET MODE", GetResetModeFill(_resetMode), FormatResetMode(_resetMode), "WARM", "POWER", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 17:
                    DrawOverlayItem(x, y, "DRIVE OVERLAY", _driveOverlayEnabled ? 1.0f : 0.0f, _driveOverlayEnabled ? "ON" : "OFF", "OFF", "ON", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 18:
                    DrawOverlayItem(x, y, "EASYFLASH", GetEasyFlashFill(), FormatEasyFlashState(), "OFF", "ON", _audioOverlaySelection == menuIndex, enabled && _system.IsEasyFlashInserted);
                    break;
                case 19:
                    DrawOverlayItem(x, y, "EF SAVE", _system.IsEasyFlashDirty ? 1.0f : 0.0f, FormatEasyFlashSaveState(), "CLEAN", "SAVE", _audioOverlaySelection == menuIndex, enabled && _system.IsEasyFlashInserted);
                    break;
                case 20:
                    DrawOverlayItem(x, y, "EF EJECT", _system.IsEasyFlashInserted ? 1.0f : 0.0f, FormatEasyFlashName(), "EMPTY", "EJECT", _audioOverlaySelection == menuIndex, enabled && _system.IsEasyFlashInserted);
                    break;
                case 21:
                    DrawOverlayItem(x, y, "REU", _system.ReuEnabled ? 1.0f : 0.0f, _system.ReuEnabled ? "ON" : "OFF", "OFF", "ON", _audioOverlaySelection == menuIndex, enabled);
                    break;
                case 22:
                    DrawOverlayItem(x, y, "REU SIZE", GetReuSizeFill(_system.ReuSize), FormatReuSize(_system.ReuSize), "128K", "16M", _audioOverlaySelection == menuIndex, enabled);
                    break;
            }
        }

        /// <summary>
        /// Draws the controller button/axis mapping submenu.
        /// </summary>
        private void DrawControllerMappingOverlay(int x, int y, int width, int height)
        {
            DrawFilledRectangleWithAlpha(x, y, width, height, 20, 24, 38, 242);
            DrawLine(x, y, x + width - 1, y, 255, 243, 168);
            DrawLine(x, y + height - 1, x + width - 1, y + height - 1, 255, 243, 168);
            DrawLine(x, y, x, y + height - 1, 255, 243, 168);
            DrawLine(x + width - 1, y, x + width - 1, y + height - 1, 255, 243, 168);

            DrawOverlayText(x + 10, y + 8, "CONTROLLER", 2, 255, 243, 168);
            DrawOverlayText(x + 10, y + 28, "ENTER ADD  BKSP CLEAR  DEL DEFAULT", 1, 192, 210, 225);

            ClampControllerMappingScroll();
            int rowY = y + 43;
            for (int row = 0; row < ControllerMappingVisibleRows; row++)
            {
                int index = _controllerMappingScroll + row;
                if (index >= ControllerMapActionCount)
                {
                    break;
                }

                ControllerMapAction action = (ControllerMapAction)index;
                bool selected = index == _controllerMappingSelection;
                byte labelRed = selected ? (byte)255 : (byte)210;
                byte labelGreen = selected ? (byte)243 : (byte)220;
                byte labelBlue = selected ? (byte)168 : (byte)210;
                byte valueRed = selected ? (byte)240 : (byte)192;
                byte valueGreen = selected ? (byte)248 : (byte)210;
                byte valueBlue = selected ? (byte)255 : (byte)225;

                string label = (selected ? "> " : "  ") + FormatControllerMapAction(action);
                string value = selected && _controllerMappingLearning
                    ? "PRESS CONTROL"
                    : FormatGamepadBindings(GetControllerBindings(action));

                int itemY = rowY + (row * ControllerMappingRowSpacing);
                DrawOverlayText(x + 10, itemY, label, 1, labelRed, labelGreen, labelBlue);
                DrawOverlayText(x + 100, itemY, FormatOverlayValue(value, 24), 1, valueRed, valueGreen, valueBlue);
            }

            if (ControllerMapActionCount > ControllerMappingVisibleRows)
            {
                DrawControllerMappingScrollBar(x + width - 8, rowY, (ControllerMappingVisibleRows * ControllerMappingRowSpacing) - 2);
            }

            DrawOverlayText(x + 10, y + height - 18, "GAMEPAD " + FormatGamepadState(), 1, 182, 214, 108);
        }

        /// <summary>
        /// Draws the scroll bar for the controller mapping submenu.
        /// </summary>
        private void DrawControllerMappingScrollBar(int x, int y, int height)
        {
            DrawFilledRectangle(x, y, 3, height, 36, 46, 62);

            int maxScroll = Math.Max(1, ControllerMapActionCount - ControllerMappingVisibleRows);
            int thumbHeight = Math.Max(18, (height * ControllerMappingVisibleRows) / ControllerMapActionCount);
            int thumbTravel = Math.Max(0, height - thumbHeight);
            int thumbY = y + ((_controllerMappingScroll * thumbTravel) / maxScroll);
            DrawFilledRectangle(x, thumbY, 3, thumbHeight, 182, 214, 108);
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
        /// Formats host keyboard layout.
        /// </summary>
        private static string FormatHostKeyboardLayout(HostKeyboardLayout layout)
        {
            return layout == HostKeyboardLayout.Ger ? "GER" : "EN";
        }

        /// <summary>
        /// Gets the host keyboard layout fill value.
        /// </summary>
        private static float GetHostKeyboardLayoutFill(HostKeyboardLayout layout)
        {
            return layout == HostKeyboardLayout.Ger ? 1.0f : 0.0f;
        }

        /// <summary>
        /// Formats the selected Commodore 1351 mouse port.
        /// </summary>
        private static string FormatMouse1351Port(Mouse1351Port mousePort)
        {
            switch (mousePort)
            {
                case Mouse1351Port.Port1:
                    return "PORT 1";
                case Mouse1351Port.Port2:
                    return "PORT 2";
                default:
                    return "OFF";
            }
        }

        /// <summary>
        /// Gets the settings slider fill value for the selected 1351 mouse port.
        /// </summary>
        private static float GetMouse1351PortFill(Mouse1351Port mousePort)
        {
            switch (mousePort)
            {
                case Mouse1351Port.Port1:
                    return 0.5f;
                case Mouse1351Port.Port2:
                    return 1.0f;
                default:
                    return 0.0f;
            }
        }

        /// <summary>
        /// Gets the next supported REU size.
        /// </summary>
        private static ReuMemorySize GetNextReuSize(ReuMemorySize size, int direction)
        {
            ReuMemorySize[] sizes = ReuExpansion.GetSupportedSizes();
            int index = Array.IndexOf(sizes, size);
            if (index < 0)
            {
                index = Array.IndexOf(sizes, ReuMemorySize.K512);
            }

            index += direction >= 0 ? 1 : -1;
            if (index < 0)
            {
                index = sizes.Length - 1;
            }

            if (index >= sizes.Length)
            {
                index = 0;
            }

            return sizes[index];
        }

        /// <summary>
        /// Formats a REU capacity for the settings overlay.
        /// </summary>
        private static string FormatReuSize(ReuMemorySize size)
        {
            return ReuExpansion.FormatSize(size).Replace(" ", string.Empty).ToUpperInvariant();
        }

        /// <summary>
        /// Gets the settings slider fill value for the REU capacity.
        /// </summary>
        private static float GetReuSizeFill(ReuMemorySize size)
        {
            ReuMemorySize[] sizes = ReuExpansion.GetSupportedSizes();
            int index = Array.IndexOf(sizes, size);
            if (index < 0)
            {
                index = Array.IndexOf(sizes, ReuMemorySize.K512);
            }

            return sizes.Length <= 1 ? 0.0f : index / (float)(sizes.Length - 1);
        }

        /// <summary>
        /// Formats the frontend render-loop frame cap.
        /// </summary>
        private static string FormatRenderFrameLimit(RenderFrameLimitMode mode)
        {
            switch (mode)
            {
                case RenderFrameLimitMode.Hz60:
                    return "60 HZ";
                case RenderFrameLimitMode.Hz120:
                    return "120 HZ";
                default:
                    return "UNLIMITED";
            }
        }

        /// <summary>
        /// Gets the settings slider fill value for the frontend render-loop frame cap.
        /// </summary>
        private static float GetRenderFrameLimitFill(RenderFrameLimitMode mode)
        {
            switch (mode)
            {
                case RenderFrameLimitMode.Hz60:
                    return 0.0f;
                case RenderFrameLimitMode.Hz120:
                    return 0.5f;
                default:
                    return 1.0f;
            }
        }

        /// <summary>
        /// Formats video filter mode.
        /// </summary>
        private static string FormatVideoFilter(VideoFilterMode mode)
        {
            switch (mode)
            {
                case VideoFilterMode.Crt:
                    return "CRT";
                case VideoFilterMode.Tv:
                    return "TV";
                default:
                    return "SHARP";
            }
        }

        /// <summary>
        /// Gets the video filter fill value.
        /// </summary>
        private static float GetVideoFilterFill(VideoFilterMode mode)
        {
            switch (mode)
            {
                case VideoFilterMode.Crt:
                    return 0.5f;
                case VideoFilterMode.Tv:
                    return 1.0f;
                default:
                    return 0.0f;
            }
        }

        /// <summary>
        /// Formats video upscaler mode.
        /// </summary>
        private static string FormatVideoUpscale(VideoUpscaleMode mode)
        {
            switch (mode)
            {
                case VideoUpscaleMode.Scale2x:
                    return "SCALE2X";
                case VideoUpscaleMode.Scale3x:
                    return "SCALE3X";
                case VideoUpscaleMode.Hq2x:
                    return "HQ2X";
                case VideoUpscaleMode.Hq3x:
                    return "HQ3X";
                case VideoUpscaleMode.Hq4x:
                    return "HQ4X";
                default:
                    return "NONE";
            }
        }

        /// <summary>
        /// Gets the video upscaler fill value.
        /// </summary>
        private static float GetVideoUpscaleFill(VideoUpscaleMode mode)
        {
            switch (mode)
            {
                case VideoUpscaleMode.Scale2x:
                    return 0.2f;
                case VideoUpscaleMode.Scale3x:
                    return 0.4f;
                case VideoUpscaleMode.Hq2x:
                    return 0.6f;
                case VideoUpscaleMode.Hq3x:
                    return 0.8f;
                case VideoUpscaleMode.Hq4x:
                    return 1.0f;
                default:
                    return 0.0f;
            }
        }

        /// <summary>
        /// Formats gamepad status.
        /// </summary>
        private string FormatGamepadState()
        {
            if (!_gamepadEnabled)
            {
                return "OFF";
            }

            return _gamepadConnected ? "ACTIVE" : "WAITING";
        }

        /// <summary>
        /// Gets the gamepad fill value.
        /// </summary>
        private float GetGamepadFill()
        {
            if (!_gamepadEnabled)
            {
                return 0.0f;
            }

            return _gamepadConnected ? 1.0f : 0.5f;
        }

        /// <summary>
        /// Formats the EasyFlash enabled state for the settings overlay.
        /// </summary>
        private string FormatEasyFlashState()
        {
            if (!_system.IsEasyFlashInserted)
            {
                return "NO CRT";
            }

            return _system.EasyFlashEnabled ? "ON" : "OFF";
        }

        /// <summary>
        /// Gets the EasyFlash enabled fill value.
        /// </summary>
        private float GetEasyFlashFill()
        {
            if (!_system.IsEasyFlashInserted)
            {
                return 0.0f;
            }

            return _system.EasyFlashEnabled ? 1.0f : 0.25f;
        }

        /// <summary>
        /// Formats the EasyFlash dirty/save state.
        /// </summary>
        private string FormatEasyFlashSaveState()
        {
            if (!_system.IsEasyFlashInserted)
            {
                return "NO CRT";
            }

            return _system.IsEasyFlashDirty ? "DIRTY" : "CLEAN";
        }

        /// <summary>
        /// Formats the inserted EasyFlash name.
        /// </summary>
        private string FormatEasyFlashName()
        {
            if (!_system.IsEasyFlashInserted)
            {
                return "NO CRT";
            }

            return FormatOverlayValue(_system.EasyFlashDisplayName, 12);
        }

        /// <summary>
        /// Formats reset mode.
        /// </summary>
        private static string FormatResetMode(ResetMode resetMode)
        {
            switch (resetMode)
            {
                case ResetMode.Reload:
                    return "RELOAD";
                case ResetMode.Power:
                    return "POWER";
                default:
                    return "WARM";
            }
        }

        /// <summary>
        /// Gets reset mode fill.
        /// </summary>
        private static float GetResetModeFill(ResetMode resetMode)
        {
            switch (resetMode)
            {
                case ResetMode.Reload:
                    return 0.5f;
                case ResetMode.Power:
                    return 1.0f;
                default:
                    return 0.0f;
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
        /// Formats a host file size for the compact media browser size column.
        /// </summary>
        private static string FormatMediaBrowserFileSize(long byteCount)
        {
            if (byteCount < 0)
            {
                return string.Empty;
            }

            if (byteCount < 1024)
            {
                return byteCount.ToString(CultureInfo.InvariantCulture) + "B";
            }

            long kibibytes = (byteCount + 1023) / 1024;
            if (kibibytes < 1024)
            {
                return kibibytes.ToString(CultureInfo.InvariantCulture) + "K";
            }

            double mebibytes = byteCount / 1048576.0;
            return mebibytes < 10.0
                ? mebibytes.ToString("0.0", CultureInfo.InvariantCulture) + "M"
                : mebibytes.ToString("0", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>
        /// Opens media browser.
        /// </summary>
        private void OpenMediaBrowser()
        {
            try
            {
                _mainMenuVisible = false;
                _audioOverlayVisible = false;
                _networkOverlayVisible = false;
                _saveOverlayVisible = false;
                _resetConfirmVisible = false;
                _mediaBrowserCurrentDirectory = ResolveInitialMediaBrowserDirectory();
                UpdateLastMediaBrowserDirectory(_mediaBrowserCurrentDirectory, false);
                _mediaBrowserSelection = 0;
                _mediaBrowserScroll = 0;
                _mediaBrowserLastJumpLetter = '\0';
                ReloadMediaBrowserEntries();
                _mediaBrowserVisible = true;
                _overlayStatusText = "BROWSER READY";
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                ReturnToMainMenuFromSubmenu();
                _overlayStatusText = "BROWSER FAILED";
            }
        }

        /// <summary>
        /// Mounts the first supported host media file dropped onto the window.
        /// </summary>
        private void MountDroppedMedia(string[] fileNames)
        {
            if (fileNames == null || fileNames.Length == 0)
            {
                return;
            }

            for (int index = 0; index < fileNames.Length; index++)
            {
                string path = fileNames[index];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                string extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".prg", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".d64", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".crt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _mediaBrowserVisible = false;
                _resetConfirmVisible = false;
                _saveOverlayVisible = false;
                int targetDrive = string.Equals(extension, ".d64", StringComparison.OrdinalIgnoreCase)
                    ? _mediaBrowserTargetDrive
                    : 8;
                UpdateLastMediaBrowserDirectory(Path.GetDirectoryName(path), true);
                _overlayStatusText = _system.MountMedia(path, targetDrive);
                SaveSettings();
                ResetEmulationTiming();
                ShowTurboToast(_overlayStatusText);
                return;
            }

            _overlayStatusText = "DROP PRG D64 OR CRT";
            ShowTurboToast(_overlayStatusText);
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
            ReturnToMainMenuFromSubmenu();
            _resetConfirmVisible = true;
            _resetConfirmYesSelected = true;
            _confirmationAction = confirmationAction;
            _overlayStatusText = "CONFIRM " + FormatResetMode(_resetMode);
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
                    PerformSelectedReset();
                    break;
                default:
                    _overlayStatusText = "NO ACTION";
                    break;
            }
        }

        /// <summary>
        /// Executes the currently selected reset behavior.
        /// </summary>
        private void PerformSelectedReset()
        {
            switch (_resetMode)
            {
                case ResetMode.Power:
                    PerformSystemReset();
                    break;
                case ResetMode.Reload:
                    _overlayStatusText = PerformInPlaceSystemReset(_system.ResetAndReloadMedia);
                    break;
                default:
                    _overlayStatusText = PerformInPlaceSystemReset(_system.WarmReset);
                    break;
            }
        }

        /// <summary>
        /// Runs an in-place reset while preserving host-facing settings.
        /// </summary>
        private string PerformInPlaceSystemReset(Func<string> resetAction)
        {
            float sidMasterVolume = _system.SidMasterVolume;
            float sidNoiseLevel = _system.SidNoiseLevel;
            SidChipModel sidChipModel = _system.CurrentSidChipModel;
            JoystickPort joystickPort = _system.CurrentJoystickPort;
            bool enableKernalIecHooks = _system.EnableKernalIecHooks;
            bool enableLoadHack = _system.EnableLoadHack;
            bool forceSoftwareIecTransport = _system.ForceSoftwareIecTransport;
            bool enableInputInjection = _system.EnableInputInjection;

            string statusText = resetAction();
            _system.SetSidMasterVolume(sidMasterVolume);
            _system.SetSidNoiseLevel(sidNoiseLevel);
            _system.SetSidChipModel(sidChipModel);
            _system.SetJoystickPort(joystickPort);
            _system.EnableKernalIecHooks = enableKernalIecHooks;
            _system.EnableLoadHack = enableLoadHack;
            _system.ForceSoftwareIecTransport = forceSoftwareIecTransport;
            _system.EnableInputInjection = enableInputInjection;
            ApplyMouse1351StateToSystem();
            _lastGamepadJoystickState = 0xFF;
            _turboTimingResetPending = true;
            return statusText;
        }

        /// <summary>
        /// Handles the perform system reset operation.
        /// </summary>
        private void PerformSystemReset()
        {
            StopEmulation();

            MountedMediaInfo mountedMedia = _system.MountedMedia;
            Dictionary<int, string> mountedDrivePaths = _system.GetMountedDriveHostPaths();
            EasyFlashCartridge easyFlashSnapshot = _system.CreateEasyFlashSnapshot();
            float sidMasterVolume = _system.SidMasterVolume;
            float sidNoiseLevel = _system.SidNoiseLevel;
            SidChipModel sidChipModel = _system.CurrentSidChipModel;
            JoystickPort joystickPort = _system.CurrentJoystickPort;
            bool enableKernalIecHooks = _system.EnableKernalIecHooks;
            bool enableLoadHack = _system.EnableLoadHack;
            bool forceSoftwareIecTransport = _system.ForceSoftwareIecTransport;
            bool enableInputInjection = _system.EnableInputInjection;
            ReuExpansion reuSnapshot = _system.CreateReuSnapshot();

            C64System oldSystem = _system;
            oldSystem.Dispose();
            CreateSystemInstance();
            _system.SetSidMasterVolume(sidMasterVolume);
            _system.SetSidNoiseLevel(sidNoiseLevel);
            _system.SetSidChipModel(sidChipModel);
            _system.SetJoystickPort(joystickPort);
            _system.EnableKernalIecHooks = enableKernalIecHooks;
            _system.EnableLoadHack = enableLoadHack;
            _system.ForceSoftwareIecTransport = forceSoftwareIecTransport;
            _system.EnableInputInjection = enableInputInjection;
            ApplyMouse1351StateToSystem();

            _turboMode = false;
            _turboTimingResetPending = false;
            _smoothedMhz = 0.0;
            _driveFooterVisibility = 0.0;
            _driveFooterIdleSeconds = 0.0;
            _driveFooterPulsePhase = 0.0;
            _turboToastSecondsRemaining = 0.0;
            _lastGamepadJoystickState = 0xFF;
            if (easyFlashSnapshot != null)
            {
                _system.InsertEasyFlashSnapshot(easyFlashSnapshot);
                mountedMedia = _system.MountedMedia;
            }

            if (reuSnapshot != null)
            {
                _system.InsertReuSnapshot(reuSnapshot);
            }

            SaveSettings();
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
            else if (mountedMedia != null &&
                mountedMedia.Kind == MountedMediaKind.EasyFlash &&
                _system.IsEasyFlashInserted)
            {
                statusText = "RESET EASYFLASH READY";
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

            string lastDirectory = NormalizeExistingDirectory(_mediaBrowserLastDirectory);
            if (!string.IsNullOrWhiteSpace(lastDirectory))
            {
                return lastDirectory;
            }

            string mediaDirectory = EnsureUserMediaDirectory();
            if (!string.IsNullOrWhiteSpace(mediaDirectory))
            {
                return mediaDirectory;
            }

            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsDirectory) && Directory.Exists(documentsDirectory))
            {
                return documentsDirectory;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// Ensures and returns the standard user media directory.
        /// </summary>
        private static string EnsureUserMediaDirectory()
        {
            try
            {
                string mediaDirectory = UserDataPaths.GetMediaDirectory();
                if (!string.IsNullOrWhiteSpace(mediaDirectory))
                {
                    Directory.CreateDirectory(mediaDirectory);
                    if (Directory.Exists(mediaDirectory))
                    {
                        return mediaDirectory;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return null;
        }

        /// <summary>
        /// Returns an existing directory path or null.
        /// </summary>
        private static string NormalizeExistingDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return null;
        }

        /// <summary>
        /// Stores the last media browser directory without storing mounted media files.
        /// </summary>
        private void UpdateLastMediaBrowserDirectory(string path, bool save)
        {
            string directory = NormalizeExistingDirectory(path);
            if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory, _mediaBrowserLastDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _mediaBrowserLastDirectory = directory;
            if (save)
            {
                SaveSettings();
            }
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
                        entries.Add(new MediaBrowserEntry(parent, "UP", true, true, string.Empty, "UP"));
                    }

                    string[] directories = Directory.GetDirectories(_mediaBrowserCurrentDirectory, "*", SearchOption.TopDirectoryOnly);
                    Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                    foreach (string directory in directories)
                    {
                        entries.Add(new MediaBrowserEntry(directory, "DIR " + Path.GetFileName(directory), true, false, string.Empty, Path.GetFileName(directory)));
                    }

                    var files = new List<string>();
                    files.AddRange(Directory.GetFiles(_mediaBrowserCurrentDirectory, "*.prg", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(_mediaBrowserCurrentDirectory, "*.d64", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(_mediaBrowserCurrentDirectory, "*.crt", SearchOption.TopDirectoryOnly));
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in files)
                    {
                        string extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                        string fileName = Path.GetFileName(file).ToUpperInvariant();
                        entries.Add(new MediaBrowserEntry(file, extension + " " + fileName, false, false, FormatMediaBrowserFileSize(new FileInfo(file).Length), Path.GetFileNameWithoutExtension(file)));
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
                _mediaBrowserEntries.Add(new MediaBrowserEntry(string.Empty, "NO PRG D64 OR CRT FILES", false, false));
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
        /// Jumps the media browser selection to entries starting with the given letter.
        /// </summary>
        /// <param name="letter">Uppercase target letter.</param>
        private void JumpMediaBrowserToLetter(char letter)
        {
            if (_mediaBrowserEntries.Count == 0)
            {
                return;
            }

            char upperLetter = char.ToUpperInvariant(letter);
            int startIndex = _mediaBrowserLastJumpLetter == upperLetter
                ? _mediaBrowserSelection + 1
                : 0;

            for (int offset = 0; offset < _mediaBrowserEntries.Count; offset++)
            {
                int index = (startIndex + offset) % _mediaBrowserEntries.Count;
                if (!MediaBrowserEntryStartsWith(_mediaBrowserEntries[index], upperLetter))
                {
                    continue;
                }

                _mediaBrowserSelection = index;
                _mediaBrowserLastJumpLetter = upperLetter;
                ClampMediaBrowserScroll();
                return;
            }
        }

        /// <summary>
        /// Maps media-browser jump keys to letters.
        /// </summary>
        private static bool TryGetMediaBrowserJumpLetter(Key key, out char letter)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                letter = (char)('A' + ((int)key - (int)Key.A));
                return true;
            }

            letter = '\0';
            return false;
        }

        /// <summary>
        /// Tests whether a media browser entry starts with the requested letter.
        /// </summary>
        private static bool MediaBrowserEntryStartsWith(MediaBrowserEntry entry, char letter)
        {
            if (entry == null)
            {
                return false;
            }

            string searchName = string.IsNullOrWhiteSpace(entry.SearchName)
                ? entry.DisplayName
                : entry.SearchName;
            searchName = searchName == null ? string.Empty : searchName.TrimStart();
            return searchName.Length > 0 && char.ToUpperInvariant(searchName[0]) == letter;
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
                UpdateLastMediaBrowserDirectory(_mediaBrowserCurrentDirectory, true);
                _mediaBrowserSelection = 0;
                _mediaBrowserScroll = 0;
                _mediaBrowserLastJumpLetter = '\0';
                ReloadMediaBrowserEntries();
                return;
            }

            UpdateLastMediaBrowserDirectory(Path.GetDirectoryName(entry.Path), true);
            string statusText = _system.MountMedia(entry.Path, _mediaBrowserTargetDrive);
            SaveSettings();
            ResetEmulationTiming();
            ReturnToMainMenuFromSubmenu();
            _overlayStatusText = statusText;
        }

        /// <summary>
        /// Handles the clamp media browser scroll operation.
        /// </summary>
        private void ClampMediaBrowserScroll()
        {
            ClampMediaBrowserScroll(GetStandaloneMediaBrowserVisibleRows());
        }

        /// <summary>
        /// Handles the clamp media browser scroll operation.
        /// </summary>
        /// <param name="visibleRows">Number of rows visible in the current media browser box.</param>
        private void ClampMediaBrowserScroll(int visibleRows)
        {
            if (_mediaBrowserSelection < _mediaBrowserScroll)
            {
                _mediaBrowserScroll = _mediaBrowserSelection;
            }

            visibleRows = Math.Max(1, visibleRows);
            int maxVisibleStart = Math.Max(0, _mediaBrowserEntries.Count - visibleRows);
            int visibleEnd = _mediaBrowserScroll + visibleRows - 1;
            if (_mediaBrowserSelection > visibleEnd)
            {
                _mediaBrowserScroll = _mediaBrowserSelection - (visibleRows - 1);
            }

            if (_mediaBrowserScroll > maxVisibleStart)
            {
                _mediaBrowserScroll = maxVisibleStart;
            }

            if (_mediaBrowserScroll < 0)
            {
                _mediaBrowserScroll = 0;
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
                case Key.PageUp:
                case Key.PageDown:
                case Key.Left:
                case Key.Right:
                case Key.Enter:
                case Key.Escape:
                case Key.Number0:
                case Key.Number1:
                case Key.Number8:
                case Key.Number9:
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Draws the media browser as a direct F10 main-menu submenu.
        /// </summary>
        private void DrawStandaloneMediaBrowserOverlay()
        {
            int overlayWidth = Math.Min(PixelsWidth - 12, 376);
            int overlayHeight = Math.Min(PixelsHeight - 12, 214);
            int overlayX = (PixelsWidth - overlayWidth) / 2;
            int overlayY = (PixelsHeight - overlayHeight) / 2;
            DrawMediaBrowserOverlay(overlayX, overlayY, overlayWidth, overlayHeight);
        }

        /// <summary>
        /// Gets the current visible media browser row count for standalone layout.
        /// </summary>
        /// <returns>Number of rows that fit in the media browser list area.</returns>
        private int GetStandaloneMediaBrowserVisibleRows()
        {
            int overlayHeight = Math.Min(PixelsHeight - 12, 214);
            return GetMediaBrowserVisibleRows(overlayHeight);
        }

        /// <summary>
        /// Gets the visible media browser row count for a given box height.
        /// </summary>
        /// <param name="height">Media browser overlay height.</param>
        /// <returns>Number of rows that fit in the list area.</returns>
        private static int GetMediaBrowserVisibleRows(int height)
        {
            int listHeight = height - MediaBrowserListTopOffset - MediaBrowserListBottomPadding;
            return Math.Max(1, ((Math.Max(7, listHeight) - 7) / MediaBrowserRowSpacing) + 1);
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
            DrawOverlayText(x + 10, y + 30, "ESC MAIN MENU  DRIVE " + _mediaBrowserTargetDrive, 1, 182, 214, 108);

            int rowY = y + MediaBrowserListTopOffset;
            int visibleRows = GetMediaBrowserVisibleRows(height);
            ClampMediaBrowserScroll(visibleRows);
            int scrollBarX = x + width - 10;
            int sizeRightX = scrollBarX - 7;
            int sizeColumnLeft = sizeRightX - 48;
            int listX = x + 10;
            int nameMaxCharacters = Math.Max(8, ((sizeColumnLeft - listX) / 6) - 1);
            int pathMaxCharacters = Math.Max(16, ((sizeColumnLeft - listX) / 6) - 5);
            DrawOverlayText(listX, y + 42, "DIR " + FormatOverlayPath(_mediaBrowserCurrentDirectory, pathMaxCharacters), 1, 182, 214, 108);
            DrawOverlayTextRightAligned(sizeRightX, y + 42, "SIZE", 1, 182, 214, 108);

            for (int row = 0; row < visibleRows; row++)
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
                int textY = rowY + (row * MediaBrowserRowSpacing);
                DrawOverlayText(listX, textY, (selected ? "> " : "  ") + FormatOverlayValue(entry.DisplayName, nameMaxCharacters), 1, red, green, blue);
                if (!string.IsNullOrWhiteSpace(entry.SizeText))
                {
                    DrawOverlayTextRightAligned(sizeRightX, textY, FormatOverlayValue(entry.SizeText, 8), 1, red, green, blue);
                }
            }

            DrawMediaBrowserScrollBar(scrollBarX, rowY, visibleRows, height);
        }

        /// <summary>
        /// Draws the media browser scroll bar.
        /// </summary>
        /// <param name="x">Scroll bar x coordinate.</param>
        /// <param name="y">Scroll bar y coordinate.</param>
        /// <param name="visibleRows">Visible media browser row count.</param>
        /// <param name="height">Media browser overlay height.</param>
        private void DrawMediaBrowserScrollBar(int x, int y, int visibleRows, int height)
        {
            int trackHeight = Math.Max(8, height - MediaBrowserListTopOffset - MediaBrowserListBottomPadding);
            DrawFilledRectangle(x, y, 2, trackHeight, 38, 46, 70);

            int entryCount = Math.Max(1, _mediaBrowserEntries.Count);
            int thumbHeight = entryCount <= visibleRows
                ? trackHeight
                : Math.Max(8, (trackHeight * visibleRows) / entryCount);
            int maxScroll = Math.Max(1, entryCount - visibleRows);
            int thumbY = entryCount <= visibleRows
                ? y
                : y + (((trackHeight - thumbHeight) * _mediaBrowserScroll) / maxScroll);
            DrawFilledRectangle(x - 1, thumbY, 4, thumbHeight, 182, 214, 108);
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
            title = "RESET " + FormatResetMode(_resetMode);
            if (_resetMode == ResetMode.Warm)
            {
                infoLine1 = "RAM AND MEDIA STAY";
                infoLine2 = "CPU RESTARTS";
            }
            else if (_resetMode == ResetMode.Reload)
            {
                infoLine1 = hasMountedMedia ? "MOUNTED MEDIA RELOADS" : "NO MEDIA INSERTED";
                infoLine2 = "MACHINE RESTARTS";
            }
            else
            {
                infoLine1 = hasMountedMedia ? "MEDIA REMOUNTS" : "NO MEDIA INSERTED";
                infoLine2 = "POWER CYCLE";
            }

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
        private void DrawOverlayItem(int x, int y, string label, float normalizedValue, string valueText, string minimumLabel, string maximumLabel, bool selected, bool enabled = true)
        {
            byte labelRed = !enabled ? (byte)105 : (selected ? (byte)255 : (byte)210);
            byte labelGreen = !enabled ? (byte)112 : (selected ? (byte)243 : (byte)220);
            byte labelBlue = !enabled ? (byte)122 : (selected ? (byte)168 : (byte)210);
            byte valueRed = enabled ? (byte)240 : (byte)130;
            byte valueGreen = enabled ? (byte)248 : (byte)136;
            byte valueBlue = enabled ? (byte)255 : (byte)146;
            byte barRed = !enabled ? (byte)76 : (selected ? (byte)182 : (byte)110);
            byte barGreen = !enabled ? (byte)84 : (selected ? (byte)214 : (byte)130);
            byte barBlue = !enabled ? (byte)98 : (selected ? (byte)108 : (byte)160);

            DrawOverlayText(x, y, (selected ? "> " : "  ") + label, 2, labelRed, labelGreen, labelBlue);
            DrawOverlayText(x + 192, y, valueText, 2, valueRed, valueGreen, valueBlue);

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
        private void DrawOverlayActionItem(int x, int y, string label, string valueText, bool selected, bool enabled = true)
        {
            byte labelRed = !enabled ? (byte)105 : (selected ? (byte)255 : (byte)210);
            byte labelGreen = !enabled ? (byte)112 : (selected ? (byte)243 : (byte)220);
            byte labelBlue = !enabled ? (byte)122 : (selected ? (byte)168 : (byte)210);
            byte valueRed = enabled ? (byte)240 : (byte)130;
            byte valueGreen = enabled ? (byte)248 : (byte)136;
            byte valueBlue = enabled ? (byte)255 : (byte)146;

            DrawOverlayText(x, y, (selected ? "> " : "  ") + label, 2, labelRed, labelGreen, labelBlue);
            DrawOverlayText(x + 150, y, valueText, 2, valueRed, valueGreen, valueBlue);
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
        /// Draws overlay text with its right edge aligned to the given x coordinate.
        /// </summary>
        private void DrawOverlayTextRightAligned(int rightX, int y, string text, int scale, byte red, byte green, byte blue)
        {
            DrawOverlayText(rightX - GetOverlayTextWidth(text ?? string.Empty, scale), y, text ?? string.Empty, scale, red, green, blue);
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
            public MediaBrowserEntry(string path, string displayName, bool isDirectory, bool isParent, string sizeText = "", string searchName = "")
            {
                Path = path;
                DisplayName = displayName ?? string.Empty;
                IsDirectory = isDirectory;
                IsParent = isParent;
                SizeText = sizeText ?? string.Empty;
                SearchName = string.IsNullOrWhiteSpace(searchName) ? DisplayName : searchName;
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
            /// Gets the user-facing file size text.
            /// </summary>
            public string SizeText { get; }

            /// <summary>
            /// Gets the name used for first-letter jumps.
            /// </summary>
            public string SearchName { get; }

            /// <summary>
            /// Gets whether the entry represents a directory.
            /// </summary>
            public bool IsDirectory { get; }

            /// <summary>
            /// Gets whether the entry navigates to the parent directory.
            /// </summary>
            public bool IsParent { get; }
        }

        /// <summary>
        /// Represents a savestate browser entry component.
        /// </summary>
        private sealed class SaveOverlayEntry
        {
            /// <summary>
            /// Initializes a new SaveOverlayEntry instance.
            /// </summary>
            public SaveOverlayEntry(string path, string displayName, bool isDirectory, bool isParent, SaveStateMetadata metadata)
            {
                Path = path;
                DisplayName = displayName ?? string.Empty;
                IsDirectory = isDirectory;
                IsParent = isParent;
                Metadata = metadata;
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

            /// <summary>
            /// Gets the savestate metadata when this entry represents a save.
            /// </summary>
            public SaveStateMetadata Metadata { get; }
        }
    }
}
