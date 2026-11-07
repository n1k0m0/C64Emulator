# Emulator-Erweiterungen für C64 freie ROMs RC3

Ziel ist [n1k0m0/C64Emulator](https://github.com/n1k0m0/C64Emulator), Commit
`f01750e17a2c3f4cabfd57dc0c470dac93a91a23`. Die Dateien unter `overrides/`
enthalten den vollständigen geänderten Quelltext. Die Patches wenden dieselben
Änderungen auf drei definierte Ausgangsstände an.

## Einen Installationsweg auswählen

| Ausgangsstand | Datei |
| --- | --- |
| Unveränderter Referenz-Commit | `0001-c64-free-roms-rc3.patch` |
| RC2-Datei-I/O-Erweiterung plus RC2-RESTORE-Erweiterung | `0002-rc2-to-rc3.patch` |
| RC2-Datei-I/O-Erweiterung ohne die damals optionale RESTORE-Erweiterung | `0003-rc2-without-restore-to-rc3.patch` |

Im Emulator-Checkout zunächst `git apply --check /pfad/zum/gewählten.patch`
ausführen und anschließend `git apply /pfad/zum/gewählten.patch`. Genau einen
der drei Patches verwenden; alle enthalten am Ende denselben RC3-Stand. Danach
den Emulator normal bauen und die vier Dateien aus `../build/emulator-roms/`
gemeinsam einsetzen.

Bei einem abweichenden eigenen Quellstand ist die Prüfung mit `git apply --check`
entscheidend. Die vollständigen Quelldateien dienen dann als nachvollziehbare
Integrationsvorlage, nicht als Aufforderung, eigene Änderungen blind zu ersetzen.

## Welche Dateien wohin gehören

Die Pfade beziehen sich auf den Stamm des Emulator-Repositories:

| Quelldatei in `overrides/` | Zielpfad | Zweck |
| --- | --- | --- |
| `D64Image.cs` | `C64Emulator/Media/D64Image.cs` | PRG/SEQ/USR/REL, BAM, Side-Sektoren, Dateibefehle und persistente D64-Änderungen |
| `IecDrive1541.cs` | `C64Emulator/Iec/IecDrive1541.cs` | IEC-Dateikanäle, binäre Befehle, direkte Puffer und Laufwerkscode-Handoff |
| `C64System.cs` | `C64Emulator/Machine/C64System.cs` | Taktweitergabe, externe Pins pro Zyklus, Tape-API, RESTORE und Modellprüfung von Savestates |
| `C64Model.cs` | `C64Emulator/Machine/C64Model.cs` | PAL und NTSC6567R8, Kommandozeilenauswahl |
| `SystemBus.cs` | `C64Emulator/Machine/SystemBus.cs` | Kassettenport, CIA1-FLAG und User-Port-Anbindung |
| `TapImage.cs` | `C64Emulator/Machine/TapImage.cs` | TAP-Parser/-Writer für Zeitintervalle |
| `TapeDeck.cs` | `C64Emulator/Machine/TapeDeck.cs` | Zyklusgesteuerter Bandtransport und Aufzeichnung |
| `UserPort.cs` | `C64Emulator/Machine/UserPort.cs` | Serielle Ein-/Ausgangspins und Gegenstellen-Callbacks |
| `Cia1.cs`, `Cia2.cs` | `C64Emulator/Cia/` | FLAG-Flanken, Modell-TOD-Takt und User-Port-Pegel |
| `Vic2.cs`, `VicBusPlan.cs` | `C64Emulator/Vic/` | 63-/65-Zyklen-Raster und passender Bildausschnitt |
| `Sid.cs` | `C64Emulator/Sid/Sid.cs` | SID-Abtastung aus dem ausgewählten CPU-Takt |
| `MountedMediaKind.cs` | `C64Emulator/Media/MountedMediaKind.cs` | TAP als Medienart |
| `Program.cs` | `C64Emulator/Program.cs` | Interaktiver Start mit `--video pal|ntsc` |
| `C64Window.cs` | `C64Emulator/C64Window.cs` | TAP im Browser/Drop und F10-Bandmenü; Aufnahmen bleiben bei Power-Reset erhalten |

`TapImage.cs`, `TapeDeck.cs` und `UserPort.cs` sind neue Dateien. Das bestehende
SDK-Projekt nimmt sie automatisch in den Build auf. Die Projektdateien des
Emulators müssen dafür nicht geändert werden.

## Verhalten und Nachweise

Normale Laufwerksoperationen verwenden weiterhin das Software-IEC-Laufwerk.
Die echte C64-CPU führt BASIC/KERNAL und die CIA2-Signale aus; Load-Hack und
KERNAL-IEC-Hooks sind in den Tests deaktiviert. Dateisystemoperationen und REL-
Side-Sektoren werden vom Host implementiert. Hochgeladener Code über B-E/M-E/
U3–U8 läuft auf der tatsächlichen emulierten 1541-CPU und besitzt nun einen
gültigen RTS-Rückweg zum Hilfs-ROM.

Dateiänderungen werden zuerst als Kandidat erstellt, in eine temporäre Datei
neben dem Ziel geschrieben und geflusht und erst nach erfolgreichem Dateiersatz
im Host veröffentlicht. Fehler behalten den vorherigen Zustand. Der dokumentierte
REL-Overflow ist eine beabsichtigte Ausnahme: Der passende Prefix wird geschrieben,
und Status51 meldet den abgeschnittenen Rest. Dies ist atomare Sichtbarkeit je
Befehl, keine zugesagte Transaktion über mehrere Kanäle oder Stromausfallsicherung
der gesamten Host-Dateisystemhierarchie.

TAP-Aufzeichnungen messen WRITE-Flanken. Die Wiedergabe erzeugt CIA1-FLAG-Flanken
und ersetzt keine LOAD-/SAVE-ROMroutine. Die Oberfläche stellt F10→TAPE sowie
`.tap` im Browser und Drag-and-drop bereit. RS-232-Signalpartner können über die
UserPort-API integriert werden; eine automatische Verbindung zu einem physischen
Host-COM-Port ist damit nicht verbunden.

Die ausführbaren Prüffälle, Ergebniszahlen, Prüfsummen und Grenzen sind in
`../docs/VALIDATION.md` zusammengeführt. Die vollständige Windows-Anwendung wurde
mit .NET10 für Windows kompiliert; die emulierte Maschine läuft für die
Integrationsprüfungen im plattformneutralen Testprogramm. Ein interaktiver
Windows-Desktoplauf wird nicht behauptet.

## Patches reproduzieren

Aus dem ROM-Projekt:

```sh
python emulator-patches/build_patches.py \
  --reference /pfad/zum/unveraenderten/C64Emulator \
  --rc2 /pfad/zum/RC2/emulator-patches/overrides \
  > results/emulator-patch-verification.json
```

Der Generator prüft den Commit und den unveränderten Referenzinhalt, erzeugt
alle drei Patches, wendet sie in getrennten temporären Verzeichnissen an und
vergleicht das Ergebnis bytegenau mit allen16 Override-Dateien. Der originale
Checkout wird dabei nicht geändert. Die JSON-Ausgabe enthält Patch- und
Quelltext-Prüfsummen.

## Kompatibilitätsgrenzen

Die Befehlsmatrix in `../docs/dos-commands.md` ist maßgeblich. Standard-D64-
Allokation umfasst35 Spuren; UI- für VIC-20-Timing und DUPLICATE werden ausdrücklich
abgewiesen. Private Commodore-ROM-Einsprünge, beliebige Fastloader sowie ein
vollständig autonomes 1541-ROM sind kein zugesagter Bestandteil.

Der neue Savestate-Header identifiziert das Modell vor dem Einlesen der RAM-
Daten. PAL- und NTSC-Zustände dürfen nicht miteinander verwechselt werden.
Ältere PAL-Zustände bleiben lesbar; ältere Zustände ohne Modellkennung werden
in NTSC ausdrücklich abgewiesen. Der neue Zustand trägt auch die Tape- und
REL-Kanaldaten. Einzelheiten zu Grenzen der Peripherie stehen in
`../docs/tape-serial-video.md`.
