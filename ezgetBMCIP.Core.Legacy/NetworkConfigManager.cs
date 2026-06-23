using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    public static class NetworkConfigManager
    {
        public static Action<string> Logger { get; set; }

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
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    SameAdapterId(n.Id, adapter.Id)
                    || n.Name.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase)
                    || n.Description.Equals(adapter.Description, StringComparison.OrdinalIgnoreCase));
            if (ni == null)
            {
                throw new InvalidOperationException("Selected adapter was not found.");
            }

            var props = ni.GetIPProperties();
            var config = new AdapterOriginalConfig
            {
                DhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false
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
            for (var i = 0; i < 10; i++)
            {
                var dhcpEnabled = await IsDhcpEnabledAsync(adapter, cancellationToken);
                var toolIpStillPresent = await HasToolStaticIpAsync(adapter, config, cancellationToken);
                var toolLeaseStillPresent = releaseToolLease && await HasToolLeaseIpAsync(adapter, config, cancellationToken);
                var registryLeaseStillPresent = releaseToolLease && HasRegistryToolLease(adapter);
                details += Environment.NewLine + "verify " + (i + 1) + ": dhcpEnabled=" + dhcpEnabled + ", toolIpStillPresent=" + toolIpStillPresent + ", toolLeaseStillPresent=" + toolLeaseStillPresent + ", registryLeaseStillPresent=" + registryLeaseStillPresent;
                if (Logger != null)
                    Logger("DHCP restore verify " + (i + 1) + ": dhcpEnabled=" + dhcpEnabled + ", toolIpStillPresent=" + toolIpStillPresent + ", toolLeaseStillPresent=" + toolLeaseStillPresent + ", registryLeaseStillPresent=" + registryLeaseStillPresent);

                if (dhcpEnabled && !toolIpStillPresent && !toolLeaseStillPresent && !registryLeaseStillPresent)
                {
                    if (Logger != null)
                        Logger("DHCP restore verified OK after " + (i + 1) + " attempt(s)");
                    return;
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
            await RunNetshAsync("interface ipv4 set dnsservers name=\"" + adapter.Name + "\" static none", cancellationToken);
        }

        public static async Task RestoreOriginalConfigAsync(WiredAdapter adapter, AdapterOriginalConfig origConfig, SubnetConfig subnetConfig, CancellationToken cancellationToken)
        {
            if (origConfig.DhcpEnabled || origConfig.StaticAddresses.Count == 0)
            {
                await ForceDhcpBestEffortAsync(adapter, subnetConfig, cancellationToken, releaseToolLease: true);
                return;
            }

            var primary = origConfig.StaticAddresses[0];
            var gatewayArg = origConfig.Gateways.Count > 0 ? origConfig.Gateways[0].ToString() : "none";
            await RunNetshAsync(
                "interface ipv4 set address name=\"" + adapter.Name + "\" static " + primary.Address + " " + primary.Mask + " " + gatewayArg,
                cancellationToken);

            for (var i = 1; i < origConfig.StaticAddresses.Count; i++)
            {
                var item = origConfig.StaticAddresses[i];
                await RunNetshAsync(
                    "interface ipv4 add address name=\"" + adapter.Name + "\" " + item.Address + " " + item.Mask,
                    cancellationToken);
            }

            if (origConfig.DnsServers.Count == 0)
            {
                await RunNetshAsync("interface ipv4 set dnsservers name=\"" + adapter.Name + "\" source=dhcp", cancellationToken);
                return;
            }

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
            return Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{" + adapter.Id + "}'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var dhcp = obj["DHCPEnabled"];
                            if (dhcp != null && System.Convert.ToBoolean(dhcp))
                                return true;
                        }
                    }
                }
                catch { }

                try
                {
                    var result = await RunProcessAsync("netsh.exe",
                        "interface ipv4 show addresses \"" + adapter.Name + "\"", cancellationToken, false);
                    if (result.IndexOf("DHCP", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                catch { }

                try
                {
                    string keyPath;
                    var key = OpenAdapterRegistryKey(adapter, false, out keyPath);
                    if (key != null)
                    {
                        var val = key.GetValue("EnableDHCP");
                        key.Close();
                        if (val != null && System.Convert.ToInt32(val) == 1)
                            return true;
                    }
                }
                catch { }

                return false;
            }, cancellationToken);
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
            var raw = adapter.Id ?? "";
            var trimmed = TrimBraces(raw).Trim();
            if (!string.IsNullOrWhiteSpace(raw))
                yield return raw.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
                yield return "{" + trimmed + "}";
                yield return trimmed.ToUpperInvariant();
                yield return "{" + trimmed.ToUpperInvariant() + "}";
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
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                },
                EnableRaisingEvents = true
            })
            {
                var exitTcs = new TaskCompletionSource<int>();
                process.Exited += (s, e) => exitTcs.TrySetResult(process.ExitCode);

                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

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

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (throwOnError && process.ExitCode != 0)
                {
                    throw new InvalidOperationException(fileName + " " + arguments + "\r\n" + stderr + "\r\n" + stdout);
                }

                return stdout + stderr;
            }
        }
    }
}
