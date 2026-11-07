# RC3 software IEC DOS and REL files

This is the command contract implemented by the two emulator overrides,
`D64Image.cs` and `IecDrive1541.cs`, for the pinned C64Emulator revision
`f01750e17a2c3f4cabfd57dc0c470dac93a91a23`. It describes the software 1541 that
receives the C64 KERNAL's actual CIA2 IEC signals. The 1541 replacement ROM
still supplies the bootstrap and drive CPU helper entry points; this work does
not turn that ROM into an autonomous, complete drive firmware.

The scope is standard 35-track D64 media, drive number `0` within each emulated
1541, and the C64 serial timing profile. Host device addresses remain 8–11 as
configured by the emulator. Additional D64 tracks are retained, but the DOS
allocator does not allocate them. Filename creation uses the host's existing
16-character printable ASCII naming contract. C64 uppercase PETSCII letters
are translated by the IEC filename decoder.

## Command matrix

Open the command channel with `OPEN 15,8,15`. Commands work in the filename of
`OPEN 15,8,15,"command"` and through `PRINT#15,"command"`. Read the status with
`INPUT#15,E,M$,T,S`. Reading a complete status consumes that response; the next
status is `00, OK,00,00` unless another operation reports an error.

| Command | Implemented behavior and important boundaries |
| --- | --- |
| `N:name,id`, `NEW:name,id` | Full logical format of the 35 standard tracks, new label and two-character ID, initialized BAM and empty directory. A D64 error-information suffix is retained and its standard-sector codes reset to `1`. |
| `N:name` | Quick format: new label and empty directory/BAM; existing ID and unreferenced data-sector bytes remain. |
| `S:pattern[,pattern...]`, `SCRATCH:` | Wildcard deletion; `?` and `*`, plus optional `=S`, `=P`, `=U` or `=L` type qualifier. Frees data and REL side sectors. Locked entries are skipped. Returns `01, FILES SCRATCHED,nn,00`; `nn` is the number removed. Active matching channels return `60`. |
| `R:new=old`, `RENAME:` | Exact-name rename; changes only the directory name field. Existing destination `63`, missing source `62`, active file `60`. |
| `C:new=source[,source...]`, `COPY:` | Copies one file or concatenates up to four compatible files. PRG, SEQ, USR and REL retain their type; REL inputs must have the same record length. Builds independent data/side-sector chains. Mixed types return `64`. |
| `V`, `V0`, `VALIDATE` | Rebuilds the standard BAM from closed file chains and directory sectors, repairs block counts, removes unclosed entries and frees orphan allocation. Rejects cycles, crosslinks and malformed REL indexes with `71`; active writers return `60`. |
| `I`, `I0`, `INITIALIZE` | Closes channels, clears command/transfer state and refreshes the mounted directory. Pending uncommitted channel data is discarded by initialization. Returns `00`. |
| Binary `P` | Positions an open REL channel by record number and optional byte position; see below. |
| `B-A:0 track sector`, `BLOCK-ALLOCATE:` | Persists a BAM allocation. An already allocated block returns `65, NO BLOCK,tt,ss` with the next free coordinate searched from the requested block onward, or `00,00` if none follows. |
| `B-F:0 track sector`, `BLOCK-FREE:` | Persists the free BAM bit without erasing sector contents. Raw allocation commands deliberately allow manual BAM management. |
| `B-R:channel 0 track sector`, `BLOCK-READ:` | Reads a counted block through an allocated `#` buffer. Sector byte 0 gives the data length; reading starts at byte 1, and EOI follows that count. |
| `B-W:channel 0 track sector`, `BLOCK-WRITE:` | Writes the entire buffer, updating byte 0 to the number of data bytes before the current pointer, then resets the pointer to byte 1. Persists the host D64. |
| `B-P:channel position`, `BUFFER-POINTER:` | Sets the direct buffer read/write pointer to `0..255`. |
| `B-E:channel 0 track sector`, `BLOCK-EXECUTE:` | Loads the raw block into the allocated buffer and starts actual emulated 1541 CPU execution at that buffer's RAM address. |
| `U1:` / `UA:` | Reads all 256 sector bytes, including byte 0, through the direct buffer. |
| `U2:` / `UB:` | Writes all 256 buffer bytes without synthesizing a count byte; retains the pointer and persists the host D64. |
| Binary `M-R`, `M-W`, `M-E` | Reads/writes drive memory and starts drive CPU execution. Binary address, count and data bytes are not split at embedded CR/NUL. Omitted M-R count means one byte; count zero reads 256 bytes. |
| `U3..U8`, `UC..UH` | Executes the corresponding RAM entry at `$0500`, `$0503`, `$0506`, `$0509`, `$050C`, `$050F` through the emulator's existing drive CPU mechanism. |
| `U9` / `UI`, `U;` / `UJ` | Resets the host drive/ROM bootstrap and returns the compatible DOS identification status. This is the host reset operation, not a promise that private original-ROM reset vectors have identical internals. |
| `UI+` | Selects the supported C64 timing profile and leaves channels open. |
| `UI-` | Explicitly returns `31`; a separate VIC-20 serial timing profile is outside this C64 host implementation. |
| `D` / `DUPLICATE` | Explicitly rejected. Commodore documents no DUPLICATE function on the single-drive 1541. |
| Unknown command / user selector | Syntax error; never a success-shaped no-op. |

