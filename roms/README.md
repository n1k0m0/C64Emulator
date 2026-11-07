# C64 freie ROMs — 2026.11.07-rc4

Dieser Stand enthält die freien ROMs und die dazugehörigen Emulator-Erweiterungen
für [n1k0m0/C64Emulator](https://github.com/n1k0m0/C64Emulator), geprüft auf dem
Quellstand `f01750e17a2c3f4cabfd57dc0c470dac93a91a23`.
BASIC/KERNAL, Zeichensatz und 1541-Helfer sind vollständig aus den mitgelieferten
Quellen baubar. Die neuen Emulatorfunktionen werden als überprüfbare Patches
und vollständige geänderte Quelldateien geliefert.

## Neu in RC4

- **Startbildschirm wie im Original:** Open Basic zeigt das Boot-Banner im
  Original-Bannerformat als `**** OPEN BASIC ****` (vorher
  `**** FREE BASIC 64 ****`) und zentriert es wie das Original auf dem
  40-Spalten-Screen: Die 20 Zeichen des Titels starten exakt mittig, also mit
  zehn führenden Leerzeichen. Wie im Original liegt die Zentrierung auf der
  BASIC-Seite: Der Cold-Start-Vektor `$A000` springt in eine 18-Byte-Routine
  am Ende des BASIC-Haupt-ROMs (`$BFEB–$BFF8`), die zuerst einen
  Wagenwechsel und dann die zehn Leerzeichen über denselben Ausgabe-Kanal wie
  den Bannertext ausgibt und anschließend `INIT` betritt. Der gesamte
  Startbildschirm entspricht damit Zeile für Zeile dem Original (gegen echte
  COMMODORE-ROMs bytegenau abgeglichen): Zeile eins leer, Zeile zwei das
  Banner, Zeile drei leer, Zeile vier ` 64K RAM SYSTEM  38911 BASIC BYTES
  FREE` mit führendem Leerzeichen wie im Original, Zeile fünf leer,
  Zeile sechs `READY.` und der Cursor auf Zeile sieben.
- **I/O-Fehlermeldungen (bewusste Abweichung):** Um im BASIC-Haupt-ROM und im
  1216-Byte-Bereich `$E000–$E4BF` Platz für die dem Original folgenden
  Startzeilen zu schaffen, wurden fünf kurze eigene I/O-Fehlertexte gekürzt:
  `NOT INPUT FILE` → `NO INPUT`, `NOT OUTPUT FILE` → `NO OUTPUT FILE`,
  `MISSING FILE NAME` → `MISSING FILE`, `ILLEGAL DEVICE NUMBER` → `BAD DEVICE`
  und `FILE NOT OPEN` → `NOT OPEN`. Die Fehlernummern und alle anderen
  Meldungen bleiben unverändert.
- Zeichensatz und 1541-Helfer sind unverändert. Der Gesamtnachweis (14
  isolierte CPU-/ROM-Suites, PAL- und NTSC-Nativlauf, bytegleicher
  Doppel-Build) wurde mit dem neuen ROM-Satz wiederholt.

## Neu in RC3

- **Bildschirmeditor:** Verlegte Bildschirmbereiche über `$0288`, einschließlich
  Löschen, Scrollen, Einfügen, Überschreiben, Zeilenerfassung und PLOT. Der echte
  Timer-IRQ zeichnet einen blinkenden Cursor und stellt Zeichen/Farbe darunter
  wieder her. Serielle Zugriffe erhalten eine vom Programm gewählte VIC-Bank.
- **REL und DOS:** REL-Dateien mit echten Daten- und Side-Sektoren auf D64,
  binärer Record-Positionierung und den zugehörigen Fehlerzuständen. Dazu kommen
  NEW/Format, Scratch, Rename, Copy, Validate, Initialize, direkte Block- und
  Pufferbefehle sowie Memory-/User-Befehle mit tatsächlicher Laufwerks-CPU-
  Ausführung. Die vollständige Befehlsmatrix steht in `docs/dos-commands.md`.
- **Kassette und serielle Schnittstelle:** Ein zyklusgesteuertes TAP-Laufwerk
  verbindet die 6510-Motor-/WRITE-Pins mit dem CIA1-FLAG-Eingang. Der Emulator
  besitzt Bandtransport und TAP-Speicherung. Die RS-232-Anbindung stellt die
  User-Port-Pins einschließlich RTS, CTS, DTR, DSR und DCD bereit.
- **NTSC:** Auswahl eines 6567R8-Modells mit angepasstem Raster, Busbelegung,
  Bildausschnitt, CIA- und SID-Takt. Das ROM erkennt PAL/NTSC am echten Raster
  und stellt seine Systemuhr entsprechend ein. PAL bleibt die Voreinstellung.

Der genaue bestandene Umfang, die zugehörigen ROM-Prüfsummen und die verbleibenden
Grenzen stehen in `docs/VALIDATION.md`. RC3 ist ein geprüfter Integrationsstand;
Programme mit privaten ROM-Einsprüngen und besondere Fastloader brauchen eigene
Kompatibilitätstests. Reale Bandhardware ist hier nicht angeschlossen und wird
nicht als getestet ausgewiesen.

## Installation

### 1. Passenden Emulator-Patch anwenden

Die neuen Hostfunktionen benötigen den Emulator-Patch zusätzlich zu den ROMs.
Wähle genau **einen** Weg entsprechend deinem Ausgangsstand:

| Ausgangsstand | Patch |
| --- | --- |
| Unveränderter oben genannter Emulator-Commit | `emulator-patches/0001-c64-free-roms-rc3.patch` |
| RC2-Datei-I/O-Patch und damaliger RESTORE-Patch vorhanden | `emulator-patches/0002-rc2-to-rc3.patch` |
| RC2-Datei-I/O-Patch vorhanden, damaliger RESTORE-Patch fehlt | `emulator-patches/0003-rc2-without-restore-to-rc3.patch` |

Beispiel für einen unveränderten Checkout:

```sh
git apply --check /pfad/zum/rom-paket/emulator-patches/0001-c64-free-roms-rc3.patch
git apply /pfad/zum/rom-paket/emulator-patches/0001-c64-free-roms-rc3.patch
```

Danach den Emulator wie üblich mit .NET 10 neu bauen. Die Patches wurden auf dem
angegebenen Commit geprüft; bei einem eigenen abweichenden Quellstand zeigt
`git apply --check`, ob sie direkt passen. Alle drei Wege enthalten die
RESTORE-Tastenanbindung und führen zum selben RC3-Quelltext. Weitere Details
und die Dateizuordnung stehen in `emulator-patches/README.md`.

### 2. Vier zusammengehörige ROM-Dateien einsetzen

Vorhandene ROMs sichern und diese vier Dateien aus `build/emulator-roms/`
gemeinsam in den ROM-Ordner des Emulators kopieren:

| Datei | Größe | Inhalt |
| --- | ---: | --- |
| `c64-basic-kernal.bin` | 16.384 Bytes | BASIC und passender KERNAL |
| `c64-character.bin` | 4.096 Bytes | Großschrift/Grafik, Kleinschrift, inverse Zeichen |
| `1541-c000-rom.bin` | 8.192 Bytes | Untere Hälfte des freien 1541-Helfers |
| `1541-e000-rom.bin` | 8.192 Bytes | Obere Hälfte des freien 1541-Helfers |

Der Referenz-Emulator sucht unter Windows unter anderem in
`%APPDATA%\C64Emulator\roms`; ein ausdrücklich angegebener ROM-Pfad hat Vorrang.
Den Emulator danach vollständig neu starten. BASIC und KERNAL bilden ein Paar:
BASIC verwendet zusätzlich `$E000–$E4BF`, die PAL/NTSC-Erkennung belegt
`$E4C0–$E4FF`. Teile aus unterschiedlichen Releases nicht mischen.
Die getrennten ROMs und Symboltabellen liegen in `build/roms/`, ihre
SHA-256-Prüfsummen in `build/manifest.json`.

### 3. Modell und Laufwerksmodus

PAL startet wie bisher. NTSC lässt sich beim Start auswählen:

```powershell
C64Emulator.exe --video ntsc
```

`--ntsc` ist eine Kurzform; `--video pal` beziehungsweise `--pal` wählt PAL.
Das Modell gilt für die interaktive Maschine. Ältere spezielle Entwickler-
Prüfprogramme des ursprünglichen Emulators können eigene fest gewählte Modelle
verwenden; für die RC3-Prüfungen dient der mitgelieferte Testläufer.

Der getestete normale Dateibetrieb verwendet das Software-IEC-Laufwerk:

```csharp
var options = C64AccuracyOptions.Compatibility;
options.EnableLoadHack = false;
options.EnableKernalIecHooks = false;
// ForceSoftwareIecTransport = true
```

Dabei führt die C64-CPU die tatsächlichen seriellen ROM-Routinen aus. Die
CIA2-Pins kommunizieren mit der Hostimplementierung des 1541-Dateisystems.
Der 1541-Helfer bleibt auf diese Architektur abgestimmt.

## Benutzung der Ergänzungen

### Bildschirm und Cursor

`POKE 648,seite` legt die physische Bildschirmseite des Editors fest. Dazu muss
das Programm den VIC-Speicherbereich über `$DD00` und `$D018` passend einstellen
und den verwendeten RAM für Bildschirm und gegebenenfalls Zeichensatz freihalten.
Der Editor verändert diese Hardwareauswahl bei normalen Ausgaben nicht.
CINT setzt `$0288` auf `$04` und die VIC-Textregister auf Standardwerte;
die CIA2-VIC-Bank bleibt dabei unverändert.

Farb-RAM bleibt an `$D800`. Clear bearbeitet genau 1.000 Zellen und erhält die
Sprite-Pointer am Ende eines Bildschirmbereichs. Der Cursor verwendet die
öffentlichen Zustandsbytes `$CC–$CF` und `$0287`. `POKE 204,1` unterdrückt ihn;
ein sichtbares Cursorzeichen wird spätestens beim nächsten System-IRQ entfernt.
`POKE 204,0` aktiviert ihn wieder. Das steuert den aktuellen Zustand: Jeder neue
Tastatur-CHRIN-Eingabesammler aktiviert das Blinken erneut. Ein am READY-Prompt
eingegebenes `POKE 204,1` schaltet den Cursor deshalb nicht dauerhaft ab.
Während einer langen Bildschirmoperation laufen Uhr- und Tastatur-IRQs weiter,
sofern der Aufrufer IRQs nicht gesperrt hat.

### REL-Datei

Die Record-Länge ist ein binäres Byte, kein ausgeschriebener Zahlenwert. Dieses
BASIC-Programm schreibt und liest Record 13 einer Datei mit 32-Byte-Records:

```basic
10 OPEN 15,8,15
20 OPEN 2,8,2,"CONTACTS,L,"+CHR$(32)
30 PRINT#15,"P";CHR$(98);CHR$(13);CHR$(0);CHR$(1)
40 PRINT#2,"ALICE,12345"
50 PRINT#15,"P";CHR$(98);CHR$(13);CHR$(0);CHR$(1)
60 INPUT#2,N$,T$
70 PRINT N$;T$
80 CLOSE 2:CLOSE 15
```

Die Positionierfolge besteht aus `P`, Kanal plus 96, Record-Lowbyte,
Record-Highbyte und optionaler Position im Record. Nicht vorhandene Records
melden Fehler 50, können anschließend aber durch Schreiben angelegt werden.
Zu lange Inhalte melden Fehler 51 und laufen nicht in den nächsten Record.
Die Record-Länge kann 1 bis 254 Bytes betragen; Daten- und Side-Sektoren werden
auf dem tatsächlichen D64 persistiert.

### DOS-Befehle

Der Statuskanal ist Kanal 15. Beispiele für eine eingelegte beschreibbare D64:

```basic
OPEN 15,8,15
PRINT#15,"R:NEU=ALT"
PRINT#15,"S:TEMP*"
PRINT#15,"C:KOPIE=QUELLE"
PRINT#15,"V"
CLOSE 15
```

`N:DISK,01` formatiert die Standardspuren und ersetzt deren bisherige Dateien;
`N:DISK` führt eine schnelle Neuinitialisierung mit erhaltener Disk-ID durch.
Die genaue Syntax und Grenzen aller Befehle, einschließlich direkter Blöcke,
stehen in `docs/dos-commands.md`. DOS-Status immer auswerten: READY allein beweist
keinen erfolgreichen Schreibvorgang. Die früheren PRG-/SEQ-/USR- und Append-
Funktionen bleiben enthalten.

### Kassette/TAP

TAP-Dateien lassen sich über den Medienbrowser oder Drag-and-drop öffnen.
Der Emulator legt das Band ein und setzt PLAY; anschließend lädt das ROM
normal über `LOAD` beziehungsweise `LOAD"NAME",1`.

**F10 → TAPE** bietet PLAY, STOP, REWIND, NEW RECORDING und SAVE TAP.
Für eine Aufnahme zuerst NEW RECORDING wählen, im C64 `SAVE"NAME",1` ausführen
und anschließend SAVE TAP wählen. Es werden die tatsächlich erzeugten
WRITE-Pulsabstände gespeichert. REWIND und PLAY können die Aufnahme direkt
wiedergeben. Kalt-, Warm-, Reload- und Frontend-Power-Reset erhalten das Band,
seine Position und auch eine noch nicht gespeicherte Aufnahme.

Ein fehlgeschlagener TAP-Dateiimport erhält das vorherige Band. Fehlgeschlagene
Dateispeicherung erhält die Aufnahme im Speicher und räumt die temporäre Datei
auf. Neue Aufnahme und Medienauswurf sind die ausdrücklichen Wege zum Leeren
des Bandes. Die Windows-Oberfläche ist kompiliert und statisch geprüft; ihre
Bedienung wurde hier nicht auf einem laufenden Windows-Desktop getestet.

Bandoperationen erhalten die lesbaren Anzeigebits 0–6 von `$D011` und die
Sprite-Freigaben in `$D015`. Eigene Raster-IRQ-Programme sollten ihren
Rastervergleich danach neu setzen; das nur schreibbare Vergleichsbit in
`$D011` kann aus dem Register nicht zurückgelesen werden.

### RS-232

Der User-Port stellt Daten- und Handshake-Pins für eine programmierbare
Gegenstelle bereit. Geprüft sind Senden und Empfangen mit 50, 300, 1200 und
2400 Baud, Drei-Leitungsbetrieb, Full-/Half-Duplex-Handshakes, verzögerte oder
fehlende Freigaben sowie Paritäts- und Rahmenfehler auf PAL und NTSC.
Der ROM-Treiber bleibt synchron: Er besitzt keinen asynchronen Hintergrund-
FIFO und bindet nicht automatisch einen Host-COM-Port an. Physische
RS-232-Adapter sind nicht getestet. Pinbelegung, API und genaue Grenzen stehen
in `docs/tape-serial-video.md`.

### RUN/STOP und RESTORE

**Tab** entspricht RUN/STOP, **Bild auf / Page Up** entspricht RESTORE.
Tab halten und Bild auf drücken führt über die ROM-NMI-Routine zu BASIC zurück
und erhält das Programm. Details stehen in `emulator-patches/RESTORE.md`.

## Quellen und Lizenzen

Der Interpreter basiert auf Microsofts offiziell unter MIT veröffentlichtem
6502 BASIC 1.1 mit C64-Anpassungen. Die bereitgestellten eigenen KERNAL-,
Zeichensatz- und 1541-Quellen sowie die neuen Host-Erweiterungen tragen
Apache-2.0. Alle relevanten Lizenztexte und die genaue Herkunft stehen in
`NOTICE.md` und `licenses/`. Keine proprietäre C64-ROM-Datei wird als Build-
Eingabe benötigt.

Der 1541-Helfer bleibt bytegleich mit dem eigenen ursprünglich gelieferten
Hilfs-ROM. Die Dateisystem-Erweiterung liegt im Software-Laufwerk des Emulators;
das ist der normale Betriebsmodus dieses Zielprojekts. Details zu den
Laufwerks-Einsprungadressen stehen in `docs/drive-integration.md`.

## Selbst bauen und prüfen

Voraussetzungen: Python 3.10+, `ca65` und `ld65` aus cc65. Bauen:

```sh
python build.py
# Alternativ: python build.py --ca65 /pfad/ca65 --ld65 /pfad/ld65
```

Isolierte CPU-Prüfungen:

```sh
python -m pip install -r requirements-test.txt
python run_tests.py
```

Native Prüfung mit dem Referenz-Checkout und .NET 10:

```sh
dotnet build tests/emulator/RomIntegration.csproj -c Release \
  -p:EmulatorSource=/absoluter/pfad/C64Emulator \
  -p:EmulatorPatchDirectory=/absoluter/pfad/rom-paket/emulator-patches/overrides \
  -o build/native-tests

dotnet build/native-tests/RomIntegration.dll build/emulator-roms \
  --model pal --suite all --json results/native-pal.json

dotnet build/native-tests/RomIntegration.dll build/emulator-roms \
  --model ntsc --suite machine --json results/native-ntsc.json
```

Die Tests kompilieren den wirklichen CPU-/VIC-/CIA-/SID-/IEC-Kern des Emulators.
Nur der Host-Audioausgabekanal ist stumm. Direkte Medien-/Transport-API-Prüfungen
sind in den Ergebnissen gesondert gruppiert; sie werden nicht als ROM-Ausführung
ausgegeben. Jede JSON-Ausgabe enthält die geladenen ROM-Prüfsummen.

## Verbleibende Grenzen

- Reale Datasette, Bandmechanik, analoge Signalqualität und physische RS-232-
  Adapter sind nicht angeschlossen. Die bestandenen Tests betreffen die
  emulierten Pins und die ausdrücklich dokumentierten Signalfolgen.
- NTSC bezeichnet hier den 6567R8. Frühere 6567R56A-Varianten, PAL-N sowie
  sämtliche VIC-Spezialeffekte sind keine pauschal geprüften Zielmodelle.
- Das 1541-Hilfs-ROM ist keine autonome Firmware für eine physische 1541 oder
  für einen reinen VIA/GCR-Laufwerksmodus. Private ROM-Einsprünge und Fastloader
  werden nicht allgemein ersetzt.
- Die DOS-Dateiallokation gilt für die 35 Standardspuren einer D64. Zusätzliche
  Spuren bleiben erhalten. Namensneuanlage folgt der vorhandenen 16-Zeichen-
  ASCII-Abbildung des Hosts. `UI-` für VIC-20-Timing ist ausdrücklich nicht
  unterstützt; DUPLICATE besitzt auf einer einzelnen 1541 keine Funktion.
- Öffentliche BASIC-Tokens, wichtige RAM-Konventionen und KERNAL-Vektoren sind
  implementiert. Jede private Einsprungadresse und jeder mehrdeutige
  Bildschirmcode-/PETSCII-Alias wird dadurch nicht automatisch identisch.

Weitere Einschränkungen und die konkreten Prüfergebnisse stehen in
`docs/VALIDATION.md` und `docs/tape-serial-video.md`.
