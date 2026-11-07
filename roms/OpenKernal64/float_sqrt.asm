; SPDX-License-Identifier: Apache-2.0
; Seven Newton iterations use the existing six-byte math transfer helpers.
; The positive SQR path skips the private interrupt-return entry at $FEBC.
.org $feb0
fp_square_root_kernal:
    lda $03e0
    beq fp_sqrt_zero_bridge
    lda $03e1
    bmi fp_sqrt_illegal
    bpl fp_square_root_continue

; Private IRQ restore entry, reached by software using the KERNAL's
; internal interrupt return contract.  The positive SQR path branches over it.
fp_irq_restore:
    pla
    tay
    pla
    tax
    pla
    rti

fp_square_root_continue:
    ldx #$f8
    jsr math_save
    jsr fp_zero
    lda $03f8
    lsr a
    adc #$40
    sta $03e0
    lda #$80
    sta $03e2
    lda #$07
    sta $03fe
fp_square_root_step:
    jsr math_copy_fac_arg
    jsr fp_sqrt_load_original
    jsr fp_divide
    jsr fp_add
    dec $03e0
    dec $03fe
    bne fp_square_root_step
fp_square_root_done:
    rts
fp_sqrt_end:

; Save two bytes in the square-root loop without changing its math calls.
.org $f18b
fp_sqrt_load_original:
    ldx #$f8
    jmp math_load

; The division loop leaves $03DE=$1F when numerator significand >= 10's
; $A0000000, else $20. Only a large-quotient decimal division transfers
; the next remainder bit into the parser's literal guard.
.org $fef2
fp_decimal_guard2:
    lda $03de
    lsr a
    bcc fp_decimal_guard_half
    lda $03df
    lsr a
    bcs fp_decimal_guard_three_quarters
    lda $03f0
    cmp #$f0
    bcc fp_decimal_guard_half
fp_decimal_guard_three_quarters:
    lda #$c0
    bne fp_decimal_guard_store
fp_decimal_guard_half:
    lda #$80
fp_decimal_guard_store:
    sta $03ff
    rts
fp_decimal_guard2_end:
