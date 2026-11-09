; SPDX-License-Identifier: Apache-2.0
; Open Kernal 64 / C64 Unix ROM edition -- independent NMOS 6510 code.
; Derived from the user-supplied Apache-2.0 Open Kernal source.
; BASIC floating-point helpers are intentionally absent in this ROM edition.
; Public entry addresses and RAM conventions follow the published C64 manual.
; The public jump table, selected BASIC glue, screen/keyboard, clock, and IEC
; protocol are implemented here; device limits are documented in README.

.org $f900
tape_save:
    lda $b7
    cmp #$11
    bcs tape_save_error
    lda $b9
    cmp #$01
    beq tape_save_basic
    lda #$03
    bne tape_save_type
tape_save_basic:
    lda #$01
tape_save_type:
    sta $0364               ; program header type
    lda $fb
    sta $0365               ; source start low
    lda $fc
    sta $0366               ; source start high
    lda $0355
    sta $0367               ; exclusive end low
    lda $0356
    sta $0368               ; exclusive end high
    lda $01
    ora #$08               ; idle cassette WRITE high
    sta $01
    jsr tape_motor_start
    lda #$89
    jsr tape_write_header_block
    lda #$09
    jsr tape_write_header_block
    lda #$89
    jsr tape_write_data_block
    lda #$09
    jsr tape_write_data_block
    jsr tape_motor_stop
    lda #$00
    clc
    jsr restore_interrupts
    rts
tape_save_error:
    lda #$08
    sec
    jsr restore_interrupts
    rts

; Each standard tape block has a short-pulse leader, nine countdown bytes,
; a payload, its XOR check byte, and a long/short end marker.
tape_write_header_block:
    sta $036f
    jsr tape_write_pilot
    lda $036f
    jsr tape_write_sync
    lda #$00
    sta $0369
    ldx #$00
tape_write_header_next:
    cpx #$05
    bcs tape_write_header_name
    lda $0364,x
    jmp tape_write_header_emit
tape_write_header_name:
    cpx #$15
    bcs tape_write_header_padding
    txa
    sec
    sbc #$05
    tay
    cpy $b7
    bcs tape_write_header_padding
    lda ($bb),y
    jmp tape_write_header_emit
tape_write_header_padding:
    lda #$20
tape_write_header_emit:
    sta $0372
    eor $0369
    sta $0369
    lda $0372
    jsr tape_write_byte
    inx
    cpx #$c0
    bne tape_write_header_next
    lda $0369
    jsr tape_write_byte
    jmp tape_write_end

tape_write_data_block:
    sta $036f
    jsr tape_write_pilot
    lda $036f
    jsr tape_write_sync
    lda $0365
    sta $fb
    lda $0366
    sta $fc
    lda #$00
    sta $0369
tape_write_data_next:
    lda $fb
    cmp $0355
    bne tape_write_data_emit
    lda $fc
    cmp $0356
    beq tape_write_data_done
tape_write_data_emit:
    ldy #$00
    lda ($fb),y
    sta $0372
    eor $0369
    sta $0369
    lda $0372
    jsr tape_write_byte
    inc $fb
    bne tape_write_data_next
    inc $fc
    jmp tape_write_data_next
tape_write_data_done:
    lda $0369
    jsr tape_write_byte
tape_write_end:
    lda #$03
    jsr tape_emit_pulse
    lda #$01
    jsr tape_emit_pulse
    rts

tape_write_pilot:
    lda #$00
    sta $0374
    lda #$04               ; 1024 short pulses per block
    sta $0375
tape_write_pilot_next:
    lda #$01
    jsr tape_emit_pulse
    dec $0374
    bne tape_write_pilot_next
    dec $0375
    bne tape_write_pilot_next
    rts

tape_write_sync:
    sta $036f
    lda #$09
    sta $0376
tape_write_sync_next:
    lda $036f
    jsr tape_write_byte
    dec $036f
    dec $0376
    bne tape_write_sync_next
    rts

; A is the byte; each bit is emitted least significant first with odd parity.
tape_write_byte:
    sta $036d
    lda #$01
    sta $036e
    lda #$03
    jsr tape_emit_pulse
    lda #$02
    jsr tape_emit_pulse
    lda #$08
    sta $036c
tape_write_bit_next:
    lsr $036d
    lda #$00
    rol A
    sta $0372
    eor $036e
    sta $036e
    lda $0372
    jsr tape_write_bit
    dec $036c
    bne tape_write_bit_next
    lda $036e
    jsr tape_write_bit
    rts
tape_write_bit:
    beq tape_write_zero
    lda #$02
    jsr tape_emit_pulse
    lda #$01
    jmp tape_emit_pulse
tape_write_zero:
    lda #$01
    jsr tape_emit_pulse
    lda #$02
    jmp tape_emit_pulse

; A=1 short, 2 medium, 3 long.  The latch values include the ROM loop work
; between WRITE rising edges.  The verifier checks the resulting TAP classes.
tape_emit_pulse:
    cmp #$01
    beq tape_emit_short
    cmp #$02
    beq tape_emit_medium
    lda #$3c               ; 572 timer cycles, plus call overhead
    ldy #$02
    bne tape_emit_start
tape_emit_medium:
    lda #$c4               ; 452 timer cycles
    ldy #$01
    bne tape_emit_start
tape_emit_short:
    lda #$04               ; 260 timer cycles
    ldy #$01
tape_emit_start:
    pha
    lda $01
    and #$f7               ; WRITE low
    sta $01
    pla
    sta $dc06
    sty $dc07
    lda $dc0d
    lda #$19               ; force-load, one-shot CIA1 timer B
    sta $dc0f
tape_emit_wait:
    lda $dc0d
    and #$02
    beq tape_emit_wait
    lda $01
    ora #$08               ; one rising WRITE edge
    sta $01
    rts

; Optional synchronous RS-232 framing.  The original 8N1 path keeps its
; established bit timing; this path handles 5-7 data bits, parity and 2 stops.
.org $fac0
rs232_data_count:
    lda $0293
    and #$60
    lsr A
    lsr A
    lsr A
    lsr A
    lsr A
    tax
    lda rs232_widths,x
    sta $0298
    rts

rs232_transmit_extended:
    sta $034a
    pha
    txa
    pha
    jsr rs232_data_count
    lda #$00
    sta $0384              ; number of one bits
    jsr rs232_tx_low
    jsr rs232_delay_full
    ldx $0298
rs232_extended_tx_bit:
    lsr $034a
    bcc rs232_extended_tx_zero
    inc $0384
    jsr rs232_tx_high
    jmp rs232_extended_tx_wait
rs232_extended_tx_zero:
    jsr rs232_tx_low
rs232_extended_tx_wait:
    jsr rs232_delay_full
    dex
    bne rs232_extended_tx_bit
    lda $0294
    and #$20
    beq rs232_extended_tx_stop
    lda $0294
    and #$80
    bne rs232_extended_tx_fixed
    lda $0384
    and #$01
    sta $0385
    lda $0294
    and #$40
    bne rs232_extended_tx_parity
    lda $0385
    eor #$01               ; odd parity
    sta $0385
    jmp rs232_extended_tx_parity
rs232_extended_tx_fixed:
    lda $0294
    and #$40
    beq rs232_extended_tx_mark
    lda #$00               ; space parity
    beq rs232_extended_tx_fixed_store
rs232_extended_tx_mark:
    lda #$01               ; mark parity
rs232_extended_tx_fixed_store:
    sta $0385
rs232_extended_tx_parity:
    lda $0385
    beq rs232_extended_tx_parity_zero
    jsr rs232_tx_high
    jmp rs232_extended_tx_parity_wait
rs232_extended_tx_parity_zero:
    jsr rs232_tx_low
rs232_extended_tx_parity_wait:
    jsr rs232_delay_full
rs232_extended_tx_stop:
    jsr rs232_tx_high
    jsr rs232_delay_full
    lda $0293
    bpl rs232_extended_tx_done
    jsr rs232_delay_full    ; second stop bit
rs232_extended_tx_done:
    pla
    tax
    pla
    clc
    rts

rs232_receive_extended:
    txa
    pha
    jsr rs232_data_count
    jsr rs232_delay_full
    jsr rs232_delay_half
    lda #$00
    sta $034a
    sta $0384
    ldx $0298
rs232_extended_rx_bit:
    lda $dd01
    lsr A
    bcc rs232_extended_rx_zero
    inc $0384
rs232_extended_rx_zero:
    ror $034a
    jsr rs232_delay_receive
    dex
    bne rs232_extended_rx_bit
    ldx $0298
rs232_extended_rx_align:
    cpx #$08
    beq rs232_extended_rx_parity
    lsr $034a
    inx
    jmp rs232_extended_rx_align
rs232_extended_rx_parity:
    lda $0294
    and #$20
    beq rs232_extended_rx_stop
    lda $dd01
    and #$01
    sta $0385
    lda $0294
    and #$80
    bne rs232_extended_rx_parity_wait
    lda $0384
    and #$01
    sta $0386
    lda $0294
    and #$40
    bne rs232_extended_rx_compare
    lda $0386
    eor #$01
    sta $0386
rs232_extended_rx_compare:
    lda $0386
    cmp $0385
    bne rs232_extended_rx_parity_error
rs232_extended_rx_parity_wait:
    jsr rs232_delay_receive
rs232_extended_rx_stop:
    lda $dd01
    and #$01
    beq rs232_extended_rx_frame_error
    lda $0293
    bpl rs232_extended_rx_done
    jsr rs232_delay_receive
    lda $dd01
    and #$01
    beq rs232_extended_rx_frame_error
rs232_extended_rx_done:
    pla
    tax
    lda $034a
    clc
    rts
rs232_extended_rx_parity_error:
    lda $0297
    ora #$01              ; RSSTAT parity error
    sta $0297
    jmp rs232_extended_rx_error
rs232_extended_rx_frame_error:
    lda $0297
    ora #$02              ; RSSTAT framing error
    sta $0297
rs232_extended_rx_error:
    pla
    tax
    lda #$00
    sec
    rts
rs232_widths:
    .byte $08,$07,$06,$05

; READST reports the most recent external device's status. RS-232 has its
; own register, which is acknowledged on read; tape and IEC retain $90.
.org $fcd5
rs232_read_status:
    lda $ba
    cmp #$02
    bne rs232_read_default
    lda $0297
    pha
    lda #$00
    sta $0297
    pla
    rts
rs232_read_default:
    lda $90
    rts
rs232_poll_empty:
    lda $0297
    ora #$08              ; no received character available to GETIN
    sta $0297
    lda #$00
    clc
    rts
tape_mark_end:
    lda $90
    ora #$80              ; cassette end-of-tape in READST
    sta $90
    rts

.org $e600
reset:
    sei
    cld
    ldx #$ff
    txs
    lda #$2f
    sta $00
    lda #$37
    sta $01
    jsr ram_init
    jsr restore_vectors
    jsr io_init
    jsr screen_init
    cli
    jmp basic_cold          ; cold entry vector of the ROM shell at $A000

ram_init:
    jsr terminal_state_init
    lda #$00
    sta $90                 ; serial status
    sta $99                 ; input logical device (keyboard)
    sta $9a                 ; output logical device (screen)
    sta $a0
    sta $a1
    sta $a2                 ; 24-bit jiffy clock
    sta $b7                 ; filename length
    sta $b8                 ; logical file number
    sta $b9                 ; secondary address
    sta $ba                 ; device number
    sta $c6                 ; keyboard queue count
    sta $d3                 ; cursor column
    sta $d6                 ; cursor row
    sta $033c               ; input line ready flag
    sta $033d               ; input line offset
    sta $033e               ; input line length
    sta $033f               ; reverse-video flag
    sta $0340               ; open-file count
    lda #$03
    sta $9a                 ; default output device is the screen
    lda #$00
    sta $0281               ; MEMBOT reports $0800; BASIC starts at $0801
    lda #$08
    sta $0282
    lda #$00
    sta $0283               ; top of BASIC RAM = $A000
    lda #$a0
    sta $0284
    lda #$0a
    sta $0289               ; keyboard queue maximum
    lda #$0e
    sta $0286               ; light blue character color
    rts

io_init:
    lda #$7f
    sta $dc0d               ; acknowledge and disable CIA 1 interrupts
    sta $dd0d               ; acknowledge and disable CIA 2 interrupts
    lda #$ff
    sta $dc02               ; keyboard column outputs
    lda #$00
    sta $dc03               ; keyboard row inputs
    lda #$3f
    sta $dd02               ; VIC bank and IEC output pins
    lda #$07
    sta $dd00               ; VIC bank 0, IEC released, RS-232 TXD idle high
    sta $0349               ; shadow for inverted IEC output pins
    lda #$25
    sta $d418               ; SID volume 5 (other voices quiet)
    lda #$00
    sta $d418
    sta $034d               ; pending CIOUT byte flag
    lda #$25
    sta $dc04               ; PAL CPU clock / 60 jiffies per second
    lda #$40
    sta $dc05
    lda #$81
    sta $dc0d               ; enable timer A IRQ
    lda #$11
    sta $dc0e               ; start timer A, load latch
    jmp private_fda3_body   ; public IOINIT and private $FDA3 share their return ABI

screen_init:
    lda #$1b
    sta $d011
    lda #$08
    sta $d016
    lda #$15
    sta $d018               ; screen $0400, character set 0
    lda #$0e               ; light-blue border around the blue text area
    sta $d020
    lda #$06
    sta $d021
    lda #$00
    tax
    sta $d01a               ; screen initialization masks VIC interrupt sources
    sta $d015               ; reset/return from a game disables all sprites
    sta $d3
    sta $d6
    lda #$20
screen_clear_loop:
    sta $0400,x
    sta $0500,x
    sta $0600,x
    sta $0700,x
    inx
    bne screen_clear_loop
    lda $0286
screen_color_loop:
    sta $d800,x
    sta $d900,x
    sta $da00,x
    sta $db00,x
    inx
    bne screen_color_loop
    clc
    rts

restore_vectors:
    ldx #$1f
    lda default_vectors+31
    sta $0333
    dex
restore_loop:
    lda default_vectors,x
    sta $0314,x
    dex
    bpl restore_loop
    ldx #$30
    ldy #$ff
    clc
    rts

vector:
    stx $fb
    sty $fc
    ldy #$1f
    bcs vector_read
vector_write_loop:
    lda ($fb),y
    sta $0314,y
    dey
    bpl vector_write_loop
    clc
    rts
vector_read:
    lda $0314,y
    sta ($fb),y
    dey
    bpl vector_read
    clc
    rts

mem_top:
    bcs mem_top_read
    stx $0283
    sty $0284
mem_top_read:
    ldx $0283
    ldy $0284
    rts

mem_bottom:
    bcs mem_bottom_read
    stx $0281
    sty $0282
mem_bottom_read:
    ldx $0281
    ldy $0282
    rts

set_message:
    sta $9d
    ; Balanced stack work preserves the reference call's 27 CPU-cycle budget.
    pha
    pla
    lda #$00
    rts

set_timeout:
    sta $0285
    rts

set_name:
    sta $b7
    stx $bb
    sty $bc
    rts

set_lfs:
    sta $b8
    stx $ba
    sty $b9
    rts

read_status:
    jmp rs232_read_status

set_time:
    sta $a2
    stx $a1
    sty $a0
    rts

read_time:
    lda $a2
    ldx $a1
    ldy $a0
    rts

update_time:
    inc $a2
    bne time_done
    inc $a1
    bne time_done
    inc $a0
time_done:
    rts

screen_size:
    ldx #$28
    ldy #$19
    rts

io_base:
    ldx #$00
    ldy #$dc
    rts

plot:
    bcs plot_read
    stx $d6
    sty $d3
    clc
    rts
plot_read:
    ldx $d6
    ldy $d3
    rts

; CHROUT preserves the character while computing its screen position.
character_out:
    pha
    lda $9a
    beq character_screen
    cmp #$03
    beq character_screen
    cmp #$02
    beq character_rs232
    cmp #$08
    bcc character_bad_device
    pla
    stx $0367              ; BASIC's string printer keeps its index in X/Y
    sty $0368
    jsr iec_output_byte
    php
    ldx $0367
    ldy $0368
    plp
    rts
