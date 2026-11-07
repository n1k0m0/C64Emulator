; SPDX-License-Identifier: Apache-2.0
; Native 6510 decimal formatter shared with Open Basic 64.
.org $fd18
; Print nine significant decimal digits, rounded by the tenth.
fp_print:
    lda $03e0
    bne fp_print_nonzero
    lda #$30
    jmp $ffd2
fp_print_nonzero:
    lda $03e1
    bpl fp_print_positive
    lda #$2d
    jsr $ffd2
    lda #$00
    sta $03e1
fp_print_positive:
    lda #$00
    sta $03db
fp_print_large:
    lda $03e0
    cmp #$84
    bcc fp_print_small
    bne fp_print_reduce
    lda $03e2
    cmp #$a0
    bcc fp_print_small
fp_print_reduce:
    jsr fp_divide_ten
    inc $03db
    jmp fp_print_large
fp_print_small:
    lda $03e0
    cmp #$81
    bcs fp_print_digits
    jsr fp_multiply_ten
    dec $03db
    jmp fp_print_small
fp_print_digits:
    lda #$00
    sta $03dc
fp_print_digit_loop:
    jsr fp_to_int
    lda $60
    cmp #$0a
    bcc fp_print_digit_valid
    lda #$09
fp_print_digit_valid:
    ldx $03dc
    sta $03a0,x
    jsr fp_push
    ldx $03dc
    lda $03a0,x
    sta $60
    lda #$00
    sta $61
    jsr fp_from_int
    jsr fp_pop_arg
    jsr fp_subtract
    jsr fp_multiply_ten
    inc $03dc
    lda $03dc
    cmp #$0a
    bne fp_print_digit_loop
    lda $03a9
    cmp #$05
    bcc fp_print_trim
    bcs fp_print_round_dispatch
private_fda3_entry:
    jmp io_init
fp_print_round_dispatch:
    ldx #$08
    jmp fp_print_round
private_screen_clear_body:
    jsr screen_init
    lda #$0c
    sta $cc
    sta $cd
    lda #$00
    sta $d1
    sta $f3
    jmp private_screen_clear_middle
fp_print_trim:
    ldx #$08
fp_print_trim_loop:
    lda $03a0,x
    bne fp_print_trim_done
    dex
    bne fp_print_trim_loop
fp_print_trim_done:
    stx $03dd
    lda $03db
    bmi fp_print_negative_power
    cmp #$09
    bcs fp_print_scientific
    jmp fp_print_fixed_positive
fp_print_negative_power:
    cmp #$fe
    bcc fp_print_scientific
    jmp fp_print_fixed_negative
fp_print_fixed_positive:
    lda $03db
    cmp $03dd
    bcs fp_print_fixed_integer
    lda $03dd
fp_print_fixed_integer:
    sta $03de
    ldx #$00
fp_print_fixed_loop:
    lda $03a0,x
    jsr fp_emit_digit
    cpx $03db
    bne fp_print_fixed_next
    cpx $03dd
    bcs fp_print_fixed_next
    lda #$2e
    jsr $ffd2
fp_print_fixed_next:
    inx
    cpx $03de
    bcc fp_print_fixed_loop
    beq fp_print_fixed_loop
    rts
fp_print_fixed_negative:
    lda #$2e
    jsr $ffd2
    lda $03db
    eor #$ff
    sta $03de
fp_print_leading_zero:
    lda $03de
    beq fp_print_fraction_digits
    lda #$30
    jsr $ffd2
    dec $03de
    jmp fp_print_leading_zero
fp_print_fraction_digits:
    ldx #$00
fp_print_fraction_loop:
    lda $03a0,x
    jsr fp_emit_digit
    cpx $03dd
    beq fp_print_done
    inx
    jmp fp_print_fraction_loop
fp_print_scientific:
    lda $03a0
    jsr fp_emit_digit
    lda $03dd
    beq fp_print_exponent
    lda #$2e
    jsr $ffd2
    ldx #$01
fp_print_mantissa_loop:
    lda $03a0,x
    jsr fp_emit_digit
    cpx $03dd
    beq fp_print_exponent
    inx
    jmp fp_print_mantissa_loop
fp_print_exponent:
    lda #$45
    jsr $ffd2
    lda $03db
    bmi fp_print_exponent_negative
    lda #$2b
    jsr $ffd2
    lda $03db
    jmp fp_print_exponent_digits
fp_print_exponent_negative:
    lda #$2d
    jsr $ffd2
    sec
    lda #$00
    sbc $03db
fp_print_exponent_digits:
    ldx #$00
fp_print_tens:
    cmp #$0a
    bcc fp_print_units
    sec
    sbc #$0a
    inx
    jmp fp_print_tens
fp_print_units:
    sta $03de
    txa
    jsr fp_emit_digit
    lda $03de
    jmp fp_emit_digit
fp_emit_digit:
    clc
    adc #$30
    jmp $ffd2
fp_print_done:
    rts
fp_print_end:
