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
    /// Describes VIC-II graphics pipeline state in machine traces.
    /// </summary>
    public sealed class MachineVicPipelineTraceEntry
    {
        public bool GraphicsDisplayState { get; set; }

        public bool MatrixFetchStartedThisLine { get; set; }

        public int MatrixFetchRequestStartCycle { get; set; }

        public int MatrixFetchStartCycle { get; set; }

        public int MatrixFetchCpuBlockStartCycle { get; set; }

        public bool VideoMatrixValid { get; set; }

        public int VideoMatrixCellY { get; set; }

        public bool VideoPatternValid { get; set; }

        public int VideoPatternCellY { get; set; }

        public int VideoPatternPixelRow { get; set; }

        public int GraphicsVc { get; set; }

        public int GraphicsVcBase { get; set; }

        public int GraphicsVmli { get; set; }

        public int GraphicsRc { get; set; }

        public int GraphicsLineCellY { get; set; }

        public int GraphicsLinePixelRow { get; set; }

        public bool LineDisplayEnabled { get; set; }

        public bool LineBitmapMode { get; set; }

        public bool LineExtendedColorMode { get; set; }

        public bool LineMulticolorMode { get; set; }

        public int LineXScroll { get; set; }

        public int LineYScroll { get; set; }

        public string DisplaySourceScreenBase { get; set; }

        public string DisplaySourceCharacterBase { get; set; }

        public string DisplaySourceBitmapBase { get; set; }
    }
}
