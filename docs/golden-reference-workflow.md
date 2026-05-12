# Golden Reference Workflow

The emulator now has three layers for accuracy reference work:

1. `--trace-machine` records structured machine-cycle JSONL traces.
2. `--golden-run` executes a manifest and writes JSON/JUnit results.
3. `--golden-accept` and `--golden-compare` manage accepted baselines.

## Run A Golden Suite

```powershell
.\scripts\run-golden-reference.ps1 `
  -Manifest docs\golden-manifest.sample.json `
  -OutputDirectory artifacts\golden-reference
```

This builds the emulator, runs the manifest, and writes:

- `golden-results.json`
- `golden-results.junit.xml`
- optional frame artifacts requested by the manifest

## Generate Local Disk Fixtures

The disk LOAD suite uses small fixtures generated from source text with the
bundled VICE tools:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\create-golden-fixtures.ps1 `
  -ViceRoot GTK3VICE-3.10-win64 `
  -OutputDirectory artifacts\golden-fixtures

C64Emulator\bin\x64\Release\C64Emulator.exe `
  --golden-run docs\golden-manifest.disk-load.json artifacts\disk-load-golden
```

The generated D64 contains a BASIC program named `AA` whose PRG file header
uses load address `$2000`. `LOAD"AA",8` must therefore relocate the program to
the current BASIC start address; `LOAD"AA",8,1` would be the absolute variant.

## Generate VIC-II Visual Fixtures

Small local VIC-II stress PRGs can be generated without third-party test assets:

```powershell
.\scripts\create-vic-fixtures.ps1 -OutputDirectory artifacts\vic-fixtures

C64Emulator\bin\x64\Release\C64Emulator.exe `
  --golden-run `
  artifacts\vic-fixtures\golden-vic-raster-bars.json `
  artifacts\vic-raster-compare\emu-golden
```

The generated manifest uses `mountAfterWarmup=true` so the emulator boots to
BASIC before each PRG is injected, then starts the machine routine with `SYS`.
Capture the same PRG with VICE `-autostart` and compare the resulting frames to
isolate border/RSEL/CSEL timing drift, sprite geometry drift, and other visual
VIC-II differences.

The stable sprite baseline is suitable for screenshot comparison:

```powershell
.\scripts\capture-vice-reference.ps1 `
  -VicePath .\GTK3VICE-3.10-win64\bin\x64sc.exe `
  -MediaPath artifacts\vic-fixtures\vic-sprite-baseline.prg `
  -Cycles 10000000 `
  -OutputDirectory artifacts\vic-sprite-compare `
  -Name vice-sprite-baseline-10m

.\scripts\compare-vice-frame.ps1 `
  -ViceFrame artifacts\vic-sprite-compare\vice-sprite-baseline-10m.png `
  -EmulatorFrame artifacts\vic-sprite-compare\emu-golden\vic-sprite-baseline.ppm `
  -OutputDirectory artifacts\vic-sprite-compare `
  -Name sprite-baseline-10m-normalized
```

The IRQ-anchored sprite phase probe is useful for `$D015` and raster-IRQ
regressions. It intentionally marks the border from the IRQ handler:

```powershell
.\scripts\capture-vice-reference.ps1 `
  -VicePath .\GTK3VICE-3.10-win64\bin\x64sc.exe `
  -MediaPath artifacts\vic-fixtures\vic-sprite-irq-phase.prg `
  -Cycles 10000000 `
  -OutputDirectory artifacts\vic-irq-compare `
  -Name vice-sprite-irq-phase-10m

.\scripts\compare-vice-frame.ps1 `
  -ViceFrame artifacts\vic-irq-compare\vice-sprite-irq-phase-10m.png `
  -EmulatorFrame artifacts\vic-irq-compare\emu-golden\vic-sprite-irq-phase.ppm `
  -OutputDirectory artifacts\vic-irq-compare `
  -Name sprite-irq-phase-10m-normalized
```

The related IRQ probes `vic-sprite-x-irq-phase.prg`,
`vic-sprite-color-irq-phase.prg`, and
`vic-sprite-multicolor-color-irq-phase.prg` isolate sprite X latching and live
sprite color timing. `vic-sprite-multicolor-enable-irq-phase.prg` does the same
for live `$D01C` sprite multicolor enable timing, while
`vic-sprite-priority-irq-phase.prg` isolates `$D01B` priority over foreground
graphics. The priority probe restores sprite pointer `$07F8` after clearing the
screen, so the sprite data remains anchored at `$3000` and the test isolates
`$D01B` priority instead of pointer-zero memory contents.
`vic-sprite3-wrap-baseline.prg` covers the sprite-DMA wraparound case where
sprite 3 fetches its data at the start of a raster line and therefore requires
BA lead-in at the end of the previous line. Use the same VICE capture and
normalized comparison shape shown above, substituting the PRG and output names.
For color and priority timing, also check the output color histogram because the
normalizer deliberately ignores palette differences while comparing geometry.

For cycle-phase debugging without BASIC tokenization or command injection in
the path, run the same PRG from its machine-code entry point:

```powershell
C64Emulator\bin\x64\Release\C64Emulator.exe `
  --run-prg-sys `
  artifacts\vic-fixtures\vic-raster-bars.prg `
  2061 `
  8000000 `
  artifacts\vic-raster-compare\emu-prg-sys-direct.log `
  artifacts\vic-raster-compare\emu-prg-sys-direct.ppm `
  2000000
```

