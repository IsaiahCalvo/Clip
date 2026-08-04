using Clip.Core;

namespace Clip.Tests;

public sealed class FileExplorerRevealCoverageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPathHasNoLaunchPlan(string? path)
    {
        Assert.Null(FileExplorerReveal.CreateStartInfo(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRevealReturnsFalseForBlankPathWithoutLaunchingAnything(string? path)
    {
        Assert.False(FileExplorerReveal.TryReveal(path));
    }

    [Fact]
    public void TryRevealReturnsFalseForMissingPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "missing.txt");

        Assert.False(FileExplorerReveal.TryReveal(missing));
    }
}
