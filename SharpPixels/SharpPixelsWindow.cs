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
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpPixels.Shaders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Key = OpenTK.Input.Key;
using KeyModifiers = OpenTK.Input.KeyModifiers;
using MouseState = OpenTK.Input.MouseState;

namespace SharpPixels
{
    /// <summary>
    /// Applies Windows scheduling hints for realtime rendering windows.
    /// </summary>
    internal static class WindowsRenderScheduling
    {
        private const int ProcessPowerThrottling = 4;
        private const int ThreadPowerThrottling = 3;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint ThreadPowerThrottlingCurrentVersion = 1;
        private const uint PowerThrottlingExecutionSpeed = 0x1;
        private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

        private static readonly object SyncRoot = new object();
        private static bool _processApplied;

        /// <summary>
        /// Applies all available process and render-thread scheduling hints for the
        /// calling thread.
        /// </summary>
        public static void Apply()
        {
            ApplyProcessHints();
            ApplyCurrentThreadHints();
        }

        /// <summary>
        /// Tells Windows not to reduce this process' execution speed or ignore timer
        /// resolution requests while it is in the background. Some systems otherwise
        /// lower unfocused OpenGL windows to a much smaller frame cadence, which is
        /// visible for remote C64 clients.
        /// </summary>
        private static void ApplyProcessHints()
        {
            if (_processApplied)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (_processApplied)
                {
                    return;
                }

                DisableProcessThrottling();
                _processApplied = true;
            }
        }

        /// <summary>
        /// Raises the current render thread above the default scheduler class and
        /// opts it out of EcoQoS execution-speed throttling.
        /// </summary>
        private static void ApplyCurrentThreadHints()
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch (ThreadStateException)
            {
                // If the runtime refuses the priority change, the Win32 QoS hint below
                // can still help on supported Windows versions.
            }

            DisableCurrentThreadThrottling();
        }

        /// <summary>
        /// Disables process-level throttling flags when the host Windows version
        /// supports them.
        /// </summary>
        private static void DisableProcessThrottling()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            if (TrySetProcessPowerThrottling(
                PowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution,
                0))
            {
                return;
            }

            TrySetProcessPowerThrottling(PowerThrottlingExecutionSpeed, 0);
        }

        /// <summary>
        /// Applies the given process-level power-throttling mask.
        /// </summary>
        /// <param name="controlMask">Power-throttling mechanisms controlled by the caller.</param>
        /// <param name="stateMask">Power-throttling mechanisms that should be enabled.</param>
        /// <returns>True if Windows accepted the setting.</returns>
        private static bool TrySetProcessPowerThrottling(uint controlMask, uint stateMask)
        {
            var throttlingState = new PowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = controlMask,
                StateMask = stateMask
            };

            try
            {
                return SetProcessInformation(
                    Process.GetCurrentProcess().Handle,
                    ProcessPowerThrottling,
                    ref throttlingState,
                    Marshal.SizeOf<PowerThrottlingState>());
            }
            catch (EntryPointNotFoundException)
            {
                // Windows versions without SetProcessInformation keep their default scheduling.
                return false;
            }
            catch (DllNotFoundException)
            {
                // Non-standard runtimes without kernel32.dll keep their default scheduling.
                return false;
            }
        }

        /// <summary>
        /// Disables thread-level EcoQoS execution-speed throttling when available.
        /// </summary>
        private static void DisableCurrentThreadThrottling()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var throttlingState = new PowerThrottlingState
            {
                Version = ThreadPowerThrottlingCurrentVersion,
                ControlMask = PowerThrottlingExecutionSpeed,
                StateMask = 0
            };

            try
            {
                SetThreadInformation(
                    GetCurrentThread(),
                    ThreadPowerThrottling,
                    ref throttlingState,
                    Marshal.SizeOf<PowerThrottlingState>());
            }
            catch (EntryPointNotFoundException)
            {
                // Windows versions without SetThreadInformation keep their default scheduling.
            }
            catch (DllNotFoundException)
            {
                // Non-standard runtimes without kernel32.dll keep their default scheduling.
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr process,
            int processInformationClass,
            ref PowerThrottlingState processInformation,
            int processInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadInformation(
            IntPtr thread,
            int threadInformationClass,
            ref PowerThrottlingState threadInformation,
            int threadInformationSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }
    }

    /// <summary>
    /// We use a BitmapMemory to "memorize" the bitmap pointer to the bitmap's pixel data
    /// Also, we lock the bitmap once, thus, this does not cost performance during each cycle if we have hundreds of bitmaps
    /// </summary>
    internal unsafe class BitmapMemory
    {
        /// <summary>
        /// Gets the cached bitmap.
        /// </summary>
        public Bitmap Bitmap { get; private set; }
        /// <summary>
        /// Gets the pinned pointer to the cached bitmap pixel data.
        /// </summary>
        public byte* BitmapPointer { get; private set; }
        /// <summary>
        /// Gets or sets when the cached bitmap data expires.
        /// </summary>
        public DateTime ExpirationTime { get; set; }

        private BitmapData _bitmapData;

        /// <summary>
        /// Creates a new BitmapMemory for the given bitmap
        /// </summary>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        public BitmapMemory(Bitmap bitmap)
        {
            Bitmap = bitmap;
            _bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            BitmapPointer = (byte*)_bitmapData.Scan0.ToPointer();
        }

        /// <summary>
        /// Unlock the bits
        /// </summary>
        public void Dispose()
        {
            Bitmap.UnlockBits(_bitmapData);
        }
    }

    /// <summary>
    /// Selects the GPU-side source presentation filter used by the emulator fast path.
    /// </summary>
    public enum SharpPixelsGpuFilterMode
    {
        Sharp = 0,
        Crt = 1,
        Tv = 2
    }

    /// <summary>
    /// Selects the optional GPU-side source upscaler applied before the presentation filter.
    /// </summary>
    public enum SharpPixelsGpuUpscaleMode
    {
        None = 0,
        Scale2x = 1,
        Scale3x = 2,
        Hq2x = 3,
        Hq3x = 4,
        Hq4x = 5,
        Xbrz = 6,
        Clean4 = 7
    }

    /// <summary>
    /// Engine that allows to easily draw pixels on the screen
    /// </summary>
    public abstract unsafe class SharpPixelsWindow : GameWindow, IDisposable
    {
        /// <summary>
        /// Object for storing draw text calls
        /// </summary>
        private struct DrawTextObject
        {
            public int X;
            public int Y;
            public string Text;
            public int Fontsize;
            public byte Red;
            public byte Green;
            public byte Blue;
            public Font Font;
        }

        /// <summary>
        /// This stopwatch is used to determine the drawing time of each frame
        /// </summary>
        private readonly Stopwatch _drawingStopwatch = new Stopwatch();      
        /// <summary>
        /// Start time of the current FPS measuring window in stopwatch milliseconds.
        /// </summary>
        private long _fpsWindowStartMilliseconds;
        /// <summary>
        /// Counter for fps
        /// </summary>
        private int _fpsCounter = 0;
        /// <summary>
        /// Queue for DrawText objects; theses texts are drawn at end of frame
        /// </summary>
        private Queue<DrawTextObject> _drawTexts = new Queue<DrawTextObject>();
        /// <summary>
        /// This dictionary stores pointer to the raw bitmap data
        /// </summary>
        private Dictionary<Bitmap, BitmapMemory> _bitmapMemories = new Dictionary<Bitmap, BitmapMemory>();
        /// <summary>
        /// Table for fast alpha blending computation
        /// </summary>
        private static readonly byte[,] _alphaMulTable = new byte[256, 256];
        private static readonly byte[,] _oneMinusAlphaMulTable = new byte[256, 256];

        private DateTime nextKeyPressTime = DateTime.Now;
        private readonly HashSet<Key> keysPressed = new HashSet<Key>();
        private KeyModifiers modifiersPressed;


        private int _vertexArrayObject;
        private int _vertexBufferObject;
        private int _textureHandle;
        private int _sourceTextureHandle;
        private int _texCoordLocation;
        private int _textureUniformLocation;
        private int _filterModeUniformLocation;
        private int _upscaleModeUniformLocation;
        private int _upscaleFactorUniformLocation;
        private int _cropOriginUniformLocation;
        private int _cropSizeUniformLocation;
        private int _sourceTextureSizeUniformLocation;
        private Shader _shader;
        private byte[] _pixels;
        private readonly List<Bitmap> _expiredBitmapScratch = new List<Bitmap>();
        private readonly int _pixelByteCount;
        private readonly int _pixelStrideBytes;
        private readonly GCHandle _pixelBufferHandle;
        private readonly IntPtr _pixelBufferPointer;
        private int[] _stretchSourceXMap;
        private int[] _stretchSourceYMap;
        private int _stretchMapSourceWidth;
        private int _stretchMapSourceHeight;
        private int _stretchMapTargetWidth;
        private int _stretchMapTargetHeight;
        private int _sourceTextureWidth;
        private int _sourceTextureHeight;
        private uint[] _gpuSourcePixels;
        private int _gpuSourceWidth;
        private int _gpuSourceHeight;
        private int _gpuSourceCropX;
        private int _gpuSourceCropY;
        private int _gpuSourceCropWidth;
        private int _gpuSourceCropHeight;
        private int _gpuDestinationX;
        private int _gpuDestinationY;
        private int _gpuDestinationWidth;
        private int _gpuDestinationHeight;
        private byte _gpuBorderRed;
        private byte _gpuBorderGreen;
        private byte _gpuBorderBlue;
        private SharpPixelsGpuFilterMode _gpuFilterMode;
        private SharpPixelsGpuUpscaleMode _gpuUpscaleMode;
        private int _gpuUpscaleFactor = 1;
        private bool _gpuSourcePresentationPending;
        private bool _presentationVSyncEnabled;
        private bool _overlayLayerMode;
        private bool _overlayTextureIsTransparent;
        private bool _overlayUploadDirty;
        private bool _overlayCurrentDirty;
        private bool _overlayPreviousDirty;
        private int _overlayUploadMinX;
        private int _overlayUploadMinY;
        private int _overlayUploadMaxX;
        private int _overlayUploadMaxY;
        private int _overlayCurrentMinX;
        private int _overlayCurrentMinY;
        private int _overlayCurrentMaxX;
        private int _overlayCurrentMaxY;
        private int _overlayPreviousMinX;
        private int _overlayPreviousMinY;
        private int _overlayPreviousMaxX;
        private int _overlayPreviousMaxY;
        private Vector2i _windowedClientSize;
        private Vector2i _windowedLocation;
        private WindowState _windowedState = WindowState.Normal;
        private WindowBorder _windowedBorder = WindowBorder.Resizable;
        private bool _hasWindowedBounds;

        private readonly float[] vertices =
        {
                -1f, -1f, 0.0f, 0.0f, 1.0f,
                1f, -1f, 0.0f, 1.0f, 1.0f,
                -1f,  1f, 0.0f, 0.0f, 0.0f,

                -1f,  1f, 0.0f, 0.0f, 0.0f,
                1f, -1f, 0.0f, 1.0f, 1.0f,
                1f,  1f, 0.0f, 1.0f, 0.0f,
        };

        public int PixelsWidth
        {
            get;
        }

        public int PixelsHeight
        {
            get;
        }

        private bool _isFullscreen = false;

        /// <summary>
        /// Handles the sharp pixels window operation.
        /// </summary>
        public SharpPixelsWindow(int width, int height, string title)
            : base(
                CreateGameWindowSettings(),
                CreateNativeWindowSettings(width, height, title))
        {
            PixelsWidth = width;
            PixelsHeight = height;
            _pixelStrideBytes = PixelsWidth << 2;
            _pixelByteCount = PixelsHeight * _pixelStrideBytes;
            _pixels = new byte[_pixelByteCount];
            _pixelBufferHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            _pixelBufferPointer = _pixelBufferHandle.AddrOfPinnedObject();
            ClearScreen();
            UpdateTextureCoordinates();
        }       

        /// <summary>
        /// Creates the OpenTK game-loop settings used by all SharpPixels windows.
        /// </summary>
        /// <returns>Game-loop settings configured for continuous realtime rendering.</returns>
        private static GameWindowSettings CreateGameWindowSettings()
        {
            return new GameWindowSettings
            {
                UpdateFrequency = 0.0,
                Win32SuspendTimerOnDrag = false
            };
        }

        /// <summary>
        /// Creates the native OpenTK window settings used by all SharpPixels windows.
        /// </summary>
        /// <param name="width">Initial client width.</param>
        /// <param name="height">Initial client height.</param>
        /// <param name="title">Initial window title.</param>
        /// <returns>Native window settings configured for OpenGL rendering.</returns>
        private static NativeWindowSettings CreateNativeWindowSettings(int width, int height, string title)
        {
            return new NativeWindowSettings
            {
                ClientSize = new Vector2i(width, height),
                Title = title,
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                IsEventDriven = false,
                Vsync = VSyncMode.Off,
                AutoIconify = false
            };
        }

        /// <summary>
        /// Current frames per second count
        /// </summary>
        public int FPS
        {
            get;
            private set;
        }

        /// <summary>
        /// Defines the interval in milliseconds the OnUserKeyPress method is called
        /// </summary>
        public uint KeyPressInterval { get; set; } = 50;

        /// <summary>
        /// Runs the SharpPixels window loop without OpenTK's Windows core-affinity
        /// pinning, which can hurt unfocused windows after they lose the foreground
        /// scheduler boost.
        /// </summary>
        public override unsafe void Run()
        {
            bool timerPeriodStarted = false;
            bool loaded = false;
            const uint TimePeriodMilliseconds = 1;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    WindowsRenderScheduling.Apply();
                    timeBeginPeriod(TimePeriodMilliseconds);
                    timerPeriodStarted = true;
                    ExpectedSchedulerPeriod = (int)TimePeriodMilliseconds;
                }

                Context?.MakeCurrent();
                OnLoad();
                loaded = true;
                OnResize(new ResizeEventArgs(ClientSize));

                var updateStopwatch = Stopwatch.StartNew();
                while (!GLFW.WindowShouldClose(WindowPtr))
                {
                    double updatePeriod = UpdateFrequency == 0.0 ? 0.0 : 1.0 / UpdateFrequency;
                    double elapsedSeconds = updateStopwatch.Elapsed.TotalSeconds;
                    if (elapsedSeconds > updatePeriod)
                    {
                        updateStopwatch.Restart();
                        NewInputFrame();
                        ProcessWindowEvents(false);
                        UpdateTime = elapsedSeconds;
                        OnUpdateFrame(new FrameEventArgs(elapsedSeconds));
                        OnRenderFrame(new FrameEventArgs(elapsedSeconds));
                    }

                    double secondsUntilNextUpdate = updatePeriod - updateStopwatch.Elapsed.TotalSeconds;
                    SleepUntilNextUpdate(secondsUntilNextUpdate);
                }
            }
            finally
            {
                if (loaded)
                {
                    OnUnload();
                }

                if (timerPeriodStarted)
                {
                    timeEndPeriod(TimePeriodMilliseconds);
                }
            }
        }

        /// <summary>
        /// Sleeps or yields until the next fixed-rate update is due.
        /// </summary>
        /// <param name="secondsUntilNextUpdate">Seconds remaining until the next update.</param>
        private void SleepUntilNextUpdate(double secondsUntilNextUpdate)
        {
            if (secondsUntilNextUpdate <= 0.0)
            {
                return;
            }

            int sleepMilliseconds = (int)Math.Floor(secondsUntilNextUpdate * 1000.0) - ExpectedSchedulerPeriod;
            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(sleepMilliseconds);
            }
            else
            {
                Thread.Yield();
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint periodMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint periodMilliseconds);

        /// <summary>
        /// Handles the on load operation.
        /// </summary>
        protected override void OnLoad()
        {
            WindowsRenderScheduling.Apply();

            GL.ClearColor(0f, 0f, 0f, 1.0f);
            // Let OpenTK run freely; presentation VSync remains a user-controlled
            // swap mode rather than a fixed render-loop throttle.
            UpdateFrequency = 0.0;
            IsEventDriven = false;
            ApplyPresentationVSync();
            AutoIconify = false;
            
            CursorState = CursorState.Hidden;

            //add mouse and keyboard handlers
            MouseMove += SharpPixels_MouseMove;
            MouseDown += SharpPixels_MouseClick;
            KeyDown += SharpPixels_KeyDown;
            KeyUp += SharpPixels_KeyUp;
            FileDrop += SharpPixels_FileDrop;
            FocusedChanged += SharpPixels_FocusedChanged;

            for (int i = 0; i < 256; i++)
            {
                float alpha = (float)(i / 255.0);
                for (int j = 0; j < 256; j++)
                {
                    _alphaMulTable[i, j] = (byte)(alpha * j);
                    _oneMinusAlphaMulTable[i, j] = (byte)((1.0 - alpha) * j);
                }
            }

            _vertexArrayObject = GL.GenVertexArray();
            GL.BindVertexArray(_vertexArrayObject);

            _vertexBufferObject = GL.GenBuffer();
            _textureHandle = GL.GenTexture();
            _sourceTextureHandle = GL.GenTexture();
            _shader = new Shader(Properties.Resources.vert, Properties.Resources.frag);
            if (!_shader.Compile())
            {
                throw new InvalidOperationException("SharpPixels could not compile the OpenGL shader program.");
            }

            _shader.Use();
            _texCoordLocation = GL.GetAttribLocation(_shader.Handle, "aTexCoord");
            _textureUniformLocation = GL.GetUniformLocation(_shader.Handle, "texture0");
            _filterModeUniformLocation = GL.GetUniformLocation(_shader.Handle, "filterMode");
            _upscaleModeUniformLocation = GL.GetUniformLocation(_shader.Handle, "upscaleMode");
            _upscaleFactorUniformLocation = GL.GetUniformLocation(_shader.Handle, "upscaleFactor");
            _cropOriginUniformLocation = GL.GetUniformLocation(_shader.Handle, "cropOrigin");
            _cropSizeUniformLocation = GL.GetUniformLocation(_shader.Handle, "cropSize");
            _sourceTextureSizeUniformLocation = GL.GetUniformLocation(_shader.Handle, "sourceTextureSize");
            if (_textureUniformLocation >= 0)
            {
                GL.Uniform1(_textureUniformLocation, 0);
            }
            _drawingStopwatch.Start();
            ResetFpsCounter();

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _textureHandle);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, PixelsWidth, PixelsHeight, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            GL.BindTexture(TextureTarget.Texture2D, _sourceTextureHandle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            if (_texCoordLocation >= 0)
            {
                GL.VertexAttribPointer(_texCoordLocation, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
                GL.EnableVertexAttribArray(_texCoordLocation);
            }

            OnUserInitialize();

            //ToggleFullscreen();

            base.OnLoad();
        }
        

        /// <summary>
        /// Handles the on unload operation.
        /// </summary>
        protected override void OnUnload()
        {
            FocusedChanged -= SharpPixels_FocusedChanged;
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            if (_vertexBufferObject != 0)
            {
                GL.DeleteBuffer(_vertexBufferObject);
            }

            if (_vertexArrayObject != 0)
            {
                GL.DeleteVertexArray(_vertexArrayObject);
            }

            if (_textureHandle != 0)
            {
                GL.DeleteTexture(_textureHandle);
            }

            if (_sourceTextureHandle != 0)
            {
                GL.DeleteTexture(_sourceTextureHandle);
            }

            _shader.Dispose();
            if (_pixelBufferHandle.IsAllocated)
            {
                _pixelBufferHandle.Free();
            }

            foreach (var bitmapMemory in _bitmapMemories.Values)
            {
                try
                {
                    bitmapMemory.Dispose();
                }
                catch (Exception)
                {
                    //can not do much here
                }
            }

            base.OnUnload();
        }

        /// <summary>
        /// Handles focus changes by reasserting render timing choices and restarting
        /// the FPS sample window.
        /// </summary>
        /// <param name="e">Focus state event data.</param>
        private void SharpPixels_FocusedChanged(FocusedChangedEventArgs e)
        {
            IsEventDriven = false;
            ApplyPresentationVSync();
            ResetFpsCounter();
        }

        /// <summary>
        /// Gets or sets whether buffer swaps should wait for monitor vertical blank.
        /// </summary>
        public bool PresentationVSyncEnabled
        {
            get { return _presentationVSyncEnabled; }
            set
            {
                _presentationVSyncEnabled = value;
                ApplyPresentationVSync();
            }
        }

        /// <summary>
        /// Applies the configured presentation sync mode to the OpenTK window.
        /// </summary>
        private void ApplyPresentationVSync()
        {
            VSync = _presentationVSyncEnabled ? VSyncMode.On : VSyncMode.Off;
        }

        /// <summary>
        /// Restarts the FPS measurement window at the current stopwatch timestamp.
        /// </summary>
        private void ResetFpsCounter()
        {
            _fpsCounter = 0;
            _fpsWindowStartMilliseconds = _drawingStopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// Renders the current pixel buffer and queued text overlays.
        /// </summary>
        /// <param name="frameEventArgs">Frame timing arguments supplied by OpenTK.</param>
        protected override void OnRenderFrame(FrameEventArgs frameEventArgs)
        {
            //call the user update method where the user updates the graphics etc
            OnUserUpdate(frameEventArgs.Time);

            if (_gpuSourcePresentationPending)
            {
                RenderGpuSourcePresentation();
            }
            else
            {
                RenderPixelBufferPresentation();
            }

            // Calls OnUserKeyPress method for all pressed keys
            var dateTimeNow = DateTime.Now;
            if (dateTimeNow >= nextKeyPressTime)
            {
                var modifiers = modifiersPressed;
                foreach (Key key in keysPressed)
                {
                    OnUserKeyPress(new KeyEventArgs(key, modifiers));
                }
                nextKeyPressTime = nextKeyPressTime.AddMilliseconds(KeyPressInterval);
            }
            _fpsCounter++;

            // Output default title of the app with fps. Use the real elapsed time
            // instead of assuming this check fires exactly once per second; otherwise a
            // previous stall can make the displayed FPS look artificially low.
            long elapsedMilliseconds = _drawingStopwatch.ElapsedMilliseconds - _fpsWindowStartMilliseconds;
            if (elapsedMilliseconds >= 1000)
            {
                FPS = (int)Math.Round((_fpsCounter * 1000.0) / elapsedMilliseconds);
                Title = string.Format("FPS: {0}", FPS);
                _fpsCounter = 0;
                _fpsWindowStartMilliseconds = _drawingStopwatch.ElapsedMilliseconds;
                
                //delete cached bitmap data 
                _expiredBitmapScratch.Clear();
                foreach (var bitmapMemory in _bitmapMemories)
                {
                    if (dateTimeNow > bitmapMemory.Value.ExpirationTime)
                    {
                        _expiredBitmapScratch.Add(bitmapMemory.Key);
                    }
                }

                foreach (var bitmap in _expiredBitmapScratch)
                {
                    UnlockBitmap(bitmap);
                }
            }
            SwapBuffers();
            base.OnRenderFrame(frameEventArgs);
        }

        /// <summary>
        /// Presents the traditional full-window CPU pixel buffer.
        /// </summary>
        private void RenderPixelBufferPresentation()
        {
            _overlayTextureIsTransparent = false;
            _overlayLayerMode = false;

            SetFullClientViewport();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _textureHandle);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, PixelsWidth, PixelsHeight, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, _pixelBufferPointer);

            _shader.Use();
            SetShaderPresentationUniforms(SharpPixelsGpuFilterMode.Sharp, SharpPixelsGpuUpscaleMode.None, 1, 0.0f, 0.0f, PixelsWidth, PixelsHeight, PixelsWidth, PixelsHeight);
            DrawTexturedQuad();
        }

        /// <summary>
        /// Presents a small source texture through the GPU and blends the dirty overlay layer on top.
        /// </summary>
        private void RenderGpuSourcePresentation()
        {
            UploadGpuSourceTexture();

            GL.Disable(EnableCap.Blend);
            SetFullClientViewport();
            GL.ClearColor(_gpuBorderRed / 255.0f, _gpuBorderGreen / 255.0f, _gpuBorderBlue / 255.0f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            SetLogicalViewport(_gpuDestinationX, _gpuDestinationY, _gpuDestinationWidth, _gpuDestinationHeight);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _sourceTextureHandle);

            _shader.Use();
            SetShaderPresentationUniforms(
                _gpuFilterMode,
                _gpuUpscaleMode,
                _gpuUpscaleFactor,
                _gpuSourceCropX,
                _gpuSourceCropY,
                _gpuSourceCropWidth,
                _gpuSourceCropHeight,
                _gpuSourceWidth,
                _gpuSourceHeight);
            DrawTexturedQuad();

            UploadOverlayTextureDirtyRegion();
            if (_overlayCurrentDirty)
            {
                SetFullClientViewport();
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _textureHandle);
                SetShaderPresentationUniforms(SharpPixelsGpuFilterMode.Sharp, SharpPixelsGpuUpscaleMode.None, 1, 0.0f, 0.0f, PixelsWidth, PixelsHeight, PixelsWidth, PixelsHeight);
                DrawTexturedQuad();
                GL.Disable(EnableCap.Blend);
            }

            _overlayPreviousDirty = _overlayCurrentDirty;
            _overlayPreviousMinX = _overlayCurrentMinX;
            _overlayPreviousMinY = _overlayCurrentMinY;
            _overlayPreviousMaxX = _overlayCurrentMaxX;
            _overlayPreviousMaxY = _overlayCurrentMaxY;
            _overlayLayerMode = false;
            _gpuSourcePresentationPending = false;
        }

        /// <summary>
        /// Uploads the current C64 source pixels into the small GPU texture.
        /// </summary>
        private void UploadGpuSourceTexture()
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _sourceTextureHandle);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);

            if (_sourceTextureWidth != _gpuSourceWidth || _sourceTextureHeight != _gpuSourceHeight)
            {
                _sourceTextureWidth = _gpuSourceWidth;
                _sourceTextureHeight = _gpuSourceHeight;
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, _sourceTextureWidth, _sourceTextureHeight, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
            }

            fixed (uint* sourcePointer = _gpuSourcePixels)
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, _gpuSourceWidth, _gpuSourceHeight, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, (IntPtr)sourcePointer);
            }
        }

        /// <summary>
        /// Uploads only the overlay rectangle changed while drawing this frame.
        /// </summary>
        private void UploadOverlayTextureDirtyRegion()
        {
            if (!_overlayUploadDirty)
            {
                return;
            }

            int x = _overlayUploadMinX;
            int y = _overlayUploadMinY;
            int width = _overlayUploadMaxX - _overlayUploadMinX;
            int height = _overlayUploadMaxY - _overlayUploadMinY;
            if (width <= 0 || height <= 0)
            {
                _overlayUploadDirty = false;
                return;
            }

            IntPtr uploadPointer = IntPtr.Add(_pixelBufferPointer, ((y * PixelsWidth) + x) << 2);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _textureHandle);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, PixelsWidth);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, uploadPointer);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            _overlayUploadDirty = false;
        }

        /// <summary>
        /// Configures the shared presentation shader for either direct or filtered output.
        /// </summary>
        private void SetShaderPresentationUniforms(SharpPixelsGpuFilterMode filterMode, SharpPixelsGpuUpscaleMode upscaleMode, int upscaleFactor, float cropX, float cropY, float cropWidth, float cropHeight, float sourceWidth, float sourceHeight)
        {
            if (_filterModeUniformLocation >= 0)
            {
                GL.Uniform1(_filterModeUniformLocation, (int)filterMode);
            }

            if (_upscaleModeUniformLocation >= 0)
            {
                GL.Uniform1(_upscaleModeUniformLocation, (int)upscaleMode);
            }

            if (_upscaleFactorUniformLocation >= 0)
            {
                GL.Uniform1(_upscaleFactorUniformLocation, Math.Max(1, upscaleFactor));
            }

            if (_cropOriginUniformLocation >= 0)
            {
                GL.Uniform2(_cropOriginUniformLocation, cropX, cropY);
            }

            if (_cropSizeUniformLocation >= 0)
            {
                GL.Uniform2(_cropSizeUniformLocation, cropWidth, cropHeight);
            }

            if (_sourceTextureSizeUniformLocation >= 0)
            {
                GL.Uniform2(_sourceTextureSizeUniformLocation, sourceWidth, sourceHeight);
            }
        }

        /// <summary>
        /// Draws the already-bound texture with the shared full-viewport quad.
        /// </summary>
        private void DrawTexturedQuad()
        {
            GL.BindVertexArray(_vertexArrayObject);
            GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Length / 5);
        }

        /// <summary>
        /// Sets the OpenGL viewport to the whole current client area.
        /// </summary>
        private void SetFullClientViewport()
        {
            GL.Viewport(0, 0, Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y));
        }

        /// <summary>
        /// Maps a logical SharpPixels rectangle into the current OpenGL client viewport.
        /// </summary>
        private void SetLogicalViewport(int x, int y, int width, int height)
        {
            int clientWidth = Math.Max(1, ClientSize.X);
            int clientHeight = Math.Max(1, ClientSize.Y);
            int viewportX = (int)Math.Round((x * clientWidth) / (double)PixelsWidth);
            int viewportY = (int)Math.Round(((PixelsHeight - y - height) * clientHeight) / (double)PixelsHeight);
            int viewportWidth = Math.Max(1, (int)Math.Round((width * clientWidth) / (double)PixelsWidth));
            int viewportHeight = Math.Max(1, (int)Math.Round((height * clientHeight) / (double)PixelsHeight));
            GL.Viewport(viewportX, viewportY, viewportWidth, viewportHeight);
        }

        /// <summary>
        /// Handles the on resize operation.
        /// </summary>
        protected override void OnResize(ResizeEventArgs e)
        {
            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
            base.OnResize(e);
        }

        /// <summary>
        /// Returns the pointer to the bitmap pixels 
        /// </summary>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        /// <returns>The computed result.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte* GetBitmapPointer(Bitmap bitmap)
        {
            BitmapMemory bitmapMemory;
            if (!_bitmapMemories.TryGetValue(bitmap, out bitmapMemory))
            {
                bitmapMemory = new BitmapMemory(bitmap);
                _bitmapMemories.Add(bitmap, bitmapMemory);
            }

            bitmapMemory.ExpirationTime = DateTime.Now.AddSeconds(1);
            return bitmapMemory.BitmapPointer;
        }

        /// <summary>
        /// Unlocks a cached bitmap
        /// This function has to be called, when you want to manipulate a bitmap that has already been used by the engine
        /// The engine will always lock and memorize the bitmap
        /// NEVER unlock bitmaps on your own. If you do so, the engine will crash, since it also caches pointers to bitmap data
        /// </summary>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        /// <returns>returns true, if the bitmap was found and the bitmap was unlocked and returns false, if the bitmap is not in the cache</returns>
        public bool UnlockBitmap(Bitmap bitmap)
        {
            BitmapMemory bitmapMemory;
            if (_bitmapMemories.TryGetValue(bitmap, out bitmapMemory))
            {
                bitmapMemory.Dispose();
                _bitmapMemories.Remove(bitmap);
                return true;
            }
            return false;
        }       

        /// <summary>
        /// MouseMove event handling
        /// Computes the x and y coordinate in the pixel space and calls the OnMouseMove method with these
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseMove(MouseMoveEventArgs e)
        {
            OnUserMouseMove((int)e.Position.X, (int)e.Position.Y);
        }

        /// <summary>
        /// MouseClick event handling
        /// Computes the x and y coordinate in the pixel space and calls the MouseClick method with these
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseClick(MouseButtonEventArgs e)
        {
            OnUserMouseClick((int)MousePosition.X, (int)MousePosition.Y, new MouseState((int)MousePosition.X, (int)MousePosition.Y));
        }

        /// <summary>
        /// User dropped one or more files onto the window.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_FileDrop(FileDropEventArgs e)
        {
            OnUserFileDrop(e.FileNames);
        }

        /// <summary>
        /// User released a key, thus, we call the OnUserKeyUp
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_KeyUp(KeyboardKeyEventArgs e)
        {
            Key key = ConvertKey(e.Key);
            if (keysPressed.Contains(key))
            {
                keysPressed.Remove(key);
                modifiersPressed = ConvertModifiers(e.Modifiers);
                OnUserKeyUp(new KeyEventArgs(key, modifiersPressed));
            }
        }

        /// <summary>
        /// User holds a key, thus, we call the OnUserKeyDown
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_KeyDown(KeyboardKeyEventArgs e)
        {
            Key key = ConvertKey(e.Key);
            if(key != Key.Unknown && !keysPressed.Contains(key))
            {
                keysPressed.Add(key);
                modifiersPressed = ConvertModifiers(e.Modifiers);
                OnUserKeyDown(new KeyEventArgs(key, modifiersPressed));
            }
        }

        /// <summary>
        /// Converts OpenTK 4 key values to the compatibility key enum used by the emulator core.
        /// </summary>
        private static Key ConvertKey(Keys key)
        {
            switch (key)
            {
                case Keys.A: return Key.A;
                case Keys.B: return Key.B;
                case Keys.C: return Key.C;
                case Keys.D: return Key.D;
                case Keys.E: return Key.E;
                case Keys.F: return Key.F;
                case Keys.G: return Key.G;
                case Keys.H: return Key.H;
                case Keys.I: return Key.I;
                case Keys.J: return Key.J;
                case Keys.K: return Key.K;
                case Keys.L: return Key.L;
                case Keys.M: return Key.M;
                case Keys.N: return Key.N;
                case Keys.O: return Key.O;
                case Keys.P: return Key.P;
                case Keys.Q: return Key.Q;
                case Keys.R: return Key.R;
                case Keys.S: return Key.S;
                case Keys.T: return Key.T;
                case Keys.U: return Key.U;
                case Keys.V: return Key.V;
                case Keys.W: return Key.W;
                case Keys.X: return Key.X;
                case Keys.Y: return Key.Y;
                case Keys.Z: return Key.Z;
                case Keys.D0:
                case Keys.KeyPad0: return Key.Number0;
                case Keys.D1:
                case Keys.KeyPad1: return Key.Number1;
                case Keys.D2:
                case Keys.KeyPad2: return Key.Number2;
                case Keys.D3:
                case Keys.KeyPad3: return Key.Number3;
                case Keys.D4:
                case Keys.KeyPad4: return Key.Number4;
                case Keys.D5:
                case Keys.KeyPad5: return Key.Number5;
                case Keys.D6:
                case Keys.KeyPad6: return Key.Number6;
                case Keys.D7:
                case Keys.KeyPad7: return Key.Number7;
                case Keys.D8:
                case Keys.KeyPad8: return Key.Number8;
                case Keys.D9:
                case Keys.KeyPad9: return Key.Number9;
                case Keys.LeftControl: return Key.ControlLeft;
                case Keys.RightControl: return Key.LControl;
                case Keys.LeftShift: return Key.ShiftLeft;
                case Keys.RightShift: return Key.ShiftRight;
                case Keys.LeftAlt: return Key.AltLeft;
                case Keys.RightAlt: return Key.LAlt;
                case Keys.Space: return Key.Space;
                case Keys.Enter:
                case Keys.KeyPadEnter: return Key.Enter;
                case Keys.Backspace: return Key.BackSpace;
                case Keys.Comma: return Key.Comma;
                case Keys.Period: return Key.Period;
                case Keys.Minus:
                case Keys.KeyPadSubtract: return Key.Minus;
                case Keys.Slash: return Key.Slash;
                case Keys.Apostrophe: return Key.Quote;
                case Keys.Semicolon: return Key.Semicolon;
                case Keys.Equal:
                case Keys.KeyPadAdd: return Key.Plus;
                case Keys.Backslash: return Key.BackSlash;
                case Keys.KeyPadMultiply: return Key.KeypadMultiply;
                case Keys.LeftBracket: return Key.BracketLeft;
                case Keys.RightBracket: return Key.BracketRight;
                case Keys.Home: return Key.Home;
                case Keys.PageUp: return Key.PageUp;
                case Keys.PageDown: return Key.PageDown;
                case Keys.Up: return Key.Up;
                case Keys.Down: return Key.Down;
                case Keys.Left: return Key.Left;
                case Keys.Right: return Key.Right;
                case Keys.Escape: return Key.Escape;
                case Keys.Tab: return Key.Tab;
                case Keys.Delete: return Key.Delete;
                case Keys.F1: return Key.F1;
                case Keys.F2: return Key.F2;
                case Keys.F3: return Key.F3;
                case Keys.F4: return Key.F4;
                case Keys.F5: return Key.F5;
                case Keys.F6: return Key.F6;
                case Keys.F7: return Key.F7;
                case Keys.F8: return Key.F8;
                case Keys.F9: return Key.F9;
                case Keys.F10: return Key.F10;
                case Keys.F11: return Key.F11;
                case Keys.F12: return Key.F12;
                default: return Key.Unknown;
            }
        }

        /// <summary>
        /// Converts OpenTK 4 modifier flags to the compatibility modifier enum used by SharpPixels.
        /// </summary>
        private static KeyModifiers ConvertModifiers(OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers modifiers)
        {
            KeyModifiers result = 0;
            if ((modifiers & OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers.Alt) != 0)
            {
                result |= KeyModifiers.Alt;
            }

            if ((modifiers & OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers.Control) != 0)
            {
                result |= KeyModifiers.Control;
            }

            if ((modifiers & OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers.Shift) != 0)
            {
                result |= KeyModifiers.Shift;
            }

            if ((modifiers & OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers.Super) != 0)
            {
                result |= KeyModifiers.Command;
            }

            return result;
        }

        /// <summary>
        /// Returns whether key pressed is true.
        /// </summary>
        public bool IsKeyPressed(Key key)
        {
            return keysPressed.Contains(key);
        }

        /// <summary>
        /// Returns whether modifier pressed is true.
        /// </summary>
        public bool IsModifierPressed(KeyModifiers keyModifiers)
        {
            return (modifiersPressed & keyModifiers) == keyModifiers;
        }

        #region abstract members the user of the engine has to override

        /// <summary>
        /// Handles the on user update operation.
        /// </summary>
        public abstract void OnUserUpdate(double time);
        /// <summary>
        /// Handles the on user initialize operation.
        /// </summary>
        public abstract void OnUserInitialize();
        /// <summary>
        /// Handles the on user mouse move operation.
        /// </summary>
        public abstract void OnUserMouseMove(int x, int y);
        /// <summary>
        /// Handles the on user mouse click operation.
        /// </summary>
        public abstract void OnUserMouseClick(int x, int y, MouseState mouseState);
        /// <summary>
        /// Handles the on user key down operation.
        /// </summary>
        public abstract void OnUserKeyDown(KeyEventArgs keyEventArgs);
        /// <summary>
        /// Handles the on user key press operation.
        /// </summary>
        public abstract void OnUserKeyPress(KeyEventArgs keyEventArgs);
        /// <summary>
        /// Handles the on user key up operation.
        /// </summary>
        public abstract void OnUserKeyUp(KeyEventArgs keyEventArgs);
        /// <summary>
        /// Handles host files dropped onto the window.
        /// </summary>
        public virtual void OnUserFileDrop(string[] fileNames)
        {
        }

        #endregion

        #region public methods

        /// <summary>
        /// Clears the screen (= all pixels black)
        /// </summary>
        public void ClearScreen()
        {
            FillPixelBuffer(PackRgba(0, 0, 0));
        }

        /// <summary>
        /// Schedules a GPU-side presentation of an ARGB source frame and switches the CPU buffer to overlay-only drawing for this frame.
        /// </summary>
        public bool DrawArgbPixelsGpuPresented(
            uint[] sourcePixels,
            int sourceWidth,
            int sourceHeight,
            int cropX,
            int cropY,
            int cropWidth,
            int cropHeight,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight,
            SharpPixelsGpuFilterMode filterMode,
            SharpPixelsGpuUpscaleMode upscaleMode,
            int upscaleFactor,
            byte borderRed,
            byte borderGreen,
            byte borderBlue)
        {
            if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
            {
                return false;
            }

            int requiredPixels = sourceWidth * sourceHeight;
            if (sourcePixels.Length < requiredPixels)
            {
                return false;
            }

            int clampedCropX = ClampInt(cropX, 0, sourceWidth - 1);
            int clampedCropY = ClampInt(cropY, 0, sourceHeight - 1);
            int clampedCropRight = ClampInt(cropX + cropWidth, clampedCropX + 1, sourceWidth);
            int clampedCropBottom = ClampInt(cropY + cropHeight, clampedCropY + 1, sourceHeight);
            int clampedDestinationX = ClampInt(destinationX, 0, PixelsWidth - 1);
            int clampedDestinationY = ClampInt(destinationY, 0, PixelsHeight - 1);
            int clampedDestinationRight = ClampInt(destinationX + destinationWidth, clampedDestinationX + 1, PixelsWidth);
            int clampedDestinationBottom = ClampInt(destinationY + destinationHeight, clampedDestinationY + 1, PixelsHeight);

            _gpuSourcePixels = sourcePixels;
            _gpuSourceWidth = sourceWidth;
            _gpuSourceHeight = sourceHeight;
            _gpuSourceCropX = clampedCropX;
            _gpuSourceCropY = clampedCropY;
            _gpuSourceCropWidth = clampedCropRight - clampedCropX;
            _gpuSourceCropHeight = clampedCropBottom - clampedCropY;
            _gpuDestinationX = clampedDestinationX;
            _gpuDestinationY = clampedDestinationY;
            _gpuDestinationWidth = clampedDestinationRight - clampedDestinationX;
            _gpuDestinationHeight = clampedDestinationBottom - clampedDestinationY;
            _gpuFilterMode = filterMode;
            _gpuUpscaleMode = upscaleMode;
            _gpuUpscaleFactor = Math.Max(1, Math.Min(4, upscaleFactor));
            _gpuBorderRed = borderRed;
            _gpuBorderGreen = borderGreen;
            _gpuBorderBlue = borderBlue;
            _gpuSourcePresentationPending = true;

            BeginOverlayLayerForGpuSource();
            return true;
        }

        /// <summary>
        /// Draws a pixel at x, y using the given color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawPixel(int x, int y, Color color)
        {
            DrawPixel(x, y, color.R, color.G, color.B);
        }

        /// <summary>
        /// Draws a pixel at x, y using the given color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawPixelWithAlpha(int x, int y, Color color)
        {
            DrawPixelWithAlpha(x, y, color.R, color.G, color.B, color.A);
        }

        /// <summary>
        /// Draws a pixel at x, y using the given r, g, b values
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        public void DrawPixel(int x, int y, byte red, byte green, byte blue)
        {
            if (x < 0 || x >= PixelsWidth || y < 0 || y >= PixelsHeight)
            {
                return;
            }

            uint* target = (uint*)_pixelBufferPointer.ToPointer();
            target[(y * PixelsWidth) + x] = PackRgba(red, green, blue);
            MarkOverlayDirty(x, y, 1, 1);
        }

        /// <summary>
        /// Draws a pixel at x, y using the given r, g, b, a values
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        public void DrawPixelWithAlpha(int x, int y, byte red, byte green, byte blue, byte alpha)
        {
            if (x < 0 || x >= PixelsWidth || y < 0 || y >= PixelsHeight)
            {
                return;
            }
            var address = (y * _pixelStrideBytes) + (x << 2);
            _pixels[address] = (byte)(_alphaMulTable[alpha, red] + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
            address++;
            _pixels[address] = (byte)(_alphaMulTable[alpha, green] + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
            address++;
            _pixels[address] = (byte)(_alphaMulTable[alpha, blue] + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
            address++;
            _pixels[address] = (byte)(alpha + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
            MarkOverlayDirty(x, y, 1, 1);
        }

        /// <summary>
        /// Prepares the CPU buffer as a transparent overlay layer for a GPU-presented source frame.
        /// </summary>
        private void BeginOverlayLayerForGpuSource()
        {
            _overlayLayerMode = true;
            _overlayCurrentDirty = false;
            _overlayUploadDirty = false;

            if (!_overlayTextureIsTransparent)
            {
                ClearPixelBufferRectangle(0, 0, PixelsWidth, PixelsHeight);
                MarkOverlayUploadDirty(0, 0, PixelsWidth, PixelsHeight);
                _overlayTextureIsTransparent = true;
                _overlayPreviousDirty = false;
                return;
            }

            if (_overlayPreviousDirty)
            {
                int width = _overlayPreviousMaxX - _overlayPreviousMinX;
                int height = _overlayPreviousMaxY - _overlayPreviousMinY;
                ClearPixelBufferRectangle(_overlayPreviousMinX, _overlayPreviousMinY, width, height);
                MarkOverlayUploadDirty(_overlayPreviousMinX, _overlayPreviousMinY, width, height);
            }
        }

        /// <summary>
        /// Clears a rectangle in the CPU upload buffer to transparent black.
        /// </summary>
        private void ClearPixelBufferRectangle(int x, int y, int width, int height)
        {
            int startX = ClampInt(x, 0, PixelsWidth);
            int startY = ClampInt(y, 0, PixelsHeight);
            int endX = ClampInt(x + width, startX, PixelsWidth);
            int endY = ClampInt(y + height, startY, PixelsHeight);
            int clearBytes = (endX - startX) << 2;
            if (clearBytes <= 0 || endY <= startY)
            {
                return;
            }

            for (int targetY = startY; targetY < endY; targetY++)
            {
                Array.Clear(_pixels, (targetY * _pixelStrideBytes) + (startX << 2), clearBytes);
            }
        }

        /// <summary>
        /// Marks a rectangle that should be visible in the overlay layer.
        /// </summary>
        private void MarkOverlayDirty(int x, int y, int width, int height)
        {
            if (!_overlayLayerMode || width <= 0 || height <= 0)
            {
                return;
            }

            int startX = ClampInt(x, 0, PixelsWidth);
            int startY = ClampInt(y, 0, PixelsHeight);
            int endX = ClampInt(x + width, startX, PixelsWidth);
            int endY = ClampInt(y + height, startY, PixelsHeight);
            if (startX >= endX || startY >= endY)
            {
                return;
            }

            if (!_overlayCurrentDirty)
            {
                _overlayCurrentMinX = startX;
                _overlayCurrentMinY = startY;
                _overlayCurrentMaxX = endX;
                _overlayCurrentMaxY = endY;
                _overlayCurrentDirty = true;
            }
            else
            {
                _overlayCurrentMinX = Math.Min(_overlayCurrentMinX, startX);
                _overlayCurrentMinY = Math.Min(_overlayCurrentMinY, startY);
                _overlayCurrentMaxX = Math.Max(_overlayCurrentMaxX, endX);
                _overlayCurrentMaxY = Math.Max(_overlayCurrentMaxY, endY);
            }

            MarkOverlayUploadDirty(startX, startY, endX - startX, endY - startY);
        }

        /// <summary>
        /// Marks a rectangle whose backing texture bytes must be refreshed, even if it only clears old overlay pixels.
        /// </summary>
        private void MarkOverlayUploadDirty(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int startX = ClampInt(x, 0, PixelsWidth);
            int startY = ClampInt(y, 0, PixelsHeight);
            int endX = ClampInt(x + width, startX, PixelsWidth);
            int endY = ClampInt(y + height, startY, PixelsHeight);
            if (startX >= endX || startY >= endY)
            {
                return;
            }

            if (!_overlayUploadDirty)
            {
                _overlayUploadMinX = startX;
                _overlayUploadMinY = startY;
                _overlayUploadMaxX = endX;
                _overlayUploadMaxY = endY;
                _overlayUploadDirty = true;
                return;
            }

            _overlayUploadMinX = Math.Min(_overlayUploadMinX, startX);
            _overlayUploadMinY = Math.Min(_overlayUploadMinY, startY);
            _overlayUploadMaxX = Math.Max(_overlayUploadMaxX, endX);
            _overlayUploadMaxY = Math.Max(_overlayUploadMaxY, endY);
        }

        /// <summary>
        /// Clamps an integer to the inclusive lower and upper-exclusive style ranges used by the drawing code.
        /// </summary>
        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        /// <summary>
        /// Handles the initialize pixel alpha channel operation.
        /// </summary>
        private void InitializePixelAlphaChannel()
        {
            int pixelCount = PixelsWidth * PixelsHeight;
            uint* target = (uint*)_pixelBufferPointer.ToPointer();
            for (int i = 0; i < pixelCount; i++)
            {
                target[i] |= 0xFF000000U;
            }
        }

        /// <summary>
        /// Converts RGBA channels to the little-endian 32-bit layout used by the upload buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PackRgba(byte red, byte green, byte blue)
        {
            return 0xFF000000U | ((uint)blue << 16) | ((uint)green << 8) | red;
        }

        /// <summary>
        /// Converts an ARGB source pixel to the little-endian RGBA layout used by the upload buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PackArgbSource(uint argb)
        {
            return 0xFF000000U | ((argb & 0x000000FFU) << 16) | (argb & 0x0000FF00U) | ((argb >> 16) & 0x000000FFU);
        }

        /// <summary>
        /// Fills the entire upload buffer with a packed RGBA color.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FillPixelBuffer(uint packedColor)
        {
            int pixelCount = PixelsWidth * PixelsHeight;
            uint* target = (uint*)_pixelBufferPointer.ToPointer();
            for (int i = 0; i < pixelCount; i++)
            {
                target[i] = packedColor;
            }
        }

        /// <summary>
        /// Updates texture coordinates.
        /// </summary>
        private void UpdateTextureCoordinates()
        {
            float left = 0.0f;
            float right = 1.0f;
            float top = 0.0f;
            float bottom = 1.0f;

            vertices[3] = left;
            vertices[4] = bottom;
            vertices[8] = right;
            vertices[9] = bottom;
            vertices[13] = left;
            vertices[14] = top;

            vertices[18] = left;
            vertices[19] = top;
            vertices[23] = right;
            vertices[24] = bottom;
            vertices[28] = right;
            vertices[29] = top;
        }

        /// <summary>
        /// Draws ARGB source pixels into the window buffer using an integer nearest-neighbor scale.
        /// </summary>
        /// <param name="sourcePixels">Source pixels in 0xAARRGGBB format.</param>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        /// <param name="destinationX">Destination X coordinate in pixels.</param>
        /// <param name="destinationY">Destination Y coordinate in pixels.</param>
        /// <param name="scale">Integer scale factor.</param>
        public void DrawArgbPixelsScaled(uint[] sourcePixels, int sourceWidth, int sourceHeight, int destinationX, int destinationY, int scale)
        {
            if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0 || scale <= 0)
            {
                return;
            }

            int requiredPixels = sourceWidth * sourceHeight;
            if (sourcePixels.Length < requiredPixels)
            {
                return;
            }

            int scaledWidth = sourceWidth * scale;
            int scaledHeight = sourceHeight * scale;
            if (destinationX >= 0 &&
                destinationY >= 0 &&
                destinationX + scaledWidth <= PixelsWidth &&
                destinationY + scaledHeight <= PixelsHeight)
            {
                uint* target = (uint*)_pixelBufferPointer.ToPointer();
                for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
                {
                    int sourceRow = sourceY * sourceWidth;
                    int targetRowIndex = ((destinationY + (sourceY * scale)) * PixelsWidth) + destinationX;
                    uint* targetRow = target + targetRowIndex;

                    if (scale == 1)
                    {
                        for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                        {
                            targetRow[sourceX] = PackArgbSource(sourcePixels[sourceRow + sourceX]);
                        }
                        continue;
                    }

                    int targetX = 0;
                    for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                    {
                        uint packed = PackArgbSource(sourcePixels[sourceRow + sourceX]);
                        for (int xRepeat = 0; xRepeat < scale; xRepeat++)
                        {
                            targetRow[targetX++] = packed;
                        }
                    }

                    int copiedRowBytes = scaledWidth << 2;
                    for (int yRepeat = 1; yRepeat < scale; yRepeat++)
                    {
                        System.Buffer.MemoryCopy(targetRow, targetRow + (yRepeat * PixelsWidth), copiedRowBytes, copiedRowBytes);
                    }
                }

                return;
            }

            uint* clippedTarget = (uint*)_pixelBufferPointer.ToPointer();
            for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
            {
                int targetY = destinationY + (sourceY * scale);
                int yRepeatStart = Math.Max(0, -targetY);
                int yRepeatEnd = Math.Min(scale, PixelsHeight - targetY);
                if (yRepeatEnd <= yRepeatStart)
                {
                    continue;
                }

                int sourceRow = sourceY * sourceWidth;
                for (int yRepeat = yRepeatStart; yRepeat < yRepeatEnd; yRepeat++)
                {
                    int targetRowIndex = (targetY + yRepeat) * PixelsWidth;
                    for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                    {
                        int targetX = destinationX + (sourceX * scale);
                        int xRepeatStart = Math.Max(0, -targetX);
                        int xRepeatEnd = Math.Min(scale, PixelsWidth - targetX);
                        if (xRepeatEnd <= xRepeatStart)
                        {
                            continue;
                        }

                        uint packed = PackArgbSource(sourcePixels[sourceRow + sourceX]);
                        int targetIndex = targetRowIndex + targetX + xRepeatStart;
                        for (int xRepeat = xRepeatStart; xRepeat < xRepeatEnd; xRepeat++)
                        {
                            clippedTarget[targetIndex++] = packed;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Draws ARGB source pixels stretched to the full window using nearest-neighbor sampling.
        /// </summary>
        /// <param name="sourcePixels">Source pixels in 0xAARRGGBB format.</param>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        public void DrawArgbPixelsStretched(uint[] sourcePixels, int sourceWidth, int sourceHeight)
        {
            if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                return;
            }

            int requiredPixels = sourceWidth * sourceHeight;
            if (sourcePixels.Length < requiredPixels)
            {
                return;
            }

            EnsureStretchMaps(sourceWidth, sourceHeight);
            uint* target = (uint*)_pixelBufferPointer.ToPointer();
            for (int y = 0; y < PixelsHeight; y++)
            {
                int sourceY = _stretchSourceYMap[y];
                int sourceRow = sourceY * sourceWidth;
                int targetIndex = y * PixelsWidth;
                for (int x = 0; x < PixelsWidth; x++)
                {
                    int sourceX = _stretchSourceXMap[x];
                    target[targetIndex++] = PackArgbSource(sourcePixels[sourceRow + sourceX]);
                }
            }
        }

        /// <summary>
        /// Draws a cropped ARGB source rectangle stretched into an arbitrary target rectangle.
        /// </summary>
        /// <param name="sourcePixels">Source pixels in 0xAARRGGBB format.</param>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        /// <param name="cropX">Source crop X coordinate.</param>
        /// <param name="cropY">Source crop Y coordinate.</param>
        /// <param name="cropWidth">Source crop width.</param>
        /// <param name="cropHeight">Source crop height.</param>
        /// <param name="destinationX">Destination X coordinate.</param>
        /// <param name="destinationY">Destination Y coordinate.</param>
        /// <param name="destinationWidth">Destination width.</param>
        /// <param name="destinationHeight">Destination height.</param>
        public void DrawArgbPixelsCroppedStretched(uint[] sourcePixels, int sourceWidth, int sourceHeight, int cropX, int cropY, int cropWidth, int cropHeight, int destinationX, int destinationY, int destinationWidth, int destinationHeight)
        {
            if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0 || cropWidth <= 0 || cropHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
            {
                return;
            }

            int requiredPixels = sourceWidth * sourceHeight;
            if (sourcePixels.Length < requiredPixels)
            {
                return;
            }

            int sourceCropX = Math.Max(0, cropX);
            int sourceCropY = Math.Max(0, cropY);
            int sourceCropRight = Math.Min(sourceWidth, cropX + cropWidth);
            int sourceCropBottom = Math.Min(sourceHeight, cropY + cropHeight);
            if (sourceCropRight <= sourceCropX || sourceCropBottom <= sourceCropY)
            {
                return;
            }

            int sourceCropWidth = sourceCropRight - sourceCropX;
            int sourceCropHeight = sourceCropBottom - sourceCropY;
            int startX = Math.Max(0, destinationX);
            int startY = Math.Max(0, destinationY);
            int endX = Math.Min(PixelsWidth, destinationX + destinationWidth);
            int endY = Math.Min(PixelsHeight, destinationY + destinationHeight);
            if (endX <= startX || endY <= startY)
            {
                return;
            }

            uint* target = (uint*)_pixelBufferPointer.ToPointer();
            for (int y = startY; y < endY; y++)
            {
                int destinationOffsetY = y - destinationY;
                int sourceY = sourceCropY + Math.Min(sourceCropHeight - 1, ((destinationOffsetY * sourceCropHeight) + (destinationHeight / 2)) / destinationHeight);
                int sourceRow = sourceY * sourceWidth;
                int targetIndex = (y * PixelsWidth) + startX;
                for (int x = startX; x < endX; x++)
                {
                    int destinationOffsetX = x - destinationX;
                    int sourceX = sourceCropX + Math.Min(sourceCropWidth - 1, ((destinationOffsetX * sourceCropWidth) + (destinationWidth / 2)) / destinationWidth);
                    target[targetIndex++] = PackArgbSource(sourcePixels[sourceRow + sourceX]);
                }
            }
        }

        /// <summary>
        /// Builds nearest-neighbor stretch lookup tables when source or target dimensions change.
        /// </summary>
        private void EnsureStretchMaps(int sourceWidth, int sourceHeight)
        {
            if (_stretchSourceXMap != null &&
                _stretchSourceYMap != null &&
                _stretchMapSourceWidth == sourceWidth &&
                _stretchMapSourceHeight == sourceHeight &&
                _stretchMapTargetWidth == PixelsWidth &&
                _stretchMapTargetHeight == PixelsHeight)
            {
                return;
            }

            _stretchSourceXMap = new int[PixelsWidth];
            _stretchSourceYMap = new int[PixelsHeight];
            for (int x = 0; x < PixelsWidth; x++)
            {
                _stretchSourceXMap[x] = (x * sourceWidth) / PixelsWidth;
            }

            for (int y = 0; y < PixelsHeight; y++)
            {
                _stretchSourceYMap[y] = (y * sourceHeight) / PixelsHeight;
            }

            _stretchMapSourceWidth = sourceWidth;
            _stretchMapSourceHeight = sourceHeight;
            _stretchMapTargetWidth = PixelsWidth;
            _stretchMapTargetHeight = PixelsHeight;
        }

        /// <summary>
        /// Draws a line from x0, y0 to x1, y1 using the given color
        /// </summary>
        /// <param name="x0">Start X coordinate in pixels.</param>
        /// <param name="y0">Start Y coordinate in pixels.</param>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawLine(int x0, int y0, int x1, int y1, Color color)
        {
            DrawLine(x0, y0, x1, y1, color.R, color.G, color.B);
        }

        /// <summary>
        /// Draws a line from x0, y0 to x1, y1 using the given r, g, b values
        /// </summary>
        /// <param name="x0">Start X coordinate in pixels.</param>
        /// <param name="y0">Start Y coordinate in pixels.</param>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        public void DrawLine(int x0, int y0, int x1, int y1, byte red, byte green, byte blue)
        {
            if (y0 == y1)
            {
                int startX = Math.Min(x0, x1);
                DrawFilledRectangle(startX, y0, Math.Abs(x1 - x0) + 1, 1, red, green, blue);
                return;
            }

            if (x0 == x1)
            {
                int startY = Math.Min(y0, y1);
                DrawFilledRectangle(x0, startY, 1, Math.Abs(y1 - y0) + 1, red, green, blue);
                return;
            }

            //code from https://de.wikipedia.org/wiki/Bresenham-Algorithmus
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -1 * Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy, e2;

            int ScreenWidth_4 = (PixelsWidth << 2);

            while (true)
            {
                if (x0 >= 0 && x0 < PixelsWidth && y0 >= 0 && y0 < PixelsHeight)
                {
                    var address = y0 * ScreenWidth_4 + (x0 << 2);
                    _pixels[address] = red;
                    address++;
                    _pixels[address] = green;
                    address++;
                    _pixels[address] = blue;
                }
                if (x0 == x1 && y0 == y1)
                {
                    break;
                }
                e2 = 2 * err;
                if (e2 > dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        /// <summary>
        /// Draws a line from x0, y0 to x1, y1 using the given color and alpha
        /// </summary>
        /// <param name="x0">Start X coordinate in pixels.</param>
        /// <param name="y0">Start Y coordinate in pixels.</param>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawLineWithAlpha(int x0, int y0, int x1, int y1, Color color)
        {
            DrawLineWithAlpha(x0, y0, x1, y1, color.R, color.G, color.B, color.A);
        }

        /// <summary>
        /// Draws a line from x0, y0 to x1, y1 using the given r, g, b, and alpha values
        /// </summary>
        /// <param name="x0">Start X coordinate in pixels.</param>
        /// <param name="y0">Start Y coordinate in pixels.</param>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        public void DrawLineWithAlpha(int x0, int y0, int x1, int y1, byte red, byte green, byte blue, byte alpha)
        {
            if (y0 == y1)
            {
                int startX = Math.Min(x0, x1);
                DrawFilledRectangleWithAlpha(startX, y0, Math.Abs(x1 - x0) + 1, 1, red, green, blue, alpha);
                return;
            }

            if (x0 == x1)
            {
                int startY = Math.Min(y0, y1);
                DrawFilledRectangleWithAlpha(x0, startY, 1, Math.Abs(y1 - y0) + 1, red, green, blue, alpha);
                return;
            }

            //code from https://de.wikipedia.org/wiki/Bresenham-Algorithmus
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -1 * Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy, e2;

            int ScreenWidth_4 = (PixelsWidth << 2);

            while (true)
            {
                if (x0 >= 0 && x0 < PixelsWidth && y0 >= 0 && y0 < PixelsHeight)
                {
                    var address = y0 * ScreenWidth_4 + (x0 << 2);
                    _pixels[address] = (byte)(_alphaMulTable[alpha, red] + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                    address++;
                    _pixels[address] = (byte)(_alphaMulTable[alpha, green] + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                    address++;
                    _pixels[address] = (byte)(_alphaMulTable[alpha, blue] + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                }
                if (x0 == x1 && y0 == y1)
                {
                    break;
                }
                e2 = 2 * err;
                if (e2 > dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        /// <summary>
        /// Draws a bitmap at the given position
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        /// <param name="maxWidth">Maximum width in pixels.</param>
        /// <param name="maxHeight">Maximum height in pixels.</param>
        public void DrawBitmap(int x, int y, Bitmap bitmap)
        {
            var bitmapPointer = GetBitmapPointer(bitmap);
            int x1, y1;
            var width = bitmap.Width;
            var height = bitmap.Height;
            var width_4 = (width << 2);
            var pixelsWidth_4 = (PixelsWidth << 2);

            for (var y0 = 0; y0 < height; y0++)
            {
                y1 = y0 + y;
                //check bounds
                if (y1 < 0)
                {
                    continue;
                }
                if (y1 >= PixelsHeight)
                {
                    break;
                }

                var a = y0 * width_4;
                var b = y1 * pixelsWidth_4;

                for (var x0 = 0; x0 < width; x0++)
                {
                    x1 = x0 + x;
                    //check bounds
                    if (x1 < 0)
                    {
                        continue;
                    }
                    if (x1 >= PixelsWidth)
                    {
                        continue;
                    }

                    var x0_4 = (x0 << 2);
                    var x1_4 = (x1 << 2);
                    
                    var address0 = a + x0_4;
                    var address1 = b + x1_4;

                    _pixels[address1 + 2] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _pixels[address1] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _pixels[address1 - 2] = bitmapPointer[address0];
                }
            }
        }

        /// <summary>
        /// Draws a part of a bitmap at the given position
        /// Uses x2, y2 and sourceWidth, sourceHeight to determine part of image
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        public void DrawBitmap(int x, int y, Bitmap bitmap, int x2, int y2, int sourceWidth, int sourceHeight)
        {
            var bitmapPointer = GetBitmapPointer(bitmap);
            int x1, y1;
            var bitmapWidth = bitmap.Width;
            var bitmapHeight = bitmap.Height;
            var bitmapWidth_4 = (bitmapWidth << 2);
            var pixelsWidth_4 = (PixelsWidth << 2);
            var x2_sourceWidth = x2 + sourceWidth;
            var y2_sourceHeight = y2 + sourceHeight;

            for (var y0 = y2; y0 < y2_sourceHeight; y0++)
            {
                y1 = y0 - y2 + y;
                //check bounds
                if (y1 < 0 || y1 < 0)
                {
                    continue;
                }
                if (y0 >= bitmapHeight || y1 >= PixelsHeight)
                {
                    break;
                }

                var a = y0 * bitmapWidth_4;
                var b = y1 * pixelsWidth_4;

                for (var x0 = x2; x0 < x2_sourceWidth; x0++)
                {
                    x1 = x0 - x2 + x;
                    //check bounds
                    if (x0 < 0 || x1 < 0)
                    {
                        continue;
                    }
                    if (x0 >= bitmapWidth || x1 >= PixelsWidth)
                    {
                        continue;
                    }

                    var x0_4 = (x0 << 2);
                    var x1_4 = (x1 << 2);

                    var address0 = a + x0_4;
                    var address1 = b + x1_4;

                    _pixels[address1 + 2] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _pixels[address1] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _pixels[address1 - 2] = bitmapPointer[address0];
                }
            }
        }

        /// <summary>
        /// Draws a bitmap at the given position using the alpha channel
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        public void DrawBitmapWithAlpha(int x, int y, Bitmap bitmap)
        {
            byte* bitmapPointer = GetBitmapPointer(bitmap);
            int x1, y1;
            var width = bitmap.Width;
            var height = bitmap.Height;
            var width_4 = (width << 2);
            var pixelsWidth_4 = (PixelsWidth << 2);

            for (var y0 = 0; y0 < height; y0++)                
            {
                y1 = y0 + y;
                //check bounds
                if (y1 < 0)
                {
                    continue;
                }
                if (y1 >= PixelsHeight)
                {
                    continue;
                }

                var a = y0 * width_4;
                var b = y1 * pixelsWidth_4;

                for (var x0 = 0; x0 < width; x0++)
                {
                    x1 = x0 + x;
                    //check bounds
                    if (x1 < 0)
                    {
                        continue;
                    }
                    if (x1 >= PixelsWidth)
                    {
                        continue;
                    }

                    var x0_4 = (x0 << 2);
                    var x1_4 = (x1 << 2);
                    var address0 = a + x0_4;
                    var address1 = b + x1_4;
                    var alphaIndex = bitmapPointer[address0 + 3];
                    //here, we use the lookup tables, which we precomputed, to do fast alpha blending
                    _pixels[address1 + 2] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _pixels[address1 + 2]]);
                    address0++;
                    address1++;
                    _pixels[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _pixels[address1]]);
                    address0++;
                    address1++;
                    _pixels[address1 - 2] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _pixels[address1 - 2]]);
                }
            }
        }

        /// <summary>
        /// Draws a part of a bitmap at the given position using the alpha channel
        /// Uses x2, y2 and sourceWidth, sourceHeight to determine part of image
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        public void DrawBitmapWithAlpha(int x, int y, Bitmap bitmap, int x2, int y2, int sourceWidth, int sourceHeight)
        {
            var bitmapPointer = GetBitmapPointer(bitmap);
            int x1, y1;
            var bitmapWidth = bitmap.Width;
            var bitmapHeight = bitmap.Height;
            var bitmapWidth_4 = (bitmapWidth << 2);
            var pixelsWidth_4 = (PixelsWidth << 2);
            var x2_sourceWidth = x2 + sourceWidth;
            var y2_sourceHeight = y2 + sourceHeight;

            for (var y0 = y2; y0 < y2_sourceHeight; y0++)
            {
                y1 = y0 - y2 + y;
                //check bounds
                if (y1 < 0 || y1 < 0)
                {
                    continue;
                }
                if (y0 >= bitmapHeight || y1 >= PixelsHeight)
                {
                    break;
                }
                
                var a = y0 * bitmapWidth_4;
                var b = y1 * pixelsWidth_4;

                for (var x0 = x2; x0 < x2_sourceWidth; x0++) 
                {
                    x1 = x0 - x2 + x;
                    //check bounds
                    if (x0 < 0 || x1 < 0)
                    {
                        continue;
                    }
                    if (x0 >= bitmapWidth || x1 >= PixelsWidth)
                    {
                        continue;
                    }

                    var x0_4 = (x0 << 2);
                    var x1_4 = (x1 << 2);                    

                    var address0 = a + x0_4;
                    var address1 = b + x1_4;
                    var alphaIndex = bitmapPointer[address0 + 3];

                    _pixels[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _pixels[address1]]);
                    address0++;
                    address1++;
                    _pixels[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _pixels[address1]]);
                    address0++;
                    address1++;
                    _pixels[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _pixels[address1]]);
                }
            }
        }

        /// <summary>
        /// Draws a bitmap at the given position using the alpha channel as 100% transparency
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        public void DrawBitmapWithTransparency(int x, int y, Bitmap bitmap)
        {
            byte* bitmapPointer = GetBitmapPointer(bitmap);
            int x1, y1;
            var width = bitmap.Width;
            var height = bitmap.Height;
            var width_4 = (width << 2);
            var pixelsWidth_4 = (PixelsWidth << 2);

            for (var y0 = 0; y0 < height; y0++)
            {
                y1 = y0 + y;
                //check bounds
                if (y1 < 0)
                {
                    continue;
                }
                if (y1 >= PixelsHeight)
                {
                    continue;
                }

                var a = y0 * width_4;
                var b = y1 * pixelsWidth_4;

                for (var x0 = 0; x0 < width; x0++)
                {
                    x1 = x0 + x;
                    //check bounds
                    if (x1 < 0)
                    {
                        continue;
                    }
                    if (x1 >= PixelsWidth)
                    {
                        continue;
                    }

                    var x0_4 = (x0 << 2);
                    var x1_4 = (x1 << 2);
                    var address0 = a + x0_4;
                    var address1 = b + x1_4;
                    var alphaIndex = bitmapPointer[address0 + 3];

                    if (alphaIndex == 255)
                    {
                        _pixels[address1] = bitmapPointer[address0];
                        address0++;
                        address1++;
                        _pixels[address1] = bitmapPointer[address0];
                        address0++;
                        address1++;
                        _pixels[address1] = bitmapPointer[address0];
                    }
                }
            }
        }

        /// <summary>
        /// Draws a part of a bitmap at the given position using the alpha channel as 100% transparency
        /// Uses x2, y2 and sourceWidth, sourceHeight to determine part of image
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        public void DrawBitmapWithTransparency(int x, int y, Bitmap bitmap, int x2, int y2, int sourceWidth, int sourceHeight)
        {
            var bitmapPointer = GetBitmapPointer(bitmap);
            int x1, y1;
            var bitmapWidth = bitmap.Width;
            var bitmapHeight = bitmap.Height;
            var bitmapWidth_4 = (bitmapWidth << 2);
            var pixelsWidth_4 = (PixelsWidth << 2);
            var x2_sourceWidth = x2 + sourceWidth;
            var y2_sourceHeight = y2 + sourceHeight;

            for (var y0 = y2; y0 < y2_sourceHeight; y0++)
            {
                y1 = y0 - y2 + y;
                //check bounds
                if (y1 < 0 || y1 < 0)
                {
                    continue;
                }
                if (y0 >= bitmapHeight || y1 >= PixelsHeight)
                {
                    break;
                }

                var a = y0 * bitmapWidth_4;
                var b = y1 * pixelsWidth_4;

                for (var x0 = x2; x0 < x2_sourceWidth; x0++)
                {
                    x1 = x0 - x2 + x;
                    //check bounds
                    if (x0 < 0 || x1 < 0)
                    {
                        continue;
                    }
                    if (x0 >= bitmapWidth || x1 >= PixelsWidth)
                    {
                        continue;
                    }

                    var x0_4 = (x0 << 2);
                    var x1_4 = (x1 << 2);

                    var address0 = a + x0_4;
                    var address1 = b + x1_4;
                    var alphaIndex = bitmapPointer[address0 + 3];
                    if (alphaIndex == 255)
                    {
                        _pixels[address1] = bitmapPointer[address0];
                        address0++;
                        address1++;
                        _pixels[address1] = bitmapPointer[address0];
                        address0++;
                        address1++;
                        _pixels[address1] = bitmapPointer[address0];
                    }
                }
            }
        }

        /// <summary>
        /// Draws a filled rectangle at the given position with the given width and height and color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawFilledRectangle(int x, int y, int width, int height, Color color)
        {
            DrawFilledRectangle(x, y, width, height, color.R, color.G, color.B);
        }

        /// <summary>
        /// Draws a filled rectangle at the given position with the given width and height and color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        public void DrawFilledRectangle(int x, int y, int width, int height, byte red, byte green, byte blue)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int startX = Math.Max(0, x);
            int startY = Math.Max(0, y);
            int endX = Math.Min(PixelsWidth, x + width);
            int endY = Math.Min(PixelsHeight, y + height);
            if (startX >= endX || startY >= endY)
            {
                return;
            }

            MarkOverlayDirty(startX, startY, endX - startX, endY - startY);
            uint packedColor = PackRgba(red, green, blue);
            uint* target = (uint*)_pixelBufferPointer.ToPointer();
            for (int targetY = startY; targetY < endY; targetY++)
            {
                int targetIndex = (targetY * PixelsWidth) + startX;
                for (int targetX = startX; targetX < endX; targetX++)
                {
                    target[targetIndex++] = packedColor;
                }
            }
        }

        /// <summary>
        /// Draws a filled rectangle at the given position with the given width and height and color and alpha value
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawFilledRectangleWithAlpha(int x, int y, int width, int height, Color color)
        {
            DrawFilledRectangleWithAlpha(x, y, width, height, color.R, color.G, color.B, color.A);
        }

        /// <summary>
        /// Draws a filled rectangle at the given position with the given width and height and color and alpha value
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        public void DrawFilledRectangleWithAlpha(int x, int y, int width, int height, byte red, byte green, byte blue, byte alpha)
        {
            if (width <= 0 || height <= 0 || alpha == 0)
            {
                return;
            }

            if (alpha == 255)
            {
                DrawFilledRectangle(x, y, width, height, red, green, blue);
                return;
            }

            int startX = Math.Max(0, x);
            int startY = Math.Max(0, y);
            int endX = Math.Min(PixelsWidth, x + width);
            int endY = Math.Min(PixelsHeight, y + height);
            if (startX >= endX || startY >= endY)
            {
                return;
            }

            MarkOverlayDirty(startX, startY, endX - startX, endY - startY);
            byte redAlpha = _alphaMulTable[alpha, red];
            byte greenAlpha = _alphaMulTable[alpha, green];
            byte blueAlpha = _alphaMulTable[alpha, blue];

            for (int targetY = startY; targetY < endY; targetY++)
            {
                int address = (targetY * _pixelStrideBytes) + (startX << 2);
                for (int targetX = startX; targetX < endX; targetX++)
                {
                    _pixels[address] = (byte)(redAlpha + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                    address++;
                    _pixels[address] = (byte)(greenAlpha + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                    address++;
                    _pixels[address] = (byte)(blueAlpha + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                    address++;
                    _pixels[address] = (byte)(alpha + _oneMinusAlphaMulTable[alpha, _pixels[address]]);
                    address++;
                }
            }
        }

        /// <summary>
        /// Draws a filled circle at the given position with the given radius and color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="radius">The radius value.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawFilledCircle(int x, int y, int radius, Color color)
        {
            DrawFilledCircle(x, y, radius, color.R, color.G, color.B);
        }

        /// <summary>
        /// Draws a filled circle at the given position with the given radius and color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="radius">The radius value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        public void DrawFilledCircle(int x, int y, int radius, byte red, byte green, byte blue)
        {
            int pixelsWidth_4 = (PixelsWidth << 2);
            for (int x2 = -radius; x2 < radius; x2++)
            {
                int xtest = x + x2;
                if (xtest < 0)
                {
                    continue;
                }
                if (xtest >= PixelsWidth)
                {
                    break;
                }
                int height = (int)Math.Sqrt(radius * radius - x2 * x2);
                int xox_4 = ((x2 + x) << 2);
                for (int y2 = -height; y2 < height; y2++)
                {
                    int ytest = y + y2;
                    if (ytest < 0)
                    {
                        continue;
                    }
                    if (ytest >= PixelsHeight)
                    {
                        break;
                    }
                    var address1 = (y2 + y) * pixelsWidth_4 + xox_4;
                    _pixels[address1] = red;
                    address1++;
                    _pixels[address1] = green;
                    address1++;
                    _pixels[address1] = blue;
                }
            }
        }

        /// <summary>
        /// Draws a filled circle at the given position with the given radius and color, and alpha value
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="radius">The radius value.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawFilledCircleWithAlpha(int x, int y, int radius, Color color)
        {
            DrawFilledCircleWithAlpha(x, y, radius, color.R, color.G, color.B, color.A);
        }

        /// <summary>
        /// Draws a filled circle at the given position with the given radius, color, and alpha value
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="radius">The radius value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        public void DrawFilledCircleWithAlpha(int x, int y, int radius, byte red, byte green, byte blue, byte alpha)
        {
            int pixelsWidth_4 = (PixelsWidth << 2);
            for (int x2 = -radius; x2 < radius; x2++)
            {
                int xtest = x + x2;
                if (xtest < 0)
                {
                    continue;
                }
                if (xtest >= PixelsWidth)
                {
                    break;
                }
                int height = (int)Math.Sqrt(radius * radius - x2 * x2);
                int xox_4 = ((x2 + x) << 2);
                for (int y2 = -height; y2 < height; y2++)
                {
                    int ytest = y + y2;
                    if (ytest < 0)
                    {
                        continue;
                    }
                    if (ytest >= PixelsHeight)
                    {
                        break;
                    }
                    var address1 = (y2 + y) * pixelsWidth_4 + xox_4;
                    _pixels[address1] = (byte)(_alphaMulTable[alpha, red] + _oneMinusAlphaMulTable[alpha, _pixels[address1]]);
                    address1++;
                    _pixels[address1] = (byte)(_alphaMulTable[alpha, green] + _oneMinusAlphaMulTable[alpha, _pixels[address1]]);
                    address1++;
                    _pixels[address1] = (byte)(_alphaMulTable[alpha, blue] + _oneMinusAlphaMulTable[alpha, _pixels[address1]]);
                }
            }
        }

        /// <summary>
        /// Draws a filled triangle at the given positions with the given color value
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawFilledTriangle(int x1, int y1, int x2, int y2, int x3, int y3, Color c)
        {
            DrawFilledTriangle(x1, y1, x2, y2, x3, y3, c.R, c.G, c.B);
        }

        /// <summary>
        /// Draws a filled triangle at the given positions with the given color value
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        public void DrawFilledTriangle(int x1, int y1, int x2, int y2, int x3, int y3, byte red, byte green, byte blue)
        {
            SortIntegerVectors(ref x1, ref y1, ref x2, ref y2, ref x3, ref y3);

            if (y2 == y3)
            {
                FillBottomFlatTriangle(x1, y1, x2, y2, x3, y3, red, green, blue);
            }
            else if (y1 == y2)
            {
                FillTopFlatTriangle(x1, y1, x2, y2, x3, y3, red, green, blue);
            }
            else
            {
                int x4 = x1 + (int)((y2 - y1) / (float)(y3 - y1)) * (x3 - x1);
                int y4 = y2;
                FillBottomFlatTriangle(x1, y1, x2, y2, x4, y4, red, green, blue);
                FillTopFlatTriangle(x2, y2, x4, y4, x3, y3, red, green, blue);
            }
        }

        /// <summary>
        /// Helper method to draw a bottom flat triangle
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        private void FillBottomFlatTriangle(int x1, int y1, int x2, int y2, int x3, int y3, byte red, byte green, byte blue)
        {
            float invslope1 = (x2 - x1) / (float)(y2 - y1);
            float invslope2 = (x3 - x1) / (float)(y3 - y1);

            float curx1 = x1;
            float curx2 = x1;

            for (int scanlineY = y1; scanlineY <= y2; scanlineY++)
            {
                DrawLine((int)curx1, scanlineY, (int)curx2, scanlineY, red, green, blue);
                curx1 += invslope1;
                curx2 += invslope2;
            }
        }

        /// <summary>
        /// Helper method to draw a top flat triangle
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        private void FillTopFlatTriangle(int x1, int y1, int x2, int y2, int x3, int y3, byte red, byte green, byte blue)
        {
            float invslope1 = (x3 - x1) / (float)(y3 - y1);
            float invslope2 = (x3 - x2) / (float)(y3 - y2);

            float curx1 = x3;
            float curx2 = x3;

            for (int scanlineY = y3; scanlineY > y1; scanlineY--)
            {
                DrawLine((int)curx1, scanlineY, (int)curx2, scanlineY, red, green, blue);
                curx1 -= invslope1;
                curx2 -= invslope2;
            }
        }

        /// <summary>
        /// Draws a filled triangle at the given positions with the given color value and alpha
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="color">Color value to draw.</param>
        public void DrawFilledTriangleWithAlpha(int x1, int y1, int x2, int y2, int x3, int y3, Color c)
        {
            DrawFilledTriangleWithAlpha(x1, y1, x2, y2, x3, y3, c.R, c.G, c.B, c.A);
        }

        /// <summary>
        /// Draws a filled triangle at the given positions with the given color value and alpha
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        public void DrawFilledTriangleWithAlpha(int x1, int y1, int x2, int y2, int x3, int y3, byte red, byte green, byte blue, byte alpha)
        {
            SortIntegerVectors(ref x1, ref y1, ref x2, ref y2, ref x3, ref y3);

            if (y2 == y3)
            {
                FillBottomFlatTriangleWithAlpha(x1, y1, x2, y2, x3, y3, red, green, blue, alpha);
            }
            else if (y1 == y2)
            {
                FillTopFlatTriangleWithAlpha(x1, y1, x2, y2, x3, y3, red, green, blue, alpha);
            }
            else
            {
                int x4 = x1 + (int)((y2 - y1) / (float)(y3 - y1)) * (x3 - x1);
                int y4 = y2;
                FillBottomFlatTriangleWithAlpha(x1, y1, x2, y2, x4, y4, red, green, blue, alpha);
                FillTopFlatTriangleWithAlpha(x2, y2, x4, y4, x3, y3, red, green, blue, alpha);
            }           
        }

        /// <summary>
        /// Helper method to draw a bottom flat triangle
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        private void FillBottomFlatTriangleWithAlpha(int x1, int y1, int x2, int y2, int x3, int y3, byte red, byte green, byte blue, byte alpha)
        {
            float invslope1 = (x2 - x1) / (float)(y2 - y1);
            float invslope2 = (x3 - x1) / (float)(y3 - y1);

            float curx1 = x1;
            float curx2 = x1;

            for (int scanlineY = y1; scanlineY <= y2; scanlineY++)
            {
                DrawLineWithAlpha((int)curx1, scanlineY, (int)curx2, scanlineY, red, green, blue, alpha);
                curx1 += invslope1;
                curx2 += invslope2;
            }
        }

        /// <summary>
        /// Helper method to draw a top flat triangle
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="alpha">Alpha color channel.</param>
        private void FillTopFlatTriangleWithAlpha(int x1, int y1, int x2, int y2, int x3, int y3, byte red, byte green, byte blue, byte alpha)
        {
            float invslope1 = (x3 - x1) / (float)(y3 - y1);
            float invslope2 = (x3 - x2) / (float)(y3 - y2);

            float curx1 = x3;
            float curx2 = x3;

            for (int scanlineY = y3; scanlineY > y1; scanlineY--)
            {
                DrawLineWithAlpha((int)curx1, scanlineY, (int)curx2, scanlineY, red, green, blue, alpha);
                curx1 -= invslope1;
                curx2 -= invslope2;
            }
        }

        /// <summary>
        /// Helper method to sort three integer "vectors" by y-coordinates
        /// </summary>
        /// <param name="x1">End X coordinate in pixels.</param>
        /// <param name="y1">End Y coordinate in pixels.</param>
        /// <param name="x2">Source X coordinate in pixels.</param>
        /// <param name="y2">Source Y coordinate in pixels.</param>
        /// <param name="x3">The x3 value.</param>
        /// <param name="y3">The y3 value.</param>
        private void SortIntegerVectors(ref int x1, ref int y1, ref int x2, ref int y2, ref int x3, ref int y3)
        {
            if (y2 < y1)
            {
                SwapIntegers(ref y1, ref y2);
                SwapIntegers(ref x1, ref x2);
            }
            if (y3 < y1)
            {
                SwapIntegers(ref y1, ref y3);
                SwapIntegers(ref x1, ref x3);
            }
            if (y3 < y2)
            {
                SwapIntegers(ref y3, ref y2);
                SwapIntegers(ref x3, ref x2);
            }

            void SwapIntegers(ref int a, ref int b)
            {
                int temp = a;
                a = b;
                b = temp;
            }
        }


        /// <summary>
        /// Draws text on the given x,y coordinate using the given color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="text">The text value.</param>
        /// <param name="fontsize">The fontsize value.</param>
        /// <param name="color">Color value to draw.</param>
        /// <param name="font">The font value.</param>
        public void DrawText(int x, int y, string text, int fontsize, Color color, Font font = null)
        {
            DrawText(x, y, text, fontsize, color.R, color.G, color.B, font);
        }

        /// <summary>
        /// Draws text on the given x,y coordinate using the given color
        /// </summary>
        /// <param name="x">Target X coordinate in pixels.</param>
        /// <param name="y">Target Y coordinate in pixels.</param>
        /// <param name="text">The text value.</param>
        /// <param name="fontsize">The fontsize value.</param>
        /// <param name="red">Red color channel.</param>
        /// <param name="green">Green color channel.</param>
        /// <param name="blue">Blue color channel.</param>
        /// <param name="font">The font value.</param>
        public void DrawText(int x, int y, string text, int fontsize, byte red, byte green, byte blue, Font font = null)
        {
            _drawTexts.Enqueue(new DrawTextObject() { X = x, Y = y, Text = text, Fontsize = fontsize, Red = red, Green = green, Blue = blue, Font = font });
        }

        #endregion

        /// <summary>
        /// Handles the toggle fullscreen operation.
        /// </summary>
        public void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                WindowState = WindowState.Normal;
                WindowBorder = _windowedBorder;
                if (_hasWindowedBounds)
                {
                    Location = _windowedLocation;
                    ClientSize = _windowedClientSize;
                }

                if (_windowedState != WindowState.Fullscreen)
                {
                    WindowState = _windowedState;
                }
            }
            else
            {
                _windowedState = WindowState;
                _windowedBorder = WindowBorder;
                _windowedClientSize = ClientSize;
                _windowedLocation = Location;
                _hasWindowedBounds = _windowedClientSize.X > 0 && _windowedClientSize.Y > 0;

                WindowBorder = WindowBorder.Hidden;
                WindowState = WindowState.Fullscreen;
            }
            _isFullscreen = !_isFullscreen;
        }
    }

    /// <summary>
    /// Represents the key event args component.
    /// </summary>
    public class KeyEventArgs
    {
        /// <summary>
        /// Gets the OpenTK key value.
        /// </summary>
        public Key Key { get; }
        /// <summary>
        /// Gets the active key modifiers.
        /// </summary>
        public KeyModifiers Modifiers { get; }

        /// <summary>
        /// Initializes a new KeyEventArgs instance.
        /// </summary>
        public KeyEventArgs(Key key, KeyModifiers keyModifiers)
        {
            Key = key;
            Modifiers = keyModifiers;
        }
    }
}
