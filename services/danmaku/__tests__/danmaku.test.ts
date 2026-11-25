import { createHash } from "crypto";
import { deflateSync, strToU8 } from "fflate";
import { md5 } from "@/utils/md5";
import { decodeBilibiliXmlBytes, parseBilibiliXml, signWbiQuery } from "../bilibili";
import { DanmakuClock, DanmakuEngine, DanmakuTimeline } from "../engine";
import { DanmakuHttp } from "../http";
import { createDanmakuService, episodeNumberFromLabel } from "../index";
import { parseDandanComments } from "../server";
import { parseTencentSegment, spreadWithinSecond } from "../tencent";
import { buildMatchFileName, parseTitle, pickBestCandidate, pickEpisodeIndex } from "../title";
import {
  DanmakuComment,
  DanmakuTrack,
  DEFAULT_DANMAKU_SETTINGS,
  normalizeDanmakuServerUrl,
  normalizeDanmakuSettings,
} from "../types";

const comment = (time: number, text = "弹幕", mode: DanmakuComment["mode"] = "scroll"): DanmakuComment => ({
  time,
  text,
  color: 0xffffff,
  mode,
});

const flush = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};

describe("弹幕设置", () => {
  it("默认关闭,且只有明确保存过 true 才算开启", () => {
    expect(DEFAULT_DANMAKU_SETTINGS.enabled).toBe(false);
    expect(DEFAULT_DANMAKU_SETTINGS.serverUrl).toBe("");
    expect(normalizeDanmakuSettings({}).enabled).toBe(false);
    expect(normalizeDanmakuSettings({ danmakuEnabled: "true" }).enabled).toBe(false);
    expect(normalizeDanmakuSettings({ danmakuEnabled: true }).enabled).toBe(true);
  });

  it("损坏的样式设置回到默认值", () => {
    const settings = normalizeDanmakuSettings({ danmakuOpacity: "abc", danmakuSpeed: "warp", danmakuFontSize: 3 });
    expect(settings.opacity).toBe(0.8);
    expect(settings.speed).toBe("normal");
    expect(settings.fontSize).toBe("normal");
    expect(normalizeDanmakuSettings({ danmakuOpacity: 5 }).opacity).toBe(1);
    expect(normalizeDanmakuSettings({ danmakuOpacity: 0 }).opacity).toBe(0.2);
  });

  it("规范化自定义服务器地址", () => {
    expect(normalizeDanmakuServerUrl(" 192.168.1.7:9321/ ")).toBe("http://192.168.1.7:9321");
    expect(normalizeDanmakuServerUrl("https://dm.example.com/87654321/api/v2/")).toBe(
      "https://dm.example.com/87654321"
    );
    expect(normalizeDanmakuServerUrl("ftp://example.com")).toBe("");
    expect(normalizeDanmakuServerUrl("   ")).toBe("");
    expect(normalizeDanmakuServerUrl(undefined)).toBe("");
  });
});

