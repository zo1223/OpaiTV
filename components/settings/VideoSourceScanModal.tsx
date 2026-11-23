import React, { useCallback, useEffect, useRef, useState } from "react";
import { Modal, Platform, StyleSheet, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { ThemedView } from "@/components/ThemedView";
import { StyledButton } from "@/components/StyledButton";
import { createVideoSourceSyncAssembler } from "@/services/videoSourceSync";
import { Colors } from "@/constants/Colors";

type CameraModule = typeof import("expo-camera");

// 相机是原生模块:电视设备没有相机,旧安装包里也可能没有这个模块。
// 用到时才加载,加载不了就按"不可用"处理,不影响设置页其余部分。
const loadCameraModule = (): CameraModule | null => {
  if (Platform.isTV) return null;
  try {
    const cameraModule = require("expo-camera") as CameraModule;
    return cameraModule?.CameraView ? cameraModule : null;
  } catch {
    return null;
  }
};

/** 当前设备能不能扫码导入(有相机模块的非电视设备) */
export const isVideoSourceScanAvailable = () => loadCameraModule() !== null;

type ScanState = "preparing" | "scanning" | "denied" | "unavailable";

interface VideoSourceScanModalProps {
  visible: boolean;
  onClose: () => void;
  /** 扫到了完整的同步链接(分片二维码已拼好) */
  onScanned: (link: string) => void;
}

// 扫码导入:对准另一台设备上"同步视频源"显示的二维码。分片二维码会自动轮播,保持对准直到扫完。
export const VideoSourceScanModal: React.FC<VideoSourceScanModalProps> = ({ visible, onClose, onScanned }) => {
  const [state, setState] = useState<ScanState>("preparing");
  const [status, setStatus] = useState("");
  const cameraRef = useRef<CameraModule | null>(null);
  const assemblerRef = useRef(createVideoSourceSyncAssembler());
  const finishedRef = useRef(false);
  const statusRef = useRef("");

  // 相机每秒回调多次,文字没变就不重新渲染
  const showStatus = useCallback((text: string) => {
    if (statusRef.current !== text) {
      statusRef.current = text;
      setStatus(text);
    }
  }, []);

  useEffect(() => {
    if (!visible) return;
    let cancelled = false;
    finishedRef.current = false;
    assemblerRef.current.reset();
    showStatus("");
    setState("preparing");

    const cameraModule = loadCameraModule();
    cameraRef.current = cameraModule;
    if (!cameraModule) {
      setState("unavailable");
      return;
    }

    cameraModule.Camera.requestCameraPermissionsAsync()
      .then((permission) => {
        if (!cancelled) setState(permission.granted ? "scanning" : "denied");
      })
      .catch(() => {
        if (!cancelled) setState("unavailable");
      });
    return () => {
      cancelled = true;
    };
  }, [visible, showStatus]);

  const handleBarcodeScanned = useCallback(
    ({ data }: { data: string }) => {
      if (finishedRef.current) return;
      const progress = assemblerRef.current.accept(data);
      if (progress.status === "complete") {
        finishedRef.current = true;
        onScanned(progress.link);
      } else if (progress.status === "partial") {
        showStatus(`已扫到 ${progress.received} / ${progress.total} 张,请保持对准,等二维码轮播完`);
      } else {
        showStatus("这不是 OpaiTV 的同步二维码");
      }
    },
    [onScanned, showStatus]
  );

  const CameraView = cameraRef.current?.CameraView;

  return (
    <Modal animationType="fade" transparent visible={visible} onRequestClose={onClose}>
      <View style={styles.backdrop}>
        <ThemedView style={styles.content}>
          <ThemedText style={styles.title}>扫码导入视频源</ThemedText>
          {state === "scanning" && CameraView ? (
            <>
              <View style={styles.cameraFrame}>
                <CameraView
                  style={StyleSheet.absoluteFill}
                  facing="back"
                  barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
                  onBarcodeScanned={handleBarcodeScanned}
                />
              </View>
              <ThemedText style={styles.description}>
                {status || "对准另一台设备“同步视频源”里显示的二维码"}
              </ThemedText>
            </>
          ) : (
            <ThemedText style={styles.description}>
              {state === "preparing"
                ? "正在打开相机..."
                : state === "denied"
                  ? "没有相机权限,无法扫码。请在系统设置里允许 OpaiTV 使用相机,或改用粘贴同步链接。"
                  : "当前设备无法使用相机扫码,请改用粘贴同步链接。"}
            </ThemedText>
          )}
          <StyledButton text="关闭" variant="primary" isSelected onPress={onClose} style={styles.closeButton} />
        </ThemedView>
      </View>
    </Modal>
  );
};

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    justifyContent: "center",
    alignItems: "center",
    backgroundColor: "rgba(0, 0, 0, 0.7)",
  },
  content: {
    width: "90%",
    maxWidth: 480,
    padding: 20,
    borderRadius: 12,
    alignItems: "center",
  },
  title: {
    fontSize: 20,
    fontWeight: "bold",
    marginBottom: 12,
  },
  cameraFrame: {
    width: "100%",
    aspectRatio: 1,
    borderRadius: 8,
    overflow: "hidden",
    backgroundColor: "black",
    borderWidth: 2,
    borderColor: Colors.dark.primary,
  },
  description: {
    fontSize: 14,
    color: "#ccc",
    textAlign: "center",
    marginTop: 12,
  },
  closeButton: {
    marginTop: 14,
  },
});
