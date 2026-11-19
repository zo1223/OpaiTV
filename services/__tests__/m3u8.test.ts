import { getAdRangesFromM3U8, getResolutionFromM3U8, parseAdRangesFromM3U8 } from "@/services/m3u8";

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

  it("gives up with no ad ranges when the playlist host never answers", async () => {
    jest.useFakeTimers();
    try {
      // Like a real fetch: stays pending until the signal aborts it
      (global.fetch as jest.Mock).mockImplementation(
        (_url: string, init?: RequestInit) =>
          new Promise((_resolve, reject) => {
            init?.signal?.addEventListener("abort", () => reject(new Error("Aborted")));
          })
      );

      const settled = jest.fn();
      const pending = getAdRangesFromM3U8("https://silent.example/movie.m3u8").then((ranges) => {
        settled(ranges);
        return ranges;
      });

      await jest.advanceTimersByTimeAsync(7_000);
      expect(settled).not.toHaveBeenCalled();

      await jest.advanceTimersByTimeAsync(1_000);
      await expect(pending).resolves.toEqual([]);
    } finally {
      jest.useRealTimers();
    }
  });

  it("still stops when the caller's signal aborts", async () => {
    (global.fetch as jest.Mock).mockImplementation(
      (_url: string, init?: RequestInit) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener("abort", () => reject(new Error("Aborted")));
        })
    );
    const caller = new AbortController();

    const pending = getAdRangesFromM3U8("https://silent.example/movie.m3u8", caller.signal);
    caller.abort();

    await expect(pending).resolves.toEqual([]);
  });

  it("does not fetch non-m3u8 urls for ad ranges", async () => {
    await expect(getAdRangesFromM3U8("https://video.example/movie.mp4")).resolves.toEqual([]);

    expect(global.fetch).not.toHaveBeenCalled();
  });
});

describe("m3u8 resolution detection", () => {
  const masterPlaylist = [
    "#EXTM3U",
    "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720",
    "720p.m3u8",
    "#EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1920x1080",
    "1080p.m3u8",
  ].join("\n");

  beforeEach(() => {
    jest.clearAllMocks();
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: jest.fn().mockResolvedValue(masterPlaylist),
    }) as any;
  });

  it("detects the highest resolution for m3u8 urls with a query string", async () => {
    await expect(
      getResolutionFromM3U8("https://video.example/signed/index.m3u8?token=abc")
    ).resolves.toBe("1080p");

    expect(global.fetch).toHaveBeenCalledTimes(1);
  });

  it("does not fetch non-m3u8 urls for resolution", async () => {
    await expect(getResolutionFromM3U8("https://video.example/movie.mp4")).resolves.toBeNull();

    expect(global.fetch).not.toHaveBeenCalled();
  });
});
