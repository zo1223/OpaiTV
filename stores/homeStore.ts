import { create } from "zustand";
import { api, SearchResult, PlayRecord } from "@/services/api";
import { FavoriteManager, PlayRecordManager } from "@/services/storage";
import { buildRecommendations } from "@/services/recommendation";
import { loadRecommendationPool, rememberRecommendationCandidates } from "@/services/recommendationPool";
import { isSourceOff } from "@/services/sourceMode";
import { usePlayRecordUndoStore, playRecordKey } from "./playRecordUndoStore";
import useAuthStore from "./authStore";
import { useSettingsStore } from "./settingsStore";
import { expandSearchResultPlaybackSources } from "@/services/videoSourceSubscription";

export type PlayabilityStatus = "checking" | "playable" | "unavailable" | "unknown";

export type RowItem = (SearchResult | PlayRecord) & {
  id: string;
  source: string;
  title: string;
  poster: string;
  progress?: number;
  play_time?: number;
  lastPlayed?: number;
  episodeIndex?: number;
  sourceName?: string;
  totalEpisodes?: number;
  year?: string;
  rate?: string;
  playabilityStatus?: PlayabilityStatus;
  playableSourceCount?: number;
  /** “为你推荐”里的推荐理由 */
  reason?: string;
};

export interface Category {
  title: string;
  type?: "movie" | "tv" | "record" | "recommend";
  tag?: string;
  tags?: string[];
}

const DOUBAN_MOVIE_TAGS = [
  "热门",
  "最新",
  "经典",
  "可播放",
  "豆瓣高分",
  "冷门佳片",
  "华语",
  "欧美",
  "韩国",
  "日本",
  "动作",
  "喜剧",
  "爱情",
  "科幻",
  "悬疑",
  "恐怖",
  "文艺",
];
const DOUBAN_TV_TAGS = ["热门", "美剧", "英剧", "韩剧", "日剧", "国产剧", "港剧", "日本动画", "综艺", "纪录片"];

// “为你推荐”:只在本机根据观看历史与收藏计算(见 services/recommendation),设置里可关,默认开
export const RECOMMEND_CATEGORY: Category = { title: "为你推荐", type: "recommend" };

const initialCategories: Category[] = [
  { title: "观看历史", type: "record" },
  RECOMMEND_CATEGORY,
  { title: "热门剧集", type: "tv", tag: "热门" },
  { title: "电视剧", type: "tv", tags: DOUBAN_TV_TAGS },
  {
    title: "电影",
    type: "movie",
    tags: DOUBAN_MOVIE_TAGS,
  },
  { title: "综艺", type: "tv", tag: "综艺" },
  { title: "豆瓣 Top250", type: "movie", tag: "top250" },
];

// 添加缓存项接口
interface CacheItem {
  data: RowItem[];
  timestamp: number;
  type: 'movie' | 'tv' | 'record' | 'recommend';
  hasMore: boolean;
  pageStart: number;
}

const CACHE_EXPIRE_TIME = 5 * 60 * 1000; // 5分钟过期
const MAX_CACHE_SIZE = 10; // 最大缓存容量
const MAX_ITEMS_PER_CACHE = 40; // 每个缓存最大条目数
// 每次请求的页大小。pageStart 以该值为固定步长推进,保证订阅源
// (按 pg 页码翻页,见 videoSourceSubscription)的页码严格单调,不会同页重复拉取
const HOME_PAGE_SIZE = 20;

const getCacheKey = (category: Category) => {
  return `${category.type || 'unknown'}-${category.title}-${category.tag || ''}`;
};

const isValidCache = (cacheItem: CacheItem) => {
  return Date.now() - cacheItem.timestamp < CACHE_EXPIRE_TIME;
};

const shouldProbePlayability = (item: RowItem) => item.source === "douban" && item.title.trim().length > 0;

const withInitialPlayability = (item: RowItem): RowItem =>
  shouldProbePlayability(item) ? { ...item, playabilityStatus: "checking", playableSourceCount: undefined } : item;

const normalizeTitle = (value: string) =>
  value.trim().toLowerCase().replace(/\s+/g, "");

