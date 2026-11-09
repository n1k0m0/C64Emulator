# C64/UX 0.2.0

**Experimentelles ROM-Profil.** In C64Emulator 0.3.21 ist C64/UX als dritter
Eintrag unter `F10` → `SETTINGS` → `ROM SET` gebündelt. Die Auswahl zeigt
`EXPERIMENTAL`; nach Bestätigung startet die Maschine mit der Shell neu.
`ORIGINAL` wählt die separat heruntergeladenen ROMs, `OPEN` den freien
BASIC-Satz. Die folgenden manuellen Dateischritte gelten für ältere
Emulator-Versionen ohne diese Auswahl.

Ein direkt aus dem ROM bootendes, Unix-artiges System für den C64 und deinen
C64Emulator. Die Shell ersetzt den BASIC-Interpreter. Der angepasste KERNAL
übernimmt Bildschirm, Tastatur, Interrupts und die vorhandene Gerätekommunikation.

**Die beiden neuen ROMs gehören zusammen.** Es sind jeweils genau 8.192 Byte:
Shell bei `$A000–$BFFF`, KERNAL bei `$E000–$FFFF`. Der Zeichensatz benötigt wie
bisher weitere 4.096 Byte. Es wird keine zusätzliche ROM-Bank vorausgesetzt.

Das System ist in 6502/6510-Assembler implementiert. Die Befehle laufen auf der
emulierten CPU, ohne Linux auf dem Host, Emulator-Systemaufrufe oder einen
eingeblendeten Host-Terminal. C64/UX ist **kein Linux-Kernel und kein POSIX-System**.
Es führt keine Linux-Programme aus und enthält keinen BASIC-Interpreter.

**Neu in 0.2:** `play` startet passende C64-Maschinensprache-Spiele an ihrer
ursprünglichen PRG-Adresse und erkennt einen einfachen BASIC-`SYS`-Vorspann.
Mitgeliefert werden das neue Spiel **ROM RUNNER**, das unveränderte
**SERPENT 64 v1.0.4** und dein bisheriger BASIC-/KERNAL-Satz als separates
klassisches Profil. Beide Spiele wurden mit diesem ROM-Paar im echten
öffentlichen C64Emulator-Kern gespielt; das ist keine Zusage für jedes C64-Spiel.

## Manuelle Einrichtung für ältere Emulator-Versionen

1. Emulator beenden und die bisher verwendeten ROM-Dateien sichern.
2. Ein separates ROM-Profil für C64/UX anlegen, wenn deine lokale Emulator-Version
   ein ROM-Verzeichnis auswählen kann.
3. Die Dateien aus `roms/` für das gewählte Profil verwenden. Für die öffentliche
   Emulator-Version sind besonders diese Namen erforderlich:

   | Datei | Größe | Inhalt |
   | --- | ---: | --- |
   | `c64-basic-kernal.bin` | 16.384 Byte | Shell, anschließend KERNAL |
   | `c64-character.bin` | 4.096 Byte | Zeichensatz aus deinem Ausgangspaket |
   | `1541-c000-rom.bin` | 8.192 Byte | Erste Hälfte des mitgelieferten freien Laufwerk-ROMs |
   | `1541-e000-rom.bin` | 8.192 Byte | Zweite Hälfte des Laufwerk-ROMs |

   Die Einzeldateien `open-basic64.bin`, `open-kernal64.bin`,
   `open-character-rom64.bin` und `open-1541-dos.bin` sind ebenfalls enthalten.
   Sie sind für Emulator-Versionen gedacht, die getrennte ROMs laden.

4. Einen **Kaltstart** ausführen, ohne ein BASIC-Programm automatisch starten zu
   lassen. Erwarteter Prompt: `root@c64:/home/root#`.
5. Für Diskettentests das Beispielabbild `examples/c64ux-demo.d64` auf Laufwerk 8
   einlegen. Es muss nur eingelegt werden; ein BASIC-`LOAD`/`RUN`-Autostart passt
   nicht zu dieser Shell.

