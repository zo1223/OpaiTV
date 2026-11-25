import React, { useEffect } from "react";
import { Pressable, StyleSheet, Text } from "react-native";
import { useDanmakuStore } from "@/stores/danmakuStore";
import { DANMAKU_NOTICE } from "@/services/danmaku";
import { showDanmakuNotice } from "@/components/DanmakuOverlay";

/**
 * 播放页上常驻的弹幕开关(手机 / 平板;TV 端在设置里开关)。
 * 与设置页的弹幕开关是同一个设置:点一下立即生效并保存。
 * 关:弹幕层整个卸载,画面上的弹幕立刻清掉;开:弹幕层重新挂上,按当前片名和集数匹配。
 */
export const DanmakuToggle: React.FC = () => {
  const enabled = useDanmakuStore((state) => state.settings.enabled);
  const update = useDanmakuStore((state) => state.update);

  useEffect(() => {
    void useDanmakuStore.getState().load();
  }, []);

  const onPress = () => {
    const next = !enabled;
    void update({ enabled: next });
    showDanmakuNotice(next ? DANMAKU_NOTICE.on : DANMAKU_NOTICE.off);
  };

  return (
    <Pressable
      testID="danmaku-toggle"
      style={[styles.entry, enabled && styles.entryOn]}
      onPress={onPress}
      accessibilityRole="switch"
      accessibilityState={{ checked: enabled }}
      accessibilityLabel={enabled ? "弹幕：开" : "弹幕：关"}
    >
      <Text style={[styles.text, enabled && styles.textOn]}>{enabled ? "弹幕 开" : "弹幕 关"}</Text>
    </Pressable>
  );
};

const styles = StyleSheet.create({
  // 在"投屏"入口(top 56)正下方,两个入口不重叠
  entry: {
    position: "absolute",
    top: 100,
    right: 16,
    zIndex: 29,
    elevation: 29,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 18,
    backgroundColor: "rgba(0, 0, 0, 0.55)",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "rgba(255, 255, 255, 0.35)",
  },
  entryOn: {
    backgroundColor: "rgba(111, 220, 168, 0.85)",
    borderColor: "rgba(255, 255, 255, 0.6)",
  },
  text: {
    color: "rgba(255, 255, 255, 0.75)",
    fontSize: 14,
    fontWeight: "600",
  },
  textOn: {
    color: "#06140E",
  },
});
