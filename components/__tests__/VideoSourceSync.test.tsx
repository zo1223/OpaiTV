import React from "react";
import { Clipboard, Platform, Share, TextInput } from "react-native";
import renderer, { act } from "react-test-renderer";
import { AddVideoSourceSection } from "../settings/AddVideoSourceSection";
import { StyledButton } from "../StyledButton";
import { useSettingsStore } from "@/stores/settingsStore";
import { buildVideoSourceSyncQrPayloads, encodeVideoSourceSyncLink } from "@/services/videoSourceSync";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(async () => null),
  setItem: jest.fn(async () => undefined),
  removeItem: jest.fn(async () => undefined),
}));

jest.mock("@/hooks/useSafeTVEventHandler", () => ({
  useSafeTVEventHandler: jest.fn(),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "mobile", columns: 3, cardWidth: 100, cardHeight: 150, spacing: 12 }),
}));

jest.mock("react-native-qrcode-svg", () => {
  const { View } = jest.requireActual("react-native");
  const MockQRCode = (props: object) => <View testID="qr" {...props} />;
  return { __esModule: true, default: MockQRCode };
});

const mockRequestCameraPermissions = jest.fn();
jest.mock("expo-camera", () => {
  const { View } = jest.requireActual("react-native");
  return {
    CameraView: (props: object) => <View testID="camera" {...props} />,
    Camera: { requestCameraPermissionsAsync: () => mockRequestCameraPermissions() },
  };
});

const sources = [
  { key: "demo", name: "Demo", api: "https://demo.example/api.php/provide/vod" },
  { key: "custom_my.example", name: "my.example", api: "https://my.example/api.php/provide/vod" },
  { key: "adult", name: "成人源", api: "https://adult.example/api.php/provide/vod", adult: true },
];

const manySources = Array.from({ length: 120 }, (_, index) => ({
  key: `source_${index}`,
  name: `采集资源站 ${index}`,
  api: `https://cj${(index * 2654435761) % 1000003}.site-${index}.example/api.php/provide/vod`,
}));

