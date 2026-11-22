import React, { forwardRef, useCallback, useImperativeHandle, useRef, useState } from "react";
import { Platform, Pressable, StyleSheet, TextInput, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { isManualVideoSource } from "@/services/videoSourceSubscription";
import { Colors } from "@/constants/Colors";

interface AddVideoSourceSectionProps {
  onFocus?: () => void;
}

export interface AddVideoSourceSectionRef {
  setInputValue: (value: string) => void;
}

// 一键添加视频源:粘贴接口地址(可选标记为成人源),点"添加"或在键盘上确认即保存并生效,
// 不需要再点"保存设置"。接口是 JSON 还是 XML 在请求时按应答自动识别。
export const AddVideoSourceSection = forwardRef<AddVideoSourceSectionRef, AddVideoSourceSectionProps>(
  ({ onFocus }, ref) => {
    const { subscribedVideoSources, showAdultContent, addVideoSource, removeVideoSource } = useSettingsStore();
    const [url, setUrl] = useState("");
    const [adult, setAdult] = useState(false);
    const [isBusy, setIsBusy] = useState(false);
    const [message, setMessage] = useState<{ text: string; isError: boolean } | null>(null);
    const [isInputRowFocused, setIsInputRowFocused] = useState(false);
    const [isInputFocused, setIsInputFocused] = useState(false);
    const inputRef = useRef<TextInput>(null);

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

    const handleAdd = async () => {
      if (isBusy) return;
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
            placeholder="粘贴苹果CMS接口地址(JSON 或 XML),如 https://example.com/api.php/provide/vod"
            placeholderTextColor="#888"
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
    backgroundColor: "#3a3a3c",
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
    color: "#888",
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
    color: "#888",
  },
});
