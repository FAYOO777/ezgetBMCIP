#nullable enable

using System;

namespace EzGetBmcIp
{
    // UI-facing classifications only. These values do not control discovery,
    // recovery, or command execution; CurrentSessionState remains the source of
    // all business facts.
    public enum SessionPageKind
    {
        Ready,
        WaitingForLink,
        ConfiguringNetwork,
        WaitingForDhcp,
        ProbingEndpoint,
        EndpointReachable,
        EndpointUnreachable,
        DhcpTimedOut,
        HistoryRetrySuggestion,
        Failure,
        Cancelled,
        Restoring,
        RestoreFailed,
        FirewallRepairing
    }

    public enum PresentationSeverity
    {
        Normal,
        Progress,
        Warning,
        Error,
        Success
    }

    // A recommendation is not a command. In particular, None means that a
    // waiting state must be displayed as status rather than a clickable action.
    public enum PageActionKind
    {
        None,
        RetryEndpointProbe,
        OpenManagementPage,
        UseHistorySubnet,
        RetryRestore,
        ExportSupportBundle
    }

    public sealed class SessionPagePresentation
    {
        public SessionPagePresentation(
            SessionPageKind page,
            string title,
            string summary,
            PageActionKind recommendedActionKind)
        {
            Page = page;
            Title = title;
            Summary = summary;
            RecommendedActionKind = recommendedActionKind;
        }

        public SessionPageKind Page { get; private set; }
        public string Title { get; private set; }
        public string Summary { get; private set; }
        public PageActionKind RecommendedActionKind { get; private set; }
    }

    public sealed class NetworkStatusPresentation
    {
        public NetworkStatusPresentation(string text, PresentationSeverity severity)
        {
            Text = text;
            Severity = severity;
        }

        public string Text { get; private set; }
        public PresentationSeverity Severity { get; private set; }
    }

    public sealed class FailurePresentation
    {
        public FailurePresentation(
            string title,
            string summary,
            PageActionKind recommendedActionKind,
            string recommendedActionText)
        {
            Title = title;
            Summary = summary;
            RecommendedActionKind = recommendedActionKind;
            RecommendedActionText = recommendedActionText;
        }

        public string Title { get; private set; }
        public string Summary { get; private set; }
        public PageActionKind RecommendedActionKind { get; private set; }
        public string RecommendedActionText { get; private set; }
    }

    public sealed class BrowserStatusPresentation
    {
        public BrowserStatusPresentation(string text, PresentationSeverity severity)
        {
            Text = text;
            Severity = severity;
        }

        public string Text { get; private set; }
        public PresentationSeverity Severity { get; private set; }
    }

    public sealed class DhcpEvidencePresentation
    {
        public DhcpEvidencePresentation(string text, PresentationSeverity severity)
        {
            Text = text;
            Severity = severity;
        }

        public string Text { get; private set; }
        public PresentationSeverity Severity { get; private set; }
    }

    // This type is internal because FirewallRiskLevel is part of the local
    // assessment implementation rather than the shared session contract.
    internal sealed class FirewallPresentation
    {
        internal FirewallPresentation(PresentationSeverity severity, string summary)
        {
            Severity = severity;
            Summary = summary;
        }

        internal PresentationSeverity Severity { get; private set; }
        internal string Summary { get; private set; }
    }

    // This class is intentionally pure: it reads facts and produces text only.
    // It is linked into both WPF applications and has no framework or network dependency.
    public static class SessionPresentation
    {
        public static SessionPageKind ResolveSessionPage(
            CurrentSessionState sessionState,
            bool hasHistoryRetrySuggestion)
        {
            if (sessionState == null)
                throw new ArgumentNullException(nameof(sessionState));

            // Network recovery is more urgent than any completed discovery result.
            if (sessionState.Network == NetworkLifecycleState.RestoreFailed)
                return SessionPageKind.RestoreFailed;

            if (sessionState.Network == NetworkLifecycleState.Restoring)
                return SessionPageKind.Restoring;

            switch (sessionState.Workflow)
            {
                case DiscoveryWorkflowState.EndpointReachable:
                    return SessionPageKind.EndpointReachable;
                case DiscoveryWorkflowState.EndpointUnreachable:
                    return SessionPageKind.EndpointUnreachable;
                case DiscoveryWorkflowState.Failed:
                    if (sessionState.Failure != null && sessionState.Failure.Kind == FailureKind.DhcpTimedOut)
                        return hasHistoryRetrySuggestion
                            ? SessionPageKind.HistoryRetrySuggestion
                            : SessionPageKind.DhcpTimedOut;
                    return SessionPageKind.Failure;
                case DiscoveryWorkflowState.Cancelled:
                    return SessionPageKind.Cancelled;
                case DiscoveryWorkflowState.WaitingForLink:
                    return SessionPageKind.WaitingForLink;
                case DiscoveryWorkflowState.ConfiguringNetwork:
                    return SessionPageKind.ConfiguringNetwork;
                case DiscoveryWorkflowState.WaitingForDhcp:
                    return SessionPageKind.WaitingForDhcp;
                case DiscoveryWorkflowState.ProbingEndpoint:
                    return SessionPageKind.ProbingEndpoint;
                case DiscoveryWorkflowState.Ready:
                default:
                    return SessionPageKind.Ready;
            }
        }

