import {
  SEARCH_INTENTS,
  isNaturalLanguageQuery,
  mergeSearchResults,
  resolveSearchIntent,
} from "../searchIntent";

describe("resolveSearchIntent", () => {
  it("ships the 18 scene / mood / weather groups", () => {
    expect(SEARCH_INTENTS).toHaveLength(18);
    for (const intent of SEARCH_INTENTS) {
      expect(intent.label).toBeTruthy();
      expect(intent.patterns.length).toBeGreaterThan(0);
      expect(intent.genres.length).toBeGreaterThan(0);
    }
  });

  it("maps 回南天 to the overcast-day genres", () => {
    const intent = resolveSearchIntent("回南天适合看什么");
    expect(intent?.label).toBe("阴天絮语");
    expect(intent?.matched).toEqual(["回南天"]);
    expect(intent?.genres).toEqual(["剧情", "悬疑", "爱情"]);
    expect(intent?.excludeGenres).toEqual(["恐怖"]);
  });

  it("maps 梅雨 to the same overcast-day group", () => {
    expect(resolveSearchIntent("梅雨季窝在家")?.label).toBe("阴天絮语");
  });

  it("maps 高铁候机 to travel genres", () => {
    const intent = resolveSearchIntent("高铁候机打发一下");
    expect(intent?.label).toBe("出差远行");
    expect(intent?.matched).toEqual(expect.arrayContaining(["高铁", "候机"]));
    expect(intent?.genres).toEqual(["喜剧", "冒险", "动画", "奇幻"]);
  });

  it("maps 夜宵 to the late-night group", () => {
    const intent = resolveSearchIntent("夜宵配点什么");
    expect(intent?.label).toBe("小酌夜宵");
    expect(intent?.genres).toEqual(["犯罪", "惊悚", "喜剧", "剧情"]);
  });

  it("maps 聚会 to the party group", () => {
    const intent = resolveSearchIntent("朋友聚会想热闹一下");
    expect(intent?.label).toBe("朋友聚会");
    expect(intent?.genres[0]).toBe("喜剧");
  });

  it("maps 家务背景音 to background-friendly genres", () => {
    const intent = resolveSearchIntent("家务背景音");
    expect(intent?.label).toBe("家务背景音");
    expect(intent?.genres).toEqual(["纪录", "喜剧", "音乐", "综艺"]);
    expect(intent?.excludeGenres).toEqual(["恐怖", "惊悚"]);
  });

  it("ignores case and whitespace in the query", () => {
    expect(resolveSearchIntent(" EMO 了 ")?.label).toBe("焦躁求治愈");
  });

  it("does not treat plain titles as natural language", () => {
    for (const title of ["流浪地球", "The Matrix", "繁花", "old", "new", "", "   "]) {
      expect(resolveSearchIntent(title)).toBeNull();
      expect(isNaturalLanguageQuery(title)).toBe(false);
    }
    expect(isNaturalLanguageQuery("回南天")).toBe(true);
  });

  it("prefers the group with the heavier match when several groups hit", () => {
    // 「热闹」命中朋友聚会(2 字)，「做家务」「家务」「当背景音」「背景音」命中家务背景音
    expect(resolveSearchIntent("做家务当背景音，热闹一点")?.label).toBe("家务背景音");
  });

  it("moves negated genres out of the suggestions", () => {
    const intent = resolveSearchIntent("回南天，不要爱情");
    expect(intent?.label).toBe("阴天絮语");
    expect(intent?.genres).toEqual(["剧情", "悬疑"]);
    expect(intent?.excludeGenres).toEqual(["恐怖", "爱情"]);
  });

  it("returns null for a negative hint without any scene", () => {
    expect(resolveSearchIntent("不要爱情")).toBeNull();
  });
});

describe("mergeSearchResults", () => {
  const item = (title: string, source: string) => ({ title, source });

  it("keeps keyword hits first and drops later duplicates by normalized title", () => {
    const merged = mergeSearchResults(
      [item("Alpha", "s1"), item("Beta", "s1"), item("alpha ", "s2")],
      [item("BETA", "s2"), item("Gamma", "s2"), item(" g a m m a ", "s3")]
    );
    expect(merged).toEqual([item("Alpha", "s1"), item("Beta", "s1"), item("Gamma", "s2")]);
  });

  it("returns the keyword list untouched when the intent list is empty", () => {
    const keyword = [item("Alpha", "s1"), item("Beta", "s1")];
    expect(mergeSearchResults(keyword, [])).toEqual(keyword);
  });

  it("keeps untitled entries instead of collapsing them together", () => {
    expect(mergeSearchResults([item("", "s1")], [item("  ", "s2"), item("Alpha", "s3")])).toHaveLength(3);
  });
});
