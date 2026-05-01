# C64Emulator

A Commodore 64 emulator written in C# with a custom OpenTK-based rendering frontend, SID audio output, IEC bus handling, and ongoing Commodore 1541 drive emulation.

## Features

- MOS 6510 CPU emulation with cycle-oriented execution.
- VIC-II video output with raster timing, sprites, bitmap/text modes, scrolling, and PAL-oriented display timing.
- SID register and audio output support.
- CIA1/CIA2 emulation for keyboard, joystick, timers, interrupts, and IEC interaction.
- IEC bus infrastructure with emulated 1541 drives on device numbers 8 to 11.
- D64 disk image support and PRG loading.
- Drive activity overlay with per-drive LEDs.
- Windowed, fullscreen, and turbo runtime controls.
- `SharpPixels` rendering library for fast pixel-buffer presentation.

## Project Layout

```text
C64Emulator.sln
README.md
LICENSE
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
  lib/
SharpPixels/
  SharpPixels.csproj
  Shaders/
```

## Requirements

- Windows.
- Visual Studio or MSBuild with .NET Framework project support.
- .NET Framework 4.7.2 Developer Pack if it is not already installed.
- .NET SDK for command-line builds with `dotnet build`.

## Build

```powershell
dotnet build C64Emulator.sln -c Release -p:Platform=x64
```

The executable is created at:

```text
C64Emulator/bin/x64/Release/C64Emulator.exe
```

## Media Handling

PRG files are loaded directly into C64 memory. D64 files are mounted into an emulated 1541 drive and accessed through the IEC path. Drive 8 is the default drive, and additional drives can be used through the emulator menu.

The emulator currently supports D64 images with the common 35-track layout and extended variants where implemented by the image parser. Some copy-protected titles or custom fast loaders may still depend on details that require further 1541 accuracy work.

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

## License

The emulator source code is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

