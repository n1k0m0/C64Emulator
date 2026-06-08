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
        private const byte TimerStartDelayCycles = 2;
        private const byte TimerForceLoadStartDelayCycles = 3;
        private const byte TimerOutputEventNone = 0;
        private const byte TimerOutputEventPulse = 1;
        private const byte TimerOutputEventToggle = 2;
        private const byte OldCiaTimerBInterruptReadHazardTicks = 0;
        private const byte JoystickUpMask = 0x01;
        private const byte JoystickDownMask = 0x02;
        private const byte JoystickLeftMask = 0x04;
        private const byte JoystickRightMask = 0x08;
        private const byte JoystickFireMask = 0x10;
        private readonly CiaChipRevision _chipRevision;
        private readonly byte[] _registers = new byte[0x10];
        private readonly bool[,] _keyboardMatrix = new bool[8, 8];
        private readonly bool[,] _networkKeyboardMatrix = new bool[8, 8];
        private readonly Dictionary<Key, MatrixKey> _keyMap = new Dictionary<Key, MatrixKey>();

        private ushort _timerALatch;
        private ushort _timerACounter;
        private ushort _timerBLatch;
        private ushort _timerBCounter;
        private byte _timerAStartDelay;
        private byte _timerBStartDelay;
        private bool _timerAReloadHold;
        private bool _timerBReloadHold;
        private bool _timerAForceLoadedZero;
        private bool _timerBForceLoadedZero;
        private byte _timerAReadSubtract;
        private byte _timerBReadSubtract;
        private bool _timerAReadSubtractSticky;
        private bool _timerBReadSubtractSticky;
        private byte _timerASourceSwitchDelay;
        private bool _timerASourceSwitchTargetCounts;
        private byte _timerBSourceSwitchDelay;
        private byte _timerBSourceSwitchPreviousControl;
        private bool _timerAUnderflowPendingForTimerB;
        private bool _timerAOutputHigh;
        private bool _timerBOutputHigh;
        private byte _timerAOutputPulseCycles;
        private byte _timerBOutputPulseCycles;
        private byte _timerAOutputPendingEvent;
        private byte _timerBOutputPendingEvent;
        private byte _interruptMask;
        private byte _interruptFlags;
        private byte _delayedInterruptLineFlags;
        private byte _ticksSinceInterruptRead;
        private bool _interruptSummaryPending;
        private byte _joystickPort1State = 0x1F;
        private byte _joystickPort2State = 0x1F;
        private byte _gamepadJoystickPort1State = 0x1F;
        private byte _gamepadJoystickPort2State = 0x1F;
        private byte _networkJoystickPort1State = 0x1F;
        private byte _networkJoystickPort2State = 0x1F;
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
            : this(CiaChipRevision.Mos6526A)
        {
        }

        /// <summary>
        /// Initializes a new Cia1 instance.
        /// </summary>
        public Cia1(CiaChipRevision chipRevision)
        {
            _chipRevision = chipRevision;
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
            System.Array.Clear(_networkKeyboardMatrix, 0, _networkKeyboardMatrix.Length);
            _registers[0x00] = 0xFF;
            _registers[0x01] = 0xFF;
            _timerALatch = 0;
            _timerACounter = 0;
            _timerBLatch = 0;
            _timerBCounter = 0;
            _timerAStartDelay = 0;
            _timerBStartDelay = 0;
            _timerAReloadHold = false;
            _timerBReloadHold = false;
            _timerAForceLoadedZero = false;
            _timerBForceLoadedZero = false;
            _timerAReadSubtract = 0;
            _timerBReadSubtract = 0;
            _timerAReadSubtractSticky = false;
            _timerBReadSubtractSticky = false;
            _timerASourceSwitchDelay = 0;
            _timerASourceSwitchTargetCounts = false;
            _timerBSourceSwitchDelay = 0;
            _timerBSourceSwitchPreviousControl = 0;
            _timerAUnderflowPendingForTimerB = false;
            _timerAOutputHigh = false;
            _timerBOutputHigh = false;
            _timerAOutputPulseCycles = 0;
            _timerBOutputPulseCycles = 0;
            _timerAOutputPendingEvent = TimerOutputEventNone;
            _timerBOutputPendingEvent = TimerOutputEventNone;
            _interruptMask = 0;
            _interruptFlags = 0;
            _delayedInterruptLineFlags = 0;
            _ticksSinceInterruptRead = byte.MaxValue;
            _interruptSummaryPending = false;
            _joystickPort1State = 0x1F;
            _joystickPort2State = 0x1F;
            _gamepadJoystickPort1State = 0x1F;
            _gamepadJoystickPort2State = 0x1F;
            _networkJoystickPort1State = 0x1F;
            _networkJoystickPort2State = 0x1F;
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
            ReleaseDelayedInterruptLine();
            TickTimerOutputPulses();
            ApplyPendingTimerOutputEvents();
            bool timerAUnderflow = TickTimerA();
            bool suppressTimerBInterrupt = false;
            if (_timerAUnderflowPendingForTimerB)
            {
                timerAUnderflow = true;
                _timerAUnderflowPendingForTimerB = false;
                suppressTimerBInterrupt = true;
            }

            TickTimerB(timerAUnderflow, suppressTimerBInterrupt);
            TickTod();
            AdvanceInterruptReadHazardWindow();
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
                    return ReadVisibleTimerLow(_timerACounter, _timerAReadSubtract);
                case 0x05:
                    return ReadVisibleTimerHigh(_timerACounter, _timerAReadSubtract);
                case 0x06:
                    return ReadVisibleTimerLow(_timerBCounter, _timerBReadSubtract);
                case 0x07:
                    return ReadVisibleTimerHigh(_timerBCounter, _timerBReadSubtract);
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
                    byte value = ReadInterruptControlRegister();
                    _interruptFlags = 0;
                    _delayedInterruptLineFlags = 0;
                    _ticksSinceInterruptRead = 0;
                    _interruptSummaryPending = false;
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
            byte previousValue = _registers[address];
            _registers[address] = value;

            switch (address)
            {
                case 0x04:
                    _timerALatch = (ushort)((_timerALatch & 0xFF00) | value);
                    _timerAReadSubtract = 0;
                    _timerAReadSubtractSticky = false;
                    ApplyTimerLowWriteToForceLoadedZero(ref _timerACounter, ref _timerAForceLoadedZero, ref _timerAReloadHold, _registers[0x0E], value);
                    break;
                case 0x05:
                    _timerALatch = (ushort)((_timerALatch & 0x00FF) | (value << 8));
                    _timerAForceLoadedZero = false;
                    _timerAReadSubtract = 0;
                    _timerAReadSubtractSticky = false;
                    if ((_registers[0x0E] & 0x01) == 0)
                    {
                        _timerACounter = _timerALatch;
                        _timerAReloadHold = false;
                    }
                    break;
                case 0x06:
                    _timerBLatch = (ushort)((_timerBLatch & 0xFF00) | value);
                    _timerBReadSubtract = 0;
                    _timerBReadSubtractSticky = false;
                    ApplyTimerLowWriteToForceLoadedZero(ref _timerBCounter, ref _timerBForceLoadedZero, ref _timerBReloadHold, _registers[0x0F], value);
                    break;
                case 0x07:
                    _timerBLatch = (ushort)((_timerBLatch & 0x00FF) | (value << 8));
                    _timerBForceLoadedZero = false;
                    _timerBReadSubtract = 0;
                    _timerBReadSubtractSticky = false;
                    if ((_registers[0x0F] & 0x01) == 0)
                    {
                        _timerBCounter = _timerBLatch;
                        _timerBReloadHold = false;
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
                    _interruptMask = Cia6526TimerRules.ApplyInterruptMaskWrite(_interruptMask, value);
                    if ((value & 0x80) == 0)
                    {
                        _interruptSummaryPending = true;
                    }

                    break;
                case 0x0E:
                    bool timerAWasRunning = (previousValue & 0x01) != 0;
                    bool timerAStarts = (value & 0x01) != 0;
                    bool timerAForceLoad = (value & 0x10) != 0;
                    if (((previousValue ^ value) & 0x20) != 0)
                    {
                        _timerASourceSwitchDelay = 2;
                        _timerASourceSwitchTargetCounts = Cia6526TimerRules.TimerACounts(value);
                    }

                    if (timerAWasRunning && !timerAStarts && !timerAForceLoad)
                    {
                        if (TickTimerA(previousValue) && HasHighByteOnlyLatch(_timerALatch))
                        {
                            _timerAUnderflowPendingForTimerB = true;
                        }

                        _timerAReadSubtract = GetTimerStopVisibleReadSubtract(_timerALatch, _timerAReadSubtractSticky);
                        if (_timerAReadSubtract != 0 && ReadVisibleTimerLow(_timerACounter, _timerAReadSubtract) == 0)
                        {
                            _timerAReadSubtractSticky = true;
                        }
                    }

                    if (timerAWasRunning &&
                        timerAStarts &&
                        !timerAForceLoad &&
                        Cia6526TimerRules.TimerACounts(previousValue) &&
                        !Cia6526TimerRules.TimerACounts(value))
                    {
                        _timerAReadSubtract = GetTimerSourceVisibleReadSubtract(_timerALatch);
                    }

                    if (timerAWasRunning &&
                        timerAStarts &&
                        !timerAForceLoad &&
                        !Cia6526TimerRules.TimerACounts(previousValue) &&
                        Cia6526TimerRules.TimerACounts(value))
                    {
                        _timerAReadSubtract = 0;
                    }

                    if ((previousValue & 0x01) == 0 && (value & 0x01) != 0)
                    {
                        if (!_timerAReadSubtractSticky)
                        {
                            _timerAReadSubtract = 0;
                        }

                        bool resumesActiveCounter = (value & 0x10) == 0 && _timerACounter != _timerALatch;
                        _timerAStartDelay = resumesActiveCounter ? (byte)1 : GetTimerStartDelay(value);
                        if ((value & 0x10) == 0 && _timerALatch == 0 && _timerACounter == 0)
                        {
                            _timerAForceLoadedZero = true;
                        }
                    }

                    if ((value & 0x10) != 0)
                    {
                        ushort timerACounterBeforeForce = _timerACounter;
                        _timerACounter = Cia6526TimerRules.ForceLoad(_timerALatch);
                        _timerAForceLoadedZero = (_timerALatch == 0 && (value & 0x01) != 0);
                        _timerAReadSubtract = 0;
                        _timerAReadSubtractSticky = false;
                        if (_timerAForceLoadedZero)
                        {
                            _interruptFlags |= 0x01;
                        }

                        _timerAReloadHold = false;
                        if (timerAWasRunning && timerAStarts)
                        {
                            AdvanceOldCiaRunningForceLoadPhase(ref _timerACounter);
                            _timerAStartDelay = GetRunningForceLoadStartDelay(timerACounterBeforeForce);
                        }

                        _registers[0x0E] &= 0xEF;
                    }
                    break;
                case 0x0F:
                    bool timerBWasRunning = (previousValue & 0x01) != 0;
                    bool timerBStarts = (value & 0x01) != 0;
                    bool timerBForceLoad = (value & 0x10) != 0;
                    if (((previousValue ^ value) & 0x60) != 0)
                    {
                        _timerBSourceSwitchDelay = 2;
                        _timerBSourceSwitchPreviousControl = previousValue;
                    }

                    if (timerBWasRunning && !timerBStarts && !timerBForceLoad)
                    {
                        TickTimerB(previousValue, Cia6526TimerRules.TimerBCounts(previousValue, false));
                        _timerBReadSubtract = GetTimerStopVisibleReadSubtract(_timerBLatch, _timerBReadSubtractSticky);
                        if (_timerBReadSubtract != 0 && ReadVisibleTimerLow(_timerBCounter, _timerBReadSubtract) == 0)
                        {
                            _timerBReadSubtractSticky = true;
                        }
                    }

                    if ((previousValue & 0x01) == 0 && (value & 0x01) != 0)
                    {
                        if (!_timerBReadSubtractSticky)
                        {
                            _timerBReadSubtract = 0;
                        }

                        bool resumesActiveCounter = (value & 0x10) == 0 && _timerBCounter != _timerBLatch;
                        _timerBStartDelay = resumesActiveCounter ? (byte)1 : GetTimerStartDelay(value);
                        if ((value & 0x10) == 0 && _timerBLatch == 0 && _timerBCounter == 0)
                        {
                            _timerBForceLoadedZero = true;
                        }
                    }

                    if ((value & 0x10) != 0)
                    {
                        _timerBCounter = Cia6526TimerRules.ForceLoad(_timerBLatch);
                        _timerBForceLoadedZero = (_timerBLatch == 0 && (value & 0x01) != 0);
                        _timerBReadSubtract = 0;
                        _timerBReadSubtractSticky = false;
                        if (_timerBForceLoadedZero)
                        {
                            _interruptFlags |= 0x02;
                        }

                        _timerBReloadHold = false;
                        if (timerBWasRunning && timerBStarts)
                        {
                            _timerBStartDelay = 3;
                        }

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
            byte visibleFlags = _chipRevision == CiaChipRevision.Mos6526
                ? (byte)(_interruptFlags & ~_delayedInterruptLineFlags)
                : _interruptFlags;
            return (visibleFlags & _interruptMask & 0x1F) != 0;
        }

        /// <summary>
        /// Reads the interrupt-control register with its one-shot summary bit.
        /// </summary>
        private byte ReadInterruptControlRegister()
        {
            bool hasFlags = (_interruptFlags & 0x1F) != 0;
            bool showSummary = IsIrqAsserted() || (_interruptSummaryPending && hasFlags);
            return (byte)(_interruptFlags | (showSummary ? 0x80 : 0x00));
        }

        /// <summary>
        /// Releases old-CIA timer interrupt lines after their one-tick visibility delay.
        /// </summary>
        private void ReleaseDelayedInterruptLine()
        {
            _delayedInterruptLineFlags = 0;
        }

        /// <summary>
        /// Ages the old-CIA Timer B interrupt-read hazard window by one CIA tick.
        /// </summary>
        private void AdvanceInterruptReadHazardWindow()
        {
            if (_ticksSinceInterruptRead < byte.MaxValue)
            {
                _ticksSinceInterruptRead++;
            }
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
        /// Gets compact debug information for CIA1.
        /// </summary>
        public string GetDebugInfo()
        {
            return string.Format(
                "pra={0:X2} prb={1:X2} ddra={2:X2} ddrb={3:X2} ta={4:X4}/{5:X4} tb={6:X4}/{7:X4} icr={8:X2}/{9:X2} tod={10:X2}:{11:X2}:{12:X2}.{13:X1} joy={14}/{15}",
                ReadPortA(),
                ReadPortB(),
                _registers[0x02],
                _registers[0x03],
                _timerACounter,
                _timerALatch,
                _timerBCounter,
                _timerBLatch,
                _interruptFlags,
                _interruptMask,
                _todHours,
                _todMinutes,
                _todSeconds,
                _todTenths,
                _joystickPort1State & _gamepadJoystickPort1State & _networkJoystickPort1State,
                _joystickPort2State & _gamepadJoystickPort2State & _networkJoystickPort2State);
        }

        /// <summary>
        /// Sets the host gamepad joystick state using C64 active-low joystick bits.
        /// </summary>
        /// <param name="activeLowJoystickState">
        /// Bits 0-4 represent up, down, left, right, and fire; a cleared bit means pressed.
        /// </param>
        public void SetGamepadJoystickState(byte activeLowJoystickState)
        {
            // Bits 5-7 are not joystick lines. Keep them high so a caller cannot
            // accidentally pull keyboard-matrix bits low through the joystick path.
            activeLowJoystickState = (byte)(activeLowJoystickState | 0xE0);
            // Recompute the per-port gamepad contribution from the current frontend
            // joystick-port setting instead of leaving stale bits on the old port.
            _gamepadJoystickPort1State = 0x1F;
            _gamepadJoystickPort2State = 0x1F;

            if (_activeJoystickPort == JoystickPort.Port1 || _activeJoystickPort == JoystickPort.Both)
            {
                _gamepadJoystickPort1State = activeLowJoystickState;
            }

            if (_activeJoystickPort == JoystickPort.Port2 || _activeJoystickPort == JoystickPort.Both)
            {
                _gamepadJoystickPort2State = activeLowJoystickState;
            }
        }

        /// <summary>
        /// Sets a network-controlled joystick state for one or both C64 joystick ports.
        /// </summary>
        /// <param name="joystickPort">C64 port that should receive the remote input.</param>
        /// <param name="activeLowJoystickState">
        /// Bits 0-4 represent up, down, left, right, and fire; a cleared bit means pressed.
        /// </param>
        public void SetNetworkJoystickState(JoystickPort joystickPort, byte activeLowJoystickState)
        {
            // Network input is a separate layer from keyboard and local gamepad input.
            // ReadPortA/ReadPortB combine layers with active-low AND semantics.
            activeLowJoystickState = (byte)(activeLowJoystickState | 0xE0);
            if (joystickPort == JoystickPort.Port1 || joystickPort == JoystickPort.Both)
            {
                _networkJoystickPort1State = activeLowJoystickState;
            }

            if (joystickPort == JoystickPort.Port2 || joystickPort == JoystickPort.Both)
            {
                _networkJoystickPort2State = activeLowJoystickState;
            }
        }

        /// <summary>
        /// Resets network-controlled joystick input to neutral.
        /// </summary>
        public void ClearNetworkJoystickState()
        {
            // 0x1F is neutral for the five C64 joystick lines. The upper bits are forced
            // high again when the state is merged into the CIA port read.
            _networkJoystickPort1State = 0x1F;
            _networkJoystickPort2State = 0x1F;
        }

        /// <summary>
        /// Sets a network-controlled keyboard matrix key without touching joystick lines.
        /// </summary>
        /// <param name="key">Frontend key to map into the C64 keyboard matrix.</param>
        /// <param name="pressed">True when the remote key is currently held.</param>
        public void SetNetworkKeyState(Key key, bool pressed)
        {
            // Remote keyboard rights are separate from joystick rights. Do not call
            // SetJoystickState here, otherwise cursor/fire keys would bypass the host's
            // joystick permission and become joystick input as a side effect.
            SetKeyState(key, pressed, _networkKeyboardMatrix);
        }

        /// <summary>
        /// Releases all network-controlled keyboard matrix keys.
        /// </summary>
        public void ClearNetworkKeyboardState()
        {
            System.Array.Clear(_networkKeyboardMatrix, 0, _networkKeyboardMatrix.Length);
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
            return TickTimerA(_registers[0x0E]);
        }

        /// <summary>
        /// Advances timer A using the supplied control-register snapshot.
        /// </summary>
        private bool TickTimerA(byte controlRegister)
        {
            if (_timerAForceLoadedZero && (controlRegister & 0x01) != 0)
            {
                return false;
            }

            bool countPulse = GetTimerACountPulse(controlRegister);
            bool underflow = Cia6526TimerRules.Tick(
                ref _timerACounter,
                _timerALatch,
                ref _timerAStartDelay,
                ref _timerAReloadHold,
                controlRegister,
                countPulse,
                out bool stopOneShot,
                (controlRegister & 0x08) != 0);
            if (!underflow)
            {
                return false;
            }

            _timerAForceLoadedZero = false;
            RaiseTimerInterruptFlag(0x01);
            if (stopOneShot)
            {
                _registers[0x0E] &= 0xFE;
            }

            QueueTimerOutputUnderflow(_registers[0x0E], ref _timerAOutputPendingEvent);
            return true;
        }

        /// <summary>
        /// Advances the timer b state by one emulated tick.
        /// </summary>
        private void TickTimerB(bool timerAUnderflow)
        {
            TickTimerB(timerAUnderflow, false);
        }

        /// <summary>
        /// Advances the timer b state by one emulated tick with explicit interrupt suppression.
        /// </summary>
        private void TickTimerB(bool timerAUnderflow, bool suppressInterruptFlag)
        {
            byte controlRegister = _registers[0x0F];
            byte sourceControlRegister = GetTimerBSourceControlRegister(controlRegister);
            TickTimerB(
                controlRegister,
                Cia6526TimerRules.TimerBCounts(sourceControlRegister, timerAUnderflow),
                Cia6526TimerRules.TimerBUsesTimerAUnderflows(sourceControlRegister),
                suppressInterruptFlag);
        }

        /// <summary>
        /// Advances timer B using the supplied control-register snapshot.
        /// </summary>
        private void TickTimerB(byte controlRegister, bool countPulse)
        {
            TickTimerB(
                controlRegister,
                countPulse,
                Cia6526TimerRules.TimerBUsesTimerAUnderflows(controlRegister),
                false);
        }

        /// <summary>
        /// Advances timer B using explicit count and source pipeline state.
        /// </summary>
        private void TickTimerB(byte controlRegister, bool countPulse, bool exposeTerminalZero)
        {
            TickTimerB(controlRegister, countPulse, exposeTerminalZero, false);
        }

        /// <summary>
        /// Advances timer B using explicit count, source pipeline, and interrupt timing state.
        /// </summary>
        private void TickTimerB(byte controlRegister, bool countPulse, bool exposeTerminalZero, bool suppressInterruptFlag)
        {
            if (_timerBForceLoadedZero && (controlRegister & 0x01) != 0)
            {
                return;
            }

            bool underflow = Cia6526TimerRules.Tick(
                ref _timerBCounter,
                _timerBLatch,
                ref _timerBStartDelay,
                ref _timerBReloadHold,
                controlRegister,
                countPulse,
                out bool stopOneShot,
                exposeTerminalZero);
            if (!underflow)
            {
                return;
            }

            _timerBForceLoadedZero = false;
            if (!suppressInterruptFlag)
            {
                RaiseTimerInterruptFlag(0x02);
            }

            if (stopOneShot)
            {
                _registers[0x0F] &= 0xFE;
            }

            QueueTimerOutputUnderflow(_registers[0x0F], ref _timerBOutputPendingEvent);
        }

        /// <summary>
        /// Advances one-cycle timer output pulses on port B.
        /// </summary>
        private void TickTimerOutputPulses()
        {
            TickTimerOutputPulse(ref _timerAOutputHigh, ref _timerAOutputPulseCycles);
            TickTimerOutputPulse(ref _timerBOutputHigh, ref _timerBOutputPulseCycles);
        }

        /// <summary>
        /// Applies PB6/PB7 timer-output events queued by the previous CIA tick.
        /// </summary>
        private void ApplyPendingTimerOutputEvents()
        {
            ApplyPendingTimerOutputEvent(ref _timerAOutputHigh, ref _timerAOutputPulseCycles, ref _timerAOutputPendingEvent);
            ApplyPendingTimerOutputEvent(ref _timerBOutputHigh, ref _timerBOutputPulseCycles, ref _timerBOutputPendingEvent);
        }

        /// <summary>
        /// Advances a single timer output pulse flip-flop.
        /// </summary>
        private static void TickTimerOutputPulse(ref bool outputHigh, ref byte pulseCycles)
        {
            if (pulseCycles == 0)
            {
                return;
            }

            pulseCycles--;
            if (pulseCycles == 0)
            {
                outputHigh = false;
            }
        }

        /// <summary>
        /// Applies one delayed PB6/PB7 timer-output side effect.
        /// </summary>
        private static void ApplyPendingTimerOutputEvent(ref bool outputHigh, ref byte pulseCycles, ref byte pendingEvent)
        {
            byte eventType = pendingEvent;
            pendingEvent = TimerOutputEventNone;

            if (eventType == TimerOutputEventToggle)
            {
                outputHigh = !outputHigh;
                pulseCycles = 0;
                return;
            }

            if (eventType == TimerOutputEventPulse)
            {
                outputHigh = true;
                pulseCycles = 1;
            }
        }

        /// <summary>
        /// Applies the configured CIA revision's timer-interrupt visibility phase.
        /// </summary>
        private void RaiseTimerInterruptFlag(byte flag)
        {
            if (flag == 0x02 &&
                _chipRevision == CiaChipRevision.Mos6526 &&
                _ticksSinceInterruptRead <= OldCiaTimerBInterruptReadHazardTicks)
            {
                return;
            }

            if (_chipRevision == CiaChipRevision.Mos6526)
            {
                _delayedInterruptLineFlags |= flag;
            }

            _interruptFlags |= flag;
        }

        /// <summary>
        /// Queues the delayed PB6/PB7 timer-output side effect produced by a timer underflow.
        /// </summary>
        private static void QueueTimerOutputUnderflow(byte controlRegister, ref byte pendingEvent)
        {
            if ((controlRegister & 0x02) == 0)
            {
                return;
            }

            if ((controlRegister & 0x04) != 0)
            {
                pendingEvent = TimerOutputEventToggle;
                return;
            }

            pendingEvent = TimerOutputEventPulse;
        }

        /// <summary>
        /// Gets the first-count delay after a timer start control write.
        /// </summary>
        private static byte GetTimerStartDelay(byte controlRegister)
        {
            if ((controlRegister & 0x10) == 0)
            {
                return TimerStartDelayCycles;
            }

            return (controlRegister & 0x02) != 0 ? TimerStartDelayCycles : TimerForceLoadStartDelayCycles;
        }

        /// <summary>
        /// Returns the restart phase for FORCE LOAD while timer A is already running.
        /// </summary>
        private byte GetRunningForceLoadStartDelay(ushort previousCounter)
        {
            // CIA-AcountsB reloads CIA1 timer A from the KERNAL warm state, where
            // the timer is already active. When the old counter is not close to
            // terminal count, real silicon starts counting from the newly forced
            // latch on the next CIA tick instead of applying the cold-start delay.
            if ((previousCounter & 0xFF00) == 0)
            {
                return _chipRevision == CiaChipRevision.Mos6526 ? (byte)4 : (byte)3;
            }

            return 0;
        }

        /// <summary>
        /// Applies the original 6526 running FORCE LOAD write phase.
        /// </summary>
        private void AdvanceOldCiaRunningForceLoadPhase(ref ushort counter)
        {
            if (_chipRevision != CiaChipRevision.Mos6526 || counter == 0)
            {
                return;
            }

            counter--;
        }

        /// <summary>
        /// Returns the counter value visible to timer register reads.
        /// </summary>
        private static byte ReadVisibleTimerLow(ushort counter, byte subtract)
        {
            byte low = (byte)(counter & 0xFF);
            if (low < subtract)
            {
                return 0;
            }

            return (byte)(low - subtract);
        }

        /// <summary>
        /// Returns the high byte after the transient visible low-byte phase borrowed from it.
        /// </summary>
        private static byte ReadVisibleTimerHigh(ushort counter, byte subtract)
        {
            if (subtract != 0 && (counter & 0xFF) < subtract)
            {
                return (byte)(((counter - subtract) >> 8) & 0xFF);
            }

            return (byte)((counter >> 8) & 0xFF);
        }

        /// <summary>
        /// Gets the transient visible low-byte phase after stopping a high-byte-only timer.
        /// </summary>
        private static byte GetTimerStopVisibleReadSubtract(ushort latch, bool sticky)
        {
            if (!HasHighByteOnlyLatch(latch))
            {
                return 0;
            }

            return sticky ? (byte)2 : (byte)1;
        }

        /// <summary>
        /// Gets the transient visible low-byte phase after switching timer A away from Phi2.
        /// </summary>
        private static byte GetTimerSourceVisibleReadSubtract(ushort latch)
        {
            return 0;
        }

        /// <summary>
        /// Returns whether the timer was loaded through the high byte while the low latch stayed zero.
        /// </summary>
        private static bool HasHighByteOnlyLatch(ushort latch)
        {
            return (latch & 0xFF00) != 0 && (latch & 0x00FF) == 0;
        }

        /// <summary>
        /// Gets timer A's count pulse after applying the delayed Phi2/CNT source switch.
        /// </summary>
        private bool GetTimerACountPulse(byte controlRegister)
        {
            if (_timerASourceSwitchDelay == 0)
            {
                return Cia6526TimerRules.TimerACounts(controlRegister);
            }

            _timerASourceSwitchDelay--;
            return !_timerASourceSwitchTargetCounts;
        }

        /// <summary>
        /// Gets the Timer B source bits after applying the delayed source switch pipeline.
        /// </summary>
        private byte GetTimerBSourceControlRegister(byte controlRegister)
        {
            if (_timerBSourceSwitchDelay == 0)
            {
                return controlRegister;
            }

            _timerBSourceSwitchDelay--;
            return _timerBSourceSwitchPreviousControl;
        }

        /// <summary>
        /// Handles the 6526 edge case where a running timer was force-loaded with latch zero.
        /// </summary>
        private static void ApplyTimerLowWriteToForceLoadedZero(
            ref ushort counter,
            ref bool forceLoadedZero,
            ref bool reloadHold,
            byte controlRegister,
            byte lowValue)
        {
            if (!forceLoadedZero || (controlRegister & 0x01) == 0)
            {
                return;
            }

            counter = lowValue;
            reloadHold = false;
            forceLoadedZero = false;
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
                    if ((_keyboardMatrix[row, column] || _networkKeyboardMatrix[row, column]) && ((_registers[0x02] >> column) & 0x01) == 0)
                    {
                        result = (byte)(result & ~(1 << column));
                    }
                }
            }

            result = (byte)(result & ((_joystickPort2State & _gamepadJoystickPort2State & _networkJoystickPort2State) | 0xE0));
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
                    if ((_keyboardMatrix[row, column] || _networkKeyboardMatrix[row, column]) && ((_registers[0x03] >> row) & 0x01) == 0)
                    {
                        result = (byte)(result & ~(1 << row));
                    }
                }
            }

            result = (byte)(result & ((_joystickPort1State & _gamepadJoystickPort1State & _networkJoystickPort1State) | 0xE0));
            result = ApplyTimerPortBOutputs(result);
            return result;
        }

        /// <summary>
        /// Overlays CIA timer output bits PB6/PB7 onto the value read from port B.
        /// </summary>
        private byte ApplyTimerPortBOutputs(byte value)
        {
            if ((_registers[0x0E] & 0x02) != 0)
            {
                value = _timerAOutputHigh ? (byte)(value | 0x40) : (byte)(value & 0xBF);
            }

            if ((_registers[0x0F] & 0x02) != 0)
            {
                value = _timerBOutputHigh ? (byte)(value | 0x80) : (byte)(value & 0x7F);
            }

            return value;
        }

        /// <summary>
        /// Sets the key state value.
        /// </summary>
        private void SetKeyState(Key key, bool pressed)
        {
            SetKeyState(key, pressed, _keyboardMatrix);
        }

        /// <summary>
        /// Sets a key state in the requested keyboard matrix layer.
        /// </summary>
        private void SetKeyState(Key key, bool pressed, bool[,] matrix)
        {
            MatrixKey matrixKey;
            if (SetShiftedFunctionKeyState(key, pressed, matrix))
            {
                return;
            }

            if (_keyMap.TryGetValue(key, out matrixKey))
            {
                matrix[matrixKey.Row, matrixKey.Column] = pressed;
            }
        }

        /// <summary>
        /// Maps PC F2/F4/F6/F8 to the shifted C64 function-key variants.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="pressed">True when the key is held.</param>
        /// <param name="matrix">Keyboard matrix layer to update.</param>
        /// <returns>True when the key was handled as a shifted function key.</returns>
        private bool SetShiftedFunctionKeyState(Key key, bool pressed, bool[,] matrix)
        {
            Key baseFunctionKey;
            switch (key)
            {
                case Key.F2:
                    baseFunctionKey = Key.F1;
                    break;
                case Key.F4:
                    baseFunctionKey = Key.F3;
                    break;
                case Key.F6:
                    baseFunctionKey = Key.F5;
                    break;
                case Key.F8:
                    baseFunctionKey = Key.F7;
                    break;
                default:
                    return false;
            }

            MatrixKey matrixKey;
            if (_keyMap.TryGetValue(baseFunctionKey, out matrixKey))
            {
                matrix[matrixKey.Row, matrixKey.Column] = pressed;
            }

            // C64 function keys F2/F4/F6/F8 are the shifted versions of
            // F1/F3/F5/F7. Use left shift (row 7, column 1) as the synthetic modifier.
            matrix[7, 1] = pressed;
            return true;
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
