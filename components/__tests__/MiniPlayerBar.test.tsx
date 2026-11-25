import React from "react";
import renderer, { act } from "react-test-renderer";
import { AppState, Platform } from "react-native";

import { MiniPlayerBar, formatMiniPlayerTime, getMiniPlayerProgress } from "../MiniPlayerBar";
import { MiniPlayerSnapshot, resetMiniPlayerStoreForTests, useMiniPlayerStore } from "@/stores/miniPlayerStore";
import { PlayRecordManager } from "@/services/storage";

const mockPush = jest.fn();
let mockPathname = "/";
let mockDeviceType = "mobile";
const mockSetPositionAsync = jest.fn(async (_position: number) => undefined);
const mockPlayAsync = jest.fn(async () => undefined);
const mockToastShow = jest.fn();

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush }),
  usePathname: () => mockPathname,
}));
jest.mock("expo-av", () => {
  const MockReact = jest.requireActual("react");
  const Video = MockReact.forwardRef((_props: any, ref: any) => {
    MockReact.useImperativeHandle(ref, () => ({
      setPositionAsync: mockSetPositionAsync,
      playAsync: mockPlayAsync,
    }));
    return null;
  });
  return { Video, ResizeMode: { COVER: "cover", CONTAIN: "contain" } };
});
jest.mock("lucide-react-native", () => ({
  ChevronDown: () => null,
  ChevronUp: () => null,
  Pause: () => null,
  Play: () => null,
  X: () => null,
}));
jest.mock("react-native-toast-message", () => ({
  __esModule: true,
  default: { show: (...args: unknown[]) => mockToastShow(...args) },
}));
jest.mock("@/services/storage", () => ({
  PlayRecordManager: {
    get: jest.fn(async () => ({
      title: "山海",
      cover: "",
      index: 3,
      total_episodes: 12,
      play_time: 65,
      total_time: 2400,
      source_name: "示例源",
      save_time: 1,
    })),
    save: jest.fn(async () => undefined),
    remove: jest.fn(async () => undefined),
  },
  PlayerSettingsManager: { get: jest.fn(async () => null) },
}));
jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: mockDeviceType }),
}));
jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: { withTag: () => ({ debug: jest.fn(), error: jest.fn(), info: jest.fn(), warn: jest.fn() }) },
}));

const snapshot = (overrides: Partial<MiniPlayerSnapshot> = {}): MiniPlayerSnapshot => ({
  title: "山海",
  episodeLabel: "第 3 集",
  url: "https://media.example/ep3.m3u8",
  positionMillis: 65_000,
  durationMillis: 2_400_000,
  isPlaying: true,
  playbackRate: 1,
  routeParams: { source: "src", id: "7", title: "山海", episodeIndex: "2" },
  ...overrides,
});

