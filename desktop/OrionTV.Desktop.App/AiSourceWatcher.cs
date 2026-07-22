using System.IO;
using System.Windows.Threading;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>
/// 监视 AI 写入的 extra_sources.json / settings.json，防抖后通知应用重载源。
/// </summary>
public sealed class AiSourceWatcher : IDisposable
{
    public enum ChangeKind
    {
        ExtraSources,
        Settings,
    }

    private readonly Action<ChangeKind> _onChanged;
    private readonly Dispatcher _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _debounce;
    private ChangeKind _pendingKind = ChangeKind.ExtraSources;
    private bool _disposed;

    public AiSourceWatcher(Action<ChangeKind> onChanged, Dispatcher? dispatcher = null)
    {
        _onChanged = onChanged;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        ExtraVideoSourcesStore.EnsureFileExists();
        DesktopPaths.EnsureDirectory();

        DisposeWatchers();
        WatchFile(ExtraVideoSourcesStore.FilePath, ChangeKind.ExtraSources);
        WatchFile(DesktopPaths.SettingsFile, ChangeKind.Settings);
    }

    private void WatchFile(string path, ChangeKind kind)
    {
        var dir = Path.GetDirectoryName(path);
        var file = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => ScheduleNotify(kind);
        watcher.Created += (_, _) => ScheduleNotify(kind);
        watcher.Renamed += (_, _) => ScheduleNotify(kind);
        _watchers.Add(watcher);
    }

    private void ScheduleNotify(ChangeKind kind)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _pendingKind = MergeKind(_pendingKind, kind);

            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _debounce.Stop();
            _debounce.Tick -= Debounce_Tick;
            _debounce.Tick += Debounce_Tick;
            _debounce.Start();
        });
    }

    private static ChangeKind MergeKind(ChangeKind current, ChangeKind incoming)
    {
        if (current == ChangeKind.Settings || incoming == ChangeKind.Settings)
        {
            return ChangeKind.Settings;
        }

        return ChangeKind.ExtraSources;
    }

    private void Debounce_Tick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        if (_disposed)
        {
            return;
        }

        var kind = _pendingKind;
        _pendingKind = ChangeKind.ExtraSources;
        try
        {
            _onChanged(kind);
        }
        catch
        {
            // 合并失败不打断 UI
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_debounce is not null)
        {
            _debounce.Stop();
            _debounce.Tick -= Debounce_Tick;
            _debounce = null;
        }

        DisposeWatchers();
    }
}
