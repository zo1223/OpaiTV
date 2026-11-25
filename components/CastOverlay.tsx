import React from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import useCastStore, { CastStatus } from "@/stores/castStore";
import { stopCastAndResumeLocal } from "@/hooks/useCastPlayback";
import { formatCastTime } from "./CastModal";

/** 投屏状态的中文说明(界面与测试共用)。 */
export const castStateLabel = (status: CastStatus, transportState: string | null, isPlaying: boolean): string => {
  if (status === "connecting") return "连接中";
  switch (transportState) {
    case "PLAYING":
      return "播放中";
    case "PAUSED_PLAYBACK":
      return "已暂停";
    case "TRANSITIONING":
      return "缓冲中";
    case "STOPPED":
    case "NO_MEDIA_PRESENT":
      return "电视端已停止";
    default:
      return isPlaying ? "播放中" : "已暂停";
  }
};

/**
 * 播放页上常驻的投屏层(只给手机 / 平板用,TV 不挂):
 * - 没在投屏:右上角一个"投屏"入口(手机 / 平板用的是系统自带播放控件,没有自己的控制条可以放按钮)。
 * - 投屏中:盖住本机画面,显示当前设备、状态与进度,提供播放/暂停、打开控制面板、停止投屏。
 *   盖住画面也是为了挡住本机播放控件,避免本机和电视同时出声。
 */
export const CastOverlay: React.FC = () => {
  const status = useCastStore((state) => state.status);
  const activeDevice = useCastStore((state) => state.activeDevice);
  const transportState = useCastStore((state) => state.transportState);
  const isPlaying = useCastStore((state) => state.isPlaying);
  const positionMillis = useCastStore((state) => state.positionMillis);
  const durationMillis = useCastStore((state) => state.durationMillis);
  const error = useCastStore((state) => state.error);
  const openModal = useCastStore((state) => state.openModal);
  const togglePlay = useCastStore((state) => state.togglePlay);

  if (status === "idle" || !activeDevice) {
    return (
      <Pressable
        testID="cast-entry"
        style={styles.entry}
        onPress={openModal}
        accessibilityRole="button"
        accessibilityLabel="投屏"
      >
        <Text style={styles.entryText}>投屏</Text>
      </Pressable>
    );
  }

  return (
    <View style={styles.cover} testID="cast-status">
      <Text style={styles.device} numberOfLines={1} testID="cast-status-device">
        {status === "connecting" ? "正在连接 " : "正在投屏到 "}
        {activeDevice.name}
      </Text>
      <Text style={styles.state} testID="cast-status-state">
        {castStateLabel(status, transportState, isPlaying)} · {formatCastTime(positionMillis)} / {formatCastTime(durationMillis)}
      </Text>
      {error ? (
        <Text style={styles.error} testID="cast-status-error">
          {error}
        </Text>
      ) : null}
      <View style={styles.row}>
        <Pressable
          testID="cast-status-toggle"
          style={[styles.button, status !== "casting" && styles.buttonDisabled]}
          onPress={togglePlay}
          disabled={status !== "casting"}
          accessibilityRole="button"
        >
          <Text style={styles.buttonText}>{isPlaying ? "暂停" : "播放"}</Text>
        </Pressable>
        <Pressable testID="cast-status-panel" style={styles.button} onPress={openModal} accessibilityRole="button">
          <Text style={styles.buttonText}>控制面板</Text>
        </Pressable>
        <Pressable
          testID="cast-status-stop"
          style={styles.button}
          onPress={() => {
            stopCastAndResumeLocal();
          }}
          accessibilityRole="button"
        >
          <Text style={styles.buttonText}>停止投屏</Text>
        </Pressable>
      </View>
    </View>
  );
};

const styles = StyleSheet.create({
  entry: {
    position: "absolute",
    top: 56,
    right: 16,
    zIndex: 30,
    elevation: 30,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 18,
    backgroundColor: "rgba(0, 0, 0, 0.55)",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "rgba(255, 255, 255, 0.35)",
  },
  entryText: {
    color: "white",
    fontSize: 14,
  },
  cover: {
    ...StyleSheet.absoluteFillObject,
    zIndex: 30,
    elevation: 30,
    backgroundColor: "rgba(0, 0, 0, 0.88)",
    alignItems: "center",
    justifyContent: "center",
    padding: 24,
  },
  device: {
    color: "white",
    fontSize: 18,
    fontWeight: "bold",
    marginBottom: 8,
  },
  state: {
    color: "#ddd",
    fontSize: 15,
    marginBottom: 16,
  },
  error: {
    color: "#ff8a80",
    fontSize: 14,
    textAlign: "center",
    marginBottom: 12,
  },
  row: {
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "center",
    gap: 12,
  },
  button: {
    paddingHorizontal: 18,
    paddingVertical: 10,
    borderRadius: 20,
    backgroundColor: "rgba(255, 255, 255, 0.16)",
  },
  buttonDisabled: {
    opacity: 0.5,
  },
  buttonText: {
    color: "white",
    fontSize: 15,
  },
});

export default CastOverlay;
