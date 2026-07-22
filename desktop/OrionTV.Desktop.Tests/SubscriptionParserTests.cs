using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class SubscriptionParserTests
{
    [Fact]
    public void Parse_JsonApiSiteObject_NormalizesSources()
    {
        var payload = """
        {
          "api_site": {
            "demo": {
              "name": "Demo Source",
              "api": "https://source.example/vod/",
              "detail": "https://detail.example/vod/"
            }
          }
        }
        """;

        var config = VideoSourceSubscriptionParser.Parse(payload);

        var source = Assert.Single(config.Sources);
        Assert.Equal("demo", source.Key);
        Assert.Equal("Demo Source", source.Name);
        Assert.Equal("https://source.example/vod", source.Api);
        Assert.Equal("https://detail.example/vod", source.Detail);
        Assert.Equal(VideoSourceProtocols.AppleCms, source.Protocol);
    }

    [Fact]
    public void Parse_Base58Payload_DecodesSubscription()
    {
        const string payload =
            "92HAZoySqaVVbtvYURVqJHprzqCoiwyxeuAsiXs27zbSnZPqTYa9WtFmUHBRgTop56YPFhJMu5pUy5Qvj2yvWuBkEkMuertyaW1a";

        var config = VideoSourceSubscriptionParser.Parse(payload);

        var source = Assert.Single(config.Sources);
        Assert.Equal("demo", source.Key);
        Assert.Equal("Demo", source.Name);
        Assert.Equal("https://source.example/vod", source.Api);
    }
}
