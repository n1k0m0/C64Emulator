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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Input;

namespace C64Emulator.Network
{
    /// <summary>
    /// Hosts a C64Net session and broadcasts the local emulator to connected clients.
    /// </summary>
    /// <remarks>
    /// The server owns only transport state. The C64 simulation remains in
    /// <c>C64Window</c>/<c>C64System</c>; this class accepts clients, validates the
    /// handshake, sends video/audio/control messages, and exposes aggregated joystick
    /// input back to the emulator. Each client has independent send/receive loops so a
    /// slow observer cannot block the host or other clients.
    /// </remarks>
    public sealed class C64NetServer : IDisposable
    {
        /// <summary>
        /// Guards the listener, client list, ban list, and shared host status fields.
        /// </summary>
        private readonly object _syncRoot = new object();
        /// <summary>
        /// Active connections that have completed the handshake.
        /// </summary>
        private readonly List<ClientConnection> _clients = new List<ClientConnection>();
        /// <summary>
        /// Remote IP addresses kicked during the current server session.
        /// </summary>
        private readonly HashSet<string> _bannedRemoteAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Host framebuffer width announced to newly accepted clients.
        /// </summary>
        private readonly int _videoWidth;
        /// <summary>
        /// Host framebuffer height announced to newly accepted clients.
        /// </summary>
        private readonly int _videoHeight;
        private TcpListener _listener;
        private CancellationTokenSource _shutdown;
        private Task _acceptTask;
        private string _password = string.Empty;
        private string _hostOverlayStatus = string.Empty;
        private int _nextClientId = 1;
        private long _frameId;
        private long _bytesSent;
        private long _bytesReceived;

        /// <summary>
        /// Raised when the server has a short user-facing status message for the overlay.
        /// </summary>
        public event Action<string> StatusChanged;

        /// <summary>
        /// Initializes a new host transport for a fixed video size.
        /// </summary>
        /// <param name="videoWidth">Visible C64 framebuffer width.</param>
        /// <param name="videoHeight">Visible C64 framebuffer height.</param>
        public C64NetServer(int videoWidth, int videoHeight)
        {
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
        }

        /// <summary>
        /// Gets whether the TCP listener is currently accepting clients.
        /// </summary>
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

        /// <summary>
        /// Gets the actual listening port. This matters when port zero is used.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Gets the total number of bytes written to clients in the current server session.
        /// </summary>
        public long BytesSent
        {
            get { return Interlocked.Read(ref _bytesSent); }
        }

        /// <summary>
        /// Gets the total number of bytes read from clients in the current server session.
        /// </summary>
        public long BytesReceived
        {
            get { return Interlocked.Read(ref _bytesReceived); }
        }

        /// <summary>
        /// Gets the average round-trip latency across clients with a valid ping sample.
        /// </summary>
        /// <returns>Average latency in milliseconds, or -1 when no sample exists.</returns>
        public int GetAverageLatencyMilliseconds()
        {
            int total = 0;
            int count = 0;
            lock (_syncRoot)
            {
                for (int index = 0; index < _clients.Count; index++)
                {
                    int latency = _clients[index].LatencyMilliseconds;
                    if (latency >= 0)
                    {
                        total += latency;
                        count++;
                    }
                }
            }

            return count == 0 ? -1 : (int)Math.Round(total / (double)count);
        }

        /// <summary>
        /// Starts listening for clients.
        /// </summary>
        /// <param name="port">TCP port to listen on.</param>
        /// <param name="password">Optional clear-text session password.</param>
        public void Start(int port, string password)
        {
            // Restarting creates a fresh session. That also clears per-session kicks
            // exactly as the network menu promises.
            Stop();
            _password = password ?? string.Empty;
            _hostOverlayStatus = string.Empty;
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _bytesReceived, 0);
            lock (_syncRoot)
            {
                _bannedRemoteAddresses.Clear();
            }

            _shutdown = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            // Accepting is isolated from the render/emulation thread.
            _acceptTask = Task.Run(() => AcceptLoop(_shutdown.Token));
            RaiseStatus("TLS SERVER LISTENING " + Port);
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
                // Null the listener under the lock first so IsRunning flips to false
                // before sockets start closing and callbacks begin to unwind.
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
                    // Stop unblocks AcceptTcpClient on the accept loop.
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
                // ClientConnection.Close is idempotent; calling it here and from worker
                // loops is fine and simplifies shutdown paths.
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
        /// <param name="pixels">ARGB32 pixels from the completed host framebuffer.</param>
        /// <param name="width">Frame width.</param>
        /// <param name="height">Frame height.</param>
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

