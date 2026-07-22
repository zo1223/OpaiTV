import Logger from '@/utils/Logger';

const logger = Logger.withTag('ChannelHealth');

/**
 * 直播频道流畅度网络探测（对齐桌面 ChannelHealthProbe.cs）。
 * 抓取 m3u8 → 选轨 → 下载首个分片测速 →（对比声明码率/分片时长）评分，
 * 不做完整解码，可对整张频道列表批量并发探测。
 */

export type ChannelHealthCategory = 'good' | 'fair' | 'bad' | 'unknown';

export interface ChannelHealthResult {
  category: ChannelHealthCategory;
  score: number;
  detail: string;
  throughputMbps: number;
  ttfbMs: number;
  resolution?: string | null;
}

export interface HealthInput {
  reachable: boolean;
  ttfbMs: number;
  throughputBps: number;
  requiredBps?: number | null;
  resolution?: string | null;
}

// 评分常量与桌面 ChannelHealthScoring 保持一致。
export const GOOD_RATIO = 1.5;
export const FAIR_RATIO = 1.0;
export const GOOD_ABS_BPS = 3_000_000;
export const FAIR_ABS_BPS = 1_000_000;
export const SLOW_TTFB_MS = 3000;

const PROBE_TIMEOUT_MS = 8000;
const SEGMENT_TIMEOUT_MS = 8000;
const MAX_PROBE_BYTES = 2_500_000;
const ANDROID_UA =
  'Mozilla/5.0 (Linux; Android 13; Android TV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36';

export const RESULT_NOT_PROBEABLE: ChannelHealthResult = {
  category: 'unknown',
  score: 0,
  detail: '不可探测',
  throughputMbps: 0,
  ttfbMs: 0,
};

export const RESULT_UNREACHABLE: ChannelHealthResult = {
  category: 'bad',
  score: 0,
  detail: '无法连接',
  throughputMbps: 0,
  ttfbMs: 0,
};

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

const downgrade = (category: ChannelHealthCategory): ChannelHealthCategory =>
  category === 'good' ? 'fair' : category === 'fair' ? 'bad' : 'bad';

const clampScoreToCategory = (score: number, category: ChannelHealthCategory): number => {
  switch (category) {
    case 'good':
      return clamp(score, 70, 100);
    case 'fair':
      return clamp(score, 40, 69);
    case 'bad':
      return clamp(score, 0, 39);
    default:
      return 0;
  }
};

/** 中文短标签（列表徽标用）。 */
export const labelOf = (category: ChannelHealthCategory | undefined): string => {
  switch (category) {
    case 'good':
      return '流畅';
    case 'fair':
      return '一般';
    case 'bad':
      return '不畅';
    default:
      return '';
  }
};

/** 等级对应的徽标颜色。 */
export const healthColor = (category: ChannelHealthCategory | undefined): string => {
  switch (category) {
    case 'good':
      return '#00bb5e';
    case 'fair':
      return '#e6a700';
    case 'bad':
      return '#e5484d';
    default:
      return '#5a5f66';
  }
};

/** 纯评分函数（与桌面 ChannelHealthScoring.Evaluate 完全对应，便于单测）。 */
export const evaluateChannelHealth = (input: HealthInput): ChannelHealthResult => {
  const { reachable, ttfbMs, throughputBps } = input;
  const requiredBps = input.requiredBps ?? null;
  const resolution = input.resolution ?? null;

  if (!reachable) {
    return { ...RESULT_UNREACHABLE, ttfbMs };
  }

  if (throughputBps <= 0) {
    return { category: 'fair', score: 40, detail: '已连接·未测速', throughputMbps: 0, ttfbMs, resolution };
  }

  const ratio = requiredBps && requiredBps > 0 ? throughputBps / requiredBps : null;

  let category: ChannelHealthCategory;
  let throughputScore: number;
  if (ratio !== null) {
    category = ratio >= GOOD_RATIO ? 'good' : ratio >= FAIR_RATIO ? 'fair' : 'bad';
    throughputScore = clamp(ratio / GOOD_RATIO, 0, 1) * 70;
  } else {
    category = throughputBps >= GOOD_ABS_BPS ? 'good' : throughputBps >= FAIR_ABS_BPS ? 'fair' : 'bad';
    throughputScore = clamp(throughputBps / GOOD_ABS_BPS, 0, 1) * 70;
  }

  if (ttfbMs > SLOW_TTFB_MS) {
    category = downgrade(category);
  }

  const latencyScore = clamp(1 - ttfbMs / SLOW_TTFB_MS, 0, 1) * 30;
  const score = clampScoreToCategory(Math.round(throughputScore + latencyScore), category);

  const mbps = throughputBps / 1_000_000;
  let detail = `${mbps.toFixed(1)} Mbps`;
  if (ratio !== null && ratio < FAIR_RATIO) {
    detail += ' · 跟不上';
  }
  if (resolution) {
    detail += ` · ${resolution}`;
  }

  return { category, score, detail, throughputMbps: mbps, ttfbMs, resolution };
};

