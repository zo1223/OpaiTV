/**
 * 弹幕取数入口。
 *
 * 默认来源是内置直连:按片名和集数依次去腾讯视频、哔哩哔哩、爱奇艺、优酷、芒果TV、咪咕视频的公开接口匹配并取弹幕,
 * 不需要登录,也不需要任何服务器。设置里填了自定义 danmu_api 地址时先问它,
 * 它匹配不到或访问失败再回到内置来源。
 *
 * 任何一步失败都只是"没有弹幕":resolve 不抛错。
 */
import { createBilibiliProvider } from "./bilibili";
import { DanmakuHttp, defaultDanmakuHttp } from "./http";
import { createIqiyiProvider } from "./iqiyi";
import { createMangoProvider } from "./mango";
import { createMiguProvider } from "./migu";
import { createServerProvider } from "./server";
import { createTencentProvider } from "./tencent";
import { DanmakuProvider, DanmakuQuery, DanmakuTrack, normalizeDanmakuServerUrl } from "./types";
import { createYoukuProvider } from "./youku";

/** 找弹幕的结果类别:found 匹配到;notFound 来源都正常应答但没有这部片/这一集;failed 没匹配到且至少一个来源访问出错 */
export type DanmakuResolveOutcome = "found" | "notFound" | "failed";

export interface DanmakuResolveResult {
  track: DanmakuTrack | null;
  outcome: DanmakuResolveOutcome;
}

export interface DanmakuService {
  /** 给一集视频找弹幕;匹配不到或全部来源失败时返回 null */
  resolve: (query: DanmakuQuery, serverUrl?: string) => Promise<DanmakuTrack | null>;
  /** 同 resolve,另外说明没找到的原因(供播放页提示用户);不抛错 */
  resolveDetailed: (query: DanmakuQuery, serverUrl?: string) => Promise<DanmakuResolveResult>;
}

/** 播放页上弹幕状态的轻提示文字(与桌面版 DanmakuNotice 一致) */
export const DANMAKU_NOTICE = {
  on: "弹幕已开启",
  off: "弹幕已关闭",
  notFound: "未找到该集弹幕",
  failed: "弹幕获取失败，请检查网络后重试",
} as const;

export function createDanmakuService(http: DanmakuHttp = defaultDanmakuHttp): DanmakuService {
  // 按优先级依次尝试,匹配到就用。原有的两个来源排在前面,原来能匹配到的片行为不变。
  const builtin: DanmakuProvider[] = [
    createTencentProvider(http),
    createBilibiliProvider(http),
    createIqiyiProvider(http),
    createYoukuProvider(http),
    createMangoProvider(http),
    createMiguProvider(http),
  ];
  let server: { url: string; provider: DanmakuProvider } | null = null;

  const resolveDetailed = async (query: DanmakuQuery, serverUrl?: string): Promise<DanmakuResolveResult> => {
    if (!query.title || !query.title.trim()) return { track: null, outcome: "notFound" };
    const normalized: DanmakuQuery = {
      ...query,
      episodeIndex: Math.max(0, Math.floor(query.episodeIndex) || 0),
      episodeNumber: Math.max(1, Math.floor(query.episodeNumber) || 1),
      episodeCount: Math.max(1, Math.floor(query.episodeCount) || 1),
    };

    const providers: DanmakuProvider[] = [];
    const url = normalizeDanmakuServerUrl(serverUrl);
    if (url) {
      if (!server || server.url !== url) {
        server = { url, provider: createServerProvider(http, url) };
      }
      providers.push(server.provider);
    }
    providers.push(...builtin);

    let failed = false;
    for (const provider of providers) {
      try {
        const track = await provider.resolve(normalized);
        if (track) return { track, outcome: "found" };
      } catch {
        // 这个来源不可用,换下一个
        failed = true;
      }
    }
    return { track: null, outcome: failed ? "failed" : "notFound" };
  };

  return {
    resolveDetailed,
    resolve: async (query, serverUrl) => (await resolveDetailed(query, serverUrl)).track,
  };
}

export const danmakuService = createDanmakuService();

/**
 * 由播放器里的集标题推出集号:"第 12 集" → 12;认不出时用下标 + 1。
 */
export function episodeNumberFromLabel(label: string | undefined | null, episodeIndex: number): number {
  const match = (label || "").match(/第\s*0*(\d{1,4})\s*[集话話回期]/) || (label || "").trim().match(/^0*(\d{1,4})$/);
  const parsed = match ? parseInt(match[1], 10) : NaN;
  return Number.isFinite(parsed) && parsed > 0 ? parsed : episodeIndex + 1;
}
