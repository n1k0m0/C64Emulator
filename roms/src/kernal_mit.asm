; SPDX-License-Identifier: Apache-2.0
; Open Kernal 64 -- independently written NMOS 6510 ROM.
; Public entry addresses and RAM conventions follow the published C64 manual.
; The public jump table, selected BASIC glue, screen/keyboard, clock, and IEC
; protocol are implemented here; device limits are documented in README.

.org $f900
tape_save:
    lda $b7
    cmp #$11
    bcs tape_save_error
    lda $b9
    and #$01               ; SA bit 0: 0=BASIC/relocatable, 1=absolute
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
    jsr tape_write_eot
    jsr tape_motor_stop
    lda #$00
    clc
    jmp restore_interrupts
tape_save_error:
    lda #$08
    sec
    jmp restore_interrupts

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
    pha
    eor $0369
    sta $0369
    pla
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
    pha
    eor $0369
    sta $0369
    pla
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
    jmp tape_emit_pulse

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
    pha
    eor $036e
    sta $036e
    pla
    jsr tape_write_bit
    dec $036c
    bne tape_write_bit_next
    lda $036e
    jmp tape_write_bit
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
    tay
    lda tape_emit_low-1,y
tape_emit_start:
    pha
    lda $01
    and #$f7               ; WRITE low
    sta $01
    pla
    sta $dc06
    lda tape_emit_high-1,y
    sta $dc07
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

tape_emit_low:
    .byte $04,$c4,$3c
tape_emit_high:
    .byte $01,$01,$02

; Shared synchronous RS-232 framing for 5-8 data bits, parity and 1-2 stops.
.org $fac0
rs232_data_count:
    lda $0293
    and #$60
    lsr A
    lsr A
    lsr A
    lsr A
    lsr A
    eor #$03
    adc #$05               ; last LSR cleared carry (masked input bits 0..4)
    sta $0298
    rts

rs232_transmit:
rs232_transmit_extended:
    sta $a7
    pha
    txa
    pha
    jsr rs232_tx_ready
    bcs rs232_tx_blocked
    php
    sei
    jsr rs232_data_count
    lda #$00
    sta $a8              ; number of one bits
    jsr rs232_tx_low
    jsr rs232_delay_full
    ldx $0298
rs232_extended_tx_bit:
    lsr $a7
    bcc rs232_extended_tx_zero
    inc $a8
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
    bmi rs232_extended_tx_fixed
    asl a
    asl a                  ; carry selects even (1) versus odd (0)
    lda $a8
    and #$01
    bcs rs232_extended_tx_parity
    eor #$01
rs232_extended_tx_parity:
    beq rs232_extended_tx_parity_zero
    jsr rs232_tx_high
    jmp rs232_extended_tx_parity_wait
rs232_extended_tx_fixed:
    lda $0294
    asl a
    asl a
    lda #$00
    rol a
    eor #$01              ; mark=1, space=0
    bpl rs232_extended_tx_parity
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
    plp
    pla
    tax
    pla
    clc
    rts

rs232_tx_blocked:
    pla
    tax
    pla
    lda #$05
    rts

rs232_receive_extended:
    txa
    pha
    php
    sei
    jsr rs232_data_count
    jsr rs232_delay_full
    jsr rs232_delay_half
    lda #$00
    sta $a7
    sta $a8
    ldx $0298
rs232_extended_rx_bit:
    lda $dd01
    lsr A
    bcc rs232_extended_rx_zero
    inc $a8
rs232_extended_rx_zero:
    ror $a7
    jsr rs232_delay_receive
    dex
    bne rs232_extended_rx_bit
    ldx $0298
rs232_extended_rx_align:
    cpx #$08
    beq rs232_extended_rx_parity
    lsr $a7
    inx
    jmp rs232_extended_rx_align
rs232_extended_rx_parity:
    lda $0294
    and #$20
    beq rs232_extended_rx_stop
    lda $0294
    bmi rs232_extended_rx_parity_wait ; mark/space do not check received parity
    asl a
    asl a
    lda $a8
    and #$01
    bcs rs232_extended_rx_expected
    eor #$01
rs232_extended_rx_expected:
    sta $a9
    lda $dd01
    and #$01
    cmp $a9
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
    plp
    pla
    tax
    lda $a7
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
    plp
    pla
    tax
    lda #$00
    sec
    rts
rs232_gate_wait:
    lda $dd01
    eor $a5
    and $a6
    bmi rs232_gate_dsr
    beq rs232_gate_ok
    jmp rs232_gate_count
rs232_gate_dsr:
    lda #$40
    jmp rs232_gate_error
rs232_gate_ok:
    clc
    rts
rs232_receive:
    jsr rs232_rx_ready
    bcs rs232_receive_return
rs232_receive_wait:
    lda $dd01
    lsr a
    bcs rs232_receive_wait
    jmp rs232_receive_extended
rs232_receive_return:
    rts

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
    jsr system_cartridge_check
    bne reset_no_cartridge
    jmp ($8000)
reset_no_cartridge:
    jsr ram_init
    jsr restore_vectors
    jsr io_init
    jsr screen_init
    cli
    jmp basic_cold          ; BASIC owns its cold vector at $A000

