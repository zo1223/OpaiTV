using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class ExtraVideoSourcesTests
{
    [Fact]
    public void ParseFlexible_ApiSiteObject_Works()
    {
        var payload = """
        {
          "api_site": {
            "demo": {
              "name": "Demo",
              "api": "https://source.example/vod/"
            }
          }
        }
        """;

        var sources = ExtraVideoSourcesStore.ParseFlexible(payload);
        var source = Assert.Single(sources);
        Assert.Equal("demo", source.Key);
        Assert.Equal("Demo", source.Name);
        Assert.Equal("https://source.example/vod", source.Api);
    }

    [Fact]
    public void ParseFlexible_SourcesArray_Works()
    {
        var payload = """
        {
          "sources": [
            { "key": "a", "name": "A站", "api": "https://a.example/api.php/provide/vod" }
          ]
        }
        """;

        var sources = ExtraVideoSourcesStore.ParseFlexible(payload);
        var source = Assert.Single(sources);
        Assert.Equal("a", source.Key);
        Assert.Equal("A站", source.Name);
    }

    [Fact]
    public void ParseFlexible_SingleObject_Works()
    {
        var payload = """
        { "key": "b", "name": "B", "api": "https://b.example/vod", "detail": "https://b.example/detail" }
        """;

        var sources = ExtraVideoSourcesStore.ParseFlexible(payload);
        var source = Assert.Single(sources);
        Assert.Equal("b", source.Key);
        Assert.Equal("https://b.example/detail", source.Detail);
    }

    [Fact]
    public void Merge_ExtraOverridesSameKey()
    {
        var subscription = new[]
        {
            new VideoSource("demo", "Old", "https://old.example/vod"),
            new VideoSource("keep", "Keep", "https://keep.example/vod"),
        };
        var extra = new[]
        {
            new VideoSource("demo", "New", "https://new.example/vod"),
            new VideoSource("ai", "AI", "https://ai.example/vod"),
        };

        var merged = VideoSourceMerger.Merge(subscription, extra);
        Assert.Equal(3, merged.Count);
        var demo = Assert.Single(merged, s => s.Key == "demo");
        Assert.Equal("New", demo.Name);
        Assert.Equal("https://new.example/vod", demo.Api);
        Assert.Contains(merged, s => s.Key == "keep");
        Assert.Contains(merged, s => s.Key == "ai");
    }

    [Fact]
    public void AiImportPromptBuilder_IncludesUrlAndPath()
    {
        var prompt = AiImportPromptBuilder.Build(
            "https://movie.example/",
            @"C:\Users\demo\AppData\Roaming\OrionTV.Desktop\extra_sources.json",
            "只要点播",
            docsDirectory: @"C:\OrionTV\docs");

        Assert.Contains("https://movie.example/", prompt);
        Assert.Contains("extra_sources.json", prompt);
        Assert.Contains("只要点播", prompt);
        Assert.Contains("api_site", prompt);
        Assert.Contains("maccms_json", prompt);
        Assert.Contains("maccms_xml", prompt);
        Assert.Contains("extra_sources", prompt);
        Assert.Contains("ORION_SOURCE_GUIDE", prompt);
        Assert.DoesNotContain("7655", prompt);
        Assert.Contains("移除", prompt);
        Assert.Contains("TVBox", prompt); // 禁止清单中仍会点名 TVBox
        Assert.Contains("第一项操作必须使用文件读取工具完整读取", prompt);
        Assert.Contains(@"C:\OrionTV\docs\ORION_SOURCE_GUIDE.md", prompt);
        Assert.Contains("立即访问并分析上述目标地址", prompt);
        Assert.Contains("不得等待用户再次输入网址", prompt);
        Assert.Contains("真实视频 ID", prompt);
        Assert.Contains("?ac=detail&ids=...", prompt);
        Assert.Contains("至少一个可解析的 HTTP/HTTPS 播放地址", prompt);
        Assert.Contains("detailAc", prompt);
        Assert.Contains("categoryAc", prompt);
    }

    [Fact]
    public void AiImportPromptBuilder_DiscoveryStartsWithoutUrlAndRequiresValidation()
    {
        var prompt = AiImportPromptBuilder.BuildDiscovery(
            @"C:\Users\demo\AppData\Roaming\OrionTV.Desktop\extra_sources.json",
            "优先中文内容",
            docsDirectory: @"C:\OrionTV\docs");

        Assert.Contains("AI 主动寻源", prompt);
        Assert.Contains("本任务无需用户提供网址", prompt);
        Assert.Contains("立即主动搜索", prompt);
        Assert.Contains("不得要求用户再提供网址", prompt);
        Assert.Contains("只写入本次会话中实际验证通过", prompt);
        Assert.Contains("vod_play_url", prompt);
        Assert.Contains("立即主动搜索候选", prompt);
        Assert.Contains("当前 CLI 配置允许的联网和文件工具", prompt);
        Assert.DoesNotContain("授权状态未核验", prompt);
        Assert.DoesNotContain("公开可访问不等于合法", prompt);
        Assert.DoesNotContain("不扫描大量地址", prompt);
        Assert.DoesNotContain("不绕过验证码", prompt);
        Assert.DoesNotContain("不下载或运行未知", prompt);
        Assert.Contains("优先中文内容", prompt);
        Assert.Contains(@"C:\OrionTV\docs\ORION_SOURCE_GUIDE.md", prompt);
        Assert.DoesNotContain("目标地址（必须原样使用）", prompt);
    }

    [Fact]
    public void SlugKey_LatinAndNonLatin()
    {
        Assert.Equal("demo_source", ExtraVideoSourcesStore.SlugKey("Demo Source"));
        var zh = ExtraVideoSourcesStore.SlugKey("测试源");
        Assert.StartsWith("ai_", zh);
        Assert.Equal(zh, ExtraVideoSourcesStore.SlugKey("测试源"));
    }
}
