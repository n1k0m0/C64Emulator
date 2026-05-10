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

namespace C64Emulator.Core
{
    /// <summary>
    /// Loads golden test manifests from JSON.
    /// </summary>
    public static class GoldenManifestLoader
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        /// <summary>
        /// Loads a manifest from a JSON file.
        /// </summary>
        public static GoldenManifest Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Manifest path must not be empty.", "path");
            }

            string fullPath = Path.GetFullPath(path);
            string json = File.ReadAllText(fullPath);
            GoldenManifest manifest = LoadJson(json);
            manifest.ManifestPath = fullPath;

            if (string.IsNullOrWhiteSpace(manifest.BaseDirectory))
            {
                manifest.BaseDirectory = Path.GetDirectoryName(fullPath);
            }
            else if (!Path.IsPathRooted(manifest.BaseDirectory))
            {
                manifest.BaseDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath), manifest.BaseDirectory));
            }

            Normalize(manifest);
            return manifest;
        }

        /// <summary>
        /// Loads a manifest from a JSON string.
        /// </summary>
        public static GoldenManifest LoadJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("Manifest JSON must not be empty.", "json");
            }

            GoldenManifest manifest = JsonSerializer.Deserialize<GoldenManifest>(json, Options);
            if (manifest == null)
            {
                throw new InvalidDataException("Manifest JSON did not contain a manifest object.");
            }

            Normalize(manifest);
            return manifest;
        }

        /// <summary>
        /// Converts a manifest to formatted JSON.
        /// </summary>
        public static string ToJson(GoldenManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            return JsonSerializer.Serialize(manifest, Options);
        }

        /// <summary>
        /// Resolves a path relative to the manifest base directory.
        /// </summary>
        public static string ResolvePath(GoldenManifest manifest, string path)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                return path;
            }

            string baseDirectory = manifest.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Directory.GetCurrentDirectory();
            }

            return Path.GetFullPath(Path.Combine(baseDirectory, path));
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions();
            options.AllowTrailingCommas = true;
            options.PropertyNameCaseInsensitive = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            options.WriteIndented = true;
            return options;
        }

        private static void Normalize(GoldenManifest manifest)
        {
            if (manifest.Tests == null)
            {
                manifest.Tests = new System.Collections.Generic.List<GoldenTestDefinition>();
            }

            if (manifest.Metadata == null)
            {
                manifest.Metadata = new System.Collections.Generic.Dictionary<string, string>();
            }

            for (int index = 0; index < manifest.Tests.Count; index++)
            {
                GoldenTestDefinition test = manifest.Tests[index];
                if (test == null)
                {
                    throw new InvalidDataException("Manifest contains a null test definition at index " + index + ".");
                }

                if (test.Tags == null)
                {
                    test.Tags = new System.Collections.Generic.List<string>();
                }

                if (test.Arguments == null)
                {
                    test.Arguments = new System.Collections.Generic.Dictionary<string, string>();
                }

                if (test.Metadata == null)
                {
                    test.Metadata = new System.Collections.Generic.Dictionary<string, string>();
                }

                if (test.Expectations == null)
                {
                    test.Expectations = new GoldenExpectations();
                }

                if (test.Expectations.Hashes == null)
                {
                    test.Expectations.Hashes = new System.Collections.Generic.Dictionary<string, string>();
                }

                if (test.Expectations.Properties == null)
                {
                    test.Expectations.Properties = new System.Collections.Generic.Dictionary<string, string>();
                }
            }
        }
    }
}
