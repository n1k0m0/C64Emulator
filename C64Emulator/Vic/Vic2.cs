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
        private const int GraphicsOutputDelayPixels = 12;
        private const int InnerDisplayWidth = VisibleColumns * CharacterWidth;
        private const int InnerDisplayHeight = VisibleRows * CharacterHeight;
        private const int CropLeft = (TotalRasterWidth - 403) / 2;
        private const int CropTop = (TotalLines - 284) / 2;
        private const int BorderLeft = 41;
        private const int BorderTop = 37;
        private const int NarrowBorderLeft = BorderLeft + 7;
        private const int NarrowBorderTop = BorderTop + 4;
        private const int StandardSpriteDisplayLeft = 24;
        private const int StandardSpriteDisplayTop = 51;
        private const int NarrowSpriteDisplayLeft = 31;
        private const int NarrowSpriteDisplayTop = 55;
        private const int SpriteSameLineDisplayX = 0x164;
        private const int SpriteVisibleOffsetX = StandardSpriteDisplayLeft - BorderLeft;
        private const int SpriteVisibleOffsetY = StandardSpriteDisplayTop - BorderTop;
        private const int SpriteRasterStartOffsetY = 1;

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

        private readonly SystemBus _bus;
        private readonly FrameBuffer _frameBuffer;
        private readonly C64Model _model;
        private readonly byte[] _registers = new byte[0x40];
        private readonly VicBusPlan _busPlan = new VicBusPlan();
        private readonly bool[] _spriteDmaActive = new bool[8];
        private readonly bool[] _spriteLineVisible = new bool[8];
        private readonly bool[] _spriteExpandFlipFlop = new bool[8];
        private readonly int[] _spriteCurrentRow = new int[8];
        private readonly int[] _spriteFetchRow = new int[8];
        private readonly int[] _spriteFetchPhase = new int[8];
        private readonly int[] _spriteDisplayRow = new int[8];
        private readonly bool[] _spriteDataValid = new bool[8];
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
        private readonly PixelResult[] _graphicsOutputDelay = new PixelResult[GraphicsOutputDelayPixels];

        private int _rasterLine;
        private int _cycleInLine;
        private long _globalCycle;
        private ushort _rasterIrqLine;
        private byte _irqFlags;
        private byte _spriteSpriteCollision;
        private byte _spriteDataCollision;
        private byte _lightPenX;
        private byte _lightPenY;
        private bool _cpuBusBlockedThisCycle;
        private bool _isBadLine;
        private bool _rasterIrqTriggeredThisLine;
        private bool _videoMatrixValid;
        private int _videoMatrixCellY;
        private bool _videoMatrixBitmapMode;
        private bool _videoPatternValid;
        private int _videoPatternCellY;
        private int _videoPatternPixelRow;
        private bool _videoPatternBitmapMode;
        private bool _graphicsDisplayState;
        private int _graphicsVc;
        private int _graphicsVcBase;
        private int _graphicsVmli;
        private int _graphicsRc;
        private int _graphicsLineMatrixBaseIndex;
        private int _graphicsLineCellY;
        private int _graphicsLinePixelRow;
        private bool _matrixFetchStartedThisLine;
        private int _matrixFetchRequestStartCycle;
        private int _matrixFetchStartCycle;
        private int _matrixFetchCpuBlockStartCycle;
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
        private VicBusSlot _currentBusSlot;
        private bool _displayEnableFrameLatched;
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
        private bool _verticalBorderActive = true;
        private bool _horizontalBorderActive = true;
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
        private bool _graphicsSequencerBitmapMode;
        private bool _graphicsSequencerExtendedColorMode;
        private bool _graphicsSequencerMulticolorMode;

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
            ApplyGraphicsBusOverrides();
            _cpuBusBlockedThisCycle = _currentBusSlot.BlocksCpu;
            ExecuteFetchAction(_currentBusSlot.Phi1Action);
        }

        /// <summary>
        /// Handles the finish cycle operation.
        /// </summary>
        public void FinishCycle()
        {
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
                    _rasterLine = 0;
                }
            }
        }

        /// <summary>
        /// Ends raster line.
        /// </summary>
        private void EndRasterLine()
        {
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
                case 0x19:
                    return (byte)(_irqFlags | (IsIrqAsserted() ? 0x80 : 0x00));
                case 0x1E:
                    return ReadAndClearSpriteSpriteCollision();
                case 0x1F:
                    return ReadAndClearSpriteDataCollision();
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
            switch (address)
            {
                case 0x11:
                    _registers[0x11] = value;
                    _rasterIrqLine = (ushort)((_rasterIrqLine & 0x00FF) | ((value & 0x80) << 1));
                    TriggerRasterIrqIfMatched();
                    break;
                case 0x12:
                    _registers[0x12] = value;
                    _rasterIrqLine = (ushort)((_rasterIrqLine & 0x0100) | value);
                    TriggerRasterIrqIfMatched();
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
            _lightPenX = 0;
            _lightPenY = 0;
            _cpuBusBlockedThisCycle = false;
            _isBadLine = false;
            _rasterIrqTriggeredThisLine = false;
            _videoMatrixValid = false;
            _videoMatrixCellY = -1;
            _videoMatrixBitmapMode = false;
            _videoPatternValid = false;
            _videoPatternCellY = -1;
            _videoPatternPixelRow = -1;
            _videoPatternBitmapMode = false;
            _graphicsDisplayState = false;
            _graphicsVc = 0;
            _graphicsVcBase = 0;
            _graphicsVmli = 0;
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
            System.Array.Clear(_spriteLineVisible, 0, _spriteLineVisible.Length);
            System.Array.Clear(_spriteExpandFlipFlop, 0, _spriteExpandFlipFlop.Length);
            System.Array.Clear(_spriteCurrentRow, 0, _spriteCurrentRow.Length);
            System.Array.Clear(_spriteFetchRow, 0, _spriteFetchRow.Length);
            System.Array.Clear(_spriteFetchPhase, 0, _spriteFetchPhase.Length);
            System.Array.Clear(_spriteDisplayRow, 0, _spriteDisplayRow.Length);
            System.Array.Clear(_spriteDataValid, 0, _spriteDataValid.Length);
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
            _currentBusSlot = default(VicBusSlot);
            _displayEnableFrameLatched = false;
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
            _verticalBorderActive = true;
            _horizontalBorderActive = true;
            LatchDisplaySourceFromLineState();
            _frameBuffer.Clear(Palette[_registers[0x20] & 0x0F]);
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
                int beamX = beamXStart + dot;
                int frameX = beamX - CropLeft;
                int frameY = beamY - CropTop;
                PixelResult currentPixel = ComposePixel(frameX, frameY);
                PixelResult outputPixel = DelayGraphicsPixel(currentPixel);

                if ((uint)frameX < (uint)_model.VisibleWidth && (uint)frameY < (uint)_model.VisibleHeight)
                {
                    _frameBuffer.SetPixel(frameX, frameY, outputPixel.Color);
                }
            }
        }

        /// <summary>
        /// Composes pixel.
        /// </summary>
        private PixelResult ComposePixel(int frameX, int frameY)
        {
            uint borderColor = Palette[_registers[0x20] & 0x0F];
            PixelResult result = new PixelResult
            {
                Color = borderColor,
                GraphicsForeground = false
            };

            UpdateHorizontalBorderState(frameX, frameY);

            int activeDisplayLeft = _lineDisplayLeftFrame;
            if (_lineDisplayEnabled &&
                !_horizontalBorderActive &&
                !_verticalBorderActive &&
                frameY >= _lineDisplayTopFrame &&
                frameY < _lineDisplayBottomFrame)
            {
                result = ComputeGraphicsPixel(frameX - activeDisplayLeft, frameY - _lineDisplayTopFrame);
            }

            ApplySprites(ref result, frameX, frameY);
            return result;
        }

        /// <summary>
        /// Computes graphics pixel.
        /// </summary>
        private PixelResult ComputeGraphicsPixel(int displayX, int displayY)
        {
            int scrolledX = displayX + GetHorizontalScrollPhase();
            if (scrolledX < 0 || scrolledX >= InnerDisplayWidth)
            {
                _graphicsSequencerCellLoaded = false;
                return CreateBackgroundPixel(GetBackgroundColor(0));
            }

            int cellX = scrolledX / CharacterWidth;
            int pixelXInCell = scrolledX & 0x07;
            if (!_graphicsSequencerCellLoaded || _graphicsSequencerCellX != cellX)
            {
                if (!LoadGraphicsSequencerCell(cellX, displayY))
                {
                    _graphicsSequencerCellLoaded = false;
                    return CreateBackgroundPixel(GetBackgroundColor(0));
                }
            }

            if (_graphicsSequencerBitmapMode)
            {
                return ComputeBitmapPixel(
                    pixelXInCell,
                    _graphicsSequencerScreenCode,
                    _graphicsSequencerColorNibble,
                    _graphicsSequencerPattern,
                    _graphicsSequencerMulticolorMode && !_graphicsSequencerExtendedColorMode);
            }

            return ComputeCharacterPixel(
                pixelXInCell,
                _graphicsSequencerScreenCode,
                _graphicsSequencerColorNibble,
                _graphicsSequencerPattern,
                _graphicsSequencerExtendedColorMode,
                _graphicsSequencerMulticolorMode);
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
                        return CreateBackgroundPixel(GetBackgroundColor(0));
                    case 1:
                        return CreateBackgroundPixel(GetBackgroundColor(1));
                    case 2:
                        return CreateForegroundPixel(GetBackgroundColor(2));
                    default:
                        return CreateForegroundPixel(Palette[colorNibble & 0x07]);
                }
            }

            bool set = (pattern & (0x80 >> pixelXInCell)) != 0;
            if (!set)
            {
                if (extendedColorMode)
                {
                    return CreateBackgroundPixel(GetBackgroundColor((screenCode >> 6) & 0x03));
                }

                return CreateBackgroundPixel(GetBackgroundColor(0));
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
                        return CreateBackgroundPixel(GetBackgroundColor(0));
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
        /// Loads graphics sequencer cell.
        /// </summary>
        private bool LoadGraphicsSequencerCell(int cellX, int displayY)
        {
            bool hasLatchedPattern = _videoPatternValid &&
                _videoPatternPixelRow >= 0 &&
                _videoPatternFetched[cellX];
            bool hasLatchedMatrix = _videoMatrixValid &&
                _videoMatrixFetched[cellX];
            bool currentBitmapMode = hasLatchedPattern
                ? _videoPatternBitmapModes[cellX]
                : (hasLatchedMatrix ? _videoMatrixBitmapModes[cellX] : _displaySourceBitmapMode);

            if (currentBitmapMode)
            {
                int scrolledY = displayY + GetVerticalScrollPhase();
                if (scrolledY < 0 || scrolledY >= InnerDisplayHeight)
                {
                    return false;
                }

                int cellY = _graphicsLineCellY >= 0 ? _graphicsLineCellY : (scrolledY / CharacterHeight);
                int pixelYInCell = _graphicsLinePixelRow >= 0 ? _graphicsLinePixelRow : (scrolledY & 0x07);
                int lineMatrixBaseIndex = GetGraphicsLineMatrixBaseIndex(cellY);
                int matrixIndex = NormalizeVideoMatrixIndex(lineMatrixBaseIndex + cellX);

                _graphicsSequencerScreenCode = (CanUseLatchedMatrixCell(cellX, true))
                    ? _videoMatrixScreenCodes[cellX]
                    : ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));
                _graphicsSequencerColorNibble = (CanUseLatchedMatrixCell(cellX, true))
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
                    : (CanUseLatchedMatrixCell(cellX, true) ? _videoMatrixExtendedColorModes[cellX] : _displaySourceExtendedColorMode);
                _graphicsSequencerMulticolorMode = useLatchedBitmapPattern
                    ? _videoPatternMulticolorModes[cellX]
                    : (CanUseLatchedMatrixCell(cellX, true) ? _videoMatrixMulticolorModes[cellX] : _displaySourceMulticolorMode);
            }
            else
            {
                int scrolledY = displayY + GetVerticalScrollPhase();
                int textCellY = _graphicsLineCellY >= 0 ? _graphicsLineCellY : (scrolledY / CharacterHeight);
                int textPixelYInCell = _graphicsLinePixelRow >= 0 ? _graphicsLinePixelRow : (scrolledY & 0x07);
                if (textCellY < 0 || textCellY >= VisibleRows)
                {
                    return false;
                }

                int textLineMatrixBaseIndex = GetGraphicsLineMatrixBaseIndex(textCellY);
                int textMatrixIndex = NormalizeVideoMatrixIndex(textLineMatrixBaseIndex + cellX);

                if (CanUseLatchedMatrixCell(cellX, false))
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
                    : (CanUseLatchedMatrixCell(cellX, false) ? _videoMatrixExtendedColorModes[cellX] : _displaySourceExtendedColorMode);
                _graphicsSequencerMulticolorMode = useLatchedCharacterPattern
                    ? _videoPatternMulticolorModes[cellX]
                    : (CanUseLatchedMatrixCell(cellX, false) ? _videoMatrixMulticolorModes[cellX] : _displaySourceMulticolorMode);
            }

            _graphicsSequencerCellLoaded = true;
            _graphicsSequencerCellX = cellX;
            return true;
        }

        /// <summary>
        /// Applies sprites.
        /// </summary>
        private void ApplySprites(ref PixelResult result, int frameX, int frameY)
        {
            if (!IsInsideActiveDisplayArea(frameX, frameY))
            {
                return;
            }

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

                if (result.GraphicsForeground)
                {
                    _spriteDataCollision |= bit;
                }

                bool behindGraphics = ((_registers[0x1B] >> spriteIndex) & 0x01) != 0;
                if (!spriteVisible && (!behindGraphics || !result.GraphicsForeground))
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
            if (((_registers[0x15] >> spriteIndex) & 0x01) == 0)
            {
                return false;
            }

            if (!_spriteLineVisible[spriteIndex])
            {
                return false;
            }

            int spriteX = _registers[spriteIndex * 2];
            int spriteY = _registers[(spriteIndex * 2) + 1];
            if (((_registers[0x10] >> spriteIndex) & 0x01) != 0)
            {
                spriteX += 256;
            }

            int localX = frameX - GetSpriteFrameLeft(spriteX);
            if (localX < 0)
            {
                return false;
            }

            bool xExpanded = ((_registers[0x1D] >> spriteIndex) & 0x01) != 0;
            bool yExpanded = ((_registers[0x17] >> spriteIndex) & 0x01) != 0;
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

            if (localX >= 24 || localY >= 21)
            {
                return false;
            }

            if (!_spriteDataValid[spriteIndex] || localY != _spriteDisplayRow[spriteIndex])
            {
                return false;
            }

            byte data0 = _spriteDataByte0[spriteIndex];
            byte data1 = _spriteDataByte1[spriteIndex];
            byte data2 = _spriteDataByte2[spriteIndex];
            uint spriteBits = (uint)((data0 << 16) | (data1 << 8) | data2);

            if (((_registers[0x1C] >> spriteIndex) & 0x01) != 0)
            {
                int pair = (int)((spriteBits >> ((11 - (localX / 2)) * 2)) & 0x03);
                switch (pair)
                {
                    case 0:
                        return false;
                    case 1:
                        spriteColor = Palette[_registers[0x25] & 0x0F];
                        return true;
                    case 2:
                        spriteColor = Palette[_registers[0x27 + spriteIndex] & 0x0F];
                        return true;
                    default:
                        spriteColor = Palette[_registers[0x26] & 0x0F];
                        return true;
                }
            }

            if (((spriteBits >> (23 - localX)) & 0x01) == 0)
            {
                return false;
            }

            spriteColor = Palette[_registers[0x27 + spriteIndex] & 0x0F];
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
            if (((_registers[0x15] >> spriteIndex) & 0x01) == 0)
            {
                return false;
            }

            return _spriteDmaActive[spriteIndex] || _spriteLineVisible[spriteIndex];
        }

        /// <summary>
        /// Updates raster irq.
        /// </summary>
        private void UpdateRasterIrq()
        {
            if (!_rasterIrqTriggeredThisLine && _rasterLine == _rasterIrqLine)
            {
                _irqFlags |= 0x01;
                _registers[0x19] = _irqFlags;
                _rasterIrqTriggeredThisLine = true;
            }
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
        /// Gets the background color value.
        /// </summary>
        private uint GetBackgroundColor(int index)
        {
            return Palette[_registers[0x21 + (index & 0x03)] & 0x0F];
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
            return frameX >= _lineDisplayLeftFrame &&
                frameX < _lineDisplayRightFrame &&
                frameY >= _lineDisplayTopFrame &&
                frameY < _lineDisplayBottomFrame;
        }

        /// <summary>
        /// Begins raster line.
        /// </summary>
        private void BeginRasterLine()
        {
            if (_rasterLine == 0)
            {
                _verticalBorderActive = true;
                _displayEnableFrameLatched = false;
                _graphicsDisplayState = false;
                _graphicsVc = 0;
                _graphicsVcBase = 0;
                _graphicsVmli = 0;
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

            _rasterIrqTriggeredThisLine = false;
            LatchCurrentLineState();
            LatchDisplaySourceFromLineState();
            UpdateVerticalBorderState();
            _isBadLine = false;
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
            UpdateSpriteLineState();
            _busPlan.BuildLine(false, _spriteDmaActive);
            _currentBusSlot = _busPlan.GetSlot(0);
            ResetGraphicsOutputDelayLine();
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
        /// Handles the latch current line state operation.
        /// </summary>
        private void LatchCurrentLineState()
        {
            _lineDisplayEnabled = _displayEnableFrameLatched;
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
            _horizontalBorderActive = true;
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
                action == VicBusAction.CharFetch ||
                action == VicBusAction.SpritePointerFetch;
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
                    if (_matrixFetchRequestStartCycle == _matrixFetchStartCycle)
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
            if ((uint)cellX >= VisibleColumns)
            {
                return;
            }

            int matrixIndex = NormalizeVideoMatrixIndex(_graphicsVc);
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
        private bool CanUseLatchedMatrixCell(int cellX, bool bitmapMode)
        {
            if ((uint)cellX >= VisibleColumns)
            {
                return false;
            }

            if (!_videoMatrixValid || !_videoMatrixFetched[cellX] || _videoMatrixBitmapModes[cellX] != bitmapMode)
            {
                return false;
            }

            if (_isBadLine && (_cycleInLine + 1) < _matrixFetchStartCycle)
            {
                return false;
            }

            return true;
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
                    _spriteDisplayRow[spriteIndex] = _spriteFetchRow[spriteIndex];
                    _spriteDataValid[spriteIndex] = true;
                    break;
            }

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
                int cellX = _graphicsVmli;
                if ((uint)cellX >= VisibleColumns)
                {
                    return;
                }

                int matrixIndex = NormalizeVideoMatrixIndex(_graphicsVc);
                if (_displaySourceBitmapMode)
                {
                    _videoPatternBytes[cellX] = ReadDisplaySourceBitmapPattern(matrixIndex, pixelRow);
                }
                else
                {
                    byte screenCode = (_videoMatrixValid &&
                        !_videoMatrixBitmapModes[cellX] &&
                        _videoMatrixFetched[cellX])
                        ? _videoMatrixScreenCodes[cellX]
                        : ReadVicAbsolute((ushort)(_displaySourceScreenBaseAbsolute + matrixIndex));

                    _videoPatternBytes[cellX] = ReadDisplaySourceCharacterPattern(pixelRow, screenCode);
                }

                _videoPatternFetched[cellX] = true;
                _videoPatternBitmapModes[cellX] = _displaySourceBitmapMode;
                _videoPatternExtendedColorModes[cellX] = _displaySourceExtendedColorMode;
                _videoPatternMulticolorModes[cellX] = _displaySourceMulticolorMode;
                _videoPatternBitmapMode = _displaySourceBitmapMode;
                _graphicsVc = NormalizeVideoMatrixIndex(_graphicsVc + 1);
                _graphicsVmli = (_graphicsVmli + 1) & 0x3F;
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
            _videoPatternBitmapMode = _displaySourceBitmapMode;
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

            ushort pointerAddress = (ushort)(_displaySourceScreenBaseAbsolute + 0x03F8 + spriteIndex);
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

            int row = _spriteFetchRow[spriteIndex];
            if ((uint)row >= 21)
            {
                return;
            }

            ushort spriteAddress = (ushort)(GetVicBankBase() + (_spritePointers[spriteIndex] * 64) + (row * 3) + fetchPhase);
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
                    _spriteDisplayRow[spriteIndex] = row;
                    _spriteDataValid[spriteIndex] = true;
                    break;
            }

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
        /// Updates graphics state for current cycle.
        /// </summary>
        private void UpdateGraphicsStateForCurrentCycle()
        {
            int cycle = _cycleInLine + 1;

            if (_rasterLine == DenLatchRasterLine && (_registers[0x11] & 0x10) != 0)
            {
                _displayEnableFrameLatched = true;
                _lineDisplayEnabled = true;
            }

            bool badLineCondition = GetBadLineConditionForCurrentCycle();
            _isBadLine = badLineCondition;

            if (cycle == 14)
            {
                _graphicsVc = NormalizeVideoMatrixIndex(_graphicsVcBase);
                _graphicsVmli = 0;
                if (badLineCondition)
                {
                    _graphicsRc = 0;
                    _graphicsDisplayState = true;
                    _matrixFetchStartedThisLine = true;
                    _matrixFetchRequestStartCycle = 12;
                    _matrixFetchStartCycle = 15;
                    _matrixFetchCpuBlockStartCycle = 15;
                }

                UpdateGraphicsLineAddressState();
                if (_matrixFetchStartedThisLine)
                {
                    BeginVideoMatrixFetchSequence();
                }
            }
            else if (!_matrixFetchStartedThisLine && badLineCondition && cycle >= 15 && cycle <= 53)
            {
                _graphicsDisplayState = true;
                _matrixFetchStartedThisLine = true;
                _matrixFetchRequestStartCycle = cycle + 1;
                _matrixFetchStartCycle = _matrixFetchRequestStartCycle;
                _matrixFetchCpuBlockStartCycle = System.Math.Min(54, _matrixFetchRequestStartCycle + 3);
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
        }

        /// <summary>
        /// Applies graphics bus overrides.
        /// </summary>
        private void ApplyGraphicsBusOverrides()
        {
            int cycle = _cycleInLine + 1;
            if (!_matrixFetchStartedThisLine)
            {
                if (_isBadLine && cycle >= 12 && cycle < 15)
                {
                    _currentBusSlot.BusRequestPending = true;
                }

                return;
            }

            if (cycle < _matrixFetchRequestStartCycle || cycle > 54)
            {
                return;
            }

            if (cycle >= _matrixFetchStartCycle)
            {
                _currentBusSlot.Phi2Action = VicBusAction.MatrixFetch;
            }

            _currentBusSlot.BusRequestPending = cycle >= _matrixFetchRequestStartCycle && cycle < _matrixFetchCpuBlockStartCycle;
            if (cycle >= _matrixFetchCpuBlockStartCycle)
            {
                _currentBusSlot.BlocksCpu = true;
            }
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
            int top = _lineDisplayTopFrame + CropTop;
            int bottom = _lineDisplayBottomFrame + CropTop;

            if (_rasterLine == 0)
            {
                _verticalBorderActive = true;
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
            if (frameY < _lineDisplayTopFrame || frameY >= _lineDisplayBottomFrame)
            {
                _horizontalBorderActive = true;
                return;
            }

            int activeDisplayLeft = _lineDisplayLeftFrame;
            int activeDisplayRight = _lineDisplayRightFrame;
            if (frameX == activeDisplayLeft)
            {
                if (frameY == _lineDisplayBottomFrame)
                {
                    _verticalBorderActive = true;
                }
                else if (frameY == _lineDisplayTopFrame && _lineDisplayEnabled)
                {
                    _verticalBorderActive = false;
                }

                if (_lineDisplayEnabled && !_verticalBorderActive)
                {
                    _horizontalBorderActive = false;
                }
            }

            if (frameX == activeDisplayRight)
            {
                _horizontalBorderActive = true;
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
                if (!enabled)
                {
                    _spriteLineVisible[spriteIndex] = false;
                    _spriteDmaActive[spriteIndex] = false;
                    _spriteDataValid[spriteIndex] = false;
                    _spriteFetchPhase[spriteIndex] = 0;
                    continue;
                }

                int spriteX = _registers[spriteIndex * 2];
                if (((_registers[0x10] >> spriteIndex) & 0x01) != 0)
                {
                    spriteX += 256;
                }

                int spriteY = _registers[(spriteIndex * 2) + 1];
                int spriteStart = GetSpriteVisibleStartRasterLine(spriteY, spriteX);
                bool yExpanded = ((_registers[0x17] >> spriteIndex) & 0x01) != 0;
                int visibleLines = yExpanded ? 42 : 21;
                int visibleDelta = _rasterLine - spriteStart;
                _spriteFetchPhase[spriteIndex] = 0;

                if (enabled && visibleDelta >= 0 && visibleDelta < visibleLines)
                {
                    _spriteLineVisible[spriteIndex] = true;
                    if (yExpanded)
                    {
                        if (visibleDelta > 0)
                        {
                            _spriteExpandFlipFlop[spriteIndex] = !_spriteExpandFlipFlop[spriteIndex];
                        }

                        _spriteCurrentRow[spriteIndex] = visibleDelta / 2;
                    }
                    else
                    {
                        _spriteCurrentRow[spriteIndex] = visibleDelta;
                    }
                }
                else
                {
                    _spriteLineVisible[spriteIndex] = false;
                }

                int fetchDelta = spriteIndex <= 2 ? (_rasterLine - spriteY) : visibleDelta;
                _spriteDmaActive[spriteIndex] = enabled && fetchDelta >= 0 && fetchDelta < visibleLines;
                _spriteFetchRow[spriteIndex] = ComputeSpriteFetchRow(fetchDelta, yExpanded);

                if (_rasterLine == spriteY)
                {
                    _spriteExpandFlipFlop[spriteIndex] = !yExpanded;
                    _spriteDataValid[spriteIndex] = false;
                }
            }
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
            if (!_rasterIrqTriggeredThisLine && _rasterLine == _rasterIrqLine)
            {
                _irqFlags |= 0x01;
                _registers[0x19] = _irqFlags;
                _rasterIrqTriggeredThisLine = true;
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
        /// Stores pixel result state.
        /// </summary>
        private struct PixelResult
        {
            public uint Color;
            public bool GraphicsForeground;
        }
    }
}
