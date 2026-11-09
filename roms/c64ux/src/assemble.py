"""Small, reproducible 6502 assembler for Open Kernal 64.

SPDX-License-Identifier: Apache-2.0

Only documented NMOS 6502 opcodes are accepted.  A fixed absolute ROM range is
filled with $FF; overlaps, out-of-range branches, and unknown symbols fail the
build.  This keeps the ROM build independent of an external assembler binary.
"""

from __future__ import annotations

import argparse
import ast
import re
from pathlib import Path


OPCODES = {}


def op(name: str, **modes: int) -> None:
    for mode, value in modes.items():
        OPCODES[name, mode] = value


op("ADC", imm=0x69, zp=0x65, zpx=0x75, abs=0x6D, abx=0x7D, aby=0x79, izx=0x61, izy=0x71)
op("AND", imm=0x29, zp=0x25, zpx=0x35, abs=0x2D, abx=0x3D, aby=0x39, izx=0x21, izy=0x31)
op("ASL", acc=0x0A, zp=0x06, zpx=0x16, abs=0x0E, abx=0x1E)
op("BCC", rel=0x90)
op("BCS", rel=0xB0)
op("BEQ", rel=0xF0)
op("BIT", zp=0x24, abs=0x2C)
op("BMI", rel=0x30)
op("BNE", rel=0xD0)
op("BPL", rel=0x10)
op("BRK", imp=0x00)
op("BVC", rel=0x50)
op("BVS", rel=0x70)
op("CLC", imp=0x18)
op("CLD", imp=0xD8)
op("CLI", imp=0x58)
op("CLV", imp=0xB8)
op("CMP", imm=0xC9, zp=0xC5, zpx=0xD5, abs=0xCD, abx=0xDD, aby=0xD9, izx=0xC1, izy=0xD1)
op("CPX", imm=0xE0, zp=0xE4, abs=0xEC)
op("CPY", imm=0xC0, zp=0xC4, abs=0xCC)
op("DEC", zp=0xC6, zpx=0xD6, abs=0xCE, abx=0xDE)
op("DEX", imp=0xCA)
op("DEY", imp=0x88)
op("EOR", imm=0x49, zp=0x45, zpx=0x55, abs=0x4D, abx=0x5D, aby=0x59, izx=0x41, izy=0x51)
op("INC", zp=0xE6, zpx=0xF6, abs=0xEE, abx=0xFE)
op("INX", imp=0xE8)
op("INY", imp=0xC8)
op("JMP", abs=0x4C, ind=0x6C)
op("JSR", abs=0x20)
op("LDA", imm=0xA9, zp=0xA5, zpx=0xB5, abs=0xAD, abx=0xBD, aby=0xB9, izx=0xA1, izy=0xB1)
op("LDX", imm=0xA2, zp=0xA6, zpy=0xB6, abs=0xAE, aby=0xBE)
op("LDY", imm=0xA0, zp=0xA4, zpx=0xB4, abs=0xAC, abx=0xBC)
op("LSR", acc=0x4A, zp=0x46, zpx=0x56, abs=0x4E, abx=0x5E)
op("NOP", imp=0xEA)
op("ORA", imm=0x09, zp=0x05, zpx=0x15, abs=0x0D, abx=0x1D, aby=0x19, izx=0x01, izy=0x11)
op("PHA", imp=0x48)
op("PHP", imp=0x08)
op("PLA", imp=0x68)
op("PLP", imp=0x28)
op("ROL", acc=0x2A, zp=0x26, zpx=0x36, abs=0x2E, abx=0x3E)
op("ROR", acc=0x6A, zp=0x66, zpx=0x76, abs=0x6E, abx=0x7E)
op("RTI", imp=0x40)
op("RTS", imp=0x60)
op("SBC", imm=0xE9, zp=0xE5, zpx=0xF5, abs=0xED, abx=0xFD, aby=0xF9, izx=0xE1, izy=0xF1)
op("SEC", imp=0x38)
op("SED", imp=0xF8)
op("SEI", imp=0x78)
op("STA", zp=0x85, zpx=0x95, abs=0x8D, abx=0x9D, aby=0x99, izx=0x81, izy=0x91)
op("STX", zp=0x86, zpy=0x96, abs=0x8E)
op("STY", zp=0x84, zpx=0x94, abs=0x8C)
op("TAX", imp=0xAA)
op("TAY", imp=0xA8)
op("TSX", imp=0xBA)
op("TXA", imp=0x8A)
op("TXS", imp=0x9A)
op("TYA", imp=0x98)

BRANCHES = {name for name, mode in OPCODES if mode == "rel"}
LONG_BRANCHES = {"J" + name: opposite for name, opposite in {
    "BCC": "BCS", "BCS": "BCC", "BEQ": "BNE", "BNE": "BEQ",
    "BMI": "BPL", "BPL": "BMI", "BVC": "BVS", "BVS": "BVC"}.items()}
MODE_BYTES = {"imp": 1, "acc": 1, "imm": 2, "zp": 2, "zpx": 2,
              "zpy": 2, "izx": 2, "izy": 2, "rel": 2,
              "abs": 3, "abx": 3, "aby": 3, "ind": 3}


def split_args(source: str) -> list[str]:
    return [part.strip() for part in source.split(",") if part.strip()]


def expression(source: str, labels: dict[str, int], allow_unknown: bool) -> int:
    source = source.strip()
    if source.startswith("<"):
        return expression(source[1:], labels, allow_unknown) & 0xFF
    if source.startswith(">"):
        return (expression(source[1:], labels, allow_unknown) >> 8) & 0xFF
    source = re.sub(r"\$([0-9a-fA-F]+)", lambda m: "0x" + m.group(1), source)
    source = re.sub(r"%([01]+)", lambda m: "0b" + m.group(1), source)

    def evaluate(node: ast.AST) -> int:
        if isinstance(node, ast.Constant) and isinstance(node.value, int):
            return node.value
        if isinstance(node, ast.Name):
            key = node.id.upper()
            if key in labels:
                return labels[key]
            if allow_unknown:
                return 0
            raise ValueError(f"undefined symbol: {node.id}")
        if isinstance(node, ast.BinOp):
            left, right = evaluate(node.left), evaluate(node.right)
            if isinstance(node.op, ast.Add):
                return left + right
            if isinstance(node.op, ast.Sub):
                return left - right
            if isinstance(node.op, ast.BitAnd):
                return left & right
            if isinstance(node.op, ast.BitOr):
                return left | right
            if isinstance(node.op, ast.LShift):
                return left << right
            if isinstance(node.op, ast.RShift):
                return left >> right
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
            return -evaluate(node.operand)
        raise ValueError(f"unsupported expression: {source}")

    return evaluate(ast.parse(source, mode="eval").body)


def addressing(mnemonic: str, argument: str) -> tuple[str, str]:
    upper = argument.upper()
    if not argument:
        return "imp", ""
    if mnemonic in BRANCHES:
        return "rel", argument
    if upper == "A":
        return "acc", ""
    if argument.startswith("#"):
        return "imm", argument[1:]
    if upper.startswith("(") and upper.endswith(",X)"):
        return "izx", argument[1:-3]
    if upper.startswith("(") and upper.endswith("),Y"):
        return "izy", argument[1:-3]
    if upper.startswith("(") and upper.endswith(")"):
        return "ind", argument[1:-1]
    suffix = ""
    if upper.endswith(",X") or upper.endswith(",Y"):
        suffix = argument[-1].lower()
        argument = argument[:-2]
    # Symbolic addresses default to absolute width.  Explicit $00-$FF and
    # decimal zero-page constants select the short addressing mode.
    is_zp = is_simple_hex(argument)
    if re.fullmatch(r"[0-9]{1,3}", argument):
        is_zp = int(argument) <= 255
    if suffix:
        return ("zp" if is_zp else "ab") + suffix, argument
    return ("zp" if is_zp else "abs"), argument


def is_simple_hex(value: str) -> bool:
    return bool(re.fullmatch(r"\$[0-9a-fA-F]{1,2}", value))


