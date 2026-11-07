; C64 interpreter entry contracts for the MIT Microsoft BASIC core.
; SPDX-License-Identifier: MIT
;
; Public RAM locations and hook roles follow the Commodore 64 Programmer's
; Reference Guide, pp. 307-311. Code below is new integration code. The
; upstream interpreter's packed error text remains in its original format.

.SEGMENT "EXT"

WARM_C64:
        JSR CQCCHN
        LDA #0
        STA CHANNL
        JSR STKINI
        JMP READY

; The PET platform routine handled STOP itself. Public C64 STOP only returns
; Z=1, so turn that result into the ordinary STOP path, preserving CONT frames.
BASIC_STOP_CHECK:
        JSR $FFE1
        BNE BASIC_STOP_RETURN
        SEC
        JMP STOPC
BASIC_STOP_RETURN:
        RTS

; Core errors use byte offsets into their packed text; IERROR takes the C64
; error number in X. Keep this translation outside the upstream parser/math.
C64_CORE_ERROR:
        TXA
        LDX #17
C64_ERROR_FIND:
        CMP C64_ERROR_OFFSETS,X
        BEQ C64_ERROR_FOUND
        DEX
        BPL C64_ERROR_FIND
        JMP ($0300)             ; Unknown code remains X=$FF: generic I/O error.
C64_ERROR_FOUND:
        TXA
        CLC
        ADC #10
        TAX
        JMP ($0300)

C64_ERROR_HANDLER:
        CPX #10
        BCC C64_EXTERNAL_ERROR
        CPX #28
        BCS C64_EXTERNAL_ERROR
        LDA C64_ERROR_OFFSETS-10,X
        TAX
        JMP C64_ERROR_TEXT
C64_EXTERNAL_ERROR:
        JMP BASIC_IO_ERROR_BODY

C64_ERROR_OFFSETS:
        .BYTE ERRNF,ERRSN,ERRRG,ERROD,ERRFC,ERROV,ERROM,ERRUS,ERRBS
        .BYTE ERRDD,ERRDV0,ERRID,ERRTM,ERRLS,ERRBD,ERRST,ERRCN,ERRUF
C64_PLATFORM_END:
