// SPDX-License-Identifier: Apache-2.0
using System;
using C64Emulator.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: NativeJobTests <directory containing open-1541-dos.bin>");
    return 2;
}

for (int slot = 0; slot < 5; slot++)
{
    foreach (bool decimalMode in new[] { false, true })
    {
        VerifyBufferJob(args[0], slot, 0xD0, decimalMode);
        VerifyBufferJob(args[0], slot, 0xE0, decimalMode);
    }
}

VerifyMissingDrive(args[0], 0xD1);
VerifyMissingDrive(args[0], 0xE1);
VerifyMissingDrive(args[0], 0xC1);
VerifyMissingDrive(args[0], 0xB1);
VerifyMissingBuffer(args[0], 0xD0);
VerifyMissingBuffer(args[0], 0xE0);
VerifyBump(args[0], 0, false, false);
VerifyBump(args[0], 5, true, true);
VerifySeek(args[0], 0, 1, 20, false, false);
VerifySeek(args[0], 2, 20, 18, true, true);
VerifySeek(args[0], 5, 42, 16, false, true);
foreach ((byte track, byte sector) in new (byte, byte)[]
{
    (0, 0), (43, 0), (17, 21), (18, 19), (25, 18), (31, 17), (42, 17)
})
{
    VerifyInvalidSeek(args[0], track, sector);
}
VerifySequentialSeek(args[0]);
Console.WriteLine("PASS native 1541 D0/E0 buffer jobs, B0 seek, C0 head restore, and invalid-job status");
return 0;

static Drive1541Hardware NewDrive(string romDirectory)
{
    var iec = new IecBus();
    return new Drive1541Hardware(iec.CreatePort("native-dos-job-test"), 8, false, romDirectory);
}

static void StartJob(Drive1541Hardware drive, bool decimalMode = false)
{
    // RTS from the job dispatcher lands in its existing idle loop.
    drive.WriteMemory(0x01FE, 0x93);
    drive.WriteMemory(0x01FF, 0xC1);
    drive.ExecuteAt(0xF2B0,
        new Drive1541Hardware.ExecuteContext(0, 0, 0, 0xFD,
            decimalMode ? (byte)0x2C : (byte)0x24));
}

static void RunCpuUntilJobCompletes(Drive1541Hardware drive, int slot, byte command)
{
    // Tick only the 6502: Drive1541Bus.Tick() deliberately runs the emulator's
    // host-side DOS queue service, which would hide a broken ROM implementation.
    for (int cycle = 0; cycle < 50_000 && drive.ReadMemory((ushort)slot) == command; cycle++)
    {
        drive.Cpu.Tick();
    }

    if (drive.ReadMemory((ushort)slot) == command)
    {
        throw new Exception($"ROM job {command:X2} in slot {slot} never completed");
    }
}

static void VerifyBufferJob(string romDirectory, int slot, byte command, bool decimalMode)
{
    var drive = NewDrive(romDirectory);
    byte track = (byte)(17 + slot);
    byte sector = (byte)(3 + slot);
    ushort trackSectorAddress = (ushort)(0x0006 + (slot * 2));
    ushort bufferAddress = (ushort)(0x0300 + (slot * 0x100));

    // Preserve A-at-entry and read the selected job's track/sector through
    // the DOS job-loop pointer. This code is authored solely for this test.
    drive.UploadMemory(bufferAddress, new byte[]
    {
        0x8D, 0x02, 0x02,       // STA $0202
        0xA0, 0x00,             // LDY #$00
        0xB1, 0x32,             // LDA ($32),Y
        0x8D, 0x00, 0x02,       // STA $0200
        0xC8,                   // INY
        0xB1, 0x32,             // LDA ($32),Y
        0x8D, 0x01, 0x02,       // STA $0201
        0xA2, 0xF7,             // LDX #$F7 (caller must restore job slot)
        0x60                    // RTS
    });
    drive.WriteMemory(trackSectorAddress, track);
    drive.WriteMemory((ushort)(trackSectorAddress + 1), sector);
    drive.WriteMemory(0x0033, 0xA5);
    drive.WriteMemory(0x02F0, 0x5A);
    drive.WriteMemory(0x02F1, 0xA5);
    drive.WriteMemory((ushort)slot, command);

    StartJob(drive, decimalMode);
    RunCpuUntilJobCompletes(drive, slot, command);

    Expect(drive.ReadMemory((ushort)slot) == 0x01, $"{command:X2} status in slot {slot}");
    Expect(drive.ReadMemory(0x0032) == (byte)trackSectorAddress &&
           drive.ReadMemory(0x0033) == 0x00,
        $"{command:X2} track/sector pointer in slot {slot}");
    Expect(drive.ReadMemory(0x0200) == track &&
           drive.ReadMemory(0x0201) == sector &&
           drive.ReadMemory(0x0202) == 0x00,
        $"{command:X2} uploaded code sees job track/sector and entry A in slot {slot}");
    Expect(drive.ReadMemory(0x02F0) == 0x5A &&
           drive.ReadMemory(0x02F1) == 0xA5,
        $"{command:X2} leaves unrelated DOS RAM untouched in slot {slot}");
}

