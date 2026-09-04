using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace EzGetBmcIp;

internal static class DiagnosticReporter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
    private static readonly Encoding NativeConsoleEncoding = CreateNativeConsoleEncoding();
    public static async Task<string> WriteReportAsync(
        MainViewModel viewModel,
        string reportPath,
        IProgress<SupportBundleProgress> progress)
    {
        var reportDirectory = Path.GetDirectoryName(reportPath);
        if (string.IsNullOrWhiteSpace(reportDirectory))
            throw new InvalidOperationException("Diagnostic report directory is unavailable.");
        Directory.CreateDirectory(reportDirectory);
        var isAdmin = IsAdministrator();

        var sb = new StringBuilder();
        AppendHeader(sb, "ezgetBMCIP diagnostics");
        AppendLine(sb, "GeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendLine(sb, "Version", AppVersionText.Get());
        AppendLine(sb, "OS", Environment.OSVersion + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
        AppendLine(sb, "Process", Environment.Is64BitProcess ? "x64" : "x86");
        AppendLine(sb, ".NET", Environment.Version.ToString());
        AppendLine(sb, "Administrator", isAdmin ? "true" : "false");
        AppendLine(sb, "LogPath", AppLogger.LogFilePath);
        AppendLine(sb, "ReportPath", reportPath);
        AppendLine(sb, "RecoverySnapshotPath", NetworkRecoveryStore.RecoveryFilePath);
        AppendLine(sb, "RecoverySnapshotExists", File.Exists(NetworkRecoveryStore.RecoveryFilePath) ? "true" : "false");

        progress.Report(new SupportBundleProgress(10, "正在收集应用和网络状态..."));
        await AppendSummaryAsync(sb, viewModel, isAdmin);

        AppendHeader(sb, "App state");
        AppendLine(sb, "Phase", viewModel.AppPhase.ToString());
        AppendLine(sb, "Status", viewModel.StatusText);
        AppendLine(sb, "Detail", viewModel.DetailText);
        AppendLine(sb, "Activity", viewModel.ActivityText);
        AppendLine(sb, "Subnet", viewModel.SubnetConfig.ServerDisplay);
        AppendLine(sb, "BMC pool address", viewModel.SubnetConfig.PoolDisplay);
        AppendLine(sb, "DHCP listener", viewModel.DhcpListenerStatus);
        AppendLine(sb, "Discovered URL", viewModel.IsIpDiscovered ? viewModel.DiscoveredIpUrl : "(not discovered)");
        AppendLine(sb, "Endpoint status", viewModel.EndpointStatusText);

        var selected = viewModel.SelectedAdapterItem;
        AppendHeader(sb, "Selected adapter");
        if (selected is null)
        {
            sb.AppendLine("No adapter selected.");
        }
        else
        {
            AppendLine(sb, "Name", selected.Name);
            AppendLine(sb, "Description", selected.Description);
            AppendLine(sb, "Id", selected.Id);
            AppendLine(sb, "Mac", selected.MacAddress);
        }

        AppendHeader(sb, "Visible wired adapters");
        try
        {
            foreach (var adapter in NetworkConfigManager.GetWiredAdapters())
            {
                sb.AppendLine(adapter.DisplayName);
                AppendLine(sb, "  Id", adapter.Id);
                AppendLine(sb, "  Mac", adapter.MacAddress);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Adapter enumeration failed: " + ex.Message);
        }

        progress.Report(new SupportBundleProgress(20, "正在读取 IP 配置..."));
        await AppendCommandAsync(sb, "ipconfig /all", "ipconfig.exe", "/all");
        progress.Report(new SupportBundleProgress(30, "正在读取路由表..."));
        await AppendCommandAsync(sb, "route print", "route.exe", "print");
        progress.Report(new SupportBundleProgress(40, "正在检查 UDP 监听..."));
        await AppendCommandAsync(sb, "netstat UDP listeners", "netstat.exe", "-ano -p udp");
        progress.Report(new SupportBundleProgress(50, "正在检查 DHCP 服务..."));
        await AppendPowerShellAsync(sb, "DHCP Client service", "Get-Service Dhcp | Format-List Name,Status,StartType,CanStop");
        progress.Report(new SupportBundleProgress(60, "正在读取网卡配置..."));
        await AppendPowerShellAsync(sb, "Network adapter IP summary", "Get-NetIPConfiguration | Format-List InterfaceAlias,InterfaceDescription,IPv4Address,IPv4DefaultGateway,DNSServer");
        progress.Report(new SupportBundleProgress(70, "正在检查 DHCP 端口占用..."));
        await AppendPowerShellAsync(sb, "UDP 67 owning process", "Get-NetUDPEndpoint -LocalPort 67 -ErrorAction SilentlyContinue | ForEach-Object { $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; [PSCustomObject]@{LocalAddress=$_.LocalAddress;LocalPort=$_.LocalPort;OwningProcess=$_.OwningProcess;ProcessName=$p.ProcessName} } | Format-List");

        progress.Report(new SupportBundleProgress(72, "正在检查网络类别和防火墙规则..."));
        AppendHeader(sb, "Selected adapter network profile and Windows Defender Firewall");
        if (selected is null)
        {
            sb.AppendLine("No adapter selected; firewall assessment skipped.");
        }
        else
        {
            try
            {
                var firewallAssessment = await FirewallAssessmentService.AssessAsync(
                    selected.Name,
                    selected.Id,
                    selected.MacAddress,
                    FirewallAssessmentService.GetCurrentExecutablePath());
                sb.AppendLine(firewallAssessment.ToDiagnosticText());
            }
            catch (Exception ex)
            {
                sb.AppendLine("Firewall assessment failed without aborting support collection: " + ex);
            }
        }

        AppendHeader(sb, "Current session application log");
        sb.AppendLine(ReadCurrentSessionLogLines(300));

        progress.Report(new SupportBundleProgress(75, "正在写入诊断报告..."));
        await File.WriteAllTextAsync(reportPath, sb.ToString(), Utf8WithBom);
        AppLogger.Log("Diagnostics report written: " + reportPath);
        return reportPath;
    }

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine("=== " + title + " ===");
    }

    private static void AppendLine(StringBuilder sb, string key, string? value)
    {
        sb.AppendLine(key + ": " + (string.IsNullOrWhiteSpace(value) ? "(empty)" : value));
    }

    private static async Task AppendSummaryAsync(StringBuilder sb, MainViewModel viewModel, bool isAdmin)
    {
        AppendHeader(sb, "Quick summary");
        AppendLine(sb, "Admin", isAdmin ? "OK" : "NOT ADMIN - network configuration and DHCP bind can fail");
        AppendLine(sb, "Selected adapter", viewModel.SelectedAdapterItem?.DisplayName ?? "(none)");
        AppendLine(sb, "Visible wired adapter count", viewModel.Adapters.Count.ToString());
        AppendLine(sb, "Subnet validation", viewModel.SubnetConfig.IsPrivateSubnet
            ? "OK"
            : "INVALID - " + viewModel.SubnetConfig.ValidationError);
        AppendLine(sb, "BMC URL", viewModel.IsIpDiscovered ? viewModel.DiscoveredIpUrl : "(not discovered yet)");
        AppendLine(sb, "UDP 67", await GetUdp67SummaryAsync());
    }

    private static async Task AppendCommandAsync(
        StringBuilder sb,
        string title,
        string fileName,
        string arguments,
        Encoding? outputEncoding = null)
    {
        AppendHeader(sb, title);
        sb.AppendLine(await RunProcessAsync(fileName, arguments, outputEncoding: outputEncoding));
    }

    private static async Task AppendPowerShellAsync(StringBuilder sb, string title, string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; $ProgressPreference='SilentlyContinue'; " + command));
        await AppendCommandAsync(
            sb,
            title,
            "powershell.exe",
            "-NoProfile -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            Utf8NoBom);
    }

    private static async Task<string> GetUdp67SummaryAsync()
    {
        var command = "Get-NetUDPEndpoint -LocalPort 67 -ErrorAction SilentlyContinue | Select-Object -First 5 LocalAddress,LocalPort,OwningProcess | Format-Table -HideTableHeaders";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; $ProgressPreference='SilentlyContinue'; " + command));
        var output = await RunProcessAsync(
            "powershell.exe",
            "-NoProfile -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            includeCommandLine: false,
            outputEncoding: Utf8NoBom);
        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("ExitCode:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.Length == 0 ? "free or not visible" : string.Join(" | ", lines);
    }

    internal static async Task<string> RunProcessAsync(
        string fileName,
        string arguments,
        bool includeCommandLine = true,
        Encoding? outputEncoding = null)
    {
        try
        {
            var encoding = outputEncoding ?? NativeConsoleEncoding;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding
                },
                EnableRaisingEvents = true
            };

            process.Start();
            var stdoutTask = ProcessOutputDecoder.ReadAllBytesAsync(process.StandardOutput.BaseStream);
            var stderrTask = ProcessOutputDecoder.ReadAllBytesAsync(process.StandardError.BaseStream);
            var waitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "Command timed out: " + fileName + " " + arguments;
            }

            var stdout = CleanProcessOutput(ProcessOutputDecoder.Decode(await stdoutTask, encoding));
            var stderr = CleanProcessOutput(ProcessOutputDecoder.Decode(await stderrTask, encoding));
            var result = new StringBuilder();
            if (includeCommandLine)
                result.AppendLine("Command: " + fileName + " " + arguments);
            result.AppendLine("ExitCode: " + process.ExitCode);
            if (!string.IsNullOrWhiteSpace(stdout))
                result.AppendLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                result.AppendLine("--- stderr ---");
                result.AppendLine(stderr.TrimEnd());
            }
            return result.ToString();
        }
        catch (Exception ex)
        {
            return "Command failed: " + fileName + " " + arguments + Environment.NewLine + ex;
        }
    }

    internal static Encoding GetNativeConsoleEncoding() => NativeConsoleEncoding;

    private static Encoding CreateNativeConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    private static string CleanProcessOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var lines = text.Replace("_x000D__x000A_", Environment.NewLine)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(line => !line.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.TrimEnd());

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string ReadCurrentSessionLogLines(int maxLines)
    {
        try
        {
            if (!File.Exists(AppLogger.LogFilePath))
                return "(log file not found)";

            var lines = File.ReadAllLines(AppLogger.LogFilePath, Encoding.UTF8);
            var startIndex = Array.FindLastIndex(lines, line =>
                line.Contains("=== ezgetBMCIP startup ===", StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0)
                startIndex = Math.Max(0, lines.Length - maxLines);

            var sessionLines = lines.Skip(startIndex).ToArray();
            if (sessionLines.Length > maxLines)
                sessionLines = sessionLines.Skip(sessionLines.Length - maxLines).ToArray();

            return sessionLines.Length == 0
                ? "(current session log is empty)"
                : string.Join(Environment.NewLine, sessionLines);
        }
        catch (Exception ex)
        {
            return "Failed to read log: " + ex.Message;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

}
