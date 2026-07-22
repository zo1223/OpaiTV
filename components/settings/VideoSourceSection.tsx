import React, { useState, useCallback } from "react";
import { StyleSheet, Switch, FlatList, Pressable, Animated } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import useSourceStore, { useSources } from "@/stores/sourceStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";

interface VideoSourceSectionProps {
  onChanged: () => void;
  onFocus?: () => void;
  onBlur?: () => void;
}

export const VideoSourceSection: React.FC<VideoSourceSectionProps> = ({ onChanged, onFocus, onBlur }) => {
  const [focusedIndex, setFocusedIndex] = useState<number | null>(null);
  const [isSectionFocused, setIsSectionFocused] = useState(false);
  const { videoSource } = useSettingsStore();
  const resources = useSources();
  const { toggleResourceEnabled } = useSourceStore();

  const handleToggle = useCallback(
    (resourceKey: string) => {
      toggleResourceEnabled(resourceKey);
      onChanged();
    },
    [onChanged, toggleResourceEnabled]
  );

  const handleSectionFocus = () => {
    setIsSectionFocused(true);
    onFocus?.();
  };

  const handleSectionBlur = () => {
    setIsSectionFocused(false);
    setFocusedIndex(null);
    onBlur?.();
  };

  // TV遥控器事件处理
  const handleTVEvent = useCallback(
    (event: any) => {
      if (event.eventType === "select") {
        if (focusedIndex !== null) {
          const resource = resources[focusedIndex];
          if (resource) {
            handleToggle(resource.source);
          }
        } else if (isSectionFocused) {
          setFocusedIndex(0);
        }
      }
    },
    [isSectionFocused, focusedIndex, resources, handleToggle]
  );

  useSafeTVEventHandler(handleTVEvent);

  const renderResourceItem = ({ item, index }: { item: { source: string; source_name: string }; index: number }) => {
    const isEnabled = videoSource.enabledAll || videoSource.sources[item.source];
    const isFocused = focusedIndex === index;

    return (
      <Animated.View style={[styles.resourceItem]}>
        <Pressable
          hasTVPreferredFocus={isFocused}
          style={[styles.resourcePressable, isFocused && styles.resourceFocused]}
          onFocus={() => setFocusedIndex(index)}
          onBlur={() => setFocusedIndex(null)}
        >
          <ThemedText style={styles.resourceName}>{item.source_name}</ThemedText>
          <Switch
            value={isEnabled}
            onValueChange={() => {}} // 禁用Switch的直接交互
            trackColor={{ false: "#767577", true: "#007AFF" }}
            thumbColor={isEnabled ? "#ffffff" : "#f4f3f4"}
            pointerEvents="none"
          />
        </Pressable>
      </Animated.View>
    );
  };

  return (
    <SettingsSection focusable onFocus={handleSectionFocus} onBlur={handleSectionBlur}>
      <ThemedText style={styles.sectionTitle}>播放源配置</ThemedText>

      {resources.length > 0 && (
        <FlatList
          data={resources}
          renderItem={renderResourceItem}
          keyExtractor={(item) => item.source}
          numColumns={3}
          columnWrapperStyle={styles.row}
          contentContainerStyle={styles.flatListContainer}
          scrollEnabled={false}
        />
      )}
    </SettingsSection>
  );
};

const styles = StyleSheet.create({
  sectionTitle: {
    fontSize: 20,
    fontWeight: "bold",
    marginBottom: 16,
  },
  flatListContainer: {
    gap: 12,
  },
  row: {
    justifyContent: "flex-start",
  },
  resourceItem: {
    width: "32%",
    marginHorizontal: 6,
    marginVertical: 6,
    borderRadius: 8,
    overflow: "hidden",
    justifyContent: "flex-start",
  },
  resourcePressable: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-start",
    paddingVertical: 12,
    paddingHorizontal: 16,
    backgroundColor: "#2a2a2a",
    borderRadius: 8,
    minHeight: 56,
  },
  resourceFocused: {
    backgroundColor: "#3a3a3c",
    borderWidth: 2,
    borderColor: "#007AFF",
    shadowColor: "#007AFF",
    shadowOffset: { width: 0, height: 0 },
    shadowOpacity: 0.8,
    shadowRadius: 10,
    elevation: 5,
  },
  resourceName: {
    fontSize: 14,
    fontWeight: "600",
    flex: 1,
    marginRight: 8,
  },
});
