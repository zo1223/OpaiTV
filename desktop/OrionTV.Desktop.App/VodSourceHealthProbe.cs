using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

internal enum VodSourceProbeStatus
{
    Playable,
    Partial,
    Unsupported,
    Unplayable,
    Inconclusive,
}

internal enum MediaDecodeProbeStatus
{
    Decoded,
    Failed,
    TimedOut,
    Unavailable,
}

internal sealed record MediaDecodeProbeResult(MediaDecodeProbeStatus Status, string Detail);

internal interface IMediaDecodeProbe
{
    Task<MediaDecodeProbeResult> ProbeAsync(string url, CancellationToken cancellationToken);
}

internal sealed record VodProbeSample(int Index, string Label, string Url);

internal static class VodSourceProbeSampler
{
    public static IReadOnlyList<VodProbeSample> Select(
        SearchResult source,
        int? preferredEpisodeIndex = null)
    {
        var available = Enumerable.Range(0, source.Episodes.Count)
            .Where(index => !string.IsNullOrWhiteSpace(source.Episodes[index]))
            .ToList();
        if (available.Count == 0)
        {
            return [];
        }

        var latest = available
            .Select(index => new
            {
                Index = index,
                HasNumber = EpisodeLabelFormatter.TryGetNumber(
                    EpisodeLabelFormatter.DisplayAt(source.EpisodeLabels, index),
                    out var number),
                Number = number,
            })
            .Where(item => item.HasNumber)
            .OrderByDescending(item => item.Number)
            .ThenByDescending(item => item.Index)
            .Select(item => (int?)item.Index)
            .FirstOrDefault()
            ?? available[^1];
        var middle = available[available.Count / 2];
        var first = available[0];

        var seen = new HashSet<int>();
        var result = new List<VodProbeSample>(3);
        var preferred = preferredEpisodeIndex is >= 0
            && preferredEpisodeIndex.Value < source.Episodes.Count
            && !string.IsNullOrWhiteSpace(source.Episodes[preferredEpisodeIndex.Value])
                ? preferredEpisodeIndex
                : null;
        foreach (var candidate in new int?[] { preferred, latest, middle, first })
        {
            if (candidate is not { } index)
            {
                continue;
            }

            if (seen.Add(index))
            {
                result.Add(new VodProbeSample(
                    index,
                    EpisodeLabelFormatter.DisplayAt(source.EpisodeLabels, index),
                    source.Episodes[index]));
                if (result.Count == 3)
                {
                    break;
                }
            }
        }

        return result;
    }
}

internal sealed record VodSourceHealthResult(
    VodSourceProbeStatus Status,
    int PassedSamples,
    int TotalSamples,
    ChannelHealthResult Network,
    string Detail)
{
    public string DisplayLabel
    {
        get
        {
            var status = Status switch
            {
                VodSourceProbeStatus.Playable => $"可播 · {PassedSamples}/{TotalSamples}",
                VodSourceProbeStatus.Partial => $"部分可播 · {PassedSamples}/{TotalSamples}",
                _ => Detail,
            };
            if (Status is not (VodSourceProbeStatus.Playable or VodSourceProbeStatus.Partial))
            {
                return status;
            }

            var metrics = ChannelHealthDisplay.MetricsLabelOf(Network);
            return metrics == "未测速"
                ? $"{status} · 未取得测速数据"
                : $"{status} · {metrics}";
        }
    }
}

internal static class VodSourceHealthDisplay
{
    public static string SummaryOf(IEnumerable<VodSourceHealthResult> results)
    {
        var measured = results.ToList();
        if (measured.Count == 0)
        {
            return string.Empty;
        }

        var playable = measured.Count(result => result.Status == VodSourceProbeStatus.Playable);
        var partial = measured.Count(result => result.Status == VodSourceProbeStatus.Partial);
        var unavailable = measured.Count - playable - partial;
        return $"已测 {measured.Count} · 可播 {playable} · 部分可播 {partial} · 不可用 {unavailable}";
    }
}

internal sealed class VodSourceHealthProbe
{
    private readonly Func<string, bool, CancellationToken, Task<ChannelHealthResult>> _networkProbe;
    private readonly IMediaDecodeProbe _decodeProbe;

    public VodSourceHealthProbe(ChannelHealthProbe networkProbe, IMediaDecodeProbe decodeProbe)
        : this(
            (url, measureSpeed, cancellationToken) =>
                networkProbe.ProbeAsync(url, cancellationToken, measureSpeed),
            decodeProbe)
    {
    }

    internal VodSourceHealthProbe(
        Func<string, bool, CancellationToken, Task<ChannelHealthResult>> networkProbe,
        IMediaDecodeProbe decodeProbe)
    {
        _networkProbe = networkProbe;
        _decodeProbe = decodeProbe;
    }

