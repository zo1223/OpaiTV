using System.Text;
using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 成人内容(18+)过滤（对齐 RN 端 services/api.ts）：开关默认关闭；关闭时标记为 adult 的源
/// 对所有取数入口不可见，收藏、观看历史等按源 key 保存的数据也据此过滤（数据本身不删除）。
/// </summary>
public static class AdultContentFilter
{
    /// <summary>所有取数入口（源列表、首页、搜索、详情）只能用这份列表。</summary>
    public static IReadOnlyList<VideoSource> VisibleSources(IReadOnlyList<VideoSource> sources, bool showAdultContent) =>
        showAdultContent || !sources.Any(source => source.Adult)
            ? sources
            : sources.Where(source => !source.Adult).ToList();

    /// <summary>该源是成人源且成人内容开关关闭。收藏、观看记录等按源 key 保存的数据用它过滤。</summary>
    public static bool IsHidden(IEnumerable<VideoSource> sources, bool showAdultContent, string? sourceKey) =>
        !showAdultContent
        && !string.IsNullOrEmpty(sourceKey)
        && sources.Any(source => source.Adult && string.Equals(source.Key, sourceKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 成人内容开关关闭，且给定的分类名 / 题材标签（type_name、vod_class）里有成人分类。
    /// 搜索、首页、详情、收藏、接着看等所有内容入口的按分类过滤都走这里（词表见 <see cref="AdultCategories"/>）。
    /// </summary>
    public static bool IsCategoryHidden(bool showAdultContent, params string?[] categoryNames) =>
        !showAdultContent && AdultCategories.IsAdult(categoryNames);

    /// <summary>条目保存到收藏 / 观看记录时一并保存的分类信息（分类名 + 题材标签），供之后按分类过滤。</summary>
    public static string? CategoryOf(SearchResult? result) => JoinCategory(result?.TypeName, result?.Class);

    public static string? JoinCategory(params string?[] categoryNames)
    {
        var parts = categoryNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).ToList();
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    /// <summary>
    /// 当前被隐藏的成人源的签名。按内容比较：只有开关切换，或成人源被添加/删除/改标记时才变化，
    /// 已打开的页面据此丢弃按旧状态取到的内容。
    /// </summary>
    public static string HiddenSignature(IEnumerable<VideoSource> sources, bool showAdultContent) =>
        showAdultContent
            ? ""
            : string.Join(
                '\u001f',
                sources
                    .Where(source => source.Adult)
                    .Select(source => source.Key)
                    .OrderBy(key => key, StringComparer.Ordinal));

    /// <summary>
    /// 读取源配置里的成人标记（adult；is_adult 是同类 api_site 配置里的同义字段）。
    /// 除布尔 true 外，也接受手写配置里常见的 1 / "1" / "true"：宁可多认也不能漏认
    /// （漏认 = 成人源在开关关闭时照常显示）；"false"、0 等一律不算。
    /// </summary>
    internal static bool ReadFlag(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if ((!item.TryGetProperty("adult", out var value) || value.ValueKind == JsonValueKind.Null)
            && !item.TryGetProperty("is_adult", out value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetDouble(out var number) && number == 1,
            JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() is "1" or "true",
            _ => false,
        };
    }
}

/// <summary>
/// 成人内容(18+)按分类过滤 —— 桌面端唯一实现。手机端对应实现：services/adultCategoryFilter.ts，
/// 两边的词表与规则必须保持一致。
/// 只看“分类”：源站分类名（type_name）与题材标签（vod_class），绝不看片名——
/// 片名里带“伦理”“福利”的正常影片（《伦理学讲座》《福利院的孩子》）不会被拦。
/// </summary>
public static class AdultCategories
{
    /// <summary>
    /// 成人分类关键词（已归一化：NFKC、小写、简体）。
    /// 含中文的词按子串匹配（匹配前去掉空白与标点）；纯英文/数字的词要求两侧不是英文字母或数字，
    /// 所以 av 不会命中 avatar，sex 不会命中 essex。
    /// </summary>
    public static readonly IReadOnlyList<string> Keywords =
    [
        // CineFlow 11 个内置源分类表里实际出现的：伦理片/伦理/港台三级/韩国伦理/西方伦理/日本伦理/
        // 里番动漫/福利/写真热舞/擦边短剧/擦边剧/两性课堂
        "伦理", "三级", "里番", "福利", "写真", "热舞", "擦边", "两性",
        // 常见变体与同类叫法
        "理论片", "肉番", "情色", "色情", "成人", "限制级", "大尺度", "18禁", "十八禁", "无码", "有码",
        "女优", "乱伦", "人妻", "熟女", "巨乳", "丝袜", "偷拍", "性爱", "做爱", "自慰", "调教", "痴汉", "淫",
        // 英文 / 数字
        "adult", "adults", "porn", "porno", "hentai", "erotic", "erotica", "sex", "xxx", "nsfw", "jav", "av",
        "18+", "r18", "r-18", "uncensored", "lunli", "lunlipian",
    ];

    /// <summary>含成人关键词但属于正常分类的固定说法：匹配前先从分类名里剔除。</summary>
    public static readonly IReadOnlyList<string> BenignPhrases =
    [
        "家庭伦理", "社会伦理", "伦理学", "伦理道德", "医学伦理", "科技伦理", "生命伦理",
        "福利院", "社会福利", "福利彩票", "成人教育", "成人礼", "成人高考", "三级跳",
    ];

    /// <summary>繁体 -> 简体（只收词表用得到的字）。</summary>
    private static readonly Dictionary<char, char> TraditionalToSimplified = new()
    {
        ['倫'] = '伦', ['論'] = '论', ['級'] = '级', ['裏'] = '里', ['裡'] = '里', ['寫'] = '写', ['熱'] = '热',
        ['邊'] = '边', ['兩'] = '两', ['課'] = '课', ['無'] = '无', ['碼'] = '码', ['優'] = '优', ['亂'] = '乱',
        ['愛'] = '爱', ['絲'] = '丝', ['襪'] = '袜', ['調'] = '调', ['癡'] = '痴', ['漢'] = '汉', ['學'] = '学',
        ['禮'] = '礼', ['會'] = '会',
    };

    // 一个字段里可能是多个标签（"剧情,伦理" / "剧情/爱情"），逐个标签判断
    private static readonly char[] TagSeparators = [',', '，', '、', '/', '|', ';', '；', '\\'];

    /// <summary>
    /// 给定的分类名 / 题材标签里是否有成人分类。可一次传多个字段（type_name、vod_class）。空值不算成人分类。
    /// </summary>
    public static bool IsAdult(params string?[] categoryNames) =>
        categoryNames.Any(name =>
            !string.IsNullOrWhiteSpace(name)
            && Normalize(name).Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries).Any(IsAdultTag));

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            builder.Append(TraditionalToSimplified.TryGetValue(c, out var simplified) ? simplified : c);
        }

        return builder.ToString();
    }

    private static bool IsAdultTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        // 去掉空白与标点后再做中文子串匹配（"伦 理 片" "伦理·片"）；保留 +（18+）
        var builder = new StringBuilder(tag.Length);
        foreach (var c in tag)
        {
            if (char.IsLetterOrDigit(c) || c == '+')
            {
                builder.Append(c);
            }
        }

        var compact = builder.ToString();
        foreach (var phrase in BenignPhrases)
        {
            compact = compact.Replace(phrase, " ", StringComparison.Ordinal);
        }

        return Keywords.Any(keyword => ContainsKeyword(keyword.Any(c => c > 0x7f) ? compact : tag, keyword));
    }

    // 关键词首/尾是英文字母或数字时，要求命中位置的前/后一个字符不是英文字母或数字
    private static bool ContainsKeyword(string text, string keyword)
    {
        var guardStart = IsAsciiAlphaNumeric(keyword[0]);
        var guardEnd = IsAsciiAlphaNumeric(keyword[^1]);
        var from = 0;
        while (from <= text.Length - keyword.Length)
        {
            var index = text.IndexOf(keyword, from, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + keyword.Length;
            if ((!guardStart || index == 0 || !IsAsciiAlphaNumeric(text[index - 1]))
                && (!guardEnd || end >= text.Length || !IsAsciiAlphaNumeric(text[end])))
            {
                return true;
            }

            from = index + 1;
        }

        return false;
    }

    private static bool IsAsciiAlphaNumeric(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
