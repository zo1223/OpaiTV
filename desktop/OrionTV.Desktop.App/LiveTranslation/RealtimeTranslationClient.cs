using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.LiveTranslation;

internal enum RealtimeTranslationConnectionState
{
    Connecting,
    Listening,
    Closed,
}

/// <summary>OpenAI 专用 Realtime Translation WebSocket 客户端。</summary>
internal sealed class RealtimeTranslationClient : ILiveTranslationSession
{
    private const int MaxServerMessageBytes = 1_048_576;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLanguage;
    private readonly string _baseUrl;
    private readonly Pcm16TranslationFrameBuffer _frameBuffer = new();
    private readonly object _frameSync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetimeCts;
    private Channel<byte[]>? _audioFrames;
    private Task? _audioSendTask;
    private Task? _receiveTask;
    private TaskCompletionSource _serverClosed = NewCompletionSource();
    private int _stopping;
    private bool _disposed;

    public RealtimeTranslationClient(
        string apiKey,
        string model,
        string targetLanguage,
        string? baseUrl = null)
    {
        _apiKey = apiKey;
        _model = model;
        _targetLanguage = targetLanguage;
        _baseUrl = RealtimeTranslationProtocol.NormalizeBaseUrl(baseUrl);
    }

    public event Action<string>? SubtitleDelta;
    public event Action<RealtimeTranslationConnectionState>? StateChanged;
    public event Action<string?, string?>? Failed;
#pragma warning disable CS0067 // Realtime 路径暂无阶段进度，接口统一保留。
    public event Action<string>? Progress;
#pragma warning restore CS0067

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null)
        {
            throw new InvalidOperationException("实时翻译会话已经启动");
        }

        StateChanged?.Invoke(RealtimeTranslationConnectionState.Connecting);
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(25)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _serverClosed = NewCompletionSource();

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", CreateSafetyIdentifier());
        socket.Options.SetRequestHeader("User-Agent", "OrionTV-Desktop/1.3.13");
        _socket = socket;

        try
        {
            await socket
                .ConnectAsync(RealtimeTranslationProtocol.BuildWebSocketUri(_model, _baseUrl), _lifetimeCts.Token)
                .ConfigureAwait(false);

            _receiveTask = ReceiveLoopAsync(socket, _lifetimeCts.Token);
            await SendTextAsync(
                    RealtimeTranslationProtocol.CreateSessionUpdate(_targetLanguage),
                    _lifetimeCts.Token)
                .ConfigureAwait(false);
            _audioSendTask = SendAudioLoopAsync(_audioFrames.Reader, _lifetimeCts.Token);
        }
        catch
        {
            var lifetimeCts = Abort();
            await ObserveCompletionAsync(_audioSendTask).ConfigureAwait(false);
            await ObserveCompletionAsync(_receiveTask).ConfigureAwait(false);
            _audioSendTask = null;
            _receiveTask = null;
            lifetimeCts?.Dispose();
            throw;
        }
    }

    public void AppendPcm16(ReadOnlySpan<byte> audio)
    {
        var writer = _audioFrames?.Writer;
        if (writer is null || Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        lock (_frameSync)
        {
            _frameBuffer.Append(audio, frame => writer.TryWrite(frame));
        }
    }

    private async Task SendAudioLoopAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendTextAsync(
                        RealtimeTranslationProtocol.CreateAudioAppend(frame),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportTransportFailure(exception);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket
                        .ReceiveAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _serverClosed.TrySetResult();
                        StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxServerMessageBytes)
                    {
                        throw new InvalidDataException("实时翻译服务消息过大");
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                HandleServerEvent(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportTransportFailure(exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleServerEvent(string json)
    {
        var serverEvent = RealtimeTranslationProtocol.ParseServerEvent(json);
        switch (serverEvent.Kind)
        {
            case RealtimeTranslationEventKind.SessionUpdated:
                StateChanged?.Invoke(RealtimeTranslationConnectionState.Listening);
                break;
            case RealtimeTranslationEventKind.SubtitleDelta when !string.IsNullOrEmpty(serverEvent.Delta):
                SubtitleDelta?.Invoke(serverEvent.Delta);
                break;
            case RealtimeTranslationEventKind.Closed:
                _serverClosed.TrySetResult();
                StateChanged?.Invoke(RealtimeTranslationConnectionState.Closed);
                break;
            case RealtimeTranslationEventKind.Error:
                Failed?.Invoke(serverEvent.ErrorCode, serverEvent.ErrorMessage);
                break;
        }
    }

    private async Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket
                    .SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _audioFrames?.Writer.TryComplete();
        try
        {
            if (_audioSendTask is not null)
            {
                await _audioSendTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            if (_socket?.State == WebSocketState.Open)
            {
                await SendTextAsync(RealtimeTranslationProtocol.CreateClose(), CancellationToken.None)
                    .ConfigureAwait(false);
                await _serverClosed.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch
        {
            // 退出时尽量按协议排空；超时则直接中止，不能阻塞页面离开。
        }
        finally
        {
            var lifetimeCts = Abort();
            await ObserveCompletionAsync(_audioSendTask).ConfigureAwait(false);
            await ObserveCompletionAsync(_receiveTask).ConfigureAwait(false);
            _audioSendTask = null;
            _receiveTask = null;
            lifetimeCts?.Dispose();
        }
    }

    private void ReportTransportFailure(Exception exception)
    {
        if (Volatile.Read(ref _stopping) == 0)
        {
            Failed?.Invoke("transport_error", exception.Message);
        }
    }

    private static string CreateSafetyIdentifier()
    {
        var localIdentity = $"{Environment.UserDomainName}\\{Environment.UserName}@{Environment.MachineName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(localIdentity));
        return $"oriontv_{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveCompletionAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 关闭路径只负责观察任务，错误已由状态事件处理。
        }
    }

    private CancellationTokenSource? Abort()
    {
        var lifetimeCts = _lifetimeCts;
        _lifetimeCts = null;
        lifetimeCts?.Cancel();
        _audioFrames?.Writer.TryComplete();
        try
        {
            _socket?.Abort();
        }
        catch
        {
        }

        _socket?.Dispose();
        _socket = null;
        lock (_frameSync)
        {
            _frameBuffer.Reset();
        }

        return lifetimeCts;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _stopping, 1);
        Abort()?.Dispose();
        // 同步退出不能等待仍在释放 SendAsync 的后台任务；信号量由 GC 回收，避免并发 Release/Dispose。
    }
}
