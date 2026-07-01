using Clip.Core;

namespace Clip.Tests;

public sealed class WindowsClipboardHistoryTests
{
    [Theory]
    [InlineData(null, null, true)]  // never set, no policy -> enable
    [InlineData(0, null, true)]     // explicitly off, no policy -> enable
    [InlineData(1, null, false)]    // already on -> leave it
    [InlineData(null, 1, true)]     // policy allows -> enable
    [InlineData(null, 0, false)]    // policy forces off -> can't enable
    [InlineData(1, 0, false)]       // policy off wins even over an existing on value
    public void ShouldEnable_decides_correctly(int? current, int? policy, bool expected)
    {
        Assert.Equal(expected, WindowsClipboardHistory.ShouldEnable(current, policy));
    }
}
