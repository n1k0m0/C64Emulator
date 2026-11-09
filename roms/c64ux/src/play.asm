; C64/UX game takeover loader. SPDX-License-Identifier: Apache-2.0
;
; play NAME [ENTRY] reads a PRG twice using the ordinary device-8 channel.
; The first pass validates its complete range and start convention without
; modifying shell files. The second pass releases shell RAM to the program.
; The protected reader never uses the shell's $0900-$0FFF workspaces.
;
; Supported payload: $0800-$CFFF inclusive. Writes under BASIC go to RAM.
; $D000 and above are rejected before any first-pass payload write. This
; avoids writing to visible I/O and does not pretend to be a full C64 loader.
;
; Pass two checks the header, length and CRC16 against pass one. A late error
; returns through a cold reset, never through overwritten shell state.
; An ordinary RTS returns through a twelve-byte RAM reset stub at $0260.
;
; Protected workspace (all below the lowest accepted PRG load address):
;   $12-$2A    private zero-page state, unused by the inherited IEC routines
;   $0200-$020F  PETSCII filename (up to 16 bytes)
;   $0210/$0211  SYS-line terminator offset / error category
;   $0220-$024F  first 48 payload bytes for strict BASIC SYS recognition
;   $0250-$0253  saved entry and end at handoff
;   $0254-$0257  one-shot cold-boot failure marker
;   $0260-$026B  synthetic RTS return stub

.equ P_PTR $12
.equ P_PTR_H $13
.equ P_START $14
.equ P_START_H $15
.equ P_END $16
.equ P_END_H $17
.equ P_ENTRY $18
.equ P_ENTRY_H $19
.equ P_CRC $1a
.equ P_CRC_H $1b
.equ P_EXPECT $1c
.equ P_EXPECT_H $1d
.equ P_NUMBER $1e
.equ P_NUMBER_H $1f
.equ P_TEMP $20
.equ P_TEMP_H $21
.equ P_STATUS $22
.equ P_BYTE $23
.equ P_PHASE $24
.equ P_COUNT $25
.equ P_NAME_LEN $26
.equ P_EXPLICIT $27
.equ P_DIGITS $28
.equ P_BASE $29
.equ P_RETRY $2a
.equ P_NAME $0200
.equ P_STOP $0210
.equ P_ERROR $0211
.equ P_PROBE $0220
.equ P_SAVED_ENTRY $0250
.equ P_SAVED_END $0252
.equ P_MARKER $0254
.equ P_RETURN $0260

; BASIC-slot space is used only before handoff, while LORAM is still set.
.org $be00
cmd_play:
    lda REDIRECT
    cmp #$ff
    bne play_usage
    lda ARGC
    cmp #$02
    beq play_arguments
    cmp #$03
    bne play_usage
play_arguments:
    lda #$00
    sta P_PHASE
    sta P_EXPLICIT
    ldx #$03
play_marker_clear:
    sta P_MARKER,x
    dex
    bpl play_marker_clear
    lda ARGC
    cmp #$03
    bne play_filename
    ldx #$02
    jsr argptr
    jsr play_explicit_number
    bcs play_usage
    inc P_EXPLICIT
play_filename:
    ldx #$01
    jsr argptr
    ldy #$00
play_name_next:
    lda (TEXT),y
    beq play_name_end
    cpy #$10
    bcs play_usage
    cmp #$20
    bcc play_usage
    cmp #$7f
    bcs play_usage
    cmp #$2c
    beq play_usage
    cmp #$3a
    beq play_usage
    cmp #$40
    beq play_usage
    cmp #$61
    bcc play_name_store
    cmp #$7b
    bcs play_name_store
    and #$df
play_name_store:
    sta P_NAME,y
    iny
    bne play_name_next
play_name_end:
    cpy #$00
    beq play_usage
    sty P_NAME_LEN
    say play_check_text
    jsr play_read_image
    bcs play_preflight_error
    lda P_CRC
    sta P_EXPECT
    lda P_CRC_H
    sta P_EXPECT_H
    jsr play_select_entry
    bcs play_start_error
    say play_start_text
    lda #$01
    sta P_PHASE
    jsr play_read_image
    bcs play_abort_reset
    jmp play_handoff
play_usage:
    point play_usage_text
    jmp error
