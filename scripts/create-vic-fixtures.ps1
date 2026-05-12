param(
    [string]$OutputDirectory = "artifacts\vic-fixtures"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$script = @'
from pathlib import Path
import json
import sys

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)

code = []

def b(*values):
    code.extend(values)

def lda(value):
    b(0xA9, value & 0xFF)

def ldx(value):
    b(0xA2, value & 0xFF)

def lda_abs(address):
    b(0xAD, address & 0xFF, (address >> 8) & 0xFF)

def sta(address):
    b(0x8D, address & 0xFF, (address >> 8) & 0xFF)

def sta_x(address):
    b(0x9D, address & 0xFF, (address >> 8) & 0xFF)

def wait_raster(line):
    start = len(code)
    b(0xAD, 0x12, 0xD0)
    b(0xC9, line & 0xFF)
    b(0xD0, (start - (len(code) + 2)) & 0xFF)

def cpx(value):
    b(0xE0, value & 0xFF)

def write_sys_prg(filename, code_bytes, main_loop_offset, byte_patches=None, patch_final_jump=True):
    sys_address = 0x080D
    while True:
        text = [0x9E] + [ord(ch) for ch in str(sys_address)] + [0x00]
        next_line = 0x0801 + 2 + 2 + len(text)
        machine_address = next_line + 2
        if machine_address == sys_address:
            break
        sys_address = machine_address

    text = [0x9E] + [ord(ch) for ch in str(sys_address)] + [0x00]
    next_line = 0x0801 + 2 + 2 + len(text)
    basic = [next_line & 0xFF, (next_line >> 8) & 0xFF, 0x0A, 0x00] + text + [0x00, 0x00]
    patched = list(code_bytes)
    main_address = sys_address + main_loop_offset
    if patch_final_jump:
        patched[-2] = main_address & 0xFF
        patched[-1] = (main_address >> 8) & 0xFF
    if byte_patches:
        for patch_offset, target_offset, part in byte_patches:
            target_address = sys_address + target_offset
            patched[patch_offset] = (target_address >> (8 if part == "hi" else 0)) & 0xFF

    prg_path = out / filename
    prg_path.write_bytes(bytes([0x01, 0x08] + basic + patched))
    return prg_path, sys_address

b(0x78)
lda(0x7F); sta(0xDC0D); sta(0xDD0D)
lda_abs(0xDC0D); lda_abs(0xDD0D)
lda(0x1B); sta(0xD011)
lda(0x08); sta(0xD016)
lda(0x06); sta(0xD020)
lda(0x00); sta(0xD021)
ldx(0x00)
clear_loop = len(code)
lda(0x20)
for address in (0x0400, 0x0500, 0x0600, 0x0700):
    sta_x(address)
lda(0x01)
for address in (0xD800, 0xD900, 0xDA00, 0xDB00):
    sta_x(address)
b(0xE8)
b(0xD0, (clear_loop - (len(code) + 2)) & 0xFF)

main_loop = len(code)
wait_raster(0x32); lda(0x02); sta(0xD020); lda(0x13); sta(0xD011)
wait_raster(0x34); lda(0x05); sta(0xD020); lda(0x1B); sta(0xD011)
wait_raster(0x64); lda(0x07); sta(0xD020); lda(0x00); sta(0xD016)
wait_raster(0x66); lda(0x01); sta(0xD020); lda(0x08); sta(0xD016)
wait_raster(0x96); lda(0x03); sta(0xD020)
wait_raster(0xC8); lda(0x06); sta(0xD020)
b(0x4C, 0x00, 0x00)

raster_prg_path, raster_sys_address = write_sys_prg("vic-raster-bars.prg", code, main_loop)

