import {
  AD_DETECTION,
  SegmentProbe,
  analyzeTsSegment,
  createDefaultSegmentProbe,
  detectBlackSilenceAdRanges,
  hostOf,
  parseForeignHostAdRanges,
  parsePlaylistSegments,
  resolveUrl,
  runAdDetection,
} from "@/services/adDetection";
import { detectAdsFromM3U8, getAdRangesFromM3U8, parseAdRangesFromM3U8 } from "@/services/m3u8";

// ---- 合成 MPEG-TS ---------------------------------------------------------

const PMT_PID = 0x1000;
const VIDEO_PID = 0x100;
const AUDIO_PID = 0x101;

const tsPacket = (pid: number, payload: number[], pusi = false): Uint8Array => {
  const packet = new Uint8Array(188).fill(0xff);
  packet[0] = 0x47;
  packet[1] = (pusi ? 0x40 : 0) | ((pid >> 8) & 0x1f);
  packet[2] = pid & 0xff;
  if (payload.length >= 184) {
    packet[3] = 0x10;
    packet.set(payload.slice(0, 184), 4);
  } else {
    const adaptationLength = 183 - payload.length;
    packet[3] = 0x30;
    packet[4] = adaptationLength;
    if (adaptationLength > 0) {
      packet[5] = 0x00;
    }
    packet.set(payload, 5 + adaptationLength);
  }
  return packet;
};

const patPacket = () =>
  tsPacket(0, [0x00, 0x00, 0xb0, 0x0d, 0x00, 0x01, 0xc1, 0x00, 0x00, 0x00, 0x01, 0xe0 | (PMT_PID >> 8), PMT_PID & 0xff, 0, 0, 0, 0], true);

const pmtPacket = (streams: { type: number; pid: number }[]) => {
  const body = [0x00, 0x01, 0xc1, 0x00, 0x00, 0xe0 | (VIDEO_PID >> 8), VIDEO_PID & 0xff, 0xf0, 0x00];
  for (const stream of streams) {
    body.push(stream.type, 0xe0 | (stream.pid >> 8), stream.pid & 0xff, 0xf0, 0x00);
  }
  const sectionLength = body.length + 4;
  return tsPacket(PMT_PID, [0x00, 0x02, 0xb0 | (sectionLength >> 8), sectionLength & 0xff, ...body, 0, 0, 0, 0], true);
};

const payloadPackets = (pid: number, bytes: number): Uint8Array[] => {
  const packets: Uint8Array[] = [];
  for (let left = bytes; left > 0; left -= 184) {
    packets.push(tsPacket(pid, new Array(Math.min(184, left)).fill(0x11)));
  }
  return packets;
};

const buildTs = (
  videoBytes: number,
  audioBytes: number,
  streams = [
    { type: 0x1b, pid: VIDEO_PID },
    { type: 0x0f, pid: AUDIO_PID },
  ]
): Uint8Array => {
  const packets = [
    patPacket(),
    pmtPacket(streams),
    tsPacket(0x1fff, []), // 空包
    ...payloadPackets(VIDEO_PID, videoBytes),
    ...payloadPackets(AUDIO_PID, audioBytes),
  ];
  const out = new Uint8Array(packets.length * 188);
  packets.forEach((packet, i) => out.set(packet, i * 188));
  return out;
};

// ---- 播放列表与假探测器 ----------------------------------------------------

const BASE = "https://cdn.example/vod/index.m3u8";
const segUrl = (i: number) => `https://cdn.example/vod/seg-${i}.ts`;

/** n 个等长分片(默认 60 × 10 秒 = 600 秒)。 */
const buildPlaylist = (count = 60, seconds = 10, extra: string[] = [], uri = (i: number) => `seg-${i}.ts`) =>
  ["#EXTM3U", "#EXT-X-TARGETDURATION:10", ...extra]
    .concat(...Array.from({ length: count }, (_, i) => [`#EXTINF:${seconds},`, uri(i)]))
    .concat("#EXT-X-ENDLIST")
    .join("\n");

