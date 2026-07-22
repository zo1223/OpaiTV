namespace OrionTV.Desktop.Core;

/// <summary>Deepgram 流式 Speech-to-Text（Nova-3）协议约定。</summary>
public static class DeepgramSttProtocol
{
    public const string DefaultModel = "nova-3";
    public const string DefaultLanguage = "en";
    public const string OfficialWsHost = "api.deepgram.com";
    public const int InputSampleRate = 16_000;
    public const int ChannelCount = 1;
    public const int BytesPerSample = 2;
    /// <summary>约 100ms 一块，兼顾延迟与开销。</summary>
    public const int ChunkDurationMs = 100;
    public const int ChunkBytes = InputSampleRate * ChannelCount * BytesPerSample * ChunkDurationMs / 1000;

    public static bool IsDeepgramProvider(string? provider) =>
        string.Equals(provider, "deepgram", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "nova-3", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "nova3", StringComparison.OrdinalIgnoreCase);

    public static bool IsDeepgramModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var m = model.Trim();
        return m.Contains("nova-3", StringComparison.OrdinalIgnoreCase)
            || m.Contains("nova3", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("nova-", StringComparison.OrdinalIgnoreCase)
            || m.Contains("flux", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !IsDeepgramModel(model))
        {
            // Chat 模型名（doubao 等）不应误当 Deepgram model
            return DefaultModel;
        }

        return model.Trim();
    }

    public static Uri BuildListenWebSocketUri(string? model = null, string language = DefaultLanguage)
    {
        var m = NormalizeModel(model);
        // 直播连续口播几乎没有长静音：必须 interim_results + utterance_end，
        // 否则可能长时间收不到 is_final，表现为「连上了但从不出字幕」。
        var q =
            $"model={Uri.EscapeDataString(m)}"
            + $"&language={Uri.EscapeDataString(language)}"
            + "&encoding=linear16"
            + $"&sample_rate={InputSampleRate}"
            + $"&channels={ChannelCount}"
            + "&punctuate=true"
            + "&smart_format=true"
            + "&interim_results=true"
            + "&endpointing=500"
            + "&utterance_end_ms=1200"
            + "&vad_events=true";
        return new Uri($"wss://{OfficialWsHost}/v1/listen?{q}", UriKind.Absolute);
    }

    /// <summary>粗检 Key 形态，避免把中文说明/空格粘进凭据管理器。</summary>
    public static bool LooksLikeApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var k = key.Trim();
        if (k.Length < 20 || k.Length > 200)
        {
            return false;
        }

        // Deepgram key 通常是 hex/base64 风格 ASCII，不应含中文或空白。
        foreach (var ch in k)
        {
            if (char.IsWhiteSpace(ch) || ch > 127)
            {
                return false;
            }

            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '=' or '+' or '/' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