character_screen:
    pla
    cmp #$0d
    beq screen_newline
    cmp #$93
    beq screen_clear_command
    cmp #$13
    beq screen_home
    cmp #$11
    beq screen_down
    cmp #$91
    beq screen_up
    cmp #$1d
    beq screen_right
    cmp #$9d
    beq screen_left
    cmp #$12
    beq reverse_on
    cmp #$92
    beq reverse_off
    cmp #$14
    beq backspace
    cmp #$20
    bcc screen_ignored
    jsr petscii_to_screen
    tya
    pha                     ; preserve caller Y while indexing screen/color RAM
    ldy $d6
    jsr screen_row_address
    ldy $d3
    lda $0341
    ora $033f
    sta ($fb),y
    lda $fc
    eor #$dc              ; screen pages $04-$07 map to color pages $D8-$DB even in decimal mode
    clv                   ; same overflow result and cycle count as the old page addition
    sta $fc
    lda $0286
    sta ($fb),y
    inc $d3
    pla
    tay
    lda $d3
    cmp #$28
    bcc screen_ignored
    jmp screen_newline
character_rs232:
    pla
    jmp rs232_transmit
character_bad_device:
    pla
    lda #$05
    sec
    rts
screen_clear_command:
    jmp screen_init
screen_home:
    lda #$00
    sta $d3
    sta $d6
screen_ignored:
    clc
    rts
screen_newline:
    lda #$00
    sta $d3
    inc $d6
    lda $d6
    cmp #$19
    bcc screen_ignored
    jsr screen_scroll
    lda #$18
    sta $d6
    clc
    rts
screen_down:
    inc $d6
    lda $d6
    cmp #$19
    bcc screen_ignored
    lda #$18
    sta $d6
    rts
screen_up:
    lda $d6
    beq screen_ignored
    dec $d6
    rts
screen_right:
    inc $d3
    lda $d3
    cmp #$28
    bcc screen_ignored
    jmp screen_newline
screen_left:
    lda $d3
    beq screen_ignored
    dec $d3
    rts
reverse_on:
    lda #$80
    sta $033f
    rts
reverse_off:
    lda #$00
    sta $033f
    rts
backspace:
    lda $d3
    beq screen_ignored
    dec $d3
    lda #$20
    jsr character_out
    dec $d3
    clc
    rts

; A printable PETSCII byte is converted to one screen code.
petscii_to_screen:
    cmp #$40
    bcc screen_code_ready
    cmp #$60
    bcc code_subtract_40
    cmp #$80
    bcc code_subtract_20
    cmp #$a0
    bcc code_subtract_80
    cmp #$c0
    bcc code_subtract_40
    cmp #$e0
    bcc code_subtract_80
code_subtract_20:
    eor #$20               ; both PETSCII ranges map by toggling bit 5
    bne screen_code_ready   ; mapped result cannot be zero
code_subtract_40:
    sec
    sbc #$40
    bcs screen_code_ready   ; both input ranges are at least $40
code_subtract_80:
    eor #$80               ; both PETSCII ranges map by toggling bit 7
screen_code_ready:
    sta $0341
    rts

screen_scroll:
    ldx #$00
scroll_page_0:
    lda $0428,x
    sta $0400,x
    inx
    bne scroll_page_0
scroll_page_1:
    lda $0528,x
    sta $0500,x
    inx
    bne scroll_page_1
scroll_page_2:
    lda $0628,x
    sta $0600,x
    inx
    bne scroll_page_2
scroll_color_0:
    lda $d828,x
    sta $d800,x
    inx
    bne scroll_color_0
scroll_color_1:
    lda $d928,x
    sta $d900,x
    inx
    bne scroll_color_1
scroll_color_2:
    lda $da28,x
    sta $da00,x
    inx
    bne scroll_color_2
scroll_tail:
    lda $0728,x
    sta $0700,x
    lda $db28,x
    sta $db00,x
    inx
    cpx #$c0
    bne scroll_tail
    lda #$20
    ldx #$00
scroll_blank:
    sta $07c0,x
    inx
    cpx #$28
    bne scroll_blank
    lda $0286
    ldx #$00
scroll_color:
    sta $dbc0,x
    inx
    cpx #$28
    bne scroll_color
    rts

; Host input injection and SCNKEY both append PETSCII to $0277 / $C6.
get_input:
    lda $99
    beq keyboard_get
    cmp #$02
    beq rs232_poll
    cmp #$08
    bcc keyboard_empty
    jmp iec_accept_byte
keyboard_get:
    jmp keyboard_get_atomic
keyboard_empty:
    lda #$00
    clc
    rts

; For keyboard input, CHRIN collects and echoes a line, then returns it
; character by character.  The line buffer lives at $0200-$0257.
character_in:
    lda $99
    beq line_input
    cmp #$02
    beq rs232_receive
    cmp #$08
    bcc character_input_empty
    jmp iec_accept_byte
character_input_empty:
    lda #$00
    rts
line_input:
    lda $033c
    bne line_read
    lda #$00
    sta $033d
    sta $033e
line_wait:
    jsr get_input
    beq line_wait
    cmp #$14
    beq line_delete
    cmp #$0d
    beq line_finish
    ldx $033e
    cpx #$58
    bcs line_wait
    sta $0200,x
    inc $033e
    jsr character_out
    jmp line_wait
line_delete:
    lda $033e
    beq line_wait
    dec $033e
    lda #$14
    jsr character_out
    jmp line_wait
line_finish:
    ldx $033e
    sta $0200,x
    inc $033e
    jsr character_out
    lda #$01
    sta $033c
line_read:
    ldx $033d
    lda $0200,x
    inc $033d
    pha
    lda $033d
    cmp $033e
    bcc line_read_more
    lda #$00
    sta $033c
line_read_more:
    pla
    clc
    rts

; SCNKEY scans all contacts before translating a key, so either Shift key,
; Control, and the Commodore modifier are detected regardless of key order.
; The implementation and repeat state live in the former BASIC math region.
.org $e9d5
scan_keyboard:
    jmp terminal_keyboard_scan

.org $ea1f
irq_body:
    lda $dc0d
    lsr a
    bcc irq_done
    jsr update_time
    jsr scan_keyboard
irq_done:
    jmp irq_epilogue
.org $ea31
private_irq_service:
    ; Software IRQ handlers may chain through this private KERNAL address.
    jmp irq
scan_key_store:
    ldx $c6
    sta $0277,x
    inc $c6
    rts

.org $ea3e
stop_key:
    lda #$7f
    sta $dc00
    lda $dc01
    and #$80
    bne stop_clear
    lda #$00
    rts
stop_clear:
    lda #$01
    rts

clear_channels:
    lda $99
    cmp #$08
    bcc clear_output_channel
    jsr iec_untalk
clear_output_channel:
    lda $9a
    cmp #$08
    bcc clear_channel_flags
    jsr iec_unlisten
clear_channel_flags:
    lda #$00
    sta $99
    lda #$03
    sta $9a
    tax
    lda #$00
    sec
    rts

close_all:
    jsr clear_channels
    lda #$00
    sta $0340
    rts

; The open-file table occupies $0259-$0272 (ten pairs of logical/device IDs).
open_file:
    jmp open_file_impl
.org $ea7e
; A second observed private IRQ-return entry shares the restore tail.
private_irq_pre_return:
    jmp private_irq_return
private_irq_return:
    pla
    tay
    pla
    tax
    pla
    rti
open_file_impl:
    ldx $0340
    cpx #$0a
    bcs too_many_files
    lda $b8
    jsr find_file
    bcc file_already_open
open_add:
    stx $035c
    lda $ba
    cmp #$02
    beq open_rs232
    cmp #$08
    bcc open_record
    jsr serial_open_channel
    bcc open_record
open_serial_error:
    lda #$05
    sec
    rts
open_rs232:
    jsr rs232_initialize
    bcs open_serial_error
open_record:
    ldx $035c
    lda $b8
    sta $0259,x
    lda $ba
    sta $0263,x
    lda $b9
    sta $026d,x
    inc $0340
    clc
    rts
too_many_files:
    lda #$01
    sec
    rts
file_already_open:
    lda #$02
    sec
    rts

close_file:
    sta $035d
    ldy #$00
