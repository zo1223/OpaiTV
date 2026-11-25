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
import { isAdultCategoryHidden } from "./adultCategoryFilter";
import { applySourceModes, isSourceOff, normalizeSourceModes, SourceModeMap } from "./sourceMode";
import {
  IncrementalSearchSnapshot,
  IncrementalSearchSource,
  runIncrementalSearch,
} from "./incrementalSearch";

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
  /** 源站分类名 / 题材标签:成人内容(18+)按分类过滤用 */
  type_name?: string;
  class?: string;
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
  /** 收藏时条目的分类(type_name 与 class 用逗号拼接)。成人内容开关关闭时按它过滤;旧数据没有该字段 */
  type_name?: string;
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
  /** 保存记录时条目的分类(type_name 与 class 用逗号拼接)。成人内容开关关闭时按它过滤;旧数据没有该字段 */
  type_name?: string;
}

export interface ApiSite {
  key: string;
  api: string;
  name: string;
  detail?: string;
  adult?: boolean;
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

// 单源搜索的标题匹配,与首页海报徽标(homeStore.titlesMatch)、详情页播放源列表
// (detailStore.titlesMatchQuery)同口径:忽略空白与大小写。这里若用严格相等,
// 徽标数进去的源会在详情页之前被滤掉(海报显示"可播",点进去却"未找到播放源")
const titleMatchesQuery = (title: unknown, query: string) => {
  const normalizedQuery = normalizeDedupeValue(query);
  return normalizedQuery !== "" && normalizeDedupeValue(title) === normalizedQuery;
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

// 订阅源聚合请求(搜索/首页)的单源时限。RN 的 OkHttp 连接与读取超时都是 0(不限时),
// 一个连得上却不回包的源会让 Promise.allSettled 一直等下去,整个搜索/首页永远转圈。
const SOURCE_REQUEST_TIMEOUT_MS = 15_000;

const withSourceTimeout = async <T>(run: (signal: AbortSignal) => Promise<T>): Promise<T> => {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), SOURCE_REQUEST_TIMEOUT_MS);
  try {
    return await run(controller.signal);
  } finally {
    clearTimeout(timer);
  }
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

/** 条目上可用于成人分类判断的字段。 */
export interface AdultFilterableItem {
  source?: string | null;
  type_name?: string | null;
  class?: string | null;
}

export class API {
  public baseURL: string = "";
  private subscribedVideoSources: VideoSourceSubscriptionSource[] = [];
  // 成人内容开关(设置页),默认关闭:标记为 adult 的源对所有取数入口不可见
  private showAdultContent = false;
  // 片源模式(稳定/影视/全量/关闭),按源 key;没有条目的源是全量。见 services/sourceMode
  private sourceModes: SourceModeMap = {};

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

  /** 片源模式立即生效:下一次搜索/首页取数就按新模式 */
  public setSourceModes(modes: SourceModeMap | null | undefined) {
    this.sourceModes = normalizeSourceModes(modes);
  }

  public getSourceModes(): SourceModeMap {
    return { ...this.sourceModes };
  }

  // 搜索与首页用的源列表:在成人过滤之后的可见源里,再去掉模式为“关闭”的源
  private getActiveVideoSources() {
    return this.getVisibleVideoSources().filter((source) => !isSourceOff(this.sourceModes, source.key));
  }

  // 成人过滤之后,再按各源的模式收窄(关闭 / 只留影视类 / 只留最稳线路)
  private modeItems<T extends AdultFilterableItem>(items: T[] | null | undefined): T[] {
    return applySourceModes(this.visibleItems(items), this.sourceModes);
  }

  public setShowAdultContent(enabled: boolean) {
    this.showAdultContent = enabled;
  }

  /** 该源是成人源且成人内容开关关闭。播放记录、收藏等按源 key 保存的数据用它过滤 */
  public isAdultSourceHidden(sourceKey: string) {
    return !this.showAdultContent && this.subscribedVideoSources.some((source) => source.adult && source.key === sourceKey);
  }

