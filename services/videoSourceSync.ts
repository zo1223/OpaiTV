import { deflateSync, Inflate, strFromU8, strToU8 } from "fflate";
import { normalizeVideoSourceConfig, VideoSourceSubscriptionSource } from "./videoSourceSubscription";

// 多端同步视频源:不经过服务器,把当前全部视频源直接编码进一段链接(同时显示为二维码),
// 另一台设备粘贴或扫码即可一次性导入。桌面端的对应实现在 desktop/OrionTV.Desktop.Core/VideoSourceSyncLink.cs,
// 两边的格式必须保持一致:
//
//   opaitv-sync://z1/<base64url(raw deflate(订阅 JSON))>   导出时使用(压缩后链接更短,二维码更容易扫)
//   opaitv-sync://v1/<base64url(订阅 JSON)>                不压缩,导入时同样识别
//   opaitv-sync://p1/<id>/<序号>/<总数>/<片段>              完整链接太长、一张二维码放不下时的分片(只用于二维码)
//
// 订阅 JSON 就是现有的订阅格式 { "api_site": [ { key, name, api, detail?, adult? } ] }。
// api_site 用数组形式:源的顺序不会因为 key 是纯数字而被 JS 对象重排。
export const VIDEO_SOURCE_SYNC_PREFIX = "opaitv-sync://";

export const INVALID_VIDEO_SOURCE_SYNC_LINK = "INVALID_VIDEO_SOURCE_SYNC_LINK";
/** 粘贴的是分片二维码里的一片,不是完整链接 */
export const INCOMPLETE_VIDEO_SOURCE_SYNC_LINK = "INCOMPLETE_VIDEO_SOURCE_SYNC_LINK";
/** 链接来自更新的版本,当前版本不认识它的编码方式 */
export const UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK = "UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK";
export const EMPTY_VIDEO_SOURCE_SYNC = "EMPTY_VIDEO_SOURCE_SYNC";

const KIND_PLAIN = "v1";
const KIND_DEFLATE = "z1";
const KIND_PART = "p1";

/** 不超过这个长度的链接放进一张二维码(纠错等级 L 下约为版本 22,105×105 个模块),屏幕对拍仍然好扫 */
export const VIDEO_SOURCE_SYNC_QR_SINGLE_MAX = 1000;
/** 分片二维码每片携带的链接长度(加上片头后约为版本 17,85×85 个模块) */
export const VIDEO_SOURCE_SYNC_QR_CHUNK = 600;
const MAX_QR_PARTS = 500;

// 解压后的 JSON 上限:正常的源列表远小于它,用来挡住恶意构造的压缩数据
const MAX_PAYLOAD_BYTES = 2 * 1024 * 1024;
const INFLATE_STEP_BYTES = 2048;

const BASE64URL_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
const BASE64_LOOKUP = (() => {
  const lookup: Record<string, number> = {};
  for (let i = 0; i < BASE64URL_ALPHABET.length; i++) {
    lookup[BASE64URL_ALPHABET[i]] = i;
  }
  // 标准 base64 的两个字符也认,手工拼出来的链接不必非得是 base64url
  lookup["+"] = 62;
  lookup["/"] = 63;
  return lookup;
})();

const toBase64Url = (bytes: Uint8Array) => {
  let result = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i];
    const b1 = i + 1 < bytes.length ? bytes[i + 1] : 0;
    const b2 = i + 2 < bytes.length ? bytes[i + 2] : 0;
    result += BASE64URL_ALPHABET[b0 >> 2];
    result += BASE64URL_ALPHABET[((b0 & 0x03) << 4) | (b1 >> 4)];
    if (i + 1 < bytes.length) result += BASE64URL_ALPHABET[((b1 & 0x0f) << 2) | (b2 >> 6)];
    if (i + 2 < bytes.length) result += BASE64URL_ALPHABET[b2 & 0x3f];
  }
  return result;
};

const fromBase64Url = (text: string) => {
  const clean = text.replace(/=+$/, "");
  if (clean.length % 4 === 1) {
    throw new Error(INVALID_VIDEO_SOURCE_SYNC_LINK);
  }
  const bytes = new Uint8Array(Math.floor((clean.length * 3) / 4));
  let buffer = 0;
  let bits = 0;
  let offset = 0;
  for (let i = 0; i < clean.length; i++) {
    const value = BASE64_LOOKUP[clean[i]];
    if (value === undefined) {
      throw new Error(INVALID_VIDEO_SOURCE_SYNC_LINK);
    }
    buffer = (buffer << 6) | value;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes[offset++] = (buffer >> bits) & 0xff;
    }
  }
  return bytes;
};

