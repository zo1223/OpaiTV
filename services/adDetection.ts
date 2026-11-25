import Logger from "@/utils/Logger";
import { smartFetch } from "@/services/smartRoute";
import type { M3U8AdRange } from "@/services/m3u8";

const logger = Logger.withTag("AdDetection");

/**
 * 广告多级识别(与桌面 HlsAdSkip 同一份规格,常量必须保持一致):
 *   第一级 cue           —— #EXT-X-CUE-OUT / #EXT-X-CUE-IN 显式标记;
 *   第二级 black-silence —— 片头/片尾 60 秒窗口里"黑场 + 静音"的分片(按 TS 负载字节估算,不解码画面);
 *   第三级 foreign-host  —— 与正片不同 host 的连续分片。
 * 每一级没有结果或失败就降到下一级;全部失败返回空区间,照常播放,任何情况下都不抛异常。
 */
export type AdTier = "cue" | "black-silence" | "foreign-host";
export type AdDetectionTier = AdTier | "none";
export type AdTierOutcome = "hit" | "miss" | "error";

export interface AdTierAttempt {
  tier: AdTier;
  outcome: AdTierOutcome;
  detail?: string;
}

export interface AdDetectionResult {
  ranges: M3U8AdRange[];
  tier: AdDetectionTier;
  attempts: AdTierAttempt[];
}

/** 第二级用到的分片探测器;测试里注入假实现。 */
export interface SegmentProbe {
  /** 分片字节数;拿不到返回 null。 */
  size(url: string, signal?: AbortSignal): Promise<number | null>;
  /** 分片内容;拿不到或超过上限返回 null。 */
  fetch(url: string, signal?: AbortSignal): Promise<Uint8Array | null>;
}

export interface AdDetectionOptions {
  signal?: AbortSignal;
  probe?: SegmentProbe;
  fetchText?: (url: string, signal: AbortSignal) => Promise<string>;
  /** 总预算,默认 8 秒。 */
  timeoutMs?: number;
  /** 第二级自身限时,默认 4 秒(同时受总预算约束)。 */
  blackSilenceTimeoutMs?: number;
}

export interface AdDetectionDeps extends AdDetectionOptions {
  /** 第一级解析器(services/m3u8.ts 的 parseAdRangesFromM3U8)。 */
  parseCueRanges: (playlist: string) => M3U8AdRange[];
}

export const AD_DETECTION = {
  TOTAL_TIMEOUT_MS: 8_000,
  BLACK_SILENCE_TIMEOUT_MS: 4_000,
  MIN_TOTAL_MS: 300_000,
  MIN_SEGMENTS: 6,
  WINDOW_MS: 60_000,
  WINDOW_MAX_SEGMENTS: 16,
  SIZE_CONCURRENCY: 6,
  CANDIDATE_MAX_BYTES_PER_SEC: 40_000,
  CANDIDATE_MEDIAN_FRACTION: 0.25,
  MAX_CANDIDATE_DOWNLOADS: 8,
  DOWNLOAD_CONCURRENCY: 4,
  MAX_SEGMENT_BYTES: 1_500_000,
  BLACK_MAX_VIDEO_BYTES_PER_SEC: 25_000,
  BLACK_MEDIAN_FRACTION: 0.2,
  SILENCE_MAX_AUDIO_BYTES_PER_SEC: 1_500,
  MAX_MARK_RUN_MS: 15_000,
  MAX_EDGE_BLOCK_MS: 45_000,
  MAX_FOREIGN_FRACTION: 0.5,
  MAX_FOREIGN_RUN_MS: 600_000,
} as const;

// ---------------------------------------------------------------------------
// 地址与播放列表解析
// ---------------------------------------------------------------------------

