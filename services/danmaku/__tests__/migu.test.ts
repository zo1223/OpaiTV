import { createCipheriv } from "crypto";
import { DanmakuHttp } from "../http";
import {
  createMiguProvider,
  decodeMiguSegmentBody,
  miguAesEcbDecrypt,
  miguBase64ToBytes,
  miguUtf8Decode,
  parseMiguDuration,
  parseMiguEpisodePage,
  parseMiguSearch,
  parseMiguSegment,
} from "../migu";
import { DanmakuQuery } from "../types";

// 与 migu.ts 里固定密钥推导出的结果一致(base64 解码后逐个半字节查表)
const NIBBLES = [3, 5, 7, 0, 15, 10, 13, 1, 11, 14, 4, 6, 9, 12, 8, 2];
const KEY = Buffer.from(
  [...Buffer.from("vwwLu7e6ug4HAQMAug8CsA8HD7oHDwuxAg4HAQG6DLA=", "base64")].map((v) => (NIBBLES[v >>> 4] << 4) | NIBBLES[v & 15])
);

const encrypt = (value: unknown): string => {
  const cipher = createCipheriv("aes-256-ecb", KEY, null);
  return Buffer.concat([cipher.update(Buffer.from(JSON.stringify(value), "utf8")), cipher.final()]).toString("base64");
};

const bytesOf = (text: string) => new Uint8Array(Buffer.from(text, "utf8"));

const searchResponse = {
  body: {
    contentInfoList: [
      { shortMediaAsset: { name: "三体 花絮速看", isLong: 0, contDisplayName: "娱乐", pID: "1" } },
      {
        shortMediaAsset: {
          name: "三体",
          isLong: 1,
          contDisplayName: "电视剧",
          year: "2023 ",
          pID: "760834922",
          extraData: { episodes: ["760835542", "760835537", "760835548"] },
        },
      },
      { shortMediaAsset: { name: "三体之灵魂危机", isLong: 1, contDisplayName: "电影", year: "2015 ", pID: "611147049" } },
      {},
    ],
  },
};

const detailResponse = {
  code: 200,
  body: {
    data: {
      name: "三体",
      epsID: "760834922",
      totalPage: 1,
      pageSize: 150,
      playing: { pID: "760835542", duration: "42:44" },
      datas: [
        { name: "《三体》第01集：自杀者名单令汪淼震惊", index: "1", pID: "760835542", duration: "42:44" },
        { name: "《三体》第02集：汪淼物理认知被颠覆", index: "2", pID: "760835537", duration: "00:50" },
        { name: "《三体》第03集 预告", index: "3", pID: "760835548", duration: "01:00" },
      ],
    },
  },
};

const segmentResponse = {
  code: 200,
  body: {
    result: [
      { msg: "打卡", playtime: 64, textcolor: "#5FEA68" },
      { msg: "第一", playtime: 60, textcolor: "FBFFFF" },
      { msg: "又一条", playtime: 64, textcolor: "" },
      { msg: "  ", playtime: 65 },
      { msg: "没时间" },
      { msg: "负数", playtime: -1 },
    ],
  },
};

interface Call {
  method: string;
  url: string;
  body?: any;
  headers?: Record<string, string>;
}

function fakeHttp(handler: (call: Call) => any): { http: DanmakuHttp; calls: Call[] } {
  const calls: Call[] = [];
  const run = async (call: Call) => {
    calls.push(call);
    return handler(call);
  };
  return {
    calls,
    http: {
      getJson: (url, headers) => run({ method: "GET", url, headers }),
      postJson: (url, body, headers) => run({ method: "POST", url, body, headers }),
      getBytes: (url, headers) => run({ method: "BYTES", url, headers }),
    },
  };
}

const miguHttp = (overrides: Partial<{ search: any; detail: any }> = {}) =>
  fakeHttp((call) => {
    if (call.url.includes("open-search")) return overrides.search ?? searchResponse;
    if (call.url.includes("content-info")) return overrides.detail ?? detailResponse;
    if (call.url.includes("barrage")) return bytesOf(encrypt(segmentResponse));
    throw new Error("unexpected " + call.url);
  });

