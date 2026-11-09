; Native C64/UX demonstration program. Load address $6000, return with RTS.
; SPDX-License-Identifier: Apache-2.0
; The index is in the application arena, not in the shell's zero page.
.org $6000
start:
    lda #$00
    sta index
next:
    ldx index
    lda message,x
    beq done
    jsr $ffd2
    inc index
    jmp next
done:
    rts
index:
    .byte 0
message:
    .text "HELLO FROM A NATIVE 6510 PROGRAM!"
    .byte $0d,0
example_end:
