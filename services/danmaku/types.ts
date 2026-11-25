/**
 * 弹幕公共类型与设置。
 *
 * 桌面版(desktop/OrionTV.Desktop.Core/Danmaku*.cs)按同一套规则实现,改动匹配/排布规则时两边要一起改。
 */

export type DanmakuMode = "scroll" | "top" | "bottom";

export interface DanmakuComment {
  /** 出现时间(秒,相对视频开头) */
  time: number;
  text: string;
  /** 0xRRGGBB */
  color: number;
  mode: DanmakuMode;
}

/** 一集视频的弹幕来源。弹幕按时间分片,播放到哪里取到哪里。 */
export interface DanmakuTrack {
  provider: string;
  /** 匹配到的片名(仅用于日志) */
  title: string;
  /** 每个分片覆盖的秒数;0 表示整集只有一个分片 */
  segmentSeconds: number;
  loadSegment: (index: number) => Promise<DanmakuComment[]>;
}

export interface DanmakuQuery {
  /** 影片标题,如"庆余年 第二季" */
  title: string;
  /** 当前集在选集列表中的下标(从 0 开始) */
  episodeIndex: number;
  /** 当前集的集号(从 1 开始);未知时用 episodeIndex + 1 */
  episodeNumber: number;
  /** 该片总集数;电影为 1 */
  episodeCount: number;
  year?: string;
}

export interface DanmakuProvider {
  name: string;
  resolve: (query: DanmakuQuery) => Promise<DanmakuTrack | null>;
}

export type DanmakuSpeed = "slow" | "normal" | "fast";
export type DanmakuFontSize = "small" | "normal" | "large";

export interface DanmakuSettings {
  enabled: boolean;
  /** 自定义 danmu_api 服务器根地址;留空使用内置来源 */
  serverUrl: string;
  /** 0.2 ~ 1 */
  opacity: number;
  speed: DanmakuSpeed;
  fontSize: DanmakuFontSize;
}

export const DEFAULT_DANMAKU_SETTINGS: DanmakuSettings = {
  enabled: false,
  serverUrl: "",
  opacity: 0.8,
  speed: "normal",
  fontSize: "normal",
};

export const DANMAKU_OPACITY_OPTIONS = [0.4, 0.6, 0.8, 1] as const;
export const DANMAKU_SPEED_OPTIONS: readonly DanmakuSpeed[] = ["slow", "normal", "fast"];
export const DANMAKU_FONT_SIZE_OPTIONS: readonly DanmakuFontSize[] = ["small", "normal", "large"];

/** 滚动弹幕横穿画面所用的秒数 */
export const DANMAKU_SCROLL_SECONDS: Record<DanmakuSpeed, number> = { slow: 11, normal: 8, fast: 5.5 };
export const DANMAKU_FONT_SCALE: Record<DanmakuFontSize, number> = { small: 0.8, normal: 1, large: 1.3 };
/** 顶部/底部弹幕停留的秒数 */
export const DANMAKU_FIXED_SECONDS = 4;

export const DANMAKU_SPEED_LABELS: Record<DanmakuSpeed, string> = { slow: "慢", normal: "标准", fast: "快" };
export const DANMAKU_FONT_SIZE_LABELS: Record<DanmakuFontSize, string> = { small: "小", normal: "标准", large: "大" };

export function normalizeDanmakuOpacity(value: unknown): number {
  const n = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(n)) return DEFAULT_DANMAKU_SETTINGS.opacity;
  return Math.min(1, Math.max(0.2, n));
}

export function normalizeDanmakuSpeed(value: unknown): DanmakuSpeed {
  return DANMAKU_SPEED_OPTIONS.includes(value as DanmakuSpeed) ? (value as DanmakuSpeed) : "normal";
}

export function normalizeDanmakuFontSize(value: unknown): DanmakuFontSize {
  return DANMAKU_FONT_SIZE_OPTIONS.includes(value as DanmakuFontSize) ? (value as DanmakuFontSize) : "normal";
}

/**
 * 规范化自定义弹幕服务器地址:去掉首尾空白、末尾的 "/" 和 "/api/v2";
 * 没写协议时补 http://;不是 http(s) 地址时返回空串(等同于未配置)。
 */
export function normalizeDanmakuServerUrl(value: unknown): string {
  if (typeof value !== "string") return "";
  let url = value.trim();
  if (!url) return "";
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(url)) {
    url = `http://${url}`;
  }
  if (!/^https?:\/\/[^/\s]+/i.test(url)) return "";
  url = url.replace(/\/+$/, "");
  url = url.replace(/\/api\/v2$/i, "");
  return url.replace(/\/+$/, "");
}

/** 把存储里读出的(可能缺失或损坏的)字段整理成合法设置。只有明确保存过 true 才算开启。 */
export function normalizeDanmakuSettings(raw: {
  danmakuEnabled?: unknown;
  danmakuServerUrl?: unknown;
  danmakuOpacity?: unknown;
  danmakuSpeed?: unknown;
  danmakuFontSize?: unknown;
}): DanmakuSettings {
  return {
    enabled: raw.danmakuEnabled === true,
    serverUrl: normalizeDanmakuServerUrl(raw.danmakuServerUrl),
    opacity: raw.danmakuOpacity === undefined ? DEFAULT_DANMAKU_SETTINGS.opacity : normalizeDanmakuOpacity(raw.danmakuOpacity),
    speed: normalizeDanmakuSpeed(raw.danmakuSpeed),
    fontSize: normalizeDanmakuFontSize(raw.danmakuFontSize),
  };
}
