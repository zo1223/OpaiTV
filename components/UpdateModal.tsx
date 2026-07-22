import React from "react";
import { Modal, View, StyleSheet, ActivityIndicator, Platform } from "react-native";
import { useUpdateStore } from "../stores/updateStore";
import { Colors } from "../constants/Colors";
import { StyledButton } from "./StyledButton";
import { ThemedText } from "./ThemedText";

export function UpdateModal() {
  const {
    showUpdateModal,
    currentVersion,
    remoteVersion,
    downloading,
    downloadProgress,
    error,
    setShowUpdateModal,
    startDownload,
    installUpdate,
    skipThisVersion,
    downloadedPath,
  } = useUpdateStore();

  const updateButtonRef = React.useRef<View>(null);
  const laterButtonRef = React.useRef<View>(null);
  const skipButtonRef = React.useRef<View>(null);

  async function handleUpdate() {
    if (!downloading && !downloadedPath) {
      // 开始下载
      await startDownload();
    } else if (downloadedPath) {
      // 已下载完成，安装
      await installUpdate();
    }
  }

  function handleLater() {
    setShowUpdateModal(false);
  }

  async function handleSkip() {
    await skipThisVersion();
  }

  React.useEffect(() => {
    if (showUpdateModal && Platform.isTV) {
      // TV平台自动聚焦到更新按钮
      setTimeout(() => {
        updateButtonRef.current?.focus();
      }, 100);
    }
  }, [showUpdateModal]);

  const getButtonText = () => {
    if (downloading) {
      return `下载中 ${downloadProgress}%`;
    } else if (downloadedPath) {
      return "立即安装";
    } else {
      return "立即更新";
    }
  };

  return (
    <Modal visible={showUpdateModal} transparent animationType="fade" onRequestClose={handleLater}>
      <View style={styles.overlay}>
        <View style={styles.container}>
          <ThemedText style={styles.title}>发现新版本</ThemedText>

          <View style={styles.versionInfo}>
            <ThemedText style={styles.versionText}>当前版本: v{currentVersion}</ThemedText>
            <ThemedText style={styles.arrow}>→</ThemedText>
            <ThemedText style={[styles.versionText, styles.newVersion]}>新版本: v{remoteVersion}</ThemedText>
          </View>

          {downloading && (
            <View style={styles.progressContainer}>
              <View style={styles.progressBar}>
                <View style={[styles.progressFill, { width: `${downloadProgress}%` }]} />
              </View>
              <ThemedText style={styles.progressText}>{downloadProgress}%</ThemedText>
            </View>
          )}

          {error && <ThemedText style={styles.errorText}>{error}</ThemedText>}

          <View style={styles.buttonContainer}>
            <StyledButton
              ref={updateButtonRef}
              onPress={handleUpdate}
              disabled={downloading && !downloadedPath}
              variant="primary"
              style={styles.button}
            >
              {downloading && !downloadedPath ? (
                <ActivityIndicator color="#fff" />
              ) : (
                <ThemedText style={styles.buttonText}>{getButtonText()}</ThemedText>
              )}
            </StyledButton>

            {!downloading && !downloadedPath && (
              <>
                <StyledButton ref={laterButtonRef} onPress={handleLater} variant="primary" style={styles.button}>
                  <ThemedText style={[styles.buttonText]}>稍后再说</ThemedText>
                </StyledButton>

                <StyledButton ref={skipButtonRef} onPress={handleSkip} variant="primary" style={styles.button}>
                  <ThemedText style={[styles.buttonText]}>跳过此版本</ThemedText>
                </StyledButton>
              </>
            )}
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  overlay: {
    flex: 1,
    backgroundColor: "rgba(0, 0, 0, 0.7)",
    justifyContent: "center",
    alignItems: "center",
  },
  container: {
    backgroundColor: Colors.dark.background,
    borderRadius: 12,
    padding: 24,
    width: Platform.isTV ? 500 : "90%",
    maxWidth: 500,
    alignItems: "center",
  },
  title: {
    fontSize: Platform.isTV ? 28 : 24,
    fontWeight: "bold",
    color: Colors.dark.text,
    marginBottom: 20,
    paddingTop: 12,
  },
  versionInfo: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 24,
  },
  versionText: {
    fontSize: Platform.isTV ? 18 : 16,
    color: Colors.dark.text,
  },
  newVersion: {
    color: Colors.dark.primary || "#00bb5e",
    fontWeight: "bold",
  },
  arrow: {
    fontSize: Platform.isTV ? 20 : 18,
    color: Colors.dark.text,
    marginHorizontal: 12,
  },
  progressContainer: {
    width: "100%",
    marginBottom: 20,
  },
  progressBar: {
    height: 6,
    backgroundColor: Colors.dark.border,
    borderRadius: 3,
    overflow: "hidden",
    marginBottom: 8,
  },
  progressFill: {
    height: "100%",
    backgroundColor: Colors.dark.primary || "#00bb5e",
  },
  progressText: {
    fontSize: Platform.isTV ? 16 : 14,
    color: Colors.dark.text,
    textAlign: "center",
  },
  errorText: {
    fontSize: Platform.isTV ? 16 : 14,
    color: "#ff4444",
    marginBottom: 16,
    textAlign: "center",
  },
  buttonContainer: {
    width: "100%",
    gap: 12,
    justifyContent: "center", // 居中对齐
    alignItems: "center",
  },
  button: {
    width: "80%",
  },

  buttonText: {
    fontSize: Platform.isTV ? 18 : 16,
    fontWeight: "600",
    color: "#fff",
  },
});
