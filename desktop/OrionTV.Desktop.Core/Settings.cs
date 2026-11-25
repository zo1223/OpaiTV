using System.Text.Json;

namespace OrionTV.Desktop.Core;

/// <summary>本地数据目录与文件路径（%AppData%/OrionTV.Desktop）。</summary>
public static class DesktopPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrionTV.Desktop");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string FavoritesFile => Path.Combine(DataDirectory, "favorites.json");
    public static string PlayRecordsFile => Path.Combine(DataDirectory, "play_records.json");
    public static string LocalMediaFile => Path.Combine(DataDirectory, "local_media.json");
    /// <summary>AI/CLI 解析写入的额外视频源（与订阅源合并）。</summary>
    public static string ExtraSourcesFile => Path.Combine(DataDirectory, "extra_sources.json");
    public static string DefaultVodCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrionTV.Desktop",
        "vod_cache");

    public static void EnsureDirectory() => Directory.CreateDirectory(DataDirectory);
}

public sealed record DesktopSettings(
    string SubscriptionUrl = "",
    string ApiBaseUrl = "",
    string M3uUrl = "",
    bool AutoSkipAds = true,
    int Volume = 100,
    string LiveTranslationModel = GeminiLiveTranslateProtocol.DefaultModel,
    string LiveTranslationBaseUrl = "",
    /// <summary>openai | gemini | doubao；空则按模型名推断。</summary>
    string LiveTranslationProvider = "",
    /// <summary>是否播放中文配音（Gemini / 豆包 AST 有效；字幕始终开启）。</summary>
    bool LiveTranslationDubbing = true,
    /// <summary>豆包同声传译 APP Key（Access Key 存在凭据管理器）。</summary>
    string DoubaoAppKey = "",
    string DoubaoResourceId = DoubaoAstProtocol.DefaultResourceId,
    IReadOnlyList<DesktopLiveSource>? AdditionalLiveSources = null,
    bool WeakNetworkCacheEnabled = true,
    int VodPrebufferSeconds = 30,
    string VodCacheDirectory = "",
    string VodCacheRetention = VodCacheSettings.AutoDelete,
    /// <summary>本地文件是否用外部 PotPlayer 播放（默认关；本地优先内嵌 mpv）。</summary>
    bool UsePotPlayerForLocal = false,
    /// <summary>PotPlayerMini64.exe 路径；空则自动探测。</summary>
    string PotPlayerPath = "",
    /// <summary>
    /// 成人内容(18+)开关，默认关闭；关闭时成人源及其内容在所有取数入口被过滤。
    /// 设置页切换后立即生效并保存，不随“保存设置”提交。
    /// </summary>
    bool ShowAdultContent = false,
    /// <summary>
    /// 弹幕开关，默认关闭。设置页里的弹幕各项都是改了立即生效并保存，不随“保存设置”提交。
    /// </summary>
    bool DanmakuEnabled = false,
    /// <summary>自定义 danmu_api 服务器根地址；留空使用内置来源（直连腾讯视频、哔哩哔哩的公开接口）。</summary>
    string DanmakuServerUrl = "",
    /// <summary>弹幕不透明度（0.2 ~ 1）。</summary>
    double DanmakuOpacity = DanmakuSettingsRules.DefaultOpacity,
    /// <summary>弹幕滚动速度：slow | normal | fast。</summary>
    string DanmakuSpeed = DanmakuSettingsRules.DefaultSpeed,
    /// <summary>弹幕字号：small | normal | large。</summary>
    string DanmakuFontSize = DanmakuSettingsRules.DefaultFontSize);

public static class VodCacheSettings
{
    public const string AutoDelete = "auto";
    public const string AskBeforeDelete = "ask";
    public const string Keep = "keep";

    public static string ResolveDirectory(string? configuredDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory)
            ? DesktopPaths.DefaultVodCacheDirectory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory.Trim()));

    public static int NormalizePrebufferSeconds(int seconds) => seconds switch
    {
        <= 20 => 15,
        <= 45 => 30,
        _ => 60,
    };

    public static string NormalizeRetention(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        AskBeforeDelete => AskBeforeDelete,
        Keep => Keep,
        _ => AutoDelete,
    };
}

public static class DesktopSettingsMigration
{
    public static DesktopSettings Apply(DesktopSettings settings) =>
        settings with
        {
            VodPrebufferSeconds = VodCacheSettings.NormalizePrebufferSeconds(settings.VodPrebufferSeconds),
            VodCacheDirectory = VodCacheSettings.ResolveDirectory(settings.VodCacheDirectory),
            VodCacheRetention = VodCacheSettings.NormalizeRetention(settings.VodCacheRetention),
            DanmakuServerUrl = DanmakuSettingsRules.NormalizeServerUrl(settings.DanmakuServerUrl),
            DanmakuOpacity = DanmakuSettingsRules.NormalizeOpacity(settings.DanmakuOpacity),
            DanmakuSpeed = DanmakuSettingsRules.NormalizeSpeed(settings.DanmakuSpeed),
            DanmakuFontSize = DanmakuSettingsRules.NormalizeFontSize(settings.DanmakuFontSize),
        };
}

public static class DesktopSettingsForm
{
    public static DesktopSettings ApplyEditableFields(
        DesktopSettings current,
        string subscriptionUrl,
        string apiBaseUrl,
        string m3uUrl,
        bool autoSkipAds,
        string? liveTranslationModel = null,
        IEnumerable<DesktopLiveSource>? additionalLiveSources = null,
        string? liveTranslationBaseUrl = null,
        string? liveTranslationProvider = null,
        bool? liveTranslationDubbing = null,
        string? doubaoAppKey = null,
        string? doubaoResourceId = null,
        bool? weakNetworkCacheEnabled = null,
        int? vodPrebufferSeconds = null,
        string? vodCacheDirectory = null,
        string? vodCacheRetention = null,
        bool? usePotPlayerForLocal = null,
        string? potPlayerPath = null) =>
        current with
        {
            SubscriptionUrl = subscriptionUrl,
            ApiBaseUrl = apiBaseUrl,
            M3uUrl = m3uUrl,
            AutoSkipAds = autoSkipAds,
            LiveTranslationModel = string.IsNullOrWhiteSpace(liveTranslationModel)
                ? current.LiveTranslationModel
                : liveTranslationModel.Trim(),
            LiveTranslationBaseUrl = liveTranslationBaseUrl is null
                ? current.LiveTranslationBaseUrl
                : liveTranslationBaseUrl.Trim().TrimEnd('/'),
            LiveTranslationProvider = string.IsNullOrWhiteSpace(liveTranslationProvider)
                ? current.LiveTranslationProvider
                : liveTranslationProvider.Trim().ToLowerInvariant(),
            LiveTranslationDubbing = liveTranslationDubbing ?? current.LiveTranslationDubbing,
            DoubaoAppKey = doubaoAppKey is null ? current.DoubaoAppKey : doubaoAppKey.Trim(),
            DoubaoResourceId = string.IsNullOrWhiteSpace(doubaoResourceId)
                ? current.DoubaoResourceId
                : doubaoResourceId.Trim(),
            AdditionalLiveSources = additionalLiveSources is null
                ? current.AdditionalLiveSources
                : DesktopLiveSourceSettings.Normalize(additionalLiveSources, m3uUrl),
            WeakNetworkCacheEnabled = weakNetworkCacheEnabled ?? current.WeakNetworkCacheEnabled,
            VodPrebufferSeconds = vodPrebufferSeconds is null
                ? current.VodPrebufferSeconds
                : VodCacheSettings.NormalizePrebufferSeconds(vodPrebufferSeconds.Value),
            VodCacheDirectory = vodCacheDirectory is null
                ? current.VodCacheDirectory
                : VodCacheSettings.ResolveDirectory(vodCacheDirectory),
            VodCacheRetention = vodCacheRetention is null
                ? current.VodCacheRetention
                : VodCacheSettings.NormalizeRetention(vodCacheRetention),
            UsePotPlayerForLocal = usePotPlayerForLocal ?? current.UsePotPlayerForLocal,
            PotPlayerPath = potPlayerPath is null
                ? current.PotPlayerPath
                : potPlayerPath.Trim().Trim('"'),
        };

    public static bool ShouldReloadSubscription(
        DesktopSettings current,
        string subscriptionUrl,
        bool hasLoadedSources) =>
        !string.IsNullOrWhiteSpace(subscriptionUrl)
        && (subscriptionUrl != current.SubscriptionUrl || !hasLoadedSources);
}

/// <summary>设置读写（JSON）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(DesktopPaths.SettingsFile))
            {
                return DesktopSettingsMigration.Apply(new DesktopSettings());
            }

            var json = File.ReadAllText(DesktopPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<DesktopSettings>(json, Options) ?? new DesktopSettings();
            var migrated = DesktopSettingsMigration.Apply(settings);
            if (migrated != settings)
            {
                try
                {
                    Save(migrated);
                }
                catch
                {
                    // Migration should not prevent the app from starting.
                }
            }

            return migrated;
        }
        catch
        {
            return DesktopSettingsMigration.Apply(new DesktopSettings());
        }
    }

    public static void Save(DesktopSettings settings)
    {
        DesktopPaths.EnsureDirectory();
        // 先写临时文件再原子替换(与 LocalStore / ExtraVideoSourcesStore 一致)。直接覆写时若进程在
        // 写入中途退出(离开播放页/关闭应用时会保存音量),留下的半截 settings.json 会被 Load 当成
        // 默认设置,下一次保存就把订阅地址、直播源等配置永久清空。
        var tempPath = DesktopPaths.SettingsFile + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, Options));
        File.Move(tempPath, DesktopPaths.SettingsFile, overwrite: true);
    }
}
