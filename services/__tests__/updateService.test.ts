import * as FileSystem from 'expo-file-system';
import Toast from 'react-native-toast-message';
import updateService from '../updateService';

jest.mock('expo-file-system', () => ({
  __esModule: true,
  documentDirectory: 'file:///data/files/',
  readDirectoryAsync: jest.fn(),
  createDownloadResumable: jest.fn(),
}));
jest.mock('expo-intent-launcher', () => ({}));
jest.mock('react-native-toast-message', () => ({ __esModule: true, default: { show: jest.fn() } }));

describe('updateService.checkVersion', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    jest.clearAllMocks();
    // Run the retry back-off (and the abort timeout) immediately.
    jest.spyOn(global, 'setTimeout').mockImplementation(((fn: () => void) => {
      fn();
      return 0;
    }) as unknown as typeof setTimeout);
    global.fetch = jest.fn().mockRejectedValue(new Error('network down'));
  });

  afterEach(() => {
    jest.restoreAllMocks();
    global.fetch = originalFetch;
  });

  it('shows an error toast when a manual check fails', async () => {
    await expect(updateService.checkVersion()).rejects.toThrow('network down');
    expect(global.fetch).toHaveBeenCalledTimes(3);
    expect(Toast.show).toHaveBeenCalledTimes(1);
  });

  it('does not show a toast when a silent check fails', async () => {
    await expect(updateService.checkVersion(true)).rejects.toThrow('network down');
    expect(global.fetch).toHaveBeenCalledTimes(3);
    expect(Toast.show).not.toHaveBeenCalled();
  });
});

describe('updateService.downloadApk', () => {
  const apkUrl = 'https://releases.example/OpaiTV.apk';
  const apkUri = 'file:///data/files/OpaiTV_v1.apk';
  const createDownloadResumable = FileSystem.createDownloadResumable as jest.Mock;

  // expo-file-system resolves for every HTTP status: by then the response body is already in the file.
  const answerWith = (status: number) => {
    const downloadAsync = jest.fn().mockResolvedValue({ uri: apkUri, status, headers: {}, mimeType: null });
    createDownloadResumable.mockReturnValue({ downloadAsync });
    return downloadAsync;
  };

  beforeEach(() => {
    jest.clearAllMocks();
    // Run the retry back-off immediately.
    jest.spyOn(global, 'setTimeout').mockImplementation(((fn: () => void) => {
      fn();
      return 0;
    }) as unknown as typeof setTimeout);
    (FileSystem.readDirectoryAsync as jest.Mock).mockResolvedValue([]);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('returns the downloaded file when the server sends the APK', async () => {
    const downloadAsync = answerWith(200);

    await expect(updateService.downloadApk(apkUrl)).resolves.toBe(apkUri);
    expect(downloadAsync).toHaveBeenCalledTimes(1);
    expect(Toast.show).not.toHaveBeenCalled();
  });

  it('fails instead of returning an HTTP error page as the APK', async () => {
    const downloadAsync = answerWith(404);

    await expect(updateService.downloadApk(apkUrl)).rejects.toThrow('HTTP 404');
    expect(downloadAsync).toHaveBeenCalledTimes(3);
    expect(Toast.show).toHaveBeenCalledTimes(1);
  });
});

describe('updateService version comparison', () => {
  it.each([
    ['1.3.26', '1.3.27', -1],
    ['1.3.27', '1.3.26', 1],
    ['1.3.26', '1.3.26', 0],
    // 按数字比较,不是按字符串
    ['1.3.9', '1.3.10', -1],
    ['1.9.0', '1.10.0', -1],
    ['2.0.0', '1.99.99', 1],
    // v 前缀与空白
    ['v1.3.27', '1.3.26', 1],
    ['v2.0.0', '1.9.9', 1],
    ['V1.3.26', ' 1.3.26 ', 0],
    // 段数不同:缺的段按 0
    ['1.3', '1.3.1', -1],
    ['1.3', '1.3.0', 0],
    ['1.4', '1.3.26', 1],
    // 预发布后缀低于同号正式版,构建号后缀不影响
    ['1.3.27-beta.1', '1.3.27', -1],
    ['1.3.27-beta.1', '1.3.26', 1],
    ['1.3.27+build5', '1.3.27', 0],
  ])('compareVersions(%s, %s) = %d', (a, b, expected) => {
    expect(updateService.compareVersions(a, b)).toBe(expected);
    expect(updateService.compareVersions(b, a)).toBe(expected === 0 ? 0 : -expected);
  });

  it('reports an update only for a strictly newer remote version', () => {
    const [major, minor, patch] = updateService.getCurrentVersion().split('.').map(Number);
    expect(updateService.isUpdateAvailable(`${major}.${minor}.${patch + 1}`)).toBe(true);
    expect(updateService.isUpdateAvailable(`v${major}.${minor}.${patch + 1}`)).toBe(true);
    expect(updateService.isUpdateAvailable(`${major}.${minor}.${patch}`)).toBe(false);
    expect(updateService.isUpdateAvailable(`${major}.${minor}.${Math.max(patch - 1, 0)}`)).toBe(false);
    expect(updateService.isUpdateAvailable('')).toBe(false);
  });
});
