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
    /// Stores vic timing state.
    /// </summary>
    public struct VicTiming
    {
        public int RasterLine;
        public int CycleInLine;
        public long GlobalCycle;
        public int BeamX;
        public int BeamY;
        public bool BadLine;
        public VicBusAction Phi1Action;
        public VicBusAction Phi2Action;
        public bool CpuBlocked;
        public bool BusRequestPending;
    }
}
