import React from "react";
import renderer, { act } from "react-test-renderer";
import { AppState, Text } from "react-native";

import { PlayRecordUndoBar } from "../PlayRecordUndoBar";
import {
  PLAY_RECORD_UNDO_WINDOW_MS,
  usePlayRecordUndoStore,
  resetPlayRecordUndoStoreForTests,
} from "@/stores/playRecordUndoStore";
import { PlayRecordManager } from "@/services/storage";

jest.mock("@/services/storage", () => ({
  PlayRecordManager: { get: jest.fn(async () => null), remove: jest.fn(async () => undefined) },
}));
jest.mock("@/hooks/useResponsiveLayout", () => ({ useResponsiveLayout: () => ({ deviceType: "mobile" }) }));
jest.mock("react-native-toast-message", () => ({ __esModule: true, default: { show: jest.fn() } }));

describe("PlayRecordUndoBar", () => {
  let tree: renderer.ReactTestRenderer;
  let appStateListener: ((status: string) => void) | undefined;

  const texts = () => tree.root.findAllByType(Text).map((node) => [node.props.children].flat().join(""));
  const requestDelete = () =>
    act(() => {
      usePlayRecordUndoStore.getState().requestDelete({ source: "src", id: "7", title: "山海" });
    });
  const settle = async () => {
    await act(async () => {
      await usePlayRecordUndoStore.getState().flushCommitted();
    });
  };

  beforeEach(() => {
    jest.useFakeTimers();
    jest.clearAllMocks();
    resetPlayRecordUndoStoreForTests();
    appStateListener = undefined;
    jest.spyOn(AppState, "addEventListener").mockImplementation(((_type: string, listener: (status: string) => void) => {
      appStateListener = listener;
      return { remove: jest.fn() };
    }) as unknown as typeof AppState.addEventListener);
    act(() => {
      tree = renderer.create(<PlayRecordUndoBar />);
    });
  });

  afterEach(() => {
    act(() => {
      tree.unmount();
    });
    resetPlayRecordUndoStoreForTests();
    jest.restoreAllMocks();
    jest.useRealTimers();
  });

  it("is hidden while nothing is pending", () => {
    expect(tree.toJSON()).toBeNull();
  });

  it("shows which record was deleted, with an undo button", () => {
    requestDelete();

    expect(texts()).toEqual(["已删除《山海》的观看记录", "撤销"]);
  });

  it("undo restores the record: the bar goes away and nothing is ever deleted", async () => {
    requestDelete();
    act(() => {
      jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS - 1);
    });

    act(() => {
      tree.root.findByProps({ testID: "play-record-undo-button" }).props.onPress();
    });
    act(() => {
      jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS * 2);
    });
    await settle();

    expect(tree.toJSON()).toBeNull();
    expect(PlayRecordManager.remove).not.toHaveBeenCalled();
  });

  it("deletes for real when the window runs out, and the bar goes away", async () => {
    requestDelete();
    expect(PlayRecordManager.remove).not.toHaveBeenCalled();

    act(() => {
      jest.advanceTimersByTime(PLAY_RECORD_UNDO_WINDOW_MS);
    });
    await settle();

    expect(PlayRecordManager.remove).toHaveBeenCalledWith("src", "7");
    expect(tree.toJSON()).toBeNull();
  });

  it("commits the pending delete when the app goes to the background", async () => {
    requestDelete();

    await act(async () => {
      appStateListener?.("background");
    });
    await settle();

    expect(PlayRecordManager.remove).toHaveBeenCalledWith("src", "7");
    expect(tree.toJSON()).toBeNull();
  });
});
