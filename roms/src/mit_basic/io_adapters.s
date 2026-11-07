; C64 public-KERNAL adapters for the MIT-licensed Microsoft BASIC core.
; SPDX-License-Identifier: MIT
;
; Core entry convention: TXTPTR points at the first argument; an RTS resumes
; NEWSTT. No Commodore BASIC binary/source is used. Filename descriptors stay
; live while later numeric arguments are evaluated, then are released only
; immediately before SETNAM / the actual I/O call.
;
; $0A is reserved by this port for LOAD/VERIFY mode. Device, secondary address
; and logical number use the public KERNAL SETLFS variables $BA/$B9/$B8.

.SEGMENT "EXT"

BASIC_LOAD:
        LDA #0
        BEQ BASIC_LOAD_MODE
BASIC_VERIFY:
        LDA #1
BASIC_LOAD_MODE:
        PHA
        JSR BASIC_FILE_ARGS
        PLA
        STA $0A
        LDX TXTTAB
        LDY TXTTAB+1
        JSR $FFD5
        JSR BASIC_IO_CHECK
        LDA $0A
        BEQ BASIC_LOAD_PROGRAM
        RTS
BASIC_LOAD_PROGRAM:
        STA CHANNL             ; A=0: no BASIC channel remains selected.
        LDA CURLIN+1
        CMP #$FF
        BEQ BASIC_LOAD_DIRECT
        ; A LOAD issued by a program chains: retain variables and arrays at
        ; their existing addresses. The new program must not overwrite them.
        ; FLOAD resets DATA and the control stack without clearing variables.
        JSR LNKPRG
        JSR CQCALL
        JSR FLOAD
        JSR STXTPT
        JMP NEWSTT
BASIC_LOAD_DIRECT:
        ; Direct LOAD updates the variable boundary even for secondary 1.
        ; An absolute ML load above BASIC RAM therefore normally needs NEW
        ; before BASIC variables can be used again, matching the C64 contract.
        STX VARTAB
        STY VARTAB+1
        JSR LNKPRG
        JSR CLEARC
        JMP READY

BASIC_SAVE:
        JSR BASIC_FILE_ARGS
        LDA #<TXTTAB
        LDX VARTAB
        LDY VARTAB+1
        JSR $FFD8
        JMP BASIC_IO_CHECK

BASIC_CLOSE:
        JSR GETBYT
        TXA
        PHA
        JSR BASIC_IO_END
        PLA
        JSR $FFC3
        JMP BASIC_IO_CHECK

; The original core expects channel selection to raise a BASIC error and
; to preserve its current source character and logical file number in A/X.
; The public KERNAL reports errors with Carry and may use these registers.
BASIC_CHKIN:
        PHA
        TXA
        PHA
        JSR $FFC6
        JMP BASIC_CHANNEL_CHECK
BASIC_CHKOUT:
        PHA
        TXA
        PHA
        JSR $FFC9
BASIC_CHANNEL_CHECK:
        JSR BASIC_IO_CHECK
        PLA
        TAX
        PLA
        RTS

BASIC_IO_DEFAULTS:
        LDA #0
        STA $B7
        LDY #0
        LDA #1
        LDX #1
        JMP $FFBA

; LOAD/SAVE/VERIFY [filename[,device[,secondary]]]. The filename descriptor
; is held until all numeric arguments have been evaluated.
BASIC_FILE_ARGS:
        JSR BASIC_IO_DEFAULTS
        JSR CHRGOT
        BEQ BASIC_IO_RETURN
        JSR FRMEVL
        JSR CHKSTR
        LDA FACMO
        PHA
        LDA FACMO+1
        PHA
        JSR CHRGOT
        BEQ BASIC_FILE_NAME
        JSR CHKCOM
        JSR GETBYT
        STX $BA
        BEQ BASIC_FILE_NAME
        JSR CHKCOM
        JSR GETBYT
        STX $B9
        JSR BASIC_IO_END
BASIC_FILE_NAME:
        PLA
        STA FACMO+1
        PLA
        STA FACMO
        JSR FREFAC
        JMP $FFBD

; OPEN logical[,device[,secondary[,filename]]]. Defaults: tape, SA0, no name.
BASIC_OPEN:
        JSR BASIC_IO_DEFAULTS
        JSR GETBYT
        STX $B8
        BEQ BASIC_OPEN_NOW
        JSR CHKCOM
        JSR GETBYT
        STX $BA
        BEQ BASIC_OPEN_NOW
        JSR CHKCOM
        JSR GETBYT
        STX $B9
        BEQ BASIC_OPEN_NOW
        JSR CHKCOM
        JSR FRMEVL
        JSR BASIC_IO_END
        JSR FRESTR
        JSR $FFBD
