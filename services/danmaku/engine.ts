/**
 * 弹幕排布与时间对齐(纯逻辑,不依赖界面)。
 *
 * 所有位置都是"视频时间"的函数:时钟停(暂停/缓冲)弹幕就停;时间跳变(拖动进度)时
 * 按新时间重建画面上的弹幕,包括已经飞到一半的。
 */
import { DanmakuComment, DanmakuMode, DanmakuTrack } from "./types";

// ---------- 时钟 ----------

/** 位置上报与推算值相差超过这么多秒才重新对表,避免上报抖动造成弹幕来回跳 */
const CLOCK_RESYNC_SECONDS = 0.4;
/** 这么久没有新的进度上报,就认为播放器卡住了(缓冲),时钟停在原地等它 */
const CLOCK_STALL_MS = 1500;
/** 播放中连续两次上报同一个位置、且间隔超过这么久,也算卡住 */
const CLOCK_REPEAT_MS = 250;
/** 卡住时时钟回拨超过这么多秒才算"画面上弹幕的位置变了" */
const CLOCK_VISIBLE_SECONDS = 0.05;

/**
 * 播放器每隔几百毫秒才上报一次进度,这里在两次上报之间按播放速度推算当前视频时间。
 * 暂停、缓冲卡住时时间不走。
 */
export class DanmakuClock {
  private baseSeconds = 0;
  private baseWallMs = 0;
  private playing = false;
  private rate = 1;
  private stalled = false;
  private lastReportSeconds = -1;
  private lastReportWallMs = 0;

  now(wallMs: number): number {
    if (!this.playing || this.stalled) return this.baseSeconds;
    // 上报断了就只再往前推 CLOCK_STALL_MS
    const until = Math.min(wallMs, Math.max(this.baseWallMs, this.lastReportWallMs + CLOCK_STALL_MS));
    return this.baseSeconds + (Math.max(0, until - this.baseWallMs) / 1000) * this.rate;
  }

  get isPlaying(): boolean {
    return this.playing;
  }

  /** 时间此刻是否在走(在播、没有卡住、进度上报也没断) */
  isRunningAt(wallMs: number): boolean {
    return this.playing && !this.stalled && wallMs < Math.max(this.baseWallMs, this.lastReportWallMs + CLOCK_STALL_MS);
  }

  get playbackRate(): number {
    return this.rate;
  }

  /** 返回状态是否变了 */
  setPlaying(playing: boolean, wallMs: number): boolean {
    if (playing === this.playing) return false;
    this.rebase(this.now(wallMs), wallMs);
    this.playing = playing;
    this.stalled = false;
    this.lastReportWallMs = wallMs;
    return true;
  }

  /** 返回倍速是否变了 */
  setRate(rate: number, wallMs: number): boolean {
    const next = Number.isFinite(rate) && rate > 0 ? rate : 1;
    if (next === this.rate) return false;
    this.rebase(this.now(wallMs), wallMs);
    this.rate = next;
    return true;
  }

  /** 播放器上报了进度。返回是否重新对了表(此时画面上弹幕的位置会跳一下,渲染层要按新时间重排) */
  sync(positionSeconds: number, wallMs: number): boolean {
    if (!Number.isFinite(positionSeconds)) return false;
    const position = Math.max(0, positionSeconds);
    const current = this.now(wallMs);
    const repeated = position === this.lastReportSeconds && wallMs - this.lastReportWallMs >= CLOCK_REPEAT_MS;
    this.lastReportSeconds = position;
    this.lastReportWallMs = wallMs;

    if (this.playing && repeated) {
      // 说是在播,进度却没动:卡住了,时钟停在上报的位置
      const wasStalled = this.stalled;
      this.stalled = true;
      this.rebase(position, wallMs);
      return !wasStalled || Math.abs(position - current) > CLOCK_VISIBLE_SECONDS;
    }
    if (this.stalled) {
      this.stalled = false;
      this.rebase(position, wallMs);
      return true;
    }
    if (Math.abs(position - current) <= CLOCK_RESYNC_SECONDS) return false;
    this.rebase(position, wallMs);
    return true;
  }

  reset(): void {
    this.baseSeconds = 0;
    this.baseWallMs = 0;
    this.playing = false;
    this.rate = 1;
    this.stalled = false;
    this.lastReportSeconds = -1;
    this.lastReportWallMs = 0;
  }

  private rebase(seconds: number, wallMs: number): void {
    this.baseSeconds = seconds;
    this.baseWallMs = wallMs;
  }
}

// ---------- 分片缓存 ----------

