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

const logger = Logger.withTag('SettingsStore');

interface SettingsState {
  apiBaseUrl: string;
  videoSourceSubscriptionUrl: string;
  subscribedVideoSources: VideoSourceSubscriptionSource[];
  autoSkipAdsEnabled: boolean;
  /** 成人内容(18+)开关,默认关闭;关闭时成人源及其内容在所有取数入口被过滤(见 services/api) */
  showAdultContent: boolean;
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

export const useSettingsStore = create<SettingsState>((set, get) => ({
  apiBaseUrl: "",
  videoSourceSubscriptionUrl: "",
  subscribedVideoSources: [],
  autoSkipAdsEnabled: true,
  showAdultContent: false,
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
    const settings = await SettingsManager.get();
    // set() re-renders subscribers and runs their effects synchronously, so the API client
    // must already point at the saved address and the config must already count as loading:
    // otherwise the startup login check sees no server and gives up (or is skipped).
    api.setSubscribedVideoSources(settings.subscribedVideoSources || []);
    // 只有明确保存过 true 才算开启:缺失或损坏的值一律按关闭处理
    api.setShowAdultContent(settings.showAdultContent === true);
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
