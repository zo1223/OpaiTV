import React from "react";
import { StyleSheet, View, StyleProp, ViewStyle } from "react-native";
import { useUpdateStore, shouldShowUpdateBadge } from "@/stores/updateStore";

interface UpdateBadgeProps {
  style?: StyleProp<ViewStyle>;
}

/**
 * 更新小红点:叠在"设置"入口的图标右上角。有新版本(且不是被跳过的版本)时显示,否则不渲染。
 * 只读 updateStore 里现有的检查结果;点进设置页后由"应用更新"一节走现有的更新流程。
 */
export const UpdateBadge: React.FC<UpdateBadgeProps> = ({ style }) => {
  const visible = useUpdateStore(shouldShowUpdateBadge);
  if (!visible) return null;
  return <View testID="update-badge" accessibilityLabel="有新版本可更新" pointerEvents="none" style={[styles.dot, style]} />;
};

const styles = StyleSheet.create({
  dot: {
    position: "absolute",
    top: -2,
    right: -4,
    width: 9,
    height: 9,
    borderRadius: 5,
    backgroundColor: "#ff3b30",
    borderWidth: 1,
    borderColor: "#151828",
  },
});

export default UpdateBadge;
