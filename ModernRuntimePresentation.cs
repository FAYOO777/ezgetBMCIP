using System;
using System.Collections.Generic;

namespace EzGetBmcIp
{

/// <summary>
/// Read-only facts consumed by the shared runtime presentation builder. Both
/// the modern and Legacy hosts implement this interface; it deliberately
/// contains no commands or mutation methods.
/// </summary>
public interface IRuntimePresentationSource
{
    bool IsFirewallRepairBusy { get; }
    SessionPageKind CurrentSessionPage { get; }
    string SessionPageTitle { get; }
    string SessionPageSummary { get; }
    int CurrentFirewallRepairStageValue { get; }
    int CurrentStepIndex { get; }
    string CurrentAdapterName { get; }
    SubnetConfig SubnetConfig { get; }
    string DhcpElapsedText { get; }
    string DhcpMaximumWaitText { get; }
    string DhcpEvidenceText { get; }
    string LinkStatusText { get; }
    string ActivityText { get; }
    string PreferredBmcScheme { get; }
    string DiscoveredIp { get; }
    string DiscoveredIpUrl { get; }
    bool IsIpDiscovered { get; }
    string CandidateSourceText { get; }
    string EndpointProtocolPortText { get; }
    string EndpointVerificationText { get; }
    string EndpointVerificationDiagnosticText { get; }
    string EndpointNetworkWarning { get; }
    bool HasEndpointNetworkWarning { get; }
    string BrowserStatusText { get; }
    string FailureTitle { get; }
    string FailureSummary { get; }
    string FailureTechnicalDetail { get; }
    bool HasFailureTechnicalDetail { get; }
    string FailureNetworkImpactText { get; }
    bool CanRetryEndpointFromFailure { get; }
    string RecoveryAdapterDisplayName { get; }
    string RecoveryOriginalConfigDetails { get; }
    string HistoryAdapterName { get; }
    string HistoryLocalAddress { get; }
    string HistoryBmcAddress { get; }
    string HistoryEndpointText { get; }
    string HistoryLastConfirmedText { get; }
    string HistoryRetrySuggestionText { get; }
    string FirewallSummary { get; }
    PresentationSeverity FirewallSeverity { get; }
    bool HasFirewallAssessment { get; }
    string FirewallTechnicalDetail { get; }
    bool HasFirewallTechnicalDetail { get; }
    string DhcpTimeoutFollowUpText { get; }
    bool HasDhcpTimeoutFollowUp { get; }
    bool ShowDhcpFirewallEvidence { get; }
    bool ShowLongDhcpWaitHint { get; }
    bool ShowLongLinkWaitHint { get; }
    bool ShowFirewallRepair { get; }
    string FirewallRepairFeedback { get; }
}

/// <summary>
/// The compact three-stage navigation shown by the modern runtime surface.
/// This is presentation-only; it never drives the discovery or recovery state
/// machine.
/// </summary>
public enum ModernStageState
{
    Pending,
    Active,
    Done,
    Attention,
    Failed
}

// The Legacy host has its FirewallRepairStage enum in a conditional namespace.
// Keep the shared presentation contract namespace-neutral by transporting the
// numeric stage value and mapping it to this display-only enum.
internal enum RuntimeFirewallRepairStage
{
    None,
    CheckingRules,
    AwaitingConsent,
    RestoringNetwork,
    ApplyingRules,
    VerifyingRules,
    Completed
}

public enum ModernRuntimeLayoutKind
{
    Running,
    Success,
    Attention,
    Failure,
    Recovery
}

public enum ModernActionKind
{
    None,
    OpenManagementPage,
    CopyAddress,
    RetryEndpoint,
    PrepareHistoryRetry,
    RepairFirewall,
    ExportSupport
}

public class ModernStageItem
{
    public ModernStageItem(string label, ModernStageState state)
    {
        Label = label;
        State = state;
    }