const NORMAL_SIZE = 2_500_000; // 10 秒 → 250 KB/s
const BLACK_TS = buildTs(40_000, 4_000); // 10 秒 → 视频 4 KB/s、音频 0.4 KB/s

interface FakeProbe extends SegmentProbe {
  size: jest.Mock;
  fetch: jest.Mock;
}

const makeProbe = (special: Record<number, Uint8Array> = {}): FakeProbe => {
  const byUrl = new Map(Object.entries(special).map(([index, bytes]) => [segUrl(Number(index)), bytes]));
  return {
    size: jest.fn(async (url: string) => byUrl.get(url)?.length ?? NORMAL_SIZE),
    fetch: jest.fn(async (url: string) => byUrl.get(url) ?? null),
  };
};

describe("analyzeTsSegment", () => {
  it("counts video and audio payload bytes without TS headers, stuffing or null packets", () => {
    expect(analyzeTsSegment(buildTs(1_000, 300))).toEqual({ videoBytes: 1_000, audioBytes: 300 });
  });

  it("recognises the stream types listed in the spec", () => {
    const stats = analyzeTsSegment(buildTs(500, 200, [{ type: 0x24, pid: VIDEO_PID }, { type: 0x81, pid: AUDIO_PID }]));
    expect(stats).toEqual({ videoBytes: 500, audioBytes: 200 });
    // 不认识的流类型不计入
    expect(analyzeTsSegment(buildTs(500, 200, [{ type: 0x06, pid: VIDEO_PID }, { type: 0x0f, pid: AUDIO_PID }]))).toEqual({
      videoBytes: 0,
      audioBytes: 200,
    });
  });

  it("tolerates leading garbage before the first sync byte", () => {
    const ts = buildTs(400, 100);
    const shifted = new Uint8Array(ts.length + 5);
    shifted.set(ts, 5);
    expect(analyzeTsSegment(shifted)).toEqual({ videoBytes: 400, audioBytes: 100 });
  });

  it("returns null for data that is not MPEG-TS", () => {
    expect(analyzeTsSegment(null)).toBeNull();
    expect(analyzeTsSegment(new Uint8Array(10))).toBeNull();
    expect(analyzeTsSegment(new Uint8Array(4_000).fill(0x20))).toBeNull();
    // 有同步字节但没有 PAT / PMT
    const noTables = new Uint8Array(188 * 4);
    payloadPackets(VIDEO_PID, 184 * 4).forEach((packet, i) => noTables.set(packet, i * 188));
    expect(analyzeTsSegment(noTables)).toBeNull();
  });
});

describe("playlist helpers", () => {
  it("resolves relative, root-relative, protocol-relative and absolute segment urls", () => {
    expect(resolveUrl(BASE, "seg-1.ts")).toBe("https://cdn.example/vod/seg-1.ts");
    expect(resolveUrl(`${BASE}?token=1`, "../ad/a.ts?x=1")).toBe("https://cdn.example/ad/a.ts?x=1");
    expect(resolveUrl(BASE, "/root/a.ts")).toBe("https://cdn.example/root/a.ts");
    expect(resolveUrl(BASE, "//ads.example/a.ts")).toBe("https://ads.example/a.ts");
    expect(resolveUrl(BASE, "http://other.example/a.ts")).toBe("http://other.example/a.ts");
    expect(hostOf("https://user:pw@CDN.Example:8443/a.ts")).toBe("cdn.example");
    expect(hostOf("not a url")).toBe("");
  });

  it("parses segments with cumulative start times", () => {
    const segments = parsePlaylistSegments(buildPlaylist(3, 4.5), BASE);
    expect(segments.map((s) => [s.startMs, s.endMs, s.url])).toEqual([
      [0, 4500, segUrl(0)],
      [4500, 9000, segUrl(1)],
      [9000, 13500, segUrl(2)],
    ]);
  });
});

