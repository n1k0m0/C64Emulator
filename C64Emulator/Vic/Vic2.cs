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
    /// Emulates VIC-II video timing, memory fetches, sprites, borders, and pixel composition.
    /// </summary>
    public sealed class Vic2
    {
        private const int TotalLines = 312;
        private const int CyclesPerLine = 63;
        private const int PixelsPerCycle = 8;
        private const int TotalRasterWidth = CyclesPerLine * PixelsPerCycle;
        private const int VisibleColumns = 40;
        private const int VisibleRows = 25;
        private const int NarrowVisibleColumns = 38;
        private const int NarrowVisibleRows = 24;
        private const int CharacterWidth = 8;
        private const int CharacterHeight = 8;
        private const int DenLatchRasterLine = 0x30;
        private const int GraphicsOutputDelayPixels = 0;
        private const int CpuWriteVisibleDot = 0;
        private const int ModeWriteVisibleDot = 0;
        private const int D011ModeWriteBackfillPixels = 17;
        private const int D016ModeWriteBackfillPixels = 16;
        private const int InnerDisplayWidth = VisibleColumns * CharacterWidth;
        private const int InnerDisplayHeight = VisibleRows * CharacterHeight;
        private const int CropLeft = (TotalRasterWidth - 403) / 2;
        private const int CropTop = (TotalLines - 284) / 2;
        private const int BorderLeft = 41;
        private const int BorderTop = 37;
        private const int NarrowBorderLeft = BorderLeft + 7;
        private const int NarrowBorderTop = BorderTop + 4;
        private const int NarrowBottomBorderRasterLine = CropTop + NarrowBorderTop + (NarrowVisibleRows * CharacterHeight);
        private const int NoPendingRasterLine = -1;
        private const int StandardSpriteDisplayLeft = 24;
        private const int StandardSpriteDisplayTop = 51;
        private const int NarrowSpriteDisplayLeft = 31;
        private const int NarrowSpriteDisplayTop = 55;
        private const int SpriteSameLineDisplayX = 0x164;
        private const int RasterIrqCompareClearLastCycle = 55;
        private const int SpriteVisibleOffsetX = StandardSpriteDisplayLeft - BorderLeft;
        private const int SpriteVisibleOffsetY = StandardSpriteDisplayTop - BorderTop;
        private const int SpriteRasterStartOffsetY = 1;
        private const ulong DisplaySequencerColumnMask = (1UL << VisibleColumns) - 1UL;

        private static readonly uint[] Palette =
        {
            0xFF000000u,
            0xFFFFFFFFu,
            0xFF68372Bu,
            0xFF70A4B2u,
            0xFF6F3D86u,
            0xFF588D43u,
            0xFF352879u,
            0xFFB8C76Fu,
            0xFF6F4F25u,
            0xFF433900u,
            0xFF9A6759u,
            0xFF444444u,
            0xFF6C6C6Cu,
            0xFF9AD284u,
            0xFF6C5EB5u,
            0xFF959595u
        };

        /// <summary>
        /// Tracks the VIC-II display sequencer independently from the visible pixel latch.
        /// </summary>
        private struct DisplaySequencerState
        {
            public int MatrixFetchColumn;
            public int PatternFetchColumn;
            public int VideoCounterOffset;
            public ulong VmliShiftRegister;
            public bool PreviousDisplayWindowActive;

            /// <summary>
            /// Clears all sequencer state at power-on or frame restart.
            /// </summary>
            public void ResetFrame()
            {
                MatrixFetchColumn = 0;
                PatternFetchColumn = 0;
                VideoCounterOffset = 0;
                VmliShiftRegister = 0;
                PreviousDisplayWindowActive = false;
            }

            /// <summary>
            /// Starts a raster line without clearing the physical VMLI carry state.
            /// </summary>
            public void BeginRasterLine()
            {
                MatrixFetchColumn = 0;
                PatternFetchColumn = 0;
                VideoCounterOffset = 0;
                PreviousDisplayWindowActive = false;
            }

            /// <summary>
            /// Restarts the fetch columns when a badline opens a new display row.
            /// </summary>
            public void RestartFetchColumns()
            {
                MatrixFetchColumn = 0;
                PatternFetchColumn = 0;
                VideoCounterOffset = 0;
            }

            /// <summary>
            /// Restarts the fetch columns with a DMA-delay video-counter offset.
            /// </summary>
            public void RestartFetchColumns(int videoCounterOffset)
            {
                MatrixFetchColumn = 0;
                PatternFetchColumn = 0;
                VideoCounterOffset = videoCounterOffset & 0x3F;
            }

            /// <summary>
            /// Advances the display shift register for one VIC cycle.
            /// </summary>
            public void ClockVmli(int cycle, bool displayState)
            {
                bool displayWindowActive = displayState && cycle >= 16 && cycle <= 55;
                bool injectDisplayToken = displayWindowActive && !PreviousDisplayWindowActive;
                VmliShiftRegister = ((VmliShiftRegister << 1) & DisplaySequencerColumnMask) |
                    (injectDisplayToken ? 1UL : 0UL);
                PreviousDisplayWindowActive = displayWindowActive;
            }

            /// <summary>
            /// Advances the matrix-fetch side of the sequencer.
            /// </summary>
            public void AdvanceMatrixFetch()
            {
                MatrixFetchColumn = (MatrixFetchColumn + 1) & 0x3F;
            }

            /// <summary>
            /// Advances the pattern-fetch side of the sequencer.
            /// </summary>
            public void AdvancePatternFetch()
            {
                PatternFetchColumn = (PatternFetchColumn + 1) & 0x3F;
            }
        }

        private readonly SystemBus _bus;
        private readonly FrameBuffer _frameBuffer;
        private readonly C64Model _model;
        private readonly byte[] _registers = new byte[0x40];
        private readonly byte[] _pixelRegisters = new byte[0x40];
        private readonly byte[] _pendingPixelRegisterValues = new byte[0x40];
        private readonly bool[] _pendingPixelRegisterWrites = new bool[0x40];
        private readonly VicBusPlan _busPlan = new VicBusPlan();
        private readonly bool[] _spriteDmaActive = new bool[8];
        private readonly bool[] _spriteDmaLatched = new bool[8];
        private readonly bool[] _spriteLineVisible = new bool[8];
        private readonly bool[] _spriteExpandFlipFlop = new bool[8];
        private readonly int[] _spriteCurrentRow = new int[8];
        private readonly int[] _spriteFetchRow = new int[8];
        private readonly int[] _spriteFetchPhase = new int[8];
        private readonly int[] _spriteDisplayRow = new int[8];
        private readonly int[] _spriteMc = new int[8];
        private readonly int[] _spriteMcBase = new int[8];
        private readonly int[] _spriteFetchStartMc = new int[8];
        private readonly bool[] _spriteRowHistoryActive = new bool[8];
        private readonly bool[] _spritePreviousLineYExpanded = new bool[8];
        private readonly bool[] _spriteFetchRowAdjusted = new bool[8];
        private readonly bool[] _spriteDisplayRowAdjusted = new bool[8];
        private readonly int[] _spriteLatchedX = new int[8];
        private readonly int[] _spriteLatchedY = new int[8];
        private readonly bool[] _spriteLatchedXExpanded = new bool[8];
        private readonly bool[] _spriteLatchedYExpanded = new bool[8];
        private readonly bool[] _spriteLatchedMulticolor = new bool[8];
        private readonly byte[] _spriteLatchedColor = new byte[8];
        private readonly int[] _spriteLineX = new int[8];
        private readonly int[] _spriteLineY = new int[8];
        private readonly bool[] _spriteLineXExpanded = new bool[8];
        private readonly bool[] _spriteLineYExpanded = new bool[8];
        private readonly bool[] _spriteLineMulticolor = new bool[8];
        private readonly byte[] _spriteLineColor = new byte[8];
        private readonly bool[] _spriteLineDataValid = new bool[8];
        private readonly byte[] _spriteLineDataByte0 = new byte[8];
        private readonly byte[] _spriteLineDataByte1 = new byte[8];
        private readonly byte[] _spriteLineDataByte2 = new byte[8];
        private readonly int[] _spriteLineDisplayRow = new int[8];
        private readonly bool[] _spriteLineDisplayRowAdjusted = new bool[8];
        private readonly bool[] _spriteDataValid = new bool[8];
        private byte _spriteCycle55ExpansionMask;
        private readonly byte[] _spritePointers = new byte[8];
        private readonly byte[] _spriteDataByte0 = new byte[8];
        private readonly byte[] _spriteDataByte1 = new byte[8];
        private readonly byte[] _spriteDataByte2 = new byte[8];
        private readonly byte[] _videoMatrixScreenCodes = new byte[VisibleColumns];
        private readonly byte[] _videoMatrixColorNibbles = new byte[VisibleColumns];
        private readonly bool[] _videoMatrixFetched = new bool[VisibleColumns];
        private readonly bool[] _videoMatrixBitmapModes = new bool[VisibleColumns];
        private readonly bool[] _videoMatrixExtendedColorModes = new bool[VisibleColumns];
        private readonly bool[] _videoMatrixMulticolorModes = new bool[VisibleColumns];
        private readonly byte[] _videoPatternBytes = new byte[VisibleColumns];
        private readonly bool[] _videoPatternFetched = new bool[VisibleColumns];
        private readonly bool[] _videoPatternBitmapModes = new bool[VisibleColumns];
        private readonly bool[] _videoPatternExtendedColorModes = new bool[VisibleColumns];
        private readonly bool[] _videoPatternMulticolorModes = new bool[VisibleColumns];
        private readonly bool[] _videoPatternIdle = new bool[VisibleColumns];
        private readonly PixelResult[] _graphicsOutputDelay = new PixelResult[GraphicsOutputDelayPixels];

        private int _rasterLine;
        private int _cycleInLine;
        private long _globalCycle;
        private bool _cyclePrepared;
        private ushort _rasterIrqLine;
        private byte _irqFlags;
        private byte _spriteSpriteCollision;
        private byte _spriteDataCollision;
        private byte _lightPenX;
        private byte _lightPenY;
        private bool _cpuBusBlockedThisCycle;
        private bool _isBadLine;
        private bool _badLineConditionThisCycle;
        private int _badLineConditionStartCycle;
        private bool _earlyD011BadLinePulseBeforeCycle14;
#pragma warning disable 0414
        // Kept for savestate compatibility with builds that serialized a per-line guard.
        private bool _rasterIrqTriggeredThisLine;
#pragma warning restore 0414
        private bool _rasterIrqCompareState;
        private bool _videoMatrixValid;
        private int _videoMatrixCellY;
        private bool _videoMatrixBitmapMode;
        private bool _videoPatternValid;
        private int _videoPatternCellY;
        private int _videoPatternPixelRow;
        private bool _videoPatternBitmapMode;
        private bool _graphicsDisplayState;
        private bool _pendingGraphicsDisplayState;
        private int _pendingGraphicsDisplayStateCycle;
        private int _pendingGraphicsDisplayStateVideoCounterOffset;
        private int _graphicsVc;
        private int _graphicsVcBase;
        private int _graphicsVmli;
        private DisplaySequencerState _displaySequencer;
        private int _graphicsRc;
        private int _graphicsLineMatrixBaseIndex;
        private int _graphicsLineCellY;
        private int _graphicsLinePixelRow;
        private bool _matrixFetchStartedThisLine;
        private int _matrixFetchRequestStartCycle;
        private int _matrixFetchStartCycle;
        private int _matrixFetchCpuBlockStartCycle;
        // Reserved for the next VIC-II sequencer pass. They are serialized today so
        // current savestates keep their shape while the pending paths are wired up.
#pragma warning disable 0414
        private int _textVcBase;
        private int _textRc;
        private int _textLineCellY;
        private int _textLinePixelRow;
        private bool _previousTextDisplayLine;
        private int _bitmapVcBase;
        private int _bitmapRc;
        private int _bitmapLineCellY;
        private int _bitmapLinePixelRow;
        private bool _previousBitmapDisplayLine;
#pragma warning restore 0414
        private VicBusSlot _currentBusSlot;
        private bool _displayEnableFrameLatched;
        private bool _displayWindowFrameLatched;
        private bool _lineDisplayEnabled;
        private bool _line40Column;
        private bool _line25Row;
        private bool _lineBitmapMode;
        private bool _lineExtendedColorMode;
        private bool _lineMulticolorMode;
        private byte _lineXScroll;
        private byte _lineYScroll;
        private ushort _lineScreenBaseAbsolute;
        private ushort _lineCharacterBaseAbsolute;
        private ushort _lineBitmapBaseAbsolute;
        private int _lineDisplayLeftFrame;
        private int _lineDisplayTopFrame;
        private int _lineDisplayRightFrame;
        private int _lineDisplayBottomFrame;
        private int _pendingVerticalBorderCloseRasterLine = NoPendingRasterLine;
        private bool _verticalBorderActive = true;
        private bool _horizontalBorderActive = true;
        private bool _horizontalSideBorderCarryOpen;
        private ushort _displaySourceScreenBaseAbsolute;
        private ushort _displaySourceCharacterBaseAbsolute;
        private ushort _displaySourceBitmapBaseAbsolute;
        private bool _displaySource40Column;
        private bool _displaySource25Row;
        private byte _displaySourceXScroll;
        private byte _displaySourceYScroll;
        private bool _displaySourceBitmapMode;
        private bool _displaySourceExtendedColorMode;
        private bool _displaySourceMulticolorMode;
        private int _graphicsOutputDelayIndex;
        private bool _graphicsSequencerCellLoaded;
        private int _graphicsSequencerCellX;
        private byte _graphicsSequencerScreenCode;
        private byte _graphicsSequencerColorNibble;
        private byte _graphicsSequencerPattern;
#pragma warning disable 0414
        // Kept so savestates written by builds with explicit sequencer mode latches
        // continue to deserialize while raw pixels now resolve modes at output time.
        private bool _graphicsSequencerBitmapMode;
        private bool _graphicsSequencerExtendedColorMode;
        private bool _graphicsSequencerMulticolorMode;
#pragma warning restore 0414

        /// <summary>
        /// Initializes a new Vic2 instance.
        /// </summary>
        public Vic2(SystemBus bus, FrameBuffer frameBuffer, C64Model model)
        {
            _bus = bus;
            _frameBuffer = frameBuffer;
            _model = model;
            ResetRegisters();
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            ResetRegisters();
            _rasterLine = 0;
            _cycleInLine = 0;
            _globalCycle = 0;
        }

        /// <summary>
        /// Writes the complete VIC-II state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            _busPlan.SaveState(writer);
            StateSerializer.WriteObjectFields(writer, this, "_bus", "_frameBuffer", "_model", "_busPlan");
        }

        /// <summary>
        /// Restores the complete VIC-II state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            _busPlan.LoadState(reader);
            StateSerializer.ReadObjectFields(reader, this, "_bus", "_frameBuffer", "_model", "_busPlan");
            SynchronizePixelRegisters();
            RepairDisplaySequencerAfterLoad();
            RepairSpriteDmaLatchAfterLoad();
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            PrepareCycle();
            FinishCycle();
        }

        /// <summary>
        /// Handles the prepare cycle operation.
        /// </summary>
        public void PrepareCycle()
        {
            _cyclePrepared = true;
            if (_cycleInLine == 0)
            {
                BeginRasterLine();
            }

            _currentBusSlot = _busPlan.GetSlot(_cycleInLine);

            if (ShouldCheckRasterIrqThisCycle())
            {
                UpdateRasterIrq();
            }

            UpdateGraphicsStateForCurrentCycle();
            CaptureSpriteExpansionFlipFlopMaskForCurrentCycle();
            UpdateSpriteDmaStartForCurrentCycle();
            LoadSpriteDataCountersForCurrentCycle();
            UpdateSpriteMcBaseForCurrentCycle();
            ApplyGraphicsBusOverrides();
            _cpuBusBlockedThisCycle = _currentBusSlot.BlocksCpu;
            ExecuteFetchAction(_currentBusSlot.Phi1Action);
            ApplyPendingGraphicsDisplayStateAfterPhi1();
            UpdateEarlySpriteDmaEndForCurrentCycle();
        }

        /// <summary>
        /// Handles the finish cycle operation.
        /// </summary>
        public void FinishCycle()
        {
            UpdateDisplayEnableLatchesAfterCpuAccess();
            UpdateSpriteExpansionFlipFlopsForCurrentCycle();
            ExecutePhi2FetchAction();
            RenderCurrentCyclePixels();

            _cycleInLine++;
            _globalCycle++;

            if (_cycleInLine >= CyclesPerLine)
            {
                EndRasterLine();
                _cycleInLine = 0;
                _rasterLine++;

                if (_rasterLine >= TotalLines)
                {
                    _frameBuffer.CaptureCompletedFrame();
                    _rasterLine = 0;
                }
            }

            _cyclePrepared = false;
        }

        /// <summary>
        /// Ends raster line.
        /// </summary>
        private void EndRasterLine()
        {
            System.Array.Clear(_spriteLineVisible, 0, _spriteLineVisible.Length);
            System.Array.Clear(_spriteLineDataValid, 0, _spriteLineDataValid.Length);
        }

        /// <summary>
        /// Handles the requires bus this cycle operation.
        /// </summary>
        public bool RequiresBusThisCycle()
        {
            return _cpuBusBlockedThisCycle;
        }

        /// <summary>
        /// Returns whether bus request pending this cycle is available or active.
        /// </summary>
        public bool HasBusRequestPendingThisCycle()
        {
            return _currentBusSlot.BusRequestPending;
        }

        /// <summary>
        /// Handles the read operation.
        /// </summary>
        public byte Read(ushort address)
        {
            address &= 0x3F;
            switch (address)
            {
                case 0x11:
                    return (byte)((_registers[0x11] & 0x7F) | ((_rasterLine & 0x100) >> 1));
                case 0x12:
                    return (byte)(_rasterLine & 0xFF);
                case 0x13:
                    return _lightPenX;
                case 0x14:
                    return _lightPenY;
                case 0x16:
                    return (byte)(_registers[0x16] | 0xC0);
                case 0x18:
                    return (byte)(_registers[0x18] | 0x01);
                case 0x19:
                    return (byte)(_irqFlags | 0x70 | (IsIrqAsserted() ? 0x80 : 0x00));
                case 0x1A:
                    return (byte)(_registers[0x1A] | 0xF0);
                case 0x1E:
                    return ReadAndClearSpriteSpriteCollision();
                case 0x1F:
                    return ReadAndClearSpriteDataCollision();
                case 0x20:
                case 0x21:
                case 0x22:
                case 0x23:
                case 0x24:
                case 0x25:
                case 0x26:
                case 0x27:
                case 0x28:
                case 0x29:
                case 0x2A:
                case 0x2B:
                case 0x2C:
                case 0x2D:
                case 0x2E:
                    return (byte)(_registers[address] | 0xF0);
                case 0x2F:
                case 0x30:
                case 0x31:
                case 0x32:
                case 0x33:
                case 0x34:
                case 0x35:
                case 0x36:
                case 0x37:
                case 0x38:
                case 0x39:
                case 0x3A:
                case 0x3B:
                case 0x3C:
                case 0x3D:
                case 0x3E:
                case 0x3F:
                    return 0xFF;
                default:
                    return _registers[address];
            }
        }

        /// <summary>
        /// Handles the write operation.
        /// </summary>
        public void Write(ushort address, byte value)
        {
            address &= 0x3F;
            byte previousValue = _registers[address];
            switch (address)
            {
                case 0x11:
                    _registers[0x11] = value;
                    _rasterIrqLine = (ushort)((_rasterIrqLine & 0x00FF) | ((value & 0x80) << 1));
                    TriggerRasterIrqForCurrentLineIfMatched();
                    TrackVerticalBorderRselWrite(value);
                    TrackEarlyD011BadLinePulse(previousValue, value);
                    break;
                case 0x12:
                    _registers[0x12] = value;
                    _rasterIrqLine = (ushort)((_rasterIrqLine & 0x0100) | value);
                    TriggerRasterIrqForCurrentLineIfMatched();
                    break;
                case 0x19:
                    _irqFlags = (byte)(_irqFlags & ~(value & 0x0F));
                    _registers[0x19] = _irqFlags;
                    break;
                case 0x1A:
                    _registers[0x1A] = (byte)(value & 0x0F);
                    break;
                case 0x1E:
                case 0x1F:
                case 0x2F:
                case 0x30:
                case 0x31:
                case 0x32:
                case 0x33:
                case 0x34:
                case 0x35:
                case 0x36:
                case 0x37:
                case 0x38:
                case 0x39:
                case 0x3A:
                case 0x3B:
                case 0x3C:
                case 0x3D:
                case 0x3E:
                case 0x3F:
                    break;
                case 0x20:
                case 0x21:
                case 0x22:
                case 0x23:
                case 0x24:
                case 0x25:
                case 0x26:
                case 0x27:
                case 0x28:
                case 0x29:
                case 0x2A:
                case 0x2B:
                case 0x2C:
                case 0x2D:
                case 0x2E:
                    _registers[address] = (byte)(value & 0x0F);
                    break;
                default:
                    _registers[address] = value;
                    break;
            }

            if (address == 0x17)
            {
                ApplySpriteYExpansionWrite(previousValue, _registers[0x17]);
            }
            else if (address == 0x16)
            {
                TrackHorizontalBorderCselWrite(previousValue, _registers[0x16]);
            }

            BackfillCroppedLeftBorderColor(address, _registers[address]);
            BackfillCroppedLeftBackgroundColor(address, _registers[address]);
            BackfillModeRegisterWrite(address, _registers[address]);
            QueuePixelRegisterWrite(address, _registers[address]);
        }

        /// <summary>
        /// Lets the VIC react to CPU writes that race the character-pattern fetch on the current scanline.
        /// </summary>
        public void NotifyCpuMemoryWrite(ushort address, byte value)
        {
            BackfillCharacterPatternWrite(address, value);
        }

        /// <summary>
        /// Applies the asynchronous part of the sprite Y-expansion register.
        /// </summary>
        private void ApplySpriteYExpansionWrite(byte previousValue, byte value)
        {
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                int mask = 1 << spriteIndex;
                if ((previousValue & mask) != 0 && (value & mask) == 0)
                {
                    _spriteExpandFlipFlop[spriteIndex] = true;
                }
            }
        }

        /// <summary>
        /// Remembers a short pre-cycle-14 D011/Y-scroll pulse that is enough to start the next badline.
        /// </summary>
        private void TrackEarlyD011BadLinePulse(byte previousValue, byte value)
        {
            if (!_cyclePrepared || _cycleInLine >= 14)
            {
                return;
            }

            bool denStaysEnabled = (previousValue & value & 0x10) != 0;
            bool onlyYScrollChanged = ((previousValue ^ value) & 0x78) == 0 &&
                ((previousValue ^ value) & 0x07) != 0;
            bool newYScrollMatchesRaster = _rasterLine >= 0x30 &&
                _rasterLine <= 0xF7 &&
                ((_rasterLine & 0x07) == (value & 0x07));
            bool previousYScrollMissedRaster = (_rasterLine & 0x07) != (previousValue & 0x07);
            if (!denStaysEnabled ||
                !onlyYScrollChanged ||
                !_displayEnableFrameLatched ||
                !newYScrollMatchesRaster ||
                !previousYScrollMissedRaster)
            {
                return;
            }

            _earlyD011BadLinePulseBeforeCycle14 = true;
            if (_badLineConditionStartCycle > _cycleInLine)
            {
                _badLineConditionStartCycle = _cycleInLine;
            }
        }

        /// Tracks late CSEL writes that can skip the right horizontal-border compare and open the side border.
        /// </summary>
        private void TrackHorizontalBorderCselWrite(byte previousValue, byte value)
        {
            bool clearedColumnSelect = (previousValue & 0x08) != 0 && (value & 0x08) == 0;
            if (!clearedColumnSelect ||
                !_cyclePrepared ||
                !_lineDisplayEnabled ||
                _verticalBorderActive ||
                !_horizontalBorderActive ||
                _cycleInLine < 52)
            {
                return;
            }

            _horizontalBorderActive = false;
            _horizontalSideBorderCarryOpen = true;
            BackfillLateOpenedRightBorder();
        }

        /// <summary>
        /// Repaints pixels that were emitted as border before a late CSEL write opened the side border.
        /// </summary>
        private void BackfillLateOpenedRightBorder()
        {
            int frameY = _rasterLine - CropTop;
            if ((uint)frameY >= (uint)_model.VisibleHeight)
            {
                return;
            }

            int oldRight = ((_pixelRegisters[0x16] & 0x08) != 0)
                ? BorderLeft + (VisibleColumns * CharacterWidth)
                : NarrowBorderLeft + (NarrowVisibleColumns * CharacterWidth);
            int frameXLimit = (_cycleInLine * PixelsPerCycle) + CpuWriteVisibleDot - CropLeft;
            if (frameXLimit <= oldRight)
            {
                return;
            }

            uint oldBorderColor = Palette[_pixelRegisters[0x20] & 0x0F];
            uint backgroundColor = Palette[_pixelRegisters[0x21] & 0x0F];
            int rowOffset = frameY * _frameBuffer.Width;
            int right = frameXLimit > _frameBuffer.Width ? _frameBuffer.Width : frameXLimit;
            for (int frameX = oldRight; frameX < right; frameX++)
            {
                int pixelIndex = rowOffset + frameX;
                if (_frameBuffer.Pixels[pixelIndex] == oldBorderColor)
                {
                    _frameBuffer.SetPixelUnchecked(frameX, frameY, backgroundColor);
                }
            }
        }

        /// <summary>
        /// Backfills early border-color writes that land inside the host-visible left crop.
        /// </summary>
        private void BackfillCroppedLeftBorderColor(ushort address, byte value)
        {
            if (address != 0x20 || !_cyclePrepared)
            {
                return;
            }

            int beamX = (_cycleInLine * PixelsPerCycle) + CpuWriteVisibleDot;
            int frameXLimit = beamX - CropLeft;
            int frameY = _rasterLine - CropTop;
            if (frameXLimit <= 0 ||
                frameXLimit > GetCurrentBorderLeftFrame() ||
                (uint)frameY >= (uint)_model.VisibleHeight)
            {
                return;
            }

            uint oldColor = Palette[_pixelRegisters[0x20] & 0x0F];
            uint color = Palette[value & 0x0F];
            int rowOffset = frameY * _frameBuffer.Width;
            for (int frameX = 0; frameX < frameXLimit; frameX++)
            {
                int pixelIndex = rowOffset + frameX;
                if (_frameBuffer.Pixels[pixelIndex] == oldColor)
                {
                    _frameBuffer.SetPixelUnchecked(frameX, frameY, color);
                }
            }
        }

        /// <summary>
        /// Backfills early background-color writes that land before the delayed graphics output catches up.
        /// </summary>
        private void BackfillCroppedLeftBackgroundColor(ushort address, byte value)
        {
            if (address != 0x21 || !_cyclePrepared)
            {
                return;
            }

            int frameY = _rasterLine - CropTop;
            if ((uint)frameY >= (uint)_model.VisibleHeight || !IsGraphicsDisplayLine())
            {
                return;
            }

            int activeLeft = GetCurrentDisplayLeftFrame();
            int activeRight = activeLeft + GetCurrentDisplayWidth();
            int frameXLimit = (_cycleInLine * PixelsPerCycle) + CpuWriteVisibleDot - CropLeft;
            if (frameXLimit <= activeLeft ||
                frameXLimit > activeRight ||
                frameXLimit > activeLeft + (CharacterWidth * 4))
            {
                return;
            }

            uint oldColor = Palette[_pixelRegisters[0x21] & 0x0F];
            uint color = Palette[value & 0x0F];
            int rowOffset = frameY * _frameBuffer.Width;
            for (int frameX = activeLeft; frameX < frameXLimit; frameX++)
            {
                int pixelIndex = rowOffset + frameX;
                if (_frameBuffer.Pixels[pixelIndex] == oldColor)
                {
                    _frameBuffer.SetPixelUnchecked(frameX, frameY, color);
                }
            }
        }

        /// <summary>
        /// Repaints text pixels affected by an in-flight CPU write to the active character generator.
        /// </summary>
        private void BackfillCharacterPatternWrite(ushort address, byte value)
        {
            if (!_cyclePrepared ||
                _lineBitmapMode ||
                !_lineDisplayEnabled ||
                _verticalBorderActive ||
                _graphicsLinePixelRow < 0)
            {
                return;
            }

            int characterOffset = address - _displaySourceCharacterBaseAbsolute;
            if (characterOffset < 0 || characterOffset >= 0x0800)
            {
                return;
            }

            int pixelRow = characterOffset & 0x07;
            if (pixelRow != _graphicsLinePixelRow)
            {
                return;
            }

            int characterCode = (characterOffset >> 3) & 0xFF;
            int firstCell = _cycleInLine - 13;
            for (int cellX = firstCell; cellX < firstCell + 2; cellX++)
            {
                BackfillCharacterPatternCell(cellX, characterCode, value);
            }
        }

        /// <summary>
        /// Repaints one character cell on the current scanline after a racing character-pattern write.
        /// </summary>
        private void BackfillCharacterPatternCell(int cellX, int characterCode, byte pattern)
        {
            if ((uint)cellX >= VisibleColumns)
            {
                return;
            }

            int frameY = _rasterLine - CropTop;
            if ((uint)frameY >= (uint)_model.VisibleHeight)
            {
                return;
            }

            int frameXStart = GetCurrentDisplayLeftFrame() + (cellX * CharacterWidth);
            int frameXEnd = frameXStart + CharacterWidth;
            if (frameXEnd <= 0 || frameXStart >= _frameBuffer.Width)
            {
                return;
            }

            byte screenCode;
            byte colorNibble;
            if (!TryGetCurrentTextCell(cellX, out screenCode, out colorNibble))
            {
                return;
            }

            int effectiveCharacterCode = _displaySourceExtendedColorMode ? (screenCode & 0x3F) : screenCode;
            if (effectiveCharacterCode != characterCode)
            {
                return;
            }

            int left = frameXStart < 0 ? 0 : frameXStart;
            int right = frameXEnd > _frameBuffer.Width ? _frameBuffer.Width : frameXEnd;
            for (int frameX = left; frameX < right; frameX++)
            {
                int pixelXInCell = frameX - frameXStart;
                PixelResult pixel = ComputeCharacterPixel(
                    pixelXInCell,
                    screenCode,
                    colorNibble,
                    pattern,
                    _displaySourceExtendedColorMode,
                    _displaySourceMulticolorMode);
                pixel = ResolveOutputPixel(pixel);
                ApplySprites(ref pixel, frameX, frameY);
                _frameBuffer.SetPixelUnchecked(frameX, frameY, pixel.Color);
            }

            _videoPatternBytes[cellX] = pattern;
            _videoPatternFetched[cellX] = true;
            _videoPatternBitmapModes[cellX] = false;
            _videoPatternExtendedColorModes[cellX] = _displaySourceExtendedColorMode;
            _videoPatternMulticolorModes[cellX] = _displaySourceMulticolorMode;
            _videoPatternIdle[cellX] = false;
            if (_graphicsSequencerCellLoaded && _graphicsSequencerCellX == cellX)
            {
                _graphicsSequencerPattern = pattern;
            }
        }

        /// <summary>
        /// Repaints the already emitted part of a raster-line mode split.
        /// </summary>
        private void BackfillModeRegisterWrite(ushort address, byte value)
        {
            if (!IsModePixelRegister(address) ||
                !_cyclePrepared ||
                !_lineDisplayEnabled)
            {
                return;
            }

            int frameY = _rasterLine - CropTop;
            if ((uint)frameY >= (uint)_model.VisibleHeight)
            {
                return;
            }

            int activeTop = GetCurrentDisplayTopFrame();
            int activeBottom = activeTop + GetCurrentDisplayHeight();
            if (_verticalBorderActive ||
                frameY < activeTop ||
                frameY >= activeBottom)
            {
                return;
            }

            int backfillPixels = address == 0x11
                ? D011ModeWriteBackfillPixels
                : D016ModeWriteBackfillPixels;
            int frameXEnd = (_cycleInLine * PixelsPerCycle) + ModeWriteVisibleDot - CropLeft;
            int frameXStart = frameXEnd - backfillPixels;
            if (frameXEnd <= 0 || frameXStart >= _frameBuffer.Width)
            {
                return;
            }

            int left = frameXStart < 0 ? 0 : frameXStart;
            int right = frameXEnd > _frameBuffer.Width ? _frameBuffer.Width : frameXEnd;
            int activeLeft = GetCurrentDisplayLeftFrame();
            int activeRight = activeLeft + GetCurrentDisplayWidth();
            int displayY = frameY - activeTop;
            byte oldPixelRegister = _pixelRegisters[address];
            bool oldCellLoaded = _graphicsSequencerCellLoaded;
            int oldCellX = _graphicsSequencerCellX;
            byte oldScreenCode = _graphicsSequencerScreenCode;
            byte oldColorNibble = _graphicsSequencerColorNibble;
            byte oldPattern = _graphicsSequencerPattern;
            bool oldBitmapMode = _graphicsSequencerBitmapMode;
            bool oldExtendedColorMode = _graphicsSequencerExtendedColorMode;
            bool oldMulticolorMode = _graphicsSequencerMulticolorMode;

            _pixelRegisters[address] = value;
            for (int frameX = left; frameX < right; frameX++)
            {
                if (frameX < activeLeft || frameX >= activeRight)
                {
                    continue;
                }

                if (!IsGraphicsSourceVisible(frameX, frameY) ||
                    !IsGraphicsSourceActiveForCurrentCycle(frameX - activeLeft))
                {
                    continue;
                }

                PixelResult pixel = ComputeGraphicsPixel(frameX - activeLeft, displayY);
                pixel = ResolveOutputPixel(pixel);
                ApplySprites(ref pixel, frameX, frameY);
                _frameBuffer.SetPixelUnchecked(frameX, frameY, pixel.Color);
            }

            _pixelRegisters[address] = oldPixelRegister;
            _graphicsSequencerCellLoaded = oldCellLoaded;
            _graphicsSequencerCellX = oldCellX;
            _graphicsSequencerScreenCode = oldScreenCode;
            _graphicsSequencerColorNibble = oldColorNibble;
            _graphicsSequencerPattern = oldPattern;
            _graphicsSequencerBitmapMode = oldBitmapMode;
            _graphicsSequencerExtendedColorMode = oldExtendedColorMode;
            _graphicsSequencerMulticolorMode = oldMulticolorMode;
        }

        /// <summary>
        /// Reads the current text cell from the latched matrix when possible, otherwise from VIC memory.
        /// </summary>
        private bool TryGetCurrentTextCell(int cellX, out byte screenCode, out byte colorNibble)
        {
            if (CanUseLatchedMatrixCell(cellX))
            {
                screenCode = _videoMatrixScreenCodes[cellX];
                colorNibble = _videoMatrixColorNibbles[cellX];
                return true;
            }

            if (_graphicsLineCellY < 0 || _graphicsLineCellY >= VisibleRows)
            {
                screenCode = 0;
                colorNibble = 0;
                return false;
            }

            int matrixIndex = NormalizeVideoMatrixIndex((_graphicsLineCellY * VisibleColumns) + cellX);
            screenCode = ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));
            colorNibble = _bus.ReadColorRam((ushort)matrixIndex);
            return true;
        }

        /// <summary>
        /// Gets the timing value.
        /// </summary>
        public VicTiming GetTiming()
        {
            return new VicTiming
            {
                RasterLine = _rasterLine,
                CycleInLine = _cycleInLine,
                GlobalCycle = _globalCycle,
                BeamX = _cycleInLine * PixelsPerCycle,
                BeamY = _rasterLine,
                BadLine = _isBadLine,
                Phi1Action = _currentBusSlot.Phi1Action,
                Phi2Action = _currentBusSlot.Phi2Action,
                CpuBlocked = _currentBusSlot.BlocksCpu,
                BusRequestPending = _currentBusSlot.BusRequestPending
            };
        }

        /// <summary>
        /// Captures the current graphics pipeline state for diagnostics.
        /// </summary>
        public VicPipelineState GetPipelineState()
        {
            return new VicPipelineState
            {
                GraphicsDisplayState = _graphicsDisplayState,
                PendingGraphicsDisplayState = _pendingGraphicsDisplayState,
                PendingGraphicsDisplayStateCycle = _pendingGraphicsDisplayStateCycle,
                BadLineConditionThisCycle = _badLineConditionThisCycle,
                BadLineConditionStartCycle = _badLineConditionStartCycle,
                MatrixFetchStartedThisLine = _matrixFetchStartedThisLine,
                MatrixFetchRequestStartCycle = _matrixFetchRequestStartCycle,
                MatrixFetchStartCycle = _matrixFetchStartCycle,
                MatrixFetchCpuBlockStartCycle = _matrixFetchCpuBlockStartCycle,
                VideoMatrixValid = _videoMatrixValid,
                VideoMatrixCellY = _videoMatrixCellY,
                VideoMatrixBitmapMode = _videoMatrixBitmapMode,
                VideoPatternValid = _videoPatternValid,
                VideoPatternCellY = _videoPatternCellY,
                VideoPatternPixelRow = _videoPatternPixelRow,
                VideoPatternBitmapMode = _videoPatternBitmapMode,
                GraphicsVc = _graphicsVc,
                GraphicsVcBase = _graphicsVcBase,
                GraphicsVmli = _graphicsVmli,
                GraphicsMatrixFetchColumn = _displaySequencer.MatrixFetchColumn,
                GraphicsPatternFetchColumn = _displaySequencer.PatternFetchColumn,
                GraphicsVideoCounterOffset = _displaySequencer.VideoCounterOffset,
                GraphicsVmliShiftRegister = _displaySequencer.VmliShiftRegister,
                GraphicsRc = _graphicsRc,
                GraphicsLineMatrixBaseIndex = _graphicsLineMatrixBaseIndex,
                GraphicsLineCellY = _graphicsLineCellY,
                GraphicsLinePixelRow = _graphicsLinePixelRow,
                LineDisplayEnabled = _lineDisplayEnabled,
                LineBitmapMode = _lineBitmapMode,
                LineExtendedColorMode = _lineExtendedColorMode,
                LineMulticolorMode = _lineMulticolorMode,
                LineXScroll = _lineXScroll,
                LineYScroll = _lineYScroll,
                DisplaySourceScreenBase = _displaySourceScreenBaseAbsolute,
                DisplaySourceCharacterBase = _displaySourceCharacterBaseAbsolute,
                DisplaySourceBitmapBase = _displaySourceBitmapBaseAbsolute,
                RegisterD011 = _registers[0x11],
                RegisterD016 = _registers[0x16],
                PixelD011 = _pixelRegisters[0x11],
                PixelD016 = _pixelRegisters[0x16],
                Line40Column = _line40Column,
                Line25Row = _line25Row,
                HorizontalBorderActive = _horizontalBorderActive,
                VerticalBorderActive = _verticalBorderActive,
                Sprite3DmaActive = _spriteDmaActive[3],
                Sprite3DmaLatched = _spriteDmaLatched[3],
                Sprite3ExpandFlipFlop = _spriteExpandFlipFlop[3],
                Sprite3Mc = _spriteMc[3],
                Sprite3McBase = _spriteMcBase[3],
                Sprite3FetchRow = _spriteFetchRow[3],
                Sprite3DisplayRow = _spriteDisplayRow[3],
                Sprite3LineVisible = _spriteLineVisible[3],
                Sprite3LineDataValid = _spriteLineDataValid[3],
                Sprite3LineDisplayRow = _spriteLineDisplayRow[3]
            };
        }

        /// <summary>
        /// Returns whether irq asserted is true.
        /// </summary>
        public bool IsIrqAsserted()
        {
            return (_irqFlags & _registers[0x1A] & 0x0F) != 0;
        }

        /// <summary>
        /// Handles the reset registers operation.
        /// </summary>
        private void ResetRegisters()
        {
            for (int index = 0; index < _registers.Length; index++)
            {
                _registers[index] = 0x00;
            }

            _registers[0x11] = 0x1B;
            _registers[0x16] = 0xC8;
            _registers[0x18] = 0x15;
            _registers[0x20] = 0x0E;
            _registers[0x21] = 0x06;
            _registers[0x22] = 0x02;
            _registers[0x23] = 0x04;
            _registers[0x24] = 0x00;
            _rasterIrqLine = 0;
            _irqFlags = 0;
            _spriteSpriteCollision = 0;
            _spriteDataCollision = 0;
            _cyclePrepared = false;
            _lightPenX = 0;
            _lightPenY = 0;
            _cpuBusBlockedThisCycle = false;
            _isBadLine = false;
            _badLineConditionStartCycle = CyclesPerLine + 1;
            _earlyD011BadLinePulseBeforeCycle14 = false;
            _rasterIrqTriggeredThisLine = false;
            _rasterIrqCompareState = false;
            _videoMatrixValid = false;
            _videoMatrixCellY = -1;
            _videoMatrixBitmapMode = false;
            _videoPatternValid = false;
            _videoPatternCellY = -1;
            _videoPatternPixelRow = -1;
            _videoPatternBitmapMode = false;
            _graphicsDisplayState = false;
            _pendingGraphicsDisplayState = false;
            _pendingGraphicsDisplayStateCycle = 64;
            _pendingGraphicsDisplayStateVideoCounterOffset = 0;
            _graphicsVc = 0;
            _graphicsVcBase = 0;
            _graphicsVmli = 0;
            _displaySequencer.ResetFrame();
            _graphicsRc = 7;
            _graphicsLineMatrixBaseIndex = -1;
            _graphicsLineCellY = -1;
            _graphicsLinePixelRow = -1;
            _matrixFetchStartedThisLine = false;
            _matrixFetchRequestStartCycle = CyclesPerLine + 1;
            _matrixFetchStartCycle = CyclesPerLine + 1;
            _matrixFetchCpuBlockStartCycle = CyclesPerLine + 1;
            _textVcBase = 0;
            _textRc = 0;
            _textLineCellY = -1;
            _textLinePixelRow = -1;
            _previousTextDisplayLine = false;
            _bitmapVcBase = 0;
            _bitmapRc = 0;
            _bitmapLineCellY = -1;
            _bitmapLinePixelRow = -1;
            _previousBitmapDisplayLine = false;
            System.Array.Clear(_spriteDmaActive, 0, _spriteDmaActive.Length);
            System.Array.Clear(_spriteDmaLatched, 0, _spriteDmaLatched.Length);
            System.Array.Clear(_spriteLineVisible, 0, _spriteLineVisible.Length);
            System.Array.Clear(_spriteExpandFlipFlop, 0, _spriteExpandFlipFlop.Length);
            System.Array.Clear(_spriteCurrentRow, 0, _spriteCurrentRow.Length);
            System.Array.Clear(_spriteFetchRow, 0, _spriteFetchRow.Length);
            System.Array.Clear(_spriteFetchPhase, 0, _spriteFetchPhase.Length);
            System.Array.Clear(_spriteDisplayRow, 0, _spriteDisplayRow.Length);
            System.Array.Clear(_spriteMc, 0, _spriteMc.Length);
            System.Array.Clear(_spriteMcBase, 0, _spriteMcBase.Length);
            System.Array.Clear(_spriteFetchStartMc, 0, _spriteFetchStartMc.Length);
            System.Array.Clear(_spriteRowHistoryActive, 0, _spriteRowHistoryActive.Length);
            System.Array.Clear(_spritePreviousLineYExpanded, 0, _spritePreviousLineYExpanded.Length);
            System.Array.Clear(_spriteFetchRowAdjusted, 0, _spriteFetchRowAdjusted.Length);
            System.Array.Clear(_spriteDisplayRowAdjusted, 0, _spriteDisplayRowAdjusted.Length);
            System.Array.Clear(_spriteLatchedX, 0, _spriteLatchedX.Length);
            System.Array.Clear(_spriteLatchedY, 0, _spriteLatchedY.Length);
            System.Array.Clear(_spriteLatchedXExpanded, 0, _spriteLatchedXExpanded.Length);
            System.Array.Clear(_spriteLatchedYExpanded, 0, _spriteLatchedYExpanded.Length);
            System.Array.Clear(_spriteLatchedMulticolor, 0, _spriteLatchedMulticolor.Length);
            System.Array.Clear(_spriteLatchedColor, 0, _spriteLatchedColor.Length);
            System.Array.Clear(_spriteLineX, 0, _spriteLineX.Length);
            System.Array.Clear(_spriteLineY, 0, _spriteLineY.Length);
            System.Array.Clear(_spriteLineXExpanded, 0, _spriteLineXExpanded.Length);
            System.Array.Clear(_spriteLineYExpanded, 0, _spriteLineYExpanded.Length);
            System.Array.Clear(_spriteLineMulticolor, 0, _spriteLineMulticolor.Length);
            System.Array.Clear(_spriteLineColor, 0, _spriteLineColor.Length);
            System.Array.Clear(_spriteLineDataValid, 0, _spriteLineDataValid.Length);
            System.Array.Clear(_spriteLineDataByte0, 0, _spriteLineDataByte0.Length);
            System.Array.Clear(_spriteLineDataByte1, 0, _spriteLineDataByte1.Length);
            System.Array.Clear(_spriteLineDataByte2, 0, _spriteLineDataByte2.Length);
            System.Array.Clear(_spriteLineDisplayRow, 0, _spriteLineDisplayRow.Length);
            System.Array.Clear(_spriteLineDisplayRowAdjusted, 0, _spriteLineDisplayRowAdjusted.Length);
            System.Array.Clear(_spriteDataValid, 0, _spriteDataValid.Length);
            _spriteCycle55ExpansionMask = 0;
            System.Array.Clear(_spritePointers, 0, _spritePointers.Length);
            System.Array.Clear(_spriteDataByte0, 0, _spriteDataByte0.Length);
            System.Array.Clear(_spriteDataByte1, 0, _spriteDataByte1.Length);
            System.Array.Clear(_spriteDataByte2, 0, _spriteDataByte2.Length);
            System.Array.Clear(_videoMatrixScreenCodes, 0, _videoMatrixScreenCodes.Length);
            System.Array.Clear(_videoMatrixColorNibbles, 0, _videoMatrixColorNibbles.Length);
            System.Array.Clear(_videoMatrixFetched, 0, _videoMatrixFetched.Length);
            System.Array.Clear(_videoMatrixBitmapModes, 0, _videoMatrixBitmapModes.Length);
            System.Array.Clear(_videoMatrixExtendedColorModes, 0, _videoMatrixExtendedColorModes.Length);
            System.Array.Clear(_videoMatrixMulticolorModes, 0, _videoMatrixMulticolorModes.Length);
            System.Array.Clear(_videoPatternBytes, 0, _videoPatternBytes.Length);
            System.Array.Clear(_videoPatternFetched, 0, _videoPatternFetched.Length);
            System.Array.Clear(_videoPatternBitmapModes, 0, _videoPatternBitmapModes.Length);
            System.Array.Clear(_videoPatternExtendedColorModes, 0, _videoPatternExtendedColorModes.Length);
            System.Array.Clear(_videoPatternMulticolorModes, 0, _videoPatternMulticolorModes.Length);
            System.Array.Clear(_videoPatternIdle, 0, _videoPatternIdle.Length);
            _currentBusSlot = default(VicBusSlot);
            _displayEnableFrameLatched = false;
            _displayWindowFrameLatched = false;
            _lineDisplayEnabled = true;
            _line40Column = true;
            _line25Row = true;
            _lineBitmapMode = false;
            _lineExtendedColorMode = false;
            _lineMulticolorMode = false;
            _lineXScroll = (byte)(_registers[0x16] & 0x07);
            _lineYScroll = (byte)(_registers[0x11] & 0x07);
            _lineScreenBaseAbsolute = GetScreenBaseAbsoluteFromRegisters();
            _lineCharacterBaseAbsolute = GetCharacterBaseAbsoluteFromRegisters();
            _lineBitmapBaseAbsolute = GetBitmapBaseAbsoluteFromRegisters();
            _lineDisplayLeftFrame = BorderLeft;
            _lineDisplayTopFrame = BorderTop;
            _lineDisplayRightFrame = _lineDisplayLeftFrame + InnerDisplayWidth;
            _lineDisplayBottomFrame = _lineDisplayTopFrame + InnerDisplayHeight;
            _pendingVerticalBorderCloseRasterLine = NoPendingRasterLine;
            _verticalBorderActive = true;
            _horizontalBorderActive = true;
            _horizontalSideBorderCarryOpen = false;
            SynchronizePixelRegisters();
            LatchDisplaySourceFromLineState();
            _frameBuffer.Clear(Palette[_registers[0x20] & 0x0F]);
        }

        /// <summary>
        /// Queues the render-visible copy of a VIC register when a CPU write occurs mid-cycle.
        /// </summary>
        private void QueuePixelRegisterWrite(ushort address, byte value)
        {
            int index = address & 0x3F;
            if (!_cyclePrepared)
            {
                _pixelRegisters[index] = value;
                return;
            }

            _pendingPixelRegisterValues[index] = value;
            _pendingPixelRegisterWrites[index] = true;
        }

        /// <summary>
        /// Applies all CPU writes that become visible at the current pixel phase.
        /// </summary>
        private void ApplyPendingPixelRegisterWrites(int dot)
        {
            for (int index = 0; index < _pendingPixelRegisterWrites.Length; index++)
            {
                if (!_pendingPixelRegisterWrites[index])
                {
                    continue;
                }

                int visibleDot = IsModePixelRegister(index) ? ModeWriteVisibleDot : CpuWriteVisibleDot;
                if (dot != visibleDot)
                {
                    continue;
                }

                _pixelRegisters[index] = _pendingPixelRegisterValues[index];
                _pendingPixelRegisterWrites[index] = false;
            }
        }

        /// <summary>
        /// Returns whether the register carries VIC display mode bits with their own output phase.
        /// </summary>
        private static bool IsModePixelRegister(int index)
        {
            return index == 0x11 || index == 0x16;
        }

        /// <summary>
        /// Resynchronizes render-visible registers with the architectural VIC registers.
        /// </summary>
        private void SynchronizePixelRegisters()
        {
            System.Array.Copy(_registers, _pixelRegisters, _registers.Length);
            System.Array.Clear(_pendingPixelRegisterValues, 0, _pendingPixelRegisterValues.Length);
            System.Array.Clear(_pendingPixelRegisterWrites, 0, _pendingPixelRegisterWrites.Length);
        }

        /// <summary>
        /// Renders current cycle pixels.
        /// </summary>
        private void RenderCurrentCyclePixels()
        {
            int beamXStart = _cycleInLine * PixelsPerCycle;
            int beamY = _rasterLine;

            for (int dot = 0; dot < PixelsPerCycle; dot++)
            {
                ApplyPendingPixelRegisterWrites(dot);

                int beamX = beamXStart + dot;
                int frameX = beamX - CropLeft;
                int frameY = beamY - CropTop;
                PixelResult outputPixel = ComposePixel(frameX, frameY);

                if ((uint)frameX < (uint)_model.VisibleWidth && (uint)frameY < (uint)_model.VisibleHeight)
                {
                    _frameBuffer.SetPixelUnchecked(frameX, frameY, outputPixel.Color);
                }
            }
        }

        /// <summary>
        /// Composes pixel.
        /// </summary>
        private PixelResult ComposePixel(int frameX, int frameY)
        {
            uint borderColor = Palette[_pixelRegisters[0x20] & 0x0F];
            PixelResult borderPixel = new PixelResult
            {
                Color = borderColor,
                GraphicsForeground = false
            };

            UpdateHorizontalBorderState(frameX, frameY);

            int activeDisplayLeft = GetCurrentDisplayLeftFrame();
            int activeDisplayTop = GetCurrentDisplayTopFrame();
            int activeDisplayBottom = activeDisplayTop + GetCurrentDisplayHeight();
            bool currentGraphicsVisible = _lineDisplayEnabled &&
                !_horizontalBorderActive &&
                !_verticalBorderActive &&
                frameY >= activeDisplayTop &&
                frameY < activeDisplayBottom;

            int sourceFrameX = frameX + GraphicsOutputDelayPixels;
            bool sourceGraphicsVisible = IsGraphicsSourceVisible(sourceFrameX, frameY) &&
                IsGraphicsSourceActiveForCurrentCycle(sourceFrameX - activeDisplayLeft);
            PixelResult graphicsPixel = sourceGraphicsVisible
                ? ComputeGraphicsPixel(sourceFrameX - activeDisplayLeft, frameY - activeDisplayTop)
                : CreateBackgroundRegisterPixel(0, false);
            PixelResult delayedGraphicsPixel = DelayGraphicsPixel(graphicsPixel);
            PixelResult result = currentGraphicsVisible ? ResolveOutputPixel(delayedGraphicsPixel) : borderPixel;

            if (!sourceGraphicsVisible)
            {
                _graphicsSequencerCellLoaded = false;
            }

            ApplySprites(ref result, frameX, frameY);
            return result;
        }

        /// <summary>
        /// Returns whether the delayed graphics shifter should sample this frame position.
        /// </summary>
        private bool IsGraphicsSourceVisible(int frameX, int frameY)
        {
            int currentDisplayLeft = GetCurrentDisplayLeftFrame();
            int currentDisplayRight = currentDisplayLeft + GetCurrentDisplayWidth();
            int currentDisplayTop = GetCurrentDisplayTopFrame();
            int currentDisplayBottom = currentDisplayTop + GetCurrentDisplayHeight();
            return _lineDisplayEnabled &&
                !_verticalBorderActive &&
                frameX >= currentDisplayLeft &&
                frameX < currentDisplayRight &&
                frameY >= currentDisplayTop &&
                frameY < currentDisplayBottom;
        }

        /// <summary>
        /// Returns whether the current cycle should feed visible graphics into the output delay.
        /// </summary>
        private bool IsGraphicsSourceActiveForCurrentCycle(int displayX)
        {
            if (_graphicsDisplayState || ((_cycleInLine + 1) < 16 && _badLineConditionThisCycle))
            {
                return true;
            }

            if (!_videoPatternValid)
            {
                return false;
            }

            if (_graphicsLineCellY >= VisibleRows)
            {
                return true;
            }

            int scrolledX = displayX + GetCurrentHorizontalScrollPhase();
            if (scrolledX < 0 || scrolledX >= InnerDisplayWidth)
            {
                return false;
            }

            int cellX = scrolledX / CharacterWidth;
            return (uint)cellX < VisibleColumns && _videoPatternIdle[cellX];
        }

        /// <summary>
        /// Computes graphics pixel.
        /// </summary>
        private PixelResult ComputeGraphicsPixel(int displayX, int displayY)
        {
            int scrolledX = displayX + GetCurrentHorizontalScrollPhase();
            if (scrolledX < 0 || scrolledX >= InnerDisplayWidth)
            {
                _graphicsSequencerCellLoaded = false;
                return CreateBackgroundRegisterPixel(0, false);
            }

            int cellX = scrolledX / CharacterWidth;
            int pixelXInCell = scrolledX & 0x07;
            bool useIdleFallback = !_graphicsDisplayState &&
                _videoPatternValid &&
                _graphicsLineCellY >= VisibleRows;
            if (!_graphicsSequencerCellLoaded || _graphicsSequencerCellX != cellX)
            {
                if (!LoadGraphicsSequencerCell(cellX, displayY))
                {
                    _graphicsSequencerCellLoaded = false;
                    return CreateIdleOrBackgroundPixel(useIdleFallback);
                }
            }

            return CreateRawGraphicsPixel(
                pixelXInCell,
                _graphicsSequencerScreenCode,
                _graphicsSequencerColorNibble,
                _graphicsSequencerPattern);
        }

        /// <summary>
        /// Resolves a delayed raw graphics pixel through the currently visible VIC mode bits.
        /// </summary>
        private PixelResult ResolveRawGraphicsPixel(PixelResult rawPixel)
        {
            bool bitmapMode = (_pixelRegisters[0x11] & 0x20) != 0;
            bool extendedColorMode = (_pixelRegisters[0x11] & 0x40) != 0;
            bool multicolorMode = (_pixelRegisters[0x16] & 0x10) != 0;
            if (bitmapMode)
            {
                // ECM combined with bitmap selects an illegal VIC-II mode; the chip
                // blanks the graphics output while the hidden sequencer still produces
                // foreground/background bits for sprite priority and collisions.
                if (extendedColorMode)
                {
                    return ComputeInvalidBitmapPixel(
                        rawPixel.RawPixelXInCell,
                        rawPixel.RawPattern,
                        multicolorMode);
                }

                return ComputeBitmapPixel(
                    rawPixel.RawPixelXInCell,
                    rawPixel.RawScreenCode,
                    rawPixel.RawColorNibble,
                    rawPixel.RawPattern,
                    multicolorMode && !extendedColorMode);
            }

            // ECM combined with multicolor text is illegal as well and must blank.
            if (extendedColorMode && multicolorMode)
            {
                return ComputeInvalidCharacterPixel(
                    rawPixel.RawPixelXInCell,
                    rawPixel.RawColorNibble,
                    rawPixel.RawPattern);
            }

            return ComputeCharacterPixel(
                rawPixel.RawPixelXInCell,
                rawPixel.RawScreenCode,
                rawPixel.RawColorNibble,
                rawPixel.RawPattern,
                extendedColorMode,
                multicolorMode);
        }

        /// <summary>
        /// Computes character pixel.
        /// </summary>
        private PixelResult ComputeCharacterPixel(
            int pixelXInCell,
            byte screenCode,
            byte colorNibble,
            byte pattern,
            bool extendedColorMode,
            bool multicolorMode)
        {
            bool multicolor = multicolorMode && (colorNibble & 0x08) != 0 && !extendedColorMode;

            if (multicolor)
            {
                int pair = (pattern >> ((3 - (pixelXInCell / 2)) * 2)) & 0x03;
                switch (pair)
                {
                    case 0:
                        return CreateBackgroundRegisterPixel(0, false);
                    case 1:
                        return CreateBackgroundRegisterPixel(1, false);
                    case 2:
                        return CreateBackgroundRegisterPixel(2, true);
                    default:
                        return CreateForegroundPixel(Palette[colorNibble & 0x07]);
                }
            }

            bool set = (pattern & (0x80 >> pixelXInCell)) != 0;
            if (!set)
            {
                if (extendedColorMode)
                {
                    return CreateBackgroundRegisterPixel((screenCode >> 6) & 0x03, false);
                }

                return CreateBackgroundRegisterPixel(0, false);
            }

            return CreateForegroundPixel(Palette[colorNibble & 0x0F]);
        }

        /// <summary>
        /// Computes bitmap pixel.
        /// </summary>
        private PixelResult ComputeBitmapPixel(
            int pixelXInCell,
            byte screenCode,
            byte colorNibble,
            byte pattern,
            bool multicolorBitmapMode)
        {
            if (multicolorBitmapMode)
            {
                int pair = (pattern >> ((3 - (pixelXInCell / 2)) * 2)) & 0x03;
                switch (pair)
                {
                    case 0:
                        return CreateBackgroundRegisterPixel(0, false);
                    case 1:
                        return CreateBackgroundPixel(Palette[(screenCode >> 4) & 0x0F]);
                    case 2:
                        return CreateForegroundPixel(Palette[screenCode & 0x0F]);
                    default:
                        return CreateForegroundPixel(Palette[colorNibble & 0x0F]);
                }
            }

            bool set = (pattern & (0x80 >> pixelXInCell)) != 0;
            if (!set)
            {
                return CreateBackgroundPixel(Palette[screenCode & 0x0F]);
            }

            return CreateForegroundPixel(Palette[(screenCode >> 4) & 0x0F]);
        }

        /// <summary>
        /// Computes the hidden foreground/background result for illegal ECM+MCM text.
        /// </summary>
        private static PixelResult ComputeInvalidCharacterPixel(int pixelXInCell, byte colorNibble, byte pattern)
        {
            bool foreground;
            if ((colorNibble & 0x08) != 0)
            {
                int pair = (pattern >> ((3 - (pixelXInCell / 2)) * 2)) & 0x03;
                foreground = pair >= 2;
            }
            else
            {
                foreground = (pattern & (0x80 >> pixelXInCell)) != 0;
            }

            return foreground ? CreateForegroundPixel(Palette[0]) : CreateBackgroundPixel(Palette[0]);
        }

        /// <summary>
        /// Computes the hidden foreground/background result for illegal ECM bitmap modes.
        /// </summary>
        private static PixelResult ComputeInvalidBitmapPixel(int pixelXInCell, byte pattern, bool multicolorMode)
        {
            bool foreground;
            if (multicolorMode)
            {
                int pair = (pattern >> ((3 - (pixelXInCell / 2)) * 2)) & 0x03;
                foreground = pair >= 2;
            }
            else
            {
                foreground = (pattern & (0x80 >> pixelXInCell)) != 0;
            }

            return foreground ? CreateForegroundPixel(Palette[0]) : CreateBackgroundPixel(Palette[0]);
        }

        /// <summary>
        /// Loads graphics sequencer cell.
        /// </summary>
        private bool LoadGraphicsSequencerCell(int cellX, int displayY)
        {
            bool hasLatchedPattern = _videoPatternValid &&
                _videoPatternPixelRow >= 0 &&
                _videoPatternFetched[cellX];
            if (hasLatchedPattern && _videoPatternIdle[cellX])
            {
                _graphicsSequencerScreenCode = 0;
                _graphicsSequencerColorNibble = 0;
                _graphicsSequencerPattern = _videoPatternBytes[cellX];
                _graphicsSequencerBitmapMode = false;
                _graphicsSequencerExtendedColorMode = _videoPatternExtendedColorModes[cellX];
                _graphicsSequencerMulticolorMode = false;
                _graphicsSequencerCellLoaded = true;
                _graphicsSequencerCellX = cellX;
                return true;
            }

            bool hasLatchedMatrix = CanUseLatchedMatrixCell(cellX);
            bool currentBitmapMode = hasLatchedPattern
                ? _videoPatternBitmapModes[cellX]
                : _displaySourceBitmapMode;

            if (currentBitmapMode)
            {
                int scrolledY = displayY + GetCurrentVerticalScrollPhase();
                if (scrolledY < 0 || scrolledY >= InnerDisplayHeight)
                {
                    return false;
                }

                int cellY = _graphicsLineCellY >= 0 ? _graphicsLineCellY : (scrolledY / CharacterHeight);
                int pixelYInCell = _graphicsLinePixelRow >= 0 ? _graphicsLinePixelRow : (scrolledY & 0x07);
                int lineMatrixBaseIndex = GetGraphicsLineMatrixBaseIndex(cellY);
                int matrixIndex = NormalizeVideoMatrixIndex(lineMatrixBaseIndex + cellX);

                _graphicsSequencerScreenCode = hasLatchedMatrix
                    ? _videoMatrixScreenCodes[cellX]
                    : ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));
                _graphicsSequencerColorNibble = hasLatchedMatrix
                    ? _videoMatrixColorNibbles[cellX]
                    : _bus.ReadColorRam((ushort)matrixIndex);
                bool useLatchedBitmapPattern = _videoPatternValid &&
                    _videoPatternBitmapModes[cellX] &&
                    _videoPatternPixelRow == pixelYInCell &&
                    _videoPatternFetched[cellX];
                _graphicsSequencerPattern = useLatchedBitmapPattern
                    ? _videoPatternBytes[cellX]
                    : ReadBitmapPatternFromSource(_displaySourceBitmapBaseAbsolute, matrixIndex, pixelYInCell);
                _graphicsSequencerBitmapMode = true;
                _graphicsSequencerExtendedColorMode = useLatchedBitmapPattern
                    ? _videoPatternExtendedColorModes[cellX]
                    : _displaySourceExtendedColorMode;
                _graphicsSequencerMulticolorMode = useLatchedBitmapPattern
                    ? _videoPatternMulticolorModes[cellX]
                    : _displaySourceMulticolorMode;
            }
            else
            {
                int scrolledY = displayY + GetCurrentVerticalScrollPhase();
                int textCellY = _graphicsLineCellY >= 0 ? _graphicsLineCellY : (scrolledY / CharacterHeight);
                int textPixelYInCell = _graphicsLinePixelRow >= 0 ? _graphicsLinePixelRow : (scrolledY & 0x07);
                if (textCellY < 0 || textCellY >= VisibleRows)
                {
                    return false;
                }

                int textLineMatrixBaseIndex = GetGraphicsLineMatrixBaseIndex(textCellY);
                int textMatrixIndex = NormalizeVideoMatrixIndex(textLineMatrixBaseIndex + cellX);

                if (hasLatchedMatrix)
                {
                    _graphicsSequencerScreenCode = _videoMatrixScreenCodes[cellX];
                    _graphicsSequencerColorNibble = _videoMatrixColorNibbles[cellX];
                }
                else
                {
                    _graphicsSequencerScreenCode = ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + textMatrixIndex));
                    _graphicsSequencerColorNibble = _bus.ReadColorRam((ushort)textMatrixIndex);
                }

                bool useLatchedCharacterPattern = _videoPatternValid &&
                    _videoPatternPixelRow == textPixelYInCell &&
                    !_videoPatternBitmapModes[cellX] &&
                    _videoPatternFetched[cellX];
                _graphicsSequencerPattern = useLatchedCharacterPattern
                    ? _videoPatternBytes[cellX]
                    : ReadCharacterPatternFromSource(_displaySourceCharacterBaseAbsolute, _displaySourceExtendedColorMode, textPixelYInCell, _graphicsSequencerScreenCode);
                _graphicsSequencerBitmapMode = false;
                _graphicsSequencerExtendedColorMode = useLatchedCharacterPattern
                    ? _videoPatternExtendedColorModes[cellX]
                    : _displaySourceExtendedColorMode;
                _graphicsSequencerMulticolorMode = useLatchedCharacterPattern
                    ? _videoPatternMulticolorModes[cellX]
                    : _displaySourceMulticolorMode;
            }

            _graphicsSequencerCellLoaded = true;
            _graphicsSequencerCellX = cellX;
            return true;
        }

        /// <summary>
        /// Creates the fallback graphics pixel for idle-state display areas.
        /// </summary>
        private PixelResult CreateIdleOrBackgroundPixel(bool useIdleFallback)
        {
            if (useIdleFallback)
            {
                return CreateBackgroundPixel(Palette[0]);
            }

            return CreateBackgroundRegisterPixel(0, false);
        }

        /// <summary>
        /// Applies sprites.
        /// </summary>
        private void ApplySprites(ref PixelResult result, int frameX, int frameY)
        {
            bool insideActiveDisplayArea = IsInsideActiveDisplayArea(frameX, frameY);
            byte opaqueSpriteMask = 0;
            bool spriteVisible = false;

            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                uint spriteColor;
                if (!TryGetSpritePixel(spriteIndex, frameX, frameY, out spriteColor))
                {
                    continue;
                }

                byte bit = (byte)(1 << spriteIndex);
                opaqueSpriteMask |= bit;

                if (insideActiveDisplayArea && result.GraphicsForeground)
                {
                    _spriteDataCollision |= bit;
                }

                bool behindGraphics = ((_pixelRegisters[0x1B] >> spriteIndex) & 0x01) != 0;
                if (insideActiveDisplayArea && !spriteVisible && (!behindGraphics || !result.GraphicsForeground))
                {
                    result.Color = spriteColor;
                    spriteVisible = true;
                }
            }

            if ((opaqueSpriteMask & (opaqueSpriteMask - 1)) != 0)
            {
                _spriteSpriteCollision |= opaqueSpriteMask;
            }

            _registers[0x1E] = _spriteSpriteCollision;
            _registers[0x1F] = _spriteDataCollision;

            if (_spriteSpriteCollision != 0)
            {
                _irqFlags |= 0x04;
            }

            if (_spriteDataCollision != 0)
            {
                _irqFlags |= 0x02;
            }

            _registers[0x19] = _irqFlags;
        }

        /// <summary>
        /// Attempts to get sprite pixel and reports whether it succeeded.
        /// </summary>
        private bool TryGetSpritePixel(int spriteIndex, int frameX, int frameY, out uint spriteColor)
        {
            spriteColor = 0;
            if (!_spriteLineVisible[spriteIndex])
            {
                return false;
            }

            // D015/D017/D01D are visible to the sprite output stage at pixel time.
            // Keep position and DMA line data latched, but let split effects use
            // the render-phased register copy.
            if (((_pixelRegisters[0x15] >> spriteIndex) & 0x01) == 0)
            {
                return false;
            }

            int spriteX = _spriteLineX[spriteIndex];
            int spriteY = _spriteLineY[spriteIndex];

            int localX = frameX - GetSpriteFrameLeft(spriteX);
            if (localX < 0)
            {
                return false;
            }

            bool xExpanded = ((_pixelRegisters[0x1D] >> spriteIndex) & 0x01) != 0;
            bool yExpanded = ((_pixelRegisters[0x17] >> spriteIndex) & 0x01) != 0;
            int localY = frameY - GetSpriteFrameTop(spriteY);
            if (localY < 0)
            {
                return false;
            }

            if (xExpanded)
            {
                localX /= 2;
            }

            if (yExpanded)
            {
                localY /= 2;
            }

            if (localX >= 24)
            {
                return false;
            }

            if (!_spriteLineDataValid[spriteIndex] ||
                _spriteLineDisplayRow[spriteIndex] < 0 ||
                _spriteLineDisplayRow[spriteIndex] >= 21)
            {
                return false;
            }

            if (!_spriteLineDisplayRowAdjusted[spriteIndex])
            {
                if (localY >= 21 || localY != _spriteLineDisplayRow[spriteIndex])
                {
                    return false;
                }
            }

            byte data0 = _spriteLineDataByte0[spriteIndex];
            byte data1 = _spriteLineDataByte1[spriteIndex];
            byte data2 = _spriteLineDataByte2[spriteIndex];
            uint spriteBits = (uint)((data0 << 16) | (data1 << 8) | data2);

            bool spriteMulticolor = ((_pixelRegisters[0x1C] >> spriteIndex) & 0x01) != 0;
            if (spriteMulticolor)
            {
                int pair = (int)((spriteBits >> ((11 - (localX / 2)) * 2)) & 0x03);
                switch (pair)
                {
                    case 0:
                        return false;
                    case 1:
                        spriteColor = Palette[_pixelRegisters[0x25] & 0x0F];
                        return true;
                    case 2:
                        spriteColor = Palette[_pixelRegisters[0x27 + spriteIndex] & 0x0F];
                        return true;
                    default:
                        spriteColor = Palette[_pixelRegisters[0x26] & 0x0F];
                        return true;
                }
            }

            if (((spriteBits >> (23 - localX)) & 0x01) == 0)
            {
                return false;
            }

            spriteColor = Palette[_pixelRegisters[0x27 + spriteIndex] & 0x0F];
            return true;
        }

        /// <summary>
        /// Reads and clear sprite sprite collision.
        /// </summary>
        private byte ReadAndClearSpriteSpriteCollision()
        {
            byte value = _spriteSpriteCollision;
            _spriteSpriteCollision = 0;
            _registers[0x1E] = 0;
            return value;
        }

        /// <summary>
        /// Reads and clear sprite data collision.
        /// </summary>
        private byte ReadAndClearSpriteDataCollision()
        {
            byte value = _spriteDataCollision;
            _spriteDataCollision = 0;
            _registers[0x1F] = 0;
            return value;
        }

        /// <summary>
        /// Returns whether sprite active on line is true.
        /// </summary>
        private bool IsSpriteActiveOnLine(int spriteIndex, int rasterLine)
        {
            return _spriteDmaActive[spriteIndex] || _spriteLineVisible[spriteIndex];
        }

        /// <summary>
        /// Updates raster irq.
        /// </summary>
        private void UpdateRasterIrq()
        {
            bool compareState = _rasterLine == _rasterIrqLine;
            if (compareState && !_rasterIrqCompareState)
            {
                SetRasterIrqFlag();
            }

            _rasterIrqCompareState = compareState;
        }

        /// <summary>
        /// Immediately asserts a raster IRQ when a D011/D012 write selects the current line.
        /// </summary>
        private void TriggerRasterIrqForCurrentLineIfMatched()
        {
            if (_rasterLine == 0 && _cycleInLine <= 1)
            {
                return;
            }

            bool compareState = _rasterLine == _rasterIrqLine;
            if (compareState)
            {
                if (!_rasterIrqCompareState)
                {
                    SetRasterIrqFlag();
                }

                _rasterIrqCompareState = true;
                return;
            }

            if (_cycleInLine <= RasterIrqCompareClearLastCycle)
            {
                _rasterIrqCompareState = false;
            }
        }

        /// <summary>
        /// Raises the raster IRQ flag and mirrors it into D019.
        /// </summary>
        private void SetRasterIrqFlag()
        {
            _irqFlags |= 0x01;
            _registers[0x19] = _irqFlags;
            _rasterIrqTriggeredThisLine = true;
        }

        /// <summary>
        /// Handles the should check raster irq this cycle operation.
        /// </summary>
        private bool ShouldCheckRasterIrqThisCycle()
        {
            // Bauer: compare happens in cycle 0 of each line, except line 0 where it
            // effectively occurs one cycle later.
            return _rasterLine == 0 ? _cycleInLine == 1 : _cycleInLine == 0;
        }

        /// <summary>
        /// Returns whether display enabled is true.
        /// </summary>
        private bool IsDisplayEnabled()
        {
            return _displayEnableFrameLatched;
        }

        /// <summary>
        /// Returns whether 40 column mode is true.
        /// </summary>
        private bool Is40ColumnMode()
        {
            return (_registers[0x16] & 0x08) != 0;
        }

        /// <summary>
        /// Returns whether 25 row mode is true.
        /// </summary>
        private bool Is25RowMode()
        {
            return (_registers[0x11] & 0x08) != 0;
        }

        /// <summary>
        /// Returns whether bitmap mode is true.
        /// </summary>
        private bool IsBitmapMode()
        {
            return _lineBitmapMode;
        }

        /// <summary>
        /// Returns whether extended color mode is true.
        /// </summary>
        private bool IsExtendedColorMode()
        {
            return _lineExtendedColorMode;
        }

        /// <summary>
        /// Returns whether multicolor bitmap mode is true.
        /// </summary>
        private bool IsMulticolorBitmapMode()
        {
            return _lineBitmapMode && _lineMulticolorMode;
        }

        /// <summary>
        /// Returns whether multicolor text mode is true.
        /// </summary>
        private bool IsMulticolorTextMode(byte colorNibble)
        {
            return !_lineBitmapMode && _lineMulticolorMode && (colorNibble & 0x08) != 0;
        }

        /// <summary>
        /// Gets the vic bank base value.
        /// </summary>
        private ushort GetVicBankBase()
        {
            return _bus.GetVicBankBase();
        }

        /// <summary>
        /// Gets the screen base absolute value.
        /// </summary>
        private ushort GetScreenBaseAbsolute()
        {
            return _lineScreenBaseAbsolute;
        }

        /// <summary>
        /// Gets the current screen base absolute value.
        /// </summary>
        private ushort GetCurrentScreenBaseAbsolute()
        {
            return GetScreenBaseAbsoluteFromRegisters();
        }

        /// <summary>
        /// Gets the character base absolute value.
        /// </summary>
        private ushort GetCharacterBaseAbsolute()
        {
            return _lineCharacterBaseAbsolute;
        }

        /// <summary>
        /// Gets the current character base absolute value.
        /// </summary>
        private ushort GetCurrentCharacterBaseAbsolute()
        {
            return GetCharacterBaseAbsoluteFromRegisters();
        }

        /// <summary>
        /// Gets the bitmap base absolute value.
        /// </summary>
        private ushort GetBitmapBaseAbsolute()
        {
            return _lineBitmapBaseAbsolute;
        }

        /// <summary>
        /// Gets the current bitmap base absolute value.
        /// </summary>
        private ushort GetCurrentBitmapBaseAbsolute()
        {
            return GetBitmapBaseAbsoluteFromRegisters();
        }

        /// <summary>
        /// Reads vic absolute.
        /// </summary>
        private byte ReadVicAbsolute(ushort absoluteAddress)
        {
            return _bus.VicReadAbsolute(absoluteAddress);
        }

        /// <summary>
        /// Gets the active display left frame value.
        /// </summary>
        private int GetActiveDisplayLeftFrame()
        {
            return _line40Column ? BorderLeft : NarrowBorderLeft;
        }

        /// <summary>
        /// Gets the active display top frame value.
        /// </summary>
        private int GetActiveDisplayTopFrame()
        {
            return _line25Row ? BorderTop : NarrowBorderTop;
        }

        /// <summary>
        /// Gets the active display width value.
        /// </summary>
        private int GetActiveDisplayWidth()
        {
            int columns = _line40Column ? VisibleColumns : NarrowVisibleColumns;
            return columns * CharacterWidth;
        }

        /// <summary>
        /// Gets the active display height value.
        /// </summary>
        private int GetActiveDisplayHeight()
        {
            int rows = _line25Row ? VisibleRows : NarrowVisibleRows;
            return rows * CharacterHeight;
        }

        /// <summary>
        /// Gets the current display left frame value.
        /// </summary>
        private int GetCurrentDisplayLeftFrame()
        {
            return _displaySource40Column ? BorderLeft : NarrowBorderLeft;
        }

        /// <summary>
        /// Gets the current display width value.
        /// </summary>
        private int GetCurrentDisplayWidth()
        {
            int columns = _displaySource40Column ? VisibleColumns : NarrowVisibleColumns;
            return columns * CharacterWidth;
        }

        /// <summary>
        /// Gets the current display top frame value.
        /// </summary>
        private int GetCurrentDisplayTopFrame()
        {
            return _displaySource25Row ? BorderTop : NarrowBorderTop;
        }

        /// <summary>
        /// Gets the current display height value.
        /// </summary>
        private int GetCurrentDisplayHeight()
        {
            int rows = _displaySource25Row ? VisibleRows : NarrowVisibleRows;
            return rows * CharacterHeight;
        }

        /// <summary>
        /// Gets the current horizontal border left frame value from live CSEL.
        /// </summary>
        private int GetCurrentBorderLeftFrame()
        {
            return (_pixelRegisters[0x16] & 0x08) != 0 ? BorderLeft : NarrowBorderLeft;
        }

        /// <summary>
        /// Gets the current horizontal border width value from live CSEL.
        /// </summary>
        private int GetCurrentBorderWidth()
        {
            int columns = (_pixelRegisters[0x16] & 0x08) != 0 ? VisibleColumns : NarrowVisibleColumns;
            return columns * CharacterWidth;
        }

        /// <summary>
        /// Gets the current vertical border top frame value from live RSEL.
        /// </summary>
        private int GetCurrentBorderTopFrame()
        {
            return (_pixelRegisters[0x11] & 0x08) != 0 ? BorderTop : NarrowBorderTop;
        }

        /// <summary>
        /// Gets the current vertical border height value from live RSEL.
        /// </summary>
        private int GetCurrentBorderHeight()
        {
            int rows = (_pixelRegisters[0x11] & 0x08) != 0 ? VisibleRows : NarrowVisibleRows;
            return rows * CharacterHeight;
        }

        /// <summary>
        /// Gets the horizontal scroll phase value.
        /// </summary>
        private int GetHorizontalScrollPhase()
        {
            int alignedScroll = _line40Column ? 0 : 7;
            return alignedScroll - _lineXScroll;
        }

        /// <summary>
        /// Gets the current horizontal scroll phase value.
        /// </summary>
        private int GetCurrentHorizontalScrollPhase()
        {
            int alignedScroll = _displaySource40Column ? 0 : 7;
            return alignedScroll - _displaySourceXScroll;
        }

        /// <summary>
        /// Gets the vertical scroll phase value.
        /// </summary>
        private int GetVerticalScrollPhase()
        {
            int alignedScroll = _line25Row ? 3 : 7;
            return alignedScroll - _lineYScroll;
        }

        /// <summary>
        /// Gets the current vertical scroll phase value.
        /// </summary>
        private int GetCurrentVerticalScrollPhase()
        {
            int alignedScroll = _displaySource25Row ? 3 : 7;
            return alignedScroll - _displaySourceYScroll;
        }

        /// <summary>
        /// Returns whether inside active display area is true.
        /// </summary>
        private bool IsInsideActiveDisplayArea(int frameX, int frameY)
        {
            int currentBorderLeft = GetCurrentBorderLeftFrame();
            int currentBorderRight = currentBorderLeft + GetCurrentBorderWidth();
            int currentBorderTop = GetCurrentBorderTopFrame();
            int currentBorderBottom = currentBorderTop + GetCurrentBorderHeight();
            return frameX >= currentBorderLeft &&
                frameX < currentBorderRight &&
                frameY >= currentBorderTop &&
                frameY < currentBorderBottom;
        }

        /// <summary>
        /// Begins raster line.
        /// </summary>
        private void BeginRasterLine()
        {
            if (_rasterLine == 0)
            {
                _displayWindowFrameLatched = false;
                _graphicsDisplayState = false;
                _graphicsVc = 0;
                _graphicsVcBase = 0;
                _graphicsVmli = 0;
                _displaySequencer.ResetFrame();
                _graphicsRc = 7;
                _graphicsLineMatrixBaseIndex = -1;
                _graphicsLineCellY = -1;
                _graphicsLinePixelRow = -1;
                _textVcBase = 0;
                _textRc = 0;
                _previousTextDisplayLine = false;
                _bitmapVcBase = 0;
                _bitmapRc = 0;
                _previousBitmapDisplayLine = false;
            }

            _displaySequencer.BeginRasterLine();
            _rasterIrqTriggeredThisLine = false;
            LatchCurrentLineState();
            LatchDisplaySourceFromLineState();
            UpdateVerticalBorderState();
            _isBadLine = false;
            _badLineConditionThisCycle = false;
            _badLineConditionStartCycle = CyclesPerLine + 1;
            _earlyD011BadLinePulseBeforeCycle14 = false;
            _matrixFetchStartedThisLine = false;
            _matrixFetchRequestStartCycle = CyclesPerLine + 1;
            _matrixFetchStartCycle = CyclesPerLine + 1;
            _matrixFetchCpuBlockStartCycle = CyclesPerLine + 1;
            _graphicsLineMatrixBaseIndex = -1;
            _graphicsLineCellY = -1;
            _graphicsLinePixelRow = -1;
            _graphicsSequencerCellLoaded = false;
            _graphicsSequencerCellX = -1;
            _videoPatternValid = IsGraphicsDisplayLine();
            _videoPatternCellY = -1;
            _videoPatternPixelRow = -1;
            _videoPatternBitmapMode = _lineBitmapMode;
            System.Array.Clear(_videoPatternFetched, 0, _videoPatternFetched.Length);
            System.Array.Clear(_videoPatternBitmapModes, 0, _videoPatternBitmapModes.Length);
            System.Array.Clear(_videoPatternExtendedColorModes, 0, _videoPatternExtendedColorModes.Length);
            System.Array.Clear(_videoPatternMulticolorModes, 0, _videoPatternMulticolorModes.Length);
            System.Array.Clear(_videoPatternIdle, 0, _videoPatternIdle.Length);
            UpdateSpriteLineState();
            _busPlan.BuildLine(false, _spriteDmaActive);
            _currentBusSlot = _busPlan.GetSlot(0);
            ResetGraphicsOutputDelayLine();
        }

        /// <summary>
        /// Rebuilds sequencer helper state after loading older savestates.
        /// </summary>
        private void RepairDisplaySequencerAfterLoad()
        {
            bool hasSerializedSequencerState =
                _displaySequencer.MatrixFetchColumn != 0 ||
                _displaySequencer.PatternFetchColumn != 0 ||
                _displaySequencer.VideoCounterOffset != 0 ||
                _displaySequencer.VmliShiftRegister != 0 ||
                _displaySequencer.PreviousDisplayWindowActive;
            if (hasSerializedSequencerState)
            {
                return;
            }

            _displaySequencer.MatrixFetchColumn = _graphicsVmli;
            _displaySequencer.PatternFetchColumn = _graphicsVmli;
            _displaySequencer.VideoCounterOffset = NormalizeVideoMatrixIndex(_graphicsVc - _graphicsVcBase - _graphicsVmli) & 0x3F;
            _displaySequencer.PreviousDisplayWindowActive = _graphicsDisplayState &&
                (_cycleInLine + 1) >= 16 &&
                (_cycleInLine + 1) <= 55;
            int activeColumn = (_cycleInLine + 1) - 16;
            if (_displaySequencer.PreviousDisplayWindowActive &&
                activeColumn >= 0 &&
                activeColumn < VisibleColumns)
            {
                _displaySequencer.VmliShiftRegister = 1UL << activeColumn;
            }
            else if (_graphicsVmli > 0 && _graphicsVmli <= VisibleColumns)
            {
                _displaySequencer.VmliShiftRegister = 1UL << (_graphicsVmli - 1);
            }
        }

        /// <summary>
        /// Reconstructs latch metadata for savestates written before sprite DMA latching existed.
        /// </summary>
        private void RepairSpriteDmaLatchAfterLoad()
        {
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                if (_spriteDmaLatched[spriteIndex] || (!_spriteDmaActive[spriteIndex] && !_spriteLineVisible[spriteIndex]))
                {
                    continue;
                }

                _spriteDmaLatched[spriteIndex] = true;
                _spriteLatchedX[spriteIndex] = _registers[spriteIndex * 2] +
                    (((_registers[0x10] >> spriteIndex) & 0x01) != 0 ? 256 : 0);
                _spriteLatchedY[spriteIndex] = _registers[(spriteIndex * 2) + 1];
                _spriteLatchedXExpanded[spriteIndex] = ((_registers[0x1D] >> spriteIndex) & 0x01) != 0;
                _spriteLatchedYExpanded[spriteIndex] = ((_registers[0x17] >> spriteIndex) & 0x01) != 0;
                _spriteLatchedMulticolor[spriteIndex] = ((_registers[0x1C] >> spriteIndex) & 0x01) != 0;
                _spriteLatchedColor[spriteIndex] = _registers[0x27 + spriteIndex];
            }
        }

        /// <summary>
        /// Handles the reset graphics output delay line operation.
        /// </summary>
        private void ResetGraphicsOutputDelayLine()
        {
            PixelResult borderPixel = CreateBackgroundPixel(Palette[_registers[0x20] & 0x0F]);
            for (int i = 0; i < _graphicsOutputDelay.Length; i++)
            {
                _graphicsOutputDelay[i] = borderPixel;
            }

            _graphicsOutputDelayIndex = 0;
        }

        /// <summary>
        /// Handles the delay graphics pixel operation.
        /// </summary>
        private PixelResult DelayGraphicsPixel(PixelResult graphicsPixel)
        {
            if (_graphicsOutputDelay.Length == 0)
            {
                return graphicsPixel;
            }

            PixelResult delayedPixel = _graphicsOutputDelay[_graphicsOutputDelayIndex];
            _graphicsOutputDelay[_graphicsOutputDelayIndex] = graphicsPixel;
            _graphicsOutputDelayIndex++;
            if (_graphicsOutputDelayIndex >= _graphicsOutputDelay.Length)
            {
                _graphicsOutputDelayIndex = 0;
            }

            return delayedPixel;
        }

        /// <summary>
        /// Resolves pixels whose color comes from a live background register at the final output phase.
        /// </summary>
        private PixelResult ResolveOutputPixel(PixelResult pixel)
        {
            if (pixel.UsesRawGraphics)
            {
                pixel = ResolveRawGraphicsPixel(pixel);
            }

            if (!pixel.UsesBackgroundRegister)
            {
                return pixel;
            }

            pixel.Color = Palette[_pixelRegisters[0x21 + (pixel.BackgroundRegisterIndex & 0x03)] & 0x0F];
            return pixel;
        }

        /// <summary>
        /// Handles the latch current line state operation.
        /// </summary>
        private void LatchCurrentLineState()
        {
            _lineDisplayEnabled = _displayWindowFrameLatched;
            _line40Column = (_registers[0x16] & 0x08) != 0;
            _line25Row = (_registers[0x11] & 0x08) != 0;
            _lineBitmapMode = (_registers[0x11] & 0x20) != 0;
            _lineExtendedColorMode = (_registers[0x11] & 0x40) != 0;
            _lineMulticolorMode = (_registers[0x16] & 0x10) != 0;
            _lineXScroll = (byte)(_registers[0x16] & 0x07);
            _lineYScroll = (byte)(_registers[0x11] & 0x07);
            _lineScreenBaseAbsolute = GetScreenBaseAbsoluteFromRegisters();
            _lineCharacterBaseAbsolute = GetCharacterBaseAbsoluteFromRegisters();
            _lineBitmapBaseAbsolute = GetBitmapBaseAbsoluteFromRegisters();
            _lineDisplayLeftFrame = GetActiveDisplayLeftFrame();
            _lineDisplayTopFrame = GetActiveDisplayTopFrame();
            _lineDisplayRightFrame = _lineDisplayLeftFrame + GetActiveDisplayWidth();
            _lineDisplayBottomFrame = _lineDisplayTopFrame + GetActiveDisplayHeight();
            _horizontalBorderActive = !_horizontalSideBorderCarryOpen;
            _horizontalSideBorderCarryOpen = false;
        }

        /// <summary>
        /// Handles the latch display source from line state operation.
        /// </summary>
        private void LatchDisplaySourceFromLineState()
        {
            _displaySourceScreenBaseAbsolute = _lineScreenBaseAbsolute;
            _displaySourceCharacterBaseAbsolute = _lineCharacterBaseAbsolute;
            _displaySourceBitmapBaseAbsolute = _lineBitmapBaseAbsolute;
            _displaySource40Column = _line40Column;
            _displaySource25Row = _line25Row;
            _displaySourceXScroll = _lineXScroll;
            _displaySourceYScroll = _lineYScroll;
            _displaySourceBitmapMode = _lineBitmapMode;
            _displaySourceExtendedColorMode = _lineExtendedColorMode;
            _displaySourceMulticolorMode = _lineMulticolorMode;
        }

        /// <summary>
        /// Handles the latch display source for action operation.
        /// </summary>
        private void LatchDisplaySourceForAction(VicBusAction action)
        {
            if (!IsDisplaySourceFetch(action))
            {
                return;
            }

            ushort newScreenBase = GetScreenBaseAbsoluteFromRegisters();
            ushort newCharacterBase = GetCharacterBaseAbsoluteFromRegisters();
            ushort newBitmapBase = GetBitmapBaseAbsoluteFromRegisters();
            bool new40Column = (_registers[0x16] & 0x08) != 0;
            bool new25Row = (_registers[0x11] & 0x08) != 0;
            byte newXScroll = (byte)(_registers[0x16] & 0x07);
            byte newYScroll = (byte)(_registers[0x11] & 0x07);
            bool newBitmapMode = (_registers[0x11] & 0x20) != 0;
            bool newExtendedColorMode = (_registers[0x11] & 0x40) != 0;
            bool newMulticolorMode = (_registers[0x16] & 0x10) != 0;

            _displaySourceScreenBaseAbsolute = newScreenBase;
            _displaySourceCharacterBaseAbsolute = newCharacterBase;
            _displaySourceBitmapBaseAbsolute = newBitmapBase;
            _displaySource40Column = new40Column;
            _displaySource25Row = new25Row;
            _displaySourceXScroll = newXScroll;
            _displaySourceYScroll = newYScroll;
            _displaySourceBitmapMode = newBitmapMode;
            _displaySourceExtendedColorMode = newExtendedColorMode;
            _displaySourceMulticolorMode = newMulticolorMode;
        }

        /// <summary>
        /// Returns whether display source fetch is true.
        /// </summary>
        private static bool IsDisplaySourceFetch(VicBusAction action)
        {
            return action == VicBusAction.MatrixFetch ||
                action == VicBusAction.CharFetch;
        }

        /// <summary>
        /// Executes phi2 fetch action.
        /// </summary>
        private void ExecutePhi2FetchAction()
        {
            if (_currentBusSlot.Phi2Action == VicBusAction.Idle)
            {
                return;
            }

            if (_bus.VicCanAccess)
            {
                ExecuteFetchAction(_currentBusSlot.Phi2Action);
                return;
            }

            ExecuteInvalidPhi2Action(_currentBusSlot.Phi2Action);
        }

        /// <summary>
        /// Executes invalid phi2 action.
        /// </summary>
        private void ExecuteInvalidPhi2Action(VicBusAction action)
        {
            LatchDisplaySourceForAction(action);
            switch (action)
            {
                case VicBusAction.MatrixFetch:
                    if (IsMatrixFetchBeforeAecTakesBus())
                    {
                        FetchInvalidMatrixCell();
                    }
                    break;
                case VicBusAction.SpriteDataFetch:
                    FetchInvalidSpriteData(_currentBusSlot.SpriteIndex);
                    break;
            }
        }

        /// <summary>
        /// Executes fetch action.
        /// </summary>
        private void ExecuteFetchAction(VicBusAction action)
        {
            LatchDisplaySourceForAction(action);
            switch (action)
            {
                case VicBusAction.MatrixFetch:
                    FetchMatrixCell();
                    break;
                case VicBusAction.CharFetch:
                    FetchPatternCell();
                    break;
                case VicBusAction.SpritePointerFetch:
                    FetchSpritePointer(_currentBusSlot.SpriteIndex);
                    break;
                case VicBusAction.SpriteDataFetch:
                    FetchSpriteData(_currentBusSlot.SpriteIndex);
                    break;
            }
        }

        /// <summary>
        /// Fetches matrix cell.
        /// </summary>
        private void FetchMatrixCell()
        {
            if (!_videoMatrixValid)
            {
                return;
            }

            int cellX = _graphicsVmli;
            int matrixIndex = NormalizeVideoMatrixIndex(_graphicsVc);
            // Track the DMLI side independently while keeping the established
            // VC/VMLI decode path for visible data until the mixer consumes it.
            _displaySequencer.AdvanceMatrixFetch();
            if ((uint)cellX >= VisibleColumns)
            {
                return;
            }

            _videoMatrixScreenCodes[cellX] = ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));
            _videoMatrixColorNibbles[cellX] = _bus.ReadColorRam((ushort)matrixIndex);
            _videoMatrixFetched[cellX] = true;
            _videoMatrixBitmapModes[cellX] = _displaySourceBitmapMode;
            _videoMatrixExtendedColorModes[cellX] = _displaySourceExtendedColorMode;
            _videoMatrixMulticolorModes[cellX] = _displaySourceMulticolorMode;
        }

        /// <summary>
        /// Returns whether the component can use latched matrix cell.
        /// </summary>
        private bool CanUseLatchedMatrixCell(int cellX)
        {
            if ((uint)cellX >= VisibleColumns)
            {
                return false;
            }

            if (!_videoMatrixValid || !_videoMatrixFetched[cellX])
            {
                return false;
            }

            if (_badLineConditionThisCycle && (_cycleInLine + 1) < _matrixFetchStartCycle)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the matrix latch selected by the display sequencer's VMLI mask.
        /// </summary>
        private bool TryReadDisplaySequencerMatrixCell(
            int fallbackCellX,
            int fallbackMatrixIndex,
            out byte screenCode,
            out byte colorNibble,
            out bool bitmapMode,
            out bool extendedColorMode,
            out bool multicolorMode)
        {
            ulong readMask = GetDisplaySequencerReadMask(fallbackCellX);
            if (readMask == 0)
            {
                screenCode = 0;
                colorNibble = 0;
                bitmapMode = _displaySourceBitmapMode;
                extendedColorMode = _displaySourceExtendedColorMode;
                multicolorMode = _displaySourceMulticolorMode;
                return false;
            }

            bool hasValue = false;
            bool hasMultipleColumns = (readMask & (readMask - 1)) != 0;
            screenCode = hasMultipleColumns ? (byte)0xFF : (byte)0x00;
            colorNibble = hasMultipleColumns ? (byte)0x0F : (byte)0x00;
            bitmapMode = _displaySourceBitmapMode;
            extendedColorMode = _displaySourceExtendedColorMode;
            multicolorMode = _displaySourceMulticolorMode;

            for (int column = 0; column < VisibleColumns; column++)
            {
                ulong columnMask = 1UL << column;
                if ((readMask & columnMask) == 0)
                {
                    continue;
                }

                byte candidateScreenCode;
                byte candidateColorNibble;
                bool candidateBitmapMode;
                bool candidateExtendedColorMode;
                bool candidateMulticolorMode;
                ReadDisplaySequencerMatrixColumn(
                    column,
                    fallbackMatrixIndex,
                    out candidateScreenCode,
                    out candidateColorNibble,
                    out candidateBitmapMode,
                    out candidateExtendedColorMode,
                    out candidateMulticolorMode);

                if (!hasValue)
                {
                    screenCode = candidateScreenCode;
                    colorNibble = candidateColorNibble;
                    bitmapMode = candidateBitmapMode;
                    extendedColorMode = candidateExtendedColorMode;
                    multicolorMode = candidateMulticolorMode;
                    hasValue = true;
                    continue;
                }

                screenCode &= candidateScreenCode;
                colorNibble = (byte)((colorNibble & candidateColorNibble) & 0x0F);
                bitmapMode &= candidateBitmapMode;
                extendedColorMode &= candidateExtendedColorMode;
                multicolorMode &= candidateMulticolorMode;
            }

            if (hasValue && hasMultipleColumns)
            {
                WriteBackDisplaySequencerMixedMatrix(readMask, screenCode, colorNibble, bitmapMode, extendedColorMode, multicolorMode);
            }

            return hasValue;
        }

        /// <summary>
        /// Builds the matrix-read mask used by the current display sequencer access.
        /// </summary>
        private ulong GetDisplaySequencerReadMask(int fallbackCellX)
        {
            ulong readMask = _displaySequencer.VmliShiftRegister & DisplaySequencerColumnMask;
            if (readMask != 0)
            {
                return readMask;
            }

            if ((uint)fallbackCellX >= VisibleColumns)
            {
                return 0;
            }

            return 1UL << fallbackCellX;
        }

        /// <summary>
        /// Reads one matrix column from the latch or from the current display source.
        /// </summary>
        private void ReadDisplaySequencerMatrixColumn(
            int column,
            int fallbackMatrixIndex,
            out byte screenCode,
            out byte colorNibble,
            out bool bitmapMode,
            out bool extendedColorMode,
            out bool multicolorMode)
        {
            if (CanUseLatchedMatrixCell(column))
            {
                screenCode = _videoMatrixScreenCodes[column];
                colorNibble = _videoMatrixColorNibbles[column];
                bitmapMode = _videoMatrixBitmapModes[column];
                extendedColorMode = _videoMatrixExtendedColorModes[column];
                multicolorMode = _videoMatrixMulticolorModes[column];
                return;
            }

            int matrixIndex = NormalizeVideoMatrixIndex(_graphicsVcBase + column);
            if ((uint)column >= VisibleColumns)
            {
                matrixIndex = fallbackMatrixIndex;
            }

            screenCode = ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));
            colorNibble = _bus.ReadColorRam((ushort)matrixIndex);
            bitmapMode = _displaySourceBitmapMode;
            extendedColorMode = _displaySourceExtendedColorMode;
            multicolorMode = _displaySourceMulticolorMode;
        }

        /// <summary>
        /// Stores a mixed multi-column sequencer read back into all selected latches.
        /// </summary>
        private void WriteBackDisplaySequencerMixedMatrix(
            ulong readMask,
            byte screenCode,
            byte colorNibble,
            bool bitmapMode,
            bool extendedColorMode,
            bool multicolorMode)
        {
            if (!_videoMatrixValid)
            {
                return;
            }

            for (int column = 0; column < VisibleColumns; column++)
            {
                if ((readMask & (1UL << column)) == 0)
                {
                    continue;
                }

                _videoMatrixScreenCodes[column] = screenCode;
                _videoMatrixColorNibbles[column] = (byte)(colorNibble & 0x0F);
                _videoMatrixFetched[column] = true;
                _videoMatrixBitmapModes[column] = bitmapMode;
                _videoMatrixExtendedColorModes[column] = extendedColorMode;
                _videoMatrixMulticolorModes[column] = multicolorMode;
            }
        }

        /// <summary>
        /// Returns whether a matrix fetch happens while AEC still leaves the CPU-side bus visible.
        /// </summary>
        private bool IsMatrixFetchBeforeAecTakesBus()
        {
            int cycle = _cycleInLine + 1;
            return _matrixFetchStartedThisLine &&
                cycle >= _matrixFetchStartCycle &&
                cycle < _matrixFetchCpuBlockStartCycle;
        }

        /// <summary>
        /// Fetches invalid matrix cell.
        /// </summary>
        private void FetchInvalidMatrixCell()
        {
            if (!_videoMatrixValid)
            {
                return;
            }

            int cellX = _graphicsVmli;
            // Invalid c-accesses still move the tracked DMLI side of the sequencer.
            _displaySequencer.AdvanceMatrixFetch();
            if ((uint)cellX >= VisibleColumns)
            {
                return;
            }

            // During DMA-delay style c-accesses with AEC still high, D0-D7 float high.
            // The upper nibble is fed from the CPU-side bus through the color RAM path,
            // so use the current CPU bus low nibble instead of a hard-coded 0x0F.
            _videoMatrixScreenCodes[cellX] = 0xFF;
            _videoMatrixColorNibbles[cellX] = (byte)(_bus.LastCpuBusValue & 0x0F);
            _videoMatrixFetched[cellX] = true;
            _videoMatrixBitmapModes[cellX] = _displaySourceBitmapMode;
            _videoMatrixExtendedColorModes[cellX] = _displaySourceExtendedColorMode;
            _videoMatrixMulticolorModes[cellX] = _displaySourceMulticolorMode;
        }

        /// <summary>
        /// Fetches invalid sprite data.
        /// </summary>
        private void FetchInvalidSpriteData(int spriteIndex)
        {
            if ((uint)spriteIndex >= 8 || !_spriteDmaActive[spriteIndex])
            {
                return;
            }

            int fetchPhase = _spriteFetchPhase[spriteIndex];
            if (fetchPhase >= 3)
            {
                return;
            }

            if (fetchPhase == 0)
            {
                _spriteFetchStartMc[spriteIndex] = _spriteMc[spriteIndex] & 0x3F;
            }

            switch (fetchPhase)
            {
                case 0:
                    _spriteDataByte0[spriteIndex] = 0xFF;
                    break;
                case 1:
                    _spriteDataByte1[spriteIndex] = 0xFF;
                    break;
                default:
                    _spriteDataByte2[spriteIndex] = 0xFF;
                    _spriteDisplayRow[spriteIndex] = (_spriteFetchStartMc[spriteIndex] & 0x3F) / 3;
                    _spriteDisplayRowAdjusted[spriteIndex] = _spriteFetchRowAdjusted[spriteIndex] ||
                        ((_spriteFetchStartMc[spriteIndex] & 0x3F) != ((_spriteFetchRow[spriteIndex] * 3) & 0x3F));
                    _spriteDataValid[spriteIndex] = true;
                    CaptureSpriteLineDataForCurrentDma(spriteIndex);
                    break;
            }

            _spriteMc[spriteIndex] = (_spriteMc[spriteIndex] + 1) & 0x3F;
            _spriteFetchPhase[spriteIndex] = fetchPhase + 1;
        }

        /// <summary>
        /// Fetches pattern cell.
        /// </summary>
        private void FetchPatternCell()
        {
            if (!_videoPatternValid)
            {
                return;
            }

            int cycle = _cycleInLine + 1;
            int pixelRow = _graphicsLinePixelRow >= 0 ? _graphicsLinePixelRow : (_graphicsRc & 0x07);

            if (_graphicsDisplayState)
            {
                int cellX = _displaySequencer.PatternFetchColumn;
                if ((uint)cellX >= VisibleColumns)
                {
                    return;
                }

                int matrixIndex = NormalizeVideoMatrixIndex(_graphicsVc);
                byte sequencerScreenCode;
                byte sequencerColorNibble;
                bool sequencerBitmapMode;
                bool sequencerExtendedColorMode;
                bool sequencerMulticolorMode;
                bool hasSequencerMatrixCell = TryReadDisplaySequencerMatrixCell(
                    cellX,
                    matrixIndex,
                    out sequencerScreenCode,
                    out sequencerColorNibble,
                    out sequencerBitmapMode,
                    out sequencerExtendedColorMode,
                    out sequencerMulticolorMode);
                bool patternBitmapMode = hasSequencerMatrixCell
                    ? sequencerBitmapMode
                    : _displaySourceBitmapMode;
                bool patternExtendedColorMode = hasSequencerMatrixCell
                    ? sequencerExtendedColorMode
                    : _displaySourceExtendedColorMode;
                bool patternMulticolorMode = hasSequencerMatrixCell
                    ? sequencerMulticolorMode
                    : _displaySourceMulticolorMode;

                if (patternBitmapMode)
                {
                    _videoPatternBytes[cellX] = ReadDisplaySourceBitmapPattern(matrixIndex, pixelRow);
                }
                else
                {
                    byte screenCode = hasSequencerMatrixCell
                        ? sequencerScreenCode
                        : ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));

                    _videoPatternBytes[cellX] = ReadCharacterPatternFromSource(
                        _displaySourceCharacterBaseAbsolute,
                        patternExtendedColorMode,
                        pixelRow,
                        screenCode);
                }

                _videoPatternFetched[cellX] = true;
                _videoPatternBitmapModes[cellX] = patternBitmapMode;
                _videoPatternExtendedColorModes[cellX] = patternExtendedColorMode;
                _videoPatternMulticolorModes[cellX] = patternMulticolorMode;
                _videoPatternIdle[cellX] = false;
                _videoPatternBitmapMode = patternBitmapMode;
                _displaySequencer.AdvancePatternFetch();
                SynchronizeLegacyDisplaySequencerFields();
                return;
            }

            if (cycle < 16 || cycle > 55)
            {
                return;
            }

            int idleCellX = cycle - 16;
            if ((uint)idleCellX >= VisibleColumns)
            {
                return;
            }

            _videoPatternBytes[idleCellX] = ReadIdlePattern(_displaySourceExtendedColorMode);
            _videoPatternFetched[idleCellX] = true;
            _videoPatternBitmapModes[idleCellX] = _displaySourceBitmapMode;
            _videoPatternExtendedColorModes[idleCellX] = _displaySourceExtendedColorMode;
            _videoPatternMulticolorModes[idleCellX] = _displaySourceMulticolorMode;
            _videoPatternIdle[idleCellX] = true;
            _videoPatternBitmapMode = _displaySourceBitmapMode;
        }

        /// <summary>
        /// Samples DEN phases that are visible only after the CPU side has had its write slot.
        /// </summary>
        private void UpdateDisplayEnableLatchesAfterCpuAccess()
        {
            int cycle = _cycleInLine;
            bool denEnabled = (_registers[0x11] & 0x10) != 0;
            if (_rasterLine == DenLatchRasterLine - 1 && cycle == 60)
            {
                _displayEnableFrameLatched = denEnabled;
            }

            if (_rasterLine == DenLatchRasterLine && cycle <= 61 && denEnabled)
            {
                _displayEnableFrameLatched = true;
            }

            if (_rasterLine == (CropTop + BorderTop - 1) && cycle == 61)
            {
                _displayWindowFrameLatched = denEnabled;
            }
        }

        /// <summary>
        /// Fetches sprite pointer.
        /// </summary>
        private void FetchSpritePointer(int spriteIndex)
        {
            if ((uint)spriteIndex >= 8)
            {
                return;
            }

            ushort pointerAddress = (ushort)(GetScreenBaseAbsoluteFromRegisters() + 0x03F8 + spriteIndex);
            _spritePointers[spriteIndex] = ReadVicAbsolute(pointerAddress);
        }

        /// <summary>
        /// Fetches sprite data.
        /// </summary>
        private void FetchSpriteData(int spriteIndex)
        {
            if ((uint)spriteIndex >= 8 || !_spriteDmaActive[spriteIndex])
            {
                return;
            }

            int fetchPhase = _spriteFetchPhase[spriteIndex];
            if (fetchPhase >= 3)
            {
                return;
            }

            if (fetchPhase == 0)
            {
                _spriteFetchStartMc[spriteIndex] = _spriteMc[spriteIndex] & 0x3F;
            }

            int mc = _spriteMc[spriteIndex] & 0x3F;
            ushort spriteAddress = (ushort)(GetVicBankBase() + (_spritePointers[spriteIndex] * 64) + mc);
            byte value = ReadVicAbsolute(spriteAddress);
            switch (fetchPhase)
            {
                case 0:
                    _spriteDataByte0[spriteIndex] = value;
                    break;
                case 1:
                    _spriteDataByte1[spriteIndex] = value;
                    break;
                default:
                    _spriteDataByte2[spriteIndex] = value;
                    _spriteDisplayRow[spriteIndex] = (_spriteFetchStartMc[spriteIndex] & 0x3F) / 3;
                    _spriteDisplayRowAdjusted[spriteIndex] = _spriteFetchRowAdjusted[spriteIndex] ||
                        ((_spriteFetchStartMc[spriteIndex] & 0x3F) != ((_spriteFetchRow[spriteIndex] * 3) & 0x3F));
                    _spriteDataValid[spriteIndex] = true;
                    CaptureSpriteLineDataForCurrentDma(spriteIndex);
                    break;
            }

            _spriteMc[spriteIndex] = (_spriteMc[spriteIndex] + 1) & 0x3F;
            _spriteFetchPhase[spriteIndex] = fetchPhase + 1;
        }

        /// <summary>
        /// Begins video matrix fetch sequence.
        /// </summary>
        private void BeginVideoMatrixFetchSequence()
        {
            _videoMatrixValid = true;
            _videoMatrixCellY = _graphicsLineCellY;
            _videoMatrixBitmapMode = _displaySourceBitmapMode;
            System.Array.Clear(_videoMatrixFetched, 0, _videoMatrixFetched.Length);
            System.Array.Clear(_videoMatrixBitmapModes, 0, _videoMatrixBitmapModes.Length);
            System.Array.Clear(_videoMatrixExtendedColorModes, 0, _videoMatrixExtendedColorModes.Length);
            System.Array.Clear(_videoMatrixMulticolorModes, 0, _videoMatrixMulticolorModes.Length);
        }

        /// <summary>
        /// Mirrors the new sequencer columns into the legacy VC/VMLI diagnostics.
        /// </summary>
        private void SynchronizeLegacyDisplaySequencerFields()
        {
            _graphicsVmli = _displaySequencer.PatternFetchColumn;
            _graphicsVc = NormalizeVideoMatrixIndex(
                _graphicsVcBase +
                _displaySequencer.VideoCounterOffset +
                _displaySequencer.PatternFetchColumn);
        }

        /// <summary>
        /// Updates graphics state for current cycle.
        /// </summary>
        private void UpdateGraphicsStateForCurrentCycle()
        {
            int cycle = _cycleInLine + 1;
            bool clockDisplaySequencer = true;

            if (_rasterLine == DenLatchRasterLine && cycle <= 61 && (_registers[0x11] & 0x10) != 0)
            {
                _displayEnableFrameLatched = true;
            }

            bool badLineCondition = GetBadLineConditionForCurrentCycle();
            _badLineConditionThisCycle = badLineCondition;
            if (badLineCondition && _badLineConditionStartCycle > CyclesPerLine)
            {
                _badLineConditionStartCycle = cycle;
            }

            if (cycle == 14)
            {
                _graphicsVc = NormalizeVideoMatrixIndex(_graphicsVcBase);
                _graphicsVmli = 0;
                _displaySequencer.RestartFetchColumns();
                if (badLineCondition || _earlyD011BadLinePulseBeforeCycle14)
                {
                    _isBadLine = true;
                    _graphicsRc = 0;
                    _graphicsDisplayState = true;
                    _matrixFetchStartedThisLine = true;
                    _matrixFetchRequestStartCycle = 12;
                    _matrixFetchStartCycle = 15;
                    _matrixFetchCpuBlockStartCycle = GetInitialBadLineCpuBlockStartCycle();
                }

                _earlyD011BadLinePulseBeforeCycle14 = false;
                UpdateGraphicsLineAddressState();
                if (_matrixFetchStartedThisLine)
                {
                    BeginVideoMatrixFetchSequence();
                }
            }

            if (!_matrixFetchStartedThisLine &&
                badLineCondition &&
                !_graphicsDisplayState &&
                cycle >= 15 &&
                cycle <= 53)
            {
                _isBadLine = true;
                if (!_graphicsDisplayState)
                {
                    _pendingGraphicsDisplayState = true;
                    _pendingGraphicsDisplayStateCycle = cycle;
                    _pendingGraphicsDisplayStateVideoCounterOffset = 0;
                    clockDisplaySequencer = false;
                }

                _matrixFetchStartedThisLine = true;
                _matrixFetchRequestStartCycle = cycle;
                _matrixFetchStartCycle = cycle;
                _matrixFetchCpuBlockStartCycle = cycle + 3;
                BeginVideoMatrixFetchSequence();
            }

            if (cycle == 58)
            {
                if (_graphicsRc == 7)
                {
                    _graphicsDisplayState = false;
                    _graphicsVcBase = NormalizeVideoMatrixIndex(_graphicsVc);
                }

                if (badLineCondition)
                {
                    _graphicsDisplayState = true;
                }

                if (_graphicsDisplayState)
                {
                    _graphicsRc = (_graphicsRc + 1) & 0x07;
                }
            }

            if (clockDisplaySequencer)
            {
                _displaySequencer.ClockVmli(cycle, _graphicsDisplayState);
            }
        }

        /// <summary>
        /// Applies a late display-state switch after the current Phi1 graphics fetch.
        /// </summary>
        private void ApplyPendingGraphicsDisplayStateAfterPhi1()
        {
            if (!_pendingGraphicsDisplayState)
            {
                return;
            }

            int cycle = _pendingGraphicsDisplayStateCycle;
            _graphicsDisplayState = true;
            _displaySequencer.RestartFetchColumns(_pendingGraphicsDisplayStateVideoCounterOffset);
            SynchronizeLegacyDisplaySequencerFields();
            _displaySequencer.ClockVmli(cycle, _graphicsDisplayState);
            _pendingGraphicsDisplayState = false;
            _pendingGraphicsDisplayStateCycle = 64;
            _pendingGraphicsDisplayStateVideoCounterOffset = 0;
        }

        /// <summary>
        /// Returns the earliest cycle where the badline BA request can begin for this line.
        /// </summary>
        private int GetInitialBadLineCpuBlockStartCycle()
        {
            return _badLineConditionStartCycle <= 13
                ? 15
                : _badLineConditionStartCycle + 3;
        }

        /// <summary>
        /// Applies graphics bus overrides.
        /// </summary>
        private void ApplyGraphicsBusOverrides()
        {
            int cycle = _cycleInLine + 1;
            if (!_matrixFetchStartedThisLine)
            {
                if (_badLineConditionThisCycle && cycle >= 12 && cycle < 15)
                {
                    _currentBusSlot.BusRequestPending = true;
                }

                return;
            }

            VicBusPlan.ApplyMatrixFetchToSlot(
                ref _currentBusSlot,
                cycle,
                _matrixFetchRequestStartCycle,
                _matrixFetchStartCycle,
                _matrixFetchCpuBlockStartCycle,
                54);
        }

        /// <summary>
        /// Updates graphics line address state.
        /// </summary>
        private void UpdateGraphicsLineAddressState()
        {
            if (!IsGraphicsDisplayLine())
            {
                _graphicsLineMatrixBaseIndex = -1;
                _graphicsLineCellY = -1;
                _graphicsLinePixelRow = -1;
                _videoPatternValid = false;
                _videoPatternCellY = -1;
                _videoPatternPixelRow = -1;
                System.Array.Clear(_videoPatternFetched, 0, _videoPatternFetched.Length);
                System.Array.Clear(_videoPatternBitmapModes, 0, _videoPatternBitmapModes.Length);
                System.Array.Clear(_videoPatternExtendedColorModes, 0, _videoPatternExtendedColorModes.Length);
                System.Array.Clear(_videoPatternMulticolorModes, 0, _videoPatternMulticolorModes.Length);
                System.Array.Clear(_videoPatternIdle, 0, _videoPatternIdle.Length);
                return;
            }

            _graphicsLineMatrixBaseIndex = NormalizeVideoMatrixIndex(_graphicsVcBase);
            _graphicsLineCellY = _graphicsLineMatrixBaseIndex / VisibleColumns;
            _graphicsLinePixelRow = _graphicsRc & 0x07;
            _videoPatternValid = true;
            _videoPatternCellY = _graphicsLineCellY;
            _videoPatternPixelRow = _graphicsLinePixelRow;
            _videoPatternBitmapMode = _lineBitmapMode;
            System.Array.Clear(_videoPatternFetched, 0, _videoPatternFetched.Length);
            System.Array.Clear(_videoPatternBitmapModes, 0, _videoPatternBitmapModes.Length);
            System.Array.Clear(_videoPatternExtendedColorModes, 0, _videoPatternExtendedColorModes.Length);
            System.Array.Clear(_videoPatternMulticolorModes, 0, _videoPatternMulticolorModes.Length);
            System.Array.Clear(_videoPatternIdle, 0, _videoPatternIdle.Length);
        }

        /// <summary>
        /// Gets the bad line condition for current cycle value.
        /// </summary>
        private bool GetBadLineConditionForCurrentCycle()
        {
            return _displayEnableFrameLatched &&
                _rasterLine >= 0x30 &&
                _rasterLine <= 0xF7 &&
                ((_rasterLine & 0x07) == (_registers[0x11] & 0x07));
        }

        /// <summary>
        /// Handles the normalize video matrix index operation.
        /// </summary>
        private static int NormalizeVideoMatrixIndex(int index)
        {
            return index & 0x03FF;
        }

        /// <summary>
        /// Gets the graphics line matrix base index value.
        /// </summary>
        private int GetGraphicsLineMatrixBaseIndex(int fallbackCellY)
        {
            if (_graphicsLineMatrixBaseIndex >= 0)
            {
                return _graphicsLineMatrixBaseIndex;
            }

            return NormalizeVideoMatrixIndex(fallbackCellY * VisibleColumns);
        }

        /// <summary>
        /// Updates vertical border state.
        /// </summary>
        private void UpdateVerticalBorderState()
        {
            int top = GetCurrentBorderTopFrame() + CropTop;
            int bottom = top + GetCurrentBorderHeight();

            if (_rasterLine == 0)
            {
                _verticalBorderActive = true;
                _pendingVerticalBorderCloseRasterLine = NoPendingRasterLine;
            }
            else if (_rasterLine == _pendingVerticalBorderCloseRasterLine)
            {
                _verticalBorderActive = true;
                _pendingVerticalBorderCloseRasterLine = NoPendingRasterLine;
            }
            else if (_pendingVerticalBorderCloseRasterLine != NoPendingRasterLine &&
                _rasterLine > _pendingVerticalBorderCloseRasterLine)
            {
                _pendingVerticalBorderCloseRasterLine = NoPendingRasterLine;
            }
            else if (_lineDisplayEnabled && _rasterLine == top)
            {
                _verticalBorderActive = false;
            }
            else if (_rasterLine == bottom)
            {
                _verticalBorderActive = true;
            }
        }

        /// <summary>
        /// Updates horizontal border state.
        /// </summary>
        private void UpdateHorizontalBorderState(int frameX, int frameY)
        {
            int currentBorderTop = GetCurrentBorderTopFrame();
            int currentBorderBottom = currentBorderTop + GetCurrentBorderHeight();
            if (frameY < currentBorderTop)
            {
                _horizontalBorderActive = true;
                return;
            }

            int currentBorderLeft = GetCurrentBorderLeftFrame();
            int currentBorderRight = currentBorderLeft + GetCurrentBorderWidth();
            if (frameX == currentBorderLeft)
            {
                if (frameY == currentBorderBottom)
                {
                    _verticalBorderActive = true;
                }
                else if (frameY == currentBorderTop && _lineDisplayEnabled)
                {
                    _verticalBorderActive = false;
                }

                if (_lineDisplayEnabled && !_verticalBorderActive)
                {
                    _horizontalBorderActive = false;
                }
            }

            if (frameX == currentBorderRight)
            {
                _horizontalBorderActive = true;
            }
        }

        /// <summary>
        /// Tracks late RSEL writes that can still trip the bottom vertical-border flip-flop.
        /// </summary>
        private void TrackVerticalBorderRselWrite(byte value)
        {
            bool rowSelectCleared = (value & 0x08) == 0;
            if (!rowSelectCleared || !_displayEnableFrameLatched || _verticalBorderActive)
            {
                return;
            }

            int cycle = _cycleInLine;
            if (_rasterLine == NarrowBottomBorderRasterLine - 1)
            {
                if (cycle >= 58)
                {
                    _pendingVerticalBorderCloseRasterLine = NarrowBottomBorderRasterLine;
                }

                return;
            }

            if (_rasterLine != NarrowBottomBorderRasterLine)
            {
                return;
            }

            if (cycle <= 13)
            {
                _verticalBorderActive = true;
            }
            else if (cycle <= 60)
            {
                _pendingVerticalBorderCloseRasterLine = NarrowBottomBorderRasterLine + 1;
            }
        }

        /// <summary>
        /// Updates sprite line state.
        /// </summary>
        private void UpdateSpriteLineState()
        {
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                bool enabled = ((_registers[0x15] >> spriteIndex) & 0x01) != 0;
                if (!enabled && !_spriteDmaLatched[spriteIndex])
                {
                    _spriteLineVisible[spriteIndex] = false;
                    _spriteLineDataValid[spriteIndex] = false;
                    _spriteDmaActive[spriteIndex] = false;
                    _spriteDmaLatched[spriteIndex] = false;
                    _spriteDataValid[spriteIndex] = false;
                    _spriteFetchPhase[spriteIndex] = 0;
                    _spriteMc[spriteIndex] = 0;
                    _spriteMcBase[spriteIndex] = 0;
                    _spriteFetchStartMc[spriteIndex] = 0;
                    _spriteRowHistoryActive[spriteIndex] = false;
                    _spritePreviousLineYExpanded[spriteIndex] = false;
                    _spriteFetchRowAdjusted[spriteIndex] = false;
                    _spriteDisplayRowAdjusted[spriteIndex] = false;
                    _spriteLineDisplayRowAdjusted[spriteIndex] = false;
                    continue;
                }

                if (!_spriteDmaLatched[spriteIndex])
                {
                    _spriteLineVisible[spriteIndex] = false;
                    _spriteLineDisplayRowAdjusted[spriteIndex] = false;
                    _spriteDmaActive[spriteIndex] = false;
                    _spriteMc[spriteIndex] = 0;
                    _spriteMcBase[spriteIndex] = 0;
                    _spriteFetchStartMc[spriteIndex] = 0;
                    continue;
                }

                _spriteFetchPhase[spriteIndex] = 0;
                UpdateLatchedSpriteLineState(spriteIndex, false);
            }
        }

        /// <summary>
        /// Updates sprite DMA start at the VIC-II Y-compare point.
        /// </summary>
        private void UpdateSpriteDmaStartForCurrentCycle()
        {
            int cycle = _cycleInLine + 1;
            if (cycle != 55)
            {
                return;
            }

            bool activatedAnySprite = false;
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                if (_spriteDmaLatched[spriteIndex])
                {
                    continue;
                }

                bool enabled = ((_registers[0x15] >> spriteIndex) & 0x01) != 0;
                int spriteY = _registers[(spriteIndex * 2) + 1];
                if (!enabled || _rasterLine != spriteY)
                {
                    continue;
                }

                LatchSpriteDma(spriteIndex);
                UpdateLatchedSpriteLineState(spriteIndex, true);
                _busPlan.ActivateSpriteDma(spriteIndex);
                activatedAnySprite = true;
            }

            if (activatedAnySprite)
            {
                _currentBusSlot = _busPlan.GetSlot(_cycleInLine);
            }
        }

        /// <summary>
        /// Loads each sprite data counter from its current MCBASE latch at the VIC-II cycle-58 point.
        /// </summary>
        private void LoadSpriteDataCountersForCurrentCycle()
        {
            if (_cycleInLine + 1 != 58)
            {
                return;
            }

            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                _spriteMc[spriteIndex] = _spriteMcBase[spriteIndex] & 0x3F;
            }
        }

        /// <summary>
        /// Advances sprite MCBASE from the Y-expansion flip-flop at the cycle-15/16 points.
        /// </summary>
        private void UpdateSpriteMcBaseForCurrentCycle()
        {
            int cycle = _cycleInLine + 1;
            if (cycle != 15 && cycle != 16)
            {
                return;
            }

            int increment = cycle == 15 ? 2 : 1;
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                if (!_spriteDmaLatched[spriteIndex] && !_spriteDmaActive[spriteIndex])
                {
                    continue;
                }

                if (_spriteExpandFlipFlop[spriteIndex])
                {
                    _spriteMcBase[spriteIndex] = (_spriteMcBase[spriteIndex] + increment) & 0x3F;
                }

                if (cycle == 16 && _spriteMcBase[spriteIndex] == 63)
                {
                    _spriteDmaActive[spriteIndex] = false;
                    _spriteDmaLatched[spriteIndex] = false;
                    _spriteFetchPhase[spriteIndex] = 0;
                    _spriteDataValid[spriteIndex] = false;
                    _spriteRowHistoryActive[spriteIndex] = false;
                    _spritePreviousLineYExpanded[spriteIndex] = false;
                    _spriteFetchRowAdjusted[spriteIndex] = false;
                    _spriteDisplayRowAdjusted[spriteIndex] = false;
                }
            }
        }

        /// <summary>
        /// Releases early-line sprite DMA after the final sprite row has been fetched.
        /// </summary>
        private void UpdateEarlySpriteDmaEndForCurrentCycle()
        {
            if (_cycleInLine + 1 != 16)
            {
                return;
            }

            for (int spriteIndex = 3; spriteIndex < 8; spriteIndex++)
            {
                if (!_spriteDmaLatched[spriteIndex] || !_spriteDmaActive[spriteIndex])
                {
                    continue;
                }

                if (_spriteLatchedYExpanded[spriteIndex] || _spriteFetchRow[spriteIndex] < 20 || _spriteFetchPhase[spriteIndex] < 3)
                {
                    continue;
                }

                // Sprites 3-7 fetch their bytes at the start of the raster line.
                // Once row 20 is fetched, DMA is off before the cycle-55 Y compare,
                // while the already latched row remains visible for this line.
                _spriteDmaActive[spriteIndex] = false;
                _spriteDmaLatched[spriteIndex] = false;
                _spriteFetchPhase[spriteIndex] = 0;
                _spriteDataValid[spriteIndex] = false;
                _spriteMc[spriteIndex] = 0;
                _spriteMcBase[spriteIndex] = 0;
                _spriteFetchStartMc[spriteIndex] = 0;
                _spriteRowHistoryActive[spriteIndex] = false;
                _spritePreviousLineYExpanded[spriteIndex] = false;
                _spriteFetchRowAdjusted[spriteIndex] = false;
                _spriteDisplayRowAdjusted[spriteIndex] = false;
            }
        }

        /// <summary>
        /// Latches the current register values for a new sprite DMA instance.
        /// </summary>
        private void LatchSpriteDma(int spriteIndex)
        {
            int spriteX = _registers[spriteIndex * 2];
            if (((_registers[0x10] >> spriteIndex) & 0x01) != 0)
            {
                spriteX += 256;
            }

            bool yExpanded = ((_registers[0x17] >> spriteIndex) & 0x01) != 0;
            _spriteDmaLatched[spriteIndex] = true;
            _spriteLatchedX[spriteIndex] = spriteX;
            _spriteLatchedY[spriteIndex] = _registers[(spriteIndex * 2) + 1];
            _spriteLatchedXExpanded[spriteIndex] = ((_registers[0x1D] >> spriteIndex) & 0x01) != 0;
            _spriteLatchedYExpanded[spriteIndex] = yExpanded;
            _spriteLatchedMulticolor[spriteIndex] = ((_registers[0x1C] >> spriteIndex) & 0x01) != 0;
            _spriteLatchedColor[spriteIndex] = _registers[0x27 + spriteIndex];
            _spriteExpandFlipFlop[spriteIndex] = !yExpanded;
            _spriteMc[spriteIndex] = 0;
            _spriteMcBase[spriteIndex] = 0;
            _spriteFetchStartMc[spriteIndex] = 0;
            _spriteDataValid[spriteIndex] = false;
            _spriteFetchPhase[spriteIndex] = 0;
            _spriteRowHistoryActive[spriteIndex] = false;
            _spritePreviousLineYExpanded[spriteIndex] = yExpanded;
            _spriteFetchRowAdjusted[spriteIndex] = false;
            _spriteDisplayRowAdjusted[spriteIndex] = false;
        }

        /// <summary>
        /// Updates the current raster-line state for a latched sprite DMA instance.
        /// </summary>
        private void UpdateLatchedSpriteLineState(int spriteIndex, bool preserveCurrentLine)
        {
            int latchedY = _spriteLatchedY[spriteIndex];
            bool latchedYExpanded = IsSpriteYExpansionEnabled(spriteIndex);
            int spriteStart = GetSpriteVisibleStartRasterLine(latchedY, _spriteLatchedX[spriteIndex]);
            int visibleDelta = _rasterLine - spriteStart;
            int fetchDelta = spriteIndex <= 2 ? (_rasterLine - latchedY) : visibleDelta;
            int fetchRow = ComputeSpriteFetchRowForCurrentLine(spriteIndex, fetchDelta, latchedYExpanded);
            bool spriteDmaContinues = fetchDelta >= 0 && (_spriteDmaLatched[spriteIndex] || _spriteDmaActive[spriteIndex]);
            int visibleLines = latchedYExpanded ? 42 : 21;
            bool spriteLineVisible = spriteIndex <= 2
                ? (visibleDelta >= 0 && visibleDelta < visibleLines)
                : (visibleDelta >= 0 && spriteDmaContinues);

            if (spriteLineVisible)
            {
                LatchSpriteRenderLine(spriteIndex);
                if (latchedYExpanded)
                {
                    _spriteCurrentRow[spriteIndex] = visibleDelta / 2;
                }
                else
                {
                    _spriteCurrentRow[spriteIndex] = fetchRow;
                }
            }
            else if (!preserveCurrentLine)
            {
                _spriteLineVisible[spriteIndex] = false;
                _spriteLineDataValid[spriteIndex] = false;
                _spriteLineDisplayRowAdjusted[spriteIndex] = false;
            }

            _spriteDmaActive[spriteIndex] = fetchDelta >= 0;
            _spriteFetchRow[spriteIndex] = fetchRow;
        }

        /// <summary>
        /// Captures the sprites whose Y-expansion flip-flops are eligible for the VIC-II cycle-55 update.
        /// </summary>
        private void CaptureSpriteExpansionFlipFlopMaskForCurrentCycle()
        {
            if (_cycleInLine + 1 != 55)
            {
                _spriteCycle55ExpansionMask = 0;
                return;
            }

            byte spriteMask = 0;
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                if (_spriteDmaLatched[spriteIndex] || _spriteDmaActive[spriteIndex])
                {
                    spriteMask |= (byte)(1 << spriteIndex);
                }
            }

            _spriteCycle55ExpansionMask = spriteMask;
        }

        /// <summary>
        /// Inverts each enabled sprite Y-expansion flip-flop at the VIC-II cycle-55 phase.
        /// </summary>
        private void UpdateSpriteExpansionFlipFlopsForCurrentCycle()
        {
            if (_cycleInLine + 1 != 55 || _spriteCycle55ExpansionMask == 0)
            {
                return;
            }

            byte yExpansion = _registers[0x17];
            byte activeMask = _spriteCycle55ExpansionMask;
            _spriteCycle55ExpansionMask = 0;
            for (int spriteIndex = 0; spriteIndex < 8; spriteIndex++)
            {
                int mask = 1 << spriteIndex;
                if ((activeMask & mask) == 0)
                {
                    continue;
                }

                if ((yExpansion & mask) != 0)
                {
                    _spriteExpandFlipFlop[spriteIndex] = !_spriteExpandFlipFlop[spriteIndex];
                }
                else
                {
                    _spriteExpandFlipFlop[spriteIndex] = true;
                }
            }
        }

        /// <summary>
        /// Latches the sprite state used by the current raster line renderer.
        /// </summary>
        private void LatchSpriteRenderLine(int spriteIndex)
        {
            _spriteLineVisible[spriteIndex] = true;
            _spriteLineX[spriteIndex] = _spriteLatchedX[spriteIndex];
            _spriteLineY[spriteIndex] = _spriteLatchedY[spriteIndex];
            _spriteLineXExpanded[spriteIndex] = _spriteLatchedXExpanded[spriteIndex];
            _spriteLineYExpanded[spriteIndex] = IsSpriteYExpansionEnabled(spriteIndex);
            _spriteLineMulticolor[spriteIndex] = _spriteLatchedMulticolor[spriteIndex];
            _spriteLineColor[spriteIndex] = _spriteLatchedColor[spriteIndex];
            CaptureSpriteLineData(spriteIndex);
        }

        /// <summary>
        /// Copies the most recently fetched sprite data into the current raster line renderer.
        /// </summary>
        private void CaptureSpriteLineData(int spriteIndex)
        {
            if (!_spriteDataValid[spriteIndex])
            {
                _spriteLineDataValid[spriteIndex] = false;
                _spriteLineDisplayRowAdjusted[spriteIndex] = false;
                return;
            }

            _spriteLineDataByte0[spriteIndex] = _spriteDataByte0[spriteIndex];
            _spriteLineDataByte1[spriteIndex] = _spriteDataByte1[spriteIndex];
            _spriteLineDataByte2[spriteIndex] = _spriteDataByte2[spriteIndex];
            _spriteLineDisplayRow[spriteIndex] = _spriteDisplayRow[spriteIndex];
            _spriteLineDisplayRowAdjusted[spriteIndex] = _spriteDisplayRowAdjusted[spriteIndex];
            _spriteLineDataValid[spriteIndex] = true;
        }

        /// <summary>
        /// Updates current-line sprite data only when the line belongs to the active DMA instance.
        /// </summary>
        private void CaptureSpriteLineDataForCurrentDma(int spriteIndex)
        {
            if (!_spriteLineVisible[spriteIndex] || _spriteLineY[spriteIndex] != _spriteLatchedY[spriteIndex])
            {
                return;
            }

            CaptureSpriteLineData(spriteIndex);
        }

        /// <summary>
        /// Computes sprite fetch row.
        /// </summary>
        private static int ComputeSpriteFetchRow(int fetchDelta, bool yExpanded)
        {
            if (fetchDelta < 0)
            {
                return 0;
            }

            int row = yExpanded ? (fetchDelta / 2) : fetchDelta;
            if (row < 0)
            {
                return 0;
            }

            if (row > 20)
            {
                return 20;
            }

            return row;
        }

        /// <summary>
        /// Computes the sprite row for the current DMA fetch without losing expansion phase history.
        /// </summary>
        private int ComputeSpriteFetchRowForCurrentLine(int spriteIndex, int fetchDelta, bool yExpanded)
        {
            _spriteFetchRowAdjusted[spriteIndex] = false;
            bool wasYExpanded = _spritePreviousLineYExpanded[spriteIndex];
            _spritePreviousLineYExpanded[spriteIndex] = yExpanded;
            int row = yExpanded
                ? ComputeSpriteFetchRow(fetchDelta, true)
                : (fetchDelta < 0 ? 0 : fetchDelta);
            if (yExpanded || !_spriteDataValid[spriteIndex])
            {
                _spriteRowHistoryActive[spriteIndex] = false;
                return row > 21 ? 21 : row;
            }

            // Sprites 0-2 fetch their data at the end of the raster line, so their
            // Y-expansion transition needs a separate late-fetch model. Keep the
            // historical row correction on the early-fetch sprites for now.
            if (spriteIndex <= 2 || (!wasYExpanded && !_spriteRowHistoryActive[spriteIndex]))
            {
                return row > 21 ? 21 : row;
            }

            int nextSequentialRow = _spriteDisplayRow[spriteIndex] + 1;
            if (nextSequentialRow < row)
            {
                _spriteRowHistoryActive[spriteIndex] = true;
                _spriteFetchRowAdjusted[spriteIndex] = true;
                return nextSequentialRow;
            }

            return row > 21 ? 21 : row;
        }

        /// <summary>
        /// Returns whether sprite DMA should remain active for the current raster line.
        /// </summary>
        private static bool IsSpriteDmaContinuing(int fetchDelta, int fetchRow, bool yExpanded)
        {
            if (fetchDelta < 0)
            {
                return false;
            }

            if (yExpanded)
            {
                return fetchDelta < 42;
            }

            return fetchRow < 21;
        }

        /// <summary>
        /// Returns whether the current VIC register state enables Y expansion for a sprite.
        /// </summary>
        private bool IsSpriteYExpansionEnabled(int spriteIndex)
        {
            return ((_registers[0x17] >> spriteIndex) & 0x01) != 0;
        }

        /// <summary>
        /// Returns whether graphics display line is true.
        /// </summary>
        private bool IsGraphicsDisplayLine()
        {
            if (!_lineDisplayEnabled)
            {
                return false;
            }

            int frameY = _rasterLine - CropTop;
            return frameY >= _lineDisplayTopFrame && frameY < _lineDisplayBottomFrame;
        }

        /// <summary>
        /// Returns whether text display line is true.
        /// </summary>
        private bool IsTextDisplayLine()
        {
            if (!_lineDisplayEnabled || _lineBitmapMode)
            {
                return false;
            }

            int frameY = _rasterLine - CropTop;
            return frameY >= _lineDisplayTopFrame && frameY < _lineDisplayBottomFrame;
        }

        /// <summary>
        /// Returns whether bitmap display line is true.
        /// </summary>
        private bool IsBitmapDisplayLine()
        {
            if (!_lineDisplayEnabled || !_lineBitmapMode)
            {
                return false;
            }

            int frameY = _rasterLine - CropTop;
            return frameY >= _lineDisplayTopFrame && frameY < _lineDisplayBottomFrame;
        }

        /// <summary>
        /// Handles the trigger raster irq if matched operation.
        /// </summary>
        private void TriggerRasterIrqIfMatched()
        {
            if (!_rasterIrqCompareState && _rasterLine == _rasterIrqLine)
            {
                SetRasterIrqFlag();
                _rasterIrqCompareState = true;
            }
        }

        /// <summary>
        /// Gets the screen base absolute from registers value.
        /// </summary>
        private ushort GetScreenBaseAbsoluteFromRegisters()
        {
            return (ushort)(GetVicBankBase() + (((_registers[0x18] >> 4) & 0x0F) * 0x0400));
        }

        /// <summary>
        /// Gets the character base absolute from registers value.
        /// </summary>
        private ushort GetCharacterBaseAbsoluteFromRegisters()
        {
            return (ushort)(GetVicBankBase() + (((_registers[0x18] >> 1) & 0x07) * 0x0800));
        }

        /// <summary>
        /// Gets the bitmap base absolute from registers value.
        /// </summary>
        private ushort GetBitmapBaseAbsoluteFromRegisters()
        {
            return (ushort)(GetVicBankBase() + (((_registers[0x18] >> 3) & 0x01) * 0x2000));
        }

        /// <summary>
        /// Reads display source bitmap pattern.
        /// </summary>
        private byte ReadDisplaySourceBitmapPattern(int matrixIndex, int pixelYInCell)
        {
            return ReadVicAbsolute((ushort)(_displaySourceBitmapBaseAbsolute + (NormalizeVideoMatrixIndex(matrixIndex) * 8) + pixelYInCell));
        }

        /// <summary>
        /// Reads display source character pattern.
        /// </summary>
        private byte ReadDisplaySourceCharacterPattern(int pixelYInCell, byte screenCode)
        {
            int characterCode = _displaySourceExtendedColorMode ? (screenCode & 0x3F) : screenCode;
            ushort characterAddress = (ushort)(_displaySourceCharacterBaseAbsolute + (characterCode * 8) + pixelYInCell);
            return ReadVicAbsolute(characterAddress);
        }

        /// <summary>
        /// Reads bitmap pattern from source.
        /// </summary>
        private byte ReadBitmapPatternFromSource(ushort bitmapBaseAbsolute, int matrixIndex, int pixelYInCell)
        {
            return ReadVicAbsolute((ushort)(bitmapBaseAbsolute + (NormalizeVideoMatrixIndex(matrixIndex) * 8) + pixelYInCell));
        }

        /// <summary>
        /// Reads character pattern from source.
        /// </summary>
        private byte ReadCharacterPatternFromSource(ushort characterBaseAbsolute, bool extendedColorMode, int pixelYInCell, byte screenCode)
        {
            int characterCode = extendedColorMode ? (screenCode & 0x3F) : screenCode;
            ushort characterAddress = (ushort)(characterBaseAbsolute + (characterCode * 8) + pixelYInCell);
            return ReadVicAbsolute(characterAddress);
        }

        /// <summary>
        /// Reads idle pattern.
        /// </summary>
        private byte ReadIdlePattern(bool extendedColorMode)
        {
            ushort absoluteAddress = (ushort)(GetVicBankBase() + (extendedColorMode ? 0x39FF : 0x3FFF));
            return ReadVicAbsolute(absoluteAddress);
        }

        /// <summary>
        /// Returns whether current40 column mode is true.
        /// </summary>
        private bool IsCurrent40ColumnMode()
        {
            return (_registers[0x16] & 0x08) != 0;
        }

        /// <summary>
        /// Returns whether current25 row mode is true.
        /// </summary>
        private bool IsCurrent25RowMode()
        {
            return (_registers[0x11] & 0x08) != 0;
        }

        /// <summary>
        /// Gets the sprite frame left value.
        /// </summary>
        private static int GetSpriteFrameLeft(int spriteX)
        {
            return spriteX - SpriteVisibleOffsetX;
        }

        /// <summary>
        /// Gets the sprite frame top value.
        /// </summary>
        private static int GetSpriteFrameTop(int spriteY)
        {
            return (spriteY + SpriteRasterStartOffsetY) - SpriteVisibleOffsetY;
        }

        /// <summary>
        /// Gets the sprite visible start raster line value.
        /// </summary>
        private static int GetSpriteVisibleStartRasterLine(int spriteY, int spriteX)
        {
            return spriteX >= SpriteSameLineDisplayX ? spriteY : (spriteY + SpriteRasterStartOffsetY);
        }

        /// <summary>
        /// Creates background pixel.
        /// </summary>
        private static PixelResult CreateBackgroundPixel(uint color)
        {
            return new PixelResult
            {
                Color = color,
                GraphicsForeground = false
            };
        }

        /// <summary>
        /// Creates foreground pixel.
        /// </summary>
        private static PixelResult CreateForegroundPixel(uint color)
        {
            return new PixelResult
            {
                Color = color,
                GraphicsForeground = true
            };
        }

        /// <summary>
        /// Creates a pixel whose visible color is selected from one of the live VIC background registers.
        /// </summary>
        private static PixelResult CreateBackgroundRegisterPixel(int index, bool graphicsForeground)
        {
            return new PixelResult
            {
                Color = 0,
                GraphicsForeground = graphicsForeground,
                UsesBackgroundRegister = true,
                BackgroundRegisterIndex = (byte)(index & 0x03)
            };
        }

        /// <summary>
        /// Creates a raw graphics pixel that is resolved after the VIC graphics output delay.
        /// </summary>
        private static PixelResult CreateRawGraphicsPixel(int pixelXInCell, byte screenCode, byte colorNibble, byte pattern)
        {
            return new PixelResult
            {
                GraphicsForeground = false,
                UsesRawGraphics = true,
                RawPixelXInCell = (byte)(pixelXInCell & 0x07),
                RawScreenCode = screenCode,
                RawColorNibble = (byte)(colorNibble & 0x0F),
                RawPattern = pattern
            };
        }

        /// <summary>
        /// Stores pixel result state.
        /// </summary>
        private struct PixelResult
        {
            public uint Color;
            public bool GraphicsForeground;
            public bool UsesBackgroundRegister;
            public byte BackgroundRegisterIndex;
            public bool UsesRawGraphics;
            public byte RawPixelXInCell;
            public byte RawScreenCode;
            public byte RawColorNibble;
            public byte RawPattern;
        }
    }
}
