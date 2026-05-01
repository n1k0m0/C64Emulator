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
    /// Represents the prg loader component.
    /// </summary>
    public static class PrgLoader
    {
        private const ushort BasicStartPointerAddress = 0x002B;
        private const ushort BasicVariablesPointerAddress = 0x002D;
        private const ushort BasicArraysPointerAddress = 0x002F;
        private const ushort BasicStringsPointerAddress = 0x0031;

        /// <summary>
        /// Attempts to load into memory and reports whether it succeeded.
        /// </summary>
        public static bool TryLoadIntoMemory(SystemBus bus, byte[] programBytes, out ushort loadAddress, out ushort endAddress)
        {
            loadAddress = 0x0000;
            endAddress = 0x0000;

            if (bus == null || programBytes == null || programBytes.Length < 2)
            {
                return false;
            }

            loadAddress = (ushort)(programBytes[0] | (programBytes[1] << 8));
            endAddress = loadAddress;
            for (int index = 2; index < programBytes.Length; index++)
            {
                bus.WriteRam(endAddress, programBytes[index]);
                endAddress++;
            }

            UpdateBasicPointers(bus, loadAddress, endAddress);
            return true;
        }

        /// <summary>
        /// Updates basic pointers.
        /// </summary>
        public static void UpdateBasicPointers(SystemBus bus, ushort loadAddress, ushort endAddress)
        {
            if (bus == null)
            {
                return;
            }

            ushort basicStart = ReadWord(bus, BasicStartPointerAddress);
            if (loadAddress != basicStart && loadAddress != 0x0801)
            {
                return;
            }

            WriteWord(bus, BasicVariablesPointerAddress, endAddress);
            WriteWord(bus, BasicArraysPointerAddress, endAddress);
            WriteWord(bus, BasicStringsPointerAddress, endAddress);
        }

        /// <summary>
        /// Reads word.
        /// </summary>
        private static ushort ReadWord(SystemBus bus, ushort address)
        {
            return (ushort)(bus.CpuRead(address) | (bus.CpuRead((ushort)(address + 1)) << 8));
        }

        /// <summary>
        /// Writes word.
        /// </summary>
        private static void WriteWord(SystemBus bus, ushort address, ushort value)
        {
            bus.WriteRam(address, (byte)(value & 0xFF));
            bus.WriteRam((ushort)(address + 1), (byte)(value >> 8));
        }
    }
}