Numeric block parameters accept commas, semicolons or whitespace, including the
spaces produced by BASIC `PRINT#15,"B-R:";channel;0;track;sector`. Long command
names and short spellings are case insensitive. The optional `0` drive prefix
is accepted for filename commands, such as `N0:DISK,01` and `R0:NEW=OLD`.

Direct buffers use channels 2–14. `OPEN 2,8,2,"#"` allocates an available buffer;
`"#0"` through `"#4"` select `$0300` through `$0700`. A first read announces the
allocated buffer number as an ASCII digit. A buffer cannot be allocated twice.
Use U1/U2 for raw sectors and B-R/B-W for their counted-block convention. These
commands operate on raw sectors; they do not automatically create directory
entries or reserve the sector with B-A.

Direct reads reflect the actual drive RAM. A subsequent M-W or uploaded CPU
routine can change bytes in an already positioned buffer; GET# sees those
changes without a second B-P. The existing pointer and counted/raw limit remain.

## REL record semantics

The record length in OPEN is **one binary byte**, from 1 to 254:

```basic
10 OPEN 15,8,15
20 OPEN 2,8,2,"CONTACTS,L,"+CHR$(32)
30 PRINT#15,"P";CHR$(98);CHR$(13);CHR$(0);CHR$(1)
40 PRINT#2,"ALICE,12345"
50 PRINT#15,"P";CHR$(98);CHR$(13);CHR$(0);CHR$(1)
60 INPUT#2,N$,T$
70 PRINT N$;T$
80 CLOSE 2:CLOSE 15
```

`P` consists of the byte `P`, the channel plus 96 (here 98), record low byte,
record high byte, and an optional one-based byte position. A missing or zero
position selects byte 1. Records are numbered 1–65535, subject to the real disk
capacity and six-side-sector limit. The command parser uses the binary field
layout and the IEC message boundary, so record bytes equal to CR or NUL remain
data. When using `PRINT#`, its normal final CR also disambiguates an explicit
position 13: the binary position CR is followed by the statement's final CR.

A position past the currently created records reports `50, RECORD NOT PRESENT`
and retains the requested position so a following write can create it.
Intermediate empty records start with `$FF` and otherwise contain zeros. A
write fills the selected record from the requested position, preserves its
prefix, and zeroes the remaining tail. An overlong write keeps only the bytes
that fit and reports `51, OVERFLOW IN RECORD`; it never spills into the next
record. CR is a data byte in a record. BASIC INPUT# can use it as a field/end
separator. Reading or writing advances to the following record when the current
record is exhausted or its write message is committed; explicit P remains the
reliable way to select a record or field.

