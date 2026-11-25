/**
 * 自定义弹幕服务器(huangxd-/danmu_api,兼容弹弹 play 接口):
 * POST /api/v2/match 按"片名 S01E05"自动匹配 → GET /api/v2/comment/{episodeId}?format=json 取整集弹幕。
 */
import { DanmakuHttp } from "./http";
import { buildMatchFileName } from "./title";
import { DanmakuComment, DanmakuMode, DanmakuProvider } from "./types";

/** 服务器要现去各平台聚合弹幕,首次请求较慢 */
const SERVER_TIMEOUT_MS = 30000;

/** 解析弹弹 play 格式的弹幕:p = "时间,类型,颜色,..."(类型 1 滚动 / 4 底部 / 5 顶部),m = 文字 */
export function parseDandanComments(data: any): DanmakuComment[] {
  const result: DanmakuComment[] = [];
  for (const item of data?.comments || []) {
    const text = String(item?.m ?? "").trim();
    const parts = String(item?.p ?? "").split(",");
    const time = parseFloat(parts[0]);
    if (!text || !Number.isFinite(time) || time < 0) continue;
    const type = parseInt(parts[1], 10);
    const mode: DanmakuMode = type === 4 ? "bottom" : type === 5 ? "top" : "scroll";
    const rawColor = parseInt(parts[2], 10);
    const color = Number.isFinite(rawColor) ? rawColor & 0xffffff : 0xffffff;
    result.push({ time, text, color, mode });
  }
  return result;
}

export function createServerProvider(http: DanmakuHttp, baseUrl: string): DanmakuProvider {
  return {
    name: "server",
    async resolve(query) {
      const match = await http.postJson(
        `${baseUrl}/api/v2/match`,
        { fileName: buildMatchFileName(query) },
        {},
        SERVER_TIMEOUT_MS
      );
      const first = match?.matches?.[0];
      const episodeId = first?.episodeId;
      if (episodeId === undefined || episodeId === null || episodeId === "") return null;

      // 整集弹幕一次取回:服务器匹配到了却没有弹幕时按未匹配处理,好让内置来源接手
      const data = await http.getJson(
        `${baseUrl}/api/v2/comment/${encodeURIComponent(String(episodeId))}?format=json`,
        {},
        SERVER_TIMEOUT_MS
      );
      const comments = parseDandanComments(data);
      if (comments.length === 0) return null;

      return {
        provider: "server",
        title: `${first.animeTitle || ""} ${first.episodeTitle || ""}`.trim(),
        segmentSeconds: 0,
        async loadSegment(index) {
          return index === 0 ? comments : [];
        },
      };
    },
  };
}
