import { useRef, useEffect } from 'react';
import { Animated } from 'react-native';

export const useButtonAnimation = (isFocused: boolean, size: number = 1.1) => {
  const scaleValue = useRef(new Animated.Value(1)).current;

  useEffect(() => {
    Animated.spring(scaleValue, {
      toValue: isFocused ? size : 1,
      friction: 5,
      useNativeDriver: true,
    }).start();
  }, [ isFocused, scaleValue, size]);

  return {
    transform: [{ scale: scaleValue }],
  };
};