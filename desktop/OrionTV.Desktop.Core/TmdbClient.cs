using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>TMDB 查到的一条元数据（只用于补全详情页里原本为空的字段）。</summary>
public sealed record TmdbMetadata(
    int TmdbId,
    string MediaType,
    string Title,
    string? PosterUrl,
    string? Overview,
    double? Rating,
    string? Year);

/// <summary>TMDB search/multi 返回的一条影视结果（已丢掉人物等其它类型）。</summary>
public sealed record TmdbCandidate(
    int Id,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    string? PosterPath,
    string? Overview,
    double VoteAverage,
    int VoteCount,
    string? Date);

/// <summary>片名归一化与候选挑选：纯函数，不联网。</summary>
public static partial class TmdbMatcher
{
    public const string Movie = "movie";
    public const string Tv = "tv";

    [GeneratedRegex(@"[\(（\[【［][^\)）\]】］]*[\)）\]】］]")]
    private static partial Regex BracketNote();

    // 末尾的季 / 部 / 集说明：第二季、第2部、Season 2、S02、S02E05、空格后的 1~2 位数字
    [GeneratedRegex(@"(?:\s*第\s*[0-9一二三四五六七八九十百零〇两]+\s*[季部期辑輯集话話回弹彈]|\s*season\s*\d{1,2}|\s+s\d{1,2}(?:e\d{1,3})?|\s+\d{1,2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonSuffix();

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex FourDigits();

    /// <summary>去掉括号里的备注（国语版、未删减…），不动季数。</summary>
    public static string StripNotes(string? title) =>
        BracketNote().Replace((title ?? string.Empty).Normalize(NormalizationForm.FormKC), " ").Trim();

    /// <summary>去掉括号备注和末尾的季 / 部 / 集说明，得到用于检索的片名。</summary>
    public static string StripSeasonSuffix(string? title)
    {
        var text = StripNotes(title);
        for (var round = 0; round < 3; round++)
        {
            var stripped = SeasonSuffix().Replace(text, string.Empty).Trim();
            if (stripped.Length == 0 || stripped == text)
            {
                break;
            }

            text = stripped;
        }

        return text;
    }

    /// <summary>比较用的归一化：NFKC、小写、只留字母和数字。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>从 "2023"、"2023-05-01" 之类的文本里取四位年份。</summary>
    public static int? ParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = FourDigits().Match(text);
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1880 and <= 2200
            ? year
            : null;
    }

    /// <summary>按源站分类名猜 TMDB 的类型：电影 / …片 → movie；剧 / 动漫 / 综艺 / 番 → tv；猜不出返回 null。</summary>
    public static string? HintMediaType(string? category)
    {
        var text = (category ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("电影", StringComparison.Ordinal) || text.Contains("電影", StringComparison.Ordinal)
            || text.EndsWith('片'))
        {
            return Movie;
        }

        foreach (var keyword in new[] { "剧", "劇", "动漫", "動漫", "综艺", "綜藝", "番" })
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                return Tv;
            }
        }