Die geprüfte öffentliche Emulator-Version sucht bevorzugt in
`%APPDATA%\C64Emulator\roms`. Dieses Verzeichnis hat Vorrang vor ROMs neben der EXE.
Falls trotz Austausch weiterhin BASIC erscheint, zuerst prüfen, aus welchem
Verzeichnis diese Version tatsächlich lädt. Die Prüfung erfolgte mit dem
öffentlichen Commit `f01750e17a2c3f4cabfd57dc0c470dac93a91a23`; dein hochgeladenes
ROM-Paket stammt aus einem neueren lokalen Entwicklungsstand.

Deine bisherigen freien ROMs liegen zusätzlich unverändert unter
`profiles/classic/roms/`. Dieses Profil wird im Emulator ausgewählt und kalt
gestartet. Ein Shell-Befehl kann die physischen ROM-Dateien nicht austauschen.

## Spiele sofort ausprobieren

### ROM RUNNER

`examples/play-games.d64` in Laufwerk 8 einlegen und in C64/UX eingeben:

```text
dir
play romrunner
```

Mit **Leertaste** starten, mit **A/D** oder Joystick 2 bewegen. Gelbe Sterne
fangen, roten Felsen ausweichen. **R** beginnt eine neue Partie; **Q** oder
**Run/Stop** kehrt zu einer frisch gestarteten C64/UX-Shell zurück.
Quellcode und vollständige Anleitung stehen in `examples/rom_runner.asm` und
`examples/ROM_RUNNER.txt`.

### SERPENT 64

`third_party/serpent64/serpent64.d64` in Laufwerk 8 einlegen:

```text
play "SERPENT 64"
```

Die Anführungszeichen sind wegen des Leerzeichens im Diskettennamen nötig.
**Leertaste** startet, **W/A/S/D** steuert, **Leertaste** pausiert.
Im Pausenbildschirm führt **Q** zum Spieltitel zurück. Zur Shell geht es über den Reset des
Emulators. Die Originalanleitung und MIT-Lizenz liegen neben dem Diskettenabbild.

**Vor dem Spielstart:** `play` gibt den Shell-Arbeitsspeicher einschließlich
RAM-Dateisystem an das Spiel ab. Die bisherige Sitzung wird nicht fortgesetzt.
Eine vorab abgewiesene Datei lässt die Sitzung bestehen; sobald das eigentliche
Laden beginnt, muss mit dem Verlust der RAM-Dateien gerechnet werden. Für die
Disketten-Schreibfunktion gelten die unten beschriebenen SEQ-Einschränkungen.

Der Lader liest jede Datei zweimal: zuerst zur Prüfung, danach in den
Spielespeicher. Über den normalen IEC-Weg kann das bei größeren Spielen eine
Weile dauern. Währenddessen erscheinen `Checking game...` und anschließend
`Starting game. RAM files released.`.

## Erste Befehle

```text
help
uname
cat /readme
pwd
ls /
echo "Hallo Nils" > notiz
cat notiz
echo "Eine zweite Zeile" >> notiz
wc notiz
mkdir arbeit
cp notiz arbeit/kopie
cd arbeit
ls
hexdump kopie
cd ..
sh hello.sh
uptime
mem
```

`>` legt eine Datei an oder ersetzt ihren Inhalt. `>>` hängt Ausgabe an.
Beide funktionieren auch ohne umgebende Leerzeichen. Eine Umleitung muss am Ende
des Befehls stehen. Ein in Anführungszeichen stehendes `">"` bleibt Text.
Fehlermeldungen erscheinen auf dem Bildschirm, auch bei umgeleiteter Ausgabe.

## Befehle

| Befehl | Funktion |
| --- | --- |
| `help` | Kompakte Befehlsübersicht |
| `uname` | Systemname, Version und CPU |
| `whoami`, `hostname` | Fester Benutzername `root` bzw. Rechnername `c64` |
| `clear` | Bildschirm leeren, Schwarz/Grün und Kleinbuchstabenmodus wiederherstellen |
| `pwd` | Aktuellen absoluten Pfad anzeigen |
| `cd [pfad]` | Verzeichnis wechseln, ohne Argument nach `/home/root` |
| `ls [pfad]` | Dateien mit Byteanzahl und Verzeichnisse auflisten |
| `cat datei` | Gespeicherten Text ausgeben |
| `echo [text ...]` | Argumente und einen Zeilenumbruch ausgeben |
| `touch datei` | Leere Datei anlegen; bestehende Datei beibehalten |
| `mkdir pfad` | Ein Verzeichnis anlegen; Elternverzeichnis muss existieren |
| `rm datei` | Reguläre Datei löschen |
| `rmdir pfad` | Leeres Verzeichnis löschen; Wurzel und aktuelles Verzeichnis sind geschützt |
| `cp quelle ziel` | Datei kopieren, vorhandene Zieldatei ersetzen |
| `mv quelle ziel` | Datei kopieren und anschließend Quelle entfernen |
| `wc datei` | Anzahl LF-Zeilenumbrüche, Wörter und Bytes anzeigen |
| `hexdump datei` | Hex-Ausgabe mit Dateioffset, acht Bytes pro Zeile |
| `mem`, `free` | Speicheraufteilung, belegte Knoten und gespeicherte Bytes anzeigen |
| `uptime` | Laufzeit aus dem 24-Bit-KERNAL-Jiffy-Zähler, in Sekunden |
| `ps` | Den einen laufenden Shell-Task anzeigen; es gibt keinen Scheduler |
| `sh datei` | Ein kleines Shell-Skript aus dem RAM-Dateisystem ausführen |
| `dir` | Verzeichnis von Laufwerk 8 anzeigen |
| `dcat name` | Datei von Laufwerk 8 als Text ausgeben |
| `import diskname rampfad` | Diskettendatei in das RAM-Dateisystem übernehmen |
| `export rampfad diskname` | RAM-Datei auf Laufwerk 8 schreiben |
| `load name` | 6510-PRG von Laufwerk 8 in den Anwendungsbereich laden |
| `run [name]` | Geladenes PRG ausführen oder vorher das genannte PRG laden |
| `play name [startadresse]` | PRG an Originaladresse laden, RAM an das Spiel abgeben und starten |
| `reboot` | Kaltstart; RAM-Dateisystem neu initialisieren |

### Tastatur

- Buchstaben ohne Shift erscheinen klein, mit Shift groß.
- Befehls- und Pfadnamen werden ohne Unterscheidung von Groß-/Kleinschreibung
  erkannt. Dateiinhalte behalten ihre Schreibweise.
- Return führt die Zeile aus. Delete/Backspace löscht auch über einen
  Bildschirmzeilenumbruch hinweg.
- Cursor hoch holt den letzten interaktiv eingegebenen Befehl zurück.
- Ctrl+C oder Run/Stop verwirft die aktuelle Eingabezeile.
- Ctrl+U löscht die Eingabezeile. Ctrl+L leert den Bildschirm und beginnt einen
  neuen Prompt.
- Beim physischen C64-Layout erzeugt Shift+Punkt `>`, Shift+Komma `<` und Shift+2
  ein doppeltes Anführungszeichen. Die PC-Zuordnung hängt von der
  Tastaturabbildung des Emulators ab.
- Shift und Ctrl werden über die vollständige CIA-Matrix ausgewertet.
  Wiederholung beginnt nach 30 Abfragen und erfolgt danach alle vier Abfragen.
  Es gibt keine vollständige Mehrtastenerkennung für mehrere normale Tasten.

## RAM-Dateisystem

Es gibt **32 Knoten insgesamt**, also Dateien und Verzeichnisse zusammen. Beim
Start sind acht davon für `/`, `/home`, `/home/root`, `/tmp`, `/etc`, `/readme`,
`/etc/motd` und `/home/root/hello.sh` belegt. Jede Datei kann bis zu **512 Byte**
enthalten. Ein einzelner Namensbestandteil darf höchstens **15 Zeichen** lang
sein. Pfade können absolut oder relativ sein; `.` und `..` werden unterstützt.
Der Speicher ist statisch zugeteilt: Verzeichnisse nutzen einen Knoten, obwohl
sie keinen Dateiinhalt speichern. Die reservierten 16 KiB Dateidaten sind daher
nicht vollständig als Nutzdaten verfügbar.

Dateien existieren im C64-RAM. Kaltstart, `reboot` oder ein frischer Emulatorstart
initialisieren sie neu. Auch der Spielmodus `play` gibt diesen Speicher auf.
Der Warmstart über den Shell-Vektor `$A002` erhält die Dateien.
Was ein Emulator-Menüpunkt namens „Warm Reset“ tatsächlich macht, hängt von
dessen Implementierung ab; er muss nicht diesem Shell-Warmstart entsprechen.

Eine Ausgabe, die 512 Byte überschreitet, stoppt am Dateilimit und meldet einen
Fehler. Bereits geschriebene Bytes bleiben erhalten. Nachbardateien werden nicht
überschrieben. `mv` ist als Kopieren plus Entfernen implementiert und benötigt
bei einem neuen Ziel einen freien Knoten. Es verschiebt keine Verzeichnisse.

Die Shell unterstützt höchstens acht Argumente und 127 Eingabezeichen. Zu lange
interaktive Eingaben werden bis Return verworfen und **nicht abgeschnitten
ausgeführt**. Einfache und doppelte Anführungszeichen gruppieren Argumente.
Es gibt keine Backslash-Escapes, Variablenexpansion, Wildcards, Pipes, Befehlslisten
mit Semikolon, Hintergrundprozesse oder Dateirechte.

### Skripte

Skripte sind gewöhnliche RAM-Textdateien mit einem Befehl pro Zeile. Eine Zeile,
die direkt mit `#` beginnt, ist ein Kommentar. `sh` verwendet denselben Parser und
dieselben Befehle wie die interaktive Shell. Beim ersten gemeldeten Fehler wird
das Skript abgebrochen. Verschachtelte Skriptaufrufe sind nicht implementiert.
Die Skriptdatei wird vor dem Start kopiert, damit ihr Inhalt während der
Ausführung sicher verändert oder gelöscht werden kann.

```text
echo "echo Hallo aus dem Skript" > start.sh
echo "uname" >> start.sh
echo "pwd" >> start.sh
sh start.sh
```

Die Ausgabeumleitung steht innerhalb der Skriptzeilen; eine Umleitung des
gesamten `sh`-Aufrufs ist nicht implementiert. Auch Ctrl+C ist hier kein
allgemeiner Signalmechanismus für laufende Programme.

## Programme und Disketten

### `load` und `run`: kooperierende kleine Programme

Der Anwendungsbereich ist `$6000–$9FFF` (16 KiB). PRGs enthalten wie üblich einen
zweibytegroßen Startadress-Header. Der Loader prüft, dass der geladene Bereich im
Anwendungsfenster bleibt. `examples/hello.asm` und das bereits gebaute
`examples/hello.prg` zeigen einen kleinen Einstieg. Ein aufgerufenes Programm
kehrt mit `RTS` zur Shell zurück.

```text
dir
load hello
run
```

Dies ist eine Schnittstelle für kooperierende 6510-Maschinenprogramme. Programme,
die eigenen Speicher, Vektoren, Hardware oder den Stack beliebig verändern,
können die Shell beschädigen. Es gibt keine Speicherisolation. BASIC-PRGs mit
Startadresse `$0801` passen nicht in diesen Programmlader.

### `play`: Programme übernehmen den Rechner

`play` hat einen eigenen Lader. Er verwendet den zweibytegroßen PRG-Header als
Ladeadresse und erlaubt Nutzdaten von **`$0800` bis einschließlich `$CFFF`**
(zusammen 50 KiB). Damit dürfen Spiele die bisherigen Shell-Puffer und das
RAM-Dateisystem überschreiben. Daten unter dem BASIC-ROM landen im darunter
liegenden RAM. Sichtbare I/O-Adressen ab `$D000` sind als Ladeziel ausgeschlossen.

| Aufruf | Startregel |
| --- | --- |
| `play name` mit Ladeadresse `$0801` | Eine einzelne tokenisierte BASIC-Zeile `SYS` mit dezimaler Zahl erkennen und dorthin springen |
| `play name` mit anderer Ladeadresse | An der PRG-Ladeadresse beginnen |
| `play name 2061` | Explizit bei Dezimaladresse 2061 beginnen |
| `play name $080d` | Explizit bei Hexadresse `$080D` beginnen |

