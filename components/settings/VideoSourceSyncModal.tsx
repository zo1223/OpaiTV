import React, { useEffect, useMemo, useState } from "react";
import { Clipboard, Modal, Platform, Share, StyleSheet, useWindowDimensions, View } from "react-native";
import QRCode from "react-native-qrcode-svg";
import { ThemedText } from "@/components/ThemedText";
import { ThemedView } from "@/components/ThemedView";
import { StyledButton } from "@/components/StyledButton";
import { useSettingsStore } from "@/stores/settingsStore";
import { buildVideoSourceSyncQrPayloads, encodeVideoSourceSyncLink } from "@/services/videoSourceSync";
import { Colors } from "@/constants/Colors";

interface VideoSourceSyncModalProps {
  visible: boolean;
  onClose: () => void;
}

// 分片二维码自动轮播的间隔:扫码端顺序任意、可重复接收,所以只要每张停留够久即可
const QR_ROTATE_INTERVAL_MS = 1500;

// 导出当前全部视频源:一段同步链接 + 同一内容的二维码。不经过服务器,数据就在链接里。
// 成人内容开关关闭时也导出成人源(带成人标记),对方设备导入后仍由它自己的开关决定是否显示。
export const VideoSourceSyncModal: React.FC<VideoSourceSyncModalProps> = ({ visible, onClose }) => {
  const subscribedVideoSources = useSettingsStore((state) => state.subscribedVideoSources);
  const { width, height } = useWindowDimensions();
  const [partIndex, setPartIndex] = useState(0);
  const [isPaused, setIsPaused] = useState(false);
  const [notice, setNotice] = useState("");

  const link = useMemo(() => {
    if (!visible) return "";
    try {
      return encodeVideoSourceSyncLink(subscribedVideoSources);
    } catch {
      return "";
    }
  }, [visible, subscribedVideoSources]);
  const qrPayloads = useMemo(() => (link ? buildVideoSourceSyncQrPayloads(link) : []), [link]);
  const partCount = qrPayloads.length;

  useEffect(() => {
    setPartIndex(0);
    setIsPaused(false);
    setNotice("");
  }, [link]);

  useEffect(() => {
    if (!visible || partCount <= 1 || isPaused) return;
    const timer = setInterval(() => setPartIndex((index) => (index + 1) % partCount), QR_ROTATE_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [visible, partCount, isPaused]);

  const adultCount = subscribedVideoSources.filter((source) => source.adult).length;
  // 二维码尽量大:模块越大越容易扫
  const qrSize = Math.max(180, Math.min(width * 0.72, height * 0.45, 360));
  const currentPart = Math.min(partIndex, Math.max(partCount - 1, 0));

  const handleCopy = () => {
    try {
      Clipboard.setString(link);
      setNotice("同步链接已复制");
    } catch {
      setNotice("复制失败,请改用“分享”");
    }
  };

  const handleShare = async () => {
    try {
      await Share.share({ message: link });
    } catch {
      setNotice("分享失败,请改用“复制链接”");
    }
  };

  const step = (delta: number) => {
    setIsPaused(true);
    setPartIndex((index) => (index + delta + partCount) % partCount);
  };

  return (
    <Modal animationType="fade" transparent visible={visible} onRequestClose={onClose}>
      <View style={styles.backdrop}>
        <ThemedView style={styles.content}>
          <ThemedText style={styles.title}>同步视频源</ThemedText>
          {link ? (
            <>
              <ThemedText style={styles.description}>
                {`共 ${subscribedVideoSources.length} 个视频源`}
                {adultCount > 0 ? `(含 ${adultCount} 个成人源,导入后仍受成人内容开关控制)` : ""}
                {"。在另一台设备的“添加视频源”里扫码,或粘贴同步链接,即可一次性导入。"}
              </ThemedText>
              <View style={styles.qrContainer}>
                <QRCode
                  value={qrPayloads[currentPart]}
                  size={qrSize}
                  ecl="L"
                  quietZone={12}
                  backgroundColor="white"
                  color="black"
                />
              </View>
              {partCount > 1 && (
                <>
                  <ThemedText style={styles.partText}>
                    {`第 ${currentPart + 1} / ${partCount} 张 · ${isPaused ? "已暂停" : "自动轮播中"},用手机对准二维码直到全部扫完`}
                  </ThemedText>
                  <View style={styles.actions}>
                    <StyledButton text="上一张" onPress={() => step(-1)} />
                    <StyledButton text={isPaused ? "继续轮播" : "暂停"} onPress={() => setIsPaused(!isPaused)} />
                    <StyledButton text="下一张" onPress={() => step(1)} />
                  </View>
                </>
              )}
              <ThemedText style={styles.link} numberOfLines={2} ellipsizeMode="middle" selectable>
                {link}
              </ThemedText>
              <ThemedText style={styles.hint}>{`同步链接共 ${link.length} 个字符`}</ThemedText>
            </>
          ) : (
            <ThemedText style={styles.description}>当前没有可导出的视频源,请先添加视频源或保存订阅链接。</ThemedText>
          )}
          {notice ? <ThemedText style={styles.notice}>{notice}</ThemedText> : null}
          <View style={styles.actions}>
            {/* 电视上没有剪贴板和分享面板,用手机扫码即可 */}
            {link && !Platform.isTV ? (
              <>
                <StyledButton text="复制链接" onPress={handleCopy} />
                <StyledButton text="分享" onPress={handleShare} />
              </>
            ) : null}
            <StyledButton text="关闭" variant="primary" isSelected hasTVPreferredFocus onPress={onClose} />
          </View>
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
    marginBottom: 8,
  },
  description: {
    fontSize: 14,
    color: "#ccc",
    textAlign: "center",
    marginBottom: 12,
  },
  qrContainer: {
    backgroundColor: "white",
    borderRadius: 8,
    overflow: "hidden",
  },
  partText: {
    fontSize: 13,
    color: Colors.dark.primary,
    textAlign: "center",
    marginTop: 10,
  },
  link: {
    fontSize: 12,
    color: "#888",
    marginTop: 12,
  },
  hint: {
    fontSize: 12,
    color: "#888",
    marginTop: 4,
  },
  notice: {
    fontSize: 14,
    color: Colors.dark.primary,
    marginTop: 10,
  },
  actions: {
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "center",
    gap: 12,
    marginTop: 14,
  },
});
