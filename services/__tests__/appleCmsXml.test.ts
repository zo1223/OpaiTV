import { parseAppleCmsXml } from "../appleCmsXml";
import {
  fetchAppleCmsCategories,
  fetchAppleCmsCategoryVideos,
  getAppleCmsDetail,
  getAppleCmsSourceResult,
  searchAppleCmsSource,
} from "../videoSourceSubscription";

// ac=list 的应答:分类 + 只有 id/名称/分类的简表(经典 XML 接口,TVBox type=0)
const LIST_XML = `<?xml version="1.0" encoding="utf-8"?>
<rss version="5.1">
  <list page="2" pagecount="35" pagesize="20" recordcount="689">
    <video>
      <last>2026-11-20 10:00:00</last>
      <id>101</id>
      <tid>6</tid>
      <name><![CDATA[第一部 & 续集]]></name>
      <type>动作片</type>
      <dt>m3u8</dt>
      <note><![CDATA[HD]]></note>
    </video>
    <video>
      <last>2026-11-19 10:00:00</last>
      <id>102</id>
      <tid>13</tid>
      <name><![CDATA[第二部]]></name>
      <type>国产剧</type>
      <dt>m3u8</dt>
      <note><![CDATA[更新至10集]]></note>
    </video>
  </list>
  <class>
    <ty id="1">电影</ty>
    <ty id="2">连续剧</ty>
    <ty id="6">动作片</ty>
  </class>
</rss>`;

// ac=videolist 的应答:带图片、简介和播放地址的完整条目
const VIDEOLIST_XML = `<?xml version="1.0" encoding="utf-8"?>
<rss version="5.1">
  <list page="1" pagecount="1" pagesize="20" recordcount="2">
    <video>
      <last>2026-11-20 10:00:00</last>
      <id>101</id>
      <tid>6</tid>
      <name><![CDATA[第一部 & 续集]]></name>
      <type>动作片</type>
      <pic>https://img.example/101.jpg?a=1&amp;b=2</pic>
      <lang>国语</lang>
      <area>大陆</area>
      <year>2026</year>
      <state>0</state>
      <note><![CDATA[HD]]></note>
      <actor><![CDATA[演员甲,演员乙]]></actor>
      <director><![CDATA[导演甲]]></director>
      <dl>
        <dd flag="m3u8"><![CDATA[正片$https://video.example/101/index.m3u8]]></dd>
      </dl>
      <des><![CDATA[<p>简介 &amp; 说明</p>]]></des>
    </video>
    <video>
      <id>102</id>
      <tid>13</tid>
      <name><![CDATA[第二部]]></name>
      <type>国产剧</type>
      <pic>https://img.example/102.jpg</pic>
      <year>2025</year>
      <note><![CDATA[更新至10集]]></note>
      <dl>
        <dd flag="yun"><![CDATA[第1集$https://yun.example/102/1.html#第2集$https://yun.example/102/2.html]]></dd>
        <dd flag="m3u8"><![CDATA[第1集$https://video.example/102/1.m3u8#第2集$https://video.example/102/2.m3u8]]></dd>
      </dl>
      <des><![CDATA[第二部简介]]></des>
    </video>
  </list>
</rss>`;

