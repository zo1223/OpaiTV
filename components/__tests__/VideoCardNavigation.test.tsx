import React from "react";
import renderer, { act } from "react-test-renderer";
import { Image, Pressable, Text, TouchableOpacity } from "react-native";

import VideoCardMobile from "../VideoCard.mobile";
import VideoCardTablet from "../VideoCard.tablet";
import VideoCardTV from "../VideoCard.tv";

const mockPush = jest.fn();
const mockReplace = jest.fn();

jest.useFakeTimers();

jest.mock("expo-router", () => ({
  useRouter: () => ({
    push: mockPush,
    replace: mockReplace,
    canGoBack: () => false,
  }),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({
    cardWidth: 120,
    cardHeight: 180,
    spacing: 8,
    deviceType: "tablet",
  }),
}));

jest.mock("@/utils/DeviceUtils", () => ({
  DeviceUtils: {
    getAnimationDuration: (duration: number) => duration,
  },
}));

jest.mock("@/services/storage", () => ({
  PlayRecordManager: {
    remove: jest.fn(),
  },
}));

const api = {
  getImageProxyUrl: (url: string) => url,
  getImageSource: jest.fn((url: string) => ({
    uri: url,
    headers: {
      Referer: "https://movie.douban.com/",
      "User-Agent": "Mozilla/5.0",
    },
  })),
};

const baseProps = {
  id: "89360",
  source: "mtzy.me",
  title: "炸薯条！电影版",
  poster: "https://example.com/poster.jpg",
  year: "2021",
  sourceName: "🎬茅台资源",
  api: api as any,
};

const renderCard = (Component: React.ComponentType<any>) => {
  let tree: renderer.ReactTestRenderer;
  act(() => {
    tree = renderer.create(<Component {...baseProps} />);
  });
  return tree!;
};

const flattenText = (value: unknown): string[] => {
  if (Array.isArray(value)) {
    return value.flatMap(flattenText);
  }
  if (typeof value === "string" || typeof value === "number") {
    return [String(value)];
  }
  return [];
};

const getRenderedText = (tree: renderer.ReactTestRenderer) =>
  tree.root.findAllByType(Text).flatMap((node) => flattenText(node.props.children));

describe("VideoCard navigation", () => {
  beforeEach(() => {
    mockPush.mockClear();
    mockReplace.mockClear();
    api.getImageSource.mockClear();
    jest.clearAllTimers();
  });

  afterEach(() => {
    act(() => {
      jest.runOnlyPendingTimers();
    });
    jest.clearAllTimers();
  });

  it.each([
    ["mobile", VideoCardMobile, TouchableOpacity],
    ["tablet", VideoCardTablet, TouchableOpacity],
    ["tv", VideoCardTV, Pressable],
  ])("%s detail navigation includes the video id", (_name, Component, PressableComponent) => {
    const tree = renderCard(Component);

    act(() => {
      tree.root.findByType(PressableComponent).props.onPress();
    });

    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/detail",
      params: {
        source: "mtzy.me",
        id: "89360",
        q: "炸薯条！电影版",
      },
    });
  });

  it.each([
    ["mobile", VideoCardMobile, TouchableOpacity],
    ["tablet", VideoCardTablet, TouchableOpacity],
    ["tv", VideoCardTV, Pressable],
  ])("%s detail navigation omits douban source ids so subscribed sources are searched immediately", (_name, Component, PressableComponent) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<Component {...baseProps} source="douban" id="36680492" sourceName={undefined} />);
    });

    act(() => {
      tree!.root.findByType(PressableComponent).props.onPress();
    });

    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/detail",
      params: {
        q: "炸薯条！电影版",
      },
    });
  });

  it("uses the API image source so native poster requests can include headers", () => {
    const tree = renderCard(VideoCardTV);

    expect(tree.root.findByType(Image).props.source).toEqual({
      uri: "https://example.com/poster.jpg",
      headers: {
        Referer: "https://movie.douban.com/",
        "User-Agent": "Mozilla/5.0",
      },
    });
    expect(api.getImageSource).toHaveBeenCalledWith("https://example.com/poster.jpg");
  });

  it.each([
    ["mobile", VideoCardMobile, TouchableOpacity],
    ["tablet", VideoCardTablet, TouchableOpacity],
    ["tv", VideoCardTV, Pressable],
  ])("%s continue-watching navigation still opens the player", (_name, Component, PressableComponent) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<Component {...baseProps} progress={0.5} episodeIndex={2} playTime={12} />);
    });

    act(() => {
      tree!.root.findByType(PressableComponent).props.onPress();
    });

    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: {
        source: "mtzy.me",
        id: "89360",
        episodeIndex: 1,
        title: "炸薯条！电影版",
        position: 12000,
      },
    });
  });

  it.each([
    ["mobile", VideoCardMobile],
    ["tablet", VideoCardTablet],
    ["tv", VideoCardTV],
  ])("%s shows playable source count on Douban cards", (_name, Component) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(
        <Component
          {...baseProps}
          source="douban"
          sourceName={undefined}
          playabilityStatus="playable"
          playableSourceCount={2}
        />
      );
    });

    expect(getRenderedText(tree!)).toContain("2源");
  });

  it.each([
    ["mobile", VideoCardMobile],
    ["tablet", VideoCardTablet],
    ["tv", VideoCardTV],
  ])("%s shows unavailable badge on Douban cards without resources", (_name, Component) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(
        <Component {...baseProps} source="douban" sourceName={undefined} playabilityStatus="unavailable" />
      );
    });

    expect(getRenderedText(tree!)).toContain("暂无资源");
  });
});