const titlesMatch = (a: string, b: string) => {
  if (!a || !b) return false;
  if (a.trim() === b.trim()) return true;
  return normalizeTitle(a) === normalizeTitle(b);
};

/** 与详情页播放源列表同口径：标题匹配 + source/id/线路去重；空剧集也计入（TVBox 常 search 无 url）。 */
const getPlayableSourceCount = (title: string, results: SearchResult[]) => {
  const sources = new Set(
    results
      .flatMap(expandSearchResultPlaybackSources)
      .filter((result) => titlesMatch(result.title, title))
      .map((result) => `${result.source}${result.id}${result.playback_source || ""}`)
  );
  return sources.size;
};

interface HomeState {
  categories: Category[];
  selectedCategory: Category;
  contentData: RowItem[];
  loading: boolean;
  loadingMore: boolean;
  pageStart: number;
  hasMore: boolean;
  error: string | null;
  fetchInitialData: () => Promise<void>;
  loadMoreData: () => Promise<void>;
  probePlayability: (items: RowItem[]) => void;
  selectCategory: (category: Category) => void;
  refreshPlayRecords: () => Promise<void>;
  clearError: () => void;
  _fetchSeq: number;
}

// 内存缓存，应用生命周期内有效
const dataCache = new Map<string, CacheItem>();

// 观看历史按源 key 保存:成人内容开关关闭时,成人源与成人分类的记录不进入首页(列表和"是否有历史"的判断都用它)
const getVisiblePlayRecords = async () => {
  const records = await PlayRecordManager.getAll();
  // 待撤销删除(或正在删除)的记录同样不进入首页:重新取数不能把它带回来
  const { isHidden } = usePlayRecordUndoStore.getState();
  return Object.fromEntries(
    Object.entries(records).filter(
      ([key, record]) =>
        !isHidden(key) && !api.isAdultItemHidden({ source: key.split("+")[0], type_name: record?.type_name })
    )
  );
};

/**
 * 本地推荐:输入是本机的观看记录、收藏和首页已经加载过的条目(候选池),全程不为推荐发任何请求。
 * 成人内容照常按开关过滤;模式为“关闭”的片源不出现在推荐里。
 */
export const computeLocalRecommendations = async (): Promise<RowItem[]> => {
  const [records, favorites, pool] = await Promise.all([
    getVisiblePlayRecords().catch(() => ({} as Record<string, PlayRecord>)),
    FavoriteManager.getAll().catch(() => ({})),
    loadRecommendationPool(),
  ]);
  const modes = api.getSourceModes();
  const usable = (item: { source?: string; type_name?: string | null; class?: string | null }) =>
    !api.isAdultItemHidden(item) && !isSourceOff(modes, item.source);
  const favoriteEntries = Object.entries(favorites as Record<string, any>)
    .map(([key, favorite]) => {
      const [source, id] = key.split("+");
      return {
        title: favorite?.title ?? "",
        type_name: favorite?.type_name,
        source,
        id,
        poster: favorite?.cover,
        year: favorite?.year,
        source_name: favorite?.source_name,
      };
    })
    .filter((entry) => !api.isAdultItemHidden(entry));
  const recommendations = buildRecommendations({
    history: Object.values(records).map((record) => ({
      title: record.title,
      type_name: record.type_name,
      save_time: record.save_time,
    })),
    favorites: favoriteEntries,
    candidates: pool.filter(usable),
  });
  return recommendations.filter(usable).map(
    (item) =>
      ({
        id: item.id,
        source: item.source,
        title: item.title,
        poster: item.poster,
        year: item.year ?? "",
        rate: item.rate,
        sourceName: item.source_name,
        source_name: item.source_name ?? "",
        type_name: item.type_name ?? undefined,
        episodes: [],
        reason: item.reason,
      } as unknown as RowItem)
  );
};

const useHomeStore = create<HomeState>((set, get) => ({
  categories: initialCategories,
  selectedCategory: initialCategories[0],
  contentData: [],
  loading: true,
  loadingMore: false,
  pageStart: 0,
  hasMore: true,
  error: null,
  _fetchSeq: 0,

  fetchInitialData: async () => {
    // 新会话开始:作废所有在途请求的写入资格
    const seq = get()._fetchSeq + 1;
    set({ _fetchSeq: seq });

    const { apiBaseUrl } = useSettingsStore.getState();
    await useAuthStore.getState().checkLoginStatus(apiBaseUrl);
    if (get()._fetchSeq !== seq) return;

    const { selectedCategory } = get();
    const cacheKey = getCacheKey(selectedCategory);

    // 最近播放与本地推荐不缓存，始终实时计算
    if (selectedCategory.type === 'record' || selectedCategory.type === 'recommend') {
      set({ loading: true, contentData: [], pageStart: 0, hasMore: true, error: null, loadingMore: false });
      await get().loadMoreData();
      return;
    }

    // 检查缓存
    if (dataCache.has(cacheKey) && isValidCache(dataCache.get(cacheKey)!)) {
      const cachedData = dataCache.get(cacheKey)!;
      set({
        loading: false,
        loadingMore: false,
        contentData: cachedData.data,
        pageStart: cachedData.pageStart,
        hasMore: cachedData.hasMore,
        error: null
      });
      get().probePlayability(cachedData.data);
      return;
    }

    set({ loading: true, contentData: [], pageStart: 0, hasMore: true, error: null, loadingMore: false });
    await get().loadMoreData();
  },

  loadMoreData: async () => {
    const { selectedCategory, pageStart, loadingMore, hasMore, _fetchSeq: seq } = get();
    if (loadingMore || !hasMore) return;

    if (pageStart > 0) {
      set({ loadingMore: true });
    }

    try {
      if (selectedCategory.type === "record") {
        const records = await getVisiblePlayRecords();
        if (get()._fetchSeq !== seq) return;
        const rowItems = Object.entries(records)
          .map(([key, record]) => {
            const [source, id] = key.split("+");
            return {
              ...record,
              id,
              source,
              progress: record.total_time ? record.play_time / record.total_time : 0,
              poster: record.cover,
              sourceName: record.source_name,
              episodeIndex: record.index,
              totalEpisodes: record.total_episodes,
              lastPlayed: record.save_time,
              play_time: record.play_time,
            };
          })
          // .filter((record) => record.progress !== undefined && record.progress > 0 && record.progress < 1)
          .sort((a, b) => (b.lastPlayed || 0) - (a.lastPlayed || 0));

        set({ contentData: rowItems, hasMore: false });
      } else if (selectedCategory.type === "recommend") {
        const recommended = await computeLocalRecommendations();
        if (get()._fetchSeq !== seq) return;
        set({ contentData: recommended, hasMore: false });
      } else if (selectedCategory.type && selectedCategory.tag) {
        const result = await api.getDoubanData(
          selectedCategory.type,
          selectedCategory.tag,
          HOME_PAGE_SIZE,
          pageStart
        );
        if (get()._fetchSeq !== seq) return;

        const newItems = result.list.map((item) =>
          withInitialPlayability({
            ...item,
            id: item.id !== undefined ? String(item.id) : item.title,
            source: item.source || "douban",
            sourceName: item.source_name,
          } as RowItem)
        );

        // 首页加载过的条目记进本机的推荐候选池(只写本机存储)
        void rememberRecommendationCandidates(
          newItems.map((item) => ({
            id: item.id,
            source: item.source,
            title: item.title,
            poster: item.poster,
            year: item.year,
            rate: item.rate,
            source_name: (item as { source_name?: string }).source_name,
            type_name: (item as { type_name?: string }).type_name,
            class: (item as { class?: string }).class,
          })),
          [selectedCategory.tag, selectedCategory.title]
        ).catch(() => {});

        const cacheKey = getCacheKey(selectedCategory);
        // 固定步长推进,而不是按去重后的返回条数推进,保证订阅源页码单调不重复
        const nextPageStart = pageStart + HOME_PAGE_SIZE;

        if (pageStart === 0) {
          // 清理过期缓存
          for (const [key, value] of dataCache.entries()) {
            if (!isValidCache(value)) {
              dataCache.delete(key);
            }
          }

          // 如果缓存太大，删除最旧的项
          if (dataCache.size >= MAX_CACHE_SIZE) {
            const oldestKey = Array.from(dataCache.keys())[0];
            dataCache.delete(oldestKey);
          }

          // 限制缓存的数据条目数，但不限制显示的数据
          const cacheItems = newItems.slice(0, MAX_ITEMS_PER_CACHE);

          // 存储新缓存
          dataCache.set(cacheKey, {
            data: cacheItems,
            timestamp: Date.now(),
            type: selectedCategory.type,
            hasMore: true, // 始终为 true，因为我们允许继续加载
            pageStart: nextPageStart,
          });

          set({
            contentData: newItems, // 使用完整的新数据
            pageStart: nextPageStart,
            hasMore: result.list.length !== 0,
          });
          get().probePlayability(newItems);
        } else {
          // 追加前按 source+id 去重,订阅源跨页返回重复条目时不再无限重复
          const existingKeys = new Set(get().contentData.map((item) => `${item.source}+${item.id}`));
          const dedupedItems = newItems.filter((item) => !existingKeys.has(`${item.source}+${item.id}`));

          // 增量加载时更新缓存
          const existingCache = dataCache.get(cacheKey);
          // 只有这一页紧接在缓存内容之后(pageStart 对得上)且整页都放得下时才更新缓存:
          // 否则缓存的 pageStart 会越过没进缓存的条目(被截掉的,或之前放不下而没缓存的页),
          // 从缓存恢复再翻页时这些条目就再也加载不出来
          if (existingCache && existingCache.pageStart === pageStart) {
            const updatedData = [...existingCache.data, ...dedupedItems];
            if (updatedData.length <= MAX_ITEMS_PER_CACHE) {
              dataCache.set(cacheKey, {
                ...existingCache,
                data: updatedData,
                hasMore: true, // 始终为 true，因为我们允许继续加载
                pageStart: nextPageStart,
              });
            }
          }

          // 更新状态时使用所有数据
          set((state) => ({
            contentData: [...state.contentData, ...dedupedItems],
            pageStart: nextPageStart,
            hasMore: result.list.length !== 0,
          }));
          get().probePlayability(dedupedItems);
        }
      } else if (selectedCategory.tags) {
        // It's a container category, do not load content, but clear current content
        set({ contentData: [], hasMore: false });
      } else {
        set({ hasMore: false });
      }
    } catch (err: any) {
      if (get()._fetchSeq !== seq) return;
      let errorMessage = "加载失败，请重试";

      if (err.message === "API_URL_NOT_SET") {
        errorMessage = "请点击右上角设置按钮，配置您的服务器地址";
      } else if (err.message === "UNAUTHORIZED") {
        errorMessage = "认证失败，请重新登录";
        useAuthStore.setState({ isLoggedIn: false, isLoginModalVisible: true });
      } else if (err.message.includes("Network")) {
        errorMessage = "网络连接失败，请检查网络连接";
      } else if (err.message.includes("timeout")) {
        errorMessage = "请求超时，请检查网络或服务器状态";
      } else if (err.message.includes("404")) {
        errorMessage = "服务器API路径不正确，请检查服务器配置";
      } else if (err.message.includes("500")) {
        errorMessage = "服务器内部错误，请联系管理员";
      } else if (err.message.includes("403")) {
        errorMessage = "访问被拒绝，请检查权限设置";
      }

      set({ error: errorMessage });
    } finally {
      // 已被新会话取代时不得触碰 loading 标志,否则会关掉新会话的加载态
      if (get()._fetchSeq === seq) {
        set({ loading: false, loadingMore: false });
      }
    }
  },

  probePlayability: (items: RowItem[]) => {
    items
      .filter(shouldProbePlayability)
      // 已有明确结论(可播/不可播)的条目不再重复探测,避免缓存恢复/刷新时的探测风暴
      .filter(
        (item) =>
          item.playabilityStatus === undefined ||
          item.playabilityStatus === "checking" ||
          item.playabilityStatus === "unknown"
      )
      .forEach((item) => {
        void api
          .searchVideos(item.title)
          .then(({ results }) => {
            const playableSourceCount = getPlayableSourceCount(item.title, results);
            const status: PlayabilityStatus = playableSourceCount > 0 ? "playable" : "unavailable";
            const count = playableSourceCount > 0 ? playableSourceCount : undefined;
            const applyStatus = (current: RowItem) =>
              current.source === "douban" && current.id === item.id && current.title === item.title
                ? { ...current, playabilityStatus: status, playableSourceCount: count }
                : current;
            set((state) => ({ contentData: state.contentData.map(applyStatus) }));
            // 回写缓存,缓存命中恢复时才能带上已有结论
            for (const [key, cacheItem] of dataCache.entries()) {
              dataCache.set(key, { ...cacheItem, data: cacheItem.data.map(applyStatus) });
            }
          })
          .catch(() => {
            const applyStatus = (current: RowItem) =>
              current.source === "douban" && current.id === item.id && current.title === item.title
                ? { ...current, playabilityStatus: "unknown" as PlayabilityStatus, playableSourceCount: undefined }
                : current;
            set((state) => ({ contentData: state.contentData.map(applyStatus) }));
            for (const [key, cacheItem] of dataCache.entries()) {
              dataCache.set(key, { ...cacheItem, data: cacheItem.data.map(applyStatus) });
            }
          });
      });
  },

  selectCategory: (category: Category) => {
    const currentCategory = get().selectedCategory;

    if (currentCategory.title !== category.title || currentCategory.tag !== category.tag) {
      // 只负责切换状态并作废在途请求;数据拉取统一由 HomeScreen 的 effect
      // 触发一次 fetchInitialData,避免与 effect 双重拉取和双倍可播性探测
      set((state) => ({
        selectedCategory: category,
        contentData: [],
        pageStart: 0,
        hasMore: true,
        error: null,
        loading: true,
        loadingMore: false,
        _fetchSeq: state._fetchSeq + 1,
      }));
    }
  },

  refreshPlayRecords: async () => {
    const { apiBaseUrl } = useSettingsStore.getState();
    await useAuthStore.getState().checkLoginStatus(apiBaseUrl);
    let records: Awaited<ReturnType<typeof PlayRecordManager.getAll>>;
    try {
      records = await getVisiblePlayRecords();
    } catch (error) {
      const { isLoggedIn } = useAuthStore.getState();
      if (!isLoggedIn) {
        set((state) => {
          const recordCategoryExists = state.categories.some((c) => c.type === "record");
          if (recordCategoryExists) {
            const newCategories = state.categories.filter((c) => c.type !== "record");
            if (state.selectedCategory.type === "record") {
              get().selectCategory(newCategories[0] || null);
            }
            return { categories: newCategories };
          }
          return {};
        });
        return;
      }
      throw error;
    }
    const hasRecords = Object.keys(records).length > 0;
    set((state) => {
      const recordCategoryExists = state.categories.some((c) => c.type === "record");
      if (hasRecords && !recordCategoryExists) {
        return { categories: [initialCategories[0], ...state.categories] };
      }
      if (!hasRecords && recordCategoryExists) {
        const newCategories = state.categories.filter((c) => c.type !== "record");
        if (state.selectedCategory.type === "record") {
          get().selectCategory(newCategories[0] || null);
        }
        return { categories: newCategories };
      }
      return {};
    });

    // 记录分类需要实时刷新;其他分类的内容与播放记录无关,重复拉取只会
    // 造成双重请求与探测风暴。error 状态下重试一次(如登录后恢复)。
    if (get().selectedCategory?.type === "record" || get().error) {
      await get().fetchInitialData();
    }
  },

  clearError: () => {
    set({ error: null });
  },
}));

