// SPDX-License-Identifier: Apache-2.0
// Only the host audio output is replaced. SID synthesis and all C64 chips
// execute the unchanged upstream implementation in the headless harness.
namespace C64Emulator.Core;
public sealed class SidAudioOutput : IDisposable
{
    public SidAudioOutput(int sampleRate) { }
    public void Write(byte[] buffer, int count) { }
    public void Dispose() { }
}
