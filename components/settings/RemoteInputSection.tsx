import React, { useCallback } from "react";
import { View, Switch, StyleSheet, Pressable, Animated, Platform, TouchableOpacity } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import { useRemoteControlStore } from "@/stores/remoteControlStore";
import { useButtonAnimation } from "@/hooks/useAnimation";
import { Colors } from "@/constants/Colors";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";

interface RemoteInputSectionProps {
  onChanged: () => void;
  onFocus?: () => void;
  onBlur?: () => void;
  onPress?: () => void;
}

export const RemoteInputSection: React.FC<RemoteInputSectionProps> = ({ onChanged, onFocus, onBlur, onPress }) => {
  const { remoteInputEnabled, setRemoteInputEnabled } = useSettingsStore();
  const { isServerRunning, serverUrl, error } = useRemoteControlStore();
  const [isFocused, setIsFocused] = React.useState(false);
  const animationStyle = useButtonAnimation(isFocused, 1.2);

  const handleToggle = useCallback(
    (enabled: boolean) => {
      setRemoteInputEnabled(enabled);
      onChanged();
    },
    [setRemoteInputEnabled, onChanged]
  );

  const handleSectionFocus = () => {
    setIsFocused(true);
    onFocus?.();
  };

  const handleSectionBlur = () => {
    setIsFocused(false);
    onBlur?.();
  };

  const handlePress = () => {
    handleToggle(!remoteInputEnabled);
  }

  // 触屏设备（平板等）没有遥控器 select 事件，只能靠点击切换；TV 上由 handleTVEvent 处理，避免重复切换
  const touchPress = Platform.isTV ? undefined : handlePress;

  // TV遥控器事件处理
  const handleTVEvent = React.useCallback(
    (event: any) => {
      if (isFocused && event.eventType === "select") {
        handleToggle(!remoteInputEnabled);
      }
    },
    [isFocused, remoteInputEnabled, handleToggle]
  );

  useSafeTVEventHandler(handleTVEvent);

  return (
    <SettingsSection focusable onFocus={handleSectionFocus} onBlur={handleSectionBlur} onPress={touchPress}>
      <Pressable
        style={styles.settingItem}
        onFocus={handleSectionFocus}
        onBlur={handleSectionBlur}
        onPress={touchPress}
      >
        <View style={styles.settingInfo}>
          <ThemedText style={styles.settingName}>启用远程输入</ThemedText>
        </View>
        <Animated.View style={animationStyle}>
          { Platform.OS === 'ios' && Platform.isTV ? (
            <TouchableOpacity
              activeOpacity={0.8}
              onPress={() => handlePress()}
              style={styles.statusLabel}
            >
              <ThemedText style={styles.statusValue}>{remoteInputEnabled ? '已启用' : '已禁用'}</ThemedText>
            </TouchableOpacity>
          ) : (
            <Switch
              value={remoteInputEnabled}
              onValueChange={() => { }} // 禁用Switch的直接交互
              trackColor={{ false: "#767577", true: Colors.dark.primary }}
              thumbColor={remoteInputEnabled ? "#ffffff" : "#f4f3f4"}
              pointerEvents="none"
            />
          )
          }
        </Animated.View>
      </Pressable>

      {remoteInputEnabled && (
        <View style={styles.statusContainer}>
          <View style={styles.statusItem}>
            <ThemedText style={styles.statusLabel}>服务状态：</ThemedText>
            <ThemedText style={[styles.statusValue, { color: isServerRunning ? Colors.dark.primary : "#FF6B6B" }]}>
              {isServerRunning ? "运行中" : "已停止"}
            </ThemedText>
          </View>

          {serverUrl && (
            <View style={styles.statusItem}>
              <ThemedText style={styles.statusLabel}>访问地址：</ThemedText>
              <ThemedText style={styles.statusValue}>{serverUrl}</ThemedText>
            </View>
          )}

          {error && (
            <View style={styles.statusItem}>
              <ThemedText style={styles.statusLabel}>错误：</ThemedText>
              <ThemedText style={[styles.statusValue, { color: "#FF6B6B" }]}>{error}</ThemedText>
            </View>
          )}
        </View>
      )}
    </SettingsSection>
  );
};

const styles = StyleSheet.create({
  settingItem: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingVertical: 12,
  },
  settingInfo: {
    flex: 1,
  },
  settingName: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 4,
  },
  settingDescription: {
    fontSize: 14,
    color: "#888",
  },
  statusContainer: {
    marginTop: 16,
    padding: 16,
    backgroundColor: "#2a2a2c",
    borderRadius: 8,
  },
  statusItem: {
    flexDirection: "row",
    marginBottom: 8,
  },
  statusLabel: {
    fontSize: 14,
    color: "#ccc",
    minWidth: 80,
  },
  statusValue: {
    fontSize: 14,
    flex: 1,
  },
  actionButtons: {
    flexDirection: "row",
    gap: 12,
    marginTop: 12,
  },
  actionButton: {
    flex: 1,
  },
});
