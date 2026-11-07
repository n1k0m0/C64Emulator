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
        /// <summary>NTSC-M with the common 6567R8 VIC-II (65 cycles, 263 lines).</summary>
        public static C64Model Ntsc
        {
            get { return new C64Model("NTSC C64 (6567R8)", 1022727.0, 263, 65, 418, 235); }
        }

        public bool IsNtsc { get { return CyclesPerLine == 65; } }
        public string VideoStandard { get { return IsNtsc ? "NTSC" : "PAL"; } }
        public double FrameHz { get { return CpuHz / (RasterLines * CyclesPerLine); } }

        /// <summary>Remove only video-selection switches; preserve positional mode arguments.</summary>
        public static C64Model ParseCommandLine(ref string[] args)
        {
            var remaining = new System.Collections.Generic.List<string>();
            C64Model selected = null;
            args = args ?? new string[0];
            for (int i = 0; i < args.Length; i++)
            {
                string value = null;
                if (string.Equals(args[i], "--ntsc", System.StringComparison.OrdinalIgnoreCase)) value = "ntsc";
                else if (string.Equals(args[i], "--pal", System.StringComparison.OrdinalIgnoreCase)) value = "pal";
                else if (string.Equals(args[i], "--video", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (++i >= args.Length) throw new System.ArgumentException("--video requires pal or ntsc.");
                    value = args[i];
                }
                else { remaining.Add(args[i]); continue; }
                C64Model next;
                if (string.Equals(value, "pal", System.StringComparison.OrdinalIgnoreCase)) next = Pal;
                else if (string.Equals(value, "ntsc", System.StringComparison.OrdinalIgnoreCase)) next = Ntsc;
                else throw new System.ArgumentException("Unknown video standard: " + value);
                if (selected != null && selected.IsNtsc != next.IsNtsc)
                    throw new System.ArgumentException("Choose one video standard, PAL or NTSC.");
                selected = next;
            }
            args = remaining.ToArray();
            return selected ?? Pal;
        }
    }
}
