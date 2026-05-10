# C64 Emulator Analysebericht

Stand: 2026-05-10

## Kurzfazit

Der Emulator ist deutlich weiter als ein funktionaler Hobby-Prototyp. CPU, Speicherbanking, VIC-II, CIA, SID, IEC, D64, 1541-Ansaetze, Savestates und ein brauchbares OpenTK-Frontend sind vorhanden und greifen in einem gemeinsamen Taktmodell ineinander. BASIC, PRG-Start, viele Standard-D64-Loads und mehrere Spiele sind realistisch erreichbar.

Die groesste Luecke zum "echten C64 Feeling" liegt nicht in einem einzelnen fehlenden Baustein, sondern in der Genauigkeit der harten Randfaelle: VIC-II-Raster-/Sprite-/Badline-Edgecases, CIA-Timer- und Portdetails, SID-Analogverhalten sowie echte 1541/IEC-Kompatibilitaet fuer Fastloader und Kopierschutz. Der Code enthaelt bereits mehrere bewusst pragmatische Kompatibilitaetsbruecken, etwa LOAD-Hacks, softwareseitigen IEC/DOS-Transport und loader-spezifische Drive-Hilfen. Das ist gut fuer Spielbarkeit, aber begrenzt Cycle-Accuracy und Hardwaretreue.

Build und interner CPU-Selbsttest wurden geprueft:

- `dotnet build C64Emulator.sln -c Release -p:Platform=x64`: erfolgreich, 0 Warnungen.
- `C64Emulator\bin\x64\Release\C64Emulator.exe --self-test-cpu ...`: erfolgreich, alle 256 Opcode-Basiszyklen, IRQ/NMI und ausgewaehlte illegale Opcodes OK.

Phase-2-Umsetzungsstand nach diesem Bericht: Drag-and-drop fuer PRG/D64, Gamepad-Joystickeingabe, CRT-/TV-Video-Filter und differenzierte Reset-Modi wurden in die Frontend-/Usability-Schicht integriert. Ein vollstaendiger Keymap-Editor und echte Drive-Sound-Emulation bleiben separate Folgearbeiten.

Phase-3-Umsetzungsstand: Ein neuer `--accuracy-tests` Runner prueft interne Timing-Smokes fuer VIC-II, CIA 6526, SID-Envelope und 1541-Transportmodus. `scripts/run-phase3-checks.ps1` buendelt ROM-Check, CPU-Selftest, Accuracy-Smokes und Benchmark; der weitere Test-ROM-/Golden-Master-Plan liegt in `docs/phase3-accuracy-plan.md`.

Phase-4-Umsetzungsstand: `--trace-cycles` exportiert CPU/VIC/CIA/SID/IEC/Drive-Snapshots als CSV, `--regression-run` erzeugt einen Headless-Regressionslog plus optionalen Framebuffer-Golden-Master als PPM und SHA-256-Hash. Der Emulator besitzt ein kompaktes Live-Debug-Overlay fuer Diagnosezwecke; `scripts/run-phase4-checks.ps1` prueft die Werkzeugkette.

## Bewertungsraster

| Bereich | Aktueller Stand | Hauptrisiko |
| --- | --- | --- |
| CPU 6510 | Stark | einzelne illegale/unstabile NMOS-Details, Interrupt-/RDY-Edgecases |
| Speicher/Bus | Gut | offene Bus-/Floating-Bus-Effekte und Prozessorport-Analogdetails fehlen |
| VIC-II | Gut sichtbar, mittlere bis gute Timingbasis | Raster-Tricks, Badline-Verschiebungen, Sprite-Edgecases, Lightpen |
| CIA 6526 | Funktional | Timer-/TOD-/Serial-Port-Edgecases, Alarm, Latches, exakte IRQ-Latenz |
| SID | Musikalisch brauchbar | keine echte resid-artige SID-Analogtreue |
| IEC/1541 | ambitioniert, aber hybrid | KERNAL/Software/Hardware-Wechsel, Fastloader, Kopierschutz, Disk-Flux |
| Performance | ausreichend, aber nicht profiliert | pro Cycle viel Objekt-/Methodenarbeit, Locking, per-Pixel-VIC |
| Usability | solide Basics | keine Controller-/Keymap-GUI, begrenzte Medien-/ROM-Diagnose, wenig Debug-UX |

