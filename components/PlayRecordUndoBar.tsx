import React, { useEffect } from "react";
import { AppState, Platform, Pressable, StyleSheet, Text, View } from "react-native";
import { usePlayRecordUndoStore } from "@/stores/playRecordUndoStore";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";

/**
 * 删除观看记录后的提示条:"已删除《片名》的观看记录  撤销"。
 * 挂在根布局,切换页面时提示条和撤销窗口都还在;应用退到后台或根布局卸载时立即提交待删除项。
 */
export const PlayRecordUndoBar: React.FC = () => {
  const pending = usePlayRecordUndoStore((state) => state.pending);
  const undo = usePlayRecordUndoStore((state) => state.undo);
  const { deviceType } = useResponsiveLayout();

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (status) => {
      if (status !== "active") {
        void usePlayRecordUndoStore.getState().flush();
      }
    });
    return () => {
      subscription.remove();
      void usePlayRecordUndoStore.getState().flush();
    };
  }, []);

  if (!pending) return null;

  return (
    <View pointerEvents="box-none" style={[styles.host, { bottom: deviceType === "mobile" ? 96 : 32 }]}>
      <View style={styles.bar} testID="play-record-undo-bar">
        <Text style={styles.message} numberOfLines={1}>
          已删除《{pending.title}》的观看记录
        </Text>
        <Pressable
          testID="play-record-undo-button"
          accessibilityRole="button"
          // TV 上被删的卡片消失后焦点落到这里,按确认键即可撤销
          hasTVPreferredFocus={Platform.isTV}
          onPress={() => undo()}
          style={({ focused, pressed }) => [styles.button, (focused || pressed) && styles.buttonActive]}
        >
          <Text style={styles.buttonText}>撤销</Text>
        </Pressable>
      </View>
    </View>
  );
};

const styles = StyleSheet.create({
  host: {
    position: "absolute",
    left: 0,
    right: 0,
    alignItems: "center",
    paddingHorizontal: 16,
  },
  bar: {
    flexDirection: "row",
    alignItems: "center",
    maxWidth: 520,
    backgroundColor: "#2c2c2e",
    borderRadius: 10,
    paddingLeft: 16,
    paddingRight: 6,
    paddingVertical: 6,
    elevation: 8,
    shadowColor: "#000",
    shadowOpacity: 0.3,
    shadowRadius: 6,
    shadowOffset: { width: 0, height: 2 },
  },
  message: {
    flexShrink: 1,
    color: "#fff",
    fontSize: 14,
  },
  button: {
    marginLeft: 12,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 8,
    borderWidth: 2,
    borderColor: "transparent",
  },
  buttonActive: {
    backgroundColor: "rgba(255,255,255,0.12)",
    borderColor: "#fff",
  },
  buttonText: {
    color: "#4da3ff",
    fontSize: 14,
    fontWeight: "600",
  },
});

export default PlayRecordUndoBar;