        public static SessionPagePresentation GetSessionPagePresentation(
            CurrentSessionState sessionState,
            bool hasHistoryRetrySuggestion,
            string? preferredBmcScheme)
        {
            var page = ResolveSessionPage(sessionState, hasHistoryRetrySuggestion);
            switch (page)
            {
                case SessionPageKind.WaitingForLink:
                    return Page(page, "正在等待网线连接", "尚未修改本机网卡，正在等待所选网卡建立物理连接。", PageActionKind.None);
                case SessionPageKind.ConfiguringNetwork:
                    return Page(page, "正在准备临时直连网络", GetConfiguringNetworkSummary(sessionState.Network), PageActionKind.None);
                case SessionPageKind.WaitingForDhcp:
                    return Page(page, "正在等待设备请求 DHCP 地址", "本机网卡已切换到临时直连配置，正在等待设备发起 DHCP。", PageActionKind.None);
                case SessionPageKind.ProbingEndpoint:
                    return Page(page, "正在确认地址可达性", "已取得候选管理地址，正在并行检测 Ping、TCP 443 和 TCP 80；不读取网页内容。", PageActionKind.None);
                case SessionPageKind.EndpointReachable:
                    return Page(page, "候选管理地址已确认可达", GetEndpointReachableSummary(preferredBmcScheme), PageActionKind.OpenManagementPage);
                case SessionPageKind.EndpointUnreachable:
                    return Page(page, "候选管理地址尚未确认可达", "已取得候选管理地址，但暂未收到 Ping 或 TCP 80/443 的成功响应；地址仍会保留。", PageActionKind.RetryEndpointProbe);
                case SessionPageKind.DhcpTimedOut:
                    return Page(page, "等待 DHCP 地址分配超时", "在 3 分钟内未观察到完成地址分配的 DHCP 流程。", PageActionKind.ExportSupportBundle);
                case SessionPageKind.HistoryRetrySuggestion:
                    return Page(page, "可尝试上次使用的网段", "本次等待 DHCP 已超时；所选本机网卡曾在下列网段确认过地址可达。", PageActionKind.UseHistorySubnet);
                case SessionPageKind.Cancelled:
                    return Page(page, "已停止当前流程", GetCancelledSummary(sessionState.Network), PageActionKind.None);
                case SessionPageKind.Restoring:
                    return Page(page, "正在恢复本机网卡", "正在恢复使用工具前的网络配置，请等待完成。", PageActionKind.None);
                case SessionPageKind.RestoreFailed:
                    return Page(page, "本机网卡尚未确认恢复", "工具无法确认所选网卡已恢复到使用前配置。", PageActionKind.RetryRestore);
                case SessionPageKind.Failure:
                    var failure = GetFailurePresentation(sessionState.Failure);
                    return Page(page, failure.Title, failure.Summary, failure.RecommendedActionKind);
                case SessionPageKind.Ready:
                default:
                    return Page(SessionPageKind.Ready, "准备开始", "选择直连服务器管理口的有线网卡后开始。", PageActionKind.None);
            }
        }

