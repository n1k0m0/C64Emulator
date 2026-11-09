"""Build the C64/UX demonstration D64 from independent test data.

SPDX-License-Identifier: Apache-2.0
No external disk utilities, copyrighted firmware, or host shortcuts are used.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
README = (
    b"Hello from the C64/UX demo disk.\n"
    b"This is an ASCII SEQ file.\n"
    b"Import it into the RAM filesystem.\n"
)


def sectors(track: int) -> int:
    return 21 if track <= 17 else 19 if track <= 24 else 18 if track <= 30 else 17


def offset(track: int, sector: int) -> int:
    if not 1 <= track <= 35 or not 0 <= sector < sectors(track):
        raise ValueError((track, sector))
    return (sum(sectors(t) for t in range(1, track)) + sector) * 256


def build(output: Path | None = None) -> Path:
    spec = importlib.util.spec_from_file_location("c64ux_build", ROOT / "build.py")
    builder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(builder)
    code, symbols = builder.assemble_relaxed(
        (ROOT / "examples/hello.asm").read_text(encoding="utf-8"), 0x6000, 512
    )
    hello = b"\x00\x60" + code[:symbols["EXAMPLE_END"] - 0x6000]
    (ROOT / "examples/hello.prg").write_bytes(hello)
    files = [
        ("HELLO", 0x82, hello),
        ("README", 0x81, README),
        ("LARGE", 0x81, b"A" * 512 + b"B"),
        ("LOWPRG", 0x82, b"\x01\x08\x60"),
        ("OVERPRG", 0x82, b"\xff\x9f\x60\x60"),
        ("TOPPRG", 0x82, b"\xff\x9f\x60"),
        ("EMPTYPRG", 0x82, b"\x00\x60"),
    ]
    disk = bytearray(174848)
    bam = offset(18, 0)
    disk[bam:bam + 4] = bytes((18, 1, 0x41, 0))
    free = {t: set(range(sectors(t))) for t in range(1, 36)}
    free[18].difference_update((0, 1))
    directory = offset(18, 1)
    disk[directory:directory + 2] = b"\x00\xff"

    available = [(t, s) for t in range(1, 36) if t != 18 for s in range(sectors(t))]
    cursor = 0
    for entry, (name, file_type, data) in enumerate(files):
        chunks = [data[i:i + 254] for i in range(0, len(data), 254)] or [b""]
        chain = available[cursor:cursor + len(chunks)]
        cursor += len(chunks)
        for i, ((track, sector), chunk) in enumerate(zip(chain, chunks)):
            location = offset(track, sector)
            next_pair = chain[i + 1] if i + 1 < len(chain) else (0, len(chunk) + 1)
            disk[location:location + 2] = bytes(next_pair)
            disk[location + 2:location + 2 + len(chunk)] = chunk
            free[track].remove(sector)
        record = directory + entry * 32
        disk[record + 2] = file_type
        disk[record + 3:record + 5] = bytes(chain[0])
        disk[record + 5:record + 21] = name.encode("ascii").ljust(16, b"\xa0")
        disk[record + 30:record + 32] = len(chain).to_bytes(2, "little")

    for track, available_sectors in free.items():
        record = bam + track * 4
        bitmap = sum(1 << sector for sector in available_sectors)
        disk[record] = len(available_sectors)
        disk[record + 1:record + 4] = bitmap.to_bytes(3, "little")
    disk[bam + 0x90:bam + 0xA0] = b"C64UX DEMO".ljust(16, b"\xa0")
    disk[bam + 0xA0:bam + 0xAB] = b"\xa0\xa0UX\xa02A\xa0\xa0\xa0\xa0"

    destination = output or ROOT / "examples/c64ux-demo.d64"
    destination.write_bytes(disk)
    return destination


if __name__ == "__main__":
    result = build()
    print(f"Created {result.name} ({result.stat().st_size} bytes)")