    public string Label { get; }
    public ModernStageState State { get; }
    public string Marker => State switch
    {
        ModernStageState.Done => "✓",
        ModernStageState.Active => "•",
        ModernStageState.Attention => "!",
        ModernStageState.Failed => "×",
        _ => "○"
    };
    public string StateText => State switch
    {
        ModernStageState.Done => "已完成",
        ModernStageState.Active => "进行中",
        ModernStageState.Attention => "需留意",
        ModernStageState.Failed => "失败",
        _ => "待处理"
    };
}

public class ModernInfoRow
{
    public ModernInfoRow(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
}

/// <summary>
/// A modern-only projection of the existing MainViewModel facts. It keeps all
/// copy, hierarchy and action priority in one place while leaving command and
/// network behavior untouched.
/// </summary>
public sealed class ModernRuntimePresentation
{
    private ModernRuntimePresentation(
        SessionPageKind page,
        ModernRuntimeLayoutKind layout,
        string title,
        string summary,
        string statusGlyph,
        PresentationSeverity severity,
        string primaryValueLabel,
        string primaryValue,
        string progressText,
        bool showProgressRing,
        bool showTimeProgress,
        double timeProgressValue,
        bool showStageRail,
        ModernActionKind primaryAction,
        ModernActionKind secondaryAction,
        bool showOtherEndpointActions,
        IReadOnlyList<ModernStageItem> stages,
        IReadOnlyList<ModernInfoRow> facts,
        string noticeText,
        PresentationSeverity noticeSeverity,
        string detailsText)
    {
        Page = page;
        Layout = layout;
        Title = title;
        Summary = summary;
        StatusGlyph = statusGlyph;
        Severity = severity;
        PrimaryValueLabel = primaryValueLabel;
        PrimaryValue = primaryValue;
        ProgressText = progressText;
        ShowProgressRing = showProgressRing;
        ShowTimeProgress = showTimeProgress;
        TimeProgressValue = timeProgressValue;
        ShowStageRail = showStageRail;
        PrimaryAction = primaryAction;
        SecondaryAction = secondaryAction;
        ShowOtherEndpointActions = showOtherEndpointActions;
        Stages = stages;
        Facts = facts;
        NoticeText = noticeText;
        NoticeSeverity = noticeSeverity;
        DetailsText = detailsText;
    }

    public SessionPageKind Page { get; }
    public ModernRuntimeLayoutKind Layout { get; }
    public string Title { get; }
    public string Summary { get; }
    public string StatusGlyph { get; }
    public PresentationSeverity Severity { get; }
    public string PrimaryValueLabel { get; }
    public string PrimaryValue { get; }
    public bool HasPrimaryValue => !string.IsNullOrWhiteSpace(PrimaryValue);
    public string ProgressText { get; }
    public bool ShowProgressRing { get; }
    public bool ShowTimeProgress { get; }
    public double TimeProgressValue { get; }
    public bool ShowStageRail { get; }
    public ModernActionKind PrimaryAction { get; }
    public ModernActionKind SecondaryAction { get; }
    public bool ShowOtherEndpointActions { get; }
    public IReadOnlyList<ModernStageItem> Stages { get; }
    public IReadOnlyList<ModernInfoRow> Facts { get; }
    public string NoticeText { get; }
    public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeText);
    public PresentationSeverity NoticeSeverity { get; }
    public string DetailsText { get; }
    public bool HasDetails => !string.IsNullOrWhiteSpace(DetailsText);

    internal static ModernRuntimePresentation From(IRuntimePresentationSource viewModel)
    {
        if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

        var page = viewModel.IsFirewallRepairBusy
            ? SessionPageKind.FirewallRepairing
            : viewModel.CurrentSessionPage;
        var stages = BuildStages(viewModel, page);
        var facts = BuildFacts(viewModel, page);
        var details = BuildDetails(viewModel, page);
        var notice = BuildNotice(viewModel, page, out var noticeSeverity);
        var title = GetTitle(viewModel, page);
        var summary = GetSummary(viewModel, page);

        var severity = GetSeverity(page);
        var layout = GetLayout(page);
        var showRing = page == SessionPageKind.WaitingForLink
            || page == SessionPageKind.ConfiguringNetwork
            || page == SessionPageKind.ProbingEndpoint
            || page == SessionPageKind.Restoring
            || page == SessionPageKind.FirewallRepairing;
        var showTimeProgress = page == SessionPageKind.WaitingForDhcp;
        var timeProgress = ParseElapsed(viewModel.DhcpElapsedText) / 180d;

        var primaryAction = ModernActionKind.None;
        var secondaryAction = ModernActionKind.None;
        if (page == SessionPageKind.EndpointReachable)
        {
            primaryAction = ModernActionKind.OpenManagementPage;
            secondaryAction = ModernActionKind.CopyAddress;
        }
        else if (page == SessionPageKind.EndpointUnreachable)
        {
            primaryAction = ModernActionKind.RetryEndpoint;
            secondaryAction = ModernActionKind.CopyAddress;
        }
        else if (page == SessionPageKind.HistoryRetrySuggestion)
        {
            primaryAction = ModernActionKind.PrepareHistoryRetry;
            secondaryAction = ModernActionKind.ExportSupport;
        }
        else if (page == SessionPageKind.DhcpTimedOut)
        {
            // Warning/Unknown are still actionable here: the repair command
            // performs a fresh read-only assessment and asks for confirmation
            // before making any change. Restricting this entry point to High
            // made the recovery path unreachable when Windows did not expose a
            // precise Public-profile Block (for example after the user missed
            // the Public checkbox in the firewall prompt).
            primaryAction = viewModel.ShowFirewallRepair
                && viewModel.HasFirewallAssessment
                && viewModel.FirewallSeverity != PresentationSeverity.Normal
                ? ModernActionKind.RepairFirewall
                : ModernActionKind.ExportSupport;
        }
        else if (page == SessionPageKind.Failure)
        {
            primaryAction = viewModel.ShowFirewallRepair && viewModel.FirewallSeverity == PresentationSeverity.Error
                ? ModernActionKind.RepairFirewall
                : viewModel.CanRetryEndpointFromFailure
                    ? ModernActionKind.RetryEndpoint
                    : ModernActionKind.ExportSupport;

            // A general failure always keeps the support-bundle escape hatch
            // visible. When the recommended action is already export, avoid
            // rendering the same action twice.
            if (primaryAction != ModernActionKind.ExportSupport)
                secondaryAction = ModernActionKind.ExportSupport;
        }
        else if (page == SessionPageKind.RestoreFailed)
        {
            // RetryRestore is intentionally kept in the fixed safety footer so
            // there is only one restore command. The main card still exposes
            // the support-bundle escape hatch alongside that footer action.
            secondaryAction = ModernActionKind.ExportSupport;
        }

        var primaryValueLabel = string.Empty;
        var primaryValue = string.Empty;
        switch (page)
        {
            case SessionPageKind.WaitingForDhcp:
                primaryValueLabel = "等待时间";
                primaryValue = viewModel.DhcpElapsedText + " / " + viewModel.DhcpMaximumWaitText;
                break;
            case SessionPageKind.ProbingEndpoint:
            case SessionPageKind.EndpointUnreachable:
                primaryValueLabel = "候选地址";
                primaryValue = viewModel.DiscoveredIp ?? string.Empty;
                break;
            case SessionPageKind.EndpointReachable:
                primaryValueLabel = "候选地址";
                primaryValue = viewModel.DiscoveredIpUrl;
                break;
            case SessionPageKind.DhcpTimedOut:
                primaryValueLabel = "等待时间";
                primaryValue = viewModel.DhcpElapsedText;
                break;
        }

        var progressText = page switch
        {
            SessionPageKind.WaitingForLink => "等待检测到物理连接…",
            SessionPageKind.ConfiguringNetwork => viewModel.ActivityText,
            SessionPageKind.ProbingEndpoint => "正在并行检查 Ping、TCP 443 和 TCP 80…",
            SessionPageKind.Restoring => "正在写回使用工具前的网络配置…",
            SessionPageKind.FirewallRepairing => viewModel.FirewallRepairFeedback,
            _ => string.Empty
        };

        return new ModernRuntimePresentation(
            page,
            layout,
            title,
            summary,
            GetStatusGlyph(layout),
            severity,
            primaryValueLabel,
            primaryValue,
            progressText,
            showRing,
            showTimeProgress,
            Math.Max(0d, Math.Min(1d, timeProgress)),
            ShouldShowStageRail(page),
            primaryAction,
            secondaryAction,
            page == SessionPageKind.EndpointReachable || page == SessionPageKind.EndpointUnreachable,
            stages,
            facts,
            notice,
            noticeSeverity,
            details);
    }