ram_init:
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
    sta $cf                 ; no software cursor overlay survives RAM initialization
    sta $028a               ; default repeat: cursor, delete/insert, space
    sta $028d               ; current modifier flags
    sta $028e               ; preceding modifier flags
    sta $0291               ; allow SHIFT+Commodore character-set switching
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
    lda #$40
    sta $c5                 ; no previously held ordinary key
    sta $cb
    lda #$04
    sta $028b               ; repeat interval
    lda #$10
    sta $028c               ; initial repeat delay
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
    jsr configure_video_clock ; bounded raster detection; 60 Hz CIA timer on PAL/NTSC
    lda #$81
    sta $dc0d               ; enable timer A IRQ
    lda #$11
    sta $dc0e               ; start timer A, load latch
    jmp private_fda3_body   ; public IOINIT and private $FDA3 share their return ABI

screen_init:
    jsr cursor_hide
    lda #$01
    sta $cc                 ; CHRIN enables blinking while collecting input
    lda #$04
    sta $0288               ; KERNAL screen page; CINT restores the default layout
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
    sta $d01a               ; screen initialization masks VIC interrupt sources
screen_clear:
    lda #$00
    sta $d3
    sta $d6
    sta $d4                 ; quote mode
    sta $033f               ; reverse mode
    sta $fb
    sta $f3
    lda $0288               ; only the KERNAL screen pointer moves this buffer
    sta $fc
    lda #$d8
    sta $f4                 ; color RAM is independent of the VIC bank
    ldy #$00
    ldx #$03
screen_clear_loop:
    lda #$20
    sta ($fb),y
    lda $0286
    sta ($f3),y
    iny
    bne screen_clear_next
    inc $fc
    inc $f4
    dex
screen_clear_next:
    cpx #$00
    bne screen_clear_loop
    cpy #$e8
    bne screen_clear_loop   ; exactly 1000 cells; the sprite-pointer tail survives
    jmp editor_reset

restore_vectors:
    ldx #$1f
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
    lda #$00
    rts

read_status:
    jmp rs232_read_status

update_time:
    inc $a2
    bne time_check_day
    inc $a1
    bne time_check_day
    inc $a0
time_check_day:
    ; Match the original C64's rollover after $4F1A00. Comparisons remain
    ; binary when an interrupted program has decimal mode enabled.
    lda $a0
    cmp #$4f
    bcc time_done
    bne time_reset
    lda $a1
    cmp #$1a
    bcc time_done
    bne time_reset
    lda $a2
    beq time_done
time_reset:
    lda #$00
    sta $a0
    sta $a1
    sta $a2
time_done:
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
character_rs232:
    pla
    jmp rs232_transmit
character_bad_device:
    pla
    lda #$05
    sec
    rts
character_screen:
    ; The incoming character is already on the stack. Save both indexes
    ; across every screen path, including recursive DELETE and scrolling.
    txa
    pha
    tya
    pha
    php
    lda $cc
    pha
    lda #$01
    sta $cc                 ; suppress cursor redraw while keyboard/jiffy IRQs continue
    jsr cursor_hide
    tsx
    lda $0105,x
    cld
    jsr character_screen_body
    pla
    sta $cc
    plp
    pla
    tay
    pla
    tax
    pla
    clc
    rts
character_screen_body:
    jmp screen_prepare_character
screen_dispatch:
    ldx #$0f
screen_color_find:
    cmp screen_color_codes,x
    beq screen_set_color
    dex
    bpl screen_color_find
    cmp #$0d
    jbeq screen_newline
    cmp #$93
    beq screen_clear_command
    cmp #$13
    beq screen_home
    cmp #$11
    jbeq screen_down
    cmp #$91
    jbeq screen_up
    cmp #$1d
    jbeq screen_right
    cmp #$9d
    jbeq screen_left
    cmp #$12
    jbeq reverse_on
    cmp #$92
    jbeq reverse_off
    cmp #$14
    jbeq backspace
    cmp #$94
    jbeq editor_insert
    cmp #$08
    beq screen_case_lock
    cmp #$09
    beq screen_case_unlock
    cmp #$0e
    beq screen_lowercase
    cmp #$8e
    beq screen_uppercase
    cmp #$20
    bcc screen_ignored
    cmp #$80
    jbcc screen_printable
    cmp #$a0
    bcc screen_ignored       ; unknown high controls never become glyphs
    jmp screen_printable
screen_set_color:
    stx $0286
    rts
screen_case_lock:
    lda #$80
    bne screen_case_store
screen_case_unlock:
    lda #$00
screen_case_store:
    sta $0291
    rts
screen_lowercase:
    lda $d018
    ora #$02
    bne screen_set_charset
screen_uppercase:
    lda $d018
    and #$fd
screen_set_charset:
    sta $d018
    rts
screen_clear_command:
    jmp screen_clear
screen_home:
    lda #$00
    sta $d3
    sta $d6
    sta $c7
    lda #$ff
    sta $c9                 ; HOME abandons the original input prompt boundary
screen_ignored:
    rts
screen_newline:
    jsr editor_bounds
    ldx $d0
    lda $d5
    cmp #$50
    bne screen_newline_last
    inx
screen_newline_last:
    stx $d6
    lda #$00
    sta $d3
    sta $d8
    sta $c7
screen_next_row:
    inc $d6
    lda $d6
    cmp #$19
    bcc screen_ignored
    jsr screen_scroll
    lda #$18
    sta $d6
    rts
screen_down:
    lda #$00
    sta $c7
    jmp screen_next_row
screen_up:
    lda #$00
    sta $c7
    lda $d6
    beq screen_ignored
    dec $d6
    rts
screen_right:
    lda #$00
    sta $c7
    inc $d3
    lda $d3
    cmp #$28
    bcc screen_ignored
    lda #$00
    sta $d3
    jmp screen_next_row
