import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text } from "react-native";
import AsyncStorage from "@react-native-async-storage/async-storage";
import MobileTabContainer from "../navigation/MobileTabContainer";
import TabletSidebarNavigator from "../navigation/TabletSidebarNavigator";

let mockDeviceType = "mobile";
const mockPush = jest.fn();

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(async () => null),
  setItem: jest.fn(async () => undefined),
  removeItem: jest.fn(),
}));
jest.mock("@/services/updateService", () => ({
  __esModule: true,
  default: { getCurrentVersion: () => "1.3.27", isUpdateAvailable: jest.fn(), checkVersion: jest.fn() },
}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));
jest.mock("@/services/storage", () => ({ FavoriteManager: { getAll: jest.fn(async () => ({})), remove: jest.fn() } }));
jest.mock("@/services/api", () => ({ api: { isAdultItemHidden: () => false } }));
jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush }),
  usePathname: () => "/",
}));
jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ spacing: 8, deviceType: mockDeviceType, isPortrait: false }),
}));
jest.mock("@/utils/DeviceUtils", () => ({ DeviceUtils: { getMinTouchTargetSize: () => 44 } }));
jest.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { Home: Icon, Search: Icon, Heart: Icon, Settings: Icon, Tv: Icon, Menu: Icon, X: Icon };
});
jest.mock("@/components/ThemedText", () => {
  const { Text: RNText } = jest.requireActual("react-native");
  return { ThemedText: RNText };
});

const getItem = AsyncStorage.getItem as jest.Mock;
const setItem = AsyncStorage.setItem as jest.Mock;
const has = (tree: renderer.ReactTestRenderer, testID: string) => tree.root.findAllByProps({ testID }).length > 0;
const flush = async () => {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
};
const mount = async (element: React.ReactElement) => {
  let tree!: renderer.ReactTestRenderer;
  await act(async () => {
    tree = renderer.create(element);
  });
  await flush();
  return tree;
};

describe("collapsible navigation (UI state only)", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    getItem.mockImplementation(async () => null);
    mockDeviceType = "mobile";
  });

  it("phone: tab bar is shown by default with every entry, hides on the handle and comes back", async () => {
    const tree = await mount(
      <MobileTabContainer>
        <Text>content</Text>
      </MobileTabContainer>
    );
    for (const key of ["home", "search", "favorites", "settings"]) expect(has(tree, `mobile-tab-${key}`)).toBe(true);
    expect(has(tree, "mobile-nav-expand")).toBe(false);

    await act(async () => {
      tree.root.findByProps({ testID: "mobile-nav-collapse" }).props.onPress();
    });
    await flush();
    expect(has(tree, "mobile-tab-home")).toBe(false);
    expect(has(tree, "mobile-nav-expand")).toBe(true);
    expect(setItem).toHaveBeenCalledWith("mytv_nav_collapsed", JSON.stringify({ mobile: true }));

    await act(async () => {
      tree.root.findByProps({ testID: "mobile-nav-expand" }).props.onPress();
    });
    await flush();
    for (const key of ["home", "search", "favorites", "settings"]) expect(has(tree, `mobile-tab-${key}`)).toBe(true);
  });

  it("phone: restores the saved collapsed state, and a storage failure leaves the bar visible", async () => {
    getItem.mockImplementation(async () => JSON.stringify({ mobile: true }));
    const collapsed = await mount(
      <MobileTabContainer>
        <Text>content</Text>
      </MobileTabContainer>
    );
    expect(has(collapsed, "mobile-nav-expand")).toBe(true);

    getItem.mockImplementation(async () => {
      throw new Error("storage down");
    });
    const fallback = await mount(
      <MobileTabContainer>
        <Text>content</Text>
      </MobileTabContainer>
    );
    expect(has(fallback, "mobile-tab-home")).toBe(true);
  });

  it("tablet: collapsing keeps every entry reachable as an icon and persists per surface", async () => {
    mockDeviceType = "tablet";
    const tree = await mount(
      <TabletSidebarNavigator>
        <Text>content</Text>
      </TabletSidebarNavigator>
    );
    const labels = () => tree.root.findAllByType(Text).map((node) => node.props.children);
    expect(labels()).toContain("设置");

    await act(async () => {
      tree.root.findByProps({ testID: "sidebar-toggle" }).props.onPress();
    });
    await flush();
    for (const key of ["home", "search", "live", "favorites", "settings"]) expect(has(tree, `sidebar-item-${key}`)).toBe(true);
    expect(labels()).not.toContain("设置");
    expect(setItem).toHaveBeenCalledWith("mytv_nav_collapsed", JSON.stringify({ tablet: true }));
  });
});