    private static ModernRuntimeLayoutKind GetLayout(SessionPageKind page)
    {
        if (page == SessionPageKind.EndpointReachable)
            return ModernRuntimeLayoutKind.Success;
        if (page == SessionPageKind.EndpointUnreachable
            || page == SessionPageKind.DhcpTimedOut
            || page == SessionPageKind.HistoryRetrySuggestion)
            return ModernRuntimeLayoutKind.Attention;
        if (page == SessionPageKind.Failure || page == SessionPageKind.RestoreFailed)
            return ModernRuntimeLayoutKind.Failure;
        if (page == SessionPageKind.Restoring)
            return ModernRuntimeLayoutKind.Recovery;
        return ModernRuntimeLayoutKind.Running;
    }

    private static string GetTitle(IRuntimePresentationSource viewModel, SessionPageKind page) => page switch
    {
        SessionPageKind.WaitingForLink => "请连接服务器管理口",
        SessionPageKind.ConfiguringNetwork => "正在配置直连网络",
        SessionPageKind.WaitingForDhcp => "正在等待 BMC 获取地址",
        SessionPageKind.ProbingEndpoint => "已获得候选地址，正在确认可达性",
        SessionPageKind.EndpointReachable => "地址已分配并确认可达",
        SessionPageKind.EndpointUnreachable => "已获得地址，但暂未确认可达",
        SessionPageKind.DhcpTimedOut => "等待 DHCP 地址分配超时",
        SessionPageKind.HistoryRetrySuggestion => "可以尝试上次使用的网段",
        SessionPageKind.Restoring => "正在恢复本机网卡",
        SessionPageKind.RestoreFailed => "本机网卡尚未确认恢复",
        SessionPageKind.FirewallRepairing => "正在处理防火墙并准备重试",
        SessionPageKind.Cancelled => "已停止当前流程",
        SessionPageKind.Failure => viewModel.FailureTitle,
        _ => viewModel.SessionPageTitle
    };

    private static string GetSummary(IRuntimePresentationSource viewModel, SessionPageKind page)
    {
        return page switch
        {
            SessionPageKind.WaitingForLink => "将所选网卡直连 IPMI/BMC 管理口，检测到连接后会自动继续。",
            SessionPageKind.ConfiguringNetwork => "正在将所选网卡设置为 " + viewModel.SubnetConfig.ServerDisplay + "，并启动临时 DHCP。",
            SessionPageKind.WaitingForDhcp => "本机网卡已切换到临时直连配置，正在等待 BMC 发起 DHCP。",
            SessionPageKind.ProbingEndpoint => "地址分配已完成，正在检查 Ping 与 TCP 80/443。",
            SessionPageKind.EndpointReachable => "已确认候选地址可达；网页是否加载成功不影响地址分配结果。",
            SessionPageKind.EndpointUnreachable => "候选地址仍会保留，可能需要重新检测，或 BMC 使用了其他端口。",
            SessionPageKind.DhcpTimedOut => "在 3 分钟内未观察到完成地址分配的 DHCP 流程。",
            SessionPageKind.HistoryRetrySuggestion => "本次 DHCP 等待已超时；该网卡曾在另一网段确认过地址可达。",
            SessionPageKind.Restoring => "正在恢复使用工具前的网络配置，请等待完成。",
            SessionPageKind.RestoreFailed => "工具无法确认所选网卡已恢复到使用工具前的配置。",
            SessionPageKind.FirewallRepairing => "工具会先恢复本机网卡，再处理并复查防火墙规则；完成后返回网卡选择页。",
            SessionPageKind.Cancelled => "当前操作已停止；请根据页脚状态确认本机网卡是否已恢复。",
            SessionPageKind.Failure => viewModel.FailureSummary,
            _ => viewModel.SessionPageSummary
        };
    }

    private static PresentationSeverity GetSeverity(SessionPageKind page)
    {
        if (page == SessionPageKind.EndpointReachable)
            return PresentationSeverity.Success;
        if (page == SessionPageKind.EndpointUnreachable
            || page == SessionPageKind.DhcpTimedOut
            || page == SessionPageKind.HistoryRetrySuggestion)
            return PresentationSeverity.Warning;
        if (page == SessionPageKind.Failure || page == SessionPageKind.RestoreFailed)
            return PresentationSeverity.Error;
        if (page == SessionPageKind.Restoring
            || page == SessionPageKind.WaitingForLink
            || page == SessionPageKind.ConfiguringNetwork
            || page == SessionPageKind.WaitingForDhcp
            || page == SessionPageKind.ProbingEndpoint
            || page == SessionPageKind.FirewallRepairing)
            return PresentationSeverity.Progress;
        return PresentationSeverity.Normal;
    }

