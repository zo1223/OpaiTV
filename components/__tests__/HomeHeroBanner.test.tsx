import React from "react";
import renderer, { act } from "react-test-renderer";

import { HomeHeroBanner, useHeroBannerItems } from "../HomeHeroBanner";
import type { HeroBannerItem } from "@/services/heroBanner";

const mockPush = jest.fn();
let mockFocusCleanup: (() => void) | void;
const mockResolveDirectPlayParams = jest.fn();
const mockComputeLocalRecommendations = jest.fn();
let mockSettings: Record<string, unknown>;

jest.mock("expo-router", () => {
  const MockReact = jest.requireActual("react");
  return {
    useRouter: () => ({ push: mockPush }),
    // 挂载即视为聚焦;测试里调用 mockFocusCleanup() 模拟页面失焦
    useFocusEffect: (callback: () => (() => void) | void) => {
      MockReact.useEffect(() => {
        mockFocusCleanup = callback();
      }, [callback]);
    },
  };
});

jest.mock("expo-linear-gradient", () => ({ LinearGradient: () => null }));
jest.mock("lucide-react-native", () => ({ Play: () => null }));
jest.mock("@/services/api", () => ({ api: { getImageSource: (uri: string) => ({ uri }) } }));
jest.mock("@/services/directPlay", () => ({
  resolveDirectPlayParams: (...args: unknown[]) => mockResolveDirectPlayParams(...args),
}));
jest.mock("@/stores/homeStore", () => ({
  computeLocalRecommendations: (...args: unknown[]) => mockComputeLocalRecommendations(...args),
}));
jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: (selector: (state: Record<string, unknown>) => unknown) => selector(mockSettings),
}));

const items: HeroBannerItem[] = [
  { id: "1", source: "src1", title: "第一部", poster: "https://img.example/1.jpg", year: "2024", rate: "8.1" },
  { id: "2", source: "douban", title: "第二部", poster: "https://img.example/2.jpg", desc: "第二部的简介" },
  { id: "3", source: "src2", title: "第三部", poster: "https://img.example/3.jpg" },
];

const textOf = (tree: renderer.ReactTestRenderer, testID: string) => {
  const node = tree.root.findAll((instance) => instance.props.testID === testID && typeof instance.type === "string")[0];
  return node ? node.children.join("") : null;
};
const byTestId = (tree: renderer.ReactTestRenderer, testID: string) =>
  tree.root.findAll((instance) => instance.props.testID === testID && typeof instance.props.onPress === "function")[0];

describe("HomeHeroBanner", () => {
  let tree: renderer.ReactTestRenderer;

  beforeEach(() => {
    jest.useFakeTimers();
    mockPush.mockClear();
    mockResolveDirectPlayParams.mockReset();
    mockFocusCleanup = undefined;
  });

  afterEach(() => {
    act(() => tree?.unmount());
    jest.useRealTimers();
  });

  const render = (list: HeroBannerItem[]) => {
    act(() => {
      tree = renderer.create(<HomeHeroBanner items={list} />);
    });
  };

  it("renders nothing without items", () => {
    render([]);
    expect(tree.toJSON()).toBeNull();
  });

  it("shows the title and a meta line, or the synopsis when the item has one", () => {
    render(items);
    expect(textOf(tree, "hero-title")).toBe("第一部");
    expect(textOf(tree, "hero-meta")).toBe("2024 · ★ 8.1");
    expect(textOf(tree, "hero-desc")).toBeNull();

    act(() => byTestId(tree, "hero-dot-1").props.onPress());
    expect(textOf(tree, "hero-title")).toBe("第二部");
    expect(textOf(tree, "hero-desc")).toBe("第二部的简介");
  });

  it("starts playback directly through the shared direct-play path", async () => {
    mockResolveDirectPlayParams.mockResolvedValue({
      source: "src1",
      id: "1",
      title: "第一部",
      episodeIndex: 2,
      position: 61000,
    });
    render(items);

    await act(async () => {
      byTestId(tree, "hero-play").props.onPress();
    });

    expect(mockResolveDirectPlayParams).toHaveBeenCalledWith("src1", "1", "第一部");
    expect(mockPush).toHaveBeenCalledTimes(1);
    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: { source: "src1", id: "1", title: "第一部", episodeIndex: 2, position: 61000 },
    });
  });

  it("opens the detail page from the secondary button", () => {
    render(items);
    act(() => byTestId(tree, "hero-detail").props.onPress());
    expect(mockPush).toHaveBeenCalledWith({ pathname: "/detail", params: { source: "src1", id: "1", q: "第一部" } });

    act(() => byTestId(tree, "hero-dot-1").props.onPress());
    act(() => byTestId(tree, "hero-detail").props.onPress());
    expect(mockPush).toHaveBeenLastCalledWith({ pathname: "/detail", params: { q: "第二部" } });
  });

  it("rotates every six seconds and wraps around", () => {
    render(items);
    expect(textOf(tree, "hero-title")).toBe("第一部");
    act(() => {
      jest.advanceTimersByTime(5900);
    });
    expect(textOf(tree, "hero-title")).toBe("第一部");
    act(() => {
      jest.advanceTimersByTime(200);
    });
    expect(textOf(tree, "hero-title")).toBe("第二部");
    act(() => {
      jest.advanceTimersByTime(12000);
    });
    expect(textOf(tree, "hero-title")).toBe("第一部");
  });

  it("does not rotate or show dots with a single item", () => {
    render([items[0]]);
    expect(byTestId(tree, "hero-dot-0")).toBeUndefined();
    act(() => {
      jest.advanceTimersByTime(60000);
    });
    expect(textOf(tree, "hero-title")).toBe("第一部");
  });

  it("stops rotating while the page is not focused and when unmounted", () => {
    render(items);
    act(() => {
      (mockFocusCleanup as () => void)();
    });
    act(() => {
      jest.advanceTimersByTime(30000);
    });
    expect(textOf(tree, "hero-title")).toBe("第一部");

    act(() => tree.unmount());
    expect(jest.getTimerCount()).toBe(0);
    render(items);
  });
});

