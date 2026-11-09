; SPDX-License-Identifier: Apache-2.0
; ROM RUNNER -- an original, ordinary C64 machine-code game.
; Load at $0801, SYS 2061. C64/UX's play command detects the BASIC SYS stub.
; All game logic, graphics, CIA input and SID effects execute on the 6510.
; IRQs keep the public KERNAL jiffy clock running. No emulator hooks.
; State belongs to the PRG's own RAM, beyond the assembled executable code.
; Temporary pointers $F7-$FC are untouched by the C64/UX IRQ scanner.

.equ SCREEN $f7
.equ COLOUR $f9
.equ STRING $fb

.org $0801
    .word basic_end
    .word 10
    .byte $9e
    .text "2061"
    .byte 0
basic_end:
    .word 0

entry:
    cld
    sei
    lda #$2f
    sta $00
    lda #$37
    sta $01
    lda $dd02
    ora #$03
    sta $dd02
    lda $dd00
    ora #$03
    sta $dd00
    lda #$1b
    sta $d011
    lda #$08
    sta $d016
    lda #$15
    sta $d018
    lda #$00
    sta $d015
    sta $d020
    sta $d021
    sta $c6
    sta $d404
    sta $d40b
    sta $d412
    lda #$0f
    sta $d418
    lda #$08
    sta $d403
    lda #$09
    sta $d405
    lda #$a8
    sta $d406
    lda #$00
    sta game_state
    sta old_input
    sta sound_ticks
    lda $a2
    eor #$b9
    ora #$01
    sta random_seed
    lda $a2
    sta last_tick
    cli
    jsr clear_screen
    lda #<title_text
    sta STRING
    lda #>title_text
    sta STRING+1
    jsr text_block

main_loop:
    lda $a2
    cmp last_tick
    beq main_loop
    sta last_tick
    jsr read_input
    lda old_input
    eor #$ff
    and input
    sta pressed
    lda input
    sta old_input
    and #$10
    beq main_keep_playing
    jmp quit_game
main_keep_playing:
    jsr update_sound
    lda game_state
    beq title_wait
    cmp #$02
    beq over_wait
    lda pressed
    and #$08
    beq tick_game
    jmp start_game
title_wait:
    lda pressed
    and #$04
    beq main_loop
    jmp start_game
over_wait:
    lda pressed
    and #$0c
    beq main_loop
    jmp start_game

tick_game:
    jsr erase_player
    dec move_delay
    bne move_done
    lda #$01
    sta move_delay
    lda input
    and #$03
    cmp #$01
    bne try_move_right
    lda player_x
    cmp #$02
    beq move_done
    dec player_x
    jmp move_done
try_move_right:
    cmp #$02
    bne move_done
    lda player_x
    cmp #$25
    beq move_done
    inc player_x
move_done:
    dec fall_timer
    bne fall_done
    lda fall_delay
    sta fall_timer
    ldx #$00
object_loop:
    stx object_index
    jsr erase_object
    ldx object_index
    inc fall_row,x
    lda fall_row,x
    cmp #$15
    bcc object_next
    lda fall_x,x
    sec
    sbc player_x
    clc
    adc #$01
    cmp #$03
    bcs object_missed
    lda fall_kind,x
    bne object_damage
    jsr earn_point
    jmp object_respawn
object_missed:
    lda fall_kind,x
    bne object_respawn
object_damage:
    jsr lose_life
object_respawn:
    jsr spawn_object
object_next:
    ldx object_index
    inx
    cpx #$03
    bne object_loop
fall_done:
    jsr draw_objects
    jsr draw_player
    jsr draw_hud
    lda lives
    bne main_loop
    lda #$02
    sta game_state
    lda #<game_over_text
    sta STRING
    lda #>game_over_text
    sta STRING+1
    jsr text_block
    jmp main_loop

start_game:
    lda #$01
    sta game_state
    lda #$03
    sta lives
    lda #$00
    sta score_ones
    sta score_tens
    sta score_hundreds
    lda #$14
    sta player_x
    lda #$01
    sta move_delay
    lda #$06
    sta fall_delay
    sta fall_timer
    ldx #$02
start_objects:
    lda initial_x,x
    sta fall_x,x
    lda initial_row,x
    sta fall_row,x
    lda initial_kind,x
    sta fall_kind,x
    dex
    bpl start_objects
    jsr clear_screen
    jsr draw_border
    lda #<play_text
    sta STRING
    lda #>play_text
    sta STRING+1
    jsr text_block
    jsr draw_objects
    jsr draw_player
    jsr draw_hud
    jmp main_loop

quit_game:
    lda #$00
    sta $d404
    sta $d40b
    sta $d412
    sta $d418
    sta $c6
    rts

; Read joystick 2 with undriven CIA1 lines, then scan the keyboard columns.
; IRQ is masked only for this short matrix transaction, and CIA settings
; are restored. Bits: left=1, right=2, start/fire=4, restart=8, quit=16.
read_input:
    php
    sei
    lda $dc00
    sta saved_port
    lda $dc02
    sta saved_ddra
    lda $dc03
    sta saved_ddrb
    lda #$00
    sta input
    sta $dc02
    sta $dc03
    lda $dc00
    eor #$ff
    and #$1c
    lsr a
    lsr a
    sta input
    lda #$ff
    sta $dc02
    lda #$fd
    sta $dc00
    lda $dc01
    and #$04
    bne input_no_a
    lda input
    ora #$01
    sta input
