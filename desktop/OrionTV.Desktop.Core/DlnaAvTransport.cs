using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>投屏设备拒绝了一个 SOAP 动作（HTTP 非 2xx 或 SOAP Fault）。</summary>
public sealed class DlnaSoapException(string action, string message, int? statusCode = null, string? upnpErrorCode = null)
    : Exception(message)
{
    public string Action { get; } = action;

    public int? StatusCode { get; } = statusCode;

    public string? UpnpErrorCode { get; } = upnpErrorCode;
}

/// <summary>GetPositionInfo 的结果。</summary>
public sealed record DlnaPositionInfo(TimeSpan Position, TimeSpan Duration, string TrackUri);

/// <summary>AVTransport 服务的 SOAP 控制：推送地址、播放、暂停、停止、跳转、查询进度与状态。</summary>
public sealed class DlnaAvTransport(HttpClient httpClient)
{
    private const string EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string EncodingNamespace = "http://schemas.xmlsoap.org/soap/encoding/";

    public Task SetAvTransportUriAsync(DlnaDevice device, string url, string title, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            device,
            "SetAVTransportURI",
            [new("InstanceID", "0"), new("CurrentURI", url), new("CurrentURIMetaData", BuildDidl(url, title))],
            cancellationToken);

    public Task PlayAsync(DlnaDevice device, CancellationToken cancellationToken = default) =>
        InvokeAsync(device, "Play", [new("InstanceID", "0"), new("Speed", "1")], cancellationToken);

    public Task PauseAsync(DlnaDevice device, CancellationToken cancellationToken = default) =>
        InvokeAsync(device, "Pause", [new("InstanceID", "0")], cancellationToken);

    public Task StopAsync(DlnaDevice device, CancellationToken cancellationToken = default) =>
        InvokeAsync(device, "Stop", [new("InstanceID", "0")], cancellationToken);

    public Task SeekAsync(DlnaDevice device, TimeSpan position, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            device,
            "Seek",
            [new("InstanceID", "0"), new("Unit", "REL_TIME"), new("Target", FormatTime(position))],
            cancellationToken);

    public async Task<DlnaPositionInfo> GetPositionInfoAsync(DlnaDevice device, CancellationToken cancellationToken = default)
    {
        var xml = await InvokeAsync(device, "GetPositionInfo", [new("InstanceID", "0")], cancellationToken).ConfigureAwait(false);
        return new DlnaPositionInfo(
            ParseTime(DlnaDescription.Pick(xml, "RelTime")),
            ParseTime(DlnaDescription.Pick(xml, "TrackDuration")),
            DlnaDescription.Pick(xml, "TrackURI"));
    }

    /// <summary>返回 CurrentTransportState：PLAYING / PAUSED_PLAYBACK / STOPPED / TRANSITIONING / NO_MEDIA_PRESENT。</summary>
    public async Task<string> GetTransportStateAsync(DlnaDevice device, CancellationToken cancellationToken = default)
    {
        var xml = await InvokeAsync(device, "GetTransportInfo", [new("InstanceID", "0")], cancellationToken).ConfigureAwait(false);
        return DlnaDescription.Pick(xml, "CurrentTransportState").ToUpperInvariant();
    }

    public async Task<string> InvokeAsync(
        DlnaDevice device,
        string action,
        IReadOnlyList<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, device.ControlUrl);
        request.Content = new StringContent(BuildEnvelope(device.ServiceType, action, arguments), Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{device.ServiceType}#{action}\"");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DlnaSoapException(action, "投屏设备无响应");
        }
        catch (HttpRequestException ex)
        {
            throw new DlnaSoapException(action, $"连接投屏设备失败：{ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var isFault = Regex.IsMatch(body, @"<(?:\w+:)?Fault\b", RegexOptions.IgnoreCase);
            if (!response.IsSuccessStatusCode || isFault)
            {
                var code = DlnaDescription.Pick(body, "errorCode");
                var description = DlnaDescription.Pick(body, "errorDescription");
                var detail = description.Length > 0 ? description : code.Length > 0 ? $"错误码 {code}" : $"HTTP {(int)response.StatusCode}";
                throw new DlnaSoapException(
                    action,
                    $"{action} 失败：{detail}",
                    (int)response.StatusCode,
                    code.Length > 0 ? code : null);
            }

            return body;
        }
    }

    public static string BuildEnvelope(string serviceType, string action, IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append($"<s:Envelope xmlns:s=\"{EnvelopeNamespace}\" s:encodingStyle=\"{EncodingNamespace}\">");
        builder.Append($"<s:Body><u:{action} xmlns:u=\"{EscapeXml(serviceType)}\">");
        foreach (var (key, value) in arguments)
        {
            builder.Append($"<{key}>{EscapeXml(value)}</{key}>");
        }

        builder.Append($"</u:{action}></s:Body></s:Envelope>");
        return builder.ToString();
    }

    /// <summary>DIDL-Lite 元数据（片名 + 视频资源）。作为参数放进 SOAP 报文时还会再转义一次。</summary>
    public static string BuildDidl(string url, string title) =>
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
        + "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
        + "<item id=\"0\" parentID=\"-1\" restricted=\"1\">"
        + $"<dc:title>{EscapeXml(string.IsNullOrWhiteSpace(title) ? "OpaiTV" : title)}</dc:title>"
        + "<upnp:class>object.item.videoItem</upnp:class>"
        + $"<res protocolInfo=\"http-get:*:*:*\">{EscapeXml(url)}</res>"
        + "</item></DIDL-Lite>";

    public static string EscapeXml(string? value) =>
        (value ?? "")
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    /// <summary>REL_TIME 格式 HH:MM:SS。</summary>
    public static string FormatTime(TimeSpan position)
    {
        var total = (long)Math.Max(0, Math.Floor(position.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}");
    }

    /// <summary>解析 H:MM:SS(.fff)；NOT_IMPLEMENTED、空串等无法解析的值按 0。</summary>
    public static TimeSpan ParseTime(string? value)
    {
        var parts = (value ?? "").Trim().Split(':');
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
    }
}
