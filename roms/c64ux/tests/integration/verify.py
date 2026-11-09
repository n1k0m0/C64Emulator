"""Build and exercise the real, externally supplied C64Emulator core.

SPDX-License-Identifier: Apache-2.0

The firmware runs unchanged.  No shell or KERNAL call is replaced by Python.
See README.md for the external source and .NET SDK requirements.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zlib


HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]
PINNED_REVISION = "f01750e17a2c3f4cabfd57dc0c470dac93a91a23"
DISK_TEXT = (
    "Hello from the C64/UX demo disk.\n"
    "This is an ASCII SEQ file.\n"
    "Import it into the RAM filesystem.\n"
)


@dataclass
class Case:
    command: str
    description: str
    expected: str | None = None
    contains: tuple[str, ...] = ()
    clock: bool = False
    observation: bool = False


def cases(disk: Path) -> list[Case]:
    return [
        Case("pwd", "boot enters the home directory", "/home/root"),
        Case("uname", "system identity", contains=("C64/UX", "6510")),
        Case('echo "Hello world" > note', "quoted text and redirection", ""),
        Case("cat note", "read saved text", "Hello world"),
        Case("echo second >> note", "append to a RAM file", ""),
        Case("cat note", "read both appended lines", "Hello world\nsecond"),
        Case("mkdir work", "create a directory", ""),
        Case("cp note work/copy", "copy across directories", ""),
        Case("cat work/copy", "read independent copied file", "Hello world\nsecond"),
        Case("sh hello.sh", "execute the bundled shell script",
             contains=("Hello from C64/UX", "scripts work")),
        Case("uptime", "clock is derived from actual CIA jiffies", clock=True),
        Case("mem", "memory accounting", contains=("16 KiB", "32", "bytes stored")),
        Case("keys:pwd", "physical CIA keyboard matrix", "/home/root"),
        Case('keys:echo "Shift Works" > keyfile', "physical Shift, quotes and >", ""),
        Case("cat keyfile", "physical mixed-case input", "Shift Works"),
        Case("keys:echo cancel\u0003", "physical Ctrl+C cancellation", contains=("^C",)),
        Case("echo alive", "shell recovers after cancellation", "alive"),
        Case("echo protected > kept", "prepare long-line guard", ""),
        Case("echo overwritten > kept" + " " * 140, "overlong command is discarded",
             contains=("line too long", "discarded")),
        Case("cat kept", "discarded input has no file side effect", "protected"),
        Case(f"mount8:{disk}", "mount the isolated D64 fixture", contains=("DISK MOUNTED",), observation=True),
        Case("run:6000000:dir", "directory over the actual IEC implementation",
             contains=("HELLO", "README", "LARGE")),
        Case("run:6000000:dcat README", "observe ASCII SEQ support over IEC", observation=True),
        Case("echo import-sentinel > imported", "prepare atomic import destination", ""),
        Case("run:6000000:import README imported", "observe complete SEQ import", observation=True),
        Case("cat imported", "observe imported or preserved bytes", observation=True),
        Case("run:6000000:import LARGE imported", "reject 513-byte import",
             observation=True),
        Case("cat imported", "failed import preserves the original RAM file",
             observation=True),
        Case("run:4000000:load LOWPRG", "reject PRG below application memory",
             contains=("PRG must fit",)),
        Case("run", "rejected PRG cannot be executed", contains=("no program loaded",)),
        Case("run:4000000:load OVERPRG", "reject PRG crossing $9FFF",
             contains=("PRG must fit",)),
        Case("run", "overflowed PRG cannot be executed", contains=("no program loaded",)),
        Case("run:4000000:load EMPTYPRG", "reject a PRG containing only its header",
             contains=("PRG must fit",)),
        Case("run:4000000:load TOPPRG", "accept the final byte of application RAM", ""),
        Case("run", "RTS from $9FFF returns to the shell", ""),
        Case("run:4000000:load HELLO", "load the native 6510 example", ""),
        Case("run", "execute native code and return",
             contains=("hello from a native 6510 program!",)),
        Case("run:6000000:export imported SAVED", "observe SEQ write/DOS status",
             observation=True),
        Case("run:6000000:dcat SAVED", "observe SEQ persistence after write",
             observation=True),
        Case("echo integration-complete", "shell still accepts commands after disk work",
             "integration-complete"),
    ]


def body(snapshot: dict) -> str:
    lines = snapshot["transcript"].split("\n")
    if len(lines) < 2:
        return snapshot["transcript"]
    return "\n".join(lines[1:-1])


def png_from_ppm(source: Path, destination: Path) -> None:
    """Losslessly encode the actual emulator's RGB framebuffer using stdlib."""
    with source.open("rb") as stream:
        if stream.readline().strip() != b"P6":
            raise ValueError("Unexpected framebuffer PPM format")
        width, height = map(int, stream.readline().split())
        if stream.readline().strip() != b"255":
            raise ValueError("Unexpected framebuffer color depth")
        data = stream.read()
    if len(data) != width * height * 3:
        raise ValueError("Incomplete emulator framebuffer")

    def chunk(kind: bytes, content: bytes) -> bytes:
        return struct.pack(">I", len(content)) + kind + content + struct.pack(">I", zlib.crc32(kind + content))

    scanlines = b"".join(b"\0" + data[y * width * 3:(y + 1) * width * 3] for y in range(height))
    destination.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(scanlines, 9))
        + chunk(b"IEND", b"")
    )


def run(command: list[str], timeout: int) -> subprocess.CompletedProcess:
    result = subprocess.run(command, text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(
            f"Command failed ({result.returncode}): {command[0]}\n"
            + result.stdout[-12000:] + "\n" + result.stderr[-4000:]
        )
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="External C64Emulator repository root")
    parser.add_argument("--dotnet", default="dotnet", help=".NET 10 SDK executable")
    parser.add_argument("--output", type=Path, default=PROJECT / "integration-results")
    parser.add_argument("--allow-other-revision", action="store_true")
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    revision = run(["git", "-C", str(source), "rev-parse", "HEAD"], 30).stdout.strip()
    if revision != PINNED_REVISION and not args.allow_other_revision:
        parser.error(f"Expected external revision {PINNED_REVISION}; found {revision}")
    output.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    results: list[dict] = []
    source_roms = PROJECT / "roms"
    rom_hashes = {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(source_roms.glob("*.bin"))
    }
    with tempfile.TemporaryDirectory(prefix="c64ux-integration-") as temporary:
        work = Path(temporary)
        roms = work / "roms"
        roms.mkdir()
        for path in source_roms.glob("*.bin"):
            shutil.copy2(path, roms / path.name)
        disk = work / "demo.d64"
        shutil.copy2(PROJECT / "examples" / "c64ux-demo.d64", disk)
        selections = cases(disk)
        input_path = work / "commands.json"
        input_path.write_text(json.dumps([item.command for item in selections]), encoding="utf-8")
        build = work / "build"
        print("Building the unchanged external emulator core...", flush=True)
        run([dotnet, "build", str(HERE / "EmulatorHarness.csproj"), "-c", "Release",
             "-o", str(build), f"-p:EmulatorSourceRoot={source}",
             f"-p:BaseIntermediateOutputPath={work / 'obj'}/"], 180)
        raw = work / "raw"
        print(f"Running {len(selections)} command scenarios on the actual C64Emulator core...", flush=True)
        run([dotnet, str(build / "EmulatorHarness.dll"), str(roms), str(raw), str(input_path)], 300)
        snapshots = json.loads((raw / "results.json").read_text(encoding="utf-8"))
        if len(snapshots) != len(selections) + 1:
            raise RuntimeError("Emulator did not return every requested snapshot")
        boot = snapshots[0]
        boot_ok = "C64/UX" in boot["transcript"] and "root@c64:/home/root# " in boot["transcript"]
        results.append({"test": "ROM cold boot", "status": "passed" if boot_ok else "failed"})
        for index, (case, snapshot) in enumerate(zip(selections, snapshots[1:]), 1):
            actual = body(snapshot)
            problems = []
            if case.expected is not None and actual != case.expected:
                problems.append(f"Expected {case.expected!r}; observed {actual!r}")
            for required in case.contains:
                if required not in snapshot["transcript"]:
                    problems.append(f"Missing output: {required!r}")
            if case.clock:
                match = re.search(r"up (\d+) seconds", snapshot["transcript"])
                lower, upper = snapshots[index - 1]["jiffies"] // 60, snapshot["jiffies"] // 60
                if not match or not lower <= int(match[1]) <= upper:
                    problems.append(f"Uptime is outside observed jiffy interval {lower}..{upper}s")
            if snapshot["state"] == "Jammed":
                problems.append("CPU JAM")
            results.append({
                "test": case.description,
                "command": case.command if not case.command.startswith("mount8:") else "mount8:demo.d64",
                "status": "failed" if problems else "observed" if case.observation else "passed",
                "problems": problems,
            })
        read_index = next(i for i, c in enumerate(selections, 1) if ":dcat README" in c.command)
        import_index = next(i for i, c in enumerate(selections, 1) if ":import README imported" in c.command)
        large_index = next(i for i, c in enumerate(selections, 1) if ":import LARGE imported" in c.command)
        disk_read = body(snapshots[read_index])
        import_result, imported_text = body(snapshots[import_index]), body(snapshots[import_index + 1])
        large_result, after_large = body(snapshots[large_index]), body(snapshots[large_index + 1])
        if (disk_read == DISK_TEXT.rstrip("\n") and not import_result
                and imported_text == DISK_TEXT.rstrip("\n")
                and "exceeds 512 bytes" in large_result and after_large == imported_text):
            read_result = "SEQ read/import and rejection of an oversized import verified in the actual emulator."
            read_status = "passed"
        elif ("disk: I/O" in disk_read and "disk: I/O" in import_result
              and imported_text == "import-sentinel" and "disk: I/O" in large_result
              and after_large == "import-sentinel"):
            read_result = (
                "This public emulator revision only opens PRG entries and does not parse SEQ suffixes; "
                "the ROM reports read/import errors and preserves the existing RAM file. "
                "Actual-core SEQ reads/imports are not verified."
            )
            read_status = "limited"
        else:
            read_result = (
                f"Unexpected SEQ result: dcat={disk_read!r}; import={import_result!r}; "
                f"contents={imported_text!r}; large={large_result!r}; after={after_large!r}"
            )
            read_status = "failed"
        results.append({"test": "SEQ read/import outcome and atomic failure", "status": read_status,
                        "detail": read_result})
        export_index = next(i for i, c in enumerate(selections, 1) if ":export imported SAVED" in c.command)
        export_text, reread = body(snapshots[export_index]), body(snapshots[export_index + 1])
        if not export_text and reread == imported_text:
            write_result = "SEQ export and subsequent reread verified in the actual emulator."
            write_status = "passed"
        elif "disk:" in export_text and "disk:" in reread:
            write_result = (
                "This public emulator revision rejects normal SEQ creation; the ROM reports the DOS/I/O "
                "error. Persistent SEQ export is not verified in this actual-core run."
            )
            write_status = "limited"
        else:
            write_result = f"Export/persistence mismatch: export={export_text!r}, reread={reread!r}"
            write_status = "failed"
        results.append({"test": "SEQ write outcome is reported honestly", "status": write_status,
                        "detail": write_result})
        png_from_ppm(raw / "boot.ppm", output / "boot.png")
        png_from_ppm(raw / f"command-{len(selections):02}.ppm", output / "final-screen.png")
        transcript = []
        for snapshot in snapshots:
            command = snapshot["command"] or "BOOT"
            if command.startswith("mount8:"):
                command = "mount8:demo.d64"
            transcript.append(f"### {command}\n{snapshot['transcript']}\n")
        (output / "transcript.txt").write_text("\n".join(transcript), encoding="utf-8")
        (output / "boot.txt").write_text(boot["screen"], encoding="utf-8")
        report = {
            "suite": "Actual C64Emulator core integration",
            "emulator_repository": "https://github.com/n1k0m0/C64Emulator",
            "emulator_commit": revision,
            "pinned_revision": revision == PINNED_REVISION,
            "firmware_execution": "Unmodified ROM instructions on the original emulator CPU and peripherals",
            "load_shortcut": False,
            "kernal_iec_hooks": False,
            "automatic_host_input_injection": False,
            "manual_queue_input_and_physical_matrix": True,
            "host_audio_sink_only_is_replaced": True,
            "success": not any(item["status"] == "failed" for item in results),
            "scenarios": len(selections),
            "seconds": round(time.perf_counter() - started, 3),
            "rom_sha256": rom_hashes,
            "disk_read_scope": read_result,
            "disk_write_scope": write_result,
            "results": results,
        }
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for item in results:
        print(f"{item['status'].upper():8} {item['test']}")
        for problem in item.get("problems", []):
            print(f"          {problem}")
    print(f"Report: {output / 'report.json'}")
    raise SystemExit(0 if report["success"] else 1)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.TimeoutExpired) as exc:
        print(f"Integration test could not complete: {exc}", file=sys.stderr)
        raise SystemExit(2)
