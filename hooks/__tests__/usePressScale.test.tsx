import React from "react";
import renderer, { act } from "react-test-renderer";
import { Animated } from "react-native";

import { usePressScale, type PressScaleOptions, type PressScaleResult } from "../usePressScale";
import { MOTION, __resetMotionStateForTests } from "@/utils/motion";

jest.useFakeTimers();

const currentValue = (value: Animated.Value): number => (value as any).__getValue();

const renderPressScale = (options?: PressScaleOptions) => {
  const result: { current: PressScaleResult | null } = { current: null };
  const Probe = (props: { options?: PressScaleOptions }) => {
    result.current = usePressScale(props.options);
    return null;
  };
  let tree: renderer.ReactTestRenderer;
  act(() => {
    tree = renderer.create(<Probe options={options} />);
  });
  return { result: result as { current: PressScaleResult }, tree: tree! };
};

const settle = () =>
  act(() => {
    jest.advanceTimersByTime(1000);
  });

describe("usePressScale", () => {
  beforeEach(() => {
    __resetMotionStateForTests({ reduceMotion: false });
  });

  it("starts at full size", () => {
    const { result } = renderPressScale();
    expect(currentValue(result.current.scale)).toBe(1);
  });

  it("shrinks to the pressed scale on press-in and springs back on press-out", () => {
    const { result } = renderPressScale();

    act(() => result.current.pressHandlers.onPressIn({} as any));
    settle();
    expect(currentValue(result.current.scale)).toBeCloseTo(MOTION.pressScale, 2);
    expect(MOTION.pressScale).toBe(0.96);

    // press-out is also what a Touchable fires when a scroll gesture takes the touch away
    act(() => result.current.pressHandlers.onPressOut({} as any));
    settle();
    expect(currentValue(result.current.scale)).toBeCloseTo(1, 2);
  });

  it("uses the native driver and never drives layout", () => {
    const spring = jest.spyOn(Animated, "spring");
    const { result } = renderPressScale();

    act(() => result.current.pressHandlers.onPressIn({} as any));

    expect(spring).toHaveBeenCalledTimes(1);
    expect(spring.mock.calls[0][1]).toMatchObject({ toValue: 0.96, useNativeDriver: true });
    spring.mockRestore();
  });

  it("forwards the caller's press-in / press-out callbacks", () => {
    const onPressIn = jest.fn();
    const onPressOut = jest.fn();
    const { result } = renderPressScale({ onPressIn, onPressOut });
    const event = { nativeEvent: {} } as any;

    act(() => result.current.pressHandlers.onPressIn(event));
    act(() => result.current.pressHandlers.onPressOut(event));

    expect(onPressIn).toHaveBeenCalledWith(event);
    expect(onPressOut).toHaveBeenCalledWith(event);
  });

  it("honours a custom pressed scale", () => {
    const { result } = renderPressScale({ pressedScale: 0.9 });

    act(() => result.current.pressHandlers.onPressIn({} as any));
    settle();

    expect(currentValue(result.current.scale)).toBeCloseTo(0.9, 2);
  });

  it("does not scale when disabled, but still forwards callbacks", () => {
    const onPressIn = jest.fn();
    const { result } = renderPressScale({ disabled: true, onPressIn });

    act(() => result.current.pressHandlers.onPressIn({} as any));
    settle();

    expect(currentValue(result.current.scale)).toBe(1);
    expect(onPressIn).toHaveBeenCalledTimes(1);
  });

  it("does not scale when the system asks for reduced motion", () => {
    __resetMotionStateForTests({ reduceMotion: true });
    const { result } = renderPressScale();

    act(() => result.current.pressHandlers.onPressIn({} as any));
    settle();

    expect(currentValue(result.current.scale)).toBe(1);
  });

  it("keeps the same animated value across re-renders, and one value per card", () => {
    const result: { current: PressScaleResult | null } = { current: null };
    const Probe = ({ tick }: { tick: number }) => {
      result.current = usePressScale();
      return tick >= 0 ? null : null;
    };
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<Probe tick={0} />);
    });
    const first = result.current!;

    act(() => tree.update(<Probe tick={1} />));

    expect(result.current!.scale).toBe(first.scale);
    expect(result.current!.pressHandlers.onPressIn).toBe(first.pressHandlers.onPressIn);
    expect(renderPressScale().result.current.scale).not.toBe(first.scale);
  });
});
