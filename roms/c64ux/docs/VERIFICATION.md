# C64/UX 0.2.0 — Prüfbericht

Stand: 6. November 2026. Geprüft wurde das im Paket enthaltene ROM-Paar.
Die SHA-256-Werte der jeweiligen Testläufe stehen in den JSON-Protokollen.

## Ergänzung: aktueller C64Emulator 0.3.21

Der unveränderte Emulator-Kern von Commit
`07dbdf14e018cce37bf7e55805330d4b87e7e242` wurde am 9. November 2026
erneut mit dem C64/UX-ROM-Paar ausgeführt. Die 40 Shell-Szenarien bestehen;
zwei SEQ-Operationen bleiben ausdrücklich `limited`, da die zugehörige
Diskettenpersistenz nicht nachgewiesen ist. Die 36 Spiel- und Eingabeszenarien
bestehen ebenfalls, darunter ROM RUNNER und das unveränderte SERPENT 64.
Der Testläufer verwendet für diesen Lauf ein explizites ROM-Verzeichnis,
damit lokal installierte Original-ROMs die Auswahl nicht überlagern.
Die vier neuen Boot-/Shell-Screenshots in der Haupt-README zeigen tatsächlich
ausgeführte Emulator-Frames; die aktuelle Windows-Menüoberfläche wurde
nicht automatisch visuell geprüft.

Die folgenden historischen Prüfberichte bleiben an den darunter genannten
früheren Commit gebunden.

## Ergebnis

**Shell, RAM-Dateisystem, Skripte, physische Tastatureingabe und der neue
Spielmodus funktionieren im unveränderten öffentlichen C64Emulator-Kern.**
ROM RUNNER und das unveränderte SERPENT 64 v1.0.4 wurden über `play` von D64
angespielt. Die automatische Prüfung beobachtet sowohl echte Framebuffer als
auch Spielzustand im emulierten RAM; die Spielsteuerung erfolgt durch die
physische CIA-Tastaturmatrix.

Die C64/UX-Binärdateien lassen sich aus den beigefügten Quellen und den zwei
übernommenen Binärassets bytegenau neu bauen. Classic-ROMs und SERPENT 64 sind
unverändert kopierte Distributionsdateien und nicht Teil dieser Quellreproduktion.
Der kombinierte C64/UX-ROM-Hash der finalen Tests lautet:

```text
403ce869e4903918c48d6a7da9f466380981690118c82107e12b4ba362e5de49
```

Die SEQ-Dateioperationen haben eine ausdrücklich getrennte Prüfaussage: Ihre
ROM-Logik besteht kontrollierte KERNAL-Dateischnittstellentests. Der ältere
öffentliche Emulatorstand unterstützt die zugehörigen normalen SEQ-Kanäle
nicht vollständig; ein erfolgreicher SEQ-Schreib-/Lese-Rundlauf auf deinem
neueren lokalen Stand ist **nicht** nachgewiesen.

| Prüfung | Umfang | Ergebnis / Grenze |
| --- | --- | --- |
| Unabhängige CPU-/Systemtests | 25 Testfälle auf py65, tatsächliche ROM-Instruktionen | Bestanden |
| Kontrollierte Dateischnittstelle | 10 Testfälle, tatsächliche Shell-/FS-Instruktionen, deterministische KERNAL-Dateiendpunkte | Bestanden; keine Aussage über IEC-Signale oder echte Diskettenpersistenz |
| IEC-Wartepfade | 6 Testfälle mit tatsächlichen KERNAL-Instruktionen und festen CIA2-Leitungszuständen | Bestanden, einschließlich Stack-Balance |
| PLAY-Dateischnittstelle | 14 Testfälle mit echter CPU-Ausführung und kontrollierten KERNAL-Dateiendpunkten | Bestanden; unter anderem Originaladresse, SYS-Erkennung, RAM unter ROM und Fehler beim zweiten Lesen |
| Echter C64Emulator-Kern, Shell | 40 Befehlsszenarien, zusätzlich Start- und Ergebnisauswertung | Shell/FS/PRG-Anforderungen erfüllt; SEQ-Grenzen als `limited` dokumentiert |
| Echter C64Emulator-Kern, Spiele | 36 Szenarien, 25 Ergebniskontrollen | Bestanden; gewöhnlicher IEC-Weg und physische Spieltasten |
| Klassisches Profil im echten Kern | 11 bestandene Prüfungen, eine bekannte Eingabegrenze | BASIC und ROM RUNNER funktionieren im geprüften Pfad; LOAD mit Anführungszeichen als PETSCII-Puffereingabe |
| Sauberer Neuaufbau | Separates Verzeichnis, nur mitgelieferte Quellen, Assets und Python-Standardbibliothek | 14 Artefakte bytegleich; keine Assemblierung außerhalb der beiden ROM-Plätze |

