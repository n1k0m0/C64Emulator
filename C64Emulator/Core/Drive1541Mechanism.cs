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

namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the drive1541 mechanism component.
    /// </summary>
    public sealed class Drive1541Mechanism
    {
        private const int DefaultHalfTrack = 34;
        private const int SubCyclesPerCpuCycle = 16;
        private const int ByteReadyPulseCycles = 2;
        private const int DiskSwapCyclesDiskEjecting = 400000;
        private const int DiskSwapCyclesNoDisk = 200000;
        private const int DiskSwapCyclesDiskInserting = 400000;

        private D64Image _diskImage;
        private int _currentHalfTrack;
        private int _stepperPhase;
        private int _bitRateSelector;
        private bool _motorOn;
        private bool _ledOn;
        private bool _writeMode;
        private byte _currentReadByte;
        private bool _syncActive;
        private int _byteReadyPulseCycles;
        private int _soPulseCycles;
        private int _diskSwapCyclesRemaining;
        private bool _writeProtectLineHigh;
        private byte[] _trackBytes = Array.Empty<byte>();
        private int _trackBitIndex;
        private int _trackBitCount;
        private int _trackByteCycles;
        private int _cyclesForBit;
        private int _ue7Counter;
        private int _uf4Counter;
        private int _ue3Counter;
        private int _readShiftRegister;
        private byte _writeShiftRegister;
        private bool _currentDiskBit;

        /// <summary>
        /// Initializes a new Drive1541Mechanism instance.
        /// </summary>
        public Drive1541Mechanism()
        {
            Reset();
        }

        public bool MotorOn
        {
            get { return _motorOn; }
        }

        public bool LedOn
        {
            get { return _ledOn; }
        }

        public int CurrentHalfTrack
        {
            get { return _currentHalfTrack; }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            _currentHalfTrack = DefaultHalfTrack;
            _stepperPhase = 0;
            _bitRateSelector = 3;
            _motorOn = false;
            _ledOn = false;
            _writeMode = false;
            _currentReadByte = 0x00;
            _syncActive = false;
            _byteReadyPulseCycles = 0;
            _soPulseCycles = 0;
            _diskSwapCyclesRemaining = 0;
            _writeProtectLineHigh = _diskImage != null && !_diskImage.IsReadOnly;
            _trackBitIndex = 0;
            _trackBitCount = 0;
            _trackByteCycles = 32;
            _cyclesForBit = 0;
            _ue7Counter = GetClockSelPreload();
            _uf4Counter = 0;
            _ue3Counter = 0;
            _readShiftRegister = 0;
            _writeShiftRegister = 0;
            _currentDiskBit = false;
            ReloadTrackStream();
        }

        /// <summary>
        /// Mounts disk.
        /// </summary>
        public void MountDisk(D64Image diskImage)
        {
            _diskImage = diskImage;
            _diskSwapCyclesRemaining = DiskSwapCyclesDiskEjecting + DiskSwapCyclesNoDisk + DiskSwapCyclesDiskInserting;
            ReloadTrackStream();
        }

        /// <summary>
        /// Ejects disk.
        /// </summary>
        public void EjectDisk()
        {
            _diskImage = null;
            _diskSwapCyclesRemaining = DiskSwapCyclesDiskEjecting + DiskSwapCyclesNoDisk;
            ReloadTrackStream();
        }

        /// <summary>
        /// Attempts to read sector and reports whether it succeeded.
        /// </summary>
        public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
        {
            sectorBytes = null;
            return _diskImage != null && _diskImage.TryReadSector(track, sector, out sectorBytes);
        }

        /// <summary>
        /// Attempts to write sector and reports whether it succeeded.
        /// </summary>
        public bool TryWriteSector(int track, int sector, byte[] sectorBytes)
        {
            return _diskImage != null && _diskImage.TryWriteSector(track, sector, sectorBytes);
        }

        /// <summary>
        /// Applies via port b.
        /// </summary>
        public void ApplyViaPortB(byte outputValue, byte directionMask)
        {
            byte drivenValue = (byte)(outputValue & directionMask);
            int nextPhase = drivenValue & 0x03;

            if (_motorOn)
            {
                if (((_stepperPhase - 1) & 0x03) == nextPhase)
                {
                    MoveHead(-1);
                }
                else if (((_stepperPhase + 1) & 0x03) == nextPhase)
                {
                    MoveHead(+1);
                }
            }

            _stepperPhase = nextPhase;
            _motorOn = (drivenValue & 0x04) != 0;
            _ledOn = (drivenValue & 0x08) != 0;
            _bitRateSelector = (drivenValue >> 5) & 0x03;
        }

        /// <summary>
        /// Reads via port b.
        /// </summary>
        public byte ReadViaPortB(byte outputValue, byte directionMask)
        {
            byte inputValue = 0x00;

            if (_writeProtectLineHigh)
            {
                inputValue |= 0x10;
            }

            if (!_syncActive)
            {
                inputValue |= 0x80;
            }

            return inputValue;
        }

        /// <summary>
        /// Reads via port a.
        /// </summary>
        public byte ReadViaPortA(byte outputValue, byte directionMask)
        {
            return _currentReadByte;
        }

        /// <summary>
        /// Writes via port a.
        /// </summary>
        public void WriteViaPortA(byte outputValue, byte directionMask)
        {
            // Port A writes drive the write shift register through the VIA
            // output latch. They must not overwrite the currently latched read
            // byte from the encoder/decoder, otherwise custom drive code sees
            // its own writes mirrored back as if they had come from disk.
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick(DriveVia6522 diskVia)
        {
            if (diskVia == null)
            {
                return;
            }

            if (_byteReadyPulseCycles > 0)
            {
                _byteReadyPulseCycles--;
                diskVia.SetCa1Level(false);
            }
            else
            {
                diskVia.SetCa1Level(true);
            }

            TickWriteProtectSignal();

            if (!_motorOn || _trackBytes.Length == 0)
            {
                _syncActive = false;
                return;
            }

            _writeMode = IsWriteModeActive(diskVia);
            bool byteReadyEnabled = IsByteReadyEnabled(diskVia);

            // D64 images are already stored as logical sector data. D64Image
            // expands that data into a GCR byte stream, not into raw magnetic
            // flux-transition timings. Feed those GCR bytes to the 1541 side
            // directly and model the two signals the ROM/custom loaders care
            // about here: SYNC and byte-ready. The older path tried to run the
            // GCR bytes through a flux decoder a second time, which made the
            // emulated drive see garbage after fast loaders took over.
            _cyclesForBit++;
            int byteCycles = GetTrackByteCycles();
            if (_cyclesForBit < byteCycles)
            {
                return;
            }

            _cyclesForBit -= byteCycles;
            AdvanceGcrByte(diskVia, byteReadyEnabled);
        }

        /// <summary>
        /// Handles the consume so pulse operation.
        /// </summary>
        public bool ConsumeSoPulse()
        {
            if (_soPulseCycles <= 0)
            {
                return false;
            }

            _soPulseCycles--;
            return true;
        }

        /// <summary>
        /// Advances the disk bitstream state by one emulated tick.
        /// </summary>
        private void TickDiskBitstream()
        {
            if (_trackBitCount <= 0)
            {
                return;
            }

            _cyclesForBit++;
            if (_cyclesForBit < GetBitCellCycles())
            {
                return;
            }

            _cyclesForBit -= GetBitCellCycles();
            _currentDiskBit = GetNextBit();
            if (_currentDiskBit)
            {
                ResetEncoderDecoder();
            }
        }

        /// <summary>
        /// Advances the encoder decoder state by one emulated tick.
        /// </summary>
        private void TickEncoderDecoder(DriveVia6522 diskVia, bool byteReadyEnabled)
        {
            _ue7Counter++;
            if (_ue7Counter < 0x10)
            {
                return;
            }

            // Match the 1541/Pi1541 divider behavior: UE7 always reloads from
            // the density preload and clocks UF4 on carry.
            _ue7Counter = GetClockSelPreload();
            _uf4Counter = (_uf4Counter + 1) & 0x0F;

            // The read shift register is clocked by UF4 output B on counts
            // 2, 6, 10 and 14. The serial data is not the raw disk bit; it is
            // the output of the NOR gate built from UF4 C/D. After a flux
            // reversal reset, only the first cell (count 2) shifts in a 1.
            if ((_uf4Counter & 0x03) == 0x02)
            {
                _readShiftRegister = ((_readShiftRegister << 1) | ((_uf4Counter == 0x02) ? 1 : 0)) & 0x03FF;

                if (_writeMode)
                {
                    _writeShiftRegister <<= 1;
                }

                bool syncNow = !_writeMode && (_readShiftRegister & 0x03FF) == 0x03FF;
                _syncActive = syncNow;
                if (syncNow)
                {
                    _ue3Counter = 0;
                }
                else
                {
                    _ue3Counter++;
                }

                return;
            }

            // Byte ready occurs on the opposite UF4 phase once eight serial
            // clocks have accumulated since the last sync / byte boundary.
            if ((_uf4Counter & 0x02) != 0 || _ue3Counter != 8)
            {
                return;
            }

            _ue3Counter = 0;
            if (_writeMode)
            {
                _writeShiftRegister = diskVia.PortAOutput;
            }
            else
            {
                _currentReadByte = (byte)(_readShiftRegister & 0xFF);
            }

            if (byteReadyEnabled)
            {
                _byteReadyPulseCycles = ByteReadyPulseCycles;
                _soPulseCycles = ByteReadyPulseCycles;
            }
        }

        /// <summary>
        /// Handles the reset encoder decoder operation.
        /// </summary>
        private void ResetEncoderDecoder()
        {
            _ue7Counter = GetClockSelPreload();
            _uf4Counter = 0;
        }

        /// <summary>
        /// Gets the next bit value.
        /// </summary>
        private bool GetNextBit()
        {
            if (_trackBitCount <= 0)
            {
                return false;
            }

            int byteIndex = _trackBitIndex >> 3;
            int bitIndex = 7 - (_trackBitIndex & 0x07);
            bool bit = ((_trackBytes[byteIndex] >> bitIndex) & 0x01) != 0;

            _trackBitIndex++;
            if (_trackBitIndex >= _trackBitCount)
            {
                _trackBitIndex = 0;
            }

            return bit;
        }

        /// <summary>
        /// Handles the advance gcr byte operation.
        /// </summary>
        private void AdvanceGcrByte(DriveVia6522 diskVia, bool byteReadyEnabled)
        {
            if (_trackBytes.Length == 0)
            {
                _syncActive = false;
                return;
            }

            int byteIndex = _trackBitIndex >> 3;
            byte value = _trackBytes[byteIndex];
            _trackBitIndex += 8;
            if (_trackBitIndex >= _trackBitCount)
            {
                _trackBitIndex = 0;
            }

            if (!_writeMode && value == 0xFF)
            {
                _syncActive = true;
                return;
            }

            _syncActive = false;
            if (_writeMode)
            {
                _writeShiftRegister = diskVia.PortAOutput;
                return;
            }

            _currentReadByte = value;
            if (byteReadyEnabled)
            {
                _byteReadyPulseCycles = ByteReadyPulseCycles;
                _soPulseCycles = ByteReadyPulseCycles;
            }
        }

        /// <summary>
        /// Gets the track byte cycles value.
        /// </summary>
        private int GetTrackByteCycles()
        {
            return _trackByteCycles;
        }

        /// <summary>
        /// Gets the clock sel preload value.
        /// </summary>
        private int GetClockSelPreload()
        {
            return _bitRateSelector & 0x03;
        }

        /// <summary>
        /// Gets the bit cell cycles value.
        /// </summary>
        private int GetBitCellCycles()
        {
            // UE7 divides the 16 MHz reference by 13..16 depending on the
            // density setting, but a serial bit cell is four encoder/decoder
            // clocks wide. Pi1541 therefore advances the track bitstream every
            // 52/56/60/64 subcycles, not every 13/14/15/16.
            return 4 * (16 - (_bitRateSelector & 0x03));
        }

        /// <summary>
        /// Handles the move head operation.
        /// </summary>
        private void MoveHead(int delta)
        {
            int nextHalfTrack = _currentHalfTrack + delta;
            if (nextHalfTrack < 0)
            {
                nextHalfTrack = 0;
            }
            else if (nextHalfTrack > 83)
            {
                nextHalfTrack = 83;
            }

            if (nextHalfTrack == _currentHalfTrack)
            {
                return;
            }

            _currentHalfTrack = nextHalfTrack;
            ReloadTrackStream();
        }

        /// <summary>
        /// Handles the reload track stream operation.
        /// </summary>
        private void ReloadTrackStream()
        {
            _trackBytes = Array.Empty<byte>();
            _trackBitIndex = 0;
            _trackBitCount = 0;
            _trackByteCycles = 32;
            _currentReadByte = 0x00;
            _syncActive = false;
            _byteReadyPulseCycles = 0;
            _soPulseCycles = 0;
            _cyclesForBit = 0;
            _ue7Counter = GetClockSelPreload();
            _uf4Counter = 0;
            _ue3Counter = 0;
            _readShiftRegister = 0;
            _writeShiftRegister = 0;
            _currentDiskBit = false;

            if (_diskImage == null)
            {
                return;
            }

            byte[] trackBytes;
            if (_diskImage.TryGetTrackStream(_currentHalfTrack, out trackBytes) && trackBytes != null)
            {
                _trackBytes = trackBytes;
                _trackBitCount = trackBytes.Length * 8;
                // A 1541 rotates at about 300 RPM, i.e. one revolution per
                // 200 ms. At the C64/1541 ~1 MHz clock this gives roughly
                // 197k cycles per track revolution on PAL machines. Cache the
                // byte-ready cadence because this path runs once per drive
                // cycle while fastloaders spin the motor.
                _trackByteCycles = Math.Max(1, 197050 / trackBytes.Length);
            }
        }

        /// <summary>
        /// Returns whether byte ready enabled is true.
        /// </summary>
        private static bool IsByteReadyEnabled(DriveVia6522 diskVia)
        {
            return (diskVia.PeripheralControlRegister & 0x02) != 0;
        }

        /// <summary>
        /// Returns whether write mode active is true.
        /// </summary>
        private static bool IsWriteModeActive(DriveVia6522 diskVia)
        {
            byte pcr = diskVia.PeripheralControlRegister;
            bool cb2IsOutput = (pcr & 0x80) != 0;
            bool cb2OutputMode0Set = (pcr & 0x20) != 0;
            return cb2IsOutput && !cb2OutputMode0Set;
        }

        /// <summary>
        /// Advances the write protect signal state by one emulated tick.
        /// </summary>
        private void TickWriteProtectSignal()
        {
            bool writableDiskPresent = _diskImage != null && !_diskImage.IsReadOnly;
            if (_diskSwapCyclesRemaining > 0)
            {
                _diskSwapCyclesRemaining--;
                if (_diskSwapCyclesRemaining == 0)
                {
                    _writeProtectLineHigh = writableDiskPresent;
                }
                else if (_diskSwapCyclesRemaining > DiskSwapCyclesNoDisk + DiskSwapCyclesDiskInserting)
                {
                    _writeProtectLineHigh = false;
                }
                else if (_diskSwapCyclesRemaining > DiskSwapCyclesDiskInserting)
                {
                    _writeProtectLineHigh = true;
                }
                else
                {
                    _writeProtectLineHigh = false;
                }

                return;
            }

            _writeProtectLineHigh = writableDiskPresent;
        }
    }
}
