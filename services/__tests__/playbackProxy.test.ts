import {
  isValidProxyAddress,
  normalizePlaybackProxyMode,
  normalizePlaybackProxySettings,
  normalizeProxyAddress,
  resolvePlaybackUrl,
} from "../playbackProxy";

const VIDEO = "https://video.example/a/index.m3u8?token=1&x=2";
const ENCODED = encodeURIComponent(VIDEO);

describe("playbackProxy", () => {
  describe("normalization", () => {
    it("defaults unknown or missing modes to system", () => {
      expect(normalizePlaybackProxyMode(undefined)).toBe("system");
      expect(normalizePlaybackProxyMode(null)).toBe("system");
      expect(normalizePlaybackProxyMode("bogus")).toBe("system");
      expect(normalizePlaybackProxyMode(3)).toBe("system");
      expect(normalizePlaybackProxyMode(" Forced ")).toBe("forced");
      expect(normalizePlaybackProxyMode("direct")).toBe("direct");
      expect(normalizePlaybackProxyMode("system")).toBe("system");
    });

    it("trims the address", () => {
      expect(normalizeProxyAddress("  https://relay.example/?url=  ")).toBe("https://relay.example/?url=");
      expect(normalizeProxyAddress(undefined)).toBe("");
    });

    it("accepts only absolute http(s) addresses with a host", () => {
      expect(isValidProxyAddress("https://relay.example/?url=")).toBe(true);
      expect(isValidProxyAddress("http://192.168.1.2:8080/proxy/{url}")).toBe(true);
      expect(isValidProxyAddress("")).toBe(false);
      expect(isValidProxyAddress("127.0.0.1:7890")).toBe(false);
      expect(isValidProxyAddress("relay.example/?url=")).toBe(false);
      expect(isValidProxyAddress("socks5://127.0.0.1:1080")).toBe(false);
      expect(isValidProxyAddress("https://")).toBe(false);
      expect(isValidProxyAddress("https://relay.example/a b")).toBe(false);
    });

    it("turns forced without a usable address into system", () => {
      expect(normalizePlaybackProxySettings({ mode: "forced", proxyUrl: "" })).toEqual({ mode: "system", proxyUrl: "" });
      expect(normalizePlaybackProxySettings({ mode: "forced", proxyUrl: "127.0.0.1:7890" })).toEqual({
        mode: "system",
        proxyUrl: "127.0.0.1:7890",
      });
      expect(normalizePlaybackProxySettings({ mode: "forced", proxyUrl: " https://relay.example/?url= " })).toEqual({
        mode: "forced",
        proxyUrl: "https://relay.example/?url=",
      });
      expect(normalizePlaybackProxySettings({ mode: "direct", proxyUrl: "" })).toEqual({ mode: "direct", proxyUrl: "" });
      expect(normalizePlaybackProxySettings(null)).toEqual({ mode: "system", proxyUrl: "" });
    });
  });

  describe("resolvePlaybackUrl", () => {
    it("leaves the URL alone in system and direct mode even when an address is saved", () => {
      const proxyUrl = "https://relay.example/?url=";
      expect(resolvePlaybackUrl(VIDEO, { mode: "system", proxyUrl })).toBe(VIDEO);
      expect(resolvePlaybackUrl(VIDEO, { mode: "direct", proxyUrl })).toBe(VIDEO);
      expect(resolvePlaybackUrl(VIDEO, null)).toBe(VIDEO);
      expect(resolvePlaybackUrl(VIDEO, undefined)).toBe(VIDEO);
    });

    it("replaces every {url} placeholder", () => {
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "https://relay.example/p/{url}?src={url}" })).toBe(
        `https://relay.example/p/${ENCODED}?src=${ENCODED}`
      );
    });

    it("appends to an address that ends with =", () => {
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "https://relay.example/?url=" })).toBe(
        `https://relay.example/?url=${ENCODED}`
      );
    });

    it("adds a url query parameter otherwise", () => {
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "https://relay.example/proxy" })).toBe(
        `https://relay.example/proxy?url=${ENCODED}`
      );
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "https://relay.example/proxy?k=v" })).toBe(
        `https://relay.example/proxy?k=v&url=${ENCODED}`
      );
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "https://relay.example/proxy?" })).toBe(
        `https://relay.example/proxy?url=${ENCODED}`
      );
    });

    it("does not wrap an already relayed URL twice", () => {
      const settings = { mode: "forced", proxyUrl: "https://relay.example/?url=" };
      const once = resolvePlaybackUrl(VIDEO, settings);
      expect(resolvePlaybackUrl(once, settings)).toBe(once);

      const placeholder = { mode: "forced", proxyUrl: "https://relay.example/p/{url}" };
      const wrapped = resolvePlaybackUrl(VIDEO, placeholder);
      expect(resolvePlaybackUrl(wrapped, placeholder)).toBe(wrapped);
    });

    it("never rewrites local or non-http URLs", () => {
      const settings = { mode: "forced", proxyUrl: "https://relay.example/?url=" };
      for (const url of [
        "",
        "file:///storage/emulated/0/Movies/a.mp4",
        "content://media/external/video/1",
        "http://localhost:8080/a.m3u8",
        "http://127.0.0.1:9000/a.m3u8",
        "http://[::1]:9000/a.m3u8",
        "not a url",
      ]) {
        expect(resolvePlaybackUrl(url, settings)).toBe(url);
      }
    });

    it("falls back to the original URL when forced has no usable address", () => {
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "" })).toBe(VIDEO);
      expect(resolvePlaybackUrl(VIDEO, { mode: "forced", proxyUrl: "127.0.0.1:7890" })).toBe(VIDEO);
    });
  });
});
