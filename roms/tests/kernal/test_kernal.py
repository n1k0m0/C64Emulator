"""Targeted KERNAL ROM regressions, independent 6502 execution with py65.

Usage: python tests/kernal/test_kernal.py [path/to/open-kernal64.bin]
No full C64 timing/IEC/VIC emulation is claimed. Keyboard inputs use an
explicit active-low matrix; all executed KERNAL instructions come from ROM.
SPDX-License-Identifier: Apache-2.0
"""
from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT.parent / "rom_review/vendor"))
from py65.devices.mpu6502 import MPU

ROM_PATH = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "build/roms/open-kernal64.bin"
ROM = ROM_PATH.read_bytes()
SYMBOLS = {name: int(addr, 16) for addr, name in
           (line.split() for line in ROM_PATH.with_suffix(".map").read_text().splitlines())}


class Memory(list):
    def __init__(self):
        super().__init__([0] * 65536)
        self.keys = set()
        self[0xE000:] = ROM

    def __getitem__(self, address):
        if address == 0xDC01:
            selected = super().__getitem__(0xDC00)
            rows = 0xFF
            for column, row in self.keys:
                if not (selected & (1 << column)):
                    rows &= ~(1 << row)
            return rows
        return super().__getitem__(address)


def start(cpu, address, a=0, x=0x5A, y=0xA5, p=0x24):
    cpu.memory[0x1000:0x1003] = [0x20, address & 255, address >> 8]
    cpu.pc, cpu.sp, cpu.a, cpu.x, cpu.y, cpu.p = 0x1000, 0xFF, a, x, y, p


def finish(cpu, limit=200000):
    for _ in range(limit):
        cpu.step()
        if cpu.pc == 0x1003:
            assert cpu.sp == 0xFF, f"unbalanced stack: {cpu.sp:02x}"
            return
    raise AssertionError(f"call did not return: PC=${cpu.pc:04X}")


def call(cpu, address, **regs):
    start(cpu, address, **regs)
    finish(cpu)


def fresh():
    c = MPU(memory=Memory())
    for entry in (0xFF87, 0xFF8A, 0xFF81):
        call(c, entry)
    c.memory[0xC5] = 0x40
    return c


def clock(cpu):
    return int.from_bytes(cpu.memory[0xA0:0xA3], "big")


def set_clock(cpu, value):
    cpu.memory[0xA0:0xA3] = list(value.to_bytes(3, "big"))


results = []


def test(name, action):
    action()
    results.append({"test": name, "result": "PASS"})


def output_contract():
    for char, row, col in ((0x41, 0, 0), (0x93, 0, 0), (13, 24, 0),
                           (0x41, 24, 39), (0x11, 24, 7), (0x14, 2, 5)):
        for decimal in (False, True):
            c = fresh()
            c.memory[0xD6], c.memory[0xD3] = row, col
            call(c, 0xFFD2, a=char, p=0x24 | (8 if decimal else 0))
            assert (c.x, c.y) == (0x5A, 0xA5), (char, row, col, c.x, c.y)
            assert c.p & 8 == (8 if decimal else 0)
            assert not c.p & 1