            byte[] packedPalettePixels = C64NetProtocol.CreatePackedVideoFrame(pixels, width, height);
            if (packedPalettePixels.Length == 0)
            {
                return;
            }

            // Pack the host frame once, then let each client choose full/delta encoding
            // against its own last transmitted reference frame at send time.
            var frame = new PendingVideoFrame(packedPalettePixels, width, height, Interlocked.Increment(ref _frameId));
            for (int index = 0; index < clients.Count; index++)
            {
                clients[index].EnqueueVideo(frame);
            }
        }

        /// <summary>
        /// Broadcasts one PCM audio chunk.
        /// </summary>
        /// <param name="buffer">PCM audio bytes generated by the host SID.</param>
        /// <param name="count">Number of valid bytes in <paramref name="buffer"/>.</param>
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

            // Like video, audio payloads are serialized once and shared between client
            // queues. Per-client backpressure is handled inside ClientConnection.
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
        /// <param name="status">Popup text, or an empty string to clear it.</param>
        public void SetHostOverlayStatus(string status)
        {
            status = status ?? string.Empty;
            lock (_syncRoot)
            {
                if (string.Equals(_hostOverlayStatus, status, StringComparison.Ordinal))
                {
                    // Avoid spamming the control queue every render tick while the host
                    // remains in the same menu.
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
        /// <returns>Copy of the current client list for UI rendering or protocol broadcast.</returns>
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
        /// <param name="clientId">Host-assigned client id.</param>
        /// <param name="permission">New joystick permission.</param>
        public void SetClientPermission(int clientId, C64NetJoystickPermission permission)
        {
            ClientConnection client = FindClient(clientId);
            if (client == null)
            {
                return;
            }

            // The changed client receives a dedicated permission update, while all
            // clients receive the refreshed list so observers see the new state too.
            client.Permission = permission;
            client.EnqueueControl(CreateMessage(C64NetMessageType.PermissionUpdate, C64NetProtocol.CreatePermissionPayload(permission, client.KeyboardEnabled)));
            BroadcastClientList();
            RaiseStatus("CLIENT " + clientId + " " + FormatPermission(permission));
        }

        /// <summary>
        /// Changes whether a client may send C64 keyboard matrix input.
        /// </summary>
        /// <param name="clientId">Host-assigned client id.</param>
        /// <param name="enabled">True to accept keyboard input from this client.</param>
        public void SetClientKeyboardEnabled(int clientId, bool enabled)
        {
            ClientConnection client = FindClient(clientId);
            if (client == null)
            {
                return;
            }

            client.KeyboardEnabled = enabled;
            if (!enabled)
            {
                client.ClearKeyboardState();
            }

            client.EnqueueControl(CreateMessage(C64NetMessageType.PermissionUpdate, C64NetProtocol.CreatePermissionPayload(client.Permission, client.KeyboardEnabled)));
            BroadcastClientList();
            RaiseStatus("CLIENT " + clientId + " KEYBOARD " + (enabled ? "ON" : "OFF"));
        }

        /// <summary>
        /// Cycles the joystick permission of a client.
        /// </summary>
        /// <param name="clientId">Host-assigned client id.</param>
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
        /// <param name="clientId">Host-assigned client id.</param>
        public void KickClient(int clientId)
        {
            ClientConnection client = FindClient(clientId);
            if (client == null)
            {
                return;
            }

            // Kicks are session-local bans by remote IP. A server restart clears the set.
            AddBannedRemoteAddress(client.RemoteAddress);
            client.EnqueueControl(CreateMessage(C64NetMessageType.Disconnect, C64NetProtocol.CreateTextPayload("KICKED BY HOST")));
            client.Close();
            RemoveClient(client);
            RaiseStatus("CLIENT " + clientId + " KICKED");
        }

        /// <summary>
        /// Aggregates every client input for the requested port using C64 active-low bits.
        /// </summary>
        /// <param name="portPermission">The port whose granted clients should be sampled.</param>
        /// <returns>Combined active-low joystick state for the requested C64 port.</returns>
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

                    // Active-low joystick bits combine with AND: any client pulling a
                    // direction/fire line low makes it visible to the emulated CIA.
                    state = (byte)(state & client.JoystickState);
                }
            }

            return (byte)(state | 0xE0);
        }

        /// <summary>
        /// Aggregates all currently pressed C64 keyboard keys from clients with keyboard rights.
        /// </summary>
        /// <returns>Set of frontend keys that should be held in the host C64 keyboard matrix.</returns>
        public HashSet<Key> GetAggregatedKeyboardKeys()
        {
            var keys = new HashSet<Key>();
            lock (_syncRoot)
            {
                for (int index = 0; index < _clients.Count; index++)
                {
                    ClientConnection client = _clients[index];
                    if (!client.IsConnected || !client.KeyboardEnabled)
                    {
                        continue;
                    }

                    List<Key> clientKeys = client.GetPressedKeyboardKeys();
                    for (int keyIndex = 0; keyIndex < clientKeys.Count; keyIndex++)
                    {
                        keys.Add(clientKeys[keyIndex]);
                    }
                }
            }

            return keys;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Accepts incoming TCP sockets until the server is stopped.
        /// </summary>
        /// <param name="cancellationToken">Server shutdown token.</param>
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
                    // The stream carries interactive input and frame/audio data. Disable
                    // Nagle so tiny control packets are not delayed behind batching.
                    tcpClient.NoDelay = true;
                    // Each handshake runs independently; a slow or malicious client
                    // should not prevent the listener from accepting the next socket.
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

        /// <summary>
        /// Validates the client handshake and promotes an accepted socket to a connection.
        /// </summary>
        /// <param name="tcpClient">Freshly accepted socket.</param>
        /// <param name="cancellationToken">Server shutdown token passed to client loops.</param>
        private void HandleAcceptedClient(TcpClient tcpClient, CancellationToken cancellationToken)
        {
            ClientConnection client = null;
            try
            {
                Stream stream = C64NetTls.AuthenticateServer(tcpClient);
                C64NetMessage hello = C64NetProtocol.ReadMessage(stream);
                AddBytesReceived(hello != null ? hello.WireLength : 0);
                if (hello == null || hello.Type != C64NetMessageType.ClientHello)
                {
                    // The protocol is strict at the handshake boundary. Unknown first
                    // messages are rejected before a ClientConnection is created.
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
                    // Version is checked before password or role so incompatible clients
                    // get a deterministic error.
                    SendReject(stream, "PROTOCOL MISMATCH");
                    tcpClient.Close();
                    return;
                }

                string remoteAddress = FormatRemoteAddress(tcpClient);
                if (IsRemoteAddressBanned(remoteAddress))
                {
                    // Session bans are keyed by IP address because a kicked client may
                    // reconnect with a new TCP port immediately.
                    RaiseStatus("BANNED CLIENT " + remoteAddress);
                    SendReject(stream, "KICKED FROM SESSION");
                    tcpClient.Close();
                    return;
                }

                if (!string.Equals(_password ?? string.Empty, password ?? string.Empty, StringComparison.Ordinal))
                {
                    // A wrong password is surfaced to the host overlay so the server
                    // operator can see rejected connection attempts.
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
                // Even "Player" joins as observer first. The host grants the actual
                // joystick port explicitly from the client list.
                client = new ClientConnection(this, tcpClient, stream, clientId, name, role, permission);
                AddBytesSent(C64NetProtocol.WriteMessage(stream, CreateMessage(
                    C64NetMessageType.ServerWelcome,
                    C64NetProtocol.CreateServerWelcomePayload(clientId, _videoWidth, _videoHeight, C64NetProtocol.DefaultAudioSampleRate, role, permission, client.KeyboardEnabled, "TLS CONNECTED"))));

                // Add the client only after welcome was sent, so the UI never shows a
                // half-handshaken socket.
                AddClient(client);
                client.Start(cancellationToken);
                RaiseStatus("CLIENT " + clientId + " JOINED");
            }
            catch (AuthenticationException ex)
            {
                Debug.WriteLine(ex);
                RaiseStatus("TLS FAILED " + FormatRemoteEndpoint(tcpClient));
                try
                {
                    tcpClient.Close();
                }
                catch
                {
                }
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

        /// <summary>
        /// Dispatches one client-to-server message.
        /// </summary>
        /// <param name="client">Connection that sent the message.</param>
        /// <param name="message">Decoded protocol message.</param>
        private void HandleClientMessage(ClientConnection client, C64NetMessage message)
        {
            if (client == null || message == null)
            {
                return;
            }

            switch (message.Type)
            {
                case C64NetMessageType.InputState:
                    // Store only the latest input. The emulator thread samples it once
                    // per render update and combines all granted clients there.
                    client.JoystickState = C64NetProtocol.ReadInputStatePayload(message.Payload);
                    break;
                case C64NetMessageType.KeyboardInput:
                    if (client.KeyboardEnabled && C64NetProtocol.ReadKeyboardInputPayload(message.Payload, out Key key, out bool pressed))
                    {
                        client.SetKeyboardKeyState(key, pressed);
                    }

                    break;
                case C64NetMessageType.Ping:
                    // Echo the payload and a timestamp-preserving pong for round-trip
                    // measurements on the client side.
                    client.EnqueueControl(new C64NetMessage
                    {
                        Type = C64NetMessageType.Pong,
                        Timestamp = message.Timestamp,
                        Payload = message.Payload
                    });
                    break;
                case C64NetMessageType.Pong:
                    client.UpdatePong(message.Timestamp);
                    BroadcastClientList();
                    break;
                case C64NetMessageType.Disconnect:
                    // A client-initiated leave is normal, so simply remove it.
                    client.Close();
                    RemoveClient(client);
                    break;
            }
        }

        /// <summary>
        /// Adds an accepted client and publishes the updated visible client list.
        /// </summary>
        /// <param name="client">Accepted connection.</param>
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
                // New clients should immediately see why the remote picture might be
                // frozen if the host is already sitting in a menu.
                client.EnqueueControl(CreateMessage(C64NetMessageType.HostOverlayStatus, C64NetProtocol.CreateTextPayload(hostOverlayStatus)));
            }
        }

        /// <summary>
        /// Removes a client, closes the socket, and publishes the new client list.
        /// </summary>
        /// <param name="client">Connection to remove.</param>
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

        /// <summary>
        /// Looks up an active client by host-assigned id.
        /// </summary>
        /// <param name="clientId">Host-assigned client id.</param>
        /// <returns>The active connection, or null when the id is no longer connected.</returns>
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

        /// <summary>
        /// Adds an IP address to the current session's kick-ban set.
        /// </summary>
        /// <param name="remoteAddress">Remote IP address without port.</param>
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

        /// <summary>
        /// Checks whether a remote IP address has been kicked from this server session.
        /// </summary>
        /// <param name="remoteAddress">Remote IP address without port.</param>
        /// <returns>True when the address is currently banned.</returns>
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

        /// <summary>
        /// Copies the active client list so callers can iterate without holding the lock.
        /// </summary>
        /// <returns>Stable client connection list copy.</returns>
        private List<ClientConnection> GetClients()
        {
            lock (_syncRoot)
            {
                return new List<ClientConnection>(_clients);
            }
        }

        /// <summary>
        /// Broadcasts the current client snapshots to every remote client.
        /// </summary>
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

        /// <summary>
        /// Tests whether a granted permission includes a requested physical port.
        /// </summary>
        /// <param name="permission">Client permission assigned by the host.</param>
        /// <param name="portPermission">Physical port being sampled.</param>
        /// <returns>True when the client may influence that port.</returns>
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

        /// <summary>
        /// Returns the next permission in the host menu cycle.
        /// </summary>
        /// <param name="permission">Current permission.</param>
        /// <returns>Next permission shown by the network menu.</returns>
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

        /// <summary>
        /// Formats a joystick permission for overlay status text.
        /// </summary>
        /// <param name="permission">Permission to format.</param>
        /// <returns>Short display string.</returns>
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

        /// <summary>
        /// Sends a handshake rejection and reason text.
        /// </summary>
        /// <param name="stream">Stream to the not-yet-accepted client.</param>
        /// <param name="reason">Human-readable rejection reason.</param>
        private void SendReject(Stream stream, string reason)
        {
            AddBytesSent(C64NetProtocol.WriteMessage(stream, CreateMessage(C64NetMessageType.ServerReject, C64NetProtocol.CreateTextPayload(reason))));
        }

        /// <summary>
        /// Formats a remote endpoint for status messages.
        /// </summary>
        /// <param name="tcpClient">Socket whose remote endpoint should be read.</param>
        /// <returns>Endpoint string or UNKNOWN when unavailable.</returns>
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

        /// <summary>
        /// Formats only the remote IP address for session-ban matching.
        /// </summary>
        /// <param name="tcpClient">Socket whose remote address should be read.</param>
        /// <returns>IP address string or UNKNOWN when unavailable.</returns>
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

        /// <summary>
        /// Creates a protocol message with a fresh UTC timestamp.
        /// </summary>
        /// <param name="type">Protocol message type.</param>
        /// <param name="payload">Serialized message payload.</param>
        /// <returns>Message ready to enqueue or write.</returns>
        private static C64NetMessage CreateMessage(C64NetMessageType type, byte[] payload)
        {
            return new C64NetMessage
            {
                Type = type,
                Timestamp = DateTime.UtcNow.Ticks,
                Payload = payload
            };
        }

        /// <summary>
        /// Raises a user-visible server status event.
        /// </summary>
        /// <param name="status">Status text for the network overlay/toast.</param>
        private void RaiseStatus(string status)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                handler(status);
            }
        }

        /// <summary>
        /// Adds bytes written by any server-side socket.
        /// </summary>
        /// <param name="byteCount">Number of bytes written to the TCP stream.</param>
        private void AddBytesSent(int byteCount)
        {
            if (byteCount > 0)
            {
                Interlocked.Add(ref _bytesSent, byteCount);
            }
        }

        /// <summary>
        /// Adds bytes read by any server-side socket.
        /// </summary>
        /// <param name="byteCount">Number of bytes read from the TCP stream.</param>
        private void AddBytesReceived(int byteCount)
        {
            if (byteCount > 0)
            {
                Interlocked.Add(ref _bytesReceived, byteCount);
            }
        }

        /// <summary>
        /// Immutable host frame prepared once before it is queued for clients.
        /// </summary>
        private sealed class PendingVideoFrame
        {
            /// <summary>
            /// Initializes a packed frame queued for one or more clients.
            /// </summary>
            /// <param name="packedPalettePixels">Packed 4-bit C64 palette pixels.</param>
            /// <param name="width">Frame width.</param>
            /// <param name="height">Frame height.</param>
            /// <param name="frameId">Host completed frame id.</param>
            public PendingVideoFrame(byte[] packedPalettePixels, int width, int height, long frameId)
            {
                PackedPalettePixels = packedPalettePixels;
                Width = width;
                Height = height;
                FrameId = frameId;
            }

            /// <summary>
            /// Gets the packed 4-bit C64 palette frame.
            /// </summary>
            public byte[] PackedPalettePixels { get; private set; }

            /// <summary>
            /// Gets the frame width.
            /// </summary>
            public int Width { get; private set; }

            /// <summary>
            /// Gets the frame height.
            /// </summary>
            public int Height { get; private set; }

            /// <summary>
            /// Gets the host completed frame id.
            /// </summary>
            public long FrameId { get; private set; }
        }

        /// <summary>
        /// Per-client transport state with independent receive and prioritized send queues.
        /// </summary>
        private sealed class ClientConnection
        {
            /// <summary>
            /// Maximum queued audio chunks per client before the oldest audio is dropped.
            /// </summary>
            private const int MaxAudioQueueLength = 8;
            /// <summary>
            /// Maximum number of delta frames before forcing a full keyframe.
            /// </summary>
            private const int MaxDeltaFramesBeforeKeyFrame = 120;
            /// <summary>
            /// Time between latency probes while the previous probe has completed.
            /// </summary>
            private const long LatencyPingIntervalTicks = TimeSpan.TicksPerSecond;
            /// <summary>
            /// Time after which an unanswered latency probe may be retried.
            /// </summary>
            private const long LatencyPingTimeoutTicks = TimeSpan.TicksPerSecond * 3;
            private readonly C64NetServer _server;
            private readonly TcpClient _tcpClient;
            private readonly Stream _stream;
            private readonly object _sendLock = new object();
            private readonly object _keyboardLock = new object();
            private readonly HashSet<Key> _pressedKeyboardKeys = new HashSet<Key>();
            /// <summary>
            /// Reliable control messages that must be delivered in order before media.
            /// </summary>
            private readonly Queue<C64NetMessage> _controlQueue = new Queue<C64NetMessage>();
            /// <summary>
            /// Audio chunks are allowed to lag slightly, but bounded to avoid memory growth.
            /// </summary>
            private readonly Queue<C64NetMessage> _audioQueue = new Queue<C64NetMessage>();
            private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);
            /// <summary>
            /// Latest pending video frame. Replacing this drops stale frames for slow clients.
            /// </summary>
            private PendingVideoFrame _latestVideo;
            private Task _receiveTask;
            private Task _sendTask;
            private volatile bool _closed;
            private int _latencyMilliseconds = -1;
            private bool _latencyPingPending;
            private long _lastLatencyPingTicks;
            private byte[] _lastSentVideoPalettePixels;
            private long _lastSentVideoFrameId;
            private int _deltaFramesSinceKeyFrame;

            /// <summary>
            /// Initializes a connection after the server has accepted the handshake.
            /// </summary>
            /// <param name="server">Owning server instance.</param>
            /// <param name="tcpClient">Accepted TCP socket.</param>
            /// <param name="stream">Authenticated TLS stream for this client.</param>
            /// <param name="clientId">Host-assigned client id.</param>
            /// <param name="name">Player name from the handshake.</param>
            /// <param name="role">Accepted role.</param>
            /// <param name="permission">Initial joystick permission.</param>
            public ClientConnection(C64NetServer server, TcpClient tcpClient, Stream stream, int clientId, string name, C64NetClientRole role, C64NetJoystickPermission permission)
            {
                _server = server;
                _tcpClient = tcpClient;
                _stream = stream;
                ClientId = clientId;
                Name = string.IsNullOrWhiteSpace(name) ? "player" : name.Trim();
                RemoteAddress = FormatRemoteAddress(tcpClient);
                RemoteEndpoint = FormatRemoteEndpoint(tcpClient);
                Role = role;
                Permission = permission;
                JoystickState = 0xFF;
            }

            /// <summary>
            /// Gets the host-assigned session-local client id.
            /// </summary>
            public int ClientId { get; private set; }

            /// <summary>
            /// Gets the player name shown in the overlay client list.
            /// </summary>
            public string Name { get; private set; }

            /// <summary>
            /// Gets the remote IP address used for session kick bans.
            /// </summary>
            public string RemoteAddress { get; private set; }

            /// <summary>
            /// Gets the full remote endpoint string used for diagnostics.
            /// </summary>
            public string RemoteEndpoint { get; private set; }

            /// <summary>
            /// Gets the accepted client role.
            /// </summary>
            public C64NetClientRole Role { get; private set; }

            /// <summary>
            /// Gets or sets the current host-granted joystick permission.
            /// </summary>
            public C64NetJoystickPermission Permission { get; set; }

            /// <summary>
            /// Gets or sets whether host accepts C64 keyboard input from this client.
            /// </summary>
            public bool KeyboardEnabled { get; set; }

            /// <summary>
            /// Gets or sets the last active-low joystick state received from this client.
            /// </summary>
            public byte JoystickState { get; set; }

            /// <summary>
            /// Gets the latest measured round-trip latency in milliseconds.
            /// </summary>
            public int LatencyMilliseconds
            {
                get { return Volatile.Read(ref _latencyMilliseconds); }
            }

            /// <summary>
            /// Gets whether the socket is still considered connected by this process.
            /// </summary>
            public bool IsConnected
            {
                get { return !_closed && _tcpClient.Connected; }
            }

            /// <summary>
            /// Starts the per-client send and receive loops.
            /// </summary>
            /// <param name="parentCancellation">Server shutdown token.</param>
            public void Start(CancellationToken parentCancellation)
            {
                _sendTask = Task.Run(() => SendLoop(parentCancellation));
                _receiveTask = Task.Run(() => ReceiveLoop(parentCancellation));
            }

            /// <summary>
            /// Enqueues a high-priority control message.
            /// </summary>
            /// <param name="message">Message to send.</param>
            public void EnqueueControl(C64NetMessage message)
            {
                if (_closed || message == null)
                {
                    return;
                }

                lock (_sendLock)
                {
                    // Control messages carry permissions, disconnects, and client lists;
                    // never drop them unless the whole socket is closed.
                    _controlQueue.Enqueue(message);
                }

                _sendSignal.Set();
            }

            /// <summary>
            /// Enqueues a bounded audio message.
            /// </summary>
            /// <param name="message">Audio chunk to send.</param>
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
                        // Audio is time-sensitive. Dropping old chunks is less harmful
                        // than letting a slow client drift seconds behind the host.
                        _audioQueue.Dequeue();
                    }

                    _audioQueue.Enqueue(message);
                }

                _sendSignal.Set();
            }

            /// <summary>
            /// Stores the latest pending video message.
            /// </summary>
            /// <param name="frame">Packed video frame to send.</param>
            public void EnqueueVideo(PendingVideoFrame frame)
            {
                if (_closed || frame == null)
                {
                    return;
                }

                lock (_sendLock)
                {
                    // Only keep the newest frame. This is the main protection that keeps
                    // one slow client from building an ever-growing video backlog.
                    _latestVideo = frame;
                }

                _sendSignal.Set();
            }

            /// <summary>
            /// Creates a UI/protocol snapshot of this connection.
            /// </summary>
            /// <returns>Current immutable snapshot values.</returns>
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
                    KeyboardEnabled = KeyboardEnabled,
                    JoystickState = JoystickState,
                    LatencyMilliseconds = LatencyMilliseconds,
                    Connected = IsConnected
                };
            }

            /// <summary>
            /// Records one remote keyboard key press or release.
            /// </summary>
            /// <param name="key">Frontend key.</param>
            /// <param name="pressed">True for key down, false for key up.</param>
            public void SetKeyboardKeyState(Key key, bool pressed)
            {
                lock (_keyboardLock)
                {
                    if (pressed)
                    {
                        _pressedKeyboardKeys.Add(key);
                    }
                    else
                    {
                        _pressedKeyboardKeys.Remove(key);
                    }
                }
            }

            /// <summary>
            /// Clears all currently held remote keyboard keys.
            /// </summary>
            public void ClearKeyboardState()
            {
                lock (_keyboardLock)
                {
                    _pressedKeyboardKeys.Clear();
                }
            }

            /// <summary>
            /// Returns a copy of currently held remote keyboard keys.
            /// </summary>
            /// <returns>Pressed frontend keys.</returns>
            public List<Key> GetPressedKeyboardKeys()
            {
                lock (_keyboardLock)
                {
                    return new List<Key>(_pressedKeyboardKeys);
                }
            }

            /// <summary>
            /// Updates the latency estimate from a pong timestamp.
            /// </summary>
            /// <param name="timestamp">Original ping timestamp in UTC ticks.</param>
            public void UpdatePong(long timestamp)
            {
                long elapsedTicks = Math.Max(0, DateTime.UtcNow.Ticks - timestamp);
                Volatile.Write(ref _latencyMilliseconds, (int)Math.Min(9999, elapsedTicks / TimeSpan.TicksPerMillisecond));
                lock (_sendLock)
                {
                    _latencyPingPending = false;
                }
            }

            /// <summary>
            /// Closes the socket and wakes the send loop so it can exit.
            /// </summary>
            public void Close()
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                // Release all emulated joystick lines immediately when a client leaves.
                JoystickState = 0xFF;
                ClearKeyboardState();
                _sendSignal.Set();
                try
                {
                    _stream.Dispose();
                }
                catch
                {
                }

                try
                {
                    _tcpClient.Close();
                }
                catch
                {
                }

                _sendSignal.Dispose();
            }

            /// <summary>
            /// Receives client-to-server messages until the connection closes.
            /// </summary>
            /// <param name="cancellationToken">Server shutdown token.</param>
            private void ReceiveLoop(CancellationToken cancellationToken)
            {
                try
                {
                    Stream stream = _stream;
                    while (!_closed && !cancellationToken.IsCancellationRequested)
                    {
                        C64NetMessage message = C64NetProtocol.ReadMessage(stream);
                        if (message == null)
                        {
                            // Null means EOF/closed stream. The finally block performs
                            // the shared removal path.
                            break;
                        }

                        _server.AddBytesReceived(message.WireLength);
                        _server.HandleClientMessage(this, message);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                finally
                {
                    // Both send and receive loops may arrive here. RemoveClient is safe
                    // to call repeatedly because it checks whether the list changed.
                    _server.RemoveClient(this);
                }
            }

            /// <summary>
            /// Sends queued server-to-client messages using control/video/audio priority.
            /// </summary>
            /// <param name="cancellationToken">Server shutdown token.</param>
            private void SendLoop(CancellationToken cancellationToken)
            {
                try
                {
                    Stream stream = _stream;
                    while (!_closed && !cancellationToken.IsCancellationRequested)
                    {
                        C64NetMessage message = DequeueMessage();
                        if (message == null)
                        {
                            // Wake periodically in case cancellation happens without a
                            // queued message, but otherwise wait for producers to signal.
                            _sendSignal.WaitOne(25);
                            continue;
                        }

                        _server.AddBytesSent(C64NetProtocol.WriteMessage(stream, message));
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

            /// <summary>
            /// Dequeues the next message according to the desired transport priority.
            /// </summary>
            /// <returns>Next message to write, or null when all queues are empty.</returns>
            private C64NetMessage DequeueMessage()
            {
                lock (_sendLock)
                {
                    if (_controlQueue.Count > 0)
                    {
                        // Permissions, disconnects, client lists, and overlay popups must
                        // be observed even if media is flowing quickly.
                        return _controlQueue.Dequeue();
                    }

                    C64NetMessage ping = CreateLatencyPingIfDue(DateTime.UtcNow.Ticks);
                    if (ping != null)
                    {
                        return ping;
                    }

                    PendingVideoFrame video = _latestVideo;
                    if (video != null)
                    {
                        // Video outranks audio because the visual state should always be
                        // the freshest possible remote frame.
                        _latestVideo = null;
                        C64NetMessage videoMessage = CreateVideoMessage(video);
                        if (videoMessage != null)
                        {
                            return videoMessage;
                        }
                    }

                    if (_audioQueue.Count > 0)
                    {
                        return _audioQueue.Dequeue();
                    }

                    return null;
                }
            }

            /// <summary>
            /// Encodes a queued frame as either a full keyframe or a delta from this client reference.
            /// </summary>
            /// <param name="video">Packed frame selected by the send queue.</param>
            /// <returns>Protocol message ready to write, or null when encoding fails.</returns>
            private C64NetMessage CreateVideoMessage(PendingVideoFrame video)
            {
                if (C64NetProtocol.PackedVideoFramesEqual(video.PackedPalettePixels, _lastSentVideoPalettePixels))
                {
                    // No visual byte changed since the last frame this client received.
                    // TCP/TLS is reliable, so the client can keep presenting its current
                    // image and we can spend zero bandwidth for this completed C64 frame.
                    return null;
                }

                bool forceKeyFrame = _lastSentVideoPalettePixels == null || _deltaFramesSinceKeyFrame >= MaxDeltaFramesBeforeKeyFrame;
                byte[] previous = forceKeyFrame ? null : _lastSentVideoPalettePixels;
                long previousFrameId = forceKeyFrame ? 0 : _lastSentVideoFrameId;
                bool usedDelta;
                byte[] payload = C64NetProtocol.CreateBestVideoFramePayload(
                    video.PackedPalettePixels,
                    previous,
                    video.Width,
                    video.Height,
                    video.FrameId,
                    previousFrameId,
                    out usedDelta);
                if (payload == null || payload.Length == 0)
                {
                    return null;
                }

                // Update the reference after payload creation. If the following socket
                // write fails, this connection is removed anyway.
                _lastSentVideoPalettePixels = video.PackedPalettePixels;
                _lastSentVideoFrameId = video.FrameId;
                _deltaFramesSinceKeyFrame = usedDelta ? _deltaFramesSinceKeyFrame + 1 : 0;
                return CreateMessage(C64NetMessageType.VideoFrame, payload);
            }

            /// <summary>
            /// Creates a latency probe when the probe interval or retry timeout elapsed.
            /// </summary>
            /// <param name="nowTicks">Current UTC ticks.</param>
            /// <returns>Ping message to send, or null when no probe is due.</returns>
            private C64NetMessage CreateLatencyPingIfDue(long nowTicks)
            {
                long elapsedTicks = nowTicks - _lastLatencyPingTicks;
                if (_latencyPingPending)
                {
                    if (elapsedTicks < LatencyPingTimeoutTicks)
                    {
                        return null;
                    }
                }
                else if (_lastLatencyPingTicks != 0 && elapsedTicks < LatencyPingIntervalTicks)
                {
                    return null;
                }

                _lastLatencyPingTicks = nowTicks;
                _latencyPingPending = true;
                return new C64NetMessage
                {
                    Type = C64NetMessageType.Ping,
                    Timestamp = nowTicks,
                    Payload = new byte[0]
                };
            }
        }
    }
}
