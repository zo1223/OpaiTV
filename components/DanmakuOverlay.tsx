import React, { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Animated, Easing, LayoutChangeEvent, Platform, StyleSheet, Text, View } from "react-native";
import type { AVPlaybackStatus } from "expo-av";
import usePlayerStore from "@/stores/playerStore";
import useDetailStore from "@/stores/detailStore";
import { useDanmakuStore } from "@/stores/danmakuStore";
import Toast from "react-native-toast-message";
import { DANMAKU_NOTICE, danmakuService, episodeNumberFromLabel } from "@/services/danmaku";
import { DanmakuClock, DanmakuEngine, DanmakuItem, DanmakuTimeline } from "@/services/danmaku/engine";
import {
  DANMAKU_FIXED_SECONDS,
  DANMAKU_FONT_SCALE,
  DANMAKU_SCROLL_SECONDS,
  DanmakuTrack,
} from "@/services/danmaku/types";

/** 多久推进一次弹幕(新弹幕进场、过期弹幕移除);弹幕本身的移动由原生动画完成 */
const TICK_MS = 250;
/** 提前这么多秒(按 1 倍速算,倍速播放时等比放大)把滚动弹幕建好,让它从画面右边之外飘进来(要大于 TICK_MS) */
const LOOKAHEAD_SECONDS = 0.6;
/** 画面上同时最多几条(电视盒子性能有限) */
const MAX_ITEMS = 40;

/** 弹幕状态轻提示(用全局 Toast,2.5 秒自动消失);提示出不来不能影响播放 */
export const showDanmakuNotice = (text: string) => {
  try {
    Toast.show({ type: "info", text1: text, visibilityTime: 2500 });
  } catch {
    // 忽略
  }
};

const toCssColor = (color: number) => `#${(color & 0xffffff).toString(16).padStart(6, "0")}`;

/** 按画面短边定基准字号:手机约 18,电视约 24 */
export const danmakuBaseFontSize = (width: number, height: number) =>
  Math.round(Math.min(28, Math.max(14, Math.min(width, height) / 22)));

interface DanmakuLineProps {
  item: DanmakuItem;
  engine: DanmakuEngine;
  clock: DanmakuClock;
  fontSize: number;
  lineHeight: number;
  /** 播放状态变了(暂停/继续/倍速/对表)就加一,让滚动动画按当前视频时间重新起步 */
  epoch: number;
}

const DanmakuLine = memo(function DanmakuLine({ item, engine, clock, fontSize, lineHeight, epoch }: DanmakuLineProps) {
  const x = useRef(new Animated.Value(engine.xAt(item, clock.now(Date.now())))).current;

  useEffect(() => {
    if (item.mode !== "scroll") return;
    const wall = Date.now();
    const time = clock.now(wall);
    x.stopAnimation();
    x.setValue(engine.xAt(item, time));
    // 暂停或卡住时停在当前位置;在播就从当前位置匀速滚到左边界外
    if (!clock.isRunningAt(wall) || item.end <= time) return;
    const animation = Animated.timing(x, {
      toValue: engine.xAt(item, item.end),
      duration: ((item.end - time) * 1000) / clock.playbackRate,
      easing: Easing.linear,
      useNativeDriver: Platform.OS !== "web",
      isInteraction: false,
    });
    animation.start();
    return () => animation.stop();
  }, [item, engine, clock, x, epoch]);

  const textStyle = [styles.text, { fontSize, lineHeight, color: toCssColor(item.comment.color) }];

  if (item.mode !== "scroll") {
    return (
      <View style={[styles.fixedRow, { top: item.y, height: lineHeight }]}>
        <Text numberOfLines={1} style={textStyle}>
          {item.comment.text}
        </Text>
      </View>
    );
  }

  return (
    <Animated.View style={[styles.scrollRow, { top: item.y, width: item.width, transform: [{ translateX: x }] }]}>
      <Text numberOfLines={1} ellipsizeMode="clip" style={textStyle}>
        {item.comment.text}
      </Text>
    </Animated.View>
  );
});

