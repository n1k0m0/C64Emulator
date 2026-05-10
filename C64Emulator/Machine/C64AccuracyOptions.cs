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
    /// Defines emulator-wide accuracy policy knobs.
    /// </summary>
    public sealed class C64AccuracyOptions
    {
        /// <summary>
        /// Initializes a new C64AccuracyOptions instance.
        /// </summary>
        public C64AccuracyOptions()
        {
            EnableLoadHack = true;
            EnableKernalIecHooks = false;
            ForceSoftwareIecTransport = true;
            EnableInputInjection = true;
            RunDriveCpuContinuously = false;
        }

        public bool EnableLoadHack { get; set; }

        public bool EnableKernalIecHooks { get; set; }

        public bool ForceSoftwareIecTransport { get; set; }

        public bool EnableInputInjection { get; set; }

        public bool RunDriveCpuContinuously { get; set; }

        /// <summary>
        /// Gets the compatibility profile used by the interactive emulator.
        /// </summary>
        public static C64AccuracyOptions Compatibility
        {
            get { return new C64AccuracyOptions(); }
        }

        /// <summary>
        /// Gets a profile that removes emulator convenience shortcuts for deterministic accuracy work.
        /// </summary>
        public static C64AccuracyOptions Accuracy
        {
            get
            {
                return new C64AccuracyOptions
                {
                    EnableLoadHack = false,
                    EnableKernalIecHooks = false,
                    ForceSoftwareIecTransport = false,
                    EnableInputInjection = false,
                    RunDriveCpuContinuously = true
                };
            }
        }

        /// <summary>
        /// Creates a detached copy.
        /// </summary>
        public C64AccuracyOptions Clone()
        {
            return new C64AccuracyOptions
            {
                EnableLoadHack = EnableLoadHack,
                EnableKernalIecHooks = EnableKernalIecHooks,
                ForceSoftwareIecTransport = ForceSoftwareIecTransport,
                EnableInputInjection = EnableInputInjection,
                RunDriveCpuContinuously = RunDriveCpuContinuously
            };
        }
    }
}
