import React, { useEffect, useState } from "react";
import { StyleSheet, TextInput, View } from "react-native";
import { ThemedText } from "@/components/ThemedText";
import { StyledButton } from "@/components/StyledButton";
import { SettingsSection } from "./SettingsSection";
import { useSettingsStore } from "@/stores/settingsStore";

interface TmdbSectionProps {
  onFocus?: () => void;
}

// TMDB 元数据:填了自己的 Key 之后,详情页用 TMDB 补全海报、简介、评分、年份。留空则完全不启用
export const TmdbSection: React.FC<TmdbSectionProps> = ({ onFocus }) => {
  const { tmdbApiKey, setTmdbApiKey } = useSettingsStore();
  const [value, setValue] = useState(tmdbApiKey);
  const [message, setMessage] = useState("");

  useEffect(() => {
    setValue(tmdbApiKey);
  }, [tmdbApiKey]);

  const save = async (next: string) => {
    try {
      await setTmdbApiKey(next);
      setMessage(next.trim() ? "已保存到本机" : "已清除");
    } catch {
      setMessage("保存失败,请重试");
    }
  };

  return (
    <SettingsSection>
      <ThemedText style={styles.sectionTitle}>TMDB 元数据</ThemedText>
      <ThemedText style={styles.status}>
        {tmdbApiKey ? "已填写:详情页会用 TMDB 补全海报、简介、评分、年份" : "未填写:不启用,不会请求 TMDB"}
      </ThemedText>
      <View style={styles.inputRow}>
        <TextInput
          style={styles.input}
          value={value}
          onChangeText={(text) => {
            setValue(text);
            setMessage("");
          }}
          onFocus={onFocus}
          onSubmitEditing={() => void save(value)}
          placeholder="TMDB API Key 或 Read Access Token"
          placeholderTextColor="#666"
          autoCapitalize="none"
          autoCorrect={false}
          secureTextEntry
          accessibilityLabel="TMDB Key"
        />
        <StyledButton text="保存" onPress={() => void save(value)} onFocus={onFocus} style={styles.button} />
        <StyledButton text="清除" onPress={() => void save("")} onFocus={onFocus} style={styles.button} />
      </View>
      {message ? <ThemedText style={styles.message}>{message}</ThemedText> : null}
      <ThemedText style={styles.hint}>
        Key 只保存在本机,只会发给 TMDB 官方接口;填写后打开详情页时会把片名发给 TMDB 查询;留空则不启用。
      </ThemedText>
    </SettingsSection>
  );
};

const styles = StyleSheet.create({
  sectionTitle: {
    fontSize: 16,
    fontWeight: "bold",
    marginBottom: 6,
  },
  status: {
    fontSize: 13,
    color: "#9AA1B8",
    marginBottom: 8,
  },
  inputRow: {
    flexDirection: "row",
    alignItems: "center",
  },
  input: {
    flex: 1,
    height: 44,
    borderWidth: 1,
    borderColor: "#2A2F47",
    borderRadius: 8,
    paddingHorizontal: 12,
    color: "#fff",
    marginRight: 8,
  },
  button: {
    minWidth: 64,
    marginRight: 6,
  },
  message: {
    fontSize: 13,
    color: "#e0a030",
    marginTop: 6,
  },
  hint: {
    fontSize: 12,
    color: "#9AA1B8",
    marginTop: 8,
  },
});
