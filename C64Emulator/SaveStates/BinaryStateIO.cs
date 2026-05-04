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
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Provides small binary helpers shared by savestate readers and writers.
    /// </summary>
    internal static class BinaryStateIO
    {
        /// <summary>
        /// Writes a nullable byte array with a length prefix.
        /// </summary>
        public static void WriteByteArray(BinaryWriter writer, byte[] value)
        {
            writer.Write(value != null);
            if (value == null)
            {
                return;
            }

            writer.Write(value.Length);
            writer.Write(value);
        }

        /// <summary>
        /// Reads a nullable byte array written with <see cref="WriteByteArray"/>.
        /// </summary>
        public static byte[] ReadByteArray(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int length = reader.ReadInt32();
            return reader.ReadBytes(length);
        }

        /// <summary>
        /// Writes a nullable unsigned integer array with a length prefix.
        /// </summary>
        public static void WriteUIntArray(BinaryWriter writer, uint[] value)
        {
            writer.Write(value != null);
            if (value == null)
            {
                return;
            }

            writer.Write(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                writer.Write(value[index]);
            }
        }

        /// <summary>
        /// Reads a nullable unsigned integer array written with <see cref="WriteUIntArray"/>.
        /// </summary>
        public static uint[] ReadUIntArray(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int length = reader.ReadInt32();
            var value = new uint[length];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = reader.ReadUInt32();
            }

            return value;
        }

        /// <summary>
        /// Writes a nullable string using an explicit presence marker.
        /// </summary>
        public static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }

        /// <summary>
        /// Reads a nullable string written with <see cref="WriteString"/>.
        /// </summary>
        public static string ReadString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadString() : null;
        }

        /// <summary>
        /// Writes a nullable byte list with a count prefix.
        /// </summary>
        public static void WriteByteList(BinaryWriter writer, List<byte> value)
        {
            writer.Write(value != null);
            if (value == null)
            {
                return;
            }

            writer.Write(value.Count);
            for (int index = 0; index < value.Count; index++)
            {
                writer.Write(value[index]);
            }
        }

        /// <summary>
        /// Reads a nullable byte list written with <see cref="WriteByteList"/>.
        /// </summary>
        public static List<byte> ReadByteList(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var result = new List<byte>(count);
            for (int index = 0; index < count; index++)
            {
                result.Add(reader.ReadByte());
            }

            return result;
        }

        /// <summary>
        /// Writes a string list with a count prefix.
        /// </summary>
        public static void WriteStringList(BinaryWriter writer, List<string> value)
        {
            writer.Write(value != null);
            if (value == null)
            {
                return;
            }

            writer.Write(value.Count);
            for (int index = 0; index < value.Count; index++)
            {
                WriteString(writer, value[index]);
            }
        }

        /// <summary>
        /// Reads a string list written with <see cref="WriteStringList"/>.
        /// </summary>
        public static List<string> ReadStringList(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var result = new List<string>(count);
            for (int index = 0; index < count; index++)
            {
                result.Add(ReadString(reader));
            }

            return result;
        }

        /// <summary>
        /// Writes a set of integer values.
        /// </summary>
        public static void WriteIntSet(BinaryWriter writer, HashSet<int> value)
        {
            writer.Write(value != null);
            if (value == null)
            {
                return;
            }

            writer.Write(value.Count);
            foreach (int entry in value)
            {
                writer.Write(entry);
            }
        }

        /// <summary>
        /// Reads a set of integer values.
        /// </summary>
        public static HashSet<int> ReadIntSet(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var result = new HashSet<int>();
            for (int index = 0; index < count; index++)
            {
                result.Add(reader.ReadInt32());
            }

            return result;
        }
    }
}
