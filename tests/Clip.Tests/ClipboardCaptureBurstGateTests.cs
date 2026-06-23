using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardCaptureBurstGateTests
{
    [Fact]
    public void ShouldSkipSuppressesSameFingerprintInsideWindow()
    {
        var gate = new ClipboardCaptureBurstGate(TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;

        Assert.False(gate.ShouldSkip("Image:abc", now));
        Assert.True(gate.ShouldSkip("Image:abc", now.AddMilliseconds(250)));
        Assert.False(gate.ShouldSkip("Image:def", now.AddMilliseconds(300)));
    }

    [Fact]
    public void ShouldSkipAllowsSameFingerprintAfterWindow()
    {
        var gate = new ClipboardCaptureBurstGate(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.UtcNow;

        Assert.False(gate.ShouldSkip("Files:abc", now));
        Assert.False(gate.ShouldSkip("Files:abc", now.AddSeconds(1)));
    }
}
