using System.Windows.Threading;
using LibVLCSharp.Shared;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>播放状态（聚合 LibVLC 事件，供 UI 显示加载/缓冲/错误/暂停态）。</summary>
public enum PlaybackState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Ended,
    Error,
}

/// <summary>
/// 封装 LibVLC <see cref="MediaPlayer"/> 的稳定播放控制：
/// - 切换媒体先 Stop、加锁串行化并按代次防抖，避免快速切台/换集导致的黑屏与崩溃；
/// - 规范 Media 生命周期（播放下一个后再释放上一个，杜绝 using 过早释放）；
/// - 把后台线程触发的 LibVLC 事件统一调度回 UI 线程后再抛出。
/// </summary>
public sealed class PlaybackController : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly Dispatcher _dispatcher;
    private readonly MediaPlayer _player;
    private readonly object _sync = new();

    private Media? _currentMedia;
    private readonly PlaybackStartSeekTracker _startSeek = new();
    private bool _applyingStartSeek;
    private int _generation;
    private int _activeMediaGeneration;
    private int _volume = 100;
    private bool _mute;
    private volatile bool _hasVideoOutput;
    private volatile bool _disposed;
    private Task? _disposeTask;
    private readonly object _disposeSync = new();

    public PlaybackController(LibVLC libVlc, Dispatcher dispatcher)
    {
        _libVlc = libVlc;
        _dispatcher = dispatcher;
        _player = new MediaPlayer(libVlc);

        _player.Opening += (_, _) => Post(() => SetState(PlaybackState.Opening));
        _player.Buffering += (_, e) => Post(() =>
        {
            Buffering?.Invoke(this, e.Cache);
            SetState(PlaybackState.Buffering);
        });
        _player.Playing += (_, _) => Post(() =>
        {
            ApplyAudioState();
            SetState(PlaybackState.Playing);
        });
        _player.Paused += (_, _) => Post(() => SetState(PlaybackState.Paused));
        _player.EncounteredError += (_, _) => Post(() => SetState(PlaybackState.Error));
        _player.EndReached += (_, _) => Post(() =>
        {
            SetState(PlaybackState.Ended);
            EndReached?.Invoke(this, EventArgs.Empty);
        });
        _player.TimeChanged += (_, e) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            Post(() =>
            {
                if (_applyingStartSeek || generation != _generation)
                {
                    // 补起播 seek 时 LibVLC 会同步回报一次“目标值”（seek 此刻还没执行，也可能随后被拒绝），不是进度；
                    // 上一个媒体迟到的进度也不是当前媒体的进度。两者都不能算数，也不往外报。
                    return;
                }

                if (e.Time > 0 && State is PlaybackState.Opening or PlaybackState.Buffering)
                {
                    SetState(PlaybackState.Playing);
                }

                ApplyStartSeekIfNeeded(e.Time);
                TimeChanged?.Invoke(this, e.Time);
            });
        };
        _player.LengthChanged += (_, e) => Post(() => LengthChanged?.Invoke(this, e.Length));
        _player.Vout += (_, e) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            var hasVideoOutput = e.Count > 0;
            Post(() =>
            {
                if (generation == _generation)
                {
                    SetVideoOutput(hasVideoOutput);
                }
            });
        };
    }

    public MediaPlayer Player => _player;

    public PlaybackState State { get; private set; } = PlaybackState.Idle;

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<float>? Buffering;
    public event EventHandler<long>? TimeChanged;
    public event EventHandler<long>? LengthChanged;
    public event EventHandler? EndReached;
    public event EventHandler<bool>? VideoOutputChanged;

    public long Length => _player.Length;

    public long Time
    {
        get => _player.Time;
        set
        {
            if (_player.Length > 0)
            {
                _player.Time = value;
            }
        }
    }

    public bool IsPlaying => _player.IsPlaying;

    public bool HasVideoOutput => _hasVideoOutput;

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            _player.Volume = _volume;
        }
    }

    public bool Mute
    {
        get => _mute;
        set
        {
            _mute = value;
            _player.Mute = value;
        }
    }

    /// <summary>切换到新地址播放（线程安全、按代次防抖）。startMs 为起播位置（用于续播）。</summary>
    public void SwitchTo(string url, long startMs = 0, int networkCachingMs = 1500)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var generation = ++_generation;
        _startSeek.Arm(startMs);
        SetVideoOutput(false);
        SetState(PlaybackState.Opening);

        Task.Run(() =>
        {
            lock (_sync)
            {
                // 排队期间又有更新的切换请求时，丢弃本次（只播最后一次）。
                if (_disposed || generation != _generation)
                {
                    return;
                }

                try
                {
                    _player.Stop();
                    Volatile.Write(ref _activeMediaGeneration, generation);
                    var media = new Media(_libVlc, uri);
                    foreach (var option in PlaybackMediaOptions.ForUri(uri, networkCachingMs))
                    {
                        media.AddOption(option);
                    }

                    // 续播：起播位置随媒体一起交给播放器，由它在打开媒体时直接跳过去，不依赖起播之后再补 seek
                    // （起播后的 seek 可能被解复用器拒绝，LibVLC 不会报错）。
                    var startTimeOption = PlaybackMediaOptions.StartTimeOption(startMs);
                    if (startTimeOption is not null)
                    {
                        media.AddOption(startTimeOption);
                    }

                    var previous = _currentMedia;
                    _currentMedia = media;
                    _player.Play(media);
                    if (startTimeOption is not null)
                    {
                        // 起播位置只管这一次起播：之后对同一个媒体再次播放（停止后再点播放）仍从头开始，与原来一致。
                        media.AddOption(PlaybackMediaOptions.ResetStartTimeOption);
                    }

                    previous?.Dispose();
                }
                catch
                {
                    Post(() => SetState(PlaybackState.Error));
                }
            }
        });
    }

    public void TogglePlayPause()
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
    }

    public void Play() => _player.Play();

    public void Pause() => _player.Pause();

    /// <summary>尝试跳转;媒体时长未知(Length<=0)时 LibVLC 会静默丢弃 seek,返回 false 供调用方稍后重试。</summary>
    public bool TrySeek(long ms)
    {
        if (_player.Length > 0)
        {
            _player.Time = ms;
            return true;
        }

        return false;
    }

    public void Stop()
    {
        // 作废仍在排队的 SwitchTo 与未消费的续播 seek:Stop 之后不得再开播旧请求。
        var generation = ++_generation;
        _startSeek.Cancel();
        Volatile.Write(ref _activeMediaGeneration, generation);
        SetVideoOutput(false);
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _player.Stop();
                }
            }
        });
    }

    /// <summary>用户自己跳了进度：不再把播放器拉回起播位置。</summary>
    public void CancelStartSeek() => _startSeek.Cancel();

    /// <summary>
    /// 起播位置已随媒体选项交给 LibVLC（:start-time）；这里用播放器报上来的真实进度确认它落实了没有。
    /// 进度说明播放器正在别处播放（起播位置没被采纳）时补 seek，被拒绝就按间隔重试，
    /// 由 <see cref="PlaybackStartSeekTracker"/> 决定。
    /// </summary>
    private void ApplyStartSeekIfNeeded(long currentTimeMs)
    {
        if (_startSeek.OnProgress(currentTimeMs, _player.Length, DateTimeOffset.UtcNow) is not { } target)
        {
            return;
        }

        _applyingStartSeek = true;
        try
        {
            _player.Time = target;
        }
        finally
        {
            _applyingStartSeek = false;
        }
    }

    private void ApplyAudioState()
    {
        _player.Volume = _volume;
        _player.Mute = _mute;
    }

    private void SetState(PlaybackState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void SetVideoOutput(bool available)
    {
        if (_hasVideoOutput == available)
        {
            return;
        }

        _hasVideoOutput = available;
        VideoOutputChanged?.Invoke(this, available);
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    public Task DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null)
            {
                return _disposeTask;
            }

            // Mark the controller dead before queueing native cleanup so pending
            // SwitchTo work cannot start playback again after the view is gone.
            _disposed = true;
            Interlocked.Increment(ref _generation);
            _disposeTask = Task.Run(DisposeCore);
            return _disposeTask;
        }
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    private void DisposeCore()
    {
        lock (_sync)
        {
            try
            {
                _player.Stop();
            }
            catch
            {
                // 忽略停止异常。
            }

            _player.Dispose();
            _currentMedia?.Dispose();
            _currentMedia = null;
        }
    }
}
