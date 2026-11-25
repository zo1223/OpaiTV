using System.Net;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 播放代理模式的三档取值（持久化在 settings.json 里的字符串）。
/// 没设置或值损坏按“系统代理”处理，与加这个设置之前的行为一致。
/// </summary>
public static class PlaybackProxyModes
{
    /// <summary>系统代理：跟随操作系统的代理设置（默认）。</summary>
    public const string SystemProxy = "system";

    /// <summary>强制代理：播放请求都走用户填写的代理地址。</summary>
    public const string ForcedProxy = "forced";

    /// <summary>直连：播放请求不走任何代理。</summary>
    public const string Direct = "direct";

    public static IReadOnlyList<string> All { get; } = [SystemProxy, ForcedProxy, Direct];

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ForcedProxy => ForcedProxy,
        Direct => Direct,
        _ => SystemProxy,
    };

    /// <summary>
    /// 设置文件里的取值规整：强制代理必须配一个能解析的地址，否则退回系统代理。
    /// 这样设置页显示的模式永远等于实际生效的模式，不会出现“显示强制代理、实际没走代理”。
    /// </summary>
    public static string NormalizeForSettings(string? mode, string? proxyUrl)
    {
        var normalized = Normalize(mode);
        return normalized == ForcedProxy && !PlaybackProxyAddress.TryParse(proxyUrl, out _)
            ? SystemProxy
            : normalized;
    }

    public static string DisplayName(string? mode) => Normalize(mode) switch
    {
        ForcedProxy => "强制代理",
        Direct => "直连",
        _ => "系统代理",
    };
}

/// <summary>用户填写的代理地址。只支持 HTTP 代理：host:port 或 http://[user:pass@]host:port。</summary>
public static class PlaybackProxyAddress
{
    public const string FormatHint = "只支持 HTTP 代理，格式 host:port 或 http://host:port，例如 http://127.0.0.1:7890";

    public static string NormalizeText(string? input) => (input ?? string.Empty).Trim();

    public static bool TryParse(string? input, out Uri proxyUri)
    {
        proxyUri = null!;
        var text = NormalizeText(input);
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        // 转发代理没有路径；带路径的多半是把中转网址（https://x/?url=）填进来了，桌面端不支持这种形式。
        if (uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        // 端口必须写明：漏写端口时 Uri 会悄悄补成 80，几乎一定不是用户想要的。
        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        var hostAndPort = authority[(authority.LastIndexOf('@') + 1)..];
        var portSeparator = hostAndPort.LastIndexOf(':');
        if (portSeparator <= 0
            || hostAndPort.EndsWith(']')
            || !int.TryParse(hostAndPort[(portSeparator + 1)..], out var port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        proxyUri = uri;
        return true;
    }

    /// <summary>界面与日志里显示的代理地址：去掉账号密码。</summary>
    public static string Describe(Uri proxyUri) => $"http://{proxyUri.Host}:{proxyUri.Port}";
}

/// <summary>某一时刻生效的播放代理决定（由设置算出，不可变）。</summary>
public sealed record PlaybackProxyDecision
{
    private PlaybackProxyDecision(string mode, Uri? proxyUri)
    {
        Mode = mode;
        ProxyUri = proxyUri;
    }

    public static PlaybackProxyDecision FollowSystem { get; } = new(PlaybackProxyModes.SystemProxy, null);

    public static PlaybackProxyDecision NoProxy { get; } = new(PlaybackProxyModes.Direct, null);

    public string Mode { get; }

    /// <summary>仅强制代理模式有值。</summary>
    public Uri? ProxyUri { get; }

    public static PlaybackProxyDecision From(string? mode, string? proxyUrl)
    {
        switch (PlaybackProxyModes.Normalize(mode))
        {
            case PlaybackProxyModes.Direct:
                return NoProxy;
            case PlaybackProxyModes.ForcedProxy:
                // 地址无效的强制代理按系统代理处理（与 NormalizeForSettings 一致）。
                return PlaybackProxyAddress.TryParse(proxyUrl, out var uri)
                    ? new PlaybackProxyDecision(PlaybackProxyModes.ForcedProxy, uri)
                    : FollowSystem;
            default:
                return FollowSystem;
        }
    }

    public static PlaybackProxyDecision From(DesktopSettings? settings) =>
        settings is null ? FollowSystem : From(settings.PlaybackProxyMode, settings.PlaybackProxyUrl);

    /// <summary>
    /// LibVLC 自己联网播放（直播、关闭弱网缓存的点播、解码探测）时追加的媒体选项。
    /// 只有强制代理会追加 <c>:http-proxy</c>；系统代理与直连不追加任何选项，保持 LibVLC 原有行为。
    /// 本机回环地址（弱网缓存的本地服务）与本地文件永远不走代理。
    /// 注意这只是尽力而为：读 VLC 3.0 源码，Windows 上它的主 http/https 访问模块只从全局配置取代理
    /// （不读系统代理，也读不到媒体选项），这个选项只有旧版 http 模块会用到。
    /// 能可靠按模式走的是 <see cref="PlaybackWebProxy"/> 接管的那些请求。
    /// </summary>
    public IReadOnlyList<string> LibVlcOptionsFor(Uri mediaUri)
    {
        if (Mode != PlaybackProxyModes.ForcedProxy
            || ProxyUri is null
            || !mediaUri.IsAbsoluteUri
            || mediaUri.Scheme is not ("http" or "https")
            || PlaybackWebProxy.IsLocalDestination(mediaUri))
        {
            return [];
        }

        return [$":http-proxy={ProxyUri.GetLeftPart(UriPartial.Authority)}"];
    }

    /// <summary>
    /// 同上，但参考直连优先分流的线路记忆：这个站点已经记为“直连能通”时不追加代理选项，不花代理流量。
    /// 没记过或记为代理的站点保持原样（强制代理时追加）：播放内核自己联网，没法先直连再换路，
    /// 所以只在确知直连能通时才省掉代理。
    /// </summary>
    public IReadOnlyList<string> LibVlcOptionsFor(Uri mediaUri, RouteMemory? routes)
    {
        var options = LibVlcOptionsFor(mediaUri);
        return options.Count > 0 && routes?.Peek(mediaUri) == NetworkRoute.Direct ? [] : options;
    }
}

/// <summary>
/// 进程内“当前播放代理决定”的取值入口。应用启动时由 AppServices 接到设置上；
/// 每次起播、每个播放请求都现读一次，所以在设置页切换后下一次播放就按新模式走，不用重启。
/// 没接线时（单元测试、工具代码）等同系统代理。
/// </summary>
public static class PlaybackProxyRuntime
{
    private static Func<PlaybackProxyDecision> _current = static () => PlaybackProxyDecision.FollowSystem;

    public static Func<PlaybackProxyDecision> Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? (static () => PlaybackProxyDecision.FollowSystem));
    }

    private static RouteMemory _routes = new();

    /// <summary>
    /// 直连优先分流的线路记忆（每个站点上次走通的是直连还是代理）。应用启动时由 AppServices 换成落盘到本机的那一份；
    /// 没接线时是一份只在内存里的空记忆。
    /// </summary>
    public static RouteMemory Routes
    {
        get => Volatile.Read(ref _routes);
        set => Volatile.Write(ref _routes, value ?? new RouteMemory());
    }
}

/// <summary>
/// 交给播放用 HttpClient 的代理对象。它自己不保存模式，每个请求都向 <c>current</c> 现取：
/// 系统代理 → 转给系统代理（与 HttpClient 默认行为相同）；强制代理 → 用户填写的地址；直连 → 不走代理。
/// 本机回环地址永远直连。
/// </summary>
public sealed class PlaybackWebProxy : IWebProxy, ICredentials
{
    private readonly Func<PlaybackProxyDecision> _current;
    private readonly Func<IWebProxy?> _systemProxy;

