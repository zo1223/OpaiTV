import AsyncStorage from "@react-native-async-storage/async-storage";
import {
  MAX_SEARCH_HISTORY,
  SEARCH_HISTORY_KEY,
  clearSearchHistory,
  loadSearchHistory,
  recordSearch,
  removeSearch,
  toTagCloud,
  upsertSearchEntry,
  withoutSearchEntry,
} from "../searchHistory";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

describe("searchHistory", () => {
  let store: Record<string, string>;

  const stored = () => JSON.parse(store[SEARCH_HISTORY_KEY] ?? "[]");

  beforeEach(() => {
    jest.clearAllMocks();
    store = {};
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => store[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      store[key] = value;
    });
    (AsyncStorage.removeItem as jest.Mock).mockImplementation(async (key: string) => {
      delete store[key];
    });
  });

  it("records a trimmed query once and counts repeats", async () => {
    await recordSearch("  流浪  地球 ", 1000);
    const list = await recordSearch("流浪 地球", 2000);

    expect(list).toEqual([{ query: "流浪 地球", count: 2, lastUsedAt: 2000 }]);
    expect(stored()).toEqual(list);
  });

  it("treats queries that differ only by case as the same entry", async () => {
    await recordSearch("Matrix", 1000);
    const list = await recordSearch("matrix", 2000);

    expect(list).toEqual([{ query: "matrix", count: 2, lastUsedAt: 2000 }]);
  });

  it("ignores blank queries", async () => {
    const list = await recordSearch("   ", 1000);

    expect(list).toEqual([]);
    expect(AsyncStorage.setItem).not.toHaveBeenCalled();
  });

  it("puts the most recent search first and keeps at most 30 entries", async () => {
    for (let index = 0; index < MAX_SEARCH_HISTORY + 5; index += 1) {
      await recordSearch(`q${index}`, 1000 + index);
    }
    const list = await loadSearchHistory();

    expect(list).toHaveLength(MAX_SEARCH_HISTORY);
    expect(list[0].query).toBe(`q${MAX_SEARCH_HISTORY + 4}`);
    expect(list.some((entry) => entry.query === "q0")).toBe(false);
  });

  it("removes a single entry and leaves the rest", async () => {
    await recordSearch("a", 1000);
    await recordSearch("b", 2000);

    const list = await removeSearch(" A ");

    expect(list.map((entry) => entry.query)).toEqual(["b"]);
    expect(stored().map((entry: { query: string }) => entry.query)).toEqual(["b"]);
  });

  it("clears everything", async () => {
    await recordSearch("a", 1000);
    await clearSearchHistory();

    expect(await loadSearchHistory()).toEqual([]);
    expect(store[SEARCH_HISTORY_KEY]).toBeUndefined();
  });

  it("applies writes in call order, so a clear issued right after a removal wins", async () => {
    await recordSearch("a", 1000);
    await recordSearch("b", 2000);

    const removing = removeSearch("a");
    const clearing = clearSearchHistory();
    const recording = recordSearch("c", 3000);
    await Promise.all([removing, clearing, recording]);

    expect(stored()).toEqual([{ query: "c", count: 1, lastUsedAt: 3000 }]);
  });

  it("recovers from corrupted or legacy stored data", async () => {
    store[SEARCH_HISTORY_KEY] = "{not json";
    expect(await loadSearchHistory()).toEqual([]);

    store[SEARCH_HISTORY_KEY] = JSON.stringify(["旧记录", { query: " 新记录 ", count: 3, lastUsedAt: 50 }, null, 7]);
    expect(await loadSearchHistory()).toEqual([
      { query: "新记录", count: 3, lastUsedAt: 50 },
      { query: "旧记录", count: 1, lastUsedAt: 0 },
    ]);
  });

  it("exposes pure helpers that do not mutate their input", () => {
    const entries = [{ query: "a", count: 1, lastUsedAt: 1 }];

    expect(upsertSearchEntry(entries, "b", 5)).toEqual([
      { query: "b", count: 1, lastUsedAt: 5 },
      { query: "a", count: 1, lastUsedAt: 1 },
    ]);
    expect(upsertSearchEntry(entries, "  ", 5)).toBe(entries);
    expect(withoutSearchEntry(entries, "a")).toEqual([]);
    expect(entries).toEqual([{ query: "a", count: 1, lastUsedAt: 1 }]);
  });
});

describe("toTagCloud", () => {
  it("returns nothing for an empty history", () => {
    expect(toTagCloud([])).toEqual([]);
  });

  it("gives a single entry full weight", () => {
    expect(toTagCloud([{ query: "a", count: 1, lastUsedAt: 10 }])).toEqual([
      { query: "a", count: 1, lastUsedAt: 10, weight: 1, fontSize: 20 },
    ]);
  });

  it("weights frequent and recent queries higher, within 0~1 and 12~20", () => {
    const cloud = toTagCloud([
      { query: "hot", count: 5, lastUsedAt: 3000 },
      { query: "recent-once", count: 1, lastUsedAt: 3000 },
      { query: "old-often", count: 5, lastUsedAt: 1000 },
      { query: "cold", count: 1, lastUsedAt: 1000 },
    ]);
    const byQuery = Object.fromEntries(cloud.map((tag) => [tag.query, tag]));

    expect(cloud.map((tag) => tag.query)).toEqual(["hot", "recent-once", "old-often", "cold"]);
    expect(byQuery.hot.weight).toBe(1);
    expect(byQuery.hot.fontSize).toBe(20);
    expect(byQuery.cold.weight).toBe(0);
    expect(byQuery.cold.fontSize).toBe(12);
    expect(byQuery["old-often"].weight).toBeGreaterThan(byQuery["recent-once"].weight);
    for (const tag of cloud) {
      expect(tag.weight).toBeGreaterThanOrEqual(0);
      expect(tag.weight).toBeLessThanOrEqual(1);
      expect(tag.fontSize).toBeGreaterThanOrEqual(12);
      expect(tag.fontSize).toBeLessThanOrEqual(20);
    }
  });
});
