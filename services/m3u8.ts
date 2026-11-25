import Logger from '@/utils/Logger';
import { smartFetch } from '@/services/smartRoute';
import { runAdDetection } from '@/services/adDetection';
import type { AdDetectionOptions, AdDetectionResult } from '@/services/adDetection';

const logger = Logger.withTag('M3U8');

interface CacheEntry {
  resolution: string | null;
  timestamp: number;
}

export interface M3U8AdRange {
  startMillis: number;
  endMillis: number;
}

const resolutionCache: { [url: string]: CacheEntry } = {};
const CACHE_DURATION = 5 * 60 * 1000; // 5 minutes
const isM3U8Url = (url: string) => /\.m3u8(?:[?#]|$)/i.test(url.trim());

export const parseAdRangesFromM3U8 = (playlist: string): M3U8AdRange[] => {
  const ranges: M3U8AdRange[] = [];
  const lines = playlist.split(/\r?\n/);
  let elapsedMillis = 0;
  let currentAdStartMillis: number | null = null;

  for (const rawLine of lines) {
    const line = rawLine.trim();

    if (/^#EXT-X-CUE-OUT(?::|$)/.test(line)) {
      currentAdStartMillis = elapsedMillis;
      continue;
    }

    if (/^#EXT-X-CUE-IN(?::|$)/.test(line)) {
      if (currentAdStartMillis !== null && elapsedMillis > currentAdStartMillis) {
        ranges.push({
          startMillis: currentAdStartMillis,
          endMillis: elapsedMillis,
        });
      }
      currentAdStartMillis = null;
      continue;
    }

    const extInfMatch = line.match(/^#EXTINF:([\d.]+)/);
    if (extInfMatch) {
      elapsedMillis += Math.round(Number(extInfMatch[1]) * 1000);
    }
  }

  return ranges;
};

// playerStore 在把剧集地址交给播放器之前会等广告区间检测结束,而 RN 的 OkHttp 客户端
// 没有连接/读取超时:播放列表地址连得上却一直不回数据时,播放页会永远停在加载中,
// 播放器拿不到地址也就不会报错换源。超时后按"没有广告区间"处理,照常开始播放。
const AD_RANGE_REQUEST_TIMEOUT_MS = 8_000;

/**
 * 广告多级识别:CUE 标记 → 片头片尾黑场 + 静音 → 异源段启发式,逐级回落(见 services/adDetection.ts)。
 * 返回命中的级别与每一级的结果;任何失败都只会得到空区间,不抛异常。
 */
export const detectAdsFromM3U8 = async (
  url: string,
  options: AdDetectionOptions = {}
): Promise<AdDetectionResult> => {
  if (typeof url !== "string" || !isM3U8Url(url)) {
    return { ranges: [], tier: "none", attempts: [] };
  }

  return runAdDetection(url, {
    timeoutMs: AD_RANGE_REQUEST_TIMEOUT_MS,
    ...options,
    parseCueRanges: parseAdRangesFromM3U8,
  });
};

export const getAdRangesFromM3U8 = async (
  url: string,
  signal?: AbortSignal
): Promise<M3U8AdRange[]> => {
  const result = await detectAdsFromM3U8(url, { signal });
  return result.ranges;
};

export const getResolutionFromM3U8 = async (
  url: string,
  signal?: AbortSignal
): Promise<string | null> => {
  const perfStart = performance.now();
  logger.info(`[PERF] M3U8 resolution detection START - url: ${url.substring(0, 100)}...`);
  
  // 1. Check cache first
  const cachedEntry = resolutionCache[url];
  if (cachedEntry && Date.now() - cachedEntry.timestamp < CACHE_DURATION) {
    const perfEnd = performance.now();
    logger.info(`[PERF] M3U8 resolution detection CACHED - took ${(perfEnd - perfStart).toFixed(2)}ms, resolution: ${cachedEntry.resolution}`);
    return cachedEntry.resolution;
  }

  // 与广告区间检测同口径:带 ?token= / #hash 的签名地址同样是 m3u8
  if (!isM3U8Url(url)) {
    logger.info(`[PERF] M3U8 resolution detection SKIPPED - not M3U8 file`);
    return null;
  }

  try {
    const fetchStart = performance.now();
    const response = await smartFetch(url, { signal });
    const fetchEnd = performance.now();
    logger.info(`[PERF] M3U8 fetch took ${(fetchEnd - fetchStart).toFixed(2)}ms, status: ${response.status}`);
    
    if (!response.ok) {
      return null;
    }
    
    const parseStart = performance.now();
    const playlist = await response.text();
    const lines = playlist.split("\n");
    let highestResolution = 0;
    let resolutionString: string | null = null;

    for (const line of lines) {
      if (line.startsWith("#EXT-X-STREAM-INF")) {
        const resolutionMatch = line.match(/RESOLUTION=(\d+)x(\d+)/);
        if (resolutionMatch) {
          const height = parseInt(resolutionMatch[2], 10);
          if (height > highestResolution) {
            highestResolution = height;
            resolutionString = `${height}p`;
          }
        }
      }
    }
    
    const parseEnd = performance.now();
    logger.info(`[PERF] M3U8 parsing took ${(parseEnd - parseStart).toFixed(2)}ms, lines: ${lines.length}`);

    // 2. Store result in cache
    resolutionCache[url] = {
      resolution: resolutionString,
      timestamp: Date.now(),
    };

    const perfEnd = performance.now();
    logger.info(`[PERF] M3U8 resolution detection COMPLETE - took ${(perfEnd - perfStart).toFixed(2)}ms, resolution: ${resolutionString}`);
    
    return resolutionString;
  } catch (error) {
    const perfEnd = performance.now();
    logger.info(`[PERF] M3U8 resolution detection ERROR - took ${(perfEnd - perfStart).toFixed(2)}ms, error: ${error}`);
    return null;
  }
};
