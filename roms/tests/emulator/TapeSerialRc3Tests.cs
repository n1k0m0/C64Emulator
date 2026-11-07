// SPDX-License-Identifier: Apache-2.0
// Physical-pin integration: CPU executes every ROM instruction, CIA timers count
// every cycle, and TAP files contain measured 6510 WRITE edge intervals.
// Independent fixture: Commodore VIC C2N Technical Manual, Tape Format diagram:
// S/L/B half cycles 182/262/342 us, odd parity, duplicated countdown/payload/XOR.
// https://www.scribd.com/document/971226317/Commodore-1531-Technical-Manual
// TAP container contract: https://vice-emu.sourceforge.io/vice_17.html
using C64Emulator.Core;
using System.Reflection;
using System.Text;

static class TapeSerialRc3Tests
{
#if TAPE_SERIAL_RC3_PATCH
    static SystemBus Bus(Session session) => (SystemBus)typeof(C64System)
        .GetField("_bus", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session.C64);

    static void Put(Session session, int address, IEnumerable<byte> bytes)
    {
        // Generated callers and payloads go into RAM. No ROM, CPU operation,
        // timer, FLAG input, byte reader or pulse primitive is intercepted.
        SystemBus bus = Bus(session);
        foreach (byte value in bytes) bus.WriteRam((ushort)address++, value);
    }

    static byte[] Get(Session session, int address, int count) => Enumerable.Range(address, count)
        .Select(a => session.C64.PeekRam((ushort)a)).ToArray();

    static List<byte> Setup(string name, byte device, byte secondary) => new List<byte> {
        0xA9,2, 0xA2,device, 0xA0,secondary, 0x20,0xBA,0xFF,
        0xA9,(byte)name.Length, 0xA2,0, 0xA0,0xC2, 0x20,0xBD,0xFF
    };

    static void Finish(List<byte> code)
    {
        code.AddRange(new byte[] { 0x8D,0xF1,0xC3, 0x08,0x68,0x8D,0xF2,0xC3,
            0x8E,0xF3,0xC3,0x8C,0xF4,0xC3,0xA5,0x90,0x8D,0xF5,0xC3,
            0xAD,0x11,0xD0,0x29,0x7F,0x8D,0xF6,0xC3,
            0xAD,0x15,0xD0,0x8D,0xF7,0xC3,
            0xA9,0xA5,0x8D,0xF0,0xC3,0x60 });
    }

    static string Execute(Session session, List<byte> code, string name, int maximumCycles = 40_000_000)
    {
        Finish(code);
        Put(session,0xC000,code);
        Put(session,0xC200,Encoding.ASCII.GetBytes(name));
        Put(session,0xC3F0,new byte[8]);
        int start=session.Output.Length;
        session.Command("SYS49152",100_000);
        int cycles=100_000;
        while(session.C64.PeekRam(0xC3F0)!=0xA5 && cycles<maximumCycles && !session.Jammed)
        { session.Run(100_000); cycles+=100_000; }
        session.Run(100_000);
        return session.Output.ToString(start,session.Output.Length-start)+
            $"\ncycles={cycles}; result={Convert.ToHexString(Get(session,0xC3F0,8))}; tapePosition={session.C64.Tape.Position}; pulses={session.C64.Tape.PulseCount}";
    }

    static bool Succeeded(Session s) => s.C64.PeekRam(0xC3F0)==0xA5 && (s.C64.PeekRam(0xC3F2)&1)==0;
    static bool Error(Session s, byte error) => s.C64.PeekRam(0xC3F0)==0xA5 &&
        (s.C64.PeekRam(0xC3F2)&1)!=0 && s.C64.PeekRam(0xC3F1)==error;
    static bool Restored(Session s) => !s.C64.Tape.MotorOn &&
        s.C64.PeekRam(0xC3F6)==0x1B && s.C64.PeekRam(0xC3F7)==0xFF &&
        (s.C64.PeekRam(0xC3F2)&4)==0;

    static void EnableDma(Session s)
    {
        // Deliberately begin every operation with DEN and all sprite enables on.
        // The ROM must control contention and restore these values itself.
        Bus(s).CpuWrite(0xD011,0x1B);
        Bus(s).CpuWrite(0xD015,0xFF);
        for(int i=0;i<8;i++) Bus(s).CpuWrite((ushort)(0xD001+i*2),(byte)(60+i*8));
    }

