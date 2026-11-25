/**
 * 内置备用弹幕来源:哔哩哔哩番剧/影视公开接口(搜索 → 分集 → 弹幕 XML),不需要登录。
 * 腾讯视频匹配不到时才用。搜索接口要 WBI 签名,且对部分网络有风控(返回 v_voucher),此时按未匹配处理。
 */
import { gunzipSync, inflateSync, strFromU8, unzlibSync } from "fflate";
import { md5 } from "@/utils/md5";
import { DANMAKU_USER_AGENT, DanmakuHttp } from "./http";
import {
  isExtraEpisodeTitle,
  parseEpisodeNumber,
  parseTitle,
  pickBestCandidate,
  pickEpisodeIndex,
  TitleCandidate,
} from "./title";
import { DanmakuComment, DanmakuMode, DanmakuProvider, DanmakuQuery } from "./types";

const WBI_MIXIN_TABLE = [
  46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41,
  13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34,
  44, 52,
];
const SESSION_TTL_MS = 60 * 60 * 1000;

export interface BilibiliCandidate extends TitleCandidate {
  seasonId: number;
}

interface BilibiliEpisode {
  cid: number;
  number: number | null;
  title: string;
}

interface BilibiliSession {
  cookie: string;
  mixinKey: string;
  createdAt: number;
}

/** 由 nav 接口返回的两张图片地址算出 WBI 签名用的 mixin key */
export function buildWbiMixinKey(imgUrl: string, subUrl: string): string {
  const keyOf = (url: string) => (url.split("/").pop() || "").split(".")[0];
  const raw = keyOf(imgUrl) + keyOf(subUrl);
  return WBI_MIXIN_TABLE.map((index) => raw[index] ?? "")
    .join("")
    .slice(0, 32);
}

/** WBI 签名:参数按键名排序拼成查询串,末尾接 mixin key 取 MD5 作为 w_rid */
export function signWbiQuery(params: Record<string, string | number>, mixinKey: string, nowSeconds: number): string {
  const all: Record<string, string | number> = { ...params, wts: nowSeconds };
  const query = Object.keys(all)
    .sort()
    .map((key) => `${encodeURIComponent(key)}=${encodeURIComponent(String(all[key]))}`)
    .join("&");
  return `${query}&w_rid=${md5(query + mixinKey)}`;
}

export function parseBilibiliSearch(data: any): BilibiliCandidate[] {
  const result: BilibiliCandidate[] = [];
  for (const item of data?.data?.result || []) {
    const seasonId = Number(item?.season_id);
    const title = String(item?.title ?? "")
      .replace(/<[^>]+>/g, "")
      .trim();
    if (!seasonId || !title) continue;
    const pubtime = Number(item.pubtime);
    result.push({
      seasonId,
      title,
      year: pubtime > 0 ? new Date(pubtime * 1000).getUTCFullYear() : undefined,
      episodeCount: Number(item.ep_size) || 0,
      isMovie: item.season_type_name === "电影" || item.media_type === 2,
    });
  }
  return result;
}

export function parseBilibiliEpisodes(data: any): BilibiliEpisode[] {
  const episodes: BilibiliEpisode[] = [];
  for (const item of data?.result?.episodes || []) {
    const cid = Number(item?.cid);
    if (!cid) continue;
    const badge = String(item.badge || "");
    const title = `${item.title ?? ""} ${item.long_title ?? ""}`.trim();
    if (badge.includes("预告") || isExtraEpisodeTitle(title)) continue;
    episodes.push({ cid, number: parseEpisodeNumber(item.title), title });
  }
  return episodes;
}

const XML_ENTITIES: Record<string, string> = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": '"', "&apos;": "'" };

function decodeXmlText(text: string): string {
  return text
    .replace(/&(amp|lt|gt|quot|apos);/g, (entity) => XML_ENTITIES[entity])
    .replace(/&#(x?)([0-9a-fA-F]+);/g, (_, hex: string, code: string) => {
      const value = parseInt(code, hex ? 16 : 10);
      return Number.isFinite(value) && value > 0 && value <= 0x10ffff ? String.fromCodePoint(value) : "";
    });
}

/** 解析 B 站弹幕 XML:<d p="时间,类型,字号,颜色,...">文字</d>(类型 1~3 滚动 / 4 底部 / 5 顶部,其余忽略) */
export function parseBilibiliXml(xml: string): DanmakuComment[] {
  const result: DanmakuComment[] = [];
  const pattern = /<d\s+p="([^"]*)"[^>]*>([\s\S]*?)<\/d>/g;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(xml)) !== null) {
    const parts = match[1].split(",");
    const time = parseFloat(parts[0]);
    const type = parseInt(parts[1], 10);
    const text = decodeXmlText(match[2]).trim();
    if (!text || !Number.isFinite(time) || time < 0) continue;
    let mode: DanmakuMode;
    if (type >= 1 && type <= 3) mode = "scroll";
    else if (type === 4) mode = "bottom";
    else if (type === 5) mode = "top";
    else continue;
    const rawColor = parseInt(parts[3], 10);
    result.push({ time, text, color: Number.isFinite(rawColor) ? rawColor & 0xffffff : 0xffffff, mode });
  }
  return result;
}

