import React, { forwardRef, useImperativeHandle, useRef, useState } from "react";
import { Animated, Platform, StyleSheet, TextInput, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { useButtonAnimation } from "@/hooks/useAnimation";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { useSettingsStore } from "@/stores/settingsStore";
import { Colors } from "@/constants/Colors";
import { SettingsSection } from "./SettingsSection";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";
import { setNativeTextInputSelection } from "@/utils/textInputSelection";

interface VideoSourceSubscriptionSectionProps {
  onChanged: () => void;
  onFocus?: () => void;
  onBlur?: () => void;
  onPress?: () => void;
}

export interface VideoSourceSubscriptionSectionRef {
  setInputValue: (value: string) => void;
}

export const VideoSourceSubscriptionSection = forwardRef<
  VideoSourceSubscriptionSectionRef,
  VideoSourceSubscriptionSectionProps
>(({ onChanged, onFocus, onBlur, onPress }, ref) => {
  const { videoSourceSubscriptionUrl, subscribedVideoSources, setVideoSourceSubscriptionUrl } = useSettingsStore();
  const [isInputFocused, setIsInputFocused] = useState(false);
  const [isSectionFocused, setIsSectionFocused] = useState(false);
  const [selection, setSelection] = useState({ start: 0, end: 0 });
  const inputRef = useRef<TextInput>(null);
  const inputAnimationStyle = useButtonAnimation(isSectionFocused, 1.01);
  const deviceType = useResponsiveLayout().deviceType;

  const handleUrlChange = (url: string) => {
    setVideoSourceSubscriptionUrl(url);
    onChanged();
  };

  useImperativeHandle(ref, () => ({
    setInputValue: (value: string) => {
      setVideoSourceSubscriptionUrl(value);
      onChanged();
    },
  }));

  const handleSectionFocus = () => {
    setIsSectionFocused(true);
    onFocus?.();
  };

  const handleSectionBlur = () => {
    setIsSectionFocused(false);
    onBlur?.();
  };

  const handlePress = () => {
    inputRef.current?.focus();
    onPress?.();
  };

  useSafeTVEventHandler(
    React.useCallback(
      (event: any) => {
        if (isSectionFocused && event.eventType === "select") {
          inputRef.current?.focus();
        }
      },
      [isSectionFocused]
    )
  );

  const handleInputFocus = () => {
    setIsInputFocused(true);
    const end = videoSourceSubscriptionUrl.length;
    setSelection({ start: end, end });
    setTimeout(() => {
      setNativeTextInputSelection(inputRef.current, { start: end, end });
    }, 0);
  };

  return (
    <SettingsSection
      focusable
      onFocus={handleSectionFocus}
      onBlur={handleSectionBlur}
      onPress={Platform.isTV || deviceType !== "tv" ? undefined : handlePress}
    >
      <View style={styles.inputContainer}>
        <View style={styles.titleContainer}>
          <ThemedText style={styles.sectionTitle}>影视源订阅链接</ThemedText>
          {subscribedVideoSources.length > 0 && (
            <ThemedText style={styles.subtitle}>已加载 {subscribedVideoSources.length} 个源</ThemedText>
          )}
        </View>
        <Animated.View style={inputAnimationStyle}>
          <TextInput
            ref={inputRef}
            style={[styles.input, isInputFocused && styles.inputFocused]}
            value={videoSourceSubscriptionUrl}
            onChangeText={handleUrlChange}
            placeholder="输入 Base58 订阅链接或 JSON 订阅地址"
            placeholderTextColor="#9AA1B8"
            autoCapitalize="none"
            autoCorrect={false}
            onFocus={handleInputFocus}
            selection={selection}
            onSelectionChange={({ nativeEvent }) => setSelection(nativeEvent.selection)}
            onBlur={() => setIsInputFocused(false)}
          />
        </Animated.View>
      </View>
    </SettingsSection>
  );
});

VideoSourceSubscriptionSection.displayName = "VideoSourceSubscriptionSection";

const styles = StyleSheet.create({
  titleContainer: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 8,
  },
  sectionTitle: {
    fontSize: 16,
    fontWeight: "bold",
    marginRight: 12,
  },
  subtitle: {
    fontSize: 12,
    color: "#9AA1B8",
    fontStyle: "italic",
  },
  inputContainer: {
    marginBottom: 12,
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
    shadowColor: Colors.dark.primary,
    shadowOffset: { width: 0, height: 0 },
    shadowOpacity: 0.8,
    shadowRadius: 10,
    elevation: 5,
  },
});
