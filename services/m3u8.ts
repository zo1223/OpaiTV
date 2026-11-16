import Logger from '@/utils/Logger';

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

export const getAdRangesFromM3U8 = async (
  url: string,
  signal?: AbortSignal
): Promise<M3U8AdRange[]> => {
  if (!isM3U8Url(url)) {
    return [];
  }

  try {
    const response = await fetch(url, { signal });
    if (!response.ok) {
      return [];
    }

    const playlist = await response.text();
    return parseAdRangesFromM3U8(playlist);
  } catch (error) {
    logger.info(`M3U8 ad range detection failed: ${error}`);
    return [];
  }
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
    const response = await fetch(url, { signal });
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
