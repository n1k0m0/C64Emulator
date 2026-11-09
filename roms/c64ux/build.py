"""Reproducible C64/UX ROM builder. Requires Python 3.10+ only.

SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "src"))
from assemble import assemble


def preprocess(source: str) -> str:
    """Expand named numeric constants and two small pointer/string macros."""
    constants = {}
    lines = []
    for raw in source.splitlines():
        if raw.strip().startswith(".equ "):
            _, name, value = raw.split(None, 2)
            constants[name.upper()] = value.split(";", 1)[0].strip()
        else:
            lines.append(raw)
    expanded = []
    for raw in lines:
        code, _, comment = raw.partition(";")
        match = re.fullmatch(r"\s*(say|point)\s+(\w+)\s*", code)
        if match:
            command, label = match.groups()
            code = f"    lda #<{label}\n    sta TEXT\n    lda #>{label}\n    sta TEXT+1"
            if command == "say":
                code += "\n    jsr puts"
        if not re.search(r"\.text\b", code, re.I):
            code = re.sub(r"\b[A-Za-z_][A-Za-z_0-9]*\b", lambda m: constants.get(m.group().upper(), m.group()), code)
        expanded.append(code + (" ;" + comment if comment else ""))
    return "\n".join(expanded)


def assemble_relaxed(source: str, base: int, size: int, write_map: bytearray | None = None):
    lines = preprocess(source).splitlines()
    for _ in range(len(lines) + 1):
        try:
            return assemble("\n".join(lines), base=base, size=size, write_map=write_map)
        except ValueError as exc:
            match = re.search(r"line (\d+): branch out of range", str(exc))
            if not match:
                raise
            i = int(match.group(1)) - 1
            lines[i] = re.sub(r"\b(bcc|bcs|beq|bmi|bne|bpl|bvc|bvs)\b", r"j\1", lines[i], count=1, flags=re.I)
    raise ValueError("Branch relaxation failed to converge")


def write_rom(name: str, source: str, base: int):
    data, labels = assemble_relaxed(source, base, 8192)
    path = ROOT / "roms" / name
    path.write_bytes(data)
    path.with_suffix(".map").write_text("".join(f"{value:04X} {key}\n" for key, value in sorted(labels.items(), key=lambda p: (p[1], p[0]))), encoding="ascii")
    return data, labels


def main():
    (ROOT / "roms").mkdir(exist_ok=True)
    shell_source = "\n".join((ROOT / "src" / f).read_text(encoding="utf-8") for f in ("shell.asm", "filesystem.asm", "disk.asm", "strings.asm"))
    kernel_source = (ROOT / "src/kernal.asm").read_text(encoding="utf-8")
    if (ROOT / "src/keyboard.asm").exists():
        kernel_source += "\n" + (ROOT / "src/keyboard.asm").read_text(encoding="utf-8")
    play_source = (ROOT / "src/play.asm").read_text(encoding="utf-8")
    written = bytearray(0x6000)
    combined, labels = assemble_relaxed(shell_source + "\n" + kernel_source + "\n" + play_source,
                                        0xA000, 0x6000, write_map=written)
    if any(written[0x2000:0x4000]):
        raise ValueError("Code/data emitted outside the two physical 8 KiB ROM slots")
    if labels["SHELL_END"] > 0xBE00:
        raise ValueError("Shell core overlaps the game loader's BASIC-slot reservation")
    basic, kernal = combined[:0x2000], combined[0x4000:]
    for filename, data in (("open-basic64.bin", basic), ("open-kernal64.bin", kernal)):
        path = ROOT / "roms" / filename
        path.write_bytes(data)
        path.with_suffix(".map").write_text("".join(f"{value:04X} {key}\n" for key, value in
                                                    sorted(labels.items(), key=lambda p: (p[1], p[0]))), encoding="ascii")
    (ROOT / "roms/c64-basic-kernal.bin").write_bytes(basic + kernal)
    (ROOT / "roms/c64-character.bin").write_bytes((ROOT / "roms/open-character-rom64.bin").read_bytes())
    drive = (ROOT / "roms/open-1541-dos.bin").read_bytes()
    assert len(drive) == 16384
    (ROOT / "roms/1541-c000-rom.bin").write_bytes(drive[:8192])
    (ROOT / "roms/1541-e000-rom.bin").write_bytes(drive[8192:])
    example_source = (ROOT / "examples/hello.asm").read_text(encoding="utf-8")
    example, exlabels = assemble_relaxed(example_source, 0x6000, 512)
    (ROOT / "examples/hello.prg").write_bytes(b"\x00\x60" + example[:exlabels["EXAMPLE_END"] - 0x6000])
    from tools.make_demo_disk import build as build_demo_disk
    build_demo_disk()
    from tools.make_play_disk import build as build_play_disk
    build_play_disk()
    files = sorted((ROOT / "roms").glob("*.bin"))
    hashes = {p.name: {"bytes": p.stat().st_size, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in files}
    (ROOT / "roms/manifest.json").write_text(json.dumps({"system": "C64/UX", "version": "0.2.0", "shell_core_bytes": labels["SHELL_END"] - 0xA000,
        "basic_used_bytes": sum(written[:0x2000]), "kernal_used_bytes": sum(written[0x4000:]),
        "images": hashes}, indent=2) + "\n")
    print(f"C64/UX 0.2.0: BASIC slot {sum(written[:0x2000])}/8192 bytes, KERNAL slot {sum(written[0x4000:])}/8192 bytes")
    print("Ready: roms/c64-basic-kernal.bin and roms/c64-character.bin")


if __name__ == "__main__":
    main()
