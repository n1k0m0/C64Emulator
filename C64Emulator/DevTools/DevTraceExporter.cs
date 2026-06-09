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
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTK.Input;

namespace C64Emulator.Core
{
    /// <summary>
    /// Exports deterministic headless developer traces and framebuffer artifacts.
    /// </summary>
    public static class DevTraceExporter
    {
        /// <summary>
        /// Runs the machine for a number of cycles and writes sampled subsystem state to CSV.
        /// </summary>
        public static void ExportTrace(int cycles, int sampleInterval, string logPath)
        {
            EnsureDirectory(logPath);
            sampleInterval = Math.Max(1, sampleInterval);

            using (var writer = new StreamWriter(logPath, false, Encoding.UTF8))
            using (var system = new C64System(C64Model.Pal))
            {
                writer.WriteLine("sample,globalCycle,rasterLine,cycleInLine,badLine,phi1,phi2,cpuBlocked,baPending,pc,opcode,a,x,y,sp,sr,mem,cpu,cia,sid,iec,drive8");
                WriteTraceSample(writer, system, 0);
                for (int cycle = 1; cycle <= cycles; cycle++)
                {
                    system.Tick();
                    if ((cycle % sampleInterval) == 0 || cycle == cycles)
                    {
                        WriteTraceSample(writer, system, cycle);
                    }
                }
            }
        }

        /// <summary>
        /// Runs the machine and writes structured JSONL samples for cycle-accuracy analysis.
        /// </summary>
        public static void ExportMachineTrace(int cycles, int sampleInterval, string logPath, bool accuracyProfile)
        {
            EnsureDirectory(logPath);
            sampleInterval = Math.Max(1, sampleInterval);

            C64AccuracyOptions options = accuracyProfile
                ? C64AccuracyOptions.Accuracy
                : C64AccuracyOptions.Compatibility;

            using (var writer = new StreamWriter(logPath, false, Encoding.UTF8))
            using (var system = new C64System(C64Model.Pal, options))
            {
                CpuTraceEntry lastCpuTrace = new CpuTraceEntry();
                bool hasCpuTrace = false;
                system.Cpu.TraceEnabled = true;
                system.Cpu.TraceEmitted += delegate(CpuTraceEntry entry)
                {
                    lastCpuTrace = entry;
                    hasCpuTrace = true;
                };

                WriteMachineTraceSample(writer, system, 0, false, lastCpuTrace);
                for (int cycle = 1; cycle <= cycles; cycle++)
                {
                    hasCpuTrace = false;
                    system.Tick();
                    if ((cycle % sampleInterval) == 0 || cycle == cycles)
                    {
                        WriteMachineTraceSample(writer, system, cycle, hasCpuTrace, lastCpuTrace);
                    }
                }
            }
        }

        /// <summary>
        /// Runs a PRG/D64 regression sample and writes log plus optional framebuffer PPM.
        /// </summary>
        public static void RunRegression(string mediaPath, int cycles, string logPath, string framePath)
        {
            EnsureDirectory(logPath);
            if (!string.IsNullOrWhiteSpace(framePath))
            {
                EnsureDirectory(framePath);
            }

            var log = new StringBuilder();
            log.AppendLine("C64 REGRESSION RUN");
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));
            log.AppendLine("Cycles=" + cycles.ToString(CultureInfo.InvariantCulture));
            log.AppendLine("Media=" + (mediaPath ?? string.Empty));

