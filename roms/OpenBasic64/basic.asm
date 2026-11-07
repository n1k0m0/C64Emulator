; SPDX-License-Identifier: Apache-2.0
; Open Basic 64. Independently written 6510 BASIC interpreter.
; ROM ABI: $A000 cold vector, $A002 warm vector; tokenized programs at $0801.
; Numeric expressions use the independent floating-point engine in float.asm.

.org $a000
    .word cold_start,warm_start
.org $a004
; COS small-angle continuation. This isolated ROM experiment uses only the
; pre-existing A004-A00F gap and leaves all existing BASIC labels anchored.
math_cos_small_subtract:
    jsr $fabb
    jsr fp_subtract
    jmp math_cos_small_clamp
math_cos_small_to_zero:
    jmp $f19c
.org $a010
    jmp syntax_error       ; BASIC RAM vector: error
    jmp warm_start         ; main loop
    jmp tokenize           ; tokenization
    jmp command_list       ; listing
    jmp statements         ; statement execution
    jmp expression         ; expression evaluation
    jmp command_load       ; selected KERNAL private glue
    jmp command_save
    jmp command_open
    jmp command_close
    jmp command_sys
    jmp parse_filename
    jmp command_verify
.org $a037
; Any nonzero tiny angle whose triangular correction rounds back to an
; exact unit receives the same one-ULP-below-unit value as COS(0).
math_cos_small_clamp:
    lda $03e0
    lsr a
    bcs math_cos_small_to_zero
    jmp $fcfc
.org $a040

cold_start:
    lda #$01
    sta $2b
    lda #$08
    sta $2c
    lda #$00
    sta $37
    lda #$a0
    sta $38
    jsr new_program
    jsr $f1e6              ; initialize the five-byte RND state
    ldx #$00
banner_loop:
    lda banner,x
    beq banner_done
    jsr $ffd2
    inx
    bne banner_loop
banner_done:
    jmp warm_start
warm_start:
    jsr $ffcc
ready:
    lda #$0d
    jsr $ffd2
    ldx #$00
ready_text_loop:
    lda ready_text,x
    beq read_line
    jsr $ffd2
    inx
    bne ready_text_loop
read_line:
    ldx #$00
read_character:
    jsr $ffcf
    cmp #$0d
    beq line_received
    cpx #$57
    bcs read_character
    sta $0200,x
    inx
    jmp read_character
line_received:
    lda #$00
    sta $0200,x
    sta $7a
    lda #$02
    sta $7b
    jsr skip_space
    cmp #$00
    beq ready
    cmp #$30
    bcc direct_line
    cmp #$3a
    bcs direct_line
    jsr parse_decimal
    lda $60
    sta $5c
    lda $61
    sta $5d
    jsr tokenize
    jsr store_line
    jmp ready
direct_line:
    jsr tokenize
    lda #$80
    sta $7a
    lda #$03
    sta $7b
    lda #$00
    sta $6e
    jsr statements
    jmp ready

; Tokenization is lexical, quote-aware, and preserves DATA/REM payloads.
; The table uses public BASIC V2 token values $80-$CB.
tokenize:
    lda #$80
    sta $6a
    lda #$03
    sta $6b
    lda #$00
    sta $6d                 ; quoted text
    sta $6f                 ; raw REM/DATA text
token_next:
    ldy #$00
    lda ($7a),y
    beq token_end
    cmp #$22
    bne token_not_quote
    lda $6d
    eor #$01
    sta $6d
token_not_quote:
    lda $6d
    bne token_copy
    lda $6f
    bne token_copy
    lda ($7a),y
    cmp #$3f
    bne token_keyword
    lda #$99
    jmp token_write
token_keyword:
    lda #<keywords
    sta $66
    lda #>keywords
    sta $67
    lda #$80
    sta $68
token_candidate:
    lda $68
    cmp #$cc
    beq token_copy
    ldy #$00
token_compare:
    lda ($66),y
    beq token_match
    cmp ($7a),y
    bne token_miss
    iny
    bne token_compare
token_match:
    cpy #$00
    beq token_miss
    tya
    clc
    adc $7a
    sta $7a
    bcc token_match_no_carry
    inc $7b
token_match_no_carry:
    lda $68
    cmp #$8f
    bne token_check_data
    lda #$01
    sta $6f
token_check_data:
    cmp #$83
    bne token_write_match
    lda #$01
    sta $6f
    lda $68
    jmp token_write_match
token_miss:
    ldy #$00
token_skip_keyword:
    lda ($66),y
    beq token_keyword_end
    iny
    bne token_skip_keyword
token_keyword_end:
    iny
    tya
    clc
    adc $66
    sta $66
    bcc token_no_carry
    inc $67
token_no_carry:
    inc $68
    jmp token_candidate
token_copy:
    ldy #$00
    lda ($7a),y
    cmp #$3a
    bne token_write
    lda #$00
    sta $6f
    lda #$3a
token_write:
    ldy #$00
    sta ($6a),y
    inc $6a
    bne token_write_next
    inc $6b
token_write_next:
    jsr advance
    jmp token_next
token_write_match:
    ldy #$00
    sta ($6a),y
    inc $6a
    bne token_next
    inc $6b
    jmp token_next
token_end:
    ldy #$00
    sta ($6a),y
    rts

new_program:
    lda #$00
    sta $0801
    sta $0802
    lda #$03
    sta $2d
    sta $2f
    sta $31
    lda #$08
    sta $2e
    sta $30
    sta $32
    lda #$00
    sta $33
    lda #$a0
    sta $34
    lda #$00
    sta $70
    sta $77
    sta $78
    jmp reset_data

; Find first line with number >= $5C/$5D, or final sentinel.
find_line:
    lda #$01
    sta $5e
    lda #$08
    sta $5f
find_line_loop:
    ldy #$01
    lda ($5e),y
    beq find_line_done
    ldy #$03
    lda ($5e),y
    cmp $5d
    bcc find_line_advance
    bne find_line_done
    dey
    lda ($5e),y
    cmp $5c
    bcs find_line_done
