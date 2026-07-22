import { create } from "zustand";
import { useSettingsStore } from "@/stores/settingsStore";
import useDetailStore, { sourcesSelector } from "./detailStore";

interface SourceState {
  toggleResourceEnabled: (resourceKey: string) => void;
}

const useSourceStore = create<SourceState>((set, get) => ({
  toggleResourceEnabled: (resourceKey: string) => {
    const { videoSource, setVideoSource } = useSettingsStore.getState();
    const isEnabled = videoSource.sources[resourceKey];
    const newEnabledSources = { ...videoSource.sources, [resourceKey]: !isEnabled };

    setVideoSource({
      enabledAll: Object.values(newEnabledSources).every((enabled) => enabled),
      sources: newEnabledSources,
    });
  },
}));

export const useSources = () => useDetailStore(sourcesSelector);

export default useSourceStore;