"""Verify the unchanged classic ROM profile on the real C64Emulator core.

SPDX-License-Identifier: Apache-2.0

This is a deliberately narrow compatibility check: BASIC arithmetic, an
ordinary $0801 PRG loaded over IEC, its BASIC SYS bootstrap, and actual game
input. It does not establish general game or BASIC compatibility. The
classic profile remains byte-for-byte unchanged throughout the test.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

from verify import HERE, PROJECT, PINNED_REVISION, png_from_ppm, run


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def symbols(path: Path) -> dict[str, int]:
    result = {}
    for line in path.read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([0-9A-Fa-f]+)\s+(\w+)", line.strip())
        if match:
            result[match[2]] = int(match[1], 16)
    return result


def classic_frontend(program: str) -> str:
    """Add wait/held-key actions to a temporary frontend copy only.

    These use the emulator's existing physical-keyboard API. They do not
    write CPU/RAM state, intercept a KERNAL call, or alter any chip source.
    Distinct prefixes avoid conflicting with other harness extensions.
    """
    marker = '    else if (command == "warm-reset:")'
    addition = '''    else if (command.StartsWith("classic-wait:", StringComparison.Ordinal))
        runCycles = int.Parse(command[13..]);
    else if (command.StartsWith("classic-hold:", StringComparison.Ordinal))
    {
        string[] pieces = command.Split(':');
        Key held = Enum.Parse<Key>(pieces[1]);
        c64.KeyboardKeyDown(held);
        Run(int.Parse(pieces[2]));
        c64.KeyboardKeyUp(held);
        runCycles = 50_000;
    }
'''
    if marker not in program:
        raise RuntimeError("Cannot locate the expected frontend command dispatch")
    return program.replace(marker, addition + marker, 1)


def check(name: str, condition: bool, **observations) -> dict:
    return {"test": name, "status": "passed" if condition else "failed", **observations}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True,
                        help="External C64Emulator repository root")
    parser.add_argument("--dotnet", default="dotnet", help=".NET 10 SDK executable")
    parser.add_argument("--output", type=Path, default=PROJECT / "docs/classic")
    parser.add_argument("--allow-other-revision", action="store_true")
    args = parser.parse_args()
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    source = args.source.resolve()
    output = args.output.resolve()
    revision = run(["git", "-C", str(source), "rev-parse", "HEAD"], 30).stdout.strip()
    if revision != PINNED_REVISION and not args.allow_other_revision:
        parser.error(f"Expected external revision {PINNED_REVISION}; found {revision}")
    source_changes = run([
        "git", "-C", str(source), "diff", "HEAD", "--", "C64Emulator", "SharpPixels"
    ], 30).stdout
    if source_changes:
        parser.error("The external emulator source has tracked changes; use an unchanged checkout")
    profile = PROJECT / "profiles/classic/roms"
    required = ("c64-basic-kernal.bin", "c64-character.bin")
    if not all((profile / name).is_file() for name in required):
        parser.error("The unchanged classic profile has not been prepared")
    rom_hashes = {p.name: digest(p) for p in sorted(profile.glob("*.bin"))}
    game_path = PROJECT / "examples/rom_runner.prg"
    game = game_path.read_bytes()
    load_address = int.from_bytes(game[:2], "little")
    labels = symbols(PROJECT / "examples/rom_runner.map")
    payload = game[2:]
    results = []
    output.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()

    with tempfile.TemporaryDirectory(prefix="c64ux-classic-") as temporary:
        work = Path(temporary)
        roms = work / "roms"
        roms.mkdir()
        for path in profile.glob("*.bin"):
            shutil.copy2(path, roms / path.name)
        disk = work / "play-games.d64"
        shutil.copy2(PROJECT / "examples/play-games.d64", disk)
        harness = work / "harness"
        harness.mkdir()
        for name in ("EmulatorHarness.csproj", "Stubs.cs"):
            shutil.copy2(HERE / name, harness / name)
        program = classic_frontend((HERE / "Program.cs").read_text(encoding="utf-8"))
        (harness / "Program.cs").write_text(program, encoding="utf-8")
        build = work / "build"
        print("Building the unchanged external emulator core for the classic profile...", flush=True)
        run([dotnet, "build", str(harness / "EmulatorHarness.csproj"), "-c", "Release",
             "-o", str(build), f"-p:EmulatorSourceRoot={source}",
             f"-p:BaseIntermediateOutputPath={work / 'obj'}/"], 180)
        commands = [
            "keys:PRINT 1+1",
            f"mount8:{disk}",
            'run:6000000:LOAD "ROMRUNNER",8,1',
            "keys:RUN",
            "classic-hold:Space:60000",
            "classic-wait:1100000",
            "classic-hold:D:150000",
            "classic-hold:A:150000",
            "classic-hold:Q:60000",
            "keys:PRINT 2+2",
        ]
        command_file = work / "commands.json"
        command_file.write_text(json.dumps(commands), encoding="utf-8")
        raw = work / "raw"
        print("Testing BASIC, IEC LOAD, RUN, game start, score and physical movement...", flush=True)
        run([dotnet, str(build / "EmulatorHarness.dll"), str(roms), str(raw),
             str(command_file), "--raw-petscii"], 180)
        snapshots = json.loads((raw / "results.json").read_text(encoding="utf-8"))
        if len(snapshots) != len(commands) + 1:
            raise RuntimeError("The emulator did not complete every classic-profile action")

        def ram(index: int) -> bytes:
            return (raw / f"command-{index:02}-ram.bin").read_bytes()

        def state(index: int) -> dict:
            data = ram(index)
            return {
                "state": data[labels["GAME_STATE"]],
                "player_x": data[labels["PLAYER_X"]],
                "score": data[labels["SCORE_HUNDREDS"]] * 100
                         + data[labels["SCORE_TENS"]] * 10
                         + data[labels["SCORE_ONES"]],
                "lives": data[labels["LIVES"]],
                "fall_delay": data[labels["FALL_DELAY"]],
            }

        results.append(check("Cold boot enters the supplied OPEN BASIC",
            "OPEN BASIC 64" in snapshots[0]["screen"] and "READY." in snapshots[0]["screen"]))
        results.append(check("Physical keyboard PRINT 1+1 produces 2",
            re.search(r"PRINT 1\+1\s+2\s+READY\.", snapshots[1]["screen"]) is not None,
            input="Physical CIA matrix"))
        results.append(check("D64 mounts in device 8",
            "DISK MOUNTED" in snapshots[2]["transcript"],
            output=snapshots[2]["transcript"].strip()))
        loaded = ram(3)[load_address:load_address + len(payload)]
        results.append(check("LOAD ROMRUNNER,8,1 transfers the exact PRG over IEC",
            loaded == payload and "?" not in snapshots[3]["screen"],
            requested_command='LOAD "ROMRUNNER",8,1',
            input="Explicit PETSCII queue for the quoted command",
            load_address=f"${load_address:04X}", bytes=len(payload),
            expected_payload_sha256=hashlib.sha256(payload).hexdigest(),
            observed_ram_sha256=hashlib.sha256(loaded).hexdigest()))
        title = state(4)
        results.append(check("Physical RUN executes the ordinary BASIC SYS bootstrap",
            "R O M  R U N N E R" in snapshots[4]["screen"] and title["state"] == 0,
            sys_entry=f"${labels['ENTRY']:04X}", observation=title))
        playing = state(5)
        results.append(check("Physical SPACE starts the game",
            playing["state"] == 1 and playing["lives"] == 3,
            observation=playing))
        scored = state(6)
        results.append(check("The running 6510 game catches its first star and updates score",
            scored["state"] == 1 and scored["score"] == 1
            and "SCORE 001" in snapshots[6]["screen"], observation=scored))
        right, left = state(7), state(8)
        results.append(check("Held physical D moves the visible player right",
            right["state"] == 1 and right["player_x"] > scored["player_x"],
            before=scored["player_x"], after=right["player_x"]))
        results.append(check("Held physical A moves the visible player left",
            left["state"] == 1 and left["player_x"] < right["player_x"],
            before=right["player_x"], after=left["player_x"]))
        results.append(check("Physical Q returns through RTS to BASIC",
            "READY." in snapshots[9]["transcript"],
            pc=snapshots[9]["pc"], output=snapshots[9]["transcript"].strip()))
        results.append(check("Physical PRINT 2+2 still produces 4 after the game",
            "PRINT 2+2" in snapshots[10]["screen"]
            and re.fullmatch(r"\s*4\s+READY\.\s*", snapshots[10]["transcript"]) is not None,
            output=snapshots[10]["transcript"].strip()))

        # A separate cold boot records the classic keyboard's shifted-quote
        # behavior instead of silently claiming the queued LOAD was physical.
        quote_commands = [f"mount8:{disk}", 'keys:LOAD "ROMRUNNER",8,1', "classic-wait:6000000"]
        quote_file = work / "quotes.json"
        quote_file.write_text(json.dumps(quote_commands), encoding="utf-8")
        quote_raw = work / "quotes"
        print("Recording the unchanged classic keyboard's quoted-command behavior...", flush=True)
        run([dotnet, str(build / "EmulatorHarness.dll"), str(roms), str(quote_raw),
             str(quote_file), "--raw-petscii"], 180)
        quote_snapshots = json.loads((quote_raw / "results.json").read_text(encoding="utf-8"))
        quote_screen = quote_snapshots[-1]["screen"]
        quote_line = next((line for line in quote_screen.splitlines() if line.startswith("LOAD ")), "")
        quote_loaded = (quote_raw / "command-03-ram.bin").read_bytes()[load_address:load_address + len(payload)]
        if quote_loaded == payload:
            quote_status = "passed"
            quote_detail = "The wholly physical quoted LOAD also transferred the exact PRG."
        elif (quote_line in ("LOAD ROMRUNNER,8,1", "LOAD 2ROMRUNNER,8,1",
                             "LOAD ROMRUNNER2,8,1", "LOAD 2ROMRUNNER2,8,1")
              and "?SYNTAX ERROR" in quote_screen):
            quote_status = "limited"
            quote_detail = (
                "With this unchanged classic keyboard routine and the harness's physical matrix path, "
                "Shift+quote does not reliably enter the required quotation marks. "
                f"The observed command {quote_line!r} reports ?SYNTAX ERROR. "
                "The successful game load above therefore uses an explicitly queued PETSCII command. "
                "Its IEC transfer and subsequent RUN/game controls still execute in the real core."
            )
        else:
            quote_status = "failed"
            quote_detail = "Unexpected physical quoted-LOAD result; inspect keyboard-limitation.txt."
        results.append({"test": "Wholly physical quoted LOAD behavior is accounted for",
                        "status": quote_status, "observed_command": quote_line, "detail": quote_detail})
        for index, filename in ((1, "basic"), (4, "game-title"), (6, "gameplay"),
                                (7, "movement"), (10, "return-basic")):
            png_from_ppm(raw / f"command-{index:02}.ppm", output / f"{filename}.png")
        png_from_ppm(quote_raw / "command-03.ppm", output / "keyboard-limitation.png")
        (output / "keyboard-limitation.txt").write_text(quote_screen, encoding="utf-8")
        text = []
        for snapshot in snapshots:
            command = snapshot["command"] or "BOOT"
            if command.startswith("mount8:"):
                command = "mount8:play-games.d64"
            text.append(f"### {command}\nCPU PC=${snapshot['pc']}  jiffies={snapshot['jiffies']}\n"
                        f"{snapshot['screen'].rstrip()}\n")
        text.append("### Separate physical quoted-LOAD probe\n" + quote_detail + "\n" + quote_screen)
        (output / "transcript.txt").write_text("\n".join(text), encoding="utf-8")
        observations = []
        for snapshot in snapshots:
            command = snapshot["command"] or "BOOT"
            if command.startswith("mount8:"):
                command = "mount8:play-games.d64"
            observations.append({key: snapshot[key] for key in ("pc", "sp", "state", "jiffies")}
                                | {"command": command})
        report = {
            "suite": "Unchanged classic ROM profile on the actual C64Emulator core",
            "emulator_repository": "https://github.com/n1k0m0/C64Emulator",
            "emulator_commit": revision,
            "external_source_tracked_changes": False,
            "profile": "Classic Open BASIC / Open KERNAL",
            "profile_origin": "Unchanged user-supplied development ROMs",
            "rom_sha256": rom_hashes,
            "game_prg_sha256": digest(game_path),
            "game_map_sha256": digest(PROJECT / "examples/rom_runner.map"),
            "disk_sha256": digest(disk),
            "temporary_frontend_sha256": hashlib.sha256(program.encode("utf-8")).hexdigest(),
            "cpu_cia_vic_sid_iec_source_modified": False,
            "load_shortcut": False,
            "kernal_iec_hooks": False,
            "automatic_host_input_injection": False,
            "explicit_petscii_input": 'Only LOAD "ROMRUNNER",8,1 uses an explicit queue',
            "physical_matrix_input": "PRINT, RUN, SPACE, held A/D, Q and the separate quote probe",
            "host_audio_sink_only_is_replaced": True,
            "audible_sound_verified": False,
            "success": not any(item["status"] == "failed" for item in results),
            "scope": (
                "This verifies the listed BASIC arithmetic and the bundled original ROM RUNNER game "
                "under these exact ROM/core versions. It does not establish broad C64 game, BASIC, "
                "fastloader, tape or cartridge compatibility."
            ),
            "physical_quoted_load": quote_detail,
            "seconds": round(time.perf_counter() - started, 3),
            "observations": observations,
            "results": results,
        }
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    # Catch accidental writes to the provided profile, not just copied fixtures.
    if rom_hashes != {p.name: digest(p) for p in sorted(profile.glob("*.bin"))}:
        raise RuntimeError("The original classic profile changed during verification")
    for result in results:
        print(f"{result['status'].upper():8} {result['test']}")
    print(f"Report: {output / 'report.json'}")
    raise SystemExit(0 if report["success"] else 1)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.TimeoutExpired) as exc:
        print(f"Classic integration test could not complete: {exc}", file=sys.stderr)
        raise SystemExit(2)
