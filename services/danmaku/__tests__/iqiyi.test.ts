import { strToU8, zlibSync } from "fflate";
import { DanmakuHttp } from "../http";
import {
  buildIqiyiSegmentUrl,
  createIqiyiProvider,
  decodeIqiyiSegmentBytes,
  parseIqiyiDuration,
  parseIqiyiEpisodePage,
  parseIqiyiSearch,
  parseIqiyiXml,
} from "../iqiyi";

const album = (overrides: Record<string, unknown> = {}) => ({
  albumDocInfo: {
    albumId: 5328486914190101,
    albumTitle: "狂飙",
    channel: "电视剧,2",
    siteId: "iqiyi",
    videoDocType: 1,
    releaseDate: "20230114",
    itemTotalNumber: 39,
    ...overrides,
  },
});

const SEARCH = {
  data: {
    code: 0,
    docinfos: [
      album({ albumId: 111, albumTitle: "电视剧《狂飙》原创解读", channel: "教育,12" }),
      album({ albumId: 222, albumTitle: "狂飙", siteId: "sohu" }),
      album({ albumId: 333, albumTitle: "狂飙 片段", videoDocType: 2 }),
      album(),
      album(),
      album({
        albumId: 7292991076670500,
        albumTitle: "流浪地球2",
        channel: "电影,1",
        releaseDate: "20230122",
        itemTotalNumber: 1,
        videoinfos: [{ tvId: 7292991076670501 }],
      }),
    ],
  },
};

const episodeItem = (order: number, tvId: number, extra: Record<string, unknown> = {}) => ({
  tvId,
  order,
  name: `狂飙第${order}集`,
  contentType: 1,
  ...extra,
});

const EPISODES = {
  code: "A00000",
  data: {
    total: 4,
    epsodelist: [
      episodeItem(1, 3493131456125200),
      episodeItem(2, 6173969719145800),
      episodeItem(3, 900, { contentType: 3, name: "狂飙第3集预告" }),
      episodeItem(3, 4296901635527000),
      episodeItem(3, 4296901635527000),
    ],
  },
};

const XML = `<?xml version="1.0" encoding="UTF-8"?><danmu><data><entry><int>1</int><list>
<bulletInfo><contentId>1</contentId><content>京海不大，创造神话</content><showTime>1</showTime><color>ffffff</color><position>0</position></bulletInfo>
<bulletInfo><contentId>2</contentId><content>电影&#127916;的质感 &amp; 好看</content><showTime>1</showTime><color>ff0000</color></bulletInfo>
<bulletInfo><contentId>3</contentId><content>   </content><showTime>2</showTime><color>ffffff</color></bulletInfo>
<bulletInfo><contentId>4</contentId><content>没有时间</content><showTime>abc</showTime><color>ffffff</color></bulletInfo>
<bulletInfo><contentId>5</contentId><content>坏颜色</content><showTime>299</showTime><color>zzz</color></bulletInfo>
</list></entry></data></danmu>`;

interface FakeHttp extends DanmakuHttp {
  calls: string[];
}

function fakeHttp(routes: (url: string) => unknown): FakeHttp {
  const calls: string[] = [];
  const answer = async (url: string) => {
    calls.push(url);
    const value = routes(url);
    if (value instanceof Error) throw value;
    return value as any;
  };
  return {
    calls,
    getJson: answer,
    postJson: (url) => answer(url),
    getBytes: answer,
  };
}

const standardRoutes = (url: string): unknown => {
  if (url.includes("search.video.iqiyi.com")) return SEARCH;
  if (url.includes("avlistinfo")) return EPISODES;
  if (url.includes("baseinfo")) return { code: "A00000", data: { durationSec: 2733 } };
  if (url.includes("cmts.iqiyi.com")) return zlibSync(strToU8(XML));
  return new Error("unexpected " + url);
};