static void VerifyMissingDrive(string romDirectory, byte command)
{
    var drive = NewDrive(romDirectory);
    drive.UploadMemory(0x0500, new byte[]
    {
        0xA9, 0x7E,             // LDA #$7E
        0x8D, 0x00, 0x02,       // STA $0200
        0x60                    // RTS
    });
    drive.WriteMemory(0x0002, command);
    StartJob(drive);
    RunCpuUntilJobCompletes(drive, 2, command);
    Expect(drive.ReadMemory(0x0002) == 0x0F, "missing-drive DOS status");
    Expect(drive.ReadMemory(0x0200) == 0x00, "missing-drive job does not enter buffer");
}

static void VerifyMissingBuffer(string romDirectory, byte command)
{
    var drive = NewDrive(romDirectory);
    drive.WriteMemory(0x0005, command);
    StartJob(drive);
    RunCpuUntilJobCompletes(drive, 5, command);
    Expect(drive.ReadMemory(0x0005) == 0x02, "slot 5 has no executable buffer");
}

static void VerifyBump(string romDirectory, int slot, bool motorInitiallyOn, bool decimalMode)
{
    var drive = NewDrive(romDirectory);
    byte initialDirection = motorInitiallyOn ? (byte)0x07 : (byte)0x00;
    byte initialPort = motorInitiallyOn ? (byte)0x06 : (byte)0x00;
    drive.WriteMemory((ushort)slot, 0xC0);
    StartJob(drive, decimalMode);
    // ExecuteAt prepares VIA defaults for custom code; set the intended
    // before-job state afterward so the ROM must restore it.
    drive.WriteMemory(0x1C02, initialDirection);
    drive.WriteMemory(0x1C00, initialPort);
    drive.WriteMemory(0x0034, 0xA5);
    Expect(drive.Bus.CurrentHalfTrack == 34, "drive starts on half-track 34");
    RunCpuUntilJobCompletes(drive, slot, 0xC0);

    Expect(drive.ReadMemory((ushort)slot) == 0x01, $"C0 status in slot {slot}");
    Expect(drive.Bus.CurrentHalfTrack == 0, $"C0 moved head to track-1 stop in slot {slot}");
    Expect(drive.IsMotorOn == motorInitiallyOn,
        $"C0 restored motor state in slot {slot}: {drive.Bus.GetDiskDebugInfo()}");
    Expect(drive.ReadMemory(0x1C02) == initialDirection,
        $"C0 restored disk VIA direction in slot {slot}");
    Expect(drive.ReadMemory(0x0034) == 0xA5,
        $"C0 restored DOS scratch byte in slot {slot}");
    if (motorInitiallyOn)
    {
        Expect((drive.ReadMemory(0x1C00) & 0x07) == initialPort,
            $"C0 restored motor and stepper phase in slot {slot}");
    }
}

