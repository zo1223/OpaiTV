using System.Net;
using System.Net.Sockets;
using System.Text;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>直连优先、代理兜底的智能分流（<see cref="SmartRouteHandler"/> + <see cref="RouteMemory"/>）。</summary>
public class SmartRoutingTests
{
    private const string Url = "https://media.example/api.php/provide/vod?wd=a";
    private const string Host = "media.example";

    private sealed class Route(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return handle(request, cancellationToken);
        }

        public static Route Ok(string body = "ok") => Status(HttpStatusCode.OK, body);

        public static Route Status(HttpStatusCode status, string body = "") =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

        public static Route Throws() => new((_, _) => throw new HttpRequestException("connection refused"));

        /// <summary>一直不应答，直到被取消。</summary>
        public static Route Hangs() => new(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
    }

    private static HttpClient Client(
        Route direct,
        Route proxy,
        RouteMemory memory,
        bool proxyAvailable = true,
        TimeSpan? directTimeout = null) =>
        new(new SmartRouteHandler(direct, proxy, _ => proxyAvailable, memory, directTimeout ?? TimeSpan.FromSeconds(5)))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

    [Fact]
    public async Task DirectWorks_ProxyIsNeverUsed_AndDirectIsRemembered()
    {
        var memory = new RouteMemory();
        var direct = Route.Ok("direct");
        var proxy = Route.Ok("proxy");
        using var http = Client(direct, proxy, memory);

        Assert.Equal("direct", await http.GetStringAsync(Url));
        Assert.Equal("direct", await http.GetStringAsync(Url));

        Assert.Equal(2, direct.Calls);
        Assert.Equal(0, proxy.Calls);
        Assert.Equal(NetworkRoute.Direct, memory.Peek(Host));
    }

    [Fact]
    public async Task DirectFails_FallsBackToProxy_AndNextRequestGoesStraightToProxy()
    {
        var memory = new RouteMemory();
        var direct = Route.Throws();
        var proxy = Route.Ok("proxy");
        using var http = Client(direct, proxy, memory);

        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(NetworkRoute.Proxy, memory.Peek(Host));
        Assert.Equal(1, direct.Calls);

        // 记住了：第二次不再先撞一次直连
        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(1, direct.Calls);
        Assert.Equal(2, proxy.Calls);
    }

