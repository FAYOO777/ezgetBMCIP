using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    public static class NetworkConfigManager
    {
        public static Action<string> Logger { get; set; }
        private static readonly Encoding NativeProcessEncoding =
            Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

        public static List<WiredAdapter> GetWiredAdapters()
        {
            var adapters = GetPhysicalEthernetAdaptersFromWmi();
            if (adapters.Count > 0)
            {
                return adapters;
            }

            return GetPhysicalEthernetAdaptersFallback();
        }

        public static AdapterOriginalConfig CaptureOriginalConfig(WiredAdapter adapter)
        {
            var ni = FindNetworkInterface(adapter);
            if (ni == null)
            {
                throw new InvalidOperationException("Selected adapter was not found.");
            }

            var props = ni.GetIPProperties();
            var fallbackDhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
            string dhcpModeSource;
            var dhcpEnabled = GetDhcpEnabled(adapter, fallbackDhcpEnabled, out dhcpModeSource);
            if (Logger != null)
                Logger("IPv4 mode captured from " + dhcpModeSource + ": dhcp=" + dhcpEnabled);
            var config = new AdapterOriginalConfig
            {
                DhcpEnabled = dhcpEnabled,
                DnsServersFromDhcp = IsDnsAutomatic(adapter, dhcpEnabled)
            };

            foreach (var addr in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                if (addr.IPv4Mask != null)
                {
                    config.StaticAddresses.Add((addr.Address, addr.IPv4Mask));
                }
            }

            foreach (var gateway in props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                config.Gateways.Add(gateway.Address);
            }

            if (!dhcpEnabled)
            {
                config.GatewayMetrics.AddRange(ReadGatewayMetrics(adapter));
            }

            foreach (var dns in props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork))
            {
                config.DnsServers.Add(dns);
            }

            return config;
        }

        public static Task ForceDhcpAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken)
        {
            return ForceDhcpBestEffortAsync(adapter, config, cancellationToken);
        }

        public static async Task ForceDhcpBestEffortAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken, bool releaseToolLease = false)
        {
            var details = await RestoreDhcpAndCollectLogAsync(adapter, config, cancellationToken, releaseToolLease);
            var expected = AdapterOriginalConfig.CreateDhcp();
            var consecutiveSuccesses = 0;
            for (var i = 0; i < 12; i++)
            {
                var verification = await VerifyOriginalConfigAsync(
                    adapter, expected, config, cancellationToken, releaseToolLease);
                details += Environment.NewLine + "verify " + (i + 1) + ": " + verification.Details;
                if (Logger != null)
                    Logger("DHCP restore verify " + (i + 1) + ": " + verification.Details);

                if (verification.IsSuccess)
                {
                    consecutiveSuccesses++;
                    if (consecutiveSuccesses >= 2)
                    {
                        if (Logger != null)
                            Logger("DHCP restore verified OK with two consecutive live checks");
                        return;
                    }
                }
                else
                {
                    consecutiveSuccesses = 0;
                }

                await Task.Delay(1000, cancellationToken);
            }

            throw new InvalidOperationException("Failed to restore adapter to DHCP." + Environment.NewLine + details);
        }

        public static async Task SetStaticForToolAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken)
        {
            await RunNetshAsync(
                "interface ipv4 set address name=\"" + adapter.Name + "\" static " + config.ServerIp + " " + config.Mask,
                cancellationToken);
            await ClearDnsForToolAsync(adapter, cancellationToken);
        }

        public static async Task RestoreOriginalConfigAsync(WiredAdapter adapter, AdapterOriginalConfig origConfig, SubnetConfig subnetConfig, CancellationToken cancellationToken)
        {
            var details = new List<string>();
            if (origConfig.DhcpEnabled)
            {
                details.Add(await RestoreDhcpAndCollectLogAsync(adapter, subnetConfig, cancellationToken, true));
            }
            else if (origConfig.StaticAddresses.Count > 0)
            {
                await RunNetshAsync(
                    BuildStaticAddressRestoreCommand(adapter, origConfig),
                    cancellationToken);

                for (var i = 1; i < origConfig.StaticAddresses.Count; i++)
                {
                    var item = origConfig.StaticAddresses[i];
                    await RunNetshAsync(
                        "interface ipv4 add address name=\"" + adapter.Name + "\" " + item.Address + " " + item.Mask,
                        cancellationToken);
                }

                for (var i = 1; i < origConfig.Gateways.Count; i++)
                {
                    await RunNetshAsync(
                        "interface ipv4 add route prefix=0.0.0.0/0 interface=\"" + adapter.Name + "\" nexthop=" + origConfig.Gateways[i] +
                        (i < origConfig.GatewayMetrics.Count ? " metric=" + origConfig.GatewayMetrics[i] : "") +
                        " store=persistent",
                        cancellationToken);
                }
            }
            else
            {
                await RunNetshAsync(
                    "interface ipv4 set address name=\"" + adapter.Name + "\" source=static",
                    cancellationToken);
            }

            if (!origConfig.DhcpEnabled)
            {
                RestoreGatewayMetrics(adapter, origConfig.GatewayMetrics);
            }

            if (origConfig.DnsServersFromDhcp || origConfig.DnsServers.Count == 0)
            {
                await RunNetshAsync("interface ipv4 set dnsservers name=\"" + adapter.Name + "\" source=dhcp", cancellationToken);
            }
            else
            {
                await RunNetshAsync(
                    "interface ipv4 set dnsservers name=\"" + adapter.Name + "\" static " + origConfig.DnsServers[0] + " primary",
                    cancellationToken);

                for (var i = 1; i < origConfig.DnsServers.Count; i++)
                {
                    await RunNetshAsync(
                        "interface ipv4 add dnsservers name=\"" + adapter.Name + "\" " + origConfig.DnsServers[i] + " index=" + (i + 1),
                        cancellationToken);
                }
            }

            var consecutiveSuccesses = 0;
            for (var i = 0; i < 12; i++)
            {
                var verification = await VerifyOriginalConfigAsync(adapter, origConfig, subnetConfig, cancellationToken, true);
                details.Add("verify " + (i + 1) + ": " + verification.Details);
                if (Logger != null)
                    Logger("Original config restore verify " + (i + 1) + ": " + verification.Details);

                if (verification.IsSuccess)
                {
                    consecutiveSuccesses++;
                    if (consecutiveSuccesses >= 2)
                    {
                        if (Logger != null)
                            Logger("Original network configuration restored and verified");
                        return;
                    }
                }
                else
                {
                    consecutiveSuccesses = 0;
                }

                await Task.Delay(1000, cancellationToken);
            }

            throw new InvalidOperationException(
                "Failed to restore the original adapter configuration." + Environment.NewLine +
                string.Join(Environment.NewLine, details));
        }

        public static Task<bool> IsLinkUpAsync(WiredAdapter adapter, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var escapedName = adapter.Name.Replace("'", "''");
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionID = '" + escapedName + "'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var status = obj["NetConnectionStatus"];
                            if (status != null && System.Convert.ToInt32(status) == 2)
                                return true;
                        }
                    }
                }
                catch { }
                return false;
            }, cancellationToken);
        }

        public static Task<bool> IsDhcpEnabledAsync(WiredAdapter adapter, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CaptureOriginalConfig(adapter).DhcpEnabled;
            }, cancellationToken);
        }

        public static async Task<NetworkConfigVerification> VerifyOriginalConfigAsync(
            WiredAdapter adapter,
            AdapterOriginalConfig expected,
            SubnetConfig subnetConfig,
            CancellationToken cancellationToken,
            bool requireToolLeaseRemoved = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await Task.Run(() => CaptureOriginalConfig(adapter), cancellationToken);
            var modeMatches = current.DhcpEnabled == expected.DhcpEnabled;
            var expectedAddresses = new HashSet<string>(
                expected.StaticAddresses.Select(a => a.Address.ToString()), StringComparer.OrdinalIgnoreCase);
            var currentAddresses = new HashSet<string>(
                current.StaticAddresses.Select(a => a.Address.ToString()), StringComparer.OrdinalIgnoreCase);
            var toolStaticRemoved = expectedAddresses.Contains(subnetConfig.ServerIp)
                || !currentAddresses.Contains(subnetConfig.ServerIp);
            var toolLeaseRemoved = !requireToolLeaseRemoved
                || expectedAddresses.Contains(subnetConfig.PoolStart)
                || !currentAddresses.Contains(subnetConfig.PoolStart);
            var toolAddressesRemoved = toolStaticRemoved && toolLeaseRemoved;
            var addressesMatch = expected.DhcpEnabled
                || IpSetEquals(current.StaticAddresses.Select(a => a.Address), expected.StaticAddresses.Select(a => a.Address));
            var gatewayAddressesMatch = expected.DhcpEnabled
                || IpSetEquals(current.Gateways, expected.Gateways);
            var gatewayMetricsMatch = GatewayMetricsMatch(
                current.GatewayMetrics,
                expected.GatewayMetrics,
                expected.DhcpEnabled);
            var gatewaysMatch = gatewayAddressesMatch && gatewayMetricsMatch;
            var dnsModeMatches = current.DnsServersFromDhcp == expected.DnsServersFromDhcp;
            var dnsServersMatch = expected.DnsServersFromDhcp
                || IpSetEquals(current.DnsServers, expected.DnsServers);
            var dnsMatches = dnsModeMatches && dnsServersMatch;
            var success = modeMatches && addressesMatch && gatewaysMatch && dnsMatches && toolAddressesRemoved;
            var details =
                "success=" + success + ", mode=" + current.DhcpEnabled + "/" + expected.DhcpEnabled +
                ", addresses=" + addressesMatch + ", gateways=" + gatewayAddressesMatch +
                ", gatewayMetrics=" + gatewayMetricsMatch +
                ", gatewayMetricsCurrent=[" + string.Join(",", current.GatewayMetrics) + "]" +
                ", gatewayMetricsExpected=[" + string.Join(",", expected.GatewayMetrics) + "]" +
                ", dnsMode=" + current.DnsServersFromDhcp + "/" + expected.DnsServersFromDhcp +
                ", dns=" + dnsServersMatch + ", toolStaticRemoved=" + toolStaticRemoved +
                ", toolLeaseRemoved=" + toolLeaseRemoved;

            return new NetworkConfigVerification
            {
                IsSuccess = success,
                ModeMatches = modeMatches,
                AddressesMatch = addressesMatch,
                GatewaysMatch = gatewaysMatch,
                DnsMatches = dnsMatches,
                ToolAddressesRemoved = toolAddressesRemoved,
                Details = details
            };
        }

        public static async Task<bool> HasToolStaticIpAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken)
        {
            return await HasAdapterIpAsync(adapter, config.ServerIp, cancellationToken);
        }

        public static async Task<bool> HasToolLeaseIpAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await RunProcessAsync("netsh.exe",
                    "interface ipv4 show addresses \"" + adapter.Name + "\"", cancellationToken, false);
                return result.Contains(config.PoolStart)
                    || Regex.IsMatch(result, @"\b\d{1,3}\.77\.77\.100\b");
            }
            catch { }
            return false;
        }

        private static async Task<bool> HasAdapterIpAsync(WiredAdapter adapter, string ipAddress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await RunProcessAsync("netsh.exe",
                    "interface ipv4 show addresses \"" + adapter.Name + "\"", cancellationToken, false);
                return result.Contains(ipAddress);
            }
            catch { }
            return false;
        }

        private static async Task<string> RestoreDhcpAndCollectLogAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken, bool releaseToolLease)
        {
            var outputs = new List<string>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{" + adapter.Id + "}'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            var result = obj.InvokeMethod("EnableDHCP", null);
                            AddRestoreOutput(outputs, "WMI EnableDHCP: " + (result?.ToString() ?? "null"));
                        }
                        catch (Exception ex)
                        {
                            AddRestoreOutput(outputs, "WMI EnableDHCP err: " + ex.Message);
                        }

                        try
                        {
                            obj.InvokeMethod("SetDNSServerSearchOrder", new object[] { new string[0] });
                            AddRestoreOutput(outputs, "WMI SetDNSServerSearchOrder: OK");
                        }
                        catch (Exception ex)
                        {
                            AddRestoreOutput(outputs, "WMI SetDNSServerSearchOrder err: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddRestoreOutput(outputs, "WMI DHCP restore error: " + ex.Message);
            }

            var commands = new[]
            {
                "interface ipv4 set address name=\"" + adapter.Name + "\" source=dhcp",
                "interface ip set address name=\"" + adapter.Name + "\" source=dhcp",
                "interface ipv4 set dnsservers name=\"" + adapter.Name + "\" source=dhcp",
                "interface ip set dns name=\"" + adapter.Name + "\" source=dhcp"
            };

            foreach (var command in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var output = await RunProcessAsync("netsh.exe", command, cancellationToken, false);
                    if (Logger != null)
                        Logger("DHCP restore netsh: " + command + " -> " + output.Trim());
                    outputs.Add("> netsh " + command + Environment.NewLine + output.Trim());
                }
                catch (Exception ex)
                {
                    if (Logger != null)
                        Logger("DHCP restore netsh: " + command + " ERR: " + ex.Message);
                    outputs.Add("> netsh " + command + " ERR: " + ex.Message);
                }
            }

            var deleteCommands = new[]
            {
                "interface ipv4 delete address name=\"" + adapter.Name + "\" addr=" + config.ServerIp,
                "interface ip delete address name=\"" + adapter.Name + "\" addr=" + config.ServerIp
            };

            foreach (var command in deleteCommands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var output = await RunProcessAsync("netsh.exe", command, cancellationToken, false);
                    if (Logger != null)
                        Logger("DHCP restore netsh: " + command + " -> " + output.Trim());
                    outputs.Add("> netsh " + command + Environment.NewLine + output.Trim());
                }
                catch (Exception ex)
                {
                    if (Logger != null)
                        Logger("DHCP restore netsh: " + command + " ERR: " + ex.Message);
                    outputs.Add("> netsh " + command + " ERR: " + ex.Message);
                }
            }

            if (releaseToolLease)
            {
                var releaseCommand = "/release \"" + adapter.Name + "\"";
                try
                {
                    var output = await RunProcessAsync("ipconfig.exe", releaseCommand, cancellationToken, false);
                    if (Logger != null)
                        Logger("DHCP restore ipconfig: " + releaseCommand + " -> " + output.Trim());
                    outputs.Add("> ipconfig " + releaseCommand + Environment.NewLine + output.Trim());
                }
                catch (Exception ex)
                {
                    if (Logger != null)
                        Logger("DHCP restore ipconfig: " + releaseCommand + " ERR: " + ex.Message);
                    outputs.Add("> ipconfig " + releaseCommand + " ERR: " + ex.Message);
                }
            }

            if (releaseToolLease)
            {
                ResetRegistryLeaseCache(adapter, outputs);
                await Task.Delay(800, cancellationToken);
                ResetRegistryLeaseCache(adapter, outputs);
            }

            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, true, out keyPath))
                {
                    if (key != null)
                    {
                        AddRestoreOutput(outputs, "Registry path: " + keyPath);
                        if (releaseToolLease)
                        {
                            ResetDhcpLeaseCache(key, outputs);
                        }

                        key.SetValue("EnableDHCP", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        key.SetValue("IPAddress", new[] { "0.0.0.0" }, Microsoft.Win32.RegistryValueKind.MultiString);
                        key.SetValue("SubnetMask", new[] { "0.0.0.0" }, Microsoft.Win32.RegistryValueKind.MultiString);
                        key.SetValue("DefaultGateway", new string[0], Microsoft.Win32.RegistryValueKind.MultiString);
                        key.SetValue("NameServer", "", Microsoft.Win32.RegistryValueKind.String);
                        AddRestoreOutput(outputs, "Registry reset: OK");
                    }
                    else
                    {
                        AddRestoreOutput(outputs, "Registry path not found. Tried: " + keyPath);
                    }
                }
            }
            catch (Exception ex)
            {
                AddRestoreOutput(outputs, "Registry reset error: " + ex.Message);
            }

            return string.Join(Environment.NewLine, outputs);
        }

        private static void ResetRegistryLeaseCache(WiredAdapter adapter, List<string> outputs)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, true, out keyPath))
                {
                    if (key == null)
                    {
                        AddRestoreOutput(outputs, "Registry lease cache path not found. Tried: " + keyPath);
                        return;
                    }

                    AddRestoreOutput(outputs, "Registry lease cache path: " + keyPath);
                    ResetDhcpLeaseCache(key, outputs);
                }
            }
            catch (Exception ex)
            {
                AddRestoreOutput(outputs, "Registry lease cache reset error: " + ex.Message);
            }
        }

        private static bool HasRegistryToolLease(WiredAdapter adapter)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, false, out keyPath))
                {
                    if (key == null)
                    {
                        Logger?.Invoke("DHCP restore registry verify: path not found. Tried: " + keyPath);
                        return false;
                    }

                    var dhcpIp = RegistryValueToText(key.GetValue("DhcpIPAddress"));
                    var dhcpServer = RegistryValueToText(key.GetValue("DhcpServer"));
                    var dhcpGateway = RegistryValueToText(key.GetValue("DhcpDefaultGateway"));
                    var dhcpDns = RegistryValueToText(key.GetValue("DhcpNameServer"));
                    var dhcpOptions = RegistryValueToText(key.GetValue("DhcpInterfaceOptions"));
                    var dhcpMaskOpt = RegistryValueToText(key.GetValue("DhcpSubnetMaskOpt"));
                    var text = "DhcpIPAddress=" + dhcpIp
                        + ", DhcpServer=" + dhcpServer
                        + ", DhcpDefaultGateway=" + dhcpGateway
                        + ", DhcpNameServer=" + dhcpDns
                        + ", DhcpInterfaceOptions=" + dhcpOptions
                        + ", DhcpSubnetMaskOpt=" + dhcpMaskOpt;

                    var stale = IsLeaseValuePresent(dhcpIp)
                        || IsLeaseValuePresent(dhcpServer)
                        || IsLeaseValuePresent(dhcpGateway)
                        || IsLeaseValuePresent(dhcpDns)
                        || IsLeaseValuePresent(dhcpMaskOpt)
                        || !string.IsNullOrWhiteSpace(dhcpOptions);
                    Logger?.Invoke("DHCP restore registry verify: path=" + keyPath + ", stale=" + stale + ", values=" + text);
                    return stale;
                }
            }
            catch (Exception ex)
            {
                Logger?.Invoke("DHCP restore registry verify error: " + ex.Message);
                return false;
            }
        }

        private static Microsoft.Win32.RegistryKey OpenAdapterRegistryKey(WiredAdapter adapter, bool writable, out string attemptedPaths)
        {
            var candidates = GetAdapterRegistryKeyNames(adapter)
                .Select(name => @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in candidates)
            {
                var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable);
                if (key != null)
                {
                    attemptedPaths = path;
                    return key;
                }
            }

            attemptedPaths = string.Join("; ", candidates);
            return null;
        }

        private static IEnumerable<string> GetAdapterRegistryKeyNames(WiredAdapter adapter)
        {
            var resolvedId = FindNetworkInterface(adapter)?.Id;
            foreach (var raw in new[] { adapter.Id, resolvedId })
            {
                var value = raw ?? "";
                var trimmed = TrimBraces(value).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    yield return trimmed;
                    yield return "{" + trimmed + "}";
                    yield return trimmed.ToUpperInvariant();
                    yield return "{" + trimmed.ToUpperInvariant() + "}";
                }
            }
        }

        private static void ResetDhcpLeaseCache(Microsoft.Win32.RegistryKey key, List<string> outputs)
        {
            try
            {
                AddRestoreOutput(outputs,
                    "Registry DHCP lease before reset: DhcpIPAddress=" + RegistryValueToText(key.GetValue("DhcpIPAddress")) +
                    ", DhcpServer=" + RegistryValueToText(key.GetValue("DhcpServer")) +
                    ", DhcpDefaultGateway=" + RegistryValueToText(key.GetValue("DhcpDefaultGateway")) +
                    ", DhcpNameServer=" + RegistryValueToText(key.GetValue("DhcpNameServer")));

                key.SetValue("DhcpIPAddress", "0.0.0.0", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DhcpSubnetMask", "0.0.0.0", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DhcpServer", "0.0.0.0", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DhcpDefaultGateway", new string[0], Microsoft.Win32.RegistryValueKind.MultiString);
                key.SetValue("DhcpNameServer", "", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DhcpDomain", "", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DhcpSubnetMaskOpt", new string[0], Microsoft.Win32.RegistryValueKind.MultiString);
                key.SetValue("DefaultGatewayMetric", new string[0], Microsoft.Win32.RegistryValueKind.MultiString);
                DeleteRegistryValue(key, "Lease");
                DeleteRegistryValue(key, "LeaseObtainedTime");
                DeleteRegistryValue(key, "LeaseTerminatesTime");
                DeleteRegistryValue(key, "T1");
                DeleteRegistryValue(key, "T2");
                DeleteRegistryValue(key, "DhcpInterfaceOptions");
                DeleteRegistryValue(key, "DhcpGatewayHardware");
                DeleteRegistryValue(key, "DhcpGatewayHardwareCount");
                AddRestoreOutput(outputs, "Registry DHCP lease reset: OK");
            }
            catch (Exception ex)
            {
                AddRestoreOutput(outputs, "Registry DHCP lease reset error: " + ex.Message);
            }
        }

        private static void DeleteRegistryValue(Microsoft.Win32.RegistryKey key, string name)
        {
            try
            {
                key.DeleteValue(name, false);
            }
            catch { }
        }

        private static string RegistryValueToText(object value)
        {
            if (value == null)
                return "";
            var values = value as string[];
            if (values != null)
                return string.Join(",", values);
            var bytes = value as byte[];
            if (bytes != null)
                return BitConverter.ToString(bytes).Replace("-", "");
            return value.ToString();
        }

        private static bool IsLeaseValuePresent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();
            if (parts.Count == 0)
                return false;
            return parts.Any(v =>
                !v.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
                && !v.Equals("255.255.255.255", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddRestoreOutput(List<string> outputs, string message)
        {
            outputs.Add(message);
            if (Logger != null)
                Logger("DHCP restore: " + message);
        }

        private static NetworkInterface FindNetworkInterface(WiredAdapter adapter)
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    SameAdapterId(n.Id, adapter.Id)
                    || n.Name.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase)
                    || n.Description.Equals(adapter.Description, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDnsAutomatic(WiredAdapter adapter, bool fallback)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, false, out keyPath))
                {
                    if (key == null)
                        return fallback;
                    return string.IsNullOrWhiteSpace(RegistryValueToText(key.GetValue("NameServer")));
                }
            }
            catch (Exception ex)
            {
                if (Logger != null)
                    Logger("DNS mode detection failed: " + ex.Message);
                return fallback;
            }
        }

        private static bool GetDhcpEnabled(WiredAdapter adapter, bool fallback, out string source)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, false, out keyPath))
                {
                    if (key != null)
                    {
                        var value = TryReadDhcpEnabled(key.GetValue("EnableDHCP"));
                        if (value.HasValue)
                        {
                            source = "registry EnableDHCP";
                            return value.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Logger != null)
                    Logger("DHCP mode detection failed: " + ex.Message);
            }

            source = "NetworkInterface fallback";
            return fallback;
        }

        internal static bool? TryReadDhcpEnabled(object value)
        {
            if (value is int)
                return (int)value != 0;
            if (value is long)
                return (long)value != 0;
            if (value is uint)
                return (uint)value != 0;

            var text = value as string;
            int number;
            if (text != null && int.TryParse(text, out number))
                return number != 0;

            return null;
        }

        private static IEnumerable<int> ReadGatewayMetrics(WiredAdapter adapter)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, false, out keyPath))
                {
                    if (key == null)
                        return new int[0];
                    return RegistryValueToText(key.GetValue("DefaultGatewayMetric"))
                        .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(value =>
                        {
                            int metric;
                            return int.TryParse(value, out metric) ? (int?)metric : null;
                        })
                        .Where(metric => metric.HasValue && metric.Value >= 0)
                        .Select(metric => metric.Value)
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                if (Logger != null)
                    Logger("Gateway metric detection failed: " + ex.Message);
                return new int[0];
            }
        }

        internal static string BuildStaticAddressRestoreCommand(
            WiredAdapter adapter,
            AdapterOriginalConfig originalConfig)
        {
            var primary = originalConfig.StaticAddresses[0];
            var command =
                "interface ipv4 set address name=\"" + adapter.Name + "\" source=static " +
                "address=" + primary.Address + " mask=" + primary.Mask;

            if (originalConfig.Gateways.Count == 0)
                return command + " gateway=none";

            command += " gateway=" + originalConfig.Gateways[0];
            if (originalConfig.GatewayMetrics.Count > 0)
                command += " gwmetric=" + originalConfig.GatewayMetrics[0].ToString(CultureInfo.InvariantCulture);

            return command;
        }

        internal static bool GatewayMetricsMatch(
            IReadOnlyCollection<int> current,
            IReadOnlyCollection<int> expected,
            bool expectedDhcpEnabled)
        {
            return expectedDhcpEnabled
                || expected.Count == 0
                || current.SequenceEqual(expected);
        }

        private static void RestoreGatewayMetrics(WiredAdapter adapter, IReadOnlyCollection<int> gatewayMetrics)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, true, out keyPath))
                {
                    if (key == null)
                    {
                        if (Logger != null)
                            Logger("Gateway metric restore skipped because the adapter registry key was not found. Tried: " + keyPath);
                        return;
                    }

                    var values = gatewayMetrics
                        .Select(metric => metric.ToString(CultureInfo.InvariantCulture))
                        .ToArray();
                    key.SetValue("DefaultGatewayMetric", values, Microsoft.Win32.RegistryValueKind.MultiString);
                    if (Logger != null)
                        Logger("Gateway metric restore persisted: [" + string.Join(",", values) + "]");
                }
            }
            catch (Exception ex)
            {
                if (Logger != null)
                    Logger("Gateway metric restore failed: " + ex.Message);
            }
        }

        private static bool IpSetEquals(IEnumerable<IPAddress> current, IEnumerable<IPAddress> expected)
        {
            return new HashSet<string>(current.Select(ip => ip.ToString()), StringComparer.OrdinalIgnoreCase)
                .SetEquals(expected.Select(ip => ip.ToString()));
        }

        private static async Task ClearDnsForToolAsync(WiredAdapter adapter, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{" + adapter.Id + "}'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            obj.InvokeMethod("SetDNSServerSearchOrder", new object[] { new string[0] });
                            if (Logger != null)
                                Logger("Clear DNS WMI SetDNSServerSearchOrder: OK");
                        }
                        catch (Exception ex)
                        {
                            if (Logger != null)
                                Logger("Clear DNS WMI SetDNSServerSearchOrder err: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Logger != null)
                    Logger("Clear DNS WMI error: " + ex.Message);
            }

            var commands = new[]
            {
                "interface ipv4 set dnsservers name=\"" + adapter.Name + "\" source=dhcp",
                "interface ip set dns name=\"" + adapter.Name + "\" source=dhcp",
                "interface ipv4 set dnsservers name=\"" + adapter.Name + "\" static none"
            };

            foreach (var command in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = await RunProcessAsync("netsh.exe", command, cancellationToken, false);
                if (Logger != null)
                    Logger("Clear DNS netsh: " + command + " -> " + output.Trim());
            }
        }

        private static List<WiredAdapter> GetPhysicalEthernetAdaptersFromWmi()
        {
            var adapters = new List<WiredAdapter>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT NetConnectionID, Name, GUID, MACAddress FROM Win32_NetworkAdapter " +
                    "WHERE PhysicalAdapter = TRUE AND NetConnectionID IS NOT NULL"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var netConnId = obj["NetConnectionID"]?.ToString();
                        var name = obj["Name"]?.ToString();
                        var guid = obj["GUID"]?.ToString();
                        var mac = obj["MACAddress"]?.ToString();

                        if (string.IsNullOrWhiteSpace(netConnId))
                            continue;
                        var desc = name ?? "";
                        if (!LooksLikeWiredOnly(netConnId) || !LooksLikeWiredOnly(desc))
                            continue;

                        adapters.Add(new WiredAdapter(
                            netConnId,
                            desc,
                            TrimBraces(guid ?? ""),
                            (mac ?? "").Replace(":", "")));
                    }
                }
            }
            catch { }
            return adapters;
        }

        private static List<WiredAdapter> GetPhysicalEthernetAdaptersFallback()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n =>
                    (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                     || n.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT
                     || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                    && !string.IsNullOrWhiteSpace(n.Name)
                    && n.GetPhysicalAddress().GetAddressBytes().Length == 6
                    && !LooksLikeVirtualOrFilterAdapter(n.Name)
                    && !LooksLikeVirtualOrFilterAdapter(n.Description))
                .Select(n => new WiredAdapter(
                    n.Name,
                    n.Description,
                    n.Id,
                    n.GetPhysicalAddress().ToString()))
                .OrderBy(n => n.Name)
                .ToList();
        }

        private static bool LooksLikeVirtualOrFilterAdapter(string value)
        {
            return !LooksLikeWiredOnly(value);
        }

        private static bool LooksLikeWiredOnly(string value)
        {
            var text = value.ToLowerInvariant();
            var blocked = new[]
            {
                "virtual",
                "vpn",
                "vnic",
                "tap",
                "tun",
                "loopback",
                "qos packet scheduler",
                "wfp",
                "filter",
                "miniport",
                "hyper-v",
                "vmware",
                "virtualbox",
                "npcap",
                "wireless",
                "wifi",
                "wi-fi",
                "bluetooth",
                "802.11",
                "wintun",
                "tunnel",
                "wireguard",
                "sangfor",
                "atrust",
                "ppp",
                "wan miniport"
            };

            return !blocked.Any(text.Contains);
        }

        private static string TrimBraces(string value)
        {
            return value.Trim('{', '}');
        }

        private static bool SameAdapterId(string left, string right)
        {
            return TrimBraces(left).Equals(TrimBraces(right), StringComparison.OrdinalIgnoreCase);
        }

        private static Task<string> RunNetshAsync(string arguments, CancellationToken cancellationToken)
        {
            return RunProcessAsync("netsh.exe", arguments, cancellationToken, true);
        }

        private static async Task<string> RunProcessAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken,
            bool throwOnError)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = NativeProcessEncoding,
                    StandardErrorEncoding = NativeProcessEncoding
                },
                EnableRaisingEvents = true
            })
            {
                var exitTcs = new TaskCompletionSource<int>();
                process.Exited += (s, e) => exitTcs.TrySetResult(process.ExitCode);

                process.Start();
                var stdoutTask = ProcessOutputDecoder.ReadAllBytesAsync(process.StandardOutput.BaseStream);
                var stderrTask = ProcessOutputDecoder.ReadAllBytesAsync(process.StandardError.BaseStream);

                using (cancellationToken.Register(() =>
                {
                    try { process.Kill(); } catch { }
                    exitTcs.TrySetCanceled();
                }))
                {
                    await Task.WhenAny(exitTcs.Task, Task.Delay(30000, CancellationToken.None));
                }

                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                    throw new Win32Exception(258);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var stdout = ProcessOutputDecoder.Decode(await stdoutTask, NativeProcessEncoding);
                var stderr = ProcessOutputDecoder.Decode(await stderrTask, NativeProcessEncoding);

                if (throwOnError && process.ExitCode != 0)
                {
                    throw new InvalidOperationException(fileName + " " + arguments + "\r\n" + stderr + "\r\n" + stdout);
                }

                return stdout + stderr;
            }
        }
    }
}
