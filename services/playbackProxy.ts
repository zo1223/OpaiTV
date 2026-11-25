// 播放代理模式(系统代理 / 强制代理 / 直连)。纯函数,不依赖任何 store。
//
// 手机端的播放器是原生的(expo-av → ExoPlayer / AVPlayer),JS 层既不能给它指定 host:port
// 形式的正向代理,也不能让它绕过系统层的代理或 VPN。所以手机端三档的实际含义是:
//  - system:按原地址请求,走不走代理由系统网络设置决定(默认);应用不做分流
//  - forced:直连优先,直连不通的源才改走用户填写的“中转地址”(URL 前缀式代理);
//           分流与每个源的路由记忆在 services/smartRoute,搜索、探测、播放都按它走
//  - direct:按原地址请求,不使用应用内填写的代理地址,也不兜底
// 模式只在“开始一次播放”时读取,切换后下次播放生效(见 stores/playerStore)。

export type PlaybackProxyMode = "system" | "forced" | "direct";

export const DEFAULT_PLAYBACK_PROXY_MODE: PlaybackProxyMode = "system";

export const PLAYBACK_PROXY_MODES: PlaybackProxyMode[] = ["system", "forced", "direct"];

export const PLAYBACK_PROXY_MODE_LABELS: Record<PlaybackProxyMode, string> = {
  system: "系统代理",
  forced: "强制代理",
  direct: "直连",
};

export const PLAYBACK_PROXY_MODE_HINTS: Record<PlaybackProxyMode, string> = {
  system: "应用不分流,按原地址请求,是否经过代理由系统网络设置决定",
  forced: "直连优先:能直连的源直接连,直连不通才走下面填写的中转地址,中转流量只花在直连不通的源上",
  direct: "只直连:按原地址请求,不使用下面的代理地址,直连不通也不兜底",
};

export interface PlaybackProxySettings {
  mode: PlaybackProxyMode;
  proxyUrl: string;
}

const URL_PLACEHOLDER = "{url}";

export const normalizePlaybackProxyMode = (value: unknown): PlaybackProxyMode => {
  if (typeof value !== "string") return DEFAULT_PLAYBACK_PROXY_MODE;
  const mode = value.trim().toLowerCase();
  return mode === "forced" || mode === "direct" ? mode : DEFAULT_PLAYBACK_PROXY_MODE;
};

export const normalizeProxyAddress = (input: unknown): string => (typeof input === "string" ? input.trim() : "");

/** 取出 http(s) 地址里的主机名(小写,不含账号与端口);不是 http(s) 绝对地址时返回 null */
const getHttpHost = (url: string): string | null => {
  const match = /^https?:\/\/([^/?#]*)/i.exec(url);
  if (!match) return null;
  let authority = match[1];
  const at = authority.lastIndexOf("@");
  if (at >= 0) authority = authority.slice(at + 1);
  let host = authority;
  if (authority.startsWith("[")) {
    const end = authority.indexOf("]");
    if (end < 0) return null;
    host = authority.slice(0, end + 1);
  } else {
    const colon = authority.indexOf(":");
    if (colon >= 0) host = authority.slice(0, colon);
  }
  host = host.toLowerCase();
  return host ? host : null;
};

/** 只接受带主机名的 http:// 或 https:// 绝对地址(host:port、socks5:// 之类都不算) */
export const isValidProxyAddress = (address: unknown): boolean => {
  const text = normalizeProxyAddress(address);
  if (!text || /\s/.test(text)) return false;
  return getHttpHost(text) !== null;
};

/**
 * 归一化:强制代理但地址为空或无效时按系统代理处理,
 * 保证设置页显示的模式与实际播放行为一致。
 */
export const normalizePlaybackProxySettings = (
  input?: { mode?: unknown; proxyUrl?: unknown } | null
): PlaybackProxySettings => {
  const proxyUrl = normalizeProxyAddress(input?.proxyUrl);
  let mode = normalizePlaybackProxyMode(input?.mode);
  if (mode === "forced" && !isValidProxyAddress(proxyUrl)) {
    mode = DEFAULT_PLAYBACK_PROXY_MODE;
  }
  return { mode, proxyUrl };
};

const isLoopbackHost = (host: string): boolean =>
  host === "localhost" ||
  host.endsWith(".localhost") ||
  host === "[::1]" ||
  host === "0.0.0.0" ||
  /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(host);

/**
 * 按代理设置得到实际交给播放器的地址。
 * 只有“强制代理 + 有效中转地址”会改写;中转地址支持三种写法:
 *  1. 含 {url} 占位符:每个占位符替换成编码后的原地址
 *  2. 以 = 结尾(如 https://relay.example/?url=):直接接上编码后的原地址
 *  3. 其它:加一个 url=<编码后的原地址> 查询参数
 * 不改写:空地址、非 http(s) 地址(file://、content:// 等本地内容)、本机回环地址、
 * 已经是经过该中转的地址(不重复套)。
 */
export const resolvePlaybackUrl = (
  url: string,
  settings?: { mode?: unknown; proxyUrl?: unknown } | null
): string => {
  if (typeof url !== "string" || !url) return url;
  const { mode, proxyUrl: relay } = normalizePlaybackProxySettings(settings);
  if (mode !== "forced") return url;

  const host = getHttpHost(url);
  if (!host || isLoopbackHost(host)) return url;

  const hasPlaceholder = relay.includes(URL_PLACEHOLDER);
  const relayPrefix = hasPlaceholder ? relay.slice(0, relay.indexOf(URL_PLACEHOLDER)) : relay;
  if (url.startsWith(relayPrefix)) return url;

  const encoded = encodeURIComponent(url);
  if (hasPlaceholder) {
    return relay.split(URL_PLACEHOLDER).join(encoded);
  }
  if (relay.endsWith("=")) {
    return `${relay}${encoded}`;
  }
  if (!relay.includes("?")) {
    return `${relay}?url=${encoded}`;
  }
  const joiner = relay.endsWith("?") || relay.endsWith("&") ? "" : "&";
  return `${relay}${joiner}url=${encoded}`;
};