The log records the final raster/cycle position plus the first several
thousand `$D011`, `$D016`, and `$D020` writes, which makes VICE screenshot
differences easier to separate into true chip timing drift versus screenshot
crop/palette differences.

## Render Savestate Frames

For VIC-II visual debugging, render any local `.c64sav` without opening the UI:

```powershell
C64Emulator\bin\x64\Release\C64Emulator.exe `
  --render-savestate `
  "$env:APPDATA\C64Emulator\saves\save-20260511-000007.c64sav" `
  1 `
  artifacts\vic-save.ppm `
  artifacts\vic-save.log
```

The log records the final raster position, CPU/VIC-adjacent debug state, drive
state, and framebuffer hash. This is the preferred path for turning observed
sprite/raster glitches into repeatable golden tests.

Golden manifests can also start from a savestate via `saveStatePath`. Use
[golden-manifest.savestate.sample.json](golden-manifest.savestate.sample.json)
as a local template, enable the test, point it at a save, run `--golden-run`,
then accept the resulting frame hash once the rendering is known-good.

## Accept A Baseline

When a run is considered the reference for a manifest, accept it into a copy:

```powershell
.\scripts\run-golden-reference.ps1 `
  -Manifest docs\golden-manifest.sample.json `
  -OutputDirectory artifacts\golden-reference `
  -Accept `
  -AcceptedManifest artifacts\golden-reference\accepted-manifest.json
```

If a manifest already contains specific expectation keys, only those keys are refreshed. If it has no expectations yet, the accept step records the frame hash and a small set of stable scalar properties: global cycle, raster line, cycle in line, PC, and ST.

## Compare Two Result Files

```powershell
C64Emulator\bin\x64\Debug\C64Emulator.exe `
  --golden-compare `
  artifacts\reference\golden-results.json `
  artifacts\candidate\golden-results.json `
  artifacts\candidate\golden-compare.log
```

The comparison checks every actual hash and scalar property present in the reference result.

## Capture A VICE Reference Screenshot

Install VICE and either put `x64sc` on `PATH`, set `VICE_X64SC`, or pass `-VicePath`.

```powershell
.\scripts\capture-vice-reference.ps1 `
  -VicePath "C:\Tools\GTK3VICE\bin\x64sc.exe" `
  -Cycles 10000000 `
  -OutputDirectory artifacts\vice-reference `
  -Name pal-boot-10m
```

For a PRG or D64:

```powershell
.\scripts\capture-vice-reference.ps1 `
  -MediaPath "%USERPROFILE%\Documents\C64Emulator\some-test.prg" `
  -Cycles 200000 `
  -OutputDirectory artifacts\vice-reference `
  -Name some-test-200000
```

The script uses VICE `x64sc`, `-limitcycles`, `-exitscreenshot`, and `-autostart` for media runs. It writes a screenshot, log, and JSON metadata containing the screenshot SHA-256.
On the local GTK3/DirectX VICE build, short cycle limits can produce an all-black exit screenshot even though VICE started correctly. The helper defaults to 10,000,000 cycles and rejects blank screenshots unless `-AllowBlankScreenshot` is passed for diagnostics.

To compare a VICE screenshot against an emulator frame while ignoring palette and outer screenshot crop differences:

```powershell
.\scripts\compare-vice-frame.ps1 `
  -ViceFrame artifacts\vice-reference\pal-boot-10m.png `
  -EmulatorFrame artifacts\phase4\regression_boot.ppm `
  -OutputDirectory artifacts\vice-compare `
  -Name pal-boot-10m
```

The comparison reports each frame's detected inner display box and writes a JSON report plus a normalized diff PNG. A mismatch count of zero means the 320x200 display classes line up after crop/palette normalization.

## Recommended Test Sets

- Boot and BASIC idle baselines for PAL timing.
- Small PRGs that write memory signatures after raster IRQ, badline, sprite DMA, CIA timer, and TOD tests.
- Known community test suites such as VICE testprogs and Lorenz-style VIC/CIA tests, if their licenses fit the repository.
- D64 loader samples that cover standard KERNAL LOAD, disk swap, custom drive code, and fast-loader handoff.

Do not commit large third-party test suites blindly. Prefer small curated manifests and document the source/license of each external test asset.
