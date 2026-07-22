import { create } from 'zustand';
import updateService from '../services/updateService';
import AsyncStorage from '@react-native-async-storage/async-storage';
import Toast from 'react-native-toast-message';
import Logger from '@/utils/Logger';

const logger = Logger.withTag('UpdateStore');

interface UpdateState {
  // 状态
  updateAvailable: boolean;
  currentVersion: string;
  remoteVersion: string;
  downloadUrl: string;
  downloading: boolean;
  downloadProgress: number;
  downloadedPath: string | null;
  error: string | null;
  lastCheckTime: number;
  skipVersion: string | null;
  showUpdateModal: boolean;
  isLatestVersion: boolean; // 新增：是否已是最新版本
  
  // 操作
  checkForUpdate: (silent?: boolean) => Promise<void>;
  startDownload: () => Promise<void>;
  installUpdate: () => Promise<void>;
  setShowUpdateModal: (show: boolean) => void;
  skipThisVersion: () => Promise<void>;
  reset: () => void;
}

const STORAGE_KEYS = {
  LAST_CHECK_TIME: 'update_last_check_time',
  SKIP_VERSION: 'update_skip_version',
};

export const useUpdateStore = create<UpdateState>((set, get) => ({
  // 初始状态
  updateAvailable: false,
  currentVersion: updateService.getCurrentVersion(),
  remoteVersion: '',
  downloadUrl: '',
  downloading: false,
  downloadProgress: 0,
  downloadedPath: null,
  error: null,
  lastCheckTime: 0,
  skipVersion: null,
  showUpdateModal: false,
  isLatestVersion: false, // 新增：初始为false

  // 检查更新
  checkForUpdate: async (silent = false) => {
    try {
      set({ error: null, isLatestVersion: false });

      // 获取跳过的版本
      const skipVersion = await AsyncStorage.getItem(STORAGE_KEYS.SKIP_VERSION);
      
      const versionInfo = await updateService.checkVersion();
      const isUpdateAvailable = updateService.isUpdateAvailable(versionInfo.version);
      
      // 如果有更新且不是要跳过的版本
      const shouldShowUpdate = isUpdateAvailable && versionInfo.version !== skipVersion;

      // 检查是否已经是最新版本
      const isLatest = !isUpdateAvailable;

      set({
        remoteVersion: versionInfo.version,
        downloadUrl: versionInfo.downloadUrl,
        updateAvailable: isUpdateAvailable,
        lastCheckTime: Date.now(),
        skipVersion,
        showUpdateModal: shouldShowUpdate && !silent,
        isLatestVersion: isLatest,
      });

      // 如果是手动检查且已是最新版本，显示提示
      if (!silent && isLatest) {
        Toast.show({
          type: 'success',
          text1: '已是最新版本',
          text2: `当前版本 v${updateService.getCurrentVersion()} 已是最新版本`,
          visibilityTime: 3000,
        });
      }

      // 保存最后检查时间
      await AsyncStorage.setItem(
        STORAGE_KEYS.LAST_CHECK_TIME,
        Date.now().toString()
      );
    } catch (error) {
      // console.info('检查更新失败:', error);
      set({ 
        error: error instanceof Error ? error.message : '检查更新失败',
        updateAvailable: false,
        isLatestVersion: false,
      });
    }
  },

  // 开始下载
  startDownload: async () => {
    const { downloadUrl } = get();
    
    if (!downloadUrl) {
      set({ error: '下载地址无效' });
      return;
    }

    try {
      set({ 
        downloading: true, 
        downloadProgress: 0, 
        error: null 
      });

      const filePath = await updateService.downloadApk(
        downloadUrl,
        (progress) => {
          set({ downloadProgress: progress });
        }
      );

      set({ 
        downloadedPath: filePath,
        downloading: false,
        downloadProgress: 100,
      });
    } catch (error) {
      // console.info('下载失败:', error);
      set({ 
        downloading: false,
        downloadProgress: 0,
        error: error instanceof Error ? error.message : '下载失败',
      });
    }
  },

  // 安装更新
  installUpdate: async () => {
    const { downloadedPath } = get();
    
    if (!downloadedPath) {
      set({ error: '安装文件不存在' });
      return;
    }

    try {
      await updateService.installApk(downloadedPath);
      // 安装开始后，关闭弹窗
      set({ showUpdateModal: false });
    } catch (error) {
      logger.error('安装失败:', error);
      set({ 
        error: error instanceof Error ? error.message : '安装失败',
      });
    }
  },

  // 设置显示更新弹窗
  setShowUpdateModal: (show: boolean) => {
    set({ showUpdateModal: show });
  },

  // 跳过此版本
  skipThisVersion: async () => {
    const { remoteVersion } = get();
    
    if (remoteVersion) {
      await AsyncStorage.setItem(STORAGE_KEYS.SKIP_VERSION, remoteVersion);
      set({ 
        skipVersion: remoteVersion,
        showUpdateModal: false,
      });
    }
  },

  // 重置状态
  reset: () => {
    set({
      downloading: false,
      downloadProgress: 0,
      downloadedPath: null,
      error: null,
      showUpdateModal: false,
      isLatestVersion: false, // 重置时也要重置这个状态
    });
  },
}));

// 初始化时加载存储的数据
export const initUpdateStore = async () => {
  try {
    const lastCheckTime = await AsyncStorage.getItem(STORAGE_KEYS.LAST_CHECK_TIME);
    const skipVersion = await AsyncStorage.getItem(STORAGE_KEYS.SKIP_VERSION);
    
    useUpdateStore.setState({
      lastCheckTime: lastCheckTime ? parseInt(lastCheckTime, 10) : 0,
      skipVersion: skipVersion || null,
    });
  } catch (error) {
    logger.error('初始化更新存储失败:', error);
  }
};