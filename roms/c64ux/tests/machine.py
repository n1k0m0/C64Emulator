"""Instruction-level C64 ROM test machine (not a complete C64 emulator).

SPDX-License-Identifier: Apache-2.0

The real ROM instructions run on py65's independent NMOS 6502 core.  The
memory model supplies the 6510 banking port, screen/color RAM, the keyboard
matrix, and cycle-driven CIA timers/IRQ acknowledgement.  It deliberately
does not implement VIC raster timing, IEC devices, tape pulses, sound, or
cycle-exact CIA edge behavior.  This harness is for firmware regression
tests, not a replacement for the user's emulator or hardware testing.

No KERNAL or shell call is intercepted/replaced.  CHROUT is only observed
at its ROM entry, and injected keys use the documented KERNAL input queue.
"""

from __future__ import annotations

from collections import deque
from pathlib import Path
import random
import re
from typing import Callable, Iterable

try:
    from py65.devices.mpu6502 import MPU
except ImportError as exc:
    raise SystemExit(
        "ROM tests need py65: python -m pip install -r tests/requirements.txt"
    ) from exc


PROJECT = Path(__file__).resolve().parents[1]

# Index = CIA 1 port-A output column * 8 + port-B input row.
KEY_ROWS = (
    ("DELETE", "RETURN", "RIGHT", "F7", "F1", "F3", "F5", "DOWN"),
    ("3", "W", "A", "4", "Z", "S", "E", "LSHIFT"),
    ("5", "R", "D", "6", "C", "F", "T", "X"),
    ("7", "Y", "G", "8", "B", "H", "U", "V"),
    ("9", "I", "J", "0", "M", "K", "O", "N"),
    ("+", "P", "L", "-", ".", ":", "@", ","),
    ("POUND", "*", ";", "HOME", "RSHIFT", "=", "UPARROW", "/"),
    ("1", "LEFTARROW", "CTRL", "2", "SPACE", "COMMODORE", "Q", "STOP"),
)
KEYS = {
    name: (column, row)
    for column, names in enumerate(KEY_ROWS)
    for row, name in enumerate(names)
}


def read_symbols(path: Path) -> dict[str, int]:
    result: dict[str, int] = {}
    if path.is_file():
        for line in path.read_text(encoding="utf-8").splitlines():
            match = re.fullmatch(r"\s*([0-9a-fA-F]{4,6})\s+(\w+)\s*", line)
            if match:
                result[match[2].upper()] = int(match[1], 16)
    return result