describe("爱奇艺弹幕:解析", () => {
  it("搜索结果只留爱奇艺站内的正片专辑并去重", () => {
    const candidates = parseIqiyiSearch(SEARCH);
    expect(candidates).toEqual([
      { albumId: "5328486914190101", movieTvId: "", title: "狂飙", year: "2023", isMovie: false, episodeCount: 39 },
      {
        albumId: "7292991076670500",
        movieTvId: "7292991076670501",
        title: "流浪地球2",
        year: "2023",
        isMovie: true,
        episodeCount: 1,
      },
    ]);
    expect(parseIqiyiSearch({ data: "search error" })).toEqual([]);
    expect(parseIqiyiSearch(null)).toEqual([]);
  });

  it("分集只留正片,按 tvId 去重", () => {
    const page = parseIqiyiEpisodePage(EPISODES);
    expect(page.total).toBe(4);
    expect(page.episodes.map((e) => [e.number, e.tvId])).toEqual([
      [1, "3493131456125200"],
      [2, "6173969719145800"],
      [3, "4296901635527000"],
    ]);
    expect(parseIqiyiEpisodePage({}).episodes).toEqual([]);
  });

  it("时长:关闭弹幕或没有时长时为 0", () => {
    expect(parseIqiyiDuration({ data: { durationSec: 2733 } })).toBe(2733);
    expect(parseIqiyiDuration({ data: { durationSec: 2733, displayBarrage: false } })).toBe(0);
    expect(parseIqiyiDuration({ data: {} })).toBe(0);
    expect(parseIqiyiDuration(null)).toBe(0);
  });

  it("分片地址用视频编号末四位做目录", () => {
    expect(buildIqiyiSegmentUrl("3493131456125200", 0)).toBe(
      "https://cmts.iqiyi.com/bullet/52/00/3493131456125200_300_1.z"
    );
    expect(buildIqiyiSegmentUrl("123", 9)).toBe("https://cmts.iqiyi.com/bullet/01/23/123_300_10.z");
  });

  it("解析弹幕 XML:解实体、丢掉空的和没时间的、同一秒摊开", () => {
    const comments = parseIqiyiXml(XML);
    expect(comments).toEqual([
      { time: 1, text: "京海不大，创造神话", color: 0xffffff, mode: "scroll" },
      { time: 1.5, text: "电影🎬的质感 & 好看", color: 0xff0000, mode: "scroll" },
      { time: 299, text: "坏颜色", color: 0xffffff, mode: "scroll" },
    ]);
    expect(parseIqiyiXml("")).toEqual([]);
  });

  it("分片字节:zlib 解压,已是文本直接用,解不开为空", () => {
    expect(decodeIqiyiSegmentBytes(zlibSync(strToU8(XML)))).toBe(XML);
    expect(decodeIqiyiSegmentBytes(strToU8(XML))).toBe(XML);
    expect(decodeIqiyiSegmentBytes(new Uint8Array([1, 2, 3]))).toBe("");
    expect(decodeIqiyiSegmentBytes(new Uint8Array(0))).toBe("");
  });
});

