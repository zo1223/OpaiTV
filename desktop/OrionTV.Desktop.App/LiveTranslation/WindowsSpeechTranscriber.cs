using System.Globalization;
using System.IO;
using System.Speech.Recognition;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// 用 Windows 自带英语听写识别 PCM 片段。中转站没有 whisper 通道时作为本地 ASR 回退。
/// </summary>
internal static class WindowsSpeechTranscriber
{
    private const int TargetSampleRate = 16_000;
    private static readonly object EngineGate = new();
    private static SpeechRecognitionEngine? _engine;
    private static string? _engineError;

    public static string? TryTranscribePcm16(ReadOnlySpan<byte> pcm16, int sampleRate)
    {
        if (pcm16.IsEmpty || sampleRate <= 0)
        {
            return null;
        }

        var wavPath = Path.Combine(Path.GetTempPath(), $"oriontv-asr-{Guid.NewGuid():N}.wav");
        try
        {
            var pcmForAsr = sampleRate == TargetSampleRate
                ? pcm16.ToArray()
                : ResamplePcm16Mono(pcm16, sampleRate, TargetSampleRate);

            // 直播二次解码音量常偏小；放大后再识别，并在尾部补静音帮助引擎收尾。
            pcmForAsr = NormalizeAndPad(pcmForAsr);
            WriteWav(wavPath, pcmForAsr, TargetSampleRate, channels: 1);
            lock (EngineGate)
            {
                var engine = EnsureEngine();
                if (engine is null)
                {
                    return null;
                }

                try
                {
                    engine.SetInputToWaveFile(wavPath);
                    var result = engine.Recognize();
                    var text = result?.Text?.Trim();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
                finally
                {
                    // 必须释放波形输入，否则临时 wav 被锁住，后续分段也会拖垮识别引擎。
                    try
                    {
                        engine.SetInputToNull();
                    }
                    catch
                    {
                        // 释放失败时丢掉引擎，下次重建。
                        try
                        {
                            engine.Dispose();
                        }
                        catch
                        {
                        }

                        _engine = null;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            lock (EngineGate)
            {
                _engineError = exception.Message;
            }

            return null;
        }
        finally
        {
            try
            {
                File.Delete(wavPath);
            }
            catch
            {
                // 临时文件清理失败不影响主流程。
            }
        }
    }

    public static string? LastEngineError
    {
        get
        {
            lock (EngineGate)
            {
                return _engineError;
            }
        }
    }

    public static bool IsAvailable
    {
        get
        {
            lock (EngineGate)
            {
                return EnsureEngine() is not null;
            }
        }
    }

    private static SpeechRecognitionEngine? EnsureEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        try
        {
            var culture = new CultureInfo("en-US");
            var engine = new SpeechRecognitionEngine(culture);
            engine.LoadGrammar(new DictationGrammar());
            // 直播片段没有自然句末静音，超时要放宽，否则引擎过早放弃。
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(1.2);
            engine.BabbleTimeout = TimeSpan.FromSeconds(2.0);
            engine.EndSilenceTimeout = TimeSpan.FromSeconds(0.9);
            engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.2);
            _engine = engine;
            _engineError = null;
            return engine;
        }
        catch (Exception exception)
        {
            _engineError = "本机缺少 en-US 语音识别：" + exception.Message;
            return null;
        }
    }

    /// <summary>峰值归一化到约 50% 满幅，并在尾部追加 500ms 静音。</summary>
    private static byte[] NormalizeAndPad(byte[] pcm)
    {
        if (pcm.Length < 4)
        {
            return pcm;
        }

        short maxAbs = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var abs = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (abs > maxAbs)
            {
                maxAbs = (short)abs;
            }
        }

        // 真静音（峰值极低）直接返回，交给上层跳过。
        if (maxAbs < 40)
        {
            return pcm;
        }

        // 最大放大 24 倍，避免底噪爆音。
        var gain = Math.Min(24.0, 14_000.0 / maxAbs);
        var padSamples = TargetSampleRate / 2; // 500ms
        var output = new byte[pcm.Length + padSamples * 2];
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (int)Math.Round(sample * gain);
            if (scaled > short.MaxValue)
            {
                scaled = short.MaxValue;
            }
            else if (scaled < short.MinValue)
            {
                scaled = short.MinValue;
            }

            output[i] = (byte)(scaled & 0xFF);
            output[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }

        return output;
    }

    private static byte[] ResamplePcm16Mono(ReadOnlySpan<byte> source, int sourceRate, int targetRate)
    {
        var sourceSamples = source.Length / 2;
        if (sourceSamples <= 1 || sourceRate == targetRate)
        {
            return source.ToArray();
        }

        var targetSamples = Math.Max(1, (int)((long)sourceSamples * targetRate / sourceRate));
        var output = new byte[targetSamples * 2];
        for (var i = 0; i < targetSamples; i++)
        {
            var srcPos = (double)i * sourceRate / targetRate;
            var srcIndex = (int)srcPos;
            if (srcIndex >= sourceSamples - 1)
            {
                srcIndex = sourceSamples - 1;
                var offset = srcIndex * 2;
                output[i * 2] = source[offset];
                output[i * 2 + 1] = source[offset + 1];
                continue;
            }

            // 线性插值，比最近邻更适合语音。
            var frac = srcPos - srcIndex;
            var s0 = (short)(source[srcIndex * 2] | (source[srcIndex * 2 + 1] << 8));
            var s1 = (short)(source[(srcIndex + 1) * 2] | (source[(srcIndex + 1) * 2 + 1] << 8));
            var mixed = (int)Math.Round(s0 + (s1 - s0) * frac);
            output[i * 2] = (byte)(mixed & 0xFF);
            output[i * 2 + 1] = (byte)((mixed >> 8) & 0xFF);
        }

        return output;
    }

    private static void WriteWav(string path, ReadOnlySpan<byte> pcm16, int sampleRate, int channels)
    {
        var byteRate = sampleRate * channels * 2;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm16.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm16.Length);
        writer.Write(pcm16);
    }
}
