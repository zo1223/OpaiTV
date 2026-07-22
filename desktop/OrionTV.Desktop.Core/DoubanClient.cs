using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 移植自安卓 services/api.ts 的豆瓣移动端公开接口客户端。
/// 首页分类网格数据直连 m.douban.com，无需后端服务器。
/// </summary>
public sealed class DoubanClient(HttpClient httpClient)
{
    private const string MobileApiUrl = "https://m.douban.com/rexxar/api/v2";

    private static readonly Dictionary<string, string> TagAliases = new()
    {
        ["hot"] = "热门",
        ["latest"] = "最新",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> MobileCollections = new()
    {
        ["movie"] = new()
        {
            ["热门"] = "movie_hot_gaia",
            ["最新"] = "movie_latest",
            ["经典"] = "movie_classic",
            ["豆瓣高分"] = "movie_score",
            ["动作"] = "movie_action",
            ["喜剧"] = "movie_comedy",
            ["爱情"] = "movie_love",
            ["科幻"] = "movie_scifi",
            ["top250"] = "movie_top250",
        },
        ["tv"] = new()
        {
            ["热门"] = "tv_hot",
            ["美剧"] = "tv_american",
            ["韩剧"] = "tv_korean",
            ["日剧"] = "tv_japanese",
            ["国产剧"] = "tv_domestic",
            ["日本动画"] = "tv_animation",
            ["综艺"] = "tv_variety_show",
            ["纪录片"] = "tv_documentary",
        },
    };

    public static readonly IReadOnlyDictionary<string, string> ImageHeaders = new Dictionary<string, string>
    {
        ["Referer"] = "https://movie.douban.com/",
        ["User-Agent"] = "Mozilla/5.0",
    };

    private static string NormalizeTag(string tag) => TagAliases.TryGetValue(tag, out var alias) ? alias : tag;

    public async Task<IReadOnlyList<DoubanItem>> FetchSubjectsAsync(
        string type,
        string tag,
        int pageSize,
        int pageStart,
        CancellationToken cancellationToken = default)
    {
        var normalizedTag = NormalizeTag(tag);

        var collectionItems = await FetchCollectionAsync(type, normalizedTag, pageSize, pageStart, cancellationToken).ConfigureAwait(false);
        if (collectionItems.Count > 0)
        {
            return collectionItems;
        }

        return await FetchRecommendationsAsync(type, normalizedTag, pageSize, pageStart, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DoubanItem>> FetchCollectionAsync(
        string type,
        string tag,
        int pageSize,
        int pageStart,
        CancellationToken cancellationToken)
    {
        if (!MobileCollections.TryGetValue(type, out var collections) || !collections.TryGetValue(tag, out var collectionId))
        {
            return [];
        }

        var url = $"{MobileApiUrl}/subject_collection/{collectionId}/items?start={pageStart}&count={pageSize}&items_only=1&for_mobile=1";
        var root = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("subject_collection_items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return MapSubjects(items, type);
    }

    private async Task<IReadOnlyList<DoubanItem>> FetchRecommendationsAsync(
        string type,
        string tag,
        int pageSize,
        int pageStart,
        CancellationToken cancellationToken)
    {
        var emptyCategories = Uri.EscapeDataString("{}");
        var encodedTag = Uri.EscapeDataString(tag);
        var url =
            $"{MobileApiUrl}/{type}/recommend?refresh=0&start={pageStart}&count={pageSize}" +
            $"&selected_categories={emptyCategories}&uncollect=false&tags={encodedTag}&for_mobile=1";

        var root = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return MapSubjects(items, type);
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Referer", "https://m.douban.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static List<DoubanItem> MapSubjects(JsonElement subjects, string expectedType)
    {
        var result = new List<DoubanItem>();
        foreach (var subject in subjects.EnumerateArray())
        {
            var item = MapSubject(subject, expectedType);
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static DoubanItem? MapSubject(JsonElement subject, string expectedType)
    {
        if (subject.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(subject, "type");
        if (!string.IsNullOrEmpty(type) && type != expectedType)
        {
            return null;
        }

        var title = GetString(subject, "title")?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var id = GetString(subject, "id") ?? title;
        return new DoubanItem(id, title, GetPoster(subject), GetRate(subject), GetString(subject, "year"));
    }

    private static string GetPoster(JsonElement subject)
    {
        if (subject.TryGetProperty("cover", out var cover))
        {
            if (cover.ValueKind == JsonValueKind.String)
            {
                return cover.GetString() ?? "";
            }

            if (cover.ValueKind == JsonValueKind.Object && cover.TryGetProperty("url", out var coverUrl) && coverUrl.ValueKind == JsonValueKind.String)
            {
                return coverUrl.GetString() ?? "";
            }
        }

        if (subject.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.Object)
        {
            if (pic.TryGetProperty("normal", out var normal) && normal.ValueKind == JsonValueKind.String)
            {
                return normal.GetString() ?? "";
            }

            if (pic.TryGetProperty("large", out var large) && large.ValueKind == JsonValueKind.String)
            {
                return large.GetString() ?? "";
            }
        }

        return GetString(subject, "cover_url") ?? "";
    }

    private static string? GetRate(JsonElement subject)
    {
        if (subject.TryGetProperty("rate", out var rate) && rate.ValueKind == JsonValueKind.String)
        {
            return rate.GetString();
        }

        if (subject.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object &&
            rating.TryGetProperty("value", out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.String => value.GetString(),
                _ => null,
            };
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        return null;
    }
}
