/**
 * 本地个性化推荐:只用本机已有的数据(观看记录、收藏、首页已经加载过的条目)做计算。
 * 这个文件不发任何网络请求,也不把观看记录传给任何地方。
 */
export interface RecommendationHistoryEntry {
  title: string;
  type_name?: string | null;
  save_time?: number;
}

export interface RecommendationFavoriteEntry {
  title: string;
  type_name?: string | null;
  source?: string;
  id?: string;
  poster?: string;
  year?: string;
  source_name?: string;
}

export interface RecommendationCandidate {
  id: string;
  source: string;
  title: string;
  poster: string;
  year?: string;
  rate?: string;
  source_name?: string;
  type_name?: string | null;
  class?: string | null;
  /** 候选所在首页分类的标签(如“科幻”“综艺”),豆瓣条目没有 type_name 时靠它匹配 */
  tags?: string[];
}

export interface Recommendation extends RecommendationCandidate {
  reason: string;
  score: number;
}

export interface RecommendationInput {
  history: RecommendationHistoryEntry[];
  favorites: RecommendationFavoriteEntry[];
  candidates: RecommendationCandidate[];
  limit?: number;
  now?: number;
}

export const RECOMMENDATION_LIMIT = 20;
const RECENT_WINDOW_MS = 30 * 24 * 60 * 60 * 1000;
const SERIES_SCORE = 10;
const UNWATCHED_FAVORITE_SCORE = 5;

export const normalizeRecommendationTitle = (value: unknown) =>
  typeof value === "string" ? value.trim().toLowerCase().replace(/\s+/g, "") : "";

/** 系列词干:去掉“第二季 / 第3部 / season 2 / 末尾数字”,用来认出同系列的其它作品。 */
export const seriesStem = (title: unknown) => {
  let stem = normalizeRecommendationTitle(title);
  let previous = "";
  while (stem !== previous) {
    previous = stem;
    stem = stem
      .replace(/第[一二三四五六七八九十\d]+[季部期]$/, "")
      .replace(/season\d+$/, "")
      .replace(/\d+$/, "")
      .replace(/[:：·\-—_]+$/, "");
  }
  return stem;
};

export const splitGenres = (value: unknown): string[] =>
  typeof value === "string"
    ? value
        .split(/[,，/、|\s]+/)
        .map((token) => token.trim().toLowerCase())
        .filter((token) => token.length > 0)
    : [];

interface Seed {
  title: string;
  kind: "watched" | "favorite";
}

export function buildRecommendations(input: RecommendationInput): Recommendation[] {
  const history = Array.isArray(input.history) ? input.history.filter((entry) => entry && entry.title) : [];
  const favorites = Array.isArray(input.favorites) ? input.favorites.filter((entry) => entry && entry.title) : [];
  const candidates = Array.isArray(input.candidates) ? input.candidates : [];
  const limit = input.limit ?? RECOMMENDATION_LIMIT;
  const now = input.now ?? Date.now();
  if (limit <= 0 || (history.length === 0 && favorites.length === 0)) {
    return [];
  }

  // 1. 偏好画像:分类词 -> 权重
  const genreWeights = new Map<string, number>();
  const addGenres = (typeName: unknown, weight: number) => {
    for (const token of splitGenres(typeName)) {
      genreWeights.set(token, (genreWeights.get(token) ?? 0) + weight);
    }
  };
  for (const entry of history) {
    const recent = typeof entry.save_time === "number" && now - entry.save_time <= RECENT_WINDOW_MS;
    addGenres(entry.type_name, recent ? 2 : 1);
  }
  for (const entry of favorites) {
    addGenres(entry.type_name, 2);
  }

  // 2. 已看标题、系列词干(看过的优先于收藏的作为理由)
  const watchedTitles = new Set(history.map((entry) => normalizeRecommendationTitle(entry.title)));
  const stems = new Map<string, Seed>();
  for (const entry of history) {
    const stem = seriesStem(entry.title);
    if (stem.length >= 2 && !stems.has(stem)) {
      stems.set(stem, { title: entry.title.trim(), kind: "watched" });
    }
  }
  for (const entry of favorites) {
    const stem = seriesStem(entry.title);
    if (stem.length >= 2 && !stems.has(stem)) {
      stems.set(stem, { title: entry.title.trim(), kind: "favorite" });
    }
  }
  const favoriteTitles = new Set(favorites.map((entry) => normalizeRecommendationTitle(entry.title)));

  const scored: Recommendation[] = [];

  // 5. 收藏了还没看
  for (const entry of favorites) {
    const normalized = normalizeRecommendationTitle(entry.title);
    if (!normalized || watchedTitles.has(normalized) || !entry.source || !entry.id) {
      continue;
    }
    scored.push({
      id: entry.id,
      source: entry.source,
      title: entry.title,
      poster: entry.poster ?? "",
      year: entry.year,
      source_name: entry.source_name,
      type_name: entry.type_name,
      reason: "你收藏了还没看",
      score: UNWATCHED_FAVORITE_SCORE,
    });
  }

  // 3/4. 候选打分
  for (const candidate of candidates) {
    const normalized = normalizeRecommendationTitle(candidate?.title);
    if (!normalized || watchedTitles.has(normalized) || favoriteTitles.has(normalized)) {
      continue;
    }
    let seriesScore = 0;
    let seriesReason = "";
    const seed = stems.get(seriesStem(candidate.title));
    if (seed) {
      seriesScore = SERIES_SCORE;
      seriesReason = seed.kind === "watched" ? `因为你看过《${seed.title}》` : `因为你收藏了《${seed.title}》`;
    }
    let genreScore = 0;
    let genreToken = "";
    const candidateGenres = [
      ...splitGenres(candidate.type_name),
      ...splitGenres(candidate.class),
      ...(candidate.tags ?? []).flatMap(splitGenres),
    ];
    for (const [token, weight] of genreWeights) {
      // 首页标签是“科幻”而源站分类是“科幻片”这类情况按包含关系算命中
      if (weight > genreScore && candidateGenres.some((genre) => genre === token || (genre.length >= 2 && token.includes(genre)) || (token.length >= 2 && genre.includes(token)))) {
        genreScore = weight;
        genreToken = token;
      }
    }
    const score = seriesScore + genreScore;
    if (score <= 0) {
      continue;
    }
    scored.push({
      ...candidate,
      reason: seriesScore >= genreScore ? seriesReason : `因为你常看「${genreToken}」`,
      score,
    });
  }

  // 6. 得分降序(稳定),按标题去重,取前 limit 条
  const ordered = scored
    .map((item, index) => ({ item, index }))
    .sort((a, b) => b.item.score - a.item.score || a.index - b.index)
    .map(({ item }) => item);
  const seen = new Set<string>();
  const output: Recommendation[] = [];
  for (const item of ordered) {
    const key = normalizeRecommendationTitle(item.title);
    if (seen.has(key)) continue;
    seen.add(key);
    output.push(item);
    if (output.length >= limit) break;
  }
  return output;
}
