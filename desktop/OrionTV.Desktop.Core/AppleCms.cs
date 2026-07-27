using System.Text.Json;

namespace OrionTV.Desktop.Core;

public static class AppleCmsUrlBuilder
{
    public static string Build(string apiUrl, IReadOnlyDictionary<string, string> parameters)
    {
        var strippedApiUrl = StripTrailingSlash(apiUrl);
        var proxyMarkerIndex = FindProxyUrlMarker(strippedApiUrl);
        if (proxyMarkerIndex < 0)
        {
            return AppendQueryParams(strippedApiUrl, parameters);
        }

        var valueStart = proxyMarkerIndex + "url=".Length;
        var valueEnd = strippedApiUrl.IndexOf('&', valueStart);
        if (valueEnd < 0)
        {
            valueEnd = strippedApiUrl.Length;
        }

        var encodedTarget = strippedApiUrl[valueStart..valueEnd];
        var targetUrl = SafeUnescape(encodedTarget);
        var targetWithParams = AppendQueryParams(targetUrl, parameters);
        return $"{strippedApiUrl[..valueStart]}{targetWithParams}{strippedApiUrl[valueEnd..]}";
    }

    /// <summary>
    /// 只把 ?url= / &amp;url= 识别为代理形态(对齐 RN 端的 [?&amp;]url= 判断),
    /// 否则 ?imgurl=、&amp;sourceurl= 这类普通参数会被误当代理目标改写。
    /// </summary>
    private static int FindProxyUrlMarker(string url)
    {
        var searchStart = 0;
        while (searchStart < url.Length)
        {
            var index = url.IndexOf("url=", searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            if (index > 0 && (url[index - 1] == '?' || url[index - 1] == '&'))
            {
                return index;
            }

            searchStart = index + 1;
        }

        return -1;
    }

    private static string AppendQueryParams(string url, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return $"{StripTrailingSlash(url)}{(url.Contains('?') ? "&" : "?")}{query}";
    }

    private static string StripTrailingSlash(string value) => value.TrimEnd('/');

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }
}

public static class AppleCmsEpisodeParser
{
    private sealed record ParsedEpisode(string Label, string Url);

    public static IReadOnlyList<string> Parse(string? playUrl, string? baseUrl = null)
    {
        return ParseEpisodes(playUrl, baseUrl)
            .Select(episode => episode.Url)
            .ToList();
    }