describe("useHeroBannerItems", () => {
  let latest: HeroBannerItem[] = [];
  const Probe = ({ home, enabled }: { home: Record<string, unknown>[]; enabled: boolean }) => {
    latest = useHeroBannerItems(home, enabled);
    return null;
  };
  const home = [{ id: "h1", source: "douban", title: "首页一", poster: "https://img.example/h1.jpg" }];
  const recommended = [{ id: "r1", source: "src1", title: "推荐一", poster: "https://img.example/r1.jpg", reason: "你收藏了还没看" }];

  beforeEach(() => {
    latest = [];
    mockComputeLocalRecommendations.mockReset();
    mockSettings = { recommendationsEnabled: true, showAdultContent: false, sourceModes: {}, subscribedVideoSources: [] };
  });

  it("puts the local recommendations before the loaded home items", async () => {
    mockComputeLocalRecommendations.mockResolvedValue(recommended);
    let probe!: renderer.ReactTestRenderer;
    await act(async () => {
      probe = renderer.create(<Probe home={home} enabled />);
    });
    expect(latest.map((entry) => entry.title)).toEqual(["推荐一", "首页一"]);
    act(() => probe.unmount());
  });

  it("does not compute recommendations when the switch is off or the banner is disabled", async () => {
    mockComputeLocalRecommendations.mockResolvedValue(recommended);
    mockSettings = { ...mockSettings, recommendationsEnabled: false };
    let probe!: renderer.ReactTestRenderer;
    await act(async () => {
      probe = renderer.create(<Probe home={home} enabled />);
    });
    expect(latest.map((entry) => entry.title)).toEqual(["首页一"]);

    await act(async () => {
      probe.update(<Probe home={home} enabled={false} />);
    });
    expect(latest).toEqual([]);
    expect(mockComputeLocalRecommendations).not.toHaveBeenCalled();
    act(() => probe.unmount());
  });

  it("drops recommendations computed under the previous 18+ / source-mode state until they are recomputed", async () => {
    mockComputeLocalRecommendations.mockResolvedValue(recommended);
    let probe!: renderer.ReactTestRenderer;
    await act(async () => {
      probe = renderer.create(<Probe home={home} enabled />);
    });
    expect(latest.map((entry) => entry.title)).toEqual(["推荐一", "首页一"]);

    // 开关变了:重算的结果还没回来之前,旧结果不能继续显示
    let resolveNext!: (value: unknown[]) => void;
    mockComputeLocalRecommendations.mockReturnValue(new Promise((resolve) => (resolveNext = resolve)));
    mockSettings = { ...mockSettings, showAdultContent: true };
    await act(async () => {
      probe.update(<Probe home={home} enabled />);
    });
    expect(latest.map((entry) => entry.title)).toEqual(["首页一"]);

    await act(async () => {
      resolveNext([]);
    });
    expect(latest.map((entry) => entry.title)).toEqual(["首页一"]);
    expect(mockComputeLocalRecommendations).toHaveBeenCalledTimes(2);
    act(() => probe.unmount());
  });
});
