using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// Google Gemini Live Translate：PCM 入 → 中文配音 PCM 出 + 中文字幕转写。
/// </summary>
internal sealed class GeminiLiveTranslateClient : ILiveTranslationSession
{
    private const int MaxServerMessageBytes = 4 * 1024 * 1024;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLanguage;
    private readonly bool _playDubbing;
    private readonly Pcm16TranslationFrameBuffer _frameBuffer =
        new(GeminiLiveTranslateProtocol.ChunkBytes);
    private readonly object _frameSync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // 24k → 16k 重采样残留
    private readonly object _resampleSync = new();
    private short[] _resampleHistory = Array.Empty<short>();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetimeCts;
    private Channel<byte[]>? _audioFrames;
    private Task? _audioSendTask;
    private Task? _receiveTask;
    private int _stopping;
    private bool _disposed;
    private bool _setupComplete;
    private string _subtitleCarry = "";

    public GeminiLiveTranslateClient(
        string apiKey,
        string? model,
        string? targetLanguage,
        bool playDubbing = true)
    {
        _apiKey = apiKey;
        _model = GeminiLiveTranslateProtocol.NormalizeModel(model);
        _targetLanguage = GeminiLiveTranslateProtocol.NormalizeTargetLanguage(targetLanguage);
        _playDubbing = playDubbing;
    }

    public event Action<string>? SubtitleDelta;
    public event Action<RealtimeTranslationConnectionState>? StateChanged;
    public event Action<string?, string?>? Failed;
    public event Action<string>? Progress;
    public event Action<ReadOnlyMemory<byte>>? DubbingAudio;

