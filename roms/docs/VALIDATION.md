# Validierung — C64 freie ROMs 2026.11.07-rc4

## Ergebnis und geprüfter Umfang

Der ausgelieferte Stand besteht **14 von 14 isolierten CPU-/ROM-Testsuiten**,
**279 von 279 Prüfungen im PAL-Gesamtlauf** und **131 von 131 Prüfungen im
NTSC-Maschinenlauf**. Keine Prüfung in diesen drei abschließenden Läufen ist
fehlgeschlagen. Ein zweiter ROM-Build erzeugt **8 von 8 bytegleiche Dateien**.
Alle drei Emulator-Patchwege ergeben dieselben 16 RC3-Quelldateien.
Der native Testläufer und die vollständige Windows-Anwendung kompilieren
jeweils mit **0 Warnungen und 0 Fehlern**.

Die Integrationssummen enthalten neben ROM-Ausführung auch ausdrücklich
ausgewiesene Host- und Komponentenprüfungen. Die 148 zusätzlichen PAL-Fälle
prüfen das modellunabhängige D64-Dateisystem; sie wurden im NTSC-Lauf nicht
erneut ausgeführt. Die folgende Gruppentabelle trennt diese Aussagebereiche.
Prüfgruppen können mehrere Eingaben und Randfälle enthalten. Ihre Anzahl ist
keine Anzahl unabhängig getesteter C64-Programme.

Damit sind verlegte Bildschirmbereiche, blinkender Cursor, REL/DOS,
vollständige emulierte TAP-Wellenformabläufe, die unten genannten RS-232-
Handshakes und das NTSC-Modell Teil des bestandenen Umfangs. Physische
Datasette-/RS-232-Hardware und eine interaktiv gestartete Windows-Oberfläche
sind weiterhin **nicht** als getestet ausgewiesen.

Die maschinenlesbare Zusammenfassung ist
[`results/release-verification.json`](../results/release-verification.json).
Sie bindet die Ergebnisse an ROMs, Quellen, Testläufer und Patch-Prüfsummen.

## Getestete Konfiguration

| Bestandteil | Stand |
| --- | --- |
| Emulator | `n1k0m0/C64Emulator`, Commit `f01750e17a2c3f4cabfd57dc0c470dac93a91a23` |
| Ergänzungen | Die 16 Quelldateien in `emulator-patches/overrides/`; Zuordnung im dortigen README |
| PAL | 985248 CPU-Takte/s, 312 Rasterzeilen × 63 Takte, Bildpuffer 403 × 284 |
| NTSC | Modell 6567R8, 1022727 CPU-Takte/s, 263 Rasterzeilen × 65 Takte, Bildpuffer 418 × 235 |
| CPU/VIC/CIA/SID/IEC | Tatsächlicher Emulator-Code mit den gelieferten Ergänzungen |
| Host-Audioausgabe | Stumm; SID-Erzeugung und Taktung werden weiter ausgeführt |
| `EnableLoadHack` | `false` |
| `EnableKernalIecHooks` | `false` |
| Laufwerk | C64-KERNAL über echte emulierte CIA2-IEC-Pins zum Software-1541-Dateisystem |
| Kassette | 6510-Motor/WRITE/SENSE, tatsächliche CIA1-FLAG-Flanken und Timer |
| RS-232 | CIA2-User-Port-Pins mit unabhängiger zeitgesteuerter Gegenstelle |
| .NET | SDK 10.0.100 |
| Isolierte CPU | `py65==1.2.0` |
| BASIC-Assembler | ca65/ld65 aus cc65 Commit `77b6484615427746e55fda2755c9cbc320a1fb51` |

Der ursprüngliche Referenz-Checkout bleibt unverändert. Die nativen Prüfungen
ersetzen keine CPU-Instruktion, KERNAL-Funktion, Bandpulsprimitive oder serielle
Byteroutine. BASIC-Kommandos werden über den Eingabeweg des Testläufers
zugeführt; die gezielten Tastatur-, RUN/STOP- und RESTORE-Fälle verwenden die
tatsächliche Tastenabbildung und CIA1-Kontakte. Der öffentliche ROM-Eintritt
wird nur beobachtet. Drei einzelne RESTORE-Tastendrücke ergeben drei NMIs;
das Halten der Taste erzeugt keine zusätzliche Folge von NMIs.

