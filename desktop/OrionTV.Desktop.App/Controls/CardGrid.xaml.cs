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

    public CardGrid()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
    }

    /// <summary>卡片被点击。</summary>
    public event Action<CatalogItem>? CardClicked;

    /// <summary>滚动接近底部，请求加载更多。</summary>
    public event Action? EndReached;

    public int Count => _items.Count;

    public void BeginLoading(string message = "加载中…")
    {
        MessageText.Text = message;
        MessageText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowResults(IEnumerable<CatalogItem> results, string emptyMessage = "暂无内容")
    {
        _items.Clear();
        foreach (var item in results)
        {
            _items.Add(item);
        }

        SetLoadingMore(false);
        ShowMessageIfEmpty(emptyMessage);
        EnsureFilled();
    }

    public void AppendResults(IEnumerable<CatalogItem> results)
    {
        foreach (var item in results)
        {
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
                _items[i] = updated;
                return true;
            }
        }

        return false;
    }

    public void ShowMessage(string message)
    {
        _items.Clear();
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

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is VideoCardControl { Item: { } item })
        {
            CardClicked?.Invoke(item);
        }
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
