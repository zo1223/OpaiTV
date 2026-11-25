import useCastStore, { CAST_LOCAL_URL_MESSAGE, CAST_POLL_INTERVAL_MS, castInternals } from "../castStore";
import { AV_TRANSPORT_SERVICE, SsdpTransport } from "@/services/dlna/ssdp";
import { UDP_MODULE_MISSING_MESSAGE } from "@/services/dlna/udpTransport";
import { DlnaDevice } from "@/services/dlna/description";

jest.mock("@/utils/Logger", () => {
  const tagged = { info: jest.fn(), warn: jest.fn(), error: jest.fn(), debug: jest.fn() };
  return { __esModule: true, default: { withTag: () => tagged } };
});

const TV_LOCATION = "http://192.168.1.50:49152/description.xml";
const TV_CONTROL = "http://192.168.1.50:49152/upnp/control/AVTransport1";
const MEDIA_URL = "https://media.example/show/ep1/index.m3u8?a=1&b=2";

const DESCRIPTION = `<root><device><friendlyName>客厅电视</friendlyName><UDN>uuid:fake-tv-1</UDN><serviceList><service>
<serviceType>${AV_TRANSPORT_SERVICE}</serviceType><controlURL>/upnp/control/AVTransport1</controlURL></service></serviceList></device></root>`;

const device: DlnaDevice = {
  id: "uuid:fake-tv-1",
  name: "客厅电视",
  host: "192.168.1.50",
  location: TV_LOCATION,
  controlUrl: TV_CONTROL,
  serviceType: AV_TRANSPORT_SERVICE,
};

const pad = (value: number) => value.toString().padStart(2, "0");
const hms = (millis: number) => {
  const total = Math.floor(millis / 1000);
  return `${pad(Math.floor(total / 3600))}:${pad(Math.floor((total % 3600) / 60))}:${pad(total % 60)}`;
};

/** 一台假电视:应答设备描述与 AVTransport 的 SOAP 动作,并记录收到的动作。 */
const createFakeTv = () => {
  const tv = {
    actions: [] as string[],
    bodies: [] as string[],
    state: "STOPPED",
    positionMillis: 0,
    durationMillis: 2_700_000,
    uri: "",
    failActions: new Set<string>(),
    fetchImpl: (async (url: string, init?: RequestInit) => {
      const respond = (body: string, status = 200) =>
        ({ ok: status >= 200 && status < 300, status, text: async () => body }) as unknown as Response;
      if (url === TV_LOCATION) return respond(DESCRIPTION);
      if (url !== TV_CONTROL) return respond("", 404);

      const headers = (init?.headers ?? {}) as Record<string, string>;
      const action = /#(\w+)"$/.exec(headers.SOAPACTION)?.[1] ?? "";
      const body = String(init?.body ?? "");
      tv.actions.push(action);
      tv.bodies.push(body);
      if (tv.failActions.has(action)) {
        return respond(
          "<s:Envelope><s:Body><s:Fault><detail><UPnPError><errorCode>701</errorCode><errorDescription>Transition not available</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>",
          500
        );
      }

      let inner = "";
      if (action === "SetAVTransportURI") {
        tv.uri = /<CurrentURI>([\s\S]*?)<\/CurrentURI>/.exec(body)?.[1] ?? "";
        tv.state = "STOPPED";
        tv.positionMillis = 0;
      } else if (action === "Play") {
        tv.state = "PLAYING";
      } else if (action === "Pause") {
        tv.state = "PAUSED_PLAYBACK";
      } else if (action === "Stop") {
        tv.state = "STOPPED";
      } else if (action === "Seek") {
        const [h, m, s] = (/<Target>(\d+):(\d+):(\d+)<\/Target>/.exec(body) ?? []).slice(1).map(Number);
        tv.positionMillis = ((h * 60 + m) * 60 + s) * 1000;
      } else if (action === "GetPositionInfo") {
        inner = `<TrackDuration>${hms(tv.durationMillis)}</TrackDuration><RelTime>${hms(tv.positionMillis)}</RelTime>`;
      } else if (action === "GetTransportInfo") {
        inner = `<CurrentTransportState>${tv.state}</CurrentTransportState>`;
      }
      return respond(`<s:Envelope><s:Body><u:${action}Response>${inner}</u:${action}Response></s:Body></s:Envelope>`);
    }) as unknown as typeof fetch,
  };
  return tv;
};

const createFakeTransport = (replies: string[]) => {
  let listener: ((message: string) => void) | null = null;
  const transport: SsdpTransport & { closed: boolean; sent: string[] } = {
    closed: false,
    sent: [],
    send(message) {
      transport.sent.push(message);
      for (const reply of replies) Promise.resolve().then(() => listener?.(reply));
    },
    onMessage(callback) {
      listener = callback;
    },
    close() {
      transport.closed = true;
    },
  };
  return transport;
};

const SSDP_REPLY = ["HTTP/1.1 200 OK", `LOCATION: ${TV_LOCATION}`, `ST: ${AV_TRANSPORT_SERVICE}`, "USN: uuid:fake-tv-1", "", ""].join("\r\n");

const initialState = useCastStore.getState();
const originalInternals = { ...castInternals };

describe("castStore", () => {
  let tv: ReturnType<typeof createFakeTv>;

  beforeEach(() => {
    jest.useFakeTimers();
    tv = createFakeTv();
    castInternals.fetchImpl = tv.fetchImpl;
    castInternals.discoveryTimeoutMs = 200;
    useCastStore.setState(initialState, true);
  });

  afterEach(async () => {
    await useCastStore.getState().stop();
    Object.assign(castInternals, originalInternals);
    jest.clearAllTimers();
    jest.useRealTimers();
  });

  it("discovers the fake TV through the injected UDP transport", async () => {
    const transport = createFakeTransport([SSDP_REPLY]);
    castInternals.createTransport = async () => transport;

    const pending = useCastStore.getState().discover();
    await jest.advanceTimersByTimeAsync(20);
    // 超时之前设备已经显示出来了。
    expect(useCastStore.getState().discovering).toBe(true);
    expect(useCastStore.getState().devices.map((item) => item.name)).toEqual(["客厅电视"]);

    await jest.advanceTimersByTimeAsync(250);
    await pending;

    const state = useCastStore.getState();
    expect(state.discovering).toBe(false);
    expect(state.discoveryError).toBeNull();
    expect(state.devices).toEqual([device]);
    expect(transport.sent[0]).toContain("M-SEARCH * HTTP/1.1");
    expect(transport.closed).toBe(true);
  });

  it("reports a clear status when the UDP module is not built in", async () => {
    castInternals.createTransport = async () => null;
    await useCastStore.getState().discover();

    const state = useCastStore.getState();
    expect(state.discovering).toBe(false);
    expect(state.devices).toEqual([]);
    expect(state.discoveryError).toBe(UDP_MODULE_MISSING_MESSAGE);
  });

  it("finishes with an empty list when nothing answers", async () => {
    castInternals.createTransport = async () => createFakeTransport([]);
    const pending = useCastStore.getState().discover();
    await jest.advanceTimersByTimeAsync(250);
    await pending;

    expect(useCastStore.getState()).toMatchObject({ discovering: false, devices: [], discoveryError: null });
  });

  it("pushes the URL, plays, seeks to the local position and then syncs progress by polling", async () => {
    const ok = await useCastStore.getState().cast(device, { url: MEDIA_URL, title: "示例剧 第1集", positionMillis: 65_000 });

    expect(ok).toBe(true);
    expect(tv.actions).toEqual(["SetAVTransportURI", "Play", "Seek"]);
    expect(tv.uri).toBe("https://media.example/show/ep1/index.m3u8?a=1&amp;b=2");
    expect(tv.bodies[0]).toContain("示例剧 第1集");
    expect(tv.bodies[2]).toContain("<Target>00:01:05</Target>");
    expect(useCastStore.getState()).toMatchObject({ status: "casting", isPlaying: true, activeDevice: device });

    // 电视播到 01:35。
    tv.positionMillis = 95_000;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    expect(useCastStore.getState()).toMatchObject({
      positionMillis: 95_000,
      durationMillis: 2_700_000,
      transportState: "PLAYING",
      isPlaying: true,
    });

    // 有人用电视遥控器按了暂停:轮询把状态同步回来。
    tv.state = "PAUSED_PLAYBACK";
    tv.positionMillis = 96_000;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    expect(useCastStore.getState()).toMatchObject({ positionMillis: 96_000, isPlaying: false });
  });

  it("toggles pause / play and seeks on the TV", async () => {
    await useCastStore.getState().cast(device, { url: MEDIA_URL });
    expect(tv.actions).toEqual(["SetAVTransportURI", "Play"]);

    await useCastStore.getState().togglePlay();
    expect(tv.state).toBe("PAUSED_PLAYBACK");
    expect(useCastStore.getState().isPlaying).toBe(false);

    await useCastStore.getState().togglePlay();
    expect(tv.state).toBe("PLAYING");
    expect(useCastStore.getState().isPlaying).toBe(true);

    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    await useCastStore.getState().seekTo(600_000);
    expect(tv.positionMillis).toBe(600_000);
    expect(useCastStore.getState().positionMillis).toBe(600_000);

    await useCastStore.getState().seekBy(30_000);
    expect(tv.positionMillis).toBe(630_000);
    // 不会跳到片长之外。
    await useCastStore.getState().seekBy(99_000_000);
    expect(tv.positionMillis).toBe(2_700_000);
  });

  it("stops the TV, returns the last position and stops polling", async () => {
    await useCastStore.getState().cast(device, { url: MEDIA_URL });
    tv.positionMillis = 120_000;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);

    const lastPosition = await useCastStore.getState().stop();
    expect(lastPosition).toBe(120_000);
    expect(tv.state).toBe("STOPPED");
    expect(useCastStore.getState()).toMatchObject({ status: "idle", activeDevice: null, isPlaying: false });

    const count = tv.actions.length;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS * 5);
    expect(tv.actions).toHaveLength(count);
    expect(await useCastStore.getState().stop()).toBeNull();
  });

  it("falls back to Pause when the device rejects Stop", async () => {
    await useCastStore.getState().cast(device, { url: MEDIA_URL });
    tv.failActions.add("Stop");
    await useCastStore.getState().stop();
    expect(tv.actions.slice(-2)).toEqual(["Stop", "Pause"]);
    expect(useCastStore.getState().status).toBe("idle");
  });

  it("retries the initial seek once the TV reports PLAYING", async () => {
    tv.failActions.add("Seek");
    const ok = await useCastStore.getState().cast(device, { url: MEDIA_URL, positionMillis: 300_000 });
    expect(ok).toBe(true);
    expect(tv.actions).toEqual(["SetAVTransportURI", "Play", "Seek"]);

    tv.failActions.delete("Seek");
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    expect(tv.actions.slice(-3)).toEqual(["GetTransportInfo", "GetPositionInfo", "Seek"]);
    expect(tv.positionMillis).toBe(300_000);
    // 等跳转落实之前,界面保持目标进度,不被电视的旧进度(0)覆盖。
    expect(useCastStore.getState().positionMillis).toBe(300_000);
  });

  it("refuses local files and loopback addresses without contacting the TV", async () => {
    expect(await useCastStore.getState().cast(device, { url: "file:///storage/emulated/0/a.mp4" })).toBe(false);
    expect(await useCastStore.getState().cast(device, { url: "http://127.0.0.1:9978/a.m3u8" })).toBe(false);
    expect(tv.actions).toEqual([]);
    expect(useCastStore.getState()).toMatchObject({ status: "idle", error: CAST_LOCAL_URL_MESSAGE });
  });

  it("surfaces the device error and stays idle when the push fails", async () => {
    tv.failActions.add("SetAVTransportURI");
    const ok = await useCastStore.getState().cast(device, { url: MEDIA_URL });

    expect(ok).toBe(false);
    expect(tv.actions).toEqual(["SetAVTransportURI"]);
    const state = useCastStore.getState();
    expect(state.status).toBe("idle");
    expect(state.activeDevice).toBeNull();
    expect(state.error).toContain("Transition not available");
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS * 3);
    expect(tv.actions).toEqual(["SetAVTransportURI"]);
  });

  it("keeps the session and shows a hint when the TV stops answering", async () => {
    await useCastStore.getState().cast(device, { url: MEDIA_URL });
    tv.failActions.add("GetTransportInfo");
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS * 5);

    const state = useCastStore.getState();
    expect(state.status).toBe("casting");
    expect(state.error).toContain("无响应");
  });
});