Die Testzahlen bezeichnen unterschiedliche Prüfarten. Die Integrations-
szenarien enthalten auch reine Beobachtungen und erwartete Fehlerfälle; sie sind
keine Behauptung, dass sämtliche Diskettenfunktionen erfolgreich arbeiten.

## Artefakte und Größen

| Abbild | Dateigröße | Belegter Programmbereich |
| --- | ---: | --- |
| `open-basic64.bin` | 8.192 Byte | 7.661 tatsächlich assemblierte Byte einschließlich PLAY-Kommando |
| `open-kernal64.bin` | 8.192 Byte | 6.727 tatsächlich assemblierte Byte einschließlich geschütztem Spiel-Lader |
| `c64-basic-kernal.bin` | 16.384 Byte | Exakte Verkettung beider Images |
| `c64-character.bin` | 4.096 Byte | Unveränderte Kopie des mitgelieferten Zeichensatzes |

Die Belegung beider ROM-Plätze wird aus dem Schreib-Bitmap des Assemblers
ermittelt, nicht durch Zählen von `$FF`-Bytes. Im BASIC-Platz sind 531 Byte und
im KERNAL-Platz 1.465 Byte frei, verteilt auf Lücken. Der Shell-Kern endet vor
der PLAY-Reservierung bei `$BE00`. `$C000–$DFFF` enthält null assemblierte Byte.
Alle Images halten ihre feste Dateigröße ein. Es wird keine zusätzliche Bank
und kein geänderter Adressdecoder verwendet.

## 1. Unabhängige Ausführung auf py65

Programm: `tests/test_rom.py`. Protokoll: `docs/rom-tests.json`.

Der Testrechner modelliert 6510-ROM-Banking, RAM unter ROM, CIA-Timerinterrupts,
die Tastaturmatrix und Bildschirm-RAM. Shellbefehle werden nicht durch Python
nachgebaut oder ersetzt. Er ist ein gezieltes Testmodell und keine vollständige,
taktgenaue C64-/1541-Emulation.

Geprüft werden unter anderem:

- ROM-Geometrie, Start-/Warmstartvektoren und öffentliche KERNAL-Sprünge.
- Boot auch mit zufällig vorbelegtem RAM.
- Rückkehr zur Eingabe nach leeren, unbekannten oder syntaktisch falschen Befehlen.
- Kommando-Dispatch unabhängig von Seitengrenzen der ROM-Stringadressen.
- Groß-/Kleinschreibung, einfache/doppelte Quotes, wörtliches `>` und `>>`.
- Acht-Argument-Grenze und vollständiges Verwerfen überlanger Eingaben.
- Tatsächliche Dateiinhalte bei Ersetzen, Anhängen, Kopieren und Verschieben.
- Relative/absolute Pfade, verschachtelte Verzeichnisse, `.` und `..`.
- Dateitypen, nichtleere Verzeichnisse, 15-Zeichen-Namen und volle Knotentabelle.
- 512-Byte-Grenze ohne Überschreiben der Nachbardatei.
- `wc`, `hexdump`, Skriptausführung und Dateierhalt beim Shell-Warmstart.
- Uhrüberträge und numerisch korrekte Umrechnung des 24-Bit-Jiffy-Wertes.
- Physische Shift-/Ctrl-Kombinationen, Tastaturwiederholung und Zehn-Byte-Puffer.
- Backspace am 40-Spalten-Umbruch, History und Eingabeabbruch.

## 2. Kontrollierte KERNAL-Dateiendpunkte

Programm: `tests/test_disk_contract.py`.
Protokoll: `tests/disk-contract-results.json`.

Hier laufen die realen Shell- und Dateisystem-Instruktionen. Nur die öffentlichen
KERNAL-Dateiaufrufe werden durch deterministische Ein-/Ausgabekanäle ersetzt,
damit Größen, Dateifehler und DOS-Antworten gezielt geprüft werden können.

Abgedeckt sind leere Dateien, exakte 512-Byte-Übertragungen, 513-Byte-Ablehnung,
Fehler während des Lesens, Erhalt vorhandener Importziele, bytegenauer Export
und DOS-Fehler wie vorhandene Datei oder volle Diskette. Der Programmlader wird
mit korrekten, leeren, falsch adressierten und zu großen PRGs geprüft. Ein
fehlgeschlagener Ladevorgang gibt das teilweise geladene Programm nicht zur
Ausführung frei. `$9FFF` ist als letzte gültige Nutzadresse ausdrücklich geprüft.

