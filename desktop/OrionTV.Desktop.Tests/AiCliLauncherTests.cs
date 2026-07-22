using System.Diagnostics;
using System.Text;
using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class AiCliLauncherTests
{
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
}
