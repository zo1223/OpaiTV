using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>键值 JSON 文件存储基类（线程安全的简单实现）。</summary>
public abstract class JsonDictionaryStore<T>
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _lock = new();
    private readonly string _filePath;

    protected JsonDictionaryStore(string filePath) => _filePath = filePath;

    protected static string Key(string source, string id) => $"{source}+{id}";

    /// <summary>存储 key（源 key+id）里的源 key 部分。</summary>
    protected static string SourceOf(string key)
    {
        var separator = key.IndexOf('+');
        return separator >= 0 ? key[..separator] : key;
    }

    public Dictionary<string, T> GetAll()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new Dictionary<string, T>();
                }

                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, T>>(json, Options) ?? new Dictionary<string, T>();
            }
            catch
            {
                return new Dictionary<string, T>();
            }
        }
    }

    public void Clear() => Mutate(all => all.Clear());

    protected void Mutate(Action<Dictionary<string, T>> mutation)
    {
        lock (_lock)
        {
            Dictionary<string, T> all;
            if (!File.Exists(_filePath))
            {
                all = new Dictionary<string, T>();
            }
            else
            {
                try
                {
                    all = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(_filePath), Options) ?? new Dictionary<string, T>();
                }
                catch (JsonException)
                {
                    // 内容已损坏(如断电留下的半截 JSON):备份后从空库重建,避免此后每次写入都失败。
                    TryBackupCorruptFile();
                    all = new Dictionary<string, T>();
                }
                catch
                {
                    // 读取瞬时失败(IO/权限):跳过本次变更。绝不能当成空库整库覆写。
                    return;
                }
            }

            mutation(all);
            DesktopPaths.EnsureDirectory();
            WriteAtomically(JsonSerializer.Serialize(all, Options));
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(_filePath, _filePath + ".corrupt.bak", overwrite: true);
        }
        catch
        {
            // 备份失败不阻断重建。
        }
    }

    private void WriteAtomically(string json)
    {
        // 先写临时文件再原子替换,写一半崩溃时不会留下半截 JSON 毁掉整库。
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}

/// <summary>一条收藏及其存储 key 拆出的源 key、id。</summary>
public sealed record FavoriteEntry(string Source, string Id, FavoriteItem Item);

/// <summary>本地收藏存储（对齐安卓 FavoriteManager 的 localstorage 模式）。</summary>
public sealed class FavoriteStore : JsonDictionaryStore<FavoriteItem>
{
    public FavoriteStore() : this(DesktopPaths.FavoritesFile)
    {
    }

    public FavoriteStore(string filePath) : base(filePath)
    {
    }

    /// <summary>
    /// 收藏按「源 key+id」保存。成人内容开关关闭时，被隐藏源的收藏、以及成人分类的收藏不返回
    /// （收藏本身不删除，开关打开后恢复）。
    /// </summary>
    /// <param name="isCategoryHidden">给定收藏保存的分类信息，返回它当前是否因成人分类被隐藏。</param>
    public Dictionary<string, FavoriteItem> GetVisible(
        Func<string, bool> isSourceHidden,
        Func<string?, bool>? isCategoryHidden = null) =>
        GetAll()
            .Where(item => !isSourceHidden(SourceOf(item.Key)) && isCategoryHidden?.Invoke(item.Value.Category) != true)
            .ToDictionary(item => item.Key, item => item.Value);

    /// <summary>
    /// 收藏页与收藏侧滑面板共用的列表：按 <see cref="GetVisible"/> 的同一套成人过滤取出，
    /// 拆出源 key 与 id，按收藏时间从新到旧排列。
    /// </summary>
    public IReadOnlyList<FavoriteEntry> GetVisibleEntries(
        Func<string, bool> isSourceHidden,
        Func<string?, bool>? isCategoryHidden = null) =>
        GetVisible(isSourceHidden, isCategoryHidden)
            .OrderByDescending(item => item.Value.SaveTime)
            .Select(item =>
            {
                var separator = item.Key.IndexOf('+');
                return new FavoriteEntry(
                    SourceOf(item.Key),
                    separator >= 0 ? item.Key[(separator + 1)..] : "",
                    item.Value);
            })
            .ToList();

    public bool IsFavorited(string source, string id) => GetAll().ContainsKey(Key(source, id));

