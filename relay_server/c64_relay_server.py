#!/usr/bin/env python3
#
# Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
#
"""Small TLS relay server for C64 Emulator Relay Mode.

The relay is intentionally blind: it only knows connection ids, roles, channel
ids, and encrypted byte blobs. The C64 Emulator instances run their own
end-to-end encryption inside each relayed channel, so the relay cannot inspect
video, audio, keyboard, joystick, or session-password payloads.
"""

from __future__ import annotations

import argparse
import asyncio
import datetime as _datetime
import hmac
import logging
import os
import ssl
import struct
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Optional


# Fixed wire-protocol constants. They must stay in sync with
# C64Emulator/Network/C64RelayTransport.cs.
MAGIC = 0x52343643  # "C64R" in little-endian bytes
VERSION = 1
HEADER = struct.Struct("<IBBHIi")
MAX_PAYLOAD = 16 * 1024 * 1024

# Relay frame types. Register/RegisterOk are the relay handshake; channel frames
# are opaque transport frames after a client has been paired with a host.
REGISTER = 1
REGISTER_OK = 2
REGISTER_REJECT = 3
CHANNEL_OPEN = 4
CHANNEL_DATA = 5
CHANNEL_CLOSE = 6
PING = 7
PONG = 8

ROLE_SERVER = 1
ROLE_CLIENT = 2


@dataclass
class RelayFrame:
    """One decoded relay frame from the TLS transport."""

    frame_type: int
    channel_id: int
    payload: bytes = b""


@dataclass
class ClientPeer:
    """A connected relay client bound to one host-side channel id."""

    channel_id: int
    writer: asyncio.StreamWriter
    endpoint: str


@dataclass
class RelaySession:
    """A registered C64 host session and the clients currently attached to it."""

    connection_id: str
    server_writer: asyncio.StreamWriter
    server_endpoint: str
    clients: Dict[int, ClientPeer] = field(default_factory=dict)
    next_channel_id: int = 1


# Global in-memory session table. The relay is intentionally stateless; when the
# process restarts, hosts simply reconnect and register their connection ids again.
sessions: Dict[str, RelaySession] = {}
sessions_lock = asyncio.Lock()
logger = logging.getLogger("c64-relay-server")


def write_string(value: str) -> bytes:
    """Encode a protocol string as little-endian length plus UTF-8 bytes."""

    data = (value or "").encode("utf-8")
    return struct.pack("<i", len(data)) + data


def read_string(payload: bytes, offset: int) -> tuple[str, int]:
    """Decode a protocol string and return the new payload offset."""

    if offset + 4 > len(payload):
        raise ValueError("missing string length")
    (length,) = struct.unpack_from("<i", payload, offset)
    offset += 4
    if length < 0 or length > 65536 or offset + length > len(payload):
        raise ValueError("invalid string length")
    value = payload[offset : offset + length].decode("utf-8", errors="strict")
    return value, offset + length


def parse_register(payload: bytes) -> tuple[int, int, str, str]:
    """Parse the initial register frame sent by a host or client emulator."""

    if len(payload) < 5:
        raise ValueError("short register payload")
    version, role = struct.unpack_from("<iB", payload, 0)
    connection_id, offset = read_string(payload, 5)
    password = ""
    if offset < len(payload):
        password, offset = read_string(payload, offset)
    if offset != len(payload):
        raise ValueError("trailing register payload")
    return version, role, normalize_connection_id(connection_id), password


def create_register_ok(channel_id: int, status: str) -> bytes:
    """Build the register-accepted payload returned to hosts and clients."""

    return struct.pack("<i", channel_id) + write_string(status)


def normalize_connection_id(connection_id: str) -> str:
    """Normalize user-entered session ids so both sides meet reliably."""

    connection_id = (connection_id or "").strip().lower()
    return connection_id or "c64"


def peer_name(writer: asyncio.StreamWriter) -> str:
    """Return a stable human-readable peer endpoint for logs and host display."""

    peer = writer.get_extra_info("peername")
    if peer is None:
        return "unknown"
    if isinstance(peer, tuple) and len(peer) >= 2:
        return f"{peer[0]}:{peer[1]}"
    return str(peer)


def configure_logging(log_file: str) -> None:
    """Configure console logging and, optionally, a persistent log file."""

    handlers: list[logging.Handler] = [logging.StreamHandler()]
    if log_file:
        log_path = Path(log_file)
        log_path.parent.mkdir(parents=True, exist_ok=True)
        handlers.append(logging.FileHandler(log_path, encoding="utf-8"))

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        handlers=handlers,
        force=True,
    )


