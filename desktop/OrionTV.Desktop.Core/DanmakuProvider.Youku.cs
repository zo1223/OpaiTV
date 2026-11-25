using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

// 优酷弹幕来源。与手机版 services/danmaku/youku.ts 逐条对应,改规则时两边一起改。

/// <summary>原始请求的应答:正文文本和 Set-Cookie(多条用 "; " 拼成一串,没有时为空串)。</summary>
public sealed record DanmakuRawResponse(string Text, string SetCookie);

/// <summary>
/// 可选的原始请求能力:能读 Set-Cookie、能发任意正文的 POST。
/// 优酷的弹幕接口要先从响应 Cookie 里拿令牌、再用表单 POST,<see cref="IDanmakuHttp"/> 的三个方法做不到。
/// http 没实现这个接口时,需要它的来源按"访问失败"处理。
/// </summary>
public interface IDanmakuRawHttp
{
    Task<DanmakuRawResponse> SendRawAsync(
        string method,
        string url,
        string? body,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken ct);
}

public sealed record YoukuDanmakuCandidate(string ShowId, string Title, string? Year, int EpisodeCount, bool? IsMovie)
    : DanmakuTitleCandidate(Title, Year, EpisodeCount, IsMovie);

/// <summary>Duration 单位是秒;未知为 0。</summary>
public sealed record YoukuDanmakuEpisode(string Vid, int? Number, string Title, double Duration);

public sealed record YoukuDanmakuEpisodePage(int Total, IReadOnlyList<YoukuDanmakuEpisode> Episodes);

public sealed record YoukuDanmakuToken(string Tk, string Enc);

public sealed record YoukuDanmakuSegmentRequest(string Url, string Body);

/// <summary>
/// 内置弹幕来源:优酷公开接口(搜索 → 分集 → 弹幕分片),不需要登录和服务器。
/// 接口与字段参考 huangxd-/danmu_api 的 youku 源。
///
/// 弹幕接口(mtop)要一个由服务端通过 Set-Cookie 下发的令牌 _m_h5_tk:请求时既要把它放在 Cookie 里,
/// 又要用它的前 32 位参与签名。所以这个来源依赖 <see cref="IDanmakuRawHttp"/>。
/// </summary>
public sealed class YoukuDanmakuProvider : IDanmakuProvider
{
    public const double SegmentSeconds = 60;

    private const string SearchUrl = "https://search.youku.com/api/search";
    private const string EpisodesUrl = "https://openapi.youku.com/v2/shows/videos.json";
    private const string TokenUrl = "https://acs.youku.com/h5/mtop.com.youku.aplatform.weakget/1.0/?jsv=2.5.1&appKey=24679788";
    private const string DanmakuUrl = "https://acs.youku.com/h5/mopen.youku.danmu.list/1.0/";
    private const string ClientId = "53e6cc67237fc59a";
    private const string AppKey = "24679788";
    private const string MsgSalt = "MkmC9SoIw6xCkSKHhJ7b5D2r51kBiREr";
    private const int PageSize = 100;

    private static readonly string[] SkipKeywords = ["中配版", "抢先看", "非正片", "解读", "揭秘", "赏析", "《"];
    private static readonly Regex Tags = new("<[^>]+>");
    private static readonly Regex Labels = new("【.+?】");
    private static readonly Regex YearPattern = new("[12][890][0-9][0-9]");
    private static readonly Regex TkPattern = new(@"_m_h5_tk=([^;,\s]+)");
    private static readonly Regex EncPattern = new(@"_m_h5_tk_enc=([^;,\s]+)");

    private sealed class Series(string showId, int total)
    {
        public string ShowId { get; } = showId;

        public int Total { get; } = total;

        public Dictionary<int, Task<IReadOnlyList<YoukuDanmakuEpisode>>> Pages { get; } = [];
    }

    private readonly IDanmakuHttp _http;
    private readonly Func<long> _now;
    private readonly DanmakuSeriesCache<Series?> _seriesCache = new();
    private readonly object _tokenSync = new();
    // 接口只把 guid 当客户端标识回传,不校验来源,本地生成一个即可
    private readonly string _guid = RandomGuid();
    private Task<YoukuDanmakuToken>? _token;

