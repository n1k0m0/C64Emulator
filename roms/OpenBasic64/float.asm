; SPDX-License-Identifier: Apache-2.0
; Independently authored five-byte binary floating-point arithmetic.
; RAM $03E0-$03E5 is FAC: biased exponent, sign, then a normalized
; 32-bit significand with its leading bit restored. $03E6-$03EB is ARG.
; Variable memory keeps the public exponent/sign/31-bit fraction layout.
; $03F0-$03F7 is an eight-byte multiplication/division workspace.

fp_expression:
fp_or:
    jsr fp_and
fp_or_loop:
    jsr skip_space
    cmp #$b0
    bne fp_or_done
    jsr fp_to_int
    lda $61
    pha
    lda $60
    pha
    jsr advance
    jsr fp_and
    jsr fp_to_int
    pla
    ora $60
    sta $60
    pla
    ora $61
    sta $61
    jsr fp_from_int
    jmp fp_or_loop
fp_or_done:
    rts
fp_and:
    jsr fp_not
fp_and_loop:
    jsr skip_space
    cmp #$af
    bne fp_and_done
    jsr fp_to_int
    lda $61
    pha
    lda $60
    pha
    jsr advance
    jsr fp_not
    jsr fp_to_int
    pla
    and $60
    sta $60
    pla
    and $61
    sta $61
    jsr fp_from_int
    jmp fp_and_loop
fp_and_done:
    rts
fp_not:
    jsr skip_space
    cmp #$a8
    bne fp_comparison_expression
    jsr advance
    jsr fp_not
    jsr fp_to_int
    lda $60
    eor #$ff
    sta $60
    lda $61
    eor #$ff
    sta $61
    jmp fp_from_int
fp_comparison_expression:
    jsr fp_sum
    jsr skip_space
    cmp #$b1
    beq fp_comparison
    cmp #$b2
    beq fp_comparison
    cmp #$b3
    beq fp_comparison
    rts
fp_comparison:
    jsr fp_push
    lda #$00
    sta $76
fp_comparison_operator:
    jsr skip_space
    cmp #$b1
    beq fp_greater_operator
    cmp #$b2
    beq fp_equal_operator
    cmp #$b3
    beq fp_less_operator
    lda $76
    pha
    jsr fp_sum
    pla
    sta $76
    jsr fp_pop_arg
    jsr $e300
fp_compare_result:
    and $76
    beq fp_compare_false
    lda #$ff
    sta $60
    sta $61
    jmp fp_from_int
fp_compare_false:
    lda #$00
    sta $60
    sta $61
    jmp fp_from_int
fp_greater_operator:
    lda #$01
    bne fp_add_operator
fp_equal_operator:
    lda #$02
    bne fp_add_operator
fp_less_operator:
    lda #$04
fp_add_operator:
    ora $76
    sta $76
    jsr advance
    jmp fp_comparison_operator

fp_sum:
    jsr fp_term
fp_sum_loop:
    jsr skip_space
    cmp #$aa
    beq fp_sum_plus
    cmp #$2b
    beq fp_sum_plus
    cmp #$ab
    beq fp_sum_minus
    cmp #$2d
    bne fp_sum_done
fp_sum_minus:
    lda #$01
    bne fp_sum_operator
fp_sum_plus:
    lda #$00
fp_sum_operator:
    pha
    jsr fp_push
    jsr advance
    jsr fp_term
    jsr fp_pop_arg
    pla
    beq fp_sum_add
    jsr fp_subtract
    jmp fp_sum_loop
fp_sum_add:
    jsr fp_add
    jmp fp_sum_loop
fp_sum_done:
    rts
fp_term:
    jsr fp_power
fp_term_loop:
    jsr skip_space
    cmp #$ac
    beq fp_term_multiply
    cmp #$2a
    beq fp_term_multiply
    cmp #$ad
    beq fp_term_divide
    cmp #$2f
    bne fp_term_done
fp_term_divide:
    lda #$01
    bne fp_term_operator
fp_term_multiply:
    lda #$00
fp_term_operator:
    pha
    jsr fp_push
    jsr advance
    jsr fp_power
    jsr fp_pop_arg
    pla
    beq fp_term_multiply_now
    ; Division is ARG / FAC. Swap first so FAC is the numerator.
    jsr $e3f0
    jsr fp_swap
    jsr fp_divide
    jmp fp_term_loop
