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
