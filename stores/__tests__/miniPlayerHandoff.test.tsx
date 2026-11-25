import React from "react";
import renderer, { act } from "react-test-renderer";
import { Platform } from "react-native";

import { buildMiniPlayerSnapshot, useMiniPlayerHandoff } from "@/hooks/useMiniPlayerHandoff";
import { resetMiniPlayerStoreForTests, useMiniPlayerStore } from "@/stores/miniPlayerStore";

let mockPlayerState: any = {};
let mockDetail: any = null;

jest.mock("@/stores/playerStore", () => ({
  __esModule: true,
  default: { getState: () => mockPlayerState },
}));
jest.mock("@/stores/detailStore", () => ({
  __esModule: true,
  default: { getState: () => ({ detail: mockDetail }) },
}));
jest.mock("@/services/storage", () => ({
  PlayRecordManager: { get: jest.fn(async () => null), save: jest.fn(async () => undefined) },
  PlayerSettingsManager: { get: jest.fn(async () => null) },
}));
jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: { withTag: () => ({ debug: jest.fn(), error: jest.fn(), info: jest.fn(), warn: jest.fn() }) },
}));

const playingState = (overrides: Record<string, unknown> = {}) => ({
  status: { isLoaded: true, isPlaying: true, positionMillis: 65_000, durationMillis: 2_400_000 },
  episodes: [
    { url: "https://media.example/ep1.m3u8", title: "第 1 集" },
    { url: "https://media.example/ep2.m3u8", title: "第 2 集" },
  ],
  currentEpisodeIndex: 1,
  playbackRate: 1.5,
  ...overrides,
});

const Probe = ({ deviceType }: { deviceType: string }) => {
  useMiniPlayerHandoff(deviceType);
  return null;
};

describe("mini player handoff", () => {
  const originalIsTV = Platform.isTV;

  beforeEach(() => {
    resetMiniPlayerStoreForTests();
    mockPlayerState = playingState();
    mockDetail = { title: "山海", source: "src", id: 7 };
  });

  afterEach(() => {
    Object.defineProperty(Platform, "isTV", { value: originalIsTV, configurable: true });
  });

  it("builds a snapshot of the episode that is playing", () => {
    expect(buildMiniPlayerSnapshot()).toEqual({
      title: "山海",
      episodeLabel: "第 2 集",
      url: "https://media.example/ep2.m3u8",
      positionMillis: 65_000,
      durationMillis: 2_400_000,
      isPlaying: true,
      playbackRate: 1.5,
      routeParams: { source: "src", id: "7", title: "山海", episodeIndex: "1" },
    });
  });

  it("has no episode label for a single-episode title", () => {
    mockPlayerState = playingState({
      episodes: [{ url: "https://media.example/movie.m3u8", title: "第 1 集" }],
      currentEpisodeIndex: 0,
    });
    expect(buildMiniPlayerSnapshot()?.episodeLabel).toBe("");
  });

  it.each([
    ["paused", { status: { isLoaded: true, isPlaying: false, positionMillis: 65_000, durationMillis: 2_400_000 } }],
    ["still loading", { status: { isLoaded: false } }],
    ["no status yet", { status: null }],
    ["just finished", { status: { isLoaded: true, isPlaying: true, positionMillis: 2_400_000, durationMillis: 2_400_000, didJustFinish: true } }],
    ["within the last seconds", { status: { isLoaded: true, isPlaying: true, positionMillis: 2_398_000, durationMillis: 2_400_000 } }],
    ["no episode url", { episodes: [], currentEpisodeIndex: 0 }],
  ])("does not hand over when %s", (_name, overrides) => {
    mockPlayerState = playingState(overrides);
    expect(buildMiniPlayerSnapshot()).toBeNull();
  });

  it("does not hand over without a detail", () => {
    mockDetail = null;
    expect(buildMiniPlayerSnapshot()).toBeNull();
  });

  it("closes the mini bar when the play screen opens and hands over when it is left", async () => {
    act(() => {
      useMiniPlayerStore.getState().show({
        title: "旧片",
        url: "https://media.example/old.m3u8",
        positionMillis: 1_000,
        durationMillis: 10_000,
        routeParams: { source: "old", id: "1", title: "旧片", episodeIndex: "0" },
      });
    });

    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<Probe deviceType="mobile" />);
    });
    expect(useMiniPlayerStore.getState().active).toBe(false);

    act(() => tree.unmount());
    const state = useMiniPlayerStore.getState();
    expect(state.active).toBe(true);
    expect(state.title).toBe("山海");
    expect(state.url).toBe("https://media.example/ep2.m3u8");
    expect(state.startPositionMillis).toBe(65_000);
  });

  it("takes the snapshot before a later effect's cleanup (the play screen's reset) clears the player", async () => {
    // 播放页里 useMiniPlayerHandoff 声明在调用 reset() 的 effect 之前;卸载时清理函数按声明顺序执行
    const ScreenLike = () => {
      useMiniPlayerHandoff("mobile");
      React.useEffect(
        () => () => {
          mockPlayerState = playingState({ status: null, episodes: [], currentEpisodeIndex: 0 });
        },
        []
      );
      return null;
    };
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<ScreenLike />);
    });
    act(() => tree.unmount());
    expect(mockPlayerState.status).toBeNull();
    expect(useMiniPlayerStore.getState().active).toBe(true);
    expect(useMiniPlayerStore.getState().positionMillis).toBe(65_000);
  });

  it("leaves the mini bar off when the video was paused on leaving", async () => {
    mockPlayerState = playingState({
      status: { isLoaded: true, isPlaying: false, positionMillis: 65_000, durationMillis: 2_400_000 },
    });
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<Probe deviceType="tablet" />);
    });
    act(() => tree.unmount());
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });

  it("does nothing on TV layouts", async () => {
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<Probe deviceType="tv" />);
    });
    act(() => tree.unmount());
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });

  it("does nothing on the TV platform", async () => {
    Object.defineProperty(Platform, "isTV", { value: true, configurable: true });
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<Probe deviceType="mobile" />);
    });
    act(() => tree.unmount());
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });
});