    public void Save(string source, string id, FavoriteItem item) =>
        Mutate(all => all[Key(source, id)] = item with { SaveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    public void Remove(string source, string id) => Mutate(all => all.Remove(Key(source, id)));

    /// <summary>
    /// 旧版本保存的收藏没有分类信息：再次打开该条目时补上，之后才能按成人分类过滤。
    /// 已有分类信息或没有这条收藏时不写盘。
    /// </summary>
    public void BackfillCategory(string source, string id, string? category)
    {
        if (string.IsNullOrWhiteSpace(category)
            || !GetAll().TryGetValue(Key(source, id), out var existing)
            || !string.IsNullOrWhiteSpace(existing.Category))
        {
            return;
        }

        Mutate(all =>
        {
            var key = Key(source, id);
            if (all.TryGetValue(key, out var favorite) && string.IsNullOrWhiteSpace(favorite.Category))
            {
                all[key] = favorite with { Category = category };
            }
        });
    }

    public bool Toggle(string source, string id, FavoriteItem item)
    {
        if (IsFavorited(source, id))
        {
            Remove(source, id);
            return false;
        }

        Save(source, id, item);
        return true;
    }
}

/// <summary>本地观看记录存储（对齐安卓 PlayRecordManager 的 localstorage 模式）。</summary>
public sealed record PlayRecordHistoryEntry(string Source, string Id, PlayRecordItem Record);

public sealed class PlayRecordStore : JsonDictionaryStore<PlayRecordItem>
{
    public PlayRecordStore() : this(DesktopPaths.PlayRecordsFile)
    {
    }

    public PlayRecordStore(string filePath) : base(filePath)
    {
    }

    public PlayRecordItem? Get(string source, string id) =>
        GetAll().TryGetValue(Key(source, id), out var record) ? record : null;

    /// <param name="isSourceHidden">
    /// 观看历史按源 key 保存：成人内容开关关闭时，被隐藏源的记录不进入结果（记录本身不删除）。
    /// 在按影片归并之前过滤，同一部片在其他源的记录仍然保留。
    /// </param>
    /// <param name="isCategoryHidden">
    /// 给定记录保存的分类信息，返回它当前是否因成人分类被隐藏（成人内容开关关闭）。记录本身不删除。
    /// </param>
    /// <param name="isExcluded">
    /// 按（源 key, id）排除个别记录：正在等待撤销的删除（<see cref="PendingPlayRecordDeletion"/>）不进入结果。
    /// </param>
    public IReadOnlyList<PlayRecordHistoryEntry> GetLatestByMedia(
        Func<string, bool>? isSourceHidden = null,
        Func<string?, bool>? isCategoryHidden = null,
        Func<string, string, bool>? isExcluded = null)
    {
        return GetAll()
            .Select(ToHistoryEntry)
            .Where(entry => isSourceHidden?.Invoke(entry.Source) != true
                && isCategoryHidden?.Invoke(entry.Record.Category) != true
                && isExcluded?.Invoke(entry.Source, entry.Id) != true)
            .GroupBy(GetMediaKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var records = group
                    .OrderByDescending(entry => entry.Record.SaveTime)
                    .ToList();
                var latest = records[0];
                if (!string.IsNullOrWhiteSpace(latest.Record.Cover))
                {
                    return latest;
                }

                var cover = records
                    .Select(entry => entry.Record.Cover)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return string.IsNullOrWhiteSpace(cover)
                    ? latest
                    : latest with { Record = latest.Record with { Cover = cover } };
            })
            .OrderByDescending(entry => entry.Record.SaveTime)
            .ToList();
    }

    public void Save(string source, string id, PlayRecordItem record) =>
        Mutate(all => all[Key(source, id)] = record with { SaveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    public void UpdateCover(string source, string id, string cover)
    {
        if (string.IsNullOrWhiteSpace(cover))
        {
            return;
        }

        Mutate(all =>
        {
            var key = Key(source, id);
            if (all.TryGetValue(key, out var record) && string.IsNullOrWhiteSpace(record.Cover))
            {
                all[key] = record with { Cover = cover };
            }
        });
    }

    public void Remove(string source, string id) => Mutate(all => all.Remove(Key(source, id)));

    /// <summary>
    /// 与给定记录归并成同一张“接着看”卡片的全部记录（同一部片在不同源各有一条），含它自己。
    /// 删除卡片时要一起删，否则删掉最新的一条后同片的旧记录会重新冒出来。记录不存在时返回空。
    /// </summary>
    public IReadOnlyList<PlayRecordHistoryEntry> GetMediaGroup(string source, string id)
    {
        var entries = GetAll().Select(ToHistoryEntry).ToList();
        var target = entries.FirstOrDefault(entry =>
            string.Equals(entry.Source, source, StringComparison.Ordinal)
            && string.Equals(entry.Id, id, StringComparison.Ordinal));
        if (target is null)
        {
            return [];
        }

        var mediaKey = GetMediaKey(target);
        return entries
            .Where(entry => string.Equals(GetMediaKey(entry), mediaKey, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Record.SaveTime)
            .ToList();
    }

    /// <summary>
    /// 提交一次可撤销删除：删掉给定快照里的记录；但快照之后又被保存过（SaveTime 更新，
    /// 即待撤销期间用户重新播放了它）的记录保留。返回实际删除的条数。
    /// </summary>
    public int RemoveUnlessUpdated(IEnumerable<PlayRecordHistoryEntry> snapshot)
    {
        var removed = 0;
        Mutate(all =>
        {
            foreach (var entry in snapshot)
            {
                var key = Key(entry.Source, entry.Id);
                if (all.TryGetValue(key, out var current) && current.SaveTime <= entry.Record.SaveTime)
                {
                    all.Remove(key);
                    removed++;
                }
            }
        });
        return removed;
    }

    private static PlayRecordHistoryEntry ToHistoryEntry(KeyValuePair<string, PlayRecordItem> item)
    {
        var separator = item.Key.IndexOf('+');
        var source = separator >= 0 ? item.Key[..separator] : item.Key;
        var id = separator >= 0 ? item.Key[(separator + 1)..] : "";
        return new PlayRecordHistoryEntry(source, id, item.Value);
    }

    private static string GetMediaKey(PlayRecordHistoryEntry entry)
    {
        if (string.Equals(entry.Source, "local", StringComparison.OrdinalIgnoreCase))
        {
            return $"local:{entry.Record.LocalMediaId ?? entry.Id}";
        }

        var title = CatalogTitleMatcher.Normalize(entry.Record.Title);
        if (!string.IsNullOrEmpty(title))
        {
            return $"online:title:{title}";
        }

        var cover = entry.Record.Cover?.Trim();
        return string.IsNullOrEmpty(cover)
            ? $"online:record:{entry.Source}+{entry.Id}"
            : $"online:poster:{cover}";
    }
}
