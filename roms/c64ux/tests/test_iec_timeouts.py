"""Execute the delivered IEC wait routines with ready and permanently held pins.

SPDX-License-Identifier: Apache-2.0

Run from the package root:
    python tests/test_iec_timeouts.py --json docs/iec-timeouts.json

These six cases execute actual KERNAL instructions using the independent
py65 NMOS 6502 core. The fixture holds the CIA2 IEC input levels constant;
it does not emulate a drive or claim end-to-end disk protocol validation.
No firmware routine is intercepted. CPU cycle counts include the RAM JSR
wrapper and the routine's RTS; they are not host execution times.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import unittest

from machine import Memory, MPU, PROJECT, read_symbols


class IecTimeoutTests(unittest.TestCase):
    records: list[dict] = []

    @classmethod
    def setUpClass(cls) -> None:
        cls.records = []
        roms = PROJECT / "roms"
        cls.basic = (roms / "open-basic64.bin").read_bytes()
        cls.kernal = (roms / "open-kernal64.bin").read_bytes()
        cls.character = (roms / "open-character-rom64.bin").read_bytes()
        cls.symbols = read_symbols(roms / "open-kernal64.map")

    def check_wait(self, symbol: str, port: int, timeout: bool) -> None:
        memory = Memory(self.basic, self.kernal, self.character)
        # CIA2 bits 4/5 drive inverted CLOCK/DATA outputs. DDRA=$3F keeps
        # input bits 6/7 as inputs. $07 releases both wires; $17/$27 hold
        # CLOCK/DATA low respectively, throughout the entire subroutine.
        memory[0xDD02] = 0x3F
        memory[0xDD00] = port
        target = self.symbols[symbol]
        memory.ram[0x0200:0x0204] = bytes(
            (0x20, target & 0xFF, target >> 8, 0xEA)
        )
        cpu = MPU(memory=memory, pc=0x0200)
        cpu.p = cpu.UNUSED | cpu.INTERRUPT
        cpu.x, cpu.y = 0x42, 0x76
        initial_sp = cpu.sp
        cycle_limit = 6_000_000
        while cpu.pc != 0x0203 and cpu.processorCycles < cycle_limit:
            cpu.step()

        record = {
            "test": self._testMethodName,
            "routine": symbol,
            "address": f"${target:04X}",
            "cia2_output_latch": f"${port:02X}",
            "expected_timeout": timeout,
            "returned": cpu.pc == 0x0203,
            "cpu_cycles": cpu.processorCycles,
            "carry": bool(cpu.p & cpu.CARRY),
            "readst": memory.ram[0x90],
            "initial_sp": initial_sp,
            "returned_sp": cpu.sp,
            "returned_x": cpu.x,
            "returned_y": cpu.y,
            "success": False,
        }
        self.records.append(record)
        self.assertEqual(cpu.pc, 0x0203, "IEC wait did not return before cycle limit")
        self.assertEqual(cpu.sp, initial_sp, "IEC wait unbalanced the CPU stack")
        self.assertEqual(bool(cpu.p & cpu.CARRY), timeout)
        self.assertEqual(memory.ram[0x90], 0x80 if timeout else 0x00)
        if timeout:
            # The extended grace period must not collapse to an immediate
            # error, and must never become an infinite busy-device wait.
            self.assertGreater(cpu.processorCycles, 3_000_000)
            self.assertLess(cpu.processorCycles, 5_000_000)
        else:
            self.assertLess(cpu.processorCycles, 100, "Ready path became slow")
        if symbol != "IEC_WAIT_CLOCK_HIGH":
            self.assertEqual((cpu.x, cpu.y), (0x42, 0x76))
        record["success"] = True

    def test_stuck_data_high_wait(self) -> None:
        self.check_wait("IEC_WAIT_DATA_HIGH", 0x27, True)

    def test_stuck_clock_high_wait(self) -> None:
        self.check_wait("IEC_WAIT_CLOCK_HIGH", 0x17, True)

    def test_missing_eoi_low_reply(self) -> None:
        self.check_wait("IEC_WAIT_EOI_LOW_BOUNDED", 0x07, True)

    def test_ready_data_high_fast_path(self) -> None:
        self.check_wait("IEC_WAIT_DATA_HIGH", 0x07, False)

    def test_ready_clock_high_fast_path(self) -> None:
        self.check_wait("IEC_WAIT_CLOCK_HIGH", 0x07, False)

    def test_ready_eoi_low_reply(self) -> None:
        self.check_wait("IEC_WAIT_EOI_LOW_BOUNDED", 0x27, False)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", type=Path, help="Write the actual measured results")
    arguments = parser.parse_args()
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(IecTimeoutTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if arguments.json:
        report = {
            "scope": "Compiled KERNAL IEC wait routines with constant CIA2 wire levels",
            "cpu": "py65 independent NMOS 6502 instruction core",
            "excludes": ["1541 drive emulation", "end-to-end IEC transfers", "cycle-exact CIA edges"],
            "command": "python tests/test_iec_timeouts.py --json docs/iec-timeouts.json",
            "cycle_count_includes": "RAM JSR instruction and KERNAL RTS",
            "success": result.wasSuccessful(),
            "tests_run": result.testsRun,
            "failures": len(result.failures),
            "errors": len(result.errors),
            "kernal_sha256": hashlib.sha256(IecTimeoutTests.kernal).hexdigest(),
            "results": IecTimeoutTests.records,
        }
        arguments.json.parent.mkdir(parents=True, exist_ok=True)
        arguments.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    raise SystemExit(0 if result.wasSuccessful() else 1)


if __name__ == "__main__":
    main()
