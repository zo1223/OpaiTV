import React from "react";
import renderer, { act } from "react-test-renderer";
import { Text } from "react-native";

import { UpdateBadge } from "../UpdateBadge";
import MobileTabContainer from "../navigation/MobileTabContainer";
import TabletSidebarNavigator from "../navigation/TabletSidebarNavigator";
import { useUpdateStore } from "@/stores/updateStore";
import useFavoritesStore from "@/stores/favoritesStore";

const mockPush = jest.fn();
let mockPathname = "/";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(async () => null),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));
jest.mock("@/services/updateService", () => ({
  __esModule: true,
  default: { getCurrentVersion: () => "1.3.26", isUpdateAvailable: jest.fn(), checkVersion: jest.fn() },
}));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));
jest.mock("@/services/storage", () => ({ FavoriteManager: { getAll: jest.fn(async () => ({})), remove: jest.fn() } }));
jest.mock("@/services/api", () => ({ api: { isAdultItemHidden: () => false } }));
jest.mock("expo-router", () => ({
  useRouter: () => ({ push: mockPush }),
  usePathname: () => mockPathname,
}));
jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ spacing: 8, deviceType: "mobile", isPortrait: false }),
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

const badges = (tree: renderer.ReactTestRenderer) => tree.root.findAllByProps({ testID: "update-badge" });
const hasBadge = (tree: renderer.ReactTestRenderer) => badges(tree).length > 0;
const render = (element: React.ReactElement) => {
  let tree: renderer.ReactTestRenderer;
  act(() => {
    tree = renderer.create(element);
  });
  return tree!;
};

describe("update red dot", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockPathname = "/";
    useUpdateStore.setState({ updateAvailable: false, remoteVersion: "", skipVersion: null });
    useFavoritesStore.setState({ drawerVisible: false });
  });

  it("is not rendered when there is no newer version", () => {
    expect(hasBadge(render(<UpdateBadge />))).toBe(false);
  });

  it("appears as soon as the store learns about a newer version, and goes away again", () => {
    const tree = render(<UpdateBadge />);

    act(() => {
      useUpdateStore.setState({ updateAvailable: true, remoteVersion: "1.3.27" });
    });
    expect(hasBadge(tree)).toBe(true);

    act(() => {
      useUpdateStore.setState({ updateAvailable: false, remoteVersion: "1.3.26" });
    });
    expect(hasBadge(tree)).toBe(false);
  });

  it("stays hidden for the version the user chose to skip", () => {
    useUpdateStore.setState({ updateAvailable: true, remoteVersion: "1.3.27", skipVersion: "1.3.27" });
    expect(hasBadge(render(<UpdateBadge />))).toBe(false);
  });

  it.each([
    ["mobile tab bar", MobileTabContainer, "mobile-tab-settings", "mobile-tab-search"],
    ["tablet sidebar", TabletSidebarNavigator, "sidebar-item-settings", "sidebar-item-search"],
  ])("%s: the dot sits on the settings entry only, and the entry still opens settings", (_name, Navigator, settingsId, otherId) => {
    useUpdateStore.setState({ updateAvailable: true, remoteVersion: "1.3.27" });
    const tree = render(
      <Navigator>
        <Text>content</Text>
      </Navigator>
    );

    const settingsEntry = tree.root.findByProps({ testID: settingsId });
    expect(settingsEntry.findAllByProps({ testID: "update-badge" }).length).toBeGreaterThan(0);
    expect(tree.root.findByProps({ testID: otherId }).findAllByProps({ testID: "update-badge" })).toHaveLength(0);

    act(() => {
      settingsEntry.props.onPress();
    });
    expect(mockPush).toHaveBeenCalledWith("/settings");
  });

  it.each([
    ["mobile tab bar", MobileTabContainer, "mobile-tab-settings"],
    ["tablet sidebar", TabletSidebarNavigator, "sidebar-item-settings"],
  ])("%s: no dot without an update", (_name, Navigator, settingsId) => {
    const tree = render(
      <Navigator>
        <Text>content</Text>
      </Navigator>
    );
    expect(tree.root.findByProps({ testID: settingsId }).findAllByProps({ testID: "update-badge" })).toHaveLength(0);
  });
});

describe("favorites entry", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockPathname = "/";
    useFavoritesStore.setState({ drawerVisible: false });
  });

  it.each([
    ["mobile tab bar", MobileTabContainer, "mobile-tab-favorites"],
    ["tablet sidebar", TabletSidebarNavigator, "sidebar-item-favorites"],
  ])("%s: slides the drawer out instead of leaving the page", (_name, Navigator, favoritesId) => {
    const tree = render(
      <Navigator>
        <Text>content</Text>
      </Navigator>
    );

    act(() => {
      tree.root.findByProps({ testID: favoritesId }).props.onPress();
    });

    expect(useFavoritesStore.getState().drawerVisible).toBe(true);
    expect(mockPush).not.toHaveBeenCalled();
  });

  it("does not stack the drawer on top of the favorites page itself", () => {
    mockPathname = "/favorites";
    const tree = render(
      <MobileTabContainer>
        <Text>content</Text>
      </MobileTabContainer>
    );

    act(() => {
      tree.root.findByProps({ testID: "mobile-tab-favorites" }).props.onPress();
    });

    expect(useFavoritesStore.getState().drawerVisible).toBe(false);
    expect(mockPush).toHaveBeenCalledWith("/favorites");
  });
});
