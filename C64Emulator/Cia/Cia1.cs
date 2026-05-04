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
using System.Collections.Generic;
using System.IO;
using OpenTK.Input;

namespace C64Emulator.Core
{
    /// <summary>
    /// Emulates a MOS 6526 CIA peripheral used by the C64.
    /// </summary>
    public sealed class Cia1
    {
        private const int TodCyclesPerTenth = 98525;
        private const byte JoystickUpMask = 0x01;
        private const byte JoystickDownMask = 0x02;
        private const byte JoystickLeftMask = 0x04;
        private const byte JoystickRightMask = 0x08;
        private const byte JoystickFireMask = 0x10;
        private readonly byte[] _registers = new byte[0x10];
        private readonly bool[,] _keyboardMatrix = new bool[8, 8];
        private readonly Dictionary<Key, MatrixKey> _keyMap = new Dictionary<Key, MatrixKey>();

        private ushort _timerALatch;
        private ushort _timerACounter;
        private ushort _timerBLatch;
        private ushort _timerBCounter;
        private byte _interruptMask;
        private byte _interruptFlags;
        private byte _joystickPort1State = 0x1F;
        private byte _joystickPort2State = 0x1F;
        private JoystickPort _activeJoystickPort = JoystickPort.Port2;
        private byte _serialDataRegister;
        private int _todCycleAccumulator;
        private byte _todTenths;
        private byte _todSeconds;
        private byte _todMinutes;
        private byte _todHours = 0x01;

        /// <summary>
        /// Initializes a new Cia1 instance.
        /// </summary>
        public Cia1()
        {
            BuildKeyMap();
            Reset();
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            System.Array.Clear(_registers, 0, _registers.Length);
            System.Array.Clear(_keyboardMatrix, 0, _keyboardMatrix.Length);
            _registers[0x00] = 0xFF;
            _registers[0x01] = 0xFF;
            _timerALatch = 0;
            _timerACounter = 0;
            _timerBLatch = 0;
            _timerBCounter = 0;
            _interruptMask = 0;
            _interruptFlags = 0;
            _joystickPort1State = 0x1F;
            _joystickPort2State = 0x1F;
            _serialDataRegister = 0;
            _todCycleAccumulator = 0;
            _todTenths = 0;
            _todSeconds = 0;
            _todMinutes = 0;
            _todHours = 0x01;
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            bool timerAUnderflow = TickTimerA();
            TickTimerB(timerAUnderflow);
            TickTod();
        }

        /// <summary>
        /// Handles the read operation.
        /// </summary>
        public byte Read(ushort address)
        {
            switch (address & 0x0F)
            {
                case 0x00:
                    return ReadPortA();
                case 0x01:
                    return ReadPortB();
                case 0x04:
                    return (byte)(_timerACounter & 0xFF);
                case 0x05:
                    return (byte)((_timerACounter >> 8) & 0xFF);
                case 0x06:
                    return (byte)(_timerBCounter & 0xFF);
                case 0x07:
                    return (byte)((_timerBCounter >> 8) & 0xFF);
                case 0x08:
                    return _todTenths;
                case 0x09:
                    return _todSeconds;
                case 0x0A:
                    return _todMinutes;
                case 0x0B:
                    return _todHours;
                case 0x0C:
                    return _serialDataRegister;
                case 0x0D:
                    byte value = (byte)(_interruptFlags | (IsIrqAsserted() ? 0x80 : 0x00));
                    _interruptFlags = 0;
                    return value;
                default:
                    return _registers[address & 0x0F];
            }
        }

        /// <summary>
        /// Handles the write operation.
        /// </summary>
        public void Write(ushort address, byte value)
        {
            address &= 0x0F;
            _registers[address] = value;

            switch (address)
            {
                case 0x04:
                    _timerALatch = (ushort)((_timerALatch & 0xFF00) | value);
                    break;
                case 0x05:
                    _timerALatch = (ushort)((_timerALatch & 0x00FF) | (value << 8));
                    if ((_registers[0x0E] & 0x01) == 0)
                    {
                        _timerACounter = _timerALatch;
                    }
                    break;
                case 0x06:
                    _timerBLatch = (ushort)((_timerBLatch & 0xFF00) | value);
                    break;
                case 0x07:
                    _timerBLatch = (ushort)((_timerBLatch & 0x00FF) | (value << 8));
                    if ((_registers[0x0F] & 0x01) == 0)
                    {
                        _timerBCounter = _timerBLatch;
                    }
                    break;
                case 0x08:
                    _todTenths = (byte)(value % 10);
                    break;
                case 0x09:
                    _todSeconds = NormalizeBcd(value, 59);
                    break;
                case 0x0A:
                    _todMinutes = NormalizeBcd(value, 59);
                    break;
                case 0x0B:
                    _todHours = NormalizeHour(value);
                    break;
                case 0x0C:
                    _serialDataRegister = value;
                    break;
                case 0x0D:
                    if ((value & 0x80) != 0)
                    {
                        _interruptMask |= (byte)(value & 0x1F);
                    }
                    else
                    {
                        _interruptMask &= (byte)~(value & 0x1F);
                    }
                    break;
                case 0x0E:
                    if ((value & 0x10) != 0)
                    {
                        _timerACounter = _timerALatch;
                        _registers[0x0E] &= 0xEF;
                    }
                    break;
                case 0x0F:
                    if ((value & 0x10) != 0)
                    {
                        _timerBCounter = _timerBLatch != 0 ? _timerBLatch : (ushort)0xFFFF;
                        _registers[0x0F] &= 0xEF;
                    }
                    break;
            }
        }

