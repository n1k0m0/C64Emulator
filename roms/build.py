#!/usr/bin/env python3
"""Build the matched C64 ROM set from the included editable sources.

Requirements: Python 3.10+ and the ca65/ld65 tools from cc65 on PATH (or pass
--ca65/--ld65). No proprietary ROM or reference binary is a build input.
SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "src"))
from assemble import assemble
from character_rom import build as build_characters, codepoint_map
from drive_build import build as build_drive

EXPECTED_FILES = {
    "roms/open-basic64.bin": 8192,
    "roms/open-kernal64.bin": 8192,
    "roms/open-character-rom64.bin": 4096,
    "roms/open-1541-dos.bin": 16384,
    "emulator-roms/c64-basic-kernal.bin": 16384,
    "emulator-roms/c64-character.bin": 4096,
    "emulator-roms/1541-c000-rom.bin": 8192,
    "emulator-roms/1541-e000-rom.bin": 8192,
}


def executable(name: str) -> str:
    result = shutil.which(name)
    if result is None:
        raise SystemExit(f"Missing build tool {name!r}. Install cc65 or pass --ca65 and --ld65 paths.")
    return result


def write_map(path: Path, labels: dict[str, int]) -> None:
    path.write_text("".join(f"{address:04X} {name}\n" for name, address in
                           sorted(labels.items(), key=lambda item: (item[1], item[0]))), encoding="utf-8")


def read_cc65_symbols(path: Path) -> dict[str, int]:
    labels = {}
    for line in path.read_text().splitlines():
        if not line.startswith("sym\t"):
            continue
        name = re.search(r'name="([A-Za-z_][A-Za-z_0-9]*)"', line)
        value = re.search(r'\bval=(0x[0-9A-Fa-f]+)', line)
        if name and value:
            labels[name[1]] = int(value[1], 16)
    return labels


def build(output: Path, ca65: str = "ca65", ld65: str = "ld65") -> dict:
    ca65, ld65 = executable(ca65), executable(ld65)
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    basic_source = ROOT / "src/mit_basic"
    with tempfile.TemporaryDirectory(prefix=".source-build-", dir=output) as directory:
        stage = Path(directory)
        roms, emulator = stage / "roms", stage / "emulator-roms"
        roms.mkdir()
        emulator.mkdir()
        subprocess.run([ca65, "--feature", "force_range", "-g", "-I", str(basic_source),
                        str(basic_source / "basic64.s"), "-o", str(stage / "basic.o")],
                       cwd=stage, check=True)
        subprocess.run([ld65, "-C", str(basic_source / "basic64.cfg"), str(stage / "basic.o"),
                        "-o", str(stage / "unused.bin"), "--dbgfile", str(stage / "basic.dbg"),
                        "-m", str(stage / "basic.linkmap")], cwd=stage, check=True)
        basic = (stage / "basic.bin").read_bytes()
        extension = (stage / "extension.bin").read_bytes()
        labels = read_cc65_symbols(stage / "basic.dbg")
        required = {"TXTTAB": 0x2B, "VARTAB": 0x2D, "ARYTAB": 0x2F,
                    "STREND": 0x31, "FRETOP": 0x33, "MEMSIZ": 0x37,
                    "FAC": 0x61, "CHRGET": 0x73, "TXTPTR": 0x7A,
                    "USRPOK": 0x0310}
        for name, address in required.items():
            if labels.get(name) != address:
                raise ValueError(f"C64 memory ABI moved: {name} must be ${address:04X}")
        if len(basic) != 8192 or len(extension) != 0x4C0:
            raise ValueError("Unexpected BASIC or extension size")
        kernal_source = (ROOT / "src/kernal_mit.asm").read_text()
        kernal_source += "\n" + (ROOT / "src/keyboard_mit.asm").read_text()
        kernal_source += "\n" + (ROOT / "src/system_mit.asm").read_text()
        kernal_raw, kernal_labels = assemble(kernal_source, base=0xE000, size=0x2000)
        if kernal_raw[:0x4C0] != b"\xff" * 0x4C0:
            raise ValueError("KERNAL overlaps the reserved BASIC extension at $E000..$E4BF")
        # The linker bounds BASIC independently; E4C0-E4FF belongs to the
        # platform timing helper and is assembled with the rest of KERNAL.
        kernal = extension + kernal_raw[0x4C0:]
        for address in range(0xFF81, 0xFFF4, 3):
            if kernal[address - 0xE000] not in (0x4C, 0x6C):
                raise ValueError(f"Invalid public KERNAL entry at ${address:04X}")
        chars = build_characters()
        drive, drive_labels = build_drive()
        (roms / "open-basic64.bin").write_bytes(basic)
        (roms / "open-kernal64.bin").write_bytes(kernal)
        (roms / "open-character-rom64.bin").write_bytes(chars)
        (roms / "open-1541-dos.bin").write_bytes(drive)
        write_map(roms / "open-basic64.map", labels)
        write_map(roms / "open-kernal64.map", kernal_labels)
        write_map(roms / "open-1541-dos.map", drive_labels)
        (roms / "open-character-rom64.codepoints.json").write_text(
            json.dumps(codepoint_map(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        (emulator / "c64-basic-kernal.bin").write_bytes(basic + kernal)
        (emulator / "c64-character.bin").write_bytes(chars)
        (emulator / "1541-c000-rom.bin").write_bytes(drive[:8192])
        (emulator / "1541-e000-rom.bin").write_bytes(drive[8192:])
        files = {}
        for name, size in EXPECTED_FILES.items():
            data = (stage / name).read_bytes()
            if len(data) != size:
                raise ValueError(f"{name}: expected {size} bytes, got {len(data)}")
            files[name] = {"bytes": size, "sha256": hashlib.sha256(data).hexdigest()}
        manifest = {
            "format_version": 1,
            "version": "2026.11.06-rc3",
            "name": "C64 freie ROMs",
            "basic_origin": "Microsoft BASIC-M6502 1.1, MIT license, adapted to C64 interfaces",
            "upstream_basic_commit": "7460af2c03ae19c0e60ff327489229d2005b9357",
            "conversion_commit": "fa31daddafa6f1280efce9d66f60a4d2bf4ea474",
            "target_emulator": "https://github.com/n1k0m0/C64Emulator",
            "reference_emulator_commit": "f01750e17a2c3f4cabfd57dc0c470dac93a91a23",
            "drive_architecture": "1541 helper for the emulator's software IEC transport",
            "basic_cold_start": f"${labels['INIT']:04X}",
            "kernal_reset_vector": f"${int.from_bytes(kernal[-4:-2], 'little'):04X}",
            "basic_extension_range": "$E000..$E4BF",
            "platform_timing_range": "$E4C0..$E4FF",
            "files": files,
            "source_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                              for p in sorted((ROOT / "src").rglob("*"))
                              if p.is_file() and p.suffix in (".asm", ".s", ".py", ".cfg")},
        }
        # Publish a matched pair only after both ROMs and every companion build.
        for folder in (roms, emulator):
            for file in sorted(folder.iterdir()):
                target = output / file.relative_to(stage)
                target.parent.mkdir(parents=True, exist_ok=True)
                os.replace(file, target)
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build")
    parser.add_argument("--ca65", default="ca65")
    parser.add_argument("--ld65", default="ld65")
    args = parser.parse_args()
    manifest = build(args.output_dir, args.ca65, args.ld65)
    print(f"Built matched ROM set: {args.output_dir.resolve()}")
    for name, entry in manifest["files"].items():
        print(f"{entry['bytes']:5d}  {entry['sha256']}  {name}")