find_line_advance:
    ldy #$00
    lda ($5e),y
    tax
    iny
    lda ($5e),y
    sta $5f
    stx $5e
    jmp find_line_loop
find_line_done:
    rts

store_line:
    jsr find_line
    ldy #$01
    lda ($5e),y
    beq store_not_found
    ldy #$02
    lda ($5e),y
    cmp $5c
    bne store_not_found
    iny
    lda ($5e),y
    cmp $5d
    bne store_not_found
    jsr delete_line
store_not_found:
    lda $0380
    beq store_finish
    ; Count token bytes, including terminator; total record length = count+4.
    ldx #$00
store_count:
    lda $0380,x
    inx
    bne store_count_end
    jmp syntax_error
store_count_end:
    cmp #$00
    bne store_count
    txa
    clc
    adc #$04
    sta $64
    lda #$00
    adc #$00
    sta $65
    ; $66 = last byte of program, $6A = its new address.
    sec
    lda $2d
    sbc #$01
    sta $66
    lda $2e
    sbc #$00
    sta $67
    clc
    lda $2d
    adc $64
    sta $2d
    lda $2e
    adc $65
    sta $2e
    cmp $38
    bcs out_of_memory
    sec
    lda $2d
    sbc #$01
    sta $6a
    lda $2e
    sbc #$00
    sta $6b
store_shift:
    ldy #$00
    lda ($66),y
    sta ($6a),y
    lda $66
    cmp $5e
    bne store_shift_more
    lda $67
    cmp $5f
    beq store_shift_done
store_shift_more:
    lda $66
    bne store_source_dec
    dec $67
store_source_dec:
    dec $66
    lda $6a
    bne store_dest_dec
    dec $6b
store_dest_dec:
    dec $6a
    jmp store_shift
store_shift_done:
    ; A newly inserted line may occupy the old zero-link sentinel.
    ; Mark it as a line before the common relinker scans by link presence.
    ; A still holds the high byte of the destination line from the compare.
    ldy #$01
    sta ($5e),y
    dey
    lda #$01
    sta ($5e),y
    ldy #$02
    lda $5c
    sta ($5e),y
    iny
    lda $5d
    sta ($5e),y
    lda $5e
    clc
    adc #$04
    sta $6a
    lda $5f
    adc #$00
    sta $6b
    ldx #$00
store_copy:
    ldy #$00
    lda $0380,x
    sta ($6a),y
    beq store_copy_done
    inc $6a
    bne store_copy_next
    inc $6b
store_copy_next:
    inx
    bne store_copy
store_copy_done:
    jsr relink
store_finish:
    jsr clear_variables
    rts

delete_line:
    ldy #$00
    lda ($5e),y
    sta $66                 ; source = old next
    iny
    lda ($5e),y
    sta $67
    sec
    lda $66
    sbc $5e
    sta $64
    lda $67
    sbc $5f
    sta $65
    lda $5e
    sta $6a
    lda $5f
    sta $6b
delete_shift:
    lda $66
    cmp $2d
    bne delete_copy
    lda $67
    cmp $2e
    beq delete_done
delete_copy:
    ldy #$00
    lda ($66),y
    sta ($6a),y
    inc $66
    bne delete_source_next
    inc $67
delete_source_next:
    inc $6a
    bne delete_shift
    inc $6b
    jmp delete_shift
delete_done:
    sec
    lda $2d
    sbc $64
    sta $2d
    lda $2e
    sbc $65
    sta $2e
    jmp relink
relink:
    lda #$01
    sta $66
    lda #$08
    sta $67
relink_line:
    ; The link high byte decides whether another BASIC line follows.
    ldy #$01
    lda ($66),y
    beq relink_done
    ldy #$04
relink_scan:
    lda ($66),y
    beq relink_end_text
    iny
    bne relink_scan
    inc $67
    jmp relink_scan
relink_end_text:
    iny
    tya
    clc
    adc $66
    sta $6a
    lda $67
    adc #$00
    sta $6b
    ldy #$00
    lda $6a
    sta ($66),y
    iny
    lda $6b
    sta ($66),y
    lda $6a
    sta $66
    lda $6b
    sta $67
    jmp relink_line
relink_done:
    rts

; The statement executor reads tokenized text through $7A/$7B.
statements:
    jsr skip_space
    beq statements_done
    cmp #$3a
    bne statement_start
    jsr advance
    jmp statements
statement_start:
    cmp #$81
    jbeq command_for
    cmp #$82
    jbeq command_next
    cmp #$99
    jbeq command_print
    cmp #$98
    jbeq command_print_hash
    cmp #$9b
    jbeq command_list
    cmp #$8a
    jbeq command_run
    cmp #$a2
    jbeq command_new
    cmp #$80
    jbeq command_end
    cmp #$90
    jbeq command_end
    cmp #$8f
    jbeq command_rem
    cmp #$83
    jbeq command_data
    cmp #$87
    jbeq command_read
    cmp #$85
    jbeq command_input
    cmp #$84
    jbeq command_input_hash
    cmp #$89
    jbeq command_goto
    cmp #$8b
    jbeq command_if
    cmp #$91
    jbeq command_on
    cmp #$8c
    jbeq command_restore
    cmp #$97
    jbeq command_poke
    cmp #$92
    jbeq command_wait
    cmp #$9e
    jbeq command_sys
    cmp #$8d
    jbeq command_gosub
    cmp #$8e
    jbeq command_return
    cmp #$9c
    jbeq command_clear
    cmp #$88
    jbeq command_let
    cmp #$93
    jbeq command_load
    cmp #$94
    jbeq command_save
    cmp #$95
    jbeq command_verify
    cmp #$9f
    jbeq command_open
    cmp #$a0
    jbeq command_close
    cmp #$a1
    jbeq command_get
    cmp #$d0
    jbcs syntax_error
    ; Assignment without LET.
    cmp #$41
    jbcc syntax_error
    cmp #$5b
    jbcs syntax_error
    jmp command_let_body
