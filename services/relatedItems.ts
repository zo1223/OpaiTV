/**
 * 详情页“相关推荐”的挑选:纯函数,只用调用方传进来的候选(本机的推荐候选池)。
 * 这个文件不发任何请求,也不做成人 / 片源模式过滤:候选必须是已经过滤后可见的。
 */
import {
  RecommendationCandidate,
  normalizeRecommendationTitle,
  seriesStem,
  splitGenres,
} from "./recommendation";

export interface RelatedCurrent {
  title: string;
  source?: string;
  id?: string | number;
  type_name?: string | null;
  class?: string | null;
}

export interface RelatedItem extends RecommendationCandidate {
  /** series = 同系列,genre = 同分类 */
  relation: "series" | "genre";
}

export const RELATED_LIMIT = 10;

const genreMatches = (a: string, b: string) =>
  a === b || (a.length >= 2 && b.includes(a)) || (b.length >= 2 && a.includes(b));

/** 同系列在前,其后是同分类;排除当前条目(同标题或同 source+id);必须有海报;按标题去重。 */
export function pickRelatedItems(input: {
  current: RelatedCurrent | null | undefined;
  candidates: RecommendationCandidate[] | null | undefined;
  limit?: number;
}): RelatedItem[] {
  const current = input.current;
  const limit = input.limit ?? RELATED_LIMIT;
  const candidates = Array.isArray(input.candidates) ? input.candidates : [];
  if (!current || !current.title || limit <= 0) {
    return [];
  }

  const currentTitle = normalizeRecommendationTitle(current.title);
  const currentStem = seriesStem(current.title);
  const currentGenres = [...splitGenres(current.type_name), ...splitGenres(current.class)];
  const currentId = current.id === undefined || current.id === null ? "" : String(current.id);

  const series: RelatedItem[] = [];
  const genre: RelatedItem[] = [];
  const seen = new Set<string>([currentTitle]);
  for (const candidate of candidates) {
    if (!candidate || !candidate.title || !candidate.poster) continue;
    const title = normalizeRecommendationTitle(candidate.title);
    if (!title || seen.has(title)) continue;
    if (current.source && candidate.source === current.source && String(candidate.id) === currentId) continue;

    if (currentStem.length >= 2 && seriesStem(candidate.title) === currentStem) {
      seen.add(title);
      series.push({ ...candidate, relation: "series" });
      continue;
    }
    if (currentGenres.length === 0) continue;
    const candidateGenres = [
      ...splitGenres(candidate.type_name),
      ...splitGenres(candidate.class),
      ...(candidate.tags ?? []).flatMap(splitGenres),
    ];
    if (candidateGenres.some((token) => currentGenres.some((mine) => genreMatches(mine, token)))) {
      seen.add(title);
      genre.push({ ...candidate, relation: "genre" });
    }
  }
  return [...series, ...genre].slice(0, limit);
}
