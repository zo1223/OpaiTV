import React, { ComponentProps } from "react";
import { StyledButton } from "./StyledButton";
import { StyleSheet, View, Text } from "react-native";

type StyledButtonProps = ComponentProps<typeof StyledButton> & {
  timeLabel?: string;
};

export const MediaButton = ({ timeLabel, ...props }: StyledButtonProps) => (
  <View>
    <StyledButton {...props} style={[styles.mediaControlButton, props.style]} variant="ghost" />
    {timeLabel && <Text style={styles.timeLabel}>{timeLabel}</Text>}
  </View>
);

const styles = StyleSheet.create({
  mediaControlButton: {
    padding: 12,
    minWidth: 80,
  },
  timeLabel: {
    position: "absolute",
    top: 14,
    right: 12,
    color: "white",
    fontSize: 10,
    fontWeight: "bold",
    backgroundColor: "rgba(0,0,0,0.6)",
    paddingHorizontal: 4,
    borderRadius: 3,
  },
});
