using Clip.Core;

namespace Clip.Tests;

public sealed class FileExplorerRevealTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FilePathLaunchPlanSelectsTheFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");

        var startInfo = FileExplorerReveal.CreateStartInfo(path);

        Assert.NotNull(startInfo);
        Assert.Equal("explorer.exe", startInfo!.FileName);
        Assert.Equal($"/select,\"{path}\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void FolderPathLaunchPlanOpensTheFolder()
    {
        Directory.CreateDirectory(_root);

        var startInfo = FileExplorerReveal.CreateStartInfo(_root);

        Assert.NotNull(startInfo);
        Assert.Equal("explorer.exe", startInfo!.FileName);
        Assert.Equal($"\"{_root}\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void MissingPathHasNoLaunchPlan()
    {
        var startInfo = FileExplorerReveal.CreateStartInfo(Path.Combine(_root, "missing.txt"));

        Assert.Null(startInfo);
    }

    [Fact]
    public void RevealTargetUsesExistingTextPath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = $"\"{path}\"",
            Preview = path,
        };

        var target = ClipboardItemRevealTarget.GetPath(item);

        Assert.Equal(path, target);
    }

    [Fact]
    public void RevealTargetUsesFirstCopiedFilePath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = "invoice.pdf",
        };

        var target = ClipboardItemRevealTarget.GetPath(item);

        Assert.Equal(path, target);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
