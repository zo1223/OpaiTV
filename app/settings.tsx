import React, { useState, useEffect, useRef } from "react";
import { View, StyleSheet, Alert, Platform, Switch } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { ThemedText } from "@/components/ThemedText";
import { ThemedView } from "@/components/ThemedView";
import { StyledButton } from "@/components/StyledButton";
import { useThemeColor } from "@/hooks/useThemeColor";
import { useSettingsStore } from "@/stores/settingsStore";
// import useAuthStore from "@/stores/authStore";
import { useRemoteControlStore } from "@/stores/remoteControlStore";
import { APIConfigSection } from "@/components/settings/APIConfigSection";
import { LiveStreamSection } from "@/components/settings/LiveStreamSection";
import { VideoSourceSubscriptionSection } from "@/components/settings/VideoSourceSubscriptionSection";
import { RemoteInputSection } from "@/components/settings/RemoteInputSection";
import { SettingsSection } from "@/components/settings/SettingsSection";
import { UpdateSection } from "@/components/settings/UpdateSection";
// import { VideoSourceSection } from "@/components/settings/VideoSourceSection";
import Toast from "react-native-toast-message";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { getCommonResponsiveStyles } from "@/utils/ResponsiveStyles";
import ResponsiveNavigation from "@/components/navigation/ResponsiveNavigation";
import ResponsiveHeader from "@/components/navigation/ResponsiveHeader";
import { DeviceUtils } from "@/utils/DeviceUtils";
import { KeyboardAwareScrollView } from 'react-native-keyboard-aware-scroll-view';
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { Colors } from "@/constants/Colors";

function AutoSkipAdsSection({
  onChanged,
  onFocus,
  onBlur,
}: {
  onChanged: () => void;
  onFocus?: () => void;
  onBlur?: () => void;
}) {
  const { autoSkipAdsEnabled, setAutoSkipAdsEnabled } = useSettingsStore();
  const [isFocused, setIsFocused] = useState(false);

  const handleToggle = React.useCallback(() => {
    setAutoSkipAdsEnabled(!autoSkipAdsEnabled);
    onChanged();
  }, [autoSkipAdsEnabled, onChanged, setAutoSkipAdsEnabled]);

  const handleSectionFocus = () => {
    setIsFocused(true);
    onFocus?.();
  };

  const handleSectionBlur = () => {
    setIsFocused(false);
    onBlur?.();
  };

  useSafeTVEventHandler(
    React.useCallback(
      (event: any) => {
        if (isFocused && event.eventType === "select") {
          handleToggle();
        }
      },
      [handleToggle, isFocused]
    )
  );

  return (
    <SettingsSection
      focusable
      onFocus={handleSectionFocus}
      onBlur={handleSectionBlur}
      // 只按 Platform.isTV 判断:宽度 >= 1024dp 的触屏设备(横屏平板)deviceType 也是 "tv",但只能靠点击切换
      onPress={Platform.isTV ? undefined : handleToggle}
    >
      <View style={autoSkipAdsStyles.item}>
        <View style={autoSkipAdsStyles.info}>
          <ThemedText style={autoSkipAdsStyles.name}>{"\u81ea\u52a8\u8df3\u8fc7\u5e7f\u544a"}</ThemedText>
        </View>
        <Switch
          value={autoSkipAdsEnabled}
          onValueChange={handleToggle}
          trackColor={{ false: "#767577", true: Colors.dark.primary }}
          thumbColor={autoSkipAdsEnabled ? "#ffffff" : "#f4f3f4"}
          pointerEvents={Platform.isTV ? "none" : "auto"}
        />
      </View>
    </SettingsSection>
  );
}

const autoSkipAdsStyles = StyleSheet.create({
  item: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingVertical: 12,
  },
  info: {
    flex: 1,
  },
  name: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 4,
  },
});

type SectionItem = {
  component: React.ReactElement;
  key: string;
};

/** 过滤掉 false/undefined，帮 TypeScript 推断出真正的数组元素类型 */
function isSectionItem(
  item: false | undefined | SectionItem
): item is SectionItem {
  return !!item;
}

