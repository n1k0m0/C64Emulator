; SPDX-License-Identifier: Apache-2.0
; C64 Unix ROM terminal extensions. Independently implemented from the
; documented CIA keyboard matrix and public KERNAL interface.
;
; This is a terminal-oriented key map: Ctrl+letters give ASCII control
; characters; both Run/Stop variants give ETX; shifted Return/Delete/Space
; remain Return/Delete/Space. Commodore is tracked but has no graphics mode.
; Shift+minus gives underscore; Shift+pound gives vertical bar. Other text
; punctuation follows the physical C64 keyboard (Shift+2 quote, Shift+6 &,
; Shift+comma <, Shift+period >). Shifted letters retain PETSCII C1-DA.
;
; Private state, all outside the shell's zero page and RAM:
; 0385 saved CIA1 port A, 0386/0387 saved DDRA/DDRB,
; 0388 column mask, 0389 row sample, 038A candidate key,
; 038B translated key, 038C repeat countdown, 038D GETIN result,
; 0398/0399 CHROUT return A/status. All remaining bytes are reserved.
; No zero-page location is used by the scanner, so IRQ scans cannot corrupt
; the shell's string pointers. Public SCNKEY also masks IRQ re-entry.

.org $e000
terminal_state_init:
    lda #$00
    ldx #$63
terminal_state_clear:
    sta $033c,x
    dex
    bpl terminal_state_clear
    sta $94                  ; no pending serial output byte
    sta $95
    sta $028a
    sta $028b
    sta $028c
    sta $028d
    sta $028e
    lda #$40
    sta $c5                  ; no held key after reset
    sta $038a
    rts

; Public CHROUT preserves X/Y even when a line scrolls or clears the screen.
; The inherited device-specific A/status result is returned unchanged.
terminal_character_out:
    pha
    txa
    pha
    tya
    pha
    tsx
    lda $0103,x
    jsr character_out
    sta $0398
    php
    pla
    sta $0399
    pla
    tay
    pla
    tax
    pla
    lda $0399
    pha
    lda $0398
    plp
    rts

; Remove one queue byte atomically. The IRQ keyboard producer cannot append
; in the middle of the shift and have its new count overwritten by GETIN.
keyboard_get_atomic:
    php
    sei
    lda $c6
    beq keyboard_get_none
    cmp #$0b
    bcc keyboard_get_valid
    lda #$0a
    sta $c6                  ; never read beyond the ten-byte queue
keyboard_get_valid:
    lda $0277
    pha
    ldy #$01
keyboard_get_move:
    cpy $c6
    beq keyboard_get_finish
    lda $0277,y
    sta $0276,y
    iny
    bne keyboard_get_move
keyboard_get_finish:
    dec $c6
    pla
    sta $038d
    plp
    lda $038d
    clc
    rts
keyboard_get_none:
    plp
    lda #$00
    clc
    rts

; Eight columns by eight rows. Modifier contacts are collected separately
; from the first ordinary key. Multiple ordinary keys use matrix priority;
; modifiers do not hide a key located later in the matrix.
.org $eecc
terminal_keyboard_scan:
    php
    sei
    lda $dc00
    sta $0385
    lda $dc02
    sta $0386
    lda $dc03
    sta $0387
    lda #$ff
    sta $dc02
    lda #$00
    sta $dc03
    sta $028d
    lda #$40
    sta $038a
    lda #$fe
    sta $0388
    ldx #$00
terminal_scan_column:
    lda $0388
    sta $dc00
    lda $dc01
    eor #$ff
    sta $0389
    ldy #$08
terminal_scan_row:
    lsr $0389
    bcc terminal_scan_next
    cpx #$0f
    beq terminal_scan_shift
    cpx #$34
    beq terminal_scan_shift
    cpx #$3a
    beq terminal_scan_control
    cpx #$3d
    beq terminal_scan_commodore
    lda $038a
    cmp #$40
    bne terminal_scan_next
    stx $038a
terminal_scan_next:
    inx
    dey
    bne terminal_scan_row
    sec
    rol $0388
    cpx #$40
    bne terminal_scan_column
    jmp terminal_scan_translate
terminal_scan_shift:
    lda $028d
    ora #$01
    sta $028d
    jmp terminal_scan_next
