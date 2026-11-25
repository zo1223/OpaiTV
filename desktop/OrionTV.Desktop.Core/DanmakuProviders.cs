using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

// 三个弹幕来源:腾讯视频(内置,首选)、哔哩哔哩(内置,备用)、自定义 danmu_api 服务器(可选)。
// 与手机版 services/danmaku/{tencent,bilibili,server}.ts 逐条对应,改规则时两边一起改。

internal static class DanmakuProviderShared
{
    private static readonly Regex Tags = new("<[^>]+>");

    internal static readonly IReadOnlyDictionary<string, string> UserAgentOnly =
        new Dictionary<string, string> { ["User-Agent"] = DanmakuHttp.UserAgent };

    /// <summary>JS 的 String(value ?? "").replace(/&lt;[^&gt;]+&gt;/g, "").trim()。</summary>
    internal static string StripTags(JsonElement? value) => DanmakuJs.Trim(Tags.Replace(value.Text(), ""));

    /// <summary>同一部片的缓存键:电影和剧集分开,避免同名的电影版/剧版串台。</summary>
    internal static string SeriesKey(DanmakuQuery query) =>
        $"{DanmakuTitle.ParseTitle(query.Title).Normalized}|{query.Year ?? ""}|{(query.EpisodeCount <= 1 ? "m" : "s")}";

    /// <summary>搜索关键词:先用完整标题,带季标记时再用去掉季标记的片名搜一次。</summary>
    internal static IReadOnlyList<string> SearchKeywords(DanmakuParsedTitle parsed) =>
        parsed.BaseText.Length > 0 && parsed.BaseText != parsed.Cleaned
            ? [parsed.Cleaned, parsed.BaseText]
            : [parsed.Cleaned];
}

/// <summary>"按片名找到的那部片"的小缓存:最多 20 部,失败(网络错误)不缓存,下次进入播放页重试。</summary>
internal sealed class DanmakuSeriesCache<T>
{
    private const int Capacity = 20;

    private readonly object _sync = new();
    private readonly Dictionary<string, Task<T>> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    internal Task<T> GetOrAdd(string key, Func<Task<T>> factory)
    {
        Task<T> task;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                return existing;
            }

