using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Controls;

/// <summary>海报网格容器：WrapPanel 卡片流 + 无限滚动 + 加载/空态/消息。</summary>
public partial class CardGrid : UserControl
{
    private readonly ObservableCollection<CatalogItem> _items = new();

    // 等待播放入场动画的卡片：条目（按引用）→ 它在本批里的序号。
    // 只登记 ShowResults / AppendResults 新加进来的条目；ReplaceItem 换上的条目不在里面，
    // 所以可播性探测刷新卡片、页面切走再回来时都不会重播动画。
    private readonly Dictionary<CatalogItem, int> _pendingEntrance = new(ReferenceEqualityComparer.Instance);

    public CardGrid()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
    }

    /// <summary>卡片被点击。</summary>
    public event Action<CatalogItem>? CardClicked;

    /// <summary>滚动接近底部，请求加载更多。</summary>
    public event Action? EndReached;

    /// <summary>
    /// 右键卡片时菜单里“删除”项的文字；为 null（默认）时卡片没有右键菜单。
    /// 首页只在“观看历史”分类下设置它。
    /// </summary>
    public string? RemoveMenuText { get; set; }

    /// <summary>在右键菜单里点了删除。</summary>
    public event Action<CatalogItem>? CardRemoveRequested;

    public int Count => _items.Count;

    /// <summary>
    /// 卡片流上方的头部内容（首页横幅）。与卡片在同一个 ScrollViewer 里，随内容一起滚走；
    /// 设为 null 时不占位。
    /// </summary>
    public UIElement? Header
    {
        get => HeaderHost.Content as UIElement;
        set
        {
            HeaderHost.Content = value;
            HeaderHost.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>从网格里移除一张卡片（不重新加载整个列表）。</summary>
    public bool RemoveItem(CatalogItem target, string emptyMessage = "暂无内容")
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (ReferenceEquals(item, target) || (item.Source == target.Source && item.Id == target.Id))
            {
                _items.RemoveAt(i);
                ShowMessageIfEmpty(emptyMessage);
                return true;
            }
        }

        return false;
    }

    public void BeginLoading(string message = "加载中…")
    {
        MessageText.Text = message;
        MessageText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowResults(IEnumerable<CatalogItem> results, string emptyMessage = "暂无内容")
    {
        // 已经在屏幕上的卡片（按标题认）不重播入场动画：增量搜索每到一批结果都会整表重刷，
        // 收藏 / 观看历史刷新时内容也基本不变，这些情况下只有新出现的卡片淡入。
        var alreadyShown = _items.Count > 0
            ? new HashSet<string>(_items.Select(item => item.Title), StringComparer.Ordinal)
            : null;

        _items.Clear();
        _pendingEntrance.Clear();
        var index = 0;
        foreach (var item in results)
        {
            if (alreadyShown is null || !alreadyShown.Contains(item.Title))
            {
                QueueEntrance(item, index++);
            }

            _items.Add(item);
        }

        SetLoadingMore(false);
        ShowMessageIfEmpty(emptyMessage);
        EnsureFilled();
    }

    public void AppendResults(IEnumerable<CatalogItem> results)
    {
        var index = 0;
        foreach (var item in results)
        {
            QueueEntrance(item, index++);
            _items.Add(item);
        }

        if (_items.Count > 0)
        {
            MessageText.Visibility = Visibility.Collapsed;
        }

        EnsureFilled();
    }

    public bool ReplaceItem(CatalogItem current, CatalogItem updated)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (ReferenceEquals(item, current)
                || (item.Source == current.Source && item.Id == current.Id && item.Title == current.Title))
            {
                // 被替换的卡片如果还没来得及入场，把入场动画交给替换后的卡片。
                if (_pendingEntrance.Remove(item, out var indexInBatch))
                {
                    _pendingEntrance[updated] = indexInBatch;
                }

                _items[i] = updated;
                return true;
            }
        }

        return false;
    }

    public void ShowMessage(string message)
    {
        _items.Clear();
        _pendingEntrance.Clear();
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
        SetLoadingMore(false);
    }

    public void SetLoadingMore(bool loading) =>
        FooterText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

    private void EnsureFilled()
    {
        // 首屏/追加后若内容没超出视口（没有滚动条），无限滚动无从触发，
        // 主动请求下一页，直到内容可滚动或没有更多（由 EndReached 处理方的 _hasMore 终止）。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_items.Count > 0 && Scroll.ScrollableHeight <= 0)
            {
                EndReached?.Invoke();
            }
        }));
    }

    private void ShowMessageIfEmpty(string emptyMessage)
    {
        if (_items.Count == 0)
        {
            MessageText.Text = emptyMessage;
            MessageText.Visibility = Visibility.Visible;
        }
        else
        {
            MessageText.Visibility = Visibility.Collapsed;
        }
    }

    private void QueueEntrance(CatalogItem item, int indexInBatch)
    {
        // 只有一屏左右的卡片播放入场动画，其余直接出现。
        if (Motion.Enabled && EntranceStagger.DelayFor(indexInBatch) is not null)
        {
            _pendingEntrance[item] = indexInBatch;
        }
    }

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (_pendingEntrance.Count == 0 || sender is not VideoCardControl { Item: { } item } card)
        {
            return;
        }

        if (_pendingEntrance.Remove(item, out var indexInBatch)
            && EntranceStagger.DelayFor(indexInBatch) is int delayMs)
        {
            card.PlayEntrance(delayMs);
        }
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is VideoCardControl { Item: { } item })
        {
            CardClicked?.Invoke(item);
        }
    }

    private void Items_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(RemoveMenuText) || e.OriginalSource is not DependencyObject origin)
        {
            return;
        }

        var card = FindCard(origin);
        if (card?.Item is not { } item)
        {
            return;
        }

        var remove = new MenuItem { Header = RemoveMenuText };
        remove.Click += (_, _) => CardRemoveRequested?.Invoke(item);
        var menu = new ContextMenu { PlacementTarget = card };
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static VideoCardControl? FindCard(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is VideoCardControl card)
            {
                return card;
            }

            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0)
        {
            return;
        }

        if (Scroll.ScrollableHeight > 0 && Scroll.VerticalOffset >= Scroll.ScrollableHeight - 240)
        {
            EndReached?.Invoke();
        }
    }
}
