// SPDX-License-Identifier: Apache-2.0
// Independent physical-pin RS232 peer. Generated RAM fixtures call public KERNAL
// entries; no ROM, CIA timer, CPU instruction or serial routine is substituted.
using C64Emulator.Core;
using System.Reflection;
using System.Text;

static class NativeSerialRc3Tests
{
    public static void Run(Session session, Action<string, bool, string, string> check)
    {
#if TAPE_SERIAL_RC3_PATCH
        var port = session.C64.UserPort;
        var oldTicked = port.Ticked;
        var oldChanged = port.OutputChanged;
        try
        {
            // CINT makes the fixture code area independent of preceding relocated
            // editor tests; the screen stays visible and timer IRQs stay enabled.
            session.Command("SYS65409", 100_000);
            foreach (var rate in new[] { (Code: 1, Baud: 50), (Code: 6, Baud: 300), (Code: 8, Baud: 1200), (Code: 10, Baud: 2400) })
            {
                var tx = Probe(session, (byte)rate.Code, 0, rate.Baud, Mode.Tx, Peer.Ready);
                bool framing = DecodeTx(tx, 0x55, 8, 0, 1, out string waveform);
                Check("tx_8n1_" + rate.Baud, tx.Done && tx.Call(0).Ok && tx.Call(1).Ok && tx.Call(2).Ok && framing &&
                    tx.Call(2).Registers(0x37,0x81) && tx.CloseReleased && tx.OpenDirection == 6 && !tx.OpenDtr,
                    "8N1 physical PA2 frame decodes at model clock/baud; OPEN DDRB=$06; IRQ/X/Y preserved; CLOSE releases all outputs", tx, waveform);
                var rx = Probe(session, (byte)rate.Code, 0, rate.Baud, Mode.Rx, Peer.Ready);
                Check("rx_8n1_" + rate.Baud, rx.Done && rx.Call(0).Ok && rx.Call(1).Ok && rx.Call(2).Ok && rx.Call(2).A == 0xA6 &&
                    rx.Call(2).Registers(0x37,0x81) && rx.Call(4).A == 0 && rx.Call(5).A == 0 && rx.CloseReleased,
                    "Independent nominal-baud RX waveform delivers $A6 through CHRIN with IRQs initially enabled and X/Y unchanged", rx);
            }

            var delayed = Probe(session, 6, 1, 300, Mode.Tx, Peer.DelayedCts);
            bool delayedFrame = DecodeTx(delayed,0x55,8,0,1,out string delayedWave);
            Check("cts_delayed", delayed.Done && delayed.Call(1).Ok && delayed.Call(2).Ok && delayedFrame && delayed.OpenDtr &&
                delayed.FirstTxCycle >= delayed.CtsReleaseCycle && delayed.CtsReleaseCycle >= delayed.Stage(2)+50_000 && delayed.SawRtsAsserted && delayed.CloseReleased,
                "X-line CHKOUT asserts RTS/DTR and transmits only after peer asserts CTS 50000 cycles later", delayed, delayedWave);

            var cts = Probe(session,6,1,300,Mode.Tx,Peer.CtsTimeout);
            Check("cts_timeout_no_tx", cts.Done && cts.Call(1).GateError(0x10) && cts.Call(1).Registers(2,0x81) &&
                cts.TxEdges.Count==0 && cts.Call(4).A==0x10 && cts.Call(5).A==0 && cts.WaitSeconds(2,4)>=0.5 && cts.WaitSeconds(2,4)<=2.6 && cts.CloseReleased,
                "Missing CTS gives bounded 0.5–2.6-second wait, Carry/A5 and RSSTAT$10; no TX start; READST acknowledges error",cts);

            var dsr = Probe(session,6,1,300,Mode.Tx,Peer.DsrLostAfterSelect);
            Check("dsr_loss_before_tx",dsr.Done&&dsr.Call(1).Ok&&dsr.Call(2).GateError(0x40)&&dsr.Call(2).Registers(0x37,0x81)&&
                dsr.TxEdges.Count==0&&dsr.Stage(4)-dsr.Stage(3)<20_000&&dsr.Call(4).A==0x40&&dsr.Call(5).A==0&&dsr.CloseReleased,
                "DSR loss after successful CHKOUT is rechecked by CHROUT: immediate Carry/A5,$40,no emitted start bit",dsr);

            var carrier = Probe(session,6,1,300,Mode.Rx,Peer.DelayedCarrier);
            Check("carrier_delayed",carrier.Done&&carrier.Call(1).Ok&&carrier.Call(2).Ok&&carrier.Call(2).A==0xA6&&
                carrier.Stage(3)>=carrier.CarrierReleaseCycle&&carrier.CarrierReleaseCycle>=carrier.Stage(2)+50_000&&carrier.OpenDtr&&carrier.SawRtsAsserted&&carrier.CloseReleased,
                "Full-duplex input waits for DCD while RTS/CTS are asserted, then receives the independent frame",carrier);

            var noCarrier = Probe(session,6,1,300,Mode.Rx,Peer.CarrierTimeout);
            Check("carrier_timeout",noCarrier.Done&&noCarrier.Call(1).GateError(8)&&noCarrier.Call(4).A==8&&noCarrier.Call(5).A==0&&
                noCarrier.WaitSeconds(2,4)>=0.5&&noCarrier.WaitSeconds(2,4)<=2.6&&noCarrier.CloseReleased,
                "DCD high gives bounded timeout and Carry/A5,$08; READST clears the reported status",noCarrier);

            var half = Probe(session,6,0x11,300,Mode.TxThenRx,Peer.HalfTurnaround);
            bool halfFrame = DecodeTx(half,0x55,8,0,1,out string halfWave);
            Check("half_duplex_turnaround",half.Done&&half.Call(1).Ok&&half.Call(2).Ok&&half.Call(6).Ok&&half.Call(7).Ok&&half.Call(7).A==0xA6&&
                halfFrame&&half.SawRtsAsserted&&half.SawRtsReleasedAfterAssert&&half.CtsTurnarounds>=2&&half.CloseReleased,
                "Half-duplex TX uses RTS low/CTS low; CHKIN releases RTS and waits for CTS high before RX; peer delays both transitions",half,halfWave);

            var three = Probe(session,6,0,300,Mode.TxThenRx,Peer.IgnoreHandshake);
            bool threeFrame = DecodeTx(three,0x55,8,0,1,out string threeWave);
            Check("three_line_ignores_control",three.Done&&three.Call(1).Ok&&three.Call(2).Ok&&three.Call(6).Ok&&three.Call(7).Ok&&three.Call(7).A==0xA6&&
                threeFrame&&!three.SawDtrAsserted&&!three.SawRtsAsserted&&three.CloseReleased,
                "Three-line mode transmits and receives with CTS/DSR/DCD all inactive, leaving RTS/DTR high",three,threeWave);

            foreach(var format in new[]{(Name:"5o1",Control:(byte)0x66,Command:(byte)0x20,Bits:5,Parity:1,Stops:1,Value:(byte)0x15),
                (Name:"7e2",Control:(byte)0xA6,Command:(byte)0x60,Bits:7,Parity:2,Stops:2,Value:(byte)0x55)})
            {
                var tx = Probe(session,format.Control,format.Command,300,Mode.Tx,Peer.Ready,format.Value);
                bool frame = DecodeTx(tx,format.Value,format.Bits,format.Parity,format.Stops,out string wave);
                Check("tx_"+format.Name,tx.Done&&tx.Call(2).Ok&&frame&&tx.Call(2).Registers(0x37,0x81),
                    "Physical TX word length, parity and stop bits match the configured frame",tx,wave);
                var rx = Probe(session,format.Control,format.Command,300,Mode.Rx,Peer.Ready,format.Value);
                Check("rx_"+format.Name,rx.Done&&rx.Call(2).Ok&&rx.Call(2).A==format.Value&&rx.Call(4).A==0,
                    "Independent framed RX waveform decodes requested word length, parity and stops",rx);
            }
            var parity = Probe(session,0xA6,0x60,300,Mode.Rx,Peer.BadParity,0x55);
            Check("rx_parity_error",parity.Done&&!parity.Call(2).Ok&&(parity.Call(2).P&1)!=0&&parity.Call(4).A==1&&parity.Call(5).A==0,
                "Incorrect received parity sets Carry and RSSTAT$01, acknowledged once by READST",parity);
            var framingError = Probe(session,6,0,300,Mode.Rx,Peer.BadStop);
            Check("rx_framing_error",framingError.Done&&!framingError.Call(2).Ok&&(framingError.Call(2).P&1)!=0&&framingError.Call(4).A==2&&framingError.Call(5).A==0,
                "Low stop bit sets Carry and RSSTAT$02, acknowledged once by READST",framingError);

            void Check(string name,bool passed,string expected,Observation observed,string extra="")
            {
                check("rs232_native_"+name,passed,expected,observed.Describe()+extra);
            }
        }
        finally
        {
            port.Ticked=oldTicked; port.OutputChanged=oldChanged;
            port.SetSerialInputs(true,false,false,false);
        }
#else
        check("rs232_native_patch",false,"RC3 UserPort/CIA integration is compiled","Missing TAPE_SERIAL_RC3_PATCH.");
#endif
    }

#if TAPE_SERIAL_RC3_PATCH
    enum Mode { Tx,Rx,TxThenRx }
    enum Peer { Ready,DelayedCts,CtsTimeout,DsrLostAfterSelect,DelayedCarrier,CarrierTimeout,HalfTurnaround,IgnoreHandshake,BadParity,BadStop }
    const ushort ProgramAddress=0xC000,ConfigAddress=0xC800,ResultAddress=0xC900,StageAddress=0xC9F0,DoneAddress=0xC9FF;
    readonly record struct CallResult(byte A,byte P,byte X,byte Y,byte RsStatus)
    {
        public bool Ok => (P&1)==0;
        public bool Registers(byte x,byte y) => X==x&&Y==y&&(P&4)==0;
        public bool GateError(byte error) => !Ok&&A==5&&(RsStatus&error)==error;
        public override string ToString()=>$"A={A:X2} P={P:X2} X={X:X2} Y={Y:X2} RS={RsStatus:X2}";
    }
    sealed class Observation
    {
        public double CpuHz,BitCycles;
        public byte[] Results;
        public bool Done,CloseReleased,SawRtsAsserted,SawRtsReleasedAfterAssert,SawDtrAsserted,OpenDtr;
        public byte OpenDirection;
        public long CtsReleaseCycle=-1,CarrierReleaseCycle=-1,RxStart=-1,EndCycle;
        public int CtsTurnarounds;
        public string Output;
        public List<(long Cycle,bool High)> TxEdges=new();
        public Dictionary<byte,long> Stages=new();
        public CallResult Call(int slot) {int at=slot*8;return new(Results[at],Results[at+1],Results[at+2],Results[at+3],Results[at+4]);}
        public long Stage(byte stage)=>Stages.GetValueOrDefault(stage,-1);
        public long FirstTxCycle=>TxEdges.FirstOrDefault(e=>!e.High,(-1,false)).Cycle;
        public double WaitSeconds(byte start,byte end)=>(Stage(end)-Stage(start))/CpuHz;
        public string Describe()=> $"done={Done}; bitCycles={BitCycles:F3}; openDDRB={OpenDirection:X2}; openDTR={OpenDtr}; closeReleased={CloseReleased}; " +
            $"CTSrelease={CtsReleaseCycle}; DCDrelease={CarrierReleaseCycle}; RXstart={RxStart}; stages="+string.Join(",",Stages.Select(p=>$"{p.Key}:{p.Value}"))+"\n"+
            string.Join("\n",Enumerable.Range(0,8).Select(i=>$"call{i}: {Call(i)}"))+"\nTX="+string.Join(",",TxEdges.Select(e=>$"{e.Cycle}:{(e.High?1:0)}"))+"\n"+Output+"\n";
    }
    sealed class Stub
    {
        public List<byte> Code=new();
        readonly Dictionary<string,int> labels=new();
        readonly List<(int Offset,string Label)> jumps=new();
        public void Emit(params byte[] bytes)=>Code.AddRange(bytes);
        public void Jsr(ushort target)=>Emit(0x20,(byte)target,(byte)(target>>8));
        public void Stage(byte number)=>Emit(0xA9,number,0x8D,(byte)(StageAddress&255),(byte)(StageAddress>>8));
        public void Capture(int slot)
        {
            ushort at=(ushort)(ResultAddress+slot*8);
            Emit(0x8D,(byte)at,(byte)(at>>8),0x8E,(byte)(at+2),(byte)(at>>8),0x8C,(byte)(at+3),(byte)(at>>8),
                0x08,0x68,0x8D,(byte)(at+1),(byte)(at>>8),0xAD,0x97,2,0x8D,(byte)(at+4),(byte)(at>>8));
        }
        public void ErrorToEnd(int slot)
        {
            ushort flags=(ushort)(ResultAddress+slot*8+1);
            Emit(0xAD,(byte)flags,(byte)(flags>>8),0x29,1,0xF0,3,0x4C,0,0);jumps.Add((Code.Count-2,"end"));
        }
        public void Select(bool input,int slot,byte stage)
        { Stage(stage);Emit(0xA2,2,0xA0,0x81);Jsr(input?(ushort)0xFFC6:(ushort)0xFFC9);Capture(slot);ErrorToEnd(slot); }
        public void Data(bool input,byte value,int slot,byte stage)
        { Stage(stage);Emit(0xA2,0x37,0xA0,0x81,0xA9,value);Jsr(input?(ushort)0xFFCF:(ushort)0xFFD2);Capture(slot); }
        public byte[] Finish()
        {
            labels["end"]=Code.Count;Stage(4);Jsr(0xFFB7);Capture(4);Jsr(0xFFB7);Capture(5);
            Jsr(0xFFCC);Emit(0xA9,2);Jsr(0xFFC3);Capture(3);
            Emit(0xA9,0xA5,0x8D,(byte)(DoneAddress&255),(byte)(DoneAddress>>8),0x60);
            foreach(var jump in jumps){int target=ProgramAddress+labels[jump.Label];Code[jump.Offset]=(byte)target;Code[jump.Offset+1]=(byte)(target>>8);}
            return Code.ToArray();
        }
    }
    static Observation Probe(Session session,byte control,byte command,int baud,Mode mode,Peer peer,byte value=0xA6)
    {
        var bus=(SystemBus)typeof(C64System).GetField("_bus",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(session.C64);
        var port=session.C64.UserPort;
        var observation=new Observation{CpuHz=session.C64.Model.CpuHz,BitCycles=session.C64.Model.CpuHz/baud};
        var stub=new Stub();stub.Stage(1);
        stub.Emit(0xA9,2,0xA2,2,0xA0,0);stub.Jsr(0xFFBA);
        stub.Emit(0xA9,2,0xA2,(byte)(ConfigAddress&255),0xA0,(byte)(ConfigAddress>>8));stub.Jsr(0xFFBD);stub.Jsr(0xFFC0);stub.Capture(0);stub.ErrorToEnd(0);
        stub.Select(mode==Mode.Rx,1,2);stub.Data(mode==Mode.Rx,mode==Mode.Rx?value:(value==0xA6?(byte)0x55:value),2,3);
        if(mode==Mode.TxThenRx){stub.Select(true,6,5);stub.Data(true,value,7,6);}
        byte[] code=stub.Finish();
        for(int i=0;i<code.Length;i++)bus.WriteRam((ushort)(ProgramAddress+i),code[i]);
        bus.WriteRam(ConfigAddress,control);bus.WriteRam(ConfigAddress+1,command);
        for(int i=0;i<256;i++)bus.WriteRam((ushort)(ResultAddress+i),0);
        int bits=8-((control>>5)&3),stops=(control&128)==0?1:2;
        int parity=(command&32)==0?0:(command&128)!=0?((command&64)==0?3:4):((command&64)==0?1:2);
        bool[] frame=Frame(value,bits,parity,stops);
        if(peer==Peer.BadParity)frame[1+bits]=!frame[1+bits];
        if(peer==Peer.BadStop)frame[frame.Length-1]=false;
        bool cts=peer!=Peer.DelayedCts&&peer!=Peer.CtsTimeout&&peer!=Peer.IgnoreHandshake&&peer!=Peer.HalfTurnaround;
        bool dsr=peer!=Peer.IgnoreHandshake,dcd=peer!=Peer.DelayedCarrier&&peer!=Peer.CarrierTimeout&&peer!=Peer.IgnoreHandshake;
        bool previousTx=port.TxHigh,previousRts=port.RtsAsserted;
        long turnaroundAt=-1;bool nextCts=false;byte lastStage=0;
        port.SetSerialInputs(true,cts,dsr,dcd);
        port.OutputChanged=p=>
        {
            if(p.TxHigh!=previousTx){observation.TxEdges.Add((p.Cycles,p.TxHigh));previousTx=p.TxHigh;}
            if(p.RtsAsserted){observation.SawRtsAsserted=true;}
            else if(observation.SawRtsAsserted)observation.SawRtsReleasedAfterAssert=true;
            if(p.DtrAsserted)observation.SawDtrAsserted=true;
            if(peer==Peer.HalfTurnaround&&p.RtsAsserted!=previousRts){turnaroundAt=p.Cycles+15_000;nextCts=p.RtsAsserted;}
            previousRts=p.RtsAsserted;
        };
        port.Ticked=p=>
        {
            byte stage=bus.ReadRam(StageAddress);
            if(stage!=lastStage)
            {
                lastStage=stage;observation.Stages.TryAdd(stage,p.Cycles);
                if(stage==2){observation.OpenDirection=p.Direction;observation.OpenDtr=p.DtrAsserted;}
                if((mode==Mode.Rx&&stage==3)||(mode==Mode.TxThenRx&&stage==6))observation.RxStart=p.Cycles+500;
                if(stage==3&&peer==Peer.DsrLostAfterSelect)dsr=false;
            }
            if(stage>=2&&peer==Peer.DelayedCts&&observation.CtsReleaseCycle<0&&p.Cycles>=observation.Stage(2)+50_000)
            { cts=true;observation.CtsReleaseCycle=p.Cycles; }
            if(stage>=2&&peer==Peer.DelayedCarrier&&observation.CarrierReleaseCycle<0&&p.Cycles>=observation.Stage(2)+50_000)
            { dcd=true;observation.CarrierReleaseCycle=p.Cycles; }
            if(turnaroundAt>=0&&p.Cycles>=turnaroundAt){cts=nextCts;turnaroundAt=-1;observation.CtsTurnarounds++;}
            bool rxHigh=true;
            if(observation.RxStart>=0&&p.Cycles>=observation.RxStart)
            {
                int index=(int)((p.Cycles-observation.RxStart)/observation.BitCycles);
                if(index<frame.Length)rxHigh=frame[index];
            }
            p.SetSerialInputs(rxHigh,cts,dsr,dcd);
            if(bus.ReadRam(DoneAddress)==0xA5)
            { observation.Done=true;observation.CloseReleased=p.TxHigh&&!p.RtsAsserted&&!p.DtrAsserted; }
        };
        int begin=session.Output.Length;session.Command("SYS49152",20_000);int elapsed=20_000;
        int maximum=(int)(session.C64.Model.CpuHz*3.2);
        while(!observation.Done&&!session.Jammed&&elapsed<maximum){session.Run(20_000);elapsed+=20_000;}
        observation.EndCycle=port.Cycles;
        observation.Results=Enumerable.Range(ResultAddress,64).Select(a=>bus.ReadRam((ushort)a)).ToArray();
        observation.Output=session.Output.ToString(begin,session.Output.Length-begin)+$"\nwatchdog cycles={elapsed}; PC={session.C64.Cpu.PC:X4}";
        port.Ticked=null;port.OutputChanged=null;port.SetSerialInputs(true,false,false,false);
        if(!observation.Done){session.C64.Reset();session.Run(2_000_000);}
        else session.Run(30_000);
        return observation;
    }
    static bool[] Frame(byte value,int bits,int parity,int stops)
    {
        var result=new List<bool>{false};int ones=0;
        for(int i=0;i<bits;i++){bool bit=(value&(1<<i))!=0;result.Add(bit);if(bit)ones++;}
        if(parity!=0)result.Add(parity switch{1=>(ones&1)==0,2=>(ones&1)!=0,3=>true,_=>false});
        for(int i=0;i<stops;i++)result.Add(true);
        return result.ToArray();
    }
    static bool DecodeTx(Observation observation,byte value,int bits,int parity,int stops,out string description)
    {
        long start=observation.FirstTxCycle;
        if(start<0){description="No falling TX start edge.";return false;}
        bool[] expected=Frame(value,bits,parity,stops);
        bool At(double cycle)
        {
            bool high=true;foreach(var edge in observation.TxEdges){if(edge.Cycle>cycle)break;high=edge.High;}return high;
        }
        string samples="";bool equal=true;
        for(int i=0;i<expected.Length;i++){bool sampled=At(start+(i+0.5)*observation.BitCycles);samples+=sampled?'1':'0';if(sampled!=expected[i])equal=false;}
        var edges=observation.TxEdges.Where(e=>e.Cycle>=start).ToArray();
        double actualPeriod=bits==8&&parity==0&&value==0x55&&edges.Length>=10?(edges[9].Cycle-start)/9.0:double.NaN;
        bool rate=double.IsNaN(actualPeriod)||Math.Abs(actualPeriod/observation.BitCycles-1)<0.05;
        description=$"samples={samples}; expected={string.Concat(expected.Select(b=>b?'1':'0'))}; meanBit={actualPeriod:F3}; nominal={observation.BitCycles:F3}";
        return equal&&rate;
    }
#endif
}
