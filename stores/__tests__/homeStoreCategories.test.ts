import useHomeStore from "../homeStore";
import useAuthStore from "../authStore";
import { useSettingsStore } from "../settingsStore";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { api } from "@/services/api";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

describe("homeStore categories", () => {
  const initialCategories = useHomeStore.getState().categories;
  const recordCategory = initialCategories.find((category) => category.type === "record")!;

  beforeEach(() => {
    jest.restoreAllMocks();
    jest.clearAllMocks();
    useAuthStore.setState({ isLoggedIn: false, isLoginModalVisible: false });
    useSettingsStore.setState({ apiBaseUrl: "" });
    useHomeStore.setState({
      categories: initialCategories,
      selectedCategory: recordCategory,
      contentData: [],
      loading: false,
      loadingMore: false,
      pageStart: 0,
      hasMore: true,
      error: null,
    });
  });

  it("keeps movie and TV subcategory tags aligned with Douban explore tags", () => {
    const categories = useHomeStore.getState().categories;

    expect(categories.find((category) => category.type === "record")?.title).toBe("观看历史");
    expect(categories.find((category) => category.title === "电影")?.tags).toEqual([
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
    ]);
    expect(categories.find((category) => category.title === "电视剧")?.tags).toEqual([
      "热门",
      "美剧",
      "英剧",
      "韩剧",
      "日剧",
      "国产剧",
      "港剧",
      "日本动画",
      "综艺",
      "纪录片",
    ]);
  });

  it("shows local watch history records without backend login", async () => {
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => {
      if (key === "mytv_play_records") {
        return JSON.stringify({
          "demo+7": {
            title: "Movie",
            source_name: "Demo",
            cover: "https://example.com/movie.jpg",
            index: 1,
            total_episodes: 2,
            play_time: 12,
            total_time: 120,
            save_time: 10,
            year: "2026",
          },
        });
      }
      if (key === "mytv_player_settings") {
        return "{}";
      }
      return null;
    });

    await useHomeStore.getState().refreshPlayRecords();

    expect(useHomeStore.getState().categories.some((category) => category.type === "record")).toBe(true);
    expect(useHomeStore.getState().contentData).toEqual([
      expect.objectContaining({
        id: "7",
        source: "demo",
        title: "Movie",
        progress: 0.1,
        play_time: 12,
        episodeIndex: 1,
      }),
    ]);
  });

  it("updates Douban home items with background playability results", async () => {
    const doubanCategory = { title: "Movies", type: "movie" as const, tag: "hot" };
    let resolveSearch: (value: Awaited<ReturnType<typeof api.searchVideos>>) => void = () => {};

    jest.spyOn(api, "getDoubanData").mockResolvedValue({
      code: 0,
      message: "",
      list: [
        {
          id: "db-1",
          title: "Playable Movie",
          poster: "https://example.com/playable.jpg",
          year: "2026",
        },
      ],
    });
    jest.spyOn(api, "searchVideos").mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveSearch = resolve;
        })
    );

    useHomeStore.setState({
      selectedCategory: doubanCategory,
      contentData: [],
      loading: false,
      loadingMore: false,
      pageStart: 0,
      hasMore: true,
      error: null,
    });

    await useHomeStore.getState().loadMoreData();

    expect(api.searchVideos).toHaveBeenCalledWith("Playable Movie");
    expect(useHomeStore.getState().contentData[0]).toMatchObject({
      id: "db-1",
      source: "douban",
      title: "Playable Movie",
      playabilityStatus: "checking",
    });

    resolveSearch({
      results: [
        {
          id: 9,
          title: "Playable Movie",
          poster: "https://example.com/source.jpg",
          episodes: ["https://video.example/1.m3u8"],
          source: "demo",
          source_name: "Demo Source",
          year: "2026",
        },
      ],
    });
    await Promise.resolve();
    await Promise.resolve();

    expect(useHomeStore.getState().contentData[0]).toMatchObject({
      playabilityStatus: "playable",
      playableSourceCount: 1,
    });

    // Leaving the home screen and returning to this category should reuse the
    // completed probe result instead of starting another search request.
    useHomeStore.getState().selectCategory({ title: "Shows", type: "tv", tag: "hot" });
    useHomeStore.getState().selectCategory(doubanCategory);
    await useHomeStore.getState().fetchInitialData();

    expect(useHomeStore.getState().contentData[0]).toMatchObject({
      playabilityStatus: "playable",
      playableSourceCount: 1,
    });
    expect(api.searchVideos).toHaveBeenCalledTimes(1);
  });
});
