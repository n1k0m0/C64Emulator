; SPDX-License-Identifier: Apache-2.0
; Open 1541 DOS bootstrap and executable buffer-job entry.
; The IEC/DOS file service still lives in the emulator. This image is an
; executable starting point, not yet a replacement for 1541 DOS 2.6.

.org $c000
reset:
    sei
    cld
    ldx #$ff
    txs
    lda #$00
    sta $1800              ; release IEC output lines
    lda #$0e
    sta $1802              ; initialize serial VIA output directions
    lda #$7f
    sta $180e              ; disable serial VIA interrupt sources
    sta $1c0e              ; disable disk VIA interrupt sources
    cli                    ; the host can enter the DOS job IRQ path
    jmp command_loop

.org $c194
command_loop:
    jmp command_loop

; Decode one complete 5-byte GCR group into four data bytes. The input and
; output pointers are $3A/$3B and $3C/$3D. $40-$49 are disposable scratch.
; Carry is clear on success and set for an invalid 5-bit disk symbol.
; READ, VERIFY, and WRITE header validation use this decoder.
.org $c200
gcr_decode_group:
    ldy #$04
gcr_copy_input:
    lda ($3a),y
    sta $0040,y
    dey
    bpl gcr_copy_input
    lda #$00
    sta $47                 ; symbol number, 0..7
    sta $48                 ; output byte number, 0..3
gcr_next_symbol:
    lda #$00
    sta $45
    lda #$05
    sta $46
gcr_shift_bit:
    asl $44
    rol $43
    rol $42
    rol $41
    rol $40
    rol $45                 ; collect one bit from the original top byte
    dec $46
    bne gcr_shift_bit
    ldy $45
    lda gcr_decode_nibble,y
    cmp #$ff
    beq gcr_invalid
    sta $45
    lda $47
    and #$01
    bne gcr_low_nibble
    lda $45
    asl a
    asl a
    asl a
    asl a
    sta $49
    jmp gcr_next
gcr_low_nibble:
    ldy $48
    lda $45
    ora $49
    sta ($3c),y
    inc $48
gcr_next:
    inc $47
    lda $47
    cmp #$08
    bne gcr_next_symbol
    clc
    rts
gcr_invalid:
    sec
    rts
gcr_decode_nibble:
    .byte $ff,$ff,$ff,$ff,$ff,$ff,$ff,$ff
    .byte $ff,$08,$00,$01,$ff,$0c,$04,$05
    .byte $ff,$ff,$02,$03,$ff,$0f,$06,$07
    .byte $ff,$09,$0a,$0b,$ff,$0d,$0e,$ff

; Capture five consecutive bytes from the disk VIA through its byte-ready SO
; signal. The caller points $3A/$3B at a five-byte RAM region.
.org $c2a0
gcr_capture_group:
    ldy #$00
gcr_capture_next:
    clv
gcr_capture_wait:
    bvc gcr_capture_wait
    lda $1c01
    sta ($3a),y
    iny
    cpy #$05
    bne gcr_capture_next
    rts

; Find a SYNC run and capture $4A encoded bytes into ($3A/$3B). The routine
; supports a contiguous header pair of 10 bytes; the caller must keep $4A
; within one page. On this emulator's VIA, bit 7 goes low during SYNC and
; rises with the first data byte. The caller bounds absent media by cycles.
gcr_capture_after_sync:
    jsr gcr_wait_sync_low
    bcs gcr_capture_timeout
gcr_wait_sync_end:
    lda $1c00
    bpl gcr_wait_sync_end
gcr_capture_first:
    ldy #$00
    lda $1c01
    sta ($3a),y
    iny
gcr_capture_rest:
    clv
gcr_capture_rest_wait:
    bvc gcr_capture_rest_wait
    lda $1c01
    sta ($3a),y
    iny
    cpy $4a
    bne gcr_capture_rest
    clc
gcr_capture_timeout:
    rts

