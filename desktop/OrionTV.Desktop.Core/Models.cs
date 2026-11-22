using System.Text.Json.Serialization;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 点播源。Protocol 缺省 applecms，保持订阅兼容。
/// protocol: applecms | maccms_json | maccms_xml
/// dialect/options: 方言细节（如 searchAc=list、listAc=videolist）。
/// adult: 成人(18+)源。设置里的“成人内容(18+)”开关关闭时，该源及其内容整体隐藏。
/// </summary>
public sealed record VideoSource(
    string Key,
    string Name,
    string Api,
    string? Detail = null,
    string Protocol = VideoSourceProtocols.AppleCms,
    string? Dialect = null,
    IReadOnlyDictionary<string, string>? Options = null,
    bool Adult = false);

public static class VideoSourceProtocols
{
    public const string AppleCms = "applecms";
    public const string MacCmsJson = "maccms_json";
    public const string MacCmsXml = "maccms_xml";

    public static string Normalize(string? value)
    {
        var protocol = (value ?? "").Trim().ToLowerInvariant();
        return protocol switch
        {
            "" or "apple" or "applecms" or "cms" or "json" => AppleCms,
            "maccms" or "maccms_json" or "mac_json" or "cms_json" or "json_cms" => MacCmsJson,
            "maccms_xml" or "mac_xml" or "cms_xml" or "xml" or "at_xml" => MacCmsXml,
            _ => protocol,
        };
    }
}

public sealed record VideoSourceSubscriptionConfig(IReadOnlyList<VideoSource> Sources);

public sealed record PlaybackSource(
    string Key,
    string Name,
    IReadOnlyList<string> Episodes,
    IReadOnlyList<string>? EpisodeLabels = null);

public sealed record SearchResult(
    string Id,
    string Title,
    string Poster,
    IReadOnlyList<string> Episodes,
    string Source,
    string SourceName,
    string Year,
    string? Description = null,
    string? TypeName = null,
    string? Class = null,
    IReadOnlyList<PlaybackSource>? PlaybackSources = null,
    string? PlaybackSourceKey = null,
    IReadOnlyList<string>? EpisodeLabels = null);

public static class EpisodeLabelFormatter
{
    public static string DisplayAt(IReadOnlyList<string>? labels, int index)
    {
        if (labels is not null && index >= 0 && index < labels.Count)
        {
            var label = labels[index]?.Trim();
            if (!string.IsNullOrWhiteSpace(label))
            {
                return label;
            }
        }

        return $"第{index + 1}集";
    }

    public static string CompactAt(IReadOnlyList<string>? labels, int index)
    {
        var label = DisplayAt(labels, index);
        return TryGetNumber(label, out var number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : label;
    }

    public static bool TryGetNumber(string? label, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var value = label.Trim();
        if (value.StartsWith('第'))
        {
            value = value[1..].Trim();
        }

        if (value.EndsWith('集'))
        {
            value = value[..^1].Trim();
        }

        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out number);
    }
}

public sealed record VideoDetail(
    string Id,
    string Title,
    string Poster,
    string Source,
    string SourceName,
    IReadOnlyList<string> Episodes,
    string Year,
    string? Description = null,
    string? TypeName = null,
    string? Area = null,
    string? Director = null,
    string? Actor = null);

public sealed class AppleCmsVodItem
{
    [JsonPropertyName("vod_id")]
    public object? VodId { get; set; }

    [JsonPropertyName("vod_name")]
    public string? VodName { get; set; }

    [JsonPropertyName("vod_pic")]
    public string? VodPic { get; set; }

    [JsonPropertyName("vod_play_url")]
    public string? VodPlayUrl { get; set; }

    [JsonPropertyName("vod_play_from")]
    public string? VodPlayFrom { get; set; }

    [JsonPropertyName("vod_year")]
    public string? VodYear { get; set; }

    [JsonPropertyName("vod_blurb")]
    public string? VodBlurb { get; set; }

    [JsonPropertyName("vod_content")]
    public string? VodContent { get; set; }

    [JsonPropertyName("vod_remarks")]
    public string? VodRemarks { get; set; }

    [JsonPropertyName("type_name")]
    public string? TypeName { get; set; }

    [JsonPropertyName("type_id")]
    public object? TypeId { get; set; }

    [JsonPropertyName("vod_class")]
    public string? VodClass { get; set; }

    [JsonPropertyName("vod_area")]
    public string? VodArea { get; set; }

    [JsonPropertyName("vod_actor")]
    public string? VodActor { get; set; }

    [JsonPropertyName("vod_director")]
    public string? VodDirector { get; set; }
}
