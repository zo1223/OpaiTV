using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// 中转站 / 普通 Chat Completions 模型用的直播字幕链路：
/// PCM 分段 →（可选远程 ASR）/ Windows 英语听写 → 聊天模型译成中文。
/// </summary>
internal sealed class ChatLiveTranslationClient : ILiveTranslationSession
{
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>约 4 秒一段，给本机听写更完整的口播上下文。</summary>
    private const int SegmentDurationMs = 4_000;

    private static readonly int SegmentBytes =
        RealtimeTranslationProtocol.SampleRate
        * RealtimeTranslationProtocol.ChannelCount
        * RealtimeTranslationProtocol.BytesPerSample
        * SegmentDurationMs
        / 1000;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly Pcm16TranslationFrameBuffer _segmentBuffer = new(SegmentBytes);
    private readonly object _bufferSync = new();
    private readonly Channel<byte[]> _segments = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private CancellationTokenSource? _lifetimeCts;
    private Task? _worker;
    private int _stopping;
    private bool _disposed;
    private string? _lastEnglish;
    private int _emptySpeechStreak;
    private int _speechSegmentCount;
    private int _recognizedCount;
    /// <summary>中转站无 whisper 时只探测一次，避免每段都卡在远程 ASR。</summary>
    private int _remoteAsrDisabled;
    private int _transientChatFailures;

