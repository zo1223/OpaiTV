using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using QRCoder;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 多端同步视频源：不经过服务器，把当前全部视频源直接编码进一段链接（同时显示为二维码），
/// 另一台设备粘贴或扫码即可一次性导入。与 RN 端 services/videoSourceSync.ts 的格式必须保持一致：
/// <code>
/// opaitv-sync://z1/&lt;base64url(raw deflate(订阅 JSON))&gt;   导出时使用（压缩后链接更短，二维码更容易扫）
/// opaitv-sync://v1/&lt;base64url(订阅 JSON)&gt;                不压缩，导入时同样识别
/// opaitv-sync://p1/&lt;id&gt;/&lt;序号&gt;/&lt;总数&gt;/&lt;片段&gt;              完整链接太长、一张二维码放不下时的分片（只用于二维码）
/// </code>
/// 订阅 JSON 就是现有的订阅格式 { "api_site": [ { key, name, api, detail?, adult? … } ] }，
/// api_site 用数组形式以保持源的顺序。
/// </summary>
public static partial class VideoSourceSyncLink
{
    public const string Prefix = "opaitv-sync://";

    public const string InvalidLinkError = "INVALID_VIDEO_SOURCE_SYNC_LINK";

    /// <summary>粘贴的是分片二维码里的一片，不是完整链接。</summary>
    public const string IncompleteLinkError = "INCOMPLETE_VIDEO_SOURCE_SYNC_LINK";

    /// <summary>链接来自更新的版本，当前版本不认识它的编码方式。</summary>
    public const string UnsupportedLinkError = "UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK";

    public const string EmptyError = "EMPTY_VIDEO_SOURCE_SYNC";

    /// <summary>不超过这个长度的链接放进一张二维码（纠错等级 L 下约为版本 22，105×105 个模块），屏幕对拍仍然好扫。</summary>
    public const int QrSingleMaxLength = 1000;

    /// <summary>分片二维码每片携带的链接长度（加上片头后约为版本 17，85×85 个模块）。</summary>
    public const int QrChunkLength = 600;

    private const string KindPlain = "v1";
    private const string KindDeflate = "z1";
    private const string KindPart = "p1";

