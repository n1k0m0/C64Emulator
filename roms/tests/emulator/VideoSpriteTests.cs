// SPDX-License-Identifier: Apache-2.0
// Regression for the NTSC viewport wrap affecting sprite-local Y coordinates.
// Bauer, VIC-II article, section3.8.2: sprite-sprite collisions also occur
// outside the visible screen area. https://www.cebix.net/VIC-Article.txt
using C64Emulator.Core;

static class VideoSpriteTests
{
    public static void Run(Session session, Action<string, bool, string, string> check)
    {
#if VIDEO_STANDARD_PATCH
        C64Model model = session.C64.Model;
        var visible = Probe(model, 60);
        check("video_sprite_visible_control", visible.Collision == 0x18 &&
            visible.ActiveRow && !visible.BorderClosed && visible.RedPixels == 24 * 21,
            "Overlapping solid sprites3/4 at X100/Y60 set both collision bits and render one complete24x21 red sprite",
            $"{model.VideoStandard}: D01E={visible.Collision:X2}, active row={visible.ActiveRow}, border={visible.BorderClosed}, red pixels={visible.RedPixels}");

        var precrop = Probe(model, 0);
        check("video_sprite_precrop_collision", precrop.Collision == 0x18 &&
            precrop.ActiveRow && precrop.BorderClosed,
            "Sprites3/4 at Y0 collide behind the closed border; viewport cropping/wrapping cannot suppress D01E",
            $"{model.VideoStandard}: D01E={precrop.Collision:X2}, active row={precrop.ActiveRow}, border={precrop.BorderClosed}");
#else
        check("video_sprite_patch_required", false,
            "The RC3 video-standard emulator patch is required", "missing patch");
#endif
    }

#if VIDEO_STANDARD_PATCH
    private static (byte Collision, int RedPixels, bool ActiveRow, bool BorderClosed) Probe(C64Model model, byte spriteY)
    {
        var bus = new SystemBus();
        var frame = new FrameBuffer(model.VisibleWidth, model.VisibleHeight);
        var vic = new Vic2(bus, frame, model);
        using var sid = new Sid(model.CpuHz) { LocalAudioEnabled = false };
        bus.Connect(vic, new Cia1(), new Cia2(), sid);
        bus.InitializeMemory();
        vic.Reset();

        // Independent solid sprite fixture in RAM; both sprites share63 bytes.
        for (int offset = 0; offset < 63; offset++)
            bus.WriteRam((ushort)(0x2000 + offset), 255);
        bus.WriteRam(0x07FB, 0x80);
        bus.WriteRam(0x07FC, 0x80);
        vic.Write(0xD006, 100);
        vic.Write(0xD007, spriteY);
        vic.Write(0xD008, 100);
        vic.Write(0xD009, spriteY);
        vic.Write(0xD015, 0x18);
        vic.Write(0xD011, 0x1B); // DEN set,25 rows; leave the border closed normally.
        vic.Write(0xD016, 8);
        vic.Write(0xD018, 0x14);
        vic.Write(0xD020, 0);
        vic.Write(0xD021, 6);
        vic.Write(0xD02A, 2); // Sprite3 wins priority over sprite4.
        vic.Write(0xD02B, 5);

        bool activeRow = false, borderClosed = false;
        int frameCycles = model.RasterLines * model.CyclesPerLine;
        for (int cycle = 0; cycle < frameCycles; cycle++)
        {
            VicTiming timing = vic.GetTiming();
            if (timing.RasterLine == spriteY + 2 && timing.CycleInLine == 25)
            {
                VicPipelineState pipeline = vic.GetPipelineState();
                activeRow = pipeline.Sprite3DmaActive && pipeline.Sprite3LineVisible &&
                    pipeline.Sprite3LineDataValid && pipeline.Sprite3LineDisplayRow == 1;
                borderClosed = pipeline.VerticalBorderActive;
            }
            vic.Tick();
        }

        int redPixels = frame.CompletedPixels.Count(pixel => pixel == 0xFF68372Bu);
        return (vic.Read(0xD01E), redPixels, activeRow, borderClosed);
    }
#endif
}
