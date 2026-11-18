import useDetailStore from "../detailStore";
import { api } from "@/services/api";
import { FavoriteManager } from "@/services/storage";
import { getResolutionFromM3U8 } from "@/services/m3u8";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

jest.mock("@/services/storage", () => ({
  FavoriteManager: {
    isFavorited: jest.fn(),
    toggle: jest.fn(),
  },
}));

jest.mock("@/services/m3u8", () => ({
  getResolutionFromM3U8: jest.fn(),
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

const flushPromises = async () => {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
};

describe("detailStore", () => {
  beforeEach(() => {
    jest.restoreAllMocks();
    useDetailStore.setState({
      q: null,
      searchResults: [],
      sources: [],
      detail: null,
      loading: true,
      error: null,
      allSourcesLoaded: false,
      controller: null,
      isFavorited: false,
      failedSources: new Set(),
    });
    (FavoriteManager.isFavorited as jest.Mock).mockResolvedValue(false);
  });

  it("shows the first playable source before m3u8 resolution probing finishes", async () => {
    jest.spyOn(api, "getResources").mockResolvedValue([{ key: "demo", name: "Demo", api: "https://source.example/vod" }]);
    jest.spyOn(api, "searchVideo").mockResolvedValue({
      results: [
        {
          id: 7,
          title: "Movie",
          poster: "https://image.example/movie.jpg",
          episodes: ["https://video.example/movie.m3u8"],
          source: "demo",
          source_name: "Demo",
          year: "2026",
        },
      ],
    });

    let resolveResolution!: (resolution: string | null) => void;
    (getResolutionFromM3U8 as jest.Mock).mockReturnValue(
      new Promise((resolve) => {
        resolveResolution = resolve;
      })
    );

    const initPromise = useDetailStore.getState().init("Movie");
    await flushPromises();

    expect(useDetailStore.getState().detail).toEqual(
      expect.objectContaining({
        source: "demo",
        title: "Movie",
      })
    );
    expect(useDetailStore.getState().loading).toBe(false);

    resolveResolution("1080p");
    await initPromise;
  });

  it("shows every playback line from the same subscribed source", async () => {
    jest.spyOn(api, "getResources").mockResolvedValue([{ key: "primary", name: "主线路", api: "https://source.example/vod" }]);
    jest.spyOn(api, "searchVideo").mockResolvedValue({
      results: [
        {
          id: 7,
          title: "Movie",
          poster: "",
          episodes: ["https://video.example/qiyi.m3u8"],
          source: "primary",
          source_name: "主线路",
          year: "2026",
          playback_sources: [
            { key: "0:qiyi", name: "qiyi", episodes: ["https://video.example/qiyi.m3u8"] },
            { key: "1:qq", name: "qq", episodes: ["https://video.example/qq.m3u8"] },
          ],
        },
      ],
    });
    (getResolutionFromM3U8 as jest.Mock).mockResolvedValue("1080p");

    await useDetailStore.getState().init("Movie");

    expect(useDetailStore.getState().searchResults.map((result) => result.source_name)).toEqual([
      "主线路 · qiyi",
      "主线路 · qq",
    ]);
  });

  it("keeps the latest selected source when an older setDetail request answers last", async () => {
    const make = (source: string) =>
      ({ id: 1, title: "片", poster: "", episodes: [`https://${source}.example/1.m3u8`], source, source_name: source, year: "" });
    let resolveSlow!: (value: any) => void;
    jest.spyOn(api, "getVideoDetail").mockImplementation((source: string) =>
      source === "slow"
        ? new Promise((resolve) => (resolveSlow = resolve))
        : Promise.resolve({ id: "1", title: "片", poster: "", source, source_name: source })
    );
    (FavoriteManager.isFavorited as jest.Mock).mockImplementation(async (source: string) => source === "slow");

    const first = useDetailStore.getState().setDetail(make("slow"));
    await useDetailStore.getState().setDetail(make("fast"));
    resolveSlow({ id: "1", title: "片", poster: "", source: "slow", source_name: "slow" });
    await first;

    expect(useDetailStore.getState().detail?.source).toBe("fast");
    expect(useDetailStore.getState().isFavorited).toBe(false);
  });
});
