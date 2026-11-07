; SPDX-License-Identifier: Apache-2.0
; Native 6510 BASIC floating-point functions. All operands/results use FAC.
.org $e060
fp_math_dispatch:
    cmp #$c5
    beq fp_math_val
    cmp #$c2
    bcc fp_math_parse
    cmp #$c4
    bcs fp_math_syntax
    jsr factor
    jmp fp_from_int
fp_math_val:
    jmp fp_val
fp_math_syntax:
    jmp syntax_error
fp_math_parse:
    pha
    jsr fp_function_argument
    pla
    cmp #$bb
    beq fp_math_rnd
    ; Math functions consume the rounded literal value. RND uses the
    ; original argument bits for its seed and is dispatched above.
    pha
    jsr fp_literal_round
    pla
    cmp #$bc
    beq fp_math_log
    cmp #$bd
    beq fp_math_exp
    cmp #$be
    beq fp_math_cos
    cmp #$bf
    beq fp_math_sin
    jmp $f101
fp_math_dispatch_end:

.org $e0b1
fp_math_print_character:
    jsr function_argument
    lda $61
    beq fp_math_character_valid
    jmp illegal_quantity
fp_math_character_valid:
    lda $60
    jsr $ffd2
    jmp print_separator
fp_math_char_end:

.org $e0d0
fp_math_print_str:
    jsr advance
    jsr expect_open
    jsr fp_expression
    jsr expect_close
    lda $03e1
    bmi fp_math_print_str_negative
    lda #$20
    jsr $ffd2
fp_math_print_str_negative:
    jsr fp_print
    jmp print_separator
fp_math_print_end:

.org $e0ec
fp_math_str_argument:
    jsr advance
    jsr expect_open
    jsr fp_expression
    jsr expect_close
    jsr fp_str_to_buffer
    jmp expect_close
fp_math_str_argument_end:

; RND keeps its five-byte floating-point state at $008B. The argument's sign
; selects a deterministic step, a direct seed, or the CIA timer seed.
.org $fc04
fp_math_rnd:
    lda #$00
    sta $03ff
    sta $03fd
    lda $03e0
    beq fp_rnd_timer
    lda $03e1
    bmi fp_rnd_mix
fp_rnd_advance:
    lda $8b
    sta $03e0
    lda #$00
    sta $03e1
    ldx #$03
fp_rnd_load:
    lda $8c,x
    sta $03e2,x
    dex
    bpl fp_rnd_load
    lda $03e2
    ora #$80
    sta $03e2
    ldx #$00
    jsr fp_rnd_constant_arg
    inc $03fd
    jsr fp_multiply
    jmp fp_rnd_finish_advance
fp_rnd_timer:
    lda #$80
    sta $03e0
    lda #$00
    sta $03e1
    lda $dc09
    sta $03e2
    lda $dc05
    sta $03e3
    lda $dc08
    sta $03e4
    lda $dc04
    sta $03e5
fp_rnd_mix:
    ldx $03e5
    lda $03e2
    sta $03e5
    stx $03e2
    ldx $03e4
    lda $03e3
    sta $03e4
    stx $03e3
    lda $03e0
    sta $03ef
    lda #$80
    sta $03e0
    lda #$00
    sta $03e1
    lda $03e2
    ora $03e3
    ora $03e4
    ora $03e5
    ora $03ef
    beq fp_rnd_zero
fp_rnd_normalize:
    lda $03e2
    bmi fp_rnd_done
    asl $03ef
    rol $03e5
    rol $03e4
    rol $03e3
    rol $03e2
    dec $03e0
    bne fp_rnd_normalize
fp_rnd_zero:
    jmp fp_zero
fp_rnd_done:
    lda $03ef
    bpl fp_rnd_store
    jsr fp_round_fac
fp_rnd_store:
    lda $03e0
    sta $8b
    ldx #$03
fp_rnd_store_loop:
    lda $03e2,x
    sta $8c,x
    dex
    bpl fp_rnd_store_loop
    lda $8c
    and #$7f
    sta $8c
    rts
