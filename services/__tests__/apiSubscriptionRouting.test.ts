import { API } from "../api";

jest.mock("@react-native-async-storage/async-storage", () => ({
  setItem: jest.fn(),
}));

describe("API subscription routing", () => {
  beforeEach(() => {
    jest.restoreAllMocks();
  });

  it("searches subscribed video sources when they are configured", async () => {
    const api = new API("https://backend.example");
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        list: [{ vod_id: 1, vod_name: "Movie", vod_pic: "", vod_play_url: "HD$https://video.example/movie.m3u8" }],
      }),
    } as Response);

    const response = await api.searchVideos("Movie");

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&wd=Movie", { signal: undefined });
    expect(response.results).toHaveLength(1);
    expect(response.results[0].source).toBe("demo");
  });

  it("falls back to the backend API when no subscribed source is configured", async () => {
    const api = new API("https://backend.example");

    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ results: [] }),
    } as Response);

    await api.searchVideos("Movie");

    expect(fetch).toHaveBeenCalledWith("https://backend.example/api/search?q=Movie", {});
  });

  it("adds Douban image headers when building native image sources", () => {
    const api = new API();
    const uri = "https://img3.doubanio.com/view/photo/s_ratio_poster/public/p2925804842.jpg";

    expect(api.getImageSource(uri)).toEqual({
      uri,
      headers: {
        Referer: "https://movie.douban.com/",
        "User-Agent": "Mozilla/5.0",
      },
    });
  });

  it("loads subscription-only home data from Douban mobile subject collections before subscribed sources", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("m.douban.com/rexxar/api/v2/subject_collection/movie_hot_gaia/items")) {
        return {
          ok: true,
          json: async () => ({
            subject_collection_items: [
              {
                id: "36680492",
                title: "大濛",
                cover: { url: "https://img.example/dameng.jpg" },
                rating: { value: 7.9 },
                year: "2025",
              },
            ],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 7,
              vod_name: "Source Latest",
              vod_pic: "https://image.example/latest.jpg",
              vod_year: "2026",
              vod_play_url: "HD$https://video.example/latest.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "热门", 20, 0);

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledWith(
      "https://m.douban.com/rexxar/api/v2/subject_collection/movie_hot_gaia/items?start=0&count=20&items_only=1&for_mobile=1",
      expect.any(Object)
    );
    expect(response).toEqual({
      code: 0,
      message: "",
      list: [
        {
          id: "36680492",
          title: "大濛",
          poster: "https://img.example/dameng.jpg",
          rate: "7.9",
          year: "2025",
          episodes: [],
        },
      ],
    });
  });

  it("loads subscription-only home data from Douban mobile recommendations when no collection mapping exists", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("m.douban.com/rexxar/api/v2/movie/recommend")) {
        return {
          ok: true,
          json: async () => ({
            items: [
              {
                id: "movie_action",
                title: "高分经典动作片榜",
                type: "chart",
                cover_url: "https://img.example/chart.jpg",
              },
              {
                id: "5294851",
                title: "控方证人",
                type: "movie",
                pic: { normal: "https://img.example/witness.jpg" },
                rating: { value: 9.4 },
                year: "1957",
              },
            ],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({ list: [] }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "冷门佳片", 20, 0);

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(String((fetch as jest.Mock).mock.calls[0][0])).toContain(
      "https://m.douban.com/rexxar/api/v2/movie/recommend?"
    );
    expect(fetch).not.toHaveBeenCalledWith(expect.stringContaining("movie.douban.com/j/search_subjects"), expect.anything());
    expect(response.list).toEqual([
      {
        id: "5294851",
        title: "控方证人",
        poster: "https://img.example/witness.jpg",
        rate: "9.4",
        year: "1957",
        episodes: [],
      },
    ]);
  });

  it("falls back to subscribed sources when Douban subjects are unavailable in subscription-only mode", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      if (String(url).includes("m.douban.com/rexxar/api/v2/subject_collection/tv_hot/items")) {
        throw new Error("Douban unavailable");
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 7,
              vod_name: "Latest",
              vod_pic: "https://image.example/latest.jpg",
              vod_year: "2026",
              vod_play_url: "HD$https://video.example/latest.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "hot", 20, 0);

    expect(fetch).toHaveBeenCalledWith(
      "https://m.douban.com/rexxar/api/v2/subject_collection/tv_hot/items?start=0&count=20&items_only=1&for_mobile=1",
      expect.any(Object)
    );
    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&pg=1", { signal: undefined });
    expect(response).toEqual({
      code: 0,
      message: "",
      list: [
        expect.objectContaining({
          id: 7,
          title: "Latest",
          poster: "https://image.example/latest.jpg",
          source: "demo",
          source_name: "Demo",
          year: "2026",
        }),
      ],
    });
  });

  it("loads subscription-only home data from matching Apple CMS categories", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      if (String(url).includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [
              { type_id: 1, type_name: "电影" },
              { type_id: 13, type_name: "国产剧" },
            ],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 7,
              vod_name: "Domestic Drama",
              vod_pic: "https://image.example/drama.jpg",
              vod_year: "2026",
              vod_play_url: "HD$https://video.example/drama.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "国产剧", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=list", { signal: undefined });
    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=13&pg=1", { signal: undefined });
    expect(response.list).toEqual([
      expect.objectContaining({
        id: 7,
        title: "Domestic Drama",
        source: "demo",
        source_name: "Demo",
      }),
    ]);
  });

  it("prefers specific home subcategory matches before broad type fallbacks", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      if (String(url).includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [
              { type_id: 1, type_name: "电影" },
              { type_id: 2, type_name: "连续剧" },
              { type_id: 6, type_name: "动作片" },
              { type_id: 13, type_name: "国产剧" },
            ],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({ list: [] }),
      } as Response;
    });

    await api.getDoubanData("tv", "国产剧", 20, 0);
    await api.getDoubanData("movie", "动作", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=13&pg=1", { signal: undefined });
    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=6&pg=1", { signal: undefined });
  });

  it("filters broad movie category results so TV shows do not appear under movies", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 1, type_name: "电影" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 1,
              vod_name: "Movie Item",
              vod_pic: "",
              vod_year: "2026",
              type_name: "电影",
              vod_play_url: "HD$https://video.example/movie.m3u8",
            },
            {
              vod_id: 2,
              vod_name: "Drama Item",
              vod_pic: "",
              vod_year: "2026",
              type_name: "国产剧",
              vod_play_url: "HD$https://video.example/drama.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "热门", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=1&pg=1", { signal: undefined });
    expect(response.list.map((item) => item.title)).toEqual(["Movie Item"]);
  });

  it("filters regional movie tags by Apple CMS area metadata", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 1, type_name: "电影" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 1,
              vod_name: "Chinese Movie",
              vod_pic: "",
              vod_year: "2026",
              type_name: "电影",
              vod_area: "中国大陆",
              vod_play_url: "HD$https://video.example/chinese.m3u8",
            },
            {
              vod_id: 2,
              vod_name: "US Movie",
              vod_pic: "",
              vod_year: "2026",
              type_name: "电影",
              vod_area: "美国",
              vod_play_url: "HD$https://video.example/us.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "华语", 20, 0);

    expect(response.list.map((item) => item.title)).toEqual(["Chinese Movie"]);
  });

  it("does not match TV drama categories for regional movie tags", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [
              { type_id: 2, type_name: "韩国剧" },
              { type_id: 1, type_name: "电影" },
            ],
          }),
        } as Response;
      }

      if (requestUrl.includes("t=2")) {
        return {
          ok: true,
          json: async () => ({
            list: [
              {
                vod_id: 2,
                vod_name: "Korean Drama",
                vod_pic: "",
                vod_year: "2026",
                type_name: "韩国剧",
                vod_play_url: "HD$https://video.example/k-drama.m3u8",
              },
            ],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 1,
              vod_name: "Korean Movie",
              vod_pic: "",
              vod_year: "2026",
              type_name: "电影",
              vod_area: "韩国",
              vod_play_url: "HD$https://video.example/k-movie.m3u8",
            },
            {
              vod_id: 3,
              vod_name: "US Movie",
              vod_pic: "",
              vod_year: "2026",
              type_name: "电影",
              vod_area: "美国",
              vod_play_url: "HD$https://video.example/us.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "韩国", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=1&pg=1", { signal: undefined });
    expect(fetch).not.toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=2&pg=1", { signal: undefined });
    expect(response.list.map((item) => item.title)).toEqual(["Korean Movie"]);
  });

  it("does not use TV-only source categories for regional movie tags when no movie category exists", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 2, type_name: "韩国剧" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 2,
              vod_name: "Korean Drama",
              vod_pic: "",
              vod_year: "2026",
              type_name: "韩国剧",
              vod_play_url: "HD$https://video.example/k-drama.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "韩国", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=list", { signal: undefined });
    expect(fetch).not.toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=2&pg=1", { signal: undefined });
    expect(response.list).toEqual([]);
  });

  it("does not turn unsupported editorial movie tags into the whole movie category", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 1, type_name: "电影" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 1,
              vod_name: "Regular Movie",
              vod_pic: "",
              vod_year: "2026",
              type_name: "电影",
              vod_area: "美国",
              vod_play_url: "HD$https://video.example/regular.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("movie", "top250", 20, 0);

    expect(response.list).toEqual([]);
  });

  it("keeps TV subcategory results when broad TV categories expose type metadata without area", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 2, type_name: "连续剧" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 1,
              vod_name: "Domestic Drama",
              vod_pic: "",
              vod_year: "2026",
              type_name: "国产剧",
              vod_play_url: "HD$https://video.example/domestic.m3u8",
            },
            {
              vod_id: 2,
              vod_name: "US Drama",
              vod_pic: "",
              vod_year: "2026",
              type_name: "美剧",
              vod_play_url: "HD$https://video.example/us.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "国产剧", 20, 0);

    expect(response.list.map((item) => item.title)).toEqual(["Domestic Drama"]);
  });

  it("matches the Douban TV documentary tag to subscribed documentary categories", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 4, type_name: "纪录片" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 4,
              vod_name: "Documentary",
              vod_pic: "",
              vod_year: "2026",
              type_name: "纪录片",
              vod_play_url: "HD$https://video.example/documentary.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "纪录片", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=4&pg=1", { signal: undefined });
    expect(response.list.map((item) => item.title)).toEqual(["Documentary"]);
  });

  it("appends Apple CMS parameters inside proxy url targets", async () => {
    const api = new API();
    api.setSubscribedVideoSources([
      { key: "proxy", name: "Proxy", api: "https://proxy.example/?url=https://source.example/vod" },
    ]);

    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({ list: [] }),
    } as Response);

    await api.getDoubanData("tv", "热门", 20, 0);

    expect(fetch).toHaveBeenCalledWith("https://proxy.example/?url=https://source.example/vod?ac=detail&pg=1", {
      signal: undefined,
    });
  });

  it("does not fall back to unfiltered latest data when a matched home category fails", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 13, type_name: "国产剧" }],
          }),
        } as Response;
      }

      if (requestUrl.includes("t=13")) {
        return {
          ok: false,
          status: 500,
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: 99,
              vod_name: "Unfiltered Latest",
              vod_pic: "",
              vod_year: "2026",
              vod_play_url: "HD$https://video.example/latest.m3u8",
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "国产剧", 20, 0);

    expect(response.list).toEqual([]);
    expect(fetch).not.toHaveBeenCalledWith("https://source.example/vod?ac=detail&pg=1", { signal: undefined });
  });

  it("mixes subscription-only home data across sources instead of letting one source fill the page", async () => {
    const api = new API();
    api.setSubscribedVideoSources([
      { key: "one", name: "One", api: "https://one.example/vod" },
      { key: "two", name: "Two", api: "https://two.example/vod" },
    ]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      const sourceKey = requestUrl.includes("one.example") ? "one" : "two";
      const sourceName = sourceKey === "one" ? "One" : "Two";
      const offset = sourceKey === "one" ? 0 : 100;

      return {
        ok: true,
        json: async () => ({
          list: Array.from({ length: 20 }, (_, index) => ({
            vod_id: offset + index + 1,
            vod_name: `${sourceName} Item ${index + 1}`,
            vod_pic: "",
            vod_year: "2026",
            vod_play_url: `HD$https://video.example/${sourceKey}-${index + 1}.m3u8`,
          })),
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "热门", 20, 0);

    expect(response.list).toHaveLength(20);
    expect(response.list.some((item) => item.source === "one")).toBe(true);
    expect(response.list.some((item) => item.source === "two")).toBe(true);
  });

  it("deduplicates subscription-only category data across sources before returning posters", async () => {
    const api = new API();
    api.setSubscribedVideoSources([
      { key: "one", name: "One", api: "https://one.example/vod" },
      { key: "two", name: "Two", api: "https://two.example/vod" },
    ]);

    jest.spyOn(global, "fetch").mockImplementation(async (url) => {
      const requestUrl = String(url);
      const sourceKey = requestUrl.includes("one.example") ? "one" : "two";

      if (requestUrl.includes("ac=list")) {
        return {
          ok: true,
          json: async () => ({
            class: [{ type_id: 3, type_name: "Drama" }],
          }),
        } as Response;
      }

      return {
        ok: true,
        json: async () => ({
          list: [
            {
              vod_id: sourceKey === "one" ? 1 : 101,
              vod_name: "Same Show",
              vod_pic: `https://image.example/${sourceKey}/same.jpg`,
              vod_year: "2026",
              vod_play_url: `HD$https://video.example/${sourceKey}-same.m3u8`,
            },
            {
              vod_id: sourceKey === "one" ? 2 : 102,
              vod_name: `${sourceKey} Unique`,
              vod_pic: `https://image.example/${sourceKey}/unique.jpg`,
              vod_year: "2026",
              vod_play_url: `HD$https://video.example/${sourceKey}-unique.m3u8`,
            },
          ],
        }),
      } as Response;
    });

    const response = await api.getDoubanData("tv", "Drama", 20, 0);

    expect(response.list.map((item) => item.title)).toEqual(["Same Show", "one Unique", "two Unique"]);
    expect(response.list.filter((item) => item.title === "Same Show")).toHaveLength(1);
  });

  it("loads subscribed source categories", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        class: [{ type_id: 3, type_name: "Drama" }],
      }),
    } as Response);

    await expect(api.getSourceCategories("demo")).resolves.toEqual([{ id: "3", name: "Drama" }]);
    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=list", { signal: undefined });
  });

  it("loads subscribed source category videos", async () => {
    const api = new API();
    api.setSubscribedVideoSources([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);

    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        total: 1,
        page: 1,
        pagecount: 1,
        list: [
          {
            vod_id: 10,
            vod_name: "Drama Show",
            vod_pic: "",
            vod_year: "2026",
            vod_play_url: "HD$https://video.example/drama.m3u8",
          },
        ],
      }),
    } as Response);

    const response = await api.getSourceVideos("demo", "3", 1);

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&t=3&pg=1", { signal: undefined });
    expect(response).toEqual({
      results: [
        expect.objectContaining({
          id: 10,
          title: "Drama Show",
          source: "demo",
          source_name: "Demo",
        }),
      ],
      total: 1,
      page: 1,
      pageCount: 1,
    });
  });

  it("loads a subscribed source result by id when one is available", async () => {
    const api = new API();
    api.setSubscribedVideoSources([
      { key: "demo", name: "Demo", api: "https://source.example/vod", detail: "https://detail.example" },
    ]);

    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      json: async () => ({
        list: [
          {
            vod_id: 7,
            vod_name: "Movie",
            vod_pic: "https://image.example/movie.jpg",
            vod_year: "2026",
            vod_play_url: "HD$https://video.example/movie.m3u8",
          },
        ],
      }),
    } as Response);

    const response = await api.searchVideo("Movie", "demo", undefined, "7");

    expect(fetch).toHaveBeenCalledWith("https://source.example/vod?ac=detail&ids=7", { signal: undefined });
    expect(response.results).toEqual([
      expect.objectContaining({
        id: 7,
        title: "Movie",
        episodes: ["https://video.example/movie.m3u8"],
        source: "demo",
        source_name: "Demo",
      }),
    ]);
  });
});