            try
            {
                using (var system = new C64System(C64Model.Pal))
                {
                    if (!string.IsNullOrWhiteSpace(mediaPath))
                    {
                        log.AppendLine("Mount=" + system.MountMedia(mediaPath, 8));
                        if (string.Equals(Path.GetExtension(mediaPath), ".d64", StringComparison.OrdinalIgnoreCase))
                        {
                            system.RunCycles(400000);
                            system.EnqueuePetsciiText("LOAD\"*\",8\r");
                            log.AppendLine("Command=LOAD\"*\",8");
                        }
                    }

                    system.RunCycles(Math.Max(0, cycles));
                    VicTiming timing = system.Timing;
                    log.AppendLine("FinalCycle=" + timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
                    log.AppendFormat("FinalVic=raster:{0} cycle:{1} badLine:{2}", timing.RasterLine, timing.CycleInLine, timing.BadLine).AppendLine();
                    log.AppendLine("FinalCpu=" + system.GetCpuDebugInfo());
                    log.AppendLine("FinalMem=" + system.GetMemoryConfigDebugInfo());
                    log.AppendLine("FinalCia=" + system.GetCiaDebugInfo());
                    log.AppendLine("FinalSid=" + system.GetSidDebugInfo());
                    log.AppendLine("FinalIec=" + system.GetIecDebugInfo());
                    log.AppendLine("FinalDrive8=" + system.GetDriveDebugInfo(8));
                    string frameHash = ComputeFrameHash(system.FrameBuffer);
                    log.AppendLine("FrameSHA256=" + frameHash);

                    if (!string.IsNullOrWhiteSpace(framePath))
                    {
                        WriteFrameBufferPpm(system.FrameBuffer, framePath);
                        log.AppendLine("FramePath=" + Path.GetFullPath(framePath));
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION:");
                log.AppendLine(ex.ToString());
                File.WriteAllText(logPath, log.ToString());
                throw;
            }

            File.WriteAllText(logPath, log.ToString());
        }

        /// <summary>
        /// Runs an EasyFlash CRT until a probe point, injects one frontend key, and logs input/cartridge state.
        /// </summary>
        public static void RunEasyFlashInputProbe(string crtPath, string logPath, string framePath, int preCycles, int postCycles, int followupCycles, string keyName, string joystickPortName)
        {
            EnsureDirectory(logPath);
            if (!string.IsNullOrWhiteSpace(framePath))
            {
                EnsureDirectory(framePath);
            }

            if (!Enum.TryParse(keyName, true, out Key key))
            {
                key = Key.ControlLeft;
            }

            if (!Enum.TryParse(joystickPortName, true, out JoystickPort joystickPort))
            {
                joystickPort = JoystickPort.Port2;
            }

            var log = new StringBuilder();
            log.AppendLine("EASYFLASH INPUT PROBE");
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));
            log.AppendLine("Crt=" + (crtPath ?? string.Empty));
            log.AppendLine("PreCycles=" + Math.Max(0, preCycles).ToString(CultureInfo.InvariantCulture));
            log.AppendLine("PostCycles=" + Math.Max(0, postCycles).ToString(CultureInfo.InvariantCulture));
            log.AppendLine("FollowupCycles=" + Math.Max(0, followupCycles).ToString(CultureInfo.InvariantCulture));
            log.AppendLine("Key=" + key);
            log.AppendLine("JoystickPort=" + joystickPort);

            try
            {
                using (var system = new C64System(C64Model.Pal))
                {
                    int ciaReadLogCount = 0;
                    int ciaAccessLogCount = 0;
                    int ioLogCount = 0;
                    int pcLogCount = 0;
                    int timerLoopAccessLogCount = 0;
                    const int maxCiaReadLogCount = 512;
                    const int maxCiaAccessLogCount = 1024;
                    const int maxIoLogCount = 512;
                    const int maxPcLogCount = 512;
                    const int maxTimerLoopAccessLogCount = 2048;

                    log.AppendLine("Mount=" + system.MountMedia(crtPath, 8));
                    system.SetJoystickPort(joystickPort);
                    system.RunCycles(Math.Max(0, preCycles));
                    AppendEasyFlashProbeState(log, "BeforeInput", system);

                    system.Cpu.TraceEmitted += delegate(CpuTraceEntry entry)
                    {
                        if (entry.AccessType == CpuTraceAccessType.Read &&
                            (entry.Address == 0xDC00 || entry.Address == 0xDC01) &&
                            ciaReadLogCount < maxCiaReadLogCount)
                        {
                            VicTiming timing = system.Timing;
                            log.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "CiaRead[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} addr:{5:X4} value:{6:X2}",
                                ciaReadLogCount,
                                timing.GlobalCycle,
                                timing.RasterLine,
                                timing.CycleInLine,
                                entry.LastOpcodeAddress,
                                entry.Address,
                                entry.Value);
                            log.AppendLine();
                            ciaReadLogCount++;
                        }

                        if ((entry.AccessType == CpuTraceAccessType.Read ||
                            entry.AccessType == CpuTraceAccessType.Write) &&
                            entry.Address >= 0xDC00 &&
                            entry.Address <= 0xDC0F &&
                            ciaAccessLogCount < maxCiaAccessLogCount)
                        {
                            VicTiming timing = system.Timing;
                            log.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "CiaAccess[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} access:{5} addr:{6:X4} value:{7:X2}",
                                ciaAccessLogCount,
                                timing.GlobalCycle,
                                timing.RasterLine,
                                timing.CycleInLine,
                                entry.LastOpcodeAddress,
                                entry.AccessType,
                                entry.Address,
                                entry.Value);
                            log.AppendLine();
                            ciaAccessLogCount++;
                        }

                        if (entry.AccessType == CpuTraceAccessType.Read &&
                            (entry.Address == 0xDC04 || entry.Address == 0xDC05) &&
                            entry.LastOpcodeAddress >= 0x5139 &&
                            entry.LastOpcodeAddress <= 0x5146 &&
                            (entry.Address == 0xDC04 || entry.Value <= 0x02) &&
                            timerLoopAccessLogCount < maxTimerLoopAccessLogCount)
                        {
                            VicTiming timing = system.Timing;
                            log.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "TimerLoopAccess[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} addr:{5:X4} value:{6:X2}",
                                timerLoopAccessLogCount,
                                timing.GlobalCycle,
                                timing.RasterLine,
                                timing.CycleInLine,
                                entry.LastOpcodeAddress,
                                entry.Address,
                                entry.Value);
                            log.AppendLine();
                            timerLoopAccessLogCount++;
                        }

                        if ((entry.AccessType == CpuTraceAccessType.Read ||
                            entry.AccessType == CpuTraceAccessType.Write) &&
                            entry.Address >= 0xDE00 &&
                            entry.Address <= 0xDFFF &&
                            ioLogCount < maxIoLogCount)
                        {
                            VicTiming timing = system.Timing;
                            log.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "EasyFlashIo[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} access:{5} addr:{6:X4} value:{7:X2}",
                                ioLogCount,
                                timing.GlobalCycle,
                                timing.RasterLine,
                                timing.CycleInLine,
                                entry.LastOpcodeAddress,
                                entry.AccessType,
                                entry.Address,
                                entry.Value);
                            log.AppendLine();
                            ioLogCount++;
                        }

