import React, { useEffect, useRef, useState } from "react";
import {
  Animated,
  FlatList,
  Image,
  Modal,
  Pressable,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
  useWindowDimensions,
} from "react-native";
import { useRouter } from "expo-router";
import { Play, Trash2, X } from "lucide-react-native";
import useFavoritesStore, { splitFavoriteKey } from "@/stores/favoritesStore";
import { useSettingsStore } from "@/stores/settingsStore";
import { Favorite } from "@/services/storage";
import { api } from "@/services/api";

const ANIMATION_MS = 220;

type FavoriteRow = Favorite & { key: string };

/**
 * 收藏侧滑面板:从屏幕右侧滑入,点遮罩 / 返回键 / 关闭按钮收起。
 * 列表就是 favoritesStore 的数据(成人内容过滤在 store 里做),不另外取数。
 * 点条目进详情,点播放按钮直接起播,点垃圾桶删除收藏;"查看全部"进原有的收藏页。
 */
export const FavoritesDrawer: React.FC = () => {
  const router = useRouter();
  const visible = useFavoritesStore((state) => state.drawerVisible);
  const closeDrawer = useFavoritesStore((state) => state.closeDrawer);
  const favorites = useFavoritesStore((state) => state.favorites);
  const loading = useFavoritesStore((state) => state.loading);
  const error = useFavoritesStore((state) => state.error);
  const fetchFavorites = useFavoritesStore((state) => state.fetchFavorites);
  const removeFavorite = useFavoritesStore((state) => state.removeFavorite);
  // 成人内容开关变化后重新取一次,与收藏页一致
  const showAdultContent = useSettingsStore((state) => state.showAdultContent);

  const { width: windowWidth } = useWindowDimensions();
  const panelWidth = Math.min(380, Math.round(windowWidth * 0.86));

  // mounted 比 visible 晚一拍变 false:收起动画播完才卸载 Modal
  const [mounted, setMounted] = useState(visible);
  const slide = useRef(new Animated.Value(visible ? 1 : 0)).current;

  useEffect(() => {
    if (visible) {
      setMounted(true);
      Animated.timing(slide, { toValue: 1, duration: ANIMATION_MS, useNativeDriver: true }).start();
      return;
    }
    // 收起动画被新的展开动画打断时(finished 为 false)不能卸载,否则"刚关又开"面板会消失
    Animated.timing(slide, { toValue: 0, duration: ANIMATION_MS, useNativeDriver: true }).start(({ finished }) => {
      if (finished) {
        setMounted(false);
      }
    });
  }, [visible, slide]);

  useEffect(() => {
    if (visible) {
      fetchFavorites();
    }
  }, [visible, fetchFavorites, showAdultContent]);

  if (!mounted) return null;

  const openDetail = (item: FavoriteRow) => {
    const { source, id } = splitFavoriteKey(item.key);
    closeDrawer();
    router.push({ pathname: "/detail", params: { source, id, q: item.title } });
  };

  const play = (item: FavoriteRow) => {
    const { source, id } = splitFavoriteKey(item.key);
    closeDrawer();
    // 与收藏页点卡片一致:从第一集开头起播
    router.push({ pathname: "/play", params: { source, id, episodeIndex: 0, title: item.title, position: 0 } });
  };

  const viewAll = () => {
    closeDrawer();
    router.push("/favorites");
  };

  const renderItem = ({ item }: { item: FavoriteRow }) => (
    <View style={styles.row} testID={`favorites-drawer-item-${item.key}`}>
      <TouchableOpacity
        style={styles.rowMain}
        activeOpacity={0.7}
        onPress={() => openDetail(item)}
        testID={`favorites-drawer-open-${item.key}`}
      >
        <Image source={api.getImageSource(item.cover)} style={styles.cover} />
        <View style={styles.rowText}>
          <Text style={styles.title} numberOfLines={2}>
            {item.title}
          </Text>
          <Text style={styles.meta} numberOfLines={1}>
            {[item.source_name, item.year].filter(Boolean).join(" · ")}
          </Text>
        </View>
      </TouchableOpacity>
      <TouchableOpacity
        style={styles.iconButton}
        onPress={() => play(item)}
        accessibilityLabel={`播放 ${item.title}`}
        testID={`favorites-drawer-play-${item.key}`}
      >
        <Play size={18} color="#fff" />
      </TouchableOpacity>
      <TouchableOpacity
        style={styles.iconButton}
        onPress={() => removeFavorite(item.key)}
        accessibilityLabel={`取消收藏 ${item.title}`}
        testID={`favorites-drawer-remove-${item.key}`}
      >
        <Trash2 size={18} color="#ff6b6b" />
      </TouchableOpacity>
    </View>
  );

  const translateX = slide.interpolate({ inputRange: [0, 1], outputRange: [panelWidth, 0] });

  return (
    <Modal visible transparent animationType="none" onRequestClose={closeDrawer} statusBarTranslucent>
      <View style={styles.root}>
        <Animated.View style={[styles.backdrop, { opacity: slide }]}>
          <Pressable
            style={StyleSheet.absoluteFill}
            onPress={closeDrawer}
            accessibilityLabel="关闭收藏面板"
            testID="favorites-drawer-backdrop"
          />
        </Animated.View>
        <Animated.View
          style={[styles.panel, { width: panelWidth, transform: [{ translateX }] }]}
          testID="favorites-drawer-panel"
        >
          <View style={styles.header}>
            <Text style={styles.headerTitle}>我的收藏</Text>
            <TouchableOpacity onPress={viewAll} style={styles.viewAll} testID="favorites-drawer-view-all">
              <Text style={styles.viewAllText}>查看全部</Text>
            </TouchableOpacity>
            <TouchableOpacity
              onPress={closeDrawer}
              style={styles.iconButton}
              accessibilityLabel="关闭"
              testID="favorites-drawer-close"
            >
              <X size={20} color="#ccc" />
            </TouchableOpacity>
          </View>
          {error ? <Text style={styles.error}>{error}</Text> : null}
          <FlatList
            data={favorites}
            keyExtractor={(item) => item.key}
            renderItem={renderItem}
            ListEmptyComponent={<Text style={styles.empty}>{loading ? "正在加载..." : "暂无收藏"}</Text>}
            contentContainerStyle={styles.listContent}
          />
        </Animated.View>
      </View>
    </Modal>
  );
};

const styles = StyleSheet.create({
  root: {
    flex: 1,
    flexDirection: "row",
    justifyContent: "flex-end",
  },
  backdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: "rgba(0,0,0,0.55)",
  },
  panel: {
    height: "100%",
    backgroundColor: "#1c1c1e",
    borderLeftWidth: 1,
    borderLeftColor: "#333",
    paddingTop: 36,
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    paddingLeft: 16,
    paddingRight: 8,
    paddingBottom: 8,
    borderBottomWidth: 1,
    borderBottomColor: "#2c2c2e",
  },
  headerTitle: {
    flex: 1,
    color: "#fff",
    fontSize: 18,
    fontWeight: "bold",
  },
  viewAll: {
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  viewAllText: {
    color: "#4da3ff",
    fontSize: 14,
  },
  listContent: {
    paddingVertical: 8,
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    paddingLeft: 12,
    paddingRight: 4,
    paddingVertical: 6,
  },
  rowMain: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
  },
  cover: {
    width: 48,
    height: 68,
    borderRadius: 6,
    backgroundColor: "#2c2c2e",
  },
  rowText: {
    flex: 1,
    marginLeft: 10,
  },
  title: {
    color: "#fff",
    fontSize: 15,
    fontWeight: "500",
  },
  meta: {
    color: "#888",
    fontSize: 12,
    marginTop: 4,
  },
  iconButton: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
  },
  empty: {
    color: "#888",
    textAlign: "center",
    marginTop: 48,
  },
  error: {
    color: "#ff6b6b",
    paddingHorizontal: 16,
    paddingTop: 8,
  },
});

export default FavoritesDrawer;
