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
        string confirmButtonText)
    {
        Title = title;
        Intro = intro;
        Items = items;
        AcknowledgementText = acknowledgementText;
        ConfirmButtonText = confirmButtonText;
    }

    public string Title { get; private set; }
    public string Intro { get; private set; }
    public IReadOnlyList<string> Items { get; private set; }
    public string AcknowledgementText { get; private set; }
    public string ConfirmButtonText { get; private set; }

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

    public static ConsentNotice CreateNetworkChange(
        WiredAdapter adapter,
        SubnetConfig subnetConfig,
        AdapterOriginalConfig originalConfig)
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
            "同意并开始");
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
}
}