describe("parseAppleCmsXml", () => {
  it("parses the class list and paging of a list response", () => {
    const data = parseAppleCmsXml(LIST_XML)!;

    expect(data.class).toEqual([
      { type_id: "1", type_name: "电影" },
      { type_id: "2", type_name: "连续剧" },
      { type_id: "6", type_name: "动作片" },
    ]);
    expect(data).toMatchObject({ page: 2, pagecount: 35, limit: 20, total: 689 });
    expect(data.list).toEqual([
      { vod_id: "101", vod_name: "第一部 & 续集", type_id: "6", type_name: "动作片", vod_remarks: "HD", vod_time: "2026-11-20 10:00:00" },
      { vod_id: "102", vod_name: "第二部", type_id: "13", type_name: "国产剧", vod_remarks: "更新至10集", vod_time: "2026-11-19 10:00:00" },
    ]);
  });

  it("maps full video entries to the JSON field names, including play lines", () => {
    const [first, second] = parseAppleCmsXml(VIDEOLIST_XML)!.list;

    expect(first).toMatchObject({
      vod_id: "101",
      vod_name: "第一部 & 续集",
      vod_pic: "https://img.example/101.jpg?a=1&b=2",
      type_id: "6",
      type_name: "动作片",
      vod_remarks: "HD",
      vod_year: "2026",
      vod_area: "大陆",
      vod_lang: "国语",
      vod_actor: "演员甲,演员乙",
      vod_director: "导演甲",
      // CDATA 内容原样保留,不做实体解码
      vod_content: "<p>简介 &amp; 说明</p>",
      vod_play_from: "m3u8",
      vod_play_url: "正片$https://video.example/101/index.m3u8",
    });
    expect(second.vod_play_from).toBe("yun$$$m3u8");
    expect(second.vod_play_url).toBe(
      "第1集$https://yun.example/102/1.html#第2集$https://yun.example/102/2.html$$$第1集$https://video.example/102/1.m3u8#第2集$https://video.example/102/2.m3u8"
    );
  });

  it("handles other common shapes: no rss wrapper, single quotes, plain text, empty and self-closing tags", () => {
    const data = parseAppleCmsXml(
      `﻿<list page='1' pagecount='3'>
         <!-- comment -->
         <video><id>7</id><name>A &lt;B&gt; &#x4E2D;&#25991;</name><pic/><note></note>
           <dl><dd flag='a'></dd><dd flag="b">HD$https://v.example/7.m3u8</dd><dd>HD$https://v.example/7b.m3u8</dd></dl>
         </video>
       </list>
       <class><ty id='5'>综艺</ty><ty id="">空</ty><ty id="9"/></class>`
    )!;

    expect(data.class).toEqual([{ type_id: "5", type_name: "综艺" }]);
    expect(data).toMatchObject({ page: 1, pagecount: 3 });
    expect(data.total).toBeUndefined();
    // 没有内容的 dd 被跳过,线路名与播放组保持一一对应
    expect(data.list).toEqual([
      {
        vod_id: "7",
        vod_name: "A <B> 中文",
        vod_play_from: "b$$$",
        vod_play_url: "HD$https://v.example/7.m3u8$$$HD$https://v.example/7b.m3u8",
      },
    ]);
  });

  it("accepts sites that reuse the JSON field names as tags", () => {
    const data = parseAppleCmsXml(
      `<rss><list><video><vod_id>3</vod_id><vod_name>N</vod_name><vod_pic>p.jpg</vod_pic><type_name>电影</type_name>
       <vod_remarks>R</vod_remarks><vod_play_from>m3u8</vod_play_from><vod_play_url>HD$u.m3u8</vod_play_url></video></list></rss>`
    )!;

    expect(data.list).toEqual([
      {
        vod_id: "3",
        vod_name: "N",
        vod_pic: "p.jpg",
        type_name: "电影",
        vod_remarks: "R",
        vod_play_from: "m3u8",
        vod_play_url: "HD$u.m3u8",
      },
    ]);
  });

  it("returns an empty result for a valid but empty response", () => {
    expect(parseAppleCmsXml(`<?xml version="1.0"?><rss version="5.1"><list page="1" pagecount="0" pagesize="20" recordcount="0"></list></rss>`)).toEqual({
      class: [],
      list: [],
      page: 1,
      pagecount: 0,
      limit: 20,
      total: 0,
    });
  });

  it.each([
    ["JSON", `{"code":1,"list":[]}`],
    ["an HTML error page", `<!DOCTYPE html><html><body><ul class="list"><li>502</li></ul></body></html>`],
    ["HTML without a doctype", `<html><head></head><body><div class="list"></div></body></html>`],
    ["unrelated XML", `<?xml version="1.0"?><feed><entry><id>1</id></entry></feed>`],
    ["plain text", "Service Unavailable"],
    ["an empty body", ""],
  ])("rejects %s", (_label, body) => {
    expect(parseAppleCmsXml(body)).toBeNull();
  });
});

