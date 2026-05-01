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
    /// Emulates a MOS 6526 CIA peripheral used by the C64.
    /// </summary>
    public sealed class Cia2
    {
        private const int TodCyclesPerTenth = 98525;
        private const byte PortABitAtnOut = 0x08;
        private const byte PortABitClockOut = 0x10;
        private const byte PortABitDataOut = 0x20;
        private const byte PortABitClockIn = 0x40;
        private const byte PortABitDataIn = 0x80;

        private readonly byte[] _registers = new byte[0x10];

        private IecBusPort _iecBusPort;
        private ushort _timerALatch;
        private ushort _timerACounter;
        private ushort _timerBLatch;
        private ushort _timerBCounter;
        private byte _interruptMask;
        private byte _interruptFlags;
        private byte _serialDataRegister;
        private int _todCycleAccumulator;
        private byte _todTenths;
        private byte _todSeconds;
        private byte _todMinutes;
        private byte _todHours = 0x01;

        /// <summary>
        /// Gets or sets the callback invoked before CIA IEC port access.
        /// </summary>
        public System.Action BeforeIecPortAccess { get; set; }

        /// <summary>
        /// Initializes a new Cia2 instance.
        /// </summary>
        public Cia2()
        {
            Reset();
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            System.Array.Clear(_registers, 0, _registers.Length);
            _registers[0x00] = 0xFF;
            _registers[0x01] = 0xFF;
            _timerALatch = 0;
            _timerACounter = 0;
            _timerBLatch = 0;
            _timerBCounter = 0;
            _interruptMask = 0;
            _interruptFlags = 0;
            _serialDataRegister = 0;
            _todCycleAccumulator = 0;
            _todTenths = 0;
            _todSeconds = 0;
            _todMinutes = 0;
            _todHours = 0x01;
            UpdateIecOutputs(false);
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
                    return ReadPort(_registers[0x01], _registers[0x03], 0xFF);
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
                    byte value = (byte)(_interruptFlags | (IsNmiAsserted() ? 0x80 : 0x00));
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
                case 0x00:
                case 0x02:
                    UpdateIecOutputs(true);
                    break;
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
                        _timerACounter = _timerALatch != 0 ? _timerALatch : (ushort)0xFFFF;
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
        /// Returns whether nmi asserted is true.
        /// </summary>
        public bool IsNmiAsserted()
        {
            return (_interruptFlags & _interruptMask & 0x1F) != 0;
        }

        /// <summary>
        /// Handles the attach iec bus operation.
        /// </summary>
        public void AttachIecBus(IecBusPort iecBusPort)
        {
            _iecBusPort = iecBusPort;
            UpdateIecOutputs(false);
        }

        public byte VicBankSelect
        {
            get { return (byte)(ReadLocalPortA() & 0x03); }
        }

        /// <summary>
        /// Gets the debug info value.
        /// </summary>
        public string GetDebugInfo()
        {
            byte portA = _registers[0x00];
            byte ddra = _registers[0x02];
            return string.Format(
                "pra={0:X2} ddra={1:X2} readA={2:X2} outA={3} outC={4} outD={5}",
                portA,
                ddra,
                ReadPortA(false),
                (ddra & PortABitAtnOut) != 0 && (portA & PortABitAtnOut) != 0,
                (ddra & PortABitClockOut) != 0 && (portA & PortABitClockOut) != 0,
                (ddra & PortABitDataOut) != 0 && (portA & PortABitDataOut) != 0);
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
        /// Reads port.
        /// </summary>
        private static byte ReadPort(byte registerValue, byte dataDirection, byte floatingInputs)
        {
            return (byte)((registerValue & dataDirection) | (floatingInputs & ~dataDirection));
        }

        /// <summary>
        /// Reads local port a.
        /// </summary>
        private byte ReadLocalPortA()
        {
            return ReadPort(_registers[0x00], _registers[0x02], 0xFF);
        }

        /// <summary>
        /// Reads port a.
        /// </summary>
        private byte ReadPortA()
        {
            return ReadPortA(true);
        }

        /// <summary>
        /// Reads port a.
        /// </summary>
        private byte ReadPortA(bool catchUpIec)
        {
            if (catchUpIec && _iecBusPort != null)
            {
                BeforeIecPortAccess?.Invoke();
            }

            byte value = ReadLocalPortA();
            byte dataDirection = _registers[0x02];

            if (_iecBusPort == null)
            {
                return value;
            }

            if ((dataDirection & PortABitClockIn) == 0)
            {
                value = _iecBusPort.IsLineLow(IecBusLine.Clock)
                    ? (byte)(value & ~PortABitClockIn)
                    : (byte)(value | PortABitClockIn);
            }

            if ((dataDirection & PortABitDataIn) == 0)
            {
                value = _iecBusPort.IsLineLow(IecBusLine.Data)
                    ? (byte)(value & ~PortABitDataIn)
                    : (byte)(value | PortABitDataIn);
            }

            return value;
        }

        /// <summary>
        /// Updates iec outputs.
        /// </summary>
        private void UpdateIecOutputs(bool catchUpIec)
        {
            if (_iecBusPort == null)
            {
                return;
            }

            if (catchUpIec)
            {
                BeforeIecPortAccess?.Invoke();
            }

            byte portA = _registers[0x00];
            byte dataDirection = _registers[0x02];

            // The C64 serial outputs PA3/PA4/PA5 are inverted by U8 before
            // reaching the IEC bus. On the software-visible CIA side this
            // means the KERNAL treats 0 as released/high and 1 as asserted/
            // low on the actual bus lines.
            _iecBusPort.SetLines(
                atnLow: (dataDirection & PortABitAtnOut) != 0 && (portA & PortABitAtnOut) != 0,
                clockLow: (dataDirection & PortABitClockOut) != 0 && (portA & PortABitClockOut) != 0,
                dataLow: (dataDirection & PortABitDataOut) != 0 && (portA & PortABitDataOut) != 0);
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
    }
}
