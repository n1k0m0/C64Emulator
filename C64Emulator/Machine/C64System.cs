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
using OpenTK.Input;

namespace C64Emulator.Core
{
    /// <summary>
    /// Coordinates the emulated C64 chips, buses, media, input, and frame timing.
    /// </summary>
    public sealed class C64System : IDisposable
    {
        /// <summary>
        /// Stores phi2 bus state state.
        /// </summary>
        private struct Phi2BusState
        {
            public bool BaLow;
            public bool AecLow;
            public bool CpuCanAccess;
            public bool VicCanAccess;
        }

        private readonly C64Model _model;
        private readonly FrameBuffer _frameBuffer;
        private readonly SystemBus _bus;
        private readonly Cpu6510 _cpu;
        private readonly Vic2 _vic;
        private readonly Cia1 _cia1;
        private readonly Cia2 _cia2;
        private readonly Sid _sid;
        private readonly IecBus _iecBus;
        private readonly IecDrive1541 _drive8;
        private readonly IecDrive1541 _drive9;
        private readonly IecDrive1541 _drive10;
        private readonly IecDrive1541 _drive11;
        private readonly IecKernalBridge _iecKernalBridge;
        private readonly MediaManager _mediaManager;
        private readonly Dictionary<int, string> _mountedDrivePaths = new Dictionary<int, string>();
        private readonly HashSet<Key> _pressedHostKeys = new HashSet<Key>();
        private readonly object _syncRoot = new object();
        private MountedMediaInfo _lastMountedMedia = MountedMediaInfo.None;
        private Key? _lastMirroredPollKey;
        private int _pollMirrorRepeatDelayCycles;
        private double _drive1541TargetCycles;
        private double _drive1541ExecutedCycles;
        private bool _forceSoftwareIecTransport = true;
        private bool _enableInputInjection = true;
        private bool _catchingUpDrivesForIecAccess;

        /// <summary>
        /// Handles the c64 system operation.
        /// </summary>
        public C64System(C64Model model)
        {
            _model = model;
            _frameBuffer = new FrameBuffer(model.VisibleWidth, model.VisibleHeight);
            _bus = new SystemBus();
            _cia1 = new Cia1();
            _cia2 = new Cia2();
            _sid = new Sid();
            _iecBus = new IecBus();
            _iecKernalBridge = new IecKernalBridge();
            _mediaManager = new MediaManager();
            _vic = new Vic2(_bus, _frameBuffer, model);
            _cia2.AttachIecBus(_iecBus.CreatePort("C64"));
            _drive8 = new IecDrive1541(8, _iecBus.CreatePort("Drive8"), _iecBus.CreatePort("Drive8-HW"));
            _drive9 = new IecDrive1541(9, _iecBus.CreatePort("Drive9"), _iecBus.CreatePort("Drive9-HW"));
            _drive10 = new IecDrive1541(10, _iecBus.CreatePort("Drive10"), _iecBus.CreatePort("Drive10-HW"));
            _drive11 = new IecDrive1541(11, _iecBus.CreatePort("Drive11"), _iecBus.CreatePort("Drive11-HW"));
            _cia2.BeforeIecPortAccess = CatchUpDrivesForIecPortAccess;
            _drive8.CustomExecutionStartGate = CanStartDriveCustomExecution;
            _drive9.CustomExecutionStartGate = CanStartDriveCustomExecution;
            _drive10.CustomExecutionStartGate = CanStartDriveCustomExecution;
            _drive11.CustomExecutionStartGate = CanStartDriveCustomExecution;
            _drive8.StandardRomTransportStartGate = CanStartDriveRomTransport;
            _drive9.StandardRomTransportStartGate = CanStartDriveRomTransport;
            _drive10.StandardRomTransportStartGate = CanStartDriveRomTransport;
            _drive11.StandardRomTransportStartGate = CanStartDriveRomTransport;
            _iecKernalBridge.AttachDrive(_drive8);
            _iecKernalBridge.AttachDrive(_drive9);
            _iecKernalBridge.AttachDrive(_drive10);
            _iecKernalBridge.AttachDrive(_drive11);
            _bus.Connect(_vic, _cia1, _cia2, _sid);
            _cpu = new Cpu6510(_bus, _mediaManager, _iecKernalBridge);
            ApplyIecTransportMode();

            _bus.InitializeMemory();
            _bus.LoadRoms(null);
            Reset();
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            lock (_syncRoot)
            {
                TickCore();
                if (!RunDrivesToCurrentTime())
                {
                    AdvanceIdleDriveVisualState(1);
                }
            }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            lock (_syncRoot)
            {
                ResetCore();
            }
        }

        /// <summary>
        /// Handles the reset and reload media operation.
        /// </summary>
        public string ResetAndReloadMedia()
        {
            lock (_syncRoot)
            {
                MountedMediaInfo mountedMedia = _mediaManager.MountedMedia;
                string mountedHostPath = mountedMedia.HostPath;
                bool hadMountedMedia = mountedMedia.HasMedia && !string.IsNullOrWhiteSpace(mountedHostPath);
                var mountedDrivePaths = new Dictionary<int, string>(_mountedDrivePaths);

                ResetCore();

                string resetMessage = "RESET COMPLETE";
                foreach (KeyValuePair<int, string> mountedDrivePath in mountedDrivePaths)
                {
                    MountDiskToDrive(mountedDrivePath.Value, mountedDrivePath.Key, true);
                    resetMessage = "RESET MEDIA RELOADED";
                }

                if (hadMountedMedia && mountedMedia.Kind == MountedMediaKind.Prg)
                {
                    MediaMountResult mountResult = _mediaManager.Mount(mountedHostPath);
                    if (!mountResult.Success)
                    {
                        return "RESET MEDIA FAILED";
                    }

                    if (mountResult.AutoLoadProgramBytes != null)
                    {
                        LoadProgramBytes(mountResult.AutoLoadProgramBytes);
                    }

                    _lastMountedMedia = mountResult.MountedMedia;
                    resetMessage = "RESET MEDIA RELOADED";
                }

                return resetMessage;
            }
        }

