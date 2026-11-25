// TMDB 元数据补全。用户在设置里填了自己的 TMDB Key 之后,详情页用它补全海报、简介、评分、年份。
//  - Key 只存在本机(独立的 AsyncStorage key,不在 AppSettings 里),只会发给 TMDB 官方接口;
//    这个文件不写任何日志,避免 Key 或带 Key 的地址进日志。
//  - 没填 Key:不发任何请求,返回 null。
//  - 任何失败(网络、非 2xx、JSON 损坏、超时、没有对上的条目)都返回 null,调用方静默沿用原有数据。

/** TMDB Key 在 AsyncStorage 里的独立 key(不随设置、同步链接等一起保存或导出) */
export const TMDB_API_KEY_STORAGE_KEY = "mytv_tmdb_api_key";

const TMDB_SEARCH_ENDPOINT = "https://api.themoviedb.org/3/search/multi";
const TMDB_IMAGE_BASE = "https://image.tmdb.org/t/p/w500";
const TMDB_TIMEOUT_MS = 8000;
const TMDB_CACHE_LIMIT = 50;

export type TmdbMediaType = "movie" | "tv";

export interface TmdbMetadata {
  tmdbId: number;
  mediaType: TmdbMediaType;
  title: string;
  posterUrl?: string;
  overview?: string;
  /** vote_average 保留一位小数;没有评分(0 分或 0 人评分)时为 undefined */
  rating?: number;
  year?: string;
}

/** 用来匹配的源站条目信息 */
export interface TmdbLookupSource {
  title: string;
  year?: string | null;
  type_name?: string | null;
}

export const normalizeTmdbApiKey = (value: unknown): string => (typeof value === "string" ? value.trim() : "");

/** v4 Read Access Token 是 JWT(eyJ 开头、三段);其余按 v3 API Key 处理 */
export const isTmdbBearerToken = (key: string): boolean => key.startsWith("eyJ") || key.split(".").length === 3;

const SEASON_SUFFIX_PATTERNS: RegExp[] = [
  // 方括号 / 圆括号里的备注:(2024)、[国语版]、【完结】、（港版）
  /[\[【(（][^\]】)）]*[\]】)）]/g,
  // 第二季 / 第2部 / 第3期 / 最终季
  /第\s*[0-9０-９一二三四五六七八九十百零两]+\s*[季部期辑]/g,
  /最终季/g,
  // Season 2 / S02
  /\bseason\s*\d+\b/gi,
  /\bs\d{1,2}\b/gi,
];

/** 去掉标题里的季数 / 备注后缀:TMDB 的剧集条目不带“第二季”这类后缀 */
export const stripSeasonSuffix = (title: string): string => {
  if (typeof title !== "string") return "";
  const original = title.trim();
  let text = original;
  for (const pattern of SEASON_SUFFIX_PATTERNS) {
    text = text.replace(pattern, " ");
  }
  // 空格后面跟着的一两位数字:“鱿鱼游戏 2”
  text = text.replace(/\s+\d{1,2}\s*$/, " ");
  text = text.replace(/\s+/g, " ").trim();
  return text || original;
};

