using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

// 爱奇艺弹幕来源。与手机版 services/danmaku/iqiyi.ts 逐条对应,改规则时两边一起改。

public sealed record IqiyiDanmakuCandidate(
    string AlbumId,
    string MovieTvId,
    string Title,
    string? Year,
    int EpisodeCount,
    bool? IsMovie)
    : DanmakuTitleCandidate(Title, Year, EpisodeCount, IsMovie);

public sealed record IqiyiDanmakuEpisode(string TvId, int? Number, string Title);

public sealed record IqiyiDanmakuEpisodePage(int Total, IReadOnlyList<IqiyiDanmakuEpisode> Episodes);

/// <summary>
/// 内置弹幕来源:爱奇艺公开接口(搜索 → 分集 → 视频信息 → 弹幕分片),不需要登录和服务器。
/// 弹幕分片地址与字段参考 huangxd-/danmu_api 的 iqiyi 源;搜索与分集用的是不需要签名的旧接口
/// (新版搜索接口频繁返回风控码,不适合在客户端直连)。
/// </summary>
public sealed class IqiyiDanmakuProvider : IDanmakuProvider
{
    private const string SearchUrl = "https://search.video.iqiyi.com/o";
    private const string EpisodesUrl = "https://pcw-api.iqiyi.com/albums/album/avlistinfo";
    private const string VideoInfoUrl = "https://pcw-api.iqiyi.com/video/video/baseinfo";
    private const string DanmakuBaseUrl = "https://cmts.iqiyi.com/bullet";

    /// <summary>每个弹幕分片覆盖的秒数。</summary>
    private const int SegmentSeconds = 300;
    private const int EpisodePageSize = 200;

    private static readonly string[] AllowedChannels = ["电视剧", "电影", "动漫", "综艺", "纪录片", "儿童"];

