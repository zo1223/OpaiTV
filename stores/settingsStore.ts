import { create } from "zustand";
import { SettingsManager } from "@/services/storage";
import { api, ServerConfig } from "@/services/api";
import { storageConfig } from "@/services/storageConfig";
import AsyncStorage from "@react-native-async-storage/async-storage";
import Logger from "@/utils/Logger";
import {
  addManualVideoSource,
  fetchVideoSourceSubscription,
  isManualVideoSource,
  VideoSourceSubscriptionSource,
} from "@/services/videoSourceSubscription";
import {
  decodeVideoSourceSyncLink,
  isVideoSourceSyncLink,
  mergeSyncedVideoSources,
  VideoSourceSyncMergeResult,
} from "@/services/videoSourceSync";
import { normalizeApiBaseUrl } from "@/services/settingsNormalization";
import { normalizeSourceModes, SourceMode, SourceModeMap, withSourceMode } from "@/services/sourceMode";
import {
  DEFAULT_PLAYBACK_PROXY_MODE,
  isValidProxyAddress,
  normalizePlaybackProxyMode,
  normalizePlaybackProxySettings,
  normalizeProxyAddress,
  PlaybackProxyMode,
} from "@/services/playbackProxy";
import { configureSmartRoute } from "@/services/smartRoute";
import { normalizeTmdbApiKey, TMDB_API_KEY_STORAGE_KEY } from "@/services/tmdb";

const logger = Logger.withTag('SettingsStore');

interface SettingsState {
  apiBaseUrl: string;
  videoSourceSubscriptionUrl: string;
  subscribedVideoSources: VideoSourceSubscriptionSource[];
  autoSkipAdsEnabled: boolean;
  /** 成人内容(18+)开关,默认关闭;关闭时成人源及其内容在所有取数入口被过滤(见 services/api) */
  showAdultContent: boolean;
  /** 片源模式(稳定/影视/全量/关闭),按源 key;没有条目的源是全量 */
  sourceModes: SourceModeMap;
  /** 首页“为你推荐”开关,默认开;推荐只在本机根据观看历史与收藏计算 */
  recommendationsEnabled: boolean;
  /** 播放代理模式(系统代理 / 强制代理 / 直连),只存本机;切换后下次播放生效。见 services/playbackProxy */
  playbackProxyMode: PlaybackProxyMode;
  /** 强制代理使用的中转地址 */
  playbackProxyUrl: string;
  /** 用户自己的 TMDB Key:只存本机(独立的存储 key,不在 AppSettings 里),只发给 TMDB 官方接口 */
  tmdbApiKey: string;
  m3uUrl: string;
  remoteInputEnabled: boolean;
  videoSource: {
    enabledAll: boolean;
    sources: {
      [key: string]: boolean;
    };
  };
  isModalVisible: boolean;
  serverConfig: ServerConfig | null;
  isLoadingServerConfig: boolean;
  loadSettings: () => Promise<void>;
  fetchServerConfig: () => Promise<void>;
  setApiBaseUrl: (url: string) => void;
  setVideoSourceSubscriptionUrl: (url: string) => void;
  setAutoSkipAdsEnabled: (enabled: boolean) => void;
  setShowAdultContent: (enabled: boolean) => Promise<void>;
  /** 切换某个片源的模式:立即生效并保存 */
  setSourceMode: (sourceKey: string, mode: SourceMode) => Promise<void>;
  setRecommendationsEnabled: (enabled: boolean) => Promise<void>;
  /** 切换播放代理模式:立即保存,下次播放生效。选强制代理但没有有效地址时不切换,返回 false */
  setPlaybackProxyMode: (mode: PlaybackProxyMode) => Promise<boolean>;
  /** 保存强制代理的中转地址;地址为空或无效时,强制代理退回系统代理 */
  setPlaybackProxyUrl: (url: string) => Promise<void>;
  /** 保存(或清空)TMDB Key,立即写入本机 */
  setTmdbApiKey: (key: string) => Promise<void>;
  addVideoSource: (url: string, adult: boolean) => Promise<void>;
  /** 导入同步链接(粘贴或扫码得到)里的全部视频源;markAllAdult 为 true 时全部标记为成人源 */
  importVideoSources: (link: string, markAllAdult?: boolean) => Promise<Omit<VideoSourceSyncMergeResult, "sources">>;
  removeVideoSource: (key: string) => Promise<void>;
  setM3uUrl: (url: string) => void;
  setRemoteInputEnabled: (enabled: boolean) => void;
  saveSettings: () => Promise<void>;
  setVideoSource: (config: { enabledAll: boolean; sources: { [key: string]: boolean } }) => void;
  showModal: () => void;
  hideModal: () => void;
}