input_no_a:
    lda #$fb
    sta $dc00
    lda $dc01
    sta matrix_bits
    and #$04
    bne input_no_d
    lda input
    ora #$02
    sta input
input_no_d:
    lda matrix_bits
    and #$02
    bne input_no_r
    lda input
    ora #$08
    sta input
input_no_r:
    lda #$7f
    sta $dc00
    lda $dc01
    sta matrix_bits
    and #$10
    bne input_no_space
    lda input
    ora #$04
    sta input
input_no_space:
    lda matrix_bits
    and #$c0
    cmp #$c0
    beq input_no_quit
    lda input
    ora #$10
    sta input
input_no_quit:
    lda saved_port
    sta $dc00
    lda saved_ddra
    sta $dc02
    lda saved_ddrb
    sta $dc03
    plp
    rts

update_sound:
    lda sound_ticks
    beq sound_done
    dec sound_ticks
    bne sound_done
    lda #$00
    sta $d404
    sta $d020
sound_done:
    rts

earn_point:
    inc score_ones
    lda score_ones
    cmp #$0a
    bcc score_sound
    lda #$00
    sta score_ones
    inc score_tens
    lda fall_delay
    cmp #$03
    beq score_no_faster
    dec fall_delay
score_no_faster:
    lda score_tens
    cmp #$0a
    bcc score_sound
    lda #$00
    sta score_tens
    inc score_hundreds
    lda score_hundreds
    cmp #$0a
    bcc score_sound
    lda #$00
    sta score_hundreds
score_sound:
    lda #$00
    sta $d404
    sta $d400
    lda #$24
    sta $d401
    lda #$11
    sta $d404
    lda #$04
    sta sound_ticks
    rts

lose_life:
    lda lives
    beq life_done
    dec lives
    lda #$02
    sta $d020
    lda #$00
    sta $d404
    sta $d400
    lda #$06
    sta $d401
    lda #$21
    sta $d404
    lda #$0c
    sta sound_ticks
life_done:
    rts

random_byte:
    lda random_seed
    asl a
    bcc random_store
    eor #$1d
random_store:
    sta random_seed
    rts
spawn_object:
    jsr random_byte
    and #$3f
    cmp #$24
    bcs spawn_object
    clc
    adc #$02
    ldx object_index
    sta fall_x,x
    lda #$03
    sta fall_row,x
    ; Object 0 is always the collectible, objects 1/2 are rocks.
    ; Their staggered heights leave time to chase the next star.
    rts

clear_screen:
    ldx #$00
    lda #$20
clear_chars:
    sta $0400,x
    sta $0500,x
    sta $0600,x
    sta $0700,x
    inx
    bne clear_chars
    lda #$0e
clear_colours:
    sta $d800,x
    sta $d900,x
    sta $da00,x
    sta $db00,x
    inx
    bne clear_colours
    rts

; X=column, Y=row. Set screen/colour pointers. Does not overwrite STRING.
cell_pointer:
    txa
    clc
    adc row_low,y
    sta SCREEN
    sta COLOUR
    lda row_high,y
    adc #$00
    sta SCREEN+1
    clc
    adc #$d4
    sta COLOUR+1
    ldy #$00
    rts

; Text records are: column, row, colour, NUL-terminated ASCII uppercase.
; $FF ends a block. Printable ASCII is converted to C64 screen codes.
text_block:
    ldy #$00
    lda (STRING),y
    cmp #$ff
    beq text_done
    tax
    iny
    lda (STRING),y
    pha
    iny
    lda (STRING),y
    sta ink
    pla
    tay
    jsr cell_pointer
    lda STRING
    clc
    adc #$03
    sta STRING
    bcc text_pointer_ready
    inc STRING+1
text_pointer_ready:
    ldy #$00
text_char:
    lda (STRING),y
    beq text_next
    and #$3f
    sta (SCREEN),y
    lda ink
    sta (COLOUR),y
    iny
    bne text_char
text_next:
    iny
    tya
    clc
    adc STRING
    sta STRING
    bcc text_block
    inc STRING+1
    jmp text_block
text_done:
    rts

draw_border:
    ldx #$00
border_horizontal:
    lda #$2d
    sta $0450,x
    sta $0770,x
    inx
    cpx #$28
    bne border_horizontal
    lda #$03
    sta draw_row
border_vertical:
    ldx #$00
    ldy draw_row
    jsr cell_pointer
    lda #$3a
    sta (SCREEN),y
    ldy #$27
    sta (SCREEN),y
    inc draw_row
    lda draw_row
    cmp #$16
    bne border_vertical
    rts

erase_player:
    lda #$20
    sta player_glyph
    jmp paint_player
draw_player:
    lda #$01
    sta player_glyph
