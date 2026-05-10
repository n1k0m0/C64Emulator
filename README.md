# C64Emulator

A Commodore 64 emulator written in C# with an OpenTK/SharpPixels rendering frontend, SID audio output, IEC bus handling, savestates, and Commodore 1541 drive emulation.

## Screenshots

| C64 boot screen | Settings overlay | Save/load overlay |
| --- | --- | --- |
| <img src="docs/screenshots/c64-boot-screen.png" alt="C64 boot screen" width="260"> | <img src="docs/screenshots/settings-menu.png" alt="Settings overlay" width="260"> | <img src="docs/screenshots/save-load-menu.png" alt="Save/load overlay" width="260"> |

| Giana Sisters intro | Giana Sisters level 1 |
| --- | --- |
| <img src="docs/screenshots/giana-intro.png" alt="Giana Sisters intro" width="320"> | <img src="docs/screenshots/giana-level-1.png" alt="Giana Sisters level 1" width="320"> |

## Current Features

- MOS 6510 CPU emulation with cycle-oriented execution and support for the implemented official and illegal opcodes.
- VIC-II video output with raster timing, sprites, bitmap/text modes, scrolling, borders, and PAL-oriented display timing.
- SID register handling and audio output.
- CIA1/CIA2 emulation for keyboard, joystick, timers, interrupts, and IEC interaction.
- IEC bus infrastructure with emulated 1541-compatible drives on device numbers 8 to 11.
- D64 disk image mounting and PRG direct loading.
- Drag-and-drop mounting for `.prg` and `.d64` media files.
- Multiple drive slots with per-drive activity LEDs in the footer overlay.
- Host gamepad support for joystick input, alongside keyboard cursor/control mapping.
- Optional sharp-pixel, CRT, and TV-grille video presentation filters.
- Savestates with complete emulator state, screenshot previews, load/delete support, and one-file save packages.
- Windowed/fullscreen controls, turbo mode, joystick port switching, reset mode selection, and runtime settings overlay.
- `SharpPixels`, a small pixel-buffer presentation library used by the emulator frontend.

## Controls

| Key | Action |
| --- | --- |
| `F9` | Toggle turbo mode. |
| `F10` | Open the settings/media overlay. The emulator pauses while this menu is open. |
| `F11` | Toggle fullscreen mode. |
| `F12` | Open the savestate overlay. The emulator pauses while this menu is open. |
| Drag `.prg` / `.d64` onto the window | Load PRG directly or mount D64 into the currently selected target drive. |
| Gamepad left stick / D-pad | C64 joystick direction for the selected joystick port. |
| Gamepad A/B/RB | C64 joystick fire. |
| `S` / `F5` in savestate menu | Create a new savestate. |
| `Enter` / `L` in savestate menu | Load the selected savestate. |
| `Del` in savestate menu | Delete the selected savestate. |
| `Esc` | Close the active emulator overlay. |

## Project Layout

```text
C64Emulator.sln
README.md
LICENSE
docs/
  screenshots/
C64Emulator/
  C64Emulator.csproj
  C64Window.cs
  Program.cs
  Machine/       C64 model, system coordinator, and memory bus
  Cpu/           MOS 6510 CPU, instruction decoder, and trace helpers
  Vic/           VIC-II, video timing, bus planning, and framebuffer
  Sid/           SID emulation and audio output
  Cia/           CIA1/CIA2 peripheral chips
  Input/         Joystick and host input mapping
  Media/         PRG loading, D64 parsing, and mounted media state
  Iec/           IEC bus and high-level drive protocol bridge
  Drive1541/     1541 drive hardware, VIA, bus, and disk mechanism
  Properties/
SharpPixels/
  SharpPixels.csproj
  Input/         OpenTK input compatibility types used by the emulator
  Shaders/
```

## Requirements

- Windows.
- .NET 10 SDK or newer.
- Visual Studio or a compatible `dotnet` CLI/MSBuild installation with .NET 10 support.

## Dependencies

- `OpenTK` 4.9.4 for the OpenGL windowing/rendering path in `SharpPixels`.
- `NAudio` 2.3.0 for SID audio output.

## Build

```powershell
dotnet build C64Emulator.sln -c Release -p:Platform=x64
```

The executable is created at:

```text
C64Emulator/bin/x64/Release/C64Emulator.exe
```

