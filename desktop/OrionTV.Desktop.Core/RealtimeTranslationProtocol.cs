using System.Text;
using System.Text.Json;

namespace OrionTV.Desktop.Core;

public enum RealtimeTranslationEventKind
{
    Other,
    SessionCreated,
    SessionUpdated,
    SubtitleDelta,
    Closed,
    Error,
}

public sealed record RealtimeTranslationServerEvent(
    RealtimeTranslationEventKind Kind,
    string? Delta = null,
    long? ElapsedMs = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>OpenAI Realtime Translation WebSocket 的消息与音频格式约定。</summary>
public static class RealtimeTranslationProtocol
{
    public const string DefaultModel = "gpt-realtime-translate";
    public const string DefaultBaseUrl = "https://api.openai.com";
    public const string DefaultTargetLanguage = "zh";
    public const int SampleRate = 24_000;
    public const int ChannelCount = 1;
    public const int BytesPerSample = 2;
    public const int FrameDurationMs = 200;
    public const int FrameBytes = SampleRate * ChannelCount * BytesPerSample * FrameDurationMs / 1000;

    /// <summary>
    /// 仅带 realtime 的模型走 WebSocket 同传；其它模型（如中转站的 doubao/gpt）走「语音识别 + 聊天翻译」。
    /// </summary>
    public static bool UsesRealtimeProtocol(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return true;
        }

        return model.Contains("realtime", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化 API 根地址：去空白与末尾斜杠；空则官方 OpenAI。</summary>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultBaseUrl;
        }

        return baseUrl.Trim().TrimEnd('/');
    }

    /// <summary>得到带 /v1 的 HTTP API 根，兼容用户填 https://host 或 https://host/v1。</summary>
    public static string GetHttpApiRoot(string? baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/v1";
    }

    public static Uri BuildWebSocketUri(string? model, string? baseUrl = null)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        var httpRoot = GetHttpApiRoot(baseUrl);
        var wsRoot = httpRoot
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
        return new Uri(
            $"{wsRoot}/realtime/translations?model={Uri.EscapeDataString(resolvedModel)}",
            UriKind.Absolute);
    }

    public static Uri BuildChatCompletionsUri(string? baseUrl) =>
        new($"{GetHttpApiRoot(baseUrl)}/chat/completions", UriKind.Absolute);

    public static Uri BuildTranscriptionsUri(string? baseUrl) =>
        new($"{GetHttpApiRoot(baseUrl)}/audio/transcriptions", UriKind.Absolute);

    public static string CreateSessionUpdate(string? targetLanguage = null)
    {
        var language = string.IsNullOrWhiteSpace(targetLanguage)
            ? DefaultTargetLanguage
            : targetLanguage.Trim();

        return JsonSerializer.Serialize(new
        {
            type = "session.update",
            session = new
            {
                audio = new
                {
                    output = new { language }
                }
            }
        });
    }

    public static string CreateAudioAppend(ReadOnlySpan<byte> pcm16) =>
        JsonSerializer.Serialize(new
        {
            type = "session.input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16)
        });

    public static string CreateClose() => JsonSerializer.Serialize(new { type = "session.close" });

    public static RealtimeTranslationServerEvent ParseServerEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return new RealtimeTranslationServerEvent(RealtimeTranslationEventKind.Other);
            }

            return typeElement.GetString() switch
            {
                "session.created" => new RealtimeTranslationServerEvent(RealtimeTranslationEventKind.SessionCreated),
                "session.updated" => new RealtimeTranslationServerEvent(RealtimeTranslationEventKind.SessionUpdated),
                "session.output_transcript.delta" => new RealtimeTranslationServerEvent(
                    RealtimeTranslationEventKind.SubtitleDelta,
                    GetString(root, "delta"),
                    GetInt64(root, "elapsed_ms")),
                "session.closed" => new RealtimeTranslationServerEvent(RealtimeTranslationEventKind.Closed),
                "error" => ParseError(root),
                _ => new RealtimeTranslationServerEvent(RealtimeTranslationEventKind.Other)
            };
        }
        catch (JsonException)
        {
            return new RealtimeTranslationServerEvent(
                RealtimeTranslationEventKind.Error,
                ErrorCode: "invalid_server_event",
                ErrorMessage: "实时翻译服务返回了无法解析的数据");
        }
    }

    private static RealtimeTranslationServerEvent ParseError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
        {
            return new RealtimeTranslationServerEvent(
                RealtimeTranslationEventKind.Error,
                ErrorCode: "unknown_error",
                ErrorMessage: "实时翻译服务返回错误");
        }

        return new RealtimeTranslationServerEvent(
            RealtimeTranslationEventKind.Error,
            ErrorCode: GetString(error, "code") ?? GetString(error, "type"),
            ErrorMessage: GetString(error, "message"));
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;
}

