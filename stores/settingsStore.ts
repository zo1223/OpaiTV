import { create } from "zustand";
import { SettingsManager } from "@/services/storage";
import { api, ServerConfig } from "@/services/api";
import { storageConfig } from "@/services/storageConfig";
import AsyncStorage from "@react-native-async-storage/async-storage";
import Logger from "@/utils/Logger";
import { fetchVideoSourceSubscription, VideoSourceSubscriptionSource } from "@/services/videoSourceSubscription";
import { normalizeApiBaseUrl } from "@/services/settingsNormalization";

const logger = Logger.withTag('SettingsStore');

interface SettingsState {
  apiBaseUrl: string;
  videoSourceSubscriptionUrl: string;
  subscribedVideoSources: VideoSourceSubscriptionSource[];
  autoSkipAdsEnabled: boolean;
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
  setM3uUrl: (url: string) => void;
  setRemoteInputEnabled: (enabled: boolean) => void;
  saveSettings: () => Promise<void>;
  setVideoSource: (config: { enabledAll: boolean; sources: { [key: string]: boolean } }) => void;
  showModal: () => void;
  hideModal: () => void;
}

export const useSettingsStore = create<SettingsState>((set, get) => ({
  apiBaseUrl: "",
  videoSourceSubscriptionUrl: "",
  subscribedVideoSources: [],
  autoSkipAdsEnabled: true,
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
    set({
      apiBaseUrl: settings.apiBaseUrl,
      videoSourceSubscriptionUrl: settings.videoSourceSubscriptionUrl,
      subscribedVideoSources: settings.subscribedVideoSources || [],
      autoSkipAdsEnabled: settings.autoSkipAdsEnabled !== false,
      m3uUrl: settings.m3uUrl,
      remoteInputEnabled: settings.remoteInputEnabled || false,
      videoSource: settings.videoSource || {
        enabledAll: true,
        sources: {},
      },
    });
    api.setSubscribedVideoSources(settings.subscribedVideoSources || []);
    if (settings.apiBaseUrl) {
      api.setBaseUrl(settings.apiBaseUrl);
      await get().fetchServerConfig();
    }
  },
  fetchServerConfig: async () => {
    set({ isLoadingServerConfig: true });
    try {
      const config = await api.getServerConfig();
      if (config) {
        storageConfig.setStorageType(config.StorageType);
        set({ serverConfig: config });
      }
    } catch (error) {
      set({ serverConfig: null });
      logger.error("Failed to fetch server config:", error);
    } finally {
      set({ isLoadingServerConfig: false });
    }
  },
  setApiBaseUrl: (url) => set({ apiBaseUrl: url }),
  setVideoSourceSubscriptionUrl: (url) => set({ videoSourceSubscriptionUrl: url }),
  setAutoSkipAdsEnabled: (enabled) => set({ autoSkipAdsEnabled: enabled }),
  setM3uUrl: (url) => set({ m3uUrl: url }),
  setRemoteInputEnabled: (enabled) => set({ remoteInputEnabled: enabled }),
  setVideoSource: (config) => set({ videoSource: config }),
  saveSettings: async () => {
    const { apiBaseUrl, videoSourceSubscriptionUrl, autoSkipAdsEnabled, m3uUrl, remoteInputEnabled, videoSource } = get();
    const currentSettings = await SettingsManager.get()
    const currentApiBaseUrl = currentSettings.apiBaseUrl;
    const processedVideoSourceSubscriptionUrl = videoSourceSubscriptionUrl.trim();
    let subscribedVideoSources = get().subscribedVideoSources;

    if (!processedVideoSourceSubscriptionUrl) {
      subscribedVideoSources = [];
    } else if (processedVideoSourceSubscriptionUrl !== currentSettings.videoSourceSubscriptionUrl) {
      const config = await fetchVideoSourceSubscription(processedVideoSourceSubscriptionUrl);
      subscribedVideoSources = config.sources;
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
    // Also update the URL in the state so the input field shows the processed URL
    set({
      isModalVisible: false,
      apiBaseUrl: processedApiBaseUrl,
      videoSourceSubscriptionUrl: processedVideoSourceSubscriptionUrl,
      subscribedVideoSources,
      autoSkipAdsEnabled,
    });
    api.setSubscribedVideoSources(subscribedVideoSources);
    if (processedApiBaseUrl) {
      await get().fetchServerConfig();
    }
  },
  showModal: () => set({ isModalVisible: true }),
  hideModal: () => set({ isModalVisible: false }),
}));
