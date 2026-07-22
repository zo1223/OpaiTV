import type { ApiSite, SearchResult, VideoDetail } from "./api";

const BASE58_ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
const BASE58_MAP = BASE58_ALPHABET.split("").reduce<Record<string, number>>((acc, char, index) => {
  acc[char] = index;
  return acc;
}, {});

export interface VideoSourceSubscriptionSource {
  key: string;
  name: string;
  api: string;
  detail?: string;
}

export interface VideoSourceSubscriptionConfig {
  sources: VideoSourceSubscriptionSource[];
}

export interface VideoSourceCategory {
  id: string;
  name: string;
}

export interface VideoSourceCategoryVideosResponse {
  results: SearchResult[];
  total: number;
  page: number;
  pageCount: number;
}

const stripTrailingSlash = (value: string) => value.replace(/\/+$/, "");

const decodeUtf8 = (bytes: number[]) => {
  let result = "";
  let i = 0;

  while (i < bytes.length) {
    const byte1 = bytes[i++];
    if (byte1 < 0x80) {
      result += String.fromCharCode(byte1);
      continue;
    }

    if (byte1 >= 0xc0 && byte1 < 0xe0) {
      const byte2 = bytes[i++];
      result += String.fromCharCode(((byte1 & 0x1f) << 6) | (byte2 & 0x3f));
      continue;
    }

    if (byte1 >= 0xe0 && byte1 < 0xf0) {
      const byte2 = bytes[i++];
      const byte3 = bytes[i++];
      result += String.fromCharCode(((byte1 & 0x0f) << 12) | ((byte2 & 0x3f) << 6) | (byte3 & 0x3f));
      continue;
    }

    const byte2 = bytes[i++];
    const byte3 = bytes[i++];
    const byte4 = bytes[i++];
    const codePoint =
      ((byte1 & 0x07) << 18) |
      ((byte2 & 0x3f) << 12) |
      ((byte3 & 0x3f) << 6) |
      (byte4 & 0x3f);
    const adjusted = codePoint - 0x10000;
    result += String.fromCharCode(0xd800 + (adjusted >> 10), 0xdc00 + (adjusted & 0x3ff));
  }

  return result;
};

export const decodeBase58 = (value: string) => {
  const trimmed = value.trim();
  if (!trimmed) return "";

  const bytes = [0];
  for (const char of trimmed) {
    const digit = BASE58_MAP[char];
    if (digit === undefined) {
      throw new Error("INVALID_BASE58_SUBSCRIPTION");
    }

    let carry = digit;
    for (let i = 0; i < bytes.length; i++) {
      const x = bytes[i] * 58 + carry;
      bytes[i] = x & 0xff;
      carry = x >> 8;
    }

    while (carry > 0) {
      bytes.push(carry & 0xff);
      carry >>= 8;
    }
  }

  for (const char of trimmed) {
    if (char === BASE58_ALPHABET[0]) {
      bytes.push(0);
    } else {
      break;
    }
  }

  return decodeUtf8(bytes.reverse());
};

export const decodeSubscriptionPayload = (payload: string) => {
  const trimmed = payload.trim();
  if (!trimmed) {
    throw new Error("EMPTY_VIDEO_SOURCE_SUBSCRIPTION");
  }

  try {
    return JSON.parse(trimmed);
  } catch {
    return JSON.parse(decodeBase58(trimmed));
  }
};

export const normalizeVideoSourceConfig = (rawConfig: any): VideoSourceSubscriptionConfig => {
  const apiSite = rawConfig?.api_site;
  if (!apiSite || typeof apiSite !== "object") {
    throw new Error("INVALID_VIDEO_SOURCE_SUBSCRIPTION");
  }

  const entries = Array.isArray(apiSite)
    ? apiSite.map((item: any, index) => [item?.key || item?.name || `source_${index}`, item])
    : Object.entries(apiSite);

  const sources = entries
    .map(([key, value]) => {
      const item = value as any;
      const api = typeof item?.api === "string" ? stripTrailingSlash(item.api.trim()) : "";
      const name = typeof item?.name === "string" ? item.name.trim() : String(key);
      if (!api || !name) return null;

      return {
        key: String(key),
        name,
        api,
        detail: typeof item?.detail === "string" ? item.detail : undefined,
      };
    })
    .filter(Boolean) as VideoSourceSubscriptionSource[];

  if (sources.length === 0) {
    throw new Error("EMPTY_VIDEO_SOURCE_SUBSCRIPTION");
  }

  return { sources };
};