def scrolling():
    c = fresh()
    before = [(i * 37 + i // 40) & 255 for i in range(1000)]
    colors = [(i * 3 + i // 40) & 15 for i in range(1000)]
    c.memory[0x400:0x7E8] = before
    c.memory[0xD800:0xDBE8] = colors
    c.memory[0xD6] = 24
    call(c, 0xFFD2, a=13)
    assert c.memory[0x400:0x7E8] == before[40:] + [0x20] * 40
    assert c.memory[0xD800:0xDBE8] == colors[40:] + [14] * 40


def controls():
    c = fresh()
    for color, code in enumerate((0x90, 5, 0x1C, 0x9F, 0x9C, 0x1E, 0x1F,
                                  0x9E, 0x81, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B)):
        call(c, 0xFFD2, a=code)
        assert c.memory[0x286] == color, (code, c.memory[0x286])
        assert c.memory[0xD3] == 0
    c.memory[0xD018] = 0x15
    call(c, 0xFFD2, a=14)
    assert c.memory[0xD018] == 0x17
    call(c, 0xFFD2, a=0x8E)
    assert c.memory[0xD018] == 0x15
    c.memory[0xD018], c.memory[0xD01A], c.memory[0xD020] = 0x17, 1, 5
    call(c, 0xFFD2, a=0x93)
    assert (c.memory[0xD018], c.memory[0xD01A], c.memory[0xD020]) == (0x17, 1, 5)
    assert c.memory[0x400:0x7E8] == [32] * 1000
    for code in (0x80, 0x82, 0x84, 0x8F):
        call(c, 0xFFD2, a=code)
        assert c.memory[0xD3] == 0


def petscii_conversion():
    for code in list(range(32, 128)) + list(range(160, 256)):
        if code < 64:
            expected = code
        elif code < 96:
            expected = code - 64
        elif code < 128:
            expected = code - 32
        elif code < 192:
            expected = code - 64
        elif code == 255:
            expected = 94
        else:
            expected = code - 128
        for p in (0x24, 0x2C):
            c = fresh()
            call(c, 0xFFD2, a=code, p=p)
            assert c.memory[0x400] == expected, (code, p, c.memory[0x400], expected)


def time_operations():
    for p in (0x20, 0x24, 0x28, 0x2C):
        c = fresh()
        call(c, 0xFFDB, a=0x67, x=0x45, y=0x23, p=p)
        assert clock(c) == 0x234567
        assert c.p & 12 == p & 12
        call(c, 0xFFDE, p=p)
        assert (c.y, c.x, c.a) == (0x23, 0x45, 0x67)
        assert c.p & 12 == p & 12
    for before, after in ((0, 1), (0xFF, 0x100), (0xFFFF, 0x10000),
                          (0x4F19FF, 0x4F1A00), (0x4F1A00, 0),
                          (0x4F1A01, 0), (0xFFFFFF, 0)):
        for p in (0x24, 0x2C):
            c = fresh()
            set_clock(c, before)
            call(c, 0xFFEA, p=p)
            assert clock(c) == after, (before, clock(c), after)
            assert c.p & 8 == p & 8


def atomic_clock():
    for entry, symbol in ((0xFFDE, "READ_TIME"), (0xFFDB, "SET_TIME")):
        c = fresh()
        set_clock(c, 0x0012FF)
        c.memory[0xDC0D] = 1
        start(c, entry, a=0xFF, x=0x34, y=0, p=0x20)
        # PHP, SEI, LDA/STA $A2 total four bytes. At that point the attempted
        # IRQ must remain masked. Deliver it as soon as PLP unmasks it.
        for _ in range(100):
            c.step()
            if c.pc == SYMBOLS[symbol] + 4:
                break
        else:
            raise AssertionError("did not reach low-byte access")
        oldpc = c.pc
        c.irq()
        assert c.pc == oldpc and c.p & 4, "IRQ was accepted mid-operation"
        for _ in range(100):
            c.step()
            if not c.p & 4:
                c.irq()
                break
        else:
            raise AssertionError("interrupt mask was not restored")
        finish(c)
        if entry == 0xFFDE:
            assert (c.y << 16) | (c.x << 8) | c.a == 0x0012FF
            assert clock(c) == 0x001300
        else:
            assert clock(c) == 0x003500


def line_input_contract():
    c = fresh()
    c.memory[0x277:0x27A] = [0x41, 0x42, 13]
    c.memory[0xC6] = 3
    for index, char in enumerate((0x41, 0x42, 13)):
        call(c, 0xFFCF)
        assert (c.a, c.x) == (char, index), (index, c.a, c.x)
    assert c.memory[0x33C] == 0


def host_queue_refill():
    # Match the emulator's relevant contract: a producer may fill a free
    # slot immediately after any CPU instruction that decreases $C6.
    # Publishing the count before shifting unread bytes loses byte ten.
    c = fresh()
    text = list(b"POKE 49152,173\rPRINT PEEK(49152)\rABCDEFGHIJKLMNOPQRSTUVWXYZ\r")
    pending = iter(text)
    producer_done = False
    actual = []
    while len(actual) < len(text):
        start(c, 0xFFE4, p=0x28)
        for _ in range(10000):
            while c.memory[0xC6] < 10 and not producer_done:
                try:
                    char = next(pending)
                except StopIteration:
                    producer_done = True
                    break
                index = c.memory[0xC6]
                c.memory[0x277 + index] = char
                c.memory[0xC6] = index + 1
            c.step()
            if c.pc == 0x1003:
                assert c.sp == 255
                assert c.p & 12 == 8
                assert bool(c.p & 2) == (c.a == 0)
                if c.a:
                    actual.append(c.a)
                break
        else:
            raise AssertionError("GETIN did not return")
    assert actual == text, (bytes(text), bytes(actual))


if __name__ == "__main__":
    for name, action in (("CHROUT X/Y, stack, carry and decimal mode", output_contract),
                         ("scroll copies all 960 characters and colors safely", scrolling),
                         ("all sixteen colors, charset and non-destructive clear", controls),
                         ("192 printable PETSCII codes in both decimal modes", petscii_conversion),
                         ("atomic clock access and C64 daily rollover", time_operations),
                         ("deferred IRQ cannot tear SETTIM or RDTIM", atomic_clock),
                         ("CHRIN keeps Open BASIC's returned line offset", line_input_contract),
                         ("GETIN preserves bytes under immediate host queue refill", host_queue_refill)):
        test(name, action)
    print(json.dumps({"rom": str(ROM_PATH), "checks": results}, indent=2))