    private static string GetStatusGlyph(ModernRuntimeLayoutKind layout) => layout switch
    {
        ModernRuntimeLayoutKind.Success => "✓",
        ModernRuntimeLayoutKind.Attention => "!",
        ModernRuntimeLayoutKind.Failure => "×",
        ModernRuntimeLayoutKind.Recovery => "↻",
        _ => "…"
    };

    private static bool ShouldShowStageRail(SessionPageKind page)
    {
        return page == SessionPageKind.WaitingForLink
            || page == SessionPageKind.ConfiguringNetwork
            || page == SessionPageKind.WaitingForDhcp
            || page == SessionPageKind.ProbingEndpoint
            || page == SessionPageKind.EndpointReachable
            || page == SessionPageKind.EndpointUnreachable
            || page == SessionPageKind.DhcpTimedOut
            || page == SessionPageKind.HistoryRetrySuggestion
            || page == SessionPageKind.FirewallRepairing;
    }

    private static IReadOnlyList<ModernStageItem> BuildStages(IRuntimePresentationSource viewModel, SessionPageKind page)
    {
        if (page == SessionPageKind.FirewallRepairing)
            return BuildFirewallRepairStages((RuntimeFirewallRepairStage)viewModel.CurrentFirewallRepairStageValue);

        var failureIndex = viewModel.CurrentStepIndex;
        var establish = ModernStageState.Done;
        var acquire = ModernStageState.Pending;
        var verify = ModernStageState.Pending;

        if (page == SessionPageKind.WaitingForLink || page == SessionPageKind.ConfiguringNetwork)
        {
            establish = ModernStageState.Active;
        }
        else if (page == SessionPageKind.WaitingForDhcp)
        {
            acquire = ModernStageState.Active;
        }
        else if (page == SessionPageKind.DhcpTimedOut || page == SessionPageKind.HistoryRetrySuggestion)
        {
            acquire = ModernStageState.Attention;
        }
        else if (page == SessionPageKind.ProbingEndpoint)
        {
            acquire = ModernStageState.Done;
            verify = ModernStageState.Active;
        }
        else if (page == SessionPageKind.EndpointUnreachable)
        {
            acquire = ModernStageState.Done;
            verify = ModernStageState.Attention;
        }
        else if (page == SessionPageKind.EndpointReachable)
        {
            acquire = ModernStageState.Done;
            verify = ModernStageState.Done;
        }
        else if (page == SessionPageKind.Failure)
        {
            if (failureIndex >= 3)
            {
                acquire = ModernStageState.Done;
                verify = ModernStageState.Failed;
            }
            else if (failureIndex >= 2)
            {
                acquire = ModernStageState.Failed;
            }
            else
            {
                establish = ModernStageState.Failed;
            }
        }

        return new[]
        {
            new ModernStageItem("建立直连", establish),
            new ModernStageItem("获取地址", acquire),
            new ModernStageItem("验证访问", verify)
        };
    }

    private static IReadOnlyList<ModernStageItem> BuildFirewallRepairStages(RuntimeFirewallRepairStage stage)
    {
        var check = stage == RuntimeFirewallRepairStage.CheckingRules
            ? ModernStageState.Active
            : ModernStageState.Done;
        var restore = stage < RuntimeFirewallRepairStage.RestoringNetwork
            ? ModernStageState.Pending
            : stage == RuntimeFirewallRepairStage.RestoringNetwork
                ? ModernStageState.Active
                : ModernStageState.Done;
        var apply = stage < RuntimeFirewallRepairStage.ApplyingRules
            ? ModernStageState.Pending
            : stage == RuntimeFirewallRepairStage.Completed
                ? ModernStageState.Done
                : ModernStageState.Active;

        return new[]
        {
            new ModernStageItem("检查规则", check),
            new ModernStageItem("恢复网卡", restore),
            new ModernStageItem("处理并复查", apply)
        };
    }