## Isolierte CPU-/ROM-Tests

| Suite | Ergebnis und Gegenstand | Bericht |
| --- | --- | --- |
| BASIC-Semantik | 63/63: Start, Rechnen, Vergleiche, Strings, Arrays, DATA/READ, Schleifen, Unterprogramme, Fehler | `results/basic.json` |
| BASIC-I/O | 58/58: tatsächlicher Parser, Kanäle, SYS, LOAD-Zustand und genaue CR-/LF-Bytes einschließlich LFN 127/128 | `results/basic-io.json` |
| BASIC-Speicher/Ablauf | 8/8: String-Garbage-Collection, Arrays, verschachtelte FOR, FN, TI/TI$, CONT und Stack | `results/basic-stress.json` |
| BASIC-Kompatibilität | 30/30: sechs RAM-Vektoren, Handler-Verkettung, Fehlernummern, USR, STOP/CONT und Warmstart | `results/basic-compatibility.json` |
| System | 20/20: öffentliche KERNAL-Aufrufe, STOP, IRQ/NMI/BRK, RAM-erhaltender Warmstart und CBM80-Vektoren | `results/system.json` |
| KERNAL | 8/8 Gruppen: Register, Stack, Ausgabe, Scrollen, Farbe, Zeichensatz, Uhr, Eingabepuffer | `results/kernal.json` |
| Tastatur | 19/19 Gruppen, darin 480 Tasten-/Modifikatorkombinationen; Wiederholung, Puffer und Zeichensatzumschaltung | `results/keyboard.json` |
| Bildschirmsteuerung | 62 zitierte Steuerzeichen sowie Quote-/Reverse-Zustände, DELETE und Umbruch | `results/screen.json` |
| Bildschirmeditor | 10/10 Gruppen: Zeilenerfassung, INSERT/DELETE mit Farben, 80-Zeichen-Grenze, Scrollen, Warmstart und Sprite-Pointer | `results/editor.json` |
| Editor RC3 | 11/11 Gruppen: verlegte Seiten, Zeiger, Farbe, IRQ-Cursor, Untergrund und CIA2-Bankerhalt | `results/editor-rc3.json` |
| Bandheader/EOT | 14/14: relative/absolute Header, SA 0–3, Byte-/Blockkodierung, Duplikate, XOR und EOT | `results/tape-header.json` |
| RS-232 | 90/90 Sende-/Empfangsfälle mit 50–2400 Baud und mehreren Daten-/Paritäts-/Stopbitformaten | `results/rs232.json` |
| Zeichensatz | 10/10 Testmethoden: Codepunkte, Geometrie, Textbänke und inverse Zeichen | `results/logs/characters.log` |
| 1541-Helfer | 8/8 Testmethoden: Build, feste Einsprünge, GCR, Prüfsummen, CPU-Jobs und IRQ-Rückkehr | `results/logs/drive.log` |

[`results/summary.json`](../results/summary.json) enthält die 14 Suite-Ergebnisse
und ihre ROM-Prüfsummen. Diese CPU-Tests verwenden teilweise gezielte
Geräte-Testpartner. Ihre älteren Bandtests ersetzen die Pulsprimitive, ihre
RS-232-Fälle verwenden ideale PAL-Rahmen. Der zusätzliche native RC3-Nachweis
für vollständige Pulsfolgen und reale emulierte Pin-Zeiten steht im nächsten
Abschnitt; er wird nicht aus den isolierten Tests abgeleitet.

## Native Integration und direkte Komponentenprüfungen

