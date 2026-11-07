#!/usr/bin/env python3
"""Assemble the host-assisted 1541 helper and optional emulator ROM halves.

SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

from assemble import assemble


HOST_ENTRIES = {
    "RESET": 0xC000,
    "DOS_COMMAND_LOOP": 0xC194,
    "DOS_IDLE_RETURN": 0xE654,
    "DOS_JOB_DISPATCH": 0xF2B0,
    "DOS_JOB_IRQ_RETURN": 0xFE7F,
}


def build() -> tuple[bytes, dict[str, int]]:
    source = Path(__file__).with_name("drive1541.asm").read_text(encoding="utf-8")
    rom, labels = assemble(source, base=0xC000, size=0x4000)
    for name, address in HOST_ENTRIES.items():
        if labels.get(name) != address:
            raise ValueError(f"Host entry {name} must remain at ${address:04X}")
    return rom, labels


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", nargs="?", type=Path,
                        default=Path(__file__).parents[1] / "roms/open-1541-dos.bin")
    parser.add_argument("--split-dir", type=Path,
                        help="Write 1541-c000-rom.bin and 1541-e000-rom.bin for C64Emulator")
    args = parser.parse_args()
    rom, labels = build()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(rom)
    args.output.with_suffix(".map").write_text("".join(
        f"{address:04X} {name}\n" for name, address in sorted(labels.items(), key=lambda pair: pair[1])), encoding="utf-8")
    if args.split_dir:
        args.split_dir.mkdir(parents=True, exist_ok=True)
        (args.split_dir / "1541-c000-rom.bin").write_bytes(rom[:0x2000])
        (args.split_dir / "1541-e000-rom.bin").write_bytes(rom[0x2000:])
    print(f"Open 1541 host helper: {args.output} ({len(rom)} bytes, SHA-256 {hashlib.sha256(rom).hexdigest()})")


if __name__ == "__main__":
    main()
