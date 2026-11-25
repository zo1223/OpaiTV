/**
 * 自然语言检索意图（纯函数，无网络）。
 *
 * 把「回南天」「高铁候机」「夜宵」这类天气 / 心情 / 场景说法翻成一组中文题材关键词，
 * 由搜索页拿这些题材词走现有的搜索路径再查一轮，与关键词搜索的结果合并去重。
 * 只在本机做字符串匹配，不上传任何内容。
 */

export interface SearchIntentDefinition {
  label: string;
  patterns: string[];
  genres: string[];
  excludeGenres?: string[];
}

export interface ResolvedSearchIntent {
  label: string;
  /** 查询里实际命中的说法 */
  matched: string[];
  /** 建议检索的题材词（已去掉被否定的题材） */
  genres: string[];
  /** 不希望出现的题材词 */
  excludeGenres: string[];
}

export const SEARCH_INTENTS: SearchIntentDefinition[] = [
  {
    label: "晴日清爽",
    patterns: ["晴天", "大晴天", "阳光", "好天气", "阳光明媚", "晒太阳", "春游", "踏青", "野餐", "郊游", "户外", "散步", "遛弯", "春日", "风和日丽"],
    genres: ["喜剧", "动画", "家庭", "爱情", "冒险"],
    excludeGenres: ["恐怖", "惊悚"],
  },
  {
    label: "阴天絮语",
    patterns: ["阴天", "多云", "灰蒙", "起雾", "大雾", "迷雾天", "潮湿", "回南天", "梅雨", "湿冷", "惆怅", "寡淡"],
    genres: ["剧情", "悬疑", "爱情"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "雪夜寒冬",
    patterns: ["下雪", "雪天", "初雪", "冬天", "冬日", "寒冷", "严寒", "雪地", "滑雪", "暴风雪", "圣诞", "飘雪"],
    genres: ["剧情", "家庭", "奇幻", "爱情"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "风雨风暴",
    patterns: ["大风", "台风", "暴风雨", "风暴", "雷阵雨", "打雷", "闪电", "电闪雷鸣", "暴雨"],
    genres: ["冒险", "动作", "悬疑", "惊悚"],
  },
  {
    label: "炎炎夏日",
    patterns: ["夏天", "炎热", "酷暑", "高温", "消暑", "纳凉", "暑假", "泳池", "海边", "海滩", "冲浪", "海岛", "烈日"],
    genres: ["冒险", "奇幻", "爱情", "喜剧"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "焦躁求治愈",
    patterns: ["焦虑", "烦躁", "心烦", "压力大", "内耗", "自闭", "emo了", "难过", "郁闷", "不开心", "想被治愈", "疗愈", "平静", "静心", "心累"],
    genres: ["动画", "喜剧", "家庭", "音乐"],
    excludeGenres: ["恐怖", "惊悚", "犯罪"],
  },
  {
    label: "无聊杀时间",
    patterns: ["无聊", "打发时间", "杀时间", "摸鱼", "划水", "放空", "随便看看", "都行", "剧荒", "不知道看什么"],
    genres: ["喜剧", "动画", "冒险", "悬疑"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "打起精神",
    patterns: ["励志", "打鸡血", "鸡血", "干劲", "冲刺", "逆袭", "奋斗", "拼搏", "振作", "燃一下", "打起精神", "重新开始"],
    genres: ["剧情", "动作", "历史"],
  },
  {
    label: "愤怒发泄",
    patterns: ["生气", "愤怒", "火大", "暴躁", "窝火", "出气", "发泄", "气死我了"],
    genres: ["动作", "惊悚", "犯罪"],
  },
  {
    label: "心碎碎片",
    patterns: ["失恋", "分手", "心碎", "白月光", "意难平", "放不下", "舔狗", "复合"],
    genres: ["剧情", "爱情"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "怀旧青春",
    patterns: ["怀旧", "复古", "童年", "小时候", "青春", "校园", "年代感", "旧时光", "80年代", "90年代", "千禧", "学生时代"],
    genres: ["剧情", "爱情", "喜剧", "动画"],
  },
  {
    label: "好奇探索",
    patterns: ["好奇", "揭秘", "未知", "探索", "冷知识", "涨知识", "真实事件", "大开眼界", "长见识"],
    genres: ["纪录", "悬疑", "冒险"],
  },
  {
    label: "出差远行",
    patterns: ["出差", "酒店", "宾馆", "民宿", "候机", "飞机上", "高铁", "火车", "大巴", "长途", "倒时差", "绿皮车", "候车"],
    genres: ["喜剧", "冒险", "动画", "奇幻"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "通勤路上",
    patterns: ["通勤", "地铁", "公交", "上下班", "坐车", "堵车", "挤地铁", "班车"],
    genres: ["喜剧", "动画", "冒险"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "公路露营",
    patterns: ["自驾", "公路旅行", "公路", "露营", "徒步", "登山", "爬山", "旅行", "旅游", "度假", "国家公园", "野餐旅行"],
    genres: ["冒险", "奇幻", "纪录"],
    excludeGenres: ["恐怖"],
  },
  {
    label: "小酌夜宵",
    patterns: ["喝酒", "小酌", "独酌", "下酒", "酒吧", "夜宵", "烧烤", "大排档", "配酒", "微醺"],
    genres: ["犯罪", "惊悚", "喜剧", "剧情"],
  },
  {
    label: "朋友聚会",
    patterns: ["聚会", "团建", "派对", "轰趴", "闺蜜", "兄弟", "几个朋友", "多人一起", "一起看", "热闹"],
    genres: ["喜剧", "动作", "奇幻", "冒险"],
  },
  {
    label: "家务背景音",
    // 「家务」「背景音」是在原始说法（做家务 / 当背景音）之外补的短说法，方便直接说“家务背景音”。
    patterns: ["打扫", "做家务", "家务", "收拾", "整理", "洗衣服", "做饭", "烹饪", "边做边看", "当背景音", "背景音", "大扫除", "拖地"],
    genres: ["纪录", "喜剧", "音乐", "综艺"],
    excludeGenres: ["恐怖", "惊悚"],
  },
];

/** 否定提示：「不要爱情」之类的说法把对应题材排除掉。 */
export const SEARCH_NEGATIVE_HINTS: { patterns: string[]; genres: string[] }[] = [
  { patterns: ["不要爱情", "不想看爱情", "别爱情", "不谈情说爱", "没有爱情"], genres: ["爱情"] },
  { patterns: ["不要纪录片", "别纪录片", "不想看纪录片"], genres: ["纪录"] },
  { patterns: ["不要动画", "别动画", "不想看动画"], genres: ["动画"] },
  { patterns: ["不要战争", "别战争", "不想看战争"], genres: ["战争", "历史"] },
  { patterns: ["不要悲伤", "不想伤感", "别虐"], genres: ["剧情"] },
];

const normalizeQuery = (value: string): string => String(value ?? "").toLowerCase().replace(/\s+/g, "");

const unique = (values: string[]): string[] => Array.from(new Set(values));

/**
 * 查询里含有任一说法即命中该组；多组同时命中时取命中说法总长度最大的一组
 * （长度相同再比命中条数，仍相同取表里靠前的）。没有任何一组命中返回 null。
 */
export function resolveSearchIntent(query: string): ResolvedSearchIntent | null {
  const text = normalizeQuery(query);
  if (!text) {
    return null;
  }

  let best: { intent: SearchIntentDefinition; matched: string[]; score: number } | null = null;
  for (const intent of SEARCH_INTENTS) {
    const matched = intent.patterns.filter((pattern) => text.includes(pattern.toLowerCase()));
    if (matched.length === 0) {
      continue;
    }
    const score = matched.reduce((sum, pattern) => sum + pattern.length, 0);
    if (!best || score > best.score || (score === best.score && matched.length > best.matched.length)) {
      best = { intent, matched, score };
    }
  }
  if (!best) {
    return null;
  }

  const negated = unique(
    SEARCH_NEGATIVE_HINTS.filter((hint) => hint.patterns.some((pattern) => text.includes(pattern))).flatMap(
      (hint) => hint.genres
    )
  );
  const excludeGenres = unique([...(best.intent.excludeGenres ?? []), ...negated]);
  const genres = unique(best.intent.genres).filter((genre) => !excludeGenres.includes(genre));

  return { label: best.intent.label, matched: best.matched, genres, excludeGenres };
}

export function isNaturalLanguageQuery(query: string): boolean {
  return resolveSearchIntent(query) !== null;
}

const normalizeTitle = (title: unknown): string => String(title ?? "").toLowerCase().replace(/\s+/g, "");

/**
 * 关键词结果与意图结果合并：按「去空白 + 小写」的标题去重，同名只留最先出现的一条，
 * 关键词命中排在前面。没有标题的条目无法判重，原样保留。
 */
export function mergeSearchResults<T extends { title: string }>(keywordResults: T[], intentResults: T[]): T[] {
  const seen = new Set<string>();
  const merged: T[] = [];
  for (const item of [...(keywordResults ?? []), ...(intentResults ?? [])]) {
    const key = normalizeTitle(item?.title);
    if (key) {
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);
    }
    merged.push(item);
  }
  return merged;
}