| JSON-Gruppe | PAL bestanden | NTSC bestanden | Ausführung und Aussage |
| --- | ---: | ---: | --- |
| `boot` | 1 | 1 | Tatsächlicher ROM-Start bis BASIC/READY |
| `video` | 13 | 13 | Gemischte Kern-/Komponenten- und native Prüfungen: Raster, IRQ, Busplätze, SID, Modellwahl, ROM-Uhr, letzte Textzeile, Savestate |
| `video-sprites` | 2 | 2 | Tatsächliche Sprite-Pixel/Kollisionen sowie Kollision hinter geschlossenem Rand |
| `basic` | 16 | 16 | Programme, Ausgaben, Fehlererholung und CIA1-Tastatur |
| `control` | 5 | 5 | RUN/STOP, CONT, RESTORE, Programm-/Variablenerhalt und NMI-Flanken |
| `editor` | 3 | 3 | INSERT/DELETE, bearbeitete LIST-Zeile mit überprüften Tokenbytes, Sprite-Pointer |
| `editor-rc3` | 8 | 8 | Verlegter Bildschirm, sichtbare VIC-Cursorpixel, Blinkphasen, LIST und Bankerhalt bei IEC |
| `iec` | 12 | 12 | Native PRG-LOAD/SAVE/RUN, Kanäle, EOI, Fortsetzung und Programm-LOAD |
| `data` | 7 | 7 | Native SEQ-/USR-Dateien, Append, INPUT#/GET#, Fehler und Ersetzen |
| `dos` | 10 | 10 | Native REL-Positionierung, Status, DOS-Dateibefehle, Puffer/Blöcke, Memory, echte hochgeladene Laufwerksprogramme |
| `tape-format` | 3 | 3 | Direkte TAP-Container-/Transportprüfungen: Grenzen, Halbwellenschema, Taktumrechnung |
| `tape` | 18 | 18 | Native SAVE/LOAD/VERIFY/EOT-Wellenformen und unabhängige Auswertung ihrer erzeugten Daten |
| `serial` | 21 | 21 | Native KERNAL-Aufrufe mit unabhängiger Gegenstelle an den emulierten User-Port-Pins |
| `tape-host` | 12 | 12 | Direkte öffentliche Host-APIs: Mount, Speichern, Fehlererhalt, Transport, Resets, Savestate/Callbacks |
| `media` | 14 | — | Modellunabhängige direkte D64-PRG-Allokation/Persistenz |
| `media-data` | 52 | — | Modellunabhängige direkte PRG-/SEQ-/USR-Streams und Fehlerfälle |
| `dos-media` | 82 | — | Modellunabhängige direkte DOS-/REL-/Puffer-/IEC-Zustandsprüfungen |
| **Gesamt** | **279** | **131** | **Jeweils 0 Fehler** |

Die vollständigen Einzelresultate stehen in
[`native-pal.json`](../results/native-pal.json) und
[`native-ntsc.json`](../results/native-ntsc.json). `native-emulator.json` ist
eine bytegleiche Kopie des PAL-Berichts für ältere Verweise. Direkte API-Tests
werden nicht als durch die CPU gelaufene BASIC-/KERNAL-Aufrufe ausgegeben.
Die `video`-Gruppe enthält beide Testarten; Einzelname und Erwartung zeigen
jeweils den Vertrag. Eine Rückkehr zu READY allein gilt nie als Nachweis eines
erfolgreichen Schreibvorgangs.

### Bildschirm und Cursor

Clear, Ausgabe, Scrollen, INSERT/DELETE, Zeilenerfassung und PLOT verwenden
die physische Seite aus `$0288`. Farb-RAM bleibt `$D800`; Clear bearbeitet
genau 1.000 Zellen und erhält die 24 nachfolgenden Bytes mit Sprite-Pointern.
Normale Editoroperationen erhalten `$DD00`/`$D018`. CINT setzt `$0288` auf
`$04` und die VIC-Textregister auf Standardwerte; die CIA2-VIC-Bank bleibt
unverändert. Der Aufrufer muss VIC-Bank, Bildschirmspeicher und Zeichensatz
passend konfigurieren.