; Locate the selected track/sector header on the current spinning track.
; $32/$33 points at its two-byte job pair (track, sector). Each candidate
; captures both GCR header groups before decoding, so no byte is missed while
; the CPU spends cycles on the decoder. Carry is clear for a checksum-valid
; matching header, set after 42 nonmatching SYNC records.
.org $c300
gcr_find_header:
    lda #$2a
    sta $4b
gcr_scan_header:
    lda #$00
    sta $3a
    sta $3c
    lda #$02
    sta $3b
    sta $3d
    lda #$0a
    sta $4a
    jsr gcr_capture_after_sync
    bcs gcr_find_timeout
    lda #$10
    sta $3c
    jsr gcr_decode_group
    bcs gcr_next_header
    lda $0210
    cmp #$08
    bne gcr_next_header
    ldy #$00
    lda ($32),y
    cmp $0213
    bne gcr_next_header
    iny
    lda ($32),y
    cmp $0212
    bne gcr_next_header
    lda #$05
    sta $3a
    lda #$14
    sta $3c
    jsr gcr_decode_group
    bcs gcr_next_header
    lda $0216
    cmp #$0f
    bne gcr_next_header
    lda $0217
    cmp #$0f
    bne gcr_next_header
    lda $0212
    eor $0213
    eor $0214
    eor $0215
    cmp $0211
    bne gcr_next_header
    clc
    rts
gcr_next_header:
    dec $4b
    bne gcr_scan_header
gcr_find_timeout:
    sec
    rts

; Capture a complete 325-byte encoded data record. The first 256 bytes go to
; temporary DOS RAM $0200-$02FF, and the remaining 69 to low stack-page
; scratch $0100-$0144. No job buffer is overwritten before validation. This runs
; directly on byte-ready SO pulses so decoding can happen after capture.
.org $c400
gcr_capture_data:
    jsr gcr_wait_sync_low
    bcs gcr_data_timeout
gcr_data_wait_sync_end:
    lda $1c00
    bpl gcr_data_wait_sync_end
gcr_data_first_byte:
    ldy #$00
    lda $1c01
    clv                    ; arm byte-ready before storing the current byte
    sta $0200,y
    iny
gcr_data_first_page:
gcr_data_first_wait:
    bvc gcr_data_first_wait
    lda $1c01
    clv
    sta $0200,y
    iny
    bne gcr_data_first_page
    ldy #$bb               ; $0045+Y addresses $0100..$0144
gcr_data_last_bytes:
gcr_data_last_wait:
    bvc gcr_data_last_wait
    lda $1c01
    clv
    sta $0045,y
    iny
    bne gcr_data_last_bytes
    clc
gcr_data_timeout:
    rts

; Decode the 325-byte record captured by gcr_capture_data into the selected
; 256-byte job buffer at $50/$51. The encoded spill in $0100-$0144 is
; decoded first into output offsets 203..255. The first 256 encoded bytes are
; in $0200-$02FF and are
; decoded afterward into output offsets 0..202. Carry reports invalid GCR,
; wrong data marker/padding, or XOR checksum mismatch.
.org $c500
gcr_decode_data:
    lda #$58
    sta $3c
    lda #$00
    sta $3d
    sta $50
    sta $54                 ; running XOR checksum
    sta $57                 ; mismatch status for VERIFY mode
    lda #$cb               ; 203 data bytes precede the spill group
    sta $52
    lda $02ff
    sta $60
    ldy #$00
gcr_cross_copy:
    lda $0100,y
    sta $0061,y
    iny
    cpy #$04
    bne gcr_cross_copy
    lda #$60
    sta $3a
    lda #$00
    sta $3b
    jsr gcr_decode_group
    bcs gcr_decode_bad
    jsr gcr_store_four
    lda #$04
    sta $3a
    lda #$01
    sta $3b
    lda #$0c               ; groups 52..63
    sta $55
