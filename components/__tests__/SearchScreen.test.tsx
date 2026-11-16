// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text, TextInput } from "react-native";

import SearchScreen from "../../app/search";

const mockSearchVideos = jest.fn();

jest.mock("@/services/api", () => ({
  api: { searchVideos: (term: string) => mockSearchVideos(term) },
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

  const texts = () => tree.root.findAllByType(Text).map((node) => node.props.children);

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