screen_left:
    lda $c7
    beq screen_left_column
    lda #$00
    sta $c7
    rts
screen_left_column:
    lda $d3
    bne screen_left_step
    lda $d6
    beq screen_ignored
    dec $d6
    lda #$28
    sta $d3
screen_left_step:
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
    jmp editor_delete

screen_printable:
    jsr petscii_to_screen
screen_write_code:
    lda $c7
    bne screen_ignored
    ldy $d6
    jsr screen_row_address
    ldy $d3
    lda $0341
    ora $033f
    sta ($fb),y
    lda $0286
    sta ($f3),y
    lda $d8
    beq screen_write_advance
    dec $d8
screen_write_advance:
    lda $d3
    cmp #$27
    beq screen_write_wrap
    inc $d3
    rts
screen_write_wrap:
    jmp editor_wrap

screen_scroll:
    ; Copy complete pages in order: interleaving adjacent destination pages
    ; would overwrite the next page's first 40 source bytes prematurely.
    lda #$00
    sta $d1
    lda #$28
    sta $fb
    lda $0288
    sta $d2
    sta $fc
    ldx #$06               ; three full pages in each of screen and color passes
scroll_pass:
    ldy #$00
scroll_page:
    lda ($fb),y
    sta ($d1),y
    iny
    bne scroll_page
    inc $d2
    inc $fc
    dex
    cpx #$03
    beq scroll_tail
    cpx #$00
    bne scroll_page
scroll_tail:
    lda ($fb),y
    sta ($d1),y
    iny
    cpy #$c0
    bne scroll_tail
    lda #$20
    cpx #$03
    beq scroll_blank
    lda $0286
scroll_blank:
    sta ($d1),y
    iny
    cpy #$e8
    bne scroll_blank
    cpx #$00
    beq scroll_done
    lda #$d8
    sta $d2
    sta $fc
    bne scroll_pass
scroll_done:
    jmp editor_scroll_links

screen_color_codes:
    .byte $90,$05,$1c,$9f,$9c,$1e,$1f,$9e,$81,$95,$96,$97,$98,$99,$9a,$9b

; For keyboard input, CHRIN collects and echoes a line, then returns it
; character by character.  The line buffer lives at $0200-$0257.
character_in:
    lda $99
    beq line_input
    cmp #$02
    jbeq rs232_receive
    cmp #$08
    bcc character_input_empty
    jmp character_serial_in
character_input_empty:
    lda #$00
    rts
line_input:
    lda $033c
    cmp #$01               ; $80 is an abandoned collector after a BASIC warm JMP
    beq line_read
    lda $d6
    sta $c9                 ; input prompt origin; scrolling keeps it aligned
    lda $d3
    sta $ca
    lda #$00
    sta $033d
    sta $d8
    sta $c7
    sta $cc                 ; public BLNSW: zero enables the waiting cursor
    lda #$80
    sta $033c               ; collecting: clamp printable input at 80 cells
line_wait:
    jsr get_input
    beq line_wait
    cmp #$0d
    beq line_finish
    jsr character_out
    lda $d6
    cmp $c9
    bcc line_left_prompt
    bne line_wait
    lda $d3
    cmp $ca
    bcs line_wait
line_left_prompt:
    lda #$ff
    sta $c9
    jmp line_wait
line_finish:
    php
    sei
    lda #$01
    sta $cc
    jsr cursor_hide         ; screen input never captures the cursor's reverse bit
    cld
    jsr editor_capture
    lda #$0d
    jsr character_out
    lda #$01
    sta $033c
    plp

line_read:
    ldx $033d
    lda $0200,x
    inx
    stx $033d
    cpx $033e
    bcc line_read_more
    dec $033c
line_read_more:
    dex                     ; Open BASIC's CHRIN caller uses the returned offset
    clc
    rts

; Public SCNKEY uses the complete independent matrix implementation.
scan_keyboard:
    jmp keyboard_scan_full

.org $ea1f
irq_body:
    lda $dc0d
    lsr a
    bcc irq_done
    jsr update_time
    jsr cursor_tick
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
    jmp system_stop

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
    lda #$00
    sta $90                 ; a new operation must not inherit a prior EOI
    beq open_file_impl
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
    jbcc rs232_close_small
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

check_input_impl:
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

check_output_impl:
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
    jbeq tape_load
    cmp #$08
    jbcc load_bad_device
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
    jbcc save_bad_device
    lda $b7
    jbeq save_no_filename
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
.org $edb2
screen_size:
    ldx #$28
    ldy #$19
    rts

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
    .word private_irq_service,break_handler,system_nmi,open_file,close_file,check_input,check_output,clear_channels
    .word character_in,character_out,stop_key,get_input,close_all,serial_unsupported,load_file,save_file

screen_row_low:
screen_row_address:
    ; Y is a physical row. Screen = ($0288 << 8) + 40*Y; color = $D800 + 40*Y.
    ; All callers use binary arithmetic. Neither VIC register $D018 nor CIA2
    ; bank bits are changed: applications configure the displayed bank separately.
    sty $fb
    tya
    asl a
    asl a
    adc $fb                 ; preceding shift cleared carry: 4*row + row
    pha
    lsr a
    lsr a
    lsr a
    lsr a
    lsr a
    ora #$d8
    sta $f4
    and #$03
    clc
    adc $0288
    sta $fc
    pla
    asl a
    asl a
    asl a
    sta $fb
    sta $f3
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
    jmp system_warm

