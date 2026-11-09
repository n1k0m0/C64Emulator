PLAY loader acceptance fixtures
===============================

These small PRGs are original, independent test programs. Their byte streams
are constructed by tests/play_fixtures.py without using the firmware's
assembler, SYS parser or disk reader. License: Apache-2.0; see ../../LICENSE.

Regenerate all PRG files and play-fixtures.d64 from the project directory:

    python tests/play_fixtures.py

Valid test programs:

  SYSDEMO   $0801 BASIC SYS 2064; 6 KiB payload deliberately overwrites the
            shell workspace and filesystem metadata before execution.
  SYSSPACE  $0801 BASIC SYS 2080 with whitespace around the literal address.
  DIRECT    $2000 machine code, using the original PRG load address.
  UNDERROM  $A000 machine code in RAM underneath the BASIC ROM.
  EXPLICIT  $0801 payload without a BASIC stub; use play EXPLICIT $0840.
  BANKRTS   $2000 code that banks ROMs out before RTS.
  TOPRAM    $CFFF, a single RTS at the final supported RAM address.

Intentionally invalid test programs:

  BADLINK   Inconsistent BASIC linked-line pointer.
  EXPR      SYS expression instead of a literal numeric entry.
  MULTILINE More than one tokenized BASIC line.
  BASIC     BASIC-only PRINT program with no machine-code SYS entry.
  LOW       Payload starts below $0800.
  IOAREA    Payload begins in I/O at $D000.
  CROSSIO   Payload starts at $CFFF but crosses into $D000.
  EMPTY     PRG load-address header without a payload.

Successful programs return with RTS and C64/UX cold boots. A successful PLAY
launch releases all shell RAM files. The invalid programs should be rejected
during preflight and preserve the current RAM filesystem.

The separately bundled play-games.d64 contains the playable ROM RUNNER game.
The test runners and their precise coverage limits are documented in
../../tests/integration/README.md.
