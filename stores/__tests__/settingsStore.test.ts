import { useSettingsStore } from "../settingsStore";
import { SettingsManager } from "@/services/storage";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

jest.mock("@/services/storage", () => ({
  SettingsManager: {
    get: jest.fn(),
    save: jest.fn(),
  },
}));

jest.mock("@/services/api", () => ({
  api: {
    getServerConfig: jest.fn(),
    setBaseUrl: jest.fn(),
    setSubscribedVideoSources: jest.fn(),
  },
}));

jest.mock("@/services/videoSourceSubscription", () => ({
  fetchVideoSourceSubscription: jest.fn(),
}));

jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: {
    withTag: () => ({
      debug: jest.fn(),
      error: jest.fn(),
      info: jest.fn(),
      warn: jest.fn(),
    }),
  },
}));

const baseSettings = {
  apiBaseUrl: "",
  videoSourceSubscriptionUrl: "",
  subscribedVideoSources: [],
  remoteInputEnabled: true,
  videoSource: {
    enabledAll: true,
    sources: {},
  },
  m3uUrl: "",
};

describe("settingsStore", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    useSettingsStore.setState({
      apiBaseUrl: "",
      videoSourceSubscriptionUrl: "",
      subscribedVideoSources: [],
      m3uUrl: "",
      remoteInputEnabled: false,
      videoSource: {
        enabledAll: true,
        sources: {},
      },
      isModalVisible: false,
      serverConfig: null,
      isLoadingServerConfig: false,
    } as any);
  });

  it("defaults automatic ad skipping to enabled when saved settings are missing the flag", async () => {
    (SettingsManager.get as jest.Mock).mockResolvedValue(baseSettings);

    await useSettingsStore.getState().loadSettings();

    expect(useSettingsStore.getState().autoSkipAdsEnabled).toBe(true);
  });

  it("persists the automatic ad skipping flag", async () => {
    (SettingsManager.get as jest.Mock).mockResolvedValue(baseSettings);
    useSettingsStore.setState({ autoSkipAdsEnabled: false } as any);

    await useSettingsStore.getState().saveSettings();

    expect(SettingsManager.save).toHaveBeenCalledWith(
      expect.objectContaining({
        autoSkipAdsEnabled: false,
      })
    );
  });

  it("drops the old server config and storage type when the API address is cleared", async () => {
    const { storageConfig } = jest.requireActual("@/services/storageConfig");
    (SettingsManager.get as jest.Mock).mockResolvedValue({ ...baseSettings, apiBaseUrl: "http://old.example" });
    storageConfig.setStorageType("redis");
    useSettingsStore.setState({
      apiBaseUrl: "",
      serverConfig: { SiteName: "old", StorageType: "redis" },
    } as any);

    await useSettingsStore.getState().saveSettings();

    expect(useSettingsStore.getState().serverConfig).toBeNull();
    expect(storageConfig.getStorageType()).toBe("localstorage");
  });

  it("keeps the new server's config when the previous address answers late with a failure", async () => {
    const { api } = jest.requireMock("@/services/api");
    let failOldRequest: (error: Error) => void = () => {};
    (api.getServerConfig as jest.Mock)
      .mockImplementationOnce(() => new Promise((_resolve, reject) => (failOldRequest = reject)))
      .mockResolvedValueOnce({ SiteName: "new", StorageType: "redis" });

    const oldRequest = useSettingsStore.getState().fetchServerConfig();
    await useSettingsStore.getState().fetchServerConfig();
    expect(useSettingsStore.getState().serverConfig).toEqual({ SiteName: "new", StorageType: "redis" });

    failOldRequest(new Error("connect timeout"));
    await oldRequest;

    expect(useSettingsStore.getState().serverConfig).toEqual({ SiteName: "new", StorageType: "redis" });
    expect(useSettingsStore.getState().isLoadingServerConfig).toBe(false);
  });

  it.each(["loadSettings", "saveSettings"] as const)(
    "%s points the API client at the address and marks the config as loading before subscribers see it",
    async (action) => {
      const { api } = jest.requireMock("@/services/api");
      (SettingsManager.get as jest.Mock).mockResolvedValue({ ...baseSettings, apiBaseUrl: "http://server.example" });
      (api.getServerConfig as jest.Mock).mockResolvedValue({ SiteName: "s", StorageType: "redis" });
      useSettingsStore.setState({ apiBaseUrl: action === "saveSettings" ? "server.example" : "" } as any);

      // Stands in for the login-check effect in app/_layout.tsx, which runs inside the store's set().
      const seen: { baseUrlSet: boolean; loading: boolean }[] = [];
      const unsubscribe = useSettingsStore.subscribe((state, previous) => {
        if (state.apiBaseUrl !== previous.apiBaseUrl) {
          seen.push({
            baseUrlSet: (api.setBaseUrl as jest.Mock).mock.calls.some(([url]) => url === state.apiBaseUrl),
            loading: state.isLoadingServerConfig,
          });
        }
      });
      await useSettingsStore.getState()[action]();
      unsubscribe();

      expect(seen).toEqual([{ baseUrlSet: true, loading: true }]);
      expect(useSettingsStore.getState().isLoadingServerConfig).toBe(false);
      expect(useSettingsStore.getState().serverConfig).toEqual({ SiteName: "s", StorageType: "redis" });
    }
  );
});
