import React, { useCallback, useEffect, useState } from "react";
import { ActivityIndicator, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ThemedView } from "@/components/ThemedView";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import VideoCard from "@/components/VideoCard";
import CustomScrollView from "@/components/CustomScrollView";
import ResponsiveNavigation from "@/components/navigation/ResponsiveNavigation";
import ResponsiveHeader from "@/components/navigation/ResponsiveHeader";
import { api, ApiSite, SearchResult } from "@/services/api";
import { useApiConfig, getApiConfigErrorMessage } from "@/hooks/useApiConfig";
import { useSettingsStore } from "@/stores/settingsStore";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { getCommonResponsiveStyles } from "@/utils/ResponsiveStyles";
import { Search } from "lucide-react-native";

interface SourceCategory {
  id: string;
  name: string;
}

const PAGE_SIZE = 20;

export default function SourceSearchScreen() {
  const [sources, setSources] = useState<ApiSite[]>([]);
  const [categories, setCategories] = useState<SourceCategory[]>([]);
  const [selectedSource, setSelectedSource] = useState<ApiSite | null>(null);
  const [selectedCategory, setSelectedCategory] = useState<SourceCategory | null>(null);
  const [videos, setVideos] = useState<SearchResult[]>([]);
  const [keyword, setKeyword] = useState("");
  const [page, setPage] = useState(1);
  const [pageCount, setPageCount] = useState(0);
  const [loadingSources, setLoadingSources] = useState(false);
  const [loadingCategories, setLoadingCategories] = useState(false);
  const [loadingVideos, setLoadingVideos] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const apiConfigStatus = useApiConfig();
  // 成人内容开关变化后重新加载源列表(api 层按开关过滤),不留已隐藏源的分类和视频
  const showAdultContent = useSettingsStore((state) => state.showAdultContent);
  const responsiveConfig = useResponsiveLayout();
  const commonStyles = getCommonResponsiveStyles(responsiveConfig);
  const { deviceType, spacing } = responsiveConfig;
  const styles = createStyles(deviceType, spacing);

  const loadCategoryVideos = useCallback(
    async (source: ApiSite, category: SourceCategory, nextPage: number, append = false) => {
      if (append) {
        setLoadingMore(true);
      } else {
        setLoadingVideos(true);
      }
      setError(null);

      try {
        const response = await api.getSourceVideos(source.key, category.id, nextPage, PAGE_SIZE);
        setVideos((current) => (append ? [...current, ...response.results] : response.results));
        setPage(response.page || nextPage);
        setPageCount(response.pageCount || 0);
      } catch {
        setError("加载视频列表失败，请切换视频源或分类重试");
      } finally {
        setLoadingVideos(false);
        setLoadingMore(false);
      }
    },
    []
  );

  const loadCategories = useCallback(
    async (source: ApiSite) => {
      setSelectedSource(source);
      setSelectedCategory(null);
      setCategories([]);
      setVideos([]);
      setPage(1);
      setPageCount(0);
      setLoadingCategories(true);
      setError(null);

      try {
        const nextCategories = await api.getSourceCategories(source.key);
        setCategories(nextCategories);
        const firstCategory = nextCategories[0];
        if (firstCategory) {
          setSelectedCategory(firstCategory);
          await loadCategoryVideos(source, firstCategory, 1, false);
        }
      } catch {
        setError("加载分类失败，请切换视频源重试");
      } finally {
        setLoadingCategories(false);
      }
    },
    [loadCategoryVideos]
  );

  useEffect(() => {
    if (!apiConfigStatus.isConfigured || apiConfigStatus.needsConfiguration) return;

    let cancelled = false;
    const loadSources = async () => {
      setLoadingSources(true);
      setError(null);
      try {
        const nextSources = await api.getResources();
        if (cancelled) return;
        setSources(nextSources);
        if (nextSources[0]) {
          await loadCategories(nextSources[0]);
        } else {
          // 没有可用的源(例如只剩被隐藏的成人源):清掉之前选中的源及其内容
          setSelectedSource(null);
          setSelectedCategory(null);
          setCategories([]);
          setVideos([]);
        }
      } catch {
        if (!cancelled) {
          setError("加载视频源失败，请检查订阅或服务器配置");
        }
      } finally {
        if (!cancelled) {
          setLoadingSources(false);
        }
      }
    };

    loadSources();
    return () => {
      cancelled = true;
    };
  }, [apiConfigStatus.isConfigured, apiConfigStatus.needsConfiguration, loadCategories, showAdultContent]);

  const handleCategorySelect = (category: SourceCategory) => {
    if (!selectedSource || category.id === selectedCategory?.id) return;
    setSelectedCategory(category);
    loadCategoryVideos(selectedSource, category, 1, false);
  };

  const handleLoadMore = () => {
    if (!selectedSource || !selectedCategory || loadingMore || loadingVideos) return;
    if (pageCount > 0 && page >= pageCount) return;
    loadCategoryVideos(selectedSource, selectedCategory, page + 1, true);
  };

  const handleSearch = async () => {
    const term = keyword.trim();
    if (!term) return;

    setLoadingVideos(true);
    setError(null);
    try {
      const response = selectedSource
        ? await api.searchVideo(term, selectedSource.key)
        : await api.searchVideos(term);
      setVideos(response.results);
      setPage(1);
      setPageCount(1);
    } catch {
      setError("搜索失败，请稍后重试");
    } finally {
      setLoadingVideos(false);
    }
  };

  const renderVideo = ({ item }: { item: SearchResult; index: number }) => (
    <VideoCard
      id={item.id.toString()}
      source={item.source}
      title={item.title}
      poster={item.poster}
      year={item.year}
      sourceName={item.source_name}
      api={api}
    />
  );

  const renderSelector = (
    title: string,
    content: React.ReactNode,
    loading = false
  ) => (
    <View style={styles.selectorBlock}>
      <ThemedText style={styles.selectorTitle}>{title}</ThemedText>
      {loading ? <ActivityIndicator style={styles.selectorLoading} /> : content}
    </View>
  );

  const content = (
    <ThemedView style={[commonStyles.container, styles.container]}>
      {apiConfigStatus.needsConfiguration ? (
        <View style={commonStyles.center}>
          <ThemedText type="subtitle" style={styles.centerText}>
            {getApiConfigErrorMessage(apiConfigStatus)}
          </ThemedText>
        </View>
      ) : (
        <>
          {deviceType === "tv" && <ThemedText style={styles.tvTitle}>源站寻片</ThemedText>}

          <View style={styles.searchRow}>
            <TextInput
              style={styles.searchInput}
              placeholder="搜索当前视频源..."
              placeholderTextColor="#888"
              value={keyword}
              onChangeText={setKeyword}
              onSubmitEditing={handleSearch}
              returnKeyType="search"
            />
            <StyledButton style={styles.searchButton} onPress={handleSearch}>
              <Search size={22} color="white" />
            </StyledButton>
          </View>

          {renderSelector(
            "选择视频源",
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
              {sources.map((source) => (
                <StyledButton
                  key={source.key}
                  text={source.name}
                  isSelected={selectedSource?.key === source.key}
                  onPress={() => loadCategories(source)}
                  style={styles.chip}
                  textStyle={styles.chipText}
                />
              ))}
            </ScrollView>,
            loadingSources
          )}

          {renderSelector(
            "选择分类",
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
              {categories.map((category) => (
                <StyledButton
                  key={category.id}
                  text={category.name}
                  isSelected={selectedCategory?.id === category.id}
                  onPress={() => handleCategorySelect(category)}
                  style={styles.chip}
                  textStyle={styles.chipText}
                />
              ))}
            </ScrollView>,
            loadingCategories
          )}

          <View style={styles.listHeader}>
            <ThemedText style={styles.listTitle}>视频列表</ThemedText>
            {pageCount > 1 && (
              <ThemedText style={styles.pageText}>
                {page}/{pageCount}
              </ThemedText>
            )}
          </View>

          <CustomScrollView
            data={videos}
            renderItem={renderVideo}
            loading={loadingVideos}
            loadingMore={loadingMore}
            error={error}
            onEndReached={handleLoadMore}
            emptyMessage="请选择视频源和分类"
          />
        </>
      )}
    </ThemedView>
  );

  if (deviceType === "tv") {
    return content;
  }

  return (
    <ResponsiveNavigation>
      <ResponsiveHeader title="源站寻片" showBackButton />
      {content}
    </ResponsiveNavigation>
  );
}

