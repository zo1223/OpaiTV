using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrionTV.Desktop.App.LiveTranslation;
using OrionTV.Desktop.App.LocalPlayback;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class SettingsView : UserControl, INavigationAware
{
    private readonly IAppHost _host;
    private readonly ObservableCollection<DesktopLiveSource> _additionalLiveSources = [];
    private bool _loaded;

    public SettingsView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        AdditionalLiveSourcesList.ItemsSource = _additionalLiveSources;
        Header.BackRequested += (_, _) => _host.GoBack();
    }

    public void OnNavigatedTo(object? parameter)
    {
        _loaded = false;
        var settings = _host.Services.Settings;
        ApiBox.Text = settings.ApiBaseUrl;
        SubBox.Text = settings.SubscriptionUrl;
        M3uBox.Text = settings.M3uUrl;
        AutoSkipToggle.IsChecked = settings.AutoSkipAds;
        VodCacheDirectoryBox.Text = VodCacheSettings.ResolveDirectory(settings.VodCacheDirectory);
        VodCacheStatusText.Text = "";
        UsePotPlayerToggle.IsChecked = settings.UsePotPlayerForLocal;
        PotPlayerPathBox.Text = string.IsNullOrWhiteSpace(settings.PotPlayerPath)
            ? PotPlayerLauncher.DefaultPath
            : settings.PotPlayerPath;
        UpdatePotPlayerStatus();
        TranslationModelBox.Text = settings.LiveTranslationModel;
        TranslationBaseUrlBox.Text = settings.LiveTranslationBaseUrl ?? "";
        TranslationDubbingToggle.IsChecked = settings.LiveTranslationDubbing;
        DoubaoAppKeyBox.Text = settings.DoubaoAppKey ?? "";
        SelectTranslationProvider(settings.LiveTranslationProvider);
        OpenAiKeyBox.Password = "";
        ReplaceAdditionalLiveSources(settings.AdditionalLiveSources, settings.M3uUrl);
        LiveSourceNameBox.Text = "";
        LiveSourceUrlBox.Text = "";
        LiveSourceEditStatus.Text = "";
        UpdateProviderDependentUi();
        UpdateOpenAiKeyStatus();
        UpdateSubHint();
        SaveButton.IsEnabled = false;
        StatusText.Text = "";
        _loaded = true;
    }

    private void SelectTranslationProvider(string? provider)
    {
        var tag = GeminiLiveTranslateProtocol.ResolveProvider(provider, TranslationModelBox.Text);
        foreach (var item in TranslationProviderBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                TranslationProviderBox.SelectedItem = item;
                return;
            }
        }

        TranslationProviderBox.SelectedIndex = 0;
    }

    private string GetSelectedTranslationProvider()
    {
        if (TranslationProviderBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && !string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }

        return "doubao";
    }

    private void TranslationProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        UpdateProviderDependentUi();
        UpdateOpenAiKeyStatus();
        OnFieldChanged(sender, e);
    }

    private void UpdateProviderDependentUi()
    {
        var provider = GetSelectedTranslationProvider();
        var isDoubao = provider == "doubao";
        var isOpenAi = provider == "openai";
        var isDeepgram = provider == "deepgram";
        var isGemini = provider == "gemini";
        var needsChat = isOpenAi || isDeepgram;

        DoubaoAppKeyLabel.Visibility = isDoubao ? Visibility.Visible : Visibility.Collapsed;
        DoubaoAppKeyBox.Visibility = isDoubao ? Visibility.Visible : Visibility.Collapsed;
        DoubaoResourceHint.Visibility = isDoubao ? Visibility.Visible : Visibility.Collapsed;
        TranslationBaseUrlLabel.Visibility = needsChat ? Visibility.Visible : Visibility.Collapsed;
        TranslationBaseUrlBox.Visibility = needsChat ? Visibility.Visible : Visibility.Collapsed;
        TranslationBaseUrlHint.Visibility = needsChat ? Visibility.Visible : Visibility.Collapsed;
        ChatKeyLabel.Visibility = isDeepgram ? Visibility.Visible : Visibility.Collapsed;
        ChatKeyBox.Visibility = isDeepgram ? Visibility.Visible : Visibility.Collapsed;
        ChatKeyHint.Visibility = isDeepgram ? Visibility.Visible : Visibility.Collapsed;

        ApiKeyLabel.Text = isDeepgram
            ? "Deepgram API Key"
            : isDoubao
                ? "豆包 Access Token"
                : isGemini
                    ? "Gemini API Key"
                    : "API Key / Access Token";

        if (isDeepgram
            && (string.IsNullOrWhiteSpace(TranslationModelBox.Text)
                || DeepgramSttProtocol.IsDeepgramModel(TranslationModelBox.Text)
                || GeminiLiveTranslateProtocol.IsGeminiLiveTranslateModel(TranslationModelBox.Text)
                || DoubaoAstProtocol.IsDoubaoAstModel(TranslationModelBox.Text)))
        {
            // Deepgram 模式下此框表示「Chat 翻译模型」
            TranslationModelBox.Text = "doubao-seed-2-0-pro-260215";
        }
        else if (isDoubao
                 && (string.IsNullOrWhiteSpace(TranslationModelBox.Text)
                     || GeminiLiveTranslateProtocol.IsGeminiLiveTranslateModel(TranslationModelBox.Text)
                     || DeepgramSttProtocol.IsDeepgramModel(TranslationModelBox.Text)))
        {
            TranslationModelBox.Text = DoubaoAstProtocol.DefaultResourceId;
        }
        else if (isGemini
                 && (string.IsNullOrWhiteSpace(TranslationModelBox.Text)
                     || DoubaoAstProtocol.IsDoubaoAstModel(TranslationModelBox.Text)
                     || DeepgramSttProtocol.IsDeepgramModel(TranslationModelBox.Text)))
        {
            TranslationModelBox.Text = GeminiLiveTranslateProtocol.DefaultModel;
        }

        // Deepgram 无配音
        if (isDeepgram)
        {
            TranslationDubbingToggle.IsChecked = false;
            TranslationDubbingToggle.IsEnabled = false;
        }
        else
        {
            TranslationDubbingToggle.IsEnabled = true;
        }
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            SaveButton.IsEnabled = true;
            StatusText.Text = "";
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        StatusText.Text = "保存中…";

        var newSubscription = SubBox.Text.Trim();
        var currentSettings = _host.Services.Settings;

        try
        {
            var provider = GetSelectedTranslationProvider();
            var modelText = TranslationModelBox.Text.Trim();
            if (provider == "gemini" && string.IsNullOrWhiteSpace(modelText))
            {
                modelText = GeminiLiveTranslateProtocol.DefaultModel;
                TranslationModelBox.Text = modelText;
            }
            else if (provider == "doubao" && string.IsNullOrWhiteSpace(modelText))
            {
                modelText = DoubaoAstProtocol.DefaultResourceId;
                TranslationModelBox.Text = modelText;
            }
            else if (provider == "deepgram" && string.IsNullOrWhiteSpace(modelText))
            {
                modelText = "doubao-seed-2-0-pro-260215";
                TranslationModelBox.Text = modelText;
            }

            var settings = DesktopSettingsForm.ApplyEditableFields(
                currentSettings,
                newSubscription,
                ApiBox.Text.Trim(),
                M3uBox.Text.Trim(),
                AutoSkipToggle.IsChecked == true,
                modelText,
                _additionalLiveSources,
                liveTranslationBaseUrl: TranslationBaseUrlBox.Text,
                liveTranslationProvider: provider,
                liveTranslationDubbing: provider == "deepgram"
                    ? false
                    : TranslationDubbingToggle.IsChecked == true,
                doubaoAppKey: DoubaoAppKeyBox.Text,
                doubaoResourceId: provider == "doubao" ? modelText : null,
                // 边下边播已移除：详情页下载始终保留文件，预缓冲参数仅保留兼容旧设置。
                weakNetworkCacheEnabled: false,
                vodPrebufferSeconds: 15,
                vodCacheDirectory: VodCacheDirectoryBox.Text,
                vodCacheRetention: VodCacheSettings.Keep,
                usePotPlayerForLocal: UsePotPlayerToggle.IsChecked == true,
                potPlayerPath: PotPlayerPathBox.Text);

            if (!string.IsNullOrWhiteSpace(OpenAiKeyBox.Password))
            {
                if (provider == "deepgram"
                    && !DeepgramSttProtocol.LooksLikeApiKey(OpenAiKeyBox.Password))
                {
                    StatusText.Text = "Deepgram Key 格式不对：请粘贴 console 里的英文/数字 Key，不要粘中文";
                    SaveButton.IsEnabled = true;
                    return;
                }

                OpenAiCredentialStore.SaveApiKeyForProvider(provider, OpenAiKeyBox.Password);
                OpenAiKeyBox.Password = "";
            }

            // Deepgram 模式可单独保存中转 Chat Key
            if (provider == "deepgram" && !string.IsNullOrWhiteSpace(ChatKeyBox.Password))
            {
                OpenAiCredentialStore.SaveApiKeyForProvider("openai", ChatKeyBox.Password);
                ChatKeyBox.Password = "";
            }

            SettingsStore.Save(settings);
            _host.Services.Settings = settings;

            if (string.IsNullOrEmpty(newSubscription))
            {
                _host.Services.ClearSources();
            }
            else if (DesktopSettingsForm.ShouldReloadSubscription(
                         currentSettings,
                         newSubscription,
                         _host.Services.HasSources))
            {
                await _host.Services.LoadSubscriptionAsync(newSubscription);
            }

            ReplaceAdditionalLiveSources(settings.AdditionalLiveSources, settings.M3uUrl);

            UpdateOpenAiKeyStatus();
            UpdateSubHint();
            LiveSourceEditStatus.Text = _additionalLiveSources.Count == 0
                ? string.IsNullOrWhiteSpace(settings.M3uUrl)
                    ? "当前未配置直播源"
                    : "当前仅使用主要直播源"
                : $"已保存 {_additionalLiveSources.Count} 个手动直播源";
            var sourceCount = _host.Services.Sources.Count;
            StatusText.Text = $"保存成功（当前 {sourceCount} 个视频源）";
        }
        catch
        {
            StatusText.Text = "保存失败：请检查订阅地址、直播源或翻译 API Key";
            SaveButton.IsEnabled = true;
        }
    }

    private void AddLiveSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var url = LiveSourceUrlBox.Text.Trim();
        if (!DesktopLiveSourceSettings.IsSupportedUrl(url))
        {
            LiveSourceEditStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            LiveSourceEditStatus.Text = "请输入有效的 http/https M3U 地址";
            return;
        }

        if (url.Equals(M3uBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
            || _additionalLiveSources.Any(source => source.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            LiveSourceEditStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            LiveSourceEditStatus.Text = "该直播源地址已经存在";
            return;
        }

        var name = LiveSourceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"直播源 {_additionalLiveSources.Count + 1}";
        }

        _additionalLiveSources.Add(new DesktopLiveSource(name, url));
        LiveSourceNameBox.Text = "";
        LiveSourceUrlBox.Text = "";
        LiveSourceEditStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        LiveSourceEditStatus.Text = "已加入列表，点击“保存设置”后生效";
        SaveButton.IsEnabled = true;
        StatusText.Text = "";
    }

    private void DeleteLiveSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DesktopLiveSource source })
        {
            return;
        }

        _additionalLiveSources.Remove(source);
        LiveSourceEditStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        LiveSourceEditStatus.Text = "已从列表移除，点击“保存设置”后生效";
        SaveButton.IsEnabled = true;
        StatusText.Text = "";
    }

    private void ReplaceAdditionalLiveSources(IEnumerable<DesktopLiveSource>? sources, string primaryUrl)
    {
        _additionalLiveSources.Clear();
        foreach (var source in DesktopLiveSourceSettings.Normalize(sources, primaryUrl))
        {
            _additionalLiveSources.Add(source);
        }
    }

    private void DeleteOpenAiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var provider = GetSelectedTranslationProvider();
            OpenAiCredentialStore.DeleteStoredApiKeyForProvider(provider);
            OpenAiKeyBox.Password = "";
            UpdateOpenAiKeyStatus();
            StatusText.Text = provider == "gemini"
                ? "已删除本机保存的 Gemini API Key"
                : "已删除本机保存的 OpenAI/中转 API Key";
        }
        catch
        {
            StatusText.Text = "删除失败：无法访问 Windows 凭据管理器";
        }
    }

    private void UpdateOpenAiKeyStatus()
    {
        var provider = GetSelectedTranslationProvider();
        var isGemini = provider == "gemini";
        var isDoubao = provider == "doubao";
        var isDeepgram = provider == "deepgram";
        var envName = isGemini
            ? "GEMINI_API_KEY / GOOGLE_API_KEY"
            : isDoubao
                ? "DOUBAO_ACCESS_KEY"
                : isDeepgram
                    ? "DEEPGRAM_API_KEY"
                    : "OPENAI_API_KEY";
        var hasEnvironmentKey = isGemini
            ? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
              || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"))
            : isDoubao
                ? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOUBAO_ACCESS_KEY"))
                  || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOLC_ACCESS_KEY"))
                : isDeepgram
                    ? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY"))
                    : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var hasStoredKey = OpenAiCredentialStore.HasStoredApiKeyForProvider(provider);
        var hasAppKey = !string.IsNullOrWhiteSpace(DoubaoAppKeyBox.Text);
        var hasChatKey = OpenAiCredentialStore.HasStoredApiKeyForProvider("openai");
        OpenAiKeyStatus.Text = hasEnvironmentKey
            ? $"已通过 {envName} 环境变量配置"
            : hasStoredKey
                ? isDeepgram
                    ? (hasChatKey
                        ? "Deepgram Key 已保存；中转 Chat Key 也已就绪，可开中译字幕"
                        : "Deepgram Key 已保存；请再配置中转 Chat Key（下方或 OpenAI 槽）")
                    : isDoubao
                        ? (hasAppKey
                            ? "豆包 Access Token 已保存；直播页可开中译（字幕+配音）"
                            : "Access Token 已保存，请再填写上方 APP ID")
                        : isGemini
                            ? "Gemini API Key 已保存；直播页可开中译（字幕+配音）"
                            : "API Key 已安全保存，可直接在直播页开启中译字幕"
                : isDeepgram
                    ? "尚未配置 Deepgram Key；到 console.deepgram.com 注册领 $200 后粘贴"
                    : isDoubao
                        ? "尚未配置 Access Token；到 console.volcengine.com/speech 开通同声传译 2.0 后粘贴"
                        : isGemini
                            ? "尚未配置 Gemini Key；到 aistudio.google.com/apikey 免费申请后粘贴保存"
                            : "尚未配置；输入后点击“保存设置”";
        DeleteOpenAiKeyButton.IsEnabled = hasStoredKey;
    }

    private void UpdateSubHint()
    {
        var count = _host.Services.Sources.Count;
        SubHint.Text = count > 0 ? $"已加载 {count} 个源" : "";
    }

    private void ChooseVodCacheDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择点播下载保存目录",
            InitialDirectory = VodCacheSettings.ResolveDirectory(VodCacheDirectoryBox.Text),
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            VodCacheDirectoryBox.Text = dialog.FolderName;
            SaveButton.IsEnabled = true;
        }
    }

    private void BrowsePotPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 PotPlayerMini64.exe",
            Filter = "PotPlayer|PotPlayerMini64.exe;PotPlayerMini.exe;PotPlayer64.exe;PotPlayer.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };
        var current = PotPlayerLauncher.ResolveExecutable(PotPlayerPathBox.Text);
        if (!string.IsNullOrWhiteSpace(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }

        if (dialog.ShowDialog() == true)
        {
            PotPlayerPathBox.Text = dialog.FileName;
            UpdatePotPlayerStatus();
            SaveButton.IsEnabled = true;
        }
    }

    private void UpdatePotPlayerStatus()
    {
        var resolved = PotPlayerLauncher.ResolveExecutable(PotPlayerPathBox.Text);
        PotPlayerStatusText.Text = string.IsNullOrWhiteSpace(resolved)
            ? "未找到 PotPlayer。请安装或浏览选择 PotPlayerMini64.exe（默认尝试 E:\\potPlayer\\…）"
            : "已找到：" + resolved;
    }

    private void OpenVodCacheDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = VodCacheSettings.ResolveDirectory(VodCacheDirectoryBox.Text);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch
        {
            VodCacheStatusText.Text = "无法打开下载目录";
        }
    }

    private async void ClearVodCacheButton_Click(object sender, RoutedEventArgs e)
    {
        VodCacheStatusText.Text = "正在清理…";
        try
        {
            var deleted = await _host.Services.VodCache.ClearAsync(VodCacheDirectoryBox.Text);
            VodCacheStatusText.Text = deleted > 0 ? $"已清理 {deleted} 个下载目录" : "没有可清理的下载";
        }
        catch
        {
            VodCacheStatusText.Text = "清理失败；正在下载或被其他程序占用的文件不会删除";
        }
    }
}
