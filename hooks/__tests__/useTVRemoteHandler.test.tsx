import React from "react";
import renderer, { act } from "react-test-renderer";
import { useTVRemoteHandler } from "../useTVRemoteHandler";

const mockSeek = jest.fn();
const mockTogglePlayPause = jest.fn();
const mockSetShowControls = jest.fn();
let mockPlayerState: Record<string, unknown> = {};
let mockRemoteListener: (event: { eventType: string; eventKeyAction?: number }) => void = () => {};

jest.mock("@/stores/playerStore", () => ({
  __esModule: true,
  default: () => mockPlayerState,
}));

jest.mock("@/hooks/useSafeTVEventHandler", () => ({
  useSafeTVEventHandler: (handler: (event: { eventType: string; eventKeyAction?: number }) => void) => {
    mockRemoteListener = handler;
  },
}));

const Harness = () => {
  useTVRemoteHandler();
  return null;
};

const mountWith = (flags: Record<string, boolean>) => {
  mockPlayerState = {
    showControls: false,
    showEpisodeModal: false,
    showSourceModal: false,
    showSpeedModal: false,
    ...flags,
    setShowControls: mockSetShowControls,
    togglePlayPause: mockTogglePlayPause,
    seek: mockSeek,
  };
  let tree: renderer.ReactTestRenderer;
  act(() => {
    tree = renderer.create(<Harness />);
  });
  return tree!;
};

// Android TV reports normal presses on key up (1); a long press starts on key down (0)
const press = (eventType: string, eventKeyAction = 1) => act(() => mockRemoteListener({ eventType, eventKeyAction }));

describe("useTVRemoteHandler", () => {
  let tree: renderer.ReactTestRenderer | undefined;

  beforeEach(() => {
    jest.useFakeTimers();
    jest.clearAllMocks();
  });

  afterEach(() => {
    // Unmounting clears the hook's auto-hide and long-press timers
    act(() => tree?.unmount());
    tree = undefined;
    jest.useRealTimers();
  });

  it("drives playback from the remote while nothing covers the player", () => {
    tree = mountWith({});

    press("right");
    press("left");
    press("select");

    expect(mockSeek.mock.calls).toEqual([[20000], [-20000]]);
    expect(mockTogglePlayPause).toHaveBeenCalledTimes(1);
    expect(mockSetShowControls).toHaveBeenCalledWith(true);
  });

  it("seeks continuously during a long press and stops on release", () => {
    tree = mountWith({});

    press("longRight", 0);
    act(() => {
      jest.advanceTimersByTime(400);
    });
    press("longRight", 1);
    act(() => {
      jest.advanceTimersByTime(1000);
    });

    expect(mockSeek.mock.calls).toEqual([[20000], [20000]]);
  });

  // Remote key events still reach this handler while a Modal is open, and the controls
  // auto-hide after 5s, so an open modal has to block player actions by itself.
  it.each(["showSourceModal", "showSpeedModal", "showEpisodeModal"])(
    "leaves playback alone for keys pressed inside a modal after the controls auto-hid (%s)",
    (modalFlag) => {
      tree = mountWith({ showControls: false, [modalFlag]: true });

      press("right");
      press("left");
      press("select");
      press("down");
      press("longRight", 0);
      act(() => {
        jest.advanceTimersByTime(1000);
      });

      expect(mockSeek).not.toHaveBeenCalled();
      expect(mockTogglePlayPause).not.toHaveBeenCalled();
      expect(mockSetShowControls).not.toHaveBeenCalledWith(true);
    }
  );
});
