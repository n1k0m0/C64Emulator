; Open 1541 host-assisted ROM helper, recovered into editable assembly.
;
; Reconstructed ONLY from the user-provided open-1541-dos.bin in
; kernal-private-eddd-current-v2.zip. No Commodore or external ROM binary
; was used. Initial reconstructed output is byte-identical to that snapshot:
; SHA-256 a512f93736ad31a0dbff01cab1aa39b9571921af77bbc59bd414627b15612522
;
; The C64Emulator host supplies IEC DOS, file handling, job scheduling and
; selected sector operations. The code here supplies bootstrap, GCR helpers,
; and uploaded-drive-code entry contracts. See docs/drive-integration.md.
;
; Zero-page ABI (scratch may be clobbered unless noted):
;   $00-$05 pending jobs/results; $06-$11 track/sector pairs
;   $18/$19 current track/sector; $32/$33 selected track/sector pointer
;   $3A/$3B GCR source, $3C/$3D GCR destination
;   $40-$49 bit-packing scratch; $4B-$4F counters/status
;   $50/$51 payload pointer; $52-$55 cursor/checksum/counters
;   $56 verify flag; $57 compare error ($07); $58-$78 block scratch
;   raw GCR uses $0200-$02FF and $0100-$0144 during reads
;
; SPDX-License-Identifier: Apache-2.0


; Initialize the serial VIA and enter the host DOS idle marker.
.org $C000
RESET:
    sei  ; $C000
    cld  ; $C001
    ldx #$FF  ; $C002
    txs  ; $C004
    lda #$00  ; $C005
    sta $1800  ; $C007
    lda #$0E  ; $C00A
    sta $1802  ; $C00C
    lda #$7F  ; $C00F
    sta $180E  ; $C011
    sta $1C0E  ; $C014
    cli  ; $C017
    jmp DOS_COMMAND_LOOP  ; $C018

; Host-visible idle/handoff marker; high-level IEC DOS runs in the emulator.
.org $C194
DOS_COMMAND_LOOP:
    jmp DOS_COMMAND_LOOP  ; $C194

; Decode five GCR bytes at ($3A) to four bytes at ($3C); C=1 on invalid code.
.org $C200
GCR_DECODE_FOUR:
    ldy #$04  ; $C200
L_C202:
    lda ($3A),y  ; $C202
    sta $0040,y  ; $C204
    dey  ; $C207
    bmi L_C20D  ; $C208
    jmp L_C202  ; $C20A
L_C20D:
    lda #$00  ; $C20D
    sta $47  ; $C20F
    sta $48  ; $C211
L_C213:
    lda #$00  ; $C213
    sta $45  ; $C215
    lda #$05  ; $C217
    sta $46  ; $C219
L_C21B:
    asl $44  ; $C21B
    rol $43  ; $C21D
    rol $42  ; $C21F
    rol $41  ; $C221
    rol $40  ; $C223
    rol $45  ; $C225
    dec $46  ; $C227
    beq L_C22E  ; $C229
    jmp L_C21B  ; $C22B
L_C22E:
    ldy $45  ; $C22E
    lda GCR_DECODE_TABLE,y  ; $C230
    cmp #$FF  ; $C233
    bne L_C23A  ; $C235
    jmp L_C267  ; $C237
L_C23A:
    sta $45  ; $C23A
    lda $47  ; $C23C
    and #$01  ; $C23E
    beq L_C245  ; $C240
    jmp L_C250  ; $C242
L_C245:
    lda $45  ; $C245
    asl a  ; $C247
    asl a  ; $C248
    asl a  ; $C249
    asl a  ; $C24A
    sta $49  ; $C24B
    jmp L_C25A  ; $C24D
L_C250:
    ldy $48  ; $C250
    lda $45  ; $C252
    ora $49  ; $C254
    sta ($3C),y  ; $C256
    inc $48  ; $C258
L_C25A:
    inc $47  ; $C25A
    lda $47  ; $C25C
    cmp #$08  ; $C25E
    beq L_C265  ; $C260
    jmp L_C213  ; $C262
L_C265:
    clc  ; $C265
    rts  ; $C266
L_C267:
    sec  ; $C267
    rts  ; $C268

; Five-bit GCR code to nibble; $FF marks invalid codes.
.org $C269
GCR_DECODE_TABLE:
    .byte $FF, $FF, $FF, $FF, $FF, $FF, $FF, $FF
    .byte $FF, $08, $00, $01, $FF, $0C, $04, $05
    .byte $FF, $FF, $02, $03, $FF, $0F, $06, $07
    .byte $FF, $09, $0A, $0B, $FF, $0D, $0E, $FF

; SO-paced reads from disk VIA $1C01. READ_SYNC_BLOCK also waits for sync.
.org $C2A0
READ_FIVE_GCR_BYTES:
    ldy #$00  ; $C2A0
L_C2A2:
    clv  ; $C2A2
L_C2A3:
    bvs L_C2A8  ; $C2A3
    jmp L_C2A3  ; $C2A5
L_C2A8:
    lda $1C01  ; $C2A8
    sta ($3A),y  ; $C2AB
    iny  ; $C2AD
    cpy #$05  ; $C2AE
    beq L_C2B5  ; $C2B0
    jmp L_C2A2  ; $C2B2
L_C2B5:
    rts  ; $C2B5
READ_SYNC_BLOCK:
    jsr WAIT_FOR_SYNC  ; $C2B6
    bcc L_C2BE  ; $C2B9
    jmp L_C2E2  ; $C2BB
L_C2BE:
    lda $1C00  ; $C2BE
    bmi L_C2C6  ; $C2C1
    jmp L_C2BE  ; $C2C3
L_C2C6:
    ldy #$00  ; $C2C6
    lda $1C01  ; $C2C8
    sta ($3A),y  ; $C2CB
    iny  ; $C2CD
L_C2CE:
    clv  ; $C2CE
L_C2CF:
    bvs L_C2D4  ; $C2CF
    jmp L_C2CF  ; $C2D1
L_C2D4:
    lda $1C01  ; $C2D4
    sta ($3A),y  ; $C2D7
    iny  ; $C2D9
    cpy $4A  ; $C2DA
    beq L_C2E1  ; $C2DC
    jmp L_C2CE  ; $C2DE
L_C2E1:
    clc  ; $C2E1
L_C2E2:
    rts  ; $C2E2

; Find and validate the requested header, with a bounded header retry count.
.org $C300
FIND_SECTOR_HEADER:
    lda #$2A  ; $C300
    sta $4B  ; $C302
L_C304:
    lda #$00  ; $C304
    sta $3A  ; $C306
    sta $3C  ; $C308
    lda #$02  ; $C30A
    sta $3B  ; $C30C
    sta $3D  ; $C30E
    lda #$0A  ; $C310
    sta $4A  ; $C312
    jsr READ_SYNC_BLOCK  ; $C314
    bcc L_C31C  ; $C317
    jmp L_C38A  ; $C319
L_C31C:
    lda #$10  ; $C31C
    sta $3C  ; $C31E
    jsr GCR_DECODE_FOUR  ; $C320
    bcc L_C328  ; $C323
    jmp L_C383  ; $C325
L_C328:
    lda $0210  ; $C328
    cmp #$08  ; $C32B
    beq L_C332  ; $C32D
    jmp L_C383  ; $C32F
L_C332:
    ldy #$00  ; $C332
    lda ($32),y  ; $C334
    cmp $0213  ; $C336
    beq L_C33E  ; $C339
    jmp L_C383  ; $C33B
L_C33E:
    iny  ; $C33E
    lda ($32),y  ; $C33F
    cmp $0212  ; $C341
    beq L_C349  ; $C344
    jmp L_C383  ; $C346
L_C349:
    lda #$05  ; $C349
    sta $3A  ; $C34B
    lda #$14  ; $C34D
    sta $3C  ; $C34F
    jsr GCR_DECODE_FOUR  ; $C351
    bcc L_C359  ; $C354
    jmp L_C383  ; $C356
L_C359:
    lda $0216  ; $C359
    cmp #$0F  ; $C35C
    beq L_C363  ; $C35E
    jmp L_C383  ; $C360
L_C363:
    lda $0217  ; $C363
    cmp #$0F  ; $C366
    beq L_C36D  ; $C368
    jmp L_C383  ; $C36A
L_C36D:
    lda $0212  ; $C36D
    eor $0213  ; $C370
    eor $0214  ; $C373
    eor $0215  ; $C376
    cmp $0211  ; $C379
    beq L_C381  ; $C37C
    jmp L_C383  ; $C37E
L_C381:
    clc  ; $C381
    rts  ; $C382
L_C383:
    dec $4B  ; $C383
    beq L_C38A  ; $C385
    jmp L_C304  ; $C387
L_C38A:
    sec  ; $C38A
    rts  ; $C38B

; Read 325 encoded bytes, split at $0200/$0100; disk SO must be driven.
.org $C400
READ_RAW_DATA_BLOCK:
    jsr WAIT_FOR_SYNC  ; $C400
    bcc L_C408  ; $C403
    jmp L_C441  ; $C405
L_C408:
    lda $1C00  ; $C408
    bmi L_C410  ; $C40B
    jmp L_C408  ; $C40D
L_C410:
    ldy #$00  ; $C410
    lda $1C01  ; $C412
    clv  ; $C415
    sta $0200,y  ; $C416
    iny  ; $C419
L_C41A:
    bvs L_C41F  ; $C41A
    jmp L_C41A  ; $C41C
L_C41F:
    lda $1C01  ; $C41F
    clv  ; $C422
    sta $0200,y  ; $C423
    iny  ; $C426
    beq L_C42C  ; $C427
    jmp L_C41A  ; $C429
L_C42C:
    ldy #$BB  ; $C42C
L_C42E:
    bvs L_C433  ; $C42E
    jmp L_C42E  ; $C430
L_C433:
    lda $1C01  ; $C433
    clv  ; $C436
    sta $0045,y  ; $C437
    iny  ; $C43A
    beq L_C440  ; $C43B
    jmp L_C42E  ; $C43D
L_C440:
    clc  ; $C440
L_C441:
    rts  ; $C441

; Decode a raw data block; destination page $51, verify flag $56, mismatch $57.
.org $C500
DECODE_DATA_BLOCK:
    lda #$58  ; $C500
    sta $3C  ; $C502
    lda #$00  ; $C504
    sta $3D  ; $C506
    sta $50  ; $C508
    sta $54  ; $C50A
    sta $57  ; $C50C
    lda #$CB  ; $C50E
    sta $52  ; $C510
    lda $02FF  ; $C512
    sta $60  ; $C515
    ldy #$00  ; $C517
L_C519:
    lda $0100,y  ; $C519
    sta $0061,y  ; $C51C
    iny  ; $C51F
    cpy #$04  ; $C520
    beq L_C527  ; $C522
    jmp L_C519  ; $C524
L_C527:
    lda #$60  ; $C527
    sta $3A  ; $C529
    lda #$00  ; $C52B
    sta $3B  ; $C52D
    jsr GCR_DECODE_FOUR  ; $C52F
    bcc L_C537  ; $C532
    jmp L_C5CC  ; $C534
L_C537:
    jsr STORE_OR_VERIFY_FOUR  ; $C537
    lda #$04  ; $C53A
    sta $3A  ; $C53C
    lda #$01  ; $C53E
    sta $3B  ; $C540
    lda #$0C  ; $C542
    sta $55  ; $C544
L_C546:
    jsr GCR_DECODE_FOUR  ; $C546
    bcc L_C54E  ; $C549
    jmp L_C5CC  ; $C54B
L_C54E:
    jsr STORE_OR_VERIFY_FOUR  ; $C54E
    jsr ADVANCE_GCR_POINTER  ; $C551
    dec $55  ; $C554
    beq L_C55B  ; $C556
    jmp L_C546  ; $C558
L_C55B:
    jsr GCR_DECODE_FOUR  ; $C55B
    bcc L_C563  ; $C55E
    jmp L_C5CC  ; $C560
L_C563:
    ldy $52  ; $C563
    lda $58  ; $C565
    jsr STORE_OR_VERIFY_BYTE  ; $C567
    lda $59  ; $C56A
    sta $53  ; $C56C
    lda $5A  ; $C56E
    ora $5B  ; $C570
    beq L_C577  ; $C572
    jmp L_C5CC  ; $C574
L_C577:
    lda #$00  ; $C577
    sta $3A  ; $C579
    sta $52  ; $C57B
    lda #$02  ; $C57D
    sta $3B  ; $C57F
    jsr GCR_DECODE_FOUR  ; $C581
    bcc L_C589  ; $C584
    jmp L_C5CC  ; $C586
L_C589:
    lda $58  ; $C589
    cmp #$07  ; $C58B
    beq L_C592  ; $C58D
    jmp L_C5CC  ; $C58F
L_C592:
    ldx #$01  ; $C592
L_C594:
    ldy $52  ; $C594
    lda $58,x  ; $C596
    jsr STORE_OR_VERIFY_BYTE  ; $C598
    inc $52  ; $C59B
    inx  ; $C59D
    cpx #$04  ; $C59E
    beq L_C5A5  ; $C5A0
    jmp L_C594  ; $C5A2