export const fetchVideoSourceSubscription = async (subscriptionUrl: string): Promise<VideoSourceSubscriptionConfig> => {
  const response = await fetch(subscriptionUrl);
  if (!response.ok) {
    throw new Error(`VIDEO_SOURCE_SUBSCRIPTION_FETCH_FAILED:${response.status}`);
  }
  const payload = await response.text();
  return normalizeVideoSourceConfig(decodeSubscriptionPayload(payload));
};

const parseAppleCmsEpisodeGroup = (group: string): string[] => {
  const urls: string[] = [];
  const seen = new Set<string>();
  for (const episode of group.split("#")) {
    const separatorIndex = episode.indexOf("$");
    const url = (separatorIndex >= 0 ? episode.slice(separatorIndex + 1) : episode).trim();
    if (url && !seen.has(url)) {
      seen.add(url);
      urls.push(url);
    }
  }
  return urls;
};

export const parseAppleCmsPlaybackSources = (playFrom: unknown, playUrl: unknown) => {
  if (typeof playUrl !== "string") return [];

  const names = typeof playFrom === "string" ? playFrom.split("$$$") : [];
  return playUrl
    .split("$$$")
    .map((group, index) => {
      const name = names[index]?.trim() || `线路 ${index + 1}`;
      return {
        key: `${index}:${name}`,
        name,
        episodes: parseAppleCmsEpisodeGroup(group),
      };
    })
    .filter((source) => source.episodes.length > 0);
};

export const getSearchResultSourceKey = (result: Pick<SearchResult, "source" | "playback_source">) =>
  `${result.source}\u001f${result.playback_source || ""}`;

export const expandSearchResultPlaybackSources = (result: SearchResult): SearchResult[] => {
  const playbackSources = result.playback_sources || [];
  if (playbackSources.length <= 1) return [result];

  return playbackSources.map((playbackSource) => ({
    ...result,
    episodes: playbackSource.episodes,
    source_name: `${result.source_name} · ${playbackSource.name}`,
    playback_source: playbackSource.key,
  }));
};

const getVodList = (data: any) => (Array.isArray(data?.list) ? data.list : []);

const getCategoryList = (data: any) => (Array.isArray(data?.class) ? data.class : []);

const appendQueryParams = (url: string, params: Record<string, string>) => {
  const searchParams = Object.entries(params)
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
    .join("&");
  return `${stripTrailingSlash(url)}${url.includes("?") ? "&" : "?"}${searchParams}`;
};

const safeDecodeURIComponent = (value: string) => {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
};

const buildAppleCmsUrl = (apiUrl: string, params: Record<string, string>) => {
  const strippedApiUrl = stripTrailingSlash(apiUrl);
  const proxyTargetMatch = strippedApiUrl.match(/([?&]url=)([^&]+)/);

  if (!proxyTargetMatch || proxyTargetMatch.index === undefined) {
    return appendQueryParams(strippedApiUrl, params);
  }

  const targetUrl = safeDecodeURIComponent(proxyTargetMatch[2]);
  const targetWithParams = appendQueryParams(targetUrl, params);
  const matchStart = proxyTargetMatch.index;
  const matchEnd = matchStart + proxyTargetMatch[0].length;

  return `${strippedApiUrl.slice(0, matchStart)}${proxyTargetMatch[1]}${targetWithParams}${strippedApiUrl.slice(matchEnd)}`;
};

const mapAppleCmsSearchResult = (source: VideoSourceSubscriptionSource, item: any): SearchResult => {
  const playbackSources = parseAppleCmsPlaybackSources(item.vod_play_from, item.vod_play_url);
  return {
    id: Number(item.vod_id),
    title: item.vod_name || "",
    poster: item.vod_pic || "",
    episodes: playbackSources[0]?.episodes || [],
    source: source.key,
    source_name: source.name,
    class: item.vod_class,
    year: item.vod_year || "",
    desc: item.vod_blurb || item.vod_content || item.vod_remarks,
    type_name: item.type_name,
    ...(playbackSources.length > 1 ? { playback_sources: playbackSources } : {}),
  };
};