describe("标题与集数匹配", () => {
  it("识别季号", () => {
    expect(parseTitle("庆余年 第二季")).toMatchObject({ season: 2, base: "庆余年" });
    expect(parseTitle("庆余年2")).toMatchObject({ season: 2, base: "庆余年" });
    expect(parseTitle("请回答1988").season).toBeNull();
    expect(parseTitle("繁花(2023)【国语】").cleaned).toBe("繁花");
  });

  it("不把别的片子或别的季套上来", () => {
    const context = { year: "2024", episodeCount: 36, episodeNumber: 5 };
    const candidates = [{ title: "庆余年" }, { title: "庆余年第二季" }, { title: "庆余年之帝王业" }];
    expect(pickBestCandidate(parseTitle("庆余年 第二季"), candidates, context)).toBe(1);
    expect(pickBestCandidate(parseTitle("庆余年"), candidates, context)).toBe(0);
    expect(pickBestCandidate(parseTitle("完全无关"), candidates, context)).toBe(-1);
    // 12 集的剧集不匹配同名电影
    expect(
      pickBestCandidate(parseTitle("孤独摇滚！"), [{ title: "孤独摇滚！", isMovie: true }], {
        episodeCount: 12,
        episodeNumber: 2,
      })
    ).toBe(-1);
  });

  it("按集号找分集,没有这一集就不匹配", () => {
    expect(pickEpisodeIndex([1, 2, 3], { episodeIndex: 1, episodeNumber: 3, episodeCount: 3 })).toBe(2);
    expect(pickEpisodeIndex([1, 2, 3], { episodeIndex: 8, episodeNumber: 9, episodeCount: 20 })).toBe(-1);
    expect(pickEpisodeIndex([null, null], { episodeIndex: 1, episodeNumber: 2, episodeCount: 2 })).toBe(1);
    expect(pickEpisodeIndex([null], { episodeIndex: 0, episodeNumber: 1, episodeCount: 1 })).toBe(0);
  });

  it("集标题推集号", () => {
    expect(episodeNumberFromLabel("第 12 集", 0)).toBe(12);
    expect(episodeNumberFromLabel("05", 0)).toBe(5);
    expect(episodeNumberFromLabel("HD", 3)).toBe(4);
    expect(episodeNumberFromLabel(undefined, 0)).toBe(1);
  });

  it("生成发给 danmu_api match 接口的文件名", () => {
    expect(buildMatchFileName({ title: "庆余年 第二季", episodeNumber: 5, episodeCount: 36 })).toBe("庆余年 S02E05");
    expect(buildMatchFileName({ title: "繁花", episodeNumber: 17, episodeCount: 30 })).toBe("繁花 S01E17");
    expect(buildMatchFileName({ title: "流浪地球", episodeNumber: 1, episodeCount: 1 })).toBe("流浪地球");
  });
});

describe("弹幕解析", () => {
  it("danmu_api / 弹弹 play JSON", () => {
    const parsed = parseDandanComments({
      comments: [
        { cid: 1, p: "1.5,1,16777215,[qq]", m: "滚动" },
        { cid: 2, p: "2,5,255,[bilibili]", m: "顶部" },
        { cid: 3, p: "3,4,65280,[qq]", m: "底部" },
        { cid: 4, p: "bad", m: "时间不对" },
        { cid: 5, p: "4,1,16777215", m: "   " },
      ],
    });
    expect(parsed).toEqual([
      { time: 1.5, text: "滚动", color: 0xffffff, mode: "scroll" },
      { time: 2, text: "顶部", color: 0x0000ff, mode: "top" },
      { time: 3, text: "底部", color: 0x00ff00, mode: "bottom" },
    ]);
    expect(parseDandanComments(null)).toEqual([]);
    expect(parseDandanComments({ comments: "oops" as any })).toEqual([]);
  });

  it("哔哩哔哩 XML(含 deflate 压缩)", () => {
    const xml =
      '<?xml version="1.0"?><i><d p="12.5,1,25,16777215,0,0,0,0">hi &amp; bye</d>' +
      '<d p="3,5,25,16711680,0">顶</d><d p="4,4,25,255,0">底</d><d p="5,7,25,255,0">高级弹幕</d></i>';
    const expected = [
      { time: 12.5, text: "hi & bye", color: 0xffffff, mode: "scroll" },
      { time: 3, text: "顶", color: 0xff0000, mode: "top" },
      { time: 4, text: "底", color: 0x0000ff, mode: "bottom" },
    ];
    expect(parseBilibiliXml(xml)).toEqual(expected);
    expect(parseBilibiliXml(decodeBilibiliXmlBytes(strToU8(xml)))).toEqual(expected);
    expect(parseBilibiliXml(decodeBilibiliXmlBytes(deflateSync(strToU8(xml))))).toEqual(expected);
    expect(decodeBilibiliXmlBytes(new Uint8Array([1, 2, 3, 4]))).toBe("");
  });

  it("腾讯视频分片:颜色、位置,同一秒内的弹幕摊开", () => {
    const parsed = parseTencentSegment({
      barrage_list: [
        { content: "哈哈", time_offset: "31000", content_style: "" },
        { content: "顶", time_offset: "31000", content_style: '{"color":"ff0000","position":2}' },
        { content: "坏样式", time_offset: "40000", content_style: "{oops" },
        { content: "", time_offset: "41000" },
      ],
    });
    expect(parsed).toEqual([
      { time: 31, text: "哈哈", color: 0xffffff, mode: "scroll" },
      { time: 31.5, text: "顶", color: 0xff0000, mode: "top" },
      { time: 40, text: "坏样式", color: 0xffffff, mode: "scroll" },
    ]);
    expect(spreadWithinSecond([comment(1), comment(1), comment(1), comment(1), comment(2.3)]).map((c) => c.time)).toEqual(
      [1, 1.25, 1.5, 1.75, 2.3]
    );
  });

  it("MD5 与 WBI 签名", () => {
    for (const text of ["", "abc", "弹幕 danmaku 😀", "x".repeat(200)]) {
      expect(md5(text)).toBe(createHash("md5").update(text, "utf8").digest("hex"));
    }
    const query = "keyword=%E7%B9%81%E8%8A%B1&page=1&search_type=media_ft&wts=1700000000";
    expect(signWbiQuery({ search_type: "media_ft", keyword: "繁花", page: 1 }, "k".repeat(32), 1700000000)).toBe(
      `${query}&w_rid=${createHash("md5")
        .update(query + "k".repeat(32))
        .digest("hex")}`
    );
  });
});

describe("弹幕时钟(跟随播放进度)", () => {
  it("暂停时不走,播放时按倍速走,拖动后对上新位置", () => {
    const clock = new DanmakuClock();
    expect(clock.sync(100, 0)).toBe(true);
    expect(clock.now(5000)).toBe(100); // 还没开始播

    expect(clock.setPlaying(true, 5000)).toBe(true);
    expect(clock.now(6000)).toBeCloseTo(101);
    // 正常的进度上报(与推算值相差很小)不会让弹幕跳
    expect(clock.sync(101.05, 6000)).toBe(false);
    expect(clock.now(6500)).toBeCloseTo(101.5);

    expect(clock.setPlaying(false, 6500)).toBe(true);
    expect(clock.now(9000)).toBeCloseTo(101.5);
    expect(clock.isRunningAt(9000)).toBe(false);

    // 暂停中拖动进度
    expect(clock.sync(300, 9000)).toBe(true);
    expect(clock.now(9500)).toBe(300);

    clock.setPlaying(true, 10000);
    expect(clock.setRate(2, 10000)).toBe(true);
    expect(clock.now(11000)).toBeCloseTo(302);

    // 播放中往回拖
    expect(clock.sync(50, 11000)).toBe(true);
    expect(clock.now(11000)).toBe(50);
  });

  it("进度上报断了(缓冲卡住)时钟自己停下", () => {
    const clock = new DanmakuClock();
    clock.sync(10, 0);
    clock.setPlaying(true, 0);
    expect(clock.isRunningAt(1000)).toBe(true);
    expect(clock.now(60000)).toBeCloseTo(11.5);
    expect(clock.isRunningAt(60000)).toBe(false);
  });

  it("说在播但进度不动也算卡住,恢复后继续", () => {
    const clock = new DanmakuClock();
    clock.setPlaying(true, 0);
    clock.sync(20, 0);
    expect(clock.sync(20, 500)).toBe(true);
    expect(clock.isRunningAt(600)).toBe(false);
    expect(clock.now(5000)).toBe(20);
    expect(clock.sync(20.4, 5500)).toBe(true);
    expect(clock.isRunningAt(5600)).toBe(true);
    expect(clock.now(6500)).toBeCloseTo(21.4);
  });
});

