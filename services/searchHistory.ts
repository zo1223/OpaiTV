/**
 * 搜索记录（标签云用）。只存在本机 AsyncStorage，不上传。
 *
 * 与 services/storage.ts 里按服务端模式同步的 SearchHistoryManager 相互独立：
 * 这里额外记录搜索次数与最近使用时间，用来算标签云的权重。
 */
import AsyncStorage from "@react-native-async-storage/async-storage";

export interface SearchHistoryEntry {
  query: string;
  count: number;
  lastUsedAt: number;
}

export interface SearchTag extends SearchHistoryEntry {
  /** 0 ~ 1，越大越醒目 */
  weight: number;
  /** 建议字号（12 ~ 20） */
  fontSize: number;
}

export const SEARCH_HISTORY_KEY = "mytv_search_tag_cloud";
export const MAX_SEARCH_HISTORY = 30;

const MIN_FONT_SIZE = 12;
const MAX_FONT_SIZE = 20;
const COUNT_SHARE = 0.6;
const RECENCY_SHARE = 0.4;

const normalizeQuery = (query: string): string => String(query ?? "").trim().replace(/\s+/g, " ");
const sameQuery = (a: string, b: string): boolean => a.toLowerCase() === b.toLowerCase();

function sanitize(raw: unknown): SearchHistoryEntry[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const entries: SearchHistoryEntry[] = [];
  for (const row of raw) {
    const query = normalizeQuery(typeof row === "string" ? row : (row as SearchHistoryEntry | null)?.query ?? "");
    if (!query || entries.some((entry) => sameQuery(entry.query, query))) {
      continue;
    }
    const count = Number((row as SearchHistoryEntry | null)?.count);
    const lastUsedAt = Number((row as SearchHistoryEntry | null)?.lastUsedAt);
    entries.push({
      query,
      count: Number.isFinite(count) && count >= 1 ? Math.floor(count) : 1,
      lastUsedAt: Number.isFinite(lastUsedAt) && lastUsedAt > 0 ? lastUsedAt : 0,
    });
  }
  return entries.sort((a, b) => b.lastUsedAt - a.lastUsedAt).slice(0, MAX_SEARCH_HISTORY);
}

/** 纯函数：把一次搜索记进列表（去空白、同词累加次数、最近的排最前、最多保留 30 条）。 */
export function upsertSearchEntry(
  entries: SearchHistoryEntry[],
  query: string,
  now: number = Date.now()
): SearchHistoryEntry[] {
  const normalized = normalizeQuery(query);
  if (!normalized) {
    return entries;
  }
  const existing = entries.find((entry) => sameQuery(entry.query, normalized));
  const rest = entries.filter((entry) => !sameQuery(entry.query, normalized));
  return [{ query: normalized, count: (existing?.count ?? 0) + 1, lastUsedAt: now }, ...rest].slice(
    0,
    MAX_SEARCH_HISTORY
  );
}

/** 纯函数：删掉一条记录。 */
export function withoutSearchEntry(entries: SearchHistoryEntry[], query: string): SearchHistoryEntry[] {
  const normalized = normalizeQuery(query);
  return entries.filter((entry) => !sameQuery(entry.query, normalized));
}

export async function loadSearchHistory(): Promise<SearchHistoryEntry[]> {
  try {
    const data = await AsyncStorage.getItem(SEARCH_HISTORY_KEY);
    return data ? sanitize(JSON.parse(data)) : [];
  } catch {
    return [];
  }
}

async function save(entries: SearchHistoryEntry[]): Promise<void> {
  await AsyncStorage.setItem(SEARCH_HISTORY_KEY, JSON.stringify(entries));
}

// 写操作排队串行执行：连续的「删一条」「清空」「再搜一次」各自都要先读后写，
// 不排队的话先发起的写入可能晚于后发起的清空落盘，把已清空的记录又写回去。
let writeQueue: Promise<unknown> = Promise.resolve();

function enqueue<T>(task: () => Promise<T>): Promise<T> {
  const run = writeQueue.then(task, task);
  writeQueue = run.catch(() => undefined);
  return run;
}

export function recordSearch(query: string, now: number = Date.now()): Promise<SearchHistoryEntry[]> {
  return enqueue(async () => {
    const current = await loadSearchHistory();
    const next = upsertSearchEntry(current, query, now);
    if (next !== current) {
      await save(next);
    }
    return next;
  });
}

export function removeSearch(query: string): Promise<SearchHistoryEntry[]> {
  return enqueue(async () => {
    const current = await loadSearchHistory();
    const next = withoutSearchEntry(current, query);
    if (next.length !== current.length) {
      await save(next);
    }
    return next;
  });
}

export function clearSearchHistory(): Promise<void> {
  return enqueue(() => AsyncStorage.removeItem(SEARCH_HISTORY_KEY));
}

/**
 * 纯函数：算标签云权重。搜得越多、用得越近的词权重越高（次数占 60%，新近度占 40%），
 * 返回顺序与传入顺序一致。
 */
export function toTagCloud(entries: SearchHistoryEntry[]): SearchTag[] {
  if (entries.length === 0) {
    return [];
  }
  const maxCount = Math.max(...entries.map((entry) => entry.count), 1);
  const times = entries.map((entry) => entry.lastUsedAt);
  const newest = Math.max(...times);
  const oldest = Math.min(...times);
  const span = newest - oldest;

  return entries.map((entry) => {
    const countScore = maxCount <= 1 ? 1 : (entry.count - 1) / (maxCount - 1);
    const recencyScore = span <= 0 ? 1 : (entry.lastUsedAt - oldest) / span;
    const weight = Math.min(1, Math.max(0, COUNT_SHARE * countScore + RECENCY_SHARE * recencyScore));
    return {
      ...entry,
      weight,
      fontSize: Math.round(MIN_FONT_SIZE + (MAX_FONT_SIZE - MIN_FONT_SIZE) * weight),
    };
  });
}