    public bool ProducesDubbing => _playDubbing;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null)
        {
            throw new InvalidOperationException("Gemini 同传会话已经启动");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Gemini API Key 为空");
        }

        // 常见 AI Studio Key 以 AIza 开头；其它前缀也可能可用，但项目若被拒会直接 403。
        if (_apiKey.StartsWith("AQ.", StringComparison.Ordinal)
            && !_apiKey.StartsWith("AIza", StringComparison.Ordinal))
        {
            LiveTranslationLog.Write("gemini key prefix is AQ.* — may be restricted; prefer AIza... from AI Studio");
        }

        StateChanged?.Invoke(RealtimeTranslationConnectionState.Connecting);
        Progress?.Invoke("正在连接 Gemini Live Translate…");
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(40)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;
        string? lastServerClose = null;

        try
        {
            await socket
                .ConnectAsync(GeminiLiveTranslateProtocol.BuildWebSocketUri(_apiKey), _lifetimeCts.Token)
                .ConfigureAwait(false);

            LiveTranslationLog.Write($"gemini ws connected model={_model} lang={_targetLanguage}");
            _receiveTask = ReceiveLoopAsync(socket, _lifetimeCts.Token, reason => lastServerClose = reason);

            var setup = GeminiLiveTranslateProtocol.CreateSetupMessage(_model, _targetLanguage);
            LiveTranslationLog.Write("gemini setup sent");
            await SendTextAsync(setup, _lifetimeCts.Token).ConfigureAwait(false);

            // 等 setupComplete，最多 12s
            var setupDeadline = DateTime.UtcNow.AddSeconds(12);
            while (!_setupComplete && DateTime.UtcNow < setupDeadline)
            {
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopping) != 0)
                {
                    throw new InvalidOperationException(
                        FormatCloseError(lastServerClose) ?? "Gemini 同传启动被取消");
                }

                await Task.Delay(50, _lifetimeCts.Token).ConfigureAwait(false);
            }

            if (!_setupComplete)
            {
                throw new InvalidOperationException(
                    FormatCloseError(lastServerClose)
                    ?? "Gemini 会话 setup 超时。请确认：1) AI Studio 新建 AIza 开头的 Key；2) 项目未被拒绝访问；3) 网络可访问 Google。");
            }

            _audioSendTask = SendAudioLoopAsync(_audioFrames.Reader, _lifetimeCts.Token);
            StateChanged?.Invoke(RealtimeTranslationConnectionState.Listening);
            Progress?.Invoke("Gemini 同传已连接，正在生成字幕与配音…");
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>接收 24kHz PCM16 mono（与 LibVlcAudioTap 一致），内部重采样到 16kHz 再送出。</summary>
    public void AppendPcm16(ReadOnlySpan<byte> audio24k)
    {
        var writer = _audioFrames?.Writer;
        if (writer is null || Volatile.Read(ref _stopping) != 0 || !_setupComplete)
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
                await SendTextAsync(
                        GeminiLiveTranslateProtocol.CreateAudioChunkMessage(frame),
                        cancellationToken)
                    .ConfigureAwait(false);
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

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken,
        Action<string>? onClose = null)
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
                        var reason =
                            $"{socket.CloseStatus}: {socket.CloseStatusDescription}".Trim();
                        LiveTranslationLog.Write("gemini server close " + reason);
                        onClose?.Invoke(reason);
                        if (Volatile.Read(ref _stopping) == 0)
                        {
                            // 让 StartAsync 的等待循环立刻看到错误。
                            Interlocked.Exchange(ref _stopping, 1);
                            if (!_setupComplete)
                            {
                                Failed?.Invoke(
                                    socket.CloseStatus?.ToString() ?? "closed",
                                    FormatCloseError(reason) ?? reason);
                            }
                            else
                            {
                                ReportFailure(
                                    socket.CloseStatus?.ToString() ?? "closed",
                                    FormatCloseError(reason) ?? reason);
                            }
                        }

                        StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxServerMessageBytes)
                    {
                        throw new InvalidDataException("Gemini 服务消息过大");
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text
                    && result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                HandleServerEvent(json);
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

    private static string? FormatCloseError(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var lower = reason.ToLowerInvariant();
        if (lower.Contains("policyviolation")
            || lower.Contains("denied access")
            || lower.Contains("permission_denied")
            || lower.Contains("permission denied"))
        {
            return "当前 Google 项目/API Key 已被拒绝访问 Live API（PolicyViolation）。"
                + "请到 aistudio.google.com/apikey 用新 Google 账号或新项目创建 AIza 开头的 Key，"
                + "或在 Google AI Studio / Cloud 控制台检查项目是否被限制。";
        }

        if (lower.Contains("invalidpayloaddata") || lower.Contains("unknown name"))
        {
            return "Gemini 协议字段不被接受：" + reason;
        }

        if (lower.Contains("api key") || lower.Contains("unauthenticated") || lower.Contains("401"))
        {
            return "Gemini API Key 无效，请重新创建并粘贴保存。";
        }

        return reason;
    }

    private void HandleServerEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("setupComplete", out _))
            {
                _setupComplete = true;
                LiveTranslationLog.Write("gemini setupComplete");
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.ToString() : "error";
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : json;
                ReportFailure(code, message);
                return;
            }

            if (!root.TryGetProperty("serverContent", out var content))
            {
                return;
            }

            // 优先输出（译文）转写作为字幕；输入转写仅作兜底。
            if (content.TryGetProperty("outputTranscription", out var outTx)
                && outTx.TryGetProperty("text", out var outText))
            {
                EmitSubtitle(outText.GetString());
            }
            else if (content.TryGetProperty("inputTranscription", out var inTx)
                     && inTx.TryGetProperty("text", out var inText))
            {
                // 输入是源语言，通常不直接当中文字幕；仅在完全没有输出时提示进度。
                var src = inText.GetString();
                if (!string.IsNullOrWhiteSpace(src))
                {
                    Progress?.Invoke("正在翻译…");
                }
            }

            if (_playDubbing
                && content.TryGetProperty("modelTurn", out var turn)
                && turn.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var inline)
                        && !part.TryGetProperty("inline_data", out inline))
                    {
                        continue;
                    }

                    if (!inline.TryGetProperty("data", out var dataElement))
                    {
                        continue;
                    }

                    var b64 = dataElement.GetString();
                    if (string.IsNullOrEmpty(b64))
                    {
                        continue;
                    }

                    try
                    {
                        var pcm = Convert.FromBase64String(b64);
                        if (pcm.Length > 0)
                        {
                            DubbingAudio?.Invoke(pcm);
                        }
                    }
                    catch (FormatException)
                    {
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            LiveTranslationLog.Write("gemini parse: " + exception.Message);
        }
    }

    private void EmitSubtitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Gemini 转写有时给累计全文，有时给增量；做简单差分。
        var incoming = text.Trim();
        string delta;
        if (!string.IsNullOrEmpty(_subtitleCarry) && incoming.StartsWith(_subtitleCarry, StringComparison.Ordinal))
        {
            delta = incoming[_subtitleCarry.Length..].TrimStart();
        }
        else if (!string.IsNullOrEmpty(_subtitleCarry) && _subtitleCarry.StartsWith(incoming, StringComparison.Ordinal))
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
            return;
        }

        if (!delta.EndsWith('。') && !delta.EndsWith('！') && !delta.EndsWith('？')
            && !delta.EndsWith('.') && !delta.EndsWith('!') && !delta.EndsWith('?')
            && !delta.EndsWith(' '))
        {
            delta += " ";
        }

        SubtitleDelta?.Invoke(delta);
    }

    private byte[] Resample24kTo16k(ReadOnlySpan<byte> pcm24k)
    {
        // 3:2 抽取（24k→16k）：每 3 个输入采样 → 2 个输出。
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
                // 线性：s0, (2*s1+s2)/3 ≈ 两档
                var s0 = combined[i];
                var s1 = combined[i + 1];
                var s2 = combined[i + 2];
                var a = s0;
                var b = (short)((s1 + s2) / 2);
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

    private async Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket
                    .SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
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
            LiveTranslationLog.Write($"gemini fail {code}: {message}");
            Failed?.Invoke(code, message);
        }
    }

    private async Task AbortAsync()
    {
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
