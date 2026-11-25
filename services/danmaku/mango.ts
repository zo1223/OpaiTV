/**
 * 内置弹幕来源:芒果TV 公开接口(搜索 → 分集(按月分页) → 弹幕分片),不需要登录和服务器。
 * 接口与字段参考 huangxd-/danmu_api 的 mango 源。
 */
import { DANMAKU_USER_AGENT, DanmakuHttp } from "./http";
import { isExtraEpisodeTitle, parseEpisodeNumber, parseTitle, pickBestCandidate, pickEpisodeIndex, TitleCandidate } from "./title";
import { DanmakuComment, DanmakuMode, DanmakuProvider, DanmakuQuery, DanmakuTrack } from "./types";

const SEARCH_URL = "https://mobileso.bz.mgtv.com/aphone/search/rebirth/v2";
const EPISODES_URL = "https://pcweb.api.mgtv.com/variety/showlist";
const CONTROL_URL = "https://galaxy.bz.mgtv.com/getctlbarrage";
const FALLBACK_SEGMENT_URL = "https://galaxy.bz.mgtv.com/rdbarrage";
const SEARCH_USER_AGENT = "Dalvik/2.1.0 (Linux; U; Android 16; 23127PN0CC Build/AP1A.240505.003)";
const SEGMENT_SECONDS = 60;
/** 分集按月分页;综艺可能跨很多个月,最多取这么多页 */
const MAX_MONTH_PAGES = 36;
/** 弹幕分片的 CDN 域名由接口下发,只接受芒果自己的域名 */
const CDN_HOST = /^[a-z0-9-]+(\.[a-z0-9-]+)*\.(hitv|mgtv)\.com$/i;
const MANGO_EXTRAS =
  /抢先(看|版)|加更|企划|专访|直播|未播|衍生|番外|合伙人手记|会员(专享|加长)|精华|解读|reaction|超前|陪看|影评|广告|推广/i;

export interface MangoCandidate extends TitleCandidate {
  clipId: string;
}

export interface MangoEpisode {
  vid: string;
  number: number | null;
  title: string;
  /** 时长(秒);未知为 0 */
  seconds: number;
  /** 上线时间(排序用) */
  ts: string;
  intact: boolean;
}

export interface MangoEpisodePage {
  /** 其余月份(不含本页) */
  months: string[];
  episodes: MangoEpisode[];
}

interface MangoSeries {
  clipId: string;
  isMovie: boolean;
  episodes: MangoEpisode[];
}

const stripTags = (text: unknown) => String(text ?? "").replace(/<[^>]+>/g, "").trim();

/** "46:36"、"1:02:03" → 秒;认不出为 0 */
export function parseMangoDuration(text: unknown): number {
  const parts = String(text ?? "")
    .trim()
    .split(":")
    .map((part) => Number(part));
  if (parts.length === 0 || parts.length > 3 || parts.some((part) => !Number.isFinite(part) || part < 0)) return 0;
  return parts.reduce((total, part) => total * 60 + part, 0);
}

export function parseMangoSearch(data: any): MangoCandidate[] {
  const result: MangoCandidate[] = [];
  const seen = new Set<string>();
  for (const content of data?.data?.contents || []) {
    if (content?.type !== "mediaRebirthV2" || !Array.isArray(content.data)) continue;
    for (const item of content.data) {
      // 只要芒果自有内容(source 为空或 imgo)
      if (item?.source && item.source !== "imgo") continue;
      if (!item?.clipId) continue;
      const clipId = String(item.clipId);
      const title = stripTags(item.title);
      if (!title || seen.has(clipId)) continue;
      seen.add(clipId);
      const desc = String((Array.isArray(item.desc) ? item.desc[0] : item.desc) || "");
      const year = desc.match(/[12][890][0-9][0-9]/);
      const isMovie = desc.split("/")[0].includes("电影");
      result.push({
        clipId,
        title,
        year: year ? year[0] : undefined,
        isMovie,
        episodeCount: isMovie ? 1 : 0,
      });
    }
  }
  return result;
}

