import React, { useState, useEffect, forwardRef } from "react";
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

const logger = Logger.withTag('VideoCardMobile');

interface VideoCardMobileProps extends React.ComponentProps<typeof TouchableOpacity> {
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

const VideoCardMobile = forwardRef<View, VideoCardMobileProps>(
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
    }: VideoCardMobileProps,
    ref
  ) => {
    const router = useRouter();
    const { cardWidth, cardHeight, spacing } = useResponsiveLayout();
    const [fadeAnim] = useState(new Animated.Value(0));

    const handlePress = () => {
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

    useEffect(() => {
      Animated.timing(fadeAnim, {
        toValue: 1,
        duration: DeviceUtils.getAnimationDuration(300),
        delay: Math.random() * 100,
        useNativeDriver: true,
      }).start();
    }, [fadeAnim]);

    const handleLongPress = () => {
      if (progress === undefined) return;

      // 触摸长按松手后不会再触发 onPress,这里不能留"已长按"标记,否则对话框关闭后的第一次点击会被吞掉

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

    const styles = createMobileStyles(cardWidth, cardHeight, spacing);

    return (
      <Animated.View style={[styles.wrapper, { opacity: fadeAnim }]} ref={ref}>
        <TouchableOpacity
          onPress={handlePress}
          onLongPress={handleLongPress}
          style={styles.pressable}
          activeOpacity={0.8}
          delayLongPress={800}
        >
          <View style={styles.card}>
            <Image source={api.getImageSource(poster)} style={styles.poster} />
            
            {/* 进度条 */}
            {isContinueWatching && (
              <View style={styles.progressContainer}>
                <View style={[styles.progressBar, { width: `${(progress || 0) * 100}%` }]} />
              </View>
            )}

            {/* 继续观看标识 */}
            {isContinueWatching && (
              <View style={styles.continueWatchingBadge}>
                <Play size={12} color="#ffffff" fill="#ffffff" />
                <Text style={styles.continueWatchingText}>继续</Text>
              </View>
            )}

            {/* 评分 */}
            {rate && (
              <View style={styles.ratingContainer}>
                <Star size={10} color="#FFD700" fill="#FFD700" />
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
              <ThemedText style={styles.continueLabel} numberOfLines={1}>
                第{episodeIndex}集 {Math.round((progress || 0) * 100)}%
              </ThemedText>
            )}
          </View>
        </TouchableOpacity>
      </Animated.View>
    );
  }
);

VideoCardMobile.displayName = "VideoCardMobile";

const createMobileStyles = (cardWidth: number, cardHeight: number, spacing: number) => {
  return StyleSheet.create({
    wrapper: {
      width: cardWidth,
      marginBottom: spacing,
    },
    pressable: {
      alignItems: 'flex-start',
    },
    card: {
      width: cardWidth,
      height: cardHeight,
      borderRadius: 8,
      backgroundColor: "#222",
      overflow: "hidden",
    },
    poster: {
      width: "100%",
      height: "100%",
      resizeMode: 'cover',
    },
    progressContainer: {
      position: "absolute",
      bottom: 0,
      left: 0,
      right: 0,
      height: 3,
      backgroundColor: "rgba(0, 0, 0, 0.6)",
    },
    progressBar: {
      height: 3,
      backgroundColor: Colors.dark.primary,
    },
    continueWatchingBadge: {
      position: 'absolute',
      top: 6,
      left: 6,
      flexDirection: 'row',
      alignItems: 'center',
      backgroundColor: Colors.dark.primary,
      paddingHorizontal: 6,
      paddingVertical: 3,
      borderRadius: 4,
    },
    continueWatchingText: {
      color: "white",
      marginLeft: 3,
      fontSize: 10,
      fontWeight: "bold",
    },
    ratingContainer: {
      position: "absolute",
      top: 6,
      right: 6,
      flexDirection: "row",
      alignItems: "center",
      backgroundColor: "rgba(0, 0, 0, 0.7)",
      borderRadius: 4,
      paddingHorizontal: 4,
      paddingVertical: 2,
    },
    ratingText: {
      color: "#FFD700",
      fontSize: 10,
      fontWeight: "bold",
      marginLeft: 2,
    },
    yearBadge: {
      position: "absolute",
      bottom: 24,
      right: 6,
      backgroundColor: "rgba(0, 0, 0, 0.7)",
      borderRadius: 4,
      paddingHorizontal: 4,
      paddingVertical: 2,
    },
    sourceNameBadge: {
      position: "absolute",
      bottom: 6,
      left: 6,
      backgroundColor: "rgba(0, 0, 0, 0.7)",
      borderRadius: 4,
      paddingHorizontal: 4,
      paddingVertical: 2,
    },
    playabilityBadge: {
      position: "absolute",
      bottom: 6,
      left: 6,
      backgroundColor: Colors.dark.primary,
      borderRadius: 4,
      paddingHorizontal: 5,
      paddingVertical: 2,
    },
    unavailableBadge: {
      backgroundColor: "rgba(0, 0, 0, 0.7)",
    },
    playabilityBadgeText: {
      color: "white",
      fontSize: 9,
      fontWeight: "700",
    },
    badgeText: {
      color: "white",
      fontSize: 9,
      fontWeight: "500",
    },
    infoContainer: {
      width: cardWidth,
      marginTop: 6,
      paddingHorizontal: 2,
    },
    title: {
      fontSize: 13,
      lineHeight: 16,
      marginBottom: 2,
    },
    continueLabel: {
      color: Colors.dark.primary,
      fontSize: 11,
    },
  });
};

export default VideoCardMobile;
