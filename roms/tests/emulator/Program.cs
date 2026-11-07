// SPDX-License-Identifier: Apache-2.0
// Runs the actual public emulator CPU, VIC, CIA and IEC implementation headlessly.
// It observes public KERNAL entry points; it never replaces those routines.
using C64Emulator.Core;
using OpenTK.Input;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length == 0) throw new ArgumentException("Usage: RomIntegration ROM_DIRECTORY [BASIC_COMMAND] [--suite video|machine|basic|control|editor|iec|channel|data|dos|tape|tape-format|tape-host|serial|media|media-data|dos-media|all] [--model pal|ntsc] [--json PATH]");
string romDirectory = Path.GetFullPath(args[0]);
string suite = "all", jsonPath = null, singleCommand = null, modelName = "pal";
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--suite") suite = args[++i];
    else if (args[i] == "--model") modelName = args[++i].ToLowerInvariant();
    else if (args[i] == "--json") jsonPath = Path.GetFullPath(args[++i]);
    else if (singleCommand == null) singleCommand = args[i];
    else throw new ArgumentException("Unknown argument: " + args[i]);
}
if (!new[] { "video", "machine", "basic", "control", "editor", "iec", "channel", "media", "data", "media-data", "dos", "dos-media", "tape", "tape-format", "tape-host", "serial", "all" }.Contains(suite)) throw new ArgumentException("Unknown suite: " + suite);
Directory.SetCurrentDirectory(romDirectory);
var loadedRomHashes = Directory.GetFiles(romDirectory, "*.bin").Order().ToDictionary(Path.GetFileName, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant());

// The pinned verification core resolves ROM files through RomPathResolver, whose
// search order gives the per-user ROM directory (the downloader target) priority
// over the directory passed on the command line. When both locations contain a
// complete set, the emulator would silently execute the user's files while this
// report still records the hashes of the directory under test. Fail explicitly
// before any test when the files that would actually be loaded differ.
string HashOfBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
byte[] ReadRomFile(string name) => File.Exists(Path.Combine(romDirectory, name)) ? File.ReadAllBytes(Path.Combine(romDirectory, name)) : null;
byte[] basicKernalExpected = ReadRomFile("c64-basic-kernal.bin");
if (basicKernalExpected == null)
{
    byte[] openBasic = ReadRomFile("open-basic64.bin");
    byte[] openKernal = ReadRomFile("open-kernal64.bin");
    if (openBasic != null && openKernal != null)
    {
        basicKernalExpected = new byte[openBasic.Length + openKernal.Length];
        Buffer.BlockCopy(openBasic, 0, basicKernalExpected, 0, openBasic.Length);
        Buffer.BlockCopy(openKernal, 0, basicKernalExpected, openBasic.Length, openKernal.Length);
    }
}
byte[] characterExpected = ReadRomFile("c64-character.bin") ?? ReadRomFile("open-character-rom64.bin");
byte[] driveLowerExpected = ReadRomFile("1541-c000-rom.bin");
byte[] driveUpperExpected = ReadRomFile("1541-e000-rom.bin");
byte[] driveCombinedExpected = ReadRomFile("open-1541-dos.bin");
if (driveCombinedExpected != null && driveCombinedExpected.Length >= 0x4000)
{
    if (driveLowerExpected == null)
    {
        driveLowerExpected = new byte[0x2000];
        Buffer.BlockCopy(driveCombinedExpected, 0, driveLowerExpected, 0, 0x2000);
    }
    if (driveUpperExpected == null)
    {
        driveUpperExpected = new byte[0x2000];
        Buffer.BlockCopy(driveCombinedExpected, 0x2000, driveUpperExpected, 0, 0x2000);
    }
}
var resolutionIssues = new List<string>();
void CheckRomResolution(string label, string[] compatibleNames, byte[] expected)
{
    if (expected == null)
    {
        return;
    }
    string resolved = RomPathResolver.FindFirstExisting(compatibleNames);
    if (resolved == null)
    {
        resolutionIssues.Add(label + ": not found in any search directory; the emulator cannot start.");
        return;
    }
    string resolvedHash = HashOfBytes(File.ReadAllBytes(resolved));
    if (resolvedHash != HashOfBytes(expected))
    {
        resolutionIssues.Add(string.Format(
            "{0}: the emulator would load {1} (sha256 {2}) instead of the ROM under test (sha256 {3}).",
            label, resolved, resolvedHash, HashOfBytes(expected)));
    }
}
CheckRomResolution("c64-basic-kernal.bin", new[] { "c64-basic-kernal.bin" }, basicKernalExpected);
CheckRomResolution("c64-character.bin", new[] { "c64-character.bin" }, characterExpected);
CheckRomResolution("1541-c000-rom.bin", new[] { "1541-c000-rom.bin", "1541-c000.325302-01.bin" }, driveLowerExpected);
CheckRomResolution("1541-e000-rom.bin", new[] { "1541-e000-rom.bin", "1541-e000.901229-01.bin", "1541-e000.901229-05.bin", "1541-e000.901229-03.bin", "1540-e000.325303-01.bin" }, driveUpperExpected);
if (resolutionIssues.Count > 0)
{
    Console.Error.WriteLine("ROM RESOLUTION MISMATCH: the emulator would not load the ROMs under test.");
    foreach (string issue in resolutionIssues)
    {
        Console.Error.WriteLine("  - " + issue);
    }
    Console.Error.WriteLine("  Search directories (first match wins):");
    foreach (string directory in RomPathResolver.GetSearchDirectories())
    {
        Console.Error.WriteLine("    " + directory);
    }
    Console.Error.WriteLine("  Rename or remove the conflicting ROM files, or rerun where no other ROM set is installed.");
    return 3;
}