Diese Tests sind **keine** Emulation eines 1541-Laufwerks und kein Nachweis
physisch geschriebener Diskettendaten.

## 3. Begrenzte IEC-Wartezeiten

Programm: `tests/test_iec_timeouts.py`.
Protokoll: `docs/iec-timeouts.json`.

Die Ausgangsquellen enthielten unbegrenzte Wartepfade für einen bereits
ausgewählten, aber nicht antwortenden Teilnehmer. Drei Pfade besitzen jetzt
einen endlichen Timeout. Die Fehlerantwort setzt Carry und READST `$80`.

| Leitungsfall | CPU-Zyklen einschließlich Test-JSR und RTS | Ergebnis |
| --- | ---: | --- |
| EOI-Bestätigung bleibt aus | 3.936.073 | Timeout, C=1, ST=$80 |
| CLOCK bleibt low | 3.939.395 | Timeout, C=1, ST=$80 |
| DATA bleibt low | 3.936.088 | Timeout, C=1, ST=$80 |
| EOI-Bestätigung liegt bereits an | 61 | Erfolg |
| CLOCK ist bereits high | 27 | Erfolg |
| DATA ist bereits high | 21 | Erfolg |

Das entspricht bei PAL ungefähr vier CPU-Sekunden für den jeweiligen
blockierten Wartepfad. Der Stack ist nach allen sechs Aufrufen ausgeglichen.
Die normalen schnellen Pfade bleiben erhalten. Aus diesen Einzeltests folgt
keine feste Gesamtdauer für beliebig viele aufeinanderfolgende Gerätefehler.

## 4. Tatsächlicher C64Emulator-Kern

Quelle: <https://github.com/n1k0m0/C64Emulator>

Revision: `f01750e17a2c3f4cabfd57dc0c470dac93a91a23`.

Der Prüfaufbau kompiliert die ursprünglichen CPU-, CIA-, VIC-, SID- und
Busquellen des öffentlichen Emulators. Nur die Host-Audioausgabe wird durch
eine stumme Senke ersetzt, damit der Test ohne Windows-GUI laufen kann.
Die Emulator-Kernquellen wurden nicht verändert.

KERNAL-IEC-Hooks, Ladeabkürzungen und automatische Eingabehilfen sind deaktiviert.
Befehle gelangen entweder gezielt in den gewöhnlichen C64-Tastaturpuffer oder
über die echte emulierte Tastaturmatrix. ROMs und Demo-Diskette werden für den
Test in ein temporäres Verzeichnis kopiert.

Nachgewiesen sind:

- Kaltstart bis zum Shellprompt.
- Speichern, Anhängen, Lesen und Kopieren von RAM-Dateien.
- Skriptausführung, Speicheranzeige und Jiffy-basierte Laufzeit.
- Physische Tastatur einschließlich Shift, Quotes, `>` und Ctrl+C.
- Verwerfen überlanger Befehle ohne unbeabsichtigte Dateiveränderung.
- Diskettenverzeichnis über den tatsächlichen IEC-Pfad.
- Laden und Ausführen von `HELLO`, anschließend Rückkehr zur Shell.
- Beim kooperierenden `load`-Befehl: Ablehnen von PRGs bei `$0801`, Überschreiten
  von `$9FFF` und leerem Payload. Der neue `play`-Pfad wird getrennt geprüft.
- Laden eines einzelnen `RTS` bei `$9FFF` und erfolgreiche Rückkehr.
- Weiterfunktionierende Shell nach Diskettenfehlern.

Der finale Lauf mit 40 Szenarien dauerte rund 86 Sekunden. Berichte,
Konsolenausgabe und echte Framebuffer-Abbilder liegen in `docs/integration/`.

### Bekannte Grenzen dieses öffentlichen Emulatorstands

Sein Standard-Dateileser akzeptiert PRG-Verzeichniseinträge und behandelt
SEQ-Typ-/Moduszusätze nicht vollständig. Normales SEQ-Anlegen besitzt dort
ebenfalls keinen vollständigen Persistenzpfad. Deshalb melden `dcat`, `import`
und `export` für die SEQ-Testdateien Geräte-/DOS-Fehler. Ein vorhandenes RAM-
Importziel bleibt dabei erhalten.

Der ROM-PRG-Lader verwendet einen einfachen Dateinamen ohne redundantes `,P,R`.
Damit funktionieren native Programme sowohl mit dem geprüften alten
Dateinamensparser als auch mit der normalen PRG-Lesekonvention.

