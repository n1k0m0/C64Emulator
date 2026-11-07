; SPDX-License-Identifier: Apache-2.0
; STOP and warm interrupt recovery, independently written from published
; C64 KERNAL/User's Guide contracts. No original firmware is used.
;
; The NMI vector remains a raw hardware-frame hook. Only the default handler
; saves A/X/Y, so user-installed RTI handlers retain the documented ABI.

.org $ee50
system_stop:
    php
    sei
    tya
    pha
    lda $dc00
    pha
    lda #$7f
    sta $dc00
    lda $dc01
    sta $91                 ; last keyboard column, including RUN/STOP
    tax
    pla
    sta $dc00               ; do not leave another caller's column selected
    txa
    and #$80
    beq system_stop_pressed
    pla
    tay
    pla
    and #$fd                ; change only Z in the caller's flags
    pha
    txa                     ; unpressed: return the raw column value
    plp
    rts
system_stop_pressed:
    jsr clear_channels
    lda #$00
    sta $c6
    pla
    tay
    pla
    ora #$02
    pha
    lda #$00
    plp
    rts

system_nmi:
    pha
    txa
    pha
    tya
    pha
    lda $dd0d               ; acknowledge a CIA2 source before accepting another NMI
    bmi system_nmi_return   ; a CIA2 interrupt is not the RESTORE key
    lda $dc00
    pha
    lda #$7f
    sta $dc00
    lda $dc01
    and #$80
    tax
    pla
    sta $dc00
    txa
    beq system_warm
system_nmi_return:
    pla
    tay
    pla
    tax
    pla
    rti

; Discard the interrupt/call frames and restore the interactive machine.
; Do not invoke RAMTAS or BASIC cold initialization: program and variables
; survive. BASIC's warm entry resets its execution stack and channel state.
system_warm:
    sei
    cld
    ldx #$ff
    txs
    lda #$37
    sta $01
    jsr system_cartridge_check
    bne system_warm_basic
    jmp ($8002)
system_warm_basic:
    jsr restore_vectors
    jsr io_init
    lda #$00
    sta $99
    sta $c6
    sta $033c
    sta $033d
    sta $033e
    sta $0340
    sta $d4
    lda #$03
    sta $9a
    lda #$0e
    sta $0286
    jsr screen_init
    cli
    jmp basic_warm

; Cartridge header: cold vector, warm vector, then high-bit CBM and "80".
; Z=1 only when all five signature bytes match. The caller performs the
; indirect transfer after this subroutine has returned, keeping SP balanced.
system_cartridge_check:
    ldx #$04
system_cartridge_byte:
    lda $8004,x
    cmp system_cartridge_signature,x
    bne system_cartridge_done
    dex
    bpl system_cartridge_byte
    inx                     ; $FF -> 0, explicitly set Z after the final match
system_cartridge_done:
    rts
system_cartridge_signature:
    .byte $c3,$c2,$cd,$38,$30
system_helpers_end:
