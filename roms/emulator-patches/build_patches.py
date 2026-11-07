#!/usr/bin/env python3
"""Regenerate RC3 cumulative/RC2-upgrade patches and verify in disposable trees."""
# SPDX-License-Identifier: Apache-2.0
import argparse
import difflib
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

PINNED_COMMIT = "f01750e17a2c3f4cabfd57dc0c470dac93a91a23"
FILES = {
    "D64Image.cs": Path("C64Emulator/Media/D64Image.cs"),
    "IecDrive1541.cs": Path("C64Emulator/Iec/IecDrive1541.cs"),
    "C64System.cs": Path("C64Emulator/Machine/C64System.cs"),
    "C64Model.cs": Path("C64Emulator/Machine/C64Model.cs"),
    "SystemBus.cs": Path("C64Emulator/Machine/SystemBus.cs"),
    "TapImage.cs": Path("C64Emulator/Machine/TapImage.cs"),
    "TapeDeck.cs": Path("C64Emulator/Machine/TapeDeck.cs"),
    "UserPort.cs": Path("C64Emulator/Machine/UserPort.cs"),
    "Cia1.cs": Path("C64Emulator/Cia/Cia1.cs"),
    "Cia2.cs": Path("C64Emulator/Cia/Cia2.cs"),
    "Vic2.cs": Path("C64Emulator/Vic/Vic2.cs"),
    "VicBusPlan.cs": Path("C64Emulator/Vic/VicBusPlan.cs"),
    "Sid.cs": Path("C64Emulator/Sid/Sid.cs"),
    "MountedMediaKind.cs": Path("C64Emulator/Media/MountedMediaKind.cs"),
    "Program.cs": Path("C64Emulator/Program.cs"),
    "C64Window.cs": Path("C64Emulator/C64Window.cs"),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True, help="Original pinned emulator checkout")
    parser.add_argument("--rc2", type=Path, required=True, help="Shipped RC2 emulator-patches/overrides directory, including RESTORE C64System.cs")
    args = parser.parse_args()
    reference, rc2 = args.reference.resolve(), args.rc2.resolve()
    output = Path(__file__).resolve().parent
    overrides = output / "overrides"
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=reference, text=True).strip()
    if head != PINNED_COMMIT:
        raise SystemExit(f"Reference must be at {PINNED_COMMIT}; found {head}")
    original_sources = {}
    for key, rel in FILES.items():
        path = reference / rel
        original_sources[key] = path.read_bytes() if path.exists() else None
        tracked = subprocess.run(["git", "show", f"{head}:{rel.as_posix()}"], cwd=reference, capture_output=True)
        if tracked.returncode == 0:
            if original_sources[key] is None or original_sources[key].replace(b"\r\n", b"\n") != tracked.stdout.replace(b"\r\n", b"\n"):
                raise SystemExit(f"Reference source is modified: {rel}")
        elif original_sources[key] is not None:
            raise SystemExit(f"Untracked reference file would be overwritten: {rel}")
    for key in ("D64Image.cs", "IecDrive1541.cs", "C64System.cs"):
        if not (rc2/key).is_file():
            raise SystemExit(f"Incomplete RC2 overrides: missing {key}")
    rc2_sources = {key: (rc2/key).read_bytes() if (rc2/key).is_file() else original_sources[key] for key in FILES}
    rc2_without_restore = dict(rc2_sources, **{"C64System.cs":original_sources["C64System.cs"]})
    result = {"pinned_commit": head, "patches": []}
    for name, before_sources in (
        ("0001-c64-free-roms-rc3.patch", original_sources),
        ("0002-rc2-to-rc3.patch", rc2_sources),
        ("0003-rc2-without-restore-to-rc3.patch", rc2_without_restore),
    ):
        chunks = []
        changed = []
        for key, rel in FILES.items():
            before_bytes = before_sources[key]
            after_bytes = (overrides/key).read_bytes()
            if before_bytes == after_bytes:
                continue
            before = (before_bytes or b"").decode("utf-8")
            after = after_bytes.decode("utf-8")
            changed.append(key)
            chunks.append(f"diff --git a/{rel.as_posix()} b/{rel.as_posix()}\n")
            if before_bytes is None:
                chunks.append("new file mode 100644\n")
            # Preserve source line endings; the generated patch reproduces the
            # shipped overrides byte-for-byte, including the upstream CRLF style.
            chunks.extend(difflib.unified_diff(before.splitlines(keepends=True), after.splitlines(keepends=True),
                fromfile="/dev/null" if before_bytes is None else "a/"+rel.as_posix(),
                tofile="b/"+rel.as_posix()))
        patch = output/name
        patch.write_bytes("".join(chunks).encode("utf-8"))
        with tempfile.TemporaryDirectory(prefix="free-c64-rc3-patch-") as temporary:
            directory = Path(temporary)
            for key, rel in FILES.items():
                if before_sources[key] is not None:
                    target = directory/rel
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(before_sources[key])
            subprocess.run(["git", "apply", "--check", "--whitespace=nowarn", str(patch)], cwd=directory, check=True)
            subprocess.run(["git", "apply", "--whitespace=nowarn", str(patch)], cwd=directory, check=True)
            for key, rel in FILES.items():
                if (directory/rel).read_bytes() != (overrides/key).read_bytes():
                    raise SystemExit(f"Applied {name} does not reproduce {key}")
        result["patches"].append({"name":name, "sha256":hashlib.sha256(patch.read_bytes()).hexdigest(),
            "verified":"isolated git apply --check, git apply and byte comparison", "files":changed})
    result["override_sha256"] = {key:hashlib.sha256((overrides/key).read_bytes()).hexdigest() for key in FILES}
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
