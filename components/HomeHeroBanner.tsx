import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Animated, Image, PanResponder, Pressable, StyleSheet, Text, View } from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { useFocusEffect, useRouter } from "expo-router";
import { Play } from "lucide-react-native";
import { api } from "@/services/api";
import { resolveDirectPlayParams } from "@/services/directPlay";
import { computeLocalRecommendations } from "@/stores/homeStore";
import { useSettingsStore } from "@/stores/settingsStore";
import { useContentVisibilityKey } from "@/hooks/useContentVisibilityKey";
import {
  HERO_BANNER_INTERVAL_MS,
  HeroBannerItem,
  HeroBannerSource,
  heroBannerMeta,
  pickHeroBannerItems,
  stepHeroIndex,
} from "@/services/heroBanner";
import { Colors } from "@/constants/Colors";

/**
 * 横幅的数据:个性化推荐(开关开着时) + 首页当前已加载的条目。
 * 两样都是首页已有的、已经过滤后可见的数据;这里不发任何请求,也不自己做过滤。
 * 18+ 开关 / 成人源 / 片源模式一变,推荐按新状态重算,旧状态下算出来的结果直接作废。
 */
export function useHeroBannerItems(homeItems: HeroBannerSource[], enabled: boolean): HeroBannerItem[] {
  const recommendationsEnabled = useSettingsStore((state) => state.recommendationsEnabled);
  const visibilityKey = useContentVisibilityKey();
  const [recommended, setRecommended] = useState<{ key: string; items: HeroBannerSource[] }>({ key: "", items: [] });
  const wantRecommendations = enabled && recommendationsEnabled !== false;
  // 首页条目加载出来之后候选池才有内容,所以“有没有首页条目”变化时再算一次
  const hasHomeItems = homeItems.length > 0;

  useEffect(() => {
    if (!wantRecommendations) return;
    let cancelled = false;
    computeLocalRecommendations()
      .then((items) => {
        if (!cancelled) setRecommended({ key: visibilityKey, items });
      })
      .catch(() => {
        if (!cancelled) setRecommended({ key: visibilityKey, items: [] });
      });
    return () => {
      cancelled = true;
    };
  }, [wantRecommendations, visibilityKey, hasHomeItems]);

  return useMemo(
    () =>
      enabled
        ? pickHeroBannerItems({
            recommendations: wantRecommendations && recommended.key === visibilityKey ? recommended.items : [],
            homeItems,
            recommendationsEnabled: wantRecommendations,
          })
        : [],
    [enabled, wantRecommendations, recommended, visibilityKey, homeItems]
  );
}

interface HomeHeroBannerProps {
  items: HeroBannerItem[];
  intervalMs?: number;
  height?: number;
}

