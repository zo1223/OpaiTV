import { create } from "zustand";
import { PlayRecordManager, PlayerSettingsManager } from "@/services/storage";
import Logger from "@/utils/Logger";

const logger = Logger.withTag("MiniPlayerStore");

/** 重新打开 /play 需要的参数(与详情页、卡片跳转时传的一致,都是字符串) */
export interface MiniPlayerRouteParams {
  source: string;
  id: string;
  title: string;
  episodeIndex: string;
}

/** 播放页离开时交给迷你条的快照 */
export interface MiniPlayerSnapshot {
  title: string;
  episodeLabel?: string;
  url: string;
  positionMillis: number;
  durationMillis: number;
  isPlaying?: boolean;
  playbackRate?: number;
  routeParams: MiniPlayerRouteParams;
}

/** 迷你条播放期间,每隔这么久把进度写回一次观看记录 */
export const MINI_PLAYER_SAVE_INTERVAL_MS = 15_000;

interface MiniPlayerState {
  active: boolean;
  collapsed: boolean;
  title: string;
  episodeLabel: string;
  url: string;
  /** 交接时的进度:迷你条里的播放器从这里起播,之后不再变(当前进度看 positionMillis) */
  startPositionMillis: number;
  positionMillis: number;
  durationMillis: number;
  isPlaying: boolean;
  playbackRate: number;
  routeParams: MiniPlayerRouteParams | null;
  /** 每次 show 加一:迷你条用它做播放器的 key,换片时重建播放器 */
  session: number;
  _lastSavedAt: number;
  show: (snapshot: MiniPlayerSnapshot) => void;
  setProgress: (positionMillis: number, durationMillis?: number) => void;
  setPlaying: (playing: boolean) => void;
  togglePlay: () => void;
  toggleCollapsed: () => void;
  /** 停止并隐藏,把当前进度写回观看记录 */
  close: () => Promise<void>;
  /** 回到全屏播放页:返回带当前进度的路由参数并收起迷你条;没有在播时返回 null */
  restore: () => (MiniPlayerRouteParams & { position: string }) | null;
}

const idleState = {
  active: false,
  collapsed: false,
  title: "",
  episodeLabel: "",
  url: "",
  startPositionMillis: 0,
  positionMillis: 0,
  durationMillis: 0,
  isPlaying: false,
  playbackRate: 1,
  routeParams: null as MiniPlayerRouteParams | null,
  _lastSavedAt: 0,
};

const toSafeMillis = (value: unknown) => (typeof value === "number" && Number.isFinite(value) && value > 0 ? value : 0);

/**
 * 把迷你条的进度写回观看记录。
 * 不自己拼记录:离开播放页时 playerStore.reset() 已经按原有逻辑存过一次完整记录,
 * 这里只读出那条记录、改进度再存回去。记录不存在(被用户删了,或从没存过)时不重建。
 */
const persistProgress = async (
  routeParams: MiniPlayerRouteParams | null,
  positionMillis: number,
  durationMillis: number
): Promise<void> => {
  if (!routeParams || positionMillis <= 0) return;
  try {
    const existing = await PlayRecordManager.get(routeParams.source, routeParams.id);
    if (!existing) return;
    // 片头/片尾标记存在播放器设置里,save() 会把缺失的字段当成"清除",所以必须原样带上
    const settings = await PlayerSettingsManager.get(routeParams.source, routeParams.id);
    const { save_time: _saveTime, ...rest } = existing;
    const episodeNumber = Number(routeParams.episodeIndex) + 1;
    await PlayRecordManager.save(routeParams.source, routeParams.id, {
      ...rest,
      index: Number.isFinite(episodeNumber) && episodeNumber > 0 ? episodeNumber : rest.index,
      play_time: Math.floor(positionMillis / 1000),
      total_time: durationMillis > 0 ? Math.floor(durationMillis / 1000) : rest.total_time,
      introEndTime: settings?.introEndTime ?? existing.introEndTime,
      outroStartTime: settings?.outroStartTime ?? existing.outroStartTime,
    });
  } catch (error) {
    logger.debug("Failed to save mini player progress:", error);
  }
};

export const useMiniPlayerStore = create<MiniPlayerState>((set, get) => ({
  ...idleState,
  session: 0,

  show: (snapshot) => {
    if (!snapshot || !snapshot.url || !snapshot.routeParams) return;
    const previous = get();
    if (previous.active) {
      // 迷你条正在播另一部时被新的交接顶替:先把旧的进度存掉
      void persistProgress(previous.routeParams, previous.positionMillis, previous.durationMillis);
    }
    const positionMillis = toSafeMillis(snapshot.positionMillis);
    set({
      active: true,
      collapsed: false,
      title: snapshot.title || "",
      episodeLabel: snapshot.episodeLabel || "",
      url: snapshot.url,
      startPositionMillis: positionMillis,
      positionMillis,
      durationMillis: toSafeMillis(snapshot.durationMillis),
      isPlaying: snapshot.isPlaying !== false,
      playbackRate: toSafeMillis(snapshot.playbackRate) || 1,
      routeParams: { ...snapshot.routeParams },
      session: previous.session + 1,
      _lastSavedAt: Date.now(),
    });
  },

  setProgress: (positionMillis, durationMillis) => {
    const state = get();
    if (!state.active) return;
    const nextPosition = toSafeMillis(positionMillis);
    const nextDuration = toSafeMillis(durationMillis) || state.durationMillis;
    const now = Date.now();
    const shouldSave = now - state._lastSavedAt >= MINI_PLAYER_SAVE_INTERVAL_MS;
    set({
      positionMillis: nextPosition,
      durationMillis: nextDuration,
      ...(shouldSave ? { _lastSavedAt: now } : {}),
    });
    if (shouldSave) {
      void persistProgress(state.routeParams, nextPosition, nextDuration);
    }
  },

  setPlaying: (playing) => {
    if (get().active) set({ isPlaying: playing });
  },

  togglePlay: () => {
    if (get().active) set({ isPlaying: !get().isPlaying });
  },

  toggleCollapsed: () => {
    if (get().active) set({ collapsed: !get().collapsed });
  },

  close: async () => {
    const state = get();
    if (!state.active) return;
    // 先隐藏(播放器随之卸载、停止出声),再存进度
    set({ ...idleState });
    await persistProgress(state.routeParams, state.positionMillis, state.durationMillis);
  },

  restore: () => {
    const state = get();
    if (!state.active || !state.routeParams) return null;
    const params = { ...state.routeParams, position: String(Math.floor(state.positionMillis)) };
    set({ ...idleState });
    void persistProgress(state.routeParams, state.positionMillis, state.durationMillis);
    return params;
  },
}));

/** 仅供测试:恢复初始状态 */
export const resetMiniPlayerStoreForTests = () => {
  useMiniPlayerStore.setState({ ...idleState, session: 0 });
};

export default useMiniPlayerStore;