const DanmakuLayer = memo(function DanmakuLayer() {
  const serverUrl = useDanmakuStore((state) => state.settings.serverUrl);
  const opacity = useDanmakuStore((state) => state.settings.opacity);
  const speed = useDanmakuStore((state) => state.settings.speed);
  const fontSizeSetting = useDanmakuStore((state) => state.settings.fontSize);

  const title = useDetailStore((state) => state.detail?.title) || "";
  const year = useDetailStore((state) => state.detail?.year) || "";
  const episodeIndex = usePlayerStore((state) => state.currentEpisodeIndex);
  const episodeCount = usePlayerStore((state) => state.episodes.length);
  const episodeLabel = usePlayerStore((state) => state.episodes[state.currentEpisodeIndex]?.title);
  const episodeNumber = episodeNumberFromLabel(episodeLabel, episodeIndex);

  const [size, setSize] = useState({ width: 0, height: 0 });
  const [track, setTrack] = useState<DanmakuTrack | null>(null);
  const [items, setItems] = useState<DanmakuItem[]>([]);
  const [engine, setEngine] = useState<DanmakuEngine | null>(null);
  const [epoch, setEpoch] = useState(0);
  const clock = useRef(new DanmakuClock()).current;
  const tickRef = useRef<(() => void) | null>(null);
  /** 视频是否已加载(换集、换源期间为 false:此时进度无意义,不显示弹幕) */
  const loadedRef = useRef(false);

  const fontSize = Math.round(danmakuBaseFontSize(size.width, size.height) * DANMAKU_FONT_SCALE[fontSizeSetting]);
  const scrollSeconds = DANMAKU_SCROLL_SECONDS[speed];

  const onLayout = useCallback((event: LayoutChangeEvent) => {
    const { width, height } = event.nativeEvent.layout;
    setSize((current) =>
      current.width === Math.round(width) && current.height === Math.round(height)
        ? current
        : { width: Math.round(width), height: Math.round(height) }
    );
  }, []);

  // 1. 按片名和集数匹配弹幕。换集、换片、换服务器地址时重新匹配;匹配不到或失败就没有弹幕,并提示原因
  useEffect(() => {
    setTrack(null);
    if (!title || episodeCount <= 0 || episodeIndex < 0 || episodeIndex >= episodeCount) return;
    let cancelled = false;
    danmakuService
      .resolveDetailed({ title, year, episodeIndex, episodeNumber, episodeCount }, serverUrl)
      .then((result) => {
        if (cancelled) return;
        setTrack(result.track);
        // 没有弹幕时给一个轻提示,不再静默:区分"没有这一集"和"访问失败"
        if (!result.track) {
          showDanmakuNotice(result.outcome === "failed" ? DANMAKU_NOTICE.failed : DANMAKU_NOTICE.notFound);
        }
      })
      .catch(() => {
        // resolve 本身不抛错,这里只是兜底
      });
    return () => {
      cancelled = true;
    };
  }, [title, year, episodeIndex, episodeNumber, episodeCount, serverUrl]);

  // 2. 弹幕时钟跟着播放器走:暂停就停,拖动进度、换倍速后重新对表
  useEffect(() => {
    const apply = (status: AVPlaybackStatus | null) => {
      const wall = Date.now();
      const loaded = !!status && status.isLoaded;
      let changed = loaded !== loadedRef.current;
      loadedRef.current = loaded;
      if (!status || !status.isLoaded) {
        changed = clock.setPlaying(false, wall) || changed;
      } else {
        changed = clock.setRate(status.rate || 1, wall) || changed;
        changed = clock.setPlaying(status.isPlaying, wall) || changed;
        changed = clock.sync(status.positionMillis / 1000, wall) || changed;
      }
      if (changed) {
        // 先按新时间重排(拖动进度后整屏换掉),再让还在画面上的弹幕从新位置起步
        tickRef.current?.();
        setEpoch((value) => value + 1);
      }
    };
    apply(usePlayerStore.getState().status);
    return usePlayerStore.subscribe((state, previous) => {
      if (state.status !== previous.status) apply(state.status);
    });
  }, [clock]);

  // 3. 排布:定时把时钟时间喂给排布引擎,画面上的弹幕集合变了才重新渲染
  useEffect(() => {
    if (!track || size.width <= 0 || size.height <= 0) return;

    const timeline: DanmakuTimeline = new DanmakuTimeline(track, (index) => {
      const [from, to] = timeline.rangeOf(index);
      layout.notifyLoaded(from, to);
    });
    const layout = new DanmakuEngine((from, to) => timeline.between(from, to), {
      width: size.width,
      height: size.height,
      fontSize,
      scrollSeconds,
      fixedSeconds: DANMAKU_FIXED_SECONDS,
      maxItems: MAX_ITEMS,
    });

    let signature = "";
    let wasRunning = clock.isRunningAt(Date.now());
    const tick = () => {
      const wall = Date.now();
      if (!loadedRef.current) {
        // 视频还没加载好(刚进入、换集、换源):清空画面,等有了进度再排
        layout.invalidate();
        if (signature !== "") {
          signature = "";
          setItems([]);
        }
        return;
      }
      const time = clock.now(wall);
      timeline.ensure(time);
      layout.advance(time, LOOKAHEAD_SECONDS * Math.max(1, clock.playbackRate));
      // 滚动弹幕提前建好(此时还在画面右边之外);顶部/底部弹幕到了时间才显示
      const visible = layout.items.filter((item) => item.mode === "scroll" || layout.isVisibleAt(item, time));
      const next = visible.map((item) => item.id).join(",");
      if (next !== signature) {
        signature = next;
        setItems(visible);
      }
      // 进度上报断了(卡住)或恢复时,让滚动动画跟着停下或继续
      const running = clock.isRunningAt(wall);
      if (running !== wasRunning) {
        wasRunning = running;
        setEpoch((value) => value + 1);
      }
    };

    setEngine(layout);
    tickRef.current = tick;
    tick();
    const timer = setInterval(tick, TICK_MS);
    return () => {
      clearInterval(timer);
      if (tickRef.current === tick) tickRef.current = null;
      timeline.dispose();
      setEngine(null);
      setItems([]);
    };
  }, [track, size.width, size.height, fontSize, scrollSeconds, clock]);

  const lineHeight = engine?.lineHeight ?? 0;
  const lines = useMemo(
    () =>
      engine
        ? items.map((item) => (
            <DanmakuLine
              key={item.id}
              item={item}
              engine={engine}
              clock={clock}
              fontSize={fontSize}
              lineHeight={lineHeight}
              epoch={epoch}
            />
          ))
        : null,
    [items, engine, clock, fontSize, lineHeight, epoch]
  );

  return (
    <View testID="danmaku-layer" pointerEvents="none" style={[styles.layer, { opacity }]} onLayout={onLayout}>
      {lines}
    </View>
  );
});

