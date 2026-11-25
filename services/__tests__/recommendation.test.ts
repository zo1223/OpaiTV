import { buildRecommendations, seriesStem, splitGenres } from "../recommendation";

const NOW = Date.UTC(2026, 10, 25);
const DAY = 24 * 60 * 60 * 1000;

const candidate = (title: string, extra: Record<string, unknown> = {}) => ({
  id: title,
  source: "src",
  title,
  poster: "",
  ...extra,
});

describe("local recommendations", () => {
  it("returns nothing without any watch history or favourites", () => {
    expect(buildRecommendations({ history: [], favorites: [], candidates: [candidate("三体", { type_name: "科幻" })] })).toEqual([]);
  });

  it("recommends other entries of a watched series and says which title it came from", () => {
    const result = buildRecommendations({
      history: [{ title: "庆余年 第一季", type_name: "国产剧", save_time: NOW - DAY }],
      favorites: [],
      candidates: [candidate("庆余年 第二季", { type_name: "国产剧" }), candidate("无关的片", { type_name: "体育" })],
      now: NOW,
    });
    expect(result.map((item) => [item.title, item.reason])).toEqual([["庆余年 第二季", "因为你看过《庆余年 第一季》"]]);
  });

  it("recommends by the categories the user watches most and names the category", () => {
    const result = buildRecommendations({
      history: [
        { title: "星际穿越", type_name: "科幻,冒险", save_time: NOW - DAY },
        { title: "火星救援", type_name: "科幻", save_time: NOW - 60 * DAY },
        { title: "老友记", type_name: "喜剧", save_time: NOW - 60 * DAY },
      ],
      favorites: [],
      candidates: [
        candidate("喜剧之王", { type_name: "喜剧" }),
        candidate("降临", { type_name: "科幻片" }),
        candidate("纪录中国", { type_name: "纪录片" }),
        candidate("豆瓣条目", { tags: ["科幻", "电影"] }),
      ],
      now: NOW,
    });
    // 科幻权重 3(近期 2 + 较早 1) > 喜剧 1;纪录片没有命中,不推荐
    expect(result.map((item) => [item.title, item.reason, item.score])).toEqual([
      ["降临", "因为你常看「科幻」", 3],
      ["豆瓣条目", "因为你常看「科幻」", 3],
      ["喜剧之王", "因为你常看「喜剧」", 1],
    ]);
  });

  it("never recommends something already watched", () => {
    const result = buildRecommendations({
      history: [{ title: "三体", type_name: "科幻", save_time: NOW }],
      favorites: [],
      candidates: [candidate(" 三 体 ", { type_name: "科幻" }), candidate("三体2", { type_name: "科幻" })],
      now: NOW,
    });
    expect(result.map((item) => item.title)).toEqual(["三体2"]);
    expect(result[0].reason).toBe("因为你看过《三体》");
  });

  it("surfaces favourites that have not been watched yet, and uses favourites as seeds", () => {
    const result = buildRecommendations({
      history: [{ title: "已看过的收藏", type_name: "悬疑", save_time: NOW }],
      favorites: [
        { title: "已看过的收藏", type_name: "悬疑", source: "s", id: "1" },
        { title: "鱿鱼游戏", type_name: "韩剧", source: "s", id: "2", poster: "p.jpg", year: "2021", source_name: "S" },
      ],
      candidates: [candidate("鱿鱼游戏 第二季", { type_name: "韩剧" }), candidate("另一部韩剧", { type_name: "韩剧" })],
      now: NOW,
    });
    expect(result.map((item) => [item.title, item.reason])).toEqual([
      ["鱿鱼游戏 第二季", "因为你收藏了《鱿鱼游戏》"],
      ["鱿鱼游戏", "你收藏了还没看"],
      ["另一部韩剧", "因为你常看「韩剧」"],
    ]);
    expect(result[1]).toMatchObject({ source: "s", id: "2", poster: "p.jpg", year: "2021" });
  });

  it("dedupes by title, keeps the best score and respects the limit", () => {
    const result = buildRecommendations({
      history: [{ title: "甲", type_name: "科幻", save_time: NOW }],
      favorites: [],
      candidates: [
        candidate("乙", { type_name: "科幻", source: "one" }),
        candidate("乙", { type_name: "科幻", source: "two" }),
        candidate("丙", { type_name: "科幻" }),
        candidate("丁", { type_name: "科幻" }),
      ],
      limit: 2,
      now: NOW,
    });
    expect(result.map((item) => [item.title, item.source])).toEqual([
      ["乙", "one"],
      ["丙", "src"],
    ]);
  });

  it("tolerates damaged input", () => {
    expect(
      buildRecommendations({
        history: [null as any, { title: "" }, { title: "甲", type_name: undefined }],
        favorites: null as any,
        candidates: [null as any, candidate(""), candidate("乙")],
        now: NOW,
      })
    ).toEqual([]);
  });

  it("computes series stems and genre tokens", () => {
    expect(seriesStem("庆余年 第二季")).toBe("庆余年");
    expect(seriesStem("Stranger Things Season 4")).toBe("strangerthings");
    expect(seriesStem("三体2")).toBe("三体");
    expect(seriesStem("唐人街探案3：")).toBe("唐人街探案");
    expect(splitGenres("科幻, 冒险/动作、剧情|爱情")).toEqual(["科幻", "冒险", "动作", "剧情", "爱情"]);
    expect(splitGenres(undefined)).toEqual([]);
  });
});

describe("local recommendations stay local", () => {
  it("does not touch the network", () => {
    const fetchSpy = jest.spyOn(global, "fetch").mockImplementation(() => {
      throw new Error("recommendations must not use the network");
    });
    try {
      buildRecommendations({
        history: [{ title: "甲", type_name: "科幻", save_time: NOW }],
        favorites: [{ title: "乙", type_name: "科幻", source: "s", id: "1" }],
        candidates: [candidate("丙", { type_name: "科幻" })],
        now: NOW,
      });
      expect(fetchSpy).not.toHaveBeenCalled();
    } finally {
      fetchSpy.mockRestore();
    }
  });
});
