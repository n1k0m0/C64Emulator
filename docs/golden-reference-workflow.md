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
  -Cycles 50000 `
  -OutputDirectory artifacts\vice-reference `
  -Name pal-boot-50000
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

## Recommended Test Sets

- Boot and BASIC idle baselines for PAL timing.
- Small PRGs that write memory signatures after raster IRQ, badline, sprite DMA, CIA timer, and TOD tests.
- Known community test suites such as VICE testprogs and Lorenz-style VIC/CIA tests, if their licenses fit the repository.
- D64 loader samples that cover standard KERNAL LOAD, disk swap, custom drive code, and fast-loader handoff.

Do not commit large third-party test suites blindly. Prefer small curated manifests and document the source/license of each external test asset.
