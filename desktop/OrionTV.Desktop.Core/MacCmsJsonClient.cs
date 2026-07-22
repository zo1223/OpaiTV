using System.Globalization;
using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>
/// MacCMS 类 JSON 方言：字段仍是 vod_*，但 ac 参数可配置。
/// options/dialect:
/// - searchAc: detail|list（默认 detail，与 AppleCMS 一致；部分站搜索用 list）
/// - listAc / pageAc: detail|videolist（默认 detail）
/// - categoryAc: 分类列表用的 ac，默认 list
/// </summary>
public sealed class MacCmsJsonClient(HttpClient httpClient) : IVodSourceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(VideoSource source, string query, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = ResolveOption(source, "searchAc", dialectDefault: "list", fallback: "detail"),
            ["wd"] = query,
        });
        return Map(source, await FetchVodItemsAsync(url, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<SearchResult>> GetByIdAsync(VideoSource source, string id, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = ResolveOption(source, "detailAc", fallback: "detail"),
            ["ids"] = id,
        });
        return Map(source, await FetchVodItemsAsync(url, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VideoDetail> GetDetailAsync(VideoSource source, string id, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Detail ?? source.Api, new Dictionary<string, string>
        {
            ["ac"] = ResolveOption(source, "detailAc", fallback: "detail"),
            ["ids"] = id,
        });
        var item = (await FetchVodItemsAsync(url, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new InvalidOperationException("MAC_CMS_JSON_DETAIL_NOT_FOUND");
        return AppleCmsMapper.ToVideoDetail(source, item);
    }

    public async Task<IReadOnlyList<VideoSourceCategory>> GetCategoriesAsync(VideoSource source, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = ResolveOption(source, "categoryAc", fallback: "list"),
        });
        return await FetchCategoriesAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> GetSourcePageAsync(VideoSource source, int pageStart, int pageSize, CancellationToken cancellationToken = default)
    {
        var page = pageStart / Math.Max(pageSize, 1) + 1;
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = ResolveOption(source, "pageAc", "listAc", dialectDefault: "videolist", fallback: "detail"),
            ["pg"] = page.ToString(CultureInfo.InvariantCulture),
        });
        var (items, _, _, _) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
        return Map(source, items);
    }

    public async Task<CategoryVideosResponse> GetCategoryVideosAsync(VideoSource source, string categoryId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = ResolveOption(source, "pageAc", "listAc", dialectDefault: "videolist", fallback: "detail"),
            ["t"] = categoryId,
            ["pg"] = page.ToString(CultureInfo.InvariantCulture),
        });
        var (items, total, responsePage, pageCount) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
        var results = Map(source, items).Take(Math.Max(pageSize, 1)).ToList();
        return new CategoryVideosResponse(results, total, responsePage == 0 ? page : responsePage, pageCount);
    }

    public async Task<IReadOnlyList<SearchResult>> GetHomePageAsync(VideoSource source, string type, string tag, int pageStart, int pageSize, CancellationToken cancellationToken = default)
    {
        // 复用 AppleCMS 首页匹配逻辑：分类 → t=；宽标签退回最新页
        var page = pageStart / Math.Max(pageSize, 1) + 1;
        var normalizedTag = HomeCategoryMatcher.NormalizeTag(tag);
        IReadOnlyList<VideoSourceCategory> categories;
        try
        {
            categories = await GetCategoriesAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return await GetSourcePageAsync(source, pageStart, pageSize, cancellationToken).ConfigureAwait(false);
        }

        var match = HomeCategoryMatcher.FindHomeCategory(categories, type, normalizedTag);
        if (match is null)
        {
            if (!HomeCategoryMatcher.IsBroadTag(normalizedTag))
            {
                return [];
            }

            return await GetSourcePageAsync(source, pageStart, pageSize, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
            {
                ["ac"] = ResolveOption(source, "pageAc", "listAc", dialectDefault: "videolist", fallback: "detail"),
                ["t"] = match.Category.Id,
                ["pg"] = page.ToString(CultureInfo.InvariantCulture),
            });
            var (items, _, _, _) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
            var filtered = items.Where(item =>
                HomeCategoryMatcher.ShouldIncludeHomeItem(item, type, normalizedTag, match.UsedTypeFallback, match.Candidate));
            return Map(source, filtered);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// dialect 快捷：search_ac_list → searchAc=list；videolist → page/list ac=videolist。
    /// </summary>
    private static string ResolveOption(
        VideoSource source,
        string primaryKey,
        string? secondaryKey = null,
        string? dialectDefault = null,
        string fallback = "detail")
    {
        if (source.Options is not null)
        {
            if (source.Options.TryGetValue(primaryKey, out var primary) && !string.IsNullOrWhiteSpace(primary))
            {
                return primary.Trim();
            }

            if (secondaryKey is not null
                && source.Options.TryGetValue(secondaryKey, out var secondary)
                && !string.IsNullOrWhiteSpace(secondary))
            {
                return secondary.Trim();
            }
        }

        var dialect = (source.Dialect ?? "").Trim().ToLowerInvariant();
        if (dialect is "search_ac_list" or "search-list" && primaryKey == "searchAc")
        {
            return "list";
        }

        if (dialect is "videolist" or "video_list" or "ac_videolist"
            && primaryKey is "pageAc" or "listAc")
        {
            return "videolist";
        }

        if (!string.IsNullOrWhiteSpace(dialectDefault)
            && dialect is "maccms" or "default" or "videolist" or "search_ac_list")
        {
            // maccms_json 未特别指定时：搜索若 dialect=search_ac_list 已处理；
            // 列表默认可走 videolist（许多 MacCMS 板子）。
            if (primaryKey is "pageAc" or "listAc" && dialect is "videolist" or "maccms")
            {
                return dialectDefault;
            }
        }

        return fallback;
    }

    private static List<SearchResult> Map(VideoSource source, IEnumerable<AppleCmsVodItem> items) =>
        items
            .Select(item => AppleCmsMapper.ToSearchResult(source, item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Title))
            .ToList();

    private async Task<IReadOnlyList<AppleCmsVodItem>> FetchVodItemsAsync(string url, CancellationToken cancellationToken)
    {
        var (items, _, _, _) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
        return items;
    }

    private async Task<(IReadOnlyList<AppleCmsVodItem> Items, int Total, int Page, int PageCount)> FetchVodPageAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MAC_CMS_JSON_REQUEST_FAILED:{(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var items = root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(item => item.Deserialize<AppleCmsVodItem>(JsonOptions))
                .Where(item => item is not null)
                .Cast<AppleCmsVodItem>()
                .ToList()
            : [];

        return (items, GetInt(root, "total"), GetInt(root, "page"), GetInt(root, "pagecount", "pageCount"));
    }

    private async Task<IReadOnlyList<VideoSourceCategory>> FetchCategoriesAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MAC_CMS_JSON_CATEGORIES_FAILED:{(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("class", out var classes) || classes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<VideoSourceCategory>();
        foreach (var entry in classes.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = entry.TryGetProperty("type_id", out var typeId) ? ConvertId(typeId) : "";
            var name = entry.TryGetProperty("type_name", out var typeName) && typeName.ValueKind == JsonValueKind.String
                ? typeName.GetString() ?? ""
                : "";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                result.Add(new VideoSourceCategory(id, name));
            }
        }

        return result;
    }

    private static string ConvertId(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.String => element.GetString() ?? "",
        _ => "",
    };

    private static int GetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}
