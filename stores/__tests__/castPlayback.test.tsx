import React from "react";
import renderer, { act, ReactTestInstance } from "react-test-renderer";
import useCastStore, { CAST_POLL_INTERVAL_MS, CAST_SEND_FAILED_MESSAGE, castInternals } from "../castStore";
import usePlayerStore from "@/stores/playerStore";
import useDetailStore from "@/stores/detailStore";
import { castCurrentVideo, stopCastAndResumeLocal, useCastLifecycle } from "@/hooks/useCastPlayback";
import { CastOverlay, castStateLabel } from "@/components/CastOverlay";
import { AV_TRANSPORT_SERVICE, SsdpTransport } from "@/services/dlna/ssdp";
import { DlnaDevice } from "@/services/dlna/description";

jest.mock("@/utils/Logger", () => {
  const tagged = { info: jest.fn(), warn: jest.fn(), error: jest.fn(), debug: jest.fn() };
  return { __esModule: true, default: { withTag: () => tagged } };
});
// 本机播放器与详情用轻量的假 store:只放投屏接线会读的字段。
jest.mock("@/stores/playerStore", () => {
  const { create } = jest.requireActual("zustand");
  const store = create(() => ({ episodes: [], currentEpisodeIndex: 0, status: null, videoRef: null }));
  return { __esModule: true, default: store };
});
jest.mock("@/stores/detailStore", () => {
  const { create } = jest.requireActual("zustand");
  const store = create(() => ({ detail: null }));
  return { __esModule: true, default: store };
});

const TV_CONTROL = "http://192.168.1.50:49152/upnp/control/AVTransport1";
const MEDIA_URL = "https://media.example/show/ep2/index.m3u8";
const NEXT_URL = "https://media.example/show/ep3/index.m3u8";

const device: DlnaDevice = {
  id: "uuid:fake-tv-1",
  name: "客厅电视",
  host: "192.168.1.50",
  location: "http://192.168.1.50:49152/description.xml",
  controlUrl: TV_CONTROL,
  serviceType: AV_TRANSPORT_SERVICE,
};

const pad = (value: number) => value.toString().padStart(2, "0");
const hms = (millis: number) => {
  const total = Math.floor(millis / 1000);
  return `${pad(Math.floor(total / 3600))}:${pad(Math.floor((total % 3600) / 60))}:${pad(total % 60)}`;
};

/** 假电视:记录收到的 SOAP 动作并维护播放状态;gates 里有的动作会等对应的 Promise 才应答。 */
const createFakeTv = () => {
  const tv = {
    actions: [] as string[],
    state: "STOPPED",
    positionMillis: 0,
    gates: {} as Record<string, Promise<void> | undefined>,
    fetchImpl: (async (_url: string, init?: RequestInit) => {
      const headers = (init?.headers ?? {}) as Record<string, string>;
      const action = /#(\w+)"$/.exec(headers.SOAPACTION)?.[1] ?? "";
      tv.actions.push(action);
      // 应答内容取"收到请求时"的状态,然后才等闸门:模拟应答在路上耽搁。
      let inner = "";
      if (action === "GetPositionInfo") {
        inner = `<TrackDuration>00:45:00</TrackDuration><RelTime>${hms(tv.positionMillis)}</RelTime>`;
      } else if (action === "GetTransportInfo") {
        inner = `<CurrentTransportState>${tv.state}</CurrentTransportState>`;
      } else if (action === "Play") {
        tv.state = "PLAYING";
      } else if (action === "Pause") {
        tv.state = "PAUSED_PLAYBACK";
      } else if (action === "Stop") {
        tv.state = "STOPPED";
      }
      if (tv.gates[action]) await tv.gates[action];
      const body = `<s:Envelope><s:Body><u:${action}Response>${inner}</u:${action}Response></s:Body></s:Envelope>`;
      return { ok: true, status: 200, text: async () => body } as unknown as Response;
    }) as unknown as typeof fetch,
  };
  return tv;
};

const createGate = () => {
  let open: () => void = () => {};
  const promise = new Promise<void>((resolve) => {
    open = resolve;
  });
  return { promise, open };
};

const textOf = (node: ReactTestInstance | string): string =>
  typeof node === "string" ? node : node.children.map((child) => textOf(child as ReactTestInstance | string)).join("");
