using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Controls;

/// <summary>
/// 收藏侧滑面板：从内容区右侧滑入，列出收藏；点条目进详情，可单条取消收藏。
/// 面板自己不读写存储，取数与删除都通过事件交给宿主窗口。
/// </summary>
public partial class FavoritesPanel : UserControl
{
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(180));
    private readonly ObservableCollection<FavoriteEntry> _entries = new();

    public FavoritesPanel()
    {
        InitializeComponent();
        Items.ItemsSource = _entries;
    }

    /// <summary>点了某条收藏（宿主负责关面板并进详情）。</summary>
    public event Action<FavoriteEntry>? EntryClicked;

    /// <summary>点了某条收藏的删除按钮。</summary>
    public event Action<FavoriteEntry>? RemoveRequested;

    /// <summary>点了“查看全部收藏”。</summary>
    public event Action? ViewAllRequested;

    /// <summary>面板关闭动画结束之后触发。</summary>
    public event Action? Closed;

    public bool IsOpen { get; private set; }

    public void SetEntries(IEnumerable<FavoriteEntry> entries)
    {
        _entries.Clear();
        foreach (var entry in entries)
        {
            _entries.Add(entry);
        }

        TitleText.Text = _entries.Count > 0 ? $"我的收藏（{_entries.Count}）" : "我的收藏";
        EmptyText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Open()
    {
        IsOpen = true;
        Visibility = Visibility.Visible;
        DrawerTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, SlideDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, SlideDuration));
        Focus();
    }

    /// <param name="animate">false 时立即收起（宿主要马上切换页面时用）。</param>
    public void Close(bool animate = true)
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        if (!animate)
        {
            DrawerTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            Scrim.BeginAnimation(OpacityProperty, null);
            DrawerTranslate.X = Drawer.Width;
            Scrim.Opacity = 0;
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
            return;
        }

        var slide = new DoubleAnimation(Drawer.Width, SlideDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        slide.Completed += (_, _) =>
        {
            // 动画期间又被打开了就不要收起。
            if (!IsOpen)
            {
                Visibility = Visibility.Collapsed;
                Closed?.Invoke();
            }
        };
        DrawerTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, SlideDuration));
    }

    private void Scrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ViewAllButton_Click(object sender, RoutedEventArgs e) => ViewAllRequested?.Invoke();

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FavoriteEntry entry })
        {
            e.Handled = true;
            EntryClicked?.Invoke(entry);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        // 按钮自己处理了鼠标事件，不会冒泡成“点了这一行”。
        if (sender is FrameworkElement { DataContext: FavoriteEntry entry })
        {
            RemoveRequested?.Invoke(entry);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsOpen)
        {
            e.Handled = true;
            Close();
        }
    }
}
