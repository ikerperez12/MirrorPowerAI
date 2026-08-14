using System.Runtime.InteropServices;
using MirrorPowerAI.Core.Audio;

namespace MirrorPowerAI.Core.Tests;

public sealed class CapturedAudioTests
{
    [Fact]
    public void Dispose_Always_ZeroesOwnedAudioBuffer()
    {
        var audio = new CapturedAudio(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromSeconds(1));
        Assert.True(MemoryMarshal.TryGetArray(audio.WavData, out var buffer));

        audio.Dispose();

        Assert.All(buffer.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Constructor_NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedAudio(Array.Empty<byte>(), TimeSpan.FromSeconds(-1)));
    }
}
