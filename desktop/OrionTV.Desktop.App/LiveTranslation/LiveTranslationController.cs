using System.IO;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

internal enum LiveTranslationState
{
    Connecting,
    Listening,
    Error,
    Stopped,
}

/// <summary>协调伴随音频解码器与翻译会话（Gemini / OpenAI Realtime / 中转站 Chat）的生命周期。</summary>
internal sealed class LiveTranslationController : IAsyncDisposable, IDisposable
{
    private LibVlcAudioTap? _audioTap;
    private ILiveTranslationSession? _session;
    private PcmWavePlayer? _wavePlayer;
    private bool _disposed;

    public event Action<string>? SubtitleDelta;
    public event Action<LiveTranslationState, string?>? StateChanged;

    public async Task StartAsync(
        string streamUrl,
        string apiKey,
        string model,
        string? baseUrl = null,
        string? provider = null,
        bool enableDubbing = true,
        string? doubaoAppKey = null,
        string? doubaoResourceId = null,
        string? chatApiKey = null,
        string? chatModel = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null || _audioTap is not null)
        {
            throw new InvalidOperationException("直播翻译已经启动");
        }

        StateChanged?.Invoke(LiveTranslationState.Connecting, null);
        var session = CreateSession(
            apiKey,
            model,
            baseUrl,
            provider,
            enableDubbing,
            doubaoAppKey,
            doubaoResourceId,
            chatApiKey,
            chatModel);
        session.SubtitleDelta += OnSubtitleDelta;
        session.StateChanged += OnConnectionStateChanged;
        session.Failed += OnClientFailed;
        session.Progress += OnProgress;
        if (enableDubbing)
        {
            if (session is GeminiLiveTranslateClient gemini)
            {
                _wavePlayer = new PcmWavePlayer(GeminiLiveTranslateProtocol.OutputSampleRate);
                gemini.DubbingAudio += OnDubbingAudio;
            }
            else if (session is DoubaoAstTranslateClient doubao)
            {
                _wavePlayer = new PcmWavePlayer(DoubaoAstProtocol.OutputSampleRate);
                doubao.DubbingAudio += OnDubbingAudio;
            }
        }

