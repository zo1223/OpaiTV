import React from "react";
import renderer, { act } from "react-test-renderer";
import { useApiConfig, ApiConfigStatus } from "../useApiConfig";
import { api } from "@/services/api";

let mockSettingsState: Record<string, unknown> = {};

jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: () => mockSettingsState,
}));

jest.mock("@/services/api", () => ({
  api: { getServerConfig: jest.fn() },
}));

const settings = (apiBaseUrl: string, isLoadingServerConfig = false) => ({
  apiBaseUrl,
  videoSourceSubscriptionUrl: "",
  subscribedVideoSources: [],
  serverConfig: { StorageType: "localstorage" },
  isLoadingServerConfig,
});

let status: ApiConfigStatus;
const Harness = () => {
  status = useApiConfig();
  return null;
};

describe("useApiConfig", () => {
  it("ignores a validation result that arrives after the server address changed", async () => {
    let rejectOld: (error: Error) => void = () => {};
    (api.getServerConfig as jest.Mock)
      .mockImplementationOnce(() => new Promise((_, reject) => (rejectOld = reject)))
      .mockResolvedValueOnce({});

    mockSettingsState = settings("http://old.example");
    let tree: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<Harness />);
    });

    // The user corrects the address; the new one validates straight away
    mockSettingsState = settings("http://new.example");
    await act(async () => {
      tree.update(<Harness />);
    });
    expect(status.isValid).toBe(true);

    // The request against the old address only fails now
    await act(async () => {
      rejectOld(new Error("Network request failed"));
    });

    expect(api.getServerConfig).toHaveBeenCalledTimes(2);
    expect(status.isValid).toBe(true);
    expect(status.error).toBeNull();
    expect(status.isValidating).toBe(false);
  });

  it("reports a failed validation of the current address", async () => {
    (api.getServerConfig as jest.Mock).mockRejectedValueOnce(new Error("Network request failed"));

    mockSettingsState = settings("http://bad.example");
    await act(async () => {
      renderer.create(<Harness />);
    });

    expect(status.isValid).toBe(false);
    expect(status.error).toBe("网络连接失败，请检查网络或服务器地址");
  });
});
