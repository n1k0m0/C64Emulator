/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using C64Emulator.Network;

namespace C64Emulator.Core
{
    /// <summary>
    /// Runs deterministic headless checks for emulator accuracy-sensitive subsystems.
    /// </summary>
    public static class AccuracyTestRunner
    {
        private sealed class AccuracyContext
        {
            private readonly List<string> _failures = new List<string>();

            public void Equal<T>(string label, T expected, T actual)
            {
                if (!EqualityComparer<T>.Default.Equals(expected, actual))
                {
                    _failures.Add(string.Format("{0}: expected {1}, got {2}", label, expected, actual));
                }
            }

            public void True(string label, bool condition)
            {
                if (!condition)
                {
                    _failures.Add(label);
                }
            }

            public int FailureCount
            {
                get { return _failures.Count; }
            }

            public IEnumerable<string> Failures
            {
                get { return _failures; }
            }
        }

        private sealed class SerializerShortReadonlyArrayState
        {
            private readonly byte[] _values = { 0x11, 0x22, 0x33 };

            public byte[] Values
            {
                get { return _values; }
            }
        }

        private sealed class SerializerLongReadonlyArrayState
        {
            private readonly byte[] _values = { 0x41, 0x42, 0x43, 0x44, 0x45 };

            public byte[] Values
            {
                get { return _values; }
            }
        }

        /// <summary>
        /// Runs all built-in accuracy checks.
        /// </summary>
        public static int Run(TextWriter output)
        {
            output.WriteLine("C64 ACCURACY TESTS");
            output.WriteLine("Scope=internal timing smoke tests plus external golden-suite infrastructure.");

            // These checks are deliberately small and deterministic.  They guard
            // the timing contracts that caused visible VIC-II regressions without
            // replacing the heavier screenshot/golden workflow.
            int failures = 0;
            failures += RunCase(output, "Accuracy profile disables emulator shortcuts", TestAccuracyProfileDisablesShortcuts);
            failures += RunCase(output, "KERNAL LOAD relocates secondary address zero", TestKernalLoadRelocatesSecondaryAddressZero);
            failures += RunCase(output, "CPU bus prediction is side-effect free", TestCpuBusPredictionIsSideEffectFree);
            failures += RunCase(output, "CPU microcycle table matches traced accesses", TestCpuMicrocycleTableMatchesTrace);
            failures += RunCase(output, "VIC frame timing", TestVicFrameTiming);
            failures += RunCase(output, "VIC raster IRQ compare is cycle driven", TestVicRasterIrqCompareIsCycleDriven);
            failures += RunCase(output, "VIC sprite DMA starts at Y-compare cycle", TestVicSpriteDmaStartsAtYCompareCycle);
            failures += RunCase(output, "VIC early sprite DMA can restart on final row", TestVicEarlySpriteDmaCanRestartOnFinalRow);
            failures += RunCase(output, "VIC sprite collisions happen outside active display", TestVicSpriteCollisionOutsideActiveDisplay);
            failures += RunCase(output, "VIC bus-plan golden slots", TestVicBusPlanGoldenSlots);
            failures += RunCase(output, "VIC badline pipeline gates c-accesses", TestVicBadlinePipelineGatesCAccesses);
            failures += RunCase(output, "VIC badline state survives later D011 changes", TestVicBadlineStateSurvivesLaterD011Changes);
            failures += RunCase(output, "VIC badline is not created after compare cycle", TestVicBadlineIsNotCreatedAfterCompareCycle);
            failures += RunCase(output, "VIC DMA delay starts late badline from idle", TestVicDmaDelayStartsLateBadlineFromIdle);
            failures += RunCase(output, "VIC display sequencer tracks VMLI token", TestVicDisplaySequencerTracksVmliToken);
            failures += RunCase(output, "VIC FLI can create consecutive badlines", TestVicFliCanCreateConsecutiveBadlines);
            failures += RunCase(output, "VIC register writes are phased through render registers", TestVicRegisterWritesUseRenderRegisterPhase);
            failures += RunCase(output, "VIC background color resolves after graphics delay", TestVicBackgroundColorResolvesAfterGraphicsDelay);
            failures += RunCase(output, "VIC midline D016 scroll affects current display source", TestVicMidlineD016ScrollAffectsCurrentDisplaySource);
            failures += RunCase(output, "VIC midline D016 CSEL affects horizontal border", TestVicMidlineD016CselAffectsHorizontalBorder);
            failures += RunCase(output, "VIC midline D011 RSEL affects vertical border", TestVicMidlineD011RselAffectsVerticalBorder);
            failures += RunCase(output, "VIC late D011 RSEL closes bottom border", TestVicLateD011RselClosesBottomBorder);
            failures += RunCase(output, "VIC unused register bits read high", TestVicUnusedRegisterBitsReadHigh);
            failures += RunCase(output, "VIC invalid display modes render black", TestVicInvalidDisplayModesRenderBlack);
            failures += RunCase(output, "VIC invalid display modes keep hidden foreground", TestVicInvalidDisplayModesKeepHiddenForeground);
            failures += RunCase(output, "VIC matrix fetch does not latch display mode", TestVicMatrixFetchDoesNotLatchDisplayMode);
            failures += RunCase(output, "CIA timer A continuous/one-shot timing", TestCiaTimerATiming);
            failures += RunCase(output, "CIA timer A latch-zero one-shot exposes terminal zero", TestCiaTimerALatchZeroOneShotTerminalZero);
            failures += RunCase(output, "CIA running timer A force-load phase", TestCiaRunningTimerAForceLoadPhase);
            failures += RunCase(output, "CIA1/CIA2 timer force-load parity", TestCiaTimerForceLoadParity);
            failures += RunCase(output, "CIA timer B counts timer A underflows", TestCiaTimerBCountsTimerA);
            failures += RunCase(output, "CIA TOD PAL tenth increment", TestCiaTodTenthIncrement);
            failures += RunCase(output, "State serializer tolerates resized readonly arrays", TestStateSerializerToleratesResizedReadonlyArrays);
            failures += RunCase(output, "EasyFlash bank and flash program sequence", TestEasyFlashBankAndProgramSequence);
            failures += RunCase(output, "EasyFlash ROM writes update underlying RAM", TestEasyFlashRomWritesUpdateUnderlyingRam);
            failures += RunCase(output, "REU registers and basic DMA", TestReuRegistersAndBasicDma);
            failures += RunCase(output, "REU swap verify and fixed addressing", TestReuSwapVerifyAndFixedAddressing);
            failures += RunCase(output, "REU size wrapping and FF00 trigger", TestReuSizeWrappingAndFf00Trigger);
            failures += RunCase(output, "SID envelope gate attack/release", TestSidEnvelopeGateAttackRelease);
            failures += RunCase(output, "SID envelope attack rate counter", TestSidEnvelopeAttackRateCounter);
            failures += RunCase(output, "SID envelope release exponential counter", TestSidEnvelopeReleaseExponentialCounter);
            failures += RunCase(output, "SID voice 3 oscillator/test readback", TestSidVoice3OscillatorAndTestReadback);
            failures += RunCase(output, "SID functional state save/restore", TestSidFunctionalStateSaveRestore);
            failures += RunCase(output, "Relay listener dispose is idempotent", TestRelayListenerDisposeIsIdempotent);
            failures += RunCase(output, "Overlay font includes password mask glyph", TestOverlayFontIncludesPasswordMaskGlyph);
            failures += RunCase(output, "1541 transport mode toggles", TestDriveTransportToggle);
            failures += RunCase(output, "1541 accuracy scheduler runs drive CPU continuously", TestDriveAccuracySchedulerRunsContinuously);
            failures += RunCase(output, "1541 custom handoff primes serial VIA outputs", TestDriveCustomHandoffPrimesSerialViaOutputs);
            failures += RunCase(output, "1541 DOTC final loader table matches VICE handoff", TestDriveDotcFinalLoaderTableMatchesViceHandoff);
            failures += RunCase(output, "1541 serial VIA inputs ignore own direct outputs", TestDriveSerialViaInputsIgnoreOwnDirectOutputs);
            failures += RunCase(output, "1541 execute-buffer job enters ROM IRQ path", TestDriveExecuteBufferJobEntersRomIrqPath);
            failures += RunCase(output, "1541 disk mount primes ROM disk context", TestDriveMountPrimesRomDiskContext);
            failures += RunCase(output, "1541 DOS job primes ROM disk context", TestDriveDosJobPrimesRomDiskContext);
            failures += RunCase(output, "1541 sequential load primes ROM disk context", TestDriveSequentialLoadPrimesRomDiskContext);
            failures += RunCase(output, "1541 IEC chunk load primes ROM disk context", TestDriveChunkLoadPrimesRomDiskContext);
            failures += RunCase(output, "1541 final sequential load advances ROM disk context", TestDriveFinalSequentialLoadAdvancesRomDiskContext);
            failures += RunCase(output, "1541 DOS context syncs GCR head position", TestDriveDosContextSyncsGcrHeadPosition);
            failures += RunCase(output, "1541 head changes preserve disk rotation phase", TestDriveHeadChangesPreserveDiskRotationPhase);
            failures += RunCase(output, "D64 GCR tracks use synthetic track skew", TestD64GcrTracksUseSyntheticTrackSkew);
            failures += RunCase(output, "1541 disk byte-ready is not PCR gated", TestDriveDiskByteReadyIsNotPcrGated);
            failures += RunCase(output, "1541 disk swap preserves custom drive code", TestDriveDiskSwapPreservesCustomCode);
            failures += RunCase(output, "1541 disk set auto-opens companion side", TestDriveDiskSetAutoOpensCompanionSide);
            failures += RunCase(output, "1541 stepper moves only while motor is on", TestDriveStepperMovesOnlyWhileMotorOn);

            output.WriteLine("Result: " + (failures == 0 ? "OK" : "FAILED"));
            output.WriteLine("Failures=" + failures);
            return failures;
        }

        private static int RunCase(TextWriter output, string name, Action<AccuracyContext> test)
        {
            var context = new AccuracyContext();
            try
            {
                test(context);
            }
            catch (Exception ex)
            {
                context.True(name + " threw " + ex.GetType().Name + ": " + ex.Message, false);
            }

            if (context.FailureCount == 0)
            {
                output.WriteLine("PASS " + name);
                return 0;
            }

            output.WriteLine("FAIL " + name);
            foreach (string failure in context.Failures)
            {
                output.WriteLine("  " + failure);
            }

            return context.FailureCount;
        }

        private static void TestAccuracyProfileDisablesShortcuts(AccuracyContext context)
        {
            using (var system = new C64System(C64Model.Pal, C64AccuracyOptions.Accuracy))
            {
                C64AccuracyOptions options = system.AccuracyOptions;
                context.True("LOAD hack is disabled", !system.EnableLoadHack);
                context.True("KERNAL IEC hooks are disabled", !system.EnableKernalIecHooks);
                context.True("software IEC transport is disabled", !system.ForceSoftwareIecTransport);
                context.True("host input injection is disabled", !system.EnableInputInjection);
                context.True("drive CPU runs continuously", options.RunDriveCpuContinuously);
            }
        }

        private static void TestKernalLoadRelocatesSecondaryAddressZero(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            bus.WriteRam(0x002B, 0x01);
            bus.WriteRam(0x002C, 0x08);

            byte[] programBytes = { 0x00, 0x20, 0x11, 0x22, 0x33 };
            ushort loadAddress;
            ushort endAddress;
            bool loaded = PrgLoader.TryLoadIntoMemory(bus, programBytes, 0x0801, out loadAddress, out endAddress);

            context.True("relocated load succeeds", loaded);
            context.Equal("relocated load address", (ushort)0x0801, loadAddress);
            context.Equal("relocated end address", (ushort)0x0804, endAddress);
            context.Equal("first relocated byte", (byte)0x11, bus.ReadRam(0x0801));
            context.Equal("second relocated byte", (byte)0x22, bus.ReadRam(0x0802));
            context.Equal("file load address untouched", (byte)0x00, bus.ReadRam(0x2000));
            context.Equal("BASIC variables pointer low", (byte)0x04, bus.ReadRam(0x002D));
            context.Equal("BASIC variables pointer high", (byte)0x08, bus.ReadRam(0x002E));
        }

        private static void TestCpuBusPredictionIsSideEffectFree(AccuracyContext context)
        {
            var harness = new CpuTraceHarness();
            const ushort startAddress = 0x0200;
            harness.Reset(startAddress);
            harness.Cpu.A = 0x55;
            harness.LoadProgram(startAddress, 0x8D, 0x20, 0xD0);

            byte busValueBeforePrediction = harness.Bus.LastCpuBusValue;
            CpuBusAccessPrediction fetch = harness.Cpu.PredictNextCycleAccess();
            context.Equal("predict opcode fetch type", CpuTraceAccessType.OpcodeFetch, fetch.AccessType);
            context.Equal("predict opcode fetch address", startAddress, fetch.Address);
            context.Equal("predict opcode fetch value", (byte)0x8D, fetch.Value);
            context.Equal("prediction does not advance PC", startAddress, harness.Cpu.PC);
            context.Equal("prediction does not update CPU bus latch", busValueBeforePrediction, harness.Bus.LastCpuBusValue);

            harness.Cpu.Tick();
            CpuBusAccessPrediction low = harness.Cpu.PredictNextCycleAccess();
            context.Equal("predict absolute low operand", (ushort)0x0201, low.Address);
            context.Equal("predict absolute low value", (byte)0x20, low.Value);

            harness.Cpu.Tick();
            CpuBusAccessPrediction high = harness.Cpu.PredictNextCycleAccess();
            context.Equal("predict absolute high operand", (ushort)0x0202, high.Address);
            context.Equal("predict absolute high value", (byte)0xD0, high.Value);

            harness.Cpu.Tick();
            CpuBusAccessPrediction write = harness.Cpu.PredictNextCycleAccess();
            context.Equal("predict absolute write type", CpuTraceAccessType.Write, write.AccessType);
            context.Equal("predict absolute write address", (ushort)0xD020, write.Address);
            context.Equal("predict absolute write value", (byte)0x55, write.Value);
        }