play_start_error:
    point play_entry_text
    jmp error
play_preflight_error:
    lda P_ERROR
    cmp #$02
    beq play_range_error
    point play_io_text
    jmp error
play_range_error:
    point play_range_text
    jmp error
play_command_end:

; Shared stream reader. All loop state is protected from PRG stores.
.org $e1d7
play_read_image:
    lda #$00
    sta P_COUNT
    lda #$ff
    sta P_CRC
    sta P_CRC_H
    jsr play_open
    bcs play_stream_io_error
    jsr play_get
    bcs play_stream_io_error
    sta P_PTR
    lda P_PHASE
    bne play_header_low_check
    lda P_PTR
    sta P_START
    jmp play_header_high
play_header_low_check:
    lda P_PTR
    cmp P_START
    bne play_stream_changed
play_header_high:
    jsr play_get
    bcs play_stream_io_error
    cmp #$08
    bcc play_stream_range_error
    cmp #$d0
    bcs play_stream_range_error
    sta P_PTR_H
    lda P_PHASE
    bne play_header_high_check
    lda P_PTR_H
    sta P_START_H
    jmp play_commit_next
play_header_high_check:
    lda P_PTR_H
    cmp P_START_H
    bne play_stream_changed
play_commit_next:
    jsr play_get
    bcs play_stream_finished
    lda P_PTR_H
    cmp #$d0
    bcs play_stream_range_error
    lda P_PHASE
    beq play_probe_store
    ; No second-pass byte may exceed the complete preflight extent.
    lda P_PTR_H
    cmp P_END_H
    bcc play_payload_store
    bne play_stream_changed
    lda P_PTR
    cmp P_END
    bcs play_stream_changed
play_payload_store:
    ldy #$00
    lda P_BYTE
    sta (P_PTR),y
    jmp play_payload_crc
play_probe_store:
    ldx P_COUNT
    cpx #$30
    bcs play_payload_crc
    lda P_BYTE
    sta P_PROBE,x
    inc P_COUNT
play_payload_crc:
    jsr play_crc_byte
    inc P_PTR
    bne play_commit_next
    inc P_PTR_H
    jmp play_commit_next
play_stream_finished:
    lda P_STATUS
    cmp #$40
    bne play_stream_io_error
    jsr play_close
    bcs play_stream_io_error
    lda P_PTR
    cmp P_START
    bne play_stream_nonempty
    lda P_PTR_H
    cmp P_START_H
    beq play_stream_range_error
play_stream_nonempty:
    lda P_PHASE
    bne play_stream_verify
    lda P_PTR
    sta P_END
    lda P_PTR_H
    sta P_END_H
    clc
    rts
play_stream_verify:
    lda P_PTR
    cmp P_END
    bne play_stream_changed
    lda P_PTR_H
    cmp P_END_H
    bne play_stream_changed
    lda P_CRC
    cmp P_EXPECT
    bne play_stream_changed
    lda P_CRC_H
    cmp P_EXPECT_H
    bne play_stream_changed
    clc
    rts
play_stream_changed:
    lda #$03
    bne play_stream_failed
play_stream_range_error:
    lda #$02
    bne play_stream_failed
play_stream_io_error:
    lda #$01
play_stream_failed:
    sta P_ERROR
    jsr play_close
    sec
    rts
play_stream_end:

; Auto-start at $0801 only recognizes a single simple tokenized SYS line.
; Link pointer, terminator and the following two zero bytes must agree.
; No expressions, further statements or additional BASIC lines are executed.
.org $e397
play_select_entry:
    lda P_EXPLICIT
    bne play_entry_bounds
    lda P_START_H
    cmp #$08
    bne play_direct_entry
    lda P_START
    cmp #$01
    beq play_basic_sys
play_direct_entry:
    lda P_START
    sta P_ENTRY
    lda P_START_H
    sta P_ENTRY_H
play_entry_bounds:
    lda P_ENTRY_H
    cmp P_START_H
    bcc play_entry_bad
    bne play_entry_upper
    lda P_ENTRY
    cmp P_START
    bcc play_entry_bad
play_entry_upper:
    lda P_ENTRY_H
    cmp P_END_H
    bcc play_entry_good
    bne play_entry_bad
    lda P_ENTRY
    cmp P_END
    bcs play_entry_bad
play_entry_good:
    clc
    rts
