using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OrionTV.Desktop.App.LiveTranslation;
using OrionTV.Desktop.App.LocalPlayback;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class SettingsView : UserControl, INavigationAware
{
    private readonly IAppHost _host;
    private readonly ObservableCollection<DesktopLiveSource> _additionalLiveSources = [];
    private DesktopUpdateInfo? _availableUpdate;
    private bool _updateBusy;
    private bool _loaded;
    private bool _addSourceBusy;
    private bool _adultToggleSaveFailed;
    // 同步视频源：二维码里的文本（一张，或自动轮播的多张分片）
    private IReadOnlyList<string> _syncQrPayloads = [];
    private int _syncQrIndex;
    private readonly System.Windows.Threading.DispatcherTimer _syncQrTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public SettingsView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        AdditionalLiveSourcesList.ItemsSource = _additionalLiveSources;
        Header.BackRequested += (_, _) => _host.GoBack();
        _syncQrTimer.Tick += (_, _) => ShowSyncQr(_syncQrIndex + 1);
        Unloaded += (_, _) => _syncQrTimer.Stop();
    }

    public void OnNavigatedTo(object? parameter)
    {
        _loaded = false;
        CurrentVersionText.Text = $"当前版本 {DesktopUpdateService.CurrentVersionText}";
        _availableUpdate = parameter as DesktopUpdateInfo ?? _host.Services.Updates.AvailableUpdate;
        RenderUpdateState();
        var settings = _host.Services.Settings;
        ApiBox.Text = settings.ApiBaseUrl;
        SubBox.Text = settings.SubscriptionUrl;
        M3uBox.Text = settings.M3uUrl;
        AutoSkipToggle.IsChecked = settings.AutoSkipAds;
        AdultContentToggle.IsChecked = settings.ShowAdultContent;
        ShowDanmakuSettings(settings);
        AddSourceBox.Text = "";
        AddSourceAdultCheck.IsChecked = false;
        AddSourceStatus.Text = "";
        HideSyncExport();
        RenderVideoSourceState();
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

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
        {
            return;
        }

        if (_availableUpdate is not null)
        {
            await DownloadAndInstallUpdateAsync(_availableUpdate);
            return;
        }

        SetUpdateBusy(true);
        UpdateStatusText.Text = "正在检查更新…";
        UpdateNotesText.Visibility = Visibility.Collapsed;
        try
        {
            _availableUpdate = await _host.Services.Updates.CheckForUpdateAsync();
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = "当前已是最新版本";
            }
            else
            {
                RenderUpdateState();
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "检查超时，请稍后重试";
        }
        catch (HttpRequestException)
        {
            UpdateStatusText.Text = "无法连接 GitHub，请检查网络后重试";
        }
        catch
        {
            UpdateStatusText.Text = "检查更新失败，请稍后重试";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async Task DownloadAndInstallUpdateAsync(DesktopUpdateInfo update)
    {
        // 免安装版（解压即用）不跑安装包，而是原地覆盖当前目录；安装版保持原有安装包流程。
        var portable = DesktopUpdateService.CurrentInstallKind == DesktopInstallKind.Portable;
        var appDirectory = DesktopUpdateService.CurrentAppDirectory;
        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            portable
                ? $"将下载 OpaiTV {update.VersionText} 免安装包，并原地更新当前目录：\n{appDirectory}\n\n下载完成后软件会自动退出、替换文件并重新启动。是否继续？"
                : $"将下载 OpaiTV {update.VersionText}（{DesktopUpdateService.FormatFileSize(update.InstallerSize)}）。\n\n下载完成后软件会自动退出、安装并重新启动。是否继续？",
            "安装更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetUpdateBusy(true);
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        var packageLabel = portable ? "更新包" : "安装包";
        UpdateStatusText.Text = $"正在下载{packageLabel}…";
        try
        {
            var progress = new Progress<int>(value =>
            {
                UpdateProgressBar.Value = value;
                UpdateStatusText.Text = $"正在下载{packageLabel}… {value}%";
            });
            if (portable)
            {
                DesktopUpdateService.EnsureDirectoryWritable(appDirectory);
                var updates = _host.Services.Updates;
                var archivePath = await updates.DownloadAndVerifyPortableAsync(update, progress);

                UpdateStatusText.Text = "正在解压更新包…";
                var stagedDirectory = await Task.Run(() => updates.StagePortableUpdate(archivePath, update));

                UpdateStatusText.Text = "正在备份视频源配置…";
                _ = DesktopUpdateService.BackupUserConfiguration(update.VersionText);

                UpdateStatusText.Text = "正在替换程序文件并重新启动…";
                DesktopUpdateService.LaunchPortableUpdater(stagedDirectory, appDirectory, Environment.ProcessId);
                Application.Current.Shutdown();
                return;
            }

            var installerPath = await _host.Services.Updates.DownloadAndVerifyAsync(update, progress);

            UpdateStatusText.Text = "正在备份视频源配置…";
            _ = DesktopUpdateService.BackupUserConfiguration(update.VersionText);

            UpdateStatusText.Text = "正在启动安装程序…";
            DesktopUpdateService.LaunchInstaller(installerPath);
            Application.Current.Shutdown();
        }
        catch (UnauthorizedAccessException) when (portable)
        {
            UpdateStatusText.Text = "当前程序目录不可写，无法原地更新；请把程序移到可写目录后重试";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            UpdateStatusText.Text = "已取消安装";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "下载已取消";
        }
        catch (InvalidDataException)
        {
            UpdateStatusText.Text = $"{packageLabel}校验失败，未执行更新";
        }
        catch (HttpRequestException)
        {
            UpdateStatusText.Text = $"{packageLabel}下载失败，请检查网络后重试";
        }
        catch
        {
            UpdateStatusText.Text = "更新失败，当前版本未被修改";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void RenderUpdateState()
    {
        if (_availableUpdate is null)
        {
            CheckUpdateButton.Content = "检查更新";
            UpdateStatusText.Text = "";
            UpdateNotesText.Text = "";
            UpdateNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        CheckUpdateButton.Content = "下载并安装";
        UpdateStatusText.Text = $"发现新版本 {_availableUpdate.VersionText} · {DesktopUpdateService.FormatFileSize(_availableUpdate.InstallerSize)}";
        var notes = NormalizeReleaseNotes(_availableUpdate.ReleaseNotes);
        UpdateNotesText.Text = notes;
        UpdateNotesText.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        CheckUpdateButton.IsEnabled = !busy;
        if (!busy)
        {
            CheckUpdateButton.Content = _availableUpdate is null ? "检查更新" : "下载并安装";
        }
    }

    private static string NormalizeReleaseNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return "";
        }

        var normalized = notes.Replace("\r\n", "\n").Trim();
        return normalized.Length <= 600 ? normalized : normalized[..600] + "…";
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
            RenderVideoSourceState();
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

    // 成人内容(18+)开关：立即生效并保存，不需要再点“保存设置”
    private bool _danmakuSaveFailed;
    private bool _danmakuServerInvalid;

    private void AdultContentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        try
        {
            _host.Services.SetShowAdultContent(AdultContentToggle.IsChecked == true);
            _adultToggleSaveFailed = false;
        }
        catch
        {
            _adultToggleSaveFailed = true;
        }

        RenderVideoSourceState();
    }

    // ---------- 弹幕：每一项改了立即生效并保存，不需要再点“保存设置” ----------

    private void ShowDanmakuSettings(DesktopSettings settings)
    {
        DanmakuToggle.IsChecked = settings.DanmakuEnabled;
        SelectByTag(
            DanmakuOpacityBox,
            DanmakuSettingsRules.OpacityOptions
                .OrderBy(option => Math.Abs(option - settings.DanmakuOpacity))
                .First()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectByTag(DanmakuSpeedBox, DanmakuSettingsRules.NormalizeSpeed(settings.DanmakuSpeed));
        SelectByTag(DanmakuFontSizeBox, DanmakuSettingsRules.NormalizeFontSize(settings.DanmakuFontSize));
        DanmakuServerBox.Text = settings.DanmakuServerUrl ?? "";
        _danmakuSaveFailed = false;
        _danmakuServerInvalid = false;
        RenderDanmakuState();
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void RenderDanmakuState()
    {
        var settings = _host.Services.Settings;
        DanmakuOptionsPanel.Visibility = settings.DanmakuEnabled ? Visibility.Visible : Visibility.Collapsed;
        DanmakuStatus.Foreground = (System.Windows.Media.Brush)FindResource(
            _danmakuSaveFailed ? "DangerBrush" : "MutedTextBrush");
        DanmakuStatus.Text = (settings.DanmakuEnabled
                ? "已开启：播放在线视频时按片名和集数自动匹配弹幕，匹配不到就不显示"
                : "已关闭")
            + (_danmakuSaveFailed ? "。本次已生效，但写入设置文件失败，重启后可能恢复原状" : "");
        DanmakuServerHint.Foreground = (System.Windows.Media.Brush)FindResource(
            _danmakuServerInvalid ? "DangerBrush" : "MutedTextBrush");
        DanmakuServerHint.Text = _danmakuServerInvalid
            ? "地址无效，请填写 http(s) 地址；留空则使用内置来源"
            : string.IsNullOrEmpty(settings.DanmakuServerUrl)
                ? "当前使用内置来源（腾讯视频、哔哩哔哩的公开弹幕），不需要任何配置"
                : "优先使用这个服务器；它匹配不到或访问失败时自动改用内置来源";
    }

    private void SaveDanmakuSettings(
        bool? enabled = null,
        string? serverUrl = null,
        double? opacity = null,
        string? speed = null,
        string? fontSize = null)
    {
        try
        {
            _host.Services.SetDanmakuSettings(enabled, serverUrl, opacity, speed, fontSize);
            _danmakuSaveFailed = false;
        }
        catch
        {
            _danmakuSaveFailed = true;
        }

        RenderDanmakuState();
    }

    private void DanmakuToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            SaveDanmakuSettings(enabled: DanmakuToggle.IsChecked == true);
        }
    }

    private void DanmakuOption_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        if (ReferenceEquals(sender, DanmakuOpacityBox))
        {
            if (double.TryParse(
                    SelectedTag(DanmakuOpacityBox),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var opacity))
            {
                SaveDanmakuSettings(opacity: opacity);
            }
        }
        else if (ReferenceEquals(sender, DanmakuSpeedBox))
        {
            SaveDanmakuSettings(speed: SelectedTag(DanmakuSpeedBox));
        }
        else if (ReferenceEquals(sender, DanmakuFontSizeBox))
        {
            SaveDanmakuSettings(fontSize: SelectedTag(DanmakuFontSizeBox));
        }
    }

    private void DanmakuServerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitDanmakuServerUrl();
        }
    }

    private void DanmakuServerBox_LostFocus(object sender, RoutedEventArgs e) => CommitDanmakuServerUrl();

    private void CommitDanmakuServerUrl()
    {
        if (!_loaded)
        {
            return;
        }

        var text = DanmakuServerBox.Text ?? "";
        var normalized = DanmakuSettingsRules.NormalizeServerUrl(text);
        // 填了内容却不是 http(s) 地址：不保存，提示一下
        _danmakuServerInvalid = text.Trim().Length > 0 && normalized.Length == 0;
        if (_danmakuServerInvalid)
        {
            RenderDanmakuState();
            return;
        }

        if (DanmakuServerBox.Text != normalized)
        {
            DanmakuServerBox.Text = normalized;
        }

        if (normalized != (_host.Services.Settings.DanmakuServerUrl ?? ""))
        {
            SaveDanmakuSettings(serverUrl: normalized);
        }
        else
        {
            RenderDanmakuState();
        }
    }

    /// <summary>
    /// 被隐藏的成人源在设置页之外发生变化（订阅加载完成、AI 写入额外源、settings.json 被外部改写）：
    /// 设置页常驻不重建，这里把开关和源列表同步到当前状态。
    /// </summary>
    internal void RefreshVideoSourceState()
    {
        if (!_loaded)
        {
            return;
        }

        _loaded = false;
        AdultContentToggle.IsChecked = _host.Services.Settings.ShowAdultContent;
        _loaded = true;
        RenderVideoSourceState();
    }

    private void AddSourceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddSourceButton_Click(sender, e);
        }
    }

    // 一键加源：校验通过即保存并生效，不需要再点“保存设置”
    private async void AddSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_addSourceBusy)
        {
            return;
        }

        var adult = AddSourceAdultCheck.IsChecked == true;
        _addSourceBusy = true;
        AddSourceButton.IsEnabled = false;
        AddSourceButton.Content = "添加中…";
        SetAddSourceStatus("正在识别地址…", isError: false);
        try
        {
            var result = await _host.Services.AddVideoSourceAsync(AddSourceBox.Text, adult);
            AddSourceBox.Text = "";
            AddSourceAdultCheck.IsChecked = false;
            if (result.FromSync)
            {
                var details = new[]
                {
                    result.Added > 0 ? $"新增 {result.Added} 个" : "",
                    result.Updated > 0 ? $"更新 {result.Updated} 个" : "",
                    result.Unchanged > 0 ? $"{result.Unchanged} 个已存在" : "",
                }.Where(text => text.Length > 0);
                var hidden = _host.Services.HiddenAdultSourceCount;
                SetAddSourceStatus(
                    $"已识别为同步链接：导入了其中 {result.Added + result.Updated + result.Unchanged} 个视频源（{string.Join("，", details)}）"
                    + (hidden > 0 ? $"。成人内容开关已关闭，{hidden} 个成人源暂不显示" : ""),
                    isError: false);
                return;
            }

            var kind = result.FromSubscription ? "已识别为订阅链接：" : "已按单个源接口处理：";
            var summary = kind + (result.Added > 0
                ? $"添加了 {result.Added} 个源" + (result.Updated > 0 ? $"，另有 {result.Updated} 个已存在的源更新了成人标记" : "")
                : result.Updated > 0
                    ? $"源已存在，更新了 {result.Updated} 个源的成人标记"
                    : "源已存在，无需重复添加");
            SetAddSourceStatus(
                adult && !_host.Services.Settings.ShowAdultContent ? summary + "。成人内容开关已关闭，成人源暂不显示" : summary,
                isError: false);
        }
        catch (InvalidOperationException exception) when (exception.Message == VideoSourceSyncLink.IncompleteLinkError)
        {
            SetAddSourceStatus("这是分片二维码里的一张，不是完整链接。请在导出的设备上点“复制链接”，粘贴完整的同步链接", isError: true);
        }
        catch (InvalidOperationException exception) when (exception.Message == VideoSourceSyncLink.UnsupportedLinkError)
        {
            SetAddSourceStatus("这个同步链接来自更新的版本，请先升级 OpaiTV 再导入", isError: true);
        }
        catch (InvalidOperationException exception) when (exception.Message == VideoSourceSyncLink.InvalidLinkError)
        {
            SetAddSourceStatus("同步链接无效或不完整，请重新复制完整的链接", isError: true);
        }
        catch (InvalidOperationException exception) when (exception.Message == OneClickVideoSources.InvalidUrlError)
        {
            SetAddSourceStatus("请输入有效的源订阅链接或源接口地址（http/https）", isError: true);
        }
        catch
        {
            SetAddSourceStatus("添加失败：无法读写额外源文件（extra_sources.json），现有源未被改动", isError: true);
        }
        finally
        {
            _addSourceBusy = false;
            AddSourceButton.IsEnabled = true;
            AddSourceButton.Content = "添加";
            RenderVideoSourceState();
        }
    }

    private void DeleteExtraSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: VideoSource source })
        {
            return;
        }

        try
        {
            _host.Services.RemoveExtraSource(source.Key);
            SetAddSourceStatus($"已删除 {source.Name}", isError: false);
        }
        catch
        {
            SetAddSourceStatus("删除失败：无法读写额外源文件（extra_sources.json）", isError: true);
        }

        RenderVideoSourceState();
    }

    // 同步视频源：展开/收起导出面板（同步链接 + 二维码）
    private void SyncExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (SyncExportPanel.Visibility == Visibility.Visible)
        {
            HideSyncExport();
            return;
        }

        SyncExportPanel.Visibility = Visibility.Visible;
        SyncExportButton.Content = "收起";
        RenderSyncExport();
    }

    private void HideSyncExport()
    {
        _syncQrTimer.Stop();
        _syncQrPayloads = [];
        SyncExportPanel.Visibility = Visibility.Collapsed;
        SyncExportButton.Content = "同步视频源";
        SyncQrCodeImage.Source = null;
        SyncLinkBox.Text = "";
        SyncExportStatus.Text = "";
    }

    /// <summary>
    /// 按当前全部视频源（订阅 + 额外源，含被隐藏的成人源）生成同步链接和二维码。
    /// 链接太长、一张二维码放不下时拆成多张分片码自动轮播，手机扫码端会拼回完整链接。
    /// </summary>
    private void RenderSyncExport()
    {
        _syncQrTimer.Stop();
        SyncExportStatus.Text = "";
        string link;
        try
        {
            link = _host.Services.ExportVideoSourceSyncLink();
        }
        catch (InvalidOperationException)
        {
            _syncQrPayloads = [];
            SyncExportContent.Visibility = Visibility.Collapsed;
            SyncExportSummary.Text = "当前没有可导出的视频源，请先保存订阅链接或添加视频源。";
            return;
        }

        var services = _host.Services;
        var adultCount = services.ExportableAdultSourceCount;
        SyncExportSummary.Text = $"共 {services.ExportableSourceCount} 个视频源"
            + (adultCount > 0 ? $"（含 {adultCount} 个成人源，导入后仍受对方设备的成人内容开关控制）" : "")
            + $"，同步链接共 {link.Length} 个字符。在另一台设备的“一键加源 / 添加视频源”里粘贴链接，或用手机版“扫码导入”扫描二维码。";
        SyncExportContent.Visibility = Visibility.Visible;
        SyncLinkBox.Text = link;
        _syncQrPayloads = VideoSourceSyncLink.BuildQrPayloads(link);
        SyncQrPager.Visibility = _syncQrPayloads.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SyncQrPauseButton.Content = "暂停";
        if (_syncQrPayloads.Count > 1)
        {
            _syncQrTimer.Start();
        }

        ShowSyncQr(0);
    }

    private void ShowSyncQr(int index)
    {
        var count = _syncQrPayloads.Count;
        if (count == 0)
        {
            return;
        }

        _syncQrIndex = ((index % count) + count) % count;
        try
        {
            // 按整数倍放大：每个模块都是同样大小的方块，边缘不发虚
            var qr = VideoSourceSyncQr.Render(_syncQrPayloads[_syncQrIndex]);
            var scaled = qr.Scale(Math.Clamp(420 / qr.Size, 2, 8));
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                scaled.Size, scaled.Size, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, scaled.Pixels, scaled.Size);
            bitmap.Freeze();
            SyncQrCodeImage.Source = bitmap;
            SyncQrPageText.Text = count > 1
                ? $"第 {_syncQrIndex + 1} / {count} 张 · {(_syncQrTimer.IsEnabled ? "自动轮播中" : "已暂停")}，源较多时二维码分成多张，用手机对准直到全部扫完"
                : "用手机版“扫码导入”扫描这个二维码";
        }
        catch
        {
            _syncQrTimer.Stop();
            SyncQrCodeImage.Source = null;
            SyncQrPageText.Text = "二维码生成失败，请改用“复制链接”";
        }
    }

    private void SyncQrPrevButton_Click(object sender, RoutedEventArgs e) => StepSyncQr(-1);

    private void SyncQrNextButton_Click(object sender, RoutedEventArgs e) => StepSyncQr(1);

    // 手动翻页时停止轮播，停在选中的那一张
    private void StepSyncQr(int delta)
    {
        _syncQrTimer.Stop();
        SyncQrPauseButton.Content = "继续轮播";
        ShowSyncQr(_syncQrIndex + delta);
    }

    private void SyncQrPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_syncQrTimer.IsEnabled)
        {
            _syncQrTimer.Stop();
            SyncQrPauseButton.Content = "继续轮播";
        }
        else
        {
            _syncQrTimer.Start();
            SyncQrPauseButton.Content = "暂停";
        }

        ShowSyncQr(_syncQrIndex);
    }

    private void SyncLinkBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SyncLinkBox.SelectAll();

    private void SyncCopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(SyncLinkBox.Text, true);
            SyncExportStatus.Text = "同步链接已复制";
        }
        catch
        {
            // 剪贴板被其他程序占用时会抛出异常
            SyncExportStatus.Text = "复制失败，请点一下链接框全选后按 Ctrl+C";
        }
    }

    private void SetAddSourceStatus(string text, bool isError)
    {
        AddSourceStatus.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "PrimaryBrush");
        AddSourceStatus.Text = text;
    }

    /// <summary>
    /// 刷新开关说明、源数量和额外源列表。成人内容开关关闭时不列出成人源，只说明有多少个被隐藏。
    /// </summary>
    private void RenderVideoSourceState()
    {
        var services = _host.Services;
        var showAdultContent = services.Settings.ShowAdultContent;
        AdultContentStatus.Foreground = (System.Windows.Media.Brush)FindResource(
            showAdultContent || _adultToggleSaveFailed ? "DangerBrush" : "MutedTextBrush");
        AdultContentStatus.Text = (showAdultContent
                ? "已开启：成人源及其内容正常显示"
                : "已关闭：成人源及其内容已隐藏（源列表、首页、搜索、收藏、观看历史）")
            + (_adultToggleSaveFailed ? "。本次已生效，但写入设置文件失败，重启后可能恢复原状" : "");

        var hidden = services.HiddenAdultSourceCount;
        SourceSummaryText.Text = $"当前共 {services.Sources.Count} 个视频源"
            + (hidden > 0 ? $"，另有 {hidden} 个成人源已隐藏（成人内容开关已关闭）" : "");
        ExtraSourcesList.ItemsSource = services.VisibleExtraSources;
        if (SyncExportPanel.Visibility == Visibility.Visible)
        {
            // 源有增删或改了成人标记：已展开的同步链接和二维码跟着更新
            RenderSyncExport();
        }

        UpdateSubHint();
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
