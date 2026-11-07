#!/usr/bin/env python3
"""BASIC I/O adapter contract tests through RESET and BASIC command parsing.

The four KERNAL file operations are spies, not disk/tape emulation. All
expression parsing, temporary-string handling, error recovery, program
relinking and SYS execution run as actual 6502 code. Native IEC integration
is tested separately by tests/emulator.

LOAD/chain contracts: Commodore 64 Programmer's Reference Guide, LOAD entry;
Jim Butterfield, COMPUTE! March 1986, "Loading and Linking Commodore Programs":
https://www.atarimagazines.com/compute/issue70/088_1_Loading_And_Linking_Commodore_Programs.php
Chaining retains variable storage at its existing addresses. A larger incoming
program can overwrite it; these tests reserve enough room and do not assume
automatic variable relocation.

PRINT# record contracts: Commodore 64 Programmer's Reference Guide, p. 349
(all carriage returns gain LF when the logical file number exceeds 127), and
Wiesemann & Theis C64-Parallelinterface 6.0, section 5.2.2 (ordinary CR output).
https://www.devili.iki.fi/Computers/Commodore/C64/Programmers_Reference/Chapter_6/page_349.html
https://www.wut.de/download/manual/e-92000-ww-prde-000.pdf
SPDX-License-Identifier: MIT
"""
from pathlib import Path
import argparse, json, sys

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]
from boot_contract import CpuBasicSession, normalize


