// Lives outside app/ on purpose: expo-router treats every file under app/ as a route.
import React from "react";
import renderer, { act } from "react-test-renderer";

import RootLayout from "../../app/_layout";
import { useSettingsStore } from "@/stores/settingsStore";
import { SettingsManager } from "@/services/storage";
import { api } from "@/services/api";

const mockCheckLoginStatus = jest.fn();
const mockStartServer = jest.fn();
const mockStopServer = jest.fn();
const mockCheckForUpdate = jest.fn();

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

jest.mock("@/services/storage", () => ({
  SettingsManager: { get: jest.fn(), save: jest.fn() },
}));

jest.mock("@/stores/authStore", () => ({
  __esModule: true,
  default: () => ({ checkLoginStatus: mockCheckLoginStatus }),
}));

jest.mock("@/stores/remoteControlStore", () => ({
  useRemoteControlStore: () => ({ startServer: mockStartServer, stopServer: mockStopServer }),
}));

jest.mock("@/stores/updateStore", () => ({
  useUpdateStore: () => ({ checkForUpdate: mockCheckForUpdate, lastCheckTime: 0 }),
  initUpdateStore: jest.fn(),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "tv" }),
}));

jest.mock("expo-font", () => ({ useFonts: () => [true, null] }));
jest.mock("expo-splash-screen", () => ({ preventAutoHideAsync: jest.fn(), hideAsync: jest.fn() }));
jest.mock("expo-router", () => {
  const Stack = () => null;
  const Screen = () => null;
  Stack.Screen = Screen;
  return { Stack };
});
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: () => null }));
jest.mock("react-native-safe-area-context", () => ({
  SafeAreaProvider: ({ children }: { children: React.ReactNode }) => children,
}));
jest.mock("@/components/LoginModal", () => ({ __esModule: true, default: () => null }));
jest.mock("@/components/UpdateModal", () => ({ UpdateModal: () => null }));

jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: {
    withTag: () => ({ debug: jest.fn(), error: jest.fn(), info: jest.fn(), warn: jest.fn() }),
  },
}));

const savedSettings = (apiBaseUrl: string) => ({
  apiBaseUrl,
  videoSourceSubscriptionUrl: "",
  subscribedVideoSources: [],
  autoSkipAdsEnabled: true,
  remoteInputEnabled: false,
  videoSource: { enabledAll: true, sources: {} },
  m3uUrl: "",
});

describe("RootLayout login check", () => {
  let tree: renderer.ReactTestRenderer;

  const startApp = async (savedApiBaseUrl: string) => {
    (SettingsManager.get as jest.Mock).mockResolvedValue(savedSettings(savedApiBaseUrl));
    await act(async () => {
      tree = renderer.create(<RootLayout />);
    });
  };

  // What APIConfigSection does on every change of its text input: the draft goes straight into the store.
  const typeApiAddress = (from: string, to: string) => {
    for (let length = from.length + 1; length <= to.length; length++) {
      act(() => useSettingsStore.getState().setApiBaseUrl(to.slice(0, length)));
    }
  };

  const save = () =>
    act(async () => {
      await useSettingsStore.getState().saveSettings();
    });

  beforeEach(() => {
    jest.clearAllMocks();
    jest.spyOn(api, "getServerConfig").mockResolvedValue({ SiteName: "test", StorageType: "redis" });
    api.setBaseUrl("");
    useSettingsStore.setState({
      apiBaseUrl: "",
      videoSourceSubscriptionUrl: "",
      subscribedVideoSources: [],
      serverConfig: null,
      isLoadingServerConfig: false,
    });
  });

  afterEach(() => {
    act(() => tree.unmount());
    jest.restoreAllMocks();
  });

  it("checks the login status once the saved API address has been loaded", async () => {
    await startApp("http://server.example");

    expect(mockCheckLoginStatus).toHaveBeenCalledTimes(1);
    expect(mockCheckLoginStatus).toHaveBeenCalledWith("http://server.example");
  });

  it("does not check the login status for every character typed as the first API address", async () => {
    await startApp("");

    typeApiAddress("", "http://server.example");

    // Nothing is saved yet: there is no server to be logged in to.
    expect(mockCheckLoginStatus).not.toHaveBeenCalled();
  });

  it("does not check the login status for every character typed while correcting the saved API address", async () => {
    await startApp("http://server.example");
    mockCheckLoginStatus.mockClear();

    typeApiAddress("http://server.example", "http://server.example:3000");

    // Requests still go to the saved address until the new one is saved.
    expect(mockCheckLoginStatus).not.toHaveBeenCalled();
  });

  it("checks the login status again once the typed API address is saved", async () => {
    await startApp("");
    typeApiAddress("", "server.example:3000");

    await save();

    expect(mockCheckLoginStatus).toHaveBeenCalledTimes(1);
    expect(mockCheckLoginStatus).toHaveBeenCalledWith("http://server.example:3000");
  });
});
