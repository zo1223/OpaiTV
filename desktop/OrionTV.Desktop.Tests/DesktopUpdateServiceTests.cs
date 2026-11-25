using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class DesktopUpdateServiceTests
{
    [Fact]
    public void SelectUpdateFromJson_selects_newest_checksummed_stable_installer()
    {
        var json = """
            [
              {
                "tag_name": "v1.3.16",
                "draft": false,
                "prerelease": false,
                "body": "Latest fixes",
                "published_at": "2026-07-27T10:00:00Z",
                "assets": [
                  {
                    "name": "OpaiTV-Setup-1.3.16-win-x64.exe",
                    "size": 120000000,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe"
                  },
                  {
                    "name": "OpaiTV-Setup-1.3.16-win-x64.exe.sha256",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe.sha256"
                  }
                ]
              },
              {
                "tag_name": "v1.3.15",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "OpaiTV-Setup-1.3.15-win-x64.exe",
                    "size": 110000000,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v1.3.15/OpaiTV-Setup-1.3.15-win-x64.exe"
                  },
                  {
                    "name": "OpaiTV-Setup-1.3.15-win-x64.exe.sha256",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v1.3.15/OpaiTV-Setup-1.3.15-win-x64.exe.sha256"
                  }
                ]
              }
            ]
            """;

        var update = DesktopUpdateService.SelectUpdateFromJson(json, new Version(1, 3, 14, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 16, 0), update!.Version);
        Assert.Equal("1.3.16", update.VersionText);
        Assert.Equal("Latest fixes", update.ReleaseNotes);
    }

    [Fact]
    public void SelectUpdateFromJson_rejects_prerelease_mismatched_or_unverified_assets()
    {
        var json = """
            [
              {
                "tag_name": "v2.0.0",
                "draft": false,
                "prerelease": true,
                "assets": [
                  {
                    "name": "OpaiTV-Setup-2.0.0-win-x64.exe",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v2.0.0/OpaiTV-Setup-2.0.0-win-x64.exe"
                  },
                  {
                    "name": "OpaiTV-Setup-2.0.0-win-x64.exe.sha256",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v2.0.0/OpaiTV-Setup-2.0.0-win-x64.exe.sha256"
                  }
                ]
              },
              {
                "tag_name": "v1.4.0",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "OpaiTV-Setup-1.4.1-win-x64.exe",
                    "size": 100,
                    "browser_download_url": "https://github.com/zo1223/OpaiTV/releases/download/v1.4.0/OpaiTV-Setup-1.4.1-win-x64.exe"
                  }
                ]
              }
            ]
            """;

        var update = DesktopUpdateService.SelectUpdateFromJson(json, new Version(1, 3, 14));

        Assert.Null(update);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789ABCDEF  setup.exe")]
    [InlineData("sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void TryParseChecksum_accepts_standard_sha256_files(string value)
    {
        Assert.True(DesktopUpdateService.TryParseChecksum(value, out var checksum));
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", checksum);
    }

    [Fact]
    public void CreateConfigurationBackup_preserves_user_video_sources()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-update-test-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "data");
        var backupDirectory = Path.Combine(root, "backups");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "settings.json"), "{\"M3uUrl\":\"https://example.com/live.m3u\"}");
        File.WriteAllText(Path.Combine(dataDirectory, "extra_sources.json"), "{\"sources\":[{\"name\":\"Custom\"}]}");

        try
        {
            var backup = DesktopUpdateService.CreateConfigurationBackup(dataDirectory, backupDirectory, "1.3.15");

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
            using var archive = ZipFile.OpenRead(backup!);
            Assert.Equal(
                ["extra_sources.json", "settings.json"],
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_streams_and_verifies_installer_in_isolated_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-update-download-test-" + Guid.NewGuid().ToString("N"));
        var installerBytes = Encoding.UTF8.GetBytes("verified installer payload");
        var checksum = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksum + "  OpaiTV-Setup-1.3.16-win-x64.exe", Encoding.ASCII),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installerBytes),
            };
        }));
        using var service = new DesktopUpdateService(client, root);
        var update = new DesktopUpdateInfo(
            new Version(1, 3, 16, 0),
            "1.3.16",
            "v1.3.16",
            "OpaiTV-Setup-1.3.16-win-x64.exe",
            new Uri("https://github.com/zo1223/OpaiTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe"),
            installerBytes.Length,
            new Uri("https://github.com/zo1223/OpaiTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe.sha256"),
            "",
            null);
        var progressValues = new List<int>();

        try
        {
            var path = await service.DownloadAndVerifyAsync(update, new InlineProgress(progressValues.Add));

            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(100, progressValues[^1]);
            Assert.False(File.Exists(path + ".download"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_deletes_partial_file_when_checksum_is_wrong()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-update-hash-test-" + Guid.NewGuid().ToString("N"));
        var installerBytes = Encoding.UTF8.GetBytes("untrusted installer payload");
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent(new string('0', 64), Encoding.ASCII)
                    : new ByteArrayContent(installerBytes),
            }));
        using var service = new DesktopUpdateService(client, root);
        var update = new DesktopUpdateInfo(
            new Version(1, 3, 16, 0),
            "1.3.16",
            "v1.3.16",
            "OpaiTV-Setup-1.3.16-win-x64.exe",
            new Uri("https://github.com/zo1223/OpaiTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe"),
            installerBytes.Length,
            new Uri("https://github.com/zo1223/OpaiTV/releases/download/v1.3.16/OpaiTV-Setup-1.3.16-win-x64.exe.sha256"),
            "",
            null);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndVerifyAsync(update));
            Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*.download", SearchOption.AllDirectories) : []);
            Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories) : []);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpaiTV", false, DesktopInstallKind.Installed)]
    [InlineData(@"c:\program files\OpaiTV\", false, DesktopInstallKind.Installed)]
    [InlineData(@"C:\Program Files (x86)\OpaiTV", false, DesktopInstallKind.Installed)]
    [InlineData(@"C:/Program Files/OpaiTV/sub", false, DesktopInstallKind.Installed)]
    [InlineData(@"D:\Apps\OpaiTV", true, DesktopInstallKind.Installed)]
    [InlineData(@"D:\Apps\OpaiTV", false, DesktopInstallKind.Portable)]
    [InlineData(@"C:\Program Files Portable\OpaiTV", false, DesktopInstallKind.Portable)]
    [InlineData(@"C:\Users\me\Desktop\OpaiTV-1.3.18-win-x64-self-contained", false, DesktopInstallKind.Portable)]
    public void DetectInstallKind_separates_installed_and_portable_copies(
        string appDirectory,
        bool hasUninstaller,
        DesktopInstallKind expected)
    {
        var kind = DesktopUpdateService.DetectInstallKind(
            appDirectory,
            [@"C:\Program Files", @"C:\Program Files (x86)\", null, ""],
            hasUninstaller);

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void GetPortableArchiveName_matches_release_asset_naming()
    {
        Assert.Equal(
            "OpaiTV-1.3.25-win-x64-self-contained.zip",
            DesktopUpdateService.GetPortableArchiveName(CreateUpdate(new Version(1, 3, 25, 0), "1.3.25")));
        Assert.Throws<InvalidDataException>(() => DesktopUpdateService.GetPortableArchiveName(
            CreateUpdate(new Version(1, 3, 25, 0), "1.3.25") with { InstallerName = "OpaiTV-Setup-1.3.24-win-x64.exe" }));
    }

    [Fact]
    public async Task DownloadAndVerifyPortableAsync_downloads_the_zip_of_the_same_release()
    {
        var root = NewTempDirectory("orion-update-portable-download-");
        var archiveBytes = Encoding.UTF8.GetBytes("portable archive payload");
        var checksum = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var requested = new List<string>();
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent(checksum + "  OpaiTV-1.3.25-win-x64-self-contained.zip", Encoding.ASCII)
                    : new ByteArrayContent(archiveBytes),
            };
        }));
        using var service = new DesktopUpdateService(client, root);
        var progressValues = new List<int>();

        try
        {
            var path = await service.DownloadAndVerifyPortableAsync(
                CreateUpdate(new Version(1, 3, 25, 0), "1.3.25"),
                new InlineProgress(progressValues.Add));

            Assert.Equal("OpaiTV-1.3.25-win-x64-self-contained.zip", Path.GetFileName(path));
            Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(100, progressValues[^1]);
            Assert.Equal(
                [
                    "https://github.com/zo1223/OpaiTV/releases/download/v1.3.25/OpaiTV-1.3.25-win-x64-self-contained.zip.sha256",
                    "https://github.com/zo1223/OpaiTV/releases/download/v1.3.25/OpaiTV-1.3.25-win-x64-self-contained.zip",
                ],
                requested);
            Assert.DoesNotContain(requested, uri => uri.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyPortableAsync_rejects_zip_with_wrong_checksum()
    {
        var root = NewTempDirectory("orion-update-portable-hash-");
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent(new string('0', 64), Encoding.ASCII)
                    : new ByteArrayContent(Encoding.UTF8.GetBytes("tampered")),
            }));
        using var service = new DesktopUpdateService(client, root);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAndVerifyPortableAsync(CreateUpdate(new Version(1, 3, 25, 0), "1.3.25")));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagePortableUpdate_extracts_package_and_confirms_its_version()
    {
        var root = NewTempDirectory("orion-update-stage-");
        try
        {
            var (archive, version) = CreatePortableArchive(root, wrapInFolder: false);
            using var service = new DesktopUpdateService(new HttpClient(), Path.Combine(root, "updates"));

            var staged = service.StagePortableUpdate(archive, CreateUpdate(version, version.ToString(3)));

            Assert.True(File.Exists(Path.Combine(staged, "OpaiTV.exe")));
            Assert.True(File.Exists(Path.Combine(staged, "OpaiTV.dll")));
            Assert.Equal("mpv", File.ReadAllText(Path.Combine(staged, "mpv", "mpv.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagePortableUpdate_accepts_single_wrapping_folder()
    {
        var root = NewTempDirectory("orion-update-stage-wrapped-");
        try
        {
            var (archive, version) = CreatePortableArchive(root, wrapInFolder: true);
            using var service = new DesktopUpdateService(new HttpClient(), Path.Combine(root, "updates"));

            var staged = service.StagePortableUpdate(archive, CreateUpdate(version, version.ToString(3)));

            Assert.Equal("OpaiTV", Path.GetFileName(staged));
            Assert.True(File.Exists(Path.Combine(staged, "OpaiTV.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagePortableUpdate_rejects_package_whose_version_differs_from_release()
    {
        var root = NewTempDirectory("orion-update-stage-version-");
        try
        {
            var (archive, version) = CreatePortableArchive(root, wrapInFolder: false);
            var updates = Path.Combine(root, "updates");
            using var service = new DesktopUpdateService(new HttpClient(), updates);
            var other = new Version(version.Major, version.Minor, version.Build + 1, 0);

            Assert.Throws<InvalidDataException>(
                () => service.StagePortableUpdate(archive, CreateUpdate(other, other.ToString(3))));
            Assert.False(Directory.Exists(Path.Combine(updates, other.ToString(3), "portable")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagePortableUpdate_rejects_package_without_executable()
    {
        var root = NewTempDirectory("orion-update-stage-empty-");
        try
        {
            var archive = Path.Combine(root, "package.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
                writer.Write("no app here");
            }

            using var service = new DesktopUpdateService(new HttpClient(), Path.Combine(root, "updates"));

            Assert.Throws<InvalidDataException>(
                () => service.StagePortableUpdate(archive, CreateUpdate(new Version(1, 3, 25, 0), "1.3.25")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUpdateApplier_arguments_round_trip()
    {
        var target = Path.Combine(Path.GetTempPath(), "Opai TV 免安装", "app") + Path.DirectorySeparatorChar;
        var arguments = PortableUpdateApplier.BuildArguments(target, 4321);

        Assert.True(PortableUpdateApplier.TryParseArguments(arguments, out var request));
        Assert.Equal(target, request.TargetDirectory);
        Assert.Equal(4321, request.ProcessId);
    }

    [Theory]
    [InlineData()]
    [InlineData("--apply-portable-update")]
    [InlineData("--apply-portable-update", "relative/dir", "12")]
    [InlineData("--other", "/tmp/app", "12")]
    [InlineData("--apply-portable-update", "/tmp/app", "-5")]
    [InlineData("--apply-portable-update", "/tmp/app", "abc")]
    public void PortableUpdateApplier_ignores_normal_or_malformed_command_lines(params string[] arguments)
    {
        Assert.False(PortableUpdateApplier.TryParseArguments(arguments, out _));
    }

    [Fact]
    public void PortableUpdateApplier_overwrites_running_folder_in_place()
    {
        var root = NewTempDirectory("orion-update-apply-");
        var source = Path.Combine(root, "staged");
        var target = Path.Combine(root, "portable copy");
        try
        {
            WriteFile(source, "OpaiTV.exe", "new exe");
            WriteFile(source, "OpaiTV.dll", "new dll 1.3.25");
            WriteFile(source, Path.Combine("mpv", "mpv.exe"), "new mpv");
            WriteFile(source, Path.Combine("libvlc", "win-x64", "plugins", "new.dll"), "new plugin");
            WriteFile(target, "OpaiTV.exe", "old exe");
            WriteFile(target, "OpaiTV.dll", "old dll 1.3.18");
            WriteFile(target, Path.Combine("mpv", "mpv.exe"), "old mpv");
            WriteFile(target, "user-notes.txt", "keep me");
            File.SetAttributes(Path.Combine(target, "mpv", "mpv.exe"), FileAttributes.ReadOnly);

            PortableUpdateApplier.Apply(source, target + Path.DirectorySeparatorChar, TimeSpan.FromSeconds(5));

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "OpaiTV.exe")));
            Assert.Equal("new dll 1.3.25", File.ReadAllText(Path.Combine(target, "OpaiTV.dll")));
            Assert.Equal("new mpv", File.ReadAllText(Path.Combine(target, "mpv", "mpv.exe")));
            Assert.Equal("new plugin", File.ReadAllText(Path.Combine(target, "libvlc", "win-x64", "plugins", "new.dll")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "user-notes.txt")));
            Assert.Equal("new exe", File.ReadAllText(Path.Combine(source, "OpaiTV.exe")));
        }
        finally
        {
            File.SetAttributes(Path.Combine(target, "mpv", "mpv.exe"), FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUpdateApplier_changes_nothing_while_the_program_is_still_in_use()
    {
        var root = NewTempDirectory("orion-update-apply-locked-");
        var source = Path.Combine(root, "staged");
        var target = Path.Combine(root, "portable");
        try
        {
            WriteFile(source, "OpaiTV.exe", "new exe");
            WriteFile(source, "OpaiTV.dll", "new dll");
            WriteFile(source, "other.dll", "new other");
            WriteFile(target, "OpaiTV.exe", "old exe");
            WriteFile(target, "OpaiTV.dll", "old dll");
            WriteFile(target, "other.dll", "old other");

            using (new FileStream(Path.Combine(target, "OpaiTV.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(
                    () => PortableUpdateApplier.Apply(source, target, TimeSpan.FromMilliseconds(300)));
            }

            Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "OpaiTV.exe")));
            Assert.Equal("old dll", File.ReadAllText(Path.Combine(target, "OpaiTV.dll")));
            Assert.Equal("old other", File.ReadAllText(Path.Combine(target, "other.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUpdateApplier_refuses_folders_that_are_not_an_app_copy()
    {
        var root = NewTempDirectory("orion-update-apply-guard-");
        var source = Path.Combine(root, "staged");
        var target = Path.Combine(root, "documents");
        try
        {
            WriteFile(source, "OpaiTV.exe", "new exe");
            WriteFile(target, "report.txt", "unrelated");

            Assert.Throws<DirectoryNotFoundException>(
                () => PortableUpdateApplier.Apply(source, target, TimeSpan.FromSeconds(1)));
            Assert.Throws<InvalidOperationException>(
                () => PortableUpdateApplier.Apply(source, source, TimeSpan.FromSeconds(1)));
            Assert.Equal(["report.txt"], Directory.GetFiles(target).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUpdateApplier_treats_missing_process_as_exited()
    {
        Assert.True(PortableUpdateApplier.WaitForProcessExit(int.MaxValue, TimeSpan.FromMilliseconds(50)));
        Assert.False(PortableUpdateApplier.WaitForProcessExit(Environment.ProcessId, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void EnsureDirectoryWritable_leaves_no_probe_file_and_reports_unwritable_folders()
    {
        var root = NewTempDirectory("orion-update-writable-");
        try
        {
            DesktopUpdateService.EnsureDirectoryWritable(root);

            Assert.Empty(Directory.GetFileSystemEntries(root));
            Assert.Throws<UnauthorizedAccessException>(
                () => DesktopUpdateService.EnsureDirectoryWritable(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanUpPortableStaging_removes_staged_files_but_keeps_installers()
    {
        var root = NewTempDirectory("orion-update-cleanup-");
        try
        {
            WriteFile(root, Path.Combine("1.3.25", "portable", "OpaiTV.exe"), "staged");
            WriteFile(root, Path.Combine("1.3.25", "OpaiTV-1.3.25-win-x64-self-contained.zip"), "zip");
            WriteFile(root, Path.Combine("1.3.25", "OpaiTV-Setup-1.3.25-win-x64.exe"), "installer");
            WriteFile(root, "last-check-utc.txt", "2026-11-24T00:00:00Z");

            DesktopUpdateService.CleanUpPortableStaging(root);

            Assert.Equal(
                ["1.3.25/OpaiTV-Setup-1.3.25-win-x64.exe", "last-check-utc.txt"],
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DesktopUpdateInfo CreateUpdate(Version version, string versionText) => new(
        version,
        versionText,
        "v" + versionText,
        $"OpaiTV-Setup-{versionText}-win-x64.exe",
        new Uri($"https://github.com/zo1223/OpaiTV/releases/download/v{versionText}/OpaiTV-Setup-{versionText}-win-x64.exe"),
        120_000_000,
        new Uri($"https://github.com/zo1223/OpaiTV/releases/download/v{versionText}/OpaiTV-Setup-{versionText}-win-x64.exe.sha256"),
        "",
        null);

    private static string NewTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Builds a portable package whose OpaiTV.dll is a real managed assembly (a copy of the assembly
    /// under test), so the staged version check reads a genuine assembly version.
    /// </summary>
    private static (string ArchivePath, Version Version) CreatePortableArchive(string root, bool wrapInFolder)
    {
        var assembly = typeof(DesktopUpdateService).Assembly;
        var assemblyVersion = assembly.GetName().Version!;
        var version = new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(0, assemblyVersion.Build), 0);
        var prefix = wrapInFolder ? "OpaiTV/" : "";
        var archive = Path.Combine(root, "package.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(assembly.Location, prefix + "OpaiTV.dll");
        foreach (var (name, content) in new[] { ("OpaiTV.exe", "exe"), ("mpv/mpv.exe", "mpv") })
        {
            using var writer = new StreamWriter(zip.CreateEntry(prefix + name).Open());
            writer.Write(content);
        }

        return (archive, version);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
