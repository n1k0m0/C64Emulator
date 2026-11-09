"""ROM disk-command tests with a controlled KERNAL file-API endpoint.

SPDX-License-Identifier: Apache-2.0

The real shell and filesystem 6502 instructions execute on py65. Only the
public KERNAL file API entries are replaced here by deterministic byte-stream
fixtures. These tests establish bounds, atomic import, byte preservation and
error handling. They do NOT verify IEC signaling, disk firmware, or real media
persistence. The separate integration harness covers actual emulator behavior.

Run: python tests/test_disk_contract.py [--json tests/disk-contract-results.json]
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass, field
import hashlib
import json
from pathlib import Path
import time
import unittest

from machine import Machine, PROJECT


@dataclass
class Channel:
    name: str
    kind: str
    mode: str
    data: bytes = b""
    position: int = 0
    pending: bytearray = field(default_factory=bytearray)
    write_error: str | None = None


class FileApiMachine(Machine):
    """Explicit contract double; the ordinary Machine never intercepts calls."""

    def __init__(self):
        super().__init__()
        self.files: dict[str, tuple[str, bytes]] = {}
        self.channels: dict[int, Channel] = {}
        self.open_requests: list[tuple[int, int, int, str]] = []
        self.read_fail_after: dict[str, int] = {}
        self.write_errors: dict[str, str] = {}
        self.status_read_failure = False
        self.status_reads = 0
        self.dos_status = "00, OK,00,00\r"
        self.filename = ""
        self.lfn, self.device, self.secondary = 2, 8, 2
        self.input_lfn: int | None = None
        self.output_lfn: int | None = None

    def finish(self, *, a: int | None = None, carry: bool | None = None):
        if a is not None:
            self.cpu.a = a & 255
            self.cpu.p &= ~(self.cpu.NEGATIVE | self.cpu.ZERO)
            if not self.cpu.a:
                self.cpu.p |= self.cpu.ZERO
            if self.cpu.a & 128:
                self.cpu.p |= self.cpu.NEGATIVE
        if carry is not None:
            self.cpu.p = (self.cpu.p | self.cpu.CARRY) if carry else (self.cpu.p & ~self.cpu.CARRY)
        self.cpu.pc = (self.cpu.stPopWord() + 1) & 65535
        self.cpu.processorCycles += 6
        self.memory.tick(6)
        self.instructions += 1

    def step(self):
        address = self.cpu.pc
        if not self.memory.kernal_visible:
            return super().step()
        if address == 0xFFBD:  # SETNAM
            pointer = self.cpu.x | (self.cpu.y << 8)
            self.filename = bytes(self.memory[pointer:pointer + self.cpu.a]).decode("latin1")
            self.finish()
        elif address == 0xFFBA:  # SETLFS
            self.lfn, self.device, self.secondary = self.cpu.a, self.cpu.x, self.cpu.y
            self.ram[0xB8], self.ram[0xBA], self.ram[0xB9] = self.lfn, self.device, self.secondary
            self.finish()
        elif address == 0xFFC0:  # OPEN
            self.open_requests.append((self.lfn, self.device, self.secondary, self.filename))
            if self.lfn in self.channels:
                self.ram[0x90] = 0x80
                self.finish(a=2, carry=True)
                return
            if self.secondary == 15:
                self.channels[self.lfn] = Channel("STATUS", "S", "R", self.dos_status.encode("ascii"))
                self.finish(a=0, carry=False)
                return
            fields = self.filename.split(",")
            name = fields[0]
            kind = fields[1] if len(fields) > 1 else "P"
            mode = fields[2] if len(fields) > 2 else "R"
            if mode == "W":
                error = self.write_errors.get(name)
                if name in self.files:
                    error = "63, FILE EXISTS,00,00\r"
                self.channels[self.lfn] = Channel(name, kind, mode, write_error=error)
                # OPEN/byte ACKs alone are allowed to succeed even when DOS
                # rejects the write. The command must read the status channel.
                self.finish(a=0, carry=False)
            elif name in self.files and self.files[name][0] == kind:
                self.channels[self.lfn] = Channel(name, kind, mode, self.files[name][1])
                self.dos_status = "00, OK,00,00\r"
                self.finish(a=0, carry=False)
            else:
                self.dos_status = "62, FILE NOT FOUND,00,00\r"
                self.ram[0x90] = 0x80
                self.finish(a=4, carry=True)
        elif address == 0xFFC6:  # CHKIN
            if self.cpu.x not in self.channels:
                self.finish(a=3, carry=True)
            else:
                self.input_lfn = self.cpu.x
                self.ram[0x99] = 8
                self.finish(carry=False)
        elif address == 0xFFC9:  # CHKOUT
            if self.cpu.x not in self.channels:
                self.finish(a=3, carry=True)
            else:
                self.output_lfn = self.cpu.x
                self.ram[0x9A] = 8
                self.finish(carry=False)
        elif address == 0xFFCF and self.input_lfn is not None:  # CHRIN
            channel = self.channels[self.input_lfn]
            if channel.name == "STATUS":
                self.status_reads += 1
            fault = self.read_fail_after.get(channel.name)
            if ((channel.name == "STATUS" and self.status_read_failure)
                    or (fault is not None and channel.position >= fault)):
                self.ram[0x90] = 0x80
                self.finish(a=0, carry=True)
            elif channel.position == len(channel.data):
                self.ram[0x90] = 0x40
                self.finish(a=0, carry=True)
            else:
                value = channel.data[channel.position]
                channel.position += 1
                self.ram[0x90] = 0x40 if channel.position == len(channel.data) else 0
                self.finish(a=value, carry=False)
        elif address == 0xFFD2 and self.output_lfn is not None:  # CHROUT to disk
            self.channels[self.output_lfn].pending.append(self.cpu.a)
            self.finish(carry=False)
        elif address == 0xFFB7:  # READST
            self.finish(a=self.ram[0x90])
        elif address == 0xFFCC:  # CLRCHN
            self.input_lfn = self.output_lfn = None
            self.ram[0x99], self.ram[0x9A] = 0, 3
            self.finish(a=0, carry=False)
        elif address == 0xFFC3:  # CLOSE
            channel = self.channels.pop(self.cpu.a, None)
            if channel is None:
                self.finish(a=3, carry=True)
            else:
                if channel.mode == "W":
                    if channel.write_error:
                        self.dos_status = channel.write_error
                    else:
                        self.files[channel.name] = (channel.kind, bytes(channel.pending))
                        self.dos_status = "00, OK,00,00\r"
                self.finish(a=0, carry=False)
        else:
            super().step()

    def local_file(self, name: str) -> bytes | None:
        for node in range(32):
            record = 0x1000 + node * 32
            if self.ram[record] != 1:
                continue
            candidate = bytes(self.ram[record + 4:record + 20]).split(b"\0", 1)[0]
            if candidate.decode("ascii") == name:
                length = int.from_bytes(self.ram[record + 2:record + 4], "little")
                begin = 0x2000 + node * 512
                return bytes(self.ram[begin:begin + length])
        return None


class DiskContractTests(unittest.TestCase):
    def setUp(self):
        self.machine = FileApiMachine()
        self.machine.boot()

    def command(self, command: str, success: bool = True):
        output = self.machine.command(command)
        self.assertEqual(self.machine.ram[0x0906], 0 if success else 1, output)
        self.assertEqual(self.machine.channels, {}, "Leaked logical file channel")
        self.assertEqual((self.machine.ram[0x99], self.machine.ram[0x9A]), (0, 3))
        return output

    def test_import_zero_one_and_512_bytes(self):
        for size in (0, 1, 512):
            with self.subTest(size=size):
                payload = bytes(i & 255 for i in range(size))
                self.machine.files["SOURCE"] = ("S", payload)
                self.command("import source target")
                self.assertEqual(self.machine.local_file("target"), payload)
        self.assertIn((2, 8, 2, "SOURCE,S,R"), self.machine.open_requests)

    def test_oversized_import_preserves_existing_and_does_not_create(self):
        self.command("echo keep > target")
        self.machine.files["LARGE"] = ("S", b"X" * 513)
        self.command("import large target", success=False)
        self.assertEqual(self.machine.local_file("target"), b"keep\n")
        self.command("import large absent", success=False)
        self.assertIsNone(self.machine.local_file("absent"))

    def test_interrupted_import_preserves_existing(self):
        self.command("echo keep > target")
        self.machine.files["BROKEN"] = ("S", b"incorrect replacement")
        self.machine.read_fail_after["BROKEN"] = 7
        self.command("import broken target", success=False)
        self.assertEqual(self.machine.local_file("target"), b"keep\n")

    def test_export_round_trip_empty_and_512_binary_bytes(self):
        for size in (0, 512):
            with self.subTest(size=size):
                payload = bytes(i & 255 for i in range(size))
                self.machine.files["SOURCE"] = ("S", payload)
                self.command("import source target")
                before_status_reads = self.machine.status_reads
                self.command(f"export target out{size}")
                self.assertEqual(self.machine.files[f"OUT{size}"], ("S", payload))
                self.assertGreater(self.machine.status_reads, before_status_reads)
                self.assertIn((2, 8, 2, f"OUT{size},S,W"), self.machine.open_requests)

    def test_export_detects_dos_file_exists_and_disk_full(self):
        self.command("echo payload > target")
        self.machine.files["EXISTS"] = ("S", b"original")
        self.command("export target exists", success=False)
        self.assertEqual(self.machine.files["EXISTS"], ("S", b"original"))
        self.machine.write_errors["FULL"] = "72, DISK FULL,00,00\r"
        self.command("export target full", success=False)
        self.assertNotIn("FULL", self.machine.files)

    def test_status_channel_failure_does_not_commit_import(self):
        self.command("echo keep > target")
        self.machine.files["SOURCE"] = ("S", b"bad replacement")
        self.machine.status_read_failure = True
        self.command("import source target", success=False)
        self.assertEqual(self.machine.local_file("target"), b"keep\n")

    def test_load_and_run_native_program(self):
        payload = (PROJECT / "examples/hello.prg").read_bytes()
        self.machine.files["HELLO"] = ("P", payload)
        self.command("load hello")
        self.assertEqual(self.machine.ram[0x0921], 1)
        self.assertEqual(int.from_bytes(self.machine.ram[0x091D:0x091F], "little"), 0x6000)
        self.assertEqual(bytes(self.machine.ram[0x6000:0x6000 + len(payload) - 2]), payload[2:])
        output = self.command("run")
        self.assertIn("hello from a native 6510 program!", output)

    def test_invalid_program_headers_and_overflow_disable_run(self):
        samples = (
            b"\x01\x08\x60", b"\x00\xa0\x60", b"\x00\x60",
            b"\xff\x9f\x60\x60", b"\x00", b"",
        )
        for data in samples:
            with self.subTest(data=data):
                self.machine.files["BAD"] = ("P", data)
                self.machine.ram[0x5FFF], self.machine.ram[0xA000] = 0xA5, 0x5A
                self.command("load bad", success=False)
                self.assertEqual(self.machine.ram[0x0921], 0)
                self.assertEqual((self.machine.ram[0x5FFF], self.machine.ram[0xA000]), (0xA5, 0x5A))
                self.command("run", success=False)

    def test_program_can_use_last_byte_of_arena(self):
        self.machine.files["TOP"] = ("P", b"\xff\x9f\x60")
        self.command("load top")
        self.assertEqual(self.machine.ram[0x0921], 1)
        self.assertEqual(int.from_bytes(self.machine.ram[0x091F:0x0921], "little"), 0xA000)
        self.command("run")

    def test_bad_filenames_are_rejected_before_open(self):
        for filename in ('""', "seventeenlettersxx", "0:name", "x,s,w", "@name"):
            with self.subTest(filename=filename):
                opens = len(self.machine.open_requests)
                self.command(f"import {filename} target", success=False)
                self.assertEqual(len(self.machine.open_requests), opens)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    started = time.monotonic()
    images = {
        name: {"bytes": (PROJECT / "roms" / name).stat().st_size,
               "sha256": hashlib.sha256((PROJECT / "roms" / name).read_bytes()).hexdigest()}
        for name in ("open-basic64.bin", "open-kernal64.bin", "open-character-rom64.bin",
                     "c64-basic-kernal.bin")
    }
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(DiskContractTests)
    test_names = [test.id() for test in suite]
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    failed_names = [test.id() for test, _ in result.failures + result.errors]
    unchanged = all(
        hashlib.sha256((PROJECT / "roms" / name).read_bytes()).hexdigest() == metadata["sha256"]
        for name, metadata in images.items()
    )
    if args.json:
        report = {
            "kind": "controlled-kernal-file-api-contract",
            "iec_signaling_verified": False,
            "real_rom_instructions": True,
            "images": images,
            "images_unchanged_during_run": unchanged,
            "tests_run": result.testsRun,
            "tests": [{"name": name,
                       "passed": not any(failed.startswith(name) for failed in failed_names)}
                      for name in test_names],
            "failures": [{"test": str(t), "detail": d} for t, d in result.failures],
            "errors": [{"test": str(t), "detail": d} for t, d in result.errors],
            "passed": result.wasSuccessful() and unchanged,
            "seconds": round(time.monotonic() - started, 3),
        }
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    raise SystemExit(0 if result.wasSuccessful() and unchanged else 1)
