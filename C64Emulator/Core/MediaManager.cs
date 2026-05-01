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
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the media manager component.
    /// </summary>
    public sealed class MediaManager
    {
        private byte[] _mountedPrgBytes;
        private string _mountedPrgName;
        private D64Image _mountedD64;
        private MountedMediaInfo _mountedMedia = MountedMediaInfo.None;

        public MountedMediaInfo MountedMedia
        {
            get { return _mountedMedia; }
        }

        public D64Image MountedDiskImage
        {
            get { return _mountedD64; }
        }

        public bool HasMountedMedia
        {
            get { return _mountedMedia.HasMedia; }
        }

        /// <summary>
        /// Handles the mount operation.
        /// </summary>
        public MediaMountResult Mount(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new MediaMountResult(false, "FILE NOT FOUND", _mountedMedia, null);
            }

            string extension = Path.GetExtension(path);
            if (string.Equals(extension, ".prg", StringComparison.OrdinalIgnoreCase))
            {
                byte[] programBytes = File.ReadAllBytes(path);
                if (programBytes.Length < 2)
                {
                    return new MediaMountResult(false, "INVALID PRG", _mountedMedia, null);
                }

                _mountedPrgBytes = programBytes;
                _mountedPrgName = Path.GetFileName(path);
                _mountedD64 = null;
                _mountedMedia = new MountedMediaInfo(MountedMediaKind.Prg, "PRG", Path.GetFileNameWithoutExtension(path), path);
                return new MediaMountResult(true, "PRG LOADED", _mountedMedia, programBytes);
            }

            if (string.Equals(extension, ".d64", StringComparison.OrdinalIgnoreCase))
            {
                D64Image image;
                try
                {
                    image = D64Image.Load(path);
                }
                catch (Exception)
                {
                    return new MediaMountResult(false, "INVALID D64", _mountedMedia, null);
                }

                _mountedPrgBytes = null;
                _mountedPrgName = null;
                _mountedD64 = image;
                _mountedMedia = new MountedMediaInfo(
                    MountedMediaKind.D64,
                    "D64",
                    string.IsNullOrWhiteSpace(image.DiskName) ? Path.GetFileNameWithoutExtension(path) : image.DiskName,
                    path);
                return new MediaMountResult(true, "DISK MOUNTED", _mountedMedia, null);
            }

            return new MediaMountResult(false, "UNSUPPORTED FILE", _mountedMedia, null);
        }

        /// <summary>
        /// Handles the eject operation.
        /// </summary>
        public string Eject()
        {
            _mountedPrgBytes = null;
            _mountedPrgName = null;
            _mountedD64 = null;
            _mountedMedia = MountedMediaInfo.None;
            return "MEDIA EJECTED";
        }

        /// <summary>
        /// Attempts to resolve load and reports whether it succeeded.
        /// </summary>
        public bool TryResolveLoad(string filename, out MediaLoadData loadData)
        {
            loadData = null;

            if (_mountedPrgBytes != null)
            {
                if (MatchesPrgLoadRequest(filename))
                {
                    loadData = new MediaLoadData(_mountedPrgName ?? "PROGRAM.PRG", _mountedPrgBytes, false);
                    return true;
                }

                return false;
            }

            return false;
        }

        /// <summary>
        /// Handles the matches prg load request operation.
        /// </summary>
        private bool MatchesPrgLoadRequest(string filename)
        {
            string normalizedRequest = NormalizeRequest(filename);
            if (normalizedRequest.Length == 0 || normalizedRequest == "*")
            {
                return true;
            }

            string prgName = NormalizeRequest(_mountedPrgName);
            string prgNameWithoutExtension = NormalizeRequest(Path.GetFileNameWithoutExtension(_mountedPrgName ?? string.Empty));
            return string.Equals(normalizedRequest, prgName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedRequest, prgNameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Handles the normalize request operation.
        /// </summary>
        private static string NormalizeRequest(string value)
        {
            string cleaned = (value ?? string.Empty).Trim().Trim('"').ToUpperInvariant();
            int colonIndex = cleaned.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < cleaned.Length - 1)
            {
                cleaned = cleaned.Substring(colonIndex + 1);
            }

            return cleaned;
        }
    }
}
