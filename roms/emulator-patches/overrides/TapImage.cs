// SPDX-License-Identifier: Apache-2.0
// Raw timing representation; no program/ROM decoding or loader shortcut.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace C64Emulator.Core
{
    public sealed class TapImage
    {
        public byte Version { get; private set; }
        public byte VideoStandard { get; private set; }
        public int[] Durations { get; private set; }
        public double ClockHz => ClockFor(VideoStandard);
        public bool HalfWaves => Version == 2;

        private TapImage() { Durations = Array.Empty<int>(); }

        public static double ClockFor(byte videoStandard)
        {
            return videoStandard switch
            {
                0 => 985248.0,
                1 or 2 => 1022727.0,
                3 => 1023440.0,
                _ => throw new InvalidDataException("Unknown C64 TAP video standard.")
            };
        }

        public static TapImage Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20 ||
                Encoding.ASCII.GetString(bytes, 0, 12) != "C64-TAPE-RAW")
                throw new InvalidDataException("Not a C64 TAP image.");
            if (bytes[12] > 2 || bytes[13] != 0 || bytes[15] != 0)
                throw new InvalidDataException("Unsupported TAP version, machine or reserved field.");
            _ = ClockFor(bytes[14]);
            uint length = (uint)(bytes[16] | bytes[17] << 8 | bytes[18] << 16 | bytes[19] << 24);
            if (length != bytes.Length - 20)
                throw new InvalidDataException("TAP payload length does not match its header.");
            var pulses = new List<int>();
            for (int i = 20; i < bytes.Length; i++)
            {
                int duration = bytes[i] * 8;
                if (duration == 0)
                {
                    if (bytes[12] == 0) duration = 2048;
                    else
                    {
                        if (i + 3 >= bytes.Length) throw new InvalidDataException("Truncated extended TAP pulse.");
                        duration = bytes[++i] | bytes[++i] << 8 | bytes[++i] << 16;
                        if (duration == 0) throw new InvalidDataException("A TAP pulse must have a positive duration.");
                    }
                }
                pulses.Add(duration);
            }
            return new TapImage { Version = bytes[12], VideoStandard = bytes[14], Durations = pulses.ToArray() };
        }

        public static byte[] Encode(IEnumerable<int> durations, byte videoStandard = 0, byte version = 1)
        {
            if (version > 2) throw new ArgumentOutOfRangeException(nameof(version));
            _ = ClockFor(videoStandard);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("C64-TAPE-RAW"));
            writer.Write(version); writer.Write((byte)0); writer.Write(videoStandard); writer.Write((byte)0);
            writer.Write(0);
            foreach (int duration in durations)
            {
                if (duration <= 0 || duration > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(durations));
                if (duration % 8 == 0 && duration <= 2040) writer.Write((byte)(duration / 8));
                else if (version == 0 && duration == 2048) writer.Write((byte)0);
                else if (version == 0) throw new InvalidDataException("TAP v0 cannot represent this cycle-exact pulse.");
                else
                {
                    writer.Write((byte)0);
                    writer.Write((byte)duration); writer.Write((byte)(duration >> 8)); writer.Write((byte)(duration >> 16));
                }
            }
            writer.Flush();
            stream.Position = 16;
            writer.Write(checked((uint)(stream.Length - 20)));
            writer.Flush();
            return stream.ToArray();
        }
    }
}