/** 弹幕 XML 以 deflate 压缩返回:平台已自动解压时直接是文本,否则在这里解压 */
export function decodeBilibiliXmlBytes(bytes: Uint8Array): string {
  let start = 0;
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) start = 3;
  if (bytes[start] === 0x3c) return strFromU8(bytes);
  for (const inflate of [inflateSync, unzlibSync, gunzipSync]) {
    try {
      return strFromU8(inflate(bytes));
    } catch {
      // 换下一种封装再试
    }
  }
  return "";
}

export function createBilibiliProvider(http: DanmakuHttp, now: () => number = Date.now): DanmakuProvider {
  let session: Promise<BilibiliSession> | null = null;
  const seriesCache = new Map<string, Promise<BilibiliEpisode[] | null>>();
  const baseHeaders = { "User-Agent": DANMAKU_USER_AGENT, Referer: "https://www.bilibili.com/" };

  const openSession = async (): Promise<BilibiliSession> => {
    const spi = await http.getJson("https://api.bilibili.com/x/frontend/finger/spi", baseHeaders);
    const b3 = spi?.data?.b_3;
    if (!b3) throw new Error("bilibili: no buvid");
    const cookie = `buvid3=${b3}; buvid4=${spi.data.b_4 || ""}`;
    // 未登录时 code 为 -101,但 wbi_img 仍然会给
    const nav = await http.getJson("https://api.bilibili.com/x/web-interface/nav", { ...baseHeaders, Cookie: cookie });
    const img = nav?.data?.wbi_img;
    if (!img?.img_url || !img?.sub_url) throw new Error("bilibili: no wbi key");
    return { cookie, mixinKey: buildWbiMixinKey(img.img_url, img.sub_url), createdAt: now() };
  };

  const getSession = async (): Promise<BilibiliSession> => {
    if (session) {
      try {
        const current = await session;
        if (now() - current.createdAt < SESSION_TTL_MS) return current;
      } catch {
        // 上次建立失败,重建
      }
    }
    session = openSession();
    return session;
  };

  const search = async (keyword: string): Promise<BilibiliCandidate[]> => {
    const current = await getSession();
    const pages = await Promise.all(
      ["media_bangumi", "media_ft"].map(async (searchType) => {
        try {
          const query = signWbiQuery(
            { search_type: searchType, keyword, page: 1 },
            current.mixinKey,
            Math.floor(now() / 1000)
          );
          const data = await http.getJson(`https://api.bilibili.com/x/web-interface/wbi/search/type?${query}`, {
            ...baseHeaders,
            Cookie: current.cookie,
          });
          return data?.code === 0 ? parseBilibiliSearch(data) : [];
        } catch {
          return [];
        }
      })
    );
    return pages.flat();
  };

  const findEpisodes = async (query: DanmakuQuery): Promise<BilibiliEpisode[] | null> => {
    const parsed = parseTitle(query.title);
    const keywords = parsed.baseText && parsed.baseText !== parsed.cleaned ? [parsed.cleaned, parsed.baseText] : [parsed.cleaned];
    for (const keyword of keywords) {
      if (!keyword) continue;
      const candidates = await search(keyword);
      const best = pickBestCandidate(parsed, candidates, query);
      if (best < 0) continue;
      const data = await http.getJson(
        `https://api.bilibili.com/pgc/view/web/season?season_id=${candidates[best].seasonId}`,
        baseHeaders
      );
      return data?.code === 0 ? parseBilibiliEpisodes(data) : null;
    }
    return null;
  };

  return {
    name: "bilibili",
    async resolve(query) {
      const key = `${parseTitle(query.title).normalized}|${query.year || ""}|${query.episodeCount <= 1 ? "m" : "s"}`;
      let pending = seriesCache.get(key);
      if (!pending) {
        pending = findEpisodes(query);
        seriesCache.set(key, pending);
        if (seriesCache.size > 20) {
          seriesCache.delete(seriesCache.keys().next().value as string);
        }
        pending.catch(() => seriesCache.delete(key));
      }
      const episodes = await pending;
      if (!episodes || episodes.length === 0) return null;
      const index = pickEpisodeIndex(
        episodes.map((episode) => episode.number),
        query
      );
      if (index < 0) return null;
      const episode = episodes[index];

      return {
        provider: "bilibili",
        title: episode.title,
        segmentSeconds: 0,
        async loadSegment(segmentIndex) {
          if (segmentIndex !== 0) return [];
          const bytes = await http.getBytes(`https://comment.bilibili.com/${episode.cid}.xml`, baseHeaders);
          return parseBilibiliXml(decodeBilibiliXmlBytes(bytes));
        },
      };
    },
  };
}