const query = (overrides: Partial<DanmakuQuery> = {}): DanmakuQuery => ({
  title: "三体",
  episodeIndex: 0,
  episodeNumber: 1,
  episodeCount: 30,
  ...overrides,
});

describe("咪咕弹幕:解析", () => {
  it("搜索结果只留长视频,剧集用第一集的号取分集", () => {
    expect(parseMiguSearch(searchResponse)).toEqual([
      { contentId: "760835542", title: "三体", year: "2023", isMovie: false, episodeCount: 3 },
      { contentId: "611147049", title: "三体之灵魂危机", year: "2015", isMovie: true, episodeCount: 1 },
    ]);
    expect(parseMiguSearch({})).toEqual([]);
    expect(parseMiguSearch({ body: { contentInfoList: null } })).toEqual([]);
  });

  it("时长", () => {
    expect(parseMiguDuration("42:44")).toBe(2564);
    expect(parseMiguDuration("01:00:04")).toBe(3604);
    expect(parseMiguDuration("90")).toBe(90);
    expect(parseMiguDuration("")).toBe(0);
    expect(parseMiguDuration("abc")).toBe(0);
    expect(parseMiguDuration(undefined)).toBe(0);
  });

  it("分集列表:带集号和时长,去掉预告", () => {
    const page = parseMiguEpisodePage(detailResponse);
    expect(page.seriesId).toBe("760834922");
    expect(page.totalPages).toBe(1);
    expect(page.pageSize).toBe(150);
    expect(page.episodes).toEqual([
      { pid: "760835542", number: 1, title: "《三体》第01集：自杀者名单令汪淼震惊", duration: 2564 },
      { pid: "760835537", number: 2, title: "《三体》第02集：汪淼物理认知被颠覆", duration: 50 },
    ]);
  });

  it("没有分集列表的单片用正在播放的那一条;空应答没有分集", () => {
    const page = parseMiguEpisodePage({
      body: { data: { name: "三体之灵魂危机", epsID: "955171179", playing: { pID: "611147049", duration: "01:00:04" } } },
    });
    expect(page.episodes).toEqual([{ pid: "611147049", number: null, title: "三体之灵魂危机", duration: 3604 }]);
    expect(parseMiguEpisodePage({ code: 410 }).episodes).toEqual([]);
  });

  it("弹幕:按时间排序,颜色可带 #,同一秒摊开,丢掉空白和没有时间的", () => {
    expect(parseMiguSegment(segmentResponse)).toEqual([
      { time: 60, text: "第一", color: 0xfbffff, mode: "scroll" },
      { time: 64, text: "打卡", color: 0x5fea68, mode: "scroll" },
      { time: 64.5, text: "又一条", color: 0xffffff, mode: "scroll" },
    ]);
    expect(parseMiguSegment({ body: {} })).toEqual([]);
    expect(parseMiguSegment(null)).toEqual([]);
  });
});

describe("咪咕弹幕:解密", () => {
  it("base64 与 UTF-8", () => {
    expect(Array.from(miguBase64ToBytes("AQID\n/v8="))).toEqual([1, 2, 3, 254, 255]);
    expect(() => miguBase64ToBytes("abc")).toThrow();
    expect(() => miguBase64ToBytes("ab*d")).toThrow();
    expect(miguUtf8Decode(bytesOf("弹幕 abc é 😀"))).toBe("弹幕 abc é 😀");
  });

  it("AES-256-ECB 解密结果与 Node 自带实现互相对得上", () => {
    for (const text of ["", "a", "0123456789abcdef", "看过原著小说就知道，此剧有多强".repeat(40)]) {
      const cipher = createCipheriv("aes-256-ecb", KEY, null);
      const encrypted = Buffer.concat([cipher.update(Buffer.from(text, "utf8")), cipher.final()]);
      expect(miguUtf8Decode(miguAesEcbDecrypt(new Uint8Array(encrypted), new Uint8Array(KEY)))).toBe(text);
    }
    expect(() => miguAesEcbDecrypt(new Uint8Array(15), new Uint8Array(KEY))).toThrow();
    expect(() => miguAesEcbDecrypt(new Uint8Array(16), new Uint8Array(16))).toThrow();
    // 不是用这把密钥加密的内容:填充对不上
    expect(() => miguAesEcbDecrypt(new Uint8Array(16).fill(7), new Uint8Array(KEY))).toThrow();
  });

  it("分片应答:加密文本解密后解析;明文 JSON 直接解析;解不开抛错", () => {
    expect(decodeMiguSegmentBody(encrypt(segmentResponse))).toEqual(segmentResponse);
    expect(decodeMiguSegmentBody(' {"body":{"result":[]}} ')).toEqual({ body: { result: [] } });
    expect(decodeMiguSegmentBody("")).toBeNull();
    expect(() => decodeMiguSegmentBody("<html>error</html>")).toThrow();
  });
});

