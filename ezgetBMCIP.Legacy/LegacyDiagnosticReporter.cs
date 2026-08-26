using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace EzGetBmcIp.Legacy
{
    internal static class LegacyDiagnosticReporter
    {
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
        private static readonly Encoding NativeConsoleEncoding = Encoding.GetEncoding(
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

        internal static async Task<string> WriteReportAsync(
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
            AppendHeader(sb, "ezgetBMCIP Legacy diagnostics");
            AppendLine(sb, "GeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            AppendLine(sb, "Version", AppVersionText.Get());
            AppendLine(sb, "OS", Environment.OSVersion + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
            AppendLine(sb, "Process", Environment.Is64BitProcess ? "x64" : "x86");
            AppendLine(sb, ".NET", Environment.Version.ToString());
            AppendLine(sb, "Administrator", isAdmin ? "true" : "false");
            AppendLine(sb, "LogPath", App.LogFilePath);
            AppendLine(sb, "ReportPath", reportPath);
            AppendLine(sb, "RecoverySnapshotPath", NetworkRecoveryStore.RecoveryFilePath);
            AppendLine(sb, "RecoverySnapshotExists", File.Exists(NetworkRecoveryStore.RecoveryFilePath) ? "true" : "false");

            progress.Report(new SupportBundleProgress(10, "正在收集应用和网卡状态..."));
            AppendHeader(sb, "App state");
            AppendLine(sb, "Status", viewModel.StatusText);
            AppendLine(sb, "Detail", viewModel.DetailText);
            AppendLine(sb, "Activity", viewModel.ActivityText);
            AppendLine(sb, "Badge", viewModel.BadgeText);
            AppendLine(sb, "Subnet", viewModel.SubnetConfig.ServerDisplay);
            AppendLine(sb, "BMC pool address", viewModel.SubnetConfig.PoolDisplay);
            AppendLine(sb, "Discovered URL", string.IsNullOrWhiteSpace(viewModel.DiscoveredIpUrl)
                ? "(not discovered)"
                : viewModel.DiscoveredIpUrl);
            AppendLine(sb, "Endpoint status", viewModel.EndpointStatusText);

            AppendHeader(sb, "Selected adapter");
            var selected = viewModel.SelectedAdapterItem;
            if (selected == null)
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
            progress.Report(new SupportBundleProgress(55, "正在检查 DHCP 服务..."));
            await AppendCommandAsync(sb, "DHCP Client service", "sc.exe", "query Dhcp");
            progress.Report(new SupportBundleProgress(70, "正在读取网卡配置..."));
            await AppendCommandAsync(sb, "Network interface configuration", "netsh.exe", "interface ip show config");

            progress.Report(new SupportBundleProgress(75, "正在写入诊断报告..."));
            await Task.Run(() => File.WriteAllText(reportPath, sb.ToString(), Utf8WithBom));
            App.LogSupport("Legacy diagnostics report written: " + reportPath);
            return reportPath;
        }

        private static void AppendHeader(StringBuilder sb, string title)
        {
            sb.AppendLine();
            sb.AppendLine("=== " + title + " ===");
        }

        private static void AppendLine(StringBuilder sb, string key, string value)
        {
            sb.AppendLine(key + ": " + (string.IsNullOrWhiteSpace(value) ? "(empty)" : value));
        }

        private static async Task AppendCommandAsync(StringBuilder sb, string title, string fileName, string arguments)
        {
            AppendHeader(sb, title);
            sb.AppendLine(await RunProcessAsync(fileName, arguments));
        }

        private static async Task<string> RunProcessAsync(string fileName, string arguments)
        {
            try
            {
                using (var process = new Process())
                {
                    var exited = new TaskCompletionSource<bool>();
                    process.StartInfo = new ProcessStartInfo(fileName, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = NativeConsoleEncoding,
                        StandardErrorEncoding = NativeConsoleEncoding
                    };
                    process.EnableRaisingEvents = true;
                    process.Exited += (sender, args) => exited.TrySetResult(true);
                    process.Start();

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var completed = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
                    if (completed != exited.Task)
                    {
                        try { process.Kill(); } catch { }
                        return "Command timed out: " + fileName + " " + arguments;
                    }

                    var stdout = await stdoutTask;
                    var stderr = await stderrTask;
                    var result = new StringBuilder();
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
            }
            catch (Exception ex)
            {
                return "Command failed: " + fileName + " " + arguments + Environment.NewLine + ex;
            }
        }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }
}