; BASIC initializes its own vectors and CHRGET image.
basic_cold:
    jmp ($a000)

.org $e544
    jmp screen_init         ; common private screen-clear entry

.org $fd00
basic_warm:
    jmp ($a002)
.org $fd15
    jmp restore_vectors     ; common private vector-restore entry

; IEC bus output.  CIA 2 PA3/4/5 are inverted: a set output bit pulls the
; corresponding ATN/CLOCK/DATA wire low.  PA6/7 sample CLOCK/DATA input.
.org $f200
iec_atn_low:
    lda $dd00
    ora #$08
    sta $0349
    sta $dd00
    rts
iec_atn_high:
    lda $dd00
    and #$f7
    sta $0349
    sta $dd00
    rts
iec_clock_low:
    lda $dd00
    ora #$10
    and #$df             ; release DATA on the falling CLOCK edge
    sta $0349
    sta $dd00
    rts
iec_clock_high:
    lda $dd00
    and #$ef
    sta $0349
    sta $dd00
    rts
iec_data_low:
    lda $dd00
    ora #$20
    sta $0349
    sta $dd00
    rts
iec_data_high:
    lda $dd00
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
    jmp iec_wait_data_high_loop ; selected listener may hold DATA while busy

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
    ; A selected talker can hold CLOCK low while fetching the next disk sector.
    jmp iec_wait_clock_high_outer
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
    lda $dd00              ; wait for the listener's EOI reply
    bmi iec_send_wait_eoi_low
    bcs iec_send_return
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

; Synchronous PAL/NTSC RS-232 rates 50-2400 baud and custom timer mode.
; Control selects width/stops; command selects parity and 3-line/X-line modes.
; Frames are IRQ-atomic; modem gates remain interruptible and time out finitely.
.org $f438
rs232_initialize:
    ldy #$00
    lda $b7
    beq rs232_default_rate
    lda ($bb),y
    jmp rs232_control_byte
rs232_default_rate:
    lda #$06
rs232_control_byte:
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
    and #$0e               ; bits1..3 are reserved; x-line and half duplex are supported
    bne rs232_unsupported
    jmp rs232_configure
rs232_default_command:
    lda #$00
    sta $0294
rs232_configure:
    txa
    bne rs232_standard_rate
    lda $b7
    cmp #$04
    bcc rs232_unsupported
    jmp rs232_custom_rate
rs232_standard_rate:
    lda $02a6
    beq rs232_ntsc_rate
    lda rs232_full_low,x
    ldy rs232_full_high,x
    bne rs232_store_period
rs232_ntsc_rate:
    lda rs232_ntsc_low,x
    ldy rs232_ntsc_high,x
rs232_store_period:
    sta $0299
    sty $029a
rs232_configured:
    lda $dd02
    ora #$04
    sta $dd02
    jmp rs232_initialize_lines
rs232_unsupported:
    lda #$05
    sec
    rts

rs232_tx_low:
    lda $dd00
    and #$fb
    sta $0349
    sta $dd00
    rts
; PAL bit periods are 985248 Hz / baud. Timer latches compensate for the
; ROM instructions between consecutive PA2 transitions (about 100 cycles).
rs232_delay_full:
    sec
    lda $0299
    sbc #$64
    jmp rs232_delay_sbc_high
rs232_delay_half:
    lda $029a
    lsr a
    pha
    lda $0299
    ror a
    sec
    sbc #$64
    sta $dd04
    pla
    sbc #$00
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
rs232_initialize_lines:
    lda #$00
    sta $0297
    lda #$06
    sta $dd03               ; RTS/DTR outputs; RX/DCD/CTS/DSR inputs
    lda $0294
    lsr a
    lda $dd01
    ora #$06
    bcc rs232_initialize_store
    and #$fb                ; X-line OPEN asserts DTR
rs232_initialize_store:
    sta $dd01
    clc
    jmp rs232_tx_high

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
.org $e51b
io_base:
    ldx #$00
    ldy #$dc
    rts

rs232_full_high:
    .byte $00,$4c,$33,$22,$1c,$19,$0c,$06,$03,$02,$01
rs232_ntsc_high:
    .byte $00,$4f,$35,$24,$1d,$1a,$0d,$06,$03,$02,$01

.org $e5a0
private_screen_clear_end:
    sec
    rts

.org $ef00
rs232_ntsc_low:
    .byte $00,$e7,$44,$52,$b4,$a2,$51,$a9,$54,$38,$aa

.org $f57d
set_time:
    php
    sei
    sta $a2
    stx $a1
    sty $a0
    plp
    rts

read_time:
    php
    sei
    lda $a2
    ldx $a1
    ldy $a0
    plp
    rts

mem_top:
    bcs mem_top_read
    stx $0283
    sty $0284
mem_top_read:
    ldx $0283
    ldy $0284
    rts

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

.org $f5af
set_name:
    sta $b7
    stx $bb
    sty $bc
    rts

.org $ef10
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
    jbcs tape_load_name_error
    jsr tape_motor_start
tape_find_header:
    lda #$00
    sta $0370               ; first header copy has not been retried
    jsr tape_seek_sync
    jbcs tape_load_error
tape_parse_header:
    lda #$00
    sta $0369               ; header XOR checksum
    sta $0373               ; filename mismatch flag
    ldx #$00
