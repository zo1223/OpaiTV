// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text, TextInput } from "react-native";

import SearchScreen from "../../app/search";

const mockSearchVideos = jest.fn();

// 搜索记录(标签云)存在本机 AsyncStorage，这里用内存表代替
const mockStorage: Record<string, string> = {};
jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(async (key: string) => mockStorage[key] ?? null),
  setItem: jest.fn(async (key: string, value: string) => {
    mockStorage[key] = value;
  }),
  removeItem: jest.fn(async (key: string) => {
    delete mockStorage[key];
  }),
}));

jest.mock("@/services/api", () => ({
  api: {
    searchVideos: (term: string) => mockSearchVideos(term),
    // 增量搜索在这些用例里按“一个源、一次回报”模拟;逐源回报的用例见 SearchIncremental.test.tsx
    searchVideosIncremental: async (term: string, onUpdate: (snapshot: any) => void) => {
      const response = await mockSearchVideos(term);
      const snapshot = {
        results: response.results,
        progress: [{ key: "s", name: "S", status: "done", count: response.results.length }],
        finished: 1,
        total: 1,
        done: true,
      };
      onUpdate(snapshot);
      return snapshot;
    },
    isAdultItemHidden: () => false,
  },
}));

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: jest.fn() }),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "tv", spacing: 8 }),
}));

jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ container: {}, center: {} }),
}));

jest.mock("@/utils/DeviceUtils", () => ({
  DeviceUtils: { getMinTouchTargetSize: () => 44 },
}));

jest.mock("@/stores/remoteControlStore", () => ({
  useRemoteControlStore: () => ({
    showModal: jest.fn(),
    lastMessage: null,
    targetPage: null,
    clearMessage: jest.fn(),
  }),
}));

jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: () => ({ remoteInputEnabled: false }),
}));

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
  return { __esModule: true, default: ({ title }: { title: string }) => <MockText>{title}</MockText> };
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

type Deferred = { resolve: (value: { results: any[] }) => void; reject: (error: Error) => void };

const resultFor = (title: string) => ({ id: 1, source: "s", title, poster: "", year: "2024", source_name: "S" });

describe("SearchScreen", () => {
  let pending: Record<string, Deferred>;
  let tree: renderer.ReactTestRenderer;

  // 逐源进度行(“已搜完 1/1 个源，共 1 条”“S 1 条”)不在这些用例的关注范围内,见 SearchIncremental.test.tsx
  const texts = () =>
    tree.root
      .findAllByType(Text)
      .map((node) => node.props.children)
      .filter((value) => !(typeof value === "string" && /个源，共 \d+ 条$|^S \d+ 条$/.test(value)));

  const submit = (term: string) => {
    act(() => {
      tree.root.findByType(TextInput).props.onChangeText(term);
    });
    act(() => {
      tree.root.findByType(TextInput).props.onSubmitEditing();
    });
  };

  const settle = (term: string, action: (deferred: Deferred) => void) =>
    act(async () => {
      action(pending[term]);
    });

  beforeEach(() => {
    pending = {};
    for (const key of Object.keys(mockStorage)) delete mockStorage[key];
    mockSearchVideos.mockReset();
    mockSearchVideos.mockImplementation(
      (term: string) =>
        new Promise((resolve, reject) => {
          pending[term] = { resolve, reject };
        })
    );
    act(() => {
      tree = renderer.create(<SearchScreen />);
    });
  });

  afterEach(() => {
    act(() => tree.unmount());
  });

  it("keeps the newer search's results when an older search resolves last", async () => {
    submit("old");
    submit("new");

    await settle("new", (d) => d.resolve({ results: [resultFor("New Movie")] }));
    expect(texts()).toEqual(["New Movie"]);

    await settle("old", (d) => d.resolve({ results: [resultFor("Old Movie")] }));
    expect(texts()).toEqual(["New Movie"]);
  });

  it("stays in the loading state until the newest search settles", async () => {
    submit("old");
    submit("new");

    await settle("old", (d) => d.resolve({ results: [resultFor("Old Movie")] }));
    expect(texts()).toEqual(["LOADING"]);

    await settle("new", (d) => d.resolve({ results: [resultFor("New Movie")] }));
    expect(texts()).toEqual(["New Movie"]);
  });

  it("does not show a superseded search's failure over newer results", async () => {
    submit("old");
    submit("new");

    await settle("new", (d) => d.resolve({ results: [resultFor("New Movie")] }));
    await settle("old", (d) => d.reject(new Error("Network request failed")));

    expect(texts()).toEqual(["New Movie"]);
  });
});

