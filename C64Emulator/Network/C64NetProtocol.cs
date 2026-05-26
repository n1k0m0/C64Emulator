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
using System.Net.Sockets;
using System.Text;

namespace C64Emulator.Network
{
    /// <summary>
    /// Lists the supported C64 network protocol messages.
    /// </summary>
    public enum C64NetMessageType : ushort
    {
        ClientHello = 1,
        ServerWelcome = 2,
        ServerReject = 3,
        InputState = 4,
        VideoFrame = 5,
        AudioChunk = 6,
        ClientList = 7,
        PermissionUpdate = 8,
        Ping = 9,
        Pong = 10,
        Disconnect = 11,
        HostOverlayStatus = 12
    }

    /// <summary>
    /// Lists the client-visible session roles.
    /// </summary>
    public enum C64NetClientRole : byte
    {
        Observer = 0,
        Player = 1
    }

    /// <summary>
    /// Lists joystick permissions granted by the host.
    /// </summary>
    public enum C64NetJoystickPermission : byte
    {
        Observer = 0,
        Port1 = 1,
        Port2 = 2,
        Both = 3
    }

    /// <summary>
    /// Represents a single framed network message.
    /// </summary>
    public sealed class C64NetMessage
    {
        public C64NetMessageType Type { get; set; }
        public ushort Flags { get; set; }
        public uint Sequence { get; set; }
        public long Timestamp { get; set; }
        public byte[] Payload { get; set; }
    }

    /// <summary>
    /// Represents a connected client snapshot for UI and protocol updates.
    /// </summary>
    public sealed class C64NetClientSnapshot
    {
        public int ClientId { get; set; }
        public string Name { get; set; }
        public string RemoteAddress { get; set; }
        public string RemoteEndpoint { get; set; }
        public C64NetClientRole Role { get; set; }
        public C64NetJoystickPermission Permission { get; set; }
        public byte JoystickState { get; set; }
        public int LatencyMilliseconds { get; set; }
        public bool Connected { get; set; }
    }