/** 比较用的归一化:NFKC(全角转半角)、小写、去掉空白与标点 */
export const normalizeTmdbTitle = (title: unknown): string => {
  if (typeof title !== "string") return "";
  return title
    .normalize("NFKC")
    .toLowerCase()
    .replace(/[\s!-/:-@[-`{-~·‐-‧‰-⁞　-〿＀-／：-＠［-｀｛-･]+/g, "");
};

const extractYear = (value: unknown): number | null => {
  if (typeof value !== "string" && typeof value !== "number") return null;
  const match = /(?:19|20)\d{2}/.exec(String(value));
  return match ? Number(match[0]) : null;
};

/** 源站分类名暗示的类型:电影 / …片 → movie;…剧 / 动漫 / 综艺 / 番 → tv */
export const guessTmdbMediaType = (typeName: unknown): TmdbMediaType | null => {
  if (typeof typeName !== "string" || !typeName.trim()) return null;
  if (/电影|片/.test(typeName)) return "movie";
  if (/剧|动漫|综艺|番/.test(typeName)) return "tv";
  return null;
};

type TmdbRawResult = {
  id?: unknown;
  media_type?: unknown;
  title?: unknown;
  name?: unknown;
  original_title?: unknown;
  original_name?: unknown;
  poster_path?: unknown;
  overview?: unknown;
  vote_average?: unknown;
  vote_count?: unknown;
  release_date?: unknown;
  first_air_date?: unknown;
};

const resultYear = (result: TmdbRawResult): number | null =>
  extractYear(result.media_type === "movie" ? result.release_date : result.first_air_date);

const toMetadata = (result: TmdbRawResult): TmdbMetadata => {
  const mediaType = result.media_type as TmdbMediaType;
  const names =
    mediaType === "movie" ? [result.title, result.original_title] : [result.name, result.original_name];
  const title = names.find((name): name is string => typeof name === "string" && name.trim() !== "") ?? "";
  const overview = typeof result.overview === "string" ? result.overview.trim() : "";
  const voteAverage = typeof result.vote_average === "number" ? result.vote_average : 0;
  const hasVotes = result.vote_count === undefined || (typeof result.vote_count === "number" && result.vote_count > 0);
  const year = resultYear(result);
  return {
    tmdbId: result.id as number,
    mediaType,
    title,
    posterUrl:
      typeof result.poster_path === "string" && result.poster_path.startsWith("/")
        ? `${TMDB_IMAGE_BASE}${result.poster_path}`
        : undefined,
    overview: overview || undefined,
    rating: voteAverage > 0 && hasVotes ? Math.round(voteAverage * 10) / 10 : undefined,
    year: year ? String(year) : undefined,
  };
};

/**
 * 从 search/multi 的结果里挑出对应条目。
 * 只有归一化后标题完全相同的电影 / 剧集才算候选,没有就不补全(宁可不补,也不补错)。
 * 候选里优先:年份与源站一致(±1)→ 类型与源站分类一致 → TMDB 返回的原顺序。
 */
export const pickTmdbMatch = (results: unknown, source: TmdbLookupSource): TmdbMetadata | null => {
  if (!Array.isArray(results)) return null;
  const wanted = normalizeTmdbTitle(stripSeasonSuffix(source?.title ?? ""));
  if (!wanted) return null;

  const sourceYear = extractYear(source.year ?? null);
  const typeHint = guessTmdbMediaType(source.type_name);

  const candidates = results
    .map((raw, index) => ({ raw: raw as TmdbRawResult, index }))
    .filter(({ raw }) => {
      if (!raw || typeof raw !== "object") return false;
      if (raw.media_type !== "movie" && raw.media_type !== "tv") return false;
      if (typeof raw.id !== "number") return false;
      return [raw.title, raw.name, raw.original_title, raw.original_name].some(
        (name) => typeof name === "string" && normalizeTmdbTitle(name) === wanted
      );
    })
    .map(({ raw, index }) => {
      const year = resultYear(raw);
      return {
        raw,
        index,
        yearMatch: sourceYear !== null && year !== null && Math.abs(year - sourceYear) <= 1 ? 1 : 0,
        typeMatch: typeHint !== null && raw.media_type === typeHint ? 1 : 0,
      };
    });

  if (candidates.length === 0) return null;
  candidates.sort((a, b) => b.yearMatch - a.yearMatch || b.typeMatch - a.typeMatch || a.index - b.index);
  return toMetadata(candidates[0].raw);
};

const cache = new Map<string, TmdbMetadata>();

export const clearTmdbCache = (): void => {
  cache.clear();
};

/**
 * 查 TMDB。没填 Key 时不发请求;任何失败都返回 null,不抛错、不提示。
 * 只发一个请求:GET /3/search/multi(include_adult 固定为 false)。
 */
export const fetchTmdbMetadata = async (
  source: TmdbLookupSource,
  apiKey: string | null | undefined,
  options: { timeoutMs?: number } = {}
): Promise<TmdbMetadata | null> => {
  const key = normalizeTmdbApiKey(apiKey);
  if (!key) return null;

  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const query = stripSeasonSuffix(source?.title ?? "");
    if (!normalizeTmdbTitle(query)) return null;

    const cacheKey = [
      normalizeTmdbTitle(query),
      extractYear(source.year ?? null) ?? "",
      guessTmdbMediaType(source.type_name) ?? "",
    ].join("|");
    const cached = cache.get(cacheKey);
    if (cached) return cached;

    const bearer = isTmdbBearerToken(key);
    const params = [
      `query=${encodeURIComponent(query)}`,
      "language=zh-CN",
      "include_adult=false",
      "page=1",
      ...(bearer ? [] : [`api_key=${encodeURIComponent(key)}`]),
    ];
    const headers: Record<string, string> = { Accept: "application/json" };
    if (bearer) headers.Authorization = `Bearer ${key}`;

    const controller = typeof AbortController !== "undefined" ? new AbortController() : null;
    const timeoutMs = options.timeoutMs ?? TMDB_TIMEOUT_MS;
    // 即使底层 fetch 不理会 abort,到点也当作失败返回
    const timeout = new Promise<never>((_, reject) => {
      timer = setTimeout(() => {
        controller?.abort();
        reject(new Error("TMDB_TIMEOUT"));
      }, timeoutMs);
    });

    const request = (async () => {
      const response = await fetch(`${TMDB_SEARCH_ENDPOINT}?${params.join("&")}`, {
        method: "GET",
        headers,
        ...(controller ? { signal: controller.signal } : {}),
      });
      if (!response || !response.ok) return null;
      const data = await response.json();
      return pickTmdbMatch(data?.results, source);
    })();
    // 超时先返回时,迟到的失败不能变成未处理的 rejection
    request.catch(() => undefined);

    const metadata = await Promise.race([request, timeout]);
    if (metadata) {
      if (cache.size >= TMDB_CACHE_LIMIT) {
        const oldest = cache.keys().next().value;
        if (oldest !== undefined) cache.delete(oldest);
      }
      cache.set(cacheKey, metadata);
    }
    return metadata ?? null;
  } catch {
    return null;
  } finally {
    if (timer) clearTimeout(timer);
  }
};

const isBlank = (value: unknown): boolean => {
  if (typeof value !== "string") return true;
  const text = value.trim().toLowerCase();
  return text === "" || text === "unknown" || text === "0";
};

/**
 * 用 TMDB 数据补全详情,只补不改:原有的非空字段一律保留,
 * 只在海报 / 简介 / 年份为空时填 TMDB 的值;评分单独放在 tmdbRating(源站条目本身没有评分)。
 * meta 为空时原样返回传入的对象。
 */
export const mergeTmdbMetadata = <T extends { poster?: string; desc?: string; year?: string }>(
  detail: T,
  meta: TmdbMetadata | null | undefined
): T & { tmdbRating?: number } => {
  if (!meta || !detail) return detail;
  return {
    ...detail,
    poster: isBlank(detail.poster) && meta.posterUrl ? meta.posterUrl : detail.poster,
    desc: isBlank(detail.desc) && meta.overview ? meta.overview : detail.desc,
    year: isBlank(detail.year) && meta.year ? meta.year : detail.year,
    tmdbRating: meta.rating,
  };
};
