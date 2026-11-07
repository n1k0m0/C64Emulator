#!/usr/bin/env python3
"""C64 BASIC RAM-vector, USR, STOP/CONT and input-error contracts.

Runs the actual interpreter through RESET and its command loop. Tiny RAM
programs chain through the saved public vectors. STOP and one lower serial
receiver are controlled test inputs; no BASIC handler is replaced by Python.

Interface references: Commodore 64 Programmer's Reference Guide, pp. 41,
86, 307-311. Original implementation code is not a source for these tests.
SPDX-License-Identifier: MIT
"""
from pathlib import Path
import argparse, json, sys
from boot_contract import CpuBasicSession, normalize

ROOT=Path(__file__).resolve().parents[2]


def word(session,address):
    return session.m[address] | session.m[address+1]<<8


def store_word(session,address,value):
    session.m[address]=value&255;session.m[address+1]=value>>8


def install_hook(session,slot):
    """A real ML hook preserves A/X/Y/P, counts calls, then chains to default."""
    original=word(session,slot)
    code=[0x08,0xee,0x00,0xc1,0x28,0x4c,original&255,original>>8]
    for i,b in enumerate(code):session.m[0xc000+i]=b
    store_word(session,slot,0xc000)
    entries=[];step=session.cpu.step
    def observe():
        c=session.cpu
        if c.pc==0xc000:
            entries.append({'a':c.a,'x':c.x,'y':c.y,'p':c.p,'text':word(session,0x7a)})
        return step()
    session.cpu.step=observe
    return entries


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--rom-dir',type=Path,default=ROOT/'build/roms')
    p.add_argument('--output',type=Path)
    p.add_argument('--filter',default='')
    args=p.parse_args();results=[]

    def session():return CpuBasicSession(args.rom_dir)
    def check(name,fn):
        if args.filter and args.filter.lower() not in name.lower():return
        try:record=dict(name=name,passed=True,detail=fn())
        except Exception as exc:record=dict(name=name,passed=False,error=str(exc))
        results.append(record)
        print(json.dumps(record),flush=True)

    def vector(slot,commands,expected,min_calls=1,required_a=None):
        s=session();entries=install_hook(s,slot)
        output=[normalize(s.command(command)) for command in commands]
        assert output[-1]==expected,output
        assert len(entries)>=min_calls,{'slot':hex(slot),'calls':len(entries),'minimum':min_calls}
        assert s.m[0xc100]==len(entries)%256
        if required_a is not None:assert required_a in [e['a'] for e in entries],entries
        return {'vector':hex(slot),'calls':len(entries),'output':output}
    check('ICRNCH tokenization vector',lambda:vector(0x304,['10 PRINT "HOOK"','RUN'],'HOOK'))
    check('IMAIN direct-mode input vector',lambda:vector(0x302,['A=7','PRINT A'],'7',2))
    check('IQPLOP character/token listing vector',lambda:vector(0x306,['10 PRINT "HOOK"','LIST'],'10 PRINT "HOOK"',5,0x99))
    check('IGONE per-statement dispatch vector',lambda:vector(0x308,['PRINT "A":PRINT "B"'],'A\nB',2))
    check('IEVAL single-term evaluation vector',lambda:vector(0x30a,['PRINT 2+3*4'],'14',3))

    def error_vector(command,code,expected):
        s=session();entries=install_hook(s,0x300)
        out=normalize(s.command(command))
        assert out==expected,out
        assert len(entries)==1,entries
        assert entries[0]['x']==code,entries
        assert normalize(s.command('PRINT 2'))=='2'
        return {'command':command,'error_number':entries[0]['x'],'output':out}
    for command,code,message in [('NEXT',10,'NEXT WITHOUT FOR'),('PRINT (',11,'SYNTAX'),
            ('RETURN',12,'RETURN WITHOUT GOSUB'),('READ A',13,'OUT OF DATA'),
            ('PRINT SQR(-1)',14,'ILLEGAL QUANTITY'),('PRINT 1E40',15,'OVERFLOW'),
            ('GOTO 123',17,"UNDEF'D STATEMENT"),('PRINT 1/0',20,'DIVISION BY ZERO'),
            ('INPUT A',21,'ILLEGAL DIRECT'),('A=CHR$(65)',22,'TYPE MISMATCH'),
            ('CONT',26,"CAN'T CONTINUE"),('PRINT FNA(1)',27,"UNDEF'D FUNCTION"),
            ('PRINT#1,"BAD"',3,'FILE NOT OPEN')]:
        check(f'IERROR {code}: {message}',lambda c=command,n=code,m=message:error_vector(c,n,'?'+m+' ERROR'))

    def usr(doubles=False):
        s=session()
        code=[0xe6,0x61,0x60] if doubles else [0x60]
        for i,b in enumerate(code):s.m[0xc200+i]=b
        assert s.m[0x310]==0x4c,hex(s.m[0x310])
        store_word(s,0x311,0xc200)
        command='PRINT USR(1.25);USR(-2.5)'
        out=normalize(s.command(command))
        assert out==('2.5 -5' if doubles else '1.25 -2.5'),out
        return {'command':command,'output':out,'usr_vector':word(s,0x311)}
    check('USR default error',lambda:error_vector('PRINT USR(1)',14,'?ILLEGAL QUANTITY ERROR'))
    check('USR floating argument and RTS return',usr)
    check('USR FAC result returned to BASIC',lambda:usr(True))

    def warm():
        s=session();ready_sp=s.cpu.sp
        s.command('10 PRINT "KEPT"');s.command('A=7')
        end=word(s,0x2d);program=bytes(s.m[0x801:end]);entries=install_hook(s,0x302)
        store_word(s,0x311,0xc200);s.m[0xc200]=0x60
        s.m[0x13]=8
        s.cpu.pc=word(s,0xa002)
        s._run(None,1_000_000)
        assert len(entries)==1,entries
        assert s.m[0x13]==0,s.m[0x13]
        assert bytes(s.m[0x801:end])==program
        assert normalize(s.command('PRINT A'))=='7'
        assert normalize(s.command('PRINT USR(3)'))=='3'
        assert s.cpu.sp==ready_sp,(s.cpu.sp,ready_sp)
        return {'program_preserved':True,'variables_preserved':True,'main_hook_calls':len(entries),'stack':s.cpu.sp}
    check('warm entry honors IMAIN and preserves BASIC RAM',warm)

    def commands(sequence,expected):
        s=session();output=[normalize(s.command(c)) for c in sequence]
        assert output[-1]==expected,output
        return {'commands':sequence,'output':output}
    check('CONT retains nested FOR and GOSUB frames',lambda:commands([
        '10 FOR I=1 TO 3:GOSUB 100:NEXT:END',
        '100 PRINT I;:IF I=2 THEN STOP','110 RETURN','RUN','LIST','CONT'],'3'))
    check('CONT after END resumes next statement',lambda:commands([
        '10 A=4:END:A=A+1:PRINT A','RUN','CONT'],'5'))
    check('error invalidates CONT',lambda:commands([
        '10 STOP:PRINT "BAD"','RUN','PRINT (','CONT'],"?CAN'T CONTINUE ERROR"))
    check('program edit invalidates CONT',lambda:commands([
        '10 STOP:PRINT "BAD"','RUN','10 STOP:PRINT "BAD"','CONT'],"?CAN'T CONTINUE ERROR"))
    check('RESTORE resets DATA without changing variables',lambda:commands([
        '10 DATA 7,"A:B"','20 READ A,B$:RESTORE:READ C,D$:PRINT A;B$;C;D$','RUN'],'7 A:B 7 A:B'))

    def stop_key(listing=False):
        s=session();step=s.cpu.step;pressed=[]
        if listing:
            for line in ('10 PRINT "A"','20 PRINT "B"'):s.command(line)
        else:
            for line in ('10 A=1','20 A=A+1','30 PRINT A'):s.command(line)
        def key_input():
            c=s.cpu
            if c.pc==0xffe1 and not pressed and (listing or word(s,s.labels['CURLIN'])==10):
                pressed.append(c.pc);c.a=0;c.p|=c.ZERO
                c.pc=(c.stPopWord()+1)&65535
                return c
            return step()
        s.cpu.step=key_input
        out=normalize(s.command('LIST' if listing else 'RUN'))
        assert pressed,'STOP was never polled'
        assert out==('BREAK' if listing else 'BREAK IN 10'),out
        continued=None
        if not listing:
            continued=normalize(s.command('CONT'))
            assert continued=='2',continued
        return {'output':out,'continued':continued}
    check('RUN/STOP key stops program and allows CONT',stop_key)
    check('RUN/STOP key interrupts LIST',lambda:stop_key(True))

    def input_error():
        s=session();step=s.cpu.step;incoming=list(b'ABC\r')
        def receive():
            c=s.cpu
            if c.pc==0xffc6:
                s.m[0x99]=8;c.p&=~c.CARRY;c.pc=(c.stPopWord()+1)&65535;return c
            if c.pc==s.labels['IEC_ACCEPT_BYTE'] and s.m[0x99]==8:
                assert incoming,'INPUT# read beyond record'
                c.a=incoming.pop(0);c.x=0x30;c.y=0xff;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535;return c
            if c.pc==0xffcc and s.m[0x99]==8:
                s.m[0x99]=0;s.m[0x9a]=3;c.a=0;c.x=3;c.p|=c.CARRY
                c.pc=(c.stPopWord()+1)&65535;return c
            return step()
        s.cpu.step=receive
        s.command('10 INPUT#2,A')
        out=normalize(s.command('RUN'))
        assert out=='?FILE DATA ERROR IN 10',out
        assert normalize(s.command('PRINT 4'))=='4'
        return {'output':out,'input_channel':s.m[0x99],'output_channel':s.m[0x9a]}
    check('INPUT# numeric type error survives CLRCHN clobber',input_error)

    summary={'suite':__doc__.splitlines()[0],'passed':sum(r['passed'] for r in results),
             'total':len(results),'results':results}
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True)
        args.output.write_text(json.dumps(summary,indent=2)+'\n')
    print(f"{summary['passed']}/{summary['total']} passed")
    return 0 if summary['passed']==summary['total'] else 1


if __name__=='__main__':sys.exit(main())