export function parseMangoEpisodePage(data: any, clipId: string): MangoEpisodePage {
  const root = data?.data;
  const months: string[] = [];
  (Array.isArray(root?.tab_m) ? root.tab_m : []).forEach((tab: any, index: number) => {
    if (index > 0 && tab?.m) months.push(String(tab.m));
  });
  const episodes: MangoEpisode[] = [];
  for (const item of Array.isArray(root?.list) ? root.list : []) {
    if (!item?.video_id || String(item.src_clip_id ?? "") !== clipId) continue;
    if (String(item.isnew ?? "") === "2") continue; // 预告
    const label = `${item.t2 || ""} ${item.t1 || ""}`.trim();
    if (isExtraEpisodeTitle(label) || MANGO_EXTRAS.test(label)) continue;
    episodes.push({
      vid: String(item.video_id),
      number: parseEpisodeNumber(item.t2),
      title: String(item.t3 || label || ""),
      seconds: parseMangoDuration(item.time),
      ts: String(item.ts || ""),
      intact: String(item.isIntact ?? "") === "1",
    });
  }
  return { months, episodes };
}

/** 各月的分集合并、去重、排序:都带集号按集号,否则按上线时间 */
export function orderMangoEpisodes(episodes: MangoEpisode[]): MangoEpisode[] {
  const seen = new Set<string>();
  const unique = episodes.filter((episode) => (seen.has(episode.vid) ? false : (seen.add(episode.vid), true)));
  const numbered = unique.every((episode) => episode.number !== null);
  return unique
    .map((episode, index) => ({ episode, index }))
    .sort((a, b) => {
      if (numbered) return (a.episode.number as number) - (b.episode.number as number) || a.index - b.index;
      if (a.episode.ts !== b.episode.ts) return a.episode.ts < b.episode.ts ? -1 : 1;
      return a.index - b.index;
    })
    .map((entry) => entry.episode);
}

function parseRgb(color: any): number {
  const ok = (v: unknown) => typeof v === "number" && v >= 0 && v <= 255;
  if (!color || !ok(color.r) || !ok(color.g) || !ok(color.b)) return -1;
  return color.r * 65536 + color.g * 256 + color.b;
}

export function parseMangoSegment(data: any): DanmakuComment[] {
  const result: DanmakuComment[] = [];
  for (const item of Array.isArray(data?.data?.items) ? data.data.items : []) {
    const text = String(item?.content ?? "").trim();
    const time = Number(item?.time) / 1000;
    if (!text || item?.time === undefined || item?.time === null || !Number.isFinite(time) || time < 0) continue;
    let color = 0xffffff;
    if (item.v2_color) {
      const left = parseRgb(item.v2_color.color_left);
      const right = parseRgb(item.v2_color.color_right);
      if (left >= 0 && right >= 0) color = Math.floor((left + right) / 2);
      else if (left >= 0) color = left;
      else if (right >= 0) color = right;
    }
    let mode: DanmakuMode = "scroll";
    if (item.v2_position === 1) mode = "top";
    else if (item.v2_position === 2) mode = "bottom";
    result.push({ time, text, color, mode });
  }
  return result.sort((a, b) => a.time - b.time);
}

/** 弹幕控制接口 → CDN 分片地址前缀;没有或域名不对时返回 null(改用备用接口) */
export function parseMangoControl(data: any): string | null {
  const host = String(data?.data?.cdn_list || "")
    .split(",")[0]
    .trim();
  const version = String(data?.data?.cdn_version || "").trim();
  if (!host || !version || !CDN_HOST.test(host) || !/^[A-Za-z0-9_\-/.]+$/.test(version) || version.includes("..")) {
    return null;
  }
  return `https://${host}/${version.replace(/^\/+|\/+$/g, "")}`;
}

function randomHex(length: number): string {
  let text = "";
  for (let i = 0; i < length; i++) text += Math.floor(Math.random() * 16).toString(16);
  return text;
}

