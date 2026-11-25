using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class PlaybackProxyTests
{
    private static readonly Uri Media = new("https://media.example/vod/a/index.m3u8");
    private static readonly Uri LocalCache = new("http://127.0.0.1:53124/vod/a/index.m3u8");

    private static PlaybackProxyDecision Forced(string address = "http://127.0.0.1:7890") =>
        PlaybackProxyDecision.From(PlaybackProxyModes.ForcedProxy, address);

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("system", "system")]
    [InlineData(" Forced ", "forced")]
    [InlineData("DIRECT", "direct")]
    [InlineData("socks", "system")]
    public void Normalize_UnknownOrMissingModeIsSystemProxy(string? input, string expected)
    {
        Assert.Equal(expected, PlaybackProxyModes.Normalize(input));
    }

    [Theory]
    [InlineData("127.0.0.1:7890", "127.0.0.1", 7890)]
    [InlineData("localhost:7890", "localhost", 7890)]
    [InlineData("http://127.0.0.1:7890", "127.0.0.1", 7890)]
    [InlineData("  http://proxy.example:8080/  ", "proxy.example", 8080)]
    [InlineData("http://user:pass@proxy.example:3128", "proxy.example", 3128)]
    [InlineData("[::1]:7890", "[::1]", 7890)]
    public void TryParse_AcceptsHttpProxyAddresses(string input, string host, int port)
    {
        Assert.True(PlaybackProxyAddress.TryParse(input, out var uri));
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(host, uri.Host);
        Assert.Equal(port, uri.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("proxy.example")]
    [InlineData("http://proxy.example")]
    [InlineData("[::1]")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("https://proxy.example:443")]
    [InlineData("https://relay.example/?url=")]
    [InlineData("http://relay.example:8080/?url=")]
    [InlineData("http://proxy.example:8080/path")]
    [InlineData("http://proxy.example:0")]
    [InlineData("http://proxy.example:99999")]
    [InlineData("127.0.0.1 7890")]
    public void TryParse_RejectsAnythingThatIsNotAnHttpForwardProxy(string? input)
    {
        Assert.False(PlaybackProxyAddress.TryParse(input, out _));
    }

    [Fact]
    public void Describe_HidesProxyCredentials()
    {
        Assert.True(PlaybackProxyAddress.TryParse("http://user:secret@proxy.example:3128", out var uri));
        Assert.Equal("http://proxy.example:3128", PlaybackProxyAddress.Describe(uri));
    }

    [Fact]
    public void Decision_MapsTheThreeModes()
    {
        var system = PlaybackProxyDecision.From("system", "127.0.0.1:7890");
        Assert.Equal(PlaybackProxyModes.SystemProxy, system.Mode);
        Assert.Null(system.ProxyUri);

        var forced = PlaybackProxyDecision.From("forced", "127.0.0.1:7890");
        Assert.Equal(PlaybackProxyModes.ForcedProxy, forced.Mode);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), forced.ProxyUri);

        var direct = PlaybackProxyDecision.From("direct", "127.0.0.1:7890");
        Assert.Equal(PlaybackProxyModes.Direct, direct.Mode);
        Assert.Null(direct.ProxyUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a proxy")]
    [InlineData("socks5://127.0.0.1:1080")]
    public void Decision_ForcedWithoutUsableAddressFallsBackToSystem(string address)
    {
        Assert.Equal(PlaybackProxyModes.SystemProxy, PlaybackProxyDecision.From("forced", address).Mode);
        Assert.Equal(PlaybackProxyModes.SystemProxy, PlaybackProxyModes.NormalizeForSettings("forced", address));
        Assert.Equal(PlaybackProxyModes.ForcedProxy, PlaybackProxyModes.NormalizeForSettings("forced", "127.0.0.1:7890"));
    }

    [Fact]
    public void Decision_NullSettingsIsSystemProxy()
    {
        Assert.Equal(PlaybackProxyModes.SystemProxy, PlaybackProxyDecision.From(null).Mode);
    }

    // ---- 设置：持久化、迁移、保存规则 ----

    [Fact]
    public void Settings_DefaultAndOldFilesAreSystemProxy()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var defaults = new DesktopSettings();
        Assert.Equal("system", defaults.PlaybackProxyMode);
        Assert.Equal("", defaults.PlaybackProxyUrl);

        // 旧设置文件没有这两个字段
        var old = JsonSerializer.Deserialize<DesktopSettings>("""{"SubscriptionUrl":"https://sub.example/list.json"}""", options)!;
        var migrated = DesktopSettingsMigration.Apply(old);
        Assert.Equal("system", migrated.PlaybackProxyMode);
        Assert.Equal("", migrated.PlaybackProxyUrl);
        Assert.Equal(PlaybackProxyModes.SystemProxy, PlaybackProxyDecision.From(migrated).Mode);
    }

    [Fact]
    public void Settings_RoundTripKeepsModeAndAddress()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var saved = new DesktopSettings(PlaybackProxyMode: "forced", PlaybackProxyUrl: "http://127.0.0.1:7890");
        var loaded = DesktopSettingsMigration.Apply(
            JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(saved, options), options)!);

        Assert.Equal("forced", loaded.PlaybackProxyMode);
        Assert.Equal("http://127.0.0.1:7890", loaded.PlaybackProxyUrl);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), PlaybackProxyDecision.From(loaded).ProxyUri);
    }

    [Fact]
    public void Migration_RepairsDamagedValues()
    {
        var damaged = DesktopSettingsMigration.Apply(
            new DesktopSettings(PlaybackProxyMode: "FORCED", PlaybackProxyUrl: "  "));
        Assert.Equal("system", damaged.PlaybackProxyMode);
        Assert.Equal("", damaged.PlaybackProxyUrl);

        var unknown = DesktopSettingsMigration.Apply(
            new DesktopSettings(PlaybackProxyMode: "tor", PlaybackProxyUrl: " 127.0.0.1:7890 "));
        Assert.Equal("system", unknown.PlaybackProxyMode);
        Assert.Equal("127.0.0.1:7890", unknown.PlaybackProxyUrl);

        var direct = DesktopSettingsMigration.Apply(new DesktopSettings(PlaybackProxyMode: " Direct "));
        Assert.Equal("direct", direct.PlaybackProxyMode);
    }

    [Fact]
    public void TryApply_RefusesForcedModeWithoutUsableAddress()
    {
        var current = new DesktopSettings(PlaybackProxyMode: "direct", PlaybackProxyUrl: "");

        Assert.False(PlaybackProxySettings.TryApply(current, "forced", "", out var unchanged));
        Assert.Same(current, unchanged);
        Assert.False(PlaybackProxySettings.TryApply(current, "forced", "socks5://127.0.0.1:1080", out unchanged));
        Assert.Same(current, unchanged);
    }

    [Fact]
    public void TryApply_SavesModeAndKeepsAddressAcrossModes()
    {
        var current = new DesktopSettings();

        Assert.True(PlaybackProxySettings.TryApply(current, "forced", " 127.0.0.1:7890 ", out var forced));
        Assert.Equal("forced", forced.PlaybackProxyMode);
        Assert.Equal("127.0.0.1:7890", forced.PlaybackProxyUrl);

        // 切到直连 / 系统代理时地址留着，切回强制代理不用重填
        Assert.True(PlaybackProxySettings.TryApply(forced, "direct", forced.PlaybackProxyUrl, out var direct));
        Assert.Equal("direct", direct.PlaybackProxyMode);
        Assert.Equal("127.0.0.1:7890", direct.PlaybackProxyUrl);
        Assert.Null(PlaybackProxyDecision.From(direct).ProxyUri);

        Assert.True(PlaybackProxySettings.TryApply(direct, "system", direct.PlaybackProxyUrl, out var system));
        Assert.Equal("system", system.PlaybackProxyMode);
    }

    [Fact]
    public void SaveSettingsButton_DoesNotOverwriteProxyMode()
    {
        var current = new DesktopSettings(PlaybackProxyMode: "forced", PlaybackProxyUrl: "http://127.0.0.1:7890");

        var saved = DesktopSettingsForm.ApplyEditableFields(
            current,
            subscriptionUrl: "https://sub.example/list.json",
            apiBaseUrl: "",
            m3uUrl: "",
            autoSkipAds: false);

        Assert.Equal("forced", saved.PlaybackProxyMode);
        Assert.Equal("http://127.0.0.1:7890", saved.PlaybackProxyUrl);
    }

    // ---- LibVLC 选项 ----

    [Fact]
    public void LibVlcOptions_OnlyForcedModeAddsHttpProxy()
    {
        Assert.Equal([":http-proxy=http://127.0.0.1:7890"], Forced().LibVlcOptionsFor(Media));
        Assert.Empty(PlaybackProxyDecision.FollowSystem.LibVlcOptionsFor(Media));
        Assert.Empty(PlaybackProxyDecision.NoProxy.LibVlcOptionsFor(Media));
    }

    [Fact]
    public void LibVlcOptions_LocalCacheAndFilesNeverUseTheProxy()
    {
        Assert.Empty(Forced().LibVlcOptionsFor(LocalCache));
        Assert.Empty(Forced().LibVlcOptionsFor(new Uri("http://localhost:8080/a.m3u8")));
        Assert.Empty(Forced().LibVlcOptionsFor(new Uri("file:///C:/video/a.mp4")));
        Assert.Equal([":file-caching=1000"], PlaybackMediaOptions.ForUri(new Uri("file:///C:/video/a.mp4"), 1500, Forced()));
    }

    [Fact]
    public void MediaOptions_SystemAndDirectAreIdenticalToTheOriginalOptions()
    {
        string[] original =
        [
            $":http-user-agent={PlaybackMediaOptions.AndroidLikeUserAgent}",
            ":http-referrer=https://media.example/",
            ":http-reconnect",
            ":network-caching=1500",
        ];

        Assert.Equal(original, PlaybackMediaOptions.ForUri(Media, 1500, PlaybackProxyDecision.FollowSystem));
        Assert.Equal(original, PlaybackMediaOptions.ForUri(Media, 1500, PlaybackProxyDecision.NoProxy));
        Assert.Equal([.. original, ":http-proxy=http://127.0.0.1:7890"], PlaybackMediaOptions.ForUri(Media, 1500, Forced()));
        // 没有接线（默认）时等同系统代理
        Assert.Equal(original, PlaybackMediaOptions.ForUri(Media));
    }

    // ---- HttpClient 用的代理对象 ----

    private sealed class ThrowingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => throw new InvalidOperationException("system proxy must not be consulted");
        public bool IsBypassed(Uri host) => throw new InvalidOperationException("system proxy must not be consulted");
    }

    [Fact]
    public void WebProxy_SystemModeDelegatesToTheSystemProxy()
    {
        var system = new WebProxy("http://sys-proxy.example:3128") { BypassList = ["intranet\\.example"] };
        var proxy = new PlaybackWebProxy(() => PlaybackProxyDecision.FollowSystem, () => system);

        Assert.Equal(new Uri("http://sys-proxy.example:3128"), proxy.GetProxy(Media));
        Assert.False(proxy.IsBypassed(Media));

        // 系统代理自己的例外名单照样生效
        var bypassed = new Uri("http://intranet.example/a.m3u8");
        Assert.Null(proxy.GetProxy(bypassed));
        Assert.True(proxy.IsBypassed(bypassed));
    }

    [Fact]
    public void WebProxy_SystemModeWithoutSystemProxyIsDirect()
    {
        var proxy = new PlaybackWebProxy(() => PlaybackProxyDecision.FollowSystem, () => null);

        Assert.Null(proxy.GetProxy(Media));
        Assert.True(proxy.IsBypassed(Media));
    }

    [Fact]
    public void WebProxy_ForcedModeUsesTheUserAddressAndIgnoresTheSystemProxy()
    {
        var proxy = new PlaybackWebProxy(() => Forced("http://10.0.0.2:8118"), () => new ThrowingProxy());

        Assert.Equal(new Uri("http://10.0.0.2:8118"), proxy.GetProxy(Media));
        Assert.False(proxy.IsBypassed(Media));
    }

    [Fact]
    public void WebProxy_DirectModeNeverUsesAProxy()
    {
        var proxy = new PlaybackWebProxy(() => PlaybackProxyDecision.NoProxy, () => new ThrowingProxy());

        Assert.Null(proxy.GetProxy(Media));
        Assert.True(proxy.IsBypassed(Media));
        Assert.Null(proxy.GetCredential(Media, "Basic"));
    }

    [Fact]
    public void WebProxy_LoopbackIsAlwaysDirect()
    {
        foreach (var decision in new[] { PlaybackProxyDecision.FollowSystem, Forced(), PlaybackProxyDecision.NoProxy })
        {
            var proxy = new PlaybackWebProxy(() => decision, () => new WebProxy("http://sys-proxy.example:3128"));
            Assert.Null(proxy.GetProxy(LocalCache));
            Assert.True(proxy.IsBypassed(LocalCache));
        }
    }

    [Fact]
    public void WebProxy_FollowsModeChangesWithoutBeingRecreated()
    {
        var decision = PlaybackProxyDecision.FollowSystem;
        var proxy = new PlaybackWebProxy(() => decision, () => new WebProxy("http://sys-proxy.example:3128"));

        Assert.Equal(new Uri("http://sys-proxy.example:3128"), proxy.GetProxy(Media));

        decision = Forced();
        Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.GetProxy(Media));

        decision = PlaybackProxyDecision.NoProxy;
        Assert.Null(proxy.GetProxy(Media));

        decision = PlaybackProxyDecision.FollowSystem;
        Assert.Equal(new Uri("http://sys-proxy.example:3128"), proxy.GetProxy(Media));
    }

    [Fact]
    public void WebProxy_ForcedCredentialsComeFromTheAddress()
    {
        var withUser = new PlaybackWebProxy(() => Forced("http://alice:p%40ss@proxy.example:3128"), () => null);
        var credential = withUser.GetCredential(new Uri("http://proxy.example:3128"), "Basic");
        Assert.NotNull(credential);
        Assert.Equal("alice", credential!.UserName);
        Assert.Equal("p@ss", credential.Password);

        var anonymous = new PlaybackWebProxy(() => Forced(), () => null);
        Assert.Null(anonymous.GetCredential(new Uri("http://127.0.0.1:7890"), "Basic"));
    }

    // ---- 真实 HttpClient：请求确实按模式送到对应的代理 ----

    private sealed class FakeProxyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public List<string> RequestLines { get; } = [];
        public int Port { get; }

        public FakeProxyServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read <= 0)
                        {
                            break;
                        }

                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    lock (RequestLines)
                    {
                        RequestLines.Add(head.ToString().Split("\r\n")[0]);
                    }

                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch
            {
                // listener stopped
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task HttpClient_RoutesEachRequestByTheCurrentMode()
    {
        using var systemProxyServer = new FakeProxyServer();
        using var forcedProxyServer = new FakeProxyServer();
        var decision = PlaybackProxyDecision.FollowSystem;
        var systemProxy = new WebProxy($"http://127.0.0.1:{systemProxyServer.Port}");
        using var http = new HttpClient(new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new PlaybackWebProxy(() => decision, () => systemProxy),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // 系统代理：请求送到“系统代理”
        Assert.Equal("ok", await http.GetStringAsync("http://media.example/vod/first.m3u8"));
        Assert.Equal(["GET http://media.example/vod/first.m3u8 HTTP/1.1"], systemProxyServer.RequestLines);
        Assert.Empty(forcedProxyServer.RequestLines);

        // 切到强制代理：同一个 HttpClient，下一个请求就走用户填的地址
        decision = Forced($"http://127.0.0.1:{forcedProxyServer.Port}");
        Assert.Equal("ok", await http.GetStringAsync("http://media.example/vod/second.m3u8"));
        Assert.Equal(["GET http://media.example/vod/second.m3u8 HTTP/1.1"], forcedProxyServer.RequestLines);
        Assert.Single(systemProxyServer.RequestLines);

        // 切回系统代理
        decision = PlaybackProxyDecision.FollowSystem;
        Assert.Equal("ok", await http.GetStringAsync("http://media.example/vod/third.m3u8"));
        Assert.Equal(2, systemProxyServer.RequestLines.Count);
        Assert.Single(forcedProxyServer.RequestLines);
    }

    [Fact]
    public async Task HttpClient_DirectModeConnectsToTheOriginInsteadOfTheProxy()
    {
        using var systemProxyServer = new FakeProxyServer();
        using var origin = new FakeProxyServer();
        var systemProxy = new WebProxy($"http://127.0.0.1:{systemProxyServer.Port}");
        using var http = new HttpClient(new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new PlaybackWebProxy(() => PlaybackProxyDecision.NoProxy, () => systemProxy),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        Assert.Equal("ok", await http.GetStringAsync($"http://127.0.0.1:{origin.Port}/vod/a.m3u8"));

        // 直连时是普通的源站请求（路径形式），代理一个请求都没收到
        Assert.Equal(["GET /vod/a.m3u8 HTTP/1.1"], origin.RequestLines);
        Assert.Empty(systemProxyServer.RequestLines);
    }
}
