import AsyncStorage from "@react-native-async-storage/async-storage";
import { useSettingsStore } from "../settingsStore";
import useDetailStore from "../detailStore";
import usePlayerStore from "../playerStore";
import { clearTmdbCache, TMDB_API_KEY_STORAGE_KEY } from "@/services/tmdb";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
  getAllKeys: jest.fn(async () => []),
  multiGet: jest.fn(async () => []),
  multiRemove: jest.fn(),
}));

jest.mock("react-native-toast-message", () => ({
  show: jest.fn(),
}));

jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: {
    withTag: () => ({
      debug: jest.fn(),
      error: jest.fn(),
      info: jest.fn(),
      warn: jest.fn(),
    }),
  },
}));

jest.mock("@/services/m3u8", () => ({
  getAdRangesFromM3U8: jest.fn(async () => []),
  getResolutionFromM3U8: jest.fn(async () => null),
}));

const Toast = jest.requireMock("react-native-toast-message") as { show: jest.Mock };
const V3_KEY = "0123456789abcdef0123456789abcdef";
const RELAY = "https://relay.example/?url=";
const EPISODE = "https://video.example/movie.m3u8";

const detailOf = (overrides: Record<string, unknown> = {}) => ({
  id: 7,
  title: "流浪地球",
  poster: "",
  episodes: [EPISODE],
  source: "demo",
  source_name: "Demo",
  year: "2019",
  type_name: "科幻片",
  ...overrides,
});

const flush = async () => {
  for (let i = 0; i < 10; i += 1) {
    await Promise.resolve();
  }
};

