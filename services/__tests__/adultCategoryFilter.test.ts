import {
  ADULT_CATEGORY_BENIGN_PHRASES,
  ADULT_CATEGORY_KEYWORDS,
  getItemCategoryNames,
  isAdultCategory,
  isAdultCategoryHidden,
} from "../adultCategoryFilter";

describe("isAdultCategory", () => {
  it.each([
    // CineFlow 内置源分类表里实际出现的成人分类
    "伦理片",
    "伦理",
    "港台三级",
    "韩国伦理",
    "西方伦理",
    "日本伦理",
    "里番动漫",
    "福利",
    "写真热舞",
    "擦边短剧",
    "擦边剧",
    "两性课堂",
    // 常见变体
    "理论片",
    "三级片",
    "福利视频",
    "写真",
    "热舞",
    "擦边",
    "情色片",
    "成人动漫",
    "18禁",
    // 繁体
    "倫理片",
    "三級",
    "港台三級",
    "裏番",
    "寫真熱舞",
    "擦邊短劇",
    "兩性課堂",
    // 全角 / 空白 / 标点
    "伦 理 片",
    "伦理·片",
    " 伦理片 ",
    "１８＋",
    "ＡＶ",
    "Ｒ１８",
    // 英文大小写
    "AV",
    "av",
    "国产AV",
    "AV解说",
    "Adult",
    "ADULT",
    "adult video",
    "18+",
    "R18",
    "r-18",
    "Hentai",
    "XXX",
  ])("treats %j as an adult category", (name) => {
    expect(isAdultCategory(name)).toBe(true);
  });

  it.each([
    "剧情片",
    "爱情片",
    "动作片",
    "喜剧片",
    "科幻片",
    "恐怖片",
    "战争片",
    "动漫",
    "国产动漫",
    "日韩动漫",
    "纪录片",
    "记录片",
    "综艺",
    "国产剧",
    "港台剧",
    "短剧",
    "电影",
    "电视剧",
    "体育",
    // 含关键词但属于正常分类的固定说法
    "家庭伦理",
    "家庭伦理剧",
    "伦理学",
    "福利院",
    "成人教育",
    // 英文:关键词只是别的单词的一部分
    "Avatar",
    "Marvel",
    "Essex",
    "Java",
    "Adulthood",
    "2018",
    "1080P",
    "",
    "   ",
  ])("leaves %j alone", (name) => {
    expect(isAdultCategory(name)).toBe(false);
  });

  it("ignores missing values", () => {
    expect(isAdultCategory()).toBe(false);
    expect(isAdultCategory(undefined, null, "")).toBe(false);
    expect(isAdultCategory(123 as unknown as string)).toBe(false);
  });

  it("checks every field it is given, and every tag inside a field", () => {
    expect(isAdultCategory("剧情片", "剧情,爱情")).toBe(false);
    expect(isAdultCategory("剧情片", "剧情,伦理")).toBe(true);
    expect(isAdultCategory("剧情片", "剧情/情色")).toBe(true);
    expect(isAdultCategory(undefined, "伦理片")).toBe(true);
    expect(isAdultCategory("福利,剧情片")).toBe(true);
    // 标签逐个判断,不会把相邻的两个标签拼成一个正常说法
    expect(isAdultCategory("成人,教育")).toBe(true);
  });

  it("keeps the word list normalized, so entries can actually match", () => {
    for (const word of [...ADULT_CATEGORY_KEYWORDS, ...ADULT_CATEGORY_BENIGN_PHRASES]) {
      expect(word).toBe(word.normalize("NFKC").toLowerCase().trim());
      expect(word.length).toBeGreaterThan(0);
    }
    expect(new Set(ADULT_CATEGORY_KEYWORDS).size).toBe(ADULT_CATEGORY_KEYWORDS.length);
  });
});

describe("isAdultCategoryHidden", () => {
  it("hides adult categories only while the toggle is off", () => {
    expect(isAdultCategoryHidden(false, "伦理片")).toBe(true);
    expect(isAdultCategoryHidden(true, "伦理片")).toBe(false);
    expect(isAdultCategoryHidden(false, "剧情片", "剧情,爱情")).toBe(false);
    expect(isAdultCategoryHidden(true, "剧情片")).toBe(false);
  });
});

describe("getItemCategoryNames", () => {
  it("joins type_name and class into the value stored with favorites and play records", () => {
    expect(getItemCategoryNames({ type_name: "伦理片", class: "剧情,伦理" })).toBe("伦理片,剧情,伦理");
    expect(getItemCategoryNames({ type_name: "剧情片" })).toBe("剧情片");
    expect(getItemCategoryNames({ class: "爱情" })).toBe("爱情");
    expect(getItemCategoryNames({ type_name: " ", class: "" })).toBeUndefined();
    expect(getItemCategoryNames({})).toBeUndefined();
    expect(getItemCategoryNames(null)).toBeUndefined();
    expect(isAdultCategory(getItemCategoryNames({ type_name: "剧情片", class: "剧情,情色" }))).toBe(true);
  });
});