fp_math_rnd_end:

.org $e5a5
fp_rnd_finish_advance:
    lda #$00
    sta $03fd
    lda $03f4
    ldx $03f0
    bmi fp_rnd_guard_ready
    asl a
    ldx $03f5
    bpl fp_rnd_guard_ready
    ora #$01
fp_rnd_guard_ready:
    sta $03ef
    ldx #$06
    jsr fp_rnd_constant_arg
    lda $03e0
    cmp $03e6
    bcc fp_rnd_do_add
    sec
    sbc $03e6
    cmp #$29
    bcs fp_rnd_mix
fp_rnd_do_add:
fp_rnd_add_rounded:
    lda $03ef
    sta $03fe
    inc $03ed
    jsr fp_add
    dec $03ed
    jmp fp_rnd_mix

; Preserve the product's fractional guard while the small recurrence
; constant is aligned. Only a carry from their sum increments the FAC;
; the final RND byte permutation consumes the unrounded significand.
.org $e5e7
fp_add_round_guard:
    lda $03ed
    lsr a
    bcc fp_add_round_regular
    clc
    lda $03ef
    adc $03fe
    bcc fp_add_round_guard_done
    jsr fp_round_fac
fp_add_round_guard_done:
    lda #$00
    rts
fp_add_round_regular:
    lda $03ef
    rts
fp_add_round_guard_end:

; $03B0-$03D7 is a math workspace. $F4/$F5 is its indirect pointer.
; Save/load helpers take the low byte of a six-byte slot in X.
.org $e1d7
math_set_pointer:
    stx $f4
    lda #$03
    sta $f5
    rts
math_save:
    jsr math_set_pointer
    ldy #$00
math_save_loop:
    lda $03e0,y
    sta ($f4),y
    iny
    cpy #$06
    bne math_save_loop
    rts
math_load:
    jsr math_set_pointer
    ldy #$00
math_load_loop:
    lda ($f4),y
    sta $03e0,y
    iny
    cpy #$06
    bne math_load_loop
    rts
math_arg:
    jsr math_set_pointer
    ldy #$00
math_arg_loop:
    lda ($f4),y
    sta $03e6,y
    iny
    cpy #$06
    bne math_arg_loop
    rts
math_copy_fac_arg:
    ldy #$05
math_copy_fac_arg_loop:
    lda $03e0,y
    sta $03e6,y
    dey
    bpl math_copy_fac_arg_loop
    rts
math_const_fac:
    ldy #$00
math_const_fac_loop:
    lda math_constants,x
    sta $03e0,y
    inx
    iny
    cpy #$06
    bne math_const_fac_loop
    rts
math_const_arg:
    ldy #$00
math_const_arg_loop:
    lda math_constants,x
    sta $03e6,y
    inx
    iny
    cpy #$06
    bne math_const_arg_loop
    rts

; term := term*Q/n, sum := sum+term; n is signed $60/$61.
math_series_step:
    ldx #$b6
    jsr math_load
    jsr math_divide_only
    ldx #$c2
    jsr math_arg
    jsr fp_multiply
    jmp math_accumulate
math_divide_accumulate:
    jsr math_divide_only
math_accumulate:
    ldx #$b6
    jsr math_save
    ldx #$bc
    jsr math_arg
    jsr fp_add
    ldx #$bc
    jmp math_save
math_divide_only:
    jsr fp_push
    jsr fp_from_int
    jsr fp_pop_arg
    jsr fp_swap
    jsr fp_divide
    rts

fp_math_exp:
    lda $03e0
    beq fp_exp_one
    jsr fp_exp_init_scale
fp_exp_scale:
    lda $03e0
    cmp $60
    bcc fp_exp_scaled
    dec $03e0
    inc $03cf
    bne fp_exp_scale
fp_exp_scaled:
    ldx #$c2
    jsr math_save
    lda $03e1
    bpl fp_exp_positive
    jmp fp_exp_negative
