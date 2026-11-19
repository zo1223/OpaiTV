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

let serverConfigSeq = 0;

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
    // set() re-renders subscribers and runs their effects synchronously, so the API client
    // must already point at the saved address and the config must already count as loading:
    // otherwise the startup login check sees no server and gives up (or is skipped).
    api.setSubscribedVideoSources(settings.subscribedVideoSources || []);
    if (settings.apiBaseUrl) {
      api.setBaseUrl(settings.apiBaseUrl);
    }
    set({
      ...(settings.apiBaseUrl ? { isLoadingServerConfig: true } : {}),
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
