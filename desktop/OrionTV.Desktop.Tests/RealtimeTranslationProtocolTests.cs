using System.Text;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class RealtimeTranslationProtocolTests
{
    [Fact]
    public void SessionUpdate_sets_translation_output_to_Chinese()
    {
        using var json = JsonDocument.Parse(RealtimeTranslationProtocol.CreateSessionUpdate());

        Assert.Equal("session.update", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "zh",
            json.RootElement
                .GetProperty("session")
                .GetProperty("audio")
                .GetProperty("output")
                .GetProperty("language")
                .GetString());
    }

    [Fact]
    public void AudioAppend_uses_translation_event_and_base64_pcm()
    {
        byte[] pcm = [0, 1, 2, 255];

        using var json = JsonDocument.Parse(RealtimeTranslationProtocol.CreateAudioAppend(pcm));

        Assert.Equal("session.input_audio_buffer.append", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(pcm, Convert.FromBase64String(json.RootElement.GetProperty("audio").GetString()!));
    }

    [Fact]
    public void ParseServerEvent_reads_translated_subtitle_delta_without_inserting_spaces()
    {
        var parsed = RealtimeTranslationProtocol.ParseServerEvent(
            """{"type":"session.output_transcript.delta","delta":"你好","elapsed_ms":1200}""");

        Assert.Equal(RealtimeTranslationEventKind.SubtitleDelta, parsed.Kind);
        Assert.Equal("你好", parsed.Delta);
        Assert.Equal(1200, parsed.ElapsedMs);
    }

    [Fact]
    public void ParseServerEvent_reads_structured_errors()
    {
        var parsed = RealtimeTranslationProtocol.ParseServerEvent(
            """{"type":"error","error":{"type":"invalid_request_error","code":"invalid_event","message":"Bad event"}}""");

        Assert.Equal(RealtimeTranslationEventKind.Error, parsed.Kind);
        Assert.Equal("invalid_event", parsed.ErrorCode);
        Assert.Equal("Bad event", parsed.ErrorMessage);
    }

    [Fact]
    public void FrameBuffer_emits_exact_200ms_frames_and_retains_remainder()
    {
        var buffer = new Pcm16TranslationFrameBuffer();
        var frames = new List<byte[]>();
        var input = Enumerable.Range(0, RealtimeTranslationProtocol.FrameBytes * 2 + 137)
            .Select(index => (byte)(index % 251))
            .ToArray();

        buffer.Append(input.AsSpan(0, 4000), frames.Add);
        buffer.Append(input.AsSpan(4000), frames.Add);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(RealtimeTranslationProtocol.FrameBytes, frame.Length));
        Assert.Equal(input.AsSpan(0, RealtimeTranslationProtocol.FrameBytes).ToArray(), frames[0]);
        Assert.Equal(137, buffer.PendingBytes);
    }

    [Fact]
    public void RollingSubtitleBuffer_appends_deltas_verbatim_and_bounds_old_sentences()
    {
        var subtitles = new RollingSubtitleBuffer(24);

        Assert.Equal("你好", subtitles.Append("你好"));
        Assert.Equal("你好，世界。", subtitles.Append("，世界。"));
        subtitles.Append("这是第二句话，很长很长。再来第三句话，也很长很长。最后一句");

        Assert.DoesNotContain("你好", subtitles.Text);
        Assert.EndsWith("最后一句", subtitles.Text);
        Assert.True(Encoding.UTF8.GetByteCount(subtitles.Text) > 0);
    }

    [Fact]
    public void RollingSubtitleBuffer_replaces_completed_subtitle_when_next_segment_arrives()
    {
        var subtitles = new RollingSubtitleBuffer();

        Assert.Equal("第一句。", subtitles.Append("第一句。 "));
        Assert.Equal("第二句", subtitles.Append("第二句"));
        Assert.DoesNotContain("第一句", subtitles.Text);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("gpt-realtime-translate", true)]
    [InlineData("gpt-4o-realtime-preview", true)]
    [InlineData("doubao-seed-2-0-pro-260215", false)]
    [InlineData("gpt-4o-mini", false)]
    public void UsesRealtimeProtocol_detects_realtime_models(string? model, bool expected) =>
        Assert.Equal(expected, RealtimeTranslationProtocol.UsesRealtimeProtocol(model));

    [Fact]
    public void BuildWebSocketUri_defaults_to_openai_and_supports_relay_host()
    {
        var official = RealtimeTranslationProtocol.BuildWebSocketUri("gpt-realtime-translate");
        Assert.Equal(
            "wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate",
            official.ToString());

        var relay = RealtimeTranslationProtocol.BuildWebSocketUri(
            "gpt-realtime-translate",
            "https://relay.example.com/");
        Assert.Equal(
            "wss://relay.example.com/v1/realtime/translations?model=gpt-realtime-translate",
            relay.ToString());

        var withV1 = RealtimeTranslationProtocol.BuildChatCompletionsUri("https://relay.example.com/v1");
        Assert.Equal("https://relay.example.com/v1/chat/completions", withV1.ToString());
    }

    [Fact]
    public void FrameBuffer_accepts_custom_segment_size()
    {
        var buffer = new Pcm16TranslationFrameBuffer(100);
        var frames = new List<byte[]>();
        buffer.Append(new byte[250], frames.Add);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(100, frame.Length));
        Assert.Equal(50, buffer.PendingBytes);
    }
}