tape_header_byte:
    jsr tape_read_byte
    jbcs tape_header_retry
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
    jbcs tape_header_retry
    cmp $0369
    jbne tape_header_retry
    lda $0364
    cmp #$05               ; a valid EOT header ends searching, independent of name
    jbeq tape_end_header
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
    lda $0364
    cmp #$03               ; an absolute tape header retains its load address
    beq tape_load_absolute
    lda $b9
    beq tape_load_relocate
tape_load_absolute:
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
    jbcs tape_retry_read
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
    jbcs tape_retry_read
    cmp $0369
    jbne tape_retry_read
    jsr tape_motor_stop
    ldx $fb
    ldy $fc
    lda #$00
    clc
    jmp restore_interrupts
tape_load_verify_error:
    jmp tape_retry_verify
tape_load_error:
    jsr tape_motor_stop
tape_load_name_error:
    lda #$04
    sec
    jmp restore_interrupts

tape_motor_start:
    jsr tape_video_pause
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
    jmp tape_video_restore

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
    bit $90                ; a bad byte while searching is recoverable
    bpl tape_sync_restart  ; only a real pulse timeout/end-of-tape terminates search
    rts                    ; tape_read_byte returned carry set

tape_read_byte:
    lda $037e
    beq tape_byte_marker
    jsr tape_pulse          ; stay phase-locked to consecutive byte markers
    jbcs tape_byte_fail
    jsr tape_pulse
    jbcs tape_byte_fail
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
.org $ef40
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
    jmp restore_interrupts

; A failed first header may still have a valid duplicate.  Reparse that
; header and continue directly to the first data block when it succeeds.
.org $ef80
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

; $E000-$E4BF are reserved for the MIT BASIC extension. This KERNAL
; does not embed floating-point routines or private Open BASIC trampolines.

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


; Private initialization/IRQ entries retained independently of BASIC math.
.org $e5a5
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
.org $fda3
private_fda3_entry:
    jmp io_init
.org $febc
private_irq_restore_febc:
    pla
    tay
    pla
    tax
    pla
    rti

; Quoted controls are visible reversed glyphs, not editor commands. Keep
; this before the color/cursor dispatch so listing strings is non-destructive.
.org $fe50
screen_prepare_character:
    cmp #$0d
    beq screen_return_reset
    ldx $d8
    beq screen_not_inserting
    cmp #$94
    jbeq editor_insert
    cmp #$22
    beq screen_quote_toggle
    jmp screen_quoted_character
screen_not_inserting:
    cmp #$14
    jbeq backspace
    cmp #$22
    beq screen_quote_toggle
    ldx $d4
    jbeq screen_dispatch
screen_quoted_character:
    cmp #$20
    bcc screen_quoted_low
    cmp #$80
    jbcc screen_printable
    cmp #$a0
    jbcs screen_printable
    ora #$40
    bne screen_quoted_emit
screen_quoted_low:
    ora #$80
screen_quoted_emit:
    sta $0341
    jmp screen_write_code
screen_quote_toggle:
    ldx $c7
    jbne screen_ignored
    lda $d4
    eor #$01
    sta $d4
    lda #$22
    jmp screen_printable
screen_return_reset:
    lda #$00
    sta $d4
    sta $033f
    sta $d8
    sta $c7
    jmp screen_newline

screen_prepare_character_end:

; A new channel selection starts a fresh status word. Preserve the indexes
; that BASIC line/string input and machine-code callers keep across I/O.
.org $efa0
check_input:
    lda #$00
    sta $90
    txa
    pha
    tya
    pha
    jsr rs232_check_input
    jmp channel_register_return
check_output:
    lda #$00
    sta $90
    txa
    pha
    tya
    pha
    jsr rs232_check_output
    jmp channel_register_return
character_serial_in:
    bit $90
    bvs character_serial_eof ; the preceding byte already carried EOI
    txa
    pha
    tya
    pha
    jsr iec_accept_byte
channel_register_return:
    sta $0348
    pla
    tay
    pla
    tax
    lda $0348
    rts
character_serial_eof:
    lda #$00
    clc
    rts
channel_register_wrappers_end:

; Screen editor state: $D9-$F1 bit7 marks a new logical line. A cleared
; bit links the row to its predecessor, with a maximum of two physical rows.
; $C7 is a pending input cursor beyond cell 80, $C9/$CA the input origin,
; $D0/$D5/$D7 the current logical start/capacity/offset, and $D8 insert count.
; No BASIC workspace is allocated: all are published KERNAL editor bytes.
.org $e5bc
editor_reset:
    lda #$00
    sta $d8
    sta $c7
    lda #$ff
    sta $c9
    ldx #$18
    lda #$80
editor_reset_rows:
    sta $d9,x
    dex
    bpl editor_reset_rows
    clc
    rts

.org $e547
editor_wrap:
    ldx $d6
    lda $d9,x
    bpl editor_wrap_second
    stx $d0
    cpx #$18
    beq editor_wrap_extend
    lda $da,x
    bpl editor_wrap_down
editor_wrap_extend:
    jsr editor_open_row
editor_wrap_down:
    lda #$00
    sta $d3
    jmp screen_next_row
editor_wrap_second:
    bit $033c
    bpl editor_wrap_down
    lda #$01
    sta $c7                 ; full keyboard line: further glyphs are ignored
    rts
editor_wrap_end:

.org $efd6
editor_bounds:
    ldx $d6
    lda $d3
    sta $d7
    cpx #$00
    beq editor_bounds_first
    lda $d9,x
    bmi editor_bounds_first
    dex
    clc
    lda $d7
    adc #$28
    sta $d7
