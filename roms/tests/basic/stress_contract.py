#!/usr/bin/env python3
"""Exercise GC, arrays, stack recovery, time and continuation through BASIC.
SPDX-License-Identifier: Apache-2.0
"""
from pathlib import Path
import argparse, json, sys
from boot_contract import CpuBasicSession, normalize

ROOT = Path(__file__).resolve().parents[2]
CASES = [
    ("string garbage collection", [
        '10 FOR I=1 TO 150:A$=STR$(I)+"ABCDEFGHIJKLMNOPQRSTUVWXYZ":NEXT',
        '20 PRINT A$', 'POKE55,0:POKE56,16:CLR', 'RUN'], '150ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
    ("string array survives collection", [
        '10 DIM A$(2):A$(2)="KEEP"',
        '20 FOR I=1 TO 150:B$=STR$(I)+"ABCDEFGHIJKLMNOPQRSTUVWXYZ":NEXT',
        '30 PRINT A$(2);B$', 'POKE55,0:POKE56,16:CLR', 'RUN'], 'KEEP 150ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
    ("nested FOR loops", [
        '10 S=0:FOR I=1 TO 5:FOR J=1 TO 4:S=S+I*J:NEXT J,I',
        '20 PRINT S', 'RUN'], '150'),
    ("array survives new scalar variable", [
        'DIM A(2,3)', 'A(2,3)=42', 'B=99', 'PRINT A(2,3);B'], '42 99'),
    ("DEF FN restores parameter", [
        '10 X=2:DEF FNA(X)=X*X:PRINT FNA(3);X', 'RUN'], '9 2'),
    ("TI$ sets actual jiffy counter", ['TI$="010203"', 'PRINT TI'], '223380'),
    ("CONT after STOP", ['10 PRINT "A":STOP:PRINT "B"', 'RUN', 'CONT'], 'B'),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rom-dir', type=Path, default=ROOT/'build/roms')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    results = []
    for name, commands, expected in CASES:
        try:
            session = CpuBasicSession(args.rom_dir)
            transcript = [session.command(command, limit=5_000_000) for command in commands]
            actual = normalize(transcript[-1])
            record = dict(name=name, commands=commands, actual=actual, expected=expected,
                          passed=actual == expected, instructions=session.total_steps)
            if name == 'TI$ sets actual jiffy counter':
                clock = int.from_bytes(session.m.ram[0xA0:0xA3], 'big')
                record['jiffy_counter'] = clock
                record['passed'] &= clock == 223380
        except Exception as exc:
            record = dict(name=name, passed=False, error=str(exc))
        results.append(record)
    # A nested syntax error used to strand return addresses on the stack.
    try:
        session = CpuBasicSession(args.rom_dir)
        states = []
        for _ in range(80):
            result = normalize(session.command('PRINT ('))
            assert result == '?SYNTAX ERROR', result
            states.append(session.cpu.sp)
        value = normalize(session.command('PRINT 1+1'))
        assert len(set(states)) == 1 and value == '2'
        record = dict(name='80 consecutive errors recover the stack', passed=True,
                      stack_pointer=states[-1], final_output=value)
    except Exception as exc:
        record = dict(name='80 consecutive errors recover the stack', passed=False, error=str(exc))
    results.append(record)
    summary = dict(suite='BASIC memory and control-flow stress', total=len(results),
                   passed=sum(r['passed'] for r in results), results=results)
    print(json.dumps(summary, indent=2))
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(summary, indent=2)+'\n')
    return 0 if summary['passed'] == summary['total'] else 1


if __name__ == '__main__': sys.exit(main())
