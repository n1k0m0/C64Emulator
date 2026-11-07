; SPDX-License-Identifier: Apache-2.0
; A decimal literal keeps its low rounding bit until it is consumed.
.org $e3ba
fp_decimal_finish:
    lda $03de
    beq fp_decimal_finish_done
    ; The divide loop's initial count is odd for a large quotient.
    lsr a
    lda $03dd
    beq fp_decimal_first_bit_clear
    ldx #$03
fp_decimal_borrow:
    dec $03e2,x
    lda $03e2,x
    cmp #$ff
    bne fp_decimal_borrow_done
    dex
    bpl fp_decimal_borrow
fp_decimal_borrow_done:
    ldx #$ff
    jmp fp_decimal_guard2
fp_decimal_first_bit_clear:
    ; The normalized large-quotient route can retain a second bit even
    ; when the first bit is zero. Small quotients keep their parser guard.
    bcc fp_decimal_finish_done
    lda $03f0
    cmp #$50
    bcc fp_decimal_finish_done
    lda #$40
    sta $03ff
fp_decimal_finish_done:
    rts
fp_decimal_finish_end:

.org $e3f0
fp_literal_round:
    lda $03ff
    bpl fp_literal_round_done
    jsr fp_round_fac
fp_literal_round_done:
    lda #$00
    sta $03ff
    rts
fp_literal_round_end:

.org $e410
fp_multiply_ten_impl:
    lda $03e0
    beq fp_multiply_ten_zero
    jsr fp_push
    jsr fp_set_ten
    jsr fp_pop_arg
    jmp fp_multiply
fp_multiply_ten_zero:
    jmp fp_zero
fp_multiply_ten_impl_end:

.org $e430
fp_divide_ten_impl:
    jsr fp_push
    jsr fp_set_ten
    jsr fp_pop_arg
    jsr fp_swap
    jmp fp_divide
fp_divide_ten_impl_end:

; When a literal is the smaller subtraction operand, keep its fractional
; guard through exponent alignment. Other operations consume the rounded
; 32-bit value, matching the public variable path.
.org $e4cf
fp_add_prepare:
    lda $03ff
    beq fp_add_prepare_clear
    lda $03e0
    cmp $03e6
    bcs fp_add_prepare_rounded
    lda $03e1
    eor $03e7
    bmi fp_add_prepare_guard
    lda $03e1
    bpl fp_add_prepare_rounded
fp_add_prepare_guard:
    lda $03ff
    sta $03ef
    lda #$00
    sta $03ff
    sta $03ee
    rts
fp_add_prepare_rounded:
    jmp $bba3
fp_add_prepare_clear:
    lda #$00
    sta $03ee
    sta $03ef
    rts
fp_add_prepare_end:

; Multiplication's right operand may be a freshly parsed literal whose
; integer significand was lowered by one and whose $03FF guard means +1/2.
; The 32-bit left operand remains in $03E8-$03EB while the product is built.
.org $e35c
fp_multiply_guard:
    lda $03ff
    beq fp_multiply_guard_done
    lda #$00
    sta $03ff
    lsr $03e8
    ror $03e9
    ror $03ea
    ror $03eb
    clc
    ldx #$03
    jmp $ff64
fp_multiply_guard_done:
    rts
fp_multiply_guard_end:

.org $ff64
fp_multiply_guard_add:
    lda $03f4,x
    adc $03e8,x
    sta $03f4,x
    dex
    bpl fp_multiply_guard_add
    bcc fp_multiply_guard_carry_done
    ldx #$03
fp_multiply_guard_carry:
    inc $03f0,x
    bne fp_multiply_guard_carry_done
    dex
    bpl fp_multiply_guard_carry
fp_multiply_guard_carry_done:
    rts
fp_multiply_guard_add_end:
