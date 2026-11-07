// SPDX-License-Identifier: Apache-2.0
// Exercise the real frontend keyboard matrix and the optional RESTORE NMI line.
using C64Emulator.Core;
using OpenTK.Input;
using System.Globalization;
using System.Text.RegularExpressions;

static class RuntimeControlTests
{
#if RESTORE_KEY_PATCH
    public const bool RestoreKeyPatch = true;
#else
    public const bool RestoreKeyPatch = false;
#endif

    public static void Run(Session session, Action<string, bool, string, string> check)
    {
        bool previousInjection = session.C64.EnableInputInjection;
        session.C64.SetHostKeyboardLayout(HostKeyboardLayout.En);
        session.Command("NEW");
        session.Command("10 A=0");
        session.Command("20 A=A+1:GOTO 20");
        byte[] program = session.BasicProgramBytes();
        int Begin() => session.Output.Length;
        string Since(int start) => session.Output.ToString(start, session.Output.Length - start);
        void Stop()
        {
            session.C64.KeyboardKeyDown(Key.Tab); // RUN/STOP is CIA1 row 7, column 7.
            session.Run(60_000);
            session.C64.KeyboardKeyUp(Key.Tab);
            session.Run(300_000);
        }
        (double Value, string Output) Counter()
        {
            string output = session.Command("PRINT\"COUNT\";A", 500_000);
            // Take the emitted numeric result, excluding the echoed source line.
            Match value = Regex.Match(output, @"COUNT\s+([0-9]+(?:\.[0-9]+)?)");
            return (value.Success ? double.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN, output);
        }
        try
        {
            session.C64.EnableInputInjection = false;
            int start = Begin();
            session.Command("RUN", 500_000);
            Stop();
            string stopped = Since(start);
            var first = Counter();
            check("physical_run_stop", stopped.Contains("BREAK") && stopped.Contains("READY.") && first.Value > 0 &&
                program.SequenceEqual(session.BasicProgramBytes()),
                "Actual Tab matrix key stops an infinite program with BREAK and preserves its counter/program",
                stopped + first.Output);

            start = Begin();
            session.Command("CONT", 500_000);
            Stop();
            string continued = Since(start);
            var second = Counter();
            check("physical_stop_cont", continued.Contains("BREAK") && !continued.Contains("?") && second.Value > first.Value,
                "CONT resumes the interrupted loop and increases the existing A counter before another RUN/STOP",
                continued + second.Output);

#if RESTORE_KEY_PATCH
            session.NmiHandler = (ushort)(session.C64.PeekRam(0x0318) | (session.C64.PeekRam(0x0319) << 8));
            int nmis = session.NmiEntries;
            start = Begin();
            session.C64.KeyboardKeyDown(Key.PageUp);
            session.Run(360_000); // Holding RESTORE must create one edge, not repeated NMIs.
            session.C64.KeyboardKeyUp(Key.PageUp);
            session.Run(30_000);
            string alone = Since(start);
            var unchanged = Counter();
            check("physical_restore_alone", session.NmiEntries == nmis + 1 && !alone.Contains("READY.") &&
                program.SequenceEqual(session.BasicProgramBytes()) && unchanged.Value == second.Value,
                "PageUp alone triggers exactly one NMI while held and returns without clearing program or variables",
                alone + unchanged.Output + $"\nNMI entries: {nmis} -> {session.NmiEntries}");

            void RestoreChord()
            {
                session.C64.KeyboardKeyDown(Key.Tab);
                session.C64.KeyboardKeyDown(Key.PageUp);
                session.Run(60_000);
                session.C64.KeyboardKeyUp(Key.PageUp);
                session.C64.KeyboardKeyUp(Key.Tab);
                session.Run(400_000);
            }
            session.Command("CONT", 300_000);
            nmis = session.NmiEntries;
            start = Begin();
            RestoreChord();
            string restored = Since(start);
            var afterRestore = Counter();
            check("physical_stop_restore", session.NmiEntries == nmis + 1 && restored.Contains("READY.") &&
                program.SequenceEqual(session.BasicProgramBytes()) && afterRestore.Value > second.Value,
                "Tab+PageUp performs warm recovery from a running loop while preserving program and variables",
                restored + afterRestore.Output + $"\nNMI entries: {nmis} -> {session.NmiEntries}");

            nmis = session.NmiEntries;
            start = Begin();
            RestoreChord();
            string repeated = Since(start);
            var retained = Counter();
            check("physical_restore_repress", session.NmiEntries == nmis + 1 && repeated.Contains("READY.") &&
                program.SequenceEqual(session.BasicProgramBytes()) && retained.Value == afterRestore.Value,
                "Releasing and pressing RESTORE again produces a second independent warm recovery",
                repeated + retained.Output + $"\nNMI entries: {nmis} -> {session.NmiEntries}");
#endif
        }
        finally
        {
            session.C64.KeyboardKeyUp(Key.Tab);
            session.C64.KeyboardKeyUp(Key.PageUp);
            session.C64.EnableInputInjection = previousInjection;
        }
    }

