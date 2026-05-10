# Phase 4 Developer Tools

Stand: 2026-05-10

## Live Overlay

Der interne Entwickleroverlay zeigt kompakte Laufzeitdaten:

- Rasterline, Cycle, Global Cycle
- BA-Pending und CPU-Blockierung
- CPU-PC/Opcode
- Speicherbanking
- CIA, SID, IEC und Drive-8 Kurzstatus

Hinweis: `F8` ist im normalen Emulatorfenster fuer den Laufwerks-Footer reserviert. Der Entwickleroverlay bleibt als Diagnoseansicht im Code erhalten, ist aber nicht mehr auf `F8` gelegt.

## Headless Trace

```powershell
C64Emulator\bin\x64\Release\C64Emulator.exe --trace-cycles 20000 artifacts\phase4\trace_cycles.csv 63
```

Parameter:

- `cycles`: Anzahl emulierter Zyklen
- `logPath`: CSV-Ziel
- `sampleInterval`: Sampling-Abstand in Zyklen

Die CSV enthaelt CPU/VIC/CIA/SID/IEC/Drive-Snapshots und ist fuer Regressionen, Issue-Anhaenge und Vergleichslaeufe gedacht.

## Regression Runner

```powershell
C64Emulator\bin\x64\Release\C64Emulator.exe --regression-run "" 500000 artifacts\phase4\regression_boot.log artifacts\phase4\regression_boot.ppm
```

Parameter:

- `mediaPath`: leer, `.prg` oder `.d64`
- `cycles`: Anzahl emulierter Zyklen nach Mount/Start
- `logPath`: Textlog mit finalem CPU/VIC/CIA/SID/IEC/Drive-Status
- `framePath`: optionaler PPM-Framebuffer

D64-Medien werden gemountet und mit `LOAD"*",8` angestossen. Der Log enthaelt zusaetzlich `FrameSHA256`, damit Golden-Master-Vergleiche auch ohne Bilddiff moeglich sind.

## Smoke Script

```powershell
.\scripts\run-phase4-checks.ps1
```

Das Script baut Release, fuehrt `--accuracy-tests`, `--trace-cycles` und `--regression-run` aus und schreibt Artefakte nach `artifacts\phase4`.