const isM3u8Url = (url: string) => /\.m3u8(?:[?#]|$)/i.test(url.trim());

const isMasterPlaylist = (text: string) => /#EXT-X-STREAM-INF/i.test(text);

const resolveUrl = (base: string, ref: string): string => {
  if (/^https?:\/\//i.test(ref)) {
    return ref;
  }

  try {
    return new URL(ref, base).toString();
  } catch {
    if (ref.startsWith('/')) {
      const origin = base.match(/^(https?:\/\/[^/]+)/i);
      return origin ? origin[1] + ref : ref;
    }
    const slash = base.lastIndexOf('/');
    return slash >= 0 ? base.slice(0, slash + 1) + ref : ref;
  }
};

const selectFirstVariant = (
  playlist: string
): { uri: string; bps: number | null; resolution: string | null } | null => {
  const lines = playlist.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!/^#EXT-X-STREAM-INF/i.test(line)) {
      continue;
    }

    const bandwidth = line.match(/BANDWIDTH=(\d+)/i);
    const resolution = line.match(/RESOLUTION=(\d+x\d+)/i);
    for (let j = i + 1; j < lines.length; j++) {
      const candidate = lines[j].trim();
      if (candidate.length > 0 && !candidate.startsWith('#')) {
        return {
          uri: candidate,
          bps: bandwidth ? Number(bandwidth[1]) : null,
          resolution: resolution ? resolution[1] : null,
        };
      }
    }
  }

  return null;
};

const selectFirstSegment = (playlist: string): { uri: string; durationSec: number } | null => {
  const lines = playlist.split('\n');
  let duration = 0;
  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (line.length === 0) {
      continue;
    }

    const extInf = line.match(/^#EXTINF:([\d.]+)/);
    if (extInf) {
      duration = Number(extInf[1]) || 0;
      continue;
    }

    if (line.startsWith('#')) {
      continue;
    }

    return { uri: line, durationSec: duration };
  }

  return null;
};

interface LinkedSignal {
  signal: AbortSignal;
  cleanup: () => void;
}

const linkedSignal = (timeoutMs: number, external?: AbortSignal): LinkedSignal => {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  const onAbort = () => controller.abort();

  if (external) {
    if (external.aborted) {
      controller.abort();
    } else {
      external.addEventListener?.('abort', onAbort);
    }
  }

  return {
    signal: controller.signal,
    cleanup: () => {
      clearTimeout(timer);
      external?.removeEventListener?.('abort', onAbort);
    },
  };
};

const measureSegment = async (
  url: string,
  external?: AbortSignal
): Promise<{ bps: number; bytes: number; fullBytes: number } | null> => {
  const { signal, cleanup } = linkedSignal(SEGMENT_TIMEOUT_MS, external);
  try {
    const start = Date.now();
    const response = await fetch(url, {
      signal,
      headers: { 'User-Agent': ANDROID_UA, Range: `bytes=0-${MAX_PROBE_BYTES - 1}` },
    });

    if (!response.ok && response.status !== 206) {
      return null;
    }

    const buffer = await response.arrayBuffer();
    const seconds = Math.max((Date.now() - start) / 1000, 0.001);
    const bytes = buffer.byteLength;

    let fullBytes = 0;
    const contentRange = response.headers.get('content-range');
    if (contentRange) {
      const total = contentRange.match(/\/(\d+)\s*$/);
      if (total) {
        fullBytes = Number(total[1]) || 0;
      }
    }
    if (!fullBytes) {
      fullBytes = Number(response.headers.get('content-length')) || 0;
    }

    return { bps: (bytes * 8) / seconds, bytes, fullBytes };
  } catch (error) {
    if (external?.aborted) {
      throw error;
    }
    return null;
  } finally {
    cleanup();
  }
};