Der öffentliche Verzeichnisgenerator markiert die letzte Zeile `BLOCKS FREE`
mit einem Null-Link. Der Standard-Verzeichnisparser zeigt deshalb alle Dateien,
aber auf diesem Stand nicht diese abschließende Freiplatzzeile.

Das von dir bereitgestellte November-Paket enthält Protokolle eines neueren
lokalen Emulators mit erweiterten Dateioperationen. Diese Protokolle wurden
nicht als neue Testergebnisse für C64/UX gezählt. Der Quellstand dieses neueren
Emulators war im Paket nicht enthalten.

## 5. Spielmodus: gezielte Fehler- und Speicherprüfungen

Programm: `tests/test_play.py`. Protokoll: `docs/play-contract.json`.

Alle 14 Fälle führen die gelieferten ROM-Instruktionen auf py65 aus. Nur die
öffentlichen KERNAL-Dateiendpunkte sind kontrolliert, um Fehler an bestimmten
Bytes und Unterschiede zwischen beiden Lesevorgängen auslösen zu können.

Geprüft sind originale Ladeadressen, automatischer BASIC-SYS-Start, dezimale
und hexadezimale explizite Startadressen, Leerzeichen und führende Nullen im
SYS-Literal, 6 KiB Payload über den bisherigen Shell-Arbeitsbereichen, RAM unter
BASIC und `$CFFF` als letzte zulässige Adresse. Ungültige Header, leere Nutzdaten,
Laden unter `$0800`, Überschreiten von `$CFFF`, ungültige Startadressen, fehlerhafte
BASIC-Verknüpfungen, mehrere BASIC-Zeilen und SYS-Ausdrücke werden abgewiesen.

Vorabfehler erhalten vorhandene RAM-Dateien. Fehler oder geänderte Daten beim
zweiten Lesen führen zum Kaltstart, ohne den Spieleinstieg aufzurufen. Der
Übergabezustand von Registern, Stack, Bildschirm und üblichen Speicherzeigern
wird geprüft. Ein Testprogramm blendet die ROMs aus, aktiviert Sprites und
kehrt mit RTS zurück: Der RAM-Stub stellt das Banking wieder her, der Kaltstart
schaltet die Sprites ab und die Shell ist erneut nutzbar.

Diese Fehlerauslösung prüft die ROM-Logik, nicht einen defekten realen IEC-Bus.
Die normale Datenübertragung wird im folgenden tatsächlichen Core-Lauf geprüft.

## 6. Zwei Spiele im tatsächlichen Emulator-Kern

Programm: `tests/integration/verify_play.py`.
Protokolle, lesende RAM-Beobachtungen und echte VIC-Frames:
`docs/play-integration/`.

Der Lader verwendet normale IEC-Dateikanäle. Ladeabkürzungen, KERNAL-IEC-Hooks
und automatische Host-Eingabeinjektion sind deaktiviert. Nach der Befehlseingabe
laufen die normalen 6510-Spielprogramme; Steuerbefehle werden mit den physischen
KeyboardKeyDown/KeyboardKeyUp-Ereignissen an die emulierte Tastatur gegeben.
Spielcode und RAM werden zur Erzeugung des Spielverlaufs nicht gepatcht.

**ROM RUNNER:** Start über `play ROMRUNNER`, Titelbild, SPACE-Start, erster
gefangener Stern mit Score 001 bei drei Leben, Bewegung mit D von x=20 nach 32
und mit A zurück nach 20. Q führt per RTS zum Kaltstart; ein anschließendes
`echo runner-returned` wird korrekt ausgeführt. Spielquelle und Diskettenbuilder
sind vollständig enthalten.

**SERPENT 64 v1.0.4:** Das unveränderte veröffentlichte PRG wird von seiner
Original-D64 über `play "SERPENT 64"` geladen. Titel, SPACE-Start, Steuerung
nach oben und links, Punktegewinn durch einen Apfel, Pause und Rückkehr aus dem
Pausenbildschirm zum Spieltitel werden überprüft. Die Apfelposition ist zufallsabhängig; der
Prüftreiber muss sie durch lesende Beobachtung bestimmen und das Spiel durch
physische Eingaben dorthin steuern. Ein Emulator-Kaltstart führt danach wieder
zu einer funktionierenden Shell. Exakte Ereignisse und Zustände stehen im JSON.