play_entry_bad:
    sec
    rts
play_basic_sys:
    lda P_COUNT
    cmp #$09
    bcc play_entry_bad
    lda P_PROBE+1
    cmp #$08
    bne play_entry_bad
    lda P_PROBE
    cmp #$08
    bcc play_entry_bad
    sec
    sbc #$01
    tax
    inx
    cpx P_COUNT
    bcs play_entry_bad
    dex
    txa
    tay
    lda P_PROBE,y
    ora P_PROBE+1,y
    bne play_entry_bad
    dey
    sty P_STOP
    lda P_PROBE,y
    bne play_entry_bad
    ldy #$04
play_sys_leading_space:
    cpy P_STOP
    bcs play_entry_bad
    lda P_PROBE,y
    cmp #$20
    bne play_sys_token
    iny
    bne play_sys_leading_space
play_sys_token:
    cmp #$9e
    bne play_entry_bad
    iny
play_sys_number_space:
    cpy P_STOP
    bcs play_entry_bad
    lda P_PROBE,y
    cmp #$20
    bne play_sys_number_begin
    iny
    bne play_sys_number_space
play_sys_number_begin:
    jsr play_number_clear
play_sys_digit:
    cpy P_STOP
    bcs play_sys_number_end
    lda P_PROBE,y
    jsr play_digit
    bcs play_sys_number_end
    jsr play_number_add
    bcs play_entry_bad
    inc P_DIGITS
    iny
    bne play_sys_digit
play_sys_number_end:
    lda P_DIGITS
    beq play_entry_bad
play_sys_trailing_space:
    cpy P_STOP
    beq play_entry_bounds
    lda P_PROBE,y
    cmp #$20
    bne play_entry_bad
    iny
    bne play_sys_trailing_space
play_sys_end:

; Decimal and $hex explicit addresses. The accumulator routines preserve Y.
.org $f0fd
play_explicit_number:
    jsr play_number_clear
    ldy #$00
    lda (TEXT),y
    cmp #$24
    bne play_number_next
    lda #$10
    sta P_BASE
    iny
play_number_next:
    lda (TEXT),y
    beq play_number_done
    jsr play_digit
    bcs play_number_bad
    jsr play_number_add
    bcs play_number_bad
    inc P_DIGITS
    iny
    bne play_number_next
play_number_bad:
    sec
    rts
play_number_done:
    lda P_DIGITS
    beq play_number_bad
    clc
    rts
play_number_clear:
    lda #$00
    sta P_ENTRY
    sta P_ENTRY_H
    sta P_DIGITS
    lda #$0a
    sta P_BASE
    rts
play_digit:
    cmp #$30
    bcc play_number_bad
    cmp #$3a
    bcc play_digit_decimal
    ldx P_BASE
    cpx #$10
    bne play_number_bad
    and #$df
    cmp #$41
    bcc play_number_bad
    cmp #$47
    bcs play_number_bad
    sec
    sbc #$37
    clc
    rts
play_digit_decimal:
    sec
    sbc #$30
    clc
    rts
play_number_add:
    sta P_TEMP
    lda P_ENTRY
    sta P_NUMBER
    lda P_ENTRY_H
    sta P_NUMBER_H
    lda #$00
    sta P_ENTRY
    sta P_ENTRY_H
    ldx P_BASE
play_number_multiply:
    clc
    lda P_ENTRY
    adc P_NUMBER
    sta P_ENTRY
    lda P_ENTRY_H
    adc P_NUMBER_H
    sta P_ENTRY_H
    bcs play_number_bad
    dex
    bne play_number_multiply
    clc
    lda P_ENTRY
    adc P_TEMP
    sta P_ENTRY
    lda P_ENTRY_H
    adc #$00
    sta P_ENTRY_H
    rts
play_number_end:

; File API calls use the private filename and private EOF/retry variables.
.org $fc04
play_open:
    lda P_NAME_LEN
    ldx #$00
    ldy #$02
    jsr $ffbd
    lda #$02
    ldx #$08
    ldy #$00
    jsr $ffba
    lda #$00
    sta $90
    sta P_STATUS
    jsr $ffc0
    bcs play_get_stop
    ldx #$02
    jmp $ffc6
play_close:
    jsr $ffcc
    lda #$02
    jmp $ffc3