gcr_decode_spill:
    jsr gcr_decode_group
    bcs gcr_decode_bad
    jsr gcr_store_four
    jsr gcr_advance_input
    dec $55
    bne gcr_decode_spill
    jsr gcr_decode_group  ; final group at encoded spill offset 64
    bcs gcr_decode_bad
    ldy $52                ; final sector data byte at index 255
    lda $58
    jsr gcr_emit_byte
    lda $59
    sta $53                ; stored data checksum
    lda $5a
    ora $5b
    bne gcr_decode_bad
    lda #$00
    sta $3a
    sta $52
    lda #$02
    sta $3b
    jsr gcr_decode_group  ; first group: $07 marker + three data bytes
    bcs gcr_decode_bad
    lda $58
    cmp #$07
    bne gcr_decode_bad
    ldx #$01
gcr_store_first:
    ldy $52
    lda $58,x
    jsr gcr_emit_byte
    inc $52
    inx
    cpx #$04
    bne gcr_store_first
    jsr gcr_advance_input
    lda #$32               ; groups 1..50
    sta $55
gcr_decode_first_page:
    jsr gcr_decode_group
    bcs gcr_decode_bad
    jsr gcr_store_four
    jsr gcr_advance_input
    dec $55
    bne gcr_decode_first_page
    lda $54
    cmp $53
    bne gcr_decode_bad
    clc
    rts
gcr_decode_bad:
    sec
    rts
gcr_store_four:
    ldx #$00
gcr_store_four_loop:
    ldy $52
    lda $58,x
    jsr gcr_emit_byte
    inc $52
    inx
    cpx #$04
    bne gcr_store_four_loop
    rts
gcr_advance_input:
    clc
    lda $3a
    adc #$05
    sta $3a
    bcc gcr_advance_done
    inc $3b
gcr_advance_done:
    rts

; A carries one decoded sector byte, Y its buffer offset. READ stores it;
; VERIFY compares it without changing the caller's buffer. Both paths feed
; the disk checksum. $56 selects VERIFY, $57 records a mismatch as status $07.
gcr_emit_byte:
    sta $65
    eor $54
    sta $54
    lda $56
    bne gcr_compare_byte
    lda $65
    sta ($50),y
    rts
gcr_compare_byte:
    lda ($50),y
    cmp $65
    beq gcr_emit_done
    lda #$07
    sta $57
gcr_emit_done:
    rts

; Experimental native sector reader for a head already on the target track.
; $32/$33 points to track/sector; $51 is the selected buffer high byte.
; Capture the header into $58-$61 and the following 325-byte data record
; before decoding either one. This preserves every byte as the disk rotates.
.org $c700
gcr_read_current_track:
    ; Precompute the packed GCR bytes containing the requested sector/track.
    ; They are bytes 2..4 of the first encoded header group, independent of
    ; the disk ID and header checksum. A fast comparison avoids missing the
    ; following data SYNC while decoding an unrelated header.
    ldy #$00
    lda ($32),y
    sta $77                 ; requested track
    iny
    lda ($32),y
    sta $78                 ; requested sector
    lda $78
    lsr a
    lsr a
    lsr a
    lsr a
    tay
    lda gcr_encode_nibble,y
    sta $70
    lda $78
    and #$0f
    tay
    lda gcr_encode_nibble,y
    sta $71
    lda $77
    lsr a
    lsr a
    lsr a
    lsr a
    tay
    lda gcr_encode_nibble,y
    sta $72
    lda $77
    and #$0f
    tay
    lda gcr_encode_nibble,y
    sta $73
    lda $70
    lsr a
    sta $74
    lda #$00
    ror a                   ; low bit of sector-high GCR symbol -> bit 7
    sta $75
    lda $71
    asl a
    asl a
    ora $75
    sta $75
    lda $72
    lsr a
    lsr a
    lsr a
    ora $75
    sta $75
    lda $72
    asl a
    asl a
    asl a
    asl a
    asl a
    ora $73
    sta $76
    lda #$40               ; allow another pass when an edge straddles capture
    sta $4b