    static List<byte> LoadCode(string name, bool verify=false, byte secondary=1, ushort address=0x3000)
    {
        var code=Setup(name,1,secondary);
        code.AddRange(new byte[] {0xA9,(byte)(verify?1:0),0xA2,(byte)address,0xA0,(byte)(address>>8),0x20,0xD5,0xFF});
        return code;
    }

    // This encoder uses no ROM symbols, tables, measured output or implementation
    // constants. Its real-time widths come from the published C2N waveform.
    sealed class Fixture
    {
        public readonly List<int> Pulses = new();
        public readonly List<int> PayloadStarts = new();
        readonly int shortPulse=(int)Math.Round(364e-6*985248);
        readonly int mediumPulse=(int)Math.Round(524e-6*985248);
        readonly int longPulse=(int)Math.Round(684e-6*985248);
        void Bit(int bit)
        {
            Pulses.Add(bit==0?shortPulse:mediumPulse);
            Pulses.Add(bit==0?mediumPulse:shortPulse);
        }
        void Byte(byte value)
        {
            Pulses.Add(longPulse);Pulses.Add(mediumPulse);
            int parity=1;
            for(int b=0;b<8;b++) {int bit=(value>>b)&1;parity^=bit;Bit(bit);}
            Bit(parity);
        }
        public void Block(byte[] data, bool repeat, bool badChecksum=false, bool badParity=false)
        {
            Pulses.AddRange(Enumerable.Repeat(shortPulse,1024));
            for(int n=9;n>0;n--) Byte((byte)(n+(repeat?0:0x80)));
            int start=Pulses.Count;PayloadStarts.Add(start);
            byte checksum=0;
            foreach(byte value in data) {Byte(value);checksum^=value;}
            Byte((byte)(checksum^(badChecksum?1:0)));
            if(badParity) (Pulses[start+18],Pulses[start+19])=(Pulses[start+19],Pulses[start+18]);
            Pulses.Add(longPulse);Pulses.Add(shortPulse);
        }
        public byte[] Tap() => TapImage.Encode(Pulses,0,1);
    }

    static byte[] Header(byte type, string name, int size)
    {
        byte[] header=Enumerable.Repeat((byte)0x20,192).ToArray();
        header[0]=type;header[1]=0;header[2]=0x30;
        header[3]=(byte)size;header[4]=(byte)(0x30+(size>>8));
        Encoding.ASCII.GetBytes(name).CopyTo(header,5);
        return header;
    }

    static Fixture Standard(byte[] payload, int badHeaders=0, int badData=0, int badParity=0, byte type=3, bool eotOnly=false)
    {
        var fixture=new Fixture();
        if(!eotOnly)
        {
            byte[] header=Header(type,"STANDARD",payload.Length);
            for(int copy=0;copy<2;copy++) fixture.Block(header,copy==1,(badHeaders&(1<<copy))!=0);
            for(int copy=0;copy<2;copy++) fixture.Block(payload,copy==1,(badData&(1<<copy))!=0,(badParity&(1<<copy))!=0);
        }
        byte[] end=Header(5,"END",payload.Length);
        fixture.Block(end,false);fixture.Block(end,true);
        return fixture;
    }

    static List<byte[]> DecodeRecorded(int[] pulses)
    {
        // A separate host decoder validates what SAVE emitted: byte markers,
        // ordered short/medium pairs, odd parity, countdowns and XOR checksums.
        int Class(int pulse) => pulse<450?1:pulse<610?2:3;
        var blocks=new List<byte[]>();int position=0;
        while(position+20<=pulses.Length)
        {
            if(Class(pulses[position])!=3 || Class(pulses[position+1])!=2) {position++;continue;}
            var bytes=new List<byte>();
            while(position+20<=pulses.Length && Class(pulses[position])==3 && Class(pulses[position+1])==2)
            {
                int value=0,parity=0;
                for(int b=0;b<9;b++)
                {
                    int a=Class(pulses[position+2+2*b]),c=Class(pulses[position+3+2*b]);
                    if(!((a==1&&c==2)||(a==2&&c==1))) throw new InvalidDataException($"Invalid recorded bit pair at {position}, bit{b}: {a}/{c}");
                    int bit=a==2?1:0;parity^=bit;if(b<8)value|=bit<<b;
                }
                if(parity!=1) throw new InvalidDataException($"Invalid recorded odd parity at {position}");
                bytes.Add((byte)value);position+=20;
            }
            blocks.Add(bytes.ToArray());
        }
        return blocks;
    }

