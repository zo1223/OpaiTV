import useHomeStore, { computeLocalRecommendations } from "../homeStore";
import { loadVisibleRecommendationCandidates } from "@/services/relatedCandidates";
import { useSettingsStore } from "../settingsStore";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { api } from "@/services/api";
import { __resetRecommendationPoolForTests, rememberRecommendationCandidates } from "@/services/recommendationPool";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

const record = (title: string, typeName: string) => ({
  title,
  source_name: "S",
  cover: "",
  index: 1,
  total_episodes: 2,
  play_time: 12,
  total_time: 120,
  save_time: Date.now(),
  year: "2026",
  type_name: typeName,
});

describe("source mode and recommendation settings", () => {
  let stored: Record<string, string>;

  beforeEach(() => {
    stored = {};
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => stored[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      stored[key] = value;
    });
    (AsyncStorage.removeItem as jest.Mock).mockImplementation(async (key: string) => {
      delete stored[key];
    });
    __resetRecommendationPoolForTests();
    api.setSourceModes({});
    api.setShowAdultContent(false);
    api.setSubscribedVideoSources([]);
    useSettingsStore.setState({ sourceModes: {}, recommendationsEnabled: true });
  });

  it("saves a source mode locally and applies it at once", async () => {
    await useSettingsStore.getState().setSourceMode("alpha", "off");
    expect(useSettingsStore.getState().sourceModes).toEqual({ alpha: "off" });
    expect(api.getSourceModes()).toEqual({ alpha: "off" });
    expect(JSON.parse(stored.mytv_settings).sourceModes).toEqual({ alpha: "off" });

    await useSettingsStore.getState().setSourceMode("alpha", "full");
    expect(api.getSourceModes()).toEqual({});
    expect(JSON.parse(stored.mytv_settings).sourceModes).toEqual({});
  });

  it("restores saved modes on load and ignores damaged ones", async () => {
    stored.mytv_settings = JSON.stringify({ sourceModes: { alpha: "stable", beta: "bogus" } });
    await useSettingsStore.getState().loadSettings();
    expect(useSettingsStore.getState().sourceModes).toEqual({ alpha: "stable" });
    expect(api.getSourceModes()).toEqual({ alpha: "stable" });
  });

  it("has recommendations on by default and remembers when they are turned off", async () => {
    await useSettingsStore.getState().loadSettings();
    expect(useSettingsStore.getState().recommendationsEnabled).toBe(true);
    expect(useHomeStore.getState().categories.some((category) => category.type === "recommend")).toBe(true);

    await useSettingsStore.getState().setRecommendationsEnabled(false);
    expect(JSON.parse(stored.mytv_settings).recommendationsEnabled).toBe(false);
    expect(useHomeStore.getState().categories.some((category) => category.type === "recommend")).toBe(false);

    await useSettingsStore.getState().loadSettings();
    expect(useSettingsStore.getState().recommendationsEnabled).toBe(false);

    await useSettingsStore.getState().setRecommendationsEnabled(true);
    const categories = useHomeStore.getState().categories;
    const recordIndex = categories.findIndex((category) => category.type === "record");
    expect(categories[recordIndex + 1].type).toBe("recommend");
  });

  it("leaves the recommend tab when it is switched off while selected", async () => {
    const recommend = useHomeStore.getState().categories.find((category) => category.type === "recommend")!;
    useHomeStore.setState({ selectedCategory: recommend });
    await useSettingsStore.getState().setRecommendationsEnabled(false);
    expect(useHomeStore.getState().selectedCategory.type).not.toBe("recommend");
  });

  it("computes recommendations from local records, favourites and the local pool without any request", async () => {
    const fetchSpy = jest.spyOn(global, "fetch").mockImplementation(() => {
      throw new Error("recommendations must not use the network");
    });
    try {
      stored.mytv_play_records = JSON.stringify({ "alpha+1": record("三体", "科幻") });
      stored.mytv_favorites = JSON.stringify({
        "alpha+9": { title: "繁花", cover: "c.jpg", source_name: "Alpha", total_episodes: 30, search_title: "繁花", year: "2023" },
      });
      await rememberRecommendationCandidates(
        [
          { id: "2", source: "alpha", title: "三体 第二季", poster: "", type_name: "科幻" },
          { id: "3", source: "beta", title: "流浪地球", poster: "", type_name: "科幻片" },
          { id: "4", source: "alpha", title: "体坛快讯", poster: "", type_name: "体育" },
          { id: "5", source: "alpha", title: "成人条目", poster: "", type_name: "伦理片" },
        ],
        ["热门"]
      );

      const items = await computeLocalRecommendations();
      expect(items.map((item) => [item.title, item.reason])).toEqual([
        ["三体 第二季", "因为你看过《三体》"],
        ["繁花", "你收藏了还没看"],
        ["流浪地球", "因为你常看「科幻」"],
      ]);

      // 关闭的片源不出现在首页推荐里
      api.setSourceModes({ beta: "off" });
      expect((await computeLocalRecommendations()).map((item) => item.title)).toEqual(["三体 第二季", "繁花"]);
      expect(fetchSpy).not.toHaveBeenCalled();
    } finally {
      fetchSpy.mockRestore();
    }
  });

  it("hands the detail page only the visible part of the local pool, without any request", async () => {
    const fetchSpy = jest.spyOn(global, "fetch").mockImplementation(() => {
      throw new Error("related items must not use the network");
    });
    try {
      await rememberRecommendationCandidates([
        { id: "2", source: "alpha", title: "三体 第二季", poster: "p2.jpg", type_name: "科幻" },
        { id: "3", source: "beta", title: "流浪地球", poster: "p3.jpg", type_name: "科幻片" },
        { id: "5", source: "alpha", title: "成人条目", poster: "p5.jpg", type_name: "伦理片" },
      ]);
      const titles = async () => (await loadVisibleRecommendationCandidates()).map((item) => item.title);

      // 18+ 开关关着:成人分类的条目不给详情页
      expect(await titles()).toEqual(["三体 第二季", "流浪地球"]);
      // 模式为“关闭”的片源同样不给
      api.setSourceModes({ beta: "off" });
      expect(await titles()).toEqual(["三体 第二季"]);
      // 开关打开后按开关放行(过滤规则本身在 api 里,这里只是照用)
      api.setSourceModes({});
      api.setShowAdultContent(true);
      expect(await titles()).toEqual(["三体 第二季", "流浪地球", "成人条目"]);
      expect(fetchSpy).not.toHaveBeenCalled();
    } finally {
      fetchSpy.mockRestore();
    }
  });

  it("shows the recommendations as the content of the recommend tab", async () => {
    stored.mytv_play_records = JSON.stringify({ "alpha+1": record("三体", "科幻") });
    await rememberRecommendationCandidates([{ id: "2", source: "alpha", title: "三体 第二季", poster: "", type_name: "科幻" }]);
    const recommend = useHomeStore.getState().categories.find((category) => category.type === "recommend")!;
    useHomeStore.setState({ selectedCategory: recommend, contentData: [], hasMore: true, loadingMore: false, pageStart: 0 });
    await useHomeStore.getState().loadMoreData();
    expect(useHomeStore.getState().contentData.map((item) => [item.title, item.reason])).toEqual([
      ["三体 第二季", "因为你看过《三体》"],
    ]);
    expect(useHomeStore.getState().hasMore).toBe(false);
  });
});