gcr_read_next_record:
    jsr gcr_capture_header_fast
    bcs gcr_read_timeout
    lda $58
    cmp #$52               ; encoded header ID $08, not data ID $07
    bne gcr_read_skip
    lda $5a
    and #$0f
    cmp $74
    bne gcr_read_skip
    lda $5b
    cmp $75
    bne gcr_read_skip
    lda $5c
    cmp $76
    bne gcr_read_skip
    lda #$00
    sta $3a
    lda $51
    sta $3b
    jsr gcr_capture_data
    bcs gcr_read_timeout
    lda #$58
    sta $3a
    lda #$68
    sta $3c
    lda #$00
    sta $3b
    sta $3d
    jsr gcr_decode_group
    bcs gcr_read_skip
    lda $68
    cmp #$08
    bne gcr_read_skip
    ldy #$00
    lda ($32),y
    cmp $6b
    bne gcr_read_skip
    iny
    lda ($32),y
    cmp $6a
    bne gcr_read_skip
    lda #$5d
    sta $3a
    lda #$6c
    sta $3c
    jsr gcr_decode_group
    bcs gcr_read_skip
    lda $6e
    cmp #$0f
    bne gcr_read_skip
    lda $6f
    cmp #$0f
    bne gcr_read_skip
    lda $6a
    eor $6b
    eor $6c
    eor $6d
    cmp $69
    bne gcr_read_skip
    jsr gcr_decode_data
    bcs gcr_read_skip       ; retry another revolution on a damaged record
    clc
    rts
gcr_read_skip:
    dec $4b
    bne gcr_read_next_record
gcr_read_timeout:
    sec
    rts
gcr_encode_nibble:
    .byte $0a,$0b,$12,$13,$0e,$0f,$16,$17
    .byte $09,$19,$1a,$1b,$0d,$1d,$1e,$15

; Bound the wait for a SYNC run to 64*256 VIA polls. A missing disk or an
; unformatted track must return to the job code rather than hang the drive CPU.
.org $c900
gcr_wait_sync_low:
    lda #$40
    sta $4d
    lda #$00
    sta $4c
gcr_wait_sync_low_poll:
    lda $1c00
    bpl gcr_wait_sync_low_found
    dec $4c
    bne gcr_wait_sync_low_poll
    dec $4d
    bne gcr_wait_sync_low_poll
    sec
    rts
gcr_wait_sync_low_found:
    clc
    rts

; Dedicated ten-byte header capture. Zero-page indexed stores keep pace with
; the shorter byte-ready interval of inner tracks (the generic pointer loop
; is too slow there). Preserve the first byte immediately at the SYNC edge.
.org $c930
gcr_capture_header_fast:
    jsr gcr_wait_sync_low
    bcs gcr_header_timeout
gcr_header_wait_end:
    lda $1c00
    bpl gcr_header_wait_end
    ldx #$00
    lda $1c01
    clv
    sta $58,x
    inx
gcr_header_next:
gcr_header_wait_ready:
    bvc gcr_header_wait_ready
    lda $1c01
    clv
    sta $58,x
    inx
    cpx #$0a
    bne gcr_header_next
    clc
gcr_header_timeout:
    rts

; Encode four data bytes into five disk GCR bytes. The source pointer is
; $3A/$3B, the destination pointer $3C/$3D. $40-$46 are scratch. This runs
; before write mode so disk rotation cannot outrun the encoding work.
.org $ca00
gcr_encode_group:
    lda #$00
    ldx #$04
gcr_encode_clear:
    sta $40,x
    dex
    bpl gcr_encode_clear
    ldy #$00
gcr_encode_source:
    lda ($3a),y
    sta $45
    lsr a
    lsr a
    lsr a
    lsr a
    tax
    lda gcr_encode_nibble,x
    jsr gcr_encode_append
    lda $45
    and #$0f
    tax
    lda gcr_encode_nibble,x
    jsr gcr_encode_append
    iny
    cpy #$04
    bne gcr_encode_source
    ldy #$00
    ldx #$00
gcr_encode_store:
    lda $40,x
    sta ($3c),y
    iny
    inx
    cpx #$05
    bne gcr_encode_store
    rts
gcr_encode_append:
    sta $46
    ldx #$05
