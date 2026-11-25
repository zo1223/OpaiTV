import React from "react";
import renderer, { act } from "react-test-renderer";
import { DanmakuOverlay } from "../DanmakuOverlay";
import { useDanmakuStore } from "@/stores/danmakuStore";
import usePlayerStore from "@/stores/playerStore";
import { danmakuService } from "@/services/danmaku";
import { DEFAULT_DANMAKU_SETTINGS } from "@/services/danmaku/types";
import Toast from "react-native-toast-message";
import { DanmakuToggle } from "../DanmakuToggle";

jest.mock("@/stores/playerStore", () => {
  const { create } = jest.requireActual("zustand");
  return {
    __esModule: true,
    default: create(() => ({
      currentEpisodeIndex: 1,
      episodes: [
        { title: "第1集", url: "a" },
        { title: "第2集", url: "b" },
      ],
      status: null,
    })),
  };
});
jest.mock("@/stores/detailStore", () => {
  const { create } = jest.requireActual("zustand");
  return { __esModule: true, default: create(() => ({ detail: { title: "繁花", year: "2023" } })) };
});
jest.mock("@/services/storage", () => ({
  SettingsManager: { get: jest.fn(async () => ({})), save: jest.fn(async () => undefined) },
}));
jest.mock("@/services/danmaku", () => ({
  ...jest.requireActual("@/services/danmaku"),
  danmakuService: {
    resolve: jest.fn(),
    // 组件用的是 resolveDetailed;测试里仍只控制 resolve 这一个假函数
    resolveDetailed(this: any, ...args: any[]) {
      return Promise.resolve().then(() => this.resolve(...args)).then((track: any) =>
        track && track.outcome ? track : { track: track ?? null, outcome: track ? "found" : "notFound" }
      );
    },
  },
}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));

const resolve = danmakuService.resolve as jest.Mock;
const setStatus = (status: any) => (usePlayerStore as any).setState({ status });
const playing = (positionMillis: number, isPlaying = true) => ({ isLoaded: true, isPlaying, positionMillis, rate: 1 });

const textsOf = (tree: renderer.ReactTestRenderer) =>
  tree.root
    .findAll((node) => (node.type as any) === "Text")
    .map((node) => node.props.children)
    .sort();

const layout = (tree: renderer.ReactTestRenderer) =>
  act(() => {
    tree.root
      .findAll((node) => node.props.testID === "danmaku-layer" && typeof node.props.onLayout === "function")[0]
      .props.onLayout({ nativeEvent: { layout: { width: 1280, height: 720 } } });
  });

describe("DanmakuOverlay", () => {
  beforeEach(() => {
    jest.useFakeTimers({ now: 1_000_000 });
    resolve.mockReset();
    setStatus(null);
    useDanmakuStore.setState({ settings: DEFAULT_DANMAKU_SETTINGS, isLoaded: true });
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it("开关关闭(默认)时不渲染、不发请求", async () => {
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<DanmakuOverlay />);
    });
    expect(tree.toJSON()).toBeNull();
    expect(resolve).not.toHaveBeenCalled();
    act(() => tree.unmount());
  });

  it("没有匹配到或匹配出错时是空的透明层,不抛错", async () => {
    useDanmakuStore.setState({ settings: { ...DEFAULT_DANMAKU_SETTINGS, enabled: true } });
    for (const outcome of [() => Promise.resolve(null), () => Promise.reject(new Error("boom"))]) {
      resolve.mockImplementation(outcome);
      let tree!: renderer.ReactTestRenderer;
      await act(async () => {
        tree = renderer.create(<DanmakuOverlay />);
      });
      layout(tree);
      await act(async () => {
        setStatus(playing(5000));
        jest.advanceTimersByTime(1000);
      });
      expect(textsOf(tree)).toEqual([]);
      const json = tree.toJSON() as renderer.ReactTestRendererJSON;
      expect(json.props.pointerEvents).toBe("none");
      act(() => tree.unmount());
      act(() => setStatus(null));
    }
  });

  it("按片名和集数匹配,弹幕跟着播放进度出现,暂停时停住,拖动后换成新位置的弹幕", async () => {
    useDanmakuStore.setState({ settings: { ...DEFAULT_DANMAKU_SETTINGS, enabled: true, opacity: 0.6 } });
    const loadSegment = jest.fn(async () => [
      { time: 5, text: "五秒", color: 0xffffff, mode: "scroll" },
      { time: 6, text: "六秒顶部", color: 0xff0000, mode: "top" },
      { time: 100, text: "一百秒", color: 0xffffff, mode: "scroll" },
    ]);
    resolve.mockResolvedValue({ provider: "test", title: "", segmentSeconds: 0, loadSegment });

    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<DanmakuOverlay />);
    });
    expect(resolve).toHaveBeenCalledWith(
      { title: "繁花", year: "2023", episodeIndex: 1, episodeNumber: 2, episodeCount: 2 },
      ""
    );
    layout(tree);
    expect((tree.toJSON() as renderer.ReactTestRendererJSON).props.style).toEqual(
      expect.arrayContaining([expect.objectContaining({ opacity: 0.6 })])
    );

    // 视频还没加载:什么都不显示
    await act(async () => {
      jest.advanceTimersByTime(500);
    });
    expect(textsOf(tree)).toEqual([]);

    // 从 4.8 秒开始播:很快 5 秒那条出现,6 秒的顶部弹幕还没到
    await act(async () => {
      setStatus(playing(4800));
    });
    await act(async () => {
      jest.advanceTimersByTime(500);
    });
    expect(textsOf(tree)).toEqual(["五秒"]);

    await act(async () => {
      setStatus(playing(5300));
      jest.advanceTimersByTime(500);
      setStatus(playing(5800));
      jest.advanceTimersByTime(500);
    });
    expect(textsOf(tree)).toEqual(["五秒", "六秒顶部"]);

    // 暂停很久:画面上的弹幕不变(顶部弹幕本来 4 秒后就该消失)
    await act(async () => {
      setStatus(playing(6300, false));
      jest.advanceTimersByTime(30000);
    });
    expect(textsOf(tree)).toEqual(["五秒", "六秒顶部"]);

    // 拖到 101 秒:换成那附近的弹幕
    await act(async () => {
      setStatus(playing(101000, false));
      jest.advanceTimersByTime(300);
    });
    expect(textsOf(tree)).toEqual(["一百秒"]);

    // 拖回 50 秒:附近没有弹幕
    await act(async () => {
      setStatus(playing(50000, false));
      jest.advanceTimersByTime(300);
    });
    expect(textsOf(tree)).toEqual([]);

    // 换集、换源期间(视频未加载)清空
    await act(async () => {
      setStatus(playing(101000));
      jest.advanceTimersByTime(300);
    });
    expect(textsOf(tree)).toEqual(["一百秒"]);
    await act(async () => {
      setStatus({ isLoaded: false });
      jest.advanceTimersByTime(300);
    });
    expect(textsOf(tree)).toEqual([]);
    act(() => tree.unmount());
  });

  it("匹配不到时提示“未找到该集弹幕”,来源访问失败时提示获取失败,不再静默", async () => {
    const show = Toast.show as jest.Mock;
    show.mockClear();
    useDanmakuStore.setState({ settings: { ...DEFAULT_DANMAKU_SETTINGS, enabled: true }, isLoaded: true });
    resolve.mockResolvedValue(null);
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(<DanmakuOverlay />);
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(show).toHaveBeenCalledWith(expect.objectContaining({ text1: "未找到该集弹幕" }));
    act(() => tree.unmount());

    show.mockClear();
    resolve.mockResolvedValue({ track: null, outcome: "failed" });
    await act(async () => {
      tree = renderer.create(<DanmakuOverlay />);
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(show).toHaveBeenCalledWith(expect.objectContaining({ text1: "弹幕获取失败，请检查网络后重试" }));
    act(() => tree.unmount());
  });

  it("播放页弹幕开关:与设置同步,关掉立即卸载弹幕层,打开立即重新匹配", async () => {
    const show = Toast.show as jest.Mock;
    show.mockClear();
    resolve.mockResolvedValue({ provider: "test", title: "", segmentSeconds: 0, loadSegment: async () => [] });
    let tree!: renderer.ReactTestRenderer;
    await act(async () => {
      tree = renderer.create(
        <>
          <DanmakuOverlay />
          <DanmakuToggle />
        </>
      );
    });
    const toggle = () => tree.root.findAll((node) => node.props?.testID === "danmaku-toggle" && node.props.onPress)[0];
    const layers = () => tree.root.findAll((node) => node.props?.testID === "danmaku-layer");
    expect(toggle().props.accessibilityState).toEqual({ checked: false });
    expect(layers()).toHaveLength(0);
    expect(resolve).not.toHaveBeenCalled();

    await act(async () => {
      toggle().props.onPress();
    });
    expect(useDanmakuStore.getState().settings.enabled).toBe(true);
    expect(toggle().props.accessibilityState).toEqual({ checked: true });
    expect(layers().length).toBeGreaterThan(0);
    expect(resolve).toHaveBeenCalledTimes(1);
    expect(resolve.mock.calls[0][0]).toMatchObject({ title: "繁花", episodeIndex: 1, episodeNumber: 2 });
    expect(show).toHaveBeenLastCalledWith(expect.objectContaining({ text1: "弹幕已开启" }));

    await act(async () => {
      toggle().props.onPress();
    });
    expect(useDanmakuStore.getState().settings.enabled).toBe(false);
    expect(layers()).toHaveLength(0);
    expect(show).toHaveBeenLastCalledWith(expect.objectContaining({ text1: "弹幕已关闭" }));

    // 设置页改了开关,播放页按钮跟着变
    await act(async () => {
      await useDanmakuStore.getState().update({ enabled: true });
    });
    expect(toggle().props.accessibilityState).toEqual({ checked: true });
    expect(resolve).toHaveBeenCalledTimes(2);
    act(() => tree.unmount());
  });
});
