"""Exercise the complete keyboard scan through CIA matrix contacts.

Expected mappings are independent data extracted from the named reference,
not the implementation's ROM tables. No input bytes are injected into the
queue by this test. It excludes electrical ghosting and joystick contacts.
SPDX-License-Identifier: Apache-2.0
"""
from pathlib import Path
import json
import sys

from test_kernal import ROM_PATH, call, fresh

REFERENCE = json.loads((Path(__file__).with_name("keyboard_expected.json")).read_text())
KEYS = {int(k): v for k, v in REFERENCE["keys"].items()}
MODIFIERS = {15, 52, 58, 61}
results = []


def press(c, codes, p=0x24):
    c.memory.keys = {(i // 8, i % 8) for i in codes}
    call(c, 0xFF9F, p=p)
    assert c.memory[0xDC00] == 0x7F
    assert c.p & 12 == p & 12


def drain(c):
    out = []
    while c.memory[0xC6]:
        call(c, 0xFFE4)
        out.append(c.a)
    return out


for mode, modifiers, flags in ((0, set(), 0), (1, {15}, 1), (1, {52}, 1),
                               (2, {61}, 2), (3, {58}, 4),
                               (3, {15, 58}, 5), (3, {58, 61}, 6),
                               (3, {15, 58, 61}, 7)):
    checked = 0
    for index in range(64):
        if index in MODIFIERS:
            continue
        c = fresh()
        press(c, modifiers | {index}, p=0x28)
        expected = KEYS[index][mode]
        actual = drain(c)
        assert actual == ([] if expected == 255 else [expected]), (index, mode, modifiers, expected, actual)
        assert c.memory[0x28D] == flags
        checked += 1
    results.append({"test": "mapping", "modifiers": sorted(modifiers), "contacts_checked": checked, "result": "PASS"})

for key in MODIFIERS:
    c = fresh()
    press(c, {key})
    assert drain(c) == [], key
results.append({"test": "all modifier keys alone never enqueue a byte", "result": "PASS"})

c = fresh()
press(c, {10})
assert drain(c) == [65]
press(c, {10, 15})
assert drain(c) == [193]
press(c, set())
assert (c.memory[0xCB], c.memory[0xC5], c.memory[0x28D]) == (64, 64, 0)
results.append({"test": "modifier change while held and release state", "result": "PASS"})

for flag, key, expected_repeated in ((0, 10, False), (0x80, 10, True),
                                     (0, 2, True), (0x40, 2, False),
                                     (0, 7, True), (0, 0, True), (0, 60, True)):
    c = fresh()
    c.memory[0x28A] = flag
    emissions = []
    for scan in range(80):
        press(c, {key})
        for value in drain(c):
            emissions.append((scan, value))
    expected_scans = [0] + list(range(19, 80, 4)) if expected_repeated else [0]
    assert [s for s, _ in emissions] == expected_scans, (flag, key, emissions)
    results.append({"test": "repeat", "flag": flag, "key": key, "emission_scans": expected_scans, "result": "PASS"})

for maximum in (0, 5, 10, 20):
    c = fresh()
    c.memory[0x289] = maximum
    for key in range(15):
        press(c, {key})
        press(c, set())
    assert c.memory[0xC6] == min(maximum, 10)
    assert c.memory[0x281] == 0, "queue overwrote memory-bottom low byte"
results.append({"test": "queue capacity and guard byte", "result": "PASS"})

c = fresh()
assert c.memory[0xD018] == 0x15
press(c, {15, 61})
assert c.memory[0xD018] == 0x17 and drain(c) == []
for _ in range(10):
    press(c, {15, 61})
assert c.memory[0xD018] == 0x17
press(c, set())
press(c, {52, 61})
assert c.memory[0xD018] == 0x15
press(c, set())
call(c, 0xFFD2, a=8)
press(c, {15, 61})
assert c.memory[0xD018] == 0x15
press(c, set())
call(c, 0xFFD2, a=9)
press(c, {15, 61})
assert c.memory[0xD018] == 0x17
results.append({"test": "SHIFT+Commodore edge-triggered case toggle and controls 8/9", "result": "PASS"})

print(json.dumps({"rom": str(ROM_PATH), "reference": REFERENCE["source"], "checks": results}, indent=2))
