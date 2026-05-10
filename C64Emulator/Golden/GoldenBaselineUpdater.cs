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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace C64Emulator.Core
{
    /// <summary>
    /// Updates golden manifests from accepted result JSON files.
    /// </summary>
    public static class GoldenBaselineUpdater
    {
        private static readonly string[] DefaultPropertyKeys =
        {
            "globalCycle",
            "rasterLine",
            "cycleInLine",
            "pc",
            "st"
        };

        /// <summary>
        /// Writes a manifest whose expectations are populated from actual run results.
        /// </summary>
        public static int Accept(string manifestPath, string resultPath, string outputManifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new ArgumentException("Manifest path must not be empty.", "manifestPath");
            }

            if (string.IsNullOrWhiteSpace(resultPath))
            {
                throw new ArgumentException("Result path must not be empty.", "resultPath");
            }

            if (string.IsNullOrWhiteSpace(outputManifestPath))
            {
                outputManifestPath = manifestPath;
            }

            JsonNode root = ParseJson(File.ReadAllText(manifestPath));
            GoldenRunResult runResult = ReadRunResult(resultPath);
            Dictionary<string, GoldenTestResult> resultsById = MapResults(runResult);

            JsonArray tests = root["tests"] as JsonArray;
            if (tests == null)
            {
                throw new InvalidDataException("Manifest does not contain a tests array.");
            }

            int updated = 0;
            foreach (JsonNode testNode in tests)
            {
                JsonObject testObject = testNode as JsonObject;
                if (testObject == null)
                {
                    continue;
                }

                string id = ReadString(testObject, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                GoldenTestResult testResult;
                if (!resultsById.TryGetValue(id, out testResult))
                {
                    continue;
                }

                JsonObject expectations = EnsureObject(testObject, "expectations");
                UpdateHashes(EnsureObject(expectations, "hashes"), testResult);
                UpdateProperties(EnsureObject(expectations, "properties"), testResult);
                updated++;
            }

            WriteJson(outputManifestPath, root);
            return updated;
        }

        private static void UpdateHashes(JsonObject hashObject, GoldenTestResult result)
        {
            if (result.ActualHashes == null)
            {
                return;
            }

            if (hashObject.Count == 0)
            {
                string frameHash;
                if (result.ActualHashes.TryGetValue("frame", out frameHash))
                {
                    hashObject["frame"] = frameHash;
                }
            }

            foreach (KeyValuePair<string, string> pair in new Dictionary<string, string>(ReadObjectValues(hashObject)))
            {
                string actual;
                if (result.ActualHashes.TryGetValue(pair.Key, out actual))
                {
                    hashObject[pair.Key] = actual;
                }
            }
        }

        private static void UpdateProperties(JsonObject propertyObject, GoldenTestResult result)
        {
            if (result.ActualProperties == null)
            {
                return;
            }

            if (propertyObject.Count == 0)
            {
                for (int index = 0; index < DefaultPropertyKeys.Length; index++)
                {
                    string actual;
                    if (result.ActualProperties.TryGetValue(DefaultPropertyKeys[index], out actual))
                    {
                        propertyObject[DefaultPropertyKeys[index]] = actual;
                    }
                }
            }

            foreach (KeyValuePair<string, string> pair in new Dictionary<string, string>(ReadObjectValues(propertyObject)))
            {
                string actual;
                if (result.ActualProperties.TryGetValue(pair.Key, out actual))
                {
                    propertyObject[pair.Key] = actual;
                }
            }
        }

        private static Dictionary<string, string> ReadObjectValues(JsonObject jsonObject)
        {
            var values = new Dictionary<string, string>();
            foreach (KeyValuePair<string, JsonNode> pair in jsonObject)
            {
                values[pair.Key] = pair.Value == null ? string.Empty : pair.Value.ToString();
            }

            return values;
        }

        private static Dictionary<string, GoldenTestResult> MapResults(GoldenRunResult runResult)
        {
            var map = new Dictionary<string, GoldenTestResult>(StringComparer.OrdinalIgnoreCase);
            if (runResult.Results == null)
            {
                return map;
            }

            for (int index = 0; index < runResult.Results.Count; index++)
            {
                GoldenTestResult result = runResult.Results[index];
                if (!string.IsNullOrWhiteSpace(result.Id))
                {
                    map[result.Id] = result;
                }
            }

            return map;
        }

        internal static GoldenRunResult ReadRunResult(string path)
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            GoldenRunResult result = JsonSerializer.Deserialize<GoldenRunResult>(File.ReadAllText(path), options);
            if (result == null)
            {
                throw new InvalidDataException("Result JSON could not be read.");
            }

            return result;
        }

        private static JsonNode ParseJson(string json)
        {
            var options = new JsonNodeOptions();
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            JsonNode node = JsonNode.Parse(json, options, documentOptions);
            if (node == null)
            {
                throw new InvalidDataException("JSON document is empty.");
            }

            return node;
        }

        private static JsonObject EnsureObject(JsonObject parent, string propertyName)
        {
            JsonObject child = parent[propertyName] as JsonObject;
            if (child != null)
            {
                return child;
            }

            child = new JsonObject();
            parent[propertyName] = child;
            return child;
        }

        private static string ReadString(JsonObject parent, string propertyName)
        {
            JsonNode value = parent[propertyName];
            return value == null ? null : value.ToString();
        }

        private static void WriteJson(string path, JsonNode root)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions();
            options.WriteIndented = true;
            File.WriteAllText(path, root.ToJsonString(options) + Environment.NewLine);
        }
    }
}
