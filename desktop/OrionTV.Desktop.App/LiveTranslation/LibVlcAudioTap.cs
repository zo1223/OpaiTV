using System.Buffers;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// 独立 LibVLC 实例抽取当前直播 PCM，不与主播放器共享实例，避免 WPF 视频链路抢音频。
/// </summary>
internal sealed class LibVlcAudioTap : IDisposable
{
    private readonly object _sync = new();
    private readonly MediaPlayer.LibVLCAudioPlayCb _playCallback;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCallback;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCallback;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCallback;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCallback;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private volatile bool _acceptAudio;
    private bool _disposed;
    private long _totalBytes;

    public LibVlcAudioTap()
    {
        // 回调必须作为实例字段保活，防止 GC 回收非托管委托。
        _playCallback = OnAudioPlay;
        _pauseCallback = (_, _) => { };
        _resumeCallback = (_, _) => { };
        _flushCallback = (_, _) => { };
        _drainCallback = _ => { };
    }

    /// <remarks>订阅者必须在回调返回前消费数据；底层缓冲随后会被归还池中。</remarks>
    public event Action<ReadOnlyMemory<byte>>? AudioAvailable;
    public event Action? PlaybackFailed;

    /// <summary>已抽取的 PCM 字节数（诊断用）。</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public void Start(string url)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LibVlcAudioTap));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("直播地址无效", nameof(url));
        }

        lock (_sync)
        {
            StopCore();
            Interlocked.Exchange(ref _totalBytes, 0);

            // 专用实例：不与主播放器的 LibVLC/声卡输出共用。
            LibVLCSharp.Shared.Core.Initialize();
            var libVlc = new LibVLC(
                "--intf", "dummy",
                "--no-video-title-show",
                "--no-audio-time-stretch",
                "--quiet");
            var player = new MediaPlayer(libVlc);
            player.SetAudioFormat(
                "S16N",
                RealtimeTranslationProtocol.SampleRate,
                RealtimeTranslationProtocol.ChannelCount);
            player.SetAudioCallbacks(
                _playCallback,
                _pauseCallback,
                _resumeCallback,
                _flushCallback,
                _drainCallback);
            player.EncounteredError += Player_EncounteredError;

            var media = new Media(libVlc, uri);
            foreach (var option in PlaybackMediaOptions.ForUri(uri))
            {
                media.AddOption(option);
            }

            media.AddOption(":no-video");
            media.AddOption(":no-spu");
            // 明确不要走硬件声卡，只走回调。
            media.AddOption(":aout=adummy");

            _libVlc = libVlc;
            _player = player;
            _media = media;
            _acceptAudio = true;
            if (!player.Play(media))
            {
                StopCore();
                throw new InvalidOperationException("无法启动直播音频解码");
            }
        }
    }

    public void SetPaused(bool paused)
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        if (paused)
        {
            player.Pause();
        }
        else
        {
            player.Play();
        }
    }

    private void Player_EncounteredError(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            PlaybackFailed?.Invoke();
        }
    }

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long presentationTime)
    {
        if (!_acceptAudio || samples == IntPtr.Zero || count == 0)
        {
            return;
        }

        int byteCount;
        try
        {
            byteCount = checked((int)count
                * RealtimeTranslationProtocol.ChannelCount
                * RealtimeTranslationProtocol.BytesPerSample);
        }
        catch (OverflowException)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy(samples, buffer, 0, byteCount);
            Interlocked.Add(ref _totalBytes, byteCount);
            AudioAvailable?.Invoke(buffer.AsMemory(0, byteCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void StopCore()
    {
        _acceptAudio = false;
        if (_player is not null)
        {
            _player.EncounteredError -= Player_EncounteredError;
            try
            {
                _player.Stop();
            }
            catch
            {
                // 切台或退出时停止失败不阻塞主播放器。
            }

            _player.Dispose();
            _player = null;
        }

        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            StopCore();
        }
    }
}