    private static IReadOnlyList<ModernInfoRow> BuildFacts(IRuntimePresentationSource viewModel, SessionPageKind page)
    {
        var facts = new List<ModernInfoRow>();
        var adapter = viewModel.CurrentAdapterName;
        var local = viewModel.SubnetConfig.ServerDisplay;

        switch (page)
        {
            case SessionPageKind.WaitingForLink:
                facts.Add(new ModernInfoRow("网卡", adapter));
                facts.Add(new ModernInfoRow("连接状态", viewModel.LinkStatusText));
                facts.Add(new ModernInfoRow("网络修改", "尚未开始"));
                break;
            case SessionPageKind.ConfiguringNetwork:
                facts.Add(new ModernInfoRow("网卡", adapter));
                facts.Add(new ModernInfoRow("临时地址", local));
                facts.Add(new ModernInfoRow("网络修改", "正在进行"));
                break;
            case SessionPageKind.WaitingForDhcp:
                facts.Add(new ModernInfoRow("本机临时地址", local));
                facts.Add(new ModernInfoRow("预期地址", viewModel.SubnetConfig.PoolDisplay));
                facts.Add(new ModernInfoRow("DHCP 状态", viewModel.DhcpEvidenceText));
                break;
            case SessionPageKind.ProbingEndpoint:
                facts.Add(new ModernInfoRow("网卡", adapter));
                facts.Add(new ModernInfoRow("地址来源", viewModel.CandidateSourceText));
                facts.Add(new ModernInfoRow("验证范围", "Ping · TCP 443 · TCP 80"));
                break;
            case SessionPageKind.EndpointReachable:
                facts.Add(new ModernInfoRow("端口状态", viewModel.EndpointProtocolPortText));
                facts.Add(new ModernInfoRow("验证结果", viewModel.EndpointVerificationText));
                facts.Add(new ModernInfoRow("地址来源", viewModel.CandidateSourceText));
                break;
            case SessionPageKind.EndpointUnreachable:
                facts.Add(new ModernInfoRow("地址来源", viewModel.CandidateSourceText));
                facts.Add(new ModernInfoRow("验证结果", viewModel.EndpointVerificationText));
                facts.Add(new ModernInfoRow("当前网卡", adapter));
                break;
            case SessionPageKind.DhcpTimedOut:
                facts.Add(new ModernInfoRow("网卡", adapter));
                facts.Add(new ModernInfoRow("本机临时地址", local));
                facts.Add(new ModernInfoRow("等待时间", viewModel.DhcpElapsedText));
                break;
            case SessionPageKind.HistoryRetrySuggestion:
                facts.Add(new ModernInfoRow("网卡", viewModel.HistoryAdapterName));
                facts.Add(new ModernInfoRow("上次临时地址", viewModel.HistoryLocalAddress));
                facts.Add(new ModernInfoRow("上次候选地址", viewModel.HistoryBmcAddress));
                break;
            case SessionPageKind.Failure:
                facts.Add(new ModernInfoRow("网卡", adapter));
                facts.Add(new ModernInfoRow("网络影响", viewModel.FailureNetworkImpactText));
                if (viewModel.IsIpDiscovered)
                    facts.Add(new ModernInfoRow("候选地址", viewModel.DiscoveredIp ?? string.Empty));
                break;
            case SessionPageKind.Restoring:
            case SessionPageKind.RestoreFailed:
                facts.Add(new ModernInfoRow("网卡", viewModel.RecoveryAdapterDisplayName));
                facts.Add(new ModernInfoRow("恢复目标", "使用工具前的 IPv4 和 DNS 配置"));
                break;
            case SessionPageKind.FirewallRepairing:
                facts.Add(new ModernInfoRow("目标网卡", adapter));
                facts.Add(new ModernInfoRow("当前步骤", viewModel.FirewallRepairFeedback));
                facts.Add(new ModernInfoRow("完成后", "返回网卡选择页，由你核对后手动开始"));
                break;
        }

        return facts;
    }

