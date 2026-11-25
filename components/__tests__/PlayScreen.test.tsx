// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";

import DetailScreen from "../../app/detail";
import PlayScreen from "../../app/play";
import useDetailStore from "@/stores/detailStore";
import usePlayerStore from "@/stores/playerStore";
import { api } from "@/services/api";

const mockPush = jest.fn();
let mockParams: Record<string, string> = {};

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush, back: jest.fn() }),
  useLocalSearchParams: () => mockParams,
}));

jest.mock("expo-av", () => {
  const MockReact = jest.requireActual("react");
  return {
    Video: MockReact.forwardRef(() => null),
    ResizeMode: { CONTAIN: "contain" },
  };
});

jest.mock("expo-keep-awake", () => ({ useKeepAwake: jest.fn() }));
// 详情页的“相关推荐”会读本机候选池(AsyncStorage);这里是 TV 布局,不渲染那一块,只需要模块能加载
jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(async () => null),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));
jest.mock("react-native-toast-message", () => ({ show: jest.fn() }));
jest.mock("@expo/vector-icons", () => ({ FontAwesome: () => null }));

jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: { withTag: () => ({ debug: jest.fn(), error: jest.fn(), info: jest.fn(), warn: jest.fn() }) },
}));

jest.mock("@/services/api", () => ({
  api: {
    searchVideo: jest.fn(),
    searchVideos: jest.fn(),
    getVideoDetail: jest.fn(),
    getImageSource: (uri: string) => ({ uri }),
  },
}));

jest.mock("@/services/storage", () => ({
  FavoriteManager: { isFavorited: jest.fn(async () => false), toggle: jest.fn() },
  PlayRecordManager: { get: jest.fn(async () => null), save: jest.fn() },
  PlayerSettingsManager: { get: jest.fn(async () => null), save: jest.fn() },
}));

jest.mock("@/services/m3u8", () => ({
  getAdRangesFromM3U8: jest.fn(async () => []),
  getResolutionFromM3U8: jest.fn(async () => null),
}));

jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: { getState: () => ({ autoSkipAdsEnabled: false, videoSource: { enabledAll: true, sources: {} } }) },
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "tv", spacing: 8 }),
}));

jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ container: {}, center: {}, safeContainer: {}, textMedium: {} }),
}));

jest.mock("@/hooks/useTVRemoteHandler", () => ({
  useTVRemoteHandler: () => ({ onScreenPress: jest.fn() }),
}));

jest.mock("@/components/StyledButton", () => {
  const { Text: MockText } = jest.requireActual("react-native");
  return {
    StyledButton: ({ text, onPress, children }: { text?: string; onPress?: () => void; children?: any }) => (
      <MockText onPress={onPress}>{text ?? children}</MockText>
    ),
  };
});

jest.mock("@/components/VideoLoadingAnimation", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/PlayerControls", () => ({ PlayerControls: () => null }));
jest.mock("@/components/EpisodeSelectionModal", () => ({ EpisodeSelectionModal: () => null }));
jest.mock("@/components/SpeedSelectionModal", () => ({ SpeedSelectionModal: () => null }));
jest.mock("@/components/SeekingBar", () => ({ SeekingBar: () => null }));
jest.mock("@/components/navigation/ResponsiveNavigation", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/navigation/ResponsiveHeader", () => ({ __esModule: true, default: () => null }));

// The same show on two sources. The titles differ only in spacing, which the detail
// store's title matching accepts, so both end up in one source list.
const sourceA = {
  id: 1,
  title: "某剧 第二季",
  poster: "",
  episodes: ["https://a.example/1.m3u8", "https://a.example/2.m3u8"],
  source: "a",
  source_name: "A站",
  year: "2024",
};
const sourceB = {
  id: 2,
  title: "某剧第二季",
  poster: "",
  episodes: ["https://b.example/1.m3u8", "https://b.example/2.m3u8"],
  source: "b",
  source_name: "B站",
  year: "2024",
};

const flush = () => act(async () => {});

const press = (tree: renderer.ReactTestRenderer, label: string) =>
  act(async () => {
    await tree.root.find((node) => (node.type as any) === "Text" && node.props.children === label).props.onPress();
  });

describe("PlayScreen opened from the detail screen", () => {
  let playTree: renderer.ReactTestRenderer | undefined;

  // Loads both sources on the detail screen, starts episode 1 of source A there, and
  // mounts the player with exactly the route params the detail screen pushed.
  const openPlayerFromDetail = async () => {
    mockParams = { q: sourceA.title, source: "a", id: "1" };
    let detailTree!: renderer.ReactTestRenderer;
    await act(async () => {
      detailTree = renderer.create(<DetailScreen />);
    });
    await flush();
    expect(useDetailStore.getState().searchResults.map((result) => result.source)).toEqual(["a", "b"]);

    await press(detailTree, "第 1 集");
    expect(mockPush).toHaveBeenCalledTimes(1);
    const pushed = mockPush.mock.calls[0][0] as { pathname: string; params: Record<string, string> };
    expect(pushed.pathname).toBe("/play");
    act(() => detailTree.unmount());

    mockParams = pushed.params;
    await act(async () => {
      playTree = renderer.create(<PlayScreen />);
    });
    await flush();
    expect(usePlayerStore.getState().episodes[0]?.url).toBe("https://a.example/1.m3u8");
  };

  beforeEach(() => {
    mockPush.mockClear();
    (api.searchVideo as jest.Mock).mockImplementation(async (_q: string, source: string) => ({
      results: [source === "a" ? sourceA : sourceB],
    }));
    (api.searchVideos as jest.Mock).mockResolvedValue({ results: [sourceA, sourceB] });
    (api.getVideoDetail as jest.Mock).mockImplementation(async (source: string) => {
      const found = source === "a" ? sourceA : sourceB;
      return { id: String(found.id), title: found.title, poster: "", source, source_name: found.source_name };
    });
  });

  afterEach(() => {
    act(() => playTree?.unmount());
    playTree = undefined;
  });

  it("stays on a source picked in the player when its title is spaced differently", async () => {
    await openPlayerFromDetail();

    act(() => usePlayerStore.getState().setShowSourceModal(true));
    await press(playTree!, "B站");
    await flush();

    expect(useDetailStore.getState().detail?.source).toBe("b");
    expect(usePlayerStore.getState().episodes[0]?.url).toBe("https://b.example/1.m3u8");
  });

  it("keeps the fallback source after a playback error when its title is spaced differently", async () => {
    await openPlayerFromDetail();

    await act(async () => {
      await usePlayerStore.getState().handleVideoError("network", "https://a.example/1.m3u8");
    });
    await flush();

    expect(useDetailStore.getState().detail?.source).toBe("b");
    expect(usePlayerStore.getState().episodes[0]?.url).toBe("https://b.example/1.m3u8");
  });
});