    [Fact]
    public async Task DirectHangs_SwitchesToProxyAfterTheShortTimeout_NotTheCallersFullTimeout()
    {
        var memory = new RouteMemory();
        var proxy = Route.Ok("proxy");
        using var http = Client(Route.Hangs(), proxy, memory, directTimeout: TimeSpan.FromMilliseconds(80));

        var started = DateTime.UtcNow;
        Assert.Equal("proxy", await http.GetStringAsync(Url));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "直连挂住时应在短超时后换路");
        Assert.Equal(NetworkRoute.Proxy, memory.Peek(Host));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons)]
    public async Task DirectBlocked_ProxyWorks_UsesProxy(HttpStatusCode blocked)
    {
        var memory = new RouteMemory();
        using var http = Client(Route.Status(blocked), Route.Ok("proxy"), memory);

        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(NetworkRoute.Proxy, memory.Peek(Host));
    }

    [Fact]
    public async Task BothRoutesBlocked_ReturnsTheDirectAnswer_AndRemembersNothing()
    {
        var memory = new RouteMemory();
        using var http = Client(Route.Status(HttpStatusCode.Forbidden, "direct-403"), Route.Status(HttpStatusCode.Forbidden, "proxy-403"), memory);

        using var response = await http.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("direct-403", await response.Content.ReadAsStringAsync());
        Assert.Null(memory.Peek(Host));
    }

    [Fact]
    public async Task BothRoutesFail_ThrowsAndRemembersNothing()
    {
        var memory = new RouteMemory();
        using var http = Client(Route.Throws(), Route.Throws(), memory);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(Url));
        Assert.Null(memory.Peek(Host));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task OtherErrorStatuses_AreAnAnswerFromTheSite_NoProxyTrafficSpent(HttpStatusCode status)
    {
        var memory = new RouteMemory();
        var proxy = Route.Ok();
        using var http = Client(Route.Status(status), proxy, memory);

        using var response = await http.GetAsync(Url);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(0, proxy.Calls);
        Assert.Equal(NetworkRoute.Direct, memory.Peek(Host));
    }

    [Fact]
    public async Task NoProxyAvailable_DirectOnly_NoFallback_NoShortTimeout()
    {
        // 代理模式为“直连”，或系统代理模式下系统没开代理：行为与没有分流时一样
        var memory = new RouteMemory();
        var proxy = Route.Ok("proxy");
        var slowDirect = new Route(async (_, token) =>
        {
            await Task.Delay(300, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("slow-direct") };
        });
        using (var http = Client(slowDirect, proxy, memory, proxyAvailable: false, directTimeout: TimeSpan.FromMilliseconds(50)))
        {
            Assert.Equal("slow-direct", await http.GetStringAsync(Url));
        }

        using (var http = Client(Route.Throws(), proxy, memory, proxyAvailable: false))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(Url));
        }

        Assert.Equal(0, proxy.Calls);
        Assert.Null(memory.Peek(Host));
    }

    [Fact]
    public async Task RememberedProxy_IgnoredWhenNoProxyIsAvailable()
    {
        var memory = new RouteMemory();
        memory.Remember(Host, NetworkRoute.Proxy);
        var proxy = Route.Ok("proxy");
        using var http = Client(Route.Ok("direct"), proxy, memory, proxyAvailable: false);

        Assert.Equal("direct", await http.GetStringAsync(Url));
        Assert.Equal(0, proxy.Calls);
    }

    [Fact]
    public async Task LocalDestinations_AlwaysDirect()
    {
        var memory = new RouteMemory();
        var proxy = Route.Ok("proxy");
        using var http = Client(Route.Throws(), proxy, memory);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("http://127.0.0.1:53124/vod/a/index.m3u8"));
        Assert.Equal(0, proxy.Calls);
    }

    [Fact]
    public async Task RememberedProxy_IsReprobedDirectAfterAWhile_AndSwitchesBackWhenDirectWorks()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var memory = new RouteMemory(now: () => now);
        memory.Remember(Host, NetworkRoute.Proxy);
        var direct = Route.Ok("direct");
        var proxy = Route.Ok("proxy");
        using var http = Client(direct, proxy, memory);

        now += RouteMemory.DefaultReprobeAfter - TimeSpan.FromMinutes(1);
        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(0, direct.Calls);

        now += TimeSpan.FromMinutes(2);
        Assert.Equal("direct", await http.GetStringAsync(Url));
        Assert.Equal(NetworkRoute.Direct, memory.Peek(Host));

        Assert.Equal("direct", await http.GetStringAsync(Url));
        Assert.Equal(1, proxy.Calls);
    }

    [Fact]
    public async Task ReprobeThatStillFails_StaysOnProxy_AndWaitsAnotherPeriod()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var memory = new RouteMemory(now: () => now);
        memory.Remember(Host, NetworkRoute.Proxy);
        var direct = Route.Throws();
        using var http = Client(direct, Route.Ok("proxy"), memory);

        now += RouteMemory.DefaultReprobeAfter + TimeSpan.FromMinutes(1);
        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(1, direct.Calls);

        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(1, direct.Calls);
        Assert.Equal(NetworkRoute.Proxy, memory.Peek(Host));
    }

    [Fact]
    public void Reprobe_IsClaimedByOneRequest_OthersKeepUsingTheProxy()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var memory = new RouteMemory(now: () => now);
        memory.Remember(Host, NetworkRoute.Proxy);
        now += RouteMemory.DefaultReprobeAfter + TimeSpan.FromSeconds(1);

        Assert.Equal(NetworkRoute.Direct, memory.FirstRouteFor(Host));
        Assert.Equal(NetworkRoute.Proxy, memory.FirstRouteFor(Host));
        Assert.Equal(NetworkRoute.Proxy, memory.FirstRouteFor(Host));
    }

    [Fact]
    public async Task RememberedProxy_ProxyDown_RetriesDirect_AndRemembersDirectWhenItWorks()
    {
        var memory = new RouteMemory();
        memory.Remember(Host, NetworkRoute.Proxy);
        using var http = Client(Route.Ok("direct"), Route.Throws(), memory);

        Assert.Equal("direct", await http.GetStringAsync(Url));
        Assert.Equal(NetworkRoute.Direct, memory.Peek(Host));
    }

    [Fact]
    public async Task RememberedProxy_BothDown_ThrowsTheProxyError_AndKeepsTheMemory()
    {
        var memory = new RouteMemory();
        memory.Remember(Host, NetworkRoute.Proxy);
        using var http = Client(Route.Throws(), Route.Throws(), memory);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(Url));
        Assert.Equal(NetworkRoute.Proxy, memory.Peek(Host));
    }

    [Fact]
    public async Task CallerCancellation_IsNotTreatedAsADirectFailure()
    {
        var memory = new RouteMemory();
        var proxy = Route.Ok("proxy");
        using var http = Client(Route.Hangs(), proxy, memory);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetStringAsync(Url, cancel.Token));

        Assert.Equal(0, proxy.Calls);
        Assert.Null(memory.Peek(Host));
    }

    [Fact]
    public async Task DirectFailed_CallerGivesUpWhileWaitingForTheProxy_NextRequestStartsWithTheProxy()
    {
        // 探测类请求总时限只有几秒：直连短超时占掉一部分后，代理可能来不及应答。
        // 这时要记住“直连不通”，否则每次都先耗在直连上，永远学不会。
        var memory = new RouteMemory();
        using var http = Client(Route.Throws(), Route.Hangs(), memory);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetStringAsync(Url, cancel.Token));

        Assert.Equal(NetworkRoute.Proxy, memory.Peek(Host));
    }

    [Fact]
    public async Task ProxyAuthenticationRequired_IsAProxyFailure_NotRemembered()
    {
        var memory = new RouteMemory();
        using var http = Client(Route.Throws(), Route.Status(HttpStatusCode.ProxyAuthenticationRequired), memory);

        using var response = await http.GetAsync(Url);

        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        Assert.Null(memory.Peek(Host));
    }

    [Fact]
    public async Task FallbackStartsAgainFromTheOriginalAddress_EvenIfTheDirectAttemptRewroteIt()
    {
        var memory = new RouteMemory();
        Uri? seenByProxy = null;
        var direct = new Route((request, _) =>
        {
            // 模拟自动重定向把请求地址改写后再失败
            request.RequestUri = new Uri("https://cdn.example/redirected");
            throw new HttpRequestException("connection reset");
        });
        var proxy = new Route((request, _) =>
        {
            seenByProxy = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("proxy") });
        });
        using var http = Client(direct, proxy, memory);

        Assert.Equal("proxy", await http.GetStringAsync(Url));
        Assert.Equal(new Uri(Url), seenByProxy);
    }

    [Fact]
    public async Task RequestsWithABody_AreSentOnce_OnTheRememberedRoute()
    {
        var memory = new RouteMemory();
        var direct = Route.Throws();
        var proxy = Route.Ok("proxy");
        using var http = Client(direct, proxy, memory);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync(Url, new StringContent("x")));
        Assert.Equal(0, proxy.Calls);

        memory.Remember(Host, NetworkRoute.Proxy);
        using var response = await http.PostAsync(Url, new StringContent("x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, direct.Calls);
    }

    [Fact]
    public void MemoryKey_IsTheLowercaseHost_WithNonDefaultPort()
    {
        Assert.Equal("media.example", RouteMemory.KeyFor(new Uri("https://Media.Example/a")));
        Assert.Equal("media.example", RouteMemory.KeyFor(new Uri("http://media.example:80/a")));
        Assert.Equal("media.example:8443", RouteMemory.KeyFor(new Uri("https://media.example:8443/a")));
    }

    [Fact]
    public void Memory_PersistsToALocalFile_AndSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opaitv-route-memory-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "route_memory.json");
        try
        {
            var memory = new RouteMemory(file);
            memory.Remember("cdn.example", NetworkRoute.Direct);
            memory.Remember("media.example", NetworkRoute.Proxy);
            memory.Flush();

            var reloaded = new RouteMemory(file);
            Assert.Equal(NetworkRoute.Direct, reloaded.Peek("cdn.example"));
            Assert.Equal(NetworkRoute.Proxy, reloaded.Peek("MEDIA.example"));
            Assert.Equal((1, 1), reloaded.Count());
            Assert.Null(reloaded.Peek("other.example"));

            reloaded.Clear();
            reloaded.Flush();
            Assert.Equal((0, 0), new RouteMemory(file).Count());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Memory_CorruptFileIsTreatedAsEmpty()
    {
        var file = Path.Combine(Path.GetTempPath(), "opaitv-route-memory-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{ not json");
            Assert.Equal((0, 0), new RouteMemory(file).Count());
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void LibVlcOptions_SkipTheProxyOnlyForHostsKnownToWorkDirect()
    {
        var forced = PlaybackProxyDecision.From(PlaybackProxyModes.ForcedProxy, "http://127.0.0.1:7890");
        var media = new Uri("https://media.example/live/a.m3u8");
        var memory = new RouteMemory();

        // 没记过：播放内核没法先直连再换路，保持原来的强制代理行为
        Assert.Equal([":http-proxy=http://127.0.0.1:7890"], forced.LibVlcOptionsFor(media, memory));
        Assert.Equal([":http-proxy=http://127.0.0.1:7890"], forced.LibVlcOptionsFor(media, null));

        memory.Remember(Host, NetworkRoute.Proxy);
        Assert.Equal([":http-proxy=http://127.0.0.1:7890"], forced.LibVlcOptionsFor(media, memory));

        memory.Remember(Host, NetworkRoute.Direct);
        Assert.Empty(forced.LibVlcOptionsFor(media, memory));
        Assert.DoesNotContain(
            PlaybackMediaOptions.ForUri(media, 1500, forced, memory),
            option => option.StartsWith(":http-proxy", StringComparison.Ordinal));
        Assert.Contains(":http-proxy=http://127.0.0.1:7890", PlaybackMediaOptions.ForUri(media, 1500, forced));

        // 直连 / 系统代理模式本来就不追加
        Assert.Empty(PlaybackProxyDecision.NoProxy.LibVlcOptionsFor(media, memory));
        Assert.Empty(PlaybackProxyDecision.FollowSystem.LibVlcOptionsFor(media, memory));
    }

    [Fact]
    public void SettingsText_SaysDirectFirstAndProxyOnlyForUnreachableSources()
    {
        Assert.Contains("直连优先", SmartRoutingText.Summary);
        Assert.Contains("代理流量只花在直连不通的源上", SmartRoutingText.Summary);
        Assert.Contains("搜索", SmartRoutingText.Summary);
        Assert.Contains("不兜底", SmartRoutingText.Summary);
        Assert.Contains("不上传", SmartRoutingText.Summary);

        Assert.Contains("直连优先", SmartRoutingText.Describe(PlaybackProxyDecision.FollowSystem));
        Assert.Contains(
            "http://127.0.0.1:7890",
            SmartRoutingText.Describe(PlaybackProxyDecision.From(PlaybackProxyModes.ForcedProxy, "http://user:pw@127.0.0.1:7890")));
        Assert.DoesNotContain(
            "pw",
            SmartRoutingText.Describe(PlaybackProxyDecision.From(PlaybackProxyModes.ForcedProxy, "http://user:pw@127.0.0.1:7890")));
        Assert.Contains("不兜底", SmartRoutingText.Describe(PlaybackProxyDecision.NoProxy));
        Assert.Contains("2 个直连站点", SmartRoutingText.DescribeMemory((2, 3)));
    }

    // ---- 真实网络处理器（SmartRouteHandler.Create）+ 本机假代理 ----

    private sealed class FakeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public List<string> RequestLines { get; } = [];

        public FakeProxy()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(_stop.Token);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                    {
                    }

                    lock (RequestLines)
                    {
                        RequestLines.Add(requestLine ?? "");
                    }

                    var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nvia-proxy");
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch
            {
                // 测试结束
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task RealHandlers_UnreachableDirect_GoesThroughTheConfiguredProxy_ThenDirectModeNeverTouchesIt()
    {
        using var proxyServer = new FakeProxy();
        var decision = PlaybackProxyDecision.From(PlaybackProxyModes.ForcedProxy, $"http://127.0.0.1:{proxyServer.Port}");
        var memory = new RouteMemory();
        using var http = new HttpClient(SmartRouteHandler.Create(() => decision, memory, directAttemptTimeout: TimeSpan.FromSeconds(2)))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        // media.example 是保留域名，直连必然解析失败 / 连不上 → 改走强制代理填的地址
        Assert.Equal("via-proxy", await http.GetStringAsync("http://media.example/vod/a.m3u8"));
        Assert.Equal(["GET http://media.example/vod/a.m3u8 HTTP/1.1"], proxyServer.RequestLines);
        Assert.Equal(NetworkRoute.Proxy, memory.Peek("media.example"));

        // 切到“直连”：同一个 HttpClient，下一个请求只走直连、不兜底，代理一个请求都收不到
        decision = PlaybackProxyDecision.NoProxy;
        await Assert.ThrowsAnyAsync<Exception>(() => http.GetStringAsync("http://media.example/vod/b.m3u8"));
        Assert.Single(proxyServer.RequestLines);
    }
}
