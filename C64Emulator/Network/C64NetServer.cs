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
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace C64Emulator.Network
{
    /// <summary>
    /// Hosts a C64Net session and broadcasts the local emulator to connected clients.
    /// </summary>
    public sealed class C64NetServer : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly List<ClientConnection> _clients = new List<ClientConnection>();
        private readonly HashSet<string> _bannedRemoteAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly int _videoWidth;
        private readonly int _videoHeight;
        private TcpListener _listener;
        private CancellationTokenSource _shutdown;
        private Task _acceptTask;
        private string _password = string.Empty;
        private string _hostOverlayStatus = string.Empty;
        private int _nextClientId = 1;
        private long _frameId;

        public event Action<string> StatusChanged;

        public C64NetServer(int videoWidth, int videoHeight)
        {
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
        }

        public bool IsRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return _listener != null;
                }
            }
        }

        public int Port { get; private set; }

        /// <summary>
        /// Starts listening for clients.
        /// </summary>
        public void Start(int port, string password)
        {
            Stop();
            _password = password ?? string.Empty;
            _hostOverlayStatus = string.Empty;
            lock (_syncRoot)
            {
                _bannedRemoteAddresses.Clear();
            }

            _shutdown = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = Task.Run(() => AcceptLoop(_shutdown.Token));
            RaiseStatus("SERVER LISTENING " + Port);
        }

        /// <summary>
        /// Stops the host session and disconnects every client.
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource shutdown;
            TcpListener listener;
            lock (_syncRoot)
            {
                shutdown = _shutdown;
                listener = _listener;
                _shutdown = null;
                _listener = null;
            }

            if (shutdown != null)
            {
                shutdown.Cancel();
            }

            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }

            List<ClientConnection> clients;
            lock (_syncRoot)
            {
                clients = new List<ClientConnection>(_clients);
                _clients.Clear();
            }

            for (int index = 0; index < clients.Count; index++)
            {
                clients[index].Close();
            }

            if (_acceptTask != null)
            {
                try
                {
                    _acceptTask.Wait(250);
                }
                catch
                {
                }

                _acceptTask = null;
            }

            if (shutdown != null)
            {
                shutdown.Dispose();
            }

            Port = 0;
            RaiseStatus("SERVER STOPPED");
        }

        /// <summary>
        /// Broadcasts one raw C64 video frame.
        /// </summary>
        public void BroadcastVideoFrame(uint[] pixels, int width, int height)
        {
            if (!IsRunning || pixels == null)
            {
                return;
            }

            List<ClientConnection> clients = GetClients();
            if (clients.Count == 0)
            {
                return;
            }

            byte[] payload = C64NetProtocol.CreateVideoFramePayload(pixels, width, height, Interlocked.Increment(ref _frameId));
            var message = CreateMessage(C64NetMessageType.VideoFrame, payload);
            for (int index = 0; index < clients.Count; index++)
            {
                clients[index].EnqueueVideo(message);
            }
        }

        /// <summary>
        /// Broadcasts one PCM audio chunk.
        /// </summary>
        public void BroadcastAudio(byte[] buffer, int count)
        {
            if (!IsRunning || buffer == null || count <= 0)
            {
                return;
            }

            List<ClientConnection> clients = GetClients();
            if (clients.Count == 0)
            {
                return;
            }

            byte[] payload = C64NetProtocol.CreateAudioChunkPayload(buffer, count, C64NetProtocol.DefaultAudioSampleRate);
            var message = CreateMessage(C64NetMessageType.AudioChunk, payload);
            for (int index = 0; index < clients.Count; index++)
            {
                clients[index].EnqueueAudio(message);
            }
        }

        /// <summary>
        /// Updates the client-visible host menu status popup.
        /// </summary>
        public void SetHostOverlayStatus(string status)
        {
            status = status ?? string.Empty;
            lock (_syncRoot)
            {
                if (string.Equals(_hostOverlayStatus, status, StringComparison.Ordinal))
                {
                    return;
                }

                _hostOverlayStatus = status;
            }

            var message = CreateMessage(C64NetMessageType.HostOverlayStatus, C64NetProtocol.CreateTextPayload(status));
            List<ClientConnection> clients = GetClients();
            for (int index = 0; index < clients.Count; index++)
            {
                clients[index].EnqueueControl(message);
            }
        }

        /// <summary>
        /// Returns a stable snapshot of connected clients.
        /// </summary>
        public List<C64NetClientSnapshot> GetClientSnapshots()
        {
            var snapshots = new List<C64NetClientSnapshot>();
            lock (_syncRoot)
            {
                for (int index = 0; index < _clients.Count; index++)
                {
                    snapshots.Add(_clients[index].CreateSnapshot());
                }
            }

            return snapshots;
        }

        /// <summary>
        /// Changes the joystick permission of a client.
        /// </summary>
        public void SetClientPermission(int clientId, C64NetJoystickPermission permission)
        {
            ClientConnection client = FindClient(clientId);
            if (client == null)
            {
                return;
            }

            client.Permission = permission;
            client.EnqueueControl(CreateMessage(C64NetMessageType.PermissionUpdate, C64NetProtocol.CreatePermissionPayload(permission)));
            BroadcastClientList();
            RaiseStatus("CLIENT " + clientId + " " + FormatPermission(permission));
        }

        /// <summary>
        /// Cycles the joystick permission of a client.
        /// </summary>
        public void CycleClientPermission(int clientId)
        {
            ClientConnection client = FindClient(clientId);
            if (client == null)
            {
                return;
            }

            SetClientPermission(clientId, GetNextPermission(client.Permission));
        }

        /// <summary>
        /// Disconnects a client from the host session.
        /// </summary>
        public void KickClient(int clientId)
        {
            ClientConnection client = FindClient(clientId);
            if (client == null)
            {
                return;
            }

            AddBannedRemoteAddress(client.RemoteAddress);
            client.EnqueueControl(CreateMessage(C64NetMessageType.Disconnect, C64NetProtocol.CreateTextPayload("KICKED BY HOST")));
            client.Close();
            RemoveClient(client);
            RaiseStatus("CLIENT " + clientId + " KICKED");
        }

        /// <summary>
        /// Aggregates every client input for the requested port using C64 active-low bits.
        /// </summary>
        public byte GetAggregatedJoystickState(C64NetJoystickPermission portPermission)
        {
            byte state = 0xFF;
            lock (_syncRoot)
            {
                for (int index = 0; index < _clients.Count; index++)
                {
                    ClientConnection client = _clients[index];
                    if (!client.IsConnected || !PermissionIncludes(client.Permission, portPermission))
                    {
                        continue;
                    }

                    state = (byte)(state & client.JoystickState);
                }
            }

            return (byte)(state | 0xE0);
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    TcpListener listener;
                    lock (_syncRoot)
                    {
                        listener = _listener;
                    }

                    if (listener == null)
                    {
                        return;
                    }

                    TcpClient tcpClient = listener.AcceptTcpClient();
                    tcpClient.NoDelay = true;
                    Task.Run(() => HandleAcceptedClient(tcpClient, cancellationToken));
                }
                catch (SocketException)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        RaiseStatus("SERVER ACCEPT FAILED");
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    RaiseStatus("SERVER ACCEPT FAILED");
                }
            }
        }

        private void HandleAcceptedClient(TcpClient tcpClient, CancellationToken cancellationToken)
        {
            ClientConnection client = null;
            try
            {
                NetworkStream stream = tcpClient.GetStream();
                C64NetMessage hello = C64NetProtocol.ReadMessage(stream);
                if (hello == null || hello.Type != C64NetMessageType.ClientHello)
                {
                    SendReject(stream, "BAD HELLO");
                    tcpClient.Close();
                    return;
                }

                C64NetProtocol.ReadClientHelloPayload(
                    hello.Payload,
                    out int version,
                    out string name,
                    out string password,
                    out C64NetClientRole role);

                if (version != C64NetProtocol.Version)
                {
                    SendReject(stream, "PROTOCOL MISMATCH");
                    tcpClient.Close();
                    return;
                }

                string remoteAddress = FormatRemoteAddress(tcpClient);
                if (IsRemoteAddressBanned(remoteAddress))
                {
                    RaiseStatus("BANNED CLIENT " + remoteAddress);
                    SendReject(stream, "KICKED FROM SESSION");
                    tcpClient.Close();
                    return;
                }

                if (!string.Equals(_password ?? string.Empty, password ?? string.Empty, StringComparison.Ordinal))
                {
                    RaiseStatus("BAD PASSWORD " + FormatRemoteEndpoint(tcpClient));
                    SendReject(stream, "PASSWORD REJECTED");
                    tcpClient.Close();
                    return;
                }

                int clientId;
                lock (_syncRoot)
                {
                    clientId = _nextClientId++;
                }

                C64NetJoystickPermission permission = role == C64NetClientRole.Observer
                    ? C64NetJoystickPermission.Observer
                    : C64NetJoystickPermission.Observer;
                client = new ClientConnection(this, tcpClient, clientId, name, role, permission);
                C64NetProtocol.WriteMessage(stream, CreateMessage(
                    C64NetMessageType.ServerWelcome,
                    C64NetProtocol.CreateServerWelcomePayload(clientId, _videoWidth, _videoHeight, C64NetProtocol.DefaultAudioSampleRate, role, permission, "CONNECTED")));

                AddClient(client);
                client.Start(cancellationToken);
                RaiseStatus("CLIENT " + clientId + " JOINED");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                if (client != null)
                {
                    client.Close();
                    RemoveClient(client);
                }
                else
                {
                    try
                    {
                        tcpClient.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void HandleClientMessage(ClientConnection client, C64NetMessage message)
        {
            if (client == null || message == null)
            {
                return;
            }

            switch (message.Type)
            {
                case C64NetMessageType.InputState:
                    client.JoystickState = C64NetProtocol.ReadInputStatePayload(message.Payload);
                    break;
                case C64NetMessageType.Ping:
                    client.EnqueueControl(CreateMessage(C64NetMessageType.Pong, message.Payload));
                    break;
                case C64NetMessageType.Pong:
                    client.UpdatePong(message.Timestamp);
                    break;
                case C64NetMessageType.Disconnect:
                    client.Close();
                    RemoveClient(client);
                    break;
            }
        }

        private void AddClient(ClientConnection client)
        {
            lock (_syncRoot)
            {
                _clients.Add(client);
            }

            BroadcastClientList();
            string hostOverlayStatus;
            lock (_syncRoot)
            {
                hostOverlayStatus = _hostOverlayStatus;
            }

            if (!string.IsNullOrEmpty(hostOverlayStatus))
            {
                client.EnqueueControl(CreateMessage(C64NetMessageType.HostOverlayStatus, C64NetProtocol.CreateTextPayload(hostOverlayStatus)));
            }
        }

        private void RemoveClient(ClientConnection client)
        {
            bool removed = false;
            lock (_syncRoot)
            {
                removed = _clients.Remove(client);
            }

            if (removed)
            {
                client.Close();
                BroadcastClientList();
                RaiseStatus("CLIENT " + client.ClientId + " LEFT");
            }
        }

        private ClientConnection FindClient(int clientId)
        {
            lock (_syncRoot)
            {
                for (int index = 0; index < _clients.Count; index++)
                {
                    if (_clients[index].ClientId == clientId)
                    {
                        return _clients[index];
                    }
                }
            }

            return null;
        }

        private void AddBannedRemoteAddress(string remoteAddress)
        {
            if (string.IsNullOrWhiteSpace(remoteAddress) || string.Equals(remoteAddress, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_syncRoot)
            {
                _bannedRemoteAddresses.Add(remoteAddress);
            }
        }

        private bool IsRemoteAddressBanned(string remoteAddress)
        {
            if (string.IsNullOrWhiteSpace(remoteAddress))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _bannedRemoteAddresses.Contains(remoteAddress);
            }
        }

        private List<ClientConnection> GetClients()
        {
            lock (_syncRoot)
            {
                return new List<ClientConnection>(_clients);
            }
        }

        private void BroadcastClientList()
        {
            List<C64NetClientSnapshot> snapshots = GetClientSnapshots();
            var message = CreateMessage(C64NetMessageType.ClientList, C64NetProtocol.CreateClientListPayload(snapshots));
            List<ClientConnection> clients = GetClients();
            for (int index = 0; index < clients.Count; index++)
            {
                clients[index].EnqueueControl(message);
            }
        }

        private static bool PermissionIncludes(C64NetJoystickPermission permission, C64NetJoystickPermission portPermission)
        {
            if (portPermission == C64NetJoystickPermission.Port1)
            {
                return permission == C64NetJoystickPermission.Port1 || permission == C64NetJoystickPermission.Both;
            }

            if (portPermission == C64NetJoystickPermission.Port2)
            {
                return permission == C64NetJoystickPermission.Port2 || permission == C64NetJoystickPermission.Both;
            }

            return false;
        }

        private static C64NetJoystickPermission GetNextPermission(C64NetJoystickPermission permission)
        {
            switch (permission)
            {
                case C64NetJoystickPermission.Observer:
                    return C64NetJoystickPermission.Port1;
                case C64NetJoystickPermission.Port1:
                    return C64NetJoystickPermission.Port2;
                case C64NetJoystickPermission.Port2:
                    return C64NetJoystickPermission.Both;
                default:
                    return C64NetJoystickPermission.Observer;
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

        private static void SendReject(NetworkStream stream, string reason)
        {
            C64NetProtocol.WriteMessage(stream, CreateMessage(C64NetMessageType.ServerReject, C64NetProtocol.CreateTextPayload(reason)));
        }

        private static string FormatRemoteEndpoint(TcpClient tcpClient)
        {
            try
            {
                return tcpClient.Client.RemoteEndPoint == null ? "UNKNOWN" : tcpClient.Client.RemoteEndPoint.ToString();
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        private static string FormatRemoteAddress(TcpClient tcpClient)
        {
            try
            {
                IPEndPoint endpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
                return endpoint == null || endpoint.Address == null ? "UNKNOWN" : endpoint.Address.ToString();
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        private static C64NetMessage CreateMessage(C64NetMessageType type, byte[] payload)
        {
            return new C64NetMessage
            {
                Type = type,
                Timestamp = DateTime.UtcNow.Ticks,
                Payload = payload
            };
        }

        private void RaiseStatus(string status)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                handler(status);
            }
        }

        private sealed class ClientConnection
        {
            private const int MaxAudioQueueLength = 8;
            private readonly C64NetServer _server;
            private readonly TcpClient _tcpClient;
            private readonly object _sendLock = new object();
            private readonly Queue<C64NetMessage> _controlQueue = new Queue<C64NetMessage>();
            private readonly Queue<C64NetMessage> _audioQueue = new Queue<C64NetMessage>();
            private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);
            private C64NetMessage _latestVideo;
            private Task _receiveTask;
            private Task _sendTask;
            private volatile bool _closed;
            private int _latencyMilliseconds;

            public ClientConnection(C64NetServer server, TcpClient tcpClient, int clientId, string name, C64NetClientRole role, C64NetJoystickPermission permission)
            {
                _server = server;
                _tcpClient = tcpClient;
                ClientId = clientId;
                Name = string.IsNullOrWhiteSpace(name) ? "player" : name.Trim();
                RemoteAddress = FormatRemoteAddress(tcpClient);
                RemoteEndpoint = FormatRemoteEndpoint(tcpClient);
                Role = role;
                Permission = permission;
                JoystickState = 0xFF;
            }

            public int ClientId { get; private set; }

            public string Name { get; private set; }

            public string RemoteAddress { get; private set; }

            public string RemoteEndpoint { get; private set; }

            public C64NetClientRole Role { get; private set; }

            public C64NetJoystickPermission Permission { get; set; }

            public byte JoystickState { get; set; }

            public bool IsConnected
            {
                get { return !_closed && _tcpClient.Connected; }
            }

            public void Start(CancellationToken parentCancellation)
            {
                _sendTask = Task.Run(() => SendLoop(parentCancellation));
                _receiveTask = Task.Run(() => ReceiveLoop(parentCancellation));
            }

            public void EnqueueControl(C64NetMessage message)
            {
                if (_closed || message == null)
                {
                    return;
                }

                lock (_sendLock)
                {
                    _controlQueue.Enqueue(message);
                }

                _sendSignal.Set();
            }

            public void EnqueueAudio(C64NetMessage message)
            {
                if (_closed || message == null)
                {
                    return;
                }

                lock (_sendLock)
                {
                    while (_audioQueue.Count >= MaxAudioQueueLength)
                    {
                        _audioQueue.Dequeue();
                    }

                    _audioQueue.Enqueue(message);
                }

                _sendSignal.Set();
            }

            public void EnqueueVideo(C64NetMessage message)
            {
                if (_closed || message == null)
                {
                    return;
                }

                lock (_sendLock)
                {
                    _latestVideo = message;
                }

                _sendSignal.Set();
            }

            public C64NetClientSnapshot CreateSnapshot()
            {
                return new C64NetClientSnapshot
                {
                    ClientId = ClientId,
                    Name = Name,
                    RemoteAddress = RemoteAddress,
                    RemoteEndpoint = RemoteEndpoint,
                    Role = Role,
                    Permission = Permission,
                    JoystickState = JoystickState,
                    LatencyMilliseconds = _latencyMilliseconds,
                    Connected = IsConnected
                };
            }

            public void UpdatePong(long timestamp)
            {
                long elapsedTicks = Math.Max(0, DateTime.UtcNow.Ticks - timestamp);
                _latencyMilliseconds = (int)Math.Min(9999, elapsedTicks / TimeSpan.TicksPerMillisecond);
            }

            public void Close()
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                JoystickState = 0xFF;
                _sendSignal.Set();
                try
                {
                    _tcpClient.Close();
                }
                catch
                {
                }

                _sendSignal.Dispose();
            }

            private void ReceiveLoop(CancellationToken cancellationToken)
            {
                try
                {
                    NetworkStream stream = _tcpClient.GetStream();
                    while (!_closed && !cancellationToken.IsCancellationRequested)
                    {
                        C64NetMessage message = C64NetProtocol.ReadMessage(stream);
                        if (message == null)
                        {
                            break;
                        }

                        _server.HandleClientMessage(this, message);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                finally
                {
                    _server.RemoveClient(this);
                }
            }

            private void SendLoop(CancellationToken cancellationToken)
            {
                try
                {
                    NetworkStream stream = _tcpClient.GetStream();
                    while (!_closed && !cancellationToken.IsCancellationRequested)
                    {
                        C64NetMessage message = DequeueMessage();
                        if (message == null)
                        {
                            _sendSignal.WaitOne(25);
                            continue;
                        }

                        C64NetProtocol.WriteMessage(stream, message);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                finally
                {
                    _server.RemoveClient(this);
                }
            }

            private C64NetMessage DequeueMessage()
            {
                lock (_sendLock)
                {
                    if (_controlQueue.Count > 0)
                    {
                        return _controlQueue.Dequeue();
                    }

                    C64NetMessage video = _latestVideo;
                    if (video != null)
                    {
                        _latestVideo = null;
                        return video;
                    }

                    if (_audioQueue.Count > 0)
                    {
                        return _audioQueue.Dequeue();
                    }

                    return null;
                }
            }
        }
    }
}