Reopening an existing REL by plain name infers its type and length. Supplying a
different length gives `64`; `@` on an existing REL does not erase it. Missing
files require a length at creation. Locked REL files can be read but writes
return `26`. An active REL channel blocks competing file mutations.

The directory contains a closed REL entry, record length, data-chain start,
side-sector start and total block count. Each side sector indexes up to 120
data sectors and repeats the active side-sector coordinates. Records can cross
254-byte data-sector boundaries. Updates write complete data and side-sector
chains, not a host-only metadata substitute. Used pointers and chain order are
validated; unused pointer bytes in existing external images are ignored.

## Persistence and failure behavior

Every successful N/S/R/C/V, allocation operation, raw sector write and REL write
constructs a candidate image, writes and flushes a sibling temporary file, and
replaces the mounted host file before publishing the new in-memory data,
directory and track caches. Invalid parameters, protected images, disk-full
allocation, malformed/crosslinked chains, read-error side sectors and host
replacement failures keep the previous image unchanged. The intentionally
partial REL overflow write is the documented exception: its fitting prefix is
committed and status 51 reports truncation.

This is per-command rollback and visibility. It does not assert host directory
fsync/power-loss durability, cross-process locking against another writer, or a
transaction spanning multiple separately closed file channels. Raw BAM/block
commands can intentionally make a filesystem inconsistent; V rebuilds a BAM
from valid closed files. Scratch rejects unclosed entries with 60; V can remove
them safely without following potentially shared incomplete chains.

REL position, pending write bytes, current record data, lock state and direct
buffer ownership survive emulator savestates. New state is carried in a named
extension field, preserving the previous per-channel binary layout. For B-E/M-E/U3–U8, the host supplies a fresh RTS return frame to its
`$C194` command-loop marker on every call. These DOS features do not validate
arbitrary fastloaders or private proprietary ROM entry points.

## Verification

`tests/emulator/DosRc3Tests.cs` provides `RunMedia` and `RunNative` for the native
integration runner. All fixtures are generated in unique temporary directories;
no user's disk image is used. Media tests inspect complete host-file equality
on failures, reconstruct linked data independently, inspect BAM ownership and
counts, and check REL indexes. An independent hand-built noncontiguous REL
fixture exercises importing and updating a file that was not created by the
production writer. The large case contains 601 data sectors plus six side
sectors, crossing every 120-pointer boundary. Error-map, write-protection,
record-length boundary, sparse record, savestate and host I/O failure cases are
included. A separate IEC pin test delays listener readiness by 2000 cycles and
checks that the talker emits no premature data clocks before DATA hold/release.

Native tests run BASIC and the replacement KERNAL through the real C64 CPU,
CIA2 and IEC implementation, with the load hack and KERNAL IEC hooks disabled.
They cover binary P low/high bytes 13 and 0, position 13, record 3328, real
PRINT#/INPUT#/GET#, status consumption, all main file commands, direct blocks,
allocation, binary memory transfers and actual drive CPU execution. The main
validation report records the final counts and the PAL/NTSC integration runs.

## Primary specifications

The command and record formats were checked against public Commodore manuals;
no original proprietary ROM implementation was copied:

- [Commodore 1541 Disk Drive User's Guide, 1982](https://www.zimmers.net/anonftp/pub/cbm/manuals/drives/1541-manual.txt), chapters 4–8 and disk format tables. The hosted text is a transcription; consult the scan when exact typography matters.
- [Commodore 1541-II User's Guide](https://s3.amazonaws.com/com.c64os.resources/weblog/sd2iecdocumentation/manuals/1541-II_Users_Guide.pdf), relative files and direct-access chapters, including partial-record zero filling and the P command.
