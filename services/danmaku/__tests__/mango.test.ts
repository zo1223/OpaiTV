import { DanmakuHttp } from "../http";
import {
  createMangoProvider,
  orderMangoEpisodes,
  parseMangoControl,
  parseMangoDuration,
  parseMangoEpisodePage,
  parseMangoSearch,
  parseMangoSegment,
} from "../mango";
import { DanmakuQuery } from "../types";

const SEARCH = {
  data: {
    contents: [
      {
        type: "mediaRebirthV2",
        data: [
          { title: "<B>大宋少年志2</B>", clipId: 448573, source: "", desc: ["类型: 电视剧 / 2023 / 内地"] },
          { title: "大宋少年志", clipId: "326733", source: "imgo", desc: ["类型: 电视剧 / 2019 / 内地"] },
          { title: "大宋少年志2", clipId: "999", source: "qq", desc: ["类型: 电视剧 / 2023"] },
          { title: "没有编号" },
          { title: "某电影", clipId: "777", desc: ["类型: 电影 / 2020 / 内地"] },
        ],
      },
      { type: "shortVideoMerge", data: [{ title: "大宋少年志2", clipId: "1" }] },
    ],
  },
};

const episode = (n: number, vid: string, extra: Record<string, unknown> = {}) => ({
  src_clip_id: "448573",
  video_id: vid,
  t1: `标题${n}`,
  t2: `第${n}集`,
  t3: `大宋少年志2 第${n}集`,
  time: "46:36",
  isnew: "0",
  isIntact: "1",
  ts: `2023-08-${String(n).padStart(2, "0")} 10:00:00.0`,
  ...extra,
});

const PAGE_NEW = {
  data: {
    tab_m: [{ m: "202308" }, { m: "202307" }],
    list: [
      episode(3, "v3"),
      episode(4, "v4"),
      episode(5, "v5", { isnew: "2" }),
      episode(6, "v6", { t1: "幕后花絮" }),
      episode(7, "v7", { src_clip_id: "other" }),
    ],
  },
};
const PAGE_OLD = { data: { tab_m: [{ m: "202308" }, { m: "202307" }], list: [episode(1, "v1"), episode(2, "v2")] } };
const CONTROL = { data: { cdn_list: "bullet-ali.hitv.com,bullet-ws.hitv.com", cdn_version: "bullet/tx/2026/11/25/1/v2" } };
const SEGMENT = {
  data: {
    items: [
      { content: "后来的", time: 61500 },
      { content: " 第一条 ", time: 60100, v2_position: 1 },
      { content: "彩色", time: 60200, v2_position: 2, v2_color: { color_left: { r: 255, g: 0, b: 0 }, color_right: { r: -1, g: -1, b: -1 } } },
      { content: "", time: 60300 },
      { content: "没时间" },
    ],
  },
};

function fakeHttp(handler: (url: string) => any): DanmakuHttp & { urls: string[] } {
  const urls: string[] = [];
  return {
    urls,
    getJson: async (url) => {
      urls.push(url);
      return handler(url);
    },
    postJson: async () => {
      throw new Error("unexpected post");
    },
    getBytes: async () => {
      throw new Error("unexpected bytes");
    },
  };
}

const realistic = (url: string) => {
  if (url.includes("/search/rebirth/v2")) return SEARCH;
  if (url.includes("/variety/showlist")) return url.includes("month=202307") ? PAGE_OLD : PAGE_NEW;
  if (url.includes("/getctlbarrage")) return CONTROL;
  if (url.includes("hitv.com/")) return SEGMENT;
  throw new Error("unexpected " + url);
};

const query = (overrides: Partial<DanmakuQuery> = {}): DanmakuQuery => ({
  title: "大宋少年志2",
  episodeIndex: 1,
  episodeNumber: 2,
  episodeCount: 42,
  ...overrides,
});

