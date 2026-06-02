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
        public bool PendingGraphicsDisplayState;
        public int PendingGraphicsDisplayStateCycle;
        public bool BadLineConditionThisCycle;
        public int BadLineConditionStartCycle;
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
        public int GraphicsMatrixFetchColumn;
        public int GraphicsPatternFetchColumn;
        public int GraphicsVideoCounterOffset;
        public ulong GraphicsVmliShiftRegister;
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
        public byte RegisterD011;
        public byte RegisterD016;
        public byte PixelD011;
        public byte PixelD016;
        public bool Line40Column;
        public bool Line25Row;
        public bool HorizontalBorderActive;
        public bool VerticalBorderActive;
        public bool Sprite3DmaActive;
        public bool Sprite3DmaLatched;
        public bool Sprite3ExpandFlipFlop;
        public bool Sprite3LatchedYExpanded;
        public bool Sprite3LineYExpanded;
        public int Sprite3Mc;
        public int Sprite3McBase;
        public int Sprite3FetchPhase;
        public int Sprite3FetchStartMc;
        public int Sprite3FetchRow;
        public int Sprite3DisplayRow;
        public bool Sprite3FetchRowAdjusted;
        public bool Sprite3DisplayRowAdjusted;
        public bool Sprite3LineVisible;
        public bool Sprite3LineDataValid;
        public int Sprite3LineDisplayRow;
        public bool Sprite3LineDisplayRowAdjusted;
        public byte Sprite3LineDataByte0;
        public byte Sprite3LineDataByte1;
        public byte Sprite3LineDataByte2;
        public byte Sprite3RegisterD017;
        public byte Sprite3PixelD017;
    }
}
