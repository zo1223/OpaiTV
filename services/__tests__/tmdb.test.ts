import {
  clearTmdbCache,
  fetchTmdbMetadata,
  guessTmdbMediaType,
  isTmdbBearerToken,
  mergeTmdbMetadata,
  normalizeTmdbTitle,
  pickTmdbMatch,
  stripSeasonSuffix,
} from "../tmdb";

const V3_KEY = "0123456789abcdef0123456789abcdef";
const BEARER = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ0ZXN0In0.c2lnbmF0dXJl";

const movie = (overrides: Record<string, unknown> = {}) => ({
  id: 11,
  media_type: "movie",
  title: "流浪地球",
  original_title: "流浪地球",
  poster_path: "/poster.jpg",
  overview: "太阳即将毁灭。",
  vote_average: 7.94,
  vote_count: 1200,
  release_date: "2019-02-05",
  ...overrides,
});

const tv = (overrides: Record<string, unknown> = {}) => ({
  id: 22,
  media_type: "tv",
  name: "庆余年",
  original_name: "庆余年",
  poster_path: "/tv.jpg",
  overview: "范闲的故事。",
  vote_average: 8.06,
  vote_count: 300,
  first_air_date: "2019-11-26",
  ...overrides,
});

const okResponse = (results: unknown[]) => ({ ok: true, status: 200, json: async () => ({ results }) });