Der Cursor blinkt durch tatsächliche Timer-A-IRQs, erhält Zeichen und Farbe
unter sich und entfernt seine Markierung auch beim Wechsel der Bildschirmseite.
Die öffentlichen Zustandsbytes `$CC–$CF` und `$0287` sind angebunden.
Ein neuer Tastatur-CHRIN-Eingabesammler aktiviert den Cursor wieder. Lange
Ausgaben sperren den Cursorzustand, ohne zuvor freigegebene System-IRQs über
die gesamte Ausgabe zu blockieren. IEC-/RS-232-Zugriffe erhalten die gewählte
VIC-Bank. Native Tests prüfen die vom VIC erzeugten Pixel.

### REL, DOS und Fehlererhalt

REL verwendet echte D64-Daten- und Side-Sektoren, Record-Längen von 1–254,
binäre Positionierbefehle und die Fehler 50/51. Eine unabhängig konstruierte,
nicht zusammenhängende REL-Datei prüft das Einlesen fremder Dateistrukturen.
Der große Fall enthält 601 Datensektoren plus sechs Side-Sektoren und
überschreitet jede Grenze von 120 Indexeinträgen.

NEW/Format, Scratch, Rename, Copy, Validate, Initialize, direkte Blöcke/Puffer
sowie Memory-/User-Befehle sind in
[`dos-commands.md`](dos-commands.md) einzeln spezifiziert. B-E, M-E und U3–U8
führen wirklichen Code auf der emulierten Laufwerks-CPU aus und kehren über
einen gültigen RTS-Rahmen zum Host-Kommandoablauf zurück. Direkte Pufferlesezugriffe
sehen zwischenzeitliche Änderungen des Laufwerks-RAM auch ohne erneutes B-P.
Ein verzögerter IEC-Listener prüft, dass der Talker vor dessen Bereitschaft
keine verfrühten Datentakte ausgibt.

Erfolgreiche Änderungen werden auf einer neu geöffneten Host-D64 anhand
konkreter Bytes, Ketten und BAM-Belegung geprüft. Schreibschutz, Platzmangel,
defekte Ketten und Host-Speicherfehler erhalten die vorherige Datei. Der
gezielte REL-Überlauf ist die dokumentierte Ausnahme: Der passende Präfix
wird gespeichert, der Rest verworfen und Fehler 51 gemeldet. Das ist
Transaktionsschutz pro Befehl, keine Garantie gegen Stromausfall des Hosts.

### Band-Wellenformen und RS-232

SAVE wird aus den wirklichen 6510-WRITE-Flanken aufgezeichnet. Ein unabhängiger
Decoder prüft die sechs Header-/Daten-/EOT-Blöcke. Ein unabhängiger Generator
liefert eine C2N-Pulsfolge, die LOAD/VERIFY über CIA1-FLAG und Timer liest;
zusätzlich wird eine vom ROM aufgezeichnete TAP wiedergegeben. Der Test mit
257 Bytes umfasst alle 256 Bytewerte und einen Seitenübergang. Prüfsummen-
und Paritätsfehler in der ersten Kopie, beschädigte Duplikate, benannte und
unbenannte Type-5-EOT-Suche sowie abgeschnittene Pulsfolgen sind enthalten.

Diese Fälle starten mit Bildschirm und allen Sprites eingeschaltet. Das ROM
muss deren DMA für den Bandtransfer selbst behandeln und auf Erfolgs- und
Fehlerpfaden die lesbaren Anzeigebits, Sprite-Freigaben, Motorzustand und
IRQ-Zustand wiederherstellen. Vier TAP-Dateien pro Modell liegen unter
`results/tapes-pal/` und `results/tapes-ntsc/`; ihre Prüfsummen stehen im
Release-Bericht. Die unabhängig erzeugte PAL-TAP wird auch unter NTSC mit
Taktumrechnung gelesen.