/** 首页顶部的推荐横幅:轮播、海报、标题、简介 / 元信息、播放(直接起播)与详情。0 条不渲染,1 条不轮播。 */
export const HomeHeroBanner: React.FC<HomeHeroBannerProps> = ({
  items,
  intervalMs = HERO_BANNER_INTERVAL_MS,
  height = 200,
}) => {
  const router = useRouter();
  const [index, setIndex] = useState(0);
  const [focused, setFocused] = useState(true);
  // 手动切换后重新计时,免得刚切过去就被自动轮播翻走
  const [manualSeq, setManualSeq] = useState(0);
  const fade = useRef(new Animated.Value(1)).current;
  const playingRef = useRef(false);
  const count = items.length;
  const countRef = useRef(count);
  countRef.current = count;
  const safeIndex = count > 0 ? Math.min(index, count - 1) : 0;
  const current = count > 0 ? items[safeIndex] : null;
  const currentKey = current ? `${current.source}+${current.id}` : "";

  // 页面失焦(进了详情 / 播放页)时停表,回来再接着转
  useFocusEffect(
    useCallback(() => {
      setFocused(true);
      return () => setFocused(false);
    }, [])
  );

  useEffect(() => {
    if (count <= 1 || !focused) return;
    const timer = setInterval(() => {
      setIndex((value) => stepHeroIndex(Math.min(value, count - 1), count, 1));
    }, intervalMs);
    return () => clearInterval(timer);
  }, [count, focused, intervalMs, manualSeq]);

  useEffect(() => {
    if (!currentKey) return;
    fade.setValue(0);
    const animation = Animated.timing(fade, { toValue: 1, duration: 350, useNativeDriver: true });
    animation.start();
    return () => animation.stop();
  }, [currentKey, fade]);

  const go = useCallback((delta: number) => {
    const total = countRef.current;
    if (total <= 1) return;
    setIndex((value) => stepHeroIndex(Math.min(value, total - 1), total, delta));
    setManualSeq((value) => value + 1);
  }, []);

  const jump = useCallback((target: number) => {
    setIndex(target);
    setManualSeq((value) => value + 1);
  }, []);

  // 只接明显的横向滑动,纵向滚动照常交给外层列表
  const pan = useMemo(
    () =>
      PanResponder.create({
        onMoveShouldSetPanResponder: (_event, gesture) =>
          countRef.current > 1 && Math.abs(gesture.dx) > 16 && Math.abs(gesture.dx) > Math.abs(gesture.dy) * 1.5,
        onPanResponderTerminationRequest: () => true,
        onPanResponderRelease: (_event, gesture) => {
          if (gesture.dx <= -40) go(1);
          else if (gesture.dx >= 40) go(-1);
        },
      }),
    [go]
  );

  if (!current) {
    return null;
  }

  const handlePlay = () => {
    if (playingRef.current) return;
    playingRef.current = true;
    // 与搜索结果“点一下起播”同一条路径:有观看记录接着播,没有就第一集
    void resolveDirectPlayParams(current.source, current.id, current.title)
      .then((params) => {
        router.push({ pathname: "/play", params: { ...params } });
      })
      .finally(() => {
        playingRef.current = false;
      });
  };

  const handleDetail = () => {
    router.push({
      pathname: "/detail",
      params:
        current.source === "douban"
          ? { q: current.title }
          : { source: current.source, id: current.id, q: current.title },
    });
  };

  const meta = heroBannerMeta(current);
  const posterHeight = Math.max(96, height - 32);
  const imageSource = api.getImageSource(current.poster);

  return (
    <View style={[styles.container, { height }]} testID="hero-banner" {...pan.panHandlers}>
      <Animated.View style={[StyleSheet.absoluteFill, { opacity: fade }]}>
        <Image source={imageSource} style={StyleSheet.absoluteFill} resizeMode="cover" blurRadius={14} />
        <LinearGradient
          colors={["rgba(21,23,24,0.35)", "rgba(21,23,24,0.92)"]}
          start={{ x: 0, y: 0 }}
          end={{ x: 1, y: 1 }}
          style={StyleSheet.absoluteFill}
        />
        <View style={styles.row}>
          <Image
            source={imageSource}
            style={[styles.poster, { height: posterHeight, width: Math.round((posterHeight * 2) / 3) }]}
            resizeMode="cover"
          />
          <View style={styles.info}>
            <Text style={styles.title} numberOfLines={2} testID="hero-title">
              {current.title}
            </Text>
            {meta ? (
              <Text style={styles.meta} numberOfLines={1} testID="hero-meta">
                {meta}
              </Text>
            ) : null}
            {current.desc ? (
              <Text style={styles.desc} numberOfLines={3} testID="hero-desc">
                {current.desc}
              </Text>
            ) : null}
            <View style={styles.actions}>
              <Pressable
                testID="hero-play"
                onPress={handlePlay}
                accessibilityRole="button"
                accessibilityLabel={`播放 ${current.title}`}
                style={({ pressed }) => [styles.playButton, pressed && styles.pressed]}
              >
                <Play size={16} color="#fff" fill="#fff" />
                <Text style={styles.playText}>播放</Text>
              </Pressable>
              <Pressable
                testID="hero-detail"
                onPress={handleDetail}
                accessibilityRole="button"
                accessibilityLabel={`查看 ${current.title} 的详情`}
                style={({ pressed }) => [styles.detailButton, pressed && styles.pressed]}
              >
                <Text style={styles.detailText}>详情</Text>
              </Pressable>
            </View>
          </View>
        </View>
      </Animated.View>
      {count > 1 ? (
        <View style={styles.dots}>
          {items.map((item, dotIndex) => (
            <Pressable
              key={`${item.source}+${item.id}`}
              testID={`hero-dot-${dotIndex}`}
              hitSlop={8}
              accessibilityRole="button"
              accessibilityLabel={`第 ${dotIndex + 1} 张`}
              onPress={() => jump(dotIndex)}
              style={[styles.dot, dotIndex === safeIndex && styles.dotActive]}
            />
          ))}
        </View>
      ) : null}
    </View>
  );
};

const styles = StyleSheet.create({
  container: {
    borderRadius: 12,
    overflow: "hidden",
    backgroundColor: "#1c1c1e",
    marginBottom: 12,
  },
  row: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    padding: 16,
  },
  poster: {
    borderRadius: 8,
    backgroundColor: "#25292b",
  },
  info: {
    flex: 1,
    marginLeft: 14,
    justifyContent: "center",
  },
  title: {
    color: "#fff",
    fontSize: 20,
    fontWeight: "bold",
  },
  meta: {
    color: "#d0d0d0",
    fontSize: 12,
    marginTop: 6,
  },
  desc: {
    color: "#ccc",
    fontSize: 12,
    lineHeight: 17,
    marginTop: 6,
  },
  actions: {
    flexDirection: "row",
    alignItems: "center",
    marginTop: 12,
  },
  playButton: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: Colors.dark.primary,
    borderRadius: 20,
    paddingHorizontal: 18,
    minHeight: 40,
  },
  playText: {
    color: "#fff",
    fontSize: 15,
    fontWeight: "bold",
    marginLeft: 6,
  },
  detailButton: {
    marginLeft: 10,
    borderRadius: 20,
    paddingHorizontal: 16,
    minHeight: 40,
    justifyContent: "center",
    backgroundColor: "rgba(255,255,255,0.16)",
  },
  detailText: {
    color: "#fff",
    fontSize: 14,
  },
  pressed: {
    opacity: 0.75,
  },
  dots: {
    position: "absolute",
    right: 14,
    bottom: 10,
    flexDirection: "row",
  },
  dot: {
    width: 6,
    height: 6,
    borderRadius: 3,
    marginLeft: 6,
    backgroundColor: "rgba(255,255,255,0.4)",
  },
  dotActive: {
    width: 16,
    backgroundColor: "#fff",
  },
});

export default HomeHeroBanner;
