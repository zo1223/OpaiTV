import { create } from "zustand";
import { Favorite, FavoriteManager } from "@/services/storage";

interface FavoritesState {
  favorites: (Favorite & { key: string })[];
  loading: boolean;
  error: string | null;
  fetchFavorites: () => Promise<void>;
}

const useFavoritesStore = create<FavoritesState>((set) => ({
  favorites: [],
  loading: false,
  error: null,
  fetchFavorites: async () => {
    set({ loading: true, error: null });
    try {
      const favoritesData = await FavoriteManager.getAll();
      const favoritesArray = Object.entries(favoritesData).map(([key, value]) => ({
        ...value,
        key,
      }));
      //   favoritesArray.sort((a, b) => (b.save_time || 0) - (a.save_time || 0));
      set({ favorites: favoritesArray, loading: false });
    } catch (e) {
      const error = e instanceof Error ? e.message : "获取收藏列表失败";
      set({ error, loading: false });
    }
  },
}));

export default useFavoritesStore;