describe("tier 3: foreign host heuristic", () => {
  const withForeign = (foreign: number[], count = 20) =>
    buildPlaylist(count, 10, [], (i) => (foreign.includes(i) ? `https://ads.example/ad-${i}.ts` : `seg-${i}.ts`));

  it("turns consecutive foreign-host segments into ad ranges", () => {
    expect(parseForeignHostAdRanges(withForeign([0, 1, 10]), BASE)).toEqual([
      { startMillis: 0, endMillis: 20_000 },
      { startMillis: 100_000, endMillis: 110_000 },
    ]);
  });

  it("gives up when there is a single host, too much foreign content or no absolute base", () => {
    expect(parseForeignHostAdRanges(buildPlaylist(20), BASE)).toEqual([]);
    // 三个 host:主 host 只占 40%,异源合计 60% > 50%
    const threeHosts = buildPlaylist(20, 10, [], (i) =>
      i < 8 ? `seg-${i}.ts` : i < 14 ? `https://ads-a.example/${i}.ts` : `https://ads-b.example/${i}.ts`
    );
    expect(parseForeignHostAdRanges(threeHosts, BASE)).toEqual([]);
    expect(parseForeignHostAdRanges(withForeign([0, 1]), "index.m3u8")).toEqual([]);
  });

  it("drops a foreign run longer than ten minutes", () => {
    const foreign = Array.from({ length: 61 }, (_, i) => i); // 610 秒
    expect(parseForeignHostAdRanges(withForeign(foreign, 200), BASE)).toEqual([]);
  });
});

describe("tier 2: black + silent segments at the head and tail", () => {
  it("skips from the start to the end of a head mark and from a tail mark to the end", async () => {
    const probe = makeProbe({ 2: BLACK_TS, 57: BLACK_TS });
    const result = await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe);

    expect(result).toEqual({
      outcome: "hit",
      ranges: [
        { startMillis: 0, endMillis: 30_000 },
        { startMillis: 570_000, endMillis: 600_000 },
      ],
    });
    // 只探测片头 / 片尾窗口(各 6 个 10 秒分片),只下载低码率候选
    expect(probe.size).toHaveBeenCalledTimes(12);
    expect(probe.fetch.mock.calls.map((call) => call[0]).sort()).toEqual([segUrl(2), segUrl(57)].sort());
  });

  it("uses the last head mark and merges adjacent marks", async () => {
    // 5 秒分片:0 号单独一段(0~5 秒),2、3 号相邻合成一段(10~20 秒)
    const probe = makeProbe({ 0: BLACK_TS, 2: BLACK_TS, 3: BLACK_TS });
    const result = await detectBlackSilenceAdRanges(buildPlaylist(120, 5), BASE, probe);
    expect(result.ranges).toEqual([{ startMillis: 0, endMillis: 20_000 }]);
  });

  it("ignores marks beyond the 45 second edge blocks", async () => {
    // 片头标记结束于 50 秒;片尾标记开始于 total − 60 秒
    const probe = makeProbe({ 4: BLACK_TS, 54: BLACK_TS });
    const result = await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe);
    expect(result).toEqual({ outcome: "miss", ranges: [], detail: "no-marks" });
  });

  it("drops a black run longer than 15 seconds", async () => {
    const probe = makeProbe({ 0: BLACK_TS, 1: BLACK_TS });
    const result = await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe);
    expect(result.outcome).toBe("miss");
  });

  it("requires black and silence together", async () => {
    const blackButLoud = buildTs(40_000, 80_000); // 音频 8 KB/s
    const silentButBright = buildTs(300_000, 4_000); // 视频 30 KB/s
    const probe = makeProbe({ 1: blackButLoud, 2: silentButBright });
    const result = await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe);
    expect(result).toEqual({ outcome: "miss", ranges: [], detail: "no-marks" });
    expect(probe.fetch).toHaveBeenCalledTimes(2);
  });

  it("applies the thresholds at their boundaries", async () => {
    const atLimit = buildTs(250_000, 15_000); // 正好 25 KB/s 与 1.5 KB/s
    const overVideo = buildTs(250_184, 15_000);
    const overAudio = buildTs(250_000, 15_184);
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, makeProbe({ 1: atLimit }))).outcome).toBe("hit");
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, makeProbe({ 1: overVideo }))).outcome).toBe("miss");
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, makeProbe({ 1: overAudio }))).outcome).toBe("miss");
  });

  it("reports a miss without downloading when no segment is far below the median bitrate", async () => {
    const probe = makeProbe();
    const result = await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe);
    expect(result).toEqual({ outcome: "miss", ranges: [], detail: "no-candidates" });
    expect(probe.fetch).not.toHaveBeenCalled();
  });

  it("reports a miss for short playlists without probing", async () => {
    const probe = makeProbe();
    expect((await detectBlackSilenceAdRanges(buildPlaylist(20), BASE, probe)).detail).toBe("too-short");
    expect((await detectBlackSilenceAdRanges(buildPlaylist(5, 100), BASE, probe)).detail).toBe("too-short");
    expect(probe.size).not.toHaveBeenCalled();
  });

  it("fails on encrypted or fMP4 playlists without probing", async () => {
    const probe = makeProbe({ 2: BLACK_TS });
    const encrypted = buildPlaylist(60, 10, ['#EXT-X-KEY:METHOD=AES-128,URI="key.bin"']);
    const fmp4 = buildPlaylist(60, 10, ['#EXT-X-MAP:URI="init.mp4"']);
    const clear = buildPlaylist(60, 10, ["#EXT-X-KEY:METHOD=NONE"]);

    expect(await detectBlackSilenceAdRanges(encrypted, BASE, probe)).toEqual({ outcome: "error", ranges: [], detail: "encrypted" });
    expect(await detectBlackSilenceAdRanges(fmp4, BASE, probe)).toEqual({ outcome: "error", ranges: [], detail: "fmp4" });
    expect(probe.size).not.toHaveBeenCalled();
    expect((await detectBlackSilenceAdRanges(clear, BASE, probe)).outcome).toBe("hit");
  });

  it("fails when fewer than half of the window sizes are available", async () => {
    const noLength = makeProbe();
    noLength.size.mockResolvedValue(null);
    expect(await detectBlackSilenceAdRanges(buildPlaylist(), BASE, noLength)).toEqual({
      outcome: "error",
      ranges: [],
      detail: "size-unavailable",
    });

    const throwing = makeProbe();
    throwing.size.mockRejectedValue(new Error("network down"));
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, throwing)).outcome).toBe("error");

    // 正好一半拿得到:继续判断
    const half = makeProbe({ 2: BLACK_TS });
    let call = 0;
    half.size.mockImplementation(async (url: string) => {
      call += 1;
      return url === segUrl(2) ? BLACK_TS.length : call % 2 === 0 ? NORMAL_SIZE : null;
    });
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, half)).outcome).not.toBe("error");
  });

  it("fails when none of the candidates can be read as MPEG-TS", async () => {
    const probe = makeProbe({ 2: BLACK_TS });
    probe.fetch.mockResolvedValue(new Uint8Array(2_000).fill(0x20));
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe)).detail).toBe("candidates-unreadable");

    probe.fetch.mockRejectedValue(new Error("boom"));
    expect((await detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe)).outcome).toBe("error");
  });

  it("keeps at most 6 size requests in flight and downloads at most 8 candidates", async () => {
    // 3 秒分片:片头 / 片尾窗口各有 20 个,各取 16 个封顶
    const special: Record<number, Uint8Array> = {};
    const small = buildTs(16_000, 1_600);
    for (let i = 0; i < 12; i += 1) {
      special[i] = small;
    }
    const probe = makeProbe(special);
    let inFlight = 0;
    let peak = 0;
    probe.size.mockImplementation(async (url: string) => {
      inFlight += 1;
      peak = Math.max(peak, inFlight);
      await new Promise((resolve) => setTimeout(resolve, 1));
      inFlight -= 1;
      return special[Number(url.match(/seg-(\d+)/)![1])]?.length ?? 1_000_000;
    });

    await detectBlackSilenceAdRanges(buildPlaylist(200, 3), BASE, probe);

    expect(probe.size).toHaveBeenCalledTimes(32);
    expect(peak).toBeLessThanOrEqual(AD_DETECTION.SIZE_CONCURRENCY);
    expect(probe.fetch).toHaveBeenCalledTimes(AD_DETECTION.MAX_CANDIDATE_DOWNLOADS);
  });

  it("reports a timeout when the signal aborts mid-probe", async () => {
    const controller = new AbortController();
    const probe = makeProbe();
    probe.size.mockImplementation(() => new Promise(() => undefined));
    const pending = detectBlackSilenceAdRanges(buildPlaylist(), BASE, probe, controller.signal);
    controller.abort();
    await expect(pending).resolves.toEqual({ outcome: "error", ranges: [], detail: "timeout" });
  });
});

describe("multi-tier pipeline", () => {
  const foreignPlaylist = buildPlaylist(60, 10, [], (i) => (i === 30 ? "https://ads.example/ad.ts" : `seg-${i}.ts`));
  const fetchTextOf = (playlists: Record<string, string>) =>
    jest.fn(async (url: string) => {
      if (!(url in playlists)) {
        throw new Error(`404 ${url}`);
      }
      return playlists[url];
    });

  it("tier 1: returns CUE ranges without probing any segment", async () => {
    const playlist = ["#EXTM3U", "#EXTINF:10,", "a.ts", "#EXT-X-CUE-OUT:10", "#EXTINF:10,", "ad.ts", "#EXT-X-CUE-IN", "#EXTINF:10,", "b.ts"].join("\n");
    const probe = makeProbe();
    const result = await detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: playlist }), probe });

    expect(result).toEqual({
      ranges: [{ startMillis: 10_000, endMillis: 20_000 }],
      tier: "cue",
      attempts: [{ tier: "cue", outcome: "hit" }],
    });
    expect(probe.size).not.toHaveBeenCalled();
  });

  it("tier 2: falls from CUE to black + silence when there are no markers", async () => {
    const result = await detectAdsFromM3U8(BASE, {
      fetchText: fetchTextOf({ [BASE]: buildPlaylist() }),
      probe: makeProbe({ 1: BLACK_TS }),
    });

    expect(result.tier).toBe("black-silence");
    expect(result.ranges).toEqual([{ startMillis: 0, endMillis: 20_000 }]);
    expect(result.attempts).toEqual([
      { tier: "cue", outcome: "miss" },
      { tier: "black-silence", outcome: "hit", detail: undefined },
    ]);
  });

  it("tier 3: falls to the foreign host heuristic when probing fails", async () => {
    const noLength = makeProbe();
    noLength.size.mockResolvedValue(null);
    const result = await detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: foreignPlaylist }), probe: noLength });

    expect(result.tier).toBe("foreign-host");
    expect(result.ranges).toEqual([{ startMillis: 300_000, endMillis: 310_000 }]);
    expect(result.attempts).toEqual([
      { tier: "cue", outcome: "miss" },
      { tier: "black-silence", outcome: "error", detail: "size-unavailable" },
      { tier: "foreign-host", outcome: "hit" },
    ]);
  });

  it("tier 3: also reached when the probe throws, the playlist is encrypted or tier 2 finds nothing", async () => {
    const throwing = makeProbe();
    throwing.size.mockRejectedValue(new Error("offline"));
    const encrypted = foreignPlaylist.replace("#EXT-X-TARGETDURATION:10", '#EXT-X-TARGETDURATION:10\n#EXT-X-KEY:METHOD=AES-128,URI="k"');

    const afterThrow = await detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: foreignPlaylist }), probe: throwing });
    const afterEncrypted = await detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: encrypted }), probe: makeProbe() });
    const afterMiss = await detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: foreignPlaylist }), probe: makeProbe() });

    expect(afterThrow.tier).toBe("foreign-host");
    expect(afterThrow.attempts[1]).toMatchObject({ tier: "black-silence", outcome: "error" });
    expect(afterEncrypted.tier).toBe("foreign-host");
    expect(afterEncrypted.attempts[1]).toEqual({ tier: "black-silence", outcome: "error", detail: "encrypted" });
    expect(afterMiss.tier).toBe("foreign-host");
    expect(afterMiss.attempts[1]).toEqual({ tier: "black-silence", outcome: "miss", detail: "no-candidates" });
  });

  it("tier 2 times out after 4 seconds and the pipeline still answers from tier 3", async () => {
    jest.useFakeTimers();
    try {
      const hanging = makeProbe();
      hanging.size.mockImplementation(() => new Promise(() => undefined));
      const settled = jest.fn();
      const pending = detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: foreignPlaylist }), probe: hanging }).then((result) => {
        settled(result);
        return result;
      });

      await jest.advanceTimersByTimeAsync(3_900);
      expect(settled).not.toHaveBeenCalled();
      await jest.advanceTimersByTimeAsync(200);

      const result = await pending;
      expect(result.tier).toBe("foreign-host");
      expect(result.attempts[1]).toEqual({ tier: "black-silence", outcome: "error", detail: "timeout" });
    } finally {
      jest.useRealTimers();
    }
  });

  it("returns no ranges and never throws when every tier fails", async () => {
    const failing = makeProbe();
    failing.size.mockRejectedValue(new Error("offline"));
    const result = await detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({ [BASE]: buildPlaylist() }), probe: failing });

    expect(result.ranges).toEqual([]);
    expect(result.tier).toBe("none");
    expect(result.attempts.map((attempt) => `${attempt.tier}:${attempt.outcome}`)).toEqual([
      "cue:miss",
      "black-silence:error",
      "foreign-host:miss",
    ]);
  });

  it("returns no ranges when the playlist cannot be fetched or is not text", async () => {
    await expect(detectAdsFromM3U8(BASE, { fetchText: fetchTextOf({}) })).resolves.toEqual({ ranges: [], tier: "none", attempts: [] });
    await expect(
      detectAdsFromM3U8(BASE, { fetchText: jest.fn(async () => undefined as unknown as string) })
    ).resolves.toEqual({ ranges: [], tier: "none", attempts: [] });
  });

  it("keeps going when the CUE parser itself throws", async () => {
    const result = await runAdDetection(BASE, {
      fetchText: fetchTextOf({ [BASE]: foreignPlaylist }),
      probe: makeProbe(),
      parseCueRanges: () => {
        throw new Error("bad playlist");
      },
    });

    expect(result.tier).toBe("foreign-host");
    expect(result.attempts[0]).toMatchObject({ tier: "cue", outcome: "error" });
  });

  it("resolves a master playlist to its first variant and probes relative to it", async () => {
    const master = ["#EXTM3U", "#EXT-X-STREAM-INF:BANDWIDTH=800000", "hd/index.m3u8", "#EXT-X-STREAM-INF:BANDWIDTH=300000", "sd/index.m3u8"].join("\n");
    const masterUrl = "https://cdn.example/vod/master.m3u8";
    const variantUrl = "https://cdn.example/vod/hd/index.m3u8";
    const black = "https://cdn.example/vod/hd/seg-1.ts";
    const probe: FakeProbe = {
      size: jest.fn(async (url: string) => (url === black ? BLACK_TS.length : NORMAL_SIZE)),
      fetch: jest.fn(async (url: string) => (url === black ? BLACK_TS : null)),
    };
    const fetchText = fetchTextOf({ [masterUrl]: master, [variantUrl]: buildPlaylist() });

    const result = await detectAdsFromM3U8(masterUrl, { fetchText, probe });

    expect(fetchText.mock.calls.map((call) => call[0])).toEqual([masterUrl, variantUrl]);
    expect(result.tier).toBe("black-silence");
    expect(probe.fetch).toHaveBeenCalledWith(black, expect.anything());
  });

  it("falls back to the master playlist when the variant cannot be fetched", async () => {
    const master = ["#EXTM3U", "#EXT-X-STREAM-INF:BANDWIDTH=800000", "hd/index.m3u8"].join("\n");
    const result = await detectAdsFromM3U8("https://cdn.example/vod/master.m3u8", {
      fetchText: fetchTextOf({ "https://cdn.example/vod/master.m3u8": master }),
      probe: makeProbe(),
    });
    expect(result).toMatchObject({ ranges: [], tier: "none" });
  });

  it("does not fetch anything for non-m3u8 urls", async () => {
    const fetchText = jest.fn();
    await expect(detectAdsFromM3U8("https://cdn.example/movie.mp4", { fetchText })).resolves.toEqual({
      ranges: [],
      tier: "none",
      attempts: [],
    });
    expect(fetchText).not.toHaveBeenCalled();
  });
});