describe("咪咕弹幕:取数", () => {
  it("匹配到剧集后按 30 秒分片取弹幕并解密", async () => {
    const { http, calls } = miguHttp();
    const track = await createMiguProvider(http).resolve(query());
    expect(track).not.toBeNull();
    expect(track!.provider).toBe("migu");
    expect(track!.title).toContain("第01集");
    expect(track!.segmentSeconds).toBe(30);
    expect(calls[0].body.k).toBe("三体");
    expect(calls[1].url).toBe("https://v3-sc.miguvideo.com/program/v4/cont/content-info/760835542/1");

    const comments = await track!.loadSegment(2);
    expect(comments.map((c) => c.text)).toEqual(["第一", "打卡", "又一条"]);
    const segment = calls[calls.length - 1];
    expect(segment.url).toBe(
      "https://webapi.miguvideo.com/gateway/live_barrage/videox/barrage/v2/list/760834922/760835542/60/90/020"
    );
    expect(segment.headers!.appCode).toBe("miguvideo_default_h5");

    // 最后一个分片截到片长;片长之外不发请求
    await track!.loadSegment(85);
    expect(calls[calls.length - 1].url).toContain("/2550/2564/020");
    const before = calls.length;
    expect(await track!.loadSegment(86)).toEqual([]);
    expect(calls.length).toBe(before);
  });

  it("同一部片换集不重新搜索", async () => {
    const { http, calls } = miguHttp();
    const provider = createMiguProvider(http);
    await provider.resolve(query());
    const second = await provider.resolve(query({ episodeIndex: 1, episodeNumber: 2 }));
    expect(second!.title).toContain("第02集");
    expect(calls.filter((c) => c.url.includes("open-search")).length).toBe(1);
    expect(calls.filter((c) => c.url.includes("content-info")).length).toBe(1);
  });

  it("没有这部片、没有这一集、不知道时长都返回 null", async () => {
    expect(await createMiguProvider(miguHttp().http).resolve(query({ title: "狂飙" }))).toBeNull();
    expect(await createMiguProvider(miguHttp({ search: { body: {} } }).http).resolve(query())).toBeNull();
    expect(await createMiguProvider(miguHttp().http).resolve(query({ episodeIndex: 8, episodeNumber: 9 }))).toBeNull();
    const noDuration = { body: { data: { epsID: "1", datas: [{ name: "第1集", index: "1", pID: "9" }] } } };
    expect(await createMiguProvider(miguHttp({ detail: noDuration }).http).resolve(query())).toBeNull();
  });

  it("季号不同的不算同一部片", async () => {
    const { http } = miguHttp();
    expect(await createMiguProvider(http).resolve(query({ title: "三体 第二季" }))).toBeNull();
  });

  it("网络错误抛出,且不缓存失败", async () => {
    let dead = true;
    const { http } = fakeHttp((call) => {
      if (dead) throw new Error("network error");
      if (call.url.includes("open-search")) return searchResponse;
      if (call.url.includes("content-info")) return detailResponse;
      throw new Error("segment down");
    });
    const provider = createMiguProvider(http);
    await expect(provider.resolve(query())).rejects.toThrow("network error");
    dead = false;
    const track = await provider.resolve(query());
    expect(track).not.toBeNull();
    await expect(track!.loadSegment(0)).rejects.toThrow("segment down");
  });
});
