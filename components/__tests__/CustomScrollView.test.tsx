import React from "react";
import { ScrollView, Text, TouchableOpacity } from "react-native";
import renderer, { act } from "react-test-renderer";
import CustomScrollView from "../CustomScrollView";

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: "mobile", columns: 2, cardWidth: 100, cardHeight: 150, spacing: 8 }),
}));

jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ center: {} }),
}));

describe("CustomScrollView", () => {
  it("only mounts the scroll-to-top button while the list is scrolled down", () => {
    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(
        <CustomScrollView data={[1, 2, 3]} renderItem={({ item }) => <Text>{String(item)}</Text>} />
      );
    });
    const root = tree!.root;
    const scrollTo = (y: number) =>
      act(() => {
        root.findByType(ScrollView).props.onScroll({
          nativeEvent: {
            layoutMeasurement: { height: 500 },
            contentOffset: { y },
            contentSize: { height: 5000 },
          },
        });
      });

    // An invisible (opacity 0) button would still swallow taps on the card under it.
    expect(root.findAllByType(TouchableOpacity)).toHaveLength(0);

    scrollTo(300);
    expect(root.findAllByType(TouchableOpacity)).toHaveLength(1);

    scrollTo(0);
    expect(root.findAllByType(TouchableOpacity)).toHaveLength(0);
  });
});