fp_term_multiply_now:
    jsr fp_multiply
    jmp fp_term_loop
fp_term_done:
    rts

; Exponentiation is left associative. Integer bits use exponentiation by
; squaring; fractional bits use successive square roots of the base.
fp_power:
    jsr skip_space
    cmp #$ab
    beq fp_power_negative
    cmp #$2d
    beq fp_power_negative
    jsr fp_factor
fp_power_loop:
    jsr skip_space
    cmp #$ae
    beq fp_power_operator
    cmp #$5e
    beq fp_power_operator
    rts
fp_power_negative:
    jsr advance
    jsr fp_power
    lda $03e0
    beq fp_power_negative_done
    lda $03e1
    eor #$80
    sta $03e1
fp_power_negative_done:
    rts
fp_power_operator:
    jsr fp_push
    jsr advance
    jsr fp_factor
    ldx #$05
fp_power_save_exponent:
    lda $03e0,x
    sta $03a0,x
    dex
    bpl fp_power_save_exponent
    lda $03a1
    and #$80
    sta $03ae
    lda $03e0
    cmp #$91
    bcs fp_overflow
    jsr fp_to_int
    lda $60
    sta $03ac
    lda $61
    sta $03ad
    jsr fp_from_int
    ldx #$05
fp_power_fraction_argument:
    lda $03a0,x
    sta $03e6,x
    dex
    bpl fp_power_fraction_argument
    jsr fp_subtract
    ldx #$05
fp_power_save_fraction:
    lda $03e0,x
    sta $03a0,x
    dex
    bpl fp_power_save_fraction
    lda $03ae
    beq fp_power_positive_exponent
    lda $03a0
    beq fp_power_positive_exponent
    lda #$00
    sta $03a1
fp_power_positive_exponent:
    lda $03ae
    beq fp_power_count_ready
    sec
    lda #$00
    sbc $03ac
    sta $03ac
    lda #$00
    sbc $03ad
    sta $03ad
fp_power_count_ready:
    jsr fp_pop_arg
    ldx #$05
fp_power_save_base:
    lda $03e6,x
    sta $03a6,x
    dex
    bpl fp_power_save_base
    lda $03a7
    bpl fp_power_start
    lda $03a0
    bne illegal_quantity
fp_power_start:
    lda #$01
    sta $60
    lda #$00
    sta $61
    jsr fp_from_int
    ldx #$05
fp_power_save_one:
    lda $03e0,x
    sta $03d8,x
    dex
    bpl fp_power_save_one
fp_power_integer_loop:
    lda $03ac
    ora $03ad
    beq fp_power_fraction_start
    lda $03ac
    and #$01
    beq fp_power_integer_shift
    jsr fp_power_load_result
    jsr fp_power_load_base_arg
    jsr fp_multiply
    jsr fp_power_save_result
fp_power_integer_shift:
    lsr $03ad
    ror $03ac
    lda $03ac
    ora $03ad
    beq fp_power_fraction_start
    ldx #$05
fp_power_square_base:
    lda $03a6,x
    sta $03e0,x
    sta $03e6,x
    dex
    bpl fp_power_square_base
    jsr fp_multiply
    ldx #$05
fp_power_store_base:
    lda $03e0,x
    sta $03a6,x
    dex
    bpl fp_power_store_base
    jmp fp_power_integer_loop
fp_power_fraction_start:
    lda $03a0
    beq fp_power_finish
    lda $03a6
    beq fp_power_zero_base_fraction
    lda #$20
    sta $03af
fp_power_fraction_loop:
    ldx #$05
fp_power_load_root:
    lda $03a6,x
    sta $03e0,x
    dex
    bpl fp_power_load_root
    jsr fp_square_root
    ldx #$05
fp_power_save_root:
    lda $03e0,x
    sta $03a6,x
    lda $03a0,x
    sta $03e0,x
    dex
    bpl fp_power_save_root
    inc $03e0
    lda $03e0
    cmp #$81
    bcc fp_power_fraction_store_only
    jsr fp_push
    lda #$01
    sta $60
    lda #$00
    sta $61
    jsr fp_from_int
    jsr fp_pop_arg
    jsr fp_subtract
    lda #$01
    bne fp_power_fraction_store_start
fp_power_fraction_store_only:
    lda #$00
fp_power_fraction_store_start:
    pha
    ldx #$05
fp_power_fraction_store:
    lda $03e0,x
    sta $03a0,x
    dex
    bpl fp_power_fraction_store
    pla
    beq fp_power_fraction_next
    jsr fp_power_load_result
    jsr fp_power_load_base_arg
    jsr fp_multiply
    jsr fp_power_save_result
fp_power_fraction_next:
    lda $03a0
    beq fp_power_finish
    dec $03af
    bne fp_power_fraction_loop
    jmp fp_power_finish
fp_power_zero_base_fraction:
    lda $03ae
    bne fp_division_by_zero
    jmp fp_zero
fp_power_finish:
    jsr fp_power_load_result
    lda $03ae
    beq fp_power_done
    ldx #$05
fp_power_reciprocal_arg:
    lda $03e0,x
    sta $03e6,x
    dex
    bpl fp_power_reciprocal_arg
    lda #$01
    sta $60
    lda #$00
    sta $61
    jsr fp_from_int
    jsr fp_divide
fp_power_done:
    jmp fp_power_loop
fp_power_load_result:
    ldx #$05
fp_power_load_result_loop:
    lda $03d8,x
    sta $03e0,x
    dex
    bpl fp_power_load_result_loop
    rts
fp_power_save_result:
    ldx #$05
fp_power_save_result_loop:
    lda $03e0,x
    sta $03d8,x
    dex
    bpl fp_power_save_result_loop
    rts
fp_power_load_base_arg:
    ldx #$05
fp_power_load_base_arg_loop:
    lda $03a6,x
    sta $03e6,x
    dex
    bpl fp_power_load_base_arg_loop
    rts
fp_factor:
    jsr skip_space
    cmp #$28
    beq fp_parenthesis
    cmp #$ab
    beq fp_negative
    cmp #$2d
    beq fp_negative
    cmp #$2b
    beq fp_positive
    cmp #$aa
    beq fp_positive
    cmp #$b6
    beq fp_absolute
    cmp #$b4
    beq fp_sign
    cmp #$b5
    beq fp_integer_part
    cmp #$ba
    beq fp_square_root_function
    cmp #$bb
    bcc fp_factor_number
    cmp #$c6
    bcs fp_factor_number
    jmp $e060
fp_factor_number:
    cmp #$2e
    beq fp_parse_number
    cmp #$30
    bcc fp_other_factor
    cmp #$3a
    bcc fp_parse_number
fp_other_factor:
    cmp #$c2
    beq fp_legacy_factor
    cmp #$c3
    beq fp_legacy_factor
    cmp #$c6
    beq fp_legacy_factor
    jsr parse_variable
    jmp fp_load_variable
fp_legacy_factor:
    jsr factor
    jmp fp_from_int
fp_parenthesis:
    jsr advance
    jsr fp_expression
    jsr expect_close
    rts
fp_negative:
    jsr advance
    jsr fp_factor
    lda $03e0
    beq fp_negative_done
    lda $03e1
    eor #$80
    sta $03e1
fp_negative_done:
    rts
fp_positive:
    jsr advance
    jmp fp_factor
fp_function_argument:
    jsr advance
    jsr expect_open
    jsr fp_expression
    jmp expect_close
fp_absolute:
    jsr fp_function_argument
    lda #$00
    sta $03e1
    rts
fp_sign:
    jsr fp_function_argument
    lda $03e0
    beq fp_integer_zero
    lda $03e1
    bmi fp_sign_negative
    lda #$01
    sta $60
    lda #$00
    sta $61
    jmp fp_from_int
fp_sign_negative:
    lda #$ff
    sta $60
    sta $61
    jmp fp_from_int
fp_sign_done:
    rts
fp_integer_part:
    jsr fp_function_argument
    lda $03e0
    beq fp_integer_zero
    cmp #$81
    bcs fp_integer_range
    lda $03e1
    bpl fp_integer_zero
    lda #$ff
    sta $60
    sta $61
    jmp fp_from_int
fp_integer_zero:
    jmp fp_zero
