import React, { useEffect, useState } from "react";
import { StyleSheet, TextInput, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import {
  PLAYBACK_PROXY_MODES,
  PLAYBACK_PROXY_MODE_HINTS,
  PLAYBACK_PROXY_MODE_LABELS,
  PlaybackProxyMode,
  isValidProxyAddress,
  normalizeProxyAddress,
} from "@/services/playbackProxy";

interface PlaybackProxySectionProps {
  onFocus?: () => void;
}

const INVALID_ADDRESS_MESSAGE = "地址无效:需要以 http:// 或 https:// 开头的中转地址";

// 播放代理模式:系统代理 / 强制代理 / 直连。切换立即保存,下次播放生效(正在播放的视频不受影响)
export const PlaybackProxySection: React.FC<PlaybackProxySectionProps> = ({ onFocus }) => {
  const { playbackProxyMode, playbackProxyUrl, setPlaybackProxyMode, setPlaybackProxyUrl } = useSettingsStore();
  const [address, setAddress] = useState(playbackProxyUrl);
  const [message, setMessage] = useState("");

  useEffect(() => {
    setAddress(playbackProxyUrl);
  }, [playbackProxyUrl]);

  const saveAddress = async (): Promise<boolean> => {
    const next = normalizeProxyAddress(address);
    if (next && !isValidProxyAddress(next)) {
      setMessage(INVALID_ADDRESS_MESSAGE);
      return false;
    }
    if (next !== playbackProxyUrl) {
      const wasForced = playbackProxyMode === "forced";
      await setPlaybackProxyUrl(next);
      setMessage(!next && wasForced ? "地址已清空,强制代理已改回系统代理" : "地址已保存,下次播放生效");
    } else {
      setMessage("");
    }
    return true;
  };

  const selectMode = async (mode: PlaybackProxyMode) => {
    if (mode === "forced") {
      // 先把输入框里的地址存下来;没有有效地址时不切换
      if (!isValidProxyAddress(address)) {
        setMessage("请先填写有效的中转地址,再选择强制代理");
        return;
      }
      if (!(await saveAddress())) return;
    }
    const switched = await setPlaybackProxyMode(mode);
    setMessage(switched ? "已切换,下次播放生效" : "请先填写有效的中转地址,再选择强制代理");
  };

  return (
    <SettingsSection>
      <ThemedText style={styles.sectionTitle}>播放代理模式</ThemedText>
      <ThemedText style={styles.status}>
        {`${PLAYBACK_PROXY_MODE_LABELS[playbackProxyMode]}:${PLAYBACK_PROXY_MODE_HINTS[playbackProxyMode]}`}
      </ThemedText>
      <View style={styles.modes}>
        {PLAYBACK_PROXY_MODES.map((option) => (
          <StyledButton
            key={option}
            text={PLAYBACK_PROXY_MODE_LABELS[option]}
            isSelected={playbackProxyMode === option}
            onPress={() => void selectMode(option)}
            onFocus={onFocus}
            style={styles.modeButton}
            accessibilityLabel={`播放代理模式 ${PLAYBACK_PROXY_MODE_LABELS[option]}`}
          />
        ))}
      </View>
      <View style={styles.inputRow}>
        <TextInput
          style={styles.input}
          value={address}
          onChangeText={(text) => {
            setAddress(text);
            setMessage("");
          }}
          onFocus={onFocus}
          onSubmitEditing={() => void saveAddress()}
          placeholder="https://relay.example/?url="
          placeholderTextColor="#666"
          autoCapitalize="none"
          autoCorrect={false}
          keyboardType="url"
          accessibilityLabel="强制代理中转地址"
        />
        <StyledButton text="保存地址" onPress={() => void saveAddress()} onFocus={onFocus} style={styles.saveButton} />
      </View>
      {message ? <ThemedText style={styles.message}>{message}</ThemedText> : null}
      <ThemedText style={styles.hint}>
        强制代理需要中转式地址(例如 https://relay.example/?url= 或带 {"{url}"} 占位符),不支持 host:port 形式的
        HTTP/SOCKS 代理;直连=不使用上面的代理地址,系统层的代理或 VPN 由系统控制。切换后下次播放生效。
      </ThemedText>
    </SettingsSection>
  );
};

const styles = StyleSheet.create({
  sectionTitle: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 6,
  },
  status: {
    fontSize: 13,
    color: "#888",
    marginBottom: 8,
  },
  modes: {
    flexDirection: "row",
    flexWrap: "wrap",
  },
  modeButton: {
    marginRight: 8,
    marginBottom: 8,
  },
  inputRow: {
    flexDirection: "row",
    alignItems: "center",
  },
  input: {
    flex: 1,
    height: 44,
    borderWidth: 1,
    borderColor: "#444",
    borderRadius: 8,
    paddingHorizontal: 12,
    color: "#fff",
    marginRight: 8,
  },
  saveButton: {
    minWidth: 88,
  },
  message: {
    fontSize: 13,
    color: "#e0a030",
    marginTop: 6,
  },
  hint: {
    fontSize: 12,
    color: "#888",
    marginTop: 8,
  },
});