    // 解压后的 JSON 上限：正常的源列表远小于它，用来挡住恶意构造的压缩数据
    private const int MaxPayloadBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        // 中文名称原样写出，不转成 \uXXXX（链接更短）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^opaitv-sync://([a-z0-9]+)/(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    /// <summary>文本是不是同步链接（含分片）。只看前缀，不保证内容能解开。</summary>
    public static bool IsSyncLink(string? text) =>
        Compact(text).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>按现有订阅 JSON 格式序列化；成人标记原样保留，未标记的源不写该字段。</summary>
    public static string SerializePayload(IEnumerable<VideoSource> sources)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Api) || string.IsNullOrWhiteSpace(source.Name))
            {
                continue;
            }

            var map = new Dictionary<string, object>
            {
                ["key"] = string.IsNullOrWhiteSpace(source.Key) ? ExtraVideoSourcesStore.SlugKey(source.Name) : source.Key.Trim(),
                ["name"] = source.Name.Trim(),
                ["api"] = source.Api.Trim().TrimEnd('/'),
            };
            if (!string.IsNullOrWhiteSpace(source.Detail))
            {
                map["detail"] = source.Detail.Trim().TrimEnd('/');
            }

            var protocol = VideoSourceProtocols.Normalize(source.Protocol);
            if (!string.Equals(protocol, VideoSourceProtocols.AppleCms, StringComparison.Ordinal))
            {
                map["protocol"] = protocol;
            }

            if (!string.IsNullOrWhiteSpace(source.Dialect))
            {
                map["dialect"] = source.Dialect.Trim();
            }

            if (source.Options is { Count: > 0 })
            {
                map["options"] = source.Options.ToDictionary(option => option.Key, option => option.Value);
            }

            if (source.Adult)
            {
                map["adult"] = true;
            }

            list.Add(map);
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["api_site"] = list }, PayloadOptions);
    }

    /// <summary>把全部视频源编码成一段同步链接。没有可导出的源时抛出 <see cref="EmptyError"/>。</summary>
    public static string Encode(IEnumerable<VideoSource> sources)
    {
        var list = sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Api) && !string.IsNullOrWhiteSpace(source.Name))
            .ToList();
        if (list.Count == 0)
        {
            throw new InvalidOperationException(EmptyError);
        }

        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(SerializePayload(list)));
        }

        return Prefix + KindDeflate + "/" + ToBase64Url(buffer.ToArray());
    }

    /// <summary>
    /// 解出同步链接里的全部源（与订阅解析走同一套字段整理，成人标记保留）。
    /// 链接无效时抛出 <see cref="InvalidLinkError"/>；是分片二维码里的一片时抛出 <see cref="IncompleteLinkError"/>。
    /// </summary>
    public static IReadOnlyList<VideoSource> Decode(string? link)
    {
        var match = LinkRegex().Match(Compact(link));
        if (!match.Success)
        {
            throw new InvalidOperationException(InvalidLinkError);
        }

        var kind = match.Groups[1].Value.ToLowerInvariant();
        if (kind == KindPart)
        {
            throw new InvalidOperationException(IncompleteLinkError);
        }

        if (kind != KindPlain && kind != KindDeflate)
        {
            throw new InvalidOperationException(UnsupportedLinkError);
        }

        try
        {
            var bytes = FromBase64Url(match.Groups[2].Value);
            if (bytes.Length > MaxPayloadBytes)
            {
                throw new InvalidOperationException(InvalidLinkError);
            }

            var json = new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(kind == KindDeflate ? Inflate(bytes) : bytes);
            // 只认 JSON：订阅解析对非 JSON 内容还会尝试 base58，同步链接不需要这条退路
            using (JsonDocument.Parse(json))
            {
            }

            return VideoSourceSubscriptionParser.Parse(json).Sources;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(InvalidLinkError);
        }
    }

    /// <summary>
    /// 二维码里放的文本。链接不长时就是链接本身（一张码）；太长时拆成多张分片码，
    /// 每张都足够稀疏、能扫出来，手机扫码端把它们拼回完整链接。
    /// </summary>
    public static IReadOnlyList<string> BuildQrPayloads(string link)
    {
        if (link.Length <= QrSingleMaxLength)
        {
            return [link];
        }

        var total = (link.Length + QrChunkLength - 1) / QrChunkLength;
        var size = (link.Length + total - 1) / total;
        var id = Checksum(link);
        var parts = new List<string>(total);
        for (var index = 0; index < total; index++)
        {
            var start = index * size;
            var chunk = link.Substring(start, Math.Min(size, link.Length - start));
            parts.Add($"{Prefix}{KindPart}/{id}/{index + 1}/{total}/{chunk}");
        }

        return parts;
    }

    /// <summary>链接和分片编号只含 ASCII，两端按同样的方式逐字符计算（FNV-1a 32 位，8 位十六进制）。</summary>
    public static string Checksum(string text)
    {
        var hash = 0x811c9dc5u;
        foreach (var character in text)
        {
            hash ^= character;
            hash = unchecked(hash * 0x01000193u);
        }

        return hash.ToString("x8");
    }

    // 聊天软件、邮件转发长链接时常会插入换行和空格
    private static string Compact(string? text) =>
        string.IsNullOrEmpty(text) ? "" : WhitespaceRegex().Replace(text, "");

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // 标准 base64 的字符（+ / =）也认，手工拼出来的链接不必非得是 base64url
    private static byte[] FromBase64Url(string text)
    {
        var value = text.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
    }

    // 边解压边累计输出大小：压缩数据可以用很小的体积展开成极大的内容，不能全部解开再检查
    private static byte[] Inflate(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxPayloadBytes)
            {
                throw new InvalidOperationException(InvalidLinkError);
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}

/// <summary>一张二维码的像素：每个模块一个字节（0 = 黑，255 = 白），含四周的空白边，逐行排列。</summary>
public sealed record SyncQrImage(int Size, byte[] Pixels)
{
    public bool IsDark(int x, int y) => Pixels[y * Size + x] == 0;

    /// <summary>按整数倍放大：每个模块变成 factor×factor 个像素。</summary>
    public SyncQrImage Scale(int factor)
    {
        factor = Math.Max(1, factor);
        var size = Size * factor;
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[y * size + x] = Pixels[y / factor * Size + x / factor];
            }
        }

        return new SyncQrImage(size, pixels);
    }
}

/// <summary>同步链接的二维码（界面按最近邻放大显示即可）。纠错等级用 L：屏幕显示不会破损，模块越少越好扫。</summary>
public static class VideoSourceSyncQr
{
    /// <summary>二维码规范要求四周留出至少 4 个模块的空白。</summary>
    public const int QuietZoneModules = 4;

    public static SyncQrImage Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L, forceUtf8: false);
        var matrix = data.ModuleMatrix;

        // QRCoder 的矩阵是否自带空白边随版本不同：先裁到实际内容，再统一补上规定的空白
        var first = int.MaxValue;
        var last = -1;
        for (var y = 0; y < matrix.Count; y++)
        {
            for (var x = 0; x < matrix[y].Length; x++)
            {
                if (matrix[y][x])
                {
                    first = Math.Min(first, Math.Min(x, y));
                    last = Math.Max(last, Math.Max(x, y));
                }
            }
        }

        var modules = last - first + 1;
        var size = modules + QuietZoneModules * 2;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < modules; y++)
        {
            for (var x = 0; x < modules; x++)
            {
                if (matrix[first + y][first + x])
                {
                    pixels[(y + QuietZoneModules) * size + x + QuietZoneModules] = 0;
                }
            }
        }

        return new SyncQrImage(size, pixels);
    }
}
