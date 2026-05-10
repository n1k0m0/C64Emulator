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
