#!/usr/bin/env python3
"""Check the published screen-code contract and the generated ROM's pixels.

Run: python tests/character_contract.py [path/to/character-rom.bin]
No emulator, Pillow, or third-party Python package is needed.
SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations

import argparse
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
import character_rom


class CharacterContract(unittest.TestCase):
    rom = character_rom.build()

    def glyph(self, code: int, bank: int = 0) -> bytes:
        start = bank * 2048 + code * 8
        return self.rom[start:start + 8]

    def points(self, code: int, bank: int = 0) -> set[tuple[int, int]]:
        return {(x, y) for y, row in enumerate(self.glyph(code, bank))
                for x in range(8) if row & (128 >> x)}

    def test_geometry_and_build_reproducibility(self) -> None:
        self.assertEqual(len(self.rom), 4096)
        self.assertEqual(self.rom, character_rom.build())

    def test_inverse_for_every_glyph(self) -> None:
        for bank in (0, 1):
            for code in range(128):
                self.assertEqual(self.glyph(code + 128, bank),
                                 bytes(value ^ 255 for value in self.glyph(code, bank)))

    def test_text_banks_and_shared_symbols(self) -> None:
        for code in range(1, 27):
            self.assertNotEqual(self.glyph(code, 0), self.glyph(code, 1))
            self.assertEqual(self.glyph(code, 0), self.glyph(code + 64, 1))
        for code in (0, *range(27, 65), 91, 92, 93, *range(96, 105), *range(106, 122), *range(123, 128)):
            self.assertEqual(self.glyph(code, 0), self.glyph(code, 1), f"screen {code}")
        for bank in (0, 1):
            for code in (32, 96):
                self.assertEqual(self.glyph(code, bank), bytes(8))

    def test_solid_fraction_blocks_and_quadrants(self) -> None:
        # Bounds are independent expectations from the screen-code chart.
        bounds = {
            0x61: (0, 4, 0, 8), 0x62: (0, 8, 4, 8),
            0x63: (0, 8, 0, 1), 0x64: (0, 8, 7, 8),
            0x65: (0, 1, 0, 8), 0x67: (7, 8, 0, 8),
            0x6A: (6, 8, 0, 8), 0x6C: (4, 8, 4, 8),
            0x6F: (0, 8, 6, 8), 0x74: (0, 2, 0, 8),
            0x75: (0, 3, 0, 8), 0x76: (5, 8, 0, 8),
            0x77: (0, 8, 0, 2), 0x78: (0, 8, 0, 3),
            0x79: (0, 8, 5, 8), 0x7B: (0, 4, 4, 8),
            0x7C: (4, 8, 0, 4), 0x7E: (0, 4, 0, 4),
        }
        for code, (x0, x1, y0, y1) in bounds.items():
            expected = {(x, y) for x in range(x0, x1) for y in range(y0, y1)}
            self.assertEqual(self.points(code), expected, f"screen ${code:02X}")
        self.assertEqual(self.points(0x7F), self.points(0x7E) | self.points(0x6C))

    def test_fine_lines_have_correct_positions(self) -> None:
        for code, column in {0x42: 3, 0x47: 2, 0x48: 5, 0x54: 1, 0x59: 6}.items():
            self.assertEqual(self.points(code), {(column, y) for y in range(8)})
        for code, row in {0x43: 3, 0x44: 2, 0x45: 1, 0x46: 5, 0x52: 6}.items():
            self.assertEqual(self.points(code), {(x, row) for x in range(8)})

    def test_box_drawing_joins_reach_expected_edges(self) -> None:
        # Joining adjacent tiles must not leave gaps or unexpected branches.
        expected = {
            0x40: "LR", 0x49: "DL", 0x4A: "UR", 0x4B: "UL",
            0x55: "DR", 0x5B: "UDLR", 0x5D: "UD", 0x6B: "UDR",
            0x6D: "UR", 0x6E: "DL", 0x70: "DR", 0x71: "ULR",
            0x72: "DLR", 0x73: "UDL", 0x7D: "UL",
        }
        for code, directions in expected.items():
            p = self.points(code)
            edges = {
                "U": {(x, 0) for x in (3, 4)}, "D": {(x, 7) for x in (3, 4)},
                "L": {(0, y) for y in (3, 4)}, "R": {(7, y) for y in (3, 4)},
            }
            for direction, cells in edges.items():
                self.assertEqual(p & cells, cells if direction in directions else set(),
                                 f"screen ${code:02X}, edge {direction}")

    def test_edge_corners_diagonals_and_triangles(self) -> None:
        for code, edges in {0x4C: (0, 7), 0x4F: (0, 0), 0x50: (7, 0), 0x7A: (7, 7)}.items():
            x, y = edges
            self.assertEqual(self.points(code), {(x, row) for row in range(8)} | {(col, y) for col in range(8)})
        self.assertEqual(self.points(0x4D), {(n, n) for n in range(8)})
        self.assertEqual(self.points(0x4E), {(n, 7 - n) for n in range(8)})
        self.assertEqual(self.points(0x56), self.points(0x4D) | self.points(0x4E))
        self.assertEqual(self.points(0x5F), {(x, y) for x in range(8) for y in range(8) if x >= y})
        self.assertEqual(self.points(0x69), {(x, y) for x in range(8) for y in range(8) if x + y <= 7})

    def test_circle_and_card_suits_regression(self) -> None:
        # Commodore's manual POKE 1504,81 example promises a filled circle.
        filled, hollow = self.points(81), self.points(87)
        self.assertTrue(hollow < filled)
        self.assertTrue({(3, 3), (4, 3), (3, 4), (4, 4)} <= filled)
        self.assertFalse({(3, 3), (4, 4)} & hollow)
        self.assertGreater(len({y for _, y in filled}), 2)  # old ROM had two bars
        suits = [self.glyph(code) for code in (65, 83, 88, 90)]
        self.assertEqual(len(set(suits)), 4)
        for code in (65, 83, 88, 90):
            p = self.points(code)
            self.assertEqual(p, {(7 - x, y) for x, y in p})
            self.assertGreater(len({x for x, _ in p}), 4)  # spade was a vertical bar
        self.assertEqual(self.glyph(65)[-1], 0x3C)  # stem; distinct from diamond
        self.assertEqual(self.glyph(90)[-1], 0x00)

    def test_shades_and_lowercase_specials(self) -> None:
        shade = self.points(0x66)
        self.assertEqual(len(shade), 32)
        self.assertEqual(self.points(0x5C), {(x, y) for x, y in shade if x < 4})
        self.assertEqual(self.points(0x68), {(x, y) for x, y in shade if y >= 4})
        for code in (0x5E, 0x5F, 0x69, 0x7A):
            self.assertNotEqual(self.glyph(code, 0), self.glyph(code, 1))
        for code in (0x5E, 0x5F, 0x69):
            self.assertEqual(len(self.points(code, 1)), 32)
        self.assertNotEqual(self.glyph(0x5F, 1), self.glyph(0x69, 1))
        self.assertEqual(self.glyph(0x5E)[2], 0x7F)  # pi's overbar, not a block
        self.assertIn((7, 1), self.points(0x7A, 1))  # check mark's high right tip

    def test_published_codepoint_assignments(self) -> None:
        mapping = character_rom.codepoint_map()
        self.assertEqual(len(mapping), 256)
        expected = {"0:41": "U+2660", "0:51": "U+25CF", "0:53": "U+2665",
                    "0:58": "U+2663", "0:5A": "U+2666", "0:5E": "U+03C0",
                    "0:5F": "U+25E5", "0:69": "U+25E4", "1:01": "U+0061",
                    "1:41": "U+0041", "1:5E": "U+1FB96", "1:5F": "U+1FB98",
                    "1:69": "U+1FB99", "1:7A": "U+2713"}
        for key, value in expected.items():
            self.assertEqual(mapping[key], value)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", nargs="?", type=Path)
    args, unittest_args = parser.parse_known_args()
    if args.rom:
        CharacterContract.rom = args.rom.read_bytes()
    unittest.main(argv=[sys.argv[0], *unittest_args], verbosity=2)