async def read_frame(reader: asyncio.StreamReader) -> Optional[RelayFrame]:
    """Read and validate one relay frame from the TLS stream.

    A clean EOF is returned as None so callers can distinguish normal disconnects
    from malformed data. Invalid headers or oversized payloads raise ValueError.
    """

    try:
        header = await reader.readexactly(HEADER.size)
    except asyncio.IncompleteReadError:
        return None

    magic, version, frame_type, _flags, channel_id, payload_length = HEADER.unpack(header)
    if magic != MAGIC or version != VERSION:
        raise ValueError("invalid relay frame header")
    if payload_length < 0 or payload_length > MAX_PAYLOAD:
        raise ValueError("invalid relay payload length")
    payload = b""
    if payload_length:
        payload = await reader.readexactly(payload_length)
    return RelayFrame(frame_type, channel_id, payload)


async def send_frame(writer: asyncio.StreamWriter, frame_type: int, channel_id: int, payload: bytes = b"") -> None:
    """Serialize and send one relay frame to a connected peer."""

    payload = payload or b""
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("relay payload too large")
    writer.write(HEADER.pack(MAGIC, VERSION, frame_type, 0, channel_id, len(payload)))
    if payload:
        writer.write(payload)
    await writer.drain()


async def close_writer(writer: asyncio.StreamWriter) -> None:
    """Close a stream writer while suppressing disconnect races."""

    try:
        writer.close()
        await writer.wait_closed()
    except Exception:
        pass


async def reject(writer: asyncio.StreamWriter, reason: str) -> None:
    """Send a register rejection and close the peer connection."""

    try:
        await send_frame(writer, REGISTER_REJECT, 0, write_string(reason))
    finally:
        await close_writer(writer)


async def handle_connection(reader: asyncio.StreamReader, writer: asyncio.StreamWriter, relay_password: str) -> None:
    """Handle a newly accepted TLS connection until it becomes host or client."""

    endpoint = peer_name(writer)
    try:
        frame = await read_frame(reader)
        if frame is None or frame.frame_type != REGISTER:
            await reject(writer, "BAD RELAY HELLO")
            return

        version, role, connection_id, password = parse_register(frame.payload)
        if version != VERSION:
            await reject(writer, "RELAY PROTOCOL MISMATCH")
            return
        if relay_password and not hmac.compare_digest(relay_password, password):
            logger.warning("bad relay password endpoint=%s role=%s id=%s", endpoint, role, connection_id)
            await reject(writer, "RELAY PASSWORD REJECTED")
            return

        # Only the first frame tells the relay whether this peer is the hosting
        # emulator or a joining client. After that, all C64Net data is opaque.
        if role == ROLE_SERVER:
            await handle_server(reader, writer, connection_id, endpoint)
        elif role == ROLE_CLIENT:
            await handle_client(reader, writer, connection_id, endpoint)
        else:
            await reject(writer, "BAD RELAY ROLE")
    except Exception:
        logger.exception("connection %s failed", endpoint)
        await close_writer(writer)


async def handle_server(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    connection_id: str,
    endpoint: str,
) -> None:
    """Register a host emulator and route channel frames to its clients."""

    # The connection id is the rendezvous key. Allowing two hosts to own the
    # same id would make client routing ambiguous, so the second host is rejected.
    async with sessions_lock:
        if connection_id in sessions:
            await reject(writer, "CONNECTION ID IN USE")
            return
        session = RelaySession(connection_id, writer, endpoint)
        sessions[connection_id] = session

    logger.info("server registered id=%s endpoint=%s", connection_id, endpoint)
    await send_frame(writer, REGISTER_OK, 0, create_register_ok(0, "RELAY SERVER READY"))

    try:
        while True:
            frame = await read_frame(reader)
            if frame is None:
                break
            if frame.frame_type == CHANNEL_DATA:
                # Host-to-client payloads are already encrypted by the emulator.
                # The relay only forwards them to the matching channel writer.
                client = session.clients.get(frame.channel_id)
                if client is not None:
                    await send_frame(client.writer, CHANNEL_DATA, frame.channel_id, frame.payload)
            elif frame.frame_type == CHANNEL_CLOSE:
                client = session.clients.pop(frame.channel_id, None)
                if client is not None:
                    await send_frame(client.writer, CHANNEL_CLOSE, frame.channel_id)
                    await close_writer(client.writer)
            elif frame.frame_type == PING:
                await send_frame(writer, PONG, frame.channel_id, frame.payload)
    finally:
        # When the host disconnects, every client belonging to the session must
        # be closed too; otherwise clients would wait forever for more frames.
        logger.info("server gone id=%s", connection_id)
        async with sessions_lock:
            if sessions.get(connection_id) is session:
                sessions.pop(connection_id, None)
        for client in list(session.clients.values()):
            try:
                await send_frame(client.writer, CHANNEL_CLOSE, client.channel_id)
            except Exception:
                pass
            await close_writer(client.writer)
        session.clients.clear()
        await close_writer(writer)


