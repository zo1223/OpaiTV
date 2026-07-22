namespace OrionTV.Desktop.Core;

/// <summary>Google Gemini Live Translate（语音同传 + 字幕转写）协议约定。</summary>
public static class GeminiLiveTranslateProtocol
{
    public const string DefaultModel = "gemini-3.5-live-translate-preview";
    public const string DefaultTargetLanguage = "zh-Hans";
    public const string OfficialWsBase = "wss://generativelanguage.googleapis.com";
    public const int InputSampleRate = 16_000;
    public const int OutputSampleRate = 24_000;
    public const int ChannelCount = 1;
    public const int BytesPerSample = 2;
    /// <summary>官方建议约 100ms 一块送入。</summary>
    public const int ChunkDurationMs = 100;
    public const int ChunkBytes = InputSampleRate * ChannelCount * BytesPerSample * ChunkDurationMs / 1000;

    public static bool IsGeminiLiveTranslateModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var m = model.Trim();
        return m.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            && m.Contains("live-translate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析供应商：显式 gemini / openai；未填时按模型名推断。
    /// </summary>
    public static string ResolveProvider(string? provider, string? model)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var p = provider.Trim().ToLowerInvariant();
            if (p is "gemini" or "google")
            {
                return "gemini";
            }

            if (p is "doubao" or "volc" or "volcengine")
            {
                return "doubao";
            }

            if (p is "deepgram" or "nova-3" or "nova3")
            {
                return "deepgram";
            }

            if (p is "openai" or "chat" or "realtime")
            {
                return "openai";
            }
        }

        if (IsGeminiLiveTranslateModel(model))
        {
            return "gemini";
        }

        if (DoubaoAstProtocol.IsDoubaoAstModel(model))
        {
            return "doubao";
        }

        if (DeepgramSttProtocol.IsDeepgramModel(model))
        {
            return "deepgram";
        }

        return "openai";
    }

    public static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public static string NormalizeTargetLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? DefaultTargetLanguage : language.Trim();

    public static Uri BuildWebSocketUri(string apiKey)
    {
        // BidiGenerateContent Live API；key 走 query。
        var url =
            $"{OfficialWsBase}/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(apiKey)}";
        return new Uri(url, UriKind.Absolute);
    }

    public static string CreateSetupMessage(string model, string targetLanguage, bool echoTargetLanguage = false)
    {
        var resolvedModel = NormalizeModel(model);
        var language = NormalizeTargetLanguage(targetLanguage);
        // 模型字段官方示例为 models/{id}
        var modelPath = resolvedModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? resolvedModel
            : "models/" + resolvedModel;

        // 官方 Live API 参考：input/outputAudioTranscription 在 setup 顶层；
        // responseModalities 在 generationConfig 内。
        // Live Translate 的 translationConfig 按 live-translate 文档放 generationConfig。
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["setup"] = new Dictionary<string, object?>
            {
                ["model"] = modelPath,
                ["generationConfig"] = new Dictionary<string, object?>
                {
                    ["responseModalities"] = new[] { "AUDIO" },
                    ["translationConfig"] = new Dictionary<string, object?>
                    {
                        ["targetLanguageCode"] = language,
                        ["echoTargetLanguage"] = echoTargetLanguage
                    }
                },
                ["inputAudioTranscription"] = new Dictionary<string, object?>(),
                ["outputAudioTranscription"] = new Dictionary<string, object?>()
            }
        });
    }

    public static string CreateAudioChunkMessage(ReadOnlySpan<byte> pcm16kMono)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            realtimeInput = new
            {
                audio = new
                {
                    data = Convert.ToBase64String(pcm16kMono),
                    mimeType = "audio/pcm;rate=16000"
                }
            }
        });
    }
}