## CPU 6510

### Staerken

- Die CPU ist mikrozyklisch umgesetzt. `Cpu6510.Tick()` verarbeitet pro emuliertem CPU-Zyklus Fetch, Instruktionsschritt oder Interruptsequenz.
- Der Decoder deckt offizielle und zahlreiche illegale Opcodes ab. Die Basiszyklen aller 256 Opcodes werden in `CpuOpcodeSelfTest` tabellarisch geprueft.
- Dummy Reads/Writes und Page-Crossing-Pfade sind in `InstructionSteps` modelliert.
- IRQ/NMI werden nicht nur logisch, sondern mit 7-Zyklen-Sequenz getestet.
- Es gibt einen Trace-Harness, der fuer Regressionstests wertvoll ist.

### Genauigkeitsgrenzen

- `CpuOpcodeSelfTest` beschreibt einige illegale NMOS-Opcodes selbst als deterministische Annaeherung. Das ist vernuenftig, aber nicht identisch mit chipindividuellen instabilen Opcodes.
- `PredictNextCycleAccessType()` simuliert den naechsten CPU-Schritt vorab, damit das VIC-II-BA/AEC-Verhalten entscheiden kann, ob ein Write noch durch darf. Das ist clever, aber riskant: jede Seiteneffektluecke in dieser Vorhersage wird zu einem Bus-Timing-Bug.
- KERNAL-LOAD und IEC-Hooks koennen direkt in CPU-Fetch eingreifen (`TryHandleLoadHack`, `TryHandleKernalIecBridge`). Diese Pfade sind nuetzlich, aber nicht cycle-accurate.

### Empfehlungen

1. CPU-Testbasis mit externen Referenzen erweitern: `6502_functional_test`, `6502_decimal_test`, `lauszus/65x02`-Timingfaelle oder eigene Trace-Goldenfiles.
2. Illegal-opcode-Modus als Kompatibilitaetsprofil dokumentieren: "deterministisch/spielbar" vs. "NMOS-nah".
3. KERNAL- und LOAD-Hacks als explizite Kompatibilitaetsoptionen sichtbar machen, nicht als unsichtbare Default-Magie.

## Speicher, Banking und Bus

### Staerken

- RAM, BASIC-ROM, KERNAL-ROM, Char-ROM, Color-RAM und IO-Mapping sind sauber getrennt.
- Prozessorport $0000/$0001 schaltet BASIC/KERNAL/CHAR/IO plausibel.
- VIC-Bankauswahl ueber CIA2 ist eingebunden.
- Der Systembus fuehrt letzte CPU-/VIC-Buswerte mit, was spaeter fuer Floating-Bus-Effekte nutzbar ist.

### Luecken

- Offener Bus/Floating-Bus wird nicht konsequent zurueckgegeben. Viele IO-Reads liefern Registerwerte oder `0xFF`, aber nicht unbedingt die jeweils realistische Bus-Restladung.
- Color-RAM ist als normales Nibble-Array modelliert. Die oberen vier Bits echter Color-RAM-Reads sind nicht stabil und koennen demosensitiv sein.
- ROM-Laden basiert in `C64System` auf `Directory.GetCurrentDirectory()`. Das ist fuer Doubleclick meistens ok, aber fuer CLI/Tests/Shortcuts fragil.

### Empfehlungen

