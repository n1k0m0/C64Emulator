# Phase 3 Accuracy Plan

Stand: 2026-05-10

## Ziel

Phase 3 soll Accuracy-Arbeit reproduzierbar machen. Der neue `--accuracy-tests` Runner prueft heute interne Timing-Invarianten fuer VIC-II, CIA, SID und 1541-Transport. Externe Test-ROMs und Golden-Master-Artefakte sollen danach schrittweise angebunden werden.

## Bereits umgesetzt

- Headless CLI: `C64Emulator.exe --accuracy-tests [logPath]`
- Phase-3-Smoke-Script: `scripts/run-phase3-checks.ps1`
- VIC-II: PAL-Frame-Timing und Busplan-Golden-Slots fuer Refresh, Char-Fetch, Sprite-Pointer und Sprite-DMA-BA-Fenster.
- CIA 6526: Timer-A Continuous/One-Shot, Timer-B-Underflow-Quelle und PAL-TOD-Tenth.
- SID: Voice-3-Envelope-Gate-Regression fuer Attack/Release.
- 1541: Runtime-Umschaltung zwischen Software-IEC und ROM-Transportmodus als Regression.

## Naechste Test-ROM-Suites

| Bereich | Kandidaten | Geplanter Vergleich |
| --- | --- | --- |
| VIC-II | VICE testprogs, Lorenz VIC tests, eigene Badline/Raster/Sprite PRGs | Screenshot-Golden plus Raster-/IRQ-Log |
| CIA | VICE CIA tests, eigene Timer-/TOD-/IRQ-Race-PRGs | Speicher-Signatur und IRQ-Zeitpunkt |
| CPU | vorhandener Opcode-Selftest plus externe 6502/6510 Funktionstests | Speicher-Signatur und Cycle-Trace |
| SID | ADSR-/Waveform-PRGs, spaeter Audio-Snippets | Register-/Envelope-Signatur, spaeter Audio-Golden |
| 1541 | DOS directory/load/save Tests, Fastloader-Kandidaten | LOAD-Status, Drive-PC/IEC-Trace, Speicher-Signatur |

## Golden-Master-Format

- `artifacts/phase3/*.log` fuer maschinenlesbare Timing-/Register-Ergebnisse.
- Spaeter `artifacts/phase3/screenshots/*.png` fuer VIC-Golden-Screens.
- Spaeter `artifacts/phase3/audio/*.wav` oder kurze PCM-Hashes fuer SID-Regressionen.

## G64/NIB-Plan

1. D64-Pfad stabil halten: Datei-/Directory-/PRG-LOAD-Regressionsfaelle zuerst.
2. Disk-Image-Abstraktion erweitern: Track/Sector-D64 und Bitstream-G64/NIB ueber ein gemeinsames Interface.
3. 1541-Mechanik an Bitstream koppeln: Rotation, Sync-Mark-Erkennung, GCR-Decode, Write-Protect.
4. Fastloader-Tests erst nach stabiler ROM-Transport-Basis aufnehmen.
5. Kopierschutzfaelle als separate, optionale Suite halten, weil sie rechtlich und technisch empfindlicher sind.
