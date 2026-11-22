import { API } from "../api";
import {
  addManualVideoSource,
  isManualVideoSource,
  normalizeVideoSourceConfig,
  videoSourceConfigToApiSites,
} from "../videoSourceSubscription";

jest.mock("@react-native-async-storage/async-storage", () => ({
  setItem: jest.fn(),
}));

const normal = { key: "normal", name: "Normal", api: "https://normal.example/vod" };
const adult = { key: "adult", name: "Adult", api: "https://adult.example/vod", adult: true };

// 每个源返回一条标题相同的影片和一个分类,用请求的主机名区分是哪个源被访问了
const mockSourceFetch = () =>
  jest.spyOn(global, "fetch").mockImplementation(async (input) => {
    const url = String(input);
    if (url.includes("douban.com")) {
      return { ok: false, json: async () => ({}) } as Response;
    }
    const isAdult = url.startsWith("https://adult.example/");
    return {
      ok: true,
      json: async () => ({
        class: [{ type_id: 1, type_name: "电影" }],
        list: [
          {
            vod_id: isAdult ? 2 : 1,
            vod_name: isAdult ? "Adult Movie" : "Movie",
            vod_pic: "",
            type_name: "电影",
            vod_play_url: "HD$https://video.example/movie.m3u8",
          },
        ],
        total: 1,
        page: 1,
        pagecount: 1,
      }),
    } as Response;
  });

const requestedHosts = () =>
  Array.from(new Set((fetch as jest.Mock).mock.calls.map(([input]) => String(input).match(/^https:\/\/([^/]+)/)![1])));

describe("adult content filter", () => {
  let api: API;

  beforeEach(() => {
    jest.restoreAllMocks();
    api = new API();
    api.setSubscribedVideoSources([normal, adult]);
    mockSourceFetch();
  });

  describe("when the toggle is off (default)", () => {
    it("hides adult sources from the source list", async () => {
      await expect(api.getResources()).resolves.toEqual([{ key: "normal", name: "Normal", api: normal.api, detail: undefined }]);
    });

    it("does not query adult sources when searching", async () => {
      const { results } = await api.searchVideos("Movie");

      expect(results.map((item) => item.source)).toEqual(["normal"]);
      expect(requestedHosts()).toEqual(["normal.example"]);
    });

    it("returns nothing for a single-source search or lookup on an adult source", async () => {
      await expect(api.searchVideo("Adult Movie", "adult")).resolves.toEqual({ results: [] });
      await expect(api.searchVideo("Adult Movie", "adult", undefined, "2")).resolves.toEqual({ results: [] });
      expect(fetch).not.toHaveBeenCalled();
    });

    it("refuses to browse or open an adult source", async () => {
      await expect(api.getSourceCategories("adult")).rejects.toThrow("VIDEO_SOURCE_NOT_FOUND");
      await expect(api.getSourceVideos("adult", "1")).rejects.toThrow("VIDEO_SOURCE_NOT_FOUND");
      await expect(api.getVideoDetail("adult", "2")).rejects.toThrow("VIDEO_SOURCE_NOT_FOUND");
      expect(fetch).not.toHaveBeenCalled();
    });

    it("leaves adult sources out of the home page and recommendations", async () => {
      const { list } = await api.getDoubanData("movie", "热门");

      expect(list.map((item) => item.source)).toEqual(["normal"]);
      expect(requestedHosts()).toEqual(["m.douban.com", "normal.example"]);
    });

    it("reports which sources are hidden, for data stored by source key", () => {
      expect(api.isAdultSourceHidden("adult")).toBe(true);
      expect(api.isAdultSourceHidden("normal")).toBe(false);
      expect(api.isAdultSourceHidden("unknown")).toBe(false);
    });

    it("stays in subscription mode when every source is hidden, instead of falling back to the backend", async () => {
      api.setBaseUrl("https://backend.example");
      api.setSubscribedVideoSources([adult]);

      await expect(api.getResources()).resolves.toEqual([]);
      await expect(api.searchVideos("Movie")).resolves.toEqual({ results: [] });
      expect(fetch).not.toHaveBeenCalled();
    });

    it("applies to sources set after the toggle as well", async () => {
      api.setSubscribedVideoSources([normal, { ...adult, key: "late" }]);

      expect((await api.getResources()).map((site) => site.key)).toEqual(["normal"]);
    });
  });

  describe("when the toggle is on", () => {
    beforeEach(() => {
      api.setShowAdultContent(true);
    });

    it("shows adult sources and their content normally", async () => {
      expect((await api.getResources()).map((site) => [site.key, site.adult])).toEqual([
        ["normal", undefined],
        ["adult", true],
      ]);
      expect((await api.searchVideos("Movie")).results.map((item) => item.source)).toEqual(["normal", "adult"]);
      expect((await api.searchVideo("Adult Movie", "adult")).results.map((item) => item.title)).toEqual(["Adult Movie"]);
      await expect(api.getSourceCategories("adult")).resolves.toEqual([{ id: "1", name: "电影" }]);
      expect((await api.getSourceVideos("adult", "1")).results.map((item) => item.title)).toEqual(["Adult Movie"]);
      await expect(api.getVideoDetail("adult", "2")).resolves.toMatchObject({ source: "adult", title: "Adult Movie" });
      expect((await api.getDoubanData("movie", "热门")).list.map((item) => item.source)).toEqual(["normal", "adult"]);
      expect(api.isAdultSourceHidden("adult")).toBe(false);
    });

    it("drops answers from adult sources that arrive after the toggle was turned off", async () => {
      const pending = [
        api.searchVideos("Movie"),
        api.getDoubanData("movie", "热门"),
        api.searchVideo("Adult Movie", "adult"),
        api.searchVideo("Adult Movie", "adult", undefined, "2"),
        api.getSourceCategories("adult").catch((error: Error) => error.message),
        api.getSourceVideos("adult", "1").catch((error: Error) => error.message),
        api.getVideoDetail("adult", "2").catch((error: Error) => error.message),
      ] as const;
      // 请求已经发出(开关还开着),应答回来之前开关被关掉
      api.setShowAdultContent(false);

      const [search, home, one, byId, categories, videos, detail] = await Promise.all(pending);

      expect(requestedHosts()).toContain("adult.example");
      expect(search.results.map((item) => item.source)).toEqual(["normal"]);
      expect(home.list.map((item) => item.source)).toEqual(["normal"]);
      expect(one).toEqual({ results: [] });
      expect(byId).toEqual({ results: [] });
      expect([categories, videos, detail]).toEqual(Array(3).fill("VIDEO_SOURCE_NOT_FOUND"));
    });

    it("hides them again as soon as the toggle is turned off", async () => {
      api.setShowAdultContent(false);

      expect((await api.searchVideos("Movie")).results.map((item) => item.source)).toEqual(["normal"]);
      expect(api.isAdultSourceHidden("adult")).toBe(true);
    });
  });
});

