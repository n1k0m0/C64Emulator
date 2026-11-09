; C64/UX 0.2 -- compact Unix-like ROM shell for the NMOS 6510.
; SPDX-License-Identifier: Apache-2.0
;
; ASCII text internally, PETSCII conversion at the terminal boundary.
; ROM: $A000-$BFFF. KERNAL: $E000-$FFFF. No emulator host services.

.equ TEXT $02
.equ NODE $04
.equ DATA $06
.equ OTHER $08
.equ NUMBER $0a
.equ WORK $0c
.equ TARGET $0e
.equ LINE $0800
.equ HISTORY $0a00
.equ COMPONENT $0b00
.equ SCRIPT $0c00
.equ TOKENS $0e00
.equ DISKBUF $0f00
.equ ARGC $0900
.equ INPUT_POS $0901
.equ OUTPUT_POS $0902
.equ QUOTE $0903
.equ REDIRECT $0904
.equ APPEND $0905
.equ STATUS $0906
.equ CWD $0907
.equ CURRENT $0908
.equ PARENT $0909
.equ FREE_NODE $090a
.equ LINE_LEN $090b
.equ SAVED_CHAR $090c
.equ HISTORY_LEN $090d
.equ SCRIPT_ACTIVE $090e
.equ SCRIPT_POS $090f
.equ SCRIPT_LEN $0911
.equ PATH_POS $0913
.equ COMPONENT_LEN $0914
.equ PATH_NODE $0915
.equ ITER $0916
.equ SRC_NODE $0917
.equ DST_NODE $0918
.equ TEMP $0919
.equ TEMP2 $091a
.equ LAST_OUTPUT $091b
.equ OVERFLOW $091c
.equ ENTRY_POINT $091d
.equ PROGRAM_END $091f
.equ PROGRAM_READY $0921
.equ SAVED_SP $0922
.equ DISK_STATUS $0923
.equ DISK_MODE $0924
.equ DISK_LEN $0925
.equ DIGITS $0930
.equ PATH_STACK $0940
.equ PATH_DEPTH $0960
.equ ARGV $0980
.equ ARG_FLAGS $0988
.equ LINE_TOO_LONG $096b

.org $a000
    .word cold_start,warm_start
    .text "C64UX"
    .byte 0
    jmp cold_start
    jmp warm_start

cold_start:
    sei
    cld
    ldx #$ff
    txs
    lda #$00
    tax
cold_clear:
    sta $0900,x
    sta $1000,x
    sta $1100,x
    sta $1200,x
    sta $1300,x
    inx
    bne cold_clear
    lda #$ff
    sta REDIRECT
    jsr fs_seed
    lda #$02
    sta CWD
    cli
    jsr terminal_clear
    say banner
    jsr play_boot_notice
    jmp shell_prompt

warm_start:
    sei
    cld
    ldx #$ff
    txs
    lda #$37
    sta $01
    jsr $ffcc
    lda #$00
    sta SCRIPT_ACTIVE
    sta PROGRAM_READY
    lda #$ff
    sta REDIRECT
    cli
    jsr terminal_style
    say warm_message
    jmp shell_prompt

shell_prompt:
    lda #$ff
    sta REDIRECT
    say prompt_prefix
    lda CWD
    jsr print_path
    say prompt_suffix
    lda #$00
    sta LINE_LEN
    sta LINE_TOO_LONG
shell_wait:
    jsr $ffe4
    cmp #$00
    beq shell_wait
    cmp #$0d
    beq shell_enter
    cmp #$14
    beq shell_delete
    cmp #$08
    beq shell_delete
    cmp #$03
    beq shell_cancel
    cmp #$15
    beq shell_kill_line
    cmp #$0c
    beq shell_clear_line
    cmp #$91
    beq shell_history
    cmp #$20
    bcc shell_wait
    cmp #$c1
    bcc input_lower
    cmp #$db
    bcs shell_wait
    sec
    sbc #$80
    jmp input_store
input_lower:
    cmp #$41
    bcc input_store
    cmp #$5b
    bcs input_store
    ora #$20
