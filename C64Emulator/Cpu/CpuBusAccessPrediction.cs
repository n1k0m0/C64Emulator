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
    /// Describes a side-effect-free prediction of the CPU bus access for the next cycle.
    /// </summary>
    public struct CpuBusAccessPrediction
    {
        public CpuTraceAccessType AccessType;

        public ushort Address;

        public byte Value;

        public static CpuBusAccessPrediction None
        {
            get
            {
                return new CpuBusAccessPrediction
                {
                    AccessType = CpuTraceAccessType.None
                };
            }
        }
    }
}
