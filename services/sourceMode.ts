/**
 * 片源模式:每个片源一档,存在本机设置里(AppSettings.sourceModes),切换立即生效。
 *  - stable 稳定:该源每条结果只保留一条“最稳”线路
 *  - media  影视:该源只保留影视类条目
 *  - full   全量:不做任何额外过滤(默认)
 *  - off    关闭:该源不参与搜索与首页
 * 这一层与成人内容(18+)过滤相互独立,只会在其结果上进一步收窄。
 */
export type SourceMode = "stable" | "media" | "full" | "off";

export const SOURCE_MODES: SourceMode[] = ["stable", "media", "full", "off"];
export const DEFAULT_SOURCE_MODE: SourceMode = "full";
export const SOURCE_MODE_LABELS: Record<SourceMode, string> = {
  stable: "稳定",
  media: "影视",
  full: "全量",
  off: "关闭",
};
export const SOURCE_MODE_HINTS: Record<SourceMode, string> = {
  stable: "只用该源最稳的一条线路",
  media: "只搜影视类内容",
  full: "什么都搜",
  off: "不参与搜索与首页",
};

export type SourceModeMap = Record<string, SourceMode>;

export const normalizeSourceMode = (value: unknown): SourceMode =>
  typeof value === "string" && (SOURCE_MODES as string[]).includes(value.trim().toLowerCase())
    ? (value.trim().toLowerCase() as SourceMode)
    : DEFAULT_SOURCE_MODE;

/** 读设置时用:丢掉损坏的条目,全量档不占存储。 */
export const normalizeSourceModes = (value: unknown): SourceModeMap => {
  const modes: SourceModeMap = {};
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return modes;
  }
  for (const [key, raw] of Object.entries(value as Record<string, unknown>)) {
    const mode = normalizeSourceMode(raw);
    if (key && mode !== DEFAULT_SOURCE_MODE) {
      modes[key] = mode;
    }
  }
  return modes;
};

export const getSourceMode = (modes: SourceModeMap | null | undefined, sourceKey: string | null | undefined): SourceMode =>
  (sourceKey && modes && Object.prototype.hasOwnProperty.call(modes, sourceKey) && normalizeSourceMode(modes[sourceKey])) ||
  DEFAULT_SOURCE_MODE;

export const withSourceMode = (modes: SourceModeMap, sourceKey: string, mode: SourceMode): SourceModeMap => {
  const next = { ...modes };
  if (mode === DEFAULT_SOURCE_MODE) {
    delete next[sourceKey];
  } else {
    next[sourceKey] = mode;
  }
  return next;
};

export const isSourceOff = (modes: SourceModeMap | null | undefined, sourceKey: string | null | undefined) =>
  getSourceMode(modes, sourceKey) === "off";

// 先看排除词(命中即不是影视),再看包含词;都不命中按“不是影视”处理。空分类无从判断,保留。
const NON_MEDIA_KEYWORDS = [
  "短剧", "解说", "预告", "花絮", "体育", "赛事", "足球", "篮球", "新闻", "资讯", "音乐", "mv",
  "演唱会", "游戏", "电竞", "教程", "课程", "直播", "写真", "福利",
];
const MEDIA_KEYWORDS = ["电影", "片", "剧", "动漫", "动画", "番", "综艺", "纪录", "记录"];

export const isMediaCategory = (typeName?: string | null, className?: string | null): boolean => {
  const text = [typeName, className]
    .filter((value): value is string => typeof value === "string")
    .join(",")
    .trim()
    .toLowerCase();
  if (!text) {
    return true;
  }
  if (NON_MEDIA_KEYWORDS.some((keyword) => text.includes(keyword))) {
    return false;
  }
  return MEDIA_KEYWORDS.some((keyword) => text.includes(keyword));
};

export interface SourceModeLine {
  key: string;
  name: string;
  episodes: string[];
}

const isHlsUrl = (url: unknown) => typeof url === "string" && /\.m3u8(\?|#|$)/i.test(url.trim());

/**
 * 稳定档选线。没有运行时测速数据,这里是启发式:
 * 剧集地址是 m3u8 的线路优先;并列时集数多的优先;再并列取原顺序第一条。
 */
export const pickStableLine = <T extends SourceModeLine>(lines: T[] | null | undefined): T | null => {
  if (!Array.isArray(lines) || lines.length === 0) {
    return null;
  }
  let best: T | null = null;
  let bestRank: [number, number] = [-1, -1];
  for (const line of lines) {
    const episodes = Array.isArray(line?.episodes) ? line.episodes : [];
    const rank: [number, number] = [episodes.length > 0 && episodes.every(isHlsUrl) ? 1 : 0, episodes.length];
    if (rank[0] > bestRank[0] || (rank[0] === bestRank[0] && rank[1] > bestRank[1])) {
      best = line;
      bestRank = rank;
    }
  }
  return best;
};

export interface SourceModeItem {
  source?: string | null;
  type_name?: string | null;
  class?: string | null;
  episodes?: string[];
  playback_source?: string;
  playback_sources?: SourceModeLine[];
}

/** 按各源的模式处理一批条目(搜索结果 / 首页条目)。没有 source 的条目原样保留。 */
export const applySourceModes = <T extends SourceModeItem>(items: T[] | null | undefined, modes: SourceModeMap | null | undefined): T[] => {
  if (!Array.isArray(items)) {
    return [];
  }
  if (!modes || Object.keys(modes).length === 0) {
    return items;
  }
  const output: T[] = [];
  for (const item of items) {
    const mode = getSourceMode(modes, typeof item?.source === "string" ? item.source : null);
    if (mode === "off") {
      continue;
    }
    if (mode === "media" && !isMediaCategory(item.type_name, item.class)) {
      continue;
    }
    if (mode === "stable" && Array.isArray(item.playback_sources) && item.playback_sources.length > 1) {
      const line = pickStableLine(item.playback_sources);
      if (line) {
        output.push({ ...item, playback_sources: [line], playback_source: line.key, episodes: line.episodes });
        continue;
      }
    }
    output.push(item);
  }
  return output;
};
