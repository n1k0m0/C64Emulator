# Open C64 ROMs

## Current private KERNAL entry update

The shared KERNAL now provides independently written `$E518` screen/editor,
`$FDA3` I/O initialization and `$FD15` vector-restore behavior. The shared
keyboard scanner source is retained, and the IRQ vector at `$EA31` checks
the interrupt source. The KERNAL SHA256 is
`F7C4558B999EB48E1B8CCB28435DC8491A6C09A2F6D0CEF0F173C5E796C65F4F`;
the BASIC, DOS and Character images are unchanged. The isolated and rebuilt
shared four-ROM Headless verifiers pass. Bruce Lee reaches a later screen
after joystick fire in a host-RAM PRG test, while complete gameplay and
keyboard repeat remain unproven. The tests, backup, observed CPU-cycle and
frame-phase differences, and remaining limits are recorded in
[the private entry report](../artifacts/kernal-private-entries-integrated-v1/README.md).

## Earlier VIC interrupt-mask update

The integrated KERNAL screen initialization now clears the VIC interrupt
mask during public CINT. Four synthetic mask seeds match the executed
reference's resulting `$D01A` state. The isolated and shared KERNAL builds
are SHA256
`D5B321F805A4CF8B5B407670B2E92D60265A01C2E7AFDCA584625817908AE211`;
the other three ROM images remain unchanged. The exact shared four-ROM
Headless verification passed. Reproduction, regression gates, and scope are
documented in `artifacts/kernal-cint-vicmask-v1/README.md`.

## Keyboard-buffer update

The integrated KERNAL GETIN routine publishes its decremented `$C6` count
after shifting the existing keyboard buffer. This prevents a newly queued
character from being overwritten during the shift. The KERNAL build at that
integration was SHA256
`DADD35CBC518B508FC493354D885D928304C7C3E919CA6880700680F6FF9CF06`;
the other three ROM images remain unchanged. Reproduction and limitations are
documented in `artifacts/kernal-getin-atomic-v1/README.md`.