BASIC_OPEN_NOW:
        LDA $B8
        LDX $BA
        LDY $B9
        JSR $FFBA
        JSR $FFC0
        JMP BASIC_IO_CHECK

BASIC_IO_END:
        JSR CHRGOT
        BEQ BASIC_IO_RETURN
        JMP SNERR
BASIC_IO_CHECK:
        BCC BASIC_IO_RETURN
        JMP BASIC_IO_ERROR
BASIC_IO_RETURN:
        RTS

; BASIC logical files 0..127 use CR; 128..255 append LF after every CR,
; including an explicit CHR$(13) inside a string. This is BASIC's convention;
; the public CHROUT call still emits the single byte it receives.
; Public CHROUT preserves X/Y; restore A=CR after writing the additional LF.
BASIC_CHROUT:
        JSR $FFD2
        CMP #13
        BNE BASIC_IO_RETURN
        BIT CHANNL
        BPL BASIC_IO_RETURN
        LDA #10
        JSR $FFD2
        LDA #13
        RTS

; SYS preserves the C64 A/X/Y/P exchange registers $030C..$030F.
BASIC_SYS:
        JSR FRMNUM
        JSR GETADR
        JSR BASIC_IO_END
        LDA #>(BASIC_SYS_RETURN-1)
        PHA
        LDA #<(BASIC_SYS_RETURN-1)
        PHA
        LDA $030F
        PHA
        LDA $030C
        LDX $030D
        LDY $030E
        PLP
        JMP (POKER)
BASIC_SYS_RETURN:
        PHP
        STA $030C
        STX $030D
        STY $030E
        PLA
        STA $030F
        CLD
        RTS

; Use the core's ordinary error suffix, source-line and stack recovery path.
BASIC_IO_ERROR:
        TAX
        JMP ($0300)             ; File errors obey the same IERROR hook.
BASIC_IO_ERROR_BODY:
        TXA
        PHA
        JSR $FFCC
        LDA #0
        STA CHANNL
        LSR CNTWFL
        JSR CRDO
        JSR OUTQST
        PLA
        CMP #$1C
        BEQ BASIC_VERIFY_ERROR
        CMP #$1D
        BEQ BASIC_LOAD_ERROR
        CMP #1
        BCC BASIC_GENERIC_ERROR
        CMP #10
        BCS BASIC_GENERIC_ERROR
        ASL A
        TAX
        LDA BASIC_IO_MESSAGES-2,X
        LDY BASIC_IO_MESSAGES-1,X
        BNE BASIC_IO_PRINT
BASIC_VERIFY_ERROR:
        LDWDI BASIC_VERIFY_TEXT
        BNE BASIC_IO_PRINT
BASIC_LOAD_ERROR:
        LDWDI BASIC_LOAD_TEXT
        BNE BASIC_IO_PRINT
BASIC_GENERIC_ERROR:
        LDWDI BASIC_IO_TEXT
BASIC_IO_PRINT:
        JSR STROUT
        JMP TYPERR
BASIC_IO_MESSAGES:
        .WORD BASIC_FILES_TEXT,BASIC_OPEN_TEXT,BASIC_NOT_OPEN_TEXT
        .WORD BASIC_NOT_FOUND_TEXT,BASIC_DEVICE_TEXT,BASIC_NOT_INPUT_TEXT
        .WORD BASIC_NOT_OUTPUT_TEXT,BASIC_MISSING_TEXT,BASIC_NUMBER_TEXT
BASIC_NOT_INPUT_TEXT:
        .ASCIIZ "NO INPUT"
BASIC_NOT_OUTPUT_TEXT:
        .ASCIIZ "NO OUTPUT FILE"
BASIC_MISSING_TEXT:
        .ASCIIZ "MISSING FILE"
BASIC_NUMBER_TEXT:
        .ASCIIZ "BAD DEVICE"
BASIC_VERIFY_TEXT:
        .ASCIIZ "VERIFY"
BASIC_LOAD_TEXT:
        .ASCIIZ "LOAD"
BASIC_IO_TEXT:
        .ASCIIZ "I/O"
BASIC_IO_EXT_END:

; Use otherwise unused main-ROM tail space for the first five error strings.
.SEGMENT "CODE"
BASIC_FILES_TEXT:
        .ASCIIZ "TOO MANY FILES"
BASIC_OPEN_TEXT:
        .ASCIIZ "FILE OPEN"
BASIC_NOT_OPEN_TEXT:
        .ASCIIZ "NOT OPEN"
BASIC_NOT_FOUND_TEXT:
        .ASCIIZ "FILE NOT FOUND"
BASIC_DEVICE_TEXT:
        .ASCIIZ "DEVICE NOT PRESENT"
BASIC_IO_MAIN_END:
