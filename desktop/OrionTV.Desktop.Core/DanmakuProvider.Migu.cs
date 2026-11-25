using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

// ---------- 咪咕视频 ----------
// 与手机版 services/danmaku/migu.ts 逐条对应,改规则时两边一起改。

public sealed record MiguDanmakuCandidate(string ContentId, string Title, string? Year, int EpisodeCount, bool? IsMovie)
    : DanmakuTitleCandidate(Title, Year, EpisodeCount, IsMovie);

/// <param name="Duration">时长(秒);未知为 0。</param>
public sealed record MiguDanmakuEpisode(string Pid, int? Number, string Title, double Duration);

/// <param name="SeriesId">整部剧的号(弹幕地址里要用);没有时用分集号。</param>
public sealed record MiguDanmakuEpisodePage(
    string SeriesId,
    int TotalPages,
    int PageSize,
    IReadOnlyList<MiguDanmakuEpisode> Episodes);

/// <summary>
/// 内置弹幕来源:咪咕视频公开接口(搜索 → 分集 → 弹幕分片),不需要登录和服务器。
/// 接口与字段参考 huangxd-/danmu_api 的 migu 源。
/// 弹幕分片的应答是 AES-256-ECB 加密后的 base64 文本,密钥是固定的。
/// </summary>
public sealed class MiguDanmakuProvider : IDanmakuProvider
{
    private const string SearchUrl = "https://jadeite.migu.cn/search/v3/open-search";
    private const string DetailBaseUrl = "https://v3-sc.miguvideo.com/program/v4/cont/content-info";
    private const string DanmakuBaseUrl = "https://webapi.miguvideo.com/gateway/live_barrage/videox/barrage/v2/list";
    private const int SegmentSeconds = 30;
    private const string SearchPackIds =
        "1002581,1002601,1003861,1003862,1003863,1003864,1003865,1003866,1004041,1004121,1004261,1004262,1004281,1004321,1004262,1004281,1004322,1004261,1004421,1004422,1002781,1004301,1004641,1004761,1005061,1005261,1005301,1005321,1005361,1005362,1005341,1005342,1005521,1005722,1005721,1015749,1015761,1015760,1015762,1015763,1015768,1015786,1015790,1015812,1015813,1015814,1015815,1015816,1015817,1015820,1015819,1015821";
    private const string SearchSid = "1X4A395AL3XS3UV5MT8PQ1L5JUYK6KFM8VKDSDD48S5Y7YX5WY1MOUHRT6512988";
    private const string GatewayKeyText = "vwwLu7e6ug4HAQMAug8CsA8HD7oHDwuxAg4HAQG6DLA=";

    private static readonly byte[] KeyNibbles = [3, 5, 7, 0, 15, 10, 13, 1, 11, 14, 4, 6, 9, 12, 8, 2];
    private static readonly Regex EpisodeInName = new(@"第[\s﻿]*0*([0-9]{1,4})[\s﻿]*[集话話回期]");
    private static readonly Regex HexColor = new(@"^[0-9a-fA-F]{6}\z");
    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Lazy<byte[]> GatewayKey = new(() =>
        Convert.FromBase64String(GatewayKeyText)
            .Select(value => (byte)((KeyNibbles[value >> 4] << 4) | KeyNibbles[value & 15]))
            .ToArray());

    private static readonly IReadOnlyDictionary<string, string> SearchHeaders = new Dictionary<string, string>
    {
        ["Accept"] = "application/json, text/plain, */*",
        ["Origin"] = "https://www.miguvideo.com",
        ["Referer"] = "https://www.miguvideo.com/",
        ["User-Agent"] = DanmakuHttp.UserAgent,
        ["appId"] = "miguvideo",
        ["terminalId"] = "www",
    };