        /// <summary>
        /// Handles the key down operation.
        /// </summary>
        public void KeyDown(Key key)
        {
            if (SetJoystickState(key, true))
            {
                return;
            }

            SetKeyState(key, true);
        }

        /// <summary>
        /// Handles the key up operation.
        /// </summary>
        public void KeyUp(Key key)
        {
            if (SetJoystickState(key, false))
            {
                return;
            }

            SetKeyState(key, false);
        }

        /// <summary>
        /// Returns whether irq asserted is true.
        /// </summary>
        public bool IsIrqAsserted()
        {
            return (_interruptFlags & _interruptMask & 0x1F) != 0;
        }

        public JoystickPort ActiveJoystickPort
        {
            get { return _activeJoystickPort; }
            set
            {
                _activeJoystickPort =
                    value == JoystickPort.Port1 || value == JoystickPort.Both
                        ? value
                        : JoystickPort.Port2;
            }
        }

        /// <summary>
        /// Writes the complete CIA state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            StateSerializer.WriteObjectFields(writer, this, "_keyMap");
        }

        /// <summary>
        /// Restores the complete CIA state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            StateSerializer.ReadObjectFields(reader, this, "_keyMap");
        }

        /// <summary>
        /// Advances the timer a state by one emulated tick.
        /// </summary>
        private bool TickTimerA()
        {
            if ((_registers[0x0E] & 0x01) == 0)
            {
                return false;
            }

            if (_timerACounter == 0)
            {
                _timerACounter = _timerALatch != 0 ? _timerALatch : (ushort)0xFFFF;
            }

            _timerACounter--;
            if (_timerACounter != 0)
            {
                return false;
            }

            _interruptFlags |= 0x01;
            if ((_registers[0x0E] & 0x08) == 0)
            {
                _timerACounter = _timerALatch != 0 ? _timerALatch : (ushort)0xFFFF;
            }
            else
            {
                _registers[0x0E] &= 0xFE;
            }

            return true;
        }

        /// <summary>
        /// Advances the timer b state by one emulated tick.
        /// </summary>
        private void TickTimerB(bool timerAUnderflow)
        {
            if ((_registers[0x0F] & 0x01) == 0)
            {
                return;
            }

            int source = (_registers[0x0F] >> 5) & 0x03;
            bool shouldCount = source == 0 || ((source == 2 || source == 3) && timerAUnderflow);
            if (!shouldCount)
            {
                return;
            }

            if (_timerBCounter == 0)
            {
                _timerBCounter = _timerBLatch != 0 ? _timerBLatch : (ushort)0xFFFF;
            }

            _timerBCounter--;
            if (_timerBCounter != 0)
            {
                return;
            }

            _interruptFlags |= 0x02;
            if ((_registers[0x0F] & 0x08) == 0)
            {
                _timerBCounter = _timerBLatch != 0 ? _timerBLatch : (ushort)0xFFFF;
            }
            else
            {
                _registers[0x0F] &= 0xFE;
            }
        }

        /// <summary>
        /// Advances the tod state by one emulated tick.
        /// </summary>
        private void TickTod()
        {
            _todCycleAccumulator++;
            if (_todCycleAccumulator < TodCyclesPerTenth)
            {
                return;
            }

            _todCycleAccumulator -= TodCyclesPerTenth;
            IncrementTod();
        }

        /// <summary>
        /// Handles the increment tod operation.
        /// </summary>
        private void IncrementTod()
        {
            _todTenths++;
            if (_todTenths < 10)
            {
                return;
            }

            _todTenths = 0;
            _todSeconds = IncrementBcd(_todSeconds, 59, out bool carryToMinutes);
            if (!carryToMinutes)
            {
                return;
            }

            _todMinutes = IncrementBcd(_todMinutes, 59, out bool carryToHours);
            if (!carryToHours)
            {
                return;
            }

            _todHours = IncrementHour(_todHours);
        }

        /// <summary>
        /// Reads port a.
        /// </summary>
        private byte ReadPortA()
        {
            byte result = (byte)((_registers[0x00] & _registers[0x02]) | (~_registers[0x02] & 0xFF));
            byte portBOutputs = (byte)((_registers[0x01] & _registers[0x03]) | (~_registers[0x03] & 0xFF));

            for (int row = 0; row < 8; row++)
            {
                if (((portBOutputs >> row) & 0x01) != 0)
                {
                    continue;
                }

                for (int column = 0; column < 8; column++)
                {
                    if (_keyboardMatrix[row, column] && ((_registers[0x02] >> column) & 0x01) == 0)
                    {
                        result = (byte)(result & ~(1 << column));
                    }
                }
            }

            result = (byte)(result & (_joystickPort2State | 0xE0));
            return result;
        }

        /// <summary>
        /// Reads port b.
        /// </summary>
        private byte ReadPortB()
        {
            byte result = (byte)((_registers[0x01] & _registers[0x03]) | (~_registers[0x03] & 0xFF));
            byte portAOutputs = (byte)((_registers[0x00] & _registers[0x02]) | (~_registers[0x02] & 0xFF));

            for (int column = 0; column < 8; column++)
            {
                if (((portAOutputs >> column) & 0x01) != 0)
                {
                    continue;
                }

                for (int row = 0; row < 8; row++)
                {
                    if (_keyboardMatrix[row, column] && ((_registers[0x03] >> row) & 0x01) == 0)
                    {
                        result = (byte)(result & ~(1 << row));
                    }
                }
            }

            result = (byte)(result & (_joystickPort1State | 0xE0));
            return result;
        }

        /// <summary>
        /// Sets the key state value.
        /// </summary>
        private void SetKeyState(Key key, bool pressed)
        {
            MatrixKey matrixKey;
            if (_keyMap.TryGetValue(key, out matrixKey))
            {
                _keyboardMatrix[matrixKey.Row, matrixKey.Column] = pressed;
            }
        }

        /// <summary>
        /// Sets the joystick state value.
        /// </summary>
        private bool SetJoystickState(Key key, bool pressed)
        {
            byte mask;
            if (key == Key.Up)
            {
                mask = JoystickUpMask;
            }
            else if (key == Key.Down)
            {
                mask = JoystickDownMask;
            }
            else if (key == Key.Left)
            {
                mask = JoystickLeftMask;
            }
            else if (key == Key.Right)
            {
                mask = JoystickRightMask;
            }
            else if (key == Key.ControlLeft || key == Key.LControl)
            {
                mask = JoystickFireMask;
            }
            else
            {
                return false;
            }

            if (pressed)
            {
                if (_activeJoystickPort == JoystickPort.Port1 || _activeJoystickPort == JoystickPort.Both)
                {
                    _joystickPort1State = (byte)(_joystickPort1State & ~mask);
                }

                if (_activeJoystickPort == JoystickPort.Port2 || _activeJoystickPort == JoystickPort.Both)
                {
                    _joystickPort2State = (byte)(_joystickPort2State & ~mask);
                }
            }
            else
            {
                _joystickPort1State = (byte)(_joystickPort1State | mask);
                _joystickPort2State = (byte)(_joystickPort2State | mask);
            }

            return true;
        }

        /// <summary>
        /// Builds key map.
        /// </summary>
        private void BuildKeyMap()
        {
            Add(Key.BackSpace, 0, 0);
            Add(Key.Enter, 1, 0);
            Add(Key.Right, 2, 0);
            Add(Key.F7, 3, 0);
            Add(Key.F1, 4, 0);
            Add(Key.F3, 5, 0);
            Add(Key.F5, 6, 0);
            Add(Key.Down, 7, 0);

            Add(Key.Number3, 0, 1);
            Add(Key.W, 1, 1);
            Add(Key.A, 2, 1);
            Add(Key.Number4, 3, 1);
            Add(Key.Z, 4, 1);
            Add(Key.S, 5, 1);
            Add(Key.E, 6, 1);
            Add(Key.ShiftLeft, 7, 1);
            Add(Key.LShift, 7, 1);

            Add(Key.Number5, 0, 2);
            Add(Key.R, 1, 2);
            Add(Key.D, 2, 2);
            Add(Key.Number6, 3, 2);
            Add(Key.C, 4, 2);
            Add(Key.F, 5, 2);
            Add(Key.T, 6, 2);
            Add(Key.X, 7, 2);

            Add(Key.Number7, 0, 3);
            Add(Key.Y, 1, 3);
            Add(Key.G, 2, 3);
            Add(Key.Number8, 3, 3);
            Add(Key.B, 4, 3);
            Add(Key.H, 5, 3);
            Add(Key.U, 6, 3);
            Add(Key.V, 7, 3);

            Add(Key.Number9, 0, 4);
            Add(Key.I, 1, 4);
            Add(Key.J, 2, 4);
            Add(Key.Number0, 3, 4);
            Add(Key.M, 4, 4);
            Add(Key.K, 5, 4);
            Add(Key.O, 6, 4);
            Add(Key.N, 7, 4);

            Add(Key.Plus, 0, 5);
            Add(Key.P, 1, 5);
            Add(Key.L, 2, 5);
            Add(Key.Minus, 3, 5);
            Add(Key.Period, 4, 5);
            Add(Key.Semicolon, 5, 5);
            Add(Key.Quote, 6, 5);
            Add(Key.Comma, 7, 5);

            Add(Key.BackSlash, 0, 6);
            Add(Key.KeypadMultiply, 1, 6);
            Add(Key.BracketLeft, 2, 6);
            Add(Key.Home, 3, 6);
            Add(Key.ShiftRight, 4, 6);
            Add(Key.RShift, 4, 6);
            Add(Key.BracketRight, 5, 6);
            Add(Key.Up, 6, 6);
            Add(Key.Slash, 7, 6);

            Add(Key.Number1, 0, 7);
            Add(Key.Escape, 1, 7);
            Add(Key.ControlLeft, 2, 7);
            Add(Key.LControl, 2, 7);
            Add(Key.Number2, 3, 7);
            Add(Key.Space, 4, 7);
            Add(Key.AltLeft, 5, 7);
            Add(Key.LAlt, 5, 7);
            Add(Key.Q, 6, 7);
            Add(Key.Tab, 7, 7);
        }

        /// <summary>
        /// Handles the add operation.
        /// </summary>
        private void Add(Key key, int row, int column)
        {
            _keyMap[key] = new MatrixKey(row, column);
        }

        /// <summary>
        /// Handles the normalize bcd operation.
        /// </summary>
        private static byte NormalizeBcd(byte value, int maxDecimal)
        {
            int decimalValue = BcdToInt(value);
            if (decimalValue > maxDecimal)
            {
                decimalValue %= (maxDecimal + 1);
            }

            return IntToBcd(decimalValue);
        }

        /// <summary>
        /// Handles the normalize hour operation.
        /// </summary>
        private static byte NormalizeHour(byte value)
        {
            bool pm = (value & 0x80) != 0;
            int hour = BcdToInt((byte)(value & 0x1F));
            if (hour <= 0 || hour > 12)
            {
                hour = ((hour - 1 + 12) % 12) + 1;
            }

            return (byte)(IntToBcd(hour) | (pm ? 0x80 : 0x00));
        }

        /// <summary>
        /// Handles the increment bcd operation.
        /// </summary>
        private static byte IncrementBcd(byte value, int maxDecimal, out bool overflow)
        {
            int decimalValue = BcdToInt(value) + 1;
            if (decimalValue > maxDecimal)
            {
                decimalValue = 0;
                overflow = true;
            }
            else
            {
                overflow = false;
            }

            return IntToBcd(decimalValue);
        }

        /// <summary>
        /// Handles the increment hour operation.
        /// </summary>
        private static byte IncrementHour(byte value)
        {
            bool pm = (value & 0x80) != 0;
            int hour = BcdToInt((byte)(value & 0x1F));
            if (hour <= 0)
            {
                hour = 12;
            }

            if (hour == 11)
            {
                hour = 12;
                pm = !pm;
            }
            else if (hour == 12)
            {
                hour = 1;
            }
            else
            {
                hour++;
            }

            return (byte)(IntToBcd(hour) | (pm ? 0x80 : 0x00));
        }

        /// <summary>
        /// Handles the bcd to int operation.
        /// </summary>
        private static int BcdToInt(byte value)
        {
            return ((value >> 4) * 10) + (value & 0x0F);
        }

        /// <summary>
        /// Handles the int to bcd operation.
        /// </summary>
        private static byte IntToBcd(int value)
        {
            return (byte)(((value / 10) << 4) | (value % 10));
        }

        /// <summary>
        /// Stores matrix key state.
        /// </summary>
        private struct MatrixKey
        {
            /// <summary>
            /// Gets the keyboard matrix row.
            /// </summary>
            public int Row { get; }
            /// <summary>
            /// Gets the keyboard matrix column.
            /// </summary>
            public int Column { get; }

            /// <summary>
            /// Initializes a new MatrixKey instance.
            /// </summary>
            public MatrixKey(int row, int column)
            {
                Row = row;
                Column = column;
            }
        }
    }
}
