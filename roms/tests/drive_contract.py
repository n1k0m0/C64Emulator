#!/usr/bin/env python3
"""Exercise the actual 6502 drive helper and its documented host entry ABI.

Run: python tests/drive_contract.py [path/to/open-1541-dos.bin]
Dependency: py65==1.2.0. No proprietary reference ROM is required.
These CPU-level tests do not claim physical VIA/disk timing validation.
SPDX-License-Identifier: Apache-2.0
"""
from __future__ import annotations

import argparse
from pathlib import Path
import random
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
for vendor in (ROOT / "vendor", ROOT.parent / "rom_review/vendor"):
    if (vendor / "py65").is_dir():
        sys.path.insert(0, str(vendor))
        break
from py65.devices.mpu6502 import MPU
import drive_build


# Published four-to-five GCR recording code, written as a format expectation.
GCR = (0x0A, 0x0B, 0x12, 0x13, 0x0E, 0x0F, 0x16, 0x17,
       0x09, 0x19, 0x1A, 0x1B, 0x0D, 0x1D, 0x1E, 0x15)


def pack_gcr(data: bytes) -> bytes:
    if len(data) % 4:
        raise ValueError("GCR input must consist of four-byte groups")
    result = bytearray()
    for offset in range(0, len(data), 4):
        value = 0
        for byte in data[offset:offset + 4]:
            value = (value << 5) | GCR[byte >> 4]
            value = (value << 5) | GCR[byte & 15]
        result.extend(value.to_bytes(5, "big"))
    return bytes(result)


def data_record(payload: bytes) -> bytes:
    if len(payload) != 256:
        raise ValueError("A sector payload is 256 bytes")
    checksum = 0
    for byte in payload:
        checksum ^= byte
    return bytes([7]) + payload + bytes([checksum, 0, 0])


