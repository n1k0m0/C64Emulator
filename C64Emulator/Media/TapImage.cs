// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>Decoded C64 TAP pulses, measured in source CPU clock cycles.</summary>
    internal sealed class TapImage
    {
        private const string Signature = "C64-TAPE-RAW";
        private readonly int[] _pulseCycles;
        private readonly byte[] _rawData;

        private TapImage(int[] pulseCycles, int sourceClockHz, byte[] rawData)
        {
            _pulseCycles = pulseCycles;
            _rawData = rawData;
            SourceClockHz = sourceClockHz;
        }

        public int PulseCount { get { return _pulseCycles.Length; } }
        public int SourceClockHz { get; }
        public int GetPulseCycles(int index) { return _pulseCycles[index]; }
        public byte[] RawData { get { return _rawData; } }

        public static TapImage Load(string path)
        {
            return Parse(File.ReadAllBytes(path));
        }

        public static byte[] Encode(IReadOnlyList<int> pulseCycles, int sourceClockHz)
        {
            if (pulseCycles == null) throw new ArgumentNullException(nameof(pulseCycles));
            byte videoStandard;
            switch (sourceClockHz)
            {
                case 985248: videoStandard = 0; break;
                case 1022730: videoStandard = 1; break;
                case 1023440: videoStandard = 3; break;
                default: throw new ArgumentOutOfRangeException(nameof(sourceClockHz));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes(Signature));
                writer.Write((byte)1); // TAP v1 stores exact long pulse lengths.
                writer.Write((byte)0); // C64
                writer.Write(videoStandard);
                writer.Write((byte)0);
                writer.Write(0); // patched with the data length below
                foreach (int cycles in pulseCycles)
                {
                    if (cycles <= 0 || cycles > 0xFFFFFF)
                    {
                        throw new InvalidDataException("TAP pulse length is out of range.");
                    }
                    if (cycles <= 2040 && (cycles & 7) == 0)
                    {
                        writer.Write((byte)(cycles / 8));
                    }
                    else
                    {
                        writer.Write((byte)0);
                        writer.Write((byte)cycles);
                        writer.Write((byte)(cycles >> 8));
                        writer.Write((byte)(cycles >> 16));
                    }
                }

                int length = checked((int)stream.Length - 20);
                stream.Position = 16;
                writer.Write(length);
                return stream.ToArray();
            }
        }

        public static TapImage Parse(byte[] data)
        {
            if (data == null || data.Length < 20 ||
                Encoding.ASCII.GetString(data, 0, 12) != Signature)
            {
                throw new InvalidDataException("Invalid C64 TAP header.");
            }

            int version = data[12];
            if (version != 0 && version != 1)
            {
                throw new InvalidDataException("Only C64 TAP versions 0 and 1 are supported.");
            }

            if (data[13] != 0)
            {
                throw new InvalidDataException("TAP image is not for a C64.");
            }

            int sourceClockHz;
            switch (data[14])
            {
                case 0: sourceClockHz = 985248; break;
                case 1:
                case 2: sourceClockHz = 1022730; break;
                case 3: sourceClockHz = 1023440; break;
                default: throw new InvalidDataException("Unsupported TAP video standard.");
            }

            uint declaredLength = (uint)(data[16] | data[17] << 8 | data[18] << 16 | data[19] << 24);
            if (declaredLength > data.Length - 20)
            {
                throw new InvalidDataException("Truncated TAP image.");
            }

            int end = 20 + (int)declaredLength;
            var pulses = new List<int>();
            for (int index = 20; index < end; index++)
            {
                int value = data[index];
                if (value != 0)
                {
                    pulses.Add(value * 8);
                }
                else if (version == 0)
                {
                    // Version 0 cannot express the exact overflow interval.
                    pulses.Add(2048);
                }
                else
                {
                    if (end - index < 4)
                    {
                        throw new InvalidDataException("Truncated TAP extended pulse.");
                    }

                    value = data[++index] | data[++index] << 8 | data[++index] << 16;
                    if (value == 0)
                    {
                        throw new InvalidDataException("Zero-length TAP pulse.");
                    }

                    pulses.Add(value);
                }
            }

            return new TapImage(pulses.ToArray(), sourceClockHz, data);
        }
    }
}
