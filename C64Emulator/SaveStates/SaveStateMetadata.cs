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

namespace C64Emulator.Core
{
    /// <summary>
    /// Contains the lightweight information shown by the savestate menu.
    /// </summary>
    internal sealed class SaveStateMetadata
    {
        /// <summary>
        /// Gets or sets the savestate file path.
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Gets or sets the local creation time stored in the savestate.
        /// </summary>
        public DateTime CreatedLocalTime { get; set; }

        /// <summary>
        /// Gets or sets the mounted media kind stored with the savestate.
        /// </summary>
        public MountedMediaKind MediaKind { get; set; }

        /// <summary>
        /// Gets or sets the short mounted media label stored with the savestate.
        /// </summary>
        public string MediaShortLabel { get; set; }

        /// <summary>
        /// Gets or sets the mounted media display name stored with the savestate.
        /// </summary>
        public string MediaDisplayName { get; set; }

        /// <summary>
        /// Gets or sets the mounted media host path stored with the savestate.
        /// </summary>
        public string MediaHostPath { get; set; }

        /// <summary>
        /// Gets or sets the screenshot width.
        /// </summary>
        public int ScreenshotWidth { get; set; }

        /// <summary>
        /// Gets or sets the screenshot height.
        /// </summary>
        public int ScreenshotHeight { get; set; }

        /// <summary>
        /// Gets or sets the screenshot pixels in ARGB format.
        /// </summary>
        public uint[] ScreenshotPixels { get; set; }
    }
}
