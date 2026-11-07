; SPDX-License-Identifier: Apache-2.0
; Guard-byte normalization after subtraction of opposite-sign FAC/ARG.
.org $ff10
fp_subtract_tail:
    lda $03ef
    beq fp_add_normalize
    lda $03ee
    bne fp_add_normalize
fp_subtract_guard_smaller:
    lda #$00
    sec
    sbc $03ef
    sta $03ef
    txa
    pha
    ldx #$03
fp_subtract_guard_borrow:
    dec $03e2,x
    lda $03e2,x
    cmp #$ff
    bne fp_subtract_guard_borrow_done
    dex
    bpl fp_subtract_guard_borrow
fp_subtract_guard_borrow_done:
    jsr $be1b
    pla
    tax
    bcc fp_add_normalize
    clv
    jmp fp_zero
fp_add_normalize:
    lda $03e2
    bmi fp_add_round_difference
    asl $03ef
    rol $03e5
    rol $03e4
    rol $03e3
    rol $03e2
    dec $03e0
    bne fp_add_normalize
    jmp fp_zero
fp_add_round_difference:
    lda $03ef
    bpl fp_add_subtract_done
    jsr fp_round_fac
fp_add_subtract_done:
    rts
fp_subtract_tail_end:
