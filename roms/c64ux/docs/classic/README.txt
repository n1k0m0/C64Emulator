Classic-Profil: konkrete Integrationspruefung
==========================================

Dieser Bericht prueft die unveraenderten Open-BASIC- und Open-KERNAL-Dateien
aus dem vom Benutzer gelieferten Entwicklungsstand. Er belegt einen
begrenzten, reproduzierbaren Startweg und keine allgemeine Spiele- oder
BASIC-Kompatibilitaet.

Die Dateien unter profiles/classic/roms wurden waehrend des Tests nicht
geaendert. report.json enthaelt ihre SHA-256-Werte. Der externe C64Emulator
wurde beim Commit f01750e17a2c3f4cabfd57dc0c470dac93a91a23 gebaut. CPU, CIA,
VIC, SID und IEC laufen mit dem unveraenderten Quellcode dieses Emulators.
Der Frontend-Test ersetzt nur die Audioausgabe durch eine Senke; hoerbarer
Ton wurde in diesem Lauf nicht bewertet.

NACHGEWIESENER ABLAUF
--------------------
1. Kaltstart in OPEN BASIC 64.
2. Physisch ueber die CIA-Matrix eingegeben: PRINT 1+1 -> 2.
3. play-games.d64 in Laufwerk 8 eingehangen.
4. LOAD "ROMRUNNER",8,1 als ausdruecklich eingereihte PETSCII-Eingabe.
   Der Dateitransfer selbst laeuft ueber IEC. Alle 1.860 geladenen PRG-
   Nutzbytes stimmen mit rom_runner.prg ueberein.
5. Physisch eingegebenes RUN startet die BASIC-SYS-Zeile (SYS 2061).
6. Physische SPACE-Taste startet das Spiel. Ein gefangener Stern erhoeht
   den Punktestand von 000 auf 001.
7. Gehaltenes D bewegt von Spalte 20 nach 29, A wieder nach 20.
8. Q kehrt per RTS nach BASIC zurueck. PRINT 2+2 ergibt anschliessend 4.

GRENZE DES UNVERAENDERTEN CLASSIC-PROFILS
--------------------------------------
Im getrennten Versuch mit ausschliesslich physischer Matrixeingabe werden
die Anfuehrungszeichen des LOAD-Befehls nicht zuverlaessig erzeugt.
Beobachtet wurden fehlende Zeichen sowie eine unverschobene 2.
Der konkret beobachtete Befehl und der folgende SYNTAX ERROR stehen in
report.json und keyboard-limitation.txt. Die Original-ROMs wurden dafuer
nicht gepatcht.

Deshalb wird nur der quoted LOAD-Befehl im erfolgreichen Ablauf als
explizite PETSCII-Eingabe eingereiht. PRINT, RUN und die Spielsteuerung
laufen ueber die echte CIA-Matrix. Emulator-LOAD-Abkuerzung, KERNAL-IEC-Hooks
und automatische Host-Eingabeinjektion sind in beiden Versuchen abgeschaltet.
Aus dem bestandenen Test folgt keine Zusage fuer beliebige andere Spiele,
BASIC-Programme, Fastloader, Kassetten oder Module.

WIEDERHOLEN
-----------
Im Projektverzeichnis, mit externem Emulator-Checkout und .NET-10-SDK:

python tests/integration/verify_classic.py --source /pfad/C64Emulator --dotnet /pfad/dotnet --output docs/classic

Das Skript kopiert ROMs und Diskette in temporaere Verzeichnisse. Es
kompiliert den externen Core, bedient die Eingabe und prueft die tatsaechlich
entstandenen RAM- und Bildschirmzustaende. Eine temporaere Kopie des
Frontends ergaenzt lediglich Warte- und gehaltene-Taste-Aktionen; der Core
wird unveraendert eingebunden.

DIE BILDER
----------
basic.png: BASIC-Rechnung vor dem Spiel.
game-title.png: durch RUN gestarteter Spieltitel.
gameplay.png: Spiel laeuft, erster Punkt erzielt.
movement.png: Figur nach rechts bewegt.
return-basic.png: BASIC-Rechnung nach RTS aus dem Spiel.
keyboard-limitation.png: separater physischer LOAD-Versuch.

Alle Bilder sind verlustfrei gespeicherte Frames der tatsaechlichen VIC-
Implementierung. transcript.txt enthaelt die Bildschirmzustaende in Textform.