        _session = session;

        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);

            // 专用 LibVLC 抽流，避免与主播放器共享实例导致回调无数据。
            var audioTap = new LibVlcAudioTap();
            audioTap.AudioAvailable += OnAudioAvailable;
            audioTap.PlaybackFailed += OnAudioPlaybackFailed;
            _audioTap = audioTap;
            audioTap.Start(streamUrl);
            LiveTranslationLog.Write(
                $"audio-tap started url={streamUrl} provider={GeminiLiveTranslateProtocol.ResolveProvider(provider, model)} model={model}");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void SetPaused(bool paused)
    {
        _audioTap?.SetPaused(paused);
        if (paused)
        {
            _wavePlayer?.Clear();
        }
    }

    private static ILiveTranslationSession CreateSession(
        string apiKey,
        string model,
        string? baseUrl,
        string? provider,
        bool enableDubbing,
        string? doubaoAppKey,
        string? doubaoResourceId,
        string? chatApiKey,
        string? chatModel)
    {
        var resolved = GeminiLiveTranslateProtocol.ResolveProvider(provider, model);
        if (resolved == "gemini")
        {
            return new GeminiLiveTranslateClient(
                apiKey,
                string.IsNullOrWhiteSpace(model)
                    ? GeminiLiveTranslateProtocol.DefaultModel
                    : model,
                GeminiLiveTranslateProtocol.DefaultTargetLanguage,
                playDubbing: enableDubbing);
        }

        if (resolved == "doubao")
        {
            if (string.IsNullOrWhiteSpace(doubaoAppKey))
            {
                throw new InvalidOperationException("请在设置中填写豆包 APP Key");
            }

            return new DoubaoAstTranslateClient(
                doubaoAppKey,
                apiKey,
                doubaoResourceId,
                DoubaoAstProtocol.DefaultSourceLanguage,
                DoubaoAstProtocol.DefaultTargetLanguage,
                enableDubbing ? "s2s" : "s2t",
                playDubbing: enableDubbing);
        }

        if (resolved == "deepgram")
        {
            var chatKey = string.IsNullOrWhiteSpace(chatApiKey) ? apiKey : chatApiKey;
            if (string.IsNullOrWhiteSpace(chatKey))
            {
                throw new InvalidOperationException("Deepgram 字幕需要中转 Chat API Key（OpenAI 兼容）用于译中文");
            }

            // model 文本框在 Deepgram 模式下存 Chat 模型（如 doubao-…）；STT 固定 nova-3。
            return new DeepgramChatLiveTranslationClient(
                deepgramApiKey: apiKey,
                chatApiKey: chatKey,
                chatModel: string.IsNullOrWhiteSpace(chatModel) ? model : chatModel,
                chatBaseUrl: baseUrl,
                sttModel: DeepgramSttProtocol.DefaultModel);
        }

        if (RealtimeTranslationProtocol.UsesRealtimeProtocol(model))
        {
            return new RealtimeTranslationClient(
                apiKey,
                model,
                RealtimeTranslationProtocol.DefaultTargetLanguage,
                baseUrl);
        }

        return new ChatLiveTranslationClient(apiKey, model, baseUrl);
    }

    private void OnAudioAvailable(ReadOnlyMemory<byte> audio) => _session?.AppendPcm16(audio.Span);

    private void OnDubbingAudio(ReadOnlyMemory<byte> pcm) => _wavePlayer?.Enqueue(pcm.Span);

    private void OnAudioPlaybackFailed() =>
        StateChanged?.Invoke(LiveTranslationState.Error, "无法读取当前频道的音频");

    private void OnSubtitleDelta(string delta) => SubtitleDelta?.Invoke(delta);

    private void OnProgress(string message) =>
        StateChanged?.Invoke(LiveTranslationState.Listening, message);

    private void OnConnectionStateChanged(RealtimeTranslationConnectionState state)
    {
        if (state == RealtimeTranslationConnectionState.Listening)
        {
            StateChanged?.Invoke(LiveTranslationState.Listening, null);
        }
        else if (state == RealtimeTranslationConnectionState.Closed)
        {
            StateChanged?.Invoke(LiveTranslationState.Stopped, null);
        }
    }

    private void OnClientFailed(string? code, string? message)
    {
        _audioTap?.SetPaused(true);
        _wavePlayer?.Clear();
        StateChanged?.Invoke(LiveTranslationState.Error, FormatError(code, message));
    }

    private static string FormatError(string? code, string? message)
    {
        var detail = $"{code} {message}".ToLowerInvariant();
        if (detail.Contains("policyviolation")
            || detail.Contains("denied access")
            || detail.Contains("permission_denied")
            || detail.Contains("project has been denied"))
        {
            return message ?? "Google 项目/API Key 被拒绝访问，请换 AI Studio 新项目创建 AIza Key";
        }

        if (detail.Contains("auth") || detail.Contains("api key") || detail.Contains("401")
            || detail.Contains("api_key") || detail.Contains("permission_denied"))
        {
            return "API Key 无效或已失效（Gemini 请到 AI Studio 重新创建）";
        }

        if (detail.Contains("rate_limit") || detail.Contains("quota") || detail.Contains("billing") || detail.Contains("429"))
        {
            return "实时翻译额度或速率已达上限";
        }

        if (detail.Contains("model") && (detail.Contains("permission") || detail.Contains("not found") || detail.Contains("no available")))
        {
            return "当前 API Key 无权使用该翻译模型，或中转站未配置此模型";
        }

        if (detail.Contains("en-us") || detail.Contains("speech") || detail.Contains("语音识别") || detail.Contains("asr"))
        {
            return "本机英语语音识别不可用，请安装 Windows 语音包（en-US）";
        }

        if (detail.Contains("no_speech") || detail.Contains("empty"))
        {
            return message ?? "暂时识别不到英语口播，请换英语频道或调高音量后重试";
        }

        if (detail.Contains("deepgram") || detail.Contains("nova-3") || detail.Contains("invalid credentials")
            || detail.Contains("unauthorized"))
        {
            return message ?? "Deepgram 连接失败，请检查 API Key 与网络（需访问 api.deepgram.com）";
        }

        if (detail.Contains("doubao") || detail.Contains("openspeech") || detail.Contains("volc")
            || detail.Contains("app key") || detail.Contains("access key") || detail.Contains("10053"))
        {
            return message ?? "豆包同传连接失败，请检查 APP Key / Access Key 是否开通同声传译 2.0";
        }

        if (detail.Contains("gemini") || detail.Contains("websocket") || detail.Contains("setup"))
        {
            return message ?? "Gemini 同传连接失败，请检查网络与 API Key";
        }

        return "实时翻译连接中断，请关闭后重试";
    }

    public async Task StopAsync()
    {
        var audioTap = _audioTap;
        _audioTap = null;
        if (audioTap is not null)
        {
            LiveTranslationLog.Write($"audio-tap stop totalBytes={audioTap.TotalBytes}");
            audioTap.AudioAvailable -= OnAudioAvailable;
            audioTap.PlaybackFailed -= OnAudioPlaybackFailed;
            audioTap.Dispose();
        }

        var session = _session;
        _session = null;
        if (session is not null)
        {
            if (session is GeminiLiveTranslateClient gemini)
            {
                gemini.DubbingAudio -= OnDubbingAudio;
            }
            else if (session is DoubaoAstTranslateClient doubao)
            {
                doubao.DubbingAudio -= OnDubbingAudio;
            }

            session.SubtitleDelta -= OnSubtitleDelta;
            session.StateChanged -= OnConnectionStateChanged;
            session.Failed -= OnClientFailed;
            session.Progress -= OnProgress;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _wavePlayer?.Dispose();
        _wavePlayer = null;

        StateChanged?.Invoke(LiveTranslationState.Stopped, null);
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
        _audioTap?.Dispose();
        _audioTap = null;
        _wavePlayer?.Dispose();
        _wavePlayer = null;
        _session?.Dispose();
        _session = null;
    }
}

/// <summary>Realtime WebSocket / Gemini Live / Chat HTTP 翻译会话的统一接口。</summary>
internal interface ILiveTranslationSession : IAsyncDisposable, IDisposable
{
    event Action<string>? SubtitleDelta;
    event Action<RealtimeTranslationConnectionState>? StateChanged;
    event Action<string?, string?>? Failed;
    event Action<string>? Progress;

    Task StartAsync(CancellationToken cancellationToken = default);
    void AppendPcm16(ReadOnlySpan<byte> audio);
}

/// <summary>轻量诊断日志，写入 %LocalAppData%/OrionTV/live-translate.log。</summary>
internal static class LiveTranslationLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrionTV",
        "live-translate.log");

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // 日志失败不影响主流程。
        }
    }
}
