// 直连优先的智能分流(手机端)。
//
// 手机端的“代理”是用户在设置里填的中转地址(URL 前缀式,见 services/playbackProxy),不是 host:port 代理。
// 所以只有“强制代理 + 有效中转地址”时才有第二条路可走:
//  - forced:先直连(短超时),直连不通才改走中转;每个主机走通的路记在本机,下次直接走那条路
//  - system / direct:应用内没有代理可兜底,原样 fetch,行为与没有这个模块时完全一致(direct 绝不兜底)
// 记忆只存本机(AsyncStorage),不上传。

import { normalizePlaybackProxySettings, PlaybackProxySettings, resolvePlaybackUrl } from "./playbackProxy";

export type RouteKind = "direct" | "proxy";

export interface RouteMemoryEntry {
  route: RouteKind;
  /**
   * direct:最近一次直连走通的时间;
   * proxy:最近一次“试过直连、不通”的时间(中转走通不会刷新它,否则常用的源永远等不到重探)。
   */
  at: number;
}

type SettingsInput = { mode?: unknown; proxyUrl?: unknown } | null | undefined;
type FetchImpl = (url: string, init?: RequestInit) => Promise<Response>;

export interface SmartFetchOptions {
  /** 不传则向 configureSmartRoute 注册的提供者现取(设置 store) */
  settings?: SettingsInput;
  now?: () => number;
  fetchImpl?: FetchImpl;
  directTimeoutMs?: number;
}

export const ROUTE_MEMORY_STORAGE_KEY = "mytv_route_memory";
/** 直连尝试的短超时:到这个时间还没拿到响应就换中转,不等调用方自己的长超时 */
export const DIRECT_ATTEMPT_TIMEOUT_MS = 5000;
/** 记成“走中转”的主机,过这么久重新试一次直连;同步取播放地址时,“直连可用”的记忆也只在这么久内算数 */
export const REPROBE_AFTER_MS = 60 * 60 * 1000;
/** 起播前:记成“直连”的主机若这么久没验证过,先探一次(原生播放器自己不会兜底) */
export const PLAYBACK_DIRECT_FRESH_MS = 10 * 60 * 1000;
export const PLAYBACK_PRIME_DIRECT_TIMEOUT_MS = 3000;
export const PLAYBACK_PRIME_TOTAL_TIMEOUT_MS = 8000;
const MAX_ENTRIES = 500;
/** 同一条路反复走通时,隔这么久才再落一次盘 */
const PERSIST_REFRESH_MS = 5 * 60 * 1000;

let settingsProvider: () => SettingsInput = () => null;

/** 由设置 store 注册:smartFetch 每次请求现读当前代理设置。没注册时等同系统代理(不分流)。 */
export const configureSmartRoute = (provider: (() => SettingsInput) | null): void => {
  settingsProvider = provider ?? (() => null);
};

const memory = new Map<string, RouteMemoryEntry>();
let loadPromise: Promise<void> | null = null;
let loaded = false;

const getStorage = (): { getItem: (key: string) => Promise<string | null>; setItem: (key: string, value: string) => Promise<void> } | null => {
  try {
    // 懒加载:不分流(非强制代理)时完全不碰存储,也不要求测试环境提供 AsyncStorage
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const mod = require("@react-native-async-storage/async-storage");
    const storage = mod?.default ?? mod;
    return storage && typeof storage.getItem === "function" ? storage : null;
  } catch {
    return null;
  }
};

const isRouteKind = (value: unknown): value is RouteKind => value === "direct" || value === "proxy";

/** 从本机存储读入路由记忆(只读一次)。读不到或损坏按“没有记忆”处理。 */
export const loadRouteMemory = (): Promise<void> => {
  if (!loadPromise) {
    loadPromise = (async () => {
      try {
        const raw = await getStorage()?.getItem(ROUTE_MEMORY_STORAGE_KEY);
        const parsed = raw ? JSON.parse(raw) : null;
        if (parsed && typeof parsed === "object") {
          for (const [host, entry] of Object.entries(parsed as Record<string, any>)) {
            if (entry && isRouteKind(entry.route) && Number.isFinite(entry.at) && !memory.has(host)) {
              memory.set(host, { route: entry.route, at: entry.at });
            }
          }
        }
      } catch {
        // 记忆只是优化,读失败不影响请求
      }
      loaded = true;
    })();
  }
  return loadPromise;
};