        private static void TestCpuMicrocycleTableMatchesTrace(AccuracyContext context)
        {
            AssertMicrocyclePredictionMatchesTrace(context, "STA abs", 0x0801, 0x8D, 0x00, 0x20, harness => harness.Cpu.A = 0x55);
            AssertMicrocyclePredictionMatchesTrace(context, "STA (zp),Y", 0x0801, 0x91, 0x20, 0xEA, harness =>
            {
                harness.Cpu.A = 0x42;
                harness.Cpu.Y = 0x03;
                harness.Bus.WriteRam(0x0020, 0x00);
                harness.Bus.WriteRam(0x0021, 0x30);
            });
            AssertMicrocyclePredictionMatchesTrace(context, "PHA", 0x0801, 0x48, 0xEA, 0xEA, harness => harness.Cpu.A = 0x7E);
            AssertMicrocyclePredictionMatchesTrace(context, "INC abs", 0x0801, 0xEE, 0x00, 0x20, harness => harness.Bus.WriteRam(0x2000, 0x10));
            AssertMicrocyclePredictionMatchesTrace(context, "JSR abs", 0x0801, 0x20, 0x00, 0x09, null);
            AssertMicrocyclePredictionMatchesTrace(context, "LDA abs,X page cross", 0x08FE, 0xBD, 0xFF, 0x20, harness =>
            {
                harness.Cpu.X = 0x01;
                harness.Bus.WriteRam(0x2100, 0x5A);
            });
            AssertMicrocyclePredictionMatchesTrace(context, "LDA zp,X", 0x0801, 0xB5, 0x20, 0xEA, harness =>
            {
                harness.Cpu.X = 0x04;
                harness.Bus.WriteRam(0x0024, 0x77);
            });
            AssertMicrocyclePredictionMatchesTrace(context, "STA zp,X", 0x0801, 0x95, 0x20, 0xEA, harness =>
            {
                harness.Cpu.A = 0x66;
                harness.Cpu.X = 0x04;
            });
            AssertMicrocyclePredictionMatchesTrace(context, "INC zp,X", 0x0801, 0xF6, 0x20, 0xEA, harness =>
            {
                harness.Cpu.X = 0x04;
                harness.Bus.WriteRam(0x0024, 0x10);
            });
            AssertMicrocyclePredictionMatchesTrace(context, "XAA immediate", 0x0801, 0x8B, 0x7F, 0xEA, harness =>
            {
                harness.Cpu.A = 0xFF;
                harness.Cpu.X = 0x33;
            });
        }

        private static void AssertMicrocyclePredictionMatchesTrace(
            AccuracyContext context,
            string label,
            ushort startAddress,
            byte opcode,
            byte operandLow,
            byte operandHigh,
            Action<CpuTraceHarness> configure)
        {
            var harness = new CpuTraceHarness();
            harness.Reset(startAddress);
            harness.LoadProgram(startAddress, opcode, operandLow, operandHigh, 0xEA, 0xEA);
            if (configure != null)
            {
                configure(harness);
            }

            var recorder = new CpuTraceRecorder();
            recorder.Attach(harness.Cpu);
            harness.Cpu.TraceEnabled = true;
            try
            {
                // Compare the predicted external bus access immediately before each
                // CPU tick against the trace emitted by the tick.  This catches
                // predictor drift while also proving the prediction path did not
                // advance CPU state on its own.
                bool enteredExecution = false;
                int previousTraceCount = 0;
                for (int cycle = 0; cycle < 16; cycle++)
                {
                    CpuBusAccessPrediction prediction = harness.Cpu.PredictNextCycleAccess();
                    bool usedMicrocycle = harness.Cpu.LastPredictionUsedMicrocycle;
                    harness.Cpu.Tick();

                    context.True(label + " cycle " + cycle + " used microcycle prediction", usedMicrocycle);
                    context.True(label + " emitted trace entry for cycle " + cycle, recorder.Entries.Count == previousTraceCount + 1);
                    CpuTraceEntry entry = recorder.Entries[previousTraceCount];
                    previousTraceCount = recorder.Entries.Count;

                    context.Equal(label + " access type cycle " + cycle, entry.AccessType, prediction.AccessType);
                    context.Equal(label + " address cycle " + cycle, entry.Address, prediction.Address);
                    context.Equal(label + " value cycle " + cycle, entry.Value, prediction.Value);

                    if (harness.Cpu.State == CpuState.ExecuteInstruction || harness.Cpu.State == CpuState.InterruptSequence)
                    {
                        enteredExecution = true;
                    }

                    if (enteredExecution && harness.Cpu.State == CpuState.FetchOpcode)
                    {
                        break;
                    }
                }
            }
            finally
            {
                harness.Cpu.TraceEnabled = false;
                recorder.Detach(harness.Cpu);
            }
        }

        private static void TestVicFrameTiming(AccuracyContext context)
        {
            using (var system = new C64System(C64Model.Pal))
            {
                VicTiming start = system.Timing;
                context.Equal("initial raster line", 0, start.RasterLine);
                context.Equal("initial cycle", 0, start.CycleInLine);
                context.Equal("initial global cycle", 0L, start.GlobalCycle);

                system.RunCycles(C64Model.Pal.CyclesPerLine);
                VicTiming nextLine = system.Timing;
                context.Equal("line after one PAL raster line", 1, nextLine.RasterLine);
                context.Equal("cycle after one PAL raster line", 0, nextLine.CycleInLine);
                context.Equal("global after one PAL raster line", 63L, nextLine.GlobalCycle);

                int remainingFrameCycles = (C64Model.Pal.RasterLines - 1) * C64Model.Pal.CyclesPerLine;
                system.RunCycles(remainingFrameCycles);
                VicTiming frame = system.Timing;
                context.Equal("line after one PAL frame", 0, frame.RasterLine);
                context.Equal("cycle after one PAL frame", 0, frame.CycleInLine);
                context.Equal("global after one PAL frame", 19656L, frame.GlobalCycle);
            }
        }

        private static void TestVicRasterIrqCompareIsCycleDriven(AccuracyContext context)
        {
            var bus = new SystemBus();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x1A, 0x01);
            vic.Write(0x12, 0x00);
            context.True("D012 write alone does not assert raster IRQ", !vic.IsIrqAsserted());

            vic.Tick();
            context.True("line 0 cycle 0 compare has not fired yet", !vic.IsIrqAsserted());
            vic.Tick();
            context.True("line 0 cycle 1 compare asserts raster IRQ", vic.IsIrqAsserted());

            var immediateBus = new SystemBus();
            var immediateFrameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var immediateVic = new Vic2(immediateBus, immediateFrameBuffer, C64Model.Pal);
            for (int cycle = 0; cycle < (10 * C64Model.Pal.CyclesPerLine) + 5; cycle++)
            {
                immediateVic.Tick();
            }

            immediateVic.Write(0x1A, 0x01);
            immediateVic.Write(0x12, 0x0A);
            context.True("D012 write matching current nonzero line asserts immediately", immediateVic.IsIrqAsserted());
        }

        private static void TestVicSpriteDmaStartsAtYCompareCycle(AccuracyContext context)
        {
            var bus = new SystemBus();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x15, 0x01);
            vic.Write(0x01, 0x00);

            for (int cycle = 1; cycle < 55; cycle++)
            {
                vic.PrepareCycle();
                if (cycle == 54)
                {
                    context.True("sprite DMA is not requested before Y compare", !vic.HasBusRequestPendingThisCycle());
                }

                vic.FinishCycle();
            }

            vic.PrepareCycle();
            context.True("sprite DMA request begins at cycle 55", vic.HasBusRequestPendingThisCycle());
            vic.FinishCycle();

            vic.Tick();
            vic.Tick();

