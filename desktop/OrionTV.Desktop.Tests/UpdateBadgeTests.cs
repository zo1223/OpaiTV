using System.Net;
using System.Text;
using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

/// <summary>更新小红点：状态来自现有的 DesktopUpdateService，不另写检查或下载逻辑。</summary>
public sealed class UpdateBadgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"opaitv-update-badge-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("1.3.27", "1.3.26", true)]
    [InlineData("1.3.26", "1.3.27", false)]
    [InlineData("1.3.26", "1.3.26", false)]
    // 逐段按数字比，不是按字符串比
    [InlineData("1.3.10", "1.3.9", true)]
    [InlineData("1.3.9", "1.3.10", false)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    // v 前缀、大小写、首尾空白
    [InlineData("v1.3.27", "1.3.26", true)]
    [InlineData("V1.3.27", "v1.3.26", true)]
    [InlineData(" v1.3.27 ", "1.3.26", true)]
    // 段数不同：缺的段按 0
    [InlineData("1.3.1", "1.3", true)]
    [InlineData("1.3", "1.3.0", false)]
    [InlineData("1.3.0", "1.3", false)]
    [InlineData("1.3.26.1", "1.3.26", true)]
    [InlineData("1.3.26", "1.3.26.0", false)]
    // 不是版本号
    [InlineData("", "1.3.26", false)]
    [InlineData(null, "1.3.26", false)]
    [InlineData("latest", "1.3.26", false)]
    [InlineData("1.3.27", "", false)]
    [InlineData("1.3.27-beta", "1.3.26", false)]
    public void IsNewerVersion_compares_versions_numerically(string? candidate, string? current, bool expected)
    {
        Assert.Equal(expected, DesktopUpdateService.IsNewerVersion(candidate, current));
    }

    [Fact]
    public void Badge_is_hidden_before_any_check()
    {
        using var service = new DesktopUpdateService(Client("[]"), _root);

        Assert.False(service.HasUpdateBadge);
        Assert.Null(service.AvailableUpdate);
        Assert.False(service.HasKnownNewerVersion());
    }

    [Fact]
    public async Task Badge_shows_after_a_check_finds_a_newer_release_and_notifies_once()
    {
        using var service = new DesktopUpdateService(Client(Releases("99.0.1")), _root);
        var notifications = 0;
        service.AvailableUpdateChanged += () => notifications++;

        var update = await service.CheckForUpdateAsync();

        Assert.NotNull(update);
        Assert.True(service.HasUpdateBadge);
        Assert.Same(update, service.AvailableUpdate);
        Assert.Equal("99.0.1", update!.VersionText);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Badge_stays_hidden_when_the_latest_release_is_not_newer()
    {
        using var service = new DesktopUpdateService(Client(Releases("0.0.1")), _root);
        var notifications = 0;
        service.AvailableUpdateChanged += () => notifications++;

        Assert.Null(await service.CheckForUpdateAsync());

        Assert.False(service.HasUpdateBadge);
        // 检查完成总会通知，界面据此把红点收起来
        Assert.Equal(1, notifications);
        Assert.False(service.HasKnownNewerVersion());
    }

    [Fact]
    public async Task Badge_clears_when_a_later_check_no_longer_finds_an_update()
    {
        var json = Releases("99.0.1");
        using var service = new DesktopUpdateService(Client(() => json), _root);
        await service.CheckForUpdateAsync();
        Assert.True(service.HasUpdateBadge);
        Assert.True(service.HasKnownNewerVersion());

        json = "[]";
        await service.CheckForUpdateAsync();

        Assert.False(service.HasUpdateBadge);
        Assert.False(service.HasKnownNewerVersion());
    }

    [Fact]
    public async Task Failed_check_keeps_the_previous_badge_state_and_does_not_notify()
    {
        var fail = false;
        using var service = new DesktopUpdateService(
            new HttpClient(new StubHandler(_ => fail
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(Releases("99.0.1")))),
            _root);
        await service.CheckForUpdateAsync();
        var notifications = 0;
        service.AvailableUpdateChanged += () => notifications++;

        fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckForUpdateAsync());

        Assert.True(service.HasUpdateBadge);
        Assert.Equal(0, notifications);
        Assert.True(service.HasKnownNewerVersion());
    }

    [Fact]
    public async Task A_known_newer_version_survives_a_restart_inside_the_check_interval()
    {
        using (var first = new DesktopUpdateService(Client(Releases("99.0.1")), _root))
        {
            await first.CheckForUpdateAsync();
        }

        // 重启：内存里的 AvailableUpdate 没了，12 小时间隔也没到，但知道上次查到过新版本
        using var restarted = new DesktopUpdateService(Client("[]"), _root);

        Assert.False(restarted.IsAutomaticCheckDue());
        Assert.False(restarted.HasUpdateBadge);
        Assert.True(restarted.HasKnownNewerVersion());
    }

    [Fact]
    public void A_known_version_that_is_no_longer_newer_is_ignored()
    {
        // 更新装完之后：记下的版本就是（或老于）当前版本
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "known-update-version.txt"), DesktopUpdateService.CurrentVersionText);
        using var service = new DesktopUpdateService(Client("[]"), _root);

        Assert.False(service.HasKnownNewerVersion());

        File.WriteAllText(Path.Combine(_root, "known-update-version.txt"), "not a version");
        Assert.False(service.HasKnownNewerVersion());
    }

    [Fact]
    public async Task Check_requests_only_the_github_releases_api()
    {
        var requested = new List<Uri>();
        using var service = new DesktopUpdateService(
            new HttpClient(new StubHandler(request =>
            {
                requested.Add(request.RequestUri!);
                return Json(Releases("99.0.1"));
            })),
            _root);

        await service.CheckForUpdateAsync();

        // 小红点不下载任何东西：一次检查只有一个请求，打到 api.github.com
        var uri = Assert.Single(requested);
        Assert.Equal("api.github.com", uri.Host);
        Assert.Empty(Directory.GetFiles(_root, "*.exe", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_root, "*.zip", SearchOption.AllDirectories));
    }

    private static HttpClient Client(string json) => Client(() => json);

    private static HttpClient Client(Func<string> json) => new(new StubHandler(_ => Json(json())));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Releases(string version) => $$"""
        [
          {
            "tag_name": "v{{version}}",
            "draft": false,
            "prerelease": false,
            "body": "notes",
            "published_at": "2026-11-20T10:00:00Z",
            "assets": [
              {
                "name": "OpaiTV-Setup-{{version}}-win-x64.exe",
                "size": 120000000,
                "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v{{version}}/OpaiTV-Setup-{{version}}-win-x64.exe"
              },
              {
                "name": "OpaiTV-Setup-{{version}}-win-x64.exe.sha256",
                "size": 100,
                "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v{{version}}/OpaiTV-Setup-{{version}}-win-x64.exe.sha256"
              }
            ]
          }
        ]
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