describe("弹幕排布", () => {
  const options = { width: 1000, height: 500, fontSize: 20, scrollSeconds: 8, fixedSeconds: 4, measure: () => 200 };
  const engineOf = (comments: DanmakuComment[], extra: Partial<typeof options> & { maxItems?: number } = {}) =>
    new DanmakuEngine((from, to) => comments.filter((c) => c.time > from && c.time <= to), { ...options, ...extra });

  it("滚动弹幕从画面右边外匀速飘到左边外", () => {
    const engine = engineOf([comment(10)]);
    engine.advance(10);
    const [item] = engine.items;
    expect(item).toMatchObject({ mode: "scroll", start: 10, end: 18, lane: 0, y: 0 });
    expect(engine.xAt(item, 10)).toBe(1000);
    expect(engine.xAt(item, 14)).toBe(400);
    expect(engine.xAt(item, 18)).toBe(-200);
    engine.advance(11);
    engine.advance(12);
    expect(engine.items).toHaveLength(1);
    engine.advance(17.9);
    engine.advance(18.1);
    expect(engine.items).toHaveLength(0);
  });

  it("拖动进度后按新时间重建,包括已经飞到一半的弹幕", () => {
    const engine = engineOf([comment(10), comment(100)]);
    engine.advance(0);
    expect(engine.items).toHaveLength(0);

    // 往后拖到 14 秒:10 秒出现的那条正飞到一半
    engine.advance(14);
    expect(engine.items).toHaveLength(1);
    expect(engine.xAt(engine.items[0], 14)).toBe(400);

    engine.advance(103);
    expect(engine.items.map((item) => item.start)).toEqual([100]);

    // 往回拖
    engine.advance(12);
    expect(engine.items.map((item) => item.start)).toEqual([10]);
    engine.advance(50);
    expect(engine.items).toHaveLength(0);
  });

  it("顶部/底部弹幕居中固定显示几秒", () => {
    const engine = engineOf([comment(5, "顶", "top"), comment(5, "底", "bottom"), comment(6, "顶2", "top")]);
    engine.advance(5);
    engine.advance(6);
    const [top, bottom, top2] = engine.items;
    expect(top).toMatchObject({ mode: "top", lane: 0, y: 0, start: 5, end: 9 });
    expect(top2).toMatchObject({ mode: "top", lane: 1, y: 27 });
    expect(bottom).toMatchObject({ mode: "bottom", lane: 0, y: 423 });
    expect(engine.xAt(top, 5)).toBe(400);
    expect(engine.xAt(top, 8)).toBe(400);
    expect(engine.isVisibleAt(top, 8.9)).toBe(true);
    engine.advance(7);
    engine.advance(8);
    engine.advance(9.05);
    expect(engine.items.map((item) => item.comment.text)).toEqual(["顶2"]);
  });

  it("同一行的弹幕不重叠,太密时丢弃", () => {
    const engine = engineOf([comment(10, "a"), comment(10.1, "b"), comment(12, "c")]);
    engine.advance(10);
    engine.advance(10.1);
    engine.advance(11);
    engine.advance(12);
    expect(engine.items.map((item) => [item.comment.text, item.lane])).toEqual([
      ["a", 0],
      ["b", 1],
      ["c", 0],
    ]);

    const crowded = engineOf(
      Array.from({ length: 200 }, (_, i) => comment(10 + i / 1000)),
      { maxItems: 5 }
    );
    crowded.advance(10.5);
    expect(crowded.items.length).toBeLessThanOrEqual(5);
    expect(crowded.items.length).toBeGreaterThan(0);
  });

  it("带提前量时弹幕先建好,到时间才算出现", () => {
    const engine = engineOf([comment(10, "滚"), comment(10, "顶", "top")]);
    engine.advance(9.5, 0.6);
    expect(engine.items).toHaveLength(2);
    const [scroll, top] = engine.items;
    expect(engine.xAt(scroll, 9.5)).toBeGreaterThan(1000);
    expect(engine.isVisibleAt(top, 9.5)).toBe(false);
    expect(engine.isVisibleAt(top, 10)).toBe(true);
  });

  it("迟到的分片加载完后补排", () => {
    const comments: DanmakuComment[] = [];
    const engine = engineOf(comments);
    engine.advance(12);
    comments.push(comment(10));
    engine.notifyLoaded(0, 30);
    engine.advance(12.2);
    expect(engine.items.map((item) => item.start)).toEqual([10]);
  });
});

