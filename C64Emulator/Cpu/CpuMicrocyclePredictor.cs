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
    /// Predicts the next 6510 bus access from the current instruction microcycle.
    /// </summary>
    internal static class CpuMicrocyclePredictor
    {
        // The predictor is intentionally table-shaped instead of executing any opcode
        // logic.  VIC-II arbitration needs to know the next CPU bus access before the
        // CPU tick runs; prediction must therefore be side-effect free and only read
        // already-latched instruction context plus peeked memory values.
        private enum AccessPattern
        {
            None,
            ImmediateRead,
            ZeroPageRead,
            ZeroPageWrite,
            ZeroPageIndexedReadX,
            ZeroPageIndexedReadY,
            ZeroPageIndexedWriteX,
            ZeroPageIndexedWriteY,
            AbsoluteRead,
            AbsoluteWrite,
            AbsoluteIndexedReadX,
            AbsoluteIndexedReadY,
            AbsoluteIndexedWriteX,
            AbsoluteIndexedWriteY,
            IndirectZeroPageXRead,
            IndirectZeroPageXWrite,
            IndirectZeroPageXModify,
            IndirectZeroPageYRead,
            IndirectZeroPageYWrite,
            IndirectZeroPageYModify,
            ZeroPageModify,
            ZeroPageIndexedModifyX,
            ZeroPageIndexedModifyY,
            AbsoluteModify,
            AbsoluteIndexedModifyX,
            AbsoluteIndexedModifyY
        }

        public static bool TryPredictNextAccess(Cpu6510 cpu, out CpuBusAccessPrediction prediction)
        {
            prediction = CpuBusAccessPrediction.None;
            switch (cpu.State)
            {
                case CpuState.FetchOpcode:
                    prediction = Read(cpu, CpuTraceAccessType.OpcodeFetch, cpu.PC);
                    return true;
                case CpuState.InterruptSequence:
                    return TryPredictInterruptAccess(cpu, out prediction);
                case CpuState.ExecuteInstruction:
                    return TryPredictInstructionAccess(cpu, out prediction);
                default:
                    return true;
            }
        }

        private static bool TryPredictInterruptAccess(Cpu6510 cpu, out CpuBusAccessPrediction prediction)
        {
            InstructionContext context = cpu.CurrentInstructionContext;
            // IRQ/NMI uses the same external bus sequence as BRK except that the
            // status byte has the break flag cleared.  The CPU core owns PC/SR/SP
            // mutation; this method only mirrors the visible reads and writes.
            switch (context.StepIndex)
            {
                case 0:
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                case 2:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)((cpu.PC >> 8) & 0xFF));
                    return true;
                case 3:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)(cpu.PC & 0xFF));
                    return true;
                case 4:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)((cpu.SR | 0x20) & 0xEF));
                    return true;
                case 5:
                    prediction = Read(cpu, CpuTraceAccessType.Read, 0xFFFE);
                    return true;
                case 6:
                    prediction = Read(cpu, CpuTraceAccessType.Read, 0xFFFF);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictInstructionAccess(Cpu6510 cpu, out CpuBusAccessPrediction prediction)
        {
            InstructionContext context = cpu.CurrentInstructionContext;
            byte opcode = context.Opcode;

            if (TryPredictSpecialInstruction(cpu, context, out prediction))
            {
                return true;
            }

            AccessPattern pattern;
            byte writeValue;
            if (!TryGetPattern(cpu, opcode, out pattern, out writeValue))
            {
                prediction = CpuBusAccessPrediction.None;
                return false;
            }

            // Most 6502/6510 opcodes collapse to a small set of addressing-mode
            // bus patterns.  Special control-flow and stack instructions are handled
            // above because their cycles do not fit the compact mode tables.
            return TryPredictPattern(cpu, context, pattern, writeValue, out prediction);
        }

        private static bool TryPredictSpecialInstruction(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.Opcode)
            {
                case 0x00:
                    return TryPredictBrk(cpu, context, out prediction);
                case 0x20:
                    return TryPredictJsr(cpu, context, out prediction);
                case 0x40:
                    return TryPredictRti(cpu, context, out prediction);
                case 0x60:
                    return TryPredictRts(cpu, context, out prediction);
                case 0x4C:
                    return TryPredictJmpAbsolute(cpu, context, out prediction);
                case 0x6C:
                    return TryPredictJmpIndirect(cpu, context, out prediction);
                case 0x0B:
                case 0x2B:
                case 0x4B:
                case 0x6B:
                case 0x8B:
                case 0xAB:
                case 0xCB:
                case 0xEB:
                    return PredictImmediateRead(cpu, context, out prediction);
                case 0x08:
                    return TryPredictPhp(cpu, context, out prediction);
                case 0x28:
                    return TryPredictPlp(cpu, context, out prediction);
                case 0x48:
                    return TryPredictPha(cpu, context, out prediction);
                case 0x68:
                    return TryPredictPla(cpu, context, out prediction);
                case 0x10:
                case 0x30:
                case 0x50:
                case 0x70:
                case 0x90:
                case 0xB0:
                case 0xD0:
                case 0xF0:
                    return TryPredictBranch(cpu, context, out prediction);
                default:
                    if (IsImpliedOrAccumulator(context.Opcode))
                    {
                        prediction = CpuBusAccessPrediction.None;
                        return true;
                    }

                    prediction = CpuBusAccessPrediction.None;
                    return false;
            }
        }

        private static bool TryGetPattern(Cpu6510 cpu, byte opcode, out AccessPattern pattern, out byte writeValue)
        {
            pattern = AccessPattern.None;
            writeValue = 0;

            // A handful of opcodes sit outside the regular cc/aaa/bbb decode grid
            // or need register-specific store values.  Resolve those explicitly
            // before falling back to the grouped opcode layout.
            switch (opcode)
            {
                case 0x24:
                case 0xC4:
                case 0xE4:
                    pattern = AccessPattern.ZeroPageRead;
                    return true;
                case 0x2C:
                case 0xCC:
                case 0xEC:
                    pattern = AccessPattern.AbsoluteRead;
                    return true;
                case 0x84:
                    pattern = AccessPattern.ZeroPageWrite;
                    writeValue = cpu.Y;
                    return true;
                case 0x94:
                    pattern = AccessPattern.ZeroPageIndexedWriteX;
                    writeValue = cpu.Y;
                    return true;
                case 0x8C:
                    pattern = AccessPattern.AbsoluteWrite;
                    writeValue = cpu.Y;
                    return true;
                case 0xA0:
                case 0xA2:
                case 0xC0:
                case 0xE0:
                    pattern = AccessPattern.ImmediateRead;
                    return true;
                case 0xA4:
                    pattern = AccessPattern.ZeroPageRead;
                    return true;
                case 0xB4:
                    pattern = AccessPattern.ZeroPageIndexedReadX;
                    return true;
                case 0xAC:
                    pattern = AccessPattern.AbsoluteRead;
                    return true;
                case 0xBC:
                    pattern = AccessPattern.AbsoluteIndexedReadX;
                    return true;
            }

            switch (opcode & 0x03)
            {
                case 0x01:
                    return TryGetGroup01Pattern(cpu, opcode, out pattern, out writeValue);
                case 0x02:
                    return TryGetGroup02Pattern(cpu, opcode, out pattern, out writeValue);
                case 0x03:
                    return TryGetGroup03Pattern(cpu, opcode, out pattern, out writeValue);
                default:
                    return TryGetGroup00Pattern(cpu, opcode, out pattern, out writeValue);
            }
        }

        private static bool TryGetGroup01Pattern(Cpu6510 cpu, byte opcode, out AccessPattern pattern, out byte writeValue)
        {
            int operation = opcode >> 5;
            int mode = (opcode >> 2) & 0x07;
            bool store = operation == 4;
            writeValue = store ? cpu.A : (byte)0;
            // Group 01 contains the common ORA/AND/EOR/ADC/STA/LDA/CMP/SBC family.
            // The addressing mode bits are regular enough to share one table.
            return TryGetMode01Pattern(mode, store, out pattern);
        }

        private static bool TryGetGroup02Pattern(Cpu6510 cpu, byte opcode, out AccessPattern pattern, out byte writeValue)
        {
            int operation = opcode >> 5;
            int mode = (opcode >> 2) & 0x07;
            writeValue = 0;

            if (operation <= 3 || operation >= 6)
            {
                return TryGetModifyMode02Pattern(mode, out pattern);
            }

            if (operation == 4)
            {
                writeValue = cpu.X;
                switch (mode)
                {
                    case 1:
                        pattern = AccessPattern.ZeroPageWrite;
                        return true;
                    case 3:
                        pattern = AccessPattern.AbsoluteWrite;
                        return true;
                    case 5:
                        pattern = AccessPattern.ZeroPageIndexedWriteY;
                        return true;
                    default:
                        pattern = AccessPattern.None;
                        return false;
                }
            }

            switch (mode)
            {
                case 0:
                    pattern = AccessPattern.ImmediateRead;
                    return true;
                case 1:
                    pattern = AccessPattern.ZeroPageRead;
                    return true;
                case 3:
                    pattern = AccessPattern.AbsoluteRead;
                    return true;
                case 5:
                    pattern = AccessPattern.ZeroPageIndexedReadY;
                    return true;
                case 7:
                    pattern = AccessPattern.AbsoluteIndexedReadY;
                    return true;
                default:
                    pattern = AccessPattern.None;
                    return false;
            }
        }

        private static bool TryGetGroup03Pattern(Cpu6510 cpu, byte opcode, out AccessPattern pattern, out byte writeValue)
        {
            int operation = opcode >> 5;
            int mode = (opcode >> 2) & 0x07;
            writeValue = 0;

            if (operation <= 3 || operation >= 6)
            {
                return TryGetModifyMode03Pattern(mode, out pattern);
            }

            if (operation == 4)
            {
                writeValue = (byte)(cpu.A & cpu.X);
                switch (mode)
                {
                    case 0:
                        pattern = AccessPattern.IndirectZeroPageXWrite;
                        return true;
                    case 1:
                        pattern = AccessPattern.ZeroPageWrite;
                        return true;
                    case 3:
                        pattern = AccessPattern.AbsoluteWrite;
                        return true;
                    case 5:
                        pattern = AccessPattern.ZeroPageIndexedWriteY;
                        return true;
                    default:
                        pattern = AccessPattern.None;
                        return false;
                }
            }

            switch (mode)
            {
                case 0:
                    pattern = AccessPattern.IndirectZeroPageXModify;
                    return true;
                case 1:
                    pattern = AccessPattern.ZeroPageRead;
                    return true;
                case 3:
                    pattern = AccessPattern.AbsoluteRead;
                    return true;
                case 4:
                    pattern = AccessPattern.IndirectZeroPageYModify;
                    return true;
                case 5:
                    pattern = AccessPattern.ZeroPageIndexedReadY;
                    return true;
                case 7:
                    pattern = AccessPattern.AbsoluteIndexedReadY;
                    return true;
                default:
                    pattern = AccessPattern.None;
                    return false;
            }
        }

        private static bool TryGetGroup00Pattern(Cpu6510 cpu, byte opcode, out AccessPattern pattern, out byte writeValue)
        {
            writeValue = 0;
            switch (opcode)
            {
                case 0x04:
                case 0x44:
                case 0x64:
                    pattern = AccessPattern.ZeroPageRead;
                    return true;
                case 0x14:
                case 0x34:
                case 0x54:
                case 0x74:
                case 0xD4:
                case 0xF4:
                    pattern = AccessPattern.ZeroPageIndexedReadX;
                    return true;
                case 0x0C:
                    pattern = AccessPattern.AbsoluteRead;
                    return true;
                case 0x1C:
                case 0x3C:
                case 0x5C:
                case 0x7C:
                case 0xDC:
                case 0xFC:
                    pattern = AccessPattern.AbsoluteIndexedReadX;
                    return true;
                default:
                    pattern = AccessPattern.None;
                    return false;
            }
        }

        private static bool TryGetMode01Pattern(int mode, bool store, out AccessPattern pattern)
        {
            switch (mode)
            {
                case 0:
                    pattern = store ? AccessPattern.IndirectZeroPageXWrite : AccessPattern.IndirectZeroPageXRead;
                    return true;
                case 1:
                    pattern = store ? AccessPattern.ZeroPageWrite : AccessPattern.ZeroPageRead;
                    return true;
                case 2:
                    pattern = store ? AccessPattern.None : AccessPattern.ImmediateRead;
                    return !store;
                case 3:
                    pattern = store ? AccessPattern.AbsoluteWrite : AccessPattern.AbsoluteRead;
                    return true;
                case 4:
                    pattern = store ? AccessPattern.IndirectZeroPageYWrite : AccessPattern.IndirectZeroPageYRead;
                    return true;
                case 5:
                    pattern = store ? AccessPattern.ZeroPageIndexedWriteX : AccessPattern.ZeroPageIndexedReadX;
                    return true;
                case 6:
                    pattern = store ? AccessPattern.AbsoluteIndexedWriteY : AccessPattern.AbsoluteIndexedReadY;
                    return true;
                case 7:
                    pattern = store ? AccessPattern.AbsoluteIndexedWriteX : AccessPattern.AbsoluteIndexedReadX;
                    return true;
                default:
                    pattern = AccessPattern.None;
                    return false;
            }
        }

        private static bool TryGetModifyMode02Pattern(int mode, out AccessPattern pattern)
        {
            switch (mode)
            {
                case 1:
                    pattern = AccessPattern.ZeroPageModify;
                    return true;
                case 3:
                    pattern = AccessPattern.AbsoluteModify;
                    return true;
                case 5:
                    pattern = AccessPattern.ZeroPageIndexedModifyX;
                    return true;
                case 7:
                    pattern = AccessPattern.AbsoluteIndexedModifyX;
                    return true;
                default:
                    pattern = AccessPattern.None;
                    return false;
            }
        }

        private static bool TryGetModifyMode03Pattern(int mode, out AccessPattern pattern)
        {
            switch (mode)
            {
                case 0:
                    pattern = AccessPattern.IndirectZeroPageXRead;
                    return true;
                case 1:
                    pattern = AccessPattern.ZeroPageModify;
                    return true;
                case 3:
                    pattern = AccessPattern.AbsoluteModify;
                    return true;
                case 4:
                    pattern = AccessPattern.IndirectZeroPageYRead;
                    return true;
                case 5:
                    pattern = AccessPattern.ZeroPageIndexedModifyX;
                    return true;
                case 6:
                    pattern = AccessPattern.AbsoluteIndexedModifyY;
                    return true;
                case 7:
                    pattern = AccessPattern.AbsoluteIndexedModifyX;
                    return true;
                default:
                    pattern = AccessPattern.None;
                    return false;
            }
        }

        private static bool TryPredictPattern(
            Cpu6510 cpu,
            InstructionContext context,
            AccessPattern pattern,
            byte writeValue,
            out CpuBusAccessPrediction prediction)
        {
            switch (pattern)
            {
                case AccessPattern.ImmediateRead:
                    return PredictImmediateRead(cpu, context, out prediction);
                case AccessPattern.ZeroPageRead:
                    return PredictZeroPageRead(cpu, context, out prediction);
                case AccessPattern.ZeroPageWrite:
                    return PredictZeroPageWrite(cpu, context, writeValue, out prediction);
                case AccessPattern.ZeroPageIndexedReadX:
                    return PredictZeroPageIndexedRead(cpu, context, cpu.X, out prediction);
                case AccessPattern.ZeroPageIndexedReadY:
                    return PredictZeroPageIndexedRead(cpu, context, cpu.Y, out prediction);
                case AccessPattern.ZeroPageIndexedWriteX:
                    return PredictZeroPageIndexedWrite(cpu, context, cpu.X, writeValue, out prediction);
                case AccessPattern.ZeroPageIndexedWriteY:
                    return PredictZeroPageIndexedWrite(cpu, context, cpu.Y, writeValue, out prediction);
                case AccessPattern.AbsoluteRead:
                    return PredictAbsoluteRead(cpu, context, out prediction);
                case AccessPattern.AbsoluteWrite:
                    return PredictAbsoluteWrite(cpu, context, writeValue, out prediction);
                case AccessPattern.AbsoluteIndexedReadX:
                    return PredictAbsoluteIndexedRead(cpu, context, cpu.X, out prediction);
                case AccessPattern.AbsoluteIndexedReadY:
                    return PredictAbsoluteIndexedRead(cpu, context, cpu.Y, out prediction);
                case AccessPattern.AbsoluteIndexedWriteX:
                    return PredictAbsoluteIndexedWrite(cpu, context, cpu.X, writeValue, out prediction);
                case AccessPattern.AbsoluteIndexedWriteY:
                    return PredictAbsoluteIndexedWrite(cpu, context, cpu.Y, writeValue, out prediction);
                case AccessPattern.IndirectZeroPageXRead:
                    return PredictIndirectZeroPageXRead(cpu, context, out prediction);
                case AccessPattern.IndirectZeroPageXWrite:
                    return PredictIndirectZeroPageXWrite(cpu, context, writeValue, out prediction);
                case AccessPattern.IndirectZeroPageXModify:
                    return PredictIndirectZeroPageXModify(cpu, context, out prediction);
                case AccessPattern.IndirectZeroPageYRead:
                    return PredictIndirectZeroPageYRead(cpu, context, out prediction);
                case AccessPattern.IndirectZeroPageYWrite:
                    return PredictIndirectZeroPageYWrite(cpu, context, writeValue, out prediction);
                case AccessPattern.IndirectZeroPageYModify:
                    return PredictIndirectZeroPageYModify(cpu, context, out prediction);
                case AccessPattern.ZeroPageModify:
                    return PredictZeroPageModify(cpu, context, out prediction);
                case AccessPattern.ZeroPageIndexedModifyX:
                    return PredictZeroPageIndexedModify(cpu, context, cpu.X, out prediction);
                case AccessPattern.ZeroPageIndexedModifyY:
                    return PredictZeroPageIndexedModify(cpu, context, cpu.Y, out prediction);
                case AccessPattern.AbsoluteModify:
                    return PredictAbsoluteModify(cpu, context, out prediction);
                case AccessPattern.AbsoluteIndexedModifyX:
                    return PredictAbsoluteIndexedModify(cpu, context, cpu.X, out prediction);
                case AccessPattern.AbsoluteIndexedModifyY:
                    return PredictAbsoluteIndexedModify(cpu, context, cpu.Y, out prediction);
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return false;
            }
        }

        private static bool PredictImmediateRead(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            prediction = context.StepIndex == 0 ? Read(cpu, CpuTraceAccessType.Read, cpu.PC) : CpuBusAccessPrediction.None;
            return true;
        }

        private static bool PredictZeroPageRead(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictZeroPageWrite(Cpu6510 cpu, InstructionContext context, byte value, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Write(cpu, (byte)context.Address, value);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictZeroPageIndexedRead(Cpu6510 cpu, InstructionContext context, byte index, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictZeroPageIndexedWrite(Cpu6510 cpu, InstructionContext context, byte index, byte value, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Write(cpu, (byte)context.Address, value);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictAbsoluteRead(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictAbsoluteWrite(Cpu6510 cpu, InstructionContext context, byte value, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    prediction = Write(cpu, context.Address, value);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictAbsoluteIndexedRead(Cpu6510 cpu, InstructionContext context, byte index, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    // Indexed reads save one cycle when the high byte does not
                    // change.  On a page cross, the 6510 first performs a dummy read
                    // from the wrapped address, then retries with the corrected page.
                    if (context.PageCrossed)
                    {
                        prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address2, context.Address));
                    }
                    else
                    {
                        prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    }

                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictAbsoluteIndexedWrite(Cpu6510 cpu, InstructionContext context, byte index, byte value, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    // Stores and read-modify-write indexed modes always pay the
                    // dummy-read cycle, even when the effective address stays on the
                    // original page.
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address2, context.Address));
                    return true;
                case 3:
                    prediction = Write(cpu, context.Address, value);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictIndirectZeroPageXRead(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    // (zp,X) performs the indexing dummy read before reading the
                    // zero-page pointer pair.  The operand has already been wrapped
                    // into context.Address by the instruction implementation.
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)(context.Address + 1));
                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address2);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictIndirectZeroPageXWrite(Cpu6510 cpu, InstructionContext context, byte value, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)(context.Address + 1));
                    return true;
                case 4:
                    prediction = Write(cpu, context.Address2, value);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictIndirectZeroPageYRead(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)(context.Address + 1));
                    return true;
                case 3:
                    if (context.PageCrossed)
                    {
                        prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address2, context.Address));
                    }
                    else
                    {
                        prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    }

                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictIndirectZeroPageYWrite(Cpu6510 cpu, InstructionContext context, byte value, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)(context.Address + 1));
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address2, context.Address));
                    return true;
                case 4:
                    prediction = Write(cpu, context.Address, value);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictIndirectZeroPageXModify(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)(context.Address + 1));
                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address2);
                    return true;
                case 5:
                    prediction = Write(cpu, context.Address2, context.Operand);
                    return true;
                case 6:
                    prediction = Write(cpu, context.Address2, GetModifiedValue(cpu, context));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictIndirectZeroPageYModify(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)(context.Address + 1));
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address2, context.Address));
                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                case 5:
                    prediction = Write(cpu, context.Address, context.Operand);
                    return true;
                case 6:
                    prediction = Write(cpu, context.Address, GetModifiedValue(cpu, context));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictZeroPageModify(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 2:
                    // NMOS 6502 read-modify-write instructions expose two writes:
                    // first the unmodified operand, then the final value.
                    prediction = Write(cpu, (byte)context.Address, context.Operand);
                    return true;
                case 3:
                    prediction = Write(cpu, (byte)context.Address, GetModifiedValue(cpu, context));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictZeroPageIndexedModify(Cpu6510 cpu, InstructionContext context, byte index, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (byte)context.Address);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, (byte)context.Address);
                    return true;
                case 3:
                    prediction = Write(cpu, (byte)context.Address, context.Operand);
                    return true;
                case 4:
                    prediction = Write(cpu, (byte)context.Address, GetModifiedValue(cpu, context));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictAbsoluteModify(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                case 3:
                    prediction = Write(cpu, context.Address, context.Operand);
                    return true;
                case 4:
                    prediction = Write(cpu, context.Address, GetModifiedValue(cpu, context));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool PredictAbsoluteIndexedModify(Cpu6510 cpu, InstructionContext context, byte index, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address2, context.Address));
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                case 4:
                    prediction = Write(cpu, context.Address, context.Operand);
                    return true;
                case 5:
                    prediction = Write(cpu, context.Address, GetModifiedValue(cpu, context));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictBrk(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                case 1:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)((cpu.PC >> 8) & 0xFF));
                    return true;
                case 2:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)(cpu.PC & 0xFF));
                    return true;
                case 3:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)(cpu.SR | 0x30));
                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, 0xFFFE);
                    return true;
                case 5:
                    prediction = Read(cpu, CpuTraceAccessType.Read, 0xFFFF);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictJsr(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (ushort)(0x0100 + cpu.SP));
                    return true;
                case 2:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)((cpu.PC >> 8) & 0xFF));
                    return true;
                case 3:
                    prediction = Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)(cpu.PC & 0xFF));
                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictRts(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (ushort)(0x0100 + cpu.SP));
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, StackPullAddress(cpu));
                    return true;
                case 3:
                    prediction = Read(cpu, CpuTraceAccessType.Read, StackPullAddress(cpu));
                    return true;
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictRti(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (ushort)(0x0100 + cpu.SP));
                    return true;
                case 2:
                case 3:
                case 4:
                    prediction = Read(cpu, CpuTraceAccessType.Read, StackPullAddress(cpu));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictPha(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            if (context.StepIndex == 0)
            {
                prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                return true;
            }

            prediction = context.StepIndex == 1 ? Write(cpu, (ushort)(0x0100 + cpu.SP), cpu.A) : CpuBusAccessPrediction.None;
            return true;
        }

        private static bool TryPredictPhp(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            if (context.StepIndex == 0)
            {
                prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                return true;
            }

            prediction = context.StepIndex == 1 ? Write(cpu, (ushort)(0x0100 + cpu.SP), (byte)(cpu.SR | 0x30)) : CpuBusAccessPrediction.None;
            return true;
        }

        private static bool TryPredictPla(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, (ushort)(0x0100 + cpu.SP));
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, StackPullAddress(cpu));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictPlp(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            return TryPredictPla(cpu, context, out prediction);
        }

        private static bool TryPredictJmpAbsolute(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictJmpIndirect(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                case 1:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.Read, context.Address);
                    return true;
                case 3:
                    // Preserve the original 6502 page-wrap bug: JMP ($xxFF) reads
                    // the high byte from $xx00, not the next page.
                    prediction = Read(cpu, CpuTraceAccessType.Read, (ushort)((context.Address & 0xFF00) | ((context.Address + 1) & 0x00FF)));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool TryPredictBranch(Cpu6510 cpu, InstructionContext context, out CpuBusAccessPrediction prediction)
        {
            switch (context.StepIndex)
            {
                case 0:
                    prediction = Read(cpu, CpuTraceAccessType.Read, cpu.PC);
                    return true;
                case 1:
                    // Taken branches perform a dummy opcode fetch from the fallthrough
                    // PC.  Page-crossing branches then add one more wrapped dummy read.
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, cpu.PC);
                    return true;
                case 2:
                    prediction = Read(cpu, CpuTraceAccessType.DummyRead, GetPageWrappedAddress(context.Address, context.Address2));
                    return true;
                default:
                    prediction = CpuBusAccessPrediction.None;
                    return true;
            }
        }

        private static bool IsImpliedOrAccumulator(byte opcode)
        {
            switch (opcode)
            {
                case 0x18:
                case 0x38:
                case 0x58:
                case 0x78:
                case 0xB8:
                case 0xD8:
                case 0xF8:
                case 0x88:
                case 0x8A:
                case 0x98:
                case 0x9A:
                case 0xA8:
                case 0xAA:
                case 0xC8:
                case 0xCA:
                case 0xE8:
                case 0xEA:
                case 0x1A:
                case 0x3A:
                case 0x5A:
                case 0x7A:
                case 0xDA:
                case 0xFA:
                case 0x0A:
                case 0x2A:
                case 0x4A:
                case 0x6A:
                    return true;
                default:
                    return false;
            }
        }

        private static CpuBusAccessPrediction Read(Cpu6510 cpu, CpuTraceAccessType accessType, ushort address)
        {
            return new CpuBusAccessPrediction
            {
                AccessType = accessType,
                Address = address,
                Value = cpu.PeekForMicrocyclePrediction(address)
            };
        }

        private static CpuBusAccessPrediction Write(Cpu6510 cpu, ushort address, byte value)
        {
            return new CpuBusAccessPrediction
            {
                AccessType = CpuTraceAccessType.Write,
                Address = address,
                Value = value
            };
        }

        private static ushort StackPullAddress(Cpu6510 cpu)
        {
            return (ushort)(0x0100 + ((cpu.SP + 1) & 0xFF));
        }

        private static ushort GetPageWrappedAddress(ushort original, ushort indexed)
        {
            return (ushort)((original & 0xFF00) | (indexed & 0x00FF));
        }

        private static byte GetModifiedValue(Cpu6510 cpu, InstructionContext context)
        {
            byte value = context.Operand;
            int operation = context.Opcode >> 5;
            switch (operation)
            {
                case 0:
                    return (byte)(value << 1);
                case 1:
                    return (byte)((value << 1) | (cpu.GetFlag(0x01) ? 1 : 0));
                case 2:
                    return (byte)(value >> 1);
                case 3:
                    return (byte)((value >> 1) | (cpu.GetFlag(0x01) ? 0x80 : 0x00));
                case 6:
                    return (byte)(value - 1);
                case 7:
                    return (byte)(value + 1);
                default:
                    return value;
            }
        }
    }
}