const mapAppleCmsDetail = (source: VideoSourceSubscriptionSource, item: any): VideoDetail => ({
  id: String(item.vod_id),
  title: item.vod_name || "",
  poster: item.vod_pic || "",
  source: source.key,
  source_name: source.name,
  desc: item.vod_content || item.vod_blurb || item.vod_remarks,
  type: item.type_id !== undefined ? String(item.type_id) : undefined,
  year: item.vod_year,
  area: item.vod_area,
  director: item.vod_director,
  actor: item.vod_actor,
  remarks: item.vod_remarks,
});

export const searchAppleCmsSource = async (
  source: VideoSourceSubscriptionSource,
  query: string,
  signal?: AbortSignal
): Promise<SearchResult[]> => {
  const response = await fetch(buildAppleCmsUrl(source.api, { ac: "detail", wd: query }), { signal });
  if (!response.ok) {
    throw new Error(`APPLE_CMS_SEARCH_FAILED:${response.status}`);
  }

  const data = await response.json();
  return getVodList(data)
    .map((item: any) => mapAppleCmsSearchResult(source, item))
    .filter((item: SearchResult) => item.id && item.title);
};

export const getAppleCmsSourceResult = async (
  source: VideoSourceSubscriptionSource,
  id: string,
  signal?: AbortSignal
): Promise<SearchResult[]> => {
  const response = await fetch(buildAppleCmsUrl(source.api, { ac: "detail", ids: id }), { signal });
  if (!response.ok) {
    throw new Error(`APPLE_CMS_DETAIL_FAILED:${response.status}`);
  }

  const data = await response.json();
  return getVodList(data)
    .map((item: any) => mapAppleCmsSearchResult(source, item))
    .filter((item: SearchResult) => item.id && item.title);
};

export const fetchAppleCmsSourcePage = async (
  source: VideoSourceSubscriptionSource,
  pageStart: number,
  pageSize: number,
  signal?: AbortSignal
): Promise<SearchResult[]> => {
  const page = Math.floor(pageStart / pageSize) + 1;
  const response = await fetch(buildAppleCmsUrl(source.api, { ac: "detail", pg: String(page) }), { signal });
  if (!response.ok) {
    throw new Error(`APPLE_CMS_PAGE_FAILED:${response.status}`);
  }

  const data = await response.json();
  return getVodList(data)
    .map((item: any) => mapAppleCmsSearchResult(source, item))
    .filter((item: SearchResult) => item.id && item.title)
    .slice(0, pageSize);
};

const HOME_CATEGORY_ALIASES: Record<string, string[]> = {
  热门: ["连续剧", "电视剧", "剧集"],
  最新: ["电影", "电影片"],
  可播放: ["可播放", "电影", "电影片"],
  国产剧: ["国产剧", "大陆剧", "内地剧"],
  美剧: ["欧美剧", "美国剧"],
  英剧: ["欧美剧", "英国剧"],
  韩剧: ["韩国剧", "韩剧"],
  日剧: ["日本剧", "日剧"],
  港剧: ["香港剧", "港剧"],
  日本动画: ["日本动漫", "日韩动漫", "日本动画"],
  动画: ["动漫", "动漫片", "动画片"],
  综艺: ["综艺", "综艺片"],
  纪录片: ["纪录片", "纪录", "纪录剧集"],
  动作: ["动作片", "动作"],
  喜剧: ["喜剧片", "喜剧"],
  爱情: ["爱情片", "爱情"],
  科幻: ["科幻片", "科幻"],
  悬疑: ["悬疑片", "悬疑"],
  恐怖: ["恐怖片", "恐怖"],
  文艺: ["文艺片", "文艺", "剧情片", "剧情", "电影", "电影片"],
  华语: ["华语", "华语电影", "国产电影", "大陆电影", "中国电影", "电影", "电影片"],
  欧美: ["欧美", "欧美电影", "美国电影", "英国电影", "电影", "电影片"],
  韩国: ["韩国电影", "韩国", "电影", "电影片"],
  日本: ["日本电影", "日本", "电影", "电影片"],
  经典: ["经典", "经典电影"],
  豆瓣高分: ["豆瓣高分", "高分电影"],
  冷门佳片: ["冷门佳片"],
  top250: ["top250", "豆瓣Top250", "豆瓣 Top250"],
};