        public static NetworkStatusPresentation GetNetworkStatusPresentation(
            NetworkLifecycleState network,
            string? adapterDisplayName)
        {
            switch (network)
            {
                case NetworkLifecycleState.RecoveryPrepared:
                    return new NetworkStatusPresentation("本机网卡：恢复保护已就绪，尚未修改网络", PresentationSeverity.Progress);
                case NetworkLifecycleState.TemporaryConfigurationMayBeActive:
                    return new NetworkStatusPresentation("本机网卡：可能已开始切换为临时配置；退出时将恢复", PresentationSeverity.Warning);
                case NetworkLifecycleState.TemporaryConfigurationActive:
                    return new NetworkStatusPresentation("本机网卡：正在使用临时直连配置", PresentationSeverity.Warning);
                case NetworkLifecycleState.Restoring:
                    return new NetworkStatusPresentation(
                        string.IsNullOrWhiteSpace(adapterDisplayName)
                            ? "本机网卡：正在恢复使用工具前的配置"
                            : "本机网卡：正在恢复“" + adapterDisplayName + "”的原配置",
                        PresentationSeverity.Progress);
                case NetworkLifecycleState.ConfigurationRestored:
                    return new NetworkStatusPresentation("本机网卡：已恢复使用工具前的配置", PresentationSeverity.Success);
                case NetworkLifecycleState.RestoreFailed:
                    return new NetworkStatusPresentation("本机网卡：尚未确认恢复，请先处理此问题", PresentationSeverity.Error);
                case NetworkLifecycleState.Untouched:
                default:
                    return new NetworkStatusPresentation("本机网卡：尚未修改", PresentationSeverity.Normal);
            }
        }

        public static string GetExitActionText(ExitIntent exitIntent)
        {
            switch (exitIntent)
            {
                case ExitIntent.StopAndExit:
                    return "停止并退出";
                case ExitIntent.CleanupAndExit:
                    return "清理并退出";
                case ExitIntent.StopAndRestore:
                    return "停止并恢复网卡";
                case ExitIntent.RestoreAndExit:
                    return "恢复网卡并退出";
                case ExitIntent.RetryRestore:
                    return "重试恢复网卡";
                case ExitIntent.WaitForRestore:
                    return "正在恢复网卡…";
                case ExitIntent.Exit:
                default:
                    return "退出";
            }
        }

        public static bool IsExitActionEnabled(ExitIntent exitIntent)
        {
            return exitIntent != ExitIntent.WaitForRestore;
        }

        public static BrowserStatusPresentation GetBrowserStatusPresentation(BrowserLaunchResult browserLaunchResult)
        {
            switch (browserLaunchResult)
            {
                case BrowserLaunchResult.Requested:
                    return new BrowserStatusPresentation("已请求系统打开默认浏览器；未验证网页是否加载。若浏览器打不开该地址，请检查代理/VPN 或为本次直连网段设置绕过。", PresentationSeverity.Normal);
                case BrowserLaunchResult.RequestFailed:
                    return new BrowserStatusPresentation("未能请求系统打开浏览器；地址可达结论不受影响。", PresentationSeverity.Warning);
                case BrowserLaunchResult.NotRequested:
                default:
                    return new BrowserStatusPresentation("尚未请求打开浏览器。", PresentationSeverity.Normal);
            }
        }

        public static DhcpEvidencePresentation GetDhcpEvidencePresentation(DhcpEvidenceState dhcpEvidence)
        {
            switch (dhcpEvidence)
            {
                case DhcpEvidenceState.AckSent:
                    return new DhcpEvidencePresentation(
                        "已发送候选地址，正在确认地址可达性…",
                        PresentationSeverity.Progress);
                case DhcpEvidenceState.None:
                default:
                    return new DhcpEvidencePresentation(
                        "正在等待地址分配流程完成…",
                        PresentationSeverity.Progress);
            }
        }

        public static string GetEndpointProtocolPortText(string? preferredBmcScheme)
        {
            return string.Equals(preferredBmcScheme, "http", StringComparison.OrdinalIgnoreCase)
                ? "HTTP · 80"
                : "HTTPS · 443";
        }

        public static string GetCandidateSourceText(CandidateAddressInfo? candidateAddress)
        {
            if (candidateAddress == null)
                return string.Empty;

            switch (candidateAddress.Source)
            {
                case CandidateAddressSource.ExistingConfiguredAddress:
                    return "来源：在预期地址发现的候选地址";
                case CandidateAddressSource.DhcpAck:
                default:
                    return "来源：已发送 DHCP ACK 的候选地址";
            }
        }

