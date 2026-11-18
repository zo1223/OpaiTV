import React from "react";
import { Platform, Pressable } from "react-native";
import renderer, { act } from "react-test-renderer";
import { RemoteInputSection } from "../settings/RemoteInputSection";
import { useSettingsStore } from "@/stores/settingsStore";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

jest.mock("@/services/remoteControlService", () => ({
  remoteControlService: { init: jest.fn(), startServer: jest.fn(), stopServer: jest.fn() },
}));

jest.mock("@/hooks/useSafeTVEventHandler", () => ({
  useSafeTVEventHandler: jest.fn(),
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "tablet", columns: 3, cardWidth: 100, cardHeight: 150, spacing: 12 }),
}));

describe("RemoteInputSection", () => {
  const originalOS = Platform.OS;
  const originalIsTV = Platform.isTV;

  const setPlatform = (OS: typeof Platform.OS, isTV: boolean) => {
    Object.defineProperty(Platform, "OS", { configurable: true, value: OS });
    Object.defineProperty(Platform, "isTV", { configurable: true, value: isTV });
  };

  const mount = (onChanged: () => void) => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<RemoteInputSection onChanged={onChanged} />);
    });
    return tree!;
  };

  beforeEach(() => {
    useSettingsStore.setState({ remoteInputEnabled: false });
  });

  afterEach(() => {
    setPlatform(originalOS, originalIsTV);
  });

  it("toggles remote input when the row is tapped on a touch device", () => {
    setPlatform("android", false);
    const onChanged = jest.fn();
    const tree = mount(onChanged);

    // A touch lands on the innermost Pressable (the row); it claims the responder, so it has to do the toggling.
    const pressables = tree.root.findAllByType(Pressable);
    const row = pressables[pressables.length - 1];
    expect(row.props.onPress).toEqual(expect.any(Function));

    act(() => row.props.onPress());
    expect(useSettingsStore.getState().remoteInputEnabled).toBe(true);
    expect(onChanged).toHaveBeenCalledTimes(1);

    const updated = tree.root.findAllByType(Pressable);
    act(() => updated[updated.length - 1].props.onPress());
    expect(useSettingsStore.getState().remoteInputEnabled).toBe(false);

    act(() => tree.unmount());
  });

  it("leaves toggling to the remote's select event on TV", () => {
    setPlatform("android", true);
    const onChanged = jest.fn();
    const tree = mount(onChanged);

    // On TV a select fires both the TV event and the focused Pressable's press; only the former may toggle.
    for (const pressable of tree.root.findAllByType(Pressable)) {
      act(() => pressable.props.onPress?.());
    }
    expect(useSettingsStore.getState().remoteInputEnabled).toBe(false);
    expect(onChanged).not.toHaveBeenCalled();

    act(() => tree.unmount());
  });
});
