import React, { useState } from 'react';
import { View, StyleSheet, TouchableOpacity, Text, ScrollView } from 'react-native';
import { useRouter, usePathname } from 'expo-router';
import { Home, Search, Heart, Settings, Tv, Menu, X } from 'lucide-react-native';
import { Colors } from '@/constants/Colors';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';
import { ThemedText } from '@/components/ThemedText';

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
  
  const [internalCollapsed, setInternalCollapsed] = useState(false);
  
  // 使用外部控制的collapsed状态，如果没有则使用内部状态
  const collapsed = controlledCollapsed !== undefined ? controlledCollapsed : internalCollapsed;
  
  const handleToggleCollapse = () => {
    if (onToggleCollapse) {
      onToggleCollapse(!collapsed);
    } else {
      setInternalCollapsed(!collapsed);
    }
  };

  const handleItemPress = (route: string) => {
    if (route === '/') {
      router.push('/');
    } else {
      router.push(route as any);
    }
    
    // 在竖屏模式下，导航后自动折叠侧边栏
    if (isPortrait && !controlledCollapsed) {
      setInternalCollapsed(true);
    }
  };

  const isItemActive = (route: string) => {
    if (route === '/' && pathname === '/') return true;
    if (route !== '/' && pathname === route) return true;
    return false;
  };

  const sidebarWidth = collapsed ? 60 : 200;
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
                onPress={() => handleItemPress(item.route)}
                activeOpacity={0.7}
              >
                <IconComponent
                  size={20}
                  color={isActive ? Colors.dark.primary : '#ccc'}
                  strokeWidth={isActive ? 2.5 : 2}
                />
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
            activeOpacity={0.7}
          >
            {collapsed ? (
              <Menu size={20} color="#ccc" />
            ) : (
              <X size={20} color="#ccc" />
            )}
          </TouchableOpacity>
          {!collapsed && (
            <ThemedText style={dynamicStyles.appTitle}>OrionTV</ThemedText>
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
      backgroundColor: '#1c1c1e',
      borderRightWidth: 1,
      borderRightColor: '#333',
      zIndex: isPortrait ? 1000 : 1, // 在竖屏时提高层级
    },
    collapsedSidebar: {
      width: 60,
    },
    sidebarHeader: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: spacing,
      paddingVertical: spacing * 1.5,
      borderBottomWidth: 1,
      borderBottomColor: '#333',
    },
    toggleButton: {
      width: minTouchTarget,
      height: minTouchTarget,
      justifyContent: 'center',
      alignItems: 'center',
      borderRadius: 8,
    },
    appTitle: {
      fontSize: 18,
      fontWeight: 'bold',
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
      color: '#888',
      fontWeight: '600',
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
      borderRadius: 8,
      minHeight: minTouchTarget,
    },
    activeSidebarItem: {
      backgroundColor: 'rgba(64, 156, 255, 0.15)',
    },
    sidebarItemLabel: {
      fontSize: 14,
      color: '#ccc',
      marginLeft: spacing,
      fontWeight: '500',
    },
    activeSidebarItemLabel: {
      color: Colors.dark.primary,
      fontWeight: '600',
    },
    content: {
      flex: 1,
      backgroundColor: '#000',
    },
  });
};

export default TabletSidebarNavigator;
