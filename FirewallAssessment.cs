#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    internal enum FirewallRiskLevel
    {
        None,
        Warning,
        High,
        Unknown
    }

    internal sealed class FirewallRuleEvidence
    {
        internal string Name { get; set; } = "";
        internal string DisplayName { get; set; } = "";
        internal string Enabled { get; set; } = "";
        internal string Direction { get; set; } = "";
        internal string Action { get; set; } = "";
        internal string Profile { get; set; } = "";
        internal string Program { get; set; } = "";
        internal string Protocol { get; set; } = "";
        internal string LocalPort { get; set; } = "";
        internal string InterfaceAlias { get; set; } = "";
        internal string PolicyStoreSourceType { get; set; } = "";
        internal string PolicyStoreSource { get; set; } = "";
        internal string Status { get; set; } = "";
    }

    internal sealed class FirewallProfileEvidence
    {
        internal string Name { get; set; } = "";
        internal bool? Enabled { get; set; }
        internal string DefaultInboundAction { get; set; } = "";
    }

    internal sealed class FirewallAssessment
    {
        internal int? InterfaceIndex { get; set; }
        internal string AdapterName { get; set; } = "";
        internal string NetworkName { get; set; } = "";
        internal string NetworkCategory { get; set; } = "Unknown";
        internal string ObservedNetworkCategory { get; private set; } = "Unknown";
        internal string NetworkCategorySource { get; set; } = "Current adapter";
        internal bool UsesNetworkCategorySnapshot { get; private set; }
        internal string IPv4Connectivity { get; set; } = "Unknown";
        internal string IPv6Connectivity { get; set; } = "Unknown";
        internal string ExecutablePath { get; set; } = "";
        internal string Error { get; set; } = "";
        internal string AssessmentSource { get; set; } = "";
        internal List<FirewallProfileEvidence> Profiles { get; } = new List<FirewallProfileEvidence>();
        internal List<FirewallRuleEvidence> Rules { get; } = new List<FirewallRuleEvidence>();
        internal FirewallRiskLevel RiskLevel { get; private set; } = FirewallRiskLevel.Unknown;
        internal bool HasMatchingProgramAllow { get; private set; }
        internal bool HasMatchingProgramBlock { get; private set; }
        internal bool HasMatchingPortAllow { get; private set; }
        internal bool HasMatchingPortBlock { get; private set; }
        internal bool? SelectedFirewallEnabled { get; private set; }
        internal bool RuleCollectionAvailable { get; set; }

        // Unknown must be visible before a network mutation. Hiding it would make an
        // older system look safe even though the assessment could not rule out a block.
        internal bool HasWarning => RiskLevel != FirewallRiskLevel.None;

        internal void ApplyNetworkCategorySnapshot(string snapshotCategory)
        {
            ObservedNetworkCategory = NetworkCategory;
            if (!string.IsNullOrWhiteSpace(NormalizeSelectedProfile(snapshotCategory)))
            {
                NetworkCategory = snapshotCategory;
                NetworkCategorySource = "Captured before adapter configuration";
                UsesNetworkCategorySnapshot = true;
            }
        }

        internal void Evaluate()
        {
            var selectedProfile = NormalizeSelectedProfile(NetworkCategory);
            var profile = Profiles.FirstOrDefault(item =>
                string.Equals(item.Name, selectedProfile, StringComparison.OrdinalIgnoreCase));
            SelectedFirewallEnabled = profile?.Enabled;

            var profileKnown = !string.IsNullOrWhiteSpace(selectedProfile);
            Func<FirewallRuleEvidence, bool> applies;
            if (profileKnown)
                applies = rule => RuleApplies(rule, selectedProfile, AdapterName);
            else
                applies = rule => RuleCouldAffectDhcpWithoutProfile(rule, AdapterName);

            HasMatchingProgramAllow = Rules.Any(rule =>
                applies(rule) &&
                ProgramMatches(rule.Program, ExecutablePath) &&
                IsAllow(rule.Action));
            HasMatchingProgramBlock = Rules.Any(rule =>
                applies(rule) &&
                ProgramMatches(rule.Program, ExecutablePath) &&
                IsBlock(rule.Action));
            HasMatchingPortAllow = Rules.Any(rule =>
                applies(rule) &&
                IsAnyProgram(rule.Program) &&
                IsAllow(rule.Action));
            HasMatchingPortBlock = Rules.Any(rule =>
                applies(rule) &&
                IsAnyProgram(rule.Program) &&
                IsBlock(rule.Action));

            if (SelectedFirewallEnabled == false)
            {
                RiskLevel = FirewallRiskLevel.None;
                return;
            }

            if (!RuleCollectionAvailable)
            {
                RiskLevel = FirewallRiskLevel.Unknown;
                return;
            }

            // With no adapter profile, the matches above are deliberately a
            // cross-profile reference set. A rule from another profile must not
            // be presented as a confirmed block for the selected adapter.
            if (!profileKnown)
            {
                RiskLevel = FirewallRiskLevel.Warning;
                return;
            }

            if (HasMatchingProgramBlock || HasMatchingPortBlock)
            {
                RiskLevel = FirewallRiskLevel.High;
                return;
            }

            // On Windows 7 an APIPA/direct-link adapter may not have a known
            // category even though the native firewall policy and rules are
            // readable. Never label that case safe: a missing Allow remains a
            // visible compatibility warning, while a matching Block is High.
            if (SelectedFirewallEnabled is null)
            {
                RiskLevel = FirewallRiskLevel.Warning;
                return;
            }

            RiskLevel = HasMatchingProgramAllow ? FirewallRiskLevel.None : FirewallRiskLevel.Warning;
        }

        internal string BuildConsentWarning()
        {
            if (!HasWarning)
                return "";

            var context = string.Equals(NetworkCategory, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? "未能确认所选网卡的网络类别；跨网络类型读取到的规则只作为参考，不会据此认定当前网卡被阻止。"
                : "所选网卡网络类别为 " + NetworkCategory + "，对应防火墙已开启。";
            if (RiskLevel == FirewallRiskLevel.High)
            {
                return "防火墙高风险：" + context +
                    "检测到覆盖当前程序或 UDP/67 的显式入站阻止规则。Windows 中显式阻止规则会优先于允许规则，DHCP 请求可能无法到达程序。" +
                    "请先核对并处理与当前 EXE 路径匹配的阻止规则，再重新检测；不要关闭整个防火墙。当前程序：" + ExecutablePath +
                    "。本工具不会自动修改防火墙；本轮结束后可点击“处理防火墙并准备重试”，核对变更并确认处理。";
            }

            if (RiskLevel == FirewallRiskLevel.Unknown)
            {
                return "防火墙状态未能完整检测，不能据此排除 DHCP 被拦截。请由管理员核对当前 EXE 是否在当前网络类型下允许 UDP/67 入站通信；" +
                    "不要关闭整个防火墙。当前程序：" + ExecutablePath + "。本工具不会自动修改防火墙；本轮结束后可在软件内核对并确认处理。";
            }

            var ruleText = string.Equals(NetworkCategory, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? (HasMatchingProgramAllow || HasMatchingProgramBlock || HasMatchingPortAllow || HasMatchingPortBlock
                    ? "发现了可能相关的跨网络类型规则，但无法确认它们是否适用于当前网卡。"
                    : "未发现可确认适用于当前网卡和当前程序路径的 UDP/67 入站规则。")
                : HasMatchingPortAllow
                ? "已发现 UDP/67 端口允许规则，但未发现匹配当前程序路径的入站允许规则；当前机器上的端口规则兼容性仍需确认。"
                : "未发现匹配当前程序路径的、可确认覆盖 UDP/67 的入站允许规则。首次启动 DHCP 服务时若 Windows 弹出防火墙提示，请为当前直连网卡对应的网络类型选择“允许访问”；若提示明确显示“公用网络”，也必须勾选“公用网络”。";
            return "防火墙提醒：" + context + ruleText + " 当前程序：" + ExecutablePath +
                "。本工具不会自动修改防火墙；本轮结束后可点击“处理防火墙并准备重试”。";
        }

        internal string BuildTimeoutGuidance()
        {
            const string fixedIp = "如果 BMC 已配置固定 IP，它不会主动发起新的 DHCP 获取；请先确认 BMC 网络模式和已知地址。";
            const string other = "同时请确认网线连接的是 IPMI/BMC 专用管理口、链路正常，并且 BMC 已启用 DHCP。";
            if (RiskLevel == FirewallRiskLevel.High)
            {
                return "应用层在等待期内未收到可完成地址分配的 DHCP 流程。当前检测到显式入站阻止规则，可能覆盖当前程序或 UDP/67；请先核对 Windows Defender 防火墙中当前 EXE 路径及对应网络类型的允许状态，不要关闭整个防火墙。" +
                    fixedIp + other;
            }

            if (RiskLevel == FirewallRiskLevel.Warning)
            {
                var portOnly = string.Equals(NetworkCategory, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "未能确认所选网卡的网络类别；读取到的跨网络类型规则仅供参考，不能据此认定当前网卡被允许或阻止，也不能据此排除拦截。"
                    : HasMatchingPortAllow
                    ? "虽然存在 UDP/67 端口允许规则，但没有匹配当前 EXE 路径的程序允许规则，仍存在防火墙兼容性风险。"
                    : "当前网卡对应防火墙已开启，且没有检测到匹配当前 EXE 路径的 UDP 入站允许规则，存在防火墙阻断风险。";
                return "应用层在等待期内未收到可完成地址分配的 DHCP 流程。" + portOnly +
                    "请核对“允许应用通过 Windows Defender 防火墙”中的当前程序和网络类型，不要关闭整个防火墙。" + fixedIp + other;
            }

            var assessment = RiskLevel == FirewallRiskLevel.Unknown
                ? "防火墙状态未能完整检测，不能据此排除拦截。"
                : "当前未检测到明显的防火墙阻断证据。";
            return "应用层在等待期内未收到可完成地址分配的 DHCP 流程。" + assessment + fixedIp + other;
        }

        internal string ToDiagnosticText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("InterfaceAlias: " + Empty(AdapterName));
            sb.AppendLine("InterfaceIndex: " + (InterfaceIndex?.ToString() ?? "(unknown)"));
            sb.AppendLine("NetworkName: " + Empty(NetworkName));
            sb.AppendLine("NetworkCategory: " + Empty(NetworkCategory));
            sb.AppendLine("ObservedNetworkCategory: " + Empty(ObservedNetworkCategory));
            sb.AppendLine("NetworkCategorySource: " + Empty(NetworkCategorySource));
            sb.AppendLine("RuleMatchScope: " +
                (string.IsNullOrWhiteSpace(NormalizeSelectedProfile(NetworkCategory))
                    ? "cross-profile reference only"
                    : "selected profile and current executable path"));
            sb.AppendLine("IPv4Connectivity: " + Empty(IPv4Connectivity));
            sb.AppendLine("IPv6Connectivity: " + Empty(IPv6Connectivity));
            sb.AppendLine("ExecutablePath: " + Empty(ExecutablePath));
            sb.AppendLine("AssessmentSource: " + Empty(AssessmentSource));
            sb.AppendLine("SelectedFirewallEnabled: " + FormatNullableBool(SelectedFirewallEnabled));
            sb.AppendLine("RuleCollectionAvailable: " + RuleCollectionAvailable.ToString().ToLowerInvariant());
            sb.AppendLine("RiskLevel: " + RiskLevel);
            sb.AppendLine("MatchingProgramAllow: " + HasMatchingProgramAllow.ToString().ToLowerInvariant());
            sb.AppendLine("MatchingProgramBlock: " + HasMatchingProgramBlock.ToString().ToLowerInvariant());
            sb.AppendLine("MatchingUdp67PortAllow: " + HasMatchingPortAllow.ToString().ToLowerInvariant());
            sb.AppendLine("MatchingUdp67PortBlock: " + HasMatchingPortBlock.ToString().ToLowerInvariant());
            sb.AppendLine("AssessmentError: " + Empty(Error));

            sb.AppendLine();
            sb.AppendLine("--- Firewall profiles (ActiveStore) ---");
            if (Profiles.Count == 0)
                sb.AppendLine("(none or unavailable)");
            foreach (var profile in Profiles)
            {
                sb.AppendLine("Name=" + Empty(profile.Name) +
                    "; Enabled=" + FormatNullableBool(profile.Enabled) +
                    "; DefaultInboundAction=" + Empty(profile.DefaultInboundAction));
            }

            sb.AppendLine();
            sb.AppendLine("--- Relevant inbound firewall rules (ActiveStore) ---");
            if (Rules.Count == 0)
                sb.AppendLine("(none or unavailable)");
            foreach (var rule in Rules)
            {
                sb.AppendLine("Name=" + Empty(rule.Name) +
                    "; DisplayName=" + Empty(rule.DisplayName) +
                    "; Enabled=" + Empty(rule.Enabled) +
                    "; Direction=" + Empty(rule.Direction) +
                    "; Action=" + Empty(rule.Action) +
                    "; Profile=" + Empty(rule.Profile) +
                    "; Program=" + Empty(rule.Program) +
                    "; Protocol=" + Empty(rule.Protocol) +
                    "; LocalPort=" + Empty(rule.LocalPort) +
                    "; InterfaceAlias=" + Empty(rule.InterfaceAlias) +
                    "; PolicyStoreSourceType=" + Empty(rule.PolicyStoreSourceType) +
                    "; PolicyStoreSource=" + Empty(rule.PolicyStoreSource) +
                    "; Status=" + Empty(rule.Status));
            }

            return sb.ToString().TrimEnd();
        }

        private static bool RuleApplies(FirewallRuleEvidence rule, string selectedProfile, string adapterName)
        {
            return IsEnabled(rule.Enabled) && IsInbound(rule.Direction) &&
                ProfileMatches(rule.Profile, selectedProfile) &&
                InterfaceMatches(rule.InterfaceAlias, adapterName) &&
                ProtocolMatches(rule.Protocol) && PortMatches(rule.LocalPort);
        }

        private static bool RuleCouldAffectDhcpWithoutProfile(FirewallRuleEvidence rule, string adapterName)
        {
            return IsEnabled(rule.Enabled) && IsInbound(rule.Direction) &&
                InterfaceMatches(rule.InterfaceAlias, adapterName) &&
                ProtocolMatches(rule.Protocol) && PortMatches(rule.LocalPort);
        }

        private static string NormalizeSelectedProfile(string category)
        {
            if (string.Equals(category, "DomainAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "Domain", StringComparison.OrdinalIgnoreCase))
                return "Domain";
            if (string.Equals(category, "Private", StringComparison.OrdinalIgnoreCase))
                return "Private";
            if (string.Equals(category, "Public", StringComparison.OrdinalIgnoreCase))
                return "Public";
            return "";
        }

        private static bool IsEnabled(string value) =>
            string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) || value == "1";
        private static bool IsInbound(string value) =>
            string.Equals(value, "Inbound", StringComparison.OrdinalIgnoreCase) || value == "1";
        private static bool IsAllow(string value) =>
            string.Equals(value, "Allow", StringComparison.OrdinalIgnoreCase) || value == "1";
        private static bool IsBlock(string value) =>
            string.Equals(value, "Block", StringComparison.OrdinalIgnoreCase) || value == "0";

        private static bool ProfileMatches(string value, string selectedProfile)
        {
            if (string.IsNullOrWhiteSpace(selectedProfile))
                return false;
            if (IsAny(value))
                return true;
            return SplitValues(value).Any(item => string.Equals(item, selectedProfile, StringComparison.OrdinalIgnoreCase));
        }

        private static bool InterfaceMatches(string value, string adapterName)
        {
            if (IsAny(value) || string.IsNullOrWhiteSpace(adapterName))
                return true;
            return SplitValues(value).Any(item => string.Equals(item, adapterName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ProtocolMatches(string value) =>
            IsAny(value) || string.Equals(value, "UDP", StringComparison.OrdinalIgnoreCase) || value == "17";

        private static bool PortMatches(string value)
        {
            if (IsAny(value))
                return true;
            foreach (var item in SplitValues(value))
            {
                if (item == "67")
                    return true;
                var parts = item.Split('-');
                int start;
                int end;
                if (parts.Length == 2 && int.TryParse(parts[0], out start) && int.TryParse(parts[1], out end) &&
                    start <= 67 && end >= 67)
                    return true;
            }
            return false;
        }

        private static bool ProgramMatches(string ruleProgram, string executablePath)
        {
            if (IsAnyProgram(ruleProgram) || string.IsNullOrWhiteSpace(executablePath))
                return false;
            return string.Equals(NormalizePath(ruleProgram), NormalizePath(executablePath), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnyProgram(string value) => IsAny(value);

        private static bool IsAny(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value == "*" ||
                string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "NotConfigured", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitValues(string value)
        {
            return (value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim().Trim('{', '}'));
        }

        private static string NormalizePath(string value)
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"')))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return (value ?? "").Trim().Trim('"');
            }
        }

        private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
        private static string FormatNullableBool(bool? value) => value.HasValue ? value.Value.ToString().ToLowerInvariant() : "unknown";
    }

    internal static class FirewallAssessmentService
    {
        // Leave process-start/cleanup headroom so the complete call stays within the 8-second UI budget.
        private const int TimeoutSeconds = 6;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static string GetCurrentExecutablePath()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return Path.GetFullPath(process.MainModule.FileName);
            }
            catch
            {
                return "(unavailable)";
            }
        }

        internal static async Task<FirewallAssessment> AssessAsync(
            string adapterName,
            string adapterId,
            string adapterMac,
            string executablePath)
        {
            return await AssessAsync(
                adapterName,
                adapterId,
                adapterMac,
                executablePath,
                null,
                CancellationToken.None).ConfigureAwait(false);
        }

        internal static Task<FirewallAssessment> AssessAsync(
            string adapterName,
            string adapterId,
            string adapterMac,
            string executablePath,
            CancellationToken cancellationToken)
        {
            return AssessAsync(adapterName, adapterId, adapterMac, executablePath, null, cancellationToken);
        }

        internal static Task<FirewallAssessment> AssessAsync(
            string adapterName,
            string adapterId,
            string adapterMac,
            string executablePath,
            string preferredNetworkCategory,
            CancellationToken cancellationToken)
        {
            // Adapter enumeration, rule parsing and the COM fallback all
            // contain synchronous Windows calls. Run the assessment boundary
            // on the pool while keeping the ViewModel continuation on WPF.
            return Task.Run(
                () => AssessCoreAsync(adapterName, adapterId, adapterMac, executablePath, preferredNetworkCategory, cancellationToken),
                cancellationToken);
        }

        private static async Task<FirewallAssessment> AssessCoreAsync(
            string adapterName,
            string adapterId,
            string adapterMac,
            string executablePath,
            string preferredNetworkCategory,
            CancellationToken cancellationToken)
        {
            var assessment = new FirewallAssessment
            {
                AdapterName = adapterName ?? "",
                ExecutablePath = executablePath ?? "",
                AssessmentSource = "PowerShell NetSecurity"
            };

            cancellationToken.ThrowIfCancellationRequested();
            assessment.InterfaceIndex = ResolveInterfaceIndex(adapterId, adapterMac);
            cancellationToken.ThrowIfCancellationRequested();
            if (!assessment.InterfaceIndex.HasValue)
            {
                assessment.Error = "Unable to resolve the selected adapter IPv4 interface index.";
                assessment.Evaluate();
                return assessment;
            }

            try
            {
                var output = await RunPowerShellAsync(
                    assessment.InterfaceIndex.Value,
                    adapterName,
                    executablePath,
                    cancellationToken).ConfigureAwait(false);
                ParseOutput(output, assessment);
            }
            catch (Exception ex)
            {
                assessment.Error = ex.Message;
            }

            if (NeedsNativeFallback(assessment))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string nativeError;
                if (TryCollectNativeFirewallEvidence(assessment, out nativeError))
                {
                    assessment.AssessmentSource = "Windows Firewall COM fallback";
                    assessment.Error = nativeError;
                }
                else
                {
                    assessment.Error = JoinErrors(assessment.Error, nativeError);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            assessment.ApplyNetworkCategorySnapshot(preferredNetworkCategory);
            assessment.Evaluate();
            return assessment;
        }

        internal static FirewallAssessment CreateForTests(
            string adapterName,
            string networkCategory,
            bool? firewallEnabled,
            string executablePath,
            IEnumerable<FirewallRuleEvidence> rules)
        {
            var assessment = new FirewallAssessment
            {
                AdapterName = adapterName,
                NetworkCategory = networkCategory,
                ExecutablePath = executablePath,
                AssessmentSource = "Synthetic test evidence"
            };
            assessment.ApplyNetworkCategorySnapshot(null);
            assessment.Profiles.Add(new FirewallProfileEvidence
            {
                Name = networkCategory == "DomainAuthenticated" ? "Domain" : networkCategory,
                Enabled = firewallEnabled,
                DefaultInboundAction = "Block"
            });
            assessment.Rules.AddRange(rules ?? Enumerable.Empty<FirewallRuleEvidence>());
            assessment.RuleCollectionAvailable = true;
            assessment.Evaluate();
            return assessment;
        }

        internal static FirewallAssessment CreateUnknown(string executablePath, string error)
        {
            var assessment = new FirewallAssessment
            {
                ExecutablePath = executablePath ?? "",
                Error = error ?? "Firewall assessment is unavailable.",
                AssessmentSource = "Unavailable"
            };
            assessment.Evaluate();
            return assessment;
        }

        private static int? ResolveInterfaceIndex(string adapterId, string adapterMac)
        {
            try
            {
                var normalizedId = NormalizeId(adapterId);
                var normalizedMac = NormalizeMac(adapterMac);
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                    NormalizeId(item.Id) == normalizedId ||
                    (!string.IsNullOrWhiteSpace(normalizedMac) && NormalizeMac(item.GetPhysicalAddress().ToString()) == normalizedMac));
                return adapter?.GetIPProperties().GetIPv4Properties()?.Index;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> RunPowerShellAsync(
            int interfaceIndex,
            string adapterName,
            string executablePath,
            CancellationToken cancellationToken)
        {
            var script = BuildPowerShellScript();
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using (var process = new Process())
            {
                var exited = new TaskCompletionSource<bool>();
                process.StartInfo = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encoded)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom
                };
                process.StartInfo.EnvironmentVariables["EZGET_INTERFACE_INDEX"] = interfaceIndex.ToString();
                process.StartInfo.EnvironmentVariables["EZGET_ADAPTER_NAME"] = adapterName ?? "";
                process.StartInfo.EnvironmentVariables["EZGET_EXECUTABLE_PATH"] = executablePath ?? "";
                process.EnableRaisingEvents = true;
                process.Exited += (sender, args) => exited.TrySetResult(true);
                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var completed = await Task.WhenAny(
                    exited.Task,
                    Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken)).ConfigureAwait(false);
                if (completed != exited.Task)
                {
                    try { process.Kill(); } catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Firewall assessment timed out after " + TimeoutSeconds + " seconds.");
                }

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                    throw new InvalidOperationException("Firewall assessment failed: " + stderr.Trim());
                return stdout;
            }
        }

        private static void ParseOutput(string output, FirewallAssessment assessment)
        {
            var errors = new List<string>();
            foreach (var rawLine in (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rawLine.Trim().Split('|');
                if (parts.Length < 2)
                    continue;
                var kind = parts[0];
                try
                {
                    if (kind == "VALUE" && parts.Length >= 3)
                    {
                        ApplyValue(Decode(parts[1]), Decode(parts[2]), assessment);
                    }
                    else if (kind == "PROFILE" && parts.Length >= 4)
                    {
                        assessment.Profiles.Add(new FirewallProfileEvidence
                        {
                            Name = Decode(parts[1]),
                            Enabled = ParseNullableBool(Decode(parts[2])),
                            DefaultInboundAction = Decode(parts[3])
                        });
                    }
                    else if (kind == "RULE" && parts.Length >= 14)
                    {
                        assessment.Rules.Add(new FirewallRuleEvidence
                        {
                            Name = Decode(parts[1]),
                            DisplayName = Decode(parts[2]),
                            Enabled = Decode(parts[3]),
                            Direction = Decode(parts[4]),
                            Action = Decode(parts[5]),
                            Profile = Decode(parts[6]),
                            Program = Decode(parts[7]),
                            Protocol = Decode(parts[8]),
                            LocalPort = Decode(parts[9]),
                            InterfaceAlias = Decode(parts[10]),
                            PolicyStoreSourceType = Decode(parts[11]),
                            PolicyStoreSource = Decode(parts[12]),
                            Status = Decode(parts[13])
                        });
                    }
                    else if (kind == "ERROR")
                    {
                        errors.Add(Decode(parts[1]));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Unable to parse firewall assessment output: " + ex.Message);
                }
            }

            if (errors.Count > 0)
                assessment.Error = string.Join(" | ", errors);
            if (assessment.Profiles.Count == 0 && string.IsNullOrWhiteSpace(assessment.Error))
                assessment.Error = "Firewall profile data was not returned.";
        }

        private static void ApplyValue(string key, string value, FirewallAssessment assessment)
        {
            switch (key)
            {
                case "NetworkName": assessment.NetworkName = value; break;
                case "NetworkCategory":
                    assessment.NetworkCategory = value;
                    assessment.NetworkCategorySource = "Selected adapter (Get-NetConnectionProfile)";
                    break;
                case "IPv4Connectivity": assessment.IPv4Connectivity = value; break;
                case "IPv6Connectivity": assessment.IPv6Connectivity = value; break;
                case "FirewallRulesComplete": assessment.RuleCollectionAvailable =
                    string.Equals(value, "True", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static bool? ParseNullableBool(string value)
        {
            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : (bool?)null;
        }

        private static string BuildPowerShellScript()
        {
            return @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
function Encode-Value([object]$value) {
    if ($null -eq $value) { $value = '' }
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$value))
}
function Write-Value([string]$key, [object]$value) {
    Write-Output ('VALUE|' + (Encode-Value $key) + '|' + (Encode-Value $value))
}
function Write-ErrorValue([object]$value) {
    Write-Output ('ERROR|' + (Encode-Value $value))
}
$interfaceIndex = [int][Environment]::GetEnvironmentVariable('EZGET_INTERFACE_INDEX')
$adapterName = [Environment]::GetEnvironmentVariable('EZGET_ADAPTER_NAME')
$exePath = [Environment]::GetEnvironmentVariable('EZGET_EXECUTABLE_PATH')
try {
    if (-not (Get-Command Get-NetConnectionProfile -ErrorAction SilentlyContinue)) { throw 'Get-NetConnectionProfile is unavailable.' }
    $network = Get-NetConnectionProfile -InterfaceIndex $interfaceIndex -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $network) { throw 'No network profile exists for the selected adapter.' }
    Write-Value 'NetworkName' $network.Name
    Write-Value 'NetworkCategory' $network.NetworkCategory
    Write-Value 'IPv4Connectivity' $network.IPv4Connectivity
    Write-Value 'IPv6Connectivity' $network.IPv6Connectivity
} catch { Write-ErrorValue ('Network profile: ' + $_.Exception.Message) }
try {
    if (-not (Get-Command Get-NetFirewallProfile -ErrorAction SilentlyContinue)) { throw 'Get-NetFirewallProfile is unavailable.' }
    foreach ($profile in (Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)) {
        Write-Output ('PROFILE|' + (Encode-Value $profile.Name) + '|' + (Encode-Value $profile.Enabled) + '|' + (Encode-Value $profile.DefaultInboundAction))
    }
} catch { Write-ErrorValue ('Firewall profiles: ' + $_.Exception.Message) }
try {
    if (-not (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) { throw 'Get-NetFirewallRule is unavailable.' }
    $candidateRules = @{}
    foreach ($appFilter in @(Get-NetFirewallApplicationFilter -PolicyStore ActiveStore -Program $exePath -ErrorAction SilentlyContinue)) {
        $programValue = [string]$appFilter.Program
        foreach ($candidate in @($appFilter | Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
            if ($null -ne $candidate -and $candidate.Direction -eq 'Inbound') { $candidateRules[[string]$candidate.Name] = $candidate }
        }
    }
    $portFilters = @(Get-NetFirewallPortFilter -PolicyStore ActiveStore -Protocol UDP -ErrorAction Stop)
    foreach ($portFilter in $portFilters) {
        $protocolValue = [string]$portFilter.Protocol
        $localPortValue = [string]($portFilter.LocalPort -join ',')
        $isUdpProtocol = $protocolValue -eq 'UDP' -or $protocolValue -eq '17'
        $contains67 = $false
        foreach ($portPart in ($localPortValue -split ',')) {
            $trimmedPort = $portPart.Trim()
            if ($trimmedPort -eq '67') { $contains67 = $true; break }
            if ($trimmedPort -match '^(\d+)-(\d+)$' -and [int]$Matches[1] -le 67 -and [int]$Matches[2] -ge 67) { $contains67 = $true; break }
        }
        if (-not ($isUdpProtocol -and $contains67)) { continue }
        foreach ($candidate in @($portFilter | Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
            if ($null -ne $candidate -and $candidate.Direction -eq 'Inbound') { $candidateRules[[string]$candidate.Name] = $candidate }
        }
    }
    foreach ($candidate in @(Get-NetFirewallRule -PolicyStore ActiveStore -DisplayName '*ezget*' -ErrorAction SilentlyContinue)) {
        if ($null -ne $candidate -and $candidate.Direction -eq 'Inbound') { $candidateRules[[string]$candidate.Name] = $candidate }
    }
    foreach ($rule in $candidateRules.Values) {
        $app = $rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
        $port = $rule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
        $iface = $rule | Get-NetFirewallInterfaceFilter -ErrorAction SilentlyContinue
        $program = [string]$app.Program
        $protocol = [string]$port.Protocol
        $localPort = [string]($port.LocalPort -join ',')
        $interfaceAlias = [string]($iface.InterfaceAlias -join ',')
        $values = @($rule.Name,$rule.DisplayName,$rule.Enabled,$rule.Direction,$rule.Action,$rule.Profile,$program,$protocol,$localPort,$interfaceAlias,$rule.PolicyStoreSourceType,$rule.PolicyStoreSource,$rule.Status)
        Write-Output ('RULE|' + (($values | ForEach-Object { Encode-Value $_ }) -join '|'))
    }
    Write-Value 'FirewallRulesComplete' 'True'
} catch { Write-ErrorValue ('Firewall rules: ' + $_.Exception.Message) }
";
        }

        private static bool NeedsNativeFallback(FirewallAssessment assessment)
        {
            return !assessment.RuleCollectionAvailable || assessment.Profiles.Count == 0 ||
                !IsKnownNetworkCategory(assessment.NetworkCategory);
        }

        // Windows 7 does not ship the NetSecurity PowerShell module used above.
        // HNetCfg.FwPolicy2 is a native Windows COM API available there, so use it
        // only as a read-only fallback.
        private static bool TryCollectNativeFirewallEvidence(
            FirewallAssessment assessment,
            out string error)
        {
            var errors = new List<string>();
            try
            {
                if (!assessment.RuleCollectionAvailable)
                    assessment.Rules.Clear();

                var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                {
                    error = "Windows Firewall COM API HNetCfg.FwPolicy2 is unavailable.";
                    return false;
                }

                var policy = Activator.CreateInstance(policyType);
                var currentProfiles = ToInt(ReadComMember(policy, "CurrentProfileTypes"));
                var category = SingleProfileName(currentProfiles);
                if (!IsKnownNetworkCategory(assessment.NetworkCategory) && IsKnownNetworkCategory(category))
                {
                    assessment.NetworkCategory = category;
                    assessment.NetworkCategorySource = "Active system profiles (COM fallback)";
                }
                else if (!IsKnownNetworkCategory(assessment.NetworkCategory))
                    errors.Add("The selected adapter firewall profile could not be determined; relevant rules were still collected.");

                AddNativeProfileEvidence(policy, assessment);

                var rules = ReadComMember(policy, "Rules");
                if (rules == null)
                {
                    errors.Add("Windows Firewall COM API did not return the rule collection.");
                }
                else
                {
                    foreach (var rule in EnumerateComItems(rules))
                    {
                        var evidence = CreateNativeRuleEvidence(rule);
                        if (IsRelevantNativeRule(evidence, assessment.ExecutablePath))
                            assessment.Rules.Add(evidence);
                    }
                    assessment.RuleCollectionAvailable = true;
                }

                error = string.Join(" | ", errors.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
                return assessment.RuleCollectionAvailable;
            }
            catch (Exception ex)
            {
                error = "Windows Firewall COM fallback failed: " + ex.Message;
                return false;
            }
        }

        private static void AddNativeProfileEvidence(object policy, FirewallAssessment assessment)
        {
            AddNativeProfileEvidence(policy, assessment, 1, "Domain");
            AddNativeProfileEvidence(policy, assessment, 2, "Private");
            AddNativeProfileEvidence(policy, assessment, 4, "Public");
        }

        private static void AddNativeProfileEvidence(
            object policy,
            FirewallAssessment assessment,
            int profileMask,
            string profileName)
        {
            if (assessment.Profiles.Any(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase)))
                return;

            assessment.Profiles.Add(new FirewallProfileEvidence
            {
                Name = profileName,
                Enabled = ToNullableBool(ReadIndexedComMember(policy, "FirewallEnabled", profileMask)),
                DefaultInboundAction = ToFirewallAction(ReadIndexedComMember(policy, "DefaultInboundAction", profileMask))
            });
        }

        private static FirewallRuleEvidence CreateNativeRuleEvidence(object rule)
        {
            var direction = ToInt(ReadComMember(rule, "Direction"));
            var action = ToInt(ReadComMember(rule, "Action"));
            var protocol = ToInt(ReadComMember(rule, "Protocol"));
            var profiles = ToInt(ReadComMember(rule, "Profiles"));
            return new FirewallRuleEvidence
            {
                Name = ToText(ReadComMember(rule, "Name")),
                DisplayName = ToText(ReadComMember(rule, "Name")),
                Enabled = ToText(ReadComMember(rule, "Enabled")),
                Direction = direction == 1 ? "Inbound" : direction == 2 ? "Outbound" : ToText(direction),
                Action = ToFirewallAction(action),
                Profile = ProfileMaskToText(profiles),
                Program = ToTextOrAny(ReadComMember(rule, "ApplicationName")),
                Protocol = ProtocolToText(protocol),
                LocalPort = ToTextOrAny(ReadComMember(rule, "LocalPorts")),
                InterfaceAlias = ToCommaSeparatedText(ReadComMember(rule, "Interfaces")),
                PolicyStoreSourceType = "NativeCom",
                PolicyStoreSource = "ActivePolicy",
                Status = "OK"
            };
        }

        private static bool IsRelevantNativeRule(FirewallRuleEvidence rule, string executablePath)
        {
            var programMatches = PathsMatch(rule.Program, executablePath);
            var globalUdp67 = IsAnyProgramValue(rule.Program) &&
                (rule.Protocol == "17" || string.Equals(rule.Protocol, "Any", StringComparison.OrdinalIgnoreCase)) &&
                PortContains67(rule.LocalPort);
            var namedForTool = rule.Name.IndexOf("ezget", StringComparison.OrdinalIgnoreCase) >= 0;
            return programMatches || globalUdp67 || namedForTool;
        }

        private static object ReadComMember(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                return instance.GetType().InvokeMember(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty,
                    null,
                    instance,
                    null);
            }
            catch
            {
                try
                {
                    return instance.GetType().InvokeMember(
                        name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod,
                        null,
                        instance,
                        null);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static object ReadIndexedComMember(object instance, string name, int index)
        {
            if (instance == null)
                return null;

            var arguments = new object[] { index };
            try
            {
                return instance.GetType().InvokeMember(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty,
                    null,
                    instance,
                    arguments);
            }
            catch
            {
                try
                {
                    return instance.GetType().InvokeMember(
                        "get_" + name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod,
                        null,
                        instance,
                        arguments);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static IEnumerable<object> EnumerateComItems(object collection)
        {
            if (collection == null)
                yield break;

            var enumerable = collection as IEnumerable;
            if (enumerable == null)
            {
                var newEnum = ReadComMember(collection, "_NewEnum");
                enumerable = newEnum as IEnumerable;
            }

            if (enumerable == null)
                yield break;

            foreach (var item in enumerable)
                yield return item;
        }

        private static string ToText(object value)
        {
            return value == null ? "" : Convert.ToString(value) ?? "";
        }

        private static string ToTextOrAny(object value)
        {
            var text = ToText(value).Trim();
            return string.IsNullOrWhiteSpace(text) ? "*" : text;
        }

        private static string ToCommaSeparatedText(object value)
        {
            if (value == null)
                return "*";

            var text = value as string;
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            var enumerable = value as IEnumerable;
            if (enumerable == null)
                return "*";

            var values = new List<string>();
            foreach (var item in enumerable)
            {
                var itemText = ToText(item).Trim();
                if (!string.IsNullOrWhiteSpace(itemText))
                    values.Add(itemText);
            }
            return values.Count == 0 ? "*" : string.Join(",", values);
        }

        private static int ToInt(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static bool? ToNullableBool(object value)
        {
            if (value == null)
                return null;
            try { return Convert.ToBoolean(value); }
            catch { return null; }
        }

        private static string ToFirewallAction(object value)
        {
            if (value == null)
                return "";
            var action = ToInt(value);
            return action == 0 ? "Block" : action == 1 ? "Allow" : ToText(value);
        }

        private static string ProtocolToText(int protocol)
        {
            return protocol == 256 ? "Any" : protocol.ToString();
        }

        private static string ProfileMaskToText(int profileMask)
        {
            if (profileMask == 0 || profileMask == -1 || profileMask == int.MaxValue)
                return "*";

            var profiles = new List<string>();
            if ((profileMask & 1) != 0) profiles.Add("Domain");
            if ((profileMask & 2) != 0) profiles.Add("Private");
            if ((profileMask & 4) != 0) profiles.Add("Public");
            return profiles.Count == 0 ? profileMask.ToString() : string.Join(",", profiles);
        }

        private static string SingleProfileName(int profileMask)
        {
            var profileText = ProfileMaskToText(profileMask);
            return profileText.IndexOf(',') >= 0 || profileText == "*" ? "Unknown" : profileText;
        }

        private static bool IsKnownNetworkCategory(string category)
        {
            return string.Equals(category, "Public", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "Private", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "DomainAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "Domain", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnyProgramValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value == "*" ||
                string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "NotConfigured", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PortContains67(string value)
        {
            if (IsAnyProgramValue(value))
                return true;

            foreach (var item in (value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var port = item.Trim();
                if (port == "67")
                    return true;
                var parts = port.Split('-');
                int start;
                int end;
                if (parts.Length == 2 && int.TryParse(parts[0], out start) && int.TryParse(parts[1], out end) &&
                    start <= 67 && end >= 67)
                    return true;
            }
            return false;
        }

        private static bool PathsMatch(string left, string right)
        {
            if (IsAnyProgramValue(left) || string.IsNullOrWhiteSpace(right))
                return false;
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string value)
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"')))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return (value ?? "").Trim().Trim('"');
            }
        }

        private static string JoinErrors(params string[] values)
        {
            return string.Join(" | ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        }

        private static string NormalizeId(string value) => (value ?? "").Trim().Trim('{', '}').ToUpperInvariant();
        private static string NormalizeMac(string value) => new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }
}
