import AsyncStorage from "@react-native-async-storage/async-storage";
import {
  fetchAppleCmsCategories,
  fetchAppleCmsCategoryVideos,
  fetchAppleCmsHomePage,
  findVideoSource,
  getAppleCmsDetail,
  getAppleCmsSourceResult,
  searchAppleCmsSource,
  videoSourceConfigToApiSites,
  VideoSourceCategory,
  VideoSourceCategoryVideosResponse,
  VideoSourceSubscriptionSource,
} from "./videoSourceSubscription";

// region: --- Interface Definitions ---
export interface DoubanItem {
  id?: number | string;
  title: string;
  poster: string;
  rate?: string;
  episodes?: string[];
  source?: string;
  source_name?: string;
  year?: string;
  desc?: string;
  type_name?: string;
}

export interface DoubanResponse {
  code: number;
  message: string;
  list: DoubanItem[];
}

export interface VideoDetail {
  id: string;
  title: string;
  poster: string;
  source: string;
  source_name: string;
  desc?: string;
  type?: string;
  year?: string;
  area?: string;
  director?: string;
  actor?: string;
  remarks?: string;
}

export interface SearchResult {
  id: number;
  title: string;
  poster: string;
  episodes: string[];
  source: string;
  source_name: string;
  class?: string;
  year: string;
  desc?: string;
  type_name?: string;
  playback_source?: string;
  playback_sources?: PlaybackSource[];
}

export interface PlaybackSource {
  key: string;
  name: string;
  episodes: string[];
}

export interface Favorite {
  cover: string;
  title: string;
  source_name: string;
  total_episodes: number;
  search_title: string;
  year: string;
  save_time?: number;
}

export interface PlayRecord {
  title: string;
  source_name: string;
  cover: string;
  index: number;
  total_episodes: number;
  play_time: number;
  total_time: number;
  save_time: number;
  year: string;
}

export interface ApiSite {
  key: string;
  api: string;
  name: string;
  detail?: string;
}

export interface ServerConfig {
  SiteName: string;
  StorageType: "localstorage" | "redis" | string;
}

export interface ImageSource {
  uri: string;
  headers?: Record<string, string>;
}

const normalizeDedupeValue = (value: unknown) =>
  typeof value === "string" ? value.trim().toLowerCase().replace(/\s+/g, "") : "";

const getVideoDedupeKey = (item: DoubanItem) => {
  const title = normalizeDedupeValue(item.title);
  const year = normalizeDedupeValue(item.year);
  const poster = normalizeDedupeValue(item.poster);

  if (title) {
    return year ? `title:${title}:year:${year}` : `title:${title}`;
  }

  return poster ? `poster:${poster}` : "";
};

const mixSourceResults = <T>(sourceResults: T[][], limit: number, getDedupeKey?: (item: T) => string) => {
  const mixed: T[] = [];
  const seenKeys = new Set<string>();
  let index = 0;

  while (mixed.length < limit) {
    let appended = false;

    for (const results of sourceResults) {
      if (results[index]) {
        const dedupeKey = getDedupeKey?.(results[index]);
        if (!dedupeKey || !seenKeys.has(dedupeKey)) {
          if (dedupeKey) {
            seenKeys.add(dedupeKey);
          }
          mixed.push(results[index]);
        }
        appended = true;

        if (mixed.length >= limit) {
          break;
        }
      }
    }

    if (!appended) {
      break;
    }

    index += 1;
  }

  return mixed;
};

const DOUBAN_MOBILE_API_URL = "https://m.douban.com/rexxar/api/v2";
const DOUBAN_IMAGE_HEADERS = {
  Referer: "https://movie.douban.com/",
  "User-Agent": "Mozilla/5.0",
};
const DOUBAN_MOBILE_HEADERS = {
  Accept: "application/json, text/plain, */*",
  Referer: "https://m.douban.com/",
  "User-Agent": "Mozilla/5.0",
};
const DOUBAN_TAG_ALIASES: Record<string, string> = {
  hot: "热门",
  latest: "最新",
};
const DOUBAN_MOBILE_COLLECTIONS: Record<"movie" | "tv", Record<string, string>> = {
  movie: {
    热门: "movie_hot_gaia",
    最新: "movie_latest",
    经典: "movie_classic",
    豆瓣高分: "movie_score",
    动作: "movie_action",
    喜剧: "movie_comedy",
    爱情: "movie_love",
    科幻: "movie_scifi",
    top250: "movie_top250",
  },
  tv: {
    热门: "tv_hot",
    美剧: "tv_american",
    韩剧: "tv_korean",
    日剧: "tv_japanese",
    国产剧: "tv_domestic",
    日本动画: "tv_animation",
    综艺: "tv_variety_show",
    纪录片: "tv_documentary",
  },
};