// 分段解压并累计输出大小:压缩数据可以用很小的体积展开成极大的内容,不能一次性全部解开再检查
const inflateWithLimit = (data: Uint8Array) => {
  const chunks: Uint8Array[] = [];
  let total = 0;
  const inflater = new Inflate((chunk) => {
    total += chunk.length;
    if (total > MAX_PAYLOAD_BYTES) {
      throw new Error(INVALID_VIDEO_SOURCE_SYNC_LINK);
    }
    chunks.push(chunk);
  });
  for (let offset = 0; offset < data.length; offset += INFLATE_STEP_BYTES) {
    const end = Math.min(offset + INFLATE_STEP_BYTES, data.length);
    inflater.push(data.subarray(offset, end), end >= data.length);
  }

  const result = new Uint8Array(total);
  let position = 0;
  for (const chunk of chunks) {
    result.set(chunk, position);
    position += chunk.length;
  }
  return result;
};

// 链接和分片编号只含 ASCII,两端按同样的方式逐字符计算(FNV-1a 32 位)
const checksum = (text: string) => {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
};

// 聊天软件、邮件转发长链接时常会插入换行和空格
const compact = (text: unknown) => (typeof text === "string" ? text.replace(/\s+/g, "") : "");

const LINK_PATTERN = /^opaitv-sync:\/\/([a-z0-9]+)\/(.*)$/i;
const PART_PATTERN = /^opaitv-sync:\/\/p1\/([0-9a-f]{8})\/(\d{1,4})\/(\d{1,4})\/(.+)$/i;

/** 文本是不是同步链接(含分片)。只看前缀,不保证内容能解开。 */
export const isVideoSourceSyncLink = (text: unknown) =>
  compact(text).toLowerCase().startsWith(VIDEO_SOURCE_SYNC_PREFIX);

/** 按现有订阅 JSON 格式序列化;成人标记原样保留,未标记的源不写该字段。 */
export const serializeVideoSourcesForSync = (sources: VideoSourceSubscriptionSource[]) =>
  JSON.stringify({
    api_site: sources
      .filter((source) => source.key && source.name && source.api)
      .map((source) => ({
        key: source.key,
        name: source.name,
        api: source.api,
        ...(source.detail ? { detail: source.detail } : {}),
        ...(source.adult ? { adult: true } : {}),
      })),
  });

/** 把全部视频源编码成一段同步链接。没有可导出的源时抛出 EMPTY_VIDEO_SOURCE_SYNC。 */
export const encodeVideoSourceSyncLink = (sources: VideoSourceSubscriptionSource[]) => {
  const json = serializeVideoSourcesForSync(sources);
  if (!sources.some((source) => source.key && source.name && source.api)) {
    throw new Error(EMPTY_VIDEO_SOURCE_SYNC);
  }
  return `${VIDEO_SOURCE_SYNC_PREFIX}${KIND_DEFLATE}/${toBase64Url(deflateSync(strToU8(json), { level: 9 }))}`;
};

/**
 * 解出同步链接里的全部源(与订阅解析走同一套字段整理,成人标记保留)。
 * 链接无效时抛出 INVALID_VIDEO_SOURCE_SYNC_LINK;是分片二维码里的一片时抛出 INCOMPLETE_VIDEO_SOURCE_SYNC_LINK。
 */
export const decodeVideoSourceSyncLink = (link: string): VideoSourceSubscriptionSource[] => {
  const match = LINK_PATTERN.exec(compact(link));
  if (!match) {
    throw new Error(INVALID_VIDEO_SOURCE_SYNC_LINK);
  }

  const kind = match[1].toLowerCase();
  if (kind === KIND_PART) {
    throw new Error(INCOMPLETE_VIDEO_SOURCE_SYNC_LINK);
  }
  if (kind !== KIND_PLAIN && kind !== KIND_DEFLATE) {
    throw new Error(UNSUPPORTED_VIDEO_SOURCE_SYNC_LINK);
  }

  try {
    const bytes = fromBase64Url(match[2]);
    if (bytes.length > MAX_PAYLOAD_BYTES) {
      throw new Error(INVALID_VIDEO_SOURCE_SYNC_LINK);
    }
    const json = strFromU8(kind === KIND_DEFLATE ? inflateWithLimit(bytes) : bytes);
    return normalizeVideoSourceConfig(JSON.parse(json)).sources;
  } catch {
    throw new Error(INVALID_VIDEO_SOURCE_SYNC_LINK);
  }
};

/**
 * 二维码里放的文本。链接不长时就是链接本身(一张码);太长时拆成多张分片码,
 * 每张都足够稀疏、能扫出来,扫码端用 createVideoSourceSyncAssembler 拼回完整链接。
 */
export const buildVideoSourceSyncQrPayloads = (link: string): string[] => {
  if (link.length <= VIDEO_SOURCE_SYNC_QR_SINGLE_MAX) {
    return [link];
  }

  const total = Math.ceil(link.length / VIDEO_SOURCE_SYNC_QR_CHUNK);
  const size = Math.ceil(link.length / total);
  const id = checksum(link);
  return Array.from(
    { length: total },
    (_, index) =>
      `${VIDEO_SOURCE_SYNC_PREFIX}${KIND_PART}/${id}/${index + 1}/${total}/${link.slice(index * size, (index + 1) * size)}`
  );
};

export type VideoSourceSyncScanProgress =
  /** 不是同步二维码 */
  | { status: "ignored" }
  /** 分片码:还没扫全 */
  | { status: "partial"; received: number; total: number }
  /** 得到了完整的同步链接 */
  | { status: "complete"; link: string };

/** 扫码端:逐张接收二维码内容(顺序任意、可重复),凑齐后给出完整的同步链接。 */
export const createVideoSourceSyncAssembler = () => {
  let currentId = "";
  let currentTotal = 0;
  let parts = new Map<number, string>();

  const reset = () => {
    currentId = "";
    currentTotal = 0;
    parts = new Map();
  };

  const accept = (scanned: unknown): VideoSourceSyncScanProgress => {
    const text = compact(scanned);
    if (!isVideoSourceSyncLink(text)) {
      return { status: "ignored" };
    }

    const match = PART_PATTERN.exec(text);
    if (!match) {
      // 分片格式不对的也归到这里,由解码给出"链接无效"
      return /^opaitv-sync:\/\/p1\//i.test(text) ? { status: "ignored" } : { status: "complete", link: text };
    }

    const id = match[1].toLowerCase();
    const index = Number(match[2]);
    const total = Number(match[3]);
    if (total < 1 || total > MAX_QR_PARTS || index < 1 || index > total) {
      return { status: "ignored" };
    }

    // 换了一组二维码(另一次导出):从头开始收
    if (id !== currentId || total !== currentTotal) {
      reset();
      currentId = id;
      currentTotal = total;
    }
    parts.set(index, match[4]);
    if (parts.size < total) {
      return { status: "partial", received: parts.size, total };
    }

    let link = "";
    for (let i = 1; i <= total; i++) {
      link += parts.get(i);
    }
    reset();
    return checksum(link) === id ? { status: "complete", link } : { status: "ignored" };
  };

  return { accept, reset };
};

export interface VideoSourceSyncMergeResult {
  /** 合并后的完整源列表 */
  sources: VideoSourceSubscriptionSource[];
  /** 链接里的源数量 */
  total: number;
  /** 新增的源 */
  added: number;
  /** 已存在、按链接内容更新了的源 */
  updated: number;
  /** 已存在且内容相同,跳过的源 */
  unchanged: number;
  /** 链接里的成人源数量 */
  adultCount: number;
}

const sameContent = (left: VideoSourceSubscriptionSource, right: VideoSourceSubscriptionSource) =>
  left.name === right.name &&
  left.api === right.api &&
  (left.detail || "") === (right.detail || "") &&
  Boolean(left.adult) === Boolean(right.adult);

/**
 * 把同步链接里的源并入现有列表,不产生重复:
 * - 接口地址已存在于另一个 key 下:跳过(只补上成人标记);
 * - key 已存在:用链接里的内容覆盖;
 * - 其余:新增。
 * 成人标记只增不减:链接里标了成人的照样标记,本机已标记的不会被这次导入取消。
 * 新增和被覆盖的源带上 imported 标记,订阅链接被清空或更换时保留(见 isManualVideoSource)。
 */
export const mergeSyncedVideoSources = (
  existing: VideoSourceSubscriptionSource[],
  incoming: VideoSourceSubscriptionSource[],
  markAllAdult = false
): VideoSourceSyncMergeResult => {
  const sources = [...existing];
  let added = 0;
  let updated = 0;
  let unchanged = 0;
  let adultCount = 0;

  for (const item of incoming) {
    const adult = markAllAdult || Boolean(item.adult);
    if (adult) adultCount++;

    const sameApiIndex = sources.findIndex((source) => source.api === item.api && source.key !== item.key);
    if (sameApiIndex >= 0) {
      const current = sources[sameApiIndex];
      if (adult && !current.adult) {
        sources[sameApiIndex] = { ...current, adult: true };
        updated++;
      } else {
        unchanged++;
      }
      continue;
    }

    const sameKeyIndex = sources.findIndex((source) => source.key === item.key);
    const current = sameKeyIndex >= 0 ? sources[sameKeyIndex] : undefined;
    const merged: VideoSourceSubscriptionSource = {
      key: item.key,
      name: item.name,
      api: item.api,
      ...(item.detail ? { detail: item.detail } : {}),
      ...(adult || current?.adult ? { adult: true } : {}),
      imported: true,
    };

    if (!current) {
      sources.push(merged);
      added++;
    } else if (sameContent(current, merged)) {
      unchanged++;
    } else {
      sources[sameKeyIndex] = merged;
      updated++;
    }
  }

  return { sources, total: incoming.length, added, updated, unchanged, adultCount };
};
