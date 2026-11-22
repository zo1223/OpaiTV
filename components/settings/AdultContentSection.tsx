import React, { useCallback, useState } from "react";
import { Platform, StyleSheet, Switch, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { Colors } from "@/constants/Colors";

interface AdultContentSectionProps {
  onFocus?: () => void;
  onBlur?: () => void;
}

// 成人内容(18+)开关。切换后立即生效并保存(见 settingsStore.setShowAdultContent),不需要再点"保存设置"
export const AdultContentSection: React.FC<AdultContentSectionProps> = ({ onFocus, onBlur }) => {
  const { showAdultContent, setShowAdultContent } = useSettingsStore();
  const [isFocused, setIsFocused] = useState(false);

  const handleToggle = useCallback(() => {
    void setShowAdultContent(!showAdultContent);
  }, [showAdultContent, setShowAdultContent]);

  const handleSectionFocus = () => {
    setIsFocused(true);
    onFocus?.();
  };

  const handleSectionBlur = () => {
    setIsFocused(false);
    onBlur?.();
  };

  useSafeTVEventHandler(
    useCallback(
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
      // 与"自动跳过广告"相同:TV 上由遥控器 select 事件切换,触屏设备靠点击
      onPress={Platform.isTV ? undefined : handleToggle}
    >
      <View style={styles.item}>
        <View style={styles.info}>
          <ThemedText style={styles.name}>成人内容(18+)</ThemedText>
          <ThemedText style={[styles.status, showAdultContent && styles.statusOn]}>
            {showAdultContent
              ? "已开启:成人源及其内容正常显示"
              : "已关闭:成人源及其内容已隐藏(源列表、首页、搜索、推荐、观看历史)"}
          </ThemedText>
        </View>
        <Switch
          value={showAdultContent}
          onValueChange={handleToggle}
          trackColor={{ false: "#767577", true: Colors.dark.primary }}
          thumbColor={showAdultContent ? "#ffffff" : "#f4f3f4"}
          pointerEvents={Platform.isTV ? "none" : "auto"}
        />
      </View>
    </SettingsSection>
  );
};

const styles = StyleSheet.create({
  item: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingVertical: 12,
  },
  info: {
    flex: 1,
    marginRight: 12,
  },
  name: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 4,
  },
  status: {
    fontSize: 13,
    color: "#888",
  },
  statusOn: {
    color: "#ff9f43",
  },
});
