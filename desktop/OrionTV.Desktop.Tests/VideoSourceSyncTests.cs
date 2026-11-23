using System.IO.Compression;
using System.Text;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>多端同步视频源：同步链接编解码、二维码、导入合并（对齐 RN 端 services/videoSourceSync.ts）。</summary>
public class VideoSourceSyncTests
{
    // 与 RN 端 services/__tests__/videoSourceSync.test.ts 用的是同一组源
    private static readonly VideoSource[] Sources =
    [
        new("dytt", "电影天堂", "https://caiji.dytt.example/api.php/provide/vod", "https://caiji.dytt.example"),
        new("1", "数字 key", "http://192.168.1.2:8080/vod"),
        new("adult_a", "成人源 A", "https://adult.example/api.php/provide/vod", Adult: true),
        new("custom_a.example_8443", "a.example:8443", "https://a.example:8443/api.php/provide/vod"),
    ];

    // RN 端 encodeVideoSourceSyncLink 对同一组源生成的链接（fflate 压缩）
    private const string MobileLink =
        "opaitv-sync://z1/hY-9DgFBFIVfRW4tMxaRNZ3nENlMzCSGXTuxd4XINiqlgmgVGoWEQrOJt7G8hrHB-gvtN-ecb-4IuFZOoFACq4-gI4fAQAwRIQ9d7hkK59k-OeyS1TpZjg01eQNbiDpglDa5aityLRA54J52JTUBolua6p7fV0LSvi9MTUjkyv3ZhCh__4GV6U_zbbJZ5K78WW4WrGqRWBWbWKTI7IJdSE3ZBhehiw5_WppMj3F8iqe52scdafjPCWkGGPZCmVmaYYC-5_B717HL5VLmfHB242_al-ev3qgRXQA";

    // RN 端对 ManySources(12) 生成的链接（fflate 的压缩结果与 .NET DeflateStream 不同）
    private const string MobileLinkOfTwelve =
        "opaitv-sync://z1/pdQ9TsMwFMDxqyDPJYntOH7OVRCqosZSAy2x8lGBqm6IrqyIIzCzcCCgtyAPZ3JwkNX5H-en6DlvTwpTLduq0yS_2pNb_UBy0tZ9s9LLhCzIXbEdCjkdj6fXp-_3x8-P56-3lwtMw8GhrLvOtHkcr26SCF9zmUT6vtiajY6HByKzNrFp6l1V6nhXl3is7Dcdybum14eFI1K_iMkVUyalUpals-xEYn4JkyuBEEKBlViYxP0SJldiwLlKrcTDpNQvYXIlSSlV3EppmCT8EiZXohwUjHMSYVLmlzC5ksgyCeOcsjBJ-iVMrqRUKkBaSZ5x5cHPYppeecZhHBqEfaDyS5imVz6hwKykwiQ6szl-2-TSD_-xHMdG5xfIFJtbGn9tDZkIKcfJ0X_WxvXhBw";

