import React, { useCallback, useContext, useEffect, useRef, useState } from "react";
import { View, Text, StyleSheet, Image, ScrollView, ActivityIndicator, TouchableOpacity, BackHandler } from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { SafeAreaInsetsContext } from "react-native-safe-area-context";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ThemedView } from "@/components/ThemedView";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import VideoLoadingAnimation from "@/components/VideoLoadingAnimation";
import useDetailStore from "@/stores/detailStore";
import { mergeTmdbMetadata } from "@/services/tmdb";
import { FontAwesome } from "@expo/vector-icons";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { getCommonResponsiveStyles } from "@/utils/ResponsiveStyles";
import ResponsiveNavigation from "@/components/navigation/ResponsiveNavigation";
import ResponsiveHeader from "@/components/navigation/ResponsiveHeader";
import { api } from "@/services/api";
import {
  ChannelHealthResult,
  healthColor,
  labelOf,
  probeChannels,
} from "@/services/channelHealth";
import { getSearchResultSourceKey } from "@/services/videoSourceSubscription";
import { resolveDirectPlayParams } from "@/services/directPlay";
import { DetailRelatedRow } from "@/components/DetailRelatedRow";
import type { RelatedItem } from "@/services/relatedItems";
import { Colors } from "@/constants/Colors";

// 沉浸式详情页主按钮要播的那一集:有观看记录时接着上次那一集和进度,否则第一集
interface PrimaryPlayTarget {
  episodeIndex: number;
  position?: number;
  resumed: boolean;
}

const FIRST_EPISODE: PrimaryPlayTarget = { episodeIndex: 0, resumed: false };

// 这个页面当前显示的是哪一部:路由参数,或者从“相关推荐”点进去的那一部
interface DetailTarget {
  q: string;
  source?: string;
  id?: string;
}