**Open Basic 64**, **Open Kernal 64**, **Open Character Rom 64**, and the
**Open 1541 DOS bootstrap** are
independently authored here under Apache 2.0. The build reads only the local
source files. It does not extract, patch, or include a Commodore, Microsoft, or
other third-party BASIC ROM. The public memory map, token values, and KERNAL
entry addresses are interface facts described in the
[Commodore 64 Programmer's Reference Guide](https://www.commodore.ca/commodore-manuals/commodore-64-programmers-reference-guide/).

## Build and run

From the repository root:

```text
python roms/build.py
dotnet build C64Emulator/C64Emulator.csproj -c Release -p:Platform=x64
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --verify-open-roms artifacts/open-roms artifacts/open-roms/verification.log
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-abi artifacts/open-roms artifacts/open-roms/abi-audit.log
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --fuzz-open-rom-float artifacts/open-roms artifacts/open-roms/float-fuzz.log
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-grid artifacts/open-roms artifacts/open-roms/math-grid.log
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/math-cases.log roms/math-cases.txt
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/math-compositions.log roms/math-compositions.txt 8000000
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/trig-boundary.log roms/trig-boundary-cases.txt 600000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/trig-stress.log roms/trig-stress-cases.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/trig-small-holdout.log roms/trig-small-holdout-cases.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/trig-small-boundary.log roms/trig-small-boundary-cases.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/cos-small-v3.log artifacts/agent-cos-small-v2/cos-small-holdout.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/cos-small-target.log roms/cos-small-target-cases.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/float-zero-payload.log roms/float-zero-payload-cases.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/zero-functions36.log roms/float-zero-functions-holdout.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/math-zero-input-v1/functions49.log roms/math-zero-input-functions.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/math-guard-boundary-pairs.log roms/math-guard-boundary-pairs.txt 2500000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/math-guard-input-pairs.log roms/math-guard-input-pairs.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/atn-series-v1/baseline80.log roms/atn-holdout-cases.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/cos-ultra-v4.log artifacts/agent-cos-phase-v4/ultra-holdout.txt 2000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/cos-phase-boundary.log roms/cos-phase-boundary-cases.txt 600000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/exp-range.log roms/exp-range-cases.txt 600000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/exp-stress.log roms/exp-stress-cases.txt 900000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/exp-extreme.log roms/exp-extreme-cases.txt 1000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/guard-compositions.log roms/guard-compositions.txt 8000000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/direct-cancellation-current.log roms/direct-cancellation-cases.txt 1800000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-math-cases artifacts/open-roms artifacts/open-roms/direct-cancellation-holdout-current.log roms/direct-cancellation-holdout.txt 1800000 2
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-rnd artifacts/open-roms artifacts/open-roms/rnd-sequences.log
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-rnd artifacts/open-roms artifacts/rnd-extended-v1/long128.log 128
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --audit-open-rom-rs232 artifacts/open-roms artifacts/open-roms/rs232-baud-audit.log
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --probe-open-rom-float artifacts/open-roms artifacts/open-roms/float-probe.log "EXP(1)"
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --capture-open-rom-boot artifacts/open-roms artifacts/open-roms/open-basic64-boot.png
dotnet run --project roms/Open1541Dos/tests/NativeJobTests.csproj -c Release -p:Platform=x64 -- artifacts/open-roms
dotnet run --project artifacts/native-read-job-v1/ReadJobAudit.csproj -c Release -p:Platform=x64 -- artifacts/open-roms
dotnet run --project artifacts/native-verify-job-v1/VerifyJobAudit.csproj -c Release -p:Platform=x64 -- artifacts/open-roms
dotnet run --project artifacts/native-write-rom-v1/EncodeGroupAudit.csproj -c Release -p:Platform=x64 -- artifacts/open-roms
dotnet run --project artifacts/native-write-commit-v1/WriteViaCommitAudit.csproj -c Release -p:Platform=x64 -- artifacts/open-roms
dotnet build artifacts/native-write-job-v2/WriteMatrixAudit.csproj -c Release -p:Platform=x64
dotnet artifacts/native-write-job-v2/bin/x64/Release/net10.0-windows7.0/WriteMatrixAudit.dll artifacts/open-roms
dotnet build artifacts/native-write-job-v2/WriteRouteAudit.csproj -c Release -p:Platform=x64
dotnet artifacts/native-write-job-v2/bin/x64/Release/net10.0-windows7.0/WriteRouteAudit.dll artifacts/open-roms
dotnet C64Emulator/bin/x64/Release/C64Emulator.dll --open-roms artifacts/open-roms
```

The builder writes exactly 8192-byte `open-basic64.bin` and
`open-kernal64.bin` images, a 4096-byte `open-character-rom64.bin`, and a
16384-byte `open-1541-dos.bin` bootstrap image. It also writes symbol maps.
Open-ROM mode requires all four images in the selected directory. It never
silently loads installed Commodore BASIC or 1541 firmware.

For tape SAVE, call `C64System.StartTapeRecording(path)` with a new `.tap`
path before issuing `SAVE"name",1`, then call `StopTapeRecording()`.
Stopping writes a TAP v1 image and mounts it for playback. The KERNAL
generates the waveform through the 6510 WRITE pin; no host-side SAVE
injection is used. A mounted TAP can be read with `LOAD"name",1` or
`VERIFY"name",1`.

## Verified behavior

The headless verifier runs the actual 6510, CIA, IEC and D64 emulation with
KERNAL load shortcuts and low-level IEC hooks disabled. It checks:

- Boot into Open Basic 64, the character ROM layout, and public KERNAL jump
  table geometry. The startup screen uses a centered Open Basic title, the
  available-memory line, classic blue colors, and the READY prompt.
- BASIC tokenization and linked program lines at `$0801`, line replacement
  and deletion, `LIST`, `RUN`, five-byte floating-point expressions with
  decimal literals, `E` notation and `+`, `-`, `*`, `/`, `^`, comparisons, bitwise
  `AND`/`OR`/`NOT`, `ABS`, `SGN`, `INT`, `SQR`, `RND`, `LOG`, `EXP`,
  `COS`, `SIN`, `TAN`, `ATN`, `WAIT`, `IF/THEN`,
  `GOTO`, `GOSUB/RETURN`, indexed `ON ... GOTO/GOSUB`, nested `FOR/NEXT`,
  `POKE/PEEK`, and `SYS`. Normal numeric variables use the public five-byte
  floating-point storage layout; `%` variables use the two-byte integer
  layout. `FOR/NEXT` limits and steps are floating-point values. `PRINT`
  uses rounded nine-digit fixed/scientific output. Four fixed five-byte
  multiplication regressions distinguish direct decimal literals from values
  previously stored in BASIC variables.
- Literal and `STR$` string assignment and `INPUT`, `LEN`, `ASC`, fractional
  `VAL` with exponent notation, `PRINT STR$(...)` and `PRINT CHR$(...)`;
  fractional numeric `INPUT`/`READ` and string
  `READ/DATA/RESTORE`.
- IEC commands and channels, BASIC `LOAD`, `SAVE`, `VERIFY`, `OPEN`,
  `CLOSE`, `GET#`, `INPUT#`, and `PRINT#`; machine-code and tokenized BASIC PRGs;
  one- and two-sector D64 save/load persistence; and a missing-file error.
- Public KERNAL calls, writable RAM vectors, and selected private entries
  `$E544` (screen clear) and `$FD15` (vector restore).
- C64 TAP v0/v1 cassette image parsing and cycle-timed playback through the
  6510 PLAY/motor pins and CIA1 FLAG input. The 6510 cassette WRITE pin now
  records rising-edge pulse intervals into a new TAP v1 image. The verifier
  records, restores an in-progress recording from a savestate, and plays the
  resulting image back. KERNAL device 1 LOAD/VERIFY decodes a standard
  program header, duplicated header, data, parity, and XOR check byte via
  CIA1 FLAG and timer B. A failed first header or data copy is retried from
  its second copy. SAVE emits two header and two data copies via the 6510 WRITE pin.
  The verifier loads separately generated TAPs with damaged first header and
  first data copies, then
  saves a program through the ROM and loads the recorded TAP back. An empty
  TAP makes LOAD fail with the end-of-tape bit in `READST`/`$90`.
- RS-232 user-port signal pins: CIA2 PB0/FLAG2 receive and PA2 transmit,
  including falling-edge handling and savestate restoration. The synchronous
  KERNAL path accepts PAL 50/75/110/134.5/150/300/600/1200/1800/2400 baud with 5-8 data bits,
  one or two stop bits, and odd/even/mark/space parity. `CHROUT` transmits
  through PA2; `CHRIN` receives from PB0; `GETIN` returns immediately when
  idle. `READST` returns and clears `$0297` when device 2 was selected;
  parity, framing, and empty-buffer conditions set bits 0, 1, and 3.
  The verifier checks all ten baud rates in 8N1, plus 7E1 and 5O2 frames
  at 300 baud, 7E1 parity and 8N1 framing errors, idle `GETIN`, and `READST` device
  selection. Control byte `$00` with the optional
  timer bytes is checked at 300 baud; fixed codes `$01` through `$0A`
  and `OPEN` device 2 interface follow the
  [Commodore input/output guide](https://www.commodore.ca/manuals/c64_programmers_reference/c64-programmers_reference_guide-06-input_output_guide.pdf).
- The independently built 1541 bootstrap is mapped into drive memory. Its
  reset vector and execution to the idle loop are checked with the drive CPU.
  The ROM's `$F2B0` path executes `$D0` and `$E0` buffer jobs in the five
  mapped RAM buffers and reports completion or invalid-job status. An isolated
  drive-CPU test checks each slot, its track/sector pointer, and Decimal-flag
  behavior without the emulator's host DOS job service. The `$C0` bump job now
  drives the disk VIA stepper to track 1 and restores its motor/port state;
  `$C1` reports the absent second drive. The scheduled CPU/ROM path handles
  bump jobs and keeps a second queued job pending until the first completes.
  The `$B0` seek job validates track/sector geometry, moves the head through
  the disk VIA and updates DOS track/sector RAM. The headless verifier checks
  valid and invalid seeks followed by another job on the same drive.
- The drive ROM contains an independently written five-GCR-byte to four-data-byte
  decoder at `$C200`. A direct 6502 test checks 256 seeded groups and invalid
  symbols. Native VIA/SO capture and the `$80` READ and `$A0` VERIFY jobs decode
  sectors through the drive CPU. A strict host-service-free READ audit checks
  12 byte-exact reads across tracks 1–35 and all five mapped buffers, three
  queued/repeated reads, missing media, invalid geometry/drive, and damaged GCR
  records. The VERIFY audit checks 16 matching/mismatching sectors across the
  tracks and buffers, buffer preservation, four sequential/repeated jobs,
  missing/invalid media, and four damaged record types. The
  ordinary emulator path routes both Open 1541 DOS jobs into the ROM.
- The `$90` WRITE job builds a 325-byte GCR data record in drive RAM,
  locates and validates the requested sector header, checks the write-protect
  sensor, and streams SYNC and data through the disk VIA. The rotating GCR
  track is committed to the D64 after a valid record and checksum are
  decoded. A strict host-service-free audit verifies 12 persistent 256-byte
  writes across tracks 1–35, all five buffers, and repeated sectors. The
  ordinary emulator route also completes through the drive CPU; geometry,
  missing media and write-protect cases are recorded in the
  [native WRITE report](../artifacts/native-write-job-v2/README.md).
- CPU-only cycle counts for `SETLFS`, `SCREEN`, and private `RESTOR` are fixed
  as regression checks and reported in `verification.log`.

The emulator's IEC/D64 service handles standard drive operations in this
mode. Installed 1541 firmware is not mapped.

## Reference comparison

The optional `--audit-open-rom-abi` command runs 42 public and selected
private KERNAL call cases through the same emulated 6510 core against the
Open ROM and the user's locally installed reference ROM. It records returned
registers, selected RAM bytes, and CPU-only cycle counts in `abi-audit.log`.
The reference ROM bytes are never written to that report or copied into the
Open ROM build. The current run has 38/42 cases returning on both sides,
19 matching register/RAM signatures, and 9 matching cycle counts. These
are diagnostics, not a compatibility certification: the isolated CPU test
does not advance VIC/CIA peripherals, and some reference routines wait or
time out in that setup. The audit also compares 28 bytes of BASIC numeric
and integer variable entries and representative floating-point expressions;
the variable-layout sample matches exactly. In the current emulator-only
sample, 45 of 47 five-byte results and all 18 printed number strings match.
The deterministic 150-case floating-point sample matches 133 results exactly:
90/90 binary operations, 25/42 math-function cases, and 18/18 comparisons.
An additional 19-case product sample, including direct decimal literals and
stored variables, matches all five-byte results. This is a finite sample,
not a proof for every possible operand.

`python roms/compare_math_logs.py baseline.log candidate.log` compares two
case-aligned audit logs, rejects mismatched reference results, and reports
exact-match gains/losses, status changes, and the sum of absolute ULP errors
where both cases have comparable exponents. This is used for independent
candidate holdouts before changing shared ROM sources.
Changed numeric values outside that ULP comparison are listed separately,
so an apparently smaller comparable sum cannot conceal exponent or
zero-payload regressions.

The 125-case math grid matches 80 results exactly: SQR 11/15,
LOG 10/15, EXP 11/15, SIN 13/20, COS 8/20, TAN 9/20, and ATN 18/20.
A deliberately difficult [LOG-near-one holdout](../artifacts/agent-log-near-one-v1/README.md)
matches 0/64 signed arguments. In the reference, `LOG(1.01)` and
`LOG(1.01+0)` differ despite the same stored five-byte input; a CPU FAC trace
shows their different guard paths. This points to intermediate precision and
rounding, rather than a correction based solely on the stored argument.
A separate [25-pair function audit](../artifacts/math-guard-boundary-v1/README.md)
finds identical stored five-byte inputs with and without `+0` in 13 input
pairs, but differing reference results for `LOG(1.01)`, `LOG(1.1)` and
`EXP(1.1)`; Open changes none. The same sampled distinction is absent from
SQR, SIN, COS, TAN and ATN. This identifies more observable guard cases,
not an output-side correction.
The positive-argument `EXP` path evaluates 24 Horner terms using a rounded
divide before each multiply and scales only at magnitude 4. This made
`EXP(2)`, `EXP(3)`, and `EXP(4)` byte-exact without losing a grid match.
The negative-argument path retains the previous forward summation and scales
at magnitude 2. A separate 17-case exponent-range file matches 4 results,
up from 2; results on either side of the scale boundary still differ.
An independent, deterministic 64-case exponent sample matches 19 results,
up from 16 with the previous positive path. The sum of absolute ULP errors
fell from 248 to 216, but `EXP(9.252598)` lost its former exact match and
now differs by 3 ULP. A 26-case extreme-value sample matches only the two
overflow cases; in particular, the underflow payload bytes at `EXP(-90)`
and `EXP(-89)` differ. These holdouts limit the apparent grid improvement.
The [negative-EXP analysis](../artifacts/agent-exp-precision-v1/README.md)
finds that reference `EXP(-88.0297)` already has exponent zero while the
current Open result still has exponent one. A later Open underflow also
retains different payload bytes; there is no general correction yet.
The angle reducer now uses a FAC-rounded quotient of radians to turns for
angles beyond the first half-turn. This added exact `SIN(±1000)` and
`TAN(±100)` results without losing an exact grid or fuzz case. `COS(-100)`
is a known rounding regression in magnitude: its error rose from 28 to 52
ULP. Eighteen probes around the reducer transition retain their previous
five-byte outputs; only two of those match the reference exactly.
The signed 63-case trigonometric stress file spans tiny arguments, period
boundaries, and up to `1E20`; 22 results currently match byte-for-byte
(SIN 9/21, COS 4/21, TAN 9/21), with no pending cases at the documented
cycle budget.
An [80-case signed ATN holdout](../artifacts/atn-series-v1/README.md) matches
54 results exactly. Isolated one-byte variants adding or removing one series
term leave all 80 results and statuses identical, so a cutoff change does
not address the measured one-ULP errors.
For `|x| < 2^-8`, the SIN input is quantized as a fraction of a turn before
the existing series: positive arguments retain 41 turn-fraction bits and
negative arguments 39. The 40-case independent signed small-angle sample
matches all 40 five-byte outputs, up from none; a 30-case sample around
`2^-24` and `2^-8` matches 16, up from 3, without losing an exact case.
The cutoff now leaves at most 10 SIN or 5 TAN ULP of observed error in its
sampled neighborhood. The separate COS-small-angle route improves two
targeted tiny TAN results to byte equality and reduces two others to one
ULP; larger-angle trigonometry remains incomplete. The current rebuilt
shared images, including the zero-exponent function correction below,
have SHA-256 hashes
`A305A4CE574E79BBC7954F3DEA6FBF835A0DDC55EB66CCF51A53D5BEFD346A2B`
(BASIC) and
`12790089D1B2C737FBEBE4841ADC730B4553E04452428871AD6ED4FA0B350B5D`
(KERNAL).
The reference emulator's `COS(x)` result matches `SIN(2*ATN(1)+x)` in all
19 sampled phase probes. Applying that phase route inside the Open ROM
improved some large-angle cases, but an independent 40-case holdout found
large rounding regressions. It remains isolated; the Open ROM still uses
its previous COS calculation. A 12-case file near both signed turn
boundaries records that the current COS route matches none of those
reference results. A separate [tiny-angle analysis](../artifacts/agent-tan-small-v1/README.md)
localizes four small `TAN` mismatches to the `COS` denominator. For signed
arguments from `1E-10` to `1E-7`, a visible linear expression
`1-ABS(x)/(4*ATN(1))` matches 24/40 reference `COS` results within at most
two ULP; the former Open series matches 0/40. The reference changes regime
sharply near `1.874E-7`. An early private prototype improved independent
holdouts but changed the `SIN(0)`/`COS(0)` entry ABI and was rejected.
The [integrated v3 route](../artifacts/agent-cos-small-v3/README.md) uses
`1-|x|/π` only for nonzero `|x|<2^-23`, below that transition. It adds a
helper in previously unused BASIC ROM bytes `$A004–$A00C`, without moving
existing labels or altering the null-angle routines. In two independent
signed 40-case holdouts, it improves exact COS results 0→16 and 0→0,
respectively, with no numeric loss. The 30-case small-angle boundary set
keeps 16 exact outputs while reducing comparable absolute ULP error 358→34.
Eight direct zero-angle CPU entry probes and the full selected ABI audit
match the previous Open ROM. The [integrated v4 clamp](../artifacts/agent-cos-phase-v4/README.md)
routes a nonzero tiny angle that rounds back to one through the established
COS zero-angle result. The preselected 60-case ultra-small holdout improves
0→60 exact; a separate 70-case boundary holdout improves 33→43 exact,
with 12 numeric improvements and no loss. The 125-case math grid improves
78→80 and 64 boundary cases improve 31→33. The transition near
`±1.874E-7` and larger angles remain discrepant; private nonzero-entry
timing is not yet certified.
The separate `math-cases.txt` file contains stable boundary expressions for
repeatable runs with `--audit-open-rom-math-cases`. A matching exponent and
sign adds `ulp=` (signed Open minus reference units in the last place) to
each differing result, so small rounding errors can be distinguished from
larger failures.
In the current run, 33/64 boundary expressions match, up from 18/64 before
the `EXP` and angle-reduction changes. The new exact cases include
`EXP(.0099)`, `EXP(.0159)`, `EXP(.25)`, `EXP(.5)`, `EXP(1)`, `EXP(2)`,
`SIN(1000)`, `TAN(±100)`, and `SIN/TAN(±1E-20)`; none of the other 64
boundary cases lost an exact match.
The optional last CLI argument sets cycles per expression; complex expressions
in `math-compositions.txt` need a larger budget to avoid false `PENDING`
results.
The math-grid runner checks independent emulator pairs on two worker threads
without opening the host audio device and writes results in case-file order.
The case-file runner can opt into the
same mode with a final `2` argument after its cycle budget.
The 13 composition probes match 7/13; `EXP(LOG(100)/2)` now matches. The
intermediate product in
`1000-159*(4*2*ATN(1))` matches the reference when stored, but the direct
subtraction differs by 112 ULP. Adding zero or multiplying by one before
subtraction makes the result match. This points to an internal guard-state
difference that the five stored bytes alone do not reveal.
RAM traces localize this difference to a fractional guard carried from
`ATN(1)` through multiplication into the following opposite-sign addition.
Before storage, the reference FAC for `ATN(1)` ends in mantissa byte `$A1`
with guard `$BA`; storage rounds it to `$A2`. Its later product by 159 ends
in `$94` with guard `$E4`; storage rounds it to `$95`. The discarded fraction
accounts for the 112-ULP subtraction difference exactly. A direct decimal
literal representing `2*pi` has a different input guard and produces a
400-ULP subtraction difference even though its stored product is the same.
Keeping only the final product's low byte would instead reproduce the
`ATN(1)+0` variant, which differs by 240 result ULP. A general correction
needs extended intermediate arithmetic through function and operator calls,
and deliberate rounding at storage boundaries; the current ROM does not
yet implement that path. The paired expressions in `guard-compositions.txt`
make this distinction reproducible: 15/34 currently match byte-for-byte.
In the independent twelve-pair near-cancellation subset, none of the direct
products match and all twelve `+0` barriers match, for both result signs and
several divisors and decimal literals. Some mismatches have a zero exponent
on both sides but different payload bytes; those count as byte differences.
The later [product-guard study](../artifacts/product-guard-mechanism-v1/README.md)
finds that an exact 40-bit multiplication of the observed reference FAC and
guard still misses four of 21 ATN products by one guard unit. Its preselected
64-expression cancellation holdout matches 0/21 direct ATN cases and 14/21
`+0` controls; decimal-literal products match 0/11 direct and 11/11 controls.
The seven new ATN control losses show that the earlier 12/12 control result
does not generalize across multipliers. A follow-up diagnostic confirms that
all seven corresponding stored Open products are already one product ULP
above the reference, with or without `+0`. A fixed-point check reproduces
all seven reference products from the unrounded ATN FAC and all seven Open
products from the prematurely rounded ATN input.
Expanded RAM-only reference traces over 3,330 integer-factor products yield
a reproducible [guard-bit rule](../artifacts/product-guard-mechanism-v1/README.md):
after exact 40-bit multiplication and normalization, the reference clears
the last guard bit on the sampled route without an extra overflow shift.
Two preselected factor-range holdouts match 480/480 and 1,280/1,280 states.
Arbitrary noninteger multiplication and the full FAC/ARG guard lifetime
remain unverified; this rule has not been integrated into the ROM.
A [direct decimal cancellation study](../artifacts/direct-cancellation-v1/README.md)
found two mismatches among 28 cases, both masked by a `+0` barrier despite
identical stored five-byte inputs. The integrated guard and null-cancellation
correction now matches **28/28** of those cases and improves a preselected
independent holdout from 49/64 to **58/64**, with no exact losses. A later
general [decimal division guard correction](../artifacts/decimal-guard-v4/README.md)
raises that holdout to **63/64** while retaining all **28/28** direct cases.
It preserves the initial quotient path through divide-by-ten and transfers
one more remainder bit only for the large-quotient path. The SQR routine was
compacted to house the guard helper without displacing other ROM functions;
its tested 13-case outputs remain unchanged. The frozen Fuzz, RND, grid,
guard, zero, EXP, LOG, COS, trig, and selected ABI gates show no losses;
two additional TAN phase-boundary cases now match. Internal divide and SQR
labels move, so arbitrary private entry and timing compatibility remains
unverified. A second [decimal-guard and rounding correction](../artifacts/decimal-guard-v5/README.md)
also carries the next remainder bit when the first bit is zero on the
normalized quotient route. It rounds stored literals only when the high
guard bit is set. The previously fixed cancellation holdout now matches
**64/64**; a newly frozen 80-case holdout across twenty other decimal bases
improves **78/80 to 80/80**, with no exact losses. Headless, Fuzz133/150,
RND640/640, grid80/125, LOG/EXP/zero/trig/COS gates, the 113-line selected
ABI log, and fourteen seeded FP entry signatures have no observed regression.
The
general 64-case math set improves from 31 to **33/64** by matching
`COS(±1E-20)`. Fuzz, grid, guard compositions, RND, null functions and
the selected ABI audit have no observed regression. The new BASIC layout
keeps 20 of 22 formerly moved inner labels at their prior addresses;
`FP_ADD_ROUND` and `FP_ADD_DONE` move by `$12` bytes, and CPU-only cycle
counts change. Arbitrary private entries and timing remain unverified.
An isolated [40-bit/layout study](../artifacts/agent-fp-guard-v3/README.md)
found 91 usable BASIC ROM bytes through loop compaction. The newer anchored
layout uses smaller gaps and explicit fall-through bridges. A complete
40-bit FAC/ARG/math-slot path still requires more code space and RAM
lifetime work; the integrated correction handles only the tested literal
guard and direct subtraction boundary.
The [current LOG audit](../artifacts/log-current-v1/README.md) confirms that
`LOG(1.01)` and `LOG(1.01+0)` still enter the Open LOG routine with identical
FAC/guard state, while the reference yields different results. An
[isolated LOG normalization study](../artifacts/log-adaptive-v3/README.md)
reduced the comparable near-one error but stayed 0/64 byte-exact. Its
independent 80-case interval holdout lost five formerly exact results and
exposed larger errors near one; it was not integrated. A separate
[literal-guard dispatch probe](../artifacts/log-guard-v2/README.md) also
regressed near-one accuracy. Two [isolated decimal-guard probes](../artifacts/decimal-guard-v1/README.md)
regress the previously exact direct-cancellation cases, so changing the
parser's guard byte alone is not a general repair.
An earlier isolated [two-bit decimal-division probe](../artifacts/decimal-guard-v2/README.md)
improves five previously missed direct cancellations but loses four frozen
holdout matches and two exact cases in the direct 28-case set. RAM traces
show the reference sometimes retains a temporarily unnormalized parser FAC
with a different exponent and guard. That probe also displaces EXP/COS code
for measurement and was not integrated.
An exponent-borrow correction removed the former catastrophic wrap in
`EXP(.01)` and neighboring small inputs; `EXP(.01)` remains one mantissa bit
below the reference result.
The separate RND audit compares the five-byte state after 64 consecutive
`RND(1)` calls for each of the boot, `RND(-1)`, `RND(-.1)`, `RND(-2)`, and
`RND(-.5)` seeds; all 320 states match in the current emulator run. This
longer audit exposed and then verified a correction to carry propagation
from the product guard during RND's small-constant addition.
The RS-232 audit matches the full-bit timer pair at `$0299/$029A` for all ten
fixed control codes and one custom 300-baud setting. Its isolated `OPEN`
register/carry signatures still differ; matching timers alone do not establish
full RS-232 compatibility.
These counts describe only the listed probes. No real C64
or 1541 timing traces were available.

## Compatibility limits

This is a working independent ROM set, **not yet a complete BASIC V2 or
KERNAL replacement**. Open Basic 64 now performs five-byte floating-point
arithmetic and the `SQR`, `RND`, `LOG`, `EXP`, `COS`, `SIN`, `TAN`, and `ATN`
functions. The optional reference
comparison shows many matching five-byte results and printed values. The
sampled binary operations, including 19 targeted products, now match. Rounding
fresh decimal literal arguments before the applicable math functions repairs
four sampled cases. Reordering a series term repairs `SIN(10)`. A separate
RND guard-carry correction keeps the extended `RND` sequences exact without
changing the sampled math results. Differences
remain in square root, logarithmic, exponential, and trigonometric cases.
Reference `SQR(x)` equals reference `x^.5` in the tested cases, including
`1E±20`; even `x^1` can differ from `x` there. Changing only the Open Newton
iteration count did not produce a general match: four steps gained two of
fourteen boundary matches but lost another, while five and six matched the
current seven-step outputs. A separate 13-operand
[route probe](../artifacts/agent-sqrt-path-v1/README.md) confirms five-byte
`SQR(x)` = `x^.5` for every reference result, whereas
`EXP(LOG(x)/2)` agrees in only 3/13; substituting that visible expression is
not a general square-root correction. A separate
[power-route audit](../artifacts/agent-sqrt-power-v1/README.md) finds
`EXP(.5*LOG(x))` equal to reference `SQR(x)` for 17/18 direct operands,
with one literal/variable rounding exception. The same visible expression
in Open matches only 2/13 reference SQR results in an independent holdout,
versus 6/13 for the existing Newton routine.
The wider grid still finds errors beyond the last mantissa bit in
trigonometric results at large arguments.
The 47-case audit still differs for
`SQR(.01)` and `EXP(-1)`. Decimal literals retain a
rounding bit until a variable stores the value. [The integrated zero-exponent
function correction](../artifacts/agent-zero-func-v1/README.md) canonicalizes
the result of `INT`, `SGN` and `SQR` when the argument exponent is zero;
arithmetic and `ABS` retain their reference-matching payload behavior.
The frozen 26-case payload set improves 23→26 exact, and a separate 36-case
function holdout improves 0→36. The 125-case grid stays 80/125, the
150-case Fuzz stays 133/150, and RND, selected ABI, 64 boundary, 34 guard,
13 composition and 63 trigonometric stress results remain unchanged from
the COS-v4 build. Underflow is not yet byte-exact in every function.
A separate [49-case survey](../artifacts/math-zero-input-v1/README.md)
finds all sampled LOG, EXP, SIN, COS, TAN, ATN and SQR outcomes on seven
zero-exponent sources byte-exact against the reference.
`RND` now uses
the five-byte state, initial seed, and sign-dependent recurrence observed in
the reference emulator. A [longer audit](../artifacts/rnd-extended-v1/README.md)
confirms five 128-step sequences, 640/640 byte-exact, while leaving the
64-step default and its log unchanged. Other seeds and longer runs remain
to be checked. `RND(0)` depends on the
different boot and CPU timing. The
[contemporaneous RND analysis](https://www.commodore.ca/wp-content/uploads/2015/12/the_transactor_vol09_04_1989_apr-www-commodore-ca.pdf)
describes the mathematical recurrence and CIA sources; this ROM implements
them independently. Exact rounding and extreme-value compatibility have not
been established. Numeric addresses still narrow to 16 bits.
The current Open FAC/ARG state at `$03E0-$03FF` and math save slots at
`$03B0-$03D7` overlap the published cassette buffer `$033C-$03FB`.
Open's `FOR` frames at `$02A0-$02DF` also overlap published tape/RS-232
cells. The reference-facing FAC/ARG and guard locations are in zero page
`$0061-$0070`, so machine code and `PEEK` can observe different RAM side
effects even when a stored five-byte result matches. The apparently unused
`$02E0-$02EF` is program- and VIC-visible RAM and cannot simply become a
permanent 40-bit guard area. A general extended-FAC implementation requires
a lifetime and RAM-layout review as well as ROM code space; see the
[published memory map](https://www.commodore.ca/manuals/c64_programmers_reference/c64-programmers_reference_guide-05-basic_to_machine_language.pdf)
and the [local RAM-ABI analysis](../artifacts/agent-guard-ram-abi-v1/report.md).
General string expressions, arrays, many functions and statements, and some
edge cases remain missing. The token table preserves the public BASIC V2
file representation, but tokenizing a keyword does not mean its statement
is implemented. `VAL` accepts decimal and exponent prefixes and stops at the
first nonnumeric character. `STR$` works for assignment and `PRINT`, including
`VAL(STR$(...))`; `CHR$` currently works as a `PRINT` item, not as a general
string expression.

Open Kernal 64 has a tested device 1 program LOAD/SAVE path but it is not a
complete cassette implementation. It requires a standard program header and
duplicate header and retries a damaged first header or data copy. An empty
TAP now reports end-of-tape status. It does not yet implement
sequential tape files, advanced error recovery,
user-facing RECORD controls, or hardware-verified pulse timing. SAVE requires
`StartTapeRecording(path)` before the ROM call. The full device 2 RS-232
driver and some printer, keyboard and screen behavior also remain open. The current
RS-232 path is synchronous and supports the ten PAL rates listed above plus
user-defined timer bytes. NTSC timing, exhaustive frame-format
validation, handshaking,
background NMI processing, C64-compatible transmit and
receive buffers, and a host serial endpoint are absent. The emulator has a
Datasette signal model for [VICE TAP v0/v1 images](https://vice-emu.sourceforge.io/vice_17.html)
with TAP v1 recording through the 6510 WRITE pin. TAP v0 overflow
pulses are approximated at 2048 cycles because that format does not store
their exact duration. Private ROM entry points are only checked selectively;
the cycle regression counts are for our own ROM and have not been compared
against reference hardware or arbitrary ROM routines. The character shapes
and graphics are independently drawn and differ at the pixel level.

The independently built 1541 image is currently a **bootstrap with selected
native jobs, not a complete DOS implementation**. The `$80` READ, `$90` WRITE
and `$A0` VERIFY jobs now use the ROM, drive CPU, disk VIA and GCR stream;
their strict tests and scratch-RAM limitations are in the
[native READ report](../artifacts/native-read-job-v1/README.md),
[native WRITE report](../artifacts/native-write-job-v2/README.md), and
[native VERIFY report](../artifacts/native-verify-job-v1/README.md).
Standard DOS commands and IEC file service still use the emulator service.
The older [sector-job baseline](../artifacts/native-sector-audit-v1/README.md)
shows how all three jobs previously remained pending under CPU-only execution.
The disk mechanism now clocks VIA port-A bytes onto the rotating GCR track
and pulses byte-ready/SO in write mode, including write-protect behavior;
the [write-signal audit](../artifacts/native-write-signal-v1/README.md) verifies
this prerequisite. Complete checksum-valid GCR records are converted into
persistent D64 sectors after a physical VIA write; the
[commit audit](../artifacts/native-write-commit-v1/README.md) checks three
tracks, bad checksums and write protection. The DOS ROM contains a
four-data-byte to five-GCR-byte encoder at `$CA00`; 258 direct drive-CPU
groups match an independent bit-packing model. The native `$90` job uses
this encoder. D64 represents logical sectors, so raw custom formats cannot
be persisted losslessly with this image type.
`$D0`/`$E0` buffer execution path works for the five physically mapped RAM
buffers; `$E0` does not yet perform disk-header preparation. The `$C0` bump
job now reaches the native ROM through the CPU job queue and moves the head
via the disk VIA. `$B0` SEEK also reaches the ROM and validates geometric
track/sector bounds, but it does not inspect a sector header on the mounted
medium, so a geometrically valid pair can report success without a disk.
Uploaded drive code that calls other 1541 DOS ROM routines remains unsupported.
The bootstrap alone does not establish 1541 compatibility.
The [read-only real-workload audit](../artifacts/real-workloads-v1/README.md)
uses locally owned D64 images outside this repository. Winter Games and
Golden Axe load their first programs byte-exactly with both ROM sets. The
[IRQ/BRK vector audit](../artifacts/kernal-irq-contract-v1/README.md) matches
synthetic custom-vector register states and cycles. The
[private `$FEBC` repair](../artifacts/kernal-febc-sqrt-v1/README.md) restores
registers and returns in the reference's 22 cycles while retaining SQR. Golden
Axe now passes its previous CPU jam and executes for eight million sampled
cycles; this does not establish playability. A small Kick Off II boot file
loaded byte-exactly in an earlier sample, but the current reproducible
workload reaches only 89/93 bytes with both the previous and current Open
builds (reference: 93/93); the earlier result is not a stable gate. The
[BASIC main-loop vector repair](../artifacts/basic-main-vector-v1/README.md)
now enters its loaded code. The [private `$EDB9` IEC SECOND entry](../artifacts/kernal-second-private-v1/README.md)
removes the subsequent CPU jam, but later CPU states still differ from the
reference and playability is unverified. The [private `$ED0C` LISTEN
entry](../artifacts/kernal-listen-private-v2/README.md) avoids falling into
SAVE code. The subsequent [private IEC command-A correction](../artifacts/iec-private-a-v3/README.md)
preserves the requested command byte while saving X/Y. Bus traces now decode
the same LISTEN `$28` and SECOND `$6F/$61/$E1` bytes as the reference;
previously both wrappers sent `$34` for the seeded tests. The isolated and
shared builds pass Headless; the 150-case Fuzz, 125-case math grid and
640-step RND logs of the command-A correction are line-identical to the
preceding build (133/150, 80/125 and 640/640). The subsequent
[IEC phase correction](../artifacts/iec-phase-v2/README.md) shortens the
software bit periods and adjusts byte-start CLOCK and DATA transitions.
The sampled private SECOND `$61` return now takes 2,186 emulator ticks
versus 2,044 in the reference, down from 5,357; the median bit-high and
inter-bit-low periods are 28/78 ticks versus 26/70. The shared four-ROM
Headless verifier passes, and the selected BASIC Fuzz, math grid, RND and
ABI logs are unchanged. The subsequent
[IEC acknowledgement correction](../artifacts/iec-byteack-v7/README.md)
distinguishes a missing device from a missing byte acknowledgement and
releases the bus after the sampled byte error. Preselected delayed-device,
delayed-byte, status-seed and retry comparisons now match the sampled
reference return registers, serial status and external bus ownership.
The 150-case Fuzz, 125-case grid, 640 RND steps and selected ABI protocol
are unchanged. An isolated
[EOI-delay candidate](../artifacts/iec-eoi-v2/README.md) transmitted the
correct four IEC bytes at longer EOI response delays, but lost two of the
13 previously matching byte-ACK boundary cases and was rolled back. IEC
cycles and the public return registers remain open. The subsequently
[integrated separate EOI wait](../artifacts/iec-eoi-v4/README.md) keeps the
ordinary DATA-low timer unchanged and transmits the sampled four-byte
sequence at EOI response delays through 2600 emulator ticks, versus a
1200-tick failure before. All 13 measured byte-ACK boundaries, eight
ATN-ACK boundaries, selected BASIC/ABI gates and initial real-disk loads
retain their sampled results. The reference still waits successfully at
sampled EOI delays through 5000 ticks; delayed final EOI-byte ACK, inner
private IEC entries and the Kick Off II follow-on path remain open. These are
concrete compatibility limits beyond the synthetic headless verifier.
The subsequent [EOI reply wait](../artifacts/iec-eoi-v5/README.md) observes
the IEC DATA line until the listener responds. Both Open and the local
reference complete the sampled transfer with delays up to 400000 emulator
ticks and remain waiting when no response arrives within the 500000-tick
observation window. The 13 ordinary byte-ACK boundaries and broad BASIC,
RND and selected ABI logs stay unchanged. The final EOI data-byte ACK and
public return-register differences remain open. No unbounded behavior or
hardware-level timing equivalence is inferred beyond the measured window.
The [public UNLISTEN ABI correction](../artifacts/iec-unlisten-abi-v6/README.md)
preserves X/Y and returns the sampled A/P values for six buffered seeds and
one bare mounted call. The pending CIOUT flag now uses bit 6 so its BIT test
sets V for buffered calls and clears V for bare calls; CIOUT restores its
input A. The isolated and hash-identical shared four-ROM builds pass
Headless. Thirteen normal byte-ACK and eight ATN-ACK boundary returns still
match, while the 3x5 final EOI-byte-ACK grid remains only 6/15 exact. Fuzz,
grid, RND and selected ABI logs are unchanged; Winter Games and Golden Axe
still load their first programs byte-exactly. Bare no-medium UNLISTEN still
reports serial `$03` where the reference reports `$80`; CPU cycles and
arbitrary private RAM consumers remain unverified.
The subsequent [final EOI-byte ACK recovery](../artifacts/iec-final-ack-v3/README.md)
uses the ordinary ATN/DATA handshake after a failed buffered data byte and
adds the timeout bit to the existing serial error. A same-cycle ROM
compaction fits it before the fixed RS-232 entry. The preselected 3x5 EOI
grid now matches 14/15 serial/wire/phase/flag results instead of 6/15;
an independent 32-pair holdout matches 30/32. Neither remaining holdout
mismatch was an exact match in the previous build. Normal ACK13/13 and ATN
ACK8/8 returns, Headless, BASIC fuzz/grid/RND, selected KERNAL ABI and the
first-program byte counts of three real disk workloads show no material
regression. One short-EOI/final-ACK boundary, bare no-medium UNLISTEN
status, private entries and exact CPU cycles remain open.
The [bare UNLISTEN command-handshake correction](../artifacts/iec-unlisten-bare-v1/README.md)
routes an unbuffered call through the existing ATN/DATA device check.
Without a medium it now returns serial `$80`, matching the local reference
instead of `$03`. A preselected 24-case no-medium/mounted/status/stack/device
holdout matches all sampled A/X/Y/P/SP, serial and bus results. The ROM image
changes one branch displacement byte; the isolated and hash-identical shared
four-ROM builds pass Headless. BASIC fuzz/grid/RND and selected KERNAL ABI
logs remain unchanged, as do the first-program byte counts in three real
disk workloads. CPU cycles, the remaining EOI/final-ACK boundaries, DOS
file service and broader software compatibility remain open.
The [selected TALK CLOCK wait](../artifacts/kernal-wait-clock-v1/README.md)
removes a premature KERNAL timeout while a device holds CLOCK low. In a
CPU/IEC test with the private native drive, the previous Open KERNAL fails
after a 300000-tick hold; the reference and corrected Open KERNAL receive
the byte and EOI even after 1000000 sampled ticks. The KERNAL image changes
two bytes without moving entries. The isolated Headless verifier passes;
the BASIC 150-case fuzz, 125-case grid, 640 RND steps and selected ABI log
are unchanged. This does not establish an unlimited reference wait or
complete DOS file service.
An isolated [native directory and PRG-sector prototype](../artifacts/native-directory-v1/README.md)
uses actual 1541 CPU/VIA/GCR READ jobs to find exact PRG names across linked
directory sectors and to load linked file sectors into a drive buffer. The
private DOS image is not integrated: IEC filename reception, channel buffer
ownership and native file LOAD/SAVE remain open. Standard files in Headless
still use the host DOS service, so that result cannot validate native DOS.
Software that inspects ROM bytes or checksums cannot be byte compatible with
an independently written ROM. Arbitrary C64 software compatibility has not
been established. Existing savestates may contain ROM data and require
separate review before redistribution.

The [EOI receive, LOAD retry and final DATA release correction](../artifacts/iec-eoi-retry-v1/README.md)
is integrated in KERNAL SHA256 `98C77C0A6687292A3996290721F232FC47199455CDEAC1970CC9482448CF2E14`.
With a separate private 1541 CPU and no host file service, missing files/media
return A=`$04`, serial=`$42` with five IEC commands, and a later successful
LOAD on the same instance works on devices 8–10. One-, two- and four-sector
PRGs and sampled delayed EOI replies load byte-exactly. A single final EOI
byte releases DATA; inter-byte backpressure is retained. Private and shared
hash-identical Headless pass; BASIC fuzz 133/150, math grid 80/125 and five
128-step RND sequences are unchanged. The selected ABI retains values/status
but LOAD/no-device takes 90 cycles versus reference 100. In that intermediate
build, public ACPTR still clobbered X/Y and had flag/cycle differences; the
subsequent ACPTR correction below addresses the observed register/flag cases.
Internal IEC/LOAD labels move,
and native DOS file service remains private, so arbitrary software
compatibility is not established.

The [public ACPTR register and flag correction](../artifacts/iec-acptr-abi-v2/README.md)
is integrated in KERNAL SHA256 `769961F3E3354B08CA2F47A4C9001CE83EB9D1DF84A4DC9AEBAB3A19AB33EBF7`.
Selected EOI-end returns and four of six no-data seeds preserve X/Y and match
observed A/P/SP/serial/IEC lines across devices 8–10; the other two v2 seeds
incorrectly clear V and are corrected by the subsequent v3 change.
Independent byte values `$00`, `$7F`, `$80`, `$FF` and two inter-byte cases
also match their observed return
contracts. The isolated Headless, BASIC fuzz 133/150, math grid 80/125,
RND 640/640 and selected 113-line ABI logs retain their previous results;
strict native multi-sector LOAD and error-to-success cases pass. A compacted
LOAD implementation makes room for the ROM wrapper without adding a permanent
RAM reservation. ACPTR cycles, some private labels, arbitrary entry points,
full DOS file service and broader workload behavior remain unverified.

The subsequent [public ACPTR overflow correction](../artifacts/iec-acptr-abi-v3/README.md)
fixes a v2 error found by a three-call IEC test: on a no-data return, v2
cleared the caller's V flag although the local reference preserved it. Thus
the six no-data seeds reported for v2 were only 4/6 exact. The new KERNAL
image SHA256 is `A48AA22A18A6C0E43815DC77121B031F2254E9BE3D8A5547EB8C47026A96C257`;
only ten public-wrapper bytes differ from v2. A preselected 12-case status
holdout matches 12/12 A/X/Y/P/SP, serial and IEC line results. A separate
36-return native VIA/IEC phase holdout improves from 28/36 to 34/36 with no
lost exact return; two pre-existing CLOCK release phase differences remain.
Headless, BASIC fuzz/grid/RND, selected ABI, native error-to-success LOAD,
and initial real-disk LOAD byte counts retain their prior results. ACPTR CPU
cycles and general ROM/software compatibility remain unverified.

The [reference 1541 boot bus audit](../artifacts/reference-drive-bus-v1/README.md)
identified host job-queue servicing as the cause of the local reference DOS
startup loop: a temporary `$80` in drive RAM job cell `$00` was overwritten
with `$02` during the ROM's RAM test. Native execution of an installed DOS ROM
now ticks VIA and mechanism without that host service. The reference DOS
initializes its serial VIA and completes a sampled native IEC
LISTEN/SECOND/UNLISTEN command transfer. Open-ROM Headless, native Open-DOS
WRITE routing, and a sampled real-disk first LOAD remain unchanged. In a
strict native test with no host file service, the locally executed reference
KERNAL loads 3974/3974 PRG bytes from the separately booted reference 1541;
Open KERNAL against the same drive and temporary disk returns serial `$80`
after only 59/3974 matching RAM bytes. That divergence is addressed by the
subsequent KERNAL change below. The four Open-ROM images were unchanged by the emulator-side
correction.

The [IEC TALK busy-DATA wait correction](../artifacts/iec-talk-busy-v1/README.md)
is now integrated in KERNAL SHA256
`FC70937B4487AFB011F556F370A1240B294C4EF559FCEA7B1C2B96C9EAEDED01`.
One branch byte at `$F26C` repeats the selected listener's DATA-high wait
instead of timing out while a native 1541 is still searching its directory.
With a separately booted local reference DOS and real VIA/GCR/IEC signals,
the Open KERNAL now loads the first PRGs of Winter Games (3974/3974),
Golden Axe (4238/4238), and Kick Off II (93/93) from temporary disk copies;
the previous Open image timed out at 59/3974 bytes on Winter Games.
Independent synthetic tests match 8/8 ATN-ACK, 13/13 byte-ACK, and 6/6
long busy-DATA returns. Missing-file status A/serial also matches the
reference, while other registers and CPU cycles differ. The isolated and
hash-identical shared Headless verifiers pass; BASIC fuzz/grid/RND and the
selected ABI logs are byte-identical to the preceding build. Native drive
jobs and a two-sector WRITE persist correctly. The shared DOS file service
still runs in the host, and initial loads do not establish game playability
or general ROM compatibility.

The [private `$EA81` IRQ-return contract](../artifacts/kernal-private-ea81-v1/README.md)
is integrated in KERNAL SHA256
`2BD676402336E45B0A89C0E6B2D28A863541AF8488AAC2D0D0534097D8E711AD`.
Black-box CPU probes match the locally executed reference KERNAL in three
independent six-byte stackframes, including A/X/Y/status/SP and the 22-cycle
RTI return. Public OPEN retains its file-table behavior by reusing the
existing logical-file lookup, while its selected no-device call takes 113
instead of 86 cycles (reference 95). All other ROM labels remain anchored.
The change removes Defender of the Crown's observed reset after a direct
game-code jump to `$EA81`; at four million RUN cycles, both Open and the
reference execute game RAM and show 968 occupied screen cells, while stack,
serial status, IRQ cycles and broader playability remain different or untested.
Headless, BASIC fuzz/grid/RND, native sector jobs, and first LOAD/RUN checks
for three other games passed in the isolated build. This does not establish
general game compatibility.

The [private `$EA31` IRQ-service contract](../artifacts/kernal-private-ea31-v4/README.md)
is integrated in KERNAL SHA256
`EFA93B5B8ABAA548C647BA2DE0E29890DF9E61F245B9126D600F832AF4ACE148`.
The entry now performs the existing jiffy and keyboard service before the
register restore/RTI. Three seeded stackframes match the executed reference
in A/X/Y/status/SP, return PC and jiffy increment; Open takes 320 versus
190 CPU cycles. The scanner's normal key state at `$C5` now matches observed
reference idle, A and Return values. Four real first-PRG LOAD/RUN probes
remain in their game/RAM paths through four million cycles; the prior
Maniac Mansion reset caused by entering scanner code at `$EA31` is gone.
The isolated and hash-identical shared Headless verifiers pass, as do BASIC
fuzz 133/150, math grid 80/125, RND 640/640, selected KERNAL ABI, native
buffer/seek jobs and 12/12 strict writes. Maniac's first BASIC line link,
stack and CPU phase, and broader gameplay remained different or untested at
that stage. The subsequent BASIC LOAD correction below addresses that link.

The [BASIC LOAD relink correction](../artifacts/basic-load-relink-v3/README.md)
is integrated in BASIC SHA256
`5020C32FDADFD6D42CFFD52E281760A13E32BC08093EEEC2BC8EE190A5CDAE59`.
The KERNAL was rebuilt against the changed BASIC map, SHA256
`4FD7E63D5411DEF1E60D8031C9D3784866FB9079B53422A334CEF0E726923068`;
the DOS and character ROMs are unchanged. BASIC LOAD now updates its end
pointer for both secondary addresses and corrects nonzero line links until
the high byte of the next link is zero. The line editor marks a newly
inserted record before using the same relinker. Black-box comparisons with
the locally executed reference match 60/60 predeclared synthetic native
IEC/1541 LOAD pairs, including independent multi-line, address, and
machine-tail cases. Winter Games, Golden Axe and Defender preserve their
byte-exact real-disk first LOADs; Maniac Mansion now matches the reference's
single corrected first-line link. A later
[Kick Off II timing trace](../artifacts/kickoff-tail-v1/README.md) establishes
that both systems initially load 93/93 bytes; an Open BRK/RESET later
overwrites the final four bytes at `$0300`–`$0303`. X and stack pointer
already differ at the boot-code entry. The isolated four-ROM Headless, BASIC fuzz
133/150, math grid 80/125, RND 640/640, selected ABI, native buffer/seek,
READ 12/12, VERIFY 16/16 and WRITE 12/12 gates pass. Standard DOS file
operations in the shared emulator still use host service; initial LOAD
success and short RUN probes do not establish general game compatibility.

## Current KERNAL screen-color correction

The independently written KERNAL maps screen pages `$04-$07` to color RAM
pages `$D8-$DB` without decimal-sensitive `ADC`. The three-byte, same-cycle
change is documented with CPU/Color RAM black-box comparisons and regression
gates in [the decimal-color report](../artifacts/kernal-decimal-color-v1/README.md).
Public CHROUT return registers/flags and cycles still differ from the
reference in the sampled calls. The screen-row helper now decodes a packed
25-entry table without decimal-sensitive addition. A CPU-only audit finds
25/25 correctly placed and colored rows with D set, versus 21/25 before this
change; the four previously missed rows match the executed reference ROM's
screen and color RAM. The first packed-table candidate failed Headless. The
revised candidate passed isolated and shared Headless and the relevant
numeric, IEC, native-file and real-program gates. Details and remaining
private-entry risks are in [the row-packing report](../artifacts/kernal-row-packed-v2/README.md).

## Private KERNAL IRQ return at $EA7E

The source now routes `$EA7E` to the existing register-restore/RTI tail while
public OPEN retains its file-table checks. A strict Arkanoid PRG smoke test
no longer JAMs at its first use of this private entry and continues for 20
million emulated cycles with the same screen/frame signatures as the executed
reference. All other 13 owned PRG five-million-cycle Open result blocks are
unchanged. The exact private and shared builds pass Headless and the selected
BASIC/KERNAL/native DOS gates. See
[the $EA7E report](../artifacts/kernal-private-ea7e-v1/README.md) for the
remaining private-label, timing and gameplay limitations.

## BASIC RUN relinks program records

`RUN` now calls the existing bounded BASIC line-link repair before execution.
The instruction sequence was kept address-stable, so KERNAL and later BASIC
entry addresses did not move. In the normal PRG mount, Impossible Mission
reaches its early 709-cell screen instead of returning to the prompt, and
Wonderboy enters low RAM rather than returning to the prompt. The other 12
owned PRG five-million-cycle screen and status probes show no material loss.
The isolated and shared builds pass Headless, numeric and native 1541 jobs;
strict first-file loads from four real D64s remain successful. See
[the RUN relink report](../artifacts/basic-run-relink-v2/README.md). Startup
does not establish full game playability, input handling or later disk access.

## KERNAL keyboard buffer boundary

SCNKEY now scans and updates its held-key state even when the keyboard queue
is full. It checks `$C6` against `$0289` immediately before storing a
translated character. In six black-box held-key/drained-queue cases this
prevents a delayed duplicate key; the old Open KERNAL duplicated all six.
Normal key/joystick results, 14 owned PRG startup screens and a strict native
Winter Games first-file LOAD retain their observed outcomes. The isolated
candidate passes Headless and selected BASIC/KERNAL gates; its shared build
is hash-identical and reverified. See
[the keyboard buffer report](../artifacts/kernal-keyscan-buffer-v2/README.md).
Matrix selection, modifiers and repeat modes remain incomplete, so this does
not establish full input compatibility or game playability.

## Private BASIC runtime clear at $A659

The source now provides a stack-safe runtime-clear entry at `$A659`. It
restores default input/output channels, clears runtime pointers and returns
with the documented stack state while retaining the open-file table. Eight
black-box stack/channel seeds match the executed reference in registers,
flags, pointers and return address. Isolated and shared builds have identical
four-ROM hashes and pass Headless. Numeric, native 1541 sector-job, 14 owned
PRG startup and four strict real-D64 first-file LOAD/RUN probes show no new
material loss. See [the $A659 report](../artifacts/basic-private-a659-v6/README.md).
Wonderboy still needs general keyboard repeat handling to reach this path
through normal input. Later game play and arbitrary private BASIC entries
remain unproven.

## KERNAL Tab key in the existing matrix map

The `$3F` matrix position now queues PETSCII `$03` when Tab is pressed.
The source changes one KERNAL ROM byte at `$EE8C` and keeps every entry
address fixed. The isolated keymap audit found no other changed key
outcomes among the enumerated plain, Shift and Commodore cases; 17
independent matrix/joystick probes retain their earlier results.
Isolated and shared builds have identical hashes and pass Headless.
See [the Tab audit](../artifacts/kernal-keymap-tab-v1/README.md).
Modifier translation, ghosting and key repeat still need broader ROM work.

