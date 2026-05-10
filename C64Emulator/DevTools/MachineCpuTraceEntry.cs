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
namespace C64Emulator.Core
{
    /// <summary>
    /// Describes CPU-side work observed during one traced machine cycle.
    /// </summary>
    public sealed class MachineCpuTraceEntry
    {
        public long Cycle { get; set; }

        public string StateBefore { get; set; }

        public string StateAfter { get; set; }

        public string Instruction { get; set; }

        public string Opcode { get; set; }

        public string LastOpcodeAddress { get; set; }

        public int StepIndexBefore { get; set; }

        public int StepIndexAfter { get; set; }

        public string PcBefore { get; set; }

        public string PcAfter { get; set; }

        public string ABefore { get; set; }

        public string AAfter { get; set; }

        public string XBefore { get; set; }

        public string XAfter { get; set; }

        public string YBefore { get; set; }

        public string YAfter { get; set; }

        public string SpBefore { get; set; }

        public string SpAfter { get; set; }

        public string SrBefore { get; set; }

        public string SrAfter { get; set; }

        public string AccessType { get; set; }

        public string Address { get; set; }

        public string Value { get; set; }
    }
}
