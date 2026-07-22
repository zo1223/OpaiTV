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
});