    /// <param name="now">当前时间(毫秒),测试用。</param>
    public YoukuDanmakuProvider(IDanmakuHttp http, Func<long>? now = null)
    {
        _http = http;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public string Name => "youku";

    public static IReadOnlyList<YoukuDanmakuCandidate> ParseSearch(JsonElement data)
    {
        var result = new List<YoukuDanmakuCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in data.Prop("pageComponentList").Items())
        {
            var common = component.Prop("commonData");
            var showIdValue = common.Prop("showId");
            var rawTitle = common.Prop("titleDTO").Prop("displayName");
            var showId = showIdValue.Text();
            if (!showIdValue.Truthy() || !rawTitle.Truthy() || seen.Contains(showId))
            {
                continue;
            }

            // 只留优酷自己有正片的条目
            if (!common.Prop("isYouku").Is(1) && !common.Prop("hasYouku").Is(1))
            {
                continue;
            }

            var displayName = rawTitle.Text();
            if (SkipKeywords.Any(keyword => displayName.Contains(keyword, StringComparison.Ordinal)))
            {
                continue;
            }

            var title = DanmakuJs.Trim(Labels.Replace(Tags.Replace(displayName, ""), ""));
            if (title.Length == 0)
            {
                continue;
            }

            var feature = common.Prop("feature").Truthy() ? common.Prop("feature").Text() : "";
            var cats = common.Prop("cats").Truthy() ? common.Prop("cats").Text() : "";
            var kind = $"{cats} {feature}".ToLowerInvariant();
            var isMovie = kind.Contains("电影", StringComparison.Ordinal) || kind.Contains("movie", StringComparison.Ordinal);
            var year = YearPattern.Match(feature);
            seen.Add(showId);
            result.Add(new YoukuDanmakuCandidate(
                showId,
                title,
                year.Success ? year.Value : null,
                isMovie ? 1 : (int)common.Prop("episodeTotal").NumberOrZero(),
                isMovie));
        }

        return result;
    }

    public static YoukuDanmakuEpisodePage ParseEpisodes(JsonElement data)
    {
        var episodes = new List<YoukuDanmakuEpisode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var video in data.Prop("videos").Items())
        {
            var idValue = video.Prop("id");
            var vid = idValue.Truthy() ? idValue.Text() : "";
            if (vid.Length == 0 || !seen.Add(vid))
            {
                continue;
            }

            var duration = video.Prop("duration").Number();
            var seq = video.Prop("seq") is { ValueKind: not JsonValueKind.Null } ? video.Prop("seq") : video.Prop("stage");
            episodes.Add(new YoukuDanmakuEpisode(
                vid,
                seq is null ? null : DanmakuTitle.ParseEpisodeNumber(seq.Text()),
                video.Prop("title").Truthy() ? video.Prop("title").Text()
                : video.Prop("rc_title").Truthy() ? video.Prop("rc_title").Text() : "",
                double.IsFinite(duration) && duration > 0 ? duration : 0));
        }

