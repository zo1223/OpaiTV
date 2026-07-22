import React from 'react';
import { TouchableOpacity, StyleSheet, ViewStyle, TextStyle } from 'react-native';
import { ThemedText } from '@/components/ThemedText';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';
import { Colors } from '@/constants/Colors';

interface ResponsiveButtonProps {
  title: string;
  onPress: () => void;
  variant?: 'primary' | 'secondary' | 'ghost';
  size?: 'small' | 'medium' | 'large';
  disabled?: boolean;
  fullWidth?: boolean;
  icon?: React.ReactNode;
  style?: ViewStyle;
  textStyle?: TextStyle;
}

const ResponsiveButton: React.FC<ResponsiveButtonProps> = ({
  title,
  onPress,
  variant = 'primary',
  size = 'medium',
  disabled = false,
  fullWidth = false,
  icon,
  style,
  textStyle,
}) => {
  const { deviceType, spacing } = useResponsiveLayout();
  const dynamicStyles = createResponsiveStyles(deviceType, spacing);

  const buttonStyle = [
    dynamicStyles.baseButton,
    dynamicStyles[variant],
    dynamicStyles[size],
    fullWidth && dynamicStyles.fullWidth,
    disabled && dynamicStyles.disabled,
    style,
  ];

  const textStyleCombined = [
    dynamicStyles.baseText,
    dynamicStyles[`${variant}Text`],
    dynamicStyles[`${size}Text`],
    disabled && dynamicStyles.disabledText,
    textStyle,
  ];

  return (
    <TouchableOpacity
      style={buttonStyle}
      onPress={onPress}
      disabled={disabled}
      activeOpacity={0.7}
    >
      {icon && <>{icon}</>}
      <ThemedText style={textStyleCombined}>{title}</ThemedText>
    </TouchableOpacity>
  );
};

const createResponsiveStyles = (deviceType: string, spacing: number) => {
  const isMobile = deviceType === 'mobile';
  const isTablet = deviceType === 'tablet';
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();

  return StyleSheet.create({
    baseButton: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'center',
      borderRadius: isMobile ? 8 : isTablet ? 10 : 12,
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      minHeight: isMobile ? minTouchTarget : isTablet ? 48 : 44,
    },
    
    // Variants
    primary: {
      backgroundColor: Colors.dark.primary,
    },
    secondary: {
      backgroundColor: '#2c2c2e',
      borderWidth: 1,
      borderColor: '#666',
    },
    ghost: {
      backgroundColor: 'transparent',
      borderWidth: 1,
      borderColor: '#666',
    },

    // Sizes
    small: {
      paddingHorizontal: spacing * 0.75,
      paddingVertical: spacing * 0.5,
      minHeight: isMobile ? minTouchTarget * 0.8 : 36,
    },
    medium: {
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      minHeight: isMobile ? minTouchTarget : isTablet ? 48 : 44,
    },
    large: {
      paddingHorizontal: spacing * 1.5,
      paddingVertical: spacing,
      minHeight: isMobile ? minTouchTarget * 1.2 : isTablet ? 56 : 52,
    },

    fullWidth: {
      width: '100%',
    },

    disabled: {
      opacity: 0.5,
    },

    // Text styles
    baseText: {
      textAlign: 'center',
      fontWeight: '600',
    },
    primaryText: {
      color: 'white',
    },
    secondaryText: {
      color: 'white',
    },
    ghostText: {
      color: '#ccc',
    },
    
    // Text sizes
    smallText: {
      fontSize: isMobile ? 14 : 12,
    },
    mediumText: {
      fontSize: isMobile ? 16 : isTablet ? 16 : 14,
    },
    largeText: {
      fontSize: isMobile ? 18 : isTablet ? 18 : 16,
    },

    disabledText: {
      opacity: 0.7,
    },
  });
};

export default ResponsiveButton;