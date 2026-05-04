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
    /// Represents the instruction steps component.
    /// </summary>
    public static class InstructionSteps
    {
        private const byte UnstableOpcodeMagic = 0xEE;

        /// <summary>
        /// Handles the sei implied operation.
        /// </summary>
        public static bool SeiImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x04, true);
            return true;
        }

        /// <summary>
        /// Handles the cli implied operation.
        /// </summary>
        public static bool CliImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x04, false);
            cpu.DelayInterruptPoll();
            return true;
        }

        /// <summary>
        /// Handles the clc implied operation.
        /// </summary>
        public static bool ClcImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x01, false);
            return true;
        }

        /// <summary>
        /// Handles the sec implied operation.
        /// </summary>
        public static bool SecImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x01, true);
            return true;
        }

        /// <summary>
        /// Handles the clv implied operation.
        /// </summary>
        public static bool ClvImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x40, false);
            return true;
        }

        /// <summary>
        /// Handles the cld implied operation.
        /// </summary>
        public static bool CldImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x08, false);
            return true;
        }

        /// <summary>
        /// Handles the sed implied operation.
        /// </summary>
        public static bool SedImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SetFlag(0x08, true);
            return true;
        }

        /// <summary>
        /// Handles the lda immediate operation.
        /// </summary>
        public static bool LdaImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.A = context.Operand;
                    cpu.SetNZ(cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the ora immediate operation.
        /// </summary>
        public static bool OraImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Ora(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the ora zero page operation.
        /// </summary>
        public static bool OraZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, cpu.Ora);
        }

        /// <summary>
        /// Handles the ora absolute operation.
        /// </summary>
        public static bool OraAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, cpu.Ora);
        }

        /// <summary>
        /// Handles the ora indirect zero page x operation.
        /// </summary>
        public static bool OraIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, cpu.Ora);
        }

        /// <summary>
        /// Handles the and immediate operation.
        /// </summary>
        public static bool AndImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.And(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the and zero page operation.
        /// </summary>
        public static bool AndZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, cpu.And);
        }

        /// <summary>
        /// Handles the and absolute operation.
        /// </summary>
        public static bool AndAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, cpu.And);
        }

        /// <summary>
        /// Handles the and indirect zero page x operation.
        /// </summary>
        public static bool AndIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, cpu.And);
        }

        /// <summary>
        /// Handles the bit zero page operation.
        /// </summary>
        public static bool BitZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, cpu.Bit);
        }

        /// <summary>
        /// Handles the bit absolute operation.
        /// </summary>
        public static bool BitAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, cpu.Bit);
        }

        /// <summary>
        /// Handles the eor immediate operation.
        /// </summary>
        public static bool EorImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Eor(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the eor zero page operation.
        /// </summary>
        public static bool EorZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, cpu.Eor);
        }

        /// <summary>
        /// Handles the eor absolute operation.
        /// </summary>
        public static bool EorAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, cpu.Eor);
        }

        /// <summary>
        /// Handles the eor indirect zero page x operation.
        /// </summary>
        public static bool EorIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, cpu.Eor);
        }

        /// <summary>
        /// Handles the adc immediate operation.
        /// </summary>
        public static bool AdcImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Adc(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the adc zero page operation.
        /// </summary>
        public static bool AdcZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, cpu.Adc);
        }

        /// <summary>
        /// Handles the adc absolute operation.
        /// </summary>
        public static bool AdcAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, cpu.Adc);
        }

        /// <summary>
        /// Handles the adc indirect zero page x operation.
        /// </summary>
        public static bool AdcIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, cpu.Adc);
        }

        /// <summary>
        /// Handles the lda zero page operation.
        /// </summary>
        public static bool LdaZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    cpu.A = context.Operand;
                    cpu.SetNZ(cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the lda zero page x operation.
        /// </summary>
        public static bool LdaZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageIndexedRead(cpu, ref context, cpu.X, value =>
            {
                cpu.A = value;
                cpu.SetNZ(cpu.A);
            });
        }

        /// <summary>
        /// Handles the lda indirect zero page y operation.
        /// </summary>
        public static bool LdaIndirectZeroPageY(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageYRead(cpu, ref context, value =>
            {
                cpu.A = value;
                cpu.SetNZ(cpu.A);
            });
        }

        /// <summary>
        /// Handles the lda indirect zero page x operation.
        /// </summary>
        public static bool LdaIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, value =>
            {
                cpu.A = value;
                cpu.SetNZ(cpu.A);
            });
        }

        /// <summary>
        /// Handles the ldy zero page operation.
        /// </summary>
        public static bool LdyZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    cpu.Y = context.Operand;
                    cpu.SetNZ(cpu.Y);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the ldy zero page x operation.
        /// </summary>
        public static bool LdyZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageIndexedRead(cpu, ref context, cpu.X, value =>
            {
                cpu.Y = value;
                cpu.SetNZ(cpu.Y);
            });
        }

        /// <summary>
        /// Handles the ldy absolute operation.
        /// </summary>
        public static bool LdyAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read(context.Address);
                    cpu.Y = context.Operand;
                    cpu.SetNZ(cpu.Y);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the lda absolute operation.
        /// </summary>
        public static bool LdaAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read(context.Address);
                    cpu.A = context.Operand;
                    cpu.SetNZ(cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the lda absolute x operation.
        /// </summary>
        public static bool LdaAbsoluteX(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedRead(cpu, ref context, cpu.X, value =>
            {
                cpu.A = value;
                cpu.SetNZ(cpu.A);
            });
        }

        /// <summary>
        /// Handles the lda absolute y operation.
        /// </summary>
        public static bool LdaAbsoluteY(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedRead(cpu, ref context, cpu.Y, value =>
            {
                cpu.A = value;
                cpu.SetNZ(cpu.A);
            });
        }

        /// <summary>
        /// Handles the ldx immediate operation.
        /// </summary>
        public static bool LdxImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.X = context.Operand;
                    cpu.SetNZ(cpu.X);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the ldx zero page operation.
        /// </summary>
        public static bool LdxZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    cpu.X = context.Operand;
                    cpu.SetNZ(cpu.X);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the ldx absolute operation.
        /// </summary>
        public static bool LdxAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read(context.Address);
                    cpu.X = context.Operand;
                    cpu.SetNZ(cpu.X);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the ldy immediate operation.
        /// </summary>
        public static bool LdyImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Y = context.Operand;
                    cpu.SetNZ(cpu.Y);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the sta zero page operation.
        /// </summary>
        public static bool StaZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Write((byte)context.Address, cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the stx zero page operation.
        /// </summary>
        public static bool StxZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Write((byte)context.Address, cpu.X);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the stx zero page y operation.
        /// </summary>
        public static bool StxZeroPageY(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageIndexedWrite(cpu, ref context, cpu.Y, cpu.X);
        }

        /// <summary>
        /// Handles the sty zero page operation.
        /// </summary>
        public static bool StyZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Write((byte)context.Address, cpu.Y);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the sty zero page x operation.
        /// </summary>
        public static bool StyZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageIndexedWrite(cpu, ref context, cpu.X, cpu.Y);
        }

        /// <summary>
        /// Handles the sta absolute operation.
        /// </summary>
        public static bool StaAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Write(context.Address, cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the stx absolute operation.
        /// </summary>
        public static bool StxAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Write(context.Address, cpu.X);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the sty absolute operation.
        /// </summary>
        public static bool StyAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Write(context.Address, cpu.Y);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the sta indirect zero page y operation.
        /// </summary>
        public static bool StaIndirectZeroPageY(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageYWrite(cpu, ref context, cpu.A);
        }

        /// <summary>
        /// Handles the sta indirect zero page x operation.
        /// </summary>
        public static bool StaIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXWrite(cpu, ref context, cpu.A);
        }

        /// <summary>
        /// Handles the sta zero page x operation.
        /// </summary>
        public static bool StaZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageIndexedWrite(cpu, ref context, cpu.X, cpu.A);
        }

        /// <summary>
        /// Handles the sta absolute x operation.
        /// </summary>
        public static bool StaAbsoluteX(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedWrite(cpu, ref context, cpu.X, cpu.A);
        }

        /// <summary>
        /// Handles the sta absolute y operation.
        /// </summary>
        public static bool StaAbsoluteY(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedWrite(cpu, ref context, cpu.Y, cpu.A);
        }

        /// <summary>
        /// Handles the jmp absolute operation.
        /// </summary>
        public static bool JmpAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC = context.Address;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the jmp indirect operation.
        /// </summary>
        public static bool JmpIndirect(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 = cpu.Read(context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Address2 |= (ushort)(cpu.Read((ushort)((context.Address & 0xFF00) | ((context.Address + 1) & 0x00FF))) << 8);
                    cpu.PC = context.Address2;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the jsr absolute operation.
        /// </summary>
        public static bool JsrAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((ushort)(0x0100 + cpu.SP));
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Push((byte)((cpu.PC >> 8) & 0xFF));
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.Push((byte)(cpu.PC & 0xFF));
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC = context.Address;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the rts implied operation.
        /// </summary>
        public static bool RtsImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((ushort)(0x0100 + cpu.SP));
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Pull();
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Operand2 = cpu.Pull();
                    cpu.PC = (ushort)(context.Operand | (context.Operand2 << 8));
                    context.StepIndex++;
                    return false;
                case 4:
                    cpu.DummyRead(cpu.PC);
                    cpu.PC++;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the bne relative operation.
        /// </summary>
        public static bool BneRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, !cpu.GetFlag(0x02));
        }

        /// <summary>
        /// Handles the beq relative operation.
        /// </summary>
        public static bool BeqRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, cpu.GetFlag(0x02));
        }

        /// <summary>
        /// Handles the bpl relative operation.
        /// </summary>
        public static bool BplRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, !cpu.GetFlag(0x80));
        }

        /// <summary>
        /// Handles the bmi relative operation.
        /// </summary>
        public static bool BmiRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, cpu.GetFlag(0x80));
        }

        /// <summary>
        /// Handles the bcc relative operation.
        /// </summary>
        public static bool BccRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, !cpu.GetFlag(0x01));
        }

        /// <summary>
        /// Handles the bcs relative operation.
        /// </summary>
        public static bool BcsRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, cpu.GetFlag(0x01));
        }

        /// <summary>
        /// Handles the cmp immediate operation.
        /// </summary>
        public static bool CmpImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Compare(cpu.A, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the cpy immediate operation.
        /// </summary>
        public static bool CpyImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Compare(cpu.Y, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the cpy zero page operation.
        /// </summary>
        public static bool CpyZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, value => cpu.Compare(cpu.Y, value));
        }

        /// <summary>
        /// Handles the cpy absolute operation.
        /// </summary>
        public static bool CpyAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, value => cpu.Compare(cpu.Y, value));
        }

        /// <summary>
        /// Handles the cmp zero page operation.
        /// </summary>
        public static bool CmpZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    cpu.Compare(cpu.A, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the cmp indirect zero page x operation.
        /// </summary>
        public static bool CmpIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, value => cpu.Compare(cpu.A, value));
        }

        /// <summary>
        /// Handles the cmp absolute operation.
        /// </summary>
        public static bool CmpAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read(context.Address);
                    cpu.Compare(cpu.A, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the cmp absolute x operation.
        /// </summary>
        public static bool CmpAbsoluteX(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedRead(cpu, ref context, cpu.X, value => cpu.Compare(cpu.A, value));
        }

        /// <summary>
        /// Handles the cmp indirect zero page y operation.
        /// </summary>
        public static bool CmpIndirectZeroPageY(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageYRead(cpu, ref context, value => cpu.Compare(cpu.A, value));
        }

        /// <summary>
        /// Handles the cpx zero page operation.
        /// </summary>
        public static bool CpxZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    cpu.Compare(cpu.X, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the cpx immediate operation.
        /// </summary>
        public static bool CpxImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Compare(cpu.X, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the sbc immediate operation.
        /// </summary>
        public static bool SbcImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    cpu.Sbc(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the sbc zero page operation.
        /// </summary>
        public static bool SbcZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, cpu.Sbc);
        }

        /// <summary>
        /// Handles the sbc absolute operation.
        /// </summary>
        public static bool SbcAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, cpu.Sbc);
        }

        /// <summary>
        /// Handles the sbc indirect zero page x operation.
        /// </summary>
        public static bool SbcIndirectZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageXRead(cpu, ref context, cpu.Sbc);
        }

        /// <summary>
        /// Handles the pha implied operation.
        /// </summary>
        public static bool PhaImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Push(cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the pla implied operation.
        /// </summary>
        public static bool PlaImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((ushort)(0x0100 + cpu.SP));
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.A = cpu.Pull();
                    cpu.SetNZ(cpu.A);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the php implied operation.
        /// </summary>
        public static bool PhpImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Push((byte)(cpu.SR | 0x30));
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the plp implied operation.
        /// </summary>
        public static bool PlpImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((ushort)(0x0100 + cpu.SP));
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.SR = (byte)((cpu.Pull() | 0x20) & 0xEF | (cpu.SR & 0x10));
                    cpu.DelayInterruptPoll();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the dey implied operation.
        /// </summary>
        public static bool DeyImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.Y--;
            cpu.SetNZ(cpu.Y);
            return true;
        }

        /// <summary>
        /// Handles the rol accumulator operation.
        /// </summary>
        public static bool RolAccumulator(Cpu6510 cpu, ref InstructionContext context)
        {
            var carryIn = cpu.GetFlag(0x01) ? 1 : 0;
            var value = cpu.A;
            cpu.SetFlag(0x01, (value & 0x80) != 0);
            cpu.A = (byte)((value << 1) | carryIn);
            cpu.SetNZ(cpu.A);
            return true;
        }

        /// <summary>
        /// Handles the txa implied operation.
        /// </summary>
        public static bool TxaImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.A = cpu.X;
            cpu.SetNZ(cpu.A);
            return true;
        }

        /// <summary>
        /// Handles the tya implied operation.
        /// </summary>
        public static bool TyaImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.A = cpu.Y;
            cpu.SetNZ(cpu.A);
            return true;
        }

        /// <summary>
        /// Handles the txs implied operation.
        /// </summary>
        public static bool TxsImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.SP = cpu.X;
            return true;
        }

        /// <summary>
        /// Handles the tay implied operation.
        /// </summary>
        public static bool TayImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.Y = cpu.A;
            cpu.SetNZ(cpu.Y);
            return true;
        }

        /// <summary>
        /// Handles the tax implied operation.
        /// </summary>
        public static bool TaxImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.X = cpu.A;
            cpu.SetNZ(cpu.X);
            return true;
        }

        /// <summary>
        /// Handles the iny implied operation.
        /// </summary>
        public static bool InyImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.Y++;
            cpu.SetNZ(cpu.Y);
            return true;
        }

        /// <summary>
        /// Handles the dex implied operation.
        /// </summary>
        public static bool DexImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.X--;
            cpu.SetNZ(cpu.X);
            return true;
        }

        /// <summary>
        /// Handles the inx implied operation.
        /// </summary>
        public static bool InxImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.X++;
            cpu.SetNZ(cpu.X);
            return true;
        }

        /// <summary>
        /// Handles the inc zero page operation.
        /// </summary>
        public static bool IncZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand++;
                    cpu.Write((byte)context.Address, context.Operand);
                    cpu.SetNZ(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the dec zero page operation.
        /// </summary>
        public static bool DecZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand--;
                    cpu.Write((byte)context.Address, context.Operand);
                    cpu.SetNZ(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the brk implied operation.
        /// </summary>
        public static bool BrkImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Push((byte)((cpu.PC >> 8) & 0xFF));
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Push((byte)(cpu.PC & 0xFF));
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.Push((byte)(cpu.SR | 0x30));
                    cpu.SetFlag(0x04, true);
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Address = cpu.Read(0xFFFE);
                    context.StepIndex++;
                    return false;
                case 5:
                    context.Address |= (ushort)(cpu.Read(0xFFFF) << 8);
                    cpu.PC = context.Address;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the rti implied operation.
        /// </summary>
        public static bool RtiImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.StepIndex)
            {
                case 0:
                    cpu.DummyRead(cpu.PC);
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((ushort)(0x0100 + cpu.SP));
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.SR = (byte)((cpu.Pull() | 0x20) & 0xEF);
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Operand = cpu.Pull();
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Operand2 = cpu.Pull();
                    cpu.PC = (ushort)(context.Operand | (context.Operand2 << 8));
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the bvc relative operation.
        /// </summary>
        public static bool BvcRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, !cpu.GetFlag(0x40));
        }

        /// <summary>
        /// Handles the bvs relative operation.
        /// </summary>
        public static bool BvsRelative(Cpu6510 cpu, ref InstructionContext context)
        {
            return Branch(cpu, ref context, cpu.GetFlag(0x40));
        }

        /// <summary>
        /// Handles the tsx implied operation.
        /// </summary>
        public static bool TsxImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.X = cpu.SP;
            cpu.SetNZ(cpu.X);
            return true;
        }

        /// <summary>
        /// Handles the nop immediate operation.
        /// </summary>
        public static bool NopImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value => { });
        }

        /// <summary>
        /// Handles the nop zero page operation.
        /// </summary>
        public static bool NopZeroPage(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageRead(cpu, ref context, value => { });
        }

        /// <summary>
        /// Handles the nop zero page x operation.
        /// </summary>
        public static bool NopZeroPageX(Cpu6510 cpu, ref InstructionContext context)
        {
            return ZeroPageIndexedRead(cpu, ref context, cpu.X, value => { });
        }

        /// <summary>
        /// Handles the nop absolute operation.
        /// </summary>
        public static bool NopAbsolute(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteRead(cpu, ref context, value => { });
        }

        /// <summary>
        /// Handles the nop absolute x operation.
        /// </summary>
        public static bool NopAbsoluteX(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedRead(cpu, ref context, cpu.X, value => { });
        }

        /// <summary>
        /// Handles the kil operation.
        /// </summary>
        public static bool Kil(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.Jam();
            return true;
        }

        /// <summary>
        /// Handles the group00 operation.
        /// </summary>
        public static bool Group00(Cpu6510 cpu, ref InstructionContext context)
        {
            switch (context.Opcode)
            {
                case 0x84:
                    return StyZeroPage(cpu, ref context);
                case 0x8C:
                    return StyAbsolute(cpu, ref context);
                case 0x94:
                    return StyZeroPageX(cpu, ref context);
                case 0xA4:
                    return LdyZeroPage(cpu, ref context);
                case 0xAC:
                    return LdyAbsolute(cpu, ref context);
                case 0xB4:
                    return LdyZeroPageX(cpu, ref context);
                case 0xBC:
                    return AbsoluteIndexedRead(cpu, ref context, cpu.X, value =>
                    {
                        cpu.Y = value;
                        cpu.SetNZ(cpu.Y);
                    });
                case 0xC4:
                    return CpyZeroPage(cpu, ref context);
                case 0xCC:
                    return CpyAbsolute(cpu, ref context);
                case 0xE4:
                    return CpxZeroPage(cpu, ref context);
                case 0xEC:
                    return AbsoluteRead(cpu, ref context, value => cpu.Compare(cpu.X, value));
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the group01 operation.
        /// </summary>
        public static bool Group01(Cpu6510 cpu, ref InstructionContext context)
        {
            int op = context.Opcode >> 5;
            int mode = (context.Opcode >> 2) & 0x07;

            switch (op)
            {
                case 0:
                    return ReadMode01(cpu, ref context, mode, cpu.Ora);
                case 1:
                    return ReadMode01(cpu, ref context, mode, cpu.And);
                case 2:
                    return ReadMode01(cpu, ref context, mode, cpu.Eor);
                case 3:
                    return ReadMode01(cpu, ref context, mode, cpu.Adc);
                case 4:
                    return StoreMode01(cpu, ref context, mode, cpu.A);
                case 5:
                    return ReadMode01(cpu, ref context, mode, value =>
                    {
                        cpu.A = value;
                        cpu.SetNZ(cpu.A);
                    });
                case 6:
                    return ReadMode01(cpu, ref context, mode, value => cpu.Compare(cpu.A, value));
                case 7:
                    return ReadMode01(cpu, ref context, mode, cpu.Sbc);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the group02 operation.
        /// </summary>
        public static bool Group02(Cpu6510 cpu, ref InstructionContext context)
        {
            int op = context.Opcode >> 5;
            int mode = (context.Opcode >> 2) & 0x07;

            switch (op)
            {
                case 0:
                    return ShiftMode02(cpu, ref context, mode, cpu.Asl);
                case 1:
                    return ShiftMode02(cpu, ref context, mode, cpu.Rol);
                case 2:
                    return ShiftMode02(cpu, ref context, mode, cpu.Lsr);
                case 3:
                    return ShiftMode02(cpu, ref context, mode, cpu.Ror);
                case 4:
                    return StoreMode02(cpu, ref context, mode, cpu.X);
                case 5:
                    return LoadMode02(cpu, ref context, mode);
                case 6:
                    return ModifyMode02(cpu, ref context, mode, value =>
                    {
                        value--;
                        cpu.SetNZ(value);
                        return value;
                    });
                case 7:
                    return ModifyMode02(cpu, ref context, mode, value =>
                    {
                        value++;
                        cpu.SetNZ(value);
                        return value;
                    });
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the group03 operation.
        /// </summary>
        public static bool Group03(Cpu6510 cpu, ref InstructionContext context)
        {
            int op = context.Opcode >> 5;
            int mode = (context.Opcode >> 2) & 0x07;

            switch (op)
            {
                case 0:
                    return ModifyMode03(cpu, ref context, mode, value =>
                    {
                        value = cpu.Asl(value);
                        cpu.Ora(value);
                        return value;
                    });
                case 1:
                    return ModifyMode03(cpu, ref context, mode, value =>
                    {
                        value = cpu.Rol(value);
                        cpu.And(value);
                        return value;
                    });
                case 2:
                    return ModifyMode03(cpu, ref context, mode, value =>
                    {
                        value = cpu.Lsr(value);
                        cpu.Eor(value);
                        return value;
                    });
                case 3:
                    return ModifyMode03(cpu, ref context, mode, value =>
                    {
                        value = cpu.Ror(value);
                        cpu.Adc(value);
                        return value;
                    });
                case 4:
                    return StoreMode03(cpu, ref context, mode, (byte)(cpu.A & cpu.X));
                case 5:
                    return ReadMode03(cpu, ref context, mode, value =>
                    {
                        cpu.A = value;
                        cpu.X = value;
                        cpu.SetNZ(value);
                    });
                case 6:
                    return ModifyMode03(cpu, ref context, mode, value =>
                    {
                        value--;
                        cpu.Compare(cpu.A, value);
                        return value;
                    });
                case 7:
                    return ModifyMode03(cpu, ref context, mode, value =>
                    {
                        value++;
                        cpu.Sbc(value);
                        return value;
                    });
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the anc immediate operation.
        /// </summary>
        public static bool AncImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value =>
            {
                cpu.And(value);
                cpu.SetFlag(0x01, cpu.GetFlag(0x80));
            });
        }

        /// <summary>
        /// Handles the alr immediate operation.
        /// </summary>
        public static bool AlrImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value =>
            {
                cpu.And(value);
                cpu.A = cpu.Lsr(cpu.A);
            });
        }

        /// <summary>
        /// Handles the arr immediate operation.
        /// </summary>
        public static bool ArrImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value =>
            {
                cpu.And(value);
                cpu.A = cpu.Ror(cpu.A);
                cpu.SetFlag(0x40, (((cpu.A >> 6) ^ (cpu.A >> 5)) & 0x01) != 0);
                cpu.SetFlag(0x01, (cpu.A & 0x40) != 0);
            });
        }

        /// <summary>
        /// Handles the xaa immediate operation.
        /// </summary>
        public static bool XaaImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value =>
            {
                cpu.A = (byte)((cpu.A | UnstableOpcodeMagic) & cpu.X & value);
                cpu.SetNZ(cpu.A);
            });
        }

        /// <summary>
        /// Handles the lax immediate operation.
        /// </summary>
        public static bool LaxImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value =>
            {
                byte result = (byte)((cpu.A | UnstableOpcodeMagic) & value);
                cpu.A = result;
                cpu.X = result;
                cpu.SetNZ(result);
            });
        }

        /// <summary>
        /// Handles the axs immediate operation.
        /// </summary>
        public static bool AxsImmediate(Cpu6510 cpu, ref InstructionContext context)
        {
            return ImmediateRead(cpu, ref context, value =>
            {
                byte ax = (byte)(cpu.A & cpu.X);
                int result = ax - value;
                cpu.X = (byte)result;
                cpu.SetFlag(0x01, ax >= value);
                cpu.SetNZ(cpu.X);
            });
        }

        /// <summary>
        /// Handles the las absolute y operation.
        /// </summary>
        public static bool LasAbsoluteY(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedRead(cpu, ref context, cpu.Y, value =>
            {
                value = (byte)(value & cpu.SP);
                cpu.A = value;
                cpu.X = value;
                cpu.SP = value;
                cpu.SetNZ(value);
            });
        }

        /// <summary>
        /// Handles the ahx indirect zero page y operation.
        /// </summary>
        public static bool AhxIndirectZeroPageY(Cpu6510 cpu, ref InstructionContext context)
        {
            return IndirectZeroPageYMaskedWrite(cpu, ref context, address => (byte)(cpu.A & cpu.X & GetHighByteMask(address)));
        }

        /// <summary>
        /// Handles the tas absolute y operation.
        /// </summary>
        public static bool TasAbsoluteY(Cpu6510 cpu, ref InstructionContext context)
        {
            byte value = (byte)(cpu.A & cpu.X);
            cpu.SP = value;
            return AbsoluteIndexedMaskedWrite(cpu, ref context, cpu.Y, address => (byte)(value & GetHighByteMask(address)));
        }

        /// <summary>
        /// Handles the shy absolute x operation.
        /// </summary>
        public static bool ShyAbsoluteX(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedMaskedWrite(cpu, ref context, cpu.X, address => (byte)(cpu.Y & GetHighByteMask(address)));
        }

        /// <summary>
        /// Handles the shx absolute y operation.
        /// </summary>
        public static bool ShxAbsoluteY(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedMaskedWrite(cpu, ref context, cpu.Y, address => (byte)(cpu.X & GetHighByteMask(address)));
        }

        /// <summary>
        /// Handles the ahx absolute y operation.
        /// </summary>
        public static bool AhxAbsoluteY(Cpu6510 cpu, ref InstructionContext context)
        {
            return AbsoluteIndexedMaskedWrite(cpu, ref context, cpu.Y, address => (byte)(cpu.A & cpu.X & GetHighByteMask(address)));
        }

        /// <summary>
        /// Handles the nop implied operation.
        /// </summary>
        public static bool NopImplied(Cpu6510 cpu, ref InstructionContext context)
        {
            return true;
        }

        /// <summary>
        /// Handles the illegal operation.
        /// </summary>
        public static bool Illegal(Cpu6510 cpu, ref InstructionContext context)
        {
            cpu.Jam();
            return true;
        }

        /// <summary>
        /// Handles the zero page read operation.
        /// </summary>
        private static bool ZeroPageRead(Cpu6510 cpu, ref InstructionContext context, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the absolute read operation.
        /// </summary>
        private static bool AbsoluteRead(Cpu6510 cpu, ref InstructionContext context, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read(context.Address);
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the indirect zero page x read operation.
        /// </summary>
        private static bool IndirectZeroPageXRead(Cpu6510 cpu, ref InstructionContext context, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((byte)context.Address);
                    context.Address = (byte)(context.Address + cpu.X);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Operand = cpu.Read(context.Address2);
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the immediate read operation.
        /// </summary>
        private static bool ImmediateRead(Cpu6510 cpu, ref InstructionContext context, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the zero page indexed read operation.
        /// </summary>
        private static bool ZeroPageIndexedRead(Cpu6510 cpu, ref InstructionContext context, byte index, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((byte)context.Address);
                    context.Address = (byte)(context.Address + index);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read((byte)context.Address);
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the zero page write operation.
        /// </summary>
        private static bool ZeroPageWrite(Cpu6510 cpu, ref InstructionContext context, byte value)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.Write((byte)context.Address, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the zero page indexed write operation.
        /// </summary>
        private static bool ZeroPageIndexedWrite(Cpu6510 cpu, ref InstructionContext context, byte index, byte value)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((byte)context.Address);
                    context.Address = (byte)(context.Address + index);
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Write((byte)context.Address, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the absolute indexed read operation.
        /// </summary>
        private static bool AbsoluteIndexedRead(Cpu6510 cpu, ref InstructionContext context, byte index, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.Address2 = context.Address;
                    context.Address = (ushort)(context.Address + index);
                    context.PageCrossed = !IsSamePage(context.Address2, context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    if (context.PageCrossed)
                    {
                        cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                        context.StepIndex++;
                        return false;
                    }

                    context.Operand = cpu.Read(context.Address);
                    apply(context.Operand);
                    return true;
                case 3:
                    context.Operand = cpu.Read(context.Address);
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the absolute write operation.
        /// </summary>
        private static bool AbsoluteWrite(Cpu6510 cpu, ref InstructionContext context, byte value)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Write(context.Address, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the absolute indexed write operation.
        /// </summary>
        private static bool AbsoluteIndexedWrite(Cpu6510 cpu, ref InstructionContext context, byte index, byte value)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.Address2 = context.Address;
                    context.Address = (ushort)(context.Address + index);
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.Write(context.Address, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles absolute indexed writes whose value depends on the final effective address high byte.
        /// </summary>
        private static bool AbsoluteIndexedMaskedWrite(Cpu6510 cpu, ref InstructionContext context, byte index, System.Func<ushort, byte> valueFactory)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.Address2 = context.Address;
                    context.Address = (ushort)(context.Address + index);
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.Write(context.Address, valueFactory(context.Address));
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the indirect zero page y read operation.
        /// </summary>
        private static bool IndirectZeroPageYRead(Cpu6510 cpu, ref InstructionContext context, System.Action<byte> apply)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.Address = (ushort)(context.Address2 + cpu.Y);
                    context.PageCrossed = !IsSamePage(context.Address2, context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    if (context.PageCrossed)
                    {
                        cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                        context.StepIndex++;
                        return false;
                    }

                    context.Operand = cpu.Read(context.Address);
                    apply(context.Operand);
                    return true;
                case 4:
                    context.Operand = cpu.Read(context.Address);
                    apply(context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the indirect zero page x write operation.
        /// </summary>
        private static bool IndirectZeroPageXWrite(Cpu6510 cpu, ref InstructionContext context, byte value)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((byte)context.Address);
                    context.Address = (byte)(context.Address + cpu.X);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.StepIndex++;
                    return false;
                case 4:
                    cpu.Write(context.Address2, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the indirect zero page y write operation.
        /// </summary>
        private static bool IndirectZeroPageYWrite(Cpu6510 cpu, ref InstructionContext context, byte value)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.Address = (ushort)(context.Address2 + cpu.Y);
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                    context.StepIndex++;
                    return false;
                case 4:
                    cpu.Write(context.Address, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles indirect zero page y writes whose value depends on the final effective address high byte.
        /// </summary>
        private static bool IndirectZeroPageYMaskedWrite(Cpu6510 cpu, ref InstructionContext context, System.Func<ushort, byte> valueFactory)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.Address = (ushort)(context.Address2 + cpu.Y);
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                    context.StepIndex++;
                    return false;
                case 4:
                    cpu.Write(context.Address, valueFactory(context.Address));
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the zero page modify operation.
        /// </summary>
        private static bool ZeroPageModify(Cpu6510 cpu, ref InstructionContext context, System.Func<byte, byte> modify)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Operand = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.Write((byte)context.Address, context.Operand);
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Operand = modify(context.Operand);
                    cpu.Write((byte)context.Address, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the zero page indexed modify operation.
        /// </summary>
        private static bool ZeroPageIndexedModify(Cpu6510 cpu, ref InstructionContext context, byte index, System.Func<byte, byte> modify)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((byte)context.Address);
                    context.Address = (byte)(context.Address + index);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.Write((byte)context.Address, context.Operand);
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Operand = modify(context.Operand);
                    cpu.Write((byte)context.Address, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the absolute modify operation.
        /// </summary>
        private static bool AbsoluteModify(Cpu6510 cpu, ref InstructionContext context, System.Func<byte, byte> modify)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Operand = cpu.Read(context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.Write(context.Address, context.Operand);
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Operand = modify(context.Operand);
                    cpu.Write(context.Address, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the absolute indexed modify operation.
        /// </summary>
        private static bool AbsoluteIndexedModify(Cpu6510 cpu, ref InstructionContext context, byte index, System.Func<byte, byte> modify)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address |= (ushort)(cpu.Read(cpu.PC) << 8);
                    cpu.PC++;
                    context.Address2 = context.Address;
                    context.Address = (ushort)(context.Address + index);
                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Operand = cpu.Read(context.Address);
                    context.StepIndex++;
                    return false;
                case 4:
                    cpu.Write(context.Address, context.Operand);
                    context.StepIndex++;
                    return false;
                case 5:
                    context.Operand = modify(context.Operand);
                    cpu.Write(context.Address, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the indirect zero page x modify operation.
        /// </summary>
        private static bool IndirectZeroPageXModify(Cpu6510 cpu, ref InstructionContext context, System.Func<byte, byte> modify)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    cpu.DummyRead((byte)context.Address);
                    context.Address = (byte)(context.Address + cpu.X);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 3:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Operand = cpu.Read(context.Address2);
                    context.StepIndex++;
                    return false;
                case 5:
                    cpu.Write(context.Address2, context.Operand);
                    context.StepIndex++;
                    return false;
                case 6:
                    context.Operand = modify(context.Operand);
                    cpu.Write(context.Address2, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Handles the indirect zero page y modify operation.
        /// </summary>
        private static bool IndirectZeroPageYModify(Cpu6510 cpu, ref InstructionContext context, System.Func<byte, byte> modify)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Address = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address2 = cpu.Read((byte)context.Address);
                    context.StepIndex++;
                    return false;
                case 2:
                    context.Address2 |= (ushort)(cpu.Read((byte)(context.Address + 1)) << 8);
                    context.Address = (ushort)(context.Address2 + cpu.Y);
                    context.StepIndex++;
                    return false;
                case 3:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address2, context.Address));
                    context.StepIndex++;
                    return false;
                case 4:
                    context.Operand = cpu.Read(context.Address);
                    context.StepIndex++;
                    return false;
                case 5:
                    cpu.Write(context.Address, context.Operand);
                    context.StepIndex++;
                    return false;
                case 6:
                    context.Operand = modify(context.Operand);
                    cpu.Write(context.Address, context.Operand);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Reads mode01.
        /// </summary>
        private static bool ReadMode01(Cpu6510 cpu, ref InstructionContext context, int mode, System.Action<byte> apply)
        {
            switch (mode)
            {
                case 0:
                    return IndirectZeroPageXRead(cpu, ref context, apply);
                case 1:
                    return ZeroPageRead(cpu, ref context, apply);
                case 2:
                    return ImmediateRead(cpu, ref context, apply);
                case 3:
                    return AbsoluteRead(cpu, ref context, apply);
                case 4:
                    return IndirectZeroPageYRead(cpu, ref context, apply);
                case 5:
                    return ZeroPageIndexedRead(cpu, ref context, cpu.X, apply);
                case 6:
                    return AbsoluteIndexedRead(cpu, ref context, cpu.Y, apply);
                case 7:
                    return AbsoluteIndexedRead(cpu, ref context, cpu.X, apply);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the store mode01 operation.
        /// </summary>
        private static bool StoreMode01(Cpu6510 cpu, ref InstructionContext context, int mode, byte value)
        {
            switch (mode)
            {
                case 0:
                    return IndirectZeroPageXWrite(cpu, ref context, value);
                case 1:
                    return ZeroPageWrite(cpu, ref context, value);
                case 3:
                    return AbsoluteWrite(cpu, ref context, value);
                case 4:
                    return IndirectZeroPageYWrite(cpu, ref context, value);
                case 5:
                    return ZeroPageIndexedWrite(cpu, ref context, cpu.X, value);
                case 6:
                    return AbsoluteIndexedWrite(cpu, ref context, cpu.Y, value);
                case 7:
                    return AbsoluteIndexedWrite(cpu, ref context, cpu.X, value);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the shift mode02 operation.
        /// </summary>
        private static bool ShiftMode02(Cpu6510 cpu, ref InstructionContext context, int mode, System.Func<byte, byte> shift)
        {
            switch (mode)
            {
                case 1:
                    return ZeroPageModify(cpu, ref context, shift);
                case 2:
                    cpu.A = shift(cpu.A);
                    return true;
                case 3:
                    return AbsoluteModify(cpu, ref context, shift);
                case 5:
                    return ZeroPageIndexedModify(cpu, ref context, cpu.X, shift);
                case 7:
                    return AbsoluteIndexedModify(cpu, ref context, cpu.X, shift);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the store mode02 operation.
        /// </summary>
        private static bool StoreMode02(Cpu6510 cpu, ref InstructionContext context, int mode, byte value)
        {
            switch (mode)
            {
                case 1:
                    return ZeroPageWrite(cpu, ref context, value);
                case 3:
                    return AbsoluteWrite(cpu, ref context, value);
                case 5:
                    return ZeroPageIndexedWrite(cpu, ref context, cpu.Y, value);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Loads mode02.
        /// </summary>
        private static bool LoadMode02(Cpu6510 cpu, ref InstructionContext context, int mode)
        {
            switch (mode)
            {
                case 0:
                    return ImmediateRead(cpu, ref context, value =>
                    {
                        cpu.X = value;
                        cpu.SetNZ(cpu.X);
                    });
                case 1:
                    return ZeroPageRead(cpu, ref context, value =>
                    {
                        cpu.X = value;
                        cpu.SetNZ(cpu.X);
                    });
                case 3:
                    return AbsoluteRead(cpu, ref context, value =>
                    {
                        cpu.X = value;
                        cpu.SetNZ(cpu.X);
                    });
                case 5:
                    return ZeroPageIndexedRead(cpu, ref context, cpu.Y, value =>
                    {
                        cpu.X = value;
                        cpu.SetNZ(cpu.X);
                    });
                case 7:
                    return AbsoluteIndexedRead(cpu, ref context, cpu.Y, value =>
                    {
                        cpu.X = value;
                        cpu.SetNZ(cpu.X);
                    });
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the modify mode02 operation.
        /// </summary>
        private static bool ModifyMode02(Cpu6510 cpu, ref InstructionContext context, int mode, System.Func<byte, byte> modify)
        {
            switch (mode)
            {
                case 1:
                    return ZeroPageModify(cpu, ref context, modify);
                case 3:
                    return AbsoluteModify(cpu, ref context, modify);
                case 5:
                    return ZeroPageIndexedModify(cpu, ref context, cpu.X, modify);
                case 7:
                    return AbsoluteIndexedModify(cpu, ref context, cpu.X, modify);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Reads mode03.
        /// </summary>
        private static bool ReadMode03(Cpu6510 cpu, ref InstructionContext context, int mode, System.Action<byte> apply)
        {
            switch (mode)
            {
                case 0:
                    return IndirectZeroPageXRead(cpu, ref context, apply);
                case 1:
                    return ZeroPageRead(cpu, ref context, apply);
                case 3:
                    return AbsoluteRead(cpu, ref context, apply);
                case 4:
                    return IndirectZeroPageYRead(cpu, ref context, apply);
                case 5:
                    return ZeroPageIndexedRead(cpu, ref context, cpu.Y, apply);
                case 7:
                    return AbsoluteIndexedRead(cpu, ref context, cpu.Y, apply);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the store mode03 operation.
        /// </summary>
        private static bool StoreMode03(Cpu6510 cpu, ref InstructionContext context, int mode, byte value)
        {
            switch (mode)
            {
                case 0:
                    return IndirectZeroPageXWrite(cpu, ref context, value);
                case 1:
                    return ZeroPageWrite(cpu, ref context, value);
                case 3:
                    return AbsoluteWrite(cpu, ref context, value);
                case 5:
                    return ZeroPageIndexedWrite(cpu, ref context, cpu.Y, value);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the modify mode03 operation.
        /// </summary>
        private static bool ModifyMode03(Cpu6510 cpu, ref InstructionContext context, int mode, System.Func<byte, byte> modify)
        {
            switch (mode)
            {
                case 0:
                    return IndirectZeroPageXModify(cpu, ref context, modify);
                case 1:
                    return ZeroPageModify(cpu, ref context, modify);
                case 3:
                    return AbsoluteModify(cpu, ref context, modify);
                case 4:
                    return IndirectZeroPageYModify(cpu, ref context, modify);
                case 5:
                    return ZeroPageIndexedModify(cpu, ref context, cpu.X, modify);
                case 6:
                    return AbsoluteIndexedModify(cpu, ref context, cpu.Y, modify);
                case 7:
                    return AbsoluteIndexedModify(cpu, ref context, cpu.X, modify);
                default:
                    return Illegal(cpu, ref context);
            }
        }

        /// <summary>
        /// Handles the branch operation.
        /// </summary>
        private static bool Branch(Cpu6510 cpu, ref InstructionContext context, bool condition)
        {
            switch (context.StepIndex)
            {
                case 0:
                    context.Operand = cpu.Read(cpu.PC);
                    cpu.PC++;
                    context.BranchTaken = condition;
                    if (!condition)
                    {
                        return true;
                    }

                    context.StepIndex++;
                    return false;
                case 1:
                    context.Address = cpu.PC;
                    context.Address2 = (ushort)(cpu.PC + (sbyte)context.Operand);
                    context.PageCrossed = !IsSamePage(context.Address, context.Address2);
                    cpu.DummyRead(cpu.PC);
                    if (!context.PageCrossed)
                    {
                        cpu.PC = context.Address2;
                        return true;
                    }

                    context.StepIndex++;
                    return false;
                case 2:
                    cpu.DummyRead(GetPageWrappedAddress(context.Address, context.Address2));
                    cpu.PC = context.Address2;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Returns whether same page is true.
        /// </summary>
        private static bool IsSamePage(ushort left, ushort right)
        {
            return (left & 0xFF00) == (right & 0xFF00);
        }

        /// <summary>
        /// Gets the page wrapped address value.
        /// </summary>
        private static ushort GetPageWrappedAddress(ushort baseAddress, ushort indexedAddress)
        {
            return (ushort)((baseAddress & 0xFF00) | (indexedAddress & 0x00FF));
        }

        /// <summary>
        /// Gets the high-byte mask used by the unstable NMOS store opcodes.
        /// </summary>
        private static byte GetHighByteMask(ushort address)
        {
            return (byte)(((address >> 8) + 1) & 0xFF);
        }
    }
}
