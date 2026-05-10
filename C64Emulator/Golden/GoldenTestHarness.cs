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

namespace C64Emulator.Core
{
    /// <summary>
    /// Runs a golden manifest through a supplied executor.
    /// </summary>
    public sealed class GoldenTestHarness
    {
        private readonly IGoldenTestExecutor _executor;

        /// <summary>
        /// Initializes a new GoldenTestHarness instance.
        /// </summary>
        public GoldenTestHarness(IGoldenTestExecutor executor)
        {
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }

            _executor = executor;
        }

        /// <summary>
        /// Runs all enabled tests in a manifest.
        /// </summary>
        public GoldenRunResult Run(GoldenManifest manifest, string outputDirectory)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            var result = new GoldenRunResult();
            result.Name = manifest.Name;
            result.ManifestPath = manifest.ManifestPath;
            result.StartedUtc = DateTime.UtcNow;

            var context = new GoldenRunContext(manifest, outputDirectory);
            for (int index = 0; index < manifest.Tests.Count; index++)
            {
                GoldenTestDefinition test = manifest.Tests[index];
                result.Results.Add(RunTest(test, context));
            }

            result.FinishedUtc = DateTime.UtcNow;
            return result;
        }

        private GoldenTestResult RunTest(GoldenTestDefinition test, GoldenRunContext context)
        {
            if (!test.Enabled)
            {
                return CreateSkipped(test, "Test is disabled in the manifest.");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                GoldenTestResult result = _executor.Execute(test, context);
                stopwatch.Stop();

                if (result == null)
                {
                    result = CreateError(test, "Executor returned no result.");
                }

                FillIdentity(test, result);
                result.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
                ApplyExpectations(test, result);
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                GoldenTestResult result = CreateError(test, ex.GetType().Name + ": " + ex.Message);
                result.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
                return result;
            }
        }

        private static void FillIdentity(GoldenTestDefinition test, GoldenTestResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Id))
            {
                result.Id = test.Id;
            }

            if (string.IsNullOrWhiteSpace(result.Name))
            {
                result.Name = string.IsNullOrWhiteSpace(test.Name) ? test.Id : test.Name;
            }

            if (string.IsNullOrWhiteSpace(result.Category))
            {
                result.Category = test.Category;
            }

            if (result.ExpectedHashes == null)
            {
                result.ExpectedHashes = new System.Collections.Generic.Dictionary<string, string>();
            }

            if (result.ActualHashes == null)
            {
                result.ActualHashes = new System.Collections.Generic.Dictionary<string, string>();
            }

            if (result.ExpectedProperties == null)
            {
                result.ExpectedProperties = new System.Collections.Generic.Dictionary<string, string>();
            }

            if (result.ActualProperties == null)
            {
                result.ActualProperties = new System.Collections.Generic.Dictionary<string, string>();
            }
        }

        private static void ApplyExpectations(GoldenTestDefinition test, GoldenTestResult result)
        {
            if (test.Expectations == null)
            {
                return;
            }

            CopyMissing(test.Expectations.Hashes, result.ExpectedHashes);
            CopyMissing(test.Expectations.Properties, result.ExpectedProperties);

            if (!string.IsNullOrWhiteSpace(test.Expectations.ExitReason))
            {
                result.ExpectedProperties["exitReason"] = test.Expectations.ExitReason;
                result.ActualProperties["exitReason"] = result.ExitReason;
            }

            string failure = FindFirstMismatch(result);
            if (failure != null && result.Outcome == GoldenTestOutcome.Passed)
            {
                result.Outcome = GoldenTestOutcome.Failed;
                result.Message = failure;
            }
        }

        private static void CopyMissing(System.Collections.Generic.Dictionary<string, string> source, System.Collections.Generic.Dictionary<string, string> target)
        {
            if (source == null || target == null)
            {
                return;
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in source)
            {
                if (!target.ContainsKey(pair.Key))
                {
                    target[pair.Key] = pair.Value;
                }
            }
        }

        private static string FindFirstMismatch(GoldenTestResult result)
        {
            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in result.ExpectedHashes)
            {
                string actual;
                if (!result.ActualHashes.TryGetValue(pair.Key, out actual))
                {
                    return "Missing actual hash '" + pair.Key + "'.";
                }

                if (!GoldenHash.Equals(pair.Value, actual))
                {
                    return "Hash mismatch '" + pair.Key + "': expected " + pair.Value + ", got " + actual + ".";
                }
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in result.ExpectedProperties)
            {
                string actual;
                if (!result.ActualProperties.TryGetValue(pair.Key, out actual))
                {
                    return "Missing actual property '" + pair.Key + "'.";
                }

                if (!string.Equals(pair.Value, actual, StringComparison.Ordinal))
                {
                    return "Property mismatch '" + pair.Key + "': expected " + pair.Value + ", got " + actual + ".";
                }
            }

            return null;
        }

        private static GoldenTestResult CreateSkipped(GoldenTestDefinition test, string message)
        {
            var result = new GoldenTestResult();
            FillIdentity(test, result);
            result.Outcome = GoldenTestOutcome.Skipped;
            result.Message = message;
            return result;
        }

        private static GoldenTestResult CreateError(GoldenTestDefinition test, string message)
        {
            var result = new GoldenTestResult();
            FillIdentity(test, result);
            result.Outcome = GoldenTestOutcome.Error;
            result.Message = message;
            return result;
        }
    }
}
