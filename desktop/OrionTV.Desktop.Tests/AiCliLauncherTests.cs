using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class AiCliLauncherTests
{
    [Fact]
    public void PublishedSourceGuide_PrefersDirectAndAllowsVerifiedProxyFallback()
    {
        var repositoryRoot = FindRepositoryRoot();
        var guidePath = Path.Combine(repositoryRoot, "desktop", "docs", "ORION_SOURCE_GUIDE.md");
        var guide = File.ReadAllText(guidePath);

        Assert.Contains("AI 找到的候选源网络条件未知", guide, StringComparison.Ordinal);
        Assert.Contains("curl --noproxy '*'", guide, StringComparison.Ordinal);
        Assert.Contains("直连三步验证全部通过时", guide, StringComparison.Ordinal);
        Assert.Contains("只有直连因连接失败、超时、DNS、地域限制或访问限制等网络原因", guide, StringComparison.Ordinal);
        Assert.Contains("代理阶段必须使用当前机器已经配置或用户明确提供的代理", guide, StringComparison.Ordinal);
        Assert.Contains("只有三步全部通过，才允许把该候选作为“代理必需源”写入", guide, StringComparison.Ordinal);
        Assert.Contains("`extra_sources.json` 仍写源站 API，不得虚构客户端不识别的 `proxy` 字段", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("仅通过代理才能访问的普通候选源必须跳过", guide, StringComparison.Ordinal);

        var projectPath = Path.Combine(
            repositoryRoot,
            "desktop",
            "OrionTV.Desktop.App",
            "OrionTV.Desktop.App.csproj");
        var project = XDocument.Load(projectPath);
        var publishedGuide = project
            .Descendants("Content")
            .SingleOrDefault(element =>
                ((string?)element.Attribute("Include"))?.Replace('/', '\\')
                    .EndsWith(@"docs\ORION_SOURCE_GUIDE.md", StringComparison.OrdinalIgnoreCase) == true);

        Assert.NotNull(publishedGuide);
        Assert.Equal(@"docs\ORION_SOURCE_GUIDE.md", (string?)publishedGuide.Element("Link"));
        Assert.Equal("PreserveNewest", (string?)publishedGuide.Element("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)publishedGuide.Element("CopyToPublishDirectory"));
    }

    [Theory]
    [InlineData(AiCliTool.ClaudeCode, 4, 5)]
    [InlineData(AiCliTool.Codex, 5, 6)]
    public void BuildRunnerScript_PassesBootstrapAsOneArgument(
        AiCliTool tool,
        int bootstrapArgument,
        int followingArgument)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "OrionTV AiCli Test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var captureFile = Path.Combine(root, "captured-args.txt");
            var fakeCli = Path.Combine(root, "fake-cli.cmd");
            var promptFile = Path.Combine(root, "ai prompts", "task with spaces.md");
            var outputFile = Path.Combine(root, "user data", "extra_sources.json");
            var docsDir = Path.Combine(root, "published app", "docs");
            var runnerFile = Path.Combine(root, "run-ai.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
            Directory.CreateDirectory(docsDir);

            File.WriteAllText(promptFile, "full prompt with JSON: {\"key\": \"value with spaces\"}");
            File.WriteAllText(
                fakeCli,
                $"@echo off\r\n" +
                $"> \"{captureFile}\" echo 1=[%~1]\r\n" +
                $">> \"{captureFile}\" echo 2=[%~2]\r\n" +
                $">> \"{captureFile}\" echo 3=[%~3]\r\n" +
                $">> \"{captureFile}\" echo 4=[%~4]\r\n" +
                $">> \"{captureFile}\" echo 5=[%~5]\r\n" +
                $">> \"{captureFile}\" echo 6=[%~6]\r\n",
                Encoding.ASCII);

            var script = AiCliLauncher.BuildRunnerScript(
                fakeCli,
                promptFile,
                outputFile,
                docsDir,
                tool,
                discoveryMode: true);
            File.WriteAllText(runnerFile, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Assert.DoesNotContain("$prompt = Get-Content", script);
            Assert.DoesNotContain("-- $prompt", script);
            Assert.Contains("ORION_SOURCE_GUIDE.md", script);
            Assert.DoesNotContain("AGENTS.md", script);
            Assert.DoesNotContain("CLAUDE.md", script);
            Assert.Contains(promptFile, script);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{runnerFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(10_000), "Runner script did not exit.");
            Assert.Equal(0, process.ExitCode);

            var captured = File.ReadAllLines(captureFile);
            var bootstrap = Assert.Single(
                captured,
                line => line.StartsWith($"{bootstrapArgument}=[", StringComparison.Ordinal));
            Assert.Contains(promptFile, bootstrap, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ORION_SOURCE_GUIDE.md", bootstrap, StringComparison.Ordinal);
            Assert.Equal($"{followingArgument}=[]", captured[followingArgument - 1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "desktop", "OrionTV.Desktop.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
