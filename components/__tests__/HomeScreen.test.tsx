// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";
import { Animated } from "react-native";

import HomeScreen from "../../app/index";

type MockHomeState = { loading: boolean; contentData: unknown[] };
let mockHomeState: MockHomeState;
const mockRefreshPlayRecords = jest.fn();

jest.mock("@/stores/homeStore", () => ({
  __esModule: true,
  default: () => ({
    categories: [{ title: "热门剧集", type: "tv", tag: "热门" }],
    selectedCategory: { title: "热门剧集", type: "tv", tag: "热门" },
    loadingMore: false,
    error: null,
    fetchInitialData: jest.fn(),
    loadMoreData: jest.fn(),
    selectCategory: jest.fn(),
    refreshPlayRecords: mockRefreshPlayRecords,
    clearError: jest.fn(),
    ...mockHomeState,
  }),
}));

jest.mock("@/stores/authStore", () => ({
  __esModule: true,
  default: () => ({ isLoggedIn: false, logout: jest.fn() }),
}));

jest.mock("@/stores/favoritesStore", () => ({
  __esModule: true,
  default: (selector: (state: { openDrawer: () => void }) => unknown) => selector({ openDrawer: jest.fn() }),
}));
jest.mock("@/components/UpdateBadge", () => ({ UpdateBadge: () => null }));
// 横幅在 TV 上不启用(这里的设备类型是 tv);整体 mock 掉,免得把设置 store 等依赖带进来
jest.mock("@/components/HomeHeroBanner", () => ({ HomeHeroBanner: () => null, useHeroBannerItems: () => [] }));

jest.mock("@/hooks/useApiConfig", () => ({
  useApiConfig: () => ({ isConfigured: true, needsConfiguration: false, isValidating: false, isValid: true, error: null }),
  getApiConfigErrorMessage: () => "",
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "tv", spacing: 8 }),
}));

jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ container: {}, center: {} }),
}));

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: jest.fn() }),
  useFocusEffect: jest.fn(),
}));

jest.mock("react-native-safe-area-context", () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));

jest.mock("@/services/api", () => ({ api: {} }));
jest.mock("@/components/VideoCard", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/StyledButton", () => ({ StyledButton: () => null }));
jest.mock("@/components/navigation/ResponsiveNavigation", () => ({ __esModule: true, default: () => null }));

jest.mock("@/components/CustomScrollView", () => {
  const { Text: MockText } = jest.requireActual("react-native");
  return {
    __esModule: true,
    default: ({ data, emptyMessage }: { data: unknown[]; emptyMessage: string }) => (
      <MockText>{data.length === 0 ? emptyMessage : "CONTENT"}</MockText>
    ),
  };
});

describe("HomeScreen content fade-in", () => {
  let tree: renderer.ReactTestRenderer;
  let timing: jest.SpyInstance;

  const show = (state: MockHomeState) => {
    mockHomeState = state;
    act(() => {
      if (tree) {
        tree.update(<HomeScreen />);
      } else {
        tree = renderer.create(<HomeScreen />);
      }
    });
  };

  const fadeInCalls = () => timing.mock.calls.filter(([, config]) => config.toValue === 1);

  beforeEach(() => {
    jest.useFakeTimers();
    timing = jest.spyOn(Animated, "timing");
  });

  afterEach(() => {
    act(() => tree.unmount());
    tree = undefined as unknown as renderer.ReactTestRenderer;
    timing.mockRestore();
    jest.useRealTimers();
  });

  it("fades the content area in when a category finishes loading with no items", () => {
    show({ loading: true, contentData: [] });
    expect(fadeInCalls()).toHaveLength(0);

    show({ loading: false, contentData: [] });

    // The empty message is rendered inside the faded container, so it is only visible once that fades in.
    expect(JSON.stringify(tree.toJSON())).toContain("该分类下暂无内容");
    expect(fadeInCalls()).toHaveLength(1);
  });

  it("fades the content area in when a category finishes loading with items", () => {
    show({ loading: true, contentData: [] });
    show({ loading: false, contentData: [{ id: "1", source: "douban", title: "A" }] });

    expect(JSON.stringify(tree.toJSON())).toContain("CONTENT");
    expect(fadeInCalls()).toHaveLength(1);
  });
});

describe("HomeScreen play-record refresh on focus", () => {
  it("does not leave a rejected refresh unhandled", async () => {
    const { useFocusEffect } = jest.requireMock("expo-router") as { useFocusEffect: jest.Mock };
    useFocusEffect.mockClear();
    const failure = mockRefreshPlayRecords.mockReturnValue(Promise.reject(new Error("play records unavailable")));
    const warn = jest.spyOn(console, "warn").mockImplementation(() => {});
    mockHomeState = { loading: false, contentData: [] };

    let screen: renderer.ReactTestRenderer;
    act(() => {
      screen = renderer.create(<HomeScreen />);
    });
    // The first focus effect registered by the screen is the play-record refresh.
    const onFocus = useFocusEffect.mock.calls[0][0] as () => void;
    onFocus();

    const settled = failure.mock.results[0].value as Promise<void>;
    await expect(settled).rejects.toThrow("play records unavailable");
    await Promise.resolve();
    expect(failure).toHaveBeenCalledTimes(1);
    expect(warn).toHaveBeenCalledTimes(1);

    act(() => screen.unmount());
    warn.mockRestore();
    mockRefreshPlayRecords.mockReset();
  });
});
