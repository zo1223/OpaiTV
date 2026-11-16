import React, { useState, useEffect, useCallback, useRef, forwardRef } from "react";
import { View, Text, Image, StyleSheet, TouchableOpacity, Alert, Animated } from "react-native";
import { useRouter } from "expo-router";
import { Star, Play } from "lucide-react-native";
import { PlayRecordManager } from "@/services/storage";
import { API } from "@/services/api";
import { ThemedText } from "@/components/ThemedText";
import { Colors } from "@/constants/Colors";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { DeviceUtils } from "@/utils/DeviceUtils";
import Logger from '@/utils/Logger';
import type { PlayabilityStatus } from "@/stores/homeStore";

const logger = Logger.withTag('VideoCardTablet');

interface VideoCardTabletProps extends React.ComponentProps<typeof TouchableOpacity> {
  id: string;
  source: string;
  title: string;
  poster: string;
  year?: string;
  rate?: string;
  sourceName?: string;
  progress?: number;
  playTime?: number;
  episodeIndex?: number;
  totalEpisodes?: number;
  playabilityStatus?: PlayabilityStatus;
  playableSourceCount?: number;
  onFocus?: () => void;
  onRecordDeleted?: () => void;
  api: API;
}

const VideoCardTablet = forwardRef<View, VideoCardTabletProps>(
  (
    {
      id,
      source,
      title,
      poster,
      year,
      rate,
      sourceName,
      progress,
      episodeIndex,
      onFocus,
      onRecordDeleted,
      api,
      playabilityStatus,
      playableSourceCount,
      playTime = 0,
    }: VideoCardTabletProps,
    ref
  ) => {
    const router = useRouter();
    const { cardWidth, cardHeight, spacing } = useResponsiveLayout();
    const [fadeAnim] = useState(new Animated.Value(0));
    const [isPressed, setIsPressed] = useState(false);

    const longPressTriggered = useRef(false);
    const scale = useRef(new Animated.Value(1)).current;

    const handlePress = () => {
      if (longPressTriggered.current) {
        longPressTriggered.current = false;
        return;
      }
      
      if (progress !== undefined && episodeIndex !== undefined) {
        router.push({
          pathname: "/play",
          params: { source, id, episodeIndex: episodeIndex - 1, title, position: playTime * 1000 },
        });
      } else {
        router.push({
          pathname: "/detail",
          params: source === "douban" ? { q: title } : { source, id, q: title },
        });
      }
    };

    const handlePressIn = useCallback(() => {
      setIsPressed(true);
      Animated.spring(scale, {
        toValue: 0.96,
        damping: 15,
        stiffness: 300,
        useNativeDriver: true,
      }).start();
    }, [scale]);

    const handlePressOut = useCallback(() => {
      setIsPressed(false);
      Animated.spring(scale, {
        toValue: 1.0,
        damping: 15,
        stiffness: 300,
        useNativeDriver: true,
      }).start();
    }, [scale]);

    useEffect(() => {
      Animated.timing(fadeAnim, {
        toValue: 1,
        duration: DeviceUtils.getAnimationDuration(400),
        delay: Math.random() * 150,
        useNativeDriver: true,
      }).start();
    }, [fadeAnim]);

    const handleLongPress = () => {
      if (progress === undefined) return;

      longPressTriggered.current = true;

      Alert.alert("删除观看记录", `确定要删除"${title}"的观看记录吗？`, [
        {
          text: "取消",
          style: "cancel",
        },
        {
          text: "删除",
          style: "destructive",
          onPress: async () => {
            try {
              await PlayRecordManager.remove(source, id);
              onRecordDeleted?.();
            } catch (error) {
              logger.info("Failed to delete play record:", error);
              Alert.alert("错误", "删除观看记录失败，请重试");
            }
          },
        },
      ]);
    };

    const isContinueWatching = progress !== undefined && progress > 0 && progress < 1;
    const getPlayabilityBadgeText = () => {
      if (source !== "douban") return null;
      if (playabilityStatus === "playable") {
        return playableSourceCount && playableSourceCount > 1 ? `${playableSourceCount}源` : "可播";
      }
      if (playabilityStatus === "unavailable") return "暂无资源";
      return null;
    };
    const playabilityBadgeText = getPlayabilityBadgeText();

    const animatedStyle = {
      transform: [{ scale }],
    };

    const styles = createTabletStyles(cardWidth, cardHeight, spacing);

    return (
      <Animated.View style={[styles.wrapper, animatedStyle, { opacity: fadeAnim }]} ref={ref}>
        <TouchableOpacity
          onPress={handlePress}
          onPressIn={handlePressIn}
          onPressOut={handlePressOut}
          onLongPress={handleLongPress}
          style={styles.pressable}
          activeOpacity={1}
          delayLongPress={900}
        >
          <View style={[styles.card, isPressed && styles.cardPressed]}>
            <Image source={api.getImageSource(poster)} style={styles.poster} />
            
            {/* 悬停效果遮罩 */}
            {isPressed && (
              <View style={styles.pressOverlay}>
                {isContinueWatching && (
                  <View style={styles.continueWatchingBadge}>
                    <Play size={16} color="#ffffff" fill="#ffffff" />
                    <Text style={styles.continueWatchingText}>继续观看</Text>
                  </View>
                )}
              </View>
            )}

            {/* 进度条 */}
            {isContinueWatching && (
              <View style={styles.progressContainer}>
                <View style={[styles.progressBar, { width: `${(progress || 0) * 100}%` }]} />
              </View>
            )}

            {/* 评分 */}
            {rate && (
              <View style={styles.ratingContainer}>
                <Star size={12} color="#FFD700" fill="#FFD700" />
                <Text style={styles.ratingText}>{rate}</Text>
              </View>
            )}

            {/* 年份 */}
            {year && (
              <View style={styles.yearBadge}>
                <Text style={styles.badgeText}>{year}</Text>
              </View>
            )}

            {/* 来源 */}
            {sourceName && (
              <View style={styles.sourceNameBadge}>
                <Text style={styles.badgeText}>{sourceName}</Text>
              </View>
            )}

            {playabilityBadgeText && (
              <View style={[styles.playabilityBadge, playabilityStatus === "unavailable" && styles.unavailableBadge]}>
                <Text style={styles.playabilityBadgeText}>{playabilityBadgeText}</Text>
              </View>
            )}
          </View>

          <View style={styles.infoContainer}>
            <ThemedText numberOfLines={2} style={styles.title}>{title}</ThemedText>
            {isContinueWatching && (
              <View style={styles.infoRow}>
                <ThemedText style={styles.continueLabel} numberOfLines={1}>
                  第{episodeIndex}集 已观看 {Math.round((progress || 0) * 100)}%
                </ThemedText>
              </View>
            )}
          </View>
        </TouchableOpacity>
      </Animated.View>
    );
  }
);

