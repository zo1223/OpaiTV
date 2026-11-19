import {
  decodeSubscriptionPayload,
  normalizeVideoSourceConfig,
  searchAppleCmsSource,
  getAppleCmsDetail,
  fetchAppleCmsCategories,
  fetchAppleCmsCategoryVideos,
  expandSearchResultPlaybackSources,
  fetchVideoSourceSubscription,
} from "../videoSourceSubscription";

const encodeBase58 = (value: string) => {
  const alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
  const bytes = Array.from(Buffer.from(value, "utf8"));
  let digits = [0];

  for (const byte of bytes) {
    let carry = byte;
    for (let i = 0; i < digits.length; i++) {
      const x = digits[i] * 256 + carry;
      digits[i] = x % 58;
      carry = Math.floor(x / 58);
    }
    while (carry > 0) {
      digits.push(carry % 58);
      carry = Math.floor(carry / 58);
    }
  }

  let result = "";
  for (const byte of bytes) {
    if (byte === 0) {
      result += alphabet[0];
    } else {
      break;
    }
  }

  for (let i = digits.length - 1; i >= 0; i--) {
    result += alphabet[digits[i]];
  }

  return result;
};

describe("video source subscriptions", () => {
  beforeEach(() => {
    jest.restoreAllMocks();
  });

  it("decodes plain JSON subscription payloads", () => {
    const payload = JSON.stringify({
      api_site: {
        demo: { api: "https://example.com/api.php/provide/vod", name: "Demo" },
      },
    });

    expect(decodeSubscriptionPayload(payload)).toEqual(JSON.parse(payload));
  });

  it("decodes Base58 subscription payloads", () => {
    const payload = JSON.stringify({
      api_site: {
        demo: { api: "https://example.com/api.php/provide/vod", name: "Demo" },
      },
    });

    expect(decodeSubscriptionPayload(encodeBase58(payload))).toEqual(JSON.parse(payload));
  });

  it("normalizes api_site entries into playable sources", () => {
    const config = normalizeVideoSourceConfig({
      api_site: {
        demo: { api: "https://example.com/api.php/provide/vod/", name: "Demo" },
        invalid: { name: "Missing API" },
      },
    });

    expect(config.sources).toEqual([
      {
        key: "demo",
        name: "Demo",
        api: "https://example.com/api.php/provide/vod",
        detail: undefined,
      },
    ]);
  });

  it("maps Apple CMS search results to app search results", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        list: [
          {
            vod_id: 123,
            vod_name: "Test Show",
            vod_pic: "https://img.example/poster.jpg",
            vod_year: "2026",
            type_name: "国产剧",
            vod_remarks: "更新至 4 集",
            vod_blurb: "short desc",
            vod_play_url: "第1集$https://video.example/1.m3u8#第2集$https://video.example/2.m3u8",
          },
        ],
      }),
    } as Response);

    const results = await searchAppleCmsSource(
      { key: "demo", name: "Demo", api: "https://example.com/api.php/provide/vod" },
      "Test Show"
    );

    expect(fetch).toHaveBeenCalledWith(
      "https://example.com/api.php/provide/vod?ac=detail&wd=Test%20Show",
      { signal: undefined }
    );
    expect(results).toEqual([
      {
        id: 123,
        title: "Test Show",
        poster: "https://img.example/poster.jpg",
        episodes: ["https://video.example/1.m3u8", "https://video.example/2.m3u8"],
        source: "demo",
        source_name: "Demo",
        class: undefined,
        year: "2026",
        desc: "short desc",
        type_name: "国产剧",
      },
    ]);
  });

  it("keeps Apple CMS playback lines separate for detail source selection", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        list: [
          {
            vod_id: 123,
            vod_name: "Test Show",
            vod_play_from: "qiyi$$$qq",
            vod_play_url:
              "第1集$https://video.example/qiyi-1.m3u8#第2集$https://video.example/qiyi-2.m3u8$$$" +
              "第1集$https://video.example/qq-1.m3u8#第2集$https://video.example/qq-2.m3u8",
          },
        ],
      }),
    } as Response);

    const [result] = await searchAppleCmsSource(
      { key: "primary", name: "主线路", api: "https://example.com/vod" },
      "Test Show"
    );
    const lines = expandSearchResultPlaybackSources(result);

    expect(lines).toHaveLength(2);
    expect(lines.map((line) => line.source_name)).toEqual(["主线路 · qiyi", "主线路 · qq"]);
    expect(lines.map((line) => line.episodes)).toEqual([
      ["https://video.example/qiyi-1.m3u8", "https://video.example/qiyi-2.m3u8"],
      ["https://video.example/qq-1.m3u8", "https://video.example/qq-2.m3u8"],
    ]);
  });

  it("loads Apple CMS detail by source id", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        list: [
          {
            vod_id: 456,
            vod_name: "Detail Show",
            vod_pic: "https://img.example/detail.jpg",
            vod_year: "2025",
            vod_area: "中国大陆",
            vod_director: "Director",
            vod_actor: "Actor A,Actor B",
            vod_content: "long desc",
          },
        ],
      }),
    } as Response);

    const detail = await getAppleCmsDetail(
      { key: "demo", name: "Demo", api: "https://example.com/api.php/provide/vod" },
      "456"
    );

    expect(fetch).toHaveBeenCalledWith(
      "https://example.com/api.php/provide/vod?ac=detail&ids=456",
      { signal: undefined }
    );
    expect(detail).toEqual({
      id: "456",
      title: "Detail Show",
      poster: "https://img.example/detail.jpg",
      source: "demo",
      source_name: "Demo",
      desc: "long desc",
      type: undefined,
      year: "2025",
      area: "中国大陆",
      director: "Director",
      actor: "Actor A,Actor B",
      remarks: undefined,
    });
  });

  it("loads Apple CMS detail from the API endpoint when the subscription's detail field is a site address", async () => {
    // In api_site subscriptions `detail` is the source's website, which answers
    // `?ac=detail&ids=` with its HTML home page instead of JSON.
    const fetchMock = jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      if (String(url).startsWith("https://example.com/api.php/provide/vod?")) {
        return {
          ok: true,
          json: async () => ({ list: [{ vod_id: 456, vod_name: "Detail Show", vod_content: "long desc" }] }),
        } as Response;
      }
      return {
        ok: true,
        json: async () => {
          throw new SyntaxError("Unexpected token < in JSON at position 0");
        },
      } as unknown as Response;
    });

    const detail = await getAppleCmsDetail(
      { key: "demo", name: "Demo", api: "https://example.com/api.php/provide/vod", detail: "https://example.com" },
      "456"
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith("https://example.com/api.php/provide/vod?ac=detail&ids=456", {
      signal: undefined,
    });
    expect(detail).toMatchObject({ id: "456", title: "Detail Show", desc: "long desc" });
  });

  it("loads Apple CMS categories", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        class: [
          { type_id: 1, type_name: "Movie" },
          { type_id: 2, type_name: "TV" },
        ],
      }),
    } as Response);

    const categories = await fetchAppleCmsCategories({
      key: "demo",
      name: "Demo",
      api: "https://example.com/api.php/provide/vod",
    });

    expect(fetch).toHaveBeenCalledWith("https://example.com/api.php/provide/vod?ac=list", { signal: undefined });
    expect(categories).toEqual([
      { id: "1", name: "Movie" },
      { id: "2", name: "TV" },
    ]);
  });

  it("loads Apple CMS category videos by source and category", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        total: 45,
        page: 2,
        pagecount: 3,
        list: [
          {
            vod_id: 9,
            vod_name: "Category Show",
            vod_pic: "https://img.example/category.jpg",
            vod_year: "2026",
            vod_play_url: "HD$https://video.example/category.m3u8",
          },
        ],
      }),
    } as Response);

    const response = await fetchAppleCmsCategoryVideos(
      { key: "demo", name: "Demo", api: "https://example.com/api.php/provide/vod" },
      "2",
      2,
      20
    );

    expect(fetch).toHaveBeenCalledWith("https://example.com/api.php/provide/vod?ac=detail&t=2&pg=2", {
      signal: undefined,
    });
    expect(response).toEqual({
      results: [
        expect.objectContaining({
          id: 9,
          title: "Category Show",
          source: "demo",
          source_name: "Demo",
        }),
      ],
      total: 45,
      page: 2,
      pageCount: 3,
    });
  });

  describe("fetchVideoSourceSubscription", () => {
    afterEach(() => {
      jest.useRealTimers();
    });

    it("loads and normalizes the subscription returned by the server", async () => {
      const fetchMock = jest.spyOn(global, "fetch").mockResolvedValue({
        ok: true,
        text: async () => JSON.stringify({ api_site: { demo: { name: "Demo", api: "https://example.com/vod/" } } }),
      } as Response);

      const config = await fetchVideoSourceSubscription("https://example.com/sub.json");

      expect(config.sources).toEqual([
        { key: "demo", name: "Demo", api: "https://example.com/vod", detail: undefined },
      ]);
      expect(fetchMock).toHaveBeenCalledWith("https://example.com/sub.json", { signal: expect.any(AbortSignal) });
    });

    it("gives up with an error when the subscription server never answers", async () => {
      jest.useFakeTimers();
      // Like a real fetch: stays pending until the signal aborts it
      jest.spyOn(global, "fetch").mockImplementation(
        (_url, init) =>
          new Promise((_resolve, reject) => {
            init?.signal?.addEventListener("abort", () => reject(new Error("Aborted")));
          })
      );

      const settled = jest.fn();
      const pending = fetchVideoSourceSubscription("https://silent-host.example/sub.json").then(
        () => settled("resolved"),
        () => settled("rejected")
      );

      await jest.advanceTimersByTimeAsync(29_000);
      expect(settled).not.toHaveBeenCalled();

      await jest.advanceTimersByTimeAsync(1_000);
      await pending;
      expect(settled).toHaveBeenCalledWith("rejected");
    });
  });
});
