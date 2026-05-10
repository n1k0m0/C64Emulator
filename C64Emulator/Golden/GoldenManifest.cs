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
    /// Describes a collection of external golden tests.
    /// </summary>
    public sealed class GoldenManifest
    {
        /// <summary>
        /// Initializes a new GoldenManifest instance.
        /// </summary>
        public GoldenManifest()
        {
            SchemaVersion = 1;
            Tests = new List<GoldenTestDefinition>();
            Metadata = new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets or sets the manifest schema version.
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Gets or sets the suite name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets an optional suite description.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Gets or sets the manifest file path after loading.
        /// </summary>
        public string ManifestPath { get; set; }

        /// <summary>
        /// Gets or sets the directory used to resolve relative test paths.
        /// </summary>
        public string BaseDirectory { get; set; }

        /// <summary>
        /// Gets or sets suite-level metadata.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; }

        /// <summary>
        /// Gets or sets the test definitions in this manifest.
        /// </summary>
        public List<GoldenTestDefinition> Tests { get; set; }
    }
}