L_C5A5:
    jsr ADVANCE_GCR_POINTER  ; $C5A5
    lda #$32  ; $C5A8
    sta $55  ; $C5AA
L_C5AC:
    jsr GCR_DECODE_FOUR  ; $C5AC
    bcc L_C5B4  ; $C5AF
    jmp L_C5CC  ; $C5B1
L_C5B4:
    jsr STORE_OR_VERIFY_FOUR  ; $C5B4
    jsr ADVANCE_GCR_POINTER  ; $C5B7
    dec $55  ; $C5BA
    beq L_C5C1  ; $C5BC
    jmp L_C5AC  ; $C5BE
L_C5C1:
    lda $54  ; $C5C1
    cmp $53  ; $C5C3
    beq L_C5CA  ; $C5C5
    jmp L_C5CC  ; $C5C7
L_C5CA:
    clc  ; $C5CA
    rts  ; $C5CB
L_C5CC:
    sec  ; $C5CC
    rts  ; $C5CD
STORE_OR_VERIFY_FOUR:
    ldx #$00  ; $C5CE
L_C5D0:
    ldy $52  ; $C5D0
    lda $58,x  ; $C5D2
    jsr STORE_OR_VERIFY_BYTE  ; $C5D4
    inc $52  ; $C5D7
    inx  ; $C5D9
    cpx #$04  ; $C5DA
    beq L_C5E1  ; $C5DC
    jmp L_C5D0  ; $C5DE
L_C5E1:
    rts  ; $C5E1
ADVANCE_GCR_POINTER:
    clc  ; $C5E2
    lda $3A  ; $C5E3
    adc #$05  ; $C5E5
    sta $3A  ; $C5E7
    bcs L_C5EE  ; $C5E9
    jmp L_C5F0  ; $C5EB
L_C5EE:
    inc $3B  ; $C5EE
L_C5F0:
    rts  ; $C5F0
STORE_OR_VERIFY_BYTE:
    sta $65  ; $C5F1
    eor $54  ; $C5F3
    sta $54  ; $C5F5
    lda $56  ; $C5F7
    beq L_C5FE  ; $C5F9
    jmp L_C603  ; $C5FB
L_C5FE:
    lda $65  ; $C5FE
    sta ($50),y  ; $C600
    rts  ; $C602
L_C603:
    lda ($50),y  ; $C603
    cmp $65  ; $C605
    bne L_C60C  ; $C607
    jmp L_C610  ; $C609
L_C60C:
    lda #$07  ; $C60C
    sta $57  ; $C60E
L_C610:
    rts  ; $C610

; Locate matching GCR header and read/decode data using the track/sector pointer.
.org $C700
READ_REQUESTED_SECTOR:
    ldy #$00  ; $C700
    lda ($32),y  ; $C702
    sta $77  ; $C704
    iny  ; $C706
    lda ($32),y  ; $C707
    sta $78  ; $C709
    lda $78  ; $C70B
    lsr a  ; $C70D
    lsr a  ; $C70E
    lsr a  ; $C70F
    lsr a  ; $C710
    tay  ; $C711
    lda GCR_ENCODE_TABLE,y  ; $C712
    sta $70  ; $C715
    lda $78  ; $C717
    and #$0F  ; $C719
    tay  ; $C71B
    lda GCR_ENCODE_TABLE,y  ; $C71C
    sta $71  ; $C71F
    lda $77  ; $C721
    lsr a  ; $C723
    lsr a  ; $C724
    lsr a  ; $C725
    lsr a  ; $C726
    tay  ; $C727
    lda GCR_ENCODE_TABLE,y  ; $C728
    sta $72  ; $C72B
    lda $77  ; $C72D
    and #$0F  ; $C72F
    tay  ; $C731
    lda GCR_ENCODE_TABLE,y  ; $C732
    sta $73  ; $C735
    lda $70  ; $C737
    lsr a  ; $C739
    sta $74  ; $C73A
    lda #$00  ; $C73C
    ror a  ; $C73E
    sta $75  ; $C73F
    lda $71  ; $C741
    asl a  ; $C743
    asl a  ; $C744
    ora $75  ; $C745
    sta $75  ; $C747
    lda $72  ; $C749
    lsr a  ; $C74B
    lsr a  ; $C74C
    lsr a  ; $C74D
    ora $75  ; $C74E
    sta $75  ; $C750
    lda $72  ; $C752
    asl a  ; $C754
    asl a  ; $C755
    asl a  ; $C756
    asl a  ; $C757
    asl a  ; $C758
    ora $73  ; $C759
    sta $76  ; $C75B
    lda #$40  ; $C75D
    sta $4B  ; $C75F
L_C761:
    jsr READ_HEADER_GCR  ; $C761
    bcc L_C769  ; $C764
    jmp L_C815  ; $C766
L_C769:
    lda $58  ; $C769
    cmp #$52  ; $C76B
    beq L_C772  ; $C76D
    jmp L_C80E  ; $C76F
L_C772:
    lda $5A  ; $C772
    and #$0F  ; $C774
    cmp $74  ; $C776
    beq L_C77D  ; $C778
    jmp L_C80E  ; $C77A
L_C77D:
    lda $5B  ; $C77D
    cmp $75  ; $C77F
    beq L_C786  ; $C781
    jmp L_C80E  ; $C783
L_C786:
    lda $5C  ; $C786
    cmp $76  ; $C788
    beq L_C78F  ; $C78A
    jmp L_C80E  ; $C78C
L_C78F:
    lda #$00  ; $C78F
    sta $3A  ; $C791
    lda $51  ; $C793
    sta $3B  ; $C795
    jsr READ_RAW_DATA_BLOCK  ; $C797
    bcc L_C79F  ; $C79A
    jmp L_C815  ; $C79C
L_C79F:
    lda #$58  ; $C79F
    sta $3A  ; $C7A1
    lda #$68  ; $C7A3
    sta $3C  ; $C7A5
    lda #$00  ; $C7A7
    sta $3B  ; $C7A9
    sta $3D  ; $C7AB
    jsr GCR_DECODE_FOUR  ; $C7AD
    bcc L_C7B5  ; $C7B0
    jmp L_C80E  ; $C7B2
L_C7B5:
    lda $68  ; $C7B5
    cmp #$08  ; $C7B7
    beq L_C7BE  ; $C7B9
    jmp L_C80E  ; $C7BB
