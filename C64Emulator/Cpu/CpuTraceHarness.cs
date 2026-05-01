/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System.Collections.Generic;

namespace C64Emulator.Core
{
    /// <summary>
    /// Represents the cpu trace harness component.
    /// </summary>
    public sealed class CpuTraceHarness
    {
        private const ushort DefaultStartAddress = 0x0200;

        private readonly SystemBus _bus;
        private readonly Cpu6510 _cpu;

        /// <summary>
        /// Initializes a new CpuTraceHarness instance.
        /// </summary>
        public CpuTraceHarness()
        {
            _bus = new SystemBus();
            _cpu = new Cpu6510(_bus);
            Reset(DefaultStartAddress);
        }

        public Cpu6510 Cpu
        {
            get { return _cpu; }
        }

        public SystemBus Bus
        {
            get { return _bus; }
        }

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset(ushort startAddress)
        {
            _bus.InitializeMemory();
            _bus.SetOwner(BusOwner.Cpu);
            _cpu.Reset(startAddress);
            _cpu.PC = startAddress;
            _cpu.A = 0;
            _cpu.X = 0;
            _cpu.Y = 0;
            _cpu.SP = 0xFD;
            _cpu.SR = 0x24;
        }

        /// <summary>
        /// Loads program.
        /// </summary>
        public void LoadProgram(ushort startAddress, params byte[] bytes)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                _bus.WriteRam((ushort)(startAddress + index), bytes[index]);
            }
        }

        /// <summary>
        /// Sets the irq value.
        /// </summary>
        public void SetIrq(bool asserted)
        {
            _bus.SetExternalIrq(asserted);
        }

        /// <summary>
        /// Sets the nmi value.
        /// </summary>
        public void SetNmi(bool asserted)
        {
            _bus.SetExternalNmi(asserted);
        }

        /// <summary>
        /// Handles the trace instruction operation.
        /// </summary>
        public IReadOnlyList<CpuTraceEntry> TraceInstruction(ushort startAddress, params byte[] bytes)
        {
            Reset(startAddress);
            LoadProgram(startAddress, bytes);
            return TraceUntilInstructionCompletes(64);
        }

        /// <summary>
        /// Handles the measure instruction cycles operation.
        /// </summary>
        public int MeasureInstructionCycles(ushort startAddress, params byte[] bytes)
        {
            return TraceInstruction(startAddress, bytes).Count;
        }

        /// <summary>
        /// Handles the trace cycles operation.
        /// </summary>
        public IReadOnlyList<CpuTraceEntry> TraceCycles(int cpuCycles)
        {
            var recorder = new CpuTraceRecorder();
            recorder.Attach(_cpu);
            _cpu.TraceEnabled = true;

            try
            {
                for (int cycle = 0; cycle < cpuCycles; cycle++)
                {
                    _cpu.Tick();
                    if (_cpu.State == CpuState.Jammed)
                    {
                        break;
                    }
                }

                return new List<CpuTraceEntry>(recorder.Entries);
            }
            finally
            {
                _cpu.TraceEnabled = false;
                recorder.Detach(_cpu);
            }
        }

        /// <summary>
        /// Handles the trace until instruction completes operation.
        /// </summary>
        public IReadOnlyList<CpuTraceEntry> TraceUntilInstructionCompletes(int maxCpuCycles)
        {
            var recorder = new CpuTraceRecorder();
            bool enteredExecution = false;

            recorder.Attach(_cpu);
            _cpu.TraceEnabled = true;

            try
            {
                for (int cycle = 0; cycle < maxCpuCycles; cycle++)
                {
                    _cpu.Tick();

                    if (_cpu.State == CpuState.ExecuteInstruction || _cpu.State == CpuState.InterruptSequence)
                    {
                        enteredExecution = true;
                    }

                    if (_cpu.State == CpuState.Jammed)
                    {
                        break;
                    }

                    if (enteredExecution && _cpu.State == CpuState.FetchOpcode)
                    {
                        break;
                    }
                }

                return new List<CpuTraceEntry>(recorder.Entries);
            }
            finally
            {
                _cpu.TraceEnabled = false;
                recorder.Detach(_cpu);
            }
        }
    }
}
