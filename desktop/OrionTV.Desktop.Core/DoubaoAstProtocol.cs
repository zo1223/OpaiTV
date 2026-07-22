using System.Text;

namespace OrionTV.Desktop.Core;

/// <summary>豆包/火山「同声传译 2.0」(AST) WebSocket + Protobuf 协议约定。</summary>
public static class DoubaoAstProtocol
{
    public const string DefaultWsUrl = "wss://openspeech.bytedance.com/api/v4/ast/v2/translate";
    public const string DefaultResourceId = "volc.service_type.10053";
    public const string DefaultMode = "s2s";
    public const string DefaultSourceLanguage = "en";
    public const string DefaultTargetLanguage = "zh";
    public const string ModelLabel = "doubao-ast-2.0";

    public const int InputSampleRate = 16_000;
    public const int OutputSampleRate = 24_000;
    public const int Bits = 16;
    public const int Channels = 1;
    /// <summary>约 80ms 一帧，与官方 demo 一致。</summary>
    public const int ChunkDurationMs = 80;
    public const int ChunkBytes = InputSampleRate * Channels * (Bits / 8) * ChunkDurationMs / 1000;

    public const int StatusSuccess = 20_000_000;

    // event.Type（proto3）
    public const int EventStartSession = 100;
    public const int EventFinishSession = 102;
    public const int EventSessionStarted = 150;
    public const int EventSessionCanceled = 151;
    public const int EventSessionFinished = 152;
    public const int EventSessionFailed = 153;
    public const int EventUsageResponse = 154;
    public const int EventTaskRequest = 200;
    public const int EventAudioMuted = 250;
    public const int EventTtsSentenceStart = 350;
    public const int EventTtsSentenceEnd = 351;
    public const int EventTtsResponse = 352;
    public const int EventTtsEnded = 359;
    public const int EventSourceSubtitleStart = 650;
    public const int EventSourceSubtitleResponse = 651;
    public const int EventSourceSubtitleEnd = 652;
    public const int EventTranslationSubtitleStart = 653;
    public const int EventTranslationSubtitleResponse = 654;
    public const int EventTranslationSubtitleEnd = 655;

