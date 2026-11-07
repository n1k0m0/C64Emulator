// SPDX-License-Identifier: Apache-2.0
// Data-file tests use generated temporary D64 images and the real emulator IEC bus.
using C64Emulator.Core;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

static class DataFileTests
{
    public static void RunMedia(Func<byte[], byte[]> makeDisk, Action<string, bool, string, string> check)
    {
        MethodInfo write = typeof(D64Image).GetMethod("TryWriteSequentialFile");
        MethodInfo prepare = typeof(D64Image).GetMethod("TryPrepareSequentialWrite");
        if (write == null || prepare == null)
        {
            check("data_file_api", false, "RC2 sequential-write APIs are compiled", "Missing host file I/O patch.");
            return;
        }
        string directory = Path.Combine(Path.GetTempPath(), "open-rom-data-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "data.d64");
        File.WriteAllBytes(path, makeDisk(new byte[] { 1, 8, 0, 0 }));
        var disk = D64Image.Load(path);
        (bool Ok, string Status) Write(D64Image target, string name, byte[] data)
        {
            object[] parameters = { name, data, null };
            bool ok = (bool)write.Invoke(target, parameters);
            return (ok, (string)parameters[2]);
        }
        void Reject(string name, string specification, string code, byte[] data)
        {
            byte[] before = File.ReadAllBytes(path);
            var result = Write(disk, specification, data);
            check(name, !result.Ok && result.Status.StartsWith(code + ",") && before.SequenceEqual(File.ReadAllBytes(path)),
                "DOS " + code + "; mounted host image is unchanged", result.Status);
        }
        try
        {
            foreach (char type in new[] { 'S', 'U' })
            {
                foreach (int size in new[] { 0, 1, 253, 254, 255, 508, 509, 4096 })
                {
                    string name = type + size.ToString();
                    byte[] initial = Payload(size), appended = new byte[] { 0, 255, 13, 128, 65 };
                    var created = Write(disk, "0:" + name + "," + type + ",W", initial);
                    byte expectedType = type == 'S' ? (byte)0x81 : (byte)0x83;
                    check("data_create_" + name, created.Ok && ReadFile(disk, name).SequenceEqual(initial) &&
                        ReadFile(D64Image.Load(path), name).SequenceEqual(initial) && EntryType(File.ReadAllBytes(path), name) == expectedType,
                        "Exact empty/binary stream persists with the requested closed directory type", created.Status);
                    var added = Write(disk, name + "," + type + ",A", appended);
                    byte[] expected = initial.Concat(appended).ToArray();
                    check("data_append_" + name, added.Ok && ReadFile(disk, name).SequenceEqual(expected) &&
                        ReadFile(D64Image.Load(path), name).SequenceEqual(expected) && EntryType(File.ReadAllBytes(path), name) == expectedType,
                        "Append preserves the prefix, type and binary suffix across sector boundaries", added.Status);
                }
            }
            check("data_files_are_not_prg_loads", !disk.TryResolveLoad("S1", out _) && !disk.TryResolveLoad("U1", out _),
                "PRG resolver does not treat SEQ/USR files as loadable programs", "SEQ/USR entries remain data files.");
            Reject("data_duplicate_rejected", "S1,S,W", "63", Payload(5));
            Reject("data_append_missing_rejected", "MISSING,A", "62", Payload(5));
            Reject("data_append_type_mismatch", "S1,U,A", "64", Payload(5));
            Reject("data_replace_type_mismatch", "@0:S1,U,W", "64", Payload(5));
            Reject("data_relative_unsupported", "RELATIVE,L,W", "64", Payload(5));
            Reject("data_write_wildcard_rejected", "NEW*,S,W", "33", Payload(5));
            Reject("data_wrong_access_rejected", "S1,S,R", "64", Payload(5));
            var replacement = Write(disk, "@0:U1,U,W", new byte[] { 7, 0, 9 });
            check("data_explicit_replace", replacement.Ok && ReadFile(D64Image.Load(path), "U1").SequenceEqual(new byte[] { 7, 0, 9 }) &&
                EntryType(File.ReadAllBytes(path), "U1") == 0x83, "@ replacement preserves USR type and exact data", replacement.Status);
            var inferred = Write(disk, "U1,A", new byte[] { 10, 0 });
            check("data_append_infers_existing_type", inferred.Ok && ReadFile(D64Image.Load(path), "U1").SequenceEqual(new byte[] { 7, 0, 9, 10, 0 }),
                "Plain ,A keeps the existing USR type", inferred.Status);
            var longForm = Write(disk, "0:LONG,SEQUENTIAL,WRITE", Encoding.ASCII.GetBytes("AB"));
            var longAppend = Write(disk, "0:LONG,APPEND", Encoding.ASCII.GetBytes("CD"));
            check("data_long_access_names", longForm.Ok && longAppend.Ok && Encoding.ASCII.GetString(ReadFile(D64Image.Load(path), "LONG")) == "ABCD",
                "Documented long SEQUENTIAL/WRITE/APPEND spellings work", longForm.Status + " / " + longAppend.Status);
            var wildcard = Write(disk, "L?NG,A", new byte[] { 69 });
            check("data_append_existing_pattern", wildcard.Ok && Encoding.ASCII.GetString(ReadFile(D64Image.Load(path), "LONG")) == "ABCDE",
                "Append pattern resolves an existing file rather than creating a wildcard name", wildcard.Status);
            object[] prepared = { "U?,A", (byte)1, null, null };
            bool preparedOk = (bool)prepare.Invoke(disk, prepared);
            check("data_open_resolves_append_target", preparedOk && (string)prepared[2] == "0:U0,U,A",
                "OPEN fixes inferred type and first matching filename before buffering data", (string)prepared[2] + " / " + (string)prepared[3]);
            byte[] unchanged = File.ReadAllBytes(path);
            var emptyAppend = Write(disk, "LONG,A", Array.Empty<byte>());
            check("data_empty_append_no_change", emptyAppend.Ok && unchanged.SequenceEqual(File.ReadAllBytes(path)),
                "Empty append leaves the existing image byte-identical", emptyAppend.Status);
            var wrongRead = typeof(D64Image).GetMethod("TryOpenSequentialFile", new[] { typeof(string), typeof(D64Image.SequentialFileReader).MakeByRefType(), typeof(string).MakeByRefType() });
            object[] readArguments = { "S1,U,R", null, null };
            bool readOk = (bool)wrongRead.Invoke(disk, readArguments);
            check("data_read_type_mismatch", !readOk && ((string)readArguments[2]).StartsWith("64,"),
                "Wrong explicit read type reports DOS 64", (string)readArguments[2]);

            File.SetAttributes(path, FileAttributes.ReadOnly);
            var protectedWrite = Write(disk, "LONG,A", new byte[] { 1 });
            check("data_live_write_protection", !protectedWrite.Ok && protectedWrite.Status.StartsWith("26,") && unchanged.SequenceEqual(File.ReadAllBytes(path)),
                "Changing host write protection after mount prevents append and preserves the image", protectedWrite.Status);
            File.SetAttributes(path, FileAttributes.Normal);

            string fullPath = Path.Combine(directory, "full.d64");
            byte[] full = makeDisk(new byte[] { 1, 8, 0, 0 });
            Array.Clear(full, SectorOffset(18, 0) + 4, 35 * 4);
            File.WriteAllBytes(fullPath, full);
            var fullDisk = D64Image.Load(fullPath);
            byte[] old = ReadFile(fullDisk, "DATA");
            var fullResult = Write(fullDisk, "DATA,S,A", Payload(400));
            check("data_append_full_rolls_back", !fullResult.Ok && fullResult.Status.StartsWith("72,") && full.SequenceEqual(File.ReadAllBytes(fullPath)) && ReadFile(fullDisk, "DATA").SequenceEqual(old),
                "Actual allocation failure preserves old file, BAM and host image", fullResult.Status);

            string corruptPath = Path.Combine(directory, "corrupt.d64");
            byte[] corrupt = makeDisk(new byte[] { 1, 8, 0, 0 });
            int dataAt = SectorOffset(1, 1); corrupt[dataAt] = 1; corrupt[dataAt + 1] = 1;
            File.WriteAllBytes(corruptPath, corrupt);
            var corruptResult = Write(D64Image.Load(corruptPath), "DATA,S,A", Payload(5));
            check("data_append_corrupt_chain_rolls_back", !corruptResult.Ok && corruptResult.Status.StartsWith("71,") && corrupt.SequenceEqual(File.ReadAllBytes(corruptPath)),
                "A cyclic existing file chain is rejected without mutation", corruptResult.Status);

            string lockedPath = Path.Combine(directory, "locked.d64");
            byte[] locked = makeDisk(new byte[] { 1, 8, 0, 0 });
            locked[SectorOffset(18, 1) + 34] |= 0x40;
            File.WriteAllBytes(lockedPath, locked);
            var lockedResult = Write(D64Image.Load(lockedPath), "DATA,A", Payload(5));
            check("data_locked_append_rejected", !lockedResult.Ok && lockedResult.Status.StartsWith("26,") && locked.SequenceEqual(File.ReadAllBytes(lockedPath)),
                "A locked data file is preserved", lockedResult.Status);

            string failurePath = Path.Combine(directory, "failure.d64");
            File.WriteAllBytes(failurePath, makeDisk(new byte[] { 1, 8, 0, 0 }));
            var failureDisk = D64Image.Load(failurePath);
            byte[] original = ReadFile(failureDisk, "DATA");
            File.Delete(failurePath); Directory.CreateDirectory(failurePath);
            var failure = Write(failureDisk, "DATA,A", Payload(3));
            check("data_host_failure_rolls_back_memory", !failure.Ok && (failure.Status.StartsWith("25,") || failure.Status.StartsWith("26,")) && ReadFile(failureDisk, "DATA").SequenceEqual(original),
                "Host commit failure leaves mounted file data unchanged", failure.Status);
            Directory.Delete(failurePath);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    public static void RunNative(Session session, Func<byte[], byte[]> makeDisk, Action<string, bool, string, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "open-rom-data-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "data.d64");
        File.WriteAllBytes(path, makeDisk(new byte[] { 1, 8, 0, 0 }));
        void Program(params string[] lines)
        {
            session.Command("NEW", 500_000);
            foreach (string line in lines) session.Command(line, 500_000);
        }
        string Run() => session.Command("RUN", 4_000_000);
        string Flat(string text) => Regex.Replace(text, @"\s+", "");
        try
        {
            session.C64.MountMedia(path, 8);
            Program("10 OPEN 15,8,15", "20 OPEN 2,8,2,\"0:NOTES,S,W\"", "30 PRINT#2,\"123,ABC\"", "40 CLOSE 2",
                "50 INPUT#15,E,M$,T,S", "60 PRINT\"WRITE\";E;M$", "70 OPEN 2,8,2,\"NOTES,S,R\"", "80 INPUT#2,N,A$",
                "90 PRINT\"SEQ\";N;\":\";A$", "100 CLOSE 2:CLOSE 15");
            string output = Run(), flat = Flat(output);
            byte[] first = Encoding.ASCII.GetBytes("123,ABC\r");
            check("native_seq_create_read", !output.Contains("?") && flat.Contains("WRITE0OK") && flat.Contains("SEQ123:ABC") &&
                ReadFile(D64Image.Load(path), "NOTES").SequenceEqual(first) && EntryType(File.ReadAllBytes(path), "NOTES") == 0x81,
                "OPEN/PRINT#/CLOSE creates persistent SEQ data; INPUT# reads 123 and ABC; status is 00", output + "\nD64 NOTES=" + Convert.ToHexString(ReadFile(D64Image.Load(path), "NOTES")));

            Program("10 OPEN 15,8,15", "20 OPEN 2,8,2,\"NOTES,A\"", "30 PRINT#2,\"456,DEF\"", "40 CLOSE 2",
                "50 INPUT#15,E,M$,T,S:PRINT\"APP\";E;M$", "60 OPEN 2,8,2,\"NOTES,S,R\"", "70 INPUT#2,N,A$",
                "80 INPUT#2,Q,B$", "90 PRINT N;\":\";A$;\":\";Q;\":\";B$", "100 CLOSE 2:CLOSE 15");
            output = Run(); flat = Flat(output);
            byte[] second = Encoding.ASCII.GetBytes("123,ABC\r456,DEF\r");
            check("native_seq_append_read", !output.Contains("?") && flat.Contains("APP0OK") && flat.Contains("123:ABC:456:DEF") &&
                ReadFile(D64Image.Load(path), "NOTES").SequenceEqual(second),
                "Plain ,A preserves the first record and appends the second; separate INPUT# calls read both", output + "\nD64 NOTES=" + Convert.ToHexString(ReadFile(D64Image.Load(path), "NOTES")));

            Program("10 OPEN 2,8,3,\"BITS,U,W\"", "20 PRINT#2,CHR$(0);CHR$(255);CHR$(13);CHR$(65);", "30 CLOSE 2",
                "40 OPEN 2,8,3,\"BITS,U,A\"", "50 PRINT#2,CHR$(128);CHR$(0);", "60 CLOSE 2", "70 OPEN 2,8,3,\"BITS,U,R\"",
                "80 S=0:FOR I=1 TO 6:GET#2,A$:S=S+ASC(A$+CHR$(0)):NEXT", "90 PRINT\"USR\";S;\":\";ST", "100 CLOSE 2");
            output = Run(); flat = Flat(output);
            byte[] binary = { 0, 255, 13, 65, 128, 0 };
            check("native_usr_binary_append", !output.Contains("?") && flat.Contains("USR461:64") &&
                ReadFile(D64Image.Load(path), "BITS").SequenceEqual(binary) && EntryType(File.ReadAllBytes(path), "BITS") == 0x83,
                "USR write/append preserves NUL, CR and high-bit bytes; GET# reaches EOI with exact checksum", output);

            byte[] beforeErrors = File.ReadAllBytes(path);
            Program("10 OPEN 15,8,15", "20 OPEN 2,8,2,\"NOTES,S,W\"", "30 INPUT#15,E,M$,T,S:PRINT\"DUP\";E;M$", "35 INPUT#15,E,M$,T,S:PRINT\"RESET\";E;M$", "40 CLOSE 2",
                "50 OPEN 2,8,2,\"NOTES,U,A\"", "60 INPUT#15,E,M$,T,S:PRINT\"TYPE\";E;M$", "70 CLOSE 2",
                "80 OPEN 2,8,2,\"MISSING,A\"", "90 INPUT#15,E,M$,T,S:PRINT\"MISS\";E;M$", "100 CLOSE 2:CLOSE 15");
            output = Run(); flat = Flat(output);
            check("native_file_open_status_errors", !output.Contains("?") && flat.Contains("DUP63FILEEXISTS") &&
                flat.Contains("RESET0OK") && flat.Contains("TYPE64FILETYPEMISMATCH") && flat.Contains("MISS62FILENOTFOUND") && beforeErrors.SequenceEqual(File.ReadAllBytes(path)),
                "The command channel reports 63/64/62 and resets an acknowledged error to 00; failed opens leave disk unchanged", output);

            Program("10 OPEN 15,8,15", "20 OPEN 2,8,2,\"STATUS1,S,W\":CLOSE 2", "30 INPUT#15,E,M$,T,S:PRINT\"FIRST\";E;M$",
                "40 OPEN 2,8,2,\"STATUS2,S,W\":CLOSE 2", "50 INPUT#15,E,M$,T,S:PRINT\"SECOND\";E;M$", "60 CLOSE 15");
            output = Run(); flat = Flat(output);
            check("native_status_channel_reusable", !output.Contains("?") && flat.Contains("FIRST0OK") && flat.Contains("SECOND0OK"),
                "Consecutive success responses can be read through one continuously open command channel", output);

            Program("10 OPEN 15,8,15", "20 OPEN 2,8,2,\"FIRST,S,W\"", "30 OPEN 3,8,3,\"SECOND,U,W\"",
                "40 PRINT#2,\"ONE\"", "50 PRINT#3,\"TWO\"", "60 CLOSE 15:CLOSE 2:CLOSE 3");
            output = Run();
            var both = D64Image.Load(path);
            check("native_close_15_commits_files", !output.Contains("?") && ReadFile(both, "FIRST").SequenceEqual(Encoding.ASCII.GetBytes("ONE\r")) &&
                ReadFile(both, "SECOND").SequenceEqual(Encoding.ASCII.GetBytes("TWO\r")),
                "CLOSE 15 closes and commits all ordinary file channels", output);

            Program("10 OPEN 2,8,2,\"EMPTY,S,W\":CLOSE 2", "20 OPEN 2,8,2,\"@0:NOTES,S,W\"", "30 PRINT#2,\"REPLACED\"", "40 CLOSE 2");
            output = Run();
            var replaced = D64Image.Load(path);
            check("native_empty_and_replace", !output.Contains("?") && HasFile(replaced, "EMPTY") && ReadFile(replaced, "EMPTY").Length == 0 &&
                ReadFile(replaced, "NOTES").SequenceEqual(Encoding.ASCII.GetBytes("REPLACED\r")),
                "Empty SEQ files are valid; explicit @ replacement writes the new contents", output);
        }
        finally
        {
            session.C64.EjectMedia();
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 29 + length)).ToArray();
    static bool HasFile(D64Image image, string name) => image.TryOpenSequentialFile(name, out _);
    static byte[] ReadFile(D64Image image, string name)
        => image.TryOpenSequentialFile(name, out D64Image.SequentialFileReader reader) ? reader.ReadAllBytes() : Array.Empty<byte>();
    static int SectorOffset(int track, int sector)
    {
        int before = 0;
        for (int t = 1; t < track; t++) before += t <= 17 ? 21 : t <= 24 ? 19 : t <= 30 ? 18 : 17;
        return (before + sector) * 256;
    }
    static byte EntryType(byte[] image, string name)
    {
        int track = 18, sector = 1;
        var visited = new HashSet<int>();
        while (track != 0 && visited.Add((track << 8) | sector))
        {
            int at = SectorOffset(track, sector);
            for (int i = 0; i < 8; i++)
            {
                int entry = at + i * 32;
                string candidate = Encoding.ASCII.GetString(image, entry + 5, 16).TrimEnd((char)0xA0, '?', '\0');
                if (candidate == name) return image[entry + 2];
            }
            track = image[at]; sector = image[at + 1];
        }
        return 0;
    }
}
