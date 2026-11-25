// DLNA 投屏:设备描述(SSDP 响应里 LOCATION 指向的 XML)解析。
// 只取投屏需要的三样:设备名、AVTransport 的 controlURL、AVTransport 的 serviceType。

export interface DlnaDevice {
  /** UDN(uuid:…);描述里没有时退回 "uuid:<host>"。 */
  id: string;
  name: string;
  host: string;
  modelName?: string;
  location: string;
  /** AVTransport 控制地址(绝对地址)。 */
  controlUrl: string;
  /** SOAP 命名空间用的服务类型,按设备描述里声明的版本(:1 / :2)。 */
  serviceType: string;
}

const DEFAULT_DESCRIPTION_TIMEOUT_MS = 2_500;
const MAX_DESCRIPTION_LENGTH = 512 * 1024;

const NAMED_ENTITIES: Record<string, string> = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" };

export const decodeXmlEntities = (value: string): string =>
  value.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (match, entity: string) => {
    if (entity[0] !== "#") {
      return NAMED_ENTITIES[entity.toLowerCase()] ?? match;
    }
    const code = /^#x/i.test(entity) ? parseInt(entity.slice(2), 16) : parseInt(entity.slice(1), 10);
    return code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
  });

/** 取第一个 <tag>…</tag> 的文本(忽略命名空间前缀与属性),已解码实体。 */
export const pickXmlText = (tag: string, source: string): string => {
  const match = source.match(new RegExp(`<(?:[\\w.-]+:)?${tag}(?:\\s[^>]*)?>([\\s\\S]*?)</(?:[\\w.-]+:)?${tag}\\s*>`, "i"));
  if (!match) return "";
  const cdata = match[1].match(/^\s*<!\[CDATA\[([\s\S]*?)\]\]>\s*$/);
  return decodeXmlEntities((cdata ? cdata[1] : match[1]).trim());
};

const splitUrl = (url: string): { origin: string; path: string; host: string } | null => {
  const match = url.trim().match(/^(https?:\/\/([^/?#]+))([^?#]*)/i);
  if (!match) return null;
  return { origin: match[1], host: match[2], path: match[3] || "/" };
};

/**
 * 把 controlURL 解析成绝对地址。RN 自带的 URL 不做相对路径解析,这里手工处理三种写法:
 * 绝对地址 / 以 "/" 开头(相对站点根) / 相对路径(相对 base 所在目录)。
 */
export const resolveUrl = (target: string, base: string): string | null => {
  const value = target.trim();
  if (!value) return null;
  if (/^https?:\/\//i.test(value)) return value;
  const parts = splitUrl(base);
  if (!parts) return null;
  if (value.startsWith("/")) return `${parts.origin}${value}`;
  const directory = parts.path.slice(0, parts.path.lastIndexOf("/") + 1) || "/";
  return `${parts.origin}${directory}${value}`;
};

export const hostNameOf = (url: string): string => {
  const parts = splitUrl(url);
  if (!parts) return "";
  // 去掉端口;IPv6 字面量保留方括号内的部分。
  const bracket = parts.host.match(/^\[([^\]]+)\]/);
  return bracket ? bracket[1] : parts.host.replace(/:\d+$/, "");
};

/** 解析设备描述。没有 AVTransport 服务(不能接收播放地址)时返回 null。 */
export const parseDeviceDescription = (xml: string, location: string): DlnaDevice | null => {
  if (typeof xml !== "string" || !xml) return null;
  const locationParts = splitUrl(location);
  if (!locationParts) return null;

  const services = xml.match(/<service(?:\s[^>]*)?>[\s\S]*?<\/service\s*>/gi) || [];
  let controlPath = "";
  let serviceType = "";
  for (const block of services) {
    const type = pickXmlText("serviceType", block);
    if (/:service:AVTransport:\d+/i.test(type)) {
      controlPath = pickXmlText("controlURL", block);
      serviceType = type;
      break;
    }
  }
  if (!controlPath || !serviceType) return null;

  const urlBase = pickXmlText("URLBase", xml);
  const base = /^https?:\/\//i.test(urlBase) ? urlBase : location;
  const controlUrl = resolveUrl(controlPath, base);
  if (!controlUrl) return null;

  const host = hostNameOf(location);
  return {
    id: pickXmlText("UDN", xml) || `uuid:${locationParts.host}`,
    name: pickXmlText("friendlyName", xml) || host,
    host,
    modelName: pickXmlText("modelName", xml) || pickXmlText("modelDescription", xml) || undefined,
    location,
    controlUrl,
    serviceType,
  };
};

export interface FetchDescriptionOptions {
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  /** 外部取消(发现流程结束时用):立即中止请求并清掉超时定时器。 */
  signal?: AbortSignal;
}

/** 取并解析设备描述;网络错误会抛出,不是投屏设备时返回 null。 */
export const fetchDeviceDescription = async (
  location: string,
  options: FetchDescriptionOptions = {}
): Promise<DlnaDevice | null> => {
  const fetchImpl: typeof fetch = options.fetchImpl ?? ((input, init) => fetch(input, init));
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), options.timeoutMs ?? DEFAULT_DESCRIPTION_TIMEOUT_MS);
  const cancel = () => {
    clearTimeout(timer);
    controller.abort();
  };
  if (options.signal?.aborted) {
    cancel();
  } else {
    options.signal?.addEventListener("abort", cancel);
  }
  try {
    const response = await fetchImpl(location, { signal: controller.signal });
    if (!response.ok) return null;
    const xml = await response.text();
    if (xml.length > MAX_DESCRIPTION_LENGTH) return null;
    return parseDeviceDescription(xml, location);
  } finally {
    clearTimeout(timer);
    options.signal?.removeEventListener("abort", cancel);
  }
};
