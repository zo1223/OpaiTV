import React from "react";
import { BackHandler, ScrollView, Text, TouchableOpacity, View } from "react-native";
import renderer, { act } from "react-test-renderer";
import {
  NavigationContainer,
  NavigationContainerRef,
  StackRouter,
  createNavigatorFactory,
  useNavigationBuilder,
} from "@react-navigation/native";
import CustomScrollView from "../CustomScrollView";

let mockDeviceType = "mobile";

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: () => ({ deviceType: mockDeviceType, columns: 2, cardWidth: 100, cardHeight: 150, spacing: 8 }),
}));

jest.mock("@/utils/ResponsiveStyles", () => ({
  getCommonResponsiveStyles: () => ({ center: {} }),
}));

// expo-router's useFocusEffect is React Navigation's, forked only to wait for the router to load.
jest.mock("expo-router", () => ({
  useFocusEffect: jest.requireActual("@react-navigation/native").useFocusEffect,
}));

type TestRoutes = { list: undefined; detail: undefined };

// Like the app's native stack: the screens under the top one stay mounted.
const TestStack = createNavigatorFactory(({ children }: { children: React.ReactNode }) => {
  const { state, descriptors, NavigationContent } = useNavigationBuilder(StackRouter, { children });
  return (
    <NavigationContent>
      {state.routes.map((route) => (
        <View key={route.key}>{descriptors[route.key].render()}</View>
      ))}
    </NavigationContent>
  );
})();

const ListScreen = () => <CustomScrollView data={[1, 2, 3]} renderItem={({ item }) => <Text>{String(item)}</Text>} />;
const DetailScreen = () => <Text>detail</Text>;

describe("CustomScrollView", () => {
  let tree: renderer.ReactTestRenderer;
  let navigation: NavigationContainerRef<TestRoutes>;
  // Same dispatch rule as BackHandler.android.js: last registered first, stop at the first `true`.
  let backHandlers: (() => boolean | null | undefined)[];

  const renderListScreen = () => {
    const navigationRef = React.createRef<NavigationContainerRef<TestRoutes>>();
    act(() => {
      tree = renderer.create(
        <NavigationContainer ref={navigationRef}>
          <TestStack.Navigator>
            <TestStack.Screen name="list" component={ListScreen} />
            <TestStack.Screen name="detail" component={DetailScreen} />
          </TestStack.Navigator>
        </NavigationContainer>
      );
    });
    navigation = navigationRef.current!;
  };

  const scrollTo = (y: number) =>
    act(() => {
      tree.root.findByType(ScrollView).props.onScroll({
        nativeEvent: {
          layoutMeasurement: { height: 500 },
          contentOffset: { y },
          contentSize: { height: 5000 },
        },
      });
    });

  const pressBack = () => {
    let handled = false;
    act(() => {
      for (let i = backHandlers.length - 1; i >= 0 && !handled; i--) {
        handled = !!backHandlers[i]();
      }
    });
    return handled;
  };

  const listScrollTo = () => tree.root.findByType(ScrollView).instance.scrollTo as jest.Mock;

  beforeEach(() => {
    // react-native's jest setup shares one scrollTo mock between all ScrollView instances
    jest.clearAllMocks();
    jest.useFakeTimers();
    mockDeviceType = "mobile";
    backHandlers = [];
    jest.spyOn(BackHandler, "addEventListener").mockImplementation((_eventName, handler) => {
      if (!backHandlers.includes(handler)) backHandlers.push(handler);
      return {
        remove: () => {
          const index = backHandlers.indexOf(handler);
          if (index !== -1) backHandlers.splice(index, 1);
        },
      };
    });
  });

  afterEach(() => {
    act(() => tree.unmount());
    jest.restoreAllMocks();
    jest.useRealTimers();
  });

  it("only mounts the scroll-to-top button while the list is scrolled down", () => {
    renderListScreen();
    const root = tree.root;

    // An invisible (opacity 0) button would still swallow taps on the card under it.
    expect(root.findAllByType(TouchableOpacity)).toHaveLength(0);

    scrollTo(300);
    expect(root.findAllByType(TouchableOpacity)).toHaveLength(1);

    scrollTo(0);
    expect(root.findAllByType(TouchableOpacity)).toHaveLength(0);
  });

  it("scrolls a scrolled-down TV list back to the top on the back key", () => {
    mockDeviceType = "tv";
    renderListScreen();

    scrollTo(500);

    expect(pressBack()).toBe(true);
    expect(listScrollTo()).toHaveBeenCalledWith({ y: 0, animated: true });
  });

  it("leaves the back key to a screen opened on top of a scrolled-down TV list", () => {
    mockDeviceType = "tv";
    renderListScreen();
    scrollTo(500);

    act(() => navigation.navigate("detail"));
    expect(navigation.getCurrentRoute()?.name).toBe("detail");

    // The list's screen is still mounted under the detail screen, but the key belongs to the detail screen.
    expect(pressBack()).toBe(true);
    expect(navigation.getCurrentRoute()?.name).toBe("list");
    expect(listScrollTo()).not.toHaveBeenCalled();

    // Back on the list, which kept its scroll position, the key scrolls to the top again.
    expect(pressBack()).toBe(true);
    expect(listScrollTo()).toHaveBeenCalledWith({ y: 0, animated: true });
    expect(navigation.getCurrentRoute()?.name).toBe("list");
  });
});
