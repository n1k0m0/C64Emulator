// SPDX-License-Identifier: Apache-2.0
// Headless test adapter only. SID synthesis/timing remain the real emulator code.
namespace C64Emulator.Core;
public sealed class SidAudioOutput : System.IDisposable
{
    public SidAudioOutput(int sampleRate) { }
    public void Write(byte[] buffer, int count) { }
    public void Dispose() { }
}
