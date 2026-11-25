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

// 按分类过滤:源本身没有成人标记,但分类表里带成人分类(伦理片 / 福利 …)
describe("adult category filter", () => {
  const site = { key: "site", name: "Site", api: "https://site.example/vod" };
  const vod = (id: number, name: string, typeName: string, vodClass?: string) => ({
    vod_id: id,
    vod_name: name,
    vod_pic: "",
    type_name: typeName,
    ...(vodClass ? { vod_class: vodClass } : {}),
    vod_play_url: "HD$https://video.example/movie.m3u8",
  });
  const catalog = [
    vod(1, "Movie", "电影"),
    // 片名带“伦理”,分类是正常分类:不能被拦
    vod(2, "伦理学讲座", "电影", "剧情"),
    vod(3, "Late Night", "伦理片"),
    // 分类正常,题材标签是成人标签
    vod(4, "Tagged", "电影", "剧情,情色"),
    vod(5, "Bonus", "福利"),
  ];
  const normalTitles = ["Movie", "伦理学讲座"];
  const allTitles = catalog.map((item) => item.vod_name);
  let api: API;

  beforeEach(() => {
    jest.restoreAllMocks();
    api = new API();
    api.setSubscribedVideoSources([site]);
    jest.spyOn(global, "fetch").mockImplementation(async (input) => {
      const url = String(input);
      if (url.includes("douban.com")) {
        return { ok: false, json: async () => ({}) } as Response;
      }
      const ids = url.match(/[?&]ids=([^&]+)/)?.[1];
      const list = ids ? catalog.filter((item) => String(item.vod_id) === ids) : catalog;
      return {
        ok: true,
        json: async () => ({
          class: [
            { type_id: 1, type_name: "电影" },
            { type_id: 2, type_name: "爱情片" },
            { type_id: 34, type_name: "伦理片" },
            { type_id: 35, type_name: "港台三级" },
            { type_id: 36, type_name: "里番动漫" },
            { type_id: 40, type_name: "福利" },
            { type_id: 41, type_name: "写真热舞" },
            { type_id: 42, type_name: "擦边短剧" },
            { type_id: 43, type_name: "两性课堂" },
          ],
          list,
          total: list.length,
          page: 1,
          pagecount: 1,
        }),
      } as Response;
    });
  });

  const titles = (items: { title: string }[]) => items.map((item) => item.title);

  describe("when the toggle is off (default)", () => {
    it("drops adult-category items from search results, and keeps a normal title that merely contains 伦理", async () => {
      expect(titles((await api.searchVideos("anything")).results)).toEqual(normalTitles);
    });

    it("drops them from a single-source search and from a lookup by id", async () => {
      expect((await api.searchVideo("Late Night", "site")).results).toEqual([]);
      expect((await api.searchVideo("Late Night", "site", undefined, "3")).results).toEqual([]);
      expect(titles((await api.searchVideo("伦理学讲座", "site")).results)).toEqual(["伦理学讲座"]);
      expect(titles((await api.searchVideo("Movie", "site", undefined, "1")).results)).toEqual(["Movie"]);
    });

    it("leaves adult categories out of the category list", async () => {
      await expect(api.getSourceCategories("site")).resolves.toEqual([
        { id: "1", name: "电影" },
        { id: "2", name: "爱情片" },
      ]);
    });

    it("drops them from category browsing", async () => {
      expect(titles((await api.getSourceVideos("site", "34")).results)).toEqual(normalTitles);
    });

    it("refuses to open the detail of an adult-category item, and opens normal ones", async () => {
      await expect(api.getVideoDetail("site", "3")).rejects.toThrow("VIDEO_SOURCE_NOT_FOUND");
      await expect(api.getVideoDetail("site", "4")).rejects.toThrow("VIDEO_SOURCE_NOT_FOUND");
      await expect(api.getVideoDetail("site", "2")).resolves.toMatchObject({ title: "伦理学讲座", type_name: "电影" });
    });

    it("leaves them out of the home page fallback", async () => {
      const { list } = await api.getDoubanData("movie", "热门");

      expect(list.length).toBeGreaterThan(0);
      expect(titles(list).filter((title) => !normalTitles.includes(title))).toEqual([]);
    });

    it("reports hidden categories and items for data stored outside the api", () => {
      expect(api.isAdultCategoryHidden("伦理片")).toBe(true);
      expect(api.isAdultCategoryHidden("电影", "剧情,情色")).toBe(true);
      expect(api.isAdultCategoryHidden("爱情片", "爱情")).toBe(false);
      expect(api.isAdultCategoryHidden(undefined, null)).toBe(false);
      expect(api.isAdultItemHidden({ source: "site", type_name: "福利" })).toBe(true);
      expect(api.isAdultItemHidden({ source: "site", type_name: "剧情片" })).toBe(false);
      // 没有分类信息(旧数据)时无法判断,不按片名猜
      expect(api.isAdultItemHidden({ source: "site" })).toBe(false);
      expect(api.isAdultItemHidden(null)).toBe(false);
    });

    it("also filters answers from the backend", async () => {
      api.setSubscribedVideoSources([]);
      api.setBaseUrl("https://backend.example");
      (fetch as jest.Mock).mockImplementation(async (input) => {
        const url = String(input);
        const results = [
          { id: 1, title: "Movie", source: "site", type_name: "剧情片" },
          { id: 3, title: "Late Night", source: "site", type_name: "伦理片" },
        ];
        const body = url.includes("/categories")
          ? { categories: [{ id: "1", name: "剧情片" }, { id: "34", name: "伦理片" }] }
          : url.includes("/api/detail")
            ? { id: "3", title: "Late Night", source: "site", type_name: "伦理片" }
            : { results, list: results, total: 2, page: 1, pageCount: 1 };
        return { ok: true, status: 200, json: async () => body } as Response;
      });

      expect(titles((await api.searchVideos("x")).results)).toEqual(["Movie"]);
      expect(titles((await api.getSourceVideos("site", "34")).results)).toEqual(["Movie"]);
      await expect(api.getSourceCategories("site")).resolves.toEqual([{ id: "1", name: "剧情片" }]);
      await expect(api.getVideoDetail("site", "3")).rejects.toThrow("VIDEO_SOURCE_NOT_FOUND");
    });
  });

  describe("when the toggle is on", () => {
    beforeEach(() => {
      api.setShowAdultContent(true);
    });

    it("shows adult categories and their items normally", async () => {
      expect(titles((await api.searchVideos("anything")).results)).toEqual(allTitles);
      expect(titles((await api.searchVideo("Late Night", "site")).results)).toEqual(["Late Night"]);
      expect(titles((await api.searchVideo("Late Night", "site", undefined, "3")).results)).toEqual(["Late Night"]);
      expect((await api.getSourceCategories("site")).map((category) => category.name)).toEqual([
        "电影",
        "爱情片",
        "伦理片",
        "港台三级",
        "里番动漫",
        "福利",
        "写真热舞",
        "擦边短剧",
        "两性课堂",
      ]);
      expect(titles((await api.getSourceVideos("site", "34")).results)).toEqual(allTitles);
      await expect(api.getVideoDetail("site", "3")).resolves.toMatchObject({ title: "Late Night", type_name: "伦理片" });
      expect(titles((await api.getDoubanData("movie", "热门")).list)).toContain("Tagged");
      expect(api.isAdultCategoryHidden("伦理片")).toBe(false);
      expect(api.isAdultItemHidden({ source: "site", type_name: "福利" })).toBe(false);
    });

    it("drops adult-category answers that arrive after the toggle was turned off", async () => {
      const pending = [
        api.searchVideos("anything"),
        api.searchVideo("Late Night", "site", undefined, "3"),
        api.getSourceCategories("site"),
        api.getSourceVideos("site", "34"),
        api.getVideoDetail("site", "3").catch((error: Error) => error.message),
        api.getDoubanData("movie", "热门"),
      ] as const;
      api.setShowAdultContent(false);

      const [search, byId, categories, videos, detail, home] = await Promise.all(pending);

      expect(titles(search.results)).toEqual(normalTitles);
      expect(byId).toEqual({ results: [] });
      expect(categories.map((category) => category.name)).toEqual(["电影", "爱情片"]);
      expect(titles(videos.results)).toEqual(normalTitles);
      expect(detail).toBe("VIDEO_SOURCE_NOT_FOUND");
      expect(titles(home.list).filter((title) => !normalTitles.includes(title))).toEqual([]);
    });

    it("takes effect immediately in both directions, without any adult-marked source", async () => {
      api.setShowAdultContent(false);
      expect(titles((await api.searchVideos("anything")).results)).toEqual(normalTitles);

      api.setShowAdultContent(true);
      expect(titles((await api.searchVideos("anything")).results)).toEqual(allTitles);
    });
  });
});
