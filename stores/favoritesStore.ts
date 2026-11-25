import { create } from "zustand";
import { Favorite, FavoriteManager } from "@/services/storage";
import { api } from "@/services/api";

interface FavoritesState {
  favorites: (Favorite & { key: string })[];
  loading: boolean;
  error: string | null;
  fetchFavorites: () => Promise<void>;
  /** 删除单条收藏(key 为 `${source}+${id}`):先从列表去掉,存储删除失败时重新取数并返回 false */
  removeFavorite: (key: string) => Promise<boolean>;
  /** 收藏侧滑面板是否打开(面板挂在根布局,各个收藏入口只改这个状态) */
  drawerVisible: boolean;
  openDrawer: () => void;
  closeDrawer: () => void;
}

/** 收藏 key 拆成 source 与 id(id 里可能带 "+",只按第一个拆) */
export const splitFavoriteKey = (key: string): { source: string; id: string } => {
  const index = key.indexOf("+");
  return index < 0 ? { source: key, id: "" } : { source: key.slice(0, index), id: key.slice(index + 1) };
};

const useFavoritesStore = create<FavoritesState>((set, get) => ({
  favorites: [],
  loading: false,
  error: null,
  drawerVisible: false,
  openDrawer: () => set({ drawerVisible: true }),
  closeDrawer: () => set({ drawerVisible: false }),
  removeFavorite: async (key) => {
    const { source, id } = splitFavoriteKey(key);
    set((state) => ({ favorites: state.favorites.filter((item) => item.key !== key), error: null }));
    try {
      await FavoriteManager.remove(source, id);
      return true;
    } catch (e) {
      // 没删掉:按存储里的实际内容恢复列表
      await get().fetchFavorites();
      set({ error: e instanceof Error ? e.message : "删除收藏失败" });
      return false;
    }
  },
  fetchFavorites: async () => {
    set({ loading: true, error: null });
    try {
      const favoritesData = await FavoriteManager.getAll();
      const favoritesArray = Object.entries(favoritesData)
        // 成人内容开关关闭时,成人源与成人分类的收藏不显示(收藏本身不删除,开关打开后恢复)
        .filter(([key, value]) => !api.isAdultItemHidden({ source: key.split("+")[0], type_name: value?.type_name }))
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