fp_integer_range:
    cmp #$a0
    bcs fp_integer_done
    lda #$a0
    sec
    sbc $03e0
    sta $03dc
    lda $03e1
    sta $03dd
    lda #$00
    sta $03de
fp_integer_shift_right:
    lda $03e5
    and #$01
    ora $03de
    sta $03de
    jsr fp_shift_right
    dec $03dc
    bne fp_integer_shift_right
    lda #$a0
    sec
    sbc $03e0
    sta $03dc
fp_integer_shift_left:
    jsr fp_shift_left
    dec $03dc
    bne fp_integer_shift_left
    lda $03dd
    bpl fp_integer_done
    lda $03de
    beq fp_integer_done
    jsr fp_push
    lda #$ff
    sta $60
    sta $61
    jsr fp_from_int
    jsr fp_pop_arg
    jmp fp_add
fp_integer_done:
    rts

fp_square_root_function:
    jsr fp_function_argument
    jsr $e3f0
    jmp fp_square_root
fp_square_root:
    jmp $feb0

; Convert a signed integer in $60/$61 to the normalized FAC.
fp_from_int:
    lda #$00
    sta $03ff
    lda $60
    ora $61
    bne fp_from_int_nonzero
    jmp fp_zero
fp_from_int_nonzero:
    lda #$00
    sta $03e1
    sta $03e4
    sta $03e5
    lda $61
    bpl fp_from_int_positive
    lda #$80
    sta $03e1
    sec
    lda #$00
    sbc $60
    sta $60
    lda #$00
    sbc $61
    sta $61
fp_from_int_positive:
    lda $61
    sta $03e2
    lda $60
    sta $03e3
    lda #$90
    sta $03e0
fp_from_int_normalize:
    lda $03e2
    bmi fp_from_int_done
    jsr fp_shift_left
    dec $03e0
    jmp fp_from_int_normalize
fp_from_int_done:
    rts

; Truncate FAC to a 16-bit integer for addresses, integer variables and
; bitwise operators. $90 is the exponent of bit 15.
fp_to_int:
    lda #$00
    sta $60
    sta $61
    lda $03e0
    cmp #$81
    bcc fp_to_int_done
    cmp #$91
    bcs fp_to_int_done
    lda $03e2
    sta $61
    lda $03e3
    sta $60
    lda #$90
    sec
    sbc $03e0
    tax
fp_to_int_shift:
    cpx #$00
    beq fp_to_int_sign
    lsr $61
    ror $60
    dex
    jmp fp_to_int_shift
fp_to_int_sign:
    lda $03e1
    bpl fp_to_int_done
    sec
    lda #$00
    sbc $60
    sta $60
    lda #$00
    sbc $61
    sta $61
fp_to_int_done:
    rts

fp_load_variable:
    lda #$00
    sta $03ff
    jsr find_variable
    bcs fp_zero
    lda $68
    bmi fp_load_integer
    ldy #$02
    lda ($66),y
    beq fp_zero
    sta $03e0
    iny
    lda ($66),y
    and #$80
    sta $03e1
    lda ($66),y
    ora #$80
    sta $03e2
    iny
    lda ($66),y
    sta $03e3
    iny
    lda ($66),y
    sta $03e4
    iny
    lda ($66),y
    sta $03e5
    rts
fp_load_integer:
    ldy #$02
    lda ($66),y
    sta $61
    iny
    lda ($66),y
    sta $60
    jmp fp_from_int
fp_store_variable:
    lda $68
    bmi fp_store_integer
    jsr $e3f0
fp_store_float_ready:
    jsr locate_or_create_variable
    ldy #$02
    lda $03e0
    sta ($66),y
    iny
    lda $03e2
    and #$7f
    ora $03e1
    sta ($66),y
    iny
    lda $03e3
    sta ($66),y
    iny
    lda $03e4
    sta ($66),y
    iny
    lda $03e5
    sta ($66),y
    rts
fp_store_integer:
    jsr fp_to_int
    jsr locate_or_create_variable
    ldy #$02
    lda $61
    sta ($66),y
    iny
    lda $60
    sta ($66),y
    rts

fp_zero:
    ldx #$05
    lda #$00
    sta $03ff
fp_zero_loop:
    sta $03e0,x
    dex
    bpl fp_zero_loop
    rts
