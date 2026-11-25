using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

// 芒果TV 弹幕来源。与手机版 services/danmaku/mango.ts 逐条对应,改规则时两边一起改。

public sealed record MangoDanmakuCandidate(string ClipId, string Title, string? Year, int EpisodeCount, bool? IsMovie)
    : DanmakuTitleCandidate(Title, Year, EpisodeCount, IsMovie);

/// <param name="Vid">视频编号</param>
/// <param name="Number">集号;认不出为 null</param>
/// <param name="Title">分集标题</param>
/// <param name="Seconds">时长(秒);未知为 0</param>
/// <param name="Ts">上线时间(排序用)</param>
/// <param name="Intact">是否正片</param>
public sealed record MangoDanmakuEpisode(string Vid, int? Number, string Title, double Seconds, string Ts, bool Intact);

/// <param name="Months">其余月份(不含本页)</param>
/// <param name="Episodes">本页的正片分集</param>
public sealed record MangoDanmakuEpisodePage(IReadOnlyList<string> Months, IReadOnlyList<MangoDanmakuEpisode> Episodes);

/// <summary>
/// 内置弹幕来源:芒果TV 公开接口(搜索 → 分集(按月分页) → 弹幕分片),不需要登录和服务器。
/// 接口与字段参考 huangxd-/danmu_api 的 mango 源。
/// </summary>
public sealed class MangoDanmakuProvider : IDanmakuProvider
{
    private const string SearchUrl = "https://mobileso.bz.mgtv.com/aphone/search/rebirth/v2";
    private const string EpisodesUrl = "https://pcweb.api.mgtv.com/variety/showlist";
    private const string ControlUrl = "https://galaxy.bz.mgtv.com/getctlbarrage";
    private const string FallbackSegmentUrl = "https://galaxy.bz.mgtv.com/rdbarrage";
    private const string SearchUserAgent = "Dalvik/2.1.0 (Linux; U; Android 16; 23127PN0CC Build/AP1A.240505.003)";
    private const int SegmentSeconds = 60;

    /// <summary>分集按月分页;综艺可能跨很多个月,最多取这么多页。</summary>
    private const int MaxMonthPages = 36;

    /// <summary>弹幕分片的 CDN 域名由接口下发,只接受芒果自己的域名。</summary>
    private static readonly Regex CdnHost = new(
        @"^[a-z0-9-]+(\.[a-z0-9-]+)*\.(hitv|mgtv)\.com\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CdnVersion = new(@"^[A-Za-z0-9_\-/.]+\z");
    private static readonly Regex MangoExtras = new(
        "抢先(看|版)|加更|企划|专访|直播|未播|衍生|番外|合伙人手记|会员(专享|加长)|精华|解读|reaction|超前|陪看|影评|广告|推广",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex YearPattern = new("[12][890][0-9][0-9]");

    private static readonly IReadOnlyDictionary<string, string> WebHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = DanmakuHttp.UserAgent,
        ["Referer"] = "https://www.mgtv.com/",
    };

    private static readonly IReadOnlyDictionary<string, string> SearchHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = SearchUserAgent,
        ["Accept"] = "application/json",
    };

    private sealed record Series(string ClipId, IReadOnlyList<MangoDanmakuEpisode> Episodes);

    private readonly IDanmakuHttp _http;
    private readonly DanmakuSeriesCache<Series?> _seriesCache = new();
    private readonly string _deviceId = RandomHex();

    public MangoDanmakuProvider(IDanmakuHttp http)
    {
        _http = http;
    }

    public string Name => "mango";

