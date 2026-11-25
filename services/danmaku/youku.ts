/**
 * 内置弹幕来源:优酷公开接口(搜索 → 分集 → 弹幕分片),不需要登录和服务器。
 * 接口与字段参考 huangxd-/danmu_api 的 youku 源。
 *
 * 弹幕接口(mtop)要一个由服务端通过 Set-Cookie 下发的令牌 _m_h5_tk:请求时既要把它放在 Cookie 里,
 * 又要用它的前 32 位参与签名。所以这个来源依赖 http.sendRaw(能读 Set-Cookie、能发表单 POST)。
 */
import { md5 } from "@/utils/md5";
import { DANMAKU_USER_AGENT, DanmakuHttp } from "./http";
import { isExtraEpisodeTitle, parseEpisodeNumber, parseTitle, pickBestCandidate, pickEpisodeIndex, TitleCandidate } from "./title";
import { DanmakuComment, DanmakuMode, DanmakuProvider, DanmakuQuery, DanmakuTrack } from "./types";

const SEARCH_URL = "https://search.youku.com/api/search";
const EPISODES_URL = "https://openapi.youku.com/v2/shows/videos.json";
const TOKEN_URL = "https://acs.youku.com/h5/mtop.com.youku.aplatform.weakget/1.0/?jsv=2.5.1&appKey=24679788";
const DANMAKU_URL = "https://acs.youku.com/h5/mopen.youku.danmu.list/1.0/";
const CLIENT_ID = "53e6cc67237fc59a";
const APP_KEY = "24679788";
const MSG_SALT = "MkmC9SoIw6xCkSKHhJ7b5D2r51kBiREr";
const PAGE_SIZE = 100;
/** 优酷弹幕按 60 秒一片 */
export const YOUKU_SEGMENT_SECONDS = 60;
const SKIP_KEYWORDS = ["中配版", "抢先看", "非正片", "解读", "揭秘", "赏析", "《"];

export interface YoukuCandidate extends TitleCandidate {
  showId: string;
}

export interface YoukuEpisode {
  vid: string;
  number: number | null;
  title: string;
  /** 秒;未知为 0 */
  duration: number;
}

export interface YoukuToken {
  tk: string;
  enc: string;
}

interface YoukuSeries {
  showId: string;
  total: number;
  pages: Map<number, Promise<YoukuEpisode[]>>;
}

export function parseYoukuSearch(data: any): YoukuCandidate[] {
  const result: YoukuCandidate[] = [];
  const seen = new Set<string>();
  for (const component of data?.pageComponentList || []) {
    const common = component?.commonData;
    const showId = common?.showId;
    const rawTitle = common?.titleDTO?.displayName;
    if (!showId || !rawTitle || seen.has(String(showId))) continue;
    // 只留优酷自己有正片的条目
    if (common.isYouku !== 1 && common.hasYouku !== 1) continue;
    const displayName = String(rawTitle);
    if (SKIP_KEYWORDS.some((keyword) => displayName.includes(keyword))) continue;
    const title = displayName
      .replace(/<[^>]+>/g, "")
      .replace(/【.+?】/g, "")
      .trim();
    if (!title) continue;
    const feature = String(common.feature || "");
    const kind = `${String(common.cats || "")} ${feature}`.toLowerCase();
    const isMovie = kind.includes("电影") || kind.includes("movie");
    const year = feature.match(/[12][890][0-9][0-9]/);
    seen.add(String(showId));
    result.push({
      showId: String(showId),
      title,
      year: year ? year[0] : undefined,
      isMovie,
      episodeCount: isMovie ? 1 : Number(common.episodeTotal) || 0,
    });
  }
  return result;
}

export function parseYoukuEpisodes(data: any): { total: number; episodes: YoukuEpisode[] } {
  const episodes: YoukuEpisode[] = [];
  const seen = new Set<string>();
  for (const video of data?.videos || []) {
    const vid = video?.id ? String(video.id) : "";
    if (!vid || seen.has(vid)) continue;
    seen.add(vid);
    const duration = Number(video.duration);
    episodes.push({
      vid,
      number: parseEpisodeNumber(video.seq ?? video.stage),
      title: String(video.title || video.rc_title || ""),
      duration: Number.isFinite(duration) && duration > 0 ? duration : 0,
    });
  }
  // 正片和花絮混在一起时只留正片
  const main = episodes.filter((episode) => !isExtraEpisodeTitle(episode.title));
  return { total: Number(data?.total) || episodes.length, episodes: main.length > 0 ? main : episodes };
}

/** 从 Set-Cookie 文本里取 mtop 令牌;缺任何一个都算没拿到 */
export function parseYoukuToken(setCookie: string | undefined | null): YoukuToken | null {
  const text = String(setCookie || "");
  const tk = text.match(/_m_h5_tk=([^;,\s]+)/);
  const enc = text.match(/_m_h5_tk_enc=([^;,\s]+)/);
  return tk && enc ? { tk: tk[1], enc: enc[1] } : null;
}

const BASE64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** 只处理 ASCII 文本的 base64(请求体里只有数字、字母和标点),不依赖 btoa/Buffer */
export function base64Ascii(input: string): string {
  let output = "";
  for (let i = 0; i < input.length; i += 3) {
    const a = input.charCodeAt(i) & 0xff;
    const hasB = i + 1 < input.length;
    const hasC = i + 2 < input.length;
    const b = hasB ? input.charCodeAt(i + 1) & 0xff : 0;
    const c = hasC ? input.charCodeAt(i + 2) & 0xff : 0;
    output += BASE64[a >> 2] + BASE64[((a & 3) << 4) | (b >> 4)];
    output += hasB ? BASE64[((b & 15) << 2) | (c >> 6)] : "=";
    output += hasC ? BASE64[c & 63] : "=";
  }
  return output;
}

/** 生成一个分片的请求:返回带签名的地址和表单正文 */
export function buildYoukuSegmentRequest(
  vid: string,
  mat: number,
  guid: string,
  tk: string,
  nowMs: number
): { url: string; body: string } {
  const msg: Record<string, unknown> = {
    ctime: nowMs,
    ctype: 10004,
    cver: "v1.0",
    guid,
    mat,
    mcount: 1,
    pid: 0,
    sver: "3.1.0",
    type: 1,
    vid,
  };
  const encoded = base64Ascii(JSON.stringify(msg));
  msg.msg = encoded;
  msg.sign = md5(`${encoded}${MSG_SALT}`);
  const data = JSON.stringify(msg);
  const sign = md5([tk.slice(0, 32), nowMs, APP_KEY, data].join("&"));
  const url =
    `${DANMAKU_URL}?jsv=2.5.6&appKey=${APP_KEY}&t=${nowMs}&sign=${sign}` +
    "&api=mopen.youku.danmu.list&v=1.0&type=originaljson&dataType=jsonp&timeout=20000&jsonpIncPrefix=utility";
  return { url, body: `data=${encodeURIComponent(data)}` };
}

/** 接口应答说令牌缺失/过期(需要换令牌重试) */
export function isYoukuTokenError(data: any): boolean {
  return (Array.isArray(data?.ret) ? data.ret : []).some((item: unknown) => String(item).includes("TOKEN"));
}

export function parseYoukuSegment(data: any): DanmakuComment[] {
  const raw = data?.data?.result;
  if (!raw) return [];
  let inner: any;
  try {
    inner = typeof raw === "string" ? JSON.parse(raw) : raw;
  } catch {
    return [];
  }
  if (!inner || String(inner.code) === "-1") return [];
  const result: DanmakuComment[] = [];
  for (const item of inner.data?.result || []) {
    const text = String(item?.content ?? "").trim();
    const time = Number(item?.playat) / 1000;
    if (!text || !Number.isFinite(time) || time < 0) continue;
    let color = 0xffffff;
    let mode: DanmakuMode = "scroll";
    try {
      const prop = typeof item.propertis === "string" ? JSON.parse(item.propertis) : item.propertis;
      const parsed = typeof prop?.color === "string" ? parseInt(prop.color, 10) : Number(prop?.color);
      if (Number.isFinite(parsed) && parsed > 0) color = parsed & 0xffffff;
      if (prop?.pos === 1) mode = "top";
      else if (prop?.pos === 2) mode = "bottom";
    } catch {
      // 样式解析失败用默认白色滚动
    }
    result.push({ time, text, color, mode });
  }
  return result.sort((a, b) => a.time - b.time);
}

function randomGuid(): string {
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let guid = "";
  for (let i = 0; i < 24; i++) guid += chars[Math.floor(Math.random() * chars.length)];
  return guid;
}

export function createYoukuProvider(http: DanmakuHttp, now: () => number = Date.now): DanmakuProvider {
  const seriesCache = new Map<string, Promise<YoukuSeries | null>>();
  // 接口只把 guid 当客户端标识回传,不校验来源,本地生成一个即可
  const guid = randomGuid();
  let token: Promise<YoukuToken> | null = null;

  const headers = { "User-Agent": DANMAKU_USER_AGENT };

  const search = async (keyword: string): Promise<YoukuCandidate[]> => {
    const data = await http.getJson(
      `${SEARCH_URL}?keyword=${encodeURIComponent(keyword)}&userAgent=${encodeURIComponent(DANMAKU_USER_AGENT)}&site=1&categories=0&ftype=0&ob=0&pg=1`,
      { ...headers, Accept: "application/json", Referer: "https://www.youku.com/" }
    );
    return parseYoukuSearch(data);
  };

  const fetchPage = async (showId: string, page: number) =>
    parseYoukuEpisodes(
      await http.getJson(
        `${EPISODES_URL}?client_id=${CLIENT_ID}&package=com.huawei.hwvplayer.youku&ext=show&show_id=${encodeURIComponent(showId)}&page=${page}&count=${PAGE_SIZE}`,
        headers
      )
    );

  const findSeries = async (query: DanmakuQuery): Promise<YoukuSeries | null> => {
    const parsed = parseTitle(query.title);
    const keywords = parsed.baseText && parsed.baseText !== parsed.cleaned ? [parsed.cleaned, parsed.baseText] : [parsed.cleaned];
    for (const keyword of keywords) {
      if (!keyword) continue;
      const candidates = await search(keyword);
      const best = pickBestCandidate(parsed, candidates, query);
      if (best < 0) continue;
      const showId = candidates[best].showId;
      const first = await fetchPage(showId, 1);
      const pages = new Map<number, Promise<YoukuEpisode[]>>();
      pages.set(1, Promise.resolve(first.episodes));
      return { showId, total: first.total, pages };
    }
    return null;
  };

  const loadPage = (series: YoukuSeries, page: number): Promise<YoukuEpisode[]> => {
    let pending = series.pages.get(page);
    if (!pending) {
      pending = fetchPage(series.showId, page).then((result) => result.episodes);
      series.pages.set(page, pending);
      pending.catch(() => series.pages.delete(page));
    }
    return pending;
  };

  const findEpisode = async (series: YoukuSeries, query: DanmakuQuery): Promise<YoukuEpisode | null> => {
    // 超过一页的长剧:只取当前集所在的那一页
    const page = series.total > PAGE_SIZE ? Math.floor((query.episodeNumber - 1) / PAGE_SIZE) + 1 : 1;
    const episodes = await loadPage(series, page);
    const index = pickEpisodeIndex(
      episodes.map((episode) => episode.number),
      { ...query, episodeIndex: page === 1 ? query.episodeIndex : query.episodeNumber - 1 - (page - 1) * PAGE_SIZE }
    );
    return index >= 0 ? episodes[index] : null;
  };

  const sendRaw = (url: string, init: { method: "GET" | "POST"; headers?: Record<string, string>; body?: string }) => {
    if (!http.sendRaw) throw new Error("youku: raw http unavailable");
    return http.sendRaw(url, init);
  };

  const getToken = (): Promise<YoukuToken> => {
    if (!token) {
      const pending = (async () => {
        const response = await sendRaw(TOKEN_URL, { method: "GET", headers });
        const parsed = parseYoukuToken(response.setCookie);
        if (!parsed) throw new Error("youku: token unavailable");
        return parsed;
      })();
      token = pending;
      pending.catch(() => {
        if (token === pending) token = null;
      });
    }
    return token;
  };

  const requestSegment = async (vid: string, mat: number, current: YoukuToken) => {
    const request = buildYoukuSegmentRequest(vid, mat, guid, current.tk, now());
    const response = await sendRaw(request.url, {
      method: "POST",
      headers: {
        ...headers,
        Cookie: `_m_h5_tk=${current.tk};_m_h5_tk_enc=${current.enc};`,
        Referer: "https://v.youku.com",
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: request.body,
    });
    return { data: JSON.parse(response.text), setCookie: response.setCookie };
  };

  const loadSegment = async (vid: string, mat: number): Promise<DanmakuComment[]> => {
    let current = await getToken();
    let response = await requestSegment(vid, mat, current);
    if (isYoukuTokenError(response.data)) {
      // 令牌过期,或系统 Cookie 里的令牌和我们手里的不一致:应答会带新令牌,换上重试一次
      const renewed = parseYoukuToken(response.setCookie);
      token = renewed ? Promise.resolve(renewed) : null;
      current = await getToken();
      response = await requestSegment(vid, mat, current);
      if (isYoukuTokenError(response.data)) {
        token = null;
        throw new Error("youku: token rejected");
      }
    }
    return parseYoukuSegment(response.data);
  };

  return {
    name: "youku",
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
      const episode = await findEpisode(series, query);
      if (!episode || episode.duration <= 0) return null;
      // 先拿到令牌:拿不到就按"访问失败"报出去,而不是匹配成功后一条弹幕都没有
      await getToken();

      const segmentCount = Math.floor(episode.duration / YOUKU_SEGMENT_SECONDS) + 1;
      const track: DanmakuTrack = {
        provider: "youku",
        title: episode.title,
        segmentSeconds: YOUKU_SEGMENT_SECONDS,
        async loadSegment(index) {
          if (index < 0 || index >= segmentCount) return [];
          return loadSegment(episode.vid, index);
        },
      };
      return track;
    },
  };
}
