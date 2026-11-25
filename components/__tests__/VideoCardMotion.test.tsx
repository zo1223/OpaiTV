import React from "react";
import renderer, { act } from "react-test-renderer";
import { Animated, Image, StyleSheet, TouchableOpacity } from "react-native";

import VideoCardMobile from "../VideoCard.mobile";
import VideoCardTablet from "../VideoCard.tablet";
import { usePlayRecordUndoStore } from "@/stores/playRecordUndoStore";
import { MOTION, __resetMotionStateForTests } from "@/utils/motion";

const mockPush = jest.fn();

jest.useFakeTimers();

jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush, replace: jest.fn(), canGoBack: () => false }),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ cardWidth: 120, cardHeight: 180, spacing: 8, deviceType: "mobile" }),
}));

jest.mock("@/services/storage", () => ({
  PlayRecordManager: { get: jest.fn(async () => null), remove: jest.fn() },
}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));

const api = { getImageProxyUrl: (url: string) => url, getImageSource: jest.fn((url: string) => ({ uri: url })) };

const baseProps = {
  id: "89360",
  source: "source.example",
  title: "示例影片",
  poster: "https://example.com/poster.jpg",
  api: api as any,
};

const flatStyle = (node: { props: { style?: unknown } }) =>
  (StyleSheet.flatten(node.props.style as any) ?? {}) as Record<string, any>;

const rootStyle = (tree: renderer.ReactTestRenderer) => flatStyle(tree.toJSON() as renderer.ReactTestRendererJSON);

const scaleOf = (style: Record<string, any>): number | undefined =>
  (style.transform as Record<string, number>[] | undefined)?.find((entry) => "scale" in entry)?.scale;

const settle = () =>
  act(() => {
    jest.advanceTimersByTime(1000);
  });

describe.each([
  ["mobile", VideoCardMobile],
  ["tablet", VideoCardTablet],
])("%s video card motion", (_name, Component) => {
  const render = (props: Record<string, unknown> = {}) => {
    const element = <Component {...baseProps} {...props} />;
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(element);
    });
    const rerender = () => act(() => tree.update(<Component {...baseProps} {...props} />));
    return { tree: tree!, rerender };
  };

  beforeEach(() => {
    mockPush.mockClear();
    __resetMotionStateForTests({ reduceMotion: false });
    usePlayRecordUndoStore.setState({ pending: null } as any);
  });

  it("shrinks while pressed and springs back when released or when a scroll cancels the press", () => {
    const { tree, rerender } = render();
    const touchable = tree.root.findByType(TouchableOpacity);
    settle();

    act(() => touchable.props.onPressIn({ nativeEvent: {} }));
    settle();
    rerender();
    expect(scaleOf(rootStyle(tree))).toBeCloseTo(MOTION.pressScale, 2);

    // a Touchable reports a cancelled press (scroll took over) as press-out, without onPress
    act(() => tree.root.findByType(TouchableOpacity).props.onPressOut({ nativeEvent: {} }));
    settle();
    rerender();
    expect(scaleOf(rootStyle(tree))).toBeCloseTo(1, 2);
    expect(mockPush).not.toHaveBeenCalled();
  });

  it("still navigates on press after a press-in / press-out cycle", () => {
    const { tree } = render();
    const touchable = tree.root.findByType(TouchableOpacity);

    act(() => touchable.props.onPressIn({ nativeEvent: {} }));
    act(() => tree.root.findByType(TouchableOpacity).props.onPressOut({ nativeEvent: {} }));
    act(() => tree.root.findByType(TouchableOpacity).props.onPress());

    expect(mockPush).toHaveBeenCalledTimes(1);
    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/detail",
      params: { source: "source.example", id: "89360", q: "示例影片" },
    });
  });

  it("still starts the undoable delete on long press while pressed", () => {
    const onRecordDeleted = jest.fn();
    const requestDelete = jest.fn();
    const original = usePlayRecordUndoStore.getState().requestDelete;
    usePlayRecordUndoStore.setState({ requestDelete } as any);
    try {
      const { tree } = render({ progress: 0.4, episodeIndex: 2, playTime: 60, onRecordDeleted });

      act(() => tree.root.findByType(TouchableOpacity).props.onPressIn({ nativeEvent: {} }));
      act(() => tree.root.findByType(TouchableOpacity).props.onLongPress());
      act(() => tree.root.findByType(TouchableOpacity).props.onPressOut({ nativeEvent: {} }));

      expect(requestDelete).toHaveBeenCalledWith({ source: "source.example", id: "89360", title: "示例影片" });
      expect(onRecordDeleted).toHaveBeenCalledTimes(1);
      expect(mockPush).not.toHaveBeenCalled();
    } finally {
      usePlayRecordUndoStore.setState({ requestDelete: original } as any);
    }
  });

  it("fades in on mount without changing its layout size", () => {
    const timing = jest.spyOn(Animated, "timing");
    const { tree, rerender } = render();

    const before = rootStyle(tree);
    expect(before.opacity).toBe(0);
    expect(before.width).toBe(120);
    expect(timing.mock.calls[0][1]).toMatchObject({ duration: MOTION.entranceDuration, useNativeDriver: true });

    settle();
    rerender();
    const after = rootStyle(tree);
    expect(after.opacity).toBe(1);
    expect(after.width).toBe(120);
    timing.mockRestore();
  });

  it("fades the poster in once it has loaded", () => {
    const { tree, rerender } = render();

    expect(tree.root.findByType(Image).props.source).toEqual({ uri: "https://example.com/poster.jpg" });
    expect(flatStyle(tree.root.findByType(Image)).opacity).toBe(0);

    act(() => tree.root.findByType(Image).props.onLoad({ nativeEvent: {} }));
    settle();
    rerender();

    expect(flatStyle(tree.root.findByType(Image)).opacity).toBe(1);
  });

  it("does not animate at all when the system asks for reduced motion", () => {
    __resetMotionStateForTests({ reduceMotion: true });
    const { tree, rerender } = render();

    expect(rootStyle(tree).opacity).toBe(1);
    expect(flatStyle(tree.root.findByType(Image)).opacity).toBe(1);

    act(() => tree.root.findByType(TouchableOpacity).props.onPressIn({ nativeEvent: {} }));
    settle();
    rerender();
    expect(scaleOf(rootStyle(tree))).toBe(1);
  });
});