export default function SettingsScreen() {
  const { loadSettings, saveSettings, setApiBaseUrl, setM3uUrl, setVideoSourceSubscriptionUrl } = useSettingsStore();
  const { lastMessage, targetPage, clearMessage } = useRemoteControlStore();
  const backgroundColor = useThemeColor({}, "background");
  const insets = useSafeAreaInsets();

  // 响应式布局配置
  const responsiveConfig = useResponsiveLayout();
  const commonStyles = getCommonResponsiveStyles(responsiveConfig);
  const { deviceType, spacing } = responsiveConfig;

  const [hasChanges, setHasChanges] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [currentFocusIndex, setCurrentFocusIndex] = useState(0);
  const [currentSection, setCurrentSection] = useState<string | null>(null);

  const saveButtonRef = useRef<any>(null);
  const apiSectionRef = useRef<any>(null);
  const videoSourceSubscriptionSectionRef = useRef<any>(null);
  const liveStreamSectionRef = useRef<any>(null);

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  useEffect(() => {
    if (lastMessage && !targetPage) {
      const realMessage = lastMessage.text;
      handleRemoteInput(realMessage);
      clearMessage(); // Clear the message after processing
      markAsChanged();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lastMessage, targetPage]);

  const handleRemoteInput = (message: string) => {
    // Handle remote input based on currently focused section
    if (currentSection === "api" && apiSectionRef.current) {
      // API Config Section
      setApiBaseUrl(message);
    } else if (currentSection === "videoSourceSubscription" && videoSourceSubscriptionSectionRef.current) {
      setVideoSourceSubscriptionUrl(message);
    } else if (currentSection === "livestream" && liveStreamSectionRef.current) {
      // Live Stream Section
      setM3uUrl(message);
    }
  };

  const handleSave = async () => {
    setIsLoading(true);
    try {
      await saveSettings();
      setHasChanges(false);
      Toast.show({
        type: "success",
        text1: "保存成功",
      });
    } catch {
      Alert.alert("错误", "保存设置失败");
    } finally {
      setIsLoading(false);
    }
  };

  const markAsChanged = () => {
    setHasChanges(true);
  };

  // const sections = [
  //   // 远程输入配置 - 仅在非手机端显示
  //   deviceType !== "mobile" && {
  //     component: (
  //       <RemoteInputSection
  //         onChanged={markAsChanged}
  //         onFocus={() => {
  //           setCurrentFocusIndex(0);
  //           setCurrentSection("remote");
  //         }}
  //       />
  //     ),
  //     key: "remote",
  //   },
  //   {
  //     component: (
  //       <APIConfigSection
  //         ref={apiSectionRef}
  //         onChanged={markAsChanged}
  //         hideDescription={deviceType === "mobile"}
  //         onFocus={() => {
  //           setCurrentFocusIndex(1);
  //           setCurrentSection("api");
  //         }}
  //       />
  //     ),
  //     key: "api",
  //   },
  //   // 直播源配置 - 仅在非手机端显示
  //   deviceType !== "mobile" && {
  //     component: (
  //       <LiveStreamSection
  //         ref={liveStreamSectionRef}
  //         onChanged={markAsChanged}
  //         onFocus={() => {
  //           setCurrentFocusIndex(2);
  //           setCurrentSection("livestream");
  //         }}
  //       />
  //     ),
  //     key: "livestream",
  //   },
  //   // {
  //   //   component: (
  //   //     <VideoSourceSection
  //   //       onChanged={markAsChanged}
  //   //       onFocus={() => {
  //   //         setCurrentFocusIndex(3);
  //   //         setCurrentSection("videoSource");
  //   //       }}
  //   //     />
  //   //   ),
  //   //   key: "videoSource",
  //   // },
  //   Platform.OS === "android" && {
  //     component: <UpdateSection />,
  //     key: "update",
  //   },
  // ].filter(Boolean);
  const rawSections = [
    deviceType !== "mobile" && {
      component: (
        <RemoteInputSection
          onChanged={markAsChanged}
          onFocus={() => {
            setCurrentFocusIndex(0);
            setCurrentSection("remote");
          }}
        />
      ),
      key: "remote",
    },
    {
      component: (
        <APIConfigSection
          ref={apiSectionRef}
          onChanged={markAsChanged}
          hideDescription={deviceType === "mobile"}
          onFocus={() => {
            setCurrentFocusIndex(1);
            setCurrentSection("api");
          }}
        />
      ),
      key: "api",
    },
    {
      component: (
        <VideoSourceSubscriptionSection
          ref={videoSourceSubscriptionSectionRef}
          onChanged={markAsChanged}
          onFocus={() => {
            setCurrentFocusIndex(2);
            setCurrentSection("videoSourceSubscription");
          }}
        />
      ),
      key: "videoSourceSubscription",
    },
    {
      component: (
        <AutoSkipAdsSection
          onChanged={markAsChanged}
          onFocus={() => {
            setCurrentFocusIndex(3);
            setCurrentSection("autoSkipAds");
          }}
        />
      ),
      key: "autoSkipAds",
    },
    deviceType !== "mobile" && {
      component: (
        <LiveStreamSection
          ref={liveStreamSectionRef}
          onChanged={markAsChanged}
          onFocus={() => {
            setCurrentFocusIndex(4);
            setCurrentSection("livestream");
          }}
        />
      ),
      key: "livestream",
    },
    Platform.OS === "android" && {
      component: <UpdateSection />,
      key: "update",
    },
  ] as const; // 把每个对象都当作字面量保留
  /** 这里得到的 sections 已经是 SectionItem[]（没有 false） */
  const sections: SectionItem[] = rawSections.filter(isSectionItem);


  // TV遥控器事件处理 - 仅在TV设备上启用
  const handleTVEvent = React.useCallback(
    (event: any) => {
      if (deviceType !== "tv") return;

      if (event.eventType === "down") {
        const nextIndex = Math.min(currentFocusIndex + 1, sections.length);
        setCurrentFocusIndex(nextIndex);
        if (nextIndex === sections.length) {
          saveButtonRef.current?.focus();
        }
      } else if (event.eventType === "up") {
        const prevIndex = Math.max(currentFocusIndex - 1, 0);
        setCurrentFocusIndex(prevIndex);
      }
    },
    [currentFocusIndex, sections.length, deviceType]
  );

  useSafeTVEventHandler(deviceType === "tv" ? handleTVEvent : () => { });

  // 动态样式
  const dynamicStyles = createResponsiveStyles(deviceType, spacing, insets);

  const renderSettingsContent = () => (
    // <KeyboardAvoidingView style={{ flex: 1, backgroundColor }} behavior={Platform.OS === "ios" ? "padding" : "height"}>
    <KeyboardAwareScrollView
      enableOnAndroid={true}
      extraScrollHeight={20}
      keyboardOpeningTime={0}
      keyboardShouldPersistTaps="always"
      scrollEnabled={true}
      style={{ flex: 1, backgroundColor }}
    >

      <ThemedView style={[commonStyles.container, dynamicStyles.container]}>
        {deviceType === "tv" && (
          <View style={dynamicStyles.header}>
            <ThemedText style={dynamicStyles.title}>设置</ThemedText>
          </View>
        )}

        {/* <View style={dynamicStyles.scrollView}>
          <FlatList
            data={sections}
            renderItem={({ item }) => {
              if (item) {
                return item.component;
              }
              return null;
            }}
            keyExtractor={(item) => (item ? item.key : "default")}
            showsVerticalScrollIndicator={false}
            contentContainerStyle={dynamicStyles.listContent}
          />
        </View> */}
        <View style={dynamicStyles.scrollView}>
          {sections.map(item => (
            // 必须把 key 放在最外层的 View 上
            <View key={item.key} style={dynamicStyles.itemWrapper}>
              {item.component}
            </View>
          ))}
        </View>

        <View style={dynamicStyles.footer}>
          <StyledButton
            text={isLoading ? "保存中..." : "保存设置"}
            onPress={handleSave}
            variant="primary"
            disabled={!hasChanges || isLoading}
            style={[dynamicStyles.saveButton, (!hasChanges || isLoading) && dynamicStyles.disabledButton]}
          />
        </View>
      </ThemedView>
    </KeyboardAwareScrollView>
    // </KeyboardAvoidingView>
  );

  // 根据设备类型决定是否包装在响应式导航中
  if (deviceType === "tv") {
    return renderSettingsContent();
  }

  return (
    <ResponsiveNavigation>
      <ResponsiveHeader title="设置" showBackButton />
      {renderSettingsContent()}
    </ResponsiveNavigation>
  );
}

const createResponsiveStyles = (deviceType: string, spacing: number, insets: any) => {
  const isMobile = deviceType === "mobile";
  const isTablet = deviceType === "tablet";
  const isTV = deviceType === "tv";
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();

  return StyleSheet.create({
    container: {
      flex: 1,
      padding: spacing,
      paddingTop: isTV ? spacing * 2 : isMobile ? insets.top + spacing : insets.top + spacing * 1.5,
    },
    header: {
      flexDirection: "row",
      justifyContent: "space-between",
      alignItems: "center",
      marginBottom: spacing,
    },
    title: {
      fontSize: isMobile ? 24 : isTablet ? 28 : 32,
      fontWeight: "bold",
      paddingTop: spacing,
      color: "white",
    },
    scrollView: {
      flex: 1,
    },
    listContent: {
      paddingBottom: spacing,
    },
    footer: {
      paddingTop: spacing,
      alignItems: isMobile ? "center" : "flex-end",
    },
    saveButton: {
      minHeight: isMobile ? minTouchTarget : isTablet ? 50 : 50,
      width: isMobile ? "100%" : isTablet ? 140 : 120,
      maxWidth: isMobile ? 280 : undefined,
    },
    disabledButton: {
      opacity: 0.5,
    },
    itemWrapper: {
      marginBottom: spacing,   // 这里的 spacing 来自 useResponsiveLayout()
    },
  });
};