            task = Start(factory);
            _entries[key] = task;
            _order.Add(key);
            if (_order.Count > Capacity)
            {
                _entries.Remove(_order[0]);
                _order.RemoveAt(0);
            }
        }

        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_sync)
                {
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
                    {
                        _entries.Remove(key);
                        _order.Remove(key);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static Task<T> Start(Func<Task<T>> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }
}

// ---------- 腾讯视频 ----------

public sealed record TencentDanmakuCandidate(string Cid, string Title, string? Year, int EpisodeCount, bool? IsMovie)
    : DanmakuTitleCandidate(Title, Year, EpisodeCount, IsMovie);

public sealed record TencentDanmakuEpisode(string Vid, int? Number, string Title);

public sealed record TencentDanmakuTab(double Begin, double End, string PageContext);

public sealed record TencentDanmakuEpisodePage(
    IReadOnlyList<TencentDanmakuTab> Tabs,
    IReadOnlyList<TencentDanmakuEpisode> Episodes);

/// <summary>
/// 内置弹幕来源:腾讯视频公开接口(搜索 → 分集 → 弹幕分片),不需要登录和服务器。
/// 接口与字段参考 huangxd-/danmu_api 的 tencent 源。
/// </summary>
public sealed class TencentDanmakuProvider : IDanmakuProvider
{
    private const string SearchUrl =
        "https://pbaccess.video.qq.com/trpc.videosearch.mobile_search.MultiTerminalSearch/MbSearch?vplatform=2";
    private const string EpisodesUrl =
        "https://pbaccess.video.qq.com/trpc.universal_backend_service.page_server_rpc.PageServer/GetPageData?video_appid=3000010&vversion_name=8.2.96&vversion_platform=2";
    private const string DanmakuBaseUrl = "https://dm.video.qq.com/barrage";
    private const double DefaultSegmentMs = 30000;

    private static readonly string[] AllowedTypes = ["电视剧", "动漫", "电影", "纪录片", "综艺", "综艺节目", "少儿"];

    private sealed class Series(string cid, IReadOnlyList<TencentDanmakuTab> tabs, IReadOnlyList<TencentDanmakuEpisode> firstPage)
    {
        public string Cid { get; } = cid;

        public IReadOnlyList<TencentDanmakuTab> Tabs { get; } = tabs;

        /// <summary>第一次请求直接带回的分集(没有分页时就是全部)。</summary>
        public IReadOnlyList<TencentDanmakuEpisode> FirstPage { get; } = firstPage;

        public Dictionary<string, Task<IReadOnlyList<TencentDanmakuEpisode>>> Pages { get; } = new(StringComparer.Ordinal);
    }

    private readonly IDanmakuHttp _http;
    private readonly DanmakuSeriesCache<Series?> _seriesCache = new();

    public TencentDanmakuProvider(IDanmakuHttp http)
    {
        _http = http;
    }

    public string Name => "tencent";

    public static IReadOnlyList<TencentDanmakuCandidate> ParseSearch(JsonElement data)
    {
        var root = data.Prop("data");
        if (!root.Truthy())
        {
            return [];
        }

        var items = new List<JsonElement>();
        foreach (var box in root.Prop("areaBoxList").Items())
        {
            if (box.Prop("boxId").Is("MainNeed"))
            {
                items.AddRange(box.Prop("itemList").Items());
            }
        }

        items.AddRange(root.Prop("normalList").Prop("itemList").Items());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TencentDanmakuCandidate>();
        foreach (var item in items)
        {
            var info = item.Prop("videoInfo");
            var cidValue = item.Prop("doc").Prop("id");
            var cid = cidValue.Text();
            if (!info.Truthy() || !cidValue.Truthy() || seen.Contains(cid))
            {
                continue;
            }

            // 没有年份的都是短视频、合集
            if (!info.Prop("year").Truthy())
            {
                continue;
            }

            if (info.Prop("subTitle").Is("全网搜") || info.Prop("playFlag").Is(2))
            {
                continue;
            }

            var typeName = info.Prop("typeName").Truthy() ? info.Prop("typeName").Text() : "";
            if (!AllowedTypes.Contains(typeName))
            {
                continue;
            }

            var sites = info.Prop("playSites").Items().Concat(info.Prop("episodeSites").Items()).ToList();
            if (sites.Count > 0 && !sites.Any(site => site.Prop("enName").Is("qq")))
            {
                continue;
            }

            var title = DanmakuProviderShared.StripTags(info.Prop("title"));
            if (title.Length == 0)
            {
                continue;
            }

            seen.Add(cid);
            var isMovie = typeName == "电影";
            result.Add(new TencentDanmakuCandidate(
                cid,
                title,
                info.Prop("year").Text(),
                isMovie ? 1 : (int)info.Prop("subjectDoc").Prop("videoNum").NumberOrZero(),
                isMovie));
        }

        return result;
    }

    public static TencentDanmakuEpisodePage ParseEpisodePage(JsonElement data)
    {
        var tabs = new List<TencentDanmakuTab>();
        var episodes = new List<TencentDanmakuEpisode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in data.Prop("data").Prop("module_list_datas").Items())
        {
            foreach (var module in list.Prop("module_datas").Items())
            {
                var rawTabs = module.Prop("module_params").Prop("tabs");
                if (rawTabs.Truthy() && tabs.Count == 0)
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(rawTabs.Text());
                        JsonElement? tabList = parsed.RootElement;
                        foreach (var tab in tabList.Items())
                        {
                            if (tab.Prop("page_context").Truthy())
                            {
                                tabs.Add(new TencentDanmakuTab(
                                    tab.Prop("begin").NumberOrZeroDouble(),
                                    tab.Prop("end").NumberOrZeroDouble(),
                                    tab.Prop("page_context").Text()));
                            }
                        }
                    }
                    catch
                    {
                        // 分页信息解析失败时按单页处理
                    }
                }

                foreach (var item in module.Prop("item_data_lists").Prop("item_datas").Items())
                {
                    var parameters = item.Prop("item_params");
                    var vidValue = parameters.Prop("vid");
                    var vid = vidValue.Text();
                    if (!vidValue.Truthy() || parameters.Prop("is_trailer").Is("1") || seen.Contains(vid))
                    {
                        continue;
                    }

                    seen.Add(vid);
                    var titleValue = parameters.Prop("title");
                    var title = FirstTruthy(parameters.Prop("play_title"), parameters.Prop("union_title"), titleValue);
                    episodes.Add(new TencentDanmakuEpisode(
                        vid,
                        titleValue is null ? null : DanmakuTitle.ParseEpisodeNumber(titleValue.Text()),
                        title));
                }
            }
        }

        // 正片和花絮混在一起时只留正片
        var main = episodes.Where(episode => !DanmakuTitle.IsExtraEpisodeTitle(episode.Title)).ToList();
        return new TencentDanmakuEpisodePage(tabs, main.Count > 0 ? main : episodes);
    }

    public static IReadOnlyList<DanmakuComment> ParseSegment(JsonElement data)
    {
        var result = new List<DanmakuComment>();
        foreach (var item in data.Prop("barrage_list").Items())
        {
            var text = DanmakuJs.Trim(item.Prop("content").Text());
            var time = item.Prop("time_offset").Number() / 1000;
            if (text.Length == 0 || !double.IsFinite(time) || time < 0)
            {
                continue;
            }

            var color = 0xffffff;
            var mode = DanmakuMode.Scroll;
            var styleValue = item.Prop("content_style");
            if (styleValue.Truthy())
            {
                JsonDocument? parsedStyle = null;
                try
                {
                    JsonElement? style = styleValue;
                    if (styleValue is { ValueKind: JsonValueKind.String })
                    {
                        parsedStyle = JsonDocument.Parse(styleValue.Text());
                        style = parsedStyle.RootElement;
                    }

                    var gradient = style.Prop("gradient_colors").First();
                    var plain = style.Prop("color");
                    var raw = gradient.Truthy()
                        ? gradient.Text()
                        : plain.Truthy() && !plain.Is("ffffff") ? plain.Text() : "";
                    if (raw.Length > 0)
                    {
                        var hashIndex = raw.IndexOf('#');
                        var hex = hashIndex < 0 ? raw : raw.Remove(hashIndex, 1);
                        if (DanmakuJs.ParseInt(hex, 16) is { } parsedColor)
                        {
                            color = DanmakuJs.ColorOf(parsedColor);
                        }
                    }

                    if (style.Prop("position").Is(2))
                    {
                        mode = DanmakuMode.Top;
                    }
                    else if (style.Prop("position").Is(3))
                    {
                        mode = DanmakuMode.Bottom;
                    }
                }
                catch
                {
                    // 样式解析失败用默认白色滚动
                }
                finally
                {
                    parsedStyle?.Dispose();
                }
            }

            result.Add(new DanmakuComment(time, text, color, mode));
        }

        return SpreadWithinSecond(result);
    }

    /// <summary>
    /// 腾讯的弹幕时间只精确到秒,同一秒常有十几二十条。原样显示会每秒整排同时冒出来,
    /// 所以把同一整秒内的弹幕按原顺序均匀摊到这一秒里。时间本来就带小数的不动。
    /// </summary>
    public static IReadOnlyList<DanmakuComment> SpreadWithinSecond(IReadOnlyList<DanmakuComment> comments)
    {
        var counts = new Dictionary<double, int>();
        foreach (var comment in comments)
        {
            if (IsInteger(comment.Time))
            {
                counts[comment.Time] = counts.GetValueOrDefault(comment.Time) + 1;
            }
        }

        var used = new Dictionary<double, int>();
        var result = new List<DanmakuComment>(comments.Count);
        foreach (var comment in comments)
        {
            if (!counts.TryGetValue(comment.Time, out var total) || total < 2)
            {
                result.Add(comment);
                continue;
            }

            var order = used.GetValueOrDefault(comment.Time);
            used[comment.Time] = order + 1;
            result.Add(order == 0 ? comment : comment with { Time = comment.Time + (double)order / total });
        }

        return result;
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
        if (episode is null)
        {
            return null;
        }

        var names = new Dictionary<long, string>();
        double spanMs;
        using (var document = await _http
                   .GetJsonAsync(
                       $"{DanmakuBaseUrl}/base/{episode.Vid}",
                       DanmakuProviderShared.UserAgentOnly,
                       DanmakuHttp.DefaultTimeout,
                       ct)
                   .ConfigureAwait(false))
        {
            JsonElement? root = document.RootElement;
            var span = root.Prop("segment_span").Number();
            spanMs = span > 0 ? span : DefaultSegmentMs;
            var segmentCount = 0;
            if (root.Prop("segment_index") is { ValueKind: JsonValueKind.Object } index)
            {
                foreach (var property in index.EnumerateObject())
                {
                    segmentCount++;
                    JsonElement? segment = property.Value;
                    var name = segment.Prop("segment_name");
                    var slot = DanmakuJs.Round(segment.Prop("segment_start").Number() / spanMs);
                    if (name.Truthy() && double.IsFinite(slot))
                    {
                        names[(long)slot] = name.Text();
                    }
                }
            }

            if (segmentCount == 0)
            {
                return null;
            }
        }

        var vid = episode.Vid;
        return new DanmakuTrack(
            "tencent",
            episode.Title,
            spanMs / 1000,
            async (segmentIndex, token) =>
            {
                if (!names.TryGetValue(segmentIndex, out var name))
                {
                    return [];
                }

                using var document = await _http
                    .GetJsonAsync(
                        $"{DanmakuBaseUrl}/segment/{vid}/{name}",
                        DanmakuProviderShared.UserAgentOnly,
                        DanmakuHttp.DefaultTimeout,
                        token)
                    .ConfigureAwait(false);
                return ParseSegment(document.RootElement);
            });
    }

    private static Dictionary<string, string> JsonHeaders(string referer) => new()
    {
        ["Origin"] = "https://v.qq.com",
        ["Referer"] = referer,
        ["User-Agent"] = DanmakuHttp.UserAgent,
        ["Accept"] = "application/json, text/plain, */*",
    };

    private async Task<IReadOnlyList<TencentDanmakuCandidate>> SearchAsync(string keyword, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            version = "25071701",
            clientType = 1,
            filterValue = "",
            uuid = "0379274D-05A0-4EB6-A89C-878C9A460426",
            query = keyword,
            retry = 0,
            pagenum = 0,
            isPrefetch = true,
            pagesize = 30,
            queryFrom = 0,
            searchDatakey = "",
            transInfo = "",
            isneedQc = true,
            preQid = "",
            adClientInfo = "",
            extraInfo = new { multi_terminal_pc = "1", themeType = "1", sugRelatedIds = "{}", appVersion = "" },
        });
        using var document = await _http
            .PostJsonAsync(SearchUrl, body, JsonHeaders("https://v.qq.com/"), DanmakuHttp.DefaultTimeout, ct)
            .ConfigureAwait(false);
        JsonElement? root = document.RootElement;
        return root.Prop("ret").Is(0) ? ParseSearch(document.RootElement) : [];
    }

    private async Task<TencentDanmakuEpisodePage> FetchEpisodePageAsync(string cid, string pageContext, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            has_cache = 1,
            page_params = new
            {
                req_from = "web_vsite",
                page_id = "vsite_episode_list",
                page_type = "detail_operation",
                id_type = "1",
                page_size = "",
                cid,
                vid = "",
                lid = "",
                page_num = "",
                page_context = pageContext,
                detail_page_type = "1",
            },
        });
        using var document = await _http
            .PostJsonAsync(
                EpisodesUrl,
                body,
                JsonHeaders($"https://v.qq.com/x/cover/{cid}.html"),
                DanmakuHttp.DefaultTimeout,
                ct)
            .ConfigureAwait(false);
        return ParseEpisodePage(document.RootElement);
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

            var cid = candidates[best].Cid;
            var first = await FetchEpisodePageAsync(
                    cid,
                    $"cid={cid}&detail_page_type=1&req_from=web_vsite&req_from_second_type=&req_type=0",
                    ct)
                .ConfigureAwait(false);
            return new Series(cid, first.Tabs, first.Episodes);
        }

        return null;
    }

    private Task<IReadOnlyList<TencentDanmakuEpisode>> LoadTabAsync(Series series, TencentDanmakuTab tab, CancellationToken ct)
    {
        lock (series.Pages)
        {
            if (series.Pages.TryGetValue(tab.PageContext, out var existing))
            {
                return existing;
            }

            var page = LoadTabCoreAsync(series, tab, ct);
            series.Pages[tab.PageContext] = page;
            return page;
        }
    }

    private async Task<IReadOnlyList<TencentDanmakuEpisode>> LoadTabCoreAsync(Series series, TencentDanmakuTab tab, CancellationToken ct)
    {
        // 先让出,保证调用方已经把这个任务放进 series.Pages(失败时才能把它摘掉)
        await Task.Yield();
        try
        {
            return (await FetchEpisodePageAsync(series.Cid, tab.PageContext, ct).ConfigureAwait(false)).Episodes;
        }
        catch
        {
            lock (series.Pages)
            {
                series.Pages.Remove(tab.PageContext);
            }

            throw;
        }
    }

    private async Task<TencentDanmakuEpisode?> FindEpisodeAsync(Series series, DanmakuQuery query, CancellationToken ct)
    {
        TencentDanmakuEpisode? Pick(IReadOnlyList<TencentDanmakuEpisode> episodes, int episodeIndex)
        {
            var index = DanmakuTitle.PickEpisodeIndex(
                episodes.Select(episode => episode.Number).ToList(),
                episodeIndex,
                query.EpisodeNumber,
                query.EpisodeCount);
            return index >= 0 ? episodes[index] : null;
        }

        if (series.Tabs.Count <= 1)
        {
            return Pick(series.FirstPage, query.EpisodeIndex);
        }

        // 分页的长剧:只取当前集所在的那一页
        var tab = series.Tabs.FirstOrDefault(t => query.EpisodeNumber >= t.Begin && query.EpisodeNumber <= t.End);
        if (tab is null)
        {
            return null;
        }

        var page = await LoadTabAsync(series, tab, ct).ConfigureAwait(false);
        return Pick(page, (int)(query.EpisodeNumber - tab.Begin));
    }

    /// <summary>JS 的 String(a || b || c || "")。</summary>
    private static string FirstTruthy(params JsonElement?[] values)
    {
        foreach (var value in values)
        {
            if (value.Truthy())
            {
                return value.Text();
            }
        }

        return "";
    }

    private static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;
}