terminal_scan_control:
    lda $028d
    ora #$04
    sta $028d
    jmp terminal_scan_next
terminal_scan_commodore:
    lda $028d
    ora #$02
    sta $028d
    jmp terminal_scan_next

terminal_scan_translate:
    ldx $038a
    cpx #$40
    beq terminal_scan_released
    cpx $c5
    bne terminal_scan_new_key
    lda $028d
    cmp $028e
    bne terminal_scan_new_key
    lda $038b
    cmp #$14                 ; Delete repeats despite being a control code
    beq terminal_scan_repeat
    cmp #$11
    beq terminal_scan_repeat
    cmp #$1d
    beq terminal_scan_repeat
    cmp #$91
    beq terminal_scan_repeat
    cmp #$9d
    beq terminal_scan_repeat
    cmp #$20
    bcc terminal_scan_done
    cmp #$7f
    bcc terminal_scan_repeat
    cmp #$c1
    bcc terminal_scan_done
    cmp #$db
    bcs terminal_scan_done
terminal_scan_repeat:
    dec $038c
    bne terminal_scan_done
    lda #$04                 ; about 15 repeats/s with the PAL 60 Hz timer
    sta $038c
    lda $038b
    jmp terminal_scan_queue
terminal_scan_new_key:
    stx $c5
    lda $028d
    sta $028e
    jsr terminal_translate_key
    sta $038b
    lda #$1e                 ; 30 scans of initial repeat delay
    sta $038c
    lda $038b
    beq terminal_scan_done
terminal_scan_queue:
    ldx $c6
    cpx $0289
    bcs terminal_scan_done
    cpx #$0a                 ; host changes to XMAX cannot overrun the queue
    bcs terminal_scan_done
    sta $0277,x
    inc $c6
    jmp terminal_scan_done
terminal_scan_released:
    stx $c5
    lda #$00
    sta $038b
    sta $038c
    lda $028d
    sta $028e
terminal_scan_done:
    lda $0385
    sta $dc00
    lda $0386
    sta $dc02
    lda $0387
    sta $dc03
    plp
    rts

terminal_translate_key:
    cpx #$3f                 ; Run/Stop is always terminal interrupt
    beq terminal_translate_stop
    lda $028d
    and #$04
    bne terminal_translate_control
    lda $028d
    and #$01
    beq terminal_translate_plain
    lda terminal_shifted,x
    rts
terminal_translate_plain:
    lda terminal_unshifted,x
    rts
terminal_translate_control:
    lda terminal_unshifted,x
    cmp #$41
    bcc terminal_translate_control_other
    cmp #$5b
    bcs terminal_translate_control_other
    and #$1f
    rts
terminal_translate_control_other:
    cmp #$0d
    beq terminal_translate_done
    cmp #$14
    beq terminal_translate_done
    lda #$00
terminal_translate_done:
    rts
terminal_translate_stop:
    lda #$03
    rts

terminal_unshifted:
    .byte $14,$0d,$1d,$88,$85,$86,$87,$11
    .byte $33,$57,$41,$34,$5a,$53,$45,$00
    .byte $35,$52,$44,$36,$43,$46,$54,$58
    .byte $37,$59,$47,$38,$42,$48,$55,$56
    .byte $39,$49,$4a,$30,$4d,$4b,$4f,$4e
    .byte $2b,$50,$4c,$2d,$2e,$3a,$40,$2c
    .byte $5c,$2a,$3b,$13,$00,$3d,$5e,$2f
    .byte $31,$5f,$00,$32,$20,$00,$51,$03
terminal_shifted:
    .byte $14,$0d,$9d,$8c,$89,$8a,$8b,$91
    .byte $23,$d7,$c1,$24,$da,$d3,$c5,$00
    .byte $25,$d2,$c4,$26,$c3,$c6,$d4,$d8
    .byte $27,$d9,$c7,$28,$c2,$c8,$d5,$d6
    .byte $29,$c9,$ca,$30,$cd,$cb,$cf,$ce
    .byte $2b,$d0,$cc,$5f,$3e,$5b,$40,$3c
    .byte $7c,$2a,$5d,$93,$00,$3d,$5e,$3f
    .byte $21,$5f,$00,$22,$20,$00,$d1,$03
terminal_keyboard_end:
