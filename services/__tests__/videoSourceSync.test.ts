import { deflateSync, strToU8 } from "fflate";
import { isManualVideoSource, VideoSourceSubscriptionSource } from "../videoSourceSubscription";
import {
  buildVideoSourceSyncQrPayloads,
  createVideoSourceSyncAssembler,
  decodeVideoSourceSyncLink,
  encodeVideoSourceSyncLink,
  isVideoSourceSyncLink,
  mergeSyncedVideoSources,
  serializeVideoSourcesForSync,
  VIDEO_SOURCE_SYNC_QR_CHUNK,
  VIDEO_SOURCE_SYNC_QR_SINGLE_MAX,
} from "../videoSourceSync";

// 与桌面端 VideoSourceSyncTests.cs 用的是同一组源
const sources: VideoSourceSubscriptionSource[] = [
  {
    key: "dytt",
    name: "电影天堂",
    api: "https://caiji.dytt.example/api.php/provide/vod",
    detail: "https://caiji.dytt.example",
  },
  { key: "1", name: "数字 key", api: "http://192.168.1.2:8080/vod" },
  { key: "adult_a", name: "成人源 A", api: "https://adult.example/api.php/provide/vod", adult: true },
  { key: "custom_a.example_8443", name: "a.example:8443", api: "https://a.example:8443/api.php/provide/vod" },
];


const manySources = (count: number): VideoSourceSubscriptionSource[] =>
  Array.from({ length: count }, (_, index) => ({
    key: `source_${index}`,
    name: `采集资源站 ${index}`,
    // 每个地址都不一样,压缩不掉
    api: `https://cj${(index * 2654435761) % 1000003}.site-${index}.example/api.php/provide/vod`,
    ...(index % 7 === 0 ? { adult: true } : {}),
  }));

// 桌面端 VideoSourceSyncLink.Encode 对 manySources(12) 生成的链接(.NET DeflateStream 压缩,与 fflate 的输出不同)
const DESKTOP_LINK =
  "opaitv-sync://z1/pdRLTsMwEAbgqyCvS-JnZpyrIFRFjaUGWmLlUYGq7hDdskUcgTUbDgT0FiRyVmCnsliPnU_WP_n3pLDVsq06Q_KrPbk1DyQnbd03K7OkZEHuiu0wIafj8fT69P3--Pnx_PX2cjGOhovDZN11ts3TdHVDk_EzlzQx98XWbkw6HEjs2qa2qXdVadJdXY7Xyn7TkbxrenNY_BJZWGQeUXIArR3LZtk_Eg9L3COhUkqjk3icJMKS8EgchdDSSSJOkmFJeiRgjGnhJBknqbCkPBITqHHKScVJWVjKPJLKMsAppyxOgrAEHklrqRCcBP9YeQyz6F15LnAKDeMeqMOS9q48ZcidpOMkNtMczFcd438MU2yMRmJzpeFrDaAKYEqOnamN68MP";

describe("video source sync link", () => {
  it("round-trips every source without losing the name, api, detail, key, order or adult mark", () => {
    const link = encodeVideoSourceSyncLink(sources);

    expect(link.startsWith("opaitv-sync://z1/")).toBe(true);
    // base64url:可以直接放进链接和二维码,不含 + / =
    expect(link.slice("opaitv-sync://z1/".length)).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(isVideoSourceSyncLink(link)).toBe(true);
    expect(decodeVideoSourceSyncLink(link)).toEqual(sources);
  });

  it("serializes in the existing subscription JSON format and keeps the adult mark as is", () => {
    const payload = JSON.parse(serializeVideoSourcesForSync([...sources, { ...sources[1], key: "local", imported: true }]));

    expect(payload.api_site).toHaveLength(5);
    expect(payload.api_site[2]).toEqual({
      key: "adult_a",
      name: "成人源 A",
      api: "https://adult.example/api.php/provide/vod",
      adult: true,
    });
    // 未标记的源不写 adult;本机的 imported 标记不导出
    expect(payload.api_site[1]).toEqual({ key: "1", name: "数字 key", api: "http://192.168.1.2:8080/vod" });
    expect(payload.api_site[4]).toEqual({ key: "local", name: "数字 key", api: "http://192.168.1.2:8080/vod" });
  });

  it("imports a link exported by the desktop app", () => {
    expect(DESKTOP_LINK).not.toBe(encodeVideoSourceSyncLink(manySources(12)));
    expect(decodeVideoSourceSyncLink(DESKTOP_LINK)).toEqual(manySources(12));
  });

  it("accepts an uncompressed v1 link, standard base64 and pasted whitespace", () => {
    const json = JSON.stringify({
      api_site: { a: { name: "源 A", api: "https://a.example/vod/", is_adult: 1 }, b: { name: "B", api: "https://b.example/vod" } },
    });
    const base64 = Buffer.from(json, "utf8").toString("base64");
    const expected = [
      { key: "a", name: "源 A", api: "https://a.example/vod", detail: undefined, adult: true },
      { key: "b", name: "B", api: "https://b.example/vod", detail: undefined },
    ];

    expect(decodeVideoSourceSyncLink(`opaitv-sync://v1/${base64}`)).toEqual(expected);
    expect(decodeVideoSourceSyncLink(`OpaiTV-Sync://V1/${base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "")}`)).toEqual(expected);

    const link = encodeVideoSourceSyncLink(sources);
    const wrapped = `  ${link.slice(0, 40)}\n${link.slice(40, 90)} \r\n${link.slice(90)}\n`;
    expect(isVideoSourceSyncLink(wrapped)).toBe(true);
    expect(decodeVideoSourceSyncLink(wrapped)).toEqual(sources);
  });

  it("rejects anything that is not a complete sync link", () => {
    const link = encodeVideoSourceSyncLink(sources);

    expect(isVideoSourceSyncLink("https://a.example/api.php/provide/vod")).toBe(false);
    expect(isVideoSourceSyncLink("")).toBe(false);
    expect(isVideoSourceSyncLink(undefined)).toBe(false);
    expect(() => encodeVideoSourceSyncLink([])).toThrow("EMPTY_VIDEO_SOURCE_SYNC");

    for (const broken of [
      "https://a.example/vod",
      "opaitv-sync://",
      "opaitv-sync://z1/",
      "opaitv-sync://z1/%%%",
      "opaitv-sync://z1/AAAA",
      link.slice(0, link.length - 12),
      `opaitv-sync://v1/${Buffer.from("not json").toString("base64")}`,
      `opaitv-sync://v1/${Buffer.from('{"api_site":{}}').toString("base64")}`,
      `opaitv-sync://v1/${Buffer.from('{"sources":[]}').toString("base64")}`,
    ]) {
      expect(() => decodeVideoSourceSyncLink(broken)).toThrow("INVALID_VIDEO_SOURCE_SYNC_LINK");
    }

    expect(() => decodeVideoSourceSyncLink("opaitv-sync://z9/AAAA")).toThrow("UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK");
  });

  it("refuses compressed data that expands beyond the payload limit", () => {
    const bomb = deflateSync(strToU8(`{"api_site":[],"pad":"${"a".repeat(3 * 1024 * 1024)}"}`), { level: 9 });
    expect(bomb.length).toBeLessThan(10_000);

    expect(() => decodeVideoSourceSyncLink(`opaitv-sync://z1/${Buffer.from(bomb).toString("base64")}`)).toThrow(
      "INVALID_VIDEO_SOURCE_SYNC_LINK"
    );
  });

  describe("QR payloads", () => {
    it("puts a short link into a single QR code as is", () => {
      const link = encodeVideoSourceSyncLink(sources);

      expect(link.length).toBeLessThanOrEqual(VIDEO_SOURCE_SYNC_QR_SINGLE_MAX);
      expect(buildVideoSourceSyncQrPayloads(link)).toEqual([link]);
      expect(createVideoSourceSyncAssembler().accept(link)).toEqual({ status: "complete", link });
    });

    it("splits a long link into scannable parts that reassemble in any order", () => {
      const many = manySources(120);
      const link = encodeVideoSourceSyncLink(many);
      const parts = buildVideoSourceSyncQrPayloads(link);

      expect(link.length).toBeGreaterThan(VIDEO_SOURCE_SYNC_QR_SINGLE_MAX);
      expect(parts.length).toBeGreaterThan(1);
      for (const part of parts) {
        expect(part).toMatch(/^opaitv-sync:\/\/p1\/[0-9a-f]{8}\/\d+\/\d+\/.+$/);
        // 片头 + 片段不超过二维码版本 17(纠错等级 L)的容量 644 字节
        expect(part.length).toBeLessThanOrEqual(VIDEO_SOURCE_SYNC_QR_CHUNK + 44);
        expect(() => decodeVideoSourceSyncLink(part)).toThrow("INCOMPLETE_VIDEO_SOURCE_SYNC_LINK");
      }

      const assembler = createVideoSourceSyncAssembler();
      const order = [...parts.keys()].reverse();
      // 轮播时同一张会被扫到很多次,中间还可能扫到别的二维码
      expect(assembler.accept(parts[order[0]])).toEqual({ status: "partial", received: 1, total: parts.length });
      expect(assembler.accept(parts[order[0]])).toEqual({ status: "partial", received: 1, total: parts.length });
      expect(assembler.accept("https://example.com/other-qr")).toEqual({ status: "ignored" });

      let result = assembler.accept(parts[order[1]]);
      for (const index of order.slice(2)) {
        expect(result.status).toBe("partial");
        result = assembler.accept(parts[index]);
      }
      expect(result).toEqual({ status: "complete", link });
      expect(decodeVideoSourceSyncLink(link)).toEqual(many);
    });

    it("starts over when the codes of another export show up", () => {
      const first = buildVideoSourceSyncQrPayloads(encodeVideoSourceSyncLink(manySources(120)));
      const secondLink = encodeVideoSourceSyncLink(manySources(90));
      const second = buildVideoSourceSyncQrPayloads(secondLink);
      const assembler = createVideoSourceSyncAssembler();

      assembler.accept(first[0]);
      assembler.accept(first[1]);
      let result = assembler.accept(second[0]);
      expect(result).toEqual({ status: "partial", received: 1, total: second.length });
      for (const part of second.slice(1)) {
        result = assembler.accept(part);
      }
      expect(result).toEqual({ status: "complete", link: secondLink });
    });

    it("ignores parts that are malformed or do not add up to the announced link", () => {
      const parts = buildVideoSourceSyncQrPayloads(encodeVideoSourceSyncLink(manySources(120)));
      const assembler = createVideoSourceSyncAssembler();

      expect(assembler.accept("opaitv-sync://p1/zzzz/1/2/abc")).toEqual({ status: "ignored" });
      expect(assembler.accept("opaitv-sync://p1/0123abcd/3/2/abc")).toEqual({ status: "ignored" });

      const tampered = parts.map((part, index) => (index === 0 ? part.slice(0, -1) + (part.endsWith("A") ? "B" : "A") : part));
      let result = assembler.accept(tampered[0]);
      for (const part of tampered.slice(1)) {
        result = assembler.accept(part);
      }
      expect(result).toEqual({ status: "ignored" });
    });
  });

  describe("merging imported sources", () => {
    it("adds everything on a device without sources and marks it to survive subscription changes", () => {
      const result = mergeSyncedVideoSources([], decodeVideoSourceSyncLink(encodeVideoSourceSyncLink(sources)));

      expect(result).toMatchObject({ total: 4, added: 4, updated: 0, unchanged: 0, adultCount: 1 });
      expect(result.sources.map(({ imported: _imported, ...source }) => source)).toEqual(sources);
      expect(result.sources.every(isManualVideoSource)).toBe(true);
      // 再导出一次得到同样的链接:imported 标记不影响导出内容
      expect(encodeVideoSourceSyncLink(result.sources)).toBe(encodeVideoSourceSyncLink(sources));
    });

    it("does not add anything twice when the same link is imported again", () => {
      const once = mergeSyncedVideoSources([], sources);
      const twice = mergeSyncedVideoSources(once.sources, sources);

      expect(twice).toMatchObject({ total: 4, added: 0, updated: 0, unchanged: 4 });
      expect(twice.sources).toEqual(once.sources);
    });

    it("overrides a source with the same key, skips a known api under another key, and appends the rest", () => {
      const existing: VideoSourceSubscriptionSource[] = [
        { key: "dytt", name: "旧名字", api: "https://old.dytt.example/api.php/provide/vod" },
        { key: "mine", name: "我自己的", api: "http://192.168.1.2:8080/vod" },
        { key: "keep", name: "保留", api: "https://keep.example/vod" },
      ];

      const result = mergeSyncedVideoSources(existing, sources);

      expect(result).toMatchObject({ total: 4, added: 2, updated: 1, unchanged: 1 });
      expect(result.sources.map((source) => source.key)).toEqual(["dytt", "mine", "keep", "adult_a", "custom_a.example_8443"]);
      // 同 key:按链接里的内容覆盖,位置不变
      expect(result.sources[0]).toEqual({ ...sources[0], imported: true });
      // 同接口地址、不同 key:跳过,不重复添加
      expect(result.sources[1]).toBe(existing[1]);
      expect(result.sources[2]).toBe(existing[2]);
      expect(new Set(result.sources.map((source) => source.api)).size).toBe(result.sources.length);
    });

    it("never removes an adult mark and can mark the whole link as adult", () => {
      const existing: VideoSourceSubscriptionSource[] = [
        { key: "dytt", name: "电影天堂", api: sources[0].api, detail: sources[0].detail, adult: true },
        { key: "other", name: "同地址", api: sources[2].api },
      ];

      const kept = mergeSyncedVideoSources(existing, sources);
      expect(kept.sources[0]).toBe(existing[0]);
      // 链接里标了成人、本机同地址的源没标:补上标记
      expect(kept.sources[1]).toEqual({ ...existing[1], adult: true });
      expect(kept).toMatchObject({ added: 2, updated: 1, unchanged: 1, adultCount: 1 });

      const all = mergeSyncedVideoSources([], sources, true);
      expect(all.adultCount).toBe(4);
      expect(all.sources.every((source) => source.adult === true)).toBe(true);
    });
  });
});
