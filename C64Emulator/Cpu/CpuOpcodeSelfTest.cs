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

namespace C64Emulator.Core
{
    /// <summary>
    /// Provides an internal smoke-test suite for 6510 opcode timing and selected undocumented opcode semantics.
    /// </summary>
    public static class CpuOpcodeSelfTest
    {
        private const ushort StartAddress = 0x0200;
        private const byte TestOperandLow = 0x40;
        private const byte TestOperandHigh = 0x20;
        private const byte TestIndex = 0x04;
        private const int KilCyclesBeforeJam = 2;

        private static readonly int[] ExpectedBaseCycles =
        {
             7, 6,-1, 8, 3, 3, 5, 5, 3, 2, 2, 2, 4, 4, 6, 6,
             2, 5,-1, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
             6, 6,-1, 8, 3, 3, 5, 5, 4, 2, 2, 2, 4, 4, 6, 6,
             2, 5,-1, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
             6, 6,-1, 8, 3, 3, 5, 5, 3, 2, 2, 2, 3, 4, 6, 6,
             2, 5,-1, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
             6, 6,-1, 8, 3, 3, 5, 5, 4, 2, 2, 2, 5, 4, 6, 6,
             2, 5,-1, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
             2, 6, 2, 6, 3, 3, 3, 3, 2, 2, 2, 2, 4, 4, 4, 4,
             2, 6,-1, 6, 4, 4, 4, 4, 2, 5, 2, 5, 5, 5, 5, 5,
             2, 6, 2, 6, 3, 3, 3, 3, 2, 2, 2, 2, 4, 4, 4, 4,
             2, 5,-1, 5, 4, 4, 4, 4, 2, 4, 2, 4, 4, 4, 4, 4,
             2, 6, 2, 8, 3, 3, 5, 5, 2, 2, 2, 2, 4, 4, 6, 6,
             2, 5,-1, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
             2, 6, 2, 8, 3, 3, 5, 5, 2, 2, 2, 2, 4, 4, 6, 6,
             2, 5,-1, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7
        };

        /// <summary>
        /// Runs the CPU self-test suite and returns the number of failed checks.
        /// </summary>
        public static int Run(TextWriter output)
        {
            if (output == null)
            {
                throw new ArgumentNullException("output");
            }

            var failures = new List<string>();
            VerifyOpcodeCycles(failures);
            VerifyInterruptCycles(failures);
            VerifyUnstableIllegalOpcodes(failures);

            output.WriteLine("CPU OPCODE SELF-TEST");
            output.WriteLine("Checked base cycles for all 256 opcodes, including KIL/JAM handling.");
            output.WriteLine("Checked IRQ/NMI cycle length and selected unstable illegal opcode masks.");

            if (failures.Count == 0)
            {
                output.WriteLine("Result: OK");
                return 0;
            }

            output.WriteLine("Result: FAILED");
            foreach (string failure in failures)
            {
                output.WriteLine(failure);
            }

            return failures.Count;
        }

        /// <summary>
        /// Verifies the base cycle count for each opcode without page crossings or taken branches.
        /// </summary>
        private static void VerifyOpcodeCycles(List<string> failures)
        {
            for (int opcode = 0; opcode < 256; opcode++)
            {
                int expectedCycles = ExpectedBaseCycles[opcode];
                CpuTraceHarness harness = CreateOpcodeHarness((byte)opcode);
                IReadOnlyList<CpuTraceEntry> trace = harness.TraceUntilInstructionCompletes(32);

                if (expectedCycles < 0)
                {
                    if (harness.Cpu.State != CpuState.Jammed)
                    {
                        failures.Add(string.Format("Opcode ${0:X2}: expected JAM state.", opcode));
                    }

                    if (trace.Count != KilCyclesBeforeJam)
                    {
                        failures.Add(string.Format("Opcode ${0:X2}: expected JAM after {1} cycles, got {2}.", opcode, KilCyclesBeforeJam, trace.Count));
                    }

                    continue;
                }

                if (harness.Cpu.State == CpuState.Jammed)
                {
                    failures.Add(string.Format("Opcode ${0:X2}: jammed unexpectedly after {1} cycles.", opcode, trace.Count));
                    continue;
                }

                if (trace.Count != expectedCycles)
                {
                    failures.Add(string.Format("Opcode ${0:X2}: expected {1} cycles, got {2}.", opcode, expectedCycles, trace.Count));
                }
            }
        }

