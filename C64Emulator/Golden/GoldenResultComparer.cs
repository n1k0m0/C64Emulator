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
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Compares two golden result JSON files.
    /// </summary>
    public static class GoldenResultComparer
    {
        /// <summary>
        /// Compares result files and writes a text report.
        /// </summary>
        public static int Compare(string referenceResultPath, string actualResultPath, string reportPath)
        {
            GoldenRunResult reference = GoldenBaselineUpdater.ReadRunResult(referenceResultPath);
            GoldenRunResult actual = GoldenBaselineUpdater.ReadRunResult(actualResultPath);
            Dictionary<string, GoldenTestResult> referenceById = Map(reference);
            Dictionary<string, GoldenTestResult> actualById = Map(actual);

            var report = new StringBuilder();
            report.AppendLine("C64 GOLDEN RESULT COMPARISON");
            report.AppendLine("Reference=" + Path.GetFullPath(referenceResultPath));
            report.AppendLine("Actual=" + Path.GetFullPath(actualResultPath));

            int failures = 0;
            foreach (KeyValuePair<string, GoldenTestResult> pair in referenceById)
            {
                GoldenTestResult actualTest;
                if (!actualById.TryGetValue(pair.Key, out actualTest))
                {
                    failures++;
                    report.AppendLine("FAIL " + pair.Key + ": missing actual test result");
                    continue;
                }

                failures += CompareTest(pair.Key, pair.Value, actualTest, report);
            }

            foreach (KeyValuePair<string, GoldenTestResult> pair in actualById)
            {
                if (!referenceById.ContainsKey(pair.Key))
                {
                    failures++;
                    report.AppendLine("FAIL " + pair.Key + ": missing reference test result");
                }
            }

            report.AppendLine("Failures=" + failures.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(reportPath, report.ToString());
            }

            Console.Write(report.ToString());
            return failures;
        }

        private static int CompareTest(string id, GoldenTestResult reference, GoldenTestResult actual, StringBuilder report)
        {
            int failures = 0;
            failures += CompareValues(id, "hash", reference.ActualHashes, actual.ActualHashes, report, true);
            failures += CompareValues(id, "property", reference.ActualProperties, actual.ActualProperties, report, false);
            if (failures == 0)
            {
                report.AppendLine("PASS " + id);
            }

            return failures;
        }

        private static int CompareValues(
            string id,
            string kind,
            Dictionary<string, string> referenceValues,
            Dictionary<string, string> actualValues,
            StringBuilder report,
            bool normalizeHash)
        {
            int failures = 0;
            referenceValues = referenceValues ?? new Dictionary<string, string>();
            actualValues = actualValues ?? new Dictionary<string, string>();

            foreach (KeyValuePair<string, string> pair in referenceValues)
            {
                string actual;
                if (!actualValues.TryGetValue(pair.Key, out actual))
                {
                    failures++;
                    report.AppendLine("FAIL " + id + ": missing " + kind + " " + pair.Key);
                    continue;
                }

                bool equal = normalizeHash
                    ? GoldenHash.Equals(pair.Value, actual)
                    : string.Equals(pair.Value, actual, StringComparison.Ordinal);
                if (!equal)
                {
                    failures++;
                    report.AppendLine("FAIL " + id + ": " + kind + " " + pair.Key + " expected " + pair.Value + " got " + actual);
                }
            }

            return failures;
        }

        private static Dictionary<string, GoldenTestResult> Map(GoldenRunResult runResult)
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
    }
}