                        if (entry.AccessType == CpuTraceAccessType.OpcodeFetch &&
                            entry.LastOpcodeAddress >= 0x5100 &&
                            entry.LastOpcodeAddress <= 0x5180 &&
                            pcLogCount < maxPcLogCount)
                        {
                            VicTiming timing = system.Timing;
                            log.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "PcLoop[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} opcode:{5:X2} instr:{6}",
                                pcLogCount,
                                timing.GlobalCycle,
                                timing.RasterLine,
                                timing.CycleInLine,
                                entry.LastOpcodeAddress,
                                entry.Opcode,
                                entry.InstructionName ?? string.Empty);
                            log.AppendLine();
                            pcLogCount++;
                        }
                    };

                    system.Cpu.TraceEnabled = true;
                    system.KeyDown(key);
                    int holdCycles = Math.Min(Math.Max(0, postCycles), 50000);
                    system.RunCycles(holdCycles);
                    AppendEasyFlashProbeState(log, "HeldInput", system);
                    system.KeyUp(key);
                    system.RunCycles(Math.Max(0, postCycles - holdCycles));
                    AppendEasyFlashProbeState(log, "AfterInput", system);

                    if (followupCycles > 0)
                    {
                        system.KeyDown(key);
                        int followupHoldCycles = Math.Min(Math.Max(0, followupCycles), 50000);
                        system.RunCycles(followupHoldCycles);
                        AppendEasyFlashProbeState(log, "HeldFollowupInput", system);
                        system.KeyUp(key);
                        system.RunCycles(Math.Max(0, followupCycles - followupHoldCycles));
                        AppendEasyFlashProbeState(log, "AfterFollowupInput", system);
                    }

                    system.Cpu.TraceEnabled = false;
                    log.AppendLine("LoggedCiaReads=" + ciaReadLogCount.ToString(CultureInfo.InvariantCulture));
                    log.AppendLine("LoggedCiaAccess=" + ciaAccessLogCount.ToString(CultureInfo.InvariantCulture));
                    log.AppendLine("LoggedEasyFlashIo=" + ioLogCount.ToString(CultureInfo.InvariantCulture));
                    log.AppendLine("LoggedPcLoop=" + pcLogCount.ToString(CultureInfo.InvariantCulture));
                    log.AppendLine("LoggedTimerLoopAccess=" + timerLoopAccessLogCount.ToString(CultureInfo.InvariantCulture));
                    log.AppendLine("FrameSHA256=" + ComputeFrameHash(system.FrameBuffer));

                    if (!string.IsNullOrWhiteSpace(framePath))
                    {
                        WriteFrameBufferPpm(system.FrameBuffer, framePath);
                        log.AppendLine("FramePath=" + Path.GetFullPath(framePath));
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION:");
                log.AppendLine(ex.ToString());
                File.WriteAllText(logPath, log.ToString());
                throw;
            }

            File.WriteAllText(logPath, log.ToString());
        }

        /// <summary>
        /// Runs a PRG from a direct machine-code entry point and writes log plus optional framebuffer PPM.
        /// </summary>
        public static void RunPrgSys(string prgPath, ushort startAddress, int cycles, string logPath, string framePath, int warmupCycles)
        {
            RunPrgSys(prgPath, startAddress, cycles, logPath, framePath, warmupCycles, string.Empty);
        }

        /// <summary>
        /// Runs a PRG from a direct machine-code entry point and writes log plus optional detailed CPU/VIC event trace.
        /// </summary>
        public static void RunPrgSys(string prgPath, ushort startAddress, int cycles, string logPath, string framePath, int warmupCycles, string traceMode)
        {
            EnsureDirectory(logPath);
            if (!string.IsNullOrWhiteSpace(framePath))
            {
                EnsureDirectory(framePath);
            }

            var log = new StringBuilder();
            log.AppendLine("C64 PRG SYS RUN");
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));
            log.AppendLine("Prg=" + (prgPath ?? string.Empty));
            log.AppendLine("StartAddress=" + startAddress.ToString("X4", CultureInfo.InvariantCulture));
            log.AppendLine("WarmupCycles=" + warmupCycles.ToString(CultureInfo.InvariantCulture));
            log.AppendLine("Cycles=" + cycles.ToString(CultureInfo.InvariantCulture));
            log.AppendLine("TraceMode=" + (traceMode ?? string.Empty));

            try
            {
                using (var system = new C64System(C64Model.Pal, C64AccuracyOptions.Accuracy))
                {
                    const int maxVicWriteLogCount = 4096;
                    const int maxDetailedTraceLogCount = 20000;
                    int vicWriteLogCount = 0;
                    int detailedTraceLogCount = 0;
                    bool detailedTrace = string.Equals(traceMode, "detailed", StringComparison.OrdinalIgnoreCase);
                    system.Cpu.TraceEnabled = false;
                    system.Cpu.TraceEmitted += delegate(CpuTraceEntry entry)
                    {
                        if (detailedTrace &&
                            detailedTraceLogCount < maxDetailedTraceLogCount &&
                            ShouldLogDetailedPrgTrace(entry))
                        {
                            VicTiming timing = system.Timing;
                            CpuBusAccessPrediction prediction = system.LastCpuBusPrediction;
                            log.AppendFormat(
                                "Trace[{0}]=global:{1} raster:{2} cycle:{3} cpuCycle:{4} state:{5}->{6} step:{7}->{8} pc:{9:X4}->{10:X4} op:{11:X2} instr:{12} access:{13} addr:{14:X4} value:{15:X2} ba:{16} aec:{17} cpuCan:{18} vicCan:{19} pred:{20}:{21:X4}:{22:X2} a:{23:X2}->{24:X2} x:{25:X2}->{26:X2} y:{27:X2}->{28:X2} sr:{29:X2}->{30:X2}",
                                detailedTraceLogCount,
                                timing.GlobalCycle,
                                timing.RasterLine,
                                timing.CycleInLine,
                                entry.Cycle,
                                entry.StateBefore,
                                entry.StateAfter,
                                entry.StepIndexBefore,
                                entry.StepIndexAfter,
                                entry.PcBefore,
                                entry.PcAfter,
                                entry.Opcode,
                                entry.InstructionName ?? string.Empty,
                                entry.AccessType,
                                entry.Address,
                                entry.Value,
                                system.BaLow,
                                system.AecLow,
                                system.CpuCanAccess,
                                system.VicCanAccess,
                                prediction.AccessType,
                                prediction.Address,
                                prediction.Value,
                                entry.ABefore,
                                entry.AAfter,
                                entry.XBefore,
                                entry.XAfter,
                                entry.YBefore,
                                entry.YAfter,
                                entry.SrBefore,
                                entry.SrAfter);
                            AppendVicPipelineSummary(log, system);
                            log.AppendLine();
                            detailedTraceLogCount++;
                        }

                        if (entry.AccessType != CpuTraceAccessType.Write || vicWriteLogCount >= maxVicWriteLogCount)
                        {
                            return;
                        }

                        if (entry.Address < 0xD000 || entry.Address > 0xD02E)
                        {
                            return;
                        }

                        VicTiming writeTiming = system.Timing;
                        log.AppendFormat(
                            "VicWrite[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} addr:{5:X4} value:{6:X2}",
                            vicWriteLogCount,
                            writeTiming.GlobalCycle,
                            writeTiming.RasterLine,
                            writeTiming.CycleInLine,
                            entry.LastOpcodeAddress,
                            entry.Address,
                            entry.Value);
                        AppendVicPipelineSummary(log, system);
                        log.AppendLine();
                        vicWriteLogCount++;
                    };

                    if (warmupCycles > 0)
                    {
                        system.RunCycles(warmupCycles);
                    }

                    log.AppendLine("Mount=" + system.MountMedia(prgPath, 8));
                    system.Cpu.StartAt(startAddress);
                    system.Cpu.TraceEnabled = true;
                    log.AppendLine("TraceEnabledAtCycle=" + system.Timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
                    system.RunCycles(Math.Max(0, cycles));

                    VicTiming timing = system.Timing;
                    log.AppendLine("FinalCycle=" + timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
                    log.AppendFormat("FinalVic=raster:{0} cycle:{1} badLine:{2}", timing.RasterLine, timing.CycleInLine, timing.BadLine).AppendLine();
                    log.AppendLine("FinalCpu=" + system.GetCpuDebugInfo());
                    log.AppendLine("FinalMem=" + system.GetMemoryConfigDebugInfo());
                    log.AppendLine("DetailedTraceEntries=" + detailedTraceLogCount.ToString(CultureInfo.InvariantCulture));
                    string frameHash = ComputeFrameHash(system.FrameBuffer);
                    log.AppendLine("FrameSHA256=" + frameHash);

                    if (!string.IsNullOrWhiteSpace(framePath))
                    {
                        WriteFrameBufferPpm(system.FrameBuffer, framePath);
                        log.AppendLine("FramePath=" + Path.GetFullPath(framePath));
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION:");
                log.AppendLine(ex.ToString());
                File.WriteAllText(logPath, log.ToString());
                throw;
            }

            File.WriteAllText(logPath, log.ToString());
        }

        /// <summary>
        /// Appends compact machine, CIA, and EasyFlash state to an input probe log.
        /// </summary>
        private static void AppendEasyFlashProbeState(StringBuilder log, string label, C64System system)
        {
            VicTiming timing = system.Timing;
            log.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}=global:{1} raster:{2} cycle:{3}",
                label,
                timing.GlobalCycle,
                timing.RasterLine,
                timing.CycleInLine);
            log.AppendLine();
            log.AppendLine(label + "Cpu=" + system.GetCpuDebugInfo());
            log.AppendLine(label + "Mem=" + system.GetMemoryConfigDebugInfo());
            log.AppendLine(label + "Cia=" + system.GetCiaDebugInfo());
            log.AppendLine(label + "EasyFlash=" + system.GetEasyFlashDebugInfo());
            log.AppendLine(label + "PcBytes=" + FormatCpuBytes(system, system.Cpu.PC, 24));
            log.AppendLine(label + "Vectors=" + FormatCpuBytes(system, 0x0314, 8));
            log.AppendLine(label + "TimerTarget=" + FormatCpuBytes(system, 0x0063, 2));
            log.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}InputRead=dc00:{1:X2} dc01:{2:X2}",
                label,
                system.Peek(0xDC00),
                system.Peek(0xDC01));
            log.AppendLine();
        }

        /// <summary>
        /// Formats mapped CPU-visible bytes for compact probe diagnostics.
        /// </summary>
        private static string FormatCpuBytes(C64System system, ushort startAddress, int count)
        {
            var builder = new StringBuilder();
            builder.Append(startAddress.ToString("X4", CultureInfo.InvariantCulture));
            builder.Append(":");
            for (int index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(' ');
                }

                ushort address = (ushort)(startAddress + index);
                builder.Append(system.Peek(address).ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Returns whether a CPU trace row is useful for PRG/VIC timing diagnosis.
        /// </summary>
        private static bool ShouldLogDetailedPrgTrace(CpuTraceEntry entry)
        {
            if (entry.StateBefore == CpuState.InterruptSequence ||
                entry.StateAfter == CpuState.InterruptSequence ||
                string.Equals(entry.InstructionName, "IRQ", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.InstructionName, "NMI", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (entry.AccessType == CpuTraceAccessType.None)
            {
                return false;
            }

            ushort address = entry.Address;
            if (address == 0xD7FF)
            {
                return true;
            }

            if (address >= 0x2001 && address <= 0x2007)
            {
                return true;
            }

            if (address >= 0xD000 && address <= 0xD02E)
            {
                return true;
            }

            if (address >= 0xDC00 && address <= 0xDD0F)
            {
                return true;
            }

            if (address >= 0x0314 && address <= 0x0319)
            {
                return true;
            }

            if (address >= 0xFFFA && address <= 0xFFFF)
            {
                return true;
            }

            return address >= 0x0100 && address <= 0x01FF &&
                (entry.StateBefore == CpuState.InterruptSequence ||
                    entry.StateAfter == CpuState.InterruptSequence);
        }

        /// <summary>
        /// Appends the compact VIC-II graphics pipeline state used by PRG timing traces.
        /// </summary>
        private static void AppendVicPipelineSummary(StringBuilder log, C64System system)
        {
            VicPipelineState state = system.VicPipeline;
            log.AppendFormat(
                CultureInfo.InvariantCulture,
                " vicDisplay:{0} badCond:{1}:{2} vc:{3:X3} vcBase:{4:X3} rc:{5} vmli:{6} mcol:{7} pcol:{8} vco:{9} mask:{10:X10}",
                state.GraphicsDisplayState ? 1 : 0,
                state.BadLineConditionThisCycle ? 1 : 0,
                state.BadLineConditionStartCycle,
                state.GraphicsVc,
                state.GraphicsVcBase,
                state.GraphicsRc,
                state.GraphicsVmli,
                state.GraphicsMatrixFetchColumn,
                state.GraphicsPatternFetchColumn,
                state.GraphicsVideoCounterOffset,
                state.GraphicsVmliShiftRegister);
            log.AppendFormat(
                CultureInfo.InvariantCulture,
                " matrixFetch:{0}:{1}/{2}/{3} line:{4}/{5} matrixValid:{6}:{7} patternValid:{8}:{9}/{10} modes:{11}{12}{13}",
                state.MatrixFetchStartedThisLine ? 1 : 0,
                state.MatrixFetchRequestStartCycle,
                state.MatrixFetchStartCycle,
                state.MatrixFetchCpuBlockStartCycle,
                state.GraphicsLineCellY,
                state.GraphicsLinePixelRow,
                state.VideoMatrixValid ? 1 : 0,
                state.VideoMatrixCellY,
                state.VideoPatternValid ? 1 : 0,
                state.VideoPatternCellY,
                state.VideoPatternPixelRow,
                state.LineBitmapMode ? "B" : "b",
                state.LineExtendedColorMode ? "E" : "e",
                state.LineMulticolorMode ? "M" : "m");
            log.AppendFormat(
                CultureInfo.InvariantCulture,
                " regs:d011={0:X2}/{1:X2} d016={2:X2}/{3:X2} line:{4}{5} border:{6}{7}",
                state.RegisterD011,
                state.PixelD011,
                state.RegisterD016,
                state.PixelD016,
                state.Line40Column ? "40" : "38",
                state.Line25Row ? "x25" : "x24",
                state.HorizontalBorderActive ? "H" : "h",
                state.VerticalBorderActive ? "V" : "v");
            log.AppendFormat(
                CultureInfo.InvariantCulture,
                " spr3:dma{0}{1} flip:{2} y:{3}{4} d017:{5:X2}/{6:X2} mc:{7}/{8} ph:{9} start:{10} row:{11}{12}/{13}{14} line:{15}{16}:{17}{18} data:{19:X2}{20:X2}{21:X2}",
                state.Sprite3DmaActive ? 1 : 0,
                state.Sprite3DmaLatched ? 1 : 0,
                state.Sprite3ExpandFlipFlop ? 1 : 0,
                state.Sprite3LatchedYExpanded ? 1 : 0,
                state.Sprite3LineYExpanded ? 1 : 0,
                state.Sprite3RegisterD017,
                state.Sprite3PixelD017,
                state.Sprite3Mc,
                state.Sprite3McBase,
                state.Sprite3FetchPhase,
                state.Sprite3FetchStartMc,
                state.Sprite3FetchRow,
                state.Sprite3FetchRowAdjusted ? "*" : string.Empty,
                state.Sprite3DisplayRow,
                state.Sprite3DisplayRowAdjusted ? "*" : string.Empty,
                state.Sprite3LineVisible ? 1 : 0,
                state.Sprite3LineDataValid ? 1 : 0,
                state.Sprite3LineDisplayRow,
                state.Sprite3LineDisplayRowAdjusted ? "*" : string.Empty,
                state.Sprite3LineDataByte0,
                state.Sprite3LineDataByte1,
                state.Sprite3LineDataByte2);
            if (state.PendingGraphicsDisplayState)
            {
                log.AppendFormat(
                    CultureInfo.InvariantCulture,
                    " pendingDisplay:{0}",
                    state.PendingGraphicsDisplayStateCycle);
            }

        }

        /// <summary>
        /// Loads a savestate, advances it, and writes a framebuffer artifact plus log.
        /// </summary>
        public static void RenderSavestate(string savePath, int frames, string logPath, string framePath)
        {
            EnsureDirectory(logPath);
            if (!string.IsNullOrWhiteSpace(framePath))
            {
                EnsureDirectory(framePath);
            }

            var log = new StringBuilder();
            log.AppendLine("C64 SAVESTATE RENDER");
            log.AppendLine("Started=" + DateTime.Now.ToString("O"));
            log.AppendLine("Save=" + Path.GetFullPath(savePath));
            log.AppendLine("Frames=" + frames.ToString(CultureInfo.InvariantCulture));

            using (var system = new C64System(C64Model.Pal))
            {
                SaveStateFile.Load(savePath, system);
                bool traceVicWrites = string.Equals(
                    Environment.GetEnvironmentVariable("C64_TRACE_RENDER_SAVESTATE"),
                    "1",
                    StringComparison.Ordinal);
                int vicWriteLogCount = 0;
                if (traceVicWrites)
                {
                    system.Cpu.TraceEnabled = true;
                    system.Cpu.TraceEmitted += delegate(CpuTraceEntry entry)
                    {
                        if (entry.AccessType != CpuTraceAccessType.Write ||
                            entry.Address < 0xD000 ||
                            entry.Address > 0xD03F ||
                            vicWriteLogCount >= 4096)
                        {
                            return;
                        }

                        VicTiming writeTiming = system.Timing;
                        log.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "VicWrite[{0}]=global:{1} raster:{2} cycle:{3} pc:{4:X4} addr:{5:X4} value:{6:X2}",
                            vicWriteLogCount,
                            writeTiming.GlobalCycle,
                            writeTiming.RasterLine,
                            writeTiming.CycleInLine,
                            entry.LastOpcodeAddress,
                            entry.Address,
                            entry.Value);
                        AppendVicPipelineSummary(log, system);
                        log.AppendLine();
                        vicWriteLogCount++;
                    };
                }

                int safeFrames = Math.Max(0, frames);
                int cycles = C64Model.Pal.RasterLines * C64Model.Pal.CyclesPerLine * safeFrames;
                RenderSavestateRunOptions runOptions = ReadRenderSavestateRunOptions(log);
                if (runOptions.HasAnyOption)
                {
                    RunSavestateWithOptions(system, cycles, runOptions, log);
                }
                else
                {
                    system.RunCycles(cycles);
                }

                VicTiming timing = system.Timing;
                log.AppendLine("FinalCycle=" + timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
                log.AppendFormat("FinalVic=raster:{0} cycle:{1} badLine:{2}", timing.RasterLine, timing.CycleInLine, timing.BadLine).AppendLine();
                log.AppendLine("FinalCpu=" + system.GetCpuDebugInfo());
                log.AppendLine("FinalMem=" + system.GetMemoryConfigDebugInfo());
                log.AppendLine("FinalCia=" + system.GetCiaDebugInfo());
                log.AppendLine("FinalSid=" + system.GetSidDebugInfo());
                log.AppendLine("FinalIec=" + system.GetIecDebugInfo());
                log.AppendLine("FinalDrive8=" + system.GetDriveDebugInfo(8));
                log.AppendLine("FrameSHA256=" + ComputePixelHash(system.FrameBuffer.Width, system.FrameBuffer.Height, system.FrameBuffer.CompletedPixels));

                if (!string.IsNullOrWhiteSpace(framePath))
                {
                    WritePixelsPpm(system.FrameBuffer.Width, system.FrameBuffer.Height, system.FrameBuffer.CompletedPixels, framePath);
                    log.AppendLine("FramePath=" + Path.GetFullPath(framePath));
                }
            }

            File.WriteAllText(logPath, log.ToString());
        }

        /// <summary>
        /// Reads optional environment-controlled savestate render probes.
        /// </summary>
        private static RenderSavestateRunOptions ReadRenderSavestateRunOptions(StringBuilder log)
        {
            var options = new RenderSavestateRunOptions();

            string firePort = Environment.GetEnvironmentVariable("C64_RENDER_JOYSTICK_FIRE_PORT");
            if (!string.IsNullOrWhiteSpace(firePort) && Enum.TryParse(firePort, true, out JoystickPort joystickPort))
            {
                options.JoystickFirePort = joystickPort;
                options.HasJoystickFire = true;
                options.JoystickFireStartCycle = Math.Max(0, ParseIntEnvironment("C64_RENDER_JOYSTICK_FIRE_START_CYCLE", 0));
                options.JoystickFireCycles = Math.Max(1, ParseIntEnvironment("C64_RENDER_JOYSTICK_FIRE_CYCLES", C64Model.Pal.RasterLines * C64Model.Pal.CyclesPerLine));
                log.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "InjectedJoystick={0}Fire start={1} cycles={2}",
                    joystickPort,
                    options.JoystickFireStartCycle,
                    options.JoystickFireCycles);
                log.AppendLine();
            }

            options.SampleIntervalCycles = Math.Max(0, ParseIntEnvironment("C64_RENDER_SAMPLE_CYCLES", 0));
            if (options.SampleIntervalCycles > 0)
            {
                log.AppendLine("RenderSamplesEveryCycles=" + options.SampleIntervalCycles.ToString(CultureInfo.InvariantCulture));
            }

            options.TraceCpu = string.Equals(Environment.GetEnvironmentVariable("C64_RENDER_CPU_TRACE"), "1", StringComparison.Ordinal);
            if (options.TraceCpu)
            {
                options.TraceCpuStart = ParseUshortEnvironment("C64_RENDER_CPU_TRACE_START", 0x0000);
                options.TraceCpuEnd = ParseUshortEnvironment("C64_RENDER_CPU_TRACE_END", 0xFFFF);
                options.TraceCpuLimit = Math.Max(1, ParseIntEnvironment("C64_RENDER_CPU_TRACE_LIMIT", 4096));
                log.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "RenderCpuTrace={0:X4}-{1:X4} limit={2}",
                    options.TraceCpuStart,
                    options.TraceCpuEnd,
                    options.TraceCpuLimit);
                log.AppendLine();
            }

            return options;
        }

        /// <summary>
        /// Runs a savestate render with optional input and trace probes.
        /// </summary>
        private static void RunSavestateWithOptions(C64System system, int cycles, RenderSavestateRunOptions options, StringBuilder log)
        {
            int traceCount = 0;
            if (options.TraceCpu)
            {
                system.Cpu.TraceEnabled = true;
                system.Cpu.TraceEmitted += delegate(CpuTraceEntry entry)
                {
                    if (traceCount >= options.TraceCpuLimit ||
                        entry.LastOpcodeAddress < options.TraceCpuStart ||
                        entry.LastOpcodeAddress > options.TraceCpuEnd)
                    {
                        return;
                    }

                    log.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "CpuTrace[{0}]=cy:{1} pc:{2:X4}->{3:X4} op:{4:X2} instr:{5} a:{6:X2}->{7:X2} x:{8:X2}->{9:X2} y:{10:X2}->{11:X2} sp:{12:X2}->{13:X2} sr:{14:X2}->{15:X2} acc:{16} addr:{17:X4} val:{18:X2}",
                        traceCount,
                        entry.Cycle,
                        entry.PcBefore,
                        entry.PcAfter,
                        entry.Opcode,
                        entry.InstructionName ?? string.Empty,
                        entry.ABefore,
                        entry.AAfter,
                        entry.XBefore,
                        entry.XAfter,
                        entry.YBefore,
                        entry.YAfter,
                        entry.SpBefore,
                        entry.SpAfter,
                        entry.SrBefore,
                        entry.SrAfter,
                        entry.AccessType,
                        entry.Address,
                        entry.Value);
                    log.AppendLine();
                    traceCount++;
                };
            }

            bool fireDown = false;
            int fireEndCycle = options.JoystickFireStartCycle + options.JoystickFireCycles;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                if (options.HasJoystickFire)
                {
                    bool shouldHoldFire = cycle >= options.JoystickFireStartCycle && cycle < fireEndCycle;
                    if (shouldHoldFire != fireDown)
                    {
                        fireDown = shouldHoldFire;
                        system.SetJoystickPort(options.JoystickFirePort);
                        system.SetGamepadJoystickState(fireDown ? (byte)0x0F : (byte)0x1F);
                        log.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "JoystickFire {0} cycle={1}",
                            fireDown ? "down" : "up",
                            cycle);
                        log.AppendLine();
                    }
                }

                system.Tick();
                if (options.SampleIntervalCycles > 0 &&
                    ((cycle + 1) % options.SampleIntervalCycles == 0 || cycle + 1 == cycles))
                {
                    log.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "Sample cycle={0} cpu={1} iec={2} drive8={3}",
                        cycle + 1,
                        system.GetCpuDebugInfo(),
                        system.GetIecDebugInfo(),
                        system.GetDriveDebugInfo(8));
                    log.AppendLine();
                }
            }

            if (fireDown)
            {
                system.SetGamepadJoystickState(0x1F);
            }
        }

        /// <summary>
        /// Parses an integer environment variable.
        /// </summary>
        private static int ParseIntEnvironment(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        /// <summary>
        /// Parses a 16-bit hexadecimal-friendly environment variable.
        /// </summary>
        private static ushort ParseUshortEnvironment(string name, ushort fallback)
        {
            int parsed = ParseIntEnvironment(name, fallback);
            if (parsed < ushort.MinValue || parsed > ushort.MaxValue)
            {
                return fallback;
            }

            return (ushort)parsed;
        }

        /// <summary>
        /// Stores environment-controlled savestate render probe settings.
        /// </summary>
        private sealed class RenderSavestateRunOptions
        {
            public bool HasJoystickFire;
            public JoystickPort JoystickFirePort;
            public int JoystickFireStartCycle;
            public int JoystickFireCycles;
            public int SampleIntervalCycles;
            public bool TraceCpu;
            public ushort TraceCpuStart;
            public ushort TraceCpuEnd;
            public int TraceCpuLimit;

            public bool HasAnyOption
            {
                get { return HasJoystickFire || SampleIntervalCycles > 0 || TraceCpu; }
            }
        }

        /// <summary>
        /// Writes a framebuffer as binary PPM (P6).
        /// </summary>
        public static void WriteFrameBufferPpm(FrameBuffer frameBuffer, string path)
        {
            WritePixelsPpm(frameBuffer.Width, frameBuffer.Height, frameBuffer.Pixels, path);
        }

        /// <summary>
        /// Writes ARGB pixels as binary PPM (P6).
        /// </summary>
        private static void WritePixelsPpm(int width, int height, uint[] pixels, string path)
        {
            EnsureDirectory(path);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                byte[] header = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "P6\n{0} {1}\n255\n", width, height));
                stream.Write(header, 0, header.Length);
                for (int index = 0; index < pixels.Length; index++)
                {
                    uint pixel = pixels[index];
                    stream.WriteByte((byte)((pixel >> 16) & 0xFF));
                    stream.WriteByte((byte)((pixel >> 8) & 0xFF));
                    stream.WriteByte((byte)(pixel & 0xFF));
                }
            }
        }

        /// <summary>
        /// Computes a stable SHA-256 hash over framebuffer dimensions and pixels.
        /// </summary>
        public static string ComputeFrameHash(FrameBuffer frameBuffer)
        {
            return ComputePixelHash(frameBuffer.Width, frameBuffer.Height, frameBuffer.Pixels);
        }

        /// <summary>
        /// Computes a stable SHA-256 hash over framebuffer dimensions and the given pixels.
        /// </summary>
        private static string ComputePixelHash(int width, int height, uint[] pixels)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(width);
                writer.Write(height);
                for (int index = 0; index < pixels.Length; index++)
                {
                    writer.Write(pixels[index]);
                }

                writer.Flush();
                byte[] hash = sha256.ComputeHash(stream.ToArray());
                return ToHex(hash);
            }
        }

        private static void WriteTraceSample(StreamWriter writer, C64System system, int sample)
        {
            VicTiming timing = system.Timing;
            writer.Write(sample.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(timing.RasterLine.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(timing.CycleInLine.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(timing.BadLine ? "1" : "0");
            writer.Write(',');
            writer.Write(timing.Phi1Action);
            writer.Write(',');
            writer.Write(timing.Phi2Action);
            writer.Write(',');
            writer.Write(timing.CpuBlocked ? "1" : "0");
            writer.Write(',');
            writer.Write(timing.BusRequestPending ? "1" : "0");
            writer.Write(',');
            writer.Write(system.Cpu.PC.ToString("X4", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(system.Cpu.CurrentOpcode.ToString("X2", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(system.Cpu.A.ToString("X2", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(system.Cpu.X.ToString("X2", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(system.Cpu.Y.ToString("X2", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(system.Cpu.SP.ToString("X2", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(system.Cpu.SR.ToString("X2", CultureInfo.InvariantCulture));
            writer.Write(',');
            WriteCsvField(writer, system.GetMemoryConfigDebugInfo());
            writer.Write(',');
            WriteCsvField(writer, system.GetCpuDebugInfo());
            writer.Write(',');
            WriteCsvField(writer, system.GetCiaDebugInfo());
            writer.Write(',');
            WriteCsvField(writer, system.GetSidDebugInfo());
            writer.Write(',');
            WriteCsvField(writer, system.GetIecDebugInfo());
            writer.Write(',');
            WriteCsvField(writer, system.GetDriveDebugInfo(8));
            writer.WriteLine();
        }

        private static void WriteMachineTraceSample(StreamWriter writer, C64System system, int sample, bool hasCpuTrace, CpuTraceEntry cpuTrace)
        {
            VicTiming timing = system.Timing;
            CpuBusAccessPrediction prediction = system.LastCpuBusPrediction;
            var entry = new MachineCycleTraceEntry
            {
                Sample = sample,
                GlobalCycle = timing.GlobalCycle,
                RasterLine = timing.RasterLine,
                CycleInLine = timing.CycleInLine,
                BeamX = timing.BeamX,
                BeamY = timing.BeamY,
                BadLine = timing.BadLine,
                Phi1Action = timing.Phi1Action.ToString(),
                Phi2Action = timing.Phi2Action.ToString(),
                BaLow = system.BaLow,
                AecLow = system.AecLow,
                CpuCanAccess = system.CpuCanAccess,
                VicCanAccess = system.VicCanAccess,
                BusOwner = system.BusOwner.ToString(),
                CpuBlocked = timing.CpuBlocked,
                BusRequestPending = timing.BusRequestPending,
                PredictedCpuAccessType = prediction.AccessType.ToString(),
                PredictedCpuAddress = prediction.Address.ToString("X4", CultureInfo.InvariantCulture),
                PredictedCpuValue = prediction.Value.ToString("X2", CultureInfo.InvariantCulture),
                Cpu = hasCpuTrace ? ConvertCpuTrace(cpuTrace) : SnapshotCpu(system),
                Vic = ConvertVicPipeline(system.VicPipeline),
                Drive8Scheduler = ConvertDriveScheduler(system.GetDriveSchedulerState(8)),
                Memory = system.GetMemoryConfigDebugInfo(),
                Cia = system.GetCiaDebugInfo(),
                Sid = system.GetSidDebugInfo(),
                Iec = system.GetIecDebugInfo(),
                Drive8 = system.GetDriveDebugInfo(8)
            };

            writer.Write(JsonSerializer.Serialize(entry));
            writer.WriteLine();
        }

        private static MachineDriveSchedulerTraceEntry ConvertDriveScheduler(DriveSchedulerState state)
        {
            return new MachineDriveSchedulerTraceEntry
            {
                DeviceNumber = state.DeviceNumber,
                TargetCycles = state.TargetCycles,
                ExecutedCycles = state.ExecutedCycles,
                NeedsClockTick = state.NeedsClockTick,
                RunHardwareContinuously = state.RunHardwareContinuously,
                ProgramCounter = state.ProgramCounter.ToString("X4", CultureInfo.InvariantCulture),
                HasCustomCodeActive = state.HasCustomCodeActive,
                IsHardwareTransportReady = state.IsHardwareTransportReady
            };
        }

        private static MachineVicPipelineTraceEntry ConvertVicPipeline(VicPipelineState state)
        {
            return new MachineVicPipelineTraceEntry
            {
                GraphicsDisplayState = state.GraphicsDisplayState,
                PendingGraphicsDisplayState = state.PendingGraphicsDisplayState,
                PendingGraphicsDisplayStateCycle = state.PendingGraphicsDisplayStateCycle,
                BadLineConditionThisCycle = state.BadLineConditionThisCycle,
                BadLineConditionStartCycle = state.BadLineConditionStartCycle,
                MatrixFetchStartedThisLine = state.MatrixFetchStartedThisLine,
                MatrixFetchRequestStartCycle = state.MatrixFetchRequestStartCycle,
                MatrixFetchStartCycle = state.MatrixFetchStartCycle,
                MatrixFetchCpuBlockStartCycle = state.MatrixFetchCpuBlockStartCycle,
                VideoMatrixValid = state.VideoMatrixValid,
                VideoMatrixCellY = state.VideoMatrixCellY,
                VideoPatternValid = state.VideoPatternValid,
                VideoPatternCellY = state.VideoPatternCellY,
                VideoPatternPixelRow = state.VideoPatternPixelRow,
                GraphicsVc = state.GraphicsVc,
                GraphicsVcBase = state.GraphicsVcBase,
                GraphicsVmli = state.GraphicsVmli,
                GraphicsMatrixFetchColumn = state.GraphicsMatrixFetchColumn,
                GraphicsPatternFetchColumn = state.GraphicsPatternFetchColumn,
                GraphicsVideoCounterOffset = state.GraphicsVideoCounterOffset,
                GraphicsVmliShiftRegister = state.GraphicsVmliShiftRegister.ToString("X10", CultureInfo.InvariantCulture),
                GraphicsRc = state.GraphicsRc,
                GraphicsLineCellY = state.GraphicsLineCellY,
                GraphicsLinePixelRow = state.GraphicsLinePixelRow,
                LineDisplayEnabled = state.LineDisplayEnabled,
                LineBitmapMode = state.LineBitmapMode,
                LineExtendedColorMode = state.LineExtendedColorMode,
                LineMulticolorMode = state.LineMulticolorMode,
                LineXScroll = state.LineXScroll,
                LineYScroll = state.LineYScroll,
                DisplaySourceScreenBase = state.DisplaySourceScreenBase.ToString("X4", CultureInfo.InvariantCulture),
                DisplaySourceCharacterBase = state.DisplaySourceCharacterBase.ToString("X4", CultureInfo.InvariantCulture),
                DisplaySourceBitmapBase = state.DisplaySourceBitmapBase.ToString("X4", CultureInfo.InvariantCulture)
            };
        }

        private static MachineCpuTraceEntry ConvertCpuTrace(CpuTraceEntry trace)
        {
            return new MachineCpuTraceEntry
            {
                Cycle = trace.Cycle,
                StateBefore = trace.StateBefore.ToString(),
                StateAfter = trace.StateAfter.ToString(),
                Instruction = trace.InstructionName,
                Opcode = trace.Opcode.ToString("X2", CultureInfo.InvariantCulture),
                LastOpcodeAddress = trace.LastOpcodeAddress.ToString("X4", CultureInfo.InvariantCulture),
                StepIndexBefore = trace.StepIndexBefore,
                StepIndexAfter = trace.StepIndexAfter,
                PcBefore = trace.PcBefore.ToString("X4", CultureInfo.InvariantCulture),
                PcAfter = trace.PcAfter.ToString("X4", CultureInfo.InvariantCulture),
                ABefore = trace.ABefore.ToString("X2", CultureInfo.InvariantCulture),
                AAfter = trace.AAfter.ToString("X2", CultureInfo.InvariantCulture),
                XBefore = trace.XBefore.ToString("X2", CultureInfo.InvariantCulture),
                XAfter = trace.XAfter.ToString("X2", CultureInfo.InvariantCulture),
                YBefore = trace.YBefore.ToString("X2", CultureInfo.InvariantCulture),
                YAfter = trace.YAfter.ToString("X2", CultureInfo.InvariantCulture),
                SpBefore = trace.SpBefore.ToString("X2", CultureInfo.InvariantCulture),
                SpAfter = trace.SpAfter.ToString("X2", CultureInfo.InvariantCulture),
                SrBefore = trace.SrBefore.ToString("X2", CultureInfo.InvariantCulture),
                SrAfter = trace.SrAfter.ToString("X2", CultureInfo.InvariantCulture),
                AccessType = trace.AccessType.ToString(),
                Address = trace.Address.ToString("X4", CultureInfo.InvariantCulture),
                Value = trace.Value.ToString("X2", CultureInfo.InvariantCulture)
            };
        }

        private static MachineCpuTraceEntry SnapshotCpu(C64System system)
        {
            return new MachineCpuTraceEntry
            {
                Cycle = system.Cpu.CpuCycleCount,
                StateBefore = system.Cpu.State.ToString(),
                StateAfter = system.Cpu.State.ToString(),
                Instruction = system.Cpu.CurrentInstructionName,
                Opcode = system.Cpu.CurrentOpcode.ToString("X2", CultureInfo.InvariantCulture),
                LastOpcodeAddress = system.Cpu.LastOpcodeAddress.ToString("X4", CultureInfo.InvariantCulture),
                PcBefore = system.Cpu.PC.ToString("X4", CultureInfo.InvariantCulture),
                PcAfter = system.Cpu.PC.ToString("X4", CultureInfo.InvariantCulture),
                ABefore = system.Cpu.A.ToString("X2", CultureInfo.InvariantCulture),
                AAfter = system.Cpu.A.ToString("X2", CultureInfo.InvariantCulture),
                XBefore = system.Cpu.X.ToString("X2", CultureInfo.InvariantCulture),
                XAfter = system.Cpu.X.ToString("X2", CultureInfo.InvariantCulture),
                YBefore = system.Cpu.Y.ToString("X2", CultureInfo.InvariantCulture),
                YAfter = system.Cpu.Y.ToString("X2", CultureInfo.InvariantCulture),
                SpBefore = system.Cpu.SP.ToString("X2", CultureInfo.InvariantCulture),
                SpAfter = system.Cpu.SP.ToString("X2", CultureInfo.InvariantCulture),
                SrBefore = system.Cpu.SR.ToString("X2", CultureInfo.InvariantCulture),
                SrAfter = system.Cpu.SR.ToString("X2", CultureInfo.InvariantCulture),
                AccessType = CpuTraceAccessType.None.ToString(),
                Address = "0000",
                Value = "00"
            };
        }

        private static void WriteCsvField(TextWriter writer, string value)
        {
            value = value ?? string.Empty;
            writer.Write('"');
            writer.Write(value.Replace("\"", "\"\""));
            writer.Write('"');
        }

        private static void EnsureDirectory(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
