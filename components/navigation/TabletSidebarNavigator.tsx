import React from 'react';
import { View, StyleSheet, TouchableOpacity, Text, ScrollView } from 'react-native';
import { useRouter, usePathname } from 'expo-router';
import { Home, Search, Heart, Settings, Tv, Menu, X } from 'lucide-react-native';
import { Colors } from '@/constants/Colors';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';
import { ThemedText } from '@/components/ThemedText';
import useFavoritesStore from '@/stores/favoritesStore';
import { UpdateBadge } from '@/components/UpdateBadge';
import { useNavCollapsed } from './useNavCollapsed';

interface SidebarItem {
  key: string;
  label: string;
  icon: React.ComponentType<any>;
  route: string;
  section?: string;
}

const sidebarItems: SidebarItem[] = [
  { key: 'home', label: '首页', icon: Home, route: '/', section: 'main' },
  { key: 'search', label: '搜索', icon: Search, route: '/search', section: 'main' },
  { key: 'live', label: '直播', icon: Tv, route: '/live', section: 'main' },
  { key: 'favorites', label: '收藏', icon: Heart, route: '/favorites', section: 'user' },
  { key: 'settings', label: '设置', icon: Settings, route: '/settings', section: 'user' },
];

interface TabletSidebarNavigatorProps {
  children: React.ReactNode;
  collapsed?: boolean;
  onToggleCollapse?: (collapsed: boolean) => void;
}

const TabletSidebarNavigator: React.FC<TabletSidebarNavigatorProps> = ({
  children,
  collapsed: controlledCollapsed,
  onToggleCollapse,
}) => {
  const router = useRouter();
  const pathname = usePathname();
  const { spacing, isPortrait } = useResponsiveLayout();
  
  // 折叠状态只是外观状态,记在本机(mytv_nav_collapsed),读写失败静默
  const [internalCollapsed, setInternalCollapsed] = useNavCollapsed('tablet');
  
  // 使用外部控制的collapsed状态，如果没有则使用内部状态
  const collapsed = controlledCollapsed !== undefined ? controlledCollapsed : internalCollapsed;
  
  const handleToggleCollapse = () => {
    if (onToggleCollapse) {
      onToggleCollapse(!collapsed);
    } else {
      setInternalCollapsed(!collapsed);
    }
  };

  const openFavoritesDrawer = useFavoritesStore((state) => state.openDrawer);

  const handleItemPress = (route: string) => {
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
    
    // 在竖屏模式下，导航后自动折叠侧边栏
    if (isPortrait && !controlledCollapsed) {
      setInternalCollapsed(true, false);
    }
  };

  const isItemActive = (route: string) => {
    if (route === '/' && pathname === '/') return true;
    if (route !== '/' && pathname === route) return true;
    return false;
  };

  const sidebarWidth = collapsed ? 64 : 208;
  const dynamicStyles = createStyles(spacing, sidebarWidth, isPortrait);

  const renderSidebarItems = () => {
    const sections = ['main', 'user'];
    
    return sections.map((section) => {
      const sectionItems = sidebarItems.filter(item => item.section === section);
      
      return (
        <View key={section} style={dynamicStyles.section}>
          {!collapsed && (
            <ThemedText style={dynamicStyles.sectionTitle}>
              {section === 'main' ? '主要功能' : '用户'}
            </ThemedText>
          )}
          {sectionItems.map((item) => {
            const isActive = isItemActive(item.route);
            const IconComponent = item.icon;
            
            return (
              <TouchableOpacity
                key={item.key}
                style={[dynamicStyles.sidebarItem, isActive && dynamicStyles.activeSidebarItem]}
                testID={`sidebar-item-${item.key}`}
                onPress={() => handleItemPress(item.route)}
                activeOpacity={0.7}
              >
                <View>
                  <IconComponent
                    size={20}
                    color={isActive ? Colors.dark.primary : '#C5CADB'}
                    strokeWidth={isActive ? 2.5 : 2}
                  />
                  {item.key === 'settings' && <UpdateBadge />}
                </View>
                {!collapsed && (
                  <Text style={[
                    dynamicStyles.sidebarItemLabel,
                    isActive && dynamicStyles.activeSidebarItemLabel
                  ]}>
                    {item.label}
                  </Text>
                )}
              </TouchableOpacity>
            );
          })}
        </View>
      );
    });
  };

  return (
    <View style={dynamicStyles.container}>
      {/* 侧边栏 */}
      <View style={[dynamicStyles.sidebar, collapsed && dynamicStyles.collapsedSidebar]}>
        {/* 侧边栏头部 */}
        <View style={dynamicStyles.sidebarHeader}>
          <TouchableOpacity
            onPress={handleToggleCollapse}
            style={dynamicStyles.toggleButton}
            testID="sidebar-toggle"
            accessibilityRole="button"
            accessibilityLabel={collapsed ? '展开侧栏' : '收起侧栏'}
            activeOpacity={0.7}
          >
            {collapsed ? (
              <Menu size={20} color="#C5CADB" />
            ) : (
              <X size={20} color="#C5CADB" />
            )}
          </TouchableOpacity>
          {!collapsed && (
            <ThemedText style={dynamicStyles.appTitle}>OpaiTV</ThemedText>
          )}
        </View>

        {/* 侧边栏内容 */}
        <ScrollView style={dynamicStyles.sidebarContent} showsVerticalScrollIndicator={false}>
          {renderSidebarItems()}
        </ScrollView>
      </View>

      {/* 主内容区域 */}
      <View style={dynamicStyles.content}>
        {children}
      </View>
    </View>
  );
};

