using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// 豆包同声传译 2.0（火山 AST）：PCM 入 → 中文字幕 + 中文配音 PCM 出。
/// </summary>
internal sealed class DoubaoAstTranslateClient : ILiveTranslationSession
{
    private const int MaxServerMessageBytes = 4 * 1024 * 1024;

    private readonly string _appKey;
    private readonly string _accessKey;
    private readonly string _resourceId;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly string _mode;
    private readonly bool _playDubbing;
    private readonly Pcm16TranslationFrameBuffer _frameBuffer =
        new(DoubaoAstProtocol.ChunkBytes);
    private readonly object _frameSync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetimeCts;
    private Channel<byte[]>? _audioFrames;
    private Task? _audioSendTask;
    private Task? _receiveTask;
    private int _stopping;
    private bool _disposed;
    private bool _sessionReady;
    private string _sessionId = "";
    private string _subtitleCarry = "";

    public DoubaoAstTranslateClient(
        string appKey,
        string accessKey,
        string? resourceId = null,
        string sourceLanguage = DoubaoAstProtocol.DefaultSourceLanguage,
        string targetLanguage = DoubaoAstProtocol.DefaultTargetLanguage,
        string mode = DoubaoAstProtocol.DefaultMode,
        bool playDubbing = true)
    {
        _appKey = appKey.Trim();
        _accessKey = accessKey.Trim();
        _resourceId = string.IsNullOrWhiteSpace(resourceId)
            ? DoubaoAstProtocol.DefaultResourceId
            : resourceId.Trim();
        _sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
            ? DoubaoAstProtocol.DefaultSourceLanguage
            : sourceLanguage.Trim();
        _targetLanguage = string.IsNullOrWhiteSpace(targetLanguage)
            ? DoubaoAstProtocol.DefaultTargetLanguage
            : targetLanguage.Trim();
        _mode = playDubbing ? "s2s" : (string.IsNullOrWhiteSpace(mode) ? "s2t" : mode.Trim());
        _playDubbing = playDubbing && _mode == "s2s";
    }

    public event Action<string>? SubtitleDelta;
    public event Action<RealtimeTranslationConnectionState>? StateChanged;
    public event Action<string?, string?>? Failed;
    public event Action<string>? Progress;
    public event Action<ReadOnlyMemory<byte>>? DubbingAudio;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null)
        {
            throw new InvalidOperationException("豆包同传会话已经启动");
        }

        if (string.IsNullOrWhiteSpace(_appKey) || string.IsNullOrWhiteSpace(_accessKey))
        {
            throw new InvalidOperationException("请配置豆包 APP Key 与 Access Key");
        }

        StateChanged?.Invoke(RealtimeTranslationConnectionState.Connecting);
        Progress?.Invoke("正在连接豆包同声传译 2.0…");
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(40)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var connectId = Guid.NewGuid().ToString("N");
        socket.Options.SetRequestHeader("X-Api-App-Key", _appKey);
        socket.Options.SetRequestHeader("X-Api-Access-Key", _accessKey);
        socket.Options.SetRequestHeader("X-Api-Resource-Id", _resourceId);
        socket.Options.SetRequestHeader("X-Api-Connect-Id", connectId);
        _socket = socket;

        try
        {
            await socket
                .ConnectAsync(new Uri(DoubaoAstProtocol.DefaultWsUrl), _lifetimeCts.Token)
                .ConfigureAwait(false);

            LiveTranslationLog.Write(
                $"doubao ws connected resource={_resourceId} mode={_mode} {_sourceLanguage}->{_targetLanguage}");

            _sessionId = Guid.NewGuid().ToString("N");
            _receiveTask = ReceiveLoopAsync(socket, _lifetimeCts.Token);

            var start = DoubaoAstProtocol.BuildStartSession(
                _sessionId,
                _sourceLanguage,
                _targetLanguage,
                _mode,
                denoise: false);
            await SendBinaryAsync(start, _lifetimeCts.Token).ConfigureAwait(false);
            LiveTranslationLog.Write("doubao StartSession sent");

            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!_sessionReady && DateTime.UtcNow < deadline)
            {
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopping) != 0)
                {
                    throw new InvalidOperationException("豆包同传启动被取消");
                }

                await Task.Delay(50, _lifetimeCts.Token).ConfigureAwait(false);
            }

            if (!_sessionReady)
            {
                throw new InvalidOperationException(
                    "豆包同传 SessionStarted 超时。请检查 APP Key / Access Key 是否开通「同声传译 2.0」，以及网络是否可达 openspeech.bytedance.com。");
            }

            _audioSendTask = SendAudioLoopAsync(_audioFrames.Reader, _lifetimeCts.Token);
            StateChanged?.Invoke(RealtimeTranslationConnectionState.Listening);
            Progress?.Invoke(_playDubbing
                ? "豆包同传已连接，正在生成字幕与配音…"
                : "豆包同传已连接，正在生成字幕…");
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>接收 24kHz PCM16 mono（LibVlcAudioTap），内部重采样到 16kHz。</summary>
    public void AppendPcm16(ReadOnlySpan<byte> audio24k)
    {
        var writer = _audioFrames?.Writer;
        if (writer is null || Volatile.Read(ref _stopping) != 0 || !_sessionReady)
        {
            return;
        }

        var pcm16k = Resample24kTo16k(audio24k);
        if (pcm16k.Length == 0)
        {
            return;
        }

        lock (_frameSync)
        {
            _frameBuffer.Append(pcm16k, frame => writer.TryWrite(frame));
        }
    }

    private async Task SendAudioLoopAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var packet = DoubaoAstProtocol.BuildTaskRequest(_sessionId, frame);
                await SendBinaryAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("transport_error", exception.Message);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket
                        .ReceiveAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        LiveTranslationLog.Write(
                            $"doubao server close {socket.CloseStatus}: {socket.CloseStatusDescription}");
                        if (Volatile.Read(ref _stopping) == 0 && !_sessionReady)
                        {
                            Failed?.Invoke(
                                socket.CloseStatus?.ToString() ?? "closed",
                                socket.CloseStatusDescription ?? "连接被关闭");
                        }

                        StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxServerMessageBytes)
                    {
                        throw new InvalidDataException("豆包服务消息过大");
                    }
                }
                while (!result.EndOfMessage);

                HandleServerEvent(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("transport_error", exception.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleServerEvent(ReadOnlySpan<byte> payload)
    {
        DoubaoAstServerEvent evt;
        try
        {
            evt = DoubaoAstProtocol.ParseResponse(payload);
        }
        catch (Exception exception)
        {
            LiveTranslationLog.Write("doubao parse: " + exception.Message);
            return;
        }

        if (evt.StatusCode != 0 && evt.StatusCode != DoubaoAstProtocol.StatusSuccess)
        {
            ReportFailure(evt.StatusCode.ToString(), evt.Message ?? "豆包服务返回错误");
            return;
        }

        switch (evt.Event)
        {
            case DoubaoAstProtocol.EventSessionStarted:
                _sessionReady = true;
                LiveTranslationLog.Write("doubao SessionStarted");
                break;

            case DoubaoAstProtocol.EventSessionFailed:
            case DoubaoAstProtocol.EventSessionCanceled:
                ReportFailure(
                    evt.Event.ToString(),
                    evt.Message ?? (evt.Event == DoubaoAstProtocol.EventSessionFailed
                        ? "会话失败"
                        : "会话取消"));
                break;

            case DoubaoAstProtocol.EventSessionFinished:
                StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
                break;

            case DoubaoAstProtocol.EventTranslationSubtitleStart:
            case DoubaoAstProtocol.EventTranslationSubtitleResponse:
            case DoubaoAstProtocol.EventTranslationSubtitleEnd:
                EmitSubtitle(evt.Text, isFinal: evt.Event == DoubaoAstProtocol.EventTranslationSubtitleEnd);
                break;

            case DoubaoAstProtocol.EventSourceSubtitleResponse:
                if (!string.IsNullOrWhiteSpace(evt.Text))
                {
                    Progress?.Invoke("正在翻译…");
                }

                break;

            case DoubaoAstProtocol.EventTtsResponse:
                if (_playDubbing && evt.Audio is { Length: > 0 })
                {
                    DubbingAudio?.Invoke(evt.Audio);
                }

                break;
        }
    }

    private void EmitSubtitle(string? text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var incoming = text.Trim();
        string delta;
        if (!string.IsNullOrEmpty(_subtitleCarry)
            && incoming.StartsWith(_subtitleCarry, StringComparison.Ordinal))
        {
            delta = incoming[_subtitleCarry.Length..].TrimStart();
        }
        else if (!string.IsNullOrEmpty(_subtitleCarry)
                 && _subtitleCarry.StartsWith(incoming, StringComparison.Ordinal))
        {
            return;
        }
        else
        {
            delta = incoming;
        }

        _subtitleCarry = incoming.Length > 400 ? incoming[^400..] : incoming;
        if (string.IsNullOrWhiteSpace(delta))
        {
            if (isFinal && !string.IsNullOrWhiteSpace(incoming))
            {
                // 终态但无增量：补一个空格便于滚动
                SubtitleDelta?.Invoke(" ");
            }

            return;
        }

        if (isFinal
            && !delta.EndsWith('。') && !delta.EndsWith('！') && !delta.EndsWith('？')
            && !delta.EndsWith('.') && !delta.EndsWith('!') && !delta.EndsWith('?'))
        {
            delta += "。";
        }
        else if (!delta.EndsWith(' '))
        {
            delta += " ";
        }

        SubtitleDelta?.Invoke(delta);
    }

    private short[] _resampleHistory = Array.Empty<short>();
    private readonly object _resampleSync = new();

    private byte[] Resample24kTo16k(ReadOnlySpan<byte> pcm24k)
    {
        var inSamples = pcm24k.Length / 2;
        if (inSamples <= 0)
        {
            return Array.Empty<byte>();
        }

        lock (_resampleSync)
        {
            var total = _resampleHistory.Length + inSamples;
            var combined = new short[total];
            if (_resampleHistory.Length > 0)
            {
                Array.Copy(_resampleHistory, combined, _resampleHistory.Length);
            }

            for (var i = 0; i < inSamples; i++)
            {
                combined[_resampleHistory.Length + i] =
                    (short)(pcm24k[i * 2] | (pcm24k[i * 2 + 1] << 8));
            }

            var outCount = (total / 3) * 2;
            var usedIn = (outCount / 2) * 3;
            var output = new byte[outCount * 2];
            var o = 0;
            for (var i = 0; i + 2 < usedIn; i += 3)
            {
                var a = combined[i];
                var b = (short)((combined[i + 1] + combined[i + 2]) / 2);
                output[o++] = (byte)(a & 0xFF);
                output[o++] = (byte)((a >> 8) & 0xFF);
                output[o++] = (byte)(b & 0xFF);
                output[o++] = (byte)((b >> 8) & 0xFF);
            }

            var remain = total - usedIn;
            if (remain > 0)
            {
                _resampleHistory = new short[remain];
                Array.Copy(combined, usedIn, _resampleHistory, 0, remain);
            }
            else
            {
                _resampleHistory = Array.Empty<short>();
            }

            return output;
        }
    }

    private async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket
                    .SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void ReportFailure(string? code, string? message)
    {
        if (Volatile.Read(ref _stopping) == 0)
        {
            LiveTranslationLog.Write($"doubao fail {code}: {message}");
            Failed?.Invoke(code, message);
        }
    }

    private async Task AbortAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _audioFrames?.Writer.TryComplete();
        try
        {
            if (_socket is { State: WebSocketState.Open } && !string.IsNullOrEmpty(_sessionId))
            {
                var finish = DoubaoAstProtocol.BuildFinishSession(_sessionId);
                await SendBinaryAsync(finish, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        _lifetimeCts?.Cancel();
        try
        {
            _socket?.Abort();
        }
        catch
        {
        }

        _socket?.Dispose();
        _socket = null;
        await ObserveAsync(_audioSendTask).ConfigureAwait(false);
        await ObserveAsync(_receiveTask).ConfigureAwait(false);
        _audioSendTask = null;
        _receiveTask = null;
        lock (_frameSync)
        {
            _frameBuffer.Reset();
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await AbortAsync().ConfigureAwait(false);
        _lifetimeCts?.Dispose();
        _sendGate.Dispose();
        StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _stopping, 1);
        _audioFrames?.Writer.TryComplete();
        _lifetimeCts?.Cancel();
        try
        {
            _socket?.Abort();
        }
        catch
        {
        }

        _socket?.Dispose();
        _socket = null;
    }
}