    private static readonly Regex Tags = new("<[^>]+>");
    private static readonly Regex IdPattern = new(@"^[1-9][0-9]*\z");
    private static readonly Regex YearPrefix = new(@"^(19|20)[0-9]{2}");
    private static readonly Regex BulletPattern = new(@"<bulletInfo>([\s\S]*?)</bulletInfo>");
    private static readonly Regex ContentPattern = new(@"<content>([\s\S]*?)</content>");
    private static readonly Regex ShowTimePattern = new(@"<showTime>([\s\S]*?)</showTime>");
    private static readonly Regex ColorPattern = new(@"<color>([\s\S]*?)</color>");
    private static readonly Regex HexColor = new(@"^[0-9a-fA-F]{1,6}\z");
    private static readonly Regex NumericEntity = new("&#(x?)([0-9a-fA-F]+);");
    private static readonly Regex NamedEntity = new("&(amp|lt|gt|quot|apos);");

    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["User-Agent"] = DanmakuHttp.UserAgent,
        ["Referer"] = "https://www.iqiyi.com/",
        ["Accept"] = "application/json, text/plain, */*",
    };

    private sealed class Series(string albumId, int total, IReadOnlyList<IqiyiDanmakuEpisode> firstPage)
    {
        public string AlbumId { get; } = albumId;

        /// <summary>分集总数(含非正片);超过一页时按集号分页取。</summary>
        public int Total { get; } = total;

        public IReadOnlyList<IqiyiDanmakuEpisode> FirstPage { get; } = firstPage;

        public Dictionary<int, Task<IReadOnlyList<IqiyiDanmakuEpisode>>> Pages { get; } = [];
    }

    private readonly IDanmakuHttp _http;
    private readonly DanmakuSeriesCache<Series?> _seriesCache = new();

    public IqiyiDanmakuProvider(IDanmakuHttp http)
    {
        _http = http;
    }

    public string Name => "iqiyi";

    /// <summary>编号统一成十进制数字串;不是正整数时返回空串。</summary>
    private static string IdText(JsonElement? value)
    {
        if (value is not { } element)
        {
            return "";
        }

        var text = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => DanmakuJs.Trim(element.GetString()),
            _ => "",
        };
        return IdPattern.IsMatch(text) ? text : "";
    }

    public static IReadOnlyList<IqiyiDanmakuCandidate> ParseSearch(JsonElement data)
    {
        var docs = data.Prop("data").Prop("docinfos");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IqiyiDanmakuCandidate>();
        foreach (var doc in docs.Items())
        {
            var album = doc.Prop("albumDocInfo");
            if (!album.Truthy())
            {
                continue;
            }

            // 只要爱奇艺站内的正片专辑(其他站点的结果、短视频、播单都不要)
            if (!album.Prop("siteId").Is("iqiyi") || !album.Prop("videoDocType").Is(1))
            {
                continue;
            }

            var channelText = album.Prop("channel").Truthy() ? album.Prop("channel").Text() : "";
            var channel = channelText.Split(',')[0];
            if (!AllowedChannels.Contains(channel))
            {
                continue;
            }

            var albumId = IdText(album.Prop("albumId"));
            var rawTitle = album.Prop("albumTitle").Truthy() ? album.Prop("albumTitle").Text() : "";
            var title = DanmakuJs.Trim(Tags.Replace(rawTitle, ""));
            if (albumId.Length == 0 || title.Length == 0 || seen.Contains(albumId))
            {
                continue;
            }

            var isMovie = channel == "电影";
            var movieTvId = "";
            if (isMovie)
            {
                movieTvId = IdText(album.Prop("videoinfos").First().Prop("tvId"));
                if (movieTvId.Length == 0)
                {
                    movieTvId = albumId;
                }
            }

            var releaseDate = album.Prop("releaseDate").Truthy() ? album.Prop("releaseDate").Text() : "";
            var yearMatch = YearPrefix.Match(releaseDate);
            seen.Add(albumId);
            result.Add(new IqiyiDanmakuCandidate(
                albumId,
                movieTvId,
                title,
                yearMatch.Success ? yearMatch.Value : null,
                isMovie ? 1 : (int)album.Prop("itemTotalNumber").NumberOrZero(),
                isMovie));
        }

        return result;
    }

    public static IqiyiDanmakuEpisodePage ParseEpisodePage(JsonElement data)
    {
        var root = data.Prop("data");
        var episodes = new List<IqiyiDanmakuEpisode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.Prop("epsodelist").Items())
        {
            JsonElement? episode = item;
            var tvId = IdText(episode.Prop("tvId"));

            // contentType 1 是正片,其余是预告、花絮
            if (tvId.Length == 0 || !episode.Prop("contentType").Is(1) || seen.Contains(tvId))
            {
                continue;
            }

            seen.Add(tvId);
            var order = episode.Prop("order").Number();
            int? number = double.IsFinite(order) && Math.Floor(order) == order && order > 0 && order <= int.MaxValue
                ? (int)order
                : null;
            var title = episode.Prop("name").Truthy()
                ? episode.Prop("name").Text()
                : episode.Prop("shortTitle").Truthy() ? episode.Prop("shortTitle").Text() : "";
            episodes.Add(new IqiyiDanmakuEpisode(tvId, number, title));
        }

        var main = episodes.Where(episode => !DanmakuTitle.IsExtraEpisodeTitle(episode.Title)).ToList();
        return new IqiyiDanmakuEpisodePage((int)root.Prop("total").NumberOrZero(), main.Count > 0 ? main : episodes);
    }

    /// <summary>视频信息里的时长(秒);不显示弹幕或没有时长时返回 0。</summary>
    public static double ParseDuration(JsonElement data)
    {
        var info = data.Prop("data");
        if (info is not { ValueKind: JsonValueKind.Object })
        {
            return 0;
        }

        if (info.Prop("displayBarrage") is { ValueKind: JsonValueKind.False })
        {
            return 0;
        }

        var seconds = info.Prop("durationSec").Number();
        return double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
    }

    /// <summary>第 index 个分片(从 0 开始)的地址:目录取视频编号的末四位。</summary>
    public static string BuildSegmentUrl(string tvId, int index)
    {
        var padded = "0000" + tvId;
        var tail = padded[^4..];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DanmakuBaseUrl}/{tail[..2]}/{tail[2..]}/{tvId}_{SegmentSeconds}_{index + 1}.z");
    }

    private static string DecodeXmlText(string text)
    {
        var numeric = NumericEntity.Replace(text, match =>
        {
            var value = DanmakuJs.ParseInt(match.Groups[2].Value, match.Groups[1].Value.Length > 0 ? 16 : 10);
            return value is > 0 and <= 0x10ffff and not (>= 0xd800 and <= 0xdfff)
                ? char.ConvertFromUtf32((int)value.Value)
                : "";
        });
        return NamedEntity.Replace(numeric, match => match.Groups[1].Value switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            _ => "'",
        });
    }

    /// <summary>解析爱奇艺弹幕 XML:每条 &lt;bulletInfo&gt; 里取 content / showTime(秒) / color(十六进制)。</summary>
    public static IReadOnlyList<DanmakuComment> ParseXml(string xml)
    {
        var result = new List<DanmakuComment>();
        foreach (Match match in BulletPattern.Matches(xml ?? ""))
        {
            var block = match.Groups[1].Value;
            var text = DanmakuJs.Trim(DecodeXmlText(Field(ContentPattern, block)));
            var time = DanmakuJs.ParseFloat(Field(ShowTimePattern, block));
            if (text.Length == 0 || !double.IsFinite(time) || time < 0)
            {
                continue;
            }

            var rawColor = DanmakuJs.Trim(Field(ColorPattern, block));
            var color = HexColor.IsMatch(rawColor)
                ? int.Parse(rawColor, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : 0xffffff;
            result.Add(new DanmakuComment(time, text, color, DanmakuMode.Scroll));
        }

        // 时间只精确到秒,同一秒的弹幕摊开显示
        return TencentDanmakuProvider.SpreadWithinSecond(result);
    }

    private static string Field(Regex pattern, string block)
    {
        var match = pattern.Match(block);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>弹幕分片是 zlib 压缩的 XML:已经是文本时直接用,否则解压;解不开返回空串。</summary>
    public static string DecodeSegmentBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }

        if (bytes[0] == 0x3C)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        try
        {
            var inflated = DanmakuJs.Inflate(
                bytes,
                stream => new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionMode.Decompress));
            return Encoding.UTF8.GetString(inflated);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return "";
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
        if (episode is null)
        {
            return null;
        }

        double duration;
        using (var document = await _http
                   .GetJsonAsync($"{VideoInfoUrl}/{episode.TvId}", Headers, DanmakuHttp.DefaultTimeout, ct)
                   .ConfigureAwait(false))
        {
            duration = ParseDuration(document.RootElement);
        }

        if (duration <= 0)
        {
            return null;
        }

        var segmentCount = (int)Math.Ceiling(duration / SegmentSeconds);
        var tvId = episode.TvId;
        return new DanmakuTrack(
            "iqiyi",
            episode.Title,
            SegmentSeconds,
            async (segmentIndex, token) =>
            {
                if (segmentIndex < 0 || segmentIndex >= segmentCount)
                {
                    return [];
                }

                var bytes = await _http
                    .GetBytesAsync(
                        BuildSegmentUrl(tvId, segmentIndex),
                        DanmakuProviderShared.UserAgentOnly,
                        DanmakuHttp.DefaultTimeout,
                        token)
                    .ConfigureAwait(false);
                return ParseXml(DecodeSegmentBytes(bytes));
            });
    }

    private async Task<IReadOnlyList<IqiyiDanmakuCandidate>> SearchAsync(string keyword, CancellationToken ct)
    {
        using var document = await _http
            .GetJsonAsync(
                $"{SearchUrl}?if=html5&key={DanmakuJs.EncodeUriComponent(keyword)}&pageNum=1&pageSize=20&video_allow_3rd=0",
                Headers,
                DanmakuHttp.DefaultTimeout,
                ct)
            .ConfigureAwait(false);
        return ParseSearch(document.RootElement);
    }

    private async Task<IqiyiDanmakuEpisodePage> FetchEpisodePageAsync(string albumId, int page, CancellationToken ct)
    {
        using var document = await _http
            .GetJsonAsync(
                string.Create(CultureInfo.InvariantCulture, $"{EpisodesUrl}?aid={albumId}&page={page}&size={EpisodePageSize}"),
                Headers,
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

            var candidate = candidates[best];
            if (candidate.IsMovie == true)
            {
                return new Series(candidate.AlbumId, 1, [new IqiyiDanmakuEpisode(candidate.MovieTvId, null, candidate.Title)]);
            }

            var first = await FetchEpisodePageAsync(candidate.AlbumId, 1, ct).ConfigureAwait(false);
            return new Series(candidate.AlbumId, first.Total, first.Episodes);
        }

        return null;
    }

    private Task<IReadOnlyList<IqiyiDanmakuEpisode>> LoadPageAsync(Series series, int page, CancellationToken ct)
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

    private async Task<IReadOnlyList<IqiyiDanmakuEpisode>> LoadPageCoreAsync(Series series, int page, CancellationToken ct)
    {
        // 先让出,保证调用方已经把这个任务放进 series.Pages(失败时才能把它摘掉)
        await Task.Yield();
        try
        {
            return (await FetchEpisodePageAsync(series.AlbumId, page, ct).ConfigureAwait(false)).Episodes;
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

    private async Task<IqiyiDanmakuEpisode?> FindEpisodeAsync(Series series, DanmakuQuery query, CancellationToken ct)
    {
        IqiyiDanmakuEpisode? Pick(IReadOnlyList<IqiyiDanmakuEpisode> episodes, int episodeIndex)
        {
            var index = DanmakuTitle.PickEpisodeIndex(
                episodes.Select(episode => episode.Number).ToList(),
                episodeIndex,
                query.EpisodeNumber,
                query.EpisodeCount);
            return index >= 0 ? episodes[index] : null;
        }

        if (series.Total <= EpisodePageSize)
        {
            return Pick(series.FirstPage, query.EpisodeIndex);
        }

        // 分页的长剧:只取当前集所在的那一页
        var page = ((query.EpisodeNumber - 1) / EpisodePageSize) + 1;
        var episodes = page == 1 ? series.FirstPage : await LoadPageAsync(series, page, ct).ConfigureAwait(false);
        return Pick(episodes, query.EpisodeNumber - 1 - ((page - 1) * EpisodePageSize));
    }
}