code = []
b(0x78)
lda(0x7F); sta(0xDC0D); sta(0xDD0D)
lda_abs(0xDC0D); lda_abs(0xDD0D)
lda(0x1B); sta(0xD011)
lda(0x08); sta(0xD016)
lda(0x00); sta(0xD020); sta(0xD021)
lda(0x00); sta(0xD010)
lda(0x78); sta(0xD000)
lda(0x64); sta(0xD001)
lda(0x01); sta(0xD027)
lda(0x01); sta(0xD015)
ldx(0x00)
sprite_fill_loop = len(code)
lda(0xFF); sta_x(0x3000)
b(0xE8)
cpx(0x40)
b(0xD0, (sprite_fill_loop - (len(code) + 2)) & 0xFF)

ldx(0x00)
sprite_clear_loop = len(code)
lda(0x20)
for address in (0x0400, 0x0500, 0x0600, 0x0700):
    sta_x(address)
lda(0x01)
for address in (0xD800, 0xD900, 0xDA00, 0xDB00):
    sta_x(address)
b(0xE8)
b(0xD0, (sprite_clear_loop - (len(code) + 2)) & 0xFF)
lda(0xC0); sta(0x07F8)
lda(0x01); sta(0xD015)
sprite_setup_code = list(code)

code = list(sprite_setup_code)
sprite_baseline_loop = len(code)
wait_raster(0x78); lda(0x01); sta(0xD015)
b(0x4C, 0x00, 0x00)
sprite_baseline_prg_path, sprite_baseline_sys_address = write_sys_prg("vic-sprite-baseline.prg", code, sprite_baseline_loop)

code = list(sprite_setup_code)
sprite_main_loop = len(code)
wait_raster(0x69); lda(0x00); sta(0xD015)
for _ in range(12):
    b(0xEA)
lda(0x01); sta(0xD015)
wait_raster(0x6B); lda(0x01); sta(0xD015)
wait_raster(0x78); lda(0x01); sta(0xD015)
b(0x4C, 0x00, 0x00)
sprite_prg_path, sprite_sys_address = write_sys_prg("vic-sprite-enable-phase.prg", code, sprite_main_loop)

code = list(sprite_setup_code)
handler_low_patch = len(code) + 1
lda(0x00); sta(0x0314)
handler_high_patch = len(code) + 1
lda(0x00); sta(0x0315)
lda(0x69); sta(0xD012)
lda(0x1B); sta(0xD011)
lda(0x01); sta(0xD019); sta(0xD01A)
b(0x58)
irq_main_loop = len(code)
irq_main_loop_low_patch = len(code) + 1
b(0x4C, 0x00, 0x00)
irq_handler = len(code)
b(0x48)
lda(0x01); sta(0xD019)
lda(0x02); sta(0xD020)
lda(0x00); sta(0xD015)
for _ in range(12):
    b(0xEA)
lda(0x01); sta(0xD015)
b(0x68)
b(0x4C, 0x81, 0xEA)
sprite_irq_prg_path, sprite_irq_sys_address = write_sys_prg(
    "vic-sprite-irq-phase.prg",
    code,
    irq_main_loop,
    [
        (handler_low_patch, irq_handler, "lo"),
        (handler_high_patch, irq_handler, "hi"),
        (irq_main_loop_low_patch, irq_main_loop, "lo"),
        (irq_main_loop_low_patch + 1, irq_main_loop, "hi"),
    ],
    False)

code = list(sprite_setup_code)
handler_low_patch = len(code) + 1
lda(0x00); sta(0x0314)
handler_high_patch = len(code) + 1
lda(0x00); sta(0x0315)
lda(0x69); sta(0xD012)
lda(0x1B); sta(0xD011)
lda(0x01); sta(0xD019); sta(0xD01A)
b(0x58)
sprite_x_main_loop = len(code)
sprite_x_main_loop_low_patch = len(code) + 1
b(0x4C, 0x00, 0x00)
sprite_x_irq_handler = len(code)
b(0x48)
lda(0x01); sta(0xD019)
lda(0x02); sta(0xD020)
lda(0xA0); sta(0xD000)
b(0x68)
b(0x4C, 0x81, 0xEA)
sprite_x_irq_prg_path, sprite_x_irq_sys_address = write_sys_prg(
    "vic-sprite-x-irq-phase.prg",
    code,
    sprite_x_main_loop,
    [
        (handler_low_patch, sprite_x_irq_handler, "lo"),
        (handler_high_patch, sprite_x_irq_handler, "hi"),
        (sprite_x_main_loop_low_patch, sprite_x_main_loop, "lo"),
        (sprite_x_main_loop_low_patch + 1, sprite_x_main_loop, "hi"),
    ],
    False)

