"""Tape header policy, executing real SAVE/LOAD control flow.

The block writer and decoded-byte reader are stubbed at their subroutine
boundaries. These tests cover header/relocation semantics, not TAP pulses.
SA bit1 must append two checksum-protected type-5 headers. Reference:
Commodore PRG p82 (secondary address bits); Commodore C2N technical manual
cassette format diagram (192-byte type-5 end block, duplicated).
https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_2/page_082.html
https://www.scribd.com/document/971226317/Commodore-1531-Technical-Manual
SPDX-License-Identifier: Apache-2.0
"""
import json
from test_kernal import ROM_PATH, SYMBOLS, call, fresh, start


def return_from_hook(c):
    low = c.memory[0x100 + ((c.sp + 1) & 255)]
    high = c.memory[0x100 + ((c.sp + 2) & 255)]
    c.sp = (c.sp + 2) & 255
    c.pc = ((high << 8) | low) + 1


def run(c, hooks):
    for _ in range(100000):
        if c.pc in hooks:
            hooks[c.pc](c)
            return_from_hook(c)
        else:
            c.step()
        if c.pc == 0x1003:
            assert c.sp == 255
            return
    raise AssertionError(f"tape policy call did not return: {c.pc:04X}")


results = []
for secondary in range(4):
    c = fresh()
    call(c, 0xFFBA, a=1, x=1, y=secondary)
    call(c, 0xFFBD, a=0, x=0, y=0)
    c.memory[0xC1:0xC3] = [0, 0x40]
    blocks = []
    def header(c):
        blocks.append(("header", c.memory[0x364], c.a))
    def data(c):
        blocks.append(("data", c.a))
    start(c, 0xFFD8, a=0xC1, x=4, y=0x40)
    run(c, {SYMBOLS["TAPE_WRITE_HEADER_BLOCK"]: header,
            SYMBOLS["TAPE_WRITE_DATA_BLOCK"]: data})
    expected_type = 3 if secondary & 1 else 1
    assert [b[1] for b in blocks if b[0] == "header"] == [expected_type] * 2 + ([5] * 2 if secondary & 2 else [])
    assert len([b for b in blocks if b[0] == "data"]) == 2
    assert not c.p & 1
    results.append({"test": "SAVE secondary bit0 header", "secondary": secondary,
                    "expected_header_type": expected_type, "result": "PASS"})

for header_type in (1, 3):
    for secondary in (0, 1):
        c = fresh()
        call(c, 0xFFBA, a=1, x=1, y=secondary)
        call(c, 0xFFBD, a=0, x=0, y=0)
        header = [header_type, 0, 0x40, 4, 0x40] + [32] * 187
        check = 0
        for b in header:
            check ^= b
        header.append(check)
        payload = [0x42, 0x43, 0, 0xFF]
        stream = iter(header + header + payload + [0x42 ^ 0x43 ^ 0xFF])
        def seek(c):
            c.p &= ~1
        def read(c):
            c.a = next(stream)
            c.p = (c.p & ~0x83) | (0x80 if c.a & 128 else 0) | (2 if c.a == 0 else 0)
        start(c, 0xFFD5, a=0, x=0, y=0x50)
        run(c, {SYMBOLS["TAPE_SEEK_SYNC"]: seek,
                SYMBOLS["TAPE_SEEK_SYNC_VALUE"]: seek,
                SYMBOLS["TAPE_READ_BYTE"]: read})
        destination = 0x4000 if header_type == 3 or secondary else 0x5000
        assert c.memory[destination:destination + 4] == payload
        assert (c.y << 8) | c.x == destination + 4
        assert not c.p & 1
        results.append({"test": "LOAD tape relocation policy", "header_type": header_type,
                        "secondary": secondary, "destination": destination, "result": "PASS"})

# Run the real block encoding loop; only the pulse-level byte writer and
# pilot/sync/end wave generators are intercepted. This checks all 192 header
# bytes, placement after data, both countdowns, and real XOR checksums.
for secondary in range(4):
    c = fresh()
    name = b"EOT-ROUNDTRIP"
    c.memory[0x1800:0x1800 + len(name)] = list(name)
    call(c, 0xFFBA, a=1, x=1, y=secondary)
    call(c, 0xFFBD, a=len(name), x=0, y=0x18)
    c.memory[0xC1:0xC3] = [0, 0x40]
    payload = [0x42, 0x43, 0, 0xFF]
    c.memory[0x4000:0x4004] = payload
    blocks = []
    def sync(c):
        blocks.append({"sync": c.a, "bytes": []})
    def emit(c):
        blocks[-1]["bytes"].append(c.a)
    def noop(c):
        pass
    start(c, 0xFFD8, a=0xC1, x=4, y=0x40)
    run(c, {SYMBOLS["TAPE_WRITE_PILOT"]: noop,
            SYMBOLS["TAPE_WRITE_SYNC"]: sync,
            SYMBOLS["TAPE_WRITE_BYTE"]: emit,
            SYMBOLS["TAPE_WRITE_END"]: noop})
    assert [b["sync"] for b in blocks] == [0x89, 9] * (3 if secondary & 2 else 2)
    for index, block in enumerate(blocks):
        data = block["bytes"]
        xor = 0
        for byte in data:
            xor ^= byte
        assert xor == 0
        if index in (2, 3):
            assert data[:-1] == payload
        else:
            assert len(data) == 193
            assert data[0] == (5 if index >= 4 else 3 if secondary & 1 else 1)
            assert data[1:5] == [0, 0x40, 4, 0x40]
            assert data[5:5 + len(name)] == list(name)
            assert data[5 + len(name):192] == [32] * (187 - len(name))
    assert blocks[0]["bytes"] == blocks[1]["bytes"]
    if secondary & 2:
        assert blocks[4]["bytes"] == blocks[5]["bytes"]
    assert not c.p & 1 and c.memory[1] & 32
    results.append({"test": "SAVE actual header/data/EOT encoder", "secondary": secondary,
                    "blocks": len(blocks), "result": "PASS"})

# A valid EOT header must terminate the filename search before any subsequent
# header/data, independent of whether its filename matches the LOAD request.
for named in (False, True):
    c = fresh()
    call(c, 0xFFBA, a=1, x=1, y=0)
    c.memory[0x1800:0x1807] = list(b"MISSING")
    call(c, 0xFFBD, a=7 if named else 0, x=0, y=0x18)
    header = [5, 0, 0x40, 4, 0x40] + [32] * 187
    checksum = 0
    for b in header:
        checksum ^= b
    stream = iter(header + [checksum])
    seen = [0]
    def seek(c):
        c.p &= ~1
    def read(c):
        c.a = next(stream)
        seen[0] += 1
        c.p = (c.p & ~0x83) | (0x80 if c.a & 128 else 0) | (2 if not c.a else 0)
    start(c, 0xFFD5, a=0, x=0, y=0x50)
    run(c, {SYMBOLS["TAPE_SEEK_SYNC"]: seek,
            SYMBOLS["TAPE_READ_BYTE"]: read})
    assert seen[0] == 193
    assert c.p & 1 and c.a == 4 and c.memory[0x90] & 128
    assert c.memory[1] & 32
    results.append({"test": "LOAD terminates on EOT", "named": named,
                    "status": c.memory[0x90], "result": "PASS"})

print(json.dumps({"rom": str(ROM_PATH), "checks": results,
                  "scope": "Real block byte/checksum encoders; physical pulse timing remains a separate test."}, indent=2))
