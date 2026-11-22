import { create } from "zustand";
import { Favorite, FavoriteManager } from "@/services/storage";
import { api } from "@/services/api";

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
      const favoritesArray = Object.entries(favoritesData)
        // 成人内容开关关闭时,成人源的收藏不显示(收藏本身不删除,开关打开后恢复)
        .filter(([key]) => !api.isAdultSourceHidden(key.split("+")[0]))
        .map(([key, value]) => ({
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
