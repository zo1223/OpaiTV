import { PlayRecordManager } from "./storage";

export interface DirectPlayParams {
  source: string;
  id: string;
  title: string;
  episodeIndex: number;
  position?: number;
}

/**
 * 搜索结果点一下直接起播:有观看记录就接着上次那一集和进度,没有就从第一集开始。
 * 读记录失败不拦播放,按第一集处理。
 */
export async function resolveDirectPlayParams(source: string, id: string, title: string): Promise<DirectPlayParams> {
  const params: DirectPlayParams = { source, id, title, episodeIndex: 0 };
  try {
    const record = await PlayRecordManager.get(source, id);
    if (record) {
      // 记录里的 index 从 1 开始
      params.episodeIndex = Math.max(0, (Number(record.index) || 1) - 1);
      if (record.play_time > 0) {
        params.position = Math.floor(record.play_time * 1000);
      }
    }
  } catch {
    // 按第一集处理
  }
  return params;
}