gcr_encode_shift:
    asl $44
    rol $43
    rol $42
    rol $41
    rol $40
    dex
    bne gcr_encode_shift
    lda $44
    ora $46
    sta $44
    rts

; Build a full 325-byte encoded sector data record from the selected 256-byte
; job buffer at $50/$51. The first 255 bytes use $0200-$02FE and the final
; 70 bytes use $0100-$0145; no encoded group crosses this split.
.org $cb00
gcr_build_data_record:
    lda #$60
    sta $3a
    lda #$00
    sta $3b
    sta $3c
    sta $64
    sta $65
    sta $66
    lda #$02
    sta $3d
    lda #$41               ; 65 four-byte source groups
    sta $67
gcr_build_group:
    ldx #$00
gcr_build_fill:
    jsr gcr_next_record_byte
    sta $60,x
    inx
    cpx #$04
    bne gcr_build_fill
    jsr gcr_encode_group
    clc
    lda $3c
    adc #$05
    sta $3c
    bcc gcr_build_no_carry
    inc $3d
gcr_build_no_carry:
    dec $67
    beq gcr_build_done
    lda $67
    cmp #$0e               ; 51 groups = 255 output bytes
    bne gcr_build_group
    lda #$00
    sta $3c
    lda #$01
    sta $3d
    jmp gcr_build_group
gcr_build_done:
    rts

gcr_next_record_byte:
    lda $65
    bne gcr_record_high
    lda $64
    beq gcr_record_marker
    sec
    sbc #$01
    tay
    lda ($50),y
    jmp gcr_record_data
gcr_record_marker:
    lda #$07
    jmp gcr_record_advance
gcr_record_high:
    lda $64
    beq gcr_record_last_data
    cmp #$01
    beq gcr_record_checksum
    lda #$00               ; two padding bytes
    jmp gcr_record_advance
gcr_record_last_data:
    ldy #$ff
    lda ($50),y
gcr_record_data:
    sta $68
    eor $66
    sta $66
    lda $68
    jmp gcr_record_advance
gcr_record_checksum:
    lda $66
gcr_record_advance:
    inc $64
    bne gcr_record_return
    inc $65
gcr_record_return:
    rts

; Stream a prebuilt sector record through the disk VIA. Find the matching
; header again after record construction, then open the write gate at its
; following data SYNC without touching the encoded record in $0100/$0200.
.org $cc00
gcr_write_current_track:
    jsr gcr_find_write_header
    bcs gcr_write_failed
    lda #$60               ; at most two full rotations, including splices
    sta $4e
gcr_write_scan_again:
    jsr gcr_capture_header_fast
    bcs gcr_write_failed
    ldx #$04
gcr_write_match_header:
    lda $58,x
    cmp $74,x              ; prevalidated encoded header prefix
    bne gcr_write_skip_sync
    dex
    bpl gcr_write_match_header
    ldy #$ff               ; data SYNC must follow this header soon
gcr_write_data_sync_low:
    lda $1c00
    bpl gcr_write_sync_begin
    dey
    bne gcr_write_data_sync_low
    jmp gcr_write_failed
gcr_write_skip_sync:
    dec $4e
    bne gcr_write_scan_again
    sec
    rts
gcr_write_failed:
    sec
    rts
gcr_write_sync_begin:
    lda #$ff
    sta $1c03
    sta $1c01
    lda #$80
    sta $1c0c
    ldx #$04
    clv
gcr_write_sync_bytes:
gcr_write_sync_ready:
    bvc gcr_write_sync_ready
    lda #$ff
    sta $1c01
    clv
    dex
    bne gcr_write_sync_bytes
    ldy #$00
gcr_write_first_page:
gcr_write_first_ready:
    bvc gcr_write_first_ready
    lda $0200,y
    sta $1c01
    clv
    iny
    cpy #$ff
    bne gcr_write_first_page
    ldy #$00
gcr_write_last_page:
gcr_write_last_ready:
    bvc gcr_write_last_ready
    lda $0100,y
    sta $1c01
    clv
    iny
    cpy #$46
    bne gcr_write_last_page
