#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace EzGetBmcIp
{

/// <summary>
/// Text shown by the owner-modal consent window. This stays internal to the
/// desktop UI so a missing presenter always fails closed.
/// </summary>
internal sealed class ConsentNotice
{
    public ConsentNotice(
        string title,
        string intro,
        IReadOnlyList<string> items,
        string acknowledgementText,
        string confirmButtonText,
        string warningText = "",
        IReadOnlyList<ConsentSection>? sections = null,
        bool preferSafeDefault = false,
        ModernNetworkChangeContent? networkChange = null)
    {
        Title = title;
        Intro = intro;
        Items = items;
        AcknowledgementText = acknowledgementText;
        ConfirmButtonText = confirmButtonText;
        WarningText = warningText ?? string.Empty;
        Sections = sections ?? Array.Empty<ConsentSection>();
        PreferSafeDefault = preferSafeDefault;
        NetworkChange = networkChange;
        LayoutKind = networkChange != null
            ? ConsentLayoutKind.NetworkChangeSummary
            : Sections.Count > 0
                ? ConsentLayoutKind.Sectioned
                : ConsentLayoutKind.Flat;
    }

    public string Title { get; private set; }
    public string Intro { get; private set; }
    public IReadOnlyList<string> Items { get; private set; }
    public string AcknowledgementText { get; private set; }
    public string ConfirmButtonText { get; private set; }
    public string WarningText { get; private set; }
    public bool HasWarning { get { return !string.IsNullOrWhiteSpace(WarningText); } }
    public bool ShowLegacyWarning => HasWarning && !HasNetworkChangeSummary;
    public IReadOnlyList<ConsentSection> Sections { get; private set; }
    public bool HasSections => Sections.Count > 0;
    public bool PreferSafeDefault { get; private set; }
    public ConsentLayoutKind LayoutKind { get; private set; }
    public ModernNetworkChangeContent? NetworkChange { get; private set; }
    public bool HasNetworkChangeSummary => NetworkChange != null;

    public static ConsentNotice CreateUsageRisk()
    {
        return new ConsentNotice(
            "使用风险告知",
            "继续前请确认以下事项。未勾选确认或关闭此窗口均不会继续。",
            new[]
            {
                "本工具只适用于笔记本有线网卡直连服务器的 IPMI/BMC 专用管理口；不要接入交换机、业务网口或已有局域网。",
                "开始后，所选网卡会被临时接管并失去原有网络连接。请不要选择正在承担上网、远程桌面或生产通信的网卡。",
                "程序退出时会停止内置 DHCP 服务，并恢复、验证所选网卡已记录的 IPv4 与 DNS 配置；原来使用 DHCP 的网卡只会恢复为自动获取，不保证回到同一个租约地址。",
                "本工具只恢复电脑上所选网卡，不会主动还原 BMC 的网络设置；退出后请按现场网络方案重新接回 BMC。",
                "强制结束程序时恢复守护会尝试恢复本机网卡；若守护未能完成或电脑断电，需在目标网卡重新连接后再次启动本工具。"
            },
            "我已阅读并理解上述风险，确认继续",
            "同意并继续");
    }

    /// <summary>
    /// The concise first-run notice used by the modern WPF-UI client. Keep the
    /// original CreateUsageRisk text for the Legacy client, which has its own
    /// dialog and remains intentionally unchanged.
    /// </summary>
    public static ConsentNotice CreateModernUsageRisk()
    {
        return new ConsentNotice(
            "开始前请确认",
            "工具将临时接管一块有线网卡。请确认以下内容后继续。",
            Array.Empty<string>(),
            "我已了解上述影响，确认继续",
            "确认并继续",
            sections: new[]
            {
                new ConsentSection(
                    "使用条件",
                    new[]
                    {
                        "仅将目标网卡直连服务器的 IPMI/BMC 管理口，不要接入交换机、业务网或已有局域网。",
                        "运行期间，这块网卡会暂时断开原有网络。请不要选择正在上网、远程桌面或承担业务的网卡。"
                    }),
                new ConsentSection(
                    "恢复方式",
                    new[]
                    {
                        "正常退出时，工具会停止自身 DHCP，并恢复这块网卡原有的 IPv4 和 DNS 设置。若原来使用 DHCP，恢复后的地址可能不同。",
                        "工具只修改本机网卡，不会修改 BMC 的网络设置。测试结束后请按原接线恢复。",
                        "请勿强制结束程序或在运行中断电；如果网卡未能自动恢复，请重新连接目标网卡后再次启动工具。"
                    })
            },
            preferSafeDefault: true);
    }

    public static ConsentNotice CreateNetworkChange(
        WiredAdapter adapter,
        SubnetConfig subnetConfig,
        AdapterOriginalConfig originalConfig,
        FirewallAssessment? firewallAssessment = null)
    {
        return new ConsentNotice(
            "网络修改风险告知",
            "此操作尚未开始。请核对即将执行的网络变更；勾选确认后才会修改网卡。",
            new[]
            {
                "目标网卡：" + adapter.DisplayName + "。运行期间该网卡将不能保持原有网络连接。",
                "本机 IPv4 将临时设置为 " + subnetConfig.ServerDisplay + "（子网掩码 " + subnetConfig.Mask + "）；原有 DNS 服务器设置会被临时清空，并启动内置 DHCP 服务。",
                "直连的 BMC 预期通过 DHCP 获得地址：" + subnetConfig.PoolDisplay + "（响应租期为 1 小时，网关和 DNS 指向本机 " + subnetConfig.ServerIp + "）。请只把这块网卡连接到 IPMI/BMC 专用管理口。",
                BuildRestoreDescription(originalConfig),
                "本工具不会主动还原 BMC 的网络设置。强制结束程序时恢复守护会尝试恢复本机网卡；若仍未恢复，请在目标网卡重新连接后再次启动本工具。"
            },
            "我已核对上述网络变更与恢复目标，确认继续",
            "同意并开始",
            firewallAssessment == null ? "" : firewallAssessment.BuildConsentWarning());
    }

    /// <summary>
    /// Structured network-change summary shared by the formal and Legacy
    /// clients. Both clients render the same facts with their native control
    /// sets; the network workflow itself remains unchanged.
    /// </summary>
    public static ConsentNotice CreateModernNetworkChange(
        WiredAdapter adapter,
        SubnetConfig subnetConfig,
        AdapterOriginalConfig originalConfig,
        FirewallAssessment? firewallAssessment = null)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        if (subnetConfig == null) throw new ArgumentNullException(nameof(subnetConfig));
        if (originalConfig == null) throw new ArgumentNullException(nameof(originalConfig));

        var networkChange = new ModernNetworkChangeContent(
            BuildFirewallNotice(firewallAssessment),
            new[]
            {
                new ConsentValueRow("目标网卡", adapter.DisplayName),
                new ConsentValueRow("临时 IPv4", subnetConfig.ServerDisplay),
                new ConsentValueRow("预期管理地址", subnetConfig.PoolDisplay),
                new ConsentValueRow("运行期间", "该网卡会断开原有网络，不能用于上网、远程桌面或其他业务连接；工具会启动临时 DHCP。")
            },
            BuildModernRestoreSummary(originalConfig),
            BuildModernRestoreDetails(originalConfig));

        return new ConsentNotice(
            "网络修改风险告知",
            "确认后，工具会临时修改所选网卡并启动 DHCP。",
            Array.Empty<string>(),
            "我已核对本次修改和恢复方式，确认继续",
            "同意并开始",
            preferSafeDefault: true,
            networkChange: networkChange);
    }

    public static ConsentNotice CreateHistoryRetryPreparation(
        WiredAdapter adapter,
        BmcHistoryRecord history)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        if (history == null) throw new ArgumentNullException(nameof(history));

        return new ConsentNotice(
            "按上次网段准备重试",
            "上一次 DHCP 等待已超时。请确认是否恢复本机网卡，并填入该网卡上次成功访问 BMC 时使用的网段。此操作不会自动开始下一轮测试。",
            new[]
            {
                "目标网卡：" + adapter.DisplayName + "。当前测试会先停止内置 DHCP 服务，并恢复这块网卡使用工具前的配置。",
                "恢复完成后，网段输入框将填入上次使用的本机地址：" + history.LocalAddress + " / 24。",
                "上次可访问的 BMC 地址为 " + history.BmcAddress + "（记录时间：" + history.LastConfirmedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "）。该地址可能仍有效，但尚未在本次操作中确认。",
                "不会自动开始新的 DHCP 测试，不会修改 BMC 配置，也不会创建、删除或修改防火墙规则。你需要在恢复完成后自行点击“开始”并再次确认网络变更。"
            },
            "我确认先恢复本机网卡，并在恢复后手动按上次网段重新开始",
            "恢复并填入上次网段");
    }

    private static string BuildRestoreDescription(AdapterOriginalConfig originalConfig)
    {
        var dns = FormatDns(originalConfig);
        if (originalConfig.DhcpEnabled)
        {
            return "使用前状态：IPv4 通过 DHCP 自动获取（当前地址：" +
                FormatAddresses(originalConfig) + "；当前网关：" +
                FormatGateways(originalConfig) + "；当前 DNS：" +
                FormatObservedDns(originalConfig) + "）。退出后会停止内置 DHCP 服务，并将本机该网卡恢复为 IPv4 DHCP 自动获取；DNS：" +
                dns + "。当前 DHCP 租约中的地址、网关及自动 DNS 仅用于展示，重新获取的租约可能不同，也不保证立即恢复联网。";
        }

        return "退出后会停止内置 DHCP 服务，并重新写入本机该网卡的静态 IPv4 " + FormatAddresses(originalConfig) +
            "；网关：" + FormatGateways(originalConfig) + "；DNS：" + dns +
            "。已记录的网关跃点会一并恢复；IPv6、VPN、静态路由等未记录项目不在恢复范围内。";
    }

    private static string FormatAddresses(AdapterOriginalConfig originalConfig)
    {
        if (originalConfig.StaticAddresses.Count == 0)
            return "未设置";

        return string.Join("、", originalConfig.StaticAddresses.Select(item =>
            item.Address + " / " + item.Mask));
    }

    private static string FormatGateways(AdapterOriginalConfig originalConfig)
    {
        return originalConfig.Gateways.Count == 0
            ? "无"
            : string.Join("、", originalConfig.Gateways.Select(item => item.ToString()));
    }

    private static string FormatDns(AdapterOriginalConfig originalConfig)
    {
        if (originalConfig.DnsServersFromDhcp || originalConfig.DnsServers.Count == 0)
            return "自动获取";

        return "手动：" + string.Join("、", originalConfig.DnsServers.Select(item => item.ToString()));
    }

    private static string FormatObservedDns(AdapterOriginalConfig originalConfig)
    {
        if (originalConfig.DnsServers.Count == 0)
            return "未检测到";

        var servers = string.Join("、", originalConfig.DnsServers.Select(item => item.ToString()));
        return originalConfig.DnsServersFromDhcp ? "自动获取（当前：" + servers + "）" : "手动：" + servers;
    }

    private static ConsentFirewallNotice? BuildFirewallNotice(FirewallAssessment? assessment)
    {
        if (assessment is null)
        {
            return new ConsentFirewallNotice(
                "防火墙状态无法完整确认",
                "未取得本次防火墙检查结果；若 Windows 弹出提示，请勾选实际网络类型，显示“公用网络”时必须勾选。",
                ConsentFirewallNoticeStatus.Attention,
                BuildModernFirewallDetails(assessment));
        }

        if (assessment.RiskLevel == FirewallRiskLevel.High)
        {
            return new ConsentFirewallNotice(
                "防火墙可能阻断 DHCP",
                "检测到当前程序或 UDP/67 的入站阻止规则，DHCP 请求可能无法到达工具；这不是 DHCP 必然失败的结论。",
                ConsentFirewallNoticeStatus.Impact,
                BuildModernFirewallDetails(assessment));
        }

        if (assessment.RiskLevel == FirewallRiskLevel.Unknown
            || string.Equals(assessment.NetworkCategory, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return new ConsentFirewallNotice(
                "防火墙状态无法完整确认",
                "未能确认所选网卡的网络类型；若 Windows 弹出提示，请勾选实际网络类型，显示“公用网络”时必须勾选。",
                ConsentFirewallNoticeStatus.Attention,
                BuildModernFirewallDetails(assessment));
        }

        if (assessment.RiskLevel == FirewallRiskLevel.None)
            return null;

        if (assessment.HasMatchingPortAllow)
        {
            return new ConsentFirewallNotice(
                "防火墙许可需要确认",
                "已发现 UDP/67 端口允许规则，但未确认它覆盖当前程序路径。",
                ConsentFirewallNoticeStatus.Attention,
                BuildModernFirewallDetails(assessment));
        }

        return new ConsentFirewallNotice(
            "防火墙许可需要确认",
            "未找到覆盖当前程序的 UDP/67 入站允许规则；若 Windows 弹出提示，请勾选所选网卡当前网络类型，显示“公用网络”时必须勾选。",
            ConsentFirewallNoticeStatus.Attention,
            BuildModernFirewallDetails(assessment));
    }

    private static IReadOnlyList<string> BuildModernRestoreSummary(AdapterOriginalConfig originalConfig)
    {
        var summary = new List<string>
        {
            "已记录这块网卡原有的 IPv4 和 DNS 设置；正常退出时，工具会停止 DHCP 并按记录恢复。"
        };

        if (originalConfig.DhcpEnabled)
        {
            summary.Add("原 IPv4 为自动获取；恢复后会重新获取租约，地址可能不同。" +
                (originalConfig.DnsServersFromDhcp ? "" : " DNS 按记录恢复。"));
        }
        else
        {
            summary.Add("原静态 IPv4、网关和 DNS 将按记录写回。");
        }

        summary.Add("工具不会修改 BMC 的网络设置。");
        return summary;
    }

    private static string BuildModernRestoreDetails(AdapterOriginalConfig originalConfig)
    {
        var mode = originalConfig.DhcpEnabled ? "DHCP 自动获取" : "静态 IPv4";
        var addresses = originalConfig.DhcpEnabled
            ? "当前观测：" + FormatAddresses(originalConfig)
            : FormatAddresses(originalConfig);
        var gateways = FormatGatewaysWithMetrics(originalConfig);
        var dns = FormatDns(originalConfig);
        var scope = originalConfig.DhcpEnabled
            ? "DHCP 当前租约仅供展示；恢复后重新获取的地址、网关和自动 DNS 可能不同。" + Environment.NewLine +
              "IPv6、VPN、额外静态路由等未记录项目不在恢复范围内。"
            : "IPv6、VPN、额外静态路由等未记录项目不在恢复范围内。";

        return "IPv4 模式：" + mode + Environment.NewLine +
            "IPv4 地址：" + addresses + Environment.NewLine +
            "网关：" + gateways + Environment.NewLine +
            "DNS：" + dns + Environment.NewLine +
            scope;
    }

    private static string FormatGatewaysWithMetrics(AdapterOriginalConfig originalConfig)
    {
        if (originalConfig.Gateways.Count == 0)
            return "无";

        return string.Join("、", originalConfig.Gateways.Select((gateway, index) =>
            gateway + (index < originalConfig.GatewayMetrics.Count
                ? "（跃点 " + originalConfig.GatewayMetrics[index] + "）"
                : "")));
    }

    private static string BuildModernFirewallDetails(FirewallAssessment? assessment)
    {
        if (assessment is null)
            return "本次未取得防火墙检查结果。";

        var warning = assessment.BuildConsentWarning();
        var diagnostic = assessment.ToDiagnosticText();
        if (string.IsNullOrWhiteSpace(warning))
            warning = "未发现明显的防火墙阻断证据。";

        return warning + Environment.NewLine + Environment.NewLine + diagnostic;
    }
}