VideoCardTablet.displayName = "VideoCardTablet";

const createTabletStyles = (cardWidth: number, cardHeight: number, spacing: number) => {
  return StyleSheet.create({
    wrapper: {
      width: cardWidth,
      marginHorizontal: spacing / 2,
      marginBottom: spacing,
    },
    pressable: {
      alignItems: 'center',
    },
    card: {
      width: cardWidth,
      height: cardHeight,
      borderRadius: 10,
      backgroundColor: "#222",
      overflow: "hidden",
    },
    cardPressed: {
      borderColor: Colors.dark.primary,
      borderWidth: 2,
    },
    poster: {
      width: "100%",
      height: "100%",
      resizeMode: 'cover',
    },
    pressOverlay: {
      ...StyleSheet.absoluteFillObject,
      backgroundColor: "rgba(0,0,0,0.4)",
      justifyContent: "center",
      alignItems: "center",
      borderRadius: 10,
    },
    progressContainer: {
      position: "absolute",
      bottom: 0,
      left: 0,
      right: 0,
      height: 4,
      backgroundColor: "rgba(0, 0, 0, 0.8)",
    },
    progressBar: {
      height: 4,
      backgroundColor: Colors.dark.primary,
    },
    continueWatchingBadge: {
      flexDirection: "row",
      alignItems: "center",
      backgroundColor: Colors.dark.primary,
      paddingHorizontal: 12,
      paddingVertical: 6,
      borderRadius: 6,
    },
    continueWatchingText: {
      color: "white",
      marginLeft: 6,
      fontSize: 14,
      fontWeight: "bold",
    },
    ratingContainer: {
      position: "absolute",
      top: 8,
      right: 8,
      flexDirection: "row",
      alignItems: "center",
      backgroundColor: "rgba(0, 0, 0, 0.7)",
      borderRadius: 6,
      paddingHorizontal: 6,
      paddingVertical: 3,
    },
    ratingText: {
      color: "#FFD700",
      fontSize: 11,
      fontWeight: "bold",
      marginLeft: 3,
    },
    yearBadge: {
      position: "absolute",
      top: 8,
      right: 8,
      backgroundColor: "rgba(0, 0, 0, 0.7)",
      borderRadius: 6,
      paddingHorizontal: 6,
      paddingVertical: 3,
    },
    sourceNameBadge: {
      position: "absolute",
      top: 8,
      left: 8,
      backgroundColor: "rgba(0, 0, 0, 0.7)",
      borderRadius: 6,
      paddingHorizontal: 6,
      paddingVertical: 3,
    },
    playabilityBadge: {
      position: "absolute",
      bottom: 8,
      left: 8,
      backgroundColor: Colors.dark.primary,
      borderRadius: 6,
      paddingHorizontal: 6,
      paddingVertical: 3,
    },
    unavailableBadge: {
      backgroundColor: "rgba(0, 0, 0, 0.7)",
    },
    playabilityBadgeText: {
      color: "white",
      fontSize: 11,
      fontWeight: "bold",
    },
    badgeText: {
      color: "white",
      fontSize: 11,
      fontWeight: "bold",
    },
    infoContainer: {
      width: cardWidth,
      marginTop: 8,
      alignItems: "flex-start",
      paddingHorizontal: 4,
    },
    infoRow: {
      flexDirection: "row",
      justifyContent: "space-between",
      width: "100%",
      marginTop: 2,
    },
    title: {
      fontSize: 15,
      lineHeight: 18,
    },
    continueLabel: {
      color: Colors.dark.primary,
      fontSize: 12,
    },
  });
};

export default VideoCardTablet;
