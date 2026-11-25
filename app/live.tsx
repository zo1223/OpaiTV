import React, { useState, useEffect, useCallback, useMemo, useRef } from "react";
import { View, FlatList, StyleSheet, ActivityIndicator, Modal, Text, Pressable } from "react-native";
import type { HWEvent } from "react-native";
import type { AVPlaybackStatus } from "expo-av";
import LivePlayer from "@/components/LivePlayer";
import { fetchAndParseM3u, getPlayableUrl, Channel } from "@/services/m3u";
import { probeChannels, labelOf, healthColor, ChannelHealthResult } from "@/services/channelHealth";
import { ThemedView } from "@/components/ThemedView";
import { StyledButton } from "@/components/StyledButton";
import { useSettingsStore } from "@/stores/settingsStore";
import { normalizePlaybackProxySettings, PlaybackProxySettings, resolvePlaybackUrl } from "@/services/playbackProxy";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { getCommonResponsiveStyles } from "@/utils/ResponsiveStyles";
import ResponsiveNavigation from "@/components/navigation/ResponsiveNavigation";
import ResponsiveHeader from "@/components/navigation/ResponsiveHeader";
import { DeviceUtils } from "@/utils/DeviceUtils";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";

/** 读取当前的播放代理设置(读不到时按系统代理,即不改写地址) */
const readPlaybackProxySettings = (): PlaybackProxySettings => {
  try {
    const { playbackProxyMode, playbackProxyUrl } = useSettingsStore.getState();
    return normalizePlaybackProxySettings({ mode: playbackProxyMode, proxyUrl: playbackProxyUrl });
  } catch {
    return normalizePlaybackProxySettings(null);
  }
};