public class GeminiLiveTranslateProtocolTests
{
    [Theory]
    [InlineData("gemini-3.5-live-translate-preview", true)]
    [InlineData("models/gemini-3.5-live-translate-preview", true)]
    [InlineData("doubao-seed-2-0-pro-260215", false)]
    [InlineData("gpt-realtime-translate", false)]
    public void IsGeminiLiveTranslateModel_detects_names(string model, bool expected) =>
        Assert.Equal(expected, GeminiLiveTranslateProtocol.IsGeminiLiveTranslateModel(model));

    [Theory]
    [InlineData("gemini", "doubao", "gemini")]
    [InlineData("openai", "gemini-3.5-live-translate-preview", "openai")]
    [InlineData("", "gemini-3.5-live-translate-preview", "gemini")]
    [InlineData(null, "doubao-seed", "openai")]
    public void ResolveProvider_prefers_explicit_then_model(string? provider, string model, string expected) =>
        Assert.Equal(expected, GeminiLiveTranslateProtocol.ResolveProvider(provider, model));

    [Fact]
    public void CreateSetupMessage_includes_translation_config_and_transcripts()
    {
        using var json = JsonDocument.Parse(
            GeminiLiveTranslateProtocol.CreateSetupMessage(
                "gemini-3.5-live-translate-preview",
                "zh-Hans"));

        var setup = json.RootElement.GetProperty("setup");
        Assert.Equal(
            "models/gemini-3.5-live-translate-preview",
            setup.GetProperty("model").GetString());
        var gen = setup.GetProperty("generationConfig");
        Assert.Equal("AUDIO", gen.GetProperty("responseModalities")[0].GetString());
        // 转写字段在 setup 顶层（官方 Live API 参考）
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
        Assert.Equal(
            "zh-Hans",
            gen.GetProperty("translationConfig").GetProperty("targetLanguageCode").GetString());
    }

    [Fact]
    public void BuildWebSocketUri_targets_google_bidi_endpoint()
    {
        var uri = GeminiLiveTranslateProtocol.BuildWebSocketUri("test-key");
        Assert.StartsWith("wss://generativelanguage.googleapis.com/", uri.ToString());
        Assert.Contains("BidiGenerateContent", uri.ToString());
        Assert.Contains("key=test-key", uri.ToString());
    }

    [Theory]
    [InlineData("doubao", "anything", "doubao")]
    [InlineData("", "doubao-ast-2.0", "doubao")]
    [InlineData("", "volc.service_type.10053", "doubao")]
    [InlineData(null, "同声传译2.0", "doubao")]
    public void ResolveProvider_detects_doubao(string? provider, string model, string expected) =>
        Assert.Equal(expected, GeminiLiveTranslateProtocol.ResolveProvider(provider, model));

