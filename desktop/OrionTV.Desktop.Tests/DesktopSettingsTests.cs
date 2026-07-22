using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class DesktopSettingsTests
{
    [Fact]
    public void BuildSubscriptionUrlList_UsesPrimaryOnly()
    {
        var settings = new DesktopSettings(
            SubscriptionUrl: "https://subscription.example/config.json");

        var urls = OrionTV.Desktop.App.AppServices.BuildSubscriptionUrlList(settings.SubscriptionUrl);

        Assert.Equal(new[] { "https://subscription.example/config.json" }, urls.ToArray());
    }

    [Fact]
    public void BuildSubscriptionUrlList_WhenPrimaryEmpty_ReturnsNoUrls()
    {
        var settings = new DesktopSettings(SubscriptionUrl: "");

        var urls = OrionTV.Desktop.App.AppServices.BuildSubscriptionUrlList(settings.SubscriptionUrl);

        Assert.Empty(urls);
    }

    [Fact]
    public void VodCacheSettings_ResolvesDefaultDirectoryAndKeepsLegacyHelpers()
    {
        // 边下边播已移除，预缓冲/保留策略 helper 仍兼容旧 settings.json。
        Assert.Equal(15, VodCacheSettings.NormalizePrebufferSeconds(10));
        Assert.Equal(VodCacheSettings.Keep, VodCacheSettings.NormalizeRetention("keep"));
        Assert.Equal(DesktopPaths.DefaultVodCacheDirectory, VodCacheSettings.ResolveDirectory(""));
    }

    [Fact]
    public void ApplyEditableFields_UpdatesVodCacheDirectoryWithoutChangingVolume()
    {
        var current = new DesktopSettings(Volume: 37, WeakNetworkCacheEnabled: true);
        var cacheDir = Path.Combine(Path.GetTempPath(), "orion-cache");

        var updated = DesktopSettingsForm.ApplyEditableFields(
            current,
            current.SubscriptionUrl,
            current.ApiBaseUrl,
            current.M3uUrl,
            current.AutoSkipAds,
            weakNetworkCacheEnabled: false,
            vodPrebufferSeconds: 15,
            vodCacheDirectory: cacheDir,
            vodCacheRetention: VodCacheSettings.Keep,
            usePotPlayerForLocal: true,
            potPlayerPath: @"E:\potPlayer\PotPlayerMini64.exe");

        Assert.False(updated.WeakNetworkCacheEnabled);
        Assert.Equal(VodCacheSettings.ResolveDirectory(cacheDir), updated.VodCacheDirectory);
        Assert.Equal(VodCacheSettings.Keep, updated.VodCacheRetention);
        Assert.True(updated.UsePotPlayerForLocal);
        Assert.Equal(@"E:\potPlayer\PotPlayerMini64.exe", updated.PotPlayerPath);
        Assert.Equal(37, updated.Volume);
    }

    [Fact]
    public void Defaults_do_not_include_video_sources()
    {
        var settings = new DesktopSettings();

        Assert.Equal("", settings.SubscriptionUrl);
        Assert.Equal("", settings.M3uUrl);
        Assert.Equal(GeminiLiveTranslateProtocol.DefaultModel, settings.LiveTranslationModel);
        Assert.Equal("", settings.LiveTranslationProvider);
        Assert.True(settings.LiveTranslationDubbing);
        Assert.Equal("", settings.LiveTranslationBaseUrl);
        Assert.Empty(DesktopLiveSourceSettings.Resolve(settings));
    }

    [Fact]
    public void Migration_preserves_user_configured_sources()
    {
        var current = new DesktopSettings(
            SubscriptionUrl: "https://subscription.example/config.json",
            M3uUrl: "https://primary.example/live.m3u",
            AdditionalLiveSources:
            [
                new DesktopLiveSource("备用源", "https://extra.example/live.m3u")
            ]);

        var migrated = DesktopSettingsMigration.Apply(current);

        Assert.Equal(current.SubscriptionUrl, migrated.SubscriptionUrl);
        Assert.Equal(current.M3uUrl, migrated.M3uUrl);
        Assert.Equal(current.AdditionalLiveSources, migrated.AdditionalLiveSources);
    }

    [Fact]
    public void ApplyEditableFields_PreservesVolume()
    {
        var current = new DesktopSettings(
            SubscriptionUrl: "https://old.example/sub",
            ApiBaseUrl: "https://old.example/api",
            M3uUrl: "https://old.example/live.m3u",
            AutoSkipAds: true,
            Volume: 37);

        var updated = DesktopSettingsForm.ApplyEditableFields(
            current,
            "https://new.example/sub",
            "https://new.example/api",
            "https://new.example/live.m3u",
            false);

        Assert.Equal(37, updated.Volume);
        Assert.Equal("https://new.example/sub", updated.SubscriptionUrl);
        Assert.Equal("https://new.example/api", updated.ApiBaseUrl);
        Assert.Equal("https://new.example/live.m3u", updated.M3uUrl);
        Assert.False(updated.AutoSkipAds);
        Assert.Equal(GeminiLiveTranslateProtocol.DefaultModel, updated.LiveTranslationModel);
    }

    [Fact]
    public void ApplyEditableFields_trims_custom_translation_model_and_preserves_blank_as_current()
    {
        var current = new DesktopSettings(LiveTranslationModel: "old-model");

        var custom = DesktopSettingsForm.ApplyEditableFields(
            current, "sub", "api", "m3u", true, "  gpt-realtime-translate-snapshot  ");
        var blankKeepsCurrent = DesktopSettingsForm.ApplyEditableFields(
            custom, "sub", "api", "m3u", true, "   ");

        Assert.Equal("gpt-realtime-translate-snapshot", custom.LiveTranslationModel);
        // 空白不再强制回默认，避免覆盖用户已保存模型。
        Assert.Equal("gpt-realtime-translate-snapshot", blankKeepsCurrent.LiveTranslationModel);
    }

    [Fact]
    public void ApplyEditableFields_trims_translation_base_url_and_preserves_when_omitted()
    {
        var current = new DesktopSettings(LiveTranslationBaseUrl: "https://old.example");

        var updated = DesktopSettingsForm.ApplyEditableFields(
            current,
            current.SubscriptionUrl,
            current.ApiBaseUrl,
            current.M3uUrl,
            current.AutoSkipAds,
            liveTranslationBaseUrl: "  https://api.example.com/  ");
        var preserved = DesktopSettingsForm.ApplyEditableFields(
            updated,
            updated.SubscriptionUrl,
            updated.ApiBaseUrl,
            updated.M3uUrl,
            updated.AutoSkipAds);

        Assert.Equal("https://api.example.com", updated.LiveTranslationBaseUrl);
        Assert.Equal("https://api.example.com", preserved.LiveTranslationBaseUrl);
    }

    [Fact]
    public void ApplyEditableFields_adds_manual_live_sources_without_replacing_primary_source()
    {
        var current = new DesktopSettings(M3uUrl: "https://primary.example/live.m3u");

        var updated = DesktopSettingsForm.ApplyEditableFields(
            current,
            "sub",
            "api",
            current.M3uUrl,
            true,
            RealtimeTranslationProtocol.DefaultModel,
            [
                new DesktopLiveSource(" BBC ", " https://extra.example/news.m3u "),
                new DesktopLiveSource("重复默认源", "https://primary.example/live.m3u")
            ]);
        var resolved = DesktopLiveSourceSettings.Resolve(updated);

        Assert.Equal(2, resolved.Count);
        Assert.True(resolved[0].IsPrimary);
        Assert.Equal("https://primary.example/live.m3u", resolved[0].Url);
        Assert.Equal("BBC", resolved[1].Name);
        Assert.Equal("https://extra.example/news.m3u", resolved[1].Url);
    }

    [Fact]
    public void LiveSourceSettings_ignores_invalid_and_duplicate_manual_urls()
    {
        var normalized = DesktopLiveSourceSettings.Normalize([
            new DesktopLiveSource("One", "https://example.com/live.m3u"),
            new DesktopLiveSource("Duplicate", "HTTPS://EXAMPLE.COM/live.m3u"),
            new DesktopLiveSource("Invalid", "not-a-url"),
            new DesktopLiveSource("", "http://other.example/live.m3u")
        ]);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("One", normalized[0].Name);
        Assert.Equal("直播源 2", normalized[1].Name);
    }

    [Fact]
    public void Legacy_settings_json_without_manual_sources_keeps_the_existing_primary_source()
    {
        const string legacyJson = """{"M3uUrl":"https://legacy.example/live.m3u","Volume":37}""";

        var settings = JsonSerializer.Deserialize<DesktopSettings>(legacyJson)!;
        var resolved = DesktopLiveSourceSettings.Resolve(settings);

        Assert.Null(settings.AdditionalLiveSources);
        Assert.Single(resolved);
        Assert.True(resolved[0].IsPrimary);
        Assert.Equal("https://legacy.example/live.m3u", resolved[0].Url);
        Assert.Equal(37, settings.Volume);
    }

    [Fact]
    public void Manual_live_sources_round_trip_through_settings_json()
    {
        var settings = new DesktopSettings(
            M3uUrl: "https://primary.example/live.m3u",
            AdditionalLiveSources:
            [
                new DesktopLiveSource("备用一", "https://one.example/live.m3u"),
                new DesktopLiveSource("备用二", "https://two.example/live.m3u")
            ]);

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<DesktopSettings>(json)!;
        var resolved = DesktopLiveSourceSettings.Resolve(loaded);

        Assert.Equal(3, resolved.Count);
        Assert.Equal(["默认直播源", "备用一", "备用二"], resolved.Select(source => source.Name));
    }

    [Fact]
    public void ShouldReloadSubscription_ReloadsSameUrlWhenSourcesAreEmpty()
    {
        var current = new DesktopSettings(SubscriptionUrl: "https://example.com/sub");

        Assert.True(DesktopSettingsForm.ShouldReloadSubscription(
            current,
            "https://example.com/sub",
            hasLoadedSources: false));
    }

    [Fact]
    public void ShouldReloadSubscription_DoesNotReloadSameUrlWhenSourcesAreLoaded()
    {
        var current = new DesktopSettings(SubscriptionUrl: "https://example.com/sub");

        Assert.False(DesktopSettingsForm.ShouldReloadSubscription(
            current,
            "https://example.com/sub",
            hasLoadedSources: true));
    }

}

public class LocalStoreTests
{
    [Fact]
    public void Clear_removes_all_persisted_items()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orion-store-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TestStringStore(path);
            store.Save("source", "1", "first");
            store.Save("source", "2", "second");

            store.Clear();

            Assert.Empty(store.GetAll());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class TestStringStore(string filePath) : JsonDictionaryStore<string>(filePath)
    {
        public void Save(string source, string id, string value) =>
            Mutate(all => all[Key(source, id)] = value);
    }
}
