using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

public sealed record DesktopUpdateInfo(
    Version Version,
    string VersionText,
    string TagName,
    string InstallerName,
    Uri InstallerUri,
    long InstallerSize,
    Uri ChecksumUri,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt);

public enum DesktopInstallKind
{
    /// <summary>Installed by the Inno Setup installer (updates run the installer).</summary>
    Installed,

    /// <summary>Extracted from the self-contained zip (updates overwrite the running folder in place).</summary>
    Portable,
}

public sealed partial class DesktopUpdateService : IDisposable
{
    internal const string AppExecutableName = "OpaiTV.exe";
    internal const string AppAssemblyName = "OpaiTV.dll";
    private const string InstallerUninstallerName = "unins000.exe";
    private const string PortableStagingDirectoryName = "portable";

    private const string ReleasesApiUrl = "https://api.github.com/repos/zo1223/OpaiTV/releases?per_page=20";
    private const long MaximumInstallerSize = 1024L * 1024 * 1024;
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(12);
    private static readonly string DefaultUpdateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrionTV.Desktop",
        "updates");

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _updateDirectory;
    private readonly string _lastCheckFile;

    public DesktopUpdateService(HttpClient? httpClient = null, string? updateDirectory = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _updateDirectory = string.IsNullOrWhiteSpace(updateDirectory)
            ? DefaultUpdateDirectory
            : Path.GetFullPath(updateDirectory);
        _lastCheckFile = Path.Combine(_updateDirectory, "last-check-utc.txt");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpaiTV-Desktop", CurrentVersionText));
        }

    }

    public static Version CurrentVersion { get; } = NormalizeVersion(
        typeof(DesktopUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentVersionText => FormatVersion(CurrentVersion);

    public DesktopUpdateInfo? AvailableUpdate { get; private set; }

    public bool IsAutomaticCheckDue()
    {
        try
        {
            if (!File.Exists(_lastCheckFile)
                || !DateTimeOffset.TryParse(File.ReadAllText(_lastCheckFile), out var lastCheck))
            {
                return true;
            }

            return DateTimeOffset.UtcNow - lastCheck.ToUniversalTime() >= AutomaticCheckInterval;
        }
        catch
        {
            return true;
        }
    }

    public async Task<DesktopUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        AvailableUpdate = SelectUpdateFromJson(json, CurrentVersion);
        RecordSuccessfulCheck();
        return AvailableUpdate;
    }

    public async Task<string> DownloadAndVerifyAsync(
        DesktopUpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var installerMatch = InstallerNameRegex().Match(update.InstallerName);
        if (!installerMatch.Success
            || !TryParseVersion(installerMatch.Groups["version"].Value, out var installerVersion)
            || installerVersion != update.Version)
        {
            throw new InvalidDataException("The installer name does not match the release version.");
        }

        if (update.InstallerSize is <= 0 or > MaximumInstallerSize)
        {
            throw new InvalidDataException("The release reports an invalid installer size.");
        }

        return await DownloadFileAsync(
            update.InstallerUri,
            update.ChecksumUri,
            update.VersionText,
            update.InstallerName,
            update.InstallerSize,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the self-contained zip that belongs to the same release as <paramref name="update"/>.
    /// The zip is verified against its published SHA-256 file before it is returned.
    /// </summary>
    public async Task<string> DownloadAndVerifyPortableAsync(
        DesktopUpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var archiveName = GetPortableArchiveName(update);
        return await DownloadFileAsync(
            new Uri(update.InstallerUri, archiveName),
            new Uri(update.InstallerUri, archiveName + ".sha256"),
            update.VersionText,
            archiveName,
            expectedSize: null,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadFileAsync(
        Uri fileUri,
        Uri checksumUri,
        string versionText,
        string fileName,
        long? expectedSize,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var expectedHash = await DownloadChecksumAsync(checksumUri, cancellationToken).ConfigureAwait(false);
        var versionDirectory = Path.Combine(_updateDirectory, versionText);
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, fileName);

        if (File.Exists(destination)
            && (expectedSize is null || new FileInfo(destination).Length == expectedSize.Value)
            && await HasExpectedHashAsync(destination, expectedHash, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(100);
            return destination;
        }

        var temporary = destination + ".download";
        try
        {
            progress?.Report(0);
            using var response = await _http.GetAsync(
                fileUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaximumInstallerSize
                || (expectedSize.HasValue && contentLength.HasValue && contentLength.Value != expectedSize.Value))
            {
                throw new InvalidDataException("The downloaded installer size does not match the release metadata.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var maximumSize = expectedSize ?? MaximumInstallerSize;
                var progressTotal = expectedSize ?? contentLength ?? 0;
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > maximumSize || written > MaximumInstallerSize)
                    {
                        throw new InvalidDataException("The downloaded installer is larger than expected.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (progressTotal > 0)
                    {
                        progress?.Report((int)Math.Min(100, written * 100 / progressTotal));
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (written == 0 || (expectedSize.HasValue && written != expectedSize.Value))
                {
                    throw new InvalidDataException("The downloaded installer is incomplete.");
                }
            }

            if (!await HasExpectedHashAsync(temporary, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The installer SHA-256 checksum does not match.");
            }

            File.Move(temporary, destination, overwrite: true);
            progress?.Report(100);
            return destination;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static string? BackupUserConfiguration(string targetVersion)
    {
        var backupDirectory = Path.Combine(DesktopPaths.DataDirectory, "backups");
        return CreateConfigurationBackup(DesktopPaths.DataDirectory, backupDirectory, targetVersion);
    }

    public static string? CreateConfigurationBackup(
        string dataDirectory,
        string backupDirectory,
        string targetVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        var files = new[] { "settings.json", "extra_sources.json" }
            .Select(name => Path.Combine(dataDirectory, name))
            .Where(File.Exists)
            .ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff");
        var safeVersion = string.Concat(targetVersion.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-'));
        var backupPath = Path.Combine(backupDirectory, $"pre-update-{safeVersion}-{timestamp}.zip");
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        }

        return backupPath;
    }

    public static void LaunchInstaller(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (!File.Exists(installerPath)
            || !string.Equals(Path.GetExtension(installerPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The verified update installer was not found.", installerPath);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1",
            WorkingDirectory = Path.GetDirectoryName(installerPath),
            UseShellExecute = true,
            Verb = "runas",
        });
        if (process is null)
        {
            throw new InvalidOperationException("The update installer could not be started.");
        }
    }

    /// <summary>Directory that holds the running OpaiTV.exe.</summary>
    public static string CurrentAppDirectory =>
        TrimDirectory(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);

    public static DesktopInstallKind CurrentInstallKind => DetectInstallKind(
        CurrentAppDirectory,
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        ],
        File.Exists(Path.Combine(CurrentAppDirectory, InstallerUninstallerName)));

    /// <summary>
    /// Decides whether the app folder is an installer-managed install or a portable (unzipped) copy.
    /// A folder below Program Files, or one that contains the Inno Setup uninstaller (custom install
    /// directory), is installer-managed; everything else is portable.
    /// </summary>
    public static DesktopInstallKind DetectInstallKind(
        string appDirectory,
        IEnumerable<string?> programFilesRoots,
        bool hasInstallerUninstaller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(programFilesRoots);
        if (hasInstallerUninstaller)
        {
            return DesktopInstallKind.Installed;
        }

        var directory = NormalizeWindowsPath(appDirectory);
        foreach (var root in programFilesRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = NormalizeWindowsPath(root);
            if (directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return DesktopInstallKind.Installed;
            }
        }

        return DesktopInstallKind.Portable;
    }

    /// <summary>Name of the self-contained zip published next to the installer of the same release.</summary>
    public static string GetPortableArchiveName(DesktopUpdateInfo update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var match = InstallerNameRegex().Match(update.InstallerName);
        if (!match.Success
            || !TryParseVersion(match.Groups["version"].Value, out var installerVersion)
            || installerVersion != update.Version)
        {
            throw new InvalidDataException("The installer name does not match the release version.");
        }

        return $"OpaiTV-{match.Groups["version"].Value}-win-x64-self-contained.zip";
    }

    /// <summary>
    /// Extracts the verified portable zip into a fresh staging folder and checks that it really
    /// contains the expected version. Returns the folder that holds the new OpaiTV.exe.
    /// </summary>
    public string StagePortableUpdate(string archivePath, DesktopUpdateInfo update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(update);
        var staging = Path.Combine(_updateDirectory, update.VersionText, PortableStagingDirectoryName);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);
            var root = FindStagedAppRoot(staging)
                ?? throw new InvalidDataException("The portable package does not contain OpaiTV.exe.");
            var stagedVersion = ReadStagedVersion(root);
            if (stagedVersion is null || stagedVersion != update.Version)
            {
                throw new InvalidDataException("The portable package version does not match the release version.");
            }

            return root;
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>Throws when files cannot be created in <paramref name="directory"/>.</summary>
    public static void EnsureDirectoryWritable(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var probe = Path.Combine(directory, $".opaitv-update-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe))
            {
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("The application directory is not writable.", exception);
        }

        TryDelete(probe);
    }

    /// <summary>
    /// Starts the freshly staged OpaiTV.exe as a helper. It waits for this process to exit, copies the
    /// staged files over <paramref name="targetDirectory"/> and then restarts the app from there.
    /// </summary>
    public static void LaunchPortableUpdater(string stagedAppDirectory, string targetDirectory, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedAppDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var helper = Path.Combine(stagedAppDirectory, AppExecutableName);
        if (!File.Exists(helper))
        {
            throw new FileNotFoundException("The staged update executable was not found.", helper);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            WorkingDirectory = stagedAppDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in PortableUpdateApplier.BuildArguments(targetDirectory, processId))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("The portable updater could not be started.");
        }
    }

    /// <summary>Best-effort removal of staged portable updates left behind by a finished update.</summary>
    public static void CleanUpPortableStaging(string? updateDirectory = null)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(updateDirectory) ? DefaultUpdateDirectory : updateDirectory;
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var versionDirectory in Directory.GetDirectories(root))
            {
                TryDeleteDirectory(Path.Combine(versionDirectory, PortableStagingDirectoryName));
                foreach (var archive in Directory.GetFiles(versionDirectory, "OpaiTV-*-win-x64-self-contained.zip"))
                {
                    TryDelete(archive);
                }
            }
        }
        catch
        {
            // Leftover staging files only cost disk space; the next start retries.
        }
    }

    public static DesktopUpdateInfo? SelectUpdateFromJson(string json, Version currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(currentVersion);
        var normalizedCurrent = NormalizeVersion(currentVersion);
        DesktopUpdateInfo? selected = null;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GitHub releases response must be an array.");
        }

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (GetBoolean(release, "draft") || GetBoolean(release, "prerelease"))
            {
                continue;
            }

            var tagName = GetString(release, "tag_name");
            if (!TryParseVersion(tagName, out var releaseVersion)
                || releaseVersion <= normalizedCurrent
                || (selected is not null && releaseVersion <= selected.Version))
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            JsonElement? installerAsset = null;
            JsonElement? checksumAsset = null;
            string? installerName = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = GetString(asset, "name");
                var match = InstallerNameRegex().Match(assetName);
                if (!match.Success
                    || !TryParseVersion(match.Groups["version"].Value, out var assetVersion)
                    || assetVersion != releaseVersion)
                {
                    continue;
                }

                installerAsset = asset;
                installerName = assetName;
                break;
            }

            if (installerAsset is null || installerName is null)
            {
                continue;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (string.Equals(
                        GetString(asset, "name"),
                        installerName + ".sha256",
                        StringComparison.OrdinalIgnoreCase))
                {
                    checksumAsset = asset;
                    break;
                }
            }

            if (checksumAsset is null
                || !TryGetGitHubDownloadUri(installerAsset.Value, out var installerUri)
                || !TryGetGitHubDownloadUri(checksumAsset.Value, out var checksumUri))
            {
                continue;
            }

            var installerSize = GetInt64(installerAsset.Value, "size");
            if (installerSize is <= 0 or > MaximumInstallerSize)
            {
                continue;
            }

            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(GetString(release, "published_at"), out var parsedPublishedAt))
            {
                publishedAt = parsedPublishedAt;
            }

            selected = new DesktopUpdateInfo(
                releaseVersion,
                FormatVersion(releaseVersion),
                tagName,
                installerName,
                installerUri,
                installerSize,
                checksumUri,
                GetString(release, "body"),
                publishedAt);
        }

        return selected;
    }

    public static bool TryParseChecksum(string value, out string checksum)
    {
        checksum = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Sha256Regex().Match(value);
        if (!match.Success)
        {
            return false;
        }

        checksum = match.Value.ToLowerInvariant();
        return true;
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
        }

        return $"{bytes / (1024d * 1024):0.#} MB";
    }

    private async Task<string> DownloadChecksumAsync(
        Uri checksumUri,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var checksumText = await _http.GetStringAsync(checksumUri, timeout.Token).ConfigureAwait(false);
        if (!TryParseChecksum(checksumText, out var checksum))
        {
            throw new InvalidDataException("The release checksum file is invalid.");
        }

        return checksum;
    }

    private static async Task<bool> HasExpectedHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash));
    }

    private static bool TryGetGitHubDownloadUri(JsonElement asset, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(GetString(asset, "browser_download_url"), UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && (uri = parsed) is not null;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version();
        var match = VersionRegex().Match(value ?? "");
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed))
        {
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string FormatVersion(Version version) => version.Revision > 0
        ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
        : $"{version.Major}.{version.Minor}.{version.Build}";

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        && property.GetBoolean();

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;

    private void RecordSuccessfulCheck()
    {
        try
        {
            Directory.CreateDirectory(_updateDirectory);
            File.WriteAllText(_lastCheckFile, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // A failed timestamp write should not turn a successful update check into an error.
        }
    }

    private static string? FindStagedAppRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, AppExecutableName)))
        {
            return staging;
        }

        // Tolerate a zip that wraps everything in one top-level folder.
        var directories = Directory.GetDirectories(staging);
        return directories.Length == 1
               && Directory.GetFiles(staging).Length == 0
               && File.Exists(Path.Combine(directories[0], AppExecutableName))
            ? directories[0]
            : null;
    }

    private static Version? ReadStagedVersion(string appDirectory)
    {
        try
        {
            var assemblyPath = Path.Combine(appDirectory, AppAssemblyName);
            if (File.Exists(assemblyPath))
            {
                var assemblyVersion = System.Reflection.AssemblyName.GetAssemblyName(assemblyPath).Version;
                return assemblyVersion is null ? null : NormalizeVersion(assemblyVersion);
            }

            var fileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(appDirectory, AppExecutableName));
            return fileVersion.FileVersion is not null
                   && Version.TryParse(
                       $"{fileVersion.FileMajorPart}.{fileVersion.FileMinorPart}.{fileVersion.FileBuildPart}.{fileVersion.FilePrivatePart}",
                       out var parsed)
                ? NormalizeVersion(parsed)
                : null;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string NormalizeWindowsPath(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\');

    private static string TrimDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Files still in use (for example the helper that is just exiting) are removed next time.
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // A stale partial download is harmless and will be overwritten on the next attempt.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    [GeneratedRegex(@"^OpaiTV-Setup-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)-win-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerNameRegex();

    [GeneratedRegex(@"^v?(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Regex();
}
