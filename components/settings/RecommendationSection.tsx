import React, { useCallback, useState } from "react";
import { Platform, StyleSheet, Switch, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { Colors } from "@/constants/Colors";

interface RecommendationSectionProps {
  onFocus?: () => void;
  onBlur?: () => void;
}

// 首页"为你推荐"开关,默认开。切换后立即生效并保存(见 settingsStore.setRecommendationsEnabled)
export const RecommendationSection: React.FC<RecommendationSectionProps> = ({ onFocus, onBlur }) => {
  const { recommendationsEnabled, setRecommendationsEnabled } = useSettingsStore();
  const [isFocused, setIsFocused] = useState(false);

  const handleToggle = useCallback(() => {
    void setRecommendationsEnabled(!recommendationsEnabled);
  }, [recommendationsEnabled, setRecommendationsEnabled]);

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
          <ThemedText style={styles.name}>个性化推荐</ThemedText>
          <ThemedText style={[styles.status, recommendationsEnabled && styles.statusOn]}>
            {recommendationsEnabled
              ? "已开启:首页显示“为你推荐”。仅在本机根据观看历史与收藏计算,不联网、不上传观看记录"
              : "已关闭:首页不显示“为你推荐”"}
          </ThemedText>
        </View>
        <Switch
          value={recommendationsEnabled}
          onValueChange={handleToggle}
          trackColor={{ false: "#2A3048", true: Colors.dark.primary }}
          thumbColor={recommendationsEnabled ? "#ffffff" : "#f4f3f4"}
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
    color: "#9AA1B8",
  },
  statusOn: {
    color: "#9AA1B8",
  },
});
