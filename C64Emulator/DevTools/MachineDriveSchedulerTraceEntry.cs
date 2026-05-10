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
    /// Describes 1541 scheduler state in machine traces.
    /// </summary>
    public sealed class MachineDriveSchedulerTraceEntry
    {
        public int DeviceNumber { get; set; }

        public double TargetCycles { get; set; }

        public double ExecutedCycles { get; set; }

        public bool NeedsClockTick { get; set; }

        public bool RunHardwareContinuously { get; set; }

        public string ProgramCounter { get; set; }

        public bool HasCustomCodeActive { get; set; }

        public bool IsHardwareTransportReady { get; set; }
    }
}