class CIA:
    """The subset of MOS 6526 registers used by ROM input/clock code."""

    def __init__(self, keyboard: bool = False) -> None:
        self.regs = bytearray(16)
        self.regs[0] = self.regs[1] = 0xFF
        self.keyboard = keyboard
        self.keys: set[tuple[int, int]] = set()
        self.latch = [0xFFFF, 0xFFFF]
        self.counter = [0xFFFF, 0xFFFF]
        self.pending = 0
        self.mask = 0

    @property
    def irq(self) -> bool:
        return bool(self.pending & self.mask & 0x1F)

    def keyboard_pins(self) -> tuple[int, int]:
        # Closed contacts join A/B pins.  Propagate driven lows through
        # the contacts; this also allows ordinary matrix ghosting.
        low = {
            bit + port * 8
            for port in range(2)
            for bit in range(8)
            if self.regs[port + 2] & (1 << bit)
            and not self.regs[port] & (1 << bit)
        }
        previous_size = -1
        while previous_size != len(low):
            previous_size = len(low)
            for column, row in self.keys:
                if column in low or row + 8 in low:
                    low.add(column)
                    low.add(row + 8)
        pins = [0xFF, 0xFF]
        for pin in low:
            pins[pin // 8] &= ~(1 << (pin % 8))
        return pins[0], pins[1]

    def read(self, reg: int) -> int:
        reg &= 0x0F
        if reg < 2:
            if self.keyboard:
                external = self.keyboard_pins()[reg]
            elif reg == 0:
                # No external IEC drive: released CLOCK/DATA are high.
                # CIA2 output bits 4/5 have external inverting drivers.
                external = 0xFF
                if self.regs[0] & self.regs[2] & 0x10:
                    external &= ~0x40
                if self.regs[0] & self.regs[2] & 0x20:
                    external &= ~0x80
            else:
                external = 0xFF
            direction = self.regs[reg + 2]
            return (self.regs[reg] & direction) | (external & ~direction & 0xFF)
        if reg in (4, 5, 6, 7):
            timer = (reg - 4) // 2
            shift = 8 if reg & 1 else 0
            return (self.counter[timer] >> shift) & 0xFF
        if reg == 13:
            value = self.pending | (0x80 if self.irq else 0)
            self.pending = 0
            return value
        return self.regs[reg]

    def write(self, reg: int, value: int) -> None:
        reg &= 0x0F
        value &= 0xFF
        if reg == 13:
            if value & 0x80:
                self.mask |= value & 0x1F
            else:
                self.mask &= ~(value & 0x1F)
            return
        if reg in (4, 5, 6, 7):
            timer = (reg - 4) // 2
            if reg & 1:
                self.latch[timer] = (self.latch[timer] & 0x00FF) | (value << 8)
                if not self.regs[14 + timer] & 1:
                    self.counter[timer] = self.latch[timer]
            else:
                self.latch[timer] = (self.latch[timer] & 0xFF00) | value
        elif reg in (14, 15):
            if value & 0x10:
                self.counter[reg - 14] = self.latch[reg - 14]
            value &= ~0x10  # force-load bit is a strobe
        self.regs[reg] = value

    def tick(self, cycles: int) -> None:
        for timer in range(2):
            control = self.regs[14 + timer]
            if not control & 1:
                continue
            # Only the processor-clock input mode is needed here.
            if control & (0x20 if timer == 0 else 0x60):
                continue
            self.counter[timer] -= cycles
            while self.counter[timer] < 0:
                self.pending |= 1 << timer
                self.counter[timer] += self.latch[timer] + 1
                if control & 8:
                    self.regs[14 + timer] &= ~1
                    self.counter[timer] = self.latch[timer]
                    break


class Memory:
    def __init__(self, basic: bytes, kernal: bytes, char: bytes, seed: int | None = None):
        if len(basic) != 8192 or len(kernal) != 8192 or len(char) != 4096:
            raise ValueError("Expected BASIC=8192, KERNAL=8192, character ROM=4096 bytes")
        self.basic, self.kernal, self.char = basic, kernal, char
        self.ram = bytearray(65536)
        if seed is not None:
            rng = random.Random(seed)
            self.ram[:] = rng.randbytes(65536)
        self.io = bytearray(4096)
        self.cia1, self.cia2 = CIA(keyboard=True), CIA()
        self.ram[0], self.ram[1] = 0x2F, 0x37

    def __len__(self) -> int:
        return 65536

    @property
    def port(self) -> int:
        # All undriven 6510 port lines are pulled high in this fixture.
        return (self.ram[1] & self.ram[0]) | (~self.ram[0] & 0xFF)

    @property
    def basic_visible(self) -> bool:
        return self.port & 3 == 3

    @property
    def kernal_visible(self) -> bool:
        return bool(self.port & 2)

    @property
    def io_visible(self) -> bool:
        return bool(self.port & 3) and bool(self.port & 4)

    def __getitem__(self, address: int | slice) -> int | list[int]:
        if isinstance(address, slice):
            return [self[i] for i in range(*address.indices(65536))]
        address &= 0xFFFF
        if address == 1:
            return self.port
        if 0xA000 <= address < 0xC000 and self.basic_visible:
            return self.basic[address - 0xA000]
        if address >= 0xE000 and self.kernal_visible:
            return self.kernal[address - 0xE000]
        if 0xD000 <= address < 0xE000 and self.port & 3:
            if not self.port & 4:
                return self.char[address - 0xD000]
            if 0xDC00 <= address < 0xDD00:
                return self.cia1.read(address & 0x0F)
            if 0xDD00 <= address < 0xDE00:
                return self.cia2.read(address & 0x0F)
            return self.io[address - 0xD000]
        return self.ram[address]

    def __setitem__(self, address: int, value: int) -> None:
        address &= 0xFFFF
        value &= 0xFF
        if 0xD000 <= address < 0xE000 and self.io_visible:
            if 0xDC00 <= address < 0xDD00:
                self.cia1.write(address & 0x0F, value)
            elif 0xDD00 <= address < 0xDE00:
                self.cia2.write(address & 0x0F, value)
            else:
                self.io[address - 0xD000] = value
        else:
            # Stores to ROM windows reach the underlying RAM.
            self.ram[address] = value

    def tick(self, cycles: int) -> None:
        self.cia1.tick(cycles)
        self.cia2.tick(cycles)


def petscii_text(values: Iterable[int], lower: bool = False) -> str:
    """Readable transcript; control characters retain their raw meanings."""
    out: list[str] = []
    for value in values:
        if value == 13:
            out.append("\n")
        elif value in (10, 9):
            out.append(chr(value))
        elif value == 0x14:
            out.append("\b")
        elif 0x41 <= value <= 0x5A and lower:
            out.append(chr(value + 32))
        elif 0x20 <= value <= 0x7E:
            out.append(chr(value))
        elif 0xC1 <= value <= 0xDA:
            out.append(chr(value - 0x80))
        # Color changes, reverse video, clear/home etc. do not print text.
    return "".join(out)


class Machine:
    """Run ROM code and observe the screen or KERNAL output stream."""

    def __init__(self, rom_dir: Path | str | None = None, seed: int | None = None):
        self.rom_dir = Path(rom_dir) if rom_dir is not None else PROJECT / "roms"
        self.symbols: dict[str, int] = {}
        for path in sorted(self.rom_dir.glob("*.map")):
            self.symbols.update(read_symbols(path))
        self.memory = Memory(
            (self.rom_dir / "open-basic64.bin").read_bytes(),
            (self.rom_dir / "open-kernal64.bin").read_bytes(),
            (self.rom_dir / "open-character-rom64.bin").read_bytes(),
            seed,
        )
        self.cpu = MPU(memory=self.memory, pc=None)
        self.cpu.p |= self.cpu.INTERRUPT
        self.raw_output: list[int] = []
        self._decoded_output: list[str] = []
        self._input: deque[int] = deque()
        self._recent: deque[tuple[int, int, int, int, int, int]] = deque(maxlen=40)
        self.instructions = 0
        self.interrupts = 0
        self._chrout = self.symbols.get("CHARACTER_OUT", self._jump_target(0xFFD2))
        self._keyread = {
            self.symbols[name]
            for name in ("GET_INPUT", "KEYBOARD_GET")
            if name in self.symbols
        }
        self._keyread.add(0xFFE4)
        self._idle = {
            self.symbols[name]
            for name in (("SHELL_WAIT",) if "SHELL_WAIT" in self.symbols else
                         ("READLINE_WAIT", "INPUT_WAIT", "LINE_WAIT"))
            if name in self.symbols
        }

    @property
    def ram(self) -> bytearray:
        return self.memory.ram

    @property
    def cycles(self) -> int:
        return self.cpu.processorCycles

    @property
    def output(self) -> str:
        return "".join(self._decoded_output)

    @property
    def jiffies(self) -> int:
        return self.ram[0xA2] | (self.ram[0xA1] << 8) | (self.ram[0xA0] << 16)

    def _jump_target(self, address: int) -> int:
        # Public CHROUT normally JMP ($0326); the map is preferred.
        if self.memory[address] == 0x4C:
            return self.memory[address + 1] | (self.memory[address + 2] << 8)
        return address

    def step(self) -> None:
        if self.memory.cia1.irq and not self.cpu.p & self.cpu.INTERRUPT:
            previous = self.cycles
            self.cpu.irq()
            self.interrupts += 1
            self.memory.tick(self.cycles - previous)
        pc = self.cpu.pc
        # Inject at a safe routine boundary, before GETIN reads the queue.
        if self._input and pc in self._keyread:
            capacity = min(10, self.ram[0x0289])
            while self._input and self.ram[0xC6] < capacity:
                position = self.ram[0xC6]
                self.ram[0x0277 + position] = self._input.popleft()
                self.ram[0xC6] += 1
        if pc == self._chrout and self.memory.kernal_visible:
            if self.ram[0x9A] in (0, 3):
                self.raw_output.append(self.cpu.a)
                self._decoded_output.append(petscii_text(
                    (self.cpu.a,), lower=bool(self.memory.io[0x18] & 2)))
        opcode = self.memory[pc]
        self._recent.append((pc, opcode, self.cpu.a, self.cpu.x, self.cpu.y, self.cpu.sp))
        if MPU.disassemble[opcode][0] == "???":
            raise AssertionError(self.diagnostic(f"Unsupported opcode ${opcode:02X} at ${pc:04X}"))
        previous = self.cycles
        self.cpu.step()
        self.memory.tick(self.cycles - previous)
        self.instructions += 1

    def run_until(self, predicate: Callable[["Machine"], bool], max_cycles: int = 3_000_000) -> None:
        deadline = self.cycles + max_cycles
        while self.cycles < deadline:
            self.step()
            if predicate(self):
                return
        raise AssertionError(self.diagnostic(f"Timed out after {max_cycles} CPU cycles"))

    def run_cycles(self, cycles: int) -> None:
        deadline = self.cycles + cycles
        while self.cycles < deadline:
            self.step()

    def idle(self) -> bool:
        return not self._input and not self.ram[0xC6] and self.cpu.pc in self._idle

    def boot(self, max_cycles: int = 3_000_000) -> str:
        if self._idle:
            self.run_until(lambda machine: machine.idle(), max_cycles)
        else:
            self.run_cycles(min(max_cycles, 400_000))
        return self.output

    def type_text(self, text: str | bytes) -> None:
        if isinstance(text, str):
            # The firmware converts the lower/upper PETSCII keyboard set
            # into ASCII.  Submit the same bytes a physical keyboard does.
            raw = bytes(
                value - 32 if 0x61 <= value <= 0x7A else
                value + 0x80 if 0x41 <= value <= 0x5A else value
                for value in text.encode("ascii")
            )
        else:
            raw = text
        self._input.extend(raw.replace(b"\n", b"\r"))

    def command(self, text: str, max_cycles: int = 4_000_000) -> str:
        start = len(self.raw_output)
        self.type_text(text.rstrip("\r\n") + "\r")
        if self._idle:
            self.run_until(lambda machine: machine.idle(), max_cycles)
        else:
            self.run_cycles(min(max_cycles, 500_000))
            if self._input:
                raise AssertionError(self.diagnostic("Input was not consumed"))
        return "".join(self._decoded_output[start:])

    def press_keys(self, *names: str) -> None:
        self.memory.cia1.keys = {KEYS[name.upper()] for name in names}

    def release_keys(self) -> None:
        self.memory.cia1.keys.clear()

    def call(self, target: str | int, a: int = 0, x: int = 0, y: int = 0,
             max_cycles: int = 1_000_000) -> None:
        """Execute a ROM subroutine via a RAM JSR wrapper; retain returned regs.

        The wrapper is in normally unused test RAM at $02A0.  Its bytes and
        the shell PC/SP are restored afterwards.  This is only for explicit
        public entry/keyboard tests; shell tests use normal input and boot.
        """
        address = self.symbols[target.upper()] if isinstance(target, str) else target
        saved_pc, saved_sp = self.cpu.pc, self.cpu.sp
        saved_bytes = bytes(self.ram[0x02A0:0x02A4])
        self.ram[0x02A0:0x02A4] = bytes((0x20, address & 255, address >> 8, 0xEA))
        self.cpu.pc = 0x02A0
        self.cpu.a, self.cpu.x, self.cpu.y = a, x, y
        self.run_until(lambda machine: machine.cpu.pc == 0x02A3, max_cycles)
        if self.cpu.sp != saved_sp:
            raise AssertionError(self.diagnostic("Subroutine did not preserve stack depth"))
        self.cpu.pc, self.cpu.sp = saved_pc, saved_sp
        self.ram[0x02A0:0x02A4] = saved_bytes

    def warm_reset(self, max_cycles: int = 3_000_000) -> str:
        start = len(self.raw_output)
        self._input.clear()
        self.cpu.sp = 0xFF
        self.cpu.p = self.cpu.UNUSED | self.cpu.INTERRUPT
        self.cpu.pc = self.memory[0xA002] | (self.memory[0xA003] << 8)
        self.boot(max_cycles)
        return "".join(self._decoded_output[start:])

    def cold_reset(self, max_cycles: int = 3_000_000) -> str:
        start = len(self.raw_output)
        self._input.clear()
        self.cpu.sp = 0xFF
        self.cpu.p = self.cpu.UNUSED | self.cpu.INTERRUPT
        self.ram[0], self.ram[1] = 0x2F, 0x37
        self.cpu.pc = self.memory[0xFFFC] | (self.memory[0xFFFD] << 8)
        self.boot(max_cycles)
        return "".join(self._decoded_output[start:])

    def screen(self) -> str:
        rows: list[str] = []
        lower = bool(self.memory.io[0x18] & 2)
        for row in range(25):
            chars: list[str] = []
            for value in self.ram[0x400 + row * 40:0x400 + (row + 1) * 40]:
                value &= 0x7F
                if value == 0:
                    char = "@"
                elif 1 <= value <= 26:
                    char = chr(value + (96 if lower else 64))
                elif 32 <= value <= 63:
                    char = chr(value)
                elif 65 <= value <= 90 and lower:
                    char = chr(value)
                elif value == 27:
                    char = "["
                elif value == 29:
                    char = "]"
                elif value == 64:
                    char = "-"
                else:
                    char = "?"
                chars.append(char)
            rows.append("".join(chars).rstrip())
        return "\n".join(rows).rstrip()

    def diagnostic(self, message: str) -> str:
        recent = "\n".join(
            f"  {pc:04X}: {opcode:02X} A={a:02X} X={x:02X} Y={y:02X} SP={sp:02X}"
            for pc, opcode, a, x, y, sp in self._recent
        )
        return (
            f"{message}\nPC=${self.cpu.pc:04X} SP=${self.cpu.sp:02X} "
            f"P=${self.cpu.p:02X} cycles={self.cycles} IRQs={self.interrupts}\n"
            f"Recent instructions:\n{recent}\nScreen:\n{self.screen()}\n"
            f"Transcript tail: {self.output[-1000:]!r}"
        )


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rom-dir", type=Path, default=PROJECT / "roms")
    parser.add_argument("command", nargs="*")
    arguments = parser.parse_args()
    machine = Machine(arguments.rom_dir)
    print(machine.boot(), end="")
    for command in arguments.command:
        print(machine.command(command), end="")
    print("\n--- FINAL 40 x 25 SCREEN ---")
    print(machine.screen())