describe("爱奇艺弹幕:匹配", () => {
  const query = { title: "狂飙", episodeIndex: 1, episodeNumber: 2, episodeCount: 39, year: "2023" };

  it("剧集:搜索 → 分集 → 时长 → 按需取分片", async () => {
    const http = fakeHttp(standardRoutes);
    const provider = createIqiyiProvider(http);
    expect(provider.name).toBe("iqiyi");
    const track = await provider.resolve(query);
    expect(track).not.toBeNull();
    expect(track!.provider).toBe("iqiyi");
    expect(track!.title).toBe("狂飙第2集");
    expect(track!.segmentSeconds).toBe(300);
    expect(http.calls[0]).toContain(`key=${encodeURIComponent("狂飙")}`);
    expect(http.calls[1]).toContain("aid=5328486914190101&page=1&size=200");
    expect(http.calls[2]).toBe("https://pcw-api.iqiyi.com/video/video/baseinfo/6173969719145800");
    expect(http.calls).toHaveLength(3);

    const comments = await track!.loadSegment(9);
    expect(http.calls[3]).toBe("https://cmts.iqiyi.com/bullet/58/00/6173969719145800_300_10.z");
    expect(comments).toHaveLength(3);
    // 2733 秒只有 10 个分片,超出的不发请求
    expect(await track!.loadSegment(10)).toEqual([]);
    expect(http.calls).toHaveLength(4);
  });

  it("同一部片换集不重复搜索", async () => {
    const http = fakeHttp(standardRoutes);
    const provider = createIqiyiProvider(http);
    await provider.resolve(query);
    const track = await provider.resolve({ ...query, episodeIndex: 0, episodeNumber: 1 });
    expect(track!.title).toBe("狂飙第1集");
    expect(http.calls.filter((url) => url.includes("search.video")).length).toBe(1);
    expect(http.calls.filter((url) => url.includes("avlistinfo")).length).toBe(1);
  });

  it("电影直接用搜索结果里的视频编号", async () => {
    const http = fakeHttp(standardRoutes);
    const track = await createIqiyiProvider(http).resolve({
      title: "流浪地球2",
      episodeIndex: 0,
      episodeNumber: 1,
      episodeCount: 1,
    });
    expect(track!.title).toBe("流浪地球2");
    expect(http.calls.some((url) => url.includes("avlistinfo"))).toBe(false);
    expect(http.calls[1]).toBe("https://pcw-api.iqiyi.com/video/video/baseinfo/7292991076670501");
  });

  it("没有这部片、没有这一集、没有弹幕都返回 null", async () => {
    const http = fakeHttp(standardRoutes);
    const provider = createIqiyiProvider(http);
    expect(await provider.resolve({ ...query, title: "藏海传" })).toBeNull();
    expect(await provider.resolve({ ...query, episodeIndex: 29, episodeNumber: 30 })).toBeNull();

    const closed = fakeHttp((url) =>
      url.includes("baseinfo") ? { data: { durationSec: 2733, displayBarrage: false } } : standardRoutes(url)
    );
    expect(await createIqiyiProvider(closed).resolve(query)).toBeNull();
  });

  it("网络出错时抛出,且不缓存失败", async () => {
    let down = true;
    const http = fakeHttp((url) => (down ? new Error("network error") : standardRoutes(url)));
    const provider = createIqiyiProvider(http);
    await expect(provider.resolve(query)).rejects.toThrow("network error");
    down = false;
    expect(await provider.resolve(query)).not.toBeNull();

    const segmentDown = fakeHttp((url) => (url.includes("cmts") ? new Error("HTTP 500") : standardRoutes(url)));
    const track = await createIqiyiProvider(segmentDown).resolve(query);
    await expect(track!.loadSegment(0)).rejects.toThrow("HTTP 500");
  });

  it("超过一页的长剧只取当前集所在的那一页", async () => {
    const pageOf = (page: number) => ({
      data: {
        total: 450,
        epsodelist: Array.from({ length: 200 }, (_, i) => episodeItem((page - 1) * 200 + i + 1, 1000 + (page - 1) * 200 + i)),
      },
    });
    const http = fakeHttp((url) => {
      if (url.includes("search.video")) return { data: { docinfos: [album({ albumTitle: "长剧", itemTotalNumber: 450 })] } };
      const match = url.match(/avlistinfo\?aid=\d+&page=(\d+)/);
      if (match) return pageOf(Number(match[1]));
      return standardRoutes(url);
    });
    const track = await createIqiyiProvider(http).resolve({
      title: "长剧",
      episodeIndex: 249,
      episodeNumber: 250,
      episodeCount: 450,
    });
    expect(track!.title).toBe("狂飙第250集");
    expect(http.calls.filter((url) => url.includes("avlistinfo")).map((url) => url.match(/page=(\d+)/)![1])).toEqual([
      "1",
      "2",
    ]);
  });
});
