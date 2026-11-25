// DLNA 投屏:AVTransport 的 SOAP 控制(推送地址、播放、暂停、跳转、停止、查询进度与状态)。

import { DlnaDevice, pickXmlText } from "./description";

const SOAP_TIMEOUT_MS = 4_000;
const DIDL_TITLE_FALLBACK = "OpaiTV";

export type DlnaControlTarget = Pick<DlnaDevice, "controlUrl" | "serviceType">;

export class DlnaError extends Error {
  action: string;
  status?: number;
  upnpCode?: string;

  constructor(message: string, action: string, status?: number, upnpCode?: string) {
    super(message);
    this.name = "DlnaError";
    this.action = action;
    this.status = status;
    this.upnpCode = upnpCode;
  }
}

export const escapeXml = (value: unknown): string =>
  String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&apos;");

export const buildSoapEnvelope = (serviceType: string, action: string, args: Record<string, string | number>): string =>
  `<?xml version="1.0" encoding="utf-8"?>` +
  `<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">` +
  `<s:Body><u:${action} xmlns:u="${escapeXml(serviceType)}">` +
  Object.entries(args)
    .map(([key, value]) => `<${key}>${escapeXml(value)}</${key}>`)
    .join("") +
  `</u:${action}></s:Body></s:Envelope>`;

export const buildDidlMetadata = (url: string, title?: string): string =>
  `<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">` +
  `<item id="0" parentID="-1" restricted="1">` +
  `<dc:title>${escapeXml(title || DIDL_TITLE_FALLBACK)}</dc:title>` +
  `<upnp:class>object.item.videoItem</upnp:class>` +
  `<res protocolInfo="http-get:*:*:*">${escapeXml(url)}</res>` +
  `</item></DIDL-Lite>`;

/** 毫秒 → "HH:MM:SS"(AVTransport 的 REL_TIME 格式)。 */
export const formatDlnaTime = (millis: number): string => {
  const totalSeconds = Math.max(0, Math.floor((Number.isFinite(millis) ? millis : 0) / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const pad = (value: number) => value.toString().padStart(2, "0");
  return `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
};

/** "H:MM:SS[.fff]" → 毫秒;NOT_IMPLEMENTED、空值或格式不对时返回 0。 */
export const parseDlnaTime = (value: string): number => {
  const match = (value || "").trim().match(/^(\d+):(\d{1,2}):(\d{1,2})(?:\.(\d+))?$/);
  if (!match) return 0;
  const fraction = match[4] ? Number(`0.${match[4]}`) : 0;
  return Math.round((Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]) + fraction) * 1000);
};

export interface SoapOptions {
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

/** 发一次 SOAP 动作,返回响应正文。非 2xx 或 SOAP Fault 抛 DlnaError。 */
export const soapRequest = async (
  target: DlnaControlTarget,
  action: string,
  args: Record<string, string | number>,
  options: SoapOptions = {}
): Promise<string> => {
  const fetchImpl: typeof fetch = options.fetchImpl ?? ((input, init) => fetch(input, init));
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), options.timeoutMs ?? SOAP_TIMEOUT_MS);

  let status = 0;
  let text = "";
  try {
    const response = await fetchImpl(target.controlUrl, {
      method: "POST",
      headers: {
        "Content-Type": 'text/xml; charset="utf-8"',
        SOAPACTION: `"${target.serviceType}#${action}"`,
      },
      body: buildSoapEnvelope(target.serviceType, action, args),
      signal: controller.signal,
    });
    status = response.status;
    text = await response.text();
  } catch (error) {
    const aborted = controller.signal.aborted;
    throw new DlnaError(aborted ? "投屏设备无响应" : `${action} 失败:无法连接投屏设备`, action);
  } finally {
    clearTimeout(timer);
  }

  const hasFault = /<(?:[\w.-]+:)?Fault[\s>]/i.test(text);
  if (status >= 400 || hasFault) {
    const code = pickXmlText("errorCode", text);
    const description = pickXmlText("errorDescription", text) || pickXmlText("faultstring", text);
    const reason = [description, code ? `错误码 ${code}` : ""].filter(Boolean).join(",") || `HTTP ${status}`;
    throw new DlnaError(`${action} 失败:${reason}`, action, status, code || undefined);
  }
  return text;
};

export const setAVTransportURI = (target: DlnaControlTarget, url: string, title?: string, options?: SoapOptions) =>
  soapRequest(
    target,
    "SetAVTransportURI",
    { InstanceID: 0, CurrentURI: url, CurrentURIMetaData: buildDidlMetadata(url, title) },
    options
  );

export const play = (target: DlnaControlTarget, options?: SoapOptions) =>
  soapRequest(target, "Play", { InstanceID: 0, Speed: 1 }, options);

export const pause = (target: DlnaControlTarget, options?: SoapOptions) =>
  soapRequest(target, "Pause", { InstanceID: 0 }, options);

export const stop = (target: DlnaControlTarget, options?: SoapOptions) =>
  soapRequest(target, "Stop", { InstanceID: 0 }, options);

export const seek = (target: DlnaControlTarget, positionMillis: number, options?: SoapOptions) =>
  soapRequest(target, "Seek", { InstanceID: 0, Unit: "REL_TIME", Target: formatDlnaTime(positionMillis) }, options);

export interface DlnaPositionInfo {
  positionMillis: number;
  durationMillis: number;
  trackUri: string;
}

export const getPositionInfo = async (target: DlnaControlTarget, options?: SoapOptions): Promise<DlnaPositionInfo> => {
  const text = await soapRequest(target, "GetPositionInfo", { InstanceID: 0 }, options);
  return {
    positionMillis: parseDlnaTime(pickXmlText("RelTime", text)),
    durationMillis: parseDlnaTime(pickXmlText("TrackDuration", text)),
    trackUri: pickXmlText("TrackURI", text),
  };
};

export interface DlnaTransportInfo {
  /** PLAYING / PAUSED_PLAYBACK / STOPPED / TRANSITIONING / NO_MEDIA_PRESENT … */
  state: string;
  status: string;
}

export const getTransportInfo = async (target: DlnaControlTarget, options?: SoapOptions): Promise<DlnaTransportInfo> => {
  const text = await soapRequest(target, "GetTransportInfo", { InstanceID: 0 }, options);
  return {
    state: pickXmlText("CurrentTransportState", text).toUpperCase(),
    status: pickXmlText("CurrentTransportStatus", text),
  };
};

/** 只有 http(s) 且不是本机回环的地址才能交给电视去取。 */
export const isCastableUrl = (url: string): boolean => {
  const match = (url || "").trim().match(/^https?:\/\/(\[[^\]]+\]|[^/:?#]+)/i);
  if (!match) return false;
  const host = match[1].toLowerCase();
  return !(host === "localhost" || host === "[::1]" || host === "0.0.0.0" || /^127\./.test(host));
};
