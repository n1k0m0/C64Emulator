# RESTORE-Taste im Referenz-Emulator

Die in den RC3-Patches enthaltene RESTORE-Anbindung verbindet **Bild auf / Page Up** mit der externen
NMI-Leitung des C64. Der untersuchte Emulatorstand stellt diese Leitung bereits
bereit, verbindet bisher aber keine normale Frontend-Taste damit.

Die vorhandene Tastaturbelegung verwendet **Tab für RUN/STOP**. Mit diesem
Patch bedeutet daher:

- **Tab:** RUN/STOP über die normale CIA1-Tastaturmatrix.
- **Bild auf:** RESTORE über NMI; die ROM-Routine entscheidet über die Wirkung.
- **Tab halten, Bild auf drücken:** Rückkehr zu BASIC mit erhaltenem Programm.

Eine gehaltene Taste bleibt ein NMI-Pegel, sodass die CPU nur die erste
steigende Flanke als Interrupt behandelt. Loslassen gibt die Leitung frei.
Warm-/Kaltreset und Layoutwechsel geben die Leitung ebenfalls frei; beim
Laden eines Savestates wird sie an die gespeicherten Host-Tasten angepasst.

RC3 enthält diese Änderung in `C64Emulator/Machine/C64System.cs`. Sie ist in
allen drei angebotenen Installationswegen enthalten; ein separater
RESTORE-Patch ist nicht mehr erforderlich. Der Upgrade-Patch
`0002-rc2-to-rc3.patch` erwartet RC2 einschließlich seiner damaligen
RESTORE-Erweiterung. Für RC2 ohne diese optionale Erweiterung dient
`0003-rc2-without-restore-to-rc3.patch`. Beide führen zum gleichen RC3-Code.

Für die reproduzierbaren nativen Tests liegt die vollständige geänderte Datei
unter `overrides/C64System.cs`. Das Testprojekt verwendet sie, wenn dieser
Override-Ordner angegeben ist. Der Test sendet die echten Frontend-KeyDown/
KeyUp-Ereignisse, ohne die ROM-NMI-Routine zu ersetzen.
