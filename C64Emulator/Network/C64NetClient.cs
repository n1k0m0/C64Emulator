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
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using C64Emulator.Core;

namespace C64Emulator.Network
{
    /// <summary>
    /// Receives a remote C64Net session and exposes frames/audio/input state to the window.
    /// </summary>
    public sealed class C64NetClient : IDisposable
    {
        private readonly object _sendLock = new object();
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private CancellationTokenSource _shutdown;
        private Task _receiveTask;
        private SidAudioOutput _audioOutput;
        private byte _lastSentJoystickState = 0xFF;
        private volatile bool _connected;

        public event Action<C64NetVideoFrame> FrameReceived;
        public event Action<List<C64NetClientSnapshot>> ClientListReceived;
        public event Action<string> HostOverlayStatusChanged;
        public event Action<string> StatusChanged;

        public int ClientId { get; private set; }
        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }
        public int AudioSampleRate { get; private set; }
        public C64NetClientRole Role { get; private set; }
        public C64NetJoystickPermission Permission { get; private set; }
        public string StatusText { get; private set; } = "DISCONNECTED";

        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>
        /// Connects to a host session.
        /// </summary>
        public bool Connect(string host, int port, string password, C64NetClientRole role, string name, out string status)
        {
            Disconnect();
            status = string.Empty;
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                Task connectTask = _tcpClient.ConnectAsync(host, port);
                if (!connectTask.Wait(3000))
                {
                    status = "CONNECT TIMEOUT";
                    Disconnect();
                    return false;
                }

                _stream = _tcpClient.GetStream();
                C64NetProtocol.WriteMessage(_stream, new C64NetMessage
                {
                    Type = C64NetMessageType.ClientHello,
                    Timestamp = DateTime.UtcNow.Ticks,
                    Payload = C64NetProtocol.CreateClientHelloPayload(name, password, role)
                });

                C64NetMessage response = C64NetProtocol.ReadMessage(_stream);
                if (response == null)
                {
                    status = "NO SERVER RESPONSE";
                    Disconnect();
                    return false;
                }

                if (response.Type == C64NetMessageType.ServerReject)
                {
                    status = C64NetProtocol.ReadTextPayload(response.Payload);
                    Disconnect();
                    return false;
                }

                if (response.Type != C64NetMessageType.ServerWelcome)
                {
                    status = "BAD SERVER RESPONSE";
                    Disconnect();
                    return false;
                }

                C64NetProtocol.ReadServerWelcomePayload(
                    response.Payload,
                    out int clientId,
                    out int width,
                    out int height,
                    out int audioSampleRate,
                    out C64NetClientRole acceptedRole,
                    out C64NetJoystickPermission permission,
                    out string welcomeStatus);

                ClientId = clientId;
                VideoWidth = width;
                VideoHeight = height;
                AudioSampleRate = audioSampleRate;
                Role = acceptedRole;
                Permission = permission;
                StatusText = string.IsNullOrWhiteSpace(welcomeStatus) ? "CONNECTED" : welcomeStatus;
                _connected = true;

                try
                {
                    _audioOutput = new SidAudioOutput(AudioSampleRate);
                }
                catch
                {
                    _audioOutput = null;
                }

                _shutdown = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoop(_shutdown.Token));
                status = StatusText;
                RaiseStatus(status);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                status = "CONNECT FAILED";
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the remote session.
        /// </summary>
        public void Disconnect()
        {
            CancellationTokenSource shutdown = _shutdown;
            Task receiveTask = _receiveTask;
            _shutdown = null;
            if (shutdown != null)
            {
                shutdown.Cancel();
            }

            try
            {
                if (_stream != null)
                {
                    C64NetProtocol.WriteMessage(_stream, new C64NetMessage
                    {
                        Type = C64NetMessageType.Disconnect,
                        Timestamp = DateTime.UtcNow.Ticks,
                        Payload = C64NetProtocol.CreateTextPayload("LEAVE")
                    });
                }
            }
            catch
            {
            }

            try
            {
                if (_tcpClient != null)
                {
                    _tcpClient.Close();
                }
            }
            catch
            {
            }

            try
            {
                if (receiveTask != null && (!Task.CurrentId.HasValue || Task.CurrentId.Value != receiveTask.Id))
                {
                    receiveTask.Wait(250);
                }
            }
            catch
            {
            }

            if (_audioOutput != null)
            {
                _audioOutput.Dispose();
                _audioOutput = null;
            }

            if (shutdown != null)
            {
                shutdown.Dispose();
            }

            _receiveTask = null;
            _stream = null;
            _tcpClient = null;
            _connected = false;
            ClientId = 0;
            Permission = C64NetJoystickPermission.Observer;
            StatusText = "DISCONNECTED";
            RaiseHostOverlayStatus(string.Empty);
            RaiseStatus(StatusText);
        }

