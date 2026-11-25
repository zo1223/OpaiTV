using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 弹幕取数用的最小 HTTP 接口:统一超时,失败一律抛异常,由上层吞掉(弹幕失败不影响播放)。
/// 测试里整个替换成假实现。返回的 <see cref="JsonDocument"/> 由调用方释放。
/// </summary>
public interface IDanmakuHttp
{
    Task<JsonDocument> GetJsonAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct);

    Task<JsonDocument> PostJsonAsync(
        string url,
        string jsonBody,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>取原始字节(B 站弹幕 XML 是 deflate 压缩的)。</summary>
    Task<byte[]> GetBytesAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>基于 <see cref="HttpClient"/> 的实现。非 2xx 抛 <see cref="HttpRequestException"/>,超时抛 <see cref="OperationCanceledException"/>。</summary>
public sealed class DanmakuHttp : IDanmakuHttp, IDanmakuRawHttp
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    private const int MaxResponseBytes = 32 * 1024 * 1024;

    private readonly HttpClient _client;

    public DanmakuHttp(HttpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 建一个适合取弹幕的 <see cref="HttpClient"/>。
    /// 只让它自动解 gzip/br:B 站弹幕 XML 标的是 deflate,实际是不带 zlib 头的裸 deflate,
    /// .NET 自动解压会报错,所以 deflate 留给 <see cref="GetBytesAsync"/> 之后的解码自己处理。
    /// </summary>
    public static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Brotli,
            // B 站的 Cookie 由来源自己带在请求头里,不要让处理器另外维护一份
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public async Task<JsonDocument> GetJsonAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var bytes = await SendAsync(request, headers, timeout, decode: true, ct).ConfigureAwait(false);
        return ParseJson(bytes);
    }

    public async Task<JsonDocument> PostJsonAsync(
        string url,
        string jsonBody,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonBody));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;
        var bytes = await SendAsync(request, headers, timeout, decode: true, ct).ConfigureAwait(false);
        return ParseJson(bytes);
    }

    public async Task<byte[]> GetBytesAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, headers, timeout, decode: false, ct).ConfigureAwait(false);
    }

    public async Task<DanmakuRawResponse> SendRawAsync(
        string method,
        string url,
        string? body,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            var contentType = headers?.FirstOrDefault(pair => string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            content.Headers.TryAddWithoutValidation("Content-Type", string.IsNullOrEmpty(contentType) ? "application/x-www-form-urlencoded" : contentType);
            request.Content = content;
        }

        var setCookie = "";
        var bytes = await SendAsync(
                request,
                headers,
                timeout,
                decode: true,
                ct,
                response => setCookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join("; ", values) : "")
            .ConfigureAwait(false);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new DanmakuRawResponse(Encoding.UTF8.GetString(bytes, start, bytes.Length - start), setCookie);
    }

    private async Task<byte[]> SendAsync(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        bool decode,
        CancellationToken ct,
        Action<HttpResponseMessage>? inspect = null)
    {
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout > TimeSpan.Zero ? timeout : DefaultTimeout);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        inspect?.Invoke(response);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new HttpRequestException("response too large");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        if (bytes.Length > MaxResponseBytes)
        {
            throw new HttpRequestException("response too large");
        }

        // HttpClient 自动解压后会去掉 Content-Encoding;还留着说明没解,JSON 接口在这里补解
        return decode ? DecodeContent(bytes, response.Content.Headers.ContentEncoding) : bytes;
    }

    private static byte[] DecodeContent(byte[] bytes, ICollection<string> encodings)
    {
        foreach (var encoding in encodings.Reverse())
        {
            bytes = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => DanmakuJs.Inflate(bytes, static stream => new GZipStream(stream, CompressionMode.Decompress)),
                "br" => DanmakuJs.Inflate(bytes, static stream => new BrotliStream(stream, CompressionMode.Decompress)),
                "deflate" => DanmakuJs.InflateAny(bytes) ?? throw new InvalidDataException("bad deflate body"),
                _ => bytes,
            };
        }

        return bytes;
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        var memory = bytes.AsMemory();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            memory = memory[3..];
        }

        return JsonDocument.Parse(memory);
    }
}

/// <summary>一个弹幕来源。匹配不到返回 null;网络失败抛异常(由 <see cref="DanmakuService"/> 吞掉)。</summary>
public interface IDanmakuProvider
{
    string Name { get; }

    Task<DanmakuTrack?> ResolveAsync(DanmakuQuery query, CancellationToken ct = default);
}

