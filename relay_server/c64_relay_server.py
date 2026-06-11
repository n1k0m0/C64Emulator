#!/usr/bin/env python3
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
import logging
import os
import ssl
import struct
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Optional


MAGIC = 0x52343643  # "C64R" in little-endian bytes
VERSION = 1
HEADER = struct.Struct("<IBBHIi")
MAX_PAYLOAD = 16 * 1024 * 1024

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
    frame_type: int
    channel_id: int
    payload: bytes = b""


@dataclass
class ClientPeer:
    channel_id: int
    writer: asyncio.StreamWriter
    endpoint: str


@dataclass
class RelaySession:
    connection_id: str
    server_writer: asyncio.StreamWriter
    server_endpoint: str
    clients: Dict[int, ClientPeer] = field(default_factory=dict)
    next_channel_id: int = 1


sessions: Dict[str, RelaySession] = {}
sessions_lock = asyncio.Lock()
logger = logging.getLogger("c64-relay-server")


def write_string(value: str) -> bytes:
    data = (value or "").encode("utf-8")
    return struct.pack("<i", len(data)) + data


def read_string(payload: bytes, offset: int) -> tuple[str, int]:
    if offset + 4 > len(payload):
        raise ValueError("missing string length")
    (length,) = struct.unpack_from("<i", payload, offset)
    offset += 4
    if length < 0 or length > 65536 or offset + length > len(payload):
        raise ValueError("invalid string length")
    value = payload[offset : offset + length].decode("utf-8", errors="strict")
    return value, offset + length


def parse_register(payload: bytes) -> tuple[int, int, str]:
    if len(payload) < 5:
        raise ValueError("short register payload")
    version, role = struct.unpack_from("<iB", payload, 0)
    connection_id, _ = read_string(payload, 5)
    return version, role, normalize_connection_id(connection_id)


def create_register_ok(channel_id: int, status: str) -> bytes:
    return struct.pack("<i", channel_id) + write_string(status)


def normalize_connection_id(connection_id: str) -> str:
    connection_id = (connection_id or "").strip().lower()
    return connection_id or "c64"


def peer_name(writer: asyncio.StreamWriter) -> str:
    peer = writer.get_extra_info("peername")
    if peer is None:
        return "unknown"
    if isinstance(peer, tuple) and len(peer) >= 2:
        return f"{peer[0]}:{peer[1]}"
    return str(peer)


def configure_logging(log_file: str) -> None:
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
    payload = payload or b""
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("relay payload too large")
    writer.write(HEADER.pack(MAGIC, VERSION, frame_type, 0, channel_id, len(payload)))
    if payload:
        writer.write(payload)
    await writer.drain()


async def close_writer(writer: asyncio.StreamWriter) -> None:
    try:
        writer.close()
        await writer.wait_closed()
    except Exception:
        pass


async def reject(writer: asyncio.StreamWriter, reason: str) -> None:
    try:
        await send_frame(writer, REGISTER_REJECT, 0, write_string(reason))
    finally:
        await close_writer(writer)


async def handle_connection(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    endpoint = peer_name(writer)
    try:
        frame = await read_frame(reader)
        if frame is None or frame.frame_type != REGISTER:
            await reject(writer, "BAD RELAY HELLO")
            return

        version, role, connection_id = parse_register(frame.payload)
        if version != VERSION:
            await reject(writer, "RELAY PROTOCOL MISMATCH")
            return

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
    async with sessions_lock:
        session = sessions.get(connection_id)
        if session is None:
            await reject(writer, "NO SERVER FOR ID")
            return
        channel_id = session.next_channel_id
        session.next_channel_id += 1
        client = ClientPeer(channel_id, writer, endpoint)
        session.clients[channel_id] = client

    logger.info("client joined id=%s channel=%s endpoint=%s", connection_id, channel_id, endpoint)
    try:
        await send_frame(session.server_writer, CHANNEL_OPEN, channel_id, write_string(endpoint))
        await send_frame(writer, REGISTER_OK, channel_id, create_register_ok(channel_id, "RELAY CLIENT READY"))
        while True:
            frame = await read_frame(reader)
            if frame is None:
                break
            if frame.frame_type == CHANNEL_DATA:
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
    parser = argparse.ArgumentParser(description="C64 Emulator TLS relay server")
    parser.add_argument("--host", default="0.0.0.0", help="listen address")
    parser.add_argument("--port", type=int, default=6465, help="TLS listen port")
    parser.add_argument("--cert", default="relay.crt", help="TLS certificate PEM path")
    parser.add_argument("--key", default="relay.key", help="TLS private key PEM path")
    parser.add_argument("--cn", default="C64RelayServer", help="self-signed certificate common name")
    parser.add_argument("--log-file", default="", help="optional relay log file path")
    args = parser.parse_args()
    configure_logging(args.log_file)

    cert_path = Path(args.cert)
    key_path = Path(args.key)
    ensure_self_signed_certificate(cert_path, key_path, args.cn)

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(certfile=os.fspath(cert_path), keyfile=os.fspath(key_path))

    server = await asyncio.start_server(handle_connection, args.host, args.port, ssl=context)
    logger.info("listening on %s:%s tls cert=%s", args.host, args.port, cert_path)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("stopped")
