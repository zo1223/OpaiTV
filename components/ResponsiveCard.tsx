import React from 'react';
import { View, StyleSheet, ViewStyle, TouchableOpacity } from 'react-native';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';

interface ResponsiveCardProps {
  children: React.ReactNode;
  onPress?: () => void;
  variant?: 'default' | 'elevated' | 'outlined';
  padding?: 'small' | 'medium' | 'large';
  style?: ViewStyle;
  disabled?: boolean;
}

const ResponsiveCard: React.FC<ResponsiveCardProps> = ({
  children,
  onPress,
  variant = 'default',
  padding = 'medium',
  style,
  disabled = false,
}) => {
  const { deviceType, spacing } = useResponsiveLayout();
  const dynamicStyles = createResponsiveStyles(deviceType, spacing);

  const cardStyle = [
    dynamicStyles.baseCard,
    dynamicStyles[variant],
    dynamicStyles[padding],
    disabled && dynamicStyles.disabled,
    style,
  ];

  if (onPress && !disabled) {
    return (
      <TouchableOpacity
        style={cardStyle}
        onPress={onPress}
        activeOpacity={0.8}
      >
        {children}
      </TouchableOpacity>
    );
  }

  return <View style={cardStyle}>{children}</View>;
};

const createResponsiveStyles = (deviceType: string, spacing: number) => {
  const isMobile = deviceType === 'mobile';
  const isTablet = deviceType === 'tablet';

  return StyleSheet.create({
    baseCard: {
      backgroundColor: '#1c1c1e',
      borderRadius: isMobile ? 8 : isTablet ? 10 : 12,
      marginBottom: spacing,
    },

    // Variants
    default: {
      backgroundColor: '#1c1c1e',
    },
    elevated: {
      backgroundColor: '#1c1c1e',
      shadowColor: '#000',
      shadowOffset: {
        width: 0,
        height: isMobile ? 2 : isTablet ? 4 : 6,
      },
      shadowOpacity: isMobile ? 0.1 : isTablet ? 0.15 : 0.2,
      shadowRadius: isMobile ? 4 : isTablet ? 6 : 8,
      elevation: isMobile ? 3 : isTablet ? 5 : 8,
    },
    outlined: {
      backgroundColor: 'transparent',
      borderWidth: 1,
      borderColor: '#333',
    },

    // Padding variants
    small: {
      padding: spacing * 0.75,
    },
    medium: {
      padding: spacing,
    },
    large: {
      padding: spacing * 1.5,
    },

    disabled: {
      opacity: 0.5,
    },
  });
};

export default ResponsiveCard;