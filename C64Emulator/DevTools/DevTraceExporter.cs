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
        /// Runs a PRG from a direct machine-code entry point and writes log plus optional framebuffer PPM.
        /// </summary>
        public static void RunPrgSys(string prgPath, ushort startAddress, int cycles, string logPath, string framePath, int warmupCycles)
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

            try
            {
                using (var system = new C64System(C64Model.Pal, C64AccuracyOptions.Accuracy))
                {
                    const int maxVicWriteLogCount = 4096;
                    int vicWriteLogCount = 0;
                    system.Cpu.TraceEnabled = true;
                    system.Cpu.TraceEmitted += delegate(CpuTraceEntry entry)
                    {
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
                            entry.Value).AppendLine();
                        vicWriteLogCount++;
                    };

                    if (warmupCycles > 0)
                    {
                        system.RunCycles(warmupCycles);
                    }

                    log.AppendLine("Mount=" + system.MountMedia(prgPath, 8));
                    system.Cpu.StartAt(startAddress);
                    system.RunCycles(Math.Max(0, cycles));

                    VicTiming timing = system.Timing;
                    log.AppendLine("FinalCycle=" + timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
                    log.AppendFormat("FinalVic=raster:{0} cycle:{1} badLine:{2}", timing.RasterLine, timing.CycleInLine, timing.BadLine).AppendLine();
                    log.AppendLine("FinalCpu=" + system.GetCpuDebugInfo());
                    log.AppendLine("FinalMem=" + system.GetMemoryConfigDebugInfo());
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
                int safeFrames = Math.Max(0, frames);
                int cycles = C64Model.Pal.RasterLines * C64Model.Pal.CyclesPerLine * safeFrames;
                system.RunCycles(cycles);

                VicTiming timing = system.Timing;
                log.AppendLine("FinalCycle=" + timing.GlobalCycle.ToString(CultureInfo.InvariantCulture));
                log.AppendFormat("FinalVic=raster:{0} cycle:{1} badLine:{2}", timing.RasterLine, timing.CycleInLine, timing.BadLine).AppendLine();
                log.AppendLine("FinalCpu=" + system.GetCpuDebugInfo());
                log.AppendLine("FinalMem=" + system.GetMemoryConfigDebugInfo());
                log.AppendLine("FinalCia=" + system.GetCiaDebugInfo());
                log.AppendLine("FinalSid=" + system.GetSidDebugInfo());
                log.AppendLine("FinalIec=" + system.GetIecDebugInfo());
                log.AppendLine("FinalDrive8=" + system.GetDriveDebugInfo(8));
                log.AppendLine("FrameSHA256=" + ComputeFrameHash(system.FrameBuffer));

                if (!string.IsNullOrWhiteSpace(framePath))
                {
                    WriteFrameBufferPpm(system.FrameBuffer, framePath);
                    log.AppendLine("FramePath=" + Path.GetFullPath(framePath));
                }
            }

            File.WriteAllText(logPath, log.ToString());
        }

        /// <summary>
        /// Writes a framebuffer as binary PPM (P6).
        /// </summary>
        public static void WriteFrameBufferPpm(FrameBuffer frameBuffer, string path)
        {
            EnsureDirectory(path);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                byte[] header = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "P6\n{0} {1}\n255\n", frameBuffer.Width, frameBuffer.Height));
                stream.Write(header, 0, header.Length);
                for (int index = 0; index < frameBuffer.Pixels.Length; index++)
                {
                    uint pixel = frameBuffer.Pixels[index];
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
            using (SHA256 sha256 = SHA256.Create())
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(frameBuffer.Width);
                writer.Write(frameBuffer.Height);
                for (int index = 0; index < frameBuffer.Pixels.Length; index++)
                {
                    writer.Write(frameBuffer.Pixels[index]);
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
