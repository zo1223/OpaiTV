import { create } from "zustand";
import Toast from "react-native-toast-message";
import { PlayRecordManager } from "@/services/storage";
import Logger from "@/utils/Logger";

const logger = Logger.withTag("PlayRecordUndo");

/** 删除观看记录后的撤销窗口(毫秒)。桌面端同为 5 秒 */
export const PLAY_RECORD_UNDO_WINDOW_MS = 5000;

export interface PendingPlayRecordDelete {
  /** 存储 key: `${source}+${id}` */
  key: string;
  source: string;
  id: string;
  title: string;
  /** 用户点删除的时刻。提交时若记录的 save_time 比它新(待删期间又播放过),不删 */
  requestedAt: number;
}

interface PlayRecordUndoState {
  /** 当前可撤销的删除;一次只保留一个 */
  pending: PendingPlayRecordDelete | null;
  /** 已过撤销窗口、正在写存储的 key。写完之前列表里同样不显示 */
  committingKeys: string[];
  /** 每当一条被隐藏的记录重新可见(撤销 / 删除失败 / 待删期间被重新播放)时加一,列表据此重新取数 */
  restoreSeq: number;
  /** 把记录标记为待删除:立即从列表隐藏,窗口结束后才真正删除。窗口内已有另一条待删时,那一条立即真正删除 */
  requestDelete: (item: { source: string; id: string; title: string }) => void;
  /** 撤销:存储没有被动过,只是恢复显示。没有待删项时返回 false */
  undo: () => boolean;
  /** 立即提交待删除项(页面卸载、应用退到后台时调用) */
  flush: () => Promise<void>;
  /** 等正在进行的真正删除写完(不提交待撤销项) */
  flushCommitted: () => Promise<void>;
  /** 列表过滤用:待删除或正在删除的记录不显示 */
  isHidden: (key: string) => boolean;
}

export const playRecordKey = (source: string, id: string) => `${source}+${id}`;

let timer: ReturnType<typeof setTimeout> | null = null;
const inFlight = new Set<Promise<void>>();

const clearTimer = () => {
  if (timer !== null) {
    clearTimeout(timer);
    timer = null;
  }
};

export const usePlayRecordUndoStore = create<PlayRecordUndoState>((set, get) => {
  const commit = (item: PendingPlayRecordDelete): Promise<void> => {
    set((state) => ({
      pending: state.pending?.key === item.key ? null : state.pending,
      committingKeys: [...state.committingKeys.filter((key) => key !== item.key), item.key],
    }));

    const work = (async () => {
      let restored = false;
      try {
        const record = await PlayRecordManager.get(item.source, item.id);
        if (record && (record.save_time ?? 0) > item.requestedAt) {
          // 待删期间又播放过:这是一条新的观看进度,不删
          restored = true;
        } else {
          await PlayRecordManager.remove(item.source, item.id);
        }
      } catch (error) {
        restored = true;
        logger.warn("Failed to delete play record:", error);
        Toast.show({ type: "error", text1: "删除观看记录失败", text2: `《${item.title}》已恢复显示` });
      }
      set((state) => ({
        committingKeys: state.committingKeys.filter((key) => key !== item.key),
        restoreSeq: restored ? state.restoreSeq + 1 : state.restoreSeq,
      }));
    })();

    inFlight.add(work);
    return work.finally(() => {
      inFlight.delete(work);
    });
  };

  return {
    pending: null,
    committingKeys: [],
    restoreSeq: 0,

    requestDelete: ({ source, id, title }) => {
      const key = playRecordKey(source, id);
      const previous = get().pending;
      clearTimer();
      if (previous && previous.key !== key) {
        void commit(previous);
      }
      set({ pending: { key, source, id, title, requestedAt: Date.now() } });
      timer = setTimeout(() => {
        timer = null;
        const current = get().pending;
        if (current) {
          void commit(current);
        }
      }, PLAY_RECORD_UNDO_WINDOW_MS);
    },

    undo: () => {
      const current = get().pending;
      if (!current) return false;
      clearTimer();
      set((state) => ({ pending: null, restoreSeq: state.restoreSeq + 1 }));
      return true;
    },

    flush: async () => {
      clearTimer();
      const current = get().pending;
      if (current) {
        void commit(current);
      }
      await Promise.all(Array.from(inFlight));
    },

    flushCommitted: async () => {
      await Promise.all(Array.from(inFlight));
    },

    isHidden: (key) => {
      const { pending, committingKeys } = get();
      return pending?.key === key || committingKeys.includes(key);
    },
  };
});

/** 仅测试用:清掉计时器与状态 */
export const resetPlayRecordUndoStoreForTests = () => {
  clearTimer();
  inFlight.clear();
  usePlayRecordUndoStore.setState({ pending: null, committingKeys: [], restoreSeq: 0 });
};