    public static bool IsDoubaoAstModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var m = model.Trim();
        return m.Contains("doubao-ast", StringComparison.OrdinalIgnoreCase)
            || m.Contains("liveinterpret", StringComparison.OrdinalIgnoreCase)
            || m.Contains("同声传译", StringComparison.Ordinal)
            || string.Equals(m, DefaultResourceId, StringComparison.OrdinalIgnoreCase)
            || m.Contains("volc.service_type.10053", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDoubaoProvider(string? provider) =>
        string.Equals(provider, "doubao", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "volc", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "volcengine", StringComparison.OrdinalIgnoreCase);

    public static byte[] BuildStartSession(
        string sessionId,
        string sourceLanguage = DefaultSourceLanguage,
        string targetLanguage = DefaultTargetLanguage,
        string mode = DefaultMode,
        bool denoise = false)
    {
        using var ms = new MemoryStream();
        // request_meta = 1 { SessionID = 6 }
        WriteMessage(ms, 1, meta => WriteString(meta, 6, sessionId));
        // event = 2
        WriteVarintField(ms, 2, EventStartSession);
        // user = 3 { uid=1, did=2 }
        WriteMessage(ms, 3, user =>
        {
            WriteString(user, 1, "oriontv_desktop");
            WriteString(user, 2, "oriontv_desktop");
        });
        // source_audio = 4 { format=4, rate=7, bits=8, channel=9 }
        WriteMessage(ms, 4, audio =>
        {
            WriteString(audio, 4, "wav");
            WriteVarintField(audio, 7, InputSampleRate);
            WriteVarintField(audio, 8, Bits);
            WriteVarintField(audio, 9, Channels);
        });
        // target_audio = 5 { format=4, rate=7 }  — pcm 便于直接播放
        WriteMessage(ms, 5, audio =>
        {
            WriteString(audio, 4, "pcm");
            WriteVarintField(audio, 7, OutputSampleRate);
        });
        // request (AST ReqParams) = 6 { mode=1, source_language=2, target_language=3 }
        WriteMessage(ms, 6, req =>
        {
            WriteString(req, 1, mode);
            WriteString(req, 2, sourceLanguage);
            WriteString(req, 3, targetLanguage);
        });
        // denoise = 7
        WriteBoolField(ms, 7, denoise);
        return ms.ToArray();
    }

    public static byte[] BuildTaskRequest(string sessionId, ReadOnlySpan<byte> pcm16)
    {
        // 必须先拷贝：lambda 不能捕获 ref-like Span。
        var pcmCopy = pcm16.ToArray();
        using var ms = new MemoryStream();
        WriteMessage(ms, 1, meta => WriteString(meta, 6, sessionId));
        WriteVarintField(ms, 2, EventTaskRequest);
        WriteMessage(ms, 4, audio =>
        {
            WriteString(audio, 4, "wav");
            WriteVarintField(audio, 7, InputSampleRate);
            WriteVarintField(audio, 8, Bits);
            WriteVarintField(audio, 9, Channels);
            WriteBytes(audio, 14, pcmCopy);
        });
        return ms.ToArray();
    }

    public static byte[] BuildFinishSession(string sessionId)
    {
        using var ms = new MemoryStream();
        WriteMessage(ms, 1, meta => WriteString(meta, 6, sessionId));
        WriteVarintField(ms, 2, EventFinishSession);
        return ms.ToArray();
    }

    public static DoubaoAstServerEvent ParseResponse(ReadOnlySpan<byte> data)
    {
        var eventCode = 0;
        var statusCode = 0;
        string? message = null;
        string? text = null;
        byte[]? audio = null;

        var i = 0;
        while (i < data.Length)
        {
            var key = ReadVarint(data, ref i);
            var field = (int)(key >> 3);
            var wire = (int)(key & 0x7);
            if (wire == 0)
            {
                var v = ReadVarint(data, ref i);
                if (field == 2)
                {
                    eventCode = (int)v;
                }
            }
            else if (wire == 2)
            {
                var len = (int)ReadVarint(data, ref i);
                var slice = data.Slice(i, len);
                i += len;
                if (field == 1)
                {
                    ParseResponseMeta(slice, ref statusCode, ref message);
                }
                else if (field == 3)
                {
                    audio = slice.ToArray();
                }
                else if (field == 4)
                {
                    text = Encoding.UTF8.GetString(slice);
                }
            }
            else if (wire == 5)
            {
                i += 4;
            }
            else if (wire == 1)
            {
                i += 8;
            }
            else
            {
                break;
            }
        }

        return new DoubaoAstServerEvent(eventCode, statusCode, message, text, audio);
    }

    private static void ParseResponseMeta(ReadOnlySpan<byte> data, ref int statusCode, ref string? message)
    {
        var i = 0;
        while (i < data.Length)
        {
            var key = ReadVarint(data, ref i);
            var field = (int)(key >> 3);
            var wire = (int)(key & 0x7);
            if (wire == 0)
            {
                var v = ReadVarint(data, ref i);
                if (field == 3)
                {
                    statusCode = (int)v;
                }
            }
            else if (wire == 2)
            {
                var len = (int)ReadVarint(data, ref i);
                var slice = data.Slice(i, len);
                i += len;
                if (field == 4)
                {
                    message = Encoding.UTF8.GetString(slice);
                }
            }
            else if (wire == 5)
            {
                i += 4;
            }
            else if (wire == 1)
            {
                i += 8;
            }
            else
            {
                break;
            }
        }
    }

    // ---- protobuf wire helpers ----

    private static void WriteVarintField(Stream s, int field, long value)
    {
        WriteKey(s, field, 0);
        WriteVarint(s, (ulong)value);
    }

    private static void WriteBoolField(Stream s, int field, bool value) =>
        WriteVarintField(s, field, value ? 1 : 0);

    private static void WriteString(Stream s, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBytes(s, field, bytes);
    }

    private static void WriteBytes(Stream s, int field, ReadOnlySpan<byte> value)
    {
        WriteKey(s, field, 2);
        WriteVarint(s, (ulong)value.Length);
        s.Write(value);
    }

    private static void WriteMessage(Stream s, int field, Action<Stream> writer)
    {
        using var nested = new MemoryStream();
        writer(nested);
        WriteBytes(s, field, nested.ToArray());
    }

    private static void WriteKey(Stream s, int field, int wire) =>
        WriteVarint(s, (ulong)((field << 3) | wire));

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        s.WriteByte((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
    {
        ulong result = 0;
        var shift = 0;
        while (i < data.Length)
        {
            var b = data[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("varint too long");
            }
        }

        return result;
    }
}

public readonly record struct DoubaoAstServerEvent(
    int Event,
    int StatusCode,
    string? Message,
    string? Text,
    byte[]? Audio);
