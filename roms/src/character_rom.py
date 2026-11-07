#!/usr/bin/env python3
"""Build the independent 4 KiB character ROM with C64 screen-code meanings.

Letter designs come from the user-supplied open ROM (character_glyphs.py).
Graphic shapes below are drawn from geometric descriptions; no Commodore ROM
or external font binary is an input. The public code-to-symbol assignment is
the compatibility contract, not byte-identical reproduction of another font.

References for the assignments:
* Commodore 64 Programmer's Reference Guide, Appendix B, pp. 376-378.
* https://www.pagetable.com/c64ref/charset/ (screen-code/Unicode mapping).
* https://www.unicode.org/charts/PDF/U1FB00.pdf (geometric symbol names).

SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations

import argparse
import hashlib
import json
from functools import reduce
from pathlib import Path
from typing import Callable

from character_glyphs import LOWER_LETTER_ROWS, UPPER_TEXT_ROWS


def pixels(predicate: Callable[[int, int], bool]) -> bytes:
    """Eight rows, bit 7 at the left; coordinates start at the upper left."""
    return bytes(sum(0x80 >> x for x in range(8) if predicate(x, y)) for y in range(8))


def pattern(*rows: str) -> bytes:
    if len(rows) != 8 or any(len(row) != 8 or set(row) - {".", "#"} for row in rows):
        raise ValueError("A character must be eight rows of eight '.'/'#' cells")
    return pixels(lambda x, y: rows[y][x] == "#")


def join(*glyphs: bytes) -> bytes:
    return bytes(reduce(int.__or__, row) for row in zip(*glyphs))


def reflect(glyph: bytes, *, horizontal: bool = False, vertical: bool = False) -> bytes:
    return pixels(lambda x, y: bool(glyph[7 - y if vertical else y] & (0x80 >> (7 - x if horizontal else x))))


def box(*connections: str) -> bytes:
    """Two-pixel strokes meet at the central 2x2 square and reach tile edges."""
    allowed = {"up", "down", "left", "right"}
    if set(connections) - allowed:
        raise ValueError(connections)
    return pixels(lambda x, y:
                  ("up" in connections and x in (3, 4) and y <= 4) or
                  ("down" in connections and x in (3, 4) and y >= 3) or
                  ("left" in connections and y in (3, 4) and x <= 4) or
                  ("right" in connections and y in (3, 4) and x >= 3))


def graphic_glyphs() -> dict[int, bytes]:
    """All 64 upper/graphics-bank assignments, keyed by SCREEN code."""
    rounded_down_right = pattern(
        "........", "........", "........", ".....###",
        "....####", "...##...", "...##...", "...##...")
    slash_down = pixels(lambda x, y: x == y)
    slash_up = pixels(lambda x, y: x + y == 7)
    shade = pixels(lambda x, y: (x + y) % 2 == 0)
    glyphs = {
        0x40: box("left", "right"),
        0x41: pattern("...##...", "..####..", ".######.", "########",
                      "########", ".#.##.#.", "...##...", "..####.."),  # spade
        0x42: pixels(lambda x, y: x == 3),
        0x43: pixels(lambda x, y: y == 3),
        0x44: pixels(lambda x, y: y == 2),
        0x45: pixels(lambda x, y: y == 1),
        0x46: pixels(lambda x, y: y == 5),
        0x47: pixels(lambda x, y: x == 2),
        0x48: pixels(lambda x, y: x == 5),
        0x49: reflect(rounded_down_right, horizontal=True),
        0x4A: reflect(rounded_down_right, vertical=True),
        0x4B: reflect(rounded_down_right, horizontal=True, vertical=True),
        0x4C: pixels(lambda x, y: x == 0 or y == 7),
        0x4D: slash_down,
        0x4E: slash_up,
        0x4F: pixels(lambda x, y: x == 0 or y == 0),
        0x50: pixels(lambda x, y: x == 7 or y == 0),
        0x51: pattern("........", "..####..", ".######.", ".######.",
                      ".######.", ".######.", "..####..", "........"),  # filled circle
        0x52: pixels(lambda x, y: y == 6),
        0x53: pattern("........", ".##..##.", "########", "########",
                      ".######.", "..####..", "...##...", "........"),  # heart
        0x54: pixels(lambda x, y: x == 1),
        0x55: rounded_down_right,
        0x56: join(slash_down, slash_up),
        0x57: pattern("........", "..####..", ".#....#.", ".#....#.",
                      ".#....#.", ".#....#.", "..####..", "........"),  # hollow circle
        0x58: pattern("..####..", "..####..", "##.##.##", "########",
                      "##.##.##", "...##...", "..####..", "........"),  # club
        0x59: pixels(lambda x, y: x == 6),
        0x5A: pattern("...##...", "..####..", ".######.", "########",
                      ".######.", "..####..", "...##...", "........"),  # diamond
        0x5B: box("up", "down", "left", "right"),
        0x5C: pixels(lambda x, y: x < 4 and bool(shade[y] & (0x80 >> x))),
        0x5D: box("up", "down"),
        0x5E: pattern("........", "........", ".#######", "..#..#..",
                      "..#..#..", "..#..#..", ".##..##.", "........"),  # pi
        0x5F: pixels(lambda x, y: x >= y),
        0x60: bytes(8),
        0x61: pixels(lambda x, y: x < 4),
        0x62: pixels(lambda x, y: y >= 4),
        0x63: pixels(lambda x, y: y == 0),
        0x64: pixels(lambda x, y: y == 7),
        0x65: pixels(lambda x, y: x == 0),
        0x66: shade,
        0x67: pixels(lambda x, y: x == 7),
        0x68: pixels(lambda x, y: y >= 4 and bool(shade[y] & (0x80 >> x))),
        0x69: pixels(lambda x, y: x + y <= 7),
        0x6A: pixels(lambda x, y: x >= 6),
        0x6B: box("up", "down", "right"),
        0x6C: pixels(lambda x, y: x >= 4 and y >= 4),
        0x6D: box("up", "right"),
        0x6E: box("down", "left"),
        0x6F: pixels(lambda x, y: y >= 6),
        0x70: box("down", "right"),
        0x71: box("up", "left", "right"),
        0x72: box("down", "left", "right"),
        0x73: box("up", "down", "left"),
        0x74: pixels(lambda x, y: x < 2),
        0x75: pixels(lambda x, y: x < 3),
        0x76: pixels(lambda x, y: x >= 5),
        0x77: pixels(lambda x, y: y < 2),
        0x78: pixels(lambda x, y: y < 3),
        0x79: pixels(lambda x, y: y >= 5),
        0x7A: pixels(lambda x, y: x == 7 or y == 7),
        0x7B: pixels(lambda x, y: x < 4 and y >= 4),
        0x7C: pixels(lambda x, y: x >= 4 and y < 4),
        0x7D: box("up", "left"),
        0x7E: pixels(lambda x, y: x < 4 and y < 4),
        0x7F: pixels(lambda x, y: (x < 4) == (y < 4)),
    }
    if set(glyphs) != set(range(0x40, 0x80)):
        raise ValueError("Missing or duplicated graphics assignments")
    return glyphs


# Unicode mappings describe character meaning, not font data or rendering.
GRAPHIC_UNICODE = (
    0x2500, 0x2660, 0x1FB72, 0x1FB78, 0x1FB77, 0x1FB76, 0x1FB7A, 0x1FB71,
    0x1FB74, 0x256E, 0x2570, 0x256F, 0x1FB7C, 0x2572, 0x2571, 0x1FB7D,
    0x1FB7E, 0x25CF, 0x1FB7B, 0x2665, 0x1FB70, 0x256D, 0x2573, 0x25CB,
    0x2663, 0x1FB75, 0x2666, 0x253C, 0x1FB8C, 0x2502, 0x03C0, 0x25E5,
    0x00A0, 0x258C, 0x2584, 0x2594, 0x2581, 0x258F, 0x2592, 0x2595,
    0x1FB8F, 0x25E4, 0x1FB87, 0x251C, 0x2597, 0x2514, 0x2510, 0x2582,
    0x250C, 0x2534, 0x252C, 0x2524, 0x258E, 0x258D, 0x1FB88, 0x1FB82,
    0x1FB83, 0x2583, 0x1FB7F, 0x2596, 0x259D, 0x2518, 0x2598, 0x259A,
)
LOWER_SPECIAL_UNICODE = {0x5E: 0x1FB96, 0x5F: 0x1FB98, 0x69: 0x1FB99, 0x7A: 0x2713}


def build() -> bytes:
    upper = [bytes.fromhex(rows) for rows in UPPER_TEXT_ROWS]
    graphics = graphic_glyphs()
    upper += [graphics[code] for code in range(0x40, 0x80)]
    lower = list(upper)
    lower[1:27] = [bytes.fromhex(rows) for rows in LOWER_LETTER_ROWS]
    lower[0x41:0x5B] = upper[1:27]
    lower[0x5E] = pixels(lambda x, y: ((x // 2) + (y // 2)) % 2 != 0)
    lower[0x5F] = pixels(lambda x, y: (x - y) % 4 < 2)
    lower[0x69] = pixels(lambda x, y: (x + y) % 4 < 2)
    lower[0x7A] = pattern("........", ".......#", "......##", ".#...##.",
                         ".##.##..", "..###...", "...#....", "........")
    normal_upper = b"".join(upper)
    normal_lower = b"".join(lower)
    if len(normal_upper) != 1024 or len(normal_lower) != 1024:
        raise ValueError("Each bank needs 128 eight-row glyphs")
    return (normal_upper + bytes(value ^ 0xFF for value in normal_upper) +
            normal_lower + bytes(value ^ 0xFF for value in normal_lower))


def codepoint_map() -> dict[str, str]:
    text = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[£]↑←" + "".join(chr(code) for code in range(32, 64))
    result = {}
    for bank in range(2):
        meanings = [ord(char) for char in text] + list(GRAPHIC_UNICODE)
        if bank == 1:
            meanings[1:27] = list(range(ord("a"), ord("z") + 1))
            meanings[0x41:0x5B] = list(range(ord("A"), ord("Z") + 1))
            for code, value in LOWER_SPECIAL_UNICODE.items():
                meanings[code] = value
        result.update({f"{bank}:{code:02X}": f"U+{value:04X}" for code, value in enumerate(meanings)})
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, nargs="?", default=Path(__file__).parents[1] / "roms/open-character-rom64.bin")
    parser.add_argument("--map", type=Path, help="Optional JSON of screen-code meanings")
    args = parser.parse_args()
    data = build()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    if args.map:
        args.map.parent.mkdir(parents=True, exist_ok=True)
        args.map.write_text(json.dumps(codepoint_map(), indent=2) + "\n", encoding="utf-8")
    print(f"Open Character ROM 64: {args.output} ({len(data)} bytes, SHA-256 {hashlib.sha256(data).hexdigest()})")


if __name__ == "__main__":
    main()
