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

            using (var window = new C64Window(C64Model.Pal, "C64 Emulator"))
            {
                window.Run();
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