statements_done:
    rts
statement_continue:
    jsr skip_space
    cmp #$3a
    bne statement_complete
    jsr advance
    jmp statements
statement_complete:
    cmp #$00
    bne syntax_error
    rts

command_end:
    lda #$00
    sta $6e
    rts
command_rem:
    lda #$00
    sta $6f
    rts
command_data:
    jsr advance
data_statement_skip:
    jsr peek
    beq statements_done
    cmp #$3a
    beq data_statement_next
    jsr advance
    jmp data_statement_skip
data_statement_next:
    jsr advance
    jmp statements
command_restore:
    jsr advance
    jsr reset_data
    jmp statement_continue

command_read:
    jsr advance
read_variable:
    jsr parse_variable
    lda $68
    sta $035c
    lda $69
    sta $035d
    lda $7a
    sta $035a
    lda $7b
    sta $035b
    jsr data_next_item
    jsr is_string_variable
    bcs read_string
    lda $75
    beq read_numeric_zero
    lda $64
    sta $7a
    lda $65
    sta $7b
    jsr fp_expression
    jmp read_store
read_numeric_zero:
    jsr fp_zero
read_store:
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr fp_store_variable
    jmp read_restore
read_string:
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr store_string_source
read_restore:
    jsr restore_text_pointer
    jsr skip_space
    cmp #$2c
    bne read_done
    jsr advance
    jmp read_variable
read_done:
    jmp statement_continue

reset_data:
    lda #$01
    sta $83
    lda #$08
    sta $84
    lda #$05
    sta $80
    lda #$08
    sta $81
    lda #$00
    sta $82
    rts
data_peek:
    ldy #$00
    lda ($80),y
    rts
data_advance:
    inc $80
    bne data_peek
    inc $81
    jmp data_peek
data_next_line:
    ldy #$00
    lda ($83),y
    tax
    iny
    lda ($83),y
    sta $84
    stx $83
    lda $83
    clc
    adc #$04
    sta $80
    lda $84
    adc #$00
    sta $81
    lda #$00
    sta $82
    rts
data_find:
    lda $82
    bne data_found
data_search:
    ldy #$01
    lda ($83),y
    beq out_of_data
    jsr data_peek
    beq data_search_next_line
    cmp #$83
    beq data_token_found
    cmp #$8f
    beq data_search_next_line
    jsr data_advance
    jmp data_search
data_search_next_line:
    jsr data_next_line
    jmp data_search
data_token_found:
    jsr data_advance
    lda #$01
    sta $82
data_found:
    rts
data_next_item:
    jsr data_find
data_skip_space:
    jsr data_peek
    cmp #$20
    bne data_item_start
    jsr data_advance
    jmp data_skip_space
data_item_start:
    cmp #$22
    bne data_unquoted
    jsr data_advance
    lda $80
    sta $64
    lda $81
    sta $65
    lda #$00
    sta $75
data_quote_scan:
    jsr data_peek
    beq data_item_finish
    cmp #$22
    beq data_quote_end
    inc $75
    jsr data_advance
    jmp data_quote_scan
data_quote_end:
    jsr data_advance
    jmp data_item_finish
data_unquoted:
    lda $80
    sta $64
    lda $81
    sta $65
    lda #$00
    sta $75
data_plain_scan:
    jsr data_peek
    beq data_item_finish
    cmp #$2c
    beq data_item_finish
    cmp #$3a
    beq data_item_finish
    inc $75
    jsr data_advance
    jmp data_plain_scan
data_item_finish:
data_delimiter:
    jsr data_peek
    cmp #$20
    bne data_delimiter_found
    jsr data_advance
    jmp data_delimiter
data_delimiter_found:
    cmp #$2c
    beq data_next_comma
    cmp #$3a
    beq data_next_statement
    cmp #$00
    bne syntax_error
    jmp data_next_line
data_next_comma:
    jmp data_advance
data_next_statement:
    jsr data_advance
    lda #$00
    sta $82
    rts

restore_text_pointer:
    lda $035a
    sta $7a
    lda $035b
    sta $7b
    rts

command_new:
    jsr new_program
    lda #$00
    sta $6e
    rts
command_clear:
    jsr clear_variable_pointers
    jsr advance
    jmp statement_continue

command_print:
    jsr advance
    jsr print_items
    jmp statement_continue
print_items:
    jsr skip_space
    beq print_newline
    cmp #$3a
    beq print_newline
print_item:
    cmp #$22
    bne print_numeric
    jsr advance
print_string:
    jsr peek
    beq syntax_error
    cmp #$22
    beq print_string_done
    jsr $ffd2
    jsr advance
    jmp print_string
print_string_done:
    jsr advance
    jmp print_separator
print_numeric:
    cmp #$c7
    beq print_character_function
    cmp #$c4
    beq print_str_function
    lda $7a
    sta $72
    jmp print_numeric_after_private
; A real game uses this independent BASIC runtime-clear entry directly.
; Keep it source-defined and route it through the existing variable reset.
.org $a659
private_basic_clear:
    jmp private_runtime_clear
print_numeric_after_private:
    lda $7b
    sta $73
    jsr peek
    cmp #$41
    bcc print_numeric_expression
    cmp #$5b
    bcs print_numeric_expression
    jsr parse_variable
    jsr is_string_variable
    bcc print_numeric_restore
    jsr print_string_variable
    jmp print_separator
print_numeric_restore:
    lda $72
    sta $7a
    lda $73
    sta $7b
print_numeric_expression:
    jsr fp_expression
    lda #$20
    jsr $ffd2
    jsr $fd18
    lda #$20
    jsr $ffd2
print_separator:
    jsr skip_space
    cmp #$3b
    beq print_semicolon
    cmp #$2c
    beq print_comma
print_newline:
    lda #$0d
    jmp $ffd2
print_semicolon:
    jsr advance
    jsr skip_space
    beq print_no_newline
    cmp #$3a
    beq print_no_newline
    jmp print_item
print_comma:
    lda #$20
    ldx #$04
