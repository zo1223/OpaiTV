import AsyncStorage from "@react-native-async-storage/async-storage";
import updateService from "@/services/updateService";
import { useUpdateStore, initUpdateStore, shouldShowUpdateBadge } from "../updateStore";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));
jest.mock("expo-file-system", () => ({ __esModule: true, documentDirectory: "file:///data/files/" }));
jest.mock("expo-intent-launcher", () => ({}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));

const current = updateService.getCurrentVersion();
const [major, minor, patch] = current.split(".").map(Number);
const newer = `${major}.${minor}.${patch + 1}`;

describe("update badge state", () => {
  let stored: Record<string, string>;
  const badge = () => shouldShowUpdateBadge(useUpdateStore.getState());
  const remoteIs = (version: string) =>
    jest.spyOn(updateService, "checkVersion").mockResolvedValue({ version, downloadUrl: `https://dl.example/${version}.apk` });

  beforeEach(() => {
    jest.restoreAllMocks();
    jest.clearAllMocks();
    stored = {};
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => stored[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      stored[key] = value;
    });
    useUpdateStore.setState({
      updateAvailable: false,
      remoteVersion: "",
      downloadUrl: "",
      skipVersion: null,
      showUpdateModal: false,
      isLatestVersion: false,
      error: null,
      lastCheckTime: 0,
    });
  });

  it("the test assumes the app is still on 1.3.27", () => {
    expect(current).toBe("1.3.27");
    expect(newer).toBe("1.3.28");
  });

  it("is a pure function of updateAvailable, remoteVersion and skipVersion", () => {
    expect(shouldShowUpdateBadge({ updateAvailable: true, remoteVersion: "1.3.28", skipVersion: null })).toBe(true);
    expect(shouldShowUpdateBadge({ updateAvailable: true, remoteVersion: "1.3.28", skipVersion: "1.3.27" })).toBe(true);
    expect(shouldShowUpdateBadge({ updateAvailable: true, remoteVersion: "1.3.28", skipVersion: "1.3.28" })).toBe(false);
    expect(shouldShowUpdateBadge({ updateAvailable: false, remoteVersion: "1.3.28", skipVersion: null })).toBe(false);
    expect(shouldShowUpdateBadge({ updateAvailable: true, remoteVersion: "", skipVersion: null })).toBe(false);
  });

  it("a silent check that finds a newer version turns the badge on without opening the dialog", async () => {
    remoteIs(newer);

    await useUpdateStore.getState().checkForUpdate(true);

    expect(useUpdateStore.getState().updateAvailable).toBe(true);
    expect(useUpdateStore.getState().showUpdateModal).toBe(false);
    expect(badge()).toBe(true);
  });

  it.each([["the same version", current], ["an older version", "1.3.9"]])("no badge when the remote is %s", async (_name, version) => {
    remoteIs(version);

    await useUpdateStore.getState().checkForUpdate(true);

    expect(useUpdateStore.getState().updateAvailable).toBe(false);
    expect(badge()).toBe(false);
  });

  it("no badge when the check fails", async () => {
    jest.spyOn(updateService, "checkVersion").mockRejectedValue(new Error("network down"));

    await useUpdateStore.getState().checkForUpdate(true);

    expect(badge()).toBe(false);
  });

  it("skipping the version clears the badge, and a later check keeps it cleared", async () => {
    remoteIs(newer);
    await useUpdateStore.getState().checkForUpdate(true);
    expect(badge()).toBe(true);

    await useUpdateStore.getState().skipThisVersion();
    expect(badge()).toBe(false);

    await useUpdateStore.getState().checkForUpdate(true);
    expect(useUpdateStore.getState().updateAvailable).toBe(true);
    expect(badge()).toBe(false);
  });

  it("remembers the newer version across a restart, until the app itself is updated", async () => {
    remoteIs(newer);
    await useUpdateStore.getState().checkForUpdate(true);
    expect(stored.update_remote_version).toBe(newer);

    // 重启:内存状态清空,只剩 AsyncStorage
    useUpdateStore.setState({ updateAvailable: false, remoteVersion: "", downloadUrl: "" });
    await initUpdateStore();
    expect(badge()).toBe(true);
    expect(useUpdateStore.getState().remoteVersion).toBe(newer);
    expect(useUpdateStore.getState().downloadUrl).toContain(newer);

    // 应用升级到了那个版本之后:记住的版本不再比当前新
    useUpdateStore.setState({ updateAvailable: false, remoteVersion: "", downloadUrl: "" });
    stored.update_remote_version = current;
    await initUpdateStore();
    expect(badge()).toBe(false);
  });
});
