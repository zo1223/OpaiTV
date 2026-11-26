import React, { useState, useRef, useEffect } from "react";
import { View, TextInput, StyleSheet, Alert, Keyboard, TouchableOpacity } from "react-native";
import { ThemedView } from "@/components/ThemedView";
import { ThemedText } from "@/components/ThemedText";
import VideoCard from "@/components/VideoCard";
import VideoLoadingAnimation from "@/components/VideoLoadingAnimation";
import { api, SearchResult } from "@/services/api";
import { Search, QrCode } from "lucide-react-native";
import { StyledButton } from "@/components/StyledButton";
import { useRemoteControlStore } from "@/stores/remoteControlStore";
import { RemoteControlModal } from "@/components/RemoteControlModal";
import { useSettingsStore } from "@/stores/settingsStore";
import { useRouter } from "expo-router";
import { Colors } from "@/constants/Colors";
import CustomScrollView from "@/components/CustomScrollView";
import { useResponsiveLayout } from "@/hooks/useResponsiveLayout";
import { getCommonResponsiveStyles } from "@/utils/ResponsiveStyles";
import ResponsiveNavigation from "@/components/navigation/ResponsiveNavigation";
import ResponsiveHeader from "@/components/navigation/ResponsiveHeader";
import { DeviceUtils } from "@/utils/DeviceUtils";
import Logger from '@/utils/Logger';
import { mergeSearchResults, resolveSearchIntent } from "@/services/searchIntent";
import {
  IncrementalSearchSnapshot,
  describeSourceProgress,
  summarizeSearchProgress,
} from "@/services/incrementalSearch";
import {
  SearchHistoryEntry,
  clearSearchHistory,
  loadSearchHistory,
  recordSearch,
  removeSearch,
  toTagCloud,
  upsertSearchEntry,
  withoutSearchEntry,
} from "@/services/searchHistory";

const logger = Logger.withTag('SearchScreen');