class DriveContract(unittest.TestCase):
    rom, labels = drive_build.build()

    def cpu(self) -> MPU:
        memory = [0] * 65536
        memory[0xC000:] = self.rom
        return MPU(memory=memory, pc=0x1000)

    def call(self, cpu: MPU, name: str, limit: int = 300000) -> None:
        entry = self.labels[name]
        cpu.memory[0x1000:0x1003] = [0x20, entry & 255, entry >> 8]
        cpu.pc, cpu.sp = 0x1000, 0xBD
        cpu.p &= ~cpu.DECIMAL
        for _ in range(limit):
            cpu.step()
            if cpu.pc == 0x1003:
                return
        self.fail(f"{name} exceeded {limit} CPU instructions at ${cpu.pc:04X}")

    def test_rebuild_and_host_entry_addresses(self) -> None:
        rom, labels = drive_build.build()
        self.assertEqual(self.rom, rom)
        self.assertEqual(len(rom), 16384)
        for name, address in drive_build.HOST_ENTRIES.items():
            self.assertEqual(labels[name], address)
        self.assertEqual(rom[0x3FFA:], bytes.fromhex("75 f4 00 c0 b0 f2"))
        self.assertEqual(rom[0x3E7F:0x3E85], bytes.fromhex("68 a8 68 aa 68 40"))

    def test_reset_sets_host_boot_ready_and_enters_idle(self) -> None:
        cpu = self.cpu()
        cpu.pc = 0xC000
        for _ in range(200):
            cpu.step()
        self.assertEqual(cpu.pc, 0xC194)
        self.assertEqual(cpu.memory[0x1800], 0)
        self.assertEqual(cpu.memory[0x1802], 0x0E)
        self.assertEqual(cpu.memory[0x180E], 0x7F)
        self.assertEqual(cpu.memory[0x1C0E], 0x7F)

    def test_encode_and_decode_all_byte_values(self) -> None:
        # Each byte value appears in each source position; expected GCR is
        # independently bit-packed rather than obtained from the ROM table.
        for value in range(256):
            original = bytes((value, value ^ 0xFF, value ^ 0x55, value ^ 0xAA))
            cpu = self.cpu()
            cpu.memory[0x0400:0x0404] = original
            cpu.memory[0x3A:0x3E] = [0, 4, 0x80, 4]
            self.call(cpu, "GCR_ENCODE_FOUR")
            encoded = bytes(cpu.memory[0x0480:0x0485])
            self.assertEqual(encoded, pack_gcr(original), f"input {original.hex()}")
            cpu.memory[0x3A:0x3E] = [0x80, 4, 0, 5]
            self.call(cpu, "GCR_DECODE_FOUR")
            self.assertFalse(cpu.p & cpu.CARRY)
            self.assertEqual(bytes(cpu.memory[0x0500:0x0504]), original)

    def test_decoder_rejects_every_invalid_five_bit_code(self) -> None:
        valid = pack_gcr(bytes([0x01, 0x23, 0x45, 0x67]))
        value = int.from_bytes(valid, "big")
        for invalid in set(range(32)) - set(GCR):
            corrupted = ((value & ((1 << 35) - 1)) | invalid << 35).to_bytes(5, "big")
            cpu = self.cpu()
            cpu.memory[0x0400:0x0405] = corrupted
            cpu.memory[0x3A:0x3E] = [0, 4, 0, 5]
            self.call(cpu, "GCR_DECODE_FOUR")
            self.assertTrue(cpu.p & cpu.CARRY, f"code ${invalid:02X}")

    def test_whole_sector_encode_and_decode(self) -> None:
        rng = random.Random(1541)
        payloads = [bytes(256), bytes([255]) * 256, bytes(range(256)),
                    bytes(reversed(range(256))), bytes(rng.randrange(256) for _ in range(256))]
        for payload in payloads:
            cpu = self.cpu()
            cpu.memory[0x0500:0x0600] = payload
            cpu.memory[0x50:0x52] = [0, 5]
            self.call(cpu, "ENCODE_DATA_BLOCK")
            expected = pack_gcr(data_record(payload))
            # The writer has a 255/70 split; the reader uses a 256/69 split.
            encoded = bytes(cpu.memory[0x0200:0x02FF] + cpu.memory[0x0100:0x0146])
            self.assertEqual(encoded, expected)
            cpu.memory[0x0200:0x0300] = expected[:256]
            cpu.memory[0x0100:0x0145] = expected[256:]
            cpu.memory[0x51] = 6
            cpu.memory[0x56] = 0
            self.call(cpu, "DECODE_DATA_BLOCK")
            self.assertFalse(cpu.p & cpu.CARRY)
            self.assertEqual(bytes(cpu.memory[0x0600:0x0700]), payload)

    def test_verify_and_checksum_failures(self) -> None:
        payload = bytes(range(256))
        for mismatch in (False, True):
            cpu = self.cpu()
            encoded = pack_gcr(data_record(payload))
            cpu.memory[0x0200:0x0300] = encoded[:256]
            cpu.memory[0x0100:0x0145] = encoded[256:]
            cpu.memory[0x0600:0x0700] = payload
            if mismatch:
                cpu.memory[0x0607] ^= 1
            before = bytes(cpu.memory[0x0600:0x0700])
            cpu.memory[0x51], cpu.memory[0x56] = 6, 1
            self.call(cpu, "DECODE_DATA_BLOCK")
            self.assertFalse(cpu.p & cpu.CARRY)
            self.assertEqual(cpu.memory[0x57], 7 if mismatch else 0)
            self.assertEqual(bytes(cpu.memory[0x0600:0x0700]), before)
        record = bytearray(data_record(payload))
        record[257] ^= 1
        encoded = pack_gcr(record)
        cpu = self.cpu()
        cpu.memory[0x0200:0x0300] = encoded[:256]
        cpu.memory[0x0100:0x0145] = encoded[256:]
        cpu.memory[0x51] = 6
        self.call(cpu, "DECODE_DATA_BLOCK")
        self.assertTrue(cpu.p & cpu.CARRY)

    def execute_job(self, slot: int, command: int) -> tuple[MPU, tuple]:
        cpu = self.cpu()
        cpu.memory[slot] = command
        cpu.memory[6 + slot * 2:8 + slot * 2] = [18, 3]
        if slot < 5:
            buffer = 0x0300 + slot * 0x100
            cpu.memory[buffer:buffer + 3] = [0xE6, 0x80, 0x60]  # INC $80; RTS
        cpu.pc, cpu.sp, cpu.a, cpu.x, cpu.y, cpu.p = 0x1234, 0xBD, 0x3C, 0x4D, 0x5E, 0x21
        original = (cpu.pc, cpu.sp, cpu.a, cpu.x, cpu.y, cpu.p)
        # Exact stack prepared by Drive1541Hardware.EnterDosJobInterrupt.
        for value in (cpu.pc >> 8, cpu.pc & 255, (cpu.p | 0x20) & 0xEF,
                      cpu.a, cpu.x, cpu.y, 0xFE, 0x7E):
            cpu.stPush(value)
        cpu.p |= cpu.INTERRUPT
        cpu.pc = 0xF2B0
        for _ in range(3000):
            cpu.step()
            if cpu.pc == original[0]:
                return cpu, original
        self.fail(f"Job ${command:02X}/{slot} did not return: ${cpu.pc:04X}")

    def test_execute_jobs_use_buffer_and_restore_host_irq_frame(self) -> None:
        for command in (0xD0, 0xE0):
            for slot in range(5):
                cpu, before = self.execute_job(slot, command)
                # B is a stack-image marker, not a physical CPU status latch;
                # py65 makes it read as one after RTI. Compare real flags.
                self.assertEqual((cpu.pc, cpu.sp, cpu.a, cpu.x, cpu.y, cpu.p & ~cpu.BREAK), before)
                self.assertEqual(cpu.memory[slot], 1)
                self.assertEqual(cpu.memory[0x80], 1)
                self.assertEqual(cpu.memory[0x32:0x34], [6 + slot * 2, 0])

    def test_execute_jobs_reject_second_drive_and_nonexistent_buffer(self) -> None:
        for slot, command, status in ((0, 0xD1, 0x0F), (0, 0xE1, 0x0F),
                                      (5, 0xD0, 0x02), (5, 0xE0, 0x02)):
            cpu, before = self.execute_job(slot, command)
            self.assertEqual(cpu.memory[slot], status)
            self.assertEqual(cpu.memory[0x80], 0)
            self.assertEqual((cpu.pc, cpu.sp, cpu.a, cpu.x, cpu.y, cpu.p & ~cpu.BREAK), before)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", nargs="?", type=Path)
    args, unittest_args = parser.parse_known_args()
    if args.rom:
        DriveContract.rom = args.rom.read_bytes()
    unittest.main(argv=[sys.argv[0], *unittest_args], verbosity=2)