1. Ein zentrales Bus-Read-Konzept fuer "open bus" einfuehren: last CPU bus, last VIC bus, Color-RAM-high-nibble-Policy.
2. ROM-Resolver robuster machen: AppDomain-Basis, CurrentDirectory, Projektpfad, klare Fehlermeldung mit erwarteten Dateien und Hashes.
3. Optionales Diagnostic Overlay fuer Memory-Config, IRQ, Raster, CIA/VIC/SID-Register.

## VIC-II Video und Cycle Accuracy

### Staerken

- PAL-Modell mit 312 Linien, 63 Zyklen pro Linie und 403x284 sichtbarem Crop ist vorhanden.
- Rendering laeuft pro VIC-Zyklus und schreibt acht Pixel pro Cycle.
- Text, Bitmap, Multicolor, Extended Color, Scrolling, Border, Sprites und Sprite-Kollisionen sind implementiert.
- Es gibt ein Busplan-Modell mit Spritepointer-, Sprite-Daten-, Refresh- und Matrix-Fetches.
- Badline-Logik blockiert CPU-Zugriffe ueber BA/AEC und Matrix-Fetch-Overrides.

### Genauigkeitsgrenzen

- `BeginRasterLine()` ruft `_busPlan.BuildLine(false, _spriteDmaActive)` auf. Badline-Fetches werden spaeter dynamisch ueber `ApplyGraphicsBusOverrides()` eingefuegt. Das funktioniert fuer viele Faelle, ist aber weniger transparent als ein vollstaendiger per-Line/half-cycle VIC-Busplan.
- Mehrere Text/Bitmap-Zustandsfelder sind im Build ungenutzt. Das deutet auf begonnene, aber nicht vollstaendig verwobene VIC-II-Sequencer-Arbeit hin.
- Raster-IRQ, DEN-Latch, Badline-Start, Border-Flipflops und Sprite-DMA sind implementiert, aber die ganz fiesen Cycle-Edgecases brauchen Test-ROMs.
- Lightpen-Register sind vorhanden, aber ohne echte Lightpen-/Mouse-Erfassung.
- PAL-Farbpalette ist eine feste RGB-Tabelle. PAL-Composite-Artefakte, Farbphasen, Chroma/Luma und CRT-Charakter fehlen.

### Empfehlungen

1. VIC-II-Test-Suite einfuehren: `VIC-II testbench`, VICE-Known-Tests, eigene Golden-Screenshots fuer Badlines, Rasterbars, Sprite Crunch, Sprite-Multicolor, Border-Openings.
2. Busplan vereinheitlichen: fuer jede Rasterzeile/Phase die Fetches und CPU-Blockierungen in einer Datenstruktur planen, statt teils statisch und teils dynamisch zu ueberschreiben.
3. Sprite-DMA und Border-Logik gegen echte Cycle-Tabellen pruefen; besonders X/Y-Expansion, DMA-Start/Stop, $D011/$D016-Midline-Writes.
4. Optionales CRT/PAL-Filterprofil bauen: "sharp pixels", "PAL blur", "scanlines", "CRT mask".

## CIA 6526, Tastatur und Joystick

### Staerken

- CIA1/CIA2 haben Ports, Timer A/B, IRQ/NMI, TOD und Tastaturmatrix.
- CIA2 ist mit IEC-Leitungen gekoppelt.
- Joystick-Port 1/2/Both ist umschaltbar.
- Keyboard-Matrix deckt viele C64-Tasten ab.

### Luecken

- Timer A/B sind funktional, aber nicht voll 6526-edgeaccurate. Exakte Reload-Latenzen, One-Shot-/Force-Load-Sequenzen, PB-Toggle-Details und IRQ-Clear-Racecases sind wahrscheinlich nicht vollstaendig.
- TOD-Alarm, TOD-Latch-on-read und 50/60-Hz-Umschaltung sind nicht erkennbar voll modelliert.
- CIA-Serial-Register im C64-Kontext ist nur Registerzustand, keine vollstaendige serielle Schieberegister-Emulation.
- Host-Input wird an einigen Stellen direkt in den KERNAL-Keyboardbuffer gespiegelt, wenn ein bestimmter Intro-Poll-Loop erkannt wird. Das verbessert Kompatibilitaet, ist aber ein klarer Hardware-Bypass.