export default function SearchScreen() {
  const [keyword, setKeyword] = useState("");
  const [results, setResults] = useState<SearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const textInputRef = useRef<TextInput>(null);
  // 搜索代次:加载中输入框仍可再次提交(遥控输入也会触发),
  // 较早发出但较晚返回的搜索不得覆盖较新搜索的结果或提前关掉加载态
  const searchSeqRef = useRef(0);
  const [isInputFocused, setIsInputFocused] = useState(false);
  // 搜索记录(标签云)只存本机；自然语言扩展检索命中时在结果上方给一行提示
  const [history, setHistory] = useState<SearchHistoryEntry[]>([]);
  const [intentHint, setIntentHint] = useState<string | null>(null);
  // 逐源进度:每个源一返回就更新一次,结果同时并入列表
  const [progress, setProgress] = useState<IncrementalSearchSnapshot<SearchResult> | null>(null);
  const { showModal: showRemoteModal, lastMessage, targetPage, clearMessage } = useRemoteControlStore();
  const { remoteInputEnabled, showAdultContent } = useSettingsStore();
  const router = useRouter();

  // 响应式布局配置
  const responsiveConfig = useResponsiveLayout();
  const commonStyles = getCommonResponsiveStyles(responsiveConfig);
  const { deviceType, spacing } = responsiveConfig;

  useEffect(() => {
    if (lastMessage && targetPage === 'search') {
      logger.debug("Received remote input:", lastMessage);
      const realMessage = lastMessage.text;
      setKeyword(realMessage);
      handleSearch(realMessage);
      clearMessage(); // Clear the message after processing
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lastMessage, targetPage]);

  useEffect(() => {
    let active = true;
    loadSearchHistory()
      .then((entries) => {
        if (active && entries.length > 0) {
          setHistory(entries);
        }
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);

  // 成人内容开关关闭后,已经显示出来的结果里不能留着成人源的条目(新的搜索由 api 层过滤)
  useEffect(() => {
    setResults((current) => current.filter((item) => !api.isAdultItemHidden(item)));
  }, [showAdultContent]);

  // useEffect(() => {
  //   // Focus the text input when the screen loads
  //   const timer = setTimeout(() => {
  //     textInputRef.current?.focus();
  //   }, 200);
  //   return () => clearTimeout(timer);
  // }, []);

  const handleSearch = async (searchText?: string) => {
    const term = typeof searchText === "string" ? searchText : keyword;
    if (!term.trim()) {
      Keyboard.dismiss();
      return;
    }
    Keyboard.dismiss();
    const seq = ++searchSeqRef.current;
    setLoading(true);
    setError(null);
    setIntentHint(null);
    // 搜索记录只写本机，写入失败不影响搜索
    setHistory((current) => upsertSearchEntry(current, term));
    recordSearch(term).catch(() => {});
    // 自然语言(天气/心情/场景)说法命中时，用题材词走同一条搜索路径再查一轮(成人过滤等逻辑一致)，
    // 与关键词结果合并去重；这一轮失败只当作没有结果，不影响关键词搜索。
    const intent = resolveSearchIntent(term);
    const intentGenres = intent ? intent.genres.slice(0, 3) : [];
    const intentLabel = intent ? `已按“${intent.label}”扩展检索：${intentGenres.join(" / ")}` : null;
    const intentSearch: Promise<SearchResult[]> =
      intentGenres.length > 0
        ? Promise.all(
            intentGenres.map((genre) =>
              Promise.resolve()
                .then(() => api.searchVideos(genre))
                .then((response) => response?.results ?? [])
                .catch(() => [] as SearchResult[])
            )
          ).then((lists) => lists.flat())
        : Promise.resolve([]);
    // 关键词搜索逐源进行:搜到一个源就先显示一个源的结果,不等全部搜完;
    // 扩展检索(题材词)的结果到了再并进来。旧搜索的迟到回调一律作废。
    let keywordResults: SearchResult[] = [];
    let intentResults: SearchResult[] = [];
    const isStale = () => searchSeqRef.current !== seq;
    const publish = () => {
      const merged = mergeSearchResults(keywordResults, intentResults);
      setResults(merged);
      if (intentResults.length > 0 && merged.length > 0) {
        setIntentHint(intentLabel);
      }
      return merged;
    };
    setResults([]);
    setProgress(null);
    const intentDone = intentSearch.then((extra) => {
      if (isStale()) return;
      intentResults = extra;
      if (extra.length > 0) {
        publish();
      }
    });
    try {
      await api.searchVideosIncremental(
        term,
        (snapshot) => {
          if (isStale()) return;
          keywordResults = snapshot.results;
          setProgress(snapshot);
          publish();
        },
        isStale
      );
      if (isStale()) return;
      await intentDone;
      if (isStale()) return;
      if (publish().length === 0) {
        setError("没有找到相关内容");
      }
    } catch (err) {
      logger.info("Search failed:", err);
      if (isStale()) return;
      await intentDone;
      if (isStale()) return;
      if (publish().length === 0) {
        setError("搜索失败，请稍后重试。");
      }
    } finally {
      if (searchSeqRef.current === seq) {
        setLoading(false);
      }
    }
  };

  const onSearchPress = () => handleSearch();

  const handleHistoryPress = (query: string) => {
    setKeyword(query);
    handleSearch(query);
  };

  const handleHistoryRemove = (query: string) => {
    setHistory((current) => withoutSearchEntry(current, query));
    removeSearch(query).catch(() => {});
  };

  const handleHistoryClear = () => {
    setHistory([]);
    clearSearchHistory().catch(() => {});
  };

  // 关键词为空且没有结果时，在搜索框下方显示搜索记录标签云
  const showHistoryCloud = !loading && keyword.trim().length === 0 && results.length === 0 && history.length > 0;

  const handleQrPress = () => {
    if (!remoteInputEnabled) {
      Alert.alert("远程输入未启用", "请先在设置页面中启用远程输入功能", [
        { text: "取消", style: "cancel" },
        { text: "去设置", onPress: () => router.push("/settings") },
      ]);
      return;
    }
    showRemoteModal('search');
  };

  // 搜索结果点一下先进详情页(不传 directPlay),在详情页里再点播放
  const renderItem = ({ item }: { item: SearchResult; index: number }) => (
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

  // 动态样式
  const dynamicStyles = createResponsiveStyles(deviceType, spacing);

  const renderSearchContent = () => (
    <>
      <View style={dynamicStyles.searchContainer}>
        <TouchableOpacity
          activeOpacity={1}
          style={[
            dynamicStyles.inputContainer,
            {
              borderColor: isInputFocused ? Colors.dark.primary : "transparent",
            },
          ]}
          onPress={() => textInputRef.current?.focus()}
        >
          <TextInput
            ref={textInputRef}
            style={dynamicStyles.input}
            placeholder="搜索电影、剧集..."
            placeholderTextColor="#888"
            value={keyword}
            onChangeText={setKeyword}
            onSubmitEditing={onSearchPress}
            onFocus={() => setIsInputFocused(true)}
            onBlur={() => setIsInputFocused(false)}
            returnKeyType="search"
          />
        </TouchableOpacity>
        <StyledButton style={dynamicStyles.searchButton} onPress={onSearchPress}>
          <Search size={deviceType === 'mobile' ? 20 : 24} color="white" />
        </StyledButton>
        {deviceType !== 'mobile' && (
          <StyledButton style={dynamicStyles.qrButton} onPress={handleQrPress}>
            <QrCode size={deviceType === 'tv' ? 24 : 20} color="white" />
          </StyledButton>
        )}
      </View>

      {showHistoryCloud && (
        <View style={dynamicStyles.historyContainer}>
          <View style={dynamicStyles.historyHeader}>
            <ThemedText style={dynamicStyles.historyTitle}>搜索记录</ThemedText>
            <TouchableOpacity
              onPress={handleHistoryClear}
              accessibilityRole="button"
              accessibilityLabel="清空搜索记录"
              hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
            >
              <ThemedText style={dynamicStyles.historyClear}>清空</ThemedText>
            </TouchableOpacity>
          </View>
          <View style={dynamicStyles.historyTags}>
            {toTagCloud(history).map((tag) => (
              <View key={tag.query} style={dynamicStyles.historyTag}>
                <TouchableOpacity
                  onPress={() => handleHistoryPress(tag.query)}
                  accessibilityRole="button"
                  accessibilityLabel={`搜索 ${tag.query}`}
                >
                  <ThemedText
                    numberOfLines={1}
                    style={[dynamicStyles.historyTagText, { fontSize: tag.fontSize, opacity: 0.7 + 0.3 * tag.weight }]}
                  >
                    {tag.query}
                  </ThemedText>
                </TouchableOpacity>
                <TouchableOpacity
                  onPress={() => handleHistoryRemove(tag.query)}
                  accessibilityRole="button"
                  accessibilityLabel={`删除搜索记录 ${tag.query}`}
                  hitSlop={{ top: 8, bottom: 8, left: 4, right: 8 }}
                >
                  <ThemedText style={dynamicStyles.historyTagRemove}>×</ThemedText>
                </TouchableOpacity>
              </View>
            ))}
          </View>
        </View>
      )}

      {progress && progress.total > 0 ? (
        <View style={dynamicStyles.progressContainer} accessibilityLabel="片源搜索进度">
          <ThemedText style={dynamicStyles.progressSummary}>{summarizeSearchProgress(progress)}</ThemedText>
          <View style={dynamicStyles.progressSources}>
            {progress.progress.map((item) => (
              <ThemedText
                key={item.key}
                numberOfLines={1}
                style={[
                  dynamicStyles.progressSource,
                  item.status === "done" && item.count > 0 && dynamicStyles.progressSourceHit,
                  (item.status === "failed" || item.status === "timeout") && dynamicStyles.progressSourceFailed,
                ]}
              >
                {describeSourceProgress(item)}
              </ThemedText>
            ))}
          </View>
        </View>
      ) : null}

      {loading && results.length === 0 ? (
        <VideoLoadingAnimation showProgressBar={false} />
      ) : error ? (
        <View style={[commonStyles.center, { flex: 1 }]}>
          <ThemedText style={dynamicStyles.errorText}>{error}</ThemedText>
        </View>
      ) : (
        <>
          {intentHint ? <ThemedText style={dynamicStyles.intentHint}>{intentHint}</ThemedText> : null}
          <CustomScrollView
            data={results}
            renderItem={renderItem}
            loading={false}
            error={error}
            emptyMessage="输入关键词开始搜索"
          />
        </>
      )}
      <RemoteControlModal />
    </>
  );

  const content = (
    <ThemedView style={[commonStyles.container, dynamicStyles.container]}>
      {renderSearchContent()}
    </ThemedView>
  );

  // 根据设备类型决定是否包装在响应式导航中
  if (deviceType === 'tv') {
    return content;
  }

  return (
    <ResponsiveNavigation>
      <ResponsiveHeader title="搜索" showBackButton />
      {content}
    </ResponsiveNavigation>
  );
}

const createResponsiveStyles = (deviceType: string, spacing: number) => {
  const isMobile = deviceType === 'mobile';
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();

  return StyleSheet.create({
    container: {
      flex: 1,
      paddingTop: deviceType === 'tv' ? 50 : 0,
    },
    searchContainer: {
      flexDirection: "row",
      paddingHorizontal: spacing,
      marginBottom: spacing,
      alignItems: "center",
      paddingTop: isMobile ? spacing / 2 : 0,
    },
    inputContainer: {
      flex: 1,
      height: isMobile ? minTouchTarget : 50,
      backgroundColor: "#1F2338",
      borderRadius: 14,
      marginRight: spacing / 2,
      borderWidth: 2,
      borderColor: "transparent",
      justifyContent: "center",
    },
    input: {
      flex: 1,
      paddingHorizontal: spacing,
      color: "white",
      fontSize: isMobile ? 16 : 18,
    },
    searchButton: {
      width: isMobile ? minTouchTarget : 50,
      height: isMobile ? minTouchTarget : 50,
      justifyContent: "center",
      alignItems: "center",
      borderRadius: 14,
      marginRight: deviceType !== 'mobile' ? spacing / 2 : 0,
    },
    qrButton: {
      width: isMobile ? minTouchTarget : 50,
      height: isMobile ? minTouchTarget : 50,
      justifyContent: "center",
      alignItems: "center",
      borderRadius: 14,
    },
    errorText: {
      color: "red",
      fontSize: isMobile ? 14 : 16,
      textAlign: "center",
    },
    historyContainer: {
      paddingHorizontal: spacing,
      marginBottom: spacing,
    },
    historyHeader: {
      flexDirection: "row",
      alignItems: "center",
      justifyContent: "space-between",
      marginBottom: spacing / 2,
    },
    historyTitle: {
      color: "#9AA1B8",
      fontSize: isMobile ? 13 : 15,
    },
    historyClear: {
      color: "#9AA1B8",
      fontSize: isMobile ? 13 : 15,
    },
    historyTags: {
      flexDirection: "row",
      flexWrap: "wrap",
      alignItems: "center",
    },
    historyTag: {
      flexDirection: "row",
      alignItems: "center",
      backgroundColor: "#1F2338",
      borderRadius: 999,
      borderWidth: 1,
      borderColor: "rgba(255,255,255,0.08)",
      paddingLeft: 12,
      paddingRight: 8,
      paddingVertical: 6,
      marginRight: 8,
      marginBottom: 8,
      maxWidth: "100%",
    },
    historyTagText: {
      color: "white",
      flexShrink: 1,
    },
    historyTagRemove: {
      color: "#888",
      fontSize: 16,
      marginLeft: 6,
    },
    progressContainer: {
      paddingHorizontal: spacing,
      marginBottom: spacing / 2,
    },
    progressSummary: {
      color: "#ccc",
      fontSize: isMobile ? 12 : 14,
      marginBottom: 4,
    },
    progressSources: {
      flexDirection: "row",
      flexWrap: "wrap",
    },
    progressSource: {
      color: "#888",
      fontSize: isMobile ? 11 : 13,
      marginRight: 10,
      marginBottom: 2,
    },
    progressSourceHit: {
      color: "#6fcf97",
    },
    progressSourceFailed: {
      color: "#eb5757",
    },
    intentHint: {
      color: "#aaa",
      fontSize: isMobile ? 12 : 14,
      paddingHorizontal: spacing,
      marginBottom: spacing / 2,
    },
  });
};