describe("tmdb", () => {
  let fetchMock: jest.Mock;
  const originalFetch = global.fetch;

  beforeEach(() => {
    clearTmdbCache();
    fetchMock = jest.fn();
    (global as unknown as { fetch: unknown }).fetch = fetchMock;
  });

  afterEach(() => {
    (global as unknown as { fetch: unknown }).fetch = originalFetch;
    jest.useRealTimers();
  });

  describe("title handling", () => {
    it("strips season and note suffixes", () => {
      expect(stripSeasonSuffix("庆余年 第二季")).toBe("庆余年");
      expect(stripSeasonSuffix("庆余年第2部")).toBe("庆余年");
      expect(stripSeasonSuffix("Stranger Things Season 2")).toBe("Stranger Things");
      expect(stripSeasonSuffix("Stranger Things S02")).toBe("Stranger Things");
      expect(stripSeasonSuffix("鱿鱼游戏 2")).toBe("鱿鱼游戏");
      expect(stripSeasonSuffix("流浪地球(2019)[国语版]")).toBe("流浪地球");
      expect(stripSeasonSuffix("1917")).toBe("1917");
      expect(stripSeasonSuffix("银翼杀手 2049")).toBe("银翼杀手 2049");
    });

    it("normalizes width, case, spaces and punctuation", () => {
      expect(normalizeTmdbTitle("Ｓｔｒａｎｇｅｒ  Things!")).toBe("strangerthings");
      expect(normalizeTmdbTitle("流浪地球：重启")).toBe("流浪地球重启");
      expect(normalizeTmdbTitle(undefined)).toBe("");
    });

    it("guesses the media type from the source category", () => {
      expect(guessTmdbMediaType("剧情片")).toBe("movie");
      expect(guessTmdbMediaType("电影")).toBe("movie");
      expect(guessTmdbMediaType("国产剧")).toBe("tv");
      expect(guessTmdbMediaType("日本动漫")).toBe("tv");
      expect(guessTmdbMediaType("综艺")).toBe("tv");
      expect(guessTmdbMediaType("")).toBeNull();
      expect(guessTmdbMediaType(undefined)).toBeNull();
    });

    it("tells a v4 token from a v3 key", () => {
      expect(isTmdbBearerToken(BEARER)).toBe(true);
      expect(isTmdbBearerToken(V3_KEY)).toBe(false);
    });
  });

  describe("pickTmdbMatch", () => {
    it("matches an exact title and builds the metadata", () => {
      expect(pickTmdbMatch([movie()], { title: "流浪地球", year: "2019", type_name: "科幻片" })).toEqual({
        tmdbId: 11,
        mediaType: "movie",
        title: "流浪地球",
        posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
        overview: "太阳即将毁灭。",
        rating: 7.9,
        year: "2019",
      });
    });

    it("matches a show when the source title carries a season suffix", () => {
      expect(pickTmdbMatch([tv()], { title: "庆余年 第二季", year: "2024", type_name: "国产剧" })?.tmdbId).toBe(22);
    });

    it("returns null without an exact title match", () => {
      expect(pickTmdbMatch([movie({ title: "流浪地球2", original_title: "流浪地球2" })], { title: "流浪地球" })).toBeNull();
      expect(pickTmdbMatch([{ id: 5, media_type: "person", name: "流浪地球" }], { title: "流浪地球" })).toBeNull();
      expect(pickTmdbMatch([], { title: "流浪地球" })).toBeNull();
      expect(pickTmdbMatch(undefined, { title: "流浪地球" })).toBeNull();
      expect(pickTmdbMatch([movie()], { title: "" })).toBeNull();
    });

    it("prefers the candidate whose year matches the source", () => {
      const results = [movie({ id: 1, release_date: "1996-01-01" }), movie({ id: 2, release_date: "2019-02-05" })];
      expect(pickTmdbMatch(results, { title: "流浪地球", year: "2019" })?.tmdbId).toBe(2);
      expect(pickTmdbMatch(results, { title: "流浪地球", year: "2020" })?.tmdbId).toBe(2);
      expect(pickTmdbMatch(results, { title: "流浪地球" })?.tmdbId).toBe(1);
    });

    it("prefers the media type hinted by the source category", () => {
      const results = [movie({ id: 1, title: "庆余年", original_title: "庆余年" }), tv({ id: 2 })];
      expect(pickTmdbMatch(results, { title: "庆余年", type_name: "国产剧" })?.tmdbId).toBe(2);
      expect(pickTmdbMatch(results, { title: "庆余年", type_name: "剧情片" })?.tmdbId).toBe(1);
      expect(pickTmdbMatch(results, { title: "庆余年" })?.tmdbId).toBe(1);
    });

    it("leaves out a rating nobody voted for and a missing poster", () => {
      const meta = pickTmdbMatch([movie({ vote_average: 0, vote_count: 0, poster_path: null, overview: " " })], {
        title: "流浪地球",
      });
      expect(meta?.rating).toBeUndefined();
      expect(meta?.posterUrl).toBeUndefined();
      expect(meta?.overview).toBeUndefined();
    });
  });

  describe("fetchTmdbMetadata", () => {
    it("makes no request without a key", async () => {
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, "")).resolves.toBeNull();
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, "   ")).resolves.toBeNull();
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, undefined)).resolves.toBeNull();
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it("sends a v3 key as api_key and only to the TMDB API host", async () => {
      fetchMock.mockResolvedValue(okResponse([movie()]));
      const meta = await fetchTmdbMetadata({ title: "流浪地球", year: "2019" }, ` ${V3_KEY} `);
      expect(meta?.tmdbId).toBe(11);
      expect(fetchMock).toHaveBeenCalledTimes(1);
      const [url, init] = fetchMock.mock.calls[0];
      expect(url.startsWith("https://api.themoviedb.org/3/search/multi?")).toBe(true);
      expect(url).toContain(`api_key=${V3_KEY}`);
      expect(url).toContain(`query=${encodeURIComponent("流浪地球")}`);
      expect(url).toContain("language=zh-CN");
      expect(url).toContain("include_adult=false");
      expect(init.method).toBe("GET");
      expect(init.headers.Authorization).toBeUndefined();
    });

    it("sends a v4 token as a bearer header and keeps it out of the URL", async () => {
      fetchMock.mockResolvedValue(okResponse([movie()]));
      await fetchTmdbMetadata({ title: "流浪地球" }, BEARER);
      const [url, init] = fetchMock.mock.calls[0];
      expect(url.startsWith("https://api.themoviedb.org/3/search/multi?")).toBe(true);
      expect(url).not.toContain("api_key");
      expect(url).not.toContain(BEARER);
      expect(url).toContain("include_adult=false");
      expect(init.headers.Authorization).toBe(`Bearer ${BEARER}`);
    });

    it("queries with the season suffix removed", async () => {
      fetchMock.mockResolvedValue(okResponse([tv()]));
      const meta = await fetchTmdbMetadata({ title: "庆余年 第二季", type_name: "国产剧" }, V3_KEY);
      expect(meta?.mediaType).toBe("tv");
      expect(fetchMock.mock.calls[0][0]).toContain(`query=${encodeURIComponent("庆余年")}&`);
    });

    it("returns null when nothing matches", async () => {
      fetchMock.mockResolvedValue(okResponse([movie({ title: "别的片", original_title: "别的片" })]));
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY)).resolves.toBeNull();
    });

    it("returns null on a network error", async () => {
      fetchMock.mockRejectedValue(new Error("Network request failed"));
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY)).resolves.toBeNull();
    });

    it("returns null on 401 and other non-2xx answers", async () => {
      const json = jest.fn(async () => ({ status_message: "Invalid API key" }));
      fetchMock.mockResolvedValue({ ok: false, status: 401, json });
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY)).resolves.toBeNull();
      fetchMock.mockResolvedValue({ ok: false, status: 500, json });
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY)).resolves.toBeNull();
    });

    it("returns null on invalid JSON or an unexpected body", async () => {
      fetchMock.mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => {
          throw new SyntaxError("Unexpected token <");
        },
      });
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY)).resolves.toBeNull();
      fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => null });
      await expect(fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY)).resolves.toBeNull();
    });

    it("returns null on timeout even when fetch never settles", async () => {
      jest.useFakeTimers();
      fetchMock.mockReturnValue(new Promise(() => undefined));
      const pending = fetchTmdbMetadata({ title: "流浪地球" }, V3_KEY, { timeoutMs: 50 });
      jest.advanceTimersByTime(60);
      await expect(pending).resolves.toBeNull();
      const [, init] = fetchMock.mock.calls[0];
      expect(init.signal.aborted).toBe(true);
    });

    it("caches a successful lookup but not a failure", async () => {
      fetchMock.mockRejectedValueOnce(new Error("offline"));
      await expect(fetchTmdbMetadata({ title: "流浪地球", year: "2019" }, V3_KEY)).resolves.toBeNull();
      fetchMock.mockResolvedValue(okResponse([movie()]));
      await expect(fetchTmdbMetadata({ title: "流浪地球", year: "2019" }, V3_KEY)).resolves.not.toBeNull();
      await expect(fetchTmdbMetadata({ title: "流浪地球", year: "2019" }, V3_KEY)).resolves.not.toBeNull();
      expect(fetchMock).toHaveBeenCalledTimes(2);
      // 清掉 Key 之后即使有缓存也不返回
      await expect(fetchTmdbMetadata({ title: "流浪地球", year: "2019" }, "")).resolves.toBeNull();
    });
  });

  describe("mergeTmdbMetadata", () => {
    const meta = {
      tmdbId: 11,
      mediaType: "movie" as const,
      title: "流浪地球",
      posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
      overview: "TMDB 简介",
      rating: 7.9,
      year: "2019",
    };

    it("returns the same object when there is no metadata", () => {
      const detail = { poster: "", desc: "", year: "" };
      expect(mergeTmdbMetadata(detail, null)).toBe(detail);
      expect(mergeTmdbMetadata(detail, undefined)).toBe(detail);
    });

    it("keeps every original value that is present", () => {
      const detail = { title: "流浪地球", poster: "https://img.example/p.jpg", desc: "源站简介", year: "2018" };
      expect(mergeTmdbMetadata(detail, meta)).toEqual({ ...detail, tmdbRating: 7.9 });
    });

    it("fills only what is blank", () => {
      expect(mergeTmdbMetadata({ title: "流浪地球", poster: "", desc: "  ", year: "unknown" }, meta)).toEqual({
        title: "流浪地球",
        poster: "https://image.tmdb.org/t/p/w500/poster.jpg",
        desc: "TMDB 简介",
        year: "2019",
        tmdbRating: 7.9,
      });
    });

    it("does not blank out fields when TMDB has nothing either", () => {
      const sparse = { tmdbId: 1, mediaType: "tv" as const, title: "x" };
      expect(mergeTmdbMetadata({ poster: "", desc: "", year: "" }, sparse)).toEqual({
        poster: "",
        desc: "",
        year: "",
        tmdbRating: undefined,
      });
    });
  });
});