const normalizeDoubanTag = (tag: string) => DOUBAN_TAG_ALIASES[tag] || tag;
const isDoubanImageUrl = (url: string) => /^https?:\/\/img\d+\.doubanio\.com\//.test(url);

const buildDoubanMobileCollectionUrl = (collectionId: string, pageSize: number, pageStart: number) => {
  return `${DOUBAN_MOBILE_API_URL}/subject_collection/${collectionId}/items?start=${pageStart}&count=${pageSize}&items_only=1&for_mobile=1`;
};

const buildDoubanMobileRecommendUrl = (type: "movie" | "tv", tag: string, pageSize: number, pageStart: number) => {
  const selectedCategories = encodeURIComponent("{}");
  return `${DOUBAN_MOBILE_API_URL}/${type}/recommend?refresh=0&start=${pageStart}&count=${pageSize}&selected_categories=${selectedCategories}&uncollect=false&tags=${encodeURIComponent(tag)}&for_mobile=1`;
};

const getDoubanPoster = (subject: any) => {
  if (typeof subject?.cover === "string") {
    return subject.cover;
  }
  if (typeof subject?.cover?.url === "string") {
    return subject.cover.url;
  }
  if (typeof subject?.pic?.normal === "string") {
    return subject.pic.normal;
  }
  if (typeof subject?.pic?.large === "string") {
    return subject.pic.large;
  }
  if (typeof subject?.cover_url === "string") {
    return subject.cover_url;
  }
  return "";
};

const getDoubanRate = (subject: any) => {
  if (typeof subject?.rate === "string") {
    return subject.rate;
  }
  const ratingValue = subject?.rating?.value;
  if (typeof ratingValue === "number" || typeof ratingValue === "string") {
    return String(ratingValue);
  }
  return undefined;
};

const mapDoubanMobileSubject = (subject: any, expectedType: "movie" | "tv"): DoubanItem | null => {
  if (subject?.type && subject.type !== expectedType) {
    return null;
  }

  const title = typeof subject?.title === "string" ? subject.title.trim() : "";
  if (!title) {
    return null;
  }

  return {
    id: subject?.id !== undefined ? String(subject.id) : title,
    title,
    poster: getDoubanPoster(subject),
    rate: getDoubanRate(subject),
    year: typeof subject?.year === "string" ? subject.year : undefined,
    episodes: [],
  };
};

const fetchDoubanMobileCollection = async (
  type: "movie" | "tv",
  tag: string,
  pageSize: number,
  pageStart: number
): Promise<DoubanItem[]> => {
  const collectionId = DOUBAN_MOBILE_COLLECTIONS[type][tag];
  if (!collectionId) {
    return [];
  }

  const response = await fetch(buildDoubanMobileCollectionUrl(collectionId, pageSize, pageStart), {
    headers: DOUBAN_MOBILE_HEADERS,
  });

  if (!response.ok) {
    return [];
  }

  const data = await response.json();
  const subjects: any[] = Array.isArray(data?.subject_collection_items) ? data.subject_collection_items : [];
  return subjects.map((subject) => mapDoubanMobileSubject(subject, type)).filter((item): item is DoubanItem => Boolean(item));
};

const fetchDoubanMobileRecommendations = async (
  type: "movie" | "tv",
  tag: string,
  pageSize: number,
  pageStart: number
): Promise<DoubanItem[]> => {
  const response = await fetch(buildDoubanMobileRecommendUrl(type, tag, pageSize, pageStart), {
    headers: DOUBAN_MOBILE_HEADERS,
  });

  if (!response.ok) {
    return [];
  }

  const data = await response.json();
  const subjects: any[] = Array.isArray(data?.items) ? data.items : [];
  return subjects.map((subject) => mapDoubanMobileSubject(subject, type)).filter((item): item is DoubanItem => Boolean(item));
};

const fetchDoubanSubjects = async (
  type: "movie" | "tv",
  tag: string,
  pageSize: number,
  pageStart: number
): Promise<DoubanItem[]> => {
  const normalizedTag = normalizeDoubanTag(tag);
  const collectionItems = await fetchDoubanMobileCollection(type, normalizedTag, pageSize, pageStart);
  if (collectionItems.length > 0) {
    return collectionItems;
  }

  return fetchDoubanMobileRecommendations(type, normalizedTag, pageSize, pageStart);
};

