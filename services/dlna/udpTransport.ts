// DLNA 投屏:SSDP 用的真实 UDP 传输层。
// RN 没有内置 UDP,这里用 react-native-udp 4.x(dgram 风格的接口,已对着 4.1.7 的 src/UdpSocket.js 核对过签名)。
// 模块用 require 懒加载并包在 try/catch 里:原生模块没链接进来的安装包(Apple TV 构建、jest 环境)里
// createSocket 会抛错,此时返回 null,由上层给出明确提示而不是崩溃。

import { SsdpTransport } from "./ssdp";

interface UdpSocketLike {
  bind(port?: number, callback?: (error?: unknown) => void): void;
  send(
    message: string,
    offset: number | undefined,
    length: number | undefined,
    port: number,
    address: string,
    callback?: (error?: unknown) => void
  ): void;
  on(event: string, callback: (...args: any[]) => void): void;
  once?(event: string, callback: (...args: any[]) => void): void;
  close(): void;
}

interface DgramLike {
  createSocket(options: { type: "udp4"; reusePort?: boolean } | "udp4"): UdpSocketLike;
}

export const UDP_MODULE_MISSING_MESSAGE = "此版本未内置 UDP 模块,无法搜索投屏设备";

const loadDgram = (): DgramLike | null => {
  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const loaded = require("react-native-udp");
    const dgram = loaded?.default ?? loaded;
    return dgram && typeof dgram.createSocket === "function" ? (dgram as DgramLike) : null;
  } catch {
    return null;
  }
};

const decodeMessage = (message: unknown): string => {
  if (typeof message === "string") return message;
  if (message && typeof (message as { toString?: unknown }).toString === "function") {
    try {
      const text = (message as { toString(encoding?: string): string }).toString("utf8");
      if (text && text !== "[object Object]") return text;
    } catch {
      // 落到下面按字节解码。
    }
  }
  if (message && typeof (message as ArrayLike<number>).length === "number") {
    let text = "";
    const bytes = message as ArrayLike<number>;
    for (let i = 0; i < bytes.length; i++) text += String.fromCharCode(bytes[i]);
    return text;
  }
  return "";
};

const BIND_TIMEOUT_MS = 1_500;

/** 创建并绑定一个 UDP 套接字。原生模块不可用或绑定失败时返回 null。 */
export const createUdpTransport = async (): Promise<SsdpTransport | null> => {
  const dgram = loadDgram();
  if (!dgram) return null;

  let socket: UdpSocketLike;
  try {
    // 端口由系统分配,用不着 reusePort。原生模块没链接进来时(比如 Apple TV 构建)这里会抛错。
    socket = dgram.createSocket({ type: "udp4" });
  } catch {
    return null;
  }

  let listener: ((message: string) => void) | null = null;
  let closed = false;
  socket.on("message", (message: unknown) => {
    if (closed || !listener) return;
    const text = decodeMessage(message);
    if (text) listener(text);
  });

  const bound = await new Promise<boolean>((resolve) => {
    let settled = false;
    const settle = (ok: boolean) => {
      if (!settled) {
        settled = true;
        resolve(ok);
      }
    };
    const timer = setTimeout(() => settle(false), BIND_TIMEOUT_MS);
    socket.on("error", () => {
      clearTimeout(timer);
      settle(false);
    });
    try {
      // 端口 0:由系统分配;设备把 M-SEARCH 响应单播回这个端口。
      // react-native-udp 绑定失败时也会调这个回调(第一个参数是错误),不能无条件当成功。
      socket.bind(0, (error?: unknown) => {
        clearTimeout(timer);
        settle(!error);
      });
    } catch {
      clearTimeout(timer);
      settle(false);
    }
  });

  if (!bound) {
    try {
      socket.close();
    } catch {
      // 忽略。
    }
    return null;
  }

  return {
    send: (message, port, address) =>
      new Promise<void>((resolve, reject) => {
        if (closed) {
          resolve();
          return;
        }
        try {
          socket.send(message, undefined, undefined, port, address, (error) => (error ? reject(error) : resolve()));
        } catch (error) {
          reject(error);
        }
      }),
    onMessage: (callback) => {
      listener = callback;
    },
    close: () => {
      if (closed) return;
      closed = true;
      listener = null;
      try {
        socket.close();
      } catch {
        // 忽略。
      }
    },
  };
};
