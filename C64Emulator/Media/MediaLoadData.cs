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
namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the media load data component.
    /// </summary>
    public sealed class MediaLoadData
    {
        /// <summary>
        /// Initializes a new MediaLoadData instance.
        /// </summary>
        public MediaLoadData(string name, byte[] programBytes, bool isDirectory)
        {
            Name = name;
            ProgramBytes = programBytes;
            IsDirectory = isDirectory;
        }

        /// <summary>
        /// Gets the model or media name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the program payload bytes.
        /// </summary>
        public byte[] ProgramBytes { get; }

        /// <summary>
        /// Gets whether the entry represents a directory.
        /// </summary>
        public bool IsDirectory { get; }
    }
}
