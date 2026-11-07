"""Quoted-control screen behavior for the MIT BASIC/KERNAL pairing.
SPDX-License-Identifier: Apache-2.0
"""
import json
from test_kernal import ROM_PATH, call, fresh

# Commodore PRG p95 explicitly makes DELETE the quote-mode exception.
# p96 permits literal DELETE only while filling INSERT-created spaces.
# https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_2/page_095.html
count = 0
for code in list(range(32)) + list(range(128, 160)):
    if code in (13, 20):
        continue
    c = fresh()
    call(c, 0xFFD2, a=34)
    call(c, 0xFFD2, a=code)
    expected = code | (0x80 if code < 32 else 0x40)
    assert c.memory[0x401] == expected, (code, c.memory[0x401], expected)
    assert (c.memory[0xD3], c.memory[0xD6]) == (2, 0)
    assert c.memory[0x286] == 14
    assert c.memory[0xD4] == 1
    assert (c.x, c.y) == (0x5A, 0xA5)
    count += 1

c = fresh()
call(c, 0xFFD2, a=34)
call(c, 0xFFD2, a=65)
call(c, 0xFFD2, a=20)
assert c.memory[0x400:0x402] == [34, 32]
assert c.memory[0xD4] == 1 and c.memory[0xD3] == 1
call(c, 0xFFD2, a=20)
assert c.memory[0x400] == 32 and c.memory[0xD4] == 0

c = fresh()
call(c, 0xFFD2, a=34)
call(c, 0xFFD2, a=34)
assert c.memory[0xD4] == 0
call(c, 0xFFD2, a=5)
assert c.memory[0x286] == 1
call(c, 0xFFD2, a=18)
call(c, 0xFFD2, a=65)
assert c.memory[0x402] == 0x81
call(c, 0xFFD2, a=34)
call(c, 0xFFD2, a=13)
assert c.memory[0xD4] == 0 and c.memory[0x33F] == 0
call(c, 0xFFD2, a=65)
assert c.memory[0x428] == 1

c = fresh()
call(c, 0xFFD2, a=34)
for _ in range(39):
    call(c, 0xFFD2, a=65)
assert (c.memory[0xD6], c.memory[0xD3]) == (1, 0)
assert c.memory[0xD4] == 1
call(c, 0xFFD2, a=0x93)
assert c.memory[0x428] == 0xD3
assert c.memory[0x400] == 34

print(json.dumps({"rom": str(ROM_PATH), "quoted_control_cases": count,
                  "quote_toggle": "PASS", "return_resets_modes": "PASS",
                  "automatic_wrap_keeps_quote_mode": "PASS"}, indent=2))
