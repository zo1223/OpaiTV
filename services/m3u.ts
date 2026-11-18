import Logger from '@/utils/Logger';

const logger = Logger.withTag('M3U');

export interface Channel {
  id: string;
  name: string;
  url: string;
  logo: string;
  group: string;
}

// EXTINF 的属性区与频道名以「第一个不在引号内的逗号」分隔。
// 用 lastIndexOf 会把带逗号的频道名(如 "News, Sports HD")截断成最后一段。
const findExtinfNameSeparator = (line: string): number => {
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const char = line[i];
    if (char === '"') {
      inQuotes = !inQuotes;
    } else if (char === ',' && !inQuotes) {
      return i;
    }
  }
  // 引号不成对时退回旧行为,避免整行被当作属性
  return inQuotes ? line.lastIndexOf(',') : -1;
};

export const parseM3U = (m3uText: string): Channel[] => {
  const parsedChannels: Channel[] = [];
  const lines = m3uText.split('\n');
  let currentChannelInfo: Partial<Channel> | null = null;

  for (const line of lines) {
    const trimmedLine = line.trim();
    if (trimmedLine.startsWith('#EXTINF:')) {
      currentChannelInfo = {}; // Start a new channel
      const commaIndex = findExtinfNameSeparator(trimmedLine);
      if (commaIndex !== -1) {
        currentChannelInfo.name = trimmedLine.substring(commaIndex + 1).trim();
        const attributesPart = trimmedLine.substring(8, commaIndex);
        const logoMatch = attributesPart.match(/tvg-logo="([^"]*)"/i);
        if (logoMatch && logoMatch[1]) {
          currentChannelInfo.logo = logoMatch[1];
        }
        const groupMatch = attributesPart.match(/group-title="([^"]*)"/i);
        if (groupMatch && groupMatch[1]) {
          currentChannelInfo.group = groupMatch[1];
        }
      } else {
        currentChannelInfo.name = trimmedLine.substring(8).trim();
      }
    } else if (currentChannelInfo && trimmedLine && !trimmedLine.startsWith('#') && trimmedLine.includes('://')) {
      currentChannelInfo.url = trimmedLine;
      // URL 前缀序号保证 id 唯一:同一 URL 可能以不同名称/分组重复出现,
      // 纯 URL 作 id 会让按 id 定位频道时永远命中第一条(选台错位)
      currentChannelInfo.id = `${parsedChannels.length}-${currentChannelInfo.url}`;
      
      // Ensure all required fields are present, providing defaults if necessary
      const finalChannel: Channel = {
        id: currentChannelInfo.id,
        url: currentChannelInfo.url,
        name: currentChannelInfo.name || 'Unknown',
        logo: currentChannelInfo.logo || '',
        group: currentChannelInfo.group || 'Default',
      };
      
      parsedChannels.push(finalChannel);
      currentChannelInfo = null; // Reset for the next channel
    }
  }
  return parsedChannels;
};

// RN 的 OkHttp 客户端没有连接/读取超时:直播源地址连得上却一直不回数据时,
// fetch 永远不结束,直播页会一直停在加载中。超时后按请求失败处理(返回空列表)。
const M3U_REQUEST_TIMEOUT_MS = 30_000;

export const fetchAndParseM3u = async (m3uUrl: string): Promise<Channel[]> => {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), M3U_REQUEST_TIMEOUT_MS);
  try {
    const response = await fetch(m3uUrl, { signal: controller.signal });
    if (!response.ok) {
      throw new Error(`Failed to fetch M3U: ${response.statusText}`);
    }
    const m3uText = await response.text();
    return parseM3U(m3uText);
  } catch (error) {
    logger.info("Error fetching or parsing M3U:", error);
    return []; // Return empty array on error
  } finally {
    clearTimeout(timer);
  }
};

export const getPlayableUrl = (originalUrl: string | null): string | null => {
  if (!originalUrl) {
    return null;
  }
  // In React Native, we use the proxy for all http streams to avoid potential issues.
  // if (originalUrl.toLowerCase().startsWith('http://')) {
  //   // Use the baseURL from the existing api instance.
  //   if (!api.baseURL) {
  //       console.warn("API base URL is not set. Cannot create proxy URL.")
  //       return originalUrl; // Fallback to original URL
  //   }
  //   return `${api.baseURL}/proxy?url=${encodeURIComponent(originalUrl)}`;
  // }
  // HTTPS streams can be played directly.
  return originalUrl;
};