L_C7BE:
    ldy #$00  ; $C7BE
    lda ($32),y  ; $C7C0
    cmp $6B  ; $C7C2
    beq L_C7C9  ; $C7C4
    jmp L_C80E  ; $C7C6
L_C7C9:
    iny  ; $C7C9
    lda ($32),y  ; $C7CA
    cmp $6A  ; $C7CC
    beq L_C7D3  ; $C7CE
    jmp L_C80E  ; $C7D0
L_C7D3:
    lda #$5D  ; $C7D3
    sta $3A  ; $C7D5
    lda #$6C  ; $C7D7
    sta $3C  ; $C7D9
    jsr GCR_DECODE_FOUR  ; $C7DB
    bcc L_C7E3  ; $C7DE
    jmp L_C80E  ; $C7E0
L_C7E3:
    lda $6E  ; $C7E3
    cmp #$0F  ; $C7E5
    beq L_C7EC  ; $C7E7
    jmp L_C80E  ; $C7E9
L_C7EC:
    lda $6F  ; $C7EC
    cmp #$0F  ; $C7EE
    beq L_C7F5  ; $C7F0
    jmp L_C80E  ; $C7F2
L_C7F5:
    lda $6A  ; $C7F5
    eor $6B  ; $C7F7
    eor $6C  ; $C7F9
    eor $6D  ; $C7FB
    cmp $69  ; $C7FD
    beq L_C804  ; $C7FF
    jmp L_C80E  ; $C801
L_C804:
    jsr DECODE_DATA_BLOCK  ; $C804
    bcc L_C80C  ; $C807
    jmp L_C80E  ; $C809
L_C80C:
    clc  ; $C80C
    rts  ; $C80D
L_C80E:
    dec $4B  ; $C80E
    beq L_C815  ; $C810
    jmp L_C761  ; $C812
L_C815:
    sec  ; $C815
    rts  ; $C816

; Nibble to five-bit GCR code, as used by the disk recording format.
.org $C817
GCR_ENCODE_TABLE:
    .byte $0A, $0B, $12, $13, $0E, $0F, $16, $17
    .byte $09, $19, $1A, $1B, $0D, $1D, $1E, $15

; Wait for active-low sync on disk VIA PB7 with a finite polling counter.
.org $C900
WAIT_FOR_SYNC:
    lda #$40  ; $C900
    sta $4D  ; $C902
    lda #$00  ; $C904
    sta $4C  ; $C906
L_C908:
    lda $1C00  ; $C908
    bmi L_C910  ; $C90B
    jmp L_C920  ; $C90D
L_C910:
    dec $4C  ; $C910
    beq L_C917  ; $C912
    jmp L_C908  ; $C914
L_C917:
    dec $4D  ; $C917
    beq L_C91E  ; $C919
    jmp L_C908  ; $C91B
L_C91E:
    sec  ; $C91E
    rts  ; $C91F
L_C920:
    clc  ; $C920
    rts  ; $C921

; Read ten header bytes into $58-$61 after sync.
.org $C930
READ_HEADER_GCR:
    jsr WAIT_FOR_SYNC  ; $C930
    bcc L_C938  ; $C933
    jmp L_C95D  ; $C935
L_C938:
    lda $1C00  ; $C938
    bmi L_C940  ; $C93B
    jmp L_C938  ; $C93D
L_C940:
    ldx #$00  ; $C940
    lda $1C01  ; $C942
    clv  ; $C945
    sta $58,x  ; $C946
    inx  ; $C948
L_C949:
    bvs L_C94E  ; $C949
    jmp L_C949  ; $C94B
L_C94E:
    lda $1C01  ; $C94E
    clv  ; $C951
    sta $58,x  ; $C952
    inx  ; $C954
    cpx #$0A  ; $C955
    beq L_C95C  ; $C957
    jmp L_C949  ; $C959
L_C95C:
    clc  ; $C95C
L_C95D:
    rts  ; $C95D

; Encode four bytes at ($3A) to five GCR bytes at ($3C).
.org $CA00
GCR_ENCODE_FOUR:
    lda #$00  ; $CA00
    ldx #$04  ; $CA02
L_CA04:
    sta $40,x  ; $CA04
    dex  ; $CA06
    bmi L_CA0C  ; $CA07
    jmp L_CA04  ; $CA09
L_CA0C:
    ldy #$00  ; $CA0C
L_CA0E:
    lda ($3A),y  ; $CA0E
    sta $45  ; $CA10
    lsr a  ; $CA12
    lsr a  ; $CA13
    lsr a  ; $CA14
    lsr a  ; $CA15
    tax  ; $CA16
    lda GCR_ENCODE_TABLE,x  ; $CA17
    jsr APPEND_GCR_CODE  ; $CA1A
    lda $45  ; $CA1D
    and #$0F  ; $CA1F
    tax  ; $CA21
    lda GCR_ENCODE_TABLE,x  ; $CA22
    jsr APPEND_GCR_CODE  ; $CA25
    iny  ; $CA28
    cpy #$04  ; $CA29
    beq L_CA30  ; $CA2B
    jmp L_CA0E  ; $CA2D
L_CA30:
    ldy #$00  ; $CA30
    ldx #$00  ; $CA32
L_CA34:
    lda $40,x  ; $CA34
    sta ($3C),y  ; $CA36
    iny  ; $CA38
    inx  ; $CA39
    cpx #$05  ; $CA3A
    beq L_CA41  ; $CA3C
    jmp L_CA34  ; $CA3E
L_CA41:
    rts  ; $CA41
APPEND_GCR_CODE:
    sta $46  ; $CA42
    ldx #$05  ; $CA44
L_CA46:
    asl $44  ; $CA46
    rol $43  ; $CA48
    rol $42  ; $CA4A
    rol $41  ; $CA4C
    rol $40  ; $CA4E
    dex  ; $CA50
    beq L_CA56  ; $CA51
    jmp L_CA46  ; $CA53
L_CA56:
    lda $44  ; $CA56
    ora $46  ; $CA58
    sta $44  ; $CA5A
    rts  ; $CA5C

; Encode marker $07, 256 payload bytes, XOR checksum, and two zero pad bytes.
.org $CB00
ENCODE_DATA_BLOCK:
    lda #$60  ; $CB00
    sta $3A  ; $CB02
    lda #$00  ; $CB04
    sta $3B  ; $CB06
    sta $3C  ; $CB08
    sta $64  ; $CB0A
    sta $65  ; $CB0C
    sta $66  ; $CB0E
    lda #$02  ; $CB10
    sta $3D  ; $CB12
    lda #$41  ; $CB14
    sta $67  ; $CB16
L_CB18:
    ldx #$00  ; $CB18
