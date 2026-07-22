import { getAdRangesFromM3U8, parseAdRangesFromM3U8 } from "@/services/m3u8";

describe("m3u8 ad range parsing", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    global.fetch = jest.fn() as any;
  });

  it("extracts cue-out to cue-in ad ranges in milliseconds", () => {
    const playlist = [
      "#EXTM3U",
      "#EXT-X-TARGETDURATION:10",
      "#EXTINF:10,",
      "main-1.ts",
      "#EXT-X-CUE-OUT:20",
      "#EXTINF:8,",
      "ad-1.ts",
      "#EXTINF:12,",
      "ad-2.ts",
      "#EXT-X-CUE-IN",
      "#EXTINF:10,",
      "main-2.ts",
    ].join("\n");

    expect(parseAdRangesFromM3U8(playlist)).toEqual([
      { startMillis: 10000, endMillis: 30000 },
    ]);
  });

  it("ignores playlists without explicit ad boundary markers", () => {
    const playlist = [
      "#EXTM3U",
      "#EXT-X-TARGETDURATION:10",
      "#EXTINF:10,",
      "main-1.ts",
      "#EXT-X-DISCONTINUITY",
      "#EXTINF:10,",
      "main-2.ts",
    ].join("\n");

    expect(parseAdRangesFromM3U8(playlist)).toEqual([]);
  });

  it("fetches and parses ad ranges from m3u8 urls", async () => {
    const playlist = [
      "#EXTM3U",
      "#EXTINF:10,",
      "main-1.ts",
      "#EXT-X-CUE-OUT:10",
      "#EXTINF:10,",
      "ad-1.ts",
      "#EXT-X-CUE-IN",
    ].join("\n");
    (global.fetch as jest.Mock).mockResolvedValue({
      ok: true,
      text: jest.fn().mockResolvedValue(playlist),
    });

    await expect(getAdRangesFromM3U8("https://video.example/movie.m3u8?token=1")).resolves.toEqual([
      { startMillis: 10000, endMillis: 20000 },
    ]);
  });

  it("does not fetch non-m3u8 urls for ad ranges", async () => {
    await expect(getAdRangesFromM3U8("https://video.example/movie.mp4")).resolves.toEqual([]);

    expect(global.fetch).not.toHaveBeenCalled();
  });
});