editor_bounds_first:
    stx $d0
    ldy #$28
    cpx #$18
    beq editor_bounds_size
    lda $da,x
    bmi editor_bounds_size
    ldy #$50
editor_bounds_size:
    sty $d5
    txa
    tay
    jmp screen_row_address

editor_capture:
    jsr editor_bounds
    lda #$00
    sta $d4
    ldy $d5
editor_trim:
    dey
    lda ($fb),y
    cmp #$20
    bne editor_trim_done
    tya
    bne editor_trim
    beq editor_trim_empty
editor_trim_done:
    iny
editor_trim_empty:
    sty $c8
    ldy #$00
    lda $c9
    cmp $d0
    beq editor_capture_origin
    sec
    sbc $d0
    cmp #$01
    bne editor_capture_copy
    lda $d5
    cmp #$50
    bne editor_capture_copy
    lda $ca
    clc
    adc #$28
    tay
    jmp editor_capture_copy
editor_capture_origin:
    ldy $ca
editor_capture_copy:
    sty $d7                 ; decode prompt quotes without returning the prompt
    ldy #$00
    ldx #$00
editor_capture_next:
    cpy $c8
    bcs editor_capture_end
    lda ($fb),y
    jsr editor_to_petscii
    cpy $d7
    bcc editor_capture_skip
    sta $0200,x
    inx
editor_capture_skip:
    iny
    bne editor_capture_next
editor_capture_end:
    lda #$0d
    sta $0200,x
    inx
    stx $033e
    rts

editor_to_petscii:
    ; Reversed control glyphs inside a quoted string recover their original
    ; PETSCII byte; ordinary reverse video does not change the text itself.
    cmp #$80
    bcc editor_decode_plain
    pha
    lda $d4
    beq editor_decode_reverse
    pla
    cmp #$a0
    bcc editor_decode_low_control
    cmp #$c0
    bcc editor_decode_plain
    cmp #$e0
    bcs editor_decode_plain
    eor #$40
    rts
editor_decode_low_control:
    and #$7f
    rts
editor_decode_reverse:
    pla
editor_decode_plain:
    and #$7f
    cmp #$20
    bcc editor_decode_letter
    cmp #$40
    bcc editor_decode_quote
    cmp #$60
    bcc editor_decode_graphic
    eor #$c0
    rts
editor_decode_graphic:
    ora #$80
    rts
editor_decode_letter:
    ora #$40
    rts
editor_decode_quote:
    cmp #$22
    bne editor_decode_done
    pha
    lda $d4
    eor #$01
    sta $d4
    pla
editor_decode_done:
    rts
editor_capture_helpers_end:

.org $f507
editor_delete:
    lda $c7
    beq editor_delete_left
    dec $c7
    jmp editor_delete_position
editor_delete_left:
    lda $d3
    bne editor_delete_move
    ldx $d6
    lda $d9,x
    bmi editor_delete_done
editor_delete_move:
    jsr screen_left
editor_delete_position:
    jsr editor_bounds
    ldy $d7
    lda ($fb),y
    and #$7f
    cmp #$22
    bne editor_delete_shift
    lda $d4
    eor #$01
    sta $d4
editor_delete_shift:
    iny
    cpy $d5
    bcs editor_delete_blank
    lda ($fb),y
    dey
    sta ($fb),y
    iny
    lda ($f3),y
    dey
    sta ($f3),y
    iny
    jmp editor_delete_shift
editor_delete_blank:
    dey
    lda #$20
    sta ($fb),y
    lda $0286
    sta ($f3),y
editor_delete_done:
    rts
editor_delete_end:

.org $f191
editor_insert:
    lda $c7
    bne editor_insert_done
    jsr editor_bounds
    lda $d5
    sec
    sbc $d7
    cmp $d8
    beq editor_insert_extend
    ldy $d5
    dey
    lda ($fb),y
    cmp #$20
    beq editor_insert_shift
editor_insert_extend:
    lda $d5
    cmp #$50
    beq editor_insert_done   ; never discard the 80th occupied character
    jsr editor_open_row
    jsr editor_bounds
editor_insert_shift:
    ldy $d5
    dey
editor_insert_next:
    cpy $d7
    beq editor_insert_blank
    dey
    lda ($fb),y
    iny
    sta ($fb),y
    dey
    lda ($f3),y
    iny
    sta ($f3),y
    dey
    jmp editor_insert_next
editor_insert_blank:
    lda #$20
    sta ($fb),y
    lda $0286
    sta ($f3),y
    inc $d8
editor_insert_done:
    rts
editor_insert_end:

.org $fc04
editor_open_row:
    lda $d0
    cmp #$18
    bne editor_open_prepare
    jsr screen_scroll
    dec $d0
    dec $d6
editor_open_prepare:
    ldx $d0
    inx
    stx $c8                 ; row inserted after the logical line's first half
    lda #$98
    sta $fb
    sta $f3
    lda #$c0
    sta $d1
    sta $f5
    lda $0288
    clc
    adc #$03
    sta $fc
    sta $d2
    lda #$db
    sta $f4
    sta $f6
    ldx #$18
editor_open_next:
    cpx $c8
    beq editor_open_blank
    lda $d8,x
    sta $d9,x
    ldy #$27
editor_open_copy:
    lda ($fb),y
    sta ($d1),y
    lda ($f3),y
    sta ($f5),y
    dey
    bpl editor_open_copy
    sec
    lda $fb
    sbc #$28
    sta $fb
    sta $f3
    bcs editor_open_source
    dec $fc
    dec $f4
