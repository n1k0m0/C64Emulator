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
    /// Selects the CIA timer-interrupt phase used by a machine configuration.
    /// </summary>
    public enum CiaChipRevision
    {
        /// <summary>
        /// Original NMOS 6526 timing. Timer interrupt flags become visible one CIA tick later.
        /// </summary>
        Mos6526,

        /// <summary>
        /// Later 6526A/8521 timing. Timer interrupt flags become visible immediately after underflow.
        /// </summary>
        Mos6526A
    }
}
