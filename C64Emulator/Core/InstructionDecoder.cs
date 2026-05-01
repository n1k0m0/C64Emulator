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
    /// Executes one micro-step of a decoded 6510 instruction.
    /// </summary>
    /// <param name="cpu">CPU instance that owns the instruction state.</param>
    /// <param name="context">Mutable instruction context for the current opcode.</param>
    /// <returns>True when the instruction has completed; otherwise false.</returns>
    public delegate bool InstructionStepper(Cpu6510 cpu, ref InstructionContext context);

    /// <summary>
    /// Represents the instruction decoder component.
    /// </summary>
    public static class InstructionDecoder
    {
        /// <summary>
        /// Handles the decode operation.
        /// </summary>
        public static InstructionStepper Decode(byte opcode)
        {
            switch (opcode)
            {
                case 0x00:
                    return InstructionSteps.BrkImplied;
                case 0x10:
                    return InstructionSteps.BplRelative;
                case 0x20:
                    return InstructionSteps.JsrAbsolute;
                case 0x24:
                    return InstructionSteps.BitZeroPage;
                case 0x2C:
                    return InstructionSteps.BitAbsolute;
                case 0x30:
                    return InstructionSteps.BmiRelative;
                case 0x40:
                    return InstructionSteps.RtiImplied;
                case 0x4C:
                    return InstructionSteps.JmpAbsolute;
                case 0x50:
                    return InstructionSteps.BvcRelative;
                case 0x58:
                    return InstructionSteps.CliImplied;
                case 0x60:
                    return InstructionSteps.RtsImplied;
                case 0x6C:
                    return InstructionSteps.JmpIndirect;
                case 0x70:
                    return InstructionSteps.BvsRelative;
                case 0x78:
                    return InstructionSteps.SeiImplied;
                case 0x80:
                case 0x82:
                case 0x89:
                case 0xC2:
                case 0xE2:
                    return InstructionSteps.NopImmediate;
                case 0x04:
                case 0x44:
                case 0x64:
                    return InstructionSteps.NopZeroPage;
                case 0x0C:
                    return InstructionSteps.NopAbsolute;
                case 0x14:
                case 0x34:
                case 0x54:
                case 0x74:
                case 0xD4:
                case 0xF4:
                    return InstructionSteps.NopZeroPageX;
                case 0x1A:
                case 0x3A:
                case 0x5A:
                case 0x7A:
                case 0xDA:
                case 0xEA:
                case 0xFA:
                    return InstructionSteps.NopImplied;
                case 0x1C:
                case 0x3C:
                case 0x5C:
                case 0x7C:
                case 0xDC:
                case 0xFC:
                    return InstructionSteps.NopAbsoluteX;
                case 0x08:
                    return InstructionSteps.PhpImplied;
                case 0x18:
                    return InstructionSteps.ClcImplied;
                case 0x28:
                    return InstructionSteps.PlpImplied;
                case 0x38:
                    return InstructionSteps.SecImplied;
                case 0x48:
                    return InstructionSteps.PhaImplied;
                case 0x68:
                    return InstructionSteps.PlaImplied;
                case 0x88:
                    return InstructionSteps.DeyImplied;
                case 0x90:
                    return InstructionSteps.BccRelative;
                case 0x8A:
                    return InstructionSteps.TxaImplied;
                case 0x98:
                    return InstructionSteps.TyaImplied;
                case 0x9A:
                    return InstructionSteps.TxsImplied;
                case 0xA0:
                    return InstructionSteps.LdyImmediate;
                case 0xA2:
                    return InstructionSteps.LdxImmediate;
                case 0xA8:
                    return InstructionSteps.TayImplied;
                case 0xAA:
                    return InstructionSteps.TaxImplied;
                case 0xAB:
                    return InstructionSteps.LaxImmediate;
                case 0xB0:
                    return InstructionSteps.BcsRelative;
                case 0xB8:
                    return InstructionSteps.ClvImplied;
                case 0xBA:
                    return InstructionSteps.TsxImplied;
                case 0xBB:
                    return InstructionSteps.LasAbsoluteY;
                case 0xC0:
                    return InstructionSteps.CpyImmediate;
                case 0xC8:
                    return InstructionSteps.InyImplied;
                case 0xCA:
                    return InstructionSteps.DexImplied;
                case 0xCB:
                    return InstructionSteps.AxsImmediate;
                case 0xD0:
                    return InstructionSteps.BneRelative;
                case 0xD8:
                    return InstructionSteps.CldImplied;
                case 0xE0:
                    return InstructionSteps.CpxImmediate;
                case 0xE8:
                    return InstructionSteps.InxImplied;
                case 0xEB:
                    return InstructionSteps.SbcImmediate;
                case 0xF0:
                    return InstructionSteps.BeqRelative;
                case 0xF8:
                    return InstructionSteps.SedImplied;
                case 0x0B:
                case 0x2B:
                    return InstructionSteps.AncImmediate;
                case 0x4B:
                    return InstructionSteps.AlrImmediate;
                case 0x6B:
                    return InstructionSteps.ArrImmediate;
                case 0x8B:
                    return InstructionSteps.XaaImmediate;
                case 0x93:
                    return InstructionSteps.AhxIndirectZeroPageY;
                case 0x9B:
                    return InstructionSteps.TasAbsoluteY;
                case 0x9C:
                    return InstructionSteps.ShyAbsoluteX;
                case 0x9E:
                    return InstructionSteps.ShxAbsoluteY;
                case 0x9F:
                    return InstructionSteps.AhxAbsoluteY;
                case 0x02:
                case 0x12:
                case 0x22:
                case 0x32:
                case 0x42:
                case 0x52:
                case 0x62:
                case 0x72:
                case 0x92:
                case 0xB2:
                case 0xD2:
                case 0xF2:
                    return InstructionSteps.Kil;
                default:
                    return DecodeByPattern(opcode);
            }
        }

        /// <summary>
        /// Decodes by pattern.
        /// </summary>
        private static InstructionStepper DecodeByPattern(byte opcode)
        {
            switch (opcode & 0x03)
            {
                case 0x01:
                    return InstructionSteps.Group01;
                case 0x02:
                    return InstructionSteps.Group02;
                case 0x03:
                    return InstructionSteps.Group03;
                case 0x00:
                    return InstructionSteps.Group00;
                default:
                    return InstructionSteps.Illegal;
            }
        }
    }
}
