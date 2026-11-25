// DLNA 投屏:SSDP 发现。
// 向 239.255.255.250:1900 发 M-SEARCH,收到带 LOCATION 的响应后取设备描述,
// 只保留带 AVTransport 服务(能接收播放地址)的设备。UDP 传输层由外部注入,便于用假设备测试。

import { DlnaDevice, fetchDeviceDescription } from "./description";

export const SSDP_ADDRESS = "239.255.255.250";
export const SSDP_PORT = 1900;
export const AV_TRANSPORT_SERVICE = "urn:schemas-upnp-org:service:AVTransport:1";
export const MEDIA_RENDERER_DEVICE = "urn:schemas-upnp-org:device:MediaRenderer:1";
export const SSDP_SEARCH_TARGETS = [AV_TRANSPORT_SERVICE, MEDIA_RENDERER_DEVICE];

export const DEFAULT_DISCOVERY_TIMEOUT_MS = 4_000;
const RESEND_DELAY_MS = 900;

/** UDP 传输层的最小接口。真实实现见 udpTransport.ts,测试里用假实现。 */
export interface SsdpTransport {
  send(message: string, port: number, address: string): void | Promise<void>;
  onMessage(callback: (message: string) => void): void;
  close(): void;
}

export interface SsdpResponse {
  location: string;
  usn: string;
  st: string;
}

export const buildMSearch = (searchTarget: string, mx = 2): string =>
  [
    "M-SEARCH * HTTP/1.1",
    `HOST: ${SSDP_ADDRESS}:${SSDP_PORT}`,
    'MAN: "ssdp:discover"',
    `MX: ${mx}`,
    `ST: ${searchTarget}`,
    "",
    "",
  ].join("\r\n");

/** 解析 M-SEARCH 响应(或 NOTIFY 通告)的头部;头名大小写不敏感。没有可用的 LOCATION 时返回 null。 */
export const parseSsdpResponse = (message: string): SsdpResponse | null => {
  if (typeof message !== "string" || !message) return null;
  const lines = message.split(/\r?\n/);
  const startLine = (lines[0] || "").trim();
  const isResponse = /^HTTP\/1\.[01]\s+200\b/i.test(startLine);
  const isNotify = /^NOTIFY\s+\*\s+HTTP\/1\.[01]$/i.test(startLine);
  if (!isResponse && !isNotify) return null;

  const headers: Record<string, string> = {};
  for (const line of lines.slice(1)) {
    const index = line.indexOf(":");
    if (index > 0) {
      headers[line.slice(0, index).trim().toUpperCase()] = line.slice(index + 1).trim();
    }
  }

  // 设备下线通告不是可用设备。
  if (isNotify && /byebye/i.test(headers.NTS || "")) return null;

  const location = headers.LOCATION || "";
  if (!/^https?:\/\//i.test(location)) return null;

  return { location, usn: headers.USN || "", st: headers.ST || headers.NT || "" };
};

export interface DiscoverOptions {
  transport: SsdpTransport;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  descriptionTimeoutMs?: number;
  /** 每发现一台设备回调一次,界面可以边搜边显示。 */
  onDevice?: (device: DlnaDevice) => void;
}

/**
 * 发现局域网内可投屏的设备。到 timeoutMs 为止返回已经发现的设备;
 * 坏响应、取不到描述、描述里没有 AVTransport 的设备都被忽略,不会让整次发现失败。
 */
export const discoverDevices = async (options: DiscoverOptions): Promise<DlnaDevice[]> => {
  const { transport, onDevice } = options;
  const fetchImpl: typeof fetch = options.fetchImpl ?? ((input, init) => fetch(input, init));
  const timeoutMs = options.timeoutMs ?? DEFAULT_DISCOVERY_TIMEOUT_MS;

  const devices: DlnaDevice[] = [];
  const seenLocations = new Set<string>();
  const seenIds = new Set<string>();
  let finished = false;
  // 发现结束时取消还没取完的设备描述请求。
  const descriptionAbort = new AbortController();

  const handleMessage = (message: string) => {
    if (finished) return;
    let response: SsdpResponse | null = null;
    try {
      response = parseSsdpResponse(message);
    } catch {
      response = null;
    }
    if (!response) return;

    // 同一台设备会对每个 ST 各回一次(USN 后缀不同),按 LOCATION 去重,取到描述后再按设备 id 去重。
    const locationKey = response.location.toLowerCase();
    if (seenLocations.has(locationKey)) return;
    seenLocations.add(locationKey);

    fetchDeviceDescription(response.location, {
      fetchImpl,
      timeoutMs: options.descriptionTimeoutMs,
      signal: descriptionAbort.signal,
    })
      .then((device) => {
        if (finished || !device || seenIds.has(device.id)) return;
        seenIds.add(device.id);
        devices.push(device);
        try {
          onDevice?.(device);
        } catch {
          // 回调出错不影响发现。
        }
      })
      .catch(() => {
        // 描述取失败:这次忽略;把 LOCATION 放回去,重发那一轮的响应到了还能再试一次。
        seenLocations.delete(locationKey);
      });
  };

  const sendProbes = () => {
    for (const target of SSDP_SEARCH_TARGETS) {
      try {
        const result = transport.send(buildMSearch(target), SSDP_PORT, SSDP_ADDRESS);
        if (result && typeof (result as Promise<void>).catch === "function") {
          (result as Promise<void>).catch(() => {});
        }
      } catch {
        // 单次发送失败不终止发现,等超时返回已有结果。
      }
    }
  };

  let resendTimer: ReturnType<typeof setTimeout> | null = null;
  let finishTimer: ReturnType<typeof setTimeout> | null = null;

  try {
    transport.onMessage(handleMessage);
    sendProbes();
    // UDP 会丢包,隔一会儿再发一轮。
    if (timeoutMs > RESEND_DELAY_MS * 2) {
      resendTimer = setTimeout(sendProbes, RESEND_DELAY_MS);
    }
    await new Promise<void>((resolve) => {
      finishTimer = setTimeout(resolve, timeoutMs);
    });
  } finally {
    finished = true;
    descriptionAbort.abort();
    if (resendTimer) clearTimeout(resendTimer);
    if (finishTimer) clearTimeout(finishTimer);
    try {
      transport.close();
    } catch {
      // 关闭失败无关紧要。
    }
  }

  return devices.slice();
};
