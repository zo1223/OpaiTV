import React, { useCallback, useEffect } from "react";
import { ActivityIndicator, Modal, ScrollView, StyleSheet, Text, View } from "react-native";
import { StyledButton } from "./StyledButton";
import useCastStore from "@/stores/castStore";
import { castCurrentVideo, stopCastAndResumeLocal } from "@/hooks/useCastPlayback";
import { DlnaDevice } from "@/services/dlna/description";

const SEEK_STEP_MS = 30_000;

export const formatCastTime = (milliseconds: number) => {
  const totalSeconds = Math.max(0, Math.floor((milliseconds || 0) / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const mmss = `${minutes.toString().padStart(2, "0")}:${seconds.toString().padStart(2, "0")}`;
  return hours > 0 ? `${hours.toString().padStart(2, "0")}:${mmss}` : mmss;
};

/** 投屏面板:搜索设备 → 选设备 → 投屏控制(播放/暂停、快退快进、停止)。 */
export const CastModal: React.FC = () => {
  const {
    modalVisible,
    discovering,
    discoveryError,
    devices,
    status,
    activeDevice,
    isPlaying,
    positionMillis,
    durationMillis,
    error,
    closeModal,
    discover,
    togglePlay,
    seekBy,
  } = useCastStore();

  const isCasting = status !== "idle";

  useEffect(() => {
    if (modalVisible && !isCasting) {
      discover();
    }
    // 只在面板打开时自动搜索一次。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modalVisible]);

  // 推送当前这一集并让本机暂停 / 停止后本机从电视端进度恢复:逻辑在 hooks/useCastPlayback.ts(有测试)。
  const onSelectDevice = useCallback((device: DlnaDevice) => {
    castCurrentVideo(device);
  }, []);

  const onStop = useCallback(() => {
    stopCastAndResumeLocal();
  }, []);

  const progress = durationMillis > 0 ? Math.min(1, positionMillis / durationMillis) : 0;

  return (
    <Modal visible={modalVisible} transparent={true} animationType="slide" onRequestClose={closeModal}>
      <View style={styles.modalContainer}>
        <View style={styles.modalContent}>
          <Text style={styles.modalTitle}>投屏</Text>

          {isCasting && activeDevice ? (
            <View testID="cast-controls">
              <Text style={styles.deviceName} numberOfLines={1}>
                {status === "connecting" ? "正在连接 " : "正在投屏到 "}
                {activeDevice.name}
              </Text>
              <View style={styles.progressTrack}>
                <View style={[styles.progressFill, { width: `${progress * 100}%` }]} />
              </View>
              <Text style={styles.timeText} testID="cast-time">
                {formatCastTime(positionMillis)} / {formatCastTime(durationMillis)}
              </Text>
              <View style={styles.controlRow}>
                <StyledButton
                  text="后退 30 秒"
                  onPress={() => seekBy(-SEEK_STEP_MS)}
                  disabled={status !== "casting"}
                  style={styles.controlButton}
                  textStyle={styles.buttonText}
                  testID="cast-rewind"
                />
                <StyledButton
                  text={isPlaying ? "暂停" : "播放"}
                  onPress={togglePlay}
                  disabled={status !== "casting"}
                  hasTVPreferredFocus
                  style={styles.controlButton}
                  textStyle={styles.buttonText}
                  testID="cast-toggle"
                />
                <StyledButton
                  text="快进 30 秒"
                  onPress={() => seekBy(SEEK_STEP_MS)}
                  disabled={status !== "casting"}
                  style={styles.controlButton}
                  textStyle={styles.buttonText}
                  testID="cast-forward"
                />
              </View>
              <StyledButton
                text="停止投屏"
                onPress={onStop}
                style={styles.wideButton}
                textStyle={styles.buttonText}
                testID="cast-stop"
              />
            </View>
          ) : (
            <View testID="cast-devices">
              {discovering ? (
                <View style={styles.statusRow}>
                  <ActivityIndicator color="white" />
                  <Text style={styles.statusText}>正在搜索局域网内的投屏设备…</Text>
                </View>
              ) : null}
              {discoveryError ? <Text style={styles.errorText}>{discoveryError}</Text> : null}
              {!discovering && !discoveryError && devices.length === 0 ? (
                <Text style={styles.statusText}>没有找到投屏设备。请确认电视与手机在同一个 Wi-Fi 下,并已打开 DLNA / 投屏功能。</Text>
              ) : null}
              <ScrollView style={styles.deviceList}>
                {devices.map((device, index) => (
                  <StyledButton
                    key={device.id}
                    text={device.modelName ? `${device.name}(${device.modelName})` : device.name}
                    onPress={() => onSelectDevice(device)}
                    hasTVPreferredFocus={index === 0}
                    style={styles.wideButton}
                    textStyle={styles.buttonText}
                    testID={`cast-device-${index}`}
                  />
                ))}
              </ScrollView>
              {!discovering ? (
                <StyledButton
                  text="重新搜索"
                  onPress={discover}
                  style={styles.wideButton}
                  textStyle={styles.buttonText}
                  testID="cast-rescan"
                />
              ) : null}
            </View>
          )}

          {error ? (
            <Text style={styles.errorText} testID="cast-error">
              {error}
            </Text>
          ) : null}

          <StyledButton
            text="关闭"
            onPress={closeModal}
            style={styles.wideButton}
            textStyle={styles.buttonText}
            testID="cast-close"
          />
        </View>
      </View>
    </Modal>
  );
};

const styles = StyleSheet.create({
  modalContainer: {
    flex: 1,
    justifyContent: "center",
    alignItems: "center",
    backgroundColor: "rgba(0, 0, 0, 0.6)",
  },
  modalContent: {
    width: "86%",
    maxWidth: 520,
    maxHeight: "86%",
    backgroundColor: "rgba(0, 0, 0, 0.92)",
    borderRadius: 12,
    padding: 20,
  },
  modalTitle: {
    color: "white",
    fontSize: 20,
    fontWeight: "bold",
    textAlign: "center",
    marginBottom: 12,
  },
  deviceName: {
    color: "white",
    fontSize: 16,
    textAlign: "center",
    marginBottom: 12,
  },
  deviceList: {
    maxHeight: 240,
  },
  statusRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
    marginBottom: 10,
  },
  statusText: {
    color: "#ccc",
    fontSize: 14,
    textAlign: "center",
    marginBottom: 10,
  },
  errorText: {
    color: "#ff8a80",
    fontSize: 14,
    textAlign: "center",
    marginVertical: 8,
  },
  progressTrack: {
    height: 6,
    borderRadius: 3,
    backgroundColor: "rgba(255, 255, 255, 0.3)",
    overflow: "hidden",
  },
  progressFill: {
    height: 6,
    backgroundColor: "#fff",
  },
  timeText: {
    color: "white",
    textAlign: "center",
    marginTop: 8,
    marginBottom: 12,
  },
  controlRow: {
    flexDirection: "row",
    justifyContent: "center",
    gap: 8,
    marginBottom: 8,
  },
  controlButton: {
    flex: 1,
    paddingVertical: 10,
  },
  wideButton: {
    marginTop: 8,
    paddingVertical: 10,
  },
  buttonText: {
    fontSize: 14,
  },
});

export default CastModal;
