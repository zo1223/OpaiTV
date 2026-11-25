using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class IncrementalSearchTests
{
    private static VideoSource Source(string key) => new(key, key.ToUpperInvariant(), $"https://{key}.example/api");

    private static SearchResult Result(string source, string id, string title, string poster = "") =>
        new(id, title, poster, ["https://v.example/1.m3u8"], source, source.ToUpperInvariant(), "2024");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task ReportsEachSourceAsItArrives_WithoutWaitingForSlowOnes()
    {
        var sources = new[] { Source("a"), Source("b") };
        var gates = sources.ToDictionary(s => s.Key, _ => new TaskCompletionSource<IReadOnlyList<SearchResult>>());
        var snapshots = new List<SearchProgress>();

        var run = IncrementalSearch.RunAsync(sources, (source, _) => gates[source.Key].Task, snapshots.Add);

        // 两个源都已开始，还没有任何结果
        Assert.All(snapshots[^1].Sources, status => Assert.Equal(SourceSearchState.Searching, status.State));
        Assert.Empty(snapshots[^1].Items);

        // b 先回来：立刻有结果，a 仍在搜索中
        gates["b"].SetResult([Result("b", "1", "流浪地球")]);
        await WaitForAsync(() => { lock (snapshots) { return snapshots[^1].Completed == 1; } });
        var afterB = snapshots[^1];
        Assert.Equal(SourceSearchState.Searching, afterB.Sources[0].State);
        Assert.Equal(SourceSearchState.Done, afterB.Sources[1].State);
        Assert.Equal(1, afterB.Sources[1].Count);
        Assert.Single(afterB.Items);
        Assert.False(afterB.IsFinished);
        Assert.Equal("已搜完 1/2 个源，共 1 条", afterB.Summary);
        Assert.False(run.IsCompleted);

        gates["a"].SetResult([Result("a", "9", "流浪 地球"), Result("a", "10", "三体")]);
        var final = await run;

        Assert.True(final.IsFinished);
        Assert.Equal("已搜完 2 个源，共 2 条", final.Summary);
        // 同名条目按标题归并成一张卡片，两个源都在候选线路里；先到的排在前面
        Assert.Equal(new[] { "流浪地球", "三体" }, final.Items.Select(item => item.Title).ToArray());
        Assert.Equal(2, final.Items[0].PlayableSourceCount);
        Assert.Equal(new[] { "b", "a" }, final.Items[0].CandidateSources!.Select(item => item.Source).ToArray());
        Assert.Same(final, snapshots[^1]);
    }

    [Fact]
    public async Task FailedAndTimedOutSourcesDoNotBlockOthers()
    {
        var sources = new[] { Source("bad"), Source("slow"), Source("ok") };
        var never = new TaskCompletionSource<IReadOnlyList<SearchResult>>();

        var final = await IncrementalSearch.RunAsync(
            sources,
            (source, _) => source.Key switch
            {
                "bad" => Task.FromException<IReadOnlyList<SearchResult>>(new HttpRequestException("boom")),
                "slow" => never.Task,
                _ => Task.FromResult<IReadOnlyList<SearchResult>>([Result("ok", "1", "三体")]),
            },
            onProgress: null,
            sourceTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(SourceSearchState.Failed, final.Sources[0].State);
        Assert.Equal(SourceSearchState.TimedOut, final.Sources[1].State);
        Assert.Equal(SourceSearchState.Done, final.Sources[2].State);
        Assert.True(final.IsFinished);
        Assert.Single(final.Items);
        Assert.Equal("失败", final.Sources[0].StateText);
        Assert.Equal("超时", final.Sources[1].StateText);
        Assert.Equal("1 条", final.Sources[2].StateText);
    }

    [Fact]
    public async Task CancelledSearchStopsReporting()
    {
        var sources = new[] { Source("a") };
        var gate = new TaskCompletionSource<IReadOnlyList<SearchResult>>();
        var snapshots = new List<SearchProgress>();
        using var cts = new CancellationTokenSource();

        var run = IncrementalSearch.RunAsync(sources, (_, _) => gate.Task, snapshots.Add, cts.Token);
        var before = snapshots.Count;

        // 新的搜索开始 → 旧搜索被取消；它迟到的结果不能再回报
        cts.Cancel();
        gate.SetResult([Result("a", "1", "迟到的结果")]);
        var final = await run;

        Assert.Equal(before, snapshots.Count);
        Assert.Empty(final.Items);
    }

    [Fact]
    public async Task NoSources_FinishesImmediately()
    {
        var snapshots = new List<SearchProgress>();

        var final = await IncrementalSearch.RunAsync([], (_, _) => throw new InvalidOperationException(), snapshots.Add);

        Assert.True(final.IsFinished);
        Assert.Empty(final.Items);
        Assert.Single(snapshots);
    }
}