Die 21 nativen RS-232-Fälle pro Modell prüfen 50/300/1200/2400 Baud, TX/RX,
3-Leitungsbetrieb, Full-/Half-Duplex-Handshakes, verzögertes/fehlendes CTS,
DSR-Verlust, verzögertes/fehlendes DCD, Paritäts-/Stopfehler, READST-Quittierung,
CLOSE-Leitungsfreigabe sowie erhaltene Register und IRQ-Zustände. Die
Gegenstelle erzeugt und misst Pin-Zeiten unabhängig vom ROM-Treiber.
Savestates erhalten die User-Port-Objektidentität und angeschlossene Callbacks.

### PAL/NTSC und Frontend

Das ROM erkennt die tatsächliche Rasterfolge, setzt `$02A6` und taktet seine
CIA1-Systemuhr mit 60 Jiffies pro Sekunde. VIC-Busplätze, späte Spritezugriffe,
Bildbereich, CIA-, SID-, Laufwerks- und Bandtakt folgen dem gewählten Modell.
Ein behobener NTSC-Fehler hielt den sichtbaren Bildversatz fälschlich für die
physische Spritezeile; Pixel- und Randkollisionstests decken diese Stelle ab.
Savestates prüfen die Modellkennung vor einer Änderung des Maschinen-RAM.

Die Windows-Anwendung wurde mit dem ursprünglichen Projekt und sämtlichen
16 geänderten Quellen als Windows-Ziel auf Linux gebaut. NTSC-Menüabmessungen
und TAP-Menüpfade wurden statisch geprüft. Die 12 Hostfälle testen die
darunterliegenden APIs, einschließlich Erhalt ungespeicherter Aufnahmen bei
Power-Reset und atomarer Ablehnung fehlerhafter Mounts. Ein interaktiver
Windows-Desktoptest gehört nicht zum bestandenen Umfang.

## Reproduzierbarkeit, Patches und Prüfsummen

`results/reproducible-build.json` dokumentiert die acht bytegleichen ROMs;
`build/manifest.json` enthält ihre Größen und die verwendeten Quelltext-Hashes.
`results/build-verification.json` hält Testquellen, Overrides und den tatsächlich
ausgeführten nativen Testläufer fest. Die abschließende Releaseprüfung gleicht
diese Hashes erneut mit den ausgelieferten Dateien ab.

| Ausgangsstand | Geprüfter Patch |
| --- | --- |
| Unveränderter Referenz-Commit | `0001-c64-free-roms-rc3.patch` |
| RC2-Datei-I/O und RESTORE | `0002-rc2-to-rc3.patch` |
| RC2-Datei-I/O ohne RESTORE | `0003-rc2-without-restore-to-rc3.patch` |

Jeder Weg wurde in einer getrennten Kopie mit `git apply --check` geprüft,
angewendet und anschließend bytegenau mit den 16 Overrides verglichen.
Alle enthalten RESTORE und ergeben denselben Stand. Das Ergebnis steht in
`results/emulator-patch-verification.json`; wähle bei der Installation genau
den passenden Weg. Ein eigener abweichender Emulator-Quellstand benötigt
einen erneuten Anwendbarkeitstest.

| Emulator-Datei | SHA-256 |
| --- | --- |
| `c64-basic-kernal.bin` | `9690fc91c9b835e91f9c31002d6bf615e16b285821b5808eb77027c9c68ed0f9` |
| `c64-character.bin` | `ce3dd32cf840a76300756cefddaa52b3544147945e7305356d811ea0325a74fb` |
| `1541-c000-rom.bin` | `312d072774e52fbe4adfbbe86ff338668294ba6d7c2ce5381ef39a1e182b0686` |
| `1541-e000-rom.bin` | `a5720a5a1d5e90a21aaa76f3fb2ce08a1118726a85a05fa69ee51846a989bdfc` |