Weitere unabhängige Disketten-Fixtures belegen den 6-KiB-SYS-Start über
überschriebenen Shell-RAM, direkten Code bei `$2000`, RAM unter BASIC bei
`$A000`, expliziten Start `$0840`, RTS bei `$CFFF` sowie Ablehnung von I/O-
Überschreitung und reinem BASIC bei gleichzeitigem Erhalt einer RAM-Datei.

Der SID-Synthesecode läuft, und Register-/Hüllkurvenzustände werden erfasst.
Die Host-Audioausgabe ist eine stumme Senke; hörbare Klangqualität wurde nicht
beurteilt. SERPENTs sämtliche Level und seine Highscore-Persistenz wurden nicht
geprüft. Aus zwei erfolgreichen Spielabläufen folgt keine allgemeine
Kompatibilitätsquote für andere Spiele.

## 7. Klassisches Profil

Programm: `tests/integration/verify_classic.py`.
Protokoll und Bilder: `docs/classic/`.

Die originalen freien BASIC-/KERNAL-Binärdateien aus dem Benutzerpaket bleiben
unverändert. Geprüft wurden BASIC-Kaltstart, physisches `PRINT 1+1`, bytegenaues
Laden aller 1.860 ROM-RUNNER-Nutzbytes über IEC, physisches `RUN`, Spielstart,
Punktegewinn, Bewegung links/rechts, RTS-Rückkehr und anschließend physisches
`PRINT 2+2`. Insgesamt sind 11 Prüfungen bestanden.

Eine Einschränkung ist separat als `limited` ausgewiesen: Die alte
Tastaturroutine dieses Profils übernahm die Anführungszeichen im vollständig
physischen LOAD-Versuch nicht zuverlässig. Der erfolgreiche Lauf reiht nur den
LOAD-Befehl mit Anführungszeichen gezielt als PETSCII ein. Der Transfer selbst läuft über IEC;
PRINT, RUN und Spieltasten laufen über die CIA-Matrix. Dies ist weder eine
Reparatur des alten Tastaturcodes noch eine Aussage über alle BASIC-Programme.

## 8. Reproduzierbarer Build

Protokoll: `docs/reproducibility.json`.

Ein neues temporäres Verzeichnis enthielt ausschließlich den mitgelieferten
Assembler, Buildcode, ASM-/Demo-Quellen sowie Zeichensatz- und Laufwerk-Binärdatei
aus dem Ausgangspaket. `python build.py` erzeugte alle acht ROM-Binärdateien,
das Manifest, `hello.prg`, `c64ux-demo.d64`, `rom_runner.prg`, seine Symbolkarte
und `play-games.d64` erneut. Alle 14 Ausgaben stimmen bytegenau mit den
gelieferten Dateien überein. Eine zusätzliche unabhängige Assemblierung prüfte
die vollständige Belegung und die beiden ROM-Slices.

Zeichensatz und Laufwerk-ROM sind binäre Eingangsassets, keine in diesem Build
aus Quellen übersetzten Komponenten. Classic-ROMs und SERPENT-Dateien sind
separat anhand ihrer gelieferten Manifest-Hashes geprüft. Ihre Quellen wurden
für diese Veröffentlichung nicht nachgebaut.

## Nicht als geprüft ausgewiesen

- Physische C64-/1541-Hardware und NTSC-Zeitgenauigkeit.
- Allgemeine C64-Spielkompatibilität, private BASIC-/KERNAL-Adressen, echte
  BASIC-Programme im UX-Profil, Cartridges und spezielle Nach-/Fastloader.
- Vollständiger SEQ-Schreib-/Lese-Rundlauf auf dem neueren lokalen Emulator.
- Die übernommenen Kassetten- und RS-232-Routinen als vollständige neue Regression.
- Beliebige Programme außerhalb der genannten Start-/Ladebereiche und
  universelle Rückkehr aus Spielen, die Stack oder Reset-Stub überschreiben.
- Sämtliche SERPENT-Level, Highscore-Speicherung und hörbare Audioqualität.

## Tests selbst ausführen

```text
python build.py
python -m pip install -r tests/requirements.txt
python tests/test_rom.py --json docs/rom-tests.json
python tests/test_disk_contract.py --json tests/disk-contract-results.json
python tests/test_play.py --json docs/play-contract.json
python tests/test_iec_timeouts.py --json docs/iec-timeouts.json
```

Für den echten Emulator-Kern werden zusätzlich dessen externe Quellkopie und
.NET 10 benötigt. Anleitung und parametrisierbarer Prüftreiber stehen in
`tests/integration/README.md`. Emulatorquellen, SDK und Host-Buildausgaben sind
kein Bestandteil dieses Pakets.
