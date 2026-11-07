"""RS-232 regression with 6502 CPU cycles, CIA timer and ideal wire frames.

This checks bit values/timing against a PAL-clock ideal serial partner. It
does not model electrical interfaces or asynchronous interrupt buffering.
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


class Memory(list):
    def __init__(self):
        super().__init__([0] * 65536)
        self[0xE000:] = ROM
        self.cpu = None
        self.deadline = None
        self.rx_start = None
        self.rx_bits = []
        self.period = 0
        self.tx = []
        self.tx_level = 1

    def __getitem__(self, address):
        now = self.cpu.processorCycles if self.cpu else 0
        if address == 0xDD0D:
            fired = self.deadline is not None and now >= self.deadline
            if fired:
                self.deadline = None
            return int(fired)
        if address == 0xDD01:
            if self.rx_start is None or now < self.rx_start:
                return 0xFF
            index = int((now - self.rx_start) // self.period)
            bit = self.rx_bits[index] if index < len(self.rx_bits) else 1
            return 0xFE | bit
        if address == 0xDC01:
            return 0xFF
        return super().__getitem__(address)

    def __setitem__(self, address, value):
        super().__setitem__(address, value)
        if isinstance(address, slice) or self.cpu is None:
            return
        now = self.cpu.processorCycles
        if address == 0xDD0E and value & 1:
            self.deadline = now + super().__getitem__(0xDD04) + 256 * super().__getitem__(0xDD05) + 1
        elif address == 0xDD00:
            level = (value >> 2) & 1
            if level != self.tx_level:
                self.tx.append((now, level))
            self.tx_level = level


def call(c, entry, a=0, x=0x5A, y=0xA5):
    c.memory[0x1000:0x1003] = [0x20, entry & 255, entry >> 8]
    c.pc, c.sp, c.a, c.x, c.y, c.p = 0x1000, 0xFF, a, x, y, 0x24
    for _ in range(2000000):
        c.step()
        if c.pc == 0x1003:
            assert c.sp == 255
            return
    raise AssertionError(f"serial call hung at {c.pc:04X}")


def initialize(rate, width, parity, stops):
    m = Memory()
    c = MPU(memory=m)
    m.cpu = c
    for entry in (0xFF87, 0xFF8A, 0xFF84):
        call(c, entry)
    control = rate | ((8 - width) << 5) | (0x80 if stops == 2 else 0)
    command = {"N": 0, "E": 0x60, "O": 0x20, "M": 0xA0, "S": 0xE0}[parity]
    m[0x2000:0x2002] = [control, command]
    call(c, 0xFFBA, a=1, x=2, y=0)
    call(c, 0xFFBD, a=2, x=0, y=0x20)
    call(c, 0xFFC0)
    assert not c.p & 1, (rate, width, parity, stops, c.a)
    m.period = m[0x299] + 256 * m[0x29A]
    m.tx = []
    return c


def frame(value, width, parity, stops):
    data = [(value >> i) & 1 for i in range(width)]
    parity_bit = {"N": [], "E": [sum(data) & 1], "O": [(sum(data) & 1) ^ 1],
                  "M": [1], "S": [0]}[parity]
    return [0] + data + parity_bit + [1] * stops


results = []
for rate in range(1, 11):
    for width, parity, stops in ((8, "N", 1), (7, "E", 1), (5, "O", 2)):
        for value in (0, 0xA5, 0xFF):
            expected = frame(value, width, parity, stops)
            c = initialize(rate, width, parity, stops)
            call(c, 0xFFC9, x=1)
            call(c, 0xFFD2, a=value)
            assert not c.p & 1
            assert (c.x, c.y) == (0x5A, 0xA5)
            edges = c.memory.tx
            assert edges and edges[0][1] == 0
            start = edges[0][0]
            decoded = []
            for index in range(len(expected)):
                sample = start + (index + 0.5) * c.memory.period
                level = 1
                for time, bit in edges:
                    if time <= sample:
                        level = bit
                    else:
                        break
                decoded.append(level)
            tx_ok = decoded == expected
            c = initialize(rate, width, parity, stops)
            call(c, 0xFFC6, x=1)
            c.memory.rx_start = c.processorCycles + 100
            c.memory.rx_bits = expected
            call(c, 0xFFCF)
            rx_ok = c.a == value & ((1 << width) - 1) and not c.p & 1
            results.append({"rate_code": rate, "format": f"{width}{parity}{stops}",
                            "value": value, "tx": tx_ok, "rx": rx_ok,
                            "actual_rx": c.a, "actual_tx": decoded if not tx_ok else None})

print(json.dumps({"rom": str(ROM_PATH), "cases": results,
                  "all_pass": all(r["tx"] and r["rx"] for r in results)}, indent=2))
if not all(r["tx"] and r["rx"] for r in results):
    raise SystemExit(1)
