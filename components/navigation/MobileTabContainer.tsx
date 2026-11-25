import React from 'react';
import { View, StyleSheet, TouchableOpacity, Text, Platform } from 'react-native';
import { useRouter, usePathname } from 'expo-router';
import { Home, Search, Heart, Settings, Tv, Menu } from 'lucide-react-native';
import { Colors } from '@/constants/Colors';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';
import useFavoritesStore from '@/stores/favoritesStore';
import { UpdateBadge } from '@/components/UpdateBadge';
import { useNavCollapsed } from './useNavCollapsed';

interface TabItem {
  key: string;
  label: string;
  icon: React.ComponentType<any>;
  route: string;
}

const tabs: TabItem[] = [
  { key: 'home', label: '首页', icon: Home, route: '/' },
  { key: 'search', label: '搜索', icon: Search, route: '/search' },
  { key: 'live', label: '直播', icon: Tv, route: '/live' },
  { key: 'favorites', label: '收藏', icon: Heart, route: '/favorites' },
  { key: 'settings', label: '设置', icon: Settings, route: '/settings' },
];

interface MobileTabContainerProps {
  children: React.ReactNode;
}

const MobileTabContainer: React.FC<MobileTabContainerProps> = ({ children }) => {
  const router = useRouter();
  const pathname = usePathname();
  const { spacing, deviceType } = useResponsiveLayout();
  
  // 在手机端过滤掉直播 tab
  const filteredTabs = tabs.filter(tab => 
    deviceType !== 'mobile' || tab.key !== 'live'
  );
  
  const openFavoritesDrawer = useFavoritesStore((state) => state.openDrawer);

  const handleTabPress = (route: string) => {
    // 收藏入口滑出侧边面板;已经在收藏页时不再叠一层
    if (route === '/favorites' && pathname !== '/favorites') {
      openFavoritesDrawer();
      return;
    }
    if (route === '/') {
      router.push('/');
    } else {
      router.push(route as any);
    }
  };

  const isTabActive = (route: string) => {
    if (route === '/' && pathname === '/') return true;
    if (route !== '/' && pathname === route) return true;
    return false;
  };

  const [collapsed, setCollapsed] = useNavCollapsed('mobile');

  const dynamicStyles = createStyles(spacing);

  return (
    <View style={dynamicStyles.container}>
      {/* 内容区域 */}
      <View style={dynamicStyles.content}>
        {children}
      </View>
      
      {/* 底部导航:悬浮胶囊条,可收起;收起后留一个小按钮恢复(纯外观状态) */}
      {collapsed ? (
        <TouchableOpacity
          style={dynamicStyles.restoreButton}
          testID="mobile-nav-expand"
          accessibilityRole="button"
          accessibilityLabel="展开导航栏"
          onPress={() => setCollapsed(false)}
          activeOpacity={0.8}
        >
          <View>
            <Menu size={20} color={Colors.dark.primary} />
            <UpdateBadge />
          </View>
        </TouchableOpacity>
      ) : (
      <View style={dynamicStyles.tabBarWrap}>
      <TouchableOpacity
        style={dynamicStyles.handleHit}
        testID="mobile-nav-collapse"
        accessibilityRole="button"
        accessibilityLabel="收起导航栏"
        onPress={() => setCollapsed(true)}
        activeOpacity={0.6}
      >
        <View style={dynamicStyles.handle} />
      </TouchableOpacity>
      <View style={dynamicStyles.tabBar}>
        {filteredTabs.map((tab) => {
          const isActive = isTabActive(tab.route);
          const IconComponent = tab.icon;
          
          return (
            <TouchableOpacity
              key={tab.key}
              style={[dynamicStyles.tab, isActive && dynamicStyles.activeTab]}
              testID={`mobile-tab-${tab.key}`}
              onPress={() => handleTabPress(tab.route)}
              activeOpacity={0.7}
            >
              <View>
                <IconComponent
                  size={20}
                  color={isActive ? Colors.dark.primary : '#9AA1B8'}
                  strokeWidth={isActive ? 2.5 : 2}
                />
                {tab.key === 'settings' && <UpdateBadge />}
              </View>
              <Text style={[
                dynamicStyles.tabLabel,
                isActive && dynamicStyles.activeTabLabel
              ]}>
                {tab.label}
              </Text>
            </TouchableOpacity>
          );
        })}
      </View>
      </View>
      )}
    </View>
  );
};

const createStyles = (spacing: number) => {
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();
  
  return StyleSheet.create({
    container: {
      flex: 1,
    },
    content: {
      flex: 1,
    },
    tabBarWrap: {
      backgroundColor: Colors.dark.background,
      paddingHorizontal: spacing * 1.5,
      paddingBottom: Platform.OS === 'ios' ? spacing * 2.5 : spacing,
    },
    handleHit: {
      alignSelf: 'center',
      width: 72,
      height: 16,
      alignItems: 'center',
      justifyContent: 'center',
    },
    handle: {
      width: 36,
      height: 4,
      borderRadius: 999,
      backgroundColor: 'rgba(255,255,255,0.22)',
    },
    tabBar: {
      flexDirection: 'row',
      backgroundColor: '#151828',
      borderWidth: 1,
      borderColor: 'rgba(255,255,255,0.08)',
      borderRadius: 28,
      paddingVertical: spacing / 2,
      paddingHorizontal: spacing,
      shadowColor: '#000',
      shadowOffset: {
        width: 0,
        height: 8,
      },
      shadowOpacity: 0.35,
      shadowRadius: 16,
      elevation: 12,
    },
    restoreButton: {
      position: 'absolute',
      left: spacing * 1.5,
      bottom: Platform.OS === 'ios' ? spacing * 3 : spacing * 1.5,
      width: 44,
      height: 44,
      borderRadius: 22,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: 'rgba(21,24,40,0.94)',
      borderWidth: 1,
      borderColor: 'rgba(111, 220, 168, 0.45)',
      shadowColor: '#000',
      shadowOffset: { width: 0, height: 6 },
      shadowOpacity: 0.35,
      shadowRadius: 12,
      elevation: 12,
      zIndex: 50,
    },
    tab: {
      flex: 1,
      alignItems: 'center',
      justifyContent: 'center',
      minHeight: minTouchTarget,
      paddingVertical: spacing / 2,
      borderRadius: 20,
    },
    activeTab: {
      backgroundColor: 'rgba(111, 220, 168, 0.14)',
    },
    tabLabel: {
      fontSize: 11,
      color: '#9AA1B8',
      marginTop: 2,
      fontWeight: '500',
    },
    activeTabLabel: {
      color: Colors.dark.primary,
      fontWeight: '600',
    },
  });
};

export default MobileTabContainer;
