/**
 * 弹幕取数入口。
 *
 * 默认来源是内置直连:按片名和集数依次去腾讯视频、哔哩哔哩的公开接口匹配并取弹幕,
 * 不需要登录,也不需要任何服务器。设置里填了自定义 danmu_api 地址时先问它,
 * 它匹配不到或访问失败再回到内置来源。
 *
 * 任何一步失败都只是"没有弹幕":resolve 不抛错。
 */
import { createBilibiliProvider } from "./bilibili";
import { DanmakuHttp, defaultDanmakuHttp } from "./http";
import { createServerProvider } from "./server";
import { createTencentProvider } from "./tencent";
import { DanmakuProvider, DanmakuQuery, DanmakuTrack, normalizeDanmakuServerUrl } from "./types";

export interface DanmakuService {
  /** 给一集视频找弹幕;匹配不到或全部来源失败时返回 null */
  resolve: (query: DanmakuQuery, serverUrl?: string) => Promise<DanmakuTrack | null>;
}

export function createDanmakuService(http: DanmakuHttp = defaultDanmakuHttp): DanmakuService {
  const builtin: DanmakuProvider[] = [createTencentProvider(http), createBilibiliProvider(http)];
  let server: { url: string; provider: DanmakuProvider } | null = null;

  return {
    async resolve(query, serverUrl) {
      if (!query.title || !query.title.trim()) return null;
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

      for (const provider of providers) {
        try {
          const track = await provider.resolve(normalized);
          if (track) return track;
        } catch {
          // 这个来源不可用,换下一个
        }
      }
      return null;
    },
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