gcr_write_flush_first:
    bvc gcr_write_flush_first
    clv
gcr_write_flush_second:
    bvc gcr_write_flush_second
    lda #$a0
    sta $1c0c
    clc
    rts

; Header capture and validation use zero page, preserving the encoded data.
.org $cd00
gcr_find_write_header:
    lda #$2a
    sta $4b
gcr_write_header_next:
    jsr gcr_capture_header_fast
    bcs gcr_write_header_fail
    lda #$58
    sta $3a
    lda #$68
    sta $3c
    lda #$00
    sta $3b
    sta $3d
    jsr gcr_decode_group
    bcs gcr_write_header_skip
    lda $68
    cmp #$08
    bne gcr_write_header_skip
    ldy #$00
    lda ($32),y
    cmp $6b
    bne gcr_write_header_skip
    iny
    lda ($32),y
    cmp $6a
    bne gcr_write_header_skip
    lda #$5d
    sta $3a
    lda #$6c
    sta $3c
    jsr gcr_decode_group
    bcs gcr_write_header_skip
    lda $6e
    cmp #$0f
    bne gcr_write_header_skip
    lda $6f
    cmp #$0f
    bne gcr_write_header_skip
    lda $6a
    eor $6b
    eor $6c
    eor $6d
    cmp $69
    bne gcr_write_header_skip
    ldx #$04
gcr_write_save_header:
    lda $58,x
    sta $74,x
    dex
    bpl gcr_write_save_header
    clc
    rts
gcr_write_header_skip:
    dec $4b
    bne gcr_write_header_next
gcr_write_header_fail:
    sec
    rts

; The write-protect contact also signals disk removal/insertion. Accept only
; a sustained high input so a short disk-change pulse cannot authorize a
; write. A bounded wait returns carry for absent or protected media.
.org $ce00
gcr_wait_write_enabled:
    lda #$00
    sta $69
    sta $6a
    sta $6b
    sta $6c
    sta $6d
gcr_write_sensor_poll:
    inc $6b
    bne gcr_write_sensor_read
    inc $6c
    bne gcr_write_sensor_read
    inc $6d
    lda $6d
    cmp #$01
    bcs gcr_write_sensor_timeout
gcr_write_sensor_read:
    lda $1c00
    and #$10
    beq gcr_write_sensor_reset
    inc $69
    bne gcr_write_sensor_poll
    inc $6a
    lda $6a
    cmp #$30
    bne gcr_write_sensor_poll
    clc
    rts
gcr_write_sensor_reset:
    lda #$00
    sta $69
    sta $6a
    jmp gcr_write_sensor_poll
gcr_write_sensor_timeout:
    sec
    rts

; Existing drive-side handoff code expects this address as an idle return.
.org $e654
idle_return:
    jmp command_loop

; The hardware job scheduler enters $F2B0 with the DOS IRQ stack frame.
; Jobs $D0/$E0 execute code at the start of buffer 0..4 ($0300..$0700).
; Slot 5 has no executable buffer: $0800 mirrors zero-page RAM on a 1541.
; $E0 currently uses the same path; its disk-header preparation is pending.
; $32/$33 must point at the selected slot's track/sector pair while that
; buffer runs. Uploaded drive programs use this DOS job-loop convention.
; $C0 restores the head to the track-1 stop using the disk VIA stepper lines.
; $B0 seeks to a job-pair track through the same physical stepper outputs.
.org $f2b0
irq_entry:
    ldx #$05
job_find:
    lda $00,x
    and #$f0
    cmp #$80
    beq job_read
    cmp #$90
    beq job_write
    cmp #$a0
    beq job_read
    cmp #$b0
    beq job_seek
    cmp #$c0
    beq job_bump
    cmp #$d0
    beq job_execute
    cmp #$e0
    beq job_execute
    dex
    bpl job_find
    rts
