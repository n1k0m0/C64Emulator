// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Runs the generated ROM through the actual 6510, CIA, IEC and disk models.
    /// No original BASIC, KERNAL, character, or 1541 ROM is used for these checks.
    /// </summary>
    public static class OpenRomVerifier
    {
        private static readonly byte[] ProgramBytes = { 0xA9, 0x2A, 0x8D, 0x00, 0xC0, 0x60 };

        public static string Run(string directory)
        {
            byte[] basic = File.ReadAllBytes(Path.Combine(directory, "open-basic64.bin"));
            byte[] kernal = File.ReadAllBytes(Path.Combine(directory, "open-kernal64.bin"));
            byte[] characters = File.ReadAllBytes(Path.Combine(directory, "open-character-rom64.bin"));
            byte[] driveRom = File.ReadAllBytes(Path.Combine(directory, "open-1541-dos.bin"));
            Expect(basic.Length == 8192, "BASIC is exactly 8 KiB");
            Expect(kernal.Length == 8192, "KERNAL is exactly 8 KiB");
            Expect(characters.Length == 4096, "character ROM is exactly 4 KiB");
            Expect(driveRom.Length == 16384, "open 1541 bootstrap is exactly 16 KiB");
            Expect((driveRom[0x3FFC] | driveRom[0x3FFD] << 8) == 0xC000,
                "open 1541 reset vector");

            for (int address = 0xFF81; address <= 0xFFF3; address += 3)
            {
                byte opcode = kernal[address - 0xE000];
                Expect(opcode == 0x4C || opcode == 0x6C, "jump table entry $" + address.ToString("X4"));
            }
            ushort reset = (ushort)(kernal[0x1FFC] | kernal[0x1FFD] << 8);
            Expect(reset >= 0xE000 && reset < 0xFF81, "reset vector points into KERNAL");

            for (int set = 0; set < 2; set++)
            {
                int offset = set * 2048;
                for (int index = 0; index < 1024; index++)
                {
                    Expect(characters[offset + 1024 + index] == (byte)~characters[offset + index],
                        "inverse glyph at " + (offset + index));
                }
            }
            Expect(characters[64 * 8 + 3] != 0, "graphics code 64 is drawn");

            var options = C64AccuracyOptions.Compatibility;
            options.EnableLoadHack = false;
            options.EnableKernalIecHooks = false;
            Expect(reset == 0xE600, "Open Kernal 64 reset vector");
            VerifyStandaloneBasic(directory, options);
            VerifyDriveBootstrap(directory, options, driveRom);
            string boot = VerifyBoot(directory, options);
            VerifyAbi(directory, true);
            VerifyPrivateEntrypoints(directory, options);
            string timing = VerifyRomTiming(directory);
            VerifyTapeSignals(directory, options);
            VerifyTapeRecording(directory, options);
            VerifyTapeKernalLoad(directory, options);
            VerifyTapeKernalSave(directory, options);
            VerifyRs232Pins(directory, options);
            VerifyRs232Readst(directory, options);
            VerifyRs232Kernal(directory, options);
            VerifyRs232Framing(directory, options);
            VerifyOwnBasic(directory, options);
            VerifyFloatProductGuard(directory, options);
            VerifySmallExp(directory, options);
            string disk = VerifyIec(directory, options, true);
            return "PASS Open Basic 64 / Open Kernal 64 / Open Character Rom 64 / Open 1541 bootstrap" + Environment.NewLine +
                "ROM geometry, glyph inversion, standalone BASIC selection, reset, tokenized program editing, five-byte floating-point arithmetic/variables, math functions and STR$/VAL, " +
                "IF/THEN, GOSUB/RETURN, fractional FOR/NEXT, INPUT and READ/DATA, selected public and private KERNAL entries, IEC primitives, file channels, " +
                "BASIC GET#/INPUT#/PRINT#/VERIFY, PRG LOAD/SAVE persistence, TAP playback/recording, device-1 KERNAL LOAD/SAVE/VERIFY, " +
                "and selected PAL 50-2400-baud plus custom-timer RS-232 KERNAL I/O including 8N1, 7E1 and 5O2 verified." + Environment.NewLine +
                boot + Environment.NewLine + disk + Environment.NewLine +
                timing + Environment.NewLine;
        }

        private static string VerifyRomTiming(string directory)
        {
            int setlfs = VerifyCall(directory, 0xFFBA, 4, 8, 1, 0x24, h => { });
            int screen = VerifyCall(directory, 0xFFED, 0, 0, 0, 0x24, h => { });
            int restor = VerifyCall(directory, 0xFD15, 0, 0, 0, 0x24, h => { });
            // CPU-only regression baseline for the RC3 open ROM set (re-baselined when the
// ROM variant changes; not a Commodore hardware timing comparison).
            Expect(setlfs == 24 && screen == 19 && restor == 497,
                "own ROM CPU cycle regression baseline: " + setlfs + "," +
                screen + "," + restor);
            // These are repeatable CPU-only call counts, not a comparison to
            // Commodore ROM timing or a VIC/CIA phase-accurate machine trace.
            return "OWN ROM CPU cycles (JSR wrapper): SETLFS=" + setlfs +
                ", SCREEN=" + screen + ", private RESTOR=" + restor;
        }

        private static void VerifyDriveBootstrap(string directory, C64AccuracyOptions options,
            byte[] driveRom)
        {
            var bootOptions = options.Clone();
            bootOptions.RunDriveCpuContinuously = true;
            using (var system = new C64System(C64Model.Pal, bootOptions, directory))
            {
                Expect(system.HasDriveFirmware(8), "own 1541 ROM is mapped");
                Expect(system.PeekDriveMemory(8, 0xC000) == driveRom[0],
                    "drive CPU reads own ROM");
                Expect(system.GetDriveProgramCounter(8) == 0xC000,
                    "drive CPU starts at own reset vector");
                system.RunCycles(300);
                ushort pc = system.GetDriveProgramCounter(8);
                Expect(pc >= 0xC194 && pc <= 0xC197,
                    "own 1541 bootstrap reaches its command loop");
            }
            var driveBus = new IecBus();
            var drive = new Drive1541Hardware(driveBus.CreatePort("open-rom-job-test"),
                8, false, directory);
            drive.UploadMemory(0x0300, new byte[] { 0xA9, 0x2A, 0x8D, 0x00, 0x02, 0x60 });
            drive.UploadMemory(0x0000, new byte[] { 0xD0 });
            drive.WriteMemory(0x01FE, 0x93);
            drive.WriteMemory(0x01FF, 0xC1);
            drive.ExecuteAt(0xF2B0,
                new Drive1541Hardware.ExecuteContext(0, 0, 0, 0xFD, 0x24));
            for (int cycles = 0; cycles < 10000 && drive.ReadMemory(0x0000) == 0xD0;
                cycles++)
            {
                drive.Tick();
            }
            Expect(drive.ReadMemory(0x0200) == 0x2A && drive.ReadMemory(0x0000) == 1,
                "own 1541 $D0 job executes buffer code and returns status");
            var scheduledDrive = new Drive1541Hardware(
                driveBus.CreatePort("open-rom-scheduled-job-test"), 9, false, directory);
            scheduledDrive.RunCpuContinuously = true;
            for (int cycle = 0; cycle < 500; cycle++) scheduledDrive.Tick();
            scheduledDrive.UploadMemory(0x0300,
                new byte[] { 0xA9, 0x63, 0x8D, 0x01, 0x02, 0x60 });
            scheduledDrive.WriteMemory(0x0000, 0xD0);
            for (int cycle = 0; cycle < 20000 && scheduledDrive.ReadMemory(0x0000) == 0xD0;
                cycle++)
            {
                scheduledDrive.Tick();
            }
            Expect(scheduledDrive.ReadMemory(0x0201) == 0x63 &&
                scheduledDrive.ReadMemory(0x0000) == 1,
                "own 1541 $D0 job runs through the scheduled IRQ path");
            for (int cycle = 0; cycle < 200; cycle++) scheduledDrive.Tick();
            Expect(scheduledDrive.ProgramCounter >= 0xC194 &&
                scheduledDrive.ProgramCounter <= 0xC197,
                "own 1541 scheduled job returns to the idle loop");

            var bumpDrive = new Drive1541Hardware(
                driveBus.CreatePort("open-rom-bump-job-test"), 10, false, directory);
            bumpDrive.RunCpuContinuously = true;
            for (int cycle = 0; cycle < 500; cycle++) bumpDrive.Tick();
            Expect(bumpDrive.Bus.CurrentHalfTrack == 34,
                "open 1541 drive starts on its default half-track");
            bumpDrive.WriteMemory(0x0005, 0xC0);
            bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0005) == 0xC0,
                "open 1541 C0 job is queued for ROM execution");
            for (int cycle = 0; cycle < 20000 && bumpDrive.ReadMemory(0x0005) == 0xC0;
                cycle++) bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0005) == 1 &&
                bumpDrive.Bus.CurrentHalfTrack == 0,
                "open 1541 C0 job homes the head through the ROM and disk VIA");
            bumpDrive.WriteMemory(0x0005, 0xC1);
            bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0005) == 0xC1,
                "open 1541 C1 job is queued for ROM error handling");
            for (int cycle = 0; cycle < 10000 && bumpDrive.ReadMemory(0x0005) == 0xC1;
                cycle++) bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0005) == 0x0F,
                "open 1541 C1 job reports the absent second drive");
            bumpDrive.WriteMemory(0x000A, 20);
            bumpDrive.WriteMemory(0x000B, 18);
            bumpDrive.WriteMemory(0x0002, 0xB0);
            bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0002) == 0xB0,
                "open 1541 B0 job is queued for ROM execution");
            for (int cycle = 0; cycle < 30000 && bumpDrive.ReadMemory(0x0002) == 0xB0;
                cycle++) bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0002) == 1 &&
                bumpDrive.Bus.CurrentHalfTrack == 38 &&
                bumpDrive.ReadMemory(0x0018) == 20 &&
                bumpDrive.ReadMemory(0x0019) == 18,
                "open 1541 B0 job seeks to track 20 through the ROM and disk VIA");
            bumpDrive.WriteMemory(0x000A, 43);
            bumpDrive.WriteMemory(0x0002, 0xB0);
            for (int cycle = 0; cycle < 10000 && bumpDrive.ReadMemory(0x0002) == 0xB0;
                cycle++) bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0002) == 2 &&
                bumpDrive.Bus.CurrentHalfTrack == 38,
                "open 1541 B0 job rejects an invalid track without moving the head" +
                " (status=$" + bumpDrive.ReadMemory(0x0002).ToString("X2") +
                ", half-track=" + bumpDrive.Bus.CurrentHalfTrack +
                ", PC=$" + bumpDrive.ProgramCounter.ToString("X4") +
                ", SR=$" + bumpDrive.Cpu.SR.ToString("X2") +
                ", bus=" + bumpDrive.Bus.GetDiskDebugInfo() + ")");
            bumpDrive.WriteMemory(0x0002, 0xB1);
            for (int cycle = 0; cycle < 10000 && bumpDrive.ReadMemory(0x0002) == 0xB1;
                cycle++) bumpDrive.Tick();
            Expect(bumpDrive.ReadMemory(0x0002) == 0x0F,
                "open 1541 B1 job reports the absent second drive");

            var queuedDrive = new Drive1541Hardware(
                driveBus.CreatePort("open-rom-two-jobs-test"), 11, false, directory);
            queuedDrive.RunCpuContinuously = true;
            for (int cycle = 0; cycle < 500; cycle++) queuedDrive.Tick();
            queuedDrive.UploadMemory(0x0300,
                new byte[] { 0xA9, 0x42, 0x8D, 0x03, 0x02, 0x60 });
            queuedDrive.WriteMemory(0x0005, 0xC0);
            queuedDrive.WriteMemory(0x0000, 0xD0);
            for (int cycle = 0; cycle < 30000 &&
                (queuedDrive.ReadMemory(0x0005) == 0xC0 ||
                 queuedDrive.ReadMemory(0x0000) == 0xD0); cycle++)
                queuedDrive.Tick();
            Expect(queuedDrive.ReadMemory(0x0005) == 1 &&
                queuedDrive.ReadMemory(0x0000) == 1 &&
                queuedDrive.ReadMemory(0x0203) == 0x42 &&
                queuedDrive.Bus.CurrentHalfTrack == 0,
                "open 1541 ROM jobs in separate slots complete in sequence");
        }

        private static void VerifyRs232Pins(string directory, C64AccuracyOptions options)
        {
            var cia = new Cia2();
            Expect((cia.Read(0x01) & 0x01) != 0, "RS-232 RXD defaults high");
            cia.SetUserPortRxDHigh(false);
            Expect((cia.Read(0x01) & 0x01) == 0 && (cia.Read(0x0D) & 0x10) != 0,
                "RS-232 RXD drives PB0 and FLAG2");
            cia.SetUserPortRxDHigh(false);
            Expect((cia.Read(0x0D) & 0x10) == 0, "RXD steady low has no second FLAG edge");
            cia.SetUserPortRxDHigh(true);
            cia.SetUserPortRxDHigh(false);
            Expect((cia.Read(0x0D) & 0x10) != 0, "new RXD falling edge raises FLAG2");
            cia.Write(0x02, 0x04);
            cia.Write(0x00, 0xFB);
            Expect(!cia.UserPortTxDHigh, "RS-232 TXD follows PA2 low");
            cia.Write(0x00, 0xFF);
            Expect(cia.UserPortTxDHigh, "RS-232 TXD follows PA2 high");

            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                system.SetRs232RxDHigh(false);
                Expect((system.Peek(0xDD01) & 0x01) == 0 &&
                    (system.Peek(0xDD0D) & 0x10) != 0, "machine RS-232 input reaches CIA2");
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    {
                        system.SaveState(writer);
                    }
                    system.SetRs232RxDHigh(true);
                    stream.Position = 0;
                    using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                    {
                        system.LoadState(reader);
                    }
                    Expect((system.Peek(0xDD01) & 0x01) == 0,
                        "savestate restores RS-232 RXD pin");
                }
            }
        }

        private static void VerifyRs232Readst(string directory, C64AccuracyOptions options)
        {
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                system.PokeRam(0x00BA, 2);
                system.PokeRam(0x0297, 0xA5);
                system.PokeRam(0x0090, 0x5A);
                CallResult serial = InvokeKernal(system, 0xFFB7, 0);
                Expect(serial.A == 0xA5 && system.PeekRam(0x0297) == 0 &&
                    system.PeekRam(0x0090) == 0x5A,
                    "READST returns and clears RS-232 status for device 2");

                system.PokeRam(0x00BA, 8);
                system.PokeRam(0x0297, 0xA5);
                CallResult disk = InvokeKernal(system, 0xFFB7, 0);
                Expect(disk.A == 0x5A && system.PeekRam(0x0297) == 0xA5,
                    "READST preserves RS-232 status for another device");
            }
        }

        private static void VerifyRs232Kernal(string directory, C64AccuracyOptions options)
        {
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                SetFileParameters(system, 4, 2, 0);
                system.PokeRam(0x0200, 6); // 300 baud, eight data bits, one stop bit
                Expect(!InvokeKernal(system, 0xFFC0, 0).Carry, "RS-232 OPEN");
                Expect(!InvokeKernal(system, 0xFFC9, 0, 4).Carry, "RS-232 CHKOUT");
                var edges = new System.Collections.Generic.List<bool>();
                bool prior = system.Rs232TxDHigh;
                int transmitCycles = 0;
                CallResult sent = InvokeKernal(system, 0xFFD2, 0x41, onTick: s =>
                {
                    transmitCycles++;
                    bool current = s.Rs232TxDHigh;
                    if (current != prior)
                    {
                        edges.Add(current);
                        prior = current;
                    }
                });
                Expect(!sent.Carry && sent.A == 0x41 && transmitCycles > 30000 &&
                    transmitCycles < 38000 && edges.Count == 6 && !edges[0] &&
                    edges[1] && !edges[2] && edges[3] && !edges[4] && edges[5] &&
                    system.Rs232TxDHigh,
                    "RS-232 CHROUT emits 8N1 bits through CIA2 PA2 (cycles=" +
                    transmitCycles + ", edges=" + string.Join(",", edges) + ")");

                InvokeKernal(system, 0xFFCC, 0);
                Expect(!InvokeKernal(system, 0xFFC6, 0, 4).Carry, "RS-232 CHKIN");
                system.SetRs232RxDHigh(true);
                int receiveCycles = 0;
                const int bitCycles = 3284;
                CallResult received = InvokeKernal(system, 0xFFCF, 0, onTick: s =>
                {
                    receiveCycles++;
                    int bit = (receiveCycles - 1000) / bitCycles;
                    bool level = receiveCycles < 1000 || bit >= 9 ||
                        (bit > 0 && ((0x4B >> (bit - 1)) & 1) != 0);
                    if ((s.Peek(0xDD01) & 1) != (level ? 1 : 0))
                    {
                        s.SetRs232RxDHigh(level);
                    }
                });
                Expect(!received.Carry && received.A == 0x4B,
                    "RS-232 CHRIN samples 8N1 bits from CIA2 PB0 (A=$" +
                    received.A.ToString("X2") + ", cycles=" + receiveCycles + ")");
                system.SetRs232RxDHigh(true);
                Expect(InvokeKernal(system, 0xFFE4, 0).A == 0,
                    "RS-232 GETIN is nonblocking when idle");
                CallResult emptyStatus = InvokeKernal(system, 0xFFB7, 0);
                Expect(emptyStatus.A == 0x08 && system.PeekRam(0x0297) == 0,
                    "RS-232 idle GETIN reports and clears buffer-empty status");
                Expect(!InvokeKernal(system, 0xFFC3, 4).Carry, "RS-232 CLOSE");

                int[] controls = { 7, 8, 10, 9, 1, 2, 3, 4, 5 };
                int[] bitPeriods = { 1642, 821, 411, 548, 19706, 13136, 8956, 7320, 6568 };
                for (int index = 0; index < controls.Length; index++)
                {
                    SetFileParameters(system, 4, 2, 0);
                    system.PokeRam(0x0200, (byte)controls[index]);
                    Expect(!InvokeKernal(system, 0xFFC0, 0).Carry,
                        "RS-232 OPEN control " + controls[index]);
                    Expect(!InvokeKernal(system, 0xFFC9, 0, 4).Carry,
                        "RS-232 CHKOUT control " + controls[index]);
                    int cycles = 0;
                    bool priorLevel = system.Rs232TxDHigh;
                    var edgeCycles = new System.Collections.Generic.List<int>();
                    CallResult sentAtRate = InvokeKernal(system, 0xFFD2, 0x55,
                        onTick: s =>
                        {
                            cycles++;
                            bool level = s.Rs232TxDHigh;
                            if (level != priorLevel)
                            {
                                edgeCycles.Add(cycles);
                                priorLevel = level;
                            }
                        });
                    Expect(!sentAtRate.Carry && edgeCycles.Count == 10,
                        "RS-232 8N1 frame edges control " + controls[index]);
                    int interruptDelayedEdges = 0;
                    for (int edge = 1; edge < edgeCycles.Count; edge++)
                    {
                        int interval = edgeCycles[edge] - edgeCycles[edge - 1];
                        int tolerance = Math.Max(60, bitPeriods[index] / 15);
                        if (Math.Abs(interval - bitPeriods[index]) >= tolerance)
                        {
                            Expect(interval > bitPeriods[index] &&
                                interval < bitPeriods[index] + 400,
                                "RS-232 baud edge interval control " +
                                controls[index] + " edge " + edge +
                                " (cycles=" + interval + ", target=" +
                                bitPeriods[index] + ")");
                            interruptDelayedEdges++;
                        }
                    }
                    Expect(interruptDelayedEdges <= 1,
                        "RS-232 frame has at most one IRQ-delayed edge at control " +
                        controls[index]);
                    Expect(!InvokeKernal(system, 0xFFC6, 0, 4).Carry,
                        "RS-232 CHKIN control " + controls[index]);
                    system.SetRs232RxDHigh(true);
                    int receiveTicks = 0;
                    int period = bitPeriods[index];
                    CallResult rateReceive = InvokeKernal(system, 0xFFCF, 0, onTick: s =>
                    {
                        receiveTicks++;
                        int bit = (receiveTicks - 1000) / period;
                        bool level = receiveTicks < 1000 || bit >= 9 ||
                            (bit > 0 && ((0x4B >> (bit - 1)) & 1) != 0);
                        if ((s.Peek(0xDD01) & 1) != (level ? 1 : 0))
                        {
                            s.SetRs232RxDHigh(level);
                        }
                    });
                    Expect(!rateReceive.Carry && rateReceive.A == 0x4B,
                        "RS-232 receives at control " + controls[index] +
                        " (A=$" + rateReceive.A.ToString("X2") +
                        ", ST=$" + rateReceive.Status.ToString("X2") +
                        ", ticks=" + receiveTicks +
                        ", TX edges=" + string.Join(",", edgeCycles) + ")");
                    system.SetRs232RxDHigh(true);
                    InvokeKernal(system, 0xFFCC, 0);
                    Expect(!InvokeKernal(system, 0xFFC3, 4).Carry,
                        "RS-232 CLOSE control " + controls[index]);
                }

                SetFileParameters(system, 4, 2, 0);
                system.PokeRam(0x00B7, 0);
                Expect(!InvokeKernal(system, 0xFFC0, 0).Carry &&
                    system.PeekRam(0x0299) == 0xD4 &&
                    system.PeekRam(0x029A) == 0x0C,
                    "RS-232 default control selects 300 baud");
                Expect(!InvokeKernal(system, 0xFFC3, 4).Carry,
                    "RS-232 default CLOSE");

                SetFileParameters(system, 4, 2, 0);
                system.PokeRam(0x00B7, 4);
                system.PokeRam(0x0200, 0); // user-defined half-bit timer
                system.PokeRam(0x0201, 0);
                system.PokeRam(0x0202, 0x06);
                system.PokeRam(0x0203, 0x06);
                Expect(!InvokeKernal(system, 0xFFC0, 0).Carry &&
                    system.PeekRam(0x0299) == 0xD4 &&
                    system.PeekRam(0x029A) == 0x0C &&
                    system.PeekRam(0x029B) == 0x06 &&
                    system.PeekRam(0x029C) == 0x06,
                    "RS-232 user-defined 300-baud timer");
                Expect(!InvokeKernal(system, 0xFFC3, 4).Carry,
                    "RS-232 user-defined CLOSE");

                SetFileParameters(system, 4, 2, 0);
                system.PokeRam(0x00B7, 2);
                system.PokeRam(0x0200, 0x26); // 300 baud, seven data bits, one stop
                system.PokeRam(0x0201, 0x60); // even parity
                Expect(!InvokeKernal(system, 0xFFC0, 0).Carry, "RS-232 7E1 OPEN");
                Expect(!InvokeKernal(system, 0xFFC9, 0, 4).Carry, "RS-232 7E1 CHKOUT");
                var parityEdges = new System.Collections.Generic.List<bool>();
                bool lastParityLevel = system.Rs232TxDHigh;
                CallResult paritySent = InvokeKernal(system, 0xFFD2, 0x55,
                    onTick: s =>
                    {
                        bool level = s.Rs232TxDHigh;
                        if (level != lastParityLevel)
                        {
                            parityEdges.Add(level);
                            lastParityLevel = level;
                        }
                    });
                Expect(!paritySent.Carry && parityEdges.Count == 10 &&
                    !parityEdges[8] && parityEdges[9],
                    "RS-232 7E1 transmits an even parity bit");
                Expect(!InvokeKernal(system, 0xFFC6, 0, 4).Carry, "RS-232 7E1 CHKIN");
                system.SetRs232RxDHigh(true);
                int parityReceiveTicks = 0;
                CallResult parityReceived = InvokeKernal(system, 0xFFCF, 0,
                    onTick: s =>
                    {
                        parityReceiveTicks++;
                        int bit = (parityReceiveTicks - 1000) / bitCycles;
                        bool level = parityReceiveTicks < 1000 || bit >= 9 ||
                            (bit >= 1 && bit <= 7 && ((0x55 >> (bit - 1)) & 1) != 0);
                        if ((s.Peek(0xDD01) & 1) != (level ? 1 : 0))
                            s.SetRs232RxDHigh(level);
                    });
                Expect(!parityReceived.Carry && parityReceived.A == 0x55,
                    "RS-232 7E1 receives and validates even parity");
                system.SetRs232RxDHigh(true);
                int badParityTicks = 0;
                CallResult badParity = InvokeKernal(system, 0xFFCF, 0,
                    onTick: s =>
                    {
                        badParityTicks++;
                        int bit = (badParityTicks - 1000) / bitCycles;
                        bool level = badParityTicks < 1000 || bit >= 8 ||
                            (bit >= 1 && bit <= 7 && ((0x55 >> (bit - 1)) & 1) != 0);
                        if ((s.Peek(0xDD01) & 1) != (level ? 1 : 0))
                            s.SetRs232RxDHigh(level);
                    });
                Expect(badParity.Carry && (system.PeekRam(0x0297) & 0x01) != 0,
                    "RS-232 7E1 reports a parity error");
                system.SetRs232RxDHigh(true);
                InvokeKernal(system, 0xFFCC, 0);
                Expect(!InvokeKernal(system, 0xFFC3, 4).Carry, "RS-232 7E1 CLOSE");

                SetFileParameters(system, 4, 2, 0);
                system.PokeRam(0x00B7, 2);
                system.PokeRam(0x0200, 0xE6); // 300 baud, five data bits, two stops
                system.PokeRam(0x0201, 0x20); // odd parity
                Expect(!InvokeKernal(system, 0xFFC0, 0).Carry, "RS-232 5O2 OPEN");
                Expect(!InvokeKernal(system, 0xFFC9, 0, 4).Carry, "RS-232 5O2 CHKOUT");
                var shortWordEdges = new System.Collections.Generic.List<bool>();
                bool lastShortWordLevel = system.Rs232TxDHigh;
                int shortWordTicks = 0;
                CallResult shortWordSent = InvokeKernal(system, 0xFFD2, 0x15,
                    onTick: s =>
                    {
                        shortWordTicks++;
                        bool level = s.Rs232TxDHigh;
                        if (level != lastShortWordLevel)
                        {
                            shortWordEdges.Add(level);
                            lastShortWordLevel = level;
                        }
                    });
                Expect(!shortWordSent.Carry && shortWordEdges.Count == 8 &&
                    !shortWordEdges[6] && shortWordEdges[7] &&
                    shortWordTicks > 28000,
                    "RS-232 5O2 transmits five bits, odd parity and two stops");
                Expect(!InvokeKernal(system, 0xFFC6, 0, 4).Carry, "RS-232 5O2 CHKIN");
                system.SetRs232RxDHigh(true);
                int shortWordReceiveTicks = 0;
                CallResult shortWordReceived = InvokeKernal(system, 0xFFCF, 0,
                    onTick: s =>
                    {
                        shortWordReceiveTicks++;
                        int bit = (shortWordReceiveTicks - 1000) / bitCycles;
                        bool level = shortWordReceiveTicks < 1000 || bit >= 7 ||
                            (bit >= 1 && bit <= 5 && ((0x15 >> (bit - 1)) & 1) != 0);
                        if ((s.Peek(0xDD01) & 1) != (level ? 1 : 0))
                            s.SetRs232RxDHigh(level);
                    });
                Expect(!shortWordReceived.Carry && shortWordReceived.A == 0x15 &&
                    shortWordReceiveTicks > 28000,
                    "RS-232 5O2 receives five bits, odd parity and two stops");
                system.SetRs232RxDHigh(true);
                InvokeKernal(system, 0xFFCC, 0);
                Expect(!InvokeKernal(system, 0xFFC3, 4).Carry, "RS-232 5O2 CLOSE");
            }
        }

        private static void VerifyRs232Framing(string directory, C64AccuracyOptions options)
        {
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                SetFileParameters(system, 4, 2, 0);
                system.PokeRam(0x0200, 6); // 300 baud, eight data bits, one stop bit
                Expect(!InvokeKernal(system, 0xFFC0, 0).Carry, "RS-232 framing OPEN");
                Expect(!InvokeKernal(system, 0xFFC6, 0, 4).Carry, "RS-232 framing CHKIN");
                system.SetRs232RxDHigh(true);
                const int bitCycles = 3284;
                int framingTicks = 0;
                CallResult badFrame = InvokeKernal(system, 0xFFCF, 0, onTick: s =>
                {
                    framingTicks++;
                    int bit = (framingTicks - 1000) / bitCycles;
                    bool level = framingTicks < 1000 || bit >= 10 ||
                        (bit > 0 && bit <= 8 && ((0x4B >> (bit - 1)) & 1) != 0);
                    if ((s.Peek(0xDD01) & 1) != (level ? 1 : 0))
                        s.SetRs232RxDHigh(level);
                });
                Expect(badFrame.Carry && (system.PeekRam(0x0297) & 0x02) != 0,
                    "RS-232 CHRIN reports a low stop bit as framing error");
                system.SetRs232RxDHigh(true);
                CallResult framingStatus = InvokeKernal(system, 0xFFB7, 0);
                Expect((framingStatus.A & 0x02) != 0 && system.PeekRam(0x0297) == 0,
                    "READST returns and clears framing status");
                InvokeKernal(system, 0xFFCC, 0);
                Expect(!InvokeKernal(system, 0xFFC3, 4).Carry,
                    "RS-232 framing CLOSE");
            }
        }

        private static void VerifyTapeSignals(string directory, C64AccuracyOptions options)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "open-rom64-tape-" + Guid.NewGuid().ToString("N") + ".tap");
            byte[] tap = new byte[26];
            System.Text.Encoding.ASCII.GetBytes("C64-TAPE-RAW").CopyTo(tap, 0);
            tap[12] = 1;
            tap[16] = 6;
            tap[20] = 3; // 24 cycles
            tap[21] = 5; // 40 cycles
            tap[22] = 0; // exact, extended pulse
            tap[23] = 0;
            tap[24] = 16;
            tap[25] = 0; // 4096 cycles
            File.WriteAllBytes(path, tap);
            try
            {
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    Expect(system.MountMedia(path) == "TAP MOUNTED - PLAY PRESSED", "mount TAP");
                    system.PokeRam(0x0000, 0x2F);
                    system.PokeRam(0x0001, 0x37);
                    Expect((system.Peek(0x0001) & 0x10) == 0, "cassette PLAY sense is low");
                    system.RunCycles(100);
                    Expect(system.TapePulseIndex == 0, "motor-off pauses tape");
                    system.PokeRam(0x0001, 0x17);
                    system.Peek(0xDC0D);
                    system.RunCycles(23);
                    Expect(system.TapePulseIndex == 0, "first TAP pulse is cycle-timed");
                    system.RunCycles(1);
                    Expect(system.TapePulseIndex == 1 && (system.Peek(0xDC0D) & 0x10) != 0,
                        "first TAP pulse raises CIA1 FLAG");
                    system.RunCycles(20);
                    system.PokeRam(0x0001, 0x37);
                    system.RunCycles(100);
                    Expect(system.TapePulseIndex == 1, "motor pause retains pulse countdown");
                    system.PokeRam(0x0001, 0x17);
                    system.RunCycles(20);
                    Expect(system.TapePulseIndex == 2, "second TAP pulse resumes at correct cycle");

                    using (var stream = new MemoryStream())
                    {
                        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                        {
                            system.RunCycles(100);
                            system.SaveState(writer);
                        }
                        File.Delete(path);
                        system.RunCycles(3996);
                        Expect(system.TapePulseIndex == 3, "extended TAP pulse duration");
                        stream.Position = 0;
                        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                        {
                            system.LoadState(reader);
                        }
                        Expect(system.TapePulseIndex == 2, "savestate restores tape position");
                        system.RunCycles(3996);
                        Expect(system.TapePulseIndex == 3, "savestate restores partial pulse timing");
                    }

                    system.RewindTape();
                    system.SetTapePlayPressed(false);
                    Expect((system.Peek(0x0001) & 0x10) != 0, "released PLAY sense is high");
                    system.RunCycles(100);
                    Expect(system.TapePulseIndex == 0, "released PLAY stops tape");
                    system.SetTapePlayPressed(true);
                    system.RunCycles(24);
                    Expect(system.TapePulseIndex == 1, "rewind restarts first pulse");
                    Expect(system.EjectMedia() == "TAP EJECTED" &&
                        (system.Peek(0x0001) & 0x10) != 0,
                        "eject releases PLAY sense");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void VerifyTapeRecording(string directory, C64AccuracyOptions options)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "open-rom64-record-" + Guid.NewGuid().ToString("N") + ".tap");
            try
            {
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    Expect(system.StartTapeRecording(path) ==
                        "TAP RECORDING - RECORD PRESSED", "TAP recording starts");
                    system.PokeRam(0x0000, 0x2F);
                    system.PokeRam(0x0001, 0x17); // motor on, WRITE low
                    system.RunCycles(5);
                    system.PokeRam(0x0001, 0x1F); // first rising edge
                    system.RunCycles(1);
                    system.PokeRam(0x0001, 0x17);
                    system.RunCycles(23);
                    system.PokeRam(0x0001, 0x1F); // 24-cycle pulse
                    system.RunCycles(1);
                    system.PokeRam(0x0001, 0x17);
                    system.RunCycles(39);
                    system.PokeRam(0x0001, 0x1F); // 40-cycle pulse
                    system.RunCycles(1);
                    Expect(system.RecordedTapePulseCount == 2,
                        "cassette WRITE captures rising-edge pulse intervals");

                    using (var stream = new MemoryStream())
                    {
                        using (var writer = new BinaryWriter(stream,
                            System.Text.Encoding.UTF8, true)) system.SaveState(writer);
                        system.PokeRam(0x0001, 0x17);
                        system.RunCycles(50);
                        system.PokeRam(0x0001, 0x1F);
                        system.RunCycles(1);
                        Expect(system.RecordedTapePulseCount == 3,
                            "recording continues after snapshot");
                        stream.Position = 0;
                        using (var reader = new BinaryReader(stream,
                            System.Text.Encoding.UTF8, true)) system.LoadState(reader);
                        Expect(system.RecordedTapePulseCount == 2,
                            "savestate restores in-progress tape recording");
                    }

                    Expect(system.StopTapeRecording() == "TAP MOUNTED - PLAY PRESSED",
                        "stopping recording mounts the resulting TAP");
                    TapImage recorded = TapImage.Load(path);
                    Expect(recorded.PulseCount == 2 && recorded.GetPulseCycles(0) == 24 &&
                        recorded.GetPulseCycles(1) == 40,
                        "recorded TAP v1 preserves both pulse lengths");
                    system.RewindTape();
                    system.PokeRam(0x0001, 0x17);
                    system.RunCycles(24);
                    Expect(system.TapePulseIndex == 1, "recorded TAP plays first pulse");
                    system.RunCycles(40);
                    Expect(system.TapePulseIndex == 2, "recorded TAP plays second pulse");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void VerifyTapeKernalLoad(string directory, C64AccuracyOptions options)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "open-rom64-load-" + Guid.NewGuid().ToString("N") + ".tap");
            string headerRetryPath = Path.Combine(Path.GetTempPath(),
                "open-rom64-header-retry-" + Guid.NewGuid().ToString("N") + ".tap");
            string emptyTapePath = Path.Combine(Path.GetTempPath(),
                "open-rom64-empty-" + Guid.NewGuid().ToString("N") + ".tap");
            try
            {
                var pulses = new System.Collections.Generic.List<int>();
                var header = new byte[192];
                header[0] = 1;
                header[1] = 0x01;
                header[2] = 0x08;
                header[3] = 0x05;
                header[4] = 0x08;
                header[5] = (byte)'A';
                for (int i = 6; i < 21; i++) header[i] = 0x20;
                byte[] program = { 0xA9, 0x2A, 0x8D, 0x00 };
                AddTapeBlock(pulses, header, 0x89);
                AddTapeBlock(pulses, header, 0x09);
                AddTapeBlock(pulses, program, 0x89, 0x01);
                AddTapeBlock(pulses, program, 0x09);
                File.WriteAllBytes(path, TapImage.Encode(pulses, 985248));
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    Expect(system.MountMedia(path, 8).StartsWith("TAP MOUNTED",
                        StringComparison.Ordinal), "test TAP mounted");
                    SetFileParameters(system, 1, 1, 0);
                    CallResult loaded = InvokeKernal(system, 0xFFD5, 0, 0x00, 0x20);
                    Expect(!loaded.Carry && loaded.X == 0x04 && loaded.Y == 0x20 &&
                        (loaded.Status & 0x80) == 0,
                        "KERNAL tape LOAD relocates the PRG, A=" +
                        loaded.A.ToString("X2") + ", pulse=" + system.TapePulseIndex);
                    for (int i = 0; i < program.Length; i++)
                        Expect(system.PeekRam((ushort)(0x2000 + i)) == program[i],
                            "KERNAL tape LOAD byte " + i);
                    system.RewindTape();
                    CallResult verified = InvokeKernal(system, 0xFFD5, 1, 0x00, 0x20);
                    Expect(!verified.Carry && verified.X == 0x04 && verified.Y == 0x20,
                        "KERNAL tape VERIFY succeeds");
                }
                pulses.Clear();
                AddTapeBlock(pulses, header, 0x89, 0x01);
                AddTapeBlock(pulses, header, 0x09);
                AddTapeBlock(pulses, program, 0x89);
                AddTapeBlock(pulses, program, 0x09);
                File.WriteAllBytes(headerRetryPath, TapImage.Encode(pulses, 985248));
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    Expect(system.MountMedia(headerRetryPath, 8).StartsWith("TAP MOUNTED",
                        StringComparison.Ordinal), "header retry TAP mounted");
                    SetFileParameters(system, 1, 1, 0);
                    CallResult loaded = InvokeKernal(system, 0xFFD5, 0, 0x00, 0x20);
                    Expect(!loaded.Carry && loaded.X == 0x04 && loaded.Y == 0x20,
                        "KERNAL tape LOAD uses intact second header");
                    for (int i = 0; i < program.Length; i++)
                        Expect(system.PeekRam((ushort)(0x2000 + i)) == program[i],
                            "KERNAL tape header retry byte " + i);
                }
                File.WriteAllBytes(emptyTapePath, TapImage.Encode(Array.Empty<int>(), 985248));
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    Expect(system.MountMedia(emptyTapePath, 8).StartsWith("TAP MOUNTED",
                        StringComparison.Ordinal), "empty TAP mounted");
                    SetFileParameters(system, 1, 1, 0);
                    CallResult missing = InvokeKernal(system, 0xFFD5, 0, 0x00, 0x20);
                    CallResult status = InvokeKernal(system, 0xFFB7, 0);
                    Expect(missing.Carry && (missing.Status & 0x80) != 0 &&
                        (status.A & 0x80) != 0,
                        "KERNAL tape LOAD reports end of an empty TAP");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(headerRetryPath)) File.Delete(headerRetryPath);
                if (File.Exists(emptyTapePath)) File.Delete(emptyTapePath);
            }
        }

        private static void VerifyTapeKernalSave(string directory, C64AccuracyOptions options)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "open-rom64-save-" + Guid.NewGuid().ToString("N") + ".tap");
            try
            {
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    // Secondary bit 0 = 0 writes the BASIC (relocatable) header
                    // type; bit 0 = 1 would store an absolute header that LOAD
                    // honors at its own address.
                    SetFileParameters(system, 1, 1, 0);
                    byte[] program = { 0xA9, 0x2A, 0x8D, 0x00 };
                    for (int i = 0; i < program.Length; i++)
                        system.PokeRam((ushort)(0x2000 + i), program[i]);
                    system.PokeRam(0x002B, 0x00);
                    system.PokeRam(0x002C, 0x20);
                    Expect(system.StartTapeRecording(path) ==
                        "TAP RECORDING - RECORD PRESSED", "KERNAL SAVE TAP recording starts");
                    CallResult saved = InvokeKernal(system, 0xFFD8, 0x2B, 0x04, 0x20);
                    Expect(!saved.Carry, "KERNAL tape SAVE returns successfully");
                    Expect(system.StopTapeRecording() == "TAP MOUNTED - PLAY PRESSED",
                        "KERNAL tape SAVE mounts the recorded TAP");
                    TapImage image = TapImage.Load(path);
                    Expect(image.PulseCount > 10000,
                        "KERNAL tape SAVE emits leaders and duplicate blocks");
                    system.RewindTape();
                    SetFileParameters(system, 1, 1, 0);
                    CallResult loaded = InvokeKernal(system, 0xFFD5, 0, 0x00, 0x30);
                    Expect(!loaded.Carry && loaded.X == 0x04 && loaded.Y == 0x30,
                        "KERNAL tape SAVE/LOAD round trip: A=" + loaded.A.ToString("X2") +
                        " X=" + loaded.X.ToString("X2") +
                        " pulse=" + system.TapePulseIndex);
                    for (int i = 0; i < program.Length; i++)
                        Expect(system.PeekRam((ushort)(0x3000 + i)) == program[i],
                            "KERNAL tape SAVE/LOAD byte " + i);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void AddTapeBlock(System.Collections.Generic.List<int> pulses,
            byte[] payload, byte syncStart, byte checksumError = 0)
        {
            for (int i = 0; i < 50; i++) pulses.Add(384);
            for (int i = 0; i < 9; i++) AddTapeByte(pulses, (byte)(syncStart - i));
            byte checksum = 0;
            foreach (byte value in payload)
            {
                AddTapeByte(pulses, value);
                checksum ^= value;
            }
            AddTapeByte(pulses, (byte)(checksum ^ checksumError));
        }

        private static void AddTapeByte(System.Collections.Generic.List<int> pulses, byte value)
        {
            pulses.Add(688);
            pulses.Add(528);
            int parity = 1;
            for (int i = 0; i < 8; i++)
            {
                int bit = (value >> i) & 1;
                parity ^= bit;
                pulses.Add(bit == 0 ? 384 : 528);
                pulses.Add(bit == 0 ? 528 : 384);
            }
            pulses.Add(parity == 0 ? 384 : 528);
            pulses.Add(parity == 0 ? 528 : 384);
        }

        private static void VerifyStandaloneBasic(string directory, C64AccuracyOptions options)
        {
            string temporary = Path.Combine(Path.GetTempPath(),
                "open-rom64-basic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string basicPath = Path.Combine(temporary, "open-basic64.bin");
            string kernalPath = Path.Combine(temporary, "open-kernal64.bin");
            string characterPath = Path.Combine(temporary, "open-character-rom64.bin");
            string drivePath = Path.Combine(temporary, "open-1541-dos.bin");
            try
            {
                File.Copy(Path.Combine(directory, "open-kernal64.bin"), kernalPath);
                File.Copy(Path.Combine(directory, "open-character-rom64.bin"), characterPath);
                File.Copy(Path.Combine(directory, "open-1541-dos.bin"), drivePath);
                bool rejectedMissingBasic = false;
                try
                {
                    using (var ignored = new C64System(C64Model.Pal, options, temporary)) { }
                }
                catch (FileNotFoundException)
                {
                    rejectedMissingBasic = true;
                }
                Expect(rejectedMissingBasic, "open ROM mode rejects missing own BASIC");
                File.Copy(Path.Combine(directory, "open-basic64.bin"), basicPath);
                using (var system = new C64System(C64Model.Pal, options, temporary))
                {
                    Expect(system.Peek(0xA000) == File.ReadAllBytes(basicPath)[0],
                        "own BASIC selected without installed ROMs");
                }
            }
            finally
            {
                if (File.Exists(basicPath)) File.Delete(basicPath);
                if (File.Exists(kernalPath)) File.Delete(kernalPath);
                if (File.Exists(characterPath)) File.Delete(characterPath);
                if (File.Exists(drivePath)) File.Delete(drivePath);
                Directory.Delete(temporary);
            }
        }

        private static void VerifyFloatProductGuard(string directory, C64AccuracyOptions options)
        {
            // These five-byte results distinguish a fresh decimal literal from
            // the same number after BASIC stores it in a variable.
            string[] commands =
            {
                "A=.1*.1", "A=10*.9", "A=.0001*.01", "A=.1:B=.1:A=A*B"
            };
            byte[][] expected =
            {
                new byte[] { 0x7A, 0x23, 0xD7, 0x0A, 0x3D },
                new byte[] { 0x84, 0x10, 0x00, 0x00, 0x00 },
                new byte[] { 0x6D, 0x06, 0x37, 0xBD, 0x06 },
                new byte[] { 0x7A, 0x23, 0xD7, 0x0A, 0x3E }
            };
            for (int caseIndex = 0; caseIndex < commands.Length; caseIndex++)
            {
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    EnterBasic(system, commands[caseIndex]);
                    int first = system.PeekRam(0x002D) | system.PeekRam(0x002E) << 8;
                    int end = system.PeekRam(0x002F) | system.PeekRam(0x0030) << 8;
                    Expect(end - first >= 7 && system.PeekRam((ushort)first) == (byte)'A',
                        "BASIC stores numeric A after " + commands[caseIndex]);
                    for (int valueIndex = 0; valueIndex < 5; valueIndex++)
                    {
                        Expect(system.PeekRam((ushort)(first + 2 + valueIndex)) ==
                            expected[caseIndex][valueIndex],
                            "BASIC product guard " + commands[caseIndex] +
                            " byte " + valueIndex);
                    }
                }
            }
        }

        private static void VerifySmallExp(string directory, C64AccuracyOptions options)
        {
            string[] expressions = { ".0099", ".01", ".016", "-.01" };
            byte[] exponents = { 0x81, 0x81, 0x81, 0x80 };
            for (int index = 0; index < expressions.Length; index++)
            {
                using (var system = new C64System(C64Model.Pal, options, directory))
                {
                    WaitForReady(system);
                    EnterBasic(system, "A=EXP(" + expressions[index] + ")");
                    int first = system.PeekRam(0x002D) | system.PeekRam(0x002E) << 8;
                    int end = system.PeekRam(0x002F) | system.PeekRam(0x0030) << 8;
                    Expect(end - first >= 7 && system.PeekRam((ushort)first) == (byte)'A' &&
                        system.PeekRam((ushort)(first + 2)) == exponents[index],
                        "EXP(" + expressions[index] + ") avoids exponent underflow wrap");
                }
            }
        }

        private static void VerifyOwnBasic(string directory, C64AccuracyOptions options)
        {
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT 2*3+4");
                Expect(ScreenHasLine(system, "10"), "BASIC multiplication precedence: " +
                    ReadScreenRows(system) + " input=" +
                    BitConverter.ToString(ReadRam(system, 0x0200, 14)) + " tokens=" +
                    BitConverter.ToString(ReadRam(system, 0x0380, 14)));
                EnterBasic(system, "PRINT (2+3)*4");
                Expect(ScreenHasLine(system, "20"), "BASIC parentheses");
                EnterBasic(system, "PRINT 1.5+2.25");
                Expect(ScreenHasLine(system, "3.75"), "BASIC floating addition: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 5.5-2.25");
                Expect(ScreenHasLine(system, "3.25"), "BASIC floating subtraction: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 7/2");
                Expect(ScreenHasLine(system, "3.5"), "BASIC floating division: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT .125*8");
                Expect(ScreenHasLine(system, "1"), "BASIC fractional multiplication: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 1E2+0.25");
                Expect(ScreenHasLine(system, "100.25"), "BASIC decimal exponent: " + ReadScreenRows(system));
                EnterBasic(system, "A=1.25:B=A*2:PRINT B");
                Expect(ScreenHasLine(system, "2.5"), "BASIC floating variables: " + ReadScreenRows(system));
                int floatVariables = system.PeekRam(0x2D) | system.PeekRam(0x2E) << 8;
                byte[] floatLayout = ReadRam(system, floatVariables, 14);
                Expect(floatLayout[0] == 'A' && floatLayout[2] == 0x81 &&
                    floatLayout[3] == 0x20 && floatLayout[7] == 'B' &&
                    floatLayout[9] == 0x82 && floatLayout[10] == 0x20,
                    "BASIC fractional five-byte variable layout: " + BitConverter.ToString(floatLayout));
                EnterBasic(system, "PRINT 1/3");
                Expect(ScreenHasLine(system, ".333333333"), "BASIC recurring decimal: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT .1+.2");
                Expect(ScreenHasLine(system, ".3"), "BASIC decimal rounding: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT -1.5*2");
                Expect(ScreenHasLine(system, "-3"), "BASIC floating sign: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 1E8");
                Expect(ScreenHasLine(system, "100000000"), "BASIC fixed output: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 1E-5");
                Expect(ScreenHasLine(system, "1E-05"), "BASIC small scientific output: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT INT(-1.5)");
                Expect(ScreenHasLine(system, "-2"), "BASIC negative INT: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT INT(1.9)");
                Expect(ScreenHasLine(system, "1"), "BASIC positive INT: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 1.5>1.25");
                Expect(ScreenHasLine(system, "-1"), "BASIC floating comparison: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 1E38>-1E38");
                Expect(ScreenHasLine(system, "-1"), "BASIC comparison without overflow: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 5>2");
                Expect(ScreenHasLine(system, "-1"), "BASIC true comparison");
                EnterBasic(system, "PRINT 5<2");
                Expect(ScreenHasLine(system, "0"), "BASIC false comparison");
                EnterBasic(system, "PRINT 6 AND 3");
                Expect(ScreenHasLine(system, "2"), "BASIC bitwise AND");
                EnterBasic(system, "PRINT 6 OR 3");
                Expect(ScreenHasLine(system, "7"), "BASIC bitwise OR");
                EnterBasic(system, "PRINT 1 OR 2 AND 0");
                Expect(ScreenHasLine(system, "1"), "BASIC logical operator precedence");
                EnterBasic(system, "PRINT NOT 2=2");
                Expect(ScreenHasLine(system, "0"), "BASIC NOT after comparison");
                EnterBasic(system, "PRINT ABS(-7)");
                Expect(ScreenHasLine(system, "7"), "BASIC ABS function");
                EnterBasic(system, "PRINT SGN(-3)");
                Expect(ScreenHasLine(system, "-1"), "BASIC SGN function");
                EnterBasic(system, "PRINT INT(-7)");
                Expect(ScreenHasLine(system, "-7"), "BASIC INT on integer evaluator");
                EnterBasic(system, "POKE 49152,42:PRINT PEEK(49152)");
                Expect(system.PeekRam(0xC000) == 42 && ScreenHasLine(system, "42"),
                    "BASIC POKE and PEEK: " + ReadScreenRows(system));
                EnterBasic(system, "POKE 49152,8:WAIT 49152,8:PRINT \"WAITOK\"");
                Expect(ScreenHasLine(system, "WAITOK"), "BASIC WAIT address and mask");
                EnterBasic(system, "POKE 49152,0:WAIT 49152,8,8:PRINT \"XOROK\"");
                Expect(ScreenHasLine(system, "XOROK"), "BASIC WAIT xor mask");
                EnterBasic(system, "A$=\"HELLO\":PRINT A$");
                Expect(ScreenHasLine(system, "HELLO"),
                    "BASIC literal string assignment: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT LEN(\"HELLO\")");
                Expect(ScreenHasLine(system, "5"), "BASIC LEN literal: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT LEN(A$)");
                Expect(ScreenHasLine(system, "5"), "BASIC LEN variable: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT ASC(A$)");
                Expect(ScreenHasLine(system, "72"), "BASIC ASC variable: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT VAL(\" -123X\")");
                Expect(ScreenHasLine(system, "-123"), "BASIC VAL prefix: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT CHR$(65);CHR$(66)");
                Expect(ScreenHasLine(system, "AB"), "BASIC PRINT CHR$: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "A$=STR$(1.25):PRINT A$:PRINT VAL(A$)");
                Expect(ScreenHasLine(system, "1.25") &&
                    Equal(ReadBasicString(system, (byte)'A'), System.Text.Encoding.ASCII.GetBytes(" 1.25")),
                    "BASIC STR$ and VAL round trip: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT STR$(-1.25)");
                Expect(ScreenHasLine(system, "-1.25"),
                    "BASIC PRINT STR$: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT VAL(STR$(1.25))");
                Expect(ScreenHasLine(system, "1.25"),
                    "BASIC VAL(STR$): " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT LOG(1)");
                Expect(ScreenHasLine(system, "0"), "BASIC LOG(1): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT EXP(0)");
                Expect(ScreenHasLine(system, "1"), "BASIC EXP(0): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT SIN(0)");
                Expect(ScreenHasLine(system, "0"), "BASIC SIN(0): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT COS(0)");
                Expect(ScreenHasLine(system, "1"), "BASIC COS(0): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT TAN(0)");
                Expect(ScreenHasLine(system, "0"), "BASIC TAN(0): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT ATN(0)");
                Expect(ScreenHasLine(system, "0"), "BASIC ATN(0): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT LOG(2)");
                Expect(ScreenHasLine(system, ".693147181"), "BASIC LOG(2): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT EXP(1)");
                Expect(ScreenHasLine(system, "2.71828183"), "BASIC EXP(1): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT SIN(1)");
                Expect(ScreenHasLine(system, ".841470985"), "BASIC SIN(1): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT COS(1)");
                Expect(ScreenHasLine(system, ".540302306"), "BASIC COS(1): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT TAN(1)");
                Expect(ScreenHasLine(system, "1.55740772"), "BASIC TAN(1): " + ReadScreenRows(system));
                EnterBasic(system, "PRINT ATN(1)");
                Expect(ScreenHasLine(system, ".785398163"), "BASIC ATN(1): " + ReadScreenRows(system));
                EnterBasic(system, "A=RND(1):PRINT A<1");
                Expect(ScreenHasLine(system, "-1"), "BASIC RND range: " + ReadScreenRows(system));
                EnterBasic(system, "A=RND(-1):B=RND(-1):PRINT A=B");
                Expect(ScreenHasLine(system, "-1"), "BASIC RND reseeding: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT RND(1)=RND(1)");
                Expect(ScreenHasLine(system, "0"), "BASIC RND advances: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT SIN(-1)");
                Expect(ScreenHasLine(system, "-.841470985"),
                    "BASIC negative SIN: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT TAN(-1)");
                Expect(ScreenHasLine(system, "-1.55740772"),
                    "BASIC negative TAN: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT COS(0)=1");
                Expect(ScreenHasLine(system, "0"),
                    "BASIC COS zero comparison: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                // RC3 INIT copies only the first four seed bytes to $8B..$8E;
                // the fifth byte at $8F is never loaded and stays zero.
                Expect(Equal(ReadRam(system, 0x008B, 5),
                    new byte[] { 0x80, 0x4F, 0xC7, 0x52, 0x00 }),
                    "BASIC initial RND state");
                EnterBasic(system, "A=RND(-.1)");
                Expect(Equal(ReadRam(system, 0x008B, 5),
                    new byte[] { 0x80, 0x4C, 0xCC, 0xCC, 0xCC }),
                    "BASIC negative decimal RND seed");
                EnterBasic(system, "A=RND(-1):A=RND(1)");
                // RND(-1) seeds from the emulated TOD clock, which is fixed at
                // this point of the deterministic test sequence.
                Expect(Equal(ReadRam(system, 0x008B, 5),
                    new byte[] { 0x80, 0x3C, 0x2A, 0xFB, 0xB5 }),
                    "BASIC seeded RND step");
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "A=0*1.5");
                int address = system.PeekRam(0x2D) | system.PeekRam(0x2E) << 8;
                Expect(Equal(ReadRam(system, address + 2, 5), new byte[] { 0, 0x40, 0, 0, 0 }),
                    "BASIC zero-product mantissa payload");
                EnterBasic(system, "A=1E-39");
                Expect(Equal(ReadRam(system, address + 2, 5), new byte[] { 0, 0x20, 0, 0, 0 }),
                    "BASIC underflow mantissa payload");
                EnterBasic(system, "A=0*.3");
                Expect(Equal(ReadRam(system, address + 2, 5),
                    new byte[] { 0, 0x19, 0x99, 0x99, 0x99 }),
                    "BASIC zero-times-decimal mantissa payload");
                EnterBasic(system, "A=0/.3");
                Expect(Equal(ReadRam(system, address + 2, 5),
                    new byte[] { 0, 0x19, 0x99, 0x99, 0x9A }),
                    "BASIC zero-divided-by-decimal mantissa payload");
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT 1/0");
                // RC3 error text is not padded to the classic 24-column layout.
                Expect(ScreenHasLine(system, "?DIVISION BY ZERO ERROR"),
                    "BASIC division by zero: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT LOG(0)");
                Expect(ScreenHasLine(system, "?ILLEGAL QUANTITY ERROR"),
                    "BASIC LOG domain: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT SQR(-1)");
                Expect(ScreenHasLine(system, "?ILLEGAL QUANTITY ERROR"),
                    "BASIC SQR domain: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "PRINT EXP(100)");
                Expect(ScreenHasLine(system, "?OVERFLOW ERROR"),
                    "BASIC EXP overflow: " + ReadScreenRows(system));
                EnterBasic(system, "PRINT 2*3");
                Expect(ScreenHasLine(system, "6"),
                    "BASIC multiplication after overflow: " + ReadScreenRows(system));
            }
            // INPUT only runs inside a program; both the original ROM and the
            // RC3 ROM raise ILLEGAL DIRECT for direct-mode INPUT, so the feature
            // checks below enter a small program first.
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "INPUT A$");
                Expect(ScreenHasLine(system, "?ILLEGAL DIRECT ERROR"),
                    "BASIC direct-mode INPUT rejected: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 INPUT A$");
                EnterBasic(system, "20 PRINT A$");
                EnterBasic(system, "RUN");
                system.EnqueuePetsciiText("HELLO\r");
                system.RunCycles(3000000);
                Expect(ScreenHasLine(system, "HELLO"),
                    "BASIC string INPUT: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 INPUT N");
                EnterBasic(system, "20 PRINT N*2");
                EnterBasic(system, "RUN");
                system.EnqueuePetsciiText("7\r");
                system.RunCycles(3000000);
                Expect(ScreenHasLine(system, "14"),
                    "BASIC numeric INPUT: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 INPUT F");
                EnterBasic(system, "20 PRINT F*2");
                EnterBasic(system, "RUN");
                system.EnqueuePetsciiText("1.75\r");
                system.RunCycles(3000000);
                Expect(ScreenHasLine(system, "3.5"),
                    "BASIC floating INPUT: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "A=1:B=-3:C%=7:D=1234:PRINT A+B+C%+D");
                Expect(ScreenHasLine(system, "1239"),
                    "BASIC numeric variable round trip: " + ReadScreenRows(system));
                int variables = system.PeekRam(0x2D) | (system.PeekRam(0x2E) << 8);
                byte[] layout = ReadRam(system, variables, 28);
                Expect(layout[0] == 0x41 && layout[2] == 0x81 && layout[3] == 0 &&
                    layout[4] == 0 && layout[5] == 0 && layout[6] == 0 &&
                    layout[7] == 0x42 && layout[9] == 0x82 && layout[10] == 0xC0 &&
                    layout[11] == 0 && layout[12] == 0 && layout[13] == 0 &&
                    layout[14] == 0xC3 && layout[15] == 0x80 &&
                    layout[16] == 0 && layout[17] == 7 &&
                    layout[21] == 0x44 && layout[23] == 0x8B && layout[24] == 0x1A &&
                    layout[25] == 0x40 && layout[26] == 0 && layout[27] == 0,
                    "BASIC five-byte numeric and two-byte integer variable layout: " +
                    BitConverter.ToString(layout));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "30 PRINT \"THREE\"");
                EnterBasic(system, "10 A=2");
                EnterBasic(system, "20 IF A=2 THEN PRINT \"TWO\"");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "TWO") && ScreenHasLine(system, "THREE"),
                    "sorted BASIC lines and IF/THEN: " + ReadScreenRows(system));
                EnterBasic(system, "20 PRINT \"NEW\"");
                EnterBasic(system, "30");
                EnterBasic(system, "40000 PRINT \"HIGH\"");
                // CHR$(147) is the KERNAL screen-clear character (the CLS
                // keyword is not part of RC3 BASIC).  It removes the earlier
                // "30 PRINT ..." keyboard echo so the negative LIST check
                // cannot match stale screen content.
                EnterBasic(system, "PRINT CHR$(147)");
                EnterBasic(system, "LIST");
                Expect(ScreenHasLine(system, "20 PRINT \"NEW\"") &&
                    ScreenHasLine(system, "40000 PRINT \"HIGH\"") &&
                    !ScreenHasLine(system, "30 PRINT \"THREE\""),
                    "BASIC line replacement, deletion and LIST: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 GOSUB 100");
                EnterBasic(system, "20 PRINT \"AFTER\"");
                EnterBasic(system, "30 END");
                EnterBasic(system, "100 PRINT \"SUB\"");
                EnterBasic(system, "110 RETURN");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "SUB") && ScreenHasLine(system, "AFTER"),
                    "BASIC GOSUB/RETURN: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 ON 2 GOTO 100,200,300");
                EnterBasic(system, "20 PRINT \"WRONG\":END");
                EnterBasic(system, "100 PRINT \"FIRST\":END");
                EnterBasic(system, "200 PRINT \"SECOND\":END");
                EnterBasic(system, "300 PRINT \"THIRD\":END");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "SECOND"),
                    "BASIC ON GOTO selects the indexed target: " + ReadScreenRows(system));
                EnterBasic(system, "ON 0 GOTO 100,200:PRINT \"FALLTHROUGH\"");
                Expect(ScreenHasLine(system, "FALLTHROUGH"),
                    "BASIC ON GOTO out-of-range fallthrough: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 ON 2 GOSUB 100,200");
                EnterBasic(system, "20 PRINT \"BACK\":END");
                EnterBasic(system, "100 PRINT \"WRONG\":RETURN");
                EnterBasic(system, "200 PRINT \"SUBTWO\":RETURN");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "SUBTWO") && ScreenHasLine(system, "BACK"),
                    "BASIC ON GOSUB returns after the target list: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 FOR I=1 TO 3");
                EnterBasic(system, "20 PRINT I");
                EnterBasic(system, "30 NEXT I");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "1") && ScreenHasLine(system, "2") &&
                    ScreenHasLine(system, "3"),
                    "BASIC FOR/NEXT: " + ReadScreenRows(system) +
                    " frame=" + BitConverter.ToString(ReadRam(system, 0x03B0, 12)) +
                    " state=" + BitConverter.ToString(ReadRam(system, 0x006E, 12)) +
                    " text=" + BitConverter.ToString(ReadRam(system, 0x007A, 4)) +
                    " program=" + BitConverter.ToString(ReadRam(system, 0x0801, 54)));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 FOR I=3 TO 1 STEP -1");
                EnterBasic(system, "20 PRINT I");
                EnterBasic(system, "30 NEXT I");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "3") && ScreenHasLine(system, "2") &&
                    ScreenHasLine(system, "1"),
                    "BASIC negative FOR step: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 FOR I=1 TO 2");
                EnterBasic(system, "20 FOR J=1 TO 2");
                EnterBasic(system, "30 PRINT I*10+J");
                EnterBasic(system, "40 NEXT J");
                EnterBasic(system, "50 NEXT I");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "11") && ScreenHasLine(system, "12") &&
                    ScreenHasLine(system, "21") && ScreenHasLine(system, "22"),
                    "BASIC nested FOR loops: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 FOR I=.5 TO 1.5 STEP .5");
                EnterBasic(system, "20 PRINT I");
                EnterBasic(system, "30 NEXT I");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, ".5") && ScreenHasLine(system, "1") &&
                    ScreenHasLine(system, "1.5"),
                    "BASIC fractional FOR/NEXT: " + ReadScreenRows(system));
                EnterBasic(system, "NEW");
                EnterBasic(system, "10 FOR I=1.5 TO .5 STEP -.5");
                EnterBasic(system, "20 PRINT I");
                EnterBasic(system, "30 NEXT I");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "1.5") && ScreenHasLine(system, "1") &&
                    ScreenHasLine(system, ".5"),
                    "BASIC negative fractional STEP: " + ReadScreenRows(system));
            }
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                WaitForReady(system);
                EnterBasic(system, "10 DATA 5,7,\"HELLO\"");
                EnterBasic(system, "20 READ A,B,C$");
                EnterBasic(system, "30 PRINT A+B");
                EnterBasic(system, "40 PRINT C$");
                EnterBasic(system, "50 RESTORE");
                EnterBasic(system, "60 READ D");
                EnterBasic(system, "70 PRINT D");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "12") && ScreenHasLine(system, "HELLO") &&
                    ScreenHasLine(system, "5"),
                    "BASIC READ/DATA/RESTORE: " + ReadScreenRows(system));
                system.RunCycles(2000000);
                EnterBasic(system, "RESTORE:READ E%,F%:PRINT E%*F%");
                system.RunCycles(2000000);
                Expect(ScreenHasLine(system, "35"),
                    "BASIC READ integer variable: " + ReadScreenRows(system));
                EnterBasic(system, "NEW");
                EnterBasic(system, "10 DATA 1.25,2.5");
                EnterBasic(system, "20 READ X,Y:PRINT X+Y");
                EnterBasic(system, "RUN");
                Expect(ScreenHasLine(system, "3.75"),
                    "BASIC fractional READ/DATA: " + ReadScreenRows(system));
            }
        }

        private static void WaitForReady(C64System system)
        {
            for (int cycles = 0; cycles < 12000000 && !ScreenHasLine(system, "READY."); cycles += 100000)
            {
                system.RunCycles(100000);
            }
            Expect(ScreenHasLine(system, "READY."), "BASIC reached READY");
        }

        private static void EnterBasic(C64System system, string command)
        {
            system.EnqueuePetsciiText(command + "\r");
            system.RunCycles(2000000);
            Expect(system.Cpu.State != CpuState.Jammed,
                "BASIC remains running after " + command);
        }

        private static string VerifyBoot(string directory, C64AccuracyOptions options)
        {
            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                Expect(system.HasDriveFirmware(8) && system.GetDriveProgramCounter(8) == 0xC000,
                    "open ROM mode maps the independently built 1541 bootstrap");
                ushort basicColdStart = (ushort)(system.Peek(0xA000) | system.Peek(0xA001) << 8);
                bool reachedBasic = false;
                for (int cycle = 0; cycle < 300000; cycle++)
                {
                    if (system.Cpu.PC == basicColdStart && system.Cpu.State == CpuState.FetchOpcode)
                    {
                        reachedBasic = true;
                        break;
                    }
                    system.Tick();
                }
                Expect(reachedBasic, "reset reaches BASIC cold start");
                Expect(system.PeekRam(0x0314) != 0, "RAM vectors initialized");
                Expect(system.PeekRam(0x0400) == 0x20, "screen cleared");
                Expect(system.PeekRam(0x0281) == 0 && system.PeekRam(0x0282) == 8,
                    "MEMBOT starts at $0800");
                for (int cycles = 0; cycles < 12000000 && !ScreenHasLine(system, "READY."); cycles += 100000)
                {
                    system.RunCycles(100000);
                }
                Expect(system.Cpu.State != CpuState.Jammed, "CPU runs after BASIC start");
                Expect(ScreenHasLine(system, "**** OPEN BASIC ****"), "own BASIC title");
                Expect(ScreenHasLine(system, "38911 BYTES FREE"),
                    "BASIC free-memory banner");
                string titleRow = ReadScreenLine(system, 0);
                int titleColumn = titleRow.IndexOf("**** OPEN BASIC ****", StringComparison.Ordinal);
                Expect(titleColumn == 0 &&
                    ReadScreenLine(system, 2).TrimStart().StartsWith("38911 BYTES FREE") &&
                    ReadScreenLine(system, 4).StartsWith("READY."),
                    "RC3 startup banner row layout");
                Expect((system.Peek(0xD020) & 0x0F) == 0x0E &&
                    (system.Peek(0xD021) & 0x0F) == 0x06 &&
                    system.PeekRam(0x0286) == 0x0E,
                    "classic blue startup palette");
                Expect(ScreenHasLine(system, "READY."), "BASIC ready prompt");

                system.EnqueuePetsciiText("PRINT 1+1\r");
                system.RunCycles(1000000);
                Expect(system.Cpu.State != CpuState.Jammed, "CPU runs after BASIC expression");
                Expect(ScreenHasLine(system, "PRINT 1+1"), "BASIC accepted keyboard input");
                Expect(ScreenHasLine(system, "2"), "BASIC evaluated PRINT 1+1: " + ReadScreenRows(system));
                Expect(ScreenHasLine(system, "READY."), "BASIC returned to prompt");
                return "BASIC: " + ReadScreenRows(system);
            }
        }

        private static void VerifyAbi(string directory, bool localProfile)
        {
            VerifyCall(directory, 0xFFBA, 4, 8, 1, 0x24, h =>
            {
                Expect(h.Bus.ReadRam(0xB8) == 4, "SETLFS logical file");
                Expect(h.Bus.ReadRam(0xBA) == 8, "SETLFS device");
                Expect(h.Bus.ReadRam(0xB9) == 1, "SETLFS secondary address");
            });
            VerifyCall(directory, 0xFFBD, 3, 0, 0x20, 0x24, h =>
            {
                Expect(h.Bus.ReadRam(0xB7) == 3, "SETNAM length");
                Expect(h.Bus.ReadRam(0xBB) == 0 && h.Bus.ReadRam(0xBC) == 0x20, "SETNAM pointer");
            });
            VerifyCall(directory, 0xFFED, 0, 0, 0, 0x24, h =>
                Expect(h.Cpu.X == 40 && h.Cpu.Y == 25, "SCREEN dimensions"));
            VerifyCall(directory, 0xFFF0, 0, 12, 20, 0x24, h =>
                Expect(h.Bus.ReadRam(0xD6) == 12 && h.Bus.ReadRam(0xD3) == 20, "PLOT coordinates"));
            VerifyCall(directory, 0xFFE4, 0, 0, 0, 0x24, h =>
                Expect(h.Cpu.A == 0, "GETIN empty queue"));
            VerifyCall(directory, 0xFFE4, 0, 0, 0, 0x24, h =>
            {
                Expect(h.Cpu.A == 0x41, "GETIN queued character");
                Expect(h.Bus.ReadRam(0xC6) == 1 && h.Bus.ReadRam(0x0277) == 0x42, "GETIN queue shift");
            }, h =>
            {
                h.Bus.WriteRam(0xC6, 2);
                h.Bus.WriteRam(0x0277, 0x41);
                h.Bus.WriteRam(0x0278, 0x42);
            });
            VerifyCall(directory, 0xFFD2, 0, 0, 0, 0x24, h =>
                Expect(h.Cpu.A == 0x42, "CHROUT follows writable RAM vector"), h =>
            {
                h.Bus.WriteRam(0x0326, 0x10);
                h.Bus.WriteRam(0x0327, 0x02);
                h.Bus.WriteRam(0x0210, 0xA9);
                h.Bus.WriteRam(0x0211, 0x42);
                h.Bus.WriteRam(0x0212, 0x60);
            });
            if (localProfile)
            {
                VerifyCall(directory, 0xFFD8, 0x2B, 0x01, 0x08, 0x24, h =>
                    Expect(h.Cpu.A == 8 && (h.Cpu.SR & 1) != 0, "SAVE requires disk filename"), h =>
                {
                    h.Bus.WriteRam(0xBA, 8);
                    h.Bus.WriteRam(0xB7, 0);
                    h.Bus.WriteRam(0x2B, 0x01);
                    h.Bus.WriteRam(0x2C, 0x08);
                });
            }
        }

        private static void VerifyPrivateEntrypoints(string directory, C64AccuracyOptions options)
        {
            VerifyCall(directory, 0xFD15, 0, 0, 0, 0x24, h =>
            {
                Expect(h.Bus.ReadRam(0x0314) != 0 || h.Bus.ReadRam(0x0315) != 0,
                    "private RESTOR entry $FD15 restores RAM vectors");
            }, h =>
            {
                h.Bus.WriteRam(0x0314, 0);
                h.Bus.WriteRam(0x0315, 0);
            });

            using (var system = new C64System(C64Model.Pal, options, directory))
            {
                for (int cycles = 0; cycles < 12000000 && !ScreenHasLine(system, "READY."); cycles += 100000)
                {
                    system.RunCycles(100000);
                }
                Expect(ScreenHasLine(system, "READY."), "private entry test reached BASIC prompt");
                system.PokeRam(0x0400, 0x01);
                InvokeKernal(system, 0xE544, 0);
                Expect(system.PeekRam(0x0400) == 0x20,
                    "private screen clear entry $E544 clears the screen");
            }
        }

        private static string VerifyIec(string directory, C64AccuracyOptions options, bool originalPrototype)
        {
            string diskPath = Path.Combine(Path.GetTempPath(), "open-rom64-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                File.WriteAllBytes(diskPath, CreateTestDisk());
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    Expect(!InvokeKernal(system, 0xFFB1, 8).Carry, "IEC LISTEN");
                    Expect(!InvokeKernal(system, 0xFF93, 0xF0).Carry, "IEC SECOND");
                    Expect(!InvokeKernal(system, 0xFFA8, (byte)'A').Carry, "IEC CIOUT");
                    Expect(!InvokeKernal(system, 0xFFAE, 0).Carry, "IEC UNLSN");
                    Expect(!InvokeKernal(system, 0xFFB4, 8).Carry, "IEC TALK");
                    Expect(!InvokeKernal(system, 0xFF96, 0x60).Carry, "IEC TKSA");
                    byte[] expected = { 0, 0x20, 0xA9, 0x2A, 0x8D, 0, 0xC0, 0x60 };
                    for (int index = 0; index < expected.Length; index++)
                    {
                        CallResult result = InvokeKernal(system, 0xFFA5, 0);
                        Expect(!result.Carry && result.A == expected[index], "IEC ACPTR byte " + index);
                        if (index == expected.Length - 1)
                        {
                            Expect((result.Status & 0x40) != 0, "IEC EOI on last byte");
                        }
                    }
                    Expect(!InvokeKernal(system, 0xFFAB, 0).Carry, "IEC UNTLK");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    SetFileParameters(system, 1, 8, 1);
                    CallResult result = InvokeKernal(system, 0xFFD5, 0, 0, 0x20);
                    Expect(!result.Carry && result.X == 0x06 && result.Y == 0x20, "PRG LOAD end address");
                    Expect(Equal(ReadRam(system, 0x2000, ProgramBytes.Length), ProgramBytes), "PRG LOAD payload");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    SetFileParameters(system, 4, 8, 2);
                    Expect(!InvokeKernal(system, 0xFFC0, 0).Carry, "OPEN serial file");
                    Expect(!InvokeKernal(system, 0xFFC6, 0, 4).Carry, "CHKIN serial file");
                    Expect(InvokeKernal(system, 0xFFCF, 0).A == 0, "CHRIN PRG address low byte");
                    Expect(InvokeKernal(system, 0xFFCF, 0).A == 0x20, "CHRIN PRG address high byte");
                    Expect(InvokeKernal(system, 0xFFCC, 0).Carry &&
                        system.PeekRam(0x009A) == 3, "CLRCHN screen default");
                    Expect(!InvokeKernal(system, 0xFFC3, 4).Carry, "CLOSE serial file");
                    Expect(system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098) == 0,
                        "file table released");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    SetFileParameters(system, 1, 8, 1);
                    system.PokeRam(0x0200, (byte)'C');
                    system.PokeRam(0x002B, 0x00);
                    system.PokeRam(0x002C, 0x20);
                    for (int index = 0; index < ProgramBytes.Length; index++)
                    {
                        system.PokeRam((ushort)(0x2000 + index), ProgramBytes[index]);
                    }
                    CallResult result = InvokeKernal(system, 0xFFD8, 0x2B, 0x06, 0x20);
                    Expect(!result.Carry, "SAVE transmits PRG bytes over IEC");
                    MediaLoadData saved;
                    Expect(D64Image.Load(diskPath).TryResolveLoad("C", out saved),
                        "SAVE created a D64 directory entry: status=$" +
                        system.PeekRam(0x0090).ToString("X2") + " drive=" +
                        system.GetDriveDebugInfo(8));
                    byte[] expectedSaved = { 0, 0x20, 0xA9, 0x2A, 0x8D, 0, 0xC0, 0x60 };
                    Expect(Equal(saved.ProgramBytes, expectedSaved), "SAVE persisted exact PRG bytes");
                    for (int index = 0; index < ProgramBytes.Length; index++)
                    {
                        system.PokeRam((ushort)(0x2000 + index), 0);
                    }
                    CallResult reloaded = InvokeKernal(system, 0xFFD5, 0, 0, 0x20);
                    Expect(!reloaded.Carry && Equal(ReadRam(system, 0x2000, 6), ProgramBytes),
                        "LOAD reads the newly saved PRG");

                    SetFileParameters(system, 1, 8, 1);
                    system.PokeRam(0x0200, (byte)'E');
                    system.PokeRam(0x002B, 0);
                    system.PokeRam(0x002C, 0x30);
                    byte[] larger = new byte[257];
                    larger[0] = 0;
                    larger[1] = 0x30;
                    for (int index = 0; index < 255; index++)
                    {
                        larger[index + 2] = (byte)(index * 37 + 11);
                        system.PokeRam((ushort)(0x3000 + index), larger[index + 2]);
                    }
                    CallResult largerSaved = InvokeKernal(system, 0xFFD8, 0x2B, 0xFF, 0x30);
                    Expect(!largerSaved.Carry, "multi-sector SAVE completed over IEC");
                    MediaLoadData largerCopy;
                    Expect(D64Image.Load(diskPath).TryResolveLoad("E", out largerCopy) &&
                        Equal(largerCopy.ProgramBytes, larger), "multi-sector SAVE persisted exact bytes");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    system.EnqueuePetsciiText("LOAD\"A\",8");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText(",1\r");
                    system.RunCycles(2000000);
                    Expect(Equal(ReadRam(system, 0x2000, ProgramBytes.Length), ProgramBytes),
                        "BASIC LOAD command transferred PRG payload");
                    Expect(system.Cpu.State != CpuState.Jammed, "CPU runs after BASIC LOAD");
                    system.EnqueuePetsciiText("VERIFY\"A\",8,1\r");
                    system.RunCycles(1600000);
                    Expect(ScreenHasLine(system, "READY.") &&
                        !ScreenHasLine(system, "?I/O ERROR"),
                        "BASIC VERIFY matched loaded PRG: " + ReadScreenRows(system));
                    system.EnqueuePetsciiText("SYS8192\r");
                    system.RunCycles(1000000);
                    Expect(system.PeekRam(0xC000) == 0x2A, "BASIC SYS executed loaded machine code");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    system.EnqueuePetsciiText("OPEN4,8,2");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText(",\"A\"\r");
                    system.RunCycles(500000);
                    Expect(system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098) == 1,
                        "BASIC OPEN command created file");
                    system.EnqueuePetsciiText("CLOSE4\r");
                    system.RunCycles(500000);
                    Expect(system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098) == 0,
                        "BASIC CLOSE command released file");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    system.EnqueuePetsciiText("5 OPEN4,");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText("8,2,\"A\"\r");
                    system.RunCycles(300000);
                    // RC3 program lines use a 4-byte header: next-low, next-high,
                    // line-low, line-high; the tokenized content follows and ends
                    // with a zero byte.  OPEN is RC3 keyword token $9F.
                    Expect(system.PeekRam(0x0803) == 0x05 &&
                        system.PeekRam(0x0805) == 0x9F &&
                        system.PeekRam(0x0806) == 0x34,
                        "first tokenized line: input=" + BitConverter.ToString(ReadRam(system, 0x0200, 24)) +
                        " tokens=" + BitConverter.ToString(ReadRam(system, 0x0380, 24)) +
                        " program=" + BitConverter.ToString(ReadRam(system, 0x0801, 24)));
                    system.EnqueuePetsciiText("10 GET#4,");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText("A$,B$\r");
                    system.RunCycles(300000);
                    system.EnqueuePetsciiText("20 PRINT");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText(" \"OK\"\r");
                    system.RunCycles(300000);
                    system.EnqueuePetsciiText("30 CLOSE4\r");
                    system.RunCycles(300000);
                    system.EnqueuePetsciiText("RUN\r");
                    system.RunCycles(5000000);
                    Expect(ScreenHasLine(system, "OK"),
                        "BASIC program resumed after OPEN/GET#: " + ReadScreenRows(system) +
                        " pc=" + system.Cpu.PC.ToString("X4") +
                        " text=" + system.PeekRam(0x007A).ToString("X2") + "/" +
                        system.PeekRam(0x007B).ToString("X2") +
                        " files=" + system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098).ToString("X2") +
                        " input=" + system.PeekRam(0x0099).ToString("X2") +
                        " status=" + system.PeekRam(0x0090).ToString("X2") +
                        " program=" + BitConverter.ToString(ReadRam(system, 0x0801, 64)) +
                        " drive=" + system.GetDriveDebugInfo(8));
                    byte[] readByte = ReadBasicString(system, (byte)'B');
                    Expect(readByte.Length == 1 && readByte[0] == 0x20,
                        "BASIC GET# assigned the second PRG byte to B$");
                    Expect(system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098) == 0,
                        "BASIC program CLOSE released file");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    system.EnqueuePetsciiText("OPEN15,8");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText(",15\r");
                    system.RunCycles(500000);
                    Expect(system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098) == 1,
                        "BASIC opened command channel");
                    system.EnqueuePetsciiText("PRINT#15,");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText("\"X\"\r");
                    system.RunCycles(1000000);
                    Expect(system.GetDriveDebugInfo(8).Contains("lastCmd=\"X\"") &&
                        system.GetDriveDebugInfo(8).Contains("SYNTAX ERROR"),
                        "BASIC PRINT# sent command channel bytes: screen=" +
                        ReadScreenRows(system) + " pc=" + system.Cpu.PC.ToString("X4") +
                        " buffer=" + system.PeekRam(0x034D).ToString("X2") + "/" +
                        system.PeekRam(0x0350).ToString("X2") +
                        " output=" + system.PeekRam(0x009A).ToString("X2") +
                        " drive=" + system.GetDriveDebugInfo(8));
                    system.EnqueuePetsciiText("CLOSE15\r");
                    system.RunCycles(500000);
                    Expect(system.PeekRam(originalPrototype ? (ushort)0x0340 : (ushort)0x0098) == 0,
                        "BASIC closed command channel");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    // INPUT# is a program statement in the RC3 ROM just like
                    // INPUT (direct mode raises ILLEGAL DIRECT), so the status
                    // query runs inside a small program.
                    system.EnqueuePetsciiText("10 OPEN15,8,15\r");
                    system.RunCycles(500000);
                    system.EnqueuePetsciiText("20 INPUT#15,S$\r");
                    system.RunCycles(300000);
                    system.EnqueuePetsciiText("30 CLOSE15\r");
                    system.RunCycles(300000);
                    system.EnqueuePetsciiText("40 PRINT S$\r");
                    system.RunCycles(300000);
                    system.EnqueuePetsciiText("RUN\r");
                    system.RunCycles(6000000);
                    byte[] statusField = ReadBasicString(system, (byte)'S');
                    Expect(statusField.Length == 2 &&
                        statusField[0] >= (byte)'0' && statusField[0] <= (byte)'9' &&
                        statusField[1] >= (byte)'0' && statusField[1] <= (byte)'9' &&
                        ScreenHasLine(system, System.Text.Encoding.ASCII.GetString(statusField)),
                        "BASIC INPUT# reads a delimited drive-status field: " +
                        ReadScreenRows(system) + " drive=" + system.GetDriveDebugInfo(8));
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    system.EnqueuePetsciiText("LOAD\"B\",8\r");
                    system.RunCycles(1500000);
                    Expect(system.PeekRam(0x0805) == 0x99, "BASIC PRG loaded and tokenized");
                    system.EnqueuePetsciiText("RUN\r");
                    system.RunCycles(1000000);
                    Expect(ScreenHasLine(system, "42"), "loaded BASIC program runs");
                    system.EnqueuePetsciiText("SAVE\"D\",8\r");
                    system.RunCycles(1500000);
                    MediaLoadData basicCopy;
                    Expect(D64Image.Load(diskPath).TryResolveLoad("D", out basicCopy),
                        "BASIC SAVE created a D64 file");
                    Expect(basicCopy.ProgramBytes.Length >= 8 &&
                        basicCopy.ProgramBytes[0] == 1 && basicCopy.ProgramBytes[1] == 8 &&
                        basicCopy.ProgramBytes[6] == 0x99,
                        "BASIC SAVE persisted tokenized program");
                }
                using (var system = MountedSystem(directory, options, diskPath))
                {
                    system.EnqueuePetsciiText("LOAD\"N\",8");
                    system.RunCycles(100000);
                    system.EnqueuePetsciiText(",1\r");
                    system.RunCycles(2000000);
                    Expect(ScreenHasLine(system, "?FILE NOT FOUND ERROR"),
                        "BASIC reports missing file precisely: " + ReadScreenRows(system));
                    Expect(system.Cpu.State != CpuState.Jammed, "CPU runs after disk error");
                }
                return "IEC: LISTEN/SECOND/CIOUT/UNLSN/TALK/TKSA/ACPTR/UNTLK, " +
                    "KERNAL and BASIC OPEN/CLOSE, CHKIN/CHKOUT/CHRIN/CHROUT/CLRCHN, " +
                    "machine-code and BASIC PRG LOAD/RUN, BASIC SYS8192, " +
                    "BASIC GET#/INPUT#/PRINT#/VERIFY, one- and two-sector SAVE/LOAD round trips";
            }
            finally
            {
                if (File.Exists(diskPath))
                {
                    File.Delete(diskPath);
                }
            }
        }

        private static C64System MountedSystem(string directory, C64AccuracyOptions options, string diskPath)
        {
            var system = new C64System(C64Model.Pal, options, directory);
            try
            {
                Expect(system.MountMedia(diskPath, 8).StartsWith("DISK MOUNTED", StringComparison.Ordinal),
                    "test disk mounted");
                for (int cycles = 0; cycles < 12000000 && !ScreenHasLine(system, "READY."); cycles += 100000)
                {
                    system.RunCycles(100000);
                }
                Expect(ScreenHasLine(system, "READY."), "mounted system reached BASIC prompt");
                return system;
            }
            catch
            {
                system.Dispose();
                throw;
            }
        }

        private static void SetFileParameters(C64System system, byte logical, byte device, byte secondary)
        {
            system.PokeRam(0x0200, (byte)'A');
            system.PokeRam(0x00B7, 1);
            system.PokeRam(0x00BB, 0);
            system.PokeRam(0x00BC, 2);
            system.PokeRam(0x00B8, logical);
            system.PokeRam(0x00BA, device);
            system.PokeRam(0x00B9, secondary);
        }

        private static byte[] CreateTestDisk()
        {
            var data = new byte[174848];
            int bamOffset = 17 * 21 * 256;
            data[bamOffset] = 18;
            data[bamOffset + 1] = 1;
            data[bamOffset + 2] = 0x41;
            for (int track = 1; track <= 35; track++)
            {
                int sectorCount = track <= 17 ? 21 : track <= 24 ? 19 : track <= 30 ? 18 : 17;
                int reserved = track == 1 || track == 18 ? 2 : 0;
                int entry = bamOffset + 4 + (track - 1) * 4;
                data[entry] = (byte)(sectorCount - reserved);
                for (int sector = reserved; sector < sectorCount; sector++)
                {
                    data[entry + 1 + sector / 8] |= (byte)(1 << (sector % 8));
                }
            }
            byte[] program = { 0, 0x20, 0xA9, 0x2A, 0x8D, 0, 0xC0, 0x60 };
            data[0] = 0;
            data[1] = (byte)(program.Length + 1);
            Array.Copy(program, 0, data, 2, program.Length);
            int directory = (17 * 21 + 1) * 256;
            data[directory + 2] = 0x82;
            data[directory + 3] = 1;
            data[directory + 4] = 0;
            for (int index = 0; index < 16; index++)
            {
                data[directory + 5 + index] = index == 0 ? (byte)'A' : (byte)0xA0;
            }
            data[directory + 30] = 1;
            byte[] basicProgram = { 0x01, 0x08, 0x0A, 0x08, 0x0A, 0x00, 0x99, 0x20,
                0x34, 0x32, 0x00, 0x00, 0x00 };
            data[256] = 0;
            data[257] = (byte)(basicProgram.Length + 1);
            Array.Copy(basicProgram, 0, data, 258, basicProgram.Length);
            data[directory + 34] = 0x82;
            data[directory + 35] = 1;
            data[directory + 36] = 1;
            for (int index = 0; index < 16; index++)
            {
                data[directory + 37 + index] = index == 0 ? (byte)'B' : (byte)0xA0;
            }
            data[directory + 62] = 1;
            return data;
        }

        private sealed class CallResult
        {
            public byte A;
            public byte X;
            public byte Y;
            public byte Status;
            public bool Carry;
        }

        private static CallResult InvokeKernal(C64System system, ushort entry, byte a, byte x = 0,
            byte y = 0, Action<C64System> onTick = null)
        {
            for (int cycle = 0; cycle < 1000 && system.Cpu.State != CpuState.FetchOpcode; cycle++)
            {
                system.Tick();
            }
            Expect(system.Cpu.State == CpuState.FetchOpcode, "CPU ready for KERNAL call");
            system.PokeRam(0x01FE, 0xFF);
            system.PokeRam(0x01FF, 0xBF);
            system.Cpu.SP = 0xFD;
            system.Cpu.A = a;
            system.Cpu.X = x;
            system.Cpu.Y = y;
            system.Cpu.PC = entry;
            for (int cycle = 0; cycle < 12000000; cycle++)
            {
                system.Tick();
                onTick?.Invoke(system);
                if (system.Cpu.PC == 0xC000 && system.Cpu.State == CpuState.FetchOpcode)
                {
                    return new CallResult
                    {
                        A = system.Cpu.A,
                        X = system.Cpu.X,
                        Y = system.Cpu.Y,
                        Carry = (system.Cpu.SR & 1) != 0,
                        Status = system.PeekRam(0x0090)
                    };
                }
                if (system.Cpu.State == CpuState.Jammed)
                {
                    break;
                }
            }
            throw new InvalidOperationException("Open ROM verification: call $" + entry.ToString("X4") +
                " did not return, PC=$" + system.Cpu.PC.ToString("X4") +
                ", status=$" + system.PeekRam(0x0090).ToString("X2") +
                ", CIA2=$" + system.Peek(0xDD00).ToString("X2") +
                ", drive=" + system.GetDriveDebugInfo(8));
        }

        private static int VerifyCall(string directory, ushort entry, byte a, byte x, byte y,
            byte sr, Action<CpuTraceHarness> check, Action<CpuTraceHarness> setup = null)
        {
            var harness = new CpuTraceHarness();
            harness.Reset(0x0200);
            harness.Bus.LoadRoms(null, directory);
            harness.Cpu.EnableLoadHack = false;
            harness.Bus.WriteRam(0x0200, 0x20);
            harness.Bus.WriteRam(0x0201, 0x8A);
            harness.Bus.WriteRam(0x0202, 0xFF);
            harness.Bus.WriteRam(0x0203, 0xEA);
            bool restored = false;
            for (int cycle = 0; cycle < 2000; cycle++)
            {
                harness.Cpu.Tick();
                if (harness.Cpu.PC == 0x0203 && harness.Cpu.State == CpuState.FetchOpcode)
                {
                    restored = true;
                    break;
                }
            }
            Expect(restored, "RESTOR returns");
            harness.Cpu.PC = 0x0200;
            harness.Bus.WriteRam(0x0201, (byte)entry);
            harness.Bus.WriteRam(0x0202, (byte)(entry >> 8));
            harness.Cpu.A = a;
            harness.Cpu.X = x;
            harness.Cpu.Y = y;
            harness.Cpu.SR = sr;
            setup?.Invoke(harness);
            bool returned = false;
            int elapsedCycles = 0;
            for (int cycle = 0; cycle < 2000; cycle++)
            {
                harness.Cpu.Tick();
                if (harness.Cpu.PC == 0x0203 && harness.Cpu.State == CpuState.FetchOpcode)
                {
                    returned = true;
                    elapsedCycles = cycle + 1;
                    break;
                }
            }
            Expect(returned, "call $" + entry.ToString("X4") + " returns");
            check(harness);
            return elapsedCycles;
        }

        internal static bool ScreenHasLine(C64System system, string expected)
        {
            for (int row = 0; row < 25; row++)
            {
                if (ReadScreenLine(system, row).Trim() == expected)
                {
                    return true;
                }
            }
            return false;
        }

        internal static string ReadScreenRows(C64System system)
        {
            var rows = new System.Collections.Generic.List<string>();
            for (int row = 0; row < 25; row++)
            {
                string line = ReadScreenLine(system, row).TrimEnd();
                if (line.Length > 0)
                {
                    rows.Add(row + ":" + line);
                }
            }
            return string.Join(" | ", rows);
        }

        private static string ReadScreenLine(C64System system, int row)
        {
            var line = new char[40];
            for (int column = 0; column < line.Length; column++)
            {
                byte code = system.PeekRam((ushort)(0x0400 + row * 40 + column));
                line[column] = code >= 1 && code <= 26 ? (char)('A' + code - 1) :
                    code >= 32 && code <= 63 ? (char)code : '.';
            }
            return new string(line);
        }

        private static byte[] ReadRam(C64System system, int start, int length)
        {
            var result = new byte[length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = system.PeekRam((ushort)(start + index));
            }
            return result;
        }

        private static byte[] ReadBasicString(C64System system, byte name)
        {
            int start = system.PeekRam(0x002D) | system.PeekRam(0x002E) << 8;
            int end = system.PeekRam(0x002F) | system.PeekRam(0x0030) << 8;
            for (int address = start; address + 6 < end; address += 7)
            {
                if (system.PeekRam((ushort)address) != name ||
                    system.PeekRam((ushort)(address + 1)) != 0x80)
                {
                    continue;
                }
                int length = system.PeekRam((ushort)(address + 2));
                int data = system.PeekRam((ushort)(address + 3)) |
                    system.PeekRam((ushort)(address + 4)) << 8;
                return ReadRam(system, data, length);
            }
            return Array.Empty<byte>();
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static void Expect(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Open ROM verification failed: " + name);
            }
        }
    }
}
