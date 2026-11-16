import useDetailStore from "../detailStore";
import usePlayerStore from "../playerStore";
import { PlayRecordManager, PlayerSettingsManager } from "@/services/storage";
import { getAdRangesFromM3U8 } from "@/services/m3u8";
import Toast from "react-native-toast-message";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
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

jest.mock("@/services/storage", () => ({
  FavoriteManager: {
    isFavorited: jest.fn(),
    toggle: jest.fn(),
  },
  PlayRecordManager: {
    get: jest.fn(),
    save: jest.fn(),
  },
  PlayerSettingsManager: {
    get: jest.fn(),
    save: jest.fn(),
  },
}));

jest.mock("@/services/m3u8", () => ({
  getAdRangesFromM3U8: jest.fn(),
}));

describe("playerStore", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    usePlayerStore.getState().reset();
    jest.clearAllMocks();
    useDetailStore.setState({
      q: null,
      searchResults: [],
      sources: [],
      detail: null,
      loading: false,
      error: null,
      allSourcesLoaded: true,
      controller: null,
      isFavorited: false,
      failedSources: new Set(),
    });
  });

  afterEach(() => {
    // reset() mirrors the player screen unmount path and clears pending timers.
    usePlayerStore.getState().reset();
  });

  it("keeps loaded episodes when play record lookup is unavailable in subscription-only mode", async () => {
    useDetailStore.setState({
      detail: {
        id: 7,
        title: "Movie",
        poster: "",
        episodes: ["https://video.example/movie.m3u8"],
        source: "demo",
        source_name: "Demo",
        year: "2026",
      },
      searchResults: [
        {
          id: 7,
          title: "Movie",
          poster: "",
          episodes: ["https://video.example/movie.m3u8"],
          source: "demo",
          source_name: "Demo",
          year: "2026",
        },
      ],
    });
    (PlayRecordManager.get as jest.Mock).mockRejectedValue(new Error("API_URL_NOT_SET"));
    (PlayerSettingsManager.get as jest.Mock).mockResolvedValue(null);

    await usePlayerStore.getState().loadVideo({
      source: "demo",
      id: "7",
      episodeIndex: 0,
      title: "Movie",
    });

    expect(usePlayerStore.getState().episodes).toEqual([
      expect.objectContaining({ url: "https://video.example/movie.m3u8" }),
    ]);
    expect(usePlayerStore.getState().currentEpisodeIndex).toBe(0);
    expect(usePlayerStore.getState().isLoading).toBe(false);
  });

  it("loads ad ranges for m3u8 episodes", async () => {
    useDetailStore.setState({
      detail: {
        id: 7,
        title: "Movie",
        poster: "",
        episodes: ["https://video.example/movie.m3u8"],
        source: "demo",
        source_name: "Demo",
        year: "2026",
      },
      searchResults: [
        {
          id: 7,
          title: "Movie",
          poster: "",
          episodes: ["https://video.example/movie.m3u8"],
          source: "demo",
          source_name: "Demo",
          year: "2026",
        },
      ],
    });
    (PlayRecordManager.get as jest.Mock).mockResolvedValue(null);
    (PlayerSettingsManager.get as jest.Mock).mockResolvedValue(null);
    (getAdRangesFromM3U8 as jest.Mock).mockResolvedValue([{ startMillis: 10000, endMillis: 30000 }]);

    await usePlayerStore.getState().loadVideo({
      source: "demo",
      id: "7",
      episodeIndex: 0,
      title: "Movie",
    });

    expect(getAdRangesFromM3U8).toHaveBeenCalledWith("https://video.example/movie.m3u8");
    expect(usePlayerStore.getState().adRanges).toEqual([{ startMillis: 10000, endMillis: 30000 }]);
  });

  it("discards a load that finishes after the player was reset", async () => {
    const movie = {
      id: 7,
      title: "Movie",
      poster: "",
      episodes: ["https://video.example/movie.m3u8"],
      source: "demo",
      source_name: "Demo",
      year: "2026",
    };
    useDetailStore.setState({ detail: movie, searchResults: [movie] });
    let resolvePlayRecord: (value: null) => void = () => {};
    (PlayRecordManager.get as jest.Mock).mockReturnValue(
      new Promise((resolve) => {
        resolvePlayRecord = resolve;
      })
    );
    (PlayerSettingsManager.get as jest.Mock).mockResolvedValue(null);
    (getAdRangesFromM3U8 as jest.Mock).mockResolvedValue([]);

    const loadPromise = usePlayerStore.getState().loadVideo({
      source: "demo",
      id: "7",
      episodeIndex: 0,
      title: "Movie",
    });
    // The user leaves the player screen while the load is still in flight
    usePlayerStore.getState().reset();
    resolvePlayRecord(null);
    await loadPromise;

    expect(usePlayerStore.getState().episodes).toEqual([]);
    expect(usePlayerStore.getState().isLoading).toBe(true);
  });

  it("seeks to the end of the current ad range during playback", () => {
    const setPositionAsync = jest.fn().mockResolvedValue(undefined);
    useDetailStore.setState({
      detail: {
        id: 7,
        title: "Movie",
        poster: "https://image.example/movie.jpg",
        episodes: ["https://video.example/movie.m3u8"],
        source: "demo",
        source_name: "Demo",
        year: "2026",
      },
    });
    usePlayerStore.setState({
      videoRef: { current: { setPositionAsync } } as any,
      currentEpisodeIndex: 0,
      episodes: [{ url: "https://video.example/movie.m3u8", title: "Episode 1" }],
      adRanges: [{ startMillis: 10000, endMillis: 30000 }],
    } as any);

    usePlayerStore.getState().handlePlaybackStatusUpdate({
      isLoaded: true,
      positionMillis: 15000,
      durationMillis: 120000,
      didJustFinish: false,
    } as any);

    expect(setPositionAsync).toHaveBeenCalledWith(30000);
    expect(Toast.show).toHaveBeenCalledWith(expect.objectContaining({ text1: "\u5df2\u8df3\u8fc7\u5e7f\u544a" }));
  });

  it("saves the latest playback position when the player resets", () => {
    useDetailStore.setState({
      detail: {
        id: 7,
        title: "Movie",
        poster: "https://image.example/movie.jpg",
        episodes: ["https://video.example/movie.m3u8"],
        source: "demo",
        source_name: "Demo",
        year: "2026",
      },
      searchResults: [
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
    usePlayerStore.setState({
      currentEpisodeIndex: 0,
      episodes: [{ url: "https://video.example/movie.m3u8", title: "Episode 1" }],
      status: {
        isLoaded: true,
        positionMillis: 45000,
        durationMillis: 120000,
      } as any,
    });

    usePlayerStore.getState().reset();

    expect(PlayRecordManager.save).toHaveBeenCalledWith(
      "demo",
      "7",
      expect.objectContaining({
        title: "Movie",
        play_time: 45,
        total_time: 120,
        index: 1,
        total_episodes: 1,
      })
    );
  });
});