editor_open_source:
    sec
    lda $d1
    sbc #$28
    sta $d1
    sta $f5
    bcs editor_open_destination
    dec $d2
    dec $f6
editor_open_destination:
    dex
    jmp editor_open_next
editor_open_blank:
    ldy #$27
editor_open_fill:
    lda #$20
    sta ($d1),y
    lda $0286
    sta ($f5),y
    dey
    bpl editor_open_fill
    lda #$00
    sta $d9,x               ; the inserted row is a logical continuation
    rts
editor_open_row_end:

.org $ff2b
editor_scroll_links:
    ldx #$00
editor_scroll_link:
    lda $da,x
    sta $d9,x
    inx
    cpx #$18
    bne editor_scroll_link
    lda #$80
    sta $f1
    ora $d9
    sta $d9                 ; a row exposed at the top begins a logical line
    lda $c9
    cmp #$19
    bcs editor_scroll_done
    dec $c9                 ; zero becomes $FF: the prompt scrolled off screen
editor_scroll_done:
    rts
editor_scroll_links_end:

.org $ff55
; Commodore PRG p82: SAVE secondary bit1 appends an end-of-tape header.
; Header identifier 5 is specified by Commodore's cassette format. Both
; copies use the ordinary leader/countdown/XOR encoder, with no data block.
tape_write_eot:
    lda $b9
    and #$02
    beq tape_eot_done
    lda #$05
    sta $0364
    lda #$89
    jsr tape_write_header_block
    lda #$09
    jmp tape_write_header_block
tape_eot_done:
    rts
tape_end_header:
    jsr tape_mark_end
    jmp tape_load_error

.org $f0b1
; Host input injection and SCNKEY both append PETSCII to $0277 / $C6.
get_input:
    lda $99
    beq keyboard_get
    cmp #$02
    jbeq rs232_poll
    cmp #$08
    bcc keyboard_empty
    jmp character_serial_in
keyboard_get:
    ; Publish the shorter queue only after moving all remaining characters.
    ; The emulator may append a host-paste byte as soon as $C6 decreases.
    ; IRQ exclusion also prevents a matrix scan from appending mid-shift.
    php
    sei
    ldx $c6
    beq keyboard_empty_atomic
    lda $0277
    pha
    dex
    ldy #$01
keyboard_shift:
    cpy $c6
    beq keyboard_result
    lda $0277,y
    sta $0276,y
    iny
    bne keyboard_shift
keyboard_result:
    stx $c6
    dey
    pla
    jmp keyboard_atomic_finish
keyboard_empty_atomic:
    txa
keyboard_atomic_finish:
    plp
    ora #$00              ; GETIN callers test Z to distinguish an empty queue
    clc
    rts
keyboard_empty:
    lda #$00
    clc
    rts


.org $fc7e
; A printable PETSCII byte is converted to one screen code.
petscii_to_screen:
    cmp #$40
    bcc screen_code_ready
    cmp #$60
    bcc code_subtract_40
    cmp #$80
    bcc code_subtract_20
    cmp #$c0
    bcc code_subtract_40
    cmp #$ff
    bne code_subtract_80
    lda #$de               ; PETSCII $FF is pi, not the $7F block glyph
    bne code_subtract_80
code_subtract_20:
    eor #$20               ; PETSCII $60-$7F -> screen $40-$5F
    bne screen_code_ready   ; mapped result cannot be zero
code_subtract_40:
    sec
    sbc #$40
    bcs screen_code_ready   ; both input ranges are at least $40
code_subtract_80:
    eor #$80               ; PETSCII $C0-$FE -> screen $40-$7E
screen_code_ready:
    sta $0341
    rts


; Relocated internal implementations; the public jump-table slots stay fixed.
.org $f1d9
set_lfs:
    sta $b8
    stx $ba
    sty $b9
    rts
set_timeout:
    sta $0285
    rts

; PLOT preserves its caller's coordinate registers while removing any old
; cursor overlay. Capture uses the saved address, including after HIBASE changes.
.org $f1e4
plot:
    bcs plot_read
    php
    sei
    stx $d6
    sty $d3
    jsr cursor_hide
    lda #$00
    sta $c7
    plp
plot_read:
    ldx $d6
    ldy $d3
    rts

; Software cursor. Public state is BLNSW $CC, BLNCT $CD, GDBLN $CE,
; BLNON $CF and GDCOL $0287. $02A7-$02AA cache the screen-row address,
; color-row high byte and column while an overlay is visible. The saved
; physical address lets relocation and PLOT restore the old cell correctly.
; FB/FC and F3/F4 are borrowed only with IRQs disabled, then restored exactly.
.org $e56b
cursor_tick:
    php
    cld
    ldx $cc
    bne cursor_cancel
    ldx $cd
    beq cursor_due
    dec $cd
    bne cursor_return
cursor_due:
    ldx $cf
    bne cursor_cancel
    inx
    bne cursor_action
cursor_hide:
    php
    sei
cursor_cancel:
    lda #$14               ; 20 jiffies per phase, using the 60 Hz system tick
    sta $cd
    lda $cf
    beq cursor_return
    ldx #$00
cursor_action:
    lda $fb
    pha
    lda $fc
    pha
    lda $f3
    pha
    lda $f4
    pha
    txa
    beq cursor_restore
    jmp cursor_plot
cursor_return:
    plp
    rts

.org $e5d1
cursor_restore:
    lda $02a7
    sta $fb
    sta $f3
    lda $02a8
    sta $fc
    lda $02a9
    sta $f4
    ldy $02aa
    lda $ce
    sta ($fb),y
    lda $0287
    sta ($f3),y
    jmp cursor_finish

