import React from "react";
import renderer, { act } from "react-test-renderer";
import { InteractionManager, TextInput } from "react-native";
import Toast from "react-native-toast-message";
import LoginModal from "../LoginModal";

const mockLogin = jest.fn();
const mockCheckLoginStatus = jest.fn();
const mockRefreshPlayRecords = jest.fn();
const mockSaveCredentials = jest.fn();

jest.mock("expo-router", () => ({
  usePathname: () => "/",
}));

jest.mock("react-native-toast-message", () => ({
  __esModule: true,
  default: { show: jest.fn() },
}));

jest.mock("@/services/api", () => ({
  api: { login: (...args: unknown[]) => mockLogin(...args) },
}));

jest.mock("@/services/storage", () => ({
  LoginCredentialsManager: {
    get: jest.fn().mockResolvedValue(null),
    save: (...args: unknown[]) => mockSaveCredentials(...args),
  },
}));

jest.mock("@/stores/authStore", () => ({
  __esModule: true,
  default: () => ({
    isLoginModalVisible: true,
    hideLoginModal: jest.fn(),
    checkLoginStatus: mockCheckLoginStatus,
  }),
}));

jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: () => ({ serverConfig: { StorageType: "redis" }, apiBaseUrl: "http://server" }),
}));

jest.mock("@/stores/homeStore", () => ({
  __esModule: true,
  default: () => ({ refreshPlayRecords: mockRefreshPlayRecords }),
}));

jest.mock("../StyledButton", () => {
  const { Text } = require("react-native");
  return {
    StyledButton: ({ text, onPress }: { text: string; onPress: () => void }) => <Text onPress={onPress}>{text}</Text>,
  };
});

describe("LoginModal", () => {
  let tree: renderer.ReactTestRenderer | undefined;

  // Unmount even when an assertion fails, so the modal's focus timers do not outlive the test.
  afterEach(async () => {
    await act(async () => {
      tree?.unmount();
    });
    tree = undefined;
  });

  beforeEach(() => {
    jest.clearAllMocks();
    // The success path closes the modal and shows the disclaimer from here; keep its timers out of the test.
    jest.spyOn(InteractionManager, "runAfterInteractions").mockImplementation(() => ({}) as any);
    mockLogin.mockResolvedValue({ ok: true });
    mockCheckLoginStatus.mockResolvedValue(undefined);
    mockSaveCredentials.mockResolvedValue(undefined);
  });

  it("does not report a failed login when only the play-record refresh fails", async () => {
    mockRefreshPlayRecords.mockRejectedValue(new Error("HTTP error! status: 500"));

    await act(async () => {
      tree = renderer.create(<LoginModal />);
    });

    const [usernameInput, passwordInput] = tree!.root.findAllByType(TextInput);
    await act(async () => {
      usernameInput.props.onChangeText("alice");
      passwordInput.props.onChangeText("secret");
    });
    await act(async () => {
      await tree!.root.findAllByType(TextInput)[1].props.onSubmitEditing();
    });

    expect(mockLogin).toHaveBeenCalledWith("alice", "secret");
    expect(mockSaveCredentials).toHaveBeenCalledWith({ username: "alice", password: "secret" });
    expect(Toast.show).toHaveBeenCalledWith(expect.objectContaining({ type: "success", text1: "登录成功" }));
    expect(Toast.show).not.toHaveBeenCalledWith(expect.objectContaining({ text1: "登录失败" }));
    expect(InteractionManager.runAfterInteractions).toHaveBeenCalledTimes(1);
  });

  it("still reports a failed login when the login request itself fails", async () => {
    mockLogin.mockRejectedValue(new Error("UNAUTHORIZED"));
    mockRefreshPlayRecords.mockResolvedValue(undefined);

    await act(async () => {
      tree = renderer.create(<LoginModal />);
    });

    const [usernameInput, passwordInput] = tree!.root.findAllByType(TextInput);
    await act(async () => {
      usernameInput.props.onChangeText("alice");
      passwordInput.props.onChangeText("wrong");
    });
    await act(async () => {
      await tree!.root.findAllByType(TextInput)[1].props.onSubmitEditing();
    });

    expect(mockSaveCredentials).not.toHaveBeenCalled();
    expect(Toast.show).toHaveBeenCalledWith(expect.objectContaining({ type: "error", text1: "登录失败" }));
    expect(InteractionManager.runAfterInteractions).not.toHaveBeenCalled();
  });
});
