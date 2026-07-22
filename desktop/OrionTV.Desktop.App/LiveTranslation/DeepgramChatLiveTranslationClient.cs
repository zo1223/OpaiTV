using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// Deepgram Nova-3 流式 STT + 中转站 Chat 译中文（仅字幕，无配音）。
/// </summary>
internal sealed class DeepgramChatLiveTranslationClient : ILiveTranslationSession
{
    private static readonly HttpClient Http = CreateHttpClient();
    private const int MaxServerMessageBytes = 1_048_576;

    private readonly string _deepgramApiKey;
    private readonly string _chatApiKey;
    private readonly string _chatModel;
    private readonly string _chatBaseUrl;
    private readonly string _sttModel;
    private readonly Pcm16TranslationFrameBuffer _frameBuffer =
        new(DeepgramSttProtocol.ChunkBytes);
    private readonly object _frameSync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<string> _englishQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetimeCts;
    private Channel<byte[]>? _audioFrames;
    private Task? _audioSendTask;
    private Task? _receiveTask;
    private Task? _translateTask;
    private int _stopping;
    private bool _disposed;
    private bool _ready;
    private string? _lastEnglish;
    private string? _lastChinese;
    private int _transientChatFailures;
    private long _audioBytesSent;
    private string? _latestInterim;
    private short[] _resampleHistory = Array.Empty<short>();
    private readonly object _resampleSync = new();

    public DeepgramChatLiveTranslationClient(
        string deepgramApiKey,
        string chatApiKey,
        string? chatModel,
        string? chatBaseUrl,
        string? sttModel = null)
    {
        _deepgramApiKey = deepgramApiKey.Trim();
        _chatApiKey = chatApiKey.Trim();
        _chatModel = string.IsNullOrWhiteSpace(chatModel)
            ? "doubao-seed-2-0-pro-260215"
            : chatModel.Trim();
        _chatBaseUrl = RealtimeTranslationProtocol.NormalizeBaseUrl(chatBaseUrl);
        _sttModel = DeepgramSttProtocol.NormalizeModel(sttModel);
    }

