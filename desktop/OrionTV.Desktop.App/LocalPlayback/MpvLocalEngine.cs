using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace OrionTV.Desktop.App.LocalPlayback;

/// <summary>
/// 本地专用播放引擎：内嵌便携 mpv（FFmpeg demux + d3d11va 硬解）。
/// 通过 --wid 画到宿主 HWND，IPC 控制 seek/音量/暂停。不依赖 PATH（优先应用目录 mpv\mpv.exe）。
/// </summary>
public sealed class MpvLocalEngine : IDisposable
{
    internal const string PreferredSubtitleLanguages = "zh-Hans,zh-CN,chs,sc,zho,chi,zh";
    internal const string PreferredSubtitleFont = "Microsoft YaHei UI";
    private const int SeekCoalesceDelayMilliseconds = 20;
    private const int SeekBurstTimeoutMilliseconds = 1_200;
    private const int SeekObservationTimeoutMilliseconds = 10_000;
    private const long MinimumSeekObservationToleranceMilliseconds = 1_500;
    private const long MinimumObservedSeekMovementMilliseconds = 500;

    private readonly IntPtr _wid;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly object _seekSync = new();
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly SemaphoreSlim _ipcWriteGate = new(1, 1);
    private readonly string _pipeName;
    private Process? _process;
    private NamedPipeClientStream? _ipc;
    private StreamWriter? _ipcWriter;
    private StreamReader? _ipcReader;
    private CancellationTokenSource? _observeCts;
    private Task? _observeTask;
    private CancellationTokenSource? _outputCts;
    private Task? _stdoutDrainTask;
    private Task? _stderrDrainTask;
    private long? _pendingSeekMs;
    private int _pendingSeekGeneration;
    private long? _pendingSeekObservationTargetMs;
    private long _pendingSeekObservationOriginMs;
    private long _pendingSeekObservationDeadline;
    private int _pendingSeekObservationGeneration;
    private long? _seekBurstTargetMs;
    private long _seekBurstOriginMs;
    private long _seekBurstDeadline;
    private int _seekBurstGeneration;
    private bool _seekPumpRunning;
    private int _generation;
    private bool _disposed;
    private PlaybackState _state = PlaybackState.Idle;
    private long _lengthMs;
    private long _timeMs;
    private int _volume = 100;
    private bool _mute;
    private bool _paused;
    private bool _fullscreen;
    private bool _mediaLoaded;
    private bool _hasVideoOutput;
    private long _nextRequestId;
    private long _pendingVideoOutputRequestId;
    private int _pendingVideoOutputGeneration;

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
    public event EventHandler<bool>? FullscreenChanged;
    public event EventHandler<bool>? VideoOutputChanged;

    public PlaybackState State => _state;
    public long Length => Interlocked.Read(ref _lengthMs);
    public long Time => Interlocked.Read(ref _timeMs);
    public bool IsPlaying => _state == PlaybackState.Playing;
    public bool Fullscreen => Volatile.Read(ref _fullscreen);
    public bool HasVideoOutput => Volatile.Read(ref _hasVideoOutput);

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

    public void SetFullscreen(bool enabled)
    {
        Volatile.Write(ref _fullscreen, enabled);
        _ = SetPropertyAsync("fullscreen", enabled);
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
        var requestedFullscreen = Fullscreen;
        var generation = Interlocked.Increment(ref _generation);
        ClearPendingSeeks();
        Volatile.Write(ref _mediaLoaded, false);
        Interlocked.Exchange(ref _pendingVideoOutputRequestId, 0);
        SetVideoOutput(false);
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

        Interlocked.Exchange(ref _timeMs, 0);
        Interlocked.Exchange(ref _lengthMs, 0);
        await SendCommandAsync(BuildLoadCommand(localPath, startMs), cancellationToken).ConfigureAwait(false);

        await SetPropertyAsync("volume", _volume, cancellationToken).ConfigureAwait(false);
        await SetPropertyAsync("mute", _mute, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _fullscreen, requestedFullscreen);
        await SetPropertyAsync("fullscreen", requestedFullscreen, cancellationToken).ConfigureAwait(false);
        await SetPropertyAsync("pause", false, cancellationToken).ConfigureAwait(false);
        _paused = false;
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

    public bool TrySeek(long ms) => TrySeekCore(ms, isRelative: false);

    public bool TrySeekBy(long deltaMs) => TrySeekCore(deltaMs, isRelative: true);

    private bool TrySeekCore(long valueMs, bool isRelative)
    {
        if (_disposed)
        {
            return false;
        }

        var currentMs = Time;
        var length = Length;
        var startPump = false;
        long targetMs;

        lock (_seekSync)
        {
            if (_disposed)
            {
                return false;
            }

            var generation = Volatile.Read(ref _generation);
            var now = Environment.TickCount64;
            var observationOriginMs = currentMs;
            var requestedTargetMs = valueMs;
            if (isRelative)
            {
                (observationOriginMs, requestedTargetMs) = _seekBurstTargetMs is { } existingTargetMs
                    ? ResolveRelativeSeekBurst(
                        _seekBurstOriginMs,
                        existingTargetMs,
                        _seekBurstGeneration,
                        _seekBurstDeadline,
                        currentMs,
                        valueMs,
                        generation,
                        now)
                    : (currentMs, currentMs + valueMs);
            }

            targetMs = length > 0
                ? Math.Clamp(requestedTargetMs, 0, length)
                : Math.Max(0, requestedTargetMs);

            _pendingSeekMs = targetMs;
            _pendingSeekGeneration = generation;
            _pendingSeekObservationOriginMs = observationOriginMs;
            _pendingSeekObservationTargetMs = targetMs;
            _pendingSeekObservationGeneration = generation;
            _pendingSeekObservationDeadline = now + SeekObservationTimeoutMilliseconds;
            if (isRelative)
            {
                _seekBurstOriginMs = observationOriginMs;
                _seekBurstTargetMs = targetMs;
                _seekBurstGeneration = generation;
                _seekBurstDeadline = now + SeekBurstTimeoutMilliseconds;
            }
            else
            {
                ClearSeekBurst_NoLock();
            }

            if (!_seekPumpRunning)
            {
                _seekPumpRunning = true;
                startPump = true;
            }
        }

        Interlocked.Exchange(ref _timeMs, targetMs);
        Post(() => TimeChanged?.Invoke(this, targetMs));
        if (startPump)
        {
            _ = FlushPendingSeeksAsync();
        }

        return true;
    }

    internal static object[] BuildSeekCommand(long targetMs)
    {
        var targetSeconds = (targetMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
        return ["seek", targetSeconds, "absolute+exact"];
    }

    internal static bool HasReachedSeekTarget(long originMs, long targetMs, long observedMs)
    {
        var distanceMs = Math.Abs(targetMs - originMs);
        if (distanceMs == 0)
        {
            return true;
        }

        var proportionalToleranceMs = distanceMs * 2 / 5;
        var toleranceMs = Math.Max(
            proportionalToleranceMs,
            MinimumSeekObservationToleranceMilliseconds);
        toleranceMs = Math.Min(
            toleranceMs,
            Math.Max(0, distanceMs - MinimumObservedSeekMovementMilliseconds));

        if (targetMs < originMs)
        {
            return observedMs <= targetMs + toleranceMs;
        }

        return observedMs >= targetMs - toleranceMs;
    }

    internal static (long OriginMs, long TargetMs) ResolveRelativeSeekBurst(
        long existingOriginMs,
        long existingTargetMs,
        int existingGeneration,
        long existingDeadline,
        long currentMs,
        long deltaMs,
        int generation,
        long now)
    {
        if (existingGeneration != generation || now >= existingDeadline)
        {
            return (currentMs, currentMs + deltaMs);
        }

        var existingDirection = Math.Sign(existingTargetMs - existingOriginMs);
        var newDirection = Math.Sign(deltaMs);
        return existingDirection != 0 && existingDirection == newDirection
            ? (existingOriginMs, existingTargetMs + deltaMs)
            : (currentMs, currentMs + deltaMs);
    }

    public void Stop()
    {
        ClearPendingSeeks();
        Volatile.Write(ref _mediaLoaded, false);
        SetVideoOutput(false);
        _ = SendCommandAsync(["stop"]);
        Interlocked.Exchange(ref _timeMs, 0);
        SetState(PlaybackState.Idle);
    }

    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_process is { HasExited: false } && _ipc is { IsConnected: true })
                {
                    return;
                }
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            await RestartProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task RestartProcessAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ShutdownProcess_NoLock();
        }

        var mpv = FindMpvExecutable()
            ?? throw new InvalidOperationException("MPV_NOT_FOUND");

        var pipePath = @"\\.\pipe\" + _pipeName;
        var startInfo = new ProcessStartInfo
        {
            FileName = mpv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(mpv) ?? AppContext.BaseDirectory,
        };
        foreach (var argument in BuildArguments(_wid.ToInt64(), pipePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("MPV_START_FAILED");
        }

        var outputCts = new CancellationTokenSource();
        var stdoutDrainTask = DrainOutputAsync(process.StandardOutput, outputCts.Token);
        var stderrDrainTask = DrainOutputAsync(process.StandardError, outputCts.Token);
        lock (_sync)
        {
            _process = process;
            _outputCts = outputCts;
            _stdoutDrainTask = stdoutDrainTask;
            _stderrDrainTask = stderrDrainTask;
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
        await SendCommandAsync(["observe_property", 5, "fullscreen"], cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(["observe_property", 6, "vo-configured"], cancellationToken).ConfigureAwait(false);

        _observeCts = new CancellationTokenSource();
        _observeTask = Task.Run(() => ObserveLoopAsync(_observeCts.Token), CancellationToken.None);
    }

    internal static IReadOnlyList<string> BuildArguments(long windowHandle, string pipePath) =>
    [
        $"--wid={windowHandle}",
        "--idle=yes",
        "--force-window=no",
        "--keep-open=yes",
        "--fullscreen=no",
        "--mute=no",
        "--osc=no",
        "--osd-level=1",
        "--no-border",
        "--hwdec=d3d11va",
        "--hwdec-codecs=all",
        "--vo=gpu",
        "--gpu-context=d3d11",
        "--cache=no",
        "--hr-seek=yes",
        $"--slang={PreferredSubtitleLanguages}",
        $"--sub-font={PreferredSubtitleFont}",
        "--sub-font-size=42",
        "--input-default-bindings=no",
        $"--input-ipc-server={pipePath}",
    ];

    internal static object[] BuildLoadCommand(string localPath, long startMs)
    {
        if (startMs <= 0)
        {
            return ["loadfile", localPath, "replace"];
        }

        var startSeconds = (startMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            "loadfile",
            localPath,
            "replace",
            -1,
            new Dictionary<string, string> { ["start"] = startSeconds },
        ];
    }

    private static async Task DrainOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
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
                if (!root.TryGetProperty("event", out var evt))
                {
                    ApplyVideoOutputResponse(root);
                    continue;
                }

                var eventName = evt.GetString();
                if (eventName == "file-loaded")
                {
                    Volatile.Write(ref _mediaLoaded, true);
                    SetState(_paused ? PlaybackState.Paused : PlaybackState.Playing);
                    await QueryVideoOutputAsync(Volatile.Read(ref _generation), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (eventName == "end-file")
                {
                    Volatile.Write(ref _mediaLoaded, false);
                    SetVideoOutput(false);
                    if (root.TryGetProperty("reason", out var reason)
                        && reason.GetString() == "error")
                    {
                        SetState(PlaybackState.Error);
                    }

                    continue;
                }

                if (eventName != "property-change")
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
                        if (!TryApplyObservedTime(ms))
                        {
                            break;
                        }

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
                        if (Volatile.Read(ref _mediaLoaded))
                        {
                            SetState(_paused ? PlaybackState.Paused : PlaybackState.Playing);
                        }
                        break;
                    case "eof-reached" when data.ValueKind == JsonValueKind.True:
                        Post(() =>
                        {
                            SetState(PlaybackState.Ended);
                            EndReached?.Invoke(this, EventArgs.Empty);
                        });
                        break;
                    case "fullscreen" when data.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    {
                        var fullscreen = data.GetBoolean();
                        Volatile.Write(ref _fullscreen, fullscreen);
                        Post(() => FullscreenChanged?.Invoke(this, fullscreen));
                        break;
                    }
                    case "vo-configured" when data.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        SetVideoOutput(data.GetBoolean());
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

    private Task QueryVideoOutputAsync(int generation, CancellationToken cancellationToken)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return Task.CompletedTask;
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        Volatile.Write(ref _pendingVideoOutputGeneration, generation);
        Interlocked.Exchange(ref _pendingVideoOutputRequestId, requestId);
        return SendCommandAsync(
            ["get_property", "vo-configured"],
            cancellationToken,
            requestId);
    }

    private void ApplyVideoOutputResponse(JsonElement root)
    {
        var requestId = Interlocked.Read(ref _pendingVideoOutputRequestId);
        if (requestId == 0
            || !TryReadBooleanResponse(root, requestId, out var available)
            || Interlocked.CompareExchange(ref _pendingVideoOutputRequestId, 0, requestId) != requestId
            || Volatile.Read(ref _pendingVideoOutputGeneration) != Volatile.Read(ref _generation))
        {
            return;
        }

        SetVideoOutput(available);
    }

    internal static bool TryReadBooleanResponse(JsonElement root, long expectedRequestId, out bool value)
    {
        value = false;
        if (!root.TryGetProperty("request_id", out var requestId)
            || !requestId.TryGetInt64(out var actualRequestId)
            || actualRequestId != expectedRequestId
            || !root.TryGetProperty("error", out var error)
            || error.GetString() != "success"
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = data.GetBoolean();
        return true;
    }

    private async Task FlushPendingSeeksAsync()
    {
        var restart = false;
        try
        {
            while (!_disposed)
            {
                await Task.Delay(SeekCoalesceDelayMilliseconds).ConfigureAwait(false);

                long? targetMs;
                int generation;
                lock (_seekSync)
                {
                    targetMs = _pendingSeekMs;
                    generation = _pendingSeekGeneration;
                    _pendingSeekMs = null;
                }

                if (targetMs is null)
                {
                    break;
                }

                if (generation != Volatile.Read(ref _generation))
                {
                    continue;
                }

                await SendCommandAsync(BuildSeekCommand(targetMs.Value)).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            lock (_seekSync)
            {
                _seekPumpRunning = false;
                if (!_disposed && _pendingSeekMs is not null)
                {
                    _seekPumpRunning = true;
                    restart = true;
                }
            }

            if (restart)
            {
                _ = FlushPendingSeeksAsync();
            }
        }
    }

    private void ClearPendingSeeks()
    {
        lock (_seekSync)
        {
            _pendingSeekMs = null;
            ClearSeekObservation_NoLock();
            ClearSeekBurst_NoLock();
        }
    }

    internal bool ShouldAcceptObservedTime(long observedMs)
    {
        lock (_seekSync)
        {
            if (_pendingSeekObservationTargetMs is not { } targetMs)
            {
                return true;
            }

            if (_pendingSeekObservationGeneration != Volatile.Read(ref _generation)
                || Environment.TickCount64 >= _pendingSeekObservationDeadline)
            {
                ClearSeekObservation_NoLock();
                return true;
            }

            if (!HasReachedSeekTarget(_pendingSeekObservationOriginMs, targetMs, observedMs))
            {
                return false;
            }

            ClearSeekObservation_NoLock();
            return true;
        }
    }

    internal bool TryApplyObservedTime(long observedMs)
    {
        if (!ShouldAcceptObservedTime(observedMs))
        {
            return false;
        }

        Interlocked.Exchange(ref _timeMs, observedMs);
        return true;
    }

    private void ClearSeekObservation_NoLock()
    {
        _pendingSeekObservationTargetMs = null;
        _pendingSeekObservationDeadline = 0;
    }

    private void ClearSeekBurst_NoLock()
    {
        _seekBurstTargetMs = null;
        _seekBurstDeadline = 0;
    }

    private async Task SendCommandAsync(
        object[] command,
        CancellationToken cancellationToken = default,
        long? requestId = null)
    {
        var gateEntered = false;
        try
        {
            await _ipcWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;

            StreamWriter? writer;
            lock (_sync)
            {
                writer = _ipcWriter;
            }

            if (writer is null)
            {
                return;
            }

            var payload = requestId.HasValue
                ? JsonSerializer.Serialize(new { command, request_id = requestId.Value })
                : JsonSerializer.Serialize(new { command });
            await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            if (gateEntered)
            {
                _ipcWriteGate.Release();
            }
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

    private void SetVideoOutput(bool available)
    {
        if (Volatile.Read(ref _hasVideoOutput) == available)
        {
            return;
        }

        Volatile.Write(ref _hasVideoOutput, available);
        Post(() => VideoOutputChanged?.Invoke(this, available));
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
        try { _outputCts?.Cancel(); } catch { }
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
        try { _outputCts?.Dispose(); } catch { }
        _outputCts = null;
        _stdoutDrainTask = null;
        _stderrDrainTask = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
        ClearPendingSeeks();
        lock (_sync)
        {
            ShutdownProcess_NoLock();
        }
    }
}