.org $f54f
cursor_plot:
    ldy $d6
    jsr screen_row_address
    lda $fb
    sta $02a7
    lda $fc
    sta $02a8
    lda $f4
    sta $02a9
    ldy $d3
    sty $02aa
    lda ($fb),y
    sta $ce
    lda ($f3),y
    and #$0f
    sta $0287
    stx $cf                 ; both underlays/cache are valid before the first write
    jmp cursor_plot_color

.org $f09e
cursor_plot_color:
    lda $ce
    eor #$80
    sta ($fb),y
    lda $0286
    sta ($f3),y
    jmp cursor_finish

.org $fca5
cursor_finish:
    stx $cf
    lda #$14
    sta $cd
    pla
    sta $f4
    pla
    sta $f3
    pla
    sta $fc
    pla
    sta $fb
    plp
    rts

; The BASIC extension ends at E4BF. This explicitly reserved 64-byte tail
; detects the actual raster; no emulator model flag is injected into RAM.
; A static VIC test partner falls back to PAL after a bounded polling loop.
.org $e4c0
configure_video_clock:
    lda #$01
    sta $02a6
    ldx #$00
    ldy #$10
video_probe:
    bit $d011
    bpl video_probe_low
    lda $d012
    cmp #$20               ; PAL reaches raster288; NTSC wraps at263
    bcs video_probe_pal
    lda #$00
    sta $02a6
video_probe_next:
    dex
    bne video_probe
    dey
    bne video_probe
video_clock_ready:
    tay
    lda video_jiffy_low,y
    sta $dc04
    lda video_jiffy_high,y
    sta $dc05
    rts
video_probe_low:
    lda $02a6
    bne video_probe_next
    beq video_clock_ready
video_probe_pal:
    lda #$01
    sta $02a6
    bne video_clock_ready
video_jiffy_low:
    .byte $95,$25
video_jiffy_high:
    .byte $42,$40

; Tape pulse timing must not be stretched by VIC badline/sprite DMA.
; Restore these registers on every path after the transport was started.
.org $fcb9
tape_video_pause:
    lda $d011
    sta $037d
    and #$ef
    sta $d011
    lda $d015
    sta $0387
    lda #$00
    sta $d015
    rts
.org $ef32
tape_video_restore:
    lda $037d
    sta $d011
    lda $0387
    sta $d015
    rts

; Public channel selection participates in physical handshaking.
.org $e5f1
rs232_check_input:
    jsr check_input_impl
    bcs rs232_check_input_return
    lda $99
    eor #$02                ; compare without changing successful channel carry
    bne rs232_check_input_return
    jmp rs232_rx_ready
rs232_check_input_return:
    rts
.org $ea6f
rs232_check_output:
    jsr check_output_impl
    bcs rs232_check_output_return
    lda $9a
    eor #$02                ; compare without changing successful channel carry
    bne rs232_check_output_return
    jmp rs232_tx_ready
rs232_check_output_return:
    rts

.org $ff47
rs232_tx_high:
    lda $dd00
    ora #$04
    sta $0349
    sta $dd00
    rts


.org $ef95
rs232_close_lines:
    lda $dd01
    ora #$06
    sta $dd01
    jmp rs232_tx_high
.org $f5b6
rs232_close_small:
    cmp #$02
    bne rs232_close_done
    jsr rs232_close_lines
rs232_close_done:
    clc
    rts

; $A3/$A4 are a finite handshake poll count; $A5 requested RTS/CTS levels,
; $A6 the input mask. $A7-$A9 are frame data/parity scratch. These are KERNAL
; cassette/RS232 work bytes, never the editor cache at $02A7-$02AA.
.org $fd98
rs232_tx_ready:
    lda #$00
    sta $a5
    lda #$c0                ; CTS and DSR must be asserted (low)
    jmp rs232_gate
.org $faa0
rs232_rx_ready:
    lda $0294
    and #$10
    beq rs232_rx_full
    lda #$42                ; half duplex releases RTS and awaits CTS release
rs232_rx_full:
    sta $a5
    lda #$d0                ; also require carrier (DCD low)
rs232_gate:
    sta $a6
    lda $0294
    lsr a
    bcc rs232_gate_return   ; three-line ignores modem inputs
    jmp rs232_gate_lines
rs232_gate_return:
    rts
.org $e536
rs232_gate_lines:
    lda $dd01
    and #$f9
    ora $a5
    sta $dd01
    jmp rs232_gate_timer
.org $ea12
rs232_gate_timer:
    lda #$00
    sta $a3
    sta $a4
    jmp rs232_gate_wait
.org $ff71
rs232_gate_count:
    dec $a3
    bne rs232_gate_again
    dec $a4
    bne rs232_gate_again
    jmp rs232_gate_timeout
rs232_gate_again:
    jmp rs232_gate_wait
.org $f0ee
rs232_gate_timeout:
    and #$40
    lsr a
    lsr a
    lsr a
    adc #$08                ; no CTS -> $10, no DCD -> $08; carry is clear
rs232_gate_error:
    ora $0297
    sta $0297
    lda #$05
    sec
    rts

.org $fd03
rs232_poll:
    jsr rs232_rx_ready
    bcs rs232_poll_return
    lda $dd01
    lsr a
    bcs rs232_poll_none
    jmp rs232_receive_extended
rs232_poll_none:
    jmp rs232_poll_empty
rs232_poll_return:
    rts