let serverConfigSeq = 0;

/** 读取本机保存的 TMDB Key;读不到(包括存储不可用)一律按没填处理 */
const readStoredTmdbApiKey = async (): Promise<string> => {
  try {
    return normalizeTmdbApiKey(await AsyncStorage.getItem(TMDB_API_KEY_STORAGE_KEY));
  } catch {
    return "";
  }
};

export const useSettingsStore = create<SettingsState>((set, get) => ({
  apiBaseUrl: "",
  videoSourceSubscriptionUrl: "",
  subscribedVideoSources: [],
  autoSkipAdsEnabled: true,
  showAdultContent: false,
  sourceModes: {},
  recommendationsEnabled: true,
  playbackProxyMode: DEFAULT_PLAYBACK_PROXY_MODE,
  playbackProxyUrl: "",
  tmdbApiKey: "",
  m3uUrl: "",
  liveStreamSources: [],
  remoteInputEnabled: false,
  isModalVisible: false,
  serverConfig: null,
  isLoadingServerConfig: false,
  videoSource: {
    enabledAll: true,
    sources: {},
  },
  loadSettings: async () => {
    // TMDB Key 单独存放,与下面的设置并行读取;没填过时不触发任何状态更新
    const tmdbKeyLoad = readStoredTmdbApiKey().then((tmdbApiKey) => {
      if (tmdbApiKey !== get().tmdbApiKey) set({ tmdbApiKey });
    });
    const settings = await SettingsManager.get();
    // set() re-renders subscribers and runs their effects synchronously, so the API client
    // must already point at the saved address and the config must already count as loading:
    // otherwise the startup login check sees no server and gives up (or is skipped).
    api.setSubscribedVideoSources(settings.subscribedVideoSources || []);
    // 只有明确保存过 true 才算开启:缺失或损坏的值一律按关闭处理
    api.setShowAdultContent(settings.showAdultContent === true);
    const sourceModes = normalizeSourceModes(settings.sourceModes);
    api.setSourceModes(sourceModes);
    const playbackProxy = normalizePlaybackProxySettings({
      mode: settings.playbackProxyMode,
      proxyUrl: settings.playbackProxyUrl,
    });
    if (settings.apiBaseUrl) {
      api.setBaseUrl(settings.apiBaseUrl);
    }
    set({
      ...(settings.apiBaseUrl ? { isLoadingServerConfig: true } : {}),
      apiBaseUrl: settings.apiBaseUrl,
      videoSourceSubscriptionUrl: settings.videoSourceSubscriptionUrl,
      subscribedVideoSources: settings.subscribedVideoSources || [],
      autoSkipAdsEnabled: settings.autoSkipAdsEnabled !== false,
      showAdultContent: settings.showAdultContent === true,
      sourceModes,
      // 默认开:只有明确保存过 false 才算关闭
      recommendationsEnabled: settings.recommendationsEnabled !== false,
      playbackProxyMode: playbackProxy.mode,
      playbackProxyUrl: playbackProxy.proxyUrl,
      m3uUrl: settings.m3uUrl,
      remoteInputEnabled: settings.remoteInputEnabled || false,
      videoSource: settings.videoSource || {
        enabledAll: true,
        sources: {},
      },
    });
    if (settings.apiBaseUrl) {
      await get().fetchServerConfig();
    }
    await tmdbKeyLoad;
  },
  fetchServerConfig: async () => {
    // Only the latest request may write: a slow answer (or late failure) from a
    // previous server address must not replace the current server's config.
    const seq = ++serverConfigSeq;
    set({ isLoadingServerConfig: true });
    try {
      const config = await api.getServerConfig();
      if (seq !== serverConfigSeq) return;
      if (config) {
        storageConfig.setStorageType(config.StorageType);
        set({ serverConfig: config });
      }
    } catch (error) {
      if (seq !== serverConfigSeq) return;
      set({ serverConfig: null });
      logger.error("Failed to fetch server config:", error);
    } finally {
      if (seq === serverConfigSeq) {
        set({ isLoadingServerConfig: false });
      }
    }
  },
  setApiBaseUrl: (url) => set({ apiBaseUrl: url }),
  setVideoSourceSubscriptionUrl: (url) => set({ videoSourceSubscriptionUrl: url }),
  setAutoSkipAdsEnabled: (enabled) => set({ autoSkipAdsEnabled: enabled }),
  // 成人内容开关立即生效并保存,不等"保存设置":关掉之后不能因为忘了点保存而继续显示。
  // 先让取数层生效,再通知订阅者(首页据此清掉已加载的成人条目),最后落盘。
  setShowAdultContent: async (enabled) => {
    api.setShowAdultContent(enabled);
    set({ showAdultContent: enabled });
    await SettingsManager.save({ showAdultContent: enabled });
  },
  // 片源模式与成人开关一样立即生效并保存:先让取数层生效,再通知订阅者,最后落盘
  setSourceMode: async (sourceKey, mode) => {
    const sourceModes = withSourceMode(get().sourceModes, sourceKey, mode);
    api.setSourceModes(sourceModes);
    set({ sourceModes });
    await SettingsManager.save({ sourceModes });
  },
  setRecommendationsEnabled: async (enabled) => {
    set({ recommendationsEnabled: enabled });
    await SettingsManager.save({ recommendationsEnabled: enabled });
  },
  // 播放代理模式:立即保存;播放器只在开始一次播放时读取,所以切换后下次播放生效
  setPlaybackProxyMode: async (mode) => {
    const playbackProxyMode = normalizePlaybackProxyMode(mode);
    if (playbackProxyMode === "forced" && !isValidProxyAddress(get().playbackProxyUrl)) {
      return false;
    }
    set({ playbackProxyMode });
    await SettingsManager.save({ playbackProxyMode });
    return true;
  },
  setPlaybackProxyUrl: async (url) => {
    // 地址被清空或改成无效地址时强制代理无法生效:退回系统代理,显示的模式与实际行为保持一致
    const next = normalizePlaybackProxySettings({ mode: get().playbackProxyMode, proxyUrl: normalizeProxyAddress(url) });
    set({ playbackProxyUrl: next.proxyUrl, playbackProxyMode: next.mode });
    await SettingsManager.save({ playbackProxyUrl: next.proxyUrl, playbackProxyMode: next.mode });
  },
  // TMDB Key 只写进本机独立的存储 key,不进 AppSettings,也不写日志
  setTmdbApiKey: async (key) => {
    const tmdbApiKey = normalizeTmdbApiKey(key);
    set({ tmdbApiKey });
    if (tmdbApiKey) {
      await AsyncStorage.setItem(TMDB_API_KEY_STORAGE_KEY, tmdbApiKey);
    } else {
      await AsyncStorage.removeItem(TMDB_API_KEY_STORAGE_KEY);
    }
  },
  // 一键添加:校验通过即保存并生效,不需要再点"保存设置"
  addVideoSource: async (url, adult) => {
    // 粘贴的是同步链接:导入其中全部源
    if (isVideoSourceSyncLink(url)) {
      await get().importVideoSources(url, adult);
      return;
    }
    const subscribedVideoSources = addManualVideoSource(get().subscribedVideoSources, url, adult);
    await SettingsManager.save({ subscribedVideoSources });
    api.setSubscribedVideoSources(subscribedVideoSources);
    set({ subscribedVideoSources });
  },
  // 同步导入:和一键添加一样立即保存并生效。成人源照常导入,显示与否仍由成人内容开关决定(过滤逻辑在 services/api)
  importVideoSources: async (link, markAllAdult = false) => {
    const incoming = decodeVideoSourceSyncLink(link);
    const { sources: subscribedVideoSources, ...summary } = mergeSyncedVideoSources(
      get().subscribedVideoSources,
      incoming,
      markAllAdult
    );
    await SettingsManager.save({ subscribedVideoSources });
    api.setSubscribedVideoSources(subscribedVideoSources);
    set({ subscribedVideoSources });
    return summary;
  },
  removeVideoSource: async (key) => {
    const subscribedVideoSources = get().subscribedVideoSources.filter((source) => source.key !== key);
    await SettingsManager.save({ subscribedVideoSources });
    api.setSubscribedVideoSources(subscribedVideoSources);
    set({ subscribedVideoSources });
  },
  setM3uUrl: (url) => set({ m3uUrl: url }),
  setRemoteInputEnabled: (enabled) => set({ remoteInputEnabled: enabled }),
  setVideoSource: (config) => set({ videoSource: config }),
  saveSettings: async () => {
    const { apiBaseUrl, videoSourceSubscriptionUrl, autoSkipAdsEnabled, m3uUrl, remoteInputEnabled, videoSource } = get();
    const currentSettings = await SettingsManager.get()
    const currentApiBaseUrl = currentSettings.apiBaseUrl;
    const processedVideoSourceSubscriptionUrl = videoSourceSubscriptionUrl.trim();
    let subscribedVideoSources = get().subscribedVideoSources;

    // 订阅链接被清空或更换时只替换订阅下发的源,一键添加的源保留
    if (!processedVideoSourceSubscriptionUrl) {
      subscribedVideoSources = subscribedVideoSources.filter(isManualVideoSource);
    } else if (processedVideoSourceSubscriptionUrl !== currentSettings.videoSourceSubscriptionUrl) {
      const config = await fetchVideoSourceSubscription(processedVideoSourceSubscriptionUrl);
      // 拉取订阅期间也可能刚添加了源,所以在拉取之后再读一次
      const manualSources = get().subscribedVideoSources.filter(isManualVideoSource);
      subscribedVideoSources = [
        ...config.sources.filter((source) => !manualSources.some((manual) => manual.key === source.key)),
        ...manualSources,
      ];
    }

    const processedApiBaseUrl = normalizeApiBaseUrl(apiBaseUrl);

    await SettingsManager.save({
      apiBaseUrl: processedApiBaseUrl,
      videoSourceSubscriptionUrl: processedVideoSourceSubscriptionUrl,
      subscribedVideoSources,
      autoSkipAdsEnabled,
      m3uUrl,
      remoteInputEnabled,
      videoSource,
    });
    if ( currentApiBaseUrl !== processedApiBaseUrl) {
      await AsyncStorage.setItem('authCookies', '');
    }
    api.setBaseUrl(processedApiBaseUrl);
    api.setSubscribedVideoSources(subscribedVideoSources);
    // Also update the URL in the state so the input field shows the processed URL
    set({
      // Same reason as in loadSettings: effects run inside this set().
      ...(processedApiBaseUrl ? { isLoadingServerConfig: true } : {}),
      isModalVisible: false,
      apiBaseUrl: processedApiBaseUrl,
      videoSourceSubscriptionUrl: processedVideoSourceSubscriptionUrl,
      subscribedVideoSources,
      autoSkipAdsEnabled,
    });
    if (processedApiBaseUrl) {
      await get().fetchServerConfig();
    } else {
      // No backend any more: drop the old server's config, otherwise favorites and
      // play records keep using its storage type and fail with API_URL_NOT_SET.
      serverConfigSeq++;
      storageConfig.setStorageType(undefined);
      set({ serverConfig: null, isLoadingServerConfig: false });
    }
  },
  showModal: () => set({ isModalVisible: true }),
  hideModal: () => set({ isModalVisible: false }),
}));

// 直连优先分流(services/smartRoute):搜索、可播性探测、广告识别等请求每次现读当前的代理设置
configureSmartRoute(() => {
  const { playbackProxyMode, playbackProxyUrl } = useSettingsStore.getState();
  return { mode: playbackProxyMode, proxyUrl: playbackProxyUrl };
});
