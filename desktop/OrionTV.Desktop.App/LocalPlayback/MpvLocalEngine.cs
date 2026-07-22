using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace OrionTV.Desktop.App.LocalPlayback;

/// <summary>
/// 本地专用播放引擎：嵌入式便携 mpv（FFmpeg demux + d3d11va 硬解）。
/// 通过 --wid 画到宿主 HWND，IPC 控制 seek/音量/暂停。不依赖 PATH（优先应用目录 mpv\mpv.exe）。
/// </summary>
public sealed class MpvLocalEngine : IDisposable
{
    private readonly IntPtr _wid;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly string _pipeName;
    private Process? _process;
    private NamedPipeClientStream? _ipc;
    private StreamWriter? _ipcWriter;
    private StreamReader? _ipcReader;
    private CancellationTokenSource? _observeCts;
    private Task? _observeTask;
    private int _generation;
    private bool _disposed;
    private PlaybackState _state = PlaybackState.Idle;
    private long _lengthMs;
    private long _timeMs;
    private int _volume = 100;
    private bool _mute;
    private bool _paused;

    public MpvLocalEngine(IntPtr wid, Dispatcher dispatcher)
    {
        if (wid == IntPtr.Zero)
        {
            throw new ArgumentException("Host window handle is required.", nameof(wid));
        }

        _wid = wid;
        _dispatcher = dispatcher;
        _pipeName = "orion-mpv-" + Guid.NewGuid().ToString("N");
    }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<long>? TimeChanged;
    public event EventHandler<long>? LengthChanged;
    public event EventHandler? EndReached;

    public PlaybackState State => _state;
    public long Length => Interlocked.Read(ref _lengthMs);
    public long Time => Interlocked.Read(ref _timeMs);
    public bool IsPlaying => _state == PlaybackState.Playing;

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            _ = SetPropertyAsync("volume", _volume);
        }
    }

    public bool Mute
    {
        get => _mute;
        set
        {
            _mute = value;
            _ = SetPropertyAsync("mute", value);
        }
    }

    /// <summary>
    /// 便携优先：应用目录 mpv\mpv.exe，再 PATH / 常见路径。
    /// </summary>
    public static string? FindMpvExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "mpv", "mpv.exe"),
            Path.Combine(AppContext.BaseDirectory, "mpv.exe"),
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), "mpv.exe"));
        }

        candidates.AddRange(
        [
            @"C:\mpv\mpv.exe",
            @"C:\Program Files\mpv\mpv.exe",
            @"D:\mpv\mpv.exe",
            @"E:\mpv\mpv.exe",
        ]);

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public static bool IsAvailable() => FindMpvExecutable() is not null;

    public async Task SwitchToAsync(string pathOrUrl, long startMs = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var generation = ++_generation;
        SetState(PlaybackState.Opening);

        var localPath = ToLocalPath(pathOrUrl);
        if (localPath is null || !File.Exists(localPath))
        {
            SetState(PlaybackState.Error);
            throw new FileNotFoundException("Local media not found.", pathOrUrl);
        }

        await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);
        if (generation != _generation || _disposed)
        {
            return;
        }

        await SendCommandAsync(["loadfile", localPath, "replace"], cancellationToken).ConfigureAwait(false);
        if (startMs > 0)
        {
            await SendCommandAsync(
                    ["seek", (startMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "absolute+keyframes"],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await SetPropertyAsync("volume", _volume, cancellationToken).ConfigureAwait(false);
        await SetPropertyAsync("mute", _mute, cancellationToken).ConfigureAwait(false);
        await SetPropertyAsync("pause", false, cancellationToken).ConfigureAwait(false);
        _paused = false;
        SetState(PlaybackState.Playing);
    }

    public void TogglePlayPause()
    {
        _paused = !_paused;
        _ = SetPropertyAsync("pause", _paused);
        SetState(_paused ? PlaybackState.Paused : PlaybackState.Playing);
    }

    public void Play()
    {
        _paused = false;
        _ = SetPropertyAsync("pause", false);
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        _paused = true;
        _ = SetPropertyAsync("pause", true);
        SetState(PlaybackState.Paused);
    }

    public bool TrySeek(long ms)
    {
        var targetSec = Math.Max(0, ms) / 1000d;
        // 关键帧快速 seek（本地 TS 更跟手）；不强制软解。
        _ = SendCommandAsync(["seek", targetSec.ToString("0.###", CultureInfo.InvariantCulture), "absolute+keyframes"]);
        Interlocked.Exchange(ref _timeMs, Math.Max(0, ms));
        Post(() => TimeChanged?.Invoke(this, Math.Max(0, ms)));
        return true;
    }

    public void Stop()
    {
        _ = SendCommandAsync(["stop"]);
        Interlocked.Exchange(ref _timeMs, 0);
        SetState(PlaybackState.Idle);
    }

    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_process is { HasExited: false } && _ipc is { IsConnected: true })
            {
                return;
            }
        }

        await RestartProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartProcessAsync(CancellationToken cancellationToken)
    {
        ShutdownProcess_NoLock();

        var mpv = FindMpvExecutable()
            ?? throw new InvalidOperationException("MPV_NOT_FOUND");

        var pipePath = @"\\.\pipe\" + _pipeName;
        // 便携 mpv：硬解 d3d11va；hr-seek=no 关键帧快跳；OSC 固定贴底（airspace）。
        var args =
            $"--wid={_wid.ToInt64()} " +
            "--idle=yes " +
            "--force-window=no " +
            "--keep-open=yes " +
            "--osc=yes --script-opts=osc-layout=bottombar,osc-valign=1 --osd-level=1 --no-border " +
            "--hwdec=d3d11va " +
            "--hwdec-codecs=all " +
            "--vo=gpu " +
            "--gpu-context=d3d11 " +
            "--cache=yes --demuxer-max-bytes=256MiB " +
            "--hr-seek=no " +
            "--input-default-bindings=yes " +
            $"--input-ipc-server={pipePath}";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = mpv,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(mpv) ?? AppContext.BaseDirectory,
            },
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("MPV_START_FAILED");
        }

        lock (_sync)
        {
            _process = process;
        }

        Exception? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                client.ReadMode = PipeTransmissionMode.Byte;
                var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var reader = new StreamReader(client, Encoding.UTF8);
                lock (_sync)
                {
                    _ipc = client;
                    _ipcWriter = writer;
                    _ipcReader = reader;
                }

                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        if (last is not null || _ipc is null)
        {
            ShutdownProcess_NoLock();
            throw new InvalidOperationException("MPV_IPC_CONNECT_FAILED", last);
        }

        await SendCommandAsync(["observe_property", 1, "time-pos"], cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(["observe_property", 2, "duration"], cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(["observe_property", 3, "pause"], cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(["observe_property", 4, "eof-reached"], cancellationToken).ConfigureAwait(false);

        _observeCts = new CancellationTokenSource();
        _observeTask = Task.Run(() => ObserveLoopAsync(_observeCts.Token), CancellationToken.None);
    }

    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            string? line;
            try
            {
                StreamReader? reader;
                lock (_sync)
                {
                    reader = _ipcReader;
                }

                if (reader is null)
                {
                    break;
                }

                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var evt) || evt.GetString() != "property-change")
                {
                    continue;
                }

                var name = root.GetProperty("name").GetString();
                if (!root.TryGetProperty("data", out var data))
                {
                    continue;
                }

                switch (name)
                {
                    case "time-pos" when data.ValueKind == JsonValueKind.Number:
                    {
                        var ms = (long)Math.Round(data.GetDouble() * 1000);
                        Interlocked.Exchange(ref _timeMs, ms);
                        Post(() =>
                        {
                            if (_state is PlaybackState.Opening or PlaybackState.Buffering)
                            {
                                SetState(PlaybackState.Playing);
                            }

                            TimeChanged?.Invoke(this, ms);
                        });
                        break;
                    }
                    case "duration" when data.ValueKind == JsonValueKind.Number:
                    {
                        var ms = (long)Math.Round(data.GetDouble() * 1000);
                        Interlocked.Exchange(ref _lengthMs, ms);
                        Post(() => LengthChanged?.Invoke(this, ms));
                        break;
                    }
                    case "pause" when data.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        _paused = data.GetBoolean();
                        Post(() => SetState(_paused ? PlaybackState.Paused : PlaybackState.Playing));
                        break;
                    case "eof-reached" when data.ValueKind == JsonValueKind.True:
                        Post(() =>
                        {
                            SetState(PlaybackState.Ended);
                            EndReached?.Invoke(this, EventArgs.Empty);
                        });
                        break;
                }
            }
            catch
            {
            }
        }
    }

    private Task SetPropertyAsync(string name, object value, CancellationToken cancellationToken = default) =>
        SendCommandAsync(["set_property", name, value], cancellationToken);

    private async Task SendCommandAsync(object[] command, CancellationToken cancellationToken = default)
    {
        StreamWriter? writer;
        lock (_sync)
        {
            writer = _ipcWriter;
        }

        if (writer is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { command });
        try
        {
            await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string? ToLocalPath(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return null;
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return uri.LocalPath;
            }

            if (uri.Scheme is "http" or "https")
            {
                return null;
            }
        }

        return pathOrUrl;
    }

    private void SetState(PlaybackState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        Post(() => StateChanged?.Invoke(this, state));
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(action);
        }
        catch
        {
        }
    }

    private void ShutdownProcess_NoLock()
    {
        try { _observeCts?.Cancel(); } catch { }
        try { _ipcWriter?.Dispose(); } catch { }
        try { _ipcReader?.Dispose(); } catch { }
        try { _ipc?.Dispose(); } catch { }
        _ipcWriter = null;
        _ipcReader = null;
        _ipc = null;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        lock (_sync)
        {
            try
            {
                _ipcWriter?.WriteLine(JsonSerializer.Serialize(new { command = new object[] { "quit" } }));
            }
            catch
            {
            }

            ShutdownProcess_NoLock();
        }
    }
}
