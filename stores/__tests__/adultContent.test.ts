import useHomeStore from "../homeStore";
import useFavoritesStore from "../favoritesStore";
import useAuthStore from "../authStore";
import useDetailStore from "../detailStore";
import usePlayerStore from "../playerStore";
import { useSettingsStore } from "../settingsStore";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { api } from "@/services/api";
import { encodeVideoSourceSyncLink } from "@/services/videoSourceSync";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

const normal = { key: "normal", name: "Normal", api: "https://normal.example/vod" };
const adult = { key: "adult", name: "Adult", api: "https://adult.example/vod", adult: true };

const record = (title: string, saveTime: number) => ({
  title,
  source_name: title,
  cover: "",
  index: 1,
  total_episodes: 2,
  play_time: 12,
  total_time: 120,
  save_time: saveTime,
  year: "2026",
});

describe("adult content in the stores", () => {
  const initialCategories = useHomeStore.getState().categories;
  const recordCategory = initialCategories.find((category) => category.type === "record")!;
  let stored: Record<string, string>;

  // 与应用里的顺序一致:先让取数层生效,再更新 store
  const setSources = (sources: (typeof normal & { adult?: boolean })[]) => {
    api.setSubscribedVideoSources(sources);
    useSettingsStore.setState({ subscribedVideoSources: sources });
  };

  beforeEach(async () => {
    jest.restoreAllMocks();
    jest.clearAllMocks();
    stored = {
      mytv_play_records: JSON.stringify({ "normal+1": record("Normal Movie", 10), "adult+2": record("Adult Movie", 20) }),
      mytv_favorites: JSON.stringify({
        "normal+1": { title: "Normal Movie", source_name: "Normal", cover: "", total_episodes: 1, search_title: "", year: "" },
        "adult+2": { title: "Adult Movie", source_name: "Adult", cover: "", total_episodes: 1, search_title: "", year: "" },
      }),
    };
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => stored[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      stored[key] = value;
    });
    useAuthStore.setState({ isLoggedIn: false, isLoginModalVisible: false });
    useSettingsStore.setState({ apiBaseUrl: "" });
    await useSettingsStore.getState().setShowAdultContent(false);
    setSources([normal, adult]);
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

  afterEach(async () => {
    setSources([]);
    await useSettingsStore.getState().setShowAdultContent(false);
  });

  const homeTitles = () => useHomeStore.getState().contentData.map((item) => item.title);

  it("leaves adult sources out of continue-watching while the toggle is off", async () => {
    await useHomeStore.getState().refreshPlayRecords();

    expect(homeTitles()).toEqual(["Normal Movie"]);
  });

  it("shows them in continue-watching when the toggle is on", async () => {
    await useSettingsStore.getState().setShowAdultContent(true);

    await useHomeStore.getState().refreshPlayRecords();

    expect(homeTitles()).toEqual(["Adult Movie", "Normal Movie"]);
  });

  it("hides the watch history category when only adult records exist", async () => {
    stored.mytv_play_records = JSON.stringify({ "adult+2": record("Adult Movie", 20) });

    await useHomeStore.getState().refreshPlayRecords();

    expect(useHomeStore.getState().categories.some((category) => category.type === "record")).toBe(false);
    expect(homeTitles()).toEqual([]);
  });

  it("does not delete the hidden records", async () => {
    await useHomeStore.getState().refreshPlayRecords();

    expect(Object.keys(JSON.parse(stored.mytv_play_records))).toEqual(["normal+1", "adult+2"]);
  });

  it("removes adult items already on the home screen as soon as the toggle is turned off", async () => {
    await useSettingsStore.getState().setShowAdultContent(true);
    await useHomeStore.getState().refreshPlayRecords();
    expect(homeTitles()).toEqual(["Adult Movie", "Normal Movie"]);

    await useSettingsStore.getState().setShowAdultContent(false);

    expect(homeTitles()).toEqual(["Normal Movie"]);
  });

  it("removes items of a source from the home screen when it is added again as adult", async () => {
    setSources([normal, { ...adult, adult: undefined }]);
    await useHomeStore.getState().refreshPlayRecords();
    expect(homeTitles()).toEqual(["Adult Movie", "Normal Movie"]);

    setSources([normal, adult]);

    expect(homeTitles()).toEqual(["Normal Movie"]);
  });

  it("does not serve a home category from the cache filled under the other toggle state", async () => {
    const category = { title: "Movies", type: "movie" as const, tag: "adult-cache" };
    const item = (source: string, id: string) => ({ id, title: id, poster: "", year: "", source, source_name: source });
    const getDoubanData = jest
      .spyOn(api, "getDoubanData")
      .mockImplementation(async (_type, _tag, _size, start = 0) => ({
        code: 0,
        message: "",
        // 取数层本身按开关过滤;这里模拟它的结果
        list: start > 0 ? [] : api.isAdultSourceHidden("adult") ? [item("normal", "n")] : [item("normal", "n"), item("adult", "a")],
      }));

    await useSettingsStore.getState().setShowAdultContent(true);
    useHomeStore.setState({ selectedCategory: category, contentData: [], pageStart: 0, hasMore: true });
    await useHomeStore.getState().fetchInitialData();
    expect(homeTitles()).toEqual(["n", "a"]);

    await useSettingsStore.getState().setShowAdultContent(false);
    expect(homeTitles()).toEqual(["n"]);
    await useHomeStore.getState().fetchInitialData();
    expect(homeTitles()).toEqual(["n"]);

    await useSettingsStore.getState().setShowAdultContent(true);
    await useHomeStore.getState().fetchInitialData();
    expect(homeTitles()).toEqual(["n", "a"]);
    expect(getDoubanData).toHaveBeenCalledTimes(3);
  });

  it("re-checks which sources can play a title when the hidden sources change", async () => {
    const searchVideos = jest.spyOn(api, "searchVideos").mockResolvedValue({ results: [] });
    useHomeStore.setState({
      contentData: [
        { id: "db-1", source: "douban", title: "Title", poster: "", playabilityStatus: "playable", playableSourceCount: 2 } as any,
      ],
    });

    // 源列表换了一个新数组但内容没变(每次 loadSettings 都是这样):不重新探测
    setSources([normal, adult]);
    expect(searchVideos).not.toHaveBeenCalled();

    await useSettingsStore.getState().setShowAdultContent(true);

    expect(searchVideos).toHaveBeenCalledTimes(1);
    expect(searchVideos).toHaveBeenCalledWith("Title");
  });

  it("leaves adult sources out of the favorites list while the toggle is off, and keeps them stored", async () => {
    await useFavoritesStore.getState().fetchFavorites();
    expect(useFavoritesStore.getState().favorites.map((favorite) => favorite.key)).toEqual(["normal+1"]);

    await useSettingsStore.getState().setShowAdultContent(true);
    await useFavoritesStore.getState().fetchFavorites();
    expect(useFavoritesStore.getState().favorites.map((favorite) => favorite.key)).toEqual(["normal+1", "adult+2"]);
  });

  it("keeps adult sources imported through a sync link hidden while the toggle is off", async () => {
    // 另一台设备导出的链接(成人标记原样带上)
    const link = encodeVideoSourceSyncLink([normal, adult]);
    setSources([]);

    const result = await useSettingsStore.getState().importVideoSources(link);

    expect(result).toMatchObject({ total: 2, added: 2, adultCount: 1 });
    expect(JSON.parse(stored.mytv_settings).subscribedVideoSources).toEqual([
      { ...normal, imported: true },
      { ...adult, imported: true },
    ]);
    // 开关关着:导入的成人源照样不显示;打开后才出现
    expect((await api.getResources()).map((site) => site.key)).toEqual(["normal"]);
    expect(api.isAdultSourceHidden("adult")).toBe(true);
    expect(useSettingsStore.getState().showAdultContent).toBe(false);

    await useSettingsStore.getState().setShowAdultContent(true);
    expect((await api.getResources()).map((site) => site.key)).toEqual(["normal", "adult"]);
  });

  it("exports adult sources with their mark even while the toggle is off", async () => {
    const exported = encodeVideoSourceSyncLink(useSettingsStore.getState().subscribedVideoSources);
    setSources([]);

    await useSettingsStore.getState().importVideoSources(exported);

    expect(useSettingsStore.getState().subscribedVideoSources.map(({ key, adult: isAdult }) => [key, isAdult])).toEqual([
      ["normal", undefined],
      ["adult", true],
    ]);
  });

  it("persists the toggle under the app settings key", async () => {
    await useSettingsStore.getState().setShowAdultContent(true);
    expect(JSON.parse(stored.mytv_settings).showAdultContent).toBe(true);

    await useSettingsStore.getState().setShowAdultContent(false);
    expect(JSON.parse(stored.mytv_settings).showAdultContent).toBe(false);
  });

  // ---- 按分类过滤:源本身没有成人标记,条目的分类是成人分类 ----
  describe("adult categories on sources that are not marked adult", () => {
    const favorite = (title: string, typeName?: string) => ({
      title,
      source_name: "Normal",
      cover: "",
      total_episodes: 1,
      search_title: "",
      year: "",
      ...(typeName ? { type_name: typeName } : {}),
    });

    beforeEach(() => {
      // 没有任何成人源:只剩分类这一条过滤依据
      setSources([normal]);
      stored.mytv_play_records = JSON.stringify({
        "normal+1": { ...record("Normal Movie", 10), type_name: "剧情片" },
        "normal+2": { ...record("Late Night", 20), type_name: "伦理片" },
        // 片名带“伦理”,分类正常:不能被拦
        "normal+3": { ...record("伦理学讲座", 30), type_name: "纪录片,人文" },
        // 旧记录没有分类:无法判断,保持显示
        "normal+4": record("Legacy", 40),
        "normal+5": { ...record("Tagged", 50), type_name: "剧情片,剧情,情色" },
      });
      stored.mytv_favorites = JSON.stringify({
        "normal+1": favorite("Normal Movie", "爱情片"),
        "normal+2": favorite("Late Night", "港台三级"),
        "normal+3": favorite("伦理学讲座", "纪录片"),
        "normal+4": favorite("Legacy"),
        "normal+5": favorite("Bonus", "福利"),
      });
    });

    it("leaves adult-category records out of continue-watching while the toggle is off", async () => {
      await useHomeStore.getState().refreshPlayRecords();

      expect(homeTitles()).toEqual(["Legacy", "伦理学讲座", "Normal Movie"]);
      // 记录本身不删除
      expect(Object.keys(JSON.parse(stored.mytv_play_records))).toHaveLength(5);
    });

    it("shows them in continue-watching when the toggle is on", async () => {
      await useSettingsStore.getState().setShowAdultContent(true);

      await useHomeStore.getState().refreshPlayRecords();

      expect(homeTitles()).toEqual(["Tagged", "Legacy", "伦理学讲座", "Late Night", "Normal Movie"]);
    });

    it("hides the watch history category when only adult-category records exist", async () => {
      stored.mytv_play_records = JSON.stringify({ "normal+2": { ...record("Late Night", 20), type_name: "伦理片" } });

      await useHomeStore.getState().refreshPlayRecords();

      expect(useHomeStore.getState().categories.some((category) => category.type === "record")).toBe(false);
    });

    it("removes adult-category items from the home screen as soon as the toggle is turned off, with no adult source at all", async () => {
      await useSettingsStore.getState().setShowAdultContent(true);
      await useHomeStore.getState().refreshPlayRecords();
      expect(homeTitles()).toContain("Late Night");

      await useSettingsStore.getState().setShowAdultContent(false);

      expect(homeTitles()).toEqual(["Legacy", "伦理学讲座", "Normal Movie"]);
    });

    it("does not serve a home category from the cache filled under the other toggle state, with no adult source at all", async () => {
      const category = { title: "Movies", type: "movie" as const, tag: "adult-category-cache" };
      const item = (id: string, typeName: string) => ({
        id,
        title: id,
        poster: "",
        year: "",
        source: "normal",
        source_name: "Normal",
        type_name: typeName,
      });
      const getDoubanData = jest
        .spyOn(api, "getDoubanData")
        .mockImplementation(async (_type, _tag, _size, start = 0) => ({
          code: 0,
          message: "",
          // 取数层本身按开关过滤;这里模拟它的结果
          list: (start > 0 ? [] : [item("n", "剧情片"), item("a", "伦理片")]).filter((entry) => !api.isAdultItemHidden(entry)),
        }));

      await useSettingsStore.getState().setShowAdultContent(true);
      useHomeStore.setState({ selectedCategory: category, contentData: [], pageStart: 0, hasMore: true });
      await useHomeStore.getState().fetchInitialData();
      expect(homeTitles()).toEqual(["n", "a"]);

      await useSettingsStore.getState().setShowAdultContent(false);
      expect(homeTitles()).toEqual(["n"]);
      await useHomeStore.getState().fetchInitialData();
      expect(homeTitles()).toEqual(["n"]);

      await useSettingsStore.getState().setShowAdultContent(true);
      await useHomeStore.getState().fetchInitialData();
      expect(homeTitles()).toEqual(["n", "a"]);
      expect(getDoubanData).toHaveBeenCalledTimes(3);
    });

    it("leaves adult-category favorites out of the favorites list while the toggle is off, and keeps them stored", async () => {
      await useFavoritesStore.getState().fetchFavorites();
      expect(useFavoritesStore.getState().favorites.map((item) => item.title)).toEqual(["Normal Movie", "伦理学讲座", "Legacy"]);

      await useSettingsStore.getState().setShowAdultContent(true);
      await useFavoritesStore.getState().fetchFavorites();
      expect(useFavoritesStore.getState().favorites.map((item) => item.title)).toEqual([
        "Normal Movie",
        "Late Night",
        "伦理学讲座",
        "Legacy",
        "Bonus",
      ]);
      expect(Object.keys(JSON.parse(stored.mytv_favorites))).toHaveLength(5);
    });

    it("stores the category with a new favorite and a new play record, so they can be filtered later", async () => {
      const detail = {
        id: 9,
        title: "Late Night 2",
        poster: "",
        episodes: ["https://video.example/a.m3u8"],
        source: "normal",
        source_name: "Normal",
        year: "2026",
        type_name: "伦理片",
        class: "剧情,伦理",
      };
      useDetailStore.setState({ detail: detail as any, q: "Late Night 2" });

      await useDetailStore.getState().toggleFavorite();
      expect(JSON.parse(stored.mytv_favorites)["normal+9"]).toMatchObject({ title: "Late Night 2", type_name: "伦理片,剧情,伦理" });

      usePlayerStore.setState({
        status: { isLoaded: true, positionMillis: 5000, durationMillis: 100000 } as any,
        episodes: [{ url: "https://video.example/a.m3u8", title: "1" }] as any,
        currentEpisodeIndex: 0,
      });
      usePlayerStore.getState()._savePlayRecord({}, { immediate: true });
      await new Promise((resolve) => setTimeout(resolve, 0));
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(JSON.parse(stored.mytv_play_records)["normal+9"]).toMatchObject({ title: "Late Night 2", type_name: "伦理片,剧情,伦理" });

      // 存下来的分类让它们在开关关闭时不显示
      await useFavoritesStore.getState().fetchFavorites();
      expect(useFavoritesStore.getState().favorites.map((item) => item.title)).not.toContain("Late Night 2");
      await useHomeStore.getState().refreshPlayRecords();
      expect(homeTitles()).not.toContain("Late Night 2");

      useDetailStore.setState({ detail: null as any });
    });
  });
});
