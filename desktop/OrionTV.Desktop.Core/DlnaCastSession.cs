using System.Net;

namespace OrionTV.Desktop.Core;

public enum DlnaCastState
{
    Idle,
    Connecting,
    Playing,
    Paused,
    Stopped,
    Error,
}

/// <summary>
/// 一次投屏会话：把地址推给设备并播放，之后可暂停/继续、跳转、停止；
/// <see cref="PollAsync"/> 取一次设备的进度与状态（界面每秒调一次）。所有方法都不抛异常，失败写进 <see cref="Error"/>。
/// 不是线程安全的：界面线程调用（方法内部的 await 会回到调用方的同步上下文）。
/// </summary>
public sealed class DlnaCastSession(DlnaAvTransport transport)
{
    /// <summary>起始进度不到这个值时从头播，不发 Seek。</summary>
    public static readonly TimeSpan MinStartSeek = TimeSpan.FromSeconds(5);

    private const int MaxPendingSeekAttempts = 5;
    private const int MaxPollFailures = 5;
    private const int StoppedPollsToEnd = 2;

    private TimeSpan? _pendingSeek;
    private int _pendingSeekAttempts;
    private int _pollFailures;
    private int _stoppedPolls;
    private bool _sawPlaying;
    private bool _polling;

    /// <summary>每发一个控制命令加一；轮询开始后如果它变了，这次轮询的结果作废（避免旧状态盖掉新命令）。</summary>
    private int _commandVersion;

    public DlnaDevice? Device { get; private set; }

    public DlnaCastState State { get; private set; } = DlnaCastState.Idle;

    public string Title { get; private set; } = "";

    public string Url { get; private set; } = "";

    /// <summary>设备端的播放进度（最后一次取到的）。停止后保留，用来恢复本机播放。</summary>
    public TimeSpan Position { get; private set; }

    public TimeSpan Duration { get; private set; }

    /// <summary>最近一次推送或控制命令的失败原因；下一次命令成功后清掉。</summary>
    public string? Error { get; private set; }

    /// <summary>连续多次取不到设备状态。会话保留并继续重试，取到后自动恢复。</summary>
    public bool Unresponsive { get; private set; }

    /// <summary>这次会话是设备端自己停的（播完，或被遥控器停止），不是这边点的停止。</summary>
    public bool EndedByDevice { get; private set; }

    /// <summary>正在投屏（已推送且没有停止）。</summary>
    public bool IsActive => State is DlnaCastState.Connecting or DlnaCastState.Playing or DlnaCastState.Paused;

    public event EventHandler? Changed;

    /// <summary>地址能否交给局域网里的设备去取。能返回 null，不能返回给用户看的原因。</summary>
    public static string? ValidateCastUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return "本地文件无法投屏，只能投在线片源";
        }

        if (uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)))
        {
            return "这是本机地址，电视取不到，无法投屏";
        }

        return null;
    }

    public async Task<bool> CastAsync(
        DlnaDevice device,
        string url,
        string title,
        TimeSpan startPosition,
        CancellationToken cancellationToken = default)
    {
        var invalid = ValidateCastUrl(url);
        if (invalid is not null)
        {
            Error = invalid;
            if (!IsActive)
            {
                State = DlnaCastState.Error;
            }

            Raise();
            return false;
        }

        var version = ++_commandVersion;
        var start = startPosition >= MinStartSeek ? startPosition : TimeSpan.Zero;
        Device = device;
        Title = title;
        Url = url;
        Position = start;
        Duration = TimeSpan.Zero;
        Error = null;
        Unresponsive = false;
        EndedByDevice = false;
        _pollFailures = 0;
        _stoppedPolls = 0;
        _sawPlaying = false;
        _pendingSeek = null;
        _pendingSeekAttempts = 0;
        State = DlnaCastState.Connecting;
        Raise();

        try
        {
            await transport.SetAvTransportUriAsync(device, url, title, cancellationToken);
            await transport.PlayAsync(device, cancellationToken);
        }
        catch (Exception ex)
        {
            if (version == _commandVersion)
            {
                var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
                Error = cancelled ? null : ex.Message;
                State = cancelled ? DlnaCastState.Stopped : DlnaCastState.Error;
                Raise();
            }

            return false;
        }

        if (version != _commandVersion || State != DlnaCastState.Connecting)
        {
            // 连接期间已被停止。
            return false;
        }

        State = DlnaCastState.Playing;
        if (start > TimeSpan.Zero)
        {
            // 不少设备在缓冲完成前拒绝 Seek：先试一次，不行就留给轮询在设备报 PLAYING 之后重试。
            _pendingSeek = start;
            await TryPendingSeekAsync(cancellationToken);
        }

        Raise();
        return true;
    }

    public async Task<bool> TogglePlayAsync(CancellationToken cancellationToken = default)
    {
        if (Device is null || !IsActive)
        {
            return false;
        }

        _commandVersion++;
        try
        {
            if (State == DlnaCastState.Paused)
            {
                await transport.PlayAsync(Device, cancellationToken);
                if (IsActive)
                {
                    State = DlnaCastState.Playing;
                }
            }
            else
            {
                await transport.PauseAsync(Device, cancellationToken);
                if (IsActive)
                {
                    State = DlnaCastState.Paused;
                }
            }

            Error = null;
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Raise();
            return false;
        }
    }

    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (Device is null || !IsActive)
        {
            return false;
        }

        var target = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (Duration > TimeSpan.Zero && target > Duration)
        {
            target = Duration;
        }

        _commandVersion++;
        try
        {
            await transport.SeekAsync(Device, target, cancellationToken);
            _pendingSeek = null;
            Position = target;
            Error = null;
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Raise();
            return false;
        }
    }

    /// <summary>
    /// 停止投屏，返回设备端最后的进度（恢复本机播放用）。状态在发请求之前就改成已停止，调用方不必等设备应答。
    /// Stop 被设备拒绝时退而求其次发 Pause（与 CineFlow 一致）；设备连不上就不再试。
    /// </summary>
    public async Task<TimeSpan> StopAsync(CancellationToken cancellationToken = default)
    {
        var device = Device;
        var wasActive = IsActive;
        var position = Position;
        _commandVersion++;
        _pendingSeek = null;
        Unresponsive = false;
        if (State != DlnaCastState.Idle)
        {
            State = DlnaCastState.Stopped;
        }

        Raise();
        if (device is null || !wasActive)
        {
            return position;
        }

        try
        {
            await transport.StopAsync(device, cancellationToken);
        }
        catch (DlnaSoapException ex) when (ex.StatusCode is not null)
        {
            try
            {
                await transport.PauseAsync(device, cancellationToken);
            }
            catch
            {
                // 设备两个动作都不认：按已停止处理。
            }
        }
        catch
        {
            // 设备已不可达：按已停止处理。
        }

        return position;
    }

    /// <summary>向设备取一次状态与进度。连续多次取不到时标记为无响应（会话保留，取到后恢复）。</summary>
    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        var device = Device;
        if (device is null || !IsActive || _polling)
        {
            return;
        }

        _polling = true;
        var version = _commandVersion;
        try
        {
            var transportState = await transport.GetTransportStateAsync(device, cancellationToken);
            if (version != _commandVersion || !IsActive)
            {
                return;
            }

            switch (transportState)
            {
                case "PLAYING":
                    _sawPlaying = true;
                    _stoppedPolls = 0;
                    State = DlnaCastState.Playing;
                    break;
                case "PAUSED_PLAYBACK":
                    _sawPlaying = true;
                    _stoppedPolls = 0;
                    State = DlnaCastState.Paused;
                    break;
                case "STOPPED":
                case "NO_MEDIA_PRESENT":
                    // 刚推送完、还在缓冲时设备可能仍报 STOPPED：只有设备报过 PLAYING / PAUSED 之后，
                    // 且连续两次都是停止，才认为是电视端停了。
                    if (_sawPlaying && ++_stoppedPolls >= StoppedPollsToEnd)
                    {
                        // 不再取进度：停止后设备多半报 0，会冲掉用来恢复本机播放的最后进度。
                        _pendingSeek = null;
                        _pollFailures = 0;
                        Unresponsive = false;
                        EndedByDevice = true;
                        State = DlnaCastState.Stopped;
                        Raise();
                        return;
                    }

                    break;
                default:
                    _stoppedPolls = 0;
                    break;
            }

            if (_sawPlaying && State == DlnaCastState.Playing && _pendingSeek is not null)
            {
                await TryPendingSeekAsync(cancellationToken);
            }

            var info = await transport.GetPositionInfoAsync(device, cancellationToken);
            if (version != _commandVersion || !IsActive)
            {
                return;
            }

            // 设备还没报过 PLAYING（缓冲中进度是 0）、或刚报停止还没确认的那一次，不采用它的进度。
            if (_pendingSeek is null && _stoppedPolls == 0 && _sawPlaying)
            {
                Position = info.Position;
            }

            if (info.Duration > TimeSpan.Zero)
            {
                Duration = info.Duration;
            }

            _pollFailures = 0;
            Unresponsive = false;
        }
        catch (Exception ex)
        {
            if (version != _commandVersion
                || !IsActive
                || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                return;
            }

            _pollFailures++;
            if (_pollFailures >= MaxPollFailures)
            {
                Unresponsive = true;
            }
        }
        finally
        {
            _polling = false;
        }

        Raise();
    }

    private async Task TryPendingSeekAsync(CancellationToken cancellationToken)
    {
        if (Device is null || _pendingSeek is not { } target)
        {
            return;
        }

        _pendingSeekAttempts++;
        try
        {
            await transport.SeekAsync(Device, target, cancellationToken);
            Position = target;
            _pendingSeek = null;
        }
        catch
        {
            if (_pendingSeekAttempts >= MaxPendingSeekAttempts)
            {
                _pendingSeek = null;
            }
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
