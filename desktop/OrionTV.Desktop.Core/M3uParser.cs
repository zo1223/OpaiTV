namespace OrionTV.Desktop.Core;

/// <summary>移植自安卓 services/m3u.ts 的 M3U 直播源解析。</summary>
public sealed class M3uParser(HttpClient httpClient)
{
    public static IReadOnlyList<LiveChannel> Parse(string m3uText)
    {
        var channels = new List<LiveChannel>();
        string? name = null;
        string? logo = null;
        string? group = null;
        var hasInfo = false;

        foreach (var rawLine in m3uText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                hasInfo = true;
                name = null;
                logo = null;
                group = null;

                var commaIndex = FindNameSeparator(line);
                if (commaIndex != -1)
                {
                    name = line[(commaIndex + 1)..].Trim();
                    var attributes = line.Substring(8, commaIndex - 8);
                    logo = MatchAttribute(attributes, "tvg-logo");
                    group = MatchAttribute(attributes, "group-title");
                }
                else
                {
                    name = line[8..].Trim();
                }
            }
            else if (hasInfo && line.Length > 0 && !line.StartsWith('#') && line.Contains("://"))
            {
                channels.Add(new LiveChannel(
                    Id: line,
                    Name: string.IsNullOrWhiteSpace(name) ? "Unknown" : name!,
                    Url: line,
                    Group: string.IsNullOrWhiteSpace(group) ? "Default" : group!,
                    Logo: string.IsNullOrWhiteSpace(logo) ? null : logo));
                hasInfo = false;
            }
        }

        return channels;
    }

    public async Task<IReadOnlyList<LiveChannel>> FetchAndParseAsync(string m3uUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(m3uUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(text);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// EXTINF 的属性区与频道名以「第一个不在引号内的逗号」分隔(与 services/m3u.ts 一致)。
    /// 用 LastIndexOf 会把带逗号的频道名(如 "CCTV-13, News HD")截断成最后一段。
    /// </summary>
    private static int FindNameSeparator(string line)
    {
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (character == ',' && !inQuotes)
            {
                return index;
            }
        }

        // 引号不成对时退回旧行为,避免整行被当作属性。
        return inQuotes ? line.LastIndexOf(',') : -1;
    }

    private static string? MatchAttribute(string attributes, string key)
    {
        var marker = key + "=\"";
        var start = attributes.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = attributes.IndexOf('"', start);
        return end < 0 ? null : attributes[start..end];
    }
}
