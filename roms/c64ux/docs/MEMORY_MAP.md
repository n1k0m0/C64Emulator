# Memory and execution model

## ROM

| Range | Length | Contents |
| --- | ---: | --- |
| `$A000-$BFFF` | 8192 | Shell, filesystem, disk routines, strings, PLAY command front end |
| `$E000-$FFFF` | 8192 | Adapted Open Kernal 64, keyboard, protected PLAY reader/handoff, public jump table, CPU vectors |
| Character ROM | 4096 | Supplied charset, also visible to VIC-II |

The combined emulator image is BASIC-slot bytes first, KERNAL-slot bytes second.
There is no extra bank or modified address decoder. Normal shell mapping keeps
LORAM/HIRAM/CHAREN asserted. The paired ROMs must be selected together.
Both are assembled in one symbol space, while emission into `$C000-$DFFF`
is forbidden. The assembler rejects overlapping emitted bytes. Build metadata
counts occupied bytes from the actual write map: 7,661 in the BASIC slot and
6,727 in the KERNAL slot for version 0.2.0. Remaining space is fragmented.

## RAM

| Range | Use |
| --- | --- |
| `$0000-$0001` | 6510 DDR / bank and cassette control port |
| `$0002-$000F` | Shell text, metadata, data, numeric and dispatch pointers |
| `$0010-$0011` | Disk subsystem pointer |
| `$0012-$002A` | Protected PLAY state while loading; handed back to game at entry |
| `$0100-$01FF` | Native CPU stack |
| `$0200-$03FF` | KERNAL tables, vectors, keyboard queue and device workspaces |
| `$0400-$07E7` | 40 by 25 screen RAM |
| `$0800-$087F` | Interactive input / current script line |
| `$0900-$09FF` | Shell state, arguments, path stack, numeric formatting |
| `$0A00-$0A7F` | One-entry interactive command history |
| `$0B00-$0B0F` | Path component scratch |
| `$0C00-$0DFF` | Snapshot of the executing shell script |
| `$0E00-$0EFF` | Tokenized command arguments |
| `$0F00-$0FFF` | Disk command and filename scratch |
| `$1000-$13FF` | 32 filesystem node records, each 32 bytes |
| `$1400-$15FF` | Transactional import staging |
| `$2000-$5FFF` | Fixed RAM-file data slots, 512 bytes per node index |
| `$6000-$9FFF` | Bounded foreground machine-code application arena |
| `$D000-$DFFF` | Visible VIC, SID, color RAM and CIA I/O region |

This is the shell's active layout. Unlisted RAM is reserved during shell use.
`play` releases `$0800-$CFFF` to the loaded program, including shell variables,
filesystem metadata/data and RAM under BASIC. The shell is not suspended there.

## Filesystem node record

| Offset | Meaning |
| --- | --- |
| 0 | Type: 0 unused, 1 regular file, 2 directory |
| 1 | Parent node index, root refers to itself |
| 2-3 | File length, little endian, maximum 512 |
| 4-19 | Null-terminated ASCII name, maximum 15 characters |
| 20-31 | Reserved |

Node `n` has metadata at `$1000 + 32*n` and file data at `$2000 + 512*n`.
The file length bounds all ordinary reads. Writes refuse length 512 and cannot
spill into the next slot. Directory nodes also reserve a slot but use no data.
Names are canonicalized to ASCII lowercase, while file contents retain case.

## Text and keyboard

Shell text/files are ASCII with LF newlines. Terminal output maps lowercase
ASCII to PETSCII `$41-$5A`, uppercase ASCII to `$C1-$DA`, and LF to carriage
return. The VIC uses the lowercase/uppercase character set (`$D018=$17`).
The reverse mapping is applied on keyboard input. Ordinary punctuation remains
in its C64 glyph positions, so the C64 charset is not a full ASCII terminal.