L_CB1A:
    jsr NEXT_DATA_BLOCK_BYTE  ; $CB1A
    sta $60,x  ; $CB1D
    inx  ; $CB1F
    cpx #$04  ; $CB20
    beq L_CB27  ; $CB22
    jmp L_CB1A  ; $CB24
L_CB27:
    jsr GCR_ENCODE_FOUR  ; $CB27
    clc  ; $CB2A
    lda $3C  ; $CB2B
    adc #$05  ; $CB2D
    sta $3C  ; $CB2F
    bcs L_CB36  ; $CB31
    jmp L_CB38  ; $CB33
L_CB36:
    inc $3D  ; $CB36
L_CB38:
    dec $67  ; $CB38
    bne L_CB3F  ; $CB3A
    jmp L_CB53  ; $CB3C
L_CB3F:
    lda $67  ; $CB3F
    cmp #$0E  ; $CB41
    beq L_CB48  ; $CB43
    jmp L_CB18  ; $CB45
L_CB48:
    lda #$00  ; $CB48
    sta $3C  ; $CB4A
    lda #$01  ; $CB4C
    sta $3D  ; $CB4E
    jmp L_CB18  ; $CB50
L_CB53:
    rts  ; $CB53
NEXT_DATA_BLOCK_BYTE:
    lda $65  ; $CB54
    beq L_CB5B  ; $CB56
    jmp L_CB70  ; $CB58
L_CB5B:
    lda $64  ; $CB5B
    bne L_CB62  ; $CB5D
    jmp L_CB6B  ; $CB5F
L_CB62:
    sec  ; $CB62
    sbc #$01  ; $CB63
    tay  ; $CB65
    lda ($50),y  ; $CB66
    jmp L_CB87  ; $CB68
L_CB6B:
    lda #$07  ; $CB6B
    jmp L_CB94  ; $CB6D
L_CB70:
    lda $64  ; $CB70
    bne L_CB77  ; $CB72
    jmp L_CB83  ; $CB74
L_CB77:
    cmp #$01  ; $CB77
    bne L_CB7E  ; $CB79
    jmp L_CB92  ; $CB7B
L_CB7E:
    lda #$00  ; $CB7E
    jmp L_CB94  ; $CB80
L_CB83:
    ldy #$FF  ; $CB83
    lda ($50),y  ; $CB85
L_CB87:
    sta $68  ; $CB87
    eor $66  ; $CB89
    sta $66  ; $CB8B
    lda $68  ; $CB8D
    jmp L_CB94  ; $CB8F
L_CB92:
    lda $66  ; $CB92
L_CB94:
    inc $64  ; $CB94
    beq L_CB9B  ; $CB96
    jmp L_CB9D  ; $CB98
L_CB9B:
    inc $65  ; $CB9B
L_CB9D:
    rts  ; $CB9D

; Find matching sector and write sync + 325 GCR bytes via disk VIA.
.org $CC00
WRITE_ENCODED_DATA_BLOCK:
    jsr FIND_WRITE_HEADER  ; $CC00
    bcc L_CC08  ; $CC03
    jmp L_CC41  ; $CC05
L_CC08:
    lda #$60  ; $CC08
    sta $4E  ; $CC0A
L_CC0C:
    jsr READ_HEADER_GCR  ; $CC0C
    bcc L_CC14  ; $CC0F
    jmp L_CC41  ; $CC11
L_CC14:
    ldx #$04  ; $CC14
L_CC16:
    lda $58,x  ; $CC16
    cmp $74,x  ; $CC18
    beq L_CC1F  ; $CC1A
    jmp L_CC38  ; $CC1C
L_CC1F:
    dex  ; $CC1F
    bmi L_CC25  ; $CC20
    jmp L_CC16  ; $CC22
L_CC25:
    ldy #$FF  ; $CC25
L_CC27:
    lda $1C00  ; $CC27
    bmi L_CC2F  ; $CC2A
    jmp L_CC43  ; $CC2C
L_CC2F:
    dey  ; $CC2F
    beq L_CC35  ; $CC30
    jmp L_CC27  ; $CC32
L_CC35:
    jmp L_CC41  ; $CC35
L_CC38:
    dec $4E  ; $CC38
    beq L_CC3F  ; $CC3A
    jmp L_CC0C  ; $CC3C
L_CC3F:
    sec  ; $CC3F
    rts  ; $CC40
L_CC41:
    sec  ; $CC41
    rts  ; $CC42
L_CC43:
    lda #$FF  ; $CC43
    sta $1C03  ; $CC45
    sta $1C01  ; $CC48
    lda #$80  ; $CC4B
    sta $1C0C  ; $CC4D
    ldx #$04  ; $CC50
    clv  ; $CC52
L_CC53:
    bvs L_CC58  ; $CC53
    jmp L_CC53  ; $CC55
L_CC58:
    lda #$FF  ; $CC58
    sta $1C01  ; $CC5A
    clv  ; $CC5D
    dex  ; $CC5E
    beq L_CC64  ; $CC5F
    jmp L_CC53  ; $CC61
L_CC64:
    ldy #$00  ; $CC64
L_CC66:
    bvs L_CC6B  ; $CC66
    jmp L_CC66  ; $CC68
L_CC6B:
    lda $0200,y  ; $CC6B
    sta $1C01  ; $CC6E
    clv  ; $CC71
    iny  ; $CC72
    cpy #$FF  ; $CC73
    beq L_CC7A  ; $CC75
    jmp L_CC66  ; $CC77
L_CC7A:
    ldy #$00  ; $CC7A
L_CC7C:
    bvs L_CC81  ; $CC7C
    jmp L_CC7C  ; $CC7E
L_CC81:
    lda $0100,y  ; $CC81
    sta $1C01  ; $CC84
    clv  ; $CC87
    iny  ; $CC88
    cpy #$46  ; $CC89
    beq L_CC90  ; $CC8B
    jmp L_CC7C  ; $CC8D
L_CC90:
    bvs L_CC95  ; $CC90
    jmp L_CC90  ; $CC92
L_CC95:
    clv  ; $CC95
L_CC96:
    bvs L_CC9B  ; $CC96
    jmp L_CC96  ; $CC98
L_CC9B:
    lda #$A0  ; $CC9B
    sta $1C0C  ; $CC9D
    clc  ; $CCA0
    rts  ; $CCA1

; Validate requested header and retain its first five encoded bytes.
.org $CD00
FIND_WRITE_HEADER:
    lda #$2A  ; $CD00
    sta $4B  ; $CD02
L_CD04:
    jsr READ_HEADER_GCR  ; $CD04
    bcc L_CD0C  ; $CD07
    jmp L_CD86  ; $CD09
