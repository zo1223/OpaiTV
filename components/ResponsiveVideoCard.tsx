import React, { useState, useEffect, useCallback, useRef, forwardRef } from "react";
import { View, Text, Image, StyleSheet, TouchableOpacity, Animated } from "react-native";
import { useRouter } from "expo-router";
import { Star, Play } from "lucide-react-native";
import { usePlayRecordUndoStore } from "@/stores/playRecordUndoStore";
import { API } from "@/services/api";
import { ThemedText } from "@/components/ThemedText";
import { Colors } from "@/constants/Colors";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { DeviceUtils } from "@/utils/DeviceUtils";
import { usePressScale } from "@/hooks/usePressScale";


interface VideoCardProps extends React.ComponentProps<typeof TouchableOpacity> {
  id: string;
  source: string;
  title: string;
  poster: string;
  year?: string;
  rate?: string;
  sourceName?: string;
  progress?: number; // 播放进度，0-1之间的小数
  playTime?: number; // 播放时间 in ms
  episodeIndex?: number; // 剧集索引
  totalEpisodes?: number; // 总集数
  onFocus?: () => void;
  onRecordDeleted?: () => void; // 添加回调属性
  api: API;
}

const ResponsiveVideoCard = forwardRef<View, VideoCardProps>(
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
      playTime = 0,
    }: VideoCardProps,
    ref
  ) => {
    const router = useRouter();
    const [isFocused, setIsFocused] = useState(false);
    const [fadeAnim] = useState(new Animated.Value(0));
    const responsiveConfig = useResponsiveLayout();

    const longPressTriggered = useRef(false);
    const scale = useRef(new Animated.Value(1)).current;

    // 触屏设备:按下缩小一点、松开弹回。TV 上不启用,焦点放大保持原样
    const { scale: pressScale, pressHandlers } = usePressScale({ disabled: responsiveConfig.deviceType === 'tv' });

    const animatedStyle = {
      transform: [{ scale }, { scale: pressScale }],
    };

    const handlePress = () => {
      if (longPressTriggered.current) {
        longPressTriggered.current = false;
        return;
      }
      // 如果有播放进度，直接转到播放页面
      if (progress !== undefined && episodeIndex !== undefined) {
        router.push({
          pathname: "/play",
          params: { source, id, episodeIndex: episodeIndex - 1, title, position: playTime * 1000 },
        });
      } else {
        router.push({
          pathname: "/detail",
          params: source === "douban" ? { q: title } : { source, q: title },
        });
      }
    };

    const handleFocus = useCallback(() => {
      // Only apply focus scaling for TV devices
      if (responsiveConfig.deviceType === 'tv') {
        setIsFocused(true);
        Animated.spring(scale, {
          toValue: 1.05,
          damping: 15,
          stiffness: 200,
          useNativeDriver: true,
        }).start();
      }
      onFocus?.();
    }, [scale, onFocus, responsiveConfig.deviceType]);

    const handleBlur = useCallback(() => {
      if (responsiveConfig.deviceType === 'tv') {
        setIsFocused(false);
        Animated.spring(scale, {
          toValue: 1.0,
          useNativeDriver: true,
        }).start();
      }
    }, [scale, responsiveConfig.deviceType]);

    useEffect(() => {
      Animated.timing(fadeAnim, {
        toValue: 1,
        duration: DeviceUtils.getAnimationDuration(400),
        delay: Math.random() * 200, // 随机延迟创建交错效果
        useNativeDriver: true,
      }).start();
    }, [fadeAnim]);

    const handleLongPress = () => {
      // Only allow long press for items with progress (play records)
      if (progress === undefined) return;

      longPressTriggered.current = true;

      // 不再弹确认框:记录先从列表隐藏,底部出现"撤销"提示条,撤销窗口结束后才真正删除
      usePlayRecordUndoStore.getState().requestDelete({ source, id, title });
      onRecordDeleted?.();
    };

    // 是否是继续观看的视频
    const isContinueWatching = progress !== undefined && progress > 0 && progress < 1;

    // Dynamic styles based on device type
    const cardWidth = responsiveConfig.cardWidth;
    const cardHeight = responsiveConfig.cardHeight;

    const dynamicStyles = StyleSheet.create({
      wrapper: {
        marginHorizontal: responsiveConfig.spacing / 2,
      },
      card: {
        width: cardWidth,
        height: cardHeight,
        borderRadius: responsiveConfig.deviceType === 'tablet' ? 16 : 14,
        backgroundColor: "#1B1F33",
        overflow: "hidden",
      },
      infoContainer: {
        width: cardWidth,
        marginTop: responsiveConfig.spacing / 2,
        alignItems: "flex-start",
        marginBottom: responsiveConfig.spacing,
        paddingHorizontal: 4,
      },
      overlay: {
        ...StyleSheet.absoluteFillObject,
        backgroundColor: "rgba(0,0,0,0.3)",
        borderColor: Colors.dark.primary,
        borderWidth: responsiveConfig.deviceType === 'tv' ? 2 : 0,
        borderRadius: responsiveConfig.deviceType === 'tablet' ? 16 : 14,
        justifyContent: "center",
        alignItems: "center",
      },
      continueWatchingBadge: {
        flexDirection: "row",
        alignItems: "center",
        backgroundColor: "#E3B26B",
        paddingHorizontal: responsiveConfig.deviceType === 'mobile' ? 8 : 10,
        paddingVertical: responsiveConfig.deviceType === 'mobile' ? 4 : 5,
        borderRadius: 5,
      },
      continueWatchingText: {
        color: "#1A1206",
        marginLeft: 5,
        fontSize: responsiveConfig.deviceType === 'mobile' ? 10 : 12,
        fontWeight: "bold",
      },
    });

    return (
      <Animated.View style={[dynamicStyles.wrapper, animatedStyle, { opacity: fadeAnim }]}>
        <TouchableOpacity
          onPress={handlePress}
          onLongPress={handleLongPress}
          {...pressHandlers}
          onFocus={handleFocus}
          onBlur={handleBlur}
          style={styles.pressable}
          activeOpacity={responsiveConfig.deviceType === 'tv' ? 1 : 0.8}
          delayLongPress={responsiveConfig.deviceType === 'mobile' ? 500 : 1000}
        >
          <View style={dynamicStyles.card}>
            <Image source={api.getImageSource(poster)} style={styles.poster} />
            {(isFocused && responsiveConfig.deviceType === 'tv') && (
              <View style={dynamicStyles.overlay}>
                {isContinueWatching && (
                  <View style={dynamicStyles.continueWatchingBadge}>
                    <Play size={responsiveConfig.deviceType === 'tv' ? 16 : 12} color="#1A1206" fill="#1A1206" />
                    <ThemedText style={dynamicStyles.continueWatchingText}>继续观看</ThemedText>
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

            {rate && (
              <View style={[styles.ratingContainer, { 
                top: responsiveConfig.spacing / 2,
                right: responsiveConfig.spacing / 2 
              }]}>
                <Star size={responsiveConfig.deviceType === 'mobile' ? 10 : 12} color="#E3B26B" fill="#E3B26B" />
                <ThemedText style={[styles.ratingText, { 
                  fontSize: responsiveConfig.deviceType === 'mobile' ? 10 : 12 
                }]}>{rate}</ThemedText>
              </View>
            )}
            {year && (
              <View style={[styles.yearBadge, { 
                top: responsiveConfig.spacing / 2,
                right: responsiveConfig.spacing / 2 
              }]}>
                <Text style={[styles.badgeText, { 
                  fontSize: responsiveConfig.deviceType === 'mobile' ? 10 : 12 
                }]}>{year}</Text>
              </View>
            )}
            {sourceName && (
              <View style={[styles.sourceNameBadge, { 
                top: responsiveConfig.spacing / 2,
                left: responsiveConfig.spacing / 2 
              }]}>
                <Text style={[styles.badgeText, { 
                  fontSize: responsiveConfig.deviceType === 'mobile' ? 10 : 12 
                }]}>{sourceName}</Text>
              </View>
            )}
          </View>
          <View style={dynamicStyles.infoContainer}>
            <ThemedText 
              numberOfLines={responsiveConfig.deviceType === 'mobile' ? 2 : 1}
              style={{ 
                fontSize: responsiveConfig.deviceType === 'mobile' ? 14 : 16,
                lineHeight: responsiveConfig.deviceType === 'mobile' ? 18 : 20,
              }}
            >
              {title}
            </ThemedText>
            {isContinueWatching && (
              <View style={styles.infoRow}>
                <ThemedText style={[styles.continueLabel, { 
                  fontSize: responsiveConfig.deviceType === 'mobile' ? 10 : 12 
                }]}>
                  第{episodeIndex! + 1}集 已观看 {Math.round((progress || 0) * 100)}%
                </ThemedText>
              </View>
            )}
          </View>
        </TouchableOpacity>
      </Animated.View>
    );
  }
);

ResponsiveVideoCard.displayName = "ResponsiveVideoCard";

export default ResponsiveVideoCard;

const styles = StyleSheet.create({
  pressable: {
    alignItems: "center",
  },
  poster: {
    width: "100%",
    height: "100%",
  },
  buttonRow: {
    position: "absolute",
    top: 8,
    left: 8,
    flexDirection: "row",
    gap: 8,
  },
  iconButton: {
    padding: 4,
  },
  favButton: {
    position: "absolute",
    top: 8,
    left: 8,
  },
  ratingContainer: {
    position: "absolute",
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "rgba(0, 0, 0, 0.7)",
    borderRadius: 6,
    paddingHorizontal: 6,
    paddingVertical: 3,
  },
  ratingText: {
    color: "#E3B26B",
    fontWeight: "bold",
    marginLeft: 4,
  },
  infoRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    width: "100%",
  },
  title: {
    color: "white",
    fontSize: 16,
    fontWeight: "bold",
    textAlign: "center",
  },
  yearBadge: {
    position: "absolute",
    backgroundColor: "rgba(0, 0, 0, 0.7)",
    borderRadius: 6,
    paddingHorizontal: 6,
    paddingVertical: 3,
  },
  sourceNameBadge: {
    position: "absolute",
    backgroundColor: "rgba(0, 0, 0, 0.7)",
    borderRadius: 6,
    paddingHorizontal: 6,
    paddingVertical: 3,
  },
  badgeText: {
    color: "white",
    fontWeight: "bold",
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
  continueLabel: {
    color: Colors.dark.primary,
  },
});
