import { heroBannerMeta, pickHeroBannerItems, stepHeroIndex, HERO_BANNER_LIMIT } from "../heroBanner";

const item = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
  id,
  source: "douban",
  title,
  poster: `https://img.example/${id}.jpg`,
  ...extra,
});

describe("pickHeroBannerItems", () => {
  it("puts recommendations first, then the loaded home items", () => {
    const picked = pickHeroBannerItems({
      recommendations: [item("r1", "推荐一", { reason: "因为你看过《甲》" })],
      homeItems: [item("h1", "首页一"), item("h2", "首页二")],
    });
    expect(picked.map((entry) => entry.title)).toEqual(["推荐一", "首页一", "首页二"]);
    expect(picked[0].reason).toBe("因为你看过《甲》");
  });

  it("ignores recommendations when the switch is off", () => {
    const picked = pickHeroBannerItems({
      recommendations: [item("r1", "推荐一")],
      homeItems: [item("h1", "首页一")],
      recommendationsEnabled: false,
    });
    expect(picked.map((entry) => entry.title)).toEqual(["首页一"]);
  });

  it("requires a poster, a title, a source and an id", () => {
    const picked = pickHeroBannerItems({
      homeItems: [
        item("a", "没海报", { poster: "  " }),
        item("b", "  "),
        { id: "c", title: "没来源", poster: "https://img.example/c.jpg" },
        { source: "douban", title: "没编号", poster: "https://img.example/d.jpg" },
        item("e", "齐全"),
      ],
    });
    expect(picked.map((entry) => entry.title)).toEqual(["齐全"]);
  });

  it("dedupes by title and keeps the first occurrence", () => {
    const picked = pickHeroBannerItems({
      recommendations: [item("r1", "同 名")],
      homeItems: [item("h1", "同名", { source: "src1" }), item("h2", "另一部")],
    });
    expect(picked.map((entry) => `${entry.source}+${entry.id}`)).toEqual(["douban+r1", "douban+h2"]);
  });

  it("caps the list at five by default and honours a custom limit", () => {
    const many = Array.from({ length: 12 }, (_, index) => item(`h${index}`, `片${index}`));
    expect(pickHeroBannerItems({ homeItems: many })).toHaveLength(HERO_BANNER_LIMIT);
    expect(pickHeroBannerItems({ homeItems: many, limit: 2 })).toHaveLength(2);
    expect(pickHeroBannerItems({ homeItems: many, limit: 0 })).toEqual([]);
  });

  it("skips items already known to have no playable source", () => {
    const picked = pickHeroBannerItems({
      homeItems: [
        item("a", "播不了", { playabilityStatus: "unavailable" }),
        item("b", "检测中", { playabilityStatus: "checking" }),
        item("c", "能播", { playabilityStatus: "playable" }),
      ],
    });
    expect(picked.map((entry) => entry.title)).toEqual(["检测中", "能播"]);
  });

  it("returns an empty list for missing input and stringifies numeric ids", () => {
    expect(pickHeroBannerItems({})).toEqual([]);
    expect(pickHeroBannerItems({ recommendations: null, homeItems: null })).toEqual([]);
    expect(pickHeroBannerItems({ homeItems: [item("x", "数字编号", { id: 42 })] })[0].id).toBe("42");
  });
});

describe("heroBannerMeta", () => {
  it("joins year, rating and reason, leaving out what is missing", () => {
    expect(heroBannerMeta({ year: "2024", rate: "8.6", reason: "你收藏了还没看" })).toBe("2024 · ★ 8.6 · 你收藏了还没看");
    expect(heroBannerMeta({ year: "2024" })).toBe("2024");
    expect(heroBannerMeta({})).toBe("");
  });
});

describe("stepHeroIndex", () => {
  it("wraps in both directions", () => {
    expect(stepHeroIndex(0, 3, 1)).toBe(1);
    expect(stepHeroIndex(2, 3, 1)).toBe(0);
    expect(stepHeroIndex(0, 3, -1)).toBe(2);
    expect(stepHeroIndex(0, 0, 1)).toBe(0);
  });
});
