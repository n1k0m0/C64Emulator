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
    /// Stores instruction context state.
    /// </summary>
    public struct InstructionContext
    {
        public byte Opcode;
        public int StepIndex;
        public ushort Address;
        public ushort Address2;
        public byte Operand;
        public byte Operand2;
        public bool PageCrossed;
        public bool BranchTaken;

        /// <summary>
        /// Resets the component to its power-on or idle state.
        /// </summary>
        public void Reset(byte opcode)
        {
            Opcode = opcode;
            StepIndex = 0;
            Address = 0;
            Address2 = 0;
            Operand = 0;
            Operand2 = 0;
            PageCrossed = false;
            BranchTaken = false;
        }
    }
}