describe("芒果TV弹幕:解析", () => {
  it("搜索结果只留芒果自有的条目,取出年份和类型", () => {
    expect(parseMangoSearch(SEARCH)).toEqual([
      { clipId: "448573", title: "大宋少年志2", year: "2023", isMovie: false, episodeCount: 0 },
      { clipId: "326733", title: "大宋少年志", year: "2019", isMovie: false, episodeCount: 0 },
      { clipId: "777", title: "某电影", year: "2020", isMovie: true, episodeCount: 1 },
    ]);
    expect(parseMangoSearch({})).toEqual([]);
    expect(parseMangoSearch({ data: { contents: "x" } })).toEqual([]);
  });

  it("分集页去掉预告、花絮和别的合集的条目,并给出其余月份", () => {
    const page = parseMangoEpisodePage(PAGE_NEW, "448573");
    expect(page.months).toEqual(["202307"]);
    expect(page.episodes.map((e) => [e.vid, e.number, e.seconds])).toEqual([
      ["v3", 3, 2796],
      ["v4", 4, 2796],
    ]);
    expect(parseMangoEpisodePage(null, "1")).toEqual({ months: [], episodes: [] });
  });

  it("分集合并后按集号排序并去重;有不带集号的按上线时间", () => {
    const a = parseMangoEpisodePage(PAGE_NEW, "448573").episodes;
    const b = parseMangoEpisodePage(PAGE_OLD, "448573").episodes;
    expect(orderMangoEpisodes([...a, ...b, ...a]).map((e) => e.vid)).toEqual(["v1", "v2", "v3", "v4"]);
    const mixed = [
      { vid: "b", number: null, title: "", seconds: 0, ts: "2024-02", intact: true },
      { vid: "a", number: 9, title: "", seconds: 0, ts: "2024-01", intact: true },
    ];
    expect(orderMangoEpisodes(mixed).map((e) => e.vid)).toEqual(["a", "b"]);
  });

  it("时长解析", () => {
    expect(parseMangoDuration("46:36")).toBe(2796);
    expect(parseMangoDuration("1:02:03")).toBe(3723);
    expect(parseMangoDuration("abc")).toBe(0);
    expect(parseMangoDuration(undefined)).toBe(0);
  });

  it("弹幕分片:时间换成秒、按时间排序、位置和颜色", () => {
    expect(parseMangoSegment(SEGMENT)).toEqual([
      { time: 60.1, text: "第一条", color: 0xffffff, mode: "top" },
      { time: 60.2, text: "彩色", color: 0xff0000, mode: "bottom" },
      { time: 61.5, text: "后来的", color: 0xffffff, mode: "scroll" },
    ]);
    expect(parseMangoSegment({})).toEqual([]);
  });

  it("CDN 地址只接受芒果自己的域名", () => {
    expect(parseMangoControl(CONTROL)).toBe("https://bullet-ali.hitv.com/bullet/tx/2026/11/25/1/v2");
    expect(parseMangoControl({ data: { cdn_list: "evil.example.com", cdn_version: "a/b" } })).toBeNull();
    expect(parseMangoControl({ data: { cdn_list: "bullet-ali.hitv.com", cdn_version: "a/../b" } })).toBeNull();
    expect(parseMangoControl({ data: {} })).toBeNull();
  });
});

describe("芒果TV弹幕:匹配", () => {
  it("搜索 → 各月分集 → 弹幕分片", async () => {
    const http = fakeHttp(realistic);
    const provider = createMangoProvider(http);
    const track = await provider.resolve(query());
    expect(track).not.toBeNull();
    expect(track!.provider).toBe("mango");
    expect(track!.title).toBe("大宋少年志2 第2集");
    expect(track!.segmentSeconds).toBe(60);
    expect(http.urls.some((url) => url.includes("vid=v2") && url.includes("cid=448573"))).toBe(true);

    const comments = await track!.loadSegment(1);
    expect(http.urls[http.urls.length - 1]).toBe("https://bullet-ali.hitv.com/bullet/tx/2026/11/25/1/v2/1.json");
    expect(comments.map((c) => c.text)).toEqual(["第一条", "彩色", "后来的"]);
    // 超出片长的分片不发请求
    const before = http.urls.length;
    expect(await track!.loadSegment(47)).toEqual([]);
    expect(http.urls.length).toBe(before);

    // 同一部片换集不再搜索
    await provider.resolve(query({ episodeIndex: 2, episodeNumber: 3 }));
    expect(http.urls.filter((url) => url.includes("/search/rebirth/v2")).length).toBe(1);
  });

  it("带季标记的写法也能对上", async () => {
    const track = await createMangoProvider(fakeHttp(realistic)).resolve(query({ title: "大宋少年志 第二季" }));
    expect(track?.title).toBe("大宋少年志2 第2集");
  });

  it("控制接口没给 CDN 时走备用分片接口", async () => {
    const http = fakeHttp((url) => (url.includes("/getctlbarrage") ? { data: {} } : url.includes("/rdbarrage") ? SEGMENT : realistic(url)));
    const track = await createMangoProvider(http).resolve(query());
    expect((await track!.loadSegment(2)).length).toBe(3);
    expect(http.urls[http.urls.length - 1]).toBe("https://galaxy.bz.mgtv.com/rdbarrage?vid=v2&cid=448573&time=120000");
  });

  it("没有这部片或没有这一集时返回 null", async () => {
    expect(await createMangoProvider(fakeHttp(realistic)).resolve(query({ title: "狂飙" }))).toBeNull();
    expect(await createMangoProvider(fakeHttp(realistic)).resolve(query({ episodeIndex: 29, episodeNumber: 30 }))).toBeNull();
    expect(await createMangoProvider(fakeHttp(() => ({ data: { contents: [] } }))).resolve(query())).toBeNull();
  });

  it("网络出错时抛错,且不缓存失败", async () => {
    let down = true;
    const http = fakeHttp((url) => {
      if (down) throw new Error("network error");
      return realistic(url);
    });
    const provider = createMangoProvider(http);
    await expect(provider.resolve(query())).rejects.toThrow("network error");
    down = false;
    expect(await provider.resolve(query())).not.toBeNull();
  });
});
