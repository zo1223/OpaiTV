import React, { forwardRef, useEffect, useMemo, useRef } from "react";
import { Animated, Easing, View, type StyleProp, type ViewProps, type ViewStyle } from "react-native";
import { MOTION, isReduceMotionEnabled, nextEntranceDelay } from "@/utils/motion";

/** 追加到出现动画之后的 transform 项(例如按压缩放),值可以是动画值 */
export type ExtraTransform = Record<string, Animated.Value | Animated.AnimatedInterpolation<number> | number>[];

export interface FadeInViewProps extends Omit<ViewProps, "style"> {
  style?: StyleProp<ViewStyle>;
  /** 出现前的等待时间(毫秒) */
  delay?: number;
  /**
   * 列表错峰:同一批挂载的项依次错开出现(间隔与上限见 MOTION),在 `delay` 之上叠加。
   * 之后追加进列表的项是新的一批,只有它们自己播放出现动画。
   */
  stagger?: boolean;
  /** 淡入时长,默认 200ms */
  duration?: number;
  /** 起始下移距离,默认 8 */
  offsetY?: number;
  /** 为 true 时不播放动画,直接显示 */
  disabled?: boolean;
  /**
   * 与出现动画合并到同一个视图上的额外 transform。
   * 注意:`style` 里自带的 transform 会被本组件覆盖,要用这个属性传。
   */
  extraTransform?: ExtraTransform;
  children?: React.ReactNode;
}

/**
 * 挂载时淡入并轻微上移的容器。子内容从第一帧起就已挂载、可点击,动画只动 opacity / transform,
 * 不改变布局尺寸,全程原生驱动。系统开启"减少动态效果"时直接显示。
 * 动画只在挂载时播放一次:之后的重新渲染(比如列表追加了别的项)不会重播。
 */
export const FadeInView = forwardRef<View, FadeInViewProps>(
  (
    {
      style,
      delay = 0,
      stagger = false,
      duration = MOTION.entranceDuration,
      offsetY = MOTION.entranceOffsetY,
      disabled = false,
      extraTransform,
      children,
      ...rest
    },
    ref
  ) => {
    // 是否跳过动画只在挂载时决定一次
    const skip = useRef(disabled || isReduceMotionEnabled()).current;
    const progress = useRef(new Animated.Value(skip ? 1 : 0)).current;

    useEffect(() => {
      if (skip) return;
      // 错峰延迟在挂载副作用里取:同一次提交挂载的项,副作用是连着执行的,自然落在同一批
      const animation = Animated.timing(progress, {
        toValue: 1,
        duration,
        delay: delay + (stagger ? nextEntranceDelay() : 0),
        easing: Easing.out(Easing.cubic),
        useNativeDriver: true,
      });
      animation.start();
      return () => animation.stop();
      // 只在挂载时播放一次
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const translateY = useMemo(
      () => progress.interpolate({ inputRange: [0, 1], outputRange: [offsetY, 0] }),
      [progress, offsetY]
    );

    return (
      <Animated.View
        {...rest}
        ref={ref}
        style={[style, { opacity: progress, transform: [{ translateY }, ...(extraTransform ?? [])] as any }]}
      >
        {children}
      </Animated.View>
    );
  }
);

FadeInView.displayName = "FadeInView";

export default FadeInView;
