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

        public static bool TimerACounts(byte controlRegisterA)
        {
            return (controlRegisterA & 0x20) == 0;
        }

        public static bool TimerBUsesTimerAUnderflows(byte controlRegisterB)
        {
            int source = (controlRegisterB >> 5) & 0x03;
            return source == 2 || source == 3;
        }

        public static bool TimerBUsesSystemClock(byte controlRegisterB)
        {
            return ((controlRegisterB >> 5) & 0x03) == 0;
        }

        public static bool Tick(
            ref ushort counter,
            ushort latch,
            ref byte startDelay,
            ref bool reloadHold,
            byte controlRegister,
            bool countPulse,
            out bool stopOneShot,
            bool exposeTerminalZero = false)
        {
            stopOneShot = false;
            if ((controlRegister & 0x01) == 0)
            {
                return false;
            }

            if (startDelay > 0)
            {
                startDelay--;
                return false;
            }

            if (!countPulse)
            {
                return false;
            }

            if (reloadHold)
            {
                if (exposeTerminalZero)
                {
                    counter = ReloadAfterUnderflow(latch);
                }

                reloadHold = false;
                return false;
            }

            bool isOneShot = (controlRegister & 0x08) != 0;
            if (latch == 0)
            {
                if (exposeTerminalZero && isOneShot && counter == 0)
                {
                    stopOneShot = true;
                    return true;
                }

                if (counter == 0)
                {
                    counter = 0xFFFF;
                }

                if (counter > 1)
                {
                    counter--;
                    return false;
                }

                if (exposeTerminalZero && isOneShot)
                {
                    counter = 0;
                    return false;
                }

                counter = ReloadAfterUnderflow(latch);
                stopOneShot = isOneShot;
                return true;
            }

            if (exposeTerminalZero && isOneShot && counter == 0)
            {
                counter = ReloadAfterUnderflow(latch);
                reloadHold = !isOneShot;
                stopOneShot = isOneShot;
                return true;
            }

            if (counter > 1)
            {
                counter--;
                return false;
            }

            if (exposeTerminalZero && isOneShot)
            {
                counter = 0;
                return false;
            }

            counter = exposeTerminalZero ? (ushort)0 : ReloadAfterUnderflow(latch);
            reloadHold = !isOneShot;
            stopOneShot = isOneShot;
            return true;
        }
    }
}
