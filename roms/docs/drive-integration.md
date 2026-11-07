# 1541 helper: scope, source and emulator ABI

This ROM is the drive helper for **Nils Kopal's C64Emulator**, where normal
IEC/DOS operations are implemented by the drive-side C# state machine.
It is not a self-contained Commodore DOS replacement for a physical 1541.
That distinction describes the target architecture; it does not mean that
normal LOAD/SAVE needs a C64 KERNAL shortcut.

## Source and reproducible build

`src/drive1541.asm` was recovered from the **user-supplied**
`open-1541-dos.bin` in `01-kernal-private-eddd-current-v2.zip`. It contains
editable 6502 instructions, named routine entries and literal format tables.
No original Commodore ROM or external firmware binary was used.

The reconstructed 16,384-byte output is byte-identical to the supplied file:

```text
a512f93736ad31a0dbff01cab1aa39b9571921af77bbc59bd414627b15612522
```

Build it with:

```sh
python src/drive_build.py roms/open-1541-dos.bin --split-dir roms/emulator
```

The optional split produces the filenames recognized by the inspected
emulator: `1541-c000-rom.bin` and `1541-e000-rom.bin`, 8 KiB each. Concatenating
the lower and upper files must reproduce the 16 KiB image exactly.

## Inspected emulator

The integration contract below was checked against the public source at
commit `f01750e17a2c3f4cabfd57dc0c470dac93a91a23` (2026-08-10):

- [Drive1541Hardware.cs](https://github.com/n1k0m0/C64Emulator/blob/f01750e17a2c3f4cabfd57dc0c470dac93a91a23/C64Emulator/Drive1541/Drive1541Hardware.cs)
- [Drive1541Bus.cs](https://github.com/n1k0m0/C64Emulator/blob/f01750e17a2c3f4cabfd57dc0c470dac93a91a23/C64Emulator/Drive1541/Drive1541Bus.cs)
- [IecDrive1541.cs](https://github.com/n1k0m0/C64Emulator/blob/f01750e17a2c3f4cabfd57dc0c470dac93a91a23/C64Emulator/Iec/IecDrive1541.cs)
- [C64System.cs](https://github.com/n1k0m0/C64Emulator/blob/f01750e17a2c3f4cabfd57dc0c470dac93a91a23/C64Emulator/Machine/C64System.cs)

`C64System.ApplyIecTransportMode` routes ordinary commands to the software IEC
drive. `CanStartDriveRomTransport` returns false in this revision. The two
C64-side accelerators, `EnableLoadHack` and `EnableKernalIecHooks`, can both be
disabled while keeping this drive architecture. `ForceSoftwareIecTransport`
must remain true for normal operations with this helper.

## Fixed ROM entry points

| Address | Name in this source | Contract |
|---|---|---|
| `$C000` | `RESET` | Sets serial VIA output data/direction, disables VIA IRQ sources and enters the idle marker. |
| `$C194` | `DOS_COMMAND_LOOP` | Idle and uploaded-code handoff marker. The host recognizes this address and direct RAM jumps to it. |
| `$E654` | `DOS_IDLE_RETURN` | Host fallback marker; jumps to `$C194`. |
| `$F2B0` | `DOS_JOB_DISPATCH` | Entered after the host has prepared an IRQ frame, saved registers and a synthetic JSR return. |
| `$FE7F` | `DOS_JOB_IRQ_RETURN` | `PLA/TAY; PLA/TAX; PLA; RTI`, matching that host frame. |

The builder rejects changed fixed-entry addresses. Their placement matters
to the emulator and to uploaded drive code.

The reset program's endless jump at `$C194` is deliberate. The emulator's
`HasSerialRomInitialization` detects the `$1802=$0E` VIA direction write and
finishes its bootstrap probe. An independent hardware emulator without this
host state machine would see only the idle loop.

## Queue and synthetic IRQ contract

The queue lives at `$00-$05`. Slot `s` uses a track/sector pair beginning at
`$06 + 2*s`. Pending jobs have bit 7 set. The host normally services READ
`$80`, WRITE `$90`, VERIFY `$A0`, SEEK `$B0`, and head-restore `$C0` classes
through its disk image mechanism. It queues `$D0/$E0` buffer-execute jobs for
the drive CPU. Low bit 1 selects an unavailable second mechanism and is
rejected by the execute path with status `$0F`.

For a CPU execute job, `EnterDosJobInterrupt` caps the active stack pointer at
`$BD` when needed, then pushes the following bytes, in this order:

1. interrupted PC high and low;
2. status with U set and the stacked B marker clear;
3. A, X, Y;
4. `$FE`, `$7E`, the return address for an RTS into `$FE7F`.

It sets I and starts execution at `$F2B0`. The helper calls RAM buffers
`$0300`, `$0400`, `$0500`, `$0600` and `$0700` for slots 0 through 4. Normal
buffer code ends in RTS. The dispatcher stores result `$01`, then returns
through `$FE7F` and RTI; the original registers, real processor flags and PC
are restored.

The helper explicitly rejects execute slot 5 with `$02`: a buffer at `$0800`
does not fit in the 1541's 2 KiB RAM. The host scheduler currently iterates six
slots for its own sector shortcuts; this must not be mistaken for six
independent CPU execution buffers in the ROM helper.

Before entering an execute job, the host normalizes `$33=0` and may resolve a
zero track/sector pair using current context `$18/$19`. The helper places the
selected pair's address in `$32/$33`.

## GCR helpers and scratch memory

| Entry | Operation | Inputs / outputs |
|---|---|---|
| `$C200` | Decode a four-byte group | Five GCR bytes at `($3A)` to four bytes at `($3C)`; C set on invalid code. |
| `$CA00` | Encode a four-byte group | Four bytes at `($3A)` to five bytes at `($3C)`. |
| `$CB00` | Encode a data record | Payload pointer `$50/$51`; emits marker `$07`, 256 bytes, XOR checksum, two zero padding bytes. |
| `$C500` | Decode / verify data record | Destination page `$51`; `$56=0` copies, `$56!=0` compares; mismatch sets `$57=$07`; malformed record/checksum sets C. |
| `$C700` | Read requested sector | Uses selected track/sector pointer and live disk VIA/SO input. |
| `$F500` | READ / VERIFY job | Slot in X; result stored to `$00,X`. |
| `$F600` | WRITE job | Slot in X; result stored to `$00,X`; restores disk VIA state. |

Group routines use `$40-$49`; block routines additionally use `$4B-$78`.
The encoded writer buffer is split **255/70** bytes across `$0200` and `$0100`.
The reader consumes the same 325-byte stream using a **256/69** split.
These different splits are intentional and are covered by whole-sector tests.
The active stack must remain above the encoded scratch bytes and below raw
loader scratch at the top of page 1; the host's `$BD` guard is part of that ABI.

## What is verified here

`tests/drive_contract.py` executes the actual ROM under an NMOS 6502 CPU model:

- exact rebuild and fixed entries;
- bootstrap VIA writes and idle destination;
- GCR encoding and decoding across all 256 byte values;
- rejection of every invalid five-bit GCR code;
- whole-record encoding/decoding with five payload patterns;
- successful VERIFY, compare mismatch, and checksum failure;
- `$D0/$E0` execution in each valid buffer, including complete synthetic IRQ
  return-frame restoration;
- wrong-drive and nonexistent-buffer rejection.

These are CPU and data-format tests. They do not establish disk revolution,
SO/VIA timing, every uploaded fastloader's private ROM entry compatibility,
or physical 1541 operation. End-to-end ordinary IEC tests belong in the
native C64Emulator integration harness with the C64 accelerators disabled.
Any passing native test demonstrates that exact target/configuration and
test case; it is not evidence of universal fastloader compatibility.

## Native file-transfer verification and file-I/O patch

The native headless test harness compiles the actual public emulator core; its
`EnableLoadHack` and `EnableKernalIecHooks` options are false. A generated D64
fixture loads over the real C64 CIA2 IEC pins, places exact bytes in RAM and runs
the expected program. This establishes the intended host-assisted path with this
bootstrap ROM, without claiming a standalone drive firmware implementation.

The unmodified host does not commit a new PRG on SAVE: native SAVE returns READY
but reloading reports FILE NOT FOUND, and reopening the D64 finds no new entry.
The optional patch in `../emulator-patches/` distinguishes filename/data payloads,
commits writing channels at CLOSE, and atomically persists file/BAM/directory bytes.
The patch also restores unread prefetched data when a file channel pauses at
UNTALK. RC2 introduced PRG, SEQ and USR files through the channel path, including
creation, explicit replacement, and append. The PRG LOAD path keeps its existing
file-type restriction. File-type qualifiers and errors are checked at OPEN and
again before commit; a rejected write leaves the image unchanged.

With this separate host patch, the native SAVE/NEW/LOAD/RUN, fresh-file reopen,
separate GET# statements, end-of-file handling, SEQ record creation/append and
INPUT#, binary USR append, and repeated status-channel checks pass. The additional
media tests exercise exact bytes and persistence directly. RC3 adds real D64
REL data/side-sector chains and the DOS command matrix documented in
[dos-commands.md](dos-commands.md). B-E, M-E and U3–U8 also exercise actual
uploaded drive CPU code. Their host handoff now supplies a valid RTS return
frame to `$C194`, so ordinary IEC commands can resume after the code returns.
See the [main README test instructions](../README.md#selbst-bauen-und-prüfen)
and the JSON reports for reproducible conditions. These fixes are distributed
as emulator changes; changing the drive ROM cannot repair missing host
persistence or channel-state operations.
