"""Independent PRG/D64 fixtures for the ROM game loader.

SPDX-License-Identifier: Apache-2.0

The byte fixtures below deliberately do not use the ROM builder or parser.
That keeps the loader tests independent of the implementation being tested.
They are original test programs, not redistributed commercial games.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Fixture:
    name: str
    data: bytes
    entry: int | None = None
    message: str | None = None
    description: str = ""

    @property
    def start(self) -> int:
        return int.from_bytes(self.data[:2], "little")

    @property
    def end(self) -> int:
        return self.start + len(self.data) - 2


def prg(address: int, payload: bytes) -> bytes:
    if not 0 <= address <= 0xFFFF:
        raise ValueError(address)
    return address.to_bytes(2, "little") + payload


def marker_program(entry: int, message: str | None = None, bank_out: bool = False) -> bytes:
    """Original native code writes $A5 to $CFF0, optionally prints, then RTS."""
    if message is None:
        result = b"\xA9\xA5\x8D\xF0\xCF"  # LDA #A5 / STA CFF0
        if bank_out:
            result += b"\x78\xA9\x34\x85\x01"  # SEI / all ROMs and I/O off
        return result + b"\x60"
    # LDX #0; LDA message,X; BEQ done; JSR CHROUT; INX; BNE loop.
    # done: LDA #A5; STA CFF0; RTS. CHROUT preserves X in this KERNAL.
    address = entry + 19
    return (bytes((0xA2, 0x00, 0xBD, address & 255, address >> 8,
                   0xF0, 0x06, 0x20, 0xD2, 0xFF, 0xE8, 0xD0, 0xF5,
                   0xA9, 0xA5, 0x8D, 0xF0, 0xCF, 0x60))
            + message.encode("ascii") + b"\r\0")


def sys_prg(entry: int = 0x0810, *, body: bytes | None = None,
            payload_length: int = 0x0500, message: str | None = "PLAY SYS OK") -> bytes:
    """A single linked/tokenized BASIC SYS line, end marker, then native code."""
    start = 0x0801
    body = b"\x9E" + str(entry).encode("ascii") if body is None else body
    next_line = start + 4 + len(body) + 1
    line = next_line.to_bytes(2, "little") + b"\x0A\x00" + body + b"\x00\x00\x00"
    code = marker_program(entry, message)
    if entry < start + len(line):
        raise ValueError("Code overlaps the BASIC end marker")
    size = max(payload_length, entry - start + len(code))
    payload = bytearray((index * 73 + 29) & 255 for index in range(size))
    payload[:len(line)] = line
    payload[entry - start:entry - start + len(code)] = code
    return prg(start, bytes(payload))


def native_prg(start: int, *, entry: int | None = None,
               length: int | None = None, message: str | None = None,
               bank_out: bool = False) -> bytes:
    entry = start if entry is None else entry
    code = marker_program(entry, message, bank_out)
    size = max(length or 0, entry - start + len(code))
    payload = bytearray((index * 37 + 0x5A) & 255 for index in range(size))
    payload[entry - start:entry - start + len(code)] = code
    return prg(start, bytes(payload))


def fixtures() -> list[Fixture]:
    basic = sys_prg(payload_length=0x1800)
    bad_link = bytearray(basic)
    bad_link[2:4] = b"\x08\x08"  # link before the terminating NUL
    expression = sys_prg(0x0820, body=b"\x9E2080+0", message="MUST NOT RUN")
    multiline = bytearray(sys_prg(0x0840, message="MUST NOT RUN"))
    next_line = int.from_bytes(multiline[2:4], "little") - 0x0801 + 2
    multiline[next_line:next_line + 9] = b"\x16\x08\x14\x00\x99\x22X\x22\0"
    return [
        Fixture("SYSDEMO", basic, 0x0810, "PLAY SYS OK",
                "SYS2064 at $0801, payload overwrites the shell and RAMFS workspaces"),
        Fixture("SYSSPACE", sys_prg(0x0820, body=b" \x9E 2080  ", message="PLAY SPACES OK"),
                0x0820, "PLAY SPACES OK", "Whitespace around decimal SYS target"),
        Fixture("DIRECT", native_prg(0x2000, message="PLAY DIRECT OK"),
                0x2000, "PLAY DIRECT OK", "Raw native code with original load address $2000"),
        Fixture("UNDERROM", native_prg(0xA000, message="PLAY ROM RAM OK"),
                0xA000, "PLAY ROM RAM OK", "Code in RAM underneath the BASIC ROM window"),
        Fixture("EXPLICIT", native_prg(0x0801, entry=0x0840, message="PLAY EXPLICIT OK"),
                0x0840, "PLAY EXPLICIT OK", "No BASIC stub, explicit entry at $0840"),
        Fixture("BANKRTS", native_prg(0x2000, bank_out=True), 0x2000, None,
                "Disables ROM and I/O before RTS; the return trampoline must restore banking"),
        Fixture("TOPRAM", prg(0xCFFF, b"\x60"), 0xCFFF, None,
                "One RTS at the last accepted RAM address"),
        Fixture("BADLINK", bytes(bad_link), None, None, "Invalid BASIC next-line link"),
        Fixture("EXPR", expression, None, None, "SYS expression instead of a literal address"),
        Fixture("MULTILINE", bytes(multiline), None, None, "Additional BASIC line is not a simple launcher"),
        Fixture("LOW", prg(0x07FF, b"\x60"), None, None, "Protected KERNAL/screen area"),
        Fixture("IOAREA", prg(0xD000, b"\x60"), None, None, "I/O address is not accepted as a payload"),
        Fixture("CROSSIO", prg(0xCFFF, b"\xEA\x60"), None, None, "Payload crosses into I/O space"),
        Fixture("EMPTY", prg(0x2000, b""), None, None, "Header without a payload"),
        Fixture("BASIC", prg(0x0801, b"\x0A\x08\x0A\x00\x99\x22X\x22\0\0\0"),
                None, None, "A BASIC PRINT program is not machine code"),
    ]


def disk_image(files: list[tuple[str, bytes]], label: str = "C64UX PLAY TEST") -> bytes:
    """Create a standard 35-track D64, with chained directory sectors."""
    def sectors(track: int) -> int:
        return 21 if track <= 17 else 19 if track <= 24 else 18 if track <= 30 else 17

    def offset(track: int, sector: int) -> int:
        if not 1 <= track <= 35 or not 0 <= sector < sectors(track):
            raise ValueError((track, sector))
        return (sum(sectors(t) for t in range(1, track)) + sector) * 256

    if len(files) > 144:
        raise ValueError("Too many directory entries")
    disk = bytearray(174848)
    free = {track: set(range(sectors(track))) for track in range(1, 36)}
    directory_sectors = max(1, (len(files) + 7) // 8)
    free[18].difference_update(range(directory_sectors + 1))
    bam = offset(18, 0)
    disk[bam:bam + 4] = bytes((18, 1, 0x41, 0))
    for part in range(directory_sectors):
        directory = offset(18, part + 1)
        disk[directory:directory + 2] = (bytes((18, part + 2)) if part + 1 < directory_sectors
                                        else b"\0\xFF")
    available = [(track, sector) for track in range(1, 36) if track != 18
                 for sector in range(sectors(track))]
    cursor = 0
    for entry, (name, data) in enumerate(files):
        if not name or len(name) > 16 or name.upper() != name:
            raise ValueError(name)
        chunks = [data[index:index + 254] for index in range(0, len(data), 254)] or [b""]
        chain = available[cursor:cursor + len(chunks)]
        if len(chain) != len(chunks):
            raise ValueError("Disk fixture exceeds data capacity")
        cursor += len(chunks)
        for index, ((track, sector), chunk) in enumerate(zip(chain, chunks)):
            location = offset(track, sector)
            link = chain[index + 1] if index + 1 < len(chain) else (0, len(chunk) + 1)
            disk[location:location + 2] = bytes(link)
            disk[location + 2:location + 2 + len(chunk)] = chunk
            free[track].remove(sector)
        location = offset(18, 1 + entry // 8) + (entry % 8) * 32
        disk[location + 2] = 0x82
        disk[location + 3:location + 5] = bytes(chain[0])
        disk[location + 5:location + 21] = name.encode("ascii").ljust(16, b"\xA0")
        disk[location + 30:location + 32] = len(chain).to_bytes(2, "little")
    for track, free_sectors in free.items():
        location = bam + track * 4
        disk[location] = len(free_sectors)
        disk[location + 1:location + 4] = sum(1 << sector for sector in free_sectors).to_bytes(3, "little")
    disk[bam + 0x90:bam + 0xA0] = label.encode("ascii")[:16].ljust(16, b"\xA0")
    disk[bam + 0xA0:bam + 0xAB] = b"\xA0\xA0UX\xA02A\xA0\xA0\xA0\xA0"
    return bytes(disk)


def write_fixtures(directory: Path) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    items = fixtures()
    for fixture in items:
        (directory / f"{fixture.name.lower()}.prg").write_bytes(fixture.data)
    disk = directory / "play-fixtures.d64"
    disk.write_bytes(disk_image([(item.name, item.data) for item in items]))
    return disk


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    path = write_fixtures(root / "examples/play-fixtures")
    print(path)