describe("playback proxy mode and TMDB key (stores)", () => {
  let stored: Record<string, string>;
  let fetchMock: jest.Mock;
  const originalFetch = global.fetch;

  beforeEach(() => {
    stored = {};
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => stored[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      stored[key] = value;
    });
    (AsyncStorage.removeItem as jest.Mock).mockImplementation(async (key: string) => {
      delete stored[key];
    });
    fetchMock = jest.fn();
    (global as unknown as { fetch: unknown }).fetch = fetchMock;
    Toast.show.mockClear();
    clearTmdbCache();
    useSettingsStore.setState({ playbackProxyMode: "system", playbackProxyUrl: "", tmdbApiKey: "" });
    useDetailStore.setState({ detail: null, searchResults: [], tmdbMeta: null });
    usePlayerStore.getState().reset();
  });

  afterEach(() => {
    usePlayerStore.getState().reset();
    (global as unknown as { fetch: unknown }).fetch = originalFetch;
  });

  describe("proxy settings", () => {
    it("refuses forced mode without a valid address", async () => {
      await expect(useSettingsStore.getState().setPlaybackProxyMode("forced")).resolves.toBe(false);
      expect(useSettingsStore.getState().playbackProxyMode).toBe("system");
      expect(stored.mytv_settings).toBeUndefined();

      await useSettingsStore.getState().setPlaybackProxyUrl("127.0.0.1:7890");
      await expect(useSettingsStore.getState().setPlaybackProxyMode("forced")).resolves.toBe(false);
      expect(useSettingsStore.getState().playbackProxyMode).toBe("system");
    });

    it("saves the address and the mode locally and restores them on load", async () => {
      await useSettingsStore.getState().setPlaybackProxyUrl(`  ${RELAY} `);
      await expect(useSettingsStore.getState().setPlaybackProxyMode("forced")).resolves.toBe(true);
      expect(JSON.parse(stored.mytv_settings)).toMatchObject({ playbackProxyMode: "forced", playbackProxyUrl: RELAY });

      useSettingsStore.setState({ playbackProxyMode: "system", playbackProxyUrl: "" });
      await useSettingsStore.getState().loadSettings();
      expect(useSettingsStore.getState().playbackProxyMode).toBe("forced");
      expect(useSettingsStore.getState().playbackProxyUrl).toBe(RELAY);

      await expect(useSettingsStore.getState().setPlaybackProxyMode("direct")).resolves.toBe(true);
      expect(JSON.parse(stored.mytv_settings)).toMatchObject({ playbackProxyMode: "direct", playbackProxyUrl: RELAY });
    });

    it("drops back to system when the address is cleared while forced", async () => {
      await useSettingsStore.getState().setPlaybackProxyUrl(RELAY);
      await useSettingsStore.getState().setPlaybackProxyMode("forced");
      await useSettingsStore.getState().setPlaybackProxyUrl("");
      expect(useSettingsStore.getState().playbackProxyMode).toBe("system");
      expect(JSON.parse(stored.mytv_settings)).toMatchObject({ playbackProxyMode: "system", playbackProxyUrl: "" });
    });

    it("loads damaged or missing values as system", async () => {
      stored.mytv_settings = JSON.stringify({ playbackProxyMode: "forced", playbackProxyUrl: "" });
      await useSettingsStore.getState().loadSettings();
      expect(useSettingsStore.getState().playbackProxyMode).toBe("system");

      stored.mytv_settings = JSON.stringify({ playbackProxyMode: 42 });
      await useSettingsStore.getState().loadSettings();
      expect(useSettingsStore.getState().playbackProxyMode).toBe("system");
      expect(useSettingsStore.getState().playbackProxyUrl).toBe("");
    });

    it("is kept by the general save", async () => {
      await useSettingsStore.getState().setPlaybackProxyUrl(RELAY);
      await useSettingsStore.getState().setPlaybackProxyMode("forced");
      await useSettingsStore.getState().saveSettings();
      expect(JSON.parse(stored.mytv_settings)).toMatchObject({ playbackProxyMode: "forced", playbackProxyUrl: RELAY });
    });
  });

  describe("proxy mode at play time", () => {
    const load = async () => {
      useDetailStore.setState({ detail: detailOf(), searchResults: [detailOf()] });
      await usePlayerStore.getState().loadVideo({ source: "demo", id: "7", episodeIndex: 0, title: "流浪地球" });
      return usePlayerStore.getState().episodes.map((episode) => episode.url);
    };

    it("plays the original URL in system and direct mode", async () => {
      await useSettingsStore.getState().setPlaybackProxyUrl(RELAY);
      expect(await load()).toEqual([EPISODE]);

      usePlayerStore.getState().reset();
      await useSettingsStore.getState().setPlaybackProxyMode("direct");
      expect(await load()).toEqual([EPISODE]);
    });

    it("plays through the relay in forced mode", async () => {
      await useSettingsStore.getState().setPlaybackProxyUrl(RELAY);
      await useSettingsStore.getState().setPlaybackProxyMode("forced");
      expect(await load()).toEqual([`${RELAY}${encodeURIComponent(EPISODE)}`]);
    });

    it("applies a mode switch to the next playback only", async () => {
      await useSettingsStore.getState().setPlaybackProxyUrl(RELAY);
      expect(await load()).toEqual([EPISODE]);

      await useSettingsStore.getState().setPlaybackProxyMode("forced");
      // 正在播放的这一次不变
      expect(usePlayerStore.getState().episodes.map((episode) => episode.url)).toEqual([EPISODE]);

      usePlayerStore.getState().reset();
      expect(await load()).toEqual([`${RELAY}${encodeURIComponent(EPISODE)}`]);

      await useSettingsStore.getState().setPlaybackProxyMode("direct");
      expect(usePlayerStore.getState().episodes.map((episode) => episode.url)).toEqual([
        `${RELAY}${encodeURIComponent(EPISODE)}`,
      ]);
    });
  });

  describe("TMDB key", () => {
    it("is stored under its own key and never inside the settings JSON", async () => {
      await useSettingsStore.getState().setTmdbApiKey(`  ${V3_KEY} `);
      expect(useSettingsStore.getState().tmdbApiKey).toBe(V3_KEY);
      expect(stored[TMDB_API_KEY_STORAGE_KEY]).toBe(V3_KEY);

      await useSettingsStore.getState().setPlaybackProxyMode("direct");
      await useSettingsStore.getState().saveSettings();
      expect(stored.mytv_settings).toBeDefined();
      expect(stored.mytv_settings).not.toContain(V3_KEY);
      expect(Object.keys(JSON.parse(stored.mytv_settings))).not.toContain("tmdbApiKey");
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it("is restored on load and removed when cleared", async () => {
      stored[TMDB_API_KEY_STORAGE_KEY] = V3_KEY;
      await useSettingsStore.getState().loadSettings();
      expect(useSettingsStore.getState().tmdbApiKey).toBe(V3_KEY);

      await useSettingsStore.getState().setTmdbApiKey("");
      expect(useSettingsStore.getState().tmdbApiKey).toBe("");
      expect(TMDB_API_KEY_STORAGE_KEY in stored).toBe(false);
    });

    it("treats unreadable storage as no key", async () => {
      (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => {
        if (key === TMDB_API_KEY_STORAGE_KEY) throw new Error("storage unavailable");
        return stored[key] ?? null;
      });
      await expect(useSettingsStore.getState().loadSettings()).resolves.toBeUndefined();
      expect(useSettingsStore.getState().tmdbApiKey).toBe("");
    });
  });

  describe("TMDB lookup from the detail store", () => {
    const tmdbMovie = {
      id: 11,
      media_type: "movie",
      title: "流浪地球",
      poster_path: "/poster.jpg",
      overview: "太阳即将毁灭。",
      vote_average: 7.9,
      vote_count: 100,
      release_date: "2019-02-05",
    };

    it("does nothing without a key", async () => {
      const detail = detailOf();
      useDetailStore.setState({ detail });
      await flush();
      expect(fetchMock).not.toHaveBeenCalled();
      expect(useDetailStore.getState().tmdbMeta).toBeNull();
      expect(useDetailStore.getState().detail).toBe(detail);
      expect(Toast.show).not.toHaveBeenCalled();
    });

    it("fills tmdbMeta in the background and only talks to the TMDB API", async () => {
      useSettingsStore.setState({ tmdbApiKey: V3_KEY });
      fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ results: [tmdbMovie] }) });
      const detail = detailOf();
      useDetailStore.setState({ detail });
      // 详情立即可用,不等 TMDB
      expect(useDetailStore.getState().detail).toBe(detail);
      await flush();
      expect(useDetailStore.getState().tmdbMeta).toMatchObject({
        tmdbId: 11,
        posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
        rating: 7.9,
      });
      expect(useDetailStore.getState().detail).toBe(detail);
      expect(fetchMock).toHaveBeenCalledTimes(1);
      for (const [url] of fetchMock.mock.calls) {
        expect(String(url).startsWith("https://api.themoviedb.org/")).toBe(true);
      }

      // 同一部片换源:不重复查,结果保留
      useDetailStore.setState({ detail: detailOf({ source: "other", source_name: "Other" }) });
      await flush();
      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(useDetailStore.getState().tmdbMeta?.tmdbId).toBe(11);
    });

    it("leaves the detail untouched and stays silent when the request fails", async () => {
      useSettingsStore.setState({ tmdbApiKey: V3_KEY });
      fetchMock.mockRejectedValue(new Error("Network request failed"));
      const detail = detailOf();
      useDetailStore.setState({ detail });
      await flush();
      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(useDetailStore.getState().tmdbMeta).toBeNull();
      expect(useDetailStore.getState().detail).toBe(detail);
      expect(useDetailStore.getState().error ?? null).toBeNull();
      expect(Toast.show).not.toHaveBeenCalled();

      fetchMock.mockResolvedValue({ ok: false, status: 401, json: async () => ({}) });
      useDetailStore.setState({ detail: detailOf({ id: 8, title: "另一部片" }) });
      await flush();
      expect(useDetailStore.getState().tmdbMeta).toBeNull();
      expect(Toast.show).not.toHaveBeenCalled();
    });

    it("ignores a late answer for a title the user has left", async () => {
      useSettingsStore.setState({ tmdbApiKey: V3_KEY });
      let resolveFirst: (value: unknown) => void = () => undefined;
      fetchMock.mockReturnValueOnce(new Promise((resolve) => (resolveFirst = resolve)));
      fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ results: [] }) });

      useDetailStore.setState({ detail: detailOf() });
      await flush();
      useDetailStore.setState({ detail: detailOf({ id: 9, title: "别的片" }) });
      await flush();

      resolveFirst({ ok: true, status: 200, json: async () => ({ results: [tmdbMovie] }) });
      await flush();
      expect(useDetailStore.getState().tmdbMeta).toBeNull();
    });

    it("clears the previous title's metadata when another title opens", async () => {
      useSettingsStore.setState({ tmdbApiKey: V3_KEY });
      fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ results: [tmdbMovie] }) });
      useDetailStore.setState({ detail: detailOf() });
      await flush();
      expect(useDetailStore.getState().tmdbMeta).not.toBeNull();

      fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ results: [] }) });
      useDetailStore.setState({ detail: detailOf({ id: 9, title: "别的片" }) });
      expect(useDetailStore.getState().tmdbMeta).toBeNull();
      await flush();
      expect(useDetailStore.getState().tmdbMeta).toBeNull();
    });
  });
});
