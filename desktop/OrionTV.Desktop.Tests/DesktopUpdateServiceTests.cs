using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class DesktopUpdateServiceTests
{
    [Fact]
    public void SelectUpdateFromJson_selects_newest_checksummed_stable_installer()
    {
        var json = """
            [
              {
                "tag_name": "v1.3.16",
                "draft": false,
                "prerelease": false,
                "body": "Latest fixes",
                "published_at": "2026-07-27T10:00:00Z",
                "assets": [
                  {
                    "name": "OpaiTV-Setup-1.3.16-win-x64.exe",
                    "size": 120000000,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe"
                  },
                  {
                    "name": "OpaiTV-Setup-1.3.16-win-x64.exe.sha256",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe.sha256"
                  }
                ]
              },
              {
                "tag_name": "v1.3.15",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "OpaiTV-Setup-1.3.15-win-x64.exe",
                    "size": 110000000,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v1.3.15/OpaiTV-Setup-1.3.15-win-x64.exe"
                  },
                  {
                    "name": "OpaiTV-Setup-1.3.15-win-x64.exe.sha256",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v1.3.15/OpaiTV-Setup-1.3.15-win-x64.exe.sha256"
                  }
                ]
              }
            ]
            """;

        var update = DesktopUpdateService.SelectUpdateFromJson(json, new Version(1, 3, 14, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 16, 0), update!.Version);
        Assert.Equal("1.3.16", update.VersionText);
        Assert.Equal("Latest fixes", update.ReleaseNotes);
    }

    [Fact]
    public void SelectUpdateFromJson_rejects_prerelease_mismatched_or_unverified_assets()
    {
        var json = """
            [
              {
                "tag_name": "v2.0.0",
                "draft": false,
                "prerelease": true,
                "assets": [
                  {
                    "name": "OpaiTV-Setup-2.0.0-win-x64.exe",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v2.0.0/OpaiTV-Setup-2.0.0-win-x64.exe"
                  },
                  {
                    "name": "OpaiTV-Setup-2.0.0-win-x64.exe.sha256",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v2.0.0/OpaiTV-Setup-2.0.0-win-x64.exe.sha256"
                  }
                ]
              },
              {
                "tag_name": "v1.4.0",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "OpaiTV-Setup-1.4.1-win-x64.exe",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OrionTV/releases/download/v1.4.0/OpaiTV-Setup-1.4.1-win-x64.exe"
                  }
                ]
              }
            ]
            """;

        var update = DesktopUpdateService.SelectUpdateFromJson(json, new Version(1, 3, 14));

        Assert.Null(update);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789ABCDEF  setup.exe")]
    [InlineData("sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void TryParseChecksum_accepts_standard_sha256_files(string value)
    {
        Assert.True(DesktopUpdateService.TryParseChecksum(value, out var checksum));
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", checksum);
    }

    [Fact]
    public void CreateConfigurationBackup_preserves_user_video_sources()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-update-test-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "data");
        var backupDirectory = Path.Combine(root, "backups");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "settings.json"), "{\"M3uUrl\":\"https://example.com/live.m3u\"}");
        File.WriteAllText(Path.Combine(dataDirectory, "extra_sources.json"), "{\"sources\":[{\"name\":\"Custom\"}]}");

        try
        {
            var backup = DesktopUpdateService.CreateConfigurationBackup(dataDirectory, backupDirectory, "1.3.15");

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
            using var archive = ZipFile.OpenRead(backup!);
            Assert.Equal(
                ["extra_sources.json", "settings.json"],
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_streams_and_verifies_installer_in_isolated_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-update-download-test-" + Guid.NewGuid().ToString("N"));
        var installerBytes = Encoding.UTF8.GetBytes("verified installer payload");
        var checksum = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksum + "  OpaiTV-Setup-1.3.16-win-x64.exe", Encoding.ASCII),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installerBytes),
            };
        }));
        using var service = new DesktopUpdateService(client, root);
        var update = new DesktopUpdateInfo(
            new Version(1, 3, 16, 0),
            "1.3.16",
            "v1.3.16",
            "OpaiTV-Setup-1.3.16-win-x64.exe",
            new Uri("https://github.com/zo1223/OrionTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe"),
            installerBytes.Length,
            new Uri("https://github.com/zo1223/OrionTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe.sha256"),
            "",
            null);
        var progressValues = new List<int>();

        try
        {
            var path = await service.DownloadAndVerifyAsync(update, new InlineProgress(progressValues.Add));

            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(100, progressValues[^1]);
            Assert.False(File.Exists(path + ".download"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_deletes_partial_file_when_checksum_is_wrong()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-update-hash-test-" + Guid.NewGuid().ToString("N"));
        var installerBytes = Encoding.UTF8.GetBytes("untrusted installer payload");
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent(new string('0', 64), Encoding.ASCII)
                    : new ByteArrayContent(installerBytes),
            }));
        using var service = new DesktopUpdateService(client, root);
        var update = new DesktopUpdateInfo(
            new Version(1, 3, 16, 0),
            "1.3.16",
            "v1.3.16",
            "OpaiTV-Setup-1.3.16-win-x64.exe",
            new Uri("https://github.com/zo1223/OrionTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe"),
            installerBytes.Length,
            new Uri("https://github.com/zo1223/OrionTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe.sha256"),
            "",
            null);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndVerifyAsync(update));
            Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*.download", SearchOption.AllDirectories) : []);
            Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories) : []);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
