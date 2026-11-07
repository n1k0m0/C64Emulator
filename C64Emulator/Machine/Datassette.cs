// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>Cassette transport with cycle-timed TAP playback and recording.</summary>
    internal sealed class Datassette
    {
        private readonly double _machineClockHz;
        private TapImage _image;
        private string _path = string.Empty;
        private int _pulseIndex;
        private int _cyclesToPulse;
        private readonly List<int> _recordedPulses = new List<int>();
        private string _recordingPath = string.Empty;
        private bool _recordMotorRunning;
        private bool _recordSawFirstRise;
        private bool _recordWriteHigh;
        private int _cyclesSinceRise;

        public Datassette(double machineClockHz)
        {
            _machineClockHz = machineClockHz;
        }

        public bool HasTape { get { return _image != null; } }
        public bool PlayPressed { get; set; }
        public string Path { get { return _path; } }
        public int PulseIndex { get { return _pulseIndex; } }
        public int CyclesToPulse { get { return _cyclesToPulse; } }
        public bool IsRecording { get { return _recordingPath.Length != 0; } }
        public string RecordingPath { get { return _recordingPath; } }
        public int RecordedPulseCount { get { return _recordedPulses.Count; } }

        public void Mount(string path)
        {
            if (IsRecording) StopRecording();
            TapImage image = TapImage.Load(path);
            _image = image;
            _path = path;
            _pulseIndex = 0;
            _cyclesToPulse = 0;
            PlayPressed = true;
        }

        public void Eject()
        {
            if (IsRecording) StopRecording();
            _image = null;
            _path = string.Empty;
            _pulseIndex = 0;
            _cyclesToPulse = 0;
            PlayPressed = false;
        }

        public void Rewind()
        {
            _pulseIndex = 0;
            _cyclesToPulse = 0;
        }

        public void StartRecording(string path)
        {
            if (IsRecording) throw new InvalidOperationException("A TAP recording is already active.");
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(System.IO.Path.GetExtension(path), ".tap",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A .tap output path is required.", nameof(path));
            }
            if (File.Exists(path)) throw new IOException("TAP output already exists.");

            _image = null;
            _path = string.Empty;
            _pulseIndex = 0;
            _cyclesToPulse = 0;
            _recordedPulses.Clear();
            _recordingPath = path;
            _recordMotorRunning = false;
            _recordSawFirstRise = false;
            _recordWriteHigh = false;
            _cyclesSinceRise = 0;
            PlayPressed = true;
        }

        public string StopRecording()
        {
            if (!IsRecording) return string.Empty;
            string path = _recordingPath;
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            byte[] tap = TapImage.Encode(_recordedPulses,
                checked((int)Math.Round(_machineClockHz)));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(tap, 0, tap.Length);
            }
            _recordingPath = string.Empty;
            _recordMotorRunning = false;
            _recordedPulses.Clear();
            PlayPressed = false;
            return path;
        }

        public bool Tick(bool motorOn, bool writeHigh)
        {
            if (IsRecording)
            {
                if (!motorOn || !PlayPressed)
                {
                    _recordMotorRunning = false;
                    _recordWriteHigh = writeHigh;
                    return false;
                }
                if (!_recordMotorRunning)
                {
                    _recordMotorRunning = true;
                    _recordWriteHigh = writeHigh;
                    return false;
                }
                _cyclesSinceRise++;
                if (!_recordWriteHigh && writeHigh)
                {
                    if (_recordSawFirstRise)
                    {
                        _recordedPulses.Add(_cyclesSinceRise);
                    }
                    _recordSawFirstRise = true;
                    _cyclesSinceRise = 0;
                }
                _recordWriteHigh = writeHigh;
                return false;
            }
            if (!motorOn || !PlayPressed || _image == null || _pulseIndex >= _image.PulseCount)
            {
                return false;
            }

            if (_cyclesToPulse == 0)
            {
                _cyclesToPulse = Math.Max(1, (int)Math.Round(
                    _image.GetPulseCycles(_pulseIndex) * _machineClockHz / _image.SourceClockHz));
            }

            if (--_cyclesToPulse != 0)
            {
                return false;
            }

            _pulseIndex++;
            return true;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_path);
            BinaryStateIO.WriteByteArray(writer, _image != null ? _image.RawData : null);
            writer.Write(PlayPressed);
            writer.Write(_pulseIndex);
            writer.Write(_cyclesToPulse);
            writer.Write(IsRecording);
            if (IsRecording)
            {
                writer.Write(_recordingPath);
                writer.Write(_recordMotorRunning);
                writer.Write(_recordSawFirstRise);
                writer.Write(_recordWriteHigh);
                writer.Write(_cyclesSinceRise);
                writer.Write(_recordedPulses.Count);
                foreach (int pulse in _recordedPulses) writer.Write(pulse);
            }
        }

        public void LoadState(BinaryReader reader, int version)
        {
            string path = reader.ReadString();
            byte[] imageData = BinaryStateIO.ReadByteArray(reader);
            bool playPressed = reader.ReadBoolean();
            int pulseIndex = reader.ReadInt32();
            int cyclesToPulse = reader.ReadInt32();
            _recordingPath = string.Empty;
            _recordedPulses.Clear();
            _recordMotorRunning = false;
            _recordSawFirstRise = false;
            _recordWriteHigh = false;
            _cyclesSinceRise = 0;
            if (imageData == null)
            {
                _image = null;
                _path = string.Empty;
                _pulseIndex = 0;
                _cyclesToPulse = 0;
            }
            else
            {
                _image = TapImage.Parse(imageData);
                _path = path;
                if (pulseIndex < 0 || pulseIndex > _image.PulseCount || cyclesToPulse < 0)
                {
                    throw new InvalidDataException("Invalid cassette transport state.");
                }
                _pulseIndex = pulseIndex;
                _cyclesToPulse = cyclesToPulse;
            }
            if (version >= 4 && reader.ReadBoolean())
            {
                if (_image != null) throw new InvalidDataException("Conflicting tape transport state.");
                _recordingPath = reader.ReadString();
                _recordMotorRunning = reader.ReadBoolean();
                _recordSawFirstRise = reader.ReadBoolean();
                _recordWriteHigh = reader.ReadBoolean();
                _cyclesSinceRise = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (string.IsNullOrWhiteSpace(_recordingPath) || _cyclesSinceRise < 0 ||
                    count < 0 || count > 10000000)
                {
                    throw new InvalidDataException("Invalid cassette recording state.");
                }
                for (int index = 0; index < count; index++)
                {
                    int pulse = reader.ReadInt32();
                    if (pulse <= 0) throw new InvalidDataException("Invalid TAP pulse.");
                    _recordedPulses.Add(pulse);
                }
            }
            PlayPressed = playPressed && (_image != null || IsRecording);
        }
    }
}
