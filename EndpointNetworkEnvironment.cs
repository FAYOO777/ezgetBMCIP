using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace EzGetBmcIp
{
    // Advisory only. The tool never stops or reconfigures third-party network software.
    internal static class EndpointNetworkEnvironment
    {
        private static readonly string[] TunnelMarkers =
        {
            "mihomo", "clash", "meta tunnel", "wintun", "wireguard", "tailscale",
            "openvpn", "tap-windows", "vpn", "zerotier"
        };

        internal static string GetPotentialInterferenceWarning()
        {
            try
            {
                var matches = new List<string>();
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    var text = (networkInterface.Name + " " + networkInterface.Description).ToLowerInvariant();
                    if (TunnelMarkers.Any(marker => text.Contains(marker)))
                        matches.Add(string.IsNullOrWhiteSpace(networkInterface.Name)
                            ? networkInterface.Description
                            : networkInterface.Name);
                }

                if (matches.Count == 0)
                    return string.Empty;

                return "检测到可能影响浏览器直连的代理/VPN/TUN 适配器：" +
                    string.Join("、", matches.Distinct(StringComparer.OrdinalIgnoreCase)) +
                    "。工具已直接验证管理服务响应；若浏览器仍打不开，请暂停相关软件或为本次直连网段设置绕过规则。";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