describe("default probe and getAdRangesFromM3U8 over fetch", () => {
  const response = (init: { ok?: boolean; text?: string; length?: number | null; bytes?: Uint8Array }) => ({
    ok: init.ok ?? true,
    status: init.ok === false ? 404 : 200,
    headers: { get: (name: string) => (name.toLowerCase() === "content-length" && init.length != null ? String(init.length) : null) },
    text: async () => init.text ?? "",
    arrayBuffer: async () => (init.bytes ?? new Uint8Array(0)).buffer,
  });

  beforeEach(() => {
    global.fetch = jest.fn() as any;
  });

  it("reads sizes from HEAD Content-Length and bytes from GET", async () => {
    (global.fetch as jest.Mock).mockImplementation(async (url: string, init?: RequestInit) => {
      if (init?.method === "HEAD") {
        return url.endsWith("missing.ts") ? response({ ok: false }) : response({ length: url.endsWith("nolen.ts") ? null : 1234 });
      }
      return url.endsWith("big.ts") ? response({ length: 2_000_000 }) : response({ bytes: BLACK_TS, length: BLACK_TS.length });
    });
    const probe = createDefaultSegmentProbe();

    await expect(probe.size("https://cdn.example/a.ts")).resolves.toBe(1234);
    await expect(probe.size("https://cdn.example/nolen.ts")).resolves.toBeNull();
    await expect(probe.size("https://cdn.example/missing.ts")).resolves.toBeNull();
    await expect(probe.fetch("https://cdn.example/big.ts")).resolves.toBeNull();
    expect(analyzeTsSegment(await probe.fetch("https://cdn.example/a.ts"))).toEqual({ videoBytes: 40_000, audioBytes: 4_000 });
  });

  it("getAdRangesFromM3U8 still returns a plain range array, now from the black + silence tier", async () => {
    (global.fetch as jest.Mock).mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === BASE) {
        return response({ text: buildPlaylist() });
      }
      const isBlack = url === segUrl(58);
      if (init?.method === "HEAD") {
        return response({ length: isBlack ? BLACK_TS.length : NORMAL_SIZE });
      }
      return response({ bytes: BLACK_TS, length: BLACK_TS.length });
    });

    await expect(getAdRangesFromM3U8(BASE)).resolves.toEqual([{ startMillis: 580_000, endMillis: 600_000 }]);
    // 只下载了那一个低码率分片
    const gets = (global.fetch as jest.Mock).mock.calls.filter(([url, init]) => url !== BASE && init?.method !== "HEAD");
    expect(gets.map(([url]) => url)).toEqual([segUrl(58)]);
  });

  it("getAdRangesFromM3U8 plays normally (empty ranges) when segment requests fail", async () => {
    (global.fetch as jest.Mock).mockImplementation(async (url: string) => {
      if (url === BASE) {
        return response({ text: buildPlaylist() });
      }
      throw new Error("Network request failed");
    });

    await expect(getAdRangesFromM3U8(BASE)).resolves.toEqual([]);
  });

  it("keeps the CUE parser behaviour", () => {
    expect(parseAdRangesFromM3U8("#EXT-X-CUE-OUT\n#EXTINF:5,\nad.ts\n#EXT-X-CUE-IN")).toEqual([{ startMillis: 0, endMillis: 5000 }]);
  });
});