            vic.PrepareCycle();
            context.True("sprite 0 DMA blocks CPU at cycle 58", vic.RequiresBusThisCycle());
            vic.FinishCycle();
        }

        private static void TestVicEarlySpriteDmaCanRestartOnFinalRow(AccuracyContext context)
        {
            var bus = new SystemBus();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            bool[] latched = GetPrivateArray<bool>(vic, "_spriteDmaLatched");
            int[] latchedY = GetPrivateArray<int>(vic, "_spriteLatchedY");

            vic.Write(0x15, 0x08);
            vic.Write(0x06, 0x64);
            vic.Write(0x07, 0x0A);

            RunVicUntil(vic, bus, 0x0A, 54);
            RunVicCycleWithoutCpu(vic, bus);
            context.True("sprite 3 latches on the first Y compare", latched[3]);
            context.Equal("sprite 3 first latched Y", 0x0A, latchedY[3]);

            RunVicUntil(vic, bus, 0x1F, 15);
            context.True("sprite 3 stays latched until its final early fetches complete", latched[3]);
            RunVicCycleWithoutCpu(vic, bus);
            context.True("sprite 3 releases before the final-row Y compare", !latched[3]);

            vic.Write(0x07, 0x1F);
            RunVicUntil(vic, bus, 0x1F, 54);
            context.True("sprite 3 is still released before the second Y compare", !latched[3]);
            RunVicCycleWithoutCpu(vic, bus);
            context.True("sprite 3 re-latches on the same raster line", latched[3]);
            context.Equal("sprite 3 second latched Y", 0x1F, latchedY[3]);
        }

        private static void TestVicSpriteCollisionOutsideActiveDisplay(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            const ushort screenBase = 0xC400;
            const byte spritePointer0 = 0x30;
            const byte spritePointer1 = 0x31;
            bus.WriteRam((ushort)(screenBase + 0x03F8), spritePointer0);
            bus.WriteRam((ushort)(screenBase + 0x03F9), spritePointer1);

            ushort spriteBase0 = (ushort)(bus.GetVicBankBase() + (spritePointer0 * 64));
            ushort spriteBase1 = (ushort)(bus.GetVicBankBase() + (spritePointer1 * 64));
            for (int offset = 0; offset < 63; offset++)
            {
                bus.WriteRam((ushort)(spriteBase0 + offset), 0xFF);
                bus.WriteRam((ushort)(spriteBase1 + offset), 0xFF);
            }

            // X=0 keeps the whole 24-pixel sprite left of the normal 40-column
            // display window, but the VIC-II still latches sprite-sprite hits.
            vic.Write(0x00, 0x00);
            vic.Write(0x01, 0x32);
            vic.Write(0x02, 0x00);
            vic.Write(0x03, 0x32);
            vic.Write(0x15, 0x03);

            int totalCycles = 80 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            context.Equal("overlapping sprites collide left of the active display", (byte)0x03, vic.Read(0x1E));
        }

        private static void TestVicBusPlanGoldenSlots(AccuracyContext context)
        {
            var plan = new VicBusPlan();
            plan.BuildLine(false, null);

            // Slot indexes are zero-based, while VIC documentation is usually
            // written in one-based cycle numbers.  The assertion labels keep the
            // hardware-facing cycle names to make failures easier to compare with
            // timing diagrams.
            context.Equal("cycle 11 phi1 refresh", VicBusAction.Refresh, plan.GetSlot(10).Phi1Action);
            context.Equal("cycle 15 phi1 refresh", VicBusAction.Refresh, plan.GetSlot(14).Phi1Action);
            context.Equal("cycle 16 phi1 char fetch", VicBusAction.CharFetch, plan.GetSlot(15).Phi1Action);
            context.Equal("cycle 55 phi1 char fetch", VicBusAction.CharFetch, plan.GetSlot(54).Phi1Action);
            context.Equal("cycle 58 sprite 0 pointer", VicBusAction.SpritePointerFetch, plan.GetSlot(57).Phi1Action);
            context.Equal("cycle 58 sprite index", 0, plan.GetSlot(57).SpriteIndex);
            context.True("inactive sprite data does not block CPU", !plan.GetSlot(57).BlocksCpu);

            var sprites = new bool[8];
            sprites[0] = true;
            plan.BuildLine(false, sprites);
            context.Equal("sprite 0 data phi2 starts at cycle 58", VicBusAction.SpriteDataFetch, plan.GetSlot(57).Phi2Action);
            context.True("sprite 0 data blocks CPU at cycle 58", plan.GetSlot(57).BlocksCpu);
            context.Equal("sprite 0 second data phi1 at cycle 59", VicBusAction.SpriteDataFetch, plan.GetSlot(58).Phi1Action);
            context.Equal("sprite 0 third data phi2 at cycle 59", VicBusAction.SpriteDataFetch, plan.GetSlot(58).Phi2Action);
            context.True("sprite BA pending cycle 55", plan.GetSlot(54).BusRequestPending);
            context.True("sprite BA pending cycle 56", plan.GetSlot(55).BusRequestPending);
            context.True("sprite BA pending cycle 57", plan.GetSlot(56).BusRequestPending);

            plan.BuildLine(true, null);
            context.True("badline BA pending cycle 12", plan.GetSlot(11).BusRequestPending);
            context.True("badline BA pending cycle 14", plan.GetSlot(13).BusRequestPending);
            context.True("badline cycle 14 does not block CPU", !plan.GetSlot(13).BlocksCpu);
            context.Equal("badline cycle 15 keeps refresh on phi1", VicBusAction.Refresh, plan.GetSlot(14).Phi1Action);
            context.Equal("badline cycle 15 matrix fetch on phi2", VicBusAction.MatrixFetch, plan.GetSlot(14).Phi2Action);
            context.True("badline cycle 15 blocks CPU", plan.GetSlot(14).BlocksCpu);
            context.Equal("badline cycle 16 keeps char fetch on phi1", VicBusAction.CharFetch, plan.GetSlot(15).Phi1Action);
            context.Equal("badline cycle 16 matrix fetch on phi2", VicBusAction.MatrixFetch, plan.GetSlot(15).Phi2Action);
            context.Equal("badline cycle 54 matrix fetch on phi2", VicBusAction.MatrixFetch, plan.GetSlot(53).Phi2Action);
            context.Equal("badline cycle 55 keeps char fetch only", VicBusAction.CharFetch, plan.GetSlot(54).Phi1Action);
            context.Equal("badline cycle 55 has no matrix fetch", VicBusAction.Idle, plan.GetSlot(54).Phi2Action);

            sprites = new bool[8];
            sprites[3] = true;
            plan.BuildLine(false, sprites);
            context.True("sprite 3 BA pending wraps to cycle 61", plan.GetSlot(60).BusRequestPending);
            context.True("sprite 3 BA pending wraps to cycle 62", plan.GetSlot(61).BusRequestPending);
            context.True("sprite 3 BA pending wraps to cycle 63", plan.GetSlot(62).BusRequestPending);
            context.Equal("sprite 3 pointer fetch at cycle 1 phi1", VicBusAction.SpritePointerFetch, plan.GetSlot(0).Phi1Action);
            context.Equal("sprite 3 data fetch at cycle 1 phi2", VicBusAction.SpriteDataFetch, plan.GetSlot(0).Phi2Action);
            context.True("sprite 3 data blocks CPU at cycle 1", plan.GetSlot(0).BlocksCpu);
            context.Equal("sprite 3 second data fetch at cycle 2 phi1", VicBusAction.SpriteDataFetch, plan.GetSlot(1).Phi1Action);
            context.Equal("sprite 3 third data fetch at cycle 2 phi2", VicBusAction.SpriteDataFetch, plan.GetSlot(1).Phi2Action);

            sprites = new bool[8];
            sprites[0] = true;
            plan.BuildLine(true, sprites);
            context.Equal("badline plus sprite 0 keeps matrix fetch through cycle 54", VicBusAction.MatrixFetch, plan.GetSlot(53).Phi2Action);
            context.True("badline plus sprite 0 keeps sprite BA pending at cycle 55", plan.GetSlot(54).BusRequestPending);
            context.Equal("badline plus sprite 0 keeps char fetch at cycle 55 phi1", VicBusAction.CharFetch, plan.GetSlot(54).Phi1Action);
            context.Equal("badline plus sprite 0 has no matrix fetch at cycle 55 phi2", VicBusAction.Idle, plan.GetSlot(54).Phi2Action);
            context.Equal("badline plus sprite 0 pointer at cycle 58 phi1", VicBusAction.SpritePointerFetch, plan.GetSlot(57).Phi1Action);
            context.Equal("badline plus sprite 0 data at cycle 58 phi2", VicBusAction.SpriteDataFetch, plan.GetSlot(57).Phi2Action);
        }

        private static void TestVicBadlinePipelineGatesCAccesses(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            for (int cycle = 0; cycle < (0x33 * C64Model.Pal.CyclesPerLine) + 13; cycle++)
            {
                vic.Tick();
            }

            vic.PrepareCycle();
            VicPipelineState cycle14 = vic.GetPipelineState();
            context.True("badline detected on matching raster", vic.GetTiming().BadLine);
            context.True("graphics display state starts on badline", cycle14.GraphicsDisplayState);
            context.True("matrix fetch sequence starts on badline", cycle14.MatrixFetchStartedThisLine);
            context.Equal("badline request start cycle", 12, cycle14.MatrixFetchRequestStartCycle);
            context.Equal("badline c-access start cycle", 15, cycle14.MatrixFetchStartCycle);
            context.Equal("badline CPU block start cycle", 15, cycle14.MatrixFetchCpuBlockStartCycle);
            context.True("cycle 14 keeps BA pending", vic.HasBusRequestPendingThisCycle());
            context.True("cycle 14 does not block CPU yet", !vic.RequiresBusThisCycle());
            vic.FinishCycle();

            vic.PrepareCycle();
            context.Equal("cycle 15 uses matrix fetch", VicBusAction.MatrixFetch, vic.GetTiming().Phi2Action);
            context.True("cycle 15 blocks CPU", vic.RequiresBusThisCycle());
            vic.FinishCycle();
        }

        private static void TestVicBadlineStateSurvivesLaterD011Changes(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            for (int cycle = 0; cycle < (0x33 * C64Model.Pal.CyclesPerLine) + 13; cycle++)
            {
                vic.Tick();
            }

            vic.PrepareCycle();
            context.True("cycle 14 badline state is latched", vic.GetTiming().BadLine);
            vic.FinishCycle();

            vic.Write(0x11, 0x1A);
            vic.PrepareCycle();
            context.True("cycle 15 remains badline state after yscroll mismatch", vic.GetTiming().BadLine);
            context.Equal("cycle 15 still uses matrix fetch", VicBusAction.MatrixFetch, vic.GetTiming().Phi2Action);
            context.True("cycle 15 still blocks CPU", vic.RequiresBusThisCycle());
            vic.FinishCycle();
        }

        private static void TestVicBadlineIsNotCreatedAfterCompareCycle(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            // Keep DEN set so the display is enabled, but choose a Y-scroll value
            // that does not match raster $33 during the VIC's badline compare.
            vic.Write(0x11, 0x1A);
            for (int cycle = 0; cycle < (0x33 * C64Model.Pal.CyclesPerLine) + 13; cycle++)
            {
                vic.Tick();
            }

            vic.PrepareCycle();
            context.True("cycle 14 is not a badline before the Y-scroll write", !vic.GetTiming().BadLine);
            vic.FinishCycle();

            vic.Write(0x11, 0x1B);
            vic.PrepareCycle();
            context.True("cycle 15 does not create a late badline", !vic.GetTiming().BadLine);
            context.Equal("cycle 15 keeps normal phi2 access", VicBusAction.Idle, vic.GetTiming().Phi2Action);
            context.True("cycle 15 does not block CPU", !vic.RequiresBusThisCycle());
            vic.FinishCycle();
        }

        private static void TestVicDmaDelayStartsLateBadlineFromIdle(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            // Keep DEN set in line $30 but avoid normal badlines until raster $33.
            // Switching YSCROLL after the cycle-14 compare then creates the classic
            // DMA-delay condition: c-accesses start late while AEC needs three
            // cycles before the VIC owns the bus.
            vic.Write(0x11, 0x1C);
            for (int cycle = 0; cycle < (0x33 * C64Model.Pal.CyclesPerLine) + 13; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            vic.PrepareCycle();
            context.True("cycle 14 starts idle before DMA delay", !vic.GetTiming().BadLine);
            context.True("cycle 14 display state is idle", !vic.GetPipelineState().GraphicsDisplayState);
            FinishPreparedVicCycleWithoutCpu(vic, bus);

            vic.Write(0x11, 0x1B);

            vic.PrepareCycle();
            VicPipelineState cycle15 = vic.GetPipelineState();
            context.True("cycle 15 starts late badline", vic.GetTiming().BadLine);
            context.True("cycle 15 switches to display state", cycle15.GraphicsDisplayState);
            context.True("cycle 15 starts matrix fetch sequence", cycle15.MatrixFetchStartedThisLine);
            context.Equal("DMA-delay request start cycle", 15, cycle15.MatrixFetchRequestStartCycle);
            context.Equal("DMA-delay c-access start cycle", 15, cycle15.MatrixFetchStartCycle);
            context.Equal("DMA-delay CPU block start cycle", 18, cycle15.MatrixFetchCpuBlockStartCycle);
            context.Equal("cycle 15 tries matrix fetch", VicBusAction.MatrixFetch, vic.GetTiming().Phi2Action);
            context.True("cycle 15 has BA low before AEC", vic.HasBusRequestPendingThisCycle());
            context.True("cycle 15 does not block CPU yet", !vic.RequiresBusThisCycle());
            FinishPreparedVicCycleWithoutCpu(vic, bus);

            vic.PrepareCycle();
            context.Equal("cycle 16 keeps matrix fetch", VicBusAction.MatrixFetch, vic.GetTiming().Phi2Action);
            context.True("cycle 16 still waits for AEC", vic.HasBusRequestPendingThisCycle());
            context.True("cycle 16 does not block CPU yet", !vic.RequiresBusThisCycle());
            FinishPreparedVicCycleWithoutCpu(vic, bus);

            vic.PrepareCycle();
            context.Equal("cycle 17 keeps matrix fetch", VicBusAction.MatrixFetch, vic.GetTiming().Phi2Action);
            context.True("cycle 17 still waits for AEC", vic.HasBusRequestPendingThisCycle());
            context.True("cycle 17 does not block CPU yet", !vic.RequiresBusThisCycle());
            FinishPreparedVicCycleWithoutCpu(vic, bus);

            vic.PrepareCycle();
            context.Equal("cycle 18 keeps matrix fetch", VicBusAction.MatrixFetch, vic.GetTiming().Phi2Action);
            context.True("cycle 18 blocks CPU after AEC delay", vic.RequiresBusThisCycle());
            FinishPreparedVicCycleWithoutCpu(vic, bus);
        }

        private static void TestVicDisplaySequencerTracksVmliToken(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            for (int cycle = 0; cycle < (0x33 * C64Model.Pal.CyclesPerLine) + 15; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            vic.PrepareCycle();
            VicPipelineState cycle16 = vic.GetPipelineState();
            context.Equal("cycle 16 VMLI token enters column 0", 0x0000000001UL, cycle16.GraphicsVmliShiftRegister);
            context.Equal("cycle 16 pattern fetch advanced to column 1", 1, cycle16.GraphicsPatternFetchColumn);
            FinishPreparedVicCycleWithoutCpu(vic, bus);

            vic.PrepareCycle();
            VicPipelineState cycle17 = vic.GetPipelineState();
            context.Equal("cycle 17 VMLI token shifts to column 1", 0x0000000002UL, cycle17.GraphicsVmliShiftRegister);
            context.Equal("cycle 17 pattern fetch advanced to column 2", 2, cycle17.GraphicsPatternFetchColumn);
            FinishPreparedVicCycleWithoutCpu(vic, bus);
        }

        private static void TestVicFliCanCreateConsecutiveBadlines(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, 0x1B);
            for (int cycle = 0; cycle < (0x33 * C64Model.Pal.CyclesPerLine) + 14; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            context.True("first display line entered display state", vic.GetPipelineState().GraphicsDisplayState);
            vic.Write(0x11, 0x1C);

            for (int cycle = 14; cycle < C64Model.Pal.CyclesPerLine + 13; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            vic.PrepareCycle();
            VicPipelineState fliLine = vic.GetPipelineState();
            context.True("next line cycle 14 creates another badline", vic.GetTiming().BadLine);
            context.True("FLI badline starts matrix fetch", fliLine.MatrixFetchStartedThisLine);
            context.Equal("FLI request start cycle", 12, fliLine.MatrixFetchRequestStartCycle);
            context.Equal("FLI c-access start cycle", 15, fliLine.MatrixFetchStartCycle);
            context.Equal("FLI CPU block start cycle", 15, fliLine.MatrixFetchCpuBlockStartCycle);
            context.Equal("FLI row counter reset", 0, fliLine.GraphicsRc);
            FinishPreparedVicCycleWithoutCpu(vic, bus);
        }

        private static void TestVicRegisterWritesUseRenderRegisterPhase(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            const int cropLeft = 50;
            const int cropTop = 14;
            const int targetRaster = 20;
            const int targetCycle = 10;
            for (int cycle = 0; cycle < (targetRaster * C64Model.Pal.CyclesPerLine) + targetCycle; cycle++)
            {
                vic.Tick();
            }

            vic.PrepareCycle();
            VicTiming timing = vic.GetTiming();
            int frameX = timing.BeamX - cropLeft;
            int frameY = timing.RasterLine - cropTop;
            vic.Write(0x20, 0x02);
            vic.FinishCycle();

            context.Equal("first rendered dot sees phased border color", 0xFF68372Bu, frameBuffer.Pixels[(frameY * frameBuffer.Width) + frameX]);
            context.Equal("last rendered dot keeps phased border color", 0xFF68372Bu, frameBuffer.Pixels[(frameY * frameBuffer.Width) + frameX + 7]);
        }

        private static void TestVicBackgroundColorResolvesAfterGraphicsDelay(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x21, 0x06);
            RunVicUntil(vic, bus, 60, 20);

            vic.PrepareCycle();
            VicTiming timing = vic.GetTiming();
            int cropLeft = ((C64Model.Pal.CyclesPerLine * 8) - C64Model.Pal.VisibleWidth) / 2;
            int cropTop = (C64Model.Pal.RasterLines - C64Model.Pal.VisibleHeight) / 2;
            int frameX = timing.BeamX - cropLeft;
            int frameY = timing.RasterLine - cropTop;
            vic.Write(0x21, 0x00);
            FinishPreparedVicCycleWithoutCpu(vic, bus);

            context.Equal(
                "delayed background pixel sees the output-phase D021 value",
                0xFF000000u,
                frameBuffer.Pixels[(frameY * frameBuffer.Width) + frameX]);
        }

        private static void TestVicMidlineD016ScrollAffectsCurrentDisplaySource(AccuracyContext context)
        {
            string stableFrameHash = RenderD016SplitProbeFrame(false);
            string splitFrameHash = RenderD016SplitProbeFrame(true);

            context.True(
                "midline D016 write must alter the rendered framebuffer",
                !string.Equals(stableFrameHash, splitFrameHash, StringComparison.Ordinal));
        }

        private static void TestVicMidlineD016CselAffectsHorizontalBorder(AccuracyContext context)
        {
            string stableNarrowHash = RenderD016CselBorderProbeFrame(false);
            string widenedMidlineHash = RenderD016CselBorderProbeFrame(true);

            context.True(
                "midline CSEL write must alter horizontal border timing",
                !string.Equals(stableNarrowHash, widenedMidlineHash, StringComparison.Ordinal));
        }

        private static void TestVicMidlineD011RselAffectsVerticalBorder(AccuracyContext context)
        {
            string stableNarrowHash = RenderD011RselBorderProbeFrame(false);
            string widenedMidframeHash = RenderD011RselBorderProbeFrame(true);

            context.True(
                "midline RSEL write must alter vertical border timing",
                !string.Equals(stableNarrowHash, widenedMidframeHash, StringComparison.Ordinal));
        }

        private static void TestVicLateD011RselClosesBottomBorder(AccuracyContext context)
        {
            uint normalBottomRowPixel = RenderD011LateRselBottomBorderProbePixel(false);
            uint closedBottomRowPixel = RenderD011LateRselBottomBorderProbePixel(true);

            context.Equal("normal 25-row bottom line remains visible", 0xFFFFFFFFu, normalBottomRowPixel);
            context.Equal("late 24-row RSEL pulse closes the bottom border", 0xFF68372Bu, closedBottomRowPixel);
        }

        private static void TestVicInvalidDisplayModesRenderBlack(AccuracyContext context)
        {
            context.Equal("ECM plus multicolor text blanks graphics", 0xFF000000u, RenderInvalidModeProbePixel(0x5B, 0x18));
            context.Equal("ECM plus bitmap blanks graphics", 0xFF000000u, RenderInvalidModeProbePixel(0x7B, 0x08));
            context.Equal("ECM plus bitmap plus multicolor blanks graphics", 0xFF000000u, RenderInvalidModeProbePixel(0x7B, 0x18));
        }

        private static void TestVicUnusedRegisterBitsReadHigh(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x16, 0x00);
            context.Equal("D016 unconnected bits read as one", (byte)0xC0, vic.Read(0x16));

            vic.Write(0x18, 0xFE);
            context.Equal("D018 unconnected bit zero reads as one", (byte)0xFF, vic.Read(0x18));

            context.Equal("D019 unused latch bits read as one", (byte)0x70, vic.Read(0x19));

            vic.Write(0x1A, 0x05);
            context.Equal("D01A unused enable bits read as one", (byte)0xF5, vic.Read(0x1A));

            vic.Write(0x20, 0x05);
            context.Equal("D020 high nibble reads as one", (byte)0xF5, vic.Read(0x20));

            vic.Write(0x27, 0x0A);
            context.Equal("sprite color high nibble reads as one", (byte)0xFA, vic.Read(0x27));
        }

        private static void TestVicInvalidDisplayModesKeepHiddenForeground(AccuracyContext context)
        {
            context.Equal(
                "invalid ECM+MCM text foreground still collides with sprite",
                (byte)0x01,
                RenderInvalidModeSpriteDataCollision(0x5B, 0x18, 0x09));
            context.Equal(
                "invalid ECM+BMM bitmap foreground still collides with sprite",
                (byte)0x01,
                RenderInvalidModeSpriteDataCollision(0x7B, 0x08, 0x01));
            context.Equal(
                "invalid ECM+BMM+MCM bitmap foreground still collides with sprite",
                (byte)0x01,
                RenderInvalidModeSpriteDataCollision(0x7B, 0x18, 0x01));
        }

        private static void TestVicMatrixFetchDoesNotLatchDisplayMode(AccuracyContext context)
        {
            context.Equal(
                "screen matrix fetched during illegal ECM+MCM is decoded after ECM clears",
                0xFFFFFFFFu,
                RenderMatrixModeSwitchProbePixel());
        }

        private static string RenderD016SplitProbeFrame(bool writeMidlineScroll)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, 0x1B);
            vic.Write(0x16, 0x08);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x00);
            vic.Write(0x21, 0x00);

            const ushort screenBase = 0xC400;
            const ushort characterBase = 0xE000;
            // Build a one-character solid-font scene with alternating color RAM.
            // A midline X-scroll change should shift the current display source,
            // so a plain frame hash is enough to catch a phase regression.
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, (byte)((column & 1) == 0 ? 0x01 : 0x02));
                }
            }

            for (int row = 0; row < 8; row++)
            {
                bus.WriteRam((ushort)(characterBase + row), 0xFF);
            }

            int totalCycles = 56 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                VicTiming timing = vic.GetTiming();
                if (writeMidlineScroll && timing.RasterLine == 52 && timing.CycleInLine == 25)
                {
                    vic.Write(0x16, 0x0F);
                }

                RunVicCycleWithoutCpu(vic, bus);
            }

            return DevTraceExporter.ComputeFrameHash(frameBuffer);
        }

        private static uint RenderInvalidModeProbePixel(byte d011, byte d016)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, d011);
            vic.Write(0x16, d016);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x06);
            vic.Write(0x21, 0x06);

            const ushort screenBase = 0xC400;
            const ushort characterBase = 0xE000;
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, 0x01);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                bus.WriteRam((ushort)(characterBase + row), 0xFF);
            }

            int totalCycles = 70 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            return frameBuffer.Pixels[(45 * frameBuffer.Width) + 80];
        }

        private static byte RenderInvalidModeSpriteDataCollision(byte d011, byte d016, byte colorNibble)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, d011);
            vic.Write(0x16, d016);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x06);
            vic.Write(0x21, 0x06);

            const ushort screenBase = 0xC400;
            const ushort patternBase = 0xE000;
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, colorNibble);
                }
            }

            for (int offset = 0; offset < 0x2000; offset++)
            {
                bus.WriteRam((ushort)(patternBase + offset), 0xFF);
            }

            const byte spritePointer = 0x30;
            bus.WriteRam((ushort)(screenBase + 0x03F8), spritePointer);
            ushort spriteBase = (ushort)(bus.GetVicBankBase() + (spritePointer * 64));
            for (int offset = 0; offset < 63; offset++)
            {
                bus.WriteRam((ushort)(spriteBase + offset), 0xFF);
            }

            vic.Write(0x00, 0x3F);
            vic.Write(0x01, 0x3A);
            vic.Write(0x15, 0x01);
            vic.Write(0x27, 0x01);

            int totalCycles = 80 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            return vic.Read(0x1F);
        }

        private static uint RenderMatrixModeSwitchProbePixel()
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, 0x5B);
            vic.Write(0x16, 0x18);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x00);
            vic.Write(0x21, 0x00);

            const ushort screenBase = 0xC400;
            const ushort characterBase = 0xE000;
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, 0x01);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                bus.WriteRam((ushort)(characterBase + row), 0xFF);
            }

            int switchCycle = (0x34 * C64Model.Pal.CyclesPerLine) + 2;
            for (int cycle = 0; cycle < switchCycle; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            vic.Write(0x11, 0x1B);

            int totalCycles = 70 * C64Model.Pal.CyclesPerLine;
            for (int cycle = switchCycle; cycle < totalCycles; cycle++)
            {
                RunVicCycleWithoutCpu(vic, bus);
            }

            return frameBuffer.Pixels[(45 * frameBuffer.Width) + 80];
        }

        private static string RenderD016CselBorderProbeFrame(bool widenMidline)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, 0x1B);
            vic.Write(0x16, 0x00);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x02);
            vic.Write(0x21, 0x00);

            // CSEL changes the horizontal border compare points.  The probe keeps
            // the display content simple so any hash change comes from border timing
            // rather than unrelated character data.
            const ushort screenBase = 0xC400;
            const ushort characterBase = 0xE000;
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, 0x01);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                bus.WriteRam((ushort)(characterBase + row), 0xFF);
            }

            int totalCycles = 56 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                VicTiming timing = vic.GetTiming();
                if (widenMidline && timing.RasterLine == 52 && timing.CycleInLine == 5)
                {
                    vic.Write(0x16, 0x08);
                }

                RunVicCycleWithoutCpu(vic, bus);
            }

            return DevTraceExporter.ComputeFrameHash(frameBuffer);
        }

        private static string RenderD011RselBorderProbeFrame(bool widenMidframe)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, 0x13);
            vic.Write(0x16, 0x08);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x02);
            vic.Write(0x21, 0x00);

            // RSEL shifts the vertical border compare rows.  Writing it midframe
            // should affect the current frame instead of waiting for the next one.
            const ushort screenBase = 0xC400;
            const ushort characterBase = 0xE000;
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, 0x01);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                bus.WriteRam((ushort)(characterBase + row), 0xFF);
            }

            int totalCycles = 58 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                VicTiming timing = vic.GetTiming();
                if (widenMidframe && timing.RasterLine == 51 && timing.CycleInLine == 5)
                {
                    vic.Write(0x11, 0x1B);
                }

                RunVicCycleWithoutCpu(vic, bus);
            }

            return DevTraceExporter.ComputeFrameHash(frameBuffer);
        }

        private static uint RenderD011LateRselBottomBorderProbePixel(bool closeEarly)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var frameBuffer = new FrameBuffer(C64Model.Pal.VisibleWidth, C64Model.Pal.VisibleHeight);
            var vic = new Vic2(bus, frameBuffer, C64Model.Pal);

            vic.Write(0x11, 0x1B);
            vic.Write(0x16, 0x08);
            vic.Write(0x18, 0x18);
            vic.Write(0x20, 0x02);
            vic.Write(0x21, 0x00);

            const ushort screenBase = 0xC400;
            const ushort characterBase = 0xE000;
            for (int row = 0; row < 25; row++)
            {
                for (int column = 0; column < 40; column++)
                {
                    int matrixIndex = (row * 40) + column;
                    bus.WriteRam((ushort)(screenBase + matrixIndex), 0x00);
                    bus.WriteColorRam((ushort)matrixIndex, 0x01);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                bus.WriteRam((ushort)(characterBase + row), 0xFF);
            }

            int totalCycles = 248 * C64Model.Pal.CyclesPerLine;
            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                VicTiming timing = vic.GetTiming();
                if (closeEarly && timing.RasterLine == 246 && timing.CycleInLine == 58)
                {
                    vic.Write(0x11, 0x13);
                }
                else if (closeEarly && timing.RasterLine == 246 && timing.CycleInLine == 62)
                {
                    vic.Write(0x11, 0x1B);
                }

                RunVicCycleWithoutCpu(vic, bus);
            }

            int cropTop = (C64Model.Pal.RasterLines - C64Model.Pal.VisibleHeight) / 2;
            int frameY = 247 - cropTop;
            int frameX = 80;
            return frameBuffer.Pixels[(frameY * frameBuffer.Width) + frameX];
        }

        private static void RunVicCycleWithoutCpu(Vic2 vic, SystemBus bus)
        {
            vic.PrepareCycle();
            FinishPreparedVicCycleWithoutCpu(vic, bus);
        }

        private static void RunVicUntil(Vic2 vic, SystemBus bus, int rasterLine, int cycleInLine)
        {
            int maxCycles = C64Model.Pal.RasterLines * C64Model.Pal.CyclesPerLine * 2;
            for (int cycle = 0; cycle < maxCycles; cycle++)
            {
                VicTiming timing = vic.GetTiming();
                if (timing.RasterLine == rasterLine && timing.CycleInLine == cycleInLine)
                {
                    return;
                }

                RunVicCycleWithoutCpu(vic, bus);
            }

            throw new InvalidOperationException("Target VIC timing position was not reached.");
        }

        private static void FinishPreparedVicCycleWithoutCpu(Vic2 vic, SystemBus bus)
        {
            bool blocksCpu = vic.RequiresBusThisCycle();
            bool requestPending = vic.HasBusRequestPendingThisCycle();
            // The probes drive the VIC directly, but the chip still observes the
            // same BA/AEC bus state that C64System would derive for a real CPU
            // cycle.  Keeping that handshake here makes fixture hashes comparable
            // with full-machine runs.
            bus.SetPhi2BusState(
                requestPending || blocksCpu,
                blocksCpu,
                !requestPending && !blocksCpu,
                blocksCpu);
            vic.FinishCycle();
        }

        private static T[] GetPrivateArray<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException("Missing private field " + fieldName + ".");
            }

            var value = field.GetValue(target) as T[];
            if (value == null)
            {
                throw new InvalidOperationException("Private field " + fieldName + " is not the expected array type.");
            }

            return value;
        }

        private static int GetPrivateInt(object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException("Missing private field " + fieldName + ".");
            }

            object value = field.GetValue(target);
            if (!(value is int))
            {
                throw new InvalidOperationException("Private field " + fieldName + " is not an int.");
            }

            return (int)value;
        }

        private static int GetDriveMechanismBitIndex(IecDrive1541 drive)
        {
            var field = typeof(Drive1541Bus).GetField(
                "_mechanism",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException("Missing drive mechanism field.");
            }

            var mechanism = field.GetValue(drive.Hardware.Bus) as Drive1541Mechanism;
            if (mechanism == null)
            {
                throw new InvalidOperationException("Drive mechanism field has an unexpected value.");
            }

            return GetPrivateInt(mechanism, "_trackBitIndex");
        }

        private static void TestCiaTimerATiming(AccuracyContext context)
        {
            var cia = new Cia1();
            cia.Write(0x04, 0x02);
            cia.Write(0x05, 0x00);
            cia.Write(0x0D, 0x81);
            cia.Write(0x0E, 0x01);
            cia.Tick();
            context.True("timer A IRQ is quiet before terminal count", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer A IRQ remains quiet during start delay", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer A IRQ is quiet at visible terminal count", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer A IRQ is asserted at terminal count", cia.IsIrqAsserted());
            context.Equal("timer A ICR bit", 0x81, cia.Read(0x0D));
            context.True("timer A ICR read clears IRQ", !cia.IsIrqAsserted());

            cia.Reset();
            cia.Write(0x04, 0x02);
            cia.Write(0x05, 0x00);
            cia.Write(0x0E, 0x09);
            cia.Tick();
            cia.Tick();
            cia.Tick();
            cia.Tick();
            context.Equal("timer A one-shot exposes terminal zero", 0x00, cia.Read(0x04));
            context.Equal("timer A one-shot remains running at terminal zero", 0x09, (byte)(cia.Read(0x0E) & 0x09));
            cia.Tick();
            context.Equal("timer A one-shot stops", 0x08, cia.Read(0x0E));
        }

        private static void TestCiaTimerALatchZeroOneShotTerminalZero(AccuracyContext context)
        {
            var cia = new Cia1();
            cia.Write(0x04, 0x08);
            cia.Write(0x05, 0x00);
            cia.Write(0x0E, 0x01);
            cia.Tick();
            cia.Tick();
            cia.Write(0x04, 0x00);
            cia.Write(0x05, 0x00);
            cia.Write(0x0E, 0x09);

            bool sawTerminalZero = false;
            for (int tick = 0; tick < 32; tick++)
            {
                if (cia.Read(0x04) == 0x00 && cia.Read(0x05) == 0x00)
                {
                    sawTerminalZero = true;
                    break;
                }

                cia.Tick();
            }

            context.True("timer A latch-zero one-shot exposes terminal zero", sawTerminalZero);
            cia.Tick();
            cia.Tick();
            context.Equal("timer A latch-zero one-shot stops after terminal zero", 0x08, (byte)(cia.Read(0x0E) & 0x09));
            context.Equal("timer A latch-zero one-shot stop low", 0x00, cia.Read(0x04));
            context.Equal("timer A latch-zero one-shot stop high", 0x00, cia.Read(0x05));
        }

        private static void TestCiaTimerForceLoadParity(AccuracyContext context)
        {
            var cia1 = new Cia1();
            var cia2 = new Cia2();

            cia1.Write(0x04, 0x00);
            cia1.Write(0x05, 0x00);
            cia1.Write(0x0E, 0x10);
            cia2.Write(0x04, 0x00);
            cia2.Write(0x05, 0x00);
            cia2.Write(0x0E, 0x10);

            context.Equal("CIA1 force-load zero low", 0x00, cia1.Read(0x04));
            context.Equal("CIA1 force-load zero high", 0x00, cia1.Read(0x05));
            context.Equal("CIA2 force-load zero low", 0x00, cia2.Read(0x04));
            context.Equal("CIA2 force-load zero high", 0x00, cia2.Read(0x05));

            cia1.Write(0x0D, 0x81);
            cia1.Write(0x0E, 0x01);
            cia2.Write(0x0D, 0x81);
            cia2.Write(0x0E, 0x01);
            cia1.Tick();
            cia2.Tick();
            context.True("CIA1 latch-zero timer does not underflow immediately after start", !cia1.IsIrqAsserted());
            context.True("CIA2 latch-zero timer does not underflow immediately after start", !cia2.IsNmiAsserted());
            context.Equal("CIA1 latch-zero first tick low", 0x00, cia1.Read(0x04));
            context.Equal("CIA2 latch-zero first tick low", 0x00, cia2.Read(0x04));
        }

        private static void TestCiaRunningTimerAForceLoadPhase(AccuracyContext context)
        {
            var newCia = new Cia1(CiaChipRevision.Mos6526A);
            StartTimerA(newCia);
            newCia.Write(0x04, 0xFF);
            newCia.Write(0x05, 0x00);
            newCia.Write(0x0E, 0xD5);
            context.Equal("6526A running force-load is visible immediately", 0xFF, newCia.Read(0x04));
            newCia.Tick();
            context.Equal("6526A running force-load counts on next tick", 0xFE, newCia.Read(0x04));

            var oldCia = new Cia1(CiaChipRevision.Mos6526);
            StartTimerA(oldCia);
            oldCia.Write(0x04, 0xFF);
            oldCia.Write(0x05, 0x00);
            oldCia.Write(0x0E, 0xD5);
            context.Equal("6526 running force-load write consumes first phase", 0xFE, oldCia.Read(0x04));
            oldCia.Tick();
            context.Equal("6526 running force-load continues from consumed phase", 0xFD, oldCia.Read(0x04));

            var nearNewCia = new Cia1(CiaChipRevision.Mos6526A);
            StartNearTerminalTimerA(nearNewCia);
            nearNewCia.Write(0x04, 0xFF);
            nearNewCia.Write(0x05, 0x00);
            nearNewCia.Write(0x0E, 0xD5);
            nearNewCia.Tick();
            nearNewCia.Tick();
            nearNewCia.Tick();
            context.Equal("6526A near-terminal running force-load keeps transfer phase", 0xFF, nearNewCia.Read(0x04));
            nearNewCia.Tick();
            context.Equal("6526A near-terminal running force-load resumes after transfer phase", 0xFE, nearNewCia.Read(0x04));
        }

        private static void TestCiaTimerBCountsTimerA(AccuracyContext context)
        {
            var cia = new Cia1();
            cia.Write(0x04, 0x01);
            cia.Write(0x05, 0x00);
            cia.Write(0x06, 0x02);
            cia.Write(0x07, 0x00);
            cia.Write(0x0D, 0x82);
            cia.Write(0x0F, 0x41);
            cia.Write(0x0E, 0x01);

            cia.Tick();
            cia.Tick();
            context.True("timer B quiet before first timer A underflow", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer B quiet after first timer A underflow", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer B quiet while timer A reload is held", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer B IRQ after second timer A underflow", cia.IsIrqAsserted());
            byte icr = cia.Read(0x0D);
            context.True("timer B ICR bit set", (icr & 0x82) == 0x82);

            cia.Reset();
            cia.Write(0x04, 0x01);
            cia.Write(0x05, 0x00);
            cia.Write(0x06, 0x01);
            cia.Write(0x07, 0x00);
            cia.Write(0x0D, 0x82);
            cia.Write(0x0F, 0x49);
            cia.Write(0x0E, 0x01);

            cia.Tick();
            cia.Tick();
            cia.Tick();
            context.True("one-shot timer B stays quiet at terminal zero", !cia.IsIrqAsserted());
            context.Equal("one-shot timer B exposes terminal zero", 0x00, cia.Read(0x06));
            cia.Tick();
            cia.Tick();
            context.True("one-shot timer B IRQ after terminal zero pulse", cia.IsIrqAsserted());
        }

        private static void StartTimerA(Cia1 cia)
        {
            cia.Write(0x04, 0x00);
            cia.Write(0x05, 0x20);
            cia.Write(0x0E, 0x01);
            cia.Tick();
            cia.Tick();
            cia.Tick();
        }

        private static void StartNearTerminalTimerA(Cia1 cia)
        {
            cia.Write(0x04, 0x08);
            cia.Write(0x05, 0x00);
            cia.Write(0x0E, 0x01);
            cia.Tick();
            cia.Tick();
            cia.Tick();
        }

        private static void TestCiaTodTenthIncrement(AccuracyContext context)
        {
            var cia = new Cia1();
            for (int cycle = 0; cycle < 98524; cycle++)
            {
                cia.Tick();
            }

            context.Equal("TOD remains at zero before PAL tenth", 0x00, cia.Read(0x08));
            cia.Tick();
            context.Equal("TOD increments at PAL tenth", 0x01, cia.Read(0x08));
        }

        private static void TestStateSerializerToleratesResizedReadonlyArrays(AccuracyContext context)
        {
            using (var stream = new MemoryStream())
            {
                var longState = new SerializerLongReadonlyArrayState();
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    StateSerializer.WriteObjectFields(writer, longState);
                }

                stream.Position = 0;
                var shortTarget = new SerializerShortReadonlyArrayState();
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                {
                    StateSerializer.ReadObjectFields(reader, shortTarget);
                }

                context.Equal("long source copies first byte", (byte)0x41, shortTarget.Values[0]);
                context.Equal("long source copies second byte", (byte)0x42, shortTarget.Values[1]);
                context.Equal("long source copies third byte", (byte)0x43, shortTarget.Values[2]);
            }

            using (var stream = new MemoryStream())
            {
                var shortState = new SerializerShortReadonlyArrayState();
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    StateSerializer.WriteObjectFields(writer, shortState);
                }

                stream.Position = 0;
                var longTarget = new SerializerLongReadonlyArrayState();
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                {
                    StateSerializer.ReadObjectFields(reader, longTarget);
                }

                context.Equal("short source copies first byte", (byte)0x11, longTarget.Values[0]);
                context.Equal("short source copies second byte", (byte)0x22, longTarget.Values[1]);
                context.Equal("short source copies third byte", (byte)0x33, longTarget.Values[2]);
                context.Equal("short source keeps fourth default", (byte)0x44, longTarget.Values[3]);
                context.Equal("short source keeps fifth default", (byte)0x45, longTarget.Values[4]);
            }
        }

        private static void TestEasyFlashBankAndProgramSequence(AccuracyContext context)
        {
            var cartridge = EasyFlashCartridge.CreateBlank("TEST EASYFLASH");
            const byte ProcessorPortDefault = 0x37;

            cartridge.TryWriteIo(0xDE02, 0x06);
            cartridge.TryWriteIo(0xDE00, 0x02);
            context.True("first unlock write consumed", cartridge.TryWrite(0x9555, ProcessorPortDefault, 0xAA));

            cartridge.TryWriteIo(0xDE00, 0x01);
            context.True("second unlock write consumed", cartridge.TryWrite(0x8AAA, ProcessorPortDefault, 0x55));

            cartridge.TryWriteIo(0xDE00, 0x02);
            context.True("program command consumed", cartridge.TryWrite(0x9555, ProcessorPortDefault, 0xA0));

            cartridge.TryWriteIo(0xDE00, 0x00);
            context.True("target program write consumed", cartridge.TryWrite(0x8000, ProcessorPortDefault, 0x42));

            byte programmed;
            context.True("programmed byte readable", cartridge.TryRead(0x8000, ProcessorPortDefault, out programmed));
            context.Equal("programmed byte value", (byte)0x42, programmed);
            context.True("programming marks cartridge dirty", cartridge.IsDirty);

            cartridge.TryWriteIo(0xDE00, 0x01);
            byte otherBank;
            context.True("other bank remains readable", cartridge.TryRead(0x8000, ProcessorPortDefault, out otherBank));
            context.Equal("other bank remains erased", (byte)0xFF, otherBank);

            cartridge.TryWriteIo(0xDE02, 0x04);
            byte hidden;
            context.True("off mode hides ROML", !cartridge.TryRead(0x8000, ProcessorPortDefault, out hidden));

            cartridge.TryWriteIo(0xDE02, 0x07);
            cartridge.TryWriteIo(0xDE00, 0x02);
            context.True("ROMH first unlock write consumed", cartridge.TryWrite(0xB555, ProcessorPortDefault, 0xAA));

            cartridge.TryWriteIo(0xDE00, 0x01);
            context.True("ROMH second unlock write consumed", cartridge.TryWrite(0xAAAA, ProcessorPortDefault, 0x55));

            cartridge.TryWriteIo(0xDE00, 0x02);
            context.True("ROMH program command consumed", cartridge.TryWrite(0xB555, ProcessorPortDefault, 0xA0));

            cartridge.TryWriteIo(0xDE00, 0x00);
            context.True("ROMH target program write consumed", cartridge.TryWrite(0xA000, ProcessorPortDefault, 0x24));

            const byte HiramOnlyPort = 0x36;
            byte romhWithLoramOff;
            context.True("HIRAM-only port reads ROMH in 16K mode", cartridge.TryRead(0xA000, HiramOnlyPort, out romhWithLoramOff));
            context.Equal("HIRAM-only ROMH value", (byte)0x24, romhWithLoramOff);

            byte romlWithLoramOff;
            context.True("LORAM off hides ROML in 16K mode", !cartridge.TryRead(0x8000, HiramOnlyPort, out romlWithLoramOff));
        }

        private static void TestEasyFlashRomWritesUpdateUnderlyingRam(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            var cartridge = EasyFlashCartridge.CreateBlank("RAM UNDER ROM");
            bus.InsertEasyFlash(cartridge);
            cartridge.TryWriteIo(0xDE02, 0x07);

            bus.CpuWrite(0x8000, 0x5A);
            bus.CpuWrite(0xA000, 0xA5);

            context.Equal("ROML write reaches RAM underneath", (byte)0x5A, bus.ReadRam(0x8000));
            context.Equal("ROMH write reaches RAM underneath", (byte)0xA5, bus.ReadRam(0xA000));

            byte visibleRom;
            context.True("ROML remains cartridge-visible for reads", bus.CpuRead(0x8000) == 0xFF);
            cartridge.TryWriteIo(0xDE02, 0x04);
            visibleRom = bus.CpuRead(0x8000);
            context.Equal("RAM underneath visible after cartridge off", (byte)0x5A, visibleRom);
        }

        private static void TestReuRegistersAndBasicDma(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            bus.ConfigureReu(true, ReuMemorySize.K512);

            context.Equal("unconnected mirrored register reads high", (byte)0xFF, bus.CpuRead(0xDF0B));
            bus.CpuWrite(0xDF22, 0x34);
            context.Equal("register mirrors through five selection lines", (byte)0x34, bus.CpuRead(0xDF02));

            bus.WriteRam(0x2000, 0x10);
            bus.WriteRam(0x2001, 0x11);
            bus.WriteRam(0x2002, 0x12);
            bus.WriteRam(0x2003, 0x13);
            ConfigureReuTransfer(bus, 0x2000, 0x000000, 4, 0x00);
            bus.CpuWrite(0xDF01, 0x90);
            RunReuDma(context, bus, 4);

            context.Equal("C64 address advanced after C64 to REU", (byte)0x04, bus.CpuRead(0xDF02));
            context.Equal("C64 address high advanced after C64 to REU", (byte)0x20, bus.CpuRead(0xDF03));
            context.Equal("length register ends at one", (byte)0x01, bus.CpuRead(0xDF07));
            byte status = bus.CpuRead(0xDF00);
            context.Equal("end-of-block status includes size bit", (byte)0x50, status);
            context.Equal("status read clears completion bits", (byte)0x10, bus.CpuRead(0xDF00));

            bus.WriteRam(0x2100, 0x00);
            bus.WriteRam(0x2101, 0x00);
            bus.WriteRam(0x2102, 0x00);
            bus.WriteRam(0x2103, 0x00);
            ConfigureReuTransfer(bus, 0x2100, 0x000000, 4, 0x00);
            bus.CpuWrite(0xDF01, 0x91);
            RunReuDma(context, bus, 4);

            context.Equal("REU to C64 byte 0", (byte)0x10, bus.ReadRam(0x2100));
            context.Equal("REU to C64 byte 1", (byte)0x11, bus.ReadRam(0x2101));
            context.Equal("REU to C64 byte 2", (byte)0x12, bus.ReadRam(0x2102));
            context.Equal("REU to C64 byte 3", (byte)0x13, bus.ReadRam(0x2103));
        }

        private static void TestReuSwapVerifyAndFixedAddressing(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            bus.ConfigureReu(true, ReuMemorySize.K512);

            bus.WriteRam(0x3000, 0xAA);
            ConfigureReuTransfer(bus, 0x3000, 0x000100, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x90);
            RunReuDma(context, bus, 1);

            bus.WriteRam(0x3000, 0x55);
            ConfigureReuTransfer(bus, 0x3000, 0x000100, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x92);
            RunReuDma(context, bus, 2);
            context.Equal("swap writes REU byte to C64", (byte)0xAA, bus.ReadRam(0x3000));

            bus.WriteRam(0x3001, 0x00);
            ConfigureReuTransfer(bus, 0x3001, 0x000100, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x91);
            RunReuDma(context, bus, 1);
            context.Equal("swap writes old C64 byte to REU", (byte)0x55, bus.ReadRam(0x3001));

            ConfigureReuTransfer(bus, 0x3001, 0x000100, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x93);
            RunReuDma(context, bus, 1);
            context.Equal("verify match sets end of block", (byte)0x50, bus.CpuRead(0xDF00));

            bus.WriteRam(0x3001, 0x56);
            ConfigureReuTransfer(bus, 0x3001, 0x000100, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x93);
            RunReuDma(context, bus, 1);
            context.Equal("verify mismatch sets fault", (byte)0x30, bus.CpuRead(0xDF00));

            bus.WriteRam(0x3100, 0x7E);
            ConfigureReuTransfer(bus, 0x3100, 0x000200, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x90);
            RunReuDma(context, bus, 1);

            bus.WriteRam(0x3200, 0x00);
            bus.WriteRam(0x3201, 0x00);
            bus.WriteRam(0x3202, 0x00);
            ConfigureReuTransfer(bus, 0x3200, 0x000200, 3, 0x40);
            bus.CpuWrite(0xDF01, 0x91);
            RunReuDma(context, bus, 3);
            context.Equal("fixed REU fills first byte", (byte)0x7E, bus.ReadRam(0x3200));
            context.Equal("fixed REU fills second byte", (byte)0x7E, bus.ReadRam(0x3201));
            context.Equal("fixed REU fills third byte", (byte)0x7E, bus.ReadRam(0x3202));
        }

        private static void TestReuSizeWrappingAndFf00Trigger(AccuracyContext context)
        {
            var bus = new SystemBus();
            bus.InitializeMemory();
            bus.ConfigureReu(true, ReuMemorySize.K128);

            bus.WriteRam(0x4000, 0x11);
            ConfigureReuTransfer(bus, 0x4000, 0x000000, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x90);
            RunReuDma(context, bus, 1);

            bus.WriteRam(0x4000, 0x22);
            ConfigureReuTransfer(bus, 0x4000, 0x020000, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x90);
            RunReuDma(context, bus, 1);

            bus.WriteRam(0x4001, 0x00);
            ConfigureReuTransfer(bus, 0x4001, 0x000000, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x91);
            RunReuDma(context, bus, 1);
            context.Equal("128 KB REU wraps bank 2 to bank 0", (byte)0x22, bus.ReadRam(0x4001));

            bus.ConfigureReu(true, ReuMemorySize.M16);
            bus.WriteRam(0x4002, 0x77);
            ConfigureReuTransfer(bus, 0x4002, 0xFF0000, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x90);
            RunReuDma(context, bus, 1);

            bus.WriteRam(0x4003, 0x00);
            ConfigureReuTransfer(bus, 0x4003, 0xFF0000, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x91);
            RunReuDma(context, bus, 1);
            context.Equal("16 MB REU stores in high bank", (byte)0x77, bus.ReadRam(0x4003));

            bus.ConfigureReu(true, ReuMemorySize.K512);
            bus.WriteRam(0xD000, 0xA6);
            bus.CpuWrite(0x0001, 0x37);
            ConfigureReuTransfer(bus, 0xD000, 0x000010, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x80);
            context.True("delayed command waits for FF00", !bus.IsReuDmaActive);
            bus.CpuWrite(0x0001, 0x30);
            bus.CpuWrite(0xFF00, bus.ReadRam(0xFF00));
            RunReuDma(context, bus, 1);

            bus.WriteRam(0xD001, 0x00);
            bus.CpuWrite(0x0001, 0x37);
            ConfigureReuTransfer(bus, 0xD001, 0x000010, 1, 0x00);
            bus.CpuWrite(0xDF01, 0x81);
            bus.CpuWrite(0x0001, 0x30);
            bus.CpuWrite(0xFF00, bus.ReadRam(0xFF00));
            RunReuDma(context, bus, 1);
            context.Equal("FF00-triggered transfer used RAM under I/O", (byte)0xA6, bus.ReadRam(0xD001));
        }

        private static void ConfigureReuTransfer(SystemBus bus, ushort c64Address, int reuAddress, ushort length, byte addressControl)
        {
            bus.CpuWrite(0xDF02, (byte)(c64Address & 0xFF));
            bus.CpuWrite(0xDF03, (byte)(c64Address >> 8));
            bus.CpuWrite(0xDF04, (byte)(reuAddress & 0xFF));
            bus.CpuWrite(0xDF05, (byte)((reuAddress >> 8) & 0xFF));
            bus.CpuWrite(0xDF06, (byte)((reuAddress >> 16) & 0xFF));
            bus.CpuWrite(0xDF07, (byte)(length & 0xFF));
            bus.CpuWrite(0xDF08, (byte)(length >> 8));
            bus.CpuWrite(0xDF0A, addressControl);
        }

        private static void RunReuDma(AccuracyContext context, SystemBus bus, int expectedCycles)
        {
            int guard = Math.Max(16, expectedCycles + 16);
            while (bus.IsReuDmaActive && guard > 0)
            {
                bus.TickReuDmaCycle();
                guard--;
            }

            context.True("REU DMA completed", !bus.IsReuDmaActive);
        }

        private static void TestSidEnvelopeGateAttackRelease(AccuracyContext context)
        {
            var sid = new Sid();
            try
            {
                sid.Write(0x13, 0x00);
                sid.Write(0x14, 0xF0);
                sid.Write(0x12, 0x01);

                for (int cycle = 0; cycle < 2200; cycle++)
                {
                    sid.Tick();
                }

                byte attackEnvelope = sid.Read(0x1C);
                context.True("voice 3 envelope rises while gate is set", attackEnvelope > 0x20);

                for (int cycle = 0; cycle < 30000; cycle++)
                {
                    sid.Tick();
                }

                byte sustainEnvelope = sid.Read(0x1C);
                context.True("voice 3 envelope reaches high sustain", sustainEnvelope >= 0xE0);

                sid.Write(0x12, 0x00);
                for (int cycle = 0; cycle < 7000; cycle++)
                {
                    sid.Tick();
                }

                byte releaseEnvelope = sid.Read(0x1C);
                context.True("voice 3 envelope falls after gate clear", releaseEnvelope < attackEnvelope);
            }
            finally
            {
                sid.Dispose();
            }
        }

        private static void TestSidEnvelopeAttackRateCounter(AccuracyContext context)
        {
            var sid = new Sid();
            try
            {
                sid.Write(0x13, 0x00);
                sid.Write(0x14, 0xF0);
                sid.Write(0x12, 0x01);

                for (int cycle = 0; cycle < 8; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("fastest attack waits for rate counter", (byte)0x00, sid.Read(0x1C));
                sid.Tick();
                context.Equal("fastest attack increments after period", (byte)0x01, sid.Read(0x1C));

                for (int cycle = 0; cycle < 8; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("attack counter holds between rate periods", (byte)0x01, sid.Read(0x1C));
                sid.Tick();
                context.Equal("attack counter advances on next period", (byte)0x02, sid.Read(0x1C));
            }
            finally
            {
                sid.Dispose();
            }
        }

        private static void TestSidEnvelopeReleaseExponentialCounter(AccuracyContext context)
        {
            var sid = new Sid();
            try
            {
                sid.Write(0x13, 0x00);
                sid.Write(0x14, 0xF0);
                sid.Write(0x12, 0x01);

                for (int cycle = 0; cycle < 9 * 255; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("attack reaches full 8-bit envelope", (byte)0xFF, sid.Read(0x1C));

                sid.Write(0x12, 0x00);
                for (int cycle = 0; cycle < (0xFF - 0x5D) * 9; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("release reaches exponential threshold", (byte)0x5D, sid.Read(0x1C));

                for (int cycle = 0; cycle < 9; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("release holds for exponential divider", (byte)0x5D, sid.Read(0x1C));

                for (int cycle = 0; cycle < 9; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("release advances after exponential divider", (byte)0x5C, sid.Read(0x1C));
            }
            finally
            {
                sid.Dispose();
            }
        }

        private static void TestSidVoice3OscillatorAndTestReadback(AccuracyContext context)
        {
            var sid = new Sid();
            try
            {
                sid.Write(0x0E, 0x00);
                sid.Write(0x0F, 0x20);
                sid.Write(0x12, 0x20);

                for (int cycle = 0; cycle < 1200; cycle++)
                {
                    sid.Tick();
                }

                byte firstOscillator = sid.Read(0x1B);
                for (int cycle = 0; cycle < 1200; cycle++)
                {
                    sid.Tick();
                }

                byte secondOscillator = sid.Read(0x1B);
                context.True("voice 3 oscillator advances while TEST is clear", firstOscillator != secondOscillator);

                sid.Write(0x12, 0x28);
                for (int cycle = 0; cycle < 32; cycle++)
                {
                    sid.Tick();
                }

                context.Equal("TEST bit forces oscillator readback low", (byte)0x00, sid.Read(0x1B));

                sid.Write(0x12, 0x20);
                for (int cycle = 0; cycle < 1200; cycle++)
                {
                    sid.Tick();
                }

                context.True("oscillator resumes after TEST is cleared", sid.Read(0x1B) != 0x00);
                context.Equal("SID paddle reads stay idle-high", (byte)0xFF, sid.Read(0x19));
                context.Equal("SID second paddle read stays idle-high", (byte)0xFF, sid.Read(0x1A));
            }
            finally
            {
                sid.Dispose();
            }
        }

        private static void TestSidFunctionalStateSaveRestore(AccuracyContext context)
        {
            var sid = new Sid();
            var restored = new Sid();
            try
            {
                sid.Write(0x0E, 0x40);
                sid.Write(0x0F, 0x18);
                sid.Write(0x13, 0x00);
                sid.Write(0x14, 0x90);
                sid.Write(0x12, 0x21);

                for (int cycle = 0; cycle < 5000; cycle++)
                {
                    sid.Tick();
                }

                byte oscillatorBeforeSave = sid.Read(0x1B);
                byte envelopeBeforeSave = sid.Read(0x1C);

                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    {
                        sid.SaveState(writer);
                    }

                    stream.Position = 0;
                    using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                    {
                        restored.LoadState(reader);
                    }
                }

                context.Equal("restored oscillator readback", oscillatorBeforeSave, restored.Read(0x1B));
                context.Equal("restored envelope readback", envelopeBeforeSave, restored.Read(0x1C));

                for (int cycle = 0; cycle < 600; cycle++)
                {
                    sid.Tick();
                    restored.Tick();
                }

                context.Equal("restored oscillator continues in lockstep", sid.Read(0x1B), restored.Read(0x1B));
                context.Equal("restored envelope continues in lockstep", sid.Read(0x1C), restored.Read(0x1C));
            }
            finally
            {
                sid.Dispose();
                restored.Dispose();
            }
        }

        private static void TestDriveTransportToggle(AccuracyContext context)
        {
            using (var system = new C64System(C64Model.Pal))
            {
                context.True("software IEC transport defaults on", system.ForceSoftwareIecTransport);
                system.ForceSoftwareIecTransport = false;
                context.True("ROM transport mode can be selected", !system.ForceSoftwareIecTransport);
                system.ForceSoftwareIecTransport = true;
                context.True("software IEC transport can be restored", system.ForceSoftwareIecTransport);
            }
        }

        private static void TestDriveAccuracySchedulerRunsContinuously(AccuracyContext context)
        {
            using (var compatibility = new C64System(C64Model.Pal))
            using (var accuracy = new C64System(C64Model.Pal, C64AccuracyOptions.Accuracy))
            {
                DriveSchedulerState compatibilityStart = compatibility.GetDriveSchedulerState(8);
                compatibility.RunCycles(12);
                DriveSchedulerState compatibilityEnd = compatibility.GetDriveSchedulerState(8);
                context.True("compatibility drive CPU remains parked without active work", compatibilityStart.ProgramCounter == compatibilityEnd.ProgramCounter);
                context.True("compatibility drive scheduler is not continuous", !compatibilityEnd.RunHardwareContinuously);

                DriveSchedulerState accuracyStart = accuracy.GetDriveSchedulerState(8);
                accuracy.RunCycles(12);
                DriveSchedulerState accuracyEnd = accuracy.GetDriveSchedulerState(8);
                context.True("accuracy drive scheduler is continuous", accuracyEnd.RunHardwareContinuously);
                context.True("accuracy drive clock executes cycles", accuracyEnd.ExecutedCycles > accuracyStart.ExecutedCycles);
                context.True("accuracy drive CPU advances while idle", accuracyStart.ProgramCounter != accuracyEnd.ProgramCounter);
            }
        }

        private static void TestDriveCustomHandoffPrimesSerialViaOutputs(AccuracyContext context)
        {
            var bus = new IecBus();
            var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));

            drive.Hardware.UploadMemory(0x0500, new byte[] { 0xEA, 0xEA, 0xEA });
            drive.Hardware.ExecuteAt(0x0500);

            string debug = drive.GetDebugInfo();
            context.True("custom code active", drive.HasCustomCodeActive);
            context.True("serial VIA DATA/CLOCK outputs are primed", debug.Contains("ddrb=0A"));
            context.True("serial VIA CA1 interrupt is enabled", debug.Contains("ier=02"));
            context.True("handoff keeps external IEC lines released", debug.Contains("dataOut=False") && debug.Contains("clockOut=False"));
        }

        private static void TestDriveDotcFinalLoaderTableMatchesViceHandoff(AccuracyContext context)
        {
            var bus = new IecBus();
            var driveBus = new Drive1541Bus(bus.CreatePort("Drive8-HW"), 8);
            MethodInfo method = typeof(Drive1541Bus).GetMethod(
                "ApplyCustomLoaderTableCompatibility",
                BindingFlags.Instance | BindingFlags.NonPublic);
            context.True("DOTC compatibility method exists", method != null);
            if (method == null)
            {
                return;
            }

            const ushort offsetBase = 0x0407;
            const ushort sectorBase = 0x045A;
            const ushort trackBase = 0x04AD;

            void Write(ushort baseAddress, int index, byte value)
            {
                driveBus.WriteRam((ushort)(baseAddress + index), value);
            }

            byte Read(ushort baseAddress, int index)
            {
                return driveBus.ReadRam((ushort)(baseAddress + index));
            }

            Write(trackBase, 0x00, 0x11);
            Write(sectorBase, 0x00, 0x00);
            Write(trackBase, 0x25, 0x10);
            Write(sectorBase, 0x25, 0x0E);
            Write(trackBase, 0x28, 0x10);
            Write(sectorBase, 0x28, 0x09);

            method.Invoke(driveBus, new object[] { (ushort)0x07A0 });

            context.Equal("DOTC entry 00 offset", (byte)0x00, Read(offsetBase, 0x00));
            context.Equal("DOTC entry 00 track", (byte)0x11, Read(trackBase, 0x00));
            context.Equal("DOTC entry 00 sector", (byte)0x0A, Read(sectorBase, 0x00));
            context.Equal("DOTC final request offset", (byte)0x7B, Read(offsetBase, 0x28));
            context.Equal("DOTC final request track", (byte)0x04, Read(trackBase, 0x28));
            context.Equal("DOTC final request sector", (byte)0x05, Read(sectorBase, 0x28));
            context.Equal("DOTC final boundary offset", (byte)0x4F, Read(offsetBase, 0x29));
            context.Equal("DOTC final boundary track", (byte)0x02, Read(trackBase, 0x29));
            context.Equal("DOTC final boundary sector", (byte)0x14, Read(sectorBase, 0x29));
            context.Equal("DOTC final reference tail offset", (byte)0x12, Read(offsetBase, 0x34));
            context.Equal("DOTC final reference tail track", (byte)0x1C, Read(trackBase, 0x34));
            context.Equal("DOTC final reference tail sector", (byte)0x06, Read(sectorBase, 0x34));
            context.Equal("DOTC unused entries are cleared", (byte)0x00, Read(trackBase, 0x35));
        }

        private static void TestDriveSerialViaInputsIgnoreOwnDirectOutputs(AccuracyContext context)
        {
            var bus = new IecBus();
            IecBusPort c64Port = bus.CreatePort("C64");
            var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));

            drive.Hardware.ExecuteAt(0x0500);
            drive.Hardware.WriteMemory(0x1800, 0x08);

            byte driveOnlyRead = drive.Hardware.ReadMemory(0x1800);
            context.True("own clock output pulls bus low", drive.GetDebugInfo().Contains("clockOut=True"));
            context.Equal("own clock is not reflected into PB2", (byte)0x00, (byte)(driveOnlyRead & 0x04));

            c64Port.SetLineLow(IecBusLine.Clock, true);
            byte externalClockRead = drive.Hardware.ReadMemory(0x1800);
            context.Equal("external clock low reaches PB2", (byte)0x04, (byte)(externalClockRead & 0x04));

            drive.Hardware.WriteMemory(0x1800, 0x02);
            byte driveDataOnlyRead = drive.Hardware.ReadMemory(0x1800);
            context.Equal("own data is not reflected into PB0", (byte)0x00, (byte)(driveDataOnlyRead & 0x01));

            c64Port.SetLineLow(IecBusLine.Data, true);
            byte externalDataRead = drive.Hardware.ReadMemory(0x1800);
            context.Equal("external data low reaches PB0", (byte)0x01, (byte)(externalDataRead & 0x01));
        }

        private static void TestDriveExecuteBufferJobEntersRomIrqPath(AccuracyContext context)
        {
            var bus = new IecBus();
            var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
            var rom = new byte[0x4000];

            WriteDriveRom(rom, 0xF2B0, 0xBA, 0x86, 0x49, 0x4C, 0x00, 0x05);
            WriteDriveRom(rom, 0xFD9E, 0xA9, 0x01, 0x85, 0x02, 0xA6, 0x49, 0x9A, 0x60);
            WriteDriveRom(rom, 0xFE7F, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40);
            drive.Hardware.Bus.LoadRom(rom);

            drive.Hardware.UploadMemory(0x0500, new byte[]
            {
                0xA5, 0x33,       // LDA $33
                0x85, 0x11,       // STA $11
                0xA9, 0x42,       // LDA #$42
                0x85, 0x10,       // STA $10
                0x4C, 0x9E, 0xFD  // JMP $FD9E
            });
            drive.Hardware.UploadMemory(0x0600, new byte[]
            {
                0xEA,             // NOP
                0x4C, 0x00, 0x06  // JMP $0600
            });
            drive.Hardware.ExecuteAt(0x0600);
            drive.Hardware.Cpu.SR = 0x20;
            drive.Hardware.WriteMemory(0x0033, 0x02);
            drive.Hardware.WriteMemory(0x0002, 0xE0);

            for (int tick = 0; tick < 500 &&
                (drive.Hardware.ReadMemory(0x0010) != 0x42 || drive.Hardware.ReadMemory(0x0002) != 0x01);
                tick++)
            {
                drive.Hardware.Tick();
            }

            context.Equal("execute buffer marker", (byte)0x42, drive.Hardware.ReadMemory(0x0010));
            context.Equal("execute buffer pointer high byte", (byte)0x00, drive.Hardware.ReadMemory(0x0011));
            context.Equal("execute buffer job status", (byte)0x01, drive.Hardware.ReadMemory(0x0002));
            context.True("custom code remains active after IRQ return", drive.HasCustomCodeActive);
        }

        private static void WriteDriveRom(byte[] rom, ushort address, params byte[] bytes)
        {
            int offset = address & 0x3FFF;
            Array.Copy(bytes, 0, rom, offset, bytes.Length);
        }

        private static void TestDriveMountPrimesRomDiskContext(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-mount-context-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, null, null);
                SetD64DiskIdForTest(tempPath, 0x4B, 0x50);

                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.Hardware.MountDisk(D64Image.Load(tempPath));

                context.Equal("ROM header id", (byte)0x08, drive.Hardware.ReadMemory(0x0039));
                context.Equal("slot 2 disk id low", (byte)0x4B, drive.Hardware.ReadMemory(0x0016));
                context.Equal("slot 2 disk id high", (byte)0x50, drive.Hardware.ReadMemory(0x0017));
                context.Equal("current track", (byte)0x12, drive.Hardware.ReadMemory(0x0018));
                context.Equal("current sector", (byte)0x00, drive.Hardware.ReadMemory(0x0019));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveDosJobPrimesRomDiskContext(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-dos-context-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, null, null);
                SetD64DiskIdForTest(tempPath, 0x4B, 0x50);

                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.Hardware.MountDisk(D64Image.Load(tempPath));

                drive.Hardware.WriteMemory(0x000A, 0x01);
                drive.Hardware.WriteMemory(0x000B, 0x00);
                drive.Hardware.WriteMemory(0x0002, 0x80);

                for (int tick = 0; tick < 20 && drive.Hardware.ReadMemory(0x0002) == 0x80; tick++)
                {
                    drive.Hardware.Bus.Tick();
                }

                context.Equal("read job status", (byte)0x01, drive.Hardware.ReadMemory(0x0002));
                context.Equal("ROM header id", (byte)0x08, drive.Hardware.ReadMemory(0x0039));
                context.Equal("slot 2 disk id low", (byte)0x4B, drive.Hardware.ReadMemory(0x0016));
                context.Equal("slot 2 disk id high", (byte)0x50, drive.Hardware.ReadMemory(0x0017));
                context.Equal("current track", (byte)0x01, drive.Hardware.ReadMemory(0x0018));
                context.Equal("current sector", (byte)0x00, drive.Hardware.ReadMemory(0x0019));

                drive.Hardware.WriteMemory(0x0002, 0xE0);
                drive.Hardware.Bus.Tick();
                context.Equal("execute current-track reference is normalized", (byte)0x01, drive.Hardware.ReadMemory(0x000A));
                context.Equal("execute current-sector reference is normalized", (byte)0x00, drive.Hardware.ReadMemory(0x000B));
                context.Equal("execute preserves current track", (byte)0x01, drive.Hardware.ReadMemory(0x0018));
                context.Equal("execute preserves current sector", (byte)0x00, drive.Hardware.ReadMemory(0x0019));
                context.True("execute job is queued for the ROM path", drive.Hardware.Bus.HasPendingExecuteBufferJob);
                context.True(
                    "execute job can be consumed by the ROM path",
                    drive.Hardware.Bus.TryConsumePendingExecuteBufferJob(out int executeSlot, out byte executeJob));
                context.Equal("execute job slot", 2, executeSlot);
                context.Equal("execute job code", (byte)0xE0, executeJob);

                drive.Hardware.WriteMemory(0x001A, 0x0E);
                drive.Hardware.Bus.Tick();
                context.Equal("active execute job does not clobber ROM checksum scratch", (byte)0x0E, drive.Hardware.ReadMemory(0x001A));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveSequentialLoadPrimesRomDiskContext(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-seq-context-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, "E5", new byte[] { 0x01, 0x08, 0xAA });
                SetD64DiskIdForTest(tempPath, 0x4B, 0x50);

                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.MountDisk(D64Image.Load(tempPath));

                IecDrive1541.CommandResult openResult = drive.OpenKernalChannel(0, "E5");
                context.Equal("open status", (byte)0x00, openResult.Status);
                context.True("first byte read", drive.TryReadKernalChannelByte(0, out byte value, out bool endOfInformation));
                context.Equal("first file byte", (byte)0x01, value);
                context.True("not finished after first byte", !endOfInformation);

                context.Equal("ROM header id", (byte)0x08, drive.Hardware.ReadMemory(0x0039));
                context.Equal("slot 2 disk id low", (byte)0x4B, drive.Hardware.ReadMemory(0x0016));
                context.Equal("slot 2 disk id high", (byte)0x50, drive.Hardware.ReadMemory(0x0017));
                context.Equal("current track", (byte)0x01, drive.Hardware.ReadMemory(0x0018));
                context.Equal("current sector", (byte)0x00, drive.Hardware.ReadMemory(0x0019));

                D64Image image = D64Image.Load(tempPath);
                context.True(
                    "next physical sector has stream offset",
                    image.TryGetNextSectorStreamStartOffset(1, 0, out int nextSectorOffset));
                context.Equal("raw GCR head moves past shortcut sector", nextSectorOffset * 8, GetDriveMechanismBitIndex(drive));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveChunkLoadPrimesRomDiskContext(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-chunk-context-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, "E5", new byte[] { 0x01, 0x08, 0xAA });
                SetD64DiskIdForTest(tempPath, 0x4B, 0x50);

                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.MountDisk(D64Image.Load(tempPath));

                IecDrive1541.CommandResult openResult = drive.OpenKernalChannel(0, "E5");
                context.Equal("open status", (byte)0x00, openResult.Status);

                MethodInfo method = typeof(IecDrive1541).GetMethod(
                    "TryResolveTalkChunk",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null)
                {
                    throw new InvalidOperationException("Missing TryResolveTalkChunk.");
                }

                object[] parameters = new object[] { (byte)0, 1, null, false };
                bool resolved = (bool)method.Invoke(drive, parameters);
                byte[] talkData = parameters[2] as byte[];
                bool isFinalChunk = (bool)parameters[3];

                context.True("chunk resolved", resolved);
                context.True("chunk contains one byte", talkData != null && talkData.Length == 1);
                context.Equal("first chunk byte", (byte)0x01, talkData == null || talkData.Length == 0 ? (byte)0x00 : talkData[0]);
                context.True("not final after first byte", !isFinalChunk);

                context.Equal("ROM header id", (byte)0x08, drive.Hardware.ReadMemory(0x0039));
                context.Equal("slot 2 disk id low", (byte)0x4B, drive.Hardware.ReadMemory(0x0016));
                context.Equal("slot 2 disk id high", (byte)0x50, drive.Hardware.ReadMemory(0x0017));
                context.Equal("current track", (byte)0x01, drive.Hardware.ReadMemory(0x0018));
                context.Equal("current sector", (byte)0x00, drive.Hardware.ReadMemory(0x0019));

                D64Image image = D64Image.Load(tempPath);
                context.True(
                    "next physical sector has stream offset",
                    image.TryGetNextSectorStreamStartOffset(1, 0, out int nextSectorOffset));
                context.Equal("raw GCR head moves past shortcut chunk sector", nextSectorOffset * 8, GetDriveMechanismBitIndex(drive));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveFinalSequentialLoadAdvancesRomDiskContext(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-final-context-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                byte[] diskBytes = new byte[174848];
                int firstSectorOffset = GetD64SectorOffsetForTest(1, 10);
                diskBytes[firstSectorOffset] = 0x01;
                diskBytes[firstSectorOffset + 1] = 0x02;
                for (int index = 0; index < 254; index++)
                {
                    diskBytes[firstSectorOffset + 2 + index] = 0xAA;
                }

                int finalSectorOffset = GetD64SectorOffsetForTest(1, 2);
                diskBytes[finalSectorOffset] = 0x00;
                diskBytes[finalSectorOffset + 1] = 0x02;
                diskBytes[finalSectorOffset + 2] = 0xBB;

                int directorySectorOffset = GetD64SectorOffsetForTest(18, 1);
                diskBytes[directorySectorOffset + 2] = 0x82;
                diskBytes[directorySectorOffset + 3] = 1;
                diskBytes[directorySectorOffset + 4] = 10;
                for (int index = 0; index < 16; index++)
                {
                    diskBytes[directorySectorOffset + 5 + index] = 0xA0;
                }

                byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes("E5");
                Array.Copy(nameBytes, 0, diskBytes, directorySectorOffset + 5, nameBytes.Length);
                diskBytes[directorySectorOffset + 30] = 2;
                File.WriteAllBytes(tempPath, diskBytes);
                SetD64DiskIdForTest(tempPath, 0x4B, 0x50);

                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.MountDisk(D64Image.Load(tempPath));

                IecDrive1541.CommandResult openResult = drive.OpenKernalChannel(0, "E5");
                context.Equal("open status", (byte)0x00, openResult.Status);

                int readCount = 0;
                bool endOfInformation = false;
                byte value = 0x00;
                while (!endOfInformation && drive.TryReadKernalChannelByte(0, out value, out endOfInformation))
                {
                    readCount++;
                }

                context.Equal("all chained file bytes read", 255, readCount);
                context.Equal("final chained byte", (byte)0xBB, value);
                context.True("final byte reports EOI", endOfInformation);

                D64Image image = D64Image.Load(tempPath);
                context.True(
                    "physical successor after previous sector resolves",
                    image.TryGetNextPhysicalSector(1, 10, out int nextSector));
                context.Equal("physical successor after sector 10", 20, nextSector);
                context.Equal("current track follows physical successor", (byte)0x01, drive.Hardware.ReadMemory(0x0018));
                context.Equal("current sector follows physical successor", (byte)0x14, drive.Hardware.ReadMemory(0x0019));
                context.True(
                    "raw stream points past physical successor",
                    image.TryGetNextSectorStreamStartOffset(1, 20, out int streamOffset));
                context.Equal("head is past physical successor", streamOffset * 8, GetDriveMechanismBitIndex(drive));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveDosContextSyncsGcrHeadPosition(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-head-context-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, null, null);

                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.Hardware.MountDisk(D64Image.Load(tempPath));

                context.Equal("mount head defaults to directory track", 34, drive.Hardware.Bus.CurrentHalfTrack);

                drive.Hardware.WriteMemory(0x000A, 0x22);
                drive.Hardware.WriteMemory(0x000B, 0x00);
                drive.Hardware.WriteMemory(0x0002, 0xE0);
                drive.Hardware.Bus.Tick();

                context.True("execute job is queued", drive.Hardware.Bus.HasPendingExecuteBufferJob);
                context.Equal("execute job preserves DOS current track", (byte)0x12, drive.Hardware.ReadMemory(0x0018));
                context.Equal("execute job moves raw GCR head to track 34", 66, drive.Hardware.Bus.CurrentHalfTrack);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveHeadChangesPreserveDiskRotationPhase(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-head-phase-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, null, null);
                var mechanism = new Drive1541Mechanism();
                var diskVia = new DriveVia6522();

                diskVia.Reset();
                mechanism.MountDisk(D64Image.Load(tempPath));
                mechanism.ApplyViaPortB(0x64, 0xE4);

                for (int cycle = 0; cycle < 1000; cycle++)
                {
                    mechanism.Tick(diskVia);
                }

                int bitIndexBeforeSeek = GetPrivateInt(mechanism, "_trackBitIndex");
                int bitCountBeforeSeek = GetPrivateInt(mechanism, "_trackBitCount");
                context.True("rotation advanced before head change", bitIndexBeforeSeek > 0);

                mechanism.SeekToTrack(34);
                int bitIndexAfterSeek = GetPrivateInt(mechanism, "_trackBitIndex");
                int bitCountAfterSeek = GetPrivateInt(mechanism, "_trackBitCount");
                int expectedSeekPhase = (int)(((long)bitIndexBeforeSeek * bitCountAfterSeek) / bitCountBeforeSeek) % bitCountAfterSeek;
                context.Equal("logical seek scales bit phase", expectedSeekPhase, bitIndexAfterSeek);
                context.Equal("logical seek moves head", 66, mechanism.CurrentHalfTrack);

                mechanism.ApplyViaPortB(0x65, 0xE7);
                context.Equal("stepper preserves same-track bit phase", bitIndexAfterSeek, GetPrivateInt(mechanism, "_trackBitIndex"));
                context.Equal("stepper moves half-track", 67, mechanism.CurrentHalfTrack);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestD64GcrTracksUseSyntheticTrackSkew(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-gcr-skew-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, null, null);
                D64Image image = D64Image.Load(tempPath);

                context.True("track 1 stream exists", image.TryGetTrackStream(0, out byte[] track1));
                context.True("track 2 stream exists", image.TryGetTrackStream(2, out byte[] track2));
                int firstSyncTrack1 = FindGcrSyncRun(track1);
                int firstSyncTrack2 = FindGcrSyncRun(track2);

                context.True("track 1 sync is not byte-aligned to zero", firstSyncTrack1 > 0);
                context.True("track 2 sync is not byte-aligned to zero", firstSyncTrack2 > 0);
                context.True("neighbor tracks have different angular alignment", firstSyncTrack1 != firstSyncTrack2);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static int FindGcrSyncRun(byte[] trackBytes)
        {
            if (trackBytes == null)
            {
                return -1;
            }

            for (int offset = 0; offset <= trackBytes.Length - 5; offset++)
            {
                if (trackBytes[offset] == 0xFF &&
                    trackBytes[offset + 1] == 0xFF &&
                    trackBytes[offset + 2] == 0xFF &&
                    trackBytes[offset + 3] == 0xFF &&
                    trackBytes[offset + 4] == 0xFF)
                {
                    return offset;
                }
            }

            return -1;
        }

        private static void TestDriveDiskByteReadyIsNotPcrGated(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-byte-ready-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                WriteMinimalD64(tempPath, null, null);
                var mechanism = new Drive1541Mechanism();
                var diskVia = new DriveVia6522();

                diskVia.Reset();
                diskVia.Write(0x0C, 0x00);
                mechanism.MountDisk(D64Image.Load(tempPath));
                mechanism.ApplyViaPortB(0x64, 0xE4);

                int soPulses = 0;
                for (int cycle = 0; cycle < 1000; cycle++)
                {
                    mechanism.Tick(diskVia);
                    if (mechanism.ConsumeSoPulse())
                    {
                        soPulses++;
                    }
                }

                context.True("SO pulses are produced with PCR $00", soPulses > 0);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveDiskSwapPreservesCustomCode(AccuracyContext context)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "c64emulator-empty-" + Guid.NewGuid().ToString("N") + ".d64");
            try
            {
                File.WriteAllBytes(tempPath, new byte[174848]);
                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));

                drive.Hardware.UploadMemory(0x0500, new byte[] { 0xEA, 0xEA, 0xEA });
                drive.Hardware.ExecuteAt(0x0500);
                context.True("custom code active before disk mount", drive.HasCustomCodeActive);
                context.Equal("custom code PC before disk mount", (ushort)0x0500, drive.Hardware.ProgramCounter);

                drive.MountDisk(D64Image.Load(tempPath));
                context.True("custom code active after disk mount", drive.HasCustomCodeActive);
                context.Equal("custom code PC after disk mount", (ushort)0x0500, drive.Hardware.ProgramCounter);

                drive.EjectDisk();
                context.True("custom code active after disk eject", drive.HasCustomCodeActive);
                context.Equal("custom code PC after disk eject", (ushort)0x0500, drive.Hardware.ProgramCounter);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TestDriveDiskSetAutoOpensCompanionSide(AccuracyContext context)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "c64emulator-diskset-" + Guid.NewGuid().ToString("N"));
            string sideAPath = Path.Combine(tempDirectory, "test_Side_1.d64");
            string sideBPath = Path.Combine(tempDirectory, "test_Side_2.d64");

            try
            {
                Directory.CreateDirectory(tempDirectory);
                WriteMinimalD64(sideAPath, null, null);
                WriteMinimalD64(sideBPath, "E5", new byte[] { 0x01, 0x08, 0xAA });

                D64Image sideA = D64Image.Load(sideAPath);
                D64Image sideB = D64Image.Load(sideBPath);
                var bus = new IecBus();
                var drive = new IecDrive1541(8, bus.CreatePort("Drive8"), bus.CreatePort("Drive8-HW"));
                drive.MountDisk(sideA, new[] { sideA, sideB });

                IecDrive1541.CommandResult openResult = drive.OpenKernalChannel(0, "0:E5");
                context.Equal("companion side open succeeds", (byte)0x00, openResult.Status);
                context.Equal("companion side status message", "AUTO-SWAPPED TO DISK 2", drive.ConsumeStatusText());
                context.True("debug records companion side", drive.GetDebugInfo().Contains("test_Side_2.d64"));

                byte value;
                bool endOfInformation;
                context.True("first companion byte can be read", drive.TryReadKernalChannelByte(0, out value, out endOfInformation));
                context.Equal("first companion byte", (byte)0x01, value);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        private static void WriteMinimalD64(string path, string fileName, byte[] fileBytes)
        {
            byte[] diskBytes = new byte[174848];
            if (!string.IsNullOrWhiteSpace(fileName) && fileBytes != null)
            {
                int fileSectorOffset = GetD64SectorOffsetForTest(1, 0);
                diskBytes[fileSectorOffset] = 0x00;
                diskBytes[fileSectorOffset + 1] = (byte)Math.Min(255, fileBytes.Length + 1);
                Array.Copy(fileBytes, 0, diskBytes, fileSectorOffset + 2, Math.Min(fileBytes.Length, 254));

                int directorySectorOffset = GetD64SectorOffsetForTest(18, 1);
                diskBytes[directorySectorOffset + 2] = 0x82;
                diskBytes[directorySectorOffset + 3] = 1;
                diskBytes[directorySectorOffset + 4] = 0;
                for (int index = 0; index < 16; index++)
                {
                    diskBytes[directorySectorOffset + 5 + index] = 0xA0;
                }

                byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName.ToUpperInvariant());
                Array.Copy(nameBytes, 0, diskBytes, directorySectorOffset + 5, Math.Min(nameBytes.Length, 16));
                diskBytes[directorySectorOffset + 30] = 1;
            }

            File.WriteAllBytes(path, diskBytes);
        }

        private static void SetD64DiskIdForTest(string path, byte diskId1, byte diskId2)
        {
            byte[] diskBytes = File.ReadAllBytes(path);
            int bamOffset = GetD64SectorOffsetForTest(18, 0);
            diskBytes[bamOffset + 0xA2] = diskId1;
            diskBytes[bamOffset + 0xA3] = diskId2;
            File.WriteAllBytes(path, diskBytes);
        }

        private static int GetD64SectorOffsetForTest(int track, int sector)
        {
            int sectorOffset = 0;
            for (int currentTrack = 1; currentTrack < track; currentTrack++)
            {
                sectorOffset += GetD64SectorsPerTrackForTest(currentTrack);
            }

            return (sectorOffset + sector) * 256;
        }

        private static int GetD64SectorsPerTrackForTest(int track)
        {
            if (track <= 17)
            {
                return 21;
            }

            if (track <= 24)
            {
                return 19;
            }

            if (track <= 30)
            {
                return 18;
            }

            return 17;
        }

        private static void TestDriveStepperMovesOnlyWhileMotorOn(AccuracyContext context)
        {
            var mechanism = new Drive1541Mechanism();
            context.Equal("initial half-track", 34, mechanism.CurrentHalfTrack);

            mechanism.ApplyViaPortB(0x01, 0x03);
            context.Equal("motor-off phase write does not step", 34, mechanism.CurrentHalfTrack);
            context.True("motor remains off", !mechanism.MotorOn);

            mechanism.ApplyViaPortB(0x04 | 0x01, 0x07);
            context.Equal("motor-on phase latch does not retro-step", 34, mechanism.CurrentHalfTrack);
            context.True("motor turns on", mechanism.MotorOn);

            mechanism.ApplyViaPortB(0x04 | 0x02, 0x07);
            context.Equal("forward phase advances half-track", 35, mechanism.CurrentHalfTrack);

            mechanism.ApplyViaPortB(0x04 | 0x01, 0x07);
            context.Equal("reverse phase returns half-track", 34, mechanism.CurrentHalfTrack);
        }

        private static void TestRelayListenerDisposeIsIdempotent(AccuracyContext context)
        {
            var listener = new C64RelayServerListener();
            listener.Dispose();

            // StartRelay can dispose a failed listener before its catch block
            // runs cleanup. A second Dispose must not mask the original error.
            listener.Dispose();
            context.True("second dispose completed without throwing", true);
        }

        private static void TestOverlayFontIncludesPasswordMaskGlyph(AccuracyContext context)
        {
            FieldInfo field = typeof(global::C64Emulator.C64Window).GetField("OverlayFont", BindingFlags.NonPublic | BindingFlags.Static);
            context.True("overlay font field exists", field != null);
            if (field == null)
            {
                return;
            }

            var font = field.GetValue(null) as IDictionary<char, byte[]>;
            context.True("overlay font can be read", font != null);
            if (font == null)
            {
                return;
            }

            byte[] glyph;
            bool hasVisibleGlyph = false;
            if (font.TryGetValue('*', out glyph) && glyph != null)
            {
                for (int index = 0; index < glyph.Length; index++)
                {
                    if (glyph[index] != 0)
                    {
                        hasVisibleGlyph = true;
                        break;
                    }
                }
            }

            context.True("asterisk glyph is visible", hasVisibleGlyph);
        }
    }
}