Die Startadresse muss innerhalb der geladenen Nutzdaten liegen. Beim automatischen
`SYS`-Start müssen Zeilenverknüpfung und BASIC-Endemarkierung korrekt sein und in
den ersten 48 Nutzbytes liegen. Leerzeichen und führende Nullen in der Zahl sind
erlaubt. Mehrere BASIC-Zeilen, Ausdrücke wie `SYS 2048+13`, weitere Anweisungen und
echte BASIC-Spiele werden nicht ausgeführt. Eine explizite Startadresse eignet
sich für bekannte Maschinenprogramme mit einem anderen Vorspann; sie macht aus
einem BASIC-Programm kein Maschinenprogramm. Ausgabeumleitung für `play` wird
abgewiesen.

Die erste Leserunde prüft Bereich, Länge, Startadresse und einen CRC-16-Wert,
ohne den PRG-Inhalt in Shell-RAM zu schreiben. Die zweite Runde vergleicht Header,
Länge und CRC beim Laden erneut. Ein Fehler vor der Übernahme führt zum normalen
Prompt zurück. Ein Fehler während der Übernahme führt zum Kaltstart mit
`play: load failed, RAM reset`, da die alte Shell bereits überschrieben sein kann.
CRC-16 dient hier der Erkennung von Leseänderungen, nicht der Authentifizierung.

Vor dem Sprung werden KERNAL-Vektoren, Bildschirm, Tastatur-/Interruptbasis und
übliche Speicherzeiger initialisiert. Der Bildschirm wechselt in den blauen
C64-Modus mit Großbuchstaben. Ein Start im Bereich `$A000–$BFFF` blendet das
BASIC-ROM aus; ansonsten bleibt es zunächst eingeblendet. Ein Programm, das
diesen Bereich später selbst nutzen will, muss seine Speicherabbildung setzen.

Ein kooperierendes `RTS` erreicht einen kleinen Reset-Stub im RAM und startet
C64/UX neu. Das funktioniert auch bei ausgeblendeten ROMs, sofern Stack und Stub
erhalten bleiben. Viele Spiele richten Stack und Vektoren selbst ein; für sie
ist der Emulator-Reset der Rückweg. Es gibt kein allgemeines Tastenkürzel, das
beliebige laufende Spiele sicher in die Shell zurückholt.

### Klassisches BASIC-/KERNAL-Profil

Unter `profiles/classic/roms/` liegt der unveränderte freie ROM-Satz aus deinem
Ausgangspaket, einschließlich kombinierter Emulator-Dateien. Wähle dieses
ROM-Verzeichnis und starte den Emulator kalt, wenn du wieder den vorhandenen
BASIC-Interpreter verwenden möchtest. Anleitung und tatsächlich geprüfter
Umfang stehen in `profiles/classic/README_DE.md`.

Das Profil ist der ursprüngliche Entwicklungsstand. Es ist weder das Commodore-
Original-ROM noch eine Zusage vollständiger BASIC- oder Spielekompatibilität.
Insbesondere Nachlader, Fastloader, private ROM-Einsprünge und spezielle
Startverfahren müssen jeweils geprüft werden.

### Gemeinsamer Diskettenweg

Die Diskettenbefehle verwenden den normalen KERNAL-/IEC-Weg zu Gerät 8. Sie
verwenden keine Host-Dateisystem-Traps. Die tatsächlich verfügbare Disketten-
und Schreibunterstützung hängt zusätzlich vom Laufwerk und der Emulator-Version
ab. Der genaue geprüfte Umfang und die Grenzen stehen in `docs/VERIFICATION.md`.
Das mitgelieferte Laufwerk-ROM stammt unverändert aus deinem Ausgangspaket.

`export` legt eine neue SEQ-Datei an und fragt anschließend den DOS-Status ab.
Eine vorhandene Diskettendatei wird nicht automatisch überschrieben. Disknamen
dürfen höchstens 16 Zeichen enthalten; Komma, Doppelpunkt und `@` sind für die
Shell-Dateinamenargumente gesperrt, damit sie nicht als DOS-Optionen wirken.
`import` liest zuerst vollständig in einen separaten 512-Byte-Puffer und ändert
die Zieldatei erst nach erfolgreichem Abschluss. Eine zu große oder fehlerhaft
gelesene Datei lässt das bisherige RAM-Ziel unverändert.

