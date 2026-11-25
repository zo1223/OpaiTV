import React, { useEffect, useState } from "react";
import { StyleSheet, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";
import { api, ApiSite } from "@/services/api";
import { SOURCE_MODES, SOURCE_MODE_HINTS, SOURCE_MODE_LABELS, getSourceMode } from "@/services/sourceMode";

interface SourceModeSectionProps {
  onFocus?: () => void;
}

// 片源模式:每个片源一档(稳定 / 影视 / 全量 / 关闭)。切换立即生效并保存,不需要再点"保存设置"
export const SourceModeSection: React.FC<SourceModeSectionProps> = ({ onFocus }) => {
  const { sourceModes, setSourceMode, subscribedVideoSources, showAdultContent, apiBaseUrl } = useSettingsStore();
  const [sources, setSources] = useState<ApiSite[]>([]);

  useEffect(() => {
    let active = true;
    api
      .getModeConfigurableSources()
      .then((sites) => {
        if (active) setSources(Array.isArray(sites) ? sites : []);
      })
      .catch(() => {
        if (active) setSources([]);
      });
    return () => {
      active = false;
    };
  }, [subscribedVideoSources, showAdultContent, apiBaseUrl]);

  if (sources.length === 0) {
    return null;
  }

  return (
    <SettingsSection>
      <ThemedText style={styles.sectionTitle}>片源模式</ThemedText>
      <ThemedText style={styles.hint}>
        稳定:只用该源最稳的一条线路;影视:只搜影视类;全量:什么都搜;关闭:该源不参与搜索与首页。切换立即生效。
      </ThemedText>
      {sources.map((source) => {
        const mode = getSourceMode(sourceModes, source.key);
        return (
          <View key={source.key} style={styles.row}>
            <View style={styles.info}>
              <ThemedText style={styles.name} numberOfLines={1}>
                {source.name || source.key}
              </ThemedText>
              <ThemedText style={styles.status}>{`${SOURCE_MODE_LABELS[mode]}:${SOURCE_MODE_HINTS[mode]}`}</ThemedText>
            </View>
            <View style={styles.modes}>
              {SOURCE_MODES.map((option) => (
                <StyledButton
                  key={option}
                  text={SOURCE_MODE_LABELS[option]}
                  isSelected={mode === option}
                  onPress={() => void setSourceMode(source.key, option)}
                  onFocus={onFocus}
                  style={styles.modeButton}
                  accessibilityLabel={`${source.name || source.key} ${SOURCE_MODE_LABELS[option]}`}
                />
              ))}
            </View>
          </View>
        );
      })}
    </SettingsSection>
  );
};

const styles = StyleSheet.create({
  sectionTitle: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 6,
  },
  hint: {
    fontSize: 13,
    color: "#9AA1B8",
    marginBottom: 8,
  },
  row: {
    paddingVertical: 8,
  },
  info: {
    marginBottom: 6,
  },
  name: {
    fontSize: 15,
    fontWeight: "600",
  },
  status: {
    fontSize: 12,
    color: "#9AA1B8",
  },
  modes: {
    flexDirection: "row",
    flexWrap: "wrap",
  },
  modeButton: {
    marginRight: 8,
    marginBottom: 4,
  },
});
