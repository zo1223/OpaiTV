import React, { forwardRef, useCallback, useImperativeHandle, useRef, useState } from "react";
import { Platform, Pressable, StyleSheet, TextInput, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { isManualVideoSource } from "@/services/videoSourceSubscription";
import {
  INCOMPLETE_VIDEO_SOURCE_SYNC_LINK,
  isVideoSourceSyncLink,
  UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK,
} from "@/services/videoSourceSync";
import { VideoSourceSyncModal } from "./VideoSourceSyncModal";
import { isVideoSourceScanAvailable, VideoSourceScanModal } from "./VideoSourceScanModal";
import { Colors } from "@/constants/Colors";

interface AddVideoSourceSectionProps {
  onFocus?: () => void;
}

export interface AddVideoSourceSectionRef {
  setInputValue: (value: string) => void;
}

// 一键添加视频源:粘贴接口地址(可选标记为成人源),点"添加"或在键盘上确认即保存并生效,
// 不需要再点"保存设置"。接口是 JSON 还是 XML 在请求时按应答自动识别。
// 多端同步也在这里:"同步视频源"把当前全部源导出成链接和二维码;粘贴同步链接或扫码则一次性导入全部源。
export const AddVideoSourceSection = forwardRef<AddVideoSourceSectionRef, AddVideoSourceSectionProps>(
  ({ onFocus }, ref) => {
    const { subscribedVideoSources, showAdultContent, addVideoSource, importVideoSources, removeVideoSource } =
      useSettingsStore();
    const [url, setUrl] = useState("");
    const [adult, setAdult] = useState(false);
    const [isBusy, setIsBusy] = useState(false);
    const [message, setMessage] = useState<{ text: string; isError: boolean } | null>(null);
    const [isInputRowFocused, setIsInputRowFocused] = useState(false);
    const [isInputFocused, setIsInputFocused] = useState(false);
    const [isSyncModalVisible, setIsSyncModalVisible] = useState(false);
    const [isScanModalVisible, setIsScanModalVisible] = useState(false);
    const inputRef = useRef<TextInput>(null);
    // 扫码只在有相机的设备上提供(电视没有相机;装的是不含相机模块的旧安装包时也不显示)
    const [canScan] = useState(isVideoSourceScanAvailable);

    // 遥控输入(手机发来的文本)填进地址框,见 app/settings.tsx 的 handleRemoteInput
    useImperativeHandle(ref, () => ({
      setInputValue: (value: string) => {
        setUrl(value);
        setMessage(null);
      },
    }));

    // TV 上和其他输入项一样:焦点先落在输入行,按确认键才进入编辑
    useSafeTVEventHandler(
      useCallback(
        (event: any) => {
          if (isInputRowFocused && event.eventType === "select") {
            inputRef.current?.focus();
          }
        },
        [isInputRowFocused]
      )
    );

    // 同步链接(粘贴或扫码得到):一次性导入其中全部源。已有的源不会重复添加。
    const importSyncLink = async (link: string, markAllAdult: boolean) => {
      if (isBusy) return;
      setIsBusy(true);
      try {
        const result = await importVideoSources(link, markAllAdult);
        setUrl("");
        setAdult(false);
        const details = [
          result.added > 0 ? `新增 ${result.added} 个` : "",
          result.updated > 0 ? `更新 ${result.updated} 个` : "",
          result.unchanged > 0 ? `${result.unchanged} 个已存在` : "",
        ].filter(Boolean);
        setMessage({
          text:
            `已导入同步链接里的 ${result.total} 个视频源(${details.join(",")})` +
            (result.adultCount > 0 && !showAdultContent
              ? `。其中 ${result.adultCount} 个是成人源,成人内容开关已关闭,暂不显示`
              : ""),
          isError: false,
        });
      } catch (error) {
        const code = error instanceof Error ? error.message : "";
        setMessage({
          text:
            code === INCOMPLETE_VIDEO_SOURCE_SYNC_LINK
              ? "这是分片二维码里的一张,不是完整链接。请用“扫码导入”扫完全部二维码,或粘贴完整的同步链接"
              : code === UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK
                ? "这个同步链接来自更新的版本,请先升级 OpaiTV 再导入"
                : "同步链接无效或不完整,请重新复制完整的链接",
          isError: true,
        });
      } finally {
        setIsBusy(false);
      }
    };

    const handleAdd = async () => {
      if (isBusy) return;
      if (isVideoSourceSyncLink(url)) {
        await importSyncLink(url, adult);
        return;
      }
      setIsBusy(true);
      try {
        await addVideoSource(url, adult);
        setUrl("");
        setAdult(false);
        setMessage({
          text: adult && !showAdultContent ? "已添加成人源。成人内容开关已关闭,该源暂不显示" : "已添加",
          isError: false,
        });
      } catch (error) {
        const isInvalidUrl = error instanceof Error && error.message === "INVALID_VIDEO_SOURCE_URL";
        setMessage({ text: isInvalidUrl ? "请输入有效的视频源接口地址(http/https)" : "添加失败,请重试", isError: true });
      } finally {
        setIsBusy(false);
      }
    };

    // 源列表:成人内容开关关闭时不列出成人源,只说明有多少个被隐藏
    const visibleSources = subscribedVideoSources.filter((source) => showAdultContent || !source.adult);
    const hiddenAdultCount = subscribedVideoSources.length - visibleSources.length;
    const manualSources = visibleSources.filter(isManualVideoSource);

    return (
      <SettingsSection>
        <ThemedText style={styles.sectionTitle}>添加视频源</ThemedText>
        <Pressable
          onFocus={() => {
            setIsInputRowFocused(true);
            onFocus?.();
          }}
          onBlur={() => setIsInputRowFocused(false)}
          onPress={Platform.isTV ? undefined : () => inputRef.current?.focus()}
        >
          <TextInput
            ref={inputRef}
            style={[styles.input, (isInputRowFocused || isInputFocused) && styles.inputFocused]}
            value={url}
            onChangeText={(value) => {
              setUrl(value);
              setMessage(null);
            }}
            placeholder="粘贴苹果CMS接口地址(JSON 或 XML),或其他设备导出的同步链接(opaitv-sync://...)"
            placeholderTextColor="#9AA1B8"
            autoCapitalize="none"
            autoCorrect={false}
            keyboardType="url"
            returnKeyType="done"
            onSubmitEditing={handleAdd}
            onFocus={() => setIsInputFocused(true)}
            onBlur={() => setIsInputFocused(false)}
          />
        </Pressable>

        <View style={styles.actions}>
          <StyledButton
            text={adult ? "成人源(18+):是" : "成人源(18+):否"}
            isSelected={adult}
            onPress={() => setAdult(!adult)}
            onFocus={onFocus}
          />
          <StyledButton
            text={isBusy ? "添加中..." : "添加"}
            variant="primary"
            isSelected
            onPress={handleAdd}
            onFocus={onFocus}
          />
        </View>

        {/* 多端同步:导出当前全部源(链接 + 二维码);扫码导入另一台设备导出的源 */}
        <View style={styles.actions}>
          <StyledButton text="同步视频源(导出)" onPress={() => setIsSyncModalVisible(true)} onFocus={onFocus} />
          {canScan && <StyledButton text="扫码导入" onPress={() => setIsScanModalVisible(true)} onFocus={onFocus} />}
        </View>

        {message && (
          <ThemedText style={[styles.message, message.isError && styles.messageError]}>{message.text}</ThemedText>
        )}

        <ThemedText style={styles.summary}>
          {`当前共 ${visibleSources.length} 个视频源`}
          {hiddenAdultCount > 0 ? `,另有 ${hiddenAdultCount} 个成人源已隐藏(成人内容开关已关闭)` : ""}
        </ThemedText>

        {manualSources.map((source) => (
          <View key={source.key} style={styles.sourceRow}>
            <View style={styles.sourceInfo}>
              <ThemedText style={styles.sourceName}>
                {source.name}
                {source.adult ? "  [18+]" : ""}
              </ThemedText>
              <ThemedText style={styles.sourceApi} numberOfLines={1}>
                {source.api}
              </ThemedText>
            </View>
            <StyledButton text="删除" onPress={() => void removeVideoSource(source.key)} onFocus={onFocus} />
          </View>
        ))}

        <VideoSourceSyncModal visible={isSyncModalVisible} onClose={() => setIsSyncModalVisible(false)} />
        {canScan && (
          <VideoSourceScanModal
            visible={isScanModalVisible}
            onClose={() => setIsScanModalVisible(false)}
            onScanned={(link) => {
              setIsScanModalVisible(false);
              void importSyncLink(link, false);
            }}
          />
        )}
      </SettingsSection>
    );
  }
);

AddVideoSourceSection.displayName = "AddVideoSourceSection";

const styles = StyleSheet.create({
  sectionTitle: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 8,
  },
  input: {
    height: 50,
    borderWidth: 2,
    borderRadius: 8,
    paddingHorizontal: 15,
    fontSize: 16,
    backgroundColor: "#2A3048",
    color: "white",
    borderColor: "transparent",
  },
  inputFocused: {
    borderColor: Colors.dark.primary,
  },
  actions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
    marginTop: 12,
  },
  message: {
    fontSize: 14,
    marginTop: 12,
    color: Colors.dark.primary,
  },
  messageError: {
    color: "#ff6b6b",
  },
  summary: {
    fontSize: 12,
    color: "#9AA1B8",
    marginTop: 12,
  },
  sourceRow: {
    flexDirection: "row",
    alignItems: "center",
    marginTop: 12,
  },
  sourceInfo: {
    flex: 1,
    marginRight: 12,
  },
  sourceName: {
    fontSize: 14,
    fontWeight: "600",
  },
  sourceApi: {
    fontSize: 12,
    color: "#9AA1B8",
  },
});
