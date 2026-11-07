"""Build the locally authored C64 ROMs and 1541 bootstrap ROM.

SPDX-License-Identifier: Apache-2.0
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path


def main() -> None:
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "output",
        nargs="?",
        type=Path,
        default=None,
        help="directory for the generated ROM images",
    )
    args = parser.parse_args()
    if args.output is None:
        args.output = root.parent / "artifacts" / "open-roms"
    args.output.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [sys.executable, str(root / "OpenBasic64" / "build.py"),
         str(args.output / "open-basic64.bin")],
        check=True,
    )
    subprocess.run(
        [
            sys.executable,
            str(root / "OpenKernal64" / "build.py"),
            str(args.output / "open-basic64.map"),
            str(args.output / "open-kernal64.bin"),
        ],
        check=True,
    )
    subprocess.run(
        [sys.executable, str(root / "Open1541Dos" / "build.py"),
         str(args.output / "open-1541-dos.bin")],
        check=True,
    )
    subprocess.run(
        [
            sys.executable,
            str(root / "OpenCharacterRom64" / "build.py"),
            str(args.output / "open-character-rom64.bin"),
        ],
        check=True,
    )


if __name__ == "__main__":
    main()