fp_exp_positive:
    ldx #$00
    jsr math_const_fac
    ; Evaluate the same Taylor polynomial from the smallest coefficient
    ; inward, so each addition is made near the scale of the final value.
    lda #$18
    sta $03ce
fp_exp_term_loop:
    lda $03ce
    sta $60
    lda #$00
    sta $61
    jsr math_divide_only
    ldx #$c2
    jsr math_arg
    jsr fp_multiply
    ldx #$00
    jsr math_const_arg
    jsr fp_add
    dec $03ce
    bne fp_exp_term_loop
fp_exp_square_loop:
    lda $03cf
    beq fp_exp_done
    jsr math_copy_fac_arg
    jsr fp_multiply
    dec $03cf
    jmp fp_exp_square_loop
fp_exp_done:
    rts

; FAC records: biased exponent, sign, and four normalized mantissa bytes.
math_constants:
    .byte $81,$00,$80,$00,$00,$00 ; 1
    .byte $80,$00,$80,$00,$00,$00 ; 1/2
    .byte $82,$00,$80,$00,$00,$00 ; 2
    .byte $80,$00,$b1,$72,$17,$f8 ; ln(2)
    .byte $82,$00,$c9,$0f,$da,$a2 ; pi
    .byte $83,$00,$c9,$0f,$da,$a2 ; 2*pi
    .byte $81,$00,$c9,$0f,$da,$a2 ; pi/2
fp_math_core_end:

; Forward Taylor accumulation avoids cancellation for negative arguments.
; The loop occupies three verified gaps between existing ROM routines.
.org $f11c
fp_exp_negative:
    ldx #$00
    jsr math_const_fac
    ldx #$b6
    jsr math_save
    ldx #$bc
    jsr math_save
    lda #$01
    jmp fp_exp_negative_loop

.org $fe9b
fp_exp_negative_loop:
    sta $60
fp_exp_negative_step:
    lda #$00
    sta $61
    jsr math_series_step
    inc $60
    jmp fp_exp_negative_tail

; Local branches from SQR keep the private $FEBC entry unobstructed.
fp_sqrt_illegal:
    jmp illegal_quantity
fp_sqrt_zero_bridge:
    jmp fp_zero

.org $e3fe
fp_exp_negative_tail:
    lda $60
    cmp #$14
    bcs fp_exp_negative_done
    jmp fp_exp_negative_step
fp_exp_negative_done:
    ldx #$bc
    jsr math_load
    jmp fp_exp_square_loop

.org $f141
fp_exp_one:
    ldx #$00
    jmp math_const_fac

; The positive Horner polynomial can safely use |x| < 4 with 24 terms.
; Keep the negative forward series on |x| < 2 to avoid cancellation.
.org $e424
fp_exp_init_scale:
    lda $03e1
    eor #$80
    asl a
    sta $03cf
    jmp fp_exp_init_scale_tail

.org $f146
fp_exp_init_scale_tail:
    lda #$82
    adc #$00
    sta $60
    rts

; Compare ARG (left operand) with FAC (right operand), returning the BASIC
; greater/equal/less bit in A. Comparing fields avoids arithmetic overflow.
.org $e300
fp_math_compare:
    lda $03e6
    beq fp_compare_left_zero
    lda $03e0
    beq fp_compare_right_zero
    lda $03e7
    eor $03e1
    bmi fp_compare_opposite_signs
    lda $03e6
    cmp $03e0
    bcc fp_compare_magnitude_less
    bne fp_compare_magnitude_greater
    ldx #$00
fp_compare_mantissa:
    lda $03e8,x
    cmp $03e2,x
    bcc fp_compare_magnitude_less
    bne fp_compare_magnitude_greater
    inx
    cpx #$04
    bne fp_compare_mantissa
fp_compare_equal:
    lda #$02
    rts
fp_compare_left_zero:
    lda $03e0
    beq fp_compare_equal
    lda $03e1
    bmi fp_compare_greater
    bpl fp_compare_less
fp_compare_right_zero:
    lda $03e7
    bmi fp_compare_less
    bpl fp_compare_greater