export default function LiveScreen() {
  const { m3uUrl } = useSettingsStore();
  
  // 响应式布局配置
  const responsiveConfig = useResponsiveLayout();
  const commonStyles = getCommonResponsiveStyles(responsiveConfig);
  const { deviceType, spacing } = responsiveConfig;

  const [channels, setChannels] = useState<Channel[]>([]);
  const [groupedChannels, setGroupedChannels] = useState<Record<string, Channel[]>>({});
  const [channelGroups, setChannelGroups] = useState<string[]>([]);
  const [selectedGroup, setSelectedGroup] = useState<string>("");

  const [currentChannelIndex, setCurrentChannelIndex] = useState(0);
  // 每次选台都递增:重选同一频道时 streamUrl 不变,靠它让播放器从"加载失败"中重试
  const [reloadToken, setReloadToken] = useState(0);
  const [isLoading, setIsLoading] = useState(false);
  const [isChannelListVisible, setIsChannelListVisible] = useState(false);
  const [channelTitle, setChannelTitle] = useState<string | null>(null);
  const titleTimer = useRef<NodeJS.Timeout | null>(null);

  const [health, setHealth] = useState<Record<string, ChannelHealthResult>>({});
  const [scan, setScan] = useState<{ running: boolean; done: number; total: number }>({
    running: false,
    done: 0,
    total: 0,
  });
  const [showStats, setShowStats] = useState(true);
  const [realtime, setRealtime] = useState<{
    playing: boolean;
    buffering: boolean;
    aheadSec: number;
    stalls: number;
  } | null>(null);
  const scanAbortRef = useRef<AbortController | null>(null);
  const stallRef = useRef(0);
  const wasBufferingRef = useRef(false);

  // 切换频道时重置实时统计。
  useEffect(() => {
    stallRef.current = 0;
    wasBufferingRef.current = false;
    setRealtime(null);
  }, [currentChannelIndex]);

  // 离开页面时取消进行中的批量测速。
  useEffect(() => () => scanAbortRef.current?.abort(), []);

  // 卸载时清理标题隐藏定时器,避免向已卸载组件 setState。
  useEffect(
    () => () => {
      if (titleTimer.current) clearTimeout(titleTimer.current);
    },
    []
  );

  const startScan = useCallback(async () => {
    if (scanAbortRef.current) {
      // 正在测速时再次点击 → 取消本轮。
      scanAbortRef.current.abort();
      scanAbortRef.current = null;
      setScan((s) => ({ ...s, running: false }));
      return;
    }
    if (channels.length === 0) return;

    const controller = new AbortController();
    scanAbortRef.current = controller;
    setHealth({});
    setScan({ running: true, done: 0, total: channels.length });

    let done = 0;
    try {
      await probeChannels(
        channels.map((c) => ({ id: c.id, url: c.url })),
        {
          concurrency: 5,
          signal: controller.signal,
          onResult: (id, result) => {
            done += 1;
            setHealth((prev) => ({ ...prev, [id]: result }));
            setScan((s) => ({ ...s, done }));
          },
        }
      );
    } finally {
      if (scanAbortRef.current === controller) scanAbortRef.current = null;
      setScan((s) => ({ ...s, running: false }));
    }
  }, [channels]);

  const handlePlaybackStatus = useCallback((status: AVPlaybackStatus) => {
    if (!status.isLoaded) return;
    const buffering = !!status.isBuffering;
    if (buffering && !wasBufferingRef.current) stallRef.current += 1;
    wasBufferingRef.current = buffering;
    const aheadSec = Math.max(
      0,
      ((status.playableDurationMillis ?? 0) - (status.positionMillis ?? 0)) / 1000
    );
    setRealtime({ playing: !!status.isPlaying, buffering, aheadSec, stalls: stallRef.current });
  }, []);

  // 频道列表可能在重载后变短,索引先钳制到有效范围再取 URL
  const selectedChannel =
    channels.length > 0 ? channels[Math.min(currentChannelIndex, channels.length - 1)] : null;
  const selectedChannelOriginalUrl = selectedChannel ? getPlayableUrl(selectedChannel.url) : null;
  // 播放代理模式在选台(开始一次播放)时读取:设置页切换后,下次选台 / 进入直播页才生效
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const liveProxy = useMemo(() => readPlaybackProxySettings(), [selectedChannelOriginalUrl, reloadToken]);
  const selectedChannelUrl = selectedChannelOriginalUrl
    ? resolvePlaybackUrl(selectedChannelOriginalUrl, liveProxy)
    : null;

  useEffect(() => {
    // 设置页每次按键都会更新 m3uUrl;旧地址的请求晚返回时不能覆盖新地址的频道列表
    let cancelled = false;
    const loadChannels = async () => {
      if (!m3uUrl) {
        setIsLoading(false);
        return;
      }
      setIsLoading(true);
      const parsedChannels = await fetchAndParseM3u(m3uUrl);
      if (cancelled) return;
      setChannels(parsedChannels);
      setCurrentChannelIndex(0);

      const groups: Record<string, Channel[]> = parsedChannels.reduce((acc, channel) => {
        const groupName = channel.group || "Other";
        if (!acc[groupName]) {
          acc[groupName] = [];
        }
        acc[groupName].push(channel);
        return acc;
      }, {} as Record<string, Channel[]>);

      const groupNames = Object.keys(groups);
      setGroupedChannels(groups);
      setChannelGroups(groupNames);
      setSelectedGroup(groupNames[0] || "");

      if (parsedChannels.length > 0) {
        showChannelTitle(parsedChannels[0].name);
      }
      setIsLoading(false);
    };
    loadChannels();
    return () => {
      cancelled = true;
    };
  }, [m3uUrl]);

  const showChannelTitle = (title: string) => {
    setChannelTitle(title);
    if (titleTimer.current) clearTimeout(titleTimer.current);
    titleTimer.current = setTimeout(() => setChannelTitle(null), 3000);
  };

  const handleSelectChannel = (channel: Channel) => {
    const globalIndex = channels.findIndex((c) => c.id === channel.id);
    if (globalIndex !== -1) {
      setCurrentChannelIndex(globalIndex);
      setReloadToken((t) => t + 1);
      showChannelTitle(channel.name);
      setIsChannelListVisible(false);
    }
  };

  const changeChannel = useCallback(
    (direction: "next" | "prev") => {
      if (channels.length === 0) return;
      let newIndex =
        direction === "next"
          ? (currentChannelIndex + 1) % channels.length
          : (currentChannelIndex - 1 + channels.length) % channels.length;
      setCurrentChannelIndex(newIndex);
      setReloadToken((t) => t + 1);
      showChannelTitle(channels[newIndex].name);
    },
    [channels, currentChannelIndex]
  );

  const handleTVEvent = useCallback(
    (event: HWEvent) => {
      if (deviceType !== 'tv') return;
      if (isChannelListVisible) return;
      if (event.eventType === "down") setIsChannelListVisible(true);
      else if (event.eventType === "left") changeChannel("prev");
      else if (event.eventType === "right") changeChannel("next");
      else if (event.eventType === "up") setShowStats((v) => !v);
    },
    [changeChannel, isChannelListVisible, deviceType]
  );

  useSafeTVEventHandler(deviceType === 'tv' ? handleTVEvent : () => {});

  // 动态样式
  const dynamicStyles = createResponsiveStyles(deviceType, spacing);

  const renderLiveContent = () => {
    const rtVerdict = realtime
      ? realtime.buffering
        ? { text: "缓冲中", color: "#e5484d" }
        : realtime.stalls > 0 && realtime.aheadSec < 1
          ? { text: "卡顿", color: "#e6a700" }
          : { text: "流畅", color: "#00bb5e" }
      : null;

    const scanValues = Object.values(health);
    const scanSummary =
      !scan.running && scanValues.length > 0
        ? `流畅 ${scanValues.filter((v) => v.category === "good").length} · 一般 ${
            scanValues.filter((v) => v.category === "fair").length
          } · 不畅 ${scanValues.filter((v) => v.category === "bad").length}`
        : null;

    return (
      <>
        <LivePlayer
          streamUrl={selectedChannelUrl}
          channelTitle={channelTitle}
          reloadToken={reloadToken}
          onPlaybackStatusUpdate={handlePlaybackStatus}
        />

        {deviceType !== "tv" && (
          <Pressable style={dynamicStyles.statsToggle} onPress={() => setShowStats((v) => !v)}>
            <Text style={dynamicStyles.statsToggleText}>{showStats ? "隐藏流畅度" : "流畅度"}</Text>
          </Pressable>
        )}

        {showStats && realtime && rtVerdict && (
          <View style={dynamicStyles.statsOverlay} pointerEvents="none">
            <Text style={dynamicStyles.statsTitle}>实时流畅度</Text>
            <Text style={[dynamicStyles.statsVerdict, { color: rtVerdict.color }]}>{rtVerdict.text}</Text>
            <Text style={dynamicStyles.statsLine}>缓冲余量 {realtime.aheadSec.toFixed(1)}s</Text>
            <Text style={dynamicStyles.statsLine}>卡顿 {realtime.stalls} 次</Text>
          </View>
        )}

        <Modal
          animationType="slide"
          transparent={true}
          visible={isChannelListVisible}
          onRequestClose={() => setIsChannelListVisible(false)}
        >
          <View style={dynamicStyles.modalContainer}>
            <View style={dynamicStyles.modalContent}>
              <View style={dynamicStyles.modalHeader}>
                <Text style={dynamicStyles.modalTitle}>选择频道</Text>
                <StyledButton
                  text={scan.running ? `测速中 ${scan.done}/${scan.total}` : "测速"}
                  onPress={startScan}
                  style={dynamicStyles.scanButton}
                  textStyle={dynamicStyles.scanButtonText}
                />
              </View>
              {scanSummary && <Text style={dynamicStyles.scanSummary}>{scanSummary}</Text>}
              <View style={dynamicStyles.listContainer}>
                <View style={dynamicStyles.groupColumn}>
                  <FlatList
                    data={channelGroups}
                    keyExtractor={(item, index) => `group-${item}-${index}`}
                    renderItem={({ item }) => (
                      <StyledButton
                        text={item}
                        onPress={() => setSelectedGroup(item)}
                        isSelected={selectedGroup === item}
                        style={dynamicStyles.groupButton}
                        textStyle={dynamicStyles.groupButtonText}
                      />
                    )}
                  />
                </View>
                <View style={dynamicStyles.channelColumn}>
                  {isLoading ? (
                    <ActivityIndicator size="large" />
                  ) : (
                    <FlatList
                      data={groupedChannels[selectedGroup] || []}
                      keyExtractor={(item, index) => `${item.id}-${item.group}-${index}`}
                      renderItem={({ item }) => {
                        const itemHealth = health[item.id];
                        const isSelected = channels[currentChannelIndex]?.id === item.id;
                        return (
                          <StyledButton
                            onPress={() => handleSelectChannel(item)}
                            isSelected={isSelected}
                            hasTVPreferredFocus={isSelected}
                            style={dynamicStyles.channelItem}
                          >
                            <View style={dynamicStyles.channelRow}>
                              <View
                                style={[
                                  dynamicStyles.channelDot,
                                  { backgroundColor: healthColor(itemHealth?.category) },
                                ]}
                              />
                              <Text style={dynamicStyles.channelItemText} numberOfLines={1}>
                                {item.name || "Unknown Channel"}
                              </Text>
                              {itemHealth && (
                                <Text
                                  style={[
                                    dynamicStyles.channelHealthLabel,
                                    { color: healthColor(itemHealth.category) },
                                  ]}
                                >
                                  {labelOf(itemHealth.category)}
                                </Text>
                              )}
                            </View>
                          </StyledButton>
                        );
                      }}
                    />
                  )}
                </View>
              </View>
            </View>
          </View>
        </Modal>
      </>
    );
  };

  const content = (
    <ThemedView style={[commonStyles.container, dynamicStyles.container]}>
      {renderLiveContent()}
    </ThemedView>
  );

  // 根据设备类型决定是否包装在响应式导航中
  if (deviceType === 'tv') {
    return content;
  }

  return (
    <ResponsiveNavigation>
      <ResponsiveHeader title="直播" showBackButton />
      {content}
    </ResponsiveNavigation>
  );
}

