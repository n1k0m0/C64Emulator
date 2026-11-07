// SPDX-License-Identifier: Apache-2.0
// Native RC3 screen/cursor checks. BASIC executes ordinary POKE/PRINT/input and
// actual KERNAL IRQs draw the cursor; completed VIC frames provide visual proof.
using C64Emulator.Core;
using System.Text;
using System.Text.RegularExpressions;

static class EditorRc3Tests
{
    public static void Run(Session session, Func<byte[], byte[]> makeD64, Action<string, bool, string, string> check)
    {
        const int screenBase = 0xC000;
        int frameCycles = session.C64.Model.RasterLines * session.C64.Model.CyclesPerLine;
        string directory = Path.Combine(Path.GetTempPath(), "free-c64-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string diskPath = Path.Combine(directory, "editor.d64");
        File.WriteAllBytes(diskPath, makeD64(new byte[] { 1, 8, 0, 0 }));
        bool mounted = false;
        string Feed(string input, int cycles = 500_000)
        {
            int start = session.Output.Length;
            session.C64.EnqueuePetsciiText(input);
            session.Run(cycles);
            return session.Output.ToString(start, session.Output.Length - start);
        }
        byte[] ScreenBytes(int address) => Enumerable.Range(address, 1000).Select(a => session.C64.PeekRam((ushort)a)).ToArray();
        string ScreenText()
        {
            var text = new StringBuilder();
            for (int row = 0; row < 25; row++)
            {
                for (int col = 0; col < 40; col++)
                {
                    byte b = (byte)(session.C64.PeekRam((ushort)(screenBase + row*40 + col)) & 127);
                    text.Append(b < 32 ? (char)(b+64) : b < 96 ? (char)b : '.');
                }
                text.Append('\n');
            }
            return text.ToString();
        }
        bool NextPhase(byte phase)
        {
            bool seenOther = session.C64.PeekRam(0xCF) != phase;
            for (int tick = 0; tick < 2400; tick++)
            {
                session.Run(500);
                byte current = session.C64.PeekRam(0xCF);
                if (current != phase) seenOther = true;
                if (seenOther && current == phase)
                {
                    // Let a complete frame reflect the newly drawn/removed overlay.
                    session.Run(frameCycles * 2);
                    return session.C64.PeekRam(0xCF) == phase;
                }
            }
            return false;
        }
        uint[] CellPixels(int row, int column)
        {
            int x0 = (session.C64.FrameBuffer.Width - 320)/2 + column*8;
            int y0 = (session.C64.Model.RasterLines == 263 ? 10 : 37) + row*8;
            uint[] pixels = session.C64.FrameBuffer.CompletedPixels;
            return Enumerable.Range(0, 64).Select(i => pixels[(y0+i/8)*session.C64.FrameBuffer.Width+x0+i%8]).ToArray();
        }
        try
        {
            session.Command("NEW", 200_000);
            // A small independently generated RAM charset fixture for bank3.
            // Space is blank, reverse-space is solid, and A is a solid glyph.
            // Other characters are still processed normally by the screen editor;
            // their fixture pixels are not part of these visual assertions.
            session.Command("FORI=0TO7:POKE51456+I,0:POKE52480+I,255:NEXT", 500_000);
            session.Command("FORI=0TO7:POKE51208+I,255:NEXT", 300_000);
            string setup = session.Command("POKE648,192:POKE56576,PEEK(56576)AND252:POKE53272,2:PRINTCHR$(147)", 500_000);
            byte[] oldScreen = ScreenBytes(0x0400);
            string output = session.Command("PRINT6*7", 300_000);
            bool mapping = session.C64.PeekRam(0x0288) == 0xC0 && (session.C64.Peek(0xDD00)&3) == 0 &&
                (session.C64.Peek(0xD018)&0xFE) == 2;
            check("editor_rc3_relocated_basic", mapping && oldScreen.SequenceEqual(ScreenBytes(0x0400)) &&
                Regex.IsMatch(output, @"(^|\s)42(\s|$)") && !output.Contains("?"),
                "BASIC input/output at C000 works while the old 0400 screen and application VIC bank remain unchanged",
                setup + output + $"\nHIBASE={session.C64.PeekRam(0x288):X2}, CIA2={session.C64.Peek(0xDD00):X2}, VIC={session.C64.Peek(0xD018):X2}");

            session.Command("POKE646,2:PRINTCHR$(147);\"A\";", 250_000);
            session.Run(frameCycles*2);
            uint[] glyph = CellPixels(0,0);
            check("editor_rc3_relocated_vic_pixels", session.C64.PeekRam(0xC000) == 1 &&
                (session.C64.Peek(0xD800)&15) == 2 && glyph.All(p => p == 0xFF68372Bu),
                "Actual VIC renders the bank3 C000 screen through the generated C800 RAM font and fixed D800 color RAM",
                $"screen code={session.C64.PeekRam(0xC000)}, color={session.C64.Peek(0xD800)&15}, red pixels={glyph.Count(p=>p==0xFF68372Bu)}");

            session.Command("POKE646,14", 200_000);
            int row = session.C64.PeekRam(0xD6), column = session.C64.PeekRam(0xD3), offset = row*40+column;
            bool off = NextPhase(0);
            byte original = session.C64.PeekRam((ushort)(screenBase+offset));
            byte color = (byte)(session.C64.Peek((ushort)(0xD800+offset))&15);
            uint[] offPixels = CellPixels(row,column);
            bool on = NextPhase(1);
            uint[] onPixels = CellPixels(row,column);
            bool overlay = session.C64.PeekRam((ushort)(screenBase+offset)) == (original^0x80) &&
                session.C64.PeekRam(0xCE) == original && session.C64.PeekRam(0x0287) == color;
            check("editor_rc3_native_cursor_blinks", off && on && original == 32 && overlay &&
                offPixels.All(p=>p==0xFF352879u) && onPixels.All(p=>p==0xFF6C5EB5u),
                "Idle CHRIN draws and removes its reverse-space cursor in completed VIC frames, preserving the original cell/color",
                $"row={row}, column={column}, original={original}, color={color}, phase={session.C64.PeekRam(0xCF)}, background pixels={offPixels.Count(p=>p==0xFF352879u)}, cursor pixels={onPixels.Count(p=>p==0xFF6C5EB5u)}");

            session.C64.DisableKernalTextCursor(); // Public host API writes the documented BLNSW byte only.
            session.Run(frameCycles*2);
            check("editor_rc3_native_cursor_disable", session.C64.PeekRam(0xCC) != 0 && session.C64.PeekRam(0xCF) == 0 &&
                session.C64.PeekRam((ushort)(screenBase+offset)) == original &&
                (session.C64.Peek((ushort)(0xD800+offset))&15) == color,
                "Setting BLNSW while visible restores both underlays on the next actual CIA timer IRQ",
                $"CC={session.C64.PeekRam(0xCC)}, CF={session.C64.PeekRam(0xCF)}, cell={session.C64.PeekRam((ushort)(screenBase+offset))}, color={session.C64.Peek((ushort)(0xD800+offset))&15}");
            output = session.Command("PRINT7*8", 300_000);
            check("editor_rc3_capture_after_blink", Regex.IsMatch(output, @"(^|\s)56(\s|$)") && !output.Contains("?"),
                "Input resumes after an active cursor and its reverse overlay never becomes a BASIC token", output);

            session.Command("NEW", 200_000);
            session.Command("10 PRINT 1", 200_000);
            byte[] before = session.BasicProgramBytes();
            session.Command("PRINTCHR$(147)", 200_000);
            session.Command("LIST", 300_000);
            string screen = ScreenText();
            string[] rows = screen.Split('\n');
            int targetRow = Array.FindLastIndex(rows, line=>line.Contains("10 PRINT 1"));
            int targetColumn = targetRow < 0 ? -1 : rows[targetRow].IndexOf("10 PRINT 1", StringComparison.Ordinal)+9;
            if (targetRow >= 0)
                Feed(((char)19).ToString()+new string((char)17,targetRow)+new string((char)29,targetColumn)+"2\r");
            byte[] expected = (byte[])before.Clone();
            bool originalOne = expected.Length>=4 && expected[^4] == (byte)'1';
            if (originalOne) expected[^4] = (byte)'2';
            output = Feed(((char)19).ToString()+new string((char)17,24)+"RUN\r");
            check("editor_rc3_relocated_list_edit", targetRow>=0 && originalOne && expected.SequenceEqual(session.BasicProgramBytes()) &&
                Regex.IsMatch(output, @"(^|\s)2(\s|$)") && !output.Contains("?"),
                "LIST/cursor editing on the relocated screen retokenizes exact changed bytes and RUN produces the changed value",
                screen + "\nProgram=" + Convert.ToHexString(session.BasicProgramBytes()) + "\n" + output);

            string mount = session.C64.MountMedia(diskPath,8);
            mounted = mount.Contains("MOUNTED");
            session.Command("NEW", 200_000);
            session.Command("10 OPEN15,8,15:INPUT#15,E,M$,T,S:CLOSE15:PRINT\"DOS\";E", 400_000);
            output = session.Command("RUN", 4_000_000);
            bool bankRetained = (session.C64.Peek(0xDD00)&3) == 0 && session.C64.PeekRam(0x0288) == 0xC0 &&
                (session.C64.Peek(0xD018)&0xFE) == 2;
            check("editor_rc3_relocated_iec_bank", mounted && bankRetained && output.Contains("DOS") && !output.Contains("?"),
                "A real IEC status-channel transaction preserves bank3, the screen page and RAM charset selection",
                mount + "\n" + output + $"\nCIA2={session.C64.Peek(0xDD00):X2}");

            output = session.Command("POKE50168,77:PRINTCHR$(147):PRINTPEEK(50168)",300_000);
            check("editor_rc3_relocated_sprite_tail", session.C64.PeekRam(50168) == 77 &&
                Regex.IsMatch(output,@"(^|\s)77(\s|$)") && !output.Contains("?"),
                "Clearing a relocated C000 screen preserves its C3F8 sprite pointer", output);
        }
        finally
        {
            session.Command("POKE648,4:POKE56576,PEEK(56576)OR3:POKE53272,21",300_000);
            session.Command("POKE646,14:PRINTCHR$(147):NEW",250_000);
            if (mounted) session.C64.EjectMedia();
            Directory.Delete(directory,true);
        }
    }
}
