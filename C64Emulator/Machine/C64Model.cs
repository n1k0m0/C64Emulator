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
namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the c64 model component.
    /// </summary>
    public sealed class C64Model
    {
        /// <summary>
        /// Gets the model or media name.
        /// </summary>
        public string Name { get; private set; }
        /// <summary>
        /// Gets the CPU clock frequency in Hertz.
        /// </summary>
        public double CpuHz { get; private set; }
        /// <summary>
        /// Gets the number of raster lines per video frame.
        /// </summary>
        public int RasterLines { get; private set; }
        /// <summary>
        /// Gets the number of CPU cycles per raster line.
        /// </summary>
        public int CyclesPerLine { get; private set; }
        /// <summary>
        /// Gets the visible framebuffer width in pixels.
        /// </summary>
        public int VisibleWidth { get; private set; }
        /// <summary>
        /// Gets the visible framebuffer height in pixels.
        /// </summary>
        public int VisibleHeight { get; private set; }

        /// <summary>
        /// Initializes a new C64Model instance.
        /// </summary>
        private C64Model(string name, double cpuHz, int rasterLines, int cyclesPerLine, int visibleWidth, int visibleHeight)
        {
            Name = name;
            CpuHz = cpuHz;
            RasterLines = rasterLines;
            CyclesPerLine = cyclesPerLine;
            VisibleWidth = visibleWidth;
            VisibleHeight = visibleHeight;
        }

        public static C64Model Pal
        {
            get
            {
                return new C64Model("PAL C64", 985248.0, 312, 63, 403, 284);
            }
        }
    }
}
