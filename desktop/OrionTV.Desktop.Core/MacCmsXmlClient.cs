using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace OrionTV.Desktop.Core;

/// <summary>
/// MacCMS 经典 XML 输出（at=xml）。将 &lt;class&gt;/&lt;list&gt;/&lt;video&gt;/&lt;dl&gt;&lt;dd&gt; 映射为统一 SearchResult。
/// </summary>
public sealed class MacCmsXmlClient(HttpClient httpClient) : IVodSourceClient
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(VideoSource source, string query, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "list",
            ["at"] = "xml",
            ["wd"] = query,
        });
        var root = await FetchXmlAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseVideos(source, root);
    }

    public async Task<IReadOnlyList<SearchResult>> GetByIdAsync(VideoSource source, string id, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["at"] = "xml",
            ["ids"] = id,
        });
        var root = await FetchXmlAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseVideos(source, root);
    }

    public async Task<VideoDetail> GetDetailAsync(VideoSource source, string id, CancellationToken cancellationToken = default)
    {
        var results = await GetByIdAsync(source, id, cancellationToken).ConfigureAwait(false);
        var item = results.FirstOrDefault()
            ?? throw new InvalidOperationException("MAC_CMS_XML_DETAIL_NOT_FOUND");
        return new VideoDetail(
            item.Id,
            item.Title,
            item.Poster,
            item.Source,
            item.SourceName,
            item.Episodes,
            item.Year,
            item.Description,
            item.TypeName);
    }

    public async Task<IReadOnlyList<VideoSourceCategory>> GetCategoriesAsync(VideoSource source, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "list",
            ["at"] = "xml",
        });
        var root = await FetchXmlAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseCategories(root);
    }

    public async Task<IReadOnlyList<SearchResult>> GetSourcePageAsync(VideoSource source, int pageStart, int pageSize, CancellationToken cancellationToken = default)
    {
        var page = pageStart / Math.Max(pageSize, 1) + 1;
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "list",
            ["at"] = "xml",
            ["pg"] = page.ToString(CultureInfo.InvariantCulture),
        });
        var root = await FetchXmlAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseVideos(source, root);
    }

    public async Task<CategoryVideosResponse> GetCategoryVideosAsync(VideoSource source, string categoryId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var url = AppleCmsUrlBuilder.Build(source.Api, new Dictionary<string, string>
        {
            ["ac"] = "list",
            ["at"] = "xml",
            ["t"] = categoryId,
            ["pg"] = page.ToString(CultureInfo.InvariantCulture),
        });
        var root = await FetchXmlAsync(url, cancellationToken).ConfigureAwait(false);
        var results = ParseVideos(source, root).Take(Math.Max(pageSize, 1)).ToList();
        var total = TryReadInt(root, "list", "pagecount") * Math.Max(pageSize, 1);
        var pageCount = TryReadInt(root, "list", "pagecount");
        return new CategoryVideosResponse(results, total, page, pageCount);
    }

    public async Task<IReadOnlyList<SearchResult>> GetHomePageAsync(VideoSource source, string type, string tag, int pageStart, int pageSize, CancellationToken cancellationToken = default)
    {
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

        var response = await GetCategoryVideosAsync(source, match.Category.Id, page, pageSize, cancellationToken)
            .ConfigureAwait(false);
        return response.Results;
    }

    private async Task<XDocument> FetchXmlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MAC_CMS_XML_REQUEST_FAILED:{(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return XDocument.Load(stream);
    }

    /// <summary>
    /// 应答是不是 AppleCMS / MacCMS XML（判断方式对齐 RN 端 parseAppleCmsXml）。JSON、HTML 错误页、
    /// 其它 XML 一律返回 null。供 <see cref="AppleCmsClient"/> 在 JSON 解析失败后按应答内容自动识别 XML 源。
    /// </summary>
    internal static XDocument? TryParseCmsDocument(byte[] payload)
    {
        ReadOnlySpan<byte> text = payload;
        if (text.StartsWith("\uFEFF"u8))
        {
            text = text[3..];
        }

        text = text.TrimStart(" \t\r\n"u8);
        if (text.IsEmpty || text[0] != (byte)'<' || StartsWithTag(text, "<!doctype html") || StartsWithTag(text, "<html"))
        {
            return null;
        }

        XDocument document;
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            // 这里的应答没有经过任何格式约定：不解析 DTD，也不解析外部实体。
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            });
            document = XDocument.Load(reader);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or NotSupportedException or ArgumentException or IOException)
        {
            return null;
        }

        var root = document.Root;
        return root is not null && (root.Name.LocalName == "rss" || IsCmsNode(root) || root.Elements().Any(IsCmsNode))
            ? document
            : null;

        static bool IsCmsNode(XElement element) => element.Name.LocalName is "list" or "class";

        // 标签名之后必须是空白或 '>'，<htmlx> 之类不算
        static bool StartsWithTag(ReadOnlySpan<byte> text, string tag)
        {
            if (text.Length <= tag.Length)
            {
                return false;
            }

            for (var index = 0; index < tag.Length; index++)
            {
                if (char.ToLowerInvariant((char)text[index]) != tag[index])
                {
                    return false;
                }
            }

            return text[tag.Length] is (byte)'>' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
        }
    }

    /// <summary>
    /// 把 XML 的 &lt;video&gt; 转成与 JSON 接口相同的字段形状（字段对应关系对齐 RN 端 appleCmsXml.ts），
    /// 后续的映射、筛选逻辑与 JSON 源共用。
    /// </summary>
    internal static List<AppleCmsVodItem> ParseVodItems(XDocument document)
    {
        var items = new List<AppleCmsVodItem>();
        foreach (var video in document.Descendants("video"))
        {
            var item = new AppleCmsVodItem
            {
                VodId = ChildText(video, "id", "vod_id"),
                VodName = ChildText(video, "name", "vod_name", "title"),
                VodPic = ChildText(video, "pic", "vod_pic", "img"),
                TypeId = ChildText(video, "tid", "type_id"),
                TypeName = ChildText(video, "type", "type_name"),
                VodRemarks = ChildText(video, "note", "vod_remarks", "remarks"),
                VodYear = ChildText(video, "year", "vod_year"),
                VodArea = ChildText(video, "area", "vod_area"),
                VodActor = ChildText(video, "actor", "vod_actor"),
                VodDirector = ChildText(video, "director", "vod_director"),
                VodContent = ChildText(video, "des", "vod_content", "content"),
            };

            // 线路名与播放组取自同一批 dd：缺 flag 或内容为空的 dd 不会让后面的线路名错位。
            var playDds = video.Descendants("dd").Where(dd => !string.IsNullOrWhiteSpace(dd.Value)).ToList();
            if (playDds.Count > 0)
            {
                item.VodPlayFrom = string.Join("$$$", playDds.Select(dd => ((string?)dd.Attribute("flag") ?? "").Trim()));
                item.VodPlayUrl = string.Join("$$$", playDds.Select(dd => dd.Value.Trim()));
            }
            else
            {
                item.VodPlayUrl = ChildText(video, "dl", "vod_play_url", "playurl");
                item.VodPlayFrom = item.VodPlayUrl is null ? null : ChildText(video, "vod_play_from", "playfrom");
            }

            items.Add(item);
        }

        return items;

        static string? ChildText(XElement parent, params string[] names)
        {
            foreach (var name in names)
            {
                var text = parent.Element(name)?.Value.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            return null;
        }
    }

    internal static IReadOnlyList<VideoSourceCategory> ParseCategories(XDocument document)
    {
        var result = new List<VideoSourceCategory>();
        foreach (var ty in document.Descendants("ty"))
        {
            var id = (string?)ty.Attribute("id") ?? ty.Value.Trim();
            var name = string.IsNullOrWhiteSpace(ty.Value) ? id : ty.Value.Trim();
            // 某些板子 <ty id="1">电影</ty>
            if (ty.Attribute("id") is not null)
            {
                id = ty.Attribute("id")!.Value;
            }

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                result.Add(new VideoSourceCategory(id.Trim(), name.Trim()));
            }
        }

        return result;
    }

    private static List<SearchResult> ParseVideos(VideoSource source, XDocument document)
    {
        var baseUri = AppleCmsEpisodeParser.TryGetBaseUri(source);
        var baseUrl = baseUri?.ToString();
        var results = new List<SearchResult>();
        foreach (var video in document.Descendants("video"))
        {
            var id = TextOf(video, "id");
            var name = TextOf(video, "name", "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var pic = TextOf(video, "pic", "pic_url");
            var year = TextOf(video, "year");
            var des = TextOf(video, "des", "content", "blurb");
            var typeName = TextOf(video, "type", "type_name");
            // 线路名与播放组必须取自同一批 dd：分开过滤空值时，一个缺 flag 或缺地址的 dd 会让后面的线路名整体错位。
            var playDds = video.Descendants("dd").Where(dd => !string.IsNullOrWhiteSpace(dd.Value)).ToList();
            var playFrom = string.Join("$$$", playDds.Select(dd => ((string?)dd.Attribute("flag") ?? "").Trim()));
            // 无 flag 时仍解析 dd 文本为播放组
            var playGroups = playDds.Select(dd => dd.Value.Trim()).ToList();
            if (playGroups.Count == 0)
            {
                var single = TextOf(video, "dl", "playurl", "vod_play_url");
                if (!string.IsNullOrWhiteSpace(single))
                {
                    playGroups.Add(single);
                }
            }

            var playUrl = string.Join("$$$", playGroups);
            var playbackSources = AppleCmsEpisodeParser.ParseSources(
                string.IsNullOrWhiteSpace(playFrom) ? null : playFrom,
                playUrl,
                baseUrl);

            results.Add(new SearchResult(
                Id: id.Trim(),
                Title: name.Trim(),
                Poster: AppleCmsEpisodeParser.TryResolvePlayUrl(pic, baseUri) ?? pic ?? "",
                Episodes: playbackSources.FirstOrDefault()?.Episodes ?? AppleCmsEpisodeParser.Parse(playUrl, baseUrl),
                Source: source.Key,
                SourceName: source.Name,
                Year: year ?? "",
                Description: des,
                TypeName: typeName,
                PlaybackSources: playbackSources.Count > 0 ? playbackSources : null,
                EpisodeLabels: playbackSources.FirstOrDefault()?.EpisodeLabels));
        }

        return results;
    }

    private static string? TextOf(XElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var el = parent.Element(name) ?? parent.Descendants(name).FirstOrDefault();
            if (el is not null && !string.IsNullOrWhiteSpace(el.Value))
            {
                return el.Value.Trim();
            }
        }

        return null;
    }

    internal static int TryReadInt(XDocument document, string elementName, string attributeName)
    {
        var el = document.Descendants(elementName).FirstOrDefault();
        var attr = el?.Attribute(attributeName)?.Value;
        return int.TryParse(attr, out var value) ? value : 0;
    }
}
