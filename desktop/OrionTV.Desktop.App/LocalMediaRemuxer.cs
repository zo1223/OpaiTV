using System.Diagnostics;
using System.IO;
using System.Text;

namespace OrionTV.Desktop.App;

/// <summary>
/// 把本地 merged.ts 无重编码转封装为 MP4，生成可快速随机 seek 的索引，
/// 拖动进度接近 PotPlayer 手感。依赖系统 PATH 或常见安装路径中的 ffmpeg。
/// </summary>
internal static class LocalMediaRemuxer
{
    public const string FastSeekFileName = "playback.mp4";

    public static string FastSeekPathOf(string directory) =>
        Path.Combine(directory, FastSeekFileName);

    public static bool HasFastSeekFile(string directory)
    {
        var path = FastSeekPathOf(directory);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public static string? FindFfmpeg()
    {
        var candidates = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe"));
            candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg"));
        }

        candidates.AddRange(
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        ]);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public static bool IsFfmpegAvailable() => FindFfmpeg() is not null;

    /// <summary>
    /// 将 merged.ts（或任意本地媒体）转封装为 playback.mp4。
    /// 成功返回 mp4 路径；ffmpeg 不可用或失败返回 null（不影响原文件播放）。
    /// </summary>
    public static async Task<string?> TryRemuxToFastSeekMp4Async(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        if (sourcePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || sourcePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || sourcePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
        {
            // 已是容器格式，无需再转。
            return sourcePath;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            VodCacheLog.Warn("remux-skip ffmpeg-not-found");
            return null;
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = FastSeekPathOf(outputDirectory);
        var tempPath = outputPath + ".partial";
        TryDelete(tempPath);
        TryDelete(outputPath);

        // -c copy：不重编码，只写索引/容器，速度通常接近文件拷贝。
        // -movflags +faststart：把 moov 提前，利于随机 seek。
        var args =
            $"-y -hide_banner -loglevel error -fflags +genpts -i \"{sourcePath}\" " +
            $"-c copy -bsf:a aac_adtstoasc -movflags +faststart \"{tempPath}\"";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    stderr.AppendLine(e.Data);
                }
            };

            if (!process.Start())
            {
                return null;
            }

            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length <= 0)
            {
                VodCacheLog.Warn(
                    $"remux-failed code={process.ExitCode} err={Trim(stderr.ToString())}");
                TryDelete(tempPath);
                return null;
            }

            File.Move(tempPath, outputPath, true);
            VodCacheLog.Info($"remux-ok src={sourcePath} out={outputPath} size={new FileInfo(outputPath).Length}");
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception error)
        {
            VodCacheLog.Warn($"remux-exception err={error.Message}");
            TryDelete(tempPath);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string Trim(string value)
    {
        var text = value.Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}
