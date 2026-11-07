// SPDX-License-Identifier: Apache-2.0
// Generated D64 fixtures only. Native cases execute BASIC/KERNAL through the real CIA2 IEC bus.
using C64Emulator.Core;
using System.Text;
using System.Text.RegularExpressions;

static class DosRc3Tests
{
    public static void RunMedia(Func<byte[], byte[]> makeDisk, Action<string, bool, string, string> check)
    {
#if DOS_RC3_PATCH
        string directory = Path.Combine(Path.GetTempPath(), "open-rom-dos-rc3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "commands.d64");
        D64Image Fresh(string name = "commands.d64")
        {
            string target = Path.Combine(directory, name);
            File.WriteAllBytes(target, makeDisk(new byte[] { 1, 8, 0, 0 }));
            return D64Image.Load(target);
        }
        void Result(string name, bool ok, string expected, string actual) => check("dos_" + name, ok, expected, actual);
        try
        {
            var disk = Fresh();
            byte[] before = File.ReadAllBytes(path), data = ReadIndependent(before, "DATA");
            bool ok = disk.TryRenameFile("RENAMED", "DATA", out string status);
            byte[] renamed = File.ReadAllBytes(path);
            int entry = FindEntry(before, "DATA"); byte[] onlyName = (byte[])before.Clone();
            Array.Fill(onlyName, (byte)0xA0, entry + 5, 16); Encoding.ASCII.GetBytes("RENAMED").CopyTo(onlyName, entry + 5);
            Result("rename_exact", ok && renamed.SequenceEqual(onlyName) && ReadIndependent(renamed, "RENAMED").SequenceEqual(data),
                "Rename changes only the 16-byte name field and persists", status);
            before = renamed;
            ok = disk.TryRenameFile("HELLO", "RENAMED", out status);
            Result("rename_duplicate_atomic", !ok && status.StartsWith("63,") && before.SequenceEqual(File.ReadAllBytes(path)), "DOS63; complete image unchanged", status);
            ok = disk.TryRenameFile("NEW", "ABSENT", out status);
            Result("rename_missing_atomic", !ok && status.StartsWith("62,") && before.SequenceEqual(File.ReadAllBytes(path)), "DOS62; complete image unchanged", status);
            ok = disk.TryCopyFiles("JOIN", new[] { "RENAMED", "RENAMED", "RENAMED", "RENAMED" }, out status);
            Result("copy_concat_four", ok && ReadIndependent(File.ReadAllBytes(path), "JOIN").SequenceEqual(Enumerable.Range(0, 4).SelectMany(_ => data)) && Consistent(File.ReadAllBytes(path)),
                "Four SEQ inputs concatenate exactly; allocation and directory remain consistent", status);
            before = File.ReadAllBytes(path);
            ok = disk.TryCopyFiles("BAD", new[] { "RENAMED", "HELLO" }, out status);
            Result("copy_type_atomic", !ok && status.StartsWith("64,") && before.SequenceEqual(File.ReadAllBytes(path)), "Mixed SEQ/PRG rejected with64 before mutation", status);
            ok = disk.TryCopyFiles("BAD", new[] { "RENAMED", "ABSENT" }, out status);
            Result("copy_missing_atomic", !ok && status.StartsWith("62,") && before.SequenceEqual(File.ReadAllBytes(path)), "Missing later source rejected with62 before mutation", status);
            ok = disk.TryCopyFiles("JOIN", new[] { "RENAMED" }, out status);
            Result("copy_duplicate_atomic", !ok && status.StartsWith("63,") && before.SequenceEqual(File.ReadAllBytes(path)), "Existing destination rejected with63 before mutation", status);
            ok = disk.TryScratchFiles(new[] { "R?NAMED=S", "J*=S" }, out int scratched, out status);
            Result("scratch_patterns_count", ok && scratched == 2 && status == "01, FILES SCRATCHED,02,00" && FindEntry(File.ReadAllBytes(path), "RENAMED") < 0 &&
                FindEntry(File.ReadAllBytes(path), "JOIN") < 0 && Consistent(File.ReadAllBytes(path)), "Multiple wildcard/type patterns free exact chains, status count2", status);
            before = File.ReadAllBytes(path);
            ok = disk.TryScratchFiles(new[] { "NO*" }, out scratched, out status);
            Result("scratch_no_match", ok && scratched == 0 && status == "01, FILES SCRATCHED,00,00" && before.SequenceEqual(File.ReadAllBytes(path)), "No matches returns01/count0 without mutation", status);

            disk = Fresh(); before = File.ReadAllBytes(path); before[FindEntry(before, "DATA") + 2] |= 0x40; File.WriteAllBytes(path, before); disk = D64Image.Load(path);
            ok = disk.TryScratchFiles(new[] { "*" }, out scratched, out status);
            Result("scratch_locked_skipped", ok && scratched == 1 && FindEntry(File.ReadAllBytes(path), "DATA") >= 0 && Consistent(File.ReadAllBytes(path)), "Locked file survives wildcard scratch; count includes only removed files", status);
            disk = Fresh(); string id = disk.DiskId; byte[] oldSector = File.ReadAllBytes(path).Skip(SectorOffset(1, 1)).Take(256).ToArray();
            ok = disk.TryFormat("QUICK", null, out status); byte[] formatted = File.ReadAllBytes(path);
            Result("new_quick", ok && disk.DiskName == "QUICK" && disk.DiskId == id && DirectoryEntries(formatted).All(e => (formatted[e + 2] & 7) == 0) &&
                formatted.Skip(SectorOffset(1, 1)).Take(256).SequenceEqual(oldSector) && FreeBlocks(formatted) == 664 && Consistent(formatted),
                "N:name clears directory/BAM, retains ID/data, returns664 data blocks free", status);
            ok = disk.TryFormat("FULL DISK", "Q7", out status); formatted = File.ReadAllBytes(path);
            Result("new_full", ok && disk.DiskName == "FULL DISK" && disk.DiskId == "Q7" && FreeBlocks(formatted) == 664 &&
                formatted.Take(256).All(b => b == 0) && Consistent(formatted), "N:name,id initializes all35 tracks, header and BAM", status);
            before = formatted; ok = disk.TryFormat("BAD", "TOO", out status);
            Result("new_invalid_atomic", !ok && status.StartsWith("33,") && before.SequenceEqual(File.ReadAllBytes(path)), "Invalid ID does not mutate the image", status);
            string errorPath = Path.Combine(directory, "errors.d64");
            byte[] errors = makeDisk(new byte[] { 1, 8, 0, 0 }).Concat(Enumerable.Repeat((byte)5, 683)).ToArray();
            File.WriteAllBytes(errorPath, errors); var errorDisk = D64Image.Load(errorPath);
            ok = errorDisk.TryFormat("ERROR RESET", "ER", out status);
            Result("new_error_suffix", ok && File.ReadAllBytes(errorPath).Length == 175531 && File.ReadAllBytes(errorPath).Skip(174848).All(b => b == 1),
                "Full format preserves D64 error-map size and resets all standard sectors to error code1", status);

            disk = Fresh(); ok = disk.TryChangeAllocation(1, 2, true, out status);
            bool allocated = ok && !BamFree(File.ReadAllBytes(path), 1, 2);
            ok = disk.TryChangeAllocation(1, 2, true, out status);
            Result("block_allocate_next_free", allocated && !ok && status == "65, NO BLOCK,01,03", "B-A persists; reallocation reports65 with next free1/3", status);
            ok = disk.TryChangeAllocation(1, 2, false, out status);
            Result("block_free", ok && BamFree(File.ReadAllBytes(path), 1, 2), "B-F persists a free BAM bit", status);
            byte[] invalidBam = File.ReadAllBytes(path); Array.Clear(invalidBam, SectorOffset(18, 0) + 4, 35 * 4);
            int dataEntry = FindEntry(invalidBam, "DATA"); invalidBam[dataEntry + 2] &= 0x7F; File.WriteAllBytes(path, invalidBam); disk = D64Image.Load(path);
            ok = disk.TryValidate(out status); byte[] validated = File.ReadAllBytes(path);
            Result("validate_rebuild", ok && FindEntry(validated, "DATA") < 0 && FindEntry(validated, "HELLO") >= 0 && FreeBlocks(validated) == 663 && Consistent(validated),
                "V rebuilds BAM from closed chains, removes unclosed entry, frees orphan sectors", status);
            disk = Fresh(); byte[] corrupt = File.ReadAllBytes(path); int hello = FindEntry(corrupt, "HELLO"); dataEntry = FindEntry(corrupt, "DATA");
            corrupt[dataEntry + 3] = corrupt[hello + 3]; corrupt[dataEntry + 4] = corrupt[hello + 4]; File.WriteAllBytes(path, corrupt); disk = D64Image.Load(path);
            ok = disk.TryValidate(out status);
            Result("validate_crosslink_atomic", !ok && status.StartsWith("71,") && corrupt.SequenceEqual(File.ReadAllBytes(path)), "V rejects crosslinked closed files with71 and preserves all bytes", status);
            disk = Fresh(); corrupt = File.ReadAllBytes(path); corrupt[SectorOffset(1, 1)] = 1; corrupt[SectorOffset(1, 1) + 1] = 1;
            File.WriteAllBytes(path, corrupt); disk = D64Image.Load(path); ok = disk.TryScratchFiles(new[] { "DATA" }, out scratched, out status);
            Result("scratch_cycle_atomic", !ok && status.StartsWith("71,") && corrupt.SequenceEqual(File.ReadAllBytes(path)), "Cyclic scratch chain rejected with71 and no freed blocks", status);

            // Binary record lengths that would break a textual/CR-delimited OPEN parser.
            disk = Fresh();
            foreach (int length in new[] { 1, 13, 32, 34, 44, 128, 193, 254 })
            {
                string name = "REL" + length;
                ok = disk.TryOpenRelativeFile(name + ",L," + (char)length, out var relative, out status);
                bool opened = ok && relative.RecordLength == length && relative.RecordCount == 1 && relative.Bytes[0] == 255;
                byte[] expected = Enumerable.Range(0, length * 3).Select(i => (byte)((i * 37 + 11) % 251)).ToArray();
                bool written = disk.TryWriteRelativeFile(name, length, expected, out status);
                var loaded = D64Image.Load(path); bool reopened = loaded.TryOpenRelativeFile(name, out relative, out status);
                Result("rel_length_" + length, opened && written && reopened && relative.Bytes.SequenceEqual(expected) &&
                    ReadIndependent(File.ReadAllBytes(path), name).SequenceEqual(expected) && RelativeIndexValid(File.ReadAllBytes(path), name) && Consistent(File.ReadAllBytes(path)),
                    "Binary length persists; independent linked-chain and side-index reconstruction matches exact records", status);
            }
            before = File.ReadAllBytes(path);
            ok = disk.TryOpenRelativeFile("REL13,L," + (char)14, out _, out status);
            Result("rel_length_mismatch_atomic", !ok && status.StartsWith("64,") && before.SequenceEqual(File.ReadAllBytes(path)), "Reopen with wrong record length gives64; no mutation", status);
            ok = disk.TryOpenRelativeFile("@0:REL13,L," + (char)13, out var existing, out status);
            Result("rel_at_preserves", ok && existing.Bytes.SequenceEqual(ReadIndependent(before, "REL13")) && before.SequenceEqual(File.ReadAllBytes(path)), "REL @ reopen retains records", status);
            foreach (int bad in new[] { 0, 255 })
            {
                ok = disk.TryOpenRelativeFile("BAD,L," + (char)bad, out _, out status);
                Result("rel_length_reject_" + bad, !ok && status.StartsWith("51,") && before.SequenceEqual(File.ReadAllBytes(path)), "Record length outside1..254 is rejected without mutation", status);
            }
            ok = disk.TryCopyFiles("RELCOPY", new[] { "REL13" }, out status);
            Result("rel_copy", ok && ReadIndependent(File.ReadAllBytes(path), "RELCOPY").SequenceEqual(ReadIndependent(before, "REL13")) && RelativeIndexValid(File.ReadAllBytes(path), "RELCOPY"),
                "C copies REL type, record length, data and independent side-sector index", status);
            ok = disk.TryScratchFiles(new[] { "RELCOPY" }, out scratched, out status);
            Result("rel_scratch_side_free", ok && scratched == 1 && Consistent(File.ReadAllBytes(path)), "S frees REL data and all side-sector blocks", status);
            ok = disk.TryValidate(out status);
            Result("rel_validate", ok && Consistent(File.ReadAllBytes(path)) && RelativeIndexValid(File.ReadAllBytes(path), "REL254"), "V keeps all REL index and data blocks allocated", status);
            byte[] validRel = File.ReadAllBytes(path); int relEntry = FindEntry(validRel, "REL13");
            corrupt = (byte[])validRel.Clone(); int sideAt = SectorOffset(corrupt[relEntry + 21], corrupt[relEntry + 22]); corrupt[sideAt + 16] = 0;
            File.WriteAllBytes(path, corrupt); disk = D64Image.Load(path); ok = disk.TryOpenRelativeFile("REL13", out _, out status);
            Result("rel_corrupt_index", !ok && status.StartsWith("71,") && corrupt.SequenceEqual(File.ReadAllBytes(path)), "Corrupt side-sector pointer rejected with71", status);

            // Construct a REL image without using any of the production REL writers.
            disk = Fresh(); byte[] foreign = File.ReadAllBytes(path);
            int foreignEntry = SectorOffset(18,1) + 64, firstData=SectorOffset(2,5), lastData=SectorOffset(2,7), foreignSide=SectorOffset(3,4);
            foreign[foreignEntry+2]=0x84;foreign[foreignEntry+3]=2;foreign[foreignEntry+4]=5;
            Array.Fill(foreign,(byte)0xA0,foreignEntry+5,16);Encoding.ASCII.GetBytes("EXTERNAL").CopyTo(foreign,foreignEntry+5);
            foreign[foreignEntry+21]=3;foreign[foreignEntry+22]=4;foreign[foreignEntry+23]=13;foreign[foreignEntry+30]=3;
            byte[] foreignBytes=Enumerable.Range(0,260).Select(i=>(byte)(i%253)).ToArray();
            foreign[firstData]=2;foreign[firstData+1]=7;Array.Copy(foreignBytes,0,foreign,firstData+2,254);
            foreign[lastData+1]=7;Array.Copy(foreignBytes,254,foreign,lastData+2,6);
            foreign[foreignSide+1]=19;foreign[foreignSide+3]=13;foreign[foreignSide+4]=3;foreign[foreignSide+5]=4;
            foreign[foreignSide+16]=2;foreign[foreignSide+17]=5;foreign[foreignSide+18]=2;foreign[foreignSide+19]=7;
            foreach(var coordinate in new[]{(2,5),(2,7),(3,4)})
            { int b=SectorOffset(18,0)+coordinate.Item1*4;foreign[b]--;foreign[b+1+coordinate.Item2/8]&=(byte)~(1<<(coordinate.Item2&7)); }
            foreign[foreignSide+14]=99;foreign[foreignSide+254]=77; // Unused pointer bytes are unspecified.
            File.WriteAllBytes(path,foreign);disk=D64Image.Load(path);ok=disk.TryOpenRelativeFile("EXTERNAL",out var external,out status);
            Result("rel_external_layout_read",ok&&external.RecordCount==20&&external.RecordLength==13&&external.Bytes.SequenceEqual(foreignBytes),
                "Read independently constructed noncontiguous REL chain and side sector, including record spanning254-byte boundary",status);
            byte[] extended=foreignBytes.Concat(Enumerable.Repeat((byte)88,13)).ToArray();
            ok=disk.TryWriteRelativeFile("EXTERNAL",13,extended,out status);
            Result("rel_external_layout_update",ok&&ReadIndependent(File.ReadAllBytes(path),"EXTERNAL").SequenceEqual(extended)&&RelativeIndexValid(File.ReadAllBytes(path),"EXTERNAL")&&Consistent(File.ReadAllBytes(path)),
                "Existing independent REL survives update with all records, directory and BAM consistent",status);

            byte[] relWithErrors=File.ReadAllBytes(path).Concat(Enumerable.Repeat((byte)1,683)).ToArray();
            int externalEntry=FindEntry(relWithErrors,"EXTERNAL");
            int faultySide=SectorOffset(relWithErrors[externalEntry+21],relWithErrors[externalEntry+22])/256;
            relWithErrors[174848+faultySide]=5;File.WriteAllBytes(errorPath,relWithErrors);errorDisk=D64Image.Load(errorPath);
            ok=errorDisk.TryOpenRelativeFile("EXTERNAL",out _,out status);
            Result("rel_side_read_error",!ok&&status.StartsWith("23,")&&relWithErrors.SequenceEqual(File.ReadAllBytes(errorPath)),
                "D64 side-sector checksum error propagates real DOS23; no mutation",status);
            ok=errorDisk.TryCopyFiles("BAD",new[]{"EXTERNAL"},out status);
            Result("copy_read_error_atomic",!ok&&status.StartsWith("23,")&&relWithErrors.SequenceEqual(File.ReadAllBytes(errorPath)),
                "Copy propagates side-sector read failure and preserves complete host image",status);

            disk = Fresh(); disk.TryOpenRelativeFile("BIG,L," + (char)254, out _, out _);
            byte[] big = Enumerable.Range(0, 601 * 254).Select(i => (byte)((i * 19 + i / 254) % 251)).ToArray();
            ok = disk.TryWriteRelativeFile("BIG", 254, big, out status); byte[] bigImage = File.ReadAllBytes(path);
            bool hugeOpen = D64Image.Load(path).TryOpenRelativeFile("BIG", out var huge, out string hugeStatus);
            int hugeEntry = FindEntry(bigImage, "BIG");
            Result("rel_six_side_sectors", ok && hugeOpen && huge.Bytes.SequenceEqual(big) && ReadIndependent(bigImage, "BIG").SequenceEqual(big) &&
                RelativeIndexValid(bigImage, "BIG") && bigImage[hugeEntry + 30] + 256 * bigImage[hugeEntry + 31] == 607 && Consistent(bigImage),
                "601 data sectors cross all five120-block boundaries; six real side sectors;607 total blocks; reopened bytes exact", status + " / " + hugeStatus);
            before = bigImage; ok = disk.TryWriteRelativeFile("BIG", 254, new byte[700 * 254], out status);
            Result("rel_full_atomic", !ok && status.StartsWith("72,") && before.SequenceEqual(File.ReadAllBytes(path)) &&
                ReadIndependent(File.ReadAllBytes(path), "BIG").SequenceEqual(big), "REL disk-full growth leaves old index, records, BAM and host file unchanged", status);
            ok = disk.TryWriteRelativeFile("BIG", 254, new byte[721 * 254], out status);
            Result("rel_limit_atomic", !ok && status.StartsWith("52,") && before.SequenceEqual(File.ReadAllBytes(path)), "Six-side-sector capacity overflow returns52 without mutation", status);

            disk = Fresh(); disk.TryOpenRelativeFile("LOCK,L," + (char)16, out _, out _); before = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var protectedCalls = new (string Name, Func<(bool Ok, string Status)> Call)[] {
                ("new", () => { bool x = disk.TryFormat("PROTECT", "PP", out string t); return (x,t); }),
                ("scratch", () => { bool x = disk.TryScratchFiles(new[] { "*" }, out _, out string t); return (x,t); }),
                ("rename", () => { bool x = disk.TryRenameFile("X", "DATA", out string t); return (x,t); }),
                ("copy", () => { bool x = disk.TryCopyFiles("X", new[] { "DATA" }, out string t); return (x,t); }),
                ("validate", () => { bool x = disk.TryValidate(out string t); return (x,t); }),
                ("relative", () => { bool x = disk.TryWriteRelativeFile("LOCK", 16, new byte[16], out string t); return (x,t); }),
                ("block", () => { bool x = disk.TryWriteSectorPersistent(1, 2, new byte[256], out string t); return (x,t); }),
                ("allocate", () => { bool x = disk.TryChangeAllocation(1, 2, true, out string t); return (x,t); })
            };
            foreach (var action in protectedCalls)
            {
                var result = action.Call();
                Result("protected_" + action.Name, !result.Ok && result.Status.StartsWith("26,") && before.SequenceEqual(File.ReadAllBytes(path)),
                    "Live host write protection returns26 and preserves the complete image", result.Status);
            }
            File.SetAttributes(path, FileAttributes.Normal);
            string failPath = Path.Combine(directory, "failure.d64"); var failDisk = Fresh("failure.d64");
            string oldName = failDisk.DiskName; File.Delete(failPath); Directory.CreateDirectory(failPath);
            ok = failDisk.TryFormat("FAILED", "FF", out status);
            Result("commit_failure_memory_atomic", !ok && (status.StartsWith("25,") || status.StartsWith("26,")) && failDisk.DiskName == oldName && failDisk.TryResolveLoad("HELLO", out _),
                "Host replacement failure leaves mounted directory/header/content unchanged", status);
            Directory.Delete(failPath);

            RunDriveMedia(Fresh("drive.d64"), Path.Combine(directory, "drive.d64"), Result);
            RunTurnaroundMedia(Result);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
#else
        check("dos_rc3_patch", false, "RC3 DOS host patch is compiled", "Missing D64/IEC DOS patch.");
#endif
    }

#if DOS_RC3_PATCH
    private static void RunTurnaroundMedia(Action<string, bool, string, string> check)
    {
        // Independent IEC pin stimulus: delay the new listener longer than the
        // normal turnaround hold. A talker must not send clock/data bits early.
        var bus = new IecBus();
        var c64 = bus.CreatePort("C64");
        var device = bus.CreatePort("Drive8");
        Type senderType = typeof(IecDrive1541).GetNestedType("IecByteSender", System.Reflection.BindingFlags.NonPublic);
        object sender = Activator.CreateInstance(senderType, new object[] { device });
        var begin = senderType.GetMethod("Begin");
        var tick = senderType.GetMethod("Tick");
        c64.SetLineLow(IecBusLine.Clock, true);
        begin.Invoke(sender, new object[] { new byte[] { 0xA5 }, false });
        for (int i = 0; i < 8; i++) tick.Invoke(sender, null);
        c64.SetLineLow(IecBusLine.Clock, false);
        tick.Invoke(sender, null);
        int risingEdges = 0;
        bus.RegisterLineChangeListener((line, low) => { if (line == IecBusLine.Clock && !low) risingEdges++; });
        for (int i = 0; i < 2000; i++) tick.Invoke(sender, null);
        int earlyEdges = risingEdges;
        bool held = bus.IsLineLow(IecBusLine.Clock) && earlyEdges == 0;
        c64.SetLineLow(IecBusLine.Data, true);
        for (int i = 0; i < 600; i++) tick.Invoke(sender, null);
        bool ready = bus.IsLineHigh(IecBusLine.Clock) && bus.IsLineLow(IecBusLine.Data);
        c64.SetLineLow(IecBusLine.Data, false);
        int beforeData = risingEdges;
        for (int i = 0; i < 2000; i++) tick.Invoke(sender, null);
        bool started = risingEdges > beforeData;
        check("iec_talker_waits_listener", held && ready && started,
            "A listener delayed2000 cycles sees no premature data clocks; transfer begins only after DATA hold/release",
            $"held={held}; earlyClockRises={earlyEdges}; clockHighOnReady={ready}; startedAfterRelease={started}");
    }

    private static IecDrive1541 MakeDrive(D64Image disk)
    {
        var bus = new IecBus(); var drive = new IecDrive1541(8, bus.CreatePort("software"), bus.CreatePort("hardware"));
        drive.MountDisk(disk); return drive;
    }
    private static void RunDriveMedia(D64Image disk, string path, Action<string, bool, string, string> check)
    {
        var drive = MakeDrive(disk);
        IecDrive1541.CommandResult Command(string command) => drive.ExecuteCommand(Encoding.ASCII.GetBytes(command));
        IecDrive1541.CommandResult Position(int record, int position = 1) => drive.ExecuteCommand(new byte[] { 80, 98, (byte)record, (byte)(record >> 8), (byte)position });
        void Bytes(params byte[] data) { foreach (byte value in data) drive.WriteKernalChannelByte(2, value); }
        byte[] Read(int count)
        {
            var result = new List<byte>();
            for (int i = 0; i < count; i++) { if (!drive.TryReadKernalChannelByte(2, out byte b, out _)) break; result.Add(b); }
            return result.ToArray();
        }
        var result = drive.OpenKernalChannel(2, "RECORDS,L," + (char)16);
        check("rel_channel_open", result.Status == 0, "Binary REL OPEN allocates data and side sector", result.StatusText);
        result = Position(256, 13);
        check("rel_position_missing", result.StatusText.StartsWith("50,"), "P to future record256/position13 reports50 but retains requested position", result.StatusText);
        Bytes(65, 66, 13); result = Position(256, 13);
        byte[] image = File.ReadAllBytes(path), rel = ReadIndependent(image, "RECORDS");
        check("rel_sparse_position", result.Status == 0 && rel.Length == 4096 && rel[255 * 16 + 12] == 65 && rel[255 * 16 + 13] == 66 && rel[255 * 16 + 14] == 13 &&
            Enumerable.Range(0, 255).All(i => rel[i * 16] == 255 && rel.Skip(i * 16 + 1).Take(15).All(b => b == 0)) && RelativeIndexValid(image, "RECORDS"),
            "P record256/offset13 creates255 empty records markedFF/zeros; exact data crosses sectors", result.StatusText);
        check("rel_channel_read", Read(4).SequenceEqual(new byte[] { 65, 66, 13, 0 }), "Read current record from requested byte offset, including CR and zero padding", drive.StatusText);
        Position(13); Bytes(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17); result = Position(13);
        rel = ReadIndependent(File.ReadAllBytes(path), "RECORDS");
        check("rel_overflow_truncated", result.StatusText.StartsWith("51,") && rel.Skip(12 * 16).Take(16).SequenceEqual(Enumerable.Range(1,16).Select(i=>(byte)i)),
            "Overlong record writes first16 bytes and reports51; no spill to next record", result.StatusText);
        Position(13, 4); Bytes(99); Position(13); rel = ReadIndependent(File.ReadAllBytes(path), "RECORDS");
        check("rel_partial_write_zero_fill", rel.Skip(12 * 16).Take(4).SequenceEqual(new byte[] {1,2,3,99}) && rel.Skip(12 * 16 + 4).Take(12).All(b=>b==0),
            "Write from byte4 preserves prefix and clears remaining record tail", Convert.ToHexString(rel.Skip(12*16).Take(16).ToArray()));
        result = Position(0); check("rel_zero_record", result.StatusText.StartsWith("50,"), "Record0 is unavailable", result.StatusText);
        result = Position(1, 17); check("rel_position_overflow", result.StatusText.StartsWith("51,"), "Position beyond record length gives51", result.StatusText);
        drive.CloseKernalChannel(2);
        result = drive.OpenKernalChannel(2, "RECORDS"); Position(13); byte[] reopened = Read(16);
        check("rel_plain_reopen", result.Status == 0 && reopened.SequenceEqual(rel.Skip(12*16).Take(16)), "Plain-name OPEN recognizes existing REL and persists exact records", result.StatusText);
        result = Command("S:RECORDS"); check("scratch_open_rejected", result.StatusText.StartsWith("60,"), "Scratch of an open REL returns60", result.StatusText);
        result = Command("R:OTHER=RECORDS"); check("rename_open_rejected", result.StatusText.StartsWith("60,"), "Rename of an open REL returns60", result.StatusText);
        result = Command("V"); check("validate_open_rejected", result.StatusText.StartsWith("60,"), "Validate with an active REL writer returns60", result.StatusText);
        Position(14); Bytes(8,9);
        using var state = new MemoryStream(); using (var writer = new BinaryWriter(state, Encoding.UTF8, true)) drive.SaveState(writer);
        drive.CloseKernalChannel(2); state.Position = 0;
        var restored = MakeDrive(D64Image.Load(path)); using (var reader = new BinaryReader(state, Encoding.UTF8, true)) restored.LoadState(reader);
        restored.CloseKernalChannel(2);
        check("rel_savestate_pending", ReadIndependent(File.ReadAllBytes(path), "RECORDS").Skip(13*16).Take(3).SequenceEqual(new byte[] {8,9,0}),
            "Savestate retains REL identity, position and pending write without changing old channel framing", restored.StatusText);
        drive.CloseKernalChannel(15); drive = MakeDrive(D64Image.Load(path));

        result = drive.OpenKernalChannel(2, "#0");
        drive.TryReadKernalChannelByte(2, out byte number, out bool numberEoi);
        check("buffer_open_number", result.Status == 0 && number == 48 && numberEoi, "#0 announces buffer number0", result.StatusText);
        result = drive.OpenKernalChannel(3, "#0"); check("buffer_duplicate", result.StatusText.StartsWith("70,"), "Two channels cannot allocate the same direct buffer", result.StatusText);
        Command("B-P:2,1"); Bytes(65, 13, 0, 255); result = Command("B-W:2,0,1,2");
        image = File.ReadAllBytes(path); int block = SectorOffset(1,2);
        check("block_counted_write", result.Status == 0 && image.Skip(block).Take(5).SequenceEqual(new byte[]{4,65,13,0,255}),
            "B-W uses buffer pointer to store count4 at byte0 and persists payload", result.StatusText);
        result = Command("BLOCK-READ:2 0 1 2"); var counted = new List<byte>(); bool eoi = false;
        for(int i=0;i<4;i++) { drive.TryReadKernalChannelByte(2,out byte b,out eoi); counted.Add(b); }
        check("block_counted_read", result.Status == 0 && counted.SequenceEqual(new byte[]{65,13,0,255}) && eoi, "B-R skips count byte and EOI follows exactly four data bytes; spaces accepted", result.StatusText);
        result = Command("UA:2,0,1,2"); byte[] raw = Read(256);
        check("block_raw_u1_alias", result.Status == 0 && raw.Length == 256 && raw[0] == 4 && raw[4] == 255, "UA/U1 reads all256 bytes including count byte", result.StatusText);
        Command("BUFFER-POINTER:2,0"); Bytes(77); result = Command("UB:2 0 1 2");
        check("block_raw_u2_alias", result.Status == 0 && File.ReadAllBytes(path)[block] == 77 && File.ReadAllBytes(path)[block+4] == 255,
            "UB/U2 writes all256 buffer bytes without updating byte0; host file persists", result.StatusText);
        result = Command("U1:14,0,1,2"); check("block_requires_channel", result.StatusText.StartsWith("70,"), "Raw block command requires an allocated # channel", result.StatusText);
        result = Command("B-R:2,1,1,2"); check("block_wrong_drive", result.StatusText.StartsWith("74,"), "Drive1 rejected on single-drive1541", result.StatusText);
        result = Command("B-P:2,256"); check("block_pointer_range", result.StatusText.StartsWith("30,"), "Buffer pointer outside0..255 rejected", result.StatusText);
        Command("B-P:2,13");
        result=drive.ExecuteCommand(new byte[]{(byte)'M',(byte)'-',(byte)'W',13,3,4,13,0,255,77});
        byte[] aliased = Read(4);
        check("buffer_memory_write_alias", result.Status == 0 && aliased.SequenceEqual(new byte[]{13,0,255,77}),
            "M-W changes an already positioned # channel immediately, without another B-P; pointer and raw limit retained", Convert.ToHexString(aliased));
        drive.CloseKernalChannel(2);
        var memory = new byte[]{(byte)'M',(byte)'-',(byte)'W',13,3,4,13,0,255,77}; result=drive.ExecuteCommand(memory);
        var read = drive.ExecuteCommand(new byte[]{(byte)'M',(byte)'-',(byte)'R',13,3,4});
        check("memory_binary_cr", result.Status == 0 && read.ReadBuffer.SequenceEqual(new byte[]{13,0,255,77}), "M-W/M-R preserve embedded CR/NUL in address and data", read.StatusText);
        read=drive.ExecuteCommand(new byte[]{(byte)'M',(byte)'-',(byte)'R',13,3});
        check("memory_read_default_count", read.Status == 0 && read.ReadBuffer.SequenceEqual(new byte[]{13}), "Five-byte M-R defaults to one byte", read.StatusText);
        drive.OpenKernalChannel(15, "");
        foreach(byte b in new byte[]{(byte)'M',(byte)'-',(byte)'R',13,3,13}) drive.WriteKernalChannelByte(15,b);
        drive.TryReadKernalChannelByte(15,out byte implicitByte,out _);
        check("memory_byte_api_binary", implicitByte==13, "Byte API defers ambiguous trailing CR until read/message boundary; five-byte M-R preserves address", implicitByte.ToString());
        drive.CloseKernalChannel(15);
        result=Command("UQ"); check("user_unknown_rejected", result.StatusText.StartsWith("31,"), "Unknown user command reports syntax error rather than success", result.StatusText);
        result=Command("UI-"); check("vic20_speed_explicitly_rejected", result.StatusText.StartsWith("31,"), "VIC20 timing profile is outside this C64 host; unsupported UI- reports31", result.StatusText);
        result=Command("INITIALIZE"); check("initialize", result.Status == 0 && result.StatusText.StartsWith("00,"), "I closes channels and refreshes directory; returns00", result.StatusText);
        result=Command("D:0=0"); check("duplicate_single_drive_rejected", result.Status != 0, "DUPLICATE has no function on a single1541; rejected explicitly", result.StatusText);
    }
#endif

    public static void RunNative(Session session, Func<byte[], byte[]> makeDisk, Action<string, bool, string, string> check)
    {
#if DOS_RC3_PATCH
        string directory = Path.Combine(Path.GetTempPath(), "open-rom-dos-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory,"native.d64");
        void Fresh()
        {
            File.WriteAllBytes(path,makeDisk(new byte[]{1,8,0,0})); session.C64.MountMedia(path,8);
        }
        void Program(params string[] lines)
        {
            session.Command("NEW",500_000); foreach(string line in lines)session.Command(line,500_000);
        }
        string Run(int cycles=8_000_000)=>session.Command("RUN",cycles);
        string Flat(string value)=>Regex.Replace(value,@"\s+","");
        try
        {
            Fresh();
            Program("10 OPEN15,8,15", "20 OPEN2,8,2,\"BINARY,L,\"+CHR$(16)",
                "30 PRINT#15,\"P\";CHR$(98);CHR$(13);CHR$(0);CHR$(13)",
                "40 PRINT#2,\"AB\";", "50 PRINT#15,\"P\";CHR$(98);CHR$(0);CHR$(1)",
                "60 PRINT#2,\"256\"", "70 PRINT#15,\"P\";CHR$(98);CHR$(0);CHR$(13)",
                "80 PRINT#2,\"HIGH\"", "90 CLOSE2:INPUT#15,E,M$,T,S:PRINT\"WRITE\";E",
                "100 OPEN2,8,2,\"BINARY\"", "110 PRINT#15,\"P\";CHR$(98);CHR$(0);CHR$(1)",
                "120 INPUT#2,A$:PRINT\"RECORD\";A$", "130 CLOSE2:CLOSE15");
            string output=Run(12_000_000),flat=Flat(output); byte[] bytes=ReadIndependent(File.ReadAllBytes(path),"BINARY");
            check("native_rel_binary_position",!output.Contains("?")&&flat.Contains("WRITE0")&&flat.Contains("RECORD256")&&bytes.Length==3328*16&&
                bytes.Skip(12*16+12).Take(4).SequenceEqual(new byte[]{65,66,0,0})&&bytes.Skip(255*16).Take(4).SequenceEqual(Encoding.ASCII.GetBytes("256\r"))&&
                bytes.Skip(3327*16).Take(5).SequenceEqual(Encoding.ASCII.GetBytes("HIGH\r"))&&RelativeIndexValid(File.ReadAllBytes(path),"BINARY"),
                "Real IEC OPEN/P/PRINT#/INPUT#: binary record low13/high13/NUL, position13, optional position, sparse3328 records and second side sector persist exactly",output+"\nREL bytes="+bytes.Length+"\n"+session.C64.GetDriveDebugInfo(8));

            Program("10 OPEN15,8,15", "20 OPEN2,8,2,\"BINARY\"", "30 PRINT#15,\"P\";CHR$(98);CHR$(1);CHR$(0)",
                "40 INPUT#15,E,M$,T,S:PRINT\"EMPTY\";E", "50 PRINT#15,\"P\";CHR$(98);CHR$(13);CHR$(0);CHR$(13)",
                "60 S=0:FORI=1TO4:GET#2,A$:S=S+ASC(A$+CHR$(0)):NEXT", "70 PRINT\"OFFSET\";S",
                "80 PRINT#15,\"P\";CHR$(98);CHR$(13);CHR$(0);CHR$(16)", "90 PRINT#2,\"TOOLONG\"",
                "100 INPUT#15,E,M$,T,S:PRINT\"OVERFLOW\";E", "110 CLOSE2:CLOSE15");
            output=Run(10_000_000);flat=Flat(output);bytes=ReadIndependent(File.ReadAllBytes(path),"BINARY");
            check("native_rel_errors_and_offset",!output.Contains("?")&&flat.Contains("EMPTY50")&&flat.Contains("OFFSET131")&&flat.Contains("OVERFLOW51")&&bytes[12*16+15]==84,
                "Native P missing record50, byte-position read, NUL bytes, and truncated overrun51",output+"\n"+session.C64.GetDriveDebugInfo(8));

            Fresh();
            Program("10 OPEN15,8,15", "20 PRINT#15,\"COPY:JOIN=DATA,DATA\"", "30 INPUT#15,E,M$,T,S:PRINT\"COPY\";E",
                "40 PRINT#15,\"RENAME:RENAMED=JOIN\"", "50 INPUT#15,E,M$,T,S:PRINT\"RENAME\";E",
                "60 OPEN2,8,2,\"RENAMED,S,R\"", "70 INPUT#2,N,A$:INPUT#2,Q,B$", "80 PRINT N;A$;Q;B$:CLOSE2",
                "90 PRINT#15,\"SCRATCH:R*=S\"", "100 INPUT#15,E,M$,T,S:PRINT\"SCRATCH\";E;T",
                "110 PRINT#15,\"V\"", "120 INPUT#15,E,M$,T,S:PRINT\"VALIDATE\";E:CLOSE15");
            output=Run(10_000_000);flat=Flat(output);byte[] image=File.ReadAllBytes(path);
            check("native_dos_copy_rename_scratch_validate",!output.Contains("?")&&flat.Contains("COPY0")&&flat.Contains("RENAME0")&&flat.Contains("123ABC123ABC")&&
                flat.Contains("SCRATCH11")&&flat.Contains("VALIDATE0")&&FindEntry(image,"RENAMED")<0&&FindEntry(image,"DATA")>=0&&Consistent(image),
                "BASIC sends long C/R/S and V over command channel; concatenated data reads twice; scratch count1; consistent remounted disk",output+"\n"+session.C64.GetDriveDebugInfo(8));
            string oldId=D64Image.Load(path).DiskId;
            Program("10 OPEN15,8,15,\"N:QUICK\"", "20 INPUT#15,E,M$,T,S:PRINT\"QUICK\";E:CLOSE15");
            output=Run();flat=Flat(output);image=File.ReadAllBytes(path);
            check("native_dos_quick_format_open15",!output.Contains("?")&&flat.Contains("QUICK0")&&D64Image.Load(path).DiskId==oldId&&FreeBlocks(image)==664&&FindEntry(image,"DATA")<0,
                "Command in OPEN15 executes quick format and retains disk ID",output);
            Program("10 OPEN15,8,15", "20 PRINT#15,\"NEW:FORMATTED,RC\"", "30 INPUT#15,E,M$,T,S:PRINT\"FORMAT\";E:CLOSE15");
            output=Run();flat=Flat(output);var formatted=D64Image.Load(path);
            check("native_dos_full_format",!output.Contains("?")&&flat.Contains("FORMAT0")&&formatted.DiskName=="FORMATTED"&&formatted.DiskId=="RC"&&Consistent(File.ReadAllBytes(path)),
                "Real IEC full format updates label/ID and persists valid blank35-track image",output);

            Fresh();
            Program("10 OPEN15,8,15", "20 OPEN2,8,2,\"#0\"", "30 PRINT#15,\"B-P:2,1\"", "40 PRINT#2,\"AB\";CHR$(13);CHR$(0);",
                "50 PRINT#15,\"B-W:2 0 1 2\"", "60 INPUT#15,E,M$,T,S:PRINT\"BLOCKWRITE\";E",
                "70 PRINT#15,\"B-R:2 0 1 2\"", "80 S=0:FORI=1TO4:GET#2,A$:S=S+ASC(A$+CHR$(0)):NEXT",
                "90 PRINT\"COUNTED\";S;ST", "100 PRINT#15,\"U1:2 0 1 2\"", "110 GET#2,A$:PRINT\"RAW\";ASC(A$)",
                "120 PRINT#15,\"B-P:2,0\"", "130 PRINT#2,CHR$(77);", "140 PRINT#15,\"U2:2 0 1 2\"",
                "150 INPUT#15,E,M$,T,S:PRINT\"RAWWRITE\";E", "160 CLOSE2:CLOSE15");
            output=Run(12_000_000);flat=Flat(output);image=File.ReadAllBytes(path);
            check("native_dos_direct_blocks",!output.Contains("?")&&flat.Contains("BLOCKWRITE0")&&flat.Contains("COUNTED14464")&&flat.Contains("RAW4")&&flat.Contains("RAWWRITE0")&&
                image.Skip(SectorOffset(1,2)).Take(5).SequenceEqual(new byte[]{77,65,66,13,0}),
                "Native # buffer, B-P, counted B-W/B-R, raw U1/U2 and EOI; exact changed256-byte sector persists",output+"\n"+session.C64.GetDriveDebugInfo(8));

            Program("10 OPEN15,8,15", "12 OPEN2,8,2,\"#0\"", "14 PRINT#15,\"B-P:2,13\"",
                "20 PRINT#15,\"M-W\";CHR$(13);CHR$(3);CHR$(4);CHR$(13);CHR$(0);CHR$(255);CHR$(77)",
                "25 PRINT\"BUFFER\";", "30 FORI=1TO4:GET#2,A$:PRINTASC(A$+CHR$(0));:NEXT", "35 PRINT\"BEND\":CLOSE2",
                "40 PRINT#15,\"M-R\";CHR$(13);CHR$(3);CHR$(4)",
                "50 S=0:FORI=1TO4:GET#15,A$:S=S+ASC(A$+CHR$(0)):NEXT", "60 PRINT\"MEMORY\";S:CLOSE15");
            output=Run(10_000_000);flat=Flat(output);
            check("native_dos_memory_binary",!output.Contains("?")&&flat.Contains("MEMORY345"),
                "Native M-W/M-R serial payload preserves embedded CR/NUL/255 and binary address13",output+"\n"+session.C64.GetDriveDebugInfo(8));
            check("native_dos_buffer_memory_alias",!output.Contains("?")&&flat.Contains("BUFFER13025577BEND"),
                "Real GET# sees exact M-W bytes13/0/255/77 in an already positioned direct buffer, without another B-P",output);
            byte[] executeDisk=makeDisk(new byte[]{1,8,0,0});
            // Independently assembled LDA #90 / STA $041F / RTS in a raw block.
            new byte[]{0xA9,90,0x8D,31,4,0x60}.CopyTo(executeDisk,SectorOffset(1,2));
            File.WriteAllBytes(path,executeDisk);session.C64.MountMedia(path,8);
            Program("10 OPEN15,8,15", "20 OPEN2,8,2,\"#0\"", "30 PRINT#15,\"B-E:2 0 1 2\"",
                "40 PRINT#15,\"M-R\";CHR$(31);CHR$(4)", "50 GET#15,A$:PRINT\"EXEC\";ASC(A$)",
                "60 P$=CHR$(238)+CHR$(32)+CHR$(4)+CHR$(96)",
                "70 PRINT#15,\"M-W\";CHR$(0);CHR$(5);CHR$(4);P$", "80 PRINT#15,\"U3\"",
                "90 PRINT#15,\"M-R\";CHR$(32);CHR$(4)", "100 GET#15,A$:PRINT\"USER\";ASC(A$)",
                "110 PRINT#15,\"M-E\";CHR$(0);CHR$(5)", "120 PRINT#15,\"M-R\";CHR$(32);CHR$(4)",
                "130 GET#15,A$:PRINT\"MEMEXEC\";ASC(A$)", "140 CLOSE2:CLOSE15");
            output=Run(12_000_000);flat=Flat(output);
            check("native_dos_execute_drive_cpu",!output.Contains("?")&&flat.Contains("EXEC90")&&flat.Contains("USER1")&&flat.Contains("MEMEXEC2"),
                "B-E executes raw block at allocated RAM buffer; U3 and M-E execute uploaded INC/RTS on actual1541 CPU; M-R observes90/1/2",output+"\n"+session.C64.GetDriveDebugInfo(8));

            Program("10 OPEN15,8,15", "20 PRINT#15,\"B-A:0 1 3\"", "30 INPUT#15,E,M$,T,S:PRINT\"ALLOC\";E",
                "40 PRINT#15,\"B-A:0 1 3\"", "50 INPUT#15,E,M$,T,S:PRINT\"NEXTBLOCK\";E;T;S",
                "60 PRINT#15,\"B-F:0 1 3\"", "70 INPUT#15,E,M$,T,S:PRINT\"FREE\";E",
                "80 PRINT#15,\"UI+\"", "90 INPUT#15,E,M$,T,S:PRINT\"SPEED\";E:CLOSE15");
            output=Run(8_000_000);flat=Flat(output);
            check("native_dos_block_allocation",!output.Contains("?")&&flat.Contains("ALLOC0")&&flat.Contains("NEXTBLOCK6514")&&flat.Contains("FREE0")&&flat.Contains("SPEED0")&&BamFree(File.ReadAllBytes(path),1,3),
                "Native B-A/B-F persist allocation; duplicate65 returns nextfree1/4; UI+ keeps C64 transport and command channel open",output);

        }
        finally { Directory.Delete(directory,true); }

#endif
    }

    private static int Sectors(int track) => track <= 17 ? 21 : track <= 24 ? 19 : track <= 30 ? 18 : 17;
    private static int SectorOffset(int track, int sector)
    {
        if (track < 1 || track > 35 || sector < 0 || sector >= Sectors(track)) throw new InvalidDataException("Invalid track/sector.");
        return (Enumerable.Range(1, track - 1).Sum(Sectors) + sector) * 256;
    }
    private static List<int> DirectoryEntries(byte[] image)
    {
        var entries = new List<int>(); var seen = new HashSet<int>(); int track = 18, sector = 1;
        while (track != 0)
        {
            int offset = SectorOffset(track, sector);
            if (track != 18 || !seen.Add(offset)) throw new InvalidDataException("Cyclic directory.");
            for (int slot = 0; slot < 8; slot++) entries.Add(offset + slot * 32);
            track = image[offset]; sector = image[offset + 1];
        }
        return entries;
    }
    private static int FindEntry(byte[] image, string name) => DirectoryEntries(image).FirstOrDefault(e => (image[e+2]&7)!=0 &&
        Encoding.Latin1.GetString(image,e+5,16).TrimEnd((char)0xA0,'\0',' ')==name,-1);
    private static List<(int Track,int Sector)> Chain(byte[] image, int track, int sector)
    {
        var result = new List<(int,int)>(); var seen = new HashSet<int>();
        while(track!=0) { int offset=SectorOffset(track,sector); if(!seen.Add(offset))throw new InvalidDataException("Cyclic chain.");
            result.Add((track,sector)); track=image[offset];sector=image[offset+1]; }
        return result;
    }
    private static byte[] ReadIndependent(byte[] image,string name)
    {
        int entry=FindEntry(image,name); if(entry<0)return Array.Empty<byte>(); var result=new List<byte>();
        foreach(var s in Chain(image,image[entry+3],image[entry+4])) { int offset=SectorOffset(s.Track,s.Sector); int count=image[offset]==0?image[offset+1]-1:254;
            if(count<0||count>254)throw new InvalidDataException("Invalid chain length.");result.AddRange(image.Skip(offset+2).Take(count)); }
        return result.ToArray();
    }
    private static bool BamFree(byte[] image,int track,int sector) => (image[SectorOffset(18,0)+track*4+1+sector/8]&(1<<(sector&7)))!=0;
    private static int FreeBlocks(byte[] image) => Enumerable.Range(1,35).Where(t=>t!=18).Sum(t=>Enumerable.Range(0,Sectors(t)).Count(s=>BamFree(image,t,s)));
    private static bool RelativeIndexValid(byte[] image,string name)
    {
        int entry=FindEntry(image,name); if(entry<0||(image[entry+2]&7)!=4)return false;
        var data=Chain(image,image[entry+3],image[entry+4]);var sides=Chain(image,image[entry+21],image[entry+22]);
        if(sides.Count!=(data.Count+119)/120||sides.Count>6)return false;
        for(int n=0;n<sides.Count;n++) { int at=SectorOffset(sides[n].Track,sides[n].Sector);
            if(image[at+2]!=n||image[at+3]!=image[entry+23])return false;
            if(n==sides.Count-1&&image[at+1]!=15+2*Math.Min(120,data.Count-n*120))return false;
            for(int i=0;i<6;i++){var pair=i<sides.Count?sides[i]:(0,0);if(image[at+4+2*i]!=pair.Item1||image[at+5+2*i]!=pair.Item2)return false;}
            for(int i=0;i<120;i++){int index=n*120+i;var pair=index<data.Count?data[index]:(0,0);if(image[at+16+2*i]!=pair.Item1||image[at+17+2*i]!=pair.Item2)return false;}
        }
        return image[entry+30]+256*image[entry+31]==data.Count+sides.Count;
    }
    private static bool Consistent(byte[] image)
    {
        var owned = new HashSet<int>{SectorOffset(18,0)};
        foreach(var s in Chain(image,18,1))if(!owned.Add(SectorOffset(s.Track,s.Sector)))return false;
        foreach(int entry in DirectoryEntries(image))
        {
            int type=image[entry+2]&7;if(type==0)continue;
            var sectors=Chain(image,image[entry+3],image[entry+4]);if(type==4)sectors.AddRange(Chain(image,image[entry+21],image[entry+22]));
            if(sectors.Count!=image[entry+30]+256*image[entry+31])return false;
            foreach(var s in sectors)if(!owned.Add(SectorOffset(s.Track,s.Sector)))return false;
        }
        for(int track=1;track<=35;track++) {
            int free=0;for(int sector=0;sector<Sectors(track);sector++) { bool available=BamFree(image,track,sector);if(available)free++;
                if(available==owned.Contains(SectorOffset(track,sector)))return false; }
            if(image[SectorOffset(18,0)+track*4]!=free)return false;
        }
        return true;
    }
}