fp_compare_opposite_signs:
    lda $03e7
    bmi fp_compare_less
    bpl fp_compare_greater
fp_compare_magnitude_less:
    lda $03e7
    bmi fp_compare_greater
    bpl fp_compare_less
fp_compare_magnitude_greater:
    lda $03e7
    bmi fp_compare_less
fp_compare_greater:
    lda #$01
    rts
fp_compare_less:
    lda #$04
    rts
fp_math_compare_end:

.org $eecc
fp_math_log:
    lda $03e0
    beq fp_math_log_domain
    lda $03e1
    bmi fp_math_log_domain
    lda $03e0
    sec
    sbc #$81
    sta $03cf
    lda #$81
    sta $03e0
    ldx #$b0
    jsr math_save
    ldx #$00
    jsr math_const_fac
    ldx #$b0
    jsr math_arg
    jsr fp_subtract
    ldx #$b6
    jsr math_save
    ldx #$b0
    jsr math_load
    ldx #$00
    jsr math_const_arg
    jsr fp_add
    jsr math_copy_fac_arg
    ldx #$b6
    jsr math_load
    jsr fp_divide
    ldx #$b0
    jsr math_save
    ldx #$b6
    jsr math_save
    ldx #$bc
    jsr math_save
    jsr math_copy_fac_arg
    jsr fp_multiply
    ldx #$c2
    jsr math_save
    lda #$03
    sta $03ce
fp_log_series:
    jsr fp_shared_series_step
    cmp #$17
    bcc fp_log_series
    ldx #$bc
    jsr math_load
    lda $03e0
    beq fp_log_add_exponent
    inc $03e0
fp_log_add_exponent:
    ldx #$c8
    jsr math_save
    lda $03cf
    sta $60
    lda #$00
    bit $03cf
    bpl fp_log_positive_exponent
    lda #$ff
fp_log_positive_exponent:
    sta $61
    jsr fp_from_int
    ldx #$12
    jsr math_const_arg
    jsr fp_multiply
    ldx #$c8
    jsr math_arg
    jmp fp_add
fp_math_log_domain:
    jmp illegal_quantity
fp_math_log_end:

; Convert radians to turns, keep the fractional turn, then map to [-pi,pi].
math_reduce_angle:
    lda $03e1
    sta $03d0
    and #$7f
    sta $03e1
    lda $03e0
    cmp #$82
    bcc math_reduce_restore_sign
    bne math_reduce_turns
    lda $03e2
    cmp #$c9
    bcc math_reduce_restore_sign
math_reduce_turns:
    ldx #$1e
    jsr math_const_arg
    jsr fp_divide
    ldx #$b0
    jsr math_save
    ldx #$00
    jsr math_const_fac
    lda $03b0
    cmp #$81
    bcc math_period_ready
    sta $03e0
math_period_ready:
    ldx #$d1
    jsr math_save
math_reduce_loop:
    ldx #$d1
    jsr math_load
    ldx #$b0
    jsr math_arg
    jsr fp_subtract
    lda $03e1
    bmi math_reduce_skip
    ldx #$b0
    jsr math_save
math_reduce_skip:
    dec $03d1
    lda $03d1
    cmp #$80
    bne math_reduce_loop
math_reduce_fraction:
    ldx #$b0
    jsr math_load
    lda $03e0
    bpl math_reduce_scale
    ldx #$b0
    jsr math_arg
    ldx #$00
    jsr math_const_fac
    jsr fp_subtract
math_reduce_scale:
    ldx #$1e
    jsr math_const_arg
    jsr fp_multiply
math_reduce_restore_sign:
    lda $03e1
    eor $03d0
    sta $03e1
math_reduce_done:
    ldx #$b0
    jmp math_save

math_trig_q:
    ldx #$b0
    jsr math_load
    jsr math_copy_fac_arg
    jsr fp_multiply
    lda #$80
    sta $03e1
math_trig_q_save:
    ldx #$c2
    jmp math_save

fp_math_sin:
    lda $03e0
    bne fp_math_sin_nonzero
    jmp $f16e
