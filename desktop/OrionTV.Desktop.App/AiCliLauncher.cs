using System.Diagnostics;
using System.IO;
using System.Text;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

public enum AiCliTool
{
    ClaudeCode,
    Codex,
}

public sealed record AiCliLaunchResult(bool Success, string Message, string? PromptFile = null);

/// <summary>在新终端中启动 Claude Code / Codex，并注入多协议源适配提示词。</summary>
public static class AiCliLauncher
{
    private enum AiCliTaskMode
    {
        AnalyzeUrl,
        DiscoverSources,
    }

    public static AiCliLaunchResult Launch(AiCliTool tool, string pageUrl, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            return new AiCliLaunchResult(false, "请先输入网址。");
        }

        if (!Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new AiCliLaunchResult(false, "网址无效，请输入以 http:// 或 https:// 开头的地址。");
        }

        return LaunchCore(tool, uri.ToString(), notes, AiCliTaskMode.AnalyzeUrl);
    }

    /// <summary>启动 CLI 主动搜索并验证播放器可用的视频源，无需用户输入网址。</summary>
    public static AiCliLaunchResult LaunchDiscovery(AiCliTool tool, string? notes = null)
    {
        return LaunchCore(tool, pageUrl: null, notes, AiCliTaskMode.DiscoverSources);
    }

    private static AiCliLaunchResult LaunchCore(
        AiCliTool tool,
        string? pageUrl,
        string? notes,
        AiCliTaskMode mode)
    {
        ExtraVideoSourcesStore.EnsureFileExists();

        var docsDir = ResolveDocsDirectory();
        var prompt = mode == AiCliTaskMode.DiscoverSources
            ? AiImportPromptBuilder.BuildDiscovery(
                ExtraVideoSourcesStore.FilePath,
                notes,
                docsDirectory: docsDir,
                settingsPath: DesktopPaths.SettingsFile)
            : AiImportPromptBuilder.Build(
                pageUrl!,
                ExtraVideoSourcesStore.FilePath,
                notes,
                docsDirectory: docsDir,
                settingsPath: DesktopPaths.SettingsFile);

        var promptDir = Path.Combine(DesktopPaths.DataDirectory, "ai_prompts");
        Directory.CreateDirectory(promptDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var toolName = tool == AiCliTool.ClaudeCode ? "claude" : "codex";
        var modeName = mode == AiCliTaskMode.DiscoverSources ? "discover" : "analyze";
        var promptFile = Path.Combine(promptDir, $"{toolName}-{modeName}-{stamp}.md");
        var scriptFile = Path.Combine(promptDir, $"run-{toolName}-{modeName}-{stamp}.ps1");
        File.WriteAllText(promptFile, prompt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var resolved = ResolveCommand(toolName);
        if (resolved is null)
        {
            var tip = tool == AiCliTool.ClaudeCode
                ? "未找到 claude 命令。请先安装 Claude Code 并确保已加入 PATH，然后重启 OrionTV。"
                : "未找到 codex 命令。请先安装 Codex CLI 并确保已加入 PATH，然后重启 OrionTV。";
            tip += $"\n提示词已保存：{promptFile}";
            if (!string.IsNullOrEmpty(docsDir))
            {
                tip += $"\n项目说明：{docsDir}";
            }

            return new AiCliLaunchResult(false, tip, promptFile);
        }

        try
        {
            WriteRunnerScript(
                scriptFile,
                resolved,
                promptFile,
                ExtraVideoSourcesStore.FilePath,
                docsDir,
                tool,
                mode);
            // 工作目录优先 docs 父级（应用根），便于 CLI 相对路径读 docs/
            var workDir = !string.IsNullOrEmpty(docsDir)
                ? Path.GetDirectoryName(docsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    ?? DesktopPaths.DataDirectory
                : DesktopPaths.DataDirectory;
            StartTerminal(scriptFile, workDir);
            var label = tool == AiCliTool.ClaudeCode ? "Claude Code" : "Codex";
            var guidePath = GetGuidePath(docsDir);
            var msg = mode == AiCliTaskMode.DiscoverSources
                ? $"{label} 已在新终端启动 AI 寻源。\n"
                  + "AI 会先完整读取 ORION_SOURCE_GUIDE.md，再主动搜索并验证播放器可用的 AppleCMS/MacCMS 源。\n"
                  + $"验证通过的源会合并写入：{ExtraVideoSourcesStore.FilePath}\n"
                  + "完成后可点「立即刷新源」。桌面端不再支持 TVBox 代理。"
                : $"{label} 已在新终端启动网址解析。\n"
                  + $"目标网址：{pageUrl}\n"
                  + "AI 会先完整读取 ORION_SOURCE_GUIDE.md，然后立即抓取并分析该网址，不会等待再次输入。\n"
                  + $"验证通过的源会合并写入：{ExtraVideoSourcesStore.FilePath}\n"
                  + "完成后可点「立即刷新源」。桌面端不再支持 TVBox 代理。";
            if (guidePath is not null)
            {
                msg += $"\n指南文件：{guidePath}";
            }

            return new AiCliLaunchResult(true, msg, promptFile);
        }
        catch (Exception ex)
        {
            return new AiCliLaunchResult(false, $"启动失败：{ex.Message}\n提示词：{promptFile}", promptFile);
        }
    }

    /// <summary>发布包内 docs/（含 ORION_SOURCE_GUIDE.md）；开发时回退仓库 desktop/docs。</summary>
    public static string? ResolveDocsDirectory()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "docs"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "desktop", "docs")),
        ];

        foreach (var dir in candidates)
        {
            try
            {
                if (Directory.Exists(dir)
                    && File.Exists(Path.Combine(dir, "ORION_SOURCE_GUIDE.md")))
                {
                    return Path.GetFullPath(dir);
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string? GetGuidePath(string? docsDir) =>
        string.IsNullOrWhiteSpace(docsDir)
            ? null
            : Path.Combine(docsDir, "ORION_SOURCE_GUIDE.md");

    public static string? ResolveCommand(string commandName)
    {
        var commandFileNames = OperatingSystem.IsWindows()
            ? new[]
            {
                commandName + ".exe",
                commandName + ".cmd",
                commandName + ".ps1",
                commandName,
            }
            : new[] { commandName };

        foreach (var fileName in commandFileNames)
        {
            if (FindOnPath(fileName) is { } fromPath)
            {
                return fromPath;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidateDirectories =
        [
            Path.Combine(appData, "npm"),
            Path.Combine(localAppData, "npm"),
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "AppData", "Roaming", "npm"),
            @"C:\Program Files\nodejs",
        ];

        foreach (var directory in candidateDirectories)
        {
            foreach (var fileName in commandFileNames)
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static void WriteRunnerScript(
        string scriptFile,
        string commandPath,
        string promptFile,
        string outputFile,
        string? docsDir,
        AiCliTool tool,
        AiCliTaskMode mode)
    {
        var script = BuildRunnerScript(
            commandPath,
            promptFile,
            outputFile,
            docsDir,
            tool,
            mode == AiCliTaskMode.DiscoverSources);
        File.WriteAllText(scriptFile, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    internal static string BuildRunnerScript(
        string commandPath,
        string promptFile,
        string outputFile,
        string? docsDir,
        AiCliTool tool,
        bool discoveryMode)
    {
        var label = tool == AiCliTool.ClaudeCode ? "Claude Code" : "Codex";
        var taskLabel = discoveryMode ? "AI 主动寻源" : "解析指定网址";
        var dataDir = Path.GetDirectoryName(outputFile) ?? DesktopPaths.DataDirectory;
        var guidePath = GetGuidePath(docsDir);
        var bootstrap = BuildBootstrapPrompt(promptFile, guidePath, tool);
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$cli = {QuotePs(commandPath)}");
        sb.AppendLine($"$promptFile = {QuotePs(promptFile)}");
        sb.AppendLine($"$outputFile = {QuotePs(outputFile)}");
        sb.AppendLine($"$dataDir = {QuotePs(dataDir)}");
        sb.AppendLine($"$bootstrap = {QuotePs(bootstrap)}");
        if (guidePath is not null)
        {
            sb.AppendLine($"$guideFile = {QuotePs(guidePath)}");
        }

        sb.AppendLine($"Write-Host 'OrionTV · AI 源站适配 ({label})' -ForegroundColor Cyan");
        sb.AppendLine($"Write-Host '任务: {taskLabel}' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host (\"提示词: \" + $promptFile) -ForegroundColor DarkGray");
        sb.AppendLine("Write-Host (\"CMS 输出: \" + $outputFile) -ForegroundColor DarkGray");
        if (guidePath is not null)
        {
            sb.AppendLine("Write-Host (\"AI 必读指南: \" + $guideFile) -ForegroundColor Yellow");
        }

        sb.AppendLine("Write-Host '仅 AppleCMS/类 CMS → extra_sources.json（桌面已移除 TVBox 代理）' -ForegroundColor DarkGray");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("try {");
        if (tool == AiCliTool.Codex)
        {
            sb.AppendLine("  $cliArgs = @('--search', '--add-dir', $dataDir, '--', $bootstrap)");
        }
        else
        {
            sb.AppendLine("  $cliArgs = @('--add-dir', $dataDir, '--', $bootstrap)");
        }

        sb.AppendLine("  & $cli @cliArgs");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Host $_.Exception.Message -ForegroundColor Red");
        sb.AppendLine("  Write-Host (\"可手动打开提示词: \" + $promptFile) -ForegroundColor Yellow");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("if (Test-Path -LiteralPath $outputFile) {");
        sb.AppendLine("  Write-Host '已检测到 extra_sources.json。可在 OrionTV 点「立即刷新源」。' -ForegroundColor Green");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildBootstrapPrompt(string promptFile, string? guidePath, AiCliTool tool)
    {
        var guideInstruction = guidePath is null
            ? "先在当前工作目录的 docs 目录中找到并使用文件读取工具完整读取 ORION_SOURCE_GUIDE.md"
            : $"先使用文件读取工具完整读取 {guidePath}";
        return $"{guideInstruction}，然后使用文件读取工具完整读取任务文件 {promptFile}，并立即执行该文件中的全部任务，不要等待用户再次输入。";
    }

    private static void StartTerminal(string scriptFile, string workDir)
    {
        var psArgs = $"-NoExit -NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"";

        if (FindOnPath("wt.exe") is { } wt)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wt,
                Arguments = $"-d \"{workDir}\" powershell.exe {psArgs}",
                UseShellExecute = true,
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = psArgs,
            WorkingDirectory = workDir,
            UseShellExecute = true,
        });
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }

    private static string QuotePs(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