  /**
   * 该分类(type_name / class)是成人分类且成人内容开关关闭。
   * 关键词只在 services/adultCategoryFilter.ts 一处维护,调用点不要自己拼。
   */
  public isAdultCategoryHidden(typeName?: string | null, className?: string | null) {
    return isAdultCategoryHidden(this.showAdultContent, typeName, className);
  }

  /** 条目是否应隐藏:来自被隐藏的成人源,或属于成人分类(开关关闭时)。所有内容入口落地前都用它 */
  public isAdultItemHidden(item: AdultFilterableItem | null | undefined) {
    if (!item) {
      return false;
    }
    return (
      (typeof item.source === "string" && this.isAdultSourceHidden(item.source)) ||
      this.isAdultCategoryHidden(item.type_name, item.class)
    );
  }

  private visibleItems<T extends AdultFilterableItem>(items: T[] | null | undefined): T[] {
    return Array.isArray(items) ? items.filter((item) => !this.isAdultItemHidden(item)) : [];
  }

  // 是否处于订阅模式仍按全部源判断:成人源被隐藏不应让请求改走后端
  private hasSubscribedVideoSources() {
    return this.subscribedVideoSources.length > 0;
  }

  // 所有取数入口(源列表、首页、搜索、分类浏览、详情)只能用这份列表,不直接用 subscribedVideoSources
  private getVisibleVideoSources() {
    return this.showAdultContent
      ? this.subscribedVideoSources
      : this.subscribedVideoSources.filter((source) => !source.adult);
  }

  // 请求在途时开关可能被关掉(或该源刚被标记为成人源):结果落地前按当前状态再确认一次
  private async unlessHidden<T>(sourceKey: string, request: Promise<T>): Promise<T> {
    const result = await request;
    if (this.isAdultSourceHidden(sourceKey)) {
      throw new Error("VIDEO_SOURCE_NOT_FOUND");
    }
    return result;
  }

  // 成人分类的详情在开关关闭时按“源不存在”处理,与被隐藏的成人源一致
  private unlessAdultCategory<T extends AdultFilterableItem>(detail: T): T {
    if (detail && this.isAdultCategoryHidden(detail.type_name, detail.class)) {
      throw new Error("VIDEO_SOURCE_NOT_FOUND");
    }
    return detail;
  }

  // 分类浏览:成人分类不进入分类列表(开关关闭时)
  private visibleCategories(categories: VideoSourceCategory[]): VideoSourceCategory[] {
    return Array.isArray(categories)
      ? categories.filter((category) => !this.isAdultCategoryHidden(category?.name))
      : categories;
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
        this.getActiveVideoSources().map((source) =>
          withSourceTimeout((signal) => fetchAppleCmsHomePage(source, type, tag, pageStart, pageSize, signal))
        )
      );
      const sourceResults = settledResults.map((result) =>
        result.status === "fulfilled" ? this.modeItems(result.value) : []
      );
      const list = mixSourceResults(sourceResults, pageSize, getVideoDedupeKey);
      return { code: 0, message: "", list };
    }

