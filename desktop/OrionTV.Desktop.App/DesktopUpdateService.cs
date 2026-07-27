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

public sealed partial class DesktopUpdateService : IDisposable
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/zo1223/OrionTV/releases?per_page=20";
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

        var expectedHash = await DownloadChecksumAsync(update, cancellationToken).ConfigureAwait(false);
        var versionDirectory = Path.Combine(_updateDirectory, update.VersionText);
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, update.InstallerName);

        if (File.Exists(destination)
            && new FileInfo(destination).Length == update.InstallerSize
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
                update.InstallerUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaximumInstallerSize
                || (contentLength.HasValue && contentLength.Value != update.InstallerSize))
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
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > update.InstallerSize || written > MaximumInstallerSize)
                    {
                        throw new InvalidDataException("The downloaded installer is larger than expected.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((int)(written * 100 / update.InstallerSize));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (written != update.InstallerSize)
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
        DesktopUpdateInfo update,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var checksumText = await _http.GetStringAsync(update.ChecksumUri, timeout.Token).ConfigureAwait(false);
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