describe("syncing video sources from the settings page", () => {
  const originalIsTV = Platform.isTV;
  const setIsTV = (isTV: boolean) => Object.defineProperty(Platform, "isTV", { configurable: true, value: isTV });

  const mounted: renderer.ReactTestRenderer[] = [];

  const mount = () => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<AddVideoSourceSection />);
    });
    mounted.push(tree!);
    return tree!;
  };

  const button = (tree: renderer.ReactTestRenderer, text: string) =>
    tree.root.findAllByType(StyledButton).find((item) => item.props.text === text);

  const press = async (tree: renderer.ReactTestRenderer, text: string) => {
    await act(async () => {
      await button(tree, text)!.props.onPress();
    });
  };

  const texts = (tree: renderer.ReactTestRenderer) => JSON.stringify(tree.toJSON());

  const storedKeys = () => useSettingsStore.getState().subscribedVideoSources.map((source) => source.key);

  beforeEach(() => {
    jest.clearAllMocks();
    setIsTV(false);
    mockRequestCameraPermissions.mockResolvedValue({ granted: true });
    useSettingsStore.setState({ subscribedVideoSources: [], showAdultContent: false });
  });

  afterEach(() => {
    act(() => {
      mounted.splice(0).forEach((tree) => tree.unmount());
    });
    jest.useRealTimers();
    setIsTV(originalIsTV);
  });

  describe("export", () => {
    it("shows every source as one link and the same link as a QR code", async () => {
      useSettingsStore.setState({ subscribedVideoSources: sources });
      const tree = mount();
      expect(tree.root.findAllByProps({ testID: "qr" })).toHaveLength(0);

      await press(tree, "同步视频源(导出)");

      const link = encodeVideoSourceSyncLink(sources);
      const qr = tree.root.findByProps({ testID: "qr" });
      // 二维码里就是这段链接;纠错等级用 L,码更稀疏、更容易扫
      expect(qr.props.value).toBe(link);
      expect(qr.props.ecl).toBe("L");
      expect(qr.props.quietZone).toBeGreaterThan(0);
      expect(texts(tree)).toContain(link);
      // 成人内容开关关着也导出成人源(带标记),界面只说明数量
      expect(texts(tree)).toContain("共 3 个视频源");
      expect(texts(tree)).toContain("含 1 个成人源");
    });

    it("copies and shares the link", async () => {
      const setString = jest.spyOn(Clipboard, "setString").mockImplementation(() => undefined);
      const share = jest.spyOn(Share, "share").mockResolvedValue({ action: "sharedAction" } as any);
      useSettingsStore.setState({ subscribedVideoSources: sources });
      const tree = mount();
      await press(tree, "同步视频源(导出)");

      await press(tree, "复制链接");
      await press(tree, "分享");

      const link = encodeVideoSourceSyncLink(sources);
      expect(setString).toHaveBeenCalledWith(link);
      expect(share).toHaveBeenCalledWith({ message: link });
      expect(texts(tree)).toContain("同步链接已复制");
    });

    it("rotates through several sparse QR codes when one code cannot hold the link", async () => {
      jest.useFakeTimers();
      useSettingsStore.setState({ subscribedVideoSources: manySources });
      const tree = mount();
      await press(tree, "同步视频源(导出)");

      const parts = buildVideoSourceSyncQrPayloads(encodeVideoSourceSyncLink(manySources));
      expect(parts.length).toBeGreaterThan(1);
      const shown = () => tree.root.findByProps({ testID: "qr" }).props.value;
      expect(shown()).toBe(parts[0]);

      act(() => {
        jest.advanceTimersByTime(1500);
      });
      expect(shown()).toBe(parts[1]);

      // 手动翻页后停在那一张
      await press(tree, "上一张");
      expect(shown()).toBe(parts[0]);
      act(() => {
        jest.advanceTimersByTime(6000);
      });
      expect(shown()).toBe(parts[0]);
    });

    it("says so when there is nothing to export", async () => {
      const tree = mount();

      await press(tree, "同步视频源(导出)");

      expect(tree.root.findAllByProps({ testID: "qr" })).toHaveLength(0);
      expect(texts(tree)).toContain("当前没有可导出的视频源");
    });
  });

  describe("import by pasting", () => {
    const paste = (tree: renderer.ReactTestRenderer, value: string) =>
      act(() => {
        tree.root.findByType(TextInput).props.onChangeText(value);
      });

    it("imports all sources of a pasted sync link at once", async () => {
      useSettingsStore.setState({ subscribedVideoSources: [sources[0]] });
      const tree = mount();
      paste(tree, encodeVideoSourceSyncLink(sources));

      await press(tree, "添加");

      expect(storedKeys()).toEqual(["demo", "custom_my.example", "adult"]);
      expect(useSettingsStore.getState().subscribedVideoSources[2].adult).toBe(true);
      const output = texts(tree);
      expect(output).toContain("已导入同步链接里的 3 个视频源(新增 2 个,1 个已存在)");
      // 成人内容开关关着:导入的成人源不显示,只说明数量
      expect(output).toContain("其中 1 个是成人源,成人内容开关已关闭,暂不显示");
      expect(output).not.toContain("成人源  [18+]");
      expect(tree.root.findByType(TextInput).props.value).toBe("");
    });

    it("explains why a broken link or a single QR part cannot be imported", async () => {
      const tree = mount();

      paste(tree, "opaitv-sync://z1/not-a-link");
      await press(tree, "添加");
      expect(texts(tree)).toContain("同步链接无效或不完整");

      paste(tree, buildVideoSourceSyncQrPayloads(encodeVideoSourceSyncLink(manySources))[0]);
      await press(tree, "添加");
      expect(texts(tree)).toContain("这是分片二维码里的一张");

      expect(storedKeys()).toEqual([]);
    });
  });

  describe("import by scanning", () => {
    const openScanner = async (tree: renderer.ReactTestRenderer) => {
      await press(tree, "扫码导入");
      // 等相机权限的应答
      await act(async () => {});
    };

    const scan = async (tree: renderer.ReactTestRenderer, data: string) => {
      await act(async () => {
        await tree.root.findByProps({ testID: "camera" }).props.onBarcodeScanned({ type: "qr", data });
      });
    };

    it("imports the sources of a scanned sync QR code and closes the scanner", async () => {
      const tree = mount();
      await openScanner(tree);
      expect(tree.root.findByProps({ testID: "camera" }).props.barcodeScannerSettings).toEqual({ barcodeTypes: ["qr"] });

      await scan(tree, "https://example.com/some-other-qr");
      expect(texts(tree)).toContain("这不是 OpaiTV 的同步二维码");
      expect(storedKeys()).toEqual([]);

      await scan(tree, encodeVideoSourceSyncLink(sources));

      expect(storedKeys()).toEqual(["demo", "custom_my.example", "adult"]);
      expect(tree.root.findAllByProps({ testID: "camera" })).toHaveLength(0);
      expect(texts(tree)).toContain("已导入同步链接里的 3 个视频源(新增 3 个)");
    });

    it("collects rotating QR parts until the link is complete", async () => {
      const parts = buildVideoSourceSyncQrPayloads(encodeVideoSourceSyncLink(manySources));
      const tree = mount();
      await openScanner(tree);

      await scan(tree, parts[1]);
      await scan(tree, parts[1]);
      expect(texts(tree)).toContain(`已扫到 1 / ${parts.length} 张`);
      expect(storedKeys()).toEqual([]);

      for (const part of [...parts.slice(2), parts[0]]) {
        await scan(tree, part);
      }

      expect(storedKeys()).toEqual(manySources.map((source) => source.key));
      expect(tree.root.findAllByProps({ testID: "camera" })).toHaveLength(0);
    });

    it("explains a refused camera permission instead of showing the camera", async () => {
      mockRequestCameraPermissions.mockResolvedValue({ granted: false });
      const tree = mount();

      await openScanner(tree);

      expect(tree.root.findAllByProps({ testID: "camera" })).toHaveLength(0);
      expect(texts(tree)).toContain("没有相机权限");
    });

    it("is not offered on TV devices, which have no camera", () => {
      setIsTV(true);
      const tree = mount();

      expect(button(tree, "扫码导入")).toBeUndefined();
      expect(button(tree, "同步视频源(导出)")).toBeDefined();
    });
  });
});