close_search:
    cpy $0340
    beq file_missing
    lda $0259,y
    cmp $035d
    beq close_found
    iny
    bne close_search
close_found:
    lda $0263,y
    sta $035e
    lda $026d,y
    sta $035f
    ldx $0340
    dex
    lda $0259,x
    sta $0259,y
    lda $0263,x
    sta $0263,y
    lda $026d,x
    sta $026d,y
    stx $0340
    lda $035e
    cmp #$08
    bcc close_success
    jsr iec_listen
    bcs close_serial_error
    lda $035f
    and #$0f
    ora #$e0
    jsr iec_second
    bcs close_serial_error
    jsr iec_unlisten
    bcs close_serial_error
close_success:
    clc
    rts
close_serial_error:
    lda #$05
    sec
    rts
file_missing:
    lda #$03
    sec
    rts

check_input:
    txa                    ; CHKIN receives the logical file in X
    jsr find_file
    bcs check_return
    sty $035c
    lda $0263,y
    sta $99
    cmp #$08
    bcc check_return
    jsr iec_talk
    bcs check_return
    ldy $035c
    lda $026d,y
    and #$0f
    ora #$60
    jsr iec_talk_secondary
check_return:
    rts

check_output:
    txa                    ; CHKOUT receives the logical file in X
    jsr find_file
    bcs check_output_return
    sty $035c
    lda $0263,y
    sta $9a
    cmp #$08
    bcc check_output_return
    jsr iec_listen
    bcs check_output_return
    ldy $035c
    lda $026d,y
    and #$0f
    ora #$60
    jsr iec_second
check_output_return:
    rts

find_file:
    ldy #$00
find_loop:
    cpy $0340
    beq find_missing
    cmp $0259,y
    beq find_found
    iny
    bne find_loop
find_missing:
    lda #$03
    sec
    rts
find_found:
    clc
    rts

; OPEN sends the filename on secondary address $F0-$FF.  A zero-length
; filename opens a command channel or a device-defined default stream.
serial_open_channel:
    lda $ba
    jsr iec_listen
    bcs serial_open_return
    lda $b9
    and #$0f
    ora #$f0
    jsr iec_second
    bcs serial_open_return
    ldy #$00
serial_open_name_loop:
    cpy $b7
    beq serial_open_end
    lda ($bb),y
    sty $035a
    jsr iec_output_byte
    bcs serial_open_return
    ldy $035a
    iny
    bne serial_open_name_loop
serial_open_end:
    jsr iec_unlisten
serial_open_return:
    rts

; Unsupported devices return an error and set the serial timeout bit.
serial_unsupported:
    lda #$80
    sta $90
    lda #$05
    sec
    rts
load_file:
    sta $0354               ; 0 = load, 1 = verify
    lda #$00
    sta $035f               ; file channel has not been opened
    sta $90
    php
    pla
    and #$04
    sta $0360               ; preserve caller's interrupt-disable flag
    sei
    stx $fb
    sty $fc
    lda $ba
    cmp #$01
    beq tape_load
    cmp #$08
    bcc load_bad_device
    jsr iec_listen
    bcs load_failed_near
    lda #$f0               ; OPEN channel 0 with the requested filename
    jsr iec_second
    bcs load_failed_near
    ldy #$00
    lda $b7
    bne load_name_loop
    lda #$2a               ; empty filename means first matching file
    jsr iec_output_byte
    jmp load_name_done
load_failed_near:
    jmp load_failed
load_name_loop:
    tya
    pha
    lda ($bb),y
    jsr iec_output_byte
    pla
    tay
    bcs load_failed_near
    iny
    cpy $b7
    bne load_name_loop
load_name_done:
    jsr iec_unlisten
    bcs load_failed_near
    inc $035f
    lda $ba
    jsr iec_talk
    bcs load_failed_near
    lda #$60
    jsr iec_talk_secondary
    bcs load_failed
    jsr iec_accept_byte
    bcs load_read_failed
    sta $0357
    jsr iec_accept_byte
    bcs load_read_failed
    sta $0358
    lda $b9
    beq load_data
    lda $0357
    sta $fb
    lda $0358
    sta $fc
load_data:
    jsr iec_accept_byte
    bcc load_data_received
    lda $90
    cmp #$42               ; retry only the EOI/no-data state, not bus timeouts
    bne load_failed
    lda #$40               ; clear transient no-data, retain pending EOI
    sta $90
    bne load_data
load_data_received:
    ldy $0354             ; zero for LOAD, one for VERIFY
    beq load_store
    ldy #$00
    cmp ($fb),y
    bne load_verify_error
    beq load_advance      ; the compare just established equality
load_store:
    sta ($fb),y
load_advance:
    inc $fb
    bne load_check_end
    inc $fc
load_check_end:
    bit $90
    bvc load_data
    jsr load_close_channel
    ldx $fb
    ldy $fc
    clc
    lda #$00
    beq restore_interrupts
load_close_channel:
    jsr iec_untalk
    lda $ba
    jsr iec_listen
    lda #$e0
    jsr iec_second
    jsr iec_unlisten
    rts
load_verify_error:
    jsr load_close_channel
    lda #$1c
    bne load_error_return
load_bad_device:
    lda #$09
    bne load_error_return
load_failed:
    jsr load_close_channel
load_read_failed:
    lda #$05
    sec
    sbc $035f             ; 0 before OPEN, 1 after OPEN: device/file error
load_error_return:
    sec
    bcs restore_interrupts

restore_interrupts:
    pha
    lda $0360
    bne restore_interrupts_done
    cli
restore_interrupts_done:
    pla
    rts
iec_unlisten_public:
    bit $034d
    txa
    pha
    tya
    pha
    jsr iec_unlisten
    pla
    tay
    pla
    tax
    lda $dd00
    clc
    cli
    rts
iec_acptr_public:
    txa
    pha
    tya
    pha
    jsr iec_accept_byte
    sta $0351             ; byte on success, bus sample on failure
    pla
    tay
    pla
    tax
    bcs iec_acptr_ready     ; failed receive retains the caller's V flag
    bit $90               ; EOI status becomes the visible V flag
iec_acptr_ready:
    lda $0351
    clc
    cli
    rts
.org $ece0
save_file:
    sta $0362
    php
    pla
    and #$04
    sta $0360
    sei
    stx $0355               ; exclusive end address
    sty $0356
    ldx $0362              ; A points to start-address pointer in zero page
    lda $0000,x
    sta $fb
    inx
    lda $0000,x
    sta $fc
    lda #$00
    sta $90
    lda $ba
    cmp #$01
    bne save_not_tape
    jmp tape_save
.org $ed0c
private_listen:
    jmp private_listen_body
save_not_tape:
    cmp #$08
    bcc save_bad_device
    lda $b7
    beq save_no_filename
    lda $ba
    jsr iec_listen
    bcs save_failed
    lda #$f1
    jsr iec_second
    bcs save_failed
    ldy #$00
save_name_loop:
    lda ($bb),y
    sty $035a
    jsr iec_output_byte
    bcs save_failed
    ldy $035a
    iny
    cpy $b7
    bne save_name_loop
    jsr save_reopen_listener
    bcs save_failed
    lda #$61               ; channel 1 carries the PRG header and data
    jsr iec_second
    bcs save_failed
    lda $fb
    jsr iec_output_byte
    bcs save_failed
    lda $fc
    jsr iec_output_byte
    bcs save_failed
save_data_loop:
    lda $fb
    cmp $0355
    bne save_data_byte
    lda $fc
    cmp $0356
    beq save_data_done
save_data_byte:
    ldy #$00
    lda ($fb),y
    jsr iec_output_byte
    bcs save_failed
    inc $fb
    bne save_data_loop
    inc $fc
    jmp save_data_loop
save_failed:
    lda #$05
    sec
    jmp restore_interrupts
save_data_done:
    jsr save_reopen_listener
    bcs save_failed
    lda #$e1
    jsr iec_second
    bcs save_failed
    jsr iec_unlisten
    bcs save_failed
    lda #$00
    ; The preceding BCS guarantees carry clear on this success path.
    jmp restore_interrupts
save_reopen_listener:
    jsr iec_unlisten
    bcs save_failed
    lda $ba
    jmp iec_listen