const probeHls = async (
  playlistUrl: string,
  playlistText: string,
  ttfbMs: number,
  external?: AbortSignal
): Promise<ChannelHealthResult> => {
  let declaredBps: number | null = null;
  let resolution: string | null = null;
  let mediaUrl = playlistUrl;
  let mediaText = playlistText;

  if (isMasterPlaylist(playlistText)) {
    const variant = selectFirstVariant(playlistText);
    if (!variant) {
      return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: 0 });
    }

    declaredBps = variant.bps;
    resolution = variant.resolution;
    mediaUrl = resolveUrl(playlistUrl, variant.uri);

    const { signal, cleanup } = linkedSignal(PROBE_TIMEOUT_MS, external);
    try {
      const response = await fetch(mediaUrl, { signal, headers: { 'User-Agent': ANDROID_UA } });
      if (!response.ok) {
        return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: 0, requiredBps: declaredBps, resolution });
      }
      mediaText = await response.text();
    } catch (error) {
      if (external?.aborted) {
        throw error;
      }
      return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: 0, requiredBps: declaredBps, resolution });
    } finally {
      cleanup();
    }
  }

  const segment = selectFirstSegment(mediaText);
  if (!segment) {
    return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: 0, requiredBps: declaredBps, resolution });
  }

  const measured = await measureSegment(resolveUrl(mediaUrl, segment.uri), external);
  if (!measured) {
    return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: 0, requiredBps: declaredBps, resolution });
  }

  const requiredBps =
    declaredBps && declaredBps > 0
      ? declaredBps
      : segment.durationSec > 0
        ? ((measured.fullBytes > 0 ? measured.fullBytes : measured.bytes) * 8) / segment.durationSec
        : null;

  return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: measured.bps, requiredBps, resolution });
};

/** 探测单个频道地址。 */
export const probeChannel = async (url: string, external?: AbortSignal): Promise<ChannelHealthResult> => {
  if (!/^https?:\/\//i.test(url)) {
    return RESULT_NOT_PROBEABLE;
  }

  const { signal, cleanup } = linkedSignal(PROBE_TIMEOUT_MS, external);
  try {
    const start = Date.now();
    const response = await fetch(url, { signal, headers: { 'User-Agent': ANDROID_UA } });
    const ttfbMs = Date.now() - start;

    if (!response.ok) {
      return evaluateChannelHealth({ reachable: false, ttfbMs, throughputBps: 0 });
    }

    const contentType = response.headers.get('content-type') || '';
    const looksHls = isM3u8Url(url) || /mpegurl/i.test(contentType);
    if (looksHls) {
      const text = await response.text();
      return await probeHls(url, text, ttfbMs, external);
    }

    // 原始流（.ts/.flv/直连）：读取响应测速，无声明码率走绝对阈值。
    try {
      const bufferStart = Date.now();
      const buffer = await response.arrayBuffer();
      const seconds = Math.max((Date.now() - bufferStart) / 1000, 0.001);
      const bps = (buffer.byteLength * 8) / seconds;
      return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: bps });
    } catch (error) {
      if (external?.aborted) {
        throw error;
      }
      // 连得上但读不完（无限流被超时打断）。
      return evaluateChannelHealth({ reachable: true, ttfbMs, throughputBps: 0 });
    }
  } catch (error) {
    if (external?.aborted) {
      throw error;
    }
    logger.info(`probe failed: ${url} - ${error}`);
    return evaluateChannelHealth({ reachable: false, ttfbMs: PROBE_TIMEOUT_MS, throughputBps: 0 });
  } finally {
    cleanup();
  }
};

export interface ProbeChannelsOptions {
  concurrency?: number;
  signal?: AbortSignal;
  onResult: (id: string, result: ChannelHealthResult) => void;
}

/** 批量并发探测：每个频道完成即回调（不会因单个失败而中断整体）。 */
export const probeChannels = async (
  channels: readonly { id: string; url: string }[],
  options: ProbeChannelsOptions
): Promise<void> => {
  const { concurrency = 5, signal, onResult } = options;
  let cursor = 0;

  const worker = async () => {
    while (cursor < channels.length) {
      if (signal?.aborted) {
        return;
      }

      const channel = channels[cursor++];
      let result: ChannelHealthResult;
      try {
        result = await probeChannel(channel.url, signal);
      } catch {
        if (signal?.aborted) {
          return;
        }
        result = RESULT_UNREACHABLE;
      }

      onResult(channel.id, result);
    }
  };

  const workerCount = Math.max(1, Math.min(concurrency, channels.length));
  await Promise.all(Array.from({ length: workerCount }, () => worker()));
};
