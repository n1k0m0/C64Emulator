"""Screen-backed editor regressions through public KERNAL entry points.

Instructions execute on py65. Keystrokes enter the real ten-byte FIFO;
no screen, capture, INSERT, DELETE, scroll, or conversion routine is stubbed.
The only modeled hardware is the keyboard matrix supplied by test_kernal.

References: Commodore C64 Programmer's Reference Guide pp94-96 (screen
editor, 80 cells, quote-mode DELETE exception, INSERT-mode literal controls).
SPDX-License-Identifier: Apache-2.0
"""
import json
from test_kernal import ROM_PATH, SYMBOLS, call, fresh, start

LEFT, RIGHT, UP, DOWN, DELETE, INSERT, CR = 157, 29, 145, 17, 20, 148, 13
results = []


def check(name, action):
    action()
    results.append({"test": name, "result": "PASS"})


def put(c, text):
    for b in text:
        call(c, 0xFFD2, a=b)


def plot(c, row, col):
    call(c, 0xFFF0, x=row, y=col, p=0x24)


def read_line(c, keys, interrupts=False):
    pending = list(keys)
    received = []
    next_irq = c.processorCycles + 16421
    start(c, 0xFFCF, x=0, p=0x20)
    for _ in range(1000000):
        # Match the native emulator's refill rule: append as soon as C6 falls.
        while pending and c.memory[0xC6] < 10:
            n = c.memory[0xC6]
            c.memory[0x277 + n] = pending.pop(0)
            c.memory[0xC6] = n + 1
        if interrupts and c.processorCycles >= next_irq and not c.p & 4:
            c.memory[0xDC0D] = 0x81
            c.irq()
            next_irq = c.processorCycles + 16421
        c.step()
        if c.pc == 0x1003:
            assert c.sp == 255
            assert c.x == len(received), ("CHRIN offset", c.x, received)
            assert not c.p & 1
            if c.a == CR:
                assert not pending, pending
                return bytes(received)
            received.append(c.a)
            assert len(received) <= 80
            start(c, 0xFFCF, x=len(received), p=0x20)
    raise AssertionError(("editor did not return", hex(c.pc), received, pending))


def input_edits():
    c = fresh()
    keys = b"ABCDEF" + bytes([LEFT, LEFT, DELETE, INSERT]) + b"X\r"
    assert read_line(c, keys) == b"ABCXEF"
    assert c.memory[0x400:0x406] == [1, 2, 3, 24, 5, 6]
    assert c.memory[0xD8] == 0
    assert (c.memory[0xD6], c.memory[0xD3]) == (1, 0)


def edit_existing():
    for line in (b"10 PRINT 1", b"10 REM " + b"A" * 43):
        c = fresh()
        put(c, line + b"\r")
        row = c.memory[0xD6]
        # Return to first physical row, strike over the first digit, RETURN
        # without moving to the end. Entire logical line must be returned.
        assert read_line(c, bytes([UP]) * row + b"2\r") == b"2" + line[1:]
        assert (c.memory[0xD6], c.memory[0xD3]) == (row, 0)


def prompt_and_clear():
    for prompt in (b"VALUE? ", b"P" * 38):
        c = fresh()
        put(c, prompt)
        assert read_line(c, b"12345\r") == b"12345"
    c = fresh()
    put(c, b'PROMPT "')
    assert read_line(c, b"A" + bytes([147]) + b'B"\r') == b"A" + bytes([147]) + b'B"'
    c = fresh()
    put(c, b"VALUE? ")
    assert read_line(c, b"OLD" + bytes([147]) + b"NEW\r") == b"NEW"
    assert c.memory[0x400:0x403] == [14, 5, 23]


