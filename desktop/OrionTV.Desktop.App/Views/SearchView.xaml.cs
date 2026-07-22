using System.Windows.Controls;
using System.Windows.Input;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class SearchView : UserControl, INavigationAware
{
    private readonly IAppHost _host;
    private CancellationTokenSource? _cts;

    public SearchView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
        Grid.CardClicked += OnCardClicked;
    }

    public void OnNavigatedTo(object? parameter)
    {
        Grid.ShowMessage("输入关键词开始搜索");
        SearchBox.Focus();
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
        var cts = new CancellationTokenSource();
        _cts = cts;

        Grid.ShowMessage("搜索中…");
        try
        {
            var results = await _host.Services.Catalog.SearchAllAsync(_host.Services.Sources, query, cts.Token);
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            Grid.ShowResults(results, "没有找到相关内容");
        }
        catch
        {
            if (!cts.Token.IsCancellationRequested)
            {
                Grid.ShowMessage("搜索失败，请稍后重试");
            }
        }
    }

    private void OnCardClicked(CatalogItem item) =>
        _host.Navigate(AppPage.Detail, new DetailParameter(item.Title, item.Source, item.Id));
}