const persist = (): void => {
  try {
    if (memory.size > MAX_ENTRIES) {
      const oldestFirst = [...memory.entries()].sort((a, b) => a[1].at - b[1].at);
      for (const [host] of oldestFirst.slice(0, memory.size - MAX_ENTRIES)) {
        memory.delete(host);
      }
    }
    const snapshot: Record<string, RouteMemoryEntry> = {};
    memory.forEach((entry, host) => {
      snapshot[host] = entry;
    });
    const pending = getStorage()?.setItem(ROUTE_MEMORY_STORAGE_KEY, JSON.stringify(snapshot));
    if (pending && typeof (pending as Promise<void>).catch === "function") {
      (pending as Promise<void>).catch(() => undefined);
    }
  } catch {
    // 落盘失败不影响请求
  }
};

const record = (host: string, route: RouteKind, at: number): void => {
  const previous = memory.get(host);
  memory.set(host, { route, at });
  if (!previous || previous.route !== route || at - previous.at > PERSIST_REFRESH_MS) {
    persist();
  }
};

/** 路由记忆的键:小写主机名,带显式端口;不是 http(s) 绝对地址时返回 null */
export const routeHostKey = (url: string): string | null => {
  const match = /^https?:\/\/([^/?#]*)/i.exec(typeof url === "string" ? url : "");
  if (!match) return null;
  let authority = match[1];
  const at = authority.lastIndexOf("@");
  if (at >= 0) authority = authority.slice(at + 1);
  authority = authority.toLowerCase();
  return authority ? authority : null;
};

export const getRememberedRoute = (url: string): RouteMemoryEntry | null => {
  const host = routeHostKey(url);
  const entry = host ? memory.get(host) : undefined;
  return entry ? { ...entry } : null;
};

interface RoutePlan {
  host: string;
  relayUrl: string;
  settings: PlaybackProxySettings;
}

/** 只有“强制代理 + 有效中转 + 可以被中转改写的 http(s) 地址”才分流;其余返回 null(原样请求) */
const planFor = (url: unknown, input: SettingsInput): RoutePlan | null => {
  if (typeof url !== "string" || !url) return null;
  const settings = normalizePlaybackProxySettings(input);
  if (settings.mode !== "forced") return null;
  const host = routeHostKey(url);
  if (!host) return null;
  // 本机回环、已经套过中转的地址:resolvePlaybackUrl 不改写,也就不分流
  const relayUrl = resolvePlaybackUrl(url, settings);
  if (relayUrl === url) return null;
  return { host, relayUrl, settings };
};

/** 被墙 / 地区限制常见的两种应答,按“直连不通”处理;其它状态(含 404、5xx)说明这条路是通的 */
const isBlockedStatus = (status: unknown): boolean => status === 403 || status === 451;

type Attempt =
  | { ok: true; response: Response }
  | { ok: false; response?: Response; error?: unknown; callerAborted: boolean };

const globalFetch: FetchImpl = (url, init) => (init === undefined ? fetch(url) : fetch(url, init));

/**
 * 直连优先的 fetch。签名与 fetch 相同(只接受字符串地址)。
 * 不分流时(非强制代理、本机地址、非 http(s)、已是中转地址)就是一次原样的 fetch。
 */
export const smartFetch = async (url: string, init?: RequestInit, options: SmartFetchOptions = {}): Promise<Response> => {
  const doFetch = options.fetchImpl ?? globalFetch;
  const plan = planFor(url, options.settings !== undefined ? options.settings : settingsProvider());
  const callerSignal = (init?.signal ?? undefined) as AbortSignal | undefined;
  if (!plan || callerSignal?.aborted) {
    return doFetch(url, init);
  }

  await loadRouteMemory();
  const now = options.now ?? Date.now;
  const directTimeoutMs = options.directTimeoutMs ?? DIRECT_ATTEMPT_TIMEOUT_MS;
  const { host, relayUrl } = plan;

  const attemptDirect = async (): Promise<Attempt> => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), directTimeoutMs);
    const onCallerAbort = () => controller.abort();
    // 响应回来之后仍保持联动:调用方之后 abort 还能中断正文读取
    callerSignal?.addEventListener?.("abort", onCallerAbort);
    try {
      const response = await doFetch(url, { ...(init ?? {}), signal: controller.signal });
      clearTimeout(timer);
      return isBlockedStatus(response?.status) ? { ok: false, response, callerAborted: false } : { ok: true, response };
    } catch (error) {
      clearTimeout(timer);
      callerSignal?.removeEventListener?.("abort", onCallerAbort);
      return { ok: false, error, callerAborted: !!callerSignal?.aborted };
    }
  };

  const attemptRelay = async (): Promise<Attempt> => {
    try {
      const response = await doFetch(relayUrl, init);
      return isBlockedStatus(response?.status) ? { ok: false, response, callerAborted: false } : { ok: true, response };
    } catch (error) {
      return { ok: false, error, callerAborted: !!callerSignal?.aborted };
    }
  };

  const giveBack = (attempt: Attempt): Response => {
    if (attempt.response) return attempt.response;
    throw (attempt as { error?: unknown }).error;
  };

  const entry = memory.get(host);
  const startedAt = now();

  if (entry?.route === "proxy" && startedAt - entry.at < REPROBE_AFTER_MS) {
    // 上次直连不通、走中转才通:这次先走中转,不再白等一次直连
    const relay = await attemptRelay();
    if (relay.ok || relay.callerAborted || relay.response) return giveBack(relay);
    // 中转本身连不上(抛错):再试一次直连,通了就改记直连
    const direct = await attemptDirect();
    if (direct.ok) {
      record(host, "direct", now());
      return direct.response;
    }
    if (direct.callerAborted) return giveBack(direct);
    if (direct.response) return direct.response;
    throw relay.error;
  }

  if (entry?.route === "proxy") {
    // 到了重探时间:由这一个请求认领重探(先把时间刷新,其余并发请求继续走中转)
    memory.set(host, { route: "proxy", at: startedAt });
  }

  const direct = await attemptDirect();
  if (direct.ok) {
    record(host, "direct", now());
    return direct.response;
  }
  // 调用方自己取消:原样抛出,不兜底、不改记忆
  if (direct.callerAborted) return giveBack(direct);

  const relay = await attemptRelay();
  if (relay.ok) {
    record(host, "proxy", now());
    return relay.response;
  }
  if (relay.callerAborted) {
    // 直连已确认不通,调用方在等中转时取消 / 到了自己的总时限:先记成走中转再抛出,
    // 否则总时限很短的请求每次都先耗在直连上,永远学不会
    if (memory.get(host)?.route !== "proxy") record(host, "proxy", now());
    return giveBack(relay);
  }
  // 两条路都不通:记忆不变,把最后的结果原样交回
  if (relay.response) return relay.response;
  if (direct.response) return direct.response;
  throw relay.error;
};

