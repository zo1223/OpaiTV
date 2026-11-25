/**
 * 标题解析与候选匹配。宁可匹配不到,也不把别的片子的弹幕套上来。
 */

export interface ParsedTitle {
  /** 去掉括号备注后的标题(用于搜索) */
  cleaned: string;
  /** 去掉空白和标点、转小写后的完整标题 */
  normalized: string;
  /** 再去掉"第二季"之类季标记和"国语版"之类版本后缀后的片名 */
  base: string;
  /** 去掉季标记后、未规范化的片名(用于第二次搜索) */
  baseText: string;
  /** 标题里写明的季号;没写为 null */
  season: number | null;
  /** 标题带"特别版/国语版"等版本后缀 */
  variant: boolean;
}

export interface TitleCandidate {
  title: string;
  year?: string | number;
  /** 该候选的总集数;未知为 0 */
  episodeCount?: number;
  isMovie?: boolean;
}

const BRACKETS = /【[^】]*】|\[[^\]]*\]|（[^）]*）|\([^)]*\)/g;
const NOISE = /[\s\-_—–~·•:：,，.。!！?？'"“”‘’`、/\\|()（）[\]【】《》<>「」『』+*&^%$#@=;；]/g;
const VARIANTS =
  /(特别版|重制版|国语版|粤语版|日语版|英文版|英语版|中配版|中文版|普通话版|未删减版|加长版|精编版|会员版|tv版|国语|粤语|日语)$/i;
const CN_DIGITS: Record<string, number> = {
  零: 0,
  一: 1,
  二: 2,
  两: 2,
  三: 3,
  四: 4,
  五: 5,
  六: 6,
  七: 7,
  八: 8,
  九: 9,
};

/** "十二" → 12,"3" → 3;认不出返回 null(只支持 0~99) */
export function parseChineseNumber(text: string): number | null {
  const value = text.trim();
  if (!value) return null;
  if (/^\d+$/.test(value)) return parseInt(value, 10);
  const tenIndex = value.indexOf("十");
  if (tenIndex < 0) {
    if (value.length !== 1 || !(value in CN_DIGITS)) return null;
    return CN_DIGITS[value];
  }
  const head = value.slice(0, tenIndex);
  const tail = value.slice(tenIndex + 1);
  if (head.length > 1 || tail.length > 1) return null;
  if (head && !(head in CN_DIGITS)) return null;
  if (tail && !(tail in CN_DIGITS)) return null;
  return (head ? CN_DIGITS[head] : 1) * 10 + (tail ? CN_DIGITS[tail] : 0);
}

export function normalizeTitleText(text: string): string {
  return text.toLowerCase().replace(NOISE, "");
}

export function parseTitle(raw: string): ParsedTitle {
  const cleaned = (raw || "").replace(BRACKETS, " ").replace(/\s+/g, " ").trim() || (raw || "").trim();
  let rest = cleaned;
  let season: number | null = null;

  const cn = rest.match(/第\s*([0-9零一二两三四五六七八九十]+)\s*[季部]/);
  if (cn) {
    const n = parseChineseNumber(cn[1]);
    if (n !== null && n > 0) {
      season = n;
      rest = rest.replace(cn[0], " ");
    }
  }
  if (season === null) {
    const en = rest.match(/(^|[^a-z])(?:season|s)\s*0*(\d{1,2})(?![0-9a-z])/i);
    if (en) {
      season = parseInt(en[2], 10);
      rest = rest.replace(en[0], en[1] + " ");
    }
  }
  if (season === null) {
    // "庆余年2":片名后直接跟 2~20 的数字按季号处理("请回答1988"这类不算)
    const tail = rest.match(/^(.*[^0-9\s])\s*(\d{1,2})$/);
    if (tail) {
      const n = parseInt(tail[2], 10);
      if (n >= 2 && n <= 20) {
        season = n;
        rest = tail[1];
      }
    }
  }

  const baseText = rest.replace(/\s+/g, " ").trim();
  let base = normalizeTitleText(baseText);
  let variant = false;
  const stripped = base.replace(VARIANTS, "");
  if (stripped && stripped !== base) {
    base = stripped;
    variant = true;
  }

  return { cleaned, normalized: normalizeTitleText(cleaned), base, baseText, season, variant };
}

function parseYear(value: string | number | undefined): number | null {
  if (value === undefined || value === null) return null;
  const match = String(value).match(/(19|20)\d{2}/);
  return match ? parseInt(match[0], 10) : null;
}

/**
 * 给候选打分;0 表示不是同一部片。
 * 完全同名 100;片名相同且季号相同 90(带版本后缀 80);再按年份、电影/剧集、集数是否够用微调。
 * 要找的是 3 集以上的剧集而候选是电影时直接算 0。
 */
export function scoreTitleCandidate(
  query: ParsedTitle,
  candidate: TitleCandidate,
  context: { year?: string; episodeCount: number; episodeNumber: number }
): number {
  const parsed = parseTitle(candidate.title);
  if (!parsed.normalized || !query.normalized) return 0;

  let score: number;
  if (parsed.normalized === query.normalized) {
    score = 100;
  } else if (parsed.base && parsed.base === query.base && (parsed.season ?? 1) === (query.season ?? 1)) {
    score = parsed.variant || query.variant ? 80 : 90;
  } else {
    return 0;
  }

  const wantYear = parseYear(context.year);
  const gotYear = parseYear(candidate.year);
  if (wantYear !== null && gotYear !== null) {
    if (wantYear === gotYear) score += 5;
    else if (Math.abs(wantYear - gotYear) > 1) score -= 8;
  }

  const wantMovie = context.episodeCount <= 1;
  if (candidate.isMovie !== undefined) {
    if (wantMovie === candidate.isMovie) score += 2;
    else if (!wantMovie && candidate.isMovie) {
      // 明显是剧集(3 集以上)却搜到同名电影/剧场版:不是同一部(如 12 集的"孤独摇滚！"对上电影"孤独摇滚(下)")
      if (context.episodeCount > 2) return 0;
      score -= 20;
    }
  }

  const count = candidate.episodeCount ?? 0;
  if (count > 0 && context.episodeNumber > count) score -= 10;

  return Math.max(score, 1);
}

/** 从候选里挑得分最高的;同分取靠前的。没有同片候选时返回 -1。 */
export function pickBestCandidate(
  query: ParsedTitle,
  candidates: TitleCandidate[],
  context: { year?: string; episodeCount: number; episodeNumber: number }
): number {
  let best = -1;
  let bestScore = 0;
  candidates.forEach((candidate, index) => {
    const score = scoreTitleCandidate(query, candidate, context);
    if (score >= 60 && score > bestScore) {
      best = index;
      bestScore = score;
    }
  });
  return best;
}

/**
 * 在平台的分集列表里找当前集。
 * 分集都带集号时只按集号找(找不到就是没有这一集);否则按下标。电影(只有 1 集)直接取第一条。
 */
export function pickEpisodeIndex(
  episodeNumbers: (number | null)[],
  query: { episodeIndex: number; episodeNumber: number; episodeCount: number }
): number {
  if (episodeNumbers.length === 0) return -1;
  if (query.episodeCount <= 1 && episodeNumbers.length === 1) return 0;

  const byNumber = episodeNumbers.indexOf(query.episodeNumber);
  if (byNumber >= 0) return byNumber;
  if (episodeNumbers.every((n) => n !== null)) return -1;
  return query.episodeIndex >= 0 && query.episodeIndex < episodeNumbers.length ? query.episodeIndex : -1;
}

/** "12"、"第12集"、"12话" → 12;不是纯集号返回 null */
export function parseEpisodeNumber(text: string | number | undefined | null): number | null {
  if (text === undefined || text === null) return null;
  const match = String(text)
    .trim()
    .match(/^第?\s*0*(\d{1,4})\s*[集话話回期]?$/);
  return match ? parseInt(match[1], 10) : null;
}

const EXTRAS = /预告|花絮|彩蛋|片花|抢先看|特辑|幕后|采访|主题曲|片尾曲|MV|纯享|看点|速看|解说/i;

/** 预告、花絮之类不是正片的分集 */
export function isExtraEpisodeTitle(title: string | undefined | null): boolean {
  return !!title && EXTRAS.test(title);
}

/** 发给 danmu_api /api/v2/match 的文件名:剧集为 "片名 S01E05",电影只发片名 */
export function buildMatchFileName(query: {
  title: string;
  episodeNumber: number;
  episodeCount: number;
}): string {
  const parsed = parseTitle(query.title);
  if (query.episodeCount <= 1) return parsed.cleaned;
  const pad = (n: number) => String(Math.max(0, Math.floor(n))).padStart(2, "0");
  const name = parsed.baseText || parsed.cleaned;
  return `${name} S${pad(parsed.season ?? 1)}E${pad(query.episodeNumber)}`;
}
