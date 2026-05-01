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
using System;

namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the drive via6522 component.
    /// </summary>
    public sealed class DriveVia6522
    {
        private const byte IrqCa2 = 0x01;
        private const byte IrqCa1 = 0x02;
        private const byte IrqShiftRegister = 0x04;
        private const byte IrqCb2 = 0x08;
        private const byte IrqCb1 = 0x10;
        private const byte IrqTimer2 = 0x20;
        private const byte IrqTimer1 = 0x40;

        private const byte AcrPortALatchEnable = 0x01;
        private const byte AcrPortBLatchEnable = 0x02;
        private const byte AcrShiftRegisterControl = 0x1C;
        private const byte AcrTimer2Pb6Mode = 0x20;
        private const byte AcrTimer1FreeRun = 0x40;
        private const byte AcrTimer1Pb7Output = 0x80;

        private byte _orb;
        private byte _ora;
        private byte _ddrb;
        private byte _ddra;
        private byte _pcr;
        private byte _acr;
        private byte _ifr;
        private byte _ier;
        private byte _shiftRegister;

        private byte _latchedValueA;
        private byte _latchedValueB;
        private bool _latchPortA;
        private bool _latchPortB;

        private bool _ca1;
        private bool _ca2;
        private bool _pulseCa2;
        private bool _cb1;
        private bool _cb1Old;
        private bool _cb2;
        private bool _pulseCb2;

        private ushort _t1Counter;
        private ushort _t1Latch;
        private bool _t1Ticking;
        private bool _t1Reload;
        private bool _t1OutPb7Enabled;
        private bool _t1FreeRun;
        private bool _t1FreeRunIrqsOn;
        private bool _t1TimedOut;
        private bool _t1Pb7High;
        private bool _t1OneShotTriggeredIrq;

        private ushort _t2Counter;
        private byte _t2Latch;
        private bool _t2Reload;
        private bool _t2CountingDown;
        private bool _t2CountingPb6ModeOld;
        private bool _t2CountingPb6Mode;
        private bool _t2TimedOut;
        private bool _t2LowTimedOut;
        private bool _t2OneShotTriggeredIrq;
        private uint _t2TimedOutCount;
        private byte _pb6Old;

        private uint _bitsShiftedSoFar;
        private bool _cb1OutputShiftClock;
        private bool _cb1OutputShiftClockPositiveEdge;
        private bool _cb2Shift;

        /// <summary>
        /// Gets or sets the VIA port B read callback.
        /// </summary>
        public Func<byte, byte, byte> PortBReadProvider { get; set; }

        /// <summary>
        /// Gets or sets the VIA port B write callback.
        /// </summary>
        public Action<byte, byte> PortBWritten { get; set; }

        /// <summary>
        /// Gets or sets the VIA port A read callback.
        /// </summary>
        public Func<byte, byte, byte> PortAReadProvider { get; set; }

        /// <summary>
        /// Gets or sets the VIA port A write callback.
        /// </summary>
        public Action<byte, byte> PortAWritten { get; set; }

        /// <summary>
        /// Handles the read operation.
        /// </summary>
        public byte Read(ushort address)
        {
            switch (address & 0x0F)
            {
                case 0x00:
                    {
                        byte value = ReadPortB();
                        if (_t1OutPb7Enabled)
                        {
                            if (!_t1Pb7High)
                            {
                                value &= 0x7F;
                            }
                            else
                            {
                                value |= 0x80;
                            }
                        }

                        return value;
                    }

                case 0x01:
                    return ReadPortA(true);

                case 0x02:
                    return _ddrb;

                case 0x03:
                    return _ddra;

                case 0x04:
                    ClearInterrupt(IrqTimer1);
                    return (byte)(_t1Counter & 0xFF);

                case 0x05:
                    return (byte)((_t1Counter >> 8) & 0xFF);

                case 0x06:
                    return (byte)(_t1Latch & 0xFF);

                case 0x07:
                    return (byte)((_t1Latch >> 8) & 0xFF);

                case 0x08:
                    ClearInterrupt(IrqTimer2);
                    return (byte)(_t2Counter & 0xFF);

                case 0x09:
                    return (byte)((_t2Counter >> 8) & 0xFF);

                case 0x0A:
                    {
                        byte value = _shiftRegister;
                        if ((_ifr & IrqShiftRegister) != 0)
                        {
                            _bitsShiftedSoFar = 0;
                        }

                        ClearInterrupt(IrqShiftRegister);
                        return value;
                    }

                case 0x0B:
                    return _acr;

                case 0x0C:
                    return _pcr;

                case 0x0D:
                    return ComposeIfr();

                case 0x0E:
                    return (byte)(_ier | 0x80);

                case 0x0F:
                    return ReadPortA(false);

                default:
                    return 0xFF;
            }
        }

        /// <summary>
        /// Handles the write operation.
        /// </summary>
        public void Write(ushort address, byte value)
        {
            switch (address & 0x0F)
            {
                case 0x00:
                    WritePortB(value);
                    break;

                case 0x01:
                    WritePortA(value, true);
                    break;

                case 0x02:
                    _ddrb = value;
                    NotifyPortB();
                    break;

                case 0x03:
                    _ddra = value;
                    NotifyPortA();
                    break;

                case 0x04:
                case 0x06:
                    _t1Latch = (ushort)((_t1Latch & 0xFF00) | value);
                    break;

                case 0x05:
                    _t1Latch = (ushort)((value << 8) | (_t1Latch & 0x00FF));
                    _t1Counter = _t1Latch;
                    _t1Ticking = true;
                    _t1Reload = true;
                    _t1FreeRunIrqsOn = true;
                    _t1TimedOut = _t1Counter == 0;
                    _t1Pb7High = true;
                    if (_t1OutPb7Enabled && (_ddrb & 0x80) != 0)
                    {
                        _orb &= 0x7F;
                        NotifyPortB();
                    }

                    if (!_t1FreeRun)
                    {
                        _t1OneShotTriggeredIrq = false;
                    }

                    ClearInterrupt(IrqTimer1);
                    break;

                case 0x07:
                    _t1Latch = (ushort)((value << 8) | (_t1Latch & 0x00FF));
                    if ((_ier & IrqTimer1) == 0)
                    {
                        _t1FreeRunIrqsOn = false;
                    }

                    ClearInterrupt(IrqTimer1);
                    break;

                case 0x08:
                    _t2Latch = value;
                    break;

                case 0x09:
                    _t2Counter = (ushort)((value << 8) | _t2Latch);
                    _t2Reload = true;
                    _t2TimedOutCount = 0;
                    _t2LowTimedOut = false;
                    _t2TimedOut = false;
                    _t2CountingDown = true;
                    _t2OneShotTriggeredIrq = false;
                    ClearInterrupt(IrqTimer2);
                    break;

                case 0x0A:
                    _shiftRegister = value;
                    if ((_ifr & IrqShiftRegister) != 0)
                    {
                        _bitsShiftedSoFar = 0;
                    }

                    ClearInterrupt(IrqShiftRegister);
                    _cb1OutputShiftClock = true;
                    _cb1OutputShiftClockPositiveEdge = false;
                    break;

                case 0x0B:
                    _acr = value;
                    _latchPortA = (value & AcrPortALatchEnable) != 0;
                    _latchPortB = (value & AcrPortBLatchEnable) != 0;
                    _t1FreeRun = (value & AcrTimer1FreeRun) != 0;
                    _t1OutPb7Enabled = (value & AcrTimer1Pb7Output) != 0;
                    _t2CountingPb6Mode = (value & AcrTimer2Pb6Mode) != 0;
                    if (_t1OutPb7Enabled && (_ddrb & 0x80) != 0)
                    {
                        if (_t1Pb7High)
                        {
                            _orb |= 0x80;
                        }
                        else
                        {
                            _orb &= 0x7F;
                        }

                        NotifyPortB();
                    }

                    break;

                case 0x0C:
                    _pcr = value;
                    UpdateControlledOutputs();
                    break;

                case 0x0D:
                    ClearInterrupt((byte)(value & 0x7F));
                    break;

                case 0x0E:
                    if ((value & 0x80) != 0)
                    {
                        _ier |= (byte)(value & 0x7F);
                    }
                    else
                    {
                        _ier &= (byte)~(value & 0x7F);
                    }

                    break;

                case 0x0F:
                    WritePortA(value, false);
                    break;
            }
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            if (_ca2 && _pulseCa2)
            {
                _ca2 = false;
            }

            if (_cb2 && _pulseCb2)
            {
                _cb2 = false;
            }

            TickTimer1();
            TickTimer2();
            TickShiftRegister();
            _cb1Old = _cb1;
        }

        /// <summary>
        /// Sets the ca1 level value.
        /// </summary>
        public void SetCa1Level(bool level)
        {
            if (_ca1 != level && ((_pcr & 0x01) != 0) == level)
            {
                _latchedValueA = ReadPortAInputMerged();
                if ((_pcr & 0x0E) == 0x08)
                {
                    _ca2 = false;
                }

                SetInterrupt(IrqCa1);
            }

            _ca1 = level;
        }

        /// <summary>
        /// Sets the ca1 level silently value.
        /// </summary>
        public void SetCa1LevelSilently(bool level)
        {
            _ca1 = level;
        }

        /// <summary>
        /// Sets the cb1 level value.
        /// </summary>
        public void SetCb1Level(bool level)
        {
            if (_cb1 != level && ((_pcr & 0x10) != 0) == level)
            {
                _latchedValueB = ReadPortBInputMerged();
                if ((_pcr & 0xE0) == 0x80)
                {
                    _cb2 = false;
                }

                SetInterrupt(IrqCb1);
            }

            _cb1 = level;
        }

        /// <summary>
        /// Sets the ca2 level value.
        /// </summary>
        public void SetCa2Level(bool level)
        {
            if ((_pcr & 0x08) == 0)
            {
                if (_ca2 != level && ((_pcr & 0x04) != 0) == level)
                {
                    SetInterrupt(IrqCa2);
                }

                _ca2 = level;
                return;
            }

            _ca2 = level;
        }

        /// <summary>
        /// Sets the cb2 level value.
        /// </summary>
        public void SetCb2Level(bool level)
        {
            if ((_pcr & 0x80) == 0)
            {
                if (_cb2 != level && ((_pcr & 0x40) != 0) == level)
                {
                    SetInterrupt(IrqCb2);
                }

                _cb2 = level;
                return;
            }

            _cb2 = level;
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            _pcr = 0;
            _acr = 0;

            _latchPortA = false;
            _latchedValueA = 0;
            _ca1 = false;
            _ca2 = false;
            _pulseCa2 = false;

            _latchPortB = false;
            _latchedValueB = 0;
            _cb1 = false;
            _cb1Old = false;
            _cb2 = false;
            _pulseCb2 = false;

            _t1Counter = 0xFFFF;
            _t1Latch = 0xFFFF;
            _t1Ticking = false;
            _t1Reload = false;
            _t1OutPb7Enabled = false;
            _t1FreeRun = false;
            _t1FreeRunIrqsOn = false;
            _t1TimedOut = false;
            _t1Pb7High = true;
            _t1OneShotTriggeredIrq = false;

            _t2Counter = 0xFBC9;
            _t2Latch = 0;
            _t2Reload = false;
            _t2CountingDown = false;
            _t2TimedOutCount = 0;
            _t2LowTimedOut = false;
            _t2CountingPb6Mode = false;
            _t2CountingPb6ModeOld = false;
            _pb6Old = 0;
            _t2TimedOut = false;
            _t2OneShotTriggeredIrq = false;

            _ifr = 0;
            _ier = 0;
            _shiftRegister = 0;
            _bitsShiftedSoFar = 0;
            _cb1OutputShiftClock = false;
            _cb1OutputShiftClockPositiveEdge = false;
            _cb2Shift = false;

            _orb = 0;
            _ora = 0;
            _ddrb = 0;
            _ddra = 0;

            SetCa2Level(true);
            SetCb2Level(true);
            NotifyPortA();
            NotifyPortB();
        }

        public bool IsIrqAsserted
        {
            get { return (_ifr & _ier & 0x7F) != 0; }
        }

        public byte PortAOutput
        {
            get { return _ora; }
        }

        public byte PortBOutput
        {
            get { return _orb; }
        }

        public byte PortADirection
        {
            get { return _ddra; }
        }

        public byte PortBDirection
        {
            get { return _ddrb; }
        }

        public byte AuxiliaryControlRegister
        {
            get { return _acr; }
        }

        public byte PeripheralControlRegister
        {
            get { return _pcr; }
        }

        public byte InterruptFlags
        {
            get { return _ifr; }
        }

        public byte InterruptEnable
        {
            get { return _ier; }
        }

        public bool Ca2Level
        {
            get { return _ca2; }
        }

        public bool Cb2Level
        {
            get { return _cb2; }
        }

        /// <summary>
        /// Advances the timer1 state by one emulated tick.
        /// </summary>
        private void TickTimer1()
        {
            if (_t1TimedOut)
            {
                _t1Counter = _t1Latch;
                _t1TimedOut = false;
                return;
            }

            if (!_t1Ticking || _t1Reload)
            {
                _t1Reload = false;
                return;
            }

            if (_t1Counter-- != 0)
            {
                return;
            }

            _t1TimedOut = true;
            if (_t1FreeRun)
            {
                if (_t1FreeRunIrqsOn)
                {
                    SetInterrupt(IrqTimer1);
                }

                if (_t1Latch > 1)
                {
                    _t1Pb7High = !_t1Pb7High;
                    if (_t1OutPb7Enabled && (_ddrb & 0x80) != 0)
                    {
                        if (_t1Pb7High)
                        {
                            _orb |= 0x80;
                        }
                        else
                        {
                            _orb &= 0x7F;
                        }

                        NotifyPortB();
                    }
                }
            }
            else if (!_t1OneShotTriggeredIrq)
            {
                _t1OneShotTriggeredIrq = true;
                SetInterrupt(IrqTimer1);
                if (_t1OutPb7Enabled && (_ddrb & 0x80) != 0)
                {
                    _orb |= 0x80;
                    NotifyPortB();
                }

                _t1Pb7High = false;
            }
        }

        /// <summary>
        /// Advances the timer2 state by one emulated tick.
        /// </summary>
        private void TickTimer2()
        {
            byte pb6 = (byte)(ReadPortBInputMerged() & 0x40);
            byte shiftMode = (byte)((_acr & AcrShiftRegisterControl) >> 2);
            bool shiftClockPositiveEdge = _cb1OutputShiftClockPositiveEdge;
            _cb1OutputShiftClockPositiveEdge = false;

            if (_t2TimedOut)
            {
                _t2TimedOut = false;

                if ((_acr & 0x0C) == 0x04)
                {
                    _cb1OutputShiftClockPositiveEdge = _cb1OutputShiftClock;
                    _cb1OutputShiftClock = !_cb1OutputShiftClock;
                }

                if (_t2Latch == 0xFF)
                {
                    _t2Counter--;
                }

                if (_t2TimedOutCount > 1 && ((_t2Counter >> 8) & 0xFF) == 0x00)
                {
                    _t2Counter = (ushort)(0xFF00 | (byte)(_t2Latch + 2));
                }
                else
                {
                    _t2Counter = (ushort)((_t2Counter & 0xFF00) | _t2Latch);
                }

                _t2LowTimedOut = false;
            }

            if (_t2CountingDown)
            {
                if (_t2CountingPb6Mode ^ _t2CountingPb6ModeOld)
                {
                    _t2OneShotTriggeredIrq = false;
                    if (!_t2CountingPb6Mode)
                    {
                        if (_t2Counter == 0)
                        {
                            SetInterrupt(IrqTimer2);
                        }
                    }
                    else
                    {
                        _t2Counter--;
                        _t2TimedOut = _t2Counter == 0;
                    }
                }
                else if (!_t2Reload)
                {
                    if (_t2CountingPb6Mode && _t2CountingPb6ModeOld)
                    {
                        if (pb6 == 0 && _pb6Old == 0x40)
                        {
                            _t2Counter--;
                            _t2TimedOut = _t2Counter == 0;
                        }
                    }
                    else
                    {
                        _t2Counter--;
                        _t2TimedOut = _t2Counter == 0;
                        if ((_t2Counter & 0xFF) == 0xFE)
                        {
                            _t2TimedOutCount++;
                            if ((_acr & 0x0C) == 0x04 && _t2TimedOutCount > 1)
                            {
                                _cb1OutputShiftClockPositiveEdge = _cb1OutputShiftClock;
                                _cb1OutputShiftClock = !_cb1OutputShiftClock;
                                _t2Counter = (ushort)((_t2Counter & 0xFF00) | _t2Latch);
                                _t2TimedOut = false;
                            }
                        }
                    }
                }
                else
                {
                    _t2Reload = false;
                }

                if (_t2TimedOut)
                {
                    if (!_t2OneShotTriggeredIrq)
                    {
                        _t2OneShotTriggeredIrq = true;
                        SetInterrupt(IrqTimer2);
                    }
                }
            }

            _pb6Old = pb6;
            _t2CountingPb6ModeOld = _t2CountingPb6Mode;

            TickShiftMode(shiftMode, shiftClockPositiveEdge);
        }

        /// <summary>
        /// Advances the shift register state by one emulated tick.
        /// </summary>
        private void TickShiftRegister()
        {
            // Shift register timing is folded into TickTimer2/shift mode,
            // mirroring Pi1541's sequencing.
        }

        /// <summary>
        /// Advances the shift mode state by one emulated tick.
        /// </summary>
        private void TickShiftMode(byte shiftMode, bool shiftClockPositiveEdge)
        {
            switch (shiftMode)
            {
                case 0:
                    return;

                case 1:
                    if (_t2TimedOutCount > 2 && shiftClockPositiveEdge && (_bitsShiftedSoFar & 8) == 0)
                    {
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2 ? 1 : 0));
                        if (++_bitsShiftedSoFar == 8)
                        {
                            SetInterrupt(IrqShiftRegister);
                        }
                    }

                    return;

                case 2:
                    if ((_bitsShiftedSoFar & 8) == 0)
                    {
                        _cb1OutputShiftClock = !_cb1OutputShiftClock;
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2 ? 1 : 0));
                        if (++_bitsShiftedSoFar == 8)
                        {
                            SetInterrupt(IrqShiftRegister);
                        }
                    }

                    return;

                case 3:
                    if (_cb1Old && !_cb1 && (_bitsShiftedSoFar & 8) == 0)
                    {
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2 ? 1 : 0));
                        if (++_bitsShiftedSoFar == 8)
                        {
                            SetInterrupt(IrqShiftRegister);
                        }
                    }

                    return;

                case 4:
                    if (shiftClockPositiveEdge)
                    {
                        _cb2Shift = (_shiftRegister & 0x80) != 0;
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2Shift ? 1 : 0));
                    }

                    return;

                case 5:
                    if (_t2TimedOutCount > 2 && shiftClockPositiveEdge && (_bitsShiftedSoFar & 8) == 0)
                    {
                        _cb2Shift = (_shiftRegister & 0x80) != 0;
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2Shift ? 1 : 0));
                        if (++_bitsShiftedSoFar == 8)
                        {
                            SetInterrupt(IrqShiftRegister);
                        }
                    }

                    return;

                case 6:
                    if ((_bitsShiftedSoFar & 8) == 0)
                    {
                        _cb1OutputShiftClock = !_cb1OutputShiftClock;
                        _cb2Shift = (_shiftRegister & 0x80) != 0;
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2Shift ? 1 : 0));
                        if (++_bitsShiftedSoFar == 8)
                        {
                            SetInterrupt(IrqShiftRegister);
                        }
                    }

                    return;

                case 7:
                    if (_cb1Old && !_cb1 && (_bitsShiftedSoFar & 8) == 0)
                    {
                        _cb1OutputShiftClock = !_cb1OutputShiftClock;
                        _cb2Shift = (_shiftRegister & 0x80) != 0;
                        _shiftRegister = (byte)((_shiftRegister << 1) | (_cb2Shift ? 1 : 0));
                        if (++_bitsShiftedSoFar == 8)
                        {
                            SetInterrupt(IrqShiftRegister);
                        }
                    }

                    return;
            }
        }

        /// <summary>
        /// Reads port b.
        /// </summary>
        private byte ReadPortB()
        {
            byte value = _latchPortB && (_ifr & IrqCb1) != 0
                ? _latchedValueB
                : ReadPortBInputMerged();

            ClearInterrupt((byte)(IrqCb1 | IrqCb2));
            return value;
        }

        /// <summary>
        /// Writes port b.
        /// </summary>
        private void WritePortB(byte value)
        {
            _orb = value;
            ClearInterrupt((byte)(IrqCb1 | IrqCb2));
            if ((_pcr & 0xA0) == 0xA0)
            {
                _cb2 = false;
            }

            NotifyPortB();
        }

        /// <summary>
        /// Reads port a.
        /// </summary>
        private byte ReadPortA(bool handshake)
        {
            byte value = _latchPortA && (_ifr & IrqCa1) != 0
                ? _latchedValueA
                : ReadPortAInputMerged();

            if (handshake)
            {
                ClearInterrupt((byte)(IrqCa1 | IrqCa2));
            }

            return value;
        }

        /// <summary>
        /// Writes port a.
        /// </summary>
        private void WritePortA(byte value, bool handshake)
        {
            if (handshake)
            {
                ClearInterrupt((byte)(IrqCa1 | IrqCa2));
                if ((_pcr & 0x0C) == 0x0C)
                {
                    _ca2 = false;
                }
            }

            _ora = value;
            NotifyPortA();
        }

        /// <summary>
        /// Handles the notify port a operation.
        /// </summary>
        private void NotifyPortA()
        {
            UpdateControlledOutputs();
            PortAWritten?.Invoke(_ora, _ddra);
        }

        /// <summary>
        /// Handles the notify port b operation.
        /// </summary>
        private void NotifyPortB()
        {
            UpdateControlledOutputs();
            PortBWritten?.Invoke(_orb, _ddrb);
        }

        /// <summary>
        /// Updates controlled outputs.
        /// </summary>
        private void UpdateControlledOutputs()
        {
            if ((_pcr & 0x08) != 0)
            {
                _pulseCa2 = (_pcr & 0x06) == 0x02;
                _ca2 = !_pulseCa2 && (_pcr & 0x06) == 0x06;
            }

            if ((_pcr & 0x80) != 0)
            {
                _pulseCb2 = (_pcr & 0x60) == 0x20;
                _cb2 = !_pulseCb2 && (_pcr & 0x60) == 0x60;
            }
        }

        /// <summary>
        /// Composes ifr.
        /// </summary>
        private byte ComposeIfr()
        {
            byte value = (byte)(_ifr & 0x7F);
            if ((value & _ier & 0x7F) != 0)
            {
                value |= 0x80;
            }

            return value;
        }

        /// <summary>
        /// Sets the interrupt value.
        /// </summary>
        private void SetInterrupt(byte mask)
        {
            _ifr |= (byte)(mask & 0x7F);
        }

        /// <summary>
        /// Handles the clear interrupt operation.
        /// </summary>
        private void ClearInterrupt(byte mask)
        {
            _ifr &= (byte)~(mask & 0x7F);
        }

        /// <summary>
        /// Reads port a input merged.
        /// </summary>
        private byte ReadPortAInputMerged()
        {
            byte input = PortAReadProvider != null ? PortAReadProvider(_ora, _ddra) : (byte)0xFF;
            return (byte)((input & (byte)~_ddra) | (_ora & _ddra));
        }

        /// <summary>
        /// Reads port b input merged.
        /// </summary>
        private byte ReadPortBInputMerged()
        {
            byte input = PortBReadProvider != null ? PortBReadProvider(_orb, _ddrb) : (byte)0xFF;
            return (byte)((input & (byte)~_ddrb) | (_orb & _ddrb));
        }
    }
}