        // 正片和花絮混在一起时只留正片
        var main = episodes.Where(episode => !DanmakuTitle.IsExtraEpisodeTitle(episode.Title)).ToList();
        var total = (int)data.Prop("total").NumberOrZero();
        return new YoukuDanmakuEpisodePage(total != 0 ? total : episodes.Count, main.Count > 0 ? main : episodes);
    }

    /// <summary>从 Set-Cookie 文本里取 mtop 令牌;缺任何一个都算没拿到。</summary>
    public static YoukuDanmakuToken? ParseToken(string? setCookie)
    {
        var text = setCookie ?? "";
        var tk = TkPattern.Match(text);
        var enc = EncPattern.Match(text);
        return tk.Success && enc.Success ? new YoukuDanmakuToken(tk.Groups[1].Value, enc.Groups[1].Value) : null;
    }

    /// <summary>生成一个分片的请求:带签名的地址和表单正文。</summary>
    public static YoukuDanmakuSegmentRequest BuildSegmentRequest(string vid, int mat, string guid, string tk, long nowMs)
    {
        var now = nowMs.ToString(CultureInfo.InvariantCulture);
        // 字段顺序与手机版 JSON.stringify 的结果一致(签名按这串文本算)
        var inner =
            $"{{\"ctime\":{now},\"ctype\":10004,\"cver\":\"v1.0\",\"guid\":{Quote(guid)},\"mat\":{mat.ToString(CultureInfo.InvariantCulture)}," +
            $"\"mcount\":1,\"pid\":0,\"sver\":\"3.1.0\",\"type\":1,\"vid\":{Quote(vid)}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner + "}"));
        var data = $"{inner},\"msg\":\"{encoded}\",\"sign\":\"{Md5(encoded + MsgSalt)}\"}}";
        var sign = Md5($"{(tk.Length > 32 ? tk[..32] : tk)}&{now}&{AppKey}&{data}");
        var url =
            $"{DanmakuUrl}?jsv=2.5.6&appKey={AppKey}&t={now}&sign={sign}" +
            "&api=mopen.youku.danmu.list&v=1.0&type=originaljson&dataType=jsonp&timeout=20000&jsonpIncPrefix=utility";
        return new YoukuDanmakuSegmentRequest(url, "data=" + DanmakuJs.EncodeUriComponent(data));
    }

    /// <summary>接口应答说令牌缺失/过期(需要换令牌重试)。</summary>
    public static bool IsTokenError(JsonElement data) =>
        data.Prop("ret").Items().Any(item => ((JsonElement?)item).Text().Contains("TOKEN", StringComparison.Ordinal));

    public static IReadOnlyList<DanmakuComment> ParseSegment(JsonElement data)
    {
        var raw = data.Prop("data").Prop("result");
        if (!raw.Truthy())
        {
            return [];
        }

        JsonDocument? document = null;
        try
        {
            JsonElement? inner;
            if (raw is { ValueKind: JsonValueKind.String })
            {
                try
                {
                    document = JsonDocument.Parse(raw.Text());
                }
                catch (JsonException)
                {
                    return [];
                }

                inner = document.RootElement;
            }
            else
            {
                inner = raw;
            }

            if (inner is not { ValueKind: JsonValueKind.Object } || inner.Prop("code").Text() == "-1")
            {
                return [];
            }

            var result = new List<DanmakuComment>();
            foreach (var item in inner.Prop("data").Prop("result").Items())
            {
                var text = DanmakuJs.Trim(item.Prop("content").Text());
                var time = item.Prop("playat").Number() / 1000;
                if (text.Length == 0 || !double.IsFinite(time) || time < 0)
                {
                    continue;
                }

                var color = 0xffffff;
                var mode = DanmakuMode.Scroll;
                ReadStyle(item.Prop("propertis"), ref color, ref mode);
                result.Add(new DanmakuComment(time, text, color, mode));
            }

            // 稳定排序,与手机版 Array.sort 一致
            return result.OrderBy(comment => comment.Time).ToList();
        }
        finally
        {
            document?.Dispose();
        }
    }

    public async Task<DanmakuTrack?> ResolveAsync(DanmakuQuery query, CancellationToken ct = default)
    {
        var series = await _seriesCache
            .GetOrAdd(DanmakuProviderShared.SeriesKey(query), () => FindSeriesAsync(query, ct))
            .ConfigureAwait(false);
        if (series is null)
        {
            return null;
        }

        var episode = await FindEpisodeAsync(series, query, ct).ConfigureAwait(false);
        if (episode is null || episode.Duration <= 0)
        {
            return null;
        }

        // 先拿到令牌:拿不到就按"访问失败"报出去,而不是匹配成功后一条弹幕都没有
        await GetTokenAsync(ct).ConfigureAwait(false);

        var segmentCount = (int)Math.Floor(episode.Duration / SegmentSeconds) + 1;
        var vid = episode.Vid;
        return new DanmakuTrack(
            "youku",
            episode.Title,
            SegmentSeconds,
            async (index, token) =>
                index < 0 || index >= segmentCount ? [] : await LoadSegmentAsync(vid, index, token).ConfigureAwait(false));
    }

    private static void ReadStyle(JsonElement? propertis, ref int color, ref DanmakuMode mode)
    {
        JsonDocument? document = null;
        try
        {
            JsonElement? prop = propertis;
            if (propertis is { ValueKind: JsonValueKind.String })
            {
                document = JsonDocument.Parse(propertis.Text());
                prop = document.RootElement;
            }

            var colorValue = prop.Prop("color");
            var parsed = colorValue is { ValueKind: JsonValueKind.String }
                ? DanmakuJs.ParseInt(colorValue.Text()) ?? double.NaN
                : colorValue.Number();
            if (double.IsFinite(parsed) && parsed > 0)
            {
                color = (int)((long)parsed & 0xffffff);
            }

            if (prop.Prop("pos").Is(1))
            {
                mode = DanmakuMode.Top;
            }
            else if (prop.Prop("pos").Is(2))
            {
                mode = DanmakuMode.Bottom;
            }
        }
        catch (JsonException)
        {
            // 样式解析失败用默认白色滚动
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Md5(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string RandomGuid()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var builder = new StringBuilder(24);
        for (var i = 0; i < 24; i++)
        {
            builder.Append(chars[Random.Shared.Next(chars.Length)]);
        }

        return builder.ToString();
    }

    private async Task<IReadOnlyList<YoukuDanmakuCandidate>> SearchAsync(string keyword, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = DanmakuHttp.UserAgent,
            ["Accept"] = "application/json",
            ["Referer"] = "https://www.youku.com/",
        };
        using var document = await _http
            .GetJsonAsync(
                $"{SearchUrl}?keyword={DanmakuJs.EncodeUriComponent(keyword)}&userAgent={DanmakuJs.EncodeUriComponent(DanmakuHttp.UserAgent)}&site=1&categories=0&ftype=0&ob=0&pg=1",
                headers,
                DanmakuHttp.DefaultTimeout,
                ct)
            .ConfigureAwait(false);
        return ParseSearch(document.RootElement);
    }

    private async Task<YoukuDanmakuEpisodePage> FetchPageAsync(string showId, int page, CancellationToken ct)
    {
        using var document = await _http
            .GetJsonAsync(
                $"{EpisodesUrl}?client_id={ClientId}&package=com.huawei.hwvplayer.youku&ext=show&show_id={DanmakuJs.EncodeUriComponent(showId)}&page={page.ToString(CultureInfo.InvariantCulture)}&count={PageSize.ToString(CultureInfo.InvariantCulture)}",
                DanmakuProviderShared.UserAgentOnly,
                DanmakuHttp.DefaultTimeout,
                ct)
            .ConfigureAwait(false);
        return ParseEpisodes(document.RootElement);
    }

    private async Task<Series?> FindSeriesAsync(DanmakuQuery query, CancellationToken ct)
    {
        var parsed = DanmakuTitle.ParseTitle(query.Title);
        foreach (var keyword in DanmakuProviderShared.SearchKeywords(parsed))
        {
            if (keyword.Length == 0)
            {
                continue;
            }

            var candidates = await SearchAsync(keyword, ct).ConfigureAwait(false);
            var best = DanmakuTitle.PickBestCandidate(parsed, candidates, query.Year, query.EpisodeCount, query.EpisodeNumber);
            if (best < 0)
            {
                continue;
            }

            var showId = candidates[best].ShowId;
            var first = await FetchPageAsync(showId, 1, ct).ConfigureAwait(false);
            var series = new Series(showId, first.Total);
            series.Pages[1] = Task.FromResult(first.Episodes);
            return series;
        }

        return null;
    }

    private Task<IReadOnlyList<YoukuDanmakuEpisode>> LoadPageAsync(Series series, int page, CancellationToken ct)
    {
        lock (series.Pages)
        {
            if (!series.Pages.TryGetValue(page, out var pending))
            {
                pending = LoadPageCoreAsync(series, page, ct);
                series.Pages[page] = pending;
            }

            return pending;
        }
    }

    private async Task<IReadOnlyList<YoukuDanmakuEpisode>> LoadPageCoreAsync(Series series, int page, CancellationToken ct)
    {
        // 先让出,保证调用方已经把这个任务放进 series.Pages(失败时才能把它摘掉)
        await Task.Yield();
        try
        {
            return (await FetchPageAsync(series.ShowId, page, ct).ConfigureAwait(false)).Episodes;
        }
        catch
        {
            lock (series.Pages)
            {
                series.Pages.Remove(page);
            }

            throw;
        }
    }

    private async Task<YoukuDanmakuEpisode?> FindEpisodeAsync(Series series, DanmakuQuery query, CancellationToken ct)
    {
        // 超过一页的长剧:只取当前集所在的那一页
        var page = series.Total > PageSize ? (query.EpisodeNumber - 1) / PageSize + 1 : 1;
        var episodes = await LoadPageAsync(series, page, ct).ConfigureAwait(false);
        var index = DanmakuTitle.PickEpisodeIndex(
            episodes.Select(episode => episode.Number).ToList(),
            page == 1 ? query.EpisodeIndex : query.EpisodeNumber - 1 - (page - 1) * PageSize,
            query.EpisodeNumber,
            query.EpisodeCount);
        return index >= 0 ? episodes[index] : null;
    }

    private Task<DanmakuRawResponse> SendRawAsync(
        string method,
        string url,
        string? body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct) =>
        _http is IDanmakuRawHttp raw
            ? raw.SendRawAsync(method, url, body, headers, DanmakuHttp.DefaultTimeout, ct)
            : Task.FromException<DanmakuRawResponse>(new HttpRequestException("youku: raw http unavailable"));

    private Task<YoukuDanmakuToken> GetTokenAsync(CancellationToken ct)
    {
        lock (_tokenSync)
        {
            return _token ??= FetchTokenAsync(ct);
        }
    }

    private async Task<YoukuDanmakuToken> FetchTokenAsync(CancellationToken ct)
    {
        // 先让出,保证调用方已经把这个任务记进 _token(失败时才能把它摘掉)
        await Task.Yield();
        try
        {
            var response = await SendRawAsync("GET", TokenUrl, null, DanmakuProviderShared.UserAgentOnly, ct).ConfigureAwait(false);
            return ParseToken(response.SetCookie) ?? throw new HttpRequestException("youku: token unavailable");
        }
        catch
        {
            lock (_tokenSync)
            {
                _token = null;
            }

            throw;
        }
    }

    private async Task<(JsonDocument Data, string SetCookie)> RequestSegmentAsync(
        string vid,
        int mat,
        YoukuDanmakuToken token,
        CancellationToken ct)
    {
        var request = BuildSegmentRequest(vid, mat, _guid, token.Tk, _now());
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = DanmakuHttp.UserAgent,
            ["Cookie"] = $"_m_h5_tk={token.Tk};_m_h5_tk_enc={token.Enc};",
            ["Referer"] = "https://v.youku.com",
            ["Content-Type"] = "application/x-www-form-urlencoded",
        };
        var response = await SendRawAsync("POST", request.Url, request.Body, headers, ct).ConfigureAwait(false);
        try
        {
            return (JsonDocument.Parse(response.Text), response.SetCookie);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("youku: bad response", exception);
        }
    }

    private async Task<IReadOnlyList<DanmakuComment>> LoadSegmentAsync(string vid, int mat, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        var (data, setCookie) = await RequestSegmentAsync(vid, mat, token, ct).ConfigureAwait(false);
        try
        {
            if (IsTokenError(data.RootElement))
            {
                // 令牌过期:应答会带新令牌,换上重试一次
                var renewed = ParseToken(setCookie);
                lock (_tokenSync)
                {
                    _token = renewed is null ? null : Task.FromResult(renewed);
                }

                token = await GetTokenAsync(ct).ConfigureAwait(false);
                data.Dispose();
                (data, _) = await RequestSegmentAsync(vid, mat, token, ct).ConfigureAwait(false);
                if (IsTokenError(data.RootElement))
                {
                    lock (_tokenSync)
                    {
                        _token = null;
                    }

                    throw new HttpRequestException("youku: token rejected");
                }
            }

            return ParseSegment(data.RootElement);
        }
        finally
        {
            data.Dispose();
        }
    }
}