L_CD0C:
    lda #$58  ; $CD0C
    sta $3A  ; $CD0E
    lda #$68  ; $CD10
    sta $3C  ; $CD12
    lda #$00  ; $CD14
    sta $3B  ; $CD16
    sta $3D  ; $CD18
    jsr GCR_DECODE_FOUR  ; $CD1A
    bcc L_CD22  ; $CD1D
    jmp L_CD7F  ; $CD1F
L_CD22:
    lda $68  ; $CD22
    cmp #$08  ; $CD24
    beq L_CD2B  ; $CD26
    jmp L_CD7F  ; $CD28
L_CD2B:
    ldy #$00  ; $CD2B
    lda ($32),y  ; $CD2D
    cmp $6B  ; $CD2F
    beq L_CD36  ; $CD31
    jmp L_CD7F  ; $CD33
L_CD36:
    iny  ; $CD36
    lda ($32),y  ; $CD37
    cmp $6A  ; $CD39
    beq L_CD40  ; $CD3B
    jmp L_CD7F  ; $CD3D
L_CD40:
    lda #$5D  ; $CD40
    sta $3A  ; $CD42
    lda #$6C  ; $CD44
    sta $3C  ; $CD46
    jsr GCR_DECODE_FOUR  ; $CD48
    bcc L_CD50  ; $CD4B
    jmp L_CD7F  ; $CD4D
L_CD50:
    lda $6E  ; $CD50
    cmp #$0F  ; $CD52
    beq L_CD59  ; $CD54
    jmp L_CD7F  ; $CD56
L_CD59:
    lda $6F  ; $CD59
    cmp #$0F  ; $CD5B
    beq L_CD62  ; $CD5D
    jmp L_CD7F  ; $CD5F
L_CD62:
    lda $6A  ; $CD62
    eor $6B  ; $CD64
    eor $6C  ; $CD66
    eor $6D  ; $CD68
    cmp $69  ; $CD6A
    beq L_CD71  ; $CD6C
    jmp L_CD7F  ; $CD6E
L_CD71:
    ldx #$04  ; $CD71
L_CD73:
    lda $58,x  ; $CD73
    sta $74,x  ; $CD75
    dex  ; $CD77
    bmi L_CD7D  ; $CD78
    jmp L_CD73  ; $CD7A
L_CD7D:
    clc  ; $CD7D
    rts  ; $CD7E
L_CD7F:
    dec $4B  ; $CD7F
    beq L_CD86  ; $CD81
    jmp L_CD04  ; $CD83
L_CD86:
    sec  ; $CD86
    rts  ; $CD87

; Require a stable writable sensor level; C=1 on timeout/protection.
.org $CE00
CHECK_WRITE_PROTECTION:
    lda #$00  ; $CE00
    sta $69  ; $CE02
    sta $6A  ; $CE04
    sta $6B  ; $CE06
    sta $6C  ; $CE08
    sta $6D  ; $CE0A
L_CE0C:
    inc $6B  ; $CE0C
    beq L_CE13  ; $CE0E
    jmp L_CE25  ; $CE10
L_CE13:
    inc $6C  ; $CE13
    beq L_CE1A  ; $CE15
    jmp L_CE25  ; $CE17
L_CE1A:
    inc $6D  ; $CE1A
    lda $6D  ; $CE1C
    cmp #$01  ; $CE1E
    bcc L_CE25  ; $CE20
    jmp L_CE4C  ; $CE22
L_CE25:
    lda $1C00  ; $CE25
    and #$10  ; $CE28
    bne L_CE2F  ; $CE2A
    jmp L_CE43  ; $CE2C
L_CE2F:
    inc $69  ; $CE2F
    beq L_CE36  ; $CE31
    jmp L_CE0C  ; $CE33
L_CE36:
    inc $6A  ; $CE36
    lda $6A  ; $CE38
    cmp #$30  ; $CE3A
    beq L_CE41  ; $CE3C
    jmp L_CE0C  ; $CE3E
L_CE41:
    clc  ; $CE41
    rts  ; $CE42
L_CE43:
    lda #$00  ; $CE43
    sta $69  ; $CE45
    sta $6A  ; $CE47
    jmp L_CE0C  ; $CE49
L_CE4C:
    sec  ; $CE4C
    rts  ; $CE4D

; Host fallback for uploaded loaders which return to the ROM command loop.
.org $E654
DOS_IDLE_RETURN:
    jmp DOS_COMMAND_LOOP  ; $E654

; Process a pending job; called with the host synthetic IRQ and JSR frame.
.org $F2B0
DOS_JOB_DISPATCH:
    ldx #$05  ; $F2B0
L_F2B2:
    lda $00,x  ; $F2B2
    and #$F0  ; $F2B4
    cmp #$80  ; $F2B6
    bne L_F2BD  ; $F2B8
    jmp READ_OR_VERIFY_JOB  ; $F2BA
L_F2BD:
    cmp #$90  ; $F2BD
    bne L_F2C4  ; $F2BF
    jmp WRITE_JOB  ; $F2C1
L_F2C4:
    cmp #$A0  ; $F2C4
    bne L_F2CB  ; $F2C6
    jmp READ_OR_VERIFY_JOB  ; $F2C8
L_F2CB:
    cmp #$B0  ; $F2CB
    bne L_F2D2  ; $F2CD
    jmp SEEK_JOB  ; $F2CF
L_F2D2:
    cmp #$C0  ; $F2D2
    bne L_F2D9  ; $F2D4
    jmp RESTORE_HEAD_JOB  ; $F2D6
L_F2D9:
    cmp #$D0  ; $F2D9
    bne L_F2E0  ; $F2DB
    jmp EXECUTE_BUFFER_JOB  ; $F2DD
L_F2E0:
    cmp #$E0  ; $F2E0
    bne L_F2E7  ; $F2E2
    jmp EXECUTE_BUFFER_JOB  ; $F2E4
L_F2E7:
    dex  ; $F2E7
    bmi L_F2ED  ; $F2E8
    jmp L_F2B2  ; $F2EA
L_F2ED:
    rts  ; $F2ED
EXECUTE_BUFFER_JOB:
    lda $00,x  ; $F2EE
    and #$01  ; $F2F0
    beq L_F2F7  ; $F2F2
    jmp JOB_WRONG_DRIVE  ; $F2F4
L_F2F7:
    cpx #$05  ; $F2F7
    bne L_F2FE  ; $F2F9
    jmp JOB_INVALID_BUFFER  ; $F2FB
