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
    /// Emulates a MOS 6526 CIA peripheral used by the C64.
    /// </summary>
    public sealed class Cia2
    {
        private const int TodCyclesPerTenth = 98525;
        private const byte TimerStartDelayCycles = 4;
        private const byte TimerOutputEventNone = 0;
        private const byte TimerOutputEventPulse = 1;
        private const byte TimerOutputEventToggle = 2;
        private const byte OldCiaTimerBInterruptReadHazardTicks = 0;
        private const byte PortABitAtnOut = 0x08;
        private const byte PortABitClockOut = 0x10;
        private const byte PortABitDataOut = 0x20;
        private const byte PortABitClockIn = 0x40;
        private const byte PortABitDataIn = 0x80;

        private readonly CiaChipRevision _chipRevision;
        private readonly byte[] _registers = new byte[0x10];

        private IecBusPort _iecBusPort;
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
            : this(CiaChipRevision.Mos6526A)
        {
        }

        /// <summary>
        /// Initializes a new Cia2 instance.
        /// </summary>
        public Cia2(CiaChipRevision chipRevision)
        {
            _chipRevision = chipRevision;
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
            _timerAStartDelay = 0;
            _timerBStartDelay = 0;
            _timerAReloadHold = false;
            _timerBReloadHold = false;
            _timerAForceLoadedZero = false;
            _timerBForceLoadedZero = false;
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
            ReleaseDelayedInterruptLine();
            TickTimerOutputPulses();
            ApplyPendingTimerOutputEvents();
            bool timerAUnderflow = TickTimerA();
            TickTimerB(timerAUnderflow);
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
                    return ApplyTimerPortBOutputs(ReadPort(_registers[0x01], _registers[0x03], 0xFF));
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
                case 0x00:
                case 0x02:
                    UpdateIecOutputs(true);
                    break;
                case 0x04:
                    _timerALatch = (ushort)((_timerALatch & 0xFF00) | value);
                    ApplyTimerLowWriteToForceLoadedZero(ref _timerACounter, ref _timerAForceLoadedZero, ref _timerAReloadHold, _registers[0x0E], value);
                    break;
                case 0x05:
                    _timerALatch = (ushort)((_timerALatch & 0x00FF) | (value << 8));
                    _timerAForceLoadedZero = false;
                    if ((_registers[0x0E] & 0x01) == 0)
                    {
                        _timerACounter = _timerALatch;
                        _timerAReloadHold = false;
                    }
                    break;
                case 0x06:
                    _timerBLatch = (ushort)((_timerBLatch & 0xFF00) | value);
                    ApplyTimerLowWriteToForceLoadedZero(ref _timerBCounter, ref _timerBForceLoadedZero, ref _timerBReloadHold, _registers[0x0F], value);
                    break;
                case 0x07:
                    _timerBLatch = (ushort)((_timerBLatch & 0x00FF) | (value << 8));
                    _timerBForceLoadedZero = false;
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
                    if (timerAWasRunning && !timerAStarts && !timerAForceLoad)
                    {
                        TickTimerA(previousValue);
                    }

                    if ((previousValue & 0x01) == 0 && (value & 0x01) != 0)
                    {
                        bool resumesActiveCounter = (value & 0x10) == 0 && _timerACounter != _timerALatch;
                        _timerAStartDelay = resumesActiveCounter ? (byte)1 : (byte)((value & 0x10) != 0 ? TimerStartDelayCycles : 2);
                        if ((value & 0x10) == 0 && _timerALatch == 0 && _timerACounter == 0)
                        {
                            _timerAForceLoadedZero = true;
                        }
                    }

                    if ((value & 0x10) != 0)
                    {
                        _timerACounter = Cia6526TimerRules.ForceLoad(_timerALatch);
                        _timerAForceLoadedZero = (_timerALatch == 0 && (value & 0x01) != 0);
                        if (_timerAForceLoadedZero)
                        {
                            _interruptFlags |= 0x01;
                        }

                        _timerAReloadHold = false;
                        if (timerAWasRunning && timerAStarts)
                        {
                            _timerAStartDelay = 3;
                        }

                        _registers[0x0E] &= 0xEF;
                    }
                    break;
                case 0x0F:
                    bool timerBWasRunning = (previousValue & 0x01) != 0;
                    bool timerBStarts = (value & 0x01) != 0;
                    bool timerBForceLoad = (value & 0x10) != 0;
                    if (timerBWasRunning && !timerBStarts && !timerBForceLoad)
                    {
                        TickTimerB(previousValue, Cia6526TimerRules.TimerBCounts(previousValue, false));
                    }

                    if (timerBWasRunning &&
                        timerBStarts &&
                        !timerBForceLoad &&
                        Cia6526TimerRules.TimerBUsesSystemClock(previousValue) &&
                        !Cia6526TimerRules.TimerBUsesSystemClock(value))
                    {
                        TickTimerB(previousValue, true);
                    }

                    if ((previousValue & 0x01) == 0 && (value & 0x01) != 0)
                    {
                        bool resumesActiveCounter = (value & 0x10) == 0 && _timerBCounter != _timerBLatch;
                        _timerBStartDelay = resumesActiveCounter ? (byte)1 : (byte)((value & 0x10) != 0 ? TimerStartDelayCycles : 2);
                        if ((value & 0x10) == 0 && _timerBLatch == 0 && _timerBCounter == 0)
                        {
                            _timerBForceLoadedZero = true;
                        }
                    }

                    if ((value & 0x10) != 0)
                    {
                        _timerBCounter = Cia6526TimerRules.ForceLoad(_timerBLatch);
                        _timerBForceLoadedZero = (_timerBLatch == 0 && (value & 0x01) != 0);
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
        /// Returns whether nmi asserted is true.
        /// </summary>
        public bool IsNmiAsserted()
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
            bool showSummary = IsNmiAsserted() || (_interruptSummaryPending && hasFlags);
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
        /// Writes the complete CIA state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            StateSerializer.WriteObjectFields(writer, this, "_iecBusPort", "<BeforeIecPortAccess>k__BackingField");
        }

        /// <summary>
        /// Restores the complete CIA state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            StateSerializer.ReadObjectFields(reader, this, "_iecBusPort", "<BeforeIecPortAccess>k__BackingField");
            UpdateIecOutputs(false);
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

            bool underflow = Cia6526TimerRules.Tick(
                ref _timerACounter,
                _timerALatch,
                ref _timerAStartDelay,
                ref _timerAReloadHold,
                controlRegister,
                Cia6526TimerRules.TimerACounts(controlRegister),
                out bool stopOneShot);
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
            TickTimerB(_registers[0x0F], Cia6526TimerRules.TimerBCounts(_registers[0x0F], timerAUnderflow));
        }

        /// <summary>
        /// Advances timer B using the supplied control-register snapshot.
        /// </summary>
        private void TickTimerB(byte controlRegister, bool countPulse)
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
                Cia6526TimerRules.TimerBUsesTimerAUnderflows(controlRegister));
            if (!underflow)
            {
                return;
            }

            _timerBForceLoadedZero = false;
            RaiseTimerInterruptFlag(0x02);
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
