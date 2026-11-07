# Cassette, RS-232 and PAL/NTSC — RC3

This document describes the implemented ROM/host contract and its verification.
The released source and final shared reports are in this package. Physical
Datasette and electrical RS-232 hardware are not attached to the test environment.

## Cassette execution

The host now supplies actual cassette pins. Processor-port bit 5 controls the active-low motor, bit 3 supplies WRITE transitions, and input bit 4 supplies the pressed PLAY/RECORD sense. While recording, rising WRITE edges become measured TAP intervals. Playback advances one emulated CPU cycle at a time and feeds falling READ edges into CIA1 FLAG. FLAG latches in the CIA interrupt-control register even if its interrupt mask is clear. The KERNAL reads its own timers and FLAG events; there is no byte reader, pulse primitive, LOAD or SAVE interception.

`TapeDeck` exposes `Mount`, `Play`, `Record`, `Stop`, `Rewind`, `Eject`, `ExportTap` and `ConfigureClock`. Recordings can be replayed with Stop/Rewind/Play without mounting the exported file. Eject stops the transport and clears media and position while retaining the configured CPU clock. CPU reset updates the processor port and motor state without ejecting a mounted tape or releasing a physical transport key.

Two ROM defects were exposed by native waveform tests:

1. VIC badline DMA stretched medium SAVE pulses into the byte-marker class. Tape operations now temporarily disable DEN and sprite DMA and restore D011's readable display-control bits and the D015 sprite-enable byte on every started-transport return, including errors. Caller IRQ state is restored. A long filename is rejected before entering this save/restore path.
2. After an early parity failure, scanning the remaining first data copy encountered the block-end marker as an invalid byte and aborted before reaching the duplicate. Synchronization search now ignores recoverable invalid bytes and terminates on an actual timed end-of-tape. Checksum and parity failures in the first copy can therefore reach the second copy.

The API preserves D011 bits 0–6 and D015. D011 bit 7 reads the current raster's high bit, not the write-only raster-IRQ comparator high bit; this is not a complete saved VIC programming context. Applications configuring custom raster IRQs should reestablish that programming after tape operations.

TAP v1 is the cycle-exact, independently exercised file path. TAP v0 is accepted; its zero byte contains an unspecified overflow, so playback deterministically uses the minimum 2048-cycle overflow and cannot recreate a longer lost interval. The parser also accepts C64-tagged v2 halfwaves and has a component test for their edge order. C16 signatures/platforms are rejected; this is not a general C16 tape implementation or a claim of C16 interoperability. v2 is treated as a sequence whose first encoded transition is rising; mounting/starting does not synthesize a spurious falling FLAG edge.

TAP headers, exact payload sizes, versions, machine tags, video standards and extended durations are validated before mounted media changes. Playback converts source-clock cycles to CPU-clock cycles with a fractional remainder so rounding does not accumulate once per pulse. PAL and NTSC are exercised as actual native machines. The independent PAL fixture also runs on NTSC through this clock conversion.

## Independent waveform evidence

The new generator is independent of the ROM writer, ROM symbols, ROM timing tables and captured SAVE output. It uses the published C2N format diagram: three full-cycle widths derived from 182/262/342 microsecond half cycles, ordered short/medium bit pairs, least significant bit first, odd parity, descending nine-byte countdowns, duplicated blocks and XOR checksums. A separate host decoder checks the captured SAVE output against byte structure, parity, countdowns and checksums.

The canonical harness separates these into `tape-format` (three component assertions) and `tape` (18 waveform assertions), in addition to its shared model-specific boot:

1. TAP v0/v1 boundary values and atomic rejection of nine malformed containers.
2. C64-tagged halfwave edge order and motor-off pause/resume.
3. Fractional-carry clock conversion over 1000 pulses.
4. Real SAVE with EOT, motor shutdown and display/sprite/IRQ restoration.
5. Independent decoding of both saved headers, both data copies and both EOT blocks.
6. Exact pulse preservation across a written/reopened TAP file.
7. Recorded SAVE replayed through real LOAD without remount.
8. Independently generated C2N standard TAP loaded through CPU/CIA/FLAG.
9. All 256 byte values plus a page crossing in a 257-byte native load.
10. VERIFY success through real waveform input.
11. VERIFY mismatch with original RAM preserved and error `$1C`.
12. First header checksum error repaired by the duplicate.
13. First data checksum error repaired by the duplicate.
14. First data parity error repaired by the duplicate.
15. Both data checksums bad, error returned.
16. Both header checksums bad, error returned.
17. Type-1 relocatable header honors the caller's destination.
18. Named search stops at a type-5 EOT header and sets READST bit 7.
19. Unnamed search stops at a type-5 EOT header and sets READST bit 7.
20. Truncated physical waveform reaches a finite timer timeout, marks EOT and restores state.
21. Rejected long name preserves display state without starting transport.

