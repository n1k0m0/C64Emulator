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
namespace OpenTK.Input
{
    /// <summary>
    /// Compatibility key enum that preserves the OpenTK 3 key names used by the emulator.
    /// </summary>
    public enum Key
    {
        Unknown = 0,
        ShiftLeft = 1,
        LShift = 1,
        ShiftRight = 2,
        RShift = 2,
        ControlLeft = 3,
        LControl = 3,
        AltLeft = 5,
        LAlt = 5,
        F1 = 10,
        F3 = 12,
        F5 = 14,
        F7 = 16,
        F8 = 17,
        F9 = 18,
        F10 = 19,
        F11 = 20,
        F12 = 21,
        Up = 45,
        Down = 46,
        Left = 47,
        Right = 48,
        Enter = 49,
        Escape = 50,
        Space = 51,
        Tab = 52,
        BackSpace = 53,
        Delete = 55,
        Home = 58,
        PageUp = 59,
        PageDown = 60,
        KeypadMultiply = 78,
        A = 83,
        B = 84,
        C = 85,
        D = 86,
        E = 87,
        F = 88,
        G = 89,
        H = 90,
        I = 91,
        J = 92,
        K = 93,
        L = 94,
        M = 95,
        N = 96,
        O = 97,
        P = 98,
        Q = 99,
        R = 100,
        S = 101,
        T = 102,
        U = 103,
        V = 104,
        W = 105,
        X = 106,
        Y = 107,
        Z = 108,
        Number0 = 109,
        Number1 = 110,
        Number2 = 111,
        Number3 = 112,
        Number4 = 113,
        Number5 = 114,
        Number6 = 115,
        Number7 = 116,
        Number8 = 117,
        Number9 = 118,
        Minus = 120,
        Plus = 121,
        BracketLeft = 122,
        BracketRight = 123,
        Semicolon = 124,
        Quote = 125,
        Comma = 126,
        Period = 127,
        Slash = 128,
        BackSlash = 129
    }

    /// <summary>
    /// Compatibility modifier flags matching the old OpenTK 3 bit layout.
    /// </summary>
    [System.Flags]
    public enum KeyModifiers
    {
        Alt = 1,
        Control = 2,
        Shift = 4,
        Command = 8
    }

    /// <summary>
    /// Minimal mouse state wrapper kept for the SharpPixels public API.
    /// </summary>
    public sealed class MouseState
    {
        /// <summary>
        /// Initializes a new MouseState instance.
        /// </summary>
        public MouseState(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>
        /// Gets the current mouse x coordinate in window pixel space.
        /// </summary>
        public int X { get; }

        /// <summary>
        /// Gets the current mouse y coordinate in window pixel space.
        /// </summary>
        public int Y { get; }
    }
}
