// SPDX-License-Identifier: Apache-2.0
// Physical TTL user-port pins; external peers supply line states, never ROM bytes.
using System;

namespace C64Emulator.Core
{
    public sealed class UserPort
    {
        private byte _inputPins = 0xFF, _outputPins = 0xFF, _direction;
        private bool _txHigh = true;
        private long _cycles;
        public byte InputPins => _inputPins;
        public byte OutputPins => _outputPins;
        public byte Direction => _direction;
        public bool RxHigh => (_inputPins & 1) != 0;
        public bool TxHigh => _txHigh;
        public bool RtsAsserted => (_direction & 2) != 0 && (_outputPins & 2) == 0;
        public bool DtrAsserted => (_direction & 4) != 0 && (_outputPins & 4) == 0;
        public long Cycles => _cycles;
        public Action<UserPort> Ticked { get; set; }
        public Action<UserPort> OutputChanged { get; set; }

        public void SetInputPins(byte levels) { _inputPins = levels; }
        public void SetSerialInputs(bool receiveHigh, bool ctsAsserted, bool dsrAsserted, bool dcdAsserted)
        {
            byte pins = (byte)(_inputPins | 0xD1);
            if (!receiveHigh) pins &= 0xFE;
            if (ctsAsserted) pins &= 0xBF;
            if (dsrAsserted) pins &= 0x7F;
            if (dcdAsserted) pins &= 0xEF;
            _inputPins = pins;
        }

        internal void UpdateOutputs(byte portA, byte directionA, byte portB, byte directionB)
        {
            bool txHigh = (directionA & 4) == 0 || (portA & 4) != 0;
            byte outputs = (byte)((portB & directionB) | ~directionB);
            bool changed = txHigh != _txHigh || outputs != _outputPins || directionB != _direction;
            _txHigh = txHigh; _outputPins = outputs; _direction = directionB;
            if (changed) OutputChanged?.Invoke(this);
        }

        // External peers attach to this stable object. Savestates restore only
        // physical state, never replace the peer's object or its callbacks.
        internal void CopyStateFrom(UserPort restored)
        {
            if (ReferenceEquals(this, restored)) return;
            _inputPins = restored._inputPins;
            _outputPins = restored._outputPins;
            _direction = restored._direction;
            _txHigh = restored._txHigh;
            _cycles = restored._cycles;
        }

        internal void Tick()
        {
            _cycles++;
            Ticked?.Invoke(this);
        }
    }
}