    public static void RunFormat(Action<string,bool,string,string> check)
    {
        int[] values={8,2040,2048,65536,0xFFFFFF};
        bool exact=TapImage.Parse(TapImage.Encode(values,0,1)).Durations.SequenceEqual(values);
        byte[] v0=TapImage.Encode(new int[]{8,2040,2048},0,0);
        exact&=TapImage.Parse(v0).Durations.SequenceEqual(new int[]{8,2040,2048});
        byte[] valid=TapImage.Encode(new int[]{384,528,688},0,1);
        var bad=new List<byte[]>();
        bad.Add(valid.Take(19).ToArray());
        var b=(byte[])valid.Clone();b[0]^=1;bad.Add(b);
        b=(byte[])valid.Clone();b[12]=3;bad.Add(b);
        b=(byte[])valid.Clone();b[13]=1;bad.Add(b);
        b=(byte[])valid.Clone();b[14]=4;bad.Add(b);
        b=(byte[])valid.Clone();b[15]=1;bad.Add(b);
        b=(byte[])valid.Clone();b[16]++;bad.Add(b);
        b=TapImage.Encode(new[]{1},0,1);b[21]=b[22]=b[23]=0;bad.Add(b);
        b=TapImage.Encode(new[]{1},0,1).Take(22).ToArray();b[16]=2;bad.Add(b);
        var deck=new TapeDeck();deck.Mount(valid);int rejected=0;
        foreach(byte[] invalid in bad)
        {
            try {deck.Mount(invalid);} catch(InvalidDataException) {rejected++;}
            exact&=deck.ExportTap().SequenceEqual(valid);
        }
        check("tap_container_bounds_and_atomic_mount",exact&&rejected==bad.Count,
            "v0 overflow and v1 24-bit boundaries roundtrip; malformed headers/escapes are rejected before mounted tape changes",$"rejected={rejected}/{bad.Count}");

        deck.Mount(TapImage.Encode(new[]{40,60,80,100},0,2));deck.Play();
        deck.WriteProcessorPort(0x2F,0x07);
        var falls=new List<int>();bool previous=deck.ReadHigh;
        for(int n=1;n<=280;n++) {deck.Tick();if(previous&&!deck.ReadHigh)falls.Add(n);previous=deck.ReadHigh;}
        bool halves=falls.SequenceEqual(new[]{100,280});
        deck.Mount(TapImage.Encode(new[]{384,528},0,1));deck.Play();
        deck.WriteProcessorPort(0x2F,0x07);for(int n=0;n<100;n++)deck.Tick();
        deck.WriteProcessorPort(0x2F,0x27);for(int n=0;n<2000;n++)deck.Tick();
        bool paused=deck.Position==0;
        deck.WriteProcessorPort(0x2F,0x07);for(int n=0;n<284;n++)deck.Tick();
        check("tap_halfwave_polarity_and_motor_pause",halves&&paused&&deck.Position==1&&!deck.ReadHigh,
            "v2 triggers negative FLAG edges after paired halfwaves; motor-off pauses rather than consumes a pulse",$"v2 falling edges={string.Join(',',falls)}; resumedPosition={deck.Position}");

        deck.ConfigureClock(1022727);deck.Mount(TapImage.Encode(Enumerable.Repeat(384,1000),0,1));deck.Play();
        deck.WriteProcessorPort(0x2F,0x07);int ticks=0;
        while(!deck.EndOfTape&&ticks<500000){deck.Tick();ticks++;}
        double expected=384000.0*1022727/985248;
        check("tap_clock_conversion_fractional_carry",Math.Abs(ticks-expected)<=1,
            "PAL TAP elapsed time is retained on the NTSC CPU clock without cumulative per-pulse rounding drift",$"actualCycles={ticks}; expectedCycles={expected:F6}");
    }

