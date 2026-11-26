import React from "react";
import renderer, { act } from "react-test-renderer";
import { Pressable, Text, TouchableOpacity } from "react-native";

import VideoCardMobile from "../VideoCard.mobile";
import VideoCardTablet from "../VideoCard.tablet";
import VideoCardTV from "../VideoCard.tv";
import { PlayRecordManager } from "@/services/storage";

const mockPush = jest.fn();

jest.useFakeTimers();

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush, replace: jest.fn(), canGoBack: () => false }),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ cardWidth: 120, cardHeight: 180, spacing: 8, deviceType: "tablet" }),
}));

jest.mock("@/utils/DeviceUtils", () => ({
  DeviceUtils: { getAnimationDuration: (duration: number) => duration },
}));

jest.mock("@/services/storage", () => ({
  PlayRecordManager: { get: jest.fn(async () => null), remove: jest.fn() },
}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));

const api = { getImageProxyUrl: (url: string) => url, getImageSource: jest.fn((url: string) => ({ uri: url })) };

const baseProps = { id: "42", source: "alpha", title: "三体", poster: "https://example.com/p.jpg", api: api as any };

const cards: [string, React.ComponentType<any>, any][] = [
  ["mobile", VideoCardMobile, TouchableOpacity],
  ["tablet", VideoCardTablet, TouchableOpacity],
  ["tv", VideoCardTV, Pressable],
];

const press = async (Component: React.ComponentType<any>, Touchable: any, props: Record<string, unknown>) => {
  let tree: renderer.ReactTestRenderer;
  act(() => {
    tree = renderer.create(<Component {...baseProps} {...props} />);
  });
  await act(async () => {
    tree!.root.findAllByType(Touchable)[0].props.onPress();
  });
  return tree!;
};

describe("VideoCard direct play and recommendation reason", () => {
  beforeEach(() => {
    mockPush.mockClear();
    (PlayRecordManager.get as jest.Mock).mockReset().mockResolvedValue(null);
  });

  afterEach(() => {
    act(() => {
      jest.runOnlyPendingTimers();
    });
  });

  it.each(cards)("%s directPlay card starts playback from the first episode without opening the detail page", async (_n, Component, Touchable) => {
    await press(Component, Touchable, { directPlay: true });
    expect(mockPush).toHaveBeenCalledTimes(1);
    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: { source: "alpha", id: "42", title: "三体", episodeIndex: 0 },
    });
  });

  it.each(cards)("%s directPlay card resumes from the saved play record", async (_n, Component, Touchable) => {
    (PlayRecordManager.get as jest.Mock).mockResolvedValue({ index: 3, play_time: 95.5 });
    await press(Component, Touchable, { directPlay: true });
    expect(PlayRecordManager.get).toHaveBeenCalledWith("alpha", "42");
    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: { source: "alpha", id: "42", title: "三体", episodeIndex: 2, position: 95500 },
    });
  });

  it.each(cards)("%s still plays from the first episode when the play record cannot be read", async (_n, Component, Touchable) => {
    (PlayRecordManager.get as jest.Mock).mockRejectedValue(new Error("storage down"));
    await press(Component, Touchable, { directPlay: true });
    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: { source: "alpha", id: "42", title: "三体", episodeIndex: 0 },
    });
  });

  it.each(cards)("%s card without directPlay (search results included) opens the detail page", async (_n, Component, Touchable) => {
    await press(Component, Touchable, {});
    expect(mockPush).toHaveBeenCalledWith({ pathname: "/detail", params: { source: "alpha", id: "42", q: "三体" } });
  });

  it.each(cards)("%s card shows the recommendation reason", async (_n, Component) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<Component {...baseProps} reason="因为你看过《三体》" />);
    });
    const texts = tree!.root.findAllByType(Text).map((node) => node.props.children);
    expect(texts).toContain("因为你看过《三体》");
  });
});
