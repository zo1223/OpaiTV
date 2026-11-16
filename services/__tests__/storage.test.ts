import AsyncStorage from "@react-native-async-storage/async-storage";
import { PlayRecordManager } from "../storage";
import { storageConfig } from "../storageConfig";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

const PLAY_RECORDS_KEY = "mytv_play_records";

const baseRecord = {
  title: "Series",
  source_name: "Demo",
  cover: "",
  index: 1,
  total_episodes: 12,
  play_time: 30,
  total_time: 1200,
  year: "2026",
};

describe("PlayRecordManager local storage", () => {
  let store: Record<string, string>;

  const storedRecords = () => JSON.parse(store[PLAY_RECORDS_KEY] ?? "{}");

  beforeEach(() => {
    store = {};
    storageConfig.setStorageType(undefined);
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => store[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      store[key] = value;
    });
    (AsyncStorage.removeItem as jest.Mock).mockImplementation(async (key: string) => {
      delete store[key];
    });
  });

  it("does not bring back a cleared intro/outro time on later saves", async () => {
    await PlayRecordManager.save("demo", "1", { ...baseRecord, introEndTime: 90000, outroStartTime: 60000 });
    // A later progress save while the markers are still set
    await PlayRecordManager.save("demo", "1", {
      ...baseRecord,
      play_time: 60,
      introEndTime: 90000,
      outroStartTime: 60000,
    });
    expect((await PlayRecordManager.get("demo", "1"))?.introEndTime).toBe(90000);

    // The user clears both markers
    await PlayRecordManager.save("demo", "1", {
      ...baseRecord,
      play_time: 90,
      introEndTime: undefined,
      outroStartTime: undefined,
    });

    const record = await PlayRecordManager.get("demo", "1");
    expect(record?.play_time).toBe(90);
    expect(record?.introEndTime).toBeUndefined();
    expect(record?.outroStartTime).toBeUndefined();
  });

  it("drops intro/outro times already persisted inside a stored play record", async () => {
    store[PLAY_RECORDS_KEY] = JSON.stringify({
      "demo+1": { ...baseRecord, save_time: 1, introEndTime: 90000, outroStartTime: 60000 },
    });

    await PlayRecordManager.save("demo", "1", { ...baseRecord, introEndTime: undefined, outroStartTime: undefined });

    const record = await PlayRecordManager.get("demo", "1");
    expect(record?.introEndTime).toBeUndefined();
    expect(record?.outroStartTime).toBeUndefined();
  });

  it("keeps player settings out of other stored records when saving or removing", async () => {
    await PlayRecordManager.save("demo", "1", { ...baseRecord, introEndTime: 90000 });
    await PlayRecordManager.save("demo", "2", { ...baseRecord, title: "Other" });
    await PlayRecordManager.save("demo", "3", { ...baseRecord, title: "Third" });
    await PlayRecordManager.remove("demo", "3");

    const stored = storedRecords();
    expect(Object.keys(stored).sort()).toEqual(["demo+1", "demo+2"]);
    expect(stored["demo+1"]).not.toHaveProperty("introEndTime");
    // The merged read view still exposes the setting
    expect((await PlayRecordManager.get("demo", "1"))?.introEndTime).toBe(90000);
  });
});