        /// <summary>
        /// Sends the current C64 active-low joystick state to the server.
        /// </summary>
        public void SendJoystickState(byte activeLowJoystickState, bool force)
        {
            activeLowJoystickState = (byte)(activeLowJoystickState | 0xE0);
            if (!force && activeLowJoystickState == _lastSentJoystickState)
            {
                return;
            }

            _lastSentJoystickState = activeLowJoystickState;
            SendMessage(new C64NetMessage
            {
                Type = C64NetMessageType.InputState,
                Timestamp = DateTime.UtcNow.Ticks,
                Payload = C64NetProtocol.CreateInputStatePayload(activeLowJoystickState)
            });
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void ReceiveLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    C64NetMessage message = C64NetProtocol.ReadMessage(_stream);
                    if (message == null)
                    {
                        break;
                    }

                    HandleMessage(message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                StatusText = "DISCONNECTED";
                _connected = false;
                RaiseHostOverlayStatus(string.Empty);
                RaiseStatus(StatusText);
            }
        }

        private void HandleMessage(C64NetMessage message)
        {
            switch (message.Type)
            {
                case C64NetMessageType.VideoFrame:
                    C64NetVideoFrame frame = C64NetProtocol.ReadVideoFramePayload(message.Payload);
                    if (frame != null)
                    {
                        Action<C64NetVideoFrame> handler = FrameReceived;
                        if (handler != null)
                        {
                            handler(frame);
                        }
                    }

                    break;
                case C64NetMessageType.AudioChunk:
                    if (C64NetProtocol.ReadAudioChunkPayload(message.Payload, out int sampleRate, out byte[] buffer, out int count))
                    {
                        if (_audioOutput != null && sampleRate == AudioSampleRate)
                        {
                            _audioOutput.Write(buffer, count);
                        }
                    }

                    break;
                case C64NetMessageType.ClientList:
                    List<C64NetClientSnapshot> clients = C64NetProtocol.ReadClientListPayload(message.Payload);
                    Action<List<C64NetClientSnapshot>> listHandler = ClientListReceived;
                    if (listHandler != null)
                    {
                        listHandler(clients);
                    }

                    break;
                case C64NetMessageType.PermissionUpdate:
                    Permission = C64NetProtocol.ReadPermissionPayload(message.Payload);
                    StatusText = "PERMISSION " + FormatPermission(Permission);
                    RaiseStatus(StatusText);
                    break;
                case C64NetMessageType.HostOverlayStatus:
                    RaiseHostOverlayStatus(C64NetProtocol.ReadTextPayload(message.Payload));
                    break;
                case C64NetMessageType.Ping:
                    SendMessage(new C64NetMessage
                    {
                        Type = C64NetMessageType.Pong,
                        Timestamp = message.Timestamp,
                        Payload = message.Payload
                    });
                    break;
                case C64NetMessageType.Disconnect:
                    StatusText = C64NetProtocol.ReadTextPayload(message.Payload);
                    RaiseStatus(StatusText);
                    Disconnect();
                    break;
            }
        }

        private void SendMessage(C64NetMessage message)
        {
            if (_stream == null || message == null)
            {
                return;
            }

            try
            {
                lock (_sendLock)
                {
                    C64NetProtocol.WriteMessage(_stream, message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Disconnect();
            }
        }

        private void RaiseStatus(string status)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                handler(status);
            }
        }

        private void RaiseHostOverlayStatus(string status)
        {
            Action<string> handler = HostOverlayStatusChanged;
            if (handler != null)
            {
                handler(status);
            }
        }

        private static string FormatPermission(C64NetJoystickPermission permission)
        {
            switch (permission)
            {
                case C64NetJoystickPermission.Port1:
                    return "PORT 1";
                case C64NetJoystickPermission.Port2:
                    return "PORT 2";
                case C64NetJoystickPermission.Both:
                    return "BOTH";
                default:
                    return "OBSERVER";
            }
        }
    }
}
