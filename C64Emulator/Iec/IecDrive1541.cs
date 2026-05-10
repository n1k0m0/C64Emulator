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
using System.Text;

namespace C64Emulator.Core
{
    /// <summary>
    /// Implements the software IEC side of an emulated Commodore 1541 disk drive.
    /// </summary>
    public sealed class IecDrive1541
    {
        /// <summary>
        /// Represents the drive channel component.
        /// </summary>
        private sealed class DriveChannel
        {
            public byte[] FilenameBytes = Array.Empty<byte>();
            public byte[] ReadBuffer = Array.Empty<byte>();
            public int ReadOffset;
            public D64Image.SequentialFileReader SequentialReader;
            public List<byte> WriteBuffer;

            /// <summary>
            /// Handles the reset read source operation.
            /// </summary>
            public void ResetReadSource()
            {
                ReadBuffer = Array.Empty<byte>();
                ReadOffset = 0;
                SequentialReader = null;
            }

            /// <summary>
            /// Handles the reset all operation.
            /// </summary>
            public void ResetAll()
            {
                FilenameBytes = Array.Empty<byte>();
                ResetReadSource();
                WriteBuffer = null;
            }

            /// <summary>
            /// Writes the complete drive channel state into a savestate stream.
            /// </summary>
            public void SaveState(BinaryWriter writer)
            {
                BinaryStateIO.WriteByteArray(writer, FilenameBytes);
                BinaryStateIO.WriteByteArray(writer, ReadBuffer);
                writer.Write(ReadOffset);
                writer.Write(SequentialReader != null);
                if (SequentialReader != null)
                {
                    SequentialReader.SaveState(writer);
                }

                BinaryStateIO.WriteByteList(writer, WriteBuffer);
            }

            /// <summary>
            /// Restores the complete drive channel state from a savestate stream.
            /// </summary>
            public void LoadState(BinaryReader reader, D64Image image)
            {
                FilenameBytes = BinaryStateIO.ReadByteArray(reader) ?? Array.Empty<byte>();
                ReadBuffer = BinaryStateIO.ReadByteArray(reader) ?? Array.Empty<byte>();
                ReadOffset = reader.ReadInt32();
                bool hasSequentialReader = reader.ReadBoolean();
                SequentialReader = hasSequentialReader && image != null
                    ? D64Image.SequentialFileReader.LoadState(image, reader)
                    : null;
                if (hasSequentialReader && image == null)
                {
                    D64Image.SequentialFileReader.SkipState(reader);
                }

                WriteBuffer = BinaryStateIO.ReadByteList(reader);
            }
        }

        /// <summary>
        /// Represents the command result component.
        /// </summary>
        public sealed class CommandResult
        {
            /// <summary>
            /// Initializes a new CommandResult instance.
            /// </summary>
            public CommandResult(byte status, string statusText, byte[] readBuffer = null)
            {
                Status = status;
                StatusText = statusText ?? "00, OK,00,00";
                ReadBuffer = readBuffer ?? Array.Empty<byte>();
            }

            /// <summary>
            /// Gets the status byte.
            /// </summary>
            public byte Status { get; }

            /// <summary>
            /// Gets the current drive status text.
            /// </summary>
            public string StatusText { get; }

            /// <summary>
            /// Gets the current channel read buffer.
            /// </summary>
            public byte[] ReadBuffer { get; }
        }

        private const byte ListenBase = 0x20;
        private const byte Unlisten = 0x3F;
        private const byte TalkBase = 0x40;
        private const byte Untalk = 0x5F;
        private const byte SecondaryBase = 0x60;
        private const byte CloseBase = 0xE0;
        private const byte OpenBase = 0xF0;

        private const int EoiThresholdCycles = 140;
        private const int EoiAcknowledgeHoldCycles = 48;
        private const int ByteAcknowledgeHoldCycles = 32;
        private const int PayloadInitialByteGraceCycles = 0;
        private const int TalkerInterByteDelayCycles = 24;
        // Keep the software IEC talker close to the timings used by Pi1541/VICE.
        // The earlier, much tighter timings were fast enough for synthetic tests,
        // but the C64 KERNAL listener can miss or mis-time end-of-file handling
        // when the VIC steals cycles. In practice that showed up as LOAD"*",8
        // stalling at the final EOI byte. These values bias toward reliability
        // over speed and leave enough margin for badlines.
        private const int TalkerTurnaroundReleaseCycles = 50;
        private const int TalkerPreambleLowCycles = 40;
        private const int TalkerPreambleSettleCycles = 21;
        private const int TalkerBitPrepareCycles = 45;
        private const int TalkerBitSetupCycles = 22;
        private const int TalkerBitValidCycles = 75;
        private const int TalkerBitClockLowHoldCycles = 22;
        private const int TalkerBitRecoveryCycles = 14;
        private const int ByteAcknowledgeTimeoutCycles = 100000;
        private const int FinalByteAcknowledgeHoldCycles = 80;
        private const int PayloadImplicitEndCycles = 16000;
        private const int PostEoiReadyHoldCycles = 480;
        private const int PresenceProbeReadyHoldCycles = 200000;
        private const int AttentionPostReleaseCommandTimeoutCycles = 100000;
        private const int TalkChunkSize = 256;
        private const int CustomExecutionArmIdleCycles = 128;
        private const int CustomExecutionFallbackStartCycles = 4096;
        private const int CustomExecutionAtnReleaseTimeoutCycles = 4096;

        private readonly int _deviceNumber;
        private readonly IecBusPort _port;
        private readonly IecBusPort _hardwarePort;
        private readonly IecByteReceiver _receiver;
        private readonly IecByteSender _sender;
        private readonly IecByteReceiver _passiveReceiver;
        private readonly Dictionary<byte, DriveChannel> _channels = new Dictionary<byte, DriveChannel>();
        private readonly List<byte> _listenBuffer = new List<byte>();
        private readonly List<byte> _recentAttentionCommands = new List<byte>();
        private readonly List<string> _recentCommandTexts = new List<string>();
        private readonly byte[] _ram = new byte[0x0800];
        private readonly Drive1541Hardware _hardware;
        private const int LedBlinkHalfPeriodCycles = 30000;
        private const int LedCommandPulseCycles = 120000;
        private const int LedMinTransferCycles = 180000;
        private const int LedMaxTransferCycles = 1200000;
        private const int LedTransferCyclesPerByte = 96;

        private D64Image _mountedImage;
        private bool _lastAtnLow;
        private bool _passiveLastAtnLow;
        private bool _deviceAttentioned;
        private bool _deviceListening;
        private bool _deviceTalking;
        private bool _passiveListening;
        private bool _passiveTalking;
        private bool _payloadReceiveArmed;
        private bool _payloadSendArmed;
        private bool _armPayloadReceiveAfterAttention;
        private bool _armPayloadSendAfterAttention;
        private bool _passivePayloadReceiveArmed;
        private bool _passiveArmPayloadReceiveAfterAttention;
        private byte _channel;
        private byte _passiveChannel;
        private byte _listeningSecondaryAddress;
        private byte _talkSecondaryAddress;
        private byte[] _talkBuffer = Array.Empty<byte>();
        private int _talkBufferIndex;
        private ushort _lastExecuteAddress;
        private string _lastCommandText = string.Empty;
        private byte _statusCode;
        private string _statusText = "73, CBM DOS V2.6 1541,00,00";
        private int _memoryWriteCommandCount;
        private ushort _lastMemoryWriteAddress;
        private int _lastMemoryWriteLength;
        private PendingCustomExecutionType _pendingCustomExecution;
        private ushort _pendingExecuteAddress;
        private byte _pendingUserCommand;
        private bool _pendingCustomExecutionArmed;
        private bool _pendingCustomExecutionWaitForAtnRelease;
        private int _pendingCustomExecutionIdleCycles;
        private int _pendingCustomExecutionWaitCycles;
        private int _ledBusyCycles;
        private int _ledPhaseCycles;
        private int _postPayloadReadyHoldCycles;
        private int _presenceProbeHoldCycles;
        private AttentionHandshakeState _attentionHandshakeState;
        private bool _forceSoftwareTransport = true;
        private bool _observeExternalIecTraffic;
        private bool _useKernalBridgeTransport;
        private bool _bridgeLowLevelSessionActive;
        private bool _softwareClockLineLow;
        private bool _softwareDataLineLow;
        private int _atnLowTransitions;
        private int _atnHighTransitions;
        private int _attentionBeginCount;
        private int _attentionEndCount;
        private int _attentionCommandCount;
        private bool _attentionSawCommand;
        private bool _presenceProbeHoldActive;
        private bool _presenceProbeObservedClockLow;
        private bool _continueAttentionCommandAfterRelease;
        private int _attentionPostReleaseWaitCycles;

        /// <summary>
        /// Gets or sets the gate that allows custom drive code to run.
        /// </summary>
        public Func<bool> CustomExecutionStartGate { get; set; }

        /// <summary>
        /// Gets or sets the gate that allows standard ROM transport to run.
        /// </summary>
        public Func<bool> StandardRomTransportStartGate { get; set; }

        /// <summary>
        /// Handles the iec drive1541 operation.
        /// </summary>
        public IecDrive1541(int deviceNumber, IecBusPort port, IecBusPort hardwarePort)
        {
            _deviceNumber = deviceNumber;
            _port = port;
            _hardwarePort = hardwarePort;
            _port.RegisterLineChangeListener(HandleIecLineChanged);
            _receiver = new IecByteReceiver(_port);
            _sender = new IecByteSender(_port);
            _passiveReceiver = new IecByteReceiver(_port, false);
            _hardware = new Drive1541Hardware(_hardwarePort, deviceNumber);
            Reset();
        }

        public byte DeviceNumber
        {
            get { return (byte)_deviceNumber; }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            _lastAtnLow = false;
            _passiveLastAtnLow = false;
            _deviceAttentioned = false;
            _deviceListening = false;
            _deviceTalking = false;
            _passiveListening = false;
            _passiveTalking = false;
            _payloadReceiveArmed = false;
            _payloadSendArmed = false;
            _armPayloadReceiveAfterAttention = false;
            _armPayloadSendAfterAttention = false;
            _passivePayloadReceiveArmed = false;
            _passiveArmPayloadReceiveAfterAttention = false;
            _channel = 0;
            _passiveChannel = 0;
            _listeningSecondaryAddress = 0;
            _talkSecondaryAddress = 0;
            _talkBuffer = Array.Empty<byte>();
            _talkBufferIndex = 0;
            ResetChannels();
            _listenBuffer.Clear();
            _recentAttentionCommands.Clear();
            Array.Clear(_ram, 0, _ram.Length);
            _receiver.Reset();
            _sender.Reset();
            _passiveReceiver.Reset();
            ReleaseLines();
            _lastExecuteAddress = 0;
            _lastCommandText = string.Empty;
            _recentCommandTexts.Clear();
            _memoryWriteCommandCount = 0;
            _lastMemoryWriteAddress = 0;
            _lastMemoryWriteLength = 0;
            _statusCode = 0x00;
            _statusText = "73, CBM DOS V2.6 1541,00,00";
            _pendingCustomExecution = PendingCustomExecutionType.None;
            _pendingExecuteAddress = 0;
            _pendingUserCommand = 0;
            _pendingCustomExecutionArmed = false;
            _pendingCustomExecutionWaitForAtnRelease = false;
            _pendingCustomExecutionIdleCycles = 0;
            _pendingCustomExecutionWaitCycles = 0;
            _ledBusyCycles = 0;
            _ledPhaseCycles = 0;
            _postPayloadReadyHoldCycles = 0;
            _presenceProbeHoldCycles = 0;
            _attentionHandshakeState = AttentionHandshakeState.Idle;
            _atnLowTransitions = 0;
            _atnHighTransitions = 0;
            _attentionBeginCount = 0;
            _attentionEndCount = 0;
            _attentionCommandCount = 0;
            _attentionSawCommand = false;
            _presenceProbeHoldActive = false;
            _presenceProbeObservedClockLow = false;
            _continueAttentionCommandAfterRelease = false;
            _attentionPostReleaseWaitCycles = 0;
            _hardware.Reset();
            _hardware.MountDisk(_mountedImage);
        }

        /// <summary>
        /// Mounts disk.
        /// </summary>
        public void MountDisk(D64Image image)
        {
            _mountedImage = image;
            if (_hardware.HasCustomCodeActive)
            {
                // A physical disk swap does not reset the 1541. Fast loaders
                // such as Maniac Mansion keep their uploaded drive code in RAM
                // while asking for the next disk side.
                _hardware.MountDisk(_mountedImage);
                return;
            }

            Reset();
        }

        /// <summary>
        /// Ejects disk.
        /// </summary>
        public void EjectDisk()
        {
            _mountedImage = null;
            if (_hardware.HasCustomCodeActive)
            {
                _hardware.EjectDisk();
                return;
            }

            Reset();
        }

        /// <summary>
        /// Writes the complete IEC drive state into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_mountedImage != null);
            if (_mountedImage != null)
            {
                _mountedImage.SaveState(writer);
            }

            StateSerializer.WriteObjectFields(
                writer,
                this,
                "_port",
                "_hardwarePort",
                "_receiver",
                "_sender",
                "_passiveReceiver",
                "_channels",
                "_listenBuffer",
                "_recentAttentionCommands",
                "_recentCommandTexts",
                "_ram",
                "_hardware",
                "_mountedImage");
            BinaryStateIO.WriteByteArray(writer, _ram);
            BinaryStateIO.WriteByteList(writer, _listenBuffer);
            BinaryStateIO.WriteByteList(writer, _recentAttentionCommands);
            BinaryStateIO.WriteStringList(writer, _recentCommandTexts);
            writer.Write(_channels.Count);
            foreach (KeyValuePair<byte, DriveChannel> channel in _channels)
            {
                writer.Write(channel.Key);
                channel.Value.SaveState(writer);
            }

            _receiver.SaveState(writer);
            _sender.SaveState(writer);
            _passiveReceiver.SaveState(writer);
            _hardware.SaveState(writer);
        }

        /// <summary>
        /// Restores the complete IEC drive state from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            _mountedImage = reader.ReadBoolean() ? D64Image.LoadState(reader) : null;
            StateSerializer.ReadObjectFields(
                reader,
                this,
                "_port",
                "_hardwarePort",
                "_receiver",
                "_sender",
                "_passiveReceiver",
                "_channels",
                "_listenBuffer",
                "_recentAttentionCommands",
                "_recentCommandTexts",
                "_ram",
                "_hardware",
                "_mountedImage");
            byte[] ram = BinaryStateIO.ReadByteArray(reader);
            if (ram != null)
            {
                Array.Copy(ram, _ram, Math.Min(ram.Length, _ram.Length));
            }

            ReplaceByteList(_listenBuffer, BinaryStateIO.ReadByteList(reader));
            ReplaceByteList(_recentAttentionCommands, BinaryStateIO.ReadByteList(reader));
            ReplaceStringList(_recentCommandTexts, BinaryStateIO.ReadStringList(reader));
            _channels.Clear();
            int channelCount = reader.ReadInt32();
            for (int index = 0; index < channelCount; index++)
            {
                byte channelNumber = reader.ReadByte();
                var channel = new DriveChannel();
                channel.LoadState(reader, _mountedImage);
                _channels[channelNumber] = channel;
            }