### Empfehlungen

1. CIA-Tests gegen bekannte 6526-Testprogramme aufnehmen.
2. TOD mit Alarm, Latching und 50/60-Hz-Quelle ergaenzen.
3. Keymap/Profile-Editor einbauen: deutsche Tastatur, symbolische C64-Tasten, frei belegbare Joystick-Buttons.
4. Input-Hacks sichtbar schaltbar machen: "Compatibility input injection".

## SID Audio

### Staerken

- Drei Stimmen mit 24-Bit-Akkumulator, ADSR, Triangle/Saw/Pulse/Noise, Sync, Ringmod und Voice-3-Reads.
- Filterpfad, 6581/8580-Umschaltung und Volume-DAC fuer Digis sind vorhanden.
- NAudio-Ausgabe mit Buffering ist integriert.

### Genauigkeitsgrenzen

- Huellkurven sind zeitbasierte float-Rampen, nicht der echte SID-Envelope-Counter mit ADSR-Bug, Rate-Counter und Exponential-Counter.
- Kombinierte Waveforms werden gemischt und mit `tanh` gesaettigt, nicht ueber chiptypische Lookup-/DAC-Modelle gebildet.
- Filter ist ein generisches State-Variable-Filter mit heuristischen 6581/8580-Kennlinien.
- Sample-Rate ist fest 44,1 kHz; keine Resampling-Qualitaetsprofile.

### Empfehlungen

1. Mittel-/langfristig resid/residfp-Ansatz portieren oder als natives Modul kapseln.
2. Kurzfristig SID-Modi anbieten: "fast", "balanced", "accurate".
3. ADSR-Bug und echte Envelope-Counter als naechstes Klang-Upgrade priorisieren.
4. Audio-Latenz/Buffer im UI einstellbar machen, plus Muting/Reset bei Savestate-Load.

## IEC, D64 und 1541

### Staerken

- Mehrere Drives 8-11, D64-Mounting, Directory-Listing, PRG-Load und Command-Channel-Logik sind vorhanden.
- IEC-Bus ist als wired-AND-Leitungssystem mit Teilnehmern umgesetzt.
- 1541-Hardwaremodell enthaelt Drive-CPU, zwei VIA6522, ROM-Halves, Disk-Mechanik, GCR-Trackstream und DOS-Jobqueue.
- Es gibt Support fuer hochgeladenen Drive-Code und einzelne Fastloader-Situationen.

### Genauigkeitsgrenzen

- Standard-LOADs laufen standardmaessig ueber `ForceSoftwareTransport = true`. Der reine ROM-/Hardwarepfad ist durch `CanStartDriveRomTransport()` aktuell deaktiviert.
- D64 wird zu synthetischen GCR-Bytes expandiert, nicht zu realen Flux-/Bitcell-Transitions. Das reicht fuer viele DOS- und manche Fastloader-Pfade, aber nicht fuer viele Kopierschutzverfahren.
- Die Drive-Mechanik gibt GCR-Bytes direkt an die VIA-Seite aus. Der aeltere Bitstream/Encoderpfad existiert teilweise noch, ist aber im Hauptpfad umgangen.
- Loader-spezifische Hinweise wie Maniac-Mansion-Stubs und User-Command-Slot-Konventionen sind eingebaut. Das ist pragmatisch, aber keine generische 1541-Accuracy.

### Empfehlungen

1. Drei klar benannte Disk-Modi einfuehren:
   - `Fast/Software DOS`: heutiger kompatibler Standard fuer Komfort.
   - `ROM IEC`: echter 1541-ROM-Transport ohne KERNAL-Hook.
   - `Cycle Accurate Drive`: CPU/VIA/Mechanik strikt getaktet, langsamer, fuer Fastloader.
