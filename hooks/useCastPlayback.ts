// DLNA 投屏与本机播放器之间的接线:开始投屏时本机暂停,停止投屏后本机从电视端进度恢复,
// 离开播放页或本机换了集 / 换了源时结束投屏会话。
import { useEffect } from "react";
import useCastStore from "@/stores/castStore";
import usePlayerStore from "@/stores/playerStore";
import useDetailStore from "@/stores/detailStore";
import { DlnaDevice } from "@/services/dlna/description";

/** 把播放页当前在播的这一集推到设备上;电视开始播了以后本机暂停。 */
export const castCurrentVideo = async (device: DlnaDevice): Promise<boolean> => {
  const player = usePlayerStore.getState();
  const episode = player.episodes[player.currentEpisodeIndex];
  const detail = useDetailStore.getState().detail;
  const title = [detail?.title, episode?.title].filter(Boolean).join(" ");
  const position = player.status?.isLoaded ? player.status.positionMillis : 0;

  const ok = await useCastStore.getState().cast(device, { url: episode?.url || "", title, positionMillis: position });
  if (ok) {
    try {
      await usePlayerStore.getState().videoRef?.current?.pauseAsync();
    } catch {
      // 本机播放器已经不在了:忽略。
    }
  }
  return ok;
};

/** 停止投屏并恢复本机播放:本机跳到电视端最后的进度后继续播。没有在投屏时什么都不做。 */
export const stopCastAndResumeLocal = async (): Promise<void> => {
  const lastPosition = await useCastStore.getState().stop();
  if (lastPosition === null) return;
  const video = usePlayerStore.getState().videoRef?.current;
  if (!video) return;
  try {
    if (lastPosition > 0) {
      await video.setPositionAsync(lastPosition);
    }
    await video.playAsync();
  } catch {
    // 本机播放器已经不在了(离开了播放页):忽略。
  }
};

/**
 * 播放页挂这个 hook:
 * - 离开播放页时结束投屏会话(电视停止、轮询停掉)。控制面板只在播放页上,不能留下没人管的会话。
 * - 投屏期间本机换了集或换了源(在播地址变了):结束投屏,电视上不再播旧的那一集。
 */
export const useCastLifecycle = () => {
  const currentUrl = usePlayerStore((state) => state.episodes[state.currentEpisodeIndex]?.url);

  useEffect(() => {
    const cast = useCastStore.getState();
    if (cast.status !== "idle" && cast.media && currentUrl && cast.media.url !== currentUrl) {
      cast.stop().catch(() => {});
    }
  }, [currentUrl]);

  useEffect(
    () => () => {
      const cast = useCastStore.getState();
      cast.closeModal();
      if (cast.status !== "idle") {
        cast.stop().catch(() => {});
      }
    },
    []
  );
};
