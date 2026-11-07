"""Build Open Kernal 64 with its native BASIC decimal formatter.

SPDX-License-Identifier: Apache-2.0
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

from assemble import assemble


BRANCH = re.compile(r"\b(bcc|bcs|beq|bmi|bne|bpl|bvc|bvs)\b", re.IGNORECASE)
FLOAT_ENTRIES = (
    "ADVANCE", "EXPECT_CLOSE", "EXPECT_OPEN", "FACTOR", "FP_ADD",
    "FP_DIVIDE", "FP_DIVIDE_TEN", "FP_EXPRESSION", "FP_FROM_INT",
    "FP_FUNCTION_ARGUMENT", "FUNCTION_ARGUMENT", "FP_MULTIPLY", "FP_MULTIPLY_TEN",
    "FP_POP_ARG", "FP_PUSH", "FP_ROUND_FAC", "FP_SET_TEN", "FP_SHIFT_LEFT", "FP_INTEGER_RANGE",
    "FP_OVERFLOW", "FP_SUBTRACT", "FP_SWAP", "FP_TO_INT", "FP_VAL", "FP_ZERO", "PRINT_SEPARATOR",
    "ILLEGAL_QUANTITY", "SYNTAX_ERROR",
)


def assemble_relaxed(source: str) -> tuple[bytes, dict[str, int]]:
    lines = source.splitlines()
    for _ in range(len(lines)):
        try:
            rom, labels = assemble("\n".join(lines), base=0xE000, size=0x2000)
            if (labels["FP_STR_END"] > 0xE060 or
                    labels["FP_MATH_DISPATCH_END"] > 0xE10C or
                    labels["FP_MATH_CHAR_END"] > 0xE0D0 or
                    labels["FP_MATH_PRINT_END"] > 0xE10C or
                    labels["FP_MATH_STR_ARGUMENT_END"] > 0xE10C or
                    labels["FP_MATH_RND_END"] > 0xFD00 or
                    labels["FP_DECIMAL_FINISH_END"] > 0xE3F0 or
                    labels["FP_LITERAL_ROUND_END"] > 0xE410 or
                    labels["FP_MULTIPLY_TEN_IMPL_END"] > 0xE430 or
                    labels["FP_DIVIDE_TEN_IMPL_END"] > 0xE447 or
                    labels["FP_ADD_PREPARE_END"] > 0xE544 or
                    labels["FP_MATH_CORE_END"] > 0xE37B or
                    labels["FP_MATH_COMPARE_END"] > 0xE37B or
                    labels["FP_MATH_ATN_HEAD_END"] > 0xE544 or
                    labels["FP_MATH_ATN_END"] > 0xE600 or
                    labels["FP_MATH_TRIG_END"] > 0xF110 or
                    labels["FP_ZERO_MULTIPLY_END"] > 0xF130 or
                    labels["FP_ZERO_DIVIDE_END"] > 0xF150 or
                    labels["FP_CHECK_MULTIPLY_END"] > 0xF190 or
                    labels["FP_SAVE_MULTIPLY_INPUT_END"] > 0xF1B0 or
                    labels["FP_CHECK_DIVIDE_END"] > 0xF200 or
                    labels["FP_SIN_ZERO_END"] > 0xF190 or
                    labels["FP_COS_ZERO_END"] > 0xF1B0 or
                    labels["FP_RND_CONSTANTS_END"] > 0xF200 or
                    labels["FP_PRINT_END"] > 0xFEB0 or
                    labels["FP_SQRT_END"] > 0xFF10 or
                    labels["FP_SUBTRACT_TAIL_END"] > 0xFF81):
                raise ValueError("floating-point extensions overlap the KERNAL jump table")
            return rom, labels
        except ValueError as exc:
            location = re.search(r"line (\d+): branch out of range", str(exc))
            if location is None:
                raise
            index = int(location.group(1)) - 1
            branch = BRANCH.search(lines[index])
            if branch is None:
                raise
            lines[index] = (lines[index][:branch.start()] + "j" + branch.group() +
                            lines[index][branch.end():])
    raise ValueError("branch relaxation did not converge")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("basic_map", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    basic_labels = {
        name: int(address, 16)
        for address, name in (line.split() for line in args.basic_map.read_text().splitlines())
    }
    aliases = "\n".join(
        f".org ${basic_labels[name]:04X}\n{name}:" for name in FLOAT_ENTRIES
    )
    source = Path(__file__).with_name("kernal.asm").read_text(encoding="utf-8")
    source += "\n" + aliases + "\n"
    source += Path(__file__).with_name("float_print.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float_sqrt.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float_subtract_tail.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float_str.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float_math.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float_zero.asm").read_text(encoding="utf-8")
    source += "\n" + Path(__file__).with_name("float_decimal.asm").read_text(encoding="utf-8")
    rom, labels = assemble_relaxed(source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(rom)
    args.output.with_suffix(".map").write_text(
        "".join(f"{address:04X} {name}\n" for name, address in
                sorted(labels.items(), key=lambda item: item[1])
                if name not in FLOAT_ENTRIES),
        encoding="utf-8",
    )
    print(f"Open Kernal 64: {args.output} ({len(rom)} bytes)")


if __name__ == "__main__":
    main()
