using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace OrionTV.Desktop.Core;

/// <summary>一条 SSDP 响应里与投屏有关的头。</summary>
public sealed record SsdpResponse(string Location, string Usn, string SearchTarget);

/// <summary>SSDP 的 UDP 传输层。真实实现是 <see cref="UdpSsdpTransport"/>，测试可注入假实现。</summary>
public interface ISsdpTransport : IDisposable
{
    /// <summary>把一条 M-SEARCH 报文发到多播目标。</summary>
    Task SendAsync(byte[] datagram, CancellationToken cancellationToken);

    /// <summary>等一条响应报文；取消时抛 <see cref="OperationCanceledException"/>。</summary>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>SSDP 报文的构造与解析（DLNA 投屏设备发现）。</summary>
public static class DlnaSsdp
{
    public const string MulticastAddress = "239.255.255.250";
    public const int MulticastPort = 1900;
    public const string AvTransportServiceType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string MediaRendererDeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";

    /// <summary>发现时依次发送的搜索目标。</summary>
    public static readonly IReadOnlyList<string> SearchTargets = [AvTransportServiceType, MediaRendererDeviceType];

    public static string BuildSearchMessage(string searchTarget, int mxSeconds = 2) =>
        string.Join(
            "\r\n",
            "M-SEARCH * HTTP/1.1",
            $"HOST: {MulticastAddress}:{MulticastPort}",
            "MAN: \"ssdp:discover\"",
            $"MX: {Math.Clamp(mxSeconds, 1, 5)}",
            $"ST: {searchTarget}",
            "",
            "");

    public static byte[] BuildSearchDatagram(string searchTarget, int mxSeconds = 2) =>
        Encoding.UTF8.GetBytes(BuildSearchMessage(searchTarget, mxSeconds));

    /// <summary>
    /// 解析 M-SEARCH 的响应（或 NOTIFY 通告）。头名大小写不敏感。
    /// 不是 SSDP 报文、没有 http(s) 的 LOCATION、或是 byebye 通告时返回 null。
    /// </summary>
    public static SsdpResponse? ParseResponse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var lines = message.Split('\n');
        var startLine = lines[0].Trim();
        var isResponse = startLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);
        var isNotify = startLine.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase);
        if (!isResponse && !isNotify)
        {
            return null;
        }

        if (isResponse)
        {
            var parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "200")
            {
                return null;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.Trim();
            var index = line.IndexOf(':');
            if (index > 0)
            {
                headers[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
        }

        if (headers.TryGetValue("NTS", out var nts) && nts.Contains("byebye", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!headers.TryGetValue("LOCATION", out var location)
            || !Uri.TryCreate(location, UriKind.Absolute, out var locationUri)
            || locationUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        headers.TryGetValue("USN", out var usn);
        if (!headers.TryGetValue("ST", out var target))
        {
            headers.TryGetValue("NT", out target);
        }

        return new SsdpResponse(locationUri.AbsoluteUri, usn ?? "", target ?? "");
    }

    public static SsdpResponse? ParseResponse(byte[] datagram)
    {
        try
        {
            return ParseResponse(Encoding.UTF8.GetString(datagram));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 真实的 SSDP 传输层：每个可用的 IPv4 网卡各开一个 UDP 套接字发 M-SEARCH，收设备的单播响应。
/// 目标端点与本机地址可配置（测试里指向回环上的假设备）。
/// </summary>
public sealed class UdpSsdpTransport : ISsdpTransport
{
    private readonly IPEndPoint _target;
    private readonly List<UdpClient> _clients = [];
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private bool _disposed;

    public UdpSsdpTransport(IPEndPoint? target = null, IEnumerable<IPAddress>? localAddresses = null)
    {
        _target = target ?? new IPEndPoint(IPAddress.Parse(DlnaSsdp.MulticastAddress), DlnaSsdp.MulticastPort);
        _token = _cts.Token;
        foreach (var address in localAddresses ?? LocalIPv4Addresses())
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.Bind(new IPEndPoint(address, 0));
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                    if (!IPAddress.Any.Equals(address))
                    {
                        // 明确指定多播出口网卡：多网卡（有线 + 无线 + 虚拟网卡）时不依赖系统默认路由。
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    }
                }
                catch
                {
                    // 选项设不上不影响同网段发现。
                }

                _clients.Add(client);
                _ = PumpAsync(client);
            }
            catch
            {
                // 这块网卡绑不上就跳过。
            }
        }
    }

    /// <summary>成功绑定的本机套接字数。</summary>
    public int SocketCount => _clients.Count;

    public async Task SendAsync(byte[] datagram, CancellationToken cancellationToken)
    {
        if (_clients.Count == 0)
        {
            throw new InvalidOperationException("没有可用的网络接口");
        }

        var sent = 0;
        Exception? last = null;
        foreach (var client in _clients)
        {
            try
            {
                await client.SendAsync(datagram, _target, cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (sent == 0)
        {
            throw new InvalidOperationException("发现报文发送失败", last);
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken) =>
        await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    private async Task PumpAsync(UdpClient client)
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    var result = await client.ReceiveAsync(_token).ConfigureAwait(false);
                    _inbox.Writer.TryWrite(result.Buffer);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows 上 UDP 收到 ICMP 端口不可达会报 ConnectionReset：与这个套接字本身无关，继续收。
                }
            }
        }
        catch
        {
            // 套接字关闭或网络错误：这一路结束。
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch
        {
            // 枚举失败时退回 Any。
        }

        if (addresses.Count == 0)
        {
            addresses.Add(IPAddress.Any);
        }

        return addresses;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        foreach (var client in _clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _inbox.Writer.TryComplete();
        _cts.Dispose();
    }
}
