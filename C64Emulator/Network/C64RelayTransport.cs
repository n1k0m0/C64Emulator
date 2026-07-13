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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace C64Emulator.Network
{
    /// <summary>
    /// Selects the transport used below the normal C64Net protocol.
    /// </summary>
    public enum C64NetTransportMode
    {
        /// <summary>
        /// Direct LAN mode: the host listens locally and clients connect to it.
        /// </summary>
        Lan = 0,
        /// <summary>
        /// Relay mode: host and clients both connect outbound to a public relay server.
        /// </summary>
        Relay = 1
    }

    /// <summary>
    /// Provides server-side accepted relay channels as authenticated, end-to-end encrypted streams.
    /// </summary>
    public sealed class C64RelayServerListener : IDisposable
    {
        private const int RelayHandshakeTimeoutMilliseconds = 8000;
        private readonly BlockingCollection<C64RelayAcceptedClient> _acceptedClients = new BlockingCollection<C64RelayAcceptedClient>();
        private readonly List<Task> _handshakeTasks = new List<Task>();
        private readonly object _syncRoot = new object();
        private bool _disposed;
        private C64RelayConnection _connection;
        private CancellationTokenSource _shutdown;

        /// <summary>
        /// Gets the relay host used by this listener.
        /// </summary>
        public string Host { get; private set; }

        /// <summary>
        /// Gets the relay TCP/TLS port used by this listener.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Gets the session connection id registered with the relay.
        /// </summary>
        public string ConnectionId { get; private set; }

        /// <summary>
        /// Gets the pinned TLS fingerprint of the relay server.
        /// </summary>
        public string RelayFingerprint { get; private set; } = "UNKNOWN";

        /// <summary>
        /// Gets whether the relay server connection is active.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                C64RelayConnection connection = _connection;
                return connection != null && connection.IsConnected;
            }
        }

        /// <summary>
        /// Opens the outbound server registration to the relay.
        /// </summary>
        /// <param name="host">Relay host or IP address.</param>
        /// <param name="port">Relay TLS port.</param>
        /// <param name="connectionId">Session identifier used to match clients.</param>
        /// <param name="relayPassword">Optional relay access password.</param>
        /// <param name="status">Short user-facing connection status.</param>
        /// <returns>True when registration succeeded.</returns>
        public bool Start(string host, int port, string connectionId, string relayPassword, out string status)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(C64RelayServerListener));
            }

            Stop();
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            Port = port;
            ConnectionId = NormalizeConnectionId(connectionId);
            _shutdown = new CancellationTokenSource();
            _connection = new C64RelayConnection(true);
            _connection.ChannelOpened += HandleChannelOpened;
            if (!_connection.Connect(Host, Port, C64RelayRole.Server, ConnectionId, relayPassword, out status))
            {
                Stop();
                return false;
            }

            RelayFingerprint = _connection.RelayFingerprint;
            status = "RELAY SERVER REGISTERED";
            return true;
        }

        /// <summary>
        /// Waits for the next relay client channel accepted by the public relay.
        /// </summary>
        /// <param name="cancellationToken">Server shutdown token.</param>
        /// <returns>Accepted encrypted client stream, or null when stopped.</returns>
        public C64RelayAcceptedClient AcceptClient(CancellationToken cancellationToken)
        {
            try
            {
                return _acceptedClients.Take(cancellationToken);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stops the relay server registration and all pending channels.
        /// </summary>
        public void Stop()
        {
            if (_disposed)
            {
                return;
            }

            CancellationTokenSource shutdown = _shutdown;
            _shutdown = null;
            if (shutdown != null)
            {
                shutdown.Cancel();
            }

            C64RelayConnection connection = _connection;
            _connection = null;
            if (connection != null)
            {
                connection.ChannelOpened -= HandleChannelOpened;
                connection.Dispose();
            }

            while (_acceptedClients.TryTake(out C64RelayAcceptedClient accepted))
            {
                accepted.Dispose();
            }

            lock (_syncRoot)
            {
                for (int index = 0; index < _handshakeTasks.Count; index++)
                {
                    try
                    {
                        _handshakeTasks[index].Wait(25);
                    }
                    catch
                    {
                    }
                }

                _handshakeTasks.Clear();
            }

            if (shutdown != null)
            {
                shutdown.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
            _acceptedClients.Dispose();
        }

        private void HandleChannelOpened(C64RelayRawStream rawStream)
        {
            CancellationToken token = _shutdown != null ? _shutdown.Token : CancellationToken.None;
            Task task = Task.Run(() =>
            {
                try
                {
                    string fingerprint;
                    rawStream.ReadTimeout = RelayHandshakeTimeoutMilliseconds;
                    rawStream.WriteTimeout = RelayHandshakeTimeoutMilliseconds;
                    Stream encryptedStream = C64RelayE2EStream.CreateServer(rawStream, ConnectionId, out fingerprint);
                    if (encryptedStream.CanTimeout)
                    {
                        encryptedStream.ReadTimeout = RelayHandshakeTimeoutMilliseconds;
                        encryptedStream.WriteTimeout = RelayHandshakeTimeoutMilliseconds;
                    }

                    var accepted = new C64RelayAcceptedClient(
                        encryptedStream,
                        "relay",
                        "relay:" + ConnectionId + "#" + rawStream.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        fingerprint);
                    if (!_acceptedClients.IsAddingCompleted && !token.IsCancellationRequested)
                    {
                        _acceptedClients.Add(accepted, token);
                    }
                    else
                    {
                        accepted.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    rawStream.Dispose();
                }
            }, token);

            lock (_syncRoot)
            {
                _handshakeTasks.Add(task);
            }
        }

        private static string NormalizeConnectionId(string connectionId)
        {
            return string.IsNullOrWhiteSpace(connectionId) ? "default" : connectionId.Trim();
        }

        private static bool IsTimeoutException(Exception exception)
        {
            while (exception != null)
            {
                if (exception is TimeoutException)
                {
                    return true;
                }

                if (exception.Message != null &&
                    exception.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                exception = exception.InnerException;
            }

            return false;
        }
    }

    /// <summary>
    /// Represents a relay-accepted client stream ready for the normal C64Net handshake.
    /// </summary>
    public sealed class C64RelayAcceptedClient : IDisposable
    {
        public C64RelayAcceptedClient(Stream stream, string remoteAddress, string remoteEndpoint, string sessionFingerprint)
        {
            Stream = stream;
            RemoteAddress = remoteAddress;
            RemoteEndpoint = remoteEndpoint;
            SessionFingerprint = sessionFingerprint;
        }

        public Stream Stream { get; private set; }

        public string RemoteAddress { get; private set; }

        public string RemoteEndpoint { get; private set; }

        public string SessionFingerprint { get; private set; }

        public void Dispose()
        {
            if (Stream != null)
            {
                Stream.Dispose();
                Stream = null;
            }
        }
    }

    /// <summary>
    /// Creates a client-side relay stream and performs the end-to-end crypto handshake.
    /// </summary>
    public static class C64RelayClientConnector
    {
        private const int RelayHandshakeTimeoutMilliseconds = 8000;

        /// <summary>
        /// Connects to a relay server and returns a C64Net-compatible encrypted stream.
        /// </summary>
        /// <param name="host">Relay host or IP.</param>
        /// <param name="port">Relay TLS port.</param>
        /// <param name="connectionId">Session id to join.</param>
        /// <param name="relayPassword">Optional relay access password.</param>
        /// <param name="relayFingerprint">Pinned relay TLS fingerprint.</param>
        /// <param name="sessionFingerprint">End-to-end server session fingerprint.</param>
        /// <param name="status">Short status text.</param>
        /// <returns>Encrypted stream, or null when connect/register failed.</returns>
        public static Stream Connect(
            string host,
            int port,
            string connectionId,
            string relayPassword,
            out string relayFingerprint,
            out string sessionFingerprint,
            out string status)
        {
            relayFingerprint = "UNKNOWN";
            sessionFingerprint = "UNKNOWN";
            var connection = new C64RelayConnection(false);
            if (!connection.Connect(host, port, C64RelayRole.Client, NormalizeConnectionId(connectionId), relayPassword, out status))
            {
                connection.Dispose();
                return null;
            }

            try
            {
                relayFingerprint = connection.RelayFingerprint;
                C64RelayRawStream rawStream = connection.GetClientChannel();
                rawStream.ReadTimeout = RelayHandshakeTimeoutMilliseconds;
                rawStream.WriteTimeout = RelayHandshakeTimeoutMilliseconds;
                Stream encryptedStream = C64RelayE2EStream.CreateClient(rawStream, NormalizeConnectionId(connectionId), out sessionFingerprint);
                if (encryptedStream.CanTimeout)
                {
                    encryptedStream.ReadTimeout = RelayHandshakeTimeoutMilliseconds;
                    encryptedStream.WriteTimeout = RelayHandshakeTimeoutMilliseconds;
                }

                status = "RELAY CONNECTED";
                return new C64RelayOwnedStream(encryptedStream, connection);
            }
            catch (IOException ex)
            {
                Debug.WriteLine(ex);
                connection.Dispose();
                status = IsTimeoutException(ex) ? "RELAY HOST TIMEOUT" : "RELAY E2E FAILED";
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                connection.Dispose();
                status = "RELAY E2E FAILED";
                return null;
            }
        }

        private static string NormalizeConnectionId(string connectionId)
        {
            return string.IsNullOrWhiteSpace(connectionId) ? "default" : connectionId.Trim();
        }

        private static bool IsTimeoutException(Exception exception)
        {
            while (exception != null)
            {
                if (exception is TimeoutException)
                {
                    return true;
                }

                if (exception.Message != null &&
                    exception.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (exception is SocketException socketException &&
                    socketException.SocketErrorCode == SocketError.TimedOut)
                {
                    return true;
                }

                exception = exception.InnerException;
            }

            return false;
        }
    }

    internal enum C64RelayRole : byte
    {
        Server = 1,
        Client = 2
    }

    internal enum C64RelayFrameType : byte
    {
        Register = 1,
        RegisterOk = 2,
        RegisterReject = 3,
        ChannelOpen = 4,
        ChannelData = 5,
        ChannelClose = 6,
        Ping = 7,
        Pong = 8
    }

    internal sealed class C64RelayConnection : IDisposable
    {
        private const int TcpConnectTimeoutMilliseconds = 3000;
        private const int RelayControlHandshakeTimeoutMilliseconds = 5000;
        private const int ProtocolVersion = 1;
        private const int HeaderLength = 16;
        private const int Magic = 0x52343643;
        private const int MaxPayloadLength = 16 * 1024 * 1024;
        private readonly object _sendLock = new object();
        private readonly ConcurrentDictionary<int, C64RelayRawStream> _channels = new ConcurrentDictionary<int, C64RelayRawStream>();
        private readonly bool _serverSide;
        private TcpClient _tcpClient;
        private Stream _stream;
        private Task _receiveTask;
        private CancellationTokenSource _shutdown;
        private int _clientChannelId;

        public C64RelayConnection(bool serverSide)
        {
            _serverSide = serverSide;
        }

        public event Action<C64RelayRawStream> ChannelOpened;

        public string RelayFingerprint { get; private set; } = "UNKNOWN";

        public bool IsConnected
        {
            get { return _tcpClient != null && _tcpClient.Connected && _stream != null; }
        }

        public bool Connect(string host, int port, C64RelayRole role, string connectionId, string relayPassword, out string status)
        {
            status = string.Empty;
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                ApplySocketTimeouts(_tcpClient, RelayControlHandshakeTimeoutMilliseconds);
                Task connectTask = _tcpClient.ConnectAsync(host, port);
                if (!connectTask.Wait(TcpConnectTimeoutMilliseconds))
                {
                    status = "RELAY TIMEOUT";
                    return false;
                }

                string tlsStatus;
                _stream = C64NetTls.AuthenticateClient(_tcpClient, host, port, out tlsStatus);
                ApplyStreamTimeouts(_stream, RelayControlHandshakeTimeoutMilliseconds);
                RelayFingerprint = C64NetTls.GetTrustedServerShortFingerprint(host, port);
                SendFrame(C64RelayFrameType.Register, 0, CreateRegisterPayload(role, connectionId, relayPassword));
                C64RelayFrame response = ReadFrame(_stream);
                if (response == null)
                {
                    status = "NO RELAY RESPONSE";
                    return false;
                }

                if (response.Type == C64RelayFrameType.RegisterReject)
                {
                    status = ReadTextPayload(response.Payload);
                    return false;
                }

                if (response.Type != C64RelayFrameType.RegisterOk)
                {
                    status = "BAD RELAY RESPONSE";
                    return false;
                }

                _clientChannelId = ReadRegisterOkPayload(response.Payload, out string relayStatus);
                if (!_serverSide && _clientChannelId <= 0)
                {
                    status = "BAD RELAY CHANNEL";
                    return false;
                }

                if (!_serverSide)
                {
                    _channels[_clientChannelId] = new C64RelayRawStream(this, _clientChannelId);
                }

                _shutdown = new CancellationTokenSource();
                ApplyStreamTimeouts(_stream, Timeout.Infinite);
                ApplySocketTimeouts(_tcpClient, Timeout.Infinite);
                _receiveTask = Task.Run(() => ReceiveLoop(_shutdown.Token));
                status = string.IsNullOrWhiteSpace(relayStatus) ? "RELAY CONNECTED" : relayStatus;
                return true;
            }
            catch (C64NetTlsException ex)
            {
                Debug.WriteLine(ex);
                if (ex.IsCertificateChanged)
                {
                    Dispose();
                    throw;
                }

                status = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                status = IsTimeoutException(ex) ? "RELAY TIMEOUT" : "RELAY CONNECT FAILED";
                return false;
            }
        }

        public C64RelayRawStream GetClientChannel()
        {
            if (_clientChannelId <= 0)
            {
                throw new InvalidOperationException("Relay client has no channel.");
            }

            return _channels[_clientChannelId];
        }

        public void SendChannelData(int channelId, byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            byte[] payload = new byte[count];
            Buffer.BlockCopy(buffer, offset, payload, 0, count);
            SendFrame(C64RelayFrameType.ChannelData, channelId, payload);
        }

        public void CloseChannel(int channelId)
        {
            SendFrame(C64RelayFrameType.ChannelClose, channelId, Array.Empty<byte>());
            if (_channels.TryRemove(channelId, out C64RelayRawStream stream))
            {
                stream.MarkRemoteClosed();
            }
        }

        public void Dispose()
        {
            CancellationTokenSource shutdown = _shutdown;
            _shutdown = null;
            if (shutdown != null)
            {
                shutdown.Cancel();
            }

            foreach (KeyValuePair<int, C64RelayRawStream> pair in _channels)
            {
                pair.Value.MarkRemoteClosed();
            }

            _channels.Clear();

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

            if (_receiveTask != null)
            {
                try
                {
                    _receiveTask.Wait(250);
                }
                catch
                {
                }
            }

            if (shutdown != null)
            {
                shutdown.Dispose();
            }
        }

        private void ReceiveLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    C64RelayFrame frame = ReadFrame(_stream);
                    if (frame == null)
                    {
                        break;
                    }

                    switch (frame.Type)
                    {
                        case C64RelayFrameType.ChannelOpen:
                            HandleChannelOpen(frame.ChannelId);
                            break;
                        case C64RelayFrameType.ChannelData:
                            if (_channels.TryGetValue(frame.ChannelId, out C64RelayRawStream stream))
                            {
                                stream.EnqueueData(frame.Payload);
                            }

                            break;
                        case C64RelayFrameType.ChannelClose:
                            if (_channels.TryRemove(frame.ChannelId, out C64RelayRawStream closed))
                            {
                                closed.MarkRemoteClosed();
                            }

                            break;
                        case C64RelayFrameType.Ping:
                            SendFrame(C64RelayFrameType.Pong, frame.ChannelId, frame.Payload);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                foreach (KeyValuePair<int, C64RelayRawStream> pair in _channels)
                {
                    pair.Value.MarkRemoteClosed();
                }
            }
        }

        private void HandleChannelOpen(int channelId)
        {
            if (channelId <= 0)
            {
                return;
            }

            var stream = new C64RelayRawStream(this, channelId);
            if (_channels.TryAdd(channelId, stream))
            {
                Action<C64RelayRawStream> handler = ChannelOpened;
                if (handler != null)
                {
                    handler(stream);
                }
            }
        }

        private void SendFrame(C64RelayFrameType type, int channelId, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            if (payload.Length > MaxPayloadLength)
            {
                throw new InvalidDataException("Relay payload is too large.");
            }

            byte[] header = new byte[HeaderLength];
            WriteInt32(header, 0, Magic);
            header[4] = ProtocolVersion;
            header[5] = (byte)type;
            WriteUInt16(header, 6, 0);
            WriteInt32(header, 8, channelId);
            WriteInt32(header, 12, payload.Length);

            lock (_sendLock)
            {
                _stream.Write(header, 0, header.Length);
                if (payload.Length > 0)
                {
                    _stream.Write(payload, 0, payload.Length);
                }

                _stream.Flush();
            }
        }

        private static C64RelayFrame ReadFrame(Stream stream)
        {
            byte[] header = new byte[HeaderLength];
            if (!ReadExact(stream, header, 0, header.Length))
            {
                return null;
            }

            if (ReadInt32(header, 0) != Magic || header[4] != ProtocolVersion)
            {
                throw new InvalidDataException("Invalid relay frame header.");
            }

            int channelId = ReadInt32(header, 8);
            int payloadLength = ReadInt32(header, 12);
            if (payloadLength < 0 || payloadLength > MaxPayloadLength)
            {
                throw new InvalidDataException("Invalid relay payload length.");
            }

            byte[] payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
            if (payloadLength > 0 && !ReadExact(stream, payload, 0, payload.Length))
            {
                return null;
            }

            return new C64RelayFrame
            {
                Type = (C64RelayFrameType)header[5],
                ChannelId = channelId,
                Payload = payload
            };
        }

        private static byte[] CreateRegisterPayload(C64RelayRole role, string connectionId, string relayPassword)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(ProtocolVersion);
                writer.Write((byte)role);
                WriteString(writer, connectionId);
                WriteString(writer, relayPassword);
                return stream.ToArray();
            }
        }

        private static int ReadRegisterOkPayload(byte[] payload, out string status)
        {
            using (var stream = new MemoryStream(payload ?? Array.Empty<byte>()))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                int channelId = reader.ReadInt32();
                status = ReadString(reader);
                return channelId;
            }
        }

        private static string ReadTextPayload(byte[] payload)
        {
            using (var stream = new MemoryStream(payload ?? Array.Empty<byte>()))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                return ReadString(reader);
            }
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
                count -= read;
            }

            return true;
        }

        private static void ApplySocketTimeouts(TcpClient tcpClient, int timeoutMilliseconds)
        {
            if (tcpClient == null)
            {
                return;
            }

            int socketTimeout = timeoutMilliseconds == Timeout.Infinite ? 0 : timeoutMilliseconds;
            tcpClient.ReceiveTimeout = socketTimeout;
            tcpClient.SendTimeout = socketTimeout;
        }

        private static void ApplyStreamTimeouts(Stream stream, int timeoutMilliseconds)
        {
            if (stream == null || !stream.CanTimeout)
            {
                return;
            }

            stream.ReadTimeout = timeoutMilliseconds;
            stream.WriteTimeout = timeoutMilliseconds;
        }

        private static bool IsTimeoutException(Exception exception)
        {
            while (exception != null)
            {
                if (exception is TimeoutException)
                {
                    return true;
                }

                if (exception.Message != null &&
                    exception.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (exception is SocketException socketException &&
                    socketException.SocketErrorCode == SocketError.TimedOut)
                {
                    return true;
                }

                exception = exception.InnerException;
            }

            return false;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 65536)
            {
                throw new InvalidDataException("Invalid relay string length.");
            }

            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            unchecked
            {
                uint unsigned = (uint)value;
                buffer[offset] = (byte)(unsigned & 0xFF);
                buffer[offset + 1] = (byte)((unsigned >> 8) & 0xFF);
                buffer[offset + 2] = (byte)((unsigned >> 16) & 0xFF);
                buffer[offset + 3] = (byte)((unsigned >> 24) & 0xFF);
            }
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            unchecked
            {
                return (int)(
                    (uint)buffer[offset] |
                    ((uint)buffer[offset + 1] << 8) |
                    ((uint)buffer[offset + 2] << 16) |
                    ((uint)buffer[offset + 3] << 24));
            }
        }
    }

    internal sealed class C64RelayFrame
    {
        public C64RelayFrameType Type { get; set; }

        public int ChannelId { get; set; }

        public byte[] Payload { get; set; }
    }

    internal sealed class C64RelayRawStream : Stream
    {
        private readonly C64RelayConnection _connection;
        private readonly object _syncRoot = new object();
        private readonly Queue<byte[]> _incoming = new Queue<byte[]>();
        private byte[] _currentReadBuffer;
        private int _currentReadOffset;
        private int _readTimeout = Timeout.Infinite;
        private int _writeTimeout = Timeout.Infinite;
        private bool _closed;
        private bool _disposed;

        public C64RelayRawStream(C64RelayConnection connection, int channelId)
        {
            _connection = connection;
            ChannelId = channelId;
        }

        public int ChannelId { get; private set; }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override bool CanTimeout
        {
            get { return true; }
        }

        public override int ReadTimeout
        {
            get { return _readTimeout; }
            set { _readTimeout = ValidateTimeout(value, nameof(value)); }
        }

        public override int WriteTimeout
        {
            get { return _writeTimeout; }
            set { _writeTimeout = ValidateTimeout(value, nameof(value)); }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (count == 0)
            {
                return 0;
            }

            lock (_syncRoot)
            {
                while (!_closed && !HasBufferedData())
                {
                    WaitForBufferedDataOrTimeout();
                }

                if (!HasBufferedData())
                {
                    return 0;
                }

                int copied = 0;
                while (count > 0 && HasBufferedData())
                {
                    if (_currentReadBuffer == null || _currentReadOffset >= _currentReadBuffer.Length)
                    {
                        _currentReadBuffer = _incoming.Dequeue();
                        _currentReadOffset = 0;
                    }

                    int available = _currentReadBuffer.Length - _currentReadOffset;
                    int chunk = Math.Min(available, count);
                    Buffer.BlockCopy(_currentReadBuffer, _currentReadOffset, buffer, offset, chunk);
                    _currentReadOffset += chunk;
                    offset += chunk;
                    count -= chunk;
                    copied += chunk;
                    if (_currentReadOffset >= _currentReadBuffer.Length)
                    {
                        _currentReadBuffer = null;
                        _currentReadOffset = 0;
                    }
                }

                return copied;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(C64RelayRawStream));
            }

            _connection.SendChannelData(ChannelId, buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        internal void EnqueueData(byte[] data)
        {
            if (data == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_closed)
                {
                    return;
                }

                _incoming.Enqueue(data);
                Monitor.PulseAll(_syncRoot);
            }
        }

        internal void MarkRemoteClosed()
        {
            lock (_syncRoot)
            {
                _closed = true;
                Monitor.PulseAll(_syncRoot);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing)
            {
                try
                {
                    _connection.CloseChannel(ChannelId);
                }
                catch
                {
                }

                MarkRemoteClosed();
            }

            base.Dispose(disposing);
        }

        private bool HasBufferedData()
        {
            return (_currentReadBuffer != null && _currentReadOffset < _currentReadBuffer.Length) || _incoming.Count > 0;
        }

        private void WaitForBufferedDataOrTimeout()
        {
            int timeout = _readTimeout;
            if (timeout == Timeout.Infinite)
            {
                Monitor.Wait(_syncRoot);
                return;
            }

            long deadlineTicks = DateTime.UtcNow.AddMilliseconds(timeout).Ticks;
            while (!_closed && !HasBufferedData())
            {
                long remainingTicks = deadlineTicks - DateTime.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    throw new IOException("Relay channel read timed out.");
                }

                int remainingMilliseconds = (int)Math.Min(
                    int.MaxValue,
                    Math.Max(1, remainingTicks / TimeSpan.TicksPerMillisecond));
                Monitor.Wait(_syncRoot, remainingMilliseconds);
            }
        }

        private static int ValidateTimeout(int timeout, string parameterName)
        {
            if (timeout < Timeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return timeout;
        }
    }

    internal sealed class C64RelayOwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly C64RelayConnection _owner;

        public C64RelayOwnedStream(Stream inner, C64RelayConnection owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead
        {
            get { return _inner.CanRead; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return _inner.CanWrite; }
        }

        public override bool CanTimeout
        {
            get { return _inner.CanTimeout; }
        }

        public override int ReadTimeout
        {
            get { return _inner.ReadTimeout; }
            set { _inner.ReadTimeout = value; }
        }

        public override int WriteTimeout
        {
            get { return _inner.WriteTimeout; }
            set { _inner.WriteTimeout = value; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class C64RelayE2EStream : Stream
    {
        private const int RecordHeaderLength = 4;
        private const int NonceLength = 12;
        private const int TagLength = 16;
        private const int MaxPlaintextLength = 16 * 1024 * 1024;
        private static readonly byte[] HandshakeMagic = Encoding.ASCII.GetBytes("C64E2E1");
        private readonly Stream _inner;
        private readonly byte[] _key;
        private readonly bool _serverSide;
        private readonly object _writeLock = new object();
        private byte[] _decryptedBuffer;
        private int _decryptedOffset;
        private ulong _sendCounter;
        private ulong _receiveCounter;

        private C64RelayE2EStream(Stream inner, byte[] key, bool serverSide)
        {
            _inner = inner;
            _key = key;
            _serverSide = serverSide;
        }

        public static Stream CreateServer(Stream inner, string connectionId, out string sessionFingerprint)
        {
            using (ECDiffieHellman local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] serverPublicKey = local.ExportSubjectPublicKeyInfo();
                WriteHandshake(inner, 1, serverPublicKey);
                byte[] clientPublicKey = ReadHandshake(inner, 2);
                byte[] key = DeriveKey(local, clientPublicKey, connectionId);
                sessionFingerprint = FormatShortFingerprint(Sha256Hex(serverPublicKey));
                return new C64RelayE2EStream(inner, key, true);
            }
        }

        public static Stream CreateClient(Stream inner, string connectionId, out string sessionFingerprint)
        {
            byte[] serverPublicKey = ReadHandshake(inner, 1);
            using (ECDiffieHellman local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] clientPublicKey = local.ExportSubjectPublicKeyInfo();
                WriteHandshake(inner, 2, clientPublicKey);
                byte[] key = DeriveKey(local, serverPublicKey, connectionId);
                sessionFingerprint = FormatShortFingerprint(Sha256Hex(serverPublicKey));
                return new C64RelayE2EStream(inner, key, false);
            }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override bool CanTimeout
        {
            get { return _inner.CanTimeout; }
        }

        public override int ReadTimeout
        {
            get { return _inner.ReadTimeout; }
            set { _inner.ReadTimeout = value; }
        }

        public override int WriteTimeout
        {
            get { return _inner.WriteTimeout; }
            set { _inner.WriteTimeout = value; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (count == 0)
            {
                return 0;
            }

            if (_decryptedBuffer == null || _decryptedOffset >= _decryptedBuffer.Length)
            {
                if (!ReadEncryptedRecord())
                {
                    return 0;
                }
            }

            int available = _decryptedBuffer.Length - _decryptedOffset;
            int copy = Math.Min(available, count);
            Buffer.BlockCopy(_decryptedBuffer, _decryptedOffset, buffer, offset, copy);
            _decryptedOffset += copy;
            if (_decryptedOffset >= _decryptedBuffer.Length)
            {
                _decryptedBuffer = null;
                _decryptedOffset = 0;
            }

            return copy;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (count > MaxPlaintextLength)
            {
                throw new InvalidDataException("Relay E2E record is too large.");
            }

            byte[] plaintext = new byte[count];
            Buffer.BlockCopy(buffer, offset, plaintext, 0, count);
            byte[] nonce;
            byte[] ciphertext = new byte[count];
            byte[] tag = new byte[TagLength];
            lock (_writeLock)
            {
                nonce = CreateNonce(GetSendDirection(), _sendCounter++);
                using (var aes = new AesGcm(_key, TagLength))
                {
                    aes.Encrypt(nonce, plaintext, ciphertext, tag);
                }

                int bodyLength = NonceLength + TagLength + ciphertext.Length;
                byte[] header = new byte[RecordHeaderLength];
                WriteInt32(header, 0, bodyLength);
                _inner.Write(header, 0, header.Length);
                _inner.Write(nonce, 0, nonce.Length);
                _inner.Write(tag, 0, tag.Length);
                _inner.Write(ciphertext, 0, ciphertext.Length);
                _inner.Flush();
            }
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                Array.Clear(_key);
            }

            base.Dispose(disposing);
        }

        private bool ReadEncryptedRecord()
        {
            byte[] header = new byte[RecordHeaderLength];
            if (!ReadExact(_inner, header, 0, header.Length))
            {
                return false;
            }

            int bodyLength = ReadInt32(header, 0);
            if (bodyLength < NonceLength + TagLength || bodyLength > MaxPlaintextLength + NonceLength + TagLength)
            {
                throw new InvalidDataException("Invalid Relay E2E record length.");
            }

            byte[] body = new byte[bodyLength];
            if (!ReadExact(_inner, body, 0, body.Length))
            {
                return false;
            }

            byte[] nonce = new byte[NonceLength];
            byte[] tag = new byte[TagLength];
            int ciphertextLength = bodyLength - NonceLength - TagLength;
            byte[] ciphertext = new byte[ciphertextLength];
            Buffer.BlockCopy(body, 0, nonce, 0, NonceLength);
            Buffer.BlockCopy(body, NonceLength, tag, 0, TagLength);
            Buffer.BlockCopy(body, NonceLength + TagLength, ciphertext, 0, ciphertextLength);

            byte[] expectedNonce = CreateNonce(GetReceiveDirection(), _receiveCounter++);
            if (!FixedTimeEquals(nonce, expectedNonce))
            {
                throw new InvalidDataException("Relay E2E nonce sequence mismatch.");
            }

            byte[] plaintext = new byte[ciphertextLength];
            using (var aes = new AesGcm(_key, TagLength))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            _decryptedBuffer = plaintext;
            _decryptedOffset = 0;
            return true;
        }

        private byte GetSendDirection()
        {
            return _serverSide ? (byte)0 : (byte)1;
        }

        private byte GetReceiveDirection()
        {
            return _serverSide ? (byte)1 : (byte)0;
        }

        private static byte[] DeriveKey(ECDiffieHellman local, byte[] peerPublicKey, string connectionId)
        {
            using (ECDiffieHellman peer = ECDiffieHellman.Create())
            {
                peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
                byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("C64Relay:" + (connectionId ?? string.Empty)));
                byte[] info = Encoding.ASCII.GetBytes("C64Emulator Relay E2E v1");
                return local.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, salt, info);
            }
        }

        private static void WriteHandshake(Stream stream, byte type, byte[] publicKey)
        {
            byte[] header = new byte[HandshakeMagic.Length + 1 + 4];
            Buffer.BlockCopy(HandshakeMagic, 0, header, 0, HandshakeMagic.Length);
            header[HandshakeMagic.Length] = type;
            WriteInt32(header, HandshakeMagic.Length + 1, publicKey.Length);
            stream.Write(header, 0, header.Length);
            stream.Write(publicKey, 0, publicKey.Length);
            stream.Flush();
        }

        private static byte[] ReadHandshake(Stream stream, byte expectedType)
        {
            byte[] header = new byte[HandshakeMagic.Length + 1 + 4];
            if (!ReadExact(stream, header, 0, header.Length))
            {
                throw new EndOfStreamException("Relay E2E handshake ended early.");
            }

            for (int index = 0; index < HandshakeMagic.Length; index++)
            {
                if (header[index] != HandshakeMagic[index])
                {
                    throw new InvalidDataException("Invalid Relay E2E handshake magic.");
                }
            }

            if (header[HandshakeMagic.Length] != expectedType)
            {
                throw new InvalidDataException("Invalid Relay E2E handshake type.");
            }

            int keyLength = ReadInt32(header, HandshakeMagic.Length + 1);
            if (keyLength <= 0 || keyLength > 8192)
            {
                throw new InvalidDataException("Invalid Relay E2E key length.");
            }

            byte[] publicKey = new byte[keyLength];
            if (!ReadExact(stream, publicKey, 0, publicKey.Length))
            {
                throw new EndOfStreamException("Relay E2E handshake key ended early.");
            }

            return publicKey;
        }

        private static byte[] CreateNonce(byte direction, ulong counter)
        {
            byte[] nonce = new byte[NonceLength];
            nonce[0] = direction;
            for (int index = 0; index < 8; index++)
            {
                nonce[4 + index] = (byte)((counter >> (index * 8)) & 0xFF);
            }

            return nonce;
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
                count -= read;
            }

            return true;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int index = 0; index < left.Length; index++)
            {
                diff |= left[index] ^ right[index];
            }

            return diff == 0;
        }

        private static string Sha256Hex(byte[] data)
        {
            byte[] hash = SHA256.HashData(data);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToUpperInvariant();
        }

        private static string FormatShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length < 12)
            {
                return "UNKNOWN";
            }

            return fingerprint.Substring(0, 4) + "-" + fingerprint.Substring(4, 4) + "-" + fingerprint.Substring(8, 4);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            unchecked
            {
                uint unsigned = (uint)value;
                buffer[offset] = (byte)(unsigned & 0xFF);
                buffer[offset + 1] = (byte)((unsigned >> 8) & 0xFF);
                buffer[offset + 2] = (byte)((unsigned >> 16) & 0xFF);
                buffer[offset + 3] = (byte)((unsigned >> 24) & 0xFF);
            }
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            unchecked
            {
                return (int)(
                    (uint)buffer[offset] |
                    ((uint)buffer[offset + 1] << 8) |
                    ((uint)buffer[offset + 2] << 16) |
                    ((uint)buffer[offset + 3] << 24));
            }
        }
    }
}
