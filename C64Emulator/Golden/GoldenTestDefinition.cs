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
    /// Defines one golden test case and its expected observable outputs.
    /// </summary>
    public sealed class GoldenTestDefinition
    {
        /// <summary>
        /// Initializes a new GoldenTestDefinition instance.
        /// </summary>
        public GoldenTestDefinition()
        {
            Enabled = true;
            Tags = new List<string>();
            Arguments = new Dictionary<string, string>();
            Metadata = new Dictionary<string, string>();
            Expectations = new GoldenExpectations();
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
        /// Gets or sets whether this test should run.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the requested C64 model name, for example PAL or NTSC.
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Gets or sets a PRG path relative to the manifest base directory.
        /// </summary>
        public string ProgramPath { get; set; }

        /// <summary>
        /// Gets or sets a media path relative to the manifest base directory.
        /// </summary>
        public string MediaPath { get; set; }

        /// <summary>
        /// Gets or sets a savestate path relative to the manifest base directory.
        /// </summary>
        public string SaveStatePath { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of machine cycles for this test.
        /// </summary>
        public long MaxCycles { get; set; }

        /// <summary>
        /// Gets or sets an optional timeout in milliseconds.
        /// </summary>
        public int TimeoutMilliseconds { get; set; }

        /// <summary>
        /// Gets or sets tags used by external runners for filtering.
        /// </summary>
        public List<string> Tags { get; set; }

        /// <summary>
        /// Gets or sets runner-specific arguments.
        /// </summary>
        public Dictionary<string, string> Arguments { get; set; }

        /// <summary>
        /// Gets or sets test-level metadata.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; }

        /// <summary>
        /// Gets or sets expected hashes and state values.
        /// </summary>
        public GoldenExpectations Expectations { get; set; }
    }
}
