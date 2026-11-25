using System.Net;
using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>一次请求可以走的两条路。</summary>
public enum NetworkRoute
{
    /// <summary>直连：不经过任何代理。</summary>
    Direct,

    /// <summary>代理：设置里的代理（强制代理填的地址，或系统代理）。</summary>
    Proxy,
}

/// <summary>
/// 每个站点（主机名[:端口]）上次走通的路。只存本机（<see cref="DesktopPaths.RouteMemoryFile"/>），不上传。
/// <list type="bullet">
/// <item>记为直连的站点：下次直接直连；直连失败时当场改走代理（失败即重探）。</item>
/// <item>记为代理的站点：下次直接走代理；超过 <see cref="DefaultReprobeAfter"/> 后由一个请求重探一次直连，
/// 通了就改回直连，免得一直多花代理流量。</item>
/// </list>
/// 线程安全。
/// </summary>
public sealed class RouteMemory
{
    /// <summary>记为“代理”的站点隔多久重探一次直连。</summary>
    public static readonly TimeSpan DefaultReprobeAfter = TimeSpan.FromMinutes(60);

    private const int MaxEntries = 2000;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _filePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _reprobeAfter;
    private Timer? _saveTimer;
    private bool _dirty;

    /// <param name="filePath">持久化文件；不传则只在内存里（单元测试、工具代码）。</param>
    /// <param name="now">当前时间（测试用）。</param>
    /// <param name="reprobeAfter">记为代理的站点多久后重探直连。</param>
    public RouteMemory(string? filePath = null, Func<DateTimeOffset>? now = null, TimeSpan? reprobeAfter = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _reprobeAfter = reprobeAfter ?? DefaultReprobeAfter;
        Load();
    }

    /// <summary>记忆用的键：小写主机名，非默认端口时带端口。</summary>
    public static string KeyFor(Uri uri) =>
        (uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}").ToLowerInvariant();

