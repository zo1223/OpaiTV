import { AccessibilityInfo } from "react-native";

/**
 * 界面动效的统一参数。原则:轻、短、只动 transform / opacity(不改布局尺寸),全部走原生驱动。
 */
export const MOTION = {
  /** 卡片按下时的缩放比例 */
  pressScale: 0.96,
  /** 按压弹簧参数(与平板卡片原有手感一致) */
  pressSpring: { damping: 15, stiffness: 300 },
  /** 列表项 / 卡片出现:淡入时长 */
  entranceDuration: 200,
  /** 列表项 / 卡片出现:起始下移距离 */
  entranceOffsetY: 8,
  /** 同一批出现的卡片,相邻两张的错峰间隔 */
  staggerStepMs: 30,
  /** 错峰最多几档;超过的卡片与最后一档同时出现,长列表不会越排越慢 */
  staggerMaxSteps: 8,
  /** 相隔不超过这么久挂载的卡片算同一批 */
  staggerBatchWindowMs: 80,
  /** 海报加载完成后的淡入时长 */
  imageFadeDuration: 200,
  /** 海报一直没有加载回调时,多久后直接显示(防止图片永远不可见) */
  imageRevealFallbackMs: 1500,
  /** 页面切换(淡入淡出)时长;只对 iOS / tvOS 生效,Android 由系统动画资源决定(150ms) */
  pageTransitionDuration: 200,
} as const;

/**
 * 页面切换:统一用短的淡入淡出。比平台默认的推入动画更短,不拖慢进入播放页。
 * 淡入淡出本身就是"减少动态效果"下推荐的替代过渡,所以这里不再区分。
 */
export const STACK_SCREEN_TRANSITION = {
  animation: "fade",
  animationDuration: MOTION.pageTransitionDuration,
} as const;

// ---- 减少动态效果 ----

let reduceMotion = false;
let reduceMotionInitialized = false;

function initReduceMotion(): void {
  if (reduceMotionInitialized) return;
  reduceMotionInitialized = true;
  try {
    const pending = AccessibilityInfo.isReduceMotionEnabled?.();
    if (pending && typeof pending.then === "function") {
      pending
        .then((enabled) => {
          reduceMotion = enabled === true;
        })
        .catch(() => {
          // 查不到就按"没开"处理:保留动画
        });
    }
    AccessibilityInfo.addEventListener?.("reduceMotionChanged", (enabled) => {
      reduceMotion = enabled === true;
    });
  } catch {
    // 平台不支持:保留动画
  }
}

/**
 * 系统是否开启了"减少动态效果"。同步读取缓存值:全应用只向系统查询一次并监听变化,
 * 不会每张卡片各查一次。首次查询返回前按"没开"处理。
 */
export function isReduceMotionEnabled(): boolean {
  initReduceMotion();
  return reduceMotion;
}

// ---- 卡片出现的错峰 ----

let batchStartedAt = Number.NEGATIVE_INFINITY;
let batchCount = 0;

/**
 * 给新挂载的卡片分配出现延迟:同一批(相隔很短时间内挂载)的卡片依次错开,延迟封顶。
 * 无限滚动追加的卡片是新的一批,只有它们自己播放出现动画,已经在列表里的卡片不受影响。
 */
export function nextEntranceDelay(now: number = Date.now()): number {
  if (now - batchStartedAt > MOTION.staggerBatchWindowMs || now < batchStartedAt) {
    batchStartedAt = now;
    batchCount = 0;
  }
  const step = Math.min(batchCount, MOTION.staggerMaxSteps - 1);
  batchCount += 1;
  return step * MOTION.staggerStepMs;
}

/** 仅供测试:重置模块内的缓存状态。 */
export function __resetMotionStateForTests(options: { reduceMotion?: boolean } = {}): void {
  batchStartedAt = Number.NEGATIVE_INFINITY;
  batchCount = 0;
  if (options.reduceMotion === undefined) {
    reduceMotion = false;
    reduceMotionInitialized = false;
  } else {
    reduceMotion = options.reduceMotion;
    reduceMotionInitialized = true;
  }
}
