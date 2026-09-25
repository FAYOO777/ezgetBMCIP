using System;
using System.Reflection;
using System.Net;
using EzGetBmcIp;

internal static class SessionPresentationTests
{
    public static void RunAll()
    {
        RestoreFailedOverridesEndpointReachable();
        RestoreFailedWithPendingRecoveryFailureStillUsesRestorePage();
        RestoringOverridesCancelled();
        DhcpTimeoutResolvesHistorySuggestionWhenAvailable();
        DhcpTimeoutWithoutHistoryUsesTimeoutPage();
        EndpointUnreachableIsNotFailurePage();
        CandidateSourceTextIsAccurate();
        DhcpEvidenceDoesNotClaimRequestObservation();
        DhcpElapsedDisplayIsCappedAndNonSemantic();
        BrowserRequestFailureDoesNotInvalidateDiscovery();
        NetworkLifecycleTextsMatchContract();
        ExitIntentTextsMatchContract();
        WaitingPagesDoNotCreateClickablePrimaryActions();
        RestoreFailureRecommendsRetry();
        FailureKindsHavePureMappings();
        FailureGuidanceUsesCandidateAndNetworkFacts();
        HistorySuggestionKeepsItsIdentityLimit();
        FirewallTextsKeepTheirEvidenceStrength();
        DhcpTimeoutFirewallActionsCoverRiskLevels();
        ModernEndpointAccessModesUseConnectedPortOnly();
        FirewallNoticeProjectionTracksRiskAndFeedback();
        FirewallRepairProgressOverridesStaleTimeout();
    }

    private static void FirewallNoticeProjectionTracksRiskAndFeedback()
    {
        var viewModel = new MainViewModel
        {
            AppPhase = AppPhase.AdapterSelection
        };

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetField("_firewallRepairFeedback", flags)!
            .SetValue(viewModel, "已为公用网络配置当前程序的 UDP/67 接收许可，并复查规则。请手动开始。");

        typeof(MainViewModel).GetMethod("SetCurrentFirewallRisk", flags)!
            .Invoke(viewModel, new object[] { FirewallRiskLevel.None });
        Assert(viewModel.ShowAdapterFirewallNotice
            && viewModel.AdapterFirewallNoticeTitle == "防火墙许可已处理"
            && viewModel.AdapterFirewallNoticeSeverity == PresentationSeverity.Success
            && !viewModel.ShowAdapterFirewallRetry,
            "A cleared firewall risk did not project a completed notice without a retry action.");

        typeof(MainViewModel).GetMethod("SetCurrentFirewallRisk", flags)!
            .Invoke(viewModel, new object[] { FirewallRiskLevel.Warning });
        Assert(viewModel.AdapterFirewallNoticeTitle == "防火墙处理结果"
            && viewModel.AdapterFirewallNoticeSeverity == PresentationSeverity.Warning
            && viewModel.ShowAdapterFirewallRetry,
            "A remaining firewall warning did not project a retry action.");

        typeof(MainViewModel).GetField("_firewallRepairStage", flags)!
            .SetValue(viewModel, FirewallRepairStage.Completed);
        Assert(viewModel.AdapterFirewallNoticeTitle == "防火墙处理结果"
            && viewModel.AdapterFirewallNoticeSeverity == PresentationSeverity.Warning
            && viewModel.ShowAdapterFirewallRetry,
            "A completed repair with a remaining firewall warning was incorrectly shown as success.");

        typeof(MainViewModel).GetField("_firewallRepairStage", flags)!
            .SetValue(viewModel, FirewallRepairStage.Failed);
        typeof(MainViewModel).GetMethod("SetCurrentFirewallRisk", flags)!
            .Invoke(viewModel, new object[] { FirewallRiskLevel.None });
        Assert(viewModel.AdapterFirewallNoticeTitle == "防火墙处理未完成"
            && viewModel.AdapterFirewallNoticeSeverity == PresentationSeverity.Error,
            "A failed firewall repair did not project an error result.");
    }

    private static void DhcpTimeoutFirewallActionsCoverRiskLevels()
    {
        foreach (var testCase in new[]
        {
            new { Risk = FirewallRiskLevel.None, Action = ModernActionKind.ExportSupport },
            new { Risk = FirewallRiskLevel.Warning, Action = ModernActionKind.RepairFirewall },
            new { Risk = FirewallRiskLevel.High, Action = ModernActionKind.RepairFirewall },
            new { Risk = FirewallRiskLevel.Unknown, Action = ModernActionKind.RepairFirewall }
        })
        {
            var viewModel = new MainViewModel
            {
                AppPhase = AppPhase.FlowRunning
            };
            viewModel.SessionState.Workflow = DiscoveryWorkflowState.Failed;
            viewModel.SessionState.Network = NetworkLifecycleState.ConfigurationRestored;
            viewModel.SessionState.SetFailure(FailureKind.DhcpTimedOut, "timeout");

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainViewModel).GetMethod("SetCurrentFirewallRisk", flags)!
                .Invoke(viewModel, new object[] { testCase.Risk });

            var presentation = viewModel.ModernRuntime;
            Assert(presentation.Page == SessionPageKind.DhcpTimedOut
                && presentation.PrimaryAction == testCase.Action,
                "DHCP timeout action mismatch for firewall risk " + testCase.Risk + ".");

            if (testCase.Risk == FirewallRiskLevel.Warning || testCase.Risk == FirewallRiskLevel.Unknown)
            {
                Assert(presentation.NoticeText.Contains("尚不能确认") || presentation.NoticeText.Contains("无法完整评估"),
                    "Uncertain firewall risk lost its evidence-strength wording for " + testCase.Risk + ".");
            }
        }
    }

    private static void FirewallRepairProgressOverridesStaleTimeout()
    {
        var viewModel = new MainViewModel
        {
            AppPhase = AppPhase.FlowRunning
        };
        viewModel.SessionState.Workflow = DiscoveryWorkflowState.Failed;
        viewModel.SessionState.Network = NetworkLifecycleState.ConfigurationRestored;
        viewModel.SessionState.SetFailure(FailureKind.DhcpTimedOut, "previous timeout");

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetField("_firewallRepairBusy", flags)!.SetValue(viewModel, true);
        typeof(MainViewModel).GetField("_firewallRepairStage", flags)!
            .SetValue(viewModel, FirewallRepairStage.ApplyingRules);
        typeof(MainViewModel).GetField("_firewallRepairFeedback", flags)!
            .SetValue(viewModel, "本机网卡已恢复，正在处理防火墙规则…");

        var presentation = viewModel.ModernRuntime;
        Assert(viewModel.CurrentSessionPage == SessionPageKind.DhcpTimedOut,
            "Test setup did not retain the previous DHCP timeout state.");
        Assert(viewModel.ShowModernRuntimeHost && presentation.Page == SessionPageKind.FirewallRepairing,
            "Firewall repair did not override the stale DHCP timeout presentation.");
        Assert(presentation.Title == "正在处理防火墙并准备重试" && presentation.ShowProgressRing,
            "Firewall repair presentation omitted its dedicated progress state.");
        Assert(presentation.PrimaryAction == ModernActionKind.None && presentation.SecondaryAction == ModernActionKind.None,
            "Firewall repair progress exposed an action while the operation was busy.");
        Assert(presentation.Stages.Count == 3
            && presentation.Stages[0].State == ModernStageState.Done
            && presentation.Stages[1].State == ModernStageState.Done
            && presentation.Stages[2].State == ModernStageState.Active,
            "Firewall repair stages did not reflect rule application after network recovery.");
    }

    private static void RestoreFailedOverridesEndpointReachable()
    {
        var state = ReachableState();
        state.Network = NetworkLifecycleState.RestoreFailed;

        Assert(SessionPresentation.ResolveSessionPage(state, false) == SessionPageKind.RestoreFailed,
            "RestoreFailed did not override EndpointReachable.");
    }

    private static void RestoreFailedWithPendingRecoveryFailureStillUsesRestorePage()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Failed,
            Network = NetworkLifecycleState.RestoreFailed
        };
        state.SetFailure(FailureKind.PendingRecoveryFailed, "startup recovery did not finish");

        var page = SessionPresentation.GetSessionPagePresentation(state, false, "https");
        Assert(page.Page == SessionPageKind.RestoreFailed && page.Title == "本机网卡尚未确认恢复",
            "PendingRecoveryFailed incorrectly displaced the RestoreFailed page.");
        Assert(SessionPresentation.GetFailurePresentation(state.Failure).Summary.Contains("遗留的恢复记录"),
            "PendingRecoveryFailed did not retain its reason explanation.");
    }

    private static void RestoringOverridesCancelled()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Cancelled,
            Network = NetworkLifecycleState.Restoring
        };

        Assert(SessionPresentation.ResolveSessionPage(state, false) == SessionPageKind.Restoring,
            "Restoring did not override Cancelled.");
    }

    private static void DhcpTimeoutResolvesHistorySuggestionWhenAvailable()
    {
        var state = DhcpTimeoutState();
        Assert(SessionPresentation.ResolveSessionPage(state, true) == SessionPageKind.HistoryRetrySuggestion,
            "A DHCP timeout with eligible history did not select HistoryRetrySuggestion.");
    }

    private static void DhcpTimeoutWithoutHistoryUsesTimeoutPage()
    {
        var state = DhcpTimeoutState();
        Assert(SessionPresentation.ResolveSessionPage(state, false) == SessionPageKind.DhcpTimedOut,
            "A DHCP timeout without history did not select DhcpTimedOut.");
    }

    private static void EndpointUnreachableIsNotFailurePage()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.EndpointUnreachable,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };
        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);

        var page = SessionPresentation.GetSessionPagePresentation(state, false, "https");
        Assert(page.Page == SessionPageKind.EndpointUnreachable
               && page.Title == "设备地址没有回应"
               && page.RecommendedActionKind == PageActionKind.RetryEndpointProbe,
            "EndpointUnreachable was not kept separate from Failure.");
    }

    private static void CandidateSourceTextIsAccurate()
    {
        var dhcp = new CandidateAddressInfo("10.77.77.100", CandidateAddressSource.DhcpAck);
        var existing = new CandidateAddressInfo("10.77.77.100", CandidateAddressSource.ExistingConfiguredAddress);

        Assert(SessionPresentation.GetCandidateSourceText(dhcp) == "来源：已发送 DHCP ACK 的候选地址",
            "DHCP candidate source text was inaccurate.");
        Assert(SessionPresentation.GetCandidateSourceText(existing) == "来源：在预期地址发现的候选地址",
            "Existing-address candidate source text was inaccurate.");
    }

    private static void DhcpEvidenceDoesNotClaimRequestObservation()
    {
        var none = SessionPresentation.GetDhcpEvidencePresentation(DhcpEvidenceState.None);
        var ack = SessionPresentation.GetDhcpEvidencePresentation(DhcpEvidenceState.AckSent);

        Assert(none.Text == "正在等待地址分配流程完成…" && !none.Text.Contains("没有收到"),
            "DHCP None presentation claimed that no request was observed.");
        Assert(ack.Text == "已发送候选地址，正在确认地址可达性…",
            "DHCP ACK presentation did not describe the actual ACK evidence.");
    }

    private static void DhcpElapsedDisplayIsCappedAndNonSemantic()
    {
        Assert(MainViewModel.FormatDhcpElapsed(TimeSpan.Zero) == "00:00",
            "DHCP elapsed display did not start at zero.");
        Assert(MainViewModel.FormatDhcpElapsed(TimeSpan.FromSeconds(42)) == "00:42",
            "DHCP elapsed display did not format seconds correctly.");
        Assert(MainViewModel.FormatDhcpElapsed(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(59)) == "02:59",
            "DHCP elapsed display did not format the final wait minute correctly.");
        Assert(MainViewModel.FormatDhcpElapsed(TimeSpan.FromMinutes(5)) == "03:00",
            "DHCP elapsed display continued beyond the business timeout boundary.");
    }

    private static void BrowserRequestFailureDoesNotInvalidateDiscovery()
    {
        var state = ReachableState();
        state.BrowserLaunchResult = BrowserLaunchResult.RequestFailed;

        var browser = SessionPresentation.GetBrowserStatusPresentation(state.BrowserLaunchResult);
        Assert(state.IsDiscoverySuccessful,
            "Browser request failure invalidated discovery success.");
        Assert(browser.Text == "未能打开系统浏览器。",
            "Browser request failure text did not state the browser request result.");
        Assert(SessionPresentation.GetBrowserStatusPresentation(BrowserLaunchResult.Requested).Text == "已交给系统浏览器打开。",
            "Browser request success text did not state the browser request result.");
        Assert(string.IsNullOrEmpty(SessionPresentation.GetBrowserStatusPresentation(BrowserLaunchResult.NotRequested).Text),
            "Browser status should be hidden before a browser request.");
    }

    private static void NetworkLifecycleTextsMatchContract()
    {
        AssertNetwork(NetworkLifecycleState.Untouched, "", "本机网卡：尚未修改", PresentationSeverity.Normal);
        AssertNetwork(NetworkLifecycleState.RecoveryPrepared, "", "本机网卡：恢复保护已就绪，尚未修改网络", PresentationSeverity.Progress);
        AssertNetwork(NetworkLifecycleState.TemporaryConfigurationMayBeActive, "", "本机网卡：可能已开始切换为临时配置；退出时将恢复", PresentationSeverity.Warning);
        AssertNetwork(NetworkLifecycleState.TemporaryConfigurationActive, "", "本机网卡：正在使用临时直连配置", PresentationSeverity.Warning);
        AssertNetwork(NetworkLifecycleState.Restoring, "以太网 2", "本机网卡：正在恢复“以太网 2”的原配置", PresentationSeverity.Progress);
        AssertNetwork(NetworkLifecycleState.ConfigurationRestored, "", "本机网卡：已恢复使用工具前的配置", PresentationSeverity.Success);
        AssertNetwork(NetworkLifecycleState.RestoreFailed, "", "本机网卡：尚未确认恢复，请先处理此问题", PresentationSeverity.Error);

        var prepared = SessionPresentation.GetNetworkStatusPresentation(NetworkLifecycleState.RecoveryPrepared, "");
        var mayBeActive = SessionPresentation.GetNetworkStatusPresentation(NetworkLifecycleState.TemporaryConfigurationMayBeActive, "");
        var restoringWithoutAdapter = SessionPresentation.GetNetworkStatusPresentation(NetworkLifecycleState.Restoring, "");
        Assert(!prepared.Text.Contains("已修改"), "RecoveryPrepared claimed that the adapter was already modified.");
        Assert(mayBeActive.Text.Contains("退出时将恢复"), "TemporaryConfigurationMayBeActive omitted the conservative recovery warning.");
        Assert(!restoringWithoutAdapter.Text.Contains("“”"), "Restoring without an adapter name rendered an empty quoted name.");
    }

    private static void ExitIntentTextsMatchContract()
    {
        AssertExit(ExitIntent.Exit, "退出", true);
        AssertExit(ExitIntent.StopAndExit, "停止并退出", true);
        AssertExit(ExitIntent.CleanupAndExit, "清理并退出", true);
        AssertExit(ExitIntent.StopAndRestore, "停止并恢复网卡", true);
        AssertExit(ExitIntent.RestoreAndExit, "恢复网卡并退出", true);
        AssertExit(ExitIntent.WaitForRestore, "正在恢复网卡…", false);
        AssertExit(ExitIntent.RetryRestore, "重试恢复网卡", true);
    }

    private static void WaitingPagesDoNotCreateClickablePrimaryActions()
    {
        foreach (var workflow in new[]
        {
            DiscoveryWorkflowState.WaitingForLink,
            DiscoveryWorkflowState.ConfiguringNetwork,
            DiscoveryWorkflowState.WaitingForDhcp,
            DiscoveryWorkflowState.ProbingEndpoint
        })
        {
            var state = new CurrentSessionState { Workflow = workflow };
            var presentation = SessionPresentation.GetSessionPagePresentation(state, false, "https");
            Assert(presentation.RecommendedActionKind == PageActionKind.None,
                workflow + " incorrectly created a clickable primary action.");
        }

        var restoring = new CurrentSessionState { Network = NetworkLifecycleState.Restoring };
        Assert(SessionPresentation.GetSessionPagePresentation(restoring, false, "https").RecommendedActionKind == PageActionKind.None,
            "Restoring incorrectly created a clickable primary action.");
    }

    private static void RestoreFailureRecommendsRetry()
    {
        var state = new CurrentSessionState { Network = NetworkLifecycleState.RestoreFailed };
        var presentation = SessionPresentation.GetSessionPagePresentation(state, false, "https");
        Assert(presentation.RecommendedActionKind == PageActionKind.RetryRestore,
            "RestoreFailed did not recommend RetryRestore.");
    }

    private static void FailureKindsHavePureMappings()
    {
        foreach (FailureKind kind in Enum.GetValues(typeof(FailureKind)))
        {
            var presentation = SessionPresentation.GetFailurePresentation(
                kind == FailureKind.None ? null : new FailureInfo(kind, "test"));
            Assert(!string.IsNullOrWhiteSpace(presentation.Title) && !string.IsNullOrWhiteSpace(presentation.Summary),
                "FailureKind " + kind + " did not have a title and summary projection.");
        }
    }

    private static void FailureGuidanceUsesCandidateAndNetworkFacts()
    {
        var endpointFailure = new FailureInfo(FailureKind.EndpointProbeError, "test");
        var withoutCandidate = SessionPresentation.GetFailureRecommendedActionText(
            endpointFailure, false, NetworkLifecycleState.TemporaryConfigurationActive);
        var withCandidate = SessionPresentation.GetFailureRecommendedActionText(
            endpointFailure, true, NetworkLifecycleState.TemporaryConfigurationActive);

        Assert(!withoutCandidate.Contains("重新检测") && withCandidate.Contains("重新检测"),
            "EndpointProbeError guidance did not distinguish candidate availability.");

        var adapterUnavailable = new FailureInfo(FailureKind.EndpointAdapterUnavailable, "resolver test");
        var adapterPresentation = SessionPresentation.GetFailurePresentation(adapterUnavailable);
        Assert(adapterPresentation.RecommendedActionKind == PageActionKind.RetryEndpointProbe
               && adapterPresentation.Summary.Contains("Windows")
               && SessionPresentation.GetFailureRecommendedActionText(
                   adapterUnavailable, true, NetworkLifecycleState.TemporaryConfigurationActive).Contains("重新检测"),
            "EndpointAdapterUnavailable did not retain the candidate retry action and explicit Windows guidance.");

        var temporaryFailure = SessionPresentation.GetFailureRecommendedActionText(
            new FailureInfo(FailureKind.TemporaryNetworkConfigurationFailed, "test"),
            false, NetworkLifecycleState.TemporaryConfigurationMayBeActive);
        Assert(temporaryFailure.Contains("恢复"),
            "Temporary configuration failure guidance did not prioritize network recovery.");
    }

    private static void HistorySuggestionKeepsItsIdentityLimit()
    {
        var state = DhcpTimeoutState();
        var presentation = SessionPresentation.GetSessionPagePresentation(state, true, "https");
        Assert(presentation.Title == "可尝试上次使用的网段"
               && presentation.Summary.Contains("确认过地址可达"),
            "History retry presentation lost its bounded historical-evidence wording.");
    }

    private static void FirewallTextsKeepTheirEvidenceStrength()
    {
        var none = SessionPresentation.GetFirewallPresentation(FirewallRiskLevel.None);
        var warning = SessionPresentation.GetFirewallPresentation(FirewallRiskLevel.Warning);
        var high = SessionPresentation.GetFirewallPresentation(FirewallRiskLevel.High);
        var unknown = SessionPresentation.GetFirewallPresentation(FirewallRiskLevel.Unknown);

        Assert(none.Summary.Contains("未发现明显的防火墙阻断证据") && !none.Summary.Contains("故障"),
            "None firewall presentation overstated an absence of evidence.");
        Assert(warning.Summary.Contains("兼容性风险") && warning.Summary.Contains("尚不能确认"),
            "Warning firewall presentation asserted causal certainty.");
        Assert(high.Summary.Contains("显式防火墙阻止规则"),
            "High firewall presentation did not preserve its explicit-block evidence.");
        Assert(unknown.Summary == "无法完整评估当前防火墙配置。",
            "Unknown firewall presentation did not preserve uncertainty.");
    }

    private static CurrentSessionState ReachableState()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.EndpointReachable,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };
        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);
        return state;
    }

    private static void ModernEndpointAccessModesUseConnectedPortOnly()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var testCase in new[]
        {
            new { Ping = false, Https = false, Http = false, Title = "设备地址没有回应", Action = ModernActionKind.RetryEndpoint, Address = "10.77.77.100", ShowHttpsFallback = false, ShowHttpFallback = false },
            new { Ping = true, Https = false, Http = false, Title = "设备地址有回应", Action = ModernActionKind.RetryEndpoint, Address = "10.77.77.100", ShowHttpsFallback = false, ShowHttpFallback = false },
            new { Ping = false, Https = true, Http = false, Title = "TCP 443 端口可连接", Action = ModernActionKind.OpenManagementPage, Address = "https://10.77.77.100", ShowHttpsFallback = false, ShowHttpFallback = false },
            new { Ping = false, Https = false, Http = true, Title = "TCP 80 端口可连接", Action = ModernActionKind.OpenManagementPage, Address = "http://10.77.77.100", ShowHttpsFallback = true, ShowHttpFallback = false },
            new { Ping = true, Https = true, Http = true, Title = "TCP 443、80 端口均可连接", Action = ModernActionKind.OpenManagementPage, Address = "https://10.77.77.100", ShowHttpsFallback = false, ShowHttpFallback = true }
        })
        {
            var viewModel = new MainViewModel { AppPhase = AppPhase.FlowRunning };
            viewModel.SessionState.Workflow = testCase.Ping || testCase.Https || testCase.Http
                ? DiscoveryWorkflowState.EndpointReachable
                : DiscoveryWorkflowState.EndpointUnreachable;
            viewModel.SessionState.Network = NetworkLifecycleState.TemporaryConfigurationActive;
            viewModel.SessionState.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);
            typeof(MainViewModel).GetField("_lastReachabilityResult", flags)!.SetValue(viewModel, new BmcReachabilityResult
            {
                TargetAddress = IPAddress.Parse("10.77.77.100"),
                PingSucceeded = testCase.Ping,
                HttpsPortOpen = testCase.Https,
                HttpPortOpen = testCase.Http
            });
            typeof(MainViewModel).GetField("_preferredBmcScheme", flags)!.SetValue(
                viewModel, testCase.Http && !testCase.Https ? "http" : "https");

            var presentation = viewModel.ModernRuntime;
            Assert(presentation.Title == testCase.Title && presentation.PrimaryAction == testCase.Action,
                "Modern endpoint title or action mismatch for " + testCase.Title + ".");
            Assert(presentation.PrimaryValue == testCase.Address,
                "Modern endpoint address mismatch for " + testCase.Title + ".");
            var expectedUrl = testCase.Https
                ? "https://10.77.77.100"
                : testCase.Http
                    ? "http://10.77.77.100"
                    : string.Empty;
            Assert(viewModel.DiscoveredIpUrl == expectedUrl,
                "A management URL was exposed before a TCP management port was confirmed for " + testCase.Title + ".");
            Assert(presentation.ShowHttpsFallbackAction == testCase.ShowHttpsFallback &&
                   presentation.ShowHttpFallbackAction == testCase.ShowHttpFallback &&
                   presentation.ShowOtherEndpointActions ==
                       (testCase.ShowHttpsFallback || testCase.ShowHttpFallback),
                "Modern endpoint alternate actions did not match the detected management ports.");
        }
    }

    private static CurrentSessionState DhcpTimeoutState()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Failed,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };
        state.SetFailure(FailureKind.DhcpTimedOut, "timeout");
        return state;
    }

    private static void AssertNetwork(
        NetworkLifecycleState state,
        string adapterDisplayName,
        string expectedText,
        PresentationSeverity expectedSeverity)
    {
        var presentation = SessionPresentation.GetNetworkStatusPresentation(state, adapterDisplayName);
        Assert(presentation.Text == expectedText && presentation.Severity == expectedSeverity,
            "Network presentation mismatch for " + state + ".");
    }

    private static void AssertExit(ExitIntent intent, string expectedText, bool expectedEnabled)
    {
        Assert(SessionPresentation.GetExitActionText(intent) == expectedText,
            "Exit text mismatch for " + intent + ".");
        Assert(SessionPresentation.IsExitActionEnabled(intent) == expectedEnabled,
            "Exit enabled-state mismatch for " + intent + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
