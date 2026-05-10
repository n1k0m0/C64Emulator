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
    /// Contains the expected observable values for a golden test.
    /// </summary>
    public sealed class GoldenExpectations
    {
        /// <summary>
        /// Initializes a new GoldenExpectations instance.
        /// </summary>
        public GoldenExpectations()
        {
            Hashes = new Dictionary<string, string>();
            Properties = new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets or sets the expected stop reason.
        /// </summary>
        public string ExitReason { get; set; }

        /// <summary>
        /// Gets or sets named expected SHA-256 hashes.
        /// </summary>
        public Dictionary<string, string> Hashes { get; set; }

        /// <summary>
        /// Gets or sets named expected scalar properties.
        /// </summary>
        public Dictionary<string, string> Properties { get; set; }
    }
}
