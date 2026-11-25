import { useMemo } from "react";
import { useSettingsStore } from "@/stores/settingsStore";

/**
 * 影响“哪些内容可见”的设置签名:18+ 开关、成人源、片源模式。
 * 横幅 / 相关推荐按它重新取数;按旧签名算出来的结果在新签名下直接作废,不再显示。
 * 这里只读设置,不实现任何过滤。
 */
export function useContentVisibilityKey(): string {
  const showAdultContent = useSettingsStore((state) => state.showAdultContent);
  const sourceModes = useSettingsStore((state) => state.sourceModes);
  const subscribedVideoSources = useSettingsStore((state) => state.subscribedVideoSources);
  return useMemo(
    () =>
      [
        showAdultContent ? "on" : "off",
        Object.entries(sourceModes || {})
          .map(([key, mode]) => `${key}=${mode}`)
          .sort()
          .join(","),
        (subscribedVideoSources || [])
          .filter((source) => source.adult)
          .map((source) => source.key)
          .join(","),
      ].join("|"),
    [showAdultContent, sourceModes, subscribedVideoSources]
  );
}
