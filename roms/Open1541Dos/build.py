"""Build the independently authored Open 1541 DOS bootstrap ROM.

SPDX-License-Identifier: Apache-2.0
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "OpenKernal64"))
from assemble import assemble  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source = Path(__file__).with_name("dos.asm").read_text(encoding="utf-8")
    rom, labels = assemble(source, base=0xC000, size=0x4000,
                           always_long_branches=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(rom)
    args.output.with_suffix(".map").write_text(
        "".join(f"{address:04X} {name}\n" for name, address in
                sorted(labels.items(), key=lambda item: item[1])),
        encoding="utf-8",
    )
    print(f"Open 1541 DOS bootstrap: {args.output} ({len(rom)} bytes)")


if __name__ == "__main__":
    main()