    private static void AssertSameSources(IReadOnlyList<VideoSource> expected, IReadOnlyList<VideoSource> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var (left, right) = (expected[index], actual[index]);
            Assert.Equal(
                (left.Key, left.Name, left.Api, left.Detail, left.Protocol, left.Dialect, left.Adult),
                (right.Key, right.Name, right.Api, right.Detail, right.Protocol, right.Dialect, right.Adult));
            Assert.Equal(
                left.Options?.OrderBy(option => option.Key).ToList() ?? [],
                right.Options?.OrderBy(option => option.Key).ToList() ?? []);
        }
    }

    private static List<VideoSource> ManySources(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new VideoSource(
                $"source_{index}",
                $"采集资源站 {index}",
                // 每个地址都不一样，压缩不掉
                $"https://cj{index * 2654435761L % 1000003}.site-{index}.example/api.php/provide/vod",
                Adult: index % 7 == 0))
            .ToList();

    private static string PlainLink(string json) =>
        "opaitv-sync://v1/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    // ---- 编解码 ----

    [Fact]
    public void Encode_ThenDecode_KeepsEverySourceIntact()
    {
        var link = VideoSourceSyncLink.Encode(Sources);

        Assert.StartsWith("opaitv-sync://z1/", link);
        // base64url：可以直接放进链接和二维码，不含 + / =
        Assert.Matches("^[A-Za-z0-9_-]+$", link["opaitv-sync://z1/".Length..]);
        Assert.True(VideoSourceSyncLink.IsSyncLink(link));
        AssertSameSources(Sources, VideoSourceSyncLink.Decode(link));
    }

    [Fact]
    public void Encode_ThenDecode_KeepsDesktopOnlyFields()
    {
        VideoSource[] sources =
        [
            new("xml", "XML 源", "https://xml.example/api.php/provide/vod/at/xml", Protocol: VideoSourceProtocols.MacCmsXml, Adult: true),
            new(
                "dialect",
                "方言源",
                "https://dialect.example/api",
                "https://dialect.example",
                VideoSourceProtocols.MacCmsJson,
                "videolist",
                new Dictionary<string, string> { ["searchAc"] = "list", ["pageAc"] = "videolist" }),
        ];

        AssertSameSources(sources, VideoSourceSyncLink.Decode(VideoSourceSyncLink.Encode(sources)));
    }

    [Fact]
    public void SerializePayload_UsesTheSubscriptionFormatAndKeepsTheAdultMarkAsIs()
    {
        var payload = VideoSourceSyncLink.SerializePayload(Sources);

        // 就是现有的订阅 JSON：订阅解析器能直接读
        AssertSameSources(Sources, VideoSourceSubscriptionParser.Parse(payload).Sources);
        // 与 RN 端 serializeVideoSourcesForSync 的输出逐字节相同；未标记的源不写 adult
        Assert.Equal(
            """{"api_site":[{"key":"dytt","name":"电影天堂","api":"https://caiji.dytt.example/api.php/provide/vod","detail":"https://caiji.dytt.example"},{"key":"1","name":"数字 key","api":"http://192.168.1.2:8080/vod"},{"key":"adult_a","name":"成人源 A","api":"https://adult.example/api.php/provide/vod","adult":true},{"key":"custom_a.example_8443","name":"a.example:8443","api":"https://a.example:8443/api.php/provide/vod"}]}""",
            payload);
    }

    [Fact]
    public void Decode_ImportsLinksExportedByTheMobileApp()
    {
        AssertSameSources(Sources, VideoSourceSyncLink.Decode(MobileLink));

        Assert.NotEqual(MobileLinkOfTwelve, VideoSourceSyncLink.Encode(ManySources(12)));
        AssertSameSources(ManySources(12), VideoSourceSyncLink.Decode(MobileLinkOfTwelve));
    }

    [Fact]
    public void Decode_AcceptsPlainV1Links_StandardBase64_AndPastedWhitespace()
    {
        const string json = """{ "api_site": { "a": { "name": "源 A", "api": "https://a.example/vod/", "is_adult": 1 }, "b": { "name": "B", "api": "https://b.example/vod" } } }""";
        var plain = PlainLink(json);
        VideoSource[] expected = [new("a", "源 A", "https://a.example/vod", Adult: true), new("b", "B", "https://b.example/vod")];

        AssertSameSources(expected, VideoSourceSyncLink.Decode(plain));
        AssertSameSources(
            expected,
            VideoSourceSyncLink.Decode("OpaiTV-Sync://V1/" + plain["opaitv-sync://v1/".Length..].TrimEnd('=').Replace('+', '-').Replace('/', '_')));

        var link = VideoSourceSyncLink.Encode(Sources);
        var wrapped = $"  {link[..40]}\n{link[40..90]} \r\n{link[90..]}\n";
        Assert.True(VideoSourceSyncLink.IsSyncLink(wrapped));
        AssertSameSources(Sources, VideoSourceSyncLink.Decode(wrapped));
    }

    [Fact]
    public void Decode_RejectsAnythingThatIsNotACompleteSyncLink()
    {
        var link = VideoSourceSyncLink.Encode(Sources);

        Assert.False(VideoSourceSyncLink.IsSyncLink("https://a.example/api.php/provide/vod"));
        Assert.False(VideoSourceSyncLink.IsSyncLink(""));
        Assert.False(VideoSourceSyncLink.IsSyncLink(null));
        Assert.Equal(
            VideoSourceSyncLink.EmptyError,
            Assert.Throws<InvalidOperationException>(() => VideoSourceSyncLink.Encode([])).Message);

        string?[] broken =
        [
            null,
            "https://a.example/vod",
            "opaitv-sync://",
            "opaitv-sync://z1/",
            "opaitv-sync://z1/%%%",
            "opaitv-sync://z1/AAAA",
            link[..^12],
            PlainLink("not json"),
            PlainLink("""{"api_site":{}}"""),
            PlainLink("""{"sources":[]}"""),
        ];
        foreach (var value in broken)
        {
            Assert.Equal(
                VideoSourceSyncLink.InvalidLinkError,
                Assert.Throws<InvalidOperationException>(() => VideoSourceSyncLink.Decode(value)).Message);
        }

        Assert.Equal(
            VideoSourceSyncLink.UnsupportedLinkError,
            Assert.Throws<InvalidOperationException>(() => VideoSourceSyncLink.Decode("opaitv-sync://z9/AAAA")).Message);
    }

    [Fact]
    public void Decode_RefusesCompressedDataThatExpandsBeyondThePayloadLimit()
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes($$"""{"api_site":[],"pad":"{{new string('a', 3 * 1024 * 1024)}}"}"""));
        }

        Assert.True(buffer.Length < 10_000);
        Assert.Equal(
            VideoSourceSyncLink.InvalidLinkError,
            Assert.Throws<InvalidOperationException>(
                () => VideoSourceSyncLink.Decode("opaitv-sync://z1/" + Convert.ToBase64String(buffer.ToArray()))).Message);
    }

    // ---- 二维码 ----

    [Fact]
    public void BuildQrPayloads_PutsAShortLinkIntoOneQrCodeAsIs()
    {
        var link = VideoSourceSyncLink.Encode(Sources);

        Assert.True(link.Length <= VideoSourceSyncLink.QrSingleMaxLength);
        Assert.Equal([link], VideoSourceSyncLink.BuildQrPayloads(link));
    }

    [Fact]
    public void BuildQrPayloads_SplitsALongLinkIntoPartsThatJoinBackToTheLink()
    {
        var many = ManySources(120);
        var link = VideoSourceSyncLink.Encode(many);
        var parts = VideoSourceSyncLink.BuildQrPayloads(link);

        Assert.True(link.Length > VideoSourceSyncLink.QrSingleMaxLength);
        Assert.True(parts.Count > 1);

        var joined = new StringBuilder();
        for (var index = 0; index < parts.Count; index++)
        {
            // 片头 + 片段不超过二维码版本 17（纠错等级 L）的容量 644 字节
            Assert.True(parts[index].Length <= VideoSourceSyncLink.QrChunkLength + 44);
            var header = $"opaitv-sync://p1/{VideoSourceSyncLink.Checksum(link)}/{index + 1}/{parts.Count}/";
            Assert.StartsWith(header, parts[index]);
            joined.Append(parts[index][header.Length..]);

            // 单独一片不是完整链接
            Assert.Equal(
                VideoSourceSyncLink.IncompleteLinkError,
                Assert.Throws<InvalidOperationException>(() => VideoSourceSyncLink.Decode(parts[index])).Message);
        }

        Assert.Equal(link, joined.ToString());
        AssertSameSources(many, VideoSourceSyncLink.Decode(joined.ToString()));
    }

    [Fact]
    public void Checksum_MatchesTheMobileImplementation()
    {
        // FNV-1a 32 位的公开测试向量；RN 端分片二维码的编号用同一算法校验
        Assert.Equal("811c9dc5", VideoSourceSyncLink.Checksum(""));
        Assert.Equal("e40c292c", VideoSourceSyncLink.Checksum("a"));
        Assert.Equal("bf9cf968", VideoSourceSyncLink.Checksum("foobar"));
    }

    [Fact]
    public void Qr_Render_ProducesAValidLookingCodeWithAQuietZone()
    {
        var link = VideoSourceSyncLink.Encode(Sources);
        var qr = VideoSourceSyncQr.Render(link);
        var quiet = VideoSourceSyncQr.QuietZoneModules;
        var modules = qr.Size - quiet * 2;

        Assert.Equal(qr.Size * qr.Size, qr.Pixels.Length);
        // 二维码边长 = 17 + 4 × 版本号
        Assert.Equal(1, modules % 4);
        Assert.InRange((modules - 17) / 4, 1, 40);
        Assert.All(qr.Pixels, pixel => Assert.True(pixel is 0 or 255));

        // 四周的空白边全白
        for (var i = 0; i < qr.Size; i++)
        {
            for (var margin = 0; margin < quiet; margin++)
            {
                Assert.False(qr.IsDark(i, margin));
                Assert.False(qr.IsDark(margin, i));
                Assert.False(qr.IsDark(i, qr.Size - 1 - margin));
                Assert.False(qr.IsDark(qr.Size - 1 - margin, i));
            }
        }

        // 三个角上的定位图形：7×7 黑框、白环、3×3 黑心
        foreach (var (originX, originY) in new[] { (quiet, quiet), (qr.Size - quiet - 7, quiet), (quiet, qr.Size - quiet - 7) })
        {
            for (var y = 0; y < 7; y++)
            {
                for (var x = 0; x < 7; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                    Assert.Equal(ring != 2, qr.IsDark(originX + x, originY + y));
                }
            }
        }

        // 单张码上限长度的链接也能生成，且不超过版本 22（105 个模块）
        var longest = VideoSourceSyncQr.Render("opaitv-sync://z1/" + new string('a', VideoSourceSyncLink.QrSingleMaxLength - 17));
        Assert.True(longest.Size - quiet * 2 <= 105);
        // 分片码不超过版本 17（85 个模块）
        foreach (var part in VideoSourceSyncLink.BuildQrPayloads(VideoSourceSyncLink.Encode(ManySources(120))))
        {
            Assert.True(VideoSourceSyncQr.Render(part).Size - quiet * 2 <= 85);
        }
    }

    [Fact]
    public void Qr_Scale_EnlargesEveryModuleByAWholeFactor()
    {
        var qr = VideoSourceSyncQr.Render("opaitv-sync://v1/abc");
        var scaled = qr.Scale(3);

        Assert.Equal(qr.Size * 3, scaled.Size);
        for (var y = 0; y < scaled.Size; y++)
        {
            for (var x = 0; x < scaled.Size; x++)
            {
                Assert.Equal(qr.IsDark(x / 3, y / 3), scaled.IsDark(x, y));
            }
        }
    }

    // ---- 导入合并 ----

    [Fact]
    public void ImportSync_OnADeviceWithoutSources_AddsEverythingAndRoundTrips()
    {
        var incoming = VideoSourceSyncLink.Decode(VideoSourceSyncLink.Encode(Sources));
        var result = OneClickVideoSources.ImportSync([], [], incoming, adult: false);

        Assert.Equal((4, 0, 0, true, false), (result.Added, result.Updated, result.Unchanged, result.FromSync, result.FromSubscription));
        AssertSameSources(Sources, result.ExtraSources);

        // 写入 extra_sources.json 再读回、再导出，得到同样的链接
        var reloaded = ExtraVideoSourcesStore.ParseFlexible(ExtraVideoSourcesStore.Serialize(result.ExtraSources));
        Assert.Equal(
            Sources.OrderBy(source => source.Key).Select(source => (source.Key, source.Name, source.Api, source.Detail, source.Adult)),
            reloaded.OrderBy(source => source.Key).Select(source => (source.Key, source.Name, source.Api, source.Detail, source.Adult)));
        Assert.Equal(VideoSourceSyncLink.Encode(Sources), VideoSourceSyncLink.Encode(result.ExtraSources));
    }

    [Fact]
    public void ImportSync_Twice_DoesNotAddAnythingAgain()
    {
        var once = OneClickVideoSources.ImportSync([], [], Sources, adult: false);
        var twice = OneClickVideoSources.ImportSync(once.ExtraSources, [], Sources, adult: false);

        Assert.Equal((0, 0, 4), (twice.Added, twice.Updated, twice.Unchanged));
        AssertSameSources(once.ExtraSources, twice.ExtraSources);

        // 源已经在主订阅里：也不重复添加，额外源保持为空
        var subscribed = OneClickVideoSources.ImportSync([], Sources, Sources, adult: false);
        Assert.Equal((0, 0, 4), (subscribed.Added, subscribed.Updated, subscribed.Unchanged));
        Assert.Empty(subscribed.ExtraSources);
    }

    [Fact]
    public void ImportSync_OverridesTheSameKey_SkipsAKnownApiUnderAnotherKey_AndAddsTheRest()
    {
        IReadOnlyList<VideoSource> subscription =
        [
            new("DYTT", "旧名字", "https://old.dytt.example/api.php/provide/vod"),
            new("keep", "保留", "https://keep.example/vod"),
        ];
        IReadOnlyList<VideoSource> extras = [new("mine", "我自己的", "http://192.168.1.2:8080/vod/")];

        var result = OneClickVideoSources.ImportSync(extras, subscription, Sources, adult: false);

        Assert.Equal((2, 1, 1), (result.Added, result.Updated, result.Unchanged));
        var merged = VideoSourceMerger.Merge(subscription, result.ExtraSources);
        Assert.Equal(["DYTT", "keep", "mine", "adult_a", "custom_a.example_8443"], merged.Select(source => source.Key));
        // 同 key（不区分大小写）：按链接里的内容覆盖订阅里的那一个
        var overridden = merged[0];
        Assert.Equal(("电影天堂", Sources[0].Api, Sources[0].Detail), (overridden.Name, overridden.Api, overridden.Detail));
        // 同接口地址、不同 key：跳过，不重复添加
        Assert.Equal("我自己的", merged[2].Name);
        Assert.Equal(merged.Count, merged.Select(source => source.Api.TrimEnd('/')).Distinct().Count());
    }

    [Fact]
    public void ImportSync_NeverRemovesAnAdultMark_AndCanMarkTheWholeLinkAsAdult()
    {
        IReadOnlyList<VideoSource> subscription = [Sources[0] with { Adult = true }, new("other", "同地址", Sources[2].Api)];

        var kept = OneClickVideoSources.ImportSync([], subscription, Sources, adult: false);
        Assert.Equal((2, 1, 1), (kept.Added, kept.Updated, kept.Unchanged));
        var merged = VideoSourceMerger.Merge(subscription, kept.ExtraSources);
        // 本机已标记成人的源不会被导入取消标记
        Assert.True(merged.Single(source => source.Key == "dytt").Adult);
        // 链接里标了成人、本机同地址的源没标：用同 key 的额外源补上标记
        Assert.True(merged.Single(source => source.Key == "other").Adult);
        Assert.DoesNotContain(merged, source => source.Key == "adult_a");

        var all = OneClickVideoSources.ImportSync([], [], Sources, adult: true);
        Assert.All(all.ExtraSources, source => Assert.True(source.Adult));
    }

    [Fact]
    public void ImportedAdultSources_StayHiddenWhileTheAdultSwitchIsOff()
    {
        var imported = OneClickVideoSources.ImportSync([], [], VideoSourceSyncLink.Decode(MobileLink), adult: false).ExtraSources;

        // 成人内容开关的过滤逻辑不变：关着时导入的成人源照样不显示
        var visible = AdultContentFilter.VisibleSources(imported, showAdultContent: false);
        Assert.Equal(["dytt", "1", "custom_a.example_8443"], visible.Select(source => source.Key));
        Assert.True(AdultContentFilter.IsHidden(imported, showAdultContent: false, "adult_a"));
        Assert.Equal(4, AdultContentFilter.VisibleSources(imported, showAdultContent: true).Count);

        // 开关关着时导出的仍是全部源，成人标记原样带上
        var exported = VideoSourceSyncLink.Decode(VideoSourceSyncLink.Encode(imported));
        Assert.True(exported.Single(source => source.Key == "adult_a").Adult);
        Assert.Equal(1, exported.Count(source => source.Adult));
    }
}
