#!/usr/bin/env python3
"""Run isolated CPU/ROM checks; native emulator integration is a separate gate.
SPDX-License-Identifier: Apache-2.0
"""
from pathlib import Path
import argparse, hashlib, json, subprocess, sys, time

ROOT = Path(__file__).resolve().parent


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--build-dir',type=Path,default=ROOT/'build')
    p.add_argument('--results-dir',type=Path,default=ROOT/'results')
    args=p.parse_args();roms=args.build_dir.resolve()/'roms';out=args.results_dir.resolve()
    out.mkdir(parents=True,exist_ok=True);logs=out/'logs';logs.mkdir(exist_ok=True)
    jobs=[
        ('basic', ['tests/basic/boot_contract.py','--rom-dir',str(roms),'--output',str(out/'basic.json')]),
        ('basic-io', ['tests/basic/io_contract.py','--rom-dir',str(roms),'--output',str(out/'basic-io.json')]),
        ('basic-stress', ['tests/basic/stress_contract.py','--rom-dir',str(roms),'--output',str(out/'basic-stress.json')]),
        ('basic-compatibility', ['tests/basic/compatibility_contract.py','--rom-dir',str(roms),'--output',str(out/'basic-compatibility.json')]),
        ('system', ['tests/system_contract.py','--rom-dir',str(roms),'--output',str(out/'system.json')]),
        ('kernal', ['tests/kernal/test_kernal.py',str(roms/'open-kernal64.bin')]),
        ('keyboard', ['tests/kernal/test_keyboard.py',str(roms/'open-kernal64.bin')]),
        ('screen', ['tests/kernal/test_screen_mit.py',str(roms/'open-kernal64.bin')]),
        ('editor', ['tests/kernal/test_editor.py',str(roms/'open-kernal64.bin')]),
        ('editor-rc3', ['tests/kernal/test_editor_rc3.py',str(roms/'open-kernal64.bin')]),
        ('tape-header', ['tests/kernal/test_tape_policy.py',str(roms/'open-kernal64.bin')]),
        ('rs232', ['tests/kernal/test_rs232.py',str(roms/'open-kernal64.bin')]),
        ('characters', ['tests/character_contract.py',str(roms/'open-character-rom64.bin')]),
        ('drive', ['tests/drive_contract.py',str(roms/'open-1541-dos.bin')]),
    ]
    records=[]
    for name,command in jobs:
        start=time.monotonic()
        result=subprocess.run([sys.executable,*command],cwd=ROOT,text=True,capture_output=True)
        (logs/f'{name}.log').write_text(result.stdout+result.stderr,encoding='utf-8')
        if name in ('kernal','keyboard','screen','editor','editor-rc3','tape-header','rs232') and result.returncode==0:
            try:
                text=result.stdout.strip();parsed=None
                # Some suites import shared CPU fixtures that also print their
                # own result block; the final JSON is the requested suite.
                decoder=json.JSONDecoder()
                while text:
                    parsed,end=decoder.raw_decode(text)
                    text=text[end:].lstrip()
                (out/f'{name}.json').write_text(json.dumps(parsed,indent=2)+'\n')
            except json.JSONDecodeError:
                pass
        record={'suite':name,'exit_code':result.returncode,'passed':result.returncode==0,
                'seconds':round(time.monotonic()-start,3),'log':f'logs/{name}.log'}
        records.append(record)
        print(f"{name}: {'PASS' if record['passed'] else 'FAIL'}",flush=True)
        if not record['passed']: print(result.stdout[-2500:]+result.stderr[-1500:],flush=True)
    summary={'scope':'isolated CPU/ROM checks; see native-emulator.json for full emulator integration',
             'rom_sha256':{f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(roms.glob('*.bin'))},
             'passed_suites':sum(r['passed'] for r in records),'total_suites':len(records),'results':records}
    (out/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
    return 0 if all(r['passed'] for r in records) else 1


if __name__=='__main__':sys.exit(main())