    private static IReadOnlyList<ParsedEpisode> ParseEpisodes(string? playUrl, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(playUrl))
        {
            return [];
        }

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase))
        {
            baseUri = parsedBase;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var episodes = new List<ParsedEpisode>();
        foreach (var group in playUrl.Split("$$$", StringSplitOptions.None))
        {
            foreach (var episode in group.Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = episode.IndexOf('$');
                var label = separatorIndex >= 0 ? episode[..separatorIndex].Trim() : "";
                var raw = (separatorIndex >= 0 ? episode[(separatorIndex + 1)..] : episode).Trim();
                if (TryResolvePlayUrl(raw, baseUri) is not { } url || !seen.Add(url))
                {
                    continue;
                }

                episodes.Add(new ParsedEpisode(label, url));
            }
        }

        return episodes;
    }

    public static IReadOnlyList<PlaybackSource> ParseSources(string? playFrom, string? playUrl, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(playUrl))
        {
            return [];
        }

        var names = (playFrom ?? "").Split("$$$", StringSplitOptions.None);
        var groups = playUrl.Split("$$$", StringSplitOptions.None);
        var result = new List<PlaybackSource>();
        for (var index = 0; index < groups.Length; index++)
        {
            var parsedEpisodes = ParseEpisodes(groups[index], baseUrl);
            var episodes = parsedEpisodes.Select(episode => episode.Url).ToList();
            if (episodes.Count == 0)
            {
                continue;
            }

            var name = index < names.Length && !string.IsNullOrWhiteSpace(names[index])
                ? names[index].Trim()
                : $"线路 {index + 1}";
            var labels = parsedEpisodes.Select(episode => episode.Label).ToList();
            result.Add(new PlaybackSource(
                $"{index}:{name}",
                name,
                episodes,
                labels.Any(label => !string.IsNullOrWhiteSpace(label)) ? labels : null));
        }

        return result
            .Where(candidate => !result.Any(direct => IsMatchingDirectHlsRoute(candidate, direct)))
            .ToList();
    }

    private static bool IsMatchingDirectHlsRoute(PlaybackSource candidate, PlaybackSource direct)
    {
        if (ReferenceEquals(candidate, direct)
            || candidate.Episodes.Count == 0
            || candidate.Episodes.Count != direct.Episodes.Count)
        {
            return false;
        }

        for (var index = 0; index < candidate.Episodes.Count; index++)
        {
            if (!IsDirectHlsVariant(candidate.Episodes[index], direct.Episodes[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDirectHlsVariant(string pageUrl, string directUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
            || !Uri.TryCreate(directUrl, UriKind.Absolute, out var direct)
            || !string.Equals(page.Scheme, direct.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(page.Host, direct.Host, StringComparison.OrdinalIgnoreCase)
            || page.Port != direct.Port)
        {
            return false;
        }

        var expectedPath = page.AbsolutePath.TrimEnd('/') + "/index.m3u8";
        return string.Equals(expectedPath, direct.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>将相对播放地址解析为绝对 URL；无法解析则返回 null。</summary>
    public static string? TryResolvePlayUrl(string? raw, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, value, out var resolved)
            && (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
        {
            return resolved.ToString();
        }

        return null;
    }

    public static Uri? TryGetBaseUri(VideoSource source)
    {
        foreach (var candidate in new[] { source.Detail, source.Api })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                // 相对播放地址按「API 所在目录」解析更常见：/play/x 相对站点根，play/x 相对 API 路径
                return uri;
            }
        }

        return null;
    }
}

public static class SearchResultSources
{
    public static string KeyOf(SearchResult result) => KeyOf(result.Source, result.PlaybackSourceKey);

    public static string KeyOf(string source, string? playbackSourceKey) =>
        $"{source}\u001f{playbackSourceKey ?? ""}";

    public static string SelectionKeyOf(SearchResult result) =>
        SelectionKeyOf(result.Source, result.Id, result.PlaybackSourceKey);

    public static string SelectionKeyOf(string source, string id, string? playbackSourceKey) =>
        $"{source}\u001f{id}\u001f{playbackSourceKey ?? ""}";

    public static IReadOnlyList<SearchResult> Expand(SearchResult result)
    {
        if (result.PlaybackSources is not { Count: > 0 })
        {
            return [result];
        }

        return result.PlaybackSources
            .Select(playbackSource => result with
            {
                Episodes = playbackSource.Episodes,
                EpisodeLabels = playbackSource.EpisodeLabels,
                SourceName = $"{result.SourceName} · {playbackSource.Name}",
                PlaybackSourceKey = playbackSource.Key,
            })
            .ToList();
    }
}

public static class AppleCmsMapper
{
    public static SearchResult ToSearchResult(VideoSource source, AppleCmsVodItem item)
    {
        var baseUrl = AppleCmsEpisodeParser.TryGetBaseUri(source)?.ToString();
        var playbackSources = AppleCmsEpisodeParser.ParseSources(item.VodPlayFrom, item.VodPlayUrl, baseUrl);
        return new SearchResult(
            Id: Convert.ToString(item.VodId, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Title: item.VodName ?? "",
            Poster: ResolveMaybeRelative(item.VodPic, baseUrl) ?? item.VodPic ?? "",
            Episodes: playbackSources.FirstOrDefault()?.Episodes ?? [],
            Source: source.Key,
            SourceName: source.Name,
            Year: item.VodYear ?? "",
            Description: item.VodBlurb ?? item.VodContent ?? item.VodRemarks,
            TypeName: item.TypeName,
            Class: item.VodClass,
            PlaybackSources: playbackSources.Count > 0 ? playbackSources : null,
            EpisodeLabels: playbackSources.FirstOrDefault()?.EpisodeLabels);
    }

    public static VideoDetail ToVideoDetail(VideoSource source, AppleCmsVodItem item)
    {
        var baseUrl = AppleCmsEpisodeParser.TryGetBaseUri(source)?.ToString();
        return new VideoDetail(
            Id: Convert.ToString(item.VodId, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Title: item.VodName ?? "",
            Poster: ResolveMaybeRelative(item.VodPic, baseUrl) ?? item.VodPic ?? "",
            Source: source.Key,
            SourceName: source.Name,
            Episodes: AppleCmsEpisodeParser.Parse(item.VodPlayUrl, baseUrl),
            Year: item.VodYear ?? "",
            Description: item.VodContent ?? item.VodBlurb ?? item.VodRemarks,
            TypeName: item.TypeName,
            Area: item.VodArea,
            Director: item.VodDirector,
            Actor: item.VodActor);
    }

    private static string? ResolveMaybeRelative(string? value, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            baseUri = parsed;
        }

        return AppleCmsEpisodeParser.TryResolvePlayUrl(value, baseUri) ?? value;
    }
}

public sealed class AppleCmsClient(HttpClient httpClient) : IVodSourceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(VideoSource source, string query, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["wd"] = query
        });
        var items = await FetchVodItemsAsync(url, cancellationToken).ConfigureAwait(false);
        return items
            .Select(item => AppleCmsMapper.ToSearchResult(source, item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Title))
            .ToList();
    }

    /// <summary>
    /// 按 vod id 取结果（含剧集），始终走 source.Api（对齐安卓 getAppleCmsSourceResult）。
    /// 注意：不要用 source.Detail —— 部分订阅的 detail 字段是裸域名，对 ac=detail&ids 无效。
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> GetByIdAsync(VideoSource source, string id, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["ids"] = id
        });
        var items = await FetchVodItemsAsync(url, cancellationToken).ConfigureAwait(false);
        return items
            .Select(item => AppleCmsMapper.ToSearchResult(source, item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Title))
            .ToList();
    }

    public async Task<VideoDetail> GetDetailAsync(VideoSource source, string id, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Detail ?? source.Api, new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["ids"] = id
        });
        var item = (await FetchVodItemsAsync(url, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new InvalidOperationException("APPLE_CMS_DETAIL_NOT_FOUND");
        return AppleCmsMapper.ToVideoDetail(source, item);
    }

    public async Task<IReadOnlyList<VideoSourceCategory>> GetCategoriesAsync(VideoSource source, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string> { ["ac"] = "list" });
        return await FetchCategoriesAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> GetSourcePageAsync(VideoSource source, int pageStart, int pageSize, CancellationToken cancellationToken = default)
    {
        var page = pageStart / pageSize + 1;
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var (items, _, _, _) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
        // 返回整页,不按 pageSize 截断:源站页大小与客户端页大小不一致时,
        // 截断掉的条目在下一次请求(已翻到源站下一页)中永久丢失。
        return MapResults(source, items).ToList();
    }

    public async Task<CategoryVideosResponse> GetCategoryVideosAsync(VideoSource source, string categoryId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["t"] = categoryId,
            ["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var (items, total, responsePage, pageCount) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
        var results = MapResults(source, items).Take(pageSize).ToList();
        return new CategoryVideosResponse(results, total, responsePage == 0 ? page : responsePage, pageCount);
    }

    public async Task<IReadOnlyList<SearchResult>> GetHomePageAsync(VideoSource source, string type, string tag, int pageStart, int pageSize, CancellationToken cancellationToken = default)
    {
        var page = pageStart / pageSize + 1;
        var normalizedTag = HomeCategoryMatcher.NormalizeTag(tag);

        IReadOnlyList<VideoSourceCategory> categories;
        try
        {
            categories = await GetCategoriesAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 源站不暴露分类时，退回到最新一页。
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
                ["ac"] = "detail",
                ["t"] = match.Category.Id,
                ["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            var (items, _, _, _) = await FetchVodPageAsync(url, cancellationToken).ConfigureAwait(false);
            var filtered = items.Where(item => HomeCategoryMatcher.ShouldIncludeHomeItem(item, type, normalizedTag, match.UsedTypeFallback, match.Candidate));
            // 同上:返回过滤后的整页,截断会造成条目永久丢失。
            return MapResults(source, filtered).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<SearchResult> MapResults(VideoSource source, IEnumerable<AppleCmsVodItem> items) =>
        items
            .Select(item => AppleCmsMapper.ToSearchResult(source, item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Title));

    private async Task<IReadOnlyList<AppleCmsVodItem>> FetchVodItemsAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"APPLE_CMS_REQUEST_FAILED:{(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray()
            .Select(item => item.Deserialize<AppleCmsVodItem>(JsonOptions))
            .Where(item => item is not null)
            .Cast<AppleCmsVodItem>()
            .ToList();
    }

    private async Task<(IReadOnlyList<AppleCmsVodItem> Items, int Total, int Page, int PageCount)> FetchVodPageAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"APPLE_CMS_REQUEST_FAILED:{(int)response.StatusCode}");
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
            throw new InvalidOperationException($"APPLE_CMS_CATEGORIES_FAILED:{(int)response.StatusCode}");
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
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0;
    }
}
