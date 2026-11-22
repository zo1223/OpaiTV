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
    setShowAdultContent: jest.fn(),
  },
}));

jest.mock("@/services/videoSourceSubscription", () => ({
  ...jest.requireActual("@/services/videoSourceSubscription"),
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
      showAdultContent: false,
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

  describe("adult content toggle", () => {
    const { api } = jest.requireMock("@/services/api");

    it("is off by default and when saved settings are missing the flag", async () => {
      expect(useSettingsStore.getInitialState().showAdultContent).toBe(false);
      (SettingsManager.get as jest.Mock).mockResolvedValue(baseSettings);

      await useSettingsStore.getState().loadSettings();

      expect(useSettingsStore.getState().showAdultContent).toBe(false);
      expect(api.setShowAdultContent).toHaveBeenLastCalledWith(false);
    });

    it.each([
      [true, true],
      [false, false],
      ["true", false],
      [1, false],
    ])("loads a saved value of %p as %p", async (saved, expected) => {
      (SettingsManager.get as jest.Mock).mockResolvedValue({ ...baseSettings, showAdultContent: saved });

      await useSettingsStore.getState().loadSettings();

      expect(useSettingsStore.getState().showAdultContent).toBe(expected);
      expect(api.setShowAdultContent).toHaveBeenLastCalledWith(expected);
    });

    it("applies a change to the API client before subscribers see it, and persists it straight away", async () => {
      const appliedWhenNotified: boolean[] = [];
      const unsubscribe = useSettingsStore.subscribe((state, previous) => {
        if (state.showAdultContent !== previous.showAdultContent) {
          appliedWhenNotified.push(
            (api.setShowAdultContent as jest.Mock).mock.calls.some(([enabled]) => enabled === state.showAdultContent)
          );
        }
      });

      await useSettingsStore.getState().setShowAdultContent(true);
      unsubscribe();

      expect(appliedWhenNotified).toEqual([true]);
      expect(useSettingsStore.getState().showAdultContent).toBe(true);
      expect(SettingsManager.save).toHaveBeenCalledWith({ showAdultContent: true });

      await useSettingsStore.getState().setShowAdultContent(false);

      expect(api.setShowAdultContent).toHaveBeenLastCalledWith(false);
      expect(useSettingsStore.getState().showAdultContent).toBe(false);
      expect(SettingsManager.save).toHaveBeenLastCalledWith({ showAdultContent: false });
    });

    it("is not overwritten by saving the other settings", async () => {
      (SettingsManager.get as jest.Mock).mockResolvedValue({ ...baseSettings, showAdultContent: true });

      await useSettingsStore.getState().saveSettings();

      expect((SettingsManager.save as jest.Mock).mock.calls[0][0]).not.toHaveProperty("showAdultContent");
    });
  });

  describe("one-click video sources", () => {
    const { api } = jest.requireMock("@/services/api");
    const { fetchVideoSourceSubscription } = jest.requireMock("@/services/videoSourceSubscription");
    const subscribed = { key: "demo", name: "Demo", api: "https://demo.example/vod" };
    const manual = { key: "custom_my.example", name: "my.example", api: "https://my.example/api.php/provide/vod" };

    it("adds a pasted source URL in one step: saved and live without saving the settings", async () => {
      useSettingsStore.setState({ subscribedVideoSources: [subscribed] } as any);

      await useSettingsStore.getState().addVideoSource(" https://my.example/api.php/provide/vod/ ", false);

      const expected = [subscribed, manual];
      expect(useSettingsStore.getState().subscribedVideoSources).toEqual(expected);
      expect(SettingsManager.save).toHaveBeenCalledWith({ subscribedVideoSources: expected });
      expect(api.setSubscribedVideoSources).toHaveBeenLastCalledWith(expected);
    });

    it("stores the adult flag of a source added as adult", async () => {
      await useSettingsStore.getState().addVideoSource("https://my.example/api.php/provide/vod", true);

      expect(useSettingsStore.getState().subscribedVideoSources).toEqual([{ ...manual, adult: true }]);
    });

    it("rejects an invalid URL without changing anything", async () => {
      useSettingsStore.setState({ subscribedVideoSources: [subscribed] } as any);

      await expect(useSettingsStore.getState().addVideoSource("not a url", false)).rejects.toThrow("INVALID_VIDEO_SOURCE_URL");

      expect(useSettingsStore.getState().subscribedVideoSources).toEqual([subscribed]);
      expect(SettingsManager.save).not.toHaveBeenCalled();
      expect(api.setSubscribedVideoSources).not.toHaveBeenCalled();
    });

    it("removes a source", async () => {
      useSettingsStore.setState({ subscribedVideoSources: [subscribed, manual] } as any);

      await useSettingsStore.getState().removeVideoSource(manual.key);

      expect(useSettingsStore.getState().subscribedVideoSources).toEqual([subscribed]);
      expect(SettingsManager.save).toHaveBeenCalledWith({ subscribedVideoSources: [subscribed] });
      expect(api.setSubscribedVideoSources).toHaveBeenLastCalledWith([subscribed]);
    });

    it("keeps added sources when the subscription link is cleared", async () => {
      (SettingsManager.get as jest.Mock).mockResolvedValue({ ...baseSettings, videoSourceSubscriptionUrl: "https://sub.example/a" });
      useSettingsStore.setState({ videoSourceSubscriptionUrl: "", subscribedVideoSources: [subscribed, manual] } as any);

      await useSettingsStore.getState().saveSettings();

      expect(SettingsManager.save).toHaveBeenCalledWith(expect.objectContaining({ subscribedVideoSources: [manual] }));
      expect(api.setSubscribedVideoSources).toHaveBeenLastCalledWith([manual]);
    });

    it("keeps added sources when the subscription link is replaced", async () => {
      const fresh = { key: "fresh", name: "Fresh", api: "https://fresh.example/vod", adult: true };
      (SettingsManager.get as jest.Mock).mockResolvedValue({ ...baseSettings, videoSourceSubscriptionUrl: "https://sub.example/a" });
      (fetchVideoSourceSubscription as jest.Mock).mockResolvedValue({ sources: [fresh] });
      useSettingsStore.setState({
        videoSourceSubscriptionUrl: "https://sub.example/b",
        subscribedVideoSources: [subscribed, manual],
      } as any);

      await useSettingsStore.getState().saveSettings();

      expect(useSettingsStore.getState().subscribedVideoSources).toEqual([fresh, manual]);
    });
  });
});
