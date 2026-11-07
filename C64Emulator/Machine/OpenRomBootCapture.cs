// SPDX-License-Identifier: Apache-2.0
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Captures an actual completed VIC frame after booting with the open ROMs.
    /// </summary>
    public static class OpenRomBootCapture
    {
        public static void Run(string romDirectory, string outputPath, int maxCycles = 12000000)
        {
            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            using (var system = new C64System(C64Model.Pal, options, romDirectory))
            {
                if (maxCycles <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxCycles));
                }
                for (int elapsed = 0; elapsed < maxCycles &&
                    !OpenRomVerifier.ScreenHasLine(system, "READY."); elapsed += 100000)
                {
                    system.RunCycles(Math.Min(100000, maxCycles - elapsed));
                }
                if (system.Cpu.State == CpuState.Jammed ||
                    !OpenRomVerifier.ScreenHasLine(system, "READY."))
                {
                    throw new InvalidOperationException("Open ROM boot did not reach BASIC READY.");
                }
                system.RunCycles(40000); // publish a completed frame containing READY.
                if (system.Cpu.State == CpuState.Jammed ||
                    system.FrameBuffer.CompletedFrameId < 2)
                {
                    throw new InvalidOperationException("Open ROM boot did not produce a complete VIC frame.");
                }

                FrameBuffer frame = system.FrameBuffer;
                string parent = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                Directory.CreateDirectory(parent);
                using (var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb))
                {
                    uint[] pixels = frame.CompletedPixels;
                    for (int y = 0; y < frame.Height; y++)
                    {
                        int row = y * frame.Width;
                        for (int x = 0; x < frame.Width; x++)
                        {
                            bitmap.SetPixel(x, y, Color.FromArgb(unchecked((int)pixels[row + x])));
                        }
                    }

                    bitmap.Save(outputPath, ImageFormat.Png);
                }
            }
        }
    }
}
