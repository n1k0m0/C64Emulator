#!/usr/bin/env python3
"""Semantic BASIC regressions from RESET through the real ROM input loop.

This is a documented-6502 CPU harness with minimal idle CIA registers. It
isolates BASIC/control-flow behavior; it is not VIC/CIA/IEC timing validation.
Use tests/emulator for actual C64Emulator core integration.
SPDX-License-Identifier: Apache-2.0
"""
from pathlib import Path
from collections import deque
import argparse, json, re, sys

ROOT = Path(__file__).resolve().parents[2]
for vendor in (ROOT/'vendor', ROOT.parent/'rom_review/vendor'):
    if (vendor/'py65').is_dir():
        sys.path.insert(0,str(vendor)); break
from py65.devices.mpu6502 import MPU


class Memory:
    def __init__(self, roms):
        self.ram=bytearray(65536)
        self.ram[1]=0x37
        self.basic=(roms/'open-basic64.bin').read_bytes()
        self.kernal=(roms/'open-kernal64.bin').read_bytes()
        self.irq_pending=0
    def __getitem__(self, address):
        if isinstance(address,slice):
            return [self[i] for i in range(*address.indices(65536))]
        a=address&65535
        if 0xa000<=a<0xc000 and self.ram[1]&3==3:return self.basic[a-0xa000]
        if a>=0xe000 and self.ram[1]&2:return self.kernal[a-0xe000]
        if a==0xdc01:return 255
        if a==0xdc0d:
            pending,self.irq_pending=self.irq_pending,0
            return pending
        if a==0xdd0d:return 0
        return self.ram[a]
    def __setitem__(self,a,v):self.ram[a&65535]=v&255


class CpuBasicSession:
    def __init__(self,roms,irq_period=None):
        self.labels={}
        for file in ('open-basic64.map','open-kernal64.map'):
            for line in (roms/file).read_text().splitlines():
                addr,name=line.split();self.labels[name]=int(addr,16)
        self.m=Memory(roms)
        self.cpu=MPU(memory=self.m,pc=None)
        self.irq_period=irq_period
        self.next_irq=irq_period
        self.total_steps=0
        self.trace=deque(maxlen=16)
        self.boot_output=self._run(None,1_000_000)

    def _run(self,command,limit):
        pending=bytearray((command+'\r').encode('latin1')) if command is not None else bytearray()
        output=[];injected=False
        for steps in range(limit):
            pc=self.cpu.pc
            # STR$ deliberately redirects the writable CHROUT vector to its
            # private capture buffer; those bytes are not screen output.
            vector=self.m[0x326] | (self.m[0x327]<<8)
            if pc==0xffd2 and vector!=self.labels.get('FP_STR_CAPTURE'):
                output.append(self.cpu.a)
            if pc==self.labels['LINE_WAIT']:
                if command is None:return bytes(output).decode('latin1')
                if pending:
                    count=min(10-self.m[0xc6],len(pending))
                    for b in pending[:count]:
                        self.m[0x277+self.m[0xc6]]=b;self.m[0xc6]+=1
                    del pending[:count];injected=True
                # LINE_WAIT is already the keyboard collection boundary.
                # Do not assume a private editor-state byte is zero: the
                # screen-backed editor marks active collection with $80.
                elif injected and self.m[0xc6]==0:
                    return bytes(output).decode('latin1')
            if self.cpu.disassemble[self.m[pc]][0]=='???':
                raise RuntimeError(f'Unsupported opcode ${self.m[pc]:02X} at ${pc:04X}')
            if self.irq_period and self.cpu.processorCycles>=self.next_irq:
                self.next_irq+=self.irq_period;self.m.irq_pending=0x81
                if not self.cpu.p&self.cpu.INTERRUPT:self.cpu.irq()
            self.trace.append(f'{pc:04X}:{self.cpu.sp:02X}:{self.cpu.a:02X}')
            self.cpu.step();self.total_steps+=1
        raise RuntimeError(f'CPU instruction limit at ${self.cpu.pc:04X}, output={bytes(output)!r}, trace={list(self.trace)}')

    def command(self,text,limit=2_000_000):
        output=self._run(text,limit)
        return output.rsplit('READY.',1)[0].strip('\r\n')


