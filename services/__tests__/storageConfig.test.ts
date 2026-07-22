import AsyncStorage from "@react-native-async-storage/async-storage";
import { api } from "../api";
import { PlayRecordManager } from "../storage";
import { storageConfig } from "../storageConfig";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

describe("storageConfig default behavior", () => {
  beforeEach(() => {
    jest.restoreAllMocks();
    storageConfig.setStorageType(undefined);
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => {
      if (key === "mytv_play_records") {
        return JSON.stringify({
          "demo+7": {
            title: "Movie",
            source_name: "Demo",
            cover: "",
            index: 1,
            total_episodes: 1,
            play_time: 12,
            total_time: 120,
            save_time: 1,
            year: "2026",
          },
        });
      }
      if (key === "mytv_player_settings") {
        return "{}";
      }
      return null;
    });
    (AsyncStorage.setItem as jest.Mock).mockResolvedValue(undefined);
  });

  it("uses local play records when no backend storage type is configured", async () => {
    const getPlayRecords = jest.spyOn(api, "getPlayRecords").mockRejectedValue(new Error("backend should not be used"));

    const record = await PlayRecordManager.get("demo", "7");

    expect(record?.title).toBe("Movie");
    expect(getPlayRecords).not.toHaveBeenCalled();
  });

  it("saves play records locally when no backend storage type is configured", async () => {
    const savePlayRecord = jest.spyOn(api, "savePlayRecord").mockRejectedValue(new Error("backend should not be used"));

    await PlayRecordManager.save("demo", "8", {
      title: "Another Movie",
      source_name: "Demo",
      cover: "",
      index: 1,
      total_episodes: 1,
      play_time: 5,
      total_time: 100,
      year: "2026",
    });

    expect(savePlayRecord).not.toHaveBeenCalled();
    expect(AsyncStorage.setItem).toHaveBeenCalledWith(
      "mytv_play_records",
      expect.stringContaining("Another Movie")
    );
  });
});
