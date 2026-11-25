// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";

import DetailScreen from "../../app/detail";

const mockBack = jest.fn();
const mockPush = jest.fn();
const mockInit = jest.fn();
const mockAbort = jest.fn();
const mockLoadCandidates = jest.fn();
let mockParams: Record<string, string> = {};
let mockDeviceType = "mobile";
let mockVisibilityKey = "off||";
let mockDetail: Record<string, unknown> | null = null;

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush, back: mockBack, canGoBack: () => true }),
  useLocalSearchParams: () => mockParams,
}));
jest.mock("expo-linear-gradient", () => ({ LinearGradient: () => null }));
jest.mock("@expo/vector-icons", () => ({ FontAwesome: () => null }));
jest.mock("@/services/api", () => ({ api: { getImageSource: (uri: string) => ({ uri }) } }));
jest.mock("@/services/tmdb", () => ({ mergeTmdbMetadata: (detail: unknown) => detail }));
jest.mock("@/services/channelHealth", () => ({
  healthColor: () => "#fff",
  labelOf: () => "",
  probeChannels: jest.fn(),
}));
jest.mock("@/services/videoSourceSubscription", () => ({
  getSearchResultSourceKey: (item: { source: string; id: number }) => `${item.source}+${item.id}`,
}));
jest.mock("@/services/directPlay", () => ({
  resolveDirectPlayParams: jest.fn(async () => ({ episodeIndex: 0 })),
}));
jest.mock("@/stores/detailStore", () => ({
  __esModule: true,
  default: () => ({
    detail: mockDetail,
    searchResults: mockDetail ? [mockDetail] : [],
    loading: false,
    error: null,
    allSourcesLoaded: true,
    init: mockInit,
    setDetail: jest.fn(),
    abort: mockAbort,
    isFavorited: false,
    toggleFavorite: jest.fn(),
    tmdbMeta: null,
  }),
}));
jest.mock("@/services/relatedCandidates", () => ({
  loadVisibleRecommendationCandidates: (...args: unknown[]) => mockLoadCandidates(...args),
}));
jest.mock("@/hooks/useContentVisibilityKey", () => ({ useContentVisibilityKey: () => mockVisibilityKey }));
jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: mockDeviceType, spacing: 8 }),
}));
jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ container: {}, center: {}, safeContainer: {}, textMedium: {} }),
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
jest.mock("@/components/navigation/ResponsiveNavigation", () => ({
  __esModule: true,
  default: ({ children }: { children: any }) => children,
}));
jest.mock("@/components/navigation/ResponsiveHeader", () => ({ __esModule: true, default: () => null }));

const candidate = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
  id,
  source: "douban",
  title,
  poster: `https://img.example/${id}.jpg`,
  ...extra,
});

const flush = () => act(async () => {});

const mount = async () => {
  let tree!: renderer.ReactTestRenderer;
  await act(async () => {
    tree = renderer.create(<DetailScreen />);
  });
  await flush();
  return tree;
};

const relatedIds = (tree: renderer.ReactTestRenderer) =>
  tree.root
    .findAll((node) => typeof node.props.testID === "string" && node.props.testID.startsWith("detail-related-item-"))
    .map((node) => node.props.testID as string)
    // 宿主组件与复合组件各带一次 testID,去重后保持顺序
    .filter((value, index, all) => all.indexOf(value) === index);

const pressTestId = (tree: renderer.ReactTestRenderer, testID: string) =>
  act(async () => {
    tree.root.findAll((node) => node.props.testID === testID)[0].props.onPress();
  });

