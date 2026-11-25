import {
  MINI_PLAYER_SAVE_INTERVAL_MS,
  MiniPlayerSnapshot,
  resetMiniPlayerStoreForTests,
  useMiniPlayerStore,
} from "@/stores/miniPlayerStore";
import { PlayRecordManager, PlayerSettingsManager } from "@/services/storage";

jest.mock("@/services/storage", () => ({
  PlayRecordManager: { get: jest.fn(), save: jest.fn(async () => undefined) },
  PlayerSettingsManager: { get: jest.fn(async () => null) },
}));
jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: { withTag: () => ({ debug: jest.fn(), error: jest.fn(), info: jest.fn(), warn: jest.fn() }) },
}));

const mockGet = PlayRecordManager.get as jest.Mock;
const mockSave = PlayRecordManager.save as jest.Mock;
const mockSettingsGet = PlayerSettingsManager.get as jest.Mock;

const snapshot = (overrides: Partial<MiniPlayerSnapshot> = {}): MiniPlayerSnapshot => ({
  title: "山海",
  episodeLabel: "第 3 集",
  url: "https://media.example/ep3.m3u8",
  positionMillis: 65_000,
  durationMillis: 2_400_000,
  isPlaying: true,
  playbackRate: 1.25,
  routeParams: { source: "src", id: "7", title: "山海", episodeIndex: "2" },
  ...overrides,
});

const storedRecord = {
  title: "山海",
  cover: "https://img.example/p.jpg",
  index: 3,
  total_episodes: 12,
  play_time: 65,
  total_time: 2400,
  source_name: "示例源",
  year: "2024",
  type_name: "国产剧",
  save_time: 1_000,
};

const flush = async () => {
  for (let i = 0; i < 5; i += 1) {
    await Promise.resolve();
  }
};

