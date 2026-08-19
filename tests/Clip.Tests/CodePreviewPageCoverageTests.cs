using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// Fills the highlighting gaps: the language arms not exercised elsewhere, whole-line comments,
/// and number colouring. Files are written to a hermetic temp dir because Build reads from disk.
/// </summary>
public sealed class CodePreviewPageCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public CodePreviewPageCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    [Theory]
    [InlineData(".xml")]
    [InlineData(".xaml")]
    [InlineData(".csproj")]
    [InlineData(".svg")]
    [InlineData(".css")]
    [InlineData(".scss")]
    [InlineData(".c")]
    [InlineData(".go")]
    [InlineData(".rs")]
    [InlineData(".kt")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ini")]
    [InlineData(".toml")]
    [InlineData(".env")]
    public void EveryMappedExtensionCountsAsCode(string extension) =>
        Assert.True(CodePreviewPage.IsCodeFile(extension));

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData("")]
    public void UnmappedExtensionsStayPlainText(string extension) =>
        Assert.False(CodePreviewPage.IsCodeFile(extension));

    [Theory]
    [InlineData(".xml", "xml")]
    [InlineData(".css", "css")]
    [InlineData(".go", "code")]
    [InlineData(".cmd", "shell")]
    [InlineData(".toml", "config")]
    public void ThePageHeaderNamesTheLanguage(string extension, string language)
    {
        var html = Build($"sample{extension}", "content");

        Assert.Contains($"<span>{language}</span>", html);
    }

    [Theory]
    [InlineData(".py", "# a whole-line comment")]
    [InlineData(".cs", "// a whole-line comment")]
    [InlineData(".sql", "-- a whole-line comment")]
    [InlineData(".cs", "* doc continuation line")]
    public void WholeLineCommentsAreColouredAsOneSpan(string extension, string comment)
    {
        var html = Build($"commented{extension}", comment + "\ncode = 1\n");

        Assert.Contains($"<span class=\"c\">{comment}</span>", html);
    }

    [Fact]
    public void NumbersStringsAndKeywordsEachGetTheirColour()
    {
        var html = Build("values.py", "answer = 42.5\nname = 'clip'\nreturn answer\n");

        Assert.Contains("<span class=\"n\">42.5</span>", html);
        Assert.Contains("<span class=\"s\">&#39;clip&#39;</span>", html);
        Assert.Contains("<span class=\"k\">return</span>", html);
    }

    private string Build(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return CodePreviewPage.Build(path, "#1e1e1e", "#ffffff", "#9a9a9a", "#569cd6");
    }
}