        public static FailurePresentation GetFailurePresentation(FailureInfo? failure)
        {
            if (failure == null || failure.Kind == FailureKind.None)
                return new FailurePresentation(
                    "当前操作未能完成",
                    "当前状态无法完整识别，请导出支持包并安全退出。",
                    PageActionKind.ExportSupportBundle,
                    "导出支持包并安全退出。");

            switch (failure.Kind)
            {
                case FailureKind.InitializationFailed:
                    return new FailurePresentation("初始化未完成", "无法完成启动所需的环境检查。", PageActionKind.ExportSupportBundle, "请重新启动工具；如仍失败，请导出支持包。");
                case FailureKind.PendingRecoveryFailed:
                    return new FailurePresentation("上次恢复记录未能处理", "启动时检测到上次会话遗留的恢复记录，但本次未能完成处理。", PageActionKind.RetryRestore, "请先重试恢复本机网卡。");
                case FailureKind.AdapterUnavailable:
                    return new FailurePresentation("未找到可用有线网卡", "未检测到可用于直连管理口的有线网卡。", PageActionKind.ExportSupportBundle, "请检查有线网卡或驱动后重新启动工具。");
                case FailureKind.LinkCheckFailed:
                    return new FailurePresentation("无法检查网线连接", "无法完成所选网卡的物理链路检测。", PageActionKind.ExportSupportBundle, "请检查网卡、网线和设备连接后重试。");
                case FailureKind.OriginalConfigurationCaptureFailed:
                    return new FailurePresentation("无法读取网卡原始配置", "为避免覆盖现有网络设置，工具没有继续修改网卡。", PageActionKind.ExportSupportBundle, "检查网卡配置后重新启动工具；如仍失败，请导出支持包。");
                case FailureKind.TemporaryNetworkConfigurationFailed:
                    return new FailurePresentation("临时直连配置未能完成", "网卡可能已开始变化，需要先恢复。", PageActionKind.None, "请先使用底部退出操作恢复本机网卡。");
                case FailureKind.DhcpListenerFailed:
                    return new FailurePresentation("DHCP 服务未能启动", "临时网络可能已经生效，但本机 DHCP 服务未能正常运行。", PageActionKind.ExportSupportBundle, "导出支持包，然后使用底部退出操作恢复本机网卡。");
                case FailureKind.DhcpTimedOut:
                    return new FailurePresentation("等待 DHCP 地址分配超时", "在 3 分钟内未观察到完成地址分配的 DHCP 流程。", PageActionKind.ExportSupportBundle, "检查当前连接与管理口网络设置，或导出支持包。");
                case FailureKind.EndpointProbeError:
                    return new FailurePresentation("地址可达性检测未能完成", "执行 Ping/TCP 80/443 可达性检测时出现异常。", PageActionKind.RetryEndpointProbe, "如已取得候选地址，可重新检测；否则请导出支持包。");
                case FailureKind.EndpointAdapterUnavailable:
                    return new FailurePresentation("暂时无法确认直连网卡", "DHCP 已取得候选管理地址，但 Windows 暂时没有确认所选网卡。候选地址仍会保留。", PageActionKind.RetryEndpointProbe, "可重新检测地址可达性；如仍失败，请导出支持包或恢复网卡并退出。");
                case FailureKind.RestoreFailed:
                    return new FailurePresentation("本机网卡尚未确认恢复", "恢复使用工具前的网络配置时未能完成验证。", PageActionKind.RetryRestore, "请重试恢复本机网卡。");
                case FailureKind.Unexpected:
                default:
                    return new FailurePresentation("操作未完成", "工具遇到未分类错误，无法给出更具体结论。", PageActionKind.ExportSupportBundle, "请导出支持包并安全退出。");
            }
        }

        public static string GetFailureRecommendedActionText(
            FailureInfo? failure,
            bool hasCandidateAddress,
            NetworkLifecycleState network)
        {
            if (failure == null || failure.Kind == FailureKind.None)
                return GetFailurePresentation(failure).RecommendedActionText;

            if (failure.Kind == FailureKind.EndpointProbeError
                || failure.Kind == FailureKind.EndpointAdapterUnavailable)
            {
                    return hasCandidateAddress
                        ? "可重新检测候选地址可达性；如仍失败，请导出支持包。"
                    : "尚未保留候选地址，建议导出支持包并安全退出。";
            }

            if (failure.Kind == FailureKind.TemporaryNetworkConfigurationFailed
                || failure.Kind == FailureKind.DhcpListenerFailed)
            {
                return network == NetworkLifecycleState.TemporaryConfigurationMayBeActive
                    || network == NetworkLifecycleState.TemporaryConfigurationActive
                    ? "本机网卡可能仍在临时直连配置下；请先使用底部退出操作恢复。"
                    : GetFailurePresentation(failure).RecommendedActionText;
            }

            return GetFailurePresentation(failure).RecommendedActionText;
        }