/** 弹幕层内部出任何错都只是不显示弹幕,不能连累播放页 */
class DanmakuErrorBoundary extends React.Component<{ children: React.ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch() {
    // 已经不再渲染弹幕层,无需其他处理
  }

  render() {
    return this.state.failed ? null : this.props.children;
  }
}

/**
 * 弹幕层:盖在视频画面上,不接收触摸和焦点。
 * 弹幕开关关闭(默认)时什么都不渲染、不发任何请求;匹配不到或取数失败时是一个空的透明层。
 */
export const DanmakuOverlay = memo(function DanmakuOverlay() {
  const enabled = useDanmakuStore((state) => state.settings.enabled);

  useEffect(() => {
    try {
      void useDanmakuStore.getState().load();
    } catch {
      // 读不出设置就按默认值(关闭)
    }
  }, []);

  if (!enabled) return null;
  return (
    <DanmakuErrorBoundary>
      <DanmakuLayer />
    </DanmakuErrorBoundary>
  );
});

const styles = StyleSheet.create({
  layer: {
    ...StyleSheet.absoluteFillObject,
    overflow: "hidden",
  },
  scrollRow: {
    position: "absolute",
    left: 0,
  },
  fixedRow: {
    position: "absolute",
    left: 0,
    right: 0,
    alignItems: "center",
  },
  text: {
    fontWeight: "bold",
    includeFontPadding: false,
    textShadowColor: "rgba(0, 0, 0, 0.9)",
    textShadowOffset: { width: 1, height: 1 },
    textShadowRadius: 2,
  },
});