    public async Task ProbeManyAsync(
        IReadOnlyList<SearchResult> sources,
        Action<string, VodSourceHealthResult> onResult,
        int concurrency,
        CancellationToken cancellationToken,
        Func<SearchResult, int?>? preferredEpisodeIndex = null)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                VodSourceHealthResult result;
                try
                {
                    result = await ProbeAsync(
                            source,
                            cancellationToken,
                            preferredEpisodeIndex?.Invoke(source))
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    result = Inconclusive("检测失败");
                }

                onResult(SearchResultSources.SelectionKeyOf(source), result);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    internal async Task<VodSourceHealthResult> ProbeAsync(
        SearchResult source,
        CancellationToken cancellationToken,
        int? preferredEpisodeIndex = null)
    {
        var samples = VodSourceProbeSampler.Select(source, preferredEpisodeIndex);
        if (samples.Count == 0)
        {
            return Inconclusive("没有可检测的剧集地址");
        }

        var networkTasks = samples
            .Select((sample, index) => _networkProbe(sample.Url, index == 0, cancellationToken))
            .ToArray();
        var networkResults = await Task.WhenAll(networkTasks).ConfigureAwait(false);
        var outcomes = samples
            .Select((sample, index) => (Sample: sample, Result: networkResults[index]))
            .ToList();

        var passed = new bool[samples.Count];
        var sawDecodeTimeout = false;
        var primaryDecoded = false;
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            if (outcome.Result.MediaStatus == MediaProbeStatus.UnsupportedContent)
            {
                if (index == 0)
                {
                    return new VodSourceHealthResult(
                        VodSourceProbeStatus.Unsupported,
                        0,
                        samples.Count,
                        outcome.Result,
                        outcome.Result.Detail);
                }

                continue;
            }

            // 最新集必须实际解码。其它样本若网络媒体校验已通过，可复用最新集的解码结论；
            // HTTP 403/超时/格式不明时则逐个交给播放器验证，避免 CDN 对 .NET 的假阴性。
            var needsDecode = index == 0
                || !primaryDecoded
                || outcome.Result.MediaStatus != MediaProbeStatus.MediaValidated;
            if (!needsDecode)
            {
                passed[index] = true;
                continue;
            }

            var decode = await _decodeProbe.ProbeAsync(outcome.Sample.Url, cancellationToken).ConfigureAwait(false);
            if (decode.Status == MediaDecodeProbeStatus.Unavailable)
            {
                return new VodSourceHealthResult(
                    VodSourceProbeStatus.Inconclusive,
                    passed.Count(value => value),
                    samples.Count,
                    ValidatedByPlayer(),
                    decode.Detail);
            }

            if (decode.Status == MediaDecodeProbeStatus.TimedOut)
            {
                if (index == 0)
                {
                    return new VodSourceHealthResult(
                        VodSourceProbeStatus.Inconclusive,
                        0,
                        samples.Count,
                        ValidatedByPlayer(),
                        "解码检测超时");
                }

                sawDecodeTimeout = true;
                continue;
            }

            if (decode.Status == MediaDecodeProbeStatus.Failed)
            {
                if (index == 0)
                {
                    return new VodSourceHealthResult(
                        VodSourceProbeStatus.Unplayable,
                        0,
                        samples.Count,
                        ValidatedByPlayer(),
                        "无法解码视频画面");
                }

                continue;
            }

            passed[index] = true;
            if (index == 0)
            {
                primaryDecoded = true;
            }
        }

        var passedCount = passed.Count(value => value);
        if (passedCount > 0)
        {
            var status = passedCount == samples.Count
                ? VodSourceProbeStatus.Playable
                : VodSourceProbeStatus.Partial;
            var network = outcomes
                .Select(item => item.Result)
                .FirstOrDefault(result =>
                    result.MediaStatus == MediaProbeStatus.MediaValidated
                    && (result.TtfbMs > 0 || result.ThroughputMbps > 0))
                ?? ValidatedByPlayer();
            return new VodSourceHealthResult(
                status,
                passedCount,
                samples.Count,
                network,
                status == VodSourceProbeStatus.Playable ? "可播" : "部分可播");
        }

        if (outcomes.All(item => item.Result.MediaStatus == MediaProbeStatus.UnsupportedContent))
        {
            return new VodSourceHealthResult(
                VodSourceProbeStatus.Unsupported,
                0,
                samples.Count,
                outcomes[0].Result,
                outcomes[0].Result.Detail);
        }

