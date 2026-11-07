; SPDX-License-Identifier: Apache-2.0
; Format FAC into $0200 for BASIC STR$, using the writable CHROUT vector.
.org $e000
fp_str_to_buffer:
    lda $0326
    sta $02f8
    lda $0327
    sta $02f9
    lda #<fp_str_capture
    sta $0326
    lda #>fp_str_capture
    sta $0327
    lda #$00
    sta $02fa
    lda $03e1
    bmi fp_str_negative
    lda #$20
    jsr $ffd2
fp_str_negative:
    jsr fp_print
    lda $02f8
    sta $0326
    lda $02f9
    sta $0327
    lda #$00
    sta $64
    lda #$02
    sta $65
    lda $02fa
    sta $75
    rts
fp_str_capture:
    sta $02fb
    tya
    pha
    ldy $02fa
    lda $02fb
    sta $0200,y
    inc $02fa
    pla
    tay
    lda $02fb
    rts
fp_str_end:
