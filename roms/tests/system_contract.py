#!/usr/bin/env python3
"""Public KERNAL and interrupt contracts, executed by an independent 6502.

The small CIA partner supplies keyboard contacts and an acknowledged NMI
register. It is not a hardware-timing test. The actual ROM handles interrupt
frames, vectors, STOP, and BASIC recovery without intercepted subroutines.

Contract references: Commodore 64 Programmer's Reference Guide, chapter 5
(STOP, VECTOR, MEMTOP/MEMBOT, SCREEN/PLOT and system-vector memory map), and
Commodore 64 User's Guide, section 2.2 (RUN/STOP+RESTORE preserves a program).
https://www.commodore.ca/manuals/c64_programmers_reference/c64-programmers_reference_guide-05-basic_to_machine_language.pdf
https://vintagecomputer.com/commodore-64-users-guide.html
SPDX-License-Identifier: Apache-2.0
"""
from pathlib import Path
import argparse
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests/basic"))
import boot_contract as basic

OriginalMemory = basic.Memory


class MatrixMemory(OriginalMemory):
    def __init__(self, roms):
        super().__init__(roms)
        self.keys = set()
        self.cia2_status = 0
        self.cia2_reads = 0

    def __getitem__(self, address):
        if isinstance(address, int) and address == 0xDC01:
            rows = 255
            columns = self.ram[0xDC00]
            for column, row in self.keys:
                if not columns & (1 << column):
                    rows &= ~(1 << row)
            return rows
        if isinstance(address, int) and address == 0xDD0D:
            value, self.cia2_status = self.cia2_status, 0
            self.cia2_reads += 1
            return value
        return super().__getitem__(address)


basic.Memory = MatrixMemory


def call(s, entry, a=0, x=0x5A, y=0xA5, p=0x24):
    c = s.cpu
    s.m.ram[0xC800:0xC803] = bytes([0x20, entry & 255, entry >> 8])
    c.pc, c.sp, c.a, c.x, c.y, c.p = 0xC800, 255, a, x, y, p
    for _ in range(200000):
        c.step()
        if c.pc == 0xC803:
            assert c.sp == 255, "unbalanced public call stack"
            return
    raise AssertionError(f"public call ${entry:04X} did not return: ${c.pc:04X}")


def resume_until(s, pc, limit=200000):
    for _ in range(limit):
        if s.cpu.pc == pc:
            return
        s.cpu.step()
    raise AssertionError(f"interrupt did not resume ${pc:04X}: ${s.cpu.pc:04X}")


def pointer(s, a):
    return s.m[a] | s.m[a + 1] << 8


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rom-dir", type=Path, default=ROOT / "build/roms")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    cases = []

    def test(name, action):
        try:
            action()
            cases.append({"name": name, "passed": True})
        except Exception as e:
            cases.append({"name": name, "passed": False, "error": str(e)})

    def fresh():
        return basic.CpuBasicSession(args.rom_dir)

    def vectors():
        s = fresh()
        initial = bytes(s.m[0x314:0x334])
        call(s, 0xFF8D, x=0, y=0xC1, p=0x25)
        assert bytes(s.m[0xC100:0xC120]) == initial
        replacement = bytes((n * 19 + 7) & 255 for n in range(32))
        s.m.ram[0xC100:0xC120] = replacement
        call(s, 0xFF8D, x=0, y=0xC1, p=0x24)
        assert bytes(s.m[0x314:0x334]) == replacement
        call(s, 0xFF8A)
        assert bytes(s.m[0x314:0x334]) == initial

    def memory_bounds():
        s = fresh()
        for entry, address, value in ((0xFF9C, 0x281, 0x0900),
                                      (0xFF99, 0x283, 0x9000)):
            call(s, entry, x=value & 255, y=value >> 8, p=0x24)
            assert pointer(s, address) == value
            call(s, entry, x=1, y=2, p=0x25)
            assert s.cpu.x | s.cpu.y << 8 == value
            assert pointer(s, address) == value

    def screen_query():
        s = fresh()
        call(s, 0xFFED)
        assert (s.cpu.x, s.cpu.y) == (40, 25)
        call(s, 0xFFF0, x=7, y=23, p=0x24)
        call(s, 0xFFF0, x=0, y=0, p=0x25)
        assert (s.cpu.x, s.cpu.y) == (7, 23)
        call(s, 0xFFF3)
        assert (s.cpu.x, s.cpu.y) == (0, 0xDC)

    def names_and_devices():
        s = fresh()
        call(s, 0xFFBA, a=2, x=8, y=3)
        assert (s.m[0xB8], s.m[0xBA], s.m[0xB9]) == (2, 8, 3)
        call(s, 0xFFBD, a=4, x=0x34, y=0xC1)
        assert (s.m[0xB7], pointer(s, 0xBB)) == (4, 0xC134)

    def stop(pressed):
        s = fresh()
        s.m.keys = {(7, 7)} if pressed else set()
        for p in range(0x20, 0x100):
            if p & 0x10:  # B is a stacked marker, not a persistent CPU flag.
                continue
            s.m[0xDC00] = 0xA5
            s.m[0xC6], s.m[0x340] = 3, 2
            s.m[0x99], s.m[0x9A] = 2, 2
            call(s, 0xFFE1, p=p)
            assert s.cpu.y == 0xA5
            assert s.m[0xDC00] == 0xA5
            assert (s.cpu.p & 0xCD) == (p & 0xCD), (p, s.cpu.p)
            assert bool(s.cpu.p & 2) == pressed
            assert s.m[0x340] == 2, "STOP must leave logical files open"
            if pressed:
                assert s.cpu.a == 0 and s.m[0xC6] == 0
                assert (s.m[0x99], s.m[0x9A]) == (0, 3)
            else:
                assert s.cpu.a == 255 and s.m[0xC6] == 3
                assert (s.m[0x99], s.m[0x9A]) == (2, 2)

    def nmi_return(cia_source=False, custom=False):
        s = fresh()
        s.cpu.pc, s.cpu.sp = 0xC800, 0xE0
        s.cpu.a, s.cpu.x, s.cpu.y, s.cpu.p = 0x39, 0x5A, 0xA5, 0xE9
        s.m[0xDC00] = 0xA5
        if cia_source:
            s.m.keys = {(7, 7)}
            s.m.cia2_status = 0x81
        if custom:
            s.m[0x318], s.m[0x319] = 0, 0xC1
            s.m.ram[0xC100:0xC104] = bytes([0xEE, 0x80, 0xC1, 0x40])
        reads = s.m.cia2_reads
        s.cpu.nmi()
        resume_until(s, 0xC800)
        assert (s.cpu.a, s.cpu.x, s.cpu.y, s.cpu.sp) == (0x39, 0x5A, 0xA5, 0xE0)
        assert s.cpu.p & 0xCF == 0xC9
        assert s.m[0xDC00] == 0xA5
        if custom:
            assert s.m[0xC180] == 1 and s.m.cia2_reads == reads
        else:
            assert s.m.cia2_reads == reads + 1
            assert s.m.cia2_status == 0

    def interrupt_hook(brk):
        s = fresh()
        return_address = s.labels["PRIVATE_IRQ_RETURN"]
        s.m.ram[0xC100:0xC106] = bytes([0xEE, 0x80, 0xC1, 0x4C,
                                       return_address & 255, return_address >> 8])
        vector = 0x316 if brk else 0x314
        s.m[vector], s.m[vector + 1] = 0, 0xC1
        s.cpu.pc, s.cpu.sp = 0xC800, 0xE0
        s.cpu.a, s.cpu.x, s.cpu.y, s.cpu.p = 0x39, 0x5A, 0xA5, 0xE9
        if brk:
            s.m[0xC800] = 0
            s.cpu.step()
        else:
            s.cpu.irq()
        resume_until(s, 0xC802 if brk else 0xC800)
        assert s.m[0xC180] == 1
        assert (s.cpu.a, s.cpu.x, s.cpu.y, s.cpu.sp) == (0x39, 0x5A, 0xA5, 0xE0)
        assert s.cpu.p & 0xCF == 0xC9

    def warm_recovery(brk):
        s = fresh()
        s.command('10 PRINT "PRESERVED"')
        s.command("A=123")
        start, end = pointer(s, 0x2B), pointer(s, 0x2D)
        program = bytes(s.m[start:end])
        s.m[0xD018], s.m[0xD020], s.m[0xD021] = 0x1F, 5, 2
        s.m[0xC6], s.m[0x33C], s.m[0xD4] = 3, 1, 1
        s.cpu.pc = 0xC800
        if brk:
            s.m[0xC800] = 0
            s.cpu.step()
        else:
            s.m.keys = {(7, 7)}
            s.cpu.nmi()
        output = s._run(None, 1_000_000)
        s.m.keys.clear()
        assert "READY." in output and "FREE BASIC" not in output
        assert bytes(s.m[start:end]) == program
        assert (s.m[0xD018], s.m[0xD020], s.m[0xD021]) == (0x15, 14, 6)
        assert s.m[0xC6] == 0 and s.m[0xD4] == 0
        assert basic.normalize(s.command("PRINT A")) == "123"
        assert basic.normalize(s.command("RUN")) == "PRESERVED"

    def cartridge_cold(corrupt_byte=None):
        m = MatrixMemory(args.rom_dir)
        m.ram[0x8000:0x8009] = bytes([0, 0xC1, 0x20, 0xC1, 0xC3, 0xC2, 0xCD, 0x38, 0x30])
        if corrupt_byte is not None:
            m.ram[0x8004 + corrupt_byte] ^= 1
        c = basic.MPU(memory=m, pc=None)
        labels = {name: int(addr, 16) for addr, name in
                  (line.split() for line in (args.rom_dir / "open-kernal64.map").read_text().splitlines())}
        expected = 0xC100 if corrupt_byte is None else labels["LINE_WAIT"]
        for _ in range(1_000_000):
            if c.pc == expected:
                if corrupt_byte is None:
                    assert c.sp == 255 and not c.p & 8
                    assert m[0x2B] == 0, "cartridge entry must precede BASIC initialization"
                return
            assert c.pc != (labels["LINE_WAIT"] if corrupt_byte is None else 0xC100)
            c.step()
        raise AssertionError(f"cartridge reset reached ${c.pc:04X}, expected ${expected:04X}")

    def cartridge_warm():
        s = fresh()
        s.m.ram[0x8000:0x8009] = bytes([0, 0xC1, 0x20, 0xC1, 0xC3, 0xC2, 0xCD, 0x38, 0x30])
        s.cpu.pc = 0xC800
        s.m.keys = {(7, 7)}
        s.cpu.nmi()
        resume_until(s, 0xC120)
        assert s.cpu.sp == 255 and not s.cpu.p & 8

    test("VECTOR read/write and RESTOR", vectors)
    test("MEMBOT and MEMTOP read/set", memory_bounds)
    test("SCREEN/PLOT/IOBASE", screen_query)
    test("SETLFS/SETNAM", names_and_devices)
    test("STOP unpressed register/flag contract", lambda: stop(False))
    test("STOP pressed flags/queue/channel contract", lambda: stop(True))
    test("RESTORE alone resumes original CPU state", nmi_return)
    test("CIA2 NMI acknowledges without warm restart", lambda: nmi_return(True))
    test("custom NMI vector receives raw hardware frame", lambda: nmi_return(custom=True))
    test("custom IRQ vector and return frame", lambda: interrupt_hook(False))
    test("custom BRK vector and return frame", lambda: interrupt_hook(True))
    test("RUN/STOP+RESTORE preserves program and variables", lambda: warm_recovery(False))
    test("default BRK returns to BASIC without cold reset", lambda: warm_recovery(True))
    test("CBM80 cartridge cold-start vector", cartridge_cold)
    for i in range(5):
        test(f"reject malformed cartridge signature byte {i}", lambda i=i: cartridge_cold(i))
    test("CBM80 cartridge warm-start vector", cartridge_warm)
    report = {
        "suite": "Public KERNAL and interrupt recovery contracts",
        "scope": "Independent 6502 CPU with explicit matrix/CIA status partner; no hardware timing claim",
        "rom_sha256": {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                       for p in sorted(args.rom_dir.glob("*.bin"))},
        "passed": sum(t["passed"] for t in cases), "total": len(cases), "results": cases,
    }
    text = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text)
    print(text)
    return 0 if all(t["passed"] for t in cases) else 1


if __name__ == "__main__":
    sys.exit(main())