        /// <summary>
        /// Verifies that hardware interrupts consume seven CPU cycles after being accepted.
        /// </summary>
        private static void VerifyInterruptCycles(List<string> failures)
        {
            CpuTraceHarness irqHarness = new CpuTraceHarness();
            irqHarness.Reset(StartAddress);
            irqHarness.Cpu.SR = 0x20;
            irqHarness.SetIrq(true);
            IReadOnlyList<CpuTraceEntry> irqTrace = irqHarness.TraceUntilInstructionCompletes(16);
            if (irqTrace.Count != 7)
            {
                failures.Add(string.Format("IRQ: expected 7 cycles, got {0}.", irqTrace.Count));
            }

            CpuTraceHarness nmiHarness = new CpuTraceHarness();
            nmiHarness.Reset(StartAddress);
            nmiHarness.SetNmi(true);
            IReadOnlyList<CpuTraceEntry> nmiTrace = nmiHarness.TraceUntilInstructionCompletes(16);
            if (nmiTrace.Count != 7)
            {
                failures.Add(string.Format("NMI: expected 7 cycles, got {0}.", nmiTrace.Count));
            }
        }

        /// <summary>
        /// Verifies deterministic approximations for the unstable NMOS undocumented opcodes.
        /// </summary>
        private static void VerifyUnstableIllegalOpcodes(List<string> failures)
        {
            VerifyRegisterResult(0x8B, 0xFF, 0x00, 0x00, 0xFF, 0xEE, 0xFF, 0x00, "XAA immediate", failures);
            VerifyRegisterResult(0xAB, 0xFF, 0x00, 0x00, 0xFF, 0xEE, 0xEE, 0x00, "LXA/LAX immediate", failures);
            VerifyMaskedStore(0x93, 0xFF, 0xFF, TestIndex, 0xFD, 0x2084, 0x21, "AHX (zp),Y", failures);
            VerifyMaskedStore(0x9B, 0xF7, 0xDF, TestIndex, 0xFD, 0x2044, 0x01, "TAS abs,Y", failures);
            VerifyMaskedStore(0x9C, 0x55, TestIndex, 0x55, 0xFD, 0x2044, 0x01, "SHY abs,X", failures);
            VerifyMaskedStore(0x9E, 0x55, 0x55, TestIndex, 0xFD, 0x2044, 0x01, "SHX abs,Y", failures);
            VerifyMaskedStore(0x9F, 0xF7, 0xDF, TestIndex, 0xFD, 0x2044, 0x01, "AHX abs,Y", failures);
        }

        /// <summary>
        /// Builds a prepared harness for measuring one opcode in a stable no-page-crossing setup.
        /// </summary>
        private static CpuTraceHarness CreateOpcodeHarness(byte opcode)
        {
            var harness = new CpuTraceHarness();
            harness.Reset(StartAddress);
            harness.LoadProgram(StartAddress, opcode, TestOperandLow, TestOperandHigh);
            PrepareCpuAndMemory(harness, opcode);
            return harness;
        }

