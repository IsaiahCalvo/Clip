using Clip.Core;

namespace Clip.Tests;

public sealed class LinkDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public LinkDetectionTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path")]
    [InlineData("www.example.com")]
    [InlineData("example.com")]
    [InlineData("hello@example.com")]
    public void AddOrUpdateDetectsLikelyLinks(string text)
    {
        var saved = _store.AddOrUpdate(TextItem(text));

        Assert.Equal(ClipboardItemKind.Link, saved.Kind);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("not.a sentence")]
    [InlineData("example")]
    [InlineData("file.txt")]
    [InlineData("1.2")]
    public void AddOrUpdateDoesNotOverDetectLinks(string text)
    {
        var saved = _store.AddOrUpdate(TextItem(text));

        Assert.Equal(ClipboardItemKind.Text, saved.Kind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ClipboardHistoryItem TextItem(string text)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
            ContentHash = text,
        };
    }
}