fp_set_ten:
    lda #$84
    sta $03e0
    lda #$a0
    sta $03e2
    lda #$00
    sta $03e1
    sta $03e3
    sta $03e4
    sta $03e5
    rts
fp_push:
    jsr $e3f0
    pla
    sta $03ee
    pla
    sta $03ef
    ldx #$00
fp_push_loop:
    lda $03e0,x
    pha
    inx
    cpx #$06
    bne fp_push_loop
    lda $03ef
    pha
    lda $03ee
    pha
    rts
fp_pop_arg:
    pla
    sta $03ee
    pla
    sta $03ef
    ldx #$05
fp_pop_arg_loop:
    pla
    sta $03e6,x
    dex
    bpl fp_pop_arg_loop
    lda $03ef
    pha
    lda $03ee
    pha
    rts
fp_copy_arg:
    ldx #$05
fp_copy_arg_loop:
    lda $03e6,x
    sta $03e0,x
    dex
    bpl fp_copy_arg_loop
    rts
fp_swap:
    ldx #$05
fp_swap_loop:
    lda $03e0,x
    sta $03ec
    lda $03e6,x
    sta $03e0,x
    lda $03ec
    sta $03e6,x
    dex
    bpl fp_swap_loop
    rts
fp_shift_left:
    asl $03e5
    rol $03e4
    rol $03e3
    rol $03e2
    rts
fp_shift_right:
    lsr $03e2
    ror $03e3
    ror $03e4
    ror $03e5
    rts
fp_shift_arg_right:
    lsr $03e8
    ror $03e9
    ror $03ea
    ror $03eb
    rts

; FAC + ARG, truncating bits below the 32-bit significand.
fp_add:
    jsr $e4cf
    lda $03e0
    beq fp_add_arg
    lda $03e6
    beq fp_add_done
    jmp fp_add_align
.org $baf1
fp_add_align:
    lda $03e0
    cmp $03e6
    beq fp_add_aligned
    bcc fp_add_shift_fac
    jsr fp_shift_arg_right
    ror $03ef
    inc $03e6
    jmp fp_add_align
.org $bb07
fp_add_shift_fac:
    jsr fp_shift_right
    ror $03ef
    inc $03e0
    jmp fp_add_align
.org $bb13
fp_add_aligned:
    lda $03e1
    eor $03e7
    bmi fp_add_opposite
    clc
    txa
    pha
    ldx #$03
fp_add_same_loop:
    lda $03e2,x
    adc $03e8,x
    sta $03e2,x
    dex
    bpl fp_add_same_loop
    pla
    tax
    bcc fp_add_round
    sec
    ror $03e2
    ror $03e3
    ror $03e4
    ror $03e5
    ror $03ef
    inc $03e0
    beq fp_overflow
fp_add_round:
    jsr $e5e7
    bpl fp_add_done
    jsr fp_round_fac
    lda $03ee
    bne fp_overflow
    lda $03e0
    beq fp_overflow
fp_add_done:
    rts
.org $bb73
fp_add_arg:
    jmp fp_copy_arg
.org $bb76
fp_add_opposite:
    ldx #$00
.org $bb78
fp_add_compare:
    lda $03e2,x
    cmp $03e8,x
    bcc fp_add_swap
    bne fp_add_subtract
    inx
    cpx #$04
    bne fp_add_compare
    jmp fp_zero
.org $bb8a
fp_add_swap:
    jsr fp_swap
.org $bb8d
fp_add_subtract:
    sec
    txa
    pha
    ldx #$03
fp_add_subtract_loop:
    lda $03e2,x
    sbc $03e8,x
    sta $03e2,x
    dex
    bpl fp_add_subtract_loop
    pla
    tax
    jmp $ff10
.org $bbb5
fp_subtract:
    lda $03e0
    beq fp_subtract_zero
    lda $03e1
    eor #$80
    sta $03e1
fp_subtract_zero:
    jmp fp_add

; 32 x 32 -> 64-bit shift-and-add multiplication. A freshly parsed decimal
; FAC may also carry a half-ULP guard in $03FF. Include that fraction in
; the full product before rounding to the stored 32-bit significand.
fp_multiply:
    lda $03e0
    beq fp_multiply_zero
    lda $03e6
    beq fp_multiply_zero
    jsr $f190
    clc
    lda $03e0
    adc $03e6
    sta $03ec
    lda #$00
    adc #$00
    sta $03ee
    sec
    lda $03ec
    sbc #$80
    sta $03ec
    lda $03ee
    sbc #$00
    sta $03ee
    lda $03e1
    eor $03e7
    and #$80
    sta $03ed
    ldx #$07
    lda #$00