job_execute:
    lda $00,x
    and #$01               ; drive 1 is absent in a single-drive 1541
    bne job_bad_drive
    cpx #$05
    beq job_no_buffer
    txa
    pha                    ; preserve job slot across uploaded code
    lda job_pair_offsets,x ; table avoids decimal-mode arithmetic
    sta $32                ; track/sector pair: slot 0 -> $0006
    lda #$00
    sta $33
    jsr job_call_buffer
    pla
    tax
    lda #$01               ; completed without disk error
    sta $00,x
    rts
job_bad_drive:
    lda #$0f               ; DOS job status: drive not present
    sta $00,x
    rts
job_no_buffer:
    lda #$02               ; no buffer is mapped for this job slot
    sta $00,x
    rts
job_seek:
    lda $00,x
    and #$01
    bne job_bad_drive
    jsr job_seek_core
    bcs job_seek_status_bad
    lda #$01
    sta $00,x
    rts
job_seek_status_bad:
    lda #$02
    sta $00,x
    rts
job_seek_core:
    txa
    pha                    ; slot is restored after X counts head steps
    lda job_pair_offsets,x
    sta $32
    lda #$00
    sta $33
    tay
    lda ($32),y            ; target track
    beq job_seek_bad_pair
    cmp #$2b               ; tracks 1..42 fit the D64 geometry
    bcs job_seek_bad_pair
    tax
    iny
    lda ($32),y            ; target sector
    cmp seek_sector_counts,x
    bcs job_seek_bad_pair
    lda $34
    pha
    lda $35
    pha
    lda $1c02
    pha
    ora #$07               ; stepper phases and motor are outputs
    sta $1c02
    lda $1c00
    pha
    and #$fc
    ora #$04               ; motor on during motion
    sta $34
    lda $1c00
    and #$03
    sta $35                ; physical stepper phase, modulo four
    ora $34
    sta $1c00
    ldx #$54               ; home from any position to track 1
job_seek_home:
    dec $35
    lda $35
    and #$03
    sta $35
    ora $34
    sta $1c00
    dex
    bne job_seek_home
    ldy #$00
    lda ($32),y            ; one logical track spans two half-tracks
    tax
    dex
    beq job_seek_restore
job_seek_outward:
    inc $35
    lda $35
    and #$03
    sta $35
    ora $34
    sta $1c00
    inc $35
    lda $35
    and #$03
    sta $35
    ora $34
    sta $1c00
    dex
    bne job_seek_outward
job_seek_restore:
    pla
    sta $1c00              ; restore previous motor/LED/density/phase
    pla
    sta $1c02
    pla
    sta $35
    pla
    sta $34
    pla
    tax
    ldy #$00
    lda ($32),y
    sta $18                ; DOS current track
    iny
    lda ($32),y
    sta $19                ; DOS current sector
    clc
    rts
job_seek_bad_pair:
    pla
    tax
    sec
    rts
job_bump:
    lda $00,x
    and #$01
    bne job_bad_drive
    txa
    pha                    ; preserve the job slot while X counts steps
    lda $34
    pha                    ; restore the DOS scratch byte on exit
    lda $1c02
    pha                    ; save the disk VIA port-B direction register
    ora #$07               ; drive both stepper phases and the motor
    sta $1c02
    lda $1c00
    pha                    ; save port-B outputs, including the motor state
    and #$fc
    ora #$04               ; keep motor on while stepping
    sta $34
    lda $1c00
    and #$03
    tay                    ; current four-phase stepper position
    tya
    ora $34
    sta $1c00
    ldx #$54               ; 84 half-track steps reach the inner stop
job_bump_step:
    dey
    tya
    and #$03
    tay
    ora $34
    sta $1c00
    dex
    bne job_bump_step
    pla
    sta $1c00              ; restore motor, LED, density, and stepper phase
    pla
    sta $1c02
    pla
    sta $34
    pla
    tax
    lda #$01
    sta $00,x
    rts
job_call_buffer:
    ; An RTS trampoline leaves $32/$33 and all other DOS RAM untouched.
    ; The helper's JSR return address remains below the target address;
    ; the uploaded buffer's RTS returns to job_execute after the JSR.
    lda job_buffer_return_pages,x
    pha
    lda #$ff               ; low byte of (buffer base - 1)
    pha
    lda #$00               ; retain the previous A-at-buffer-entry value
    rts
