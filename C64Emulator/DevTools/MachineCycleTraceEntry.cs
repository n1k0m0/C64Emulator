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
    /// Describes one machine-cycle trace sample.
    /// </summary>
    public sealed class MachineCycleTraceEntry
    {
        public int Sample { get; set; }

        public long GlobalCycle { get; set; }

        public int RasterLine { get; set; }

        public int CycleInLine { get; set; }

        public int BeamX { get; set; }

        public int BeamY { get; set; }

        public bool BadLine { get; set; }

        public string Phi1Action { get; set; }

        public string Phi2Action { get; set; }

        public bool BaLow { get; set; }

        public bool AecLow { get; set; }

        public bool CpuCanAccess { get; set; }

        public bool VicCanAccess { get; set; }

        public string BusOwner { get; set; }

        public bool CpuBlocked { get; set; }

        public bool BusRequestPending { get; set; }

        public string PredictedCpuAccessType { get; set; }

        public string PredictedCpuAddress { get; set; }

        public string PredictedCpuValue { get; set; }

        public MachineCpuTraceEntry Cpu { get; set; }

        public MachineVicPipelineTraceEntry Vic { get; set; }

        public MachineDriveSchedulerTraceEntry Drive8Scheduler { get; set; }

        public string Memory { get; set; }

        public string Cia { get; set; }

        public string Sid { get; set; }

        public string Iec { get; set; }

        public string Drive8 { get; set; }
    }
}