const createStyles = (spacing: number, sidebarWidth: number, isPortrait: boolean) => {
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();
  
  return StyleSheet.create({
    container: {
      flex: 1,
      flexDirection: 'row',
    },
    sidebar: {
      width: sidebarWidth,
      backgroundColor: '#151828',
      borderRightWidth: 1,
      borderRightColor: '#2A2F47',
      zIndex: isPortrait ? 1000 : 1, // 在竖屏时提高层级
    },
    collapsedSidebar: {
      width: 64,
    },
    sidebarHeader: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: spacing,
      paddingVertical: spacing * 1.5,
      borderBottomWidth: 1,
      borderBottomColor: 'rgba(255,255,255,0.06)',
    },
    toggleButton: {
      width: minTouchTarget,
      height: minTouchTarget,
      justifyContent: 'center',
      alignItems: 'center',
      borderRadius: 14,
      backgroundColor: 'rgba(255,255,255,0.05)',
    },
    appTitle: {
      fontSize: 18,
      fontWeight: '800',
      letterSpacing: 0.5,
      marginLeft: spacing,
      color: Colors.dark.primary,
    },
    sidebarContent: {
      flex: 1,
      paddingTop: spacing,
    },
    section: {
      marginBottom: spacing * 1.5,
    },
    sectionTitle: {
      fontSize: 12,
      color: '#9AA1B8',
      fontWeight: '700',
      letterSpacing: 1,
      textTransform: 'uppercase',
      marginBottom: spacing / 2,
      marginHorizontal: spacing,
    },
    sidebarItem: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      marginHorizontal: spacing / 2,
      marginBottom: 2,
      borderRadius: 14,
      minHeight: minTouchTarget,
    },
    activeSidebarItem: {
      backgroundColor: 'rgba(111, 220, 168, 0.15)',
    },
    sidebarItemLabel: {
      fontSize: 14,
      color: '#C5CADB',
      marginLeft: spacing,
      fontWeight: '500',
    },
    activeSidebarItemLabel: {
      color: Colors.dark.primary,
      fontWeight: '600',
    },
    content: {
      flex: 1,
      backgroundColor: Colors.dark.background,
    },
  });
};

export default TabletSidebarNavigator;
