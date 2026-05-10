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
    /// Captures VIC-II graphics pipeline state for accuracy tracing and tests.
    /// </summary>
    public struct VicPipelineState
    {
        public bool GraphicsDisplayState;
        public bool MatrixFetchStartedThisLine;
        public int MatrixFetchRequestStartCycle;
        public int MatrixFetchStartCycle;
        public int MatrixFetchCpuBlockStartCycle;
        public bool VideoMatrixValid;
        public int VideoMatrixCellY;
        public bool VideoMatrixBitmapMode;
        public bool VideoPatternValid;
        public int VideoPatternCellY;
        public int VideoPatternPixelRow;
        public bool VideoPatternBitmapMode;
        public int GraphicsVc;
        public int GraphicsVcBase;
        public int GraphicsVmli;
        public int GraphicsRc;
        public int GraphicsLineMatrixBaseIndex;
        public int GraphicsLineCellY;
        public int GraphicsLinePixelRow;
        public bool LineDisplayEnabled;
        public bool LineBitmapMode;
        public bool LineExtendedColorMode;
        public bool LineMulticolorMode;
        public byte LineXScroll;
        public byte LineYScroll;
        public ushort DisplaySourceScreenBase;
        public ushort DisplaySourceCharacterBase;
        public ushort DisplaySourceBitmapBase;
    }
}
