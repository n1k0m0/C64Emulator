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
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the frame buffer component.
    /// </summary>
    public sealed class FrameBuffer
    {
        /// <summary>
        /// Gets the framebuffer width in pixels.
        /// </summary>
        public int Width { get; private set; }
        /// <summary>
        /// Gets the framebuffer height in pixels.
        /// </summary>
        public int Height { get; private set; }
        /// <summary>
        /// Gets the raw framebuffer pixel array.
        /// </summary>
        public uint[] Pixels { get; private set; }
        /// <summary>
        /// Gets the most recently completed full-frame pixel array.
        /// </summary>
        public uint[] CompletedPixels { get; private set; }

        /// <summary>
        /// Initializes a new FrameBuffer instance.
        /// </summary>
        public FrameBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new uint[width * height];
            CompletedPixels = new uint[width * height];
        }

        /// <summary>
        /// Sets the pixel value.
        /// </summary>
        public void SetPixel(int x, int y, uint argb)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                return;
            }

            Pixels[y * Width + x] = argb;
        }

        /// <summary>
        /// Sets a pixel after the caller has already checked the coordinates.
        /// </summary>
        public void SetPixelUnchecked(int x, int y, uint argb)
        {
            Pixels[y * Width + x] = argb;
        }

        /// <summary>
        /// Handles the clear operation.
        /// </summary>
        public void Clear(uint argb)
        {
            for (var i = 0; i < Pixels.Length; i++)
            {
                Pixels[i] = argb;
            }

            System.Array.Copy(Pixels, CompletedPixels, Pixels.Length);
        }

        /// <summary>
        /// Captures the currently rendered frame as the stable display frame.
        /// </summary>
        public void CaptureCompletedFrame()
        {
            System.Array.Copy(Pixels, CompletedPixels, Pixels.Length);
        }

        /// <summary>
        /// Writes the framebuffer contents into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            writer.Write(Width);
            writer.Write(Height);
            BinaryStateIO.WriteUIntArray(writer, Pixels);
        }

        /// <summary>
        /// Restores the framebuffer contents from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            uint[] pixels = BinaryStateIO.ReadUIntArray(reader);
            if (width == Width && height == Height && pixels != null && pixels.Length == Pixels.Length)
            {
                System.Array.Copy(pixels, Pixels, Pixels.Length);
                System.Array.Copy(pixels, CompletedPixels, CompletedPixels.Length);
            }
        }
    }
}
