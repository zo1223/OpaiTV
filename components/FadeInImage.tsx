import React, { useCallback, useEffect, useRef } from "react";
import { Animated, type ImageProps } from "react-native";
import { MOTION, isReduceMotionEnabled } from "@/utils/motion";

type ImageLoadHandler = NonNullable<ImageProps["onLoad"]>;
type ImageErrorHandler = NonNullable<ImageProps["onError"]>;

/**
 * 加载完成后淡入的图片(海报)。加载完成前是透明的,露出父容器的底色。
 *
 * 兜底:加载失败,或在限定时间内既没有成功也没有失败回调时,直接设为可见,
 * 之后的表现与普通 Image 相同 —— 不会出现"图片永远不显示"。
 * 系统开启"减少动态效果"时不做淡入。
 */
export function FadeInImage({ style, onLoad, onError, ...rest }: ImageProps) {
  const skip = useRef(isReduceMotionEnabled()).current;
  const opacity = useRef(new Animated.Value(skip ? 1 : 0)).current;
  const revealed = useRef(skip);

  const reveal = useCallback(
    (animated: boolean) => {
      if (revealed.current) return;
      revealed.current = true;
      if (!animated) {
        opacity.setValue(1);
        return;
      }
      Animated.timing(opacity, {
        toValue: 1,
        duration: MOTION.imageFadeDuration,
        useNativeDriver: true,
      }).start();
    },
    [opacity]
  );

  useEffect(() => {
    if (revealed.current) return;
    const timer = setTimeout(() => reveal(false), MOTION.imageRevealFallbackMs);
    return () => {
      clearTimeout(timer);
      opacity.stopAnimation();
    };
  }, [reveal, opacity]);

  const handleLoad = useCallback<ImageLoadHandler>(
    (event) => {
      reveal(true);
      onLoad?.(event);
    },
    [reveal, onLoad]
  );

  const handleError = useCallback<ImageErrorHandler>(
    (event) => {
      reveal(false);
      onError?.(event);
    },
    [reveal, onError]
  );

  return <Animated.Image {...rest} style={[style, { opacity }]} onLoad={handleLoad} onError={handleError} />;
}

export default FadeInImage;
