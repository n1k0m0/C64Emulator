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
using System.Collections.Generic;

namespace C64Emulator.Core
{
    /// <summary>
    /// Contains the observed output of one golden test.
    /// </summary>
    public sealed class GoldenTestResult
    {
        /// <summary>
        /// Initializes a new GoldenTestResult instance.
        /// </summary>
        public GoldenTestResult()
        {
            Outcome = GoldenTestOutcome.Passed;
            ExpectedHashes = new Dictionary<string, string>();
            ActualHashes = new Dictionary<string, string>();
            ExpectedProperties = new Dictionary<string, string>();
            ActualProperties = new Dictionary<string, string>();
            Artifacts = new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets or sets the stable test id.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the display name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the logical category.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Gets or sets the test outcome.
        /// </summary>
        public GoldenTestOutcome Outcome { get; set; }

        /// <summary>
        /// Gets or sets the elapsed time in milliseconds.
        /// </summary>
        public long DurationMilliseconds { get; set; }

        /// <summary>
        /// Gets or sets a summary message for failures, errors, or skips.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Gets or sets the actual stop reason.
        /// </summary>
        public string ExitReason { get; set; }

        /// <summary>
        /// Gets or sets expected named hashes.
        /// </summary>
        public Dictionary<string, string> ExpectedHashes { get; set; }

        /// <summary>
        /// Gets or sets observed named hashes.
        /// </summary>
        public Dictionary<string, string> ActualHashes { get; set; }

        /// <summary>
        /// Gets or sets expected scalar properties.
        /// </summary>
        public Dictionary<string, string> ExpectedProperties { get; set; }

        /// <summary>
        /// Gets or sets observed scalar properties.
        /// </summary>
        public Dictionary<string, string> ActualProperties { get; set; }

        /// <summary>
        /// Gets or sets named artifact paths.
        /// </summary>
        public Dictionary<string, string> Artifacts { get; set; }
    }
}
