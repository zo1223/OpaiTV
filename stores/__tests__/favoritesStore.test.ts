import useFavoritesStore, { splitFavoriteKey } from "../favoritesStore";
import { FavoriteManager } from "@/services/storage";
import AsyncStorage from "@react-native-async-storage/async-storage";

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  setItem: jest.fn(),
  removeItem: jest.fn(),
}));

const favorite = (title: string) => ({
  title,
  source_name: "Source",
  cover: "",
  total_episodes: 1,
  search_title: title,
  year: "2026",
});

describe("favoritesStore", () => {
  let stored: Record<string, string>;
  const storedKeys = () => Object.keys(JSON.parse(stored.mytv_favorites)).sort();
  const shownKeys = () => useFavoritesStore.getState().favorites.map((item) => item.key);

  beforeEach(async () => {
    jest.restoreAllMocks();
    jest.clearAllMocks();
    stored = {
      mytv_favorites: JSON.stringify({ "src+1": favorite("One"), "src+2": favorite("Two"), "other+a+b": favorite("Three") }),
    };
    (AsyncStorage.getItem as jest.Mock).mockImplementation(async (key: string) => stored[key] ?? null);
    (AsyncStorage.setItem as jest.Mock).mockImplementation(async (key: string, value: string) => {
      stored[key] = value;
    });
    useFavoritesStore.setState({ favorites: [], loading: false, error: null, drawerVisible: false });
    await useFavoritesStore.getState().fetchFavorites();
  });

  it("splits a favorite key on the first plus sign only", () => {
    expect(splitFavoriteKey("src+1")).toEqual({ source: "src", id: "1" });
    expect(splitFavoriteKey("other+a+b")).toEqual({ source: "other", id: "a+b" });
  });

  it("removes one favorite from the list and from storage, leaving the others", async () => {
    expect(shownKeys()).toEqual(["src+1", "src+2", "other+a+b"]);

    await expect(useFavoritesStore.getState().removeFavorite("src+2")).resolves.toBe(true);

    expect(shownKeys()).toEqual(["src+1", "other+a+b"]);
    expect(storedKeys()).toEqual(["other+a+b", "src+1"]);
  });

  it("removes a favorite whose id contains a plus sign", async () => {
    await useFavoritesStore.getState().removeFavorite("other+a+b");
    expect(storedKeys()).toEqual(["src+1", "src+2"]);
  });

  it("puts the favorite back in the list when storage refuses the delete", async () => {
    jest.spyOn(FavoriteManager, "remove").mockRejectedValueOnce(new Error("offline"));

    await expect(useFavoritesStore.getState().removeFavorite("src+2")).resolves.toBe(false);

    expect(shownKeys()).toEqual(["src+1", "src+2", "other+a+b"]);
    expect(useFavoritesStore.getState().error).toBe("offline");
    expect(storedKeys()).toEqual(["other+a+b", "src+1", "src+2"]);
  });

  it("opens and closes the drawer", () => {
    useFavoritesStore.getState().openDrawer();
    expect(useFavoritesStore.getState().drawerVisible).toBe(true);
    useFavoritesStore.getState().closeDrawer();
    expect(useFavoritesStore.getState().drawerVisible).toBe(false);
  });
});
