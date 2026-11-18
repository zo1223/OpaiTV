import { fetchAndParseM3u, parseM3U } from "@/services/m3u";

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

describe("fetchAndParseM3u", () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.useRealTimers();
  });

  it("parses the playlist returned by the server", async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      text: async () => ["#EXTINF:-1,Plain", "http://s/c.m3u8"].join("\n"),
    }) as unknown as typeof fetch;

    const channels = await fetchAndParseM3u("http://host/list.m3u");

    expect(channels.map((channel) => channel.name)).toEqual(["Plain"]);
    expect(global.fetch).toHaveBeenCalledWith("http://host/list.m3u", { signal: expect.any(AbortSignal) });
  });

  it("gives up with an empty list when the server never answers", async () => {
    jest.useFakeTimers();
    // Like a real fetch: stays pending until the signal aborts it
    global.fetch = jest.fn(
      (_url: string, init?: RequestInit) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener("abort", () => reject(new Error("Aborted")));
        })
    ) as unknown as typeof fetch;

    const settled = jest.fn();
    const pending = fetchAndParseM3u("http://silent-host/list.m3u").then((channels) => {
      settled(channels);
      return channels;
    });

    await jest.advanceTimersByTimeAsync(29_000);
    expect(settled).not.toHaveBeenCalled();

    await jest.advanceTimersByTimeAsync(1_000);
    await expect(pending).resolves.toEqual([]);
  });
});