describe("MiniPlayerBar", () => {
  let tree: renderer.ReactTestRenderer;
  let appStateListener: ((status: string) => void) | undefined;
  const originalIsTV = Platform.isTV;

  const all = (testID: string) => tree.root.findAllByProps({ testID });
  const has = (testID: string) => all(testID).length > 0;
  const first = (testID: string) => all(testID)[0];
  const textOf = (testID: string) => [first(testID).props.children].flat(Infinity).join("");
  const video = () => first("mini-player-video");
  const press = async (testID: string) => {
    await act(async () => {
      first(testID).props.onPress();
    });
  };
  const show = (overrides: Partial<MiniPlayerSnapshot> = {}) =>
    act(() => {
      useMiniPlayerStore.getState().show(snapshot(overrides));
    });
  const load = async () => {
    await act(async () => {
      await video().props.onLoad();
    });
  };
  const status = (positionMillis: number, extra: Record<string, unknown> = {}) =>
    act(() => {
      video().props.onPlaybackStatusUpdate({ isLoaded: true, positionMillis, durationMillis: 2_400_000, ...extra });
    });

  beforeEach(() => {
    jest.clearAllMocks();
    resetMiniPlayerStoreForTests();
    mockPathname = "/";
    mockDeviceType = "mobile";
    appStateListener = undefined;
    jest.spyOn(AppState, "addEventListener").mockImplementation(((_type: string, listener: (status: string) => void) => {
      appStateListener = listener;
      return { remove: jest.fn() };
    }) as any);
    act(() => {
      tree = renderer.create(<MiniPlayerBar />);
    });
  });

  afterEach(() => {
    act(() => tree.unmount());
    Object.defineProperty(Platform, "isTV", { value: originalIsTV, configurable: true });
    jest.restoreAllMocks();
  });

  it("renders nothing until a video is handed over", () => {
    expect(tree.toJSON()).toBeNull();
  });

  it("shows the title, episode, time and progress of the handed-over video", () => {
    show({ positionMillis: 600_000 });
    expect(textOf("mini-player-title")).toBe("山海 · 第 3 集");
    expect(textOf("mini-player-time")).toBe("10:00 / 40:00");
    expect(first("mini-player-progress").props.style).toEqual(
      expect.arrayContaining([expect.objectContaining({ width: "25%" })])
    );
    expect(video().props.source).toEqual({ uri: "https://media.example/ep3.m3u8" });
    expect(video().props.shouldPlay).toBe(true);
    expect(has("mini-player-toggle")).toBe(true);
    expect(has("mini-player-close")).toBe(true);
  });

  it("omits the episode label for single-episode titles", () => {
    show({ episodeLabel: "" });
    expect(textOf("mini-player-title")).toBe("山海");
  });

  it("resumes from the handed-over position and ignores progress reported before that seek", async () => {
    show();
    // 定位完成前播放器报的进度是 0,不能覆盖交接进度
    status(0);
    expect(useMiniPlayerStore.getState().positionMillis).toBe(65_000);

    await load();
    expect(mockSetPositionAsync).toHaveBeenCalledWith(65_000);
    expect(mockPlayAsync).toHaveBeenCalledTimes(1);

    status(70_000);
    expect(useMiniPlayerStore.getState().positionMillis).toBe(70_000);
    expect(textOf("mini-player-time")).toBe("01:10 / 40:00");
  });

  it("toggles play and pause", async () => {
    show();
    expect(first("mini-player-toggle").props.accessibilityLabel).toBe("暂停");
    await press("mini-player-toggle");
    expect(video().props.shouldPlay).toBe(false);
    expect(first("mini-player-toggle").props.accessibilityLabel).toBe("播放");
    await press("mini-player-toggle");
    expect(video().props.shouldPlay).toBe(true);
  });

  it("collapses to a capsule that keeps the title, progress and the same player, then expands again", async () => {
    show();
    await load();
    const playerBefore = video().instance;
    await press("mini-player-collapse");

    expect(has("mini-player-title")).toBe(true);
    expect(has("mini-player-progress")).toBe(true);
    expect(has("mini-player-time")).toBe(false);
    expect(has("mini-player-toggle")).toBe(false);
    expect(has("mini-player-close")).toBe(false);
    expect(first("mini-player-collapse").props.accessibilityLabel).toBe("展开迷你播放器");
    // 播放器没有被卸载重建:声音不断,也不会重新定位
    expect(video().instance).toBe(playerBefore);
    expect(video().props.shouldPlay).toBe(true);
    expect(mockSetPositionAsync).toHaveBeenCalledTimes(1);

    await press("mini-player-collapse");
    expect(has("mini-player-time")).toBe(true);
    expect(has("mini-player-close")).toBe(true);
    expect(mockSetPositionAsync).toHaveBeenCalledTimes(1);
  });

  it("close stops playback, hides the bar and saves the play record", async () => {
    show();
    await load();
    status(125_000);
    await press("mini-player-close");

    expect(tree.toJSON()).toBeNull();
    expect(useMiniPlayerStore.getState().active).toBe(false);
    expect(PlayRecordManager.save).toHaveBeenCalledTimes(1);
    expect((PlayRecordManager.save as jest.Mock).mock.calls[0][2]).toEqual(
      expect.objectContaining({ play_time: 125, total_time: 2400, index: 3 })
    );
    expect(mockPush).not.toHaveBeenCalled();
  });

  it("tapping the bar returns to the full-screen player at the current position", async () => {
    show();
    await load();
    status(301_500);
    await press("mini-player-body");

    expect(mockPush).toHaveBeenCalledTimes(1);
    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: { source: "src", id: "7", title: "山海", episodeIndex: "2", position: "301500" },
    });
    expect(tree.toJSON()).toBeNull();
  });

  it("closes itself when the video finishes", async () => {
    show();
    await load();
    await act(async () => {
      video().props.onPlaybackStatusUpdate({
        isLoaded: true,
        positionMillis: 2_400_000,
        durationMillis: 2_400_000,
        didJustFinish: true,
      });
    });
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });

  it("closes with a message when playback fails", async () => {
    show();
    await act(async () => {
      video().props.onError("boom");
    });
    expect(useMiniPlayerStore.getState().active).toBe(false);
    expect(mockToastShow).toHaveBeenCalledWith(expect.objectContaining({ type: "error" }));
  });

  it("pauses when the app goes to the background", () => {
    show();
    act(() => appStateListener?.("background"));
    expect(useMiniPlayerStore.getState().isPlaying).toBe(false);
    expect(video().props.shouldPlay).toBe(false);
  });

  it("closes when the live page opens so two players never sound at once", async () => {
    show();
    mockPathname = "/live";
    await act(async () => {
      tree.update(<MiniPlayerBar />);
    });
    expect(useMiniPlayerStore.getState().active).toBe(false);
  });

  it("sits above the bottom tabs on phones and in the bottom-right corner on tablets", () => {
    show();
    expect(first("mini-player-host").props.style).toEqual(
      expect.arrayContaining([expect.objectContaining({ left: 12, right: 12, bottom: 96 })])
    );
    mockDeviceType = "tablet";
    act(() => tree.update(<MiniPlayerBar />));
    expect(first("mini-player-host").props.style).toEqual(
      expect.arrayContaining([expect.objectContaining({ right: 24, bottom: 24 })])
    );
  });

  it("is not rendered on TV layouts", () => {
    mockDeviceType = "tv";
    act(() => tree.update(<MiniPlayerBar />));
    show();
    expect(tree.toJSON()).toBeNull();
  });

  it("is not rendered on the TV platform", () => {
    Object.defineProperty(Platform, "isTV", { value: true, configurable: true });
    act(() => tree.update(<MiniPlayerBar />));
    show();
    expect(tree.toJSON()).toBeNull();
  });

  it("formats time and progress", () => {
    expect(formatMiniPlayerTime(0)).toBe("00:00");
    expect(formatMiniPlayerTime(65_999)).toBe("01:05");
    expect(formatMiniPlayerTime(3_725_000)).toBe("1:02:05");
    expect(formatMiniPlayerTime(Number.NaN)).toBe("00:00");
    expect(getMiniPlayerProgress(30, 120)).toBe(0.25);
    expect(getMiniPlayerProgress(500, 120)).toBe(1);
    expect(getMiniPlayerProgress(10, 0)).toBe(0);
  });
});