export class API {
  public baseURL: string = "";
  private subscribedVideoSources: VideoSourceSubscriptionSource[] = [];

  constructor(baseURL?: string) {
    if (baseURL) {
      this.baseURL = baseURL;
    }
  }

  public setBaseUrl(url: string) {
    this.baseURL = url;
  }

  public setSubscribedVideoSources(sources: VideoSourceSubscriptionSource[]) {
    this.subscribedVideoSources = sources;
  }

  private hasSubscribedVideoSources() {
    return this.subscribedVideoSources.length > 0;
  }

  private async _fetch(url: string, options: RequestInit = {}): Promise<Response> {
    if (!this.baseURL) {
      throw new Error("API_URL_NOT_SET");
    }

    const response = await fetch(`${this.baseURL}${url}`, options);

    if (response.status === 401) {
      throw new Error("UNAUTHORIZED");
    }

    if (!response.ok) {
      throw new Error(`HTTP error! status: ${response.status}`);
    }

    return response;
  }

  async login(username?: string | undefined, password?: string): Promise<{ ok: boolean }> {
    const response = await this._fetch("/api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password }),
    });

    // 存储cookie到AsyncStorage
    const cookies = response.headers.get("Set-Cookie");
    if (cookies) {
      await AsyncStorage.setItem("authCookies", cookies);
    }

