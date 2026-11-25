import { useCallback, useEffect, useRef } from "react";
import { Animated, type GestureResponderEvent } from "react-native";
import { MOTION, isReduceMotionEnabled } from "@/utils/motion";

export interface PressScaleOptions {
  /** 按下时缩到多少,默认 0.96 */
  pressedScale?: number;
  /** 为 true 时不做缩放(回调照常转发) */
  disabled?: boolean;
  onPressIn?: (event: GestureResponderEvent) => void;
  onPressOut?: (event: GestureResponderEvent) => void;
}

export interface PressScaleResult {
  /** 放进 `transform: [{ scale }]` 的动画值 */
  scale: Animated.Value;
  /** 展开到 Touchable / Pressable 上;不占用 onPress / onLongPress */
  pressHandlers: {
    onPressIn: (event: GestureResponderEvent) => void;
    onPressOut: (event: GestureResponderEvent) => void;
  };
}

/**
 * 卡片按压缩放:按下缩小一点,松开弹回。
 *
 * - 只动 transform,原生驱动,不触发重新渲染,也不改变布局尺寸。
 * - 只接 onPressIn / onPressOut:点击、长按的判定仍由 Touchable 自己完成。
 *   滚动手势抢走触摸时 Touchable 会触发 onPressOut,卡片随之回弹。
 * - 系统开启"减少动态效果"时不缩放。
 */
export function usePressScale(options: PressScaleOptions = {}): PressScaleResult {
  const { pressedScale = MOTION.pressScale, disabled = false, onPressIn, onPressOut } = options;
  const scale = useRef(new Animated.Value(1)).current;

  const animateTo = useCallback(
    (toValue: number) => {
      if (disabled || isReduceMotionEnabled()) {
        scale.stopAnimation();
        scale.setValue(1);
        return;
      }
      Animated.spring(scale, {
        toValue,
        damping: MOTION.pressSpring.damping,
        stiffness: MOTION.pressSpring.stiffness,
        useNativeDriver: true,
      }).start();
    },
    [disabled, scale]
  );

  const handlePressIn = useCallback(
    (event: GestureResponderEvent) => {
      animateTo(pressedScale);
      onPressIn?.(event);
    },
    [animateTo, pressedScale, onPressIn]
  );

  const handlePressOut = useCallback(
    (event: GestureResponderEvent) => {
      animateTo(1);
      onPressOut?.(event);
    },
    [animateTo, onPressOut]
  );

  useEffect(() => {
    return () => {
      scale.stopAnimation();
    };
  }, [scale]);

  return { scale, pressHandlers: { onPressIn: handlePressIn, onPressOut: handlePressOut } };
}
