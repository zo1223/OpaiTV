// DLNA 投屏状态:发现设备 → 选设备推送并播放 → 播放/暂停、跳转、停止,期间轮询电视端进度。

import { create } from "zustand";
import { DlnaDevice } from "@/services/dlna/description";
import { discoverDevices, SsdpTransport } from "@/services/dlna/ssdp";
import { createUdpTransport, UDP_MODULE_MISSING_MESSAGE } from "@/services/dlna/udpTransport";
import * as avTransport from "@/services/dlna/avTransport";
import Logger from "@/utils/Logger";

const logger = Logger.withTag("CastStore");

export const CAST_POLL_INTERVAL_MS = 1_000;
/** 连续这么多次轮询失败后提示设备无响应(会话保留,用户可以自己停止)。 */
export const CAST_POLL_FAILURE_LIMIT = 5;
/** 起始进度小于这个值就不跳转,从头播。 */
const MIN_RESUME_POSITION_MS = 5_000;

export const CAST_LOCAL_URL_MESSAGE = "本地文件或本机地址无法投屏,电视取不到这个地址";
export const CAST_SEND_FAILED_MESSAGE =
  "搜索报文发不出去。请确认已连接 Wi-Fi 并允许本应用访问本地网络(iOS 还需要安装包带有多播授权)";

export interface CastMedia {
  url: string;
  title?: string;
  positionMillis?: number;
}

export type CastStatus = "idle" | "connecting" | "casting";

/** 可替换的外部依赖(测试里换成假 UDP 传输层与假 fetch)。 */
export const castInternals: {
  createTransport: () => Promise<SsdpTransport | null>;
  fetchImpl: typeof fetch;
  discoveryTimeoutMs?: number;
} = {
  createTransport: createUdpTransport,
  fetchImpl: (input, init) => fetch(input, init),
  discoveryTimeoutMs: undefined,
};

interface CastState {
  modalVisible: boolean;
  discovering: boolean;
  discoveryError: string | null;
  devices: DlnaDevice[];
  status: CastStatus;
  activeDevice: DlnaDevice | null;
  media: CastMedia | null;
  /** 电视端上报的状态:PLAYING / PAUSED_PLAYBACK / STOPPED / TRANSITIONING … */
  transportState: string | null;
  isPlaying: boolean;
  positionMillis: number;
  durationMillis: number;
  error: string | null;

  openModal: () => void;
  closeModal: () => void;
  discover: () => Promise<void>;
  cast: (device: DlnaDevice, media: CastMedia) => Promise<boolean>;
  togglePlay: () => Promise<void>;
  seekTo: (positionMillis: number) => Promise<void>;
  seekBy: (deltaMillis: number) => Promise<void>;
  /** 停止投屏,返回电视端最后的进度(毫秒),本机可以从这里接着播。没有在投屏时返回 null。 */
  stop: () => Promise<number | null>;
  _poll: () => Promise<void>;
}

let pollTimer: ReturnType<typeof setInterval> | null = null;
let pollInFlight = false;
let pollFailures = 0;
let sessionId = 0;
let discoverySeq = 0;
let pendingSeekMillis: number | null = null;
/** 每发一次控制命令加一;轮询开始后如果有新命令,这次轮询的结果就是旧的,丢掉。 */
let commandSeq = 0;

const clearPollTimer = () => {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
  pollInFlight = false;
  pollFailures = 0;
};

const errorMessage = (error: unknown, fallback: string) =>
  error instanceof Error && error.message ? error.message : fallback;

const soapOptions = () => ({ fetchImpl: castInternals.fetchImpl });

export const useCastStore = create<CastState>((set, get) => ({
  modalVisible: false,
  discovering: false,
  discoveryError: null,
  devices: [],
  status: "idle",
  activeDevice: null,
  media: null,
  transportState: null,
  isPlaying: false,
  positionMillis: 0,
  durationMillis: 0,
  error: null,

  openModal: () => set({ modalVisible: true }),
  closeModal: () => set({ modalVisible: false }),

  discover: async () => {
    if (get().discovering) return;
    const seq = ++discoverySeq;
    set({ discovering: true, discoveryError: null, devices: [] });

    let transport: SsdpTransport | null = null;
    try {
      transport = await castInternals.createTransport();
    } catch (error) {
      logger.info(`UDP transport failed: ${error}`);
      transport = null;
    }
    if (!transport) {
      if (seq === discoverySeq) {
        set({ discovering: false, discoveryError: UDP_MODULE_MISSING_MESSAGE });
      }
      return;
    }

    // 统计探测报文的发送结果:一条都没发出去(iOS 没有多播授权 / 没连 Wi-Fi)时给出明确提示,而不是"没找到设备"。
    let sendOk = 0;
    let sendFailed = 0;
    const rawTransport = transport;
    const countingTransport: SsdpTransport = {
      send: async (message, port, address) => {
        try {
          await rawTransport.send(message, port, address);
          sendOk += 1;
        } catch (error) {
          sendFailed += 1;
          throw error;
        }
      },
      onMessage: (callback) => rawTransport.onMessage(callback),
      close: () => rawTransport.close(),
    };

    try {
      const devices = await discoverDevices({
        transport: countingTransport,
        fetchImpl: castInternals.fetchImpl,
        timeoutMs: castInternals.discoveryTimeoutMs,
        onDevice: (device) => {
          if (seq !== discoverySeq) return;
          const current = get().devices;
          if (!current.some((item) => item.id === device.id)) {
            set({ devices: [...current, device] });
          }
        },
      });
      if (seq === discoverySeq) {
        const nothingSent = devices.length === 0 && sendOk === 0 && sendFailed > 0;
        set({ devices, discovering: false, discoveryError: nothingSent ? CAST_SEND_FAILED_MESSAGE : null });
      }
    } catch (error) {
      logger.info(`DLNA discovery failed: ${error}`);
      if (seq === discoverySeq) {
        set({ discovering: false, discoveryError: "搜索投屏设备失败,请确认手机已连接 Wi-Fi" });
      }
    }
  },

  cast: async (device, media) => {
    if (!avTransport.isCastableUrl(media.url)) {
      set({ error: CAST_LOCAL_URL_MESSAGE });
      return false;
    }

    clearPollTimer();
    const session = ++sessionId;
    pendingSeekMillis = null;
    set({
      status: "connecting",
      activeDevice: device,
      media,
      error: null,
      transportState: null,
      isPlaying: false,
      positionMillis: media.positionMillis ?? 0,
      durationMillis: 0,
    });

    try {
      await avTransport.setAVTransportURI(device, media.url, media.title, soapOptions());
      // 推地址期间用户点了停止(或重新选了设备):不要再发 Play,否则电视会在没有会话的情况下自己播起来。
      if (session !== sessionId) return false;
      await avTransport.play(device, soapOptions());
    } catch (error) {
      if (session === sessionId) {
        set({ status: "idle", activeDevice: null, media: null, error: errorMessage(error, "投屏失败") });
      }
      return false;
    }
    if (session !== sessionId) {
      // Play 在路上时被停止:停止命令可能先于 Play 到达电视,这里再补一次 Stop。
      if (get().status === "idle") {
        avTransport.stop(device, soapOptions()).catch(() => {});
      }
      return false;
    }

    const startAt = media.positionMillis ?? 0;
    if (startAt >= MIN_RESUME_POSITION_MS) {
      try {
        await avTransport.seek(device, startAt, soapOptions());
      } catch {
        // 不少电视在刚开始缓冲时拒绝跳转:记下来,等轮询看到 PLAYING 再跳一次。
        pendingSeekMillis = startAt;
      }
    }
    if (session !== sessionId) return false;

    set({ status: "casting", isPlaying: true, transportState: "TRANSITIONING" });
    pollTimer = setInterval(() => {
      get()._poll();
    }, CAST_POLL_INTERVAL_MS);
    return true;
  },

  _poll: async () => {
    const { status, activeDevice } = get();
    if (status !== "casting" || !activeDevice || pollInFlight) return;
    const session = sessionId;
    const commandsAtStart = commandSeq;
    pollInFlight = true;
    try {
      const transport = await avTransport.getTransportInfo(activeDevice, soapOptions());
      const position = await avTransport.getPositionInfo(activeDevice, soapOptions());
      if (session !== sessionId) return;
      pollFailures = 0;
      // 轮询期间用户发了暂停 / 播放 / 跳转:这份结果是命令之前的状态,丢掉,等下一轮。
      if (commandsAtStart !== commandSeq) return;

      const state = transport.state || null;
      const updates: Partial<CastState> = {
        transportState: state,
        isPlaying: state === "PLAYING" || state === "TRANSITIONING",
        error: null,
      };
      // 跳转还没落实之前,电视报的还是旧进度,不拿它覆盖界面上的目标进度。
      // 电视端停了(播完 / 有人按了退出)以后进度会报 0:保留最后一次有效进度,本机恢复播放时还用得上。
      const stoppedAtZero = (state === "STOPPED" || state === "NO_MEDIA_PRESENT") && position.positionMillis === 0;
      if (pendingSeekMillis === null && !stoppedAtZero) {
        updates.positionMillis = position.positionMillis;
      }
      if (position.durationMillis > 0) {
        updates.durationMillis = position.durationMillis;
      }
      set(updates);

      if (pendingSeekMillis !== null && state === "PLAYING") {
        const target = pendingSeekMillis;
        pendingSeekMillis = null;
        try {
          await avTransport.seek(activeDevice, target, soapOptions());
        } catch (error) {
          logger.info(`Deferred seek failed: ${error}`);
        }
      }
    } catch (error) {
      if (session !== sessionId) return;
      pollFailures += 1;
      if (pollFailures >= CAST_POLL_FAILURE_LIMIT) {
        set({ error: "投屏设备无响应,进度可能不是最新的" });
      }
    } finally {
      if (session === sessionId) {
        pollInFlight = false;
      }
    }
  },

  togglePlay: async () => {
    const { status, activeDevice, isPlaying } = get();
    if (status !== "casting" || !activeDevice) return;
    commandSeq += 1;
    try {
      if (isPlaying) {
        await avTransport.pause(activeDevice, soapOptions());
        set({ isPlaying: false, transportState: "PAUSED_PLAYBACK", error: null });
      } else {
        await avTransport.play(activeDevice, soapOptions());
        set({ isPlaying: true, transportState: "PLAYING", error: null });
      }
    } catch (error) {
      set({ error: errorMessage(error, "操作失败") });
    }
  },

  seekTo: async (positionMillis) => {
    const { status, activeDevice, durationMillis } = get();
    if (status !== "casting" || !activeDevice) return;
    const upper = durationMillis > 0 ? durationMillis : Number.MAX_SAFE_INTEGER;
    const target = Math.max(0, Math.min(Math.floor(positionMillis), upper));
    commandSeq += 1;
    try {
      pendingSeekMillis = null;
      await avTransport.seek(activeDevice, target, soapOptions());
      set({ positionMillis: target, error: null });
    } catch (error) {
      set({ error: errorMessage(error, "跳转失败") });
    }
  },

  seekBy: async (deltaMillis) => {
    await get().seekTo(get().positionMillis + deltaMillis);
  },

  stop: async () => {
    const { status, activeDevice, positionMillis } = get();
    if (status === "idle" || !activeDevice) return null;

    clearPollTimer();
    sessionId += 1;
    pendingSeekMillis = null;
    set({
      status: "idle",
      activeDevice: null,
      media: null,
      transportState: null,
      isPlaying: false,
      error: null,
    });

    try {
      await avTransport.stop(activeDevice, soapOptions());
    } catch {
      // 有的设备不支持 Stop:退而求其次暂停(CineFlow 的做法)。
      try {
        await avTransport.pause(activeDevice, soapOptions());
      } catch (error) {
        logger.info(`Stop cast failed: ${error}`);
      }
    }
    return positionMillis;
  },
}));

export default useCastStore;
