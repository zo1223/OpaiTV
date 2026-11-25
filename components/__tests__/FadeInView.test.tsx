import React from "react";
import renderer, { act } from "react-test-renderer";
import { Animated, Image, StyleSheet, Text, View } from "react-native";

import { FadeInView } from "../FadeInView";
import { FadeInImage } from "../FadeInImage";
import { MOTION, __resetMotionStateForTests } from "@/utils/motion";

jest.useFakeTimers();

const flatStyle = (node: renderer.ReactTestRendererJSON | renderer.ReactTestInstance) =>
  (StyleSheet.flatten((node as any).props.style) ?? {}) as Record<string, any>;

const rootJson = (tree: renderer.ReactTestRenderer) => tree.toJSON() as renderer.ReactTestRendererJSON;

const transformValue = (style: Record<string, any>, key: string): number | undefined =>
  (style.transform as Record<string, number>[] | undefined)?.find((entry) => key in entry)?.[key];

describe("FadeInView", () => {
  let timing: jest.SpyInstance;

  beforeEach(() => {
    __resetMotionStateForTests({ reduceMotion: false });
    timing = jest.spyOn(Animated, "timing");
  });

  afterEach(() => {
    timing.mockRestore();
  });

  const render = (element: React.ReactElement) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(element);
    });
    return tree!;
  };

  it("mounts its children immediately, starting transparent and slightly lowered", () => {
    const tree = render(
      <FadeInView>
        <Text>内容</Text>
      </FadeInView>
    );

    expect(tree.root.findByType(Text).props.children).toBe("内容");
    const style = flatStyle(rootJson(tree));
    expect(style.opacity).toBe(0);
    expect(transformValue(style, "translateY")).toBe(MOTION.entranceOffsetY);
  });

  it("fades in over 200ms on the native driver", () => {
    render(<FadeInView />);

    expect(timing).toHaveBeenCalledTimes(1);
    expect(timing.mock.calls[0][1]).toMatchObject({ toValue: 1, duration: 200, delay: 0, useNativeDriver: true });
  });

  it("ends fully visible at its natural position", () => {
    const tree = render(<FadeInView style={{ width: 120 }} />);

    act(() => {
      jest.advanceTimersByTime(1000);
    });
    act(() => tree.update(<FadeInView style={{ width: 120 }} />));

    const style = flatStyle(rootJson(tree));
    expect(style.opacity).toBe(1);
    expect(transformValue(style, "translateY")).toBe(0);
    expect(style.width).toBe(120); // layout untouched
  });

  it("passes delay through and staggers items of one batch up to a cap", () => {
    render(
      <View>
        {Array.from({ length: 12 }, (_, index) => (
          <FadeInView key={index} stagger delay={index === 0 ? 5 : 0} />
        ))}
      </View>
    );

    const delays = timing.mock.calls.map((call) => call[1].delay);
    expect(delays).toHaveLength(12);
    expect(delays[0]).toBe(5);
    expect(delays[1]).toBe(30);
    expect(delays[7]).toBe(210);
    expect(Math.max(...delays)).toBe(210);
  });

  it("plays only once: re-rendering or appending siblings does not replay it", () => {
    const list = (count: number) => (
      <View>
        {Array.from({ length: count }, (_, index) => (
          <FadeInView key={index} stagger />
        ))}
      </View>
    );
    const tree = render(list(3));
    expect(timing).toHaveBeenCalledTimes(3);
    act(() => {
      jest.advanceTimersByTime(1000);
    });

    act(() => tree.update(list(5)));

    // only the two appended items animate, as a fresh batch
    expect(timing).toHaveBeenCalledTimes(5);
    expect(timing.mock.calls[3][1].delay).toBe(0);
    expect(timing.mock.calls[4][1].delay).toBe(30);
  });

  it("merges extra transforms (press scale) with its own", () => {
    const scale = new Animated.Value(0.96);
    const tree = render(<FadeInView extraTransform={[{ scale }]} />);

    const style = flatStyle(rootJson(tree));
    expect(transformValue(style, "scale")).toBe(0.96);
    expect(transformValue(style, "translateY")).toBe(MOTION.entranceOffsetY);
  });

  it("shows immediately without animating when disabled", () => {
    const tree = render(<FadeInView disabled />);

    expect(timing).not.toHaveBeenCalled();
    expect(flatStyle(rootJson(tree)).opacity).toBe(1);
  });

  it("shows immediately when the system asks for reduced motion", () => {
    __resetMotionStateForTests({ reduceMotion: true });
    const tree = render(<FadeInView stagger />);

    expect(timing).not.toHaveBeenCalled();
    const style = flatStyle(rootJson(tree));
    expect(style.opacity).toBe(1);
    expect(transformValue(style, "translateY")).toBe(0);
  });

  it("forwards its ref and view props", () => {
    const ref = React.createRef<View>();
    const tree = render(<FadeInView ref={ref} testID="fade" />);

    expect(rootJson(tree).props.testID).toBe("fade");
  });
});

