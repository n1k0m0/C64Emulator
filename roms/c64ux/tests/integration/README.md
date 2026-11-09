# Tests with the user's public C64Emulator core

This harness compiles the existing emulator's CPU, CIA, VIC, memory, SID and
IEC code unchanged and boots the delivered ROMs. Only the host sound output
is replaced by a sink that discards audio. It requires the .NET 10 SDK.

The independently checked source revision is
`f01750e17a2c3f4cabfd57dc0c470dac93a91a23` of
<https://github.com/n1k0m0/C64Emulator>. The uploaded ROM project was made with
a newer, unpublished local emulator; that source was not supplied. Its APIs
and disk behavior may differ from this public revision.

## Run the acceptance suite

Clone the public source in a separate directory. The emulator source and SDK
are not included in this ROM package.

```sh
git clone https://github.com/n1k0m0/C64Emulator.git /path/to/C64Emulator
git -C /path/to/C64Emulator checkout f01750e17a2c3f4cabfd57dc0c470dac93a91a23
python tests/integration/verify.py --source /path/to/C64Emulator --output integration-results
```

Use `--dotnet /path/to/dotnet` if the SDK is not on `PATH`. The runner builds
the harness in a temporary directory, executes the actual emulator, asserts
the observed command results and saves a JSON report, a text transcript, and
PNG images rendered by the actual VIC implementation. It does not patch ROM
instructions or replace KERNAL calls with host implementations.

The runner records the external source revision. A different revision is
rejected unless `--allow-other-revision` is supplied; this does not establish
compatibility if that revision fails to compile or execute the suite.

For the current C64Emulator source, pass `--allow-other-revision`. The harness
selects the supplied ROM directory explicitly, so existing original ROMs in
the user's AppData directory do not replace the C64/UX test images.

## Run the game acceptance suite

```sh
python tests/play_fixtures.py
python tests/test_play.py --json docs/play-contract.json
python tests/integration/verify_play.py --source /path/to/C64Emulator --output docs/play-integration
```

The first command regenerates the independent PRG/D64 fixtures. The second
executes the actual ROM on an instruction emulator with controlled public
KERNAL file endpoints. It checks original load addresses, RAM under BASIC,
the game entry state, malformed SYS stubs, address bounds, two-pass changes,
I/O failures, RAM filesystem preservation and reset after destructive errors.
That test uses explicit file-API doubles and does not establish IEC behavior.

The third command uses the actual external emulator for 36 command/input
scenarios in three isolated runs. It checks normal IEC loading of independent
PRGs, then plays the bundled original ROM RUNNER and the unchanged upstream
SERPENT64 v1.0.4 release. Physical SPACE/A/D/W/Q inputs are checked against
read-only game state observations and actual VIC frames: movement, scoring,
pause, title return, and recovery of the terminal. The report records the
ROM and asset hashes and the source of the third-party game state addresses.
Because SERPENT64 places apples randomly, one additional test action chooses
a short route using read-only screen observations, then steers with ordinary
physical WASD key events. It never writes game memory or replaces game code.

The upstream game defaults to `third_party/serpent64/serpent64.d64`; use
`--serpent-disk /path/to/serpent64.d64` to select an identical release copy
with its `serpent64.prg` beside it. This suite gives each run a private disk
copy. The two-pass IEC load receives a generous cycle budget, so the full
suite takes a few minutes. Sound synthesis runs normally, but the tests only
inspect SID state; they do not evaluate audible output. These scenarios
establish compatibility for the exercised games on the recorded emulator
revision, not for all C64 software or physical machines.

## Run the harness directly

```sh
dotnet build tests/integration/EmulatorHarness.csproj -c Release -p:EmulatorSourceRoot=/path/to/C64Emulator
dotnet tests/integration/bin/Release/net10.0/EmulatorHarness.dll /absolute/path/to/roms /absolute/path/to/results commands.json
```

`EmulatorSourceRoot` must point at the repository root, containing both
`C64Emulator` and `SharpPixels` directories. `commands.json` is an array of
ASCII command strings. Input is encoded into the shell's PETSCII character
set. For an original BASIC baseline, append `--raw-petscii`.

The supported command prefixes are:

- `keys:pwd`: enter text through the physical CIA keyboard matrix. Letter
  case, shell punctuation and Shift combinations are supported; `\u0003`
  sends Ctrl+C.
- `mount8:/absolute/path/demo.d64`: attach a D64 image to device 8.
- `run:5000000:dir`: submit a command, then execute the specified cycle budget.
- `warm-reset:`: call the emulator's warm-reset facility.
- `cold-reset:`: reset all emulator components and run the cold boot sequence.
- `wait:800000`: execute cycles without adding any input.
- `hold:200000:D`: hold the named host key through the physical keyboard
  matrix for exactly this many cycles, then release it. There is no implicit
  Return or additional idle period; use `wait:` when a settling period is needed.
- `serpent-food:8000000`: in the unchanged SERPENT64 v1.0.4 game, steer to a
  reachable apple with physical WASD input within the supplied cycle budget.
  The route follows read-only screen and game-state observations and adapts
  to the game's randomly placed food. This is a test-frontend action only.

Ordinary commands run for 800,000 cycles; initial boot runs for 1,200,000.
Physical keys stay down for 35,000 cycles and up for 25,000. The harness
disables the emulator's LOAD shortcut, KERNAL IEC hooks and automatic input
injection. Explicit queue input remains available in addition to matrix keys.

The raw harness intentionally saves debug RAM dumps and PPM frames. The
acceptance runner keeps those in a temporary directory and retains only its
compact report, transcript and selected PNGs. CPU JAM is a hard failure.

## ROM lookup

The public emulator expects `c64-basic-kernal.bin` (BASIC-slot image followed
by KERNAL-slot image), `c64-character.bin`, and optional 8 KiB drive halves
named `1541-c000-rom.bin` and `1541-e000-rom.bin`. The harness rejects a run
if it discovers that the emulator resolved CPU/character ROMs from another
directory. This prevents an AppData ROM installation from silently replacing
the intended fixture.

Existing BASIC auto-load/autostart commands should be disabled for this shell
ROM. Mount media separately and use `play` for a supported game PRG, or the
shell's `load` and `run` commands for a native shell application.
