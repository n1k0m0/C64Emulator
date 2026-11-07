# Open Kernal 64

`kernal.asm` is an independently written NMOS 6510 KERNAL ROM. `assemble.py`
is a small, self-contained two-pass assembler that writes the 8 KiB ROM and a
symbol map. The build combines this source with the independent floating-point
helper sources in the same directory.

The ROM implements the public C64 jump table and selected private entry
points used by Open Basic 64. IEC traffic uses the emulated CIA 2 lines and
the normal drive-side bus state machine. The headless verifier exercises
this path with emulator load shortcuts disabled.

The current source also handles observed private `$E518`, `$FDA3`, and
`$FD15` calls with a shared I/O initialization path and source-checked
`$EA31` IRQ dispatch. The full keyboard matrix, four-mode PETSCII mapping,
and two-counter repeat behavior still need ROM integration. The isolated
evidence and shared regression gates are in
[the private entry report](../../artifacts/kernal-private-entries-integrated-v1/README.md).

Build the open ROM set with `python roms/build.py` from the repository root.
See [the ROM overview](../README.md) for verified behavior and remaining gaps.

The public `UDTIM` entry now increments the 24-bit jiffy counter from `$A2`
through `$A0`, matching the locally installed reference ROM for ordinary
ticks and both byte carries. Its 24-hour reset and returned registers/flags
remain different; the reproducible comparison is in
[the jiffy audit](../../artifacts/kernal-time-v1/README.md).