        return new VodSourceHealthResult(
            sawDecodeTimeout ? VodSourceProbeStatus.Inconclusive : VodSourceProbeStatus.Unplayable,
            0,
            samples.Count,
            ValidatedByPlayer(),
            sawDecodeTimeout ? "解码检测超时" : "无法解码视频画面");
    }

    private static ChannelHealthResult ValidatedByPlayer() => new(
        ChannelHealth.Fair,
        40,
        "播放器验证",
        MediaStatus: MediaProbeStatus.MediaValidated);

    private static VodSourceHealthResult Inconclusive(string detail) => new(
        VodSourceProbeStatus.Inconclusive,
        0,
        0,
        ChannelHealthResult.NotProbeable,
        detail);
}

internal sealed class LibVlcMediaDecodeProbe(Func<Task<LibVLC>> getLibVlc) : IMediaDecodeProbe
{
    internal static readonly TimeSpan DecodeTimeout = PlaybackStartupWatchdog.DefaultTimeout;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint PmRemove = 0x0001;

    public async Task<MediaDecodeProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new MediaDecodeProbeResult(MediaDecodeProbeStatus.Failed, "无效的媒体地址");
        }

        LibVLC libVlc;
        try
        {
            libVlc = await getLibVlc().ConfigureAwait(false);
        }
        catch
        {
            return new MediaDecodeProbeResult(MediaDecodeProbeStatus.Unavailable, "LibVLC 初始化失败");
        }

        try
        {
            return await Task.Run(
                    () => ProbeCore(libVlc, uri, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new MediaDecodeProbeResult(MediaDecodeProbeStatus.Failed, "LibVLC 解码检测失败");
        }
    }

    internal static IReadOnlyList<string> BuildMediaOptions(Uri uri) =>
    [
        .. PlaybackMediaOptions.ForUri(uri, networkCachingMs: 6000),
        ":no-audio",
    ];

    private static MediaDecodeProbeResult ProbeCore(
        LibVLC libVlc,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var hwnd = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            "STATIC",
            "",
            WsPopup | WsVisible,
            -32000,
            -32000,
            64,
            64,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            return new MediaDecodeProbeResult(MediaDecodeProbeStatus.Unavailable, "无法创建 LibVLC 检测窗口");
        }

        try
        {
            using var signal = new ManualResetEventSlim();
            using var player = new MediaPlayer(libVlc)
            {
                Hwnd = hwnd,
                EnableKeyInput = false,
                EnableMouseInput = false,
            };
            using var media = new Media(libVlc, uri);
            foreach (var option in BuildMediaOptions(uri))
            {
                media.AddOption(option);
            }

            var resultSync = new object();
            MediaDecodeProbeResult? result = null;
            void Complete(MediaDecodeProbeResult value)
            {
                lock (resultSync)
                {
                    if (result is not null)
                    {
                        return;
                    }

                    result = value;
                }

                signal.Set();
            }

            EventHandler<MediaPlayerVoutEventArgs> onVout = (_, e) =>
            {
                if (e.Count > 0)
                {
                    Complete(new MediaDecodeProbeResult(MediaDecodeProbeStatus.Decoded, "LibVLC 已输出视频画面"));
                }
            };
            EventHandler<EventArgs> onError = (_, _) =>
                Complete(new MediaDecodeProbeResult(MediaDecodeProbeStatus.Failed, "LibVLC 无法输出视频画面"));
            EventHandler<EventArgs> onEnded = (_, _) =>
                Complete(new MediaDecodeProbeResult(MediaDecodeProbeStatus.Failed, "媒体结束前未输出视频画面"));

            player.Vout += onVout;
            player.EncounteredError += onError;
            player.EndReached += onEnded;
            try
            {
                if (!player.Play(media))
                {
                    return new MediaDecodeProbeResult(MediaDecodeProbeStatus.Failed, "LibVLC 启动播放失败");
                }

                var deadline = DateTimeOffset.UtcNow + DecodeTimeout;
                while (!signal.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PumpWindowMessages();
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return new MediaDecodeProbeResult(MediaDecodeProbeStatus.TimedOut, "LibVLC 解码检测超时");
                    }

                    signal.Wait(TimeSpan.FromMilliseconds(20), cancellationToken);
                }

                lock (resultSync)
                {
                    return result
                        ?? new MediaDecodeProbeResult(MediaDecodeProbeStatus.Failed, "LibVLC 未输出视频画面");
                }
            }
            finally
            {
                player.Vout -= onVout;
                player.EncounteredError -= onError;
                player.EndReached -= onEnded;
                try { player.Stop(); } catch { }
            }
        }
        finally
        {
            _ = DestroyWindow(hwnd);
        }
    }

    private static void PumpWindowMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out WindowMessage message,
        IntPtr hwnd,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref WindowMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref WindowMessage message);
}
