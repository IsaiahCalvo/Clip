using Clip.Core;

namespace Clip.Tests;

public sealed class PeriodicWorkThrottleTests
{
    [Fact]
    public void TryBeginAllowsFirstAttempt()
    {
        var throttle = new PeriodicWorkThrottle(TimeSpan.FromMinutes(5));

        Assert.True(throttle.TryBegin(new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TryBeginBlocksAttemptsInsideInterval()
    {
        var throttle = new PeriodicWorkThrottle(TimeSpan.FromMinutes(5));
        var start = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

        Assert.True(throttle.TryBegin(start));
        Assert.False(throttle.TryBegin(start.AddMinutes(4)));
    }

    [Fact]
    public void TryBeginAllowsAttemptsAfterInterval()
    {
        var throttle = new PeriodicWorkThrottle(TimeSpan.FromMinutes(5));
        var start = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

        Assert.True(throttle.TryBegin(start));
        Assert.True(throttle.TryBegin(start.AddMinutes(5)));
    }

    [Fact]
    public void TryBeginBlocksWhenClockMovesBack()
    {
        var throttle = new PeriodicWorkThrottle(TimeSpan.FromMinutes(5));
        var start = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

        Assert.True(throttle.TryBegin(start));
        Assert.False(throttle.TryBegin(start.AddMinutes(-1)));
    }

    [Fact]
    public void ConstructorRejectsNegativeInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodicWorkThrottle(TimeSpan.FromMilliseconds(-1)));
    }
}
