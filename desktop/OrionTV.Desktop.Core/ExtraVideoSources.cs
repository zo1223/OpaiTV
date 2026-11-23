using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>AI/CLI 导入的额外视频源，与订阅源合并后供播放器使用。</summary>
public static class ExtraVideoSourcesStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => DesktopPaths.ExtraSourcesFile;

    public static IReadOnlyList<VideoSource> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var payload = File.ReadAllText(FilePath);
            return ParseFlexible(payload);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 兼容多种写入格式：
    /// 1) 完整订阅 JSON：{ "api_site": { ... } }
    /// 2) { "sources": [ { key,name,api,detail } ] }
    /// 3) 直接数组 [ { key,name,api,detail } ]
    /// 4) 单个源对象 { key,name,api,detail }
    /// </summary>
    public static IReadOnlyList<VideoSource> ParseFlexible(string payload)
    {
        var trimmed = payload.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        // 优先尝试标准订阅格式（含 base58）
        try
        {
            return VideoSourceSubscriptionParser.Parse(trimmed).Sources;
        }
        catch
        {
            // fall through
        }

        using var document = JsonDocument.Parse(trimmed);
        var root = document.RootElement;
        return root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray()
                .Select((item, index) => MapLoose(item, index))
                .Where(source => source is not null)
                .Cast<VideoSource>()
                .ToList(),
            JsonValueKind.Object when root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array
                => sources.EnumerateArray()
                    .Select((item, index) => MapLoose(item, index))
                    .Where(source => source is not null)
                    .Cast<VideoSource>()
                    .ToList(),
            JsonValueKind.Object => MapLoose(root, 0) is { } single ? [single] : [],
            _ => [],
        };
    }

    /// <summary>
    /// 一键加源 / 删除源改写文件之前读取现有内容。与 <see cref="Load"/> 不同：文件存在但无法解析时抛出异常，
    /// 调用方据此放弃写入，避免把读不出来的现有源整体覆盖掉。
    /// </summary>
    public static IReadOnlyList<VideoSource> LoadForUpdate()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return ParseFlexible(File.ReadAllText(FilePath));
    }

    public static void Save(IEnumerable<VideoSource> sources)
    {
        DesktopPaths.EnsureDirectory();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Serialize(sources), Encoding.UTF8);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>序列化为 extra_sources.json 的标准格式（{ "api_site": { key: {...} } }）。</summary>
    public static string Serialize(IEnumerable<VideoSource> sources)
    {
        var list = sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Api) && !string.IsNullOrWhiteSpace(source.Name))
            .Select(source =>
            {
                var key = string.IsNullOrWhiteSpace(source.Key) ? SlugKey(source.Name) : source.Key;
                var map = new Dictionary<string, object?>
                {
                    ["name"] = source.Name.Trim(),
                    ["api"] = source.Api.Trim().TrimEnd('/'),
                };
                if (!string.IsNullOrWhiteSpace(source.Detail))
                {
                    map["detail"] = source.Detail.Trim().TrimEnd('/');
                }

                var protocol = VideoSourceProtocols.Normalize(source.Protocol);
                if (!string.Equals(protocol, VideoSourceProtocols.AppleCms, StringComparison.Ordinal))
                {
                    map["protocol"] = protocol;
                }

                if (!string.IsNullOrWhiteSpace(source.Dialect))
                {
                    map["dialect"] = source.Dialect.Trim();
                }

                if (source.Options is { Count: > 0 })
                {
                    map["options"] = source.Options.ToDictionary(kv => kv.Key, kv => kv.Value);
                }

                // 未标记的源不写入该字段，存储格式与原来一致
                if (source.Adult)
                {
                    map["adult"] = true;
                }

                return (key, map);
            })
            .ToList();

        var envelope = new Dictionary<string, object>
        {
            ["api_site"] = list.ToDictionary(item => item.key, item => item.map),
        };

        return JsonSerializer.Serialize(envelope, WriteOptions);
    }

    public static void EnsureFileExists()
    {
        if (File.Exists(FilePath))
        {
            return;
        }

        DesktopPaths.EnsureDirectory();
        Save([]);
    }

    private static VideoSource? MapLoose(JsonElement item, int index)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var api = GetString(item, "api") ?? GetString(item, "url") ?? GetString(item, "base");
        var name = GetString(item, "name") ?? GetString(item, "title") ?? GetString(item, "key");
        var key = GetString(item, "key") ?? GetString(item, "id");
        key ??= name is not null ? slugFrom(name) : $"ai_{index}";
        name ??= key;
        if (string.IsNullOrWhiteSpace(api) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var detail = GetString(item, "detail");
        var protocol = VideoSourceProtocols.Normalize(
            GetString(item, "protocol") ?? GetString(item, "type") ?? GetString(item, "format"));
        var dialect = GetString(item, "dialect");
        IReadOnlyDictionary<string, string>? options = null;
        if (item.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in optionsElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[property.Name] = value.Trim();
                }
            }

            if (map.Count > 0)
            {
                options = map;
            }
        }

        return new VideoSource(
            key.Trim(),
            name.Trim(),
            api.Trim().TrimEnd('/'),
            string.IsNullOrWhiteSpace(detail) ? null : detail.Trim().TrimEnd('/'),
            protocol,
            string.IsNullOrWhiteSpace(dialect) ? null : dialect.Trim(),
            options,
            AdultContentFilter.ReadFlag(item));
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string slugFrom(string name) => SlugKey(name);

    public static string SlugKey(string name)
    {
        var ascii = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (!string.IsNullOrEmpty(ascii))
        {
            return ascii.Length > 32 ? ascii[..32] : ascii;
        }

        // 非拉丁名：用稳定短哈希，避免 key 冲突
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim())))[..8]
            .ToLowerInvariant();
        return "ai_" + hash;
    }
}

/// <summary>订阅源 + AI 额外源合并（同 key 时额外源覆盖订阅源）。</summary>
public static class VideoSourceMerger
{
    public static IReadOnlyList<VideoSource> Merge(
        IEnumerable<VideoSource> subscriptionSources,
        IEnumerable<VideoSource> extraSources)
    {
        var map = new Dictionary<string, VideoSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in subscriptionSources)
        {
            if (string.IsNullOrWhiteSpace(source.Key) || string.IsNullOrWhiteSpace(source.Api))
            {
                continue;
            }

            map[source.Key] = source;
        }

        foreach (var source in extraSources)
        {
            if (string.IsNullOrWhiteSpace(source.Key) || string.IsNullOrWhiteSpace(source.Api))
            {
                continue;
            }

            map[source.Key] = source;
        }

        return map.Values.ToList();
    }
}

/// <summary>
/// 一键加源（对齐 RN 端 addManualVideoSource）：粘贴一个源订阅链接或单个源接口地址即可添加，可标记为成人源。
/// 添加的源写入 extra_sources.json，与订阅源合并；主订阅链接被清空或更换时这些源保留。
/// 接口是 JSON 还是 XML 不需要选择，请求时按应答自动识别。
/// </summary>
public static partial class OneClickVideoSources
{
    /// <summary>单个源地址生成的 key 带固定前缀（与 RN 端一致）。</summary>
    public const string ManualKeyPrefix = "custom_";

    public const string InvalidUrlError = "INVALID_VIDEO_SOURCE_URL";

    /// <param name="ExtraSources">加入后的额外源列表（写回 extra_sources.json）。</param>
    /// <param name="Added">新增的源数量。</param>
    /// <param name="Updated">地址已存在、只更新了成人标记的源数量。</param>
    /// <param name="FromSubscription">粘贴的地址被识别为订阅配置（导入其中全部源），否则是单个源接口。</param>
    /// <param name="FromSync">粘贴的是其他设备导出的同步链接（导入其中全部源）。</param>
    /// <param name="Unchanged">同步链接里已存在且内容相同、被跳过的源数量。</param>
    public sealed record Result(
        IReadOnlyList<VideoSource> ExtraSources,
        int Added,
        int Updated,
        bool FromSubscription = false,
        bool FromSync = false,
        int Unchanged = 0);

    // 主机必须是域名、IP 或带端口的主机名：ftp://…、javascript:… 以及随手输入的一段文字都不会被当成地址存下来。
    [GeneratedRegex(@"^https?://((?:[a-z0-9-]+\.)*[a-z0-9-]+)(:[0-9]{1,5})?(?:[/?#]\S*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"^(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]+)?$")]
    private static partial Regex IpHostRegex();

    /// <summary>
    /// 整理粘贴的地址：去掉首尾空白和结尾的 /，没写协议时补全（IP 或带端口按 http，其余按 https）。
    /// 不是可用的 http(s) 地址时抛出 <see cref="InvalidUrlError"/>。
    /// </summary>
    public static string NormalizeUrl(string? input) => ParseUrl(input).Url;

    private static (string Url, string Host) ParseUrl(string? input)
    {
        var url = (input ?? "").Trim().TrimEnd('/');

        if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var hostPart = url.Split('/')[0];
            url = (IpHostRegex().IsMatch(hostPart) || Regex.IsMatch(hostPart, ":[0-9]+") ? "http://" : "https://") + url;
        }

        var match = HttpUrlRegex().Match(url);
        var host = match.Success ? (match.Groups[1].Value + match.Groups[2].Value).ToLowerInvariant() : "";
        if (!match.Success || !(match.Groups[1].Value.Contains('.') || match.Groups[2].Success || host == "localhost"))
        {
            throw new InvalidOperationException(InvalidUrlError);
        }

        return (url, host);
    }

    /// <summary>
    /// 添加单个源接口地址。地址已存在时不重复添加，只按本次选择更新它的成人标记
    /// （地址属于主订阅里的源时，在额外源里写一份同 key 的记录覆盖它的标记）。
    /// </summary>
    public static Result AddSingle(
        IReadOnlyList<VideoSource> extraSources,
        IReadOnlyList<VideoSource> subscriptionSources,
        string? url,
        bool adult)
    {
        var (api, host) = ParseUrl(url);
        var key = ManualKeyPrefix + Regex.Replace(host, "[^a-z0-9.-]", "_");
        return Upsert(extraSources, subscriptionSources, [new VideoSource(key, host, api, Adult: adult)], keepExistingMark: false);
    }

    /// <summary>
    /// 添加一个订阅链接下发的全部源。adult 为 true 时全部标记为成人源；订阅里自带的成人标记始终保留，
    /// 已存在的源原有的成人标记也不会被这次导入取消。
    /// </summary>
    public static Result AddSubscription(
        IReadOnlyList<VideoSource> extraSources,
        IReadOnlyList<VideoSource> subscriptionSources,
        IEnumerable<VideoSource> incoming,
        bool adult) =>
        Upsert(
            extraSources,
            subscriptionSources,
            incoming.Select(source => adult ? source with { Adult = true } : source),
            keepExistingMark: true) with { FromSubscription = true };