        public static string GetFailureNetworkImpactText(NetworkLifecycleState network)
        {
            switch (network)
            {
                case NetworkLifecycleState.RecoveryPrepared:
                    return "当前影响：本机网卡尚未修改，但恢复会话仍待清理。";
                case NetworkLifecycleState.TemporaryConfigurationMayBeActive:
                case NetworkLifecycleState.TemporaryConfigurationActive:
                    return "当前影响：本机网卡可能仍在临时直连配置下；请先使用底部操作恢复。";
                case NetworkLifecycleState.ConfigurationRestored:
                    return "当前影响：本机网卡已恢复到使用工具前的配置。";
                case NetworkLifecycleState.Untouched:
                default:
                    return "当前影响：本机网卡尚未修改，可以安全退出。";
            }
        }

        internal static FirewallPresentation GetFirewallPresentation(FirewallRiskLevel? riskLevel)
        {
            switch (riskLevel.GetValueOrDefault(FirewallRiskLevel.Unknown))
            {
                case FirewallRiskLevel.None:
                    return new FirewallPresentation(PresentationSeverity.Normal, "未发现明显的防火墙阻断证据。");
                case FirewallRiskLevel.High:
                    return new FirewallPresentation(PresentationSeverity.Error, "发现可能影响 DHCP 接收的显式防火墙阻止规则。");
                case FirewallRiskLevel.Warning:
                    return new FirewallPresentation(PresentationSeverity.Warning, "当前防火墙配置存在兼容性风险，但尚不能确认它导致了本次等待。");
                case FirewallRiskLevel.Unknown:
                default:
                    return new FirewallPresentation(PresentationSeverity.Warning, "无法完整评估当前防火墙配置。");
            }
        }

        internal static string GetDhcpTimeoutFollowUpText(FirewallRiskLevel? riskLevel)
        {
            switch (riskLevel.GetValueOrDefault(FirewallRiskLevel.Unknown))
            {
                case FirewallRiskLevel.High:
                    return "请先查看防火墙规则详情，并核对当前程序与所选网络类型的允许状态。";
                case FirewallRiskLevel.None:
                    return "请确认管理口是否启用了 DHCP / 自动获取地址。";
                case FirewallRiskLevel.Warning:
                case FirewallRiskLevel.Unknown:
                default:
                    return string.Empty;
            }
        }

        private static SessionPagePresentation Page(
            SessionPageKind page,
            string title,
            string summary,
            PageActionKind recommendedActionKind)
        {
            return new SessionPagePresentation(page, title, summary, recommendedActionKind);
        }

        private static string GetConfiguringNetworkSummary(NetworkLifecycleState network)
        {
            switch (network)
            {
                case NetworkLifecycleState.RecoveryPrepared:
                    return "恢复保护已准备，尚未开始修改网卡。";
                case NetworkLifecycleState.TemporaryConfigurationMayBeActive:
                    return "正在切换本机网卡配置；退出时将执行恢复。";
                case NetworkLifecycleState.TemporaryConfigurationActive:
                    return "临时直连配置已生效。";
                default:
                    return "正在准备本机网卡的临时直连配置。";
            }
        }

        private static string GetEndpointReachableSummary(string? preferredBmcScheme)
        {
            return "已确认候选地址可达；" +
                (string.Equals(preferredBmcScheme, "http", StringComparison.OrdinalIgnoreCase)
                    ? "如端口可连接，将使用 HTTP 打开浏览器。"
                    : "如端口可连接，将优先使用 HTTPS 打开浏览器。");
        }

        private static string GetCancelledSummary(NetworkLifecycleState network)
        {
            switch (network)
            {
                case NetworkLifecycleState.RecoveryPrepared:
                    return "当前操作已停止，正在清理恢复保护。";
                case NetworkLifecycleState.ConfigurationRestored:
                    return "当前操作已停止，本机网卡配置已恢复。";
                case NetworkLifecycleState.TemporaryConfigurationMayBeActive:
                case NetworkLifecycleState.TemporaryConfigurationActive:
                    return "当前操作已停止，本机网卡仍需恢复。";
                case NetworkLifecycleState.Untouched:
                default:
                    return "当前操作已停止，本机网卡未被修改。";
            }
        }
    }
}
