using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace OrionTV.Desktop.App;

/// <summary>
/// Image 的附加属性 ImageLoader.SourceUrl：异步下载远程图片并设为 Source。
/// 豆瓣图片域名自动附加 Referer（对齐安卓 getImageSource 的 header 处理）。
/// </summary>
public static class ImageLoader
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new();

    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.RegisterAttached(
            "SourceUrl",
            typeof(string),
            typeof(ImageLoader),
            new PropertyMetadata(null, OnSourceUrlChanged));

    public static void SetSourceUrl(DependencyObject element, string? value) => element.SetValue(SourceUrlProperty, value);

    public static string? GetSourceUrl(DependencyObject element) => (string?)element.GetValue(SourceUrlProperty);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        return client;
    }

    private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
        {
            return;
        }

        image.Source = null;
        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (Cache.TryGetValue(url, out var cached))
        {
            image.Source = cached;
            return;
        }

        try
        {
            var bitmap = await LoadAsync(url).ConfigureAwait(true);
            if (bitmap is not null && GetSourceUrl(image) == url)
            {
                // 简单容量上限，避免长时间浏览导致缓存无限增长。
                if (Cache.Count > 500)
                {
                    Cache.Clear();
                }

                Cache[url] = bitmap;
                image.Source = bitmap;
            }
        }
        catch
        {
            // 图片加载失败时保持空白占位。
        }
    }

    private static async Task<BitmapSource?> LoadAsync(string url)
    {
        byte[] bytes;
        if (TryGetLocalPath(url, out var localPath))
        {
            bytes = await File.ReadAllBytesAsync(localPath).ConfigureAwait(false);
        }
        else
        {
            var requestUrl = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (requestUrl.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Referer", "https://movie.douban.com/");
            }

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        return await Task.Run<BitmapSource?>(() =>
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
            catch
            {
                return null;
            }
        }).ConfigureAwait(false);
    }

    private static bool TryGetLocalPath(string value, out string path)
    {
        path = "";
        try
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
                return File.Exists(path);
            }

            if (Path.IsPathFullyQualified(value) && File.Exists(value))
            {
                path = Path.GetFullPath(value);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }
}
