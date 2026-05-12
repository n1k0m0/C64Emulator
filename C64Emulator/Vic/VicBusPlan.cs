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
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Lists the supported vic bus action values.
    /// </summary>
    public enum VicBusAction
    {
        Idle,
        Refresh,
        CharFetch,
        MatrixFetch,
        SpritePointerFetch,
        SpriteDataFetch
    }

    /// <summary>
    /// Stores vic bus slot state.
    /// </summary>
    public struct VicBusSlot
    {
        public VicBusAction Phi1Action;
        public VicBusAction Phi2Action;
        public int SpriteIndex;
        public bool BlocksCpu;
        public bool BusRequestPending;
    }

    /// <summary>
    /// Represents the vic bus plan component.
    /// </summary>
    public sealed class VicBusPlan
    {
        private readonly VicBusSlot[] _slots = new VicBusSlot[63];

        /// <summary>
        /// Gets the slot value.
        /// </summary>
        public VicBusSlot GetSlot(int cycleInLine)
        {
            return _slots[cycleInLine % _slots.Length];
        }

        /// <summary>
        /// Writes the current line bus plan into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            StateSerializer.WriteObjectFields(writer, this);
        }

        /// <summary>
        /// Restores the current line bus plan from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            StateSerializer.ReadObjectFields(reader, this);
        }

        /// <summary>
        /// Builds line.
        /// </summary>
        public void BuildLine(bool badLine, bool[] spriteDmaActive)
        {
            for (int index = 0; index < _slots.Length; index++)
            {
                _slots[index] = new VicBusSlot
                {
                    Phi1Action = VicBusAction.Idle,
                    Phi2Action = VicBusAction.Idle,
                    SpriteIndex = -1,
                    BlocksCpu = false,
                    BusRequestPending = false
                };
            }

            // Sprite pointers on phi1: 3..7 at the start of the line, 0..2 at the end.
            SetPhi1Pointer(1, 3);
            SetPhi1Pointer(3, 4);
            SetPhi1Pointer(5, 5);
            SetPhi1Pointer(7, 6);
            SetPhi1Pointer(9, 7);
            SetPhi1Pointer(58, 0);
            SetPhi1Pointer(60, 1);
            SetPhi1Pointer(62, 2);

            // RAM refresh cycles on phi1.
            for (int cycle = 11; cycle <= 15; cycle++)
            {
                SetPhi1Action(cycle, VicBusAction.Refresh, -1);
            }

            // Character/graphics fetches on phi1 for the visible matrix.
            for (int cycle = 16; cycle <= 55; cycle++)
            {
                SetPhi1Action(cycle, VicBusAction.CharFetch, -1);
            }

            if (badLine)
            {
                for (int cycle = 12; cycle <= 54; cycle++)
                {
                    ApplyMatrixFetchToSlot(ref _slots[cycle - 1], cycle, 12, 15, 15, 54);
                }
            }

            if (spriteDmaActive == null)
            {
                return;
            }

            for (int spriteIndex = 0; spriteIndex < spriteDmaActive.Length && spriteIndex < 8; spriteIndex++)
            {
                if (!spriteDmaActive[spriteIndex])
                {
                    continue;
                }

                int pointerCycle = GetPointerCycle(spriteIndex);
                int nextCycle = (pointerCycle % 63) + 1;

                // Each active sprite consumes three half-cycles after the pointer:
                // phi2(pointer cycle), phi1(next cycle), phi2(next cycle).
                SetPhi2Action(pointerCycle, VicBusAction.SpriteDataFetch, spriteIndex, true);
                SetPhi1Action(nextCycle, VicBusAction.SpriteDataFetch, spriteIndex);
                SetPhi2Action(nextCycle, VicBusAction.SpriteDataFetch, spriteIndex, true);
                MarkBusRequestPending(pointerCycle);
            }
        }

        /// <summary>
        /// Activates sprite DMA fetch slots for the current line.
        /// </summary>
        public void ActivateSpriteDma(int spriteIndex)
        {
            if ((uint)spriteIndex >= 8)
            {
                return;
            }

            int pointerCycle = GetPointerCycle(spriteIndex);
            int nextCycle = (pointerCycle % 63) + 1;

            SetPhi2Action(pointerCycle, VicBusAction.SpriteDataFetch, spriteIndex, true);
            SetPhi1Action(nextCycle, VicBusAction.SpriteDataFetch, spriteIndex);
            SetPhi2Action(nextCycle, VicBusAction.SpriteDataFetch, spriteIndex, true);
            MarkBusRequestPending(pointerCycle);
        }

        /// <summary>
        /// Applies a badline matrix-fetch sequence to an existing half-cycle slot.
        /// </summary>
        public static void ApplyMatrixFetchToSlot(
            ref VicBusSlot slot,
            int cycle,
            int requestStartCycle,
            int fetchStartCycle,
            int cpuBlockStartCycle,
            int fetchEndCycle)
        {
            if (cycle < requestStartCycle || cycle > fetchEndCycle)
            {
                return;
            }

            if (cycle >= fetchStartCycle)
            {
                slot.Phi2Action = VicBusAction.MatrixFetch;
            }

            if (cycle >= requestStartCycle && cycle < cpuBlockStartCycle)
            {
                slot.BusRequestPending = true;
            }

            if (cycle >= cpuBlockStartCycle)
            {
                slot.BlocksCpu = true;
            }
        }

        /// <summary>
        /// Handles the mark bus request pending operation.
        /// </summary>
        private void MarkBusRequestPending(int firstBlockedCycle)
        {
            for (int offset = 3; offset >= 1; offset--)
            {
                int cycle = firstBlockedCycle - offset;
                while (cycle <= 0)
                {
                    cycle += _slots.Length;
                }

                _slots[cycle - 1].BusRequestPending = true;
            }
        }

        /// <summary>
        /// Sets the phi1 pointer value.
        /// </summary>
        private void SetPhi1Pointer(int cycle, int spriteIndex)
        {
            SetPhi1Action(cycle, VicBusAction.SpritePointerFetch, spriteIndex);
        }

        /// <summary>
        /// Sets the phi1 action value.
        /// </summary>
        private void SetPhi1Action(int cycle, VicBusAction action, int spriteIndex)
        {
            int index = cycle - 1;
            _slots[index].Phi1Action = action;
            if (spriteIndex >= 0)
            {
                _slots[index].SpriteIndex = spriteIndex;
            }
        }

        /// <summary>
        /// Sets the phi2 action value.
        /// </summary>
        private void SetPhi2Action(int cycle, VicBusAction action, int spriteIndex, bool blocksCpu)
        {
            int index = cycle - 1;
            _slots[index].Phi2Action = action;
            _slots[index].BlocksCpu = blocksCpu;
            if (spriteIndex >= 0)
            {
                _slots[index].SpriteIndex = spriteIndex;
            }
        }

        /// <summary>
        /// Gets the pointer cycle value.
        /// </summary>
        private static int GetPointerCycle(int spriteIndex)
        {
            switch (spriteIndex)
            {
                case 0: return 58;
                case 1: return 60;
                case 2: return 62;
                case 3: return 1;
                case 4: return 3;
                case 5: return 5;
                case 6: return 7;
                case 7: return 9;
                default: return 1;
            }
        }
    }
}
