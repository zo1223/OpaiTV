import React, { useEffect, useMemo, useState } from "react";
import { Image, ScrollView, StyleSheet, Text, TouchableOpacity, View } from "react-native";
import { api } from "@/services/api";
import { loadVisibleRecommendationCandidates } from "@/services/relatedCandidates";
import { useContentVisibilityKey } from "@/hooks/useContentVisibilityKey";
import type { RecommendationCandidate } from "@/services/recommendation";
import { pickRelatedItems, RelatedCurrent, RelatedItem } from "@/services/relatedItems";

/**
 * 详情页“相关推荐”的数据:本机候选池(首页加载过的条目)里与当前条目同系列 / 同分类的。
 * 候选由 services/relatedCandidates 按 18+ 开关与片源模式筛好,这里不发请求、不自己做过滤;
 * 18+ 开关 / 成人源 / 片源模式一变就重新取,旧设置下取到的候选直接作废。
 */
export function useRelatedItems(current: RelatedCurrent | null): RelatedItem[] {
  const visibilityKey = useContentVisibilityKey();
  const [pool, setPool] = useState<{ key: string; items: RecommendationCandidate[] }>({ key: "", items: [] });

  useEffect(() => {
    let cancelled = false;
    loadVisibleRecommendationCandidates()
      .then((items) => {
        if (!cancelled) setPool({ key: visibilityKey, items });
      })
      .catch(() => {
        if (!cancelled) setPool({ key: visibilityKey, items: [] });
      });
    return () => {
      cancelled = true;
    };
  }, [visibilityKey]);

  const title = current?.title;
  const source = current?.source;
  const id = current?.id;
  const typeName = current?.type_name;
  const genreClass = current?.class;
  return useMemo(
    () =>
      title && pool.key === visibilityKey
        ? pickRelatedItems({
            current: { title, source, id, type_name: typeName, class: genreClass },
            candidates: pool.items,
          })
        : [],
    [title, source, id, typeName, genreClass, pool, visibilityKey]
  );
}

interface DetailRelatedRowProps {
  current: RelatedCurrent | null;
  onSelect: (item: RelatedItem) => void;
  spacing: number;
  isTablet?: boolean;
}

/** 详情页集数下方的“相关推荐”:横向一排海报,同系列在前、同分类在后;没有可推荐的就整块不显示。 */
export const DetailRelatedRow: React.FC<DetailRelatedRowProps> = ({ current, onSelect, spacing, isTablet = false }) => {
  const items = useRelatedItems(current);
  if (items.length === 0) {
    return null;
  }
  const posterWidth = isTablet ? 128 : 104;
  const posterHeight = Math.round(posterWidth * 1.5);

  return (
    <View testID="detail-related" style={{ paddingBottom: spacing * 2 }}>
      <Text style={[styles.heading, { fontSize: isTablet ? 18 : 16, marginBottom: spacing / 2 }]}>相关推荐</Text>
      <ScrollView horizontal showsHorizontalScrollIndicator={false}>
        {items.map((item) => (
          <TouchableOpacity
            key={`${item.source}+${item.id}`}
            testID={`detail-related-item-${item.source}-${item.id}`}
            accessibilityRole="button"
            accessibilityLabel={item.title}
            activeOpacity={0.8}
            onPress={() => onSelect(item)}
            style={{ width: posterWidth, marginRight: spacing * 0.75 }}
          >
            <View style={[styles.posterFrame, { width: posterWidth, height: posterHeight }]}>
              <Image source={api.getImageSource(item.poster)} style={styles.poster} resizeMode="cover" />
              {item.relation === "series" ? (
                <View style={styles.badge}>
                  <Text style={styles.badgeText}>同系列</Text>
                </View>
              ) : null}
            </View>
            <Text style={styles.title} numberOfLines={2}>
              {item.title}
            </Text>
            {item.year || item.rate ? (
              <Text style={styles.meta} numberOfLines={1}>
                {[item.year, item.rate ? `${item.rate} 分` : ""].filter(Boolean).join(" · ")}
              </Text>
            ) : null}
          </TouchableOpacity>
        ))}
      </ScrollView>
    </View>
  );
};

const styles = StyleSheet.create({
  heading: {
    fontWeight: "bold",
    color: "white",
  },
  posterFrame: {
    borderRadius: 14,
    overflow: "hidden",
    backgroundColor: "#151828",
  },
  poster: {
    width: "100%",
    height: "100%",
  },
  badge: {
    position: "absolute",
    left: 6,
    top: 6,
    paddingHorizontal: 6,
    paddingVertical: 2,
    borderRadius: 14,
    backgroundColor: "rgba(0,0,0,0.6)",
  },
  badgeText: {
    color: "#fff",
    fontSize: 10,
    fontWeight: "bold",
  },
  title: {
    color: "#fff",
    fontSize: 13,
    lineHeight: 18,
    marginTop: 6,
  },
  meta: {
    color: "#C5CADB",
    fontSize: 11,
    marginTop: 2,
  },
});
