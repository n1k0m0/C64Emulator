"""Build the independently authored Open Basic 64 ROM.

SPDX-License-Identifier: Apache-2.0
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "OpenKernal64"))
from assemble import assemble  # noqa: E402


def assemble_relaxed(source: str) -> tuple[bytes, dict[str, int]]:
    """Use short branches when possible and widen only out-of-range ones."""
    lines = source.splitlines()
    for _ in range(len(lines)):
        try:
            rom, labels = assemble("\n".join(lines), base=0xA000, size=0x4000)
            if labels["FLOAT_END"] > 0xC000:
                raise ValueError(f"Open Basic 64 exceeds 8 KiB: ${labels['FLOAT_END']:04X}")
            return rom[:0x2000], labels
        except ValueError as exc:
            location = re.search(r"line (\d+): branch out of range", str(exc))
            if location is None:
                raise
            index = int(location.group(1)) - 1
            branch = re.search(r"\b(bcc|bcs|beq|bmi|bne|bpl|bvc|bvs)\b",
                               lines[index], re.IGNORECASE)
            if branch is None:
                raise
            lines[index] = (lines[index][:branch.start()] + "j" + branch.group() +
                            lines[index][branch.end():])
    raise ValueError("branch relaxation did not converge")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source = Path(__file__).with_name("basic.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("for_float.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float.asm").read_text(encoding="utf-8")
    rom, labels = assemble_relaxed(source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(rom)
    args.output.with_suffix(".map").write_text(
        "".join(f"{address:04X} {name}\n" for name, address in
                sorted(labels.items(), key=lambda item: item[1])),
        encoding="utf-8",
    )
    print(f"Open Basic 64: {args.output} ({len(rom)} bytes)")


if __name__ == "__main__":
    main()
