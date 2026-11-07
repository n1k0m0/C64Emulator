"""Relocated text screens and interrupt-driven cursor contracts.

All tested instructions execute from the delivered KERNAL. The CPU fixture
models CIA keyboard input and explicitly schedules real 6502 IRQ entries;
it never intercepts screen output, cursor drawing, capture or editing code.

Primary contracts: C64 Programmer's Reference Guide pp102-103 and memory map
pp311-319 (online full table):
https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_3/page_103.html
https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_5/page_311.html

SPDX-License-Identifier: Apache-2.0
"""
import json
from test_kernal import ROM_PATH, SYMBOLS, call, fresh, start

BASES = (0x0400, 0x0800, 0x0C00, 0x3C00, 0x4000, 0x8400, 0xC000, 0xCC00)
LEFT, UP, DELETE, INSERT, RETURN, CLEAR = 157, 145, 20, 148, 13, 147
checks = []


def check(name, fn):
    fn()
    checks.append({"test": name, "result": "PASS"})


def put(c, text):
    for b in text:
        call(c, 0xFFD2, a=b)


def plot(c, row, col):
    call(c, 0xFFF0, x=row, y=col, p=0x20)
    assert (c.x, c.y) == (row, col)


def relocated(base):
    c = fresh()
    c.memory[0x0288] = base >> 8
    # Deliberately independent VIC configuration: normal editor operations
    # obey HIBASE, without resetting the application's bank/character setup.
    c.memory[0xDD00], c.memory[0xD018] = 0x85, 0xA7
    c.memory[base:base+1024] = [0x61] * 1024
    c.memory[0xD800:0xDC00] = [0x05] * 1024
    put(c, [CLEAR])
    return c


def irq(c, timer=True):
    c.p &= ~c.INTERRUPT
    before = (c.pc, c.sp, c.a, c.x, c.y, c.p & 0xCF)
    c.memory[0xDC0D] = 0x81 if timer else 0
    c.irq()
    for _ in range(20000):
        c.step()
        if c.pc == before[0]:
            break
    else:
        raise AssertionError("IRQ did not return")
    assert (c.pc, c.sp, c.a, c.x, c.y, c.p & 0xCF) == before


def show_cursor(c):
    c.memory[0xCC] = c.memory[0xCD] = 0
    irq(c)
    assert c.memory[0xCF] == 1


def input_line(c, keys, first_start=True):
    pending, received = list(keys), []
    if first_start:
        start(c, 0xFFCF, x=0, p=0x20)
    next_irq = c.processorCycles + 16421
    for _ in range(1000000):
        while pending and c.memory[0xC6] < 10:
            n = c.memory[0xC6]
            c.memory[0x277+n] = pending.pop(0)
            c.memory[0xC6] = n+1
        if c.processorCycles >= next_irq and not c.p & 4:
            c.memory[0xDC0D] = 0x81
            c.irq()
            next_irq = c.processorCycles + 16421
        c.step()
        if c.pc == 0x1003:
            assert c.sp == 255
            assert c.memory[0xCF] == 0 and c.memory[0xCC] != 0
            if c.a == RETURN:
                assert not pending
                return bytes(received)
            received.append(c.a)
            start(c, 0xFFCF, x=len(received), p=0x20)
    raise AssertionError(("line did not finish", hex(c.pc), received))


def relocation_clear_output():
    for base in BASES:
        c = relocated(base)
        assert c.memory[base:base+1000] == [32] * 1000, hex(base)
        assert c.memory[base+1000:base+1024] == [0x61] * 24
        assert c.memory[0xD800:0xDBE8] == [14] * 1000
        assert c.memory[0xDBE8:0xDC00] == [5] * 24
        old = c.memory[0x400:0x800]
        plot(c, 17, 7)
        put(c, [28, ord('Z')])  # red Z at an address crossing multiple pages
        offset = 17*40+7
        assert c.memory[base+offset] == 26
        assert c.memory[0xD800+offset] == 2
        assert c.memory[0xDD00] == 0x85 and c.memory[0xD018] == 0xA7
        assert c.memory[0x0288] == base >> 8
        if base != 0x400:
            assert c.memory[0x400:0x800] == old


def relocation_input():
    for base in BASES:
        c = relocated(base)
        original = c.memory[0x400:0x800]
        result = input_line(c, b"ABCDEF" + bytes([LEFT, LEFT, DELETE, INSERT]) + b"X\r")
        assert result == b"ABCXEF", (hex(base), result)
        assert c.memory[base:base+6] == [1, 2, 3, 24, 5, 6]
        if base != 0x400:
            assert c.memory[0x400:0x800] == original
        # Re-enter the previous displayed logical line, changing its first cell.
        assert input_line(c, bytes([UP]) + b"Z\r") == b"ZBCXEF"


def relocation_scroll():
    for base in BASES:
        c = relocated(base)
        for row in range(25):
            c.memory[base+row*40:base+(row+1)*40] = [row+1] * 40
            c.memory[0xD800+row*40:0xD800+(row+1)*40] = [row & 15] * 40
        chars, colors = c.memory[base:base+1000], c.memory[0xD800:0xDBE8]
        plot(c, 24, 0)
        put(c, [RETURN])
        assert c.memory[base:base+1000] == chars[40:] + [32] * 40
        assert c.memory[0xD800:0xDBE8] == colors[40:] + [14] * 40
        assert c.memory[base+1000:base+1024] == [0x61] * 24
        assert c.memory[0xDBE8:0xDC00] == [5] * 24


def relocation_insert_rows():
    for base in (0x0800, 0xC000):
        c = relocated(base)
        for row in range(25):
            c.memory[base+row*40:base+(row+1)*40] = [row+1] * 40
            c.memory[0xD800+row*40:0xD800+(row+1)*40] = [row & 15] * 40
        chars, colors = c.memory[base:base+1000], c.memory[0xD800:0xDBE8]
        plot(c, 7, 20)
        put(c, [INSERT])
        offset = 7*40
        assert c.memory[base+offset:base+offset+80] == [8]*20+[32]+[8]*20+[32]*39
        assert c.memory[0xD800+offset:0xD800+offset+80] == [7]*20+[14]+[7]*20+[14]*39
        assert c.memory[base+9*40:base+1000] == chars[8*40:24*40]
        assert c.memory[0xD800+9*40:0xDBE8] == colors[8*40:24*40]
        put(c, b"X" + bytes([DELETE]))
        assert c.memory[base+offset:base+offset+80] == [8]*40+[32]*40
        assert c.memory[0xD800+offset:0xD800+offset+80] == [7]*40+[14]*40


def cursor_phases_and_registers():
    for base in (0x0400, 0x0800, 0xC000):
        c = relocated(base)
        plot(c, 12, 39)
        offset = 12*40+39
        c.memory[base+offset] = 0x81  # reverse A must blink back to plain A
        c.memory[0xD800+offset] = 5
        pointers = [0x72, 0x96, 0x18, 0x57]
        for address, value in zip((0xFB, 0xFC, 0xF3, 0xF4), pointers):
            c.memory[address] = value
        c.a, c.x, c.y, c.p = 0x51, 0xA6, 0x39, 0xE9  # including decimal and carry
        show_cursor(c)
        assert c.memory[base+offset] == 1
        assert c.memory[0xD800+offset] == 14
        assert c.memory[0xCE] == 0x81 and c.memory[0x287] == 5
        assert [c.memory[a] for a in (0xFB, 0xFC, 0xF3, 0xF4)] == pointers
        assert c.memory[0xCD] == 20
        for _ in range(19):
            irq(c)
            assert c.memory[0xCF] == 1
        irq(c)
        assert c.memory[0xCF] == 0 and c.memory[base+offset] == 0x81
        assert c.memory[0xD800+offset] == 5
        for _ in range(20):
            irq(c)
        assert c.memory[0xCF] == 1
        assert [c.memory[a] for a in (0xFB, 0xFC, 0xF3, 0xF4)] == pointers


def cursor_enable_disable():
    c = relocated(0xC000)
    c.memory[0xC000], c.memory[0xD800] = 7, 3
    show_cursor(c)
    c.memory[0xCC] = 255
    irq(c)
    assert c.memory[0xCF] == 0 and c.memory[0xC000] == 7 and c.memory[0xD800] == 3
    for _ in range(40):
        irq(c)
    assert c.memory[0xCF] == 0
    c.memory[0xCC] = 0
    for _ in range(20):
        irq(c)
    assert c.memory[0xCF] == 1
    # A non-timer IRQ must not tick or toggle the cursor.
    phase, count = c.memory[0xCF], c.memory[0xCD]
    irq(c, timer=False)
    assert (c.memory[0xCF], c.memory[0xCD]) == (phase, count)


def cursor_plot_and_output():
    c = relocated(0xC000)
    c.memory[0xC000], c.memory[0xD800] = 0x42, 6
    show_cursor(c)
    call(c, 0xFFF0, p=0x21)  # read position must leave an active cursor intact
    assert (c.x, c.y) == (0, 0) and c.memory[0xCF] == 1
    plot(c, 3, 5)
    assert c.memory[0xC000] == 0x42 and c.memory[0xD800] == 6
    assert c.memory[0xCF] == 0 and c.memory[0xCC] == 0
    show_cursor(c)
    put(c, b"Z")
    assert c.memory[0xC000+125] == 26 and c.memory[0xD800+125] == 14
    assert (c.memory[0xD6], c.memory[0xD3]) == (3, 6)
    assert c.memory[0xCF] == 0 and c.memory[0xCC] == 0


