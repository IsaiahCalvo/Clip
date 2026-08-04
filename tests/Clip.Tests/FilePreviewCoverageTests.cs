using System.Security.AccessControl;
using System.Security.Principal;
using Clip.Core;

namespace Clip.Tests;

public sealed class FilePreviewCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public FilePreviewCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TryReadTextExcerptReturnsFalseWhenFileIsLocked()
    {
        var path = Path.Combine(_root, "locked.txt");
        File.WriteAllText(path, "cannot be read while locked");

        using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(FilePreview.TryReadTextExcerpt(new[] { path }, 2_000, out var excerpt));
        Assert.Equal(string.Empty, excerpt);
    }

    [Fact]
    public void TryReadTextExcerptReturnsFalseWhenReadAccessIsDenied()
    {
        var path = Path.Combine(_root, "denied.txt");
        File.WriteAllText(path, "secret");
        var user = WindowsIdentity.GetCurrent().User!;
        var file = new FileInfo(path);
        // Deny only ReadData so File.Exists (which reads attributes) still sees the file, but
        // opening it for reading throws UnauthorizedAccessException.
        var denyRead = new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny);
        var security = file.GetAccessControl();
        security.AddAccessRule(denyRead);
        file.SetAccessControl(security);

        try
        {
            Assert.False(FilePreview.TryReadTextExcerpt(new[] { path }, 2_000, out var excerpt));
            Assert.Equal(string.Empty, excerpt);
        }
        finally
        {
            security = file.GetAccessControl();
            security.RemoveAccessRule(denyRead);
            file.SetAccessControl(security);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