Die öffentliche Emulator-Version des geprüften Commits kann SEQ-Dateien über
ihren Standardkanal nicht vollständig lesen/schreiben. `dcat`, `import` und
`export` sind deshalb zusätzlich über eine kontrollierte KERNAL-Dateischnittstelle
geprüft; ein vollständiger SEQ-Schreib-/Lese-Rundlauf auf deinem neueren lokalen
Emulator wurde hier nicht ausgeführt. Verzeichnisabfrage und native PRGs werden
separat im echten Emulator-Kern geprüft. Diese Unterscheidung ist im Testbericht
ausdrücklich festgehalten.

## Selbst bauen

Der ROM-Build benötigt nur **Python 3.10 oder neuer**, keine externen
Assemblerprogramme und keine Python-Pakete:

```text
python build.py
```

Unter Windows geht auch `build.cmd`. Der mitgelieferte Assembler akzeptiert nur
dokumentierte NMOS-6502-Opcodes. Überlappende ROM-Bereiche, unbekannte Symbole und
zu große Images führen zu einem Buildfehler. Lange bedingte Sprünge werden
deterministisch in invertierten Kurzsprung plus `JMP` umgesetzt.

`roms/manifest.json` enthält Größen und SHA-256-Prüfsummen. Es wird mitgebaut.
Die ROM-Dateien enthalten keine zeitabhängigen Buildstempel.

## Tests

Für die unabhängigen CPU-Tests:

```text
python -m pip install -r tests/requirements.txt
python tests/test_rom.py
python tests/test_play.py --json docs/play-contract.json
```

Diese Tests führen die ROM-Instruktionen auf py65 aus und modellieren
ROM-Banking, CIA-Timer, Tastaturmatrix und Bildschirm-RAM. Dieses Testmodell ist
kein vollständiger C64 oder 1541. Zusätzlich wurde der echte öffentliche
C64Emulator-Kern mit deaktivierten Lade-/KERNAL-Abkürzungen benutzt.
Testberichte und die wiederverwendbare Integration liegen unter `docs/` und
`tests/integration/`.

## Herkunft und Grenzen

Die neue Shell, der Spielmodus und ROM RUNNER stehen unter Apache-2.0, ebenso wie die
mitgelieferten Ausgangsquellen durch ihre SPDX-Kennzeichnung ausgewiesen sind.
Siehe `LICENSE` und `NOTICE`. Zeichensatz- und Laufwerk-Binärdateien wurden aus
dem von dir bereitgestellten Paket übernommen; ihre Originalquellen und eine
gesonderte Lizenzdatei waren darin nicht enthalten.
SERPENT 64 ist ein unverändertes Fremdspiel unter MIT-Lizenz. Herkunft, exakter
Release-Stand und Prüfsummen sind in `third_party/serpent64/PROVENANCE.json`
festgehalten; seine Lizenz wird nicht durch die Projektlizenz ersetzt.

Das ist ein brauchbares kleines ROM-System mit bewusst begrenztem Umfang. Es
ist kein universeller Ersatz für sämtliche BASIC-/KERNAL-Interna beliebiger
C64-Spiele. Bewahrt sind die öffentlichen KERNAL-Einstiegspunkte und die
zugehörige Gerätebasis, soweit im Testbericht beschrieben. Entfernte
Gleitkomma-Routinen und BASIC-interne Einsprungadressen stehen nicht mehr zur
Verfügung. Die Uhr verwendet die übernommene PAL-Auslegung von ungefähr
60 Jiffies pro Sekunde; NTSC-Genauigkeit ist nicht validiert. Der 24-Bit-Zähler
läuft nach seinem natürlichen Überlauf wieder bei null an.

## Technische Referenzen

- Commodore, *C64 Programmer's Reference Guide*, Speicherverwaltung:
  <https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_5/page_260.html>
- Commodore, KERNAL GETIN und Tastaturpuffer:
  <https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_5/page_283.html>
- cc65, C64-Programmstart und Speicheraufteilung:
  <https://cc65.github.io/doc/c64.html>
- SERPENT 64, offizieller Release v1.0.4:
  <https://github.com/AdvancingDevelopment/c64snake/releases/tag/v1.0.4>
- Linux-Kernel-Dokumentation, CPU-Architekturen:
  <https://docs.kernel.org/arch/index.html>
- Geprüfter öffentlicher Emulatorstand:
  <https://github.com/n1k0m0/C64Emulator/tree/f01750e17a2c3f4cabfd57dc0c470dac93a91a23>
