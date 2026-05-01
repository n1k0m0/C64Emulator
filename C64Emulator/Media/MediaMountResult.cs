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
    /// Represents the media mount result component.
    /// </summary>
    public sealed class MediaMountResult
    {
        /// <summary>
        /// Initializes a new MediaMountResult instance.
        /// </summary>
        public MediaMountResult(bool success, string message, MountedMediaInfo mountedMedia, byte[] autoLoadProgramBytes)
        {
            Success = success;
            Message = message;
            MountedMedia = mountedMedia ?? MountedMediaInfo.None;
            AutoLoadProgramBytes = autoLoadProgramBytes;
        }

        /// <summary>
        /// Gets whether the media operation succeeded.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the user-facing media operation message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the mounted media information.
        /// </summary>
        public MountedMediaInfo MountedMedia { get; }

        /// <summary>
        /// Gets optional program bytes for immediate autoload.
        /// </summary>
        public byte[] AutoLoadProgramBytes { get; }
    }
}
