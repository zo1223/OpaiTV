import { StyleSheet } from 'react-native';
import { useResponsiveLayout, ResponsiveConfig } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';

// 响应式样式创建器类型
export type ResponsiveStyleCreator<T> = (config: ResponsiveConfig) => T;

/**
 * 创建响应式样式的高阶函数
 */
export const createResponsiveStyles = <T extends Record<string, any>>(
  styleCreator: ResponsiveStyleCreator<T>
) => {
  return (config: ResponsiveConfig): T => {
    return StyleSheet.create(styleCreator(config)) as T;
  };
};

/**
 * 响应式样式 Hook
 */
export const useResponsiveStyles = <T extends Record<string, any>>(
  styleCreator: ResponsiveStyleCreator<T>
): T => {
  const config = useResponsiveLayout();
  return createResponsiveStyles(styleCreator)(config);
};

/**
 * 通用响应式样式
 */
export const getCommonResponsiveStyles = (config: ResponsiveConfig) => {
  const { deviceType, spacing } = config;
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();

  return StyleSheet.create({
    // 容器样式
    container: {
      flex: 1,
      paddingHorizontal: spacing,
    },

    safeContainer: {
      flex: 1,
      paddingHorizontal: spacing,
      paddingTop: deviceType === 'mobile' ? 20 : deviceType === 'tablet' ? 30 : 40,
    },

    // 标题样式
    pageTitle: {
      fontSize: DeviceUtils.getOptimalFontSize(deviceType === 'mobile' ? 24 : deviceType === 'tablet' ? 28 : 32),
      fontWeight: 'bold',
      marginBottom: spacing,
      color: 'white',
    },

    sectionTitle: {
      fontSize: DeviceUtils.getOptimalFontSize(deviceType === 'mobile' ? 18 : deviceType === 'tablet' ? 20 : 22),
      fontWeight: '600',
      marginBottom: spacing / 2,
      color: 'white',
    },

    // 按钮样式
    primaryButton: {
      minHeight: minTouchTarget,
      paddingHorizontal: spacing * 1.5,
      paddingVertical: spacing,
      borderRadius: deviceType === 'mobile' ? 8 : deviceType === 'tablet' ? 10 : 12,
      justifyContent: 'center',
      alignItems: 'center',
    },

    secondaryButton: {
      minHeight: minTouchTarget,
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      borderRadius: deviceType === 'mobile' ? 6 : deviceType === 'tablet' ? 8 : 10,
      justifyContent: 'center',
      alignItems: 'center',
      borderWidth: 1,
      borderColor: '#ccc',
    },

    // 输入框样式
    textInput: {
      minHeight: minTouchTarget,
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      borderRadius: deviceType === 'mobile' ? 8 : deviceType === 'tablet' ? 10 : 12,
      fontSize: DeviceUtils.getOptimalFontSize(16),
      backgroundColor: '#2c2c2e',
      color: 'white',
      borderWidth: 2,
      borderColor: 'transparent',
    },

    // 卡片样式
    card: {
      backgroundColor: '#1c1c1e',
      borderRadius: deviceType === 'mobile' ? 8 : deviceType === 'tablet' ? 10 : 12,
      padding: spacing,
      marginBottom: spacing,
    },

    // 网格样式
    gridContainer: {
      paddingHorizontal: spacing / 2,
    },

    gridRow: {
      flexDirection: 'row',
      justifyContent: 'flex-start',
      flexWrap: 'wrap',
    },

    gridItem: {
      margin: spacing / 2,
      alignItems: 'center',
    },

    // 间距工具类
    marginTopSmall: { marginTop: spacing / 2 },
    marginTopMedium: { marginTop: spacing },
    marginTopLarge: { marginTop: spacing * 1.5 },

    marginBottomSmall: { marginBottom: spacing / 2 },
    marginBottomMedium: { marginBottom: spacing },
    marginBottomLarge: { marginBottom: spacing * 1.5 },

    paddingSmall: { padding: spacing / 2 },
    paddingMedium: { padding: spacing },
    paddingLarge: { padding: spacing * 1.5 },

    // 布局工具类
    row: {
      flexDirection: 'row',
      alignItems: 'center',
    },

    rowBetween: {
      flexDirection: 'row',
      justifyContent: 'space-between',
      alignItems: 'center',
    },

    column: {
      flexDirection: 'column',
    },

    center: {
      justifyContent: 'center',
      alignItems: 'center',
    },

    centerHorizontal: {
      alignItems: 'center',
    },

    centerVertical: {
      justifyContent: 'center',
    },

    // 文本样式
    textSmall: {
      fontSize: DeviceUtils.getOptimalFontSize(12),
      color: '#ccc',
    },

    textMedium: {
      fontSize: DeviceUtils.getOptimalFontSize(14),
      color: 'white',
    },

    textLarge: {
      fontSize: DeviceUtils.getOptimalFontSize(16),
      color: 'white',
    },

    // 阴影样式
    shadow: deviceType !== 'tv' ? {
      shadowColor: '#000',
      shadowOffset: {
        width: 0,
        height: 2,
      },
      shadowOpacity: 0.25,
      shadowRadius: 3.84,
      elevation: 5,
    } : {}, // TV端不需要阴影
  });
};

/**
 * 响应式文本大小
 */
export const getResponsiveTextSize = (baseSize: number, deviceType: string) => {
  const scaleFactors = {
    mobile: 1.0,
    tablet: 1.1,
    tv: 1.25,
  };
  
  const scaleFactor = scaleFactors[deviceType as keyof typeof scaleFactors] || 1.0;
  
  return Math.round(baseSize * scaleFactor);
};

/**
 * 响应式间距
 */
export const getResponsiveSpacing = (baseSpacing: number, deviceType: string) => {
  const scaleFactors = {
    mobile: 0.8,
    tablet: 1.0,
    tv: 1.5,
  };
  
  const scaleFactor = scaleFactors[deviceType as keyof typeof scaleFactors] || 1.0;
  
  return Math.round(baseSpacing * scaleFactor);
};