    public event Action<string>? SubtitleDelta;
    public event Action<RealtimeTranslationConnectionState>? StateChanged;
    public event Action<string?, string?>? Failed;
    public event Action<string>? Progress;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null)
        {
            throw new InvalidOperationException("Deepgram 字幕会话已经启动");
        }

        if (!DeepgramSttProtocol.LooksLikeApiKey(_deepgramApiKey))
        {
            throw new InvalidOperationException(
                "Deepgram API Key 无效。请到 console.deepgram.com 复制 Key（一串英文/数字），不要粘贴中文说明。");
        }

        if (string.IsNullOrWhiteSpace(_chatApiKey))
        {
            throw new InvalidOperationException("请配置中转站 Chat API Key（用于把英文译成中文）");
        }

        LiveTranslationLog.Write(
            $"deepgram start model={_sttModel} keyLen={_deepgramApiKey.Length} chatModel={_chatModel} base={_chatBaseUrl}");

        StateChanged?.Invoke(RealtimeTranslationConnectionState.Connecting);
        Progress?.Invoke("正在连接 Deepgram Nova-3…");
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // Deepgram 文档：Authorization: Token <KEY>
        socket.Options.SetRequestHeader("Authorization", "Token " + _deepgramApiKey);
        _socket = socket;

        try
        {
            var uri = DeepgramSttProtocol.BuildListenWebSocketUri(_sttModel);
            LiveTranslationLog.Write("deepgram connecting " + uri);
            await socket.ConnectAsync(uri, _lifetimeCts.Token).ConfigureAwait(false);
            LiveTranslationLog.Write($"deepgram ws connected state={socket.State} model={_sttModel}");

            _ready = true;
            _receiveTask = ReceiveLoopAsync(socket, _lifetimeCts.Token);
            _audioSendTask = SendAudioLoopAsync(_audioFrames.Reader, _lifetimeCts.Token);
            _translateTask = TranslateLoopAsync(_lifetimeCts.Token);

            StateChanged?.Invoke(RealtimeTranslationConnectionState.Listening);
            Progress?.Invoke("Deepgram 已连接，正在识别英语并译成中文…");
        }
        catch (Exception exception)
        {
            LiveTranslationLog.Write("deepgram start FAIL: " + exception.Message);
            await AbortAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "Deepgram 连接失败：" + exception.Message
                + "（需能访问 api.deepgram.com；国内网络可能需要代理）",
                exception);
        }
    }

    /// <summary>接收 24kHz PCM16 mono，重采样到 16kHz 后送 Deepgram。</summary>
    public void AppendPcm16(ReadOnlySpan<byte> audio24k)
    {
        var writer = _audioFrames?.Writer;
        if (writer is null || Volatile.Read(ref _stopping) != 0 || !_ready)
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
                await SendBinaryAsync(frame, cancellationToken).ConfigureAwait(false);
                var total = Interlocked.Add(ref _audioBytesSent, frame.Length);
                // 每约 5 秒记一次，证明抽流/上传在工作
                if (total % (DeepgramSttProtocol.InputSampleRate * 2 * 5) < frame.Length)
                {
                    LiveTranslationLog.Write($"deepgram audio sent ~{total / 32000.0:F1}s");
                }
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
                            $"deepgram close {socket.CloseStatus}: {socket.CloseStatusDescription}");
                        if (Volatile.Read(ref _stopping) == 0)
                        {
                            ReportFailure(
                                socket.CloseStatus?.ToString() ?? "closed",
                                socket.CloseStatusDescription ?? "Deepgram 连接关闭");
                        }

                        StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxServerMessageBytes)
                    {
                        throw new InvalidDataException("Deepgram 消息过大");
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text
                    && result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                HandleServerJson(json);
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
            _englishQueue.Writer.TryComplete();
        }
    }

    private void HandleServerJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String)
            {
                var type = typeEl.GetString();
                if (string.Equals(type, "Metadata", StringComparison.OrdinalIgnoreCase))
                {
                    LiveTranslationLog.Write("deepgram Metadata received");
                    return;
                }

                if (string.Equals(type, "UtteranceEnd", StringComparison.OrdinalIgnoreCase))
                {
                    // 句末：把当前 interim 冲进翻译队列
                    if (!string.IsNullOrWhiteSpace(_latestInterim))
                    {
                        EnqueueEnglish(_latestInterim!, force: true);
                        _latestInterim = null;
                    }

                    return;
                }

                if (string.Equals(type, "SpeechStarted", StringComparison.OrdinalIgnoreCase))
                {
                    Progress?.Invoke("检测到英语口播…");
                    return;
                }
            }

            // 错误包
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : err.ToString();
                ReportFailure("deepgram_error", msg);
                return;
            }

            if (root.TryGetProperty("type", out var errType)
                && errType.ValueKind == JsonValueKind.String
                && string.Equals(errType.GetString(), "Error", StringComparison.OrdinalIgnoreCase))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : json;
                ReportFailure("deepgram_error", msg);
                return;
            }

            // Results 包
            if (!root.TryGetProperty("channel", out var channel))
            {
                return;
            }

            if (!channel.TryGetProperty("alternatives", out var alts)
                || alts.ValueKind != JsonValueKind.Array
                || alts.GetArrayLength() == 0)
            {
                return;
            }

            var alt0 = alts[0];
            if (!alt0.TryGetProperty("transcript", out var tEl))
            {
                return;
            }

            var transcript = tEl.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            var isFinal = (root.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("speech_final", out var sf) && sf.ValueKind == JsonValueKind.True);

            if (!isFinal)
            {
                _latestInterim = transcript;
                Progress?.Invoke("识别中: " + Truncate(transcript, 48));
                // 连续新闻口播可能长时间无 is_final：每 2.5s 用当前 interim 冲一次
                return;
            }

            _latestInterim = null;
            EnqueueEnglish(transcript, force: true);
        }
        catch (JsonException exception)
        {
            LiveTranslationLog.Write("deepgram parse: " + exception.Message);
        }
    }

    private void EnqueueEnglish(string transcript, bool force)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        if (!force
            && string.Equals(transcript, _lastEnglish, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 若只是 interim 扩展了上一次已送出的句子，允许更新（force 或更长）
        if (!force
            && _lastEnglish is not null
            && transcript.StartsWith(_lastEnglish, StringComparison.OrdinalIgnoreCase)
            && transcript.Length - _lastEnglish.Length < 8)
        {
            return;
        }

        _lastEnglish = transcript;
        LiveTranslationLog.Write("deepgram EN: " + transcript);
        Progress?.Invoke("已识别英语，正在翻译…");
        _englishQueue.Writer.TryWrite(transcript);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private async Task TranslateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var english in _englishQueue.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                // 积压时只译最新一句
                var latest = english;
                while (_englishQueue.Reader.TryRead(out var newer))
                {
                    latest = newer;
                }

                try
                {
                    var chinese = await TranslateToChineseAsync(latest, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(chinese))
                    {
                        Progress?.Invoke("翻译暂时失败，继续识别中…");
                        continue;
                    }

                    chinese = chinese.Trim();
                    if (string.Equals(chinese, _lastChinese, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _lastChinese = chinese;
                    if (!chinese.EndsWith('。') && !chinese.EndsWith('！') && !chinese.EndsWith('？')
                        && !chinese.EndsWith('.') && !chinese.EndsWith('!') && !chinese.EndsWith('?'))
                    {
                        chinese += "。";
                    }

                    LiveTranslationLog.Write("deepgram ZH: " + chinese);
                    SubtitleDelta?.Invoke(chinese + " ");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LiveTranslationLog.Write("deepgram chat: " + exception.Message);
                    Progress?.Invoke("翻译异常，继续识别中…");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<string?> TranslateToChineseAsync(string english, CancellationToken cancellationToken)
    {
        // 与 ChatLiveTranslationClient 一致：关 thinking，短超时
        var payloadObject = new Dictionary<string, object?>
        {
            ["model"] = _chatModel,
            ["temperature"] = 0.1,
            ["max_tokens"] = 80,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "你是英语直播同声字幕翻译。把用户给出的英语口播内容译成简洁通顺的简体中文。"
                        + "口播识别可能不完整或有错词，仍请尽力译出最可能的意思。"
                        + "只输出一行译文，不要解释，不要道歉，不要说无法翻译，不要加引号，不要保留英文原文。"
                },
                new { role = "user", content = english },
            },
            ["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" },
        };
        var payload = JsonSerializer.Serialize(payloadObject);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            RealtimeTranslationProtocol.BuildChatCompletionsUri(_chatBaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chatApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LiveTranslationLog.Write($"deepgram chat HTTP {(int)response.StatusCode}: {body[..Math.Min(160, body.Length)]}");
                _transientChatFailures++;
                if (_transientChatFailures >= 5)
                {
                    Failed?.Invoke(((int)response.StatusCode).ToString(), "中转 Chat 连续失败");
                }

                return null;
            }

            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            _transientChatFailures = 0;
            return content?.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _transientChatFailures++;
            LiveTranslationLog.Write("deepgram chat timeout");
            return null;
        }
    }

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
            LiveTranslationLog.Write($"deepgram fail {code}: {message}");
            Failed?.Invoke(code, message);
        }
    }

    private async Task AbortAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _audioFrames?.Writer.TryComplete();
        _englishQueue.Writer.TryComplete();

        // Deepgram 优雅结束：发 CloseStream JSON（必须是 Text 帧）
        try
        {
            if (_socket is { State: WebSocketState.Open })
            {
                var close = Encoding.UTF8.GetBytes("""{"type":"CloseStream"}""");
                await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await _socket
                        .SendAsync(close, WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _sendGate.Release();
                }
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
        await ObserveAsync(_translateTask).ConfigureAwait(false);
        _audioSendTask = null;
        _receiveTask = null;
        _translateTask = null;
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpaiTV-Desktop/1.3.13");
        return client;
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
        _englishQueue.Writer.TryComplete();
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
