import {
  IncrementalSearchSnapshot,
  describeSourceProgress,
  mergeByTitle,
  runIncrementalSearch,
  summarizeSearchProgress,
} from "../incrementalSearch";

type Item = { title: string; source: string };

const deferred = <T,>() => {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
};

const flush = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};

const sources = [
  { key: "a", name: "A源" },
  { key: "b", name: "B源" },
  { key: "c", name: "C源" },
];

describe("incremental cross-source search", () => {
  it("reports every source as searching first, then one update per finished source", async () => {
    const pending = { a: deferred<Item[]>(), b: deferred<Item[]>(), c: deferred<Item[]>() };
    const updates: IncrementalSearchSnapshot<Item>[] = [];
    const run = runIncrementalSearch<Item>({
      sources,
      searchOne: (source) => pending[source.key as "a" | "b" | "c"].promise,
      onUpdate: (snapshot) => updates.push(snapshot),
    });
    await flush();
    expect(updates).toHaveLength(1);
    expect(updates[0].progress.map((item) => item.status)).toEqual(["searching", "searching", "searching"]);
    expect(updates[0].results).toEqual([]);
    expect(updates[0].done).toBe(false);

    // B 先回来:它的结果马上可见,不用等 A 和 C
    pending.b.resolve([{ title: "流浪地球", source: "b" }]);
    await flush();
    expect(updates).toHaveLength(2);
    expect(updates[1].results).toEqual([{ title: "流浪地球", source: "b" }]);
    expect(updates[1].finished).toBe(1);
    expect(updates[1].progress[1]).toEqual({ key: "b", name: "B源", status: "done", count: 1 });
    expect(summarizeSearchProgress(updates[1])).toBe("正在搜索 1/3 个源，共 1 条");

    // A 后回来:同名条目归并成一条,并且按源的顺序排(A 在 B 前)
    pending.a.resolve([{ title: "流浪 地球", source: "a" }, { title: "三体", source: "a" }]);
    await flush();
    expect(updates[2].results).toEqual([
      { title: "流浪 地球", source: "a" },
      { title: "三体", source: "a" },
    ]);

    pending.c.resolve([]);
    const final = await run;
    expect(final.done).toBe(true);
    expect(final.finished).toBe(3);
    expect(updates).toHaveLength(4);
    expect(summarizeSearchProgress(final)).toBe("已搜完 3/3 个源，共 2 条");
    expect(final.progress.map(describeSourceProgress)).toEqual(["A源 2 条", "B源 1 条", "C源 0 条"]);
  });

  it("does not let a failing or hanging source hold back the others", async () => {
    jest.useFakeTimers();
    try {
      const updates: IncrementalSearchSnapshot<Item>[] = [];
      const signals: AbortSignal[] = [];
      const run = runIncrementalSearch<Item>({
        sources,
        timeoutMs: 1000,
        searchOne: (source, signal) => {
          signals.push(signal);
          if (source.key === "a") return Promise.reject(new Error("boom"));
          if (source.key === "b") return new Promise<Item[]>(() => {});
          return Promise.resolve([{ title: "三体", source: "c" }]);
        },
        onUpdate: (snapshot) => updates.push(snapshot),
      });
      await flush();
      const beforeTimeout = updates[updates.length - 1];
      expect(beforeTimeout.results).toEqual([{ title: "三体", source: "c" }]);
      expect(beforeTimeout.progress.map((item) => item.status)).toEqual(["failed", "searching", "done"]);
      expect(beforeTimeout.done).toBe(false);

      jest.advanceTimersByTime(1000);
      const final = await run;
      expect(final.progress.map((item) => item.status)).toEqual(["failed", "timeout", "done"]);
      expect(final.done).toBe(true);
      expect(signals[1].aborted).toBe(true);
      expect(final.progress.map(describeSourceProgress)).toEqual(["A源 失败", "B源 超时", "C源 1 条"]);
    } finally {
      jest.useRealTimers();
    }
  });

  it("stops calling back once the search is superseded", async () => {
    const pending = deferred<Item[]>();
    let cancelled = false;
    const onUpdate = jest.fn();
    const run = runIncrementalSearch<Item>({
      sources: [sources[0]],
      searchOne: () => pending.promise,
      onUpdate,
      isCancelled: () => cancelled,
    });
    await flush();
    expect(onUpdate).toHaveBeenCalledTimes(1);
    cancelled = true;
    pending.resolve([{ title: "迟到的结果", source: "a" }]);
    await run;
    expect(onUpdate).toHaveBeenCalledTimes(1);
  });

  it("handles an empty source list", async () => {
    const onUpdate = jest.fn();
    const final = await runIncrementalSearch<Item>({ sources: [], searchOne: async () => [], onUpdate });
    expect(final).toEqual({ progress: [], finished: 0, total: 0, results: [], done: true });
    expect(summarizeSearchProgress(final)).toBe("没有可搜索的片源");
  });

  it("merges by title and keeps untitled items", () => {
    expect(mergeByTitle([[{ title: "A" }, { title: "" }], [{ title: " a " }, { title: "" }, { title: "B" }]])).toEqual([
      { title: "A" },
      { title: "" },
      { title: "" },
      { title: "B" },
    ]);
  });
});