describe("弹幕分片加载", () => {
  it("播到哪里取到哪里,并提前取下一片", async () => {
    const loadSegment = jest.fn(async (index: number) => [comment(index * 30 + 2), comment(index * 30 + 1)]);
    const loaded: number[] = [];
    const track: DanmakuTrack = { provider: "t", title: "", segmentSeconds: 30, loadSegment };
    const timeline = new DanmakuTimeline(track, (index) => loaded.push(index));

    timeline.ensure(5);
    timeline.ensure(6);
    expect(loadSegment.mock.calls).toEqual([[0]]);
    await flush();
    expect(loaded).toEqual([0]);
    expect(timeline.between(0, 30).map((c) => c.time)).toEqual([1, 2]);
    expect(timeline.between(1, 30).map((c) => c.time)).toEqual([2]);

    timeline.ensure(25);
    expect(loadSegment.mock.calls).toEqual([[0], [1]]);
    await flush();
    expect(timeline.between(0, 60).map((c) => c.time)).toEqual([1, 2, 31, 32]);
    expect(timeline.rangeOf(1)).toEqual([30, 60]);
  });

  it("拖到分片开头附近时上一片也取(末尾的弹幕还在飞)", async () => {
    const loadSegment = jest.fn(async (index: number) => [comment(index * 30 + 29.5)]);
    const timeline = new DanmakuTimeline({ provider: "t", title: "", segmentSeconds: 30, loadSegment });
    timeline.ensure(600);
    expect(loadSegment.mock.calls.map((call) => call[0]).sort()).toEqual([19, 20]);
    await flush();
    const engine = new DanmakuEngine((from, to) => timeline.between(from, to), {
      width: 1000,
      height: 500,
      fontSize: 20,
      scrollSeconds: 8,
      fixedSeconds: 4,
    });
    engine.advance(600);
    expect(engine.items.map((item) => item.start)).toEqual([599.5]);
  });

  it("分片取失败只是这一段没有弹幕,稍后重试,不抛错", async () => {
    let wall = 0;
    let fail = true;
    const loadSegment = jest.fn(async () => {
      if (fail) throw new Error("network");
      return [comment(3)];
    });
    const timeline = new DanmakuTimeline(
      { provider: "t", title: "", segmentSeconds: 0, loadSegment },
      undefined,
      () => wall
    );
    timeline.ensure(1);
    await flush();
    expect(timeline.between(0, 100)).toEqual([]);

    wall = 5000;
    timeline.ensure(2);
    expect(loadSegment).toHaveBeenCalledTimes(1);

    wall = 25000;
    fail = false;
    timeline.ensure(3);
    await flush();
    expect(loadSegment).toHaveBeenCalledTimes(2);
    expect(timeline.between(0, 100).map((c) => c.time)).toEqual([3]);

    timeline.dispose();
    expect(() => timeline.ensure(4)).not.toThrow();
  });
});

