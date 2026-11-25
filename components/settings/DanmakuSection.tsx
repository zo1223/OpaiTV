import React, { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from "react";
import { Platform, Pressable, StyleSheet, Switch, TextInput, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import { SettingsSection } from "./SettingsSection";
import { useDanmakuStore } from "@/stores/danmakuStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { Colors } from "@/constants/Colors";
import {
  DANMAKU_FONT_SIZE_LABELS,
  DANMAKU_FONT_SIZE_OPTIONS,
  DANMAKU_OPACITY_OPTIONS,
  DANMAKU_SPEED_LABELS,
  DANMAKU_SPEED_OPTIONS,
  normalizeDanmakuServerUrl,
} from "@/services/danmaku/types";

interface DanmakuSectionProps {
  onFocus?: () => void;
}

export interface DanmakuSectionRef {
  setInputValue: (value: string) => void;
}

// 弹幕设置。开关默认关闭;每一项改动都立即生效并保存(见 stores/danmakuStore),不需要再点"保存设置"。
// 服务器地址留空时使用内置来源(直连腾讯视频、哔哩哔哩的公开接口),不需要用户部署任何服务。
export const DanmakuSection = forwardRef<DanmakuSectionRef, DanmakuSectionProps>(({ onFocus }, ref) => {
  const settings = useDanmakuStore((state) => state.settings);
  const update = useDanmakuStore((state) => state.update);
  const [serverUrl, setServerUrl] = useState(settings.serverUrl);
  const [urlError, setUrlError] = useState(false);
  const [isToggleFocused, setIsToggleFocused] = useState(false);
  const [isInputRowFocused, setIsInputRowFocused] = useState(false);
  const [isInputFocused, setIsInputFocused] = useState(false);
  const inputRef = useRef<TextInput>(null);

  useEffect(() => {
    void useDanmakuStore.getState().load();
  }, []);

  // 存储里的地址读出来(或被规范化)后同步到输入框
  useEffect(() => {
    setServerUrl(settings.serverUrl);
  }, [settings.serverUrl]);

  const handleToggle = useCallback(() => {
    void update({ enabled: !settings.enabled });
  }, [settings.enabled, update]);

  const commitServerUrl = useCallback(
    (value: string) => {
      const normalized = normalizeDanmakuServerUrl(value);
      // 填了内容却不是 http(s) 地址:不保存,提示一下
      const invalid = value.trim() !== "" && normalized === "";
      setUrlError(invalid);
      if (invalid) return;
      setServerUrl(normalized);
      if (normalized !== settings.serverUrl) {
        void update({ serverUrl: normalized });
      }
    },
    [settings.serverUrl, update]
  );

  // 遥控输入(手机发来的文本)填进地址框并保存,见 app/settings.tsx 的 handleRemoteInput
  useImperativeHandle(ref, () => ({
    setInputValue: (value: string) => {
      setServerUrl(value);
      commitServerUrl(value);
    },
  }));

  // TV 上焦点先落在行上,按确认键才切换开关 / 进入编辑
  useSafeTVEventHandler(
    useCallback(
      (event: any) => {
        if (event.eventType !== "select") return;
        if (isToggleFocused) {
          handleToggle();
        } else if (isInputRowFocused) {
          inputRef.current?.focus();
        }
      },
      [handleToggle, isInputRowFocused, isToggleFocused]
    )
  );

  return (
    <SettingsSection>
      <Pressable
        style={[styles.toggleRow, isToggleFocused && styles.rowFocused]}
        onFocus={() => {
          setIsToggleFocused(true);
          onFocus?.();
        }}
        onBlur={() => setIsToggleFocused(false)}
        onPress={Platform.isTV ? undefined : handleToggle}
      >
        <View style={styles.info}>
          <ThemedText style={styles.name}>弹幕</ThemedText>
          <ThemedText style={[styles.status, settings.enabled && styles.statusOn]}>
            {settings.enabled
              ? "已开启:播放时按片名和集数自动匹配弹幕,匹配不到就不显示"
              : "已关闭"}
          </ThemedText>
        </View>
        <Switch
          value={settings.enabled}
          onValueChange={handleToggle}
          trackColor={{ false: "#767577", true: Colors.dark.primary }}
          thumbColor={settings.enabled ? "#ffffff" : "#f4f3f4"}
          pointerEvents={Platform.isTV ? "none" : "auto"}
        />
      </Pressable>

      {settings.enabled && (
        <>
          <ThemedText style={styles.label}>透明度</ThemedText>
          <View style={styles.options}>
            {DANMAKU_OPACITY_OPTIONS.map((value) => (
              <StyledButton
                key={value}
                text={`${Math.round(value * 100)}%`}
                isSelected={Math.abs(settings.opacity - value) < 0.01}
                onPress={() => void update({ opacity: value })}
                onFocus={onFocus}
              />
            ))}
          </View>

          <ThemedText style={styles.label}>滚动速度</ThemedText>
          <View style={styles.options}>
            {DANMAKU_SPEED_OPTIONS.map((value) => (
              <StyledButton
                key={value}
                text={DANMAKU_SPEED_LABELS[value]}
                isSelected={settings.speed === value}
                onPress={() => void update({ speed: value })}
                onFocus={onFocus}
              />
            ))}
          </View>

          <ThemedText style={styles.label}>字号</ThemedText>
          <View style={styles.options}>
            {DANMAKU_FONT_SIZE_OPTIONS.map((value) => (
              <StyledButton
                key={value}
                text={DANMAKU_FONT_SIZE_LABELS[value]}
                isSelected={settings.fontSize === value}
                onPress={() => void update({ fontSize: value })}
                onFocus={onFocus}
              />
            ))}
          </View>

          <ThemedText style={styles.label}>弹幕服务器地址(可选)</ThemedText>
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
              value={serverUrl}
              onChangeText={(value) => {
                setServerUrl(value);
                setUrlError(false);
              }}
              placeholder="留空即可;高级用户可填自己的 danmu_api 地址,如 http://192.168.1.7:9321"
              placeholderTextColor="#888"
              autoCapitalize="none"
              autoCorrect={false}
              keyboardType="url"
              returnKeyType="done"
              onSubmitEditing={() => commitServerUrl(serverUrl)}
              onFocus={() => setIsInputFocused(true)}
              onBlur={() => {
                setIsInputFocused(false);
                commitServerUrl(serverUrl);
              }}
            />
          </Pressable>
          <ThemedText style={[styles.hint, urlError && styles.hintError]}>
            {urlError
              ? "地址无效,请填写 http(s) 地址;留空则使用内置来源"
              : settings.serverUrl
                ? "优先使用这个服务器;它匹配不到或访问失败时自动改用内置来源"
                : "当前使用内置来源(腾讯视频、哔哩哔哩的公开弹幕),不需要任何配置"}
          </ThemedText>
        </>
      )}
    </SettingsSection>
  );
});

DanmakuSection.displayName = "DanmakuSection";

const styles = StyleSheet.create({
  toggleRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingVertical: 12,
    borderRadius: 8,
  },
  rowFocused: {
    backgroundColor: "#007AFF10",
  },
  info: {
    flex: 1,
    marginRight: 12,
  },
  name: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 4,
  },
  status: {
    fontSize: 13,
    color: "#888",
  },
  statusOn: {
    color: Colors.dark.primary,
  },
  label: {
    fontSize: 14,
    fontWeight: "600",
    marginTop: 12,
    marginBottom: 8,
  },
  options: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
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
  hint: {
    fontSize: 12,
    color: "#888",
    marginTop: 8,
  },
  hintError: {
    color: "#ff6b6b",
  },
});