var options = C64AccuracyOptions.Compatibility;
options.EnableLoadHack = false;
options.EnableKernalIecHooks = false;
if (modelName != "pal" && modelName != "ntsc") throw new ArgumentException("--model must be pal or ntsc.");
#if VIDEO_STANDARD_PATCH
C64Model selectedModel = modelName == "ntsc" ? C64Model.Ntsc : C64Model.Pal;
#else
if (modelName != "pal") throw new ArgumentException("NTSC requires the video-standard emulator patch.");
C64Model selectedModel = C64Model.Pal;
#endif
using var session = new Session(new C64System(selectedModel, options));
session.C64.LocalAudioEnabled = false;
session.C64.ForceSoftwareIecTransport = true;
var tests = new List<TestResult>();
string testGroup = "boot";
session.Run(2_000_000);
string boot = session.Output.ToString();
Check(modelName + "_boot", boot.Contains("**** OPEN BASIC ****") && boot.Contains("READY."), "OPEN BASIC banner and READY prompt", boot);

if (singleCommand != null)
{
    testGroup = "command";
    string output = session.Command(singleCommand, 2_000_000);
    Check("single_command", !session.Jammed, "CPU keeps executing", output);
}
else
{
    if (suite == "video" || suite == "all" || suite == "machine") RunGroup("video", () => VideoStandardTests.Run(session, Check));
    if (suite == "video" || suite == "all" || suite == "machine") RunGroup("video-sprites", () => VideoSpriteTests.Run(session, Check));
    if (suite == "basic" || suite == "all" || suite == "machine") RunGroup("basic", BasicTests);
    if (suite == "control" || suite == "all" || suite == "machine") RunGroup("control", () => RuntimeControlTests.Run(session, Check));
    if (suite == "editor" || suite == "all" || suite == "machine") RunGroup("editor", () => RuntimeControlTests.RunEditor(session, Check));
    if (suite == "editor" || suite == "all" || suite == "machine") RunGroup("editor-rc3", () => EditorRc3Tests.Run(session, MakeD64, Check));
    if (suite == "iec" || suite == "channel" || suite == "all" || suite == "machine") RunGroup("iec", IecTests);
    if (suite == "data" || suite == "all" || suite == "machine") RunGroup("data", () => DataFileTests.RunNative(session, MakeD64, Check));
    if (suite == "dos" || suite == "all" || suite == "machine") RunGroup("dos", () => DosRc3Tests.RunNative(session, MakeD64, Check));
    if (suite == "tape" || suite == "tape-format" || suite == "all" || suite == "machine") RunGroup("tape-format", () => TapeSerialRc3Tests.RunFormat(Check));
    if (suite == "tape" || suite == "all" || suite == "machine") RunGroup("tape", () => TapeSerialRc3Tests.RunTape(session, Check));
    if (suite == "serial" || suite == "all" || suite == "machine") RunGroup("serial", () => NativeSerialRc3Tests.Run(session, Check));
    if (suite == "media" || (suite == "all" && typeof(D64Image).GetMethod("TrySavePrg") != null)) RunGroup("media", MediaTests);
    if (suite == "media-data" || (suite == "all" && typeof(D64Image).GetMethod("TryWriteSequentialFile") != null)) RunGroup("media-data", () => DataFileTests.RunMedia(MakeD64, Check));
    if (suite == "dos-media" || suite == "all") RunGroup("dos-media", () => DosRc3Tests.RunMedia(MakeD64, Check));
    if (suite == "tape-host" || suite == "all" || suite == "machine") RunGroup("tape-host", () => TapeHostTests.Run(selectedModel, Check));
}
var report = new
{
    rom_directory = romDirectory,
    rom_sha256 = loadedRomHashes,
    emulator_source_commit = "f01750e17a2c3f4cabfd57dc0c470dac93a91a23",
    emulator_save_patch = typeof(D64Image).GetMethod("TrySavePrg") != null,
    emulator_data_file_patch = typeof(D64Image).GetMethod("TryPrepareSequentialWrite") != null,
    emulator_restore_key_patch = RuntimeControlTests.RestoreKeyPatch,
    observed_nmi_entries = session.NmiEntries,
    model = modelName.ToUpperInvariant(),
    cpu_hz = selectedModel.CpuHz,
    raster_lines = selectedModel.RasterLines,
    cycles_per_line = selectedModel.CyclesPerLine,
    frame_hz = selectedModel.CpuHz / (selectedModel.RasterLines * selectedModel.CyclesPerLine),
    load_hack = false,
    kernal_iec_hooks = false,
    drive_transport = "real C64 CIA2 IEC signaling to software drive; supplied 1541 ROM provides bootstrap and drive CPU helper ABI",
    state = session.C64.Cpu.State.ToString(),
    pc = session.C64.Cpu.PC.ToString("X4"),
    sp = session.C64.Cpu.SP.ToString("X2"),
    public_entries = session.Entries.ToDictionary(k => k.Key.ToString("X4"), v => v.Value),
    getin_trace = session.GetinTrace,
    passed = tests.Count(t => t.Passed),
    failed = tests.Count(t => !t.Passed),
    groups = tests.GroupBy(t => t.Group).Select(g => new { name = g.Key, passed = g.Count(t => t.Passed), failed = g.Count(t => !t.Passed) }),
    tests,
    screen = session.Screen(),
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (jsonPath != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
    File.WriteAllText(jsonPath, json + "\n");
}
return tests.Any(t => !t.Passed) ? 1 : 0;

void RunGroup(string name, Action action)
{
    testGroup = name;
    action();
}
void Check(string name, bool passed, string expected, string output)
{
    passed &= !session.Jammed;
    tests.Add(new TestResult(name, testGroup, passed, expected, output, passed ? null : session.Recent.Select(v => v.ToString()).ToArray()));
    Console.Error.WriteLine($"{name}: {(passed ? "PASS" : "FAIL")}");
}
void Contains(string name, string command, string expected)
{
    string output = session.Command(command);
    // Remove the echoed command before matching the result.
    string value = ResultText(output, command);
    Check(name, value.Contains(expected) && !value.Contains("?"), expected, output);
}
void ProgramLines(params string[] lines)
{
    session.Command("NEW");
    foreach (string line in lines) session.Command(line);
}
void BasicTests()
{
    Contains("keyboard_paste_buffer_boundary", "PRINT\"0123456789012345678901234567890123456789\"", "0123456789012345678901234567890123456789");
    Contains("integer_arithmetic", "PRINT 1+1", "2");
    Contains("operator_precedence", "PRINT 2+3*4", "14");
    Contains("decimal_arithmetic", "PRINT 1.25+.5", "1.75");
    Contains("square_root", "PRINT SQR(81)", "9");
    Contains("string_slices", "PRINT LEFT$(\"ABCDE\",2);\":\";RIGHT$(\"ABCDE\",2);\":\";MID$(\"ABCDE\",2,3)", "AB:DE:BCD");
    ProgramLines("10 REM VERIFIED", "20 PRINT\"REM OK\"");
    Contains("rem_program", "RUN", "REM OK");
    ProgramLines("10 DATA\"A:B\",PRINT", "20 READ A$,B$", "30 PRINT A$;\":\";B$");
    Contains("quoted_data_colon", "RUN", "A:B:PRINT");
    ProgramLines("10 FOR I=1 TO 2 STEP .5", "20 PRINT I;", "30 NEXT I");
    string output = session.Command("RUN");
    string flat = Regex.Replace(ResultText(output, "RUN"), @"\s+", " ");
    Check("fractional_for_step", flat.Contains("1 1.5 2") && !flat.Contains("?"), "1 1.5 2", output);
    ProgramLines("10 GOSUB 100:PRINT\"DONE\":END", "100 PRINT\"SUB\":RETURN");
    output = session.Command("RUN");
    string value = ResultText(output, "RUN");
    Check("gosub_return", value.Contains("SUB") && value.Contains("DONE") && !value.Contains("?"), "SUB then DONE", output);
    output = session.Command("PRINT 1/0");
    Check("division_by_zero", ResultText(output, "PRINT 1/0").Contains("DIVISION BY ZERO"), "DIVISION BY ZERO error", output);
    Contains("error_recovery", "PRINT 3", "3");
    output = session.Command("POKE 49152,173");
    Check("poke_memory", session.C64.PeekRam(0xC000) == 173, "$C000 = 173", output);
    Contains("peek_memory", "PRINT PEEK(49152)", "173");
    Contains("sys_returns", "POKE 49152,96:SYS 49152:PRINT\"SYS OK\"", "SYS OK");
    int matrixStart = session.Output.Length;
    bool previousInjection = session.C64.EnableInputInjection;
    session.C64.EnableInputInjection = false;
    session.C64.SetHostKeyboardLayout(HostKeyboardLayout.En);
    foreach (Key key in new[] { Key.P, Key.R, Key.I, Key.N, Key.T, Key.Space, Key.Number1, Key.Plus, Key.Number1, Key.Enter })
    {
        // The frontend's English layout maps Shift+= to the C64 '+' key.
        if (key == Key.Plus) session.C64.KeyboardKeyDown(Key.ShiftLeft);
        session.C64.KeyboardKeyDown(key);
        session.Run(30_000);
        session.C64.KeyboardKeyUp(key);
        if (key == Key.Plus) session.C64.KeyboardKeyUp(Key.ShiftLeft);
        session.Run(30_000);
    }
    session.Run(1_000_000);
    output = session.Output.ToString(matrixStart, session.Output.Length - matrixStart);
    session.C64.EnableInputInjection = previousInjection;
    value = ResultText(output, "PRINT 1+1");
    Check("physical_cia_keyboard", Regex.IsMatch(value, @"(^|\s)2(\s|$)") && value.Contains("READY.") && !value.Contains("?"),
        "CIA1 matrix key presses type PRINT 1+1 and produce 2 with host input injection disabled", output);
}
void IecTests()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "open-rom-integration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    string diskPath = Path.Combine(tempDirectory, "fixture.d64");
    byte[] fixturePrg = MakePrg("IEC LOAD OK");
    File.WriteAllBytes(diskPath, MakeD64(fixturePrg));
    try
    {
        string mount = session.C64.MountMedia(diskPath, 8);
        Check("mount_fixture_d64", mount.Contains("MOUNTED"), "D64 mounted in drive 8", mount);
        session.Command("NEW");
        long loads = session.EntryCount(0xFFD5);
        string output = session.Command("LOAD\"HELLO\",8", 12_000_000);
        bool bytesMatch = fixturePrg.Skip(2).Select((b, i) => session.C64.PeekRam((ushort)(0x0801 + i)) == b).All(v => v);
        bool serialUsed = session.EntryCount(0xFFD5) > loads && session.C64.GetDriveDebugInfo(8).Contains("recent=");
        Check("iec_load_bytes", bytesMatch && serialUsed && !ResultText(output, "LOAD\"HELLO\",8").Contains("?"), "Exact PRG bytes at $0801; public LOAD executed with IEC shortcuts disabled", output);
        Contains("iec_loaded_program_run", "RUN", "IEC LOAD OK");
        ProgramLines("10 OPEN 2,8,0,\"HELLO\"", "20 GET#2,A$,B$", "30 PRINT ASC(A$);\":\";ASC(B$)", "40 CLOSE 2");
        long opens = session.EntryCount(0xFFC0), closes = session.EntryCount(0xFFC3);
        output = session.Command("RUN", 3_000_000);
        string byteValues = Regex.Replace(ResultText(output, "RUN"), @"\s+", "");
        Check("iec_open_get_close", byteValues.Contains("1:8") && !byteValues.Contains("?") &&
            session.EntryCount(0xFFC0) > opens && session.EntryCount(0xFFC3) > closes,
            "OPEN/GET#/CLOSE reads exact PRG address bytes $01 and $08 through public KERNAL entries", output);
        ProgramLines("10 OPEN 2,8,0,\"HELLO\"", "20 GET#2,A$", "30 GET#2,B$", "40 PRINT ASC(A$);\":\";ASC(B$)", "50 CLOSE 2");
        output = session.Command("RUN", 3_000_000);
        byteValues = Regex.Replace(ResultText(output, "RUN"), @"\s+", "");
        Check("iec_separate_get_statements", byteValues.Contains("1:8") && !byteValues.Contains("?"),
            "Separate GET# statements retain the next unread channel byte across UNTALK/TALK", output);
        ProgramLines("10 OPEN 2,8,0,\"HELLO\"", $"20 S=0:FOR I=1 TO {fixturePrg.Length}",
            "30 GET#2,A$:S=S+ASC(A$+CHR$(0)):NEXT", "40 PRINT\"END\";S;\":\";ST",
            "50 GET#2,B$:PRINT\"EXTRA\";ST;\":\";LEN(B$)", "60 CLOSE 2");
        output = session.Command("RUN", 6_000_000);
        byteValues = Regex.Replace(ResultText(output, "RUN"), @"\s+", "");
        Check("iec_channel_eof", byteValues.Contains($"END{fixturePrg.Sum(b => (int)b)}:64") && byteValues.Contains("EXTRA64:0") && !byteValues.Contains("?"),
            "Read entire file with exact checksum and EOI=64; one additional GET# returns empty without timeout", output);
        ProgramLines("10 OPEN 2,8,2,\"DATA,S,R\"", "20 INPUT#2,N,A$", "30 PRINT N;\":\";A$", "40 CLOSE 2");
        output = session.Command("RUN", 3_000_000);
        byteValues = Regex.Replace(ResultText(output, "RUN"), @"\s+", "");
        Check("iec_input_number_and_string", byteValues.Contains("123:ABC") && !byteValues.Contains("?"),
            "INPUT# reads number 123 and string ABC from a sequential file through real IEC", output);
        if (suite == "channel") return;
        ProgramLines("10 PRINT\"IEC SAVE OK\"", "20 END");
        byte[] expectedSavedPrg = session.BasicProgramBytes();
        output = session.Command("SAVE\"ROUNDTRIP\",8", 12_000_000);
        string value = ResultText(output, "SAVE\"ROUNDTRIP\",8");
        Check("iec_save_returns", value.Contains("READY.") && !value.Contains("?"), "SAVE returns without an error", output);
        session.Command("NEW");
        output = session.Command("LOAD\"ROUNDTRIP\",8", 12_000_000);
        Check("iec_saved_file_reload", !ResultText(output, "LOAD\"ROUNDTRIP\",8").Contains("?") && output.Contains("READY."), "Saved PRG can be reloaded", output);
        Contains("iec_save_load_roundtrip", "RUN", "IEC SAVE OK");
        bool onDisk = D64Image.Load(diskPath).TryResolveLoad("ROUNDTRIP", out MediaLoadData saved);
        Check("iec_save_persists_d64", onDisk && expectedSavedPrg.Length > 2 && saved.ProgramBytes.SequenceEqual(expectedSavedPrg), "Freshly reopened host D64 contains exact saved PRG bytes", onDisk ? Convert.ToHexString(saved.ProgramBytes) : "No ROUNDTRIP entry in reopened image.");
        // This independently generated target is shorter than the loader, so the
        // preserved variable table is outside the bytes replaced by LOAD.
        byte[] chainPrg = MakeTokenPrg(new byte[] { 0x99, 0x22 }.Concat(Encoding.ASCII.GetBytes("CHAIN"))
            .Concat(new byte[] { 0x22, 0x3B, 0x41 }).ToArray());
        string chainPath = Path.Combine(tempDirectory, "chain.d64");
        File.WriteAllBytes(chainPath, MakeD64(chainPrg));
        session.C64.MountMedia(chainPath, 8);
        ProgramLines("10 A=42", "20 LOAD\"HELLO\",8", "30 PRINT\"OLD PROGRAM RESUMED\"",
            "40 REM KEEP VARIABLES BEYOND THE SMALL CHAIN TARGET");
        bool smaller = session.BasicProgramBytes().Length > chainPrg.Length;
        output = session.Command("RUN", 12_000_000);
        value = Regex.Replace(ResultText(output, "RUN"), @"\s+", "");
        Check("iec_program_load_chain", smaller && value.Contains("CHAIN42") && !value.Contains("OLDPROGRAMRESUMED") && !value.Contains("?"),
            "In-program LOAD reruns the smaller loaded program from its start and preserves A=42", output);
        if (tests.Any(t => t.Name.StartsWith("iec_") && !t.Passed))
            Check("iec_diagnostics", false, "Diagnostic context for preceding IEC failures", session.C64.GetIecDebugInfo() + "\n" + session.C64.GetDriveDebugInfo(8));
    }
    finally
    {
        session.C64.EjectMedia();
        Directory.Delete(tempDirectory, true);
    }
}
void MediaTests()
{
    var saveMethod = typeof(D64Image).GetMethod("TrySavePrg");
    if (saveMethod == null)
    {
        Check("media_save_api", false, "Optional emulator SAVE patch is compiled", "TrySavePrg is absent.");
        return;
    }
    string directory = Path.Combine(Path.GetTempPath(), "open-rom-media-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "save.d64");
    File.WriteAllBytes(path, MakeD64(MakePrg("ORIGINAL")));
    var image = D64Image.Load(path);
    (bool Ok, string Status) Save(D64Image target, string name, byte[] data)
    {
        object[] parameters = { name, data, null };
        bool result = (bool)saveMethod.Invoke(target, parameters);
        return (result, (string)parameters[2]);
    }
    byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 29 + length)).ToArray();
    try
    {
        foreach (int size in new[] { 2, 253, 254, 255, 256, 508, 509, 4096 })
        {
            string name = "P" + size;
            byte[] expected = Payload(size);
            var result = Save(image, "0:" + name + ",P,W", expected);
            bool found = D64Image.Load(path).TryResolveLoad(name, out MediaLoadData read);
            Check("media_save_" + size, result.Ok && found && read.ProgramBytes.SequenceEqual(expected),
                "Persisted PRG is byte-identical across sector and directory boundaries", result.Status);
        }
        byte[] before = File.ReadAllBytes(path);
        var duplicate = Save(image, "P2", Payload(8));
        Check("media_duplicate_preserves_image", !duplicate.Ok && duplicate.Status.StartsWith("63,") && before.SequenceEqual(File.ReadAllBytes(path)),
            "Existing file is rejected without modifying the image", duplicate.Status);
        byte[] replacement = Payload(600);
        var replaced = Save(image, "@0:P2", replacement);
        bool exists = D64Image.Load(path).TryResolveLoad("P2", out MediaLoadData replacedData);
        Check("media_explicit_replace", replaced.Ok && exists && replacedData.ProgramBytes.SequenceEqual(replacement),
            "Explicit @0: replacement persists the complete new file", replaced.Status);
        before = File.ReadAllBytes(path);
        var full = Save(image, "TOOBIG", Payload(200_000));
        Check("media_full_is_transactional", !full.Ok && full.Status.StartsWith("72,") && before.SequenceEqual(File.ReadAllBytes(path)) && !image.TryResolveLoad("TOOBIG", out _),
            "Disk-full failure leaves on-disk and in-memory image unchanged", full.Status);
        var invalid = Save(image, "BAD*,S,W", Payload(20));
        Check("media_invalid_name_or_type", !invalid.Ok && before.SequenceEqual(File.ReadAllBytes(path)),
            "Unsupported filename/type is rejected without modification", invalid.Status);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var protectedImage = D64Image.Load(path);
        var protectedSave = Save(protectedImage, "BLOCKED", Payload(20));
        Check("media_write_protection", !protectedSave.Ok && protectedSave.Status.StartsWith("26,") && before.SequenceEqual(File.ReadAllBytes(path)),
            "Write-protected D64 is unchanged", protectedSave.Status);
        File.SetAttributes(path, FileAttributes.Normal);
        string errorsPath = Path.Combine(directory, "errors.d64");
        byte[] errorTable = Enumerable.Range(0, 683).Select(i => (byte)(1 + i % 11)).ToArray();
        File.WriteAllBytes(errorsPath, MakeD64(MakePrg("ORIGINAL")).Concat(errorTable).ToArray());
        var errorsImage = D64Image.Load(errorsPath);
        var withErrors = Save(errorsImage, "ERRORS", Payload(300));
        byte[] updated = File.ReadAllBytes(errorsPath);
        Check("media_error_table_preserved", withErrors.Ok && updated.Length == 175531 && updated.Skip(174848).SequenceEqual(errorTable),
            "D64 error-information suffix survives a SAVE unchanged", withErrors.Status);
    }
    finally
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
}
static string ResultText(string output, string command)
{
    int echo = output.IndexOf(command, StringComparison.Ordinal);
    return echo >= 0 ? output[(echo + command.Length)..] : output;
}
static byte[] MakePrg(string message)
{
    byte[] tokens = new byte[] { 0x99, 0x20, 0x22 }.Concat(Encoding.ASCII.GetBytes(message)).Concat(new byte[] { 0x22 }).ToArray();
    return MakeTokenPrg(tokens);
}
static byte[] MakeTokenPrg(byte[] tokens)
{
    int next = 0x0801 + 4 + tokens.Length + 1;
    return new byte[] { 1, 8, (byte)next, (byte)(next >> 8), 10, 0 }.Concat(tokens).Concat(new byte[] { 0, 0, 0 }).ToArray();
}
static byte[] MakeD64(byte[] prg)
{
    byte[] disk = new byte[174848];
    int Sectors(int track) => track <= 17 ? 21 : track <= 24 ? 19 : track <= 30 ? 18 : 17;
    int Offset(int track, int sector) => (Enumerable.Range(1, track - 1).Sum(Sectors) + sector) * 256;
    int bam = Offset(18, 0), directory = Offset(18, 1), data = Offset(1, 0);
    disk[bam] = 18; disk[bam + 1] = 1; disk[bam + 2] = 0x41;
    for (int track = 1; track <= 35; track++)
    {
        int at = bam + track * 4, count = Sectors(track);
        disk[at] = (byte)count;
        for (int sector = 0; sector < count; sector++) disk[at + 1 + sector / 8] |= (byte)(1 << (sector % 8));
    }
    void Used(int track, int sector)
    {
        int at = bam + track * 4;
        disk[at]--; disk[at + 1 + sector / 8] &= (byte)~(1 << (sector % 8));
    }
    Used(18, 0); Used(18, 1); Used(1, 0);
    Array.Fill(disk, (byte)0xA0, bam + 0x90, 16);
    Encoding.ASCII.GetBytes("ROM TEST").CopyTo(disk, bam + 0x90);
    disk[bam + 0xA2] = (byte)'0'; disk[bam + 0xA3] = (byte)'1';
    disk[bam + 0xA5] = (byte)'2'; disk[bam + 0xA6] = (byte)'A';
    disk[directory] = 0; disk[directory + 1] = 255;
    disk[directory + 2] = 0x82; disk[directory + 3] = 1; disk[directory + 4] = 0;
    Array.Fill(disk, (byte)0xA0, directory + 5, 16);
    Encoding.ASCII.GetBytes("HELLO").CopyTo(disk, directory + 5);
    disk[directory + 30] = 1;
    disk[data] = 0; disk[data + 1] = (byte)(prg.Length + 1);
    prg.CopyTo(disk, data + 2);
    // A second sequential file exercises INPUT# without a PRG load-address prefix.
    Used(1, 1);
    int textEntry = directory + 32, textData = Offset(1, 1);
    disk[textEntry + 2] = 0x81; disk[textEntry + 3] = 1; disk[textEntry + 4] = 1;
    Array.Fill(disk, (byte)0xA0, textEntry + 5, 16);
    Encoding.ASCII.GetBytes("DATA").CopyTo(disk, textEntry + 5);
    disk[textEntry + 30] = 1;
    byte[] textBytes = Encoding.ASCII.GetBytes("123,ABC\r");
    disk[textData + 1] = (byte)(textBytes.Length + 1);
    textBytes.CopyTo(disk, textData + 2);
    return disk;
}
record TestResult(string Name, string Group, bool Passed, string Expected, string Output, string[] Recent);
sealed class Session(C64System c64) : IDisposable
{
    public C64System C64 { get; } = c64;
    public StringBuilder Output { get; } = new();
    public Dictionary<ushort, long> Entries { get; } = new();
    public Queue<Trace> Recent { get; } = new();
    public List<string> GetinTrace { get; } = new();
    public ushort? NmiHandler { get; set; }
    public int NmiEntries { get; private set; }
    private ushort getinReturn;
    private byte getinReturnSp;
    private bool getinPending;
    public bool Jammed => C64.Cpu.State == CpuState.Jammed;
    public long EntryCount(ushort address) => Entries.GetValueOrDefault(address);
    public void Run(int cycles)
    {
        for (int i = 0; i < cycles && !Jammed; i++)
        {
            bool fetch = C64.Cpu.State == CpuState.FetchOpcode;
            ushort pc = C64.Cpu.PC;
            byte a = C64.Cpu.A, x = C64.Cpu.X, y = C64.Cpu.Y, sp = C64.Cpu.SP;
            C64.Tick();
            // VIC stalls can leave the CPU on the same FetchOpcode for many ticks.
            // Count only the transition that really fetched this instruction.
            if (fetch && C64.Cpu.State == CpuState.ExecuteInstruction && C64.Cpu.LastOpcodeAddress == pc)
            {
                if (pc == NmiHandler) NmiEntries++;
                if (pc >= 0xFF81 && pc <= 0xFFF3) Entries[pc] = EntryCount(pc) + 1;
                if (pc == 0xFFE4)
                {
                    getinReturn = (ushort)(1 + (C64.PeekRam((ushort)(0x100 | (byte)(sp + 1))) |
                        (C64.PeekRam((ushort)(0x100 | (byte)(sp + 2))) << 8)));
                    getinReturnSp = (byte)(sp + 2);
                    getinPending = true;
                    GetinTrace.Add($"entry PC={pc:X4} A={a:X2} X={x:X2} Y={y:X2} SP={sp:X2} return={getinReturn:X4} input99={C64.PeekRam(0x99):X2} deviceBA={C64.PeekRam(0xBA):X2} status90={C64.PeekRam(0x90):X2} 0349={C64.PeekRam(0x0349):X2} 034F={C64.PeekRam(0x034F):X2}");
                }
                else if (getinPending && pc == getinReturn && sp == getinReturnSp)
                {
                    getinPending = false;
                    GetinTrace.Add($"return PC={pc:X4} A={a:X2} X={x:X2} Y={y:X2} SP={sp:X2} P={C64.Cpu.SR:X2} status90={C64.PeekRam(0x90):X2} data0351={C64.PeekRam(0x0351):X2} " + C64.GetIecDebugInfo());
                }
                if (pc == 0xFFD2)
                {
                    if (a == 13) Output.Append('\n');
                    else if (a == 29) Output.Append(' '); // PETSCII cursor-right numeric separator.
                    else if (a is >= 32 and <= 126) Output.Append((char)a);
                    else if (a is >= 0xC1 and <= 0xDA) Output.Append((char)(a - 128));
                }
                Recent.Enqueue(new Trace(pc, sp, a, x, y));
                if (Recent.Count > 24) Recent.Dequeue();
            }
        }
    }
    public string Command(string command, int cycles = 1_000_000)
    {
        int start = Output.Length;
        C64.EnqueuePetsciiText(command + "\r");
        Run(cycles);
        return Output.ToString(start, Output.Length - start);
    }
    public byte[] BasicProgramBytes()
    {
        const int start = 0x0801;
        int line = start;
        for (int guard = 0; guard < 1000; guard++)
        {
            int next = C64.PeekRam((ushort)line) | (C64.PeekRam((ushort)(line + 1)) << 8);
            if (next == 0)
                return new byte[] { 1, 8 }.Concat(Enumerable.Range(start, line + 2 - start).Select(i => C64.PeekRam((ushort)i))).ToArray();
            if (next <= line || next >= 0xA000) return Array.Empty<byte>();
            line = next;
        }
        return Array.Empty<byte>();
    }
    public string Screen()
    {
        var sb = new StringBuilder();
        for (int row = 0; row < 25; row++)
        {
            var line = new StringBuilder();
            for (int col = 0; col < 40; col++)
            {
                byte b = (byte)(C64.PeekRam((ushort)((C64.PeekRam(0x0288) << 8) + row * 40 + col)) & 127);
                line.Append(b < 32 ? (char)(b + 64) : b < 96 ? (char)b : '.');
            }
            sb.AppendLine(line.ToString().TrimEnd());
        }
        return sb.ToString().TrimEnd();
    }
    public void Dispose() => C64.Dispose();
}

readonly record struct Trace(ushort Pc, byte Sp, byte A, byte X, byte Y)
{
    public override string ToString() => $"{Pc:X4}:SP{Sp:X2}:A{A:X2}:X{X:X2}:Y{Y:X2}";
}