static void VerifySeek(string romDirectory, int slot, byte track, byte sector,
    bool motorInitiallyOn, bool decimalMode)
{
    var drive = NewDrive(romDirectory);
    ushort pair = (ushort)(0x0006 + (slot * 2));
    drive.WriteMemory(pair, track);
    drive.WriteMemory((ushort)(pair + 1), sector);
    drive.WriteMemory((ushort)slot, 0xB0);
    StartJob(drive, decimalMode);
    byte initialDirection = motorInitiallyOn ? (byte)0x07 : (byte)0x00;
    byte initialPort = motorInitiallyOn ? (byte)0x06 : (byte)0x00;
    drive.WriteMemory(0x1C02, initialDirection);
    drive.WriteMemory(0x1C00, initialPort);
    drive.WriteMemory(0x0034, 0xA5);
    drive.WriteMemory(0x0035, 0x5A);
    drive.WriteMemory(0x0018, 18);
    drive.WriteMemory(0x0019, 2);
    Expect(drive.Bus.CurrentHalfTrack == 34, "drive starts on half-track 34");

    RunCpuUntilJobCompletes(drive, slot, 0xB0);

    Expect(drive.ReadMemory((ushort)slot) == 0x01, $"B0 status in slot {slot}");
    Expect(drive.Bus.CurrentHalfTrack == (track - 1) * 2,
        $"B0 reached track {track} in slot {slot}: {drive.Bus.GetDiskDebugInfo()}");
    Expect(drive.ReadMemory(0x0018) == track && drive.ReadMemory(0x0019) == sector,
        $"B0 updated DOS current track/sector in slot {slot}");
    Expect(drive.ReadMemory(0x0032) == (byte)pair && drive.ReadMemory(0x0033) == 0,
        $"B0 selected job track/sector pointer in slot {slot}");
    Expect(drive.IsMotorOn == motorInitiallyOn &&
           drive.ReadMemory(0x1C02) == initialDirection,
        $"B0 restored VIA motor and direction in slot {slot}");
    Expect(drive.ReadMemory(0x0034) == 0xA5 && drive.ReadMemory(0x0035) == 0x5A,
        $"B0 restored DOS scratch bytes in slot {slot}");
    if (motorInitiallyOn)
    {
        Expect((drive.ReadMemory(0x1C00) & 0x07) == initialPort,
            $"B0 restored port-B phase in slot {slot}");
    }
}

static void VerifyInvalidSeek(string romDirectory, byte track, byte sector)
{
    var drive = NewDrive(romDirectory);
    drive.WriteMemory(0x000A, track);   // slot 2 track/sector pair
    drive.WriteMemory(0x000B, sector);
    drive.WriteMemory(0x0002, 0xB0);
    StartJob(drive);
    drive.WriteMemory(0x0018, 18);
    drive.WriteMemory(0x0019, 2);
    int initialHalfTrack = drive.Bus.CurrentHalfTrack;

    RunCpuUntilJobCompletes(drive, 2, 0xB0);

    Expect(drive.ReadMemory(0x0002) == 0x02,
        $"B0 rejects invalid track/sector {track}/{sector}");
    Expect(drive.Bus.CurrentHalfTrack == initialHalfTrack,
        $"B0 does not step for invalid track/sector {track}/{sector}");
    Expect(drive.ReadMemory(0x0018) == 18 && drive.ReadMemory(0x0019) == 2,
        $"B0 leaves DOS current sector on invalid pair {track}/{sector}");
}

static void VerifySequentialSeek(string romDirectory)
{
    var drive = NewDrive(romDirectory);
    drive.WriteMemory(0x000A, 20);
    drive.WriteMemory(0x000B, 18);
    drive.WriteMemory(0x0002, 0xB0);
    StartJob(drive);
    RunCpuUntilJobCompletes(drive, 2, 0xB0);
    RunCpuToIdle(drive);
    Expect(drive.ReadMemory(0x0002) == 1 && drive.Bus.CurrentHalfTrack == 38,
        "first B0 reached track 20 on shared drive");

    drive.WriteMemory(0x000A, 43);
    drive.WriteMemory(0x0002, 0xB0);
    StartJob(drive);
    RunCpuUntilJobCompletes(drive, 2, 0xB0);
    RunCpuToIdle(drive);
    Expect(drive.ReadMemory(0x0002) == 2 && drive.Bus.CurrentHalfTrack == 38,
        "second B0 rejects invalid track on shared drive");
    Expect(drive.ReadMemory(0x0018) == 20 && drive.ReadMemory(0x0019) == 18,
        "invalid second B0 preserves last valid DOS sector");

    drive.WriteMemory(0x0002, 0xB1);
    StartJob(drive);
    RunCpuUntilJobCompletes(drive, 2, 0xB1);
    RunCpuToIdle(drive);
    Expect(drive.ReadMemory(0x0002) == 0x0F && drive.Bus.CurrentHalfTrack == 38,
        "B1 reports missing drive after two B0 jobs on shared drive");
}

static void RunCpuToIdle(Drive1541Hardware drive)
{
    for (int cycle = 0; cycle < 1_000 && drive.ProgramCounter != 0xC194; cycle++)
    {
        drive.Cpu.Tick();
    }

    Expect(drive.ProgramCounter == 0xC194, "job returned to the DOS idle loop");
}

static void Expect(bool condition, string description)
{
    if (!condition)
    {
        throw new Exception("FAIL " + description);
    }
}