    /// <summary>
    /// Represents a decoded remote video frame.
    /// </summary>
    public sealed class C64NetVideoFrame
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public long FrameId { get; set; }
        public uint[] Pixels { get; set; }
    }

    /// <summary>
    /// Implements C64Net v1 framing and payload helpers.
    /// </summary>
    public static class C64NetProtocol
    {
        public const int Version = 1;
        public const int DefaultPort = 6464;
        public const int DefaultAudioSampleRate = 44100;
        public const int HeaderLength = 20;
        public const int MaxPayloadLength = 16 * 1024 * 1024;
        private const int VideoFormatArgb32 = 0;
        private const int VideoFormatC64Palette4 = 1;

        private static readonly byte[] EmptyPayload = new byte[0];
        private static readonly uint[] C64Palette =
        {
            0xFF000000u,
            0xFFFFFFFFu,
            0xFF68372Bu,
            0xFF70A4B2u,
            0xFF6F3D86u,
            0xFF588D43u,
            0xFF352879u,
            0xFFB8C76Fu,
            0xFF6F4F25u,
            0xFF433900u,
            0xFF9A6759u,
            0xFF444444u,
            0xFF6C6C6Cu,
            0xFF9AD284u,
            0xFF6C5EB5u,
            0xFF959595u
        };
        private static readonly Dictionary<uint, byte> C64PaletteLookup = CreateC64PaletteLookup();

        /// <summary>
        /// Writes a framed message to the network stream.
        /// </summary>
        public static void WriteMessage(NetworkStream stream, C64NetMessage message)
        {
            if (stream == null || message == null)
            {
                return;
            }

            byte[] payload = message.Payload ?? EmptyPayload;
            if (payload.Length > MaxPayloadLength)
            {
                throw new InvalidDataException("C64Net payload is too large.");
            }

            byte[] header = new byte[HeaderLength];
            WriteInt32(header, 0, payload.Length);
            WriteUInt16(header, 4, (ushort)message.Type);
            WriteUInt16(header, 6, message.Flags);
            WriteUInt32(header, 8, message.Sequence);
            WriteInt64(header, 12, message.Timestamp);
            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
            {
                stream.Write(payload, 0, payload.Length);
            }
        }

        /// <summary>
        /// Reads one framed message from the network stream.
        /// </summary>
        public static C64NetMessage ReadMessage(NetworkStream stream)
        {
            if (stream == null)
            {
                return null;
            }

            byte[] header = new byte[HeaderLength];
            if (!ReadExact(stream, header, 0, header.Length))
            {
                return null;
            }

            int payloadLength = ReadInt32(header, 0);
            if (payloadLength < 0 || payloadLength > MaxPayloadLength)
            {
                throw new InvalidDataException("Invalid C64Net payload length.");
            }

            byte[] payload = payloadLength == 0 ? EmptyPayload : new byte[payloadLength];
            if (payloadLength > 0 && !ReadExact(stream, payload, 0, payloadLength))
            {
                return null;
            }

            return new C64NetMessage
            {
                Type = (C64NetMessageType)ReadUInt16(header, 4),
                Flags = ReadUInt16(header, 6),
                Sequence = ReadUInt32(header, 8),
                Timestamp = ReadInt64(header, 12),
                Payload = payload
            };
        }

        /// <summary>
        /// Builds a ClientHello payload.
        /// </summary>
        public static byte[] CreateClientHelloPayload(string name, string password, C64NetClientRole role)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Version);
                WriteString(writer, name);
                WriteString(writer, password);
                writer.Write((byte)role);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Parses a ClientHello payload.
        /// </summary>
        public static void ReadClientHelloPayload(byte[] payload, out int version, out string name, out string password, out C64NetClientRole role)
        {
            using (var stream = new MemoryStream(payload ?? EmptyPayload))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                version = reader.ReadInt32();
                name = ReadString(reader);
                password = ReadString(reader);
                role = (C64NetClientRole)reader.ReadByte();
            }
        }

        /// <summary>
        /// Builds a ServerWelcome payload.
        /// </summary>
        public static byte[] CreateServerWelcomePayload(int clientId, int width, int height, int audioSampleRate, C64NetClientRole role, C64NetJoystickPermission permission, string status)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(clientId);
                writer.Write(width);
                writer.Write(height);
                writer.Write(audioSampleRate);
                writer.Write((byte)role);
                writer.Write((byte)permission);
                WriteString(writer, status);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Parses a ServerWelcome payload.
        /// </summary>
        public static void ReadServerWelcomePayload(byte[] payload, out int clientId, out int width, out int height, out int audioSampleRate, out C64NetClientRole role, out C64NetJoystickPermission permission, out string status)
        {
            using (var stream = new MemoryStream(payload ?? EmptyPayload))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                clientId = reader.ReadInt32();
                width = reader.ReadInt32();
                height = reader.ReadInt32();
                audioSampleRate = reader.ReadInt32();
                role = (C64NetClientRole)reader.ReadByte();
                permission = (C64NetJoystickPermission)reader.ReadByte();
                status = ReadString(reader);
            }
        }

        /// <summary>
        /// Builds a text-only payload.
        /// </summary>
        public static byte[] CreateTextPayload(string text)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, text);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Reads a text-only payload.
        /// </summary>
        public static string ReadTextPayload(byte[] payload)
        {
            using (var stream = new MemoryStream(payload ?? EmptyPayload))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                return ReadString(reader);
            }
        }

        /// <summary>
        /// Builds an input-state payload.
        /// </summary>
        public static byte[] CreateInputStatePayload(byte activeLowJoystickState)
        {
            return new[] { (byte)(activeLowJoystickState | 0xE0) };
        }

        /// <summary>
        /// Reads an input-state payload.
        /// </summary>
        public static byte ReadInputStatePayload(byte[] payload)
        {
            return payload != null && payload.Length > 0 ? (byte)(payload[0] | 0xE0) : (byte)0xFF;
        }

        /// <summary>
        /// Builds a compact C64 palette video frame payload.
        /// </summary>
        public static byte[] CreateVideoFramePayload(uint[] pixels, int width, int height, long frameId)
        {
            int pixelCount = width * height;
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < pixelCount)
            {
                return EmptyPayload;
            }

            int encodedLength = (pixelCount + 1) / 2;
            byte[] payload = new byte[24 + encodedLength];
            WriteInt32(payload, 0, width);
            WriteInt32(payload, 4, height);
            WriteInt64(payload, 8, frameId);
            WriteInt32(payload, 16, VideoFormatC64Palette4);
            WriteInt32(payload, 20, encodedLength);
            int offset = 24;
            for (int index = 0; index < pixelCount; index += 2)
            {
                byte left = FindC64PaletteIndex(pixels[index]);
                byte right = index + 1 < pixelCount ? FindC64PaletteIndex(pixels[index + 1]) : (byte)0;
                payload[offset++] = (byte)(left | (right << 4));
            }

            return payload;
        }

        /// <summary>
        /// Parses a video frame payload into ARGB pixels.
        /// </summary>
        public static C64NetVideoFrame ReadVideoFramePayload(byte[] payload)
        {
            if (payload == null || payload.Length < 20)
            {
                return null;
            }

            int width = ReadInt32(payload, 0);
            int height = ReadInt32(payload, 4);
            long frameId = ReadInt64(payload, 8);
            int formatOrByteCount = ReadInt32(payload, 16);
            int pixelCount = width * height;
            if (width <= 0 || height <= 0 || pixelCount <= 0)
            {
                return null;
            }

            uint[] pixels = new uint[pixelCount];
            if (payload.Length >= 24 && formatOrByteCount == VideoFormatC64Palette4)
            {
                int encodedLength = ReadInt32(payload, 20);
                if (encodedLength != (pixelCount + 1) / 2 || payload.Length < 24 + encodedLength)
                {
                    return null;
                }

                int offset = 24;
                for (int index = 0; index < pixelCount; index += 2)
                {
                    byte packed = payload[offset++];
                    pixels[index] = C64Palette[packed & 0x0F];
                    if (index + 1 < pixelCount)
                    {
                        pixels[index + 1] = C64Palette[(packed >> 4) & 0x0F];
                    }
                }
            }
            else
            {
                int byteCount = formatOrByteCount;
                if (byteCount != pixelCount * 4 || payload.Length < 20 + byteCount)
                {
                    return null;
                }

                int offset = 20;
                for (int index = 0; index < pixelCount; index++)
                {
                    pixels[index] =
                        (uint)(payload[offset] |
                        (payload[offset + 1] << 8) |
                        (payload[offset + 2] << 16) |
                        (payload[offset + 3] << 24));
                    offset += 4;
                }
            }

            return new C64NetVideoFrame
            {
                Width = width,
                Height = height,
                FrameId = frameId,
                Pixels = pixels
            };
        }

        private static Dictionary<uint, byte> CreateC64PaletteLookup()
        {
            var lookup = new Dictionary<uint, byte>();
            for (byte index = 0; index < C64Palette.Length; index++)
            {
                lookup[C64Palette[index]] = index;
            }

            return lookup;
        }

        private static byte FindC64PaletteIndex(uint argb)
        {
            byte paletteIndex;
            if (C64PaletteLookup.TryGetValue(argb, out paletteIndex))
            {
                return paletteIndex;
            }

            int bestIndex = 0;
            int bestDistance = int.MaxValue;
            int red = (int)((argb >> 16) & 0xFF);
            int green = (int)((argb >> 8) & 0xFF);
            int blue = (int)(argb & 0xFF);
            for (int index = 0; index < C64Palette.Length; index++)
            {
                uint paletteColor = C64Palette[index];
                int deltaRed = red - (int)((paletteColor >> 16) & 0xFF);
                int deltaGreen = green - (int)((paletteColor >> 8) & 0xFF);
                int deltaBlue = blue - (int)(paletteColor & 0xFF);
                int distance = (deltaRed * deltaRed) + (deltaGreen * deltaGreen) + (deltaBlue * deltaBlue);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }

            return (byte)bestIndex;
        }

        /// <summary>
        /// Builds a PCM audio chunk payload.
        /// </summary>
        public static byte[] CreateAudioChunkPayload(byte[] buffer, int count, int sampleRate)
        {
            if (buffer == null || count <= 0)
            {
                return EmptyPayload;
            }

            count = Math.Min(count, buffer.Length);
            byte[] payload = new byte[8 + count];
            WriteInt32(payload, 0, sampleRate);
            WriteInt32(payload, 4, count);
            Buffer.BlockCopy(buffer, 0, payload, 8, count);
            return payload;
        }

        /// <summary>
        /// Parses a PCM audio chunk payload.
        /// </summary>
        public static bool ReadAudioChunkPayload(byte[] payload, out int sampleRate, out byte[] buffer, out int count)
        {
            sampleRate = DefaultAudioSampleRate;
            buffer = EmptyPayload;
            count = 0;
            if (payload == null || payload.Length < 8)
            {
                return false;
            }

            sampleRate = ReadInt32(payload, 0);
            count = ReadInt32(payload, 4);
            if (sampleRate <= 0 || count <= 0 || payload.Length < 8 + count)
            {
                count = 0;
                return false;
            }

            buffer = new byte[count];
            Buffer.BlockCopy(payload, 8, buffer, 0, count);
            return true;
        }

        /// <summary>
        /// Builds a client-list payload.
        /// </summary>
        public static byte[] CreateClientListPayload(IList<C64NetClientSnapshot> clients)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                int count = clients == null ? 0 : clients.Count;
                writer.Write(count);
                for (int index = 0; index < count; index++)
                {
                    C64NetClientSnapshot client = clients[index];
                    writer.Write(client.ClientId);
                    WriteString(writer, client.Name);
                    WriteString(writer, client.RemoteAddress);
                    WriteString(writer, client.RemoteEndpoint);
                    writer.Write((byte)client.Role);
                    writer.Write((byte)client.Permission);
                    writer.Write(client.JoystickState);
                    writer.Write(client.LatencyMilliseconds);
                    writer.Write(client.Connected);
                }

                return stream.ToArray();
            }
        }

        /// <summary>
        /// Parses a client-list payload.
        /// </summary>
        public static List<C64NetClientSnapshot> ReadClientListPayload(byte[] payload)
        {
            var clients = new List<C64NetClientSnapshot>();
            using (var stream = new MemoryStream(payload ?? EmptyPayload))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                for (int index = 0; index < count; index++)
                {
                    clients.Add(new C64NetClientSnapshot
                    {
                        ClientId = reader.ReadInt32(),
                        Name = ReadString(reader),
                        RemoteAddress = ReadString(reader),
                        RemoteEndpoint = ReadString(reader),
                        Role = (C64NetClientRole)reader.ReadByte(),
                        Permission = (C64NetJoystickPermission)reader.ReadByte(),
                        JoystickState = reader.ReadByte(),
                        LatencyMilliseconds = reader.ReadInt32(),
                        Connected = reader.ReadBoolean()
                    });
                }
            }

            return clients;
        }

        /// <summary>
        /// Builds a permission-update payload.
        /// </summary>
        public static byte[] CreatePermissionPayload(C64NetJoystickPermission permission)
        {
            return new[] { (byte)permission };
        }

        /// <summary>
        /// Reads a permission-update payload.
        /// </summary>
        public static C64NetJoystickPermission ReadPermissionPayload(byte[] payload)
        {
            return payload != null && payload.Length > 0
                ? (C64NetJoystickPermission)payload[0]
                : C64NetJoystickPermission.Observer;
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
                count -= read;
            }

            return true;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 65536)
            {
                throw new InvalidDataException("Invalid C64Net string length.");
            }

            byte[] bytes = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            WriteUInt32(buffer, offset, unchecked((uint)value));
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return unchecked((int)ReadUInt32(buffer, offset));
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return
                (uint)(buffer[offset] |
                (buffer[offset + 1] << 8) |
                (buffer[offset + 2] << 16) |
                (buffer[offset + 3] << 24));
        }

        private static void WriteInt64(byte[] buffer, int offset, long value)
        {
            ulong unsigned = unchecked((ulong)value);
            for (int index = 0; index < 8; index++)
            {
                buffer[offset + index] = (byte)((unsigned >> (index * 8)) & 0xFF);
            }
        }

        private static long ReadInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int index = 0; index < 8; index++)
            {
                value |= ((ulong)buffer[offset + index]) << (index * 8);
            }

            return unchecked((long)value);
        }
    }
}