class SpySession(CpuBasicSession):
    def __init__(self,roms):
        super().__init__(roms)
        self.ready_sp=self.cpu.sp
        self.calls=[]
        self.error=0
        self.payload=None
        self.real_step=self.cpu.step
        self.cpu.step=self.step

    def word(self,a):return self.m[a]|self.m[a+1]<<8
    def step(self):
        names={0xffd5:'LOAD',0xffd8:'SAVE',0xffc0:'OPEN',0xffc3:'CLOSE'}
        c=self.cpu
        if c.pc not in names:return self.real_step()
        start=self.word(c.a)
        nameptr=self.word(0xbb)
        record=dict(op=names[c.pc],a=c.a,x=c.x,y=c.y,lfn=self.m[0xb8],
                    device=self.m[0xba],secondary=self.m[0xb9],
                    filename=bytes(self.m[nameptr:nameptr+self.m[0xb7]]).decode('latin1'))
        self.calls.append(record)
        if record['op']=='SAVE':
            record['start']=start;record['end']=c.x|c.y<<8
            record['bytes']=list(self.m[start:record['end']])
        if record['op']=='LOAD' and not self.error:
            target=c.x|c.y<<8
            if self.payload is not None:
                data=self.payload
                if self.m[0xb9]:target=data[0]|data[1]<<8
                data=data[2:]
                if not c.a:
                    for offset,b in enumerate(data):self.m[target+offset]=b
                end=target+len(data)
            else:end=self.word(self.labels['VARTAB'])
            c.x=end&255;c.y=end>>8
        c.p=(c.p&~c.CARRY)|(c.CARRY if self.error else 0)
        c.a=self.error
        c.pc=(c.stPopWord()+1)&65535
        return c


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--rom-dir',type=Path,default=ROOT/'build/roms')
    p.add_argument('--output',type=Path,default=ROOT/'build/io-contract.json')
    args=p.parse_args();results=[]

    def check(name,fn):
        try:
            detail=fn()
            record=dict(name=name,pass_=True,detail=detail)
        except Exception as exc:record=dict(name=name,pass_=False,error=str(exc))
        results.append(record);print(json.dumps(record),flush=True)

    def contract(command,op,expected,setup=()):
        s=SpySession(args.rom_dir)
        for line in setup:
            out=s.command(line)
            assert '?' not in out,(line,out)
        out=s.command(command)
        assert '?' not in out,out
        assert len(s.calls)==1,s.calls
        call=s.calls[0];assert call['op']==op,call
        for k,v in expected.items():assert call[k]==v,(k,call,expected)
        assert s.cpu.sp==s.ready_sp,hex(s.cpu.sp)
        return {'command':command,'call':call,'output':normalize(out),'sp':s.cpu.sp}

    for command,op,expected in [
        ('SAVE','SAVE',dict(lfn=1,device=1,secondary=0,filename='',start=0x801)),
        ('SAVE "FILE",8','SAVE',dict(lfn=1,device=8,secondary=0,filename='FILE',start=0x801)),
        ('SAVE "FILE",1,3','SAVE',dict(device=1,secondary=3,filename='FILE')),
        ('LOAD','LOAD',dict(a=0,device=1,secondary=0,filename='',x=1,y=8)),
        ('LOAD "FILE",8','LOAD',dict(a=0,device=8,secondary=0,filename='FILE')),
        ('LOAD "ML",8,1','LOAD',dict(a=0,device=8,secondary=1,filename='ML')),
        ('VERIFY "FILE",8','LOAD',dict(a=1,device=8,secondary=0,filename='FILE')),
        ('VERIFY "ML",8,1','LOAD',dict(a=1,device=8,secondary=1,filename='ML')),
        ('OPEN 2','OPEN',dict(lfn=2,device=1,secondary=0,filename='')),
        ('OPEN 3,8','OPEN',dict(lfn=3,device=8,secondary=0,filename='')),
        ('OPEN 4,8,4','OPEN',dict(lfn=4,device=8,secondary=4,filename='')),
        ('OPEN 15,8,15,"I"','OPEN',dict(lfn=15,device=8,secondary=15,filename='I')),
        ('CLOSE 15','CLOSE',dict(a=15)),
        ('SAVE "F"+CHR$(65),VAL("8"+""),LEN("A"+"B")-2','SAVE',dict(device=8,secondary=0,filename='FA')),
        ('OPEN 2+1,4*2,7-4,"P"+CHR$(82)+"G"','OPEN',dict(lfn=3,device=8,secondary=3,filename='PRG')),
    ]:
        check(command,lambda c=command,o=op,e=expected:contract(c,o,e))

    def save_program():
        s=SpySession(args.rom_dir)
        s.command('10 PRINT "SAVED"')
        start=s.word(s.labels['TXTTAB']);end=s.word(s.labels['VARTAB'])
        expected=list(s.m[start:end])
        s.command('SAVE "TEST",8')
        call=s.calls[-1]
        assert call['start']==start and call['end']==end,call
        assert call['bytes']==expected,call
        return {'start':start,'end':end,'bytes':expected}
    check('SAVE exact tokenized program bounds',save_program)

    def load_program(in_program=False):
        source=SpySession(args.rom_dir);source.command('10 PRINT "LOADED"')
        start=source.word(source.labels['TXTTAB']);end=source.word(source.labels['VARTAB'])
        payload=bytes([start&255,start>>8]+source.m[start:end])
        s=SpySession(args.rom_dir);s.payload=payload
        s.command('A=99')
        if in_program:
            s.command('10 A=99:LOAD "TEST",8');s.command('20 PRINT "OLD"')
            variable_start=s.word(s.labels['VARTAB'])
            out=normalize(s.command('RUN'))
            assert out=='LOADED',out
        else:
            out=normalize(s.command('LOAD "TEST",8'))
            assert not out,out
            assert normalize(s.command('RUN'))=='LOADED'
        assert s.word(s.labels['VARTAB'])==(variable_start if in_program else end)
        assert normalize(s.command('PRINT A'))==('99' if in_program else '0')
        assert s.cpu.sp==s.ready_sp
        return {'chained':in_program,'output':out,'end':end}
    check('LOAD in direct mode relinks without running',load_program)
    check('LOAD in program mode chains and keeps variables',lambda:load_program(True))

    def verify_preserve():
        s=SpySession(args.rom_dir);s.command('10 PRINT "KEEP"');s.command('A=7')
        end=s.word(s.labels['VARTAB'])
        s.payload=bytes([1,8,0,0])
        s.command('VERIFY "TEST",8')
        assert s.word(s.labels['VARTAB'])==end
        assert normalize(s.command('PRINT A'))=='7'
        assert normalize(s.command('RUN'))=='KEEP'
        return {'vartab':end,'mode':s.calls[0]['a']}
    check('VERIFY preserves program and variables',verify_preserve)

    def absolute_load():
        s=SpySession(args.rom_dir);s.command('10 PRINT "KEEP"');s.command('A=7')
        s.payload=bytes([0,0xc0,0x60,0x42])
        s.command('LOAD "ML",8,1')
        assert list(s.m[0xc000:0xc002])==[0x60,0x42]
        # C64 direct LOAD changes start-of-variables even for an absolute ML
        # load. NEW is required before using BASIC variables above MEMSIZ.
        assert s.word(s.labels['VARTAB'])==0xc002
        assert s.word(s.labels['ARYTAB'])==0xc002
        s.command('NEW')
        assert normalize(s.command('PRINT A'))=='0'
        return {'load_end':0xc002,'bytes':list(s.m[0xc000:0xc002])}
    check('absolute direct LOAD updates BASIC end and clears variables',absolute_load)

    def basic_absolute_load():
        source=SpySession(args.rom_dir);source.command('10 A=5:PRINT A')
        end=source.word(source.labels['VARTAB'])
        s=SpySession(args.rom_dir);s.payload=bytes([1,8]+source.m[0x801:end])
        s.command('A=99')
        assert not normalize(s.command('LOAD "TEST",8,1'))
        assert s.word(s.labels['VARTAB'])==end
        assert normalize(s.command('RUN'))=='5'
        return {'program_end':end,'output':'5'}
    check('absolute direct BASIC LOAD supports variables',basic_absolute_load)

    def chain_state():
        source=SpySession(args.rom_dir)
        source.command('10 DATA 7')
        source.command('20 READ C:PRINT A;A$;B(1);C')
        end=source.word(source.labels['VARTAB'])
        s=SpySession(args.rom_dir);s.payload=bytes([1,8]+source.m[0x801:end])
        s.command('10 A=99:A$="KE"+"PT":DIM B(1):B(1)=8')
        s.command('20 READ D:LOAD "TEST",8')
        s.command('30 DATA 123')
        old_variables=s.word(s.labels['VARTAB'])
        assert old_variables>end,'Loader must reserve enough space for chained code'
        out=normalize(s.command('RUN'))
        assert out=='99 KEPT 8 7',out
        assert s.word(s.labels['VARTAB'])==old_variables
        return {'output':out,'variables_begin':old_variables,'new_program_end':end}
    check('chained LOAD keeps scalar string array state and resets DATA',chain_state)

    def absolute_program_restart():
        s=SpySession(args.rom_dir);s.payload=bytes([0,0xc0,42])
        s.command('10 IF A=1 THEN PRINT PEEK(49152):END')
        s.command('20 A=1:LOAD "ML",8,1')
        s.command('30 PRINT "BAD"')
        out=normalize(s.command('RUN'))
        assert out=='42',out
        assert len(s.calls)==1,s.calls
        return {'output':out,'loads':len(s.calls),'loaded_byte':s.m[0xc000]}
    check('absolute program LOAD restarts loader preserving state',absolute_program_restart)

    def error_recover(code,message):
        s=SpySession(args.rom_dir);s.error=code
        for _ in range(3):
            out=normalize(s.command('LOAD "FAIL",8'))
            assert out==f'?{message} ERROR',out
            assert s.cpu.sp==s.ready_sp,hex(s.cpu.sp)
        assert normalize(s.command('PRINT 2+2'))=='4'
        return {'code':code,'output':out,'sp':s.cpu.sp}
    for code,message in [(1,'TOO MANY FILES'),(2,'FILE OPEN'),(3,'NOT OPEN'),(4,'FILE NOT FOUND'),(5,'DEVICE NOT PRESENT'),(6,'NO INPUT'),(7,'NO OUTPUT FILE'),(8,'MISSING FILE'),(9,'BAD DEVICE'),(0x1c,'VERIFY'),(0x1d,'LOAD'),(0x7f,'I/O')]:
        check(f'I/O error {code}',lambda c=code,m=message:error_recover(c,m))

    def syntax(command,message):
        s=SpySession(args.rom_dir);out=normalize(s.command(command))
        assert out==f'?{message} ERROR',out
        assert not s.calls,s.calls
        assert s.cpu.sp==s.ready_sp,hex(s.cpu.sp)
        return {'command':command,'output':out}
    for command,message in [('SAVE 123','TYPE MISMATCH'),('LOAD "X",256','ILLEGAL QUANTITY'),('OPEN 256,8','ILLEGAL QUANTITY'),('CLOSE 1,2','SYNTAX'),('SAVE "X",8,1,2','SYNTAX'),('SYS -1','ILLEGAL QUANTITY'),('SYS 65536','ILLEGAL QUANTITY')]:
        check(command,lambda c=command,m=message:syntax(c,m))

    check('PRINT# unopened channel',lambda:syntax('PRINT#1,"HELLO"','NOT OPEN'))

    def unopened_input(statement):
        s=SpySession(args.rom_dir)
        s.command('10 '+statement)
        out=normalize(s.command('RUN'))
        assert out=='?NOT OPEN ERROR IN 10',out
        assert s.cpu.sp==s.ready_sp,hex(s.cpu.sp)
        assert normalize(s.command('PRINT 4'))=='4'
        return {'statement':statement,'output':out,'sp':s.cpu.sp}
    check('GET# unopened channel',lambda:unopened_input('GET#1,A$'))
    check('INPUT# unopened channel',lambda:unopened_input('INPUT#1,A$'))

    def output_token_preserved():
        s=SpySession(args.rom_dir);step=s.cpu.step
        def select_output():
            c=s.cpu
            if c.pc!=0xffc9:return step()
            # A/X are caller data needed by the PET core after channel select.
            c.a=0xee;c.x=0;c.p&=~c.CARRY
            c.pc=(c.stPopWord()+1)&65535
            return c
        s.cpu.step=select_output
        out=s.command('PRINT#2,SPC(3);"A"')
        assert '?' not in out and normalize(out)=='A',out
        assert out.startswith('   A'),repr(out)
        assert s.cpu.sp==s.ready_sp
        return {'command':'PRINT#2,SPC(3);"A"','output':out,'sp':s.cpu.sp}
    check('CHKOUT preserves first PRINT# token',output_token_preserved)

    def input_channel_number_preserved():
        s=SpySession(args.rom_dir);step=s.cpu.step;channel=[]
        def select_input():
            c=s.cpu
            if c.pc==0xffc6:
                c.a=0xee;c.x=0x30;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            if c.pc==0xffe4:
                channel.append(s.m[s.labels['CHANNL']]);c.a=65;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            return step()
        s.cpu.step=select_input
        s.command('10 GET#2,A$:PRINT A$')
        out=normalize(s.command('RUN'))
        assert out=='A',out
        assert channel==[2],channel
        return {'channels_seen':channel,'output':out}
    check('CHKIN preserves logical file number',input_channel_number_preserved)

    def serial_input_buffer_index():
        s=SpySession(args.rom_dir);step=s.cpu.step
        incoming=list(b'123,ABC\r');returned=[]
        def serial_input():
            c=s.cpu
            if c.pc==0xffc6:
                s.m[0x99]=8;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            if c.pc==s.labels['IEC_ACCEPT_BYTE'] and s.m[0x99]==8:
                assert incoming,'INPUT# read beyond the supplied complete line'
                c.a=incoming.pop(0);returned.append(c.a)
                # The lower IEC bit receiver uses these indexes. Public
                # CHRIN must preserve its caller's BASIC buffer index in X.
                c.x=0x30;c.y=0xff;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            if c.pc==0xffcc and s.m[0x99]==8:
                s.m[0x99]=0;s.m[0x9a]=3;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            return step()
        s.cpu.step=serial_input
        s.command('10 INPUT#2,A,B$:PRINT A;":";B$')
        out=normalize(s.command('RUN'))
        assert out.replace(' ','')=='123:ABC',out
        assert not incoming,incoming
        assert s.cpu.sp==s.ready_sp
        return {'serial_bytes':returned,'output':out,'sp':s.cpu.sp}
    check('INPUT# preserves buffer index across serial receiver',serial_input_buffer_index)

    def print_record(lfn,tail,expected):
        s=SpySession(args.rom_dir);step=s.cpu.step;written=[]
        def channel_output():
            c=s.cpu
            if c.pc==0xffc9:
                c.a=0xee;c.x=0;c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            if c.pc==0xffd2 and s.m[s.labels['CHANNL']]==lfn:
                written.append(c.a);c.p&=~c.CARRY
                c.pc=(c.stPopWord()+1)&65535
                return c
            return step()
        s.cpu.step=channel_output
        command=f'PRINT#{lfn},'+tail
        out=normalize(s.command(command))
        assert '?' not in out,out
        assert written==list(expected),(written,list(expected))
        assert s.cpu.sp==s.ready_sp
        return {'command':command,'bytes':written,'hex':bytes(written).hex()}
    for lfn in (2,127,128,130):
        expected=b'ABC\r'+(b'\n' if lfn>=128 else b'')
        check(f'PRINT# {lfn} record terminator',lambda n=lfn,e=expected:print_record(n,'"ABC"',e))
    for lfn in (2,130):
        check(f'PRINT# {lfn} trailing semicolon',lambda n=lfn:print_record(n,'"ABC";',b'ABC'))
        expected=b'A\r'+(b'\n' if lfn>=128 else b'')+b'B'
        check(f'PRINT# {lfn} explicit CHR$(13)',lambda n=lfn,e=expected:print_record(n,'"A";CHR$(13);"B";',e))

    def high_file_registers():
        s=SpySession(args.rom_dir);step=s.cpu.step;written=[]
        def character_output():
            c=s.cpu
            if c.pc!=0xffd2:return step()
            written.append(c.a);c.p&=~c.CARRY
            c.pc=(c.stPopWord()+1)&65535
            return c
        s.cpu.step=character_output
        s.m[s.labels['CHANNL']]=130
        entry=s.labels['BASIC_CHROUT']
        s.m.ram[0xc800:0xc803]=bytes([0x20,entry&255,entry>>8])
        c=s.cpu;c.pc=0xc800;c.a=13;c.x=0x5a;c.y=0xa5;c.sp=0xfd
        for _ in range(100):
            c.step()
            if c.pc==0xc803:break
        assert c.pc==0xc803,hex(c.pc)
        assert written==[13,10],written
        assert (c.a,c.x,c.y,c.sp)==(13,0x5a,0xa5,0xfd),(c.a,c.x,c.y,c.sp)
        return {'bytes':written,'a':c.a,'x':c.x,'y':c.y,'sp':c.sp}
    check('high logical file CR preserves output registers',high_file_registers)

    def sys_registers():
        s=SpySession(args.rom_dir)
        # Store entry A/X/Y/P at $C100..3, then return fresh registers/P.
        code=bytes([0x8d,0x00,0xc1,0x8e,0x01,0xc1,0x8c,0x02,0xc1,0x08,0x68,0x8d,0x03,0xc1,
                    0xa9,0x42,0xa2,0x24,0xa0,0x18,0x38,0x60])
        for i,b in enumerate(code):s.m[0xc000+i]=b
        for i,b in enumerate([0x11,0x22,0x33,0x20]):s.m[0x30c+i]=b
        out=normalize(s.command('SYS 49152:PRINT 7'))
        assert out=='7',out
        assert list(s.m[0xc100:0xc103])==[0x11,0x22,0x33]
        assert s.m[0xc103]&1==0
        assert list(s.m[0x30c:0x30f])==[0x42,0x24,0x18]
        assert s.m[0x30f]&1
        assert s.cpu.sp==s.ready_sp,hex(s.cpu.sp)
        return {'entry':list(s.m[0xc100:0xc104]),'return':list(s.m[0x30c:0x310]),'output':out}
    check('SYS register exchange and colon continuation',sys_registers)

    summary={'suite':__doc__.splitlines()[0],'passed':sum(r['pass_'] for r in results),'total':len(results),'results':results}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(summary,indent=2)+'\n')
    print(f"{summary['passed']}/{summary['total']} passed")
    return 0 if summary['passed']==summary['total'] else 1


if __name__=='__main__':sys.exit(main())