    public static void RunTape(Session session, Action<string,bool,string,string> check)
    {
        byte standard=(byte)(session.C64.Model.Name.StartsWith("NTSC")?1:0);
        byte[] payload=Enumerable.Range(0,64).Select(i=>(byte)(i*73+19)).ToArray();
        Put(session,0x3000,payload);EnableDma(session);
        session.C64.Tape.Record(standard);
        var save=Setup("ROUND",1,3);
        save.AddRange(new byte[] {0xA9,0,0x85,0xF9,0xA9,0x30,0x85,0xFA,
            0xA9,0xF9,0xA2,(byte)payload.Length,0xA0,0x30,0x20,0xD8,0xFF});
        string output=Execute(session,save,"ROUND");
        int[] pulses=session.C64.Tape.RecordedPulses;
        bool saved=Succeeded(session)&&Restored(session)&&pulses.Length>20000;
        session.C64.Tape.Stop();byte[] tap=session.C64.Tape.ExportTap();
        if(Environment.GetEnvironmentVariable("FREE_ROM_TAP_TRACE") is string tracePath)File.WriteAllBytes(tracePath,tap);
        check("tap_save_physical_waveform",saved,
            "Actual SAVE with EOT records physical WRITE intervals, stops motor, and restores DEN/sprite enables/caller IRQ state",
            output+$"\npulses={pulses.Length}; minimum={pulses.Min()}; maximum={pulses.Max()}; totalCycles={pulses.Sum(x=>(long)x)}");
        try
        {
            List<byte[]> blocks=DecodeRecorded(pulses);
            bool structure=blocks.Count==6;
            for(int n=0;n<blocks.Count;n++)
            {
                byte[] block=blocks[n];
                structure&=block.Take(9).SequenceEqual(Enumerable.Range(1,9).Reverse().Select(x=>(byte)(x+(n%2==0?128:0))));
                structure&=block.Skip(9).Aggregate(0,(x,y)=>x^y)==0;
                structure&=block.Length==(n==2||n==3?payload.Length+10:202);
            }
            if(blocks.Count==6) structure&=blocks[0][9]==3&&blocks[1][9]==3&&blocks[4][9]==5&&blocks[5][9]==5&&
                blocks[2].Skip(9).Take(payload.Length).SequenceEqual(payload)&&blocks[3].Skip(9).Take(payload.Length).SequenceEqual(payload)&&
                blocks[0].Skip(9).SequenceEqual(blocks[1].Skip(9))&&blocks[4].Skip(9).SequenceEqual(blocks[5].Skip(9));
            check("tap_saved_format_independent_decode",structure,
                "Separate pulse decoder finds both headers, both data copies, both type-5 EOT headers, descending countdowns, odd parity and zero XOR",$"block byte counts={string.Join(',',blocks.Select(x=>x.Length))}");
        }
        catch(InvalidDataException ex) {check("tap_saved_format_independent_decode",false,"Every recorded frame has valid independent bit-pair/parity decoding",ex.Message);}
        string path=Path.Combine(Path.GetTempPath(),"free-rom-native-"+Guid.NewGuid().ToString("N")+".tap");
        try
        {
            File.WriteAllBytes(path,tap);byte[] reopened=File.ReadAllBytes(path);TapImage image=TapImage.Parse(reopened);
            check("tap_file_exact_pulses",image.Durations.SequenceEqual(pulses)&&image.VideoStandard==standard,
                "TAP v1 reopened from disk preserves every measured pulse cycle exactly",$"bytes={reopened.Length}; pulses={pulses.Length}; standard={standard}");
            // Do not mount again here: physical Rewind/Play must replay a recording.
            Put(session,0x3000,Enumerable.Repeat((byte)0xEE,payload.Length));EnableDma(session);
            session.C64.Tape.Rewind();session.C64.Tape.Play();output=Execute(session,LoadCode("ROUND"),"ROUND");
            check("tap_load_waveform_roundtrip",Succeeded(session)&&Restored(session)&&Get(session,0x3000,payload.Length).SequenceEqual(payload),
                "Actual LOAD replays the new recording through CIA1 FLAG without remount and restores the exact payload",output);
        }
        finally {File.Delete(path);session.C64.Tape.Stop();}

        void LoadCase(string id,Fixture fixture,string name,bool verify,Func<bool> success,byte secondary=1,ushort address=0x3000)
        {
            EnableDma(session);session.C64.Tape.Mount(fixture.Tap());session.C64.Tape.Play();
            string result=Execute(session,LoadCode(name,verify,secondary,address),name);
            check(id,success()&&Restored(session),"Real CPU/CIA/FLAG waveform execution meets payload/status policy and restores video/motor/IRQ state",result);
            session.C64.Tape.Stop();
        }
        Fixture independent=Standard(payload);
        if(Environment.GetEnvironmentVariable("FREE_ROM_TAPE_ARTIFACTS") is string artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.WriteAllBytes(Path.Combine(artifacts,"rom-save-with-eot.tap"),tap);
            File.WriteAllBytes(Path.Combine(artifacts,"independent-c2n-pal.tap"),independent.Tap());
            File.WriteAllBytes(Path.Combine(artifacts,"independent-first-data-checksum-bad.tap"),Standard(payload,badData:1).Tap());
            File.WriteAllBytes(Path.Combine(artifacts,"independent-type5-eot.tap"),Standard(payload,eotOnly:true).Tap());
        }
        Put(session,0x3000,new byte[payload.Length]);
        LoadCase("tap_independent_c2n_standard",independent,"STANDARD",false,()=>Succeeded(session)&&Get(session,0x3000,payload.Length).SequenceEqual(payload));
        byte[] allBytes=Enumerable.Range(0,257).Select(i=>(byte)(i*73+19)).ToArray();
        Put(session,0x3000,new byte[allBytes.Length]);
        LoadCase("tap_all_byte_values_and_page_crossing",Standard(allBytes),"STANDARD",false,()=>Succeeded(session)&&
            Get(session,0x3000,allBytes.Length).SequenceEqual(allBytes)&&session.C64.PeekRam(0xC3F3)==1&&session.C64.PeekRam(0xC3F4)==0x31);
        Put(session,0x3000,payload);
        LoadCase("tap_verify_waveform_success",independent,"STANDARD",true,()=>Succeeded(session)&&Get(session,0x3000,payload.Length).SequenceEqual(payload));
        byte[] changed=(byte[])payload.Clone();changed[11]^=0x80;Put(session,0x3000,changed);
        LoadCase("tap_verify_waveform_mismatch",independent,"STANDARD",true,()=>Error(session,0x1C)&&Get(session,0x3000,payload.Length).SequenceEqual(changed));
        Put(session,0x3000,new byte[payload.Length]);
        LoadCase("tap_header_duplicate_repairs_checksum",Standard(payload,badHeaders:1),"STANDARD",false,()=>Succeeded(session)&&Get(session,0x3000,payload.Length).SequenceEqual(payload));
        Put(session,0x3000,new byte[payload.Length]);
        LoadCase("tap_data_duplicate_repairs_checksum",Standard(payload,badData:1),"STANDARD",false,()=>Succeeded(session)&&Get(session,0x3000,payload.Length).SequenceEqual(payload));
        Put(session,0x3000,new byte[payload.Length]);
        LoadCase("tap_data_duplicate_repairs_parity",Standard(payload,badParity:1),"STANDARD",false,()=>Succeeded(session)&&Get(session,0x3000,payload.Length).SequenceEqual(payload));
        LoadCase("tap_both_data_copies_bad",Standard(payload,badData:3),"STANDARD",false,()=>Error(session,4));
        LoadCase("tap_both_header_copies_bad",Standard(payload,badHeaders:3),"STANDARD",false,()=>Error(session,4));
        Put(session,0x3400,new byte[payload.Length]);
        LoadCase("tap_independent_relocatable_header",Standard(payload,type:1),"STANDARD",false,()=>Succeeded(session)&&Get(session,0x3400,payload.Length).SequenceEqual(payload),0,0x3400);
        foreach(string name in new[]{"MISSING",""})
        {
            Fixture end=Standard(payload,eotOnly:true);
            LoadCase("tap_eot_"+(name.Length==0?"unnamed":"named"),end,name,false,()=>Error(session,4)&&
                (session.C64.PeekRam(0xC3F5)&0x80)!=0&&session.C64.Tape.Position<end.Pulses.Count/2+10);
        }
        var truncated=new Fixture();truncated.Pulses.AddRange(independent.Pulses.Take(1500));
        LoadCase("tap_truncated_waveform_timeout",truncated,"STANDARD",false,()=>Error(session,4)&&
            (session.C64.PeekRam(0xC3F5)&0x80)!=0&&session.C64.Tape.EndOfTape);
        EnableDma(session);session.C64.Tape.Mount(independent.Tap());
        output=Execute(session,LoadCode("NAME-OVER-SIXTEEN!"),"NAME-OVER-SIXTEEN!");
        check("tap_invalid_name_preserves_video",Error(session,4)&&Restored(session)&&session.C64.Tape.Position==0,
            "A rejected long filename returns before starting transport and does not restore uninitialized video state",output);
        Bus(session).CpuWrite(0xD015,0);
    }
#else
    public static void RunFormat(Action<string,bool,string,string> check) => check("tap_patch_required",false,"RC3 TAP patch is compiled","Missing patch");
    public static void RunTape(Session session, Action<string,bool,string,string> check) => RunFormat(check);
#endif
}