const HOME_TAG_ALIASES: Record<string, string> = {
  hot: "热门",
  latest: "最新",
};

const HOME_AREA_FILTERS: Record<string, string[]> = {
  国产剧: ["中国", "大陆", "内地"],
  美剧: ["美国"],
  英剧: ["英国"],
  韩剧: ["韩国", "韩"],
  日剧: ["日本", "日"],
  港剧: ["香港", "港"],
  日本动画: ["日本", "日"],
  华语: ["中国", "大陆", "内地", "香港", "台湾", "澳门", "华语"],
  欧美: ["欧美", "美国", "英国", "法国", "德国", "意大利", "西班牙", "加拿大", "澳大利亚", "欧洲"],
  韩国: ["韩国", "韩"],
  日本: ["日本", "日"],
};

const HOME_CLASS_FILTERS: Record<string, string[]> = {
  国产剧: ["国产", "大陆", "内地"],
  美剧: ["美剧", "美国"],
  英剧: ["英剧", "英国"],
  韩剧: ["韩剧", "韩国"],
  日剧: ["日剧", "日本"],
  港剧: ["港剧", "香港"],
  日本动画: ["日本动漫", "日韩动漫", "日本动画"],
  动画: ["动漫", "动画"],
  综艺: ["综艺"],
  纪录片: ["纪录片", "纪录"],
  动作: ["动作"],
  喜剧: ["喜剧"],
  爱情: ["爱情"],
  科幻: ["科幻"],
  悬疑: ["悬疑"],
  恐怖: ["恐怖"],
  文艺: ["文艺", "剧情"],
};

const BROAD_HOME_TAGS = new Set(["热门", "最新"]);

const normalizeHomeTag = (tag: string) => HOME_TAG_ALIASES[tag] || tag;

const getTypeFallback = (type: "movie" | "tv") =>
  type === "movie" ? ["电影", "电影片"] : ["连续剧", "电视剧", "剧集"];

const shouldUseTypeFallback = (tag: string) =>
  BROAD_HOME_TAGS.has(tag) || Boolean(HOME_AREA_FILTERS[tag] || HOME_CLASS_FILTERS[tag]);

const getHomeCategoryCandidates = (type: "movie" | "tv", tag: string) => {
  const normalizedTag = normalizeHomeTag(tag);
  const tagCandidates = HOME_CATEGORY_ALIASES[normalizedTag] || HOME_CATEGORY_ALIASES[normalizedTag.toLowerCase()] || [normalizedTag];

  if (normalizedTag === "热门" && type === "movie") {
    return { exact: ["电影", "电影片"], fallback: [] };
  }

  return {
    exact: tagCandidates,
    fallback: shouldUseTypeFallback(normalizedTag) ? getTypeFallback(type) : [],
  };
};

const normalizeCategoryName = (value: string) =>
  value
    .trim()
    .toLowerCase()
    .replace(/\s+/g, "")
    .replace(/[片劇剧]$/u, "");

const hasAnyKeyword = (value: string, keywords: string[]) => keywords.some((keyword) => value.includes(keyword));

const isClearlyTvCategory = (value: string) =>
  hasAnyKeyword(value, ["电视剧", "连续剧", "剧集", "国产剧", "大陆剧", "内地剧", "欧美剧", "美国剧", "英国剧", "韩剧", "韩国剧", "日剧", "日本剧", "港剧", "香港剧", "美剧", "英剧", "纪录片"]);

const isClearlyMovieCategory = (value: string) =>
  hasAnyKeyword(value, ["电影", "电影片", "动作片", "喜剧片", "爱情片", "科幻片", "悬疑片", "恐怖片", "剧情片", "纪录片"]);

const isCompatibleHomeCategory = (categoryName: string, type: "movie" | "tv") => {
  if (type === "movie") {
    return !isClearlyTvCategory(categoryName);
  }

  return !isClearlyMovieCategory(categoryName) || isClearlyTvCategory(categoryName);
};

