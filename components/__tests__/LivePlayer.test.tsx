import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text } from "react-native";

import LivePlayer from "../LivePlayer";

jest.mock("expo-keep-awake", () => ({ useKeepAwake: jest.fn() }));

jest.mock("expo-av", () => {
  const { View } = jest.requireActual("react-native");
  const { forwardRef } = jest.requireActual("react");
  return {
    Video: forwardRef((props: object, _ref: unknown) => <View testID="video" {...props} />),
    ResizeMode: { CONTAIN: "contain" },
  };
});

describe("LivePlayer", () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  const render = (tree: renderer.ReactTestRenderer | null, reloadToken: number) => {
    const element = (
      <LivePlayer streamUrl="http://example.com/a.m3u8" reloadToken={reloadToken} onPlaybackStatusUpdate={jest.fn()} />
    );
    let result = tree;
    act(() => {
      if (result) result.update(element);
      else result = renderer.create(element);
    });
    return result!;
  };

  const texts = (tree: renderer.ReactTestRenderer) => tree.root.findAllByType(Text).map((t) => t.props.children);
  const videos = (tree: renderer.ReactTestRenderer) => tree.root.findAllByProps({ testID: "video" });

  it("retries the same stream after a failure when reloadToken changes", () => {
    const tree = render(null, 0);
    act(() => videos(tree)[0].props.onError("boom"));
    expect(texts(tree)).toContain("加载失败，请重试");
    expect(videos(tree)).toHaveLength(0);

    render(tree, 1);

    expect(texts(tree)).not.toContain("加载失败，请重试");
    expect(videos(tree).length).toBeGreaterThan(0);
    act(() => tree.unmount());
  });

  it("keeps showing the failure while nothing changes", () => {
    const tree = render(null, 0);
    act(() => jest.advanceTimersByTime(15000));
    expect(texts(tree)).toContain("加载失败，请重试");

    render(tree, 0);

    expect(texts(tree)).toContain("加载失败，请重试");
    act(() => tree.unmount());
  });
});
