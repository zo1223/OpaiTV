import {
  DIRECT_ATTEMPT_TIMEOUT_MS,
  PLAYBACK_DIRECT_FRESH_MS,
  REPROBE_AFTER_MS,
  ROUTE_MEMORY_STORAGE_KEY,
  __resetRouteMemoryForTests,
  __setRouteMemoryForTests,
  configureSmartRoute,
  getRememberedRoute,
  loadRouteMemory,
  primePlaybackRoute,
  resolveSmartPlaybackUrl,
  routeHostKey,
  smartFetch,
} from "../smartRoute";

const mockStorage: Record<string, string> = {};

jest.mock("@react-native-async-storage/async-storage", () => ({
  __esModule: true,
  default: {
    getItem: jest.fn(async (key: string) => (key in mockStorage ? mockStorage[key] : null)),
    setItem: jest.fn(async (key: string, value: string) => {
      mockStorage[key] = value;
    }),
  },
}));

const RELAY = "https://relay.example/?url=";
const FORCED = { mode: "forced", proxyUrl: RELAY };
const API = "https://source.example/api.php/provide/vod?ac=detail&wd=x";
const HOST = "source.example";
const viaRelay = (url: string) => `${RELAY}${encodeURIComponent(url)}`;

const response = (status = 200) => ({ ok: status >= 200 && status < 300, status }) as unknown as Response;

/** 永不应答、只在 signal abort 时拒绝的 fetch(模拟连不上的直连) */
const hang = (init?: RequestInit) =>
  new Promise<Response>((_, reject) => {
    const signal = init?.signal as AbortSignal | undefined;
    const fail = () => reject(Object.assign(new Error("Aborted"), { name: "AbortError" }));
    if (signal?.aborted) fail();
    signal?.addEventListener("abort", fail);
  });

const savedMemory = () => JSON.parse(mockStorage[ROUTE_MEMORY_STORAGE_KEY] ?? "{}");