input_store:
    cmp #$7f
    bcs shell_wait
    ldx LINE_LEN
    cpx #$7f
    bcs input_overflow
    sta LINE,x
    inc LINE_LEN
    jsr putc
    jmp shell_wait
input_overflow:
    lda #$01
    sta LINE_TOO_LONG
    jmp shell_wait

shell_delete:
    lda LINE_LEN
    beq shell_wait
    dec LINE_LEN
    jsr erase_character
    jmp shell_wait
shell_kill_line:
    lda LINE_LEN
    beq shell_wait
    dec LINE_LEN
    jsr erase_character
    jmp shell_kill_line
shell_clear_line:
    jsr terminal_clear
    jmp shell_prompt
shell_cancel:
    say cancel_message
    jmp shell_prompt
shell_history:
    lda LINE_LEN
    beq history_copy
    dec LINE_LEN
    jsr erase_character
    jmp shell_history
history_copy:
    ldx #$00
history_copy_next:
    cpx HISTORY_LEN
    beq history_copied
    lda HISTORY,x
    sta LINE,x
    jsr putc
    inx
    bne history_copy_next
history_copied:
    stx LINE_LEN
    jmp shell_wait
shell_enter:
    lda LINE_TOO_LONG
    beq shell_enter_valid
    jsr newline
    point msg_line_long
    jsr error
    jmp shell_prompt
shell_enter_valid:
    ldx LINE_LEN
    lda #$00
    sta LINE,x
    stx HISTORY_LEN
    ldx #$00
history_save:
    lda LINE,x
    sta HISTORY,x
    inx
    cpx #$80
    bne history_save
    jsr newline
    jsr execute_line
command_done:
    jmp shell_prompt

; Backspace works across the physical 40-column screen boundary.
erase_character:
    lda $d3
    bne erase_same_row
    lda $d6
    beq erase_done
    dec $d6
    lda #$28
    sta $d3
erase_same_row:
    dec $d3
    lda $d3
    pha
    lda $d6
    pha
    lda #$20
    jsr $ffd2
    pla
    sta $d6
    pla
    sta $d3
erase_done:
    rts

terminal_clear:
    jsr $ff81
terminal_style:
    lda #$00
    sta $d020
    sta $d021
    lda #$05
    sta $0286
    lda #$17
    sta $d018
    rts

; putc preserves A/X/Y. TEXT is never clobbered. LF is ASCII newline.
putc:
    sta SAVED_CHAR
    pha
    txa
    pha
    tya
    pha
    lda NODE
    pha
    lda NODE+1
    pha
    lda DATA
    pha
    lda DATA+1
    pha
    lda TARGET
    pha
    lda TARGET+1
    pha
    lda SAVED_CHAR
    sta LAST_OUTPUT
    lda REDIRECT
    cmp #$ff
    beq putc_terminal
    lda SAVED_CHAR
    jsr append_byte
    bcc putc_return
    lda #$01
    sta OVERFLOW
    bne putc_return
putc_terminal:
    lda SAVED_CHAR
    cmp #$0a
    bne putc_lower
    lda #$0d
    bne putc_emit
putc_lower:
    cmp #$61
    bcc putc_upper
    cmp #$7b
    bcs putc_emit
    sec
    sbc #$20
    bne putc_emit
putc_upper:
    cmp #$41
    bcc putc_emit
    cmp #$5b
    bcs putc_emit
    ora #$80
putc_emit:
    jsr $ffd2
putc_return:
    pla
    sta TARGET+1
    pla
    sta TARGET
    pla
    sta DATA+1
    pla
    sta DATA
    pla
    sta NODE+1
    pla
    sta NODE
    pla
    tay
    pla
    tax
    pla
    rts

newline:
    lda #$0a
    jmp putc
space:
    lda #$20
    jmp putc
puts:
    ldy #$00
puts_next:
    lda (TEXT),y
    beq puts_done
    jsr putc
    iny
    bne puts_next
    inc TEXT+1
    jmp puts_next
puts_done:
    rts

; Explicit errors always go to the console, independent of redirection.
error:
    lda #$01
    sta STATUS
    lda REDIRECT
    pha
    lda #$ff
    sta REDIRECT
    jsr puts
    jsr newline
    pla
    sta REDIRECT
    sec
    rts