    public static void RunEditor(Session session, Action<string, bool, string, string> check)
    {
        string Feed(string input)
        {
            int start = session.Output.Length;
            session.C64.EnqueuePetsciiText(input);
            session.Run(1_000_000);
            return session.Output.ToString(start, session.Output.Length - start);
        }
        string output = Feed("PRINT 12" + (char)157 + (char)20 + (char)148 + "3\r");
        check("editor_insert_delete_execute", Regex.IsMatch(output, @"(^|\s)32(\s|$)") && !output.Contains("?"),
            "LEFT, DELETE, INSERT and overwrite change PRINT 12 into PRINT 32 before execution", output);

        session.Command("NEW");
        session.Command("10 PRINT 1");
        byte[] before = session.BasicProgramBytes();
        session.Command("PRINTCHR$(147)");
        session.Command("LIST");
        string screen = session.Screen();
        string[] rows = screen.Split('\n');
        int targetRow = Array.FindLastIndex(rows, row => row.Contains("10 PRINT 1"));
        int targetColumn = targetRow < 0 ? -1 : rows[targetRow].IndexOf("10 PRINT 1", StringComparison.Ordinal) + 9;
        if (targetRow >= 0)
            Feed(((char)19).ToString() + new string((char)17, targetRow) + new string((char)29, targetColumn) + "2\r");
        byte[] expected = (byte[])before.Clone();
        bool originalOne = expected.Length >= 4 && expected[^4] == (byte)'1';
        if (originalOne) expected[^4] = (byte)'2';
        byte[] after = session.BasicProgramBytes();
        // RETURN leaves the cursor on the next existing screen row. Move to a
        // known blank row before RUN so an old READY prompt is not recaptured.
        output = Feed(((char)19).ToString() + new string((char)17, 24) + "RUN\r");
        check("editor_recaptures_listed_line", targetRow >= 0 && originalOne && expected.SequenceEqual(session.BasicProgramBytes()) &&
            Regex.IsMatch(output, @"(^|\s)2(\s|$)") && !output.Contains("?"),
            "HOME/DOWN/RIGHT edits the visible LIST row; RETURN retokenizes the corrected line and RUN prints 2",
            "Before edit:\n" + screen + "\nProgram before=" + Convert.ToHexString(before) +
            "\nProgram after=" + Convert.ToHexString(after) + "\nRUN:\n" + output);

        output = session.Command("POKE2040,77:PRINTCHR$(147):PRINTPEEK(2040)");
        check("editor_clear_preserves_sprite_pointers", session.C64.PeekRam(2040) == 77 &&
            Regex.IsMatch(output, @"(^|\s)77(\s|$)") && !output.Contains("?"),
            "Clear screen leaves the sprite pointer byte at $07F8 unchanged", output);
    }
}
