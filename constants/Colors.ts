/**
 * App palette. Dark theme follows the round-11 "CineFlow-like" visual language:
 * deep indigo-black surfaces, a mint accent and a warm gold secondary accent.
 * (CineFlow itself uses #070810 / #7fd7c6 / #d8b874 — ours is shifted in hue and lightness on purpose.)
 */

const tintColorLight = "#0a7ea4";
const tintColorDark = "#9AA1B8";

export const Colors = {
  light: {
    text: "#11181C",
    background: "#fff",
    tint: tintColorLight,
    icon: "#687076",
    tabIconDefault: "#687076",
    tabIconSelected: tintColorLight,
    link: "#0a7ea4",
    border: "#E5E5E5",
  },
  dark: {
    text: "#EEF0F7",
    background: "#0A0B14",
    tint: tintColorDark,
    icon: "#9AA1B8",
    tabIconDefault: "#9AA1B8",
    tabIconSelected: "#6FDCA8",
    link: "#2E8F6A",
    border: "#2A2F47",
    primary: "#6FDCA8",
    // semantic additions
    accent: "#6FDCA8",
    accentHover: "#8CE8BC",
    accentPressed: "#56C08E",
    onAccent: "#06140E",
    accentSoft: "rgba(111, 220, 168, 0.14)",
    gold: "#E3B26B",
    panel: "#151828",
    panelAlt: "#1B1F33",
    input: "#1F2338",
    hover: "#2A3048",
    hairline: "rgba(255,255,255,0.08)",
    textMuted: "#9AA1B8",
    textSoft: "#C5CADB",
    danger: "#FF6B6B",
  },
};

/** Shared corner radii for the round-11 visual language. */
export const Radii = {
  hero: 28,
  panel: 20,
  control: 14,
  pill: 999,
};