usage_error:
    point msg_usage
    jmp error
path_error:
    point msg_path
    jmp error
file_error:
    point msg_file
    jmp error
full_error:
    point msg_full
    jmp error
not_dir_error:
    point msg_not_dir
    jmp error

; ASCII case fold for command/path comparisons.
lowercase:
    cmp #$41
    bcc lowercase_done
    cmp #$5b
    bcs lowercase_done
    ora #$20
lowercase_done:
    rts

; X=argument index => TEXT points into the separate token buffer.
argptr:
    lda ARGV,x
    sta TEXT
    lda #$0e
    sta TEXT+1
    rts

; Quotes group arguments. > and >> are standalone tokens without spaces.
tokenize:
    lda #$00
    sta ARGC
    sta INPUT_POS
    sta OUTPUT_POS
token_skip:
    ldx INPUT_POS
    lda LINE,x
    beq token_success
    cmp #$20
    bne token_start
    inc INPUT_POS
    bne token_skip
token_start:
    ldx ARGC
    cpx #$08
    bcs token_fail
    lda OUTPUT_POS
    sta ARGV,x
    lda #$00
    sta ARG_FLAGS,x
    inc ARGC
    lda #$00
    sta QUOTE
    ldx INPUT_POS
    lda LINE,x
    cmp #$3e
    beq token_redirect
token_char:
    ldx INPUT_POS
    lda LINE,x
    beq token_end_input
    ldy QUOTE
    bne token_in_quote
    cmp #$20
    beq token_end
    cmp #$3e
    beq token_end
    cmp #$22
    beq token_open_quote
    cmp #$27
    beq token_open_quote
token_store:
    ldy OUTPUT_POS
    sta TOKENS,y
    inc OUTPUT_POS
    inc INPUT_POS
    jmp token_char
token_open_quote:
    sta QUOTE
    inc INPUT_POS
    jmp token_char
token_in_quote:
    cmp QUOTE
    bne token_store
    lda #$00
    sta QUOTE
    inc INPUT_POS
    jmp token_char
token_end_input:
    lda QUOTE
    bne token_fail
token_end:
    ldy OUTPUT_POS
    lda #$00
    sta TOKENS,y
    inc OUTPUT_POS
    jmp token_skip
token_redirect:
    ldx ARGC
    dex
    lda #$01
    sta ARG_FLAGS,x
    lda #$3e
    ldy OUTPUT_POS
    sta TOKENS,y
    inc OUTPUT_POS
    inc INPUT_POS
    ldx INPUT_POS
    lda LINE,x
    cmp #$3e
    bne token_end
    ldy OUTPUT_POS
    sta TOKENS,y
    inc OUTPUT_POS
    inc INPUT_POS
    jmp token_end
token_success:
    clc
    rts
token_fail:
    point msg_parse
    jmp error

execute_line:
    lda #$00
    sta STATUS
    sta OVERFLOW
    lda #$ff
    sta REDIRECT
    jsr tokenize
    bcs execute_return
    lda ARGC
    beq execute_return
    jsr setup_redirect
    bcs execute_return
    lda #<command_table
    sta OTHER
    lda #>command_table
    sta OTHER+1
dispatch_next:
    ldy #$00
    lda (OTHER),y
    sta TEXT
    iny
    lda (OTHER),y
    sta TEXT+1
    ora TEXT
    beq dispatch_missing
    ldx #$00
    ldy ARGV
dispatch_compare:
    lda TOKENS,y
    jsr lowercase
    sta TEMP
    tya
    pha
    txa
    tay
    lda (TEXT),y
    cmp TEMP
    bne dispatch_different
    cmp #$00
    beq dispatch_found
    pla
    tay
    iny
    inx
    bne dispatch_compare
dispatch_different:
    pla
    clc
    lda OTHER
    adc #$04
    sta OTHER
    bcc dispatch_next
    inc OTHER+1
    jmp dispatch_next
dispatch_found:
    pla
    ldy #$02
    lda (OTHER),y
    sta WORK
    iny
    lda (OTHER),y
    sta WORK+1
    jsr call_command
    lda OVERFLOW
    beq execute_return
    point msg_file_full
    jsr error
execute_return:
    lda #$ff
    sta REDIRECT
    rts
dispatch_missing:
    point msg_unknown
    jmp error
call_command:
    jmp (WORK)

setup_redirect:
    ldx #$01
redirect_scan:
    cpx ARGC
    bcs redirect_none
    stx TEMP2
    lda ARG_FLAGS,x
    beq redirect_skip
    jsr argptr
    ldy #$00
    lda (TEXT),y
    cmp #$3e
    beq redirect_found
redirect_skip:
    ldx TEMP2
    inx
    bne redirect_scan
redirect_found:
    iny
    lda (TEXT),y
    sta APPEND
    beq redirect_validate
    cmp #$3e
    bne redirect_bad
    iny
    lda (TEXT),y
    bne redirect_bad
redirect_validate:
    lda TEMP2
    clc
    adc #$02
    cmp ARGC
    bne redirect_bad
    ldx TEMP2
    inx
    jsr argptr
    jsr resolve_path
    bcc redirect_exists
    ldx TEMP2
    inx
    jsr argptr
    lda #$01
    jsr create_path
    bcs redirect_bad_path
redirect_exists:
    sta REDIRECT
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    cmp #$01
    bne redirect_bad_file
    lda APPEND
    bne redirect_ready
    lda #$00
    ldy #$02
    sta (NODE),y
    iny
    sta (NODE),y
redirect_ready:
    lda TEMP2
    sta ARGC
redirect_none:
    clc
    rts
redirect_bad_file:
    lda #$ff
    sta REDIRECT
    jmp file_error
redirect_bad_path:
    jmp path_error
redirect_bad:
    jmp usage_error

cmd_help:
    say help_text
    rts
cmd_uname:
    say version_text
    rts
cmd_whoami:
    say user_text
    rts
cmd_hostname:
    say host_text
    rts
cmd_echo:
    ldx #$01
echo_next:
    cpx ARGC
    bcs echo_done
    stx ITER
    jsr argptr
    jsr puts
    ldx ITER
    inx
    cpx ARGC
    bcs echo_done
    jsr space
    jmp echo_next
echo_done:
    jmp newline
cmd_clear:
    jmp terminal_clear
cmd_pwd:
    lda CWD
    jsr print_path
    jmp newline
cmd_ps:
    say ps_text
    rts
cmd_mem:
    lda #$00
    sta NUMBER
    sta NUMBER+1
    sta TEMP2
    ldx #$00
mem_count:
    txa
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    beq mem_next
    inc TEMP2
    cmp #$01
    bne mem_next
    ldy #$02
    clc
    lda (NODE),y
    adc NUMBER
    sta NUMBER
    iny
    lda (NODE),y
    adc NUMBER+1
    sta NUMBER+1
mem_next:
    inx
    cpx #$20
    bne mem_count
    lda NUMBER
    pha
    lda NUMBER+1
    pha
    say mem_text
    lda TEMP2
    sta NUMBER
    lda #$00
    sta NUMBER+1
    jsr print_number
    say mem_nodes
    pla
    sta NUMBER+1
    pla
    sta NUMBER
    jsr print_number
    say mem_bytes
    rts

cmd_uptime:
    php
    sei
    jsr $ffde
    plp
    ; 24-bit jiffies / 60 => 24-bit seconds (max 279620).
    sta WORK
    stx TARGET
    sty TARGET+1
    lda #$00
    sta NUMBER
    ldx #$18
uptime_divide:
    asl WORK
    rol TARGET
    rol TARGET+1
    rol NUMBER
    lda NUMBER
    cmp #$3c
    bcc uptime_next
    sbc #$3c
    sta NUMBER
    inc WORK
uptime_next:
    dex
    bne uptime_divide
    ; Format by repeated 24-bit division, no uptime wrap at 65535 sec.
    lda #$00
    sta TEMP2
uptime_decimal:
    lda #$00
    sta NUMBER
    ldx #$18
uptime_decimal_bits:
    asl WORK
    rol TARGET
    rol TARGET+1
    rol NUMBER
    lda NUMBER
    cmp #$0a
    bcc uptime_decimal_next
    sbc #$0a
    sta NUMBER
    inc WORK
uptime_decimal_next:
    dex
    bne uptime_decimal_bits
    ldx TEMP2
    lda NUMBER
    ora #$30
    sta DIGITS,x
    inc TEMP2
    lda WORK
    ora TARGET
    ora TARGET+1
    bne uptime_decimal
    say uptime_prefix
    ldx TEMP2
uptime_emit:
    dex
    lda DIGITS,x
    jsr putc
    cpx #$00
    bne uptime_emit
    say uptime_suffix
    rts

; Print unsigned 16-bit NUMBER without leading zeroes.
print_number:
    ldx #$00
number_digit:
    lda #$00
    sta WORK
    ldy #$10
number_divide:
    asl NUMBER
    rol NUMBER+1
    rol WORK
    lda WORK
    cmp #$0a
    bcc number_bit_next
    sbc #$0a
    sta WORK
    inc NUMBER
number_bit_next:
    dey
    bne number_divide
    lda WORK
    ora #$30
    sta DIGITS,x
    inx
    lda NUMBER
    ora NUMBER+1
    bne number_digit
number_emit:
    dex
    lda DIGITS,x
    jsr putc
    cpx #$00
    bne number_emit
    rts
print_hex:
    pha
    lsr a
    lsr a
    lsr a
    lsr a
    jsr print_nibble
    pla
    and #$0f
print_nibble:
    cmp #$0a
    bcc hex_digit
    clc
    adc #$27
hex_digit:
    clc
    adc #$30
    jmp putc

cmd_reboot:
    jmp ($fffc)

; A shell script is copied before execution, so editing/removing it is safe.
; Nested scripts are rejected. The parser/dispatch is shared with the console.
cmd_sh:
    lda REDIRECT
    cmp #$ff
    bne script_redirect
    lda SCRIPT_ACTIVE
    bne script_nested
    lda ARGC
    cmp #$02
    bne script_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs script_path
    jsr require_file
    bcs script_file
    jsr data_ptr
    ldy #$02
    lda (NODE),y
    sta SCRIPT_LEN
    iny
    lda (NODE),y
    sta SCRIPT_LEN+1
    ldy #$00
script_copy:
    lda (DATA),y
    sta SCRIPT,y
    iny
    bne script_copy
    inc DATA+1
script_copy_second:
    lda (DATA),y
    sta SCRIPT+$100,y
    iny
    bne script_copy_second
    lda #$00
    sta SCRIPT_POS
    sta SCRIPT_POS+1
    lda #$01
    sta SCRIPT_ACTIVE
script_next_line:
    lda #$00
    sta LINE_LEN
script_read:
    lda SCRIPT_POS
    cmp SCRIPT_LEN
    bne script_get
    lda SCRIPT_POS+1
    cmp SCRIPT_LEN+1
    beq script_end
script_get:
    lda SCRIPT_POS
    sta TEXT
    lda SCRIPT_POS+1
    clc
    adc #$0c
    sta TEXT+1
    ldy #$00
    lda (TEXT),y
    inc SCRIPT_POS
    bne script_have
    inc SCRIPT_POS+1
script_have:
    cmp #$0d
    beq script_execute
    cmp #$0a
    beq script_execute
    ldx LINE_LEN
    cpx #$7f
    bcs script_long
    sta LINE,x
    inc LINE_LEN
    jmp script_read
script_execute:
    ldx LINE_LEN
    lda #$00
    sta LINE,x
    lda LINE
    cmp #$23
    beq script_next_line
    jsr execute_line
    lda STATUS
    bne script_finish
    jmp script_next_line
script_end:
    lda LINE_LEN
    beq script_finish
    ldx LINE_LEN
    lda #$00
    sta LINE,x
    lda LINE
    cmp #$23
    beq script_finish
    jsr execute_line
script_finish:
    lda #$00
    sta SCRIPT_ACTIVE
    rts
script_long:
    point msg_script_long
    jsr error
    jmp script_finish
script_nested:
    point msg_script_nested
    jmp error
script_redirect:
    point msg_script_redirect
    jmp error
script_usage:
    jmp usage_error
script_path:
    jmp path_error
script_file:
    jmp file_error
