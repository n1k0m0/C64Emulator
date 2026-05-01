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
    /// Represents the mounted media info component.
    /// </summary>
    public sealed class MountedMediaInfo
    {
        public static readonly MountedMediaInfo None = new MountedMediaInfo(MountedMediaKind.None, "NONE", string.Empty, string.Empty);

        /// <summary>
        /// Initializes a new MountedMediaInfo instance.
        /// </summary>
        public MountedMediaInfo(MountedMediaKind kind, string shortLabel, string displayName, string hostPath)
        {
            Kind = kind;
            ShortLabel = shortLabel;
            DisplayName = displayName ?? string.Empty;
            HostPath = hostPath ?? string.Empty;
        }

        /// <summary>
        /// Gets the mounted media kind.
        /// </summary>
        public MountedMediaKind Kind { get; }

        /// <summary>
        /// Gets the short media label for overlays.
        /// </summary>
        public string ShortLabel { get; }

        /// <summary>
        /// Gets the user-facing display name.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Gets the host filesystem path.
        /// </summary>
        public string HostPath { get; }

        public bool HasMedia
        {
            get { return Kind != MountedMediaKind.None; }
        }
    }
}