L_F2FE:
    txa  ; $F2FE
    pha  ; $F2FF
    lda JOB_TRACK_SECTOR_OFFSETS,x  ; $F300
    sta $32  ; $F303
    lda #$00  ; $F305
    sta $33  ; $F307
    jsr EXECUTE_BUFFER  ; $F309
    pla  ; $F30C
    tax  ; $F30D
    lda #$01  ; $F30E
    sta $00,x  ; $F310
    rts  ; $F312
JOB_WRONG_DRIVE:
    lda #$0F  ; $F313
    sta $00,x  ; $F315
    rts  ; $F317
JOB_INVALID_BUFFER:
    lda #$02  ; $F318
    sta $00,x  ; $F31A
    rts  ; $F31C
SEEK_JOB:
    lda $00,x  ; $F31D
    and #$01  ; $F31F
    beq L_F326  ; $F321
    jmp JOB_WRONG_DRIVE  ; $F323
L_F326:
    jsr SEEK_REQUESTED_TRACK  ; $F326
    bcc L_F32E  ; $F329
    jmp L_F333  ; $F32B
L_F32E:
    lda #$01  ; $F32E
    sta $00,x  ; $F330
    rts  ; $F332
L_F333:
    lda #$02  ; $F333
    sta $00,x  ; $F335
    rts  ; $F337
SEEK_REQUESTED_TRACK:
    txa  ; $F338
    pha  ; $F339
    lda JOB_TRACK_SECTOR_OFFSETS,x  ; $F33A
    sta $32  ; $F33D
    lda #$00  ; $F33F
    sta $33  ; $F341
    tay  ; $F343
    lda ($32),y  ; $F344
    bne L_F34B  ; $F346
    jmp L_F3E0  ; $F348
L_F34B:
    cmp #$2B  ; $F34B
    bcc L_F352  ; $F34D
    jmp L_F3E0  ; $F34F
L_F352:
    tax  ; $F352
    iny  ; $F353
    lda ($32),y  ; $F354
    cmp SECTORS_PER_TRACK,x  ; $F356
    bcc L_F35E  ; $F359
    jmp L_F3E0  ; $F35B
L_F35E:
    lda $34  ; $F35E
    pha  ; $F360
    lda $35  ; $F361
    pha  ; $F363
    lda $1C02  ; $F364
    pha  ; $F367
    ora #$07  ; $F368
    sta $1C02  ; $F36A
    lda $1C00  ; $F36D
    pha  ; $F370
    and #$FC  ; $F371
    ora #$04  ; $F373
    sta $34  ; $F375
    lda $1C00  ; $F377
    and #$03  ; $F37A
    sta $35  ; $F37C
    ora $34  ; $F37E
    sta $1C00  ; $F380
    ldx #$54  ; $F383
L_F385:
    dec $35  ; $F385
    lda $35  ; $F387
    and #$03  ; $F389
    sta $35  ; $F38B
    ora $34  ; $F38D
    sta $1C00  ; $F38F
    dex  ; $F392
    beq L_F398  ; $F393
    jmp L_F385  ; $F395
L_F398:
    ldy #$00  ; $F398
    lda ($32),y  ; $F39A
    tax  ; $F39C
    dex  ; $F39D
    bne L_F3A3  ; $F39E
    jmp L_F3C3  ; $F3A0
L_F3A3:
    inc $35  ; $F3A3
    lda $35  ; $F3A5
    and #$03  ; $F3A7
    sta $35  ; $F3A9
    ora $34  ; $F3AB
    sta $1C00  ; $F3AD
    inc $35  ; $F3B0
    lda $35  ; $F3B2
    and #$03  ; $F3B4
    sta $35  ; $F3B6
    ora $34  ; $F3B8
    sta $1C00  ; $F3BA
    dex  ; $F3BD
    beq L_F3C3  ; $F3BE
    jmp L_F3A3  ; $F3C0
L_F3C3:
    pla  ; $F3C3
    sta $1C00  ; $F3C4
    pla  ; $F3C7
    sta $1C02  ; $F3C8
    pla  ; $F3CB
    sta $35  ; $F3CC
    pla  ; $F3CE
    sta $34  ; $F3CF
    pla  ; $F3D1
    tax  ; $F3D2
    ldy #$00  ; $F3D3
    lda ($32),y  ; $F3D5
    sta $18  ; $F3D7
    iny  ; $F3D9
    lda ($32),y  ; $F3DA
    sta $19  ; $F3DC
    clc  ; $F3DE
    rts  ; $F3DF
L_F3E0:
    pla  ; $F3E0
    tax  ; $F3E1
    sec  ; $F3E2
    rts  ; $F3E3
RESTORE_HEAD_JOB:
    lda $00,x  ; $F3E4
    and #$01  ; $F3E6
    beq L_F3ED  ; $F3E8
    jmp JOB_WRONG_DRIVE  ; $F3EA
L_F3ED:
    txa  ; $F3ED
    pha  ; $F3EE
    lda $34  ; $F3EF
    pha  ; $F3F1
    lda $1C02  ; $F3F2
    pha  ; $F3F5
    ora #$07  ; $F3F6
    sta $1C02  ; $F3F8
    lda $1C00  ; $F3FB
    pha  ; $F3FE
    and #$FC  ; $F3FF
    ora #$04  ; $F401
    sta $34  ; $F403
    lda $1C00  ; $F405
    and #$03  ; $F408
    tay  ; $F40A
    tya  ; $F40B
    ora $34  ; $F40C
    sta $1C00  ; $F40E
    ldx #$54  ; $F411
L_F413:
    dey  ; $F413
    tya  ; $F414
    and #$03  ; $F415
    tay  ; $F417
    ora $34  ; $F418
    sta $1C00  ; $F41A
    dex  ; $F41D
    beq L_F423  ; $F41E
    jmp L_F413  ; $F420
L_F423:
    pla  ; $F423
    sta $1C00  ; $F424
    pla  ; $F427
    sta $1C02  ; $F428
    pla  ; $F42B
    sta $34  ; $F42C
    pla  ; $F42E
    tax  ; $F42F
    lda #$01  ; $F430
    sta $00,x  ; $F432
    rts  ; $F434
EXECUTE_BUFFER:
    lda EXECUTE_RETURN_HIGH,x  ; $F435
    pha  ; $F438
    lda #$FF  ; $F439
    pha  ; $F43B
    lda #$00  ; $F43C
    rts  ; $F43E

; Low-byte track/sector pointers for queue slots 0..5.
.org $F43F
JOB_TRACK_SECTOR_OFFSETS:
    .byte $06, $08, $0A, $0C, $0E, $10