private_listen_body:
    sta $034c              ; preserve command A while saving X/Y
    txa
    pha
    tya
    pha
    lda $034c
    jsr iec_listen
    bcs private_iec_restore
    asl a
    jmp private_iec_restore
.org $edb9
private_second:
    sta $034c
    txa
    pha
    tya
    pha
    lda $034c
    jsr iec_second
private_iec_restore:
    sta $034c
    pla
    tay
    pla
    tax
    lda $034c
    clv                    ; observed private IEC return clears overflow
    clc                    ; private entry reports serial error in $90
    cli                    ; observed private IEC return enables IRQ
    rts
save_no_filename:
    lda #$08
    jmp save_error

; Preserve the IRQ/default-vector layout after the SAVE compaction.
.org $edd9
; Preserve the IRQ entry while giving the private IEC output its own entry.
irq:
    jmp irq_body
.org $eddd
private_output_byte:
    jmp iec_output_byte
.org $ede0
iec_output_first_flags:
    clc
    lda #$80
    php
    lda $95
    plp
    rts
.org $ede8
irq_dispatch:
    pha
    txa
    pha
    tya
    pha
    tsx
    lda $0104,x
    and #$10
    beq irq_dispatch_irq
    jmp ($0316)
irq_dispatch_irq:
    jmp ($0314)

default_vectors:
    .word private_irq_service,break_handler,nmi,open_file,close_file,check_input,check_output,clear_channels
    .word character_in,terminal_character_out,stop_key,get_input,close_all,serial_unsupported,load_file,save_file

screen_row_low:
screen_row_address:
    ; $0400 + 40*row = $0400 + 8*(5*row), rows 0..24. The arithmetic
    ; must remain binary even if the caller entered CHROUT with D set.
    php
    cld
    tya
    asl a
    asl a
    ; 4*row <= 96, so the preceding shift has already cleared carry.
    adc $d6
    pha
    lsr a
    lsr a
    lsr a
    lsr a
    lsr a
    ora #$04
    sta $fc
    pla
    asl a
    asl a
    asl a
    sta $fb
    plp
    rts
.org $ee41
irq_epilogue:
    pla
    tay
    pla
    tax
    pla
nmi:
    rti
nmi_dispatch:
    jmp ($0318)
break_handler:
    jmp reset

; Retain the cold-entry address while replacing the BASIC interpreter with
; the shell. The first two words in the $A000 ROM are cold and warm vectors.
.org $ee8d
basic_cold:
    jmp ($a000)

basic_line_char:
    lda $99
    bne basic_line_device
    txa
    pha
    tya
    pha
basic_line_wait:
    jsr get_input
    beq basic_line_wait
    sta $0347
    jsr character_out
    pla
    tay
    pla
    tax
    lda $0347
    rts
basic_line_device:
    jmp character_in

; These addresses are part of the BASIC ROM's documented interface.
.org $e10c
    jmp terminal_character_out
.org $e112
    jmp basic_line_char
.org $e118
    jmp ($0320)            ; BASIC CHKOUT helper
.org $e11e
    jmp ($031e)            ; BASIC CHKIN helper
.org $e124
    jmp ($032a)            ; BASIC GETIN helper
.org $e12a
    jmp basic_warm
.org $e156
    jmp basic_warm
.org $e165
    jmp basic_warm
.org $e168
    jmp basic_warm
.org $e1be
    jmp basic_warm
.org $e1c7
    jmp basic_warm
.org $e1d4
    jmp basic_warm
.org $e37b
    jmp basic_warm
.org $e386
    jmp basic_warm
.org $e38b
    jmp basic_error
.org $e394
    jmp basic_cold
.org $e544
    jmp screen_init         ; common private screen-clear entry

.org $fd00
basic_warm:
    jsr clear_channels
    jmp ($a002)
basic_error:
    jmp basic_warm
.org $fd15
    jmp restore_vectors     ; common private vector-restore entry

; IEC bus output.  CIA 2 PA3/4/5 are inverted: a set output bit pulls the
; corresponding ATN/CLOCK/DATA wire low.  PA6/7 sample CLOCK/DATA input.
.org $f200
iec_atn_low:
    lda $0349
    ora #$08
    sta $0349
    sta $dd00
    rts
iec_atn_high:
    lda $0349
    and #$f7
    sta $0349
    sta $dd00
    rts
iec_clock_low:
    lda $0349
    ora #$10
    and #$df             ; release DATA on the falling CLOCK edge
    sta $0349
    sta $dd00
    rts
iec_clock_high:
    lda $0349
    and #$ef
    sta $0349
    sta $dd00
    rts
iec_data_low:
    lda $0349
    ora #$20
    sta $0349
    sta $dd00
    rts
iec_data_high:
    lda $0349
    and #$df
    sta $0349
    sta $dd00
    rts

iec_delay:
    ldy #$04
iec_delay_loop:
    dey
    bne iec_delay_loop
    rts

iec_wait_data_low:
    ldy #$60
iec_wait_data_low_loop:
    lda $dd00
    bpl iec_wait_ready
    dey
    bne iec_wait_data_low_loop
    beq iec_timeout
iec_wait_data_high:
iec_wait_data_high_loop:
    lda $dd00
    bmi iec_wait_ready
    nop
    jmp iec_wait_data_high_bounded ; busy listeners get a finite grace period

iec_wait_clock_low:
    ldx #$30
iec_wait_clock_low_outer:
    ldy #$ff
iec_wait_clock_low_loop:
    lda $dd00
    and #$40
    beq iec_wait_ready
    dey
    bne iec_wait_clock_low_loop
    dex
    bne iec_wait_clock_low_outer
    beq iec_timeout

iec_wait_clock_high:
    ldx #$30
iec_wait_clock_high_outer:
    ldy #$ff
iec_wait_clock_high_loop:
    lda $dd00
    asl a
    bmi iec_wait_ready
    dey
    bne iec_wait_clock_high_loop
    ; Preserve the fast bit-sampling loop; only a slow device takes the
    ; extended, bounded sector-fetch path.
    jmp iec_wait_clock_high_bounded
iec_timeout:
    lda $90
    ora #$80
    sta $90
    clv
    lda $dd00              ; return sampled bus state on a failed handshake
    sec
    rts
iec_wait_ready:
    clc
    rts

iec_send_byte:
    php
    sei                     ; keep IEC bit timing independent of the CIA IRQ
    sta $034c
    jsr iec_clock_low
    ldy #$b1
iec_send_initial_hold:
    dey
    bne iec_send_initial_hold
    jsr iec_clock_high
    jsr iec_wait_data_high
    bcs iec_send_return
    lda $034e               ; signal EOI only for the final buffered byte
    beq iec_send_start
iec_send_wait_eoi_low:
    jsr iec_wait_eoi_low_bounded
    bcs iec_send_return
    nop                    ; retain the following fixed IEC routine addresses
    nop
    jsr iec_wait_data_high
    bcs iec_send_return
iec_send_start:
    jsr iec_clock_low
    jsr iec_delay
    ldy #$08
iec_send_bit:
    lsr $034c
    bcs iec_send_one
    jsr iec_data_low
    bcc iec_send_pulse
iec_send_one:
    jsr iec_data_high
iec_send_pulse:
    jsr iec_clock_high
    jsr iec_clock_low
    dey
    bne iec_send_bit
    jsr iec_wait_data_low
    bcc iec_send_return
    lda #$03               ; transmitted byte lacked the receiver ACK
    sta $90
    jsr iec_release_all
    lda $dd00
iec_send_return:
    bcs iec_send_error
    plp
    clc
    rts
iec_send_error:
    plp
    sec
    rts

iec_command_start:
    lsr $034e
    jsr iec_clock_high
    jsr iec_data_high
    jsr iec_atn_low
    jsr iec_wait_data_low
    bcc iec_command_ready
    jsr iec_atn_high
    ror $034f              ; carry from failed wait marks this command only
    sec
    lda $dd00
iec_command_ready:
    rts

iec_listen:
    ora #$20
iec_address:
    sta $034f
    jsr iec_command_start
    bcs iec_listen_return
    lda $034f
    jsr iec_send_byte
iec_listen_return:
    rts

iec_talk:
    ora #$40
    bne iec_address