// 被隐藏的成人源发生变化(开关切换,或成人源被添加/删除)后,首页不能留着按旧状态取到的内容:
// 缓存整体作废(两个方向都要重新取),已显示的条目去掉被隐藏源的内容,豆瓣条目的"可播放源数"重新探测。
// 取数层(api)在 store 通知订阅者之前已经切到新状态。按内容比较而不是按引用比较:
// loadSettings 每次都会换一个新的源数组,不能因此每次打开设置页都清缓存、重新探测。
// 开关本身也算进签名:成人分类过滤不依赖成人源,没有任何成人源时切换开关同样要立即重算。
const getHiddenAdultSourceKeys = (state: ReturnType<typeof useSettingsStore.getState>) =>
  state.showAdultContent
    ? "on"
    : "off\u001f" +
      (state.subscribedVideoSources || [])
        .filter((source) => source.adult)
        .map((source) => source.key)
        .join("\u001f");

useSettingsStore.subscribe((state, previous) => {
  if (getHiddenAdultSourceKeys(state) === getHiddenAdultSourceKeys(previous)) {
    return;
  }
  dataCache.clear();
  const contentData = useHomeStore
    .getState()
    .contentData.filter((item) => !api.isAdultItemHidden(item))
    .map(withInitialPlayability);
  useHomeStore.setState({ contentData });
  useHomeStore.getState().probePlayability(contentData);
});

