/**
 * 首页大横幅的选片:纯函数,只用调用方传进来的数据(个性化推荐的结果 + 首页当前已加载的条目)。
 * 这个文件不发任何请求,也不做成人 / 片源模式过滤:输入必须是已经过滤后可见的数据。
 */
import { normalizeRecommendationTitle } from "./recommendation";

export interface HeroBannerSource {
  id?: string | number;
  source?: string;
  title?: string;
  poster?: string;
  year?: string;
  rate?: string;
  desc?: string;
  reason?: string;
  playabilityStatus?: string;
}

export interface HeroBannerItem {
  id: string;
  source: string;
  title: string;
  poster: string;
  year?: string;
  rate?: string;
  desc?: string;
  reason?: string;
}

export const HERO_BANNER_LIMIT = 5;
export const HERO_BANNER_INTERVAL_MS = 6000;

export interface HeroBannerInput {
  /** 个性化推荐的结果(开关关闭时不使用) */
  recommendations?: HeroBannerSource[] | null;
  /** 首页当前已加载的条目 */
  homeItems?: HeroBannerSource[] | null;
  recommendationsEnabled?: boolean;
  limit?: number;
}

const clean = (value: unknown) => (typeof value === "string" ? value.trim() : "");

/**
 * 最多 limit 条:推荐在前,其后是首页已加载的条目;必须有海报;按标题去重;
 * 已经探测出“没有可播放源”的条目不上横幅(横幅的主按钮是直接起播)。
 */
export function pickHeroBannerItems(input: HeroBannerInput): HeroBannerItem[] {
  const limit = input.limit ?? HERO_BANNER_LIMIT;
  if (limit <= 0) {
    return [];
  }
  const recommendations =
    input.recommendationsEnabled !== false && Array.isArray(input.recommendations) ? input.recommendations : [];
  const homeItems = Array.isArray(input.homeItems) ? input.homeItems : [];

  const seen = new Set<string>();
  const output: HeroBannerItem[] = [];
  for (const item of [...recommendations, ...homeItems]) {
    if (!item) continue;
    const title = clean(item.title);
    const poster = clean(item.poster);
    const source = clean(item.source);
    const id = item.id === undefined || item.id === null ? "" : String(item.id).trim();
    if (!title || !poster || !source || !id) continue;
    if (item.playabilityStatus === "unavailable") continue;
    const key = normalizeRecommendationTitle(title);
    if (!key || seen.has(key)) continue;
    seen.add(key);
    output.push({
      id,
      source,
      title,
      poster,
      year: clean(item.year) || undefined,
      rate: clean(item.rate) || undefined,
      desc: clean(item.desc) || undefined,
      reason: clean(item.reason) || undefined,
    });
    if (output.length >= limit) break;
  }
  return output;
}

/** 没有简介时显示的一行元信息:年份 / 评分 / 推荐理由 */
export function heroBannerMeta(item: Pick<HeroBannerItem, "year" | "rate" | "reason">): string {
  return [item.year, item.rate ? `★ ${item.rate}` : "", item.reason].filter((part) => !!part).join(" · ");
}

/** 轮播到下一张 / 上一张(循环) */
export function stepHeroIndex(current: number, count: number, delta: number): number {
  if (count <= 0) return 0;
  return (((current + delta) % count) + count) % count;
}