    /// <summary>"46:36"、"1:02:03" → 秒;认不出为 0。</summary>
    public static double ParseDuration(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length == 0 || parts.Length > 3)
        {
            return 0;
        }

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value)
                || value < 0)
            {
                return 0;
            }

            total = (total * 60) + value;
        }

        return total;
    }

    public static IReadOnlyList<MangoDanmakuCandidate> ParseSearch(JsonElement data)
    {
        var result = new List<MangoDanmakuCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in data.Prop("data").Prop("contents").Items())
        {
            if (!content.Prop("type").Is("mediaRebirthV2"))
            {
                continue;
            }

            foreach (var item in content.Prop("data").Items())
            {
                // 只要芒果自有内容(source 为空或 imgo)
                var source = item.Prop("source");
                if (source.Truthy() && !source.Is("imgo"))
                {
                    continue;
                }

                if (!item.Prop("clipId").Truthy())
                {
                    continue;
                }

                var clipId = item.Prop("clipId").Text();
                var title = DanmakuProviderShared.StripTags(item.Prop("title"));
                if (title.Length == 0 || !seen.Add(clipId))
                {
                    continue;
                }

                var descValue = item.Prop("desc");
                var first = descValue is { ValueKind: JsonValueKind.Array } ? descValue.First() : descValue;
                var desc = first.Truthy() ? first.Text() : "";
                var year = YearPattern.Match(desc);
                var isMovie = desc.Split('/')[0].Contains("电影", StringComparison.Ordinal);
                result.Add(new MangoDanmakuCandidate(clipId, title, year.Success ? year.Value : null, isMovie ? 1 : 0, isMovie));
            }
        }

        return result;
    }

    public static MangoDanmakuEpisodePage ParseEpisodePage(JsonElement data, string clipId)
    {
        var root = data.Prop("data");
        var months = new List<string>();
        var tabIndex = 0;
        foreach (var tab in root.Prop("tab_m").Items())
        {
            if (tabIndex++ > 0 && tab.Prop("m").Truthy())
            {
                months.Add(tab.Prop("m").Text());
            }
        }

        var episodes = new List<MangoDanmakuEpisode>();
        foreach (var item in root.Prop("list").Items())
        {
            if (!item.Prop("video_id").Truthy() || item.Prop("src_clip_id").Text() != clipId)
            {
                continue;
            }

            // 预告
            if (item.Prop("isnew").Text() == "2")
            {
                continue;
            }

            var t1 = item.Prop("t1").Truthy() ? item.Prop("t1").Text() : "";
            var t2 = item.Prop("t2").Truthy() ? item.Prop("t2").Text() : "";
            var label = DanmakuJs.Trim($"{t2} {t1}");
            if (DanmakuTitle.IsExtraEpisodeTitle(label) || MangoExtras.IsMatch(label))
            {
                continue;
            }

            var t3 = item.Prop("t3");
            episodes.Add(new MangoDanmakuEpisode(
                item.Prop("video_id").Text(),
                item.Prop("t2") is null ? null : DanmakuTitle.ParseEpisodeNumber(item.Prop("t2").Text()),
                t3.Truthy() ? t3.Text() : label,
                ParseDuration(item.Prop("time").Text()),
                item.Prop("ts").Truthy() ? item.Prop("ts").Text() : "",
                item.Prop("isIntact").Text() == "1"));
        }

        return new MangoDanmakuEpisodePage(months, episodes);
    }

    /// <summary>各月的分集合并、去重、排序:都带集号按集号,否则按上线时间。</summary>
    public static IReadOnlyList<MangoDanmakuEpisode> OrderEpisodes(IEnumerable<MangoDanmakuEpisode> episodes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = episodes.Where(episode => seen.Add(episode.Vid)).ToList();
        // OrderBy 是稳定排序,同键保持原顺序
        return unique.All(episode => episode.Number is not null)
            ? unique.OrderBy(episode => episode.Number!.Value).ToList()
            : unique.OrderBy(episode => episode.Ts, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<DanmakuComment> ParseSegment(JsonElement data)
    {
        var result = new List<DanmakuComment>();
        foreach (var item in data.Prop("data").Prop("items").Items())
        {
            var text = DanmakuJs.Trim(item.Prop("content").Text());
            var timeValue = item.Prop("time");
            if (timeValue is null || timeValue.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var time = timeValue.Number() / 1000;
            if (text.Length == 0 || !double.IsFinite(time) || time < 0)
            {
                continue;
            }

            var color = 0xffffff;
            var v2Color = item.Prop("v2_color");
            if (v2Color.Truthy())
            {
                var left = ParseRgb(v2Color.Prop("color_left"));
                var right = ParseRgb(v2Color.Prop("color_right"));
                if (left >= 0 && right >= 0)
                {
                    color = (left + right) / 2;
                }
                else if (left >= 0)
                {
                    color = left;
                }
                else if (right >= 0)
                {
                    color = right;
                }
            }

            var mode = DanmakuMode.Scroll;
            var position = item.Prop("v2_position");
            if (position is { ValueKind: JsonValueKind.Number })
            {
                if (position.Is(1))
                {
                    mode = DanmakuMode.Top;
                }
                else if (position.Is(2))
                {
                    mode = DanmakuMode.Bottom;
                }
            }

            result.Add(new DanmakuComment(time, text, color, mode));
        }

        return result.OrderBy(comment => comment.Time).ToList();
    }

    /// <summary>弹幕控制接口 → CDN 分片地址前缀;没有或域名不对时返回 null(改用备用接口)。</summary>
    public static string? ParseControl(JsonElement data)
    {
        var root = data.Prop("data");
        var list = root.Prop("cdn_list");
        var versionValue = root.Prop("cdn_version");
        var host = DanmakuJs.Trim((list.Truthy() ? list.Text() : "").Split(',')[0]);
        var version = DanmakuJs.Trim(versionValue.Truthy() ? versionValue.Text() : "");
        if (host.Length == 0
            || version.Length == 0
            || !CdnHost.IsMatch(host)
            || !CdnVersion.IsMatch(version)
            || version.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return $"https://{host}/{version.Trim('/')}";
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

        var index = DanmakuTitle.PickEpisodeIndex(
            series.Episodes.Select(item => item.Number).ToList(),
            query.EpisodeIndex,
            query.EpisodeNumber,
            query.EpisodeCount);
        if (index < 0)
        {
            return null;
        }

        var episode = series.Episodes[index];
        var vid = Uri.EscapeDataString(episode.Vid);
        var cid = Uri.EscapeDataString(series.ClipId);

        string? cdnBase;
        using (var document = await _http
                   .GetJsonAsync(
                       $"{ControlUrl}?version=8.1.39&abroad=0&uuid=&os=10.15.7&platform=0&mac=&vid={vid}&pid=&cid={cid}&ticket=",
                       WebHeaders,
                       DanmakuHttp.DefaultTimeout,
                       ct)
                   .ConfigureAwait(false))
        {
            cdnBase = ParseControl(document.RootElement);
        }

        var segmentCount = episode.Seconds > 0 ? Math.Ceiling(episode.Seconds / SegmentSeconds) : double.PositiveInfinity;
        return new DanmakuTrack(
            "mango",
            episode.Title,
            SegmentSeconds,
            async (segment, token) =>
            {
                if (segment < 0 || segment >= segmentCount)
                {
                    return [];
                }

                var url = cdnBase is not null
                    ? $"{cdnBase}/{segment.ToString(CultureInfo.InvariantCulture)}.json"
                    : $"{FallbackSegmentUrl}?vid={vid}&cid={cid}&time={((long)segment * SegmentSeconds * 1000).ToString(CultureInfo.InvariantCulture)}";
                using var document = await _http
                    .GetJsonAsync(url, DanmakuProviderShared.UserAgentOnly, DanmakuHttp.DefaultTimeout, token)
                    .ConfigureAwait(false);
                return ParseSegment(document.RootElement);
            });
    }

    private static int ParseRgb(JsonElement? color)
    {
        static int Channel(JsonElement? value)
        {
            if (value is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDouble(out var parsed))
            {
                return -1;
            }

            return parsed >= 0 && parsed <= 255 ? (int)parsed : -1;
        }

        var r = Channel(color.Prop("r"));
        var g = Channel(color.Prop("g"));
        var b = Channel(color.Prop("b"));
        return r < 0 || g < 0 || b < 0 ? -1 : (r * 65536) + (g * 256) + b;
    }

    private static string RandomHex() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    private async Task<IReadOnlyList<MangoDanmakuCandidate>> SearchAsync(string keyword, CancellationToken ct)
    {
        var url =
            $"{SearchUrl}?q={Uri.EscapeDataString(keyword)}&_support=10100001&device=23127PN0CC&osVersion=16&appVersion=9.3.3"
            + $"&did={_deviceId}&mac={_deviceId}&seqId={RandomHex()}&ticket=&userId=0&osType=android&type=10&abroad=0";
        using var document = await _http.GetJsonAsync(url, SearchHeaders, DanmakuHttp.DefaultTimeout, ct).ConfigureAwait(false);
        return ParseSearch(document.RootElement);
    }

    private async Task<MangoDanmakuEpisodePage> FetchEpisodePageAsync(string clipId, string month, CancellationToken ct)
    {
        using var document = await _http
            .GetJsonAsync(
                $"{EpisodesUrl}?allowedRC=1&collection_id={Uri.EscapeDataString(clipId)}&month={Uri.EscapeDataString(month)}&page=1&_support=10000000",
                WebHeaders,
                DanmakuHttp.DefaultTimeout,
                ct)
            .ConfigureAwait(false);
        return ParseEpisodePage(document.RootElement, clipId);
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

            var candidate = candidates[best];
            var first = await FetchEpisodePageAsync(candidate.ClipId, "", ct).ConfigureAwait(false);
            var all = new List<MangoDanmakuEpisode>(first.Episodes);
            foreach (var month in first.Months.Take(MaxMonthPages - 1))
            {
                all.AddRange((await FetchEpisodePageAsync(candidate.ClipId, month, ct).ConfigureAwait(false)).Episodes);
            }

            var episodes = OrderEpisodes(all);
            if (candidate.IsMovie == true && episodes.Count > 1)
            {
                // 电影:只留正片
                episodes = [episodes.FirstOrDefault(item => item.Intact) ?? episodes[0]];
            }

            return new Series(candidate.ClipId, episodes);
        }

        return null;
    }
}