// “为你推荐”开关:关掉后分类立即消失(正选中它时切到下一个分类),打开后回到“观看历史”之后。
// 片源模式变化:首页缓存按旧模式取的,整体作废;已显示的条目去掉刚被关闭的源。
const getSourceModeSignature = (state: ReturnType<typeof useSettingsStore.getState>) =>
  Object.entries(state.sourceModes || {})
    .map(([key, mode]) => `${key}=${mode}`)
    .sort()
    .join("\u001f");

export const syncRecommendCategory = (enabled: boolean) => {
  const state = useHomeStore.getState();
  const exists = state.categories.some((category) => category.type === "recommend");
  if (enabled && !exists) {
    const recordIndex = state.categories.findIndex((category) => category.type === "record");
    const categories = [...state.categories];
    categories.splice(recordIndex + 1, 0, RECOMMEND_CATEGORY);
    useHomeStore.setState({ categories });
  } else if (!enabled && exists) {
    const categories = state.categories.filter((category) => category.type !== "recommend");
    useHomeStore.setState({ categories });
    if (state.selectedCategory?.type === "recommend") {
      const next = categories.find((category) => category.type !== "record") || categories[0];
      if (next) {
        useHomeStore.getState().selectCategory(next);
      }
    }
  }
};

useSettingsStore.subscribe((state, previous) => {
  if (state.recommendationsEnabled !== previous.recommendationsEnabled) {
    syncRecommendCategory(state.recommendationsEnabled !== false);
  }
  if (getSourceModeSignature(state) !== getSourceModeSignature(previous)) {
    dataCache.clear();
    const modes = state.sourceModes || {};
    const home = useHomeStore.getState();
    // 观看历史不受片源模式影响(关闭只管搜索与首页聚合)
    if (home.selectedCategory?.type !== "record") {
      const contentData = home.contentData.filter((item) => !isSourceOff(modes, item.source));
      if (contentData.length !== home.contentData.length) {
        useHomeStore.setState({ contentData });
      }
    }
  }
});

// 观看记录的"删除 + 撤销":
// - 记录被标记为待删除时,立即从已显示的"最近播放"列表里去掉(不重新取数,避免整页闪一下);列表因此变空时刷新一次,让"最近播放"分类一起消失。
// - 记录重新可见(撤销 / 删除失败 / 待删期间又播放过)时重新取数。
usePlayRecordUndoStore.subscribe((state, previous) => {
  const home = useHomeStore.getState();
  const refresh = () => {
    home.refreshPlayRecords().catch(() => {
      // 与首页聚焦时的刷新一致:失败时保留现有内容
    });
  };

  if (state.restoreSeq !== previous.restoreSeq) {
    refresh();
    return;
  }

  const newlyHidden = state.pending && state.pending.key !== previous.pending?.key ? state.pending.key : null;
  if (!newlyHidden || home.selectedCategory?.type !== "record") {
    return;
  }
  const contentData = home.contentData.filter((item) => playRecordKey(item.source, item.id) !== newlyHidden);
  if (contentData.length === home.contentData.length) {
    return;
  }
  useHomeStore.setState({ contentData });
  if (contentData.length === 0) {
    refresh();
  }
});

export default useHomeStore;