        /// <summary>
        /// Performs a C64-side reset while preserving RAM and inserted host media.
        /// </summary>
        public string WarmReset()
        {
            lock (_syncRoot)
            {
                _cia1.Reset();
                _cia2.Reset();
                _vic.Reset();
                _sid.Reset();
                _iecKernalBridge.Reset();
                _pressedHostKeys.Clear();
                _lastMirroredPollKey = null;
                _pollMirrorRepeatDelayCycles = 0;
                _catchingUpDrivesForIecAccess = false;
                _cpu.Reset(_bus.ReadResetVector());
                return "WARM RESET COMPLETE";
            }
        }

        /// <summary>
        /// Releases resources owned by the component.
        /// </summary>
        public void Dispose()
        {
            lock (_syncRoot)
            {
                _sid.Dispose();
            }
        }

        /// <summary>
        /// Writes the complete emulated machine state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            lock (_syncRoot)
            {
                _bus.SaveState(writer);
                _cpu.SaveState(writer);
                _vic.SaveState(writer);
                _cia1.SaveState(writer);
                _cia2.SaveState(writer);
                _sid.SaveState(writer);
                _frameBuffer.SaveState(writer);
                _drive8.SaveState(writer);
                _drive9.SaveState(writer);
                _drive10.SaveState(writer);
                _drive11.SaveState(writer);
                _iecBus.SaveState(writer);
                _mediaManager.SaveState(writer);
                WriteMountedMediaInfo(writer, _lastMountedMedia);
                WriteMountedDrivePaths(writer);
                WritePressedHostKeys(writer);
                writer.Write(_lastMirroredPollKey.HasValue);
                if (_lastMirroredPollKey.HasValue)
                {
                    writer.Write((int)_lastMirroredPollKey.Value);
                }

                writer.Write(_pollMirrorRepeatDelayCycles);
                writer.Write(_drive1541TargetCycles);
                writer.Write(_drive1541ExecutedCycles);
                writer.Write(_catchingUpDrivesForIecAccess);
            }
        }