/**
 * 起播前给原生播放器定路:播放器自己联网,JS 没法在它失败时替它换路,所以先用一次小请求把这台主机的路探出来。
 * 该主机已有新鲜记忆时不发请求;探测失败(两条路都不通、超时)不抛错,记忆保持原样。
 */
export const primePlaybackRoute = async (
  url: string,
  settings: SettingsInput,
  options: Omit<SmartFetchOptions, "settings"> = {}
): Promise<void> => {
  try {
    const plan = planFor(url, settings);
    if (!plan) return;
    await loadRouteMemory();
    const entry = memory.get(plan.host);
    if (entry) {
      const age = (options.now ?? Date.now)() - entry.at;
      if (age < (entry.route === "proxy" ? REPROBE_AFTER_MS : PLAYBACK_DIRECT_FRESH_MS)) return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), PLAYBACK_PRIME_TOTAL_TIMEOUT_MS);
    try {
      // Range:地址是整部 mp4 时不把正文拉下来
      await smartFetch(
        url,
        { signal: controller.signal, headers: { Range: "bytes=0-2047" } },
        { ...options, settings, directTimeoutMs: options.directTimeoutMs ?? PLAYBACK_PRIME_DIRECT_TIMEOUT_MS }
      );
    } finally {
      clearTimeout(timer);
      controller.abort();
    }
  } catch {
    // 探测只是优化;失败时 resolveSmartPlaybackUrl 按“未知 → 中转”处理
  }
};

/**
 * 交给播放器的地址(同步,只看记忆):
 * 强制代理下,该主机最近记成“直连可用”→ 原地址;记成“走中转”或没有记忆 → 中转地址(与原来的强制代理一致)。
 * 其它模式与 resolvePlaybackUrl 相同(不改写)。
 */
export const resolveSmartPlaybackUrl = (url: string, settings: SettingsInput, now: () => number = Date.now): string => {
  const plan = planFor(url, settings);
  if (!plan) return resolvePlaybackUrl(url, settings);
  if (!loaded) void loadRouteMemory();
  const entry = memory.get(plan.host);
  return entry?.route === "direct" && now() - entry.at < REPROBE_AFTER_MS ? url : plan.relayUrl;
};

/** 仅测试用:清空内存里的记忆并允许重新从存储加载 */
export const __resetRouteMemoryForTests = (): void => {
  memory.clear();
  loadPromise = null;
  loaded = false;
};

/** 仅测试用:直接写入一条记忆 */
export const __setRouteMemoryForTests = (host: string, entry: RouteMemoryEntry): void => {
  memory.set(host, { ...entry });
};
