/**
 * 内置弹幕来源:爱奇艺公开接口(搜索 → 分集 → 视频信息 → 弹幕分片),不需要登录和服务器。
 * 弹幕分片地址与字段参考 huangxd-/danmu_api 的 iqiyi 源;搜索与分集用的是不需要签名的旧接口
 * (新版搜索接口频繁返回风控码,不适合在客户端直连)。
 */
import { strFromU8, unzlibSync } from "fflate";
import { DANMAKU_USER_AGENT, DanmakuHttp } from "./http";
import { spreadWithinSecond } from "./tencent";
import { isExtraEpisodeTitle, parseTitle, pickBestCandidate, pickEpisodeIndex, TitleCandidate } from "./title";
import { DanmakuComment, DanmakuProvider, DanmakuQuery, DanmakuTrack } from "./types";

const SEARCH_URL = "https://search.video.iqiyi.com/o";
const EPISODES_URL = "https://pcw-api.iqiyi.com/albums/album/avlistinfo";
const VIDEO_INFO_URL = "https://pcw-api.iqiyi.com/video/video/baseinfo";
const DANMAKU_BASE_URL = "https://cmts.iqiyi.com/bullet";
const ALLOWED_CHANNELS = ["电视剧", "电影", "动漫", "综艺", "纪录片", "儿童"];
/** 每个弹幕分片覆盖的秒数 */
const SEGMENT_SECONDS = 300;
const EPISODE_PAGE_SIZE = 200;

export interface IqiyiCandidate extends TitleCandidate {
  albumId: string;
  /** 电影没有分集列表,搜索结果里直接带着正片的视频编号 */
  movieTvId: string;
}

export interface IqiyiEpisode {
  tvId: string;
  number: number | null;
  title: string;
}

interface IqiyiSeries {
  albumId: string;
  /** 分集总数(含非正片);超过一页时按集号分页取 */
  total: number;
  firstPage: IqiyiEpisode[];
  pages: Map<number, Promise<IqiyiEpisode[]>>;
}

/** 编号统一成十进制数字串;不是正整数时返回空串 */
const idText = (value: unknown): string => {
  const text = typeof value === "number" ? (Number.isSafeInteger(value) ? String(value) : "") : String(value ?? "").trim();
  return /^[1-9]\d*$/.test(text) ? text : "";
};

export function parseIqiyiSearch(data: any): IqiyiCandidate[] {
  const root = data?.data;
  if (!root || typeof root !== "object" || !Array.isArray(root.docinfos)) return [];
  const seen = new Set<string>();
  const result: IqiyiCandidate[] = [];
  for (const doc of root.docinfos) {
    const album = doc?.albumDocInfo;
    if (!album) continue;
    // 只要爱奇艺站内的正片专辑(其他站点的结果、短视频、播单都不要)
    if (album.siteId !== "iqiyi" || album.videoDocType !== 1) continue;
    const channel = String(album.channel || "").split(",")[0];
    if (!ALLOWED_CHANNELS.includes(channel)) continue;
    const albumId = idText(album.albumId);
    const title = String(album.albumTitle || "")
      .replace(/<[^>]+>/g, "")
      .trim();
    if (!albumId || !title || seen.has(albumId)) continue;
    const isMovie = channel === "电影";
    const movieTvId = isMovie ? idText(album.videoinfos?.[0]?.tvId) || albumId : "";
    const yearMatch = String(album.releaseDate || "").match(/^(19|20)\d{2}/);
    seen.add(albumId);
    result.push({
      albumId,
      movieTvId,
      title,
      year: yearMatch ? yearMatch[0] : undefined,
      isMovie,
      episodeCount: isMovie ? 1 : Number(album.itemTotalNumber) || 0,
    });
  }
  return result;
}

export function parseIqiyiEpisodePage(data: any): { total: number; episodes: IqiyiEpisode[] } {
  const root = data?.data;
  const episodes: IqiyiEpisode[] = [];
  const seen = new Set<string>();
  for (const item of Array.isArray(root?.epsodelist) ? root.epsodelist : []) {
    const tvId = idText(item?.tvId);
    // contentType 1 是正片,其余是预告、花絮
    if (!tvId || item.contentType !== 1 || seen.has(tvId)) continue;
    seen.add(tvId);
    const order = Number(item.order);
    episodes.push({
      tvId,
      number: Number.isInteger(order) && order > 0 ? order : null,
      title: String(item.name || item.shortTitle || ""),
    });
  }
  const main = episodes.filter((episode) => !isExtraEpisodeTitle(episode.title));
  return { total: Number(root?.total) || 0, episodes: main.length > 0 ? main : episodes };
}

/** 视频信息里的时长(秒);不显示弹幕或没有时长时返回 0 */
export function parseIqiyiDuration(data: any): number {
  const info = data?.data;
  if (!info || typeof info !== "object" || info.displayBarrage === false) return 0;
  const seconds = Number(info.durationSec);
  return Number.isFinite(seconds) && seconds > 0 ? seconds : 0;
}

/** 第 index 个分片(从 0 开始)的地址:目录取视频编号的末四位 */
export function buildIqiyiSegmentUrl(tvId: string, index: number): string {
  const padded = `0000${tvId}`;
  return `${DANMAKU_BASE_URL}/${padded.slice(-4, -2)}/${padded.slice(-2)}/${tvId}_${SEGMENT_SECONDS}_${index + 1}.z`;
}

const XML_ENTITIES: Record<string, string> = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": '"', "&apos;": "'" };