fp_multiply_clear:
    sta $03f0,x
    dex
    bpl fp_multiply_clear
    ldy #$20
fp_multiply_bit:
    asl $03f7
    rol $03f6
    rol $03f5
    rol $03f4
    rol $03f3
    rol $03f2
    rol $03f1
    rol $03f0
    asl $03e5
    rol $03e4
    rol $03e3
    rol $03e2
    bcc fp_multiply_next
    clc
    ldx #$03
fp_multiply_add_low:
    lda $03f4,x
    adc $03e8,x
    sta $03f4,x
    dex
    bpl fp_multiply_add_low
    bcc fp_multiply_add_high_zero
    ldx #$03
fp_multiply_add_high:
    lda $03f0,x
    adc #$00
    sta $03f0,x
    dex
    bpl fp_multiply_add_high
    jmp fp_multiply_next
fp_multiply_add_high_zero:
    lda $03f0
    adc #$00
    jmp fp_multiply_next
.org $bc7c
fp_multiply_next:
    dey
    bne fp_multiply_bit
    jsr $e35c
    lda $03ec
    sta $03e0
    lda $03ed
    sta $03e1
    ldx #$03
.org $bc90
fp_multiply_result:
    lda $03f0,x
    sta $03e2,x
    dex
    bpl fp_multiply_result
    lda $03e2
    bmi fp_multiply_round_normal
    jsr fp_shift_left
    lda $03f4
    bpl fp_multiply_no_low_bit
    inc $03e5
.org $bca9
fp_multiply_no_low_bit:
    lda $03e0
    bne fp_multiply_shift_exp_nonzero
    dec $03ee
.org $bcb1
fp_multiply_shift_exp_nonzero:
    dec $03e0
.org $bcb4
fp_multiply_round_shifted:
    lda $03f4
    and #$40
    jmp fp_multiply_round_maybe
.org $bcbc
fp_multiply_round_normal:
    lda $03f4
    and #$80
.org $bcc1
fp_multiply_round_maybe:
    beq fp_multiply_check
    lda $03fd
    bne fp_multiply_check
    jsr fp_round_fac
.org $bccb
fp_multiply_check:
    jsr $f150
.org $bcce
fp_multiply_done:
    rts
.org $bccf
fp_multiply_zero:
    jmp $f110

; Restoring 32-bit division. FAC / ARG, with a 33-bit remainder.
.org $bcd2
fp_divide:
    lda $03e6
    beq fp_division_by_zero
    lda $03e0
    beq fp_divide_zero
    jsr $e3f0
    sec
    lda $03e0
    sbc $03e6
    sta $03ec
    lda #$00
    sbc #$00
    sta $03ee
    clc
    lda $03ec
    adc #$80
    sta $03ec
    lda $03ee
    adc #$00
    sta $03ee
    lda $03e1
    eor $03e7
    and #$80
    sta $03ed
    ldx #$03
fp_divide_init:
    lda $03e2,x
    sta $03f0,x
    lda #$00
    sta $03f4,x
    dex
    bpl fp_divide_init
    sta $03df
    ldx #$00
fp_divide_compare_initial:
    lda $03f0,x
    cmp $03e8,x
    bcc fp_divide_small
    bne fp_divide_large
    inx
    cpx #$04
    bne fp_divide_compare_initial
fp_divide_large:
    jsr fp_divide_subtract
    lda #$01
    sta $03f7
    inc $03ec
    bne fp_divide_large_no_carry
    inc $03ee
fp_divide_large_no_carry:
    lda #$1f
    bne fp_divide_count
fp_divide_small:
    lda #$20
fp_divide_count:
    ; Preserve the initial 31/32-bit quotient path for the decimal caller.
    sta $03de
    sta $03ef
fp_divide_loop:
    asl $03f3
    rol $03f2
    rol $03f1
    rol $03f0
    rol $03df
    lda $03df
    bne fp_divide_take
    ldx #$00