paint_player:
    ldx player_x
    dex
    ldy #$15
    jsr cell_pointer
    lda player_glyph
    cmp #$20
    beq player_left
    lda #$3c
player_left:
    sta (SCREEN),y
    lda #$03
    sta (COLOUR),y
    iny
    lda player_glyph
    sta (SCREEN),y
    lda #$03
    sta (COLOUR),y
    iny
    lda player_glyph
    cmp #$20
    beq player_right
    lda #$3e
player_right:
    sta (SCREEN),y
    lda #$03
    sta (COLOUR),y
    rts

erase_object:
    lda fall_row,x
    tay
    lda fall_x,x
    tax
    jsr cell_pointer
    lda #$20
    sta (SCREEN),y
    rts

draw_objects:
    lda #$00
    sta draw_index
draw_object:
    ldx draw_index
    lda fall_kind,x
    beq object_star
    lda #$18
    sta object_glyph
    lda #$02
    bne object_ink
object_star:
    lda #$2a
    sta object_glyph
    lda #$07
object_ink:
    sta ink
    lda fall_row,x
    tay
    lda fall_x,x
    tax
    jsr cell_pointer
    lda object_glyph
    sta (SCREEN),y
    lda ink
    sta (COLOUR),y
    inc draw_index
    lda draw_index
    cmp #$03
    bne draw_object
    rts

draw_hud:
    lda score_hundreds
    clc
    adc #$30
    sta $0430
    lda score_tens
    clc
    adc #$30
    sta $0431
    lda score_ones
    clc
    adc #$30
    sta $0432
    lda lives
    clc
    adc #$30
    sta $0440
    lda #$09
    sec
    sbc fall_delay
    clc
    adc #$30
    sta $044c
    rts

row_low:
    .byte $00,$28,$50,$78,$a0,$c8,$f0,$18,$40,$68,$90,$b8,$e0
    .byte $08,$30,$58,$80,$a8,$d0,$f8,$20,$48,$70,$98,$c0
row_high:
    .byte $04,$04,$04,$04,$04,$04,$04,$05,$05,$05,$05,$05,$05
    .byte $06,$06,$06,$06,$06,$06,$06,$07,$07,$07,$07,$07
initial_x:
    .byte 20,10,30
initial_row:
    .byte 11,6,3
initial_kind:
    .byte 0,1,1

title_text:
    .byte 11,3,3
    .text "R O M  R U N N E R\0"
    .byte 8,5,14
    .text "AN ORIGINAL C64/UX GAME\0"
    .byte 3,8,7
    .text "* CATCH THE STARS WITH YOUR <A> SHIP\0"
    .byte 3,10,2
    .text "X DODGE THE RED FALLING ROCKS\0"
    .byte 3,12,1
    .text "MISSED STAR OR ROCK HIT: LOSE 1 LIFE\0"
    .byte 3,14,1
    .text "3 LIVES. FASTER AT 10,20,30 STARS.\0"
    .byte 5,17,3
    .text "MOVE: HOLD A / D OR JOYSTICK 2\0"
    .byte 5,19,3
    .text "R RESTARTS   Q OR RUN/STOP EXITS\0"
    .byte 8,22,7
    .text "PRESS SPACE OR FIRE TO PLAY\0"
    .byte $ff
play_text:
    .byte 2,0,3
    .text "ROM RUNNER\0"
    .byte 25,0,14
    .text "CATCH * DODGE X\0"
    .byte 2,1,7
    .text "SCORE 000\0"
    .byte 18,1,3
    .text "LIVES 3\0"
    .byte 30,1,14
    .text "SPEED 3\0"
    .byte 2,23,3
    .text "A/D OR JOY2 MOVE     R RESTART\0"
    .byte 2,24,14
    .text "Q OR RUN/STOP RETURNS TO C64/UX\0"
    .byte $ff
game_over_text:
    .byte 11,8,1
    .text "                  \0"
    .byte 11,9,2
    .text "    GAME  OVER    \0"
    .byte 11,10,1
    .text "                  \0"
    .byte 6,12,7
    .text "R / SPACE / FIRE: PLAY AGAIN\0"
    .byte 6,14,3
    .text "Q / RUNSTOP: RETURN TO SHELL\0"
    .byte $ff

; Game-owned writable state, distinct from ROM/shell workspace.
game_state:     .byte 0
player_x:       .byte 20
score_ones:     .byte 0
score_tens:     .byte 0
score_hundreds: .byte 0
lives:          .byte 3
fall_delay:     .byte 6
fall_timer:     .byte 6
fall_x:         .fill 3,0
fall_row:       .fill 3,0
fall_kind:      .fill 3,0
random_seed:    .byte 1
input:          .byte 0
old_input:      .byte 0
pressed:        .byte 0
last_tick:      .byte 0
move_delay:     .byte 2
sound_ticks:    .byte 0
object_index:   .byte 0
draw_index:     .byte 0
saved_port:     .byte 0
saved_ddra:     .byte 0
saved_ddrb:     .byte 0
matrix_bits:    .byte 0
ink:            .byte 0
draw_row:       .byte 0
player_glyph:   .byte 0
object_glyph:   .byte 0
game_end:
