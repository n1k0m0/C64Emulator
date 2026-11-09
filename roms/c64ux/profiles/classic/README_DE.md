# Klassisches Profil: dein bisheriger BASIC-/KERNAL-Satz

Dieser Ordner enthält die unveränderten Open-BASIC- und Open-KERNAL-Binärdateien
aus deinem bereitgestellten `kernal-private-eddd-current-v2.zip`. Zeichensatz
und Laufwerk-ROM sind ebenfalls unverändert. `roms/manifest.json` dokumentiert
Größen und SHA-256-Werte. Die kombinierten Emulator-Dateien sind exakte
Verkettungen, Kopien oder Hälften dieser Ausgangsdateien.

## Aktivieren

Wähle im Emulator dieses `roms/`-Verzeichnis anstelle des C64/UX-Verzeichnisses
und starte kalt. Falls deine Version kein Profilmenü besitzt, verwende den
bisherigen ROM-Austauschweg. Die öffentliche Version bevorzugt
`%APPDATA%\C64Emulator\roms` gegenüber Dateien neben der EXE; kontrolliere deshalb
das tatsächlich verwendete Verzeichnis. BASIC und KERNAL immer zusammen wählen.

Es erscheint **OPEN BASIC 64** mit `READY.`. Dieses Profil hat keinen `play`-
Befehl. Für das mitgelieferte Spiel `examples/play-games.d64` als Laufwerk 8
einlegen und in BASIC ausführen:

```text
LOAD "ROMRUNNER",8,1
RUN
```

SPACE startet, A/D bewegt die Figur, Q kehrt hier zu BASIC zurück. C64/UX wird
wieder durch Auswählen seines ROM-Paars und einen Kaltstart aktiviert. Ein
gewöhnlicher C64-Befehl kann diese Emulator-ROM-Dateien nicht umschalten.

## Tatsächlich geprüft

Im unveränderten öffentlichen C64Emulator-Kern, Commit
`f01750e17a2c3f4cabfd57dc0c470dac93a91a23`, sind 11 gezielte Prüfungen bestanden:
BASIC-Start, `PRINT 1+1`, bytegenaues Laden von ROM RUNNER über IEC, Start über
die normale BASIC-SYS-Zeile, Spielstart, Punktegewinn, Bewegung links/rechts,
RTS-Rückkehr nach BASIC und anschließend `PRINT 2+2`. Bericht, Eingabepfade und
echte Framebufferbilder liegen unter `docs/classic/` im Projekt-Hauptverzeichnis.

**Bekannte Tastatureinschränkung:** Die ursprüngliche Tastaturroutine dieses
Profils erzeugte im vollständig physischen Test die Anführungszeichen im
`LOAD`-Befehl nicht zuverlässig. Daher wurde ausschließlich dieser Befehl als
PETSCII-Zeichenfolge in den normalen Tastaturpuffer gegeben. Der Dateitransfer
lief weiterhin über IEC; PRINT, RUN und die Spielsteuerung wurden physisch über
die CIA-Matrix eingegeben. Falls deine lokale Version eine Texteingabe- oder
Einfügefunktion anbietet, kann sie für den LOAD-Befehl mit Anführungszeichen helfen. Der
erfolgreiche Test belegt keine Reparatur dieser alten Tastaturroutine.

Das C64/UX-Hauptprofil enthält dagegen die erweiterten Tastaturroutinen. Beide
Profile wurden getrennt geprüft und dokumentiert.

## Grenzen und Herkunft

Dies ist dein ursprünglicher Entwicklungsstand, kein Commodore-Original-ROM
und keine allgemeine Zusage für BASIC-Programme, Nachlader, Fastloader oder
Spiele mit privaten ROM-Einsprüngen. Das Classic-Profil wurde nicht verändert.

`python build.py` baut das neue C64/UX-Paar und seine eigenen Beispiele. Es baut
dieses Classic-Profil nicht aus Quellen nach: Der vollständige Quellcode für
das mitgelieferte BASIC-Binärabbild war im Ausgangspaket nicht enthalten.
Für die übernommenen Binärdateien wird hier keine zusätzliche Lizenz erteilt;
siehe `NOTICE` und `docs/PROVENANCE.json` im Projekt-Hauptverzeichnis.
