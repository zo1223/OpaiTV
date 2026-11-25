import { API } from "../api";

jest.mock("@react-native-async-storage/async-storage", () => ({
  setItem: jest.fn(),
}));

const alpha = { key: "alpha", name: "Alpha", api: "https://alpha.example/vod" };
const beta = { key: "beta", name: "Beta", api: "https://beta.example/vod" };
const adult = { key: "adult", name: "Adult", api: "https://adult.example/vod", adult: true };

// 每个源返回一部电影和一部短剧;电影有两条线路(mp4 在前,m3u8 在后)
const mockSourceFetch = (failingHosts: string[] = []) =>
  jest.spyOn(global, "fetch").mockImplementation(async (input) => {
    const url = String(input);
    if (url.includes("douban.com")) {
      return { ok: false, json: async () => ({}) } as Response;
    }
    const host = url.match(/^https:\/\/([^/]+)/)![1];
    if (failingHosts.includes(host)) {
      throw new Error("Network request failed");
    }
    const name = host.split(".")[0];
    return {
      ok: true,
      json: async () => ({
        class: [{ type_id: 1, type_name: "电影" }],
        list: [
          {
            vod_id: 1,
            vod_name: `${name} 电影`,
            vod_pic: "",
            type_name: "动作片",
            vod_play_from: "mp4$$$hls",
            vod_play_url: "HD$https://video.example/a.mp4$$$HD$https://video.example/a.m3u8",
          },
          {
            vod_id: 2,
            vod_name: `${name} 短剧`,
            vod_pic: "",
            type_name: "短剧",
            vod_play_url: "HD$https://video.example/b.m3u8",
          },
        ],
        total: 2,
        page: 1,
        pagecount: 1,
      }),
    } as Response;
  });

const requestedHosts = () =>
  Array.from(new Set((fetch as jest.Mock).mock.calls.map(([input]) => String(input).match(/^https:\/\/([^/]+)/)![1]))).sort();

describe("source modes in the API client", () => {
  let api: API;

  beforeEach(() => {
    jest.restoreAllMocks();
    api = new API();
    api.setSubscribedVideoSources([alpha, beta, adult]);
    mockSourceFetch();
  });

  it("searches everything in full mode (the default)", async () => {
    const { results } = await api.searchVideos("x");
    expect(results.map((item) => item.title)).toEqual(["alpha 电影", "alpha 短剧", "beta 电影", "beta 短剧"]);
    expect(results[0].playback_sources?.length).toBe(2);
  });

  it("keeps a source that is off out of search, single-source search, the source list and the home page", async () => {
    api.setSourceModes({ beta: "off" });

    const { results } = await api.searchVideos("x");
    expect(results.map((item) => item.source)).toEqual(["alpha", "alpha"]);
    expect(requestedHosts()).toEqual(["alpha.example"]);

    await expect(api.searchVideo("beta 电影", "beta")).resolves.toEqual({ results: [] });
    // 按 id 打开已知条目(续播)不受片源模式影响
    expect((await api.searchVideo("beta 电影", "beta", undefined, "1")).results.length).toBeGreaterThan(0);
    (fetch as jest.Mock).mockClear();
    await api.searchVideos("x");
    expect((await api.getResources()).map((site) => site.key)).toEqual(["alpha"]);

    (fetch as jest.Mock).mockClear();
    const home = await api.getDoubanData("movie", "热门", 20, 0);
    expect(home.list.every((item) => item.source === "alpha")).toBe(true);
    expect(requestedHosts()).toEqual(["alpha.example", "m.douban.com"]);

    // 设置页仍然列得出被关闭的源(否则没法再打开);成人源照常按开关隐藏
    expect((await api.getModeConfigurableSources()).map((site) => site.key)).toEqual(["alpha", "beta"]);
  });

  it("takes effect immediately when the mode changes", async () => {
    api.setSourceModes({ beta: "off" });
    expect((await api.searchVideos("x")).results.some((item) => item.source === "beta")).toBe(false);
    api.setSourceModes({});
    expect((await api.searchVideos("x")).results.some((item) => item.source === "beta")).toBe(true);
  });

  it("keeps only film and series items for a source in media mode", async () => {
    api.setSourceModes({ alpha: "media" });
    const { results } = await api.searchVideos("x");
    expect(results.map((item) => item.title)).toEqual(["alpha 电影", "beta 电影", "beta 短剧"]);
  });

  it("keeps only the steadiest line for a source in stable mode", async () => {
    api.setSourceModes({ alpha: "stable" });
    const { results } = await api.searchVideos("x");
    const alphaMovie = results.find((item) => item.title === "alpha 电影")!;
    const betaMovie = results.find((item) => item.title === "beta 电影")!;
    expect(alphaMovie.playback_sources?.length).toBe(1);
    expect(alphaMovie.episodes).toEqual(["https://video.example/a.m3u8"]);
    expect(betaMovie.playback_sources?.length).toBe(2);
  });

  it("does not loosen the adult filter", async () => {
    api.setSourceModes({ adult: "full" });
    const { results } = await api.searchVideos("x");
    expect(results.some((item) => item.source === "adult")).toBe(false);
    expect(requestedHosts()).not.toContain("adult.example");
  });

  describe("incremental search", () => {
    it("reports per-source progress and skips sources that are off or hidden", async () => {
      api.setSourceModes({ beta: "off" });
      const snapshots: any[] = [];
      const final = await api.searchVideosIncremental("x", (snapshot) => snapshots.push(snapshot));
      expect(snapshots[0].progress).toEqual([{ key: "alpha", name: "Alpha", status: "searching", count: 0 }]);
      expect(final.progress).toEqual([{ key: "alpha", name: "Alpha", status: "done", count: 2 }]);
      expect(final.results.map((item) => item.title)).toEqual(["alpha 电影", "alpha 短剧"]);
      expect(requestedHosts()).toEqual(["alpha.example"]);
    });

    it("still returns the other sources' results when one source fails", async () => {
      jest.restoreAllMocks();
      mockSourceFetch(["alpha.example"]);
      const final = await api.searchVideosIncremental("x", () => {});
      expect(final.progress.map((item) => [item.key, item.status])).toEqual([
        ["alpha", "failed"],
        ["beta", "done"],
      ]);
      expect(final.results.map((item) => item.source)).toEqual(["beta", "beta"]);
      expect(final.done).toBe(true);
    });

    it("applies each source's mode to its results", async () => {
      api.setSourceModes({ alpha: "media" });
      const final = await api.searchVideosIncremental("x", () => {});
      expect(final.results.map((item) => item.title)).toEqual(["alpha 电影", "beta 电影", "beta 短剧"]);
      expect(final.progress[0].count).toBe(1);
    });
  });
});