iec_second:
    bit $034f              ; current command failed, not stale serial status
    bpl iec_second_send
    jmp iec_wait_data_low   ; complete the failed-handshake wait
iec_second_send:
    jsr iec_send_byte
    bcs iec_talk_secondary_return
    jsr iec_atn_high
    jmp iec_delay

iec_talk_secondary:
    jsr iec_send_byte
    bcs iec_talk_secondary_return
    jsr iec_atn_high
    jsr iec_clock_high
    jsr iec_data_low      ; C64 becomes listener
    jsr iec_wait_clock_low
    bcs iec_talk_secondary_return
    jsr iec_wait_clock_high
iec_talk_secondary_return:
    rts

iec_accept_byte:
    php
    sei                     ; keep EOI and bit sampling in one IRQ-free window
    lda #$00
    sta $0351
    lda #$01
    sta $0352               ; shift sentinel counts eight received bits
    jsr iec_wait_clock_high ; release hold-off only after inter-byte clock rises
    bcs iec_read_return
    jsr iec_data_high
    ldy #$28
iec_wait_byte_or_eoi:
    bit $dd00
    nop                    ; retain the original receive poll's cycle count
    bvc iec_read_start
    dey
    bne iec_wait_byte_or_eoi
    lda $90
    ora #$40
    sta $90                 ; EOI on this byte
    jsr iec_data_low
    jsr iec_delay
    jsr iec_data_high
    ldy #$1e               ; EOI response has a bounded next-byte window
iec_eoi_wait_clock_low:
    bit $dd00
    bvc iec_read_start
    dey
    bne iec_eoi_wait_clock_low
    lda $90
    ora #$02               ; no data byte followed the EOI handshake
    sta $90
    lda $dd00
    sec
    bcs iec_read_return
iec_read_start:
iec_read_bit:
    jsr iec_wait_clock_high
    bcs iec_read_return
    lda $dd00
    asl a                   ; incoming DATA bit becomes the 6502 carry
    ror $0351               ; LSB-first wire byte accumulates high to low
iec_read_zero:
    jsr iec_wait_clock_low
    bcs iec_read_return
    asl $0352
    bne iec_read_bit
    jsr iec_data_low
    jsr iec_delay
    jsr iec_delay
    bit $90
    bvc iec_ack_held        ; hold DATA until the next ACPTR between bytes
    jsr iec_data_high       ; release DATA after the final EOI byte
iec_ack_held:
    clc
iec_read_return:
    bcs iec_read_error
    plp
    lda $0351
    clc
    rts
iec_read_error:
    plp
    sec
    rts

iec_output_byte:
    pha
    sec
    lda $94
    beq iec_output_store
    clc
    bit $034f
    bmi iec_output_store
    lda #$00
    sta $034e
    tya
    pha
    lda $95
    jsr iec_send_byte
    pla
    tay
    bcs iec_output_error
iec_output_store:
    lda #$80
    sta $94
    lda #$40
    sta $034d
    pla
    sta $95
    clv
    bcc iec_output_second_return
    jmp iec_output_first_flags
iec_output_second_return:
    rts
iec_output_error:
    pla
    rts

iec_unlisten:
    lda $94
    beq iec_unlisten_command_start
    lda #$01
    sta $034e
    lda $95
    jsr iec_send_byte
    bcc iec_unlisten_sent
iec_unlisten_command_start:
    jsr iec_command_start
    bcs iec_untalk_return
iec_unlisten_sent:
    lda #$00
    sta $94
    sta $034d
    sta $034e
iec_unlisten_command:
    lda #$3f
    bne iec_end_command
iec_untalk:
    jsr iec_data_high
    lda #$5f
iec_end_command:
    pha
    jsr iec_atn_low
    jsr iec_delay
    pla
    jsr iec_send_byte
    bcs iec_untalk_return
iec_release_all:
    jsr iec_atn_high
    jsr iec_clock_high
    jsr iec_data_high
iec_untalk_return:
    rts
save_bad_device:
    lda #$09
save_error:
    sec
    jmp restore_interrupts

; Synchronous PAL RS-232 rates 50-2400 baud and custom timer mode.
; Control bytes select word length and stop bits; command bytes select parity.
; Background buffering and handshaking remain to be added.
.org $f438
rs232_initialize:
    lda $b7
    bne rs232_name
    lda #$06               ; default when no control byte was supplied
    sta $0293
    ldx #$06
    jmp rs232_code_found
rs232_name:
    ldy #$00
    lda ($bb),y
    sta $0293
    and #$0f
    tax
    cpx #$0b
    bcs rs232_unsupported
rs232_code_found:
    lda $b7
    cmp #$02
    bcc rs232_default_command
    iny
    lda ($bb),y
    sta $0294
    and #$1f               ; no synchronous handshake or half duplex yet
    bne rs232_unsupported
    jmp rs232_configure
rs232_default_command:
    lda #$00
    sta $0294
rs232_configure:
    cpx #$00
    bne rs232_standard_rate
    lda $b7
    cmp #$04
    bcc rs232_unsupported
    jmp rs232_custom_rate
rs232_standard_rate:
    lda rs232_full_low,x
    sta $0299
    lda rs232_full_high,x
    sta $029a
    lda rs232_half_low,x
    sta $029b
    lda rs232_half_high,x
    sta $029c
rs232_configured:
    lda $dd02
    ora #$04
    sta $dd02
    jsr rs232_tx_high
    clc
    rts
rs232_unsupported:
    lda #$05
    sec
    rts

rs232_tx_low:
    lda $0349
    and #$fb
    sta $0349
    sta $dd00
    rts
rs232_tx_high:
    lda $0349
    ora #$04
    sta $0349
    sta $dd00
    rts

rs232_transmit:
    pha
    lda $0293
    and #$e0
    bne rs232_transmit_custom
    lda $0294
    and #$e0
    bne rs232_transmit_custom
    pla
    sta $034a
    pha
    txa
    pha
    jsr rs232_tx_low       ; start bit
    jsr rs232_delay_full
    ldx #$08
rs232_transmit_bit:
    lsr $034a
    bcc rs232_transmit_zero
    jsr rs232_tx_high
    jmp rs232_transmit_wait
rs232_transmit_zero:
    jsr rs232_tx_low
rs232_transmit_wait:
    jsr rs232_delay_full
    dex
    bne rs232_transmit_bit
    jsr rs232_tx_high      ; stop bit and idle
    jsr rs232_delay_full
    jmp rs232_extended_tx_done
rs232_transmit_custom:
    pla
    jmp rs232_transmit_extended

rs232_poll:
    lda $dd01
    and #$01
    beq rs232_receive_start
    jmp rs232_poll_empty
rs232_receive:
    lda $dd01
    and #$01
    bne rs232_receive
rs232_receive_start:
    lda $0293
    and #$e0
    bne rs232_receive_custom
    lda $0294
    and #$e0
    bne rs232_receive_custom
    txa
    pha
    jsr rs232_delay_full
    jsr rs232_delay_half
    lda #$00
    sta $034a
    ldx #$08
rs232_receive_bit:
    lda $dd01
    lsr A                  ; PB0 into carry, least-significant bit first
    ror $034a
    jsr rs232_delay_receive
    dex
    bne rs232_receive_bit
    lda $dd01              ; stop bit must be high
    and #$01
    beq rs232_frame_error
    jmp rs232_extended_rx_done
rs232_receive_custom:
    jmp rs232_receive_extended
rs232_frame_error:
    ; The standard and extended receive paths have the same saved-X frame
    ; and report the same RSSTAT framing error and return registers.
    jmp rs232_extended_rx_frame_error

; PAL bit periods are 985248 Hz / baud. Timer latches compensate for the
; ROM instructions between consecutive PA2 transitions (about 100 cycles).
rs232_delay_full:
    sec
    lda $0299
    sbc #$64
    jmp rs232_delay_sbc_high
rs232_delay_half:
    lda $029b
    sta $dd04
    lda $029c
    sta $dd05
    jmp rs232_delay_start
rs232_delay_receive:
    sec
    lda $0299
    sbc #$3c               ; RX loop has less work than TX
rs232_delay_sbc_high:
    sta $dd04
    lda $029a
    sbc #$00
    sta $dd05
