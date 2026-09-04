using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using EzGetBmcIp;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--render-ui")
            {
                RenderUiSnapshot(args[1]);
                Console.WriteLine(args[1]);
                return 0;
            }

            RecoverySnapshotRoundTrips();
            RecoverySnapshotSchemaV1RemainsCompatible();
            RecoverySnapshotMatchesAdapterIdentity();
            StaticRestoreUsesNamedGatewayMetric();
            AutomaticApipaIsExcludedButManualLinkLocalIsPreserved();
            StaticFallbackOnlyRunsForModeMismatch();
            await LinkCancellationDoesNotEnterMutationStageAsync();
            await CancellationAfterMutationRequiresRecoveryAsync();
            WatchdogStartupDoesNotCreateInteractiveWindow();
            DhcpModeUsesRegistryValues();
            FirewallAssessmentClassifiesRisk();
            await FirewallAssessmentLiveProbeFailsOpenAsync();
            ConsentNoticeDescribesNetworkChanges();
            ConsentDialogRequiresActiveAcknowledgement();
            SupportBundleShortcutMatches();
            await SupportBundleArchiveContainsLogAndDiagnosticsAsync();
            DhcpServerUsesWildcardSocketAndInterfaceFilter();
            DhcpLeaseAssignedBeforeWaitIsCached();
            DhcpRequestServerSelectionIsRespected();
            await NativeCommandOutputUsesSystemOemEncodingAsync();
            MixedNativeCommandEncodingsAreDetected();
            await EndpointProbeFindsListeningPortAsync();
            await EndpointProbeTimesOutAsync();
            Console.WriteLine("All smoke tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ConsentNoticeDescribesNetworkChanges()
    {
        var adapter = new WiredAdapter("测试网卡", "直连 BMC 管理口", "test-id", "001122334455");
        var subnet = new SubnetConfig
        {
            Octet1 = 192,
            Octet2 = 168,
            Octet3 = 55,
            Octet4 = 1
        };
        var staticConfig = new AdapterOriginalConfig
        {
            DhcpEnabled = false,
            DnsServersFromDhcp = false
        };
        staticConfig.StaticAddresses.Add(new AdapterIpv4Address(
            IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")));
        staticConfig.Gateways.Add(IPAddress.Parse("192.168.1.1"));
        staticConfig.DnsServers.Add(IPAddress.Parse("1.1.1.1"));
        staticConfig.DnsServers.Add(IPAddress.Parse("8.8.8.8"));

        var staticNotice = ConsentNotice.CreateNetworkChange(adapter, subnet, staticConfig);
        var staticText = string.Join("\n", staticNotice.Items);
        Assert(staticNotice.Title == "网络修改风险告知", "Network-change notice title was incorrect.");
        Assert(staticText.Contains("192.168.55.1 / 24"), "Temporary adapter IP was not disclosed.");
        Assert(staticText.Contains("192.168.55.100"), "Expected BMC IP was not disclosed.");
        Assert(staticText.Contains("192.168.1.20 / 255.255.255.0"), "Static IPv4 restore target was not disclosed.");
        Assert(staticText.Contains("192.168.1.1"), "Static gateway restore target was not disclosed.");
        Assert(staticText.Contains("1.1.1.1") && staticText.Contains("8.8.8.8"),
            "Static DNS restore targets were not disclosed.");
        Assert(staticText.Contains("不会主动还原 BMC"),
            "The notice did not disclose that BMC settings are not restored.");
        Assert(staticText.Contains("DNS 服务器设置会被临时清空"),
            "The temporary DNS clearing was not disclosed.");

        var dhcpConfig = AdapterOriginalConfig.CreateDhcp();
        dhcpConfig.StaticAddresses.Add(new AdapterIpv4Address(
            IPAddress.Parse("10.10.10.25"), IPAddress.Parse("255.255.255.0")));
        dhcpConfig.Gateways.Add(IPAddress.Parse("10.10.10.1"));
        dhcpConfig.DnsServers.Add(IPAddress.Parse("10.10.10.53"));
        var dhcpNotice = ConsentNotice.CreateNetworkChange(adapter, subnet, dhcpConfig);
        var dhcpText = string.Join("\n", dhcpNotice.Items);
        Assert(dhcpText.Contains("DHCP 自动获取"), "DHCP restore mode was not disclosed.");
        Assert(dhcpText.Contains("10.10.10.25") && dhcpText.Contains("10.10.10.1") && dhcpText.Contains("10.10.10.53"),
            "Current DHCP address, gateway, and DNS were not disclosed.");
        Assert(dhcpText.Contains("重新获取的租约可能不同"), "DHCP lease caveat was not disclosed.");
        Assert(dhcpText.Contains("不保证立即恢复联网"), "DHCP connectivity caveat was not disclosed.");
    }

    private static void FirewallAssessmentClassifiesRisk()
    {
        const string adapter = "I350-右2";
        const string currentExe = @"C:\Tools\ezgetBMCIP-lite.exe";

        var publicNoRule = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, Array.Empty<FirewallRuleEvidence>());
        Assert(publicNoRule.RiskLevel == FirewallRiskLevel.Warning,
            "Public firewall without a matching application rule must warn.");
        Assert(publicNoRule.BuildConsentWarning().Contains("未发现匹配当前程序路径"),
            "Missing application rule warning was not actionable.");

        var portAllow = Rule("ezgetBMCIP DHCP Server", "Allow", "Any", "UDP", "67", adapter, "Public");
        var publicPortOnly = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, new[] { portAllow });
        Assert(publicPortOnly.RiskLevel == FirewallRiskLevel.Warning && publicPortOnly.HasMatchingPortAllow,
            "A port-only allow rule must remain a compatibility warning.");
        Assert(publicPortOnly.BuildConsentWarning().Contains("端口允许规则"),
            "Port-only compatibility warning was not distinguished.");

        var appAllow = Rule("ezgetBMCIP app allow", "Allow", currentExe, "UDP", "67", adapter, "Private, Public");
        var publicAppAllow = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, new[] { appAllow });
        Assert(publicAppAllow.RiskLevel == FirewallRiskLevel.None && publicAppAllow.HasMatchingProgramAllow,
            "A current-path UDP application allow rule must clear the warning.");

        var appBlock = Rule("UDP Query User", "Block", currentExe, "Any", "Any", adapter, "Public");
        var blocked = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, new[] { portAllow, appAllow, appBlock });
        Assert(blocked.RiskLevel == FirewallRiskLevel.High && blocked.HasMatchingProgramBlock,
            "An explicit application block must take precedence over allow rules.");
        Assert(blocked.BuildTimeoutGuidance().Contains("显式入站阻止规则") &&
               blocked.BuildTimeoutGuidance().Contains("固定 IP"),
            "High-risk timeout guidance must include firewall and fixed-IP causes.");

        var oldPathAllow = Rule("old path", "Allow", @"C:\Desktop\ezgetBMCIP-lite.exe", "UDP", "67", adapter, "Public");
        var movedExe = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, new[] { oldPathAllow });
        Assert(movedExe.RiskLevel == FirewallRiskLevel.Warning && !movedExe.HasMatchingProgramAllow,
            "An allow rule for an old executable path must not match the current executable.");

        var tcpOnly = Rule("TCP Query User", "Allow", currentExe, "TCP", "Any", adapter, "Public");
        var tcpAssessment = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, new[] { tcpOnly });
        Assert(tcpAssessment.RiskLevel == FirewallRiskLevel.Warning && !tcpAssessment.HasMatchingProgramAllow,
            "A TCP-only application rule must not cover DHCP UDP/67.");

        var privateNoRule = FirewallAssessmentService.CreateForTests(
            adapter, "Private", true, currentExe, Array.Empty<FirewallRuleEvidence>());
        Assert(privateNoRule.RiskLevel == FirewallRiskLevel.Warning,
            "Private firewall without a matching application rule must also warn.");

        var disabled = FirewallAssessmentService.CreateForTests(
            adapter, "Public", false, currentExe, Array.Empty<FirewallRuleEvidence>());
        Assert(disabled.RiskLevel == FirewallRiskLevel.None && !disabled.HasWarning,
            "A disabled selected firewall profile must be reported without a warning card.");

        var unknown = FirewallAssessmentService.CreateForTests(
            adapter, "Unknown", null, currentExe, Array.Empty<FirewallRuleEvidence>());
        Assert(unknown.RiskLevel == FirewallRiskLevel.Unknown && !unknown.HasWarning,
            "Unavailable firewall assessment must fail open without a blocking warning.");
        Assert(unknown.BuildTimeoutGuidance().Contains("不能据此排除拦截") &&
               unknown.BuildTimeoutGuidance().Contains("固定 IP"),
            "Unknown timeout guidance must preserve both uncertainty and the fixed-IP cause.");

        var rulesUnavailable = new FirewallAssessment
        {
            AdapterName = adapter,
            NetworkCategory = "Public",
            ExecutablePath = currentExe,
            Error = "Firewall rules: access denied"
        };
        rulesUnavailable.Profiles.Add(new FirewallProfileEvidence { Name = "Public", Enabled = true });
        rulesUnavailable.Evaluate();
        Assert(rulesUnavailable.RiskLevel == FirewallRiskLevel.Unknown && !rulesUnavailable.HasWarning,
            "A partial assessment without rule evidence must fail open instead of showing a false warning.");

        var notice = ConsentNotice.CreateNetworkChange(
            new WiredAdapter(adapter, "直连 BMC 管理口", "test-id", "001122334455"),
            new SubnetConfig(),
            AdapterOriginalConfig.CreateDhcp(),
            blocked);
        Assert(notice.HasWarning && notice.WarningText.Contains(currentExe),
            "The network consent notice did not include the detected firewall warning.");
    }

    private static FirewallRuleEvidence Rule(
        string name,
        string action,
        string program,
        string protocol,
        string localPort,
        string interfaceAlias,
        string profile)
    {
        return new FirewallRuleEvidence
        {
            Name = name,
            DisplayName = name,
            Enabled = "True",
            Direction = "Inbound",
            Action = action,
            Profile = profile,
            Program = program,
            Protocol = protocol,
            LocalPort = localPort,
            InterfaceAlias = interfaceAlias,
            PolicyStoreSourceType = "Local",
            PolicyStoreSource = "PersistentStore",
            Status = "OK"
        };
    }

    private static async Task FirewallAssessmentLiveProbeFailsOpenAsync()
    {
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
        {
            try { return item.GetIPProperties().GetIPv4Properties() is not null; }
            catch { return false; }
        });
        if (networkInterface is null)
            return;

        var assessment = await FirewallAssessmentService.AssessAsync(
            networkInterface.Name,
            networkInterface.Id,
            networkInterface.GetPhysicalAddress().ToString(),
            FirewallAssessmentService.GetCurrentExecutablePath());
        var diagnostics = assessment.ToDiagnosticText();
        Assert(assessment.InterfaceIndex.HasValue,
            "Live firewall assessment did not resolve a known interface index.");
        Assert(diagnostics.Contains("NetworkCategory:", StringComparison.Ordinal) &&
               diagnostics.Contains("Firewall profiles (ActiveStore)", StringComparison.Ordinal) &&
               diagnostics.Contains("Relevant inbound firewall rules (ActiveStore)", StringComparison.Ordinal),
            "Live firewall assessment did not produce a complete fail-open diagnostic section.");
    }

    private static void SupportBundleShortcutMatches()
    {
        Assert(SupportBundleShortcut.Matches(ModifierKeys.Alt, Key.L, Key.None),
            "Alt+L did not match the support-bundle shortcut.");
        Assert(SupportBundleShortcut.Matches(ModifierKeys.Alt, Key.System, Key.L),
            "Alt+L reported as Key.System did not match the support-bundle shortcut.");
        Assert(!SupportBundleShortcut.Matches(ModifierKeys.None, Key.L, Key.None),
            "L without Alt matched the support-bundle shortcut.");
        Assert(!SupportBundleShortcut.Matches(ModifierKeys.Alt | ModifierKeys.Control, Key.L, Key.None),
            "Alt+Ctrl+L matched the support-bundle shortcut.");
        Assert(!SupportBundleShortcut.Matches(ModifierKeys.Alt, Key.D, Key.None),
            "Alt+D matched the support-bundle shortcut.");
    }

    private static async Task SupportBundleArchiveContainsLogAndDiagnosticsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezgetBMCIP-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logPath = Path.Combine(root, "source", "ezgetBMCIP.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, "support log content", new UTF8Encoding(true));

            var archiveDirectory = Path.Combine(root, "Support");
            var stagingRoot = Path.Combine(root, "staging");
            var progress = new ProgressRecorder();
            var firstArchive = await SupportBundleCollector.CreateAsync(
                "ezgetBMCIP-support",
                logPath,
                reportPath =>
                {
                    progress.Report(new SupportBundleProgress(10, "正在收集应用和网络状态..."));
                    progress.Report(new SupportBundleProgress(75, "正在写入诊断报告..."));
                    return File.WriteAllTextAsync(reportPath, "diagnostic report content", new UTF8Encoding(true));
                },
                progress,
                archiveDirectory,
                stagingRoot);
            var secondArchive = await SupportBundleCollector.CreateAsync(
                "ezgetBMCIP-support",
                logPath,
                reportPath => File.WriteAllTextAsync(reportPath, "diagnostic report content", new UTF8Encoding(true)),
                archiveDirectory,
                stagingRoot);

            var expectedProgress = new[] { 0, 10, 75, 85, 95, 100 };
            Assert(progress.Items.Select(item => item.Percent).SequenceEqual(expectedProgress),
                "Support-bundle progress stages were incomplete or out of order.");
            Assert(progress.Items.Zip(progress.Items.Skip(1), (left, right) => left.Percent <= right.Percent).All(value => value),
                "Support-bundle progress decreased between stages.");
            Assert(progress.Items.Last().Percent == 100, "Successful support collection did not report 100%.");

            var failureProgress = new ProgressRecorder();
            try
            {
                await SupportBundleCollector.CreateAsync(
                    "ezgetBMCIP-support",
                    logPath,
                    reportPath =>
                    {
                        failureProgress.Report(new SupportBundleProgress(10, "正在收集应用和网络状态..."));
                        return Task.FromException(new InvalidOperationException("Expected diagnostic writer failure."));
                    },
                    failureProgress,
                    archiveDirectory,
                    stagingRoot);
                throw new InvalidOperationException("A failing diagnostic writer unexpectedly created a support archive.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Expected diagnostic writer failure.")
            {
            }
            Assert(!failureProgress.Items.Any(item => item.Percent == 100),
                "Failed support collection reported a completed progress stage.");

            Assert(File.Exists(firstArchive), "First support archive was not created.");
            Assert(File.Exists(secondArchive), "Second support archive was not created.");
            Assert(!string.Equals(firstArchive, secondArchive, StringComparison.OrdinalIgnoreCase),
                "Repeated support collection overwrote the first archive.");
            Assert(Path.GetFileName(firstArchive).StartsWith("ezgetBMCIP-support-", StringComparison.Ordinal),
                "Support archive name did not include the configured prefix.");

            using var archive = ZipFile.OpenRead(firstArchive);
            var logEntry = archive.GetEntry("ezgetBMCIP.log");
            var diagnosticsEntry = archive.GetEntry("diagnostics.txt");
            Assert(logEntry is not null, "Support archive did not contain ezgetBMCIP.log.");
            Assert(diagnosticsEntry is not null, "Support archive did not contain diagnostics.txt.");

            using var logReader = new StreamReader(logEntry!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var diagnosticsReader = new StreamReader(diagnosticsEntry!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Assert(await logReader.ReadToEndAsync() == "support log content",
                "Support archive log content was incorrect.");
            Assert(await diagnosticsReader.ReadToEndAsync() == "diagnostic report content",
                "Support archive diagnostics content was incorrect.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ConsentDialogRequiresActiveAcknowledgement()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, updateAccent: true);

                var dialog = new ConsentDialog(ConsentNotice.CreateUsageRisk());
                dialog.Show();
                dialog.UpdateLayout();
                var acknowledgement = (System.Windows.Controls.CheckBox)dialog.FindName("AcknowledgementCheckBox");
                var agreeButton = (Wpf.Ui.Controls.Button)dialog.FindName("AgreeButton");
                var warningBorder = (System.Windows.Controls.Border)dialog.FindName("FirewallWarningBorder");
                Assert(acknowledgement.IsChecked != true, "Consent acknowledgement must start unchecked.");
                Assert(!agreeButton.IsEnabled, "Consent button must start disabled.");
                Assert(warningBorder.Visibility == Visibility.Collapsed,
                    "Consent warning must be hidden when no firewall risk was supplied.");

                acknowledgement.IsChecked = true;
                Assert(agreeButton.IsEnabled, "Consent button did not enable after acknowledgement.");

                dialog.Close();

                var warningAssessment = FirewallAssessmentService.CreateForTests(
                    "I350-右2", "Public", true, @"C:\Tools\ezgetBMCIP-lite.exe",
                    Array.Empty<FirewallRuleEvidence>());
                var warningNotice = ConsentNotice.CreateNetworkChange(
                    new WiredAdapter("I350-右2", "直连 BMC 管理口", "test-id", "001122334455"),
                    new SubnetConfig(),
                    AdapterOriginalConfig.CreateDhcp(),
                    warningAssessment);
                var warningDialog = new ConsentDialog(warningNotice);
                warningDialog.Show();
                warningDialog.UpdateLayout();
                var visibleWarning = (System.Windows.Controls.Border)warningDialog.FindName("FirewallWarningBorder");
                var warningAgreeButton = (Wpf.Ui.Controls.Button)warningDialog.FindName("AgreeButton");
                Assert(visibleWarning.Visibility == Visibility.Visible,
                    "Detected firewall risk was not rendered in the consent dialog.");
                Assert(!warningAgreeButton.IsEnabled,
                    "Firewall warning must not bypass active consent acknowledgement.");
                Assert(warningDialog.ActualHeight <= warningDialog.MaxHeight,
                    "Firewall warning caused the consent dialog to exceed its maximum height.");
                warningDialog.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("Consent dialog test timed out.");
        if (failure is not null)
            throw new InvalidOperationException("Consent dialog test failed.", failure);
    }

    private static void DhcpLeaseAssignedBeforeWaitIsCached()
    {
        using var server = new DhcpServer(new SubnetConfig(), 0);
        var lease = new DhcpLease
        {
            IpAddress = IPAddress.Parse("10.77.77.100"),
            MacAddress = new byte[] { 0xB0, 0x7B, 0x25, 0x47, 0xF5, 0xF5 }
        };

        server.NotifyLeaseAssigned(lease);

        Assert(ReferenceEquals(server.LastAssignedLease, lease),
            "A DHCP ACK received before the UI starts waiting must remain available.");
    }

    private static void MixedNativeCommandEncodingsAreDetected()
    {
        var text = "该计算机上没有配置域名服务器(DNS)。";
        var decoded = ProcessOutputDecoder.Decode(
            Encoding.UTF8.GetBytes(text),
            DiagnosticReporter.GetNativeConsoleEncoding());
        Assert(decoded == text, "UTF-8 native command output was decoded as the OEM code page.");
    }

    private static async Task NativeCommandOutputUsesSystemOemEncodingAsync()
    {
        Assert(
            DiagnosticReporter.GetNativeConsoleEncoding().CodePage ==
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
            "Native command output must use the current Windows OEM code page.");

        var output = await DiagnosticReporter.RunProcessAsync(
            "cmd.exe",
            "/d /c \"echo 中文编码测试\"");
        Assert(output.Contains("中文编码测试", StringComparison.Ordinal),
            "Native Chinese command output was decoded incorrectly.");
    }

    private static void DhcpServerUsesWildcardSocketAndInterfaceFilter()
    {
        var config = new SubnetConfig
        {
            Octet1 = 127,
            Octet2 = 0,
            Octet3 = 0,
            Octet4 = 1
        };

        using var server = new DhcpServer(config, 0);
        server.Start();
        Assert(server.BoundEndpoint is not null, "DHCP server did not bind a socket.");
        Assert(server.BoundEndpoint!.Address.Equals(IPAddress.Any),
            "DHCP server must use a wildcard socket so pre-address DHCP broadcasts are receivable.");
        Assert(server.ReplyBroadcastAddress.Equals(IPAddress.Parse("127.0.0.255")),
            "DHCP replies must use the configured subnet broadcast address.");
        Assert(DhcpServer.IsPacketFromExpectedInterface(14, 14),
            "DHCP packets from the selected interface must be accepted.");
        Assert(!DhcpServer.IsPacketFromExpectedInterface(14, 12),
            "DHCP packets from a different interface must be rejected.");
        Assert(DhcpServer.IsPacketFromExpectedInterface(0, 12),
            "An unspecified interface index must preserve test compatibility.");
    }

    private static void DhcpRequestServerSelectionIsRespected()
    {
        var request = BuildDhcpRequestWithServerIdentifier(IPAddress.Parse("192.168.1.1"));
        Assert(!DhcpServer.IsRequestForServer(
                request,
                IPAddress.Parse("10.77.77.1"),
                out var requestedServer),
            "A DHCPREQUEST selecting another server must be ignored.");
        Assert(requestedServer?.ToString() == "192.168.1.1",
            "The selected DHCP server was not parsed correctly.");

        var matchingRequest = BuildDhcpRequestWithServerIdentifier(IPAddress.Parse("10.77.77.1"));
        Assert(DhcpServer.IsRequestForServer(
                matchingRequest,
                IPAddress.Parse("10.77.77.1"),
                out _),
            "A DHCPREQUEST selecting this server must be accepted.");
    }

    private static byte[] BuildDhcpRequestWithServerIdentifier(IPAddress serverIdentifier)
    {
        var packet = new byte[250];
        packet[0] = 1;
        packet[236] = 99;
        packet[237] = 130;
        packet[238] = 83;
        packet[239] = 99;
        packet[240] = 53;
        packet[241] = 1;
        packet[242] = 3;
        packet[243] = 54;
        packet[244] = 4;
        Array.Copy(serverIdentifier.GetAddressBytes(), 0, packet, 245, 4);
        packet[249] = 255;
        return packet;
    }

    private static void RecoverySnapshotRoundTrips()
    {
        var snapshot = new NetworkRecoverySnapshot
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = "Ethernet",
            AdapterDescription = "Test adapter",
            AdapterId = "11111111-2222-3333-4444-555555555555",
            AdapterMacAddress = "001122334455",
            ToolServerIp = "10.77.77.1",
            ToolLeaseIp = "10.77.77.100",
            DhcpEnabled = false,
            DnsServersFromDhcp = false,
            StaticAddresses = new List<RecoveryAddress>
            {
                new RecoveryAddress
                {
                    Address = "192.168.50.20",
                    Mask = "255.255.255.0",
                    PrefixOrigin = PrefixOrigin.Manual.ToString(),
                    SuffixOrigin = SuffixOrigin.Manual.ToString(),
                    AddressState = DuplicateAddressDetectionState.Preferred.ToString()
                },
                new RecoveryAddress
                {
                    Address = "192.168.50.21",
                    Mask = "255.255.255.0",
                    PrefixOrigin = PrefixOrigin.Manual.ToString(),
                    SuffixOrigin = SuffixOrigin.Manual.ToString(),
                    AddressState = DuplicateAddressDetectionState.Preferred.ToString()
                }
            },
            Gateways = new List<string> { "192.168.50.1" },
            GatewayMetrics = new List<int> { 25 },
            DnsServers = new List<string> { "1.1.1.1", "8.8.8.8" }
        };

        var serializer = new XmlSerializer(typeof(NetworkRecoverySnapshot));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, snapshot);
        stream.Position = 0;
        var restoredSnapshot = (NetworkRecoverySnapshot)serializer.Deserialize(stream)!;
        var restoredConfig = restoredSnapshot.ToOriginalConfig();
        var restoredSubnet = restoredSnapshot.ToSubnetConfig();

        Assert(!restoredConfig.DhcpEnabled, "Static mode was not preserved.");
        Assert(!restoredConfig.DnsServersFromDhcp, "Manual DNS mode was not preserved.");
        Assert(restoredConfig.StaticAddresses.Count == 2, "Static addresses were not preserved.");
        Assert(restoredConfig.StaticAddresses.All(item => item.PrefixOrigin == PrefixOrigin.Manual),
            "Schema v2 address origins were not preserved.");
        Assert(restoredConfig.Gateways.Single().ToString() == "192.168.50.1", "Gateway was not preserved.");
        Assert(restoredConfig.GatewayMetrics.Single() == 25, "Gateway metric was not preserved.");
        Assert(restoredConfig.DnsServers.Count == 2, "DNS servers were not preserved.");
        Assert(restoredSubnet.ServerIp == "10.77.77.1", "Tool subnet was not preserved.");
    }

    private static void RecoverySnapshotSchemaV1RemainsCompatible()
    {
        var snapshot = new NetworkRecoverySnapshot
        {
            SchemaVersion = 1,
            SessionId = Guid.NewGuid().ToString("N"),
            AdapterName = "Ethernet",
            AdapterId = "11111111-2222-3333-4444-555555555555",
            ToolServerIp = "10.77.77.1",
            ToolLeaseIp = "10.77.77.100",
            DhcpEnabled = false,
            StaticAddresses = new List<RecoveryAddress>
            {
                new RecoveryAddress { Address = "169.254.170.39", Mask = "255.255.0.0" }
            }
        };

        var serializer = new XmlSerializer(typeof(NetworkRecoverySnapshot));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, snapshot);
        stream.Position = 0;
        var restored = (NetworkRecoverySnapshot)serializer.Deserialize(stream)!;
        var config = restored.ToOriginalConfig();

        Assert(restored.SchemaVersion == 1, "Schema v1 marker was not preserved.");
        Assert(config.StaticAddresses.Single().Address.ToString() == "169.254.170.39",
            "A legacy link-local address was discarded without proof that it was automatic APIPA.");
        Assert(config.StaticAddresses.Single().PrefixOrigin == PrefixOrigin.Other,
            "Missing v1 origin metadata must use the conservative unknown origin.");
    }

    private static void RecoverySnapshotMatchesAdapterIdentity()
    {
        var snapshot = new NetworkRecoverySnapshot
        {
            AdapterName = "Ethernet",
            AdapterId = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
            AdapterMacAddress = "00-11-22-33-44-55"
        };

        Assert(snapshot.MatchesAdapter(new WiredAdapter(
            "Renamed", "Adapter", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "001122334455")),
            "Adapter GUID normalization failed.");
        Assert(snapshot.MatchesAdapter(new WiredAdapter(
            "Ethernet 2", "Adapter", "different", "00:11:22:33:44:55")),
            "Adapter MAC fallback failed.");
        Assert(!snapshot.MatchesAdapter(new WiredAdapter(
            "Ethernet 3", "Adapter", "different", "AABBCCDDEEFF")),
            "Unrelated adapter was incorrectly matched.");
    }

    private static void StaticRestoreUsesNamedGatewayMetric()
    {
        var adapter = new WiredAdapter("Ethernet", "Test adapter", "test-id", "001122334455");
        var config = new AdapterOriginalConfig
        {
            DhcpEnabled = false,
            DnsServersFromDhcp = false
        };
        config.StaticAddresses.Add(new AdapterIpv4Address(
            IPAddress.Parse("192.168.50.20"), IPAddress.Parse("255.255.255.0")));
        config.Gateways.Add(IPAddress.Parse("192.168.50.1"));
        config.GatewayMetrics.Add(25);

        var command = NetworkConfigManager.BuildStaticAddressRestoreCommand(adapter, config);
        Assert(command.Contains("source=static address=192.168.50.20 mask=255.255.255.0"),
            "Static restoration must use named address and mask arguments.");
        Assert(command.Contains("gateway=192.168.50.1 gwmetric=25"),
            "Static restoration did not explicitly restore the gateway metric.");
        Assert(command.EndsWith("store=persistent", StringComparison.Ordinal),
            "Static restoration was not explicitly persisted.");
        Assert(!command.Contains("source=dhcp", StringComparison.OrdinalIgnoreCase),
            "A static-to-static restoration unexpectedly passed through DHCP.");
        Assert(NetworkConfigManager.GatewayMetricsMatch(new[] { 25 }, new[] { 25 }, false),
            "Matching gateway metrics were rejected.");
        Assert(!NetworkConfigManager.GatewayMetricsMatch(new[] { 5 }, new[] { 25 }, false),
            "Different gateway metrics were accepted.");

        var noGateway = new AdapterOriginalConfig { DhcpEnabled = false, DnsServersFromDhcp = false };
        noGateway.StaticAddresses.Add(new AdapterIpv4Address(
            IPAddress.Parse("192.168.50.20"), IPAddress.Parse("255.255.255.0")));
        Assert(NetworkConfigManager.BuildStaticAddressRestoreCommand(adapter, noGateway)
                .EndsWith("gateway=none store=persistent", StringComparison.Ordinal),
            "Static restoration without a gateway must explicitly remove the gateway.");
    }

    private static void AutomaticApipaIsExcludedButManualLinkLocalIsPreserved()
    {
        var linkLocal = IPAddress.Parse("169.254.170.39");
        Assert(!NetworkConfigManager.ShouldPreserveCapturedAddress(
                linkLocal, PrefixOrigin.WellKnown, SuffixOrigin.LinkLayerAddress, true),
            "Automatically generated APIPA was included in the recovery snapshot.");
        Assert(NetworkConfigManager.ShouldPreserveCapturedAddress(
                linkLocal, PrefixOrigin.Manual, SuffixOrigin.Manual, false),
            "A manually configured 169.254 address was incorrectly excluded.");
        Assert(NetworkConfigManager.ShouldPreserveCapturedAddress(
                linkLocal, PrefixOrigin.Other, SuffixOrigin.Other, false),
            "An unknown legacy link-local origin must be preserved conservatively.");
    }

    private static void StaticFallbackOnlyRunsForModeMismatch()
    {
        Assert(NetworkConfigManager.NeedsStaticFallback(new NetworkConfigVerification
        {
            ActiveModeMatches = false,
            PersistentModeMatches = false,
            AddressesMatch = true
        }), "Static fallback was not selected when DHCP remained enabled.");

        Assert(!NetworkConfigManager.NeedsStaticFallback(new NetworkConfigVerification
        {
            ActiveModeMatches = true,
            PersistentModeMatches = true,
            AddressesMatch = false
        }), "WMI static fallback was selected even though both mode checks already matched.");
    }

    private static async Task LinkCancellationDoesNotEnterMutationStageAsync()
    {
        var configureCalled = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await MainViewModel.RunLinkThenConfigureAsync(
                token => Task.FromCanceled(token),
                token =>
                {
                    configureCalled = true;
                    return Task.CompletedTask;
                },
                cts.Token);
            throw new InvalidOperationException("Cancelled Link wait unexpectedly continued.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert(!configureCalled,
            "Adapter mutation or recovery snapshot stage ran before Link UP.");
        Assert(!MainViewModel.ShouldRestoreAdapter(false),
            "No-link cancellation incorrectly requested adapter restoration.");
    }

    private static async Task CancellationAfterMutationRequiresRecoveryAsync()
    {
        var mutationStarted = false;
        try
        {
            await MainViewModel.RunLinkThenConfigureAsync(
                token => Task.CompletedTask,
                token =>
                {
                    mutationStarted = true;
                    throw new OperationCanceledException(token);
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }

        Assert(mutationStarted && MainViewModel.ShouldRestoreAdapter(mutationStarted),
            "Cancellation after the first mutation was not routed to recovery.");
    }

    private static void WatchdogStartupDoesNotCreateInteractiveWindow()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        Assert(!App.ShouldCreateInteractiveWindow(new[]
            {
                NetworkRecoveryStore.WatchdogArgument,
                "1234",
                sessionId
            }), "Watchdog arguments unexpectedly selected interactive UI startup.");
        Assert(App.ShouldCreateInteractiveWindow(Array.Empty<string>()),
            "Normal startup unexpectedly selected watchdog mode.");
    }

    private static void DhcpModeUsesRegistryValues()
    {
        Assert(NetworkConfigManager.TryReadDhcpEnabled(0) == false,
            "A registry EnableDHCP value of 0 must restore static IPv4 mode.");
        Assert(NetworkConfigManager.TryReadDhcpEnabled(1) == true,
            "A registry EnableDHCP value of 1 must restore DHCP mode.");
        Assert(NetworkConfigManager.TryReadDhcpEnabled("0") == false,
            "String registry DHCP values must be parsed.");
        Assert(NetworkConfigManager.TryReadDhcpEnabled(null) is null,
            "Missing registry DHCP values must use the fallback detector.");
    }

    private static async Task EndpointProbeFindsListeningPortAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        try
        {
            var result = await BmcEndpointProbe.WaitForEndpointAsync(
                IPAddress.Loopback,
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                candidates: new[] { new BmcEndpointCandidate { Scheme = "https", Port = port } });
            using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert(result is not null, "Listening endpoint was not detected.");
            Assert(result!.Scheme == "https" && result.Port == port, "Detected endpoint was incorrect.");
            Assert(result.Url == "https://127.0.0.1:" + port, "Detected URL was incorrect.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task EndpointProbeTimesOutAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var unusedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var result = await BmcEndpointProbe.WaitForEndpointAsync(
            IPAddress.Loopback,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None,
            candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = unusedPort } });
        Assert(result is null, "Closed endpoint should have timed out.");
    }

    private static void RenderUiSnapshot(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, updateAccent: true);
                var window = new MainWindow
                {
                    Width = 700,
                    Height = 900
                };
                var supportProgressCard = (System.Windows.Controls.Border)window.FindName("SupportProgressCard");
                Assert(supportProgressCard is not null && supportProgressCard.Visibility == Visibility.Collapsed,
                    "Support progress card must be hidden until Alt+L collection starts.");
                var visibleSupportProgressCard = supportProgressCard!;
                typeof(MainWindow).GetMethod("ShowSupportProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object[] { new SupportBundleProgress(60, "正在读取网卡配置...") });
                Assert(visibleSupportProgressCard.Visibility == Visibility.Visible,
                    "Support progress card was not shown for an active collection stage.");
                var vm = (MainViewModel)window.DataContext;
                vm.AppPhase = AppPhase.FlowRunning;
                vm.DiscoveredIp = "10.77.77.100";
                vm.EndpointStatusText = "IP 已分配，但 45 秒内管理页面尚未响应。可以重新检测，或手动尝试 HTTPS / HTTP。";
                vm.AdapterCardLine1 = "测试网卡 - 直连 BMC 管理口";
                vm.CurrentStepIndex = 3;
                vm.BadgeState = StepState.Pending;
                vm.BadgeText = "等待页面";
                vm.ActivityText = "DHCP 地址分配已完成，管理页面仍在启动或使用了其他端口。";

                window.Show();
                window.UpdateLayout();
                var endpointButtons = FindVisualChildren<System.Windows.Controls.Button>(window)
                    .Where(button => button.Content is string text &&
                        (text == "复制地址" || text == "打开 HTTPS" || text == "打开 HTTP" || text == "重新检测"))
                    .ToList();
                Assert(endpointButtons.Count == 4, "Endpoint action buttons were not all rendered.");
                var buttonBounds = endpointButtons
                    .Select(button =>
                    {
                        var point = button.TransformToAncestor(window).Transform(new Point(0, 0));
                        return new Rect(point.X, point.Y, button.ActualWidth, button.ActualHeight);
                    })
                    .ToList();
                Assert(buttonBounds.All(rect => rect.Left >= 0 && rect.Right <= window.ActualWidth),
                    "Endpoint action buttons overflow the window.");
                for (var i = 0; i < buttonBounds.Count; i++)
                {
                    for (var j = i + 1; j < buttonBounds.Count; j++)
                        Assert(!buttonBounds[i].IntersectsWith(buttonBounds[j]), "Endpoint action buttons overlap.");
                }

                var supportProgressPoint = visibleSupportProgressCard.TransformToAncestor(window).Transform(new Point(0, 0));
                var supportProgressBounds = new Rect(
                    supportProgressPoint.X,
                    supportProgressPoint.Y,
                    visibleSupportProgressCard.ActualWidth,
                    visibleSupportProgressCard.ActualHeight);
                Assert(supportProgressBounds.Left >= 0 && supportProgressBounds.Right <= window.ActualWidth,
                    "Support progress card overflowed the window.");
                Assert(supportProgressBounds.Bottom <= window.ActualHeight - 56,
                    "Support progress card overlapped the footer.");

                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX,
                    96 * dpi.DpiScaleY,
                    PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(outputPath))
                    encoder.Save(stream);

                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                window.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("UI snapshot render timed out.");
        if (failure is not null)
            throw new InvalidOperationException("UI snapshot render failed.", failure);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ProgressRecorder : IProgress<SupportBundleProgress>
    {
        public List<SupportBundleProgress> Items { get; } = new();

        public void Report(SupportBundleProgress value)
        {
            Items.Add(value);
        }
    }
}