fp_divide_compare:
    lda $03f0,x
    cmp $03e8,x
    bcc fp_divide_skip
    bne fp_divide_take
    inx
    cpx #$04
    bne fp_divide_compare
fp_divide_take:
    jsr fp_divide_subtract
    sec
    jmp fp_divide_quotient
fp_divide_skip:
    clc
fp_divide_quotient:
    rol $03f7
    rol $03f6
    rol $03f5
    rol $03f4
    dec $03ef
    bne fp_divide_loop
    ; The remainder determines the next quotient bit and rounding.
    asl $03f3
    rol $03f2
    rol $03f1
    rol $03f0
    rol $03df
    lda $03df
    bne fp_divide_round
    ldx #$00
fp_divide_round_compare:
    lda $03f0,x
    cmp $03e8,x
    bcc fp_divide_copy
    bne fp_divide_round
    inx
    cpx #$04
    bne fp_divide_round_compare
fp_divide_round:
    sec
    bcs fp_divide_set_guard
fp_divide_copy:
    clc
fp_divide_set_guard:
    lda #$00
    rol a
    sta $03ef
fp_divide_copy_ready:
    lda $03ec
    sta $03e0
    lda $03ed
    sta $03e1
    ldx #$03
fp_divide_result:
    lda $03f4,x
    sta $03e2,x
    dex
    bpl fp_divide_result
    lda $03ef
    beq fp_divide_check
    jsr fp_round_fac
fp_divide_check:
    jsr $f1b0
    rts
fp_divide_zero:
    jmp $f130

; Increment the significand after a guard-bit rounding decision.
fp_round_fac:
    ldx #$05
fp_round_loop:
    inc $03e0,x
    bne fp_round_done
    dex
    cpx #$01
    bne fp_round_loop
    lda #$80
    sta $03e2
    inc $03e0
    bne fp_round_done
    inc $03ee
fp_round_done:
    rts
fp_divide_subtract:
    sec
    txa
    pha
    ldx #$03
fp_divide_subtract_loop:
    lda $03f0,x
    sbc $03e8,x
    sta $03f0,x
    dex
    bpl fp_divide_subtract_loop
    pla
    tax
    lda $03df
    sbc #$00
    sta $03df
    rts
.org $be2d
fp_division_by_zero:
    lda #<fp_division_text
    sta $66
    lda #>fp_division_text
    sta $67
    jmp report_error
.org $be38
fp_division_text:
    .text "?DIVISION BY ZERO "
    .byte 0

; Decimal literals are accumulated as binary floats, then scaled by 10.
; $03D8 is the signed power of ten; $03D9 marks the decimal point.
.org $be51
fp_val:
    jsr string_argument
    lda $7a
    sta $03aa
    lda $7b
    sta $03ab
    ldy #$00
fp_val_copy:
    cpy $75
    beq fp_val_copy_done
    lda ($64),y
    sta $0200,y
    iny
    bne fp_val_copy
fp_val_copy_done:
    lda #$00
    sta $0200,y
    sta $7a
    sta $03a9
    lda #$02
    sta $7b
    jsr skip_space
    cmp #$2d
    beq fp_val_negative
    cmp #$2b
    bne fp_val_number
    jsr advance
    jmp fp_val_number
fp_val_negative:
    inc $03a9
    jsr advance
fp_val_number:
    jsr peek
    cmp #$2e
    beq fp_val_parse
    cmp #$30
    bcc fp_val_zero
    cmp #$3a
    bcs fp_val_zero
fp_val_parse:
    jsr fp_parse_number
    lda $03a9
    beq fp_val_restore
    lda $03e0
    beq fp_val_restore
    lda #$80
    sta $03e1
    jmp fp_val_restore
fp_val_zero:
    jsr fp_zero
fp_val_restore:
    lda $03aa
    sta $7a
    lda $03ab
    sta $7b
    rts

fp_parse_number:
    jsr fp_zero
    lda #$00
    sta $03d8
    sta $03d9
    sta $03dd
fp_number_loop:
    jsr peek
    cmp #$30
    bcc fp_number_dot
    cmp #$3a
    bcs fp_number_dot
    lda #$01
    sta $03dd
    lda $03d9
    beq fp_number_integer_digit
    dec $03d8