async def handle_client(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    connection_id: str,
    endpoint: str,
) -> None:
    """Attach a client emulator to an existing host session."""

    async with sessions_lock:
        session = sessions.get(connection_id)
        if session is None:
            await reject(writer, "NO SERVER FOR ID")
            return
        # Channel ids are host-local. They let one server connection multiplex
        # arbitrarily many client streams through the same relay registration.
        channel_id = session.next_channel_id
        session.next_channel_id += 1
        client = ClientPeer(channel_id, writer, endpoint)
        session.clients[channel_id] = client

    logger.info("client joined id=%s channel=%s endpoint=%s", connection_id, channel_id, endpoint)
    try:
        # Notify the host first so it can create its end of the encrypted C64Net
        # stream, then acknowledge the client-side relay registration.
        await send_frame(session.server_writer, CHANNEL_OPEN, channel_id, write_string(endpoint))
        await send_frame(writer, REGISTER_OK, channel_id, create_register_ok(channel_id, "RELAY CLIENT READY"))
        while True:
            frame = await read_frame(reader)
            if frame is None:
                break
            if frame.frame_type == CHANNEL_DATA:
                # Client-to-host C64Net data remains opaque to the relay.
                await send_frame(session.server_writer, CHANNEL_DATA, channel_id, frame.payload)
            elif frame.frame_type == CHANNEL_CLOSE:
                break
            elif frame.frame_type == PING:
                await send_frame(writer, PONG, channel_id, frame.payload)
    finally:
        logger.info("client left id=%s channel=%s", connection_id, channel_id)
        if session.clients.pop(channel_id, None) is not None:
            try:
                await send_frame(session.server_writer, CHANNEL_CLOSE, channel_id)
            except Exception:
                pass
        await close_writer(writer)


def ensure_self_signed_certificate(cert_path: Path, key_path: Path, common_name: str) -> None:
    """Ensure a TLS certificate/key pair exists for the relay listener.

    Production deployments may provide their own certificate files. For simple
    private relays, the script can create a self-signed certificate that the C64
    Emulator will pin on first use.
    """

    if cert_path.exists() and key_path.exists():
        return
    cert_path.parent.mkdir(parents=True, exist_ok=True)
    key_path.parent.mkdir(parents=True, exist_ok=True)
    if try_generate_with_cryptography(cert_path, key_path, common_name):
        return

    command = [
        "openssl",
        "req",
        "-x509",
        "-newkey",
        "rsa:2048",
        "-nodes",
        "-keyout",
        str(key_path),
        "-out",
        str(cert_path),
        "-days",
        "3650",
        "-subj",
        f"/CN={common_name}",
    ]
    try:
        subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception as exc:
        raise RuntimeError(
            "TLS certificate/key are missing and openssl could not create them. "
            f"Create {cert_path} and {key_path} manually or install openssl."
        ) from exc


def try_generate_with_cryptography(cert_path: Path, key_path: Path, common_name: str) -> bool:
    """Create a self-signed certificate with the optional cryptography package."""

    try:
        from cryptography import x509
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
        from cryptography.x509.oid import NameOID
    except Exception:
        return False

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subject = issuer = x509.Name(
        [
            x509.NameAttribute(NameOID.COMMON_NAME, common_name),
        ]
    )
    now = _datetime.datetime.now(_datetime.timezone.utc)
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(issuer)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - _datetime.timedelta(days=1))
        .not_valid_after(now + _datetime.timedelta(days=3650))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )

    key_path.write_bytes(
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.TraditionalOpenSSL,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return True


async def main() -> None:
    """Parse command-line options, prepare TLS, and run the asyncio server."""

    parser = argparse.ArgumentParser(description="C64 Emulator TLS relay server")
    parser.add_argument("--host", default="0.0.0.0", help="listen address")
    parser.add_argument("--port", type=int, default=6465, help="TLS listen port")
    parser.add_argument("--cert", default="relay.crt", help="TLS certificate PEM path")
    parser.add_argument("--key", default="relay.key", help="TLS private key PEM path")
    parser.add_argument("--cn", default="C64RelayServer", help="self-signed certificate common name")
    parser.add_argument("--log-file", default="", help="optional relay log file path")
    parser.add_argument("--password", default="", help="optional relay access password")
    args = parser.parse_args()
    configure_logging(args.log_file)

    cert_path = Path(args.cert)
    key_path = Path(args.key)
    ensure_self_signed_certificate(cert_path, key_path, args.cn)

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(certfile=os.fspath(cert_path), keyfile=os.fspath(key_path))

    relay_password = args.password or ""
    handler = lambda reader, writer: handle_connection(reader, writer, relay_password)
    server = await asyncio.start_server(handler, args.host, args.port, ssl=context)
    logger.info(
        "listening on %s:%s tls cert=%s relay_password=%s",
        args.host,
        args.port,
        cert_path,
        "enabled" if relay_password else "disabled",
    )
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("stopped")