rs232_delay_start:
    lda $dd0d              ; discard a previous timer-A underflow
    lda #$19               ; force-load and start one-shot timer A
    sta $dd0e
rs232_delay_poll:
    lda $dd0d
    and #$01
    beq rs232_delay_poll
    rts
; These are source-level private-entry and data layouts. Public ROM anchors
; remain fixed; no RAM patch or emulator path is involved.
.org $e504
rs232_full_low:
    .byte $00,$fa,$50,$fc,$98,$a8,$d4,$6a,$36,$24,$9a
private_fda3_body:
    lda #$e7
    sta $01
    lda #$d7
    ldx #$ff
    rts

.org $e518
private_screen_clear:
    jmp private_screen_clear_body

; Full-period values are exposed at $0299/$029A like the reference KERNAL.
; Half-period latches still compensate for the ROM's 100-cycle work.
.org $e524
rs232_full_high:
    .byte $00,$4c,$33,$22,$1c,$19,$0c,$06,$03,$02,$01
rs232_half_low:
    .byte $00,$18,$44,$1a,$eb,$70,$06,$d1,$36,$ae,$69

.org $e5a0
private_screen_clear_end:
    sec
    rts

.org $f0f2
rs232_half_high:
    .byte $00,$26,$19,$11,$0d,$0c,$06,$02,$01,$00,$00

; Control code zero uses the third and fourth filename bytes as the half-bit
; timer value. The calculation is split across two free ROM gaps.
.org $f5a0
rs232_custom_rate:
    iny
    lda ($bb),y
    sta $029b
    iny
    lda ($bb),y
    sta $029c
    jmp rs232_custom_calculate

.org $e12d
rs232_custom_calculate:
    lda $029b
    asl a
    sta $0299
    lda $029c
    rol a
    sta $029a
    clc
    lda $0299
    adc #$c8
    sta $0299
    lda $029a
    adc #$00
    sta $029a
    jmp rs232_configured

; Device 1 standard CBM tape block reader.  CIA1 timer B measures the
; interval between FLAG edges; $0364-$0375 hold the in-flight header state.
.org $f5c0
tape_load:
    lda $b7
    cmp #$11
    bcs tape_load_error
    jsr tape_motor_start
tape_find_header:
    lda #$00
    sta $0370               ; first header copy has not been retried
    jsr tape_seek_sync
    bcs tape_load_error
tape_parse_header:
    lda #$00
    sta $0369               ; header XOR checksum
    sta $0373               ; filename mismatch flag
    ldx #$00
tape_header_byte:
    jsr tape_read_byte
    bcs tape_header_retry
    sta $0372
    eor $0369
    sta $0369
    cpx #$05
    bcs tape_header_name
    lda $0372
    sta $0364,x             ; type, start and exclusive end
    jmp tape_header_next
tape_header_name:
    cpx #$15
    bcs tape_header_next
    txa
    sec
    sbc #$05
    tay
    cpy $b7
    bcs tape_header_next
    lda ($bb),y
    cmp $0372
    beq tape_header_next
    lda #$01
    sta $0373
tape_header_next:
    inx
    cpx #$c0
    bne tape_header_byte
    jsr tape_read_byte      ; checksum after 192-byte header
    bcs tape_header_retry
    cmp $0369
    bne tape_header_retry
    lda $0364
    cmp #$01               ; relocatable BASIC program
    beq tape_header_type_ok
    cmp #$03               ; absolute machine-code program
    bne tape_find_header
tape_header_type_ok:
    lda $0373
    bne tape_find_header
    lda $0367
    sec
    sbc $0365
    sta $0374               ; byte count low
    lda $0368
    sbc $0366
    sta $0375               ; byte count high
    lda $b9
    beq tape_load_relocate
    lda $0365
    sta $fb
    lda $0366
    sta $fc
tape_load_relocate:
    clc
    lda $fb
    adc $0374
    sta $0367               ; destination end low
    lda $fc
    adc $0375
    sta $0368               ; destination end high
    lda $fb
    sta $0374               ; destination start for a second data copy
    lda $fc
    sta $0375
    lda #$00
    sta $0373               ; first data copy
    lda $0370
    bne tape_data_sync      ; the duplicate header was already read
    lda #$09
    jsr tape_seek_sync_value
    bcs tape_load_error
    ldx #$c1               ; repeated 192-byte header plus XOR byte
tape_skip_repeated_header:
    jsr tape_read_byte
    bcs tape_load_error
    dex
    bne tape_skip_repeated_header
tape_data_sync:
    jsr tape_seek_sync
    bcs tape_load_error
    lda #$00
    sta $0369
tape_load_data:
    lda $fb
    cmp $0367
    bne tape_load_data_byte
    lda $fc
    cmp $0368
    beq tape_load_checksum
tape_load_data_byte:
    jsr tape_read_byte
    bcs tape_retry_read
    sta $0372
    eor $0369
    sta $0369
    ldy #$00
    lda $0354
    beq tape_load_store
    lda ($fb),y
    cmp $0372
    bne tape_load_verify_error
    jmp tape_load_advance
tape_load_store:
    lda $0372
    sta ($fb),y
tape_load_advance:
    inc $fb
    bne tape_load_data
    inc $fc
    jmp tape_load_data
tape_load_checksum:
    jsr tape_read_byte
    bcs tape_retry_read
    cmp $0369
    bne tape_retry_read
    jsr tape_motor_stop
    ldx $fb
    ldy $fc
    lda #$00
    clc
    jsr restore_interrupts
    rts
tape_load_verify_error:
    jmp tape_retry_verify
tape_load_error:
    jsr tape_motor_stop
    lda #$04
    sec
    jsr restore_interrupts
    rts

tape_motor_start:
    lda #$00
    sta $037e              ; no byte phase has been synchronized yet
    lda #$ff
    sta $dc06
    sta $dc07
    sta $0379              ; previous free-running timer value
    sta $037a
    lda $dc0d              ; clear stale FLAG/timer status
    lda #$11               ; continuous CIA1 timer B
    sta $dc0f
    lda $01
    and #$df               ; 6510 cassette motor on
    sta $01
    rts
tape_motor_stop:
    lda #$00
    sta $dc0f
    lda $01
    ora #$20
    sta $01
    rts

; Search for the first-copy countdown $89..$81.  A byte reader searches
; for a long/medium marker, so short-pulse leaders and gaps are skipped.
tape_seek_sync:
    lda #$89
tape_seek_sync_value:
    sta $036f
    sec
    sbc #$09
    sta $0376
    lda #$00
    sta $037e
tape_sync_next:
    jsr tape_read_byte
    bcs tape_sync_fail
    cmp $036f
    bne tape_sync_restart
    dec $036f
    lda $036f
    cmp $0376
    beq tape_sync_done
    jmp tape_sync_next
tape_sync_restart:
    lda $0376
    clc
    adc #$09
    sta $036f
    jmp tape_sync_next
tape_sync_done:
    clc
    rts
tape_sync_fail:
    sec
    rts

tape_read_byte:
    lda $037e
    beq tape_byte_marker
    jsr tape_pulse          ; stay phase-locked to consecutive byte markers
    bcs tape_byte_fail
    jsr tape_pulse
    bcs tape_byte_fail
    jmp tape_byte_start
tape_byte_marker:
    jsr tape_pulse
    bcs tape_byte_fail
    cmp #$03
    bne tape_byte_marker
    jsr tape_pulse
    bcs tape_byte_fail
    cmp #$02
    bne tape_byte_marker
tape_byte_start:
    lda #$00
    sta $036d
    sta $036e
    sta $0381              ; ambiguous bit flag
    sta $0383              ; ambiguous bit count
    lda #$08
    sta $036c
tape_byte_bit:
    jsr tape_read_bit
    bcs tape_byte_fail
    sta $0372
    lda $0381
    beq tape_byte_bit_certain
    inc $0383
    ldy $036c
    lda tape_bit_masks,y
    sta $0382
    lda #$00
    sta $0381