internal sealed class ConsentSection
{
    public ConsentSection(string header, IReadOnlyList<string> items)
    {
        Header = header;
        Items = items;
    }

    public string Header { get; }
    public IReadOnlyList<string> Items { get; }
}

internal enum ConsentLayoutKind
{
    Flat,
    Sectioned,
    NetworkChangeSummary
}

internal enum ConsentFirewallNoticeStatus
{
    Attention,
    Impact
}

internal sealed class ConsentFirewallNotice
{
    public ConsentFirewallNotice(
        string title,
        string description,
        ConsentFirewallNoticeStatus status,
        string details)
    {
        Title = title;
        Description = description;
        Status = status;
        Details = details;
    }

    public string Title { get; }
    public string Description { get; }
    public ConsentFirewallNoticeStatus Status { get; }
    public string Details { get; }
}

internal sealed class ConsentValueRow
{
    public ConsentValueRow(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
}

internal sealed class ModernNetworkChangeContent
{
    public ModernNetworkChangeContent(
        ConsentFirewallNotice? firewallNotice,
        IReadOnlyList<ConsentValueRow> changeRows,
        IReadOnlyList<string> restoreSummary,
        string restoreDetails)
    {
        FirewallNotice = firewallNotice;
        ChangeRows = changeRows;
        RestoreSummary = restoreSummary;
        RestoreDetails = restoreDetails;
    }

    public ConsentFirewallNotice? FirewallNotice { get; }
    public bool HasFirewallNotice => FirewallNotice != null;
    public IReadOnlyList<ConsentValueRow> ChangeRows { get; }
    public IReadOnlyList<string> RestoreSummary { get; }
    public string RestoreDetails { get; }
}
}
