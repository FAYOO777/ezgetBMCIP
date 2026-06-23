using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace EzGetBmcIp;

internal static class NetworkConfigManager
{
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
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n =>
                SameAdapterId(n.Id, adapter.Id)
                || n.Name.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase)
                || n.Description.Equals(adapter.Description, StringComparison.OrdinalIgnoreCase));
        if (ni is null)
        {
            throw new InvalidOperationException("Selected adapter was not found.");
        }

        var props = ni.GetIPProperties();
        var config = new AdapterOriginalConfig
        {
            DhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false
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
        for (var i = 0; i < 10; i++)
        {
            var dhcpEnabled = await IsDhcpEnabledAsync(adapter, cancellationToken);
            var toolIpStillPresent = await HasToolStaticIpAsync(adapter, config, cancellationToken);
            var toolLeaseStillPresent = releaseToolLease && await HasToolLeaseIpAsync(adapter, config, cancellationToken);
            Logger?.Invoke($"DHCP restore verify {i + 1}: dhcpEnabled={dhcpEnabled}, toolIpStillPresent={toolIpStillPresent}, toolLeaseStillPresent={toolLeaseStillPresent}");
            details += Environment.NewLine + $"verify {i + 1}: dhcpEnabled={dhcpEnabled}, toolIpStillPresent={toolIpStillPresent}, toolLeaseStillPresent={toolLeaseStillPresent}";

            if (dhcpEnabled && !toolIpStillPresent && !toolLeaseStillPresent)
            {
                Logger?.Invoke("DHCP restore verified OK after " + (i + 1) + " attempt(s)");
                return;
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
        await RunNetshAsync($"interface ipv4 set dnsservers name=\"{adapter.Name}\" static none", cancellationToken);
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
            $"interface ipv4 set address name=\"{adapter.Name}\" static {primary.Address} {primary.Mask} {gatewayArg}",
            cancellationToken);

        for (var i = 1; i < origConfig.StaticAddresses.Count; i++)
        {
            var item = origConfig.StaticAddresses[i];
            await RunNetshAsync(
                $"interface ipv4 add address name=\"{adapter.Name}\" {item.Address} {item.Mask}",
                cancellationToken);
        }

        if (origConfig.DnsServers.Count == 0)
        {
            await RunNetshAsync($"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp", cancellationToken);
            return;
        }

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
        var result = await RunPowerShellAsync(BuildDhcpCheckScript(adapter), cancellationToken, throwOnError: false);
        var lines = result.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();

        return lines.Any(line => line.Equals("WMI=True", StringComparison.OrdinalIgnoreCase))
            || lines.Any(line => line.Equals("NET=Enabled", StringComparison.OrdinalIgnoreCase))
            || lines.Any(line => line.Equals("REG=1", StringComparison.OrdinalIgnoreCase));
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
            "if($cfg) { " +
            "try { Invoke-CimMethod -InputObject $cfg -MethodName EnableDHCP -ErrorAction Stop | Out-Null; $out+='EnableDHCP OK' } catch { $out+='EnableDHCP ERR: ' + $_.Exception.Message }; " +
            "try { Invoke-CimMethod -InputObject $cfg -MethodName SetDNSServerSearchOrder -Arguments @{DNSServerSearchOrder=$null} -ErrorAction Stop | Out-Null; $out+='SetDNSServerSearchOrder OK' } catch { $out+='SetDNSServerSearchOrder ERR: ' + $_.Exception.Message } " +
            "} else { $out+='WMI config not found' }; " +
            "$path='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\' + $guid; " +
            "if(Test-Path $path) { " +
            "try { Set-ItemProperty -Path $path -Name EnableDHCP -Type DWord -Value 1 -ErrorAction Stop; $out+='REG EnableDHCP OK' } catch { $out+='REG EnableDHCP ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name IPAddress -Value @('0.0.0.0') -ErrorAction Stop; $out+='REG IPAddress OK' } catch { $out+='REG IPAddress ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name SubnetMask -Value @('0.0.0.0') -ErrorAction Stop; $out+='REG SubnetMask OK' } catch { $out+='REG SubnetMask ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name DefaultGateway -Value @() -ErrorAction Stop; $out+='REG DefaultGateway OK' } catch { $out+='REG DefaultGateway ERR: ' + $_.Exception.Message }; " +
            "try { Set-ItemProperty -Path $path -Name NameServer -Value '' -ErrorAction Stop; $out+='REG NameServer OK' } catch { $out+='REG NameServer ERR: ' + $_.Exception.Message } " +
            "} else { $out+='REG path not found' }; " +
            "$out";
    }

    private static string BuildDhcpCheckScript(WiredAdapter adapter)
    {
        var name = EscapePowerShellSingleQuoted(adapter.Name);
        var id = EscapePowerShellSingleQuoted(adapter.Id);
        return
            "$name='" + name + "'; " +
            "$guid='" + id + "'; " +
            "$cfg=Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.SettingID -eq $guid }; " +
            "if($cfg){'WMI=' + $cfg.DHCPEnabled}; " +
            "$net=Get-NetIPInterface -InterfaceAlias $name -AddressFamily IPv4 -ErrorAction SilentlyContinue; if($net){'NET=' + $net.Dhcp}; " +
            "$path='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\' + $guid; " +
            "if(Test-Path $path){'REG=' + (Get-ItemProperty -Path $path -Name EnableDHCP -ErrorAction SilentlyContinue).EnableDHCP}";
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
            $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            cancellationToken,
            throwOnError);
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        bool throwOnError)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
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
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

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