def assemble(source: str, base: int = 0xE000, size: int = 0x2000,
             always_long_branches: bool = False,
             write_map: bytearray | None = None) -> tuple[bytes, dict[str, int]]:
    lines = []
    labels: dict[str, int] = {}
    pc = base
    for number, raw in enumerate(source.splitlines(), 1):
        line = raw.split(";", 1)[0].strip()
        if not line:
            continue
        while ":" in line and re.match(r"^[A-Za-z_][A-Za-z0-9_]*:", line):
            name, line = line.split(":", 1)
            name = name.upper()
            if name in labels:
                raise ValueError(f"line {number}: duplicate label {name}")
            labels[name] = pc
            line = line.strip()
        if not line:
            continue
        parts = line.split(None, 1)
        command = parts[0].upper()
        arg = parts[1].strip() if len(parts) > 1 else ""
        if command == ".ORG":
            pc = expression(arg, labels, False)
        elif command == ".BYTE":
            pc += len(split_args(arg))
        elif command == ".TEXT":
            value = ast.literal_eval(arg)
            if not isinstance(value, str) or not value.isascii():
                raise ValueError(f"line {number}: .text requires an ASCII string")
            pc += len(value)
        elif command == ".WORD":
            pc += 2 * len(split_args(arg))
        elif command == ".FILL":
            pc += expression(split_args(arg)[0], labels, False)
        elif command in LONG_BRANCHES or (always_long_branches and command in BRANCHES):
            pc += 5
        else:
            mode, _ = addressing(command, arg)
            if (command, mode) not in OPCODES:
                raise ValueError(f"line {number}: unsupported instruction {command} {arg} ({mode})")
            pc += MODE_BYTES[mode]
        lines.append((number, command, arg))

    rom = bytearray([0xFF] * size)
    written = bytearray(size)
    pc = base

    def write(value: int) -> None:
        nonlocal pc
        offset = pc - base
        if not 0 <= offset < size:
            raise ValueError(f"write outside ROM at ${pc:04X}")
        if written[offset]:
            raise ValueError(f"overlapping ROM data at ${pc:04X}")
        rom[offset] = value & 0xFF
        written[offset] = 1
        pc += 1

    for number, command, arg in lines:
        try:
            if command == ".ORG":
                pc = expression(arg, labels, False)
            elif command == ".BYTE":
                for item in split_args(arg):
                    value = expression(item, labels, False)
                    if not 0 <= value <= 255:
                        raise ValueError(f"byte out of range: {item}")
                    write(value)
            elif command == ".WORD":
                for item in split_args(arg):
                    value = expression(item, labels, False)
                    if not 0 <= value <= 65535:
                        raise ValueError(f"word out of range: {item}")
                    write(value)
                    write(value >> 8)
            elif command == ".TEXT":
                value = ast.literal_eval(arg)
                if not isinstance(value, str) or not value.isascii():
                    raise ValueError(".text requires an ASCII string")
                for character in value:
                    write(ord(character))
            elif command == ".FILL":
                items = split_args(arg)
                count = expression(items[0], labels, False)
                value = expression(items[1], labels, False) if len(items) > 1 else 0xFF
                for _ in range(count):
                    write(value)
            elif command in LONG_BRANCHES or (always_long_branches and command in BRANCHES):
                target = expression(arg, labels, False)
                opposite = LONG_BRANCHES[command if command in LONG_BRANCHES else "J" + command]
                write(OPCODES[opposite, "rel"])
                write(3)
                write(OPCODES["JMP", "abs"])
                write(target)
                write(target >> 8)
            else:
                mode, operand = addressing(command, arg)
                write(OPCODES[command, mode])
                if mode in ("imp", "acc"):
                    continue
                value = expression(operand, labels, False)
                if mode == "rel":
                    delta = value - (pc + 1)
                    if not -128 <= delta <= 127:
                        raise ValueError(f"branch out of range: {delta}")
                    write(delta)
                elif MODE_BYTES[mode] == 2:
                    if not 0 <= value <= 255:
                        raise ValueError(f"8-bit operand out of range: {operand}")
                    write(value)
                else:
                    if not 0 <= value <= 65535:
                        raise ValueError(f"16-bit operand out of range: {operand}")
                    write(value)
                    write(value >> 8)
        except (KeyError, ValueError, SyntaxError) as exc:
            raise ValueError(f"line {number}: {exc}") from exc
    if write_map is not None:
        if len(write_map) != size:
            raise ValueError("write_map length must match the assembly address range")
        write_map[:] = written
    return bytes(rom), labels


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    data, labels = assemble(args.source.read_text(encoding="utf-8"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    args.output.with_suffix(".map").write_text(
        "".join(f"{address:04X} {name}\n" for name, address in sorted(labels.items(), key=lambda item: item[1])),
        encoding="utf-8")
    print(f"Open Kernal 64: {args.output} ({len(data)} bytes)")


if __name__ == "__main__":
    main()
