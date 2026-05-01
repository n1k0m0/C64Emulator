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
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.ES30;
using OpenTK.Input;
using SharpPixels.Shaders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace SharpPixels
{
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
        /// StartTime of fps counter; is resettet each 1 second
        /// </summary>
        private DateTime _nextFPSTime = DateTime.Now;
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


        private int _vertexBufferObject;
        private int _textureHandle;
        private int _texCoordLocation;
        private int _textureUniformLocation;
        private Shader _shader;
        private byte[] _pixels;
        private readonly List<Bitmap> _expiredBitmapScratch = new List<Bitmap>();
        private readonly int _pixelByteCount;
        private readonly int _pixelStrideBytes;

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
        public SharpPixelsWindow(int width, int height, string title) : base(width, height, GraphicsMode.Default, title)
        {
            PixelsWidth = width;
            PixelsHeight = height;
            _pixelStrideBytes = PixelsWidth << 2;
            _pixelByteCount = PixelsHeight * _pixelStrideBytes;
            _pixels = new byte[PixelsWidth * (PixelsHeight << 2)];
            InitializePixelAlphaChannel();
            UpdateTextureCoordinates();
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
        /// Handles the on load operation.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {             
            GL.ClearColor(0f, 0f, 0f, 1.0f);
            
            CursorVisible = false;            

            //add mouse and keyboard handlers
            MouseMove += SharpPixels_MouseMove;
            MouseDown += SharpPixels_MouseClick;
            KeyDown += SharpPixels_KeyDown;
            KeyUp += SharpPixels_KeyUp;

            for (int i = 0; i < 256; i++)
            {
                float alpha = (float)(i / 255.0);
                for (int j = 0; j < 256; j++)
                {
                    _alphaMulTable[i, j] = (byte)(alpha * j);
                    _oneMinusAlphaMulTable[i, j] = (byte)((1.0 - alpha) * j);
                }
            }

            _vertexBufferObject = GL.GenBuffer();
            _textureHandle = GL.GenTexture();
            _shader = new Shader(Properties.Resources.vert, Properties.Resources.frag);
            _shader.Compile();
            _shader.Use();
            _texCoordLocation = GL.GetAttribLocation(_shader.Handle, "aTexCoord");
            _textureUniformLocation = GL.GetUniformLocation(_shader.Handle, "texture0");
            if (_textureUniformLocation >= 0)
            {
                GL.Uniform1(_textureUniformLocation, 0);
            }
            _drawingStopwatch.Start();

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _textureHandle);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba, PixelsWidth, PixelsHeight, 0, OpenTK.Graphics.ES30.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

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

            base.OnLoad(e);
        }
        

        /// <summary>
        /// Handles the on unload operation.
        /// </summary>
        protected override void OnUnload(EventArgs e)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.DeleteBuffer(_vertexBufferObject);
            if (_textureHandle != 0)
            {
                GL.DeleteTexture(_textureHandle);
            }

            _shader.Dispose();

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

            base.OnUnload(e);
        }

        /// <summary>
        /// Renders the current pixel buffer and queued text overlays.
        /// </summary>
        /// <param name="frameEventArgs">Frame timing arguments supplied by OpenTK.</param>
        protected override void OnRenderFrame(FrameEventArgs frameEventArgs)
        {
            //call the user update method where the user updates the graphics etc
            OnUserUpdate(frameEventArgs.Time);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _textureHandle);
            GL.TexSubImage2D(TextureTarget2d.Texture2D, 0, 0, 0, PixelsWidth, PixelsHeight, OpenTK.Graphics.ES30.PixelFormat.Rgba, PixelType.UnsignedByte, _pixels);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            if (_texCoordLocation >= 0)
            {
                GL.VertexAttribPointer(_texCoordLocation, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
                GL.EnableVertexAttribArray(_texCoordLocation);
            }

            _shader.Use();
            
            GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Length / 5);

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
            //output default title of the app with fps
            if (dateTimeNow >= _nextFPSTime)
            {
                FPS = _fpsCounter;
                Title = string.Format("FPS: {0}", FPS);
                _fpsCounter = 0;
                _nextFPSTime = _nextFPSTime.AddSeconds(1);
                
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
            
            _fpsCounter++;         

            Context.SwapBuffers();
            base.OnRenderFrame(frameEventArgs);
        }

        /// <summary>
        /// Handles the on resize operation.
        /// </summary>
        protected override void OnResize(EventArgs e)
        {
            GL.Viewport(0, 0, Width, Height);
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
            if (!_bitmapMemories.ContainsKey(bitmap))
            {
                _bitmapMemories.Add(bitmap, new BitmapMemory(bitmap));
            }
            _bitmapMemories[bitmap].ExpirationTime = DateTime.Now.AddSeconds(1);
            return _bitmapMemories[bitmap].BitmapPointer;
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
            if (_bitmapMemories.ContainsKey(bitmap))
            {
                var bitmapMemory = _bitmapMemories[bitmap];
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
        private void SharpPixels_MouseMove(object sender, MouseMoveEventArgs e)
        {
            OnUserMouseMove(e.Position.X, e.Position.Y);
        }

        /// <summary>
        /// MouseClick event handling
        /// Computes the x and y coordinate in the pixel space and calls the MouseClick method with these
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseClick(object sender, MouseButtonEventArgs e)
        {
            OnUserMouseClick(e.Position.X, e.Position.Y, e.Mouse);
        }

        /// <summary>
        /// User released a key, thus, we call the OnUserKeyUp
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_KeyUp(object sender, KeyboardKeyEventArgs e)
        {
            if (keysPressed.Contains(e.Key))
            {
                keysPressed.Remove(e.Key);
                modifiersPressed = e.Modifiers;
                OnUserKeyUp(new KeyEventArgs(e.Key, e.Modifiers));
            }
        }

        /// <summary>
        /// User holds a key, thus, we call the OnUserKeyDown
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_KeyDown(object sender, KeyboardKeyEventArgs e)
        {
            if(!keysPressed.Contains(e.Key))
            {
                keysPressed.Add(e.Key);
                modifiersPressed = e.Modifiers;
                OnUserKeyDown(new KeyEventArgs(e.Key, e.Modifiers));
            }
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

        #endregion

        #region public methods

        /// <summary>
        /// Clears the screen (= all pixels black)
        /// </summary>
        public void ClearScreen()
        {
            Array.Clear(_pixels, 0, _pixelByteCount);
            InitializePixelAlphaChannel();
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
            var address = (y * _pixelStrideBytes) + (x << 2);
            _pixels[address] = red;
            address++;
            _pixels[address] = green;
            address++;
            _pixels[address] = blue;
            address++;
            _pixels[address] = 255;
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
            _pixels[address] = 255;
        }

        /// <summary>
        /// Handles the initialize pixel alpha channel operation.
        /// </summary>
        private void InitializePixelAlphaChannel()
        {
            for (int i = 3; i < _pixels.Length; i += 4)
            {
                _pixels[i] = 255;
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
                for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
                {
                    int sourceRow = sourceY * sourceWidth;
                    int targetY = destinationY + (sourceY * scale);
                    for (int yRepeat = 0; yRepeat < scale; yRepeat++)
                    {
                        int address = ((targetY + yRepeat) * _pixelStrideBytes) + (destinationX << 2);
                        for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                        {
                            uint argb = sourcePixels[sourceRow + sourceX];
                            byte red = (byte)((argb >> 16) & 0xFF);
                            byte green = (byte)((argb >> 8) & 0xFF);
                            byte blue = (byte)(argb & 0xFF);
                            for (int xRepeat = 0; xRepeat < scale; xRepeat++)
                            {
                                _pixels[address++] = red;
                                _pixels[address++] = green;
                                _pixels[address++] = blue;
                                _pixels[address++] = 255;
                            }
                        }
                    }
                }

                return;
            }

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
                    int targetRowAddress = (targetY + yRepeat) * _pixelStrideBytes;
                    for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                    {
                        int targetX = destinationX + (sourceX * scale);
                        int xRepeatStart = Math.Max(0, -targetX);
                        int xRepeatEnd = Math.Min(scale, PixelsWidth - targetX);
                        if (xRepeatEnd <= xRepeatStart)
                        {
                            continue;
                        }

                        uint argb = sourcePixels[sourceRow + sourceX];
                        byte red = (byte)((argb >> 16) & 0xFF);
                        byte green = (byte)((argb >> 8) & 0xFF);
                        byte blue = (byte)(argb & 0xFF);
                        int address = targetRowAddress + ((targetX + xRepeatStart) << 2);
                        for (int xRepeat = xRepeatStart; xRepeat < xRepeatEnd; xRepeat++)
                        {
                            _pixels[address++] = red;
                            _pixels[address++] = green;
                            _pixels[address++] = blue;
                            _pixels[address++] = 255;
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

            for (int y = 0; y < PixelsHeight; y++)
            {
                int sourceY = (y * sourceHeight) / PixelsHeight;
                int sourceRow = sourceY * sourceWidth;
                int address = y * _pixelStrideBytes;
                for (int x = 0; x < PixelsWidth; x++)
                {
                    int sourceX = (x * sourceWidth) / PixelsWidth;
                    uint argb = sourcePixels[sourceRow + sourceX];
                    _pixels[address++] = (byte)((argb >> 16) & 0xFF);
                    _pixels[address++] = (byte)((argb >> 8) & 0xFF);
                    _pixels[address++] = (byte)(argb & 0xFF);
                    _pixels[address++] = 255;
                }
            }
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

            for (int targetY = startY; targetY < endY; targetY++)
            {
                int address = (targetY * _pixelStrideBytes) + (startX << 2);
                for (int targetX = startX; targetX < endX; targetX++)
                {
                    _pixels[address++] = red;
                    _pixels[address++] = green;
                    _pixels[address++] = blue;
                    _pixels[address++] = 255;
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
                    address += 2;
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
                WindowBorder = WindowBorder.Resizable;
                WindowState = WindowState.Normal;
                ClientSize = new Size(PixelsWidth, PixelsHeight);
            }
            else
            {
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
