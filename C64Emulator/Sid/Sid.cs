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
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Emulates SID register state and audio sample generation.
    /// </summary>
    public enum SidChipModel
    {
        Mos6581,
        Mos8580
    }

    /// <summary>
    /// Emulates SID register state and audio sample generation.
    /// </summary>
    public sealed class Sid : IDisposable
    {
        private const double SidClockHz = 985248.0;
        private const int SampleRate = 44100;
        private const int VoiceCount = 3;
        private const int RegistersPerVoice = 7;
        private const double CyclesPerSample = SidClockHz / SampleRate;
        private const double SecondsPerSidCycle = 1.0 / SidClockHz;
        private const uint AccumulatorMask = 0x00FFFFFF;
        private static readonly double[] AttackTimes =
        {
            0.002, 0.008, 0.016, 0.024,
            0.038, 0.056, 0.068, 0.080,
            0.100, 0.250, 0.500, 0.800,
            1.000, 3.000, 5.000, 8.000
        };
        private static readonly double[] DecayReleaseTimes =
        {
            0.006, 0.024, 0.048, 0.072,
            0.114, 0.168, 0.204, 0.240,
            0.300, 0.750, 1.500, 2.400,
            3.000, 9.000, 15.000, 24.000
        };

        private readonly byte[] _registers = new byte[0x20];
        private readonly SidVoice[] _voices = new SidVoice[VoiceCount];
        private readonly bool[] _voiceMsbRising = new bool[VoiceCount];
        private readonly float[] _voiceOutputs = new float[VoiceCount];
        private readonly byte[] _audioBytes = new byte[2048];
        private readonly SidAudioOutput _audioOutput;

        private int _audioByteCount;
        private double _sampleCycleAccumulator;
        private byte _oscillator3Value;
        private byte _envelope3Value;
        private float _masterVolume = 1.0f;
        private float _noiseLevel = 0.55f;
        private SidChipModel _chipModel = SidChipModel.Mos6581;
        private float _filterLow;
        private float _filterBand;
        private float _volumeDacTarget;
        private float _volumeDacCurrent;
        private float _volumeDacDcBlock;

        /// <summary>
        /// Initializes a new Sid instance.
        /// </summary>
        public Sid()
        {
            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++)
            {
                _voices[voiceIndex] = new SidVoice((uint)(0x7FFFF8u ^ (uint)(voiceIndex * 0x1F1F1Fu)));
            }

            try
            {
                _audioOutput = new SidAudioOutput(SampleRate);
            }
            catch
            {
                _audioOutput = null;
            }

            Reset();
        }

        public float MasterVolume
        {
            get { return _masterVolume; }
            set { _masterVolume = Clamp(value, 0.0f, 1.5f); }
        }

        public float NoiseLevel
        {
            get { return _noiseLevel; }
            set { _noiseLevel = Clamp(value, 0.0f, 1.0f); }
        }

        public SidChipModel ChipModel
        {
            get { return _chipModel; }
            set
            {
                _chipModel = value;
                _filterLow = 0.0f;
                _filterBand = 0.0f;
                _volumeDacCurrent = 0.0f;
                _volumeDacDcBlock = 0.0f;
                _volumeDacTarget = ComputeVolumeDacTarget(_registers[0x18]);
            }
        }

        /// <summary>
        /// Gets compact debug information for the SID.
        /// </summary>
        public string GetDebugInfo()
        {
            int cutoff = ((_registers[0x16] << 3) | (_registers[0x15] & 0x07)) & 0x07FF;
            return string.Format(
                "model={0} vol={1:X1} fc={2:X3} res={3:X1} mode={4:X2} osc3={5:X2} env3={6:X2} v0={7} v1={8} v2={9}",
                _chipModel == SidChipModel.Mos6581 ? "6581" : "8580",
                _registers[0x18] & 0x0F,
                cutoff,
                (_registers[0x17] >> 4) & 0x0F,
                _registers[0x18],
                _oscillator3Value,
                _envelope3Value,
                _voices[0].GetDebugInfo(),
                _voices[1].GetDebugInfo(),
                _voices[2].GetDebugInfo());
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_registers, 0, _registers.Length);
            Array.Clear(_voiceMsbRising, 0, _voiceMsbRising.Length);
            Array.Clear(_voiceOutputs, 0, _voiceOutputs.Length);
            _audioByteCount = 0;
            _sampleCycleAccumulator = 0.0;
            _oscillator3Value = 0;
            _envelope3Value = 0;
            _filterLow = 0.0f;
            _filterBand = 0.0f;
            _volumeDacTarget = 0.0f;
            _volumeDacCurrent = 0.0f;
            _volumeDacDcBlock = 0.0f;

            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++)
            {
                _voices[voiceIndex].Reset((uint)(0x7FFFF8u ^ (uint)(voiceIndex * 0x1F1F1Fu)));
            }
        }

        /// <summary>
        /// Writes the complete SID state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            BinaryStateIO.WriteByteArray(writer, _registers);
            StateSerializer.WriteObjectFields(writer, this, "_registers", "_voices", "_audioOutput");
            writer.Write(_voices.Length);
            for (int voiceIndex = 0; voiceIndex < _voices.Length; voiceIndex++)
            {
                _voices[voiceIndex].SaveState(writer);
            }
        }

        /// <summary>
        /// Restores the complete SID state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            byte[] registers = BinaryStateIO.ReadByteArray(reader);
            if (registers != null)
            {
                Array.Copy(registers, _registers, Math.Min(registers.Length, _registers.Length));
            }

            StateSerializer.ReadObjectFields(reader, this, "_registers", "_voices", "_audioOutput");
            int voiceCount = reader.ReadInt32();
            for (int voiceIndex = 0; voiceIndex < voiceCount && voiceIndex < _voices.Length; voiceIndex++)
            {
                _voices[voiceIndex].LoadState(reader);
            }

            for (int voiceIndex = _voices.Length; voiceIndex < voiceCount; voiceIndex++)
            {
                SidVoice.SkipState(reader);
            }
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            AdvanceVoicesOneSidCycle();

            _sampleCycleAccumulator += 1.0;
            while (_sampleCycleAccumulator >= CyclesPerSample)
            {
                _sampleCycleAccumulator -= CyclesPerSample;
                AppendSample(RenderNextSample());
            }
        }

        /// <summary>
        /// Handles the read operation.
        /// </summary>
        public byte Read(ushort address)
        {
            address &= 0x1F;
            switch (address)
            {
                case 0x19:
                case 0x1A:
                    return 0xFF;
                case 0x1B:
                    return _oscillator3Value;
                case 0x1C:
                    return _envelope3Value;
                default:
                    return _registers[address];
            }
        }

        /// <summary>
        /// Handles the write operation.
        /// </summary>
        public void Write(ushort address, byte value)
        {
            address &= 0x1F;
            byte previousValue = _registers[address];
            _registers[address] = value;

            if (address < RegistersPerVoice * VoiceCount)
            {
                SyncVoiceFromRegisters(address / RegistersPerVoice);
            }

            if (address == 0x18)
            {
                UpdateVolumeDac(previousValue, value);
            }
        }

        /// <summary>
        /// Releases resources owned by the component.
        /// </summary>
        public void Dispose()
        {
            FlushAudio();
            if (_audioOutput != null)
            {
                _audioOutput.Dispose();
            }
        }

        /// <summary>
        /// Handles the advance voices one sid cycle operation.
        /// </summary>
        private void AdvanceVoicesOneSidCycle()
        {
            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++)
            {
                _voiceMsbRising[voiceIndex] = _voices[voiceIndex].Advance(SecondsPerSidCycle);
            }

            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++)
            {
                int sourceVoiceIndex = GetSyncSourceIndex(voiceIndex);
                SidVoice voice = _voices[voiceIndex];
                SidVoice sourceVoice = _voices[sourceVoiceIndex];
                if (voice.SyncEnabled && _voiceMsbRising[sourceVoiceIndex] && !sourceVoice.TestEnabled)
                {
                    voice.ApplySyncReset();
                }
            }

            _oscillator3Value = _voices[2].GetOscillatorByte();
            _envelope3Value = _voices[2].GetEnvelopeByte();
        }

        /// <summary>
        /// Handles the sync voice from registers operation.
        /// </summary>
        private void SyncVoiceFromRegisters(int voiceIndex)
        {
            int registerBase = voiceIndex * RegistersPerVoice;
            SidVoice voice = _voices[voiceIndex];
            voice.FrequencyRegister = (ushort)(_registers[registerBase] | (_registers[registerBase + 1] << 8));
            voice.PulseWidthRegister = (ushort)((_registers[registerBase + 2] | ((_registers[registerBase + 3] & 0x0F) << 8)) & 0x0FFF);
            voice.AttackDecay = _registers[registerBase + 5];
            voice.SustainRelease = _registers[registerBase + 6];
            voice.SetControl(_registers[registerBase + 4]);
        }

        /// <summary>
        /// Renders next sample.
        /// </summary>
        private short RenderNextSample()
        {
            byte filterRouteRegister = _registers[0x17];
            byte modeVolumeRegister = _registers[0x18];
            bool voice3Off = (modeVolumeRegister & 0x80) != 0;

            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++)
            {
                int sourceVoiceIndex = GetSyncSourceIndex(voiceIndex);
                _voiceOutputs[voiceIndex] = _voices[voiceIndex].Render(_voices[sourceVoiceIndex].MostSignificantBitSet, _noiseLevel);
            }

            float directMix = 0.0f;
            float filterInput = 0.0f;
            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++)
            {
                bool routedToFilter = (filterRouteRegister & (1 << voiceIndex)) != 0;
                bool muteVoice3 = voiceIndex == 2 && voice3Off && !routedToFilter;
                if (muteVoice3)
                {
                    continue;
                }

                if (routedToFilter)
                {
                    filterInput += _voiceOutputs[voiceIndex];
                }
                else
                {
                    directMix += _voiceOutputs[voiceIndex];
                }
            }

            float filteredMix = ApplyFilter(filterInput, modeVolumeRegister, filterRouteRegister);
            float volume = (modeVolumeRegister & 0x0F) / 15.0f;
            float normalOutput = ((directMix + filteredMix) / VoiceCount) * volume;
            float digiOutput = GetVolumeDacSample();
            float output = (normalOutput + digiOutput) * _masterVolume * 0.35f;
            output = Clamp(output, -1.0f, 1.0f);
            return (short)(output * short.MaxValue);
        }

        /// <summary>
        /// Applies filter.
        /// </summary>
        private float ApplyFilter(float input, byte modeVolumeRegister, byte filterRouteRegister)
        {
            int cutoffRegister = ((_registers[0x16] << 3) | (_registers[0x15] & 0x07)) & 0x07FF;
            float cutoffHz = MapCutoffToFrequency(cutoffRegister, _chipModel);
            float normalizedCutoff = 2.0f * (float)Math.Sin(Math.PI * Math.Min(cutoffHz, SampleRate * 0.45f) / SampleRate);
            normalizedCutoff = Clamp(normalizedCutoff, 0.0f, 0.99f);

            float resonance = ((_registers[0x17] >> 4) & 0x0F) / 15.0f;
            float damping = _chipModel == SidChipModel.Mos6581
                ? 0.22f + (1.70f * (1.0f - resonance))
                : 0.14f + (1.35f * (1.0f - resonance));

            if (_chipModel == SidChipModel.Mos6581)
            {
                input *= 0.92f;
            }

            _filterLow += normalizedCutoff * _filterBand;
            float high = input - _filterLow - (_filterBand * damping);
            _filterBand += normalizedCutoff * high;

            float filteredOutput = 0.0f;
            if ((modeVolumeRegister & 0x10) != 0)
            {
                filteredOutput += _filterLow;
            }

            if ((modeVolumeRegister & 0x20) != 0)
            {
                filteredOutput += _filterBand;
            }

            if ((modeVolumeRegister & 0x40) != 0)
            {
                filteredOutput += high;
            }

            if ((filterRouteRegister & 0x08) != 0)
            {
                filteredOutput += 0.0f;
            }

            return filteredOutput;
        }

        /// <summary>
        /// Gets the volume dac sample value.
        /// </summary>
        private float GetVolumeDacSample()
        {
            float response = _chipModel == SidChipModel.Mos6581 ? 0.35f : 0.22f;
            float dcBlockResponse = _chipModel == SidChipModel.Mos6581 ? 0.0024f : 0.0038f;
            _volumeDacCurrent += (_volumeDacTarget - _volumeDacCurrent) * response;
            float acSample = _volumeDacCurrent - _volumeDacDcBlock;
            _volumeDacDcBlock += acSample * dcBlockResponse;
            return acSample;
        }

        /// <summary>
        /// Updates volume dac.
        /// </summary>
        private void UpdateVolumeDac(byte previousValue, byte newValue)
        {
            if ((previousValue & 0x0F) == (newValue & 0x0F))
            {
                return;
            }

            _volumeDacTarget = ComputeVolumeDacTarget(newValue);
        }

        /// <summary>
        /// Computes volume dac target.
        /// </summary>
        private float ComputeVolumeDacTarget(byte modeVolumeRegister)
        {
            float centered = ((modeVolumeRegister & 0x0F) - 7.5f) / 15.0f;
            return centered * (_chipModel == SidChipModel.Mos6581 ? 0.85f : 0.42f);
        }

        /// <summary>
        /// Maps cutoff to frequency.
        /// </summary>
        private static float MapCutoffToFrequency(int cutoffRegister, SidChipModel chipModel)
        {
            float normalized = cutoffRegister / 2047.0f;
            if (chipModel == SidChipModel.Mos6581)
            {
                normalized = (float)Math.Pow(normalized, 1.55);
            }
            else
            {
                normalized = (float)Math.Pow(normalized, 1.20);
            }

            double minimumFrequency = chipModel == SidChipModel.Mos6581 ? 220.0 : 30.0;
            double maximumFrequency = chipModel == SidChipModel.Mos6581 ? 12000.0 : 18000.0;
            double mapped = minimumFrequency * Math.Pow(maximumFrequency / minimumFrequency, normalized);
            return (float)mapped;
        }

        /// <summary>
        /// Handles the append sample operation.
        /// </summary>
        private void AppendSample(short sample)
        {
            _audioBytes[_audioByteCount++] = (byte)(sample & 0xFF);
            _audioBytes[_audioByteCount++] = (byte)((sample >> 8) & 0xFF);

            if (_audioByteCount >= _audioBytes.Length - 2)
            {
                FlushAudio();
            }
        }

        /// <summary>
        /// Handles the flush audio operation.
        /// </summary>
        private void FlushAudio()
        {
            if (_audioByteCount == 0)
            {
                return;
            }

            if (_audioOutput != null)
            {
                _audioOutput.Write(_audioBytes, _audioByteCount);
            }

            _audioByteCount = 0;
        }

        /// <summary>
        /// Gets the sync source index value.
        /// </summary>
        private static int GetSyncSourceIndex(int voiceIndex)
        {
            return (voiceIndex + VoiceCount - 1) % VoiceCount;
        }

        /// <summary>
        /// Handles the clamp operation.
        /// </summary>
        private static float Clamp(float value, float minimum, float maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }

        /// <summary>
        /// Emulates SID register state and audio sample generation.
        /// </summary>
        private sealed class SidVoice
        {
            private uint _noiseLfsr;
            private uint _accumulator;
            private float _envelope;
            private EnvelopeStage _envelopeStage;
            private bool _gate;

            /// <summary>
            /// Initializes a new SidVoice instance.
            /// </summary>
            public SidVoice(uint seed)
            {
                Reset(seed);
            }

            /// <summary>
            /// Gets or sets the SID voice frequency register.
            /// </summary>
            public ushort FrequencyRegister { get; set; }

            /// <summary>
            /// Gets or sets the SID voice pulse-width register.
            /// </summary>
            public ushort PulseWidthRegister { get; set; }

            /// <summary>
            /// Gets the SID voice control register.
            /// </summary>
            public byte Control { get; private set; }

            /// <summary>
            /// Gets or sets the SID attack/decay envelope register.
            /// </summary>
            public byte AttackDecay { get; set; }

            /// <summary>
            /// Gets or sets the SID sustain/release envelope register.
            /// </summary>
            public byte SustainRelease { get; set; }

            public bool SyncEnabled
            {
                get { return (Control & 0x02) != 0; }
            }

            public bool RingModEnabled
            {
                get { return (Control & 0x04) != 0; }
            }

            public bool TestEnabled
            {
                get { return (Control & 0x08) != 0; }
            }

            public bool MostSignificantBitSet
            {
                get { return (_accumulator & 0x00800000) != 0; }
            }

            /// <summary>
            /// Resets the component to its power-on or idle state.
            /// </summary>
            public void Reset(uint seed)
            {
                _noiseLfsr = seed == 0 ? 0x7FFFF8u : seed;
                _accumulator = 0u;
                _envelope = 0.0f;
                _envelopeStage = EnvelopeStage.Release;
                _gate = false;
                FrequencyRegister = 0;
                PulseWidthRegister = 0x0800;
                Control = 0;
                AttackDecay = 0;
                SustainRelease = 0;
            }

            /// <summary>
            /// Sets the control value.
            /// </summary>
            public void SetControl(byte control)
            {
                bool newGate = (control & 0x01) != 0;
                if (!_gate && newGate)
                {
                    _envelopeStage = EnvelopeStage.Attack;
                }
                else if (_gate && !newGate)
                {
                    _envelopeStage = EnvelopeStage.Release;
                }

                _gate = newGate;
                Control = control;

                if ((control & 0x08) != 0)
                {
                    _accumulator = 0u;
                    _noiseLfsr = 0x7FFFF8u;
                }
            }

            /// <summary>
            /// Handles the advance operation.
            /// </summary>
            public bool Advance(double deltaTime)
            {
                bool msbRising = false;
                if (!TestEnabled)
                {
                    uint oldAccumulator = _accumulator;
                    _accumulator = (_accumulator + FrequencyRegister) & AccumulatorMask;

                    bool oldMsb = (oldAccumulator & 0x00800000) != 0;
                    bool newMsb = (_accumulator & 0x00800000) != 0;
                    msbRising = !oldMsb && newMsb;

                    bool oldNoiseClock = (oldAccumulator & 0x00080000) != 0;
                    bool newNoiseClock = (_accumulator & 0x00080000) != 0;
                    if (!oldNoiseClock && newNoiseClock)
                    {
                        ClockNoise();
                    }
                }
                else
                {
                    _accumulator = 0u;
                }

                UpdateEnvelope(deltaTime);
                return msbRising;
            }

            /// <summary>
            /// Applies sync reset.
            /// </summary>
            public void ApplySyncReset()
            {
                if (!TestEnabled)
                {
                    _accumulator = 0u;
                }
            }

            /// <summary>
            /// Handles the render operation.
            /// </summary>
            public float Render(bool sourceMsb, float noiseLevel)
            {
                if (TestEnabled)
                {
                    return 0.0f;
                }

                int activeWaveforms = 0;
                float waveformSum = 0.0f;

                if ((Control & 0x10) != 0)
                {
                    waveformSum += GetTriangleSample(sourceMsb && RingModEnabled);
                    activeWaveforms++;
                }

                if ((Control & 0x20) != 0)
                {
                    waveformSum += GetSawSample();
                    activeWaveforms++;
                }

                if ((Control & 0x40) != 0)
                {
                    waveformSum += GetPulseSample();
                    activeWaveforms++;
                }

                if ((Control & 0x80) != 0)
                {
                    waveformSum += GetNoiseSample() * noiseLevel;
                    activeWaveforms++;
                }

                if (activeWaveforms == 0)
                {
                    return 0.0f;
                }

                float waveform = waveformSum / activeWaveforms;
                if (activeWaveforms > 1)
                {
                    float drive = 1.2f + (activeWaveforms - 1) * 0.3f;
                    waveform = (float)Math.Tanh(waveform * drive);
                    if ((Control & 0x80) != 0)
                    {
                        waveform *= 0.85f;
                    }
                }

                return waveform * _envelope;
            }

            /// <summary>
            /// Gets the oscillator byte value.
            /// </summary>
            public byte GetOscillatorByte()
            {
                return (byte)((_accumulator >> 16) & 0xFF);
            }

            /// <summary>
            /// Gets the envelope byte value.
            /// </summary>
            public byte GetEnvelopeByte()
            {
                return (byte)Clamp(_envelope * 255.0f, 0.0f, 255.0f);
            }

            /// <summary>
            /// Gets compact debug information for this voice.
            /// </summary>
            public string GetDebugInfo()
            {
                return string.Format(
                    "f={0:X4} pw={1:X3} c={2:X2} e={3:X2} s={4}",
                    FrequencyRegister,
                    PulseWidthRegister,
                    Control,
                    GetEnvelopeByte(),
                    _envelopeStage);
            }

            /// <summary>
            /// Writes the complete SID voice state into a savestate stream.
            /// </summary>
            public void SaveState(BinaryWriter writer)
            {
                StateSerializer.WriteObjectFields(writer, this);
            }

            /// <summary>
            /// Restores the complete SID voice state from a savestate stream.
            /// </summary>
            public void LoadState(BinaryReader reader)
            {
                StateSerializer.ReadObjectFields(reader, this);
            }

            /// <summary>
            /// Skips one serialized voice state.
            /// </summary>
            public static void SkipState(BinaryReader reader)
            {
                var temporary = new SidVoice(0x7FFFF8u);
                temporary.LoadState(reader);
            }

            /// <summary>
            /// Gets the triangle sample value.
            /// </summary>
            private float GetTriangleSample(bool invertWithRingMod)
            {
                uint phase = _accumulator;
                if (invertWithRingMod)
                {
                    phase ^= 0x00800000;
                }

                uint folded = ((phase & 0x00800000) != 0) ? ((~phase) & 0x007FFFFF) : (phase & 0x007FFFFF);
                return (folded / 4194303.5f) - 1.0f;
            }

            /// <summary>
            /// Gets the saw sample value.
            /// </summary>
            private float GetSawSample()
            {
                return (_accumulator / 8388607.5f) - 1.0f;
            }

            /// <summary>
            /// Gets the pulse sample value.
            /// </summary>
            private float GetPulseSample()
            {
                uint pulseWidth = (uint)Math.Max(16, Math.Min(4080, (int)PulseWidthRegister));
                return ((_accumulator >> 12) & 0x0FFF) < pulseWidth ? 1.0f : -1.0f;
            }

            /// <summary>
            /// Updates envelope.
            /// </summary>
            private void UpdateEnvelope(double deltaTime)
            {
                switch (_envelopeStage)
                {
                    case EnvelopeStage.Attack:
                        _envelope += (float)(deltaTime / AttackTimes[(AttackDecay >> 4) & 0x0F]);
                        if (_envelope >= 1.0f)
                        {
                            _envelope = 1.0f;
                            _envelopeStage = EnvelopeStage.Decay;
                        }
                        break;
                    case EnvelopeStage.Decay:
                        float sustainLevel = (SustainRelease >> 4) / 15.0f;
                        if (_envelope > sustainLevel)
                        {
                            _envelope -= (float)(deltaTime / DecayReleaseTimes[AttackDecay & 0x0F]);
                            if (_envelope <= sustainLevel)
                            {
                                _envelope = sustainLevel;
                                _envelopeStage = EnvelopeStage.Sustain;
                            }
                        }
                        else
                        {
                            _envelopeStage = EnvelopeStage.Sustain;
                        }
                        break;
                    case EnvelopeStage.Release:
                        if (_envelope > 0.0f)
                        {
                            _envelope -= (float)(deltaTime / DecayReleaseTimes[SustainRelease & 0x0F]);
                            if (_envelope < 0.0f)
                            {
                                _envelope = 0.0f;
                            }
                        }
                        break;
                }
            }

            /// <summary>
            /// Clocks noise.
            /// </summary>
            private void ClockNoise()
            {
                uint feedback = ((_noiseLfsr >> 22) ^ (_noiseLfsr >> 17)) & 0x01;
                _noiseLfsr = ((_noiseLfsr << 1) | feedback) & 0x007FFFFF;
                if (_noiseLfsr == 0)
                {
                    _noiseLfsr = 0x7FFFF8u;
                }
            }

            /// <summary>
            /// Gets the noise sample value.
            /// </summary>
            private float GetNoiseSample()
            {
                int sample =
                    (int)(((_noiseLfsr >> 22) & 0x01) << 7) |
                    (int)(((_noiseLfsr >> 20) & 0x01) << 6) |
                    (int)(((_noiseLfsr >> 16) & 0x01) << 5) |
                    (int)(((_noiseLfsr >> 13) & 0x01) << 4) |
                    (int)(((_noiseLfsr >> 11) & 0x01) << 3) |
                    (int)(((_noiseLfsr >> 7) & 0x01) << 2) |
                    (int)(((_noiseLfsr >> 4) & 0x01) << 1) |
                    (int)((_noiseLfsr >> 2) & 0x01);

                return (sample / 127.5f) - 1.0f;
            }

            /// <summary>
            /// Lists the supported envelope stage values.
            /// </summary>
            private enum EnvelopeStage
            {
                Attack,
                Decay,
                Sustain,
                Release
            }
        }
    }
}
