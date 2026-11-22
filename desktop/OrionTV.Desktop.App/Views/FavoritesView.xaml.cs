using System.Windows.Controls;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class FavoritesView : UserControl, INavigationAware
{
    private readonly IAppHost _host;

    public FavoritesView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
        Grid.CardClicked += OnCardClicked;
    }

    public void OnNavigatedTo(object? parameter)
    {
        // 成人内容开关关闭时，成人源的收藏不取出（收藏本身保留，开关打开后恢复）
        var items = _host.Services.Favorites.GetVisible(_host.Services.IsAdultSourceHidden)
            .OrderByDescending(kv => kv.Value.SaveTime)
            .Select(kv =>
            {
                var separator = kv.Key.IndexOf('+');
                var source = separator >= 0 ? kv.Key[..separator] : kv.Key;
                var id = separator >= 0 ? kv.Key[(separator + 1)..] : "";
                var favorite = kv.Value;
                return new CatalogItem(id, favorite.Title, favorite.Cover, source, favorite.SourceName, favorite.Year);
            })
            .ToList();

        Grid.ShowResults(items, "暂无收藏");
    }

    private void OnCardClicked(CatalogItem item) =>
        _host.Navigate(AppPage.Detail, new DetailParameter(item.Title, item.Source, item.Id));
}