code = list(sprite_setup_code)
handler_low_patch = len(code) + 1
lda(0x00); sta(0x0314)
handler_high_patch = len(code) + 1
lda(0x00); sta(0x0315)
lda(0x69); sta(0xD012)
lda(0x1B); sta(0xD011)
lda(0x01); sta(0xD019); sta(0xD01A)
b(0x58)
sprite_color_entry_loop_low_patch = len(code) + 1
b(0x4C, 0x00, 0x00)
sprite_color_irq_handler = len(code)
b(0x48)
lda(0x01); sta(0xD019)
lda(0x05); sta(0xD020)
lda(0x02); sta(0xD027)
b(0x68)
b(0x4C, 0x81, 0xEA)
sprite_color_main_loop = len(code)
wait_raster(0x00); lda(0x01); sta(0xD027)
b(0x4C, 0x00, 0x00)
sprite_color_irq_prg_path, sprite_color_irq_sys_address = write_sys_prg(
    "vic-sprite-color-irq-phase.prg",
    code,
    sprite_color_main_loop,
    [
        (handler_low_patch, sprite_color_irq_handler, "lo"),
        (handler_high_patch, sprite_color_irq_handler, "hi"),
        (sprite_color_entry_loop_low_patch, sprite_color_main_loop, "lo"),
        (sprite_color_entry_loop_low_patch + 1, sprite_color_main_loop, "hi"),
    ],
    True)

code = list(sprite_setup_code)
lda(0x01); sta(0xD01C)
ldx(0x00)
sprite_multicolor_fill_loop = len(code)
lda(0xAA); sta_x(0x3000)
b(0xE8)
cpx(0x40)
b(0xD0, (sprite_multicolor_fill_loop - (len(code) + 2)) & 0xFF)
handler_low_patch = len(code) + 1
lda(0x00); sta(0x0314)
handler_high_patch = len(code) + 1
lda(0x00); sta(0x0315)
lda(0x69); sta(0xD012)
lda(0x1B); sta(0xD011)
lda(0x01); sta(0xD019); sta(0xD01A)
b(0x58)
sprite_multicolor_color_entry_loop_low_patch = len(code) + 1
b(0x4C, 0x00, 0x00)
sprite_multicolor_color_irq_handler = len(code)
b(0x48)
lda(0x01); sta(0xD019)
lda(0x04); sta(0xD020)
lda(0x02); sta(0xD027)
b(0x68)
b(0x4C, 0x81, 0xEA)
sprite_multicolor_color_main_loop = len(code)
wait_raster(0x00); lda(0x01); sta(0xD027)
b(0x4C, 0x00, 0x00)
sprite_multicolor_color_irq_prg_path, sprite_multicolor_color_irq_sys_address = write_sys_prg(
    "vic-sprite-multicolor-color-irq-phase.prg",
    code,
    sprite_multicolor_color_main_loop,
    [
        (handler_low_patch, sprite_multicolor_color_irq_handler, "lo"),
        (handler_high_patch, sprite_multicolor_color_irq_handler, "hi"),
        (sprite_multicolor_color_entry_loop_low_patch, sprite_multicolor_color_main_loop, "lo"),
        (sprite_multicolor_color_entry_loop_low_patch + 1, sprite_multicolor_color_main_loop, "hi"),
    ],
    True)

