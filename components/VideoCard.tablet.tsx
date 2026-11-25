import React, { useState, useCallback, forwardRef } from "react";
import { resolveDirectPlayParams } from "@/services/directPlay";
import { View, Text, StyleSheet, TouchableOpacity } from "react-native";
import { useRouter } from "expo-router";
import { Star, Play } from "lucide-react-native";
import { usePlayRecordUndoStore } from "@/stores/playRecordUndoStore";
import { API } from "@/services/api";
import { ThemedText } from "@/components/ThemedText";
import { Colors } from "@/constants/Colors";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { usePressScale } from "@/hooks/usePressScale";
import { FadeInView } from "@/components/FadeInView";
import { FadeInImage } from "@/components/FadeInImage";
import type { PlayabilityStatus } from "@/stores/homeStore";


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
  reason?: string;
  directPlay?: boolean;
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
      reason,
      directPlay,
      playTime = 0,
    }: VideoCardTabletProps,
    ref
  ) => {
    const router = useRouter();
    const { cardWidth, cardHeight, spacing } = useResponsiveLayout();
    const [isPressed, setIsPressed] = useState(false);

    // 按下缩小一点、松开弹回(共用 hook);按下时的描边与遮罩仍由 isPressed 控制
    const markPressed = useCallback(() => setIsPressed(true), []);
    const markReleased = useCallback(() => setIsPressed(false), []);
    const { scale, pressHandlers } = usePressScale({ onPressIn: markPressed, onPressOut: markReleased });

    const handlePress = () => {
      if (progress !== undefined && episodeIndex !== undefined) {
        router.push({
          pathname: "/play",
          params: { source, id, episodeIndex: episodeIndex - 1, title, position: playTime * 1000 },
        });
      } else if (directPlay && source !== "douban") {
        // 搜索结果:直接起播,不先进详情页
        void resolveDirectPlayParams(source, id, title).then((params) => {
          router.push({ pathname: "/play", params: { ...params } });
        });
      } else {
        router.push({
          pathname: "/detail",
          params: source === "douban" ? { q: title } : { source, id, q: title },
        });
      }
    };

    const handleLongPress = () => {
      if (progress === undefined) return;

      // 触摸长按松手后不会再触发 onPress,这里不能留"已长按"标记,否则对话框关闭后的第一次点击会被吞掉

      // 不再弹确认框:记录先从列表隐藏,底部出现"撤销"提示条,撤销窗口结束后才真正删除
      usePlayRecordUndoStore.getState().requestDelete({ source, id, title });
      onRecordDeleted?.();
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

    const styles = createTabletStyles(cardWidth, cardHeight, spacing);

    return (
      <FadeInView style={styles.wrapper} stagger extraTransform={[{ scale }]} ref={ref}>
        <TouchableOpacity
          onPress={handlePress}
          {...pressHandlers}
          onLongPress={handleLongPress}
          style={styles.pressable}
          activeOpacity={1}
          delayLongPress={900}
        >
          <View style={[styles.card, isPressed && styles.cardPressed]}>
            <FadeInImage source={api.getImageSource(poster)} style={styles.poster} />
            
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
            {reason ? (
              <ThemedText numberOfLines={2} style={{ fontSize: 11, lineHeight: 15, color: "#8fb8ff", marginTop: 2 }}>
                {reason}
              </ThemedText>
            ) : null}
            {isContinueWatching && (
              <View style={styles.infoRow}>
                <ThemedText style={styles.continueLabel} numberOfLines={1}>
                  第{episodeIndex}集 已观看 {Math.round((progress || 0) * 100)}%
                </ThemedText>
              </View>
            )}
          </View>
        </TouchableOpacity>
      </FadeInView>
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
