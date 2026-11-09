; IEC disk commands, device 8. No host traps or BASIC entry points.
; SPDX-License-Identifier: Apache-2.0
; Text transfers use SEQ files with ASCII payloads. Program loads use PRG.
; Import commits only after a complete <=512-byte read into scratch RAM.
; A failed/oversize program load is never marked executable, and every byte
; is checked before writing to the application arena $6000-$9FFF.
.equ DSKPTR $10
.equ DSK_COUNT $092a
.equ DSK_NODE $092c
.equ DSK_BYTE $092d
.equ DSK_TEMP $092e
.equ DSK_RETRY $092f
.equ DSK_PREVIOUS $0961
.equ DISK_STAGE $1400

cmd_dir:
    lda ARGC
    cmp #$01
    bne disk_usage
    lda #$03
    sta DISK_MODE
    lda #$24
    sta DISKBUF
    lda #$01
    sta DISK_LEN
    jsr disk_open
    bcs disk_fail
    jsr disk_get
    bcs disk_fail
    jsr disk_get
    bcs disk_fail
dir_line:
    jsr disk_get
    bcs disk_fail
    sta DSK_TEMP
    jsr disk_get
    bcs disk_fail
    ora DSK_TEMP
    beq dir_finished
    jsr disk_get
    bcs disk_fail
    sta NUMBER
    jsr disk_get
    bcs disk_fail
    sta NUMBER+1
    jsr print_number
    jsr space
dir_text:
    jsr disk_get
    bcs disk_fail
    cmp #$00
    beq dir_newline
    cmp #$a0
    bne dir_not_space
    lda #$20
dir_not_space:
    cmp #$c1
    bcc dir_ascii
    cmp #$db
    bcs dir_ascii
    and #$7f
dir_ascii:
    cmp #$20
    bcc dir_text
    cmp #$7f
    bcs dir_text
    jsr putc
    jmp dir_text
dir_newline:
    jsr newline
    jmp dir_line
dir_finished:
    jsr disk_close
    bcs disk_error
    clc
    rts

disk_usage:
    jmp usage_error
disk_fail:
    jsr disk_close
disk_error:
    point disk_error_text
    jmp error
disk_close:
    jsr $ffcc
    lda #$02
    jmp $ffc3

cmd_dcat:
    lda ARGC
    cmp #$02
    bne disk_usage
    lda #$00
    sta DISK_MODE
    sta DSK_PREVIOUS
    ldx #$01
    jsr disk_open_arg
    bcs disk_fail
dcat_next:
    jsr disk_get
    bcs disk_read_finish
    ldx DSK_PREVIOUS
    sta DSK_PREVIOUS
    cmp #$0a
    bne dcat_not_lf
    cpx #$0d
    beq dcat_next
    bne dcat_emit
dcat_not_lf:
    cmp #$0d
    bne dcat_not_cr
    lda #$0a
    bne dcat_emit
dcat_not_cr:
    cmp #$09
    bne dcat_printable
    lda #$20
dcat_printable:
    cmp #$20
    bcc dcat_replace
    cmp #$7f
    bcc dcat_emit
dcat_replace:
    lda #$2e
dcat_emit:
    jsr putc
    jmp dcat_next

disk_read_finish:
    lda DISK_STATUS
    cmp #$40
    bne disk_fail
    jsr disk_close
    bcs disk_error
    clc
    rts

cmd_import:
    lda ARGC
    cmp #$03
    bne disk_usage
    lda #$00
    sta DISK_MODE
    sta DSK_COUNT
    sta DSK_COUNT+1
    sta DSKPTR
    lda #$14
    sta DSKPTR+1
    ldx #$01
    jsr disk_open_arg
    bcs disk_fail
import_read:
    jsr disk_get
    bcs import_finished
    ldx DSKPTR+1
    cpx #$16
    bcs import_too_big
    ldy #$00
    sta (DSKPTR),y
    inc DSKPTR
    bne import_count
    inc DSKPTR+1
import_count:
    inc DSK_COUNT
    bne import_read
    inc DSK_COUNT+1
    jmp import_read
