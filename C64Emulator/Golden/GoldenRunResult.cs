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

namespace C64Emulator.Core
{
    /// <summary>
    /// Contains aggregate results for a golden manifest run.
    /// </summary>
    public sealed class GoldenRunResult
    {
        /// <summary>
        /// Initializes a new GoldenRunResult instance.
        /// </summary>
        public GoldenRunResult()
        {
            Results = new List<GoldenTestResult>();
        }

        /// <summary>
        /// Gets or sets the suite name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the manifest file path.
        /// </summary>
        public string ManifestPath { get; set; }

        /// <summary>
        /// Gets or sets when the run started.
        /// </summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>
        /// Gets or sets when the run finished.
        /// </summary>
        public DateTime FinishedUtc { get; set; }

        /// <summary>
        /// Gets or sets per-test results.
        /// </summary>
        public List<GoldenTestResult> Results { get; set; }

        /// <summary>
        /// Gets the number of tests in the result.
        /// </summary>
        public int TestCount
        {
            get { return Results == null ? 0 : Results.Count; }
        }

        /// <summary>
        /// Gets the number of failed tests.
        /// </summary>
        public int FailureCount
        {
            get { return Count(GoldenTestOutcome.Failed); }
        }

        /// <summary>
        /// Gets the number of tests that ended in an error.
        /// </summary>
        public int ErrorCount
        {
            get { return Count(GoldenTestOutcome.Error); }
        }

        /// <summary>
        /// Gets the number of skipped tests.
        /// </summary>
        public int SkippedCount
        {
            get { return Count(GoldenTestOutcome.Skipped); }
        }

        /// <summary>
        /// Gets the elapsed suite duration in milliseconds.
        /// </summary>
        public long DurationMilliseconds
        {
            get
            {
                if (FinishedUtc < StartedUtc)
                {
                    return 0;
                }

                return (long)(FinishedUtc - StartedUtc).TotalMilliseconds;
            }
        }

        private int Count(GoldenTestOutcome outcome)
        {
            if (Results == null)
            {
                return 0;
            }

            int count = 0;
            for (int index = 0; index < Results.Count; index++)
            {
                if (Results[index].Outcome == outcome)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