// ---------- 哔哩哔哩 ----------

public sealed record BilibiliDanmakuCandidate(long SeasonId, string Title, string? Year, int EpisodeCount, bool? IsMovie)
    : DanmakuTitleCandidate(Title, Year, EpisodeCount, IsMovie);

public sealed record BilibiliDanmakuEpisode(long Cid, int? Number, string Title);

/// <summary>
/// 内置备用弹幕来源:哔哩哔哩番剧/影视公开接口(搜索 → 分集 → 弹幕 XML),不需要登录。
/// 腾讯视频匹配不到时才用。搜索接口要 WBI 签名,且对部分网络有风控(返回 v_voucher),此时按未匹配处理。
/// </summary>
public sealed class BilibiliDanmakuProvider : IDanmakuProvider
{
    private const long SessionTtlMs = 60 * 60 * 1000;

    private static readonly int[] WbiMixinTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12,
        38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62,
        11, 36, 20, 34, 44, 52,
    ];

    private static readonly IReadOnlyDictionary<string, string> BaseHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = DanmakuHttp.UserAgent,
        ["Referer"] = "https://www.bilibili.com/",
    };

    private static readonly Regex Tags = new("<[^>]+>");
    private static readonly Regex CommentPattern = new("<d\\s+p=\"([^\"]*)\"[^>]*>([\\s\\S]*?)</d>");
    private static readonly Regex NamedEntity = new("&(amp|lt|gt|quot|apos);");
    private static readonly Regex NumericEntity = new("&#(x?)([0-9a-fA-F]+);");

    private sealed record Session(string Cookie, string MixinKey, long CreatedAt);

    private readonly IDanmakuHttp _http;
    private readonly Func<long> _now;
    private readonly object _sync = new();
    private readonly DanmakuSeriesCache<IReadOnlyList<BilibiliDanmakuEpisode>?> _seriesCache = new();
    private Task<Session>? _session;

    /// <param name="http">HTTP 实现</param>
    /// <param name="now">当前时间(Unix 毫秒);测试里替换</param>
    public BilibiliDanmakuProvider(IDanmakuHttp http, Func<long>? now = null)
    {
        _http = http;
        _now = now ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public string Name => "bilibili";

    /// <summary>由 nav 接口返回的两张图片地址算出 WBI 签名用的 mixin key。</summary>
    public static string BuildWbiMixinKey(string imgUrl, string subUrl)
    {
        static string KeyOf(string url)
        {
            var name = url[(url.LastIndexOf('/') + 1)..];
            var dot = name.IndexOf('.');
            return dot < 0 ? name : name[..dot];
        }

        var raw = KeyOf(imgUrl) + KeyOf(subUrl);
        var builder = new StringBuilder(64);
        foreach (var index in WbiMixinTable)
        {
            if (index < raw.Length)
            {
                builder.Append(raw[index]);
            }
        }

        return builder.Length > 32 ? builder.ToString(0, 32) : builder.ToString();
    }

    /// <summary>WBI 签名:参数按键名排序拼成查询串,末尾接 mixin key 取 MD5 作为 w_rid。</summary>
    public static string SignWbiQuery(IReadOnlyDictionary<string, string> parameters, string mixinKey, long nowSeconds)
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            all[key] = value;
        }

        all["wts"] = nowSeconds.ToString(CultureInfo.InvariantCulture);
        var query = string.Join(
            "&",
            all.Select(pair => $"{DanmakuJs.EncodeUriComponent(pair.Key)}={DanmakuJs.EncodeUriComponent(pair.Value)}"));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey));
        return $"{query}&w_rid={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static IReadOnlyList<BilibiliDanmakuCandidate> ParseSearch(JsonElement data)
    {
        var result = new List<BilibiliDanmakuCandidate>();
        foreach (var item in data.Prop("data").Prop("result").Items())
        {
            var seasonId = item.Prop("season_id").Number();
            var title = DanmakuJs.Trim(Tags.Replace(item.Prop("title").Text(), ""));
            if (!double.IsFinite(seasonId) || seasonId == 0 || title.Length == 0)
            {
                continue;
            }

            var pubtime = item.Prop("pubtime").Number();
            string? year = null;
            if (pubtime > 0 && pubtime < 253402300800)
            {
                year = DateTimeOffset.FromUnixTimeSeconds((long)pubtime).UtcDateTime.Year
                    .ToString(CultureInfo.InvariantCulture);
            }

            result.Add(new BilibiliDanmakuCandidate(
                (long)seasonId,
                title,
                year,
                (int)item.Prop("ep_size").NumberOrZero(),
                item.Prop("season_type_name").Is("电影") || item.Prop("media_type").Is(2)));
        }

        return result;
    }

    public static IReadOnlyList<BilibiliDanmakuEpisode> ParseEpisodes(JsonElement data)
    {
        var episodes = new List<BilibiliDanmakuEpisode>();
        foreach (var item in data.Prop("result").Prop("episodes").Items())
        {
            var cid = item.Prop("cid").Number();
            if (!double.IsFinite(cid) || cid == 0)
            {
                continue;
            }

            var badge = item.Prop("badge").Truthy() ? item.Prop("badge").Text() : "";
            var shortTitle = item.Prop("title");
            var title = DanmakuJs.Trim($"{shortTitle.Text()} {item.Prop("long_title").Text()}");
            if (badge.Contains("预告", StringComparison.Ordinal) || DanmakuTitle.IsExtraEpisodeTitle(title))
            {
                continue;
            }

            episodes.Add(new BilibiliDanmakuEpisode(
                (long)cid,
                shortTitle is null ? null : DanmakuTitle.ParseEpisodeNumber(shortTitle.Text()),
                title));
        }

        return episodes;
    }

    /// <summary>解析 B 站弹幕 XML:&lt;d p="时间,类型,字号,颜色,..."&gt;文字&lt;/d&gt;(类型 1~3 滚动 / 4 底部 / 5 顶部,其余忽略)。</summary>
    public static IReadOnlyList<DanmakuComment> ParseXml(string xml)
    {
        var result = new List<DanmakuComment>();
        foreach (Match match in CommentPattern.Matches(xml ?? ""))
        {
            var parts = match.Groups[1].Value.Split(',');
            var time = DanmakuJs.ParseFloat(parts[0]);
            var type = DanmakuJs.ParseInt(parts.Length > 1 ? parts[1] : null);
            var text = DanmakuJs.Trim(DecodeXmlText(match.Groups[2].Value));
            if (text.Length == 0 || !double.IsFinite(time) || time < 0)
            {
                continue;
            }

            DanmakuMode mode;
            if (type is >= 1 and <= 3)
            {
                mode = DanmakuMode.Scroll;
            }
            else if (type == 4)
            {
                mode = DanmakuMode.Bottom;
            }
            else if (type == 5)
            {
                mode = DanmakuMode.Top;
            }
            else
            {
                continue;
            }

            result.Add(new DanmakuComment(
                time,
                text,
                DanmakuJs.ColorOf(DanmakuJs.ParseInt(parts.Length > 3 ? parts[3] : null)),
                mode));
        }

        return result;
    }

    /// <summary>弹幕 XML 以 deflate 压缩返回:已经是文本时直接用,否则在这里解压。解不开返回空串。</summary>
    public static string DecodeXmlBytes(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        if (bytes.Length > start && bytes[start] == 0x3C)
        {
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        var inflated = DanmakuJs.InflateAny(bytes);
        return inflated is null ? "" : Encoding.UTF8.GetString(inflated);
    }

    public async Task<DanmakuTrack?> ResolveAsync(DanmakuQuery query, CancellationToken ct = default)
    {
        var episodes = await _seriesCache
            .GetOrAdd(DanmakuProviderShared.SeriesKey(query), () => FindEpisodesAsync(query, ct))
            .ConfigureAwait(false);
        if (episodes is null || episodes.Count == 0)
        {
            return null;
        }

        var index = DanmakuTitle.PickEpisodeIndex(
            episodes.Select(episode => episode.Number).ToList(),
            query.EpisodeIndex,
            query.EpisodeNumber,
            query.EpisodeCount);
        if (index < 0)
        {
            return null;
        }

        var episode = episodes[index];
        return new DanmakuTrack(
            "bilibili",
            episode.Title,
            0,
            async (segmentIndex, token) =>
            {
                if (segmentIndex != 0)
                {
                    return [];
                }

                var bytes = await _http
                    .GetBytesAsync(
                        $"https://comment.bilibili.com/{episode.Cid.ToString(CultureInfo.InvariantCulture)}.xml",
                        BaseHeaders,
                        DanmakuHttp.DefaultTimeout,
                        token)
                    .ConfigureAwait(false);
                return ParseXml(DecodeXmlBytes(bytes));
            });
    }

    private static string DecodeXmlText(string text)
    {
        var named = NamedEntity.Replace(text, match => match.Groups[1].Value switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            _ => "'",
        });
        return NumericEntity.Replace(named, match =>
        {
            var value = DanmakuJs.ParseInt(match.Groups[2].Value, match.Groups[1].Value.Length > 0 ? 16 : 10);
            return value is > 0 and <= 0x10FFFF && value is < 0xD800 or > 0xDFFF
                ? char.ConvertFromUtf32((int)value.Value)
                : "";
        });
    }

    private static Dictionary<string, string> WithCookie(string cookie) => new(BaseHeaders) { ["Cookie"] = cookie };

    private async Task<Session> OpenSessionAsync(CancellationToken ct)
    {
        string cookie;
        using (var spi = await _http
                   .GetJsonAsync("https://api.bilibili.com/x/frontend/finger/spi", BaseHeaders, DanmakuHttp.DefaultTimeout, ct)
                   .ConfigureAwait(false))
        {
            JsonElement? root = spi.RootElement;
            var b3 = root.Prop("data").Prop("b_3");
            if (!b3.Truthy())
            {
                throw new InvalidOperationException("bilibili: no buvid");
            }

            var b4 = root.Prop("data").Prop("b_4");
            cookie = $"buvid3={b3.Text()}; buvid4={(b4.Truthy() ? b4.Text() : "")}";
        }

        // 未登录时 code 为 -101,但 wbi_img 仍然会给
        using var nav = await _http
            .GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", WithCookie(cookie), DanmakuHttp.DefaultTimeout, ct)
            .ConfigureAwait(false);
        JsonElement? navRoot = nav.RootElement;
        var image = navRoot.Prop("data").Prop("wbi_img");
        if (!image.Prop("img_url").Truthy() || !image.Prop("sub_url").Truthy())
        {
            throw new InvalidOperationException("bilibili: no wbi key");
        }

        return new Session(
            cookie,
            BuildWbiMixinKey(image.Prop("img_url").Text(), image.Prop("sub_url").Text()),
            _now());
    }

    private async Task<Session> GetSessionAsync(CancellationToken ct)
    {
        Task<Session>? existing;
        lock (_sync)
        {
            existing = _session;
        }

        if (existing is not null)
        {
            try
            {
                var current = await existing.ConfigureAwait(false);
                if (_now() - current.CreatedAt < SessionTtlMs)
                {
                    return current;
                }
            }
            catch
            {
                // 上次建立失败,重建
            }
        }

        Task<Session> opened;
        lock (_sync)
        {
            if (_session is not null && !ReferenceEquals(_session, existing))
            {
                // 别的调用已经重建了
                opened = _session;
            }
            else
            {
                opened = OpenSessionAsync(ct);
                _session = opened;
            }
        }

        return await opened.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BilibiliDanmakuCandidate>> SearchAsync(string keyword, CancellationToken ct)
    {
        var session = await GetSessionAsync(ct).ConfigureAwait(false);
        var pages = await Task.WhenAll(new[] { "media_bangumi", "media_ft" }.Select(async searchType =>
        {
            try
            {
                var query = SignWbiQuery(
                    new Dictionary<string, string>
                    {
                        ["search_type"] = searchType,
                        ["keyword"] = keyword,
                        ["page"] = "1",
                    },
                    session.MixinKey,
                    (long)Math.Floor(_now() / 1000d));
                using var document = await _http
                    .GetJsonAsync(
                        $"https://api.bilibili.com/x/web-interface/wbi/search/type?{query}",
                        WithCookie(session.Cookie),
                        DanmakuHttp.DefaultTimeout,
                        ct)
                    .ConfigureAwait(false);
                JsonElement? root = document.RootElement;
                return root.Prop("code").Is(0) ? ParseSearch(document.RootElement) : [];
            }
            catch
            {
                return (IReadOnlyList<BilibiliDanmakuCandidate>)[];
            }
        })).ConfigureAwait(false);
        return pages.SelectMany(page => page).ToList();
    }

    private async Task<IReadOnlyList<BilibiliDanmakuEpisode>?> FindEpisodesAsync(DanmakuQuery query, CancellationToken ct)
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

            using var document = await _http
                .GetJsonAsync(
                    $"https://api.bilibili.com/pgc/view/web/season?season_id={candidates[best].SeasonId.ToString(CultureInfo.InvariantCulture)}",
                    BaseHeaders,
                    DanmakuHttp.DefaultTimeout,
                    ct)
                .ConfigureAwait(false);
            JsonElement? root = document.RootElement;
            return root.Prop("code").Is(0) ? ParseEpisodes(document.RootElement) : null;
        }

        return null;
    }
}

