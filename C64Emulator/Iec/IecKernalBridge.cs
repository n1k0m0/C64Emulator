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
namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the iec kernal bridge component.
    /// </summary>
    public sealed class IecKernalBridge
    {
        /// <summary>
        /// Represents the open channel component.
        /// </summary>
        private sealed class OpenChannel
        {
            public byte Device;
            public byte SecondaryAddress;
            public bool IsCommandChannel;
        }

        private readonly Dictionary<byte, IecDrive1541> _drives = new Dictionary<byte, IecDrive1541>();
        private IecDrive1541 _listener;
        private IecDrive1541 _talker;
        private readonly Dictionary<byte, OpenChannel> _openChannels = new Dictionary<byte, OpenChannel>();
        private byte _status;
        private string _statusText = "73, CBM DOS V2.6 1541,00,00";
        private byte _currentLogicalFile;
        private byte _currentDevice;
        private byte _currentSecondaryAddress;
        private string _currentFilename = string.Empty;
        private byte _activeInputLogicalFile = 0xFF;
        private byte _activeOutputLogicalFile = 0xFF;

        /// <summary>
        /// Handles the attach drive operation.
        /// </summary>
        public void AttachDrive(IecDrive1541 drive)
        {
            if (drive == null)
            {
                return;
            }

            _drives[drive.DeviceNumber] = drive;
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset()
        {
            foreach (IecDrive1541 drive in _drives.Values)
            {
                drive.BridgeLowLevelSessionActive = false;
            }

            _listener = null;
            _talker = null;
            _status = 0;
            _statusText = "73, CBM DOS V2.6 1541,00,00";
            _openChannels.Clear();
            _currentLogicalFile = 0;
            _currentDevice = 0;
            _currentSecondaryAddress = 0;
            _currentFilename = string.Empty;
            _activeInputLogicalFile = 0xFF;
            _activeOutputLogicalFile = 0xFF;
        }

        public bool IsActive
        {
            get
            {
                foreach (IecDrive1541 drive in _drives.Values)
                {
                    if (drive.IsMounted && !drive.IsHardwareTransportReady)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public byte Status
        {
            get { return _status; }
        }

        public byte CurrentDevice
        {
            get { return _currentDevice; }
        }

        public string CurrentFilename
        {
            get { return _currentFilename; }
        }

        public byte CurrentSecondaryAddress
        {
            get { return _currentSecondaryAddress; }
        }

        public string StatusText
        {
            get { return _statusText; }
        }

        public bool IsCurrentDeviceSupported
        {
            get { return TryGetMountedDrive(_currentDevice, out _); }
        }

        public bool HasAnyOpenChannels
        {
            get { return _openChannels.Count > 0; }
        }

        public bool HasActiveInputChannel
        {
            get { return _activeInputLogicalFile != 0xFF && _openChannels.ContainsKey(_activeInputLogicalFile); }
        }

        public bool HasActiveOutputChannel
        {
            get { return _activeOutputLogicalFile != 0xFF && _openChannels.ContainsKey(_activeOutputLogicalFile); }
        }

        /// <summary>
        /// Returns whether logical file open is true.
        /// </summary>
        public bool IsLogicalFileOpen(byte logicalFileNumber)
        {
            return _openChannels.ContainsKey(logicalFileNumber);
        }

        /// <summary>
        /// Handles the should bypass low level hooks operation.
        /// </summary>
        public bool ShouldBypassLowLevelHooks(byte device)
        {
            if (!TryGetMountedDrive(device, out IecDrive1541 drive))
            {
                return false;
            }

            return drive.IsHardwareTransportReady;
        }

        /// <summary>
        /// Sets the lfs value.
        /// </summary>
        public bool SetLfs(byte logicalFileNumber, byte device, byte secondaryAddress)
        {
            _currentLogicalFile = logicalFileNumber;
            _currentDevice = device;
            _currentSecondaryAddress = secondaryAddress;
            SetDosStatus(0x00, "00, OK,00,00");
            return true;
        }

        /// <summary>
        /// Sets the nam value.
        /// </summary>
        public bool SetNam(string filename)
        {
            _currentFilename = filename ?? string.Empty;
            SetDosStatus(0x00, "00, OK,00,00");
            return true;
        }

        /// <summary>
        /// Handles the load operation.
        /// </summary>
        public bool Load(out byte[] programBytes)
        {
            programBytes = Array.Empty<byte>();

            if (!TryGetMountedDrive(_currentDevice, out _))
            {
                SetDosStatus(0x80, "74, DRIVE NOT READY,00,00");
                return false;
            }

            if (!Open())
            {
                return false;
            }

            bool checkedIn = false;
            try
            {
                if (!ChkIn(_currentLogicalFile))
                {
                    return false;
                }

                checkedIn = true;
                var bytes = new List<byte>(12288);
                while (true)
                {
                    byte value;
                    if (!ChrIn(out value))
                    {
                        return false;
                    }

                    bytes.Add(value);
                    if ((_status & 0x40) != 0)
                    {
                        break;
                    }
                }

                programBytes = bytes.ToArray();
                return true;
            }
            finally
            {
                if (checkedIn)
                {
                    ClrChn();
                }

                Close(_currentLogicalFile);
            }
        }

        /// <summary>
        /// Handles the open operation.
        /// </summary>
        public bool Open()
        {
            if (!TryGetMountedDrive(_currentDevice, out IecDrive1541 drive))
            {
                SetDosStatus(0x80, "74, DRIVE NOT READY,00,00");
                return false;
            }

            var channel = new OpenChannel
            {
                Device = _currentDevice,
                SecondaryAddress = _currentSecondaryAddress,
                IsCommandChannel = (_currentSecondaryAddress & 0x0F) == 15
            };

            IecDrive1541.CommandResult result = drive.OpenKernalChannel(_currentSecondaryAddress, _currentFilename);
            SetDosStatus(result.Status, result.StatusText);
            if (result.Status != 0x00)
            {
                return false;
            }

            _openChannels[_currentLogicalFile] = channel;
            return true;
        }

        /// <summary>
        /// Handles the close operation.
        /// </summary>
        public bool Close(byte logicalFileNumber)
        {
            OpenChannel channel;
            if (!_openChannels.TryGetValue(logicalFileNumber, out channel))
            {
                SetDosStatus(0x00, "00, OK,00,00");
                return true;
            }

            IecDrive1541 drive;
            if (TryGetDrive(channel.Device, out drive))
            {
                IecDrive1541.CommandResult result = drive.CloseKernalChannel(channel.SecondaryAddress);
                SetDosStatus(result.Status, result.StatusText);
            }

            _openChannels.Remove(logicalFileNumber);
            if (_activeInputLogicalFile == logicalFileNumber)
            {
                _activeInputLogicalFile = 0xFF;
            }

            if (_activeOutputLogicalFile == logicalFileNumber)
            {
                _activeOutputLogicalFile = 0xFF;
            }

            return true;
        }

        /// <summary>
        /// Handles the chk in operation.
        /// </summary>
        public bool ChkIn(byte logicalFileNumber)
        {
            if (!_openChannels.ContainsKey(logicalFileNumber))
            {
                SetDosStatus(0x03, "70, NO CHANNEL,00,00");
                return false;
            }

            _activeInputLogicalFile = logicalFileNumber;
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the chk out operation.
        /// </summary>
        public bool ChkOut(byte logicalFileNumber)
        {
            if (!_openChannels.ContainsKey(logicalFileNumber))
            {
                SetDosStatus(0x03, "70, NO CHANNEL,00,00");
                return false;
            }

            _activeOutputLogicalFile = logicalFileNumber;
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the clr chn operation.
        /// </summary>
        public bool ClrChn()
        {
            _activeInputLogicalFile = 0xFF;
            _activeOutputLogicalFile = 0xFF;
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the chr in operation.
        /// </summary>
        public bool ChrIn(out byte value)
        {
            value = 0;
            OpenChannel channel;
            if (!_openChannels.TryGetValue(_activeInputLogicalFile, out channel))
            {
                return false;
            }

            IecDrive1541 drive;
            if (!TryGetDrive(channel.Device, out drive))
            {
                return false;
            }

            bool endOfInformation;
            if (!drive.TryReadKernalChannelByte(channel.SecondaryAddress, out value, out endOfInformation))
            {
                return false;
            }

            SetDosStatus(drive.StatusCode, drive.StatusText);
            return true;
        }

        /// <summary>
        /// Handles the chr out operation.
        /// </summary>
        public bool ChrOut(byte value)
        {
            OpenChannel channel;
            if (!_openChannels.TryGetValue(_activeOutputLogicalFile, out channel))
            {
                return false;
            }

            IecDrive1541 drive;
            if (!TryGetDrive(channel.Device, out drive))
            {
                return false;
            }

            IecDrive1541.CommandResult result = drive.WriteKernalChannelByte(channel.SecondaryAddress, value);
            SetDosStatus(result.Status, result.StatusText);
            return true;
        }

        /// <summary>
        /// Handles the listen operation.
        /// </summary>
        public bool Listen(byte device)
        {
            SetBridgeSession(_listener, false);
            SetBridgeSession(_talker, false);
            if (!TryGetMountedDrive(device, out IecDrive1541 drive))
            {
                return false;
            }

            _listener = drive;
            _talker = null;
            SetBridgeSession(drive, true);
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the talk operation.
        /// </summary>
        public bool Talk(byte device)
        {
            SetBridgeSession(_listener, false);
            SetBridgeSession(_talker, false);
            if (!TryGetMountedDrive(device, out IecDrive1541 drive))
            {
                return false;
            }

            _talker = drive;
            _listener = null;
            SetBridgeSession(drive, true);
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the secondary operation.
        /// </summary>
        public bool Secondary(byte secondaryAddress)
        {
            if (_listener == null)
            {
                return false;
            }

            _listener.BeginListen(secondaryAddress);
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the talk secondary operation.
        /// </summary>
        public bool TalkSecondary(byte secondaryAddress)
        {
            if (_talker == null)
            {
                return false;
            }

            _talker.BeginTalk(secondaryAddress);
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the ci out operation.
        /// </summary>
        public bool CiOut(byte value)
        {
            if (_listener == null)
            {
                return false;
            }

            _listener.ReceiveListenByte(value);
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the ac ptr operation.
        /// </summary>
        public bool AcPtr(out byte value)
        {
            value = 0;
            if (_talker == null)
            {
                return false;
            }

            bool endOfInformation;
            if (!_talker.TryReadTalkByte(out value, out endOfInformation))
            {
                SetDosStatus(0x02, _statusText);
                return true;
            }

            SetDosStatus(endOfInformation ? (byte)0x40 : (byte)0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the unlisten operation.
        /// </summary>
        public bool Unlisten()
        {
            if (_listener == null)
            {
                return false;
            }

            SetBridgeSession(_listener, false);
            _listener.EndListen();
            _listener = null;
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Handles the untalk operation.
        /// </summary>
        public bool Untalk()
        {
            if (_talker == null)
            {
                return false;
            }

            SetBridgeSession(_talker, false);
            _talker.EndTalk();
            _talker = null;
            SetDosStatus(0x00, _statusText);
            return true;
        }

        /// <summary>
        /// Sets the dos status value.
        /// </summary>
        public void SetDosStatus(byte status, string statusText)
        {
            _status = status;
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                _statusText = statusText;
            }
        }
        /// <summary>
        /// Attempts to get drive and reports whether it succeeded.
        /// </summary>
        private bool TryGetDrive(byte deviceNumber, out IecDrive1541 drive)
        {
            return _drives.TryGetValue(deviceNumber, out drive);
        }

        /// <summary>
        /// Attempts to get mounted drive and reports whether it succeeded.
        /// </summary>
        private bool TryGetMountedDrive(byte deviceNumber, out IecDrive1541 drive)
        {
            if (_drives.TryGetValue(deviceNumber, out drive) && drive.IsMounted)
            {
                return true;
            }

            drive = null;
            return false;
        }

        /// <summary>
        /// Sets the bridge session value.
        /// </summary>
        private static void SetBridgeSession(IecDrive1541 drive, bool active)
        {
            if (drive != null)
            {
                drive.BridgeLowLevelSessionActive = active;
            }
        }
    }
}
