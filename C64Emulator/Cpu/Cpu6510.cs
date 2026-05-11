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
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Emulates the MOS 6510 CPU core and instruction execution state.
    /// </summary>
    public sealed class Cpu6510
    {
        private const ushort KernalSetLfsEntry = 0xFFBA;
        private const ushort KernalSetLfsRoutine = 0xFE00;
        private const ushort KernalSetNamEntry = 0xFFBD;
        private const ushort KernalSetNamRoutine = 0xFDF9;
        private const ushort KernalOpenEntry = 0xFFC0;
        private const ushort KernalCloseEntry = 0xFFC3;
        private const ushort KernalChkInEntry = 0xFFC6;
        private const ushort KernalChkOutEntry = 0xFFC9;
        private const ushort KernalClrChnEntry = 0xFFCC;
        private const ushort KernalChrInEntry = 0xFFCF;
        private const ushort KernalChrOutEntry = 0xFFD2;
        private const ushort KernalSecondEntry = 0xFF93;
        private const ushort KernalSecondRoutine = 0xEDB9;
        private const ushort KernalTalkSecondaryEntry = 0xFF96;
        private const ushort KernalTalkSecondaryRoutine = 0xEDC7;
        private const ushort KernalAcptrEntry = 0xFFA5;
        private const ushort KernalAcptrRoutine = 0xEE13;
        private const ushort KernalCioutEntry = 0xFFA8;
        private const ushort KernalCioutRoutine = 0xEDDD;
        private const ushort KernalUntalkEntry = 0xFFAB;
        private const ushort KernalUntalkRoutine = 0xEDEF;
        private const ushort KernalUnlistenEntry = 0xFFAE;
        private const ushort KernalUnlistenRoutine = 0xEDFE;
        private const ushort KernalListenEntry = 0xFFB1;
        private const ushort KernalListenRoutine = 0xED0C;
        private const ushort KernalTalkEntry = 0xFFB4;
        private const ushort KernalTalkRoutine = 0xED09;
        private const ushort KernalReadStatusEntry = 0xFFB7;
        private const ushort KernalReadStatusRoutine = 0xFE07;
        private const ushort KernalLoadEntry = 0xFFD5;
        private const ushort KernalLoadRoutine = 0xF49E;
        private const ushort KernalLoadSuccessPc = 0xF5D2;
        private const ushort FilenameLengthAddress = 0x00B7;
        private const ushort LogicalFileAddress = 0x00B8;
        private const ushort SecondaryAddressWord = 0x00B9;
        private const ushort DeviceNumberAddress = 0x00BA;
        private const ushort FilenamePointerAddress = 0x00BB;
        private const ushort StatusWordAddress = 0x0090;
        private readonly ICpuBus _bus;
        private readonly IecKernalBridge _iecKernalBridge;
        private readonly MediaManager _mediaManager;
        private bool _enableLoadHack = true;
        private bool _enableKernalIecHooks;
        private CpuState _state = CpuState.FetchOpcode;
        private InstructionStepper _currentStepper;
        private InstructionContext _context;

        private ushort _pc;
        private ushort _lastOpcodeAddress;
        private string _currentInstructionName;
        private ushort _interruptVector;
        private bool _interruptIsNmi;
        private bool _nmiPending;
        private bool _lastNmiLevel;
        private bool _soPending;
        private bool _skipIrqPollOnce;
        private bool _accessPredictionMode;
        private CpuTraceAccessType _predictedAccessType;
        private ushort _predictedAddress;
        private byte _predictedValue;
        private long _cpuCycleCount;
        private CpuTraceEntry _currentTraceEntry;
        private bool _traceEntryActive;
        private string _lastIecHookName = string.Empty;
        private bool _lastIecHookSuccess;
        private byte _a;
        private byte _x;
        private byte _y;
        private byte _sp;
        private byte _sr;

        /// <summary>
        /// Initializes a new Cpu6510 instance.
        /// </summary>
        public Cpu6510(ICpuBus bus, MediaManager mediaManager = null, IecKernalBridge iecKernalBridge = null)
        {
            _bus = bus;
            _mediaManager = mediaManager;
            _iecKernalBridge = iecKernalBridge;
            _enableKernalIecHooks = false;
            _sp = 0xFF;
        }

        public event Action<CpuTraceEntry> TraceEmitted;

        /// <summary>
        /// Gets or sets whether CPU tracing is enabled.
        /// </summary>
        public bool TraceEnabled { get; set; }

        public bool EnableKernalIecHooks
        {
            get { return _enableKernalIecHooks; }
            set { _enableKernalIecHooks = value && _iecKernalBridge != null; }
        }

        public bool EnableLoadHack
        {
            get { return _enableLoadHack; }
            set { _enableLoadHack = value; }
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            _cpuCycleCount++;
            BeginTraceCycle();

            if (_soPending)
            {
                SetFlag(0x40, true);
                _soPending = false;
            }

            bool nmiLevel = _bus.IsNmiAsserted();
            if (nmiLevel && !_lastNmiLevel)
            {
                _nmiPending = true;
            }

            _lastNmiLevel = nmiLevel;

            if (!_bus.CpuCanAccess)
            {
                EndTraceCycle();
                return;
            }

            if (_state == CpuState.FetchOpcode)
            {
                bool skipIrqPoll = _skipIrqPollOnce;
                _skipIrqPollOnce = false;

                if (_nmiPending)
                {
                    _nmiPending = false;
                    BeginInterrupt(0xFFFA, true);
                }
                else if (!skipIrqPoll && !_sr.HasFlag(0x04) && _bus.IsIrqAsserted())
                {
                    BeginInterrupt(0xFFFE, false);
                }
            }

            switch (_state)
            {
                case CpuState.FetchOpcode:
                    TickFetchOpcode();
                    break;
                case CpuState.ExecuteInstruction:
                    TickExecuteInstruction();
                    break;
                case CpuState.InterruptSequence:
                    TickInterruptSequence();
                    break;
            }

            EndTraceCycle();
        }

        /// <summary>
        /// Handles the predict next cycle access type operation.
        /// </summary>
        public CpuTraceAccessType PredictNextCycleAccessType()
        {
            return PredictNextCycleAccess().AccessType;
        }

        /// <summary>
        /// Predicts the next cycle access without committing CPU or bus state.
        /// </summary>
        public CpuBusAccessPrediction PredictNextCycleAccess()
        {
            if (_state == CpuState.Jammed)
            {
                return CpuBusAccessPrediction.None;
            }

            CpuState savedState = _state;
            InstructionStepper savedStepper = _currentStepper;
            InstructionContext savedContext = _context;
            ushort savedPc = _pc;
            ushort savedLastOpcodeAddress = _lastOpcodeAddress;
            string savedInstructionName = _currentInstructionName;
            ushort savedInterruptVector = _interruptVector;
            bool savedInterruptIsNmi = _interruptIsNmi;
            bool savedNmiPending = _nmiPending;
            bool savedLastNmiLevel = _lastNmiLevel;
            bool savedSoPending = _soPending;
            bool savedSkipIrqPollOnce = _skipIrqPollOnce;
            long savedCpuCycleCount = _cpuCycleCount;
            CpuTraceEntry savedTraceEntry = _currentTraceEntry;
            bool savedTraceEntryActive = _traceEntryActive;
            bool savedTraceEnabled = TraceEnabled;
            CpuTraceAccessType savedPredictedAccessType = _predictedAccessType;
            ushort savedPredictedAddress = _predictedAddress;
            byte savedPredictedValue = _predictedValue;
            byte savedA = _a;
            byte savedX = _x;
            byte savedY = _y;
            byte savedSp = _sp;
            byte savedSr = _sr;

            _accessPredictionMode = true;
            _predictedAccessType = CpuTraceAccessType.None;
            _predictedAddress = 0;
            _predictedValue = 0;
            TraceEnabled = false;

            try
            {
                if (_soPending)
                {
                    SetFlag(0x40, true);
                    _soPending = false;
                }

                bool nmiLevel = _bus.IsNmiAsserted();
                if (nmiLevel && !_lastNmiLevel)
                {
                    _nmiPending = true;
                }

                _lastNmiLevel = nmiLevel;

                if (_state == CpuState.FetchOpcode)
                {
                    bool skipIrqPoll = _skipIrqPollOnce;
                    _skipIrqPollOnce = false;

                    if (_nmiPending)
                    {
                        _nmiPending = false;
                        BeginInterrupt(0xFFFA, true);
                    }
                    else if (!skipIrqPoll && !_sr.HasFlag(0x04) && _bus.IsIrqAsserted())
                    {
                        BeginInterrupt(0xFFFE, false);
                    }
                }

                switch (_state)
                {
                    case CpuState.FetchOpcode:
                        TickFetchOpcode();
                        break;
                    case CpuState.ExecuteInstruction:
                        TickExecuteInstruction();
                        break;
                    case CpuState.InterruptSequence:
                        TickInterruptSequence();
                        break;
                }

                return new CpuBusAccessPrediction
                {
                    AccessType = _predictedAccessType,
                    Address = _predictedAddress,
                    Value = _predictedValue
                };
            }
            finally
            {
                _state = savedState;
                _currentStepper = savedStepper;
                _context = savedContext;
                _pc = savedPc;
                _lastOpcodeAddress = savedLastOpcodeAddress;
                _currentInstructionName = savedInstructionName;
                _interruptVector = savedInterruptVector;
                _interruptIsNmi = savedInterruptIsNmi;
                _nmiPending = savedNmiPending;
                _lastNmiLevel = savedLastNmiLevel;
                _soPending = savedSoPending;
                _skipIrqPollOnce = savedSkipIrqPollOnce;
                _cpuCycleCount = savedCpuCycleCount;
                _currentTraceEntry = savedTraceEntry;
                _traceEntryActive = savedTraceEntryActive;
                TraceEnabled = savedTraceEnabled;
                _predictedAccessType = savedPredictedAccessType;
                _predictedAddress = savedPredictedAddress;
                _predictedValue = savedPredictedValue;
                _a = savedA;
                _x = savedX;
                _y = savedY;
                _sp = savedSp;
                _sr = savedSr;
                _accessPredictionMode = false;
            }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset(ushort resetVector)
        {
            _pc = resetVector;
            _state = CpuState.FetchOpcode;
            _sp = 0xFD;
            _sr = 0x24;
            _interruptIsNmi = false;
            _nmiPending = false;
            _lastNmiLevel = false;
            _soPending = false;
            _skipIrqPollOnce = false;
            _cpuCycleCount = 0;
            _traceEntryActive = false;
        }

        /// <summary>
        /// Writes the complete CPU execution state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_enableKernalIecHooks);
            writer.Write((int)_state);
            WriteInstructionContext(writer, _context);
            writer.Write(_pc);
            writer.Write(_lastOpcodeAddress);
            BinaryStateIO.WriteString(writer, _currentInstructionName);
            writer.Write(_interruptVector);
            writer.Write(_interruptIsNmi);
            writer.Write(_nmiPending);
            writer.Write(_lastNmiLevel);
            writer.Write(_soPending);
            writer.Write(_skipIrqPollOnce);
            writer.Write(_cpuCycleCount);
            BinaryStateIO.WriteString(writer, _lastIecHookName);
            writer.Write(_lastIecHookSuccess);
            writer.Write(_a);
            writer.Write(_x);
            writer.Write(_y);
            writer.Write(_sp);
            writer.Write(_sr);
        }

        /// <summary>
        /// Restores the complete CPU execution state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            _enableKernalIecHooks = reader.ReadBoolean() && _iecKernalBridge != null;
            _state = (CpuState)reader.ReadInt32();
            _context = ReadInstructionContext(reader);
            _pc = reader.ReadUInt16();
            _lastOpcodeAddress = reader.ReadUInt16();
            _currentInstructionName = BinaryStateIO.ReadString(reader);
            _interruptVector = reader.ReadUInt16();
            _interruptIsNmi = reader.ReadBoolean();
            _nmiPending = reader.ReadBoolean();
            _lastNmiLevel = reader.ReadBoolean();
            _soPending = reader.ReadBoolean();
            _skipIrqPollOnce = reader.ReadBoolean();
            _cpuCycleCount = reader.ReadInt64();
            _lastIecHookName = BinaryStateIO.ReadString(reader) ?? string.Empty;
            _lastIecHookSuccess = reader.ReadBoolean();
            _a = reader.ReadByte();
            _x = reader.ReadByte();
            _y = reader.ReadByte();
            _sp = reader.ReadByte();
            _sr = reader.ReadByte();
            _currentStepper = _state == CpuState.ExecuteInstruction ? InstructionDecoder.Decode(_context.Opcode) : null;
            _accessPredictionMode = false;
            _predictedAccessType = CpuTraceAccessType.None;
            _currentTraceEntry = default(CpuTraceEntry);
            _traceEntryActive = false;
            TraceEnabled = false;
        }

        /// <summary>
        /// Handles the assert so operation.
        /// </summary>
        public void AssertSo()
        {
            _soPending = true;
        }

        /// <summary>
        /// Writes an instruction context into a savestate stream.
        /// </summary>
        private static void WriteInstructionContext(BinaryWriter writer, InstructionContext context)
        {
            writer.Write(context.Opcode);
            writer.Write(context.StepIndex);
            writer.Write(context.Address);
            writer.Write(context.Address2);
            writer.Write(context.Operand);
            writer.Write(context.Operand2);
            writer.Write(context.PageCrossed);
            writer.Write(context.BranchTaken);
        }

        /// <summary>
        /// Reads an instruction context from a savestate stream.
        /// </summary>
        private static InstructionContext ReadInstructionContext(BinaryReader reader)
        {
            return new InstructionContext
            {
                Opcode = reader.ReadByte(),
                StepIndex = reader.ReadInt32(),
                Address = reader.ReadUInt16(),
                Address2 = reader.ReadUInt16(),
                Operand = reader.ReadByte(),
                Operand2 = reader.ReadByte(),
                PageCrossed = reader.ReadBoolean(),
                BranchTaken = reader.ReadBoolean()
            };
        }

        /// <summary>
        /// Handles the read operation.
        /// </summary>
        public byte Read(ushort address)
        {
            return ReadWithTrace(address, CpuTraceAccessType.Read);
        }

        /// <summary>
        /// Handles the write operation.
        /// </summary>
        public void Write(ushort address, byte value)
        {
            WriteWithTrace(address, value, CpuTraceAccessType.Write);
        }

        /// <summary>
        /// Handles the dummy read operation.
        /// </summary>
        public byte DummyRead(ushort address)
        {
            return ReadWithTrace(address, CpuTraceAccessType.DummyRead);
        }

        /// <summary>
        /// Handles the delay interrupt poll operation.
        /// </summary>
        public void DelayInterruptPoll()
        {
            _skipIrqPollOnce = true;
        }

        /// <summary>
        /// Handles the jam operation.
        /// </summary>
        public void Jam()
        {
            _state = CpuState.Jammed;
        }

        /// <summary>
        /// Handles the push operation.
        /// </summary>
        public void Push(byte value)
        {
            Write((ushort)(0x0100 + _sp), value);
            _sp--;
        }

        /// <summary>
        /// Handles the pull operation.
        /// </summary>
        public byte Pull()
        {
            _sp++;
            return Read((ushort)(0x0100 + _sp));
        }

        /// <summary>
        /// Handles the compare operation.
        /// </summary>
        public void Compare(byte left, byte right)
        {
            var result = left - right;
            SetFlag(0x01, left >= right);
            SetFlag(0x02, (result & 0xFF) == 0);
            SetFlag(0x80, (result & 0x80) != 0);
        }

        /// <summary>
        /// Handles the and operation.
        /// </summary>
        public void And(byte value)
        {
            _a = (byte)(_a & value);
            SetNZ(_a);
        }

        /// <summary>
        /// Handles the ora operation.
        /// </summary>
        public void Ora(byte value)
        {
            _a = (byte)(_a | value);
            SetNZ(_a);
        }

        /// <summary>
        /// Handles the eor operation.
        /// </summary>
        public void Eor(byte value)
        {
            _a = (byte)(_a ^ value);
            SetNZ(_a);
        }

        /// <summary>
        /// Handles the adc operation.
        /// </summary>
        public void Adc(byte value)
        {
            int carryIn = GetFlag(0x01) ? 1 : 0;
            int sum = _a + value + carryIn;
            byte binaryResult = (byte)sum;

            SetFlag(0x40, (~(_a ^ value) & (_a ^ binaryResult) & 0x80) != 0);

            if (GetFlag(0x08))
            {
                int low = (_a & 0x0F) + (value & 0x0F) + carryIn;
                int high = (_a & 0xF0) + (value & 0xF0);

                if (low > 0x09)
                {
                    low += 0x06;
                    high += 0x10;
                }

                if ((high & 0x1F0) > 0x90)
                {
                    high += 0x60;
                }

                SetFlag(0x01, high > 0xF0);
                _a = (byte)((high & 0xF0) | (low & 0x0F));
                SetNZ(_a);
                return;
            }

            SetFlag(0x01, sum > 0xFF);
            _a = binaryResult;
            SetNZ(_a);
        }

        /// <summary>
        /// Handles the sbc operation.
        /// </summary>
        public void Sbc(byte value)
        {
            int borrow = GetFlag(0x01) ? 0 : 1;
            int difference = _a - value - borrow;
            byte binaryResult = (byte)difference;

            SetFlag(0x40, ((_a ^ value) & (_a ^ binaryResult) & 0x80) != 0);

            if (GetFlag(0x08))
            {
                int low = (_a & 0x0F) - (value & 0x0F) - borrow;
                int high = (_a >> 4) - (value >> 4);

                if (low < 0)
                {
                    low -= 0x06;
                    high--;
                }

                if (high < 0)
                {
                    high -= 0x06;
                }

                SetFlag(0x01, difference >= 0);
                _a = (byte)(((high << 4) & 0xF0) | (low & 0x0F));
                SetNZ(_a);
                return;
            }

            SetFlag(0x01, difference >= 0);
            _a = binaryResult;
            SetNZ(_a);
        }

        /// <summary>
        /// Handles the asl operation.
        /// </summary>
        public byte Asl(byte value)
        {
            SetFlag(0x01, (value & 0x80) != 0);
            value = (byte)(value << 1);
            SetNZ(value);
            return value;
        }

        /// <summary>
        /// Handles the lsr operation.
        /// </summary>
        public byte Lsr(byte value)
        {
            SetFlag(0x01, (value & 0x01) != 0);
            value = (byte)(value >> 1);
            SetNZ(value);
            return value;
        }

        /// <summary>
        /// Handles the rol operation.
        /// </summary>
        public byte Rol(byte value)
        {
            int carryIn = GetFlag(0x01) ? 1 : 0;
            SetFlag(0x01, (value & 0x80) != 0);
            value = (byte)((value << 1) | carryIn);
            SetNZ(value);
            return value;
        }

        /// <summary>
        /// Handles the ror operation.
        /// </summary>
        public byte Ror(byte value)
        {
            int carryIn = GetFlag(0x01) ? 0x80 : 0;
            SetFlag(0x01, (value & 0x01) != 0);
            value = (byte)((value >> 1) | carryIn);
            SetNZ(value);
            return value;
        }

        /// <summary>
        /// Handles the inc operation.
        /// </summary>
        public byte Inc(byte value)
        {
            value++;
            SetNZ(value);
            return value;
        }

        /// <summary>
        /// Handles the dec operation.
        /// </summary>
        public byte Dec(byte value)
        {
            value--;
            SetNZ(value);
            return value;
        }

        /// <summary>
        /// Handles the bit operation.
        /// </summary>
        public void Bit(byte value)
        {
            SetFlag(0x02, (_a & value) == 0);
            SetFlag(0x40, (value & 0x40) != 0);
            SetFlag(0x80, (value & 0x80) != 0);
        }

        /// <summary>
        /// Handles the branch relative operation.
        /// </summary>
        public void BranchRelative(sbyte offset)
        {
            _pc = (ushort)(_pc + offset);
        }

        /// <summary>
        /// Sets the nz value.
        /// </summary>
        public void SetNZ(byte value)
        {
            SetFlag(0x02, value == 0);
            SetFlag(0x80, (value & 0x80) != 0);
        }

        /// <summary>
        /// Gets the flag value.
        /// </summary>
        public bool GetFlag(byte mask)
        {
            return (_sr & mask) != 0;
        }

        /// <summary>
        /// Sets the flag value.
        /// </summary>
        public void SetFlag(byte mask, bool value)
        {
            if (value)
            {
                _sr |= mask;
            }
            else
            {
                _sr &= (byte)~mask;
            }
        }

        public ushort PC
        {
            get { return _pc; }
            set { _pc = value; }
        }

        public byte A
        {
            get { return _a; }
            set { _a = value; }
        }

        public byte X
        {
            get { return _x; }
            set { _x = value; }
        }

        public byte Y
        {
            get { return _y; }
            set { _y = value; }
        }

        public byte SP
        {
            get { return _sp; }
            set { _sp = value; }
        }

        public byte SR
        {
            get { return _sr; }
            set { _sr = value; }
        }

        public CpuState State
        {
            get { return _state; }
        }

        public byte CurrentOpcode
        {
            get { return _context.Opcode; }
        }

        public ushort LastOpcodeAddress
        {
            get { return _lastOpcodeAddress; }
        }

        public string CurrentInstructionName
        {
            get { return _currentInstructionName; }
        }

        public long CpuCycleCount
        {
            get { return _cpuCycleCount; }
        }

        /// <summary>
        /// Starts execution at the given address on the next opcode fetch.
        /// </summary>
        public void StartAt(ushort address)
        {
            _pc = address;
            _lastOpcodeAddress = address;
            _state = CpuState.FetchOpcode;
            _currentStepper = null;
            _context.Reset(0x00);
            _currentInstructionName = string.Empty;
            _skipIrqPollOnce = true;
        }

        public string LastIecHookDebug
        {
            get { return _lastIecHookName + ":" + (_lastIecHookSuccess ? "OK" : "FAIL"); }
        }

        /// <summary>
        /// Advances the fetch opcode state by one emulated tick.
        /// </summary>
        private void TickFetchOpcode()
        {
            if (TryHandleKernalIecBridge())
            {
                return;
            }

            if (TryHandleLoadHack())
            {
                return;
            }

            _lastOpcodeAddress = _pc;
            var opcode = ReadWithTrace(_pc++, CpuTraceAccessType.OpcodeFetch);
            _context.Reset(opcode);
            _currentStepper = InstructionDecoder.Decode(opcode);
            _currentInstructionName = TraceEnabled ? _currentStepper.Method.Name : string.Empty;
            _state = CpuState.ExecuteInstruction;
        }

        /// <summary>
        /// Advances the execute instruction state by one emulated tick.
        /// </summary>
        private void TickExecuteInstruction()
        {
            if (_currentStepper == null)
            {
                _state = CpuState.FetchOpcode;
                return;
            }

            var done = _currentStepper(this, ref _context);
            if (done && _state != CpuState.Jammed)
            {
                _state = CpuState.FetchOpcode;
            }
        }

        /// <summary>
        /// Advances the interrupt sequence state by one emulated tick.
        /// </summary>
        private void TickInterruptSequence()
        {
            switch (_context.StepIndex)
            {
                case 0:
                    DummyRead(_pc);
                    _context.StepIndex++;
                    return;
                case 1:
                    DummyRead(_pc);
                    _context.StepIndex++;
                    return;
                case 2:
                    Push((byte)((_pc >> 8) & 0xFF));
                    _context.StepIndex++;
                    return;
                case 3:
                    Push((byte)(_pc & 0xFF));
                    _context.StepIndex++;
                    return;
                case 4:
                    Push((byte)((_sr | 0x20) & (_interruptIsNmi ? 0xEF : 0xEF)));
                    SetFlag(0x04, true);
                    _context.StepIndex++;
                    return;
                case 5:
                    _context.Address = Read(_interruptVector);
                    _context.StepIndex++;
                    return;
                case 6:
                    _context.Address |= (ushort)(Read((ushort)(_interruptVector + 1)) << 8);
                    _pc = _context.Address;
                    _state = CpuState.FetchOpcode;
                    _currentInstructionName = _interruptIsNmi ? "NMI" : "IRQ";
                    return;
                default:
                    _state = CpuState.FetchOpcode;
                    return;
            }
        }

        /// <summary>
        /// Begins interrupt.
        /// </summary>
        private void BeginInterrupt(ushort vector, bool isNmi)
        {
            _interruptVector = vector;
            _interruptIsNmi = isNmi;
            _context.Reset(0x00);
            _currentStepper = null;
            _currentInstructionName = isNmi ? "NMI" : "IRQ";
            _state = CpuState.InterruptSequence;
        }

        /// <summary>
        /// Attempts to handle load hack and reports whether it succeeded.
        /// </summary>
        private bool TryHandleLoadHack()
        {
            if (!_enableLoadHack || _accessPredictionMode)
            {
                return false;
            }

            byte deviceNumber = Read(DeviceNumberAddress);
            if (deviceNumber >= 8 && deviceNumber <= 11)
            {
                return false;
            }

            if (_pc != KernalLoadEntry)
            {
                return false;
            }

            string filename = ReadFilenameFromKernalBuffer();
            if (string.IsNullOrWhiteSpace(filename))
            {
                return false;
            }

            MediaLoadData mediaLoadData;
            if (_mediaManager != null && _mediaManager.TryResolveLoad(filename, out mediaLoadData))
            {
                return CompleteLoadHack(mediaLoadData.ProgramBytes, mediaLoadData.Name);
            }

            string resolvedPath = ResolveProgramPath(filename);
            if (resolvedPath == null)
            {
                return false;
            }

            byte[] fileBytes = File.ReadAllBytes(resolvedPath);
            return CompleteLoadHack(fileBytes, Path.GetFileName(resolvedPath));
        }

        /// <summary>
        /// Reads filename from kernal buffer.
        /// </summary>
        private string ReadFilenameFromKernalBuffer()
        {
            int filenameLength = Read(FilenameLengthAddress);
            if (filenameLength <= 0)
            {
                return string.Empty;
            }

            ushort filenameAddress = (ushort)(Read(FilenamePointerAddress) | (Read((ushort)(FilenamePointerAddress + 1)) << 8));
            byte[] filenameBytes = new byte[filenameLength];
            for (int index = 0; index < filenameLength; index++)
            {
                filenameBytes[index] = Read((ushort)(filenameAddress + index));
            }

            return Encoding.ASCII.GetString(filenameBytes).Trim().Trim('"');
        }

        /// <summary>
        /// Handles the resolve program path operation.
        /// </summary>
        private static string ResolveProgramPath(string filename)
        {
            string cleaned = filename.Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }

            var directories = new List<string>();
            AddIfMissing(directories, Directory.GetCurrentDirectory());
            AddIfMissing(directories, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            AddIfMissing(directories, Path.GetDirectoryName(Directory.GetCurrentDirectory()));

            var candidateNames = new List<string>();
            AddIfMissing(candidateNames, cleaned);
            if (!cleaned.EndsWith(".prg", System.StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(candidateNames, cleaned + ".prg");
            }

            foreach (string directory in directories)
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                foreach (string candidateName in candidateNames)
                {
                    string directPath = Path.Combine(directory, candidateName);
                    if (File.Exists(directPath))
                    {
                        return directPath;
                    }

                    foreach (string file in files)
                    {
                        if (string.Equals(Path.GetFileName(file), candidateName, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Handles the add if missing operation.
        /// </summary>
        private static void AddIfMissing(List<string> values, string value)
        {
            if (!string.IsNullOrEmpty(value) && !values.Contains(value))
            {
                values.Add(value);
            }
        }

        /// <summary>
        /// Handles the complete load hack operation.
        /// </summary>
        private bool CompleteLoadHack(byte[] fileBytes, string sourceName)
        {
            SystemBus systemBus = _bus as SystemBus;
            if (systemBus == null)
            {
                return false;
            }

            ushort loadAddress;
            ushort endAddress;
            ushort targetAddress = Read(SecondaryAddressWord) == 0
                ? (ushort)(X | (Y << 8))
                : PrgLoader.GetFileLoadAddress(fileBytes);
            if (!PrgLoader.TryLoadIntoMemory(systemBus, fileBytes, targetAddress, out loadAddress, out endAddress))
            {
                return false;
            }

            A = 0x00;
            X = (byte)(endAddress & 0xFF);
            Y = (byte)(endAddress >> 8);
            SetFlag(0x01, false);
            Write(StatusWordAddress, 0x00);

            _lastOpcodeAddress = _pc;
            _currentInstructionName = "LoadHack:" + (sourceName ?? string.Empty);
            _state = CpuState.FetchOpcode;
            _currentStepper = null;
            _context.Reset(0x00);
            _pc = KernalLoadSuccessPc;
            return true;
        }

        /// <summary>
        /// Attempts to handle kernal iec bridge and reports whether it succeeded.
        /// </summary>
        private bool TryHandleKernalIecBridge()
        {
            if (!_enableKernalIecHooks)
            {
                return false;
            }

            if (_accessPredictionMode || _iecKernalBridge == null || !_iecKernalBridge.IsActive)
            {
                return false;
            }

            if (ShouldBypassKernalIecHook())
            {
                return false;
            }

            switch (_pc)
            {
                case KernalSetLfsEntry:
                case KernalSetLfsRoutine:
                    if (X < 8 || X > 11)
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("SETLFS", true, true, () =>
                    {
                        _iecKernalBridge.SetLfs(A, X, Y);
                        Write(LogicalFileAddress, A);
                        Write(SecondaryAddressWord, Y);
                        Write(DeviceNumberAddress, X);
                    });
                case KernalSetNamEntry:
                case KernalSetNamRoutine:
                    if (!_iecKernalBridge.IsCurrentDeviceSupported)
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("SETNAM", true, true, () =>
                    {
                        string filename = ReadFilename(X, Y, A);
                        _iecKernalBridge.SetNam(filename);
                        Write(FilenameLengthAddress, A);
                        Write(FilenamePointerAddress, X);
                        Write((ushort)(FilenamePointerAddress + 1), Y);
                    });
                case KernalOpenEntry:
                    if (!_iecKernalBridge.IsCurrentDeviceSupported)
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("OPEN", true, _iecKernalBridge.Open());
                case KernalCloseEntry:
                    if (!_iecKernalBridge.IsLogicalFileOpen(A))
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("CLOSE", true, _iecKernalBridge.Close(A));
                case KernalChkInEntry:
                    if (!_iecKernalBridge.IsLogicalFileOpen(X))
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("CHKIN", true, _iecKernalBridge.ChkIn(X));
                case KernalChkOutEntry:
                    if (!_iecKernalBridge.IsLogicalFileOpen(X))
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("CHKOUT", true, _iecKernalBridge.ChkOut(X));
                case KernalClrChnEntry:
                    if (!_iecKernalBridge.HasAnyOpenChannels &&
                        !_iecKernalBridge.HasActiveInputChannel &&
                        !_iecKernalBridge.HasActiveOutputChannel)
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("CLRCHN", true, _iecKernalBridge.ClrChn());
                case KernalChrInEntry:
                    if (!_iecKernalBridge.HasActiveInputChannel)
                    {
                        return false;
                    }
                    byte chrInByte;
                    if (!_iecKernalBridge.ChrIn(out chrInByte))
                    {
                        _lastIecHookName = "CHRIN";
                        _lastIecHookSuccess = false;
                        return false;
                    }

                    return CompleteKernalBridgeCallResult("CHRIN", true, true, () => A = chrInByte);
                case KernalChrOutEntry:
                    if (!_iecKernalBridge.HasActiveOutputChannel)
                    {
                        return false;
                    }
                    return CompleteKernalBridgeCallResult("CHROUT", true, _iecKernalBridge.ChrOut(A));
                case KernalListenEntry:
                case KernalListenRoutine:
                    return CompleteKernalBridgeCallResult("LISTEN", true, _iecKernalBridge.Listen(A));
                case KernalTalkEntry:
                case KernalTalkRoutine:
                    return CompleteKernalBridgeCallResult("TALK", true, _iecKernalBridge.Talk(A));
                case KernalSecondEntry:
                case KernalSecondRoutine:
                    return CompleteKernalBridgeCallResult("SECOND", true, _iecKernalBridge.Secondary(A));
                case KernalTalkSecondaryEntry:
                case KernalTalkSecondaryRoutine:
                    return CompleteKernalBridgeCallResult("TKSA", true, _iecKernalBridge.TalkSecondary(A));
                case KernalCioutEntry:
                case KernalCioutRoutine:
                    return CompleteKernalBridgeCallResult("CIOUT", true, _iecKernalBridge.CiOut(A));
                case KernalAcptrEntry:
                case KernalAcptrRoutine:
                    byte acptrByte;
                    if (!_iecKernalBridge.AcPtr(out acptrByte))
                    {
                        _lastIecHookName = "ACPTR";
                        _lastIecHookSuccess = false;
                        return false;
                    }

                    return CompleteKernalBridgeCallResult("ACPTR", true, true, () => A = acptrByte);
                case KernalUntalkEntry:
                case KernalUntalkRoutine:
                    return CompleteKernalBridgeCallResult("UNTALK", true, _iecKernalBridge.Untalk());
                case KernalUnlistenEntry:
                case KernalUnlistenRoutine:
                    return CompleteKernalBridgeCallResult("UNLISTN", true, _iecKernalBridge.Unlisten());
                case KernalReadStatusEntry:
                case KernalReadStatusRoutine:
                    return CompleteKernalBridgeCallResult("READST", true, true, () => A = _iecKernalBridge.Status);
                case KernalLoadEntry:
                case KernalLoadRoutine:
                    return TryHandleKernalBridgeLoad();
                default:
                    return false;
            }
        }

        /// <summary>
        /// Attempts to handle kernal bridge load and reports whether it succeeded.
        /// </summary>
        private bool TryHandleKernalBridgeLoad()
        {
            byte device = ReadDeviceNumberForIecHookBypass();
            if (device < 8 || device > 11)
            {
                return false;
            }

            byte[] programBytes;
            if (!_iecKernalBridge.Load(out programBytes) || programBytes == null || programBytes.Length < 2)
            {
                _lastIecHookName = "LOAD";
                _lastIecHookSuccess = false;
                Write(StatusWordAddress, _iecKernalBridge.Status);
                SetFlag(0x01, true);
                ReturnFromKernalRoutine();
                return true;
            }

            SystemBus systemBus = _bus as SystemBus;
            if (systemBus == null)
            {
                return false;
            }

            ushort loadAddress;
            ushort endAddress;
            ushort targetAddress = _iecKernalBridge.CurrentSecondaryAddress == 0
                ? (ushort)(X | (Y << 8))
                : PrgLoader.GetFileLoadAddress(programBytes);
            if (!PrgLoader.TryLoadIntoMemory(systemBus, programBytes, targetAddress, out loadAddress, out endAddress))
            {
                _lastIecHookName = "LOAD";
                _lastIecHookSuccess = false;
                Write(StatusWordAddress, 0x04);
                SetFlag(0x01, true);
                ReturnFromKernalRoutine();
                return true;
            }

            _lastIecHookName = "LOAD";
            _lastIecHookSuccess = true;
            A = 0x00;
            X = (byte)(endAddress & 0xFF);
            Y = (byte)(endAddress >> 8);
            Write(StatusWordAddress, _iecKernalBridge.Status);
            NormalizeIecBridgeBusState();
            SetFlag(0x01, false);
            ReturnFromKernalRoutine();
            return true;
        }

        /// <summary>
        /// Handles the should bypass kernal iec hook operation.
        /// </summary>
        private bool ShouldBypassKernalIecHook()
        {
            byte device = ReadDeviceNumberForIecHookBypass();
            switch (_pc)
            {
                case KernalListenEntry:
                case KernalListenRoutine:
                case KernalTalkEntry:
                case KernalTalkRoutine:
                    device = A;
                    break;
            }

            if (device < 8 || device > 11)
            {
                return false;
            }

            return _iecKernalBridge.ShouldBypassLowLevelHooks(device);
        }

        /// <summary>
        /// Reads device number for iec hook bypass.
        /// </summary>
        private byte ReadDeviceNumberForIecHookBypass()
        {
            SystemBus systemBus = _bus as SystemBus;
            if (systemBus != null)
            {
                return systemBus.CpuRead(DeviceNumberAddress);
            }

            return _bus.CpuRead(DeviceNumberAddress);
        }

        /// <summary>
        /// Handles the complete kernal bridge call result operation.
        /// </summary>
        private bool CompleteKernalBridgeCallResult(string hookName, bool handled, bool success, Action onSuccess = null)
        {
            _lastIecHookName = hookName ?? string.Empty;
            _lastIecHookSuccess = success;
            if (!handled)
            {
                return false;
            }

            Write(StatusWordAddress, _iecKernalBridge.Status);
            if (success && onSuccess != null)
            {
                onSuccess();
            }

            NormalizeIecBridgeBusState();

            SetFlag(0x01, !success);
            ReturnFromKernalRoutine();
            return true;
        }

        /// <summary>
        /// Reads filename.
        /// </summary>
        private string ReadFilename(byte low, byte high, byte length)
        {
            if (length == 0)
            {
                return string.Empty;
            }

            ushort address = (ushort)(low | (high << 8));
            byte[] bytes = new byte[length];
            for (int index = 0; index < length; index++)
            {
                bytes[index] = Read((ushort)(address + index));
            }

            return Encoding.ASCII.GetString(bytes).Trim().Trim('"');
        }

        /// <summary>
        /// Handles the normalize iec bridge bus state operation.
        /// </summary>
        private void NormalizeIecBridgeBusState()
        {
            const ushort Cia2PortAAddress = 0xDD00;
            const byte IecOutputMask = 0x38;

            byte current = Read(Cia2PortAAddress);
            Write(Cia2PortAAddress, (byte)(current | IecOutputMask));
        }

        /// <summary>
        /// Handles the return from kernal routine operation.
        /// </summary>
        private void ReturnFromKernalRoutine()
        {
            _sp++;
            byte low = _bus.CpuRead((ushort)(0x0100 + _sp));
            _sp++;
            byte high = _bus.CpuRead((ushort)(0x0100 + _sp));
            _lastOpcodeAddress = _pc;
            _currentInstructionName = "IEC";
            _state = CpuState.FetchOpcode;
            _currentStepper = null;
            _context.Reset(0x00);
            _pc = (ushort)(((high << 8) | low) + 1);
        }

        /// <summary>
        /// Reads with trace.
        /// </summary>
        private byte ReadWithTrace(ushort address, CpuTraceAccessType accessType)
        {
            if (_accessPredictionMode)
            {
                byte predictedValue = PeekForPrediction(address);
                RecordPredictedAccess(accessType, address, predictedValue);
                return predictedValue;
            }

            byte value = _bus.CpuRead(address);
            RecordTraceAccess(accessType, address, value);
            return value;
        }

        /// <summary>
        /// Writes with trace.
        /// </summary>
        private void WriteWithTrace(ushort address, byte value, CpuTraceAccessType accessType)
        {
            if (_accessPredictionMode)
            {
                RecordPredictedAccess(accessType, address, value);
                return;
            }

            _bus.CpuWrite(address, value);
            RecordTraceAccess(accessType, address, value);
        }

        /// <summary>
        /// Records the first bus access observed during prediction.
        /// </summary>
        private void RecordPredictedAccess(CpuTraceAccessType accessType, ushort address, byte value)
        {
            if (_predictedAccessType != CpuTraceAccessType.None)
            {
                return;
            }

            _predictedAccessType = accessType;
            _predictedAddress = address;
            _predictedValue = value;
        }

        /// <summary>
        /// Reads CPU-visible memory for prediction without I/O side effects.
        /// </summary>
        private byte PeekForPrediction(ushort address)
        {
            var systemBus = _bus as SystemBus;
            if (systemBus != null)
            {
                return systemBus.PeekCpuRead(address);
            }

            return 0;
        }

        /// <summary>
        /// Begins trace cycle.
        /// </summary>
        private void BeginTraceCycle()
        {
            if (!TraceEnabled)
            {
                _traceEntryActive = false;
                return;
            }

            _currentTraceEntry = new CpuTraceEntry
            {
                Cycle = _cpuCycleCount,
                StateBefore = _state,
                InstructionName = _currentInstructionName,
                Opcode = _context.Opcode,
                LastOpcodeAddress = _lastOpcodeAddress,
                StepIndexBefore = _context.StepIndex,
                PcBefore = _pc,
                ABefore = _a,
                XBefore = _x,
                YBefore = _y,
                SpBefore = _sp,
                SrBefore = _sr,
                AccessType = CpuTraceAccessType.None
            };
            _traceEntryActive = true;
        }

        /// <summary>
        /// Ends trace cycle.
        /// </summary>
        private void EndTraceCycle()
        {
            if (!TraceEnabled || !_traceEntryActive)
            {
                return;
            }

            _currentTraceEntry.StateAfter = _state;
            _currentTraceEntry.InstructionName = _currentInstructionName;
            _currentTraceEntry.Opcode = _context.Opcode;
            _currentTraceEntry.LastOpcodeAddress = _lastOpcodeAddress;
            _currentTraceEntry.StepIndexAfter = _context.StepIndex;
            _currentTraceEntry.PcAfter = _pc;
            _currentTraceEntry.AAfter = _a;
            _currentTraceEntry.XAfter = _x;
            _currentTraceEntry.YAfter = _y;
            _currentTraceEntry.SpAfter = _sp;
            _currentTraceEntry.SrAfter = _sr;

            var handler = TraceEmitted;
            if (handler != null)
            {
                handler(_currentTraceEntry);
            }

            _traceEntryActive = false;
        }

        /// <summary>
        /// Handles the record trace access operation.
        /// </summary>
        private void RecordTraceAccess(CpuTraceAccessType accessType, ushort address, byte value)
        {
            if (!TraceEnabled || !_traceEntryActive)
            {
                return;
            }

            _currentTraceEntry.AccessType = accessType;
            _currentTraceEntry.Address = address;
            _currentTraceEntry.Value = value;
        }
    }

    /// <summary>
    /// Represents the byte extensions component.
    /// </summary>
    internal static class ByteExtensions
    {
        /// <summary>
        /// Returns whether flag is available or active.
        /// </summary>
        public static bool HasFlag(this byte value, byte mask)
        {
            return (value & mask) != 0;
        }
    }
}