2. `CanStartDriveRomTransport()` implementieren und ueber Test-D64s absichern.
3. G64/NIB-Unterstuetzung planen, wenn Kopierschutz/Fastloader wichtig wird.
4. Drive-CPU- und IEC-Traces als Debugfenster/Log exportieren.
5. Software-Transport und loader-spezifische Patches im UI sichtbar machen.

## Savestates

### Staerken

- Savestates erfassen Bus, CPU, VIC, CIAs, SID, Framebuffer, vier Drives, IEC-Bus, Media-Manager und Host-Key-State.
- Vorschau-Screenshots und Metadaten sind vorhanden.
- Der Zustand ist breit genug, um komplexe laufende Sessions zu speichern.

### Risiken

- `StateSerializer` serialisiert private Felder per Reflection und Feldnamen. Das ist komfortabel, aber Versionsmigrationen sind fragil.
- Reflection-basierte Vollserialisierung ist fuer seltene Savestates ok, aber nicht ideal fuer Rewind oder Autosave in kurzen Intervallen.
- Audio-Ausgabepuffer wird bewusst nicht als echter Hardwarezustand behandelt; nach Load koennen kleine Audio-Artefakte auftreten.

### Empfehlungen

1. Savestate-Version pro Subsystem einfuehren.
2. Manuelle Serializer fuer Hot-Subsysteme schreiben, wenn Rewind/Autosaves geplant sind.
3. Savestate-Kompatibilitaetsmatrix dokumentieren: welche Version kann welche Saves laden.

## Performance

### Aktueller Eindruck

Der Emulator ist vermutlich schnell genug fuer normale PAL-Geschwindigkeit, aber die Architektur hat mehrere Hotspots:

- Pro C64-Zyklus laufen CPU, VIC, CIA1, CIA2, SID, Input-Mirroring und ggf. Drive-Catchup.
- VIC rendert pro Cycle acht Pixel und ruft pro Pixel `ComposePixel`, Borderlogik, Graphics-Shifter und Sprite-Compositor auf.
- CPU-BA/AEC-Entscheidung kann `PredictNextCycleAccessType()` aufrufen, was CPU-Zustand kopiert und probeweise ausfuehrt.
- UI kopiert pro Renderframe den kompletten Framebuffer unter Lock in `_frameSnapshot`.
- Savestates nutzen Reflection.
- Die Emulation laeuft in grossen Batches mit globalem Lock; UI- und Emulationsthread konkurrieren beim Snapshot.

### Empfehlungen

1. Erst messen: Benchmark-Harness fuer `RunCycles(1_000_000)` ohne UI, mit SID an/aus, Drive an/aus, VIC an/aus.
2. CPU-Zugriffsvorhersage optimieren: statt Vollsnapshot eine kleine "next bus intent"-State-Machine pro Instruktionsschritt.
3. VIC-Pixelpfad profilieren und entzweigen: schnelle Pfade fuer Border-only, Textmode ohne Sprites, Sprites inactive.
4. Framebuffer-Doppelpuffer ohne langen System-Lock: Swap/Copy nur zu Framegrenzen oder lock-free Snapshot-Versionierung.
5. SID optional decouplen: Audio pro Sampleblock aus Register-Timeline oder zumindest "silent fast mode" fuer Turbo.
6. Drive-Ticking nur fuer aktive/motorisierte/IEC-relevante Drives, mit klaren Wakeup-Bedingungen.
7. Release-Profiling mit BenchmarkDotNet oder ETW/dotTrace.

## Usability und echtes C64-Feeling

### Schon gut

- Overlays fuer Settings, Media und Savestates.
- Turbo, Fullscreen, Joystick-Portwechsel, Drive-LEDs, Save/Load/Delete.
- In-App-Medienbrowser fuer PRG/D64.
- Screenshots/README erklaeren Grundbedienung.

