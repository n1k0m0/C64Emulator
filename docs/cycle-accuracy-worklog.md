# Cycle Accuracy Worklog

This document records the first implementation pass for the cycle-accuracy roadmap.

## Phase 1: Accuracy Mode And Golden Runs

- Added `C64AccuracyOptions` with separate compatibility and accuracy profiles.
- The accuracy profile disables emulator shortcuts: LOAD hack, KERNAL IEC hooks, software IEC transport, host input injection, and parked drive CPU behavior.
- Added an external golden-test harness under `C64Emulator/Golden`.
- Added `--golden-run <manifest.json> [outputDir]`, producing JSON and JUnit XML results.
- Golden tests can now compare frame hashes, scalar CPU/VIC properties, RAM ranges, and drive RAM ranges.

## Phase 2: Structured Machine Trace

- Added `--trace-machine <cycles> [jsonl] [sampleInterval] [accuracy|compatibility]`.
- The JSONL trace includes VIC timing, BA/AEC state, bus owner, CPU trace access type/address/value, subsystem debug fields, and scheduler state.
- This trace is intended for comparing emulator behavior against VICE snapshots, custom probes, or future hardware-derived references.

## Phase 3: CPU/Bus Arbitration

- Replaced the old access-type-only prediction with `CpuBusAccessPrediction`.
- CPU prediction now returns access type, address, and value.
- Prediction now uses side-effect-free CPU-visible bus peeks, so operand fetches see real RAM/ROM bytes without mutating I/O registers or bus latches.
- The C64 bus arbiter records the prediction used for BA/AEC decisions, and machine traces include it.

## Phase 4: VIC-II Pipeline Diagnostics

- Added `VicPipelineState` and exposed it through `C64System`.
- Machine traces now include structured VIC graphics pipeline state: badline matrix-fetch gates, VC/VMLI/RC, display source bases, line mode flags, and sequencer validity.
- Added an accuracy test for badline C-access request/block timing.

## Phase 5: CIA 6526 Timer Rules

- Added shared `Cia6526TimerRules` for CIA1 and CIA2.
- Unified interrupt-mask writes, timer reload-after-underflow behavior, timer B count source handling, and force-load behavior.
- Added parity coverage for CIA1/CIA2 latch-zero force-load behavior.

## Phase 6: 1541 Accuracy Scheduler

- Accuracy mode now drives the 1541 hardware CPU continuously instead of only while high-level IEC work is active.
- Added `DriveSchedulerState` and machine-trace output for target/executed drive cycles, continuous-mode state, drive PC, and transport readiness.
- Added an accuracy test proving the drive CPU advances in accuracy mode while remaining parked in compatibility mode.

## Current Limits

- This is not full cycle accuracy yet. It is the scaffolding and first correctness pass needed to make future chip-level work measurable.
- The VIC-II still needs deeper validation against known raster test suites.
- CIA TOD/alarm edge cases and serial port behavior need more coverage.
- The 1541 path now has an accuracy mode, but true disk rotation, GCR timing, VIA edge timing, and ROM execution fidelity still need dedicated passes.
- SID remains mostly functional rather than cycle-accurate.

## Recommended Next Work

- Add a curated golden manifest with small public-domain PRGs that exercise raster IRQs, badlines, sprites, CIA timers, and IEC loading.
- Use `--golden-accept`, `--golden-compare`, and `scripts/capture-vice-reference.ps1` to collect accepted baselines and VICE screenshot references.
- Replace CPU bus prediction with an explicit micro-cycle access table per opcode once the trace suite is rich enough.
- Extend 1541 accuracy toward VIA timer/edge timing and GCR bitstream scheduling.
- Add per-frame performance counters for compatibility vs. accuracy mode.

## Night Pass: VICE And Disk LOAD Baselines

- Wired the local `GTK3VICE-3.10-win64` tools into the reference workflow.
- Fixed `scripts/capture-vice-reference.ps1` so VICE's expected cycle-limit exit still writes metadata instead of failing the capture.
- Added `scripts/create-golden-fixtures.ps1`, which uses VICE `petcat` and `c1541` to generate a deterministic D64 fixture from BASIC source text.
- Added `docs/golden-manifest.disk-load.json` for a standard D64 `LOAD"AA",8` relocation regression. The fixture deliberately stores a PRG load address of `$2000`; the emulator must load it at BASIC start `$0801` for secondary address zero.
- Extended golden runs with requested RAM/drive-RAM hash arguments and optional RAM dumps so LOAD-status and memory signatures can be captured without adding new one-off command-line runners.
- Added a 1541 stepper/motor smoke test as the first explicit mechanism-level regression before deeper VIA/GCR timing work.

## VIC-II Visual Accuracy Pass

- Added `--render-savestate <save.c64sav> <frames> <frame.ppm> [log]` so local game saves can be rendered headlessly into deterministic framebuffer artifacts.
- Rendered the latest Zak/Maniac saves under `artifacts/vic-save-renders*` and recorded frame hashes/logs for visual regression work.
- Corrected the pixel path to use the current/fetch-latched display source for horizontal and vertical scroll phase instead of the raster-line-start latch. This closes a class of mid-line `$D016/$D011` split glitches where games change scroll/display mode during active display.
- Corrected horizontal border timing so CSEL is taken from the live `$D016` register at the border comparisons, while graphics fetch/source selection remains fetch-latched. Added regressions for mid-line `$D016` scroll splits and pre-fetch CSEL border changes.
- Added `scripts/compare-vice-frame.ps1` to compare VICE screenshots against emulator PPM/PNG frames after normalizing crop and palette. The 10M-cycle PAL boot reference currently matches the emulator's 320x200 inner display with zero normalized mismatches.
- Corrected vertical border timing so RSEL is taken from the live `$D011` register at the border comparisons, while graphics source selection remains fetch-latched. Added a regression for mid-frame `$D011` RSEL changes.
- Re-rendered all local `.c64sav` frames after the RSEL pass under `artifacts/vic-save-renders-after-rsel`. Existing overlapping Zak/Maniac save renders remained byte-identical to the previous border pass.
- Split VIC badline condition from latched badline state. The current-cycle condition still drives render gating and BA lead-in timing, while the line state now remains visible to diagnostics after a post-cycle-14 `$D011` change has already started DMA. Added a regression for this D011 edge case and rechecked the VICE 10M-cycle boot comparison at zero normalized mismatches.
- Added `mountAfterWarmup` support to golden runs and `scripts/create-vic-fixtures.ps1` for generated local raster-bar PRGs. The fixture now executes in the emulator through the golden runner and in VICE through autostart, exposing the next class of sub-cycle border color/write-phase differences without relying on game disks or saves.
- Added a render-visible VIC register phase. CPU writes update the architectural registers immediately, while pixel composition reads a separate render register copy at a controlled phase. The current calibrated phase is the start of the render cycle, which reduced the local raster-bar mismatch against VICE while preserving the zero-mismatch PAL boot baseline.
- Extended the direct PRG/SYS trace runner so generated raster fixtures can bypass BASIC and log `$D011`, `$D016`, and `$D020` writes through the full VICE comparison window. The 10M-cycle raster-bar trace shows the final-frame writes at the expected PAL raster/cycle positions; the remaining left-edge visual difference is explained by VICE's narrower screenshot crop rather than a loader or BASIC `SYS` timing issue.
- Switched sprite enable and sprite priority pixel composition to the render-visible VIC register copy as well, matching the border/background path and preventing mid-cycle sprite register writes from bypassing the calibrated pixel phase.
- Added generated sprite VIC fixtures. `vic-sprite-baseline.prg` gives a stable 24x21 sprite reference and currently matches VICE after crop/palette normalization with zero mismatches. `vic-sprite-enable-phase.prg` deliberately toggles `$D015` on a sprite line and is best used through `--run-prg-sys` because VICE autostart and emulator golden runs do not share a guaranteed CPU phase anchor.
- Fixed the VICE frame comparison script's PPM reader for black top-left emulator frames and changed dimension mismatches so they no longer report as a misleading zero-pixel mismatch.
- Added `vic-sprite-irq-phase.prg`, a raster-IRQ anchored sprite enable probe with a border marker. This exposed that the emulator was treating `$D015` as a live pixel gate and was also terminating an already-started sprite DMA on the next line. VICE keeps the current sprite DMA/render instance alive after `$D015` is cleared. The VIC-II path now latches sprite enable at DMA start and lets active sprite DMA continue until its normal end; the IRQ probe now compares against VICE with zero normalized mismatches.
- Added IRQ-anchored sprite X and color probes. `vic-sprite-x-irq-phase.prg` confirms that the current sprite DMA instance keeps its latched X coordinate when `$D000` changes in the IRQ, while `vic-sprite-color-irq-phase.prg` exposed that sprite color `$D027+n` is live during pixel composition rather than latched with the sprite DMA instance.
- Updated sprite pixel composition so the sprite shape, X/Y position, expansion, and data bytes remain latched for the active DMA/render instance, but the sprite's own color reads the render-visible `$D027+n` register at pixel time. Hires and multicolor sprite color IRQ probes now match VICE with zero normalized mismatches and matching white/red sprite-pixel counts.
- Added `vic-sprite-multicolor-enable-irq-phase.prg`, which exposed that `$D01C` sprite multicolor enable is live during pixel composition. The emulator now reads `$D01C` from the render-visible register copy for sprite pixel decoding; the probe matches VICE with zero normalized mismatches and the expected 384 red / 120 white split.
- Added `vic-sprite-priority-irq-phase.prg` as a foreground-overlap probe for `$D01B`. It confirms that priority timing needs a dedicated absolute sprite-Y/IRQ-phase pass rather than a broad sprite-start offset change; the attempted global render-start adjustment regressed the existing sprite probes and was not kept.
