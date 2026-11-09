"""Build the original ROM RUNNER PRG and its C64/UX demonstration disk.

The game is self-contained 6510 machine code, distributed under Apache-2.0.
The disk writer accepts ordinary named PRGs so integration tests can add
independent loader fixtures without modifying the game. No third-party ROM,
game, or asset is copied by this builder.

SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
from typing import Iterable

ROOT = Path(__file__).resolve().parents[1]


def _builder():
    spec = importlib.util.spec_from_file_location("c64ux_game_builder", ROOT / "build.py")
    builder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(builder)
    return builder


def build_game(output: Path | None = None) -> Path:
    """Assemble ROM RUNNER; return the path to the ordinary $0801 PRG."""
    builder = _builder()
    code, symbols = builder.assemble_relaxed(
        (ROOT / "examples/rom_runner.asm").read_text(encoding="utf-8"),
        0x0801, 8192,
    )
    assert symbols["ENTRY"] == 2061, "BASIC SYS and machine entry must agree"
    destination = output or ROOT / "examples/rom_runner.prg"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(b"\x01\x08" + code[:symbols["GAME_END"] - 0x0801])
    destination.with_suffix(".map").write_text(
        "".join(f"{value:04X} {name}\n" for name, value in
                sorted(symbols.items(), key=lambda p: (p[1], p[0]))),
        encoding="ascii",
    )
    return destination


def _sectors(track: int) -> int:
    return 21 if track <= 17 else 19 if track <= 24 else 18 if track <= 30 else 17


def _offset(track: int, sector: int) -> int:
    if not 1 <= track <= 35 or not 0 <= sector < _sectors(track):
        raise ValueError((track, sector))
    return (sum(_sectors(t) for t in range(1, track)) + sector) * 256


def write_disk(files: Iterable[tuple[str, bytes]], output: Path,
               name: str = "C64UX PLAY") -> Path:
    """Create a 35-track D64 containing up to eight closed PRG files.

    files contains (PETSCII-compatible ASCII filename, complete PRG bytes).
    Only the documented DOS directory, BAM and sector-chain formats are used.
    """
    items = list(files)
    if not 1 <= len(items) <= 8:
        raise ValueError("The example disk accepts one to eight PRG files")
    if len({title.upper() for title, _ in items}) != len(items):
        raise ValueError("Duplicate directory filename")
    disk = bytearray(174848)
    bam = _offset(18, 0)
    directory = _offset(18, 1)
    disk[bam:bam + 4] = bytes((18, 1, 0x41, 0))
    disk[directory:directory + 2] = b"\x00\xff"
    free = {t: set(range(_sectors(t))) for t in range(1, 36)}
    free[18].difference_update((0, 1))
    available = [(t, s) for t in range(1, 36) if t != 18 for s in range(_sectors(t))]
    cursor = 0
    for entry, (title, data) in enumerate(items):
        encoded = title.upper().encode("ascii")
        if not 1 <= len(encoded) <= 16:
            raise ValueError("D64 filenames need one to sixteen ASCII bytes")
        if len(data) < 2:
            raise ValueError("A PRG needs its two-byte load address")
        chunks = [data[i:i + 254] for i in range(0, len(data), 254)]
        chain = available[cursor:cursor + len(chunks)]
        if len(chain) != len(chunks):
            raise ValueError("Example disk is full")
        cursor += len(chunks)
        for i, ((track, sector), chunk) in enumerate(zip(chain, chunks)):
            pos = _offset(track, sector)
            next_pair = chain[i + 1] if i + 1 < len(chain) else (0, len(chunk) + 1)
            disk[pos:pos + 2] = bytes(next_pair)
            disk[pos + 2:pos + 2 + len(chunk)] = chunk
            free[track].remove(sector)
        pos = directory + entry * 32
        disk[pos + 2] = 0x82
        disk[pos + 3:pos + 5] = bytes(chain[0])
        disk[pos + 5:pos + 21] = encoded.ljust(16, b"\xa0")
        disk[pos + 30:pos + 32] = len(chain).to_bytes(2, "little")
    for track, unused in free.items():
        pos = bam + track * 4
        disk[pos] = len(unused)
        disk[pos + 1:pos + 4] = sum(1 << s for s in unused).to_bytes(3, "little")
    disk[bam + 0x90:bam + 0xA0] = name.upper().encode("ascii")[:16].ljust(16, b"\xa0")
    disk[bam + 0xA0:bam + 0xAB] = b"\xa0\xa0UX\xa02A\xa0\xa0\xa0\xa0"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(disk)
    return output


def build(output: Path | None = None,
          extra_files: Iterable[tuple[str, bytes]] = ()) -> Path:
    """Rebuild game and a D64; optional extra PRGs are loader test fixtures."""
    game = build_game()
    return write_disk(
        [("ROMRUNNER", game.read_bytes()), *extra_files],
        output or ROOT / "examples/play-games.d64",
    )


if __name__ == "__main__":
    result = build()
    print(f"Created {result.name} ({result.stat().st_size} bytes)")
    game = ROOT / "examples/rom_runner.prg"
    print(f"Created {game.name} ({game.stat().st_size} bytes, SYS 2061)")
