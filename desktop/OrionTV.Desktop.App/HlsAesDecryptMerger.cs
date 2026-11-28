using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.App;

/// <summary>
/// 将已下载完成、但没有边下边合并的 HLS 缓存合并为单个 merged.ts：
/// AES-128 加密源先逐片解密；没有密钥、只是分片后缀不是 .ts 的源（例如把 TS 伪装成 .jpeg/.png 的图床分片）直接按内容合并。
/// 通用流程：读 offline.m3u8 → 取 key/IV → 逐片 AES-CBC 解密（明文分片跳过）→ 去掉分片头部的伪装数据 → 接到同一条时间轴上 → 拼成单文件。
/// </summary>
internal static class HlsAesDecryptMerger
{
    private static readonly Regex ExtInfRegex = new(@"^#EXTINF:([\d.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyLineRegex = new(
        @"^#EXT-X-KEY:(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MediaSequenceRegex = new(
        @"^#EXT-X-MEDIA-SEQUENCE:(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AttrRegex = new(
        @"([A-Z0-9-]+)=(""(?:[^""]|"""")*""|[^,]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int SegmentProbeBytes = 64 * 1024;
    private const int StandardTsPacketLength = 188;

    internal sealed record DecryptPlan(
        IReadOnlyList<DecryptSegment> Segments,
        bool HasAes128);

    internal sealed record DecryptSegment(
        string FileName,
        byte[] Key,
        byte[] Iv);

    /// <summary>
    /// 这个缓存目录能否由本类合并成 merged.ts（还没有成品、清单在、分片在）：
    /// <list type="bullet">
    /// <item>清单带 AES-128 密钥：可以（解密后再判断是不是 TS）。</item>
    /// <item>清单没有密钥：第一片的内容必须是 MPEG-TS。分片后缀不作数——有的源把 TS 分片挂在图床上，后缀是 .jpeg/.png。</item>
    /// <item>fMP4（#EXT-X-MAP）或其它加密方式（如 SAMPLE-AES）：不行。</item>
    /// </list>
    /// </summary>
    internal static bool LooksDecryptable(string directory)
    {
        try
        {
            var playlist = Path.Combine(directory, "offline.m3u8");
            var merged = Path.Combine(directory, VodCacheSession.MergedFileName);
            if (!File.Exists(playlist) || (File.Exists(merged) && new FileInfo(merged).Length > 0))
            {
                return false;
            }

            var hasAes = false;
            var hasUnsupportedKey = false;
            string? firstSegment = null;
            foreach (var raw in File.ReadLines(playlist))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
                {
                    // fMP4 需要 init 段，不能按 TS 拼接。
                    return false;
                }

                var keyMatch = KeyLineRegex.Match(line);
                if (keyMatch.Success)
                {
                    var method = ParseAttributes(keyMatch.Groups[1].Value)
                        .GetValueOrDefault("METHOD", "NONE")
                        .Trim()
                        .ToUpperInvariant();
                    if (method == "AES-128")
                    {
                        hasAes = true;
                    }
                    else if (method is not ("NONE" or ""))
                    {
                        hasUnsupportedKey = true;
                    }

                    continue;
                }

                if (line[0] != '#')
                {
                    firstSegment ??= LocalFileNameOf(line);
                }
            }

            if (hasUnsupportedKey)
            {
                return false;
            }

            if (hasAes)
            {
                // 至少有一片已落盘。
                return Directory.EnumerateFiles(directory, "segment_*", SearchOption.TopDirectoryOnly).Any();
            }

            return !string.IsNullOrWhiteSpace(firstSegment)
                && SegmentFileLooksLikeTs(Path.Combine(directory, firstSegment));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清单里是否带 AES-128 密钥（只用于界面文案：区分“解密合并”和“合并”）。</summary>
    internal static bool HasAes128Key(string directory)
    {
        try
        {
            var playlist = Path.Combine(directory, "offline.m3u8");
            return File.Exists(playlist)
                && File.ReadLines(playlist).Any(line =>
                {
                    var keyMatch = KeyLineRegex.Match(line.Trim());
                    return keyMatch.Success
                        && string.Equals(
                            ParseAttributes(keyMatch.Groups[1].Value).GetValueOrDefault("METHOD", "").Trim(),
                            "AES-128",
                            StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return false;
        }
    }

    private static bool SegmentFileLooksLikeTs(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[(int)Math.Min(stream.Length, SegmentProbeBytes)];
        stream.ReadExactly(head);
        return head.Length > 0 && FindMergeableTsStart(head) >= 0;
    }

    internal static DecryptPlan BuildPlan(string directory)
    {
        var playlistPath = Path.Combine(directory, "offline.m3u8");
        if (!File.Exists(playlistPath))
        {
            throw new InvalidOperationException("OFFLINE_PLAYLIST_MISSING");
        }

        var lines = File.ReadAllLines(playlistPath);
        byte[]? currentKey = null;
        byte[]? currentIv = null;
        var mediaSequence = 0L;
        var hasExplicitIv = false;
        var hasAes = false;
        var segments = new List<DecryptSegment>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var seqMatch = MediaSequenceRegex.Match(line);
            if (seqMatch.Success && long.TryParse(seqMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
            {
                mediaSequence = seq;
                continue;
            }

            var keyMatch = KeyLineRegex.Match(line);
            if (keyMatch.Success)
            {
                var attrs = ParseAttributes(keyMatch.Groups[1].Value);
                var method = attrs.GetValueOrDefault("METHOD", "NONE").Trim().ToUpperInvariant();
                if (method is "NONE" or "")
                {
                    currentKey = null;
                    currentIv = null;
                    hasExplicitIv = false;
                    continue;
                }

                if (method != "AES-128")
                {
                    throw new NotSupportedException($"UNSUPPORTED_HLS_KEY_METHOD {method}");
                }

                if (!attrs.TryGetValue("URI", out var uri) || string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("HLS_KEY_URI_MISSING");
                }

                uri = uri.Trim().Trim('"');
                var keyPath = ResolveLocalAssetPath(directory, uri);
                if (!File.Exists(keyPath))
                {
                    throw new FileNotFoundException("HLS_KEY_FILE_MISSING", keyPath);
                }

                var keyBytes = File.ReadAllBytes(keyPath);
                if (keyBytes.Length != 16)
                {
                    throw new InvalidOperationException($"HLS_KEY_LENGTH_INVALID {keyBytes.Length}");
                }

                currentKey = keyBytes;
                hasAes = true;
                if (attrs.TryGetValue("IV", out var ivText) && !string.IsNullOrWhiteSpace(ivText))
                {
                    currentIv = ParseIv(ivText.Trim().Trim('"'));
                    hasExplicitIv = true;
                }
                else
                {
                    currentIv = null;
                    hasExplicitIv = false;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            // 媒体 URI：offline 清单里是相对文件名 segment_xxxxx.ts（后缀跟随源地址，也可能是 .jpeg 之类）
            var fileName = LocalFileNameOf(line);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var segmentPath = Path.Combine(directory, fileName);
            if (!File.Exists(segmentPath))
            {
                throw new FileNotFoundException("SEGMENT_FILE_MISSING", segmentPath);
            }

            if (currentKey is null)
            {
                // 明文分片：用空 key 标记，解密时原样拷贝。
                segments.Add(new DecryptSegment(fileName, [], []));
            }
            else
            {
                var iv = hasExplicitIv && currentIv is not null
                    ? currentIv
                    : SequenceToIv(mediaSequence);
                segments.Add(new DecryptSegment(fileName, currentKey, iv));
            }

            mediaSequence++;
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("NO_SEGMENTS_TO_DECRYPT");
        }

        return new DecryptPlan(segments, hasAes);
    }

    internal static async Task<(string MergedPath, long[] SegmentLengths)> DecryptAndMergeAsync(
        string directory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 没有密钥的清单也走这里：分片后缀不是 .ts 的明文源（伪装成图片的 TS）下载时不会边下边合并。
        var plan = BuildPlan(directory);

        var mergedPath = Path.Combine(directory, VodCacheSession.MergedFileName);
        var tempPath = mergedPath + ".decrypting";
        TryDeleteFile(tempPath);

        var total = plan.Segments.Count;
        var lengths = new long[total];
        // 分片之间时间戳可能重新起算（#EXT-X-DISCONTINUITY、逐片切片）；原样拼接后播放器只能读到一小段时长。
        var timeline = new MpegTsTimelineStitcher();
        long disguisedBytes = 0;
        try
        {
            await using var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (var index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = plan.Segments[index];
                var segmentPath = Path.Combine(directory, segment.FileName);
                byte[] plain;
                if (segment.Key.Length == 0)
                {
                    plain = await File.ReadAllBytesAsync(segmentPath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var cipher = await File.ReadAllBytesAsync(segmentPath, cancellationToken).ConfigureAwait(false);
                    plain = DecryptAes128Cbc(cipher, segment.Key, segment.Iv);
                }

                // 解密结果必须是 MPEG-TS 才能拼成 merged.ts：密钥不对解出的乱码、fMP4 分片（需要 init 段）
                // 一旦拼进去，成品无法播放，而原始分片随后会被清理。这里中止，保留原缓存（仍可按清单播放）。
                var tsStart = FindMergeableTsStart(plain);
                if (tsStart < 0)
                {
                    throw new InvalidDataException($"DECRYPTED_SEGMENT_NOT_MPEGTS {segment.FileName} bytes={plain.Length}");
                }

                // 分片头部的伪装数据（假图片头）不写进成品：留在文件开头会让播放器把 merged.ts 当成图片，
                // 留在中间则是每片一段垃圾数据。
                var payload = plain.AsMemory(tsStart);
                disguisedBytes += tsStart;
                timeline.Restamp(payload.Span);
                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                lengths[index] = payload.Length;
                progress?.Report((index + 1) / (double)total);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 半成品不留在缓存目录里。
            TryDeleteFile(tempPath);
            throw;
        }

        if (File.Exists(mergedPath))
        {
            File.Delete(mergedPath);
        }

        File.Move(tempPath, mergedPath);
        if (timeline.Discontinuities > 0 || disguisedBytes > 0)
        {
            VodCacheLog.Info(
                $"decrypt-merge-timeline dir={directory} segs={total} aes={plan.HasAes128} discontinuities={timeline.Discontinuities} strippedHeaderBytes={disguisedBytes}");
        }

        return (mergedPath, lengths);
    }

    /// <summary>
    /// 明文分片里可以拼进 merged.ts 的数据从哪个偏移开始；不是 MPEG-TS 返回 -1。
    /// 不足以判断的极短分片（凑不满探测所需的包数）整片放行；
    /// 其余必须能找到按包长对齐的 TS 同步字节，同步字节之前的伪装数据（假图片头）不算在内。
    /// 192/204 字节包的同步字节之前本来就有附加字节，这两种包长不裁头，保持原样。
    /// </summary>
    internal static int FindMergeableTsStart(ReadOnlySpan<byte> plain)
    {
        if (plain.Length < MpegTsTimelineStitcher.MinProbeBytes)
        {
            return 0;
        }

        if (!MpegTsTimelineStitcher.TryFindFraming(plain, out var start, out var packetSize))
        {
            return -1;
        }

        return packetSize == StandardTsPacketLength ? start : 0;
    }

    private static string LocalFileNameOf(string playlistLine) =>
        Path.GetFileName(playlistLine.Split('?', 2)[0].Trim().Replace('\\', '/'));

    internal static byte[] DecryptAes128Cbc(byte[] cipher, byte[] key, byte[] iv)
    {
        if (cipher.Length == 0)
        {
            return [];
        }

        if (cipher.Length % 16 != 0)
        {
            throw new InvalidOperationException($"CIPHER_LENGTH_NOT_BLOCK_ALIGNED {cipher.Length}");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    internal static byte[] SequenceToIv(long mediaSequence)
    {
        // HLS: 未声明 IV 时，用媒体序号的 128-bit 大端表示。
        var iv = new byte[16];
        var value = unchecked((ulong)mediaSequence);
        for (var index = 15; index >= 8; index--)
        {
            iv[index] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return iv;
    }

    private static byte[] ParseIv(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (hex.Length != 32 || !IsHex(hex))
        {
            throw new InvalidOperationException($"HLS_IV_INVALID {text}");
        }

        var bytes = new byte[16];
        for (var index = 0; index < 16; index++)
        {
            bytes[index] = byte.Parse(hex.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            var isHex = character is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttrRegex.Matches(text))
        {
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            }

            result[key] = value;
        }

        return result;
    }

    private static string ResolveLocalAssetPath(string directory, string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.IsFile)
        {
            return absolute.LocalPath;
        }

        // offline 清单里密钥一般是 asset_000.key 这种相对名。
        var fileName = Path.GetFileName(uri.Replace('\\', '/').Split('?', 2)[0]);
        return Path.Combine(directory, string.IsNullOrWhiteSpace(fileName) ? uri : fileName);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
