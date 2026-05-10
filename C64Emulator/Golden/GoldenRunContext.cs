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

namespace C64Emulator.Core
{
    /// <summary>
    /// Carries suite-level state for a golden test execution.
    /// </summary>
    public sealed class GoldenRunContext
    {
        /// <summary>
        /// Initializes a new GoldenRunContext instance.
        /// </summary>
        public GoldenRunContext(GoldenManifest manifest, string outputDirectory)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            Manifest = manifest;
            OutputDirectory = outputDirectory;
        }

        /// <summary>
        /// Gets the loaded manifest.
        /// </summary>
        public GoldenManifest Manifest { get; private set; }

        /// <summary>
        /// Gets the optional output directory for artifacts.
        /// </summary>
        public string OutputDirectory { get; private set; }

        /// <summary>
        /// Resolves a test asset path against the manifest base directory.
        /// </summary>
        public string ResolvePath(string path)
        {
            return GoldenManifestLoader.ResolvePath(Manifest, path);
        }

        /// <summary>
        /// Resolves an artifact path against the output directory.
        /// </summary>
        public string ResolveOutputPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
            {
                return fileName;
            }

            string outputDirectory = OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Directory.GetCurrentDirectory();
            }

            return Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        }
    }
}