fp_number_integer_digit:
    jsr peek
    and #$0f
    sta $03da
    jsr fp_multiply_ten
    jsr fp_push
    lda $03da
    sta $60
    lda #$00
    sta $61
    jsr fp_from_int
    jsr fp_pop_arg
    jsr fp_add
    jsr advance
    jmp fp_number_loop
fp_number_dot:
    cmp #$2e
    bne fp_number_exponent
    lda $03d9
    bne fp_number_exponent
    inc $03d9
    jsr advance
    jmp fp_number_loop
fp_number_exponent:
    lda $03dd
    beq syntax_error
    jsr peek
    cmp #$45
    bne fp_number_scale
    jsr advance
    lda #$00
    sta $03db
    sta $03dc
    jsr peek
    cmp #$2d
    beq fp_exponent_minus
    cmp #$ab
    beq fp_exponent_minus
    cmp #$2b
    beq fp_exponent_plus
    cmp #$aa
    bne fp_exponent_digits
fp_exponent_plus:
    jsr advance
    jmp fp_exponent_digits
fp_exponent_minus:
    lda #$01
    sta $03db
    jsr advance
fp_exponent_digits:
    jsr peek
    cmp #$30
    bcc fp_exponent_end
    cmp #$3a
    bcs fp_exponent_end
    and #$0f
    sta $03da
    lda $03dc
    asl A
    sta $03de
    asl A
    asl A
    clc
    adc $03de
    clc
    adc $03da
    sta $03dc
    cmp #$51
    bcs fp_exponent_range
    jsr advance
    jmp fp_exponent_digits
fp_exponent_range:
    lda $03db
    bne fp_number_underflow
    jmp fp_overflow
fp_exponent_end:
    lda $03db
    beq fp_exponent_positive
    sec
    lda $03d8
    sbc $03dc
    sta $03d8
    jmp fp_number_scale
fp_exponent_positive:
    clc
    lda $03d8
    adc $03dc
    sta $03d8
fp_number_scale:
    lda #$00
    ; Zero the division-path marker; each executed divide replaces it.
    sta $03de
fp_number_scale_loop:
    lda $03d8
    beq fp_number_done
    bmi fp_number_divide
    jsr fp_multiply_ten
    dec $03d8
    jmp fp_number_scale_loop
fp_number_divide:
    jsr fp_divide_ten
    lda $03ef
    sta $03dd
    inc $03d8
    jmp fp_number_scale_loop
fp_number_done:
    jmp $e3ba
fp_number_underflow:
    jmp fp_zero
fp_overflow:
    lda #$00
    sta $03fd
    lda #<fp_overflow_text
    sta $66
    lda #>fp_overflow_text
    sta $67
    jmp report_error
fp_overflow_text:
    .text "?OVERFLOW "
    .byte 0
fp_multiply_ten:
    jmp $e410
fp_divide_ten:
    jmp $e430

float_end:

; Guard correction in anchored loop-compaction gaps. The main public
; entries and all but two inner labels retain their earlier addresses.
.org $bb61
fp_guard_round:
    jsr $e3f0
    jmp $e4fb
fp_guard_decision:
    bcc fp_guard_smaller
    jsr $e4e9
    inc $03ee
    rts
fp_guard_smaller:
    jmp $e4e9
fp_guard_decision_end:

.org $bba3
fp_guard_dispatch:
    bcc fp_guard_round
    bne fp_guard_round
    lda $03e1
    eor $03e7
    bpl fp_guard_round
    jmp fp_guard_compare
fp_guard_dispatch_end:

.org $bc60
fp_guard_compare:
    txa
    pha
    ldx #$00
fp_guard_compare_loop:
    lda $03e2,x
    cmp $03e8,x
    bne fp_guard_compare_result
    inx
    cpx #$04
    bne fp_guard_compare_loop
    pla
    tax
    jmp fp_guard_round
fp_guard_compare_result:
    pla
    tax
    jmp fp_guard_decision
fp_guard_compare_end:

.org $be1b
fp_guard_only:
    ldx #$03
fp_guard_only_scan:
    lda $03e2,x
    bne fp_guard_only_nonzero
    dex
    bpl fp_guard_only_scan
    sec
    rts
fp_guard_only_nonzero:
    clc
    rts
fp_guard_only_end:

