/**
 * 内置弹幕来源:咪咕视频公开接口(搜索 → 分集 → 弹幕分片),不需要登录和服务器。
 * 接口与字段参考 huangxd-/danmu_api 的 migu 源。
 *
 * 弹幕分片的应答是 AES-256-ECB 加密后的 base64 文本,密钥是固定的;这里自带一个只解密的小实现,不引入依赖。
 * 桌面版 desktop/OrionTV.Desktop.Core/DanmakuProvider.Migu.cs 按同一套规则实现,改规则时两边一起改。
 */
import { DANMAKU_USER_AGENT, DanmakuHttp } from "./http";
import { spreadWithinSecond } from "./tencent";
import { isExtraEpisodeTitle, parseEpisodeNumber, parseTitle, pickBestCandidate, pickEpisodeIndex, TitleCandidate } from "./title";
import { DanmakuComment, DanmakuProvider, DanmakuQuery, DanmakuTrack } from "./types";

const SEARCH_URL = "https://jadeite.migu.cn/search/v3/open-search";
const DETAIL_BASE_URL = "https://v3-sc.miguvideo.com/program/v4/cont/content-info";
const DANMAKU_BASE_URL = "https://webapi.miguvideo.com/gateway/live_barrage/videox/barrage/v2/list";
const SEGMENT_SECONDS = 30;
const SEARCH_PACK_IDS =
  "1002581,1002601,1003861,1003862,1003863,1003864,1003865,1003866,1004041,1004121,1004261,1004262,1004281,1004321,1004262,1004281,1004322,1004261,1004421,1004422,1002781,1004301,1004641,1004761,1005061,1005261,1005301,1005321,1005361,1005362,1005341,1005342,1005521,1005722,1005721,1015749,1015761,1015760,1015762,1015763,1015768,1015786,1015790,1015812,1015813,1015814,1015815,1015816,1015817,1015820,1015819,1015821";
const SEARCH_SID = "1X4A395AL3XS3UV5MT8PQ1L5JUYK6KFM8VKDSDD48S5Y7YX5WY1MOUHRT6512988";

export interface MiguCandidate extends TitleCandidate {
  /** 用来取分集列表的节目号(剧集是第一集的号,电影是它自己) */
  contentId: string;
}

export interface MiguEpisode {
  pid: string;
  number: number | null;
  title: string;
  /** 时长(秒);未知为 0 */
  duration: number;
}

export interface MiguEpisodePage {
  /** 整部剧的号(弹幕地址里要用);没有时用分集号 */
  seriesId: string;
  totalPages: number;
  pageSize: number;
  episodes: MiguEpisode[];
}

interface MiguSeries {
  contentId: string;
  first: MiguEpisodePage;
  pages: Map<number, Promise<MiguEpisodePage>>;
}

// ---------- 解析 ----------

export function parseMiguSearch(data: any): MiguCandidate[] {
  const list = data?.body?.contentInfoList;
  if (!Array.isArray(list)) return [];
  const seen = new Set<string>();
  const result: MiguCandidate[] = [];
  for (const item of list) {
    const asset = item?.shortMediaAsset;
    // isLong 为 0 的是短视频、资讯
    if (!asset || !asset.isLong) continue;
    const episodeIds = asset.extraData?.episodes;
    const hasEpisodes = Array.isArray(episodeIds) && episodeIds.length > 0;
    const contentId = String((asset.extraData ? episodeIds?.[0] : asset.pID) ?? "").trim();
    const title = String(asset.name ?? "").trim();
    if (!contentId || !title || seen.has(contentId)) continue;
    seen.add(contentId);
    const isMovie = String(asset.contDisplayName ?? "") === "电影";
    result.push({
      contentId,
      title,
      year: String(asset.year ?? "").trim() || undefined,
      isMovie,
      episodeCount: isMovie ? 1 : hasEpisodes ? episodeIds.length : 0,
    });
  }
  return result;
}

/** "42:44"、"01:00:04" → 秒;认不出返回 0 */
export function parseMiguDuration(value: unknown): number {
  if (typeof value === "number") return Number.isFinite(value) && value > 0 ? value : 0;
  const text = String(value ?? "").trim();
  if (!text) return 0;
  const parts = text.split(":").map(Number);
  if (parts.length > 3 || parts.some((part) => !Number.isFinite(part) || part < 0)) return 0;
  return parts.reduce((total, part) => total * 60 + part, 0);
}

function miguEpisodeNumber(index: unknown, name: string): number | null {
  const byIndex = parseEpisodeNumber(index as string | number | undefined | null);
  if (byIndex !== null && byIndex > 0) return byIndex;
  const match = name.match(/第\s*0*(\d{1,4})\s*[集话話回期]/);
  return match ? parseInt(match[1], 10) : null;
}

export function parseMiguEpisodePage(data: any): MiguEpisodePage {
  const root = data?.body?.data;
  const episodes: MiguEpisode[] = [];
  if (!root) return { seriesId: "", totalPages: 1, pageSize: 0, episodes };
  const seen = new Set<string>();
  if (Array.isArray(root.datas) && root.datas.length > 0) {
    for (const item of root.datas) {
      const pid = String(item?.pID ?? "").trim();
      if (!pid || seen.has(pid)) continue;
      seen.add(pid);
      const title = String(item.name ?? "");
      episodes.push({ pid, number: miguEpisodeNumber(item.index, title), title, duration: parseMiguDuration(item.duration) });
    }
  } else if (root.playing?.pID) {
    // 没有分集列表的单片(电影)
    episodes.push({
      pid: String(root.playing.pID),
      number: null,
      title: String(root.name || root.playing.name || ""),
      duration: parseMiguDuration(root.playing.duration),
    });
  }
  // 正片和花絮混在一起时只留正片
  const main = episodes.filter((episode) => !isExtraEpisodeTitle(episode.title));
  return {
    seriesId: String(root.epsID ?? "").trim(),
    totalPages: Math.max(1, Math.floor(Number(root.totalPage)) || 1),
    pageSize: Math.max(0, Math.floor(Number(root.pageSize)) || 0),
    episodes: main.length > 0 ? main : episodes,
  };
}

