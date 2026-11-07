// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace C64Emulator.Core
{
    /// <summary>
    /// Black-box comparison of selected ROM calls on the same 6510 emulator.
    /// Reference ROM bytes remain in the user's local installation.
    /// </summary>
    public static class OpenRomAbiAudit
    {
        private sealed class Probe
        {
            public string Name;
            public ushort Entry;
            public byte A;
            public byte X;
            public byte Y;
            public byte Status = 0x24;
            public ushort[] Memory = Array.Empty<ushort>();
            public ushort[] SetupAddresses = Array.Empty<ushort>();
            public byte[] SetupValues = Array.Empty<byte>();
        }

        private sealed class Result
        {
            public string State;
            public int Cycles;
            public byte A;
            public byte X;
            public byte Y;
            public byte Status;
            public byte[] Memory;

            public string Describe()
            {
                if (State != "RETURN")
                {
                    return State;
                }

                string memory = Memory.Length == 0 ? "-" : BitConverter.ToString(Memory);
                return string.Format("{0}cy A={1:X2} X={2:X2} Y={3:X2} P={4:X2} RAM={5}",
                    Cycles, A, X, Y, Status, memory);
            }
        }

        private static readonly Probe[] Probes =
        {
            new Probe { Name = "CINT", Entry = 0xFF81,
                Memory = new ushort[] { 0x0400, 0x00D3, 0x00D6 } },
            new Probe { Name = "IOINIT", Entry = 0xFF84 },
            new Probe { Name = "RAMTAS", Entry = 0xFF87,
                Memory = new ushort[] { 0x0000, 0x0001, 0x0281, 0x0282 } },
            new Probe { Name = "RESTOR", Entry = 0xFF8A,
                Memory = new ushort[] { 0x0314, 0x0315, 0x0330, 0x0331 } },
            new Probe { Name = "VECTOR/get", Entry = 0xFF8D, X = 0, Y = 4, Status = 0x25,
                Memory = new ushort[] { 0x0400, 0x0401, 0x041E, 0x041F } },
            new Probe { Name = "VECTOR/set", Entry = 0xFF8D, X = 0, Y = 4,
                SetupAddresses = new ushort[] { 0x0400, 0x0401, 0x041E, 0x041F },
                SetupValues = new byte[] { 0x34, 0x12, 0x78, 0x56 },
                Memory = new ushort[] { 0x0314, 0x0315, 0x0332, 0x0333 } },
            new Probe { Name = "SETMSG/zero", Entry = 0xFF90, A = 0, Memory = new ushort[] { 0x009D } },
            new Probe { Name = "SETMSG/one", Entry = 0xFF90, A = 1, Memory = new ushort[] { 0x009D } },
            new Probe { Name = "SETMSG/mask", Entry = 0xFF90, A = 0x55, Memory = new ushort[] { 0x009D } },
            new Probe { Name = "READST", Entry = 0xFFB7 },
            new Probe { Name = "READST/EOI", Entry = 0xFFB7,
                SetupAddresses = new ushort[] { 0x0090 }, SetupValues = new byte[] { 0x40 },
                Memory = new ushort[] { 0x0090 } },
            new Probe { Name = "SETLFS", Entry = 0xFFBA, A = 4, X = 8, Y = 1,
                Memory = new ushort[] { 0x00B8, 0x00BA, 0x00B9 } },
            new Probe { Name = "SETNAM", Entry = 0xFFBD, A = 3, X = 0, Y = 0x20,
                Memory = new ushort[] { 0x00B7, 0x00BB, 0x00BC } },
            new Probe { Name = "MEMBOT/get", Entry = 0xFF9C, Status = 0x25,
                SetupAddresses = new ushort[] { 0x0281, 0x0282 }, SetupValues = new byte[] { 0, 8 } },
            new Probe { Name = "MEMBOT/set", Entry = 0xFF9C, X = 0, Y = 0x10,
                Memory = new ushort[] { 0x0281, 0x0282 } },
            new Probe { Name = "MEMTOP/get", Entry = 0xFF99, Status = 0x25,
                SetupAddresses = new ushort[] { 0x0283, 0x0284 }, SetupValues = new byte[] { 0, 0xA0 } },
            new Probe { Name = "MEMTOP/set", Entry = 0xFF99, X = 0, Y = 0x90,
                Memory = new ushort[] { 0x0283, 0x0284 } },
            new Probe { Name = "SETTMO", Entry = 0xFFA2, A = 0x55,
                Memory = new ushort[] { 0x0285 } },
            new Probe { Name = "OPEN/no-device", Entry = 0xFFC0,
                Memory = new ushort[] { 0x0090, 0x0340 } },
            new Probe { Name = "CLOSE/missing", Entry = 0xFFC3, A = 7,
                Memory = new ushort[] { 0x0340 } },
            new Probe { Name = "CHKIN/missing", Entry = 0xFFC6, X = 7,
                Memory = new ushort[] { 0x0099 } },
            new Probe { Name = "CHKOUT/missing", Entry = 0xFFC9, X = 7,
                Memory = new ushort[] { 0x009A } },
            new Probe { Name = "CLRCHN", Entry = 0xFFCC,
                Memory = new ushort[] { 0x0099, 0x009A } },
            new Probe { Name = "CHROUT/A", Entry = 0xFFD2, A = 0x41,
                Memory = new ushort[] { 0x0400, 0x00D3 } },
            new Probe { Name = "LOAD/no-device", Entry = 0xFFD5,
                Memory = new ushort[] { 0x0090 } },
            new Probe { Name = "SAVE/no-device", Entry = 0xFFD8,
                Memory = new ushort[] { 0x0090 } },
            new Probe { Name = "SETTIM", Entry = 0xFFDB, A = 1, X = 2, Y = 3,
                Memory = new ushort[] { 0x00A0, 0x00A1, 0x00A2 } },
            new Probe { Name = "RDTIM", Entry = 0xFFDE },
            new Probe { Name = "STOP", Entry = 0xFFE1 },
            new Probe { Name = "GETIN", Entry = 0xFFE4 },
            new Probe { Name = "CLALL", Entry = 0xFFE7,
                Memory = new ushort[] { 0x0099, 0x009A, 0x0340 } },
            new Probe { Name = "UDTIM", Entry = 0xFFEA,
                Memory = new ushort[] { 0x00A0, 0x00A1, 0x00A2 } },
            new Probe { Name = "SCREEN", Entry = 0xFFED },
            new Probe { Name = "PLOT/set", Entry = 0xFFF0, X = 12, Y = 20,
                Memory = new ushort[] { 0x00D3, 0x00D6 } },
            new Probe { Name = "PLOT/get", Entry = 0xFFF0, Status = 0x25,
                SetupAddresses = new ushort[] { 0x00D3, 0x00D6 },
                SetupValues = new byte[] { 15, 7 } },
            new Probe { Name = "IOBASE", Entry = 0xFFF3 },
            new Probe { Name = "private E10C/CHROUT", Entry = 0xE10C, A = 0x41,
                Memory = new ushort[] { 0x0400, 0x00D3 } },
            new Probe { Name = "private E118/CHKOUT", Entry = 0xE118, X = 7 },
            new Probe { Name = "private E11E/CHKIN", Entry = 0xE11E, X = 7 },
            new Probe { Name = "private E124/GETIN", Entry = 0xE124 },
            new Probe { Name = "private RESTOR", Entry = 0xFD15 },
            new Probe { Name = "private CLRSCR", Entry = 0xE544,
                Memory = new ushort[] { 0x0400, 0x0401 } },
        };

        public static string Run(string openRomDirectory)
        {
            var output = new StringBuilder();
            output.AppendLine("Open ROM ABI audit: same 6510 core, CPU-only cycles; not hardware timing.");
            output.AppendLine("Reference ROMs are read from the local installation; no reference bytes are exported.");
            int bothReturned = 0;
            int sameRegistersAndMemory = 0;
            int sameCycles = 0;
            foreach (Probe probe in Probes)
            {
                Result open = Call(probe, openRomDirectory);
                Result reference = Call(probe, null);
                bool returned = open.State == "RETURN" && reference.State == "RETURN";
                if (returned)
                {
                    bothReturned++;
                    bool same = open.A == reference.A && open.X == reference.X &&
                        open.Y == reference.Y && open.Status == reference.Status &&
                        Equal(open.Memory, reference.Memory);
                    if (same) sameRegistersAndMemory++;
                    if (open.Cycles == reference.Cycles) sameCycles++;
                }

                output.Append(probe.Name).Append(" $").Append(probe.Entry.ToString("X4"))
                    .Append(" | open: ").Append(open.Describe())
                    .Append(" | reference: ").Append(reference.Describe()).AppendLine();
            }

            output.Append("Both returned: ").Append(bothReturned).Append('/').Append(Probes.Length)
                .Append("; exact register/RAM signature: ").Append(sameRegistersAndMemory)
                .Append("; same CPU cycle count: ").Append(sameCycles).AppendLine();
            output.AppendLine(AuditBasicVariableLayout(openRomDirectory));
            output.Append(AuditBasicFloatingPoint(openRomDirectory));
            output.Append(AuditBasicFloatPrinting(openRomDirectory));
            return output.ToString();
        }

        public static string ProbeFloatingPoint(string openRomDirectory, string expression,
            int cyclesPerExpression = 600000)
        {
            return AuditBasicFloatingPoint(openRomDirectory, new[] { expression }, cyclesPerExpression);
        }

        public static string AuditBasicMathCases(string openRomDirectory, string caseFile,
            int cyclesPerExpression = 600000, int parallelism = 1)
        {
            var expressions = new List<string>();
            foreach (string line in System.IO.File.ReadAllLines(caseFile))
            {
                string expression = line.Trim();
                if (expression.Length == 0 || expression.StartsWith("#", StringComparison.Ordinal))
                    continue;
                expressions.Add(expression);
            }
            return "BASIC math cases: " + expressions.Count + " expressions from " + caseFile +
                Environment.NewLine + AuditBasicFloatingPoint(openRomDirectory, expressions.ToArray(),
                    cyclesPerExpression, parallelism);
        }

        public static string AuditBasicMathGrid(string openRomDirectory, int parallelism = 1)
        {
            string[] positiveArguments =
            {
                "0", ".01", ".1", ".5", ".9", "1", "1.1", "2", "3",
                "4", "10", "100", "1E-20", "1E20", "-1"
            };
            string[] signedArguments =
            {
                "-1000", "-100", "-10", "-2", "-1", "-.9", "-.5",
                "-.1", "-1E-20", "0", "1E-20", ".1", ".3", ".5",
                ".9", "1", "2", "10", "100", "1000"
            };
            var expressions = new List<string>();
            foreach (string name in new[] { "SQR", "LOG", "EXP" })
                foreach (string argument in positiveArguments)
                    expressions.Add(name + "(" + argument + ")");
            foreach (string name in new[] { "SIN", "COS", "TAN", "ATN" })
                foreach (string argument in signedArguments)
                    expressions.Add(name + "(" + argument + ")");
            return "BASIC math grid: " + expressions.Count +
                " deterministic function/argument pairs" + Environment.NewLine +
                AuditBasicFloatingPoint(openRomDirectory, expressions.ToArray(), 600000, parallelism);
        }

        public static string ProbeBasicCommand(string openRomDirectory, string command)
        {
            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            using (var open = new C64System(C64Model.Pal, options, openRomDirectory))
            using (var reference = new C64System(C64Model.Pal, options, null))
            {
                WaitForBasicReady(open);
                WaitForBasicReady(reference);
                byte[] openInitialRnd = ReadRam(open, 0x008B, 5);
                byte[] referenceInitialRnd = ReadRam(reference, 0x008B, 5);
                open.EnqueuePetsciiText(command + "\r");
                reference.EnqueuePetsciiText(command + "\r");
                open.RunCycles(1000000);
                reference.RunCycles(1000000);
                return command + " | open=" + BitConverter.ToString(ReadVariableA(open)) +
                    " rnd=" + BitConverter.ToString(ReadRam(open, 0x008B, 5)) +
                    " fac=" + BitConverter.ToString(ReadRam(open, 0x03E0, 6)) +
                    " guard=" + BitConverter.ToString(ReadRam(open, 0x03EF, 1)) +
                    " initial=" + BitConverter.ToString(openInitialRnd) +
                    " status=" + BasicResultStatus(open) + " screen=" +
                    OpenRomVerifier.ReadScreenRows(open) + Environment.NewLine +
                    command + " | reference=" + BitConverter.ToString(ReadVariableA(reference)) +
                    " rnd=" + BitConverter.ToString(ReadRam(reference, 0x008B, 5)) +
                    " initial=" + BitConverter.ToString(referenceInitialRnd) +
                    " status=" + BasicResultStatus(reference) + " screen=" +
                    OpenRomVerifier.ReadScreenRows(reference) + Environment.NewLine;
            }
        }

        public static string ProbeRndArgument(string openRomDirectory, string command)
        {
            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            using (var open = new C64System(C64Model.Pal, options, openRomDirectory))
            using (var reference = new C64System(C64Model.Pal, options, null))
            {
                WaitForBasicReady(open);
                WaitForBasicReady(reference);
                open.EnqueuePetsciiText(command + "\r");
                reference.EnqueuePetsciiText(command + "\r");
                return command + " | open=" + CaptureRndArgument(open, 0xFC04) +
                    " reference=" + CaptureRndArgument(reference, 0xE097);
            }
        }

        private static string CaptureRndArgument(C64System system, ushort entry)
        {
            for (int cycles = 0; cycles < 1000000; cycles++)
            {
                if (system.Cpu.PC == entry && system.Cpu.State == CpuState.FetchOpcode)
                    return BitConverter.ToString(ReadRam(system, entry == 0xFC04 ? (ushort)0x03E0 : (ushort)0x0061, 16)) +
                        " cia=" + system.Peek(0xDC04).ToString("X2") + "-" +
                        system.Peek(0xDC05).ToString("X2") + "-" +
                        system.Peek(0xDC08).ToString("X2") + "-" +
                        system.Peek(0xDC09).ToString("X2");
                system.RunCycles(1);
            }
            return "NO_ENTRY";
        }

        private static byte[] ReadRam(C64System system, ushort start, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < count; index++)
                result[index] = system.PeekRam((ushort)(start + index));
            return result;
        }

        public static string FuzzFloatingPoint(string openRomDirectory)
        {
            string[] numbers =
            {
                "0", ".0001", ".01", ".1", ".2", ".3", ".5", ".9",
                "1", "1.5", "2", "3", "7", "10", "100", "1000",
                "1E-20", "1E20", "-1E-20", "-.1", "-1", "-2", "-10"
            };
            string[] binary = { "+", "-", "*", "/" };
            string[] functions = { "SQR", "LOG", "EXP", "SIN", "COS", "TAN", "ATN" };
            var random = new Random(0xC64);
            var expressions = new List<string>();
            for (int index = 0; index < 90; index++)
            {
                string left = numbers[random.Next(numbers.Length)];
                string right = numbers[random.Next(numbers.Length)];
                string op = binary[random.Next(binary.Length)];
                expressions.Add("(" + left + ")" + op + "(" + right + ")");
            }
            for (int index = 0; index < 42; index++)
            {
                string function = functions[index % functions.Length];
                string number = numbers[random.Next(numbers.Length)];
                expressions.Add(function + "(" + number + ")");
            }
            for (int index = 0; index < 18; index++)
            {
                string left = numbers[random.Next(numbers.Length)];
                string right = numbers[random.Next(numbers.Length)];
                string op = index % 3 == 0 ? ">" : index % 3 == 1 ? "<" : "=";
                expressions.Add("(" + left + ")" + op + "(" + right + ")");
            }
            return AuditBasicFloatingPoint(openRomDirectory, expressions.ToArray());
        }

        public static string AuditRndSequences(string openRomDirectory,
            int stepsPerSeed = 64)
        {
            if (stepsPerSeed < 1 || stepsPerSeed > 1024)
                throw new ArgumentOutOfRangeException(nameof(stepsPerSeed));
            var output = new StringBuilder();
            output.AppendLine("RNDX five-byte sequences on the same PAL emulator; reference ROM bytes are not exported.");
            foreach (string seed in new[] { "boot", "-1", "-.1", "-2", "-.5" })
            {
                var options = C64AccuracyOptions.Compatibility;
                options.EnableLoadHack = false;
                options.EnableKernalIecHooks = false;
                using (var open = new C64System(C64Model.Pal, options, openRomDirectory))
                using (var reference = new C64System(C64Model.Pal, options, null))
                {
                    WaitForBasicReady(open);
                    WaitForBasicReady(reference);
                    output.AppendLine("seed=" + seed);
                    if (seed != "boot")
                        AuditRndStep(open, reference, "A=RND(" + seed + ")", 0, output);
                    int exact = 0;
                    int firstMismatch = 0;
                    for (int step = 1; step <= stepsPerSeed; step++)
                    {
                        if (AuditRndStep(open, reference, "A=RND(1)", step, output))
                            exact++;
                        else if (firstMismatch == 0)
                            firstMismatch = step;
                    }
                    output.Append("exact=").Append(exact).Append('/').Append(stepsPerSeed)
                        .Append(" first-mismatch=")
                        .Append(firstMismatch == 0 ? "none" : firstMismatch.ToString())
                        .AppendLine();
                }
            }
            return output.ToString();
        }

        public static string AuditRs232BaudTimers(string openRomDirectory)
        {
            var output = new StringBuilder();
            output.AppendLine("RS-232 OPEN PAL timer latches; reference ROM bytes are not exported.");
            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            foreach (byte code in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
            {
                using (var open = new C64System(C64Model.Pal, options, openRomDirectory))
                using (var reference = new C64System(C64Model.Pal, options, null))
                {
                    WaitForBasicReady(open);
                    WaitForBasicReady(reference);
                    bool ownCarry;
                    bool referenceCarry;
                    byte ownA;
                    byte referenceA;
                    byte[] own = OpenRs232AndReadTimer(open, code,
                        out ownCarry, out ownA);
                    byte[] original = OpenRs232AndReadTimer(reference, code,
                        out referenceCarry, out referenceA);
                    output.Append("  control=").Append(code)
                        .Append(" open=").Append(BitConverter.ToString(own))
                        .Append(" reference=").Append(BitConverter.ToString(original))
                        .Append(" exact=").Append(Equal(own, original))
                        .Append(" carry=").Append(ownCarry).Append('/')
                        .Append(referenceCarry)
                        .Append(" A=").Append(ownA.ToString("X2")).Append('/')
                        .Append(referenceA.ToString("X2")).AppendLine();
                }
            }
            return output.ToString();
        }

        private static byte[] OpenRs232AndReadTimer(C64System system, byte control,
            out bool carry, out byte a)
        {
            system.PokeRam(0x0200, control);
            system.PokeRam(0x0201, 0);
            system.PokeRam(0x0202, 0x06);
            system.PokeRam(0x0203, 0x06);
            system.PokeRam(0x00B7, control == 0 ? (byte)4 : (byte)1);
            system.PokeRam(0x00BB, 0);
            system.PokeRam(0x00BC, 2);
            system.PokeRam(0x00B8, 4);
            system.PokeRam(0x00BA, 2);
            system.PokeRam(0x00B9, 0);
            for (int cycle = 0; cycle < 1000 && system.Cpu.State != CpuState.FetchOpcode;
                cycle++) system.Tick();
            if (system.Cpu.State != CpuState.FetchOpcode)
                throw new InvalidOperationException("CPU did not reach an opcode boundary");
            system.PokeRam(0x01FE, 0xFF);
            system.PokeRam(0x01FF, 0x0F);
            system.Cpu.SP = 0xFD;
            system.Cpu.A = 0;
            system.Cpu.X = 0;
            system.Cpu.Y = 0;
            system.Cpu.PC = 0xFFC0;
            for (int cycle = 0; cycle < 12000000; cycle++)
            {
                system.Tick();
                if (system.Cpu.PC == 0x1000 &&
                    system.Cpu.State == CpuState.FetchOpcode)
                {
                    carry = (system.Cpu.SR & 1) != 0;
                    a = system.Cpu.A;
                    return ReadRam(system, 0x0299, 2);
                }
            }
            throw new InvalidOperationException("RS-232 OPEN did not return");
        }

        private static bool AuditRndStep(C64System open, C64System reference,
            string command, int step, StringBuilder output)
        {
            open.EnqueuePetsciiText(command + "\r");
            reference.EnqueuePetsciiText(command + "\r");
            if (step <= 2)
            {
                output.AppendLine("    open intermediates: " +
                    CaptureRndIntermediates(open, 0xFC3D, 0xFC62, 0x03E0, 0x03EF));
                output.AppendLine("    reference intermediates: " +
                    CaptureRndIntermediates(reference, 0xE0CC, 0xE0D3, 0x0061, 0x0070));
            }
            else
            {
                open.RunCycles(600000);
                reference.RunCycles(600000);
            }
            byte[] left = ReadRam(open, 0x008B, 5);
            byte[] right = ReadRam(reference, 0x008B, 5);
            bool exact = Equal(left, right);
            output.Append("  ").Append(step.ToString("D2")).Append(' ')
                .Append(command).Append(" | open=").Append(BitConverter.ToString(left))
                .Append(" reference=").Append(BitConverter.ToString(right))
                .Append(" exact=").Append(exact).AppendLine();
            return exact;
        }

        private static string CaptureRndIntermediates(C64System system,
            ushort afterMultiply, ushort afterAdd, ushort fac, ushort guard)
        {
            string multiply = "not reached";
            string add = "not reached";
            for (int cycles = 0; cycles < 600000; cycles++)
            {
                if (system.Cpu.State == CpuState.FetchOpcode)
                {
                    if (system.Cpu.PC == afterMultiply)
                        multiply = BitConverter.ToString(ReadRam(system, fac, 6)) +
                            " guard=" + system.PeekRam(guard).ToString("X2") +
                            (fac == 0x03E0 ? " product-low=" +
                                BitConverter.ToString(ReadRam(system, 0x03F4, 2)) : "");
                    if (system.Cpu.PC == afterAdd)
                    {
                        add = BitConverter.ToString(ReadRam(system, fac, 6)) +
                            " guard=" + system.PeekRam(guard).ToString("X2");
                        system.RunCycles(600000 - cycles);
                        break;
                    }
                }
                system.RunCycles(1);
            }
            return "multiply=" + multiply + " add=" + add;
        }

        private static string AuditBasicFloatPrinting(string openRomDirectory)
        {
            string[] expressions =
            {
                "1/3", ".1+.2", "1.23456789", "9999999", "10000000",
                ".001", ".0001", ".00001", "1E+20", "1E-20",
                "1E+38", "1E-38", "LOG(2)", "EXP(1)",
                "SIN(1)", "COS(1)", "TAN(1)", "ATN(1)"
            };
            var output = new StringBuilder();
            output.AppendLine("BASIC PRINT floating-point text (independent ROMs):");
            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            foreach (string expression in expressions)
            {
                using (var open = new C64System(C64Model.Pal, options, openRomDirectory))
                using (var reference = new C64System(C64Model.Pal, options, null))
                {
                    WaitForBasicReady(open);
                    WaitForBasicReady(reference);
                    string command = "PRINT " + expression + "\r";
                    open.EnqueuePetsciiText(command);
                    reference.EnqueuePetsciiText(command);
                    open.RunCycles(2000000);
                    reference.RunCycles(2000000);
                    output.Append("  ").Append(expression).Append(" | open=")
                        .Append(LastPrintLine(open)).Append(" reference=")
                        .Append(LastPrintLine(reference)).AppendLine();
                }
            }
            return output.ToString();
        }

        private static string LastPrintLine(C64System system)
        {
            string[] rows = OpenRomVerifier.ReadScreenRows(system).Split(" | ");
            for (int index = rows.Length - 1; index >= 0; index--)
            {
                if (rows[index].Contains("READY."))
                {
                    if (index == 0) return "<missing>";
                    string previous = rows[index - 1];
                    int separator = previous.IndexOf(':');
                    return separator >= 0 ? previous.Substring(separator + 1).Trim() : previous;
                }
            }
            return "<pending> " + OpenRomVerifier.ReadScreenRows(system);
        }

        private static string AuditBasicFloatingPoint(string openRomDirectory,
            string[] selected = null, int cyclesPerExpression = 600000, int parallelism = 1)
        {
            string[] expressions = selected ?? new[]
            {
                "1.5+2.25", "5.5-2.25", "7/2", "1/3", ".1+.2",
                "1E+20", "1E-20", "1E+38", "1E-38", "1E-39",
                "INT(-1.5)", "INT(65536.5)", "VAL(\"1.25\")",
                "2^3", "2^-1", "2^0.5", "2^3^2", "(-2)^3", "-2^2",
                "0^0.5", "SQR(2)", "SQR(.01)", "1-.1", ".3-.2",
                ".3-.1", "1/10", "2/3", "1E+20+1", "1E+20-1",
                "1E-38/10", ".999999999", "-1.000000001",
                "LOG(1)", "LOG(2)", "LOG(10)", "EXP(0)",
                "EXP(1)", "EXP(-1)", "SIN(0)", "SIN(1)",
                "COS(0)", "COS(1)", "TAN(1)", "ATN(0)",
                "ATN(1)", "ATN(2)", "SQR(4)"
            };
            var output = new StringBuilder();
            output.AppendLine("BASIC floating-point variable results (five bytes, independent ROMs):");
            try
            {
                parallelism = Math.Max(1, Math.Min(parallelism, 2));
                var lines = new string[expressions.Length];
                if (parallelism > 1)
                {
                    Parallel.For(0, expressions.Length,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                        index => lines[index] = AuditBasicFloatingPointCase(openRomDirectory,
                            expressions[index], selected == null ? 2000000 : cyclesPerExpression));
                }
                else
                {
                    for (int index = 0; index < expressions.Length; index++)
                        lines[index] = AuditBasicFloatingPointCase(openRomDirectory,
                            expressions[index], selected == null ? 2000000 : cyclesPerExpression);
                }
                foreach (string line in lines) output.AppendLine(line);
            }
            catch (Exception ex)
            {
                output.Append("  ERROR ").Append(ex.GetType().Name).Append(' ')
                    .Append(ex.Message).AppendLine();
            }
            return output.ToString();
        }

        private static string AuditBasicFloatingPointCase(string openRomDirectory,
            string expression, int cycles)
        {
            var output = new StringBuilder();
            {
                var options = C64AccuracyOptions.Compatibility;
                options.EnableLoadHack = false;
                options.EnableKernalIecHooks = false;
                // Independent workers must not contend for the host audio device.
                using (var open = new C64System(C64Model.Pal, options, openRomDirectory, false))
                using (var reference = new C64System(C64Model.Pal, options, null, false))
                {
                    WaitForBasicReady(open);
                    WaitForBasicReady(reference);
                    string command = "A=" + expression + "\r";
                    open.EnqueuePetsciiText(command);
                    reference.EnqueuePetsciiText(command);
                    open.RunCycles(cycles);
                    reference.RunCycles(cycles);
                    byte[] left = ReadVariableA(open);
                    byte[] right = ReadVariableA(reference);
                    string openStatus = BasicResultStatus(open);
                    string referenceStatus = BasicResultStatus(reference);
                    bool exact = Equal(left, right) && openStatus == referenceStatus;
                    output.Append("  ").Append(expression).Append(" | open=")
                        .Append(BitConverter.ToString(left)).Append(" reference=")
                        .Append(BitConverter.ToString(right)).Append(" exact=")
                        .Append(exact).Append(" status=").Append(openStatus)
                        .Append('/').Append(referenceStatus);
                    if (!exact)
                    {
                        if (openStatus == "VALUE" && referenceStatus == "VALUE" &&
                            left[0] != 0 && left[0] == right[0] &&
                            (left[1] & 0x80) == (right[1] & 0x80))
                        {
                            long openMantissa = ((long)(left[1] | 0x80) << 24) |
                                ((long)left[2] << 16) | ((long)left[3] << 8) | left[4];
                            long referenceMantissa = ((long)(right[1] | 0x80) << 24) |
                                ((long)right[2] << 16) | ((long)right[3] << 8) | right[4];
                            long ulpDifference = openMantissa - referenceMantissa;
                            if ((left[1] & 0x80) != 0) ulpDifference = -ulpDifference;
                            output.Append(" ulp=").Append(ulpDifference);
                        }
                        output.Append(" openPC=").Append(open.Cpu.PC.ToString("X4"))
                            .Append(" screen=").Append(OpenRomVerifier.ReadScreenRows(open));
                    }
                }
            }
            return output.ToString();
        }

        private static void WaitForBasicReady(C64System system)
        {
            for (int cycles = 0; cycles < 12000000; cycles += 100000)
            {
                if (OpenRomVerifier.ScreenHasLine(system, "READY.")) return;
                system.RunCycles(100000);
            }
            throw new InvalidOperationException("BASIC did not reach READY.");
        }

        private static byte[] ReadVariableA(C64System system)
        {
            int start = system.PeekRam(0x2D) | system.PeekRam(0x2E) << 8;
            int end = system.PeekRam(0x2F) | system.PeekRam(0x30) << 8;
            var result = new byte[5];
            if (end - start < 7 || system.PeekRam((ushort)start) != 'A') return result;
            for (int index = 0; index < 5; index++)
            {
                result[index] = system.PeekRam((ushort)(start + 2 + index));
            }
            return result;
        }

        private static string BasicResultStatus(C64System system)
        {
            string screen = OpenRomVerifier.ReadScreenRows(system);
            if (screen.Contains("?OVERFLOW") && screen.Contains("ERROR")) return "OVERFLOW";
            if (screen.Contains("?DIVISION BY ZERO") && screen.Contains("ERROR")) return "DIVISION_BY_ZERO";
            if (screen.Contains("?ILLEGAL QUANTITY") && screen.Contains("ERROR")) return "ILLEGAL_QUANTITY";
            if (screen.Contains("?SYNTAX") && screen.Contains("ERROR")) return "SYNTAX";
            int start = system.PeekRam(0x2D) | system.PeekRam(0x2E) << 8;
            int end = system.PeekRam(0x2F) | system.PeekRam(0x30) << 8;
            return end - start >= 7 && system.PeekRam((ushort)start) == 'A' ?
                "VALUE" : "PENDING";
        }

        private static string AuditBasicVariableLayout(string openRomDirectory)
        {
            try
            {
                byte[] open = ReadBasicVariables(openRomDirectory);
                byte[] reference = ReadBasicVariables(null);
                return "BASIC variable RAM layout (A=1:B=-3:C%=7:D=1234): open=" +
                    BitConverter.ToString(open) + " reference=" +
                    BitConverter.ToString(reference) + " exact=" + Equal(open, reference);
            }
            catch (Exception ex)
            {
                return "BASIC variable RAM layout: ERROR " + ex.GetType().Name;
            }
        }

        private static byte[] ReadBasicVariables(string openRomDirectory)
        {
            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            using (var system = new C64System(C64Model.Pal, options, openRomDirectory))
            {
                bool ready = false;
                for (int cycles = 0; cycles < 12000000; cycles += 100000)
                {
                    if (OpenRomVerifier.ScreenHasLine(system, "READY."))
                    {
                        ready = true;
                        break;
                    }
                    system.RunCycles(100000);
                }
                if (!ready)
                {
                    throw new InvalidOperationException("BASIC did not reach READY.");
                }

                system.EnqueuePetsciiText("A=1:B=-3:C%=7:D=1234\r");
                system.RunCycles(1200000);
                int start = system.PeekRam(0x2D) | (system.PeekRam(0x2E) << 8);
                int end = system.PeekRam(0x2F) | (system.PeekRam(0x30) << 8);
                if (end - start != 28)
                {
                    throw new InvalidOperationException("Unexpected BASIC variable count.");
                }

                var result = new byte[28];
                for (int index = 0; index < result.Length; index++)
                {
                    result[index] = system.PeekRam((ushort)(start + index));
                }
                return result;
            }
        }

        private static Result Call(Probe probe, string openRomDirectory)
        {
            try
            {
                var harness = new CpuTraceHarness();
                harness.Reset(0x0200);
                harness.Bus.LoadRoms(null, openRomDirectory);
                Result initialization = Execute(harness, 0xFF8A, 0, 0, 0, 0x24,
                    Array.Empty<ushort>());
                if (initialization.State != "RETURN")
                {
                    return new Result { State = "RESTOR " + initialization.State };
                }

                for (int index = 0; index < probe.SetupAddresses.Length; index++)
                {
                    harness.Bus.WriteRam(probe.SetupAddresses[index], probe.SetupValues[index]);
                }

                return Execute(harness, probe.Entry, probe.A, probe.X, probe.Y,
                    probe.Status, probe.Memory);
            }
            catch (Exception ex)
            {
                return new Result { State = "ERROR " + ex.GetType().Name };
            }
        }

        private static Result Execute(CpuTraceHarness harness, ushort entry, byte a,
            byte x, byte y, byte status, ushort[] memoryAddresses)
        {
            harness.Bus.WriteRam(0x0200, 0x20);
            harness.Bus.WriteRam(0x0201, (byte)entry);
            harness.Bus.WriteRam(0x0202, (byte)(entry >> 8));
            harness.Bus.WriteRam(0x0203, 0xEA);
            harness.Cpu.PC = 0x0200;
            harness.Cpu.A = a;
            harness.Cpu.X = x;
            harness.Cpu.Y = y;
            harness.Cpu.SR = status;
            harness.Cpu.SP = 0xFD;

            for (int cycle = 1; cycle <= 100000; cycle++)
            {
                harness.Cpu.Tick();
                if (harness.Cpu.State == CpuState.Jammed)
                {
                    return new Result { State = "JAM" };
                }

                if (harness.Cpu.PC == 0x0203 && harness.Cpu.State == CpuState.FetchOpcode)
                {
                    var memory = new byte[memoryAddresses.Length];
                    for (int index = 0; index < memory.Length; index++)
                    {
                        memory[index] = harness.Bus.ReadRam(memoryAddresses[index]);
                    }

                    return new Result
                    {
                        State = "RETURN", Cycles = cycle, A = harness.Cpu.A,
                        X = harness.Cpu.X, Y = harness.Cpu.Y,
                        Status = harness.Cpu.SR, Memory = memory
                    };
                }
            }

            return new Result { State = "TIMEOUT" };
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++)
            {
                if (a[index] != b[index]) return false;
            }
            return true;
        }
    }
}
