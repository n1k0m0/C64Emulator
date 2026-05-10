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
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Executes golden tests through the real C64 machine.
    /// </summary>
    public sealed class C64GoldenTestExecutor : IGoldenTestExecutor
    {
        private const int CycleChunkSize = 1000000;

        /// <summary>
        /// Runs a test and returns observed results.
        /// </summary>
        public GoldenTestResult Execute(GoldenTestDefinition test, GoldenRunContext context)
        {
            if (test == null)
            {
                throw new ArgumentNullException("test");
            }

            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            C64Model model = ResolveModel(test.Model);
            using (var system = new C64System(model, ResolveAccuracyOptions(test)))
            {
                var result = new GoldenTestResult();
                result.Outcome = GoldenTestOutcome.Passed;

                MountInputs(system, test, context, result);
                RunOptionalWarmup(system, test);
                EnqueueOptionalCommand(system, test);
                RunMachine(system, test.MaxCycles);
                FillObservedState(system, test, context, result);
                result.ExitReason = "cycles";
                return result;
            }
        }

        private static C64AccuracyOptions ResolveAccuracyOptions(GoldenTestDefinition test)
        {
            string profile = GetArgument(test, "profile");
            if (string.Equals(profile, "compatibility", StringComparison.OrdinalIgnoreCase))
            {
                return C64AccuracyOptions.Compatibility;
            }

            return C64AccuracyOptions.Accuracy;
        }

        private static C64Model ResolveModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName) ||
                string.Equals(modelName, "pal", StringComparison.OrdinalIgnoreCase))
            {
                return C64Model.Pal;
            }

            throw new NotSupportedException("Unsupported golden model '" + modelName + "'.");
        }

        private static void MountInputs(C64System system, GoldenTestDefinition test, GoldenRunContext context, GoldenTestResult result)
        {
            string programPath = context.ResolvePath(test.ProgramPath);
            if (!string.IsNullOrWhiteSpace(programPath))
            {
                string message = system.MountMedia(programPath, ResolveDriveNumber(test));
                result.ActualProperties["programMount"] = message;
                result.Artifacts["program"] = Path.GetFullPath(programPath);
            }

            string mediaPath = context.ResolvePath(test.MediaPath);
            if (!string.IsNullOrWhiteSpace(mediaPath))
            {
                string message = system.MountMedia(mediaPath, ResolveDriveNumber(test));
                result.ActualProperties["mediaMount"] = message;
                result.Artifacts["media"] = Path.GetFullPath(mediaPath);
            }
        }

        private static void RunOptionalWarmup(C64System system, GoldenTestDefinition test)
        {
            long warmupCycles = GetLongArgument(test, "warmupCycles", 0);
            RunMachine(system, warmupCycles);
        }

        private static void EnqueueOptionalCommand(C64System system, GoldenTestDefinition test)
        {
            string command = GetArgument(test, "command");
            if (string.IsNullOrEmpty(command))
            {
                return;
            }

            command = command.Replace("\\r", "\r").Replace("\\n", "\n");
            system.EnqueuePetsciiText(command);
        }

        private static void RunMachine(C64System system, long cycles)
        {
            long remaining = Math.Max(0, cycles);
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(CycleChunkSize, remaining);
                system.RunCycles(chunk);
                remaining -= chunk;
            }
        }

        private static void FillObservedState(C64System system, GoldenTestDefinition test, GoldenRunContext context, GoldenTestResult result)
        {
            VicTiming timing = system.Timing;
            result.ActualProperties["globalCycle"] = timing.GlobalCycle.ToString(CultureInfo.InvariantCulture);
            result.ActualProperties["rasterLine"] = timing.RasterLine.ToString(CultureInfo.InvariantCulture);
            result.ActualProperties["cycleInLine"] = timing.CycleInLine.ToString(CultureInfo.InvariantCulture);
            result.ActualProperties["badLine"] = timing.BadLine ? "true" : "false";
            result.ActualProperties["pc"] = system.Cpu.PC.ToString("X4", CultureInfo.InvariantCulture);
            result.ActualProperties["opcode"] = system.Cpu.CurrentOpcode.ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["a"] = system.Cpu.A.ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["x"] = system.Cpu.X.ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["y"] = system.Cpu.Y.ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["sp"] = system.Cpu.SP.ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["sr"] = system.Cpu.SR.ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["st"] = system.PeekRam(0x0090).ToString("X2", CultureInfo.InvariantCulture);
            result.ActualProperties["mem"] = system.GetMemoryConfigDebugInfo();
            result.ActualProperties["cia"] = system.GetCiaDebugInfo();
            result.ActualProperties["iec"] = system.GetIecDebugInfo();
            result.ActualProperties["drive8"] = system.GetDriveDebugInfo(8);

            result.ActualHashes["frame"] = DevTraceExporter.ComputeFrameHash(system.FrameBuffer);
            ComputeRequestedMemoryHashes(system, test, result);
            WriteOptionalFrameArtifact(system, test, context, result);
        }

        private static void ComputeRequestedMemoryHashes(C64System system, GoldenTestDefinition test, GoldenTestResult result)
        {
            if (test.Expectations == null || test.Expectations.Hashes == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> pair in test.Expectations.Hashes)
            {
                if (result.ActualHashes.ContainsKey(pair.Key))
                {
                    continue;
                }

                if (pair.Key.StartsWith("ram:", StringComparison.OrdinalIgnoreCase))
                {
                    result.ActualHashes[pair.Key] = HashRamRange(system, pair.Key.Substring(4));
                }
                else if (pair.Key.StartsWith("drive8ram:", StringComparison.OrdinalIgnoreCase))
                {
                    result.ActualHashes[pair.Key] = HashDriveRamRange(system, 8, pair.Key.Substring(10));
                }
            }
        }

        private static string HashRamRange(C64System system, string range)
        {
            ushort address;
            int length;
            ParseRange(range, out address, out length);
            byte[] bytes = new byte[length];
            for (int index = 0; index < length; index++)
            {
                bytes[index] = system.PeekRam((ushort)(address + index));
            }

            return HashBytes(bytes);
        }

        private static string HashDriveRamRange(C64System system, int driveNumber, string range)
        {
            ushort address;
            int length;
            ParseRange(range, out address, out length);
            byte[] bytes = new byte[length];
            for (int index = 0; index < length; index++)
            {
                bytes[index] = system.PeekDriveMemory(driveNumber, (ushort)(address + index));
            }

            return HashBytes(bytes);
        }

        private static void ParseRange(string range, out ushort address, out int length)
        {
            string[] parts = (range ?? string.Empty).Split(':');
            if (parts.Length != 2)
            {
                throw new FormatException("Expected range as HEXADDRESS:HEXLENGTH.");
            }

            address = ushort.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            length = int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (length < 0)
            {
                throw new FormatException("Range length must not be negative.");
            }
        }

        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("X2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static void WriteOptionalFrameArtifact(C64System system, GoldenTestDefinition test, GoldenRunContext context, GoldenTestResult result)
        {
            bool writeFrame = GetBoolArgument(test, "writeFrame", false);
            if (!writeFrame || string.IsNullOrWhiteSpace(context.OutputDirectory))
            {
                return;
            }

            string fileName = SafeName(string.IsNullOrWhiteSpace(test.Id) ? test.Name : test.Id) + ".ppm";
            string path = context.ResolveOutputPath(fileName);
            DevTraceExporter.WriteFrameBufferPpm(system.FrameBuffer, path);
            result.Artifacts["frame"] = Path.GetFullPath(path);
        }

        private static int ResolveDriveNumber(GoldenTestDefinition test)
        {
            long drive = GetLongArgument(test, "drive", 8);
            if (drive < 8 || drive > 11)
            {
                throw new ArgumentOutOfRangeException("drive", "Drive must be 8, 9, 10, or 11.");
            }

            return (int)drive;
        }

        private static string GetArgument(GoldenTestDefinition test, string key)
        {
            string value;
            if (test.Arguments != null && test.Arguments.TryGetValue(key, out value))
            {
                return value;
            }

            return null;
        }

        private static long GetLongArgument(GoldenTestDefinition test, string key, long fallback)
        {
            string value = GetArgument(test, key);
            long parsed;
            if (!string.IsNullOrWhiteSpace(value) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static bool GetBoolArgument(GoldenTestDefinition test, string key, bool fallback)
        {
            string value = GetArgument(test, key);
            bool parsed;
            if (!string.IsNullOrWhiteSpace(value) &&
                bool.TryParse(value, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string SafeName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "golden-test" : value;
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            return builder.ToString();
        }
    }
}
