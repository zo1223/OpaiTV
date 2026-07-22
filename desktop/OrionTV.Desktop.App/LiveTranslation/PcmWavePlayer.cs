using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// 将 PCM16 mono 实时推到系统默认扬声器（winmm waveOut）。
/// 用于 Gemini Live Translate 的中文配音回放。
/// </summary>
internal sealed class PcmWavePlayer : IDisposable
{
    private readonly int _sampleRate;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private bool _disposed;

    public PcmWavePlayer(int sampleRate = 24_000)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 24_000;
        _worker = Task.Run(() => PumpAsync(_cts.Token));
    }

    public void Enqueue(ReadOnlySpan<byte> pcm16)
    {
        if (_disposed || pcm16.IsEmpty)
        {
            return;
        }

        _queue.Writer.TryWrite(pcm16.ToArray());
    }

    public void Clear()
    {
        while (_queue.Reader.TryRead(out _))
        {
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        IntPtr waveOut = IntPtr.Zero;
        try
        {
            var format = new WaveFormat
            {
                wFormatTag = 1, // PCM
                nChannels = 1,
                nSamplesPerSec = (uint)_sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = (uint)(_sampleRate * 2),
                cbSize = 0
            };

            var openResult = waveOutOpen(out waveOut, 0xFFFFFFFF /* WAVE_MAPPER */, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (openResult != 0 || waveOut == IntPtr.Zero)
            {
                LiveTranslationLog.Write($"waveOutOpen failed: {openResult}");
                return;
            }

            await foreach (var chunk in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Length == 0)
                {
                    continue;
                }

                PlayChunk(waveOut, chunk);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LiveTranslationLog.Write("PcmWavePlayer: " + exception.Message);
        }
        finally
        {
            if (waveOut != IntPtr.Zero)
            {
                try
                {
                    waveOutReset(waveOut);
                    waveOutClose(waveOut);
                }
                catch
                {
                }
            }
        }
    }

    private static void PlayChunk(IntPtr waveOut, byte[] pcm)
    {
        var handle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            var header = new WaveHdr
            {
                lpData = handle.AddrOfPinnedObject(),
                dwBufferLength = (uint)pcm.Length,
                dwFlags = 0,
                dwLoops = 0
            };

            var headerHandle = GCHandle.Alloc(header, GCHandleType.Pinned);
            try
            {
                var hdrPtr = headerHandle.AddrOfPinnedObject();
                if (waveOutPrepareHeader(waveOut, hdrPtr, Marshal.SizeOf<WaveHdr>()) != 0)
                {
                    return;
                }

                if (waveOutWrite(waveOut, hdrPtr, Marshal.SizeOf<WaveHdr>()) != 0)
                {
                    waveOutUnprepareHeader(waveOut, hdrPtr, Marshal.SizeOf<WaveHdr>());
                    return;
                }

                // 同步等这块播完，避免 header 被提前释放。
                while ((Marshal.PtrToStructure<WaveHdr>(hdrPtr).dwFlags & 0x00000001) == 0) // WHDR_DONE
                {
                    Thread.Sleep(5);
                }

                waveOutUnprepareHeader(waveOut, hdrPtr, Marshal.SizeOf<WaveHdr>());
            }
            finally
            {
                headerHandle.Free();
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(
        out IntPtr hWaveOut,
        uint uDeviceID,
        ref WaveFormat lpFormat,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint dwFlags);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutClose(IntPtr hWaveOut);
}
