// SPDX-License-Identifier: Apache-2.0
// Cycle-driven cassette transport: 6510 motor/WRITE pins and CIA1 FLAG READ.
using System;
using System.Collections.Generic;
using System.IO;

namespace C64Emulator.Core
{
    public sealed class TapeDeck
    {
        private int[] _pulses = Array.Empty<int>();
        private List<int> _recorded = new List<int>();
        private byte _version = 1, _videoStandard;
        private double _cpuHz = 985248.0, _sourceHz = 985248.0, _rounding;
        private int _position, _remaining, _currentDuration;
        private long _recordElapsed;
        private bool _playing, _recording, _motorOn, _writeHigh = true, _readHigh = true;
        private bool _recordEdgeSeen, _halfWaveHigh;

        public bool MotorOn => _motorOn;
        public bool IsPlaying => _playing;
        public bool IsRecording => _recording;
        public bool SensePressed => _playing || _recording;
        public bool ReadHigh => _readHigh;
        public bool EndOfTape => _playing && _position >= _pulses.Length && _remaining == 0;
        public int Position => _position;
        public int PulseCount => _recording || _recorded.Count > 0 ? _recorded.Count : _pulses.Length;
        public byte VideoStandard => _videoStandard;
        public int[] RecordedPulses => _recorded.ToArray();

        public void ConfigureClock(double cpuHz)
        {
            if (cpuHz < 100000 || cpuHz > 2000000) throw new ArgumentOutOfRangeException(nameof(cpuHz));
            _cpuHz = cpuHz;
        }

        public void Mount(byte[] bytes)
        {
            // Parse completely before changing the mounted tape.
            TapImage image = TapImage.Parse(bytes);
            Stop();
            _pulses = image.Durations;
            _version = image.Version;
            _videoStandard = image.VideoStandard;
            _sourceHz = image.ClockHz;
            _recorded.Clear();
            Rewind();
        }

        public void Play()
        {
            if (_recorded.Count > 0)
            {
                _pulses = _recorded.ToArray();
                _recorded.Clear();
                _version = 1;
            }
            _recording = false;
            _playing = true;
            _readHigh = true;
        }

        public void Record(byte videoStandard = 0)
        {
            _sourceHz = TapImage.ClockFor(videoStandard);
            _videoStandard = videoStandard;
            _version = 1;
            _playing = false;
            _recording = true;
            _recorded.Clear();
            _pulses = Array.Empty<int>();
            Rewind();
            _recordElapsed = 0;
            _recordEdgeSeen = false;
            _readHigh = true;
        }

        public void Stop()
        {
            _playing = false;
            _recording = false;
            _readHigh = true;
            _recordEdgeSeen = false;
            _recordElapsed = 0;
        }

        public void Eject()
        {
            Stop();
            _pulses = Array.Empty<int>();
            _recorded.Clear();
            _version = 1;
            _videoStandard = 0;
            _sourceHz = 985248.0;
            Rewind();
        }

        public void Rewind()
        {
            _position = _remaining = _currentDuration = 0;
            _rounding = 0;
            _halfWaveHigh = false;
            _readHigh = true;
        }

        public byte[] ExportTap()
        {
            return TapImage.Encode(_recorded.Count > 0 ? _recorded : _pulses, _videoStandard,
                _recorded.Count > 0 ? (byte)1 : _version);
        }

        public void WriteProcessorPort(byte direction, byte data)
        {
            // Cassette motor is active low. Input pins are pulled up.
            byte pins = (byte)((data & direction) | ~direction);
            bool motor = (pins & 0x20) == 0;
            bool write = (pins & 0x08) != 0;
            if (_recording && motor && _motorOn && !_writeHigh && write)
            {
                if (_recordEdgeSeen)
                {
                    double scaled = _recordElapsed * _sourceHz / _cpuHz;
                    int duration = checked((int)Math.Round(scaled));
                    if (duration <= 0 || duration > 0xFFFFFF)
                        throw new InvalidDataException("Recorded cassette pulse is outside TAP v1 range.");
                    _recorded.Add(duration);
                }
                _recordElapsed = 0;
                _recordEdgeSeen = true;
            }
            _writeHigh = write;
            _motorOn = motor;
        }

        public void Tick()
        {
            if (!_motorOn) return;
            if (_recording)
            {
                _recordElapsed++;
                return;
            }
            if (!_playing) return;
            if (_remaining == 0)
            {
                if (_position >= _pulses.Length) { _readHigh = true; return; }
                double scaled = _pulses[_position] * _cpuHz / _sourceHz + _rounding;
                _currentDuration = Math.Max(1, (int)Math.Round(scaled));
                _rounding = scaled - _currentDuration;
                _remaining = _currentDuration;
            }
            _remaining--;
            if (_version == 2)
            {
                if (_remaining == 0) { _halfWaveHigh = !_halfWaveHigh; _readHigh = _halfWaveHigh; _position++; }
            }
            else
            {
                if (_remaining == _currentDuration / 2) _readHigh = true;
                if (_remaining == 0) { _readHigh = false; _position++; }
            }
        }
    }
}
