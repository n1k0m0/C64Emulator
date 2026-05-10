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
    /// Centralizes MOS 6526 timer rules shared by CIA1 and CIA2.
    /// </summary>
    internal static class Cia6526TimerRules
    {
        public static ushort ForceLoad(ushort latch)
        {
            return latch;
        }

        public static ushort ReloadAfterUnderflow(ushort latch)
        {
            return latch != 0 ? latch : (ushort)0xFFFF;
        }

        public static byte ApplyInterruptMaskWrite(byte currentMask, byte value)
        {
            if ((value & 0x80) != 0)
            {
                return (byte)(currentMask | (value & 0x1F));
            }

            return (byte)(currentMask & ~(value & 0x1F));
        }

        public static bool TimerBCounts(byte controlRegisterB, bool timerAUnderflow)
        {
            int source = (controlRegisterB >> 5) & 0x03;
            return source == 0 || ((source == 2 || source == 3) && timerAUnderflow);
        }
    }
}
