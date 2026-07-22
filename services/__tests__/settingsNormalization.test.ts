import { normalizeApiBaseUrl } from "../settingsNormalization";

describe("settings normalization", () => {
  it("keeps an empty API base URL empty", () => {
    expect(normalizeApiBaseUrl("   ")).toBe("");
  });

  it("adds http for IP addresses and hosts with ports", () => {
    expect(normalizeApiBaseUrl("192.168.1.10:3000/")).toBe("http://192.168.1.10:3000");
  });

  it("adds https for plain domains", () => {
    expect(normalizeApiBaseUrl("example.com/")).toBe("https://example.com");
  });
});