const createStyles = (deviceType: string, spacing: number) => {
  const isMobile = deviceType === "mobile";

  return StyleSheet.create({
    container: {
      flex: 1,
      paddingTop: deviceType === "tv" ? 40 : spacing,
    },
    tvTitle: {
      fontSize: 32,
      fontWeight: "bold",
      marginBottom: spacing,
      color: "white",
    },
    centerText: {
      padding: spacing,
      textAlign: "center",
    },
    searchRow: {
      flexDirection: "row",
      alignItems: "center",
      marginBottom: spacing,
    },
    searchInput: {
      flex: 1,
      height: isMobile ? 44 : 50,
      backgroundColor: "#1F2338",
      borderRadius: 8,
      color: "white",
      fontSize: isMobile ? 15 : 18,
      paddingHorizontal: spacing,
      marginRight: spacing / 2,
    },
    searchButton: {
      width: isMobile ? 44 : 50,
      height: isMobile ? 44 : 50,
      borderRadius: 8,
    },
    selectorBlock: {
      marginBottom: spacing,
    },
    selectorTitle: {
      color: "white",
      fontSize: isMobile ? 15 : 18,
      fontWeight: "600",
      marginBottom: spacing / 2,
    },
    selectorLoading: {
      alignSelf: "flex-start",
      marginVertical: spacing / 2,
    },
    chipRow: {
      paddingRight: spacing,
    },
    chip: {
      marginRight: spacing / 2,
      marginBottom: spacing / 2,
    },
    chipText: {
      fontSize: isMobile ? 13 : 15,
    },
    listHeader: {
      flexDirection: "row",
      alignItems: "center",
      justifyContent: "space-between",
      marginBottom: spacing / 2,
    },
    listTitle: {
      color: "white",
      fontSize: isMobile ? 18 : 22,
      fontWeight: "700",
    },
    pageText: {
      color: "#aaa",
      fontSize: isMobile ? 12 : 14,
    },
  });
};
