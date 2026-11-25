using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class SearchView : UserControl, INavigationAware, IKeyboardFocusTarget, IDisposable
{
    /// <summary>键盘切换到搜索页后的默认焦点：搜索框，光标在末尾。</summary>
    public void FocusDefaultElement()
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private readonly IAppHost _host;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    // 发起过搜索后收起搜索记录；清空搜索框时再展开。
    private bool _hasSearched;

    public SearchView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
        Grid.CardClicked += OnCardClicked;
        // 点卡片直接起播；详情页入口保留在右键菜单里
        Grid.RemoveMenuText = "查看详情";
        Grid.CardRemoveRequested += OpenDetail;
    }

    public void OnNavigatedTo(object? parameter)
    {
        Grid.ShowMessage("输入关键词开始搜索");
        SearchBox.Focus();
        RefreshHistory();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_hasSearched && string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _hasSearched = false;
            IntentHint.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            RefreshHistory();
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _host.Services.SearchHistory.Clear();
        RefreshHistory();
    }

    /// <summary>把搜索记录渲染成标签云：字号随搜索次数与新近度变化。</summary>
    private void RefreshHistory()
    {
        HistoryTags.Children.Clear();
        var tags = SearchHistoryStore.ToTagCloud(_host.Services.SearchHistory.GetAll(), minFontSize: 12.5, maxFontSize: 20);
        foreach (var tag in tags)
        {
            HistoryTags.Children.Add(BuildHistoryTag(tag));
        }

        HistoryPanel.Visibility = tags.Count > 0 && !_hasSearched ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildHistoryTag(SearchTag tag)
    {
        var label = new TextBlock
        {
            Text = tag.Query,
            FontSize = tag.FontSize,
            Foreground = (Brush)FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = $"搜索过 {tag.Count} 次，点击重新搜索",
        };
        label.MouseLeftButtonUp += (_, _) =>
        {
            SearchBox.Text = tag.Query;
            _ = SearchAsync();
        };

        var remove = new TextBlock
        {
            Text = "×",
            FontSize = 15,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "删除这条记录",
        };
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _host.Services.SearchHistory.Remove(tag.Query);
            RefreshHistory();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(remove);
        return new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResource("InputBrush"),
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 5, 8, 5),
            Child = row,
        };
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SearchAsync();
        }
    }

    private void SearchButton_Click(object sender, System.Windows.RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        if (!_host.Services.HasSources)
        {
            Grid.ShowMessage("请先在设置中配置影视源订阅地址");
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        // 搜索记录只存本机；自然语言说法（回南天、高铁候机、夜宵…）额外按题材扩展检索。
        _host.Services.SearchHistory.Record(query);
        _finalShown = false;
        _shownCount = 0;
        _hasSearched = true;
        RefreshHistory();
        var intent = SearchIntents.Resolve(query);
        IntentHint.Text = intent is null ? "" : SearchIntents.Describe(intent);
        IntentHint.Visibility = intent is null ? Visibility.Collapsed : Visibility.Visible;

        Grid.ShowMessage("搜索中…");
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressSummary.Text = "正在搜索…";
        ProgressSources.Children.Clear();
        try
        {
            // 意图检索与关键词搜索同时发出；它内部吞掉所有失败，不会影响关键词结果。
            var intentTask = intent is null
                ? Task.FromResult<IReadOnlyList<CatalogItem>>([])
                : _host.Services.Catalog.SearchByIntentAsync(_host.Services.Sources, intent, cts.Token);
            // 逐源回报：某个源一返回就先把它的结果显示出来，不等全部搜完。
            // 回调可能来自后台线程；新的搜索开始（cts 被取消）后，旧搜索迟到的回调一律丢弃。
            var final = await _host.Services.Catalog.SearchAllIncrementalAsync(
                _host.Services.Sources,
                query,
                progress => Dispatcher.BeginInvoke(() =>
                {
                    if (!cts.Token.IsCancellationRequested && _cts == cts)
                    {
                        RenderProgress(progress);
                    }
                }),
                cts.Token);
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            var results = final.Items;
            if (intent is not null)
            {
                // 关键词结果到了以后最多再等意图检索几秒：慢片源不能拖住关键词结果。
                var grace = Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
                if (await Task.WhenAny(intentTask, grace) == intentTask)
                {
                    results = CatalogService.MergeKeywordAndIntent(results, await intentTask);
                }
            }

            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            // 排在队列里的进度回调先处理完，再落最终结果，避免被迟到的快照盖掉
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (cts.Token.IsCancellationRequested || _cts != cts)
            {
                return;
            }

            _finalShown = true;
            RenderProgressSummary(final);
            Grid.ShowResults(results, final.Total == 0 ? "没有参与搜索的片源（片源模式全部为“关闭”）" : "没有找到相关内容");
        }
        catch
        {
            if (!cts.Token.IsCancellationRequested)
            {
                Grid.ShowMessage("搜索失败，请稍后重试");
            }
        }
    }

    // 最终结果（含意图扩展）已经显示后，不再让迟到的进度快照覆盖列表
    private bool _finalShown;

    private void RenderProgressSummary(SearchProgress progress)
    {
        ProgressSummary.Text = progress.Summary;
        ProgressSources.Children.Clear();
        foreach (var source in progress.Sources)
        {
            var brush = source.State switch
            {
                SourceSearchState.Done => source.Count > 0 ? "PrimaryBrush" : "MutedTextBrush",
                SourceSearchState.Failed or SourceSearchState.TimedOut => "DangerBrush",
                _ => "MutedTextBrush",
            };
            ProgressSources.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = (Brush)FindResource("InputBrush"),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = $"{source.Name} · {source.StateText}",
                    FontSize = 12,
                    Foreground = (Brush)FindResource(brush),
                },
            });
        }
    }

    private void RenderProgress(SearchProgress progress)
    {
        if (_finalShown)
        {
            return;
        }

        RenderProgressSummary(progress);
        if (progress.Items.Count > 0 && progress.Items.Count != _shownCount)
        {
            _shownCount = progress.Items.Count;
            Grid.ShowResults(progress.Items, "没有找到相关内容");
        }
    }

    private int _shownCount;

    private void OpenDetail(CatalogItem item) =>
        _host.Navigate(AppPage.Detail, new DetailParameter(
            item.Title,
            item.Source,
            item.Id,
            item.CandidateSources,
            item.PlayableSources));

    /// <summary>
    /// 点任意一条结果直接起播：有观看记录的线路按记录续播，没有就从第一条有剧集的线路的第一集开始。
    /// 取不到可播剧集时退回详情页。
    /// </summary>
    private async void OnCardClicked(CatalogItem item)
    {
        try
        {
            // 选线逻辑与首页横幅共用，见 DirectPlay（从这里原样搬过去的）
            var play = await DirectPlay.ResolveAsync(_host.Services, item);
            if (play is not null)
            {
                _host.Navigate(AppPage.Player, play);
                return;
            }
        }
        catch
        {
            // 起播准备失败时退回详情页
        }

        OpenDetail(item);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
