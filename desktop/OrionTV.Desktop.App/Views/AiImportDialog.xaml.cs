using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class AiImportDialog : Window
{
    private readonly IAppHost _host;
    private readonly AiCliTool _tool;

    public AiImportDialog(IAppHost host, AiCliTool tool)
    {
        InitializeComponent();
        _host = host;
        _tool = tool;
        TitleText.Text = tool == AiCliTool.ClaudeCode ? "Claude Code" : "Codex";
        Title = "AI 解析视频源 · " + TitleText.Text;
        OutputPathText.Text = ExtraVideoSourcesStore.FilePath;
        var docsDirectory = AiCliLauncher.ResolveDocsDirectory();
        DocsPathText.Text = docsDirectory is null
            ? "（未找到 docs/ORION_SOURCE_GUIDE.md，请使用完整发布包）"
            : Path.Combine(docsDirectory, "ORION_SOURCE_GUIDE.md");
        StatusText.Text = $"当前已加载视频源 {_host.Services.Sources.Count} 个"
            + $"（含 AI 额外源 {_host.Services.ExtraSourceCount} 个）。";
        UrlBox.Focus();
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLaunchResult(AiCliLauncher.Launch(_tool, UrlBox.Text, NotesBox.Text));
    }

    private void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLaunchResult(AiCliLauncher.LaunchDiscovery(_tool, NotesBox.Text));
    }

    private void ShowLaunchResult(AiCliLaunchResult result)
    {
        StatusText.Text = result.Message;
        StatusText.Foreground = (Brush)FindResource(
            result.Success ? "PrimaryBrush" : "DangerBrush");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Foreground = (Brush)FindResource("MutedTextBrush");
            StatusText.Text = "正在刷新源…";
            var count = await _host.Services.ReloadSourcesFromDiskAsync();
            StatusText.Foreground = (Brush)FindResource("PrimaryBrush");
            StatusText.Text = $"已重新加载，当前共 {count} 个视频源"
                + $"（AI 额外源 {_host.Services.ExtraSourceCount} 个）。"
                + "可在「搜索」中使用。";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            StatusText.Text = "刷新失败：" + ex.Message;
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopPaths.EnsureDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = DesktopPaths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            StatusText.Text = "打开目录失败：" + ex.Message;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