        return null;
    }

    /// <summary>
    /// 只接受片名完全相同（归一化后）的影视结果，没有就不补全。先按完整片名找，找不到再按去掉季数后的片名找；
    /// 同名多条时优先年份相符（±1），其次类型与分类相符，再次原顺序。
    /// </summary>
    public static TmdbCandidate? PickBest(
        IReadOnlyList<TmdbCandidate> results,
        string? title,
        string? year,
        string? category)
    {
        var full = Normalize(StripNotes(title));
        var stripped = Normalize(StripSeasonSuffix(title));
        if (full.Length == 0 || results.Count == 0)
        {
            return null;
        }

        var hint = HintMediaType(category);
        var picked = PickByTitle(results, full, year, hint);
        if (picked is null && stripped.Length > 0 && stripped != full)
        {
            // 片名带“第二季”之类：只可能是剧集，分类猜不出类型时按剧集优先
            picked = PickByTitle(results, stripped, year, hint ?? Tv);
        }

        return picked;
    }

    private static TmdbCandidate? PickByTitle(
        IReadOnlyList<TmdbCandidate> results,
        string normalizedTitle,
        string? year,
        string? hint)
    {
        var sourceYear = ParseYear(year);
        TmdbCandidate? best = null;
        var bestScore = -1;
        foreach (var candidate in results)
        {
            if (candidate.MediaType is not (Movie or Tv))
            {
                continue;
            }

            if (Normalize(candidate.Title) != normalizedTitle && Normalize(candidate.OriginalTitle) != normalizedTitle)
            {
                continue;
            }

            var score = 0;
            if (sourceYear is { } wanted && ParseYear(candidate.Date) is { } actual && Math.Abs(wanted - actual) <= 1)
            {
                score += 2;
            }

            if (hint is not null && candidate.MediaType == hint)
            {
                score += 1;
            }

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>解析 search/multi 的响应；格式不对时抛异常，由调用方按失败处理。</summary>
    public static IReadOnlyList<TmdbCandidate> ParseResults(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<TmdbCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var mediaType = Text(item, "media_type");
            if (mediaType is not (Movie or Tv))
            {
                continue;
            }

            if (item.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                continue;
            }

            var isMovie = mediaType == Movie;
            list.Add(new TmdbCandidate(
                id,
                mediaType,
                Text(item, isMovie ? "title" : "name") ?? Text(item, isMovie ? "name" : "title"),
                Text(item, isMovie ? "original_title" : "original_name"),
                Text(item, "poster_path"),
                Text(item, "overview"),
                Number(item, "vote_average"),
                (int)Number(item, "vote_count"),
                Text(item, isMovie ? "release_date" : "first_air_date")));
        }

        return list;
    }

    public static TmdbMetadata ToMetadata(TmdbCandidate candidate)
    {
        var posterPath = candidate.PosterPath?.Trim();
        var overview = candidate.Overview?.Trim();
        return new TmdbMetadata(
            candidate.Id,
            candidate.MediaType,
            candidate.Title ?? candidate.OriginalTitle ?? string.Empty,
            string.IsNullOrEmpty(posterPath)
                ? null
                : TmdbClient.ImageBase + (posterPath.StartsWith('/') ? posterPath : "/" + posterPath),
            string.IsNullOrEmpty(overview) ? null : overview,
            candidate.VoteCount > 0 && candidate.VoteAverage > 0 ? Math.Round(candidate.VoteAverage, 1) : null,
            ParseYear(candidate.Date)?.ToString(CultureInfo.InvariantCulture));
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0;
}

/// <summary>把 TMDB 元数据并进详情页已有字段的结果：只补空，不覆盖。</summary>
public sealed record TmdbMergeResult(
    string Poster,
    string Description,
    string Year,
    string RatingText,
    bool PosterFilled,
    bool DescriptionFilled,
    bool YearFilled);

public static class TmdbMerge
{
    /// <summary>原有的非空值全部保留；海报、简介、年份只在原值为空时用 TMDB 的补上。</summary>
    public static TmdbMergeResult Apply(
        string? poster,
        string? description,
        string? year,
        TmdbMetadata? tmdb,
        string? existingRating = null)
    {
        var fillPoster = string.IsNullOrWhiteSpace(poster) && !string.IsNullOrWhiteSpace(tmdb?.PosterUrl);
        var fillDescription = string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(tmdb?.Overview);
        var fillYear = string.IsNullOrWhiteSpace(year) && !string.IsNullOrWhiteSpace(tmdb?.Year);
        return new TmdbMergeResult(
            fillPoster ? tmdb!.PosterUrl! : poster ?? string.Empty,
            fillDescription ? tmdb!.Overview! : description ?? string.Empty,
            fillYear ? tmdb!.Year! : year ?? string.Empty,
            string.IsNullOrWhiteSpace(existingRating) ? RatingText(tmdb) : existingRating.Trim(),
            fillPoster,
            fillDescription,
            fillYear);
    }

    /// <summary>详情页显示的评分文字，例如 "TMDB 8.1"；没有评分时为空。</summary>
    public static string RatingText(TmdbMetadata? tmdb) =>
        tmdb?.Rating is { } rating
            ? "TMDB " + rating.ToString("0.0", CultureInfo.InvariantCulture)
            : string.Empty;
}

/// <summary>
/// TMDB 元数据查询。只有用户在设置里填了自己的 Key 才会发请求；请求只发往 TMDB 官方接口。
/// 任何失败（网络、401、响应格式、超时）都返回 null，调用方照旧显示原有数据。
/// </summary>
public sealed class TmdbClient
{
    public const string ApiHost = "api.themoviedb.org";
    public const string ImageBase = "https://image.tmdb.org/t/p/w500";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private const string SearchEndpoint = "https://api.themoviedb.org/3/search/multi";
    private const int CacheLimit = 200;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, TmdbMetadata> _cache = new(StringComparer.Ordinal);

    public TmdbClient(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>v4 Read Access Token 是 JWT（eyJ 开头、两个点）；其余按 v3 API Key 处理。</summary>
    public static bool LooksLikeBearerToken(string credential) =>
        credential.StartsWith("eyJ", StringComparison.Ordinal) && credential.Count(ch => ch == '.') == 2;

    /// <summary>构造查询请求：Bearer 走请求头，v3 Key 走 api_key 参数；始终 include_adult=false。</summary>
    public static HttpRequestMessage BuildSearchRequest(string credential, string query)
    {
        var bearer = LooksLikeBearerToken(credential);
        var url = SearchEndpoint
            + "?query=" + Uri.EscapeDataString(query)
            + "&language=zh-CN&include_adult=false&page=1"
            + (bearer ? string.Empty : "&api_key=" + Uri.EscapeDataString(credential));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        return request;
    }

    /// <summary>
    /// 按片名查 TMDB。没填 Key 时直接返回 null，不发请求；失败、超时、被取消、没有同名结果都返回 null，不抛异常。
    /// </summary>
    public async Task<TmdbMetadata?> LookupAsync(
        string? apiKey,
        string? title,
        string? year,
        string? category,
        CancellationToken cancellationToken = default)
    {
        var credential = apiKey?.Trim();
        if (string.IsNullOrEmpty(credential))
        {
            return null;
        }

        try
        {
            var query = TmdbMatcher.StripSeasonSuffix(title);
            if (TmdbMatcher.Normalize(query).Length == 0)
            {
                return null;
            }

            var cacheKey = string.Join(
                '|',
                TmdbMatcher.Normalize(TmdbMatcher.StripNotes(title)),
                TmdbMatcher.ParseYear(year)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                TmdbMatcher.HintMediaType(category) ?? string.Empty);
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = BuildSearchRequest(credential, query);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var picked = TmdbMatcher.PickBest(TmdbMatcher.ParseResults(json), title, year, category);
            if (picked is null)
            {
                return null;
            }

            var metadata = TmdbMatcher.ToMetadata(picked);
            if (_cache.Count >= CacheLimit)
            {
                _cache.Clear();
            }

            _cache[cacheKey] = metadata;
            return metadata;
        }
        catch
        {
            // 元数据只是锦上添花：任何失败都静默回落到原有数据。不记录请求地址（里面可能带 Key）。
            return null;
        }
    }
}
