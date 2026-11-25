import { AccessibilityInfo } from "react-native";

import {
  MOTION,
  STACK_SCREEN_TRANSITION,
  __resetMotionStateForTests,
  isReduceMotionEnabled,
  nextEntranceDelay,
} from "../motion";

describe("motion", () => {
  beforeEach(() => {
    __resetMotionStateForTests();
    jest.restoreAllMocks();
  });

  describe("nextEntranceDelay", () => {
    it("staggers items mounted in the same batch", () => {
      expect(nextEntranceDelay(1000)).toBe(0);
      expect(nextEntranceDelay(1001)).toBe(30);
      expect(nextEntranceDelay(1002)).toBe(60);
    });

    it("caps the delay so long lists do not get slower and slower", () => {
      const delays = Array.from({ length: 40 }, () => nextEntranceDelay(1000));
      const cap = (MOTION.staggerMaxSteps - 1) * MOTION.staggerStepMs;

      expect(Math.max(...delays)).toBe(cap);
      expect(cap).toBe(210);
      expect(delays.slice(MOTION.staggerMaxSteps).every((delay) => delay === cap)).toBe(true);
    });

    it("starts a new batch for items appended later (infinite scroll)", () => {
      nextEntranceDelay(1000);
      nextEntranceDelay(1000);
      nextEntranceDelay(1000);

      expect(nextEntranceDelay(1000 + MOTION.staggerBatchWindowMs + 1)).toBe(0);
      expect(nextEntranceDelay(1000 + MOTION.staggerBatchWindowMs + 2)).toBe(30);
    });

    it("starts a new batch when the clock goes backwards", () => {
      nextEntranceDelay(5000);
      nextEntranceDelay(5000);
      expect(nextEntranceDelay(100)).toBe(0);
    });
  });

  describe("isReduceMotionEnabled", () => {
    it("is false until the system answers, then follows the system setting", async () => {
      jest.spyOn(AccessibilityInfo, "isReduceMotionEnabled").mockResolvedValue(true);

      expect(isReduceMotionEnabled()).toBe(false);
      await Promise.resolve();
      await Promise.resolve();

      expect(isReduceMotionEnabled()).toBe(true);
    });

    it("asks the system only once, however many cards are on screen", async () => {
      const query = jest.spyOn(AccessibilityInfo, "isReduceMotionEnabled").mockResolvedValue(false);
      // RN 的 jest 环境里它本身就是 jest.fn,spyOn 返回的是同一个对象,要清掉之前用例留下的调用记录
      query.mockClear();

      for (let i = 0; i < 50; i++) isReduceMotionEnabled();
      await Promise.resolve();

      expect(query).toHaveBeenCalledTimes(1);
    });

    it("follows later changes of the system setting", () => {
      let listener: ((enabled: boolean) => void) | undefined;
      jest.spyOn(AccessibilityInfo, "isReduceMotionEnabled").mockResolvedValue(false);
      jest.spyOn(AccessibilityInfo, "addEventListener").mockImplementation(((event: string, handler: any) => {
        if (event === "reduceMotionChanged") listener = handler;
        return { remove: jest.fn() };
      }) as any);

      expect(isReduceMotionEnabled()).toBe(false);
      listener?.(true);
      expect(isReduceMotionEnabled()).toBe(true);
      listener?.(false);
      expect(isReduceMotionEnabled()).toBe(false);
    });

    it("keeps animations when the system cannot be asked", async () => {
      jest.spyOn(AccessibilityInfo, "isReduceMotionEnabled").mockRejectedValue(new Error("unsupported"));

      expect(isReduceMotionEnabled()).toBe(false);
      await Promise.resolve();
      await Promise.resolve();

      expect(isReduceMotionEnabled()).toBe(false);
    });
  });

  it("uses a short fade for page transitions", () => {
    expect(STACK_SCREEN_TRANSITION).toEqual({ animation: "fade", animationDuration: 200 });
  });
});