import_finished:
    jsr disk_read_finish
    bcs disk_return
    jsr disk_dos_check
    bcs disk_error
    ldx #$02
    jsr argptr
    jsr resolve_path
    bcc import_existing
    ldx #$02
    jsr argptr
    lda #$01
    jsr create_path
    bcs disk_path_error
import_existing:
    jsr require_file
    bcs disk_file_error
    sta DSK_NODE
    jsr data_ptr
    ldy #$00
import_copy:
    lda DISK_STAGE,y
    sta (DATA),y
    iny
    bne import_copy
    inc DATA+1
import_copy_tail:
    lda DISK_STAGE+$100,y
    sta (DATA),y
    iny
    bne import_copy_tail
    lda DSK_NODE
    jsr node_ptr
    ldy #$02
    lda DSK_COUNT
    sta (NODE),y
    iny
    lda DSK_COUNT+1
    sta (NODE),y
    clc
disk_return:
    rts
import_too_big:
    jsr disk_close
    point disk_size_text
    jmp error
disk_path_error:
    jmp path_error
disk_file_error:
    jmp file_error

cmd_export:
    lda ARGC
    cmp #$03
    bne disk_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs disk_path_error
    jsr require_file
    bcs disk_file_error
    jsr data_ptr
    lda DATA
    sta DSKPTR
    lda DATA+1
    sta DSKPTR+1
    ldy #$02
    lda (NODE),y
    sta DSK_COUNT
    iny
    lda (NODE),y
    sta DSK_COUNT+1
    lda #$01
    sta DISK_MODE
    ldx #$02
    jsr disk_open_arg
    bcs disk_fail
export_next:
    lda DSK_COUNT
    ora DSK_COUNT+1
    beq export_finish
    ldy #$00
    lda (DSKPTR),y
    jsr $ffd2
    bcs disk_fail
    jsr $ffb7
    bne disk_fail
    inc DSKPTR
    bne export_count
    inc DSKPTR+1
export_count:
    lda DSK_COUNT
    bne export_count_low
    dec DSK_COUNT+1
export_count_low:
    dec DSK_COUNT
    jmp export_next
export_finish:
    jsr disk_close
    bcs disk_error
    jsr disk_dos_check
    bcs disk_error
    clc
    rts

cmd_load:
    lda ARGC
    cmp #$02
    bne disk_usage
    lda #$00
    sta PROGRAM_READY
    lda #$02
    sta DISK_MODE
    ldx #$01
    jsr disk_open_arg
    bcs disk_fail
    jsr disk_get
    bcs disk_fail
    sta ENTRY_POINT
    sta DSKPTR
    jsr disk_get
    bcs disk_fail
    cmp #$60
    bcc load_range_error
    cmp #$a0
    bcs load_range_error
    sta ENTRY_POINT+1
    sta DSKPTR+1
load_next:
    jsr disk_get
    bcs load_finished
    ldx DSKPTR+1
    cpx #$a0
    bcs load_range_error
    ldy #$00
    sta (DSKPTR),y
    inc DSKPTR
    bne load_next
    inc DSKPTR+1
    jmp load_next
load_finished:
    jsr disk_read_finish
    bcs disk_return
    lda DSKPTR
    sta PROGRAM_END
    cmp ENTRY_POINT
    bne load_ready
    lda DSKPTR+1
    cmp ENTRY_POINT+1
    beq load_range_error
load_ready:
    lda DSKPTR+1
    sta PROGRAM_END+1
    lda #$01
    sta PROGRAM_READY
    clc
    rts
load_range_error:
    jsr disk_close
    point disk_range_text
    jmp error

cmd_run:
    lda ARGC
    cmp #$01
    beq run_loaded
    jsr cmd_load
    bcs disk_return
run_loaded:
    lda PROGRAM_READY
    beq run_no_program
    tsx
    stx SAVED_SP
    jsr run_call
    sei
    cld
    ldx SAVED_SP
    txs
    lda #$37
    sta $01
    jsr $ffcc
    jsr terminal_style
    cli
    clc
    rts
run_call:
    jmp (ENTRY_POINT)
run_no_program:
    point disk_no_program_text
    jmp error