const SEGMENT_RETRY_MS = 20000;
const SEGMENT_MAX_ATTEMPTS = 3;
/** 离下一个分片还有这么多秒时提前取 */
const SEGMENT_LOOKAHEAD_SECONDS = 10;
/** 离所在分片开头不到这么多秒时上一片也要取:拖到分片开头附近时,上一片末尾的弹幕还在画面上飞(不小于最慢的滚动时长) */
const SEGMENT_LOOKBEHIND_SECONDS = 12;

interface SegmentState {
  comments: DanmakuComment[] | null;
  loading: boolean;
  attempts: number;
  lastAttemptMs: number;
}

/** 按播放位置懒加载弹幕分片,并按时间区间查询已加载的弹幕。分片加载失败只是该段没有弹幕。 */
export class DanmakuTimeline {
  private readonly segments = new Map<number, SegmentState>();
  private disposed = false;

  constructor(
    private readonly track: DanmakuTrack,
    private readonly onSegmentLoaded?: (index: number) => void,
    private readonly wallNow: () => number = Date.now
  ) {}

  private indexOf(seconds: number): number {
    return this.track.segmentSeconds > 0 ? Math.max(0, Math.floor(seconds / this.track.segmentSeconds)) : 0;
  }

  /** 确保 seconds 所在分片(以及即将播到的下一片、弹幕还没飞完的上一片)已在加载 */
  ensure(seconds: number): void {
    if (this.disposed) return;
    const index = this.indexOf(seconds);
    this.load(index);
    if (this.track.segmentSeconds > 0) {
      const previous = this.indexOf(Math.max(0, seconds - SEGMENT_LOOKBEHIND_SECONDS));
      if (previous !== index) this.load(previous);
      const next = this.indexOf(seconds + SEGMENT_LOOKAHEAD_SECONDS);
      if (next !== index) this.load(next);
    }
  }

  private load(index: number): void {
    let state = this.segments.get(index);
    if (!state) {
      state = { comments: null, loading: false, attempts: 0, lastAttemptMs: 0 };
      this.segments.set(index, state);
    }
    if (state.comments || state.loading || state.attempts >= SEGMENT_MAX_ATTEMPTS) return;
    const nowMs = this.wallNow();
    if (state.attempts > 0 && nowMs - state.lastAttemptMs < SEGMENT_RETRY_MS) return;

    const current = state;
    current.loading = true;
    current.attempts++;
    current.lastAttemptMs = nowMs;
    let pending: Promise<DanmakuComment[]>;
    try {
      pending = this.track.loadSegment(index);
    } catch (error) {
      pending = Promise.reject(error);
    }
    pending
      .then((comments) => {
        current.loading = false;
        if (this.disposed) return;
        current.comments = sortComments(comments);
        this.onSegmentLoaded?.(index);
      })
      .catch(() => {
        current.loading = false;
      });
  }

  /** 分片覆盖的时间范围 [from, to)(秒);整集只有一个分片时到正无穷 */
  rangeOf(index: number): [number, number] {
    const span = this.track.segmentSeconds;
    return span > 0 ? [index * span, (index + 1) * span] : [0, Infinity];
  }

  /** 时间在 (fromExclusive, toInclusive] 内的已加载弹幕,按时间升序 */
  between(fromExclusive: number, toInclusive: number): DanmakuComment[] {
    if (toInclusive <= fromExclusive) return [];
    const result: DanmakuComment[] = [];
    const first = this.indexOf(Math.max(0, fromExclusive));
    const last = this.indexOf(Math.max(0, toInclusive));
    for (let index = first; index <= last; index++) {
      const comments = this.segments.get(index)?.comments;
      if (!comments) continue;
      for (let i = lowerBound(comments, fromExclusive); i < comments.length; i++) {
        if (comments[i].time > toInclusive) break;
        result.push(comments[i]);
      }
    }
    return result;
  }

  dispose(): void {
    this.disposed = true;
    this.segments.clear();
  }
}

function sortComments(comments: DanmakuComment[]): DanmakuComment[] {
  return comments
    .map((comment, order) => ({ comment, order }))
    .sort((a, b) => a.comment.time - b.comment.time || a.order - b.order)
    .map((entry) => entry.comment);
}

/** 第一个 time > value 的下标 */
function lowerBound(comments: DanmakuComment[], value: number): number {
  let low = 0;
  let high = comments.length;
  while (low < high) {
    const mid = (low + high) >> 1;
    if (comments[mid].time > value) high = mid;
    else low = mid + 1;
  }
  return low;
}

// ---------- 排布 ----------

export interface DanmakuLayoutOptions {
  width: number;
  height: number;
  fontSize: number;
  /** 滚动弹幕横穿画面的秒数 */
  scrollSeconds: number;
  /** 顶部/底部弹幕停留的秒数 */
  fixedSeconds: number;
  /** 文字宽度;不给时按字符估算 */
  measure?: (text: string, fontSize: number) => number;
  /** 画面上同时最多几条;不给时为 60 */
  maxItems?: number;
}

