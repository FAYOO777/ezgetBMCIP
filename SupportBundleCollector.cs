using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace EzGetBmcIp
{

internal static class AppVersionText
{
    internal static string Get()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
            version = assembly.GetName().Version?.ToString();

        if (string.IsNullOrWhiteSpace(version))
            return "v0.0.0";

        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version.Substring(0, plusIndex);

        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;
    }
}

internal static class SupportBundleShortcut
{
    internal static bool Matches(ModifierKeys modifiers, Key key, Key systemKey)
    {
        if (modifiers != ModifierKeys.Alt)
            return false;

        var effectiveKey = key == Key.System ? systemKey : key;
        return effectiveKey == Key.L;
    }
}

internal sealed class SupportBundleProgress
{
    internal SupportBundleProgress(int percent, string message)
    {
        if (percent < 0 || percent > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));

        Percent = percent;
        Message = message ?? string.Empty;
    }

    internal int Percent { get; }
    internal string Message { get; }
}

internal static class SupportBundleCollector
{
    private sealed class IgnoreProgress : IProgress<SupportBundleProgress>
    {
        public void Report(SupportBundleProgress value)
        {
        }
    }

    private static readonly IProgress<SupportBundleProgress> NoProgress = new IgnoreProgress();

    internal static string DefaultSupportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ezgetBMCIP", "Support");

    internal static Task<string> CreateAsync(
        string archivePrefix,
        string logFilePath,
        Func<string, Task> writeDiagnosticsAsync,
        string supportDirectory = "",
        string temporaryDirectoryRoot = "")
    {
        return CreateAsync(
            archivePrefix,
            logFilePath,
            writeDiagnosticsAsync,
            NoProgress,
            supportDirectory,
            temporaryDirectoryRoot);
    }

    internal static async Task<string> CreateAsync(
        string archivePrefix,
        string logFilePath,
        Func<string, Task> writeDiagnosticsAsync,
        IProgress<SupportBundleProgress> progress,
        string supportDirectory = "",
        string temporaryDirectoryRoot = "")
    {
        if (string.IsNullOrWhiteSpace(archivePrefix))
            throw new ArgumentException("Archive prefix is required.", nameof(archivePrefix));
        if (string.IsNullOrWhiteSpace(logFilePath))
            throw new ArgumentException("Log file path is required.", nameof(logFilePath));
        if (writeDiagnosticsAsync is null)
            throw new ArgumentNullException(nameof(writeDiagnosticsAsync));
        if (progress is null)
            throw new ArgumentNullException(nameof(progress));

        progress.Report(new SupportBundleProgress(0, "正在准备支持包..."));

        var outputDirectory = string.IsNullOrWhiteSpace(supportDirectory)
            ? DefaultSupportDirectory
            : supportDirectory;
        Directory.CreateDirectory(outputDirectory);

        var tempRoot = string.IsNullOrWhiteSpace(temporaryDirectoryRoot)
            ? Path.GetTempPath()
            : temporaryDirectoryRoot;
        Directory.CreateDirectory(tempRoot);

        var stagingDirectory = Path.Combine(tempRoot, "ezgetBMCIP-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var archivePath = "";

        try
        {
            var diagnosticsPath = Path.Combine(stagingDirectory, "diagnostics.txt");
            await writeDiagnosticsAsync(diagnosticsPath);
            if (!File.Exists(diagnosticsPath))
                throw new InvalidOperationException("Diagnostics writer did not create diagnostics.txt.");

            progress.Report(new SupportBundleProgress(85, "正在复制应用日志..."));
            await Task.Run(() => SnapshotLog(logFilePath, Path.Combine(stagingDirectory, "ezgetBMCIP.log")));

            progress.Report(new SupportBundleProgress(95, "正在压缩支持包..."));
            archivePath = GetUniqueArchivePath(outputDirectory, archivePrefix);
            await Task.Run(() => ZipFile.CreateFromDirectory(
                stagingDirectory,
                archivePath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false));
            progress.Report(new SupportBundleProgress(100, "支持包已生成。"));
            return archivePath;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
            {
                try { File.Delete(archivePath); } catch { }
            }

            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
                // The archive is already complete; leftover temporary files can be removed later by Windows.
            }
        }
    }

    private static string GetUniqueArchivePath(string outputDirectory, string archivePrefix)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        for (var suffix = 0; ; suffix++)
        {
            var name = archivePrefix + "-" + timestamp + (suffix == 0 ? "" : "-" + suffix) + ".zip";
            var candidate = Path.Combine(outputDirectory, name);
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static void SnapshotLog(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            File.WriteAllText(destinationPath, "(log file not found: " + sourcePath + ")", new UTF8Encoding(true));
            return;
        }

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }
}

}