The scanner evaluates all 64 matrix contacts, separates modifiers, and adds
translated bytes to the standard ten-byte `$0277` queue with length `$C6`.
GETIN removes a byte atomically relative to IRQ-driven queue production.
KERNAL scanner state uses `$0385-$038D`; CHROUT wrapper state uses `$0398-$0399`.

The public CHROUT wrapper preserves X/Y across scrolling. Its inherited screen
implementation still uses `$FB/$FC`; shell pointers deliberately do not.
Shell `putc` additionally preserves its live metadata/data/target pointers during
redirection, so commands can read a file while writing a different file.

## Execution

Reset enters the KERNAL vector, initializes hardware, and jumps through the
cold pointer at `$A000`. `$A002` is the warm shell pointer. Cold start recreates
filesystem defaults. The shell's warm entry resets its stack and input channels
while preserving filesystem metadata and content.

The foreground shell runs one command at a time. `ps` reports this one task;
there is no scheduler, privilege level, virtual memory, signal delivery or
multiuser authentication. A program launched with `run` executes on the same
CPU stack and must cooperate with the memory layout and return with RTS.

The parser supports eight tokens, quotes, and one final output redirection.
Scripts use the same parser. A script is copied into a separate 512-byte buffer
before execution, and nested scripts are rejected.

## PLAY loading and handoff

`play NAME [ENTRY]` opens logical file 2 on device 8, secondary address 0.
It reads the PRG twice through ordinary KERNAL file operations. The first pass
validates a nonempty payload entirely within `$0800-$CFFF`, an entry inside
that payload, the load convention and a CRC-16. The second pass compares
header, exact length and CRC while storing bytes. A first-pass failure leaves
the session intact. A second-pass failure goes directly to a cold reset because
the shell's variables and files can already have been overwritten.

At `$0801`, automatic entry selection requires a single tokenized BASIC `SYS`
statement with a decimal literal. The linked next-line address, preceding
statement terminator and following zero end marker must agree within the first
48 bytes. This is a bootstrap recognizer, not a BASIC interpreter. Other load
addresses default to direct machine-code entry; an explicit decimal or `$hex`
entry overrides either convention.

The protected reader runs from KERNAL ROM. Its active state stays below the
lowest permitted payload address:

| Range | PLAY use |
| --- | --- |
| `$0012-$002A` | Pointer, range, entry, CRC, status, counters and numeric parsing |
| `$0200-$020F` | Up to 16 PETSCII filename bytes |
| `$0210-$0211` | SYS terminator offset and failure category |
| `$0220-$024F` | First 48 payload bytes for SYS recognition |
| `$0250-$0253` | Entry and exclusive end retained during handoff initialization |
| `$0254-$0257` | One-shot second-pass-error marker checked on cold boot |
| `$0260-$026B` | Twelve-byte synthetic RTS return/reset stub |

The entry sequence restores KERNAL RAM state and vectors, initializes I/O and
the standard blue uppercase text screen, clears enabled sprites, and initializes
common BASIC memory pointers. `$2B/$2C=$0801`; `$2D-$32` each receive the
exclusive payload end; `$33/$34` and `$37/$38` are `$A000`; `$39/$3A=$FFFF`.
This supplies useful conventional state without recreating a BASIC interpreter.

Entry registers are A/X/Y=0 and SP=`$FD`, with decimal mode clear and IRQs enabled.
DDR `$00=$2F`. `$01=$36` for an entry under BASIC (`$A000-$BFFF`), otherwise
`$01=$37`. A program entered elsewhere must bank BASIC out itself if it later
executes or reads its own data in `$A000-$BFFF`. Writes to mapped ROM addresses
already go to the underlying RAM.

The synthetic stack return points at `$0260`. Its stub executes SEI, restores
DDR/banking and jumps through the reset vector, so an RTS can reset even with
ROMs banked out. This requires the return address and stub to survive. A program
that replaces the stack, overwrites the stub or never returns must be left using
the emulator's reset facility. No scheduler or hidden shell remains active.