print_comma_space_loop:
    jsr $ffd2
    dex
    bne print_comma_space_loop
    jsr advance
    jsr skip_space
    beq print_no_newline
    jmp print_item
print_no_newline:
    rts
print_character_function:
    jmp $e0b1
print_str_function:
    jmp $e0d0

command_run:
    ; Rebuild BASIC links before execution, including programs mounted directly
    ; into RAM. Keep the established high-byte terminator rule in relink.
    jsr relink
    jsr clear_variables
    jsr reset_data
    lda #$01
    sta $5a
    sta $6e
    lda #$08
    sta $5b
run_line:
    ldy #$01
    lda ($5a),y
    beq run_done
    lda $77
    bne run_resume
    lda $5a
    clc
    adc #$04
    sta $7a
    lda $5b
    adc #$00
    sta $7b
run_resume:
    lda #$00
    sta $77
    sta $6f
    jsr statements
    lda $6e
    beq run_done
    lda $6f
    beq run_next
    lda #$00
    sta $6f
    jmp run_line
run_next:
    ldy #$00
    lda ($5a),y
    tax
    iny
    lda ($5a),y
    sta $5b
    stx $5a
    jmp run_line
run_done:
    jmp warm_start

command_list:
    lda #$01
    sta $5a
    lda #$08
    sta $5b
list_line:
    ldy #$01
    lda ($5a),y
    beq list_done
    lda #$0d
    jsr $ffd2
    ldy #$02
    lda ($5a),y
    sta $60
    iny
    lda ($5a),y
    sta $61
    jsr print_positive       ; line numbers are unsigned 0..63999
    lda #$20
    jsr $ffd2
    lda $5a
    clc
    adc #$04
    sta $7a
    lda $5b
    adc #$00
    sta $7b
    jsr peek
    cmp #$20
    bne list_text
    jsr advance
list_text:
    jsr peek
    beq list_next
    cmp #$80
    bcc list_literal
    sec
    sbc #$80
    sta $68
    lda #<keywords
    sta $66
    lda #>keywords
    sta $67
list_find_token:
    lda $68
    beq list_token_found
    ldy #$00
list_skip_token:
    lda ($66),y
    beq list_token_end
    iny
    bne list_skip_token
list_token_end:
    iny
    tya
    clc
    adc $66
    sta $66
    bcc list_token_no_carry
    inc $67
list_token_no_carry:
    dec $68
    jmp list_find_token
list_token_found:
    lda #$00
    sta $75
list_emit_token:
    ldy $75
    lda ($66),y
    beq list_token_done
    jsr $ffd2
    inc $75
    jmp list_emit_token
list_token_done:
    jsr advance
    jmp list_text
list_literal:
    jsr $ffd2
    jsr advance
    jmp list_text
list_next:
    ldy #$00
    lda ($5a),y
    tax
    iny
    lda ($5a),y
    sta $5b
    stx $5a
    jmp list_line
list_done:
    lda #$0d
    jsr $ffd2
    jmp warm_start

command_goto:
    jsr advance
goto_number:
    jsr expression
goto_value:
    lda $60
    sta $5c
    lda $61
    sta $5d
    jsr find_line
    ldy #$01
    lda ($5e),y
    beq undefined_statement
    ldy #$02
    lda ($5e),y
    cmp $5c
    bne undefined_statement
    iny
    lda ($5e),y
    cmp $5d
    bne undefined_statement
    lda $5e
    sta $5a
    lda $5f
    sta $5b
    lda #$01
    sta $6f
    sta $6e
    rts
command_if:
    jsr advance
    jsr expression
    lda $60
    ora $61
    bne if_true
    ; A false IF discards the remainder of the source line.
if_false:
    jsr peek
    beq statement_complete
    jsr advance
    jmp if_false
if_true:
    jsr skip_space
    cmp #$a7
    beq if_then
    cmp #$89
    beq command_goto
    jmp syntax_error
if_then:
    jsr advance
    jsr skip_space
    cmp #$30
    bcc statements
    cmp #$3a
    bcs statements
    jmp goto_number

; ON selector GOTO/GOSUB line[,line...] keeps the resume position after
; the complete target list, including for the GOSUB return path.
command_on:
    jsr advance
    jsr expression
    lda $60
    sta $0356               ; 1-based target index
    lda $61
    sta $0357               ; nonzero means no selectable target
    jsr skip_space
    cmp #$89
    beq on_goto
    cmp #$8d
    bne syntax_error
    lda #$01
    bne on_mode
on_goto:
    lda #$00
on_mode:
    sta $0358               ; 0 = GOTO, 1 = GOSUB
    lda #$00
    sta $0359               ; selected-target flag starts clear
    jsr advance
on_target:
    jsr expression
    lda $0357
    bne on_target_next
    lda $0356
    cmp #$01
    bne on_target_next
    lda $60
    sta $0354
    lda $61
    sta $0355
    lda #$01
    sta $0359
on_target_next:
    dec $0356
    jsr skip_space
    cmp #$2c
    bne on_dispatch
    jsr advance
    jmp on_target
on_dispatch:
    lda $0359
    beq on_no_target
    lda $0358
    bne on_gosub_selected
    lda $0354
    sta $60
    lda $0355
    sta $61
    jmp goto_value
on_gosub_selected:
    jmp gosub_push
on_no_target:
    jmp statement_continue

command_let:
    jsr advance
command_let_body:
    jsr parse_variable
    lda $68
    sta $035c
    lda $69
    sta $035d
    jsr skip_space
    cmp #$b2
    beq let_equals
    cmp #$3d
    bne syntax_error
let_equals:
    jsr advance
    jsr is_string_variable
    bcs let_string
    jsr fp_expression
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr fp_store_variable
    jmp statement_continue
let_string:
    jsr skip_space
    cmp #$c4
    beq let_string_number
    cmp #$22
    bne syntax_error
    jsr advance
    lda $7a
    sta $64
    lda $7b
    sta $65
    lda #$00
    sta $75
