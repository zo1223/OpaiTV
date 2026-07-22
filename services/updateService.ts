// UpdateService.ts
import * as FileSystem from 'expo-file-system';
import * as IntentLauncher from 'expo-intent-launcher';
// import * as Device from 'expo-device';
import Toast from 'react-native-toast-message';
import { version as currentVersion } from '../package.json';
import { UPDATE_CONFIG } from '../constants/UpdateConfig';
import Logger from '@/utils/Logger';
import { Platform } from 'react-native';

const logger = Logger.withTag('UpdateService');

interface VersionInfo {
  version: string;
  downloadUrl: string;
}

/**
 * 只在 Android 平台使用的常量（iOS 不会走到下载/安装流程）
 */
const ANDROID_MIME_TYPE = 'application/vnd.android.package-archive';

class UpdateService {
  private static instance: UpdateService;
  static getInstance(): UpdateService {
    if (!UpdateService.instance) {
      UpdateService.instance = new UpdateService();
    }
    return UpdateService.instance;
  }

  /** --------------------------------------------------------------
   *  1️⃣ 远程版本检查（保持不变，只是把 fetch 包装成 async/await）
   * --------------------------------------------------------------- */
  async checkVersion(): Promise<VersionInfo> {
    const maxRetries = 3;
    for (let attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 10_000);
        const response = await fetch(UPDATE_CONFIG.GITHUB_RAW_URL, {
          signal: controller.signal,
        });
        clearTimeout(timeoutId);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }
        const remotePackage = await response.json();
        const remoteVersion = remotePackage.version as string;
        return {
          version: remoteVersion,
          downloadUrl: UPDATE_CONFIG.getDownloadUrl(remoteVersion),
        };
      } catch (e) {
        logger.warn(`checkVersion attempt ${attempt}/${maxRetries}`, e);
        if (attempt === maxRetries) {
          Toast.show({
            type: 'error',
            text1: '检查更新失败',
            text2: '无法获取版本信息，请检查网络',
          });
          throw e;
        }
        // 指数退避
        await new Promise(r => setTimeout(r, 2_000 * attempt));
      }
    }
    // 这句永远走不到，仅为 TypeScript 报错
    throw new Error('Unexpected');
  }

  /** --------------------------------------------------------------
   *  2️⃣ 清理旧的 APK 文件（使用 expo-file-system 的 API）
   * --------------------------------------------------------------- */
  private async cleanOldApkFiles(): Promise<void> {
    try {
      const dirUri = FileSystem.documentDirectory; // e.g. file:///data/user/0/.../files/
      if (!dirUri) {
        throw new Error('Document directory is not available');
      }
      const listing = await FileSystem.readDirectoryAsync(dirUri);
      const apkFiles = listing.filter(name => name.startsWith('OrionTV_v') && name.endsWith('.apk'));

      if (apkFiles.length <= 2) return;

      const sorted = apkFiles.sort((a, b) => {
        const numA = parseInt(a.replace(/[^0-9]/g, ''), 10);
        const numB = parseInt(b.replace(/[^0-9]/g, ''), 10);
        return numB - numA; // 倒序（最新在前）
      });

      const stale = sorted.slice(2); // 保留最新的两个
      for (const file of stale) {
        const path = `${dirUri}${file}`;
        try {
          await FileSystem.deleteAsync(path, { idempotent: true });
          logger.debug(`Deleted old APK: ${file}`);
        } catch (e) {
          logger.warn(`Failed to delete ${file}`, e);
        }
      }
    } catch (e) {
      logger.warn('cleanOldApkFiles error', e);
    }
  }

  /** --------------------------------------------------------------
   *  3️⃣ 下载 APK（使用 expo-file-system 的下载 API）
   * --------------------------------------------------------------- */
  async downloadApk(
    url: string,
    onProgress?: (percent: number) => void,
  ): Promise<string> {
    const maxRetries = 3;
    await this.cleanOldApkFiles();

    for (let attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        const timestamp = Date.now();
        const fileName = `OrionTV_v${timestamp}.apk`;
        const fileUri = `${FileSystem.documentDirectory}${fileName}`;

        // expo-file-system 把下载进度回调参数统一为 `{totalBytesWritten, totalBytesExpectedToWrite}`
        const downloadResumable = FileSystem.createDownloadResumable(
          url,
          fileUri,
          {
            // Android 需要在 AndroidManifest 中声明 INTERNET、WRITE_EXTERNAL_STORAGE (API 33+ 使用 MANAGE_EXTERNAL_STORAGE)
            // 这里不使用系统下载管理器，因为我们想自己控制进度回调。
          },
          progress => {
            if (onProgress && progress.totalBytesExpectedToWrite) {
              const percent = Math.floor(
                (progress.totalBytesWritten / progress.totalBytesExpectedToWrite) * 100,
              );
              onProgress(percent);
            }
          },
        );

        const result = await downloadResumable.downloadAsync();
        if (result && result.uri) {
          logger.debug(`APK downloaded to ${result.uri}`);
          return result.uri;
        } else {
          throw new Error('Download failed: No URI available');
        }
      } catch (e) {
        logger.warn(`downloadApk attempt ${attempt}/${maxRetries}`, e);
        if (attempt === maxRetries) {
          Toast.show({
            type: 'error',
            text1: '下载失败',
            text2: 'APK 下载出现错误，请检查网络',
          });
          throw e;
        }
        // 指数退避
        await new Promise(r => setTimeout(r, 3_000 * attempt));
      }
    }
    // 同上，理论不会到这里
    throw new Error('Download failed');
  }

  /** --------------------------------------------------------------
   *  4️⃣ 安装 APK（只在 Android 可用，使用 expo-intent-launcher）
   * --------------------------------------------------------------- */
  async installApk(fileUri: string): Promise<void> {
    // ① 先确认文件存在
    const exists = await FileSystem.getInfoAsync(fileUri);
    if (!exists.exists) {
      throw new Error(`APK not found at ${fileUri}`);
    }

    // ② 把 file:// 转成 content://，Expo‑FileSystem 已经实现了 FileProvider
    const contentUri = await FileSystem.getContentUriAsync(fileUri);

    // ③ 只在 Android 里执行
    if (Platform.OS === 'android') {
      try {
        // FLAG_ACTIVITY_NEW_TASK = 0x10000000 (1)
        // FLAG_GRANT_READ_URI_PERMISSION = 0x00000010
        const flags = 1 | 0x00000010;   // 1 | 16

        await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
          data: contentUri,          // 必须是 content://
          type: ANDROID_MIME_TYPE,   // application/vnd.android.package-archive
          flags,
        });
      } catch (e: any) {
        // 统一错误提示
        if (e.message?.includes('Activity not found')) {
          Toast.show({
            type: 'error',
            text1: '安装失败',
            text2: '系统没有找到可以打开 APK 的应用，请检查系统设置',
          });
        } else if (e.message?.includes('permission')) {
          Toast.show({
            type: 'error',
            text1: '安装失败',
            text2: '请在设置里允许“未知来源”安装',
          });
        } else {
          Toast.show({
            type: 'error',
            text1: '安装失败',
            text2: '未知错误，请稍后重试',
          });
        }
        throw e;
      }
    } else {
      // iOS 设备不支持直接安装 APK
      Toast.show({
        type: 'error',
        text1: '安装失败',
        text2: 'iOS 设备无法直接安装 APK',
      });
      throw new Error('APK install not supported on iOS');
    }
  }

  /** --------------------------------------------------------------
   *  5️⃣ 版本比对工具（保持原来的实现）
   * --------------------------------------------------------------- */
  compareVersions(v1: string, v2: string): number {
    const p1 = v1.split('.').map(Number);
    const p2 = v2.split('.').map(Number);
    for (let i = 0; i < Math.max(p1.length, p2.length); i++) {
      const n1 = p1[i] ?? 0;
      const n2 = p2[i] ?? 0;
      if (n1 > n2) return 1;
      if (n1 < n2) return -1;
    }
    return 0;
  }
  getCurrentVersion(): string {
    return currentVersion;
  }
  isUpdateAvailable(remoteVersion: string): boolean {
    return this.compareVersions(remoteVersion, currentVersion) > 0;
  }
}

/* 单例导出 */
export default UpdateService.getInstance();
