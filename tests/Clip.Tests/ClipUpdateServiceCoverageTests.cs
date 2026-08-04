using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Clip.Shell;

namespace Clip.Tests;

public sealed class ClipUpdateServiceCoverageTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private static ClipUpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)), "https://example.invalid/releases/latest");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void CurrentVersionIsCleanAndParseable()
    {
        var version = ClipUpdateService.CurrentVersion;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain("+", version);
        Assert.False(version.StartsWith('v'));
        Assert.True(Version.TryParse(version, out _));
    }

    [Fact]
    public void NotCheckedStatusCarriesCurrentVersion()
    {
        var status = ClipUpdateStatus.NotChecked("1.2.3");

        Assert.Equal("Not checked", status.State);
        Assert.Equal("1.2.3", status.CurrentVersion);
        Assert.Null(status.LatestVersion);
        Assert.Null(status.DownloadUrl);
    }

    [Fact]
    public async Task CheckReportsFailureStatusCode()
    {
        var service = Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var status = await service.CheckAsync();

        Assert.Equal("Check failed", status.State);
        Assert.Contains("500", status.Message);
    }

    [Fact]
    public async Task CheckReportsMissingVersion()
    {
        var service = Service(_ => Json("{}"));

        var status = await service.CheckAsync();

        Assert.Equal("Check failed", status.State);
        Assert.Contains("Could not read the latest release version", status.Message);
    }

    [Fact]
    public async Task CheckReportsExceptionsAsFailure()
    {
        var service = new ClipUpdateService(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("boom"))),
            "https://example.invalid/releases/latest");

        var status = await service.CheckAsync();

        Assert.Equal("Check failed", status.State);
        Assert.Contains("boom", status.Message);
    }

    [Fact]
    public async Task CheckFindsNewerReleaseAndPicksInstallerAsset()
    {
        var service = Service(_ => Json("""
            {
              "tag_name": "v999.0.0",
              "html_url": "https://example.invalid/rel",
              "assets": [
                { "browser_download_url": "https://example.invalid/readme.txt" },
                { "browser_download_url": "https://example.invalid/Clip.zip" }
              ]
            }
            """));

        var status = await service.CheckAsync();

        Assert.Equal("Update available", status.State);
        Assert.Equal("999.0.0", status.LatestVersion);
        Assert.Equal("https://example.invalid/rel", status.ReleaseUrl);
        Assert.Equal("https://example.invalid/Clip.zip", status.DownloadUrl);
    }

    [Fact]
    public async Task CheckFallsBackToReleaseNameWhenTagMissing()
    {
        var service = Service(_ => Json("""{ "name": "v999.0.0" }"""));

        var status = await service.CheckAsync();

        Assert.Equal("Update available", status.State);
        Assert.Equal("999.0.0", status.LatestVersion);
        Assert.Null(status.DownloadUrl);
    }

    [Fact]
    public async Task CheckReportsUpToDateForOldRelease()
    {
        var service = Service(_ => Json("""{ "tag_name": "v0.0.0" }"""));

        var status = await service.CheckAsync();

        Assert.Equal("Up to date", status.State);
        Assert.Equal("0.0.0", status.LatestVersion);
    }

    [Fact]
    public async Task DownloadReturnsNullWithoutDownloadUrl()
    {
        var service = Service(_ => throw new InvalidOperationException("must not be called"));
        var status = ClipUpdateStatus.NotChecked("1.0.0");

        Assert.Null(await service.DownloadUpdateAsync(status));
    }

    [Fact]
    public async Task DownloadWritesAssetToTempUpdateFolder()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var service = Service(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var latest = $"9.9.{Random.Shared.Next(1000, 9999)}";
        var status = new ClipUpdateStatus(
            "Update available",
            "msg",
            "1.0.0",
            latest,
            DownloadUrl: "https://example.invalid/Clip-setup.zip");

        var target = await service.DownloadUpdateAsync(status);

        try
        {
            Assert.NotNull(target);
            Assert.EndsWith($"Clip-{latest}.zip", target);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target!));
        }
        finally
        {
            if (target is not null && File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    [Fact]
    public void LaunchInstallerReturnsFalseForMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Clip.Tests", $"{Guid.NewGuid():N}.exe");

        Assert.False(ClipUpdateService.LaunchInstaller(missing, Path.GetTempPath(), processId: 0));
    }
}