def two_rows_and_limit():
    for size in (39, 40, 41, 79, 80, 81, 100):
        c = fresh()
        c.memory[0x251:0x259] = [0xD7] * 8
        text = bytes(65 + i % 26 for i in range(size))
        assert read_line(c, text + b"\r", interrupts=True) == text[:80], size
        assert c.memory[0x251:0x259] == [0xD7] * 8
        assert c.memory[0xD4] == 0 and c.memory[0xD8] == 0
    for origin in (0, 1):
        c = fresh()
        line = b"10 REM " + b"A" * 73
        put(c, line)
        assert c.memory[0xD6] == 2
        assert read_line(c, bytes([UP]) * (2 - origin) + b"\r") == line


def insertion_colors_and_line_shift():
    c = fresh()
    put(c, b"ABCDE")
    c.memory[0xD800:0xD805] = [1, 2, 3, 4, 5]
    plot(c, 0, 2)
    put(c, [INSERT])
    assert c.memory[0x400:0x406] == [1, 2, 32, 3, 4, 5]
    assert c.memory[0xD800:0xD806] == [1, 2, 14, 3, 4, 5]
    put(c, b"X")
    assert c.memory[0x400:0x406] == [1, 2, 24, 3, 4, 5]
    put(c, [DELETE])
    assert c.memory[0x400:0x405] == [1, 2, 3, 4, 5]
    assert c.memory[0xD800:0xD805] == [1, 2, 3, 4, 5]

    # Allocate an extra physical row without trampling following screen text.
    for row in (0, 7, 23, 24):
        c = fresh()
        for r in range(25):
            c.memory[0x400 + 40*r:0x428 + 40*r] = [r + 1] * 40
            c.memory[0xD800 + 40*r:0xD828 + 40*r] = [r & 15] * 40
        before = c.memory[0x400:0x7E8]
        colors = c.memory[0xD800:0xDBE8]
        plot(c, row, 20)
        put(c, [INSERT])
        actual_row = row if row < 24 else 23
        start_at = actual_row * 40
        expected = before[:] if row < 24 else before[40:] + [32] * 40
        expected_colors = colors[:] if row < 24 else colors[40:] + [14] * 40
        if row < 24:
            expected[(row + 2)*40:] = before[(row + 1)*40:24*40]
            expected_colors[(row + 2)*40:] = colors[(row + 1)*40:24*40]
        line_code = row + 1
        expected[start_at:start_at + 80] = [line_code]*20 + [32] + [line_code]*20 + [32]*39
        expected_colors[start_at:start_at + 80] = [row & 15]*20 + [14] + [row & 15]*20 + [14]*39
        assert c.memory[0x400:0x7E8] == expected, row
        assert c.memory[0xD800:0xDBE8] == expected_colors, row
        assert c.memory[0xD9 + actual_row + 1] & 128 == 0
        assert (c.memory[0xD6], c.memory[0xD3]) == (actual_row, 20)


def boundary_delete_and_cursor():
    c = fresh()
    put(c, b"A" * 39 + b"BCDE")
    plot(c, 1, 0)
    put(c, [DELETE])
    assert c.memory[0x400:0x42A] == [1]*39 + [3, 4, 5]
    assert (c.memory[0xD6], c.memory[0xD3]) == (0, 39)
    plot(c, 1, 0)
    put(c, [LEFT])
    assert (c.memory[0xD6], c.memory[0xD3]) == (0, 39)
    put(c, [RIGHT])
    assert (c.memory[0xD6], c.memory[0xD3]) == (1, 0)
    plot(c, 0, 0)
    put(c, [LEFT, UP, DELETE])
    assert (c.memory[0xD6], c.memory[0xD3]) == (0, 0)
    assert c.memory[0x400] == 1