    /// <param name="current">当前的代理决定。</param>
    /// <param name="systemProxy">系统代理；不传则用 <see cref="System.Net.Http.HttpClient.DefaultProxy"/>。</param>
    public PlaybackWebProxy(Func<PlaybackProxyDecision> current, Func<IWebProxy?>? systemProxy = null)
    {
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _systemProxy = systemProxy ?? (static () => System.Net.Http.HttpClient.DefaultProxy);
    }

    /// <summary>HttpClient 只在首次使用时读取一次 Credentials，所以这里返回自身，按请求动态取凭据。</summary>
    public ICredentials? Credentials
    {
        get => this;
        set { }
    }

    public Uri? GetProxy(Uri destination)
    {
        if (IsLocalDestination(destination))
        {
            return null;
        }

        var decision = _current();
        switch (decision.Mode)
        {
            case PlaybackProxyModes.Direct:
                return null;
            case PlaybackProxyModes.ForcedProxy:
                return decision.ProxyUri;
            default:
                var system = _systemProxy();
                if (system is null || system.IsBypassed(destination))
                {
                    return null;
                }

                var proxy = system.GetProxy(destination);
                // 老约定：GetProxy 原样返回目标地址表示“不走代理”。
                return proxy is null || proxy == destination ? null : proxy;
        }
    }

    public bool IsBypassed(Uri host)
    {
        if (IsLocalDestination(host))
        {
            return true;
        }

        var decision = _current();
        return decision.Mode switch
        {
            PlaybackProxyModes.Direct => true,
            PlaybackProxyModes.ForcedProxy => decision.ProxyUri is null,
            _ => _systemProxy()?.IsBypassed(host) ?? true,
        };
    }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        var decision = _current();
        switch (decision.Mode)
        {
            case PlaybackProxyModes.Direct:
                return null;
            case PlaybackProxyModes.ForcedProxy:
                return CredentialFromUserInfo(decision.ProxyUri);
            default:
                return _systemProxy()?.Credentials?.GetCredential(uri, authType);
        }
    }

    /// <summary>本机地址（弱网缓存的本地 HTTP 服务等）不应该被送去代理。</summary>
    public static bool IsLocalDestination(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static NetworkCredential? CredentialFromUserInfo(Uri? proxyUri)
    {
        if (proxyUri is null || string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            return null;
        }

        var parts = proxyUri.UserInfo.Split(':', 2);
        return new NetworkCredential(
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }
}

/// <summary>设置页“播放代理”的保存规则（纯函数）。</summary>
public static class PlaybackProxySettings
{
    /// <summary>
    /// 把用户选的模式和填的地址写进设置。选了强制代理但地址不合法时返回 false，设置保持不变
    /// （不会出现“显示强制代理、实际没走代理”）。地址在其它模式下原样保留，方便来回切换。
    /// </summary>
    public static bool TryApply(DesktopSettings current, string? mode, string? proxyUrl, out DesktopSettings updated)
    {
        var normalizedMode = PlaybackProxyModes.Normalize(mode);
        var address = PlaybackProxyAddress.NormalizeText(proxyUrl);
        if (normalizedMode == PlaybackProxyModes.ForcedProxy && !PlaybackProxyAddress.TryParse(address, out _))
        {
            updated = current;
            return false;
        }

        updated = current with { PlaybackProxyMode = normalizedMode, PlaybackProxyUrl = address };
        return true;
    }
}