def cursor_relocation_and_clear():
    c = relocated(0x0400)
    c.memory[0x0400], c.memory[0xD800] = 6, 5
    show_cursor(c)
    c.memory[0x288] = 0xC0
    c.memory[0xC000:0xC400] = [0x25] * 1024
    put(c, [CLEAR])
    assert c.memory[0x0400] == 6, "old cursor restored before HIBASE is used"
    assert c.memory[0xC000:0xC3E8] == [32] * 1000
    assert c.memory[0xC3E8:0xC400] == [0x25] * 24
    assert c.memory[0xCF] == 0 and c.memory[0xCC] == 0
    c.memory[0xC000], c.memory[0xD800] = 0x27, 2
    show_cursor(c)
    call(c, 0xFF81)  # CINT restores standard page, unlike CHROUT CLEAR.
    assert c.memory[0xC000] == 0x27 and c.memory[0x288] == 4
    assert c.memory[0xCF] == 0 and c.memory[0xCC] != 0


def cursor_scroll_and_insert():
    c = relocated(0xC000)
    for row in range(25):
        c.memory[0xC000+row*40:0xC000+(row+1)*40] = [row+1] * 40
    before = c.memory[0xC000:0xC3E8]
    plot(c, 24, 3)
    show_cursor(c)
    put(c, [RETURN])
    assert c.memory[0xC000:0xC3E8] == before[40:] + [32]*40
    assert c.memory[0xCF] == 0
    plot(c, 5, 12)
    show_cursor(c)
    put(c, [INSERT])
    assert c.memory[0xC000+5*40:0xC000+7*40] == [7]*12+[32]+[7]*28+[32]*39
    assert c.memory[0xCF] == 0


def idle_input_blinks_and_capture_hides():
    c = relocated(0xC000)
    start(c, 0xFFCF, p=0x20)
    for _ in range(20000):
        c.step()
        if c.pc == SYMBOLS['LINE_WAIT']:
            break
    else:
        raise AssertionError('CHRIN did not wait for input')
    assert c.memory[0xCC] == 0 and c.memory[0xCF] == 0
    for _ in range(20):
        irq(c)
    assert c.memory[0xCF] == 1 and c.memory[0xC000] == 0xA0
    assert input_line(c, b"HELLO\r", first_start=False) == b"HELLO"
    assert c.memory[0xC000:0xC005] == [8, 5, 12, 12, 15]
    assert c.memory[0xCC] != 0 and c.memory[0xCF] == 0


def long_output_keeps_irqs_running():
    for masked in (False, True):
        c = relocated(0xC000)
        c.memory[0xC000:0xC3E8] = [7] * 1000
        show_cursor(c)
        before = int.from_bytes(c.memory[0xA0:0xA3], 'big')
        start(c, 0xFFD2, a=CLEAR, x=0x5A, y=0xA5,
              p=0x2C if masked else 0x28)
        next_irq, serviced = c.processorCycles + 16421, 0
        for _ in range(100000):
            if c.processorCycles >= next_irq and not c.p & 4:
                assert c.memory[0xCC] == 1, 'cursor must stay disabled during clear'
                irq(c)
                serviced += 1
                next_irq = c.processorCycles + 16421
            c.step()
            if c.pc == 0x1003:
                break
        else:
            raise AssertionError('interruptible clear did not return')
        after = int.from_bytes(c.memory[0xA0:0xA3], 'big')
        assert serviced == after - before
        assert (serviced == 0) if masked else (serviced >= 1)
        assert (c.a, c.x, c.y, c.sp) == (CLEAR, 0x5A, 0xA5, 255)
        assert c.p & 12 == (12 if masked else 8), 'caller I/D flags preserved'
        assert c.memory[0xC000:0xC3E8] == [32] * 1000
        assert c.memory[0xD800:0xDBE8] == [14] * 1000
        assert c.memory[0xCC] == 0 and c.memory[0xCF] == 0


for name, fn in (
    ('HIBASE relocates exact 1000-cell clear/output and fixed color RAM across eight page-aligned screens', relocation_clear_output),
    ('relocated input capture and editing leave the old screen unchanged', relocation_input),
    ('relocated scrolling copies all 960 cells/colors and preserves sprite tails', relocation_scroll),
    ('relocated INSERT row expansion and DELETE preserve character/color layout', relocation_insert_rows),
    ('cursor blink preserves reverse underlay, color, scratch pointers and interrupted CPU state', cursor_phases_and_registers),
    ('BLNSW disable restores the overlay; reenable and timer-only 20-jiffy phases work', cursor_enable_disable),
    ('PLOT and CHROUT restore the old cursor before changing position or characters', cursor_plot_and_output),
    ('HIBASE changes and screen clear restore the cached old cursor address', cursor_relocation_and_clear),
    ('scroll and INSERT never move a cursor overlay into text', cursor_scroll_and_insert),
    ('CHRIN enables idle blinking and removes cursor before captured input', idle_input_blinks_and_capture_hides),
    ('long CHROUT clear services timer IRQs while preserving caller interrupt and decimal flags', long_output_keeps_irqs_running),
):
    check(name, fn)

print(json.dumps({'rom': str(ROM_PATH), 'checks': checks,
                  'scope': 'Real 6502 ROM instructions and IRQ frames; keyboard/CIA scheduling fixture, not native VIC rendering.'}, indent=2))
