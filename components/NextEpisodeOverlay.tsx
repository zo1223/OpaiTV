import React from "react";
import { View, StyleSheet } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "./StyledButton";

interface NextEpisodeOverlayProps {
  visible: boolean;
  onCancel: () => void;
}

export const NextEpisodeOverlay: React.FC<NextEpisodeOverlayProps> = ({ visible, onCancel }) => {
  if (!visible) {
    return null;
  }

  return (
    <View style={styles.nextEpisodeOverlay}>
      <View style={styles.nextEpisodeContent}>
        <ThemedText style={styles.nextEpisodeTitle}>即将播放下一集...</ThemedText>
        <StyledButton
          text="取消"
          onPress={onCancel}
          style={styles.nextEpisodeButton}
          textStyle={styles.nextEpisodeButtonText}
        />
      </View>
    </View>
  );
};

const styles = StyleSheet.create({
  nextEpisodeOverlay: {
    position: "absolute",
    right: 40,
    bottom: 60,
    backgroundColor: "rgba(0,0,0,0.8)",
    borderRadius: 8,
    padding: 15,
    width: 250,
  },
  nextEpisodeContent: {
    alignItems: "center",
  },
  nextEpisodeTitle: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 10,
  },
  nextEpisodeButton: {
    padding: 8,
    paddingHorizontal: 15,
  },
  nextEpisodeButtonText: {
    fontSize: 14,
  },
});