describe("SearchScreen search records and natural-language expansion", () => {
  const HISTORY_KEY = "mytv_search_tag_cloud";
  let pending: Record<string, Deferred>;
  let tree: renderer.ReactTestRenderer;

  // 逐源进度行(“已搜完 1/1 个源，共 1 条”“S 1 条”)不在这些用例的关注范围内,见 SearchIncremental.test.tsx
  const texts = () =>
    tree.root
      .findAllByType(Text)
      .map((node) => node.props.children)
      .filter((value) => !(typeof value === "string" && /个源，共 \d+ 条$|^S \d+ 条$/.test(value)));
  const press = (label: string) =>
    act(() => {
      tree.root
        .findAll((node) => node.props.accessibilityLabel === label && typeof node.props.onPress === "function")[0]
        .props.onPress();
    });
  const mount = () =>
    act(async () => {
      tree = renderer.create(<SearchScreen />);
    });
  const settle = (term: string, action: (deferred: Deferred) => void) =>
    act(async () => {
      action(pending[term]);
    });

  beforeEach(() => {
    pending = {};
    for (const key of Object.keys(mockStorage)) delete mockStorage[key];
    mockSearchVideos.mockReset();
    mockSearchVideos.mockImplementation(
      (term: string) =>
        new Promise((resolve, reject) => {
          pending[term] = { resolve, reject };
        })
    );
  });

  afterEach(() => {
    act(() => tree.unmount());
  });

  const seedHistory = () => {
    mockStorage[HISTORY_KEY] = JSON.stringify([
      { query: "流浪地球", count: 3, lastUsedAt: 2000 },
      { query: "繁花", count: 1, lastUsedAt: 1000 },
    ]);
  };

  it("shows nothing extra when there is no search record", async () => {
    await mount();
    expect(texts()).toEqual([]);
  });

  it("shows the search records as a tag cloud and replays a tapped record", async () => {
    seedHistory();
    await mount();

    expect(texts()).toEqual(["搜索记录", "清空", "流浪地球", "×", "繁花", "×"]);

    press("搜索 流浪地球");

    expect(mockSearchVideos).toHaveBeenCalledWith("流浪地球");
    expect(tree.root.findByType(TextInput).props.value).toBe("流浪地球");
    expect(texts()).toEqual(["LOADING"]);
  });

  it("removes a single record and clears all records", async () => {
    seedHistory();
    await mount();

    press("删除搜索记录 繁花");
    expect(texts()).toEqual(["搜索记录", "清空", "流浪地球", "×"]);

    press("清空搜索记录");
    expect(texts()).toEqual([]);

    await act(async () => {});
    expect(mockStorage[HISTORY_KEY]).toBeUndefined();
  });

  it("records a submitted search locally", async () => {
    await mount();
    act(() => {
      tree.root.findByType(TextInput).props.onChangeText("繁花");
    });
    act(() => {
      tree.root.findByType(TextInput).props.onSubmitEditing();
    });
    await act(async () => {});

    const saved = JSON.parse(mockStorage[HISTORY_KEY]);
    expect(saved).toHaveLength(1);
    expect(saved[0]).toMatchObject({ query: "繁花", count: 1 });
  });

  it("expands a natural-language query through genre searches and merges the results", async () => {
    await mount();
    act(() => {
      tree.root.findByType(TextInput).props.onChangeText("回南天");
    });
    act(() => {
      tree.root.findByType(TextInput).props.onSubmitEditing();
    });
    await act(async () => {});

    expect(mockSearchVideos.mock.calls.map((call) => call[0]).sort()).toEqual(["剧情", "回南天", "悬疑", "爱情"].sort());

    await settle("回南天", (d) => d.resolve({ results: [resultFor("回南天")] }));
    // 增量搜索:关键词结果先显示,扩展检索的结果到了再并进来
    expect(texts()).toEqual(["回南天"]);
    await settle("剧情", (d) => d.resolve({ results: [resultFor("Drama One")] }));
    await settle("悬疑", (d) => d.resolve({ results: [resultFor("drama one "), resultFor("Mystery Two")] }));
    await settle("爱情", (d) => d.reject(new Error("Network request failed")));

    expect(texts()).toEqual(["已按“阴天絮语”扩展检索：剧情 / 悬疑 / 爱情", "回南天", "Drama One", "Mystery Two"]);
  });

  it("keeps the genre results when the keyword search itself fails", async () => {
    await mount();
    act(() => {
      tree.root.findByType(TextInput).props.onChangeText("夜宵");
    });
    act(() => {
      tree.root.findByType(TextInput).props.onSubmitEditing();
    });
    await act(async () => {});

    await settle("夜宵", (d) => d.reject(new Error("Network request failed")));
    await settle("犯罪", (d) => d.resolve({ results: [resultFor("Crime One")] }));
    await settle("惊悚", (d) => d.resolve({ results: [] }));
    await settle("喜剧", (d) => d.resolve({ results: [] }));

    expect(texts()).toEqual(["已按“小酌夜宵”扩展检索：犯罪 / 惊悚 / 喜剧", "Crime One"]);
  });
});
