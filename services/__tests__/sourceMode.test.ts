import {
  applySourceModes,
  getSourceMode,
  isMediaCategory,
  isSourceOff,
  normalizeSourceMode,
  normalizeSourceModes,
  pickStableLine,
  withSourceMode,
} from "../sourceMode";

describe("source mode", () => {
  it("defaults to full for missing, unknown or damaged values", () => {
    expect(normalizeSourceMode(undefined)).toBe("full");
    expect(normalizeSourceMode("whatever")).toBe("full");
    expect(normalizeSourceMode(3)).toBe("full");
    expect(normalizeSourceMode(" Stable ")).toBe("stable");
    expect(getSourceMode({}, "a")).toBe("full");
    expect(getSourceMode(null, "a")).toBe("full");
    expect(getSourceMode({ a: "off" }, "a")).toBe("off");
    expect(getSourceMode({ a: "off" }, "toString")).toBe("full");
  });

  it("drops damaged entries and does not store the default mode", () => {
    expect(normalizeSourceModes({ a: "off", b: "full", c: "bogus", d: "media" })).toEqual({ a: "off", d: "media" });
    expect(normalizeSourceModes("nope")).toEqual({});
    expect(normalizeSourceModes(["off"])).toEqual({});
    expect(withSourceMode({ a: "off" }, "a", "full")).toEqual({});
    expect(withSourceMode({ a: "off" }, "b", "stable")).toEqual({ a: "off", b: "stable" });
  });

  it("recognises film and series categories and rejects the rest", () => {
    expect(isMediaCategory("电影")).toBe(true);
    expect(isMediaCategory("国产剧")).toBe(true);
    expect(isMediaCategory("动作片")).toBe(true);
    expect(isMediaCategory("日本动漫")).toBe(true);
    expect(isMediaCategory("综艺")).toBe(true);
    expect(isMediaCategory("纪录片")).toBe(true);
    expect(isMediaCategory("短剧")).toBe(false);
    expect(isMediaCategory("电影解说")).toBe(false);
    expect(isMediaCategory("体育赛事")).toBe(false);
    expect(isMediaCategory("MV")).toBe(false);
    expect(isMediaCategory("其它")).toBe(false);
    // 没有分类无从判断,保留
    expect(isMediaCategory("")).toBe(true);
    expect(isMediaCategory(undefined, null)).toBe(true);
  });

  it("picks the m3u8 line first, then the line with more episodes, then the first one", () => {
    const mp4 = { key: "a", name: "A", episodes: ["https://v.example/1.mp4", "https://v.example/2.mp4", "https://v.example/3.mp4"] };
    const hlsShort = { key: "b", name: "B", episodes: ["https://v.example/1.m3u8"] };
    const hlsLong = { key: "c", name: "C", episodes: ["https://v.example/1.m3u8?t=1", "https://v.example/2.m3u8"] };
    const hlsLongTwin = { key: "d", name: "D", episodes: ["https://v.example/x.m3u8", "https://v.example/y.m3u8"] };
    expect(pickStableLine([mp4, hlsShort, hlsLong, hlsLongTwin])?.key).toBe("c");
    expect(pickStableLine([mp4, hlsShort])?.key).toBe("b");
    expect(pickStableLine([mp4])?.key).toBe("a");
    expect(pickStableLine([])).toBeNull();
    expect(pickStableLine(undefined)).toBeNull();
  });

  it("applies each source's mode to a list of items", () => {
    const lines = [
      { key: "mp4", name: "MP4", episodes: ["https://v.example/1.mp4"] },
      { key: "hls", name: "HLS", episodes: ["https://v.example/1.m3u8"] },
    ];
    const items = [
      { source: "off", title: "关掉的", type_name: "电影" },
      { source: "media", title: "电影", type_name: "动作片" },
      { source: "media", title: "短剧", type_name: "短剧" },
      { source: "stable", title: "多线路", type_name: "短剧", playback_sources: lines, episodes: lines[0].episodes },
      { source: "full", title: "全量", type_name: "短剧", playback_sources: lines },
      { title: "没有源" },
    ];
    const modes = { off: "off", media: "media", stable: "stable" } as const;
    const output = applySourceModes(items, modes);
    expect(output.map((item) => item.title)).toEqual(["电影", "多线路", "全量", "没有源"]);
    const stable = output[1] as (typeof items)[3] & { playback_source?: string };
    expect(stable.playback_sources).toEqual([lines[1]]);
    expect(stable.playback_source).toBe("hls");
    expect(stable.episodes).toEqual(lines[1].episodes);
    // 全量:原样
    expect(output[2]).toBe(items[4]);
    // 没有任何模式设置时原样返回
    expect(applySourceModes(items, {})).toBe(items);
    expect(isSourceOff(modes, "off")).toBe(true);
    expect(isSourceOff(modes, "media")).toBe(false);
  });
});