export interface DanmakuItem {
  id: number;
  comment: DanmakuComment;
  mode: DanmakuMode;
  lane: number;
  /** 文字顶边的纵坐标 */
  y: number;
  width: number;
  /** 出现/消失的视频时间(秒) */
  start: number;
  end: number;
}

const LINE_HEIGHT_RATIO = 1.35;
/** 滚动弹幕占画面上方这么大的比例 */
const SCROLL_AREA_RATIO = 0.45;
/** 滚动弹幕最多几行 */
const MAX_SCROLL_LANES = 12;
const DEFAULT_MAX_ITEMS = 60;
/** 顶部/底部弹幕各自最多占的行数比例 */
const FIXED_AREA_RATIO = 0.3;
/** 底部弹幕离画面底边留出的比例(避开进度条和字幕) */
const BOTTOM_MARGIN_RATIO = 0.1;
/** 同一行前后两条弹幕的最小间隔(字号的倍数) */
const LANE_GAP_RATIO = 1;
/** 时间向前跳超过这么多秒按拖动处理 */
const JUMP_FORWARD_SECONDS = 1.5;
/** 时间往回退超过这么多秒按拖动处理;更小的回退只当时间没动 */
const JUMP_BACKWARD_SECONDS = 1;
const MAX_TEXT_LENGTH = 80;

/** 按字符估算文字宽度:中日韩等全角字符算一个字宽,其余算 0.58 个 */
export function estimateTextWidth(text: string, fontSize: number): number {
  let units = 0;
  for (let i = 0; i < text.length; i++) {
    units += text.charCodeAt(i) > 0x2e7f ? 1 : 0.58;
  }
  return Math.ceil(units * fontSize) + Math.ceil(fontSize * 0.4);
}

export class DanmakuEngine {
  private options: DanmakuLayoutOptions;
  private active: DanmakuItem[] = [];
  private scrollLanes: (DanmakuItem | null)[] = [];
  private topLanes: number[] = [];
  private bottomLanes: number[] = [];
  private lastTime: number | null = null;
  /** 已经排到的视频时间(lastTime 加上提前量) */
  private placedUntil = 0;
  private nextId = 1;

  constructor(
    private readonly source: (fromExclusive: number, toInclusive: number) => DanmakuComment[],
    options: DanmakuLayoutOptions
  ) {
    this.options = options;
    this.resetLanes();
  }

  get items(): readonly DanmakuItem[] {
    return this.active;
  }

  get lineHeight(): number {
    return Math.ceil(this.options.fontSize * LINE_HEIGHT_RATIO);
  }

  /** 画面尺寸或样式变了:清空,下次 advance 时按当前时间重建 */
  setOptions(options: DanmakuLayoutOptions): void {
    this.options = options;
    this.invalidate();
  }

  /** 清空画面;下次 advance 时按当时的时间重建 */
  invalidate(): void {
    this.active = [];
    this.lastTime = null;
    this.placedUntil = 0;
    this.resetLanes();
  }

  /**
   * 把画面重建到视频时间 time:清掉现有弹幕,把此刻应该还在画面上的弹幕按各自出现时间重新排一遍。
   * lookahead > 0 时把接下来这么多秒内要出现的也先排好(它们的横坐标还在画面右边之外)。
   */
  seek(time: number, lookahead = 0): void {
    this.active = [];
    this.resetLanes();
    const window = Math.max(this.options.scrollSeconds, this.options.fixedSeconds);
    for (const comment of this.source(time - window, time + lookahead)) {
      this.place(comment);
    }
    this.active = this.active.filter((item) => item.end > time);
    this.lastTime = time;
    this.placedUntil = time + lookahead;
  }

  /**
   * 推进到视频时间 time。返回画面上的弹幕集合是否变了。
   * 时间跳变(拖动进度、换集)时自动重建。
   * 渲染层不是每帧都来推进时给一个 lookahead(秒),让弹幕在出现之前就建好,从画面外飘进来。
   */
  advance(time: number, lookahead = 0): boolean {
    if (this.options.width <= 0 || this.options.height <= 0) return false;
    if (
      this.lastTime === null ||
      time < this.lastTime - JUMP_BACKWARD_SECONDS ||
      time > this.lastTime + JUMP_FORWARD_SECONDS
    ) {
      const hadItems = this.active.length > 0;
      this.seek(time, lookahead);
      return hadItems || this.active.length > 0;
    }
    if (time <= this.lastTime) return false;

    let changed = false;
    const until = time + lookahead;
    if (until > this.placedUntil) {
      for (const comment of this.source(this.placedUntil, until)) {
        if (this.place(comment)) changed = true;
      }
      this.placedUntil = until;
    }
    const before = this.active.length;
    this.active = this.active.filter((item) => item.end > time);
    this.lastTime = time;
    return changed || this.active.length !== before;
  }

