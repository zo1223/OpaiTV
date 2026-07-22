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

/// <summary>本地收藏存储（对齐安卓 FavoriteManager 的 localstorage 模式）。</summary>
public sealed class FavoriteStore() : JsonDictionaryStore<FavoriteItem>(DesktopPaths.FavoritesFile)
{
    public bool IsFavorited(string source, string id) => GetAll().ContainsKey(Key(source, id));

    public void Save(string source, string id, FavoriteItem item) =>
        Mutate(all => all[Key(source, id)] = item with { SaveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    public void Remove(string source, string id) => Mutate(all => all.Remove(Key(source, id)));

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
public sealed class PlayRecordStore() : JsonDictionaryStore<PlayRecordItem>(DesktopPaths.PlayRecordsFile)
{
    public PlayRecordItem? Get(string source, string id) =>
        GetAll().TryGetValue(Key(source, id), out var record) ? record : null;

    public void Save(string source, string id, PlayRecordItem record) =>
        Mutate(all => all[Key(source, id)] = record with { SaveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    public void Remove(string source, string id) => Mutate(all => all.Remove(Key(source, id)));
}