code = list(sprite_setup_code)
lda(0x02); sta(0xD026)
lda(0x00); sta(0xD01C)
handler_low_patch = len(code) + 1
lda(0x00); sta(0x0314)
handler_high_patch = len(code) + 1
lda(0x00); sta(0x0315)
lda(0x69); sta(0xD012)
lda(0x1B); sta(0xD011)
lda(0x01); sta(0xD019); sta(0xD01A)
b(0x58)
sprite_multicolor_enable_entry_loop_low_patch = len(code) + 1
b(0x4C, 0x00, 0x00)
sprite_multicolor_enable_irq_handler = len(code)
b(0x48)
lda(0x01); sta(0xD019)
lda(0x06); sta(0xD020)
lda(0x01); sta(0xD01C)
b(0x68)
b(0x4C, 0x81, 0xEA)
sprite_multicolor_enable_main_loop = len(code)
wait_raster(0x00); lda(0x00); sta(0xD01C)
b(0x4C, 0x00, 0x00)
sprite_multicolor_enable_irq_prg_path, sprite_multicolor_enable_irq_sys_address = write_sys_prg(
    "vic-sprite-multicolor-enable-irq-phase.prg",
    code,
    sprite_multicolor_enable_main_loop,
    [
        (handler_low_patch, sprite_multicolor_enable_irq_handler, "lo"),
        (handler_high_patch, sprite_multicolor_enable_irq_handler, "hi"),
        (sprite_multicolor_enable_entry_loop_low_patch, sprite_multicolor_enable_main_loop, "lo"),
        (sprite_multicolor_enable_entry_loop_low_patch + 1, sprite_multicolor_enable_main_loop, "hi"),
    ],
    True)

code = list(sprite_setup_code)
ldx(0x00)
sprite_priority_char_loop = len(code)
lda(0xFF); sta_x(0x2000)
b(0xE8)
cpx(0x08)
b(0xD0, (sprite_priority_char_loop - (len(code) + 2)) & 0xFF)
ldx(0x00)
sprite_priority_screen_loop = len(code)
lda(0x00)
for address in (0x0400, 0x0500, 0x0600, 0x0700):
    sta_x(address)
lda(0x07)
for address in (0xD800, 0xD900, 0xDA00, 0xDB00):
    sta_x(address)
b(0xE8)
b(0xD0, (sprite_priority_screen_loop - (len(code) + 2)) & 0xFF)
lda(0xC0); sta(0x07F8)
lda(0x18); sta(0xD018)
lda(0x00); sta(0xD01B)
handler_low_patch = len(code) + 1
lda(0x00); sta(0x0314)
handler_high_patch = len(code) + 1
lda(0x00); sta(0x0315)
lda(0x69); sta(0xD012)
lda(0x1B); sta(0xD011)
lda(0x01); sta(0xD019); sta(0xD01A)
b(0x58)
sprite_priority_entry_loop_low_patch = len(code) + 1
b(0x4C, 0x00, 0x00)
sprite_priority_irq_handler = len(code)
b(0x48)
lda(0x01); sta(0xD019)
lda(0x02); sta(0xD020)
lda(0x01); sta(0xD01B)
b(0x68)
b(0x4C, 0x81, 0xEA)
sprite_priority_main_loop = len(code)
wait_raster(0x00); lda(0x00); sta(0xD01B)
b(0x4C, 0x00, 0x00)
sprite_priority_irq_prg_path, sprite_priority_irq_sys_address = write_sys_prg(
    "vic-sprite-priority-irq-phase.prg",
    code,
    sprite_priority_main_loop,
    [
        (handler_low_patch, sprite_priority_irq_handler, "lo"),
        (handler_high_patch, sprite_priority_irq_handler, "hi"),
        (sprite_priority_entry_loop_low_patch, sprite_priority_main_loop, "lo"),
        (sprite_priority_entry_loop_low_patch + 1, sprite_priority_main_loop, "hi"),
    ],
    True)

code = []
b(0x78)
lda(0x7F); sta(0xDC0D); sta(0xDD0D)
lda_abs(0xDC0D); lda_abs(0xDD0D)
lda(0x1B); sta(0xD011)
lda(0x08); sta(0xD016)
lda(0x00); sta(0xD020); sta(0xD021)
lda(0x78); sta(0xD006)
lda(0x64); sta(0xD007)
lda(0x01); sta(0xD02A)
lda(0x08); sta(0xD015)
ldx(0x00)
sprite3_fill_loop = len(code)
lda(0xFF); sta_x(0x3000)
b(0xE8)
cpx(0x40)
b(0xD0, (sprite3_fill_loop - (len(code) + 2)) & 0xFF)
ldx(0x00)
sprite3_clear_loop = len(code)
lda(0x20)
for address in (0x0400, 0x0500, 0x0600, 0x0700):
    sta_x(address)
