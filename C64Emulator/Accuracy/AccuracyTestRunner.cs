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

        /// <summary>
        /// Runs all built-in accuracy checks.
        /// </summary>
        public static int Run(TextWriter output)
        {
            output.WriteLine("C64 ACCURACY TESTS");
            output.WriteLine("Scope=internal timing smoke tests plus external golden-suite infrastructure.");

            int failures = 0;
            failures += RunCase(output, "Accuracy profile disables emulator shortcuts", TestAccuracyProfileDisablesShortcuts);
            failures += RunCase(output, "CPU bus prediction is side-effect free", TestCpuBusPredictionIsSideEffectFree);
            failures += RunCase(output, "VIC frame timing", TestVicFrameTiming);
            failures += RunCase(output, "VIC raster IRQ compare is cycle driven", TestVicRasterIrqCompareIsCycleDriven);
            failures += RunCase(output, "VIC sprite DMA starts at Y-compare cycle", TestVicSpriteDmaStartsAtYCompareCycle);
            failures += RunCase(output, "VIC bus-plan golden slots", TestVicBusPlanGoldenSlots);
            failures += RunCase(output, "VIC badline pipeline gates c-accesses", TestVicBadlinePipelineGatesCAccesses);
            failures += RunCase(output, "CIA timer A continuous/one-shot timing", TestCiaTimerATiming);
            failures += RunCase(output, "CIA1/CIA2 timer force-load parity", TestCiaTimerForceLoadParity);
            failures += RunCase(output, "CIA timer B counts timer A underflows", TestCiaTimerBCountsTimerA);
            failures += RunCase(output, "CIA TOD PAL tenth increment", TestCiaTodTenthIncrement);
            failures += RunCase(output, "SID envelope gate attack/release", TestSidEnvelopeGateAttackRelease);
            failures += RunCase(output, "1541 transport mode toggles", TestDriveTransportToggle);
            failures += RunCase(output, "1541 accuracy scheduler runs drive CPU continuously", TestDriveAccuracySchedulerRunsContinuously);
            failures += RunCase(output, "1541 disk swap preserves custom drive code", TestDriveDiskSwapPreservesCustomCode);

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

        private static void TestVicBusPlanGoldenSlots(AccuracyContext context)
        {
            var plan = new VicBusPlan();
            plan.BuildLine(false, null);

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
            context.True("timer A IRQ is asserted at terminal count", cia.IsIrqAsserted());
            context.Equal("timer A ICR bit", 0x81, cia.Read(0x0D));
            context.True("timer A ICR read clears IRQ", !cia.IsIrqAsserted());

            cia.Reset();
            cia.Write(0x04, 0x02);
            cia.Write(0x05, 0x00);
            cia.Write(0x0E, 0x09);
            cia.Tick();
            cia.Tick();
            context.Equal("timer A one-shot stops", 0x08, cia.Read(0x0E));
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
            context.Equal("CIA1 latch-zero first tick low", 0xFE, cia1.Read(0x04));
            context.Equal("CIA2 latch-zero first tick low", 0xFE, cia2.Read(0x04));
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
            context.True("timer B quiet after first timer A underflow", !cia.IsIrqAsserted());
            cia.Tick();
            context.True("timer B IRQ after second timer A underflow", cia.IsIrqAsserted());
            byte icr = cia.Read(0x0D);
            context.True("timer B ICR bit set", (icr & 0x82) == 0x82);
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
    }
}
