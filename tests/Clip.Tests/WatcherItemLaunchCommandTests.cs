using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardItemLaunchCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.ItemLaunch.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenStartInfoUsesCopiedFilePath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = "invoice.pdf"
        };

        var startInfo = ClipboardItemLaunchCommand.CreateOpenStartInfo(item, appPath: null);

        Assert.NotNull(startInfo);
        Assert.Equal(path, startInfo!.FileName);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void OpenStartInfoCanUseSpecificAppForFilePath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "note.txt");
        File.WriteAllText(path, "hello");
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = path,
            Preview = path
        };

        var startInfo = ClipboardItemLaunchCommand.CreateOpenStartInfo(item, @"C:\Windows\notepad.exe");

        Assert.NotNull(startInfo);
        Assert.Equal(@"C:\Windows\notepad.exe", startInfo!.FileName);
        Assert.Equal($"\"{path}\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void OpenStartInfoNormalizesLinkText()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Link,
            Text = "example.com",
            Preview = "example.com"
        };

        var startInfo = ClipboardItemLaunchCommand.CreateOpenStartInfo(item, appPath: null);

        Assert.NotNull(startInfo);
        Assert.Equal("https://example.com", startInfo!.FileName);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void RevealStartInfoUsesExplorerSelection()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = $"\"{path}\"",
            Preview = path
        };

        var startInfo = ClipboardItemLaunchCommand.CreateRevealStartInfo(item);

        Assert.NotNull(startInfo);
        Assert.Equal("explorer.exe", startInfo!.FileName);
        Assert.Equal($"/select,\"{path}\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
