using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Controls;

public partial class VideoCardControl : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(CatalogItem), typeof(VideoCardControl),
            new PropertyMetadata(null, OnItemChanged));

    public static readonly RoutedEvent ClickEvent =
        EventManager.RegisterRoutedEvent(nameof(Click), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(VideoCardControl));

    public VideoCardControl()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    public CatalogItem? Item
    {
        get => (CatalogItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((VideoCardControl)d).Render();
    }

    private void Render()
    {
        var item = Item;
        if (item is null)
        {
            return;
        }

        ImageLoader.SetSourceUrl(PosterImage, item.Poster);
        TitleText.Text = item.Title;

        var playabilityLabel = item.Source == "douban"
            ? CatalogPlayabilityBadge.GetLabel(item.PlayabilityStatus, item.PlayableSourceCount)
            : null;
        if (!string.IsNullOrWhiteSpace(playabilityLabel))
        {
            SourceText.Text = playabilityLabel;
            SourceBadge.Background = item.PlayabilityStatus == CatalogPlayabilityStatus.Playable
                ? new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0xA8, 0x62))
                : new SolidColorBrush(Color.FromArgb(0xCC, 0x7F, 0x1D, 0x1D));
            SourceBadge.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrWhiteSpace(item.SourceName) && item.Source != "douban")
        {
            SourceText.Text = item.SourceName;
            SourceBadge.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
            SourceBadge.Visibility = Visibility.Visible;
        }
        else
        {
            SourceBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(item.Rate))
        {
            MetaText.Text = $"★ {item.Rate}";
            MetaText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            MetaBadge.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrWhiteSpace(item.Year))
        {
            MetaText.Text = item.Year;
            MetaText.Foreground = Brushes.White;
            MetaBadge.Visibility = Visibility.Visible;
        }
        else
        {
            MetaBadge.Visibility = Visibility.Collapsed;
        }

        var progress = item.Progress ?? 0;
        var isContinueWatching = progress > 0 && progress < 1;
        if (isContinueWatching)
        {
            ProgressTrack.Visibility = Visibility.Visible;
            ProgressBar.Width = 150 * progress;
            if (item.EpisodeIndex is int episode)
            {
                ContinueText.Text = $"第{episode + 1}集 已观看 {(int)Math.Round(progress * 100)}%";
                ContinueText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
            ContinueText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMouseEnter(object sender, RoutedEventArgs e)
    {
        CardBorder.BorderBrush = (Brush)FindResource("PrimaryBrush");
    }

    private void OnMouseLeave(object sender, RoutedEventArgs e)
    {
        CardBorder.BorderBrush = Brushes.Transparent;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            RaiseEvent(new RoutedEventArgs(ClickEvent, this));
        }
    }
}
