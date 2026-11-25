import React from "react";
import renderer, { act } from "react-test-renderer";
import { Modal, Text } from "react-native";

import { FavoritesDrawer } from "../FavoritesDrawer";
import useFavoritesStore from "@/stores/favoritesStore";
import { FavoriteManager } from "@/services/storage";

const mockPush = jest.fn();

jest.mock("expo-router", () => ({ useRouter: () => ({ push: mockPush }) }));
jest.mock("lucide-react-native", () => ({ Play: () => null, Trash2: () => null, X: () => null }));
jest.mock("@/services/api", () => ({
  api: {
    getImageSource: (uri: string) => ({ uri }),
    // 模拟成人过滤:store 里的过滤照常生效,面板只显示过滤后的列表
    isAdultItemHidden: ({ source }: { source: string }) => source === "adult",
  },
}));
jest.mock("@/services/storage", () => ({
  FavoriteManager: { getAll: jest.fn(), remove: jest.fn() },
}));
jest.mock("@/stores/settingsStore", () => ({
  useSettingsStore: (selector: (state: { showAdultContent: boolean }) => unknown) => selector({ showAdultContent: false }),
}));

const favorite = (title: string) => ({
  title,
  source_name: "Source",
  cover: `https://img.example/${title}.jpg`,
  total_episodes: 1,
  search_title: title,
  year: "2026",
});

describe("FavoritesDrawer", () => {
  let tree: renderer.ReactTestRenderer;
  let stored: Record<string, ReturnType<typeof favorite>>;

  const texts = () => tree.root.findAllByType(Text).map((node) => [node.props.children].flat().join(""));
  const press = async (testID: string) => {
    await act(async () => {
      tree.root.findByProps({ testID }).props.onPress();
    });
  };
  const open = async () => {
    await act(async () => {
      useFavoritesStore.getState().openDrawer();
    });
  };

  beforeEach(async () => {
    jest.useFakeTimers();
    jest.clearAllMocks();
    stored = { "src+1": favorite("One"), "src+2": favorite("Two"), "adult+9": favorite("Hidden") };
    (FavoriteManager.getAll as jest.Mock).mockImplementation(async () => ({ ...stored }));
    (FavoriteManager.remove as jest.Mock).mockImplementation(async (source: string, id: string) => {
      delete stored[`${source}+${id}`];
    });
    useFavoritesStore.setState({ favorites: [], loading: false, error: null, drawerVisible: false });
    await act(async () => {
      tree = renderer.create(<FavoritesDrawer />);
    });
  });

  afterEach(() => {
    act(() => {
      tree.unmount();
    });
    jest.useRealTimers();
  });

  it("renders nothing until a favorites entry opens it", () => {
    expect(tree.toJSON()).toBeNull();
    expect(FavoriteManager.getAll).not.toHaveBeenCalled();
  });

  it("slides in with the existing favorites data, adult filter included", async () => {
    await open();

    expect(tree.root.findAllByType(Modal)).toHaveLength(1);
    expect(tree.root.findAllByProps({ testID: "favorites-drawer-panel" }).length).toBeGreaterThan(0);
    expect(texts()).toEqual(expect.arrayContaining(["我的收藏", "One", "Two"]));
    expect(texts()).not.toContain("Hidden");
  });

  it("opens the detail page for a tapped item and closes the drawer", async () => {
    await open();
    await press("favorites-drawer-open-src+2");

    expect(mockPush).toHaveBeenCalledWith({ pathname: "/detail", params: { source: "src", id: "2", q: "Two" } });
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);
  });

  it("starts playback from the play button", async () => {
    await open();
    await press("favorites-drawer-play-src+1");

    expect(mockPush).toHaveBeenCalledWith({
      pathname: "/play",
      params: { source: "src", id: "1", episodeIndex: 0, title: "One", position: 0 },
    });
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);
  });

  it("removes a favorite from inside the drawer and stays open", async () => {
    await open();
    await press("favorites-drawer-remove-src+1");

    expect(FavoriteManager.remove).toHaveBeenCalledWith("src", "1");
    expect(texts()).not.toContain("One");
    expect(texts()).toContain("Two");
    expect(useFavoritesStore.getState().drawerVisible).toBe(true);
    expect(mockPush).not.toHaveBeenCalled();
  });

  it("shows the empty state after the last favorite is removed", async () => {
    await open();
    await press("favorites-drawer-remove-src+1");
    await press("favorites-drawer-remove-src+2");

    expect(texts()).toContain("暂无收藏");
  });

  it("closes from the backdrop, the close button and the hardware back key", async () => {
    await open();
    await press("favorites-drawer-backdrop");
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);

    await open();
    await press("favorites-drawer-close");
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);

    await open();
    await act(async () => {
      tree.root.findByType(Modal).props.onRequestClose();
    });
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);
  });

  it("unmounts once the slide-out animation has finished", async () => {
    await open();
    await press("favorites-drawer-close");
    await act(async () => {
      jest.advanceTimersByTime(1000);
    });

    expect(tree.toJSON()).toBeNull();
  });

  it("stays on screen when reopened before the slide-out animation has finished", async () => {
    await open();
    await press("favorites-drawer-close");
    await act(async () => {
      jest.advanceTimersByTime(50);
    });
    await open();
    await act(async () => {
      jest.advanceTimersByTime(1000);
    });

    expect(tree.root.findAllByType(Modal)).toHaveLength(1);
    expect(texts()).toContain("One");
  });

  it("keeps the full favorites page reachable", async () => {
    await open();
    await press("favorites-drawer-view-all");

    expect(mockPush).toHaveBeenCalledWith("/favorites");
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);
  });
});