; RTS target-minus-one high bytes for RAM buffers $0300..$0700.
.org $F445
EXECUTE_RETURN_HIGH:
    .byte $02, $03, $04, $05, $06

; Track index 0..42, used for range validation.
.org $F44A
SECTORS_PER_TRACK:
    .byte $00, $15, $15, $15, $15, $15, $15, $15
    .byte $15, $15, $15, $15, $15, $15, $15, $15
    .byte $15, $15, $13, $13, $13, $13, $13, $13
    .byte $13, $12, $12, $12, $12, $12, $12, $11
    .byte $11, $11, $11, $11, $11, $11, $11, $11
    .byte $11, $11, $11

; No NMI service beyond returning.
.org $F475
NMI_RETURN:
    rti  ; $F475

; READ/VERIFY job path; slot in X; result is stored back to $00,X.
.org $F500
READ_OR_VERIFY_JOB:
    lda $00,x  ; $F500
    and #$F0  ; $F502
    cmp #$A0  ; $F504
    bne L_F50B  ; $F506
    jmp L_F512  ; $F508
L_F50B:
    lda #$00  ; $F50B
    sta $56  ; $F50D
    jmp L_F516  ; $F50F
L_F512:
    lda #$01  ; $F512
    sta $56  ; $F514
L_F516:
    lda $00,x  ; $F516
    and #$01  ; $F518
    beq L_F51F  ; $F51A
    jmp L_F577  ; $F51C
L_F51F:
    cpx #$05  ; $F51F
    bne L_F526  ; $F521
    jmp L_F57A  ; $F523
L_F526:
    jsr SEEK_REQUESTED_TRACK  ; $F526
    bcc L_F52E  ; $F529
    jmp L_F572  ; $F52B
L_F52E:
    txa  ; $F52E
    pha  ; $F52F
    lda JOB_TRACK_SECTOR_OFFSETS,x  ; $F530
    sta $32  ; $F533
    lda #$00  ; $F535
    sta $33  ; $F537
    txa  ; $F539
    clc  ; $F53A
    adc #$03  ; $F53B
    sta $51  ; $F53D
    lda $1C02  ; $F53F
    pha  ; $F542
    ora #$07  ; $F543
    sta $1C02  ; $F545
    lda $1C00  ; $F548
    pha  ; $F54B
    ora #$04  ; $F54C
    sta $1C00  ; $F54E
    jsr READ_REQUESTED_SECTOR  ; $F551
    pla  ; $F554
    sta $1C00  ; $F555
    pla  ; $F558
    sta $1C02  ; $F559
    pla  ; $F55C
    tax  ; $F55D
    bcc L_F563  ; $F55E
    jmp L_F572  ; $F560
L_F563:
    lda $57  ; $F563
    beq L_F56A  ; $F565
    jmp L_F56F  ; $F567
L_F56A:
    lda #$01  ; $F56A
    sta $00,x  ; $F56C
    rts  ; $F56E
L_F56F:
    sta $00,x  ; $F56F
    rts  ; $F571
L_F572:
    lda #$02  ; $F572
    sta $00,x  ; $F574
    rts  ; $F576
L_F577:
    jmp JOB_WRONG_DRIVE  ; $F577
L_F57A:
    jmp JOB_INVALID_BUFFER  ; $F57A

; WRITE job path; slot in X; restores disk VIA state after use.
.org $F600
WRITE_JOB:
    lda $00,x  ; $F600
    and #$01  ; $F602
    beq L_F609  ; $F604
    jmp L_F68E  ; $F606
L_F609:
    cpx #$05  ; $F609
    bne L_F610  ; $F60B
    jmp L_F691  ; $F60D
L_F610:
    jsr SEEK_REQUESTED_TRACK  ; $F610
    bcc L_F618  ; $F613
    jmp L_F689  ; $F615
L_F618:
    txa  ; $F618
    pha  ; $F619
    lda JOB_TRACK_SECTOR_OFFSETS,x  ; $F61A
    sta $32  ; $F61D
    lda #$00  ; $F61F
    sta $33  ; $F621
    sta $50  ; $F623
    txa  ; $F625
    clc  ; $F626
    adc #$03  ; $F627
    sta $51  ; $F629
    lda $1C02  ; $F62B
    pha  ; $F62E
    ora #$07  ; $F62F
    sta $1C02  ; $F631
    lda $1C00  ; $F634
    pha  ; $F637
    ora #$04  ; $F638
    sta $1C00  ; $F63A
    lda $1C03  ; $F63D
    pha  ; $F640
    lda $1C0C  ; $F641
    pha  ; $F644
    jsr FIND_WRITE_HEADER  ; $F645
    bcc L_F64D  ; $F648
    jmp L_F667  ; $F64A
L_F64D:
    jsr CHECK_WRITE_PROTECTION  ; $F64D
    bcc L_F655  ; $F650
    jmp L_F66E  ; $F652
L_F655:
    jsr ENCODE_DATA_BLOCK  ; $F655
    jsr WRITE_ENCODED_DATA_BLOCK  ; $F658
    bcc L_F660  ; $F65B
    jmp L_F667  ; $F65D
L_F660:
    lda #$01  ; $F660
    beq L_F667  ; $F662
    jmp L_F670  ; $F664
L_F667:
    lda #$02  ; $F667
    beq L_F66E  ; $F669
    jmp L_F670  ; $F66B
L_F66E:
    lda #$08  ; $F66E
L_F670:
    sta $4F  ; $F670
    pla  ; $F672
    sta $1C0C  ; $F673
    pla  ; $F676
    sta $1C03  ; $F677
    pla  ; $F67A
    sta $1C00  ; $F67B
    pla  ; $F67E
    sta $1C02  ; $F67F
    pla  ; $F682
    tax  ; $F683
    lda $4F  ; $F684
    sta $00,x  ; $F686
    rts  ; $F688
L_F689:
    lda #$02  ; $F689
    sta $00,x  ; $F68B
    rts  ; $F68D
L_F68E:
    jmp JOB_WRONG_DRIVE  ; $F68E
L_F691:
    jmp JOB_INVALID_BUFFER  ; $F691

; Restore Y, X, A and RTI from the frame prepared by Drive1541Hardware.
.org $FE7F
DOS_JOB_IRQ_RETURN:
    pla  ; $FE7F
    tay  ; $FE80
    pla  ; $FE81
    tax  ; $FE82
    pla  ; $FE83
    rti  ; $FE84

; These vectors preserve the supplied host helper ABI; this is not standalone DOS.
.org $FFFA
HARDWARE_VECTORS:
    .word NMI_RETURN, RESET, DOS_JOB_DISPATCH
