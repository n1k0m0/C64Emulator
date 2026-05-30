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
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using C64Emulator.Core;
using OpenTK.Input;

namespace C64Emulator.Network
{
    /// <summary>
    /// Receives a remote C64Net session and exposes frames/audio/input state to the window.
    /// </summary>
    /// <remarks>
    /// A connected remote client deliberately does not run the local C64 simulation.
    /// It receives completed host frames and host audio, raises events back to the
    /// window, and sends local joystick state upstream when the host grants input
    /// rights. The UI remains responsible for drawing filters, fullscreen, and menus.
    /// </remarks>
    public sealed class C64NetClient : IDisposable
    {
        /// <summary>
        /// Time between client-initiated latency probes after a completed sample.
        /// </summary>
        private const long LatencyPingIntervalTicks = TimeSpan.TicksPerSecond;
        /// <summary>
        /// Time after which an unanswered latency probe may be retried.
        /// </summary>
        private const long LatencyPingTimeoutTicks = TimeSpan.TicksPerSecond * 3;
        /// <summary>
        /// Serializes writes from UI/input callbacks against ping/pong responses.
        /// </summary>
        private readonly object _sendLock = new object();
        private TcpClient _tcpClient;
        private Stream _stream;
        private CancellationTokenSource _shutdown;
        private Task _receiveTask;
        private SidAudioOutput _audioOutput;
        /// <summary>
        /// Last joystick state sent to the server, used to suppress unchanged packets.
        /// </summary>
        private byte _lastSentJoystickState = 0xFF;
        /// <summary>
        /// Last full packed frame accepted by the client, used as the base for deltas.
        /// </summary>
        private byte[] _videoReferencePalettePixels;
        /// <summary>
        /// Frame id belonging to <see cref="_videoReferencePalettePixels"/>.
        /// </summary>
        private long _videoReferenceFrameId;
        /// <summary>
        /// Reusable ARGB32 decode target to avoid allocating one large video array per network frame.
        /// </summary>
        private uint[] _videoDecodePixels;
        private long _bytesSent;
        private long _bytesReceived;
        private volatile bool _connected;
        private int _latencyMilliseconds = -1;
        private int _latencyPingPending;
        private long _lastLatencyPingTicks;
        private string _serverCertificateFingerprint = "UNKNOWN";

        /// <summary>
        /// Raised when a complete host video frame has been decoded.
        /// </summary>
        public event Action<C64NetVideoFrame> FrameReceived;
        /// <summary>
        /// Raised when the host broadcasts the current client list.
        /// </summary>
        public event Action<List<C64NetClientSnapshot>> ClientListReceived;
        /// <summary>
        /// Raised when the host enters or leaves a local menu overlay.
        /// </summary>
        public event Action<string> HostOverlayStatusChanged;
        /// <summary>
        /// Raised when the client has a short user-facing connection status update.
        /// </summary>
        public event Action<string> StatusChanged;

        /// <summary>
        /// Gets the host-assigned session-local client id.
        /// </summary>
        public int ClientId { get; private set; }
        /// <summary>
        /// Gets the remote framebuffer width announced by the host.
        /// </summary>
        public int VideoWidth { get; private set; }
        /// <summary>
        /// Gets the remote framebuffer height announced by the host.
        /// </summary>
        public int VideoHeight { get; private set; }
        /// <summary>
        /// Gets the host audio sample rate.
        /// </summary>
        public int AudioSampleRate { get; private set; }
        /// <summary>
        /// Gets the role accepted by the host.
        /// </summary>
        public C64NetClientRole Role { get; private set; }
        /// <summary>
        /// Gets the current host-granted joystick permission.
        /// </summary>
        public C64NetJoystickPermission Permission { get; private set; }
        /// <summary>
        /// Gets whether the host currently accepts C64 keyboard input from this client.
        /// </summary>
        public bool KeyboardEnabled { get; private set; }
        /// <summary>
        /// Gets the latest status text shown in the network overlay.
        /// </summary>
        public string StatusText { get; private set; } = "DISCONNECTED";

        /// <summary>
        /// Gets the short SHA-256 fingerprint prefix of the connected/pinned server certificate.
        /// </summary>
        public string ServerCertificateFingerprint
        {
            get { return _serverCertificateFingerprint; }
        }

        /// <summary>
        /// Gets the total number of bytes written to the server in the current session.
        /// </summary>
        public long BytesSent
        {
            get { return Interlocked.Read(ref _bytesSent); }
        }

        /// <summary>
        /// Gets the total number of bytes read from the server in the current session.
        /// </summary>
        public long BytesReceived
        {
            get { return Interlocked.Read(ref _bytesReceived); }
        }

        /// <summary>
        /// Gets the latest measured round-trip latency to the host.
        /// </summary>
        public int LatencyMilliseconds
        {
            get { return Volatile.Read(ref _latencyMilliseconds); }
        }

        /// <summary>
        /// Gets whether the client considers the remote session active.
        /// </summary>
        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>
        /// Connects to a host session.
        /// </summary>
        /// <param name="host">Host name or IP address from the network client menu.</param>
        /// <param name="port">TCP port from the network client menu.</param>
        /// <param name="password">Optional session password.</param>
        /// <param name="role">Requested client role.</param>
        /// <param name="name">Player name to show on the host.</param>
        /// <param name="status">Result status text for the overlay.</param>
        /// <returns>True when the handshake completed and the receive loop started.</returns>
        public bool Connect(string host, int port, string password, C64NetClientRole role, string name, out string status)
        {
            // Always start from a clean transport state. Reusing a half-closed stream can
            // otherwise leak old audio output or receive loops into the next connection.
            Disconnect();
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _bytesReceived, 0);
            status = string.Empty;
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                Task connectTask = _tcpClient.ConnectAsync(host, port);
                if (!connectTask.Wait(3000))
                {
                    // Keep the UI responsive if a host address silently drops packets.
                    status = "CONNECT TIMEOUT";
                    Disconnect();
                    return false;
                }

                string tlsStatus;
                _stream = C64NetTls.AuthenticateClient(_tcpClient, host, port, out tlsStatus);
                if (!string.IsNullOrWhiteSpace(tlsStatus))
                {
                    StatusText = tlsStatus;
                    RaiseStatus(tlsStatus);
                }

                _serverCertificateFingerprint = C64NetTls.GetTrustedServerShortFingerprint(host, port);

                // ClientHello is the only message the server accepts before welcome/reject.
                AddBytesSent(C64NetProtocol.WriteMessage(_stream, new C64NetMessage
                {
                    Type = C64NetMessageType.ClientHello,
                    Timestamp = DateTime.UtcNow.Ticks,
                    Payload = C64NetProtocol.CreateClientHelloPayload(name, password, role)
                }));

                C64NetMessage response = C64NetProtocol.ReadMessage(_stream);
                AddBytesReceived(response != null ? response.WireLength : 0);
                if (response == null)
                {
                    // Socket closed before the host sent a protocol response.
                    status = "NO SERVER RESPONSE";
                    Disconnect();
                    return false;
                }

                if (response.Type == C64NetMessageType.ServerReject)
                {
                    // Surface the server's reason verbatim; it is already short overlay text.
                    status = C64NetProtocol.ReadTextPayload(response.Payload);
                    Disconnect();
                    return false;
                }

                if (response.Type != C64NetMessageType.ServerWelcome)
                {
                    // Any other first response means this is not a compatible C64Net host.
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
                    out bool keyboardEnabled,
                    out string welcomeStatus);

                ClientId = clientId;
                VideoWidth = width;
                VideoHeight = height;
                AudioSampleRate = audioSampleRate;
                Role = acceptedRole;
                Permission = permission;
                KeyboardEnabled = keyboardEnabled;
                StatusText = string.IsNullOrWhiteSpace(welcomeStatus) ? "CONNECTED" : welcomeStatus;
                _connected = true;

                try
                {
                    // Remote clients play host audio locally. If audio initialization fails,
                    // the session can still remain useful as a silent viewer/player.
                    _audioOutput = new SidAudioOutput(AudioSampleRate);
                }
                catch
                {
                    _audioOutput = null;
                }

                // Receiving is backgrounded so the UI/render thread can continue drawing
                // the last decoded frame and collecting joystick input.
                _shutdown = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoop(_shutdown.Token));
                status = StatusText;
                RaiseStatus(status);
                return true;
            }
            catch (C64NetTlsException ex)
            {
                Debug.WriteLine(ex);
                status = ex.Message;
                Disconnect();
                return false;
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
                // Signal the receive loop before closing the socket; either path may win.
                shutdown.Cancel();
            }

            try
            {
                if (_stream != null)
                {
                    // Best-effort graceful leave. Socket close below is still the real
                    // guarantee if the peer is gone or the write fails.
                    AddBytesSent(C64NetProtocol.WriteMessage(_stream, new C64NetMessage
                    {
                        Type = C64NetMessageType.Disconnect,
                        Timestamp = DateTime.UtcNow.Ticks,
                        Payload = C64NetProtocol.CreateTextPayload("LEAVE")
                    }));
                }
            }
            catch
            {
            }

            try
            {
                if (_stream != null)
                {
                    _stream.Dispose();
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
                    // Avoid deadlocking when Disconnect is called from inside ReceiveLoop
                    // after a server-initiated disconnect message.
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
            Volatile.Write(ref _latencyMilliseconds, -1);
            Interlocked.Exchange(ref _latencyPingPending, 0);
            Interlocked.Exchange(ref _lastLatencyPingTicks, 0);
            ClientId = 0;
            Permission = C64NetJoystickPermission.Observer;
            KeyboardEnabled = false;
            _serverCertificateFingerprint = "UNKNOWN";
            _videoReferencePalettePixels = null;
            _videoReferenceFrameId = 0;
            _videoDecodePixels = null;
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _bytesReceived, 0);
            StatusText = "DISCONNECTED";
            // Clear stale host-menu popup text as soon as the session is gone.
            RaiseHostOverlayStatus(string.Empty);
            RaiseStatus(StatusText);
        }

        /// <summary>
        /// Sends the current C64 active-low joystick state to the server.
        /// </summary>
        /// <param name="activeLowJoystickState">Active-low C64 joystick state.</param>
        /// <param name="force">True to send even when the state did not change.</param>
        public void SendJoystickState(byte activeLowJoystickState, bool force)
        {
            activeLowJoystickState = (byte)(activeLowJoystickState | 0xE0);
            if (!force && activeLowJoystickState == _lastSentJoystickState)
            {
                // Joystick polling happens every frame; suppress unchanged packets unless
                // the caller requests a periodic refresh.
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

        /// <summary>
        /// Sends one C64 keyboard key press or release to the host.
        /// </summary>
        /// <param name="key">Frontend key.</param>
        /// <param name="pressed">True for key down, false for key up.</param>
        public void SendKeyboardKey(Key key, bool pressed)
        {
            if (!KeyboardEnabled)
            {
                return;
            }

            SendMessage(new C64NetMessage
            {
                Type = C64NetMessageType.KeyboardInput,
                Timestamp = DateTime.UtcNow.Ticks,
                Payload = C64NetProtocol.CreateKeyboardInputPayload(key, pressed)
            });
        }

        /// <summary>
        /// Sends a periodic client-to-server latency probe when one is due.
        /// </summary>
        public void PollLatency()
        {
            if (!_connected || _stream == null)
            {
                return;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            long lastPingTicks = Interlocked.Read(ref _lastLatencyPingTicks);
            long elapsedTicks = nowTicks - lastPingTicks;
            bool pending = Interlocked.CompareExchange(ref _latencyPingPending, 0, 0) != 0;
            if (pending)
            {
                if (elapsedTicks < LatencyPingTimeoutTicks)
                {
                    return;
                }
            }
            else if (lastPingTicks != 0 && elapsedTicks < LatencyPingIntervalTicks)
            {
                return;
            }

            Interlocked.Exchange(ref _lastLatencyPingTicks, nowTicks);
            Interlocked.Exchange(ref _latencyPingPending, 1);
            SendMessage(new C64NetMessage
            {
                Type = C64NetMessageType.Ping,
                Timestamp = nowTicks,
                Payload = new byte[0]
            });
        }

        public void Dispose()
        {
            Disconnect();
        }

        /// <summary>
        /// Receives server-to-client messages until the session ends.
        /// </summary>
        /// <param name="cancellationToken">Client shutdown token.</param>
        private void ReceiveLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    C64NetMessage message = C64NetProtocol.ReadMessage(_stream);
                    if (message == null)
                    {
                        // Null means the host closed the TCP stream or the socket failed.
                        break;
                    }

                    AddBytesReceived(message.WireLength);
                    HandleMessage(message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                // The window notices IsConnected == false on its next update and returns
                // to local emulation cleanup.
                StatusText = "DISCONNECTED";
                _connected = false;
                RaiseHostOverlayStatus(string.Empty);
                RaiseStatus(StatusText);
            }
        }

        /// <summary>
        /// Dispatches one server-to-client protocol message.
        /// </summary>
        /// <param name="message">Decoded message from the host.</param>
        private void HandleMessage(C64NetMessage message)
        {
            switch (message.Type)
            {
                case C64NetMessageType.VideoFrame:
                    C64NetVideoFrame frame = C64NetProtocol.ReadVideoFramePayload(message.Payload, ref _videoReferencePalettePixels, ref _videoReferenceFrameId, _videoDecodePixels);
                    if (frame != null)
                    {
                        _videoDecodePixels = frame.Pixels;
                        // The window owns frame storage because it must synchronize with
                        // the render thread and apply the user's local video filter.
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
                            // Mismatched sample rates are ignored; changing the audio
                            // device mid-session would require a new handshake.
                            _audioOutput.Write(buffer, count);
                        }
                    }

                    break;
                case C64NetMessageType.ClientList:
                    List<C64NetClientSnapshot> clients = C64NetProtocol.ReadClientListPayload(message.Payload);
                    Action<List<C64NetClientSnapshot>> listHandler = ClientListReceived;
                    if (listHandler != null)
                    {
                        // Clients use this mainly for observer information and for keeping
                        // the network client list consistent with the host.
                        listHandler(clients);
                    }

                    break;
                case C64NetMessageType.PermissionUpdate:
                    // Permission is authoritative from the host. The local F10 joystick
                    // item is displayed read-only while connected.
                    C64NetProtocol.ReadPermissionPayload(message.Payload, out C64NetJoystickPermission permission, out bool keyboardEnabled);
                    Permission = permission;
                    KeyboardEnabled = keyboardEnabled;
                    StatusText = "PERMISSION " + FormatPermission(Permission) + " KEYBOARD " + (KeyboardEnabled ? "ON" : "OFF");
                    RaiseStatus(StatusText);
                    break;
                case C64NetMessageType.HostOverlayStatus:
                    // Host menu state is persistent, not a short toast, so keep the last
                    // text until the server clears it.
                    RaiseHostOverlayStatus(C64NetProtocol.ReadTextPayload(message.Payload));
                    break;
                case C64NetMessageType.Ping:
                    // Preserve the host timestamp to make the round-trip calculation exact
                    // on the host side.
                    SendMessage(new C64NetMessage
                    {
                        Type = C64NetMessageType.Pong,
                        Timestamp = message.Timestamp,
                        Payload = message.Payload
                    });
                    break;
                case C64NetMessageType.Pong:
                    UpdateLatency(message.Timestamp);
                    break;
                case C64NetMessageType.Disconnect:
                    StatusText = C64NetProtocol.ReadTextPayload(message.Payload);
                    RaiseStatus(StatusText);
                    // Disconnect performs the full cleanup and sends a best-effort leave;
                    // that is harmless even when the server initiated the close.
                    Disconnect();
                    break;
            }
        }

        /// <summary>
        /// Sends one client-to-server message in a thread-safe way.
        /// </summary>
        /// <param name="message">Message to send.</param>
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
                    // Input events and ping responses can originate from different
                    // threads. Serialize writes so protocol frames never interleave.
                    AddBytesSent(C64NetProtocol.WriteMessage(_stream, message));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                // Any write failure means the TCP stream is no longer usable.
                Disconnect();
            }
        }

        /// <summary>
        /// Updates the local latency estimate from a timestamp-preserving pong.
        /// </summary>
        /// <param name="timestamp">Original ping timestamp in UTC ticks.</param>
        private void UpdateLatency(long timestamp)
        {
            long elapsedTicks = Math.Max(0, DateTime.UtcNow.Ticks - timestamp);
            Volatile.Write(ref _latencyMilliseconds, (int)Math.Min(9999, elapsedTicks / TimeSpan.TicksPerMillisecond));
            Interlocked.Exchange(ref _latencyPingPending, 0);
        }

        /// <summary>
        /// Raises a short user-visible client status update.
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
        /// Raises the persistent host-overlay popup status.
        /// </summary>
        /// <param name="status">Popup text, or an empty string to clear it.</param>
        private void RaiseHostOverlayStatus(string status)
        {
            Action<string> handler = HostOverlayStatusChanged;
            if (handler != null)
            {
                handler(status);
            }
        }

        /// <summary>
        /// Adds bytes written by this client transport.
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
        /// Adds bytes read by this client transport.
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
        /// Formats a joystick permission for the remote title bar and status text.
        /// </summary>
        /// <param name="permission">Permission value from the host.</param>
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
    }
}