export function createMangoProvider(http: DanmakuHttp): DanmakuProvider {
  const seriesCache = new Map<string, Promise<MangoSeries | null>>();
  const deviceId = randomHex(32);
  const webHeaders = { "User-Agent": DANMAKU_USER_AGENT, Referer: "https://www.mgtv.com/" };

  const search = async (keyword: string): Promise<MangoCandidate[]> => {
    const url =
      `${SEARCH_URL}?q=${encodeURIComponent(keyword)}&_support=10100001&device=23127PN0CC&osVersion=16&appVersion=9.3.3` +
      `&did=${deviceId}&mac=${deviceId}&seqId=${randomHex(32)}&ticket=&userId=0&osType=android&type=10&abroad=0`;
    return parseMangoSearch(await http.getJson(url, { "User-Agent": SEARCH_USER_AGENT, Accept: "application/json" }));
  };

  const fetchEpisodePage = async (clipId: string, month: string) =>
    parseMangoEpisodePage(
      await http.getJson(
        `${EPISODES_URL}?allowedRC=1&collection_id=${encodeURIComponent(clipId)}&month=${encodeURIComponent(month)}&page=1&_support=10000000`,
        webHeaders
      ),
      clipId
    );

  const findSeries = async (query: DanmakuQuery): Promise<MangoSeries | null> => {
    const parsed = parseTitle(query.title);
    const keywords = parsed.baseText && parsed.baseText !== parsed.cleaned ? [parsed.cleaned, parsed.baseText] : [parsed.cleaned];
    for (const keyword of keywords) {
      if (!keyword) continue;
      const candidates = await search(keyword);
      const best = pickBestCandidate(parsed, candidates, query);
      if (best < 0) continue;
      const candidate = candidates[best];
      const first = await fetchEpisodePage(candidate.clipId, "");
      const all = [...first.episodes];
      for (const month of first.months.slice(0, MAX_MONTH_PAGES - 1)) {
        all.push(...(await fetchEpisodePage(candidate.clipId, month)).episodes);
      }
      let episodes = orderMangoEpisodes(all);
      if (candidate.isMovie && episodes.length > 1) {
        // 电影:只留正片
        episodes = [episodes.find((episode) => episode.intact) || episodes[0]];
      }
      return { clipId: candidate.clipId, isMovie: !!candidate.isMovie, episodes };
    }
    return null;
  };

  return {
    name: "mango",
    async resolve(query) {
      const key = `${parseTitle(query.title).normalized}|${query.year || ""}|${query.episodeCount <= 1 ? "m" : "s"}`;
      let pending = seriesCache.get(key);
      if (!pending) {
        pending = findSeries(query);
        seriesCache.set(key, pending);
        if (seriesCache.size > 20) {
          seriesCache.delete(seriesCache.keys().next().value as string);
        }
        // 失败(网络错误)不缓存,下次进入播放页重试
        pending.catch(() => seriesCache.delete(key));
      }
      const series = await pending;
      if (!series) return null;
      const index = pickEpisodeIndex(
        series.episodes.map((episode) => episode.number),
        query
      );
      if (index < 0) return null;
      const episode = series.episodes[index];

      const control = await http.getJson(
        `${CONTROL_URL}?version=8.1.39&abroad=0&uuid=&os=10.15.7&platform=0&mac=&vid=${encodeURIComponent(episode.vid)}&pid=&cid=${encodeURIComponent(series.clipId)}&ticket=`,
        webHeaders
      );
      const cdnBase = parseMangoControl(control);
      const segmentCount = episode.seconds > 0 ? Math.ceil(episode.seconds / SEGMENT_SECONDS) : Infinity;

      const track: DanmakuTrack = {
        provider: "mango",
        title: episode.title,
        segmentSeconds: SEGMENT_SECONDS,
        async loadSegment(segment) {
          if (segment < 0 || segment >= segmentCount) return [];
          const url = cdnBase
            ? `${cdnBase}/${segment}.json`
            : `${FALLBACK_SEGMENT_URL}?vid=${encodeURIComponent(episode.vid)}&cid=${encodeURIComponent(series.clipId)}&time=${segment * SEGMENT_SECONDS * 1000}`;
          return parseMangoSegment(await http.getJson(url, { "User-Agent": DANMAKU_USER_AGENT }));
        },
      };
      return track;
    },
  };
}