    [Theory]
    [InlineData("deepgram", "anything", "deepgram")]
    [InlineData("", "nova-3", "deepgram")]
    [InlineData(null, "nova-3-general", "deepgram")]
    public void ResolveProvider_detects_deepgram(string? provider, string model, string expected) =>
        Assert.Equal(expected, GeminiLiveTranslateProtocol.ResolveProvider(provider, model));
}

public class DeepgramSttProtocolTests
{
    [Fact]
    public void BuildListenWebSocketUri_uses_nova3_and_pcm_params()
    {
        var uri = DeepgramSttProtocol.BuildListenWebSocketUri("nova-3", "en");
        Assert.Equal("api.deepgram.com", uri.Host);
        Assert.Contains("/v1/listen", uri.AbsolutePath);
        Assert.Contains("model=nova-3", uri.Query);
        Assert.Contains("encoding=linear16", uri.Query);
        Assert.Contains("sample_rate=16000", uri.Query);
        Assert.Contains("language=en", uri.Query);
        Assert.Contains("interim_results=true", uri.Query);
    }

    [Fact]
    public void NormalizeModel_defaults_chat_model_names_to_nova3()
    {
        Assert.Equal("nova-3", DeepgramSttProtocol.NormalizeModel("doubao-seed-2-0-pro-260215"));
        Assert.Equal("nova-3", DeepgramSttProtocol.NormalizeModel("nova-3"));
    }

    [Theory]
    [InlineData("e0aeae1234567890abcdef1234567890abcdef", true)]
    [InlineData("现在先验证字幕：abc", false)]
    [InlineData("short", false)]
    [InlineData("", false)]
    public void LooksLikeApiKey_filters_junk(string key, bool expected) =>
        Assert.Equal(expected, DeepgramSttProtocol.LooksLikeApiKey(key));
}

public class DoubaoAstProtocolTests
{
    [Fact]
    public void BuildStartSession_and_parse_roundtrip_shape()
    {
        var bytes = DoubaoAstProtocol.BuildStartSession("session-abc", "en", "zh", "s2s");
        Assert.True(bytes.Length > 20);

        // 最小模拟 SessionStarted 响应：event=150, status=20000000
        using var ms = new MemoryStream();
        // response_meta field 1: StatusCode=3 varint 20000000, Message=4 empty skip
        // We'll encode event=2 as 150, response_meta nested with status
        void WriteVarint(Stream s, ulong v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }
        void WriteKey(Stream s, int f, int w) => WriteVarint(s, (ulong)((f << 3) | w));
        // event = 2 / wire0 / 150
        WriteKey(ms, 2, 0);
        WriteVarint(ms, 150);
        // response_meta = 1 / len-delimited
        using (var meta = new MemoryStream())
        {
            WriteKey(meta, 3, 0);
            WriteVarint(meta, DoubaoAstProtocol.StatusSuccess);
            var metaBytes = meta.ToArray();
            WriteKey(ms, 1, 2);
            WriteVarint(ms, (ulong)metaBytes.Length);
            ms.Write(metaBytes);
        }

        var evt = DoubaoAstProtocol.ParseResponse(ms.ToArray());
        Assert.Equal(DoubaoAstProtocol.EventSessionStarted, evt.Event);
        Assert.Equal(DoubaoAstProtocol.StatusSuccess, evt.StatusCode);
    }

    [Fact]
    public void BuildTaskRequest_embeds_pcm_payload()
    {
        var pcm = new byte[320]; // 10ms @16k mono
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);
        var bytes = DoubaoAstProtocol.BuildTaskRequest("sid", pcm);
        Assert.True(bytes.Length > pcm.Length);
        // 原始 PCM 字节应出现在序列化结果中
        var hay = Convert.ToHexString(bytes);
        var needle = Convert.ToHexString(pcm.AsSpan(0, 16));
        Assert.Contains(needle, hay);
    }
}
