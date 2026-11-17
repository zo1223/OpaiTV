// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text } from "react-native";

import LiveScreen from "../../app/live";

const mockFetchAndParseM3u = jest.fn();
let mockM3uUrl = "";

jest.mock("@/services/m3u", () => ({
  fetchAndParseM3u: (url: string) => mockFetchAndParseM3u(url),
  getPlayableUrl: (url: string | null) => url,
}));

jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: () => ({ m3uUrl: mockM3uUrl }),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "tv", spacing: 8 }),
}));

jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ container: {} }),
}));

jest.mock("@/utils/DeviceUtils", () => ({
  DeviceUtils: { getMinTouchTargetSize: () => 44 },
}));

jest.mock("@/hooks/useSafeTVEventHandler", () => ({ useSafeTVEventHandler: jest.fn() }));
jest.mock("@/components/StyledButton", () => ({ StyledButton: () => null }));
jest.mock("@/components/navigation/ResponsiveNavigation", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/navigation/ResponsiveHeader", () => ({ __esModule: true, default: () => null }));

jest.mock("@/components/LivePlayer", () => {
  const { Text: MockText } = jest.requireActual("react-native");
  return {
    __esModule: true,
    default: ({ streamUrl }: { streamUrl: string | null }) => <MockText>{streamUrl ?? "NO_STREAM"}</MockText>,
  };
});

type Channel = { id: string; name: string; url: string; logo: string; group: string };

const channel = (url: string): Channel => ({ id: `0-${url}`, name: url, url, logo: "", group: "Default" });

describe("LiveScreen", () => {
  let pending: Record<string, (channels: Channel[]) => void>;
  let tree: renderer.ReactTestRenderer;

  const streamUrl = () => tree.root.findByType(Text).props.children;

  const show = (url: string) => {
    mockM3uUrl = url;
    act(() => {
      if (tree) {
        tree.update(<LiveScreen />);
      } else {
        tree = renderer.create(<LiveScreen />);
      }
    });
  };

  const settle = (url: string, channels: Channel[]) =>
    act(async () => {
      pending[url](channels);
    });

  beforeEach(() => {
    jest.useFakeTimers();
    pending = {};
    mockFetchAndParseM3u.mockReset();
    mockFetchAndParseM3u.mockImplementation(
      (url: string) =>
        new Promise((resolve) => {
          pending[url] = resolve;
        })
    );
  });

  afterEach(() => {
    act(() => tree.unmount());
    tree = undefined as unknown as renderer.ReactTestRenderer;
    jest.useRealTimers();
  });

  it("keeps the newer playlist's channels when an older playlist resolves last", async () => {
    show("http://host/old.m3u");
    show("http://host/new.m3u");

    await settle("http://host/new.m3u", [channel("http://stream/new")]);
    expect(streamUrl()).toBe("http://stream/new");

    await settle("http://host/old.m3u", [channel("http://stream/old")]);
    expect(streamUrl()).toBe("http://stream/new");
  });

  it("does not let a superseded playlist's empty result wipe the newer channels", async () => {
    show("http://host/list.m3");
    show("http://host/list.m3u");

    await settle("http://host/list.m3u", [channel("http://stream/full")]);
    await settle("http://host/list.m3", []);

    expect(streamUrl()).toBe("http://stream/full");
  });

  it("loads the channels of a playlist that was never superseded", async () => {
    show("http://host/only.m3u");
    expect(streamUrl()).toBe("NO_STREAM");

    await settle("http://host/only.m3u", [channel("http://stream/only")]);
    expect(streamUrl()).toBe("http://stream/only");
  });
});
