using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>一条搜索记录：搜过的词、次数、最近一次搜索时间（Unix 毫秒）。</summary>
public sealed record SearchHistoryEntry(string Query, int Count, long LastUsedAt);

/// <summary>标签云里的一个词：Weight 为 0~1 的相对权重，FontSize 为建议字号。</summary>
public sealed record SearchTag(string Query, int Count, double Weight, double FontSize);

/// <summary>
/// 搜索记录（只存本机 %AppData%/OrionTV.Desktop/search_history.json，不上传）。
/// 支持单条删除、清空；界面按权重渲染成标签云，点一下即可重搜。
/// </summary>
public sealed class SearchHistoryStore
{
    public const int MaxEntries = 30;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _lock = new();
    private readonly string _filePath;

    public SearchHistoryStore() : this(DesktopPaths.SearchHistoryFile)
    {
    }

    public SearchHistoryStore(string filePath) => _filePath = filePath;

    /// <summary>全部记录，最近搜索的在前。</summary>
    public IReadOnlyList<SearchHistoryEntry> GetAll()
    {
        lock (_lock)
        {
            return Read();
        }
    }

    /// <summary>记一次搜索：同一个词（忽略首尾空白与大小写）累加次数并刷新时间，超出上限丢弃最旧的。</summary>
    public void Record(string? query, DateTimeOffset? now = null)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
        {
            return;
        }

        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        lock (_lock)
        {
            var entries = Read().ToList();
            var index = entries.FindIndex(entry => SameQuery(entry.Query, text));
            var count = 1;
            if (index >= 0)
            {
                count = entries[index].Count + 1;
                entries.RemoveAt(index);
            }

            entries.Insert(0, new SearchHistoryEntry(text, count, timestamp));
            Write(entries.OrderByDescending(entry => entry.LastUsedAt).Take(MaxEntries).ToList());
        }
    }

    /// <summary>删除单条记录；返回是否确实删掉了。</summary>
    public bool Remove(string? query)
    {
        var text = (query ?? "").Trim();
        lock (_lock)
        {
            var entries = Read().ToList();
            var removed = entries.RemoveAll(entry => SameQuery(entry.Query, text)) > 0;
            if (removed)
            {
                Write(entries);
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Write([]);
        }
    }

    /// <summary>
    /// 标签云权重：搜索次数（对数压缩，占 70%）+ 新近度（按时间排名，占 30%）。
    /// 返回顺序与传入顺序一致（最近的在前），字号在 [minFontSize, maxFontSize] 内线性取值。
    /// </summary>
    public static IReadOnlyList<SearchTag> ToTagCloud(
        IReadOnlyList<SearchHistoryEntry> entries,
        double minFontSize = 12,
        double maxFontSize = 20)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var maxLog = entries.Max(entry => Math.Log(1 + Math.Max(1, entry.Count)));
        // 新近度按时间排名（0 = 最近）。按下标算，传入重复项也不会出错。
        var order = Enumerable.Range(0, entries.Count)
            .OrderByDescending(index => entries[index].LastUsedAt)
            .ToList();
        var rankOf = new int[entries.Count];
        for (var rank = 0; rank < order.Count; rank++)
        {
            rankOf[order[rank]] = rank;
        }

        return entries.Select((entry, index) =>
        {
            var frequency = maxLog <= 0 ? 0 : Math.Log(1 + Math.Max(1, entry.Count)) / maxLog;
            var recency = entries.Count == 1 ? 1 : 1 - (double)rankOf[index] / (entries.Count - 1);
            var weight = Math.Clamp(frequency * 0.7 + recency * 0.3, 0, 1);
            var fontSize = Math.Round(minFontSize + (maxFontSize - minFontSize) * weight, 1);
            return new SearchTag(entry.Query, entry.Count, weight, fontSize);
        }).ToList();
    }

    private static bool SameQuery(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private List<SearchHistoryEntry> Read()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<SearchHistoryEntry>>(File.ReadAllText(_filePath), Options) ?? [];
            return entries
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Query))
                .OrderByDescending(entry => entry.LastUsedAt)
                .ToList();
        }
        catch
        {
            // 记录文件损坏不影响搜索，按空记录处理。
            return [];
        }
    }

    private void Write(IReadOnlyList<SearchHistoryEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再原子替换，与 SettingsStore / LocalStore 一致。
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, Options));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            // 写不进去只是丢一条搜索记录，不能影响搜索本身。
        }
    }
}