export default function DetailScreen() {
  const params = useLocalSearchParams<{ q: string; source?: string; id?: string }>();
  const router = useRouter();
  // 从“相关推荐”一路点进来的轨迹(最后一项是正在显示的)。在同一个页面里换内容而不是再压一层详情页:
  // 详情 store 是全局的,压一层的话返回时上一层会显示成后一部的数据。返回先按轨迹退回上一部,退完才离开页面。
  const [relatedTrail, setRelatedTrail] = useState<DetailTarget[]>([]);
  const { q, source, id }: Partial<DetailTarget> =
    relatedTrail.length > 0 ? relatedTrail[relatedTrail.length - 1] : params;
  const routeQ = params.q;
  const routeSource = params.source;
  const routeId = params.id;

  // 响应式布局配置
  const responsiveConfig = useResponsiveLayout();
  const commonStyles = getCommonResponsiveStyles(responsiveConfig);
  const { deviceType, spacing } = responsiveConfig;

  const {
    detail,
    searchResults,
    loading,
    error,
    allSourcesLoaded,
    init,
    setDetail,
    abort,
    isFavorited,
    toggleFavorite,
    tmdbMeta,
  } = useDetailStore();
  // TMDB 只补空缺(海报 / 简介 / 年份)并提供评分;没填 Key 或查询失败时 tmdbMeta 为空,显示与原来完全一致
  const view = detail ? mergeTmdbMetadata(detail, tmdbMeta) : null;
  const [health, setHealth] = useState<Record<string, ChannelHealthResult>>({});
  const [scan, setScan] = useState({ running: false, done: 0, total: 0 });
  const scanAbortRef = useRef<AbortController | null>(null);
  // 用 context 而不是 useSafeAreaInsets():后者在没有 SafeAreaProvider 时会抛错,这里没有就按 0 处理
  const topInset = useContext(SafeAreaInsetsContext)?.top ?? 0;
  const [primaryTarget, setPrimaryTarget] = useState<PrimaryPlayTarget>(FIRST_EPISODE);
  const detailSource = detail?.source;
  const detailId = detail?.id;
  const detailTitle = detail?.title;

  // 主按钮续播:读本机观看记录(与搜索结果点一下起播同一个函数),读不到或读失败按第一集
  useEffect(() => {
    setPrimaryTarget(FIRST_EPISODE);
    if (detailSource === undefined || detailId === undefined) return;
    let cancelled = false;
    void resolveDirectPlayParams(String(detailSource), String(detailId), detailTitle ?? "").then((params) => {
      if (cancelled) return;
      setPrimaryTarget({
        episodeIndex: params.episodeIndex,
        position: params.position,
        resumed: params.episodeIndex > 0 || params.position !== undefined,
      });
    });
    return () => {
      cancelled = true;
    };
  }, [detailSource, detailId, detailTitle]);

  useEffect(() => {
    if (q) {
      init(q, source, id);
    }
    return () => {
      abort();
    };
  }, [abort, init, q, source, id]);

  useEffect(() => () => scanAbortRef.current?.abort(), []);

  // 路由参数换了(从别处重新进入这个页面)就丢掉轨迹,按新参数显示
  useEffect(() => {
    setRelatedTrail((trail) => (trail.length > 0 ? [] : trail));
  }, [routeQ, routeSource, routeId]);

  // 轨迹非空时,系统返回键(安卓)同样先退回上一部
  const hasRelatedTrail = relatedTrail.length > 0;
  useEffect(() => {
    if (!hasRelatedTrail) return;
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      setRelatedTrail((trail) => trail.slice(0, -1));
      return true;
    });
    return () => subscription.remove();
  }, [hasRelatedTrail]);

  const handleOpenRelated = useCallback((item: RelatedItem) => {
    // 与首页卡片进详情页的参数规则相同:豆瓣条目只按标题找,片源条目带 source + id
    const next: DetailTarget =
      item.source === "douban" ? { q: item.title } : { q: item.title, source: item.source, id: String(item.id) };
    setRelatedTrail((trail) => [...trail, next]);
  }, []);

  useEffect(() => {
    scanAbortRef.current?.abort();
    scanAbortRef.current = null;
    setHealth({});
    setScan({ running: false, done: 0, total: 0 });
  }, [q, source, id]);

  const startScan = useCallback(async () => {
    if (scanAbortRef.current) {
      scanAbortRef.current.abort();
      scanAbortRef.current = null;
      setScan((current) => ({ ...current, running: false }));
      return;
    }

    const playableSources = searchResults.filter((result) => result.episodes[0]);
    if (playableSources.length === 0) return;

    const controller = new AbortController();
    scanAbortRef.current = controller;
    setHealth({});
    setScan({ running: true, done: 0, total: playableSources.length });
    let done = 0;

    try {
      await probeChannels(
        playableSources.map((result) => ({
          id: getSearchResultSourceKey(result),
          url: result.episodes[0],
        })),
        {
          concurrency: 5,
          signal: controller.signal,
          onResult: (sourceKey, result) => {
            done += 1;
            setHealth((current) => ({ ...current, [sourceKey]: result }));
            setScan((current) => ({ ...current, done }));
          },
        }
      );
    } finally {
      if (scanAbortRef.current === controller) scanAbortRef.current = null;
      setScan((current) => ({ ...current, running: false }));
    }
  }, [searchResults]);

  const handlePlay = (episodeIndex: number, position?: number) => {
    if (!detail) return;
    abort(); // Cancel any ongoing fetches
    router.push({
      pathname: "/play",
      params: {
        // Pass necessary identifiers, the rest will be in the store
        // 标题必须以 title 传入(播放页不读 q):缺少时播放页退回读 store 里的 detail.title,
        // 播放中换到标题仅空格/大小写不同的源会触发整页重载,并被路由里的旧 source 换回原来的源
        title: detail.title,
        source: detail.source,
        id: detail.id.toString(),
        episodeIndex: episodeIndex.toString(),
        // 只有主按钮续播时才带进度;点集数按钮与原来一样不带
        ...(position !== undefined ? { position: position.toString() } : {}),
      },
    });
  };

  // 主按钮:记录里的那一集在当前播放源里不存在(换了集数更少的源)时退回第一集、不带进度
  const episodeCount = detail?.episodes.length ?? 0;
  const primaryInRange = primaryTarget.resumed && primaryTarget.episodeIndex < episodeCount;
  const primaryEpisodeIndex = primaryInRange ? primaryTarget.episodeIndex : 0;
  const primaryLabel =
    episodeCount === 0
      ? "暂无可播放剧集"
      : primaryInRange
        ? `继续播放 第${primaryEpisodeIndex + 1}集`
        : episodeCount > 1
          ? "播放 第1集"
          : "播放";
  const handlePrimaryPlay = () => {
    if (episodeCount === 0) return;
    handlePlay(primaryEpisodeIndex, primaryInRange ? primaryTarget.position : undefined);
  };
  const handleBack = () => {
    if (relatedTrail.length > 0) {
      setRelatedTrail((trail) => trail.slice(0, -1));
      return;
    }
    if (router.canGoBack()) {
      router.back();
    }
  };

  if (loading) {
    return <VideoLoadingAnimation showProgressBar={false} />;
  }

  if (error) {
    const content = (
      <ThemedView style={[commonStyles.safeContainer, commonStyles.center]}>
        <ThemedText type="subtitle" style={commonStyles.textMedium}>
          {error}
        </ThemedText>
      </ThemedView>
    );

    if (deviceType === 'tv') {
      return content;
    }

    return (
      <ResponsiveNavigation>
        <ResponsiveHeader title="详情" showBackButton />
        {content}
      </ResponsiveNavigation>
    );
  }

  if (!detail) {
    const content = (
      <ThemedView style={[commonStyles.safeContainer, commonStyles.center]}>
        <ThemedText type="subtitle">未找到详情信息</ThemedText>
      </ThemedView>
    );

    if (deviceType === 'tv') {
      return content;
    }

    return (
      <ResponsiveNavigation>
        <ResponsiveHeader title="详情" showBackButton />
        {content}
      </ResponsiveNavigation>
    );
  }

  // 动态样式
  const dynamicStyles = createResponsiveStyles(deviceType, spacing);
  const scanValues = Object.values(health);
  const scanSummary =
    !scan.running && scanValues.length > 0
      ? `流畅 ${scanValues.filter((item) => item.category === "good").length} · 一般 ${
          scanValues.filter((item) => item.category === "fair").length
        } · 不畅 ${scanValues.filter((item) => item.category === "bad").length}`
      : null;

  const renderDetailContent = () => {
    if (deviceType !== 'tv') {
      // 手机、平板:沉浸式布局。大海报通顶,标题与简介叠在海报上,主播放按钮在海报底部,播放源与集数往下排
      return (
        <ScrollView style={dynamicStyles.scrollContainer} testID="detail-immersive">
          <View style={dynamicStyles.hero}>
            <Image
              source={api.getImageSource(view?.poster ?? detail.poster)}
              style={dynamicStyles.heroPoster}
              resizeMode="cover"
            />
            {/* 上沿压暗保证返回按钮与状态栏可读,下半部渐变到页面背景色 */}
            <LinearGradient
              colors={["rgba(0,0,0,0.45)", "rgba(0,0,0,0)", "rgba(10,11,20,0.82)", Colors.dark.background]}
              locations={[0, 0.3, 0.72, 1]}
              style={StyleSheet.absoluteFill}
              pointerEvents="none"
            />
            <View style={dynamicStyles.heroContent}>
              <ThemedText style={dynamicStyles.heroTitle} numberOfLines={2}>
                {detail.title}
              </ThemedText>
              <View style={dynamicStyles.metaContainer}>
                <ThemedText style={dynamicStyles.heroMetaText}>{view?.year ?? detail.year}</ThemedText>
                <ThemedText style={dynamicStyles.heroMetaText}>{detail.type_name}</ThemedText>
                {view?.tmdbRating ? (
                  <ThemedText style={dynamicStyles.heroMetaText}>{`TMDB ${view.tmdbRating.toFixed(1)}`}</ThemedText>
                ) : null}
              </View>
              {view?.desc ?? detail.desc ? (
                <ThemedText style={dynamicStyles.heroDescription} numberOfLines={4}>
                  {view?.desc ?? detail.desc}
                </ThemedText>
              ) : null}
              <View style={dynamicStyles.heroActions}>
                <StyledButton
                  testID="detail-primary-play"
                  variant="primary"
                  onPress={handlePrimaryPlay}
                  disabled={episodeCount === 0}
                  style={dynamicStyles.heroPlayButton}
                >
                  <FontAwesome name="play" size={16} color="#06140E" />
                  <Text style={dynamicStyles.heroPlayText}>{primaryLabel}</Text>
                </StyledButton>
                <StyledButton
                  testID="detail-favorite"
                  onPress={toggleFavorite}
                  variant="ghost"
                  style={dynamicStyles.heroFavoriteButton}
                >
                  <FontAwesome
                    name={isFavorited ? "heart" : "heart-o"}
                    size={22}
                    color={isFavorited ? "#feff5f" : "#fff"}
                  />
                </StyledButton>
              </View>
            </View>
          </View>

          {/* 播放源 */}
          <View style={dynamicStyles.sourcesContainer}>
            <View style={dynamicStyles.sourcesTitleContainer}>
              <ThemedText style={dynamicStyles.sourcesTitle}>播放源 ({searchResults.length})</ThemedText>
              {!allSourcesLoaded && <ActivityIndicator style={{ marginLeft: 10 }} />}
              <StyledButton
                text={scan.running ? `测速中 ${scan.done}/${scan.total}` : "测速"}
                onPress={startScan}
                style={dynamicStyles.scanButton}
                textStyle={dynamicStyles.scanButtonText}
              />
            </View>
            {scanSummary && <ThemedText style={dynamicStyles.scanSummary}>{scanSummary}</ThemedText>}
            <View style={dynamicStyles.sourceList}>
              {searchResults.map((item, index) => {
                const isSelected = getSearchResultSourceKey(detail) === getSearchResultSourceKey(item);
                const itemHealth = health[getSearchResultSourceKey(item)];
                return (
                  <StyledButton
                    key={index}
                    onPress={() => setDetail(item)}
                    isSelected={isSelected}
                    style={dynamicStyles.sourceButton}
                  >
                    <ThemedText style={dynamicStyles.sourceButtonText}>{item.source_name}</ThemedText>
                    {itemHealth && (
                      <View style={dynamicStyles.sourceStatus}>
                        <View style={[dynamicStyles.sourceDot, { backgroundColor: healthColor(itemHealth.category) }]} />
                        <Text style={[dynamicStyles.sourceHealthText, { color: healthColor(itemHealth.category) }]}>
                          {labelOf(itemHealth.category)}
                        </Text>
                      </View>
                    )}
                    {item.episodes.length > 1 && (
                      <View style={[dynamicStyles.badge, isSelected && dynamicStyles.selectedBadge]}>
                        <Text style={dynamicStyles.badgeText}>
                          {item.episodes.length > 99 ? "99+" : `${item.episodes.length}`} 集
                        </Text>
                      </View>
                    )}
                    {item.resolution && (
                      <View style={[dynamicStyles.badge, { backgroundColor: "#666" }, isSelected && dynamicStyles.selectedBadge]}>
                        <Text style={dynamicStyles.badgeText}>{item.resolution}</Text>
                      </View>
                    )}
                  </StyledButton>
                );
              })}
            </View>
          </View>

          {/* 剧集列表 */}
          <View style={dynamicStyles.episodesContainer}>
            <ThemedText style={dynamicStyles.episodesTitle}>播放列表</ThemedText>
            <View style={dynamicStyles.episodeList}>
              {detail.episodes.map((episode, index) => (
                <StyledButton
                  key={index}
                  style={dynamicStyles.episodeButton}
                  onPress={() => handlePlay(index)}
                  text={`第 ${index + 1} 集`}
                  textStyle={dynamicStyles.episodeButtonText}
                />
              ))}
            </View>
          </View>

          {/* 相关推荐:集数下方,只用本机候选池,没有可推荐的不显示 */}
          <DetailRelatedRow
            current={detail}
            onSelect={handleOpenRelated}
            spacing={spacing}
            isTablet={deviceType === 'tablet'}
          />
        </ScrollView>
      );
    } else {
      // 平板和TV端水平布局
      return (
        <ScrollView style={dynamicStyles.scrollContainer}>
          <View style={dynamicStyles.topContainer}>
            <Image source={api.getImageSource(view?.poster ?? detail.poster)} style={dynamicStyles.poster} />
            <View style={dynamicStyles.infoContainer}>
              <View style={dynamicStyles.titleContainer}>
                <ThemedText style={dynamicStyles.title} numberOfLines={1} ellipsizeMode="tail">
                  {detail.title}
                </ThemedText>
                <StyledButton onPress={toggleFavorite} variant="ghost" style={dynamicStyles.favoriteButton}>
                  <FontAwesome
                    name={isFavorited ? "heart" : "heart-o"}
                    size={24}
                    color={isFavorited ? "#feff5f" : "#ccc"}
                  />
                </StyledButton>
              </View>
              <View style={dynamicStyles.metaContainer}>
                <ThemedText style={dynamicStyles.metaText}>{view?.year ?? detail.year}</ThemedText>
                <ThemedText style={dynamicStyles.metaText}>{detail.type_name}</ThemedText>
                {view?.tmdbRating ? (
                  <ThemedText style={dynamicStyles.metaText}>{`TMDB ${view.tmdbRating.toFixed(1)}`}</ThemedText>
                ) : null}
              </View>

              <ScrollView style={dynamicStyles.descriptionScrollView}>
                <ThemedText style={dynamicStyles.description}>{view?.desc ?? detail.desc}</ThemedText>
              </ScrollView>
            </View>
          </View>

          <View style={dynamicStyles.bottomContainer}>
            <View style={dynamicStyles.sourcesContainer}>
              <View style={dynamicStyles.sourcesTitleContainer}>
                <ThemedText style={dynamicStyles.sourcesTitle}>选择播放源 共 {searchResults.length} 个</ThemedText>
                {!allSourcesLoaded && <ActivityIndicator style={{ marginLeft: 10 }} />}
                <StyledButton
                  text={scan.running ? `测速中 ${scan.done}/${scan.total}` : "测速"}
                  onPress={startScan}
                  style={dynamicStyles.scanButton}
                  textStyle={dynamicStyles.scanButtonText}
                />
              </View>
              {scanSummary && <ThemedText style={dynamicStyles.scanSummary}>{scanSummary}</ThemedText>}
              <View style={dynamicStyles.sourceList}>
                {searchResults.map((item, index) => {
                  const isSelected = getSearchResultSourceKey(detail) === getSearchResultSourceKey(item);
                  const itemHealth = health[getSearchResultSourceKey(item)];
                  return (
                    <StyledButton
                      key={index}
                      onPress={() => setDetail(item)}
                      hasTVPreferredFocus={index === 0}
                      isSelected={isSelected}
                      style={dynamicStyles.sourceButton}
                    >
                      <ThemedText style={dynamicStyles.sourceButtonText}>{item.source_name}</ThemedText>
                      {itemHealth && (
                        <View style={dynamicStyles.sourceStatus}>
                          <View style={[dynamicStyles.sourceDot, { backgroundColor: healthColor(itemHealth.category) }]} />
                          <Text style={[dynamicStyles.sourceHealthText, { color: healthColor(itemHealth.category) }]}>
                            {labelOf(itemHealth.category)}
                          </Text>
                        </View>
                      )}
                      {item.episodes.length > 1 && (
                        <View style={[dynamicStyles.badge, isSelected && dynamicStyles.selectedBadge]}>
                          <Text style={dynamicStyles.badgeText}>
                            {item.episodes.length > 99 ? "99+" : `${item.episodes.length}`} 集
                          </Text>
                        </View>
                      )}
                      {item.resolution && (
                        <View style={[dynamicStyles.badge, { backgroundColor: "#666" }, isSelected && dynamicStyles.selectedBadge]}>
                          <Text style={dynamicStyles.badgeText}>{item.resolution}</Text>
                        </View>
                      )}
                    </StyledButton>
                  );
                })}
              </View>
            </View>
            <View style={dynamicStyles.episodesContainer}>
              <ThemedText style={dynamicStyles.episodesTitle}>播放列表</ThemedText>
              <ScrollView contentContainerStyle={dynamicStyles.episodeList}>
                {detail.episodes.map((episode, index) => (
                  <StyledButton
                    key={index}
                    style={dynamicStyles.episodeButton}
                    onPress={() => handlePlay(index)}
                    text={`第 ${index + 1} 集`}
                    textStyle={dynamicStyles.episodeButtonText}
                  />
                ))}
              </ScrollView>
            </View>
          </View>
        </ScrollView>
      );
    }
  };

  const content = (
    <ThemedView style={[commonStyles.container, { paddingTop: deviceType === 'tv' ? 40 : 0 }]}>
      {renderDetailContent()}
    </ThemedView>
  );

  // 根据设备类型决定是否包装在响应式导航中
  if (deviceType === 'tv') {
    return content;
  }

  // 沉浸式:不再占一行标题栏,返回按钮浮在海报左上角(避开状态栏),滚动时保持可见
  return (
    <ResponsiveNavigation>
      {content}
      <TouchableOpacity
        testID="detail-back"
        accessibilityRole="button"
        accessibilityLabel="返回"
        onPress={handleBack}
        activeOpacity={0.7}
        style={[dynamicStyles.floatingBack, { top: topInset + 8 }]}
      >
        <FontAwesome name="chevron-left" size={16} color="#fff" />
      </TouchableOpacity>
    </ResponsiveNavigation>
  );
}

