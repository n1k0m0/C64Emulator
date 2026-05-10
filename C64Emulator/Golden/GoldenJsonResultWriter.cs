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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C64Emulator.Core
{
    /// <summary>
    /// Writes golden run results as JSON.
    /// </summary>
    public static class GoldenJsonResultWriter
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        /// <summary>
        /// Converts a run result to formatted JSON.
        /// </summary>
        public static string ToJson(GoldenRunResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException("result");
            }

            return JsonSerializer.Serialize(result, Options);
        }

        /// <summary>
        /// Writes a run result to a JSON file.
        /// </summary>
        public static void Write(string path, GoldenRunResult result)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Output path must not be empty.", "path");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, ToJson(result));
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions();
            options.WriteIndented = true;
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
