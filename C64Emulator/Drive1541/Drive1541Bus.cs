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
    /// Maps 1541 CPU memory, ROM, RAM, VIA registers, and disk job state.
    /// </summary>
    public sealed class Drive1541Bus : ICpuBus
    {
        private readonly byte[] _ram = new byte[0x0800];
        private readonly byte[] _rom = new byte[0x4000];
        private readonly DriveVia6522 _serialVia = new DriveVia6522();
        private readonly DriveVia6522 _diskVia = new DriveVia6522();
        private readonly Drive1541Mechanism _mechanism = new Drive1541Mechanism();
        private const int DriveRamMask = 0x07FF;
        private const int NoDosJobSlot = -1;
        private const int DosJobSlotCount = 6;
        private const int DosJobTrackSectorBase = 0x0006;
        private const int DosJobBufferBase = 0x0300;
        private const int DosJobBufferSize = 0x0100;
        private const int DosDiskIdBase = 0x0012;
        private const int DosCurrentTrackAddress = 0x0018;
        private const int DosCurrentSectorAddress = 0x0019;
        private const int DosHeaderBlockIdAddress = 0x0039;
        private const int DosBlockIndexLowBase = 0x005A;
        private const int DosBlockIndexHighBase = 0x00AD;
        private const int DosBlockIndexEntryCount = DosBlockIndexHighBase - DosBlockIndexLowBase;
        private const int DotcFinalLoaderEntryAddress = 0x07A0;
        private const int RawLoaderSectorTableBase = 0x045A;
        private const int RawLoaderTrackTableBase = 0x04AD;
        private const int RawLoaderOffsetTableBase = 0x0407;
        private const int DosTrackSectorPointerHighAddress = 0x0033;
        private const int DosBamWorkspaceStart = 0x029D;
        private const int DosBamWorkspaceEnd = 0x02B0;
        private const int DosCurrentSectorSearchLeadBytes = 32;
        private const byte DosHeaderBlockId = 0x08;
        private const byte DiskViaPortBOutputMask = 0x6F;
        private const byte DiskViaPortBMotorAndDensity = 0x64;
        private static readonly byte[] DotcFinalLoaderReferenceOffsets =
        {
            0x00, 0xF2, 0xE3, 0xDB, 0xD2, 0xCF, 0xD6, 0xDE, 0xDF, 0xF4, 0xD3, 0xB2,
            0xA5, 0x75, 0x4C, 0xF9, 0x1E, 0x03, 0x12, 0x06, 0xEB, 0xC8, 0xB6, 0x06,
            0x22, 0x68, 0x7E, 0x94, 0x88, 0x3F, 0x12, 0xDA, 0xE9, 0xEF, 0x01, 0x5D,
            0xAB, 0xC9, 0xA0, 0x04, 0x7B, 0x4F, 0x0D, 0x3D, 0xD4, 0xD9, 0x06, 0x9A,
            0x60, 0x81, 0x2A, 0xAD, 0x12
        };

        private static readonly byte[] DotcFinalLoaderReferenceSectors =
        {
            0x0A, 0x0A, 0x14, 0x08, 0x12, 0x06, 0x10, 0x04, 0x0E, 0x02, 0x0C, 0x01,
            0x0B, 0x03, 0x0D, 0x0D, 0x0B, 0x00, 0x0A, 0x14, 0x14, 0x08, 0x06, 0x0E,
            0x04, 0x0E, 0x0C, 0x09, 0x05, 0x03, 0x0B, 0x11, 0x0A, 0x01, 0x14, 0x0E,
            0x09, 0x0C, 0x0C, 0x07, 0x05, 0x14, 0x14, 0x07, 0x08, 0x08, 0x09, 0x12,
            0x0E, 0x03, 0x07, 0x03, 0x06
        };

        private static readonly byte[] DotcFinalLoaderReferenceTracks =
        {
            0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11,
            0x11, 0x11, 0x11, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10,
            0x0F, 0x0F, 0x0F, 0x0F, 0x0E, 0x0E, 0x0E, 0x0D, 0x0D, 0x0C, 0x0B, 0x0A,
            0x09, 0x08, 0x07, 0x05, 0x04, 0x02, 0x01, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x19, 0x1A, 0x1B, 0x1C, 0x1C
        };

        private readonly IecBusPort _iecPort;
        private readonly byte _deviceSelectorBits;
        private bool _piAtn;
        private bool _piData;
        private bool _piClock;
        private bool _viaAtna;
        private bool _viaData;
        private bool _viaClock;
        private bool _dataSetToOut;
        private bool _clockSetToOut;
        private bool _atnaDataSetToOut;
        private bool _lowerRomLoaded;
        private bool _upperRomLoaded;
        private bool _serialOutputsEnabled;
        private bool _deferSerialOutputsUntilPortWrite;
        private bool _customSerialOrbZeroReleasesAtna;
        private bool _alignDosCurrentSectorWritesToGcr;
        private bool _dotcInterBlockClockRelease;
#pragma warning disable CS0414
        // Legacy savestates from the GEOS loader repair pass contain this field.
        // It is no longer used by the drive bus, but keeping it preserves loadability.
        private bool _geosSecondStageVectorReadRepairArmed;
        private bool _suppressGeosRomSerialPortWrites;
        // Kept for the same legacy GEOS savestate shape as the repair flag above.
        private string _lastGeosVectorRepairDebug = "-";
#pragma warning restore CS0414
        private string _lastJobQueueDebug = "-";
        private int _pendingExecuteBufferJobSlot = NoDosJobSlot;
        private byte _pendingExecuteBufferJobCode;
        private int _activeExecuteBufferJobSlot = NoDosJobSlot;
        private byte _activeExecuteBufferJobCode;

        /// <summary>
        /// Initializes a new Drive1541Bus instance.
        /// </summary>
        public Drive1541Bus(IecBusPort iecPort, int deviceNumber)
        {
            _iecPort = iecPort;
            _deviceSelectorBits = (byte)Math.Max(0, Math.Min(3, deviceNumber - 8));
            _serialVia.PortBReadProvider = ReadSerialPortB;
            _serialVia.PortBWritten = WriteSerialPortB;
            _diskVia.PortAReadProvider = ReadDiskPortA;
            _diskVia.PortAWritten = WriteDiskPortA;
            _diskVia.PortBReadProvider = ReadDiskPortB;
            _diskVia.PortBWritten = WriteDiskPortB;
            _iecPort.RegisterLineChangeListener(HandleIecLineChanged);
        }

        public bool CpuCanAccess
        {
            get { return true; }
        }

        /// <summary>
        /// Returns whether irq asserted is true.
        /// </summary>
        public bool IsIrqAsserted()
        {
            return _diskVia.IsIrqAsserted || _serialVia.IsIrqAsserted;
        }

        /// <summary>
        /// Returns whether nmi asserted is true.
        /// </summary>
        public bool IsNmiAsserted()
        {
            return false;
        }

        /// <summary>
        /// Handles the cpu read operation.
        /// </summary>
        public byte CpuRead(ushort address)
        {
            if (address < 0x1800)
            {
                return _ram[address & 0x07FF];
            }

            if (address >= 0x1800 && address <= 0x1BFF)
            {
                return _serialVia.Read((ushort)(address & 0x000F));
            }

            if (address >= 0x1C00 && address <= 0x1FFF)
            {
                return _diskVia.Read((ushort)(address & 0x000F));
            }

            if (address >= 0x8000)
            {
                return _rom[address & 0x3FFF];
            }

            return (byte)(address >> 8);
        }

        /// <summary>
        /// Handles the cpu write operation.
        /// </summary>
        public void CpuWrite(ushort address, byte value)
        {
            if (address < 0x1800)
            {
                int ramAddress = address & DriveRamMask;
                _ram[ramAddress] = value;
                if (_alignDosCurrentSectorWritesToGcr &&
                    (ramAddress == DosCurrentTrackAddress || ramAddress == DosCurrentSectorAddress))
                {
                    AlignGcrStreamToDosCurrentSector();
                }

                return;
            }

            if (address >= 0x1800 && address <= 0x1BFF)
            {
                _serialVia.Write((ushort)(address & 0x000F), value);
                return;
            }

            if (address >= 0x1C00 && address <= 0x1FFF)
            {
                _diskVia.Write((ushort)(address & 0x000F), value);
            }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_ram, 0, _ram.Length);
            _diskVia.Reset();
            _serialVia.Reset();
            _mechanism.Reset();
            _piAtn = false;
            _piData = false;
            _piClock = false;
            _viaAtna = false;
            _viaData = false;
            _viaClock = false;
            _dataSetToOut = false;
            _clockSetToOut = false;
            _atnaDataSetToOut = false;
            _serialOutputsEnabled = false;
            _deferSerialOutputsUntilPortWrite = false;
            _customSerialOrbZeroReleasesAtna = false;
            _alignDosCurrentSectorWritesToGcr = false;
            _dotcInterBlockClockRelease = false;
            _geosSecondStageVectorReadRepairArmed = false;
            _suppressGeosRomSerialPortWrites = false;
            _lastGeosVectorRepairDebug = "-";
            _lastJobQueueDebug = "-";
            _pendingExecuteBufferJobSlot = NoDosJobSlot;
            _pendingExecuteBufferJobCode = 0;
            _activeExecuteBufferJobSlot = NoDosJobSlot;
            _activeExecuteBufferJobCode = 0;
            UpdateSerialInputs();
        }

        /// <summary>
        /// Mounts disk.
        /// </summary>
        public void MountDisk(D64Image image)
        {
            _mechanism.MountDisk(image);
            if (image != null &&
                _ram[DosCurrentTrackAddress] == 0x00 &&
                _ram[DosCurrentSectorAddress] == 0x00)
            {
                // The high-level IEC transport can mount a disk without
                // running the full 1541 DOS init path. Seed the ROM context
                // that raw-disk loaders inherit after the first M-E handoff.
                PrimeDosDiskContext(18, 0, true, false);
            }
        }

        /// <summary>
        /// Ejects disk.
        /// </summary>
        public void EjectDisk()
        {
            _mechanism.EjectDisk();
            _alignDosCurrentSectorWritesToGcr = false;
        }

        /// <summary>
        /// Writes the complete 1541 bus state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            StateSerializer.WriteObjectFields(writer, this, "_iecPort", "_serialVia", "_diskVia", "_mechanism");
            _serialVia.SaveState(writer);
            _diskVia.SaveState(writer);
            _mechanism.SaveState(writer);
        }

        /// <summary>
        /// Restores the complete 1541 bus state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader, D64Image mountedImage)
        {
            StateSerializer.ReadObjectFields(reader, this, "_iecPort", "_serialVia", "_diskVia", "_mechanism");
            _serialVia.LoadState(reader);
            _diskVia.LoadState(reader);
            _mechanism.LoadState(reader, mountedImage);
            RefreshSerialLineLevels(true);
            ApplySerialOutputs();
        }

        /// <summary>
        /// Writes ram.
        /// </summary>
        public void WriteRam(ushort address, byte value)
        {
            if (address < 0x1800)
            {
                _ram[address & 0x07FF] = value;
            }
        }

        /// <summary>
        /// Reads ram.
        /// </summary>
        public byte ReadRam(ushort address)
        {
            return address < 0x1800 ? _ram[address & 0x07FF] : (byte)0x00;
        }

        /// <summary>
        /// Primes DOS zero-page state after the software IEC path read a disk sector.
        /// </summary>
        public void PrimeDosDiskContextForSector(int track, int sector)
        {
            if (track <= 0 || sector < 0)
            {
                return;
            }

            PrimeDosDiskContextAfterShortcutSectorJob(track, sector);
        }

        /// <summary>
        /// Primes DOS zero-page state after the software IEC path opened a PRG file.
        /// </summary>
        public void PrimeDosFileOpenContext(int track, int sector, int blocks)
        {
            if (track <= 0 || sector < 0)
            {
                return;
            }

            PrimeDosDiskContext(track, sector, true, false);
            PrimeDosBlockIndexWorkspace(blocks);
            _alignDosCurrentSectorWritesToGcr = true;
        }

        /// <summary>
        /// Returns whether an uploaded DOS buffer execution job is waiting for the drive CPU.
        /// </summary>
        public bool HasPendingExecuteBufferJob
        {
            get { return _pendingExecuteBufferJobSlot != NoDosJobSlot; }
        }

        /// <summary>
        /// Consumes a pending DOS buffer execution job.
        /// </summary>
        public bool TryConsumePendingExecuteBufferJob(out int slot, out byte job)
        {
            slot = _pendingExecuteBufferJobSlot;
            job = _pendingExecuteBufferJobCode;
            if (slot == NoDosJobSlot)
            {
                return false;
            }

            PrimeExecuteBufferJobZeroPage();
            _activeExecuteBufferJobSlot = slot;
            _activeExecuteBufferJobCode = job;
            _pendingExecuteBufferJobSlot = NoDosJobSlot;
            _pendingExecuteBufferJobCode = 0;
            _lastJobQueueDebug = string.Format("#{0}:{1:X2}:EXEC-RUN", slot, job);
            return true;
        }

        /// <summary>
        /// Completes a queued DOS buffer execution job without running ROM code.
        /// </summary>
        public void CompletePendingExecuteBufferJob(bool ok)
        {
            int slot = _pendingExecuteBufferJobSlot != NoDosJobSlot
                ? _pendingExecuteBufferJobSlot
                : _activeExecuteBufferJobSlot;
            if (slot == NoDosJobSlot)
            {
                return;
            }

            _ram[slot & DriveRamMask] = ok ? (byte)0x01 : (byte)0x02;
            _lastJobQueueDebug = string.Format("#{0}:EXEC:{1}", slot, ok ? "OK" : "ERR");
            _pendingExecuteBufferJobSlot = NoDosJobSlot;
            _pendingExecuteBufferJobCode = 0;
            _activeExecuteBufferJobSlot = NoDosJobSlot;
            _activeExecuteBufferJobCode = 0;
        }

        /// <summary>
        /// Loads rom.
        /// </summary>
        public void LoadRom(byte[] romBytes)
        {
            Array.Clear(_rom, 0, _rom.Length);
            if (romBytes != null)
            {
                Array.Copy(romBytes, 0, _rom, 0, Math.Min(_rom.Length, romBytes.Length));
                _lowerRomLoaded = romBytes.Length >= 0x2000;
                _upperRomLoaded = romBytes.Length >= 0x4000;
            }
            else
            {
                _lowerRomLoaded = false;
                _upperRomLoaded = false;
            }
        }

        /// <summary>
        /// Loads rom halves.
        /// </summary>
        public void LoadRomHalves(byte[] lowerRomBytes, byte[] upperRomBytes)
        {
            Array.Clear(_rom, 0, _rom.Length);

            _lowerRomLoaded = lowerRomBytes != null && lowerRomBytes.Length >= 0x2000;
            _upperRomLoaded = upperRomBytes != null && upperRomBytes.Length >= 0x2000;

            if (_lowerRomLoaded)
            {
                Array.Copy(lowerRomBytes, 0, _rom, 0x0000, 0x2000);
            }

            if (_upperRomLoaded)
            {
                Array.Copy(upperRomBytes, 0, _rom, 0x2000, 0x2000);
            }
        }

        /// <summary>
        /// Attempts to load rom from file and reports whether it succeeded.
        /// </summary>
        public bool TryLoadRomFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            LoadRom(File.ReadAllBytes(path));
            return true;
        }

        /// <summary>
        /// Attempts to load rom halves and reports whether it succeeded.
        /// </summary>
        public bool TryLoadRomHalves(string lowerPath, string upperPath)
        {
            byte[] lowerRom = null;
            byte[] upperRom = null;

            if (!string.IsNullOrWhiteSpace(lowerPath) && File.Exists(lowerPath))
            {
                lowerRom = File.ReadAllBytes(lowerPath);
            }

            if (!string.IsNullOrWhiteSpace(upperPath) && File.Exists(upperPath))
            {
                upperRom = File.ReadAllBytes(upperPath);
            }

            LoadRomHalves(lowerRom, upperRom);
            return _lowerRomLoaded || _upperRomLoaded;
        }

        /// <summary>
        /// Reads reset vector.
        /// </summary>
        public ushort ReadResetVector()
        {
            if (!_upperRomLoaded)
            {
                return 0x0000;
            }

            int offset = 0x3FFC;
            return (ushort)(_rom[offset] | (_rom[offset + 1] << 8));
        }

        public bool HasLowerRomLoaded
        {
            get { return _lowerRomLoaded; }
        }

        public bool HasUpperRomLoaded
        {
            get { return _upperRomLoaded; }
        }

        public bool HasSerialRomInitialization
        {
            get
            {
                return _serialVia.PortBDirection != 0x00 ||
                    _serialVia.PortADirection != 0x00 ||
                    _serialVia.PeripheralControlRegister != 0x00 ||
                    _serialVia.AuxiliaryControlRegister != 0x00;
            }
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            UpdateSerialInputs();
            _mechanism.Tick(_diskVia);
            ServiceDosJobQueue();
            _diskVia.Tick();
            _serialVia.Tick();
            UpdateSerialInputs();
        }

        /// <summary>
        /// Handles the consume so pulse operation.
        /// </summary>
        public bool ConsumeSoPulse()
        {
            return _mechanism.ConsumeSoPulse();
        }

        public bool IsDiskLedOn
        {
            get { return _mechanism.LedOn; }
        }

        public bool IsDiskMotorOn
        {
            get { return _mechanism.MotorOn; }
        }

        /// <summary>
        /// Gets the serial debug info value.
        /// </summary>
        public string GetSerialDebugInfo()
        {
            return string.Format(
                "orb={0:X2} ddrb={1:X2} pcr={2:X2} acr={3:X2} ifr={4:X2} ier={5:X2} viaAtna={6} viaData={7} viaClock={8} piAtn={9} piData={10} piClock={11} dataOut={12} clockOut={13} atnaDataOut={14} serialOutEn={15}",
                _serialVia.PortBOutput,
                _serialVia.PortBDirection,
                _serialVia.PeripheralControlRegister,
                _serialVia.AuxiliaryControlRegister,
                _serialVia.InterruptFlags,
                _serialVia.InterruptEnable,
                _viaAtna,
                _viaData,
                _viaClock,
                _piAtn,
                _piData,
                _piClock,
                _dataSetToOut,
                _clockSetToOut,
                _atnaDataSetToOut,
                _serialOutputsEnabled);
        }

        /// <summary>
        /// Gets the disk debug info value.
        /// </summary>
        public string GetDiskDebugInfo()
        {
            return string.Format(
                "diskA={0:X2}/{1:X2} diskB={2:X2}/{3:X2} motor={4} led={5} htrk={6}",
                _diskVia.PortAOutput,
                _diskVia.PortADirection,
                _diskVia.PortBOutput,
                _diskVia.PortBDirection,
                _mechanism.MotorOn,
                _mechanism.LedOn,
                _mechanism.CurrentHalfTrack) + " job=" + _lastJobQueueDebug;
        }

        public int CurrentHalfTrack
        {
            get { return _mechanism.CurrentHalfTrack; }
        }

        public bool IsC64DataLineLow
        {
            get { return _iecPort.IsOwnerDrivingLineLow("C64", IecBusLine.Data); }
        }

        public bool IsC64ClockLineLow
        {
            get { return _iecPort.IsOwnerDrivingLineLow("C64", IecBusLine.Clock); }
        }

        /// <summary>
        /// Gets or sets whether DOTC's final loader wait loop must keep CLOCK released.
        /// </summary>
        public bool DotcInterBlockClockRelease
        {
            get { return _dotcInterBlockClockRelease; }
            set
            {
                if (_dotcInterBlockClockRelease == value)
                {
                    return;
                }

                _dotcInterBlockClockRelease = value;
                ApplySerialOutputs();
                UpdateObservedClockLine();
            }
        }

        public bool HasManiacMansionLoaderStub
        {
            get
            {
                return _ram[0x0500] == 0x20 &&
                    _ram[0x0501] == 0x5D &&
                    _ram[0x0502] == 0x06 &&
                    _ram[0x0626] == 0xA9 &&
                    _ram[0x0627] == 0x08 &&
                    _ram[0x06AE] == 0xA2 &&
                    _ram[0x06AF] == 0x04;
            }
        }

        public bool SerialOutputsEnabled
        {
            get { return _serialOutputsEnabled; }
            set
            {
                if (_serialOutputsEnabled == value)
                {
                    return;
                }

                _serialOutputsEnabled = value;
                _deferSerialOutputsUntilPortWrite = false;
                if (!_serialOutputsEnabled)
                {
                    _customSerialOrbZeroReleasesAtna = false;
                    _iecPort.SetLineLow(IecBusLine.Data, false);
                    _iecPort.SetLineLow(IecBusLine.Clock, false);
                    RefreshSerialLineLevels(true);
                    return;
                }

                RefreshSerialLineLevels(true);
                PulseActiveAtnToSerialVia();
            }
        }

        /// <summary>
        /// Handles the prepare for custom code start operation.
        /// </summary>
        public void PrepareForCustomCodeStart(ushort entryAddress)
        {
            PrimeDosWorkspaceSelfPatchBytes(entryAddress);
            PrimeSerialViaForCustomLoaderHandoff();
            PrimeDiskViaForCustomLoaderHandoff();
            ApplyCustomLoaderTableCompatibility(entryAddress);
            _serialOutputsEnabled = false;
            _deferSerialOutputsUntilPortWrite = true;
            _customSerialOrbZeroReleasesAtna = true;
            // After a shortcut IEC read, the raw stream is already positioned
            // as if the consumed sector had spun past the head. Uploaded drive
            // code must then observe normal disk rotation; repeatedly snapping
            // the head back to ROM's $18/$19 current-sector pair makes raw
            // scanners see the same sector over and over.
            _alignDosCurrentSectorWritesToGcr = false;
            // Keep the serial VIA state intact when custom drive code takes
            // over. Fast loaders upload their own code on top of the already
            // initialised DOS/ROM environment and expect timer/PCR/DDR state
            // as well as the current port image to survive the handover. We
            // only release the external IEC lines here and defer re-enabling
            // them until the custom code performs its first explicit port
            // write.
            _iecPort.SetLineLow(IecBusLine.Data, false);
            _iecPort.SetLineLow(IecBusLine.Clock, false);
            // The command-channel phase often ends with ATN still asserted.
            // Sync CA1 to that level without generating a fresh IRQ edge,
            // otherwise the uploaded loader hits CLI and immediately vectors
            // back into the ROM ATN handler instead of staying in its own
            // serial routine.
            RefreshSerialLineLevels(false);
            _serialVia.SetCa1LevelSilently(_piAtn);
            // The software IEC path can leave a stale CA1 interrupt pending
            // from the just-finished command phase. Keep the CA1 interrupt
            // enable intact for loaders that wait for new serial edges, but
            // acknowledge the old edge so a CLI in uploaded code does not jump
            // into the ROM IRQ handler before the loader's first instruction.
            _serialVia.Write(0x0D, 0x02);
            // Keep the current VIA interrupt enables and timer setup intact.
            // Uploaded fastloaders are installed on top of the already running
            // DOS environment and often rely on the serial VIA continuing to
            // generate IRQs once they execute CLI. Clearing IER/IFR here left
            // Maniac Mansion's drive code spinning forever after U3 because
            // its handshake state byte was only advanced from the IRQ side.
            // The custom code is responsible for masking or acknowledging
            // interrupts explicitly if it wants a different serial state.
        }

        /// <summary>
        /// Restores DOS workspace bytes that self-patching raw-disk loaders copy back into their code.
        /// </summary>
        private void PrimeDosWorkspaceSelfPatchBytes(ushort entryAddress)
        {
            if (entryAddress >= 0x1800)
            {
                return;
            }

            int start = entryAddress & DriveRamMask;
            int end = Math.Min(DriveRamMask - 11, start + 0x80);
            for (int offset = start; offset <= end; offset++)
            {
                if (_ram[offset] != 0xAD ||
                    _ram[offset + 3] != 0x8D ||
                    _ram[offset + 6] != 0xAD ||
                    _ram[offset + 9] != 0x8D)
                {
                    continue;
                }

                int source1 = _ram[offset + 1] | (_ram[offset + 2] << 8);
                int target1 = _ram[offset + 4] | (_ram[offset + 5] << 8);
                int source2 = _ram[offset + 7] | (_ram[offset + 8] << 8);
                int target2 = _ram[offset + 10] | (_ram[offset + 11] << 8);
                if (source2 != source1 + 1 ||
                    source1 < DosBamWorkspaceStart ||
                    source2 > DosBamWorkspaceEnd ||
                    !IsDriveRamAddress(target1) ||
                    !IsDriveRamAddress(target2))
                {
                    continue;
                }

                int sourceIndex1 = source1 & DriveRamMask;
                int sourceIndex2 = source2 & DriveRamMask;
                if (_ram[sourceIndex1] != 0x00 || _ram[sourceIndex2] != 0x00)
                {
                    continue;
                }

                byte targetValue1 = _ram[target1 & DriveRamMask];
                byte targetValue2 = _ram[target2 & DriveRamMask];
                if (targetValue1 == 0x00 && targetValue2 == 0x00)
                {
                    continue;
                }

                // The high-level IEC command path bypasses parts of 1541 DOS
                // RAM setup. Some disk loaders copy operands from the DOS/BAM
                // workspace into already uploaded code; mirroring the target
                // bytes prevents zero-filled workspace from corrupting them.
                _ram[sourceIndex1] = targetValue1;
                _ram[sourceIndex2] = targetValue2;
            }
        }

        /// <summary>
        /// Returns whether an address maps to 1541 RAM.
        /// </summary>
        private static bool IsDriveRamAddress(int address)
        {
            return address >= 0x0000 && address < 0x1800;
        }

        /// <summary>
        /// Applies narrow compatibility fixes for uploaded raw-disk loader tables.
        /// </summary>
        private void ApplyCustomLoaderTableCompatibility(ushort entryAddress)
        {
            if (entryAddress != DotcFinalLoaderEntryAddress || !IsDotcFinalLoaderTableUnbiased())
            {
                return;
            }

            // The software IEC shortcut services DOTC's M-W/M-E sequence
            // without the earlier 1541-side table builder that VICE executes.
            // Recreate that resident table exactly so the final decruncher
            // reads the expected sector slices instead of whole sectors.
            for (int index = 0; index < DosBlockIndexEntryCount; index++)
            {
                byte offset = index < DotcFinalLoaderReferenceOffsets.Length
                    ? DotcFinalLoaderReferenceOffsets[index]
                    : (byte)0x00;
                byte sector = index < DotcFinalLoaderReferenceSectors.Length
                    ? DotcFinalLoaderReferenceSectors[index]
                    : (byte)0x00;
                byte track = index < DotcFinalLoaderReferenceTracks.Length
                    ? DotcFinalLoaderReferenceTracks[index]
                    : (byte)0x00;

                _ram[(RawLoaderOffsetTableBase + index) & DriveRamMask] = offset;
                _ram[(RawLoaderSectorTableBase + index) & DriveRamMask] = sector;
                _ram[(RawLoaderTrackTableBase + index) & DriveRamMask] = track;
            }
        }

        /// <summary>
        /// Returns whether the resident table matches Defender of the Crown's final loader handoff.
        /// </summary>
        private bool IsDotcFinalLoaderTableUnbiased()
        {
            return
                _ram[(RawLoaderTrackTableBase + 0x00) & DriveRamMask] == 0x11 &&
                _ram[(RawLoaderSectorTableBase + 0x00) & DriveRamMask] == 0x00 &&
                _ram[(RawLoaderTrackTableBase + 0x25) & DriveRamMask] == 0x10 &&
                _ram[(RawLoaderSectorTableBase + 0x25) & DriveRamMask] == 0x0E &&
                _ram[(RawLoaderTrackTableBase + 0x28) & DriveRamMask] == 0x10 &&
                _ram[(RawLoaderSectorTableBase + 0x28) & DriveRamMask] == 0x09;
        }

        /// <summary>
        /// Primes the serial VIA when the software IEC transport hands over to uploaded drive code.
        /// </summary>
        private void PrimeSerialViaForCustomLoaderHandoff()
        {
            if (_serialVia.PortBDirection != 0x00)
            {
                return;
            }

            // The high-level IEC transport services M-W/M-E without executing
            // the real 1541 ROM serial command path. Uploaded fastloaders then
            // inherit a reset-like VIA1 state even though on hardware the ROM
            // has just used PB1/PB3 as DATA/CLOCK outputs and left CA1 IRQs
            // live for the serial handshake. Seed those bits so loaders that
            // immediately bit-bang $1800 can drive the bus and still receive
            // the IRQ edge they wait for after CLI.
            _serialVia.Write(0x02, 0x0A);
            _serialVia.Write(0x0E, 0x82);
        }

        /// <summary>
        /// Primes the disk VIA state expected by uploaded raw-disk fast loaders.
        /// </summary>
        private void PrimeDiskViaForCustomLoaderHandoff()
        {
            if (_diskVia.PortBDirection != 0x00)
            {
                return;
            }

            // The software IEC path can accept M-W/M-E without running enough
            // 1541 ROM disk code to leave VIA2 in its normal post-DOS state.
            // Raw-disk loaders such as Defender of the Crown then only toggle
            // ORB bits and expect motor/density outputs to already be enabled.
            _diskVia.Write(0x02, DiskViaPortBOutputMask);
            _diskVia.Write(0x03, 0x00);
            _diskVia.Write(0x00, (byte)(_diskVia.PortBOutput | DiskViaPortBMotorAndDensity));
        }

        /// <summary>
        /// Reads serial port b.
        /// </summary>
        private byte ReadSerialPortB(byte orb, byte ddrb)
        {
            RefreshSerialLineLevels(false);
            byte value = 0x00;

            // On a real 1541 the IEC inputs are inverted before they reach the
            // serial VIA. That means asserted/low IEC DATA/CLOCK/ATN appear as
            // logical 1 on PB0/PB2/PB7.
            if (_piData)
            {
                value |= 0x01;
            }

            // Just like Pi1541 models it, the 1541 VIA output pins read back
            // as pulled high once the corresponding pin direction is switched
            // to input. Custom drive code polls these bits directly.
            if ((ddrb & 0x02) == 0)
            {
                value |= 0x02;
            }

            if (_piClock)
            {
                value |= 0x04;
            }

            if ((ddrb & 0x08) == 0)
            {
                value |= 0x08;
            }

            bool suppressAtnaPullup = _customSerialOrbZeroReleasesAtna &&
                orb == 0 &&
                (ddrb & 0x10) == 0;

            if ((ddrb & 0x10) == 0 && !suppressAtnaPullup)
            {
                value |= 0x10;
            }

            if (_piAtn)
            {
                value |= 0x80;
            }

            if ((_deviceSelectorBits & 0x01) != 0)
            {
                value |= 0x20;
            }

            if ((_deviceSelectorBits & 0x02) != 0)
            {
                value |= 0x40;
            }

            return value;
        }

        /// <summary>
        /// Writes serial port b.
        /// </summary>
        private void WriteSerialPortB(byte orb, byte ddrb)
        {
            _viaAtna = (orb & 0x10) != 0;
            _viaData = (orb & 0x02) != 0;
            _viaClock = (orb & 0x08) != 0;
            RecomputeAtnaDataSetToOut(ddrb);
            // Pi1541/VICE both model the 1541 serial VIA in a slightly odd
            // but hardware-faithful way: once PB1/PB3 are switched to input
            // the port still reads them back as asserted/high and the serial
            // gate logic continues from that port image. Fastloaders like
            // Maniac Mansion rely on this while handing off from the command
            // channel to their uploaded drive code. Keep PB4 special-cased
            // separately via RecomputeAtnaDataSetToOut().
            if ((ddrb & 0x02) == 0)
            {
                _viaData = true;
            }

            if ((ddrb & 0x08) == 0)
            {
                _viaClock = true;
            }

            // Match Pi1541's emulation of the 1541 serial VIA: PB1/PB3 are
            // the logical "assert IEC DATA/CLOCK" signals after the external
            // inverter/open-collector stage has been accounted for. A high
            // VIA port image therefore means "pull the IEC line low" here.
            _dataSetToOut = (ddrb & 0x02) != 0 && _viaData;
            _clockSetToOut = (ddrb & 0x08) != 0 && _viaClock;
            if (_deferSerialOutputsUntilPortWrite)
            {
                _serialOutputsEnabled = true;
                _deferSerialOutputsUntilPortWrite = false;
            }
            ApplySerialOutputs();
            UpdateSerialInputs();
        }

        /// <summary>
        /// Updates serial inputs.
        /// </summary>
        private void UpdateSerialInputs()
        {
            RefreshSerialLineLevels(true);
            ApplySerialOutputs();
        }

        /// <summary>
        /// Handles iec line changed.
        /// </summary>
        private void HandleIecLineChanged(IecBusLine line, bool isLow)
        {
            switch (line)
            {
                case IecBusLine.Atn:
                    _piAtn = isLow;
                    UpdateSerialViaAtnInput();
                    RecomputeAtnaDataSetToOut(_serialVia.PortBDirection);
                    ApplySerialOutputs();
                    UpdateObservedDataLine();
                    UpdateObservedClockLine();
                    break;

                case IecBusLine.Data:
                    UpdateObservedDataLine();
                    break;

                case IecBusLine.Clock:
                    UpdateObservedClockLine();
                    break;
            }
        }

        /// <summary>
        /// Handles the refresh serial line levels operation.
        /// </summary>
        private void RefreshSerialLineLevels(bool notifyCa1)
        {
            _piAtn = _iecPort.IsLineLow(IecBusLine.Atn);
            if (notifyCa1)
            {
                UpdateSerialViaAtnInput();
            }

            RecomputeAtnaDataSetToOut(_serialVia.PortBDirection);
            if ((_serialVia.PortBDirection & 0x10) == 0)
            {
                // Pi1541 and real hardware both stop the ATN/ATNA XOR path
                // from actively pulling DATA low once PB4 (ATNA) is switched
                // to input. Maniac Mansion's uploaded loader relies on this
                // transition while it hands control from the command channel
                // to its custom serial routine.
                _atnaDataSetToOut = false;
            }
            ApplySerialOutputs();
            UpdateObservedDataLine();
            UpdateObservedClockLine();
        }

        /// <summary>
        /// Handles the recompute atna data set to out operation.
        /// </summary>
        private void RecomputeAtnaDataSetToOut(byte ddrb)
        {
            // Match Pi1541 / real 1541 wiring: the ATN/ATNA XOR path can only
            // pull IEC DATA low while PB4 (ATNA) is configured as an output.
            // As soon as PB4 becomes an input the XOR path must release DATA.
            // Maniac Mansion explicitly relies on that transition when it
            // hands over from the command channel into uploaded drive code.
            if ((ddrb & 0x10) == 0)
            {
                _atnaDataSetToOut = false;
                return;
            }

            _atnaDataSetToOut = _viaAtna != _piAtn;
        }

        /// <summary>
        /// Applies serial outputs.
        /// </summary>
        private void ApplySerialOutputs()
        {
            if (!_serialOutputsEnabled)
            {
                _iecPort.SetLines(clockLow: false, dataLow: false);
                return;
            }

            _iecPort.SetLines(
                clockLow: _clockSetToOut && !_dotcInterBlockClockRelease,
                dataLow: _dataSetToOut || _atnaDataSetToOut);
        }

        /// <summary>
        /// Updates observed data line.
        /// </summary>
        private void UpdateObservedDataLine()
        {
            // PB0 is the external IEC DATA input. Direct PB1 DATA output must
            // still pull the bus low, but it must not be mirrored back as an
            // external talker response; DOTC-style two-wire loaders wait for
            // the C64 side to release DATA while the drive holds CLOCK.
            _piData = _atnaDataSetToOut
                ? true
                : _iecPort.IsLineLowExcludingOwner(IecBusLine.Data);
        }

        /// <summary>
        /// Updates observed clock line.
        /// </summary>
        private void UpdateObservedClockLine()
        {
            // PB2 observes external IEC CLOCK. Do not feed PB3 CLOCK output
            // back into PB2, otherwise fastloader acknowledge loops can
            // deadlock with the C64 waiting for the drive to release CLOCK.
            _piClock = _iecPort.IsLineLowExcludingOwner(IecBusLine.Clock);
        }

        /// <summary>
        /// Pulses active atn to serial via.
        /// </summary>
        private void PulseActiveAtnToSerialVia()
        {
            bool atnLow = _iecPort.IsLineLow(IecBusLine.Atn);
            _piAtn = atnLow;

            if (!atnLow)
            {
                _serialVia.SetCa1Level(false);
                return;
            }

            // If the 1541 ROM finishes initializing its serial VIA while ATN is
            // already asserted, the normal edge can be missed entirely. We
            // synthesize the current ATN assertion as a fresh CA1 transition so
            // the ROM serial state machine wakes up just like it would on
            // hardware after an in-flight device attention.
            _serialVia.SetCa1Level(false);
            _serialVia.SetCa1Level(true);
        }

        /// <summary>
        /// Updates serial via atn input.
        /// </summary>
        private void UpdateSerialViaAtnInput()
        {
            // Pi1541 models CA1 with the same logical ATN sense as PB7:
            // asserted IEC ATN (bus low) appears as a logical 1 on the 1541
            // side. The ROM programs VIA1 CA1 for positive-edge interrupts,
            // so feeding the already-inverted "ATN asserted" sense directly
            // into CA1 makes it wake up on ATN assertion, not on release.
            _serialVia.SetCa1Level(_piAtn);
        }

        /// <summary>
        /// Reads disk port a.
        /// </summary>
        private byte ReadDiskPortA(byte ora, byte ddra)
        {
            return _mechanism.ReadViaPortA(ora, ddra);
        }

        /// <summary>
        /// Handles the service dos job queue operation.
        /// </summary>
        private void ServiceDosJobQueue()
        {
            RefreshExecuteBufferJobState();

            // The 1541 DOS job queue is used by many uploaded drive-side
            // fastloaders. Jobs live at $00-$05, each slot has a paired
            // track/sector entry at $06/$07, $08/$09... and maps to buffers
            // $0300-$0800. Earlier this helper only serviced slot 4, which
            // left loaders that use the lower DOS buffers waiting forever.
            for (int slot = DosJobSlotCount - 1; slot >= 0; slot--)
            {
                byte job = _ram[slot];
                if ((job & 0x80) == 0)
                {
                    continue;
                }

                int trackSectorOffset = DosJobTrackSectorBase + (slot * 2);
                int bufferOffset = DosJobBufferBase + (slot * DosJobBufferSize);
                int command = job & 0xF0;
                int track = _ram[trackSectorOffset];
                int sector = _ram[trackSectorOffset + 1];
                bool handled = true;
                bool ok = false;

                switch (command)
                {
                    case 0xD0:
                    case 0xE0:
                        if (IsExecuteBufferJobTracked(slot, job))
                        {
                            continue;
                        }

                        ResolveExecuteJobCurrentSectorReference(ref track, ref sector, trackSectorOffset);
                        PrimeDosDiskContext(track, sector, false, false);
                        QueueExecuteBufferJob(slot, job, track, sector);
                        return;

                    case 0x80: // READ
                        ok = TryReadJobSector(track, sector, bufferOffset);
                        if (ok)
                        {
                            PrimeDosDiskContextAfterShortcutSectorJob(track, sector);
                        }

                        break;

                    case 0x90: // WRITE
                        ok = TryWriteJobSector(track, sector, bufferOffset);
                        if (ok)
                        {
                            PrimeDosDiskContextAfterShortcutSectorJob(track, sector);
                        }

                        break;

                    case 0xA0: // VERIFY
                    case 0xB0: // SEEK
                        ok = _mechanism.TryReadSector(track, sector, out _);
                        if (ok)
                        {
                            PrimeDosDiskContextAfterShortcutSectorJob(track, sector);
                        }

                        break;

                    case 0xC0: // BUMP/RESTORE class jobs complete without a data buffer.
                        ok = true;
                        break;

                    default:
                        handled = false;
                        break;
                }

                if (!handled)
                {
                    continue;
                }

                _ram[slot] = ok ? (byte)0x01 : (byte)0x02;
                _lastJobQueueDebug = string.Format("#{0}:{1:X2}@{2}/{3}:{4}", slot, job, track, sector, ok ? "OK" : "ERR");
            }
        }

        /// <summary>
        /// Attempts to read job sector and reports whether it succeeded.
        /// </summary>
        private bool TryReadJobSector(int track, int sector, int bufferOffset)
        {
            byte[] sectorBytes;
            if (!_mechanism.TryReadSector(track, sector, out sectorBytes) || sectorBytes == null || sectorBytes.Length < 256)
            {
                return false;
            }

            CopySectorToRam(sectorBytes, bufferOffset);
            return true;
        }

        /// <summary>
        /// Attempts to write job sector and reports whether it succeeded.
        /// </summary>
        private bool TryWriteJobSector(int track, int sector, int bufferOffset)
        {
            var sectorBytes = new byte[256];
            CopyRamToSector(bufferOffset, sectorBytes);
            return _mechanism.TryWriteSector(track, sector, sectorBytes);
        }

        /// <summary>
        /// Queues a DOS buffer execution job for the 1541 CPU/ROM path.
        /// </summary>
        private void QueueExecuteBufferJob(int slot, byte job, int track, int sector)
        {
            if ((job & 0x01) != 0)
            {
                _ram[slot & DriveRamMask] = 0x0F;
                _lastJobQueueDebug = string.Format("#{0}:{1:X2}@{2}/{3}:DRV", slot, job, track, sector);
                return;
            }

            if (IsExecuteBufferJobTracked(slot, job))
            {
                return;
            }

            _pendingExecuteBufferJobSlot = slot;
            _pendingExecuteBufferJobCode = job;
            _lastJobQueueDebug = string.Format("#{0}:{1:X2}@{2}/{3}:EXEC", slot, job, track, sector);
        }

        /// <summary>
        /// Returns whether the DOS buffer execute job is already owned by the CPU/ROM path.
        /// </summary>
        private bool IsExecuteBufferJobTracked(int slot, byte job)
        {
            return (_pendingExecuteBufferJobSlot == slot && _pendingExecuteBufferJobCode == job) ||
                (_activeExecuteBufferJobSlot == slot && _activeExecuteBufferJobCode == job);
        }

        /// <summary>
        /// Maps an invalid $00/$00 execute-job pointer to the current ROM disk context.
        /// </summary>
        private void ResolveExecuteJobCurrentSectorReference(ref int track, ref int sector, int trackSectorOffset)
        {
            if (track != 0 || sector != 0 || _ram[DosCurrentTrackAddress] == 0x00)
            {
                return;
            }

            // Track 0 does not exist on a 1541 disk. Some fast loaders use an
            // execute job with $00/$00 as "continue from DOS current sector",
            // then call ROM helpers that copy the job pair into $18/$19.
            // Normalize the pair before the ROM path sees it so those helpers
            // inherit the mounted/previous sector instead of searching track 0.
            track = _ram[DosCurrentTrackAddress];
            sector = _ram[DosCurrentSectorAddress];
            _ram[trackSectorOffset & DriveRamMask] = (byte)track;
            _ram[(trackSectorOffset + 1) & DriveRamMask] = (byte)sector;
        }

        /// <summary>
        /// Restores DOS zero-page state assumed by the ROM buffer execution path.
        /// </summary>
        private void PrimeExecuteBufferJobZeroPage()
        {
            // The ROM job loop writes the low byte of the track/sector pointer
            // to $32, but it relies on $33 staying zero from DOS init. Uploaded
            // loaders can use $33 as scratch before queuing $D0/$E0; reset it
            // at the handoff so ROM helpers such as $F50A read the intended
            // job track/sector pair instead of a stray page.
            _ram[DosTrackSectorPointerHighAddress] = 0x00;
        }

        /// <summary>
        /// Primes DOS disk context after a host-side shortcut sector job.
        /// </summary>
        private void PrimeDosDiskContextAfterShortcutSectorJob(int track, int sector)
        {
            PrimeDosDiskContext(track, sector, true, true);
            _alignDosCurrentSectorWritesToGcr = true;
        }

        /// <summary>
        /// Seeds the 1541 DOS block-index workspace inherited by uploaded loaders.
        /// </summary>
        private void PrimeDosBlockIndexWorkspace(int blocks)
        {
            int count = blocks > 0
                ? Math.Min(DosBlockIndexEntryCount, blocks)
                : DosBlockIndexEntryCount;
            for (int index = 0; index < count; index++)
            {
                _ram[(DosBlockIndexLowBase + index) & DriveRamMask] = (byte)(index & 0xFF);
                _ram[(DosBlockIndexHighBase + index) & DriveRamMask] = (byte)(index >> 8);
            }
        }

        /// <summary>
        /// Aligns the raw GCR stream when ROM code advances the DOS current-sector pair.
        /// </summary>
        private void AlignGcrStreamToDosCurrentSector()
        {
            int track = _ram[DosCurrentTrackAddress];
            int sector = _ram[DosCurrentSectorAddress];
            if (track <= 0 || sector < 0)
            {
                return;
            }

            byte[] ignoredSectorBytes;
            if (!_mechanism.TryReadSector(track, sector, out ignoredSectorBytes))
            {
                return;
            }

            _mechanism.SeekToSectorStart(track, sector, DosCurrentSectorSearchLeadBytes);
        }

        /// <summary>
        /// Primes DOS zero-page disk context used by ROM header search helpers.
        /// </summary>
        private void PrimeDosDiskContext(int track, int sector, bool updateCurrentSector, bool seekPastSector)
        {
            _ram[DosHeaderBlockIdAddress] = DosHeaderBlockId;
            byte currentTrack = _ram[DosCurrentTrackAddress];
            byte currentSector = _ram[DosCurrentSectorAddress];

            if (TryReadMountedDiskId(out byte diskId1, out byte diskId2))
            {
                for (int index = 0; index < DosJobSlotCount; index++)
                {
                    int offset = DosDiskIdBase + (index * 2);
                    _ram[offset & DriveRamMask] = diskId1;
                    _ram[(offset + 1) & DriveRamMask] = diskId2;
                }
            }

            if (updateCurrentSector)
            {
                if (seekPastSector)
                {
                    // A host-side shortcut has already consumed the sector
                    // that DOS requested. Leave the DOS current-sector bytes
                    // on that sector, but put the raw stream where a spinning
                    // disk would be afterward so uploaded loaders see the
                    // next record.
                    _mechanism.SeekToNextSectorStart(track, sector);
                }
                else
                {
                    _mechanism.SeekToSectorStart(track, sector);
                }
            }
            else
            {
                _mechanism.SeekToTrack(track);
            }

            if (updateCurrentSector)
            {
                _ram[DosCurrentTrackAddress] = (byte)track;
                _ram[DosCurrentSectorAddress] = (byte)sector;
            }
            else
            {
                _ram[DosCurrentTrackAddress] = currentTrack;
                _ram[DosCurrentSectorAddress] = currentSector;
            }
        }

        /// <summary>
        /// Reads the mounted disk id bytes from the BAM sector.
        /// </summary>
        private bool TryReadMountedDiskId(out byte diskId1, out byte diskId2)
        {
            diskId1 = 0x00;
            diskId2 = 0x00;

            byte[] bam;
            if (!_mechanism.TryReadSector(18, 0, out bam) || bam == null || bam.Length <= 0xA3)
            {
                return false;
            }

            diskId1 = bam[0xA2];
            diskId2 = bam[0xA3];
            return true;
        }

        /// <summary>
        /// Clears execute-job tracking once the ROM or custom code changes the job byte.
        /// </summary>
        private void RefreshExecuteBufferJobState()
        {
            if (_pendingExecuteBufferJobSlot != NoDosJobSlot &&
                _ram[_pendingExecuteBufferJobSlot & DriveRamMask] != _pendingExecuteBufferJobCode)
            {
                _pendingExecuteBufferJobSlot = NoDosJobSlot;
                _pendingExecuteBufferJobCode = 0;
            }

            if (_activeExecuteBufferJobSlot != NoDosJobSlot &&
                _ram[_activeExecuteBufferJobSlot & DriveRamMask] != _activeExecuteBufferJobCode)
            {
                _activeExecuteBufferJobSlot = NoDosJobSlot;
                _activeExecuteBufferJobCode = 0;
            }
        }

        /// <summary>
        /// Copies a disk sector into mirrored 1541 RAM.
        /// </summary>
        private void CopySectorToRam(byte[] sectorBytes, int bufferOffset)
        {
            for (int index = 0; index < DosJobBufferSize; index++)
            {
                _ram[(bufferOffset + index) & DriveRamMask] = sectorBytes[index];
            }
        }

        /// <summary>
        /// Copies mirrored 1541 RAM into a disk sector buffer.
        /// </summary>
        private void CopyRamToSector(int bufferOffset, byte[] sectorBytes)
        {
            for (int index = 0; index < DosJobBufferSize; index++)
            {
                sectorBytes[index] = _ram[(bufferOffset + index) & DriveRamMask];
            }
        }

        /// <summary>
        /// Writes disk port a.
        /// </summary>
        private void WriteDiskPortA(byte ora, byte ddra)
        {
            _mechanism.WriteViaPortA(ora, ddra);
        }

        /// <summary>
        /// Reads disk port b.
        /// </summary>
        private byte ReadDiskPortB(byte orb, byte ddrb)
        {
            return _mechanism.ReadViaPortB(orb, ddrb);
        }

        /// <summary>
        /// Writes disk port b.
        /// </summary>
        private void WriteDiskPortB(byte orb, byte ddrb)
        {
            _mechanism.ApplyViaPortB(orb, ddrb);
        }
    }
}
