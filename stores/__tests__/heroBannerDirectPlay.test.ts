// 首页横幅对豆瓣条目的“直接起播”:横幅把 source="douban" + 豆瓣 id 交给播放页,
// 播放页用它初始化详情(useDetailStore.init)。豆瓣不是片源,首选源取不到结果,
// 必须落到“按标题搜全部片源、取第一条匹配”的回退上,否则横幅点了播放什么都播不出来。
import useDetailStore from "@/stores/detailStore";
import { api } from "@/services/api";

jest.mock("@/utils/Logger", () => ({
  __esModule: true,
  default: { withTag: () => ({ debug: jest.fn(), error: jest.fn(), info: jest.fn(), warn: jest.fn() }) },
}));

jest.mock("@/services/api", () => ({
  api: {
    searchVideo: jest.fn(),
    searchVideos: jest.fn(),
    getVideoDetail: jest.fn(),
  },
}));

jest.mock("@/services/storage", () => ({
  FavoriteManager: { isFavorited: jest.fn(async () => false), toggle: jest.fn() },
}));

jest.mock("@/services/m3u8", () => ({
  getResolutionFromM3U8: jest.fn(async () => null),
}));

jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: { getState: () => ({ videoSource: { enabledAll: true, sources: {} } }) },
}));

const playable = {
  id: 1,
  title: "横幅上的片子",
  poster: "",
  episodes: ["https://a.example/1.m3u8", "https://a.example/2.m3u8"],
  source: "a",
  source_name: "A站",
  year: "2024",
};

describe("hero banner direct play for a douban item", () => {
  beforeEach(() => {
    (api.searchVideo as jest.Mock).mockReset();
    (api.searchVideos as jest.Mock).mockReset();
  });

  it("falls back to a title search across all sources and picks the first match", async () => {
    (api.searchVideo as jest.Mock).mockResolvedValue({ results: [] });
    (api.searchVideos as jest.Mock).mockResolvedValue({
      results: [{ ...playable, id: 9, title: "别的片子", source: "b" }, playable],
    });

    await useDetailStore.getState().init("横幅上的片子", "douban", "36000001");

    const state = useDetailStore.getState();
    expect(api.searchVideo).toHaveBeenCalledWith("横幅上的片子", "douban", expect.anything(), "36000001");
    expect(state.error).toBeNull();
    expect(state.detail?.source).toBe("a");
    expect(state.detail?.episodes[0]).toBe("https://a.example/1.m3u8");
  });

  it("reports an error instead of hanging when no source has the title", async () => {
    (api.searchVideo as jest.Mock).mockRejectedValue(new Error("unknown source"));
    (api.searchVideos as jest.Mock).mockResolvedValue({ results: [] });

    await useDetailStore.getState().init("谁也没有的片子", "douban", "36000002");

    const state = useDetailStore.getState();
    expect(state.detail).toBeNull();
    expect(state.loading).toBe(false);
    expect(state.error).toContain("未找到");
  });
});