const findMatchingCategory = (categories: VideoSourceCategory[], candidates: string[], type: "movie" | "tv") => {
  for (const candidate of candidates) {
    const exactName = candidate.trim().toLowerCase();
    const matched = categories.find(
      (category) => category.name.trim().toLowerCase() === exactName && isCompatibleHomeCategory(category.name, type)
    );
    if (matched) {
      return { category: matched, candidate };
    }
  }

  for (const candidate of candidates) {
    const normalizedName = normalizeCategoryName(candidate);
    const matched = categories.find(
      (category) => normalizeCategoryName(category.name) === normalizedName && isCompatibleHomeCategory(category.name, type)
    );
    if (matched) {
      return { category: matched, candidate };
    }
  }

  return undefined;
};

const findHomeCategory = (categories: VideoSourceCategory[], type: "movie" | "tv", tag: string) => {
  const candidates = getHomeCategoryCandidates(type, tag);
  const exact = findMatchingCategory(categories, candidates.exact, type);
  if (exact) {
    return { ...exact, usedTypeFallback: false };
  }

  const fallback = findMatchingCategory(categories, candidates.fallback, type);
  if (fallback) {
    return { ...fallback, usedTypeFallback: true };
  }

  return undefined;
};

const isGenericTypeCandidate = (candidate: string, type: "movie" | "tv") => {
  const normalizedCandidate = normalizeCategoryName(candidate);
  return getTypeFallback(type).some((fallback) => normalizeCategoryName(fallback) === normalizedCandidate);
};

const isCompatibleHomeType = (item: any, type: "movie" | "tv") => {
  const metadata = [item?.type_name, item?.vod_class].filter(Boolean).join(" ");
  if (!metadata) return true;

  if (type === "movie") {
    return !isClearlyTvCategory(metadata);
  }

  return !isClearlyMovieCategory(metadata) || isClearlyTvCategory(metadata);
};

const getHomeClassMetadata = (item: any) => [item?.vod_class, item?.type_name].filter(Boolean).join(" ");

const matchesHomeClass = (item: any, tag: string, requireMetadata: boolean) => {
  const keywords = HOME_CLASS_FILTERS[tag];
  if (!keywords) return true;

  const metadata = getHomeClassMetadata(item);
  if (!metadata) return !requireMetadata;
  return hasAnyKeyword(metadata, keywords);
};

const matchesHomeArea = (item: any, tag: string, requireMetadata: boolean) => {
  const keywords = HOME_AREA_FILTERS[tag];
  if (!keywords) return true;
  if (!requireMetadata) return true;

  const classKeywords = HOME_CLASS_FILTERS[tag];
  if (classKeywords && hasAnyKeyword(getHomeClassMetadata(item), classKeywords)) {
    return true;
  }

  const area = typeof item?.vod_area === "string" ? item.vod_area.trim() : "";
  if (!area) return false;
  return hasAnyKeyword(area, keywords);
};

const shouldIncludeHomeItem = (
  item: any,
  type: "movie" | "tv",
  tag: string,
  usedTypeFallback: boolean,
  matchedCandidate: string
) => {
  const requireMetadata = usedTypeFallback || isGenericTypeCandidate(matchedCandidate, type);
  return (
    isCompatibleHomeType(item, type) &&
    matchesHomeArea(item, tag, requireMetadata) &&
    matchesHomeClass(item, tag, requireMetadata)
  );
};

const fetchAppleCmsFilteredCategoryResults = async (
  source: VideoSourceSubscriptionSource,
  categoryId: string,
  type: "movie" | "tv",
  tag: string,
  usedTypeFallback: boolean,
  matchedCandidate: string,
  page: number,
  pageSize: number,
  signal?: AbortSignal
) => {
  const response = await fetch(
    buildAppleCmsUrl(source.api, { ac: "detail", t: categoryId, pg: String(page) }),
    { signal }
  );
  if (!response.ok) {
    throw new Error(`APPLE_CMS_CATEGORY_VIDEOS_FAILED:${response.status}`);
  }

  const data = await response.json();
  return getVodList(data)
    .filter((item: any) => shouldIncludeHomeItem(item, type, tag, usedTypeFallback, matchedCandidate))
    .map((item: any) => mapAppleCmsSearchResult(source, item))
    .filter((item: SearchResult) => item.id && item.title)
    .slice(0, pageSize);
};

