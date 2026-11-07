; SPDX-License-Identifier: Apache-2.0
; Four FOR frames, 16 bytes each, at $02A0-$02DF:
; name (2), five-byte limit, five-byte step, line/text resume pointers (4).

command_for:
    jsr advance
    jsr parse_variable
    lda $68
    sta $0352
    lda $69
    sta $0353
    jsr skip_space
    cmp #$b2
    bne syntax_error
    jsr advance
    jsr fp_expression
    lda $0352
    sta $68
    lda $0353
    sta $69
    jsr fp_store_variable
    jsr skip_space
    cmp #$a4
    bne syntax_error
    jsr advance
    jsr fp_expression
    lda $78
    cmp #$04
    bcs out_of_memory
    sta $79
    jsr frame_offset
    ldy #$00
for_save_limit:
    lda $03e0,y
    sta $02a2,x
    inx
    iny
    cpy #$05
    bne for_save_limit
    lda #$01
    sta $60
    lda #$00
    sta $61
    jsr fp_from_int
    jsr skip_space
    cmp #$a9
    bne for_save_frame
    jsr advance
    jsr fp_expression
for_save_frame:
    jsr frame_offset
    lda $0352
    sta $02a0,x
    lda $0353
    sta $02a1,x
    txa
    clc
    adc #$07
    tax
    ldy #$00
for_save_step:
    lda $03e0,y
    sta $02a0,x
    inx
    iny
    cpy #$05
    bne for_save_step
    lda $5a
    sta $02a0,x
    inx
    lda $5b
    sta $02a0,x
    inx
    lda $7a
    sta $02a0,x
    inx
    lda $7b
    sta $02a0,x
    inc $78
    jmp statement_continue

command_next:
    jsr advance
    lda $78
    beq syntax_error
    sec
    sbc #$01
    sta $79
    jsr skip_space
    cmp #$41
    bcc next_float_no_name
    cmp #$5b
    bcs next_float_no_name
    jsr parse_variable
    jsr frame_offset
    lda $68
    cmp $02a0,x
    bne syntax_error
    lda $69
    cmp $02a1,x
    bne syntax_error
next_float_no_name:
    jsr frame_offset
    lda $02a0,x
    sta $68
    lda $02a1,x
    sta $69
    jsr fp_load_variable
    jsr frame_offset
    txa
    clc
    adc #$07
    tax
    ldy #$00
next_float_load_step:
    lda $02a0,x
    sta $03e6,y
    inx
    iny
    cpy #$05
    bne next_float_load_step
    lda $03e7
    sta $03af
    jsr fp_add
    jsr frame_offset
    lda $02a0,x
    sta $68
    lda $02a1,x
    sta $69
    jsr fp_store_variable
    ldx #$05
next_float_save_value:
    lda $03e0,x
    sta $03e6,x
    dex
    bpl next_float_save_value
    jsr frame_offset
    txa
    clc
    adc #$02
    tax
    ldy #$00
next_float_load_limit:
    lda $02a0,x
    sta $03e0,y
    inx
    iny
    cpy #$05
    bne next_float_load_limit
    jsr fp_subtract
    lda $03e0
    beq next_float_continue
    lda $03af
    bmi next_float_negative_step
    lda $03e1
    bmi next_float_continue
    jmp next_float_finished
next_float_negative_step:
    lda $03e1
    bpl next_float_continue
next_float_finished:
    dec $78
    jmp statement_continue
next_float_continue:
    jsr frame_offset
    lda $02ac,x
    sta $5a
    lda $02ad,x
    sta $5b
    lda $02ae,x
    sta $7a
    lda $02af,x
    sta $7b
    lda #$01
    sta $6f
    sta $77
    rts

frame_offset:
    lda $79
    asl A
    asl A
    asl A
    asl A
    tax
    rts
