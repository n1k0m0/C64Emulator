; SPDX-License-Identifier: Apache-2.0
; Preserve the reference BASIC's zero-exponent mantissa payloads.
.org $f110
fp_zero_multiply:
    lda #$00
    sta $03ff
    sta $03e0
    sta $03e1
    rts
fp_zero_multiply_end:

.org $f130
fp_zero_divide:
    ldx #$03
fp_zero_divide_copy:
    lda $03e8,x
    sta $03e2,x
    dex
    bpl fp_zero_divide_copy
    jsr fp_literal_round
    jmp fp_zero_multiply
fp_zero_divide_end:

.org $f150
fp_check_multiply:
    lda $03ee
    bmi fp_multiply_underflow
    bne fp_overflow
    lda $03e0
    beq fp_multiply_underflow
    rts
fp_multiply_underflow:
    ldx #$03
fp_multiply_underflow_copy:
    lda $03f9,x
    sta $03e2,x
    dex
    bpl fp_multiply_underflow_copy
    jmp fp_zero_multiply
fp_check_multiply_end:

.org $f190
fp_save_multiply_input:
    ldx #$03
fp_save_multiply_loop:
    lda $03e2,x
    sta $03f9,x
    dex
    bpl fp_save_multiply_loop
    rts
fp_save_multiply_input_end:

.org $f1b0
fp_check_divide:
    lda $03ee
    bmi fp_zero_divide
    bne fp_overflow
    lda $03e0
    beq fp_zero_divide
    rts
fp_check_divide_end:

; The reference trigonometric polynomials retain these low mantissa bytes
; at zero, even though SIN(0) is numerically zero and COS(0) prints as 1.
.org $f16e
fp_sin_zero:
    lda #$00
    sta $03e0
    sta $03e1
    lda #$c9
    sta $03e2
    lda #$0f
    sta $03e3
    lda #$da
    sta $03e4
    lda #$a2
    sta $03e5
    rts
fp_sin_zero_end:

.org $f19c
fp_cos_zero:
    lda #$ff
    ldx #$03
fp_cos_zero_fill:
    sta $03e2,x
    dex
    bpl fp_cos_zero_fill
    inx
    stx $03e1
    lda #$80
    sta $03e0
    rts
fp_cos_zero_end:

; Independent numeric constants for the C64-compatible RND recurrence.
.org $f1c6
fp_rnd_constant_arg:
    ldy #$00
fp_rnd_constant_loop:
    lda fp_rnd_constants,x
    sta $03e6,y
    inx
    iny
    cpy #$06
    bne fp_rnd_constant_loop
    rts
fp_rnd_constants:
    .byte $98,$00,$b5,$44,$7a,$00
    .byte $68,$00,$a8,$b1,$46,$00
fp_rnd_initial:
    .byte $80,$4f,$c7,$52,$58
fp_rnd_init_state:
    lda #$00
    sta $03fd
    ldx #$04
fp_rnd_init_loop:
    lda fp_rnd_initial,x
    sta $8b,x
    dex
    bpl fp_rnd_init_loop
    rts
fp_rnd_constants_end:
