import { md5 } from "@/utils/md5";
import { DanmakuHttp } from "../http";
import {
  base64Ascii,
  buildYoukuSegmentRequest,
  createYoukuProvider,
  isYoukuTokenError,
  parseYoukuEpisodes,
  parseYoukuSearch,
  parseYoukuSegment,
  parseYoukuToken,
} from "../youku";

const searchJson = (items: any[]) => ({ pageComponentList: items.map((commonData) => ({ commonData })) });
const show = (showId: string, displayName: string, feature: string, extra: Record<string, unknown> = {}) => ({
  showId,
  titleDTO: { displayName },
  feature,
  isYouku: 1,
  hasYouku: 1,
  episodeTotal: 40,
  ...extra,
});
const video = (n: number, extra: Record<string, unknown> = {}) => ({
  id: `VID${n}==`,
  seq: String(n),
  stage: String(n),
  title: `藏海传 ${String(n).padStart(2, "0")}`,
  duration: "2725.04",
  ...extra,
});
const segmentJson = (items: any[]) => ({
  api: "mopen.youku.danmu.list",
  data: { result: JSON.stringify({ code: 1, data: { result: items } }) },
  ret: ["SUCCESS::调用成功"],
});
const TOKEN_COOKIE =
  "mtop_partitioned_detect=1;Path=/; _m_h5_tk=0123456789abcdef0123456789abcdef_1795629354744;Path=/;Domain=youku.com; _m_h5_tk_enc=encvalue;Path=/";

interface Fake extends DanmakuHttp {
  calls: { url: string; method: string; headers?: Record<string, string>; body?: string }[];
}

function fakeHttp(options: {
  search?: any;
  episodes?: (page: number) => any;
  token?: () => { text: string; setCookie: string };
  segment?: (call: number) => { text: string; setCookie: string };
  raw?: boolean;
}): Fake {
  const calls: Fake["calls"] = [];
  let segmentCalls = 0;
  const http: Fake = {
    calls,
    async getJson(url, headers) {
      calls.push({ url, method: "GET", headers });
      if (url.includes("search.youku.com")) {
        if (options.search instanceof Error) throw options.search;
        return options.search ?? searchJson([]);
      }
      if (url.includes("shows/videos.json")) {
        const page = Number(/[?&]page=(\d+)/.exec(url)?.[1]);
        return options.episodes ? options.episodes(page) : { total: 0, videos: [] };
      }
      throw new Error("unexpected " + url);
    },
    async postJson(url) {
      throw new Error("unexpected " + url);
    },
    async getBytes(url) {
      throw new Error("unexpected " + url);
    },
  };
  if (options.raw !== false) {
    http.sendRaw = async (url, init) => {
      calls.push({ url, method: init.method, headers: init.headers, body: init.body });
      if (url.includes("aplatform.weakget")) {
        return options.token ? options.token() : { text: "{}", setCookie: TOKEN_COOKIE };
      }
      if (url.includes("mopen.youku.danmu.list")) {
        return options.segment ? options.segment(segmentCalls++) : { text: JSON.stringify(segmentJson([])), setCookie: "" };
      }
      throw new Error("unexpected " + url);
    };
  }
  return http;
}

const QUERY = { title: "藏海传", episodeIndex: 1, episodeNumber: 2, episodeCount: 40, year: "2025" };
const happy = (extra: Parameters<typeof fakeHttp>[0] = {}) =>
  fakeHttp({
    search: searchJson([
      show("show-hl", "藏海传 高光版", "2026 · 电视剧 · 中国 · 20集全", { episodeTotal: 20 }),
      show("show-main", "藏海传", "2025 · 电视剧 · 中国 · 40集全(完结)"),
    ]),
    episodes: () => ({ total: 3, videos: [video(1), video(2), video(3)] }),
    ...extra,
  });

describe("优酷弹幕:解析", () => {
  it("搜索结果只留优酷有正片的条目,去掉解读类和标签,取出年份与类型", () => {
    const result = parseYoukuSearch(
      searchJson([
        show("a", "<em>藏海传</em>【独播】", "2025 · 电视剧 · 中国 · 40集全(完结)"),
        show("b", "《藏海传》剧情解读", "2026 · 电视剧 · 共4集"),
        show("c", "藏海传 抢先看", "2025 · 电视剧"),
        show("d", "别的平台的片", "2024 · 电视剧", { isYouku: 0, hasYouku: 0 }),
        show("e", "长安的荔枝", "2025 · 电影 · 中国", { episodeTotal: 0 }),
        show("a", "藏海传", "2025 · 电视剧"),
        { titleDTO: { displayName: "没有 showId" } },
      ])
    );
    expect(result).toEqual([
      { showId: "a", title: "藏海传", year: "2025", isMovie: false, episodeCount: 40 },
      { showId: "e", title: "长安的荔枝", year: "2025", isMovie: true, episodeCount: 1 },
    ]);
    expect(parseYoukuSearch(null)).toEqual([]);
    expect(parseYoukuSearch({ pageComponentList: [{}] })).toEqual([]);
  });

  it("分集取集号和时长,花絮混在正片里时只留正片", () => {
    const parsed = parseYoukuEpisodes({
      total: "40",
      videos: [video(1), video(2, { duration: "abc" }), video(9, { title: "藏海传 预告片" }), { title: "没有 id" }],
    });
    expect(parsed.total).toBe(40);
    expect(parsed.episodes).toEqual([
      { vid: "VID1==", number: 1, title: "藏海传 01", duration: 2725.04 },
      { vid: "VID2==", number: 2, title: "藏海传 02", duration: 0 },
    ]);
    expect(parseYoukuEpisodes({})).toEqual({ total: 0, episodes: [] });
  });

  it("令牌要两个 Cookie 都在才算拿到", () => {
    expect(parseYoukuToken(TOKEN_COOKIE)).toEqual({ tk: "0123456789abcdef0123456789abcdef_1795629354744", enc: "encvalue" });
    expect(parseYoukuToken("_m_h5_tk=abc_1;Path=/")).toBeNull();
    expect(parseYoukuToken("")).toBeNull();
    expect(parseYoukuToken(undefined)).toBeNull();
  });

  it("弹幕分片:时间换成秒并排序,颜色和位置取自 propertis,坏数据跳过", () => {
    const comments = parseYoukuSegment(
      segmentJson([
        { content: "后到", playat: 61500, propertis: '{"color":16711680,"pos":1}' },
        { content: "先到", playat: 60250, propertis: '{"color":"255","pos":2}' },
        { content: "默认", playat: 60836, propertis: "不是 JSON" },
        { content: "滚动", playat: 62000, propertis: '{"color":16777215,"pos":3}' },
        { content: "  ", playat: 100 },
        { content: "没时间" },
      ])
    );
    expect(comments).toEqual([
      { time: 60.25, text: "先到", color: 0x0000ff, mode: "bottom" },
      { time: 60.836, text: "默认", color: 0xffffff, mode: "scroll" },
      { time: 61.5, text: "后到", color: 0xff0000, mode: "top" },
      { time: 62, text: "滚动", color: 0xffffff, mode: "scroll" },
    ]);
    expect(parseYoukuSegment({ data: {} })).toEqual([]);
    expect(parseYoukuSegment({ data: { result: "{bad" } })).toEqual([]);
    expect(parseYoukuSegment({ data: { result: '{"code":"-1"}' } })).toEqual([]);
  });

  it("识别令牌失效的应答", () => {
    expect(isYoukuTokenError({ ret: ["FAIL_SYS_TOKEN_EMPTY::令牌为空"] })).toBe(true);
    expect(isYoukuTokenError({ ret: ["FAIL_SYS_TOKEN_EXOIRED::令牌过期"] })).toBe(true);
    expect(isYoukuTokenError({ ret: ["SUCCESS::调用成功"] })).toBe(false);
    expect(isYoukuTokenError({})).toBe(false);
  });
});

describe("优酷弹幕:签名", () => {
  it("base64 与标准实现一致(含补位)", () => {
    for (const text of ["", "a", "ab", "abc", '{"vid":"XNjQ3Nzc2ODgwOA==","mat":1}']) {
      expect(base64Ascii(text)).toBe(Buffer.from(text, "latin1").toString("base64"));
    }
  });

  it("请求体带 msg 和 sign,地址上的 sign 由令牌前 32 位、时间、appKey 和请求体算出", () => {
    const tk = "0123456789abcdef0123456789abcdef_1795629354744";
    const request = buildYoukuSegmentRequest("VID1==", 3, "guid-1", tk, 1700000000000);
    expect(request.body.startsWith("data=")).toBe(true);
    const data = decodeURIComponent(request.body.slice(5));
    const msg = JSON.parse(data);
    const inner = { ctime: 1700000000000, ctype: 10004, cver: "v1.0", guid: "guid-1", mat: 3, mcount: 1, pid: 0, sver: "3.1.0", type: 1, vid: "VID1==" };
    const encoded = Buffer.from(JSON.stringify(inner)).toString("base64");
    expect(msg).toEqual({ ...inner, msg: encoded, sign: md5(encoded + "MkmC9SoIw6xCkSKHhJ7b5D2r51kBiREr") });
    const sign = md5(`0123456789abcdef0123456789abcdef&1700000000000&24679788&${data}`);
    expect(request.url).toBe(
      `https://acs.youku.com/h5/mopen.youku.danmu.list/1.0/?jsv=2.5.6&appKey=24679788&t=1700000000000&sign=${sign}` +
        "&api=mopen.youku.danmu.list&v=1.0&type=originaljson&dataType=jsonp&timeout=20000&jsonpIncPrefix=utility"
    );
  });
});

