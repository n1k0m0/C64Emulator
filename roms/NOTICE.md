# Source provenance and licenses

## Microsoft BASIC

The BASIC interpreter is derived from **Microsoft BASIC for the 6502,
version 1.1**, released by Microsoft under the MIT license:

- Official source: <https://github.com/microsoft/BASIC-M6502>
- Official release announcement:
  <https://opensource.microsoft.com/blog/2025/09/03/microsoft-open-source-historic-6502-basic/>
- Official commit: `7460af2c03ae19c0e60ff327489229d2005b9357`.
- Original `m6502.asm` SHA-256:
  `8f9e48dcae34eefce0462f1e7c4379dfe2c8aa98c0b772041d9989b40bcbff3b`.
- Copyright (c) Microsoft Corporation. Full license: `licenses/Microsoft-MIT.txt`.

The maintained ca65 representation used as the starting point is
`m6502-cbm.s` from <https://github.com/rene6502/BASIC-M6502-CC65>, commit
`fa31daddafa6f1280efce9d66f60a4d2bf4ea474`. Its original MACRO-10 source and
license were checked byte-for-byte against Microsoft's official repository.
The conversion project supplies `Converter.java` and `Formatter.java`; it
identifies the Commodore target as PET BASIC 2. This is a C64 port of the
licensed interpreter, not a claim that Microsoft released the C64 ROM image.

Port changes include:

- Code placement in `$A000–$BFFF`, with an extension in `$E000–$E4BF`.
  RC3 reserves `$E4C0–$E4FF` for the independent PAL/NTSC timing helper.
- C64 cold/warm vectors, selected RAM vectors, and the standard BASIC zero-page
  locations, while moving PET-only terminal variables to reserved RAM.
- C64 program start `$0801`, top of RAM `$A000`, screen column, status, jiffy
  clock and CIA timer references.
- Public-KERNAL adapters for BASIC file commands, channel errors and SYS.
- C64 keyboard line completion without the duplicate PET newline.
- Live BASIC RAM-vector dispatch, C64 error numbers, STOP/CONT handling,
  program LOAD chaining, and logical-file-dependent CR/LF output.
- A new boot banner. The interpreter's upstream credits and MIT license remain.

`src/mit_basic/io_adapters.s`, `platform_adapters.s` and the related I/O tests are newly written
integration code distributed under MIT. No proprietary C64 BASIC or KERNAL
source/binary was used as an input for this port.

## User-provided Open-ROM project

The KERNAL, small assembler and drive helper originate from the supplied project
archive `01-kernal-private-eddd-current-v2.zip`. Its supplied KERNAL and assembler
sources carry `SPDX-License-Identifier: Apache-2.0`; those notices are preserved.
The new KERNAL, keyboard, character generator, build tools and ordinary regression
tests use the same license, with the full text in `LICENSE`.

The RC2 screen editor, tape EOT support, and `src/system_mit.asm` STOP/NMI/BRK
and cartridge-header handling are independently written additions. Public
interface facts come from the Commodore 64 Programmer's Reference Guide and
User's Guide, as referenced by the contract tests. Original Commodore firmware
source and binary images are not build inputs.

RC3 adds relocated screen addressing, IRQ-driven cursor display, physical
cassette waveform handling, expanded serial handshaking and model-dependent
timing. Its host additions implement D64 REL side sectors, the documented DOS
command interface, TAP transport, user-port pins and an NTSC 6567R8 model.
These are independently written from the published functional contracts;
see `docs/dos-commands.md` and `docs/tape-serial-video.md` for references and
the exact implemented scope. The emulator modifications and new host/test
files use Apache-2.0, consistent with the reference project.

The drive source was reconstructed from the **user-provided own Open-ROM binary
and map**, not from a Commodore ROM. Before maintenance, assembly reproduced its
16,384-byte input byte-for-byte. The delivered helper retains that binary:

`a512f93736ad31a0dbff01cab1aa39b9571921af77bbc59bd414627b15612522`.

The text-glyph data was transcribed from the supplied `open-character-rom64.bin`;
the geometry generator implements the published screen-code meanings using new
pixel-shape descriptions. No external font or proprietary character-ROM binary
is a build input. The reference assignments are documented in the generator and
its tests; a matching shape meaning does not imply identical font pixels.

The earlier recovered custom BASIC and floating-point extensions are not used in
this delivered build. Their repair work remains separate from this MIT-based
replacement.

## Emulator integration and optional patch

The reference emulator is <https://github.com/n1k0m0/C64Emulator>, commit
`f01750e17a2c3f4cabfd57dc0c470dac93a91a23`.
Copyright 2026 Nils Kopal; Apache-2.0. The included override source files retain
the original copyright/license headers. The original reference checkout was
not modified during testing. The native test project compiles that actual core
plus a silent audio-output implementation, and optionally the separately
identified compatibility patch.

## Build and test tools

The BASIC source was built with ca65/ld65 from cc65 commit
`77b6484615427746e55fda2755c9cbc320a1fb51`. Compiler binaries are not bundled.
The isolated 6502 tests use `py65==1.2.0`; it is a test dependency, not part of
any ROM. The native harness uses .NET 10.0.100.