    private static readonly IReadOnlyDictionary<string, string> SegmentHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = DanmakuHttp.UserAgent,
        ["appCode"] = "miguvideo_default_h5",
    };

    private sealed class Series(string contentId, MiguDanmakuEpisodePage first)
    {
        public string ContentId { get; } = contentId;

        public MiguDanmakuEpisodePage First { get; } = first;

        public Dictionary<int, Task<MiguDanmakuEpisodePage>> Pages { get; } = [];
    }

    private readonly IDanmakuHttp _http;
    private readonly DanmakuSeriesCache<Series?> _seriesCache = new();

    public MiguDanmakuProvider(IDanmakuHttp http)
    {
        _http = http;
    }

    public string Name => "migu";

    // ---------- 解析 ----------

    public static IReadOnlyList<MiguDanmakuCandidate> ParseSearch(JsonElement data)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MiguDanmakuCandidate>();
        foreach (var item in data.Prop("body").Prop("contentInfoList").Items())
        {
            var asset = item.Prop("shortMediaAsset");
            // isLong 为 0 的是短视频、资讯
            if (!asset.Truthy() || !asset.Prop("isLong").Truthy())
            {
                continue;
            }

            var extra = asset.Prop("extraData");
            var episodeIds = extra.Prop("episodes").Items().ToList();
            var contentId = DanmakuJs.Trim(extra.Truthy() ? episodeIds.Count > 0 ? ((JsonElement?)episodeIds[0]).Text() : "" : asset.Prop("pID").Text());
            var title = DanmakuJs.Trim(asset.Prop("name").Text());
            if (contentId.Length == 0 || title.Length == 0 || !seen.Add(contentId))
            {
                continue;
            }

            var isMovie = asset.Prop("contDisplayName").Text() == "电影";
            var year = DanmakuJs.Trim(asset.Prop("year").Text());
            result.Add(new MiguDanmakuCandidate(
                contentId,
                title,
                year.Length > 0 ? year : null,
                isMovie ? 1 : episodeIds.Count,
                isMovie));
        }

        return result;
    }

    /// <summary>"42:44"、"01:00:04" → 秒;认不出返回 0。</summary>
    public static double ParseDuration(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return 0;
        }

        var parts = text.Split(':');
        if (parts.Length > 3)
        {
            return 0;
        }

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number)
                || number < 0)
            {
                return 0;
            }

            total = total * 60 + number;
        }

        return total;
    }

    public static MiguDanmakuEpisodePage ParseEpisodePage(JsonElement data)
    {
        var root = data.Prop("body").Prop("data");
        var episodes = new List<MiguDanmakuEpisode>();
        if (!root.Truthy())
        {
            return new MiguDanmakuEpisodePage("", 1, 0, episodes);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var datas = root.Prop("datas").Items().ToList();
        if (datas.Count > 0)
        {
            foreach (var item in datas)
            {
                var pid = DanmakuJs.Trim(item.Prop("pID").Text());
                if (pid.Length == 0 || !seen.Add(pid))
                {
                    continue;
                }

                var title = item.Prop("name").Text();
                episodes.Add(new MiguDanmakuEpisode(
                    pid,
                    EpisodeNumber(item.Prop("index"), title),
                    title,
                    ParseDuration(item.Prop("duration").Text())));
            }
        }
        else if (root.Prop("playing").Prop("pID").Truthy())
        {
            // 没有分集列表的单片(电影)
            var playing = root.Prop("playing");
            episodes.Add(new MiguDanmakuEpisode(
                playing.Prop("pID").Text(),
                null,
                root.Prop("name").Truthy() ? root.Prop("name").Text() : playing.Prop("name").Text(),
                ParseDuration(playing.Prop("duration").Text())));
        }

        // 正片和花絮混在一起时只留正片
        var main = episodes.Where(episode => !DanmakuTitle.IsExtraEpisodeTitle(episode.Title)).ToList();
        return new MiguDanmakuEpisodePage(
            DanmakuJs.Trim(root.Prop("epsID").Text()),
            Math.Max(1, PositiveInt(root.Prop("totalPage").Number())),
            PositiveInt(root.Prop("pageSize").Number()),
            main.Count > 0 ? main : episodes);
    }

    public static IReadOnlyList<DanmakuComment> ParseSegment(JsonElement? data)
    {
        var result = new List<DanmakuComment>();
        foreach (var item in data.Prop("body").Prop("result").Items())
        {
            var text = DanmakuJs.Trim(item.Prop("msg").Text());
            var playtime = item.Prop("playtime");
            if (text.Length == 0
                || playtime is null
                || playtime.Value.ValueKind == JsonValueKind.Null
                || (playtime.Value.ValueKind == JsonValueKind.String && playtime.Value.GetString()!.Length == 0))
            {
                continue;
            }

            var time = playtime.Number();
            if (!double.IsFinite(time) || time < 0)
            {
                continue;
            }

            var raw = item.Prop("textcolor").Text();
            if (raw.StartsWith('#'))
            {
                raw = raw[1..];
            }

            var color = HexColor.IsMatch(raw) ? int.Parse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0xffffff;
            result.Add(new DanmakuComment(time, text, color, DanmakuMode.Scroll));
        }

        // 咪咕的弹幕时间只精确到秒,摊开到这一秒里,避免每秒整排同时冒出来
        return TencentDanmakuProvider.SpreadWithinSecond(result.OrderBy(comment => comment.Time).ToList());
    }

    // ---------- 解密 ----------

    /// <summary>AES-256-ECB 解密并去掉 PKCS#7 填充。密文不合法时抛异常。</summary>
    public static byte[] DecryptSegment(byte[] cipher)
    {
        if (cipher.Length == 0 || cipher.Length % 16 != 0)
        {
            throw new FormatException("migu: bad cipher length");
        }

        using var aes = Aes.Create();
        aes.Key = GatewayKey.Value;
        try
        {
            return aes.DecryptEcb(cipher, PaddingMode.PKCS7);
        }
        catch (CryptographicException exception)
        {
            throw new FormatException("migu: bad padding", exception);
        }
    }

    /// <summary>
    /// 弹幕分片应答 → JSON。应答通常是加密的 base64 文本;已经是明文 JSON 时直接解析;空应答返回 null。
    /// 解不开时抛异常。返回的文档由调用方释放。
    /// </summary>
    public static JsonDocument? DecodeSegmentBody(string body)
    {
        var text = body.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '{' || text[0] == '[')
        {
            return JsonDocument.Parse(text);
        }

        var cipher = Convert.FromBase64String(Whitespace.Replace(text, ""));
        return JsonDocument.Parse(DecryptSegment(cipher));
    }

    // ---------- 来源 ----------

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
        // 不知道时长就没法分片
        if (episode is null || episode.Duration <= 0)
        {
            return null;
        }

        var seriesId = series.First.SeriesId.Length > 0 ? series.First.SeriesId : episode.Pid;
        var pid = episode.Pid;
        var duration = episode.Duration;
        return new DanmakuTrack(
            "migu",
            episode.Title,
            SegmentSeconds,
            async (segmentIndex, token) =>
            {
                double start = (double)segmentIndex * SegmentSeconds;
                if (start < 0 || start >= duration)
                {
                    return [];
                }

                var end = Math.Min(start + SegmentSeconds, duration);
                var bytes = await _http
                    .GetBytesAsync(
                        $"{DanmakuBaseUrl}/{seriesId}/{pid}/{Js(start)}/{Js(end)}/020",
                        SegmentHeaders,
                        DanmakuHttp.DefaultTimeout,
                        token)
                    .ConfigureAwait(false);
                using var document = DecodeSegmentBody(Encoding.UTF8.GetString(bytes));
                return ParseSegment(document?.RootElement);
            });
    }

    private async Task<IReadOnlyList<MiguDanmakuCandidate>> SearchAsync(string keyword, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            appVersion = "6.1.1.00",
            ct = 101,
            isCorrectWord = 1,
            k = keyword,
            mediaSource = 9000000,
            packId = SearchPackIds,
            pageIdx = 1,
            pageSize = 20,
            sid = SearchSid,
            copyrightTerminal = 3,
            searchScene = 2,
            uiVersion = "A3.26.0",
        });
        using var document = await _http
            .PostJsonAsync(SearchUrl, body, SearchHeaders, DanmakuHttp.DefaultTimeout, ct)
            .ConfigureAwait(false);
        return ParseSearch(document.RootElement);
    }

    private async Task<MiguDanmakuEpisodePage> FetchEpisodePageAsync(string contentId, int page, CancellationToken ct)
    {
        using var document = await _http
            .GetJsonAsync(
                $"{DetailBaseUrl}/{Uri.EscapeDataString(contentId)}/{page.ToString(CultureInfo.InvariantCulture)}",
                DanmakuProviderShared.UserAgentOnly,
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

            var contentId = candidates[best].ContentId;
            return new Series(contentId, await FetchEpisodePageAsync(contentId, 1, ct).ConfigureAwait(false));
        }

        return null;
    }

    private Task<MiguDanmakuEpisodePage> LoadPageAsync(Series series, int page, CancellationToken ct)
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

    private async Task<MiguDanmakuEpisodePage> LoadPageCoreAsync(Series series, int page, CancellationToken ct)
    {
        // 先让出,保证调用方已经把这个任务放进 series.Pages(失败时才能把它摘掉)
        await Task.Yield();
        try
        {
            return await FetchEpisodePageAsync(series.ContentId, page, ct).ConfigureAwait(false);
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

    private async Task<MiguDanmakuEpisode?> FindEpisodeAsync(Series series, DanmakuQuery query, CancellationToken ct)
    {
        MiguDanmakuEpisode? Pick(IReadOnlyList<MiguDanmakuEpisode> episodes, int episodeIndex)
        {
            var index = DanmakuTitle.PickEpisodeIndex(
                episodes.Select(episode => episode.Number).ToList(),
                episodeIndex,
                query.EpisodeNumber,
                query.EpisodeCount);
            return index >= 0 ? episodes[index] : null;
        }

        var first = series.First;
        if (first.TotalPages <= 1 || first.PageSize <= 0)
        {
            return Pick(first.Episodes, query.EpisodeIndex);
        }

        // 分页的长剧:只取当前集所在的那一页
        var page = (query.EpisodeNumber - 1) / first.PageSize + 1;
        if (page > first.TotalPages)
        {
            return null;
        }

        var offset = (page - 1) * first.PageSize;
        var episodes = page == 1 ? first.Episodes : (await LoadPageAsync(series, page, ct).ConfigureAwait(false)).Episodes;
        return Pick(episodes, query.EpisodeIndex - offset);
    }

    private static int? EpisodeNumber(JsonElement? index, string name)
    {
        var byIndex = index is null || index.Value.ValueKind == JsonValueKind.Null
            ? null
            : DanmakuTitle.ParseEpisodeNumber(index.Text());
        if (byIndex is > 0)
        {
            return byIndex;
        }

        var match = EpisodeInName.Match(name);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static int PositiveInt(double value) =>
        double.IsFinite(value) && value >= 1 && value < int.MaxValue ? (int)Math.Floor(value) : 0;

    private static string Js(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
