using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardPathTextCoverageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTextIsNotAFilePathList(string? text)
    {
        var parsed = ClipboardPathText.TryParseExistingFilePaths(text, out var paths);

        Assert.False(parsed);
        Assert.Empty(paths);
    }
}
