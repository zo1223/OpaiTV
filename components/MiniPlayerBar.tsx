import React, { useCallback, useEffect, useRef } from "react";
import { AppState, Platform, Pressable, StyleSheet, Text, View } from "react-native";
import { usePathname, useRouter } from "expo-router";
import { AVPlaybackStatus, ResizeMode, Video } from "expo-av";
import Toast from "react-native-toast-message";
import { ChevronDown, ChevronUp, Pause, Play, X } from "lucide-react-native";
import { useMiniPlayerStore } from "@/stores/miniPlayerStore";
import { usePlayRecordUndoStore } from "@/stores/playRecordUndoStore";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";

export const formatMiniPlayerTime = (millis: number): string => {
  const totalSeconds = Math.max(0, Math.floor((Number.isFinite(millis) ? millis : 0) / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const pad = (value: number) => value.toString().padStart(2, "0");
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${pad(minutes)}:${pad(seconds)}`;
};

export const getMiniPlayerProgress = (positionMillis: number, durationMillis: number): number => {
  if (!(durationMillis > 0) || !(positionMillis > 0)) return 0;
  return Math.min(1, positionMillis / durationMillis);
};

/**
 * 悬浮迷你播放条(灵动岛):离开播放页时正在播的视频在这里接着播。
 * 挂在根布局,切换页面不会断;手机上浮在底部 Tab 上方,平板上在右下角。
 * 可收起成一个小胶囊(只留片名、进度条和展开按钮,声音不断);点条本体回到全屏播放页并从当前进度续播。
 * TV 上不启用。
 */
export const MiniPlayerBar: React.FC = () => {
  const active = useMiniPlayerStore((state) => state.active);
  const collapsed = useMiniPlayerStore((state) => state.collapsed);
  const title = useMiniPlayerStore((state) => state.title);
  const episodeLabel = useMiniPlayerStore((state) => state.episodeLabel);
  const url = useMiniPlayerStore((state) => state.url);
  const startPositionMillis = useMiniPlayerStore((state) => state.startPositionMillis);
  const positionMillis = useMiniPlayerStore((state) => state.positionMillis);
  const durationMillis = useMiniPlayerStore((state) => state.durationMillis);
  const isPlaying = useMiniPlayerStore((state) => state.isPlaying);
  const playbackRate = useMiniPlayerStore((state) => state.playbackRate);
  const session = useMiniPlayerStore((state) => state.session);
  const undoPending = usePlayRecordUndoStore((state) => !!state.pending);
  const { deviceType } = useResponsiveLayout();
  const router = useRouter();
  const pathname = usePathname();
  const videoRef = useRef<Video>(null);
  // 起播定位完成之前的状态回调里进度还是 0,不能写进 store(否则进度条跳回开头、此时点回全屏会从头播)
  const seekedSessionRef = useRef(-1);

  const disabled = Platform.isTV || deviceType === "tv";

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (status) => {
      // 与播放页一致:应用退到后台就暂停
      if (status !== "active") {
        useMiniPlayerStore.getState().setPlaying(false);
      }
    });
    return () => subscription.remove();
  }, []);

  useEffect(() => {
    // 直播页有自己的播放器:进去时收掉迷你条,避免两路声音。播放页由 useMiniPlayerHandoff 负责收
    if (active && pathname === "/live") {
      void useMiniPlayerStore.getState().close();
    }
  }, [active, pathname]);

  const handleLoad = useCallback(async () => {
    const loadedSession = session;
    try {
      if (startPositionMillis > 0) {
        await videoRef.current?.setPositionAsync(startPositionMillis);
      }
      if (useMiniPlayerStore.getState().isPlaying) {
        await videoRef.current?.playAsync();
      }
    } catch {
      // 定位或自动播放失败不致命:照常显示,用户可以手动点播放
    } finally {
      seekedSessionRef.current = loadedSession;
    }
  }, [session, startPositionMillis]);

  const handleError = useCallback(() => {
    Toast.show({ type: "error", text1: "迷你播放器播放失败", text2: "请回到播放页重试" });
    void useMiniPlayerStore.getState().close();
  }, []);

  const handleStatus = useCallback(
    (status: AVPlaybackStatus) => {
      if (!status.isLoaded) {
        if (status.error) handleError();
        return;
      }
      if (seekedSessionRef.current !== session) return;
      useMiniPlayerStore.getState().setProgress(status.positionMillis, status.durationMillis);
      if (status.didJustFinish) {
        void useMiniPlayerStore.getState().close();
      }
    },
    [session, handleError]
  );

  const handleRestore = useCallback(() => {
    const params = useMiniPlayerStore.getState().restore();
    if (params) {
      router.push({ pathname: "/play", params: { ...params } });
    }
  }, [router]);

  if (disabled || !active || !url) return null;

  const progress = getMiniPlayerProgress(positionMillis, durationMillis);
  const isMobile = deviceType === "mobile";
  const hostStyle = isMobile
    ? { left: 12, right: 12, bottom: undoPending ? 156 : 96 }
    : { right: 24, bottom: 24, width: 400 };
  const heading = episodeLabel ? `${title} · ${episodeLabel}` : title;

  return (
    <View pointerEvents="box-none" style={[styles.host, hostStyle]} testID="mini-player-host">
      <View style={[styles.bar, collapsed && styles.barCollapsed]} testID="mini-player-bar">
        <Pressable
          testID="mini-player-body"
          accessibilityRole="button"
          accessibilityLabel={`回到全屏播放 ${heading}`}
          onPress={handleRestore}
          style={styles.body}
        >
          {/* 播放器始终留在同一个位置:收起时只是缩到看不见,不卸载,所以声音不断、进度不丢 */}
          <Video
            key={session}
            ref={videoRef}
            testID="mini-player-video"
            style={collapsed ? styles.videoHidden : styles.video}
            source={{ uri: url }}
            resizeMode={ResizeMode.COVER}
            shouldPlay={isPlaying}
            rate={playbackRate}
            useNativeControls={false}
            onLoad={handleLoad}
            onError={handleError}
            onPlaybackStatusUpdate={handleStatus}
          />
          <View style={styles.info}>
            <Text style={styles.title} numberOfLines={1} testID="mini-player-title">
              {heading}
            </Text>
            {!collapsed && (
              <Text style={styles.time} numberOfLines={1} testID="mini-player-time">
                {formatMiniPlayerTime(positionMillis)} / {formatMiniPlayerTime(durationMillis)}
              </Text>
            )}
            <View style={styles.progressTrack}>
              <View
                testID="mini-player-progress"
                style={[styles.progressFill, { width: `${Math.round(progress * 100)}%` }]}
              />
            </View>
          </View>
        </Pressable>

        {!collapsed && (
          <Pressable
            testID="mini-player-toggle"
            accessibilityRole="button"
            accessibilityLabel={isPlaying ? "暂停" : "播放"}
            onPress={() => useMiniPlayerStore.getState().togglePlay()}
            style={({ pressed }) => [styles.iconButton, pressed && styles.iconButtonActive]}
          >
            {isPlaying ? <Pause size={20} color="#fff" /> : <Play size={20} color="#fff" />}
          </Pressable>
        )}

        <Pressable
          testID="mini-player-collapse"
          accessibilityRole="button"
          accessibilityLabel={collapsed ? "展开迷你播放器" : "收起迷你播放器"}
          onPress={() => useMiniPlayerStore.getState().toggleCollapsed()}
          style={({ pressed }) => [styles.iconButton, pressed && styles.iconButtonActive]}
        >
          {collapsed ? <ChevronUp size={20} color="#fff" /> : <ChevronDown size={20} color="#fff" />}
        </Pressable>

        {!collapsed && (
          <Pressable
            testID="mini-player-close"
            accessibilityRole="button"
            accessibilityLabel="关闭迷你播放器"
            onPress={() => void useMiniPlayerStore.getState().close()}
            style={({ pressed }) => [styles.iconButton, pressed && styles.iconButtonActive]}
          >
            <X size={20} color="#fff" />
          </Pressable>
        )}
      </View>
    </View>
  );
};

const styles = StyleSheet.create({
  host: {
    position: "absolute",
    alignItems: "flex-end",
  },
  bar: {
    flexDirection: "row",
    alignItems: "center",
    alignSelf: "stretch",
    backgroundColor: "#151828",
    borderRadius: 20,
    borderWidth: 1,
    borderColor: "rgba(255,255,255,0.08)",
    padding: 6,
    elevation: 10,
    shadowColor: "#000",
    shadowOpacity: 0.35,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 3 },
  },
  barCollapsed: {
    alignSelf: "flex-end",
    maxWidth: 220,
    borderRadius: 22,
    paddingLeft: 14,
  },
  body: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    minWidth: 0,
  },
  video: {
    width: 96,
    height: 54,
    borderRadius: 12,
    backgroundColor: "#000",
    marginRight: 10,
  },
  videoHidden: {
    width: 1,
    height: 1,
    opacity: 0,
  },
  info: {
    flex: 1,
    minWidth: 0,
  },
  title: {
    color: "#fff",
    fontSize: 14,
    fontWeight: "600",
  },
  time: {
    color: "#9AA1B8",
    fontSize: 12,
    marginTop: 2,
  },
  progressTrack: {
    height: 3,
    borderRadius: 2,
    backgroundColor: "rgba(255,255,255,0.2)",
    marginTop: 6,
    overflow: "hidden",
  },
  progressFill: {
    height: 3,
    borderRadius: 2,
    backgroundColor: "#6FDCA8",
  },
  iconButton: {
    width: 40,
    height: 40,
    borderRadius: 20,
    alignItems: "center",
    justifyContent: "center",
    marginLeft: 2,
  },
  iconButtonActive: {
    backgroundColor: "rgba(255,255,255,0.14)",
  },
});

export default MiniPlayerBar;
