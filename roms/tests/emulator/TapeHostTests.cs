// SPDX-License-Identifier: Apache-2.0
// Public host cassette API checks. These are deliberately reported separately
// from TapeSerialRc3Tests, which execute real ROM waveforms through the CIA.
using C64Emulator.Core;
using System.Text;

static class TapeHostTests
{
    public static void Run(C64Model model, Action<string, bool, string, string> check)
    {
#if TAPE_SERIAL_RC3_PATCH
        string directory = Path.Combine(Path.GetTempPath(), "free-c64-tape-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = C64AccuracyOptions.Compatibility;
        options.EnableLoadHack = options.EnableKernalIecHooks = false;
        using var system = new C64System(model, options);
        system.LocalAudioEnabled = false;
        try
        {
            // Independent TAP v1 fixture: three short-encoded pulses and one
            // explicit 24-bit duration. No production encoder builds this input.
            byte[] fixture = new byte[27];
            Encoding.ASCII.GetBytes("C64-TAPE-RAW").CopyTo(fixture, 0);
            fixture[12] = 1;
            fixture[14] = model.IsNtsc ? (byte)1 : (byte)0;
            fixture[16] = 7;
            new byte[] {48,66,86,0,0xF0,0x0A,0}.CopyTo(fixture,20);
            var malformed = new List<byte[]>();
            foreach (var change in new[] {(At:0, Value:(byte)0), (At:12,Value:(byte)3), (At:13,Value:(byte)1),
                (At:14,Value:(byte)4), (At:15,Value:(byte)1), (At:16,Value:(byte)8)})
            {
                byte[] bad = (byte[])fixture.Clone(); bad[change.At] = change.Value; malformed.Add(bad);
            }
            byte[] zeroPulse = (byte[])fixture.Clone(); Array.Clear(zeroPulse,24,3); malformed.Add(zeroPulse);
            malformed.Add(fixture[..^1]);
            int rejected = 0;
            foreach (byte[] bad in malformed)
            {
                try { TapImage.Parse(bad); }
                catch (InvalidDataException) { rejected++; }
            }
            check("tape_host_malformed_headers", rejected == malformed.Count,
                "Wrong signature/version/platform/standard/reserved field/length and invalid extended pulses reject cleanly",
                $"rejected={rejected}/{malformed.Count}");

            // A physical interval keeps its duration when a tape recorded on
            // one standard is played on the other; the cycle counts change.
            byte[] otherStandard = (byte[])fixture.Clone(); otherStandard[14] = model.IsNtsc ? (byte)0 : (byte)1;
            var transport = new TapeDeck(); transport.ConfigureClock(model.CpuHz);
            transport.Mount(otherStandard); transport.Play(); transport.WriteProcessorPort(0x2F,0x17);
            int expectedCycles = (int)Math.Round(384.0 * (model.IsNtsc ? 1022727.0 : 985248.0) /
                (model.IsNtsc ? 985248.0 : 1022727.0));
            for (int i=0;i<expectedCycles-1;i++) transport.Tick();
            bool notEarly = transport.Position == 0;
            transport.Tick();
            check("tape_host_cross_standard_clock", notEarly && transport.Position == 1 && !transport.ReadHigh,
                "TAP source PAL/NTSC clock converts to the selected machine clock without changing pulse time",
                $"source={otherStandard[14]}, target={model.VideoStandard}, firstEdgeCycles={expectedCycles}");
            string source = Path.Combine(directory, "source.tap");
            File.WriteAllBytes(source, fixture);
            string result = system.MountMedia(source);
            check("tape_host_mount", result.Contains("MOUNTED") && system.Tape.IsPlaying &&
                system.Tape.PulseCount == 4 && system.MountedMedia.Kind == MountedMediaKind.Tape &&
                system.MountedMedia.HostPath == source && system.Tape.ExportTap().SequenceEqual(fixture),
                "Mount a valid TAP through the normal media API, press PLAY and retain exact pulses", result);

            string invalid = Path.Combine(directory, "invalid.tap");
            File.WriteAllBytes(invalid, fixture[..^1]);
            result = system.MountMedia(invalid);
            check("tape_host_invalid_mount_atomic", result.Contains("FAILED") && system.Tape.IsPlaying &&
                system.MountedMedia.HostPath == source && system.Tape.ExportTap().SequenceEqual(fixture),
                "A truncated TAP is rejected without changing the mounted tape or metadata", result);

            string saved = Path.Combine(directory, "saved.tap");
            File.WriteAllText(saved, "old target");
            result = system.SaveTape(saved);
            check("tape_host_save_exact", result.StartsWith("TAPE SAVED") && File.ReadAllBytes(saved).SequenceEqual(fixture) &&
                system.Tape.IsPlaying && system.MountedMedia.HostPath == saved,
                "Saving atomically replaces the chosen file with exact TAP bytes and preserves transport", result);

            string blocked = Path.Combine(directory, "blocked");
            Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "keep.txt"), "keep");
            result = system.SaveTape(blocked);
            check("tape_host_failed_save_preserves", result.StartsWith("TAPE SAVE FAILED") &&
                File.ReadAllText(Path.Combine(blocked, "keep.txt")) == "keep" &&
                system.Tape.ExportTap().SequenceEqual(fixture) && system.MountedMedia.HostPath == saved &&
                !Directory.EnumerateFiles(directory, ".c64-tape-*.tmp").Any(),
                "Failed file replacement leaves destination and in-memory tape intact and removes temporary output", result);

            system.Tape.WriteProcessorPort(0x2F, 0x17);
            for (int cycle = 0; cycle < 800; cycle++) system.Tape.Tick();
            int position = system.Tape.Position;
            system.Reset();
            bool cold = system.MountedMedia.Kind == MountedMediaKind.Tape && system.MountedMedia.HostPath == saved &&
                system.Tape.Position == position && system.Tape.ExportTap().SequenceEqual(fixture);
            result = system.ResetAndReloadMedia();
            bool reload = system.MountedMedia.HostPath == saved && system.Tape.Position == position &&
                system.Tape.ExportTap().SequenceEqual(fixture);
            system.WarmReset();
            check("tape_host_resets_preserve_cassette", position > 0 && cold && reload &&
                system.MountedMedia.HostPath == saved && system.Tape.Position == position,
                "Cold, warm and reload resets retain mounted cassette bytes, head position and metadata",
                $"position={position}, cold={cold}, reload={reload}, {result}");

            system.StopTape();
            bool stopped = !system.Tape.IsPlaying && !system.Tape.IsRecording;
            system.RewindTape();
            bool rewound = system.Tape.Position == 0;
            result = system.PlayTape();
            check("tape_host_transport_controls", stopped && rewound && system.Tape.IsPlaying && result == "TAPE PLAY",
                "STOP releases sense, REWIND returns to the first pulse and PLAY resumes", result);

            system.BeginTapeRecording();
            check("tape_host_recording_clock", system.Tape.IsRecording && system.Tape.PulseCount == 0 &&
                system.Tape.VideoStandard == (model.IsNtsc ? 1 : 0) && system.MountedMedia.Kind == MountedMediaKind.Tape,
                "A new recording captures the selected PAL/NTSC standard and replaces the old transport",
                $"video={system.Tape.VideoStandard}, recording={system.Tape.IsRecording}");
            system.Reset();
            bool recordingRetained = system.Tape.IsRecording && system.MountedMedia.Kind == MountedMediaKind.Tape;
            system.Tape.WriteProcessorPort(0x2F, 0x17);
            system.Tape.WriteProcessorPort(0x2F, 0x1F);
            for (int cycle = 0; cycle < 512; cycle++) system.Tape.Tick();
            system.Tape.WriteProcessorPort(0x2F, 0x17);
            system.Tape.WriteProcessorPort(0x2F, 0x1F);
            var snapshot = system.CreateTapeSnapshot();
            using (var replacement = new C64System(model, options))
            {
                replacement.LocalAudioEnabled = false;
                replacement.InsertTapeSnapshot(snapshot, system.MountedMedia);
                bool copy = replacement.Tape.IsRecording && replacement.Tape.PulseCount == 1 &&
                    replacement.Tape.RecordedPulses.SequenceEqual(new[] {512}) &&
                    replacement.MountedMedia.Kind == MountedMediaKind.Tape;
                system.Tape.Eject();
                check("tape_host_power_cycle_preserves_recording", copy && replacement.Tape.PulseCount == 1,
                    "Frontend power-cycle transfer preserves unsaved pulses, RECORD state and metadata as an independent copy",
                    $"copied={copy}, remaining={replacement.Tape.PulseCount}");
            }
            system.EjectMedia();
            check("tape_host_eject_and_empty_save", recordingRetained && system.Tape.PulseCount == 0 &&
                !system.Tape.IsPlaying && !system.Tape.IsRecording && system.Tape.Position == 0 &&
                system.MountedMedia.Kind == MountedMediaKind.None && system.SaveTape(saved) == "NO TAPE PULSES TO SAVE" &&
                File.ReadAllBytes(saved).SequenceEqual(fixture),
                "Reset retains an unsaved recording; explicit eject clears the tape; saving an empty tape preserves old output",
                $"recordingRetained={recordingRetained}, kind={system.MountedMedia.Kind}");

            var externalPort = system.UserPort;
            int callbacks = 0;
            Action<UserPort> onTick = _ => callbacks++;
            Action<UserPort> onOutput = _ => { };
            externalPort.Ticked = onTick; externalPort.OutputChanged = onOutput;
            externalPort.SetSerialInputs(true,true,true,true);
            byte pins = externalPort.InputPins;
            system.MountMedia(source);
            using var state = new MemoryStream();
            using (var writer = new BinaryWriter(state, Encoding.UTF8, true)) system.SaveState(writer);
            externalPort.SetSerialInputs(false,false,false,false);
            system.Tape.Eject();
            state.Position = 0;
            using (var reader = new BinaryReader(state, Encoding.UTF8, true)) system.LoadState(reader);
            system.Tick();
            check("tape_host_peripheral_savestate", ReferenceEquals(externalPort,system.UserPort) &&
                externalPort.Ticked == onTick && externalPort.OutputChanged == onOutput && callbacks == 1 &&
                externalPort.InputPins == pins && system.Tape.IsPlaying && system.Tape.ExportTap().SequenceEqual(fixture) &&
                system.MountedMedia.HostPath == source,
                "Savestate restores tape and serial pins while preserving the attached UserPort object and live callbacks",
                $"samePort={ReferenceEquals(externalPort,system.UserPort)}, callbacks={callbacks}, pins={externalPort.InputPins:X2}");
        }
        finally { Directory.Delete(directory, true); }
#else
        check("tape_host_patch_required", false, "The RC3 TAP host patch is required", "missing patch");
#endif
    }
}
