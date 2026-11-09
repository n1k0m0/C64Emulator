"""Instruction tests for the original-address C64/UX game loader.

SPDX-License-Identifier: Apache-2.0

Actual ROM code runs on py65. Public KERNAL file calls receive independent,
deterministic PRG streams; they are explicit contract doubles, not IEC or
1541 emulation. A separate real-emulator runner checks physical loading.
The game-entry observations are read-only: no instruction is replaced there.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import time
import unittest

from machine import PROJECT
from test_disk_contract import FileApiMachine
from play_fixtures import Fixture, fixtures, native_prg, prg, sys_prg


class PlayApiMachine(FileApiMachine):
    def __init__(self):
        super().__init__()
        self.reset_count = 0
        self.handoff_count = 0
        self.expected: Fixture | None = None
        self.entries: list[dict] = []
        self.read_sessions: Counter = Counter()
        self.second_pass_payload: dict[str, bytes] = {}
        self.second_pass_fault: dict[str, int] = {}

    def step(self):
        pc = self.cpu.pc
        if pc == self.symbols.get("RESET") and self.memory.kernal_visible:
            self.reset_count += 1
        if pc == self.symbols.get("PLAY_HANDOFF") and self.memory.kernal_visible:
            self.handoff_count += 1
        fixture = self.expected
        if fixture and fixture.entry is not None and pc == fixture.entry and self.handoff_count > len(self.entries):
            self.entries.append({
                "pc": pc, "a": self.cpu.a, "x": self.cpu.x, "y": self.cpu.y,
                "sp": self.cpu.sp, "flags": self.cpu.p,
                "port": self.memory.port, "ddr": self.ram[0],
                "d011": self.memory[0xD011], "d015": self.memory[0xD015],
                "d016": self.memory[0xD016], "d018": self.memory[0xD018],
                "border": self.memory[0xD020], "background": self.memory[0xD021],
                "text_color": self.ram[0x0286], "device": self.ram[0xBA],
                "basic_start": int.from_bytes(self.ram[0x2B:0x2D], "little"),
                "basic_end_pointers": [int.from_bytes(self.ram[p:p + 2], "little") for p in (0x2D, 0x2F, 0x31)],
                "string_top": int.from_bytes(self.ram[0x33:0x35], "little"),
                "memory_top": int.from_bytes(self.ram[0x37:0x39], "little"),
                "text_pointer": int.from_bytes(self.ram[0x7A:0x7C], "little"),
                "payload": bytes(self.ram[fixture.start:fixture.end]),
                "open_channels": sorted(self.channels),
                "irq_vector": int.from_bytes(self.ram[0x0314:0x0316], "little"),
            })
        if pc == 0xFFC0 and self.memory.kernal_visible and self.secondary != 15:
            name = self.filename.split(",")[0]
            self.read_sessions[name] += 1
            if self.read_sessions[name] == 2:
                if name in self.second_pass_payload:
                    self.files[name] = ("P", self.second_pass_payload[name])
                if name in self.second_pass_fault:
                    self.read_fail_after[name] = self.second_pass_fault[name]
        super().step()


class PlayTests(unittest.TestCase):
    def setUp(self):
        self.machine = PlayApiMachine()
        self.machine.boot()
        self.samples = {fixture.name: fixture for fixture in fixtures()}
        for fixture in self.samples.values():
            self.machine.files[fixture.name] = ("P", fixture.data)

    def remember(self):
        self.machine.command("echo keep-me > keeper")
        self.assertEqual(self.machine.local_file("keeper"), b"keep-me\n")

    def command(self, command: str, cycles: int = 20_000_000) -> str:
        output = self.machine.command(command, max_cycles=cycles)
        self.assertFalse(self.machine.channels, "Leaked file channel")
        self.assertEqual((self.machine.ram[0x99], self.machine.ram[0x9A]), (0, 3))
        return output

    def launch(self, fixture: Fixture, argument: str | None = None) -> tuple[str, dict]:
        self.machine.files[fixture.name] = ("P", fixture.data)
        self.machine.expected = fixture
        before_entries = len(self.machine.entries)
        before_reset = self.machine.reset_count
        output = self.command(f"play {fixture.name}" + (f" {argument}" if argument else ""))
        self.assertEqual(len(self.machine.entries), before_entries + 1, output)
        self.assertEqual(self.machine.reset_count, before_reset + 1, "RTS must cold-reset to a fresh shell")
        snapshot = self.machine.entries[-1]
        self.assertEqual(snapshot["payload"], fixture.data[2:], "Loaded payload changed before execution")
        self.assertEqual(snapshot["open_channels"], [])
        self.assertIn("root@c64:/home/root# ", output)
        if fixture.message:
            self.assertIn(fixture.message, output)
        return output, snapshot

    def rejected(self, command: str, *, expect_preserved: bool = True, cycles: int = 20_000_000):
        before_entries = len(self.machine.entries)
        before_reset = self.machine.reset_count
        output = self.command(command, cycles)
        self.assertEqual(len(self.machine.entries), before_entries, output)
        self.assertRegex(output.lower(), r"(?:^|\n)(?:play:|usage: play )", output)
        if expect_preserved:
            self.assertEqual(self.machine.reset_count, before_reset, "Preflight failure destroyed the current session")
            self.assertEqual(self.machine.local_file("keeper"), b"keep-me\n")
        else:
            self.assertEqual(self.machine.reset_count, before_reset + 1, "Failed commit must reset, not use overwritten shell state")
            self.assertIsNone(self.machine.local_file("keeper"))
            self.assertIn("root@c64:/home/root# ", output)
        return output

    def test_basic_sys_overwrites_shell_workspace_and_returns_by_reset(self):
        self.remember()
        fixture = self.samples["SYSDEMO"]
        self.assertGreater(fixture.end, 0x1400)
        output, snapshot = self.launch(fixture)
        self.assertEqual(self.machine.ram[0xCFF0], 0xA5)
        self.assertIsNone(self.machine.local_file("keeper"))
        self.assertEqual(self.machine.read_sessions[fixture.name], 2)
        self.assertEqual(snapshot["pc"], 0x0810)

    def test_game_entry_has_c64_registers_memory_pointers_and_terminal(self):
        _, state = self.launch(self.samples["SYSDEMO"])
        self.assertEqual((state["a"], state["x"], state["y"], state["sp"]), (0, 0, 0, 0xFD))
        self.assertFalse(state["flags"] & self.machine.cpu.DECIMAL)
        self.assertFalse(state["flags"] & self.machine.cpu.INTERRUPT)
        self.assertEqual(state["ddr"], 0x2F)
        self.assertEqual(state["port"] & 7, 7)
        self.assertEqual((state["d011"], state["d015"], state["d016"], state["d018"]),
                         (0x1B, 0, 0x08, 0x15))
        self.assertEqual((state["border"], state["background"], state["text_color"]), (14, 6, 14))
        self.assertEqual(state["basic_start"], 0x0801)
        self.assertEqual(state["basic_end_pointers"], [self.samples["SYSDEMO"].end] * 3)
        self.assertEqual((state["string_top"], state["memory_top"], state["text_pointer"], state["device"]),
                         (0xA000, 0xA000, 0x0800, 8))
        self.assertEqual(state["irq_vector"], self.machine.symbols["PRIVATE_IRQ_SERVICE"])

    def test_whitespace_and_leading_zeroes_in_literal_sys(self):
        self.launch(self.samples["SYSSPACE"])
        fixture = Fixture("ZEROS", sys_prg(0x0820, body=b"\x9E0002080", message="PLAY ZEROES OK"),
                          0x0820, "PLAY ZEROES OK")
        self.launch(fixture)

    def test_direct_programs_use_their_original_addresses(self):
        for address in (0x0800, 0x2000, 0x8000, 0xC000):
            with self.subTest(address=f"${address:04X}"):
                fixture = Fixture(f"RAW{address:04X}", native_prg(address), address)
                _, state = self.launch(fixture)
                self.assertEqual(state["port"] & 7, 7)
                self.assertEqual(self.machine.ram[0xCFF0], 0xA5)

    def test_ram_under_basic_rom_is_loaded_and_executable(self):
        _, state = self.launch(self.samples["UNDERROM"])
        self.assertEqual(state["port"] & 7, 6)
        self.assertEqual(self.machine.ram[0xCFF0], 0xA5)
        fixture = Fixture("LASTBASIC", prg(0xBFFF, b"\x60"), 0xBFFF)
        _, state = self.launch(fixture)
        self.assertEqual(state["port"] & 7, 6)

    def test_explicit_decimal_and_hex_entry_are_inside_payload(self):
        for argument in ("2112", "$0840"):
            with self.subTest(argument=argument):
                self.launch(self.samples["EXPLICIT"], argument)
        fixture = Fixture("ENTRYC000", native_prg(0xBFF0, entry=0xC000), 0xC000)
        _, state = self.launch(fixture, "$c000")
        self.assertEqual(state["port"] & 7, 7)

    def test_return_trampoline_restores_banking_and_reset_clears_sprites(self):
        self.launch(self.samples["BANKRTS"])
        self.assertEqual(self.machine.memory.port & 7, 7)
        self.assertTrue(self.machine.memory.basic_visible)
        self.assertTrue(self.machine.memory.kernal_visible)
        sprite = Fixture("SPRITE", prg(0x2000, b"\xA9\xFF\x8D\x15\xD0\x60"), 0x2000)
        self.launch(sprite)
        self.assertEqual(self.machine.memory[0xD015], 0)
        self.assertEqual(self.machine.memory[0xD018], 0x17)

    def test_last_ram_byte_is_valid_but_io_boundary_is_rejected(self):
        self.launch(self.samples["TOPRAM"])
        self.remember()
        self.machine.ram[0xCFFF] = 0x5A
        self.machine.memory[0xD000] = 0xA5
        for name in ("LOW", "IOAREA", "CROSSIO"):
            with self.subTest(name=name):
                self.rejected(f"play {name}")
                self.assertEqual(self.machine.ram[0xCFFF], 0x5A)
                self.assertEqual(self.machine.memory[0xD000], 0xA5)

    def test_unsupported_or_malformed_basic_is_rejected_before_loading(self):
        self.remember()
        for name in ("BADLINK", "EXPR", "MULTILINE", "BASIC"):
            with self.subTest(name=name):
                self.rejected(f"play {name}")
        for target in (b"0", b"65536", b"-1", b"2064+0"):
            with self.subTest(target=target):
                data = sys_prg(0x0840, body=b"\x9E" + target, message="MUST NOT RUN")
                self.machine.files["BADSYSTEM"] = ("P", data)
                self.rejected("play badsystem")
        raw = self.samples["SYSDEMO"].data
        for length in (2, 3, 6, 11, 12):
            with self.subTest(truncated_length=length):
                self.machine.files["TRUNCATED"] = ("P", raw[:length])
                self.rejected("play truncated")

    def test_empty_headers_and_loads_outside_supported_ram_are_rejected(self):
        self.remember()
        for index, data in enumerate((b"", b"\x01", prg(0x0800, b""), prg(0x0000, b"\x60"),
                                      prg(0xE000, b"\x60"), prg(0xFFF0, b"\xEA" * 64))):
            with self.subTest(index=index):
                self.machine.files["INVALID"] = ("P", data)
                self.rejected("play invalid")

    def test_explicit_entry_syntax_and_bounds_are_checked(self):
        self.remember()
        for argument in ("0", "$07ff", "65536", "$10000", "-1", "0x0840", "$", "$g000", "32768"):
            with self.subTest(argument=argument):
                self.rejected(f"play explicit {argument}")
        # An address exactly at the exclusive end must not be executed.
        self.rejected(f"play explicit {self.samples['EXPLICIT'].end}")

    def test_missing_file_and_first_pass_io_failure_preserve_session(self):
        self.remember()
        self.rejected("play absent")
        self.machine.read_fail_after["SYSDEMO"] = 300
        self.rejected("play sysdemo")
        self.assertEqual(self.machine.handoff_count, 0)

    def test_second_pass_io_failure_cold_resets_after_partial_write(self):
        self.remember()
        self.machine.second_pass_fault["SYSDEMO"] = 800
        self.rejected("play sysdemo", expect_preserved=False)
        self.assertEqual(self.machine.handoff_count, 0)

    def test_changed_second_pass_data_never_reaches_game_entry(self):
        for mutation in ("header", "shorter", "longer", "checksum"):
            with self.subTest(mutation=mutation):
                self.setUp()
                self.remember()
                data = self.samples["SYSDEMO"].data
                if mutation == "header":
                    changed = b"\x02\x08" + data[2:]
                elif mutation == "shorter":
                    changed = data[:-1]
                elif mutation == "longer":
                    changed = data + b"\x19"
                else:
                    changed = data[:700] + bytes((data[700] ^ 0x80,)) + data[701:]
                self.machine.second_pass_payload["SYSDEMO"] = changed
                # Header changes can be rejected before the first destructive
                # store; implementations may still deliberately cold reset.
                if mutation == "header":
                    output = self.command("play sysdemo")
                    self.assertIn("play:", output.lower())
                    self.assertEqual(self.machine.handoff_count, 0)
                    self.assertIn(self.machine.local_file("keeper"), (None, b"keep-me\n"))
                else:
                    self.rejected("play sysdemo", expect_preserved=False)
                    self.assertEqual(self.machine.handoff_count, 0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", type=Path, default=PROJECT / "docs/play-contract.json")
    args = parser.parse_args()
    started = time.monotonic()
    images = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
              for path in sorted((PROJECT / "roms").glob("*.bin"))}
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(PlayTests)
    names = [test.id() for test in suite]
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    failed = [test.id() for test, _ in result.failures + result.errors]
    unchanged = all(hashlib.sha256((PROJECT / "roms" / name).read_bytes()).hexdigest() == digest
                    for name, digest in images.items())
    report = {
        "suite": "C64/UX game loader instruction contract",
        "actual_rom_instructions": True,
        "controlled_public_kernal_file_endpoints": True,
        "iec_signaling_verified": False,
        "rom_sha256": images,
        "images_unchanged_during_run": unchanged,
        "tests_run": result.testsRun,
        "tests": [{"name": name, "passed": not any(item.startswith(name) for item in failed)} for name in names],
        "failures": [{"test": str(test), "detail": detail} for test, detail in result.failures],
        "errors": [{"test": str(test), "detail": detail} for test, detail in result.errors],
        "success": result.wasSuccessful() and unchanged,
        "seconds": round(time.monotonic() - started, 3),
    }
    args.json.parent.mkdir(parents=True, exist_ok=True)
    args.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    raise SystemExit(0 if report["success"] else 1)


if __name__ == "__main__":
    main()
