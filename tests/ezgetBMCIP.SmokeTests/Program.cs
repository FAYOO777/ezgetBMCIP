using System.IO;
using System.IO.Compression;
using System.Diagnostics;
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
using System.Windows.Threading;
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
            SessionStateTests.RunAll();
            SessionPresentationTests.RunAll();
            AdapterIdentityResolverTests.RunAll();
            FirewallRepairTests.RunAll();
            await BmcReachabilityTests.RunAllAsync();
            if (args.Length == 1 && args[0] == "--firewall-repair-readonly")
            {
                using var policy = new NativeFirewallRepairPolicy();
                var rules = policy.ReadRules();
                Console.WriteLine("Native firewall rule snapshot read successfully: " + rules.Count);
                policy.CheckWritable(4);
                Console.WriteLine("Public policy write eligibility check completed (no rules modified).");
                NativeFirewallRepairPolicy.ValidateDetachedAllow(new FirewallRepairPlan
                {
                    AdapterName = NetworkInterface.GetAllNetworkInterfaces().First().Name,
                    ExecutablePath = FirewallAssessmentService.GetCurrentExecutablePath(), Profile = 4
                });
                Console.WriteLine("Unregistered native rule creation validated (never added to firewall).");
                return 0;
            }
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
            await CancellationAfterLinkCompletionDoesNotEnterMutationStageAsync();
            await CancellationAfterMutationRequiresRecoveryAsync();
            WatchdogStartupDoesNotCreateInteractiveWindow();
            DhcpModeUsesRegistryValues();
            FirewallAssessmentClassifiesRisk();
            await FirewallAssessmentLiveProbeFailsOpenAsync();
            ConsentNoticeDescribesNetworkChanges();
            ConsentDialogRequiresActiveAcknowledgement();
            ModernUsageConsentUsesSafeDefaultLayout();
            ModernNetworkChangeConsentUsesStructuredSummary();
            SupportBundleShortcutMatches();
            await SupportBundleArchiveContainsLogAndDiagnosticsAsync();
            DhcpServerUsesWildcardSocketAndInterfaceFilter();
            DhcpLeaseAssignedBeforeWaitIsCached();
            DhcpRequestServerSelectionIsRespected();
            DhcpRequestPolicyClassifiesStates();
            DhcpRequestPolicyRejectsMalformedRequestStates();
            DhcpRequestNakDoesNotAssignLease();
            DhcpOldLeaseStudyDefaultsToProductionValues();
            BmcHistoryStoresOnlyTheLastConfirmedEndpoint();
            BmcHistoryTcpOnlyRecordsAreInvalidAndMigrated();
            BmcHistoryRetryEligibilityIsConservative();
            await NativeCommandOutputUsesSystemOemEncodingAsync();
            MixedNativeCommandEncodingsAreDetected();
            await BmcDiscoveryUsesExistingConfiguredAddressAsync();
            await BmcDiscoveryCarriesVerifiedEndpointAsync();
            await BmcDiscoveryPrefersDhcpWhenItArrivesFirstAsync();
            await EndpointProbeRequiresHttpResponseAsync();
            await EndpointProbeRejectsUnexpectedHttpStatusAsync();
            await EndpointProbeRejectsBareTcpAsync();
            await EndpointProbeRejectsRouteOrPeerMismatchAsync();
            await EndpointProbeTimesOutAsync();
            await EndpointProbeOffloadsBlockingEvidenceAsync();
            await EndpointProbeCancellationReturnsWithoutOverlapAsync();
            NativeIpv4AbiPreservesWireBytes();
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

        var historyNotice = ConsentNotice.CreateHistoryRetryPreparation(adapter, new BmcHistoryRecord
        {
            AdapterMac = adapter.MacAddress,
            AdapterName = adapter.DisplayName,
            BmcAddress = "192.168.77.100",
            LocalAddress = "192.168.77.1",
            Mask = "255.255.255.0",
            EndpointScheme = "http",
            EndpointPort = 80,
            BmcMac = "6CB31126AB48",
            LastConfirmedUtc = DateTime.UtcNow,
            VerificationVersion = 2,
            VerificationKind = "HttpResponseV1",
            HttpStatusCode = 401
        });
        var historyText = string.Join("\n", historyNotice.Items);
        Assert(historyNotice.Title == "按上次网段准备重试" &&
               historyText.Contains("不会自动开始") &&
               historyText.Contains("不会修改 BMC") &&
               historyText.Contains("192.168.77.1 / 24"),
            "History-retry confirmation did not disclose restoration, manual start, and BMC boundaries.");
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
        Assert(publicNoRule.BuildConsentWarning().Contains("公用网络"),
            "The missing-allow warning must explain how to handle a Public-network firewall prompt.");

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
        var tcpBlock = Rule("TCP Query User", "Block", currentExe, "TCP", "Any", adapter, "Public");
        var tcpBlockAssessment = FirewallAssessmentService.CreateForTests(
            adapter, "Public", true, currentExe, new[] { tcpBlock });
        Assert(tcpBlockAssessment.RiskLevel == FirewallRiskLevel.Warning && !tcpBlockAssessment.HasMatchingProgramBlock,
            "A TCP-only block must not be reported as blocking DHCP UDP/67.");

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
        Assert(unknown.RiskLevel == FirewallRiskLevel.Warning && unknown.HasWarning,
            "A readable rule collection with an unknown adapter profile must remain a visible warning.");
        Assert(unknown.BuildConsentWarning().Contains("未能确认所选网卡的网络类别"),
            "An unknown adapter profile must explain its firewall-assessment limit.");
        Assert(unknown.BuildTimeoutGuidance().Contains("不能据此排除拦截") &&
               unknown.BuildTimeoutGuidance().Contains("固定 IP"),
            "Unknown timeout guidance must preserve both uncertainty and the fixed-IP cause.");

        var privateAllow = Rule("private allow", "Allow", currentExe, "UDP", "67", adapter, "Private");
        var publicBlock = Rule("public block", "Block", currentExe, "UDP", "67", adapter, "Public");
        var unknownWithCrossProfileRules = FirewallAssessmentService.CreateForTests(
            adapter, "Unknown", null, currentExe, new[] { privateAllow, publicBlock });
        Assert(unknownWithCrossProfileRules.HasMatchingProgramAllow &&
               unknownWithCrossProfileRules.HasMatchingProgramBlock &&
               unknownWithCrossProfileRules.RiskLevel == FirewallRiskLevel.Warning,
            "Cross-profile reference rules must remain visible without becoming a confirmed High risk.");
        Assert(unknownWithCrossProfileRules.BuildConsentWarning().Contains("只作为参考"),
            "Unknown-profile guidance must explain that cross-profile matches are reference evidence.");

        unknownWithCrossProfileRules.ApplyNetworkCategorySnapshot("Public");
        unknownWithCrossProfileRules.Profiles.Add(new FirewallProfileEvidence
        {
            Name = "Public",
            Enabled = true,
            DefaultInboundAction = "Block"
        });
        unknownWithCrossProfileRules.Evaluate();
        Assert(unknownWithCrossProfileRules.UsesNetworkCategorySnapshot &&
               unknownWithCrossProfileRules.ObservedNetworkCategory == "Unknown" &&
               unknownWithCrossProfileRules.NetworkCategory == "Public" &&
               unknownWithCrossProfileRules.RiskLevel == FirewallRiskLevel.High &&
               unknownWithCrossProfileRules.HasMatchingProgramBlock &&
               !unknownWithCrossProfileRules.HasMatchingProgramAllow,
            "The pre-mutation profile snapshot must restore profile-specific rule evaluation.");
        Assert(unknownWithCrossProfileRules.ToDiagnosticText().Contains(
                "NetworkCategorySource: Captured before adapter configuration"),
            "Diagnostics must disclose when the pre-mutation profile snapshot was used.");

        var rulesUnavailable = new FirewallAssessment
        {
            AdapterName = adapter,
            NetworkCategory = "Public",
            ExecutablePath = currentExe,
            Error = "Firewall rules: access denied"
        };
        rulesUnavailable.Profiles.Add(new FirewallProfileEvidence { Name = "Public", Enabled = true });
        rulesUnavailable.Evaluate();
        Assert(rulesUnavailable.RiskLevel == FirewallRiskLevel.Unknown && rulesUnavailable.HasWarning,
            "A partial assessment without rule evidence must stay visible instead of appearing safe.");

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

                var modernNetworkNotice = ConsentNotice.CreateModernNetworkChange(
                    new WiredAdapter("I350-右2", "直连 BMC 管理口", "test-id", "001122334455"),
                    new SubnetConfig(),
                    AdapterOriginalConfig.CreateDhcp(),
                    warningAssessment);
                var modernNetworkDialog = new ConsentDialog(modernNetworkNotice);
                modernNetworkDialog.Show();
                modernNetworkDialog.UpdateLayout();
                var modernNetworkPanel = (System.Windows.Controls.StackPanel)modernNetworkDialog.FindName("NetworkChangeSummaryPanel");
                var modernGenericBorder = (System.Windows.Controls.Border)modernNetworkDialog.FindName("GenericItemsBorder");
                var modernFirewallWarning = (System.Windows.Controls.Border)modernNetworkDialog.FindName("FirewallWarningBorder");
                var modernNetworkFooter = (System.Windows.Controls.Border)modernNetworkDialog.FindName("NetworkActionFooter");
                var modernNetworkGenericActions = (System.Windows.Controls.StackPanel)modernNetworkDialog.FindName("GenericActionPanel");
                var modernNetworkAcknowledgement = (System.Windows.Controls.CheckBox)modernNetworkDialog.FindName("NetworkAcknowledgementCheckBox");
                var modernNetworkCancel = (Wpf.Ui.Controls.Button)modernNetworkDialog.FindName("NetworkCancelButton");
                var modernNetworkAgree = (Wpf.Ui.Controls.Button)modernNetworkDialog.FindName("NetworkAgreeButton");
                var modernNetworkScrollViewer = (System.Windows.Controls.ScrollViewer)modernNetworkDialog.FindName("ConsentScrollViewer");
                Assert(modernNetworkPanel.Visibility == Visibility.Visible &&
                       modernGenericBorder.Visibility == Visibility.Collapsed &&
                       modernFirewallWarning.Visibility == Visibility.Collapsed &&
                       modernNetworkFooter.Visibility == Visibility.Visible &&
                       modernNetworkGenericActions.Visibility == Visibility.Collapsed &&
                       System.Windows.Controls.Grid.GetRow(modernNetworkScrollViewer) == 0 &&
                       System.Windows.Controls.Grid.GetRow(modernNetworkFooter) == 1,
                    "Modern network-change consent did not render the structured summary layout exclusively.");
                Assert(modernNetworkFooter.Background is SolidColorBrush footerBrush && footerBrush.Color.A == 255,
                    "Modern network-change footer must use an opaque theme surface.");
                Assert(modernNetworkCancel.Visibility == Visibility.Visible &&
                       modernNetworkCancel.IsEnabled &&
                       modernNetworkAgree.Visibility == Visibility.Visible &&
                       !modernNetworkAgree.IsEnabled,
                    "Modern network-change consent must highlight the safe cancel action and keep confirmation secondary.");
                modernNetworkAcknowledgement.IsChecked = true;
                Assert(modernNetworkAgree.IsEnabled,
                    "Modern network-change consent confirmation did not enable after acknowledgement.");
                modernNetworkDialog.Close();

                var modernDialog = new ConsentDialog(ConsentNotice.CreateModernUsageRisk());
                modernDialog.Show();
                modernDialog.UpdateLayout();
                var cancelButton = (Wpf.Ui.Controls.Button)modernDialog.FindName("CancelButton");
                var secondaryAgreeButton = (Wpf.Ui.Controls.Button)modernDialog.FindName("AgreeSecondaryButton");
                var primaryAgreeButton = (Wpf.Ui.Controls.Button)modernDialog.FindName("AgreeButton");
                Assert(cancelButton.Visibility == Visibility.Visible,
                    "Modern usage consent must highlight the cancel action.");
                Assert(cancelButton.Focusable,
                    "Modern usage consent cancel action must remain focusable for the initial safe focus.");
                Assert(primaryAgreeButton.Visibility != Visibility.Visible,
                    "Modern usage consent must not show the primary confirm style (actual=" + primaryAgreeButton.Visibility + ", safe=" + ((ConsentNotice)modernDialog.DataContext).PreferSafeDefault + ").");
                Assert(!secondaryAgreeButton.IsEnabled,
                    "Modern usage consent confirmation must start disabled.");
                var modernAcknowledgement = (System.Windows.Controls.CheckBox)modernDialog.FindName("AcknowledgementCheckBox");
                modernAcknowledgement.IsChecked = true;
                Assert(secondaryAgreeButton.IsEnabled,
                    "Modern usage consent confirmation did not enable after acknowledgement.");
                modernDialog.Close();
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

    private static void ModernUsageConsentUsesSafeDefaultLayout()
    {
        var notice = ConsentNotice.CreateModernUsageRisk();
        Assert(notice.Title == "开始前请确认", "Modern usage consent title changed unexpectedly.");
        Assert(notice.Sections.Count == 2, "Modern usage consent must keep two grouped sections.");
        Assert(notice.Sections.Sum(section => section.Items.Count) == 5,
            "Modern usage consent must keep five risk items.");
        Assert(notice.PreferSafeDefault, "Modern usage consent must prefer the safe exit action.");
        Assert(ConsentNotice.CreateUsageRisk().Sections.Count == 0,
            "Legacy usage consent must retain the original flat layout.");

    }

    private static void ModernNetworkChangeConsentUsesStructuredSummary()
    {
        var adapter = new WiredAdapter("I350-右2", "Intel(R) Ethernet Server Adapter I350-T4", "test-id", "001122334455");
        var subnet = new SubnetConfig
        {
            Octet1 = 10,
            Octet2 = 77,
            Octet3 = 77,
            Octet4 = 1
        };
        var exe = @"C:\Tools\ezgetBMCIP-lite.exe";
        var allow = FirewallAssessmentService.CreateForTests(
            adapter.Name, "Public", true, exe,
            new[] { Rule("app allow", "Allow", exe, "UDP", "67", adapter.Name, "Public") });
        var notice = ConsentNotice.CreateModernNetworkChange(
            adapter, subnet, AdapterOriginalConfig.CreateDhcp(), allow);

        Assert(notice.HasNetworkChangeSummary && notice.LayoutKind == ConsentLayoutKind.NetworkChangeSummary,
            "Modern network-change consent must use the structured summary layout.");
        Assert(notice.Title == "网络修改风险告知" &&
               notice.Intro == "确认后，工具会临时修改所选网卡并启动 DHCP。" &&
               notice.AcknowledgementText == "我已核对本次修改和恢复方式，确认继续" &&
               notice.ConfirmButtonText == "同意并开始",
            "Modern network-change consent copy changed unexpectedly.");
        var modernContent = notice.NetworkChange!;
        Assert(!modernContent.HasFirewallNotice,
            "A None firewall assessment must keep the network-change notice quiet.");
        Assert(modernContent.ChangeRows.Any(row => row.Label == "目标网卡" && row.Value == adapter.DisplayName) &&
               modernContent.ChangeRows.Any(row => row.Label == "临时 IPv4" && row.Value == subnet.ServerDisplay) &&
               modernContent.ChangeRows.Any(row => row.Label == "预期管理地址" && row.Value == subnet.PoolDisplay) &&
               modernContent.ChangeRows.Any(row => row.Label == "运行期间" &&
                   row.Value.Contains("不能用于上网、远程桌面或其他业务连接") &&
                   row.Value.Contains("启动临时 DHCP")),
            "Modern network-change rows did not use the existing adapter and subnet values.");
        Assert(modernContent.RestoreSummary.Any(text => text.Contains("已记录这块网卡原有的 IPv4 和 DNS 设置")) &&
               modernContent.RestoreSummary.Any(text => text.Contains("恢复后会重新获取租约，地址可能不同")) &&
               modernContent.RestoreDetails.Contains("IPv4 模式：DHCP 自动获取") &&
               modernContent.RestoreDetails.Contains("IPv6、VPN、额外静态路由"),
            "DHCP restore summary or details omitted the recovery caveats.");

        var staticConfig = new AdapterOriginalConfig
        {
            DhcpEnabled = false,
            DnsServersFromDhcp = false
        };
        staticConfig.StaticAddresses.Add(new AdapterIpv4Address(
            IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")));
        staticConfig.Gateways.Add(IPAddress.Parse("192.168.1.1"));
        staticConfig.GatewayMetrics.Add(25);
        staticConfig.DnsServers.Add(IPAddress.Parse("1.1.1.1"));
        var staticNotice = ConsentNotice.CreateModernNetworkChange(adapter, subnet, staticConfig, null);
        Assert(staticNotice.NetworkChange!.RestoreSummary.Any(text => text.Contains("原静态 IPv4、网关和 DNS 将按记录写回")) &&
               staticNotice.NetworkChange.RestoreDetails.Contains("跃点 25") &&
               staticNotice.NetworkChange.RestoreDetails.Contains("1.1.1.1"),
            "Static restore summary or details omitted the recorded gateway metric or DNS.");

        var legacyNotice = ConsentNotice.CreateNetworkChange(adapter, subnet, staticConfig, allow);
        Assert(!legacyNotice.HasNetworkChangeSummary && legacyNotice.Items.Count == 5,
            "Legacy network-change consent must retain its original flat layout.");

        var portOnly = FirewallAssessmentService.CreateForTests(
            adapter.Name, "Public", true, exe,
            new[] { Rule("port allow", "Allow", "Any", "UDP", "67", adapter.Name, "Public") });
        var portOnlyNotice = ConsentNotice.CreateModernNetworkChange(adapter, subnet, staticConfig, portOnly).NetworkChange!;
        Assert(portOnlyNotice.HasFirewallNotice &&
               portOnlyNotice.FirewallNotice!.Title == "防火墙许可需要确认" &&
               portOnlyNotice.FirewallNotice.Status == ConsentFirewallNoticeStatus.Attention &&
               portOnlyNotice.FirewallNotice.Description.Contains("未确认它覆盖当前程序路径"),
            "Port-only firewall allow must remain an attention notice without overstating coverage.");
        Assert(portOnlyNotice.FirewallNotice!.Details.Contains(exe),
            "Firewall details did not preserve the complete executable path.");
        var blocked = FirewallAssessmentService.CreateForTests(
            adapter.Name, "Public", true, exe,
            new[] { Rule("block", "Block", exe, "UDP", "67", adapter.Name, "Public") });
        var blockedNotice = ConsentNotice.CreateModernNetworkChange(adapter, subnet, staticConfig, blocked).NetworkChange!;
        Assert(blockedNotice.HasFirewallNotice &&
               blockedNotice.FirewallNotice!.Title == "防火墙可能阻断 DHCP" &&
               blockedNotice.FirewallNotice.Status == ConsentFirewallNoticeStatus.Impact &&
               blockedNotice.FirewallNotice.Description.Contains("不是 DHCP 必然失败"),
            "Explicit firewall block must remain an impact notice without guaranteeing DHCP failure.");
        var unknown = FirewallAssessmentService.CreateForTests(
            adapter.Name, "Unknown", null, exe, Array.Empty<FirewallRuleEvidence>());
        var unknownNotice = ConsentNotice.CreateModernNetworkChange(adapter, subnet, staticConfig, unknown).NetworkChange!;
        Assert(unknownNotice.HasFirewallNotice &&
               unknownNotice.FirewallNotice!.Title == "防火墙状态无法完整确认" &&
               unknownNotice.FirewallNotice.Status == ConsentFirewallNoticeStatus.Attention &&
               unknownNotice.FirewallNotice.Description.Contains("公用网络"),
            "Unknown firewall assessment must remain an attention notice.");
        var missingNotice = ConsentNotice.CreateModernNetworkChange(adapter, subnet, staticConfig, null).NetworkChange!;
        Assert(missingNotice.HasFirewallNotice &&
               missingNotice.FirewallNotice!.Title == "防火墙状态无法完整确认" &&
               missingNotice.FirewallNotice.Status == ConsentFirewallNoticeStatus.Attention,
            "Missing firewall assessment must remain an attention notice.");
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

    private static void DhcpRequestPolicyClassifiesStates()
    {
        var serverIp = IPAddress.Parse("10.99.99.1");
        var mask = IPAddress.Parse("255.255.255.0");
        var fixedBmcIp = IPAddress.Parse("10.99.99.100");
        var oldBmcIp = IPAddress.Parse("10.77.77.100");

        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(requestedIp: fixedBmcIp, serverIdentifier: serverIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ack,
            "A current-subnet SELECTING request must receive DHCPACK.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(requestedIp: fixedBmcIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ack,
            "A current-subnet INIT-REBOOT request must receive DHCPACK.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(ciaddr: fixedBmcIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ack,
            "A current-subnet Renew/Rebind request must receive DHCPACK.");

        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(requestedIp: fixedBmcIp, serverIdentifier: IPAddress.Parse("10.77.77.1")),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A request selecting another DHCP server must remain ignored.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(ciaddr: oldBmcIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Nak,
            "The captured old-ciaddr DHCPREQUEST must receive DHCPNAK, not DHCPACK.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(requestedIp: oldBmcIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Nak,
            "An INIT-REBOOT request for an old subnet must receive DHCPNAK.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(ciaddr: fixedBmcIp, requestedIp: IPAddress.Parse("10.99.99.101")),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "Contradictory ciaddr and Option 50 fields must be ignored.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(giaddr: IPAddress.Parse("10.10.20.1")),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "DHCP relay requests must be ignored by the direct-connect server.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST without ciaddr or Option 50 must be ignored.");

        var nak = DhcpNakResponse.Build(BuildDhcpRequest(ciaddr: oldBmcIp), serverIp);
        Assert(nak.Skip(16).Take(4).All(value => value == 0),
            "A DHCPNAK must set yiaddr to 0.0.0.0.");
        Assert(ReadDhcpOption(nak, 53)?.SequenceEqual(new byte[] { 6 }) == true,
            "A DHCPNAK must contain DHCP message type 6.");
        Assert(ReadDhcpOption(nak, 54)?.SequenceEqual(serverIp.GetAddressBytes()) == true,
            "A DHCPNAK must identify the current DHCP server.");
        foreach (var forbiddenOption in new byte[] { 1, 3, 6, 51, 58, 59 })
        {
            Assert(ReadDhcpOption(nak, forbiddenOption) is null,
                "A DHCPNAK must not include address-configuration option " + forbiddenOption + ".");
        }

        Assert(DhcpNakResponse.DestinationAddress.Equals(IPAddress.Broadcast),
            "A direct-connect DHCPNAK must use the global broadcast destination.");
    }

    private static void DhcpRequestNakDoesNotAssignLease()
    {
        var config = new SubnetConfig { Octet1 = 10, Octet2 = 99, Octet3 = 99, Octet4 = 1 };
        var oldBmcIp = IPAddress.Parse("10.77.77.100");
        var currentBmcIp = IPAddress.Parse("10.99.99.100");
        var logs = new List<string>();
        var assignedCount = 0;
        using var server = new DhcpServer(config, 0) { Logger = logs.Add };
        server.LeaseAssigned += (_, _) => assignedCount++;

        server.HandlePacketForTestAsync(BuildDhcpRequest(ciaddr: oldBmcIp)).GetAwaiter().GetResult();
        Assert(server.LastAssignedLease is null && assignedCount == 0,
            "A DHCPNAK path must not allocate, cache, or publish a lease.");
        Assert(logs.Any(line => line.Contains("ciaddr=10.77.77.100", StringComparison.Ordinal) &&
                                line.Contains("option50=absent", StringComparison.Ordinal) &&
                                line.Contains("option54=absent", StringComparison.Ordinal) &&
                                line.Contains("disposition=Nak", StringComparison.Ordinal)),
            "DHCPREQUEST diagnostics must record ciaddr, Option 50, Option 54, and the NAK decision.");

        server.HandlePacketForTestAsync(BuildDhcpRequest(ciaddr: currentBmcIp)).GetAwaiter().GetResult();
        Assert(server.LastAssignedLease?.IpAddress.Equals(currentBmcIp) == true && assignedCount == 1,
            "A valid current-subnet renewal must still assign and publish the fixed BMC lease.");
    }

    private static void DhcpRequestPolicyRejectsMalformedRequestStates()
    {
        var serverIp = IPAddress.Parse("10.99.99.1");
        var mask = IPAddress.Parse("255.255.255.0");
        var fixedBmcIp = IPAddress.Parse("10.99.99.100");

        var badCookie = BuildDhcpRequest(requestedIp: fixedBmcIp);
        badCookie[236] = 0;
        Assert(DhcpRequestPolicy.Classify(badCookie, serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST with an invalid magic cookie must be ignored.");

        var missingType = BuildDhcpRequest().Take(240).Append((byte)255).ToArray();
        Assert(DhcpRequestPolicy.Classify(missingType, serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST without message type 3 must be ignored.");

        var invalidType = BuildDhcpRequest();
        invalidType[242] = 1;
        Assert(DhcpRequestPolicy.Classify(invalidType, serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A non-DHCPREQUEST message type must be ignored by the request classifier.");

        Assert(DhcpRequestPolicy.Classify(
                AppendDhcpOption(BuildDhcpRequest(), 53, new byte[] { 3 }),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST with duplicate message-type options must be ignored.");
        Assert(DhcpRequestPolicy.Classify(
                AppendDhcpOption(BuildDhcpRequest(requestedIp: fixedBmcIp), 50, fixedBmcIp.GetAddressBytes()),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST with duplicate Option 50 must be ignored.");
        Assert(DhcpRequestPolicy.Classify(
                AppendDhcpOption(BuildDhcpRequest(serverIdentifier: serverIp), 54, serverIp.GetAddressBytes()),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST with duplicate Option 54 must be ignored.");
        Assert(DhcpRequestPolicy.Classify(
                AppendDhcpOption(BuildDhcpRequest(), 50, new byte[] { 10, 99, 99 }),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST with an invalid Option 50 length must be ignored.");

        var truncated = BuildDhcpRequest(requestedIp: fixedBmcIp);
        Array.Resize(ref truncated, truncated.Length - 1);
        Assert(DhcpRequestPolicy.Classify(truncated, serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A DHCPREQUEST with a truncated options field must be ignored.");

        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(ciaddr: fixedBmcIp, requestedIp: fixedBmcIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A ciaddr renewal carrying Option 50 must be ignored as an invalid mixed state.");
        Assert(DhcpRequestPolicy.Classify(
                BuildDhcpRequest(ciaddr: fixedBmcIp, serverIdentifier: serverIp),
                serverIp, mask, fixedBmcIp).Disposition == DhcpRequestDisposition.Ignore,
            "A ciaddr renewal carrying Option 54 must be ignored as an invalid mixed state.");

        using var server = new DhcpServer(new SubnetConfig { Octet1 = 10, Octet2 = 99, Octet3 = 99, Octet4 = 1 }, 0);
        var assignedCount = 0;
        server.LeaseAssigned += (_, _) => assignedCount++;
        server.HandlePacketForTestAsync(BuildDhcpRequest(ciaddr: fixedBmcIp, requestedIp: fixedBmcIp)).GetAwaiter().GetResult();
        Assert(server.LastAssignedLease is null && assignedCount == 0,
            "An invalid mixed DHCPREQUEST must not create or publish a lease.");
    }

    private static void DhcpOldLeaseStudyDefaultsToProductionValues()
    {
        Assert(!DhcpOldLeaseStudy.IsEnabled,
            "The ordinary smoke-test build must not enable the old-lease study controls.");
        Assert(DhcpOldLeaseStudy.LeaseSeconds == 3600 &&
               DhcpOldLeaseStudy.RenewalSeconds == 1800 &&
               DhcpOldLeaseStudy.RebindingSeconds == 3150,
            "The ordinary build must preserve the 3600/1800/3150 DHCP timing values.");
    }

    private static byte[] BuildDhcpRequestWithServerIdentifier(IPAddress serverIdentifier)
    {
        return BuildDhcpRequest(serverIdentifier: serverIdentifier);
    }

    private static byte[] BuildDhcpRequest(
        IPAddress? ciaddr = null,
        IPAddress? requestedIp = null,
        IPAddress? serverIdentifier = null,
        IPAddress? giaddr = null)
    {
        var packet = new byte[300];
        packet[0] = 1;
        packet[1] = 1;
        packet[2] = 6;
        packet[4] = 0x4C;
        packet[5] = 0xAA;
        packet[6] = 0x92;
        packet[7] = 0x23;
        Array.Copy(new byte[] { 0x6C, 0xB3, 0x11, 0x26, 0xAB, 0x48 }, 0, packet, 28, 6);
        if (ciaddr is not null)
        {
            Array.Copy(ciaddr.GetAddressBytes(), 0, packet, 12, 4);
        }

        if (giaddr is not null)
        {
            Array.Copy(giaddr.GetAddressBytes(), 0, packet, 24, 4);
        }

        packet[236] = 99;
        packet[237] = 130;
        packet[238] = 83;
        packet[239] = 99;
        var optionIndex = 240;
        WriteDhcpOption(packet, ref optionIndex, 53, new byte[] { 3 });
        if (requestedIp is not null)
        {
            WriteDhcpOption(packet, ref optionIndex, 50, requestedIp.GetAddressBytes());
        }

        if (serverIdentifier is not null)
        {
            WriteDhcpOption(packet, ref optionIndex, 54, serverIdentifier.GetAddressBytes());
        }

        packet[optionIndex++] = 255;
        return packet.Take(optionIndex).ToArray();
    }

    private static void WriteDhcpOption(byte[] packet, ref int index, byte code, byte[] value)
    {
        packet[index++] = code;
        packet[index++] = (byte)value.Length;
        Array.Copy(value, 0, packet, index, value.Length);
        index += value.Length;
    }

    private static byte[] AppendDhcpOption(byte[] packet, byte code, byte[] value)
    {
        var endIndex = Array.LastIndexOf(packet, (byte)255);
        Assert(endIndex >= 240, "The DHCP test packet did not contain an end option.");
        return packet.Take(endIndex)
            .Append(code)
            .Append((byte)value.Length)
            .Concat(value)
            .Append((byte)255)
            .ToArray();
    }

    private static byte[]? ReadDhcpOption(byte[] packet, byte expectedCode)
    {
        var index = 240;
        while (index < packet.Length)
        {
            var code = packet[index++];
            if (code == 255)
            {
                return null;
            }

            if (code == 0)
            {
                continue;
            }

            if (index >= packet.Length)
            {
                return null;
            }

            var length = packet[index++];
            if (index + length > packet.Length)
            {
                return null;
            }

            if (code == expectedCode)
            {
                return packet.Skip(index).Take(length).ToArray();
            }

            index += length;
        }

        return null;
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
        Assert(!(new CurrentSessionState()).RequiresNetworkRecovery,
            "No-link cancellation incorrectly requested adapter restoration.");
    }

    private static async Task CancellationAfterLinkCompletionDoesNotEnterMutationStageAsync()
    {
        var configureCalled = false;
        using var cts = new CancellationTokenSource();
        try
        {
            await MainViewModel.RunLinkThenConfigureAsync(
                token =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                },
                token =>
                {
                    configureCalled = true;
                    return Task.CompletedTask;
                },
                cts.Token);
            throw new InvalidOperationException("Cancellation after Link completion unexpectedly entered configuration.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert(!configureCalled,
            "Cancellation between Link completion and configuration entered the adapter mutation stage.");
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

        var sessionState = new CurrentSessionState
        {
            Network = NetworkLifecycleState.TemporaryConfigurationMayBeActive
        };
        Assert(mutationStarted && sessionState.RequiresNetworkRecovery,
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

    private static async Task BmcDiscoveryUsesExistingConfiguredAddressAsync()
    {
        var configuredAddress = IPAddress.Parse("192.168.77.100");
        var dhcpCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await BmcDiscovery.WaitForAddressAsync(
            configuredAddress,
            token =>
            {
                var pendingDhcp = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() =>
                {
                    dhcpCancelled.TrySetResult(true);
                    pendingDhcp.TrySetCanceled(token);
                });
                return pendingDhcp.Task;
            },
            (address, _) =>
            {
                Assert(address.Equals(configuredAddress),
                    "The existing-address probe must use the configured DHCP pool address.");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert(result.Source == BmcDiscoverySource.ExistingConfiguredAddress &&
               result.IpAddress.Equals(configuredAddress),
            "A reachable retained .100 address must win without waiting for DHCP renewal.");
        await dhcpCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void BmcHistoryStoresOnlyTheLastConfirmedEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezgetBMCIP-history-smoke-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "bmc-history.json");
        var adapter = new WiredAdapter("I350-测试", "直连 BMC 管理口", "adapter-a", "00-11-22-33-44-55");
        var firstSubnet = new SubnetConfig { Octet1 = 192, Octet2 = 168, Octet3 = 77, Octet4 = 1 };
        var firstEndpoint = new BmcEndpointProbeResult
        {
            Scheme = "http",
            Port = 80,
            Url = "http://192.168.77.100",
            VerificationVersion = 2,
            VerificationKind = "HttpResponseV1",
            HttpStatusCode = 401,
            PeerMac = "6C-B3-11-26-AB-48"
        };

        try
        {
            BmcHistoryStore.SaveConfirmedEndpoint(
                adapter, firstSubnet, IPAddress.Parse("192.168.77.100"), firstEndpoint,
                "6C-B3-11-26-AB-48", path);

            var loaded = BmcHistoryStore.LoadForAdapter(adapter, path);
            Assert(loaded is not null && loaded.BmcAddress == "192.168.77.100" &&
                   loaded.LocalAddress == "192.168.77.1" && loaded.BmcMac == "6CB31126AB48",
                "A confirmed BMC endpoint was not recorded for its adapter.");

            var reachableAddress = new BmcReachabilityResult
            {
                TargetAddress = IPAddress.Parse("192.168.77.100"),
                PingSucceeded = true
            };
            BmcHistoryStore.SaveReachableAddress(
                adapter, firstSubnet, IPAddress.Parse("192.168.77.100"), reachableAddress,
                "6C-B3-11-26-AB-48", path);
            loaded = BmcHistoryStore.LoadForAdapter(adapter, path);
            Assert(loaded is not null && loaded.VerificationVersion == 3 &&
                   loaded.VerificationKind == "AddressReachableV1" &&
                   loaded.PingSucceeded && string.IsNullOrEmpty(loaded.EndpointScheme) &&
                   loaded.EndpointPort == 0,
                "A Ping-only reachable address was not stored without a fabricated web endpoint.");

            var newerSubnet = new SubnetConfig { Octet1 = 10, Octet2 = 88, Octet3 = 77, Octet4 = 1 };
            var newerEndpoint = new BmcEndpointProbeResult
            {
                Scheme = "https",
                Port = 443,
                Url = "https://10.88.77.100",
                VerificationVersion = 2,
                VerificationKind = "HttpResponseV1",
                HttpStatusCode = 302,
                PeerMac = "6C-B3-11-26-AB-49"
            };
            BmcHistoryStore.SaveConfirmedEndpoint(
                adapter, newerSubnet, IPAddress.Parse("10.88.77.100"), newerEndpoint, "", path);

            loaded = BmcHistoryStore.LoadForAdapter(adapter, path);
            Assert(loaded is not null && loaded.BmcAddress == "10.88.77.100" &&
                   loaded.LocalAddress == "10.88.77.1" && loaded.EndpointScheme == "https" &&
                   loaded.VerificationVersion == 2 && loaded.VerificationKind == "HttpResponseV1" &&
                   loaded.HttpStatusCode == 302 && loaded.BmcMac == "6CB31126AB49",
                "A later confirmed BMC endpoint did not replace the adapter's previous record.");
            var latest = loaded ?? throw new InvalidOperationException("The latest BMC history record was missing.");

            var differentAdapter = new WiredAdapter("I350-其他", "其他", "adapter-b", "00-11-22-33-44-56");
            Assert(BmcHistoryStore.LoadForAdapter(differentAdapter, path) is null,
                "A BMC history record was offered to a different physical adapter.");

            Assert(latest.TryApplyTo(firstSubnet) && firstSubnet.ServerIp == "10.88.77.1",
                "A valid BMC history record did not restore its saved local subnet values.");

            File.WriteAllText(path, "not valid json");
            Assert(BmcHistoryStore.LoadForAdapter(adapter, path) is null,
                "A damaged BMC history record must be ignored.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void BmcHistoryTcpOnlyRecordsAreInvalidAndMigrated()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezgetBMCIP-history-v2-smoke-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "bmc-history.json");
        var adapter = new WiredAdapter("I350-测试", "直连 BMC 管理口", "adapter-a", "001122334455");
        var currentSubnet = new SubnetConfig { Octet1 = 10, Octet2 = 77, Octet3 = 77, Octet4 = 1 };
        var legacyRecord = new BmcHistoryRecord
        {
            AdapterMac = adapter.MacAddress,
            AdapterName = adapter.DisplayName,
            BmcAddress = "192.168.77.100",
            LocalAddress = "192.168.77.1",
            Mask = "255.255.255.0",
            EndpointScheme = "https",
            EndpointPort = 443,
            BmcMac = "6CB31126AB48",
            LastConfirmedUtc = DateTime.UtcNow
            // Intentionally no v2 verification fields: this simulates the
            // TCP-connect-only record written by 1.5.6 and earlier.
        };

        try
        {
            Directory.CreateDirectory(root);
            WriteBmcHistoryDocument(path, new BmcHistoryDocument
            {
                Records = new List<BmcHistoryRecord> { legacyRecord }
            });

            Assert(!legacyRecord.IsValid(),
                "A TCP-only BMC history record must be invalid after the v2 verification upgrade.");
            var v2TcpOnlyRecord = new BmcHistoryRecord
            {
                AdapterMac = legacyRecord.AdapterMac,
                AdapterName = legacyRecord.AdapterName,
                BmcAddress = legacyRecord.BmcAddress,
                LocalAddress = legacyRecord.LocalAddress,
                Mask = legacyRecord.Mask,
                EndpointScheme = legacyRecord.EndpointScheme,
                EndpointPort = legacyRecord.EndpointPort,
                BmcMac = legacyRecord.BmcMac,
                LastConfirmedUtc = legacyRecord.LastConfirmedUtc,
                VerificationVersion = 2,
                VerificationKind = "TcpConnectV1",
                HttpStatusCode = 200
            };
            Assert(!v2TcpOnlyRecord.IsValid(),
                "A v2-versioned record without the HTTP-response verification kind must remain invalid.");
            var noStatusRecord = new BmcHistoryRecord
            {
                AdapterMac = legacyRecord.AdapterMac,
                AdapterName = legacyRecord.AdapterName,
                BmcAddress = legacyRecord.BmcAddress,
                LocalAddress = legacyRecord.LocalAddress,
                Mask = legacyRecord.Mask,
                EndpointScheme = legacyRecord.EndpointScheme,
                EndpointPort = legacyRecord.EndpointPort,
                BmcMac = legacyRecord.BmcMac,
                LastConfirmedUtc = legacyRecord.LastConfirmedUtc,
                VerificationVersion = 2,
                VerificationKind = "HttpResponseV1",
                HttpStatusCode = 0
            };
            Assert(!noStatusRecord.IsValid(),
                "An HTTP verification record without a real status code must remain invalid.");
            var proxyStatusRecord = new BmcHistoryRecord
            {
                AdapterMac = legacyRecord.AdapterMac,
                AdapterName = legacyRecord.AdapterName,
                BmcAddress = legacyRecord.BmcAddress,
                LocalAddress = legacyRecord.LocalAddress,
                Mask = legacyRecord.Mask,
                EndpointScheme = legacyRecord.EndpointScheme,
                EndpointPort = legacyRecord.EndpointPort,
                BmcMac = legacyRecord.BmcMac,
                LastConfirmedUtc = legacyRecord.LastConfirmedUtc,
                VerificationVersion = 2,
                VerificationKind = "HttpResponseV1",
                HttpStatusCode = 407
            };
            Assert(!proxyStatusRecord.IsValid(),
                "A proxy-authentication response must never become a BMC history suggestion.");
            Assert(BmcHistoryStore.LoadForAdapter(adapter, path) is null,
                "A pre-v2 BMC history record was offered for a subnet retry.");
            Assert(!BmcHistoryRetryPolicy.ShouldOffer(
                    adapter, currentSubnet, legacyRecord, false, FirewallRiskLevel.Warning),
                "The retry policy accepted a TCP-only history record.");

            var tcpOnlyEndpoint = new BmcEndpointProbeResult
            {
                Scheme = "https",
                Port = 443,
                Url = "https://10.77.77.100",
                VerificationVersion = 1,
                VerificationKind = "TcpConnectV1",
                HttpStatusCode = 0,
                PeerMac = "6C-B3-11-26-AB-48"
            };
            var tcpOnlyRejected = false;
            try
            {
                BmcHistoryStore.SaveConfirmedEndpoint(
                    adapter, currentSubnet, IPAddress.Parse("10.77.77.100"), tcpOnlyEndpoint,
                    "6C-B3-11-26-AB-48", path);
            }
            catch (InvalidOperationException)
            {
                tcpOnlyRejected = true;
            }
            Assert(tcpOnlyRejected,
                "Saving an endpoint without a verified HTTP response must fail closed.");

            var verifiedEndpoint = new BmcEndpointProbeResult
            {
                Scheme = "https",
                Port = 443,
                Url = "https://10.77.77.100",
                VerificationVersion = 2,
                VerificationKind = "HttpResponseV1",
                HttpStatusCode = 401,
                PeerMac = "6C-B3-11-26-AB-48"
            };
            BmcHistoryStore.SaveConfirmedEndpoint(
                adapter, currentSubnet, IPAddress.Parse("10.77.77.100"), verifiedEndpoint,
                "6C-B3-11-26-AB-48", path);

            var loaded = BmcHistoryStore.LoadForAdapter(adapter, path);
            Assert(loaded is not null
                   && loaded.VerificationVersion == 2
                   && loaded.VerificationKind == "HttpResponseV1"
                   && loaded.HttpStatusCode == 401
                   && loaded.BmcMac == "6CB31126AB48",
                "A v2 history record did not retain the verified HTTP and ARP evidence.");

            var document = ReadBmcHistoryDocument(path);
            Assert(document.Records.Count == 1 && document.Records[0].IsValid(),
                "The successful v2 save did not atomically replace the invalid legacy history record.");

            File.WriteAllText(path, "{\"Records\":null}");
            Assert(BmcHistoryStore.LoadForAdapter(adapter, path) is null,
                "A structurally incomplete history document must be ignored without throwing.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void WriteBmcHistoryDocument(string path, BmcHistoryDocument document)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(BmcHistoryDocument));
            serializer.WriteObject(stream, document);
        }
    }

    private static BmcHistoryDocument ReadBmcHistoryDocument(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(BmcHistoryDocument));
            return (BmcHistoryDocument)serializer.ReadObject(stream)!;
        }
    }

    private static void BmcHistoryRetryEligibilityIsConservative()
    {
        var adapter = new WiredAdapter("I350-测试", "直连 BMC 管理口", "adapter-a", "001122334455");
        var currentSubnet = new SubnetConfig { Octet1 = 10, Octet2 = 77, Octet3 = 77, Octet4 = 1 };
        var history = new BmcHistoryRecord
        {
            AdapterMac = "001122334455",
            AdapterName = adapter.DisplayName,
            BmcAddress = "192.168.77.100",
            LocalAddress = "192.168.77.1",
            Mask = "255.255.255.0",
            EndpointScheme = "http",
            EndpointPort = 80,
            BmcMac = "6CB31126AB48",
            LastConfirmedUtc = DateTime.UtcNow,
            VerificationVersion = 2,
            VerificationKind = "HttpResponseV1",
            HttpStatusCode = 401
        };

        Assert(BmcHistoryRetryPolicy.ShouldOffer(adapter, currentSubnet, history, false, FirewallRiskLevel.Warning),
            "A first timeout on a different historical subnet should offer one manual retry.");
        Assert(!BmcHistoryRetryPolicy.ShouldOffer(adapter, currentSubnet, history, true, FirewallRiskLevel.Warning),
            "A second timeout in the same application session must not repeat the history hint.");
        Assert(!BmcHistoryRetryPolicy.ShouldOffer(adapter, currentSubnet, history, false, FirewallRiskLevel.High),
            "An explicit firewall block must take precedence over an old-subnet suggestion.");

        currentSubnet.Octet1 = 192;
        currentSubnet.Octet2 = 168;
        Assert(!BmcHistoryRetryPolicy.ShouldOffer(adapter, currentSubnet, history, false, FirewallRiskLevel.Warning),
            "The current historical subnet must not offer a redundant retry hint.");
    }

    private static async Task BmcDiscoveryCarriesVerifiedEndpointAsync()
    {
        var configuredAddress = IPAddress.Parse("192.168.77.100");
        var verified = new BmcEndpointProbeResult
        {
            Url = "https://192.168.77.100",
            Scheme = "https",
            Port = 443,
            VerificationVersion = BmcEndpointProbe.StrictVerificationVersion,
            VerificationKind = BmcEndpointProbe.StrictVerificationKind,
            HttpStatusCode = 401,
            PeerMac = "6CB31126AB48"
        };

        var result = await BmcDiscovery.WaitForAddressAsync(
            configuredAddress,
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return IPAddress.None;
            },
            (address, _) =>
            {
                Assert(address.Equals(configuredAddress),
                    "The strict configured-address probe must receive the pool address.");
                return Task.FromResult(verified);
            },
            CancellationToken.None);

        Assert(result.Source == BmcDiscoverySource.ExistingConfiguredAddress &&
               ReferenceEquals(result.VerifiedEndpoint, verified),
            "Configured-address discovery did not preserve its strict endpoint proof.");
    }

    private static async Task BmcDiscoveryPrefersDhcpWhenItArrivesFirstAsync()
    {
        var configuredAddress = IPAddress.Parse("192.168.77.100");
        var dhcpAddress = IPAddress.Parse("192.168.77.101");
        var probeCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await BmcDiscovery.WaitForAddressAsync(
            configuredAddress,
            _ => Task.FromResult(dhcpAddress),
            async (_, token) =>
            {
                token.Register(() => probeCancelled.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            },
            CancellationToken.None);

        Assert(result.Source == BmcDiscoverySource.Dhcp && result.IpAddress.Equals(dhcpAddress),
            "A newly assigned DHCP address must win when it arrives before the configured-address probe.");
        await probeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task EndpointProbeRequiresHttpResponseAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responder = RespondOnceAsync(listener, "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            var outcome = await BmcEndpointProbe.ProbeForEndpointAsync(
                CreateLoopbackProbeRequest(),
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = port } },
                networkEvidenceProvider: new FixedEndpointNetworkEvidenceProvider());
            await responder.WaitAsync(TimeSpan.FromSeconds(2));

            var result = outcome.VerifiedEndpoint;
            Assert(result is not null, "A complete HTTP management response was not detected.");
            Assert(result!.Scheme == "http" && result.Port == port, "Detected endpoint was incorrect.");
            Assert(result.Url == "http://127.0.0.1:" + port, "Detected URL was incorrect.");
            Assert(result.VerificationVersion == 2 && result.VerificationKind == "HttpResponseV1" &&
                   result.HttpStatusCode == 401 && result.PeerMac == "6CB31126AB48",
                "A strict HTTP endpoint did not retain verification evidence.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task EndpointProbeRejectsUnexpectedHttpStatusAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // The first informational response and the final proxy challenge are
        // deliberately written together to cover both final-header parsing
        // and the no-proxy-false-success contract.
        var responder = RespondOnceAsync(listener,
            "HTTP/1.1 100 Continue\r\n\r\n" +
            "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            var outcome = await BmcEndpointProbe.ProbeForEndpointAsync(
                CreateLoopbackProbeRequest(),
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = port } },
                networkEvidenceProvider: new FixedEndpointNetworkEvidenceProvider());
            await responder.WaitAsync(TimeSpan.FromSeconds(2));

            Assert(outcome.VerifiedEndpoint is null,
                "A proxy challenge must never be treated as a BMC management page.");
            Assert(outcome.Evidence.HttpResponseReceived && outcome.Evidence.HttpStatusCode == 407 &&
                   outcome.Evidence.FailureStage == EndpointProbeFailureStage.UnexpectedHttpStatus,
                "The probe did not retain the final unexpected HTTP status as diagnostic evidence.");
            Assert(!BmcEndpointProbe.IsAcceptedManagementHttpStatus(100) &&
                   !BmcEndpointProbe.IsAcceptedManagementHttpStatus(407) &&
                   !BmcEndpointProbe.IsAcceptedManagementHttpStatus(503) &&
                   BmcEndpointProbe.IsAcceptedManagementHttpStatus(302) &&
                   BmcEndpointProbe.IsAcceptedManagementHttpStatus(401),
                "The accepted management HTTP status set is too broad or rejects normal BMC responses.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task EndpointProbeRejectsBareTcpAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responder = AcceptAndCloseOnceAsync(listener);
        try
        {
            var outcome = await BmcEndpointProbe.ProbeForEndpointAsync(
                CreateLoopbackProbeRequest(),
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = port } },
                networkEvidenceProvider: new FixedEndpointNetworkEvidenceProvider());
            await responder.WaitAsync(TimeSpan.FromSeconds(2));

            Assert(outcome.VerifiedEndpoint is null,
                "A bare TCP handshake must never be treated as a BMC management service.");
            Assert(outcome.Evidence.FailureStage == EndpointProbeFailureStage.HttpResponseMissing,
                "The bare TCP rejection did not report its HTTP-response failure stage.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task EndpointProbeRejectsRouteOrPeerMismatchAsync()
    {
        var request = CreateLoopbackProbeRequest();
        var routeMismatch = await BmcEndpointProbe.ProbeForEndpointAsync(
            request,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None,
            candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = 80 } },
            networkEvidenceProvider: new FixedEndpointNetworkEvidenceProvider { InterfaceIndex = 2 });
        Assert(routeMismatch.VerifiedEndpoint is null &&
               routeMismatch.Evidence.FailureStage == EndpointProbeFailureStage.RouteInterfaceMismatch,
            "A route selected through another adapter must fail before transport probing.");

        request.ExpectedPeerMac = "001122334455";
        var peerMismatch = await BmcEndpointProbe.ProbeForEndpointAsync(
            request,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None,
            candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = 80 } },
            networkEvidenceProvider: new FixedEndpointNetworkEvidenceProvider());
        Assert(peerMismatch.VerifiedEndpoint is null &&
               peerMismatch.Evidence.FailureStage == EndpointProbeFailureStage.PeerMacMismatch,
            "A DHCP MAC mismatch must fail before transport probing.");
    }

    private static async Task EndpointProbeTimesOutAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var unusedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var outcome = await BmcEndpointProbe.ProbeForEndpointAsync(
            CreateLoopbackProbeRequest(),
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None,
            candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = unusedPort } },
            networkEvidenceProvider: new FixedEndpointNetworkEvidenceProvider());
        Assert(outcome.VerifiedEndpoint is null, "Closed endpoint should have timed out.");
    }

    private static async Task EndpointProbeOffloadsBlockingEvidenceAsync()
    {
        var completion = new TaskCompletionSource<ProbeDispatcherResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var provider = new BlockingEndpointNetworkEvidenceProvider(1100);
            var ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, __) => ticks++;
            timer.Start();

            var probeTask = BmcEndpointProbe.ProbeForEndpointAsync(
                CreateLoopbackProbeRequest(),
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = 1 } },
                networkEvidenceProvider: provider);
            _ = probeTask.ContinueWith(
                completed => dispatcher.BeginInvoke(new Action(() =>
                {
                    timer.Stop();
                    completion.TrySetResult(new ProbeDispatcherResult
                    {
                        DispatcherThreadId = Thread.CurrentThread.ManagedThreadId,
                        ProviderThreadId = provider.LastInspectThreadId,
                        TimerTicks = ticks,
                        MaxInFlight = provider.MaxInFlight,
                        Faulted = completed.IsFaulted
                    });
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                })),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        thread.Join(TimeSpan.FromSeconds(2));
        Assert(!result.Faulted, "A slow endpoint evidence provider unexpectedly faulted the probe.");
        Assert(result.ProviderThreadId != result.DispatcherThreadId,
            "Blocking endpoint evidence inspection still ran on the WPF dispatcher.");
        Assert(result.TimerTicks >= 7,
            "DispatcherTimer did not continue refreshing while endpoint evidence was inspected.");
        Assert(result.MaxInFlight == 1,
            "A single endpoint probe started overlapping native evidence reads.");
    }

    private static async Task EndpointProbeCancellationReturnsWithoutOverlapAsync()
    {
        var provider = new BlockingEndpointNetworkEvidenceProvider(1400);
        using var cancellation = new CancellationTokenSource();
        var probe = BmcEndpointProbe.ProbeForEndpointAsync(
            CreateLoopbackProbeRequest(),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = 1 } },
            networkEvidenceProvider: provider);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var started = Stopwatch.StartNew();
        cancellation.Cancel();
        try
        {
            await probe;
            throw new InvalidOperationException("Cancelled endpoint probe unexpectedly completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Assert(started.Elapsed < TimeSpan.FromMilliseconds(700),
                "Endpoint probe cancellation waited for a slow native evidence call.");
        }

        await provider.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(provider.MaxInFlight == 1,
            "A cancelled endpoint probe allowed another native evidence read to overlap.");
    }

    private static BmcEndpointProbeRequest CreateLoopbackProbeRequest()
    {
        return new BmcEndpointProbeRequest
        {
            TargetAddress = IPAddress.Loopback,
            SourceAddress = IPAddress.Loopback,
            AdapterName = "loopback-test",
            AdapterId = "loopback-test",
            InterfaceIndex = 1
        };
    }

    private static async Task RespondOnceAsync(TcpListener listener, string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var request = new byte[1024];
        _ = await stream.ReadAsync(request, 0, request.Length);
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static async Task AcceptAndCloseOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var request = new byte[1024];
        _ = await stream.ReadAsync(request, 0, request.Length);
    }

    private static void NativeIpv4AbiPreservesWireBytes()
    {
        var address = IPAddress.Parse("10.77.77.100");
        var native = NativeEndpointNetworkEvidenceProvider.ToNativeIpv4ForWindows(address);
        Assert(BitConverter.GetBytes(native).SequenceEqual(address.GetAddressBytes()),
            "The IP helper IPv4 ABI value no longer preserves network-order address bytes.");
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
                    Height = 660
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
                vm.SessionState.Workflow = DiscoveryWorkflowState.EndpointUnreachable;
                vm.SessionState.Network = NetworkLifecycleState.TemporaryConfigurationActive;
                typeof(MainViewModel).GetMethod("NotifySessionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                vm.EndpointStatusText = "没有收到 Ping、TCP 443 或 TCP 80 的成功响应。";
                vm.AdapterCardLine1 = "测试网卡 - 直连 BMC 管理口";
                vm.CurrentStepIndex = 3;
                vm.BadgeState = StepState.Pending;
                vm.BadgeText = "等待可达性";
                vm.ActivityText = "正在检查管理页面。";
                typeof(MainViewModel).GetMethod("SetHistoryRetrySuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object[] { new BmcHistoryRecord
                    {
                        AdapterMac = "001122334455",
                        AdapterName = "测试网卡",
                        BmcAddress = "192.168.77.100",
                        LocalAddress = "192.168.77.1",
                        Mask = "255.255.255.0",
                        EndpointScheme = "http",
                        EndpointPort = 80,
                        BmcMac = "6CB31126AB48",
                        LastConfirmedUtc = DateTime.UtcNow,
                        VerificationVersion = 2,
                        VerificationKind = "HttpResponseV1",
                        HttpStatusCode = 401
                    } });
                typeof(MainViewModel).GetProperty("ShowSupportBundleAction", BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(vm, true);

                Assert(!vm.ShowLegacySupportBundleCard,
                    "EndpointUnreachable left the legacy failure support card eligible to render beside the new terminal page.");

                window.Show();
                window.UpdateLayout();
                var startPreflightOverlay = (System.Windows.Controls.Border)window.FindName("StartPreflightOverlay")
                    ?? throw new InvalidOperationException("Start preflight overlay was not found.");
                var startPreflightCard = (System.Windows.Controls.Border)window.FindName("StartPreflightCard")
                    ?? throw new InvalidOperationException("Start preflight card was not found.");
                Assert(startPreflightOverlay.Background is SolidColorBrush scrimBrush && scrimBrush.Color.A < 255,
                    "Preflight overlay scrim must remain a deliberate translucent layer.");
                Assert(startPreflightCard.Background is SolidColorBrush preflightBrush && preflightBrush.Color.A == 255,
                    "Preflight central card must use an opaque theme surface.");
                var modernRuntimeHost = (System.Windows.Controls.Border)window.FindName("ModernRuntimeHost")
                    ?? throw new InvalidOperationException("Modern runtime host was not found.");
                Assert(modernRuntimeHost.Visibility == Visibility.Visible,
                    "The unified modern runtime host was not selected for the running session.");
                var endpointButtons = FindVisualChildren<System.Windows.Controls.Button>(modernRuntimeHost)
                    .Where(button => button.IsVisible && button.Content is string text &&
                        (text == "复制地址" || text == "重新检测"))
                    .ToList();
                Assert(endpointButtons.Count == 2,
                    "Endpoint primary/secondary actions were not rendered. Count=" + endpointButtons.Count +
                        "; page=" + vm.CurrentSessionPage + ".");
                Assert(vm.CurrentSessionPage == SessionPageKind.EndpointUnreachable,
                    "EndpointUnreachable was not selected from CurrentSessionPage.");
                var buttonBounds = endpointButtons
                    .Select(button =>
                    {
                        var point = button.TransformToAncestor(window).Transform(new Point(0, 0));
                        return new Rect(point.X, point.Y, button.ActualWidth, button.ActualHeight);
                    })
                    .ToList();
                Assert(buttonBounds.All(rect => rect.Left >= 0 && rect.Right <= window.ActualWidth),
                    "Endpoint action buttons overflow the window.");
                Assert(buttonBounds.Any(rect => rect.Bottom <= window.ActualHeight - 64),
                    "EndpointUnreachable primary actions were not visible above the fixed footer at the default window height.");
                for (var i = 0; i < buttonBounds.Count; i++)
                {
                    for (var j = i + 1; j < buttonBounds.Count; j++)
                        Assert(!buttonBounds[i].IntersectsWith(buttonBounds[j]), "Endpoint action buttons overlap.");
                }

                var notifySession = typeof(MainViewModel).GetMethod(
                    "NotifySessionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
                vm.SessionState.ClearCandidateAddress();
                vm.SessionState.ClearFailure();
                vm.SessionState.Workflow = DiscoveryWorkflowState.WaitingForLink;
                vm.SessionState.Network = NetworkLifecycleState.Untouched;
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.WaitingForLink
                    && modernRuntimeHost.Visibility == Visibility.Visible,
                    "WaitingForLink was not selected from CurrentSessionPage.");
                Assert(vm.NetworkStatusText == "本机网卡：尚未修改" && vm.ExitButtonText == "停止并退出",
                    "WaitingForLink did not retain the untouched-network exit semantics.");

                vm.SessionState.Workflow = DiscoveryWorkflowState.WaitingForDhcp;
                vm.SessionState.Network = NetworkLifecycleState.TemporaryConfigurationActive;
                notifySession.Invoke(vm, null);
                typeof(MainViewModel).GetMethod("StartDhcpElapsedTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.WaitingForDhcp
                    && modernRuntimeHost.Visibility == Visibility.Visible
                    && vm.DhcpElapsedText == "00:00",
                    "WaitingForDhcp did not render its local elapsed display from zero.");
                Assert(vm.SessionState.Workflow == DiscoveryWorkflowState.WaitingForDhcp,
                    "DHCP elapsed display incorrectly changed SessionState.");
                typeof(MainViewModel).GetMethod("StopDhcpElapsedTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object[] { true });

                vm.DiscoveredIp = "10.77.77.100";
                vm.SessionState.Workflow = DiscoveryWorkflowState.ProbingEndpoint;
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.ProbingEndpoint
                    && modernRuntimeHost.Visibility == Visibility.Visible
                    && !vm.ShowLegacyRuntimeProgress,
                    "ProbingEndpoint did not use the unified modern runtime host.");

                typeof(MainViewModel).GetMethod("SetHistoryRetrySuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object?[] { null });
                typeof(MainViewModel).GetMethod("SetDhcpElapsedToMaximum", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                vm.SessionState.ClearCandidateAddress();
                vm.SessionState.Workflow = DiscoveryWorkflowState.Failed;
                vm.SessionState.SetFailure(FailureKind.DhcpTimedOut, "test timeout");
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.DhcpTimedOut
                    && modernRuntimeHost.Visibility == Visibility.Visible
                    && vm.DhcpElapsedText == "03:00",
                    "DhcpTimedOut did not render its independent timeout page and capped elapsed time.");
                Assert(!vm.ShowLegacyRuntimeProgress && !vm.ShowLegacySupportBundleCard,
                    "DhcpTimedOut left a legacy terminal card eligible to render.");
                var timeoutSupportButton = FindVisualChildren<System.Windows.Controls.Button>(modernRuntimeHost)
                    .Single(button => button.IsVisible && button.Content is string text && text == "导出支持包");
                var timeoutSupportPoint = timeoutSupportButton.TransformToAncestor(window).Transform(new Point(0, 0));
                Assert(timeoutSupportPoint.Y + timeoutSupportButton.ActualHeight <= window.ActualHeight - 64,
                    "DhcpTimedOut support action was not visible above the fixed footer at the default window height.");

                vm.SessionState.SetFailure(FailureKind.EndpointProbeError, "probe test");
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.Failure
                    && modernRuntimeHost.Visibility == Visibility.Visible
                    && !vm.CanRetryEndpointFromFailure,
                    "EndpointProbeError without a candidate exposed an invalid retry action.");
                var failureSupportButton = FindVisualChildren<System.Windows.Controls.Button>(modernRuntimeHost)
                    .Single(button => button.IsVisible && button.Content is string text && text == "导出支持包");
                var failureSupportPoint = failureSupportButton.TransformToAncestor(window).Transform(new Point(0, 0));
                Assert(failureSupportPoint.Y + failureSupportButton.ActualHeight <= window.ActualHeight - 64,
                    "Failure support action was not visible above the fixed footer at the default window height.");
                vm.DiscoveredIp = "10.77.77.100";
                vm.SessionState.SetFailure(FailureKind.EndpointProbeError, "probe test");
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CanRetryEndpointFromFailure,
                    "EndpointProbeError with a candidate did not expose the existing retry action.");

                vm.SessionState.ClearFailure();
                vm.SessionState.ClearCandidateAddress();
                vm.SessionState.Workflow = DiscoveryWorkflowState.Cancelled;
                vm.SessionState.Network = NetworkLifecycleState.Untouched;
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.Cancelled
                    && modernRuntimeHost.Visibility == Visibility.Visible
                    && !vm.FailureTitle.Contains("失败"),
                    "Cancelled was rendered as an error page.");

                vm.SessionState.Network = NetworkLifecycleState.Restoring;
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                var footerExitButton = FindVisualChildren<System.Windows.Controls.Button>(window)
                    .Single(button => button.IsVisible && button.Content is string text && text == "正在恢复网卡…");
                Assert(vm.CurrentSessionPage == SessionPageKind.Restoring
                    && modernRuntimeHost.Visibility == Visibility.Visible
                    && !footerExitButton.IsEnabled,
                    "Restoring did not select the recovery page and disable the shared exit action.");

                vm.SessionState.Network = NetworkLifecycleState.TemporaryConfigurationActive;
                vm.SessionState.Workflow = DiscoveryWorkflowState.EndpointUnreachable;
                vm.SessionState.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);
                notifySession.Invoke(vm, null);
                window.UpdateLayout();

                vm.SessionState.Workflow = DiscoveryWorkflowState.EndpointReachable;
                vm.SessionState.ClearFailure();
                typeof(MainViewModel).GetMethod("NotifySessionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.EndpointReachable
                    && modernRuntimeHost.Visibility == Visibility.Visible,
                    "EndpointReachable page was not selected from CurrentSessionPage.");
                var openManagementButton = FindVisualChildren<System.Windows.Controls.Button>(modernRuntimeHost)
                    .SingleOrDefault(button => button.IsVisible && button.Content is string text && text == "打开管理页面");
                Assert(openManagementButton is not null,
                    "EndpointReachable page did not render its primary management-page action.");
                var openManagementPoint = openManagementButton!.TransformToAncestor(window).Transform(new Point(0, 0));
                Assert(openManagementPoint.Y + openManagementButton.ActualHeight <= window.ActualHeight - 64,
                    "EndpointReachable primary action was not visible above the fixed footer at the default window height.");

                vm.SessionState.Network = NetworkLifecycleState.RestoreFailed;
                vm.SessionState.SetFailure(FailureKind.PendingRecoveryFailed, "test pending recovery failure");
                typeof(MainViewModel).GetMethod("NotifySessionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.RestoreFailed
                    && modernRuntimeHost.Visibility == Visibility.Visible,
                    "RestoreFailed page did not override EndpointReachable.");
                Assert(modernRuntimeHost.Visibility == Visibility.Visible,
                    "RestoreFailed did not leave the unified runtime host visible.");
                var retryRestoreButtons = FindVisualChildren<System.Windows.Controls.Button>(window)
                    .Where(button => button.IsVisible && button.Content is string text && text == "重试恢复网卡")
                    .ToList();
                Assert(retryRestoreButtons.Count == 1
                    && !FindVisualChildren<System.Windows.Controls.Button>(modernRuntimeHost)
                        .Any(button => button.IsVisible && button.Content is string text && text == "重试恢复网卡"),
                    "RestoreFailed duplicated its RetryRestore action instead of using the fixed footer once.");

                vm.SessionState.Workflow = DiscoveryWorkflowState.Failed;
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.RestoreFailed
                    && !vm.ShowHistoryRetryCard
                    && vm.ExitButtonText == "重试恢复网卡"
                    && vm.IsExitActionEnabled,
                    "RestoreFailed did not override Failed/PendingRecoveryFailed or retained a normal exit action.");

                typeof(MainViewModel).GetMethod("SetHistoryRetrySuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object[] { new BmcHistoryRecord
                    {
                        AdapterMac = "001122334455",
                        AdapterName = "测试网卡",
                        BmcAddress = "192.168.77.100",
                        LocalAddress = "192.168.77.1",
                        Mask = "255.255.255.0",
                        EndpointScheme = "http",
                        EndpointPort = 80,
                        BmcMac = "6CB31126AB48",
                        LastConfirmedUtc = DateTime.UtcNow,
                        VerificationVersion = 2,
                        VerificationKind = "HttpResponseV1",
                        HttpStatusCode = 401
                    } });
                vm.SessionState.Network = NetworkLifecycleState.TemporaryConfigurationActive;
                vm.SessionState.Workflow = DiscoveryWorkflowState.Failed;
                vm.SessionState.SetFailure(FailureKind.DhcpTimedOut, "test timeout");
                typeof(MainViewModel).GetMethod("NotifySessionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                window.UpdateLayout();
                var historyRetryButton = FindVisualChildren<System.Windows.Controls.Button>(window)
                    .SingleOrDefault(button => button.IsVisible && ReferenceEquals(button.Command, vm.PrepareHistoryRetryCommand));
                Assert(historyRetryButton is not null && historyRetryButton.IsVisible,
                    "The history-subnet retry action was not rendered for a first DHCP timeout.");
                var historyRetryPoint = historyRetryButton!.TransformToAncestor(window).Transform(new Point(0, 0));
                var historyRetryBounds = new Rect(
                    historyRetryPoint.X, historyRetryPoint.Y,
                    historyRetryButton.ActualWidth, historyRetryButton.ActualHeight);
                Assert(historyRetryBounds.Left >= 0 && historyRetryBounds.Right <= window.ActualWidth,
                    "History-subnet retry action overflowed the window.");

                MainViewModel.ResetSessionStateForNewFlow(vm.SessionState);
                vm.AppPhase = AppPhase.AdapterSelection;
                notifySession.Invoke(vm, null);
                window.UpdateLayout();
                Assert(vm.CurrentSessionPage == SessionPageKind.Ready
                    && !historyRetryButton.IsVisible
                    && modernRuntimeHost.Visibility == Visibility.Collapsed,
                    "Returning to adapter selection after a history retry retained old timeout content.");
                vm.AppPhase = AppPhase.FlowRunning;

                var supportProgressPoint = visibleSupportProgressCard.TransformToAncestor(window).Transform(new Point(0, 0));
                var supportProgressBounds = new Rect(
                    supportProgressPoint.X,
                    supportProgressPoint.Y,
                    visibleSupportProgressCard.ActualWidth,
                    visibleSupportProgressCard.ActualHeight);
                Assert(supportProgressBounds.Left >= 0 && supportProgressBounds.Right <= window.ActualWidth,
                    "Support progress card overflowed the window.");
                Assert(supportProgressBounds.Bottom <= window.ActualHeight - 64,
                    "Support progress card overlapped the footer.");
                Assert(visibleSupportProgressCard.Background is SolidColorBrush supportBrush && supportBrush.Color.A == 255,
                    "Support progress card must use an opaque theme surface.");

                vm.SessionState.Workflow = DiscoveryWorkflowState.EndpointReachable;
                vm.SessionState.ClearFailure();
                vm.SessionState.BrowserLaunchResult = BrowserLaunchResult.Requested;
                typeof(MainViewModel).GetMethod("NotifySessionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                visibleSupportProgressCard.Visibility = Visibility.Collapsed;
                window.UpdateLayout();

                window.Height = 580;
                var contentScroller = (System.Windows.Controls.ScrollViewer)window.FindName("ContentScroller")
                    ?? throw new InvalidOperationException("Content scroller was not found.");
                contentScroller.ScrollToTop();
                window.UpdateLayout();
                var minimumOpenManagementPoint = openManagementButton!.TransformToAncestor(window).Transform(new Point(0, 0));
                Assert(minimumOpenManagementPoint.Y + openManagementButton.ActualHeight <= window.ActualHeight - 64,
                    "EndpointReachable primary action was not visible above the fixed footer at the minimum window height.");

                window.Height = 660;
                window.UpdateLayout();

                typeof(MainViewModel).GetMethod("SetHistoryRetrySuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object?[] { null });
                vm.SessionState.ClearCandidateAddress();
                vm.SessionState.Workflow = DiscoveryWorkflowState.Failed;
                vm.SessionState.SetFailure(FailureKind.DhcpTimedOut, "firewall blocked test");
                typeof(MainViewModel).GetMethod("SetCurrentFirewallRisk", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object[] { FirewallRiskLevel.High });
                notifySession.Invoke(vm, null);
                contentScroller.ScrollToTop();
                window.UpdateLayout();
                var firewallButton = FindVisualChildren<System.Windows.Controls.Button>(modernRuntimeHost)
                    .Single(b => b.IsVisible && b.Content is string title && title == "处理防火墙并准备重试");
                var firewallPoint = firewallButton.TransformToAncestor(window).Transform(new Point(0, 0));
                Assert(firewallPoint.Y + firewallButton.ActualHeight < window.ActualHeight - 64,
                    "Firewall repair action must be reachable above the footer at default size.");
                typeof(MainViewModel).GetMethod("SetFirewallRepairBusy", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object[] { true });
                window.UpdateLayout();
                Assert(!firewallButton.IsEnabled && !vm.IsExitActionEnabled,
                    "Repeated repair and exit must be disabled during firewall recovery.");
                typeof(MainViewModel).GetMethod("SetFirewallRepairBusy", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, new object[] { false });
                window.UpdateLayout();

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

    private sealed class FixedEndpointNetworkEvidenceProvider : IEndpointNetworkEvidenceProvider
    {
        public int InterfaceIndex { get; set; } = 1;
        public string NeighborMac { get; set; } = "6C-B3-11-26-AB-48";

        public EndpointNetworkEvidence Inspect(BmcEndpointProbeRequest request)
        {
            return new EndpointNetworkEvidence
            {
                RouteResolved = true,
                BestRouteInterfaceIndex = InterfaceIndex,
                NeighborResolved = true,
                NeighborMac = NeighborMac
            };
        }
    }

    private sealed class BlockingEndpointNetworkEvidenceProvider : IEndpointNetworkEvidenceProvider
    {
        private readonly int _delayMilliseconds;
        private int _inFlight;

        public BlockingEndpointNetworkEvidenceProvider(int delayMilliseconds)
        {
            _delayMilliseconds = delayMilliseconds;
        }

        public int LastInspectThreadId { get; private set; }
        public int MaxInFlight { get; private set; }
        public TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EndpointNetworkEvidence Inspect(BmcEndpointProbeRequest request)
        {
            LastInspectThreadId = Thread.CurrentThread.ManagedThreadId;
            var current = Interlocked.Increment(ref _inFlight);
            Started.TrySetResult(true);
            if (current > MaxInFlight)
                MaxInFlight = current;
            try
            {
                Thread.Sleep(_delayMilliseconds);
                return new EndpointNetworkEvidence
                {
                    RouteResolved = true,
                    BestRouteInterfaceIndex = request.InterfaceIndex,
                    NeighborResolved = true,
                    NeighborMac = "6C-B3-11-26-AB-48"
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                Completed.TrySetResult(true);
            }
        }
    }

    private sealed class ProbeDispatcherResult
    {
        public int DispatcherThreadId { get; set; }
        public int ProviderThreadId { get; set; }
        public int TimerTicks { get; set; }
        public int MaxInFlight { get; set; }
        public bool Faulted { get; set; }
    }
}
