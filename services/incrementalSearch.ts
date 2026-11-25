/**
 * 跨片源增量搜索:逐源并发,一个源返回就回报一次(进度 + 目前为止归并好的结果),不等全部搜完。
 * 纯逻辑,不依赖界面;取数由调用方通过 searchOne 注入。
 */
export type SourceSearchStatus = "pending" | "searching" | "done" | "failed" | "timeout";

export interface SourceSearchProgress {
  key: string;
  name: string;
  status: SourceSearchStatus;
  /** 该源返回的条数(归并前) */
  count: number;
}

export interface IncrementalSearchSnapshot<T> {
  progress: SourceSearchProgress[];
  /** 已结束的源数(完成 + 失败 + 超时) */
  finished: number;
  total: number;
  results: T[];
  done: boolean;
}

export interface IncrementalSearchSource {
  key: string;
  name: string;
}

export interface IncrementalSearchOptions<T> {
  sources: IncrementalSearchSource[];
  searchOne: (source: IncrementalSearchSource, signal: AbortSignal) => Promise<T[]>;
  onUpdate: (snapshot: IncrementalSearchSnapshot<T>) => void;
  /** 旧搜索作废:返回 true 后不再回调 */
  isCancelled?: () => boolean;
  /** 单源时限,超时记为 timeout,不拖住其它源 */
  timeoutMs?: number;
  /** 结果归并(默认按标题去重,先到先得);入参按源在列表里的顺序排好,结果顺序不随返回先后抖动 */
  merge?: (lists: T[][]) => T[];
}

const normalizeTitle = (value: unknown) =>
  typeof value === "string" ? value.trim().toLowerCase().replace(/\s+/g, "") : "";

/** 默认归并:按标题去重(同名条目保留排在前面的源),没有标题的条目原样保留。 */
export const mergeByTitle = <T>(lists: T[][]): T[] => {
  const seen = new Set<string>();
  const merged: T[] = [];
  for (const item of lists.flat()) {
    const key = normalizeTitle((item as { title?: unknown } | null)?.title);
    if (key) {
      if (seen.has(key)) continue;
      seen.add(key);
    }
    merged.push(item);
  }
  return merged;
};

export const summarizeSearchProgress = (snapshot: Pick<IncrementalSearchSnapshot<unknown>, "finished" | "total" | "results" | "done">) =>
  snapshot.total === 0
    ? "没有可搜索的片源"
    : `${snapshot.done ? "已搜完" : "正在搜索"} ${snapshot.finished}/${snapshot.total} 个源，共 ${snapshot.results.length} 条`;

export const describeSourceProgress = (item: SourceSearchProgress) => {
  switch (item.status) {
    case "pending":
      return `${item.name} 等待`;
    case "searching":
      return `${item.name} 搜索中`;
    case "done":
      return `${item.name} ${item.count} 条`;
    case "timeout":
      return `${item.name} 超时`;
    default:
      return `${item.name} 失败`;
  }
};

export const DEFAULT_SOURCE_SEARCH_TIMEOUT_MS = 15_000;

export async function runIncrementalSearch<T>(options: IncrementalSearchOptions<T>): Promise<IncrementalSearchSnapshot<T>> {
  const { sources, searchOne, onUpdate } = options;
  const merge = options.merge ?? mergeByTitle;
  const timeoutMs = options.timeoutMs ?? DEFAULT_SOURCE_SEARCH_TIMEOUT_MS;
  const cancelled = () => options.isCancelled?.() === true;

  const progress: SourceSearchProgress[] = sources.map((source) => ({
    key: source.key,
    name: source.name || source.key,
    status: "searching",
    count: 0,
  }));
  const perSource: T[][] = sources.map(() => []);

  const snapshot = (): IncrementalSearchSnapshot<T> => {
    const finished = progress.filter((item) => item.status !== "pending" && item.status !== "searching").length;
    return {
      progress: progress.map((item) => ({ ...item })),
      finished,
      total: progress.length,
      results: merge(perSource),
      done: finished === progress.length,
    };
  };
  const emit = () => {
    if (!cancelled()) {
      onUpdate(snapshot());
    }
  };

  // 先回报一次:界面立刻能列出全部源的“搜索中”状态
  emit();

  await Promise.all(
    sources.map(async (source, index) => {
      const controller = new AbortController();
      let timedOut = false;
      const timer = setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, timeoutMs);
      try {
        const found = await Promise.race([
          Promise.resolve().then(() => searchOne(source, controller.signal)),
          new Promise<never>((_, reject) => {
            controller.signal.addEventListener("abort", () => reject(new Error("SOURCE_SEARCH_TIMEOUT")));
          }),
        ]);
        perSource[index] = Array.isArray(found) ? found : [];
        progress[index] = { ...progress[index], status: "done", count: perSource[index].length };
      } catch {
        progress[index] = { ...progress[index], status: timedOut ? "timeout" : "failed", count: 0 };
      } finally {
        clearTimeout(timer);
      }
      emit();
    })
  );

  return snapshot();
}