Der getrennte KERNAL hat SHA-256
`9e805616d5362799dc3666524c8b9834519a773e66353c20457374183fa72cfc`.
BASIC und KERNAL unterscheiden sich gegenüber RC3 nur durch das geänderte
Start-Banner `**** OPEN BASIC ****`; Zeichensatz und 1541-Helfer bleiben
bytegleich.
BASIC und KERNAL müssen als zusammengehöriges Paar eingesetzt werden:
Die BASIC-Erweiterung belegt `$E000–$E4BF`, der neue Timing-Helfer `$E4C0–$E4FF`.
Der Build prüft die Bereichsgrenzen. Keine proprietäre ROM-Datei wird als
Build- oder Testeingabe benötigt.

Wiederholungsbefehle stehen in der
[README](../README.md#selbst-bauen-und-prüfen) und im
[Testläufer-README](../tests/emulator/README.md).

## Verbleibende Grenzen

- **Physische Geräte:** Keine reale Datasette, kein elektrischer RS-232-Adapter
  und keine reale 1541 wurden angeschlossen. Analoge Pegel, Bandgeschwindigkeit,
  Kopfjustage, Motoranlauf und Kabellasten benötigen einen separaten Hardwarelauf.
  Ein konkretes Prüfprotokoll steht in [tape-serial-video.md](tape-serial-video.md).
- **RS-232:** Der Treiber bleibt synchron. Full-/Half-Duplex beschreibt die
  Leitungsprotokolle, keine parallel arbeitenden Hintergrund-FIFOs. Es gibt
  keine automatische Host-COM-Anbindung. Der eigene Zustand einer externen
  Gegenstelle wird bei einem Maschinen-Savestate nicht zurückgesetzt.
- **Band/VIC:** TAP v1 trägt den vollständig geprüften nativen Wellenformpfad.
  TAP v0 enthält bei Nullbytes keine genaue Überlauflänge; der Parser verwendet
  deterministisch das Minimum. C64-markierte v2-Halbwellen haben einen
  Komponententest, keine allgemeine C16-Kompatibilitätszusage. Nach Bandoperationen
  müssen eigene Raster-IRQ-Programme gegebenenfalls neu gesetzt werden:
  `$D011`-Bit 7 liest den aktuellen Rasterstand und nicht das schreibbare
  Vergleichsbit; erhalten werden die lesbaren Bits 0–6 und `$D015`.
- **NTSC:** Geprüft ist das 6567R8-Modell. Andere VIC-Revisionen, analoge
  Videosignale und sämtliche undokumentierten Randtricks wurden nicht allgemein
  gegen reale Chips validiert. Ältere Spezial-Testprogramme des ursprünglichen
  Emulators können intern weiterhin ein festes Modell wählen.
- **Laufwerk:** Der 1541-Teil setzt das Software-IEC-Dateisystem des Ziel-Emulators
  voraus. Er ist keine autonome vollständige 1541-Firmware. Die Allokation
  verwendet die 35 Standardspuren; zusätzliche D64-Spuren bleiben erhalten,
  werden aber nicht neu vergeben. Dateinamen folgen dem vorhandenen Host-
  Namensvertrag, nicht dem vollständigen PETSCII-Zeichenvorrat. `UI-` meldet den
  nicht implementierten VIC-20-Timingmodus ausdrücklich als Fehler; DUPLICATE
  hat auf der einzelnen 1541 keine entsprechende Laufwerksfunktion.
- **Allgemeine Kompatibilität:** Private BASIC-/KERNAL-/1541-Einsprünge,
  beliebige Fastloader und ein breiter Spiele-/Demo-Bestand sind nicht
  nachgewiesen. Private Editortabellen und alle mehrdeutigen PETSCII-Aliasse
  werden nicht vollständig reproduziert; Ghosting und Joystick-Kollisionen
  sind nicht Teil der Tastaturmatrixprüfung.
- **Programm-LOAD:** Variablen bleiben an ihren bisherigen RAM-Adressen.
  Das nachgeladene Programm muss ausreichend freien Platz haben; überlappende
  Variablenbereiche werden nicht automatisch verschoben oder gerettet.

Diese Grenzen beschreiben den tatsächlichen Nachweisumfang dieses RC4-Pakets;
sie ändern die oben aufgeführten bestandenen Ergebnisse nicht.
