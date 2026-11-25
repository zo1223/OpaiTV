// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text, TextInput } from "react-native";

import SearchScreen from "../../app/search";

type Update = (snapshot: any) => void;
const mockRuns: { term: string; onUpdate: Update; isCancelled: () => boolean; finish: () => void }[] = [];
const mockCardProps: any[] = [];

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(async () => null),
  setItem: jest.fn(async () => {}),
  removeItem: jest.fn(async () => {}),
}));

jest.mock("@/services/api", () => ({
  api: {
    searchVideos: jest.fn(async () => ({ results: [] })),
    searchVideosIncremental: (term: string, onUpdate: Update, isCancelled: () => boolean) =>
      new Promise<void>((resolve) => {
        mockRuns.push({ term, onUpdate, isCancelled, finish: resolve });
      }),
    isAdultItemHidden: () => false,
  },
}));

jest.mock("expo-router", () => ({ useRouter: () => ({ push: jest.fn() }) }));
jest.mock("@/hooks/useResponsiveLayout", () => ({ useResponsiveLayout: () => ({ deviceType: "tv", spacing: 8 }) }));
jest.mock("@/utils/ResponsiveStyles", () => ({ getCommonResponsiveStyles: () => ({ container: {}, center: {} }) }));
jest.mock("@/utils/DeviceUtils", () => ({ DeviceUtils: { getMinTouchTargetSize: () => 44 } }));
jest.mock("@/stores/remoteControlStore", () => ({
  useRemoteControlStore: () => ({ showModal: jest.fn(), lastMessage: null, targetPage: null, clearMessage: jest.fn() }),
}));
jest.mock("@/stores/settingsStore", () => ({ useSettingsStore: () => ({ remoteInputEnabled: false }) }));
jest.mock("lucide-react-native", () => ({ Search: () => null, QrCode: () => null }));
jest.mock("@/components/RemoteControlModal", () => ({ RemoteControlModal: () => null }));
jest.mock("@/components/StyledButton", () => ({ StyledButton: () => null }));
jest.mock("@/components/navigation/ResponsiveNavigation", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/navigation/ResponsiveHeader", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/VideoLoadingAnimation", () => {
  const { Text: MockText } = jest.requireActual("react-native");
  return { __esModule: true, default: () => <MockText>LOADING</MockText> };
});
jest.mock("@/components/VideoCard", () => {
  const { Text: MockText } = jest.requireActual("react-native");
  return {
    __esModule: true,
    default: (props: { title: string }) => {
      mockCardProps.push(props);
      return <MockText>{props.title}</MockText>;
    },
  };
});
jest.mock("@/components/CustomScrollView", () => {
  const { View: MockView } = jest.requireActual("react-native");
  return {
    __esModule: true,
    default: ({ data, renderItem }: { data: any[]; renderItem: (info: { item: any; index: number }) => any }) => (
      <MockView>
        {data.map((item, index) => (
          <MockView key={index}>{renderItem({ item, index })}</MockView>
        ))}
      </MockView>
    ),
  };
});

const item = (title: string, source: string) => ({ id: 1, source, title, poster: "", year: "2024", source_name: source });

const snapshot = (statuses: [string, string, number][], results: any[]) => ({
  progress: statuses.map(([name, status, count]) => ({ key: name, name, status, count })),
  finished: statuses.filter(([, status]) => status !== "searching" && status !== "pending").length,
  total: statuses.length,
  results,
  done: statuses.every(([, status]) => status !== "searching" && status !== "pending"),
});

describe("SearchScreen incremental cross-source search", () => {
  let tree: renderer.ReactTestRenderer;
  const texts = () =>
    tree.root.findAllByType(Text).map((node) => [node.props.children].flat().join(""));

  const submit = (term: string) => {
    act(() => {
      tree.root.findByType(TextInput).props.onChangeText(term);
    });
    act(() => {
      tree.root.findByType(TextInput).props.onSubmitEditing();
    });
  };

  beforeEach(async () => {
    mockRuns.length = 0;
    mockCardProps.length = 0;
    await act(async () => {
      tree = renderer.create(<SearchScreen />);
    });
  });

  it("shows each source's results as soon as that source answers, with per-source progress", async () => {
    submit("三体");
    expect(texts()).toContain("LOADING");

    await act(async () => {
      mockRuns[0].onUpdate(snapshot([["A源", "searching", 0], ["B源", "searching", 0]], []));
    });
    expect(texts()).toEqual(expect.arrayContaining(["正在搜索 0/2 个源，共 0 条", "A源 搜索中", "B源 搜索中", "LOADING"]));

    // B 源先回来:结果立刻显示,A 源还在搜
    await act(async () => {
      mockRuns[0].onUpdate(snapshot([["A源", "searching", 0], ["B源", "done", 1]], [item("三体", "B源")]));
    });
    expect(texts()).toEqual(expect.arrayContaining(["正在搜索 1/2 个源，共 1 条", "A源 搜索中", "B源 1 条", "三体"]));
    expect(texts()).not.toContain("LOADING");

    await act(async () => {
      mockRuns[0].onUpdate(
        snapshot([["A源", "failed", 0], ["B源", "done", 1]], [item("三体", "B源")])
      );
      mockRuns[0].finish();
    });
    expect(texts()).toEqual(expect.arrayContaining(["已搜完 2/2 个源，共 1 条", "A源 失败", "三体"]));
  });

  it("renders search results as direct-play cards", async () => {
    submit("三体");
    await act(async () => {
      mockRuns[0].onUpdate(snapshot([["A源", "done", 1]], [item("三体", "A源")]));
      mockRuns[0].finish();
    });
    expect(mockCardProps[mockCardProps.length - 1]).toMatchObject({ title: "三体", directPlay: true });
  });

  it("ignores late updates from a superseded search", async () => {
    submit("旧词");
    submit("新词");
    expect(mockRuns[0].isCancelled()).toBe(true);
    expect(mockRuns[1].isCancelled()).toBe(false);
    await act(async () => {
      mockRuns[1].onUpdate(snapshot([["A源", "done", 1]], [item("新结果", "A源")]));
    });
    await act(async () => {
      mockRuns[0].onUpdate(snapshot([["A源", "done", 1]], [item("旧结果", "A源")]));
      mockRuns[0].finish();
    });
    expect(texts()).toContain("新结果");
    expect(texts()).not.toContain("旧结果");
  });

  it("says nothing was found only after every source has answered", async () => {
    submit("不存在");
    await act(async () => {
      mockRuns[0].onUpdate(snapshot([["A源", "done", 0], ["B源", "searching", 0]], []));
    });
    expect(texts()).not.toContain("没有找到相关内容");
    await act(async () => {
      mockRuns[0].onUpdate(snapshot([["A源", "done", 0], ["B源", "done", 0]], []));
      mockRuns[0].finish();
    });
    expect(texts()).toContain("没有找到相关内容");
  });
});
