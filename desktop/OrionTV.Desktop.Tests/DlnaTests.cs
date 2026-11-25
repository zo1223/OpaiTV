using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using OrionTV.Desktop.Core;
using Xunit;

namespace OrionTV.Desktop.Tests;

/// <summary>
/// DLNA 投屏：用假电视验证发现与控制流程。假电视的 HTTP 端（设备描述、AVTransport 控制）是进程内的
/// HttpMessageHandler，不开端口；SSDP 端有两种：走本机回环 UDP 的（验证真实的 UdpSsdpTransport），
/// 和纯内存的假传输层（验证面板逻辑）。
/// </summary>
public sealed class DlnaTests
{
    private const string MediaUrl = "https://cdn.example/ep1.m3u8";

    private static DlnaDiscovery DiscoveryFor(FakeTv tv) =>
        new(tv.Http, () => new UdpSsdpTransport(tv.SsdpEndPoint, [IPAddress.Loopback]))
        {
            ProbeInterval = TimeSpan.FromMilliseconds(200),
            DescriptionTimeout = TimeSpan.FromSeconds(2),
        };

    [Fact]
    public void SearchMessage_HasRequiredSsdpHeaders()
    {
        var message = DlnaSsdp.BuildSearchMessage(DlnaSsdp.AvTransportServiceType);

        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", message);
        Assert.Contains("HOST: 239.255.255.250:1900\r\n", message);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", message);
        Assert.Contains("MX: 2\r\n", message);
        Assert.Contains("ST: urn:schemas-upnp-org:service:AVTransport:1\r\n", message);
        Assert.EndsWith("\r\n\r\n", message);
        Assert.Contains(DlnaSsdp.MediaRendererDeviceType, DlnaSsdp.SearchTargets);
    }

    [Fact]
    public void ParseResponse_IsCaseInsensitiveAndRejectsBadMessages()
    {
        var ok = DlnaSsdp.ParseResponse("HTTP/1.1 200 OK\r\nlocation: http://192.168.1.9:49152/desc.xml\r\nUsn: uuid:abc::x\r\nst: upnp:rootdevice\r\n\r\n");
        Assert.NotNull(ok);
        Assert.Equal("http://192.168.1.9:49152/desc.xml", ok!.Location);
        Assert.Equal("uuid:abc::x", ok.Usn);
        Assert.Equal("upnp:rootdevice", ok.SearchTarget);

        Assert.Null(DlnaSsdp.ParseResponse(""));
        Assert.Null(DlnaSsdp.ParseResponse("garbage"));
        Assert.Null(DlnaSsdp.ParseResponse("HTTP/1.1 200 OK\r\nST: x\r\n\r\n"));
        Assert.Null(DlnaSsdp.ParseResponse("HTTP/1.1 200 OK\r\nLOCATION: ftp://192.168.1.9/desc.xml\r\n\r\n"));
        Assert.Null(DlnaSsdp.ParseResponse("HTTP/1.1 404 Not Found\r\nLOCATION: http://192.168.1.9/desc.xml\r\n\r\n"));
        Assert.Null(DlnaSsdp.ParseResponse("NOTIFY * HTTP/1.1\r\nNTS: ssdp:byebye\r\nLOCATION: http://192.168.1.9/desc.xml\r\n\r\n"));
        Assert.Null(DlnaSsdp.ParseResponse(new byte[] { 0xFF, 0xFE, 0x00 }));
    }

    [Fact]
    public void Description_ResolvesControlUrlAgainstUrlBaseOrLocation()
    {
        var relative = DlnaDescription.Parse(FakeTv.DescriptionXml("客厅电视 &amp; 盒子", "AVTransport/control", urlBase: null), "http://192.168.1.9:49152/dev/desc.xml");
        Assert.NotNull(relative);
        Assert.Equal("客厅电视 & 盒子", relative!.Name);
        Assert.Equal("http://192.168.1.9:49152/dev/AVTransport/control", relative.ControlUrl);
        Assert.Equal("uuid:fake-tv-1", relative.Udn);
        Assert.Equal("FakeTV 1", relative.ModelName);
        Assert.Equal(DlnaSsdp.AvTransportServiceType, relative.ServiceType);

        var rooted = DlnaDescription.Parse(FakeTv.DescriptionXml("TV", "/upnp/control/AVTransport1", urlBase: null), "http://192.168.1.9:49152/dev/desc.xml");
        Assert.Equal("http://192.168.1.9:49152/upnp/control/AVTransport1", rooted!.ControlUrl);

        var based = DlnaDescription.Parse(FakeTv.DescriptionXml("TV", "ctl/av", urlBase: "http://192.168.1.9:8080/base/"), "http://192.168.1.9:49152/dev/desc.xml");
        Assert.Equal("http://192.168.1.9:8080/base/ctl/av", based!.ControlUrl);

        Assert.Null(DlnaDescription.Parse("<root><device><friendlyName>音箱</friendlyName></device></root>", "http://192.168.1.9/desc.xml"));
        Assert.Null(DlnaDescription.Parse("not xml", "http://192.168.1.9/desc.xml"));
        Assert.Null(DlnaDescription.Parse(null, "http://192.168.1.9/desc.xml"));
    }

    [Fact]
    public async Task Discovery_FindsFakeTvOverLoopbackUdpAndDeduplicates()
    {
        using var tv = new FakeTv { ResponsesPerSearch = 2 };
        var seen = new List<DlnaDevice>();

        var result = await DiscoveryFor(tv).DiscoverAsync(TimeSpan.FromMilliseconds(900), seen.Add);

        Assert.Null(result.Error);
        var device = Assert.Single(result.Devices);
        Assert.Equal("假电视", device.Name);
        Assert.Equal(tv.ControlUrl, device.ControlUrl);
        Assert.Equal(tv.Location, device.Location);
        Assert.Single(seen);

        var searches = tv.Searches();
        Assert.Contains(searches, message => message.Contains("ST: " + DlnaSsdp.AvTransportServiceType));
        Assert.Contains(searches, message => message.Contains("ST: " + DlnaSsdp.MediaRendererDeviceType));
        Assert.All(searches, message => Assert.Contains("MAN: \"ssdp:discover\"", message));
        // 发现期间会重发，但每台设备的描述只取一次。
        Assert.True(searches.Count >= 4);
        Assert.Equal(1, tv.DescriptionRequests);
    }

    [Fact]
    public async Task Discovery_IgnoresGarbageAndUnreachableDescriptions()
    {
        using var tv = new FakeTv { SendGarbage = true, DescriptionBroken = true };

        var result = await DiscoveryFor(tv).DiscoverAsync(TimeSpan.FromMilliseconds(600));

        Assert.Empty(result.Devices);
        Assert.Null(result.Error);
        Assert.NotEmpty(tv.Searches());
    }

    [Fact]
    public async Task Discovery_ReturnsEmptyAtTimeoutWhenNothingAnswers()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var tv = new FakeTv();
        var discovery = new DlnaDiscovery(tv.Http, () => new UdpSsdpTransport((IPEndPoint)silent.Client.LocalEndPoint!, [IPAddress.Loopback]));
        var started = DateTime.UtcNow;

        var result = await discovery.DiscoverAsync(TimeSpan.FromMilliseconds(400));

        Assert.Empty(result.Devices);
        Assert.Null(result.Error);
        Assert.InRange((DateTime.UtcNow - started).TotalMilliseconds, 300, 5000);
    }

    [Fact]
    public async Task Discovery_ReportsErrorInsteadOfThrowingWhenTransportFails()
    {
        using var tv = new FakeTv();
        var failingFactory = new DlnaDiscovery(tv.Http, () => throw new InvalidOperationException("no nic"));
        var failingSend = new DlnaDiscovery(tv.Http, () => new ThrowingTransport());

        var first = await failingFactory.DiscoverAsync(TimeSpan.FromMilliseconds(200));
        var second = await failingSend.DiscoverAsync(TimeSpan.FromMilliseconds(200));

        Assert.Empty(first.Devices);
        Assert.Contains("no nic", first.Error);
        Assert.Empty(second.Devices);
        Assert.NotNull(second.Error);
    }

    [Fact]
    public async Task Discovery_ListsEveryRendererAndDropsLoopbackAndNonRendererAnswers()
    {
        using var living = new FakeTv { Host = "192.168.1.50", Name = "客厅电视", Udn = "uuid:tv-living" };
        using var bedroom = new FakeTv { Host = "192.168.1.51", Name = "卧室盒子", Udn = "uuid:tv-bedroom" };
        using var http = FakeTv.HttpFor(living, bedroom);
        var transport = new FakeSsdpTransport(
            FakeSsdpTransport.Reply(living.Location, living.Udn),
            FakeSsdpTransport.Reply(bedroom.Location, bedroom.Udn),
            // 同一台设备对第二个搜索目标的重复应答。
            FakeSsdpTransport.Reply(living.Location, living.Udn),
            // 把描述地址指到本机的应答：不请求。
            FakeSsdpTransport.Reply("http://127.0.0.1:8123/desc/device.xml", "uuid:evil"),
            // 描述取不到的设备（路由器之类）。
            FakeSsdpTransport.Reply("http://192.168.1.1:1900/igd.xml", "uuid:router"));
        var discovery = new DlnaDiscovery(http, () => transport) { ProbeInterval = TimeSpan.FromSeconds(30) };

        var result = await discovery.DiscoverAsync(TimeSpan.FromMilliseconds(400));

        Assert.Null(result.Error);
        Assert.Equal(new[] { "卧室盒子", "客厅电视" }, result.Devices.Select(device => device.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, living.DescriptionRequests);
        Assert.Equal(1, bedroom.DescriptionRequests);
        Assert.Equal(DlnaSsdp.SearchTargets.Count, transport.Sent.Count);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Soap_ActionsCarrySoapActionHeaderAndEscapedBody()
    {
        using var tv = new FakeTv();
        var device = tv.Device();
        var transport = new DlnaAvTransport(tv.Http);
        const string url = "https://cdn.example/v/index.m3u8?a=1&b=<2>";

        await transport.SetAvTransportUriAsync(device, url, "流浪地球 & \"2\"");
        await transport.PlayAsync(device);
        await transport.PauseAsync(device);
        await transport.SeekAsync(device, TimeSpan.FromSeconds(3725));
        await transport.StopAsync(device);

        var calls = tv.Calls();
        Assert.Equal(new[] { "SetAVTransportURI", "Play", "Pause", "Seek", "Stop" }, calls.Select(call => call.Action).ToArray());
        Assert.All(calls, call => Assert.Equal($"\"{DlnaSsdp.AvTransportServiceType}#{call.Action}\"", call.SoapAction));
        Assert.All(calls, call => Assert.StartsWith("text/xml", call.ContentType));
        Assert.All(calls, call => Assert.Contains("<InstanceID>0</InstanceID>", call.Body));

        var set = calls[0].Body;
        Assert.Contains("xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"", set);
        Assert.Contains($"<u:SetAVTransportURI xmlns:u=\"{DlnaSsdp.AvTransportServiceType}\">", set);
        Assert.Contains("<CurrentURI>https://cdn.example/v/index.m3u8?a=1&amp;b=&lt;2&gt;</CurrentURI>", set);
        // DIDL 作为参数值要整体再转义一次；里面的地址因此是二次转义。
        Assert.Contains("<CurrentURIMetaData>&lt;DIDL-Lite", set);
        Assert.Contains("a=1&amp;amp;b=&amp;lt;2&amp;gt;", set);
        Assert.Contains("流浪地球 &amp;amp; &amp;quot;2&amp;quot;", set);
        Assert.DoesNotContain("<DIDL-Lite", set);

        Assert.Contains("<Speed>1</Speed>", calls[1].Body);
        Assert.Contains("<Unit>REL_TIME</Unit><Target>01:02:05</Target>", calls[3].Body);
    }

    [Fact]
    public async Task Soap_ParsesPositionAndTransportState()
    {
        using var tv = new FakeTv { RelTime = "0:01:05", Duration = "00:42:10.500", TransportState = "PAUSED_PLAYBACK" };
        var transport = new DlnaAvTransport(tv.Http);

        var info = await transport.GetPositionInfoAsync(tv.Device());
        var state = await transport.GetTransportStateAsync(tv.Device());

        Assert.Equal(TimeSpan.FromSeconds(65), info.Position);
        Assert.Equal(TimeSpan.FromSeconds(2530.5), info.Duration);
        Assert.Equal("PAUSED_PLAYBACK", state);
        Assert.Equal(TimeSpan.Zero, DlnaAvTransport.ParseTime("NOT_IMPLEMENTED"));
        Assert.Equal(TimeSpan.Zero, DlnaAvTransport.ParseTime(""));
        Assert.Equal("00:00:00", DlnaAvTransport.FormatTime(TimeSpan.FromSeconds(-5)));
        Assert.Equal("27:46:39", DlnaAvTransport.FormatTime(TimeSpan.FromSeconds(99999.9)));
    }

    [Fact]
    public async Task Soap_FaultHttpErrorAndNetworkErrorBecomeDlnaSoapException()
    {
        using var tv = new FakeTv { FaultAction = "Seek" };
        var transport = new DlnaAvTransport(tv.Http);

        var fault = await Assert.ThrowsAsync<DlnaSoapException>(() => transport.SeekAsync(tv.Device(), TimeSpan.FromSeconds(10)));

        Assert.Equal("Seek", fault.Action);
        Assert.Equal(500, fault.StatusCode);
        Assert.Equal("701", fault.UpnpErrorCode);
        Assert.Contains("Transition not available", fault.Message);

        var gone = tv.Device() with { ControlUrl = $"http://{tv.Host}:49152/missing" };
        var missing = await Assert.ThrowsAsync<DlnaSoapException>(() => transport.PlayAsync(gone));
        Assert.Equal(404, missing.StatusCode);

        tv.Unreachable = true;
        var offline = await Assert.ThrowsAsync<DlnaSoapException>(() => transport.PlayAsync(tv.Device()));
        Assert.Null(offline.StatusCode);
    }

    [Fact]
    public async Task Session_CastPauseResumePollSeekStop()
    {
        using var tv = new FakeTv();
        var discovered = await DiscoveryFor(tv).DiscoverAsync(TimeSpan.FromMilliseconds(600));
        var device = Assert.Single(discovered.Devices);
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));
        var changes = 0;
        session.Changed += (_, _) => changes++;

        Assert.True(await session.CastAsync(device, MediaUrl, "某剧 第1集", TimeSpan.FromSeconds(90)));
        Assert.Equal(DlnaCastState.Playing, session.State);
        Assert.True(session.IsActive);
        Assert.Equal(new[] { "SetAVTransportURI", "Play", "Seek" }, tv.Calls().Select(call => call.Action).ToArray());
        Assert.Equal(MediaUrl, tv.CurrentUri);
        Assert.Equal("00:01:30", tv.RelTime);

        Assert.True(await session.TogglePlayAsync());
        Assert.Equal(DlnaCastState.Paused, session.State);
        Assert.Equal("PAUSED_PLAYBACK", tv.TransportState);
        Assert.True(await session.TogglePlayAsync());
        Assert.Equal(DlnaCastState.Playing, session.State);
        Assert.Equal("PLAYING", tv.TransportState);

        // 电视端自己播到了 00:05:00、并被遥控器暂停：轮询把进度和状态同步回来。
        tv.RelTime = "00:05:00";
        tv.TransportState = "PAUSED_PLAYBACK";
        await session.PollAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), session.Position);
        Assert.Equal(TimeSpan.FromSeconds(2530), session.Duration);
        Assert.Equal(DlnaCastState.Paused, session.State);

        Assert.True(await session.SeekAsync(TimeSpan.FromMinutes(10)));
        Assert.Equal("00:10:00", tv.RelTime);
        Assert.Equal(TimeSpan.FromMinutes(10), session.Position);
        // 跳转不会越过片长。
        Assert.True(await session.SeekAsync(TimeSpan.FromHours(9)));
        Assert.Equal("00:42:10", tv.RelTime);

        // 断开：返回电视端最后的进度，用来恢复本机播放。
        Assert.True(await session.SeekAsync(TimeSpan.FromMinutes(12)));
        var resumeAt = await session.StopAsync();
        Assert.Equal(TimeSpan.FromMinutes(12), resumeAt);
        Assert.Equal(TimeSpan.FromMinutes(12), session.Position);
        Assert.Equal(DlnaCastState.Stopped, session.State);
        Assert.False(session.IsActive);
        Assert.False(session.EndedByDevice);
        Assert.Equal("STOPPED", tv.TransportState);
        Assert.Equal("Stop", tv.Calls()[^1].Action);
        Assert.True(changes > 0);

        // 停止后的操作不再发请求。
        var count = tv.Calls().Count;
        Assert.False(await session.TogglePlayAsync());
        Assert.False(await session.SeekAsync(TimeSpan.FromMinutes(1)));
        await session.PollAsync();
        Assert.Equal(count, tv.Calls().Count);
    }

    [Fact]
    public async Task Session_ShortStartPositionPlaysFromBeginningWithoutSeek()
    {
        using var tv = new FakeTv();
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));

        Assert.True(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.FromSeconds(3)));

        Assert.Equal(new[] { "SetAVTransportURI", "Play" }, tv.Calls().Select(call => call.Action).ToArray());
        Assert.Equal(TimeSpan.Zero, session.Position);
    }

    [Fact]
    public async Task Session_RetriesStartSeekAfterDeviceStartsPlaying()
    {
        using var tv = new FakeTv { FaultAction = "Seek" };
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));

        Assert.True(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.FromSeconds(600)));
        Assert.Equal(DlnaCastState.Playing, session.State);
        Assert.Equal("00:00:00", tv.RelTime);
        // 起始跳转还没成功时，界面上的进度先显示目标位置。
        Assert.Equal(TimeSpan.FromMinutes(10), session.Position);

        tv.FaultAction = null;
        await session.PollAsync();

        Assert.Equal("00:10:00", tv.RelTime);
        Assert.Equal(TimeSpan.FromMinutes(10), session.Position);
    }

    [Fact]
    public async Task Session_FailuresAreReportedNotThrown()
    {
        using var tv = new FakeTv { FaultAction = "SetAVTransportURI" };
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));

        Assert.False(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.Zero));
        Assert.Equal(DlnaCastState.Error, session.State);
        Assert.False(session.IsActive);
        Assert.Contains("SetAVTransportURI", session.Error);

        // 本机中转地址、本地文件不能投，且不会发任何请求。
        var before = tv.Calls().Count;
        Assert.False(await session.CastAsync(tv.Device(), "http://127.0.0.1:8123/proxy.m3u8", "片", TimeSpan.Zero));
        Assert.False(await session.CastAsync(tv.Device(), @"C:\Videos\a.mp4", "片", TimeSpan.Zero));
        Assert.Equal(before, tv.Calls().Count);
        Assert.NotNull(DlnaCastSession.ValidateCastUrl("http://localhost/a.m3u8"));
        Assert.NotNull(DlnaCastSession.ValidateCastUrl(""));
        Assert.NotNull(DlnaCastSession.ValidateCastUrl(null));
        Assert.Null(DlnaCastSession.ValidateCastUrl("http://192.168.1.20/a.m3u8"));

        // 控制命令失败：会话保留，错误写进 Error，下一次成功后清掉。
        tv.FaultAction = null;
        Assert.True(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.Zero));
        tv.FaultAction = "Pause";
        Assert.False(await session.TogglePlayAsync());
        Assert.Equal(DlnaCastState.Playing, session.State);
        Assert.Contains("Pause", session.Error);
        tv.FaultAction = null;
        Assert.True(await session.TogglePlayAsync());
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task Session_UnreachableDeviceIsFlaggedThenRecoversAndStopDoesNotHang()
    {
        using var tv = new FakeTv();
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));
        Assert.True(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.Zero));
        tv.RelTime = "00:03:00";
        await session.PollAsync();
        tv.Unreachable = true;

        for (var i = 0; i < 4; i++)
        {
            await session.PollAsync();
        }

        Assert.False(session.Unresponsive);
        await session.PollAsync();
        Assert.True(session.Unresponsive);
        // 会话保留、最后进度保留。
        Assert.True(session.IsActive);
        Assert.Equal(TimeSpan.FromMinutes(3), session.Position);

        tv.Unreachable = false;
        tv.RelTime = "00:03:20";
        await session.PollAsync();
        Assert.False(session.Unresponsive);
        Assert.Equal(TimeSpan.FromSeconds(200), session.Position);

        tv.Unreachable = true;
        var resumeAt = await session.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(200), resumeAt);
        Assert.Equal(DlnaCastState.Stopped, session.State);
    }

    [Fact]
    public async Task Session_DeviceSideStopEndsSessionAndKeepsLastPosition()
    {
        using var tv = new FakeTv();
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));
        Assert.True(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.Zero));
        tv.RelTime = "00:20:00";
        await session.PollAsync();
        Assert.Equal(TimeSpan.FromMinutes(20), session.Position);

        // 电视被遥控器停掉：进度归零。第一次看到停止先不认（跳转、换台时会闪一下），连续两次才结束会话。
        tv.TransportState = "STOPPED";
        tv.RelTime = "00:00:00";
        await session.PollAsync();
        Assert.True(session.IsActive);
        Assert.Equal(TimeSpan.FromMinutes(20), session.Position);
        await session.PollAsync();

        Assert.Equal(DlnaCastState.Stopped, session.State);
        Assert.True(session.EndedByDevice);
        Assert.Equal(TimeSpan.FromMinutes(20), session.Position);
        Assert.Equal(TimeSpan.FromMinutes(20), await session.StopAsync());
        // 已经停了：不再给设备发 Stop。
        Assert.DoesNotContain(tv.Calls(), call => call.Action == "Stop");
    }

    [Fact]
    public async Task Session_StoppedWhileStillBufferingIsNotTreatedAsEnded()
    {
        using var tv = new FakeTv { PlayLeavesStopped = true };
        var session = new DlnaCastSession(new DlnaAvTransport(tv.Http));
        Assert.True(await session.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.FromSeconds(120)));

        for (var i = 0; i < 4; i++)
        {
            await session.PollAsync();
        }

        Assert.True(session.IsActive);
        Assert.False(session.EndedByDevice);
        // 缓冲期间设备报的 0 不会冲掉起始进度。
        Assert.Equal(TimeSpan.FromMinutes(2), session.Position);
    }

    [Fact]
    public async Task Controller_SearchListsDevicesThenCastControlsAndStatusTexts()
    {
        using var tv = new FakeTv { Name = "客厅电视" };
        var transport = new FakeSsdpTransport(FakeSsdpTransport.Reply(tv.Location, tv.Udn));
        var controller = new DlnaCastController(
            new DlnaDiscovery(tv.Http, () => transport) { ProbeInterval = TimeSpan.FromSeconds(30) },
            new DlnaCastSession(new DlnaAvTransport(tv.Http)))
        {
            SearchTimeout = TimeSpan.FromMilliseconds(300),
        };
        var changes = 0;
        controller.Changed += (_, _) => Interlocked.Increment(ref changes);
        Assert.Equal("", controller.SearchStatusText);

        var search = controller.SearchAsync();
        Assert.True(controller.IsSearching);
        // 假设备的应答是同步到的：这时可能已经列出设备，文字是“正在搜索…”或“已找到 N 台设备，还在搜索…”。
        Assert.Contains("搜索", controller.SearchStatusText);
        await search;

        Assert.False(controller.IsSearching);
        var device = Assert.Single(controller.Devices);
        Assert.Equal("客厅电视", device.Name);
        Assert.Contains("找到 1 台设备", controller.SearchStatusText);

        Assert.True(await controller.CastAsync(device, MediaUrl, "某剧 第2集", TimeSpan.FromSeconds(65)));
        Assert.Equal("播放中", controller.CastStatusText);
        await controller.TickAsync();
        Assert.Equal("01:05 / 42:10", controller.ProgressText);
        Assert.InRange(controller.ProgressFraction, 0.02, 0.03);

        Assert.True(await controller.SeekByAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("00:01:35", tv.RelTime);
        Assert.True(await controller.SeekByAsync(TimeSpan.FromSeconds(-600)));
        Assert.Equal("00:00:00", tv.RelTime);
        Assert.True(await controller.SeekToFractionAsync(0.5));
        Assert.Equal("00:21:05", tv.RelTime);

        Assert.True(await controller.Session.TogglePlayAsync());
        Assert.Equal("已暂停", controller.CastStatusText);

        var resumeAt = await controller.Session.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(1265), resumeAt);
        Assert.Equal("已停止投屏", controller.CastStatusText);
        Assert.True(changes > 4);
        Assert.Equal("1:02:05", DlnaCastController.FormatClock(TimeSpan.FromSeconds(3725)));
    }

    [Fact]
    public async Task Controller_NoDevicesUnavailableUrlAndCancelledSearch()
    {
        using var tv = new FakeTv();
        var created = 0;
        var controller = new DlnaCastController(
            new DlnaDiscovery(tv.Http, () =>
            {
                created++;
                return new FakeSsdpTransport();
            })
            {
                ProbeInterval = TimeSpan.FromSeconds(30),
            },
            new DlnaCastSession(new DlnaAvTransport(tv.Http)))
        {
            SearchTimeout = TimeSpan.FromMilliseconds(200),
        };

        await controller.SearchAsync();
        Assert.Empty(controller.Devices);
        Assert.Contains("没有找到投屏设备", controller.SearchStatusText);

        // 本地文件：不搜索，只给原因。
        controller.ShowUnavailable(DlnaCastSession.ValidateCastUrl(@"D:\电影\a.mkv")!);
        Assert.False(controller.IsSearching);
        Assert.Contains("本地文件", controller.SearchStatusText);
        Assert.Equal(1, created);

        // 搜索中途取消（关掉面板）：立刻不再是“搜索中”，那一轮的结果作废。
        var pending = controller.SearchAsync();
        Assert.True(controller.IsSearching);
        controller.CancelSearch();
        Assert.False(controller.IsSearching);
        await pending;
        Assert.False(controller.IsSearching);

        // 投屏失败时的状态文字带原因。
        tv.FaultAction = "SetAVTransportURI";
        Assert.False(await controller.CastAsync(tv.Device(), MediaUrl, "片", TimeSpan.Zero));
        Assert.StartsWith("投屏失败：", controller.CastStatusText);
        Assert.False(await controller.SeekToFractionAsync(0.5));
    }

    private sealed class ThrowingTransport : ISsdpTransport
    {
        public Task SendAsync(byte[] datagram, CancellationToken cancellationToken) => throw new SocketException(10051);

        public Task<byte[]> ReceiveAsync(CancellationToken cancellationToken) => throw new SocketException(10051);

        public void Dispose()
        {
        }
    }

    /// <summary>纯内存的假 SSDP 传输层：第一次发 M-SEARCH 后，把预置的应答依次交出。</summary>
    private sealed class FakeSsdpTransport(params string[] replies) : ISsdpTransport
    {
        private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
        private int _answered;

        public List<string> Sent { get; } = [];

        public bool Disposed { get; private set; }

        public static string Reply(string location, string udn) =>
            "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nEXT:\r\n"
            + $"LOCATION: {location}\r\nST: {DlnaSsdp.AvTransportServiceType}\r\nUSN: {udn}::{DlnaSsdp.AvTransportServiceType}\r\n\r\n";

        public Task SendAsync(byte[] datagram, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(Encoding.UTF8.GetString(datagram));
            }

            if (Interlocked.Exchange(ref _answered, 1) == 0)
            {
                foreach (var reply in replies)
                {
                    _inbox.Writer.TryWrite(Encoding.UTF8.GetBytes(reply));
                }
            }

            return Task.CompletedTask;
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken) =>
            await _inbox.Reader.ReadAsync(cancellationToken);

        public void Dispose() => Disposed = true;
    }

    /// <summary>假电视：HTTP 端是进程内的 handler；SSDP 端按需开一个本机回环 UDP 套接字应答 M-SEARCH。</summary>
    private sealed class FakeTv : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _searches = [];
        private readonly List<SoapCall> _calls = [];
        private UdpClient? _udp;
        private HttpClient? _http;
        private int _descriptionRequests;

        public sealed record SoapCall(string Action, string SoapAction, string ContentType, string Body);

        public string Host { get; init; } = "192.168.1.50";

        public string Name { get; init; } = "假电视";

        public string Udn { get; init; } = "uuid:fake-tv-1";

        public int ResponsesPerSearch { get; init; } = 1;

        public bool SendGarbage { get; init; }

        public bool DescriptionBroken { get; init; }

        /// <summary>收到 Play 后仍报 STOPPED（模拟一直在缓冲的设备）。</summary>
        public bool PlayLeavesStopped { get; init; }

        public volatile bool Unreachable;

        public volatile string? FaultAction;

        public volatile string TransportState = "STOPPED";

        public volatile string RelTime = "00:00:00";

        public string Duration { get; init; } = "00:42:10";

        public volatile string CurrentUri = "";

        public HttpClient Http => _http ??= HttpFor(this);

        public IPEndPoint SsdpEndPoint
        {
            get
            {
                if (_udp is null)
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    _ = UdpLoopAsync(_udp);
                }

                return (IPEndPoint)_udp.Client.LocalEndPoint!;
            }
        }

        public string Location => $"http://{Host}:49152/desc/device.xml";

        public string ControlUrl => $"http://{Host}:49152/upnp/control/AVTransport1";

        public int DescriptionRequests => Volatile.Read(ref _descriptionRequests);

        public DlnaDevice Device() => new(Udn, Name, ControlUrl, Location);

        public static HttpClient HttpFor(params FakeTv[] tvs) =>
            new(new Router(tvs)) { Timeout = TimeSpan.FromSeconds(5) };

        public List<string> Searches()
        {
            lock (_searches)
            {
                return _searches.ToList();
            }
        }

        public List<SoapCall> Calls()
        {
            lock (_calls)
            {
                return _calls.ToList();
            }
        }

        public static string DescriptionXml(string name, string controlUrl, string? urlBase, string udn = "uuid:fake-tv-1") =>
            "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\">"
            + (urlBase is null ? "" : $"<URLBase>{urlBase}</URLBase>")
            + "<device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>"
            + $"<friendlyName>{name}</friendlyName><modelName>FakeTV 1</modelName><UDN>{udn}</UDN>"
            + "<serviceList>"
            + "<service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>"
            + "<controlURL>/upnp/control/RenderingControl1</controlURL></service>"
            + "<service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>"
            + $"<serviceId>urn:upnp-org:serviceId:AVTransport</serviceId><controlURL>{controlUrl}</controlURL></service>"
            + "</serviceList></device></root>";

        private async Task UdpLoopAsync(UdpClient udp)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var received = await udp.ReceiveAsync(_cts.Token);
                    var text = Encoding.UTF8.GetString(received.Buffer);
                    lock (_searches)
                    {
                        _searches.Add(text);
                    }

                    if (!text.StartsWith("M-SEARCH", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var target = text.Split("\r\n").FirstOrDefault(line => line.StartsWith("ST:", StringComparison.Ordinal))?[3..].Trim() ?? "";
                    if (SendGarbage)
                    {
                        await udp.SendAsync(new byte[] { 0x00, 0xFF, 0x13 }, received.RemoteEndPoint, _cts.Token);
                        await udp.SendAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nST: x\r\n\r\n"), received.RemoteEndPoint, _cts.Token);
                    }

                    for (var i = 0; i < ResponsesPerSearch; i++)
                    {
                        var reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nEXT:\r\n"
                            + $"Location: {Location}\r\nSERVER: Fake/1.0 UPnP/1.0 FakeTV/1.0\r\n"
                            + $"St: {target}\r\nusn: {Udn}::{target}\r\n\r\n";
                        await udp.SendAsync(Encoding.UTF8.GetBytes(reply), received.RemoteEndPoint, _cts.Token);
                    }
                }
            }
            catch
            {
                // 关闭。
            }
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("No route to host");
            }

            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/desc/device.xml")
            {
                Interlocked.Increment(ref _descriptionRequests);
                return DescriptionBroken
                    ? Respond(HttpStatusCode.NotFound, "gone")
                    : Respond(HttpStatusCode.OK, DescriptionXml(Name, "/upnp/control/AVTransport1", urlBase: null, Udn));
            }

            if (request.Method != HttpMethod.Post || path != "/upnp/control/AVTransport1" || request.Content is null)
            {
                return Respond(HttpStatusCode.NotFound, "not found");
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var soapAction = request.Headers.TryGetValues("SOAPAction", out var values) ? values.First() : "";
            var action = soapAction.Trim('"').Split('#').Last();
            lock (_calls)
            {
                _calls.Add(new SoapCall(action, soapAction, request.Content.Headers.ContentType?.ToString() ?? "", body));
            }

            if (FaultAction == action)
            {
                return Respond(
                    HttpStatusCode.InternalServerError,
                    Envelope("<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>"
                        + "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>701</errorCode>"
                        + "<errorDescription>Transition not available</errorDescription></UPnPError></detail></s:Fault>"));
            }

            var inner = "";
            switch (action)
            {
                case "SetAVTransportURI":
                    CurrentUri = WebUtility.HtmlDecode(Between(body, "<CurrentURI>", "</CurrentURI>"));
                    TransportState = "STOPPED";
                    RelTime = "00:00:00";
                    break;
                case "Play":
                    TransportState = PlayLeavesStopped ? "STOPPED" : "PLAYING";
                    break;
                case "Pause":
                    TransportState = "PAUSED_PLAYBACK";
                    break;
                case "Stop":
                    TransportState = "STOPPED";
                    break;
                case "Seek":
                    RelTime = Between(body, "<Target>", "</Target>");
                    break;
                case "GetPositionInfo":
                    inner = $"<Track>1</Track><TrackDuration>{Duration}</TrackDuration><TrackMetaData></TrackMetaData>"
                        + $"<TrackURI>{WebUtility.HtmlEncode(CurrentUri)}</TrackURI><RelTime>{RelTime}</RelTime>"
                        + "<AbsTime>NOT_IMPLEMENTED</AbsTime><RelCount>2147483647</RelCount><AbsCount>2147483647</AbsCount>";
                    break;
                case "GetTransportInfo":
                    inner = $"<CurrentTransportState>{TransportState}</CurrentTransportState>"
                        + "<CurrentTransportStatus>OK</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>";
                    break;
            }

            return Respond(
                HttpStatusCode.OK,
                Envelope($"<u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">{inner}</u:{action}Response>"));
        }

        private static string Envelope(string body) =>
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" + body + "</s:Body></s:Envelope>";

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0)
            {
                return "";
            }

            from += start.Length;
            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? "" : text[from..to];
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _udp?.Dispose();
            }
            catch
            {
                // ignore
            }

            _http?.Dispose();
        }

        /// <summary>按主机名把请求分给对应的假电视；不认识的主机当作连不上。</summary>
        private sealed class Router(FakeTv[] tvs) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var tv = tvs.FirstOrDefault(candidate => candidate.Host == request.RequestUri?.Host);
                return tv is null
                    ? throw new HttpRequestException("No route to host")
                    : tv.HandleAsync(request, cancellationToken);
            }
        }
    }
}
