// 成人内容(18+)按分类过滤 —— 手机端唯一实现。
// 桌面端对应实现:desktop/OrionTV.Desktop.Core/AdultContent.cs 的 AdultCategories,两边的词表与规则必须保持一致。
//
// 只看“分类”:源站分类名(type_name)与题材标签(vod_class),绝不看片名。
// 片名里带“伦理”“福利”的正常影片(《伦理学讲座》《福利院的孩子》)不会被拦。
// 所有取数入口通过 services/api.ts 的 isAdultCategoryHidden / isAdultItemHidden 调用,调用点不自己拼关键词。

/** 繁体 -> 简体(只收词表用得到的字) */
const TRADITIONAL_TO_SIMPLIFIED: Record<string, string> = {
  倫: "伦",
  論: "论",
  級: "级",
  裏: "里",
  裡: "里",
  寫: "写",
  熱: "热",
  邊: "边",
  兩: "两",
  課: "课",
  無: "无",
  碼: "码",
  優: "优",
  亂: "乱",
  愛: "爱",
  絲: "丝",
  襪: "袜",
  調: "调",
  癡: "痴",
  漢: "汉",
  學: "学",
  禮: "礼",
  會: "会",
};

/**
 * 成人分类关键词(已归一化:NFKC、小写、简体)。
 * 含中文的词按子串匹配(匹配前去掉空白与标点);纯英文/数字的词要求两侧不是英文字母或数字,
 * 所以 av 不会命中 avatar,sex 不会命中 essex。
 */
export const ADULT_CATEGORY_KEYWORDS: readonly string[] = [
  // CineFlow 11 个内置源分类表里实际出现的:伦理片/伦理/港台三级/韩国伦理/西方伦理/日本伦理/
  // 里番动漫/福利/写真热舞/擦边短剧/擦边剧/两性课堂
  "伦理",
  "三级",
  "里番",
  "福利",
  "写真",
  "热舞",
  "擦边",
  "两性",
  // 常见变体与同类叫法
  "理论片",
  "肉番",
  "情色",
  "色情",
  "成人",
  "限制级",
  "大尺度",
  "18禁",
  "十八禁",
  "无码",
  "有码",
  "女优",
  "乱伦",
  "人妻",
  "熟女",
  "巨乳",
  "丝袜",
  "偷拍",
  "性爱",
  "做爱",
  "自慰",
  "调教",
  "痴汉",
  "淫",
  // 英文 / 数字
  "adult",
  "adults",
  "porn",
  "porno",
  "hentai",
  "erotic",
  "erotica",
  "sex",
  "xxx",
  "nsfw",
  "jav",
  "av",
  "18+",
  "r18",
  "r-18",
  "uncensored",
  "lunli",
  "lunlipian",
];

/** 含成人关键词但属于正常分类的固定说法:匹配前先从分类名里剔除。 */
export const ADULT_CATEGORY_BENIGN_PHRASES: readonly string[] = [
  "家庭伦理",
  "社会伦理",
  "伦理学",
  "伦理道德",
  "医学伦理",
  "科技伦理",
  "生命伦理",
  "福利院",
  "社会福利",
  "福利彩票",
  "成人教育",
  "成人礼",
  "成人高考",
  "三级跳",
];

// 一个字段里可能是多个标签("剧情,伦理" / "剧情/爱情"),逐个标签判断
const TAG_SEPARATORS = /[,，、/|;；\\]+/;
// 去掉空白与标点后再做中文子串匹配("伦 理 片" "伦理·片");保留 +(18+)
const NON_WORD = /[^\p{L}\p{N}+]+/gu;
const HAS_NON_ASCII = /[^\x00-\x7f]/;

const isAsciiAlphaNumeric = (char: string | undefined) => char !== undefined && /^[a-z0-9]$/.test(char);

const normalize = (value: string) =>
  Array.from(value.normalize("NFKC").toLowerCase())
    .map((char) => TRADITIONAL_TO_SIMPLIFIED[char] ?? char)
    .join("");

// 关键词首/尾是英文字母或数字时,要求命中位置的前/后一个字符不是英文字母或数字
const containsKeyword = (text: string, keyword: string) => {
  const guardStart = isAsciiAlphaNumeric(keyword[0]);
  const guardEnd = isAsciiAlphaNumeric(keyword[keyword.length - 1]);
  let from = 0;
  while (from <= text.length - keyword.length) {
    const index = text.indexOf(keyword, from);
    if (index < 0) {
      return false;
    }
    const end = index + keyword.length;
    if ((!guardStart || !isAsciiAlphaNumeric(text[index - 1])) && (!guardEnd || !isAsciiAlphaNumeric(text[end]))) {
      return true;
    }
    from = index + 1;
  }
  return false;
};

const isAdultTag = (tag: string) => {
  if (!tag.trim()) {
    return false;
  }
  let compact = tag.replace(NON_WORD, "");
  for (const phrase of ADULT_CATEGORY_BENIGN_PHRASES) {
    compact = compact.split(phrase).join(" ");
  }
  return ADULT_CATEGORY_KEYWORDS.some((keyword) => containsKeyword(HAS_NON_ASCII.test(keyword) ? compact : tag, keyword));
};

/**
 * 给定的分类名 / 题材标签里是否有成人分类。可一次传多个字段(type_name、class)。
 * 空值不算成人分类。
 */
export const isAdultCategory = (...names: (string | null | undefined)[]): boolean =>
  names.some((name) => typeof name === "string" && normalize(name).split(TAG_SEPARATORS).some(isAdultTag));

/** 成人内容开关关闭且属于成人分类 -> 需要隐藏。 */
export const isAdultCategoryHidden = (showAdultContent: boolean, ...names: (string | null | undefined)[]): boolean =>
  !showAdultContent && isAdultCategory(...names);

/**
 * 收藏 / 播放记录里保存的分类:条目的 type_name 与 class 用逗号拼接,都没有时为 undefined。
 * 保存下来的值之后原样交给 isAdultCategory 判断。
 */
export const getItemCategoryNames = (
  item: { type_name?: string | null; class?: string | null } | null | undefined
): string | undefined =>
  [item?.type_name, item?.class]
    .filter((name): name is string => typeof name === "string" && name.trim().length > 0)
    .join(",") || undefined;
