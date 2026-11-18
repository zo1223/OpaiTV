using System.Net;
using System.Text;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class VodProtocolTests
{
    [Theory]
    [InlineData(null, VideoSourceProtocols.AppleCms)]
    [InlineData("", VideoSourceProtocols.AppleCms)]
    [InlineData("applecms", VideoSourceProtocols.AppleCms)]
    [InlineData("maccms_json", VideoSourceProtocols.MacCmsJson)]
    [InlineData("cms_json", VideoSourceProtocols.MacCmsJson)]
    [InlineData("maccms_xml", VideoSourceProtocols.MacCmsXml)]
    [InlineData("xml", VideoSourceProtocols.MacCmsXml)]
    public void NormalizeProtocol(string? input, string expected) =>
        Assert.Equal(expected, VideoSourceProtocols.Normalize(input));

    [Fact]
    public void ParseSubscription_ReadsProtocolDialectOptions()
    {
        var payload = """
        {
          "api_site": {
            "mac": {
              "name": "Mac站",
              "api": "https://mac.example/api.php/provide/vod/",
              "protocol": "maccms_json",
              "dialect": "search_ac_list",
              "options": { "pageAc": "videolist" }
            }
          }
        }
        """;

        var source = Assert.Single(VideoSourceSubscriptionParser.Parse(payload).Sources);
        Assert.Equal(VideoSourceProtocols.MacCmsJson, source.Protocol);
        Assert.Equal("search_ac_list", source.Dialect);
        Assert.Equal("videolist", source.Options!["pageAc"]);
        Assert.Equal("https://mac.example/api.php/provide/vod", source.Api);
    }

    [Fact]
    public void Factory_RoutesProtocols()
    {
        using var http = new HttpClient();
        var factory = new VodSourceClientFactory(http);
        Assert.IsType<AppleCmsClient>(factory.For(new VideoSource("a", "A", "https://a.example")));
        Assert.IsType<MacCmsJsonClient>(factory.For(new VideoSource("b", "B", "https://b.example", Protocol: VideoSourceProtocols.MacCmsJson)));
        Assert.IsType<MacCmsXmlClient>(factory.For(new VideoSource("c", "C", "https://c.example", Protocol: VideoSourceProtocols.MacCmsXml)));
        Assert.IsNotType<AppleCmsClient>(factory.For(new VideoSource("d", "D", "https://d.example", Protocol: "tvbox_csp")));
    }

    [Fact]
    public async Task MacCmsJson_SearchUsesListAcWhenDialectSaysSo()
    {
        string? seen = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            seen = request.RequestUri!.ToString();
            return """{ "list": [ { "vod_id": 1, "vod_name": "Film", "vod_play_url": "1$https://v.example/1.m3u8" } ] }""";
        }));

        var client = new MacCmsJsonClient(http);
        var source = new VideoSource("mac", "Mac", "https://mac.example/vod", Protocol: VideoSourceProtocols.MacCmsJson, Dialect: "search_ac_list");
        var results = await client.SearchAsync(source, "Film");

        Assert.Contains("ac=list", seen);
        Assert.Contains("wd=Film", seen);
        Assert.Equal("Film", Assert.Single(results).Title);
    }

    [Fact]
    public async Task MacCmsXml_ParsesClassAndVideoPlayDd()
    {
        var xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss>
          <class>
            <ty id="1">电影</ty>
            <ty id="2">连续剧</ty>
          </class>
          <list page="1" pagecount="3" pagesize="20" recordcount="50">
            <video>
              <id>9</id>
              <name>测试片</name>
              <pic>https://img.example/9.jpg</pic>
              <year>2024</year>
              <type>电影</type>
              <dl>
                <dd flag="线路A">第1集$https://video.example/a1.m3u8#第2集$/play/a2.m3u8</dd>
                <dd flag="线路B">第1集$https://video.example/b1.m3u8</dd>
              </dl>
            </video>
          </list>
        </rss>
        """;

        using var http = new HttpClient(new StubHandler(_ => xml));
        var client = new MacCmsXmlClient(http);
        var source = new VideoSource("xml", "XML站", "https://xml.example/api.php/provide/vod/", Protocol: VideoSourceProtocols.MacCmsXml);

        var categories = await client.GetCategoriesAsync(source);
        Assert.Equal(2, categories.Count);
        Assert.Equal("1", categories[0].Id);

        var results = await client.SearchAsync(source, "测试");
        var item = Assert.Single(results);
        Assert.Equal("9", item.Id);
        Assert.Equal("测试片", item.Title);
        Assert.NotNull(item.PlaybackSources);
        Assert.Equal(2, item.PlaybackSources!.Count);
        // 相对 /play/a2 相对 API 路径 https://xml.example/api.php/provide/vod/ → 落到同 host
        Assert.Contains(item.PlaybackSources[0].Episodes, u => u.EndsWith("/play/a2.m3u8", StringComparison.Ordinal));
        Assert.All(item.PlaybackSources[0].Episodes, u => Assert.StartsWith("http", u, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MacCmsXml_KeepsLineNamesAlignedWhenADdIsEmptyOrUnflagged()
    {
        var xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss>
          <list page="1" pagecount="1" pagesize="20" recordcount="1">
            <video>
              <id>9</id>
              <name>测试片</name>
              <dl>
                <dd flag="空线路"></dd>
                <dd>第1集$https://video.example/n1.m3u8</dd>
                <dd flag="线路B">第1集$https://video.example/b1.m3u8</dd>
              </dl>
            </video>
          </list>
        </rss>
        """;

        using var http = new HttpClient(new StubHandler(_ => xml));
        var client = new MacCmsXmlClient(http);
        var source = new VideoSource("xml", "XML站", "https://xml.example/api.php/provide/vod/", Protocol: VideoSourceProtocols.MacCmsXml);

        var item = Assert.Single(await client.SearchAsync(source, "测试"));
        Assert.NotNull(item.PlaybackSources);
        Assert.Equal(2, item.PlaybackSources!.Count);
        Assert.Equal("线路 1", item.PlaybackSources[0].Name);
        Assert.Equal(["https://video.example/n1.m3u8"], item.PlaybackSources[0].Episodes);
        Assert.Equal("线路B", item.PlaybackSources[1].Name);
        Assert.Equal(["https://video.example/b1.m3u8"], item.PlaybackSources[1].Episodes);
    }

    [Fact]
    public void ParseRelativeEpisode_ResolvesAgainstSourceOrigin()
    {
        var episodes = AppleCmsEpisodeParser.Parse(
            "1$/vod/play/1.m3u8#2$https://cdn.example/2.m3u8",
            "https://site.example/api.php/provide/vod");
        Assert.Equal(
            ["https://site.example/vod/play/1.m3u8", "https://cdn.example/2.m3u8"],
            episodes);
    }

    [Fact]
    public async Task UnsupportedProtocol_SearchReturnsEmpty()
    {
        using var http = new HttpClient();
        var factory = new VodSourceClientFactory(http);
        var source = new VideoSource("x", "X", "https://x.example", Protocol: "csp_foo");
        var results = await factory.For(source).SearchAsync(source, "q");
        Assert.Empty(results);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = responder(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, body.TrimStart().StartsWith('<') ? "application/xml" : "application/json"),
            });
        }
    }
}