play_get:
    lda P_STATUS
    bne play_get_stop
    lda #$01
    sta P_RETRY
play_get_try:
    jsr $ffcf
    bcs play_get_error
    sta P_BYTE
    jsr $ffb7
    sta P_STATUS
    and #$bf
    bne play_get_stop
    lda P_BYTE
    clc
    rts
play_get_error:
    jsr $ffb7
    sta P_STATUS
    cmp #$42
    bne play_get_stop
    dec P_RETRY
    bmi play_get_stop
    lda #$40
    sta $90
    jmp play_get_try
play_get_stop:
    sec
    rts
play_file_api_end:

; CRC-16 reflected polynomial $A001, initialized to $FFFF for each pass.
; It detects ordinary changed-file/transfer corruption, not hostile changes.
.org $e079
play_crc_byte:
    lda P_BYTE
    eor P_CRC
    sta P_CRC
    ldx #$08
play_crc_bit:
    lsr P_CRC_H
    ror P_CRC
    bcc play_crc_next
    lda P_CRC
    eor #$01
    sta P_CRC
    lda P_CRC_H
    eor #$a0
    sta P_CRC_H
play_crc_next:
    dex
    bne play_crc_bit
    rts
play_crc_end:

; This whole transition executes from KERNAL. BASIC may be banked out.
.org $fdc2
play_handoff:
    sei
    lda P_ENTRY
    sta P_SAVED_ENTRY
    lda P_ENTRY_H
    sta P_SAVED_ENTRY+1
    lda P_END
    sta P_SAVED_END
    lda P_END_H
    sta P_SAVED_END+1
    jsr $ff87
    jsr $ff8a
    jsr $ff84
    jsr $ff81
    lda #$00
    ldx #$8f
play_clear_zero_page:
    sta $00,x
    dex
    cpx #$01
    bne play_clear_zero_page
    lda #$01
    sta $2b
    lda #$08
    sta $2c
    sta $7b
    sta $ba
    lda P_SAVED_END
    sta $2d
    sta $2f
    sta $31
    lda P_SAVED_END+1
    sta $2e
    sta $30
    sta $32
    lda #$a0
    sta $34
    sta $38
    lda #$ff
    sta $39
    sta $3a
    lda #$7f
    sta $dc00
    lda #$00
    sta $030c
    sta $030d
    sta $030e
    sta $030f
    ldx #$0b
play_copy_return:
    lda play_return_image,x
    sta P_RETURN,x
    dex
    bpl play_copy_return
    ldx #$ff
    txs
    lda #$02
    pha
    lda #$5f
    pha
    lda #$37
    ldx P_SAVED_ENTRY+1
    cpx #$a0
    bcc play_set_bank
    cpx #$c0
    bcs play_set_bank
    lda #$36
play_set_bank:
    sta $01
    lda #$00
    tax
    tay
    clc
    cld
    cli
play_enter:
    jmp (P_SAVED_ENTRY)
play_return_image:
    .byte $78,$a9,$2f,$85,$00,$a9,$37,$85,$01,$6c,$fc,$ff
play_abort_reset:
    ldx #$03
play_abort_mark:
    lda play_abort_magic,x
    sta P_MARKER,x
    dex
    bpl play_abort_mark
    jmp reset
play_boot_notice:
    ldx #$03
play_notice_check:
    lda P_MARKER,x
    cmp play_abort_magic,x
    bne play_notice_clear
    dex
    bpl play_notice_check
    say play_reset_text
play_notice_clear:
    lda #$00
    ldx #$03
play_notice_clear_next:
    sta P_MARKER,x
    dex
    bpl play_notice_clear_next
    rts
play_abort_magic:
    .byte $75,$9a,$3c,$e1
play_handoff_end:

.org $fd18
play_usage_text:
    .text "usage: play name [decimal or $hex]\0"
play_range_text:
    .text "play: PRG must fit $0800-$cfff\0"
play_io_text:
    .text "play: disk read failed\0"
play_entry_text:
    .text "play: invalid entry or no simple SYS\0"
play_messages_end:

.org $e547
play_check_text:
    .text "Checking game...\n\0"
play_start_text:
    .text "Starting game. RAM files released.\n\0"
play_reset_text:
    .text "play: load failed, RAM reset\n\0"
play_progress_end:
