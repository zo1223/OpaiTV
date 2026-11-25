import AsyncStorage from "@react-native-async-storage/async-storage";
import type { RecommendationCandidate } from "./recommendation";

/**
 * 推荐候选池:首页已经加载过的条目,只存在本机(AsyncStorage),用来在不联网的前提下做“为你推荐”。
 * 这里不发任何网络请求。
 */
const STORAGE_KEY = "mytv_recommendation_pool";
export const RECOMMENDATION_POOL_LIMIT = 300;

let memoryPool: RecommendationCandidate[] | null = null;

const candidateKey = (item: RecommendationCandidate) => `${item.source}+${item.id}`;

export async function loadRecommendationPool(): Promise<RecommendationCandidate[]> {
  if (memoryPool) {
    return memoryPool;
  }
  try {
    const raw = await AsyncStorage.getItem(STORAGE_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    memoryPool = Array.isArray(parsed)
      ? parsed.filter((item) => item && typeof item.title === "string" && typeof item.source === "string" && item.id != null)
      : [];
  } catch {
    memoryPool = [];
  }
  return memoryPool;
}

/** 把首页刚加载的条目并入候选池(新的在前,同一条目合并标签),超出上限丢最旧的。 */
export async function rememberRecommendationCandidates(
  items: RecommendationCandidate[],
  tags: (string | undefined)[] = []
): Promise<void> {
  const cleanTags = tags.filter((tag): tag is string => typeof tag === "string" && tag.trim().length > 0);
  const incoming = (items ?? [])
    .filter((item) => item && item.title && item.source && item.id != null)
    .map((item) => ({
      id: String(item.id),
      source: item.source,
      title: item.title,
      poster: item.poster ?? "",
      year: item.year,
      rate: item.rate,
      source_name: item.source_name,
      type_name: item.type_name,
      class: item.class,
      tags: cleanTags,
    }));
  if (incoming.length === 0) {
    return;
  }
  const current = await loadRecommendationPool();
  const byKey = new Map(current.map((item) => [candidateKey(item), item]));
  const merged: RecommendationCandidate[] = incoming.map((item) => {
    const previous = byKey.get(candidateKey(item));
    byKey.delete(candidateKey(item));
    return previous ? { ...item, tags: Array.from(new Set([...(item.tags ?? []), ...(previous.tags ?? [])])) } : item;
  });
  memoryPool = [...merged, ...byKey.values()].slice(0, RECOMMENDATION_POOL_LIMIT);
  try {
    await AsyncStorage.setItem(STORAGE_KEY, JSON.stringify(memoryPool));
  } catch {
    // 写不进去只影响下次启动后的推荐范围
  }
}

export async function clearRecommendationPool(): Promise<void> {
  memoryPool = [];
  try {
    await AsyncStorage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
}

/** 仅测试用:丢掉内存副本,下次读取重新走存储 */
export const __resetRecommendationPoolForTests = () => {
  memoryPool = null;
};
