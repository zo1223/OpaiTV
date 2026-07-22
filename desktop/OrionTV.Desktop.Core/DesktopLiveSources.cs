namespace OrionTV.Desktop.Core;

public sealed record DesktopLiveSource(string Name, string Url);

public sealed record ConfiguredLiveSource(string Name, string Url, bool IsPrimary);

/// <summary>桌面端多直播源配置、兼容解析与频道合并规则。</summary>
public static class DesktopLiveSourceSettings
{
    public const string PrimarySourceName = "默认直播源";

    public static IReadOnlyList<DesktopLiveSource> Normalize(
        IEnumerable<DesktopLiveSource>? sources,
        string? primaryUrl = null)
    {
        if (sources is null)
        {
            return [];
        }

        var normalized = new List<DesktopLiveSource>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primaryUrl))
        {
            seenUrls.Add(primaryUrl.Trim());
        }

        foreach (var source in sources)
        {
            var url = source.Url?.Trim() ?? "";
            if (!IsSupportedUrl(url) || !seenUrls.Add(url))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(source.Name)
                ? $"直播源 {normalized.Count + 1}"
                : source.Name.Trim();
            normalized.Add(new DesktopLiveSource(name, url));
        }

        return normalized;
    }

    public static IReadOnlyList<ConfiguredLiveSource> Resolve(DesktopSettings settings)
    {
        var result = new List<ConfiguredLiveSource>();
        var primaryUrl = settings.M3uUrl?.Trim() ?? "";
        if (IsSupportedUrl(primaryUrl))
        {
            result.Add(new ConfiguredLiveSource(PrimarySourceName, primaryUrl, IsPrimary: true));
        }

        result.AddRange(Normalize(settings.AdditionalLiveSources, primaryUrl)
            .Select(source => new ConfiguredLiveSource(source.Name, source.Url, IsPrimary: false)));
        return result;
    }

    public static bool IsSupportedUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<LiveChannel> MergeChannels(
        IEnumerable<(ConfiguredLiveSource Source, IReadOnlyList<LiveChannel> Channels)> sourceChannels)
    {
        var merged = new List<LiveChannel>();
        var seenStreamUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (source, channels) in sourceChannels)
        {
            foreach (var channel in channels)
            {
                if (!seenStreamUrls.Add(channel.Url))
                {
                    continue;
                }

                merged.Add(source.IsPrimary
                    ? channel
                    : channel with
                    {
                        Id = $"{source.Url}::{channel.Id}",
                        Group = $"{source.Name} · {channel.Group}"
                    });
            }
        }

        return merged;
    }
}