    return response.json();
  }

  async logout(): Promise<{ ok: boolean }> {
    const response = await this._fetch("/api/logout", {
      method: "POST",
    });
    await AsyncStorage.setItem("authCookies", '');
    return response.json();
  }

  async getServerConfig(): Promise<ServerConfig> {
    const response = await this._fetch("/api/server-config");
    return response.json();
  }

  async getFavorites(key?: string): Promise<Record<string, Favorite> | Favorite | null> {
    const url = key ? `/api/favorites?key=${encodeURIComponent(key)}` : "/api/favorites";
    const response = await this._fetch(url);
    return response.json();
  }

  async addFavorite(key: string, favorite: Omit<Favorite, "save_time">): Promise<{ success: boolean }> {
    const response = await this._fetch("/api/favorites", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ key, favorite }),
    });
    return response.json();
  }

  async deleteFavorite(key?: string): Promise<{ success: boolean }> {
    const url = key ? `/api/favorites?key=${encodeURIComponent(key)}` : "/api/favorites";
    const response = await this._fetch(url, { method: "DELETE" });
    return response.json();
  }

  async getPlayRecords(): Promise<Record<string, PlayRecord>> {
    const response = await this._fetch("/api/playrecords");
    return response.json();
  }

  async savePlayRecord(key: string, record: Omit<PlayRecord, "save_time">): Promise<{ success: boolean }> {
    const response = await this._fetch("/api/playrecords", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ key, record }),
    });
    return response.json();
  }

  async deletePlayRecord(key?: string): Promise<{ success: boolean }> {
    const url = key ? `/api/playrecords?key=${encodeURIComponent(key)}` : "/api/playrecords";
    const response = await this._fetch(url, { method: "DELETE" });
    return response.json();
  }

  async getSearchHistory(): Promise<string[]> {
    const response = await this._fetch("/api/searchhistory");
    return response.json();
  }

  async addSearchHistory(keyword: string): Promise<string[]> {
    const response = await this._fetch("/api/searchhistory", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ keyword }),
    });
    return response.json();
  }

  async deleteSearchHistory(keyword?: string): Promise<{ success: boolean }> {
    const url = keyword ? `/api/searchhistory?keyword=${encodeURIComponent(keyword)}` : "/api/searchhistory";
    const response = await this._fetch(url, { method: "DELETE" });
    return response.json();
  }

  getImageProxyUrl(imageUrl: string): string {
    if (this.hasSubscribedVideoSources() || !this.baseURL) {
      return imageUrl;
    }
    return `${this.baseURL}/api/image-proxy?url=${encodeURIComponent(imageUrl)}`;
  }

  getImageSource(imageUrl: string): ImageSource {
    const uri = this.getImageProxyUrl(imageUrl);
    return isDoubanImageUrl(uri) ? { uri, headers: DOUBAN_IMAGE_HEADERS } : { uri };
  }

  async getDoubanData(
    type: "movie" | "tv",
    tag: string,
    pageSize: number = 16,
    pageStart: number = 0
  ): Promise<DoubanResponse> {
    if (this.hasSubscribedVideoSources() && !this.baseURL) {
      try {
        const doubanList = await fetchDoubanSubjects(type, tag, pageSize, pageStart);
        if (doubanList.length > 0) {
          return { code: 0, message: "", list: doubanList };
        }
      } catch {
        // Web builds may hit Douban CORS; native TV falls back only when Douban is unavailable.
      }

      const settledResults = await Promise.allSettled(
        this.subscribedVideoSources.map((source) => fetchAppleCmsHomePage(source, type, tag, pageStart, pageSize))
      );
      const sourceResults = settledResults.map((result) => (result.status === "fulfilled" ? result.value : []));
      const list = mixSourceResults(sourceResults, pageSize, getVideoDedupeKey);
      return { code: 0, message: "", list };
    }

    const url = `/api/douban?type=${type}&tag=${encodeURIComponent(tag)}&pageSize=${pageSize}&pageStart=${pageStart}`;
    const response = await this._fetch(url);
    return response.json();
  }

  async searchVideos(query: string): Promise<{ results: SearchResult[] }> {
    if (this.hasSubscribedVideoSources()) {
      const settledResults = await Promise.allSettled(
        this.subscribedVideoSources.map((source) => searchAppleCmsSource(source, query))
      );
      const results = settledResults.flatMap((result) => (result.status === "fulfilled" ? result.value : []));
      return { results };
    }

    const url = `/api/search?q=${encodeURIComponent(query)}`;
    const response = await this._fetch(url);
    return response.json();
  }

  async searchVideo(query: string, resourceId: string, signal?: AbortSignal, id?: string): Promise<{ results: SearchResult[] }> {
    if (this.hasSubscribedVideoSources()) {
      const source = findVideoSource({ sources: this.subscribedVideoSources }, resourceId);
      if (!source) {
        return { results: [] };
      }
      if (id) {
        const results = await getAppleCmsSourceResult(source, id, signal);
        return { results };
      }
      const results = await searchAppleCmsSource(source, query, signal);
      return { results: results.filter((item) => item.title === query) };
    }

    const url = `/api/search/one?q=${encodeURIComponent(query)}&resourceId=${encodeURIComponent(resourceId)}`;
    const response = await this._fetch(url, { signal });
    const { results } = await response.json();
    return { results: results.filter((item: any) => item.title === query )};
  }

  async getResources(signal?: AbortSignal): Promise<ApiSite[]> {
    if (this.hasSubscribedVideoSources()) {
      return videoSourceConfigToApiSites({ sources: this.subscribedVideoSources });
    }

    const url = `/api/search/resources`;
    const response = await this._fetch(url, { signal });
    return response.json();
  }

  async getSourceCategories(source: string, signal?: AbortSignal): Promise<VideoSourceCategory[]> {
    if (this.hasSubscribedVideoSources()) {
      const subscribedSource = findVideoSource({ sources: this.subscribedVideoSources }, source);
      if (!subscribedSource) {
        throw new Error("VIDEO_SOURCE_NOT_FOUND");
      }
      return fetchAppleCmsCategories(subscribedSource, signal);
    }

    const response = await this._fetch(`/api/source-search/categories?source=${encodeURIComponent(source)}`, { signal });
    const data = await response.json();
    return data.categories || data;
  }

  async getSourceVideos(
    source: string,
    categoryId: string,
    page: number = 1,
    pageSize: number = 20,
    signal?: AbortSignal
  ): Promise<VideoSourceCategoryVideosResponse> {
    if (this.hasSubscribedVideoSources()) {
      const subscribedSource = findVideoSource({ sources: this.subscribedVideoSources }, source);
      if (!subscribedSource) {
        throw new Error("VIDEO_SOURCE_NOT_FOUND");
      }
      return fetchAppleCmsCategoryVideos(subscribedSource, categoryId, page, pageSize, signal);
    }

    const response = await this._fetch(
      `/api/source-search/videos?source=${encodeURIComponent(source)}&categoryId=${encodeURIComponent(categoryId)}&page=${page}`,
      { signal }
    );
    return response.json();
  }

  async getVideoDetail(source: string, id: string): Promise<VideoDetail> {
    if (this.hasSubscribedVideoSources()) {
      const subscribedSource = findVideoSource({ sources: this.subscribedVideoSources }, source);
      if (!subscribedSource) {
        throw new Error("VIDEO_SOURCE_NOT_FOUND");
      }
      return getAppleCmsDetail(subscribedSource, id);
    }

    const url = `/api/detail?source=${source}&id=${id}`;
    const response = await this._fetch(url);
    return response.json();
  }
}

// 默认实例
export let api = new API();