fp_math_sin_nonzero:
    jsr math_reduce_sin_angle
    jsr math_trig_q
    ldx #$b0
    jsr math_load
    ldx #$b6
    jsr math_save
    ldx #$bc
    jsr math_save
    lda #$00
    sta $03ce
    lda #$0c
    sta $03cf
    jmp math_trig_series
fp_math_cos:
    lda $03e0
    bne fp_math_cos_nonzero
    jmp $f19c
fp_math_cos_nonzero:
    jsr math_cos_small_dispatch
    jsr math_trig_q
    ldx #$00
    jsr math_const_fac
    ldx #$b6
    jsr math_save
    ldx #$bc
    jsr math_save
    lda #$0c
    sta $03ce
    lda #$18
    sta $03cf
math_trig_series:
    ldx $03ce
    lda math_trig_den_low,x
    sta $60
    lda math_trig_den_high,x
    sta $61
    jsr math_series_step
    inc $03ce
    lda $03ce
    cmp $03cf
    bcc math_trig_series
    ldx #$bc
    jmp math_load
fp_math_tan:
    jsr fp_math_sin
    ldx #$c8
    jsr math_save
    ldx #$b0
    jsr math_load
    jsr fp_math_cos
    jsr math_copy_fac_arg
    ldx #$c8
    jsr math_load
    jmp fp_divide

math_trig_den_low:
    .byte <6,<20,<42,<72,<110,<156,<210,<272,<342,<420,<506,<600
    .byte <2,<12,<30,<56,<90,<132,<182,<240,<306,<380,<462,<552
math_trig_den_high:
    .byte >6,>20,>42,>72,>110,>156,>210,>272,>342,>420,>506,>600
    .byte >2,>12,>30,>56,>90,>132,>182,>240,>306,>380,>462,>552
fp_math_trig_end:

; The reference's near-zero COS has a linear turn component. Restrict this
; candidate to the observed low-turn region before evaluating the COS series.
.org $e14f
math_cos_small_dispatch:
    cmp #$6a
    bcc math_cos_small_start
    jmp math_reduce_angle

.org $e1c1
math_cos_small_start:
    pla
    pla
    jmp math_cos_triangle

.org $e059
math_cos_triangle:
    asl $03e1
    jmp math_cos_pi_arg

.org $e38e
math_cos_pi_arg:
    jsr math_cos_pi_arg_trampoline
    jmp math_cos_divide

.org $e3eb
math_cos_pi_arg_trampoline:
    ldx #$18
    jmp math_const_arg

.org $f5b9
math_cos_divide:
    jsr fp_divide
    jmp $a004

.org $fabb
math_cos_one_arg_trampoline:
    ldx #$00
    jmp math_const_arg

.org $fcfc
math_cos_make_positive:
    asl $03e1
    rts

; Tiny arguments lose low turn bits in the reference trigonometric path.
; Quantize a 40-bit turn fraction before evaluating the shared series.
; The FAC integer-part routine allows counts beyond signed 16-bit range.
; Negative turns keep two fewer low fractional bits than positive turns.
; Helper fragments occupy verified spare ROM intervals.
.org $e1ca
math_reduce_sin_angle:
    lda $03e0
    cmp #$79
    bcc math_sin_tiny_divide
    jmp math_reduce_angle

.org $e159
math_sin_tiny_divide:
    ldx #$1e
    jsr math_const_arg
    jsr fp_divide
    jmp math_sin_tiny_test

.org $e0c3
math_sin_tiny_test:
    lda $03e0
    cmp #$60
    bcc math_sin_tiny_zero_bridge
    jmp math_sin_tiny_sign_save
math_sin_tiny_zero_bridge:
    jmp math_sin_tiny_zero

.org $e37e
math_sin_tiny_zero:
    pla
    pla
    jmp fp_sin_zero

.org $e0fe
math_sin_tiny_sign_save:
    lda $03e1
    asl a
    php
    sta $03e1
    bcs math_sin_tiny_negative_scale_up
    jmp math_sin_tiny_positive_scale_up