    public ChatLiveTranslationClient(string apiKey, string model, string? baseUrl)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model)
            ? RealtimeTranslationProtocol.DefaultModel
            : model.Trim();
        _baseUrl = RealtimeTranslationProtocol.NormalizeBaseUrl(baseUrl);
    }

    public event Action<string>? SubtitleDelta;
    public event Action<RealtimeTranslationConnectionState>? StateChanged;
    public event Action<string?, string?>? Failed;
    public event Action<string>? Progress;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is not null)
        {
            throw new InvalidOperationException("聊天翻译会话已经启动");
        }

        StateChanged?.Invoke(RealtimeTranslationConnectionState.Connecting);
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 中转站通常无 whisper；先确认本机英语听写可用，避免空跑。
        if (!WindowsSpeechTranscriber.IsAvailable)
        {
            var detail = WindowsSpeechTranscriber.LastEngineError
                ?? "本机英语语音识别不可用，请安装 Windows 语音包 en-US";
            throw new InvalidOperationException(detail);
        }

        // 非官方 OpenAI 根地址基本没有 whisper 通道，跳过首次探测的空等。
        if (!IsOfficialOpenAiBase(_baseUrl))
        {
            Volatile.Write(ref _remoteAsrDisabled, 1);
        }

        _worker = ProcessLoopAsync(_lifetimeCts.Token);
        StateChanged?.Invoke(RealtimeTranslationConnectionState.Listening);
        return Task.CompletedTask;
    }

    public void AppendPcm16(ReadOnlySpan<byte> audio)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        lock (_bufferSync)
        {
            _segmentBuffer.Append(audio, frame => _segments.Writer.TryWrite(frame));
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var segment in _segments.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // 翻译慢时只保留队列里最新一段，避免字幕严重落后直播。
                var latest = segment;
                while (_segments.Reader.TryRead(out var newer))
                {
                    latest = newer;
                }

                try
                {
                    await ProcessSegmentAsync(latest, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // 单段失败不终止整条会话；连续致命错误才上报。
                    if (IsFatalTranslationException(exception))
                    {
                        if (Volatile.Read(ref _stopping) == 0)
                        {
                            Failed?.Invoke("transport_error", exception.Message);
                        }

                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                Failed?.Invoke("transport_error", exception.Message);
            }
        }
        finally
        {
            StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
        }
    }

    private async Task ProcessSegmentAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        if (IsMostlySilence(pcm))
        {
            LiveTranslationLog.Write($"seg silence bytes={pcm.Length}");
            return;
        }

        _speechSegmentCount++;
        if (_speechSegmentCount == 1)
        {
            Progress?.Invoke("已捕获音频，正在本机识别英语…");
        }

        LiveTranslationLog.Write(
            $"seg speech#{_speechSegmentCount} bytes={pcm.Length} remoteAsr={Volatile.Read(ref _remoteAsrDisabled) == 0}");

        var english = await TryRemoteTranscribeAsync(pcm, cancellationToken).ConfigureAwait(false)
            ?? WindowsSpeechTranscriber.TryTranscribePcm16(pcm, RealtimeTranslationProtocol.SampleRate);

        if (string.IsNullOrWhiteSpace(english))
        {
            _emptySpeechStreak++;
            LiveTranslationLog.Write(
                $"seg ASR empty streak={_emptySpeechStreak} err={WindowsSpeechTranscriber.LastEngineError}");

            if (_emptySpeechStreak == 3)
            {
                Progress?.Invoke("本机听写暂未识别到口播，仍在尝试…");
            }

            // 有音频但长期识别为空：明确提示，避免“几分钟无字幕且无错误”。
            if (_emptySpeechStreak >= 10 && _recognizedCount == 0)
            {
                Failed?.Invoke(
                    "no_speech",
                    "暂时识别不到英语口播。请确认频道是英语、音量正常，并已安装 Windows 英语语音包（en-US）");
            }
            else if (_emptySpeechStreak >= 8
                && !string.IsNullOrWhiteSpace(WindowsSpeechTranscriber.LastEngineError))
            {
                Failed?.Invoke("asr_unavailable", WindowsSpeechTranscriber.LastEngineError);
            }

            return;
        }

        _emptySpeechStreak = 0;
        _recognizedCount++;

        english = english.Trim();
        if (string.Equals(english, _lastEnglish, StringComparison.OrdinalIgnoreCase))
        {
            LiveTranslationLog.Write($"seg ASR dup: {english}");
            return;
        }

        _lastEnglish = english;
        LiveTranslationLog.Write($"seg EN: {english}");
        Progress?.Invoke("已识别英语，正在翻译…");
        var chinese = await TranslateToChineseAsync(english, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(chinese))
        {
            LiveTranslationLog.Write("seg ZH empty/fail");
            Progress?.Invoke("翻译暂时失败，继续识别中…");
            return;
        }

        chinese = chinese.Trim();
        if (!chinese.EndsWith('。') && !chinese.EndsWith('！') && !chinese.EndsWith('？')
            && !chinese.EndsWith('.') && !chinese.EndsWith('!') && !chinese.EndsWith('?'))
        {
            chinese += "。";
        }

        // 段落之间加空格，RollingSubtitleBuffer 按句号裁剪更自然。
        LiveTranslationLog.Write($"seg ZH: {chinese}");
        SubtitleDelta?.Invoke(chinese + " ");
    }

    private async Task<string?> TryRemoteTranscribeAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _remoteAsrDisabled) != 0)
        {
            return null;
        }

        // 很多中转站没有 whisper 通道；短超时探测，失败后永久回退本地 ASR。
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("en"), "language");
            form.Add(new StringContent("json"), "response_format");

            var wav = BuildWav(pcm, RealtimeTranslationProtocol.SampleRate);
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "segment.wav");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                RealtimeTranslationProtocol.BuildTranscriptionsUri(_baseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = form;

            using var response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Volatile.Write(ref _remoteAsrDisabled, 1);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            Volatile.Write(ref _remoteAsrDisabled, 1);
        }
        catch
        {
            Volatile.Write(ref _remoteAsrDisabled, 1);
            // 远程 ASR 不可用时走本地听写。
        }

        return null;
    }

    private async Task<string?> TranslateToChineseAsync(string english, CancellationToken cancellationToken)
    {
        // doubao-seed 等思考模型默认会写 reasoning_content，直播同传必须关掉思考，
        // 否则单次常要 25–40s，超过超时后就表现为“有识别、无字幕”。
        // 只发 thinking.type=disabled；部分兼容中转不接受额外的 reasoning_effort 字段。
        var payloadObject = new Dictionary<string, object?>
        {
            ["model"] = _model,
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

        // pro 思考模型在未关 thinking 时可达 30s+；给足余量，避免整段被丢。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            RealtimeTranslationProtocol.BuildChatCompletionsUri(_baseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        string body;
        var started = Environment.TickCount64;
        try
        {
            response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LiveTranslationLog.Write("chat timeout after 45s");
            RegisterTransientChatFailure("timeout", "翻译请求超时（模型过慢，可改用 lite 模型）");
            return null;
        }
        catch (HttpRequestException exception)
        {
            LiveTranslationLog.Write("chat network: " + exception.Message);
            RegisterTransientChatFailure("network_error", exception.Message);
            return null;
        }

        using (response)
        {
            var elapsed = Environment.TickCount64 - started;
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var message = ExtractErrorMessage(body) ?? body;
                LiveTranslationLog.Write($"chat HTTP {status} {elapsed}ms: {message}");
                if (IsFatalChatStatus(status, message))
                {
                    Failed?.Invoke(status.ToString(), message);
                }
                else
                {
                    RegisterTransientChatFailure(status.ToString(), message);
                }

                return null;
            }

            try
            {
                var content = ExtractChatContent(body);
                LiveTranslationLog.Write($"chat OK {elapsed}ms chars={(content?.Length ?? 0)}");
                if (string.IsNullOrWhiteSpace(content))
                {
                    RegisterTransientChatFailure("empty_content", "模型返回空译文");
                    return null;
                }

                _transientChatFailures = 0;
                return content.Trim();
            }
            catch (Exception exception)
            {
                LiveTranslationLog.Write("chat parse: " + exception.Message);
                RegisterTransientChatFailure("invalid_response", exception.Message);
                return null;
            }
        }
    }

    private static string? ExtractChatContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        if (message.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                var text = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            else if (contentElement.ValueKind == JsonValueKind.Array)
            {
                // 部分兼容接口把 content 拆成 [{type:text,text:...}]
                var parts = new StringBuilder();
                foreach (var part in contentElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                    {
                        parts.Append(textElement.GetString());
                    }
                    else if (part.ValueKind == JsonValueKind.String)
                    {
                        parts.Append(part.GetString());
                    }
                }

                var joined = parts.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined;
                }
            }
        }

        // 思考模型有时只把结果放在 reasoning 末尾——仍不要回传 reasoning，避免字幕刷屏。
        return null;
    }

    private static bool IsOfficialOpenAiBase(string baseUrl) =>
        baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

    private void RegisterTransientChatFailure(string code, string? message)
    {
        _transientChatFailures++;
        // 连续多次瞬时失败再报错，避免偶发抖动直接关掉中译。
        if (_transientChatFailures >= 5 && Volatile.Read(ref _stopping) == 0)
        {
            Failed?.Invoke(code, message);
        }
    }

    private static bool IsFatalChatStatus(int status, string? message)
    {
        if (status is 401 or 403)
        {
            return true;
        }

        var detail = $"{status} {message}".ToLowerInvariant();
        return detail.Contains("api key", StringComparison.Ordinal)
            || detail.Contains("invalid_api_key", StringComparison.Ordinal)
            || detail.Contains("authentication", StringComparison.Ordinal)
            || detail.Contains("permission", StringComparison.Ordinal)
            || (detail.Contains("model", StringComparison.Ordinal)
                && (detail.Contains("not found", StringComparison.Ordinal)
                    || detail.Contains("no available", StringComparison.Ordinal)));
    }

    private static bool IsFatalTranslationException(Exception exception)
    {
        var detail = exception.Message.ToLowerInvariant();
        return detail.Contains("api key", StringComparison.Ordinal)
            || detail.Contains("unauthorized", StringComparison.Ordinal)
            || detail.Contains("401", StringComparison.Ordinal);
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message))
                {
                    return message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsMostlySilence(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 4)
        {
            return true;
        }

        long sum = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += Math.Abs(sample);
        }

        // 直播二次解码音量常偏小；阈值放宽，避免把轻声口播当静音。
        return samples == 0 || sum / samples < 60;
    }

    private static byte[] BuildWav(ReadOnlySpan<byte> pcm16, int sampleRate)
    {
        using var stream = new MemoryStream(44 + pcm16.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm16.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm16.Length);
        writer.Write(pcm16);
        return stream.ToArray();
    }

    private static HttpClient CreateHttpClient()
    {
        // 单次请求超时由调用方 CancelAfter 控制；这里只做整体兜底。
        // pro 模型在思考未关闭时可达 40s+，HttpClient.Timeout 必须更大。
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpaiTV-Desktop/1.3.14");
        return client;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _segments.Writer.TryComplete();
        _lifetimeCts?.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        _worker = null;
        lock (_bufferSync)
        {
            _segmentBuffer.Reset();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _stopping, 1);
        _segments.Writer.TryComplete();
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        lock (_bufferSync)
        {
            _segmentBuffer.Reset();
        }
    }
}
