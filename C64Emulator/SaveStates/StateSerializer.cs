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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace C64Emulator.Core
{
    /// <summary>
    /// Serializes private component fields whose state is purely data.
    /// </summary>
    internal static class StateSerializer
    {
        /// <summary>
        /// Writes all serializable instance fields of an object.
        /// </summary>
        public static void WriteObjectFields(BinaryWriter writer, object instance, params string[] excludedFieldNames)
        {
            FieldInfo[] fields = GetSerializableFields(instance.GetType(), excludedFieldNames);
            writer.Write(fields.Length);
            for (int index = 0; index < fields.Length; index++)
            {
                FieldInfo field = fields[index];
                writer.Write(field.Name);
                WriteValue(writer, field.FieldType, field.GetValue(instance));
            }
        }

        /// <summary>
        /// Restores all serializable instance fields of an object.
        /// </summary>
        public static void ReadObjectFields(BinaryReader reader, object instance, params string[] excludedFieldNames)
        {
            var fieldsByName = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            FieldInfo[] fields = GetSerializableFields(instance.GetType(), excludedFieldNames);
            for (int index = 0; index < fields.Length; index++)
            {
                fieldsByName[fields[index].Name] = fields[index];
            }

            int count = reader.ReadInt32();
            for (int index = 0; index < count; index++)
            {
                string fieldName = reader.ReadString();
                FieldInfo field;
                if (!fieldsByName.TryGetValue(fieldName, out field))
                {
                    throw new InvalidDataException("Savestate field mismatch: " + fieldName);
                }

                object value = ReadValue(reader, field.FieldType);
                AssignFieldValue(instance, field, value);
            }
        }

        /// <summary>
        /// Writes a typed value.
        /// </summary>
        private static void WriteValue(BinaryWriter writer, Type type, object value)
        {
            if (type == typeof(bool))
            {
                writer.Write((bool)value);
                return;
            }

            if (type == typeof(byte))
            {
                writer.Write((byte)value);
                return;
            }

            if (type == typeof(sbyte))
            {
                writer.Write((sbyte)value);
                return;
            }

            if (type == typeof(short))
            {
                writer.Write((short)value);
                return;
            }

            if (type == typeof(ushort))
            {
                writer.Write((ushort)value);
                return;
            }

            if (type == typeof(int))
            {
                writer.Write((int)value);
                return;
            }

            if (type == typeof(uint))
            {
                writer.Write((uint)value);
                return;
            }

            if (type == typeof(long))
            {
                writer.Write((long)value);
                return;
            }

            if (type == typeof(ulong))
            {
                writer.Write((ulong)value);
                return;
            }

            if (type == typeof(float))
            {
                writer.Write((float)value);
                return;
            }

            if (type == typeof(double))
            {
                writer.Write((double)value);
                return;
            }

            if (type == typeof(string))
            {
                BinaryStateIO.WriteString(writer, (string)value);
                return;
            }

            if (type.IsEnum)
            {
                writer.Write(Convert.ToInt32(value));
                return;
            }

            if (type.IsArray)
            {
                WriteArray(writer, type.GetElementType(), (Array)value);
                return;
            }

            if (IsGenericList(type))
            {
                WriteList(writer, type.GetGenericArguments()[0], (IList)value);
                return;
            }

            if (type == typeof(HashSet<int>))
            {
                BinaryStateIO.WriteIntSet(writer, (HashSet<int>)value);
                return;
            }

            if (type.IsValueType)
            {
                WriteObjectFields(writer, value);
                return;
            }

            writer.Write(value != null);
            if (value != null)
            {
                WriteObjectFields(writer, value);
            }
        }

        /// <summary>
        /// Reads a typed value.
        /// </summary>
        private static object ReadValue(BinaryReader reader, Type type)
        {
            if (type == typeof(bool))
            {
                return reader.ReadBoolean();
            }

            if (type == typeof(byte))
            {
                return reader.ReadByte();
            }

            if (type == typeof(sbyte))
            {
                return reader.ReadSByte();
            }

            if (type == typeof(short))
            {
                return reader.ReadInt16();
            }

            if (type == typeof(ushort))
            {
                return reader.ReadUInt16();
            }

            if (type == typeof(int))
            {
                return reader.ReadInt32();
            }

            if (type == typeof(uint))
            {
                return reader.ReadUInt32();
            }

            if (type == typeof(long))
            {
                return reader.ReadInt64();
            }

            if (type == typeof(ulong))
            {
                return reader.ReadUInt64();
            }

            if (type == typeof(float))
            {
                return reader.ReadSingle();
            }

            if (type == typeof(double))
            {
                return reader.ReadDouble();
            }

            if (type == typeof(string))
            {
                return BinaryStateIO.ReadString(reader);
            }

            if (type.IsEnum)
            {
                return Enum.ToObject(type, reader.ReadInt32());
            }

            if (type.IsArray)
            {
                return ReadArray(reader, type.GetElementType());
            }

            if (IsGenericList(type))
            {
                return ReadList(reader, type, type.GetGenericArguments()[0]);
            }

            if (type == typeof(HashSet<int>))
            {
                return BinaryStateIO.ReadIntSet(reader);
            }

            if (type.IsValueType)
            {
                object boxed = Activator.CreateInstance(type);
                ReadObjectFields(reader, boxed);
                return boxed;
            }

            if (!reader.ReadBoolean())
            {
                return null;
            }

            object instance = Activator.CreateInstance(type, true);
            ReadObjectFields(reader, instance);
            return instance;
        }

        /// <summary>
        /// Writes a nullable array of any supported element type.
        /// </summary>
        private static void WriteArray(BinaryWriter writer, Type elementType, Array array)
        {
            writer.Write(array != null);
            if (array == null)
            {
                return;
            }

            writer.Write(array.Rank);
            for (int dimension = 0; dimension < array.Rank; dimension++)
            {
                writer.Write(array.GetLength(dimension));
            }

            int[] indices = new int[array.Rank];
            WriteArrayElements(writer, elementType, array, indices, 0);
        }

        /// <summary>
        /// Writes array elements recursively so multidimensional arrays keep their shape.
        /// </summary>
        private static void WriteArrayElements(BinaryWriter writer, Type elementType, Array array, int[] indices, int dimension)
        {
            if (dimension == array.Rank)
            {
                WriteValue(writer, elementType, array.GetValue(indices));
                return;
            }

            for (int index = 0; index < array.GetLength(dimension); index++)
            {
                indices[dimension] = index;
                WriteArrayElements(writer, elementType, array, indices, dimension + 1);
            }
        }

        /// <summary>
        /// Reads a nullable array written with <see cref="WriteArray"/>.
        /// </summary>
        private static Array ReadArray(BinaryReader reader, Type elementType)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int rank = reader.ReadInt32();
            var lengths = new int[rank];
            for (int dimension = 0; dimension < rank; dimension++)
            {
                lengths[dimension] = reader.ReadInt32();
            }

            Array array = Array.CreateInstance(elementType, lengths);
            var indices = new int[rank];
            ReadArrayElements(reader, elementType, array, indices, 0);
            return array;
        }

        /// <summary>
        /// Reads array elements recursively.
        /// </summary>
        private static void ReadArrayElements(BinaryReader reader, Type elementType, Array array, int[] indices, int dimension)
        {
            if (dimension == array.Rank)
            {
                array.SetValue(ReadValue(reader, elementType), indices);
                return;
            }

            for (int index = 0; index < array.GetLength(dimension); index++)
            {
                indices[dimension] = index;
                ReadArrayElements(reader, elementType, array, indices, dimension + 1);
            }
        }

        /// <summary>
        /// Writes a nullable generic list.
        /// </summary>
        private static void WriteList(BinaryWriter writer, Type elementType, IList list)
        {
            writer.Write(list != null);
            if (list == null)
            {
                return;
            }

            writer.Write(list.Count);
            for (int index = 0; index < list.Count; index++)
            {
                WriteValue(writer, elementType, list[index]);
            }
        }

        /// <summary>
        /// Reads a nullable generic list.
        /// </summary>
        private static object ReadList(BinaryReader reader, Type listType, Type elementType)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            IList list = (IList)Activator.CreateInstance(listType, true);
            for (int index = 0; index < count; index++)
            {
                list.Add(ReadValue(reader, elementType));
            }

            return list;
        }

        /// <summary>
        /// Assigns a field while preserving readonly array/list instances where possible.
        /// </summary>
        private static void AssignFieldValue(object instance, FieldInfo field, object value)
        {
            object existing = field.GetValue(instance);
            if (field.IsInitOnly && existing is Array existingArray && value is Array valueArray)
            {
                if (existingArray.Rank == valueArray.Rank && existingArray.Length == valueArray.Length)
                {
                    Array.Copy(valueArray, existingArray, valueArray.Length);
                    return;
                }
            }

            if (field.IsInitOnly && existing is IList existingList && value is IList valueList)
            {
                existingList.Clear();
                for (int index = 0; index < valueList.Count; index++)
                {
                    existingList.Add(valueList[index]);
                }

                return;
            }

            field.SetValue(instance, value);
        }

        /// <summary>
        /// Gets deterministic serializable instance fields.
        /// </summary>
        private static FieldInfo[] GetSerializableFields(Type type, params string[] excludedFieldNames)
        {
            var excluded = new HashSet<string>(excludedFieldNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            var fields = new List<FieldInfo>();
            FieldInfo[] allFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int index = 0; index < allFields.Length; index++)
            {
                FieldInfo field = allFields[index];
                if (field.IsStatic || field.IsLiteral || excluded.Contains(field.Name))
                {
                    continue;
                }

                if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                fields.Add(field);
            }

            fields.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            return fields.ToArray();
        }

        /// <summary>
        /// Returns whether a type is a concrete generic list.
        /// </summary>
        private static bool IsGenericList(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
        }
    }
}
