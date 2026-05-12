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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using C64Emulator.Core;

namespace C64Emulator
{
    /// <summary>
    /// Represents the program component.
    /// </summary>
    static class Program
    {
        /// <summary>
        /// Der Haupteinstiegspunkt fuer die Anwendung.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Command-line modes are kept before any WinForms/OpenTK startup so
            // CI, release scripts, and VICE comparisons can run headlessly.
            if (args != null && args.Length >= 1 && string.Equals(args[0], "--self-test-cpu", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = args.Length >= 2
                    ? args[1]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cpu_self_test.log");
                RunCpuSelfTest(logPath);
                return;
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--check-roms", StringComparison.OrdinalIgnoreCase))
            {
                RunRomCheck();
                return;
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--benchmark", StringComparison.OrdinalIgnoreCase))
            {
                int cycles = 2000000;
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmark.log");
                if (args.Length >= 2)
                {
                    int parsedCycles;
                    if (int.TryParse(args[1], out parsedCycles) && parsedCycles > 0)
                    {
                        cycles = parsedCycles;
                    }
                    else
                    {
                        logPath = args[1];
                    }
                }

                if (args.Length >= 3)
                {
                    logPath = args[2];
                }

                RunBenchmark(cycles, logPath);
                return;
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--accuracy-tests", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = args.Length >= 2
                    ? args[1]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accuracy_tests.log");
                RunAccuracyTests(logPath);
                return;
            }

            if (args != null && args.Length >= 2 && string.Equals(args[0], "--golden-run", StringComparison.OrdinalIgnoreCase))
            {
                string outputDirectory = args.Length >= 3
                    ? args[2]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-results");
                RunGoldenManifest(args[1], outputDirectory);
                return;
            }

            if (args != null && args.Length >= 3 && string.Equals(args[0], "--golden-accept", StringComparison.OrdinalIgnoreCase))
            {
                string outputManifestPath = args.Length >= 4 ? args[3] : args[1];
                AcceptGoldenBaseline(args[1], args[2], outputManifestPath);
                return;
            }

            if (args != null && args.Length >= 3 && string.Equals(args[0], "--golden-compare", StringComparison.OrdinalIgnoreCase))
            {
                string reportPath = args.Length >= 4
                    ? args[3]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden_compare.log");
                CompareGoldenResults(args[1], args[2], reportPath);
                return;
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--trace-cycles", StringComparison.OrdinalIgnoreCase))
            {
                int cycles = args.Length >= 2 && int.TryParse(args[1], out int parsedCycles) && parsedCycles > 0
                    ? parsedCycles
                    : 20000;
                string logPath = args.Length >= 3
                    ? args[2]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace_cycles.csv");
                int sampleInterval = args.Length >= 4 && int.TryParse(args[3], out int parsedInterval) && parsedInterval > 0
                    ? parsedInterval
                    : 63;
                RunTraceCycles(cycles, logPath, sampleInterval);
                return;
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--trace-machine", StringComparison.OrdinalIgnoreCase))
            {
                int cycles = args.Length >= 2 && int.TryParse(args[1], out int parsedMachineCycles) && parsedMachineCycles > 0
                    ? parsedMachineCycles
                    : 20000;
                string logPath = args.Length >= 3
                    ? args[2]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace_machine.jsonl");
                int sampleInterval = args.Length >= 4 && int.TryParse(args[3], out int parsedMachineInterval) && parsedMachineInterval > 0
                    ? parsedMachineInterval
                    : 1;
                // Accuracy traces default to the hardware-faithful profile; pass
                // "compatibility" only when comparing against the interactive
                // convenience settings.
                bool accuracyProfile = args.Length < 5 || !string.Equals(args[4], "compatibility", StringComparison.OrdinalIgnoreCase);
                RunMachineTrace(cycles, logPath, sampleInterval, accuracyProfile);
                return;
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--regression-run", StringComparison.OrdinalIgnoreCase))
            {
                string mediaPath = args.Length >= 2 ? args[1] : string.Empty;
                if (string.Equals(mediaPath, "-", StringComparison.Ordinal))
                {
                    // "-" is used by scripts to mean "boot without media" without
                    // making argument positions ambiguous.
                    mediaPath = string.Empty;
                }

                int cycles = args.Length >= 3 && int.TryParse(args[2], out int parsedRegressionCycles) && parsedRegressionCycles >= 0
                    ? parsedRegressionCycles
                    : 1000000;
                string logPath = args.Length >= 4
                    ? args[3]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "regression_run.log");
                string framePath = args.Length >= 5
                    ? args[4]
                    : string.Empty;
                RunRegression(mediaPath, cycles, logPath, framePath);
                return;
            }

            if (args != null && args.Length >= 3 && string.Equals(args[0], "--run-prg-sys", StringComparison.OrdinalIgnoreCase))
            {
                ushort startAddress = ParseUShort(args[2]);
                int cycles = args.Length >= 4 && int.TryParse(args[3], out int parsedPrgCycles) && parsedPrgCycles >= 0
                    ? parsedPrgCycles
                    : 1000000;
                string logPath = args.Length >= 5
                    ? args[4]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prg_sys_run.log");
                string framePath = args.Length >= 6
                    ? args[5]
                    : string.Empty;
                int warmupCycles = args.Length >= 7 && int.TryParse(args[6], out int parsedWarmupCycles) && parsedWarmupCycles >= 0
                    ? parsedWarmupCycles
                    : 0;
                RunPrgSys(args[1], startAddress, cycles, logPath, framePath, warmupCycles);
                return;
            }

            if (args != null && args.Length >= 2 && string.Equals(args[0], "--render-savestate", StringComparison.OrdinalIgnoreCase))
            {
                int frames = args.Length >= 3 && int.TryParse(args[2], out int parsedFrames) && parsedFrames >= 0
                    ? parsedFrames
                    : 1;
                string framePath = args.Length >= 4
                    ? args[3]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "savestate_frame.ppm");
                string logPath = args.Length >= 5
                    ? args[4]
                    : Path.ChangeExtension(framePath, ".log");
                RunSavestateRender(args[1], frames, logPath, framePath);
                return;
            }

            if (args != null && args.Length >= 2 && string.Equals(args[0], "--probe-iec-load", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = args.Length >= 3
                    ? args[2]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe_iec_load.log");
                RunIecLoadProbe(args[1], logPath);
                return;
            }

            if (args != null && args.Length >= 2 && string.Equals(args[0], "--probe-run-d64", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = args.Length >= 3
                    ? args[2]
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe_run_d64.log");
                RunD64RunProbe(args[1], logPath);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!RomStartupDownloader.EnsureRequiredRoms())
            {
                return;
            }

            try
            {
                using (var window = new C64Window(C64Model.Pal, "C64 Emulator"))
                {
                    window.Run();
                }
            }
            catch (Exception ex)
            {
                string crashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_crash.log");
                try
                {
                    File.WriteAllText(crashLogPath, ex.ToString());
                }
                catch
                {
                }

                try
                {
                    MessageBox.Show(
                        "C64 Emulator konnte nicht starten.\r\n\r\nDetails wurden geschrieben nach:\r\n" + crashLogPath + "\r\n\r\n" + ex.Message,
                        "C64 Emulator Startfehler",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch
                {
                }

                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Runs the built-in CPU opcode self-test routine.
        /// </summary>
        private static void RunCpuSelfTest(string logPath)
        {
            var log = new StringWriter();
            int failures = CpuOpcodeSelfTest.Run(log);
            File.WriteAllText(logPath, log.ToString());
            Console.Write(log.ToString());
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Prints the currently resolved ROM files and exits with a failing code if any are missing.
        /// </summary>
        private static void RunRomCheck()
        {
            string report = RomPathResolver.BuildStatusReport();
            Console.Write(report);
            Environment.ExitCode = RomPathResolver.HasCompleteRomSet() ? 0 : 1;
        }

        /// <summary>
        /// Runs a headless emulation throughput benchmark.
        /// </summary>
        private static void RunBenchmark(int cycles, string logPath)
        {
            var log = new StringBuilder();
            log.AppendLine("C64 BENCHMARK");
            log.AppendLine("Cycles=" + cycles);
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));

            try
            {
                using (var system = new C64System(C64Model.Pal))
                {
                    system.RunCycles(20000);
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    system.RunCycles(cycles);
                    stopwatch.Stop();

                    double seconds = Math.Max(0.000001, stopwatch.Elapsed.TotalSeconds);
                    double cyclesPerSecond = cycles / seconds;
                    double realtimeFactor = cyclesPerSecond / C64Model.Pal.CpuHz;
                    log.AppendFormat(CultureInfo.InvariantCulture, "ElapsedSeconds={0:F6}", seconds).AppendLine();
                    log.AppendFormat(CultureInfo.InvariantCulture, "CyclesPerSecond={0:F0}", cyclesPerSecond).AppendLine();
                    log.AppendFormat(CultureInfo.InvariantCulture, "EmulatedMHz={0:F3}", cyclesPerSecond / 1000000.0).AppendLine();
                    log.AppendFormat(CultureInfo.InvariantCulture, "RealtimeFactor={0:F2}x", realtimeFactor).AppendLine();
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION:");
                log.AppendLine(ex.ToString());
                Environment.ExitCode = 1;
                File.WriteAllText(logPath, log.ToString());
                Console.Write(log.ToString());
                return;
            }

            File.WriteAllText(logPath, log.ToString());
            Console.Write(log.ToString());
            Environment.ExitCode = 0;
        }

        /// <summary>
        /// Runs the built-in subsystem accuracy smoke tests.
        /// </summary>
        private static void RunAccuracyTests(string logPath)
        {
            var log = new StringWriter();
            int failures = AccuracyTestRunner.Run(log);
            EnsureLogDirectory(logPath);
            File.WriteAllText(logPath, log.ToString());
            Console.Write(log.ToString());
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Runs an external golden manifest through the real emulator.
        /// </summary>
        private static void RunGoldenManifest(string manifestPath, string outputDirectory)
        {
            try
            {
                // The golden harness owns test orchestration; Program only resolves
                // paths, serializes machine-readable artifacts, and maps failures to
                // a process exit code for scripts.
                Directory.CreateDirectory(outputDirectory);
                GoldenManifest manifest = GoldenManifestLoader.Load(manifestPath);
                var harness = new GoldenTestHarness(new C64GoldenTestExecutor());
                GoldenRunResult result = harness.Run(manifest, outputDirectory);

                string jsonPath = Path.Combine(outputDirectory, "golden-results.json");
                string junitPath = Path.Combine(outputDirectory, "golden-results.junit.xml");
                GoldenJsonResultWriter.Write(jsonPath, result);
                GoldenJUnitResultWriter.Write(junitPath, result);

                Console.WriteLine("Golden suite: " + (string.IsNullOrWhiteSpace(result.Name) ? "golden" : result.Name));
                Console.WriteLine("Tests=" + result.TestCount + " Failures=" + result.FailureCount + " Errors=" + result.ErrorCount + " Skipped=" + result.SkippedCount);
                Console.WriteLine("JSON=" + Path.GetFullPath(jsonPath));
                Console.WriteLine("JUnit=" + Path.GetFullPath(junitPath));
                Environment.ExitCode = result.FailureCount == 0 && result.ErrorCount == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GOLDEN RUN FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Accepts actual golden output values into a manifest.
        /// </summary>
        private static void AcceptGoldenBaseline(string manifestPath, string resultPath, string outputManifestPath)
        {
            try
            {
                int updated = GoldenBaselineUpdater.Accept(manifestPath, resultPath, outputManifestPath);
                Console.WriteLine("Golden baseline updated: " + Path.GetFullPath(outputManifestPath));
                Console.WriteLine("UpdatedTests=" + updated.ToString(CultureInfo.InvariantCulture));
                Environment.ExitCode = updated > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GOLDEN ACCEPT FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Compares two golden result JSON files.
        /// </summary>
        private static void CompareGoldenResults(string referenceResultPath, string actualResultPath, string reportPath)
        {
            try
            {
                int failures = GoldenResultComparer.Compare(referenceResultPath, actualResultPath, reportPath);
                Console.WriteLine("Report=" + Path.GetFullPath(reportPath));
                Environment.ExitCode = failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GOLDEN COMPARE FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Ensures the target log directory exists.
        /// </summary>
        private static void EnsureLogDirectory(string logPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// Runs a sampled developer trace export.
        /// </summary>
        private static void RunTraceCycles(int cycles, string logPath, int sampleInterval)
        {
            try
            {
                DevTraceExporter.ExportTrace(cycles, sampleInterval, logPath);
                Console.WriteLine("Trace written: " + Path.GetFullPath(logPath));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("TRACE EXPORT FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Runs a structured machine-cycle trace export.
        /// </summary>
        private static void RunMachineTrace(int cycles, string logPath, int sampleInterval, bool accuracyProfile)
        {
            try
            {
                DevTraceExporter.ExportMachineTrace(cycles, sampleInterval, logPath, accuracyProfile);
                Console.WriteLine("Machine trace written: " + Path.GetFullPath(logPath));
                Console.WriteLine("Profile=" + (accuracyProfile ? "accuracy" : "compatibility"));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("MACHINE TRACE EXPORT FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Runs a media regression sample.
        /// </summary>
        private static void RunRegression(string mediaPath, int cycles, string logPath, string framePath)
        {
            try
            {
                DevTraceExporter.RunRegression(mediaPath, cycles, logPath, framePath);
                Console.WriteLine("Regression log written: " + Path.GetFullPath(logPath));
                if (!string.IsNullOrWhiteSpace(framePath))
                {
                    Console.WriteLine("Frame written: " + Path.GetFullPath(framePath));
                }

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("REGRESSION RUN FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Parses an unsigned 16-bit integer in decimal or hexadecimal notation.
        /// </summary>
        private static ushort ParseUShort(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            value = value.Trim();
            if (value.StartsWith("$", StringComparison.Ordinal))
            {
                return ushort.Parse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ushort.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Runs a PRG by jumping directly to a machine-code entry point.
        /// </summary>
        private static void RunPrgSys(string prgPath, ushort startAddress, int cycles, string logPath, string framePath, int warmupCycles)
        {
            try
            {
                DevTraceExporter.RunPrgSys(prgPath, startAddress, cycles, logPath, framePath, warmupCycles);
                Console.WriteLine("PRG SYS log written: " + Path.GetFullPath(logPath));
                if (!string.IsNullOrWhiteSpace(framePath))
                {
                    Console.WriteLine("Frame written: " + Path.GetFullPath(framePath));
                }

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PRG SYS RUN FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Renders a savestate framebuffer artifact.
        /// </summary>
        private static void RunSavestateRender(string savePath, int frames, string logPath, string framePath)
        {
            try
            {
                DevTraceExporter.RenderSavestate(savePath, frames, logPath, framePath);
                Console.WriteLine("Savestate render log written: " + Path.GetFullPath(logPath));
                Console.WriteLine("Frame written: " + Path.GetFullPath(framePath));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SAVESTATE RENDER FAILED");
                Console.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Runs the iec load probe routine.
        /// </summary>
        private static void RunIecLoadProbe(string d64Path, string logPath)
        {
            var log = new StringBuilder();
            log.AppendLine("IEC LOAD PROBE");
            log.AppendLine("D64=" + d64Path);
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));

            try
            {
                using (var system = new C64System(C64Model.Pal))
                {
                    log.AppendLine("Mount=" + system.MountMedia(d64Path, 8));

                    // Let the boot ROM reach READY before injecting BASIC text.  The
                    // probe is intentionally simple because it is used for diagnosing
                    // IEC handshakes, not for exercising the UI input path.
                    for (int i = 0; i < 400000; i++)
                    {
                        system.Tick();
                    }

                    system.EnqueuePetsciiText("LOAD\"*\",8\r");

                    for (int cycle = 0; cycle <= 3000000; cycle++)
                    {
                        system.Tick();

                        if (cycle % 50000 == 0)
                        {
                            log.AppendFormat(
                                "cycle={0} pc={1:X4} a={2:X2} x={3:X2} y={4:X2} sp={5:X2} sr={6:X2} st={7:X2} iec={8} drive8={9}",
                                cycle,
                                system.Cpu.PC,
                                system.Cpu.A,
                                system.Cpu.X,
                                system.Cpu.Y,
                                system.Cpu.SP,
                                system.Cpu.SR,
                                system.Peek(0x0090),
                                system.GetIecDebugInfo(),
                                system.GetDriveDebugInfo(8));
                            log.AppendLine();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION:");
                log.AppendLine(ex.ToString());
            }

            File.WriteAllText(logPath, log.ToString());
        }

        /// <summary>
        /// Runs the d64 run probe routine.
        /// </summary>
        private static void RunD64RunProbe(string d64Path, string logPath)
        {
            var log = new StringBuilder();
            log.AppendLine("D64 RUN PROBE");
            log.AppendLine("D64=" + d64Path);
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));

            try
            {
                using (var system = new C64System(C64Model.Pal))
                {
                    log.AppendLine("Mount=" + system.MountMedia(d64Path, 8));

                    for (int i = 0; i < 400000; i++)
                    {
                        system.Tick();
                    }

                    system.EnqueuePetsciiText("LOAD\"*\",8\r");

                    bool loadCompleted = false;
                    string lastDriveDebug = string.Empty;
                    string lastIecDebug = string.Empty;

                    for (int cycle = 0; cycle <= 120000000; cycle++)
                    {
                        system.Tick();

                        if (!loadCompleted &&
                            system.Peek(0x0090) == 0x40 &&
                            !system.IsDriveActive(8))
                        {
                            loadCompleted = true;
                            log.AppendFormat(
                                "LOAD-DONE cycle={0} pc={1:X4} st={2:X2}",
                                cycle,
                                system.Cpu.PC,
                                system.Peek(0x0090));
                            log.AppendLine();
                            system.EnqueuePetsciiText("RUN\r");
                        }

                        if (loadCompleted && cycle > 0 && (cycle % 4000000) == 2000000)
                        {
                            // Periodic CONTROL taps skip many intro pauses in common
                            // disk games, allowing the probe to reach gameplay frames
                            // while still using normal keyboard input.
                            system.KeyDown(OpenTK.Input.Key.ControlLeft);
                        }

                        if (loadCompleted && cycle > 0 && (cycle % 4000000) == 2001000)
                        {
                            system.KeyUp(OpenTK.Input.Key.ControlLeft);
                        }

                        if ((cycle % 1000000) == 0)
                        {
                            log.AppendFormat(
                                "cycle={0} pc={1:X4} a={2:X2} x={3:X2} y={4:X2} sp={5:X2} sr={6:X2} st={7:X2} active={8}",
                                cycle,
                                system.Cpu.PC,
                                system.Cpu.A,
                                system.Cpu.X,
                                system.Cpu.Y,
                                system.Cpu.SP,
                                system.Cpu.SR,
                                system.Peek(0x0090),
                                system.IsDriveActive(8));
                            log.AppendLine();
                            log.AppendLine("DRIVE8-SNAPSHOT=" + system.GetDriveDebugInfo(8));
                        }

                        string iecDebug = system.GetIecDebugInfo();
                        if (!string.Equals(iecDebug, lastIecDebug, StringComparison.Ordinal))
                        {
                            lastIecDebug = iecDebug;
                            log.AppendLine("IEC=" + iecDebug);
                        }

                        string driveDebug = system.GetDriveDebugInfo(8);
                        if (!string.Equals(driveDebug, lastDriveDebug, StringComparison.Ordinal))
                        {
                            lastDriveDebug = driveDebug;
                            log.AppendLine("DRIVE8=" + driveDebug);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION:");
                log.AppendLine(ex.ToString());
            }

            File.WriteAllText(logPath, log.ToString());
        }
    }
}