/// <summary>
/// 弹幕取数入口。
///
/// 默认来源是内置直连:按片名和集数依次去腾讯视频、哔哩哔哩、爱奇艺、优酷、芒果TV、咪咕视频的公开接口匹配并取弹幕,
/// 不需要登录,也不需要任何服务器。设置里填了自定义 danmu_api 地址时先问它,
/// 它匹配不到或访问失败再回到内置来源。
///
/// 任何一步失败都只是"没有弹幕":<see cref="ResolveAsync"/> 不抛异常。
/// </summary>
public sealed class DanmakuService
{
    private readonly IDanmakuHttp _http;
    private readonly IReadOnlyList<IDanmakuProvider> _builtin;
    private readonly object _sync = new();
    private string _serverUrl = "";
    private IDanmakuProvider? _server;

    public DanmakuService(IDanmakuHttp http)
    {
        _http = http;
        // 按优先级依次尝试,匹配到就用。原有的两个来源排在前面,原来能匹配到的片行为不变。
        _builtin =
        [
            new TencentDanmakuProvider(http),
            new BilibiliDanmakuProvider(http),
            new IqiyiDanmakuProvider(http),
            new YoukuDanmakuProvider(http),
            new MangoDanmakuProvider(http),
            new MiguDanmakuProvider(http),
        ];
    }

    /// <summary>给一集视频找弹幕;匹配不到、全部来源失败或被取消时返回 null。</summary>
    public async Task<DanmakuTrack?> ResolveAsync(DanmakuQuery query, string? serverUrl, CancellationToken ct = default) =>
        (await ResolveDetailedAsync(query, serverUrl, ct).ConfigureAwait(false)).Track;

    /// <summary>
    /// 给一集视频找弹幕,并说明没找到的原因,供播放页提示用户:
    /// 各来源都正常应答但没有这部片/这一集是 <see cref="DanmakuResolveOutcome.NotFound"/>;
    /// 没有匹配到且至少一个来源访问出错(断网、超时、接口报错)是 <see cref="DanmakuResolveOutcome.Failed"/>。
    /// 不抛异常。
    /// </summary>
    public async Task<DanmakuResolveResult> ResolveDetailedAsync(
        DanmakuQuery query,
        string? serverUrl,
        CancellationToken ct = default)
    {
        var failed = false;
        try
        {
            if (query is null || string.IsNullOrWhiteSpace(query.Title))
            {
                return DanmakuResolveResult.NotFound;
            }

            var normalized = query with
            {
                EpisodeIndex = Math.Max(0, query.EpisodeIndex),
                EpisodeNumber = Math.Max(1, query.EpisodeNumber),
                EpisodeCount = Math.Max(1, query.EpisodeCount),
            };

            var providers = new List<IDanmakuProvider>(_builtin.Count + 1);
            var url = DanmakuSettingsRules.NormalizeServerUrl(serverUrl);
            if (url.Length > 0)
            {
                lock (_sync)
                {
                    if (_server is null || _serverUrl != url)
                    {
                        _serverUrl = url;
                        _server = new ServerDanmakuProvider(_http, url);
                    }

                    providers.Add(_server);
                }
            }

            providers.AddRange(_builtin);

            foreach (var provider in providers)
            {
                if (ct.IsCancellationRequested)
                {
                    return DanmakuResolveResult.Cancelled;
                }

                try
                {
                    var track = await provider.ResolveAsync(normalized, ct).ConfigureAwait(false);
                    if (track is not null)
                    {
                        return new DanmakuResolveResult(track, DanmakuResolveOutcome.Found);
                    }
                }
                catch
                {
                    // 这个来源不可用,换下一个
                    failed = true;
                }
            }

            if (ct.IsCancellationRequested)
            {
                return DanmakuResolveResult.Cancelled;
            }

            return failed ? DanmakuResolveResult.Failed : DanmakuResolveResult.NotFound;
        }
        catch
        {
            return ct.IsCancellationRequested ? DanmakuResolveResult.Cancelled : DanmakuResolveResult.Failed;
        }
    }
}

/// <summary>找弹幕的结果类别。</summary>
public enum DanmakuResolveOutcome
{
    /// <summary>匹配到了。</summary>
    Found,

    /// <summary>来源都正常应答,但没有这部片或这一集。</summary>
    NotFound,

    /// <summary>没有匹配到,且至少一个来源访问出错。</summary>
    Failed,

    /// <summary>调用方取消了(换集、关掉弹幕、离开播放页),不需要提示。</summary>
    Cancelled,
}

public sealed record DanmakuResolveResult(DanmakuTrack? Track, DanmakuResolveOutcome Outcome)
{
    public static readonly DanmakuResolveResult NotFound = new(null, DanmakuResolveOutcome.NotFound);
    public static readonly DanmakuResolveResult Failed = new(null, DanmakuResolveOutcome.Failed);
    public static readonly DanmakuResolveResult Cancelled = new(null, DanmakuResolveOutcome.Cancelled);
}

/// <summary>播放页上弹幕状态的轻提示文字(纯文本规则,有单元测试)。</summary>
public static class DanmakuNotice
{
    public const string Off = "弹幕已关闭";
    public const string Loading = "弹幕已开启，正在匹配…";
    public const string NotFound = "未找到该集弹幕";
    public const string Failed = "弹幕获取失败，请检查网络后重试";
    public const string LocalFile = "本地视频不匹配弹幕";
    public const string RenderFailed = "弹幕显示出错，本集已停止显示";

    public static string ProviderLabel(string? provider) => provider switch
    {
        "tencent" => "腾讯视频",
        "bilibili" => "哔哩哔哩",
        "iqiyi" => "爱奇艺",
        "youku" => "优酷",
        "mango" => "芒果TV",
        "migu" => "咪咕视频",
        "server" => "自定义弹幕服务器",
        _ => "",
    };

    /// <summary>匹配结果对应的提示;被取消时没有提示(null)。</summary>
    public static string? ForResult(DanmakuResolveResult result) => result.Outcome switch
    {
        DanmakuResolveOutcome.Found => ProviderLabel(result.Track?.Provider) is { Length: > 0 } label
            ? $"弹幕已加载 · {label}"
            : "弹幕已加载",
        DanmakuResolveOutcome.NotFound => NotFound,
        DanmakuResolveOutcome.Failed => Failed,
        _ => null,
    };
}

/// <summary>把 JSON 当 JavaScript 值来读的小工具:字段缺失或类型不对时不抛异常,取值规则与手机版的 JS 一致。</summary>
internal static class DanmakuJs
{
    internal static JsonElement? Prop(this JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var found)
            ? found
            : null;

