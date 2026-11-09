"""Exercise PLAY and two real games on the unchanged external C64Emulator core.

SPDX-License-Identifier: Apache-2.0

The D64 images are read through the actual CIA/IEC implementation. Neither
KERNAL calls nor game instructions are replaced. Gameplay input is delivered
through the emulator's physical keyboard matrix API. Observations of game
RAM and the video framebuffer are read-only.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
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


PROMPT = "root@c64:/home/root# "
SERPENT_COMMIT = "d45412cef4242f97d505f8913509c5542939b768"


def commands(fixture_disk: Path, runner_disk: Path, serpent_disk: Path) -> dict[str, list[str]]:
    return {
        "fixtures": [
            f"mount8:{fixture_disk}", "echo keep-me > keeper",
            "run:70000000:play SYSDEMO", "cat keeper",
            "run:6000000:play DIRECT", "run:6000000:play UNDERROM",
            "run:6000000:play EXPLICIT $0840", "run:6000000:play TOPRAM",
            "echo boundary-safe > keeper", "run:6000000:play CROSSIO",
            "cat keeper", "run:6000000:play BASIC", "cat keeper",
            "echo play-fixtures-complete",
        ],
        "runner": [
            f"mount8:{runner_disk}", "run:30000000:play ROMRUNNER",
            "hold:50000:Space", "wait:1100000", "hold:200000:D",
            "hold:200000:A", "hold:50000:Q", "wait:800000",
            "echo runner-returned",
        ],
        "serpent": [
            f"mount8:{serpent_disk}", 'run:140000000:play "SERPENT 64"',
            "hold:50000:Space", "wait:800000", "hold:400000:W",
            "hold:400000:A", "serpent-food:8000000", "hold:50000:Space", "wait:200000",
            "hold:50000:Q", "wait:500000", "cold-reset:",
            "echo serpent-reset-complete",
        ],
    }


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def rom_hashes() -> dict[str, str]:
    return {path.name: digest(path) for path in sorted((PROJECT / "roms").glob("*.bin"))}


def normalized_command(command: str | None) -> str:
    if command and command.startswith("mount8:"):
        return "mount8:" + Path(command[7:]).name
    return command or "BOOT"


def evaluate(work: Path, output: Path, revision: str, hashes: dict[str, str],
             assets: dict[str, Path], elapsed: float) -> dict:
    """Evaluate actual captured states and save compact, reviewable evidence."""
    output.mkdir(parents=True, exist_ok=True)
    raw = {name: work / name for name in ("fixtures", "runner", "serpent")}
    snapshots = {
        name: json.loads((directory / "results.json").read_text(encoding="utf-8"))
        for name, directory in raw.items()
    }
    expected_counts = {"fixtures": 15, "runner": 10, "serpent": 14}
    for name, count in expected_counts.items():
        if len(snapshots[name]) != count:
            raise RuntimeError(f"Incomplete {name} run: {len(snapshots[name])}/{count} snapshots")
    results = []

    def check(name: str, passed: bool, **evidence):
        results.append({"test": name, "status": "passed" if passed else "failed", **evidence})

    def ram(name: str, index: int) -> bytes:
        return (raw[name] / f"command-{index:02}-ram.bin").read_bytes()

    def saved_frame(name: str, index: int, filename: str):
        png_from_ppm(raw[name] / f"command-{index:02}.ppm", output / filename)

    check("All three runs cold boot the supplied ROMs",
          all("C64/UX 0.2.0" in rows[0]["transcript"] and PROMPT in rows[0]["transcript"]
              for rows in snapshots.values()))
    check("No CPU JAM during any scenario",
          all(row["state"] != "Jammed" for rows in snapshots.values() for row in rows))
    check("Firmware images remain unchanged throughout execution", rom_hashes() == hashes)

    rows = snapshots["fixtures"]
    for index, name, message in (
        (3, "6 KiB BASIC SYS PRG overwrites shell workspace and executes", "PLAY SYS OK"),
        (5, "Machine code executes at its original $2000 address", "PLAY DIRECT OK"),
        (6, "Machine code loads and executes in RAM under BASIC at $A000", "PLAY ROM RAM OK"),
        (7, "Explicit $0840 entry executes a PRG without a BASIC stub", "PLAY EXPLICIT OK"),
    ):
        transcript = rows[index]["transcript"]
        check(name, message in transcript and "C64/UX 0.2.0" in transcript and PROMPT in transcript,
              transcript=transcript, execution_marker=ram("fixtures", index)[0xCFF0])
    check("Successful game launch releases the prior RAM filesystem",
          "error: path missing" in rows[4]["transcript"] and "\nkeep-me\n" not in rows[4]["transcript"])
    check("RTS at the last supported RAM byte $CFFF returns through cold boot",
          "Starting game." in rows[8]["transcript"] and "C64/UX 0.2.0" in rows[8]["transcript"]
          and PROMPT in rows[8]["transcript"] and ram("fixtures", 8)[0xCFFF] == 0x60)
    check("A PRG crossing into $D000 is rejected and keeps the RAM file",
          "play: PRG must fit" in rows[10]["transcript"]
          and "\nboundary-safe\n" in rows[11]["transcript"]
          and ram("fixtures", 10)[0xCFFF] == 0x60)
    check("A BASIC-only program is rejected and keeps the RAM file",
          "no simple SYS" in rows[12]["transcript"]
          and "\nboundary-safe\n" in rows[13]["transcript"])
    check("The terminal still accepts commands after the fixture scenarios",
          "\nplay-fixtures-complete\n" in rows[14]["transcript"])

    # Symbol addresses come from the independently assembled original game.
    symbols = {
        name: int(address, 16)
        for address, name in re.findall(r"^([0-9A-F]{4}) (\w+)$",
                                       assets["rom_runner.map"].read_text(), re.MULTILINE)
    }
    observed_runner = {}
    rows = snapshots["runner"]
    for index in (2, 3, 4, 5, 6):
        memory = ram("runner", index)
        observed_runner[index] = {name.lower(): memory[symbols[name]]
                                  for name in ("GAME_STATE", "PLAYER_X", "SCORE_ONES", "LIVES")}
    title, started, scored, right, left = (observed_runner[i] for i in (2, 3, 4, 5, 6))
    check("ROM RUNNER reaches its title through the actual IEC loader",
          title["game_state"] == 0 and "R O M  R U N N E R" in rows[2]["screen"]
          and "Starting game." in rows[2]["transcript"], observed=title)
    check("Physical SPACE starts ROM RUNNER",
          started["game_state"] == 1 and started["score_ones"] == 0, observed=started)
    check("ROM RUNNER catches a star and earns a point with three lives",
          scored["game_state"] == 1 and scored["score_ones"] == 1 and scored["lives"] == 3,
          observed=scored, sid_debug=rows[4]["sid"])
    check("Physical D and A move the ROM RUNNER player in both directions",
          right["player_x"] > scored["player_x"] and left["player_x"] < right["player_x"],
          positions=[scored["player_x"], right["player_x"], left["player_x"]])
    check("Q returns from ROM RUNNER to a fresh, working terminal",
          "C64/UX 0.2.0" in rows[8]["transcript"] and PROMPT in rows[8]["transcript"]
          and "\nrunner-returned\n" in rows[9]["transcript"])
    for index, filename in ((2, "rom-runner-title.png"), (4, "rom-runner-gameplay.png"),
                            (5, "rom-runner-move-right.png"), (8, "rom-runner-return.png")):
        saved_frame("runner", index, filename)

    # Read-only observations from SERPENT64 v1.0.4 constants.asm. The shipped
    # upstream release PRG is executed unchanged, with no TEST_* input build.
    serpent_addresses = {"state": 0x12, "head_x": 0x13, "head_y": 0x14,
                         "direction": 0x17, "lives": 0x29, "level": 0x2A, "food_eaten": 0x2D}
    observed_serpent = {
        index: {name: ram("serpent", index)[address] for name, address in serpent_addresses.items()}
        for index in (2, 3, 4, 5, 6, 7, 8, 9, 11)
    }
    rows = snapshots["serpent"]
    title, game, up, left, scored, pause, later_pause, back = (
        observed_serpent[i] for i in (2, 4, 5, 6, 7, 8, 9, 11))
    check("Unmodified SERPENT64 v1.0.4 reaches its title through IEC",
          title["state"] == 0 and "S E R P E N T   6 4" in rows[2]["screen"]
          and "PRESS FIRE OR SPACE TO PLAY" in rows[2]["screen"], observed=title)
    check("Physical SPACE starts the third-party game",
          observed_serpent[3]["state"] == 2 and game["state"] == 3
          and game["direction"] == 1 and game["lives"] == 3, observed=game)
    check("Physical W steers the third-party snake upwards",
          up["state"] == 3 and up["direction"] == 0 and up["head_y"] < game["head_y"]
          and up["head_x"] == game["head_x"], observed=up)
    check("Physical A steers the third-party snake left",
          left["state"] == 3 and left["direction"] == 3 and left["head_x"] < up["head_x"]
          and left["head_y"] == up["head_y"] and left["lives"] == 3, observed=left)
    check("A physical WASD route to the observed apple earns points",
          scored["state"] == 3 and scored["food_eaten"] > left["food_eaten"]
          and scored["lives"] == 3 and bool(re.search(r"SCORE 0000[1-9]0", rows[7]["screen"])),
          observed=scored, route=rows[7]["transcript"], sid_debug=rows[7]["sid"])
    check("SPACE pauses SERPENT64 and its position remains fixed",
          pause["state"] == later_pause["state"] == 9
          and (pause["head_x"], pause["head_y"]) == (later_pause["head_x"], later_pause["head_y"])
          and "PAUSED" in rows[9]["screen"], observed=later_pause)
    check("Q leaves SERPENT64's pause screen for its title",
          back["state"] == 0 and "PRESS FIRE OR SPACE TO PLAY" in rows[11]["screen"])
    check("Emulator cold reset restores a usable terminal after SERPENT64",
          "C64/UX 0.2.0" in rows[12]["transcript"] and PROMPT in rows[12]["transcript"]
          and "\nserpent-reset-complete\n" in rows[13]["transcript"])
    for index, filename in ((2, "serpent64-title.png"), (4, "serpent64-gameplay.png"),
                            (5, "serpent64-up.png"), (6, "serpent64-left.png"),
                            (7, "serpent64-score.png"), (9, "serpent64-pause.png"),
                            (11, "serpent64-title-return.png"), (12, "serpent64-reset.png")):
        saved_frame("serpent", index, filename)

    compact_snapshots = {}
    transcript = []
    for name, rows in snapshots.items():
        compact_snapshots[name] = []
        for row in rows:
            normalized = {**row, "command": normalized_command(row["command"])}
            compact_snapshots[name].append(normalized)
            transcript.append(f"### {name}: {normalized['command']}\n{row['transcript']}\n")
    (output / "snapshots.json").write_text(json.dumps(compact_snapshots, indent=2) + "\n", encoding="utf-8")
    (output / "transcript.txt").write_text("\n".join(transcript), encoding="utf-8")
    report = {
        "suite": "Actual C64Emulator PLAY and game integration",
        "emulator_repository": "https://github.com/n1k0m0/C64Emulator",
        "emulator_commit": revision,
        "pinned_revision": revision == PINNED_REVISION,
        "firmware_execution": "Unchanged ROM instructions on the external CPU, VIC, SID, CIA and IEC core",
        "load_shortcut": False, "kernal_iec_hooks": False, "automatic_host_input_injection": False,
        "game_input": "Physical keyboard matrix via KeyboardKeyDown/KeyboardKeyUp; apple route chosen from read-only game/screen observations",
        "host_audio_sink_only_is_replaced": True,
        "sound_scope": "SID synthesis is executed and register/envelope state captured; audible output is not evaluated",
        "game_observation_source": f"https://github.com/AdvancingDevelopment/c64snake/blob/{SERPENT_COMMIT}/src/constants.asm",
        "serpent64_version": "1.0.4", "serpent64_upstream_commit": SERPENT_COMMIT,
        "serpent64_prg_changed_for_tests": False,
        "coverage_limits": [
            "Proves these scenarios on the pinned emulator revision, not all C64 games or physical hardware.",
            "SERPENT64 was exercised through its title, first apple, steering, pause and title return; all levels and high-score persistence were not tested.",
            "Original Commodore BASIC/KERNAL private routines, cartridges, tape and custom fast loaders are outside this proof.",
            "Mid-read fault injection is covered separately by controlled public-file-API instruction tests, not this normal IEC run.",
        ],
        "scenarios": sum(len(rows) - 1 for rows in snapshots.values()),
        "checks": len(results), "seconds": round(elapsed, 3),
        "rom_sha256": hashes, "asset_sha256": {name: digest(path) for name, path in assets.items()},
        "rom_runner_observations": observed_runner, "serpent64_observations": observed_serpent,
        "results": results, "success": all(item["status"] == "passed" for item in results),
    }
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet", help=".NET 10 SDK executable")
    parser.add_argument("--output", type=Path, default=PROJECT / "docs/play-integration")
    parser.add_argument("--serpent-disk", type=Path, default=PROJECT / "third_party/serpent64/serpent64.d64")
    parser.add_argument("--allow-other-revision", action="store_true")
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    revision = run(["git", "-C", str(source), "rev-parse", "HEAD"], 30).stdout.strip()
    if revision != PINNED_REVISION and not args.allow_other_revision:
        parser.error(f"Expected external revision {PINNED_REVISION}; found {revision}")
    assets = {
        "play-fixtures.d64": PROJECT / "examples/play-fixtures/play-fixtures.d64",
        "play-games.d64": PROJECT / "examples/play-games.d64",
        "rom_runner.prg": PROJECT / "examples/rom_runner.prg",
        "rom_runner.map": PROJECT / "examples/rom_runner.map",
        "serpent64.d64": args.serpent_disk.resolve(),
        "serpent64.prg": args.serpent_disk.resolve().with_suffix(".prg"),
    }
    for path in assets.values():
        if not path.is_file():
            parser.error(f"Missing game fixture: {path}")
    started = time.perf_counter()
    hashes = rom_hashes()
    with tempfile.TemporaryDirectory(prefix="c64ux-play-integration-") as temporary:
        work = Path(temporary)
        roms = work / "roms"
        roms.mkdir()
        for path in (PROJECT / "roms").glob("*.bin"):
            shutil.copy2(path, roms / path.name)
        # Each run receives an isolated disk image, including the upstream game
        # which can write its own high-score table during longer play sessions.
        fixture_disk, runner_disk, serpent_disk = (work / name for name in ("fixtures.d64", "runner.d64", "serpent64.d64"))
        for src, dst in ((assets["play-fixtures.d64"], fixture_disk), (assets["play-games.d64"], runner_disk),
                         (assets["serpent64.d64"], serpent_disk)):
            shutil.copy2(src, dst)
        selections = commands(fixture_disk, runner_disk, serpent_disk)
        print("Building the unchanged external emulator core...", flush=True)
        build = work / "build"
        run([dotnet, "build", str(HERE / "EmulatorHarness.csproj"), "-c", "Release", "-o", str(build),
             f"-p:EmulatorSourceRoot={source}", f"-p:BaseIntermediateOutputPath={work / 'obj'}/"], 180)

        def exercise(name: str):
            input_path = work / f"{name}-commands.json"
            input_path.write_text(json.dumps(selections[name]), encoding="utf-8")
            return run([dotnet, str(build / "EmulatorHarness.dll"), str(roms), str(work / name), str(input_path)], 400)

        print("Running PRG fixtures, ROM RUNNER and unchanged SERPENT64 with physical game input...", flush=True)
        with ThreadPoolExecutor(max_workers=3) as executor:
            futures = {name: executor.submit(exercise, name) for name in selections}
            for name, future in futures.items():
                result = future.result()
                (work / f"{name}.log").write_text(result.stdout, encoding="utf-8")
        report = evaluate(work, output, revision, hashes, assets, time.perf_counter() - started)
    for item in report["results"]:
        print(f"{item['status'].upper():8} {item['test']}")
    print(f"Report: {output / 'report.json'}")
    raise SystemExit(0 if report["success"] else 1)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.TimeoutExpired) as exc:
        print(f"Game integration could not complete: {exc}", file=sys.stderr)
        raise SystemExit(2)