        /// <summary>
        /// Restores the complete emulated machine state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            lock (_syncRoot)
            {
                _bus.LoadState(reader);
                _cpu.LoadState(reader);
                _vic.LoadState(reader);
                _cia1.LoadState(reader);
                _cia2.LoadState(reader);
                _sid.LoadState(reader);
                _frameBuffer.LoadState(reader);
                _drive8.LoadState(reader);
                _drive9.LoadState(reader);
                _drive10.LoadState(reader);
                _drive11.LoadState(reader);
                _iecBus.LoadState(reader);
                _mediaManager.LoadState(reader);
                _lastMountedMedia = ReadMountedMediaInfo(reader);
                ReadMountedDrivePaths(reader);
                ReadPressedHostKeys(reader);
                _lastMirroredPollKey = reader.ReadBoolean() ? (Key?)((Key)reader.ReadInt32()) : null;
                _pollMirrorRepeatDelayCycles = reader.ReadInt32();
                _drive1541TargetCycles = reader.ReadDouble();
                _drive1541ExecutedCycles = reader.ReadDouble();
                _catchingUpDrivesForIecAccess = reader.ReadBoolean();
                _iecKernalBridge.Reset();
            }
        }

        public FrameBuffer FrameBuffer
        {
            get { return _frameBuffer; }
        }

        public C64Model Model
        {
            get { return _model; }
        }

        public VicTiming Timing
        {
            get
            {
                lock (_syncRoot)
                {
                    return _vic.GetTiming();
                }
            }
        }

        public Cpu6510 Cpu
        {
            get { return _cpu; }
        }

        public bool EnableKernalIecHooks
        {
            get
            {
                lock (_syncRoot)
                {
                    return _cpu.EnableKernalIecHooks;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _cpu.EnableKernalIecHooks = value;
                    ApplyIecTransportMode();
                }
            }
        }

        public bool EnableLoadHack
        {
            get
            {
                lock (_syncRoot)
                {
                    return _cpu.EnableLoadHack;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _cpu.EnableLoadHack = value;
                }
            }
        }

        public bool ForceSoftwareIecTransport
        {
            get
            {
                lock (_syncRoot)
                {
                    return _forceSoftwareIecTransport;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _forceSoftwareIecTransport = value;
                    ApplyIecTransportMode();
                }
            }
        }

        public bool EnableInputInjection
        {
            get
            {
                lock (_syncRoot)
                {
                    return _enableInputInjection;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _enableInputInjection = value;
                    if (!_enableInputInjection)
                    {
                        _lastMirroredPollKey = null;
                        _pollMirrorRepeatDelayCycles = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Runs the cycles routine.
        /// </summary>
        public void RunCycles(int cycles)
        {
            lock (_syncRoot)
            {
                for (var i = 0; i < cycles; i++)
                {
                    TickCore();
                }

                if (!RunDrivesToCurrentTime())
                {
                    AdvanceIdleDriveVisualState(cycles);
                }
            }
        }

        /// <summary>
        /// Handles the peek operation.
        /// </summary>
        public byte Peek(ushort address)
        {
            lock (_syncRoot)
            {
                return _bus.CpuRead(address);
            }
        }

        /// <summary>
        /// Peeks ram.
        /// </summary>
        public byte PeekRam(ushort address)
        {
            lock (_syncRoot)
            {
                return _bus.ReadRam(address);
            }
        }

        /// <summary>
        /// Gets the memory config debug info value.
        /// </summary>
        public string GetMemoryConfigDebugInfo()
        {
            lock (_syncRoot)
            {
                return string.Format(
                    "ddr={0:X2} port={1:X2} effective={2:X2} ioVisible={3}",
                    _bus.ReadRam(0x0000),
                    _bus.ReadRam(0x0001),
                    _bus.ProcessorPortValue,
                    _bus.IsIoAreaVisible);
            }
        }

        public object SyncRoot
        {
            get { return _syncRoot; }
        }

        public IecBus IecBus
        {
            get { return _iecBus; }
        }

        /// <summary>
        /// Returns whether drive mounted is true.
        /// </summary>
        public bool IsDriveMounted(int deviceNumber)
        {
            IecDrive1541 drive = GetDrive(deviceNumber);
            return drive != null && drive.IsMounted;
        }

        /// <summary>
        /// Returns whether drive led on is true.
        /// </summary>
        public bool IsDriveLedOn(int deviceNumber)
        {
            IecDrive1541 drive = GetDrive(deviceNumber);
            return drive != null && drive.IsLedOn;
        }

        /// <summary>
        /// Returns whether drive active is true.
        /// </summary>
        public bool IsDriveActive(int deviceNumber)
        {
            IecDrive1541 drive = GetDrive(deviceNumber);
            return drive != null && drive.IsActivityActive;
        }

        public JoystickPort CurrentJoystickPort
        {
            get
            {
                lock (_syncRoot)
                {
                    return _cia1.ActiveJoystickPort;
                }
            }
        }

        /// <summary>
        /// Gets the drive debug info value.
        /// </summary>
        public string GetDriveDebugInfo(int deviceNumber)
        {
            lock (_syncRoot)
            {
                IecDrive1541 drive = GetDrive(deviceNumber);
                return drive != null ? drive.GetDebugInfo() : "NO DRIVE";
            }
        }

        /// <summary>
        /// Peeks drive memory.
        /// </summary>
        public byte PeekDriveMemory(int deviceNumber, ushort address)
        {
            lock (_syncRoot)
            {
                IecDrive1541 drive = GetDrive(deviceNumber);
                return drive != null ? drive.Hardware.ReadMemory(address) : (byte)0xFF;
            }
        }

        /// <summary>
        /// Gets the drive program counter value.
        /// </summary>
        public ushort GetDriveProgramCounter(int deviceNumber)
        {
            lock (_syncRoot)
            {
                IecDrive1541 drive = GetDrive(deviceNumber);
                return drive != null ? drive.Hardware.ProgramCounter : (ushort)0x0000;
            }
        }

        /// <summary>
        /// Gets the iec debug info value.
        /// </summary>
        public string GetIecDebugInfo()
        {
            lock (_syncRoot)
            {
                return string.Format(
                    "cia2={0} atnLow={1}({4}) clkLow={2}({5}) dataLow={3}({6})",
                    _cia2.GetDebugInfo(),
                    _iecBus.IsLineLow(IecBusLine.Atn),
                    _iecBus.IsLineLow(IecBusLine.Clock),
                    _iecBus.IsLineLow(IecBusLine.Data),
                    _iecBus.GetLineOwnersDebug(IecBusLine.Atn),
                    _iecBus.GetLineOwnersDebug(IecBusLine.Clock),
                    _iecBus.GetLineOwnersDebug(IecBusLine.Data));
            }
        }

        /// <summary>
        /// Gets compact CIA debug information.
        /// </summary>
        public string GetCiaDebugInfo()
        {
            lock (_syncRoot)
            {
                return "cia1={" + _cia1.GetDebugInfo() + "} cia2={" + _cia2.GetDebugInfo() + "}";
            }
        }

        /// <summary>
        /// Gets compact SID debug information.
        /// </summary>
        public string GetSidDebugInfo()
        {
            lock (_syncRoot)
            {
                return _sid.GetDebugInfo();
            }
        }

        /// <summary>
        /// Gets the cpu debug info value.
        /// </summary>
        public string GetCpuDebugInfo()
        {
            lock (_syncRoot)
            {
                return string.Format(
                    "pc={0:X4} a={1:X2} x={2:X2} y={3:X2} sp={4:X2} sr={5:X2} state={6} lastIec={7}",
                    _cpu.PC,
                    _cpu.A,
                    _cpu.X,
                    _cpu.Y,
                    _cpu.SP,
                    _cpu.SR,
                    _cpu.State,
                    _cpu.LastIecHookDebug);
            }
        }

        /// <summary>
        /// Enqueues petscii text.
        /// </summary>
        public void EnqueuePetsciiText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_syncRoot)
            {
                const ushort KeyboardBufferCountAddress = 0x00C6;
                const ushort KeyboardBufferStartAddress = 0x0277;
                const int KeyboardBufferCapacity = 10;

                for (int index = 0; index < text.Length; index++)
                {
                    byte count = _bus.CpuRead(KeyboardBufferCountAddress);
                    if (count >= KeyboardBufferCapacity)
                    {
                        break;
                    }

                    _bus.WriteRam((ushort)(KeyboardBufferStartAddress + count), (byte)text[index]);
                    _bus.WriteRam(KeyboardBufferCountAddress, (byte)(count + 1));
                }
            }
        }

        /// <summary>
        /// Handles the key down operation.
        /// </summary>
        public void KeyDown(Key key)
        {
            lock (_syncRoot)
            {
                _pressedHostKeys.Add(key);
                TryEnqueueHostKeyToKeyboardBuffer(key);
                _cia1.KeyDown(key);
            }
        }

        /// <summary>
        /// Handles the key up operation.
        /// </summary>
        public void KeyUp(Key key)
        {
            lock (_syncRoot)
            {
                _pressedHostKeys.Remove(key);
                _cia1.KeyUp(key);
                if (_lastMirroredPollKey == key)
                {
                    _lastMirroredPollKey = null;
                    _pollMirrorRepeatDelayCycles = 0;
                }
            }
        }

        public float SidMasterVolume
        {
            get
            {
                lock (_syncRoot)
                {
                    return _sid.MasterVolume;
                }
            }
        }

        public float SidNoiseLevel
        {
            get
            {
                lock (_syncRoot)
                {
                    return _sid.NoiseLevel;
                }
            }
        }

        public SidChipModel CurrentSidChipModel
        {
            get
            {
                lock (_syncRoot)
                {
                    return _sid.ChipModel;
                }
            }
        }

        /// <summary>
        /// Sets the sid master volume value.
        /// </summary>
        public void SetSidMasterVolume(float value)
        {
            lock (_syncRoot)
            {
                _sid.MasterVolume = value;
            }
        }

        /// <summary>
        /// Sets the sid noise level value.
        /// </summary>
        public void SetSidNoiseLevel(float value)
        {
            lock (_syncRoot)
            {
                _sid.NoiseLevel = value;
            }
        }

        /// <summary>
        /// Sets the sid chip model value.
        /// </summary>
        public void SetSidChipModel(SidChipModel chipModel)
        {
            lock (_syncRoot)
            {
                _sid.ChipModel = chipModel;
            }
        }

        /// <summary>
        /// Sets the joystick port value.
        /// </summary>
        public void SetJoystickPort(JoystickPort joystickPort)
        {
            lock (_syncRoot)
            {
                _cia1.ActiveJoystickPort = joystickPort;
            }
        }

        /// <summary>
        /// Sets the active-low joystick state supplied by a host gamepad.
        /// </summary>
        public void SetGamepadJoystickState(byte activeLowJoystickState)
        {
            lock (_syncRoot)
            {
                _cia1.SetGamepadJoystickState(activeLowJoystickState);
            }
        }

        public MountedMediaInfo MountedMedia
        {
            get
            {
                lock (_syncRoot)
                {
                    return _lastMountedMedia;
                }
            }
        }

        /// <summary>
        /// Gets a snapshot of all host disk images currently inserted into emulated drives.
        /// </summary>
        public Dictionary<int, string> GetMountedDriveHostPaths()
        {
            lock (_syncRoot)
            {
                return new Dictionary<int, string>(_mountedDrivePaths);
            }
        }

        /// <summary>
        /// Mounts media.
        /// </summary>
        public string MountMedia(string path)
        {
            return MountMedia(path, 8);
        }

        /// <summary>
        /// Mounts media.
        /// </summary>
        public string MountMedia(string path, int deviceNumber)
        {
            lock (_syncRoot)
            {
                if (string.Equals(Path.GetExtension(path), ".d64", StringComparison.OrdinalIgnoreCase))
                {
                    return MountDiskToDrive(path, deviceNumber, true);
                }

                MediaMountResult result = _mediaManager.Mount(path);
                if (result.Success && result.AutoLoadProgramBytes != null)
                {
                    LoadProgramBytes(result.AutoLoadProgramBytes);
                    _lastMountedMedia = result.MountedMedia;
                }

                return result.Message;
            }
        }

        /// <summary>
        /// Ejects media.
        /// </summary>
        public string EjectMedia()
        {
            lock (_syncRoot)
            {
                string message = _mediaManager.Eject();
                _mountedDrivePaths.Clear();
                _drive8.EjectDisk();
                _drive9.EjectDisk();
                _drive10.EjectDisk();
                _drive11.EjectDisk();
                _lastMountedMedia = MountedMediaInfo.None;
                return message;
            }
        }

        /// <summary>
        /// Computes phi2 bus state.
        /// </summary>
        private Phi2BusState ComputePhi2BusState()
        {
            if (_vic.RequiresBusThisCycle())
            {
                return new Phi2BusState
                {
                    BaLow = true,
                    AecLow = true,
                    CpuCanAccess = false,
                    VicCanAccess = true
                };
            }

            if (_vic.HasBusRequestPendingThisCycle())
            {
                CpuTraceAccessType accessType = _cpu.PredictNextCycleAccessType();
                if (accessType == CpuTraceAccessType.Write)
                {
                    return new Phi2BusState
                    {
                        BaLow = true,
                        AecLow = false,
                        CpuCanAccess = true,
                        VicCanAccess = false
                    };
                }

                return new Phi2BusState
                {
                    BaLow = true,
                    AecLow = false,
                    CpuCanAccess = false,
                    VicCanAccess = false
                };
            }

            return new Phi2BusState
            {
                BaLow = false,
                AecLow = false,
                CpuCanAccess = true,
                VicCanAccess = false
            };
        }

        /// <summary>
        /// Handles the reset core operation.
        /// </summary>
        private void ResetCore()
        {
            _bus.InitializeMemory();
            _bus.LoadRoms(null);
            _cia1.Reset();
            _cia2.Reset();
            _vic.Reset();
            _sid.Reset();
            _drive8.Reset();
            _drive9.Reset();
            _drive10.Reset();
            _drive11.Reset();
            _iecKernalBridge.Reset();
            _mountedDrivePaths.Clear();
            _lastMountedMedia = MountedMediaInfo.None;
            _pressedHostKeys.Clear();
            _lastMirroredPollKey = null;
            _pollMirrorRepeatDelayCycles = 0;
            _drive1541TargetCycles = 0.0;
            _drive1541ExecutedCycles = 0.0;
            _catchingUpDrivesForIecAccess = false;
            _cpu.Reset(_bus.ReadResetVector());
        }

        /// <summary>
        /// Writes mounted media overlay metadata into a savestate stream.
        /// </summary>
        private static void WriteMountedMediaInfo(BinaryWriter writer, MountedMediaInfo mediaInfo)
        {
            mediaInfo = mediaInfo ?? MountedMediaInfo.None;
            writer.Write((int)mediaInfo.Kind);
            BinaryStateIO.WriteString(writer, mediaInfo.ShortLabel);
            BinaryStateIO.WriteString(writer, mediaInfo.DisplayName);
            BinaryStateIO.WriteString(writer, mediaInfo.HostPath);
        }

        /// <summary>
        /// Reads mounted media overlay metadata from a savestate stream.
        /// </summary>
        private static MountedMediaInfo ReadMountedMediaInfo(BinaryReader reader)
        {
            var kind = (MountedMediaKind)reader.ReadInt32();
            string shortLabel = BinaryStateIO.ReadString(reader) ?? "NONE";
            string displayName = BinaryStateIO.ReadString(reader) ?? string.Empty;
            string hostPath = BinaryStateIO.ReadString(reader) ?? string.Empty;
            return new MountedMediaInfo(kind, shortLabel, displayName, hostPath);
        }

        /// <summary>
        /// Writes mounted drive host paths.
        /// </summary>
        private void WriteMountedDrivePaths(BinaryWriter writer)
        {
            writer.Write(_mountedDrivePaths.Count);
            foreach (KeyValuePair<int, string> entry in _mountedDrivePaths)
            {
                writer.Write(entry.Key);
                BinaryStateIO.WriteString(writer, entry.Value);
            }
        }

        /// <summary>
        /// Reads mounted drive host paths.
        /// </summary>
        private void ReadMountedDrivePaths(BinaryReader reader)
        {
            _mountedDrivePaths.Clear();
            int count = reader.ReadInt32();
            for (int index = 0; index < count; index++)
            {
                int drive = reader.ReadInt32();
                string path = BinaryStateIO.ReadString(reader);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _mountedDrivePaths[drive] = path;
                }
            }
        }

        /// <summary>
        /// Writes currently pressed host keys.
        /// </summary>
        private void WritePressedHostKeys(BinaryWriter writer)
        {
            writer.Write(_pressedHostKeys.Count);
            foreach (Key key in _pressedHostKeys)
            {
                writer.Write((int)key);
            }
        }

        /// <summary>
        /// Reads currently pressed host keys.
        /// </summary>
        private void ReadPressedHostKeys(BinaryReader reader)
        {
            _pressedHostKeys.Clear();
            int count = reader.ReadInt32();
            for (int index = 0; index < count; index++)
            {
                _pressedHostKeys.Add((Key)reader.ReadInt32());
            }
        }

        /// <summary>
        /// Handles the advance drive clock operation.
        /// </summary>
        private void AdvanceDriveClock()
        {
            // The 1541 has its own 1 MHz clock. On a PAL C64 the computer runs
            // at roughly 0.985 MHz, so custom bit-banged loaders expect the
            // drive CPU to advance slightly faster than the C64 CPU.
            _drive1541TargetCycles += 1000000.0 / _model.CpuHz;
        }

        /// <summary>
        /// Runs the drives to current time routine.
        /// </summary>
        private bool RunDrivesToCurrentTime()
        {
            if (!AnyDriveNeedsClockTick())
            {
                _drive1541ExecutedCycles = _drive1541TargetCycles;
                return false;
            }

            bool tickedDrive = false;
            while (_drive1541ExecutedCycles + 1.0 <= _drive1541TargetCycles)
            {
                TickAllDrivesOnce();
                tickedDrive = true;
                _drive1541ExecutedCycles += 1.0;
            }

            return tickedDrive;
        }

        /// <summary>
        /// Advances drive activity indicators while their emulated CPUs are parked.
        /// </summary>
        private void AdvanceIdleDriveVisualState(int c64Cycles)
        {
            if (c64Cycles <= 0)
            {
                return;
            }

            int driveCycles = Math.Max(1, (int)Math.Ceiling(c64Cycles * 1000000.0 / _model.CpuHz));
            _drive8.AdvanceIdleVisualState(driveCycles);
            _drive9.AdvanceIdleVisualState(driveCycles);
            _drive10.AdvanceIdleVisualState(driveCycles);
            _drive11.AdvanceIdleVisualState(driveCycles);
        }

        /// <summary>
        /// Handles the any drive needs clock tick operation.
        /// </summary>
        private bool AnyDriveNeedsClockTick()
        {
            return _drive8.NeedsClockTick ||
                _drive9.NeedsClockTick ||
                _drive10.NeedsClockTick ||
                _drive11.NeedsClockTick;
        }

        /// <summary>
        /// Advances the all drives once state by one emulated tick.
        /// </summary>
        private void TickAllDrivesOnce()
        {
            if (_drive8.NeedsClockTick)
            {
                _drive8.Tick();
            }

            if (_drive9.NeedsClockTick)
            {
                _drive9.Tick();
            }

            if (_drive10.NeedsClockTick)
            {
                _drive10.Tick();
            }

            if (_drive11.NeedsClockTick)
            {
                _drive11.Tick();
            }
        }

        /// <summary>
        /// Handles the catch up drives for iec port access operation.
        /// </summary>
        private void CatchUpDrivesForIecPortAccess()
        {
            if (_catchingUpDrivesForIecAccess)
            {
                return;
            }

            _catchingUpDrivesForIecAccess = true;
            try
            {
                RunDrivesToCurrentTime();
            }
            finally
            {
                _catchingUpDrivesForIecAccess = false;
            }
        }

        /// <summary>
        /// Advances the core state by one emulated tick.
        /// </summary>
        private void TickCore()
        {
            AdvanceDriveClock();
            _vic.PrepareCycle();
            Phi2BusState phi2BusState = ComputePhi2BusState();
            _bus.SetPhi2BusState(
                phi2BusState.BaLow,
                phi2BusState.AecLow,
                phi2BusState.CpuCanAccess,
                phi2BusState.VicCanAccess);
            _cpu.Tick();
            _cia1.Tick();
            _cia2.Tick();
            _sid.Tick();
            MirrorHeldHostInputIntoKeyboardBuffer();
            _vic.FinishCycle();
            if (AnyDriveNeedsClockTick())
            {
                RunDrivesToCurrentTime();
            }
        }

        /// <summary>
        /// Loads program bytes.
        /// </summary>
        private void LoadProgramBytes(byte[] programBytes)
        {
            ushort loadAddress;
            ushort endAddress;
            if (!PrgLoader.TryLoadIntoMemory(_bus, programBytes, out loadAddress, out endAddress))
            {
                return;
            }

            _bus.WriteRam(0x0090, 0x00);
        }

        /// <summary>
        /// Mounts disk to drive.
        /// </summary>
        private string MountDiskToDrive(string path, int deviceNumber, bool updateLastMountedMedia)
        {
            IecDrive1541 drive = GetDrive(deviceNumber);
            if (drive == null)
            {
                return "INVALID DRIVE";
            }

            D64Image mountedDiskImage;
            try
            {
                mountedDiskImage = D64Image.Load(path);
            }
            catch (Exception)
            {
                return "INVALID D64";
            }

            drive.MountDisk(mountedDiskImage);
            _mountedDrivePaths[deviceNumber] = path;

            if (updateLastMountedMedia)
            {
                string displayName = string.IsNullOrWhiteSpace(mountedDiskImage.DiskName)
                    ? Path.GetFileNameWithoutExtension(path)
                    : mountedDiskImage.DiskName;
                _lastMountedMedia = new MountedMediaInfo(MountedMediaKind.D64, "D" + deviceNumber, displayName, path);
            }

            return "DISK MOUNTED IN DRIVE " + deviceNumber;
        }

        /// <summary>
        /// Gets the drive value.
        /// </summary>
        private IecDrive1541 GetDrive(int deviceNumber)
        {
            switch (deviceNumber)
            {
                case 8:
                    return _drive8;
                case 9:
                    return _drive9;
                case 10:
                    return _drive10;
                case 11:
                    return _drive11;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Applies iec transport mode.
        /// </summary>
        private void ApplyIecTransportMode()
        {
            // Keep standard CBM-DOS LOADs on the drive-side IEC/DOS transport.
            // This still goes over the IEC bus and no longer uses KERNAL hooks;
            // uploaded/custom drive code hands over to the hardware/ROM side
            // explicitly once the loader requests it.
            bool forceSoftwareTransport = _forceSoftwareIecTransport;
            _drive8.ForceSoftwareTransport = forceSoftwareTransport;
            _drive9.ForceSoftwareTransport = forceSoftwareTransport;
            _drive10.ForceSoftwareTransport = forceSoftwareTransport;
            _drive11.ForceSoftwareTransport = forceSoftwareTransport;
            bool useKernalBridgeTransport = _cpu.EnableKernalIecHooks;
            _drive8.UseKernalBridgeTransport = useKernalBridgeTransport;
            _drive9.UseKernalBridgeTransport = useKernalBridgeTransport;
            _drive10.UseKernalBridgeTransport = useKernalBridgeTransport;
            _drive11.UseKernalBridgeTransport = useKernalBridgeTransport;
            // Keep passively observing the IEC bus even while the bridged
            // KERNAL serial path is active. Standard LOADs still use the
            // bridged transport for now, but many cracked loaders switch to
            // direct CIA2 bit-banging after RUN. If we disable passive
            // observation while hooks are enabled, the drive never sees those
            // post-loader commands and cannot arm uploaded/custom drive code.
            bool observeExternalIecTraffic = true;
            _drive8.ObserveExternalIecTraffic = observeExternalIecTraffic;
            _drive9.ObserveExternalIecTraffic = observeExternalIecTraffic;
            _drive10.ObserveExternalIecTraffic = observeExternalIecTraffic;
            _drive11.ObserveExternalIecTraffic = observeExternalIecTraffic;
        }

        /// <summary>
        /// Returns whether the component can start drive custom execution.
        /// </summary>
        private bool CanStartDriveCustomExecution()
        {
            // Do not let uploaded drive code take over while the C64 side is
            // still inside the KERNAL IEC command path. Real loaders return
            // from CLOSE/UNLISTEN first, then their RAM-side bit-banged serial
            // loop performs the first DATA/CLOCK handshake.
            if (_cpu.State != CpuState.FetchOpcode)
            {
                return false;
            }

            if (_cpu.PC >= 0xE000 || _cpu.LastOpcodeAddress >= 0xE000)
            {
                return false;
            }

            return !HasImminentKernalCall(_cpu.PC);
        }

        /// <summary>
        /// Returns whether imminent kernal call is available or active.
        /// </summary>
        private bool HasImminentKernalCall(ushort startAddress)
        {
            // A few fastloaders queue U3/M-E, return to their own RAM stub, and
            // then immediately call KERNAL CLRCHN/CLOSE before switching to the
            // bit-banged protocol. If the drive jumps into custom code in that
            // tiny window, the remaining KERNAL call sees no standard listener
            // and sets ST=$80. Delay the handoff while a nearby JSR/JMP targets
            // the KERNAL ROM.
            const int scanBytes = 8;
            for (int offset = 0; offset < scanBytes; offset++)
            {
                ushort opcodeAddress = (ushort)(startAddress + offset);
                byte opcode = _bus.ReadRam(opcodeAddress);
                if (opcode != 0x20 && opcode != 0x4C)
                {
                    continue;
                }

                ushort operandLowAddress = (ushort)(opcodeAddress + 1);
                ushort operandHighAddress = (ushort)(opcodeAddress + 2);
                ushort target = (ushort)(_bus.ReadRam(operandLowAddress) | (_bus.ReadRam(operandHighAddress) << 8));
                if (target >= 0xE000)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns whether the component can start drive rom transport.
        /// </summary>
        private bool CanStartDriveRomTransport()
        {
            return false;
        }

        /// <summary>
        /// Handles the mirror held host input into keyboard buffer operation.
        /// </summary>
        private void MirrorHeldHostInputIntoKeyboardBuffer()
        {
            if (!CanMirrorHostInputIntoKeyboardBuffer())
            {
                _lastMirroredPollKey = null;
                _pollMirrorRepeatDelayCycles = 0;
                return;
            }

            const ushort KeyboardBufferCountAddress = 0x00C6;
            const int RepeatDelayCycles = 25000;

            if (_bus.CpuRead(KeyboardBufferCountAddress) != 0)
            {
                return;
            }

            Key heldKey;
            byte petscii;
            if (!TryGetHeldPollKey(out heldKey, out petscii))
            {
                _lastMirroredPollKey = null;
                _pollMirrorRepeatDelayCycles = 0;
                return;
            }

            if (_lastMirroredPollKey.HasValue && _lastMirroredPollKey.Value == heldKey)
            {
                if (_pollMirrorRepeatDelayCycles > 0)
                {
                    _pollMirrorRepeatDelayCycles--;
                    return;
                }
            }

            EnqueuePetsciiByte(petscii);
            _lastMirroredPollKey = heldKey;
            _pollMirrorRepeatDelayCycles = RepeatDelayCycles;
        }

        /// <summary>
        /// Attempts to get held poll key and reports whether it succeeded.
        /// </summary>
        private bool TryGetHeldPollKey(out Key key, out byte petscii)
        {
            Key[] priority =
            {
                Key.Number1,
                Key.Number2,
                Key.Space,
                Key.Enter,
                Key.BackSpace
            };

            for (int index = 0; index < priority.Length; index++)
            {
                Key candidate = priority[index];
                if (_pressedHostKeys.Contains(candidate) && TryMapHostKeyToPetscii(candidate, out petscii))
                {
                    key = candidate;
                    return true;
                }
            }

            key = default(Key);
            petscii = 0x00;
            return false;
        }

        /// <summary>
        /// Enqueues petscii byte.
        /// </summary>
        private void EnqueuePetsciiByte(byte petscii)
        {
            const ushort KeyboardBufferCountAddress = 0x00C6;
            const ushort KeyboardBufferStartAddress = 0x0277;
            const int KeyboardBufferCapacity = 10;

            byte count = _bus.CpuRead(KeyboardBufferCountAddress);
            if (count >= KeyboardBufferCapacity)
            {
                return;
            }

            _bus.WriteRam((ushort)(KeyboardBufferStartAddress + count), petscii);
            _bus.WriteRam(KeyboardBufferCountAddress, (byte)(count + 1));
        }

        /// <summary>
        /// Attempts to enqueue host key to keyboard buffer and reports whether it succeeded.
        /// </summary>
        private void TryEnqueueHostKeyToKeyboardBuffer(Key key)
        {
            // Some cracked intros poll GETIN while they have IRQs disabled.
            // In that situation the normal KERNAL keyboard scan does not refill
            // the keyboard buffer, so host key presses would otherwise be
            // invisible even though the program is explicitly waiting for a key.
            if (!CanMirrorHostInputIntoKeyboardBuffer() || !IsBufferOnlyHotkey(key))
            {
                return;
            }

            byte petscii;
            if (!TryMapHostKeyToPetscii(key, out petscii))
            {
                return;
            }

            EnqueuePetsciiByte(petscii);
        }

        /// <summary>
        /// Returns whether buffer only hotkey is true.
        /// </summary>
        private static bool IsBufferOnlyHotkey(Key key)
        {
            return key == Key.Space ||
                key == Key.Enter ||
                key == Key.BackSpace ||
                key == Key.Number1 ||
                key == Key.Number2;
        }

        /// <summary>
        /// Returns whether the component can mirror host input into keyboard buffer.
        /// </summary>
        private bool CanMirrorHostInputIntoKeyboardBuffer()
        {
            return _enableInputInjection && (_cpu.SR & 0x04) != 0 && IsLikelyIntroKeyboardPollLoop();
        }

        /// <summary>
        /// Returns whether likely intro keyboard poll loop is true.
        /// </summary>
        private bool IsLikelyIntroKeyboardPollLoop()
        {
            ushort pc = _cpu.PC;

            // Keep this host-side fallback away from the normal KERNAL/BASIC
            // input loops. Otherwise held printable keys are injected into the
            // BASIC keyboard buffer repeatedly in addition to the CIA matrix.
            return pc >= 0xC020 && pc <= 0xC02D;
        }

        /// <summary>
        /// Attempts to map host key to petscii and reports whether it succeeded.
        /// </summary>
        private static bool TryMapHostKeyToPetscii(Key key, out byte petscii)
        {
            petscii = 0x00;

            switch (key)
            {
                case Key.A:
                    petscii = (byte)'A';
                    return true;
                case Key.B:
                    petscii = (byte)'B';
                    return true;
                case Key.C:
                    petscii = (byte)'C';
                    return true;
                case Key.D:
                    petscii = (byte)'D';
                    return true;
                case Key.E:
                    petscii = (byte)'E';
                    return true;
                case Key.F:
                    petscii = (byte)'F';
                    return true;
                case Key.G:
                    petscii = (byte)'G';
                    return true;
                case Key.H:
                    petscii = (byte)'H';
                    return true;
                case Key.I:
                    petscii = (byte)'I';
                    return true;
                case Key.J:
                    petscii = (byte)'J';
                    return true;
                case Key.K:
                    petscii = (byte)'K';
                    return true;
                case Key.L:
                    petscii = (byte)'L';
                    return true;
                case Key.M:
                    petscii = (byte)'M';
                    return true;
                case Key.N:
                    petscii = (byte)'N';
                    return true;
                case Key.O:
                    petscii = (byte)'O';
                    return true;
                case Key.P:
                    petscii = (byte)'P';
                    return true;
                case Key.Q:
                    petscii = (byte)'Q';
                    return true;
                case Key.R:
                    petscii = (byte)'R';
                    return true;
                case Key.S:
                    petscii = (byte)'S';
                    return true;
                case Key.T:
                    petscii = (byte)'T';
                    return true;
                case Key.U:
                    petscii = (byte)'U';
                    return true;
                case Key.V:
                    petscii = (byte)'V';
                    return true;
                case Key.W:
                    petscii = (byte)'W';
                    return true;
                case Key.X:
                    petscii = (byte)'X';
                    return true;
                case Key.Y:
                    petscii = (byte)'Y';
                    return true;
                case Key.Z:
                    petscii = (byte)'Z';
                    return true;
                case Key.Number0:
                    petscii = (byte)'0';
                    return true;
                case Key.Number1:
                    petscii = (byte)'1';
                    return true;
                case Key.Number2:
                    petscii = (byte)'2';
                    return true;
                case Key.Number3:
                    petscii = (byte)'3';
                    return true;
                case Key.Number4:
                    petscii = (byte)'4';
                    return true;
                case Key.Number5:
                    petscii = (byte)'5';
                    return true;
                case Key.Number6:
                    petscii = (byte)'6';
                    return true;
                case Key.Number7:
                    petscii = (byte)'7';
                    return true;
                case Key.Number8:
                    petscii = (byte)'8';
                    return true;
                case Key.Number9:
                    petscii = (byte)'9';
                    return true;
                case Key.ControlLeft:
                    petscii = 0x20;
                    return true;
                case Key.Space:
                    petscii = 0x20;
                    return true;
                case Key.Enter:
                    petscii = 0x0D;
                    return true;
                case Key.BackSpace:
                    petscii = 0x14;
                    return true;
                case Key.Comma:
                    petscii = (byte)',';
                    return true;
                case Key.Period:
                    petscii = (byte)'.';
                    return true;
                case Key.Minus:
                    petscii = (byte)'-';
                    return true;
                case Key.Slash:
                    petscii = (byte)'/';
                    return true;
                case Key.Quote:
                    petscii = (byte)'"';
                    return true;
                case Key.Semicolon:
                    petscii = (byte)';';
                    return true;
                case Key.Plus:
                    petscii = (byte)'=';
                    return true;
                default:
                    return false;
            }
        }
    }
}
