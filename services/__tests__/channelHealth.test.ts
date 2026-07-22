import {
  evaluateChannelHealth,
  labelOf,
  healthColor,
  probeChannel,
  probeChannels,
} from '@/services/channelHealth';

describe('evaluateChannelHealth scoring', () => {
  it('marks unreachable channels as bad with zero score', () => {
    const result = evaluateChannelHealth({ reachable: false, ttfbMs: 1200, throughputBps: 0 });
    expect(result.category).toBe('bad');
    expect(result.score).toBe(0);
    expect(result.detail).toBe('无法连接');
  });

  it('rates download well above bitrate as good', () => {
    const result = evaluateChannelHealth({ reachable: true, ttfbMs: 200, throughputBps: 12_000_000, requiredBps: 2_000_000 });
    expect(result.category).toBe('good');
    expect(result.score).toBeGreaterThanOrEqual(70);
  });

  it('rates download barely meeting bitrate as fair', () => {
    const result = evaluateChannelHealth({ reachable: true, ttfbMs: 300, throughputBps: 2_200_000, requiredBps: 2_000_000 });
    expect(result.category).toBe('fair');
    expect(result.score).toBeGreaterThanOrEqual(40);
    expect(result.score).toBeLessThanOrEqual(69);
  });

  it('rates download below bitrate as bad and flags it', () => {
    const result = evaluateChannelHealth({ reachable: true, ttfbMs: 300, throughputBps: 1_000_000, requiredBps: 4_000_000 });
    expect(result.category).toBe('bad');
    expect(result.detail).toContain('跟不上');
  });

  it('downgrades one notch when first byte is slow', () => {
    const fast = evaluateChannelHealth({ reachable: true, ttfbMs: 200, throughputBps: 12_000_000, requiredBps: 2_000_000 });
    const slow = evaluateChannelHealth({ reachable: true, ttfbMs: 5000, throughputBps: 12_000_000, requiredBps: 2_000_000 });
    expect(fast.category).toBe('good');
    expect(slow.category).toBe('fair');
  });

  it('falls back to absolute throughput when bitrate is unknown', () => {
    expect(evaluateChannelHealth({ reachable: true, ttfbMs: 200, throughputBps: 5_000_000 }).category).toBe('good');
    expect(evaluateChannelHealth({ reachable: true, ttfbMs: 200, throughputBps: 500_000 }).category).toBe('bad');
  });

  it('treats connected-but-unmeasured as fair', () => {
    const result = evaluateChannelHealth({ reachable: true, ttfbMs: 400, throughputBps: 0, requiredBps: 2_000_000 });
    expect(result.category).toBe('fair');
    expect(result.detail).toBe('已连接·未测速');
  });

  it('appends resolution to the detail when present', () => {
    const result = evaluateChannelHealth({
      reachable: true,
      ttfbMs: 200,
      throughputBps: 12_000_000,
      requiredBps: 2_000_000,
      resolution: '1920x1080',
    });
    expect(result.detail).toContain('1920x1080');
  });
});

describe('labelOf / healthColor', () => {
  it('maps each category to a label', () => {
    expect(labelOf('good')).toBe('流畅');
    expect(labelOf('fair')).toBe('一般');
    expect(labelOf('bad')).toBe('不畅');
    expect(labelOf('unknown')).toBe('');
    expect(labelOf(undefined)).toBe('');
  });

  it('maps each category to a distinct color', () => {
    const colors = new Set([healthColor('good'), healthColor('fair'), healthColor('bad'), healthColor('unknown')]);
    expect(colors.size).toBe(4);
  });
});

const makeResponse = (options: {
  ok?: boolean;
  status?: number;
  headers?: Record<string, string>;
  text?: string;
  bytes?: number;
}) => ({
  ok: options.ok ?? true,
  status: options.status ?? 200,
  headers: { get: (key: string) => options.headers?.[key.toLowerCase()] ?? null },
  text: async () => options.text ?? '',
  arrayBuffer: async () => new ArrayBuffer(options.bytes ?? 0),
});

describe('probeChannel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    global.fetch = jest.fn() as any;
  });

  it('skips non-http urls', async () => {
    const result = await probeChannel('rtp://239.0.0.1:5000');
    expect(result.category).toBe('unknown');
    expect(result.detail).toBe('不可探测');
    expect(global.fetch).not.toHaveBeenCalled();
  });

  it('reports bad when the playlist is unreachable', async () => {
    (global.fetch as jest.Mock).mockResolvedValue(makeResponse({ ok: false, status: 404 }));
    const result = await probeChannel('https://live.example/cctv1.m3u8');
    expect(result.category).toBe('bad');
  });

  it('probes an m3u8 media playlist and its first segment', async () => {
    const playlist = ['#EXTM3U', '#EXT-X-TARGETDURATION:10', '#EXTINF:10,', 'seg1.ts'].join('\n');
    (global.fetch as jest.Mock).mockImplementation((url: string) => {
      if (url.includes('.m3u8')) {
        return Promise.resolve(makeResponse({ headers: { 'content-type': 'application/vnd.apple.mpegurl' }, text: playlist }));
      }
      return Promise.resolve(
        makeResponse({ status: 206, headers: { 'content-range': 'bytes 0-1249999/1250000' }, bytes: 1_250_000 })
      );
    });

    const result = await probeChannel('https://live.example/cctv1.m3u8');
    expect(result.category).not.toBe('unknown');
    expect(result.throughputMbps).toBeGreaterThan(0);
    expect(global.fetch).toHaveBeenCalledTimes(2);
  });
});

describe('probeChannels', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    global.fetch = jest.fn() as any;
  });

  it('reports a result for every channel', async () => {
    (global.fetch as jest.Mock).mockResolvedValue(makeResponse({ ok: false, status: 500 }));
    const channels = [
      { id: 'a', url: 'https://live.example/a.m3u8' },
      { id: 'b', url: 'https://live.example/b.m3u8' },
      { id: 'c', url: 'rtp://239.0.0.1:5000' },
    ];
    const seen: Record<string, string> = {};
    await probeChannels(channels, { concurrency: 2, onResult: (id, res) => (seen[id] = res.category) });

    expect(Object.keys(seen).sort()).toEqual(['a', 'b', 'c']);
    expect(seen.c).toBe('unknown');
  });
});