    /// <summary>该站点记住的路（不管是否到了重探时间）；没记过返回 null。</summary>
    public NetworkRoute? Peek(string key)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Route : null;
        }
    }

    public NetworkRoute? Peek(Uri uri) => Peek(KeyFor(uri));

    /// <summary>
    /// 这次请求先走哪条路。记为代理且到了重探时间时，只有第一个来问的请求拿到“先直连”（认领重探），
    /// 同一时刻的其它请求继续走代理，不会一起去撞直连超时。
    /// </summary>
    public NetworkRoute FirstRouteFor(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Route == NetworkRoute.Direct)
            {
                return NetworkRoute.Direct;
            }

            var now = _now();
            if (now - entry.At < _reprobeAfter)
            {
                return NetworkRoute.Proxy;
            }

            _entries[key] = entry with { At = now };
            return NetworkRoute.Direct;
        }
    }

    /// <summary>记下这个站点刚走通的路。</summary>
    public void Remember(string key, NetworkRoute route)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing) && existing.Route == route)
            {
                // 路没变：不刷新时间。代理的时间戳表示“上次试直连是什么时候”（记下时或重探时），
                // 如果每次代理成功都刷新，常用的站点就永远等不到重探，会一直花代理流量。
                return;
            }

            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
            {
                TrimOldest();
            }

            _entries[key] = new Entry(route, _now());
            ScheduleSave();
        }
    }

    /// <summary>（直连站点数，代理站点数）。设置页显示用。</summary>
    public (int Direct, int Proxy) Count()
    {
        lock (_sync)
        {
            var proxy = _entries.Values.Count(entry => entry.Route == NetworkRoute.Proxy);
            return (_entries.Count - proxy, proxy);
        }
    }

    /// <summary>忘掉全部记忆（下次每个站点都重新先试直连）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            ScheduleSave();
        }
    }

    /// <summary>把还没落盘的改动立刻写入文件（退出前、测试里调用）。写失败不抛出：记忆丢了只是多探一次。</summary>
    public void Flush()
    {
        string json;
        lock (_sync)
        {
            if (!_dirty || _filePath is null)
            {
                _dirty = false;
                return;
            }

            _dirty = false;
            var file = new RouteMemoryFile
            {
                Hosts = _entries.ToDictionary(
                    pair => pair.Key,
                    pair => (RouteMemoryFileEntry?)new RouteMemoryFileEntry
                    {
                        Route = pair.Value.Route == NetworkRoute.Proxy ? "proxy" : "direct",
                        At = pair.Value.At,
                    },
                    StringComparer.OrdinalIgnoreCase),
            };
            json = JsonSerializer.Serialize(file, JsonOptions);
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // 只是缓存，写不进去不影响功能。
        }
    }

    private void Load()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<RouteMemoryFile>(File.ReadAllText(_filePath), JsonOptions);
            if (file?.Hosts is null)
            {
                return;
            }

            foreach (var (host, entry) in file.Hosts)
            {
                if (string.IsNullOrWhiteSpace(host) || entry is null || _entries.Count >= MaxEntries)
                {
                    continue;
                }

                var route = string.Equals(entry.Route, "proxy", StringComparison.OrdinalIgnoreCase)
                    ? NetworkRoute.Proxy
                    : NetworkRoute.Direct;
                _entries[host.Trim().ToLowerInvariant()] = new Entry(route, entry.At);
            }
        }
        catch
        {
            // 文件损坏按没有记忆处理。
        }
    }

    private void TrimOldest()
    {
        foreach (var key in _entries.OrderBy(pair => pair.Value.At).Take(MaxEntries / 10).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private void ScheduleSave()
    {
        _dirty = true;
        if (_filePath is null)
        {
            return;
        }

        // 一次搜索会同时记下几十个站点：攒 2 秒写一次文件。
        _saveTimer ??= new Timer(static state => ((RouteMemory)state!).Flush(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private readonly record struct Entry(NetworkRoute Route, DateTimeOffset At);

    private sealed class RouteMemoryFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, RouteMemoryFileEntry?>? Hosts { get; set; }
    }

    private sealed class RouteMemoryFileEntry
    {
        public string? Route { get; set; }

        public DateTimeOffset At { get; set; }
    }
}

/// <summary>
/// 直连优先、代理兜底的 HTTP 处理器。搜索、点播下载 / 边下边播、广告识别、可播性探测都经过它。
/// <list type="number">
/// <item>代理模式是“直连”，或这个地址没有可用代理（系统代理模式下系统没开代理），或目标是本机：
/// 只走直连，不设短超时，不兜底——与没有分流时完全一样。</item>
/// <item>否则按 <see cref="RouteMemory"/>：没记过 / 记为直连 → 先直连，等响应头最多
/// <see cref="DefaultDirectAttemptTimeout"/>，失败（连不上、超时、403、451）立刻改走代理；
/// 记为代理 → 直接走代理，代理失败再试一次直连。</item>
/// <item>哪条路走通就记哪条。两条都不通不改记忆，把失败原样交回调用方。</item>
/// </list>
/// 带请求体的请求（POST 等）不能安全重发：按记住的路只发一次。
/// </summary>
public sealed class SmartRouteHandler : HttpMessageHandler
{
    /// <summary>直连尝试等响应头的时限：到点还没应答就换代理，不等满调用方的 20 秒。</summary>
    public static readonly TimeSpan DefaultDirectAttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpMessageInvoker _direct;
    private readonly HttpMessageInvoker _proxy;
    private readonly Func<Uri, bool> _proxyAvailable;
    private readonly RouteMemory _memory;
    private readonly TimeSpan _directAttemptTimeout;

    /// <param name="direct">不走代理的处理器。</param>
    /// <param name="proxy">走设置里代理的处理器。</param>
    /// <param name="proxyAvailable">当前设置下，这个地址有没有代理可走（直连模式恒为 false）。</param>
    public SmartRouteHandler(
        HttpMessageHandler direct,
        HttpMessageHandler proxy,
        Func<Uri, bool> proxyAvailable,
        RouteMemory memory,
        TimeSpan? directAttemptTimeout = null)
    {
        _direct = new HttpMessageInvoker(direct ?? throw new ArgumentNullException(nameof(direct)), disposeHandler: true);
        _proxy = new HttpMessageInvoker(proxy ?? throw new ArgumentNullException(nameof(proxy)), disposeHandler: true);
        _proxyAvailable = proxyAvailable ?? throw new ArgumentNullException(nameof(proxyAvailable));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _directAttemptTimeout = directAttemptTimeout ?? DefaultDirectAttemptTimeout;
    }

    /// <summary>
    /// 用真实的网络处理器组装：直连一路 <c>UseProxy=false</c>，代理一路用 <see cref="PlaybackWebProxy"/>
    /// （每个请求现读当前代理设置，所以设置页切换后下一个请求就生效）。
    /// </summary>
    /// <param name="configure">两路共同的选项（重定向、解压、证书校验等）。</param>
    public static SmartRouteHandler Create(
        Func<PlaybackProxyDecision> current,
        RouteMemory memory,
        Action<HttpClientHandler>? configure = null,
        TimeSpan? directAttemptTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var webProxy = new PlaybackWebProxy(current);
        var direct = new HttpClientHandler();
        var proxy = new HttpClientHandler();
        configure?.Invoke(direct);
        configure?.Invoke(proxy);
        direct.UseProxy = false;
        direct.Proxy = null;
        proxy.UseProxy = true;
        proxy.Proxy = webProxy;
        return new SmartRouteHandler(direct, proxy, uri => HasProxy(webProxy, uri), memory, directAttemptTimeout);
    }

    private static bool HasProxy(PlaybackWebProxy webProxy, Uri uri)
    {
        try
        {
            return webProxy.GetProxy(uri) is not null;
        }
        catch
        {
            // 系统代理查询出错：当作没有代理，按直连走。
            return false;
        }
    }

    /// <summary>这些状态说明“这条路被挡了”（地区限制、防火墙页面），换一条路可能就通。</summary>
    public static bool IsBlockedStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.UnavailableForLegalReasons;

    private static bool IsProxyFailure(HttpStatusCode status) =>
        IsBlockedStatus(status) || status == HttpStatusCode.ProxyAuthenticationRequired;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null
            || !uri.IsAbsoluteUri
            || PlaybackWebProxy.IsLocalDestination(uri)
            || !_proxyAvailable(uri))
        {
            return await _direct.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var key = RouteMemory.KeyFor(uri);
        if (request.Content is not null)
        {
            var remembered = _memory.Peek(key) == NetworkRoute.Proxy ? _proxy : _direct;
            return await remembered.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return _memory.FirstRouteFor(key) == NetworkRoute.Proxy
            ? await SendProxyFirstAsync(request, key, cancellationToken).ConfigureAwait(false)
            : await SendDirectFirstAsync(request, key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendDirectFirstAsync(HttpRequestMessage request, string key, CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        var direct = await TryDirectAsync(request, cancellationToken).ConfigureAwait(false);
        if (direct.Succeeded)
        {
            _memory.Remember(key, NetworkRoute.Direct);
            return direct.Response!;
        }

        // 直连那一次如果跟过重定向，请求地址已被改写；换路时从原地址重新来。
        request.RequestUri = originalUri;
        HttpResponseMessage viaProxy;
        try
        {
            viaProxy = await _proxy.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 直连确实没通，代理还没来得及应答调用方就到时限了（直连的短超时占掉了一部分时间）。
            // 先记成代理，下次不再把时间花在直连上；记错了也会自己纠正（代理失败时会回头试直连，到期也会重探）。
            if (direct.Response is null)
            {
                _memory.Remember(key, NetworkRoute.Proxy);
            }

            direct.Response?.Dispose();
            throw;
        }
        catch when (direct.Response is not null)
        {
            // 代理也走不通：把直连拿到的应答（403 / 451）交回去，记忆不动。
            return direct.Response;
        }

        if (IsProxyFailure(viaProxy.StatusCode))
        {
            // 两条路都被挡（或代理自己要认证）：不是换路能解决的，记忆不动。有直连应答就交回直连的。
            if (direct.Response is not null)
            {
                viaProxy.Dispose();
                return direct.Response;
            }

            return viaProxy;
        }

        direct.Response?.Dispose();
        _memory.Remember(key, NetworkRoute.Proxy);
        return viaProxy;
    }

    private async Task<HttpResponseMessage> SendProxyFirstAsync(HttpRequestMessage request, string key, CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        HttpResponseMessage? viaProxy = null;
        Exception? proxyError = null;
        try
        {
            viaProxy = await _proxy.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!IsProxyFailure(viaProxy.StatusCode))
            {
                _memory.Remember(key, NetworkRoute.Proxy);
                return viaProxy;
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            proxyError = error;
        }

        // 代理这次没走通（代理没开、被挡）：失败即重探，回头试一次直连。
        request.RequestUri = originalUri;
        var direct = await TryDirectAsync(request, cancellationToken).ConfigureAwait(false);
        if (direct.Succeeded)
        {
            viaProxy?.Dispose();
            _memory.Remember(key, NetworkRoute.Direct);
            return direct.Response!;
        }

        direct.Response?.Dispose();
        if (viaProxy is not null)
        {
            return viaProxy;
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(proxyError!).Throw();
        throw proxyError!;
    }

    /// <summary>
    /// 直连试一次，只等响应头，最多等 <see cref="_directAttemptTimeout"/>。
    /// 调用方自己取消时原样抛出；其余失败（连不上、短超时）返回“未成功”，被挡的应答（403 / 451）一并带回。
    /// </summary>
    private async Task<DirectAttempt> TryDirectAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_directAttemptTimeout);
        try
        {
            var response = await _direct.SendAsync(request, attempt.Token).ConfigureAwait(false);
            return new DirectAttempt(!IsBlockedStatus(response.StatusCode), response);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new DirectAttempt(false, null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _direct.Dispose();
            _proxy.Dispose();
        }

        base.Dispose(disposing);
    }

    private readonly record struct DirectAttempt(bool Succeeded, HttpResponseMessage? Response);
}

/// <summary>设置页“代理 / 分流”的说明文字（集中在 Core，便于测试，两处界面用同一份）。</summary>
public static class SmartRoutingText
{
    public const string Summary =
        "直连优先：搜索、点播（弱网缓存开启时，默认开启）、下载、广告识别和可播性检测的请求都先直连，"
        + "约 5 秒内连不上或被拒绝才改走代理，所以代理流量只花在直连不通的源上。"
        + "每个站点走通的路会记在本机（不上传）：直连通的下次直接直连；要走代理的下次直接走代理，"
        + "约 1 小时后或代理失败时重新试一次直连。"
        + "选“直连”时只走直连、不兜底。"
        + "直播以及关闭弱网缓存后的点播由播放内核自行联网，只能参考已记住的结果，不保证按这里的规则走。"
        + "豆瓣推荐、TMDB、海报、订阅与更新检查不受这里影响，仍跟随系统代理。";

    /// <summary>设置页状态行：当前模式实际怎么走。</summary>
    public static string Describe(PlaybackProxyDecision decision) => decision.Mode switch
    {
        PlaybackProxyModes.ForcedProxy when decision.ProxyUri is not null =>
            $"直连优先，直连不通的源走 {PlaybackProxyAddress.Describe(decision.ProxyUri)}",
        PlaybackProxyModes.Direct => "只走直连，不走代理、不兜底",
        _ => "直连优先，直连不通的源走系统代理（系统没开代理时等同直连）",
    };

    /// <summary>设置页状态行后半句：已经记住了多少站点。</summary>
    public static string DescribeMemory((int Direct, int Proxy) count) =>
        count.Direct + count.Proxy == 0
            ? "还没有记住任何站点的线路"
            : $"已记住 {count.Direct} 个直连站点、{count.Proxy} 个需要代理的站点";
}