    private static string BuildNotice(
        IRuntimePresentationSource viewModel,
        SessionPageKind page,
        out PresentationSeverity severity)
    {
        severity = PresentationSeverity.Warning;
        if (page == SessionPageKind.WaitingForLink && viewModel.ShowLongLinkWaitHint)
            return "仍未检测到连接，请检查网线是否连接到服务器的独立管理口。";

        if (page == SessionPageKind.WaitingForDhcp)
        {
            if (viewModel.ShowDhcpFirewallEvidence
                && viewModel.HasFirewallAssessment
                && viewModel.FirewallSeverity != PresentationSeverity.Normal)
            {
                severity = viewModel.FirewallSeverity;
                return viewModel.FirewallSummary;
            }
            if (viewModel.ShowLongDhcpWaitHint)
                return "等待时间较长。请确认网线连接的是独立管理口，且 BMC 已启用 DHCP / 自动获取地址。";
        }

        if (page == SessionPageKind.EndpointReachable && viewModel.HasEndpointNetworkWarning)
            return viewModel.EndpointNetworkWarning;

        if (page == SessionPageKind.DhcpTimedOut)
        {
            if (viewModel.HasFirewallAssessment
                && viewModel.FirewallSeverity != PresentationSeverity.Normal)
            {
                severity = viewModel.FirewallSeverity;
                return viewModel.FirewallSummary;
            }
            if (viewModel.HasDhcpTimeoutFollowUp)
                return viewModel.DhcpTimeoutFollowUpText;
        }

        if (page == SessionPageKind.HistoryRetrySuggestion)
            return "历史地址仅供参考，不代表当前连接的是同一台设备，也不保证仍然可用。";

        if (page == SessionPageKind.RestoreFailed)
        {
            severity = PresentationSeverity.Error;
            return "该网卡可能仍在临时配置下，原有网络连接可能尚未恢复。";
        }

        if (page == SessionPageKind.Failure)
        {
            severity = PresentationSeverity.Error;
            return viewModel.FailureNetworkImpactText;
        }

        return string.Empty;
    }

    private static string BuildDetails(IRuntimePresentationSource viewModel, SessionPageKind page)
    {
        var lines = new List<string>();
        switch (page)
        {
            case SessionPageKind.WaitingForDhcp:
                lines.Add("DHCP 证据：" + viewModel.DhcpEvidenceText);
                if (viewModel.HasFirewallTechnicalDetail)
                    lines.Add("防火墙详情：\n" + viewModel.FirewallTechnicalDetail);
                break;
            case SessionPageKind.ProbingEndpoint:
            case SessionPageKind.EndpointUnreachable:
            case SessionPageKind.EndpointReachable:
                lines.Add(viewModel.EndpointVerificationDiagnosticText);
                break;
            case SessionPageKind.DhcpTimedOut:
                lines.Add("未在最长等待时间内完成地址分配流程。");
                if (viewModel.HasFailureTechnicalDetail)
                    lines.Add(viewModel.FailureTechnicalDetail);
                if (viewModel.HasFirewallTechnicalDetail)
                    lines.Add("防火墙详情：\n" + viewModel.FirewallTechnicalDetail);
                break;
            case SessionPageKind.HistoryRetrySuggestion:
                lines.Add(viewModel.HistoryRetrySuggestionText);
                lines.Add("上次可达性证据：" + viewModel.HistoryEndpointText);
                lines.Add("上次确认时间：" + viewModel.HistoryLastConfirmedText);
                lines.Add("选择后会先恢复本机网卡，再填入上次网段；不会自动开始下一轮，也不会修改 BMC。");
                break;
            case SessionPageKind.Failure:
                if (viewModel.HasFailureTechnicalDetail)
                    lines.Add(viewModel.FailureTechnicalDetail);
                if (viewModel.HasFirewallTechnicalDetail)
                    lines.Add("防火墙详情：\n" + viewModel.FirewallTechnicalDetail);
                break;
            case SessionPageKind.RestoreFailed:
                if (!string.IsNullOrWhiteSpace(viewModel.RecoveryOriginalConfigDetails))
                    lines.Add(viewModel.RecoveryOriginalConfigDetails);
                if (viewModel.HasFailureTechnicalDetail)
                    lines.Add(viewModel.FailureTechnicalDetail);
                lines.Add("恢复记录会保留；后续启动工具时会再次尝试处理。");
                break;
            case SessionPageKind.ConfiguringNetwork:
                if (!string.IsNullOrWhiteSpace(viewModel.ActivityText))
                    lines.Add(viewModel.ActivityText);
                break;
            case SessionPageKind.FirewallRepairing:
                lines.Add("处理期间不会自动开始下一轮测试，也不会修改 BMC 配置。");
                break;
        }

        return string.Join("\n\n", lines);
    }

    private static double ParseElapsed(string value)
    {
        if (TimeSpan.TryParse(value, out var elapsed))
            return Math.Max(0d, elapsed.TotalSeconds);

        return 0d;
    }
}
}
