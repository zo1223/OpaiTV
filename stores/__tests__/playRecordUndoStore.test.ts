import useHomeStore from "../homeStore";
import useAuthStore from "../authStore";
import { useSettingsStore } from "../settingsStore";
import {
  PLAY_RECORD_UNDO_WINDOW_MS,
  usePlayRecordUndoStore,
  resetPlayRecordUndoStoreForTests,
} from "../playRecordUndoStore";
import { PlayRecordManager } from "@/services/storage";
import AsyncStorage from "@react-native-async-storage/async-storage";
import Toast from "react-native-toast-message";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));

const record = (title: string, saveTime: number) => ({
  title,
  source_name: title,
  cover: "",
  index: 1,
  total_episodes: 2,
  play_time: 12,
  total_time: 120,
  save_time: saveTime,
  year: "2026",
});

describe("continue-watching delete with undo", () => {
  const initialCategories = useHomeStore.getState().categories;
  const recordCategory = initialCategories.find((category) => category.type === "record")!;
  let stored: Record<string, string>;

  const storedRecordKeys = () => Object.keys(JSON.parse(stored.mytv_play_records)).sort();
  const shownTitles = () => useHomeStore.getState().contentData.map((item) => item.title);
  const undoStore = () => usePlayRecordUndoStore.getState();
  // 让已经排队的异步工作(存储写入、列表刷新)跑完
  const settle = async () => {
    for (let i = 0; i < 20; i++) {
      await Promise.resolve();
    }
    await undoStore().flushCommitted();
    for (let i = 0; i < 20; i++) {
      await Promise.resolve();
    }
  };

  beforeEach(async () => {
    jest.useFakeTimers();
    jest.restoreAllMocks();
    jest.clearAllMocks();
    resetPlayRecordUndoStoreForTests();
    stored = {
      mytv_play_records: JSON.stringify({
        "src+1": record("First", 10),
        "src+2": record("Second", 20),
        "src+3": record("Third", 30),
      }),
    };
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => stored[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      stored[key] = value;
    });
    useAuthStore.setState({ isLoggedIn: false, isLoginModalVisible: false });
    useSettingsStore.setState({ apiBaseUrl: "" });
    useHomeStore.setState({
      categories: initialCategories,
      selectedCategory: recordCategory,
      contentData: [],
      loading: false,
      loadingMore: false,
      pageStart: 0,
      hasMore: true,
      error: null,
    });
    await useHomeStore.getState().refreshPlayRecords();
  });

  afterEach(() => {
    resetPlayRecordUndoStoreForTests();
    jest.useRealTimers();
  });

  it("uses a 5 second undo window", () => {
    expect(PLAY_RECORD_UNDO_WINDOW_MS).toBe(5000);
  });

  it("hides the record at once but leaves storage alone during the window", async () => {
    expect(shownTitles()).toEqual(["Third", "Second", "First"]);

    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });

    expect(shownTitles()).toEqual(["Third", "First"]);
    expect(undoStore().pending).toMatchObject({ key: "src+2", title: "Second" });
    expect(storedRecordKeys()).toEqual(["src+1", "src+2", "src+3"]);

    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS - 1);
    await settle();
    expect(storedRecordKeys()).toEqual(["src+1", "src+2", "src+3"]);
    expect(undoStore().pending).not.toBeNull();
  });

  it("does not bring the pending record back when the list is fetched again", async () => {
    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });

    await useHomeStore.getState().refreshPlayRecords();
    expect(shownTitles()).toEqual(["Third", "First"]);

    await useHomeStore.getState().fetchInitialData();
    expect(shownTitles()).toEqual(["Third", "First"]);
  });

  it("restores the record when undone inside the window, without ever touching storage", async () => {
    const remove = jest.spyOn(PlayRecordManager, "remove");
    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });
    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS - 1);

    expect(undoStore().undo()).toBe(true);
    await settle();

    expect(undoStore().pending).toBeNull();
    expect(shownTitles()).toEqual(["Third", "Second", "First"]);

    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS * 2);
    await settle();
    expect(remove).not.toHaveBeenCalled();
    expect(AsyncStorage.setItem).not.toHaveBeenCalledWith("mytv_play_records", expect.anything());
    expect(storedRecordKeys()).toEqual(["src+1", "src+2", "src+3"]);
  });

  it("really deletes once the window has passed, and undo is no longer possible", async () => {
    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });

    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS);
    await settle();

    expect(storedRecordKeys()).toEqual(["src+1", "src+3"]);
    expect(undoStore().pending).toBeNull();
    expect(undoStore().undo()).toBe(false);
    await useHomeStore.getState().refreshPlayRecords();
    expect(shownTitles()).toEqual(["Third", "First"]);
  });

  it("keeps only one undoable delete: a second delete commits the first immediately", async () => {
    undoStore().requestDelete({ source: "src", id: "1", title: "First" });
    jest.advanceTimersByTime(1000);
    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });
    await settle();

    expect(storedRecordKeys()).toEqual(["src+2", "src+3"]);
    expect(undoStore().pending).toMatchObject({ key: "src+2" });
    expect(shownTitles()).toEqual(["Third"]);

    // 第二条的窗口从它自己被删的时刻算起
    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS - 1);
    await settle();
    expect(storedRecordKeys()).toEqual(["src+2", "src+3"]);
    expect(undoStore().undo()).toBe(true);
    await settle();
    expect(shownTitles()).toEqual(["Third", "Second"]);
  });

  it("flush commits the pending delete right away", async () => {
    undoStore().requestDelete({ source: "src", id: "3", title: "Third" });

    await undoStore().flush();

    expect(storedRecordKeys()).toEqual(["src+1", "src+2"]);
    expect(undoStore().pending).toBeNull();
    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS * 2);
    await settle();
    expect(storedRecordKeys()).toEqual(["src+1", "src+2"]);
  });

  it("keeps a record that was played again while the delete was pending", async () => {
    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });
    jest.advanceTimersByTime(1000);
    const records = JSON.parse(stored.mytv_play_records);
    records["src+2"] = record("Second", Date.now());
    stored.mytv_play_records = JSON.stringify(records);

    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS);
    await settle();

    expect(storedRecordKeys()).toEqual(["src+1", "src+2", "src+3"]);
    expect(undoStore().pending).toBeNull();
    expect(shownTitles()).toEqual(["Second", "Third", "First"]);
  });

  it("shows the record again and reports it when the delete fails", async () => {
    jest.spyOn(PlayRecordManager, "remove").mockRejectedValueOnce(new Error("offline"));
    undoStore().requestDelete({ source: "src", id: "2", title: "Second" });

    jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS);
    await settle();

    expect(Toast.show).toHaveBeenCalledWith(expect.objectContaining({ type: "error" }));
    expect(storedRecordKeys()).toEqual(["src+1", "src+2", "src+3"]);
    expect(shownTitles()).toEqual(["Third", "Second", "First"]);
  });

  it("drops the continue-watching category when the last record is deleted, and brings it back on undo", async () => {
    stored.mytv_play_records = JSON.stringify({ "src+1": record("Only", 10) });
    await useHomeStore.getState().refreshPlayRecords();
    expect(shownTitles()).toEqual(["Only"]);

    undoStore().requestDelete({ source: "src", id: "1", title: "Only" });
    await settle();
    expect(useHomeStore.getState().categories.some((category) => category.type === "record")).toBe(false);

    undoStore().undo();
    await settle();
    expect(useHomeStore.getState().categories.some((category) => category.type === "record")).toBe(true);
    expect(storedRecordKeys()).toEqual(["src+1"]);
  });
});