    const url = `/api/douban?type=${type}&tag=${encodeURIComponent(tag)}&pageSize=${pageSize}&pageStart=${pageStart}`;
    const response = await this._fetch(url);
    const data: DoubanResponse = await response.json();
    return Array.isArray(data?.list) ? { ...data, list: this.modeItems(data.list) } : data;
  }

  /**
   * 跨片源增量搜索:逐源并发,每个源一返回就通过 onUpdate 回报进度和目前为止的结果。
   * 参与的源 = 成人过滤后可见、且模式不是“关闭”的源;每个源的结果先过成人过滤再按该源模式收窄。
   * 后端模式下源列表取自后端,逐源走 /api/search/one;拿不到源列表时退回一次性的 /api/search。
   */
  async searchVideosIncremental(
    query: string,
    onUpdate: (snapshot: IncrementalSearchSnapshot<SearchResult>) => void,
    isCancelled?: () => boolean
  ): Promise<IncrementalSearchSnapshot<SearchResult>> {
    if (this.hasSubscribedVideoSources()) {
      const sources = this.getActiveVideoSources();
      return runIncrementalSearch<SearchResult>({
        sources: sources.map((source) => ({ key: source.key, name: source.name || source.key })),
        searchOne: async (target, signal) => {
          const source = sources.find((item) => item.key === target.key);
          const found = source ? await searchAppleCmsSource(source, query, signal) : [];
          // 请求在途时该源可能刚被关掉或被隐藏:落地前按当前状态再确认一次
          return this.isAdultSourceHidden(target.key) || isSourceOff(this.sourceModes, target.key)
            ? []
            : this.modeItems(found);
        },
        onUpdate,
        isCancelled,
        timeoutMs: SOURCE_REQUEST_TIMEOUT_MS,
      });
    }

    let sites: IncrementalSearchSource[] = [];
    try {
      sites = (await this.getResources()).map((site) => ({ key: site.key, name: site.name || site.key }));
    } catch (error) {
      if (error instanceof Error && (error.message === "API_URL_NOT_SET" || error.message === "UNAUTHORIZED")) {
        throw error;
      }
    }
    if (sites.length === 0) {
      return runIncrementalSearch<SearchResult>({
        sources: [{ key: "__all__", name: "全部片源" }],
        searchOne: async () => (await this.searchVideos(query)).results,
        onUpdate,
        isCancelled,
        timeoutMs: SOURCE_REQUEST_TIMEOUT_MS * 2,
      });
    }
    return runIncrementalSearch<SearchResult>({
      sources: sites,
      searchOne: async (target, signal) => {
        const url = `/api/search/one?q=${encodeURIComponent(query)}&resourceId=${encodeURIComponent(target.key)}`;
        const response = await this._fetch(url, { signal });
        const data = await response.json();
        return this.modeItems<SearchResult>(data?.results);
      },
      onUpdate,
      isCancelled,
      timeoutMs: SOURCE_REQUEST_TIMEOUT_MS,
    });
  }

  async searchVideos(query: string): Promise<{ results: SearchResult[] }> {
    if (this.hasSubscribedVideoSources()) {
      const settledResults = await Promise.allSettled(
        this.getActiveVideoSources().map((source) =>
          withSourceTimeout((signal) => searchAppleCmsSource(source, query, signal))
        )
      );
      const results = this.modeItems(
        settledResults.flatMap((result) => (result.status === "fulfilled" ? result.value : []))
      );
      return { results };
    }

    const url = `/api/search?q=${encodeURIComponent(query)}`;
    const response = await this._fetch(url);
    const data = await response.json();
    return { ...data, results: this.modeItems<SearchResult>(data?.results) };
  }

  async searchVideo(query: string, resourceId: string, signal?: AbortSignal, id?: string): Promise<{ results: SearchResult[] }> {
    if (this.hasSubscribedVideoSources()) {
      // 带 id 是打开/续播某一条已知条目(不是搜索):片源模式不拦它,只有成人过滤照常
      const source = findVideoSource(
        { sources: id ? this.getVisibleVideoSources() : this.getActiveVideoSources() },
        resourceId
      );
      if (!source) {
        return { results: [] };
      }
      const results = id
        ? await getAppleCmsSourceResult(source, id, signal)
        : (await searchAppleCmsSource(source, query, signal)).filter((item) => titleMatchesQuery(item.title, query));
      if (this.isAdultSourceHidden(source.key)) {
        return { results: [] };
      }
      return { results: id ? this.visibleItems(results) : this.modeItems(results) };
    }

    const url = `/api/search/one?q=${encodeURIComponent(query)}&resourceId=${encodeURIComponent(resourceId)}`;
    const response = await this._fetch(url, { signal });
    const { results } = await response.json();
    if (isSourceOff(this.sourceModes, resourceId)) {
      return { results: [] };
    }
    return { results: this.modeItems<SearchResult>(results).filter((item) => titleMatchesQuery(item.title, query)) };
  }

  async getResources(signal?: AbortSignal): Promise<ApiSite[]> {
    if (this.hasSubscribedVideoSources()) {
      return videoSourceConfigToApiSites({ sources: this.getActiveVideoSources() });
    }

    const url = `/api/search/resources`;
    const response = await this._fetch(url, { signal });
    const sites = await response.json();
    return Array.isArray(sites) ? sites.filter((site) => !isSourceOff(this.sourceModes, site?.key)) : sites;
  }

  /** 设置页“片源模式”用的源列表:包含模式为“关闭”的源(否则关掉之后就找不到它、没法再打开);成人源照常按开关隐藏 */
  async getModeConfigurableSources(signal?: AbortSignal): Promise<ApiSite[]> {
    if (this.hasSubscribedVideoSources()) {
      return videoSourceConfigToApiSites({ sources: this.getVisibleVideoSources() });
    }
    const response = await this._fetch(`/api/search/resources`, { signal });
    const sites = await response.json();
    return Array.isArray(sites) ? sites : [];
  }

  async getSourceCategories(source: string, signal?: AbortSignal): Promise<VideoSourceCategory[]> {
    if (this.hasSubscribedVideoSources()) {
      const subscribedSource = findVideoSource({ sources: this.getVisibleVideoSources() }, source);
      if (!subscribedSource) {
        throw new Error("VIDEO_SOURCE_NOT_FOUND");
      }
      const categories = await this.unlessHidden(source, fetchAppleCmsCategories(subscribedSource, signal));
      return this.visibleCategories(categories);
    }

    const response = await this._fetch(`/api/source-search/categories?source=${encodeURIComponent(source)}`, { signal });
    const data = await response.json();
    return this.visibleCategories(data.categories || data);
  }

  async getSourceVideos(
    source: string,
    categoryId: string,
    page: number = 1,
    pageSize: number = 20,
    signal?: AbortSignal
  ): Promise<VideoSourceCategoryVideosResponse> {
    if (this.hasSubscribedVideoSources()) {
      const subscribedSource = findVideoSource({ sources: this.getVisibleVideoSources() }, source);
      if (!subscribedSource) {
        throw new Error("VIDEO_SOURCE_NOT_FOUND");
      }
      const videos = await this.unlessHidden(
        source,
        fetchAppleCmsCategoryVideos(subscribedSource, categoryId, page, pageSize, signal)
      );
      return { ...videos, results: this.visibleItems(videos.results) };
    }

    const response = await this._fetch(
      `/api/source-search/videos?source=${encodeURIComponent(source)}&categoryId=${encodeURIComponent(categoryId)}&page=${page}`,
      { signal }
    );
    const data: VideoSourceCategoryVideosResponse = await response.json();
    return Array.isArray(data?.results) ? { ...data, results: this.visibleItems(data.results) } : data;
  }

  async getVideoDetail(source: string, id: string): Promise<VideoDetail> {
    if (this.hasSubscribedVideoSources()) {
      const subscribedSource = findVideoSource({ sources: this.getVisibleVideoSources() }, source);
      if (!subscribedSource) {
        throw new Error("VIDEO_SOURCE_NOT_FOUND");
      }
      return this.unlessAdultCategory(await this.unlessHidden(source, getAppleCmsDetail(subscribedSource, id)));
    }

    const url = `/api/detail?source=${source}&id=${id}`;
    const response = await this._fetch(url);
    return this.unlessAdultCategory<VideoDetail>(await response.json());
  }
}

// 默认实例
export let api = new API();
