// SPDX-License-Identifier: Apache-2.0
// Independent expectations from Christian Bauer, VIC-II article, sections3.4/3.6.3:
// https://www.cebix.net/VIC-Article.txt . Test the actual VIC/CPU/CIA/SID core.
using C64Emulator.Core;
using System.Text;

static class VideoStandardTests
{
    public static void Run(Session session, Action<string, bool, string, string> check)
    {
#if VIDEO_STANDARD_PATCH
        C64Model model = session.C64.Model;
        int expectedLines = model.IsNtsc ? 263 : 312;
        int expectedCycles = model.IsNtsc ? 65 : 63;
        double expectedClock = model.IsNtsc ? 1022727 : 985248;
        string standard = model.VideoStandard;
        check("video_model_constants", model.RasterLines == expectedLines && model.CyclesPerLine == expectedCycles && model.CpuHz == expectedClock,
            "PAL6569 and NTSC6567R8 use independent documented timing constants",
            $"{standard}: {model.CpuHz}Hz, {model.CyclesPerLine}cycles/line, {model.RasterLines}lines/frame, {model.FrameHz:F6}Hz frame");

        var bus = new SystemBus();
        var frame = new FrameBuffer(model.VisibleWidth, model.VisibleHeight);
        var vic = new Vic2(bus, frame, model);
        using var sid = new Sid(model.CpuHz) { LocalAudioEnabled = false };
        bus.Connect(vic, new Cia1(), new Cia2(), sid);
        bus.InitializeMemory();
        vic.Reset();
        long initialFrame = frame.CompletedFrameId;
        bool rasterRegisters = true;
        for (int line = 0; line < expectedLines; line++)
        {
            VicTiming t = vic.GetTiming();
            rasterRegisters &= t.RasterLine == line && t.CycleInLine == 0;
            rasterRegisters &= vic.Read(0xD012) == (line & 255) && ((vic.Read(0xD011) >> 7) & 1) == (line >> 8);
            for (int cycle = 0; cycle < expectedCycles; cycle++) vic.Tick();
        }
        VicTiming afterFrame = vic.GetTiming();
        check("video_exact_frame_period", rasterRegisters && afterFrame.RasterLine == 0 && afterFrame.CycleInLine == 0 &&
            afterFrame.GlobalCycle == expectedLines * expectedCycles && frame.CompletedFrameId == initialFrame + 1,
            "Every raster register value, full line length and exact frame wrap follow the selected model",
            $"cycles={afterFrame.GlobalCycle}, raster={afterFrame.RasterLine}:{afterFrame.CycleInLine}, frames={frame.CompletedFrameId-initialFrame}");

        vic.Reset();
        vic.Write(0xD01A, 1);
        vic.Write(0xD011, 0x80); // Compare line260, beyond the eight-bit raster range.
        vic.Write(0xD012, 4);
        for (int cycle = 0; cycle < 260 * expectedCycles; cycle++) vic.Tick();
        bool notEarly = (vic.Read(0xD019) & 1) == 0;
        vic.Tick();
        bool highRasterIrq = (vic.Read(0xD019) & 0x81) == 0x81;
        vic.Write(0xD019, 1);
        bool acknowledged = (vic.Read(0xD019) & 1) == 0;
        check("video_high_raster_irq", notEarly && highRasterIrq && acknowledged,
            "Raster260 triggers exactly at its first cycle and write-one acknowledges it",
            $"notEarly={notEarly}, irq={highRasterIrq}, acknowledged={acknowledged}");

        var plan = new VicBusPlan(expectedCycles);
        plan.BuildLine(false, Enumerable.Repeat(true,8).ToArray());
        int[] expectedPointers = model.IsNtsc ? new[] {60,62,64,1,3,5,7,9} : new[] {58,60,62,1,3,5,7,9};
        bool pointers = true;
        for(int sprite=0;sprite<8;sprite++)
        {
            int pointer=expectedPointers[sprite];
            VicBusSlot a=plan.GetSlot(pointer-1), b=plan.GetSlot(pointer % expectedCycles);
            pointers &= a.Phi1Action==VicBusAction.SpritePointerFetch && a.SpriteIndex==sprite &&
                a.Phi2Action==VicBusAction.SpriteDataFetch && a.BlocksCpu &&
                b.Phi1Action==VicBusAction.SpriteDataFetch && b.Phi2Action==VicBusAction.SpriteDataFetch && b.BlocksCpu;
        }
        check("video_sprite_bus_slots", pointers,
            "All eight pointer/data DMA slots, including NTSC cycles64/65, occur without PAL wrapping",
            "pointer cycles="+string.Join(',',expectedPointers));
        plan.BuildLine(true, new bool[8]);
        int matrixReads=0, requests=0, chars=0;
        for(int cycle=0;cycle<expectedCycles;cycle++)
        {
            VicBusSlot slot=plan.GetSlot(cycle);
            if(slot.Phi2Action==VicBusAction.MatrixFetch)matrixReads++;
            if(slot.BusRequestPending)requests++;
            if(slot.Phi1Action==VicBusAction.CharFetch)chars++;
        }
        check("video_badline_bus_slots", matrixReads==40 && requests==3 && chars==40,
            "Both standards retain forty matrix/character fetches and the three-cycle BA lead",
            $"matrix={matrixReads}, graphics={chars}, BA lead={requests}");

        long audioBytes=0;
        sid.AudioBytesGenerated += (_,count)=>audioBytes+=count;
        for(int cycle=0;cycle<(int)expectedClock;cycle++)sid.Tick();
        // SID emits signed16-bit mono samples. One unflushed2048-byte buffer is allowed.
        long expectedBytes=44100*2;
        check("video_sid_sample_clock", sid.ClockHz==expectedClock && audioBytes<=expectedBytes && audioBytes>=expectedBytes-2048,
            "One second of the selected CPU/SID clock produces44100 mono samples, within one output buffer",
            $"SID clock={sid.ClockHz}, flushed bytes={audioBytes}, expected={expectedBytes}");

        string[] arguments={"--benchmark","1000","--video",model.IsNtsc?"ntsc":"pal","result.log"};
        C64Model selected=C64Model.ParseCommandLine(ref arguments);
        bool rejected=false;
        try { string[] conflict={"--pal","--ntsc"}; C64Model.ParseCommandLine(ref conflict); }
        catch(ArgumentException){rejected=true;}
        check("video_frontend_selection", selected.VideoStandard==standard && arguments.SequenceEqual(new[]{"--benchmark","1000","result.log"}) && rejected,
            "Frontend video switches preserve mode arguments and reject conflicting selections",
            selected.Name+": "+string.Join(' ',arguments));

        long frameId=session.C64.FrameBuffer.CompletedFrameId;
        session.Run(expectedLines*expectedCycles*3);
        check("video_native_frame_pacing", session.C64.FrameBuffer.CompletedFrameId-frameId==3,
            "The complete running machine produces exactly three frames in three model frame periods",
            $"frames={session.C64.FrameBuffer.CompletedFrameId-frameId}");

        byte videoFlag=session.C64.PeekRam(0x02A6);
        check("video_rom_autodetection", videoFlag==(model.IsNtsc?0:1),
            "The ROM detects the actual raster standard and publishes PAL1/NTSC0 at$02A6",
            $"$02A6={videoFlag}, machine={standard}");
        int Jiffies() => (session.C64.PeekRam(0xA0)<<16)|(session.C64.PeekRam(0xA1)<<8)|session.C64.PeekRam(0xA2);
        int startJiffy=Jiffies();
        session.Run((int)expectedClock);
        int elapsedJiffies=(Jiffies()-startJiffy+5184000)%5184000;
        check("video_rom_jiffy_rate", elapsedJiffies>=59 && elapsedJiffies<=61,
            "The native CIA/ROM clock advances60 jiffies per emulated second under either video standard",
            $"elapsed={elapsedJiffies}");

        session.Command("PRINTCHR$(147):POKE1984,1:POKE56256,2",300_000);
        session.Run(expectedLines*expectedCycles*2);
        int glyphX=(model.VisibleWidth-320)/2;
        int glyphY=(model.IsNtsc?10:37)+24*8;
        int foregroundPixels=0;
        uint[] pixels=session.C64.FrameBuffer.CompletedPixels;
        for(int y=glyphY;y<glyphY+8;y++)
            for(int x=glyphX;x<glyphX+8;x++)
                if(pixels[y*model.VisibleWidth+x]==0xFF68372Bu)foregroundPixels++;
        check("video_last_text_row_visible", session.C64.PeekRam(1984)==1 && foregroundPixels>0,
            "The actual VIC renders the last text row inside the PAL/NTSC framebuffer with its color RAM",
            $"frame={model.VisibleWidth}x{model.VisibleHeight}, glyph=({glyphX},{glyphY}), red pixels={foregroundPixels}");

        byte[] snapshot;
        using(var stream=new MemoryStream())
        {
            using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))session.C64.SaveState(writer);
            snapshot=stream.ToArray();
        }
        byte before=session.C64.PeekRam(0xC000);
        session.Command("POKE49152,"+(before^0xFF),200_000);
        using(var stream=new MemoryStream(snapshot))using(var reader=new BinaryReader(stream))session.C64.LoadState(reader);
        check("video_savestate_same_model", session.C64.PeekRam(0xC000)==before,
            "A same-model savestate restores RAM and the machine's raster/peripheral state",
            $"restored C000={session.C64.PeekRam(0xC000)}");
        byte[] mismatch=(byte[])snapshot.Clone();
        BitConverter.GetBytes(model.IsNtsc?63:65).CopyTo(mismatch,4);
        bool wrongModelRejected=false;
        session.Command("POKE49152,90",200_000);
        try { using var stream=new MemoryStream(mismatch);using var reader=new BinaryReader(stream);session.C64.LoadState(reader); }
        catch(InvalidDataException){wrongModelRejected=true;}
        check("video_savestate_model_guard", wrongModelRejected && session.C64.PeekRam(0xC000)==0x5A,
            "A mismatched video-standard snapshot is rejected before mutating RAM",
            $"rejected={wrongModelRejected}, C000={session.C64.PeekRam(0xC000)}");
        session.Command("POKE49152,"+before,200_000);
#else
        check("video_standard_patch_missing",false,"Video-standard emulator overrides are required","Compile with the RC3 override directory.");
#endif
    }
}
