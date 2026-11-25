import { pickRelatedItems, RELATED_LIMIT } from "../relatedItems";

const candidate = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
  id,
  source: "douban",
  title,
  poster: `https://img.example/${id}.jpg`,
  ...extra,
});

describe("pickRelatedItems", () => {
  const current = { title: "星际探险 第二季", source: "src1", id: 7, type_name: "科幻片" };

  it("lists the same series first, then the same category", () => {
    const picked = pickRelatedItems({
      current,
      candidates: [
        candidate("g1", "别的科幻", { tags: ["科幻"] }),
        candidate("s1", "星际探险"),
        candidate("x1", "毫不相干", { type_name: "喜剧片" }),
        candidate("s2", "星际探险 第三季"),
      ],
    });
    expect(picked.map((entry) => `${entry.title}:${entry.relation}`)).toEqual([
      "星际探险:series",
      "星际探险 第三季:series",
      "别的科幻:genre",
    ]);
  });

  it("excludes the current item by title and by source + id", () => {
    const picked = pickRelatedItems({
      current,
      candidates: [
        candidate("a", "星际探险 第二季"),
        candidate("7", "星际探险 2 (同一条)", { source: "src1" }),
        candidate("b", "星际探险 第四季"),
      ],
    });
    expect(picked.map((entry) => entry.title)).toEqual(["星际探险 第四季"]);
  });

  it("matches the category through type_name, class and home tags", () => {
    const picked = pickRelatedItems({
      current: { title: "甲", type_name: "剧情片", class: "悬疑,犯罪" },
      candidates: [
        candidate("1", "按分类", { type_name: "剧情" }),
        candidate("2", "按题材", { class: "悬疑" }),
        candidate("3", "按标签", { tags: ["犯罪"] }),
        candidate("4", "不沾边", { type_name: "综艺" }),
      ],
    });
    expect(picked.map((entry) => entry.title)).toEqual(["按分类", "按题材", "按标签"]);
  });

  it("requires a poster and dedupes by title", () => {
    const picked = pickRelatedItems({
      current,
      candidates: [
        candidate("1", "星际探险 外传", { poster: "" }),
        candidate("2", "星际探险 第五季"),
        candidate("3", "星际探险 第五季", { source: "src2" }),
      ],
    });
    expect(picked.map((entry) => `${entry.source}+${entry.id}`)).toEqual(["douban+2"]);
  });

  it("caps the list and returns nothing without a current item or candidates", () => {
    const many = Array.from({ length: 30 }, (_, index) => candidate(`g${index}`, `科幻${index}号片`, { tags: ["科幻"] }));
    expect(pickRelatedItems({ current, candidates: many })).toHaveLength(RELATED_LIMIT);
    expect(pickRelatedItems({ current, candidates: many, limit: 3 })).toHaveLength(3);
    expect(pickRelatedItems({ current: null, candidates: many })).toEqual([]);
    expect(pickRelatedItems({ current, candidates: null })).toEqual([]);
    expect(pickRelatedItems({ current: { title: "没有分类也不成系列" }, candidates: many })).toEqual([]);
  });
});
