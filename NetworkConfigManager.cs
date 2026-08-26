using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace EzGetBmcIp;

internal static class NetworkConfigManager
{
    private static readonly Encoding NativeProcessEncoding = CreateNativeProcessEncoding();
    public static Action<string>? Logger { get; set; }

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
        if (ni is null)
        {
            throw new InvalidOperationException("Selected adapter was not found.");
        }

        var props = ni.GetIPProperties();
        var fallbackDhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
        var dhcpEnabled = GetDhcpEnabled(adapter, fallbackDhcpEnabled, out var dhcpModeSource);
        Logger?.Invoke("IPv4 mode captured from " + dhcpModeSource + ": dhcp=" + dhcpEnabled);
        var config = new AdapterOriginalConfig
        {
            DhcpEnabled = dhcpEnabled,
            DnsServersFromDhcp = IsDnsAutomatic(adapter, dhcpEnabled)
        };

        foreach (var addr in props.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        {
            if (addr.IPv4Mask is not null)
            {
                config.StaticAddresses.Add((addr.Address, addr.IPv4Mask));
            }
        }

        foreach (var gateway in props.GatewayAddresses.Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        {
            config.Gateways.Add(gateway.Address);
        }

        if (!dhcpEnabled)
        {
            config.GatewayMetrics.AddRange(ReadGatewayMetrics(adapter));
        }

        foreach (var dns in props.DnsAddresses.Where(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
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
                adapter,
                expected,
                config,
                cancellationToken,
                requireToolLeaseRemoved: releaseToolLease);
            Logger?.Invoke($"DHCP restore verify {i + 1}: {verification.Details}");
            details += Environment.NewLine + $"verify {i + 1}: {verification.Details}";

            if (verification.IsSuccess)
            {
                consecutiveSuccesses++;
                if (consecutiveSuccesses >= 2)
                {
                    Logger?.Invoke("DHCP restore verified OK with two consecutive live checks");
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
            $"interface ipv4 set address name=\"{adapter.Name}\" static {config.ServerIp} {config.Mask}",
            cancellationToken);
        await ClearDnsForToolAsync(adapter, cancellationToken);
    }

    public static async Task RestoreOriginalConfigAsync(WiredAdapter adapter, AdapterOriginalConfig origConfig, SubnetConfig subnetConfig, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        if (origConfig.DhcpEnabled)
        {
            details.Add(await RestoreDhcpAndCollectLogAsync(adapter, subnetConfig, cancellationToken, releaseToolLease: true));
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
                    $"interface ipv4 add address name=\"{adapter.Name}\" {item.Address} {item.Mask}",
                    cancellationToken);
            }

            for (var i = 1; i < origConfig.Gateways.Count; i++)
            {
                await RunNetshAsync(
                    $"interface ipv4 add route prefix=0.0.0.0/0 interface=\"{adapter.Name}\" nexthop={origConfig.Gateways[i]}" +
                    (i < origConfig.GatewayMetrics.Count ? $" metric={origConfig.GatewayMetrics[i]}" : string.Empty) +
                    " store=persistent",
                    cancellationToken);
            }
        }
        else
        {
            await RunNetshAsync(
                $"interface ipv4 set address name=\"{adapter.Name}\" source=static",
                cancellationToken);
        }

        if (!origConfig.DhcpEnabled)
        {
            RestoreGatewayMetrics(adapter, origConfig.GatewayMetrics);
        }

        if (origConfig.DnsServersFromDhcp || origConfig.DnsServers.Count == 0)
        {
            await RunNetshAsync($"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp", cancellationToken);
        }
        else
        {
            await RunNetshAsync(
                $"interface ipv4 set dnsservers name=\"{adapter.Name}\" static {origConfig.DnsServers[0]} primary",
                cancellationToken);

            for (var i = 1; i < origConfig.DnsServers.Count; i++)
            {
                await RunNetshAsync(
                    $"interface ipv4 add dnsservers name=\"{adapter.Name}\" {origConfig.DnsServers[i]} index={i + 1}",
                    cancellationToken);
            }
        }

        var consecutiveSuccesses = 0;
        for (var i = 0; i < 12; i++)
        {
            var verification = await VerifyOriginalConfigAsync(adapter, origConfig, subnetConfig, cancellationToken);
            Logger?.Invoke($"Original config restore verify {i + 1}: {verification.Details}");
            details.Add($"verify {i + 1}: {verification.Details}");

            if (verification.IsSuccess)
            {
                consecutiveSuccesses++;
                if (consecutiveSuccesses >= 2)
                {
                    Logger?.Invoke("Original network configuration restored and verified");
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

    public static async Task<bool> IsLinkUpAsync(WiredAdapter adapter, CancellationToken cancellationToken)
    {
        var command = "$name='" + EscapePowerShellSingleQuoted(adapter.Name) + "'; " +
                      "$a=Get-CimInstance Win32_NetworkAdapter | Where-Object { $_.NetConnectionID -eq $name }; " +
                      "if($a){$a.NetConnectionStatus}";
        var result = await RunPowerShellAsync(command, cancellationToken, throwOnError: false);

        return result.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim() == "2");
    }

    public static async Task<bool> IsDhcpEnabledAsync(WiredAdapter adapter, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
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
            || IpSetEquals(
                current.StaticAddresses.Select(a => a.Address),
                expected.StaticAddresses.Select(a => a.Address));
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
            $"success={success}, mode={current.DhcpEnabled}/{expected.DhcpEnabled}, " +
            $"addresses={addressesMatch}, gateways={gatewayAddressesMatch}, gatewayMetrics={gatewayMetricsMatch}, " +
            $"gatewayMetricsCurrent=[{string.Join(",", current.GatewayMetrics)}], " +
            $"gatewayMetricsExpected=[{string.Join(",", expected.GatewayMetrics)}], " +
            $"dnsMode={current.DnsServersFromDhcp}/{expected.DnsServersFromDhcp}, dns={dnsServersMatch}, " +
            $"toolStaticRemoved={toolStaticRemoved}, toolLeaseRemoved={toolLeaseRemoved}";

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
        return await HasAdapterIpAsync(adapter, config.PoolStart, cancellationToken);
    }

    private static async Task<bool> HasAdapterIpAsync(WiredAdapter adapter, string ipAddress, CancellationToken cancellationToken)
    {
        var command = "$name='" + EscapePowerShellSingleQuoted(adapter.Name) + "'; " +
                      "$ips=Get-NetIPAddress -InterfaceAlias $name -AddressFamily IPv4 -ErrorAction SilentlyContinue; " +
                      "if($ips){$ips.IPAddress}";
        var result = await RunPowerShellAsync(command, cancellationToken, throwOnError: false);

        return result.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim().Equals(ipAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> RestoreDhcpAndCollectLogAsync(WiredAdapter adapter, SubnetConfig config, CancellationToken cancellationToken, bool releaseToolLease)
    {
        var psOutput = await RunPowerShellAsync(BuildDhcpRestoreScript(adapter), cancellationToken, throwOnError: false);
        Logger?.Invoke("DHCP restore PS output: " + psOutput);
        var outputs = new List<string>
        {
            psOutput
        };

        var commands = new[]
        {
            $"interface ipv4 set address name=\"{adapter.Name}\" source=dhcp",
            $"interface ip set address name=\"{adapter.Name}\" source=dhcp",
            $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp",
            $"interface ip set dns name=\"{adapter.Name}\" source=dhcp",
            $"interface ipv4 delete address name=\"{adapter.Name}\" addr={config.ServerIp}",
            $"interface ip delete address name=\"{adapter.Name}\" addr={config.ServerIp}"
        };

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await RunProcessAsync("netsh.exe", command, cancellationToken, throwOnError: false);
            Logger?.Invoke("DHCP restore netsh: " + command + " -> " + output.Trim());
            outputs.Add("> netsh " + command + Environment.NewLine + output.Trim());
        }

        if (releaseToolLease)
        {
            var releaseCommand = $"/release \"{adapter.Name}\"";
            var releaseOutput = await RunProcessAsync("ipconfig.exe", releaseCommand, cancellationToken, throwOnError: false);
            Logger?.Invoke("DHCP restore ipconfig: " + releaseCommand + " -> " + releaseOutput.Trim());
            outputs.Add("> ipconfig " + releaseCommand + Environment.NewLine + releaseOutput.Trim());
        }

        return string.Join(Environment.NewLine, outputs);
    }

    private static string BuildDhcpRestoreScript(WiredAdapter adapter)
    {
        var name = EscapePowerShellSingleQuoted(adapter.Name);
        var id = EscapePowerShellSingleQuoted(adapter.Id);
        return
            "$ErrorActionPreference='Continue'; " +
            "$name='" + name + "'; " +
            "$guid='" + id + "'; " +
            "$out=@(); " +
            "$cfg=Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.SettingID -eq $guid }; " +
            "if(-not $cfg){$nic=Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue | Where-Object { $_.NetConnectionID -eq $name }; if($nic){$cfg=Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.Index -eq $nic.Index }}}; " +
            "try { Set-NetIPInterface -InterfaceAlias $name -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop; $out+='Set-NetIPInterface OK' } catch { $out+='Set-NetIPInterface ERR: ' + $_.Exception.Message }; " +
            "try { Set-DnsClientServerAddress -InterfaceAlias $name -ResetServerAddresses -ErrorAction Stop; $out+='Set-DnsClientServerAddress OK' } catch { $out+='Set-DnsClientServerAddress ERR: ' + $_.Exception.Message }; " +
            "if($cfg) { $guid=$cfg.SettingID; " +
            "try { Invoke-CimMethod -InputObject $cfg -MethodName EnableDHCP -ErrorAction Stop | Out-Null; $out+='EnableDHCP OK' } catch { $out+='EnableDHCP ERR: ' + $_.Exception.Message }; " +
            "try { Invoke-CimMethod -InputObject $cfg -MethodName SetDNSServerSearchOrder -Arguments @{DNSServerSearchOrder=$null} -ErrorAction Stop | Out-Null; $out+='SetDNSServerSearchOrder OK' } catch { $out+='SetDNSServerSearchOrder ERR: ' + $_.Exception.Message } " +
            "} else { $out+='WMI config not found' }; " +
            "$base='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\'; " +
            "$trimmed=$guid.Trim('{}'); $path=@($base+$guid,$base+'{'+$trimmed+'}',$base+$trimmed) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1; " +
            "if($path -and (Test-Path -LiteralPath $path)) { " +
            "try { Set-ItemProperty -Path $path -Name EnableDHCP -Type DWord -Value 1 -ErrorAction Stop; $out+='REG EnableDHCP OK' } catch { $out+='REG EnableDHCP ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name IPAddress -Value @('0.0.0.0') -ErrorAction Stop; $out+='REG IPAddress OK' } catch { $out+='REG IPAddress ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name SubnetMask -Value @('0.0.0.0') -ErrorAction Stop; $out+='REG SubnetMask OK' } catch { $out+='REG SubnetMask ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name DefaultGateway -Value @() -ErrorAction Stop; $out+='REG DefaultGateway OK' } catch { $out+='REG DefaultGateway ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name NameServer -Value '' -ErrorAction Stop; $out+='REG NameServer OK' } catch { $out+='REG NameServer ERR: ' + $_.Exception.Message } " +
            "} else { $out+='REG path not found' }; " +
            "$out";
    }

    private static async Task ClearDnsForToolAsync(WiredAdapter adapter, CancellationToken cancellationToken)
    {
        var psOutput = await RunPowerShellAsync(BuildClearDnsScript(adapter), cancellationToken, throwOnError: false);
        Logger?.Invoke("Clear DNS PS output: " + psOutput.Trim());

        var commands = new[]
        {
            $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp",
            $"interface ip set dns name=\"{adapter.Name}\" source=dhcp",
            $"interface ipv4 set dnsservers name=\"{adapter.Name}\" static none"
        };

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await RunProcessAsync("netsh.exe", command, cancellationToken, throwOnError: false);
            Logger?.Invoke("Clear DNS netsh: " + command + " -> " + output.Trim());
        }
    }

    private static string BuildClearDnsScript(WiredAdapter adapter)
    {
        var name = EscapePowerShellSingleQuoted(adapter.Name);
        var id = EscapePowerShellSingleQuoted(adapter.Id);
        return
            "$ErrorActionPreference='Continue'; " +
            "$name='" + name + "'; " +
            "$guid='" + id + "'; " +
            "$out=@(); " +
            "try { Set-DnsClientServerAddress -InterfaceAlias $name -ResetServerAddresses -ErrorAction Stop; $out+='Set-DnsClientServerAddress OK' } catch { $out+='Set-DnsClientServerAddress ERR: ' + $_.Exception.Message }; " +
            "$cfg=Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.SettingID -eq $guid }; " +
            "if(-not $cfg){$nic=Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue | Where-Object { $_.NetConnectionID -eq $name }; if($nic){$cfg=Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.Index -eq $nic.Index }}}; " +
            "if($cfg) { $guid=$cfg.SettingID; try { Invoke-CimMethod -InputObject $cfg -MethodName SetDNSServerSearchOrder -Arguments @{DNSServerSearchOrder=$null} -ErrorAction Stop | Out-Null; $out+='SetDNSServerSearchOrder OK' } catch { $out+='SetDNSServerSearchOrder ERR: ' + $_.Exception.Message } } else { $out+='WMI config not found' }; " +
            "$base='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\'; " +
            "$trimmed=$guid.Trim('{}'); $path=@($base+$guid,$base+'{'+$trimmed+'}',$base+$trimmed) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1; " +
            "if($path -and (Test-Path -LiteralPath $path)) { try { Set-ItemProperty -Path $path -Name NameServer -Value '' -ErrorAction Stop; $out+='REG NameServer OK' } catch { $out+='REG NameServer ERR: ' + $_.Exception.Message } } else { $out+='REG path not found' }; " +
            "$out";
    }

    private static NetworkInterface? FindNetworkInterface(WiredAdapter adapter)
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
            using var key = OpenAdapterRegistryKey(adapter);
            if (key is null)
            {
                return fallback;
            }

            var configured = RegistryValueToText(key.GetValue("NameServer"));
            return string.IsNullOrWhiteSpace(configured);
        }
        catch (Exception ex)
        {
            Logger?.Invoke("DNS mode detection failed: " + ex.Message);
            return fallback;
        }
    }

    private static bool GetDhcpEnabled(WiredAdapter adapter, bool fallback, out string source)
    {
        try
        {
            using var key = OpenAdapterRegistryKey(adapter);
            if (key is not null)
            {
                var value = TryReadDhcpEnabled(key.GetValue("EnableDHCP"));
                if (value.HasValue)
                {
                    source = "registry EnableDHCP";
                    return value.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.Invoke("DHCP mode detection failed: " + ex.Message);
        }

        source = "NetworkInterface fallback";
        return fallback;
    }

    private static RegistryKey? OpenAdapterRegistryKey(WiredAdapter adapter, bool writable = false)
    {
        const string basePath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
        var names = GetAdapterRegistryKeyNames(adapter);

        foreach (var name in names)
        {
            var key = Registry.LocalMachine.OpenSubKey(basePath + name, writable);
            if (key is not null)
            {
                return key;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetAdapterRegistryKeyNames(WiredAdapter adapter)
    {
        var resolvedId = FindNetworkInterface(adapter)?.Id;
        return new[] { adapter.Id, resolvedId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value =>
            {
                var raw = value ?? string.Empty;
                var trimmed = TrimBraces(raw);
                return new[] { raw, trimmed, "{" + trimmed + "}" };
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool? TryReadDhcpEnabled(object? value)
    {
        return value switch
        {
            int number => number != 0,
            long number => number != 0,
            uint number => number != 0,
            string text when int.TryParse(text, out var number) => number != 0,
            _ => null
        };
    }

    private static string RegistryValueToText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            string[] values => string.Join(",", values),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static IEnumerable<int> ReadGatewayMetrics(WiredAdapter adapter)
    {
        try
        {
            using var key = OpenAdapterRegistryKey(adapter);
            if (key is null)
                return Array.Empty<int>();
            return RegistryValueToText(key.GetValue("DefaultGatewayMetric"))
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var metric) ? (int?)metric : null)
                .Where(metric => metric.HasValue && metric.Value >= 0)
                .Select(metric => metric!.Value)
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger?.Invoke("Gateway metric detection failed: " + ex.Message);
            return Array.Empty<int>();
        }
    }

    internal static string BuildStaticAddressRestoreCommand(
        WiredAdapter adapter,
        AdapterOriginalConfig originalConfig)
    {
        var primary = originalConfig.StaticAddresses[0];
        var command =
            $"interface ipv4 set address name=\"{adapter.Name}\" source=static " +
            $"address={primary.Address} mask={primary.Mask}";

        if (originalConfig.Gateways.Count == 0)
        {
            return command + " gateway=none";
        }

        command += " gateway=" + originalConfig.Gateways[0];
        if (originalConfig.GatewayMetrics.Count > 0)
        {
            command += " gwmetric=" + originalConfig.GatewayMetrics[0].ToString(CultureInfo.InvariantCulture);
        }

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
            using var key = OpenAdapterRegistryKey(adapter, writable: true);
            if (key is null)
            {
                Logger?.Invoke("Gateway metric restore skipped because the adapter registry key was not found.");
                return;
            }

            var values = gatewayMetrics
                .Select(metric => metric.ToString(CultureInfo.InvariantCulture))
                .ToArray();
            key.SetValue("DefaultGatewayMetric", values, RegistryValueKind.MultiString);
            Logger?.Invoke("Gateway metric restore persisted: [" + string.Join(",", values) + "]");
        }
        catch (Exception ex)
        {
            Logger?.Invoke("Gateway metric restore failed: " + ex.Message);
        }
    }

    private static bool IpSetEquals(IEnumerable<IPAddress> current, IEnumerable<IPAddress> expected)
    {
        return new HashSet<string>(current.Select(ip => ip.ToString()), StringComparer.OrdinalIgnoreCase)
            .SetEquals(expected.Select(ip => ip.ToString()));
    }

    private static List<WiredAdapter> GetPhysicalEthernetAdaptersFromWmi()
    {
        const string script =
            "$items=Get-CimInstance Win32_NetworkAdapter | Where-Object { " +
            "$_.PhysicalAdapter -eq $true -and $_.NetConnectionID -and " +
            "$_.Name -notmatch 'QoS Packet Scheduler|WFP|Filter|Miniport|Loopback' -and " +
            "$_.NetConnectionID -notmatch 'QoS Packet Scheduler|WFP|Filter|Miniport|Loopback' " +
            "} | Select-Object NetConnectionID,Name,GUID,MACAddress; " +
            "$items | ConvertTo-Json -Compress";

        try
        {
            var json = RunPowerShellSync(script);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<WiredAdapter>();
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var rows = json.TrimStart().StartsWith("[", StringComparison.Ordinal)
                ? JsonSerializer.Deserialize<List<WmiAdapterRow>>(json, options)
                : new List<WmiAdapterRow> { JsonSerializer.Deserialize<WmiAdapterRow>(json, options)! };

            return rows?
                .Where(row => !string.IsNullOrWhiteSpace(row.NetConnectionID))
                .Where(row => LooksLikeWiredOnly(row.NetConnectionID!) && LooksLikeWiredOnly(row.Name ?? ""))
                .Select(row => new WiredAdapter(
                    row.NetConnectionID!,
                    row.Name ?? string.Empty,
                    TrimBraces(row.GUID ?? string.Empty),
                    (row.MACAddress ?? string.Empty).Replace(":", "", StringComparison.Ordinal)))
                .OrderBy(adapter => adapter.Name)
                .ToList() ?? new List<WiredAdapter>();
        }
        catch
        {
            return new List<WiredAdapter>();
        }
    }

    private static List<WiredAdapter> GetPhysicalEthernetAdaptersFallback()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n =>
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.FastEthernetT
                        or NetworkInterfaceType.GigabitEthernet
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

    private static string RunPowerShellSync(string command)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildPowerShellCommand(command)));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stderr))
            Logger?.Invoke("PowerShell stderr: " + stderr);

        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout + "\r\n" + stderr;
    }

    private static string TrimBraces(string value)
    {
        return value.Trim('{', '}');
    }

    private static bool SameAdapterId(string left, string right)
    {
        return TrimBraces(left).Equals(TrimBraces(right), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WmiAdapterRow
    {
        public string? NetConnectionID { get; set; }
        public string? Name { get; set; }
        public string? GUID { get; set; }
        public string? MACAddress { get; set; }
    }

    private static Task<string> RunNetshAsync(string arguments, CancellationToken cancellationToken)
    {
        return RunProcessAsync("netsh.exe", arguments, cancellationToken, throwOnError: true);
    }

    private static Task<string> RunPowerShellAsync(string command, CancellationToken cancellationToken, bool throwOnError)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildPowerShellCommand(command)));
        return RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            cancellationToken,
            throwOnError,
            Encoding.UTF8);
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        bool throwOnError,
        Encoding? outputEncoding = null)
    {
        var encoding = outputEncoding ?? NativeProcessEncoding;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding
            },
            EnableRaisingEvents = true
        };

        process.Start();
        var stdoutTask = ProcessOutputDecoder.ReadAllBytesAsync(process.StandardOutput.BaseStream);
        var stderrTask = ProcessOutputDecoder.ReadAllBytesAsync(process.StandardError.BaseStream);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }
        var stdout = ProcessOutputDecoder.Decode(await stdoutTask, encoding);
        var stderr = ProcessOutputDecoder.Decode(await stderrTask, encoding);

        var logArgs = FormatArgsForLog(fileName, arguments);
        if (process.ExitCode != 0)
            Logger?.Invoke($"Process exit={process.ExitCode}: {fileName} {logArgs}\r\n{stderr}");
        else if (!string.IsNullOrWhiteSpace(stderr))
            Logger?.Invoke($"Process stderr: {fileName}\r\n{stderr}");

        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments}\r\n{stderr}\r\n{stdout}".Trim());
        }

        return stdout + stderr;
    }

    private static Encoding CreateNativeProcessEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    private static string FormatArgsForLog(string fileName, string arguments)
    {
        var exeName = System.IO.Path.GetFileName(fileName);
        if (exeName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("-EncodedCommand"))
        {
            var idx = arguments.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
            return arguments.Substring(0, idx + "-EncodedCommand".Length) + " [...]";
        }
        return arguments;
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string BuildPowerShellCommand(string command)
    {
        return "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); " +
               "$OutputEncoding=[Console]::OutputEncoding; " +
               "$ProgressPreference='SilentlyContinue'; " +
               command;
    }
}