            _receiver.LoadState(reader);
            _sender.LoadState(reader);
            _passiveReceiver.LoadState(reader);
            _hardware.LoadState(reader, _mountedImage);
        }

        public bool IsMounted
        {
            get { return _mountedImage != null; }
        }

        /// <summary>
        /// Replaces a readonly byte list's contents after a savestate restore.
        /// </summary>
        private static void ReplaceByteList(List<byte> target, List<byte> source)
        {
            target.Clear();
            if (source == null)
            {
                return;
            }

            for (int index = 0; index < source.Count; index++)
            {
                target.Add(source[index]);
            }
        }

        /// <summary>
        /// Replaces a readonly string list's contents after a savestate restore.
        /// </summary>
        private static void ReplaceStringList(List<string> target, List<string> source)
        {
            target.Clear();
            if (source == null)
            {
                return;
            }

            for (int index = 0; index < source.Count; index++)
            {
                target.Add(source[index]);
            }
        }

        public bool IsLedOn
        {
            get
            {
                if (_mountedImage == null)
                {
                    return false;
                }

                if (_hardware.IsLedOn)
                {
                    return true;
                }

                return HasVisibleActivity && (((_ledPhaseCycles / LedBlinkHalfPeriodCycles) & 0x01) == 0);
            }
        }

        public bool IsActivityActive
        {
            get { return _mountedImage != null && HasVisibleActivity; }
        }

        public bool NeedsClockTick
        {
            get { return _mountedImage != null && HasCycleCriticalWork; }
        }

        public bool HasCustomCodeActive
        {
            get { return _hardware.HasCustomCodeActive; }
        }

        public bool IsHardwareTransportReady
        {
            get
            {
                if (_hardware.HasCustomCodeActive)
                {
                    // As soon as uploaded/custom drive code is running, the
                    // high-level software IEC state machine must get fully out
                    // of the way. The custom loader may begin by only reading
                    // current IEC/VIA state before it performs its first port
                    // write, so waiting for "serial outputs enabled" here lets
                    // the old software transport keep consuming ATN/command
                    // traffic and breaks the handover after M-W/U3. The drive
                    // hardware continues to tick and observe the IEC inputs,
                    // so treat custom-code mode itself as the ownership switch.
                    return true;
                }

                if (!_forceSoftwareTransport)
                {
                    return _hardware.IsSerialTransportActive;
                }

                return CanStandardRomTransportTakeBus() &&
                    _hardware.IsSerialTransportActive;
            }
        }

        public bool ForceSoftwareTransport
        {
            get { return _forceSoftwareTransport; }
            set
            {
                _forceSoftwareTransport = value;
                if (_forceSoftwareTransport && !_hardware.HasCustomCodeActive)
                {
                    UpdateHardwareSerialTransportMode();
                    _receiver.Reset();
                    _sender.Reset();
                    _attentionHandshakeState = AttentionHandshakeState.Idle;
                    _postPayloadReadyHoldCycles = 0;
                }
            }
        }

        public bool ObserveExternalIecTraffic
        {
            get { return _observeExternalIecTraffic; }
            set
            {
                _observeExternalIecTraffic = value;
                if (!_observeExternalIecTraffic)
                {
                    ResetPassiveObserverState();
                }
            }
        }

        public bool UseKernalBridgeTransport
        {
            get { return _useKernalBridgeTransport; }
            set
            {
                _useKernalBridgeTransport = value;
                if (_useKernalBridgeTransport && _bridgeLowLevelSessionActive && !_hardware.HasCustomCodeActive)
                {
                    ResetSoftwareTransportStateForBridge();
                    ResetPassiveObserverState();
                }
            }
        }

        public bool RunHardwareContinuously
        {
            get { return _hardware.RunCpuContinuously; }
            set { _hardware.RunCpuContinuously = value; }
        }

        public bool BridgeLowLevelSessionActive
        {
            get { return _bridgeLowLevelSessionActive; }
            set
            {
                _bridgeLowLevelSessionActive = value;
                if (_bridgeLowLevelSessionActive && _useKernalBridgeTransport && !_hardware.HasCustomCodeActive)
                {
                    ResetSoftwareTransportStateForBridge();
                    ResetPassiveObserverState();
                }
                else if (!_bridgeLowLevelSessionActive)
                {
                    ResetPassiveObserverState();
                }
            }
        }

        private bool HasVisibleActivity
        {
            get
            {
                return _ledBusyCycles > 0 ||
                    HasCycleCriticalWork ||
                    _hardware.IsMotorOn ||
                    _hardware.IsLedOn;
            }
        }

        private bool HasCycleCriticalWork
        {
            get
            {
                if (_presenceProbeHoldActive ||
                    _attentionHandshakeState != AttentionHandshakeState.Idle ||
                    _deviceListening ||
                    _deviceTalking ||
                    _payloadReceiveArmed ||
                    _payloadSendArmed ||
                    _armPayloadReceiveAfterAttention ||
                    _armPayloadSendAfterAttention ||
                    _pendingCustomExecution != PendingCustomExecutionType.None ||
                    _pendingCustomExecutionArmed ||
                    _pendingCustomExecutionWaitForAtnRelease ||
                    !_receiver.IsIdle ||
                    !_sender.IsIdle ||
                    _hardware.IsBooting)
                {
                    return true;
                }

                if (_hardware.HasCustomCodeActive)
                {
                    return true;
                }

                if (_hardware.IsSerialTransportActive)
                {
                    return IsIecBusActiveForDrive();
                }

                return false;
            }
        }

        public string LastCommandText
        {
            get { return _lastCommandText; }
        }

        public ushort LastExecuteAddress
        {
            get { return _lastExecuteAddress; }
        }

        public Drive1541Hardware Hardware
        {
            get { return _hardware; }
        }

        /// <summary>
        /// Gets the or create channel value.
        /// </summary>
        private DriveChannel GetOrCreateChannel(byte channel)
        {
            DriveChannel state;
            if (!_channels.TryGetValue(channel, out state))
            {
                state = new DriveChannel();
                _channels[channel] = state;
            }

            return state;
        }

        /// <summary>
        /// Attempts to get channel and reports whether it succeeded.
        /// </summary>
        private bool TryGetChannel(byte channel, out DriveChannel state)
        {
            return _channels.TryGetValue(channel, out state);
        }

        /// <summary>
        /// Handles the remove channel operation.
        /// </summary>
        private void RemoveChannel(byte channel)
        {
            _channels.Remove(channel);
        }

        /// <summary>
        /// Handles the reset channels operation.
        /// </summary>
        private void ResetChannels()
        {
            _channels.Clear();
        }

        /// <summary>
        /// Sets the channel read buffer value.
        /// </summary>
        private void SetChannelReadBuffer(byte channel, byte[] data)
        {
            DriveChannel state = GetOrCreateChannel(channel);
            state.ResetReadSource();
            state.ReadBuffer = data ?? Array.Empty<byte>();
        }

        public byte[] RecentAttentionCommands
        {
            get { return _recentAttentionCommands.ToArray(); }
        }

        /// <summary>
        /// Gets the debug info value.
        /// </summary>
        public string GetDebugInfo()
        {
            return string.Format(
                "mounted={0} hwCustom={1} hwBoot={2} hwPc={3:X4} status={4:X2}:\"{5}\" pending={6} lastExec={7:X4} lastCmd=\"{8}\" cmdHist=\"{9}\" mw={10}@{11:X4}+{12} listening={13} talking={14} deviceAttn={15} payloadRx={16} payloadTx={17} armRx={18} armTx={19} attn={20} probeHold={21}:{22} ch={23} listenSA={24:X2} talkSA={25:X2} talkIdx={26}/{27} sender={28} receiver={29} bus={30} recent={31} open0={32} open15={33} atnEdges={34}/{35} attnBeg={36} attnEnd={37} attnCmds={38} swClk={39} swData={40} pendArmed={41} pendWaitAtn={42} pendWait={43} pendIdle={44}",
                _mountedImage != null,
                _hardware.HasCustomCodeActive,
                _hardware.IsBooting,
                _hardware.ProgramCounter,
                _statusCode,
                _statusText,
                _pendingCustomExecution,
                _lastExecuteAddress,
                _lastCommandText,
                FormatRecentCommandTexts(),
                _memoryWriteCommandCount,
                _lastMemoryWriteAddress,
                _lastMemoryWriteLength,
                _deviceListening,
                _deviceTalking,
                _deviceAttentioned,
                _payloadReceiveArmed,
                _payloadSendArmed,
                _armPayloadReceiveAfterAttention,
                _armPayloadSendAfterAttention,
                _attentionHandshakeState,
                _presenceProbeHoldActive,
                _presenceProbeHoldCycles,
                _channel,
                _listeningSecondaryAddress,
                _talkSecondaryAddress,
                _talkBufferIndex,
                _talkBuffer != null ? _talkBuffer.Length : 0,
                _sender.GetDebugState(),
                _receiver.GetDebugState(),
                _hardware.Bus.GetSerialDebugInfo() + " " + _hardware.Bus.GetDiskDebugInfo(),
                FormatRecentCommands(),
                DescribeOpenChannel(0),
                DescribeOpenChannel(15),
                _atnLowTransitions,
                _atnHighTransitions,
                _attentionBeginCount,
                _attentionEndCount,
                _attentionCommandCount,
                _softwareClockLineLow,
                _softwareDataLineLow,
                _pendingCustomExecutionArmed,
                _pendingCustomExecutionWaitForAtnRelease,
                _pendingCustomExecutionWaitCycles,
                _pendingCustomExecutionIdleCycles);
        }

        /// <summary>
        /// Opens kernal channel.
        /// </summary>
        public CommandResult OpenKernalChannel(byte secondaryAddress, string filename)
        {
            PulseLed(LedCommandPulseCycles);

            byte channel = (byte)(secondaryAddress & 0x0F);
            _channel = channel;
            DriveChannel state = GetOrCreateChannel(channel);
            state.ReadOffset = 0;

            if (channel == 15)
            {
                state.FilenameBytes = Array.Empty<byte>();
                SetChannelReadBuffer(channel, EncodeStatusText(_statusText));
                state.WriteBuffer = new List<byte>();
                return SetStatus(new CommandResult(0x00, _statusText, state.ReadBuffer));
            }

            string normalizedFilename = filename ?? string.Empty;
            state.FilenameBytes = Encoding.ASCII.GetBytes(normalizedFilename);
            state.ResetReadSource();
            state.WriteBuffer = null;

            if (!TryPrepareStandardReadChannel(channel))
            {
                return SetStatus(new CommandResult(0x04, "62, FILE NOT FOUND,00,00"));
            }

            byte[] statusPayload = state.ReadBuffer ?? Array.Empty<byte>();
            return SetStatus(new CommandResult(0x00, "00, OK,00,00", statusPayload));
        }

        /// <summary>
        /// Closes kernal channel.
        /// </summary>
        public CommandResult CloseKernalChannel(byte secondaryAddress)
        {
            byte channel = (byte)(secondaryAddress & 0x0F);
            if (channel == 15)
            {
                ProcessKernalCommandChannel(channel, true);
                ArmPendingCustomExecutionAfterCommandChannel();
            }

            RemoveChannel(channel);
            return SetStatus(new CommandResult(0x00, _statusText));
        }

        /// <summary>
        /// Attempts to read kernal channel byte and reports whether it succeeded.
        /// </summary>
        public bool TryReadKernalChannelByte(byte secondaryAddress, out byte value, out bool endOfInformation)
        {
            value = 0x00;
            endOfInformation = false;

            byte channel = (byte)(secondaryAddress & 0x0F);
            if (!TryPrepareStandardReadChannel(channel))
            {
                return false;
            }

            DriveChannel state;
            if (!TryGetChannel(channel, out state))
            {
                return false;
            }

            D64Image.SequentialFileReader sequentialReader = state.SequentialReader;
            if (sequentialReader != null)
            {
                if (!sequentialReader.TryReadByte(out value))
                {
                    SetStatus(new CommandResult(0x40, _statusText));
                    endOfInformation = true;
                    return true;
                }

                state.ReadOffset = sequentialReader.BytesRead;
                endOfInformation = sequentialReader.IsFinished;
                PulseLed(LedCommandPulseCycles / 8);
                SetStatus(new CommandResult(endOfInformation ? (byte)0x40 : (byte)0x00, _statusText));
                return true;
            }

            byte[] channelData = state.ReadBuffer;
            if (channelData == null)
            {
                return false;
            }

            int offset = state.ReadOffset;

            if (offset < 0)
            {
                offset = 0;
            }

            if (offset >= channelData.Length)
            {
                SetStatus(new CommandResult(0x40, _statusText));
                endOfInformation = true;
                return true;
            }

            value = channelData[offset];
            offset++;
            state.ReadOffset = offset;
            endOfInformation = offset >= channelData.Length;
            PulseLed(LedCommandPulseCycles / 8);
            SetStatus(new CommandResult(endOfInformation ? (byte)0x40 : (byte)0x00, _statusText));
            return true;
        }

        /// <summary>
        /// Writes kernal channel byte.
        /// </summary>
        public CommandResult WriteKernalChannelByte(byte secondaryAddress, byte value)
        {
            byte channel = (byte)(secondaryAddress & 0x0F);
            DriveChannel state = GetOrCreateChannel(channel);
            List<byte> writeBuffer = state.WriteBuffer;
            if (writeBuffer == null)
            {
                writeBuffer = new List<byte>();
                state.WriteBuffer = writeBuffer;
            }

            writeBuffer.Add(value);
            if (channel == 15 && value == 0x0D)
            {
                ProcessKernalCommandChannel(channel, false);
            }

            return SetStatus(new CommandResult(0x00, _statusText));
        }

        public byte StatusCode
        {
            get { return _statusCode; }
        }

        public string StatusText
        {
            get { return _statusText; }
        }

        /// <summary>
        /// Executes command.
        /// </summary>
        public CommandResult ExecuteCommand(byte[] commandBytes)
        {
            return ExecuteCommand(commandBytes, true);
        }

        /// <summary>
        /// Executes command.
        /// </summary>
        private CommandResult ExecuteCommand(byte[] commandBytes, bool trimTrailingCarriageReturns)
        {
            PulseLed(LedCommandPulseCycles);
            byte[] trimmed = trimTrailingCarriageReturns
                ? TrimTrailingCarriageReturns(commandBytes)
                : (commandBytes ?? Array.Empty<byte>());
            if (trimmed.Length == 0)
            {
                return new CommandResult(0x00, "00, OK,00,00");
            }

            _lastCommandText = DescribeCommand(trimmed);
            AddRecentCommandText(_lastCommandText);
            MirrorCommandToInputBuffer(trimmed);

            if (MatchesInitializeCommand(trimmed))
            {
                InitializeDriveState(true);
                return SetStatus(new CommandResult(0x00, "73, CBM DOS V2.6 1541,00,00"));
            }

            if (IsMemoryWriteCommand(trimmed))
            {
                return ExecuteMemoryWrite(trimmed);
            }

            if (IsMemoryReadCommand(trimmed))
            {
                return ExecuteMemoryRead(trimmed);
            }

            if (IsMemoryExecuteCommand(trimmed))
            {
                return ExecuteMemoryExecute(trimmed);
            }

            if (IsUserCommand(trimmed))
            {
                return ExecuteUserCommand(trimmed);
            }

            if (IsBlockReadCommand(trimmed))
            {
                return ExecuteBlockRead(trimmed);
            }

            if (IsBlockWriteCommand(trimmed))
            {
                return ExecuteBlockWrite(trimmed);
            }

            if (IsBlockPointerCommand(trimmed))
            {
                return ExecuteBlockPointer(trimmed);
            }

            if (IsBlockAllocateOrFreeCommand(trimmed))
            {
                return ExecuteBlockAllocateOrFree(trimmed);
            }

            return SetStatus(new CommandResult(0x10, "30, SYNTAX ERROR,00,00"));
        }

        /// <summary>
        /// Begins listen.
        /// </summary>
        public void BeginListen(byte secondaryAddress)
        {
            PulseLed(LedCommandPulseCycles);
            _listeningSecondaryAddress = secondaryAddress;
            _channel = (byte)(secondaryAddress & 0x0F);
            _listenBuffer.Clear();

            if ((secondaryAddress & 0xF0) == CloseBase)
            {
                RemoveChannel(_channel);
            }
        }

        /// <summary>
        /// Handles the receive listen byte operation.
        /// </summary>
        public void ReceiveListenByte(byte value)
        {
            PulseLed(LedCommandPulseCycles);
            _listenBuffer.Add(value);
        }

        /// <summary>
        /// Ends listen.
        /// </summary>
        public void EndListen()
        {
            PulseLed(LedCommandPulseCycles);
            byte command = (byte)(_listeningSecondaryAddress & 0xF0);
            if (command == OpenBase || command == SecondaryBase)
            {
                DriveChannel state = GetOrCreateChannel(_channel);
                state.FilenameBytes = _listenBuffer.ToArray();
            }

            _listenBuffer.Clear();
        }

        /// <summary>
        /// Begins talk.
        /// </summary>
        public void BeginTalk(byte secondaryAddress)
        {
            PulseLed(LedCommandPulseCycles);
            _talkSecondaryAddress = secondaryAddress;
            _channel = (byte)(secondaryAddress & 0x0F);
            _talkBufferIndex = 0;
            _talkBuffer = Array.Empty<byte>();
            TryPrepareStandardReadChannel(_channel);
        }

        /// <summary>
        /// Attempts to read talk byte and reports whether it succeeded.
        /// </summary>
        public bool TryReadTalkByte(out byte value, out bool endOfInformation)
        {
            return TryReadKernalChannelByte(_talkSecondaryAddress, out value, out endOfInformation);
        }

        /// <summary>
        /// Ends talk.
        /// </summary>
        public void EndTalk()
        {
            PulseLed(LedCommandPulseCycles);
            _talkSecondaryAddress = 0x00;
            _talkBuffer = Array.Empty<byte>();
            _talkBufferIndex = 0;
        }

        /// <summary>
        /// Advances the component by one emulated tick.
        /// </summary>
        public void Tick()
        {
            TickLed();
            if (ShouldTickHardware())
            {
                _hardware.Tick();
            }

            UpdateHardwareSerialTransportMode();

            TickPassiveObserver();

            if (IsHardwareTransportReady)
            {
                _lastAtnLow = _port.IsLineLow(IecBusLine.Atn);
                _attentionHandshakeState = AttentionHandshakeState.Idle;
                _postPayloadReadyHoldCycles = 0;
                _receiver.Reset();
                _sender.Reset();
                TryStartPendingCustomExecution(_lastAtnLow);
                return;
            }

            if (_useKernalBridgeTransport && _bridgeLowLevelSessionActive && !_hardware.HasCustomCodeActive)
            {
                bool atnLowBridge = _port.IsLineLow(IecBusLine.Atn);
                _lastAtnLow = atnLowBridge;
                // While the bridge owns the low-level IEC path, the software
                // sender/receiver must never leave stale DATA/CLOCK levels on
                // the bus. Resetting them here is intentionally aggressive:
                // the bridge itself carries the command/data flow, so any
                // leftover byte-level state only risks wedging the KERNAL in
                // SEARCHING FOR * with DATA held low.
                ResetSoftwareTransportStateForBridge();
                if (_pendingCustomExecution != PendingCustomExecutionType.None)
                {
                    TryStartPendingCustomExecution(atnLowBridge);
                }

                ReleaseLines();
                return;
            }

            if (_mountedImage == null && !_hardware.HasCustomCodeActive)
            {
                _hardware.Bus.SerialOutputsEnabled = false;
                _lastAtnLow = _port.IsLineLow(IecBusLine.Atn);
                ReleaseLines();
                return;
            }

            if (_hardware.HasCustomCodeActive)
            {
                TryStartPendingCustomExecution(_port.IsLineLow(IecBusLine.Atn));
                return;
            }

            bool atnLow = _port.IsLineLow(IecBusLine.Atn);

            // The IEC bus callback normally catches ATN edges immediately.
            // During heavy CPU/VIC timing or while switching between bridge
            // and software transport, that callback can be missed relative to
            // the software IEC state machine. If that happens the receiver
            // enters its generic WaitTalkerReady path and pulls DATA low
            // forever, which shows up as SEARCHING FOR * followed by DEVICE
            // NOT PRESENT. Re-detect the ATN edge here as a safety net so the
            // drive always enters/leaves the proper attention handshake.
            if (atnLow != _lastAtnLow)
            {
                if (atnLow)
                {
                    _atnLowTransitions++;
                    BeginAttention();
                }
                else
                {
                    _atnHighTransitions++;
                    EndAttention();
                }
            }

            _lastAtnLow = atnLow;

            if (TryStartPendingCustomExecution(atnLow))
            {
                return;
            }

            if (_pendingCustomExecution != PendingCustomExecutionType.None)
            {
                if (!IsSoftwareTransportInternallyIdleForCustomExecution())
                {
                    DrainSoftwareTransportForPendingCustomExecution(atnLow);
                }

                return;
            }

            if (_continueAttentionCommandAfterRelease)
            {
                TickPendingAttentionCommandAfterRelease();
                return;
            }

            if (atnLow)
            {
                if (!TickAttentionHandshake())
                {
                    return;
                }

                TickReceiveAttentionBytes();
                return;
            }

            TryStartPendingCustomExecution(false);

            if (_deviceListening && _payloadReceiveArmed)
            {
                if (atnLow)
                {
                    if (_receiver.HasInFlightByte)
                    {
                        TickReceivePayloadBytes();
                    }
                    else
                    {
                        FinalizePendingPayloadIfNeeded();
                    }
                }
                else
                {
                    TickReceivePayloadBytes();
                }

                return;
            }

            if (_postPayloadReadyHoldCycles > 0)
            {
                _postPayloadReadyHoldCycles--;
                ReleaseClock();
                PullDataLow();
                return;
            }

            if (TickPresenceProbeHold())
            {
                return;
            }

            if (_deviceTalking && _payloadSendArmed)
            {
                TickSendPayloadBytes();
                return;
            }

            ReleaseLines();
        }

        /// <summary>
        /// Advances drive-visible activity indicators without running the IEC or 1541 CPU state machines.
        /// </summary>
        public void AdvanceIdleVisualState(int cycles)
        {
            if (cycles <= 0 || NeedsClockTick)
            {
                return;
            }

            if (_ledBusyCycles > 0)
            {
                _ledBusyCycles -= cycles;
                if (_ledBusyCycles < 0)
                {
                    _ledBusyCycles = 0;
                }
            }

            if (HasVisibleActivity)
            {
                _ledPhaseCycles += cycles;
            }
            else
            {
                _ledPhaseCycles = 0;
            }
        }

        /// <summary>
        /// Handles the drain software transport for pending custom execution operation.
        /// </summary>
        private void DrainSoftwareTransportForPendingCustomExecution(bool atnLow)
        {
            if (_attentionHandshakeState != AttentionHandshakeState.Idle && atnLow)
            {
                if (!TickAttentionHandshake())
                {
                    return;
                }

                TickReceiveAttentionBytes();
                return;
            }

            if (_continueAttentionCommandAfterRelease)
            {
                TickPendingAttentionCommandAfterRelease();
                return;
            }

            if (_deviceListening && _payloadReceiveArmed)
            {
                TickReceivePayloadBytes();
                return;
            }

            if (_postPayloadReadyHoldCycles > 0)
            {
                _postPayloadReadyHoldCycles--;
                ReleaseClock();
                PullDataLow();
                return;
            }

            if (TickPresenceProbeHold())
            {
                return;
            }

            if (_deviceTalking && _payloadSendArmed)
            {
                TickSendPayloadBytes();
                return;
            }

            if (!_receiver.IsIdle)
            {
                ReceivedIecByte receivedByte;
                if (_receiver.Tick(out receivedByte))
                {
                    if (_payloadReceiveArmed)
                    {
                        _receiver.AppendToPayload(receivedByte.Value);
                        if (receivedByte.IsEoi)
                        {
                            return;
                        }

                        _receiver.PrepareForNextByte();
                    }
                    else if (atnLow)
                    {
                        HandleCommand(receivedByte.Value);
                    }
                }

                return;
            }

            if (!_sender.IsIdle)
            {
                _sender.Tick();
            }
        }

        /// <summary>
        /// Advances the passive observer state by one emulated tick.
        /// </summary>
        private void TickPassiveObserver()
        {
            if (!ShouldPassiveObserveIecTraffic())
            {
                return;
            }

            bool atnLow = _port.IsLineLow(IecBusLine.Atn);
            if (atnLow != _passiveLastAtnLow)
            {
                if (atnLow)
                {
                    BeginPassiveAttention();
                }
                else
                {
                    EndPassiveAttention();
                }

                _passiveLastAtnLow = atnLow;
            }

            ReceivedIecByte passiveByte;
            if (_passiveReceiver.Tick(out passiveByte))
            {
                if (atnLow)
                {
                    HandlePassiveCommand(passiveByte.Value);
                }
                else if (_passivePayloadReceiveArmed && _passiveChannel == 15)
                {
                    AppendPassivePayloadByte(passiveByte.Value);
                    if (passiveByte.IsEoi)
                    {
                        FinalizePassivePayload();
                    }
                }
            }

            if (!atnLow)
            {
                TryStartPendingCustomExecution(false);
            }
        }

        /// <summary>
        /// Handles the should passive observe iec traffic operation.
        /// </summary>
        private bool ShouldPassiveObserveIecTraffic()
        {
            if (!_observeExternalIecTraffic)
            {
                return false;
            }

            // While the low-level KERNAL IEC bridge is active, LISTEN/TALK/
            // OPEN/SECOND/... are already delivered directly into the drive
            // state machine. Observing the same phase a second time from the
            // passive sniffer duplicates commands and can e.g. remove channel
            // 0 again via a stray CLOSE interpretation, which shows up as
            // LOAD"*",8 falling from SEARCHING FOR * into DEVICE NOT PRESENT.
            // The passive path is only for direct CIA2 bit-banged traffic
            // after the KERNAL bridge is no longer owning the bus.
            if (_hardware.HasCustomCodeActive || IsHardwareTransportReady)
            {
                return false;
            }

            return _observeExternalIecTraffic &&
                _useKernalBridgeTransport &&
                !_bridgeLowLevelSessionActive;
        }

        /// <summary>
        /// Handles the reset passive observer state operation.
        /// </summary>
        private void ResetPassiveObserverState()
        {
            _passiveListening = false;
            _passiveTalking = false;
            _passivePayloadReceiveArmed = false;
            _passiveArmPayloadReceiveAfterAttention = false;
            _passiveChannel = 0;
            _passiveLastAtnLow = _port.IsLineLow(IecBusLine.Atn);
            _passiveReceiver.Reset();
        }

        /// <summary>
        /// Handles the reset software transport state for bridge operation.
        /// </summary>
        private void ResetSoftwareTransportStateForBridge()
        {
            _deviceAttentioned = false;
            _deviceListening = false;
            _deviceTalking = false;
            _payloadReceiveArmed = false;
            _payloadSendArmed = false;
            _armPayloadReceiveAfterAttention = false;
            _armPayloadSendAfterAttention = false;
            _channel = 0;
            _listeningSecondaryAddress = 0;
            _talkSecondaryAddress = 0;
            _talkBuffer = Array.Empty<byte>();
            _talkBufferIndex = 0;
            _listenBuffer.Clear();
            _receiver.Reset();
            _sender.Reset();
            _attentionHandshakeState = AttentionHandshakeState.Idle;
            _postPayloadReadyHoldCycles = 0;
            _presenceProbeHoldActive = false;
            _presenceProbeObservedClockLow = false;
            _presenceProbeHoldCycles = 0;
            _continueAttentionCommandAfterRelease = false;
            _attentionPostReleaseWaitCycles = 0;
        }

        /// <summary>
        /// Begins passive attention.
        /// </summary>
        private void BeginPassiveAttention()
        {
            PulseLed(LedCommandPulseCycles / 8);
            if (_passivePayloadReceiveArmed)
            {
                FinalizePassivePayload();
            }

            _passiveReceiver.Reset();
            _passiveReceiver.PrepareForCommandByte();
        }

        /// <summary>
        /// Ends passive attention.
        /// </summary>
        private void EndPassiveAttention()
        {
            _passiveReceiver.Reset();
            if (_passiveListening && _passiveArmPayloadReceiveAfterAttention)
            {
                _passiveArmPayloadReceiveAfterAttention = false;
                _passivePayloadReceiveArmed = true;
                _passiveReceiver.PrepareForNextByte();
            }
        }

        /// <summary>
        /// Handles passive command.
        /// </summary>
        private void HandlePassiveCommand(byte command)
        {
            PulseLed(LedCommandPulseCycles / 8);
            _recentAttentionCommands.Add(command);
            if (_recentAttentionCommands.Count > 16)
            {
                _recentAttentionCommands.RemoveAt(0);
            }

            if (command == (byte)(ListenBase | _deviceNumber))
            {
                _passiveListening = true;
                _passiveTalking = false;
                _passivePayloadReceiveArmed = false;
                _passiveArmPayloadReceiveAfterAttention = false;
                return;
            }

            if (command == (byte)(TalkBase | _deviceNumber))
            {
                if (_passivePayloadReceiveArmed)
                {
                    FinalizePassivePayload();
                }

                _passiveTalking = true;
                _passiveListening = false;
                _passivePayloadReceiveArmed = false;
                _passiveArmPayloadReceiveAfterAttention = false;
                return;
            }

            if (command == Unlisten)
            {
                if (_passivePayloadReceiveArmed)
                {
                    FinalizePassivePayload();
                }

                _passiveListening = false;
                _passivePayloadReceiveArmed = false;
                _passiveArmPayloadReceiveAfterAttention = false;
                return;
            }

            if (command == Untalk)
            {
                _passiveTalking = false;
                return;
            }

            if (_passiveListening && (command & 0xF0) == OpenBase)
            {
                _passiveChannel = (byte)(command & 0x0F);
                _passivePayloadReceiveArmed = false;
                _passiveArmPayloadReceiveAfterAttention = _passiveChannel == 15;
                if (_passiveChannel == 15)
                {
                    DriveChannel state = GetOrCreateChannel(15);
                    state.FilenameBytes = Array.Empty<byte>();
                }

                return;
            }

            if (_passiveListening && (command & 0xF0) == SecondaryBase)
            {
                _passiveChannel = (byte)(command & 0x0F);
                _passivePayloadReceiveArmed = false;
                _passiveArmPayloadReceiveAfterAttention = _passiveChannel == 15;
                if (_passiveChannel == 15)
                {
                    DriveChannel state = GetOrCreateChannel(15);
                    state.FilenameBytes = Array.Empty<byte>();
                }

                return;
            }

            if ((_passiveListening || _passiveTalking) && (command & 0xF0) == CloseBase)
            {
                byte channel = (byte)(command & 0x0F);
                if (channel == 15)
                {
                    _channel = 15;
                    ProcessCommandChannelIfNeeded();
                    ArmPendingCustomExecutionAfterCommandChannel();
                }

                RemoveChannel(channel);
            }
        }

        /// <summary>
        /// Handles the append passive payload byte operation.
        /// </summary>
        private void AppendPassivePayloadByte(byte value)
        {
            if (_passiveChannel != 15)
            {
                return;
            }

            PulseLed(LedCommandPulseCycles / 12);
            DriveChannel state = GetOrCreateChannel(15);
            byte[] existing = state.FilenameBytes ?? Array.Empty<byte>();
            byte[] updated = new byte[existing.Length + 1];
            if (existing.Length > 0)
            {
                Array.Copy(existing, updated, existing.Length);
            }

            updated[existing.Length] = value;
            state.FilenameBytes = updated;
        }

        /// <summary>
        /// Handles the finalize passive payload operation.
        /// </summary>
        private void FinalizePassivePayload()
        {
            if (_passiveChannel == 15)
            {
                _channel = 15;
                ProcessCommandChannelIfNeeded();
                ArmPendingCustomExecutionAfterCommandChannel();
            }

            _passivePayloadReceiveArmed = false;
            _passiveArmPayloadReceiveAfterAttention = false;
            _passiveReceiver.Reset();
        }

        /// <summary>
        /// Handles iec line changed.
        /// </summary>
        private void HandleIecLineChanged(IecBusLine line, bool isLow)
        {
            if (line != IecBusLine.Atn)
            {
                return;
            }

            if (IsHardwareTransportReady)
            {
                _lastAtnLow = isLow;
                return;
            }

            if (_useKernalBridgeTransport && _bridgeLowLevelSessionActive && !_hardware.HasCustomCodeActive)
            {
                _lastAtnLow = isLow;
                return;
            }

            if (_mountedImage == null && !_hardware.HasCustomCodeActive)
            {
                _lastAtnLow = isLow;
                return;
            }

            if (isLow == _lastAtnLow)
            {
                return;
            }

            if (isLow)
            {
                _atnLowTransitions++;
            }
            else
            {
                _atnHighTransitions++;
            }

            if (_hardware.HasCustomCodeActive)
            {
                _lastAtnLow = isLow;
                return;
            }

            if (_pendingCustomExecution != PendingCustomExecutionType.None)
            {
                // A just-accepted U3/M-E command does not mean the C64 side is
                // already done with KERNAL IEC housekeeping. Some loaders still
                // issue one more ATN phase before jumping into their RAM-side
                // bit-banged routine. Keep acknowledging those ATN phases with
                // the software IEC state machine until the custom code actually
                // takes over, otherwise the KERNAL sets ST=$80 even though the
                // drive accepted the command.
                if (isLow)
                {
                    BeginAttention();
                }
                else
                {
                    EndAttention();
                }

                _lastAtnLow = isLow;
                return;
            }

            if (isLow)
            {
                BeginAttention();
            }
            else
            {
                EndAttention();
            }

            _lastAtnLow = isLow;
        }

        /// <summary>
        /// Advances the presence probe hold state by one emulated tick.
        /// </summary>
        private bool TickPresenceProbeHold()
        {
            if (!_presenceProbeHoldActive)
            {
                return false;
            }

            if (_port.IsLineLow(IecBusLine.Clock))
            {
                _presenceProbeObservedClockLow = true;
            }

            ReleaseClock();

            if (!_presenceProbeObservedClockLow)
            {
                if (_presenceProbeHoldCycles > 0)
                {
                    _presenceProbeHoldCycles--;
                }

                PullDataLow();

                if (_presenceProbeHoldCycles <= 0)
                {
                    _presenceProbeHoldActive = false;
                    _presenceProbeObservedClockLow = false;
                    _presenceProbeHoldCycles = 0;
                    ReleaseData();
                }

                return true;
            }

            ReleaseData();

            if (!_port.IsLineLow(IecBusLine.Clock))
            {
                _presenceProbeHoldActive = false;
                _presenceProbeObservedClockLow = false;
                _presenceProbeHoldCycles = 0;
                return false;
            }

            if (_presenceProbeHoldCycles > 0)
            {
                _presenceProbeHoldCycles--;
            }

            if (_presenceProbeHoldCycles <= 0)
            {
                _presenceProbeHoldActive = false;
                _presenceProbeObservedClockLow = false;
                _presenceProbeHoldCycles = 0;
            }

            return true;
        }

        /// <summary>
        /// Handles the should tick hardware operation.
        /// </summary>
        private bool ShouldTickHardware()
        {
            if (_hardware.RunCpuContinuously)
            {
                return true;
            }

            if (IsHardwareTransportReady)
            {
                return true;
            }

            if (_hardware.HasCustomCodeActive ||
                _hardware.IsBooting ||
                _pendingCustomExecution != PendingCustomExecutionType.None)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether the shared IEC lines currently require the custom drive code to be caught up.
        /// </summary>
        private bool IsIecBusActiveForDrive()
        {
            return _port.IsLineLow(IecBusLine.Atn) ||
                _port.IsLineLow(IecBusLine.Clock) ||
                _port.IsLineLow(IecBusLine.Data);
        }

        /// <summary>
        /// Returns whether the component can standard rom transport take bus.
        /// </summary>
        private bool CanStandardRomTransportTakeBus()
        {
            if (_hardware.HasCustomCodeActive)
            {
                return false;
            }

            if (!_hardware.HasSerialRomInitialization)
            {
                return false;
            }

            if (!_forceSoftwareTransport)
            {
                return true;
            }

            if (StandardRomTransportStartGate != null && !StandardRomTransportStartGate())
            {
                return false;
            }

            return !_useKernalBridgeTransport || !_bridgeLowLevelSessionActive;
        }

        /// <summary>
        /// Updates hardware serial transport mode.
        /// </summary>
        private void UpdateHardwareSerialTransportMode()
        {
            bool enableHardwareOutputs;
            if (_hardware.HasCustomCodeActive)
            {
                enableHardwareOutputs = true;
            }
            else if (!_forceSoftwareTransport)
            {
                enableHardwareOutputs = _hardware.HasSerialRomInitialization;
            }
            else
            {
                enableHardwareOutputs = CanStandardRomTransportTakeBus();
            }

            _hardware.Bus.SerialOutputsEnabled = enableHardwareOutputs;
        }

        /// <summary>
        /// Begins attention.
        /// </summary>
        private void BeginAttention()
        {
            _attentionBeginCount++;
            _attentionSawCommand = false;
            _continueAttentionCommandAfterRelease = false;
            _attentionPostReleaseWaitCycles = 0;
            PulseLed(LedCommandPulseCycles / 4);
            if (_deviceListening && _payloadReceiveArmed)
            {
                ReceivedIecByte pendingPayloadByte;
                if (_receiver.TryFinalizePendingByte(out pendingPayloadByte))
                {
                    _receiver.AppendToPayload(pendingPayloadByte.Value);
                    if (pendingPayloadByte.IsEoi)
                    {
                        FinalizeReceivedPayload();
                    }
                }

                if (_receiver.HasPayloadBytes)
                {
                    FinalizeReceivedPayload();
                }
            }

            _receiver.Reset();
            _sender.Reset();
            _deviceAttentioned = false;
            _postPayloadReadyHoldCycles = 0;
            _presenceProbeHoldActive = false;
            _presenceProbeObservedClockLow = false;
            _presenceProbeHoldCycles = 0;
            // As soon as ATN falls, every IEC device must stop what it is doing,
            // release CLOCK and assert DATA to acknowledge that it is present
            // and ready to listen. Doing this only later in the polling tick is
            // enough to trip DEVICE NOT PRESENT on some KERNAL paths.
            ReleaseClock();
            PullDataLow();
            _attentionHandshakeState = AttentionHandshakeState.WaitClockLowForByteStart;
        }

        /// <summary>
        /// Ends attention.
        /// </summary>
        private void EndAttention()
        {
            _attentionEndCount++;
            if (!_payloadReceiveArmed &&
                _attentionHandshakeState == AttentionHandshakeState.Receiving &&
                !_receiver.IsIdle)
            {
                ReceivedIecByte lateCommandByte;
                if (_receiver.TryFinalizePendingByte(out lateCommandByte))
                {
                    _attentionSawCommand = true;
                    _attentionCommandCount++;
                    HandleCommand(lateCommandByte.Value);
                    CompleteAttentionEnd(true);
                    return;
                }

                // Pi1541 keeps the command-phase receive alive across ATN
                // release if the computer already started shifting the command
                // byte. Resetting the receiver here loses that last TALK/
                // LISTEN/SECOND/OPEN byte and wedges the C64 in SEARCHING FOR
                // * or DEVICE NOT PRESENT loops.
                _continueAttentionCommandAfterRelease = true;
                _attentionPostReleaseWaitCycles = 0;
                return;
            }

            CompleteAttentionEnd(false);
        }

        /// <summary>
        /// Handles the complete attention end operation.
        /// </summary>
        private void CompleteAttentionEnd(bool commandAlreadyHandled)
        {
            ReceivedIecByte pendingByte;
            if (_payloadReceiveArmed)
            {
                if (_receiver.TryFinalizePendingByte(out pendingByte))
                {
                    _receiver.AppendToPayload(pendingByte.Value);
                }

                if (_receiver.HasPayloadBytes)
                {
                    FinalizeReceivedPayload();
                }
                else
                {
                    _receiver.PrepareForNextByte();
                }
            }
            else if (!commandAlreadyHandled && _receiver.TryFinalizePendingByte(out pendingByte))
            {
                _attentionSawCommand = true;
                _attentionCommandCount++;
                HandleCommand(pendingByte.Value);
                _receiver.Reset();
            }

            _continueAttentionCommandAfterRelease = false;
            _attentionPostReleaseWaitCycles = 0;
            _attentionHandshakeState = AttentionHandshakeState.Idle;

            // Once ATN is released, the command-phase receiver must no longer
            // keep DATA pulled low. Leaving the receiver parked in its
            // command/listener wait states here makes the C64 KERNAL stall
            // after LISTEN/OPEN/UNLISTEN because TALK never starts: the drive
            // still looks like it is acknowledging a byte forever. We always
            // drop the command receiver back to an idle/released state here
            // and then explicitly arm the next payload phase if needed.
            _receiver.Reset();

            if (!_attentionSawCommand &&
                !_payloadReceiveArmed &&
                !_armPayloadReceiveAfterAttention &&
                !_deviceTalking)
            {
                // The first KERNAL presence probe can assert ATN without
                // delivering any command bytes yet. Real drives keep their
                // "device present" response visible long enough for the C64 to
                // sample it after ATN is released. If we drop DATA
                // immediately here, the KERNAL falls back to DEVICE NOT
                // PRESENT before it ever reaches LISTEN/TALK.
                _presenceProbeHoldActive = true;
                _presenceProbeObservedClockLow = false;
                _presenceProbeHoldCycles = Math.Max(_presenceProbeHoldCycles, PresenceProbeReadyHoldCycles);
                // Make the hold visible on the bus immediately. If we wait
                // until the next periodic Tick(), the C64 KERNAL can sample
                // the post-ATN presence window before DATA is re-asserted and
                // conclude "DEVICE NOT PRESENT" even though the drive decided
                // to keep acknowledging.
                ReleaseClock();
                PullDataLow();
            }

            if (!_payloadReceiveArmed && _armPayloadReceiveAfterAttention)
            {
                _armPayloadReceiveAfterAttention = false;
                _receiver.PrepareForNextByte();
                _payloadReceiveArmed = true;
            }

            if (_deviceTalking && (_payloadSendArmed || _armPayloadSendAfterAttention))
            {
                _armPayloadSendAfterAttention = false;
                _payloadSendArmed = true;
                PrepareTalkData();
            }
        }

        /// <summary>
        /// Advances the pending attention command after release state by one emulated tick.
        /// </summary>
        private void TickPendingAttentionCommandAfterRelease()
        {
            _attentionPostReleaseWaitCycles++;

            ReceivedIecByte receivedByte;
            if (_receiver.Tick(out receivedByte))
            {
                _attentionSawCommand = true;
                _attentionCommandCount++;
                HandleCommand(receivedByte.Value);
                CompleteAttentionEnd(true);
                return;
            }

            if (_receiver.IsIdle || _attentionPostReleaseWaitCycles >= AttentionPostReleaseCommandTimeoutCycles)
            {
                CompleteAttentionEnd(false);
            }
        }

        /// <summary>
        /// Advances the attention handshake state by one emulated tick.
        /// </summary>
        private bool TickAttentionHandshake()
        {
            switch (_attentionHandshakeState)
            {
                case AttentionHandshakeState.Idle:
                case AttentionHandshakeState.Receiving:
                    return true;

                case AttentionHandshakeState.WaitClockLowForByteStart:
                    ReleaseClock();
                    PullDataLow();
                    if (_port.IsLineLow(IecBusLine.Clock))
                    {
                        _attentionHandshakeState = AttentionHandshakeState.WaitInterByteClockHighForByteTransferStart;
                    }

                    return false;

                case AttentionHandshakeState.WaitInterByteClockLowForByteStart:
                    ReleaseClock();
                    PullDataLow();
                    if (_port.IsLineLow(IecBusLine.Clock))
                    {
                        _attentionHandshakeState = AttentionHandshakeState.WaitInterByteClockHighForByteTransferStart;
                    }

                    return false;

                case AttentionHandshakeState.WaitInterByteClockHighForByteTransferStart:
                    ReleaseClock();
                    PullDataLow();
                    if (!_port.IsLineLow(IecBusLine.Clock))
                    {
                        _receiver.BeginCommandByteTransfer();
                        _attentionHandshakeState = AttentionHandshakeState.Receiving;
                    }

                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Advances the receive attention bytes state by one emulated tick.
        /// </summary>
        private void TickReceiveAttentionBytes()
        {
            ReceivedIecByte receivedByte;
            if (!_receiver.Tick(out receivedByte))
            {
                return;
            }

            // Bytes transferred while ATN is asserted are always bus commands
            // (LISTEN/TALK/SECOND/OPEN/CLOSE/UNLISTEN/UNTALK). The filename or
            // data payload only starts after ATN is released.
            _attentionSawCommand = true;
            _attentionCommandCount++;
            HandleCommand(receivedByte.Value);
            _attentionHandshakeState = AttentionHandshakeState.WaitInterByteClockLowForByteStart;
        }

        /// <summary>
        /// Advances the receive payload bytes state by one emulated tick.
        /// </summary>
        private void TickReceivePayloadBytes()
        {
            ReceivedIecByte receivedByte;
            if (!_receiver.Tick(out receivedByte))
            {
                if (_receiver.ShouldImplicitlyEndPayload(PayloadImplicitEndCycles))
                {
                    FinalizeReceivedPayload();
                }

                return;
            }

            _receiver.AppendToPayload(receivedByte.Value);
            if (receivedByte.IsEoi)
            {
                // Pi1541 and the real IEC flow keep the listener in the normal
                // post-byte state after an EOI-marked byte. The payload is then
                // effectively completed by the following ATN/UNLISTEN phase (or
                // by a later idle timeout), not by immediately hard-resetting
                // the listener on the same system tick.
                return;
            }

            _receiver.PrepareForNextByte();
        }

        /// <summary>
        /// Advances the send payload bytes state by one emulated tick.
        /// </summary>
        private void TickSendPayloadBytes()
        {
            if (_sender.NeedsMoreData)
            {
                if (!PrepareTalkData())
                {
                    _sender.Reset();
                    _deviceTalking = false;
                    _payloadSendArmed = false;
                    ReleaseLines();
                    return;
                }
            }

            if (_sender.IsIdle)
            {
                if (PrepareTalkData())
                {
                    return;
                }

                _deviceTalking = false;
                _payloadSendArmed = false;
                ReleaseLines();
                return;
            }

            _sender.Tick();
        }

        /// <summary>
        /// Handles command.
        /// </summary>
        private void HandleCommand(byte command)
        {
            _recentAttentionCommands.Add(command);
            if (_recentAttentionCommands.Count > 16)
            {
                _recentAttentionCommands.RemoveAt(0);
            }

            if (command == (byte)(ListenBase | _deviceNumber))
            {
                _deviceAttentioned = true;
                _deviceListening = true;
                _deviceTalking = false;
                _payloadReceiveArmed = false;
                _payloadSendArmed = false;
                _armPayloadReceiveAfterAttention = false;
                _armPayloadSendAfterAttention = false;
                return;
            }

            if (command == Unlisten)
            {
                FinalizePendingPayloadIfNeeded();
                ProcessCommandChannelIfNeeded();
                if (_channel == 15)
                {
                    ArmPendingCustomExecutionAfterCommandChannel();
                }
                _deviceAttentioned = false;
                _deviceListening = false;
                _payloadReceiveArmed = false;
                _armPayloadReceiveAfterAttention = false;
                return;
            }

            if (command == (byte)(TalkBase | _deviceNumber))
            {
                FinalizePendingPayloadIfNeeded();
                _deviceAttentioned = true;
                _deviceTalking = true;
                _deviceListening = false;
                _payloadSendArmed = false;
                _payloadReceiveArmed = false;
                _armPayloadReceiveAfterAttention = false;
                return;
            }

            if (command == Untalk)
            {
                _deviceAttentioned = false;
                _deviceTalking = false;
                _payloadSendArmed = false;
                _armPayloadSendAfterAttention = false;
                return;
            }

            if (_deviceListening && (command & 0xF0) == OpenBase)
            {
                _channel = (byte)(command & 0x0F);
                _payloadReceiveArmed = false;
                _armPayloadReceiveAfterAttention = true;
                return;
            }

            if ((_deviceListening || _deviceTalking) && (command & 0xF0) == SecondaryBase)
            {
                _channel = (byte)(command & 0x0F);
                if (_deviceListening)
                {
                    _payloadReceiveArmed = false;
                    _armPayloadReceiveAfterAttention = true;
                }
                else if (_deviceTalking)
                {
                    _payloadSendArmed = false;
                    _armPayloadSendAfterAttention = true;
                }

                return;
            }

            if ((_deviceListening || _deviceTalking) && (command & 0xF0) == CloseBase)
            {
                byte channel = (byte)(command & 0x0F);
                if (channel == 15)
                {
                    FinalizePendingPayloadIfNeeded();
                    ProcessCommandChannelIfNeeded();
                    ArmPendingCustomExecutionAfterCommandChannel();
                    _deviceAttentioned = false;
                    _deviceListening = false;
                    _deviceTalking = false;
                    _payloadReceiveArmed = false;
                    _payloadSendArmed = false;
                    _armPayloadReceiveAfterAttention = false;
                    _armPayloadSendAfterAttention = false;
                    _receiver.Reset();
                    _sender.Reset();
                    _attentionHandshakeState = AttentionHandshakeState.Idle;
                }

                RemoveChannel(channel);
            }
        }

        /// <summary>
        /// Handles the finalize received payload operation.
        /// </summary>
        private void FinalizeReceivedPayload()
        {
            byte[] payload = _receiver.ConsumePayload();
            GetOrCreateChannel(_channel).FilenameBytes = payload;
            _payloadReceiveArmed = false;
            _presenceProbeHoldActive = false;
            _presenceProbeObservedClockLow = false;
            _presenceProbeHoldCycles = 0;
            // After the last filename/data byte the listener must still remain
            // visibly ready for a short time so the KERNAL does not conclude
            // "DEVICE NOT PRESENT" before issuing UNLISTEN/TALK. ATN handling
            // cancels this hold immediately, so the following turnaround to the
            // next attention phase is still fast.
            _postPayloadReadyHoldCycles = PostEoiReadyHoldCycles;
        }

        /// <summary>
        /// Handles the finalize pending payload if needed operation.
        /// </summary>
        private void FinalizePendingPayloadIfNeeded()
        {
            if (!_payloadReceiveArmed)
            {
                return;
            }

            ReceivedIecByte pendingPayloadByte;
            if (_receiver.TryFinalizePendingByte(out pendingPayloadByte))
            {
                _receiver.AppendToPayload(pendingPayloadByte.Value);
            }

            if (_receiver.HasPayloadBytes)
            {
                FinalizeReceivedPayload();
                return;
            }

            _receiver.Reset();
            _payloadReceiveArmed = false;
        }

        /// <summary>
        /// Handles the prepare talk data operation.
        /// </summary>
        private bool PrepareTalkData()
        {
            byte[] talkData;
            bool isFinalChunk;
            if (!TryResolveTalkChunk(_channel, TalkChunkSize, out talkData, out isFinalChunk))
            {
                talkData = Array.Empty<byte>();
                isFinalChunk = true;
            }

            if (talkData == null || talkData.Length == 0)
            {
                return false;
            }

            BeginSimulatedTransfer(talkData.Length);
            if (_sender.NeedsMoreData)
            {
                _sender.Continue(talkData, isFinalChunk);
            }
            else
            {
                _sender.Begin(talkData, isFinalChunk);
            }

            return true;
        }

        /// <summary>
        /// Attempts to resolve talk chunk and reports whether it succeeded.
        /// </summary>
        private bool TryResolveTalkChunk(byte channel, int maxBytes, out byte[] talkData, out bool isFinalChunk)
        {
            talkData = Array.Empty<byte>();
            isFinalChunk = true;

            DriveChannel state;
            if (TryGetChannel(channel, out state) && state.SequentialReader != null)
            {
                int requestedBytes = Math.Max(1, maxBytes);
                var chunk = new List<byte>(requestedBytes);
                byte value;
                while (chunk.Count < requestedBytes && state.SequentialReader.TryReadByte(out value))
                {
                    chunk.Add(value);
                }

                state.ReadOffset = state.SequentialReader.BytesRead;
                isFinalChunk = state.SequentialReader.IsFinished;
                if (chunk.Count == 0)
                {
                    talkData = Array.Empty<byte>();
                    return false;
                }

                talkData = chunk.ToArray();
                return talkData.Length > 0;
            }

            byte[] bufferedChannelData;
            if (!TryResolveChannelData(channel, out bufferedChannelData) || bufferedChannelData == null || bufferedChannelData.Length == 0)
            {
                return false;
            }

            if (!TryGetChannel(channel, out state))
            {
                return false;
            }

            int offset = state.ReadOffset;

            if (offset < 0)
            {
                offset = 0;
            }

            if (offset >= bufferedChannelData.Length)
            {
                return false;
            }

            int length = Math.Min(Math.Max(1, maxBytes), bufferedChannelData.Length - offset);
            byte[] sliced = new byte[length];
            Array.Copy(bufferedChannelData, offset, sliced, 0, length);
            offset += length;
            state.ReadOffset = offset;
            isFinalChunk = offset >= bufferedChannelData.Length;
            talkData = sliced;
            return true;
        }

        /// <summary>
        /// Attempts to prepare standard read channel and reports whether it succeeded.
        /// </summary>
        private bool TryPrepareStandardReadChannel(byte channel)
        {
            if (channel == 15)
            {
                byte[] statusBytes;
                return TryResolveChannelData(channel, out statusBytes) && statusBytes != null;
            }

            DriveChannel state;
            if (TryGetChannel(channel, out state) && state.SequentialReader != null)
            {
                return true;
            }

            byte[] channelData;
            return TryResolveChannelData(channel, out channelData) ||
                (TryGetChannel(channel, out state) && state.SequentialReader != null);
        }

        /// <summary>
        /// Attempts to resolve channel data and reports whether it succeeded.
        /// </summary>
        private bool TryResolveChannelData(byte channel, out byte[] channelData)
        {
            channelData = null;
            DriveChannel state = GetOrCreateChannel(channel);

            if (channel == 15)
            {
                channelData = state.ReadBuffer;
                if (channelData == null || channelData.Length == 0)
                {
                    channelData = EncodeStatusText(_statusText);
                    SetChannelReadBuffer(channel, channelData);
                }

                return true;
            }

            channelData = state.ReadBuffer;
            if (channelData != null && channelData.Length > 0)
            {
                return true;
            }

            if (state.SequentialReader != null)
            {
                return false;
            }

            if (_mountedImage == null)
            {
                SetStatus(new CommandResult(0x08, "74, DRIVE NOT READY,00,00"));
                return false;
            }

            string filename = DecodeFilename(state.FilenameBytes);
            if (filename.StartsWith("$", StringComparison.Ordinal))
            {
                if (!_mountedImage.TryBuildDirectoryListing(out channelData) || channelData == null)
                {
                    SetStatus(new CommandResult(0x08, "74, DRIVE NOT READY,00,00"));
                    return false;
                }
            }
            else
            {
                D64Image.SequentialFileReader reader;
                if (!_mountedImage.TryOpenSequentialFile(filename, out reader) || reader == null)
                {
                    SetStatus(new CommandResult(0x04, "62, FILE NOT FOUND,00,00"));
                    return false;
                }
                state.ResetReadSource();
                state.SequentialReader = reader;
                return true;
            }

            SetChannelReadBuffer(channel, channelData);
            BeginSimulatedTransfer(channelData.Length);
            return true;
        }

        /// <summary>
        /// Executes memory write.
        /// </summary>
        private CommandResult ExecuteMemoryWrite(byte[] commandBytes)
        {
            if (commandBytes.Length < 6)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            ushort address = (ushort)(commandBytes[3] | (commandBytes[4] << 8));
            int count = commandBytes[5];
            if (count < 0 || commandBytes.Length < 6 + count)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            _memoryWriteCommandCount++;
            _lastMemoryWriteAddress = address;
            _lastMemoryWriteLength = count;

            for (int index = 0; index < count; index++)
            {
                ushort currentAddress = (ushort)(address + index);
                byte currentValue = commandBytes[6 + index];
                if (currentAddress < 0x1800)
                {
                    _ram[currentAddress & 0x07FF] = currentValue;
                }

                _hardware.WriteMemory(currentAddress, currentValue);
            }

            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Executes memory read.
        /// </summary>
        private CommandResult ExecuteMemoryRead(byte[] commandBytes)
        {
            if (commandBytes.Length < 6)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            ushort address = (ushort)(commandBytes[3] | (commandBytes[4] << 8));
            int count = commandBytes[5];
            if (count <= 0)
            {
                count = 1;
            }

            byte[] readBuffer = new byte[count];
            for (int index = 0; index < count; index++)
            {
                ushort currentAddress = (ushort)(address + index);
                byte value = _hardware.ReadMemory(currentAddress);
                if (currentAddress < 0x1800)
                {
                    value = _ram[currentAddress & 0x07FF];
                }

                readBuffer[index] = value;
            }

            return SetStatus(new CommandResult(0x00, "00, OK,00,00", readBuffer));
        }

        /// <summary>
        /// Executes memory execute.
        /// </summary>
        private CommandResult ExecuteMemoryExecute(byte[] commandBytes)
        {
            if (commandBytes.Length < 5)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            _pendingCustomExecution = PendingCustomExecutionType.ExecuteAddress;
            _pendingExecuteAddress = (ushort)(commandBytes[3] | (commandBytes[4] << 8));
            _pendingUserCommand = 0;
            _lastExecuteAddress = _pendingExecuteAddress;
            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Executes user command.
        /// </summary>
        private CommandResult ExecuteUserCommand(byte[] commandBytes)
        {
            byte command = commandBytes.Length > 1 ? (byte)char.ToUpperInvariant((char)commandBytes[1]) : (byte)0x00;
            switch (command)
            {
                case (byte)'1':
                    return ExecuteUserBlockRead(commandBytes);
                case (byte)'2':
                    return ExecuteUserBlockWrite(commandBytes);
                case (byte)'3':
                case (byte)'C':
                    return ExecuteUserBufferEntry(command);
                case (byte)'4':
                case (byte)'D':
                    return ExecuteUserBufferEntry(command);
                case (byte)'5':
                case (byte)'E':
                    return ExecuteUserBufferEntry(command);
                case (byte)'6':
                case (byte)'F':
                    return ExecuteUserBufferEntry(command);
                case (byte)'7':
                case (byte)'G':
                    return ExecuteUserBufferEntry(command);
                case (byte)'8':
                case (byte)'H':
                    return ExecuteUserBufferEntry(command);
                case (byte)'9':
                case (byte)'I':
                    InitializeDriveState(true);
                    return SetStatus(new CommandResult(0x00, "73, CBM DOS V2.6 1541,00,00"));
                case (byte)'J':
                case (byte)';':
                    InitializeDriveState(true);
                    return SetStatus(new CommandResult(0x00, "73, CBM DOS V2.6 1541,00,00"));
                default:
                    return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
            }
        }

        /// <summary>
        /// Executes user buffer entry.
        /// </summary>
        private CommandResult ExecuteUserBufferEntry(byte command)
        {
            _pendingCustomExecution = PendingCustomExecutionType.UserCommand;
            _pendingUserCommand = command;
            _pendingExecuteAddress = 0;
            _lastExecuteAddress = 0;
            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Handles the enter custom code mode operation.
        /// </summary>
        private void EnterCustomCodeMode()
        {
            _deviceAttentioned = false;
            _deviceListening = false;
            _deviceTalking = false;
            _payloadReceiveArmed = false;
            _payloadSendArmed = false;
            _channel = 0;
            _listeningSecondaryAddress = 0;
            _talkSecondaryAddress = 0;
            _talkBuffer = Array.Empty<byte>();
            _talkBufferIndex = 0;
            _listenBuffer.Clear();
            _receiver.Reset();
            _sender.Reset();
            _postPayloadReadyHoldCycles = 0;
            _attentionHandshakeState = AttentionHandshakeState.Idle;
            ReleaseLines();
        }

        /// <summary>
        /// Attempts to start pending custom execution and reports whether it succeeded.
        /// </summary>
        private bool TryStartPendingCustomExecution(bool atnLow)
        {
            if (_pendingCustomExecution == PendingCustomExecutionType.None)
            {
                _pendingCustomExecutionArmed = false;
                _pendingCustomExecutionWaitForAtnRelease = false;
                _pendingCustomExecutionIdleCycles = 0;
                _pendingCustomExecutionWaitCycles = 0;
                return false;
            }

            if (_hardware.IsBooting)
            {
                return false;
            }

            if (!_pendingCustomExecutionArmed)
            {
                if (!IsSoftwareTransportIdleForCustomExecution())
                {
                    _pendingCustomExecutionIdleCycles = 0;
                    return false;
                }

                _pendingCustomExecutionIdleCycles++;
                if (_pendingCustomExecutionIdleCycles < CustomExecutionArmIdleCycles)
                {
                    return false;
                }

                _pendingCustomExecutionArmed = true;
                _pendingCustomExecutionWaitForAtnRelease = false;
                _pendingCustomExecutionIdleCycles = 0;
                _pendingCustomExecutionWaitCycles = 0;
                return false;
            }

            if (_pendingCustomExecutionWaitForAtnRelease)
            {
                if (atnLow)
                {
                    _pendingCustomExecutionWaitCycles++;
                    // Do not start uploaded/custom drive code while ATN is
                    // still asserted. The command channel and the custom
                    // loader share the same serial VIA state; starting early
                    // lets the uploaded code observe the tail end of the
                    // command phase and immediately deadlock in the ROM ATN
                    // handler instead of taking over after UNLISTEN.
                    //
                    // Some protected loaders keep ATN asserted a little
                    // longer while they immediately switch their C64 side over
                    // to a custom serial routine. Waiting forever in that
                    // case wedges the handover even though the command channel
                    // itself is already quiesced. Once we have waited a full
                    // timeout on an otherwise idle software transport, hand
                    // control to the uploaded drive code anyway and let it
                    // own the IEC lines from there on.
                    if (_pendingCustomExecutionWaitCycles >= CustomExecutionAtnReleaseTimeoutCycles)
                    {
                        if (_pendingCustomExecution == PendingCustomExecutionType.UserCommand)
                        {
                            return false;
                        }

                        if (CustomExecutionStartGate != null && !CustomExecutionStartGate())
                        {
                            return false;
                        }

                        return StartPendingCustomExecutionNow();
                    }

                    return false;
                }

                _pendingCustomExecutionWaitForAtnRelease = false;
                _pendingCustomExecutionIdleCycles = 0;
                _pendingCustomExecutionWaitCycles = 0;
                return false;
            }

            bool dataLow = _port.IsLineLow(IecBusLine.Data);
            bool clockLow = _port.IsLineLow(IecBusLine.Clock);
            bool c64DataLow = IsC64DrivingDataLow();
            bool c64ClockLow = IsC64DrivingClockLow();
            bool busActivityStarted = atnLow || dataLow || clockLow;

            if (_pendingCustomExecution == PendingCustomExecutionType.UserCommand)
            {
                if (atnLow)
                {
                    return false;
                }

                if (CustomExecutionStartGate != null && !CustomExecutionStartGate())
                {
                    return false;
                }

                return StartPendingCustomExecutionNow();
            }

            // After the command channel released ATN, many custom loaders
            // immediately begin their own protocol by pulling DATA/CLOCK low
            // again. Waiting for the whole bus to become idle at that point is
            // too strict and keeps the uploaded drive code from ever taking
            // over. Use the first fresh post-ATN activity as the handover cue.
            if (!atnLow && (c64DataLow || c64ClockLow))
            {
                if (CustomExecutionStartGate != null && !CustomExecutionStartGate())
                {
                    return false;
                }

                return StartPendingCustomExecutionNow();
            }

            if (IsSoftwareTransportIdleForCustomExecution())
            {
                _pendingCustomExecutionIdleCycles++;
            }
            else if (!atnLow)
            {
                _pendingCustomExecutionIdleCycles = 0;
                return false;
            }

            if (atnLow && _pendingCustomExecutionIdleCycles <= 0)
            {
                return false;
            }

            if (!busActivityStarted && _pendingCustomExecutionIdleCycles < CustomExecutionFallbackStartCycles)
            {
                return false;
            }

            if (atnLow)
            {
                return false;
            }

            if (CustomExecutionStartGate != null && !CustomExecutionStartGate())
            {
                return false;
            }

            return StartPendingCustomExecutionNow();
        }

        /// <summary>
        /// Returns whether c64 driving data low is true.
        /// </summary>
        private bool IsC64DrivingDataLow()
        {
            return _port.IsOwnerDrivingLineLow("C64", IecBusLine.Data);
        }

        /// <summary>
        /// Returns whether c64 driving clock low is true.
        /// </summary>
        private bool IsC64DrivingClockLow()
        {
            return _port.IsOwnerDrivingLineLow("C64", IecBusLine.Clock);
        }

        /// <summary>
        /// Handles the arm pending custom execution after command channel operation.
        /// </summary>
        private void ArmPendingCustomExecutionAfterCommandChannel()
        {
            if (_pendingCustomExecution == PendingCustomExecutionType.None)
            {
                return;
            }

            // Once a command like U3/M-E has been accepted on channel 15, the
            // software IEC transport must get completely out of the way. The
            // following bytes on the bus belong to the uploaded fast/custom
            // loader and must not be consumed by the standard command-channel
            // receiver.
            QuiesceSoftwareTransportForPendingCustomExecution();
            _pendingCustomExecutionArmed = true;
            // U3/M-E are accepted while the command channel still runs under
            // ATN. Starting the uploaded drive code before the command phase is
            // over lets it see the command-channel bus state and deadlock.
            // Wait for ATN to release if it is still asserted right now; after
            // that, the first new DATA/CLOCK activity will hand over to the
            // uploaded loader.
            _pendingCustomExecutionWaitForAtnRelease = _port.IsLineLow(IecBusLine.Atn);
            _pendingCustomExecutionIdleCycles = 0;
            _pendingCustomExecutionWaitCycles = 0;
        }

        /// <summary>
        /// Starts pending custom execution now.
        /// </summary>
        private bool StartPendingCustomExecutionNow()
        {
            PendingCustomExecutionType executionType = _pendingCustomExecution;
            ushort executeAddress = _pendingExecuteAddress;
            byte userCommand = _pendingUserCommand;

            if (executionType == PendingCustomExecutionType.None)
            {
                return false;
            }

            _pendingCustomExecution = PendingCustomExecutionType.None;
            _pendingExecuteAddress = 0;
            _pendingUserCommand = 0;
            _pendingCustomExecutionArmed = false;
            _pendingCustomExecutionWaitForAtnRelease = false;
            _pendingCustomExecutionIdleCycles = 0;
            _pendingCustomExecutionWaitCycles = 0;

            EnterCustomCodeMode();

            if (executionType == PendingCustomExecutionType.ExecuteAddress)
            {
                _lastExecuteAddress = executeAddress;
                _hardware.ExecuteAt(executeAddress);
                return true;
            }

            if (executionType == PendingCustomExecutionType.UserCommand)
            {
                _hardware.ExecuteUserCommand(userCommand);
                _lastExecuteAddress = _hardware.ProgramCounter;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handles the quiesce software transport for pending custom execution operation.
        /// </summary>
        private void QuiesceSoftwareTransportForPendingCustomExecution()
        {
            _deviceAttentioned = false;
            _deviceListening = false;
            _deviceTalking = false;
            _payloadReceiveArmed = false;
            _payloadSendArmed = false;
            _armPayloadReceiveAfterAttention = false;
            _armPayloadSendAfterAttention = false;
            _passiveListening = false;
            _passiveTalking = false;
            _passivePayloadReceiveArmed = false;
            _passiveArmPayloadReceiveAfterAttention = false;
            _channel = 0;
            _passiveChannel = 0;
            _listeningSecondaryAddress = 0;
            _talkSecondaryAddress = 0;
            _talkBuffer = Array.Empty<byte>();
            _talkBufferIndex = 0;
            _listenBuffer.Clear();
            _receiver.Reset();
            _passiveReceiver.Reset();
            _sender.Reset();
            _postPayloadReadyHoldCycles = 0;
            _presenceProbeHoldActive = false;
            _presenceProbeObservedClockLow = false;
            _presenceProbeHoldCycles = 0;
            _continueAttentionCommandAfterRelease = false;
            _attentionPostReleaseWaitCycles = 0;
            _attentionHandshakeState = AttentionHandshakeState.Idle;
            ReleaseLines();
        }

        /// <summary>
        /// Returns whether software transport idle for custom execution is true.
        /// </summary>
        private bool IsSoftwareTransportIdleForCustomExecution()
        {
            if (_port.IsLineLow(IecBusLine.Atn) ||
                _port.IsLineLow(IecBusLine.Clock) ||
                _port.IsLineLow(IecBusLine.Data))
            {
                return false;
            }

            if (_deviceListening ||
                _deviceTalking ||
                _passiveListening ||
                _passiveTalking ||
                _payloadReceiveArmed ||
                _payloadSendArmed ||
                _armPayloadReceiveAfterAttention ||
                _armPayloadSendAfterAttention ||
                _passivePayloadReceiveArmed ||
                _passiveArmPayloadReceiveAfterAttention ||
                _postPayloadReadyHoldCycles > 0 ||
                _presenceProbeHoldActive ||
                _continueAttentionCommandAfterRelease ||
                _attentionHandshakeState != AttentionHandshakeState.Idle ||
                !_receiver.IsIdle ||
                !_passiveReceiver.IsIdle ||
                !_sender.IsIdle)
            {
                return false;
            }

            DriveChannel commandChannel;
            if (TryGetChannel(15, out commandChannel) &&
                commandChannel.WriteBuffer != null &&
                commandChannel.WriteBuffer.Count > 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns whether software transport internally idle for custom execution is true.
        /// </summary>
        private bool IsSoftwareTransportInternallyIdleForCustomExecution()
        {
            if (_deviceListening ||
                _deviceTalking ||
                _passiveListening ||
                _passiveTalking ||
                _payloadReceiveArmed ||
                _payloadSendArmed ||
                _armPayloadReceiveAfterAttention ||
                _armPayloadSendAfterAttention ||
                _passivePayloadReceiveArmed ||
                _passiveArmPayloadReceiveAfterAttention ||
                _postPayloadReadyHoldCycles > 0 ||
                _presenceProbeHoldActive ||
                _continueAttentionCommandAfterRelease ||
                _attentionHandshakeState != AttentionHandshakeState.Idle ||
                !_receiver.IsIdle ||
                !_passiveReceiver.IsIdle ||
                !_sender.IsIdle)
            {
                return false;
            }

            DriveChannel commandChannel;
            if (TryGetChannel(15, out commandChannel) &&
                commandChannel.WriteBuffer != null &&
                commandChannel.WriteBuffer.Count > 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Handles the mirror command to input buffer operation.
        /// </summary>
        private void MirrorCommandToInputBuffer(byte[] commandBytes)
        {
            if (commandBytes == null)
            {
                return;
            }

            int start = 0x0200;
            int commandLength = Math.Min(commandBytes.Length, 255);
            int mirroredLength = Math.Min(commandLength + 1, _ram.Length - start);
            if (mirroredLength <= 0)
            {
                return;
            }

            byte[] mirrored = new byte[mirroredLength];
            Array.Copy(commandBytes, 0, mirrored, 0, Math.Min(commandLength, mirroredLength));
            mirrored[mirroredLength - 1] = 0x00;

            Array.Copy(mirrored, 0, _ram, start, mirroredLength);
            _hardware.UploadMemory((ushort)start, mirrored);
        }

        /// <summary>
        /// Handles the reset command state operation.
        /// </summary>
        private void ResetCommandState()
        {
            ResetChannels();
            _listenBuffer.Clear();
            _lastExecuteAddress = 0;
            _lastCommandText = string.Empty;
            _recentCommandTexts.Clear();
            _memoryWriteCommandCount = 0;
            _lastMemoryWriteAddress = 0;
            _lastMemoryWriteLength = 0;
            _statusCode = 0x00;
            _statusText = "00, OK,00,00";
            _hardware.StopCustomCode();
        }

        /// <summary>
        /// Handles the initialize drive state operation.
        /// </summary>
        private void InitializeDriveState(bool rebootRom)
        {
            ResetChannels();
            _listenBuffer.Clear();
            _receiver.Reset();
            _sender.Reset();
            _deviceAttentioned = false;
            _deviceListening = false;
            _deviceTalking = false;
            _payloadReceiveArmed = false;
            _payloadSendArmed = false;
            _armPayloadReceiveAfterAttention = false;
            _armPayloadSendAfterAttention = false;
            _pendingCustomExecution = PendingCustomExecutionType.None;
            _pendingExecuteAddress = 0;
            _pendingUserCommand = 0;
            _lastExecuteAddress = 0;
            _lastCommandText = string.Empty;
            _recentCommandTexts.Clear();
            _memoryWriteCommandCount = 0;
            _lastMemoryWriteAddress = 0;
            _lastMemoryWriteLength = 0;
            _pendingCustomExecutionArmed = false;
            _pendingCustomExecutionWaitForAtnRelease = false;
            _pendingCustomExecutionIdleCycles = 0;
            _pendingCustomExecutionWaitCycles = 0;
            _postPayloadReadyHoldCycles = 0;
            _presenceProbeHoldActive = false;
            _presenceProbeObservedClockLow = false;
            _presenceProbeHoldCycles = 0;
            _continueAttentionCommandAfterRelease = false;
            _attentionPostReleaseWaitCycles = 0;
            _attentionHandshakeState = AttentionHandshakeState.Idle;
            _statusCode = 0x00;
            _statusText = "00, OK,00,00";
            ReleaseLines();

            if (rebootRom && _hardware.HasBootableRom)
            {
                _hardware.BeginRomBoot();
            }
            else
            {
                _hardware.StopCustomCode();
            }
        }

        /// <summary>
        /// Processes command channel if needed.
        /// </summary>
        private void ProcessCommandChannelIfNeeded()
        {
            DriveChannel state;
            if (_channel != 15 || !TryGetChannel(15, out state) || state.FilenameBytes == null || state.FilenameBytes.Length == 0)
            {
                return;
            }

            byte[] payload = state.FilenameBytes;
            int consumedBytes;
            CommandResult lastResult = ExecuteCommandPayload(payload, true, out consumedBytes);

            state.FilenameBytes = Array.Empty<byte>();
            SetChannelReadBuffer(15, lastResult.ReadBuffer.Length > 0
                ? lastResult.ReadBuffer
                : EncodeStatusText(_statusText));
        }

        /// <summary>
        /// Processes kernal command channel.
        /// </summary>
        private void ProcessKernalCommandChannel(byte channel, bool finalize)
        {
            DriveChannel state;
            if (channel != 15 || !TryGetChannel(channel, out state) || state.WriteBuffer == null || state.WriteBuffer.Count == 0)
            {
                return;
            }

            List<byte> payload = state.WriteBuffer;
            byte[] payloadBytes = payload.ToArray();
            int consumedBytes;
            CommandResult lastResult = ExecuteCommandPayload(payloadBytes, finalize, out consumedBytes);
            if (consumedBytes <= 0 && !finalize)
            {
                return;
            }

            if (consumedBytes > 0)
            {
                payload.RemoveRange(0, Math.Min(consumedBytes, payload.Count));
            }

            SetChannelReadBuffer(channel, lastResult.ReadBuffer.Length > 0
                ? lastResult.ReadBuffer
                : EncodeStatusText(_statusText));
            if (finalize)
            {
                payload.Clear();
            }
        }

        /// <summary>
        /// Executes command payload.
        /// </summary>
        private CommandResult ExecuteCommandPayload(byte[] payload, bool finalize, out int consumedBytes)
        {
            consumedBytes = 0;
            CommandResult lastResult = new CommandResult(_statusCode, _statusText);
            if (payload == null || payload.Length == 0)
            {
                return lastResult;
            }

            int index = 0;
            while (index < payload.Length)
            {
                while (index < payload.Length && payload[index] == 0x0D)
                {
                    index++;
                    consumedBytes = index;
                }

                if (index >= payload.Length)
                {
                    break;
                }

                if (IsMemoryWriteCommandAt(payload, index))
                {
                    int remaining = payload.Length - index;
                    if (remaining < 6)
                    {
                        if (!finalize)
                        {
                            break;
                        }

                        byte[] incomplete = CopyCommandSlice(payload, index, remaining);
                        lastResult = ExecuteCommand(incomplete, false);
                        index = payload.Length;
                        consumedBytes = index;
                        break;
                    }

                    int count = payload[index + 5];
                    int commandLength = 6 + count;
                    if (remaining < commandLength)
                    {
                        if (!finalize)
                        {
                            break;
                        }

                        byte[] incomplete = CopyCommandSlice(payload, index, remaining);
                        lastResult = ExecuteCommand(incomplete, false);
                        index = payload.Length;
                        consumedBytes = index;
                        break;
                    }

                    byte[] command = CopyCommandSlice(payload, index, commandLength);
                    lastResult = ExecuteCommand(command, false);
                    index += commandLength;
                    consumedBytes = index;
                    continue;
                }

                int end = index;
                while (end < payload.Length && payload[end] != 0x0D)
                {
                    end++;
                }

                if (end >= payload.Length && !finalize)
                {
                    break;
                }

                int length = end - index;
                if (length > 0)
                {
                    byte[] command = CopyCommandSlice(payload, index, length);
                    lastResult = ExecuteCommand(command, true);
                }

                index = end < payload.Length ? end + 1 : end;
                consumedBytes = index;
            }

            return lastResult;
        }

        /// <summary>
        /// Handles the copy command slice operation.
        /// </summary>
        private static byte[] CopyCommandSlice(byte[] payload, int start, int length)
        {
            if (length <= 0)
            {
                return Array.Empty<byte>();
            }

            byte[] command = new byte[length];
            Array.Copy(payload, start, command, 0, length);
            return command;
        }

        /// <summary>
        /// Sets the status value.
        /// </summary>
        private CommandResult SetStatus(CommandResult result)
        {
            _statusCode = result.Status;
            if (!string.IsNullOrWhiteSpace(result.StatusText))
            {
                _statusText = result.StatusText;
            }

            return result;
        }

        /// <summary>
        /// Handles the matches initialize command operation.
        /// </summary>
        private static bool MatchesInitializeCommand(byte[] commandBytes)
        {
            if (commandBytes.Length == 0)
            {
                return false;
            }

            if (commandBytes[0] != (byte)'I')
            {
                return false;
            }

            return commandBytes.Length == 1 || (commandBytes.Length == 2 && commandBytes[1] == (byte)'0');
        }

        /// <summary>
        /// Returns whether memory write command is true.
        /// </summary>
        private static bool IsMemoryWriteCommand(byte[] commandBytes)
        {
            return IsMemoryWriteCommandAt(commandBytes, 0);
        }

        /// <summary>
        /// Returns whether memory write command at is true.
        /// </summary>
        private static bool IsMemoryWriteCommandAt(byte[] commandBytes, int index)
        {
            return commandBytes != null &&
                index >= 0 &&
                commandBytes.Length >= index + 3 &&
                ToAsciiUpper(commandBytes[index]) == (byte)'M' &&
                commandBytes[index + 1] == (byte)'-' &&
                ToAsciiUpper(commandBytes[index + 2]) == (byte)'W';
        }

        /// <summary>
        /// Returns whether memory read command is true.
        /// </summary>
        private static bool IsMemoryReadCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 3 &&
                ToAsciiUpper(commandBytes[0]) == (byte)'M' &&
                commandBytes[1] == (byte)'-' &&
                ToAsciiUpper(commandBytes[2]) == (byte)'R';
        }

        /// <summary>
        /// Returns whether memory execute command is true.
        /// </summary>
        private static bool IsMemoryExecuteCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 3 &&
                ToAsciiUpper(commandBytes[0]) == (byte)'M' &&
                commandBytes[1] == (byte)'-' &&
                ToAsciiUpper(commandBytes[2]) == (byte)'E';
        }

        /// <summary>
        /// Returns whether user command is true.
        /// </summary>
        private static bool IsUserCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 2 && ToAsciiUpper(commandBytes[0]) == (byte)'U';
        }

        /// <summary>
        /// Handles the to ascii upper operation.
        /// </summary>
        private static byte ToAsciiUpper(byte value)
        {
            return value >= (byte)'a' && value <= (byte)'z'
                ? (byte)(value - 0x20)
                : value;
        }

        /// <summary>
        /// Returns whether block read command is true.
        /// </summary>
        private static bool IsBlockReadCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 3 &&
                commandBytes[0] == (byte)'B' &&
                commandBytes[1] == (byte)'-' &&
                (commandBytes[2] == (byte)'R' || commandBytes[2] == (byte)'r');
        }

        /// <summary>
        /// Returns whether block pointer command is true.
        /// </summary>
        private static bool IsBlockPointerCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 3 &&
                commandBytes[0] == (byte)'B' &&
                commandBytes[1] == (byte)'-' &&
                (commandBytes[2] == (byte)'P' || commandBytes[2] == (byte)'p');
        }

        /// <summary>
        /// Returns whether block write command is true.
        /// </summary>
        private static bool IsBlockWriteCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 3 &&
                commandBytes[0] == (byte)'B' &&
                commandBytes[1] == (byte)'-' &&
                (commandBytes[2] == (byte)'W' || commandBytes[2] == (byte)'w');
        }

        /// <summary>
        /// Returns whether block allocate or free command is true.
        /// </summary>
        private static bool IsBlockAllocateOrFreeCommand(byte[] commandBytes)
        {
            return commandBytes.Length >= 3 &&
                commandBytes[0] == (byte)'B' &&
                commandBytes[1] == (byte)'-' &&
                ((commandBytes[2] == (byte)'A' || commandBytes[2] == (byte)'a') ||
                 (commandBytes[2] == (byte)'F' || commandBytes[2] == (byte)'f'));
        }

        /// <summary>
        /// Executes user block read.
        /// </summary>
        private CommandResult ExecuteUserBlockRead(byte[] commandBytes)
        {
            int[] values;
            if (!TryParseNumericCommand(commandBytes, 2, out values) || values.Length < 4)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            return LoadSectorIntoChannel(values[0], values[2], values[3]);
        }

        /// <summary>
        /// Executes user block write.
        /// </summary>
        private CommandResult ExecuteUserBlockWrite(byte[] commandBytes)
        {
            int[] values;
            if (!TryParseNumericCommand(commandBytes, 2, out values) || values.Length < 4)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            return WriteChannelToSector(values[0], values[2], values[3]);
        }

        /// <summary>
        /// Executes block allocate or free.
        /// </summary>
        private CommandResult ExecuteBlockAllocateOrFree(byte[] commandBytes)
        {
            if (_mountedImage == null)
            {
                return SetStatus(new CommandResult(0x08, "74, DRIVE NOT READY,00,00"));
            }

            if (_mountedImage.IsReadOnly)
            {
                return SetStatus(new CommandResult(0x08, "26, WRITE PROTECT ON,00,00"));
            }

            int[] values;
            if (!TryParseNumericCommand(commandBytes, 3, out values) || values.Length < 3)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            int track = values[1];
            int sector = values[2];
            bool allocate = commandBytes[2] == (byte)'A' || commandBytes[2] == (byte)'a';
            bool ok = allocate
                ? _mountedImage.TryAllocateSector(track, sector)
                : _mountedImage.TryFreeSector(track, sector);

            if (!ok)
            {
                return SetStatus(new CommandResult(0x14, "66, ILLEGAL TRACK OR SECTOR,00,00"));
            }

            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Executes block read.
        /// </summary>
        private CommandResult ExecuteBlockRead(byte[] commandBytes)
        {
            int[] values;
            if (!TryParseNumericCommand(commandBytes, 3, out values) || values.Length < 4)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            return LoadSectorIntoChannel(values[0], values[2], values[3]);
        }

        /// <summary>
        /// Executes block write.
        /// </summary>
        private CommandResult ExecuteBlockWrite(byte[] commandBytes)
        {
            int[] values;
            if (!TryParseNumericCommand(commandBytes, 3, out values) || values.Length < 4)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            return WriteChannelToSector(values[0], values[2], values[3]);
        }

        /// <summary>
        /// Executes block pointer.
        /// </summary>
        private CommandResult ExecuteBlockPointer(byte[] commandBytes)
        {
            int[] values;
            if (!TryParseNumericCommand(commandBytes, 3, out values) || values.Length < 2)
            {
                return new CommandResult(0x10, "30, SYNTAX ERROR,00,00");
            }

            byte channel = (byte)(values[0] & 0x0F);
            GetOrCreateChannel(channel).ReadOffset = Math.Max(0, values[1]);
            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Loads sector into channel.
        /// </summary>
        private CommandResult LoadSectorIntoChannel(int channelNumber, int track, int sector)
        {
            if (_mountedImage == null)
            {
                return SetStatus(new CommandResult(0x08, "74, DRIVE NOT READY,00,00"));
            }

            if (TryCreateSectorErrorStatus(track, sector, out CommandResult sectorError))
            {
                return SetStatus(sectorError);
            }

            byte[] sectorBytes;
            if (!_mountedImage.TryReadSector(track, sector, out sectorBytes) || sectorBytes == null)
            {
                return SetStatus(new CommandResult(0x14, "66, ILLEGAL TRACK OR SECTOR,00,00"));
            }

            byte channel = (byte)(channelNumber & 0x0F);
            SetChannelReadBuffer(channel, sectorBytes);
            BeginSimulatedTransfer(sectorBytes.Length);
            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Writes channel to sector.
        /// </summary>
        private CommandResult WriteChannelToSector(int channelNumber, int track, int sector)
        {
            if (_mountedImage == null)
            {
                return SetStatus(new CommandResult(0x08, "74, DRIVE NOT READY,00,00"));
            }

            if (_mountedImage.IsReadOnly)
            {
                return SetStatus(new CommandResult(0x08, string.Format("26, WRITE PROTECT ON,{0:00},{1:00}", track, sector)));
            }

            if (TryCreateSectorErrorStatus(track, sector, out CommandResult sectorError))
            {
                return SetStatus(sectorError);
            }

            byte channel = (byte)(channelNumber & 0x0F);
            DriveChannel state;
            if (!TryGetChannel(channel, out state))
            {
                return SetStatus(new CommandResult(0x03, "70, NO CHANNEL,00,00"));
            }

            byte[] bufferedChannelData = state.WriteBuffer != null && state.WriteBuffer.Count > 0
                ? state.WriteBuffer.ToArray()
                : state.ReadBuffer;
            if (bufferedChannelData == null)
            {
                return SetStatus(new CommandResult(0x03, "70, NO CHANNEL,00,00"));
            }

            int offset = state.ReadOffset;

            if (offset < 0)
            {
                offset = 0;
            }

            byte[] sectorBytes = new byte[256];
            if (offset < bufferedChannelData.Length)
            {
                int copyLength = Math.Min(256, bufferedChannelData.Length - offset);
                Array.Copy(bufferedChannelData, offset, sectorBytes, 0, copyLength);
            }

            if (!_mountedImage.TryWriteSector(track, sector, sectorBytes))
            {
                return SetStatus(new CommandResult(0x14, "66, ILLEGAL TRACK OR SECTOR,00,00"));
            }

            SetChannelReadBuffer(channel, sectorBytes);
            state.WriteBuffer = null;
            BeginSimulatedTransfer(256);
            return SetStatus(new CommandResult(0x00, "00, OK,00,00"));
        }

        /// <summary>
        /// Attempts to create sector error status and reports whether it succeeded.
        /// </summary>
        private bool TryCreateSectorErrorStatus(int track, int sector, out CommandResult result)
        {
            result = null;
            if (_mountedImage == null)
            {
                return false;
            }

            if (!_mountedImage.TryGetSectorErrorCode(track, sector, out byte errorCode))
            {
                return false;
            }

            switch (errorCode)
            {
                case 0x01:
                    return false;
                case 0x02:
                    result = new CommandResult(0x02, string.Format("20, READ ERROR,{0:00},{1:00}", track, sector));
                    return true;
                case 0x03:
                    result = new CommandResult(0x03, string.Format("21, READ ERROR,{0:00},{1:00}", track, sector));
                    return true;
                case 0x04:
                    result = new CommandResult(0x04, string.Format("22, READ ERROR,{0:00},{1:00}", track, sector));
                    return true;
                case 0x05:
                    result = new CommandResult(0x05, string.Format("23, READ ERROR,{0:00},{1:00}", track, sector));
                    return true;
                case 0x08:
                    result = new CommandResult(0x08, string.Format("26, WRITE PROTECT ON,{0:00},{1:00}", track, sector));
                    return true;
                case 0x09:
                    result = new CommandResult(0x09, string.Format("27, READ ERROR,{0:00},{1:00}", track, sector));
                    return true;
                case 0x0B:
                    result = new CommandResult(0x0B, string.Format("29, DISK ID MISMATCH,{0:00},{1:00}", track, sector));
                    return true;
                case 0x0F:
                    result = new CommandResult(0x08, "74, DRIVE NOT READY,00,00");
                    return true;
                default:
                    result = new CommandResult(0x14, "66, ILLEGAL TRACK OR SECTOR,00,00");
                    return true;
            }
        }

        /// <summary>
        /// Attempts to parse numeric command and reports whether it succeeded.
        /// </summary>
        private static bool TryParseNumericCommand(byte[] commandBytes, int prefixLength, out int[] values)
        {
            values = Array.Empty<int>();
            if (commandBytes == null || commandBytes.Length <= prefixLength)
            {
                return false;
            }

            string text = Encoding.ASCII.GetString(commandBytes);
            int colonIndex = text.IndexOf(':');
            string numericPart = colonIndex >= 0 && colonIndex < text.Length - 1
                ? text.Substring(colonIndex + 1)
                : text.Substring(prefixLength);
            numericPart = numericPart.Trim();
            if (numericPart.Length == 0)
            {
                return false;
            }

            string[] parts = numericPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var parsed = new int[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                int value;
                if (!int.TryParse(parts[index].Trim(), out value))
                {
                    return false;
                }

                parsed[index] = value;
            }

            values = parsed;
            return true;
        }

        /// <summary>
        /// Lists the supported pending custom execution type values.
        /// </summary>
        private enum PendingCustomExecutionType
        {
            None,
            ExecuteAddress,
            UserCommand
        }

        /// <summary>
        /// Handles the trim trailing carriage returns operation.
        /// </summary>
        private static byte[] TrimTrailingCarriageReturns(byte[] commandBytes)
        {
            if (commandBytes == null || commandBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            int length = commandBytes.Length;
            while (length > 0 && commandBytes[length - 1] == 0x0D)
            {
                length--;
            }

            if (length == commandBytes.Length)
            {
                return commandBytes;
            }

            byte[] trimmed = new byte[length];
            Array.Copy(commandBytes, 0, trimmed, 0, length);
            return trimmed;
        }

        /// <summary>
        /// Handles the describe command operation.
        /// </summary>
        private static string DescribeCommand(byte[] commandBytes)
        {
            if (commandBytes == null || commandBytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(commandBytes.Length * 2);
            for (int index = 0; index < commandBytes.Length; index++)
            {
                byte value = commandBytes[index];
                if (value >= 0x20 && value <= 0x7E)
                {
                    builder.Append((char)value);
                }
                else
                {
                    builder.Append('<');
                    builder.Append(value.ToString("X2"));
                    builder.Append('>');
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Handles the encode status text operation.
        /// </summary>
        private static byte[] EncodeStatusText(string text)
        {
            return Encoding.ASCII.GetBytes((text ?? string.Empty) + "\r");
        }

        /// <summary>
        /// Releases lines.
        /// </summary>
        private void ReleaseLines()
        {
            ReleaseClock();
            ReleaseData();
        }

        /// <summary>
        /// Releases clock.
        /// </summary>
        private void ReleaseClock()
        {
            _softwareClockLineLow = false;
            _port.SetLineLow(IecBusLine.Clock, false);
        }

        /// <summary>
        /// Pulls clock low.
        /// </summary>
        private void PullClockLow()
        {
            _softwareClockLineLow = true;
            _port.SetLineLow(IecBusLine.Clock, true);
        }

        /// <summary>
        /// Releases data.
        /// </summary>
        private void ReleaseData()
        {
            _softwareDataLineLow = false;
            _port.SetLineLow(IecBusLine.Data, false);
        }

        /// <summary>
        /// Pulls data low.
        /// </summary>
        private void PullDataLow()
        {
            _softwareDataLineLow = true;
            _port.SetLineLow(IecBusLine.Data, true);
        }

        /// <summary>
        /// Begins simulated transfer.
        /// </summary>
        public void BeginSimulatedTransfer(int byteCount)
        {
            int cycles = byteCount * LedTransferCyclesPerByte;
            if (cycles < LedMinTransferCycles)
            {
                cycles = LedMinTransferCycles;
            }
            else if (cycles > LedMaxTransferCycles)
            {
                cycles = LedMaxTransferCycles;
            }

            PulseLed(cycles);
        }

        /// <summary>
        /// Pulses led.
        /// </summary>
        private void PulseLed(int cycles)
        {
            if (cycles > _ledBusyCycles)
            {
                _ledBusyCycles = cycles;
            }
        }

        /// <summary>
        /// Advances the led state by one emulated tick.
        /// </summary>
        private void TickLed()
        {
            if (_ledBusyCycles > 0)
            {
                _ledBusyCycles--;
            }

            if (HasVisibleActivity)
            {
                _ledPhaseCycles++;
            }
            else
            {
                _ledPhaseCycles = 0;
            }
        }

        /// <summary>
        /// Decodes filename.
        /// </summary>
        private static string DecodeFilename(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Length);
            for (int index = 0; index < bytes.Length; index++)
            {
                byte value = bytes[index];
                if (value == 0x00)
                {
                    break;
                }

                if (value >= 0x20 && value <= 0x7E)
                {
                    builder.Append((char)value);
                }
                else if (value >= 0xC1 && value <= 0xDA)
                {
                    builder.Append((char)(value - 0x80));
                }
                else
                {
                    builder.Append((char)value);
                }
            }

            return builder.ToString().Trim().Trim('"');
        }

        /// <summary>
        /// Handles the describe open channel operation.
        /// </summary>
        private string DescribeOpenChannel(byte channel)
        {
            DriveChannel state;
            if (!TryGetChannel(channel, out state) || state.FilenameBytes == null)
            {
                return "-";
            }

            string filename = DecodeFilename(state.FilenameBytes);
            if (state.SequentialReader != null)
            {
                return string.Format(
                    "{0}:{1}@{2}{3}",
                    state.FilenameBytes.Length,
                    filename,
                    state.SequentialReader.BytesRead,
                    state.SequentialReader.IsFinished ? ":eof" : string.Empty);
            }

            if (state.ReadBuffer != null && state.ReadBuffer.Length > 0)
            {
                return string.Format("{0}:{1}@{2}/{3}", state.FilenameBytes.Length, filename, state.ReadOffset, state.ReadBuffer.Length);
            }

            return string.Format("{0}:{1}", state.FilenameBytes.Length, filename);
        }

        /// <summary>
        /// Formats recent commands.
        /// </summary>
        private string FormatRecentCommands()
        {
            if (_recentAttentionCommands.Count == 0)
            {
                return "-";
            }

            var builder = new StringBuilder();
            for (int index = 0; index < _recentAttentionCommands.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append('-');
                }

                builder.Append(_recentAttentionCommands[index].ToString("X2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Handles the add recent command text operation.
        /// </summary>
        private void AddRecentCommandText(string commandText)
        {
            if (string.IsNullOrEmpty(commandText))
            {
                return;
            }

            _recentCommandTexts.Add(commandText);
            while (_recentCommandTexts.Count > 32)
            {
                _recentCommandTexts.RemoveAt(0);
            }
        }

        /// <summary>
        /// Formats recent command texts.
        /// </summary>
        private string FormatRecentCommandTexts()
        {
            if (_recentCommandTexts.Count == 0)
            {
                return "-";
            }

            return string.Join("|", _recentCommandTexts.ToArray());
        }

        /// <summary>
        /// Represents the received iec byte component.
        /// </summary>
        private sealed class ReceivedIecByte
        {
            public byte Value;
            public bool IsEoi;
        }

        /// <summary>
        /// Lists the supported attention handshake state values.
        /// </summary>
        private enum AttentionHandshakeState
        {
            Idle,
            WaitClockLowForByteStart,
            WaitInterByteClockLowForByteStart,
            WaitInterByteClockHighForByteTransferStart,
            Receiving
        }

        /// <summary>
        /// Represents the iec byte receiver component.
        /// </summary>
        private sealed class IecByteReceiver
        {
            private readonly IecBusPort _port;
            private readonly bool _driveDataLine;
            private readonly List<byte> _payload = new List<byte>();
            private ReceiveState _state;
            private int _bitIndex;
            private int _waitCycles;
            private int _ackHoldCycles;
            private int _initialByteStartGraceCycles;
            private byte _currentByte;
            private bool _eoiPending;
            private bool _allowEoi;
            private byte _currentChannel;
            private bool _lastClockLow;
            private bool _hasCompletedByte;
            private byte _completedByte;
            private bool _completedEoi;

            /// <summary>
            /// Initializes a new IecByteReceiver instance.
            /// </summary>
            public IecByteReceiver(IecBusPort port, bool driveDataLine = true)
            {
                _port = port;
                _driveDataLine = driveDataLine;
                Reset();
            }

            /// <summary>
            /// Resets the component to its power-on or idle state.
            /// </summary>
            public void Reset()
            {
                _state = ReceiveState.WaitTalkerReady;
                _bitIndex = 0;
                _waitCycles = 0;
                _ackHoldCycles = 0;
                _currentByte = 0;
                _eoiPending = false;
                _allowEoi = false;
                _lastClockLow = _port.IsLineLow(IecBusLine.Clock);
                _hasCompletedByte = false;
                _completedByte = 0;
                _completedEoi = false;
                _payload.Clear();
                ReleaseData();
            }

            /// <summary>
            /// Begins payload.
            /// </summary>
            public void BeginPayload(byte channel)
            {
                _currentChannel = channel;
                _payload.Clear();
                PrepareForNextByte(PayloadInitialByteGraceCycles);
            }

            /// <summary>
            /// Handles the prepare for next byte operation.
            /// </summary>
            public void PrepareForNextByte()
            {
                PrepareForNextByte(0);
            }

            /// <summary>
            /// Handles the prepare for next byte operation.
            /// </summary>
            public void PrepareForNextByte(int initialByteStartGraceCycles)
            {
                _state = ReceiveState.WaitTalkerReady;
                _bitIndex = 0;
                _waitCycles = 0;
                _ackHoldCycles = 0;
                _initialByteStartGraceCycles = Math.Max(0, initialByteStartGraceCycles);
                _currentByte = 0;
                _eoiPending = false;
                _allowEoi = true;
                _lastClockLow = _port.IsLineLow(IecBusLine.Clock);
                _hasCompletedByte = false;
                _completedByte = 0;
                _completedEoi = false;
                PullDataLow();
            }

            /// <summary>
            /// Handles the prepare for command byte operation.
            /// </summary>
            public void PrepareForCommandByte()
            {
                _state = ReceiveState.WaitTalkerReady;
                _bitIndex = 0;
                _waitCycles = 0;
                _ackHoldCycles = 0;
                _initialByteStartGraceCycles = 0;
                _currentByte = 0;
                _eoiPending = false;
                _allowEoi = false;
                _lastClockLow = _port.IsLineLow(IecBusLine.Clock);
                _hasCompletedByte = false;
                _completedByte = 0;
                _completedEoi = false;
                PullDataLow();
            }

            /// <summary>
            /// Begins byte transfer.
            /// </summary>
            public void BeginByteTransfer(bool allowEoi, bool eoiPending = false)
            {
                _state = _port.IsLineLow(IecBusLine.Clock)
                    ? ReceiveState.WaitClockHigh
                    : ReceiveState.WaitClockLow;
                _bitIndex = 0;
                _waitCycles = 0;
                _ackHoldCycles = 0;
                _currentByte = 0;
                _eoiPending = eoiPending;
                _allowEoi = allowEoi;
                _lastClockLow = _port.IsLineLow(IecBusLine.Clock);
                _hasCompletedByte = false;
                _completedByte = 0;
                _completedEoi = false;
                ReleaseData();
            }

            /// <summary>
            /// Begins command byte transfer.
            /// </summary>
            public void BeginCommandByteTransfer()
            {
                // During ATN, the computer is still the talker. After the
                // initial presence handshake it first releases CLOCK and only
                // then releases DATA before the actual command byte starts.
                // Pi1541/VICE therefore re-enter the normal listener state
                // machine at the "listeners ready" phase, not directly at the
                // byte-start/EOI timer. Jumping straight to WaitByteStartOrEoi
                // makes the drive start the 200us EOI timer while the C64 is
                // still holding DATA low, which wedges SEARCHING FOR * into
                // DEVICE NOT PRESENT before the first LISTEN byte ever
                // arrives.
                _state = ReceiveState.WaitListenersReady;
                _bitIndex = 0;
                _waitCycles = 0;
                _ackHoldCycles = 0;
                _initialByteStartGraceCycles = 0;
                _currentByte = 0;
                _eoiPending = false;
                _allowEoi = true;
                _lastClockLow = _port.IsLineLow(IecBusLine.Clock);
                _hasCompletedByte = false;
                _completedByte = 0;
                _completedEoi = false;
                ReleaseData();
            }

            /// <summary>
            /// Handles the append to payload operation.
            /// </summary>
            public void AppendToPayload(byte value)
            {
                _payload.Add(value);
            }

            public bool HasPayloadBytes
            {
                get { return _payload.Count > 0; }
            }

            public bool HasInFlightByte
            {
                get
                {
                    return _bitIndex > 0 ||
                        _hasCompletedByte ||
                        _state == ReceiveState.WaitClockHigh ||
                        _state == ReceiveState.WaitClockLow ||
                        _state == ReceiveState.WaitByteAcknowledgeClockLow ||
                        _state == ReceiveState.ByteAcknowledgeHold;
                }
            }

            /// <summary>
            /// Handles the consume payload operation.
            /// </summary>
            public byte[] ConsumePayload()
            {
                return _payload.ToArray();
            }

            /// <summary>
            /// Handles the should implicitly end payload operation.
            /// </summary>
            public bool ShouldImplicitlyEndPayload(int idleCyclesThreshold)
            {
                return _payload.Count > 0 &&
                    _state == ReceiveState.WaitByteStartOrEoi &&
                    _waitCycles >= idleCyclesThreshold;
            }

            /// <summary>
            /// Advances the component by one emulated tick.
            /// </summary>
            public bool Tick(out ReceivedIecByte receivedByte)
            {
                receivedByte = null;
                bool clockLow = _port.IsLineLow(IecBusLine.Clock);
                bool dataLow = _port.IsLineLow(IecBusLine.Data);
                bool clockRose = _lastClockLow && !clockLow;

                switch (_state)
                {
                    case ReceiveState.WaitTalkerReady:
                        PullDataLow();
                        if (!clockLow)
                        {
                            ReleaseData();
                            _state = ReceiveState.WaitListenersReady;
                        }

                        break;

                    case ReceiveState.WaitListenersReady:
                        ReleaseData();
                        if (!_port.IsLineLow(IecBusLine.Data))
                        {
                            _state = ReceiveState.WaitByteStartOrEoi;
                            _waitCycles = 0;
                        }

                        break;

                    case ReceiveState.WaitByteStartOrEoi:
                        ReleaseData();
                        if (clockLow)
                        {
                            _state = ReceiveState.WaitClockLow;
                            _bitIndex = 0;
                            _currentByte = 0;
                            _waitCycles = 0;
                            break;
                        }

                        if (_initialByteStartGraceCycles > 0)
                        {
                            _initialByteStartGraceCycles--;
                            break;
                        }

                        _waitCycles++;
                        if (_allowEoi && !_eoiPending && _waitCycles >= EoiThresholdCycles)
                        {
                            PullDataLow();
                            _ackHoldCycles = EoiAcknowledgeHoldCycles;
                            _state = ReceiveState.EoiAcknowledgeHold;
                        }

                        break;

                    case ReceiveState.EoiAcknowledgeHold:
                        PullDataLow();
                        _ackHoldCycles--;
                        if (_ackHoldCycles <= 0)
                        {
                            ReleaseData();
                            _eoiPending = true;
                            _waitCycles = 0;
                            _state = ReceiveState.WaitByteStartOrEoi;
                        }

                        break;

                    case ReceiveState.WaitClockLow:
                        ReleaseData();
                        if (clockLow)
                        {
                            _state = ReceiveState.WaitClockHigh;
                        }

                        break;

                    case ReceiveState.WaitClockHigh:
                        ReleaseData();
                        if (clockRose || (!clockLow && _waitCycles > 2))
                        {
                            if (!dataLow)
                            {
                                _currentByte |= (byte)(1 << _bitIndex);
                            }

                            _bitIndex++;
                            if (_bitIndex >= 8)
                            {
                                _completedByte = _currentByte;
                                _completedEoi = _eoiPending;
                                _hasCompletedByte = true;
                                _eoiPending = false;
                                _state = ReceiveState.WaitByteAcknowledgeClockLow;
                            }
                            else
                            {
                                _state = ReceiveState.WaitClockLow;
                            }

                            _waitCycles = 0;
                        }
                        else
                        {
                            _waitCycles++;
                        }

                        break;

                    case ReceiveState.WaitByteAcknowledgeClockLow:
                        if (clockLow)
                        {
                            PullDataLow();
                            _ackHoldCycles = ByteAcknowledgeHoldCycles;
                            _state = ReceiveState.ByteAcknowledgeHold;
                        }

                        break;

                    case ReceiveState.ByteAcknowledgeHold:
                        PullDataLow();
                        _ackHoldCycles--;
                        if (_ackHoldCycles <= 0)
                        {
                            receivedByte = ConsumeCompletedByte();
                            _state = ReceiveState.WaitTalkerReady;
                            _lastClockLow = clockLow;
                            return receivedByte != null;
                        }

                        break;
                }

                _lastClockLow = clockLow;
                return false;
            }

            /// <summary>
            /// Attempts to finalize pending byte and reports whether it succeeded.
            /// </summary>
            public bool TryFinalizePendingByte(out ReceivedIecByte receivedByte)
            {
                receivedByte = null;
                if (_hasCompletedByte)
                {
                    receivedByte = ConsumeCompletedByte();
                    _state = ReceiveState.WaitTalkerReady;
                    return receivedByte != null;
                }

                if (_bitIndex < 8)
                {
                    return false;
                }

                if (_state != ReceiveState.WaitClockHigh &&
                    _state != ReceiveState.WaitClockLow &&
                    _state != ReceiveState.WaitByteAcknowledgeClockLow &&
                    _state != ReceiveState.ByteAcknowledgeHold)
                {
                    return false;
                }

                _completedByte = _currentByte;
                _completedEoi = _eoiPending;
                _hasCompletedByte = true;
                _eoiPending = false;
                receivedByte = ConsumeCompletedByte();
                _state = ReceiveState.WaitTalkerReady;
                return receivedByte != null;
            }

            /// <summary>
            /// Handles the consume completed byte operation.
            /// </summary>
            private ReceivedIecByte ConsumeCompletedByte()
            {
                if (!_hasCompletedByte)
                {
                    return null;
                }

                var receivedByte = new ReceivedIecByte
                {
                    Value = _completedByte,
                    IsEoi = _completedEoi
                };
                _hasCompletedByte = false;
                _completedByte = 0;
                _completedEoi = false;
                _bitIndex = 0;
                _currentByte = 0;
                return receivedByte;
            }

            /// <summary>
            /// Pulls data low.
            /// </summary>
            private void PullDataLow()
            {
                if (_driveDataLine)
                {
                    _port.SetLineLow(IecBusLine.Data, true);
                }
            }

            /// <summary>
            /// Releases data.
            /// </summary>
            private void ReleaseData()
            {
                if (_driveDataLine)
                {
                    _port.SetLineLow(IecBusLine.Data, false);
                }
            }

            /// <summary>
            /// Gets the debug state value.
            /// </summary>
            public string GetDebugState()
            {
                return string.Format(
                    "{0} bits={1} wait={2} ack={3} eoi={4} done={5}:{6:X2}",
                    _state,
                    _bitIndex,
                    _waitCycles,
                    _ackHoldCycles,
                    _eoiPending,
                    _hasCompletedByte,
                    _completedByte);
            }

            public bool IsIdle
            {
                get
                {
                    return _state == ReceiveState.WaitTalkerReady &&
                        !_hasCompletedByte &&
                        _bitIndex == 0 &&
                        _waitCycles == 0 &&
                        _ackHoldCycles == 0 &&
                        !_eoiPending;
                }
            }

            /// <summary>
            /// Writes the receiver handshake state into a savestate stream.
            /// </summary>
            public void SaveState(BinaryWriter writer)
            {
                StateSerializer.WriteObjectFields(writer, this, "_port");
            }

            /// <summary>
            /// Restores the receiver handshake state from a savestate stream.
            /// </summary>
            public void LoadState(BinaryReader reader)
            {
                StateSerializer.ReadObjectFields(reader, this, "_port");
            }

            /// <summary>
            /// Lists the supported receive state values.
            /// </summary>
            private enum ReceiveState
            {
                WaitTalkerReady,
                WaitListenersReady,
                WaitByteStartOrEoi,
                EoiAcknowledgeHold,
                WaitClockLow,
                WaitClockHigh,
                WaitByteAcknowledgeClockLow,
                ByteAcknowledgeHold
            }
        }

        /// <summary>
        /// Represents the iec byte sender component.
        /// </summary>
        private sealed class IecByteSender
        {
            private readonly IecBusPort _port;
            private byte[] _buffer = Array.Empty<byte>();
            private int _bufferIndex;
            private int _bitIndex;
            private int _countdown;
            private SendState _state;
            private bool _signalEoiOnLastByte;
            private bool _awaitingContinuation;
            private int _eoiSignalCount;
            private int _finalAckCount;
            private bool _sawEoiWaitLow;
            private bool _sawEoiWaitRelease;

            /// <summary>
            /// Initializes a new IecByteSender instance.
            /// </summary>
            public IecByteSender(IecBusPort port)
            {
                _port = port;
                Reset();
            }

            public bool IsIdle
            {
                get { return _state == SendState.Idle; }
            }

            public bool NeedsMoreData
            {
                get { return _state == SendState.WaitNextChunk && _awaitingContinuation; }
            }

            /// <summary>
            /// Resets the component to its power-on or idle state.
            /// </summary>
            public void Reset()
            {
                _buffer = Array.Empty<byte>();
                _bufferIndex = 0;
                _bitIndex = 0;
                _countdown = 0;
                _state = SendState.Idle;
                _signalEoiOnLastByte = false;
                _awaitingContinuation = false;
                _sawEoiWaitLow = false;
                _sawEoiWaitRelease = false;
                ReleaseClock();
                ReleaseData();
            }

            /// <summary>
            /// Handles the begin operation.
            /// </summary>
            public void Begin(byte[] buffer, bool signalEoiOnLastByte)
            {
                _buffer = buffer ?? Array.Empty<byte>();
                _bufferIndex = 0;
                _bitIndex = 0;
                _signalEoiOnLastByte = signalEoiOnLastByte;
                if (signalEoiOnLastByte && _buffer.Length > 0)
                {
                    _eoiSignalCount++;
                }
                _countdown = _buffer.Length == 0 ? 0 : TalkerTurnaroundReleaseCycles;
                _state = _buffer.Length == 0 ? SendState.Idle : SendState.WaitTurnaroundClockHigh;
                _awaitingContinuation = false;
                _sawEoiWaitLow = false;
                _sawEoiWaitRelease = false;
                ReleaseClock();
                PullDataLow();
            }

            /// <summary>
            /// Handles the continue operation.
            /// </summary>
            public void Continue(byte[] buffer, bool signalEoiOnLastByte)
            {
                if (_state != SendState.WaitNextChunk || !_awaitingContinuation)
                {
                    return;
                }

                _buffer = buffer ?? Array.Empty<byte>();
                _bufferIndex = 0;
                _bitIndex = 0;
                _signalEoiOnLastByte = signalEoiOnLastByte;
                _awaitingContinuation = false;
            }

            /// <summary>
            /// Advances the component by one emulated tick.
            /// </summary>
            public void Tick()
            {
                if (_state == SendState.Idle)
                {
                    return;
                }

                switch (_state)
                {
                    case SendState.WaitTurnaroundClockHigh:
                        ReleaseClock();
                        PullDataLow();
                        if (!_port.IsLineLow(IecBusLine.Clock))
                        {
                            ReleaseData();
                            PullClockLow();
                            _countdown = TalkerTurnaroundReleaseCycles;
                            _state = SendState.TurnaroundClockLowHold;
                        }

                        break;

                    case SendState.TurnaroundClockLowHold:
                        PullClockLow();
                        ReleaseData();
                        if (--_countdown <= 0)
                        {
                            ReleaseClock();
                            _state = SendState.WaitListenerReady;
                        }

                        break;

                    case SendState.InterByteDelay:
                        PullClockLow();
                        ReleaseData();
                        if (--_countdown <= 0)
                        {
                            ReleaseClock();
                            _state = SendState.WaitListenerReady;
                        }

                        break;

                    case SendState.WaitNextChunk:
                        PullClockLow();
                        ReleaseData();
                        if (_awaitingContinuation || _buffer.Length == 0)
                        {
                            break;
                        }

                        if (--_countdown <= 0)
                        {
                            ReleaseClock();
                            _state = SendState.WaitListenerReady;
                        }

                        break;

                    case SendState.WaitListenerReady:
                        ReleaseClock();
                        ReleaseData();
                        if (!_port.IsLineLow(IecBusLine.Data))
                        {
                            if (_signalEoiOnLastByte && _bufferIndex == _buffer.Length - 1)
                            {
                                _sawEoiWaitLow = true;
                                _state = SendState.WaitEoiAcknowledgeLow;
                            }
                            else
                            {
                                PullClockLow();
                                _countdown = TalkerPreambleLowCycles;
                                _state = SendState.PreambleLow;
                            }
                        }

                        break;

                    case SendState.WaitEoiAcknowledgeLow:
                        ReleaseClock();
                        ReleaseData();
                        if (_port.IsLineLow(IecBusLine.Data))
                        {
                            _sawEoiWaitRelease = true;
                            _state = SendState.WaitEoiAcknowledgeRelease;
                        }

                        break;

                    case SendState.WaitEoiAcknowledgeRelease:
                        ReleaseClock();
                        ReleaseData();
                        if (!_port.IsLineLow(IecBusLine.Data))
                        {
                            PullClockLow();
                            _countdown = TalkerPreambleLowCycles;
                            _state = SendState.PreambleLow;
                        }

                        break;

                    case SendState.PreambleLow:
                        PullClockLow();
                        ReleaseData();
                        if (--_countdown <= 0)
                        {
                            if (_port.IsLineLow(IecBusLine.Data))
                            {
                                _countdown = 1;
                                break;
                            }

                            _countdown = TalkerPreambleSettleCycles;
                            _state = SendState.PreambleSettle;
                        }

                        break;

                    case SendState.PreambleSettle:
                        PullClockLow();
                        ReleaseData();
                        if (--_countdown <= 0)
                        {
                            _bitIndex = 0;
                            PrepareBit();
                        }

                        break;

                    case SendState.BitPrepare:
                        PullClockLow();
                        ApplyCurrentBitToDataLine();
                        if (--_countdown <= 0)
                        {
                            _countdown = TalkerBitSetupCycles;
                            _state = SendState.BitSetup;
                        }

                        break;

                    case SendState.BitSetup:
                        PullClockLow();
                        ApplyCurrentBitToDataLine();
                        if (--_countdown <= 0)
                        {
                            ReleaseClock();
                            _countdown = TalkerBitValidCycles;
                            _state = SendState.BitValid;
                        }

                        break;

                    case SendState.BitValid:
                        ReleaseClock();
                        ApplyCurrentBitToDataLine();
                        if (--_countdown <= 0)
                        {
                            PullClockLow();
                            _countdown = TalkerBitClockLowHoldCycles;
                            _state = SendState.BitClockLowHold;
                        }

                        break;

                    case SendState.BitClockLowHold:
                        PullClockLow();
                        ApplyCurrentBitToDataLine();
                        if (--_countdown <= 0)
                        {
                            ReleaseData();
                            _countdown = TalkerBitRecoveryCycles;
                            _state = SendState.BitRecovery;
                        }

                        break;

                    case SendState.BitRecovery:
                        PullClockLow();
                        ReleaseData();
                        if (--_countdown <= 0)
                        {
                            _bitIndex++;
                            if (_bitIndex >= 8)
                            {
                                _countdown = ByteAcknowledgeTimeoutCycles;
                                _state = SendState.WaitByteAcknowledgeLow;
                            }
                            else
                            {
                                PrepareBit();
                            }
                        }

                        break;

                    case SendState.WaitByteAcknowledgeLow:
                        PullClockLow();
                        ReleaseData();
                        if (_port.IsLineLow(IecBusLine.Data))
                        {
                            _bufferIndex++;
                            if (_bufferIndex >= _buffer.Length)
                            {
                                if (_signalEoiOnLastByte)
                                {
                                    _countdown = FinalByteAcknowledgeHoldCycles;
                                    _state = SendState.FinalByteAcknowledgeHold;
                                }
                                else
                                {
                                    _buffer = Array.Empty<byte>();
                                    _bufferIndex = 0;
                                    _bitIndex = 0;
                                    _countdown = TalkerInterByteDelayCycles;
                                    _awaitingContinuation = true;
                                    _state = SendState.WaitNextChunk;
                                }
                            }
                            else
                            {
                                _countdown = TalkerInterByteDelayCycles;
                                _state = SendState.InterByteDelay;
                            }
                        }
                        else if (--_countdown <= 0)
                        {
                            Reset();
                        }

                        break;

                    case SendState.FinalByteAcknowledgeHold:
                        PullClockLow();
                        ReleaseData();
                        if (--_countdown <= 0)
                        {
                            _finalAckCount++;
                            Reset();
                        }

                        break;
                }
            }

            /// <summary>
            /// Handles the prepare bit operation.
            /// </summary>
            private void PrepareBit()
            {
                PullClockLow();
                ApplyCurrentBitToDataLine();
                _countdown = TalkerBitPrepareCycles;
                _state = SendState.BitPrepare;
            }

            /// <summary>
            /// Applies current bit to data line.
            /// </summary>
            private void ApplyCurrentBitToDataLine()
            {
                bool bitIsOne = ((_buffer[_bufferIndex] >> _bitIndex) & 0x01) != 0;
                if (bitIsOne)
                {
                    ReleaseData();
                }
                else
                {
                    PullDataLow();
                }
            }

            /// <summary>
            /// Pulls clock low.
            /// </summary>
            private void PullClockLow()
            {
                _port.SetLineLow(IecBusLine.Clock, true);
            }

            /// <summary>
            /// Releases clock.
            /// </summary>
            private void ReleaseClock()
            {
                _port.SetLineLow(IecBusLine.Clock, false);
            }

            /// <summary>
            /// Pulls data low.
            /// </summary>
            private void PullDataLow()
            {
                _port.SetLineLow(IecBusLine.Data, true);
            }

            /// <summary>
            /// Releases data.
            /// </summary>
            private void ReleaseData()
            {
                _port.SetLineLow(IecBusLine.Data, false);
            }

            /// <summary>
            /// Gets the debug state value.
            /// </summary>
            public string GetDebugState()
            {
                return string.Format(
                    "{0} idx={1}/{2} bit={3} t={4} eoi={5} fin={6} low={7} rel={8}",
                    _state,
                    _bufferIndex,
                    _buffer != null ? _buffer.Length : 0,
                    _bitIndex,
                    _countdown,
                    _eoiSignalCount,
                    _finalAckCount,
                    _sawEoiWaitLow,
                    _sawEoiWaitRelease);
            }

            /// <summary>
            /// Writes the sender handshake state into a savestate stream.
            /// </summary>
            public void SaveState(BinaryWriter writer)
            {
                StateSerializer.WriteObjectFields(writer, this, "_port");
            }

            /// <summary>
            /// Restores the sender handshake state from a savestate stream.
            /// </summary>
            public void LoadState(BinaryReader reader)
            {
                StateSerializer.ReadObjectFields(reader, this, "_port");
            }

            /// <summary>
            /// Lists the supported send state values.
            /// </summary>
            private enum SendState
            {
                Idle,
                WaitTurnaroundClockHigh,
                TurnaroundClockLowHold,
                InterByteDelay,
                WaitNextChunk,
                WaitListenerReady,
                WaitEoiAcknowledgeLow,
                WaitEoiAcknowledgeRelease,
                PreambleLow,
                PreambleSettle,
                BitPrepare,
                BitSetup,
                BitValid,
                BitClockLowHold,
                BitRecovery,
                WaitByteAcknowledgeLow,
                FinalByteAcknowledgeHold
            }
        }
    }
}