const ABSOLUTE_URL = /^[a-z][a-z0-9+.-]*:/i;
const HIERARCHICAL_URL = /^([a-z][a-z0-9+.-]*:)\/\/([^/?#]*)([^?#]*)/i;

/** 不依赖全局 URL(RN 自带的 URL 不做相对地址解析,host 也没实现)。 */
export const resolveUrl = (base: string, ref: string): string => {
  const target = ref.trim();
  if (ABSOLUTE_URL.test(target)) {
    return target;
  }

  const baseMatch = base.trim().match(HIERARCHICAL_URL);
  if (!baseMatch) {
    return target;
  }

  const [, scheme, authority, basePath] = baseMatch;
  if (target.startsWith("//")) {
    return `${scheme}${target}`;
  }

  const origin = `${scheme}//${authority}`;
  if (target.startsWith("/")) {
    return origin + target;
  }
  if (target.startsWith("?") || target.startsWith("#")) {
    return origin + basePath + target;
  }

  const directory = basePath.slice(0, basePath.lastIndexOf("/") + 1) || "/";
  const cut = target.search(/[?#]/);
  const refPath = cut >= 0 ? target.slice(0, cut) : target;
  const suffix = cut >= 0 ? target.slice(cut) : "";
  const out: string[] = [];
  for (const part of (directory + refPath).split("/")) {
    if (part === "..") {
      if (out.length > 1) {
        out.pop();
      }
    } else if (part !== ".") {
      out.push(part);
    }
  }
  return origin + out.join("/") + suffix;
};

/** 小写 host,不含端口与账号;不是 scheme://host 形式时返回空串。 */
export const hostOf = (url: string): string => {
  const match = url.trim().match(HIERARCHICAL_URL);
  if (!match) {
    return "";
  }

  let authority = match[2];
  const at = authority.lastIndexOf("@");
  if (at >= 0) {
    authority = authority.slice(at + 1);
  }
  if (authority.startsWith("[")) {
    return authority.slice(0, authority.indexOf("]") + 1).toLowerCase();
  }
  const colon = authority.lastIndexOf(":");
  if (colon >= 0) {
    authority = authority.slice(0, colon);
  }
  return authority.toLowerCase();
};

export interface PlaylistSegment {
  index: number;
  startMs: number;
  durationMs: number;
  endMs: number;
  /** 播放列表里的原始写法。 */
  uri: string;
  /** 按播放列表地址解析后的绝对地址;没有地址行时为空串。 */
  url: string;
}

const segmentMillis = (line: string): number => {
  const match = line.match(/^#EXTINF:([\d.]+)/);
  if (!match) {
    return 0;
  }
  const millis = Math.round(Number(match[1]) * 1000);
  return Number.isFinite(millis) ? millis : 0;
};

export const parsePlaylistSegments = (playlist: string, baseUrl: string): PlaylistSegment[] => {
  const lines = playlist.split(/\r?\n/);
  const segments: PlaylistSegment[] = [];
  let elapsed = 0;

  for (let i = 0; i < lines.length; i += 1) {
    const millis = segmentMillis(lines[i].trim());
    if (millis <= 0) {
      continue;
    }

    let uri = "";
    for (let j = i + 1; j < lines.length; j += 1) {
      const candidate = lines[j].trim();
      if (!candidate || candidate.startsWith("#")) {
        continue;
      }
      uri = candidate;
      break;
    }

    segments.push({
      index: segments.length,
      startMs: elapsed,
      durationMs: millis,
      endMs: elapsed + millis,
      uri,
      url: uri ? resolveUrl(baseUrl, uri) : "",
    });
    elapsed += millis;
  }

  return segments;
};

const isMasterPlaylist = (playlist: string) => playlist.includes("#EXT-X-STREAM-INF");

const firstVariant = (playlist: string): string | null => {
  for (const rawLine of playlist.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line && !line.startsWith("#") && /\.m3u8/i.test(line)) {
      return line;
    }
  }
  return null;
};

// ---------------------------------------------------------------------------
// 第三级:异源段启发式(移植自桌面 HlsAdSkip.ParseForeignHostAdRanges)
// ---------------------------------------------------------------------------

export const parseForeignHostAdRanges = (playlist: string, baseUrl: string): M3U8AdRange[] => {
  if (!HIERARCHICAL_URL.test(baseUrl.trim())) {
    return [];
  }

  const segments = parsePlaylistSegments(playlist, baseUrl).map((segment) => ({
    start: segment.startMs,
    duration: segment.durationMs,
    host: segment.url ? hostOf(segment.url) : "",
  }));
  if (segments.length === 0) {
    return [];
  }

  const durationByHost = new Map<string, number>();
  for (const segment of segments) {
    durationByHost.set(segment.host, (durationByHost.get(segment.host) ?? 0) + segment.duration);
  }
  if (durationByHost.size < 2) {
    return [];
  }

  let dominantHost = "";
  let dominantDuration = -1;
  let total = 0;
  durationByHost.forEach((duration, host) => {
    total += duration;
    if (duration > dominantDuration) {
      dominantHost = host;
      dominantDuration = duration;
    }
  });

  const foreignTotal = total - dominantDuration;
  if (total <= 0 || foreignTotal > total * AD_DETECTION.MAX_FOREIGN_FRACTION) {
    return [];
  }

  const ranges: M3U8AdRange[] = [];
  let runStart: number | null = null;
  let runEnd = 0;
  const appendRun = () => {
    if (runStart !== null && runEnd > runStart && runEnd - runStart <= AD_DETECTION.MAX_FOREIGN_RUN_MS) {
      ranges.push({ startMillis: runStart, endMillis: runEnd });
    }
    runStart = null;
  };

  for (const segment of segments) {
    const isForeign = segment.host !== "" && segment.host !== dominantHost;
    if (isForeign) {
      if (runStart === null) {
        runStart = segment.start;
      }
      runEnd = segment.start + segment.duration;
    } else {
      appendRun();
    }
  }
  appendRun();

  return ranges;
};

// ---------------------------------------------------------------------------
// 第二级:MPEG-TS 负载统计
// ---------------------------------------------------------------------------

const TS_PACKET_SIZE = 188;
const TS_SYNC_BYTE = 0x47;
const VIDEO_STREAM_TYPES = [0x01, 0x02, 0x1b, 0x24];
const AUDIO_STREAM_TYPES = [0x03, 0x04, 0x0f, 0x11, 0x81];

export interface TsSegmentStats {
  /** 视频 PID 的 TS 负载字节数(含 PES 头,不含 TS 头与适配域)。 */
  videoBytes: number;
  /** 音频 PID 的 TS 负载字节数。 */
  audioBytes: number;
}

const findSyncOffset = (bytes: Uint8Array): number => {
  for (let offset = 0; offset < TS_PACKET_SIZE; offset += 1) {
    if (offset + TS_PACKET_SIZE * 2 >= bytes.length) {
      return -1;
    }
    if (
      bytes[offset] === TS_SYNC_BYTE &&
      bytes[offset + TS_PACKET_SIZE] === TS_SYNC_BYTE &&
      bytes[offset + TS_PACKET_SIZE * 2] === TS_SYNC_BYTE
    ) {
      return offset;
    }
  }
  return -1;
};

/** 负载起点(绝对下标);该包没有负载时返回 -1。 */
const payloadStartOf = (bytes: Uint8Array, packet: number): number => {
  const adaptation = (bytes[packet + 3] >> 4) & 0x3;
  if (adaptation === 0 || adaptation === 2) {
    return -1;
  }
  const start = adaptation === 3 ? packet + 5 + bytes[packet + 4] : packet + 4;
  return start < packet + TS_PACKET_SIZE ? start : -1;
};

/**
 * 最小 MPEG-TS 解析:PAT(PID 0) → PMT → 各基本流的类型,再累计视频 / 音频 PID 的负载字节。
 * 不是 TS(找不到同步字节、没有 PAT/PMT)时返回 null。
 */
export const analyzeTsSegment = (bytes: Uint8Array | null | undefined): TsSegmentStats | null => {
  if (!bytes || bytes.length < TS_PACKET_SIZE * 3) {
    return null;
  }

  const offset = findSyncOffset(bytes);
  if (offset < 0) {
    return null;
  }

  let pmtPid = -1;
  let sawPmt = false;
  const videoPids = new Set<number>();
  const audioPids = new Set<number>();

  // 第一遍:只找 PAT / PMT。
  for (let packet = offset; packet + TS_PACKET_SIZE <= bytes.length && !sawPmt; packet += TS_PACKET_SIZE) {
    if (bytes[packet] !== TS_SYNC_BYTE || (bytes[packet + 1] & 0x40) === 0) {
      continue;
    }
    const pid = ((bytes[packet + 1] & 0x1f) << 8) | bytes[packet + 2];
    if (pid !== 0 && pid !== pmtPid) {
      continue;
    }
    const payload = payloadStartOf(bytes, packet);
    if (payload < 0) {
      continue;
    }

    const packetEnd = packet + TS_PACKET_SIZE;
    const section = payload + 1 + bytes[payload];
    if (section + 12 > packetEnd) {
      continue;
    }
    const sectionLength = ((bytes[section + 1] & 0x0f) << 8) | bytes[section + 2];
    const sectionEnd = Math.min(section + 3 + sectionLength - 4, packetEnd);

    if (pid === 0) {
      if (bytes[section] !== 0x00) {
        continue;
      }
      for (let i = section + 8; i + 4 <= sectionEnd; i += 4) {
        const programNumber = (bytes[i] << 8) | bytes[i + 1];
        if (programNumber !== 0) {
          pmtPid = ((bytes[i + 2] & 0x1f) << 8) | bytes[i + 3];
          break;
        }
      }
    } else {
      if (bytes[section] !== 0x02) {
        continue;
      }
      const programInfoLength = ((bytes[section + 10] & 0x0f) << 8) | bytes[section + 11];
      for (let i = section + 12 + programInfoLength; i + 5 <= sectionEnd; ) {
        const streamType = bytes[i];
        const elementaryPid = ((bytes[i + 1] & 0x1f) << 8) | bytes[i + 2];
        const esInfoLength = ((bytes[i + 3] & 0x0f) << 8) | bytes[i + 4];
        if (VIDEO_STREAM_TYPES.includes(streamType)) {
          videoPids.add(elementaryPid);
        } else if (AUDIO_STREAM_TYPES.includes(streamType)) {
          audioPids.add(elementaryPid);
        }
        i += 5 + esInfoLength;
      }
      sawPmt = true;
    }
  }

  if (!sawPmt) {
    return null;
  }

  // 第二遍:累计负载字节。
  let videoBytes = 0;
  let audioBytes = 0;
  for (let packet = offset; packet + TS_PACKET_SIZE <= bytes.length; packet += TS_PACKET_SIZE) {
    if (bytes[packet] !== TS_SYNC_BYTE) {
      continue;
    }
    const pid = ((bytes[packet + 1] & 0x1f) << 8) | bytes[packet + 2];
    const isVideo = videoPids.has(pid);
    if (!isVideo && !audioPids.has(pid)) {
      continue;
    }
    const payload = payloadStartOf(bytes, packet);
    if (payload < 0) {
      continue;
    }
    const length = packet + TS_PACKET_SIZE - payload;
    if (isVideo) {
      videoBytes += length;
    } else {
      audioBytes += length;
    }
  }

  return { videoBytes, audioBytes };
};

// ---------------------------------------------------------------------------
// 第二级:片头片尾黑场 + 静音
// ---------------------------------------------------------------------------

const raceAbort = <T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> => {
  if (!signal) {
    return promise;
  }
  if (signal.aborted) {
    promise.catch(() => undefined);
    return Promise.reject(new Error("Aborted"));
  }
  return new Promise<T>((resolve, reject) => {
    const onAbort = () => reject(new Error("Aborted"));
    signal.addEventListener("abort", onAbort);
    promise.then(
      (value) => {
        signal.removeEventListener("abort", onAbort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener("abort", onAbort);
        reject(error);
      }
    );
  });
};

const mapWithConcurrency = async <T, R>(
  items: T[],
  limit: number,
  worker: (item: T, index: number) => Promise<R>
): Promise<R[]> => {
  const results = new Array<R>(items.length);
  let next = 0;
  const run = async () => {
    while (next < items.length) {
      const current = next;
      next += 1;
      results[current] = await worker(items[current], current);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, run));
  return results;
};

const medianOf = (values: number[]): number => {
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
};

const isEncryptedPlaylist = (playlist: string): boolean => {
  for (const rawLine of playlist.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!/^#EXT-X-KEY:/i.test(line)) {
      continue;
    }
    const method = line.match(/METHOD=([^,\s]+)/i);
    if (!method || method[1].toUpperCase() !== "NONE") {
      return true;
    }
  }
  return false;
};

/** 默认探测器:大小用 HEAD 的 Content-Length,内容用 GET(超过上限放弃)。 */
export const createDefaultSegmentProbe = (): SegmentProbe => ({
  async size(url, signal) {
    const response = await smartFetch(url, { method: "HEAD", signal });
    if (!response?.ok) {
      return null;
    }
    const raw = response.headers?.get?.("content-length");
    const size = raw == null || raw === "" ? NaN : Number(raw);
    return Number.isFinite(size) && size > 0 ? size : null;
  },
  async fetch(url, signal) {
    const response = await smartFetch(url, { signal });
    if (!response?.ok || typeof response.arrayBuffer !== "function") {
      return null;
    }
    const declared = Number(response.headers?.get?.("content-length"));
    if (Number.isFinite(declared) && declared > AD_DETECTION.MAX_SEGMENT_BYTES) {
      return null;
    }
    const buffer = await response.arrayBuffer();
    if (buffer.byteLength > AD_DETECTION.MAX_SEGMENT_BYTES) {
      return null;
    }
    return new Uint8Array(buffer);
  },
});

export interface BlackSilenceResult {
  outcome: AdTierOutcome;
  ranges: M3U8AdRange[];
  detail?: string;
}

/**
 * 第二级。只看片头 / 片尾各 60 秒窗口:先取各分片大小,码率远低于中位数的才下载,
 * 再用 TS 负载字节判断"黑场 + 静音"。不抛异常:失败一律返回 outcome = "error"。
 */
export const detectBlackSilenceAdRanges = async (
  playlist: string,
  playlistUrl: string,
  probe: SegmentProbe,
  signal?: AbortSignal
): Promise<BlackSilenceResult> => {
  const fail = (detail: string): BlackSilenceResult => ({ outcome: "error", ranges: [], detail });
  const miss = (detail: string): BlackSilenceResult => ({ outcome: "miss", ranges: [], detail });

  try {
    if (isEncryptedPlaylist(playlist)) {
      return fail("encrypted");
    }
    if (/^\s*#EXT-X-MAP\b/im.test(playlist)) {
      return fail("fmp4");
    }

    const segments = parsePlaylistSegments(playlist, playlistUrl);
    const total = segments.length > 0 ? segments[segments.length - 1].endMs : 0;
    if (total < AD_DETECTION.MIN_TOTAL_MS || segments.length < AD_DETECTION.MIN_SEGMENTS) {
      return miss("too-short");
    }

    const head = segments
      .filter((segment) => segment.startMs < AD_DETECTION.WINDOW_MS)
      .slice(0, AD_DETECTION.WINDOW_MAX_SEGMENTS);
    const tail = segments
      .filter((segment) => segment.endMs > total - AD_DETECTION.WINDOW_MS)
      .slice(-AD_DETECTION.WINDOW_MAX_SEGMENTS);
    const windowIndexes = new Set<number>([...head, ...tail].map((segment) => segment.index));
    const windowSegments = segments.filter((segment) => windowIndexes.has(segment.index));

    const sizes = await mapWithConcurrency(windowSegments, AD_DETECTION.SIZE_CONCURRENCY, async (segment) => {
      if (!segment.url) {
        return null;
      }
      try {
        const size = await raceAbort(probe.size(segment.url, signal), signal);
        return typeof size === "number" && Number.isFinite(size) && size > 0 ? size : null;
      } catch (error) {
        if (signal?.aborted) {
          throw error;
        }
        return null;
      }
    });

    const measured: { segment: PlaylistSegment; size: number; rate: number }[] = [];
    windowSegments.forEach((segment, i) => {
      const size = sizes[i];
      if (size !== null) {
        measured.push({ segment, size, rate: size / (segment.durationMs / 1000) });
      }
    });
    if (measured.length * 2 < windowSegments.length) {
      return fail("size-unavailable");
    }

    const median = medianOf(measured.map((entry) => entry.rate));
    const candidates = measured
      .filter(
        (entry) =>
          entry.rate <= AD_DETECTION.CANDIDATE_MAX_BYTES_PER_SEC &&
          entry.rate <= AD_DETECTION.CANDIDATE_MEDIAN_FRACTION * median
      )
      // 候选多于上限时先看码率最低的(并列按播放列表顺序)。
      .sort((a, b) => a.rate - b.rate || a.segment.index - b.segment.index)
      .slice(0, AD_DETECTION.MAX_CANDIDATE_DOWNLOADS);
    if (candidates.length === 0) {
      return miss("no-candidates");
    }

    const blackLimit = Math.min(
      AD_DETECTION.BLACK_MAX_VIDEO_BYTES_PER_SEC,
      AD_DETECTION.BLACK_MEDIAN_FRACTION * median
    );
    const marked = new Set<number>();
    let analyzed = 0;
    await mapWithConcurrency(candidates, AD_DETECTION.DOWNLOAD_CONCURRENCY, async (candidate) => {
      if (candidate.size > AD_DETECTION.MAX_SEGMENT_BYTES) {
        return;
      }
      let bytes: Uint8Array | null = null;
      try {
        bytes = await raceAbort(probe.fetch(candidate.segment.url, signal), signal);
      } catch (error) {
        if (signal?.aborted) {
          throw error;
        }
        return;
      }
      if (!bytes || bytes.length > AD_DETECTION.MAX_SEGMENT_BYTES) {
        return;
      }
      const stats = analyzeTsSegment(bytes);
      if (!stats) {
        return;
      }
      analyzed += 1;
      const seconds = candidate.segment.durationMs / 1000;
      const isBlack = stats.videoBytes / seconds <= blackLimit;
      const isSilent = stats.audioBytes / seconds <= AD_DETECTION.SILENCE_MAX_AUDIO_BYTES_PER_SEC;
      if (isBlack && isSilent) {
        marked.add(candidate.segment.index);
      }
    });
    if (analyzed === 0) {
      return fail("candidates-unreadable");
    }

    // 相邻的标记分片合成一段;过长的段不是转场黑场,丢弃。
    const runs: { startMs: number; endMs: number }[] = [];
    let previousIndex = -2;
    for (const index of Array.from(marked).sort((a, b) => a - b)) {
      const segment = segments[index];
      if (index === previousIndex + 1 && runs.length > 0) {
        runs[runs.length - 1].endMs = segment.endMs;
      } else {
        runs.push({ startMs: segment.startMs, endMs: segment.endMs });
      }
      previousIndex = index;
    }
    const validRuns = runs.filter((run) => run.endMs - run.startMs <= AD_DETECTION.MAX_MARK_RUN_MS);

    const ranges: M3U8AdRange[] = [];
    const headRuns = validRuns.filter((run) => run.endMs <= AD_DETECTION.MAX_EDGE_BLOCK_MS);
    if (headRuns.length > 0) {
      ranges.push({ startMillis: 0, endMillis: headRuns[headRuns.length - 1].endMs });
    }
    const tailRun = validRuns.find((run) => run.startMs >= total - AD_DETECTION.MAX_EDGE_BLOCK_MS);
    if (tailRun) {
      ranges.push({ startMillis: tailRun.startMs, endMillis: total });
    }

    return ranges.length > 0 ? { outcome: "hit", ranges } : miss("no-marks");
  } catch (error) {
    return fail(signal?.aborted ? "timeout" : String(error));
  }
};

// ---------------------------------------------------------------------------
// 流水线
// ---------------------------------------------------------------------------

const defaultFetchText = async (url: string, signal: AbortSignal): Promise<string> => {
  const response = await smartFetch(url, { signal });
  if (!response?.ok) {
    throw new Error(`HTTP ${response?.status}`);
  }
  return response.text();
};

/** 关联一个父信号与一个限时,任一触发即中止。 */
const linkedAbort = (parent: AbortSignal | undefined, timeoutMs: number) => {
  const controller = new AbortController();
  const abort = () => controller.abort();
  const timer = setTimeout(abort, timeoutMs);
  if (parent?.aborted) {
    abort();
  } else {
    parent?.addEventListener("abort", abort);
  }
  return {
    signal: controller.signal,
    dispose: () => {
      clearTimeout(timer);
      parent?.removeEventListener("abort", abort);
    },
  };
};

export const runAdDetection = async (url: string, deps: AdDetectionDeps): Promise<AdDetectionResult> => {
  const attempts: AdTierAttempt[] = [];
  const none = (): AdDetectionResult => ({ ranges: [], tier: "none", attempts });
  const total = linkedAbort(deps.signal, deps.timeoutMs ?? AD_DETECTION.TOTAL_TIMEOUT_MS);

  try {
    const fetchText = deps.fetchText ?? defaultFetchText;

    // 0. 取播放列表;主列表再取第一条变体(变体失败退回主列表)。
    let playlistUrl = url;
    let playlist: string;
    try {
      playlist = await raceAbort(fetchText(url, total.signal), total.signal);
      if (typeof playlist !== "string") {
        return none();
      }
      if (isMasterPlaylist(playlist)) {
        const variant = firstVariant(playlist);
        if (variant) {
          const variantUrl = resolveUrl(url, variant);
          try {
            const variantPlaylist = await raceAbort(fetchText(variantUrl, total.signal), total.signal);
            if (typeof variantPlaylist === "string") {
              playlist = variantPlaylist;
              playlistUrl = variantUrl;
            }
          } catch {
            // 变体取失败时退回主列表。
          }
        }
      }
    } catch (error) {
      logger.info(`M3U8 ad range detection failed: ${error}`);
      return none();
    }

    // 1. 显式 CUE 标记。
    try {
      const cueRanges = deps.parseCueRanges(playlist);
      if (cueRanges.length > 0) {
        attempts.push({ tier: "cue", outcome: "hit" });
        return { ranges: cueRanges, tier: "cue", attempts };
      }
      attempts.push({ tier: "cue", outcome: "miss" });
    } catch (error) {
      attempts.push({ tier: "cue", outcome: "error", detail: String(error) });
    }

    // 2. 片头片尾黑场 + 静音。
    const tierTwo = linkedAbort(total.signal, deps.blackSilenceTimeoutMs ?? AD_DETECTION.BLACK_SILENCE_TIMEOUT_MS);
    try {
      const result = await detectBlackSilenceAdRanges(
        playlist,
        playlistUrl,
        deps.probe ?? createDefaultSegmentProbe(),
        tierTwo.signal
      );
      attempts.push({ tier: "black-silence", outcome: result.outcome, detail: result.detail });
      if (result.outcome === "hit") {
        return { ranges: result.ranges, tier: "black-silence", attempts };
      }
    } catch (error) {
      attempts.push({ tier: "black-silence", outcome: "error", detail: String(error) });
    } finally {
      tierTwo.dispose();
    }

    // 3. 异源段启发式。
    try {
      const foreignRanges = parseForeignHostAdRanges(playlist, playlistUrl);
      if (foreignRanges.length > 0) {
        attempts.push({ tier: "foreign-host", outcome: "hit" });
        return { ranges: foreignRanges, tier: "foreign-host", attempts };
      }
      attempts.push({ tier: "foreign-host", outcome: "miss" });
    } catch (error) {
      attempts.push({ tier: "foreign-host", outcome: "error", detail: String(error) });
    }

    return none();
  } catch (error) {
    logger.info(`M3U8 ad detection pipeline failed: ${error}`);
    return none();
  } finally {
    total.dispose();
  }
};
