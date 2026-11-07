# Native emulator integration tests — RC3

The harness compiles the real CPU, VIC, CIA, SID, drive, IEC and media sources
from [n1k0m0/C64Emulator](https://github.com/n1k0m0/C64Emulator), commit
`f01750e17a2c3f4cabfd57dc0c470dac93a91a23`. `NullAudio.cs` replaces only host
audio output. No CPU instruction, KERNAL entry, tape pulse primitive or serial
byte routine is intercepted. The actual call path is observed at instruction
fetch boundaries, so VIC stalls do not count a character or API entry twice.

## Build and execute

Use .NET 10 and the matching original emulator checkout:

```sh
dotnet build tests/emulator/RomIntegration.csproj -c Release \
  -p:EmulatorSource=/absolute/path/to/C64Emulator \
  -p:EmulatorPatchDirectory=/absolute/path/to/rom-project/emulator-patches/overrides \
  -o build/native-tests

dotnet build/native-tests/RomIntegration.dll build/emulator-roms \
  --model pal --suite all --json results/native-pal.json

dotnet build/native-tests/RomIntegration.dll build/emulator-roms \
  --model ntsc --suite machine --json results/native-ntsc.json
```

For the complete RC3 behavior use the supplied override directory. Omitting it
is useful only when investigating the original host; unsupported feature cases
report a missing patch instead of establishing RC3 compatibility.
The standalone Windows frontend is compiled separately by the original
Windows project. This harness validates its public system APIs and keyboard
mapping, not an interactive WinForms/OpenGL desktop session.

## Suites and report groups

Each process checks its selected model's boot once. A positional BASIC command
can replace a suite. `--json` saves the complete report, including loaded ROM
hashes, model clocks, feature flags, named groups and individual results.

| Suite | Execution |
| --- | --- |
| `video` | Direct VIC/CIA/SID/core contracts plus native raster/clock/pixel behavior and model savestate checks; includes actual sprite collision regression |
| `basic` | BASIC expressions, programs, errors, SYS and real CIA1 keyboard typing |
| `control` | Tab RUN/STOP, CONT, PageUp RESTORE, STOP+RESTORE and NMI edge behavior |
| `editor` | Original editor regressions plus relocated screen, VIC cursor pixels, LIST retokenization and IEC bank preservation |
| `iec` | Native PRG LOAD/SAVE/RUN, channels/EOF and program LOAD chaining |
| `channel` | Native LOAD and GET#/INPUT#/EOI subset without SAVE/chaining |
| `data` | Native SEQ/USR create, append, binary readback, status and replacement |
| `dos` | Native REL binary positioning/errors, file commands, counted/raw direct buffers, memory alias, actual uploaded 1541 CPU code and allocation |
| `tape` | Three format/transport component checks and 18 CPU/CIA waveform assertions |
| `tape-format` | Only the three TAP/container/transport component checks |
| `serial` | 21 native physical-pin RS232 scenarios with an independent timed peer |
| `tape-host` | 12 public host API checks: input validation, clock conversion, mount/save/rollback, transport, resets and peripheral snapshots |
| `media` | 14 direct D64 PRG allocation/persistence checks |
| `media-data` | 52 direct D64 SEQ/USR creation/append/rollback checks |
| `dos-media` | 82 direct DOS/REL/buffer/IEC-state-machine checks |
| `machine` | All model-dependent groups above, including tape/serial/host lifecycle, excluding the three model-independent D64 media groups |
| `all` | Every group, with one shared initial boot |

The `Group` field distinguishes direct host/component assertions from ROM-driven
ones. Direct D64 API calls are not claimed as native CPU/IEC execution. The exact
final aggregate counts and unchanged source hashes are in `../../docs/VALIDATION.md`
and `../../results/release-verification.json`.

## Native call paths

`EnableLoadHack=false` and `EnableKernalIecHooks=false`. BASIC enters the real
KERNAL and transmits via CIA2 IEC pins. The software 1541 handles the other end;
the supplied free 1541 ROM provides bootstrap and drive-CPU helper addresses.
File writes must survive independent reopening of the host D64, with exact
linked-chain bytes and allocation metadata. READY alone is not a success test.

TAP playback drives actual CIA1 FLAG transitions and timers. SAVE records actual
processor-port WRITE edge intervals. The test independently generates a standard
C2N tape and independently decodes captured SAVE output; it does not validate
only a writer against its own inverse. All waveform operations start with the
screen and all eight sprites enabled. Success and error paths check restored
readable display-control bits, sprite enables, motor and caller IRQ state.

Serial cases install small generated RAM callers for public KERNAL entries.
An independent peer samples TX at nominal baud and supplies RX/control pins
at model-clock times. It deliberately delays or withholds handshake responses;
timeouts, line state, status acknowledgement and frame errors are checked.
The peer object/callback lifecycle is also checked across machine savestates.

## Reproducible tape artifacts

Set `FREE_ROM_TAPE_ARTIFACTS` to a different directory for each model before
running `--suite tape`, `machine` or `all`. Four generated files are written:

- `rom-save-with-eot.tap`: actual measured ROM SAVE waveform.
- `independent-c2n-pal.tap`: independently generated standard tape.
- `independent-first-data-checksum-bad.tap`: first data copy is damaged; duplicate recovers.
- `independent-type5-eot.tap`: an EOT header without a program.

`FREE_ROM_TAP_TRACE` optionally writes the captured SAVE to a chosen filename.
The release includes the artifacts from its final PAL and NTSC runs. All other
media fixtures are temporary generated files and are removed after each group.
No user disk image or proprietary ROM is a test input.

Failures produce process status1 and include recent actual instruction fetches.
The final output is evidence for the exact ROM hashes/model/options it records;
it is not a claim of every private firmware entry or physical peripheral.
