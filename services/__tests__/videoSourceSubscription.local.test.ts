import { API } from "../api";
import { fetchVideoSourceSubscription } from "../videoSourceSubscription";

jest.mock("@react-native-async-storage/async-storage", () => ({
  setItem: jest.fn(),
}));

const localServerUrl = "http://127.0.0.1:18080";

const describeIfLocalServer = process.env.ORION_LOCAL_MOCK === "1" ? describe : describe.skip;

describeIfLocalServer("local video source subscription flow", () => {
  it("loads JSON subscription, searches, and loads detail from a local Apple CMS mock", async () => {
    const config = await fetchVideoSourceSubscription(`${localServerUrl}/sub.json`);
    expect(config.sources).toEqual([
      {
        key: "localtest",
        name: "本地测试源",
        api: `${localServerUrl}/api.php/provide/vod`,
        detail: undefined,
      },
    ]);

    const api = new API();
    api.setSubscribedVideoSources(config.sources);

    const search = await api.searchVideos("本地测试片");
    expect(search.results).toHaveLength(1);
    expect(search.results[0]).toMatchObject({
      id: 101,
      title: "本地测试片",
      source: "localtest",
      source_name: "本地测试源",
      episodes: [`${localServerUrl}/video/1.m3u8`, `${localServerUrl}/video/2.m3u8`],
    });

    const detail = await api.getVideoDetail("localtest", "101");
    expect(detail).toMatchObject({
      id: "101",
      title: "本地测试片",
      source: "localtest",
      source_name: "本地测试源",
      desc: "这是本地 mock 苹果 CMS 接口返回的详情。",
    });
  });

  it("loads Base58 subscription content from the local mock", async () => {
    const config = await fetchVideoSourceSubscription(`${localServerUrl}/sub58`);
    expect(config.sources[0].key).toBe("localtest");
    expect(config.sources[0].name).toBe("本地测试源");
  });
});