    /// <summary>
    /// 导入同步链接里的全部源（对齐 RN 端 mergeSyncedVideoSources），不产生重复：
    /// 接口地址已存在于另一个 key 下的跳过（只补上成人标记）；key 已存在的用链接里的内容覆盖；其余新增。
    /// 成人标记只增不减：链接里标了成人的照样标记，本机已标记的不会被这次导入取消；adult 为 true 时全部标记为成人源。
    /// 要改动的源属于主订阅时，在额外源里写一份同 key 的记录覆盖它。
    /// </summary>
    public static Result ImportSync(
        IReadOnlyList<VideoSource> extraSources,
        IReadOnlyList<VideoSource> subscriptionSources,
        IEnumerable<VideoSource> incoming,
        bool adult)
    {
        var extras = extraSources.ToList();
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        // 写入额外源：已有同 key 的额外源就地替换，否则追加（合并时覆盖同 key 的订阅源）
        void Put(VideoSource source)
        {
            var index = extras.FindIndex(existing => SameKey(existing.Key, source.Key));
            if (index >= 0)
            {
                extras[index] = source;
            }
            else
            {
                extras.Add(source);
            }
        }

        foreach (var item in incoming)
        {
            if (string.IsNullOrWhiteSpace(item.Api) || string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(item.Key) ? ExtraVideoSourcesStore.SlugKey(item.Name) : item.Key.Trim();
            var markAdult = adult || item.Adult;
            // 当前实际生效的源：同 key 时额外源覆盖订阅源
            var effective = VideoSourceMerger.Merge(subscriptionSources, extras);

            var sameApi = effective.FirstOrDefault(existing => SameApi(existing, item) && !SameKey(existing.Key, key));
            if (sameApi is not null)
            {
                if (markAdult && !sameApi.Adult)
                {
                    Put(sameApi with { Adult = true });
                    updated++;
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var current = effective.FirstOrDefault(existing => SameKey(existing.Key, key));
            var merged = item with { Key = current?.Key ?? key, Adult = markAdult || current?.Adult == true };
            if (current is null)
            {
                extras.Add(merged);
                added++;
            }
            else if (SameContent(current, merged))
            {
                unchanged++;
            }
            else
            {
                Put(merged);
                updated++;
            }
        }

        return new Result(extras, added, updated, FromSync: true, Unchanged: unchanged);
    }

    public static IReadOnlyList<VideoSource> Remove(IReadOnlyList<VideoSource> extraSources, string key) =>
        extraSources.Where(source => !SameKey(source.Key, key)).ToList();

    private static Result Upsert(
        IReadOnlyList<VideoSource> extraSources,
        IReadOnlyList<VideoSource> subscriptionSources,
        IEnumerable<VideoSource> incoming,
        bool keepExistingMark)
    {
        var extras = extraSources.ToList();
        var added = 0;
        var updated = 0;
        foreach (var source in incoming)
        {
            if (string.IsNullOrWhiteSpace(source.Api) || string.IsNullOrWhiteSpace(source.Name))
            {
                continue;
            }

            var index = extras.FindIndex(existing => SameApi(existing, source));
            if (index >= 0)
            {
                var adult = source.Adult || (keepExistingMark && extras[index].Adult);
                if (extras[index].Adult != adult)
                {
                    extras[index] = extras[index] with { Adult = adult };
                    updated++;
                }

                continue;
            }

            // 被同 key 额外源覆盖的订阅源本来就不生效，不算“已存在”。
            var subscribed = subscriptionSources.FirstOrDefault(existing =>
                SameApi(existing, source) && !extras.Any(extra => SameKey(extra.Key, existing.Key)));
            if (subscribed is not null)
            {
                var adult = source.Adult || (keepExistingMark && subscribed.Adult);
                if (subscribed.Adult != adult)
                {
                    // 同 key 的额外源在合并时覆盖订阅源
                    extras.Add(subscribed with { Adult = adult });
                    updated++;
                }

                continue;
            }

            var baseKey = string.IsNullOrWhiteSpace(source.Key) ? ExtraVideoSourcesStore.SlugKey(source.Name) : source.Key.Trim();
            var key = baseKey;
            for (var n = 2; extras.Concat(subscriptionSources).Any(existing => SameKey(existing.Key, key)); n++)
            {
                key = $"{baseKey}_{n}";
            }

            extras.Add(source with { Key = key });
            added++;
        }

        return new Result(extras, added, updated);
    }

    private static bool SameKey(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameApi(VideoSource left, VideoSource right) =>
        string.Equals(left.Api.Trim().TrimEnd('/'), right.Api.Trim().TrimEnd('/'), StringComparison.Ordinal);

    // record 自带的相等比较对 Options 只比引用，这里按内容比较
    private static bool SameContent(VideoSource left, VideoSource right) =>
        string.Equals(left.Name.Trim(), right.Name.Trim(), StringComparison.Ordinal)
        && SameApi(left, right)
        && string.Equals(left.Detail?.Trim().TrimEnd('/') ?? "", right.Detail?.Trim().TrimEnd('/') ?? "", StringComparison.Ordinal)
        && string.Equals(VideoSourceProtocols.Normalize(left.Protocol), VideoSourceProtocols.Normalize(right.Protocol), StringComparison.Ordinal)
        && string.Equals(left.Dialect?.Trim() ?? "", right.Dialect?.Trim() ?? "", StringComparison.Ordinal)
        && left.Adult == right.Adult
        && (left.Options?.Count ?? 0) == (right.Options?.Count ?? 0)
        && (left.Options ?? new Dictionary<string, string>()).All(option =>
            right.Options is not null
            && right.Options.TryGetValue(option.Key, out var value)
            && string.Equals(value, option.Value, StringComparison.Ordinal));
}

/// <summary>生成给 Claude Code / Codex 的视频源任务提示词（仅 AppleCMS / 类 CMS）。</summary>
public static class AiImportPromptBuilder
{
    /// <param name="docsDirectory">发布包内 docs 目录（含 ORION_SOURCE_GUIDE.md）；可空。</param>
    public static string Build(
        string pageUrl,
        string extraSourcesPath,
        string? notes = null,
        string? docsDirectory = null,
        string? settingsPath = null)
    {
        var taskBlock = $$"""
            ## 1. 当前任务：立即解析指定网址

            目标地址（必须原样使用）：
            {{pageUrl.Trim()}}

            读完项目说明后，立即访问并分析上述目标地址。此提示词已经给出网址：
            - 不得等待用户再次输入网址或确认，不得只复述任务、只给步骤或只给建议。
            - 先抓取该网址返回的 HTML/JSON/XML，再检查页面引用的 JavaScript、网络接口和常见 CMS 路径。
            - 从目标站点中定位播放器能直接使用的 AppleCMS/MacCMS API。
            - 先从列表/搜索结果取得真实视频 ID，再用详情接口补拉剧集；搜索结果只有元数据时不能判定源可用。
            - 找到候选接口后按下方三步验证标准实测；只有得到至少一个可解析的 HTTP/HTTPS 播放地址才写入配置。
            """;

        return BuildCore(
            taskBlock,
            extraSourcesPath,
            notes,
            docsDirectory,
            settingsPath);
    }

    /// <summary>生成无需用户提供网址、由 CLI 主动搜索并验证可用源的提示词。</summary>
    public static string BuildDiscovery(
        string extraSourcesPath,
        string? notes = null,
        string? docsDirectory = null,
        string? settingsPath = null)
    {
        const string taskBlock = """
            ## 1. 当前任务：AI 主动寻源

            本任务无需用户提供网址。读完项目说明后，立即主动搜索候选
            AppleCMS/MacCMS 视频源，并解析、验证播放器能否使用：
            - 不得要求用户再提供网址，不得只列搜索方法、候选名单或未经验证的地址。
            - 使用当前 CLI 配置允许的联网和文件工具寻找候选 API，优先定位 provide/vod、MacCMS JSON/XML 接口。
            - 逐个执行下方三步验证；只写入本次会话中实际验证通过且含可解析播放字段的源。
            - 如果没有找到合格源，明确汇报搜索与验证结果，不得伪造配置。
            """;

        return BuildCore(
            taskBlock,
            extraSourcesPath,
            notes,
            docsDirectory,
            settingsPath);
    }

    private static string BuildCore(
        string taskBlock,
        string extraSourcesPath,
        string? notes,
        string? docsDirectory,
        string? settingsPath)
    {
        var dataDir = Path.GetDirectoryName(extraSourcesPath) ?? DesktopPaths.DataDirectory;
        docsDirectory = string.IsNullOrWhiteSpace(docsDirectory)
            ? ""
            : Path.GetFullPath(docsDirectory.Trim());

        var noteBlock = string.IsNullOrWhiteSpace(notes)
            ? ""
            : $$"""

               用户备注：
               {{notes.Trim()}}
               """;

        var docsBlock = string.IsNullOrWhiteSpace(docsDirectory)
            ? """
              第一项操作必须使用文件读取工具查找并完整读取 ORION_SOURCE_GUIDE.md，
              优先检查当前工作目录下的 docs/ORION_SOURCE_GUIDE.md。若确实找不到指南，第一条进度消息必须明确报告缺失，
              但仍要遵守本提示词中的格式与验证约束。
              """
            : $$"""
              第一项操作必须使用文件读取工具完整读取以下本机文件，不得只依赖本提示词中的摘要：
              {{Path.Combine(docsDirectory, "ORION_SOURCE_GUIDE.md")}}

              完成读取后，第一条进度消息必须明确写出“已完整阅读 ORION_SOURCE_GUIDE.md”，
              然后立即执行当前任务；在读取完成前不要分析目标或修改配置。
              """;

        return $$"""
            你是 OpaiTV **桌面端**视频源适配助手。桌面端**只支持** AppleCMS / 类 CMS 源。
            TVBox csp/jar/drpy 配置没有桌面端执行器；桌面已移除 TVBox 代理。

            ## 0. 强制开始步骤：先完整读取项目说明
            {{docsBlock}}

            {{taskBlock}}
            {{noteBlock}}

            用户数据目录（可写）：
            {{dataDir}}

            ## 2. 三步接口验证标准

            每个候选源都必须在当前会话中实际完成以下步骤：
            1. 结构：请求分类接口（如 `?ac=list`）和分页/搜索接口，确认 HTTP 成功及合法 JSON/XML；分类可只返回 `class`/`ty`，但分页或搜索必须给出真实视频节点和真实 ID。
            2. 数据：用上述真实 ID 请求 `?ac=detail&ids=...`（或该方言等价请求），确认至少一条有效影视记录；搜索结果没有播放字段时必须执行此补拉。
            3. 播放：确认 `vod_play_url` 或 XML 等价字段非空，并按 `$$$`、`#`、`$` 规则解析出至少一个绝对或可解析的 HTTP/HTTPS 地址；空值、占位值、`javascript:`、`file:`、`magnet:` 均不通过。
            4. 记录最终 API、协议/方言、每个验证请求、HTTP/结构结果和播放地址结果；只配置全部通过的候选源。

            ## 3. 唯一写入路径：AppleCMS / 类 CMS -> extra_sources.json

            适用：页面或接口已是 / 能验证出：
            - api.php/provide/vod 或同等 JSON list/class
            - MacCMS JSON 方言 / XML(at=xml)

            写入文件（UTF-8，必须）：
            {{extraSourcesPath}}

            格式（推荐）：
            {
              "api_site": {
                "source_key": {
                  "name": "显示名称",
                  "api": "https://example.com/api.php/provide/vod",
                  "detail": "可选",
                  "protocol": "applecms",
                   "dialect": "可选 search_ac_list|videolist",
                   "options": {
                     "searchAc": "list",
                     "detailAc": "detail",
                     "pageAc": "videolist",
                     "categoryAc": "list"
                   }
                }
              }
            }

            protocol: applecms | maccms_json | maccms_xml
            写入时解析现有 JSON 并合并保留已有源；同 key 可在验证后更新。
            使用 UTF-8 缩进 JSON，先写同目录临时文件再替换，并在写入后重新解析确认文件完整。

            ## 4. 无法适配时
            目标只有桌面端无法执行的 TVBox csp/jar/drpy 等配置时，说明原因，不要伪造源。

            ## 5. 完成条件
            1. 已完整读取 ORION_SOURCE_GUIDE.md。
            2. 已执行当前任务所要求的抓取或搜索，而不是停留在计划阶段。
            3. 只把三步验证全部通过的源合并写入 extra_sources.json。
            4. 用中文汇报：指南读取情况、任务/搜索范围、每个候选的请求与结果、写入或跳过的 key 和原因。
            5. 提醒用户回到 OpaiTV 点击“立即刷新源”。

            ## 6. 桌面端兼容性说明
            - csp_*/jar/drpy 不是桌面端可执行的 provide/vod 接口
            - extra_sources.json 按 key 合并保留已有数据
            - tvbox 配置和旧 TVBox 代理已不再使用

            现在开始执行：第一项操作是完整读取 ORION_SOURCE_GUIDE.md，不要等待用户补充指令。
            """;
    }
}
