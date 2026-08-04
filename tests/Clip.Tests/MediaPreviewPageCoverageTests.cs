using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The media element's type attribute is what lets the browser pick a decoder without probing the
/// stream, so every extension the player accepts must map to its real MIME type — and anything
/// unknown must degrade to octet-stream rather than lying.
/// </summary>
public sealed class MediaPreviewPageCoverageTests
{
    [Theory]
    [InlineData(".mp4", "video/mp4", true)]
    [InlineData(".m4v", "video/mp4", true)]
    [InlineData(".webm", "video/webm", true)]
    [InlineData(".ogv", "video/ogg", true)]
    [InlineData(".mov", "video/quicktime", true)]
    [InlineData(".MOV", "video/quicktime", true)] // extension case must not matter
    [InlineData(".mkv", "video/x-matroska", true)]
    [InlineData(".avi", "video/x-msvideo", true)]
    [InlineData(".mp3", "audio/mpeg", false)]
    [InlineData(".m4a", "audio/mp4", false)]
    [InlineData(".wav", "audio/wav", false)]
    [InlineData(".ogg", "audio/ogg", false)]
    [InlineData(".oga", "audio/ogg", false)]
    [InlineData(".flac", "audio/flac", false)]
    [InlineData(".aac", "audio/aac", false)]
    [InlineData(".wma", "audio/x-ms-wma", false)]
    [InlineData(".xyz", "application/octet-stream", true)]
    public void EveryPlayableExtensionDeclaresItsRealMimeType(string extension, string mime, bool isVideo)
    {
        var html = MediaPreviewPage.Build(
            $@"C:\media\sample{extension}",
            "https://clip-preview.local/sample",
            isVideo,
            backgroundHex: "#101010",
            textHex: "#f0f0f0");

        Assert.Contains($"type=\"{mime}\"", html);
    }
}
