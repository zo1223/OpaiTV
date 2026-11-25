import { create } from "zustand";
import { SettingsManager } from "@/services/storage";
import { DanmakuSettings, DEFAULT_DANMAKU_SETTINGS, normalizeDanmakuSettings } from "@/services/danmaku/types";

interface DanmakuState {
  settings: DanmakuSettings;
  isLoaded: boolean;
  /** 从本地存储读出弹幕设置(只读一次;force 为 true 时重读) */
  load: (force?: boolean) => Promise<void>;
  /** 修改弹幕设置:立即生效并保存,不需要再点"保存设置" */
  update: (patch: Partial<DanmakuSettings>) => Promise<void>;
}

// 弹幕设置和其他设置存在同一份 AsyncStorage 记录里(danmaku* 字段),但单独成一个 store:
// 播放页只订阅弹幕相关的状态,读写失败也只影响弹幕。
let revision = 0;
let loading: Promise<void> | null = null;

const toStored = (settings: DanmakuSettings) => ({
  danmakuEnabled: settings.enabled,
  danmakuServerUrl: settings.serverUrl,
  danmakuOpacity: settings.opacity,
  danmakuSpeed: settings.speed,
  danmakuFontSize: settings.fontSize,
});

export const useDanmakuStore = create<DanmakuState>((set, get) => ({
  settings: DEFAULT_DANMAKU_SETTINGS,
  isLoaded: false,
  load: (force = false) => {
    if (get().isLoaded && !force) return Promise.resolve();
    if (loading && !force) return loading;
    const startedAt = revision;
    // 包一层 Promise:读取同步抛错时也只是用默认值(关闭),不能影响播放页
    const current: Promise<void> = Promise.resolve()
      .then(() => SettingsManager.get())
      .then((stored) => {
        // 读取期间用户已经改过设置的话,以用户刚改的为准
        if (revision === startedAt) {
          set({ settings: normalizeDanmakuSettings(stored || {}), isLoaded: true });
        }
      })
      .catch(() => {
        // 读不出来就用默认值(关闭)
      })
      .then(() => {
        if (loading === current) loading = null;
      });
    loading = current;
    return current;
  },
  update: async (patch) => {
    revision++;
    const settings = normalizeDanmakuSettings(toStored({ ...get().settings, ...patch }));
    set({ settings, isLoaded: true });
    try {
      await SettingsManager.save(toStored(settings));
    } catch {
      // 保存失败只影响下次启动时的弹幕设置
    }
  },
}));