def normalize(value):
    # Normalize captured output from both old CR/LF and current CR records.
    # Cursor-right ($1D) occupies one column in printed numeric spacing.
    value=value.replace('\r\n','\n').replace('\r','\n').replace('\x1d',' ')
    return re.sub(r'[ \t]+',' ',value).strip()

# A sequence is one program/session; all preceding commands must complete
# without an error, and the last command is compared to the expected output.
CASES=[
    ('arithmetic',['PRINT 2+3*4'],'14'),
    ('decimal equality',['PRINT 0.1=0.1'],'-1'),
    ('decimal greater',['PRINT 0.1>0.1'],'0'),
    ('decimal subtraction',['PRINT 0.1-0.1'],'0'),
    ('positive INT',['PRINT INT(1.1)=1'],'-1'),
    ('negative INT',['PRINT INT(-1.1)+2'],'0'),
    ('signed logic',['PRINT (-1 AND 3);(1 OR 2);NOT 0'],'3 3 -1'),
    ('logic positive overflow',['PRINT 32768 OR 0'],'?ILLEGAL QUANTITY ERROR'),
    ('logic high overflow',['PRINT 65536 AND 1'],'?ILLEGAL QUANTITY ERROR'),
    ('division by zero',['PRINT 1/0'],'?DIVISION BY ZERO ERROR'),
    ('square root domain',['PRINT SQR(-1)'],'?ILLEGAL QUANTITY ERROR'),
    ('power integer',['PRINT 2^10'],'1024'),
    ('power negative base',['PRINT (-2)^3'],'-8'),
    ('power reciprocal',['PRINT 2^-3'],'.125'),
    ('power zero',['PRINT 0^0'],'1'),
    ('power fraction',['PRINT 9^.5'],'3'),
    ('power negative fraction',['PRINT (-2)^.5'],'?ILLEGAL QUANTITY ERROR'),
    ('remark',['REM HELLO'],'') ,
    ('variable assignment',['A=1.25','PRINT A+2'],'3.25'),
    ('integer variable',['A%=12.5','PRINT A%'],'12'),
    ('LEFT$',['PRINT LEFT$("ABCDE",2)'],'AB'),
    ('RIGHT$',['PRINT RIGHT$("ABCDE",2)'],'DE'),
    ('MID$ two arguments',['PRINT MID$("ABCDE",3)'],'CDE'),
    ('MID$ three arguments',['PRINT MID$("ABCDE",2,3)'],'BCD'),
    ('string variable',['A$="HELLO"','B$=A$','PRINT B$'],'HELLO'),
    ('string concatenation',['A$="AB"+"CD"','PRINT A$'],'ABCD'),
    ('string nested functions',['PRINT LEFT$(RIGHT$("ABCDE",4),2)'],'BC'),
    ('STR$ concatenation',['PRINT STR$(12)+STR$(34)'],'12 34'),
    ('CHR$ assignment',['A$=CHR$(65)','PRINT A$'],'A'),
    ('LEN expression',['PRINT LEN("A"+"BC")'],'3'),
    ('ASC expression',['PRINT ASC(RIGHT$("ABC",1))'],'67'),
    ('VAL expression',['PRINT VAL(MID$("X123",2))'],'123'),
    ('CHR$ range',['PRINT CHR$(256)'],'?ILLEGAL QUANTITY ERROR'),
    ('string equality',['PRINT "ABC"="ABC"'],'-1'),
    ('string ordering',['PRINT "ABC"<"ABD"'],'-1'),
    ('FOR integer',['10 FOR I=1 TO 3:PRINT I;:NEXT','RUN'],'1 2 3'),
    ('FOR fractional',['10 FOR I=.1 TO .3 STEP .1:PRINT I;:NEXT','RUN'],'.1 .2 .3'),
    ('FOR variable assignment',['10 FOR I=1 TO 3:A=I*2:PRINT A;:NEXT','RUN'],'2 4 6'),
    ('GOSUB RETURN',['10 GOSUB 100:PRINT "DONE":END','100 PRINT "SUB":RETURN','RUN'],'SUB\nDONE'),
    ('IF THEN',['10 IF 1 THEN PRINT "YES"','RUN'],'YES'),
    ('IF false',['10 IF 0 THEN PRINT "NO"','RUN'],'') ,
    ('IF comparison',['10 A=.1:IF A=.1 THEN PRINT "YES"','RUN'],'YES'),
    ('ON GOTO',['10 ON 2 GOTO 100,200','100 PRINT "BAD":END','200 PRINT "OK":END','RUN'],'OK'),
    ('GO TO',['10 GO TO 30','20 PRINT "BAD":END','30 PRINT "OK"','RUN'],'OK'),
    ('DATA READ',['10 DATA 12,34','20 READ A,B:PRINT A;B','RUN'],'12 34'),
    ('quoted DATA colon',['10 DATA "A:B",PRINT','20 READ A$,B$:PRINT A$;B$','RUN'],'A:BPRINT'),
    ('DATA RESTORE',['10 DATA 7','20 READ A:RESTORE:READ B:PRINT A;B','RUN'],'7 7'),
    ('DIM numeric',['DIM A(2)','A(2)=42','PRINT A(2)'],'42'),
    ('DIM multidimensional',['DIM A(2,3)','A(2,3)=42','PRINT A(2,3)'],'42'),
    ('implicit array',['A(5)=23','PRINT A(5)'],'23'),
    ('DIM string',['DIM A$(2)','A$(1)="ABC"','PRINT A$(1)'],'ABC'),
    ('array subscript range',['DIM A(2)','PRINT A(3)'],'?BAD SUBSCRIPT ERROR'),
    ('DEF FN',['10 DEF FNA(X)=X*X+1','20 PRINT FNA(3)','RUN'],'10'),
    ('PEEK POKE',['POKE 49152,42','PRINT PEEK(49152)'],'42'),
    ('SGN ABS',['PRINT SGN(-2);ABS(-3)'],'-1 3'),
    # BASIC V2 reports FRE as a signed 16-bit value; ~38 KiB appears negative.
    ('FRE signed result',['PRINT FRE(0)>0'],'0'),
    ('POS',['PRINT POS(0)>=0'],'-1'),
    ('TI numeric',['PRINT TI>=0'],'-1'),
    ('TI$ assignment',['TI$="000100"','PRINT TI$'],'000100'),
    ('SPC',['PRINT "A";SPC(3);"B"'],'A B'),
    ('TAB',['PRINT "A";TAB(4);"B"'],'A B'),
    ('GET empty string',['10 GET A$:PRINT LEN(A$)','RUN'],'0'),
    ('CLR variables',['A=99','CLR','PRINT A'],'0'),
]


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--rom-dir',type=Path,default=ROOT/'build/roms')
    p.add_argument('--output',type=Path)
    p.add_argument('--filter',default='')
    args=p.parse_args();results=[]
    for name,commands,expected in CASES:
        if args.filter and args.filter.lower() not in name.lower():continue
        record={'name':name,'commands':commands,'expected':expected}
        try:
            s=CpuBasicSession(args.rom_dir)
            transcript=[s.command(c) for c in commands]
            prior_errors=[(cmd,out) for cmd,out in zip(commands[:-1],transcript[:-1]) if '?' in out]
            actual=normalize(transcript[-1])
            record.update(actual=actual,pass_=actual==expected and not prior_errors,prior_errors=prior_errors,
                          steps=s.total_steps,sp=s.cpu.sp)
        except Exception as exc:
            record.update(pass_=False,error=str(exc))
        results.append(record)
        print(json.dumps(record,ensure_ascii=False),flush=True)
    summary={'suite':'BASIC semantics through RESET/input loop; isolated CPU, not full C64',
             'passed':sum(r['pass_'] for r in results),'total':len(results),'results':results}
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True)
        args.output.write_text(json.dumps(summary,indent=2,ensure_ascii=False)+'\n')
    print(f"{summary['passed']}/{summary['total']} passed")
    return 0 if summary['passed']==summary['total'] else 1

if __name__=='__main__':sys.exit(main())
