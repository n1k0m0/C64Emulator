# C64Emulator

Ein in C# geschriebener Commodore-64-Emulator mit eigenem Rendering-Frontend und laufender 1541-/IEC-Emulation.

## Features

- MOS 6510 CPU, VIC-II, SID, CIA und Systembus
- PAL-orientiertes Timing mit Raster-/Sprite-Unterstuetzung
- D64- und PRG-Medienhandling
- 1541-Laufwerke auf dem IEC-Bus, inklusive Drive-LED-Anzeige
- OpenTK-basiertes Fenster-/Rendering-Frontend ueber `SharpPixels`

## Projektstruktur

```text
C64Emulator.sln
README.md
C64Emulator/
  C64Emulator.csproj
  Core/
  lib/
SharpPixels/
  SharpPixels.csproj
  Shaders/
```

## Voraussetzungen

- Windows
- .NET SDK mit Unterstuetzung fuer klassische .NET-Framework-Projekte
- .NET Framework 4.7.2 Developer Pack, falls Visual Studio/MSBuild es nicht bereits installiert hat

## Build

```powershell
dotnet build C64Emulator.sln -c Release -p:Platform=x64
```

Die ausfuehrbare Datei liegt danach unter:

```text
C64Emulator/bin/x64/Release/C64Emulator.exe
```

## Medien

`.prg`-Dateien werden direkt geladen. `.d64`-Images werden als Diskette in ein emuliertes 1541-Laufwerk eingelegt. Standardmaessig ist Laufwerk 8 aktiv; weitere Laufwerke koennen ueber das Emulator-Menue verwendet werden.

## Hinweise

Die ROM-Dateien liegen im Projektordner `C64Emulator/` und werden beim Build in das Ausgabeverzeichnis kopiert. Wenn dieses Repository oeffentlich veroeffentlicht wird, sollten die Rechte an den ROM-Dateien vorher geprueft werden.

## Lizenz

Der Emulator-Code steht unter der Apache License, Version 2.0. Siehe [LICENSE](LICENSE).

ROM-Dateien und Disk-/Programm-Medien sind nicht Teil dieser Lizenz.
