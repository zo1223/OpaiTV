using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace OrionTV.Desktop.App.LocalPlayback;

/// <summary>
/// 方案 A：本地文件用外部 PotPlayer 播放（在线仍走 LibVLC）。
/// </summary>
public static class PotPlayerLauncher
{
    public const string DefaultPath = @"E:\potPlayer\PotPlayerMini64.exe";

    public static string ResolveExecutable(string? configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"')));
        }

        candidates.AddRange(
        [
            DefaultPath,
            @"E:\potPlayer\PotPlayer64.exe",
            @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe",
            @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini.exe",
            @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini64.exe",
            @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini.exe",
            Path.Combine(AppContext.BaseDirectory, "PotPlayer", "PotPlayerMini64.exe"),
        ]);

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), "PotPlayerMini64.exe"));
            candidates.Add(Path.Combine(dir.Trim('"'), "PotPlayerMini.exe"));
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }

        return "";
    }

    public static bool IsAvailable(string? configuredPath) =>
        !string.IsNullOrWhiteSpace(ResolveExecutable(configuredPath));

    /// <summary>
    /// 将播放地址解析为本地文件路径；非本地则返回 null。
    /// </summary>
    public static string? TryGetLocalFilePath(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return null;
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            }

            if (uri.Scheme is "http" or "https")
            {
                return null;
            }
        }

        try
        {
            var full = Path.GetFullPath(pathOrUrl);
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsLocalMedia(string pathOrUrl) => TryGetLocalFilePath(pathOrUrl) is not null;

    /// <summary>
    /// 用 PotPlayer 打开本地文件。reuseExisting=true 时尽量复用已打开实例（/current）。
    /// </summary>
    public static bool TryLaunch(
        string pathOrUrl,
        string? configuredPath,
        out string message,
        long startMs = 0,
        bool reuseExisting = true)
    {
        var filePath = TryGetLocalFilePath(pathOrUrl);
        if (filePath is null)
        {
            message = "不是本地文件";
            return false;
        }

        var exe = ResolveExecutable(configuredPath);
        if (string.IsNullOrWhiteSpace(exe))
        {
            message = "未找到 PotPlayer，请在设置中指定 PotPlayerMini64.exe 路径";
            return false;
        }

        var args = new StringBuilder();
        args.Append('"').Append(filePath).Append('"');
        if (reuseExisting)
        {
            args.Append(" /current");
        }
        else
        {
            args.Append(" /new");
        }

        if (startMs > 0)
        {
            args.Append(" /seek=").Append(FormatSeek(startMs));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args.ToString(),
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };
            Process.Start(psi);
            message = $"已用 PotPlayer 打开：{Path.GetFileName(filePath)}";
            return true;
        }
        catch (Exception ex)
        {
            message = "启动 PotPlayer 失败：" + ex.Message;
            return false;
        }
    }

    public static string FormatSeek(long startMs)
    {
        if (startMs < 0)
        {
            startMs = 0;
        }

        var ts = TimeSpan.FromMilliseconds(startMs);
        // PotPlayer: hh:mm:ss.ms
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds);
    }
}
