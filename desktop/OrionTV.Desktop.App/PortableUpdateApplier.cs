using System.Diagnostics;
using System.IO;

namespace OrionTV.Desktop.App;

/// <summary>
/// In-place update of a portable (unzipped) copy. Windows cannot overwrite a running exe, so the old
/// app starts the freshly staged OpaiTV.exe with <see cref="ApplySwitch"/>; that helper waits for the
/// old process to exit, copies its own folder over the target folder and restarts the app there.
/// </summary>
public static class PortableUpdateApplier
{
    public const string ApplySwitch = "--apply-portable-update";

    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    public sealed record Request(string TargetDirectory, int ProcessId);

    public static IReadOnlyList<string> BuildArguments(string targetDirectory, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        return [ApplySwitch, targetDirectory, processId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }

    public static bool TryParseArguments(IReadOnlyList<string> arguments, out Request request)
    {
        request = null!;
        if (arguments is null
            || arguments.Count != 3
            || !string.Equals(arguments[0], ApplySwitch, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[1])
            || !Path.IsPathFullyQualified(arguments[1])
            || !int.TryParse(
                arguments[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processId))
        {
            return false;
        }

        request = new Request(arguments[1], processId);
        return true;
    }

    /// <summary>
    /// Helper entry point: waits for the old process, overwrites the target folder with the folder this
    /// helper runs from and starts the updated app. Throws when the update could not be applied.
    /// </summary>
    public static void Run(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WaitForProcessExit(request.ProcessId, ProcessExitTimeout);
        Apply(AppContext.BaseDirectory, request.TargetDirectory, FileLockTimeout);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(request.TargetDirectory, DesktopUpdateService.AppExecutableName),
            WorkingDirectory = request.TargetDirectory,
            UseShellExecute = false,
        });
    }

    /// <summary>Returns true once the process is gone (or never existed), false on timeout.</summary>
    public static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // Already exited.
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Copies every file below <paramref name="sourceDirectory"/> over <paramref name="targetDirectory"/>.
    /// Nothing is modified unless the target's main program files can be opened exclusively, which
    /// proves no instance is still running from that folder. Files that only exist in the target are kept.
    /// </summary>
    public static void Apply(string sourceDirectory, string targetDirectory, TimeSpan lockTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update source and target folders are the same.");
        }

        if (!File.Exists(Path.Combine(source, DesktopUpdateService.AppExecutableName)))
        {
            throw new FileNotFoundException("The staged update does not contain OpaiTV.exe.");
        }

        var targetExecutable = Path.Combine(target, DesktopUpdateService.AppExecutableName);
        if (!File.Exists(targetExecutable))
        {
            throw new DirectoryNotFoundException("The folder to update does not contain OpaiTV.exe.");
        }

        var deadline = Stopwatch.StartNew();
        WaitUntilUnlocked(targetExecutable, deadline, lockTimeout);
        var targetAssembly = Path.Combine(target, DesktopUpdateService.AppAssemblyName);
        if (File.Exists(targetAssembly))
        {
            WaitUntilUnlocked(targetAssembly, deadline, lockTimeout);
        }

        // OpaiTV.dll carries the version shown in settings, so it goes last: a copy that fails half-way
        // never reports the new version.
        var files = Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .OrderBy(file => string.Equals(
                Path.GetRelativePath(source, file),
                DesktopUpdateService.AppAssemblyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var file in files)
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination, deadline, lockTimeout);
        }
    }

    private static void WaitUntilUnlocked(string filePath, Stopwatch elapsed, TimeSpan timeout)
    {
        while (true)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (elapsed.Elapsed >= timeout)
                {
                    throw new IOException($"The file is still in use: {filePath}", exception);
                }
            }

            Thread.Sleep(RetryDelay);
        }
    }

    private static void CopyWithRetry(string source, string destination, Stopwatch elapsed, TimeSpan timeout)
    {
        while (true)
        {
            try
            {
                if (File.Exists(destination))
                {
                    var attributes = File.GetAttributes(destination);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (elapsed.Elapsed >= timeout)
                {
                    throw new IOException($"The file could not be replaced: {destination}", exception);
                }
            }

            Thread.Sleep(RetryDelay);
        }
    }
}