const byTestId = (root: ReactTestInstance, testID: string) => root.findAllByProps({ testID })[0];

const setPlayer = (state: Record<string, unknown>) => (usePlayerStore as any).setState(state);
const initialCastState = useCastStore.getState();
const originalInternals = { ...castInternals };

describe("cast ↔ local player wiring", () => {
  let tv: ReturnType<typeof createFakeTv>;
  let video: { pauseAsync: jest.Mock; playAsync: jest.Mock; setPositionAsync: jest.Mock };

  beforeEach(() => {
    jest.useFakeTimers();
    tv = createFakeTv();
    castInternals.fetchImpl = tv.fetchImpl;
    castInternals.discoveryTimeoutMs = 200;
    useCastStore.setState(initialCastState, true);
    video = {
      pauseAsync: jest.fn(async () => undefined),
      playAsync: jest.fn(async () => undefined),
      setPositionAsync: jest.fn(async (_position: number) => undefined),
    };
    setPlayer({
      episodes: [
        { url: MEDIA_URL, title: "第2集" },
        { url: NEXT_URL, title: "第3集" },
      ],
      currentEpisodeIndex: 0,
      status: { isLoaded: true, positionMillis: 65_000 },
      videoRef: { current: video },
    });
    (useDetailStore as any).setState({ detail: { title: "示例剧" } });
  });

  afterEach(async () => {
    await useCastStore.getState().stop();
    Object.assign(castInternals, originalInternals);
    jest.clearAllTimers();
    jest.useRealTimers();
  });

  it("pushes the episode the local player is on (from its position) and pauses the local player", async () => {
    await expect(castCurrentVideo(device)).resolves.toBe(true);

    expect(tv.actions).toEqual(["SetAVTransportURI", "Play", "Seek"]);
    expect(useCastStore.getState()).toMatchObject({
      status: "casting",
      activeDevice: device,
      media: { url: MEDIA_URL, title: "示例剧 第2集", positionMillis: 65_000 },
    });
    expect(video.pauseAsync).toHaveBeenCalledTimes(1);
  });

  it("leaves the local player alone when the address cannot be cast", async () => {
    setPlayer({ episodes: [{ url: "file:///storage/emulated/0/Download/a.mp4", title: "本地" }] });
    await expect(castCurrentVideo(device)).resolves.toBe(false);

    expect(tv.actions).toEqual([]);
    expect(video.pauseAsync).not.toHaveBeenCalled();
    expect(useCastStore.getState().status).toBe("idle");
  });

  it("stops the TV and resumes the local player from the TV position", async () => {
    await castCurrentVideo(device);
    tv.positionMillis = 120_000;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);

    await stopCastAndResumeLocal();

    expect(tv.actions[tv.actions.length - 1]).toBe("Stop");
    expect(video.setPositionAsync).toHaveBeenCalledWith(120_000);
    expect(video.playAsync).toHaveBeenCalledTimes(1);
    expect(useCastStore.getState()).toMatchObject({ status: "idle", activeDevice: null });

    // 没有在投屏时再点一次:不动本机播放器。
    await stopCastAndResumeLocal();
    expect(video.playAsync).toHaveBeenCalledTimes(1);
  });

  it("keeps the last real position after the TV stopped on its own", async () => {
    await castCurrentVideo(device);
    tv.positionMillis = 120_000;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    // 有人在电视上按了退出:状态 STOPPED,进度归零。
    tv.state = "STOPPED";
    tv.positionMillis = 0;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);

    const state = useCastStore.getState();
    expect(state).toMatchObject({ status: "casting", transportState: "STOPPED", isPlaying: false, positionMillis: 120_000 });
    expect(castStateLabel(state.status, state.transportState, state.isPlaying)).toBe("电视端已停止");

    await stopCastAndResumeLocal();
    expect(video.setPositionAsync).toHaveBeenCalledWith(120_000);
  });

  it("ends the session when the play page unmounts, and when the local player switches episode", async () => {
    const Host = () => {
      useCastLifecycle();
      return null;
    };
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<Host />);
    });

    // 本机换集:电视上不再播旧的那一集。
    await castCurrentVideo(device);
    await act(async () => {
      setPlayer({ currentEpisodeIndex: 1 });
    });
    await jest.advanceTimersByTimeAsync(0);
    expect(tv.actions[tv.actions.length - 1]).toBe("Stop");
    expect(useCastStore.getState().status).toBe("idle");

    // 离开播放页:结束会话,之后不再轮询。
    await castCurrentVideo(device);
    useCastStore.getState().openModal();
    await act(async () => {
      tree.unmount();
    });
    await jest.advanceTimersByTimeAsync(0);
    expect(tv.actions[tv.actions.length - 1]).toBe("Stop");
    expect(useCastStore.getState()).toMatchObject({ status: "idle", modalVisible: false });
    const count = tv.actions.length;
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS * 3);
    expect(tv.actions).toHaveLength(count);
  });

  it("shows an entry while idle and the device, state and progress while casting", async () => {
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<CastOverlay />);
    });
    expect(textOf(byTestId(tree.root, "cast-entry"))).toBe("投屏");
    await act(async () => {
      byTestId(tree.root, "cast-entry").props.onPress();
    });
    expect(useCastStore.getState().modalVisible).toBe(true);

    await act(async () => {
      await castCurrentVideo(device);
      tv.positionMillis = 95_000;
      await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    });
    expect(tree.root.findAllByProps({ testID: "cast-entry" })).toHaveLength(0);
    expect(textOf(byTestId(tree.root, "cast-status-device"))).toBe("正在投屏到 客厅电视");
    expect(textOf(byTestId(tree.root, "cast-status-state"))).toBe("播放中 · 01:35 / 45:00");

    await act(async () => {
      await byTestId(tree.root, "cast-status-toggle").props.onPress();
    });
    expect(tv.state).toBe("PAUSED_PLAYBACK");
    expect(textOf(byTestId(tree.root, "cast-status-state"))).toContain("已暂停");

    await act(async () => {
      byTestId(tree.root, "cast-status-stop").props.onPress();
      await jest.advanceTimersByTimeAsync(0);
    });
    expect(useCastStore.getState().status).toBe("idle");
    expect(video.playAsync).toHaveBeenCalledTimes(1);
    expect(textOf(byTestId(tree.root, "cast-entry"))).toBe("投屏");
    await act(async () => {
      tree.unmount();
    });
  });

  it("does not let the TV start playing when the user stops while the address is still being pushed", async () => {
    const gate = createGate();
    tv.gates.SetAVTransportURI = gate.promise;

    const pending = useCastStore.getState().cast(device, { url: MEDIA_URL });
    await jest.advanceTimersByTimeAsync(0);
    expect(useCastStore.getState().status).toBe("connecting");
    await useCastStore.getState().stop();
    gate.open();

    await expect(pending).resolves.toBe(false);
    expect(tv.actions).toEqual(["SetAVTransportURI", "Stop"]);
    expect(useCastStore.getState().status).toBe("idle");
  });

  it("drops a poll result that was already on its way when the user paused", async () => {
    await useCastStore.getState().cast(device, { url: MEDIA_URL });
    const gate = createGate();
    tv.gates.GetPositionInfo = gate.promise;
    // 轮询发出时电视还在播;应答在路上。
    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    await useCastStore.getState().togglePlay();
    expect(useCastStore.getState().isPlaying).toBe(false);

    delete tv.gates.GetPositionInfo;
    gate.open();
    await jest.advanceTimersByTimeAsync(0);
    // 迟到的"PLAYING"不会把界面翻回播放中。
    expect(useCastStore.getState()).toMatchObject({ isPlaying: false, transportState: "PAUSED_PLAYBACK" });

    await jest.advanceTimersByTimeAsync(CAST_POLL_INTERVAL_MS);
    expect(useCastStore.getState()).toMatchObject({ isPlaying: false, transportState: "PAUSED_PLAYBACK" });
  });

  it("says so when no probe could be sent at all", async () => {
    const transport: SsdpTransport = {
      send: async () => {
        throw new Error("EHOSTUNREACH");
      },
      onMessage: () => {},
      close: jest.fn(),
    };
    castInternals.createTransport = async () => transport;

    const pending = useCastStore.getState().discover();
    await jest.advanceTimersByTimeAsync(250);
    await pending;

    expect(useCastStore.getState()).toMatchObject({ discovering: false, devices: [], discoveryError: CAST_SEND_FAILED_MESSAGE });
    expect(transport.close).toHaveBeenCalled();
  });
});