describe("detail screen related items", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockParams = { q: "星际探险 第二季", source: "src1", id: "7" };
    mockDeviceType = "mobile";
    mockVisibilityKey = "off||";
    mockDetail = {
      id: 7,
      title: "星际探险 第二季",
      poster: "https://img.example/current.jpg",
      episodes: ["https://media.example/1.m3u8", "https://media.example/2.m3u8"],
      source: "src1",
      source_name: "源一",
      year: "2024",
      type_name: "科幻片",
    };
    mockLoadCandidates.mockResolvedValue([
      candidate("g1", "别的科幻", { tags: ["科幻"] }),
      candidate("x1", "毫不相干", { type_name: "喜剧片" }),
      candidate("9", "星际探险 第三季", { source: "src2", year: "2025" }),
    ]);
  });

  it("shows the related row below the episode list, same series first", async () => {
    const tree = await mount();

    expect(relatedIds(tree)).toEqual(["detail-related-item-src2-9", "detail-related-item-douban-g1"]);
    const rendered = JSON.stringify(tree.toJSON());
    expect(rendered).toContain("相关推荐");
    expect(rendered).not.toContain("毫不相干");
    // 位置:在“播放列表”(集数)之后
    expect(rendered.indexOf("相关推荐")).toBeGreaterThan(rendered.indexOf("第 2 集"));
    expect(mockInit).toHaveBeenCalledTimes(1);
    expect(mockInit).toHaveBeenLastCalledWith("星际探险 第二季", "src1", "7");
  });

  it("opens a related item in place and walks back through the trail before leaving the page", async () => {
    const tree = await mount();

    // 片源条目:带 source + id
    await pressTestId(tree, "detail-related-item-src2-9");
    expect(mockInit).toHaveBeenLastCalledWith("星际探险 第三季", "src2", "9");
    // 豆瓣条目:只按标题找
    await pressTestId(tree, "detail-related-item-douban-g1");
    expect(mockInit).toHaveBeenLastCalledWith("别的科幻", undefined, undefined);
    expect(mockPush).not.toHaveBeenCalled();
    // 每次换内容之前先取消上一部还没完成的加载
    expect(mockAbort.mock.calls.length).toBeGreaterThanOrEqual(2);

    await pressTestId(tree, "detail-back");
    expect(mockInit).toHaveBeenLastCalledWith("星际探险 第三季", "src2", "9");
    await pressTestId(tree, "detail-back");
    expect(mockInit).toHaveBeenLastCalledWith("星际探险 第二季", "src1", "7");
    expect(mockBack).not.toHaveBeenCalled();

    await pressTestId(tree, "detail-back");
    expect(mockBack).toHaveBeenCalledTimes(1);
  });

  it("renders no related block when nothing in the pool is related", async () => {
    mockLoadCandidates.mockResolvedValue([candidate("x1", "毫不相干", { type_name: "喜剧片" })]);
    const tree = await mount();

    expect(relatedIds(tree)).toEqual([]);
    expect(JSON.stringify(tree.toJSON())).not.toContain("相关推荐");
  });

  it("drops candidates loaded under the previous visibility settings and reloads", async () => {
    const tree = await mount();
    expect(relatedIds(tree)).toHaveLength(2);
    expect(mockLoadCandidates).toHaveBeenCalledTimes(1);

    // 18+ 开关 / 片源模式变了:新的候选还没回来之前,旧候选不再显示
    let resolveNext!: (items: unknown[]) => void;
    mockLoadCandidates.mockReturnValue(new Promise((resolve) => (resolveNext = resolve)));
    mockVisibilityKey = "on||";
    await act(async () => {
      tree.update(<DetailScreen />);
    });
    expect(mockLoadCandidates).toHaveBeenCalledTimes(2);
    expect(relatedIds(tree)).toEqual([]);

    await act(async () => {
      resolveNext([candidate("g2", "新的科幻", { tags: ["科幻"] })]);
    });
    expect(relatedIds(tree)).toEqual(["detail-related-item-douban-g2"]);
  });

  it("keeps the TV detail page unchanged (no related block, no pool read)", async () => {
    mockDeviceType = "tv";
    const tree = await mount();

    expect(relatedIds(tree)).toEqual([]);
    expect(mockLoadCandidates).not.toHaveBeenCalled();
  });
});