function decodeXmlText(text: string): string {
  return text
    .replace(/&#(x?)([0-9a-fA-F]+);/g, (_, hex: string, code: string) => {
      const value = parseInt(code, hex ? 16 : 10);
      const valid = Number.isFinite(value) && value > 0 && value <= 0x10ffff && !(value >= 0xd800 && value <= 0xdfff);
      return valid ? String.fromCodePoint(value) : "";
    })
    .replace(/&(amp|lt|gt|quot|apos);/g, (entity) => XML_ENTITIES[entity]);
}

/** 解析爱奇艺弹幕 XML:每条 <bulletInfo> 里取 content / showTime(秒) / color(十六进制) */
export function parseIqiyiXml(xml: string): DanmakuComment[] {
  const result: DanmakuComment[] = [];
  const pattern = /<bulletInfo>([\s\S]*?)<\/bulletInfo>/g;
  const field = (block: string, tag: string) => {
    const match = block.match(new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`));
    return match ? match[1] : "";
  };
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(xml)) !== null) {
    const text = decodeXmlText(field(match[1], "content")).trim();
    const time = parseFloat(field(match[1], "showTime"));
    if (!text || !Number.isFinite(time) || time < 0) continue;
    const rawColor = field(match[1], "color").trim();
    const color = /^[0-9a-fA-F]{1,6}$/.test(rawColor) ? parseInt(rawColor, 16) : 0xffffff;
    result.push({ time, text, color, mode: "scroll" });
  }
  // 时间只精确到秒,同一秒的弹幕摊开显示
  return spreadWithinSecond(result);
}

/** 弹幕分片是 zlib 压缩的 XML:已经是文本时直接用,否则解压;解不开返回空串 */
export function decodeIqiyiSegmentBytes(bytes: Uint8Array): string {
  if (bytes.length === 0) return "";
  if (bytes[0] === 0x3c) return strFromU8(bytes);
  try {
    return strFromU8(unzlibSync(bytes));
  } catch {
    return "";
  }
}

export function createIqiyiProvider(http: DanmakuHttp): DanmakuProvider {
  const seriesCache = new Map<string, Promise<IqiyiSeries | null>>();

  const headers = {
    "User-Agent": DANMAKU_USER_AGENT,
    Referer: "https://www.iqiyi.com/",
    Accept: "application/json, text/plain, */*",
  };

  const search = async (keyword: string): Promise<IqiyiCandidate[]> => {
    const data = await http.getJson(
      `${SEARCH_URL}?if=html5&key=${encodeURIComponent(keyword)}&pageNum=1&pageSize=20&video_allow_3rd=0`,
      headers
    );
    return parseIqiyiSearch(data);
  };

  const fetchEpisodePage = async (albumId: string, page: number) =>
    parseIqiyiEpisodePage(
      await http.getJson(`${EPISODES_URL}?aid=${albumId}&page=${page}&size=${EPISODE_PAGE_SIZE}`, headers)
    );

  const findSeries = async (query: DanmakuQuery): Promise<IqiyiSeries | null> => {
    const parsed = parseTitle(query.title);
    const keywords = parsed.baseText && parsed.baseText !== parsed.cleaned ? [parsed.cleaned, parsed.baseText] : [parsed.cleaned];
    for (const keyword of keywords) {
      if (!keyword) continue;
      const candidates = await search(keyword);
      const best = pickBestCandidate(parsed, candidates, query);
      if (best < 0) continue;
      const candidate = candidates[best];
      if (candidate.isMovie) {
        const firstPage = [{ tvId: candidate.movieTvId, number: null, title: candidate.title }];
        return { albumId: candidate.albumId, total: 1, firstPage, pages: new Map() };
      }
      const first = await fetchEpisodePage(candidate.albumId, 1);
      return { albumId: candidate.albumId, total: first.total, firstPage: first.episodes, pages: new Map() };
    }
    return null;
  };

  const loadPage = (series: IqiyiSeries, page: number): Promise<IqiyiEpisode[]> => {
    let pending = series.pages.get(page);
    if (!pending) {
      pending = fetchEpisodePage(series.albumId, page).then((result) => result.episodes);
      series.pages.set(page, pending);
      pending.catch(() => series.pages.delete(page));
    }
    return pending;
  };

  const findEpisode = async (series: IqiyiSeries, query: DanmakuQuery): Promise<IqiyiEpisode | null> => {
    const pick = (episodes: IqiyiEpisode[], episodeIndex: number) => {
      const index = pickEpisodeIndex(
        episodes.map((episode) => episode.number),
        { ...query, episodeIndex }
      );
      return index >= 0 ? episodes[index] : null;
    };

    if (series.total <= EPISODE_PAGE_SIZE) {
      return pick(series.firstPage, query.episodeIndex);
    }
    // 分页的长剧:只取当前集所在的那一页
    const page = Math.floor((query.episodeNumber - 1) / EPISODE_PAGE_SIZE) + 1;
    const episodes = page === 1 ? series.firstPage : await loadPage(series, page);
    return pick(episodes, query.episodeNumber - 1 - (page - 1) * EPISODE_PAGE_SIZE);
  };

  return {
    name: "iqiyi",
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
      if (!episode) return null;

      const duration = parseIqiyiDuration(await http.getJson(`${VIDEO_INFO_URL}/${episode.tvId}`, headers));
      if (duration <= 0) return null;
      const segmentCount = Math.ceil(duration / SEGMENT_SECONDS);

      const track: DanmakuTrack = {
        provider: "iqiyi",
        title: episode.title,
        segmentSeconds: SEGMENT_SECONDS,
        async loadSegment(index) {
          if (index < 0 || index >= segmentCount) return [];
          const bytes = await http.getBytes(buildIqiyiSegmentUrl(episode.tvId, index), {
            "User-Agent": DANMAKU_USER_AGENT,
          });
          return parseIqiyiXml(decodeIqiyiSegmentBytes(bytes));
        },
      };
      return track;
    },
  };
}
