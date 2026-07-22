import { create } from "zustand";
import Toast from "react-native-toast-message";
import { AVPlaybackStatus, Video } from "expo-av";
import { RefObject } from "react";
import { PlayRecord, PlayRecordManager, PlayerSettingsManager } from "@/services/storage";
import { getAdRangesFromM3U8, M3U8AdRange } from "@/services/m3u8";
import { useSettingsStore } from "@/stores/settingsStore";
import useDetailStore, { episodesSelectorBySource } from "./detailStore";
import { getSearchResultSourceKey } from "@/services/videoSourceSubscription";
import Logger from '@/utils/Logger';

const logger = Logger.withTag('PlayerStore');

interface Episode {
  url: string;
  title: string;
}

interface PlayerState {
  videoRef: RefObject<Video> | null;
  currentEpisodeIndex: number;
  episodes: Episode[];
  status: AVPlaybackStatus | null;
  isLoading: boolean;
  showControls: boolean;
  showEpisodeModal: boolean;
  showSourceModal: boolean;
  showSpeedModal: boolean;
  showNextEpisodeOverlay: boolean;
  isSeeking: boolean;
  seekPosition: number;
  progressPosition: number;
  initialPosition: number;
  adRanges: M3U8AdRange[];
  lastSkippedAdRangeEndMillis?: number;
  playbackRate: number;
  introEndTime?: number;
  outroStartTime?: number;
  setVideoRef: (ref: RefObject<Video>) => void;
  loadVideo: (options: {
    source: string;
    id: string;
    title: string;
    episodeIndex: number;
    position?: number;
  }) => Promise<void>;
  playEpisode: (index: number) => void;
  togglePlayPause: () => void;
  seek: (duration: number) => void;
  handlePlaybackStatusUpdate: (newStatus: AVPlaybackStatus) => void;
  setLoading: (loading: boolean) => void;
  setShowControls: (show: boolean) => void;
  setShowEpisodeModal: (show: boolean) => void;
  setShowSourceModal: (show: boolean) => void;
  setShowSpeedModal: (show: boolean) => void;
  setShowNextEpisodeOverlay: (show: boolean) => void;
  setPlaybackRate: (rate: number) => void;
  setIntroEndTime: () => void;
  setOutroStartTime: () => void;
  reset: () => void;
  _seekTimeout?: NodeJS.Timeout;
  _recordSaveTimeout?: NodeJS.Timeout;
  _isRecordSaveThrottled: boolean;
  _playSeq: number;
  _episodeSwitching: boolean;
  // Internal helper
  _savePlayRecord: (updates?: Partial<PlayRecord>, options?: { immediate?: boolean }) => void;
  handleVideoError: (errorType: 'ssl' | 'network' | 'other', failedUrl: string) => Promise<void>;
}