describe("弹幕取数(来源选择)", () => {
  const TENCENT_SEARCH = "MbSearch";
  const TENCENT_EPISODES = "GetPageData";

  /** 腾讯视频上有"繁花"两集;哔哩哔哩不可用 */
  const builtinHttp = (calls: string[]): DanmakuHttp => ({
    async postJson(url) {
      calls.push(`POST ${url}`);
      if (url.includes(TENCENT_SEARCH)) {
        return {
          ret: 0,
          data: {
            normalList: {
              itemList: [
                { doc: { id: "other" }, videoInfo: { title: "繁花似锦", year: 2020, typeName: "电视剧" } },
                {
                  doc: { id: "cid1" },
                  videoInfo: { title: "<em>繁花</em>", year: 2023, typeName: "电视剧", subjectDoc: { videoNum: 30 } },
                },
              ],
            },
          },
        };
      }
      if (url.includes(TENCENT_EPISODES)) {
        return {
          data: {
            module_list_datas: [
              {
                module_datas: [
                  {
                    module_params: {},
                    item_data_lists: {
                      item_datas: [
                        { item_params: { vid: "v1", title: "1", play_title: "繁花 第01集" } },
                        { item_params: { vid: "v2", title: "2", play_title: "繁花 第02集" } },
                        { item_params: { vid: "v9", title: "3", play_title: "繁花 第03集预告", is_trailer: "1" } },
                      ],
                    },
                  },
                ],
              },
            ],
          },
        };
      }
      throw new Error("unexpected POST");
    },
    async getJson(url) {
      calls.push(`GET ${url}`);
      if (url.endsWith("/barrage/base/v2")) {
        return {
          segment_span: "30000",
          segment_index: {
            "0": { segment_start: "0", segment_name: "t/v1/0/30000" },
            "30000": { segment_start: "30000", segment_name: "t/v1/30000/60000" },
          },
        };
      }
      if (url.endsWith("/barrage/segment/v2/t/v1/30000/60000")) {
        return { barrage_list: [{ content: "第二片", time_offset: "31000" }] };
      }
      throw new Error("unavailable");
    },
    async getBytes() {
      throw new Error("unavailable");
    },
  });

  const query = { title: "繁花", year: "2023", episodeIndex: 1, episodeNumber: 2, episodeCount: 30 };

  it("不填服务器地址:直连腾讯视频公开接口,不访问任何自建服务", async () => {
    const calls: string[] = [];
    const track = await createDanmakuService(builtinHttp(calls)).resolve(query, "");
    expect(track).not.toBeNull();
    expect(track!.provider).toBe("tencent");
    expect(track!.title).toBe("繁花 第02集");
    expect(track!.segmentSeconds).toBe(30);
    expect(await track!.loadSegment(1)).toEqual([{ time: 31, text: "第二片", color: 0xffffff, mode: "scroll" }]);
    expect(await track!.loadSegment(7)).toEqual([]);
    expect(calls.every((call) => /https:\/\/(pbaccess|dm)\.video\.qq\.com\//.test(call))).toBe(true);
  });

  it("填了服务器地址:按 danmu_api 接口匹配并取弹幕", async () => {
    const calls: { method: string; url: string; body?: unknown }[] = [];
    const http: DanmakuHttp = {
      async postJson(url, body) {
        calls.push({ method: "POST", url, body });
        return { isMatched: true, matches: [{ episodeId: 10086, animeTitle: "庆余年第二季", episodeTitle: "第5集" }] };
      },
      async getJson(url) {
        calls.push({ method: "GET", url });
        return { count: 1, comments: [{ cid: 1, p: "7.5,5,16711680,[qq]", m: "来了" }] };
      },
      async getBytes() {
        throw new Error("unused");
      },
    };
    const track = await createDanmakuService(http).resolve(
      { title: "庆余年 第二季", episodeIndex: 4, episodeNumber: 5, episodeCount: 36 },
      "192.168.1.7:9321/"
    );
    expect(calls).toEqual([
      { method: "POST", url: "http://192.168.1.7:9321/api/v2/match", body: { fileName: "庆余年 S02E05" } },
      { method: "GET", url: "http://192.168.1.7:9321/api/v2/comment/10086?format=json" },
    ]);
    expect(track!.provider).toBe("server");
    expect(await track!.loadSegment(0)).toEqual([{ time: 7.5, text: "来了", color: 0xff0000, mode: "top" }]);
  });

  it("自定义服务器连不上或没匹配到时回到内置来源", async () => {
    for (const serverReply of ["throw", "empty"]) {
      const calls: string[] = [];
      const builtin = builtinHttp(calls);
      const http: DanmakuHttp = {
        ...builtin,
        async postJson(url, body, headers, timeoutMs) {
          if (url.startsWith("http://my.server")) {
            calls.push(`POST ${url}`);
            if (serverReply === "throw") throw new Error("ECONNREFUSED");
            return { isMatched: false, matches: [] };
          }
          return builtin.postJson(url, body, headers, timeoutMs);
        },
      };
      const track = await createDanmakuService(http).resolve(query, "http://my.server");
      expect(calls[0]).toBe("POST http://my.server/api/v2/match");
      expect(track!.provider).toBe("tencent");
    }
  });

  it("没有匹配到、全部来源失败、标题为空时都返回 null,不抛错", async () => {
    const dead: DanmakuHttp = {
      getJson: async () => {
        throw new Error("offline");
      },
      postJson: async () => {
        throw new Error("offline");
      },
      getBytes: async () => {
        throw new Error("offline");
      },
    };
    await expect(createDanmakuService(dead).resolve(query, "http://my.server")).resolves.toBeNull();
    await expect(createDanmakuService(dead).resolve({ ...query, title: "  " })).resolves.toBeNull();

    const calls: string[] = [];
    const service = createDanmakuService(builtinHttp(calls));
    await expect(service.resolve({ ...query, title: "完全不存在的片子" })).resolves.toBeNull();
    // 这部片没有第 9 集
    await expect(service.resolve({ ...query, episodeIndex: 8, episodeNumber: 9 })).resolves.toBeNull();
  });
});