; Mode: 0=read ASCII SEQ, 1=write new ASCII SEQ, 2=read PRG, 3=directory.
; PRG reads use the standard load channel (secondary 0) and plain filename.
; No ,P,R suffix is needed there, including on the user's older emulator.
; Filename argument X is copied/uppercased and bounded to 16 characters.
; No DOS command or overwrite prefixes are accepted.
disk_open_arg:
    jsr argptr
    ldy #$00
disk_name_next:
    lda (TEXT),y
    beq disk_name_end
    cpy #$10
    bcs disk_name_bad
    cmp #$20
    bcc disk_name_bad
    cmp #$7f
    bcs disk_name_bad
    cmp #$2c
    beq disk_name_bad
    cmp #$3a
    beq disk_name_bad
    cmp #$40
    beq disk_name_bad
    cmp #$61
    bcc disk_name_store
    cmp #$7b
    bcs disk_name_store
    and #$df
disk_name_store:
    sta DISKBUF,y
    iny
    bne disk_name_next
disk_name_end:
    cpy #$00
    beq disk_name_bad
    lda DISK_MODE
    cmp #$02
    beq disk_name_length
    lda #$2c
    sta DISKBUF,y
    iny
    lda #$53
    sta DISKBUF,y
    iny
    lda #$2c
    sta DISKBUF,y
    iny
    lda DISK_MODE
    cmp #$01
    beq disk_name_write
    lda #$52
    bne disk_name_mode
disk_name_write:
    lda #$57
disk_name_mode:
    sta DISKBUF,y
    iny
disk_name_length:
    sty DISK_LEN
disk_open:
    lda DISK_LEN
    ldx #$00
    ldy #$0f
    jsr $ffbd
    ldy #$02
    lda DISK_MODE
    cmp #$02
    bcc disk_open_channel
    ldy #$00
disk_open_channel:
    lda #$02
    ldx #$08
    jsr $ffba
    lda #$00
    sta DISK_STATUS
    sta $90
    jsr $ffc0
    bcs disk_name_bad
    ldx #$02
    lda DISK_MODE
    cmp #$01
    beq disk_open_write
    jmp $ffc6
disk_open_write:
    jmp $ffc9
disk_name_bad:
    sec
    rts

; Carry clear means A is a valid byte, including the final byte with ST=$40.
; A subsequent call returns carry set without requesting a byte past EOF.
; The inherited IEC implementation can signal one transient $42 EOI state.
disk_get:
    lda DISK_STATUS
    bne disk_get_stop
    lda #$01
    sta DSK_RETRY
disk_get_try:
    jsr $ffcf
    bcs disk_get_error
    sta DSK_BYTE
    jsr $ffb7
    sta DISK_STATUS
    and #$bf
    bne disk_get_stop
    lda DSK_BYTE
    clc
    rts
disk_get_error:
    jsr $ffb7
    sta DISK_STATUS
    cmp #$42
    bne disk_get_stop
    dec DSK_RETRY
    bmi disk_get_stop
    lda #$40
    sta $90
    jmp disk_get_try
disk_get_stop:
    sec
    rts

; Bus acknowledgement alone does not establish that a write succeeded.
; Read the drive's DOS status after closing a completed text transfer.
disk_dos_check:
    lda #$00
    sta $90
    jsr $ffbd
    lda #$0f
    ldx #$08
    ldy #$0f
    jsr $ffba
    jsr $ffc0
    bcs disk_dos_fail
    ldx #$0f
    jsr $ffc6
    bcs disk_dos_fail
    jsr $ffcf
    bcs disk_dos_fail
    cmp #$30
    bne disk_dos_fail
    jsr $ffcf
    bcs disk_dos_fail
    cmp #$30
    bne disk_dos_fail
    lda #$00
    beq disk_dos_close
disk_dos_fail:
    lda #$01
disk_dos_close:
    pha
    jsr $ffcc
    lda #$0f
    jsr $ffc3
    pla
    cmp #$01
    rts

disk_error_text:
    .text "disk: I/O, DOS or filename error"
    .byte 0
disk_size_text:
    .text "disk: file exceeds 512 bytes"
    .byte 0
disk_range_text:
    .text "load: PRG must fit $6000-$9fff"
    .byte 0
disk_no_program_text:
    .text "run: no program loaded"
    .byte 0