/// <summary>把 LibVLC 的任意大小 PCM 回调整理成固定长度音频帧。</summary>
public sealed class Pcm16TranslationFrameBuffer
{
    private readonly byte[] _pending;
    private int _pendingCount;

    public Pcm16TranslationFrameBuffer(int frameBytes = 0)
    {
        var size = frameBytes > 0 ? frameBytes : RealtimeTranslationProtocol.FrameBytes;
        _pending = new byte[size];
    }

    public int PendingBytes => _pendingCount;

    public void Append(ReadOnlySpan<byte> audio, Action<byte[]> emitFrame)
    {
        ArgumentNullException.ThrowIfNull(emitFrame);

        while (!audio.IsEmpty)
        {
            var copyLength = Math.Min(_pending.Length - _pendingCount, audio.Length);
            audio[..copyLength].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += copyLength;
            audio = audio[copyLength..];

            if (_pendingCount != _pending.Length)
            {
                continue;
            }

            emitFrame((byte[])_pending.Clone());
            _pendingCount = 0;
        }
    }

    public void Reset()
    {
        Array.Clear(_pending);
        _pendingCount = 0;
    }
}

/// <summary>按服务端 delta 原样追加，并只保留适合两行字幕的最近文本。</summary>
public sealed class RollingSubtitleBuffer(int maxCharacters = 96)
{
    private static readonly char[] SentenceBoundaries = ['。', '！', '？', '!', '?', '；', ';', '\n'];
    private readonly StringBuilder _text = new();
    private readonly int _maxCharacters = Math.Max(24, maxCharacters);
    private bool _segmentCompleted;

    public string Text => _text.ToString().Trim();

    public string Append(string? delta)
    {
        if (!string.IsNullOrEmpty(delta))
        {
            // 实时服务会把同一句话拆成多个 delta，需要继续拼接；
            // 但上一段字幕结束后，下一段应替换旧字幕，而不是继续累积。
            if (_segmentCompleted)
            {
                _text.Clear();
                _segmentCompleted = false;
            }

            _text.Append(delta);
            TrimOldText();
            _segmentCompleted = EndsAtSentenceBoundary();
        }

        return Text;
    }

    public void Clear()
    {
        _text.Clear();
        _segmentCompleted = false;
    }

    private bool EndsAtSentenceBoundary()
    {
        for (var index = _text.Length - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(_text[index]))
            {
                continue;
            }

            return SentenceBoundaries.Contains(_text[index]);
        }

        return false;
    }

    private void TrimOldText()
    {
        if (_text.Length <= _maxCharacters)
        {
            return;
        }

        var minimumRemoval = _text.Length - _maxCharacters;
        var boundary = -1;
        for (var index = minimumRemoval; index < _text.Length; index++)
        {
            if (SentenceBoundaries.Contains(_text[index]))
            {
                boundary = index;
                break;
            }
        }

        var removal = boundary >= 0 ? boundary + 1 : minimumRemoval;
        _text.Remove(0, removal);
    }
}