All native tape operations deliberately start with DEN and all eight sprite enables on. The test does not disable DMA for the ROM. Success/failure cases assert restored display bits, sprite enables, motor-off and caller interrupt-enable state. Only the three container/transport component cases directly instantiate a deck; every SAVE/LOAD/VERIFY/EOT/error case executes the native CPU and CIA path.

Optional `FREE_ROM_TAPE_ARTIFACTS=<directory>` emits:

- `rom-save-with-eot.tap`
- `independent-c2n-pal.tap`
- `independent-first-data-checksum-bad.tap`
- `independent-type5-eot.tap`

Set separate directories for PAL and NTSC runs. `FREE_ROM_TAP_TRACE=<filename>` additionally writes the captured SAVE alone. The independent fixture has a PAL source clock even in the NTSC test run.

## RS-232 behavior

The user port is modeled at TTL pin level. No socket or OS serial adapter is silently attached. `C64System.UserPort` gives an external peer `SetSerialInputs(receiveHigh, ctsAsserted, dsrAsserted, dcdAsserted)`, `SetInputPins`, a per-cycle `Ticked` callback, an `OutputChanged` callback and observable pins/cycle count. Asserted handshake signals are TTL-low; an external RS-232 level converter supplies the electrical inversion.

| Signal | C64 pin |
| --- | --- |
| TXD | CIA2 PA2 |
| RXD | CIA2 PB0 |
| RTS | CIA2 PB1, output |
| DTR | CIA2 PB2, output |
| DCD | CIA2 PB4, input |
| CTS | CIA2 PB6, input |
| DSR | CIA2 PB7, input |

Command bit 0 selects 3-line or X-line. Bit 4 selects full or half duplex. Bits 1–3 remain rejected. Word length, parity and stop selection keep the established driver behavior. Standard periods are selected from PAL or NTSC tables by the ROM-detected `$02A6` flag; custom timer mode remains available.

- In 3-line mode, modem gate inputs are ignored and RTS/DTR remain deasserted.
- X-line OPEN asserts DTR, leaves RTS released and configures only PB1/PB2 as outputs.
- CHKOUT/CHROUT assert RTS and require asserted CTS and DSR.
- Full-duplex CHKIN/CHRIN/GETIN assert RTS and require asserted CTS, DSR and DCD.
- Half-duplex input releases RTS and waits for released CTS, with DSR and DCD asserted.
- Missing DSR returns immediately with RSSTAT bit `$40`; a finite 65536-poll wait reports missing CTS as `$10` or missing carrier as `$08`. Errors return carry set and A=5.
- READST returns and acknowledges RSSTAT. CLOSE releases RTS/DTR and returns TXD to idle-high.
- Each frame masks ordinary IRQs and restores the caller's I state; modem waits remain interruptible. Caller X/Y are preserved.
- IEC activity preserves the live CIA2 bank bits. Nonserial channel selection retains its original successful carry; a native integration finding corrected a comparison that had accidentally set carry for device 8.

CIA2 savestate loading preserves the `UserPort` object identity and attached peer callbacks. Physical data fields are restored into the existing object; delegates are not serialized. An external peer is still an external system: its own independent protocol state is not automatically rolled back by a machine savestate.

The independent native serial suite contains 21 scenarios on each model, including 50/300/1200/2400 baud, physical TX decoding, timed RX frames, parity/framing faults, delayed/missing CTS, DSR loss, carrier hold/release, full/half duplex, 3-line behavior, READST acknowledgement and CLOSE line release. The existing 90 CPU framing/rate cases also pass on the new driver.

This remains a synchronous driver. There is no interrupt-driven full-duplex receive/transmit FIFO, background buffering, autonomous flow control during arbitrary caller computation, or attached host COM-device backend. Half/full duplex select the documented line protocol; they do not turn a synchronous ROM into two concurrently buffered workers.

## PAL and NTSC model

The same ROM pair detects actual raster progression. The 64-byte helper at
`$E4C0–$E4FF` distinguishes PAL's raster 288 from the earlier NTSC frame wrap,
sets `$02A6=1` for PAL or 0 for NTSC, and selects a CIA1 timer-A period for
60 Hz KERNAL jiffies. A bounded fallback supports CPU fixtures with static VIC
registers. BASIC's extension ends at `$E4BF`; the linker/build reject overlap.
The emulator does not inject the ROM's model flag.

| Model | CPU Hz | Raster lines | Cycles/line | Visible framebuffer | Frame Hz |
| --- | ---: | ---: | ---: | --- | ---: |
| PAL 6569 | 985248 | 312 | 63 | 403×284 | 50.124542 |
| NTSC 6567R8 | 1022727 | 263 | 65 | 418×235 | 59.826089 |

The frame frequency is CPU Hz / (lines × cycles). KERNAL jiffies remain 60 per second,
independent of the frame frequency. Both CIA clocks, the cassette converter,
SID sample production and drive scheduling receive the selected model clock.
The NTSC visible region wraps raster 0–12 below raster 262; raster 13–40 is blanked.
Sprite row calculations and collisions remain in physical raster coordinates
so that this viewport transform cannot suppress valid offscreen collisions.
Late sprite fetches use cycles 60/62/64 under NTSC and 58/60/62 under PAL.

`VideoStandardTests.cs` tests model constants, exact raster/frame periods,
line260 IRQ/acknowledgement, sprite and badline bus slots, SID sample counts,
command-line parsing, completed frames, ROM model detection, the 60 Hz clock,
and actual pixels of the last text row. It also checks model-matched savestate
restoration and rejects a mismatched model before RAM mutation.
`VideoSpriteTests.cs` adds actual pixel/collision controls and a collision
behind the closed border. See the final JSON group counts in `VALIDATION.md`.

The model is the common 6567R8, not a family-wide certification of every VIC
revision, analog composite behavior, undocumented border trick or fastloader.
The existing core's other VIC behaviors have not all been revalidated against
physical chips. The selected model also applies to the interactive frontend:
`C64Emulator.exe --video ntsc`; PAL is the default. Shorter NTSC Settings and
Network overlays fit the viewport while retaining keyboard navigation.
The Windows application was compiled, and those layouts were reviewed
statically; no Windows desktop interaction was performed here.

Savestates carry a model signature before mutable machine data. Loading a
PAL snapshot into NTSC or the reverse rejects early. Older PAL snapshots
without the new model header are accepted; NTSC requires an identified model.
A peripheral snapshot preserves tape pulses/transport and restores user-port
pins while retaining attached callback objects. A remote peer or physical
external device has its own state that a machine snapshot cannot rewind.

## Physical validation still required

No result above is a claim of real Datasette or electrical RS-232 verification. A concrete next hardware run should record and replay a generated payload on PAL and NTSC machines, capture WRITE rising/READ falling intervals, verify both header/data copies and EOT, check motor/sense and restore behavior, then repeat on a separately recorded tape. Tape speed variation, analog level thresholds, head alignment, motor spin-up and media defects are not modeled.

For serial hardware, use a correctly wired user-port TTL-to-RS-232 interface and a programmable peer to exercise each baud/format and RTS/CTS/DTR/DSR/DCD transition, then hold each readiness input inactive to observe the defined error. Electrical thresholds, inversion through a specific adapter, cable loading and real peer delays need bench evidence. No physical measurement is marked passed in this release.

## Primary references

- Christian Bauer, original VIC-II research, model dimensions and cycle diagrams: https://www.cebix.net/VIC-Article.txt

- Commodore VIC C2N technical/service material, including the tape-format diagram and rising-WRITE/falling-READ convention: https://www.scribd.com/document/971226317/Commodore-1531-Technical-Manual
- VICE TAP format specification: https://vice-emu.sourceforge.io/vice_17.html
- Commodore 64 Programmer's Reference Guide, command bits: https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_6/page_351.html
- PRG, CHKIN/input handshake: https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_6/page_353.html
- PRG, CHKOUT/CLOSE: https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_6/page_354.html
- PRG, signal pins and status: https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_6/page_355.html

The implementation does not copy or analyze proprietary ROM routines. Public interface and signal descriptions are the contract; native tests expose the resulting behavior.
