import React, { useState } from "react";
import { StyleSheet, Pressable, Platform } from "react-native";
import { ThemedView } from "@/components/ThemedView";
import { Colors } from "@/constants/Colors";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";

interface SettingsSectionProps {
  children: React.ReactNode;
  onFocus?: () => void;
  onBlur?: () => void;
  onPress?: () => void;
  focusable?: boolean;
}

export const SettingsSection: React.FC<SettingsSectionProps> = ({ children, onFocus, onBlur, onPress, focusable = false }) => {
  const [isFocused, setIsFocused] = useState(false);
  const deviceType = useResponsiveLayout().deviceType;

  const handleFocus = () => {
    setIsFocused(true);
    onFocus?.();
  };

  const handleBlur = () => {
    setIsFocused(false);
    onBlur?.();
  };

  const handlePress = () => {
    onPress?.();
  }

  if (!focusable) {
    return <ThemedView style={styles.section}>{children}</ThemedView>;
  }

  return (
    <ThemedView style={[styles.section, isFocused && styles.sectionFocused]}>
      <Pressable
        android_ripple={Platform.isTV||deviceType !=='tv'? {color:'transparent'}:{color:Colors.dark.link}}
        style={styles.sectionPressable}
        // {...(Platform.isTV ? {onFocus: handleFocus, onBlur: handleBlur} : {onPress: onPress})}
        onFocus={handleFocus}
        onBlur={handleBlur}
        onPress={handlePress}
      >
        {children}
      </Pressable>
    </ThemedView>
  );
};

const styles = StyleSheet.create({
  section: {
    padding: 20,
    marginBottom: 16,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#333",
  },
  sectionFocused: {
    borderColor: Colors.dark.primary,
    backgroundColor: "#007AFF10",
  },
  sectionPressable: {
    width: "100%",
  },
});