describe("优酷弹幕:匹配与取数", () => {
  it("按标题和集号匹配到对应的集,分片按 60 秒取", async () => {
    const http = happy({
      segment: () => ({ text: JSON.stringify(segmentJson([{ content: "来了", playat: 61000 }])), setCookie: "" }),
    });
    const track = await createYoukuProvider(http, () => 1700000000000).resolve(QUERY);
    expect(track).not.toBeNull();
    expect(track!.provider).toBe("youku");
    expect(track!.title).toBe("藏海传 02");
    expect(track!.segmentSeconds).toBe(60);
    expect(http.calls.some((call) => call.url.includes("show_id=show-main"))).toBe(true);

    expect(await track!.loadSegment(1)).toEqual([{ time: 61, text: "来了", color: 0xffffff, mode: "scroll" }]);
    const post = http.calls.find((call) => call.method === "POST")!;
    expect(post.headers!.Cookie).toBe("_m_h5_tk=0123456789abcdef0123456789abcdef_1795629354744;_m_h5_tk_enc=encvalue;");
    expect(post.headers!["Content-Type"]).toBe("application/x-www-form-urlencoded");
    const msg = JSON.parse(decodeURIComponent(post.body!.slice(5)));
    expect(msg.vid).toBe("VID2==");
    expect(msg.mat).toBe(1);

    // 2725 秒 → 46 片(0..45);超出范围不发请求
    const before = http.calls.length;
    expect(await track!.loadSegment(46)).toEqual([]);
    expect(await track!.loadSegment(-1)).toEqual([]);
    expect(http.calls.length).toBe(before);
    // 令牌只取一次
    await track!.loadSegment(2);
    expect(http.calls.filter((call) => call.url.includes("weakget")).length).toBe(1);
  });

  it("同一部片再次匹配不重复搜索", async () => {
    const http = happy();
    const provider = createYoukuProvider(http);
    await provider.resolve(QUERY);
    await provider.resolve({ ...QUERY, episodeIndex: 2, episodeNumber: 3 });
    expect(http.calls.filter((call) => call.url.includes("search.youku.com")).length).toBe(1);
    expect(http.calls.filter((call) => call.url.includes("videos.json")).length).toBe(1);
  });

  it("超过一页的长剧只取当前集所在的那一页", async () => {
    const http = happy({
      search: searchJson([show("long", "藏海传", "2025 · 电视剧", { episodeTotal: 150 })]),
      episodes: (page) => ({ total: 150, videos: page === 2 ? [video(101), video(102)] : [video(1), video(2)] }),
    });
    const track = await createYoukuProvider(http).resolve({ title: "藏海传", episodeIndex: 101, episodeNumber: 102, episodeCount: 150 });
    expect(track!.title).toBe("藏海传 102");
    expect(http.calls.some((call) => call.url.includes("page=2&count=100"))).toBe(true);
  });

  it("没有这部片、季不对、没有这一集时返回 null", async () => {
    expect(await createYoukuProvider(fakeHttp({})).resolve(QUERY)).toBeNull();
    expect(await createYoukuProvider(happy()).resolve({ ...QUERY, title: "藏海传 第二季" })).toBeNull();
    expect(await createYoukuProvider(happy()).resolve({ ...QUERY, episodeIndex: 29, episodeNumber: 30 })).toBeNull();
    expect(await createYoukuProvider(happy()).resolve({ ...QUERY, title: "完全不同的片" })).toBeNull();
  });

  it("搜索访问失败时抛错,且不缓存失败", async () => {
    const http = happy({ search: new Error("network") });
    const provider = createYoukuProvider(http);
    await expect(provider.resolve(QUERY)).rejects.toThrow("network");
    await expect(provider.resolve(QUERY)).rejects.toThrow("network");
    expect(http.calls.filter((call) => call.url.includes("search.youku.com")).length).toBe(2);
  });

  it("拿不到令牌(读不到 Set-Cookie 或 http 不支持原始请求)按访问失败处理", async () => {
    await expect(createYoukuProvider(happy({ token: () => ({ text: "{}", setCookie: "" }) })).resolve(QUERY)).rejects.toThrow("token");
    await expect(createYoukuProvider(happy({ raw: false })).resolve(QUERY)).rejects.toThrow("raw http");
  });

  it("令牌失效时用应答里的新令牌重试一次;仍失效则抛错", async () => {
    const expired = { text: JSON.stringify({ data: {}, ret: ["FAIL_SYS_TOKEN_EXOIRED::令牌过期"] }), setCookie: "_m_h5_tk=fresh_1;Path=/; _m_h5_tk_enc=freshenc;Path=/" };
    const ok = { text: JSON.stringify(segmentJson([{ content: "好了", playat: 1000 }])), setCookie: "" };
    const http = happy({ segment: (call) => (call === 0 ? expired : ok) });
    const track = await createYoukuProvider(http).resolve(QUERY);
    expect(await track!.loadSegment(0)).toEqual([{ time: 1, text: "好了", color: 0xffffff, mode: "scroll" }]);
    const posts = http.calls.filter((call) => call.method === "POST");
    expect(posts.length).toBe(2);
    expect(posts[1].headers!.Cookie).toBe("_m_h5_tk=fresh_1;_m_h5_tk_enc=freshenc;");

    const dead = happy({ segment: () => ({ ...expired, setCookie: "" }) });
    const deadTrack = await createYoukuProvider(dead).resolve(QUERY);
    await expect(deadTrack!.loadSegment(0)).rejects.toThrow("token rejected");
  });
});