describe("miniPlayerStore", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    resetMiniPlayerStoreForTests();
    mockGet.mockResolvedValue({ ...storedRecord });
    mockSettingsGet.mockResolvedValue(null);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it("starts idle", () => {
    const state = useMiniPlayerStore.getState();
    expect(state.active).toBe(false);
    expect(state.url).toBe("");
    expect(state.routeParams).toBeNull();
  });

  it("show() activates the bar expanded, at the handed-over position", () => {
    useMiniPlayerStore.getState().show(snapshot());
    const state = useMiniPlayerStore.getState();
    expect(state.active).toBe(true);
    expect(state.collapsed).toBe(false);
    expect(state.title).toBe("山海");
    expect(state.episodeLabel).toBe("第 3 集");
    expect(state.url).toBe("https://media.example/ep3.m3u8");
    expect(state.startPositionMillis).toBe(65_000);
    expect(state.positionMillis).toBe(65_000);
    expect(state.durationMillis).toBe(2_400_000);
    expect(state.isPlaying).toBe(true);
    expect(state.playbackRate).toBe(1.25);
    expect(state.routeParams).toEqual({ source: "src", id: "7", title: "山海", episodeIndex: "2" });
    expect(state.session).toBe(1);
  });

  it("show() ignores a snapshot without a playable url", () => {
    useMiniPlayerStore.getState().show(snapshot({ url: "" }));
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });

  it("show() bumps the session each time and saves the progress of the video it replaces", async () => {
    useMiniPlayerStore.getState().show(snapshot());
    useMiniPlayerStore.getState().setProgress(90_000, 2_400_000);
    useMiniPlayerStore
      .getState()
      .show(snapshot({ title: "另一部", url: "https://media.example/other.m3u8", routeParams: { source: "s2", id: "9", title: "另一部", episodeIndex: "0" } }));
    await flush();

    expect(useMiniPlayerStore.getState().session).toBe(2);
    expect(useMiniPlayerStore.getState().title).toBe("另一部");
    expect(mockSave).toHaveBeenCalledTimes(1);
    expect(mockSave.mock.calls[0][0]).toBe("src");
    expect(mockSave.mock.calls[0][1]).toBe("7");
    expect(mockSave.mock.calls[0][2].play_time).toBe(90);
  });

  it("setProgress() tracks position and duration but never moves the start position", () => {
    useMiniPlayerStore.getState().show(snapshot({ durationMillis: 0 }));
    useMiniPlayerStore.getState().setProgress(70_500, 2_400_000);
    const state = useMiniPlayerStore.getState();
    expect(state.positionMillis).toBe(70_500);
    expect(state.durationMillis).toBe(2_400_000);
    expect(state.startPositionMillis).toBe(65_000);
  });

  it("setProgress() keeps the known duration when the player reports none", () => {
    useMiniPlayerStore.getState().show(snapshot());
    useMiniPlayerStore.getState().setProgress(70_000, undefined);
    expect(useMiniPlayerStore.getState().durationMillis).toBe(2_400_000);
  });

  it("actions are no-ops while the bar is not active", async () => {
    const store = useMiniPlayerStore.getState();
    store.setProgress(5_000, 10_000);
    store.togglePlay();
    store.setPlaying(true);
    store.toggleCollapsed();
    await store.close();
    const state = useMiniPlayerStore.getState();
    expect(state.positionMillis).toBe(0);
    expect(state.isPlaying).toBe(false);
    expect(state.collapsed).toBe(false);
    expect(store.restore()).toBeNull();
    expect(mockSave).not.toHaveBeenCalled();
  });

  it("togglePlay() and toggleCollapsed() flip their flags", () => {
    useMiniPlayerStore.getState().show(snapshot());
    useMiniPlayerStore.getState().togglePlay();
    expect(useMiniPlayerStore.getState().isPlaying).toBe(false);
    useMiniPlayerStore.getState().togglePlay();
    expect(useMiniPlayerStore.getState().isPlaying).toBe(true);
    useMiniPlayerStore.getState().toggleCollapsed();
    expect(useMiniPlayerStore.getState().collapsed).toBe(true);
    useMiniPlayerStore.getState().toggleCollapsed();
    expect(useMiniPlayerStore.getState().collapsed).toBe(false);
  });

  it("close() hides the bar and writes the progress back into the existing play record", async () => {
    mockSettingsGet.mockResolvedValue({ introEndTime: 30_000, outroStartTime: 60_000, playbackRate: 1.25 });
    useMiniPlayerStore.getState().show(snapshot());
    useMiniPlayerStore.getState().setProgress(125_900, 2_400_000);
    await useMiniPlayerStore.getState().close();

    expect(useMiniPlayerStore.getState().active).toBe(false);
    expect(useMiniPlayerStore.getState().url).toBe("");
    expect(mockGet).toHaveBeenCalledWith("src", "7");
    expect(mockSave).toHaveBeenCalledTimes(1);
    expect(mockSave).toHaveBeenCalledWith("src", "7", {
      title: "山海",
      cover: "https://img.example/p.jpg",
      index: 3,
      total_episodes: 12,
      play_time: 125,
      total_time: 2400,
      source_name: "示例源",
      year: "2024",
      // 分类原样保留(成人分类过滤依赖它)
      type_name: "国产剧",
      // 片头片尾标记必须带上,否则 save() 会把它们清掉
      introEndTime: 30_000,
      outroStartTime: 60_000,
    });
  });

  it("close() does not recreate a play record that no longer exists", async () => {
    mockGet.mockResolvedValue(null);
    useMiniPlayerStore.getState().show(snapshot());
    await useMiniPlayerStore.getState().close();
    expect(useMiniPlayerStore.getState().active).toBe(false);
    expect(mockSave).not.toHaveBeenCalled();
  });

  it("close() still hides the bar when storage fails", async () => {
    mockGet.mockRejectedValue(new Error("storage down"));
    useMiniPlayerStore.getState().show(snapshot());
    await expect(useMiniPlayerStore.getState().close()).resolves.toBeUndefined();
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });

  it("restore() returns the /play params with the current position and hides the bar", async () => {
    useMiniPlayerStore.getState().show(snapshot());
    useMiniPlayerStore.getState().setProgress(301_700.6, 2_400_000);
    const params = useMiniPlayerStore.getState().restore();
    await flush();

    expect(params).toEqual({ source: "src", id: "7", title: "山海", episodeIndex: "2", position: "301700" });
    expect(useMiniPlayerStore.getState().active).toBe(false);
    expect(mockSave).toHaveBeenCalledTimes(1);
    expect(mockSave.mock.calls[0][2].play_time).toBe(301);
  });

  it("saves progress at most once per interval while playing", async () => {
    let now = 1_000_000;
    jest.spyOn(Date, "now").mockImplementation(() => now);
    useMiniPlayerStore.getState().show(snapshot());

    now += MINI_PLAYER_SAVE_INTERVAL_MS - 1;
    useMiniPlayerStore.getState().setProgress(70_000, 2_400_000);
    await flush();
    expect(mockSave).not.toHaveBeenCalled();

    now += 1;
    useMiniPlayerStore.getState().setProgress(80_000, 2_400_000);
    await flush();
    expect(mockSave).toHaveBeenCalledTimes(1);
    expect(mockSave.mock.calls[0][2].play_time).toBe(80);

    now += 500;
    useMiniPlayerStore.getState().setProgress(80_500, 2_400_000);
    await flush();
    expect(mockSave).toHaveBeenCalledTimes(1);
  });
});
