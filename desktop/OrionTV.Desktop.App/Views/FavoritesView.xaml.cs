using System.Windows.Controls;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class FavoritesView : UserControl, INavigationAware, IDisposable
{
    private readonly IAppHost _host;
    private bool _disposed;

    public FavoritesView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
        Grid.CardClicked += OnCardClicked;
        _host.Services.FavoritesChanged += OnFavoritesChanged;
    }

    public void OnNavigatedTo(object? parameter)
    {
        // 成人内容开关关闭时，成人源、成人分类的收藏不取出（收藏本身保留，开关打开后恢复）
        var items = _host.Services.Favorites
            .GetVisibleEntries(_host.Services.IsAdultSourceHidden, _host.Services.IsAdultCategoryHidden)
            .Select(entry => new CatalogItem(
                entry.Id,
                entry.Item.Title,
                entry.Item.Cover,
                entry.Source,
                entry.Item.SourceName,
                entry.Item.Year))
            .ToList();

        Grid.ShowResults(items, "暂无收藏");
    }

    // 侧滑面板里删了收藏：已打开的收藏页跟着刷新
    private void OnFavoritesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed)
        {
            OnNavigatedTo(null);
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Services.FavoritesChanged -= OnFavoritesChanged;
    }

    private void OnCardClicked(CatalogItem item) =>
        _host.Navigate(AppPage.Detail, new DetailParameter(item.Title, item.Source, item.Id));
}