        /// <summary>
        /// Initializes registers and memory so every addressing mode can complete deterministically.
        /// </summary>
        private static void PrepareCpuAndMemory(CpuTraceHarness harness, byte opcode)
        {
            harness.Cpu.A = 0x45;
            harness.Cpu.X = TestIndex;
            harness.Cpu.Y = TestIndex;
            harness.Cpu.SP = 0xFD;
            harness.Cpu.SR = GetStatusForUntakenBranch(opcode);

            SystemBus bus = harness.Bus;
            bus.WriteRam(TestOperandLow, 0x80);
            bus.WriteRam((byte)(TestOperandLow + 1), 0x20);
            bus.WriteRam((byte)(TestOperandLow + TestIndex), 0x80);
            bus.WriteRam((byte)(TestOperandLow + TestIndex + 1), 0x20);
            bus.WriteRam(0x2040, 0x33);
            bus.WriteRam(0x2044, 0x33);
            bus.WriteRam(0x2080, 0x33);
            bus.WriteRam(0x2084, 0x33);
            bus.WriteRam(0x01FE, 0x00);
            bus.WriteRam(0x01FF, 0x30);
            bus.WriteRam(0x0100, 0x24);
        }

        /// <summary>
        /// Returns a processor status value that keeps branch opcodes in their base two-cycle path.
        /// </summary>
        private static byte GetStatusForUntakenBranch(byte opcode)
        {
            switch (opcode)
            {
                case 0x10:
                    return 0xA4;
                case 0x30:
                    return 0x24;
                case 0x50:
                    return 0x64;
                case 0x70:
                    return 0x24;
                case 0x90:
                    return 0x25;
                case 0xB0:
                    return 0x24;
                case 0xD0:
                    return 0x26;
                case 0xF0:
                    return 0x24;
                default:
                    return 0x24;
            }
        }

        /// <summary>
        /// Verifies the register result of an immediate opcode.
        /// </summary>
        private static void VerifyRegisterResult(
            byte opcode,
            byte operand,
            byte initialA,
            byte initialY,
            byte initialX,
            byte expectedA,
            byte expectedX,
            byte expectedY,
            string label,
            List<string> failures)
        {
            var harness = new CpuTraceHarness();
            harness.Reset(StartAddress);
            harness.LoadProgram(StartAddress, opcode, operand, 0xEA);
            PrepareCpuAndMemory(harness, opcode);
            harness.Cpu.A = initialA;
            harness.Cpu.X = initialX;
            harness.Cpu.Y = initialY;
            harness.TraceUntilInstructionCompletes(8);

            if (harness.Cpu.A != expectedA || harness.Cpu.X != expectedX || harness.Cpu.Y != expectedY)
            {
                failures.Add(string.Format(
                    "{0}: expected A/X/Y={1:X2}/{2:X2}/{3:X2}, got {4:X2}/{5:X2}/{6:X2}.",
                    label,
                    expectedA,
                    expectedX,
                    expectedY,
                    harness.Cpu.A,
                    harness.Cpu.X,
                    harness.Cpu.Y));
            }
        }

        /// <summary>
        /// Verifies one of the high-byte-masked undocumented store opcodes.
        /// </summary>
        private static void VerifyMaskedStore(
            byte opcode,
            byte initialA,
            byte initialX,
            byte initialY,
            byte initialSp,
            ushort targetAddress,
            byte expectedValue,
            string label,
            List<string> failures)
        {
            var harness = new CpuTraceHarness();
            harness.Reset(StartAddress);
            harness.LoadProgram(StartAddress, opcode, TestOperandLow, TestOperandHigh);
            PrepareCpuAndMemory(harness, opcode);
            harness.Cpu.A = initialA;
            harness.Cpu.X = initialX;
            harness.Cpu.Y = initialY;
            harness.Cpu.SP = initialSp;
            harness.Bus.WriteRam(targetAddress, 0x00);
            harness.TraceUntilInstructionCompletes(16);

            byte actualValue = harness.Bus.ReadRam(targetAddress);
            if (actualValue != expectedValue)
            {
                failures.Add(string.Format("{0}: expected ${1:X2} at ${2:X4}, got ${3:X2}.", label, expectedValue, targetAddress, actualValue));
            }
        }
    }
}