lda(0x01)
for address in (0xD800, 0xD900, 0xDA00, 0xDB00):
    sta_x(address)
b(0xE8)
b(0xD0, (sprite3_clear_loop - (len(code) + 2)) & 0xFF)
lda(0xC0); sta(0x07FB)
lda(0x08); sta(0xD015)
sprite3_wrap_loop = len(code)
wait_raster(0x78); lda(0x08); sta(0xD015)
b(0x4C, 0x00, 0x00)
sprite3_wrap_prg_path, sprite3_wrap_sys_address = write_sys_prg("vic-sprite3-wrap-baseline.prg", code, sprite3_wrap_loop)

manifest = {
    "schemaVersion": 1,
    "name": "Local VIC fixtures",
    "description": "Small generated PRGs for VICE/emulator VIC-II visual timing comparisons.",
    "tests": [
        {
            "id": "vic-raster-bars",
            "name": "VIC raster bars D011 D016",
            "category": "vic",
            "model": "PAL",
            "programPath": str(raster_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(raster_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-baseline",
            "name": "VIC sprite baseline",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_baseline_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_baseline_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-enable-phase",
            "name": "VIC sprite enable phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-irq-phase",
            "name": "VIC sprite IRQ phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_irq_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_irq_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-x-irq-phase",
            "name": "VIC sprite X IRQ phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_x_irq_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_x_irq_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-color-irq-phase",
            "name": "VIC sprite color IRQ phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_color_irq_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_color_irq_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-multicolor-color-irq-phase",
            "name": "VIC sprite multicolor color IRQ phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_multicolor_color_irq_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_multicolor_color_irq_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-multicolor-enable-irq-phase",
            "name": "VIC sprite multicolor enable IRQ phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_multicolor_enable_irq_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_multicolor_enable_irq_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite-priority-irq-phase",
            "name": "VIC sprite priority IRQ phase",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite_priority_irq_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite_priority_irq_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        },
        {
            "id": "vic-sprite3-wrap-baseline",
            "name": "VIC sprite 3 wrap baseline",
            "category": "vic",
            "model": "PAL",
            "programPath": str(sprite3_wrap_prg_path.resolve()),
            "maxCycles": 8000000,
            "arguments": {
                "profile": "accuracy",
                "mountAfterWarmup": "true",
                "warmupCycles": "2000000",
                "command": "SYS" + str(sprite3_wrap_sys_address) + "\\r",
                "writeFrame": "true"
            },
            "expectations": {
                "hashes": {},
                "properties": {}
            }
        }
    ]
}

manifest_path = out / "golden-vic-raster-bars.json"
manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(str(raster_prg_path.resolve()))
print(str(sprite_baseline_prg_path.resolve()))
print(str(sprite_prg_path.resolve()))
print(str(sprite_irq_prg_path.resolve()))
print(str(sprite_x_irq_prg_path.resolve()))
print(str(sprite_color_irq_prg_path.resolve()))
print(str(sprite_multicolor_color_irq_prg_path.resolve()))
print(str(sprite_multicolor_enable_irq_prg_path.resolve()))
print(str(sprite_priority_irq_prg_path.resolve()))
print(str(sprite3_wrap_prg_path.resolve()))
print(str(manifest_path.resolve()))
'@

$tempScript = Join-Path $OutputDirectory "create-vic-fixtures.py"
Set-Content -LiteralPath $tempScript -Value $script -Encoding ASCII

$python = "python"
$bundledPython = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if (Test-Path -LiteralPath $bundledPython) {
    $python = $bundledPython
}

& $python $tempScript (Resolve-Path -LiteralPath $OutputDirectory).Path
