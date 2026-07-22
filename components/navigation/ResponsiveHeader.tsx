import React from 'react';
import { View, StyleSheet, TouchableOpacity, StatusBar, Platform } from 'react-native';
import { useRouter } from 'expo-router';
import { ArrowLeft } from 'lucide-react-native';
import { ThemedText } from '@/components/ThemedText';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

interface ResponsiveHeaderProps {
  title?: string;
  showBackButton?: boolean;
  rightComponent?: React.ReactNode;
  onBackPress?: () => void;
}

const ResponsiveHeader: React.FC<ResponsiveHeaderProps> = ({
  title,
  showBackButton = false,
  rightComponent,
  onBackPress,
}) => {
  const router = useRouter();
  const { deviceType, spacing } = useResponsiveLayout();
  const insets = useSafeAreaInsets();
  
  // TV端不显示Header，使用现有的页面内导航
  if (deviceType === 'tv') {
    return null;
  }

  const handleBackPress = () => {
    if (onBackPress) {
      onBackPress();
    } else if (router.canGoBack()) {
      router.back();
    }
  };

  const dynamicStyles = createStyles(spacing, deviceType, insets);

  return (
    <>
      {Platform.OS === 'android' && <StatusBar backgroundColor="#1c1c1e" barStyle="light-content" />}
      <View style={dynamicStyles.container}>
        <View style={dynamicStyles.content}>
          {/* 左侧区域 */}
          <View style={dynamicStyles.leftSection}>
            {showBackButton && (
              <TouchableOpacity
                onPress={handleBackPress}
                style={dynamicStyles.backButton}
                activeOpacity={0.7}
              >
                <ArrowLeft size={20} color="#fff" strokeWidth={2} />
              </TouchableOpacity>
            )}
          </View>

          {/* 中间标题区域 */}
          <View style={dynamicStyles.centerSection}>
            {title && (
              <ThemedText style={dynamicStyles.title} numberOfLines={1}>
                {title}
              </ThemedText>
            )}
          </View>

          {/* 右侧区域 */}
          <View style={dynamicStyles.rightSection}>
            {rightComponent}
          </View>
        </View>
      </View>
    </>
  );
};

const createStyles = (spacing: number, deviceType: string, insets: any) => {
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();
  
  return StyleSheet.create({
    container: {
      backgroundColor: '#1c1c1e',
      paddingTop: insets.top,
      borderBottomWidth: 1,
      borderBottomColor: '#333',
      shadowColor: '#000',
      shadowOffset: {
        width: 0,
        height: 2,
      },
      shadowOpacity: 0.1,
      shadowRadius: 3,
      elevation: 5,
    },
    content: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      minHeight: minTouchTarget + spacing,
    },
    leftSection: {
      width: minTouchTarget + spacing,
      justifyContent: 'flex-start',
      alignItems: 'flex-start',
    },
    centerSection: {
      flex: 1,
      justifyContent: 'center',
      alignItems: 'center',
    },
    rightSection: {
      width: minTouchTarget + spacing,
      justifyContent: 'flex-end',
      alignItems: 'flex-end',
      flexDirection: 'row',
    },
    backButton: {
      width: minTouchTarget,
      height: minTouchTarget,
      justifyContent: 'center',
      alignItems: 'center',
      borderRadius: minTouchTarget / 2,
    },
    title: {
      fontSize: DeviceUtils.getOptimalFontSize(deviceType === 'mobile' ? 18 : 20),
      fontWeight: '600',
      color: '#fff',
    },
  });
};

export default ResponsiveHeader;