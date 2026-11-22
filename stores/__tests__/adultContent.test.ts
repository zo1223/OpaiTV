import useHomeStore from "../homeStore";
import useFavoritesStore from "../favoritesStore";
import useAuthStore from "../authStore";
import { useSettingsStore } from "../settingsStore";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { api } from "@/services/api";

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

  it("persists the toggle under the app settings key", async () => {
    await useSettingsStore.getState().setShowAdultContent(true);
    expect(JSON.parse(stored.mytv_settings).showAdultContent).toBe(true);

    await useSettingsStore.getState().setShowAdultContent(false);
    expect(JSON.parse(stored.mytv_settings).showAdultContent).toBe(false);
  });
});