export const fetchAppleCmsHomePage = async (
  source: VideoSourceSubscriptionSource,
  type: "movie" | "tv",
  tag: string,
  pageStart: number,
  pageSize: number,
  signal?: AbortSignal
): Promise<SearchResult[]> => {
  const page = Math.floor(pageStart / pageSize) + 1;
  const normalizedTag = normalizeHomeTag(tag);
  let categories: VideoSourceCategory[];

  try {
    categories = await fetchAppleCmsCategories(source, signal);
  } catch {
    // Fall back to the source's latest page when a source does not expose categories.
    return fetchAppleCmsSourcePage(source, pageStart, pageSize, signal);
  }

  const matchedCategory = findHomeCategory(categories, type, normalizedTag);
  if (!matchedCategory) {
    if (!BROAD_HOME_TAGS.has(normalizedTag)) {
      return [];
    }
    return fetchAppleCmsSourcePage(source, pageStart, pageSize, signal);
  }

  try {
    return await fetchAppleCmsFilteredCategoryResults(
      source,
      matchedCategory.category.id,
      type,
      normalizedTag,
      matchedCategory.usedTypeFallback,
      matchedCategory.candidate,
      page,
      pageSize,
      signal
    );
  } catch {
    return [];
  }
};

export const fetchAppleCmsCategories = async (
  source: VideoSourceSubscriptionSource,
  signal?: AbortSignal
): Promise<VideoSourceCategory[]> => {
  const response = await fetch(buildAppleCmsUrl(source.api, { ac: "list" }), { signal });
  if (!response.ok) {
    throw new Error(`APPLE_CMS_CATEGORIES_FAILED:${response.status}`);
  }

  const data = await response.json();
  return getCategoryList(data)
    .map((item: any) => ({
      id: item.type_id !== undefined ? String(item.type_id) : "",
      name: item.type_name || "",
    }))
    .filter((item: VideoSourceCategory) => item.id && item.name);
};

export const fetchAppleCmsCategoryVideos = async (
  source: VideoSourceSubscriptionSource,
  categoryId: string,
  page: number,
  pageSize: number,
  signal?: AbortSignal
): Promise<VideoSourceCategoryVideosResponse> => {
  const response = await fetch(
    buildAppleCmsUrl(source.api, { ac: "detail", t: categoryId, pg: String(page) }),
    { signal }
  );
  if (!response.ok) {
    throw new Error(`APPLE_CMS_CATEGORY_VIDEOS_FAILED:${response.status}`);
  }

  const data = await response.json();
  return {
    results: getVodList(data)
      .map((item: any) => mapAppleCmsSearchResult(source, item))
      .filter((item: SearchResult) => item.id && item.title)
      .slice(0, pageSize),
    total: Number(data?.total) || 0,
    page: Number(data?.page) || page,
    pageCount: Number(data?.pagecount ?? data?.pageCount) || 0,
  };
};

export const getAppleCmsDetail = async (
  source: VideoSourceSubscriptionSource,
  id: string,
  signal?: AbortSignal
): Promise<VideoDetail> => {
  const response = await fetch(buildAppleCmsUrl(source.detail || source.api, { ac: "detail", ids: id }), { signal });
  if (!response.ok) {
    throw new Error(`APPLE_CMS_DETAIL_FAILED:${response.status}`);
  }

  const data = await response.json();
  const item = getVodList(data)[0];
  if (!item) {
    throw new Error("APPLE_CMS_DETAIL_NOT_FOUND");
  }

  return mapAppleCmsDetail(source, item);
};

export const videoSourceConfigToApiSites = (config: VideoSourceSubscriptionConfig): ApiSite[] =>
  config.sources.map((source) => ({
    key: source.key,
    name: source.name,
    api: source.api,
    detail: source.detail,
  }));

export const findVideoSource = (config: VideoSourceSubscriptionConfig, key: string) =>
  config.sources.find((source) => source.key === key);
