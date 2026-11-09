"""Behavioral regression tests that execute the delivered ROM instructions.

SPDX-License-Identifier: Apache-2.0

Run from the package root: python tests/test_rom.py
Install the independent CPU core: python -m pip install -r tests/requirements.txt
Use --json PATH to write a machine-readable verification report.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys
import time
import unittest

from machine import Machine, PROJECT


class RomTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.machine = Machine()
        self.boot_output = self.machine.boot()

    def command(self, command: str, *, max_cycles: int = 4_000_000) -> str:
        return self.machine.command(command, max_cycles=max_cycles)

    def body(self, command: str, *, max_cycles: int = 4_000_000) -> str:
        """Remove echoed input and the next prompt from a command result."""
        output = self.command(command, max_cycles=max_cycles)
        # Every normal command echoes one input line; the final line is
        # the next prompt.  Tests use text files terminated by a newline.
        lines = output.split("\n")
        self.assertGreaterEqual(len(lines), 2, repr(output))
        return "\n".join(lines[1:-1])

    def assertError(self, command: str) -> str:
        output = self.body(command)
        self.assertTrue(output.strip(), f"Expected a diagnostic for {command!r}")
        self.assertNotIn("unsupported opcode", output.lower())
        return output


class BootAndAbiTests(RomTestCase):
    def test_geometry_vectors_and_prompt(self) -> None:
        roms = PROJECT / "roms"
        self.assertEqual((roms / "open-basic64.bin").stat().st_size, 8192)
        self.assertEqual((roms / "open-kernal64.bin").stat().st_size, 8192)
        self.assertEqual((roms / "open-character-rom64.bin").stat().st_size, 4096)
        basic = (roms / "open-basic64.bin").read_bytes()
        kernal = (roms / "open-kernal64.bin").read_bytes()
        for offset in (0, 2):
            vector = int.from_bytes(basic[offset:offset + 2], "little")
            self.assertTrue(0xA004 <= vector < 0xC000)
        for offset in (0x1FFA, 0x1FFC, 0x1FFE):
            vector = int.from_bytes(kernal[offset:offset + 2], "little")
            self.assertTrue(0xE000 <= vector < 0xFFFA)
        for address in range(0xFF81, 0xFFF4, 3):
            self.assertIn(kernal[address - 0xE000], (0x4C, 0x6C), f"${address:04X}")
        self.assertIn("/home/root", self.body("pwd"))
        self.assertIn("root", self.body("whoami").lower())
        self.assertTrue(self.machine.memory.basic_visible)
        self.assertTrue(self.machine.memory.kernal_visible)
        self.assertTrue(self.machine.memory.io_visible)

    def test_nonzero_power_on_ram(self) -> None:
        for seed in (1, 0xC64):
            with self.subTest(seed=seed):
                machine = Machine(seed=seed)
                machine.boot()
                self.assertIn("/home/root", machine.command("pwd"))
                self.assertIn("readycheck", machine.command("echo readycheck"))

    def test_kernal_public_screen_and_iobase(self) -> None:
        self.machine.call(0xFFED)  # SCREEN
        self.assertEqual((self.machine.cpu.x, self.machine.cpu.y), (40, 25))
        self.machine.call(0xFFF3)  # IOBASE
        self.assertEqual((self.machine.cpu.x, self.machine.cpu.y), (0, 0xDC))

    def test_jiffy_carries_and_real_timer_interrupts(self) -> None:
        machine = self.machine
        machine.cpu.p |= machine.cpu.INTERRUPT
        machine.ram[0xA0:0xA3] = bytes((0x12, 0xFF, 0xFF))
        machine.call(0xFFEA)  # UDTIM
        self.assertEqual(machine.ram[0xA0:0xA3], bytes((0x13, 0, 0)))
        machine.cpu.p &= ~machine.cpu.INTERRUPT
        before = machine.jiffies
        machine.run_cycles(200_000)
        self.assertGreater(machine.jiffies, before)
        self.assertGreater(machine.interrupts, 0)
        self.assertTrue(self.body("uptime").strip())

    def test_uptime_formats_the_actual_24bit_clock(self) -> None:
        machine = self.machine
        machine.cpu.p |= machine.cpu.INTERRUPT
        for ticks in (0, 1, 59, 60, 61, 600, 65535, 0xFFFFFF):
            with self.subTest(ticks=ticks):
                machine.ram[0xA0:0xA3] = bytes((ticks >> 16, (ticks >> 8) & 255, ticks & 255))
                start = len(machine.output)
                machine.call("CMD_UPTIME")
                self.assertEqual(machine.output[start:],
                                 f"up {ticks // 60} seconds (60 Hz jiffies)\n")

    def test_empty_unknown_and_syntax_error_recover(self) -> None:
        self.assertEqual(self.body(""), "")
        self.assertEqual(self.body("   "), "")
        self.assertError("no_such_command")
        self.assertError('echo "unterminated')
        self.assertIn("aftererror", self.body("echo aftererror"))

    def test_warm_preserves_files_cold_boot_recreates_defaults(self) -> None:
        self.body("echo persistence > /home/root/probe")
        self.machine.warm_reset()
        self.assertIn("persistence", self.body("cat /home/root/probe"))
        self.machine.cold_reset()
        self.assertError("cat /home/root/probe")
        self.assertTrue(self.body("cat /etc/motd").strip())


class ShellAndFilesystemTests(RomTestCase):
    def test_all_builtins_remain_reachable_after_rom_relocation(self) -> None:
        # A command-name address can end in $00. Only a full null word
        # terminates the ROM dispatch table; exercise each documented name
        # without depending on the current symbol addresses.
        names = ("help uname whoami hostname echo clear pwd cd ls cat touch "
                 "mkdir rm rmdir cp mv wc hexdump mem free uptime ps sh "
                 "dcat import export load run").split()
        for name in names:
            with self.subTest(command=name):
                output = self.command(name)
                self.assertNotIn("command not found", output.lower())

    def test_help_system_and_process_commands(self) -> None:
        help_text = self.body("help").lower()
        for name in ("ls", "cat", "echo", "mkdir", "uptime"):
            self.assertIn(name, help_text)
        for command in ("uname", "hostname", "mem", "ps"):
            with self.subTest(command=command):
                self.assertTrue(self.body(command).strip())

    def test_quote_case_spaces_and_general_redirection(self) -> None:
        self.assertEqual(self.body('echo "Hello world"'), "Hello world")
        self.assertEqual(self.body("echo 'second phrase'"), "second phrase")
        self.body('echo "Hello world" > note')
        self.assertEqual(self.body("cat note"), "Hello world")
        self.body("echo second >> note")
        self.assertEqual(self.body("cat note"), "Hello world\nsecond")
        self.body("pwd > where")
        self.assertEqual(self.body("cat where"), "/home/root")
        self.body("echo replaced > note")
        self.assertEqual(self.body("cat note"), "replaced")

    def test_redirection_tokens_inside_quotes_remain_text(self) -> None:
        self.assertEqual(self.body('echo "a > b"'), "a > b")
        self.assertEqual(self.body('echo ">" ">>"'), "> >>")
        self.body('echo "a > b" > literal')
        self.assertEqual(self.body("cat literal"), "a > b")
        self.assertError("echo a b c d e f g h")
        self.assertEqual(self.body("echo a b c d e f g"), "a b c d e f g")

    def test_overlong_input_is_discarded_before_side_effects(self) -> None:
        self.body("echo protected > kept")
        diagnostic = self.assertError("echo overwritten > kept" + " " * 140)
        self.assertIn("discard", diagnostic.lower())
        self.assertEqual(self.body("cat kept"), "protected")
        self.assertEqual(self.body("echo recovered"), "recovered")

    def test_backspace_and_last_line_recall(self) -> None:
        self.assertEqual(self.body("echo abcd\x14\x14XY"), "abXY")
        self.body("echo recalled")
        start = len(self.machine.output)
        self.machine.type_text(b"\x91\r")  # cursor up, then Enter
        self.machine.run_until(lambda machine: machine.idle())
        self.assertIn("\nrecalled\n", self.machine.output[start:])

    def test_backspace_at_the_40_column_wrap(self) -> None:
        machine = self.machine
        initial_row, initial_column = machine.ram[0xD6], machine.ram[0xD3]
        count = (40 - initial_column - len("echo ")) % 40
        self.assertGreater(count, 0)
        machine.type_text("echo " + "x" * count)
        machine.run_until(lambda m: m.idle())
        self.assertEqual((machine.ram[0xD6], machine.ram[0xD3]), (initial_row + 1, 0))
        machine.type_text(b"\x14")
        machine.run_until(lambda m: m.idle())
        self.assertEqual((machine.ram[0xD6], machine.ram[0xD3]), (initial_row, 39))
        start = len(machine.output)
        machine.type_text("YZ\r")
        machine.run_until(lambda m: m.idle())
        self.assertIn("\n" + "x" * (count - 1) + "YZ\n", machine.output[start:])
        self.assertLess(machine.ram[0xD3], 40)

    def test_touch_copy_move_remove(self) -> None:
        self.body("touch empty")
        self.assertEqual(self.body("cat empty"), "")
        self.body("echo original > source")
        self.body("cp source copy")
        self.body("echo changed > source")
        self.assertEqual(self.body("cat copy"), "original")
        self.body("mv copy moved")
        self.assertError("cat copy")
        self.assertEqual(self.body("cat moved"), "original")
        self.body("rm moved")
        self.assertError("cat moved")
        self.assertEqual(self.body("cat source"), "changed")

    def test_nested_directories_and_relative_paths(self) -> None:
        self.body("mkdir work")
        self.body("mkdir work/sub")
        self.body("cd work/sub")
        self.assertEqual(self.body("pwd"), "/home/root/work/sub")
        self.body("echo nested > item")
        self.assertEqual(self.body("cat ../sub/item"), "nested")
        self.body("cd ../..")
        self.assertEqual(self.body("pwd"), "/home/root")
        self.assertEqual(self.body("cat work/sub/item"), "nested")
        self.body("cd /")
        self.body("cd ..")
        self.assertEqual(self.body("pwd"), "/")

    def test_directory_type_and_nonempty_guards(self) -> None:
        self.body("mkdir work")
        self.body("echo data > work/file")
        self.assertError("rmdir work")
        self.assertError("rm work")
        self.assertError("cd work/file")
        self.assertEqual(self.body("pwd"), "/home/root")
        self.assertEqual(self.body("cat work/file"), "data")
        self.body("rm work/file")
        self.body("rmdir work")
        self.assertError("cd work")

    def test_name_limit_and_missing_parent(self) -> None:
        short_name, long_name = "abcdefghijklmno", "abcdefghijklmnop"
        self.body(f"echo safe > {short_name}")
        self.assertEqual(self.body(f"cat {short_name}"), "safe")
        self.assertError(f"touch {long_name}")
        self.assertEqual(self.body(f"cat {short_name}"), "safe")
        self.assertError("touch missing/child")

    def test_file_capacity_failure_keeps_other_files(self) -> None:
        self.body("echo keeper > neighbor")
        chunk = "x" * 100
        self.body(f"echo {chunk} > full")
        for _ in range(4):
            self.body(f"echo {chunk} >> full")
        before = self.body("cat full")
        self.assertEqual(before, "\n".join([chunk] * 5))
        self.assertError(f"echo {chunk} >> full")
        after = self.body("cat full")
        self.assertTrue(after.startswith(before))
        self.assertLessEqual(len(after), 512)
        self.assertEqual(self.body("cat neighbor"), "keeper")

    def test_entry_exhaustion_is_reported_and_recoverable(self) -> None:
        successes = []
        first_error = None
        for index in range(40):
            name = f"f{index:02}"
            output = self.body(f"touch {name}")
            if output.strip():
                first_error = output
                break
            successes.append(name)
        self.assertIsNotNone(first_error, "RAMFS accepted more than its 32 entries")
        self.assertGreater(len(successes), 0)
        self.assertLess(len(successes), 32)
        self.body(f"rm {successes[-1]}")
        self.assertEqual(self.body("touch again"), "")
        self.assertEqual(self.body("cat again"), "")

    def test_ls_wc_and_hexdump_observe_saved_data(self) -> None:
        self.body("echo abc > sample")
        self.assertIn("sample", self.body("ls"))
        wc = self.body("wc sample")
        self.assertRegex(wc, r"\b4\b")  # three ASCII characters + line feed
        dump = self.body("hexdump sample").lower()
        for value in ("61", "62", "63", "0a"):
            self.assertIn(value, dump)

    def test_script_executes_real_shell_commands(self) -> None:
        self.body('echo "echo scripted > result" > script')
        self.body('echo "echo tail >> result" >> script')
        self.body("sh script")
        self.assertEqual(self.body("cat result"), "scripted\ntail")


class KeyboardTests(RomTestCase):
    def scan(self, *keys: str) -> bytes:
        machine = self.machine
        machine.cpu.p |= machine.cpu.INTERRUPT
        machine.ram[0xC6] = 0
        machine.release_keys()
        machine.call(0xFF9F)  # SCNKEY: observe a release before this contact
        machine.press_keys(*keys)
        machine.call(0xFF9F)
        return bytes(machine.ram[0x277:0x277 + machine.ram[0xC6]])

    def test_physical_letter_shift_and_ctrl_c(self) -> None:
        self.assertEqual(self.scan("A"), b"A")
        self.assertEqual(self.scan("LSHIFT", "A"), b"\xC1")
        self.assertEqual(self.scan("RSHIFT", "A"), b"\xC1")
        self.assertEqual(self.scan("LSHIFT", "."), b">")
        self.assertEqual(self.scan("LSHIFT", "2"), b'"')
        self.assertEqual(self.scan("CTRL", "C"), b"\x03")
        self.assertEqual(self.scan("STOP"), b"\x03")

    def test_repeat_delay_and_queue_limit(self) -> None:
        machine = self.machine
        self.assertEqual(self.scan("A"), b"A")
        machine.ram[0xC6] = 0
        for _ in range(10):
            machine.call(0xFF9F)
        self.assertEqual(machine.ram[0xC6], 0, "Held key repeated without its initial delay")
        for _ in range(60):
            machine.call(0xFF9F)
        self.assertGreater(machine.ram[0xC6], 0)
        self.assertLessEqual(machine.ram[0xC6], 10)
        machine.ram[0x281] = 0x5A  # first byte beyond the ten-byte queue
        machine.ram[0xC6] = 10
        for _ in range(20):
            machine.call(0xFF9F)
        self.assertEqual(machine.ram[0xC6], 10)
        self.assertEqual(machine.ram[0x281], 0x5A)

    def test_cancel_line_and_physical_return(self) -> None:
        machine = self.machine
        machine.type_text("echo cancelled")
        machine.run_until(lambda m: not m._input and m.ram[0xC6] == 0 and m.idle())
        machine.type_text(b"\x03")
        machine.run_until(lambda m: m.idle())
        self.assertIn("alive", self.body("echo alive"))
        machine.press_keys("RETURN")
        before = len(machine.raw_output)
        machine.run_cycles(40_000)
        machine.release_keys()
        machine.run_cycles(40_000)
        self.assertGreater(len(machine.raw_output), before)


class RecordingResult(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.records: list[dict] = []
        self._started = 0.0

    def startTest(self, test) -> None:
        self._started = time.perf_counter()
        super().startTest(test)

    def _record(self, test, status: str, detail: str | None = None) -> None:
        item = {"test": test.id(), "status": status,
                "seconds": round(time.perf_counter() - self._started, 4)}
        if detail:
            item["detail"] = detail
        self.records.append(item)

    def addSuccess(self, test) -> None:
        self._record(test, "passed")
        super().addSuccess(test)

    def addFailure(self, test, err) -> None:
        self._record(test, "failed", self._exc_info_to_string(err, test))
        super().addFailure(test, err)

    def addError(self, test, err) -> None:
        self._record(test, "error", self._exc_info_to_string(err, test))
        super().addError(test, err)

    def addSkip(self, test, reason) -> None:
        self._record(test, "skipped", reason)
        super().addSkip(test, reason)

    def addSubTest(self, test, subtest, err) -> None:
        if err is not None:
            self._record(subtest, "failed", self._exc_info_to_string(err, test))
        super().addSubTest(test, subtest, err)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", type=Path)
    parser.add_argument("names", nargs="*", help="Optional unittest test/class names")
    arguments = parser.parse_args()
    if arguments.names:
        suite = unittest.defaultTestLoader.loadTestsFromNames(arguments.names, sys.modules[__name__])
    else:
        suite = unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__])
    started = time.perf_counter()
    result = unittest.TextTestRunner(verbosity=2, resultclass=RecordingResult).run(suite)
    if arguments.json:
        report = {
            "suite": "C64 Unix ROM instruction-level behavioral regression",
            "cpu_core": "py65 1.2.0 NMOS 6502",
            "hardware_model": "6510 banking, keyboard matrix, CIA timer IRQ, screen/color RAM",
            "excluded": ["cycle-exact VIC/CIA", "IEC drive", "tape", "RS-232", "audio"],
            "success": result.wasSuccessful(),
            "tests_run": result.testsRun,
            "failures": len(result.failures),
            "errors": len(result.errors),
            "skipped": len(result.skipped),
            "seconds": round(time.perf_counter() - started, 3),
            "rom_sha256": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted((PROJECT / "roms").glob("*.bin"))
            },
            "results": result.records,
        }
        arguments.json.parent.mkdir(parents=True, exist_ok=True)
        arguments.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    raise SystemExit(0 if result.wasSuccessful() else 1)


if __name__ == "__main__":
    main()