## Diagnostics

The executable also exposes a few headless checks that are useful before accuracy or performance work:

```powershell
C64Emulator\bin\x64\Release\C64Emulator.exe --check-roms
C64Emulator\bin\x64\Release\C64Emulator.exe --self-test-cpu C64Emulator\bin\x64\Release\cpu_self_test.log
C64Emulator\bin\x64\Release\C64Emulator.exe --accuracy-tests C64Emulator\bin\x64\Release\accuracy_tests.log
C64Emulator\bin\x64\Release\C64Emulator.exe --trace-cycles 20000 C64Emulator\bin\x64\Release\trace_cycles.csv 63
C64Emulator\bin\x64\Release\C64Emulator.exe --regression-run "" 500000 C64Emulator\bin\x64\Release\regression_boot.log C64Emulator\bin\x64\Release\regression_boot.ppm
C64Emulator\bin\x64\Release\C64Emulator.exe --benchmark 2000000 C64Emulator\bin\x64\Release\benchmark.log
```

For a single Phase 1 smoke run after a Release build:

```powershell
.\scripts\run-phase1-checks.ps1
```

For the Phase 3 accuracy smoke suite:

```powershell
.\scripts\run-phase3-checks.ps1
```

For the Phase 4 developer-tool smoke suite:

```powershell
.\scripts\run-phase4-checks.ps1
```

The live emulator also has a compact developer overlay on `F8` with raster/cycle, BA, CPU, memory, CIA, SID, IEC, and drive state.

## Media Handling

PRG files are loaded directly into C64 memory. D64 files are mounted into an emulated 1541 drive and accessed through the IEC path instead of being injected into RAM.

Drive 8 is the default drive. Drives 9, 10, and 11 can also be used from the emulator menu when media is mounted for them. Idle drives are kept quiet unless a disk image is mounted.

Media can be selected from the `F10` browser or dropped directly onto the emulator window. Dropped D64 images use the menu's current target drive.

The D64 parser handles the common 35-track layout and extended image sizes where supported by the image parser. Some copy-protected titles or custom fast loaders can still expose gaps in the current 1541 accuracy work.

## Savestates

Savestates are stored as individual files in the `saves` directory next to the emulator executable. A savestate contains the C64 machine state, chip state, mounted drive state, metadata, and a screenshot preview used by the `F12` overlay.

## ROM Files

The emulator expects the required C64 and 1541 ROM binary files in the `C64Emulator/` project directory. They are copied to the output folder during the build.

The currently expected ROM files correspond to these reference files:

| Local file | Reference file | Source URL | SHA-256 |
| --- | --- | --- | --- |
| `c64-basic-kernal.bin` | `64c.251913-01.bin` | <https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/c64/64c.251913-01.bin> | `64E40E09124FC452AE97C83A880B82C912C4F7F74A1156C76963E4FF3717DE13` |
| `c64-character.bin` | `characters.901225-01.bin` | <https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/c64/characters.901225-01.bin> | `FD0D53B8480E86163AC98998976C72CC58D5DD8EB824ED7B829774E74213B420` |
| `1541-c000-rom.bin` | `1541-c000.325302-01.bin` | <https://www.zimmers.net/anonftp/pub/cbm/firmware/drives/new/1541/1541-c000.325302-01.bin> | `6FA7B07AFF92DA66B0A28A52BB3C82FFE310AB0FAD2CC473B40137A8D299C7E5` |
| `1541-e000-rom.bin` | `1541-e000.901229-01.bin` | <https://www.zimmers.net/anonftp/pub/cbm/firmware/drives/new/1541/1541-e000.901229-01.bin> | `1B216F85C6FDD91B91BFD256AFFD9661D79FA411441A57D728D113ECF5B5451B` |

The source directory listings are available at:

- <https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/c64/>
- <https://www.zimmers.net/anonftp/pub/cbm/firmware/drives/new/1541/>

ROM images and disk/program media are not covered by the source-code license. Check the rights for those files before publishing or redistributing a repository or build package.

## Status

The emulator is already useful for BASIC, PRG loading, D64 directory access, several games, savestates, and interactive testing. Cycle accuracy, VIC-II edge cases, and 1541 custom loader compatibility remain the main long-term accuracy areas.

## License

The emulator source code is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).