describe("XML Apple CMS sources", () => {
  const source = { key: "xml", name: "XML 源", api: "https://xml.example/api.php/provide/vod/at/xml" };
  const xmlResponse = (body: string) =>
    new Response(body, { status: 200, headers: { "Content-Type": "text/xml; charset=utf-8" } });

  // 按查询参数应答;匹配不到的请求算测试失败
  const mockFetch = (routes: [RegExp, string][]) =>
    jest.spyOn(global, "fetch").mockImplementation(async (input) => {
      const url = String(input);
      const route = routes.find(([pattern]) => pattern.test(url));
      if (!route) {
        throw new Error(`unexpected request: ${url}`);
      }
      return xmlResponse(route[1]);
    });

  const requestedUrls = () => (fetch as jest.Mock).mock.calls.map(([input]) => String(input));

  beforeEach(() => {
    jest.restoreAllMocks();
  });

  it("reads categories from an XML response", async () => {
    mockFetch([[/\?ac=list$/, LIST_XML]]);

    await expect(fetchAppleCmsCategories(source)).resolves.toEqual([
      { id: "1", name: "电影" },
      { id: "2", name: "连续剧" },
      { id: "6", name: "动作片" },
    ]);
  });

  it("uses a single request when the XML answer to ac=detail already carries play URLs", async () => {
    mockFetch([[/\?ac=detail&t=6&pg=1$/, VIDEOLIST_XML]]);

    const page = await fetchAppleCmsCategoryVideos(source, "6", 1, 20);

    expect(requestedUrls()).toHaveLength(1);
    expect(page).toMatchObject({ total: 2, page: 1, pageCount: 1 });
    expect(page.results).toEqual([
      expect.objectContaining({
        id: 101,
        title: "第一部 & 续集",
        poster: "https://img.example/101.jpg?a=1&b=2",
        episodes: ["https://video.example/101/index.m3u8"],
        source: "xml",
        source_name: "XML 源",
        year: "2026",
        type_name: "动作片",
      }),
      expect.objectContaining({
        id: 102,
        title: "第二部",
        episodes: ["https://yun.example/102/1.html", "https://yun.example/102/2.html"],
        playback_sources: [
          expect.objectContaining({ name: "yun" }),
          expect.objectContaining({ name: "m3u8", episodes: ["https://video.example/102/1.m3u8", "https://video.example/102/2.m3u8"] }),
        ],
      }),
    ]);
  });

  it("fetches pictures and play URLs with ac=videolist when a classic interface answers with the brief list", async () => {
    mockFetch([
      [/\?ac=detail&t=6&pg=2$/, LIST_XML],
      [/\?ac=videolist&ids=101%2C102$/, VIDEOLIST_XML],
    ]);

    const page = await fetchAppleCmsCategoryVideos(source, "6", 2, 20);

    expect(requestedUrls()).toHaveLength(2);
    // 翻页信息以列表应答为准,不能被 ac=videolist 的应答(只有这一页的条目)盖掉
    expect(page).toMatchObject({ total: 689, page: 2, pageCount: 35 });
    expect(page.results.map((item) => [item.id, item.poster, item.episodes.length])).toEqual([
      [101, "https://img.example/101.jpg?a=1&b=2", 1],
      [102, "https://img.example/102.jpg", 2],
    ]);
  });

  it("keeps the brief entries when the follow-up request fails", async () => {
    jest.spyOn(global, "fetch").mockImplementation(async (input) => {
      if (String(input).includes("ac=videolist")) {
        throw new Error("Network request failed");
      }
      return xmlResponse(LIST_XML);
    });

    const page = await fetchAppleCmsCategoryVideos(source, "6", 2, 20);

    expect(page.results.map((item) => [item.id, item.title, item.episodes])).toEqual([
      [101, "第一部 & 续集", []],
      [102, "第二部", []],
    ]);
  });

  it("searches an XML source", async () => {
    mockFetch([
      [/\?ac=detail&wd=%E7%AC%AC$/, LIST_XML],
      [/\?ac=videolist&ids=101%2C102$/, VIDEOLIST_XML],
    ]);

    const results = await searchAppleCmsSource(source, "第");

    expect(results.map((item) => [item.id, item.title, item.episodes[0]])).toEqual([
      [101, "第一部 & 续集", "https://video.example/101/index.m3u8"],
      [102, "第二部", "https://yun.example/102/1.html"],
    ]);
  });

  it("returns no search results for an empty XML answer without a second request", async () => {
    mockFetch([[/\?ac=detail&wd=none$/, `<rss><list page="1" pagecount="0" pagesize="20" recordcount="0"></list></rss>`]]);

    await expect(searchAppleCmsSource(source, "none")).resolves.toEqual([]);
    expect(requestedUrls()).toHaveLength(1);
  });

  it("loads the requested video, not the first list entry, when ac=detail ignores ids", async () => {
    // 不认识 ac=detail 的老接口忽略 ids,返回最新列表(第一条是 101)
    mockFetch([
      [/\?ac=detail&ids=102$/, LIST_XML],
      [/\?ac=videolist&ids=102$/, VIDEOLIST_XML],
    ]);

    await expect(getAppleCmsDetail(source, "102")).resolves.toMatchObject({
      id: "102",
      title: "第二部",
      poster: "https://img.example/102.jpg",
      desc: "第二部简介",
      type: "13",
      year: "2025",
      remarks: "更新至10集",
    });

    const results = await getAppleCmsSourceResult(source, "102");
    expect(results).toHaveLength(1);
    expect(results[0]).toMatchObject({ id: 102, episodes: ["https://yun.example/102/1.html", "https://yun.example/102/2.html"] });
  });

  it("reports a missing video instead of an unrelated one", async () => {
    mockFetch([
      [/\?ac=detail&ids=999$/, LIST_XML],
      [/\?ac=videolist&ids=999$/, `<rss><list page="1" pagecount="0" pagesize="20" recordcount="0"></list></rss>`],
    ]);

    await expect(getAppleCmsDetail(source, "999")).rejects.toThrow("APPLE_CMS_DETAIL_NOT_FOUND");
  });

  it("still fails like before when the response is neither JSON nor Apple CMS XML", async () => {
    jest.spyOn(global, "fetch").mockImplementation(async () => new Response("<html><body>502 Bad Gateway</body></html>"));

    await expect(fetchAppleCmsCategories(source)).rejects.toMatchObject({ name: "SyntaxError" });
  });

  it("keeps reading JSON sources through response.json() only", async () => {
    const json = jest.fn(async () => ({
      class: [{ type_id: 1, type_name: "电影" }],
      list: [{ vod_id: 5, vod_name: "JSON 片", vod_play_url: "HD$https://video.example/5.m3u8" }],
      total: 1,
      page: 1,
      pagecount: 1,
    }));
    const text = jest.fn();
    jest.spyOn(global, "fetch").mockImplementation(async () => ({ ok: true, json, text, clone: () => ({ text }) }) as unknown as Response);

    await expect(fetchAppleCmsCategories(source)).resolves.toEqual([{ id: "1", name: "电影" }]);
    const page = await fetchAppleCmsCategoryVideos(source, "1", 1, 20);

    expect(page.results.map((item) => item.title)).toEqual(["JSON 片"]);
    expect(requestedUrls()).toHaveLength(2);
    expect(text).not.toHaveBeenCalled();
  });
});