describe("smartRoute", () => {
  let now = 1_000_000;
  const clock = () => now;

  beforeEach(() => {
    for (const key of Object.keys(mockStorage)) delete mockStorage[key];
    __resetRouteMemoryForTests();
    configureSmartRoute(null);
    now = 1_000_000;
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it("keys memory by lower-cased host with explicit port", () => {
    expect(routeHostKey("https://User:pw@Source.Example:8443/a?b=1")).toBe("source.example:8443");
    expect(routeHostKey("http://source.example/a")).toBe("source.example");
    expect(routeHostKey("file:///a.mp4")).toBeNull();
  });

  it("goes direct first and remembers direct when it works", async () => {
    const fetchImpl = jest.fn(async () => response(200));
    const result = await smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });

    expect(result.status).toBe(200);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect((fetchImpl.mock.calls[0] as any[])[0]).toBe(API);
    expect(getRememberedRoute(API)).toEqual({ route: "direct", at: now });
    expect(savedMemory()[HOST]).toEqual({ route: "direct", at: now });
  });

  it("treats 404 / 5xx as a working route (no fallback)", async () => {
    const fetchImpl = jest.fn(async () => response(503));
    const result = await smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });

    expect(result.status).toBe(503);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(getRememberedRoute(API)?.route).toBe("direct");
  });

  it.each([
    ["network error", async () => Promise.reject(new TypeError("Network request failed"))],
    ["HTTP 403", async () => response(403)],
    ["HTTP 451", async () => response(451)],
  ])("falls back to the relay on %s and remembers proxy", async (_label, directResult) => {
    const fetchImpl = jest.fn((url: string) => (url === API ? directResult() : Promise.resolve(response(200))));
    const result = await smartFetch(API, { headers: { A: "1" } }, { settings: FORCED, fetchImpl, now: clock });

    expect(result.status).toBe(200);
    expect(fetchImpl.mock.calls.map((call) => call[0])).toEqual([API, viaRelay(API)]);
    expect((fetchImpl.mock.calls[1] as any[])[1]).toEqual({ headers: { A: "1" } });
    expect(getRememberedRoute(API)).toEqual({ route: "proxy", at: now });
    expect(savedMemory()[HOST].route).toBe("proxy");
  });

  it("gives up on a silent direct attempt after the short timeout instead of waiting for the caller's", async () => {
    jest.useFakeTimers();
    const fetchImpl = jest.fn((url: string, init?: RequestInit) =>
      url === API ? hang(init) : Promise.resolve(response(200))
    );
    const pending = smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });

    await jest.advanceTimersByTimeAsync(DIRECT_ATTEMPT_TIMEOUT_MS - 1);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    await jest.advanceTimersByTimeAsync(1);

    expect((await pending).status).toBe(200);
    expect(fetchImpl.mock.calls.map((call) => call[0])).toEqual([API, viaRelay(API)]);
    expect(getRememberedRoute(API)?.route).toBe("proxy");
  });

  it("goes straight to the relay for a host remembered as proxy, without touching the re-probe clock", async () => {
    __setRouteMemoryForTests(HOST, { route: "proxy", at: now });
    now += REPROBE_AFTER_MS - 1;
    const fetchImpl = jest.fn(async () => response(200));
    await smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });

    expect(fetchImpl.mock.calls.map((call: any[]) => call[0])).toEqual([viaRelay(API)]);
    expect(getRememberedRoute(API)).toEqual({ route: "proxy", at: 1_000_000 });
  });

  it("re-probes direct once the proxy memory is old, and only one concurrent request does the probing", async () => {
    __setRouteMemoryForTests(HOST, { route: "proxy", at: now });
    now += REPROBE_AFTER_MS;
    const fetchImpl = jest.fn(async () => response(200));
    await Promise.all([
      smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock }),
      smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock }),
    ]);

    const urls = fetchImpl.mock.calls.map((call: any[]) => call[0]).sort();
    expect(urls).toEqual([viaRelay(API), API].sort());
    expect(getRememberedRoute(API)).toEqual({ route: "direct", at: now });
  });

  it("keeps proxy (with a fresh clock) when the re-probe finds direct still down", async () => {
    __setRouteMemoryForTests(HOST, { route: "proxy", at: now });
    now += REPROBE_AFTER_MS + 5;
    const fetchImpl = jest.fn((url: string) =>
      url === API ? Promise.reject(new TypeError("Network request failed")) : Promise.resolve(response(200))
    );
    await smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });

    expect(fetchImpl.mock.calls.map((call) => call[0])).toEqual([API, viaRelay(API)]);
    expect(getRememberedRoute(API)).toEqual({ route: "proxy", at: now });
  });

  it("falls back to direct when the relay itself is unreachable", async () => {
    __setRouteMemoryForTests(HOST, { route: "proxy", at: now });
    const fetchImpl = jest.fn((url: string) =>
      url === API ? Promise.resolve(response(200)) : Promise.reject(new TypeError("relay down"))
    );
    const result = await smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });

    expect(result.status).toBe(200);
    expect(fetchImpl.mock.calls.map((call) => call[0])).toEqual([viaRelay(API), API]);
    expect(getRememberedRoute(API)?.route).toBe("direct");
  });

  it("leaves memory alone and reports the last failure when both routes fail", async () => {
    __setRouteMemoryForTests(HOST, { route: "direct", at: 5 });
    const fetchImpl = jest.fn((url: string) =>
      Promise.reject(new TypeError(url === API ? "direct down" : "relay down"))
    );

    await expect(smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock })).rejects.toThrow("relay down");
    expect(fetchImpl).toHaveBeenCalledTimes(2);
    expect(getRememberedRoute(API)).toEqual({ route: "direct", at: 5 });
  });

  it.each(["direct", "system"])("only fetches the original address once in %s mode", async (mode) => {
    const fetchImpl = jest.fn(async () => Promise.reject(new TypeError("Network request failed")));
    const init = { headers: { A: "1" } };

    await expect(
      smartFetch(API, init, { settings: { mode, proxyUrl: RELAY }, fetchImpl, now: clock })
    ).rejects.toThrow("Network request failed");
    expect(fetchImpl.mock.calls).toEqual([[API, init]]);
    expect(getRememberedRoute(API)).toBeNull();
    expect(mockStorage[ROUTE_MEMORY_STORAGE_KEY]).toBeUndefined();
  });

  it("does not split loopback, non-http or already relayed addresses", async () => {
    const fetchImpl = jest.fn(async () => Promise.reject(new TypeError("down")));
    for (const url of ["http://127.0.0.1:8080/a.m3u8", "file:///a.mp4", viaRelay(API)]) {
      await expect(smartFetch(url, undefined, { settings: FORCED, fetchImpl, now: clock })).rejects.toThrow("down");
    }
    expect(fetchImpl.mock.calls.map((call: any[]) => call[0])).toEqual([
      "http://127.0.0.1:8080/a.m3u8",
      "file:///a.mp4",
      viaRelay(API),
    ]);
  });

  it("does not fall back or touch memory when the caller aborts", async () => {
    const controller = new AbortController();
    const fetchImpl = jest.fn((_url: string, init?: RequestInit) => hang(init));
    const pending = smartFetch(API, { signal: controller.signal }, { settings: FORCED, fetchImpl, now: clock });
    // 等 smartFetch 读完记忆、发出直连请求
    await loadRouteMemory();
    await Promise.resolve();
    await Promise.resolve();
    controller.abort();

    await expect(pending).rejects.toMatchObject({ name: "AbortError" });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(getRememberedRoute(API)).toBeNull();
  });

  it("remembers proxy when direct has failed and the caller gives up while waiting for the relay", async () => {
    const controller = new AbortController();
    const fetchImpl = jest.fn((url: string, init?: RequestInit) =>
      url === API ? Promise.reject(new TypeError("Network request failed")) : hang(init)
    );
    const pending = smartFetch(API, { signal: controller.signal }, { settings: FORCED, fetchImpl, now: clock });
    await loadRouteMemory();
    for (let i = 0; i < 10; i += 1) await Promise.resolve();
    expect(fetchImpl).toHaveBeenCalledTimes(2);
    controller.abort();

    await expect(pending).rejects.toMatchObject({ name: "AbortError" });
    expect(getRememberedRoute(API)).toEqual({ route: "proxy", at: now });
  });

  it("uses the registered settings provider when no settings are passed", async () => {
    const realFetch = global.fetch;
    const globalFetch = jest.fn((url: string) =>
      url === API ? Promise.reject(new TypeError("down")) : Promise.resolve(response(200))
    );
    global.fetch = globalFetch as unknown as typeof fetch;
    try {
      // 没注册:不分流,只请求原地址
      await expect(smartFetch(API)).rejects.toThrow("down");
      expect(globalFetch.mock.calls).toEqual([[API]]);

      configureSmartRoute(() => FORCED);
      expect((await smartFetch(API)).status).toBe(200);
      expect(globalFetch.mock.calls.map((call) => call[0])).toEqual([API, API, viaRelay(API)]);
    } finally {
      global.fetch = realFetch;
    }
  });

  it("persists memory on this device and reloads it", async () => {
    const fetchImpl = jest.fn((url: string) =>
      url === API ? Promise.reject(new TypeError("down")) : Promise.resolve(response(200))
    );
    await smartFetch(API, undefined, { settings: FORCED, fetchImpl, now: clock });
    expect(savedMemory()).toEqual({ [HOST]: { route: "proxy", at: now } });

    __resetRouteMemoryForTests();
    expect(getRememberedRoute(API)).toBeNull();
    await loadRouteMemory();
    expect(getRememberedRoute(API)).toEqual({ route: "proxy", at: now });
  });

  it("ignores corrupt stored memory", async () => {
    mockStorage[ROUTE_MEMORY_STORAGE_KEY] = "{not json";
    await loadRouteMemory();
    expect(getRememberedRoute(API)).toBeNull();

    __resetRouteMemoryForTests();
    mockStorage[ROUTE_MEMORY_STORAGE_KEY] = JSON.stringify({ [HOST]: { route: "bogus", at: 1 }, "b.example": 3 });
    await loadRouteMemory();
    expect(getRememberedRoute(API)).toBeNull();
  });

  describe("playback", () => {
    const EPISODE = "https://cdn.example/v/1/index.m3u8";

    it("resolves by memory: direct → original, proxy or unknown → relay", () => {
      expect(resolveSmartPlaybackUrl(EPISODE, FORCED, clock)).toBe(viaRelay(EPISODE));

      __setRouteMemoryForTests("cdn.example", { route: "proxy", at: now });
      expect(resolveSmartPlaybackUrl(EPISODE, FORCED, clock)).toBe(viaRelay(EPISODE));

      __setRouteMemoryForTests("cdn.example", { route: "direct", at: now });
      expect(resolveSmartPlaybackUrl(EPISODE, FORCED, clock)).toBe(EPISODE);

      // 太久没验证过的“直连可用”不再算数
      now += REPROBE_AFTER_MS;
      expect(resolveSmartPlaybackUrl(EPISODE, FORCED, clock)).toBe(viaRelay(EPISODE));
    });

    it("never rewrites outside forced mode, whatever the memory says", () => {
      __setRouteMemoryForTests("cdn.example", { route: "proxy", at: now });
      expect(resolveSmartPlaybackUrl(EPISODE, { mode: "direct", proxyUrl: RELAY }, clock)).toBe(EPISODE);
      expect(resolveSmartPlaybackUrl(EPISODE, { mode: "system", proxyUrl: RELAY }, clock)).toBe(EPISODE);
      expect(resolveSmartPlaybackUrl(EPISODE, null, clock)).toBe(EPISODE);
      expect(resolveSmartPlaybackUrl("", FORCED, clock)).toBe("");
    });

    it("primes an unknown host with one small probe so the player gets the working route", async () => {
      const fetchImpl = jest.fn(async (_url: string, _init?: RequestInit) => response(206));
      await primePlaybackRoute(EPISODE, FORCED, { fetchImpl, now: clock });

      expect(fetchImpl).toHaveBeenCalledTimes(1);
      expect(fetchImpl.mock.calls[0][0]).toBe(EPISODE);
      expect((fetchImpl.mock.calls[0][1]?.headers as Record<string, string>).Range).toBe("bytes=0-2047");
      expect(resolveSmartPlaybackUrl(EPISODE, FORCED, clock)).toBe(EPISODE);
    });

    it("primes to proxy when direct is down, and swallows a total failure", async () => {
      const blocked = jest.fn((url: string) =>
        url === EPISODE ? Promise.reject(new TypeError("down")) : Promise.resolve(response(200))
      );
      await primePlaybackRoute(EPISODE, FORCED, { fetchImpl: blocked, now: clock });
      expect(getRememberedRoute(EPISODE)?.route).toBe("proxy");

      const dead = jest.fn(async () => Promise.reject(new TypeError("down")));
      await expect(primePlaybackRoute("https://dead.example/a.m3u8", FORCED, { fetchImpl: dead, now: clock })).resolves.toBeUndefined();
      expect(getRememberedRoute("https://dead.example/a.m3u8")).toBeNull();
    });

    it("skips the probe while memory is fresh, re-checks an old direct memory, and sends nothing outside forced mode", async () => {
      const fetchImpl = jest.fn(async () => response(200));

      __setRouteMemoryForTests("cdn.example", { route: "proxy", at: now });
      now += REPROBE_AFTER_MS - 1;
      await primePlaybackRoute(EPISODE, FORCED, { fetchImpl, now: clock });
      expect(fetchImpl).not.toHaveBeenCalled();

      __setRouteMemoryForTests("cdn.example", { route: "direct", at: now });
      now += PLAYBACK_DIRECT_FRESH_MS - 1;
      await primePlaybackRoute(EPISODE, FORCED, { fetchImpl, now: clock });
      expect(fetchImpl).not.toHaveBeenCalled();

      now += 1;
      await primePlaybackRoute(EPISODE, FORCED, { fetchImpl, now: clock });
      expect(fetchImpl).toHaveBeenCalledTimes(1);

      __resetRouteMemoryForTests();
      await primePlaybackRoute(EPISODE, { mode: "direct", proxyUrl: RELAY }, { fetchImpl, now: clock });
      await primePlaybackRoute(EPISODE, { mode: "system", proxyUrl: RELAY }, { fetchImpl, now: clock });
      expect(fetchImpl).toHaveBeenCalledTimes(1);
    });
  });
});
