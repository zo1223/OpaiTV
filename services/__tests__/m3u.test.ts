import { parseM3U } from "@/services/m3u";

describe("parseM3U", () => {
  it("keeps commas that are part of the channel name", () => {
    const channels = parseM3U(
      ['#EXTM3U', '#EXTINF:-1 tvg-logo="http://l/a.png" group-title="News",CCTV-13, News HD', "http://s/a.m3u8"].join(
        "\r\n"
      )
    );

    expect(channels).toEqual([
      { id: "0-http://s/a.m3u8", name: "CCTV-13, News HD", url: "http://s/a.m3u8", logo: "http://l/a.png", group: "News" },
    ]);
  });

  it("ignores commas inside quoted attribute values", () => {
    const [channel] = parseM3U(['#EXTINF:-1 group-title="Movies, Drama",HBO', "http://s/b.m3u8"].join("\n"));

    expect(channel.name).toBe("HBO");
    expect(channel.group).toBe("Movies, Drama");
  });

  it("parses plain entries without attributes", () => {
    const [channel] = parseM3U(["#EXTINF:-1,Plain", "http://s/c.m3u8"].join("\n"));

    expect(channel).toMatchObject({ name: "Plain", logo: "", group: "Default" });
  });
});
