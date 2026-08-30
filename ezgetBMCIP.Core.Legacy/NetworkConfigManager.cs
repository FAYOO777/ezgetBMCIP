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

        public static WiredAdapter ResolveCurrentAdapter(WiredAdapter previousAdapter)
        {
            var adapters = GetWiredAdapters();
            var previousMac = NormalizeMac(previousAdapter.MacAddress);
            var resolved = adapters.FirstOrDefault(adapter =>
                SameAdapterId(adapter.Id, previousAdapter.Id)
                || (previousMac.Length > 0
                    && NormalizeMac(adapter.MacAddress).Equals(previousMac, StringComparison.OrdinalIgnoreCase)));
            if (resolved == null)
                throw new InvalidOperationException("The original adapter could not be found again by GUID or MAC address.");

            if (Logger != null)
                Logger("Recovery adapter re-enumerated: name=" + resolved.Name +
                    ", id=" + resolved.Id + ", mac=" + resolved.MacAddress);
            return resolved;
        }

        public static AdapterOriginalConfig CaptureOriginalConfig(WiredAdapter adapter)
        {
            var ni = FindNetworkInterface(adapter);
            if (ni == null)
            {
                throw new InvalidOperationException("Selected adapter was not found.");
            }

            var props = ni.GetIPProperties();
            var activeDhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
            string dhcpModeSource;
            var persistentDhcpEnabled = GetDhcpEnabled(adapter, activeDhcpEnabled, out dhcpModeSource);
            if (Logger != null)
                Logger("IPv4 mode captured: active=" + activeDhcpEnabled +
                    ", persistent=" + persistentDhcpEnabled +
                    ", persistentSource=" + dhcpModeSource);
            var config = new AdapterOriginalConfig
            {
                DhcpEnabled = persistentDhcpEnabled,
                ActiveDhcpEnabled = activeDhcpEnabled,
                PersistentDhcpEnabled = persistentDhcpEnabled,
                DnsServersFromDhcp = IsDnsAutomatic(adapter, persistentDhcpEnabled)
            };

            foreach (var addr in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                if (addr.IPv4Mask != null)
                {
                    var preserve = ShouldPreserveCapturedAddress(
                        addr.Address, addr.PrefixOrigin, addr.SuffixOrigin, persistentDhcpEnabled);
                    if (Logger != null)
                    {
                        Logger("IPv4 address observed: address=" + addr.Address +
                            ", mask=" + addr.IPv4Mask +
                            ", prefixOrigin=" + addr.PrefixOrigin +
                            ", suffixOrigin=" + addr.SuffixOrigin +
                            ", state=" + addr.DuplicateAddressDetectionState +
                            ", snapshot=" + preserve);
                    }
                    if (preserve)
                    {
                        config.StaticAddresses.Add(new AdapterIpv4Address(
                            addr.Address,
                            addr.IPv4Mask,
                            addr.PrefixOrigin,
                            addr.SuffixOrigin,
                            addr.DuplicateAddressDetectionState));
                    }
                    else if (Logger != null)
                    {
                        Logger("Automatic link-local IPv4 excluded from recovery snapshot: " +
                            addr.Address + " prefixOrigin=" + addr.PrefixOrigin +
                            " suffixOrigin=" + addr.SuffixOrigin);
                    }
                }
            }

            foreach (var gateway in props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                config.Gateways.Add(gateway.Address);
            }

            if (!persistentDhcpEnabled)
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
            var expected = new AdapterOriginalConfig
            {
                DhcpEnabled = false,
                ActiveDhcpEnabled = false,
                PersistentDhcpEnabled = false,
                DnsServersFromDhcp = true
            };
            expected.StaticAddresses.Add(new AdapterIpv4Address(
                IPAddress.Parse(config.ServerIp),
                IPAddress.Parse(config.Mask),
                PrefixOrigin.Manual,
                SuffixOrigin.Manual,
                DuplicateAddressDetectionState.Preferred));

            var details = new List<string>();
            await ApplyStaticPrimaryAsync(adapter, expected, cancellationToken);
            await ClearDnsForToolAsync(adapter, cancellationToken);

            var primary = await VerifyUntilStableAsync(
                adapter, expected, config, cancellationToken, 4,
                "Tool static primary verify", details, false);
            if (primary.IsSuccess)
                return;

            if (!NeedsStaticFallback(primary.LastVerification))
            {
                throw new InvalidOperationException(
                    "The adapter entered static mode, but the tool address or DNS state did not match." + Environment.NewLine +
                    string.Join(Environment.NewLine, details));
            }

            if (Logger != null)
                Logger("Tool static primary verification failed; invoking WMI EnableStatic fallback.");
            await ApplyStaticFallbackAsync(adapter, expected, cancellationToken);
            await ClearDnsForToolAsync(adapter, cancellationToken);
            var fallback = await VerifyUntilStableAsync(
                adapter, expected, config, cancellationToken, 8,
                "Tool static fallback verify", details, false);
            if (fallback.IsSuccess)
                return;

            throw new InvalidOperationException(
                "Failed to configure the local adapter with the tool static address." + Environment.NewLine +
                string.Join(Environment.NewLine, details));
        }

        public static async Task RestoreOriginalConfigAsync(WiredAdapter adapter, AdapterOriginalConfig origConfig, SubnetConfig subnetConfig, CancellationToken cancellationToken)
        {
            var details = new List<string>();
            if (origConfig.DhcpEnabled)
            {
                details.Add(await RestoreDhcpAndCollectLogAsync(adapter, subnetConfig, cancellationToken, true));
                var dhcpVerification = await VerifyUntilStableAsync(
                    adapter, origConfig, subnetConfig, cancellationToken, 12,
                    "Original DHCP restore verify", details, true);
                if (dhcpVerification.IsSuccess)
                {
                    if (Logger != null)
                        Logger("Original DHCP configuration restored and verified");
                    return;
                }
            }
            else if (origConfig.StaticAddresses.Count > 0)
            {
                await ApplyStaticPrimaryAsync(adapter, origConfig, cancellationToken);
                await RestoreDnsAsync(adapter, origConfig, cancellationToken);
                var primary = await VerifyUntilStableAsync(
                    adapter, origConfig, subnetConfig, cancellationToken, 4,
                    "Original static primary verify", details, true);
                if (primary.IsSuccess)
                {
                    if (Logger != null)
                        Logger("Original static configuration restored and verified by the primary path");
                    return;
                }

                if (!NeedsStaticFallback(primary.LastVerification))
                {
                    throw new InvalidOperationException(
                        "The adapter entered static mode, but the restored address, gateway, or DNS did not match." + Environment.NewLine +
                        string.Join(Environment.NewLine, details));
                }

                if (Logger != null)
                    Logger("Original static primary verification failed; invoking WMI EnableStatic fallback. Last=" +
                        (primary.LastVerification != null ? primary.LastVerification.Details : "none"));
                await ApplyStaticFallbackAsync(adapter, origConfig, cancellationToken);
                await RestoreDnsAsync(adapter, origConfig, cancellationToken);
                var fallback = await VerifyUntilStableAsync(
                    adapter, origConfig, subnetConfig, cancellationToken, 12,
                    "Original static fallback verify", details, true);
                if (fallback.IsSuccess)
                {
                    if (Logger != null)
                        Logger("Original static configuration restored and verified by the fallback path");
                    return;
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The original adapter was static but contained no restorable IPv4 address.");
            }

            throw new InvalidOperationException(
                "Failed to restore the original adapter configuration." + Environment.NewLine +
                string.Join(Environment.NewLine, details));
        }

        private static async Task ApplyStaticPrimaryAsync(
            WiredAdapter adapter,
            AdapterOriginalConfig config,
            CancellationToken cancellationToken)
        {
            var output = await RunPowerShellAsync(BuildDisableDhcpScript(adapter), cancellationToken, false);
            if (Logger != null)
                Logger("Static mode primary PS output: " + output.Trim());
            await ApplyStaticNetshAsync(adapter, config, cancellationToken);
        }

        private static async Task ApplyStaticFallbackAsync(
            WiredAdapter adapter,
            AdapterOriginalConfig config,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var addresses = config.StaticAddresses.Select(item => item.Address.ToString()).ToArray();
            var masks = config.StaticAddresses.Select(item => item.Mask.ToString()).ToArray();
            var fallbackResult = "WMI config not found";
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var settingId = TrimBraces(Convert.ToString(obj["SettingID"]));
                    if (!SameAdapterId(settingId, adapter.Id))
                        continue;
                    try
                    {
                        var input = obj.GetMethodParameters("EnableStatic");
                        input["IPAddress"] = addresses;
                        input["SubnetMask"] = masks;
                        var result = obj.InvokeMethod("EnableStatic", input, null);
                        fallbackResult = "EnableStatic ReturnValue=" + Convert.ToString(result?["ReturnValue"]);
                    }
                    catch (Exception ex)
                    {
                        fallbackResult = "EnableStatic ERR: " + ex.Message;
                    }
                    break;
                }
            }
            if (Logger != null)
                Logger("Static mode WMI fallback output: " + fallbackResult);
            await ApplyStaticNetshAsync(adapter, config, cancellationToken);
        }

        private static async Task ApplyStaticNetshAsync(
            WiredAdapter adapter,
            AdapterOriginalConfig config,
            CancellationToken cancellationToken)
        {
            await RunNetshAsync(BuildStaticAddressRestoreCommand(adapter, config), cancellationToken);
            for (var i = 1; i < config.StaticAddresses.Count; i++)
            {
                var item = config.StaticAddresses[i];
                await RunNetshAsync(
                    "interface ipv4 add address name=\"" + adapter.Name + "\" address=" + item.Address +
                    " mask=" + item.Mask + " store=persistent",
                    cancellationToken);
            }

            for (var i = 1; i < config.Gateways.Count; i++)
            {
                await RunNetshAsync(
                    "interface ipv4 add route prefix=0.0.0.0/0 interface=\"" + adapter.Name +
                    "\" nexthop=" + config.Gateways[i] +
                    (i < config.GatewayMetrics.Count ? " metric=" + config.GatewayMetrics[i] : "") +
                    " store=persistent",
                    cancellationToken);
            }
        }

        private static async Task RestoreDnsAsync(
            WiredAdapter adapter,
            AdapterOriginalConfig config,
            CancellationToken cancellationToken)
        {
            if (config.DnsServersFromDhcp || config.DnsServers.Count == 0)
            {
                await RunNetshAsync(
                    "interface ipv4 set dnsservers name=\"" + adapter.Name + "\" source=dhcp",
                    cancellationToken);
                return;
            }

            await RunNetshAsync(
                "interface ipv4 set dnsservers name=\"" + adapter.Name + "\" source=static address=" +
                config.DnsServers[0] + " register=both validate=no",
                cancellationToken);
            for (var i = 1; i < config.DnsServers.Count; i++)
            {
                await RunNetshAsync(
                    "interface ipv4 add dnsservers name=\"" + adapter.Name + "\" address=" +
                    config.DnsServers[i] + " index=" + (i + 1) + " validate=no",
                    cancellationToken);
            }
        }

        private static async Task<VerificationWindowResult> VerifyUntilStableAsync(
            WiredAdapter adapter,
            AdapterOriginalConfig expected,
            SubnetConfig subnetConfig,
            CancellationToken cancellationToken,
            int attempts,
            string label,
            List<string> details,
            bool requireToolLeaseRemoved)
        {
            var consecutiveSuccesses = 0;
            NetworkConfigVerification last = null;
            for (var i = 0; i < attempts; i++)
            {
                last = await VerifyOriginalConfigAsync(
                    adapter, expected, subnetConfig, cancellationToken, requireToolLeaseRemoved);
                if (Logger != null)
                    Logger(label + " " + (i + 1) + ": " + last.Details);
                details.Add(label + " " + (i + 1) + ": " + last.Details);
                if (last.IsSuccess)
                {
                    consecutiveSuccesses++;
                    if (consecutiveSuccesses >= 2)
                        return new VerificationWindowResult(true, last);
                }
                else
                {
                    consecutiveSuccesses = 0;
                }

                if (i + 1 < attempts)
                    await Task.Delay(1000, cancellationToken);
            }
            return new VerificationWindowResult(false, last);
        }

        private sealed class VerificationWindowResult
        {
            public VerificationWindowResult(bool isSuccess, NetworkConfigVerification lastVerification)
            {
                IsSuccess = isSuccess;
                LastVerification = lastVerification;
            }

            public bool IsSuccess { get; private set; }
            public NetworkConfigVerification LastVerification { get; private set; }
        }

        internal static bool NeedsStaticFallback(NetworkConfigVerification verification)
        {
            return verification == null
                || !verification.ActiveModeMatches
                || !verification.PersistentModeMatches;
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
            var activeModeMatches = current.ActiveDhcpEnabled == expected.DhcpEnabled;
            var persistentModeMatches = current.PersistentDhcpEnabled == expected.DhcpEnabled;
            var modeMatches = activeModeMatches && persistentModeMatches;
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
                || Ipv4AddressSetEquals(current.StaticAddresses, expected.StaticAddresses);
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
                ", modeActive=" + current.ActiveDhcpEnabled + "/" + expected.DhcpEnabled +
                ", modePersistent=" + current.PersistentDhcpEnabled + "/" + expected.DhcpEnabled +
                ", addresses=" + addressesMatch + ", gateways=" + gatewayAddressesMatch +
                ", addressesCurrent=[" + FormatIpv4Addresses(current.StaticAddresses) + "]" +
                ", addressesExpected=[" + FormatIpv4Addresses(expected.StaticAddresses) + "]" +
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
                ActiveModeMatches = activeModeMatches,
                PersistentModeMatches = persistentModeMatches,
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

            return string.Join(Environment.NewLine, outputs);
        }

        private static Microsoft.Win32.RegistryKey OpenAdapterRegistryKey(WiredAdapter adapter, out string attemptedPaths)
        {
            var candidates = GetAdapterRegistryKeyNames(adapter)
                .Select(name => @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in candidates)
            {
                var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, false);
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

        internal static bool ShouldPreserveCapturedAddress(
            IPAddress address,
            PrefixOrigin prefixOrigin,
            SuffixOrigin suffixOrigin,
            bool persistentDhcpEnabled)
        {
            var ignored = persistentDhcpEnabled;
            var bytes = address.GetAddressBytes();
            var isLinkLocal = bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
            if (!isLinkLocal)
                return true;

            if (prefixOrigin == PrefixOrigin.Manual || suffixOrigin == SuffixOrigin.Manual)
                return true;

            var confirmedAutomatic = prefixOrigin == PrefixOrigin.WellKnown
                || prefixOrigin == PrefixOrigin.Dhcp
                || suffixOrigin == SuffixOrigin.WellKnown
                || suffixOrigin == SuffixOrigin.OriginDhcp
                || suffixOrigin == SuffixOrigin.LinkLayerAddress
                || suffixOrigin == SuffixOrigin.Random;
            return !confirmedAutomatic;
        }

        public static bool IsCurrentlyConfirmedAutomaticApipa(
            WiredAdapter adapter,
            IPAddress address)
        {
            try
            {
                var ni = FindNetworkInterface(adapter);
                if (ni == null)
                    return false;
                var current = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(item => item.Address.Equals(address));
                return current != null
                    && !ShouldPreserveCapturedAddress(
                        current.Address, current.PrefixOrigin, current.SuffixOrigin, true);
            }
            catch (Exception ex)
            {
                if (Logger != null)
                    Logger("Legacy APIPA origin check failed; address preserved: " + ex.Message);
                return false;
            }
        }

        private static bool IsDnsAutomatic(WiredAdapter adapter, bool fallback)
        {
            try
            {
                string keyPath;
                using (var key = OpenAdapterRegistryKey(adapter, out keyPath))
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
                using (var key = OpenAdapterRegistryKey(adapter, out keyPath))
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
                using (var key = OpenAdapterRegistryKey(adapter, out keyPath))
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
                return command + " gateway=none store=persistent";

            command += " gateway=" + originalConfig.Gateways[0];
            if (originalConfig.GatewayMetrics.Count > 0)
                command += " gwmetric=" + originalConfig.GatewayMetrics[0].ToString(CultureInfo.InvariantCulture);

            return command + " store=persistent";
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

        private static bool IpSetEquals(IEnumerable<IPAddress> current, IEnumerable<IPAddress> expected)
        {
            return new HashSet<string>(current.Select(ip => ip.ToString()), StringComparer.OrdinalIgnoreCase)
                .SetEquals(expected.Select(ip => ip.ToString()));
        }

        private static bool Ipv4AddressSetEquals(
            IEnumerable<AdapterIpv4Address> current,
            IEnumerable<AdapterIpv4Address> expected)
        {
            return new HashSet<string>(
                    current.Select(item => item.Address + "/" + item.Mask),
                    StringComparer.OrdinalIgnoreCase)
                .SetEquals(expected.Select(item => item.Address + "/" + item.Mask));
        }

        private static string FormatIpv4Addresses(IEnumerable<AdapterIpv4Address> addresses)
        {
            return string.Join(",", addresses.Select(item =>
                item.Address + "/" + item.Mask +
                "{" + item.PrefixOrigin + "/" + item.SuffixOrigin + "/" + item.AddressState + "}"));
        }

        private static string BuildDisableDhcpScript(WiredAdapter adapter)
        {
            var name = adapter.Name.Replace("'", "''");
            return "$ErrorActionPreference='Continue'; $out=@(); $name='" + name + "'; " +
                "try { Set-NetIPInterface -InterfaceAlias $name -AddressFamily IPv4 -Dhcp Disabled -ErrorAction Stop; $out+='Set-NetIPInterface Dhcp Disabled OK' } " +
                "catch { $out+='Set-NetIPInterface Dhcp Disabled ERR: ' + $_.Exception.Message }; $out";
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

        private static string NormalizeMac(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).ToArray());
        }

        private static Task<string> RunNetshAsync(string arguments, CancellationToken cancellationToken)
        {
            return RunProcessAsync("netsh.exe", arguments, cancellationToken, true);
        }

        private static Task<string> RunPowerShellAsync(
            string command,
            CancellationToken cancellationToken,
            bool throwOnError)
        {
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            return RunProcessAsync(
                "powershell.exe",
                "-NoProfile -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                cancellationToken,
                throwOnError);
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