job_pair_offsets:
    .byte $06,$08,$0a,$0c,$0e,$10
job_buffer_return_pages:
    .byte $02,$03,$04,$05,$06 ; high byte of (buffer base - 1)
seek_sector_counts:
    ; Sector count for track 0 (invalid), then tracks 1..42.
    .byte $00
    .byte $15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15,$15
    .byte $13,$13,$13,$13,$13,$13,$13
    .byte $12,$12,$12,$12,$12,$12
    .byte $11,$11,$11,$11,$11,$11,$11,$11,$11,$11,$11,$11

nmi_entry:
    rti

; Native READ and VERIFY jobs: validate/seek through the disk VIA, then
; receive a complete GCR sector. VERIFY compares against the selected buffer.
.org $f500
job_read:
    lda $00,x
    and #$f0
    cmp #$a0
    beq job_verify_mode
    lda #$00
    sta $56
    jmp job_sector_start
job_verify_mode:
    lda #$01
    sta $56
job_sector_start:
    lda $00,x
    and #$01
    bne job_read_bad_drive
    cpx #$05
    beq job_read_no_buffer
    jsr job_seek_core
    bcs job_read_error     ; no transient completion status during motion
    txa
    pha                    ; slot survives GCR decoding
    lda job_pair_offsets,x
    sta $32
    lda #$00
    sta $33
    txa
    clc
    adc #$03
    sta $51                ; selected $0300-$0700 job buffer page
    lda $1c02
    pha                    ; preserve disk VIA direction and motor state
    ora #$07
    sta $1c02
    lda $1c00
    pha
    ora #$04
    sta $1c00
    jsr gcr_read_current_track
    pla
    sta $1c00
    pla
    sta $1c02
    pla
    tax
    bcs job_read_error
    lda $57
    bne job_verify_error
    lda #$01
    sta $00,x
job_read_done:
    rts
job_verify_error:
    sta $00,x              ; $07: sector differs from the selected buffer
    rts
job_read_error:
    lda #$02
    sta $00,x
    rts
job_read_bad_drive:
    jmp job_bad_drive
job_read_no_buffer:
    jmp job_no_buffer

; Native sector WRITE through the disk VIA and GCR stream.
.org $f600
job_write:
    lda $00,x
    and #$01
    bne job_write_bad_drive
    cpx #$05
    beq job_write_no_buffer
    jsr job_seek_core
    bcs job_write_error
    txa
    pha
    lda job_pair_offsets,x
    sta $32
    lda #$00
    sta $33
    sta $50
    txa
    clc
    adc #$03
    sta $51
    lda $1c02
    pha
    ora #$07
    sta $1c02
    lda $1c00
    pha
    ora #$04
    sta $1c00
    lda $1c03
    pha
    lda $1c0c
    pha
    jsr gcr_find_write_header
    bcs job_write_stream_error
    jsr gcr_wait_write_enabled
    bcs job_write_protected
    jsr gcr_build_data_record
    jsr gcr_write_current_track
    bcs job_write_stream_error
    lda #$01
    bne job_write_status
job_write_stream_error:
    lda #$02
    bne job_write_status
job_write_protected:
    lda #$08
job_write_status:
    sta $4f
    pla
    sta $1c0c
    pla
    sta $1c03
    pla
    sta $1c00
    pla
    sta $1c02
    pla
    tax
    lda $4f
    sta $00,x
    rts
job_write_error:
    lda #$02
    sta $00,x
    rts
job_write_bad_drive:
    jmp job_bad_drive
job_write_no_buffer:
    jmp job_no_buffer

; Synthetic DOS IRQ calls $F2B0 with an $FE7E return frame. Restore the
; saved registers and the interrupted PC/status after the job completes.
.org $fe7f
job_irq_return:
    pla
    tay
    pla
    tax
    pla
    rti

.org $fffa
    .word nmi_entry,reset,irq_entry