  /**
   * 时间在 [from, to) 的一段弹幕刚加载完。如果这段时间已经排过(加载比播放慢,或刚拖过进度),
   * 清空画面,下次 advance 时把它们补排进去;还没播到的不用管。
   */
  notifyLoaded(from: number, to: number): void {
    if (this.lastTime === null) return;
    const window = Math.max(this.options.scrollSeconds, this.options.fixedSeconds);
    if (from < this.placedUntil && to > this.lastTime - window) {
      this.invalidate();
    }
  }

  /** 视频时间 time 时这条弹幕是否该显示(提前排好的弹幕在出现时间之前不显示) */
  isVisibleAt(item: DanmakuItem, time: number): boolean {
    return item.start <= time && time < item.end;
  }

  /** 弹幕在视频时间 time 的横坐标(文字左边) */
  xAt(item: DanmakuItem, time: number): number {
    if (item.mode !== "scroll") {
      return Math.round((this.options.width - item.width) / 2);
    }
    const progress = (time - item.start) / (item.end - item.start);
    return this.options.width - progress * (this.options.width + item.width);
  }

  private resetLanes(): void {
    const lineHeight = Math.ceil(this.options.fontSize * LINE_HEIGHT_RATIO);
    const lanesIn = (ratio: number) =>
      lineHeight > 0 ? Math.max(1, Math.floor((this.options.height * ratio) / lineHeight)) : 1;
    this.scrollLanes = new Array(Math.min(MAX_SCROLL_LANES, lanesIn(SCROLL_AREA_RATIO))).fill(null);
    this.topLanes = new Array(lanesIn(FIXED_AREA_RATIO)).fill(-Infinity);
    this.bottomLanes = new Array(lanesIn(FIXED_AREA_RATIO)).fill(-Infinity);
  }

  /** 给弹幕找一行;没有空行或画面已满就丢弃(弹幕太密时自然限流) */
  private place(comment: DanmakuComment): boolean {
    const { width: screenWidth, height, fontSize, scrollSeconds, fixedSeconds } = this.options;
    if (this.active.length >= (this.options.maxItems ?? DEFAULT_MAX_ITEMS)) {
      // 已经消失的不占名额
      this.active = this.active.filter((item) => item.end > comment.time);
      if (this.active.length >= (this.options.maxItems ?? DEFAULT_MAX_ITEMS)) return false;
    }
    const text = comment.text.length > MAX_TEXT_LENGTH ? comment.text.slice(0, MAX_TEXT_LENGTH) : comment.text;
    const width = (this.options.measure || estimateTextWidth)(text, fontSize);
    const lineHeight = this.lineHeight;
    const start = comment.time;
    const shown = text === comment.text ? comment : { ...comment, text };

    if (comment.mode === "scroll") {
      const speed = (screenWidth + width) / scrollSeconds;
      const gap = fontSize * LANE_GAP_RATIO;
      for (let lane = 0; lane < this.scrollLanes.length; lane++) {
        const last = this.scrollLanes[lane];
        if (last) {
          const lastSpeed = (screenWidth + last.width) / scrollSeconds;
          // 前一条的尾巴已经完全进入画面并留出间隔,且新弹幕在前一条飞出之前追不上它
          const tailEntered = last.start + (last.width + gap) / lastSpeed <= start;
          const noCatchUp = start + screenWidth / speed >= last.end;
          if (!tailEntered || !noCatchUp) continue;
        }
        const item: DanmakuItem = {
          id: this.nextId++,
          comment: shown,
          mode: "scroll",
          lane,
          y: lane * lineHeight,
          width,
          start,
          end: start + scrollSeconds,
        };
        this.scrollLanes[lane] = item;
        this.active.push(item);
        return true;
      }
      return false;
    }

    const lanes = comment.mode === "top" ? this.topLanes : this.bottomLanes;
    for (let lane = 0; lane < lanes.length; lane++) {
      if (lanes[lane] > start) continue;
      lanes[lane] = start + fixedSeconds;
      const y =
        comment.mode === "top"
          ? lane * lineHeight
          : Math.max(0, Math.round(height * (1 - BOTTOM_MARGIN_RATIO)) - (lane + 1) * lineHeight);
      this.active.push({
        id: this.nextId++,
        comment: shown,
        mode: comment.mode,
        lane,
        y,
        width,
        start,
        end: start + fixedSeconds,
      });
      return true;
    }
    return false;
  }
}