const createResponsiveStyles = (deviceType: string, spacing: number) => {
  const isTV = deviceType === 'tv';
  const isTablet = deviceType === 'tablet';
  const isMobile = deviceType === 'mobile';

  return StyleSheet.create({
    scrollContainer: {
      flex: 1,
    },
    
    // 沉浸式头部(手机、平板)
    hero: {
      width: '100%',
      height: isTablet ? 480 : 440,
      justifyContent: 'flex-end',
      backgroundColor: '#151828',
    },
    heroPoster: {
      ...StyleSheet.absoluteFillObject,
      width: '100%',
      height: '100%',
    },
    heroContent: {
      paddingHorizontal: spacing,
      paddingBottom: spacing,
    },
    heroTitle: {
      fontSize: isTablet ? 30 : 26,
      lineHeight: isTablet ? 38 : 32,
      fontWeight: '800',
      letterSpacing: 0.3,
      color: '#fff',
      marginBottom: 6,
    },
    heroMetaText: {
      color: '#d6d6d6',
      marginRight: spacing / 2,
      fontSize: isTablet ? 14 : 13,
    },
    heroDescription: {
      color: '#e2e2e2',
      fontSize: isTablet ? 14 : 13,
      lineHeight: isTablet ? 22 : 19,
      marginBottom: spacing * 0.75,
    },
    heroActions: {
      flexDirection: 'row',
      alignItems: 'center',
    },
    heroPlayButton: {
      flex: 1,
      minHeight: 48,
      borderRadius: 24,
      backgroundColor: '#6FDCA8',
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'center',
    },
    heroPlayText: {
      color: '#06140E',
      fontSize: 16,
      fontWeight: 'bold',
      marginLeft: 8,
    },
    heroFavoriteButton: {
      width: 48,
      height: 48,
      borderRadius: 24,
      marginLeft: spacing / 2,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: 'rgba(255,255,255,0.14)',
    },
    floatingBack: {
      position: 'absolute',
      left: 12,
      width: 36,
      height: 36,
      borderRadius: 18,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: 'rgba(0,0,0,0.45)',
    },

    // 移动端专用样式
    mobileTopContainer: {
      paddingHorizontal: spacing,
      paddingTop: spacing,
      paddingBottom: spacing / 2,
    },
    mobilePoster: {
      width: '100%',
      height: 280,
      borderRadius: 20,
      alignSelf: 'center',
      marginBottom: spacing,
    },
    mobileInfoContainer: {
      flex: 1,
    },
    descriptionContainer: {
      paddingHorizontal: spacing,
      paddingBottom: spacing,
    },

    // 平板和TV端样式
    topContainer: {
      flexDirection: "row",
      padding: spacing,
    },
    poster: {
      width: isTV ? 200 : 160,
      height: isTV ? 300 : 240,
      borderRadius: 14,
    },
    infoContainer: {
      flex: 1,
      marginLeft: spacing,
      justifyContent: "flex-start",
    },
    descriptionScrollView: {
      height: 150,
    },

    // 通用样式
    titleContainer: {
      flexDirection: "row",
      alignItems: "center",
      marginBottom: spacing / 2,
    },
    title: {
      paddingTop: 16,
      fontSize: isMobile ? 20 : isTablet ? 24 : 28,
      fontWeight: "bold",
      flexShrink: 1,
      color: 'white',
    },
    favoriteButton: {
      padding: 10,
      marginLeft: 10,
      backgroundColor: "transparent",
    },
    metaContainer: {
      flexDirection: "row",
      marginBottom: spacing / 2,
    },
    metaText: {
      color: "#aaa",
      marginRight: spacing / 2,
      fontSize: isMobile ? 12 : 14,
    },
    description: {
      fontSize: isMobile ? 13 : 14,
      color: "#ccc",
      lineHeight: isMobile ? 18 : 22,
    },

    // 播放源和剧集样式
    bottomContainer: {
      paddingHorizontal: spacing,
    },
    sourcesContainer: {
      marginTop: spacing,
    },
    sourcesTitleContainer: {
      flexDirection: "row",
      alignItems: "center",
      marginBottom: spacing / 2,
    },
    sourcesTitle: {
      fontSize: isMobile ? 16 : isTablet ? 18 : 20,
      fontWeight: "bold",
      color: 'white',
    },
    scanButton: {
      marginLeft: spacing / 2,
      minHeight: isMobile ? 32 : 38,
    },
    scanButtonText: {
      fontSize: isMobile ? 12 : 14,
    },
    scanSummary: {
      color: "#aaa",
      fontSize: isMobile ? 12 : 13,
      marginBottom: spacing / 2,
    },
    sourceList: {
      flexDirection: "row",
      flexWrap: "wrap",
    },
    sourceButton: {
      margin: isMobile ? 4 : 8,
      minHeight: isMobile ? 36 : 44,
    },
    sourceButtonText: {
      color: "white",
      fontSize: isMobile ? 14 : 16,
    },
    sourceStatus: {
      flexDirection: "row",
      alignItems: "center",
      marginLeft: 8,
    },
    sourceDot: {
      width: 8,
      height: 8,
      borderRadius: 4,
      marginRight: 4,
    },
    sourceHealthText: {
      fontSize: isMobile ? 11 : 12,
      fontWeight: "bold",
    },
    badge: {
      backgroundColor: "#666",
      borderRadius: 10,
      paddingHorizontal: 6,
      paddingVertical: 2,
      marginLeft: 8,
    },
    badgeText: {
      color: "#fff",
      fontSize: isMobile ? 10 : 12,
      fontWeight: "bold",
      paddingBottom: 2.5,
    },
    selectedBadge: {
      backgroundColor: "#4c4c4c",
    },

    episodesContainer: {
      marginTop: spacing,
      paddingBottom: spacing * 2,
    },
    episodesTitle: {
      fontSize: isMobile ? 16 : isTablet ? 18 : 20,
      fontWeight: "bold",
      marginBottom: spacing / 2,
      color: 'white',
    },
    episodeList: {
      flexDirection: "row",
      flexWrap: "wrap",
    },
    episodeButton: {
      margin: isMobile ? 3 : 5,
      minHeight: isMobile ? 32 : 36,
    },
    episodeButtonText: {
      color: "white",
      fontSize: isMobile ? 12 : 14,
    },
  });
};