.org $e0a8
math_sin_tiny_negative_scale_up:
    lda #$27
    sta $03d1
    jmp math_sin_tiny_scale_up

.org $e43f
math_sin_tiny_positive_scale_up:
    lda #$29
    sta $03d1
    jmp math_sin_tiny_scale_up

.org $fd09
math_sin_tiny_scale_up:
    lda $03d1
    jsr math_sin_adjust_exp
    jsr fp_integer_range
    jmp math_sin_tiny_scale_down

.org $f108
math_sin_adjust_exp:
    clc
    adc $03e0
    sta $03e0
    rts

.org $f1f6
math_sin_tiny_scale_down:
    plp
    ror $03e1
    sec
    lda #$00
    jmp math_sin_tiny_scale_down_finish

.org $e53b
math_sin_tiny_scale_down_finish:
    sbc $03d1
    jsr math_sin_adjust_exp
    jmp math_sin_tiny_multiply

.org $e397
math_sin_tiny_multiply:
    ldx #$1e
    jsr math_const_arg
    jsr fp_multiply
    jmp math_reduce_angle

; Tokens reaching here are TAN ($C0) or ATN ($C1). The preceding factor
; dispatch admits only $BB-$C1 to this path.
.org $f101
fp_math_tail_dispatch:
    cmp #$c0
    beq fp_math_tan
    jmp fp_math_atn
fp_math_tail_dispatch_end:

.org $e453
fp_math_atn:
    lda $03e1
    and #$80
    sta $03d0
    lda #$00
    sta $03e1
    sta $03d1
    lda $03e0
    cmp #$81
    bcc fp_atn_argument_ready
    bne fp_atn_reciprocal
    lda $03e2
    cmp #$80
    bne fp_atn_reciprocal
    lda $03e3
    ora $03e4
    ora $03e5
    beq fp_atn_argument_ready
fp_atn_reciprocal:
    inc $03d1
    jsr math_copy_fac_arg
    ldx #$00
    jsr math_const_fac
    jsr fp_divide
fp_atn_argument_ready:
    ldx #$b0
    jsr math_save
    jsr math_copy_fac_arg
    jsr fp_multiply
    ldx #$00
    jsr math_const_arg
    jsr fp_add
    jsr $feb0
    ldx #$00
    jsr math_const_arg
    jsr fp_add
    jsr math_copy_fac_arg
    ldx #$b0
    jsr math_load
    jsr fp_divide
    ldx #$b0
    jsr math_save
    ldx #$b6
    jsr math_save
    ldx #$bc
    jsr math_save
    jsr math_trig_q
    lda #$03
    sta $03ce
    jmp fp_atn_series
fp_math_atn_head_end:

.org $e547
fp_atn_series:
    jsr fp_shared_series_step
    cmp #$1b
    bcc fp_atn_series
    ldx #$bc
    jsr math_load
    lda $03e0
    beq fp_atn_invert
    inc $03e0
fp_atn_invert:
    lda $03d1
    beq fp_atn_sign
    ldx #$24
    jsr math_const_arg
    jsr fp_subtract
    jmp fp_atn_sign

; LOG and ATN share the same polynomial series step. The caller owns the
; iteration limit and its continuation; no new RAM state is introduced.
fp_shared_series_step:
    ldx #$b6
    jsr math_load
    ldx #$c2
    jsr math_arg
    jsr fp_multiply
    ldx #$c8
    jsr math_save
    lda $03ce
    sta $60
    lda #$00
    sta $61
    jsr math_divide_accumulate
    ldx #$c8
    jsr math_load
    ldx #$b6
    jsr math_save
    inc $03ce
    inc $03ce
    lda $03ce
    rts
fp_math_atn_end:

; The sign tail fits a former unassigned ROM span without a RAM trampoline.
.org $f8ee
fp_atn_sign:
    lda $03e0
    beq fp_atn_done
    lda $03e1
    eor $03d0
    sta $03e1
fp_atn_done:
    rts