const usePlayerStore = create<PlayerState>((set, get) => ({
  videoRef: null,
  episodes: [],
  currentEpisodeIndex: -1,
  status: null,
  isLoading: true,
  showControls: false,
  showEpisodeModal: false,
  showSourceModal: false,
  showSpeedModal: false,
  showNextEpisodeOverlay: false,
  isSeeking: false,
  seekPosition: 0,
  progressPosition: 0,
  initialPosition: 0,
  adRanges: [],
  lastSkippedAdRangeEndMillis: undefined,
  playbackRate: 1.0,
  introEndTime: undefined,
  outroStartTime: undefined,
  _seekTimeout: undefined,
  _recordSaveTimeout: undefined,
  _isRecordSaveThrottled: false,
  _playSeq: 0,
  _episodeSwitching: false,

  setVideoRef: (ref) => set({ videoRef: ref }),

  loadVideo: async ({ source, id, episodeIndex, position, title }) => {
    const perfStart = performance.now();
    logger.info(`[PERF] PlayerStore.loadVideo START - source: ${source}, id: ${id}, title: ${title}`);
    
    let detail = useDetailStore.getState().detail;
    let episodes: string[] = [];
    
    // 如果有detail，使用detail的source获取episodes；否则使用传入的source
    if (detail && detail.source) {
      logger.info(`[INFO] Using existing detail source "${detail.source}" to get episodes`);
      episodes = episodesSelectorBySource(detail.source, detail.playback_source)(useDetailStore.getState());
    } else {
      logger.info(`[INFO] No existing detail, using provided source "${source}" to get episodes`);
      episodes = episodesSelectorBySource(source)(useDetailStore.getState());
    }

    set({
      isLoading: true,
      // 作废仍在途的 playEpisode,并抑制换源/加载期间旧视频残留的片尾自动切集
      _playSeq: get()._playSeq + 1,
      _episodeSwitching: true,
    });

    const detailStoreState = useDetailStore.getState();
    // 同名不同片必须重新初始化:缓存仅在请求的 (source,id) 就是当前 detail
    // 或属于同一次搜索会话(searchResults 中存在)时才可复用
    const requestMatchesCache =
      !!detail &&
      detail.title === title &&
      ((detail.source === source && String(detail.id) === id) ||
        detailStoreState.searchResults.some((r) => r.source === source && String(r.id) === id));
    const needsDetailInit = !requestMatchesCache || !episodes || episodes.length === 0;
    logger.info(`[PERF] Detail check - needsInit: ${needsDetailInit}, hasDetail: ${!!detail}, episodesCount: ${episodes?.length || 0}`);

    if (needsDetailInit) {
      const detailInitStart = performance.now();
      logger.info(`[PERF] DetailStore.init START - ${title}`);
      
      await useDetailStore.getState().init(title, source, id);
      
      const detailInitEnd = performance.now();
      logger.info(`[PERF] DetailStore.init END - took ${(detailInitEnd - detailInitStart).toFixed(2)}ms`);
      
      detail = useDetailStore.getState().detail;
      
      if (!detail) {
        logger.error(`[ERROR] Detail not found after initialization for "${title}" (source: ${source}, id: ${id})`);
        
        // 检查DetailStore的错误状态
        const detailStoreState = useDetailStore.getState();
        if (detailStoreState.error) {
          logger.error(`[ERROR] DetailStore error: ${detailStoreState.error}`);
          set({ 
            isLoading: false,
            // 可以选择在这里设置一个错误状态，但playerStore可能没有error字段
          });
        } else {
          logger.error(`[ERROR] DetailStore init completed but no detail found and no error reported`);
          set({ isLoading: false });
        }
        return;
      }
      
      // 使用DetailStore找到的实际source来获取episodes，而不是原始的preferredSource
      logger.info(`[INFO] Using actual source "${detail.source}" instead of preferred source "${source}"`);  
      episodes = episodesSelectorBySource(detail.source, detail.playback_source)(useDetailStore.getState());
      
      if (!episodes || episodes.length === 0) {
        logger.error(`[ERROR] No episodes found for "${title}" from source "${detail.source}" (${detail.source_name})`);
        
        // 尝试从searchResults中直接获取episodes
        const detailStoreState = useDetailStore.getState();
        logger.info(`[INFO] Available sources in searchResults: ${detailStoreState.searchResults.map(r => `${r.source}(${r.episodes?.length || 0} episodes)`).join(', ')}`);
        
        // 如果当前source没有episodes，尝试使用第一个有episodes的source
        const sourceWithEpisodes = detailStoreState.searchResults.find(r => r.episodes && r.episodes.length > 0);
        if (sourceWithEpisodes) {
          logger.info(`[FALLBACK] Using alternative source "${sourceWithEpisodes.source}" with ${sourceWithEpisodes.episodes.length} episodes`);
          episodes = sourceWithEpisodes.episodes;
          // 更新detail为有episodes的source
          detail = sourceWithEpisodes;
        } else {
          logger.error(`[ERROR] No source with episodes found in searchResults`);
          set({ isLoading: false });
          return;
        }
      }
      
      logger.info(`[SUCCESS] Detail and episodes loaded - source: ${detail.source_name}, episodes: ${episodes.length}`);
    } else {
      logger.info(`[PERF] Skipping DetailStore.init - using cached data`);

      // 即使是缓存的数据，也要确保使用正确的source获取episodes
      if (detail && detail.source && detail.source !== source) {
        const requestedResult = useDetailStore
          .getState()
          .searchResults.find(
            (r) => r.source === source && String(r.id) === id && r.episodes && r.episodes.length > 0
          );
        if (requestedResult) {
          logger.info(
            `[INFO] Cached detail source "${detail.source}" differs from provided source "${source}", switching to requested source`
          );
          detail = requestedResult;
          useDetailStore.setState({ detail: requestedResult });
          episodes = requestedResult.episodes;
        } else {
          logger.warn(
            `[WARN] Provided source "${source}" has no playable episodes, keeping cached detail source "${detail.source}"`
          );
          episodes = episodesSelectorBySource(detail.source, detail.playback_source)(useDetailStore.getState());
        }
      }
    }

    // 最终验证：确保我们有有效的detail和episodes数据
    if (!detail) {
      logger.error(`[ERROR] Final check failed: detail is null`);
      set({ isLoading: false });
      return;
    }
    
    if (!episodes || episodes.length === 0) {
      logger.error(`[ERROR] Final check failed: no episodes available for source "${detail.source}" (${detail.source_name})`);
      set({ isLoading: false });
      return;
    }
    
    logger.info(`[SUCCESS] Final validation passed - detail: ${detail.source_name}, episodes: ${episodes.length}`);

    try {
      const storageStart = performance.now();
      logger.info(`[PERF] Storage operations START`);
      
      const playRecord = await PlayRecordManager.get(detail!.source, detail!.id.toString());
      const storagePlayRecordEnd = performance.now();
      logger.info(`[PERF] PlayRecordManager.get took ${(storagePlayRecordEnd - storageStart).toFixed(2)}ms`);
      
      const playerSettings = await PlayerSettingsManager.get(detail!.source, detail!.id.toString());
      const storageEnd = performance.now();
      logger.info(`[PERF] PlayerSettingsManager.get took ${(storageEnd - storagePlayRecordEnd).toFixed(2)}ms`);
      logger.info(`[PERF] Total storage operations took ${(storageEnd - storageStart).toFixed(2)}ms`);
      
      const initialPositionFromRecord = playRecord?.play_time ? playRecord.play_time * 1000 : 0;
      const savedPlaybackRate = playerSettings?.playbackRate || 1.0;
      const adRanges = useSettingsStore.getState().autoSkipAdsEnabled
        ? await getAdRangesFromM3U8(episodes[episodeIndex] || "")
        : [];
      
      const episodesMappingStart = performance.now();
      const mappedEpisodes = episodes.map((ep, index) => ({
        url: ep,
        title: `第 ${index + 1} 集`,
      }));
      const episodesMappingEnd = performance.now();
      logger.info(`[PERF] Episodes mapping (${episodes.length} episodes) took ${(episodesMappingEnd - episodesMappingStart).toFixed(2)}ms`);
      
      set({
        isLoading: false,
        currentEpisodeIndex: episodeIndex,
        initialPosition: position || initialPositionFromRecord,
        adRanges,
        lastSkippedAdRangeEndMillis: undefined,
        playbackRate: savedPlaybackRate,
        episodes: mappedEpisodes,
        introEndTime: playRecord?.introEndTime || playerSettings?.introEndTime,
        outroStartTime: playRecord?.outroStartTime || playerSettings?.outroStartTime,
      });
      
      const perfEnd = performance.now();
      logger.info(`[PERF] PlayerStore.loadVideo COMPLETE - total time: ${(perfEnd - perfStart).toFixed(2)}ms`);
      
    } catch (error) {
      logger.debug("Failed to load play record", error);
      const adRanges = useSettingsStore.getState().autoSkipAdsEnabled
        ? await getAdRangesFromM3U8(episodes[episodeIndex] || "")
        : [];
      const mappedEpisodes = episodes.map((ep, index) => ({
        url: ep,
        title: `Episode ${index + 1}`,
      }));

      set({
        isLoading: false,
        currentEpisodeIndex: episodeIndex,
        initialPosition: position || 0,
        adRanges,
        lastSkippedAdRangeEndMillis: undefined,
        playbackRate: 1.0,
        episodes: mappedEpisodes,
        introEndTime: undefined,
        outroStartTime: undefined,
      });
      
      const perfEnd = performance.now();
      logger.info(`[PERF] PlayerStore.loadVideo ERROR - total time: ${(perfEnd - perfStart).toFixed(2)}ms`);
    }
  },

  playEpisode: async (index) => {
    const { episodes, videoRef } = get();
    if (index >= 0 && index < episodes.length) {
      // 切集守卫:记录本次切换代次;期间旧集残留的片尾事件不得再次触发自动切集
      const seq = get()._playSeq + 1;
      set({ _playSeq: seq, _episodeSwitching: true });
      const adRanges = useSettingsStore.getState().autoSkipAdsEnabled
        ? await getAdRangesFromM3U8(episodes[index].url)
        : [];
      if (get()._playSeq !== seq) {
        return; // 已被更新的切集/加载取代,丢弃本次结果
      }
      set({
        currentEpisodeIndex: index,
        showNextEpisodeOverlay: false,
        initialPosition: 0,
        progressPosition: 0,
        seekPosition: 0,
        adRanges,
        lastSkippedAdRangeEndMillis: undefined,
      });
      try {
        await videoRef?.current?.replayAsync();
      } catch (error) {
        logger.debug("Failed to replay video:", error);
        Toast.show({ type: "error", text1: "播放失败" });
      }
    }
  },

  togglePlayPause: async () => {
    const { status, videoRef } = get();
    if (status?.isLoaded) {
      try {
        if (status.isPlaying) {
          await videoRef?.current?.pauseAsync();
        } else {
          await videoRef?.current?.playAsync();
        }
      } catch (error) {
        logger.debug("Failed to toggle play/pause:", error);
        Toast.show({ type: "error", text1: "操作失败" });
      }
    }
  },

  seek: async (duration) => {
    const { status, videoRef } = get();
    if (!status?.isLoaded || !status.durationMillis) return;

    const newPosition = Math.max(0, Math.min(status.positionMillis + duration, status.durationMillis));
    try {
      await videoRef?.current?.setPositionAsync(newPosition);
    } catch (error) {
      logger.debug("Failed to seek video:", error);
      Toast.show({ type: "error", text1: "快进/快退失败" });
    }

    set({
      isSeeking: true,
      seekPosition: newPosition / status.durationMillis,
    });

    if (get()._seekTimeout) {
      clearTimeout(get()._seekTimeout);
    }
    const timeoutId = setTimeout(() => set({ isSeeking: false }), 1000);
    set({ _seekTimeout: timeoutId });
  },

  setIntroEndTime: () => {
    const { status, introEndTime: existingIntroEndTime } = get();
    const detail = useDetailStore.getState().detail;
    if (!status?.isLoaded || !detail) return;

    if (existingIntroEndTime) {
      // Clear the time
      set({ introEndTime: undefined });
      get()._savePlayRecord({ introEndTime: undefined }, { immediate: true });
      Toast.show({
        type: "info",
        text1: "已清除片头时间",
      });
    } else {
      // Set the time
      const newIntroEndTime = status.positionMillis;
      set({ introEndTime: newIntroEndTime });
      get()._savePlayRecord({ introEndTime: newIntroEndTime }, { immediate: true });
      Toast.show({
        type: "success",
        text1: "设置成功",
        text2: "片头时间已记录。",
      });
    }
  },

  setOutroStartTime: () => {
    const { status, outroStartTime: existingOutroStartTime } = get();
    const detail = useDetailStore.getState().detail;
    if (!status?.isLoaded || !detail) return;

    if (existingOutroStartTime) {
      // Clear the time
      set({ outroStartTime: undefined });
      get()._savePlayRecord({ outroStartTime: undefined }, { immediate: true });
      Toast.show({
        type: "info",
        text1: "已清除片尾时间",
      });
    } else {
      // Set the time
      if (!status.durationMillis) return;
      const newOutroStartTime = status.durationMillis - status.positionMillis;
      set({ outroStartTime: newOutroStartTime });
      get()._savePlayRecord({ outroStartTime: newOutroStartTime }, { immediate: true });
      Toast.show({
        type: "success",
        text1: "设置成功",
        text2: "片尾时间已记录。",
      });
    }
  },

  _savePlayRecord: (updates = {}, options = {}) => {
    const { immediate = false } = options;
    if (!immediate) {
      if (get()._isRecordSaveThrottled) {
        return;
      }
      const timeoutId = setTimeout(() => {
        // Ignore a stale callback if reset() or a newer throttle replaced it.
        if (get()._recordSaveTimeout === timeoutId) {
          set({ _isRecordSaveThrottled: false, _recordSaveTimeout: undefined });
        }
      }, 10000); // 10 seconds
      set({ _isRecordSaveThrottled: true, _recordSaveTimeout: timeoutId });
    }

    const { detail } = useDetailStore.getState();
    const { currentEpisodeIndex, episodes, status, introEndTime, outroStartTime } = get();
    if (detail && status?.isLoaded) {
      const existingRecord = {
        introEndTime,
        outroStartTime,
      };
      PlayRecordManager.save(detail.source, detail.id.toString(), {
        title: detail.title,
        cover: detail.poster || "",
        index: currentEpisodeIndex + 1,
        total_episodes: episodes.length,
        play_time: Math.floor(status.positionMillis / 1000),
        total_time: status.durationMillis ? Math.floor(status.durationMillis / 1000) : 0,
        source_name: detail.source_name,
        year: detail.year || "",
        ...existingRecord,
        ...updates,
      });
    }
  },

  handlePlaybackStatusUpdate: (newStatus) => {
    if (!newStatus.isLoaded) {
      if (newStatus.error) {
        logger.debug(`Playback Error: ${newStatus.error}`);
      }
      set({ status: newStatus });
      return;
    }

    const {
      currentEpisodeIndex,
      episodes,
      outroStartTime,
      playEpisode,
      adRanges,
      lastSkippedAdRangeEndMillis,
      videoRef,
    } = get();
    const detail = useDetailStore.getState().detail;
    const autoSkipAdsEnabled = useSettingsStore.getState().autoSkipAdsEnabled;

    // 切集/换源进行中:旧集残留的片尾状态(接近结尾或 didJustFinish)直接丢弃,
    // 防止二次触发自动切集(连跳 2+ 集)或把旧进度写进新集的播放记录
    if (get()._episodeSwitching) {
      const statusUri = (newStatus as { uri?: string }).uri;
      const currentUrl = episodes[currentEpisodeIndex]?.url;
      const uriMatchesCurrent = !!statusUri && !!currentUrl && statusUri === currentUrl;
      const nearEnd =
        newStatus.didJustFinish ||
        (!!newStatus.durationMillis &&
          (newStatus.positionMillis / newStatus.durationMillis > 0.95 ||
            (!!outroStartTime && newStatus.positionMillis >= newStatus.durationMillis - outroStartTime)));
      if (!uriMatchesCurrent && nearEnd) {
        return;
      }
      set({ _episodeSwitching: false });
    }

    const activeAdRange = autoSkipAdsEnabled
      ? adRanges.find(
          (range) =>
            newStatus.positionMillis >= range.startMillis &&
            newStatus.positionMillis < range.endMillis
        )
      : undefined;

    if (activeAdRange) {
      if (lastSkippedAdRangeEndMillis !== activeAdRange.endMillis) {
        set({ lastSkippedAdRangeEndMillis: activeAdRange.endMillis });
        videoRef?.current?.setPositionAsync(activeAdRange.endMillis).catch((error) => {
          logger.debug("Failed to skip ad range:", error);
        });
        Toast.show({ type: "info", text1: "\u5df2\u8df3\u8fc7\u5e7f\u544a" });
      }
    } else if (lastSkippedAdRangeEndMillis !== undefined) {
      set({ lastSkippedAdRangeEndMillis: undefined });
    }

    if (
      outroStartTime &&
      newStatus.durationMillis &&
      newStatus.positionMillis >= newStatus.durationMillis - outroStartTime
    ) {
      if (currentEpisodeIndex < episodes.length - 1) {
        playEpisode(currentEpisodeIndex + 1);
        return; // Stop further processing for this update
      }
    }

    if (detail && newStatus.durationMillis) {
      get()._savePlayRecord();

      const isNearEnd = newStatus.positionMillis / newStatus.durationMillis > 0.95;
      if (isNearEnd && currentEpisodeIndex < episodes.length - 1 && !outroStartTime) {
        set({ showNextEpisodeOverlay: true });
      } else {
        set({ showNextEpisodeOverlay: false });
      }
    }

    if (newStatus.didJustFinish) {
      if (currentEpisodeIndex < episodes.length - 1) {
        playEpisode(currentEpisodeIndex + 1);
      }
    }

    const progressPosition = newStatus.durationMillis ? newStatus.positionMillis / newStatus.durationMillis : 0;
    set({ status: newStatus, progressPosition });
  },

  setLoading: (loading) => set({ isLoading: loading }),
  setShowControls: (show) => set({ showControls: show }),
  setShowEpisodeModal: (show) => set({ showEpisodeModal: show }),
  setShowSourceModal: (show) => set({ showSourceModal: show }),
  setShowSpeedModal: (show) => set({ showSpeedModal: show }),
  setShowNextEpisodeOverlay: (show) => set({ showNextEpisodeOverlay: show }),

  setPlaybackRate: async (rate) => {
    const { videoRef } = get();
    const detail = useDetailStore.getState().detail;
    
    try {
      await videoRef?.current?.setRateAsync(rate, true);
      set({ playbackRate: rate });
      
      // Save the playback rate preference
      if (detail) {
        await PlayerSettingsManager.save(detail.source, detail.id.toString(), { playbackRate: rate });
      }
    } catch (error) {
      logger.debug("Failed to set playback rate:", error);
    }
  },

  reset: () => {
    const { _seekTimeout, _recordSaveTimeout } = get();
    if (_seekTimeout) {
      clearTimeout(_seekTimeout);
    }
    if (_recordSaveTimeout) {
      clearTimeout(_recordSaveTimeout);
    }
    get()._savePlayRecord({}, { immediate: true });
    set({
      episodes: [],
      currentEpisodeIndex: 0,
      status: null,
      isLoading: true,
      showControls: false,
      showEpisodeModal: false,
      showSourceModal: false,
      showSpeedModal: false,
      showNextEpisodeOverlay: false,
      initialPosition: 0,
      adRanges: [],
      lastSkippedAdRangeEndMillis: undefined,
      playbackRate: 1.0,
      introEndTime: undefined,
      outroStartTime: undefined,
      _seekTimeout: undefined,
      _recordSaveTimeout: undefined,
      _isRecordSaveThrottled: false,
      _playSeq: get()._playSeq + 1,
      _episodeSwitching: false,
    });
  },

  handleVideoError: async (errorType: 'ssl' | 'network' | 'other', failedUrl: string) => {
    const perfStart = performance.now();
    logger.error(`[VIDEO_ERROR] Handling ${errorType} error for URL: ${failedUrl}`);
    
    const detailStoreState = useDetailStore.getState();
    const { detail } = detailStoreState;
    const { currentEpisodeIndex } = get();
    
    if (!detail) {
      logger.error(`[VIDEO_ERROR] Cannot fallback - no detail available`);
      set({ isLoading: false });
      return;
    }
    
    // 标记当前source为失败
    const currentSource = getSearchResultSourceKey(detail);
    const errorReason = `${errorType} error: ${failedUrl.substring(0, 100)}...`;
    useDetailStore.getState().markSourceAsFailed(currentSource, errorReason);
    
    // 获取下一个可用的source
    const fallbackSource = useDetailStore.getState().getNextAvailableSource(currentSource, currentEpisodeIndex);
    
    if (!fallbackSource) {
      logger.error(`[VIDEO_ERROR] No fallback sources available for episode ${currentEpisodeIndex + 1}`);
      Toast.show({ 
        type: "error", 
        text1: "播放失败", 
        text2: "所有播放源都不可用，请稍后重试" 
      });
      set({ isLoading: false });
      return;
    }
    
    logger.info(`[VIDEO_ERROR] Switching to fallback source: ${fallbackSource.source} (${fallbackSource.source_name})`);
    
    try {
      // 更新DetailStore的当前detail为fallback source
      await useDetailStore.getState().setDetail(fallbackSource);
      
      // 重新加载当前集数的episodes
      const newEpisodes = fallbackSource.episodes || [];
      if (newEpisodes.length > currentEpisodeIndex) {
        const mappedEpisodes = newEpisodes.map((ep, index) => ({
          url: ep,
          title: `第 ${index + 1} 集`,
        }));
        
        set({
          episodes: mappedEpisodes,
          isLoading: false, // 让Video组件重新渲染
        });
        
        const perfEnd = performance.now();
        logger.info(`[VIDEO_ERROR] Successfully switched to fallback source in ${(perfEnd - perfStart).toFixed(2)}ms`);
        logger.info(`[VIDEO_ERROR] New episode URL: ${newEpisodes[currentEpisodeIndex].substring(0, 100)}...`);
        
        Toast.show({ 
          type: "success", 
          text1: "已切换播放源", 
          text2: `正在使用 ${fallbackSource.source_name}` 
        });
      } else {
        logger.error(`[VIDEO_ERROR] Fallback source doesn't have episode ${currentEpisodeIndex + 1}`);
        set({ isLoading: false });
      }
    } catch (error) {
      logger.error(`[VIDEO_ERROR] Failed to switch to fallback source:`, error);
      set({ isLoading: false });
    }
  },
}));

export default usePlayerStore;

export const selectCurrentEpisode = (state: PlayerState) => {
  // 增强数据安全性检查
  if (
    state.episodes &&
    Array.isArray(state.episodes) &&
    state.episodes.length > 0 &&
    state.currentEpisodeIndex >= 0 &&
    state.currentEpisodeIndex < state.episodes.length
  ) {
    const episode = state.episodes[state.currentEpisodeIndex];
    // 确保episode有有效的URL
    if (episode && episode.url && episode.url.trim() !== "") {
      return episode;
    } else {
      // 仅在调试模式下打印
      if (__DEV__) {
        logger.debug(`[PERF] selectCurrentEpisode - episode found but invalid URL: ${episode?.url}`);
      }
    }
  } else {
    // 仅在调试模式下打印
    if (__DEV__) {
      logger.debug(`[PERF] selectCurrentEpisode - no valid episode: episodes.length=${state.episodes?.length}, currentIndex=${state.currentEpisodeIndex}`);
    }
  }
  return undefined;
};
