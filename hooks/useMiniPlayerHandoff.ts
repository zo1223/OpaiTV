import { useEffect } from "react";
import { Platform } from "react-native";
import usePlayerStore from "@/stores/playerStore";
import useDetailStore from "@/stores/detailStore";
import { useMiniPlayerStore, MiniPlayerSnapshot } from "@/stores/miniPlayerStore";

/** 离片尾不到这么久就算"播完了",不再交给迷你条 */
const NEAR_END_MILLIS = 3_000;

/**
 * 从播放页当前状态生成交给迷你条的快照。
 * 只在"有视频正在播、还没播完"时返回快照;暂停着离开、还在加载、已经播完都返回 null(保持原来"返回即停止"的行为)。
 */
export const buildMiniPlayerSnapshot = (): MiniPlayerSnapshot | null => {
  const { status, episodes, currentEpisodeIndex, playbackRate } = usePlayerStore.getState();
  const detail = useDetailStore.getState().detail;
  if (!detail || !status || !status.isLoaded || !status.isPlaying || status.didJustFinish) return null;

  const episode = episodes[currentEpisodeIndex];
  if (!episode || !episode.url) return null;

  const positionMillis = status.positionMillis || 0;
  const durationMillis = status.durationMillis || 0;
  if (durationMillis > 0 && positionMillis >= durationMillis - NEAR_END_MILLIS) return null;

  return {
    title: detail.title,
    episodeLabel: episodes.length > 1 ? episode.title : "",
    url: episode.url,
    positionMillis,
    durationMillis,
    isPlaying: true,
    playbackRate,
    routeParams: {
      source: detail.source,
      id: detail.id.toString(),
      title: detail.title,
      episodeIndex: String(currentEpisodeIndex),
    },
  };
};

/**
 * 播放页与迷你条的交接,只在手机/平板上启用(TV 上行为与原来一致)。
 * - 进入播放页:收掉迷你条,避免两个播放器同时出声。
 * - 离开播放页:当时正在播且没播完,就把快照交给迷你条接着播。
 *
 * 必须在播放页里调用 reset() 的那个 effect 之前调用:卸载时 React 按声明顺序执行清理函数,
 * 这样取快照时 playerStore 还没被 reset() 清空。
 */
export const useMiniPlayerHandoff = (deviceType: string) => {
  const enabled = deviceType !== "tv" && !Platform.isTV;

  useEffect(() => {
    if (!enabled) return;
    void useMiniPlayerStore.getState().close();
    return () => {
      const snapshot = buildMiniPlayerSnapshot();
      if (snapshot) {
        useMiniPlayerStore.getState().show(snapshot);
      }
    };
  }, [enabled]);
};