// ---------- 自定义 danmu_api 服务器 ----------

/// <summary>
/// 自定义弹幕服务器(huangxd-/danmu_api,兼容弹弹 play 接口):
/// POST /api/v2/match 按"片名 S01E05"自动匹配 → GET /api/v2/comment/{episodeId}?format=json 取整集弹幕。
/// </summary>
public sealed class ServerDanmakuProvider : IDanmakuProvider
{
    /// <summary>服务器要现去各平台聚合弹幕,首次请求较慢。</summary>
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(30);

    private readonly IDanmakuHttp _http;
    private readonly string _baseUrl;

    public ServerDanmakuProvider(IDanmakuHttp http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public string Name => "server";

    /// <summary>解析弹弹 play 格式的弹幕:p = "时间,类型,颜色,..."(类型 1 滚动 / 4 底部 / 5 顶部),m = 文字。</summary>
    public static IReadOnlyList<DanmakuComment> ParseComments(JsonElement data)
    {
        var result = new List<DanmakuComment>();
        foreach (var item in data.Prop("comments").Items())
        {
            var text = DanmakuJs.Trim(item.Prop("m").Text());
            var parts = item.Prop("p").Text().Split(',');
            var time = DanmakuJs.ParseFloat(parts[0]);
            if (text.Length == 0 || !double.IsFinite(time) || time < 0)
            {
                continue;
            }

            var type = DanmakuJs.ParseInt(parts.Length > 1 ? parts[1] : null);
            var mode = type == 4 ? DanmakuMode.Bottom : type == 5 ? DanmakuMode.Top : DanmakuMode.Scroll;
            result.Add(new DanmakuComment(
                time,
                text,
                DanmakuJs.ColorOf(DanmakuJs.ParseInt(parts.Length > 2 ? parts[2] : null)),
                mode));
        }

        return result;
    }

    private static readonly JsonSerializerOptions MatchBodyOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<DanmakuTrack?> ResolveAsync(DanmakuQuery query, CancellationToken ct = default)
    {
        string episodeId;
        string title;
        // 中文片名原样发送（不转成 \uXXXX），和手机版发出的请求体一致
        var body = JsonSerializer.Serialize(
            new
            {
                fileName = DanmakuTitle.BuildMatchFileName(query.Title, query.EpisodeNumber, query.EpisodeCount),
            },
            MatchBodyOptions);
        using (var match = await _http
                   .PostJsonAsync($"{_baseUrl}/api/v2/match", body, null, ServerTimeout, ct)
                   .ConfigureAwait(false))
        {
            JsonElement? root = match.RootElement;
            var first = root.Prop("matches").First();
            var id = first.Prop("episodeId");
            if (id is null || id.Value.ValueKind is JsonValueKind.Null || id.Is(""))
            {
                return null;
            }

            episodeId = id.Text();
            title = DanmakuJs.Trim(
                $"{(first.Prop("animeTitle").Truthy() ? first.Prop("animeTitle").Text() : "")} {(first.Prop("episodeTitle").Truthy() ? first.Prop("episodeTitle").Text() : "")}");
        }

        // 整集弹幕一次取回:服务器匹配到了却没有弹幕时按未匹配处理,好让内置来源接手
        IReadOnlyList<DanmakuComment> comments;
        using (var data = await _http
                   .GetJsonAsync(
                       $"{_baseUrl}/api/v2/comment/{DanmakuJs.EncodeUriComponent(episodeId)}?format=json",
                       null,
                       ServerTimeout,
                       ct)
                   .ConfigureAwait(false))
        {
            comments = ParseComments(data.RootElement);
        }

        if (comments.Count == 0)
        {
            return null;
        }

        return new DanmakuTrack(
            "server",
            title,
            0,
            (segmentIndex, _) => Task.FromResult(segmentIndex == 0 ? comments : (IReadOnlyList<DanmakuComment>)[]));
    }
}
