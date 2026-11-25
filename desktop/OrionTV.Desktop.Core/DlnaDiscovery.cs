using System.Net;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>一台可投屏的 DLNA 设备（带 AVTransport 控制地址）。</summary>
public sealed record DlnaDevice(string Udn, string Name, string ControlUrl, string Location)
{
    public string ServiceType { get; init; } = DlnaSsdp.AvTransportServiceType;

    public string ModelName { get; init; } = "";
}

/// <summary>一次发现的结果。<see cref="Error"/> 非空表示发现本身没能进行（例如没有可用网卡）。</summary>
public sealed record DlnaDiscoveryResult(IReadOnlyList<DlnaDevice> Devices, string? Error = null);

/// <summary>设备描述 XML 的解析（容错的正则解析，不要求文档完全合法）。</summary>
public static partial class DlnaDescription
{
    [GeneratedRegex(@"<service\b[^>]*>(.*?)</service>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ServiceBlockRegex();

    public static DlnaDevice? Parse(string? xml, string location)
    {
        if (string.IsNullOrWhiteSpace(xml) || !Uri.TryCreate(location, UriKind.Absolute, out var locationUri))
        {
            return null;
        }

        string? controlUrl = null;
        var serviceType = DlnaSsdp.AvTransportServiceType;
        foreach (Match block in ServiceBlockRegex().Matches(xml))
        {
            var type = Pick(block.Groups[1].Value, "serviceType");
            if (type.Contains(":service:AVTransport:", StringComparison.OrdinalIgnoreCase))
            {
                controlUrl = Pick(block.Groups[1].Value, "controlURL");
                serviceType = type;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(controlUrl))
        {
            return null;
        }

        var baseUri = locationUri;
        var urlBase = Pick(xml, "URLBase");
        if (Uri.TryCreate(urlBase, UriKind.Absolute, out var declaredBase) && declaredBase.Scheme is "http" or "https")
        {
            baseUri = declaredBase;
        }

        if (!Uri.TryCreate(baseUri, controlUrl, out var absolute) || absolute.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var udn = Pick(xml, "UDN");
        var name = Pick(xml, "friendlyName");
        var model = Pick(xml, "modelName");
        if (model.Length == 0)
        {
            model = Pick(xml, "modelDescription");
        }

        return new DlnaDevice(
            udn.Length > 0 ? udn : $"uuid:{locationUri.Authority}",
            name.Length > 0 ? name : locationUri.Host,
            absolute.AbsoluteUri,
            locationUri.AbsoluteUri)
        {
            ServiceType = serviceType,
            ModelName = model,
        };
    }

    internal static string Pick(string source, string tag)
    {
        var match = Regex.Match(
            source,
            $@"<(?:\w+:)?{Regex.Escape(tag)}\b[^>]*>(.*?)</(?:\w+:)?{Regex.Escape(tag)}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : "";
    }
}

/// <summary>
/// DLNA 设备发现：发 M-SEARCH → 收响应 → 按 LOCATION / UDN 去重 → 取设备描述 → 得到带控制地址的设备。
/// 坏响应、取不到描述、没有 AVTransport 服务的设备一律忽略；到时限返回已发现的设备；不抛异常。
/// </summary>
public sealed class DlnaDiscovery(HttpClient httpClient, Func<ISsdpTransport>? transportFactory = null)
{
    /// <summary>发现期间重发 M-SEARCH 的间隔（UDP 可能丢包）。</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>取单台设备描述的时限。</summary>
    public TimeSpan DescriptionTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<DlnaDiscoveryResult> DiscoverAsync(
        TimeSpan timeout,
        Action<DlnaDevice>? onDevice = null,
        CancellationToken cancellationToken = default)
    {
        var devices = new List<DlnaDevice>();
        var gate = new object();
        ISsdpTransport transport;
        try
        {
            transport = (transportFactory ?? (() => new UdpSsdpTransport()))();
        }
        catch (Exception ex)
        {
            return new DlnaDiscoveryResult(devices, $"无法开始搜索：{ex.Message}");
        }

        string? error = null;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout);
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUdns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Task>();

        try
        {
            using (transport)
            {
                try
                {
                    await SendProbesAsync(transport, window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new DlnaDiscoveryResult(devices);
                }
                catch (Exception ex)
                {
                    return new DlnaDiscoveryResult(devices, $"无法开始搜索：{ex.Message}");
                }

                var reprobe = ReprobeLoopAsync(transport, window.Token);
                while (!window.IsCancellationRequested)
                {
                    byte[] datagram;
                    try
                    {
                        datagram = await transport.ReceiveAsync(window.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    var response = DlnaSsdp.ParseResponse(datagram);
                    // 局域网里的真实设备不会把描述地址指到“本机”：这类响应直接丢弃，不去请求本机上的服务。
                    if (response is null || IsLoopbackUrl(response.Location) || !seenLocations.Add(response.Location))
                    {
                        continue;
                    }

                    pending.Add(RegisterAsync(response.Location));
                }

                try
                {
                    await reprobe.ConfigureAwait(false);
                }
                catch
                {
                    // 重发失败不影响已收到的结果。
                }
            }

            // 时限到了以后，已在路上的描述请求再等它们自己的时限。
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = $"搜索出错：{ex.Message}";
        }

        lock (gate)
        {
            return new DlnaDiscoveryResult(devices.ToList(), devices.Count == 0 ? error : null);
        }

        async Task RegisterAsync(string location)
        {
            try
            {
                // 调用方取消（关掉面板、离开播放页）时，在路上的描述请求一并取消。
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(DescriptionTimeout);
                using var response = await httpClient
                    .GetAsync(location, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDescriptionBytes)
                {
                    return;
                }

                var xml = await ReadLimitedAsync(response.Content, limit.Token).ConfigureAwait(false);
                var device = DlnaDescription.Parse(xml, location);
                if (device is null || IsLoopbackUrl(device.ControlUrl))
                {
                    return;
                }

                lock (gate)
                {
                    if (!seenUdns.Add(device.Udn))
                    {
                        return;
                    }

                    devices.Add(device);
                }

                try
                {
                    onDevice?.Invoke(device);
                }
                catch
                {
                    // 回调出错不影响发现。
                }
            }
            catch
            {
                // 取不到描述的设备忽略。
            }
        }
    }

    /// <summary>设备描述最多读这么多字节，超出的部分不要（正常的描述只有几 KB）。</summary>
    public const int MaxDescriptionBytes = 512 * 1024;

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length < MaxDescriptionBytes)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxDescriptionBytes - buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static bool IsLoopbackUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));

    private static async Task SendProbesAsync(ISsdpTransport transport, CancellationToken cancellationToken)
    {
        foreach (var target in DlnaSsdp.SearchTargets)
        {
            await transport.SendAsync(DlnaSsdp.BuildSearchDatagram(target), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReprobeLoopAsync(ISsdpTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
                await SendProbesAsync(transport, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // 到时限或发送失败：停止重发。
        }
    }
}
