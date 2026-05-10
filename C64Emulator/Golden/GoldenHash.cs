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
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Provides stable SHA-256 helpers for golden outputs.
    /// </summary>
    public static class GoldenHash
    {
        /// <summary>
        /// Computes a SHA-256 hash for a byte array.
        /// </summary>
        public static string ComputeBytes(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException("bytes");
            }

            using (SHA256 sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(bytes));
            }
        }

        /// <summary>
        /// Computes a SHA-256 hash for a stream from its current position.
        /// </summary>
        public static string ComputeStream(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            using (SHA256 sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(stream));
            }
        }

        /// <summary>
        /// Computes a SHA-256 hash for a file.
        /// </summary>
        public static string ComputeFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return ComputeStream(stream);
            }
        }

        /// <summary>
        /// Computes a SHA-256 hash for UTF-8 text.
        /// </summary>
        public static string ComputeText(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            return ComputeBytes(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Normalizes a hash string for comparison.
        /// </summary>
        public static string Normalize(string hash)
        {
            if (hash == null)
            {
                return null;
            }

            string value = hash.Trim();
            if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("sha256:".Length);
            }

            return value.Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>
        /// Compares two SHA-256 hash strings after normalization.
        /// </summary>
        public static bool Equals(string expected, string actual)
        {
            return string.Equals(Normalize(expected), Normalize(actual), StringComparison.OrdinalIgnoreCase);
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