describe("adult flag in the source config", () => {
  it("reads the flag from subscription entries and leaves unflagged sources unchanged", () => {
    const { sources } = normalizeVideoSourceConfig({
      api_site: {
        a: { api: "https://a.example/vod", name: "A", adult: true },
        b: { api: "https://b.example/vod", name: "B", is_adult: true },
        c: { api: "https://c.example/vod", name: "C", adult: "1" },
        d: { api: "https://d.example/vod", name: "D", adult: false },
        e: { api: "https://e.example/vod", name: "E", adult: "false" },
        f: { api: "https://f.example/vod", name: "F", adult: 0 },
        g: { api: "https://g.example/vod", name: "G" },
      },
    });

    expect(sources.map((source) => [source.key, source.adult])).toEqual([
      ["a", true],
      ["b", true],
      ["c", true],
      ["d", undefined],
      ["e", undefined],
      ["f", undefined],
      ["g", undefined],
    ]);
    expect(sources[6]).toEqual({ key: "g", name: "G", api: "https://g.example/vod", detail: undefined });
  });

  it("carries the flag into the source list entries", () => {
    expect(videoSourceConfigToApiSites({ sources: [normal, adult] })).toEqual([
      { key: "normal", name: "Normal", api: normal.api, detail: undefined },
      { key: "adult", name: "Adult", api: adult.api, detail: undefined, adult: true },
    ]);
  });
});

describe("addManualVideoSource", () => {
  it("turns a pasted URL into a source in the existing format", () => {
    const sources = addManualVideoSource([normal], "  https://My.Example:8443/api.php/provide/vod/at/xml/  ", false);

    expect(sources).toEqual([
      normal,
      {
        key: "custom_my.example_8443",
        name: "my.example:8443",
        api: "https://My.Example:8443/api.php/provide/vod/at/xml",
        detail: undefined,
      },
    ]);
    expect(sources.map(isManualVideoSource)).toEqual([false, true]);
  });

  it("marks the source as adult when asked", () => {
    expect(addManualVideoSource([], "https://my.example/vod", true)[0]).toMatchObject({ key: "custom_my.example", adult: true });
  });

  it("adds a scheme the same way the server address setting does", () => {
    expect(addManualVideoSource([], "my.example/api.php/provide/vod", false)[0].api).toBe("https://my.example/api.php/provide/vod");
    expect(addManualVideoSource([], "192.168.1.5:8080/vod", false)[0].api).toBe("http://192.168.1.5:8080/vod");
  });

  it("gives two interfaces on the same host different keys", () => {
    const first = addManualVideoSource([], "https://my.example/json", false);
    const second = addManualVideoSource(first, "https://my.example/xml", false);

    expect(second.map((source) => source.key)).toEqual(["custom_my.example", "custom_my.example_2"]);
  });

  it("does not add the same interface twice; it only updates the adult flag", () => {
    const first = addManualVideoSource([normal], "https://my.example/vod", false);
    const flagged = addManualVideoSource(first, "https://my.example/vod/", true);
    const unflagged = addManualVideoSource(flagged, "https://my.example/vod", false);

    expect(flagged).toHaveLength(2);
    expect(flagged[1]).toEqual({ ...first[1], adult: true });
    expect(unflagged).toEqual(first);
    expect(unflagged[1]).not.toHaveProperty("adult");
  });

  it("accepts LAN hosts given with a port", () => {
    expect(addManualVideoSource([], "http://nas:8080/vod", false)[0]).toMatchObject({ key: "custom_nas_8080", api: "http://nas:8080/vod" });
    expect(addManualVideoSource([], "http://localhost/vod", false)[0].api).toBe("http://localhost/vod");
  });

  it.each([
    "",
    "   ",
    "not a url",
    "movie",
    "ftp://my.example/vod",
    "https://",
    "https://my example/vod",
    "https://my.example/vod and more",
    "javascript:alert(1)",
    "https://user@my.example/vod",
  ])(
    "rejects %p",
    (url) => {
      expect(() => addManualVideoSource([normal], url, false)).toThrow("INVALID_VIDEO_SOURCE_URL");
    }
  );
});