    internal static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var found) ? found : null;

    /// <summary>数组的元素;不是数组时为空。</summary>
    internal static IEnumerable<JsonElement> Items(this JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            yield return item;
        }
    }

    internal static JsonElement? First(this JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0)
        {
            return array[0];
        }

        return null;
    }

    /// <summary>JS 的 String(value ?? "")。</summary>
    internal static string Text(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => NumberText(value),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => value.GetRawText(),
        };
    }

    /// <summary>JS 的真值判断。</summary>
    internal static bool Truthy(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0 && !double.IsNaN(number),
            JsonValueKind.True => true,
            JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };
    }

    /// <summary>JS 的 Number(value):缺失为 NaN,null 和空串为 0,不是数字的字符串为 NaN。</summary>
    internal static double Number(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return double.NaN;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) ? number : double.NaN;
            case JsonValueKind.Null:
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.String:
                var text = (value.GetString() ?? "").Trim();
                if (text.Length == 0)
                {
                    return 0;
                }

                return double.TryParse(
                    text,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : double.NaN;
            default:
                return double.NaN;
        }
    }

    /// <summary>JS 的 Number(value) || 0,再取整数部分。</summary>
    internal static long NumberOrZero(this JsonElement? element)
    {
        var number = element.Number();
        return double.IsFinite(number) ? (long)number : 0;
    }

    /// <summary>JS 的 Number(value) || 0(保留小数)。</summary>
    internal static double NumberOrZeroDouble(this JsonElement? element)
    {
        var number = element.Number();
        return double.IsFinite(number) ? number : 0;
    }

    /// <summary>JS 的 String.prototype.trim:除了空白还去掉 BOM(U+FEFF)。</summary>
    internal static string Trim(string? text)
    {
        var value = text ?? "";
        var start = 0;
        var end = value.Length;
        while (start < end && IsJsWhitespace(value[start]))
        {
            start++;
        }

        while (end > start && IsJsWhitespace(value[end - 1]))
        {
            end--;
        }

        return start == 0 && end == value.Length ? value : value[start..end];
    }

    private static bool IsJsWhitespace(char c) => char.IsWhiteSpace(c) || c == '\uFEFF';

    /// <summary>JS 的 value === "text"。</summary>
    internal static bool Is(this JsonElement? element, string text) =>
        element is { ValueKind: JsonValueKind.String } value && value.GetString() == text;

    /// <summary>JS 的 value === number。</summary>
    internal static bool Is(this JsonElement? element, double number) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var actual) && actual == number;

    private static string NumberText(JsonElement value)
    {
        if (value.TryGetInt64(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        return value.TryGetDouble(out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : value.GetRawText();
    }

    /// <summary>JS 的 parseFloat:取开头能认出的数字,认不出为 NaN。</summary>
    internal static double ParseFloat(string? text)
    {
        var value = (text ?? "").TrimStart();
        var i = 0;
        if (i < value.Length && (value[i] == '+' || value[i] == '-'))
        {
            i++;
        }

        if (string.CompareOrdinal(value, i, "Infinity", 0, 8) == 0)
        {
            return value.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        }

        var digits = 0;
        while (i < value.Length && char.IsAsciiDigit(value[i]))
        {
            i++;
            digits++;
        }

        if (i < value.Length && value[i] == '.')
        {
            var j = i + 1;
            var fraction = 0;
            while (j < value.Length && char.IsAsciiDigit(value[j]))
            {
                j++;
                fraction++;
            }

            if (digits > 0 || fraction > 0)
            {
                i = j;
                digits += fraction;
            }
        }

        if (digits == 0)
        {
            return double.NaN;
        }

        if (i < value.Length && (value[i] == 'e' || value[i] == 'E'))
        {
            var j = i + 1;
            if (j < value.Length && (value[j] == '+' || value[j] == '-'))
            {
                j++;
            }

            var exponent = 0;
            while (j < value.Length && char.IsAsciiDigit(value[j]))
            {
                j++;
                exponent++;
            }

            if (exponent > 0)
            {
                i = j;
            }
        }

        return double.TryParse(value.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
    }

    /// <summary>JS 的 parseInt(text, radix):取开头能认出的整数,认不出为 null(NaN)。</summary>
    internal static long? ParseInt(string? text, int radix = 10)
    {
        var value = (text ?? "").TrimStart();
        var i = 0;
        var negative = false;
        if (i < value.Length && (value[i] == '+' || value[i] == '-'))
        {
            negative = value[i] == '-';
            i++;
        }

        if (radix == 16 && i + 1 < value.Length && value[i] == '0' && (value[i + 1] == 'x' || value[i + 1] == 'X'))
        {
            i += 2;
        }

        long result = 0;
        var digits = 0;
        for (; i < value.Length; i++)
        {
            var digit = DigitOf(value[i]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            digits++;
            if (result < long.MaxValue / 32)
            {
                result = result * radix + digit;
            }
        }

        if (digits == 0)
        {
            return null;
        }

        return negative ? -result : result;
    }

    /// <summary>JS 的 (value &amp; 0xffffff):解析不出时为白色。</summary>
    internal static int ColorOf(long? value) => value is { } number ? (int)(number & 0xffffff) : 0xffffff;

    private static int DigitOf(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>JS 的 Math.round(.5 向正无穷进位)。</summary>
    internal static double Round(double value) => Math.Floor(value + 0.5);

    /// <summary>JS 的 encodeURIComponent。</summary>
    internal static string EncodeUriComponent(string text)
    {
        var builder = new StringBuilder(text.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "-_.!~*'()".Contains(c))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    internal static byte[] Inflate(byte[] bytes, Func<Stream, Stream> open)
    {
        using var input = new MemoryStream(bytes);
        using var stream = open(input);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>依次按 zlib、gzip、裸 deflate 解压(先看文件头决定顺序);都解不开返回 null。</summary>
    internal static byte[]? InflateAny(byte[] bytes)
    {
        var looksZlib = bytes.Length >= 2 && (bytes[0] & 0x0F) == 8 && ((bytes[0] << 8) | bytes[1]) % 31 == 0;
        var looksGzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        var openers = new List<Func<Stream, Stream>>(3);
        Func<Stream, Stream> raw = static stream => new DeflateStream(stream, CompressionMode.Decompress);
        Func<Stream, Stream> zlib = static stream => new ZLibStream(stream, CompressionMode.Decompress);
        Func<Stream, Stream> gzip = static stream => new GZipStream(stream, CompressionMode.Decompress);
        if (looksGzip)
        {
            openers.AddRange([gzip, raw, zlib]);
        }
        else if (looksZlib)
        {
            openers.AddRange([zlib, raw, gzip]);
        }
        else
        {
            openers.AddRange([raw, zlib, gzip]);
        }

        foreach (var open in openers)
        {
            try
            {
                var result = Inflate(bytes, open);
                if (result.Length > 0)
                {
                    return result;
                }
            }
            catch
            {
                // 换下一种封装再试
            }
        }

        return null;
    }
}