tape_byte_bit_certain:
    lda $0372
    eor $036e
    sta $036e
    lda $0372
    lsr A
    ror $036d
    dec $036c
    bne tape_byte_bit
    jsr tape_read_bit      ; odd parity
    bcs tape_byte_fail
    sta $0372
    lda $036e
    eor #$01
    cmp $0372
    beq tape_byte_parity_ok
    lda $0383
    cmp #$01               ; repair one timing-ambiguous bit by odd parity
    bne tape_byte_fail
    lda $036d
    eor $0382
    sta $036d
tape_byte_parity_ok:
    lda #$01
    sta $037e
    lda $036d
    clc
    rts
tape_byte_fail:
    lda #$00
    sta $037e
    sec
    rts

tape_read_bit:
    jsr tape_pulse
    bcs tape_bit_fail
    lda $0377
    sta $037b
    lda $0378
    sta $037c
    jsr tape_pulse
    bcs tape_bit_fail
    lda $037b
    cmp $0377
    bcc tape_bit_zero
    bne tape_bit_one
    lda $037c
    sec
    sbc $0378
    bcc tape_bit_small_negative
    cmp #$20
    bcc tape_bit_ambiguous
    jmp tape_bit_one
tape_bit_small_negative:
    cmp #$e0
    bcs tape_bit_ambiguous
    jmp tape_bit_zero
tape_bit_ambiguous:
    lda #$01
    sta $0381              ; timing difference below 32 cycles
    jmp tape_bit_zero
tape_bit_one:
    lda #$01
    clc
    rts
tape_bit_zero:
    lda #$00
    clc
    rts
tape_bit_fail:
    sec
    rts

; A=1 short, 2 medium, 3 long; carry set after about 6.5s without edges.
tape_pulse:
    lda #$00
    sta $0371
tape_pulse_wait:
    lda $dc0d
    and #$12               ; FLAG or timer B underflow
    beq tape_pulse_wait
    and #$10
    bne tape_pulse_classify
    inc $0371
    lda $0371
    cmp #$64
    bcc tape_pulse_wait
    jsr tape_mark_end
    sec
    rts
tape_pulse_classify:
    lda $dc07
    sta $0380
    lda $dc06
    sta $037f
    lda $dc07              ; retry if low-byte wrap changed the high byte
    cmp $0380
    beq tape_pulse_sampled
    sta $0380
    lda $dc06
    sta $037f
tape_pulse_sampled:
    lda $0379
    sec
    sbc $037f
    sta $0378              ; elapsed cycles low
    lda $037a
    sbc $0380
    sta $0377
    lda $037f
    sta $0379
    lda $0380
    sta $037a
    lda $0377
    beq tape_pulse_short
    cmp #$01
    beq tape_pulse_one_page
    cmp #$02
    beq tape_pulse_two_pages
    lda #$03
    clc
    rts
tape_pulse_one_page:
    lda $0378
    cmp #$c8               ; short/medium boundary: 456 cycles
    bcc tape_pulse_short
    lda #$02
    clc
    rts
tape_pulse_two_pages:
    lda $0378
    cmp #$60               ; medium/long boundary: 608 cycles
    bcs tape_pulse_long
    lda #$02
    clc
    rts
tape_pulse_short:
    lda #$01
    clc
    rts
tape_pulse_long:
    lda #$03
    clc
    rts

tape_bit_masks:
    .byte $00,$80,$40,$20,$10,$08,$04,$02,$01

; A bad first data copy can be replaced by the second standard tape copy.
; The header's byte count is no longer needed after the destination end is
; calculated, so $0374/$0375 retain the start for a retry.
.org $e16b
tape_retry_read:
    lda #$04
    bne tape_retry_set_error
tape_retry_verify:
    lda #$1c
tape_retry_set_error:
    sta $0370
    lda $0373
    bne tape_retry_fail
    inc $0373
    lda #$09
    jsr tape_seek_sync_value
    bcs tape_retry_fail
    lda $0374
    sta $fb
    lda $0375
    sta $fc
    lda #$00
    sta $0369
    jmp tape_load_data
tape_retry_fail:
    lda $0370
    cmp #$1c
    beq tape_retry_verify_fail
    jmp tape_load_error
tape_retry_verify_fail:
    jsr tape_motor_stop
    lda #$1c
    sec
    jsr restore_interrupts
    rts

; A failed first header may still have a valid duplicate.  Reparse that
; header and continue directly to the first data block when it succeeds.
.org $e1a9
tape_header_retry:
    lda $0370
    bne tape_header_fail
    inc $0370
    lda #$09
    jsr tape_seek_sync_value
    bcs tape_header_fail
    jmp tape_parse_header
tape_header_fail:
    jmp tape_load_error

; Open Basic 64 implements its own commands. The $E1xx private trampolines
; above enter the stable Open Basic 64 stubs at $A022-$A034.

; The public C64 KERNAL jump table is an ABI: every entry is exactly 3 bytes.
.org $ff81
    jmp screen_init         ; CINT
    jmp io_init             ; IOINIT
    jmp ram_init            ; RAMTAS
    jmp restore_vectors     ; RESTOR
    jmp vector              ; VECTOR
    jmp set_message         ; SETMSG
    jmp iec_second          ; SECOND
    jmp iec_talk_secondary  ; TKSA
    jmp mem_top             ; MEMTOP
    jmp mem_bottom          ; MEMBOT
    jmp scan_keyboard       ; SCNKEY
    jmp set_timeout         ; SETTMO
    jmp iec_acptr_public    ; ACPTR
    jmp iec_output_byte     ; CIOUT
    jmp iec_untalk          ; UNTLK
    jmp iec_unlisten_public ; UNLSN
    jmp private_listen      ; LISTEN (same register-preserving entry contract)
    jmp iec_talk            ; TALK
    jmp read_status         ; READST
    jmp set_lfs             ; SETLFS
    jmp set_name            ; SETNAM
    jmp ($031a)            ; OPEN
    jmp ($031c)            ; CLOSE
    jmp ($031e)            ; CHKIN
    jmp ($0320)            ; CHKOUT
    jmp ($0322)            ; CLRCHN
    jmp ($0324)            ; CHRIN
    jmp ($0326)            ; CHROUT
    jmp ($0330)            ; LOAD
    jmp ($0332)            ; SAVE
    jmp set_time            ; SETTIM
    jmp read_time           ; RDTIM
    jmp ($0328)            ; STOP
    jmp ($032a)            ; GETIN
    jmp ($032c)            ; CLALL
    jmp update_time         ; UDTIM
    jmp screen_size         ; SCREEN
    jmp plot                ; PLOT
    jmp io_base             ; IOBASE

.org $fffa
    .word nmi_dispatch,reset,irq_dispatch


; These two small I/O entry points used to share float_print.asm. They are
; independently retained here without the BASIC floating-point formatter.
.org $fda3
private_fda3_entry:
    jmp io_init
.org $fdab
private_screen_clear_body:
    jsr screen_init
    lda #$0c
    sta $cc
    sta $cd
    lda #$00
    sta $d1
    sta $f3
    ldx #$01
    ldy #$84
    lda #$d8
    sec
    rts

; A selected IEC device may hold DATA/CLOCK during a seek or sector read,
; but a stalled device must eventually return control to the ROM shell.
; The normal clock-bit polling path above is unchanged. These slow paths
; allow 3*65536 samples (roughly four PAL CPU seconds) and set READST bit 7
; plus carry on expiry. They use no X/Y or zero-page caller workspace.
; $039A/$039B are mask/expected value, $039C-$039E the sample countdown.
.org $e5a2
iec_wait_data_high_bounded:
    lda #$80
    bne iec_wait_high_select
iec_wait_clock_high_bounded:
    lda #$40
iec_wait_high_select:
    sta $039a
    sta $039b
    jmp iec_wait_bounded
iec_wait_eoi_low_bounded:
    lda #$80
    sta $039a
    lda #$00
    sta $039b
iec_wait_bounded:
    lda #$00
    sta $039c
    sta $039d
    lda #$03
    sta $039e
iec_wait_bounded_sample:
    lda $dd00
    and $039a
    cmp $039b
    beq iec_wait_bounded_ready
    dec $039c
    bne iec_wait_bounded_sample
    dec $039d
    bne iec_wait_bounded_sample
    dec $039e
    bne iec_wait_bounded_sample
    jmp iec_timeout
iec_wait_bounded_ready:
    lda $dd00
    clc
    rts
iec_wait_bounded_end:
