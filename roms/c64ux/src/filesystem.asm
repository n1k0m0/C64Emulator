; RAM filesystem: 32 nodes, 15-character names, 512 bytes per file.
; Metadata $1000-$13FF, fixed data slots $2000-$5FFF. No hidden host storage.
; SPDX-License-Identifier: Apache-2.0
.equ SEARCH_INDEX $0926
.equ MATCH_POS $0927
.equ CREATE_TYPE $0928
.equ COPY_INDEX $0929
.equ WC_LINES $0970
.equ WC_WORDS $0972
.equ WC_BYTES $0974
.equ WC_SPACE $0976

; A=node number (0..31), NODE=$1000 + A*32. X/Y preserved.
node_ptr:
    pha
    and #$07
    asl a
    asl a
    asl a
    asl a
    asl a
    sta NODE
    pla
    lsr a
    lsr a
    lsr a
    clc
    adc #$10
    sta NODE+1
    rts
; A=node number, DATA=$2000 + A*512. X/Y preserved.
data_ptr:
    asl a
    clc
    adc #$20
    sta DATA+1
    lda #$00
    sta DATA
    rts
require_file:
    pha
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    cmp #$01
    bne require_file_bad
    pla
    clc
    rts
require_file_bad:
    pla
    sec
    rts
require_directory:
    lda PATH_NODE
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    cmp #$02
    bne path_failed
    clc
    rts
path_failed:
    sec
    rts

path_begin:
    lda #$00
    sta PATH_POS
    ldy #$00
    lda (TEXT),y
    beq path_failed
    cmp #$2f
    bne path_relative
    lda #$00
    sta PATH_NODE
    clc
    rts
path_relative:
    lda CWD
    sta PATH_NODE
    clc
    rts
resolve_path:
    jsr path_begin
    bcs path_failed
resolve_next:
    ldy PATH_POS
    lda (TEXT),y
    beq path_found
    jsr require_directory
    bcs path_failed
    ldy PATH_POS
    lda (TEXT),y
    cmp #$2f
    bne resolve_component
    inc PATH_POS
    jmp resolve_next
resolve_component:
    jsr read_component
    bcs path_failed
    jsr component_special
    bcc resolve_next
    jsr lookup_component
    bcs path_failed
    sta PATH_NODE
    jmp resolve_next
path_found:
    lda PATH_NODE
    clc
    rts

read_component:
    lda #$00
    sta COMPONENT_LEN
read_component_next:
    ldy PATH_POS
    lda (TEXT),y
    beq read_component_end
    cmp #$2f
    beq read_component_end
    cmp #$20
    bcc path_failed
    cmp #$7f
    bcs path_failed
    ldx COMPONENT_LEN
    cpx #$0f
    bcs path_failed
    jsr lowercase
    sta COMPONENT,x
    inc COMPONENT_LEN
    inc PATH_POS
    jmp read_component_next
read_component_end:
    ldx COMPONENT_LEN
    lda #$00
    sta COMPONENT,x
    clc
    rts

; Handle . and .., returning C=0 when handled, C=1 for ordinary names.
component_special:
    lda COMPONENT
    cmp #$2e
    bne component_ordinary
    lda COMPONENT+1
    beq component_handled
    cmp #$2e
    bne component_ordinary
    lda COMPONENT+2
    bne component_ordinary
    lda PATH_NODE
    jsr node_ptr
    ldy #$01
    lda (NODE),y
    sta PATH_NODE
component_handled:
    clc
    rts
component_ordinary:
    sec
    rts

; Find COMPONENT in PATH_NODE. TEXT/PATH_POS remain intact.
lookup_component:
    ldx #$01
lookup_next:
    stx SEARCH_INDEX
    txa
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    beq lookup_skip
    iny
    lda (NODE),y
    cmp PATH_NODE
    bne lookup_skip
    ldy #$04
    ldx #$00
lookup_name:
    lda (NODE),y
    cmp COMPONENT,x
    bne lookup_skip
    cmp #$00
    beq lookup_found
    iny
    inx
    bne lookup_name
lookup_skip:
    ldx SEARCH_INDEX
    inx
    cpx #$20
    bne lookup_next
    sec
    rts
lookup_found:
    lda SEARCH_INDEX
    clc
    rts

; Create final component, require existing parent directories.
; Input TEXT=path, A=type. Returns C=0/A=node or C=1 without mutation.
create_path:
    sta CREATE_TYPE
    jsr path_begin
    bcs create_fail
create_next:
    jsr require_directory
    bcs create_fail
    ldy PATH_POS
    lda (TEXT),y
    beq create_fail
    cmp #$2f
    bne create_component
    inc PATH_POS
    jmp create_next
create_component:
    jsr read_component
    bcs create_fail
    ldy PATH_POS
    lda (TEXT),y
    beq create_final
    jsr component_special
    bcc create_next
    jsr lookup_component
    bcs create_fail
    sta PATH_NODE
    jmp create_next
create_final:
    jsr component_special
    bcc create_fail
    jsr lookup_component
    bcc create_fail
    ldx #$01
create_find_slot:
    txa
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    beq create_slot
    inx
    cpx #$20
    bne create_find_slot
create_fail:
    sec
    rts
create_slot:
    stx FREE_NODE
    lda CREATE_TYPE
    sta (NODE),y
    iny
    lda PATH_NODE
    sta (NODE),y
    iny
    lda #$00
    sta (NODE),y
    iny
    sta (NODE),y
    iny
    ldx #$00
create_name:
    lda COMPONENT,x
    sta (NODE),y
    beq create_success
    inx
    iny
    bne create_name
create_success:
    lda FREE_NODE
    clc
    rts

; A=ASCII byte, REDIRECT=node. Bounded write, no wrap to the next slot.
; Clobbers NODE, DATA, TARGET and SAVED_CHAR. No parser/loop variables.
append_byte:
    sta SAVED_CHAR
    lda REDIRECT
    jsr node_ptr
    ldy #$02
    lda (NODE),y
    sta TARGET
    iny
    lda (NODE),y
    cmp #$02
    bcs append_full
    sta TARGET+1
    lda REDIRECT
    jsr data_ptr
    clc
    lda DATA+1
    adc TARGET+1
    sta DATA+1
    ldy TARGET
    lda SAVED_CHAR
    sta (DATA),y
    ldy #$02
    lda TARGET
    clc
    adc #$01
    sta (NODE),y
    bcc append_success
    iny
    lda TARGET+1
    adc #$00
    sta (NODE),y
append_success:
    clc
    rts
append_full:
    sec
    rts

print_path:
    ldx #$00
path_walk:
    cmp #$00
    beq path_print_begin
    sta PATH_STACK,x
    inx
    jsr node_ptr
    ldy #$01
    lda (NODE),y
    cpx #$20
    bcc path_walk
path_print_begin:
    stx PATH_DEPTH
    cpx #$00
    bne path_print_part
    lda #$2f
    jmp putc
path_print_part:
    lda #$2f
    jsr putc
    dec PATH_DEPTH
    ldx PATH_DEPTH
    lda PATH_STACK,x
    jsr node_ptr
    jsr print_node_name
    lda PATH_DEPTH
    bne path_print_part
    rts
print_node_name:
    ldy #$04
node_name_next:
    lda (NODE),y
    beq node_name_done
    jsr putc
    iny
    bne node_name_next
node_name_done:
    rts

cmd_cd:
    lda ARGC
    cmp #$01
    beq cd_home
    cmp #$02
    bne fs_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs fs_path_error
    sta CURRENT
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    cmp #$02
    bne fs_not_dir
    lda CURRENT
    sta CWD
    rts
cd_home:
    point home_path
    jsr resolve_path
    bcs fs_path_error
    sta CWD
    rts
fs_usage:
    jmp usage_error
fs_path_error:
    jmp path_error
fs_not_dir:
    jmp not_dir_error
fs_file_error:
    jmp file_error

cmd_ls:
    lda ARGC
    cmp #$01
    beq ls_current
    cmp #$02
    bne fs_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs fs_path_error
    jmp ls_target
ls_current:
    lda CWD
ls_target:
    sta CURRENT
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    cmp #$02
    bne ls_one
    ldx #$01
ls_next:
    stx ITER
    txa
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    beq ls_skip
    iny
    lda (NODE),y
    cmp CURRENT
    bne ls_skip
    jsr ls_one
ls_skip:
    ldx ITER
    inx
    cpx #$20
    bne ls_next
    rts
ls_one:
    ldy #$00
    lda (NODE),y
    cmp #$02
    bne ls_file
    lda #$64
    jsr putc
    jsr space
    jsr print_node_name
    lda #$2f
    jsr putc
    jmp newline
ls_file:
    lda #$2d
    jsr putc
    jsr space
    jsr print_node_name
    jsr space
    ldy #$02
    lda (NODE),y
    sta NUMBER
    iny
    lda (NODE),y
    sta NUMBER+1
    jsr print_number
    jmp newline

cmd_touch:
    lda #$01
    bne create_command
cmd_mkdir:
    lda #$02
create_command:
    sta CURRENT
    lda ARGC
    cmp #$02
    bne fs_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs create_command_new
    jsr node_ptr
    ldy #$00
    lda CURRENT
    cmp #$01
    bne fs_file_error
    lda (NODE),y
    cmp #$01
    bne fs_file_error
    rts
create_command_new:
    ldx #$01
    jsr argptr
    lda CURRENT
    jsr create_path
    bcs fs_path_error
    rts

cmd_cat:
    jsr get_file_argument
    bcs cat_return
    sta CURRENT
    jsr data_ptr
    ldy #$02
    lda (NODE),y
    sta NUMBER
    iny
    lda (NODE),y
    sta NUMBER+1
    ldy #$00
cat_next:
    lda NUMBER
    ora NUMBER+1
    beq cat_return
    lda (DATA),y
    jsr putc
    inc DATA
    bne cat_advance
    inc DATA+1
cat_advance:
    jsr count_down
    jmp cat_next
cat_return:
    rts
get_file_argument:
    lda ARGC
    cmp #$02
    bne get_file_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs get_file_path
    jsr require_file
    bcs get_file_bad
    clc
    rts
get_file_usage:
    jmp usage_error
get_file_path:
    jmp path_error
get_file_bad:
    jmp file_error
count_down:
    lda NUMBER
    bne count_down_low
    dec NUMBER+1
count_down_low:
    dec NUMBER
    rts

cmd_rm:
    jsr get_file_argument
    bcs rm_return
    ldy #$00
    lda #$00
    sta (NODE),y
rm_return:
    rts
cmd_rmdir:
    lda ARGC
    cmp #$02
    bne rmdir_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs rmdir_path
    cmp #$00
    beq rmdir_protected
    cmp CWD
    beq rmdir_protected
    sta CURRENT
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    cmp #$02
    bne rmdir_not_dir
    ldx #$01
rmdir_scan:
    txa
    jsr node_ptr
    ldy #$00
    lda (NODE),y
    beq rmdir_next
    iny
    lda (NODE),y
    cmp CURRENT
    beq rmdir_not_empty
rmdir_next:
    inx
    cpx #$20
    bne rmdir_scan
    lda CURRENT
    jsr node_ptr
    ldy #$00
    lda #$00
    sta (NODE),y
    rts
rmdir_usage:
    jmp usage_error
rmdir_path:
    jmp path_error
rmdir_not_dir:
    jmp not_dir_error
rmdir_protected:
    point msg_busy
    jmp error
rmdir_not_empty:
    point msg_not_empty
    jmp error

cmd_cp:
    lda #$00
    beq copy_command
cmd_mv:
    lda #$01
copy_command:
    sta COPY_INDEX
    lda ARGC
    cmp #$03
    bne copy_usage
    ldx #$01
    jsr argptr
    jsr resolve_path
    bcs copy_path
    jsr require_file
    bcs copy_file
    sta SRC_NODE
    ldx #$02
    jsr argptr
    jsr resolve_path
    bcc copy_existing
    ldx #$02
    jsr argptr
    lda #$01
    jsr create_path
    bcs copy_path
copy_existing:
    cmp SRC_NODE
    beq copy_same
    jsr require_file
    bcs copy_file
    sta DST_NODE
    jsr data_ptr
    lda DATA
    sta OTHER
    lda DATA+1
    sta OTHER+1
    lda SRC_NODE
    jsr data_ptr
    ldx #$02
    ldy #$00
copy_page:
    lda (DATA),y
    sta (OTHER),y
    iny
    bne copy_page
    inc DATA+1
    inc OTHER+1
    dex
    bne copy_page
    lda SRC_NODE
    jsr node_ptr
    ldy #$02
    lda (NODE),y
    sta NUMBER
    iny
    lda (NODE),y
    sta NUMBER+1
    lda DST_NODE
    jsr node_ptr
    ldy #$02
    lda NUMBER
    sta (NODE),y
    iny
    lda NUMBER+1
    sta (NODE),y
    lda COPY_INDEX
    beq copy_done
    lda SRC_NODE
    jsr node_ptr
    ldy #$00
    lda #$00
    sta (NODE),y
copy_done:
    rts
copy_usage:
    jmp usage_error
copy_path:
    jmp path_error
copy_file:
    jmp file_error
copy_same:
    point msg_same
    jmp error

cmd_wc:
    jsr get_file_argument
    bcs wc_return
    jsr data_ptr
    ldy #$02
    lda (NODE),y
    sta NUMBER
    sta WC_BYTES
    iny
    lda (NODE),y
    sta NUMBER+1
    sta WC_BYTES+1
    lda #$00
    sta WC_LINES
    sta WC_LINES+1
    sta WC_WORDS
    sta WC_WORDS+1
    lda #$01
    sta WC_SPACE
    ldy #$00
wc_next:
    lda NUMBER
    ora NUMBER+1
    beq wc_print
    lda (DATA),y
    cmp #$0a
    bne wc_not_lf
    inc WC_LINES
    bne wc_whitespace
    inc WC_LINES+1
wc_whitespace:
    lda #$01
    sta WC_SPACE
    bne wc_advance
wc_not_lf:
    cmp #$21
    bcc wc_whitespace
    lda WC_SPACE
    beq wc_advance
    inc WC_WORDS
    bne wc_new_word
    inc WC_WORDS+1
wc_new_word:
    lda #$00
    sta WC_SPACE
wc_advance:
    inc DATA
    bne wc_pointer_ok
    inc DATA+1
wc_pointer_ok:
    jsr count_down
    jmp wc_next
wc_print:
    lda WC_LINES
    sta NUMBER
    lda WC_LINES+1
    sta NUMBER+1
    jsr print_number
    jsr space
    lda WC_WORDS
    sta NUMBER
    lda WC_WORDS+1
    sta NUMBER+1
    jsr print_number
    jsr space
    lda WC_BYTES
    sta NUMBER
    lda WC_BYTES+1
    sta NUMBER+1
    jsr print_number
    jmp newline
wc_return:
    rts

cmd_hexdump:
    jsr get_file_argument
    bcs hexdump_return
    jsr data_ptr
    ldy #$02
    lda (NODE),y
    sta NUMBER
    iny
    lda (NODE),y
    sta NUMBER+1
    lda #$00
    sta WC_BYTES
    sta WC_BYTES+1
hexdump_next:
    lda NUMBER
    ora NUMBER+1
    beq hexdump_done
    lda WC_BYTES
    and #$07
    bne hexdump_byte
    lda WC_BYTES+1
    jsr print_hex
    lda WC_BYTES
    jsr print_hex
    jsr space
hexdump_byte:
    jsr space
    ldy #$00
    lda (DATA),y
    jsr print_hex
    inc DATA
    bne hexdump_pointer_ok
    inc DATA+1
hexdump_pointer_ok:
    inc WC_BYTES
    bne hexdump_offset_ok
    inc WC_BYTES+1
hexdump_offset_ok:
    jsr count_down
    lda WC_BYTES
    and #$07
    bne hexdump_next
    jsr newline
    jmp hexdump_next
hexdump_done:
    lda WC_BYTES
    and #$07
    beq hexdump_return
    jsr newline
hexdump_return:
    rts

fs_seed:
    ldx #$00
seed_metadata:
    lda seed_nodes,x
    sta $1000,x
    inx
    bne seed_metadata
    point readme_text
    lda #$05
    jsr seed_text
    point motd_text
    lda #$06
    jsr seed_text
    point demo_script
    lda #$07
seed_text:
    sta REDIRECT
    ldy #$00
seed_text_next:
    lda (TEXT),y
    beq seed_text_done
    tya
    pha
    lda (TEXT),y
    jsr append_byte
    pla
    tay
    iny
    bne seed_text_next
    inc TEXT+1
    jmp seed_text_next
seed_text_done:
    lda #$ff
    sta REDIRECT
    rts

seed_nodes:
    .byte 2,0,0,0
    .fill 28,0
    .byte 2,0,0,0
    .text "home"
    .fill 24,0
    .byte 2,1,0,0
    .text "root"
    .fill 24,0
    .byte 2,0,0,0
    .text "tmp"
    .fill 25,0
    .byte 2,0,0,0
    .text "etc"
    .fill 25,0
    .byte 1,0,0,0
    .text "readme"
    .fill 22,0
    .byte 1,4,0,0
    .text "motd"
    .fill 24,0
    .byte 1,2,0,0
    .text "hello.sh"
    .fill 20,0
