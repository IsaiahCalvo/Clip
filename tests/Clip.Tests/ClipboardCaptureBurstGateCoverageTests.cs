using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardCaptureBurstGateCoverageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldSkipNeverSuppressesBlankFingerprints(string? fingerprint)
    {
        var gate = new ClipboardCaptureBurstGate(TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;

        Assert.False(gate.ShouldSkip(fingerprint, now));
        Assert.False(gate.ShouldSkip(fingerprint, now.AddMilliseconds(10)));
    }

    [Fact]
    public void BlankFingerprintDoesNotClobberRememberedFingerprint()
    {
        var gate = new ClipboardCaptureBurstGate(TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;

        Assert.False(gate.ShouldSkip("Text:abc", now));
        Assert.False(gate.ShouldSkip(null, now.AddMilliseconds(10)));
        Assert.True(gate.ShouldSkip("Text:abc", now.AddMilliseconds(20)));
    }
}
