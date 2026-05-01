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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static SharpPixels.User32;

namespace SharpPixels
{
    /// <summary>
    /// Wrapper class for user32 win api
    /// </summary>
    internal class User32
    {
        [DllImport("user32.dll")]
        public static extern int EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll")]
        public static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);
        [DllImport("User32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("User32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int CDS_UPDATEREGISTRY = 0x01;
        public const int CDS_TEST = 0x02;
        public const int DISP_CHANGE_SUCCESSFUL = 0;
        public const int DISP_CHANGE_RESTART = 1;
        public const int DISP_CHANGE_FAILED = -1;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;

            public short dmOrientation;
            public short dmPaperSize;
            public short dmPaperLength;
            public short dmPaperWidth;

            public short dmScale;
            public short dmCopies;
            public short dmDefaultSource;
            public short dmPrintQuality;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public short dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;

            public int dmDisplayFlags;
            public int dmDisplayFrequency;

            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;

            public int dmPanningWidth;
            public int dmPanningHeight;
        };
    }

    /// <summary>
    /// Wrapper class for gdi32 win api
    /// </summary>
    internal class Gdi32
    {
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, TernaryRasterOperations dwRop);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, TernaryRasterOperations dwRop);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

        public enum TernaryRasterOperations : uint
        {
            SRCCOPY = 0x00CC0020,
            SRCPAINT = 0x00EE0086,
            SRCAND = 0x008800C6,
            SRCINVERT = 0x00660046,
            SRCERASE = 0x00440328,
            NOTSRCCOPY = 0x00330008,
            NOTSRCERASE = 0x001100A6,
            MERGECOPY = 0x00C000CA,
            MERGEPAINT = 0x00BB0226,
            PATCOPY = 0x00F00021,
            PATPAINT = 0x00FB0A09,
            PATINVERT = 0x005A0049,
            DSTINVERT = 0x00550009,
            BLACKNESS = 0x00000042,
            WHITENESS = 0x00FF0062,
            CAPTUREBLT = 0x40000000
        }
    }

    /// <summary>
    /// We use a BitmapMemory to "memorize" the bitmap pointer to the bitmap's pixel data
    /// Also, we lock the bitmap once, thus, this does not cost performance during each cycle if we have hundrets of bitmaps
    /// </summary>
    internal unsafe class BitmapMemory : IDisposable
    {
        public Bitmap Bitmap { get; private set; }
        public byte* BitmapPointer { get; private set; }

        private BitmapData _bitmapData;

        /// <summary>
        /// Creates a new BitmapMemory for the given bitmap
        /// </summary>
        /// <param name="bitmap">Bitmap to draw or cache.</param>
        public BitmapMemory(Bitmap bitmap)
        {
            Bitmap = bitmap;
            _bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
    public abstract unsafe partial class SharpPixelsWindow : IDisposable
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
        /// Memorize the last total elapsed time in miliseconds
        /// </summary>
        private long _totalElapsedMiliseconds = 0;
        /// <summary>
        /// Bitmap the pixels are drawn on
        /// </summary>
        private Bitmap _bitmap;
        /// <summary>
        /// Data of the bitmap
        /// </summary>
        private BitmapData _bitmapData;
        /// <summary>
        /// Point to the data of the bitmap
        /// </summary>
        private byte* _bitmapPointer;
        /// <summary>
        /// Flag that indicates if we are in fullscreen or windowed
        /// </summary>
        private bool _fullScreen = false;
        /// <summary>
        /// Rectangle used for locking the image we are drawing on
        /// </summary>
        private Rectangle _lockRectangle;
        /// <summary>
        /// StartTime of fps counter; is resettet each 1 second
        /// </summary>
        private DateTime _startFPSTime = DateTime.Now;
        /// <summary>
        /// Counter for fps
        /// </summary>
        private int _fpsCounter = 0;
        /// <summary>
        /// Queue for DrawText objects; theses texts are drawn at end of frame
        /// </summary>
        private Queue<DrawTextObject> _drawTexts = new Queue<DrawTextObject>();
        /// <summary>
        /// Width of windows resolution 
        /// </summary>
        private int windowsScreenWidth;
        /// <summary>
        /// Height of windows resolution
        /// </summary>
        private int windowsScreenHeight;
        /// <summary>
        /// This dictionary stores pointer to the raw bitmap data
        /// </summary>
        private Dictionary<Bitmap, BitmapMemory> _bitmapMemories = new Dictionary<Bitmap, BitmapMemory>();
        /// <summary>
        /// Table for fast alpha blending computation
        /// </summary>
        private static byte[,] _alphaMulTable = new byte[256, 256];
        private static byte[,] _oneMinusAlphaMulTable = new byte[256, 256];

        /// <summary>
        /// Width used for drawing
        /// </summary>
        public int ScreenWidth
        {
            get;
            set;
        }

        /// <summary>
        /// Height used for drawing
        /// </summary>
        public int ScreenHeight
        {
            get;
            set;
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
        /// Enables or disables the fullscreen mode
        /// </summary>
        public bool FullScreen
        {
            set
            {
                _fullScreen = value;
                if (_fullScreen == true)
                {
                    WindowState = FormWindowState.Normal;
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                    Bounds = new Rectangle(0, 0, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
                    ChangeScreenResolution(ScreenWidth, ScreenHeight);
                }
                else
                {
                    WindowState = FormWindowState.Maximized;
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
                }
            }
            get
            {
                return _fullScreen;
            }
        }

        /// <summary>
        /// Indicates, if the windows resolution should be changed
        /// </summary>
        public bool EnableScreenResolutionChange
        {
            get;
            set;
        } = false;

        /// <summary>
        /// Revert user's resolution, when we changed it
        /// </summary>
        /// <param name="disposing">The disposing value.</param>
        protected override void Dispose(bool disposing)
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen.Bounds.Width != windowsScreenWidth || screen.Bounds.Height != windowsScreenHeight)
            {
                ChangeScreenResolution(windowsScreenWidth, windowsScreenHeight);
            }

            base.Dispose(disposing);
        }

        public new void Dispose()
        {
            Dispose(true);

            foreach (var bitmapMemory in _bitmapMemories.Values)
            {
                try
                {
                    bitmapMemory.Dispose();
                }
                catch (Exception ex)
                {
                    //can not do much here
                }
            }
            base.Dispose();
        }

        /// <summary>
        /// Initializes our ui. Also calls the OnUserInitialize() method where the user
        /// may initialize several things
        /// </summary>
        private void InitializeComponent()
        {
            //Store user's screen resolution
            Screen screen = Screen.PrimaryScreen;
            windowsScreenWidth = screen.Bounds.Width;
            windowsScreenHeight = screen.Bounds.Height;

            //Set virtual screen width and height
            ScreenWidth = Width;
            ScreenHeight = Height;

            //add mouse and keyboard handlers
            MouseMove += SharpPixels_MouseMove;
            MouseEnter += SharpPixels_MouseEnter;
            MouseLeave += SharpPixels_MouseLeave;
            MouseClick += SharpPixels_MouseClick;
            KeyDown += SharpPixels_KeyDown;
            KeyUp += SharpPixels_KeyUp;

            //call init method of user
            OnUserInitialize();

            //create bitmap which engine uses for drawing
            _bitmap = new Bitmap(ScreenWidth, ScreenHeight);
            _lockRectangle = new Rectangle(0, 0, _bitmap.Width, _bitmap.Height);

            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);

            //initialize alpha and alpha lookup tables
            //used in DrawBitmapWithAlpha
            for (int i = 0; i < 256; i++)
            {
                float alpha = (float)(i / 255.0);
                for (int j = 0; j < 256; j++)
                {
                    _alphaMulTable[i, j] = (byte)(alpha * j);
                    _oneMinusAlphaMulTable[i, j] = (byte)((1.0 - alpha) * j);
                }
            }
            _drawingStopwatch.Start();
        }

        /// <summary>
        /// Called in endless loop
        /// Paints everything the user drawed onto the screen using StretchBlt
        /// </summary>
        /// <param name="paintEventArgs">The paintEventArgs value.</param>
        protected override void OnPaint(PaintEventArgs paintEventArgs)
        {
            //compute elapsed time of last frame
            var elapsedTimeMiliseconds = _drawingStopwatch.ElapsedMilliseconds - _totalElapsedMiliseconds;
            _totalElapsedMiliseconds = _drawingStopwatch.ElapsedMilliseconds;

            //lock bitmap to draw on
            _bitmapData = _bitmap.LockBits(_lockRectangle, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            _bitmapPointer = (byte*)_bitmapData.Scan0.ToPointer();

            //output default title of the app with fps
            if (DateTime.Now >= _startFPSTime.AddSeconds(1))
            {
                FPS = _fpsCounter;
                Text = string.Format("FPS: {0}", FPS);
                _fpsCounter = 0;
                _startFPSTime = DateTime.Now;

                //every second, we call garbage collector
                GC.Collect();
            }

            //call the user update method where the user updates the graphics etc
            OnUserUpdate(elapsedTimeMiliseconds);

            //unlock bitmap which was drawn on
            _bitmap.UnlockBits(_bitmapData);

            //We draw the text in a second step; this increases our fps
            //one drawback: Text is drawn over everything
            if (_drawTexts.Count > 0)
            {
                var graphics = Graphics.FromImage(_bitmap);
                graphics.InterpolationMode = InterpolationMode.High;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                while (_drawTexts.Count > 0)
                {
                    var textObject = _drawTexts.Dequeue();
                    var brush = new SolidBrush(Color.FromArgb(textObject.Red, textObject.Green, textObject.Blue));
                    if (textObject.Font == null)
                    {
                        textObject.Font = new Font(FontFamily.GenericMonospace, textObject.Fontsize, FontStyle.Regular);
                    }
                    graphics.DrawString(textObject.Text, textObject.Font, brush, textObject.X, textObject.Y);
                }
            }

            //copy everything from bitmap to graphics using windows api
            var pTarget = paintEventArgs.Graphics.GetHdc();
            var pSource = Gdi32.CreateCompatibleDC(pTarget);
            var hbitmap = _bitmap.GetHbitmap();
            var pOrig = Gdi32.SelectObject(pSource, hbitmap);

            if (FullScreen == true && EnableScreenResolutionChange)
            {
                //if we changed the screen to the resolution, we can use bitBlt, which is much faster
                Gdi32.BitBlt(pTarget, 0, 0, Width, Height, pSource, 0, 0, Gdi32.TernaryRasterOperations.SRCCOPY);
            }
            else
            {
                //We need to stretch the image since the user may have a much higher resolution, thus we use stretchBlt
                Gdi32.StretchBlt(pTarget, 0, 0, Width, Height, pSource, 0, 0, ScreenWidth, ScreenHeight, Gdi32.TernaryRasterOperations.SRCCOPY);
            }

            //housekeep
            Gdi32.DeleteDC(pSource);
            Gdi32.DeleteObject(pOrig);
            Gdi32.DeleteObject(hbitmap);
            paintEventArgs.Graphics.ReleaseHdc(pTarget);

            //increment our fps counter each frame by one
            _fpsCounter++;

            base.OnPaint(paintEventArgs);

            //invalidate window to force redrawing => endless loop                  
            Invalidate(true);
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
        /// Hide the mouse, when it is over the application
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseEnter(object sender, EventArgs e)
        {
            System.Windows.Forms.Cursor.Hide();
        }

        /// <summary>
        /// Show the mouse, when it leaves the application (only in window mode)
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseLeave(object sender, EventArgs e)
        {
            if (!FullScreen)
            {
                System.Windows.Forms.Cursor.Show();
            }
        }

        /// <summary>
        /// MouseMove event handling
        /// Computes the x and y coordinate in the pixel space and calls the OnMouseMove method with these
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseMove(object sender, MouseEventArgs e)
        {
            int x, y;
            if (EnableScreenResolutionChange && FullScreen)
            {
                x = e.X;
                y = e.Y;
            }
            else
            {
                x = (int)(e.X / ((double)Width) * ScreenWidth);
                y = (int)(e.Y / ((double)Height) * ScreenHeight);
            }
            OnUserMouseMove((int)x, (int)y);
        }

        /// <summary>
        /// MouseClick event handling
        /// Computes the x and y coordinate in the pixel space and calls the MouseClick method with these
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_MouseClick(object sender, MouseEventArgs e)
        {
            int x, y;
            if (EnableScreenResolutionChange && FullScreen)
            {
                x = e.X;
                y = e.Y;
            }
            else
            {
                x = (int)(e.X / ((double)Width) * ScreenWidth);
                y = (int)(e.Y / ((double)Height) * ScreenHeight);
            }
            OnUserMouseClick((int)x, (int)y, e.Button);
        }

        /// <summary>
        /// User released a key, thus, we call the OnUserKeyUp
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_KeyUp(object sender, KeyEventArgs e)
        {
            OnUserKeyUp(e);
        }

        /// <summary>
        /// User presses a key, thus, we call the OnUserKeyDown
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void SharpPixels_KeyDown(object sender, KeyEventArgs e)
        {
            OnUserKeyDown(e);
        }

        /// <summary>
        /// Changes the windows screen resolution to the given settings
        /// </summary>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        private void ChangeScreenResolution(int width, int height)
        {
            if (!EnableScreenResolutionChange)
            {
                return;
            }

            var devmode = new DEVMODE();
            devmode.dmDeviceName = new String(new char[32]);
            devmode.dmFormName = new String(new char[32]);
            devmode.dmSize = (short)Marshal.SizeOf(devmode);

            if (User32.EnumDisplaySettings(null, User32.ENUM_CURRENT_SETTINGS, ref devmode) != 0)
            {
                devmode.dmPelsWidth = width;
                devmode.dmPelsHeight = height;
                var changeDisplaySettingsReturnCode = User32.ChangeDisplaySettings(ref devmode, User32.CDS_TEST);

                if (changeDisplaySettingsReturnCode == User32.DISP_CHANGE_FAILED)
                {
                    //failed to change
                    //fallback to not changing screen resolution
                    EnableScreenResolutionChange = false;
                }
                else
                {
                    changeDisplaySettingsReturnCode = User32.ChangeDisplaySettings(ref devmode, User32.CDS_UPDATEREGISTRY);
                    switch (changeDisplaySettingsReturnCode)
                    {
                        case User32.DISP_CHANGE_SUCCESSFUL:
                            {
                                break;
                                //successfull change
                            }
                        default:
                            {
                                //failed to change
                                //fallback to not changing screen resolution
                                EnableScreenResolutionChange = false;
                                break;
                            }
                    }
                }
            }
        }

        #region abstract members the user of the engine has to override

        public abstract void OnUserUpdate(long elapsedMiliseconds);
        public abstract void OnUserInitialize();
        public abstract void OnUserMouseMove(int x, int y);
        public abstract void OnUserMouseClick(int x, int y, MouseButtons buttons);
        public abstract void OnUserKeyDown(KeyEventArgs e);
        public abstract void OnUserKeyUp(KeyEventArgs e);

        #endregion

        #region public methods

        /// <summary>
        /// Clears the screen (= all pixels black)
        /// </summary>
        public void ClearScreen()
        {
            int max = ScreenWidth * ScreenHeight * 3;
            for (int i = 0; i < max; i++)
            {
                _bitmapPointer[i] = (byte)0;
            }
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
            if (x < 0 || x >= ScreenWidth || y < 0 || y >= ScreenHeight)
            {
                return;
            }
            var address = y * 3 * ScreenWidth + x * 3;
            _bitmapPointer[address] = blue;
            address++;
            _bitmapPointer[address] = green;
            address++;
            _bitmapPointer[address] = red;
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
            if (x < 0 || x >= ScreenWidth || y < 0 || y >= ScreenHeight)
            {
                return;
            }           
            var address = y * 3 * ScreenWidth + x * 3;
            _bitmapPointer[address] = (byte)(_alphaMulTable[alpha, blue] + _oneMinusAlphaMulTable[alpha, _bitmapPointer[address]]);
            address++;
            _bitmapPointer[address] = (byte)(_alphaMulTable[alpha, green] + _oneMinusAlphaMulTable[alpha, _bitmapPointer[address]]);
            address++;
            _bitmapPointer[address] = (byte)(_alphaMulTable[alpha, red] + _oneMinusAlphaMulTable[alpha, _bitmapPointer[address]]);
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

            int ScreenWidth_3 = ScreenWidth * 3;

            while (true)
            {
                if (x0 >= 0 && x0 < ScreenWidth && y0 >= 0 && y0 < ScreenHeight)
                {
                    var address = y0 * ScreenWidth_3 + x0 * 3;
                    _bitmapPointer[address] = blue;
                    address++;
                    _bitmapPointer[address] = green;
                    address++;
                    _bitmapPointer[address] = red;
                }
                if (x0 == x1 && y0 == y1) break;
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
            var pixelsWidth = ScreenWidth;
            var pixelsHeight = ScreenHeight;
            var width_4 = width * 4;
            var pixelsWidth_3 = pixelsWidth * 3;

            for (var x0 = 0; x0 < width; x0++)
            {
                x1 = x0 + x;
                //check bounds
                if (x1 < 0)
                {
                    continue;
                }
                if (x1 >= pixelsWidth)
                {
                    continue;
                }

                var x0_4 = x0 * 4;
                var x1_3 = x1 * 3;
                for (var y0 = 0; y0 < height; y0++)
                {
                    y1 = y0 + y;
                    //check bounds
                    if (y1 < 0)
                    {
                        continue;
                    }
                    if (y1 >= pixelsHeight)
                    {
                        break;
                    }
                    var address0 = y0 * width_4 + x0_4;
                    var address1 = y1 * pixelsWidth_3 + x1_3;

                    _bitmapPointer[address1] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = bitmapPointer[address0];
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
            var pixelsWidth = ScreenWidth;
            var pixelsHeight = ScreenHeight;
            var bitmapWidth = bitmap.Width;
            var bitmapHeight = bitmap.Height;
            var bitmapWidth_4 = bitmapWidth * 4;
            var pixelsWidth_3 = pixelsWidth * 3;
            var x2_sourceWidth = x2 + sourceWidth;
            var y2_sourceHeight = y2 + sourceHeight;

            for (var x0 = x2; x0 < x2_sourceWidth; x0++)
            {
                x1 = x0 - x2 + x;
                //check bounds
                if (x0 < 0 || x1 < 0)
                {
                    continue;
                }
                if (x0 >= bitmapWidth || x1 >= pixelsWidth)
                {
                    continue;
                }

                var x0_4 = x0 * 4;
                var x1_3 = x1 * 3;
                for (var y0 = y2; y0 < y2_sourceHeight; y0++)
                {
                    y1 = y0 - y2 + y;
                    //check bounds
                    if (y1 < 0 || y1 < 0)
                    {
                        continue;
                    }
                    if (y0 >= bitmapHeight || y1 >= pixelsHeight)
                    {
                        break;
                    }

                    var address0 = y0 * bitmapWidth_4 + x0_4;
                    var address1 = y1 * pixelsWidth_3 + x1_3;

                    _bitmapPointer[address1] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = bitmapPointer[address0];
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = bitmapPointer[address0];
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
            var pixelsWidth = ScreenWidth;
            var pixelsHeight = ScreenHeight;
            var width_4 = width * 4;
            var pixelsWidth_3 = pixelsWidth * 3;

            for (var x0 = 0; x0 < width; x0++)
            {
                x1 = x0 + x;
                //check bounds
                if (x1 < 0)
                {
                    continue;
                }
                if (x1 >= pixelsWidth)
                {
                    continue;
                }

                var x0_4 = x0 * 4;
                var x1_3 = x1 * 3;

                for (var y0 = 0; y0 < height; y0++)
                {
                    y1 = y0 + y;
                    //check bounds
                    if (y1 < 0)
                    {
                        continue;
                    }
                    if (y1 >= pixelsHeight)
                    {
                        continue;
                    }

                    var address0 = y0 * width_4 + x0_4;
                    var address1 = y1 * pixelsWidth_3 + x1_3;
                    var alphaIndex = bitmapPointer[address0 + 3];
                    //here, we use the lookup tables, which we precomputed, to do fast alpha blending
                    _bitmapPointer[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _bitmapPointer[address1]]);
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _bitmapPointer[address1]]);
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _bitmapPointer[address1]]);
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
            var pixelsWidth = ScreenWidth;
            var pixelsHeight = ScreenHeight;
            var bitmapWidth = bitmap.Width;
            var bitmapHeight = bitmap.Height;
            var bitmapWidth_4 = bitmapWidth * 4;
            var pixelsWidth_3 = pixelsWidth * 3;
            var x2_sourceWidth = x2 + sourceWidth;
            var y2_sourceHeight = y2 + sourceHeight;

            for (var x0 = x2; x0 < x2_sourceWidth; x0++)
            {
                x1 = x0 - x2 + x;
                //check bounds
                if (x0 < 0 || x1 < 0)
                {
                    continue;
                }
                if (x0 >= bitmapWidth || x1 >= pixelsWidth)
                {
                    continue;
                }

                var x0_4 = x0 * 4;
                var x1_3 = x1 * 3;
                for (var y0 = y2; y0 < y2_sourceHeight; y0++)
                {
                    y1 = y0 - y2 + y;
                    //check bounds
                    if (y1 < 0 || y1 < 0)
                    {
                        continue;
                    }
                    if (y0 >= bitmapHeight || y1 >= pixelsHeight)
                    {
                        break;
                    }

                    var address0 = y0 * bitmapWidth_4 + x0_4;
                    var address1 = y1 * pixelsWidth_3 + x1_3;
                    var alphaIndex = bitmapPointer[address0 + 3];

                    _bitmapPointer[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _bitmapPointer[address1]]);
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _bitmapPointer[address1]]);
                    address0++;
                    address1++;
                    _bitmapPointer[address1] = (byte)(_alphaMulTable[alphaIndex, bitmapPointer[address0]] + _oneMinusAlphaMulTable[alphaIndex, _bitmapPointer[address1]]);
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
            var pixelsWidth = ScreenWidth;
            var pixelsHeight = ScreenHeight;
            int x1, y1;
            int pixelsWidth_3 = pixelsWidth * 3;

            for (var x0 = 0; x0 < width; x0++)
            {
                x1 = x0 + x;
                //check bounds
                if (x1 < 0)
                {
                    continue;
                }
                if (x1 >= pixelsWidth)
                {
                    break;
                }

                var x1_3 = x1 * 3;
                for (var y0 = 0; y0 < height; y0++)
                {
                    y1 = y0 + y;
                    //check bounds
                    if (y1 < 0)
                    {
                        continue;
                    }
                    if (y1 >= pixelsHeight)
                    {
                        break;
                    }
                    var address1 = y1 * pixelsWidth_3 + x1_3;
                    _bitmapPointer[address1] = blue;
                    address1++;
                    _bitmapPointer[address1] = green;
                    address1++;
                    _bitmapPointer[address1] = red;
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
            var pixelsWidth = ScreenWidth;
            var pixelsHeight = ScreenHeight;
            int pixelsWidth_3 = pixelsWidth * 3;
            for (int x2 = -radius; x2 < radius; x2++)
            {
                int xtest = x + x2;
                if (xtest < 0)
                {
                    continue;
                }
                if (xtest >= pixelsWidth)
                {
                    break;
                }
                int height = (int)Math.Sqrt(radius * radius - x2 * x2);
                int xox_3 = (x2 + x) * 3;
                for (int y2 = -height; y2 < height; y2++)
                {
                    int ytest = y + y2;
                    if (ytest < 0)
                    {
                        continue;
                    }
                    if (ytest >= pixelsHeight)
                    {
                        break;
                    }
                    var address1 = (y2 + y) * pixelsWidth_3 + xox_3;
                    _bitmapPointer[address1] = blue;
                    address1++;
                    _bitmapPointer[address1] = green;
                    address1++;
                    _bitmapPointer[address1] = red;
                }
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
    }
}