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
    /// Represents the cpu trace recorder component.
    /// </summary>
    public sealed class CpuTraceRecorder
    {
        private readonly List<CpuTraceEntry> _entries = new List<CpuTraceEntry>();

        public IReadOnlyList<CpuTraceEntry> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// Handles the attach operation.
        /// </summary>
        public void Attach(Cpu6510 cpu)
        {
            cpu.TraceEmitted += OnTraceEmitted;
        }

        /// <summary>
        /// Handles the detach operation.
        /// </summary>
        public void Detach(Cpu6510 cpu)
        {
            cpu.TraceEmitted -= OnTraceEmitted;
        }

        /// <summary>
        /// Handles the on trace emitted operation.
        /// </summary>
        private void OnTraceEmitted(CpuTraceEntry entry)
        {
            _entries.Add(entry);
        }
    }
}
