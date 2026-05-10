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
