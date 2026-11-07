; SPDX-License-Identifier: Apache-2.0
; Independent C64 keyboard matrix scan. No original ROM instructions used.
; Functional key/PETSCII mappings follow the published C64 keyboard layout:
; https://www.pagetable.com/c64ref/charset/
; Scratch: existing KERNAL $0343/$0344/$0346, standard $CB and $028A-$028E.
; $F1/$F2/$F4 in the plain table are internal modifier markers only.

.org $f100
keyboard_scan_full:
    php
    sei
    cld
    lda #$00
    sta $028d
    lda #$40
    sta $cb
    lda #$fe
    sta $0343
    ldx #$00
keyboard_scan_column:
    lda $0343
    sta $dc00
    lda $dc01
    cmp #$ff
    beq keyboard_empty_column
    sta $0344
    ldy #$08
keyboard_scan_contact_bit:
    lsr $0344
    bcs keyboard_scan_next_bit
    jsr keyboard_record_contact
keyboard_scan_next_bit:
    inx
    dey
    bne keyboard_scan_contact_bit
keyboard_scan_next_column:
    sec
    rol $0343
    bcs keyboard_scan_column
    lda #$7f
    sta $dc00
    lda $028d
    cmp #$03
    bne keyboard_select_key
    cmp $028e
    beq keyboard_quiet
    bit $0291
    bmi keyboard_quiet
    lda $d018
    eor #$02
    sta $d018
keyboard_quiet:
    lda $028d
    sta $028e
    lda #$40
    sta $c5
    jmp keyboard_scan_exit
keyboard_empty_column:
    ; CMP #$FF set C. ADC #7 therefore advances the eight matrix indexes.
    txa
    adc #$07
    tax
    jmp keyboard_scan_next_column
keyboard_select_key:
    ldx $cb
    cpx #$40
    beq keyboard_quiet
    jsr keyboard_translate
    cmp #$ff
    beq keyboard_quiet
    sta $0346
    jmp keyboard_repeat
keyboard_record_contact:
    lda keyboard_plain,x
    cmp #$f0
    bcc keyboard_record_normal
    and #$0f
    ora $028d
    sta $028d
    rts
keyboard_record_normal:
    stx $cb
    rts
keyboard_scan_exit:
    plp
    rts
keyboard_scan_full_end:

.org $fd18
keyboard_plain:
    .byte $14,$0d,$1d,$88,$85,$86,$87,$11
    .byte $33,$57,$41,$34,$5a,$53,$45,$f1
    .byte $35,$52,$44,$36,$43,$46,$54,$58
    .byte $37,$59,$47,$38,$42,$48,$55,$56
    .byte $39,$49,$4a,$30,$4d,$4b,$4f,$4e
    .byte $2b,$50,$4c,$2d,$2e,$3a,$40,$2c
    .byte $5c,$2a,$3b,$13,$f1,$3d,$5e,$2f
    .byte $31,$5f,$f4,$32,$20,$f2,$51,$03
keyboard_shifted:
    .byte $94,$8d,$9d,$8c,$89,$8a,$8b,$91
    .byte $23,$d7,$c1,$24,$da,$d3,$c5,$ff
    .byte $25,$d2,$c4,$26,$c3,$c6,$d4,$d8
    .byte $27,$d9,$c7,$28,$c2,$c8,$d5,$d6
    .byte $29,$c9,$ca,$30,$cd,$cb,$cf,$ce
    .byte $db,$d0,$cc,$dd,$3e,$5b,$ba,$3c
    .byte $a9,$c0,$5d,$93,$ff,$3d,$de,$3f
    .byte $21,$5f,$ff,$22,$a0,$ff,$d1,$83

.org $fda6
keyboard_commodore:
    .byte $94,$8d,$9d,$8c,$89,$8a,$8b,$91
    .byte $96,$b3,$b0,$97,$ad,$ae,$b1,$ff
    .byte $98,$b2,$ac,$99,$bc,$bb,$a3,$bd
    .byte $9a,$b7,$a5,$9b,$bf,$b4,$b8,$be
    .byte $29,$a2,$b5,$30,$a7,$a1,$b9,$aa
    .byte $a6,$af,$b6,$dc,$3e,$5b,$a4,$3c
    .byte $a8,$df,$5d,$93,$ff,$3d,$de,$3f
    .byte $81,$5f,$ff,$95,$a0,$ff,$ab,$83
keyboard_control:
    .byte $ff,$ff,$ff,$ff,$ff,$ff,$ff,$ff
    .byte $1c,$17,$01,$9f,$1a,$13,$05,$ff
    .byte $9c,$12,$04,$1e,$03,$06,$14,$18
    .byte $1f,$19,$07,$9e,$02,$08,$15,$16
    .byte $12,$09,$0a,$92,$0d,$0b,$0f,$0e
    .byte $ff,$10,$0c,$ff,$ff,$1b,$00,$ff
    .byte $1c,$ff,$1d,$ff,$ff,$1f,$1e,$ff
    .byte $90,$06,$ff,$05,$ff,$ff,$11,$ff

keyboard_translate:
    lda $028d
    and #$04
    bne keyboard_translate_control
    lda $028d
    and #$02
    bne keyboard_translate_commodore
    lda $028d
    and #$01
    bne keyboard_translate_shift
    lda keyboard_plain,x
    rts
keyboard_translate_control:
    lda keyboard_control,x
    rts
keyboard_translate_commodore:
    lda keyboard_commodore,x
    rts
keyboard_translate_shift:
    lda keyboard_shifted,x
    rts
keyboard_translate_end:

.org $fec2
keyboard_repeat:
    ldx $cb
    cpx $c5
    bne keyboard_new_key
    lda $028d
    cmp $028e
    bne keyboard_new_key
    lda $028a
    bmi keyboard_repeat_allowed
    and #$40
    bne keyboard_repeat_exit
    cpx #$00               ; DELETE / INSERT
    beq keyboard_repeat_allowed
    cpx #$02               ; horizontal cursor
    beq keyboard_repeat_allowed
    cpx #$07               ; vertical cursor
    beq keyboard_repeat_allowed
    cpx #$3c               ; SPACE / SHIFT-SPACE
    bne keyboard_repeat_exit
keyboard_repeat_allowed:
    lda $028c
    beq keyboard_repeat_interval
    dec $028c
    bne keyboard_repeat_exit
keyboard_repeat_interval:
    dec $028b
    bne keyboard_repeat_exit
    lda #$04
    sta $028b
    lda $c6
    bne keyboard_repeat_exit ; do not accumulate auto-repeat ahead of input
    beq keyboard_enqueue
keyboard_new_key:
    stx $c5
    lda $028d
    sta $028e
    lda #$10
    sta $028c
    lda #$04
    sta $028b
keyboard_enqueue:
    ldx $c6
    cpx $0289
    bcs keyboard_repeat_exit
    cpx #$0a
    bcs keyboard_repeat_exit
    lda $0346
    sta $0277,x
    inc $c6
keyboard_repeat_exit:
    jmp keyboard_scan_exit
keyboard_repeat_end:
