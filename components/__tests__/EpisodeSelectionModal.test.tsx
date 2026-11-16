import React from "react";
import renderer, { act } from "react-test-renderer";
import { EpisodeSelectionModal } from "../EpisodeSelectionModal";
import usePlayerStore from "@/stores/playerStore";

jest.mock("@/stores/playerStore", () => {
  const { create } = require("zustand");
  return {
    __esModule: true,
    default: create(() => ({
      showEpisodeModal: false,
      episodes: [],
      currentEpisodeIndex: 0,
      playEpisode: jest.fn(),
      setShowEpisodeModal: jest.fn(),
    })),
  };
});

jest.mock("../StyledButton", () => {
  const { Text } = require("react-native");
  return {
    StyledButton: ({ text, isSelected }: { text: string; isSelected?: boolean }) => (
      <Text>{isSelected ? `[${text}]` : text}</Text>
    ),
  };
});

describe("EpisodeSelectionModal", () => {
  it("opens on the group that contains the current episode", () => {
    const episodes = Array.from({ length: 70 }, (_, i) => ({ url: `u${i}`, title: `E${i + 1}` }));
    act(() => {
      usePlayerStore.setState({ episodes, currentEpisodeIndex: 0, showEpisodeModal: false });
    });

    let tree: renderer.ReactTestRenderer;
    act(() => {
      tree = renderer.create(<EpisodeSelectionModal />);
    });

    act(() => {
      usePlayerStore.setState({ currentEpisodeIndex: 44, showEpisodeModal: true });
    });

    const output = JSON.stringify(tree!.toJSON());
    expect(output).toContain("[31-60]");
    expect(output).toContain("[E45]");
    expect(output).not.toContain('"E1"');
  });
});