let_string_scan:
    jsr peek
    beq syntax_error
    cmp #$22
    beq let_string_done
    inc $75
    jsr advance
    jmp let_string_scan
let_string_done:
    jsr advance
    jsr store_string_source
    jmp statement_continue
let_string_number:
    jsr advance
    jsr expect_open
    jsr fp_expression
    jsr expect_close
    jsr $e000
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr store_string_source
    jmp statement_continue

command_input:
    jsr advance
    jsr parse_variable
    lda $68
    sta $035c
    lda $69
    sta $035d
    lda $7a
    sta $035a
    lda $7b
    sta $035b
    lda #$3f
    jsr $ffd2
    lda #$20
    jsr $ffd2
input_read:
    jsr $ffcf
    cmp #$0d
    bne input_read
    lda #$00
    sta $0200,x
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr is_string_variable
    bcs input_string
    lda #$00
    sta $7a
    lda #$02
    sta $7b
    jsr fp_expression
    jmp input_store
input_string:
    lda #$00
    sta $64
    lda #$02
    sta $65
    ldy #$00
input_string_count:
    lda ($64),y
    beq input_string_store
    iny
    bne input_string_count
input_string_store:
    sty $75
input_store:
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr is_string_variable
    bcc input_numeric_store
    jsr store_string_source
    jmp input_restore
input_numeric_store:
    jsr fp_store_variable
input_restore:
    jsr restore_text_pointer
    jmp statement_continue

; INPUT# consumes comma- or CR-delimited fields from an already open file.
command_input_hash:
    jsr advance
    jsr expression
    ldx $60
    jsr $ffc6              ; CHKIN
    bcs input_hash_error
    jsr expect_comma
input_hash_variable:
    jsr parse_variable
    lda $68
    sta $035c
    lda $69
    sta $035d
    lda $7a
    sta $035a
    lda $7b
    sta $035b
    lda #$00
    sta $035e
input_hash_read:
    jsr $ffcf              ; CHRIN
    bcs input_hash_error
    cmp #$0d
    beq input_hash_field_end
    cmp #$2c
    beq input_hash_field_end
    ldx $035e
    cpx #$57
    bcs input_hash_error
    sta $0200,x
    inc $035e
    jsr $ffb7              ; READST
    and #$40               ; end of input
    beq input_hash_read
input_hash_field_end:
    ldx $035e
    lda #$00
    sta $0200,x
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr is_string_variable
    bcs input_hash_string
    lda #$00
    sta $7a
    lda #$02
    sta $7b
    jsr fp_expression
    jmp input_hash_store
input_hash_string:
    lda #$00
    sta $64
    lda #$02
    sta $65
    lda $035e
    sta $75
input_hash_store:
    lda $035c
    sta $68
    lda $035d
    sta $69
    jsr is_string_variable
    bcs input_hash_store_string
    jsr fp_store_variable
    jmp input_hash_restore
input_hash_store_string:
    jsr store_string_source
input_hash_restore:
    jsr restore_text_pointer
    jsr skip_space
    cmp #$2c
    bne input_hash_done
    jsr advance
    jmp input_hash_variable
input_hash_done:
    jsr $ffcc              ; CLRCHN
    jmp statement_continue
input_hash_error:
    jsr $ffcc
    jmp io_error

command_poke:
    jsr advance
    jsr expression
    lda $60
    sta $0350
    lda $61
    sta $0351
    jsr expect_comma
    jsr expression
    lda $0350
    sta $64
    lda $0351
    sta $65
    ldy #$00
    lda $60
    sta ($64),y
    jmp statement_continue

; WAIT address,mask[,xor] polls the selected memory-mapped byte.
command_wait:
    jsr advance
    jsr expression
    lda $60
    sta $0350
    lda $61
    sta $0351
    jsr expect_comma
    jsr expression
    lda $60
    sta $0352
    lda #$00
    sta $0353
    jsr skip_space
    cmp #$2c
    bne wait_begin
    jsr advance
    jsr expression
    lda $60
    sta $0353
wait_begin:
    lda $0350
    sta $64
    lda $0351
    sta $65
    ldy #$00
wait_poll:
    lda ($64),y
    eor $0353
    and $0352
    beq wait_poll
    jmp statement_continue

command_sys:
    jsr advance
    jsr expression
    lda $60
    sta $64
    lda $61
    sta $65
    lda #>(sys_return - 1)
    pha
    lda #<(sys_return - 1)
    pha
    lda $030f
    pha
    plp
    lda $030c
    ldx $030d
    ldy $030e
    jmp ($64)
sys_return:
    sta $030c
    stx $030d
    sty $030e
    php
    pla
    sta $030f
    jmp statement_continue

command_print_hash:
    jsr advance
    jsr expression
    ldx $60
    jsr $ffc9
    bcs io_error
    jsr expect_comma
    jsr print_items
    jsr $ffcc
    jmp statement_continue

command_open:
    jsr advance
    jsr expression
    lda $60
    sta $b8
    jsr expect_comma
    jsr expression
    lda $60
    sta $ba
    lda #$00
    sta $b9
    sta $b7
    jsr skip_space
    cmp #$2c
    bne open_now
    jsr advance
    jsr expression
    lda $60
    sta $b9
    jsr skip_space
    cmp #$2c
    bne open_now
    jsr advance
    jsr parse_filename
open_now:
    jsr $ffc0
    bcs io_error
    jmp statement_continue

command_close:
    jsr advance
    jsr expression
    lda $60
    jsr $ffc3
    bcs io_error
    jmp statement_continue

command_load:
    jsr advance
    lda #$00
    sta $b9
    jsr parse_file_arguments
    ldx $2b
    ldy $2c
    lda #$00
    jsr $ffd5
    bcs io_error
    stx $2d
    sty $2e
    lda $b9
    bne load_relink
    jsr clear_variables
load_relink:
    jsr relink
load_done:
    ; LOAD returns through the writable BASIC main-loop vector.
    jmp ($0302)

command_save:
    jsr advance
    lda #$01
    sta $b9
    jsr parse_file_arguments
    lda #$2b
    ldx $2d
    ldy $2e
    jsr $ffd8
    bcs io_error
    jmp statement_continue

command_verify:
    jsr advance
    lda #$00
    sta $b9
    jsr parse_file_arguments
    ldx $2b
    ldy $2c
    lda #$01
    jsr $ffd5
    bcs io_error
    jmp statement_continue

parse_file_arguments:
    lda #$01
    sta $ba
    lda #$00
    sta $b7
    jsr parse_filename
    jsr skip_space
    cmp #$2c
    bne file_arguments_done
    jsr advance
    jsr expression
    lda $60
    sta $ba
    jsr skip_space
    cmp #$2c
    bne file_arguments_done
    jsr advance
    jsr expression
    lda $60
    sta $b9
file_arguments_done:
    rts
parse_filename:
    jsr skip_space
    cmp #$22
    bne syntax_error
    jsr advance
    lda $7a
    sta $bb
    lda $7b
    sta $bc
    lda #$00
    sta $b7
filename_loop:
    jsr peek
    beq syntax_error
    cmp #$22
    beq filename_end
    inc $b7
    lda $b7
    cmp #$11
    bcs syntax_error
    jsr advance
    jmp filename_loop
filename_end:
    jmp advance

command_get:
    jsr advance
    jsr skip_space
    cmp #$23
    bne get_keyboard
    jsr advance
    jsr expression
    ldx $60
    jsr $ffc6
    bcs io_error
    jsr expect_comma
get_channel_variable:
    jsr parse_variable
    jsr $ffcf
    jsr assign_input_character
    jsr skip_space
    cmp #$2c
    bne get_channel_done
    jsr advance
    jmp get_channel_variable
get_channel_done:
    jsr $ffcc
    jmp statement_continue
get_keyboard:
    jsr parse_variable
    jsr $ffe4
    jsr assign_input_character
    jmp statement_continue
assign_input_character:
    pha
    jsr is_string_variable
    bcc assign_numeric_character
    pla
    jmp store_string_character
assign_numeric_character:
    pla
    sta $60
    lda #$00
    sta $61
    jmp store_variable

io_error:
    cmp #$04
    bne io_generic_error
    lda #<file_not_found_text
    sta $66
    lda #>file_not_found_text
    sta $67
    jmp report_error
io_generic_error:
    lda #<io_error_text
    sta $66
    lda #>io_error_text
    sta $67
    jmp report_error

; GOSUB/RETURN use a small RAM stack of BASIC line/text pointers.
command_gosub:
    jsr advance
    jsr expression
    lda $60
    sta $0354
    lda $61
    sta $0355
gosub_push:
    lda $70
    cmp #$08
    bcs out_of_memory
    asl A
    asl A
    tax
    lda $5a
    sta $0390,x
    lda $5b
    sta $0391,x
    lda $7a
    sta $0392,x
    lda $7b
    sta $0393,x
    inc $70
    lda $0354
    sta $60
    lda $0355
    sta $61
    jmp goto_value
command_return:
    lda $70
    beq syntax_error
    dec $70
    lda $70
    asl A
    asl A
    tax
    lda $0390,x
    sta $5a
    lda $0391,x
    sta $5b
    lda $0392,x
    sta $7a
    lda $0393,x
    sta $7b
    lda #$00
    sta $6f
    jmp statement_continue

; Floating-point FOR/NEXT implementation is assembled from for_float.asm.
; Integer entry point retained for addresses, loop frames and legacy
; functions. The floating-point engine returns a truncated 16-bit value here.
expression:
    jsr fp_expression
    jmp fp_to_int
factor:
    jsr skip_space
    cmp #$c3
    beq factor_len
    cmp #$c6
    beq factor_asc
    cmp #$c2
    beq factor_peek
    jmp syntax_error
factor_peek:
    jsr advance
    jsr expect_open
    jsr expression
    jsr expect_close
    ldy #$00
    lda ($60),y
    sta $60
    lda #$00
    sta $61
    rts
function_argument:
    jsr advance
    jsr expect_open
    jsr expression
    jmp expect_close

; A string argument can be a quoted literal or a simple string variable.
; Return its address in $64/$65 and its length in $75.
string_argument:
    jsr advance
    jsr expect_open
    jsr skip_space
    cmp #$c4
    beq string_argument_str
    cmp #$22
    beq string_argument_literal
    jsr parse_variable
    jsr is_string_variable
    bcc syntax_error
    jsr find_variable
    bcs string_argument_empty
    ldy #$02
    lda ($66),y
    sta $75
    iny
    lda ($66),y
    sta $64
    iny
    lda ($66),y
    sta $65
    jmp expect_close
string_argument_empty:
    lda #$00
    sta $75
    sta $64
    sta $65
    jmp expect_close
string_argument_literal:
    jsr advance
    lda $7a
    sta $64
    lda $7b
    sta $65
    lda #$00
    sta $75
string_argument_scan:
    jsr peek
    beq syntax_error
    cmp #$22
    beq string_argument_end_quote
    inc $75
    jsr advance
    jmp string_argument_scan
string_argument_end_quote:
    jsr advance
    jmp expect_close
string_argument_str:
    jmp $e0ec
factor_len:
    jsr string_argument
    lda $75
    sta $60
    lda #$00
    sta $61
    rts
factor_asc:
    jsr string_argument
    lda $75
    beq illegal_quantity
    ldy #$00
    lda ($64),y
    sta $60
    lda #$00
    sta $61
    rts
parse_decimal:
    jsr fp_parse_number
    jmp fp_to_int

parse_variable:
    jsr skip_space
    cmp #$41
    bcc syntax_error
    cmp #$5b
    bcs syntax_error
    sta $68
    lda #$00
    sta $69
    jsr advance
    jsr peek
    cmp #$41
    bcc variable_digit
    cmp #$5b
    bcs variable_digit
    sta $69
    jsr advance
    jmp variable_tail
variable_digit:
    cmp #$30
    bcc variable_tail
    cmp #$3a
    bcs variable_tail
    sta $69
    jsr advance
variable_tail:
    jsr peek
    cmp #$41
    bcc variable_tail_digit
    cmp #$5b
    bcs variable_tail_digit
    jsr advance
    jmp variable_tail
variable_tail_digit:
    cmp #$30
    bcc variable_done
    cmp #$3a
    bcs variable_done
    jsr advance
    jmp variable_tail
variable_done:
    cmp #$24
    bne variable_percent
    lda $69
    ora #$80
    sta $69
    jmp advance
variable_percent:
    cmp #$25
    bne variable_final
    lda $68
    ora #$80
    sta $68
    lda $69
    ora #$80
    sta $69
    jsr advance
variable_final:
    rts
; String names set the high bit of their second byte. Integer names set
; the high bit of both name bytes, as in the original variable table ABI.
is_string_variable:
    lda $69
    bpl variable_is_numeric
    lda $68
    bmi variable_is_numeric
    sec
    rts
variable_is_numeric:
    clc
    rts
clear_variables:
    jsr clear_variable_pointers
    lda #$00
    sta $70
    sta $77
    sta $78
clear_variables_tail:
    sta $33
    lda #$a0
    sta $34
    rts
clear_variable_pointers:
    lda $2d
    sta $2f
    sta $31
    lda $2e
    sta $30
    sta $32
    rts
find_variable:
    lda $2d
    sta $66
    lda $2e
    sta $67
find_variable_loop:
    lda $66
    cmp $2f
    bne variable_compare
    lda $67
    cmp $30
    beq variable_missing
variable_compare:
    ldy #$00
    lda ($66),y
    cmp $68
    bne variable_next
    iny
    lda ($66),y
    cmp $69
    beq variable_found
variable_next:
    clc
    lda $66
    adc #$07
    sta $66
    bcc find_variable_loop
    inc $67
    jmp find_variable_loop
variable_missing:
    sec
    rts
variable_found:
    clc
    rts
load_variable:
    jsr fp_load_variable
    jmp fp_to_int
store_variable:
    lda $60
    pha
    lda $61
    pha
    jsr fp_from_int
    jsr fp_store_variable
    pla
    sta $61
    pla
    sta $60
    rts
locate_or_create_variable:
    jsr find_variable
    bcc variable_exists
    lda $2f
    sta $66
    lda $30
    sta $67
    clc
    lda $2f
    adc #$07
    sta $2f
    sta $31
    lda $30
    adc #$00
    sta $30
    sta $32
    cmp $38
    bcs out_of_memory
    ldy #$00
    lda $68
    sta ($66),y
    iny
    lda $69
    sta ($66),y
    lda #$00
    iny
clear_new_variable:
    sta ($66),y
    iny
    cpy #$07
    bne clear_new_variable
variable_exists:
    rts

store_string_character:
    sta $74
    jsr locate_or_create_variable
    lda $33
    bne string_free_decrement
    dec $34
string_free_decrement:
    dec $33
    lda $33
    cmp $31
    bne string_free_safe
    lda $34
    cmp $32
    beq out_of_memory
string_free_safe:
    ldy #$00
    lda $74
    sta ($33),y
    ldy #$02
    lda #$01
    sta ($66),y
    iny
    lda $33
    sta ($66),y
    iny
    lda $34
    sta ($66),y
    rts
store_string_source:
    jsr locate_or_create_variable
    sec
    lda $33
    sbc $75
    sta $33
    lda $34
    sbc #$00
    sta $34
    cmp $32
    bcc out_of_memory
    bne string_source_copy
    lda $33
    cmp $31
    bcc out_of_memory
string_source_copy:
    ldy #$00
string_source_loop:
    cpy $75
    beq string_source_done
    lda ($64),y
    sta ($33),y
    iny
    bne string_source_loop
string_source_done:
    ldy #$02
    lda $75
    sta ($66),y
    iny
    lda $33
    sta ($66),y
    iny
    lda $34
    sta ($66),y
    rts
print_string_variable:
    jsr find_variable
    bcs print_variable_done
    ldy #$02
    lda ($66),y
    sta $75
    iny
    lda ($66),y
    sta $64
    iny
    lda ($66),y
    sta $65
print_string_variable_loop:
    lda $75
    beq print_variable_done
    ldy #$00
    lda ($64),y
    jsr $ffd2
    inc $64
    bne print_string_variable_next
    inc $65
print_string_variable_next:
    dec $75
    jmp print_string_variable_loop
print_variable_done:
    rts

; Print unsigned magnitude by repeated subtraction, with signed prefix.
print_word:
    lda $61
    bpl print_positive
    lda #$2d
    jsr $ffd2
    sec
    lda #$00
    sbc $60
    sta $60
    lda #$00
    sbc $61
    sta $61
print_positive:
    lda #$00
    sta $6c
    ldx #$00
print_digit_position:
    lda decimal_low,x
    sta $64
    lda decimal_high,x
    sta $65
    ldy #$00
print_subtract:
    lda $61
    cmp $65
    bcc print_digit
    bne print_subtract_now
    lda $60
    cmp $64
    bcc print_digit
print_subtract_now:
    sec
    lda $60
    sbc $64
    sta $60
    lda $61
    sbc $65
    sta $61
    iny
    jmp print_subtract
print_digit:
    cpy #$00
    bne print_emit
    lda $6c
    bne print_emit
    cpx #$04
    bne print_next_position
print_emit:
    lda #$01
    sta $6c
    tya
    clc
    adc #$30
    jsr $ffd2
print_next_position:
    inx
    cpx #$05
    bne print_digit_position
    rts

peek:
    ldy #$00
    lda ($7a),y
    rts
advance:
    inc $7a
    bne peek
    inc $7b
    jmp peek
skip_space:
    jsr peek
    cmp #$20
    bne skip_done
    jsr advance
    jmp skip_space
skip_done:
    cmp #$00
    rts
expect_comma:
    jsr skip_space
    cmp #$2c
    bne syntax_error
    jmp advance
expect_open:
    jsr skip_space
    cmp #$28
    bne syntax_error
    jmp advance
expect_close:
    jsr skip_space
    cmp #$29
    bne syntax_error
    jmp advance

undefined_statement:
    lda #<undefined_text
    sta $66
    lda #>undefined_text
    sta $67
    jmp report_error
out_of_data:
    lda #<out_of_data_text
    sta $66
    lda #>out_of_data_text
    sta $67
    jmp report_error
out_of_memory:
    lda #<memory_text
    sta $66
    lda #>memory_text
    sta $67
    jmp report_error
illegal_quantity:
    lda #<quantity_text
    sta $66
    lda #>quantity_text
    sta $67
    jmp report_error
syntax_error:
    lda #<syntax_text
    sta $66
    lda #>syntax_text
    sta $67
report_error:
    lda #$0d
    jsr $ffd2
    ldy #$00
error_text_loop:
    lda ($66),y
    beq error_text_done
    jsr $ffd2
    iny
    bne error_text_loop
error_text_done:
    ldy #$00
error_suffix_loop:
    lda error_suffix,y
    beq error_suffix_done
    jsr $ffd2
    iny
    bne error_suffix_loop
error_suffix_done:
    lda #$0d
    jsr $ffd2
    jmp warm_start

; Stack-resetting runtime clear for callers of the private BASIC entry.
; Capture both return bytes before moving either, including stack-wrap cases.
private_runtime_clear:
    pla
    tax
    pla
    sta $01fa
    stx $01f9
    ldx #$f8
    txs
    jsr $ffcc
    jsr clear_variable_pointers
    lda #$00
    sta $7a
    jsr clear_variables_tail
    ldx #$fa
    ldy $01f9
    lda #$00
    sec
    cli
    clv
    rts

error_suffix:
    .text " ERROR"
    .byte 0

; $A000 - $0801 = 38911 bytes available to a freshly cleared program.
banner:
    .byte $0d
    .text "        **** OPEN BASIC 64 ****"
    .byte $0d,$0d
    .text " 64K RAM SYSTEM  38911 BASIC BYTES FREE"
    .byte $0d,0
ready_text:
    .text "READY."
    .byte $0d,0
syntax_text:
    .text "?SYNTAX"
    .byte 0
undefined_text:
    .text "?UNDEF'D STATEMENT"
    .byte 0
memory_text:
    .text "?OUT OF MEMORY"
    .byte 0
io_error_text:
    .text "?I/O"
    .byte 0
file_not_found_text:
    .text "?FILE NOT FOUND"
    .byte 0
out_of_data_text:
    .text "?OUT OF DATA"
    .byte 0
quantity_text:
    .text "?ILLEGAL QUANTITY "
    .byte 0
decimal_low:
    .byte <10000,<1000,<100,<10,<1
decimal_high:
    .byte >10000,>1000,>100,>10,>1

; Ordered exactly as the public BASIC V2 token set.
keywords:
    .text "END"
    .byte 0
    .text "FOR"
    .byte 0
    .text "NEXT"
    .byte 0
    .text "DATA"
    .byte 0
    .text "INPUT#"
    .byte 0
    .text "INPUT"
    .byte 0
    .text "DIM"
    .byte 0
    .text "READ"
    .byte 0
    .text "LET"
    .byte 0
    .text "GOTO"
    .byte 0
    .text "RUN"
    .byte 0
    .text "IF"
    .byte 0
    .text "RESTORE"
    .byte 0
    .text "GOSUB"
    .byte 0
    .text "RETURN"
    .byte 0
    .text "REM"
    .byte 0
    .text "STOP"
    .byte 0
    .text "ON"
    .byte 0
    .text "WAIT"
    .byte 0
    .text "LOAD"
    .byte 0
    .text "SAVE"
    .byte 0
    .text "VERIFY"
    .byte 0
    .text "DEF"
    .byte 0
    .text "POKE"
    .byte 0
    .text "PRINT#"
    .byte 0
    .text "PRINT"
    .byte 0
    .text "CONT"
    .byte 0
    .text "LIST"
    .byte 0
    .text "CLR"
    .byte 0
    .text "CMD"
    .byte 0
    .text "SYS"
    .byte 0
    .text "OPEN"
    .byte 0
    .text "CLOSE"
    .byte 0
    .text "GET"
    .byte 0
    .text "NEW"
    .byte 0
    .text "TAB("
    .byte 0
    .text "TO"
    .byte 0
    .text "FN"
    .byte 0
    .text "SPC("
    .byte 0
    .text "THEN"
    .byte 0
    .text "NOT"
    .byte 0
    .text "STEP"
    .byte 0
    .text "+"
    .byte 0
    .text "-"
    .byte 0
    .text "*"
    .byte 0
    .text "/"
    .byte 0
    .text "^"
    .byte 0
    .text "AND"
    .byte 0
    .text "OR"
    .byte 0
    .text ">"
    .byte 0
    .text "="
    .byte 0
    .text "<"
    .byte 0
    .text "SGN"
    .byte 0
    .text "INT"
    .byte 0
    .text "ABS"
    .byte 0
    .text "USR"
    .byte 0
    .text "FRE"
    .byte 0
    .text "POS"
    .byte 0
    .text "SQR"
    .byte 0
    .text "RND"
    .byte 0
    .text "LOG"
    .byte 0
    .text "EXP"
    .byte 0
    .text "COS"
    .byte 0
    .text "SIN"
    .byte 0
    .text "TAN"
    .byte 0
    .text "ATN"
    .byte 0
    .text "PEEK"
    .byte 0
    .text "LEN"
    .byte 0
    .text "STR$"
    .byte 0
    .text "VAL"
    .byte 0
    .text "ASC"
    .byte 0
    .text "CHR$"
    .byte 0
    .text "LEFT$"
    .byte 0
    .text "RIGHT$"
    .byte 0
    .text "MID$"
    .byte 0
    .text "GO"
    .byte 0