def quoted_and_inserted_controls():
    c = fresh()
    text = b'PRINT "A' + bytes([147]) + b'B"'
    assert read_line(c, text + b"\r") == text
    c = fresh()
    assert read_line(c, b'PRINT "AB' + bytes([DELETE]) + b'C"\r') == b'PRINT "AC"'
    c = fresh()
    assert read_line(c, b'PRINT "A"' + bytes([DELETE]) + b'B"\r') == b'PRINT "AB"'
    c = fresh()
    put(c, b'"AB"')
    plot(c, 0, 2)
    put(c, [INSERT, DELETE])
    assert c.memory[0x402] == 0x94 and c.memory[0xD8] == 0
    put(c, [CR])
    assert read_line(c, [UP, CR]) == b'"A\x14B"'
    c = fresh()
    put(c, [INSERT] * 300)
    assert c.memory[0xD8] == 80, "INSERT must not wrap its pending count"
    assert c.memory[0xDA] & 128 == 0
    put(c, [CR])
    assert c.memory[0xD8] == 0
    c = fresh()
    put(c, [INSERT, INSERT])
    assert c.memory[0xD8] == 2
    put(c, [147, 5])
    assert c.memory[0x400:0x402] == [0xD3, 0x85]
    assert c.memory[0xD8] == 0 and c.memory[0x286] == 14


def abandoned_collector_reentry():
    c = fresh()
    c.memory[0x200:0x204] = list(b"OLD\r")
    start(c, 0xFFCF, x=0, p=0x20)
    for _ in range(10000):
        c.step()
        if c.pc == SYMBOLS["LINE_WAIT"]:
            break
    else:
        raise AssertionError("initial CHRIN did not reach its waiting loop")
    assert c.memory[0x33C] == 0x80
    # A direct BASIC warm-entry JMP abandons this call stack. Re-entering
    # CHRIN must start a new collector, never treat $80 as buffered text.
    assert read_line(c, b"NEW\r") == b"NEW"


def full_line_delete_and_insert():
    c = fresh()
    text = b"A" * 80
    assert read_line(c, text + bytes([DELETE]) + b"B\r") == b"A"*79 + b"B"
    c = fresh()
    put(c, b"A" * 80)
    plot(c, 0, 1)
    put(c, [INSERT])
    assert c.memory[0x400:0x450] == [1]*80
    assert c.memory[0xD8] == 0


def scroll_and_sprite_tail():
    c = fresh()
    sentinel = list(range(0xB0, 0xC8))
    c.memory[0x7E8:0x800] = sentinel
    c.memory[0xDBE8:0xDC00] = sentinel
    put(c, [147])
    assert c.memory[0x7E8:0x800] == sentinel
    assert c.memory[0xDBE8:0xDC00] == sentinel
    plot(c, 24, 0)
    text = b"Z" * 75
    assert read_line(c, text + b"\r") == text
    assert c.memory[0x7E8:0x800] == sentinel
    assert c.memory[0xDBE8:0xDC00] == sentinel
    assert (c.memory[0xD6], c.memory[0xD3]) == (24, 0)
    # No continuation chain can extend beyond the supported two rows.
    links = c.memory[0xD9:0xF2]
    assert links[0] & 128
    assert not any(not (a & 128) and not (b & 128) for a, b in zip(links, links[1:]))


for name, fn in (
    ("CHRIN edits typed text instead of returning cursor-control bytes", input_edits),
    ("RETURN re-reads a visible existing line from either physical half", edit_existing),
    ("input prompt boundaries and clear-mid-input", prompt_and_clear),
    ("39/40/41/79/80/81/100-character limits under IRQ and rapid host refill", two_rows_and_limit),
    ("INSERT/DELETE preserve colors and expanding rows preserves following text", insertion_colors_and_line_shift),
    ("DELETE crosses a continuation and cursor movement crosses physical rows", boundary_delete_and_cursor),
    ("quote and insert mode controls round-trip with documented DELETE exception", quoted_and_inserted_controls),
    ("CHRIN restarts an abandoned collector after direct BASIC warm entry", abandoned_collector_reentry),
    ("80th-cell deletion works and full occupied line cannot lose data on INSERT", full_line_delete_and_insert),
    ("bottom scrolling retains 80-cell input and CLEAR preserves sprite pointers", scroll_and_sprite_tail),
):
    check(name, fn)
print(json.dumps({"rom": str(ROM_PATH), "checks": results,
                  "scope": "CPU instructions + host FIFO; native VIC/raster timing is separate."}, indent=2))
