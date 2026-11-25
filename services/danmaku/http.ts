/**
 * 弹幕取数用的最小 HTTP 封装:统一超时,失败一律抛错,由上层吞掉(弹幕失败不影响播放)。
 * 测试里整个替换成假实现。
 */

export interface DanmakuHttp {
  getJson: (url: string, headers?: Record<string, string>, timeoutMs?: number) => Promise<any>;
  postJson: (url: string, body: unknown, headers?: Record<string, string>, timeoutMs?: number) => Promise<any>;
  /** 取原始字节(B 站弹幕 XML 是 deflate 压缩的,部分平台不会自动解压) */
  getBytes: (url: string, headers?: Record<string, string>, timeoutMs?: number) => Promise<Uint8Array>;
  /**
   * 可选:发一个原始请求,拿回正文文本和响应里的 Set-Cookie(多条用 "; " 拼成一串,读不到时为空串)。
   * 优酷的弹幕接口要先从响应 Cookie 里拿令牌、再用表单 POST,上面三个方法做不到。
   * 没实现这个方法的 http(比如测试里的假实现)上,需要它的来源按"访问失败"处理。
   */
  sendRaw?: (
    url: string,
    init: { method: "GET" | "POST"; headers?: Record<string, string>; body?: string },
    timeoutMs?: number
  ) => Promise<DanmakuRawResponse>;
}

export interface DanmakuRawResponse {
  text: string;
  setCookie: string;
}

export const DANMAKU_USER_AGENT =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

const DEFAULT_TIMEOUT_MS = 12000;

async function request(url: string, init: RequestInit, timeoutMs: number): Promise<Response> {
  const controller = typeof AbortController !== "undefined" ? new AbortController() : null;
  const timer = setTimeout(() => controller?.abort(), timeoutMs);
  try {
    const response = await fetch(url, { ...init, signal: controller?.signal });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    return response;
  } finally {
    clearTimeout(timer);
  }
}

function getBytesWithXhr(url: string, headers: Record<string, string>, timeoutMs: number): Promise<Uint8Array> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("GET", url);
    xhr.responseType = "arraybuffer";
    xhr.timeout = timeoutMs;
    Object.keys(headers).forEach((key) => xhr.setRequestHeader(key, headers[key]));
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300 && xhr.response) {
        resolve(new Uint8Array(xhr.response as ArrayBuffer));
      } else {
        reject(new Error(`HTTP ${xhr.status}`));
      }
    };
    xhr.onerror = () => reject(new Error("network error"));
    xhr.ontimeout = () => reject(new Error("timeout"));
    xhr.send();
  });
}

export const defaultDanmakuHttp: DanmakuHttp = {
  async getJson(url, headers = {}, timeoutMs = DEFAULT_TIMEOUT_MS) {
    const response = await request(url, { method: "GET", headers }, timeoutMs);
    return response.json();
  },
  async postJson(url, body, headers = {}, timeoutMs = DEFAULT_TIMEOUT_MS) {
    const response = await request(
      url,
      { method: "POST", headers: { "Content-Type": "application/json", ...headers }, body: JSON.stringify(body) },
      timeoutMs
    );
    return response.json();
  },
  async getBytes(url, headers = {}, timeoutMs = DEFAULT_TIMEOUT_MS) {
    // React Native 的 fetch 不保证支持 arrayBuffer(),二进制统一走 XHR;Node(验证脚本)没有 XHR 时走 fetch
    if (typeof XMLHttpRequest !== "undefined") {
      return getBytesWithXhr(url, headers, timeoutMs);
    }
    const response = await request(url, { method: "GET", headers }, timeoutMs);
    return new Uint8Array(await response.arrayBuffer());
  },
  async sendRaw(url, init, timeoutMs = DEFAULT_TIMEOUT_MS) {
    const response = await request(url, { method: init.method, headers: init.headers || {}, body: init.body }, timeoutMs);
    const headers: any = response.headers;
    // Node/新环境有 getSetCookie();React Native 只能用 get("set-cookie")(多条会被拼成一串)
    const list: string[] | undefined = typeof headers?.getSetCookie === "function" ? headers.getSetCookie() : undefined;
    const setCookie = list && list.length > 0 ? list.join("; ") : String(headers?.get?.("set-cookie") || "");
    return { text: await response.text(), setCookie };
  },
};