describe("FadeInImage", () => {
  const source = { uri: "https://example.com/poster.jpg" };
  let timing: jest.SpyInstance;

  beforeEach(() => {
    __resetMotionStateForTests({ reduceMotion: false });
    timing = jest.spyOn(Animated, "timing");
  });

  afterEach(() => {
    timing.mockRestore();
  });

  const render = (element: React.ReactElement) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(element);
    });
    return tree!;
  };

  const opacityAfterRerender = (tree: renderer.ReactTestRenderer, element: React.ReactElement) => {
    act(() => tree.update(element));
    return flatStyle(tree.root.findByType(Image)).opacity;
  };

  it("keeps the source and style, and is transparent until the image has loaded", () => {
    const tree = render(<FadeInImage source={source} style={{ width: 10, height: 20 }} />);
    const image = tree.root.findByType(Image);

    expect(image.props.source).toEqual(source);
    expect(flatStyle(image)).toMatchObject({ width: 10, height: 20, opacity: 0 });
    expect(timing).not.toHaveBeenCalled();
  });

  it("fades in once loaded and still calls the caller's onLoad", () => {
    const onLoad = jest.fn();
    const element = <FadeInImage source={source} onLoad={onLoad} />;
    const tree = render(element);

    act(() => tree.root.findByType(Image).props.onLoad({ nativeEvent: {} }));

    expect(onLoad).toHaveBeenCalledTimes(1);
    expect(timing).toHaveBeenCalledTimes(1);
    expect(timing.mock.calls[0][1]).toMatchObject({ toValue: 1, duration: 200, useNativeDriver: true });

    act(() => {
      jest.advanceTimersByTime(1000);
    });
    expect(opacityAfterRerender(tree, element)).toBe(1);
  });

  it("becomes visible without animation when loading fails, and reports the error", () => {
    const onError = jest.fn();
    const element = <FadeInImage source={source} onError={onError} />;
    const tree = render(element);

    act(() => tree.root.findByType(Image).props.onError({ nativeEvent: { error: "404" } }));

    expect(onError).toHaveBeenCalledTimes(1);
    expect(timing).not.toHaveBeenCalled();
    expect(opacityAfterRerender(tree, element)).toBe(1);
  });

  it("never stays invisible: shows itself if no load event ever arrives", () => {
    const element = <FadeInImage source={source} />;
    const tree = render(element);

    act(() => {
      jest.advanceTimersByTime(MOTION.imageRevealFallbackMs - 1);
    });
    expect(opacityAfterRerender(tree, element)).toBe(0);

    act(() => {
      jest.advanceTimersByTime(1);
    });
    expect(opacityAfterRerender(tree, element)).toBe(1);
  });

  it("is visible from the start when the system asks for reduced motion", () => {
    __resetMotionStateForTests({ reduceMotion: true });
    const tree = render(<FadeInImage source={source} />);

    expect(flatStyle(tree.root.findByType(Image)).opacity).toBe(1);
  });
});
