import Toast from 'react-native-toast-message';
import updateService from '../updateService';

jest.mock('expo-file-system', () => ({}));
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