const createResponsiveStyles = (deviceType: string, spacing: number) => {
  const isMobile = deviceType === 'mobile';
  const isTablet = deviceType === 'tablet';
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();

  return StyleSheet.create({
    container: {
      flex: 1,
    },
    modalContainer: {
      flex: 1,
      flexDirection: "row",
      justifyContent: isMobile ? "center" : "flex-end",
      backgroundColor: "transparent",
    },
    modalContent: {
      width: isMobile ? '90%' : isTablet ? 400 : 450,
      height: "100%",
      backgroundColor: "rgba(0, 0, 0, 0.85)",
      padding: spacing,
    },
    modalTitle: {
      color: "white",
      marginBottom: spacing / 2,
      textAlign: "center",
      fontSize: isMobile ? 18 : 16,
      fontWeight: "bold",
    },
    listContainer: {
      flex: 1,
      flexDirection: isMobile ? "column" : "row",
    },
    groupColumn: {
      flex: isMobile ? 0 : 1,
      marginRight: isMobile ? 0 : spacing / 2,
      marginBottom: isMobile ? spacing : 0,
      maxHeight: isMobile ? 120 : undefined,
    },
    channelColumn: {
      flex: isMobile ? 1 : 2,
    },
    groupButton: {
      paddingVertical: isMobile ? minTouchTarget / 4 : 8,
      paddingHorizontal: spacing / 2,
      marginVertical: isMobile ? 2 : 4,
      minHeight: isMobile ? minTouchTarget * 0.7 : undefined,
    },
    groupButtonText: {
      fontSize: isMobile ? 14 : 13,
    },
    channelItem: {
      paddingVertical: isMobile ? minTouchTarget / 5 : 6,
      paddingHorizontal: spacing,
      marginVertical: isMobile ? 2 : 3,
      minHeight: isMobile ? minTouchTarget * 0.8 : undefined,
    },
    channelItemText: {
      fontSize: isMobile ? 14 : 12,
      color: "#fff",
      flex: 1,
    },
    channelRow: {
      flexDirection: "row",
      alignItems: "center",
      width: "100%",
    },
    channelDot: {
      width: 9,
      height: 9,
      borderRadius: 5,
      marginRight: 8,
    },
    channelHealthLabel: {
      fontSize: 11,
      marginLeft: 8,
    },
    modalHeader: {
      flexDirection: "row",
      alignItems: "center",
      justifyContent: "space-between",
    },
    scanButton: {
      paddingVertical: 6,
      paddingHorizontal: 14,
    },
    scanButtonText: {
      fontSize: 13,
    },
    scanSummary: {
      color: "#9BA1A6",
      fontSize: 12,
      marginTop: 4,
      marginBottom: spacing / 2,
      textAlign: "center",
    },
    statsToggle: {
      position: "absolute",
      top: 16,
      right: 16,
      backgroundColor: "rgba(0,0,0,0.55)",
      paddingHorizontal: 10,
      paddingVertical: 6,
      borderRadius: 6,
    },
    statsToggleText: {
      color: "#fff",
      fontSize: 12,
    },
    statsOverlay: {
      position: "absolute",
      top: 52,
      right: 16,
      backgroundColor: "rgba(0,0,0,0.7)",
      paddingHorizontal: 12,
      paddingVertical: 8,
      borderRadius: 6,
      minWidth: 130,
    },
    statsTitle: {
      color: "#fff",
      fontSize: 12,
      fontWeight: "bold",
      marginBottom: 4,
    },
    statsVerdict: {
      fontSize: 13,
      fontWeight: "600",
      marginBottom: 4,
    },
    statsLine: {
      color: "#ddd",
      fontSize: 12,
    },
  });
};