export function parseMiguSegment(data: any): DanmakuComment[] {
  const result: DanmakuComment[] = [];
  const list = data?.body?.result;
  if (!Array.isArray(list)) return result;
  for (const item of list) {
    const text = String(item?.msg ?? "").trim();
    const time = Number(item?.playtime);
    if (!text || item?.playtime === null || item?.playtime === undefined || item?.playtime === "") continue;
    if (!Number.isFinite(time) || time < 0) continue;
    const raw = String(item.textcolor ?? "").replace(/^#/, "");
    const color = /^[0-9a-f]{6}$/i.test(raw) ? parseInt(raw, 16) : 0xffffff;
    result.push({ time, text, color, mode: "scroll" });
  }
  // 咪咕的弹幕时间只精确到秒,摊开到这一秒里,避免每秒整排同时冒出来
  return spreadWithinSecond(result.sort((a, b) => a.time - b.time));
}

// ---------- 解密 ----------

const GATEWAY_KEY = "vwwLu7e6ug4HAQMAug8CsA8HD7oHDwuxAg4HAQG6DLA=";
const KEY_NIBBLES = [3, 5, 7, 0, 15, 10, 13, 1, 11, 14, 4, 6, 9, 12, 8, 2];
const BASE64_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

export function miguBase64ToBytes(text: string): Uint8Array {
  const clean = text.replace(/\s+/g, "");
  if (clean.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(clean)) throw new Error("migu: bad base64");
  const padding = clean.endsWith("==") ? 2 : clean.endsWith("=") ? 1 : 0;
  const out = new Uint8Array((clean.length / 4) * 3 - padding);
  let at = 0;
  for (let i = 0; i < clean.length; i += 4) {
    const n =
      (BASE64_CHARS.indexOf(clean[i]) << 18) |
      (BASE64_CHARS.indexOf(clean[i + 1]) << 12) |
      ((BASE64_CHARS.indexOf(clean[i + 2]) & 63) << 6) |
      (BASE64_CHARS.indexOf(clean[i + 3]) & 63);
    if (at < out.length) out[at++] = (n >> 16) & 255;
    if (at < out.length) out[at++] = (n >> 8) & 255;
    if (at < out.length) out[at++] = n & 255;
  }
  return out;
}

export function miguUtf8Decode(bytes: Uint8Array): string {
  const parts: string[] = [];
  let chunk: number[] = [];
  for (let i = 0; i < bytes.length; ) {
    const b = bytes[i++];
    let code = b;
    if (b >= 0xf0) code = ((b & 7) << 18) | ((bytes[i++] & 63) << 12) | ((bytes[i++] & 63) << 6) | (bytes[i++] & 63);
    else if (b >= 0xe0) code = ((b & 15) << 12) | ((bytes[i++] & 63) << 6) | (bytes[i++] & 63);
    else if (b >= 0xc0) code = ((b & 31) << 6) | (bytes[i++] & 63);
    if (code > 0xffff) {
      code -= 0x10000;
      chunk.push(0xd800 + (code >> 10), 0xdc00 + (code & 0x3ff));
    } else {
      chunk.push(code);
    }
    if (chunk.length >= 4096) {
      parts.push(String.fromCharCode.apply(null, chunk));
      chunk = [];
    }
  }
  if (chunk.length > 0) parts.push(String.fromCharCode.apply(null, chunk));
  return parts.join("");
}

let aesTables: { sbox: Uint8Array; inverse: Uint8Array } | null = null;

function getAesTables() {
  if (aesTables) return aesTables;
  const sbox = new Uint8Array(256);
  const inverse = new Uint8Array(256);
  let p = 1;
  let q = 1;
  do {
    p = (p ^ (p << 1) ^ (p & 0x80 ? 0x1b : 0)) & 255;
    q ^= q << 1;
    q ^= q << 2;
    q ^= q << 4;
    q &= 255;
    if (q & 0x80) q ^= 0x09;
    const x = (q ^ ((q << 1) | (q >> 7)) ^ ((q << 2) | (q >> 6)) ^ ((q << 3) | (q >> 5)) ^ ((q << 4) | (q >> 4)) ^ 0x63) & 255;
    sbox[p] = x;
    inverse[x] = p;
  } while (p !== 1);
  sbox[0] = 0x63;
  inverse[0x63] = 0;
  aesTables = { sbox, inverse };
  return aesTables;
}

function gmul(a: number, b: number): number {
  let result = 0;
  for (let i = 0; i < 8; i++) {
    if (b & 1) result ^= a;
    a = ((a << 1) ^ (a & 0x80 ? 0x1b : 0)) & 255;
    b >>= 1;
  }
  return result;
}

/** AES-ECB 解密(只支持 256 位密钥),去掉 PKCS#7 填充。密文不合法时抛错。 */
export function miguAesEcbDecrypt(cipher: Uint8Array, key: Uint8Array): Uint8Array {
  if (key.length !== 32) throw new Error("migu: bad key");
  if (cipher.length === 0 || cipher.length % 16 !== 0) throw new Error("migu: bad cipher length");
  const { sbox, inverse } = getAesTables();
  const rounds = 14;
  const expanded = new Uint8Array(16 * (rounds + 1));
  expanded.set(key);
  let rcon = 1;
  for (let i = 32; i < expanded.length; i += 4) {
    let t0 = expanded[i - 4];
    let t1 = expanded[i - 3];
    let t2 = expanded[i - 2];
    let t3 = expanded[i - 1];
    if (i % 32 === 0) {
      const first = t0;
      t0 = sbox[t1] ^ rcon;
      t1 = sbox[t2];
      t2 = sbox[t3];
      t3 = sbox[first];
      rcon = gmul(rcon, 2);
    } else if (i % 32 === 16) {
      t0 = sbox[t0];
      t1 = sbox[t1];
      t2 = sbox[t2];
      t3 = sbox[t3];
    }
    expanded[i] = expanded[i - 32] ^ t0;
    expanded[i + 1] = expanded[i - 31] ^ t1;
    expanded[i + 2] = expanded[i - 30] ^ t2;
    expanded[i + 3] = expanded[i - 29] ^ t3;
  }
  const mul9 = new Uint8Array(256);
  const mul11 = new Uint8Array(256);
  const mul13 = new Uint8Array(256);
  const mul14 = new Uint8Array(256);
  for (let i = 0; i < 256; i++) {
    mul9[i] = gmul(i, 9);
    mul11[i] = gmul(i, 11);
    mul13[i] = gmul(i, 13);
    mul14[i] = gmul(i, 14);
  }

  const out = new Uint8Array(cipher.length);
  const s = new Uint8Array(16);
  const t = new Uint8Array(16);
  for (let offset = 0; offset < cipher.length; offset += 16) {
    for (let i = 0; i < 16; i++) s[i] = cipher[offset + i] ^ expanded[rounds * 16 + i];
    for (let round = rounds - 1; round >= 0; round--) {
      // 逆行移位 + 逆字节代换
      for (let column = 0; column < 4; column++) {
        for (let row = 0; row < 4; row++) {
          t[((column + row) % 4) * 4 + row] = inverse[s[column * 4 + row]];
        }
      }
      for (let i = 0; i < 16; i++) t[i] ^= expanded[round * 16 + i];
      if (round === 0) {
        s.set(t);
        break;
      }
      // 逆列混合
      for (let column = 0; column < 16; column += 4) {
        const a = t[column];
        const b = t[column + 1];
        const c = t[column + 2];
        const d = t[column + 3];
        s[column] = mul14[a] ^ mul11[b] ^ mul13[c] ^ mul9[d];
        s[column + 1] = mul9[a] ^ mul14[b] ^ mul11[c] ^ mul13[d];
        s[column + 2] = mul13[a] ^ mul9[b] ^ mul14[c] ^ mul11[d];
        s[column + 3] = mul11[a] ^ mul13[b] ^ mul9[c] ^ mul14[d];
      }
    }
    out.set(s, offset);
  }

  const padding = out[out.length - 1];
  if (padding < 1 || padding > 16) throw new Error("migu: bad padding");
  for (let i = out.length - padding; i < out.length; i++) {
    if (out[i] !== padding) throw new Error("migu: bad padding");
  }
  return out.subarray(0, out.length - padding);
}

let gatewayKey: Uint8Array | null = null;

function getGatewayKey(): Uint8Array {
  if (!gatewayKey) {
    gatewayKey = miguBase64ToBytes(GATEWAY_KEY).map((value) => (KEY_NIBBLES[value >>> 4] << 4) | KEY_NIBBLES[value & 15]);
  }
  return gatewayKey;
}

/** 弹幕分片应答 → JSON。应答通常是加密的 base64 文本;已经是明文 JSON 时直接解析。解不开时抛错。 */
export function decodeMiguSegmentBody(body: string): any {
  const text = body.trim();
  if (!text) return null;
  if (text.startsWith("{") || text.startsWith("[")) return JSON.parse(text);
  return JSON.parse(miguUtf8Decode(miguAesEcbDecrypt(miguBase64ToBytes(text), getGatewayKey())));
}

// ---------- 来源 ----------

export function createMiguProvider(http: DanmakuHttp): DanmakuProvider {
  const seriesCache = new Map<string, Promise<MiguSeries | null>>();

  const search = async (keyword: string): Promise<MiguCandidate[]> => {
    const data = await http.postJson(
      SEARCH_URL,
      {
        appVersion: "6.1.1.00",
        ct: 101,
        isCorrectWord: 1,
        k: keyword,
        mediaSource: 9000000,
        packId: SEARCH_PACK_IDS,
        pageIdx: 1,
        pageSize: 20,
        sid: SEARCH_SID,
        copyrightTerminal: 3,
        searchScene: 2,
        uiVersion: "A3.26.0",
      },
      {
        Accept: "application/json, text/plain, */*",
        Origin: "https://www.miguvideo.com",
        Referer: "https://www.miguvideo.com/",
        "User-Agent": DANMAKU_USER_AGENT,
        appId: "miguvideo",
        terminalId: "www",
      }
    );
    return parseMiguSearch(data);
  };

  const fetchEpisodePage = async (contentId: string, page: number): Promise<MiguEpisodePage> =>
    parseMiguEpisodePage(
      await http.getJson(`${DETAIL_BASE_URL}/${encodeURIComponent(contentId)}/${page}`, { "User-Agent": DANMAKU_USER_AGENT })
    );

  const findSeries = async (query: DanmakuQuery): Promise<MiguSeries | null> => {
    const parsed = parseTitle(query.title);
    const keywords = parsed.baseText && parsed.baseText !== parsed.cleaned ? [parsed.cleaned, parsed.baseText] : [parsed.cleaned];
    for (const keyword of keywords) {
      if (!keyword) continue;
      const candidates = await search(keyword);
      const best = pickBestCandidate(parsed, candidates, query);
      if (best < 0) continue;
      const contentId = candidates[best].contentId;
      return { contentId, first: await fetchEpisodePage(contentId, 1), pages: new Map() };
    }
    return null;
  };

  const loadPage = (series: MiguSeries, page: number): Promise<MiguEpisodePage> => {
    let pending = series.pages.get(page);
    if (!pending) {
      pending = fetchEpisodePage(series.contentId, page);
      series.pages.set(page, pending);
      pending.catch(() => series.pages.delete(page));
    }
    return pending;
  };

  const findEpisode = async (series: MiguSeries, query: DanmakuQuery): Promise<MiguEpisode | null> => {
    const pick = (episodes: MiguEpisode[], episodeIndex: number) => {
      const index = pickEpisodeIndex(
        episodes.map((episode) => episode.number),
        { ...query, episodeIndex }
      );
      return index >= 0 ? episodes[index] : null;
    };
    const { first } = series;
    if (first.totalPages <= 1 || first.pageSize <= 0) return pick(first.episodes, query.episodeIndex);
    // 分页的长剧:只取当前集所在的那一页
    const page = Math.floor((query.episodeNumber - 1) / first.pageSize) + 1;
    if (page > first.totalPages) return null;
    const offset = (page - 1) * first.pageSize;
    return pick(page === 1 ? first.episodes : (await loadPage(series, page)).episodes, query.episodeIndex - offset);
  };

  return {
    name: "migu",
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
      // 不知道时长就没法分片
      if (!episode || episode.duration <= 0) return null;

      const seriesId = series.first.seriesId || episode.pid;
      const duration = episode.duration;
      const track: DanmakuTrack = {
        provider: "migu",
        title: episode.title,
        segmentSeconds: SEGMENT_SECONDS,
        async loadSegment(index) {
          const start = Math.floor(index) * SEGMENT_SECONDS;
          if (!(start >= 0) || start >= duration) return [];
          const end = Math.min(start + SEGMENT_SECONDS, duration);
          const bytes = await http.getBytes(`${DANMAKU_BASE_URL}/${seriesId}/${episode.pid}/${start}/${end}/020`, {
            "User-Agent": DANMAKU_USER_AGENT,
            appCode: "miguvideo_default_h5",
          });
          return parseMiguSegment(decodeMiguSegmentBody(miguUtf8Decode(bytes)));
        },
      };
      return track;
    },
  };
}