### Was noch fehlt

- Frei konfigurierbare Tastatur- und Joystickbelegung.
- Gamepad-Unterstuetzung.
- Drag-and-drop fuer PRG/D64.
- ROM-Status-/Fehlerdialog mit klaren Pfaden.
- Resetvarianten: Soft Reset, Hard Reset, Power Cycle.
- Disk-Komfort: Disk-Flip, mehrere Images pro Slot, Schreibschutz-Toggle, Drive-Geraeusche optional.
- Debug-/Accuracy-Modi fuer Entwickler: Rasterline, Cycle, IRQ-Status, Drive-PC, Busleitungen.
- PAL/CRT-Videooptionen und Seitenverhaeltnisoptionen.

## Priorisierte Roadmap

### Phase 1: Stabilisieren und sichtbar machen

1. ROM-Resolver und Fehlermeldungen robust machen.
2. CPU-Selftest in CI/Build-Script aufnehmen.
3. Kompatibilitaetsoptionen im UI anzeigen: LOAD-Hack, IEC-Softwaretransport, Input-Injection.
4. Benchmark-Harness einbauen.
5. Build-Warnungen im VIC/Drive bereinigen oder bewusst kommentieren.

### Phase 2: C64-Feeling verbessern

1. Keymap-Editor und Gamepad-Support.
2. Drag-and-drop fuer Medien.
3. CRT/PAL-Filter.
4. Drive-LED/Drive-Sound optional.
5. Better reset/power-cycle UX.

### Phase 3: Accuracy-Ausbau

1. VIC-II-Test-ROM-Suite und Golden-Screenshots.
2. CIA-6526-Timingtests.
3. SID-ADSR-Bug und bessere Waveform-/Filtermodelle.
4. ROM-basierter 1541-Transport aktivieren.
5. G64/NIB-Plan fuer Kopierschutz und echte Bitstream-Szenarien.

### Phase 4: Entwicklerwerkzeuge

1. CPU/VIC/CIA/SID/IEC Tracefenster oder Log-Exporter.
2. Raster-Debug-Overlay.
3. Regressionsrunner fuer PRG/D64-Testprogramme.
4. Screenshot-/Audio-Golden-Master-Tests.

## Konkrete Top-10-Massnahmen

1. `C64System` ROM-Suche von `Directory.GetCurrentDirectory()` loesen und mit klarer Diagnose versehen.
2. Benchmark-Projekt oder `--benchmark` CLI-Modus ergaenzen.
3. CPU-Selftest automatisiert laufen lassen.
4. UI-Schalter fuer Disk-/Input-Kompatibilitaetsbruecken einbauen.
5. VIC-Build-Warnungen aufraeumen und ungenutzte Sequencer-Felder entweder verdrahten oder entfernen.
6. VIC-Test-ROMs als Regression aufnehmen.
7. CIA-TOD/Timer-Edgecases erweitern.
8. SID ADSR-Bug und echte Envelope-Counter implementieren.
9. `CanStartDriveRomTransport()` und ROM-IEC-Modus aktiv entwickeln.
10. Keymap/Gamepad/Drag-and-drop fuer deutlich besseres Nutzungsgefuehl.

## Gesamturteil

Der Emulator ist auf einem sehr guten funktionalen Fundament. Er wirkt aktuell wie ein spielbarer, bewusst pragmatischer PAL-C64 mit wachsender 1541-Unterstuetzung. Der naechste grosse Qualitaetssprung kommt nicht durch noch mehr Einzelhacks, sondern durch drei Dinge: messbare Regressionstests, sichtbare Accuracy-/Kompatibilitaetsprofile und ein konsequenterer VIC/CIA/1541-Timingpfad. Wenn diese Basis steht, kann man gezielt entscheiden, wo "schnell und bequem" und wo "hart nah an echter Hardware" wichtiger ist.
