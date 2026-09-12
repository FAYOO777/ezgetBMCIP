using System;
using EzGetBmcIp;

internal static class SessionStateTests
{
    public static void RunAll()
    {
        InitialStateIsSafeToExit();
        CandidateAddressDoesNotConfirmEndpoint();
        EndpointReachableDoesNotRequireBrowserRequest();
        BrowserLaunchFailureDoesNotInvalidateEndpointReachability();
        RecoveryPreparedRunningWorkflowRequiresCleanup();
        RecoveryPreparedTerminalWorkflowRequiresCleanup();
        PreparedSessionCleanupCompletionHasNoRecoveryRequirement();
        PotentialMutationRequiresRecovery();
        ActiveTemporaryConfigurationRequiresRecovery();
        MainViewModelExitTextUsesSessionIntent();
        ExitIntentMatrixMatchesSharedContract();
        ConfigurationRestoredAllowsNormalExit();
        RestoreFailureBlocksNormalExit();
        RestoringWaitsForExistingRecovery();
        CancellationClearsFailureAndIsTerminal();
        NewFlowResetClearsPriorSessionFacts();
        DhcpAckDoesNotImplyDiscoverySuccess();
        DhcpTimeoutDoesNotCreateAddressOrEndpointFacts();
    }

    private static void InitialStateIsSafeToExit()
    {
        var state = new CurrentSessionState();

        Assert(state.Workflow == DiscoveryWorkflowState.Ready, "Initial workflow was not Ready.");
        Assert(state.Network == NetworkLifecycleState.Untouched, "Initial network state was not Untouched.");
        Assert(state.DhcpEvidence == DhcpEvidenceState.None, "Initial DHCP evidence was not None.");
        Assert(!state.HasCandidateAddress, "Initial state unexpectedly had a candidate address.");
        Assert(!state.IsEndpointReachable, "Initial state unexpectedly reported an endpoint.");
        Assert(!state.RequiresNetworkRecovery && !state.RequiresCleanup,
            "Initial state unexpectedly required recovery or cleanup.");
        Assert(state.CanExitNormally && state.ExitIntent == ExitIntent.Exit,
            "Initial state was not safely exit-ready.");
    }

    private static void CandidateAddressDoesNotConfirmEndpoint()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.ProbingEndpoint
        };
        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);

        Assert(state.HasCandidateAddress, "Candidate address was not retained.");
        var candidate = state.CandidateAddress;
        Assert(candidate is not null && candidate.IPv4Address == "10.77.77.100" &&
               candidate.Source == CandidateAddressSource.DhcpAck,
            "Candidate-address facts were not retained.");
        Assert(!state.IsEndpointReachable && !state.IsDiscoverySuccessful,
            "A candidate address incorrectly confirmed discovery success.");
    }

    private static void EndpointReachableDoesNotRequireBrowserRequest()
    {
        var state = CreateReachableState(BrowserLaunchResult.NotRequested);

        Assert(state.IsEndpointReachable && state.IsDiscoverySuccessful,
            "Reachable endpoint with a candidate address was not discovery success.");
        Assert(state.BrowserLaunchResult == BrowserLaunchResult.NotRequested,
            "Endpoint reachability unexpectedly required a browser request.");
    }

    private static void BrowserLaunchFailureDoesNotInvalidateEndpointReachability()
    {
        var state = CreateReachableState(BrowserLaunchResult.RequestFailed);

        Assert(state.IsEndpointReachable && state.IsDiscoverySuccessful,
            "Browser-launch failure incorrectly invalidated discovery success.");
        Assert(state.BrowserLaunchResult == BrowserLaunchResult.RequestFailed,
            "Browser-launch failure was not retained as presentation information.");
    }

    private static void RecoveryPreparedRunningWorkflowRequiresCleanup()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.ConfiguringNetwork,
            Network = NetworkLifecycleState.RecoveryPrepared
        };

        Assert(!state.RequiresNetworkRecovery && state.RequiresCleanup && !state.CanExitNormally,
            "RecoveryPrepared running workflow must require session cleanup, not network recovery.");
        Assert(state.ExitIntent == ExitIntent.CleanupAndExit,
            "RecoveryPrepared running workflow must clean up the recovery session before exit.");
    }

    private static void RecoveryPreparedTerminalWorkflowRequiresCleanup()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Failed,
            Network = NetworkLifecycleState.RecoveryPrepared
        };

        Assert(!state.RequiresNetworkRecovery && state.RequiresCleanup && !state.CanExitNormally,
            "RecoveryPrepared terminal workflow must require session cleanup, not network recovery.");
        Assert(state.ExitIntent == ExitIntent.CleanupAndExit,
            "RecoveryPrepared terminal workflow must not be represented as a stop or restore action.");
    }

    private static void PotentialMutationRequiresRecovery()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.ConfiguringNetwork,
            Network = NetworkLifecycleState.TemporaryConfigurationMayBeActive
        };

        Assert(state.RequiresNetworkRecovery && state.RequiresCleanup,
            "Potential mutation was not treated as requiring recovery.");
        Assert(!state.CanExitNormally && state.ExitIntent == ExitIntent.StopAndRestore,
            "Potential mutation did not require stop-and-restore exit handling.");
    }

    private static void PreparedSessionCleanupCompletionHasNoRecoveryRequirement()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Cancelled,
            Network = NetworkLifecycleState.RecoveryPrepared
        };

        // This is the required postcondition of the RecoveryPrepared-only cleanup branch:
        // snapshot/watchdog teardown completes without claiming that an adapter was restored.
        state.Network = NetworkLifecycleState.Untouched;
        Assert(!state.RequiresNetworkRecovery && !state.RequiresCleanup && state.CanExitNormally,
            "Prepared-session cleanup completion left a recovery requirement behind.");
        Assert(state.ExitIntent == ExitIntent.Exit,
            "Prepared-session cleanup completion did not return to normal exit semantics.");
    }

    private static void ActiveTemporaryConfigurationRequiresRecovery()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.EndpointUnreachable,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };

        Assert(state.RequiresNetworkRecovery && state.RequiresCleanup,
            "Active temporary configuration was not treated as requiring recovery.");
        Assert(!state.HasFailure,
            "EndpointUnreachable incorrectly carried a failure fact.");
        Assert(!state.CanExitNormally && state.ExitIntent == ExitIntent.RestoreAndExit,
            "Completed discovery with a temporary configuration did not require restore-and-exit.");
    }

    private static void MainViewModelExitTextUsesSessionIntent()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.EndpointUnreachable,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };

        Assert(state.ExitIntent == ExitIntent.RestoreAndExit,
            "A temporary configuration without a candidate address did not require restore-and-exit.");
        Assert(SessionPresentation.GetExitActionText(state.ExitIntent) == "恢复网卡并退出",
            "Exit text was not projected from the shared restore-and-exit intent.");

        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);
        Assert(!state.IsEndpointReachable && state.ExitIntent == ExitIntent.RestoreAndExit,
            "A candidate address changed endpoint or exit semantics before a reachable port was confirmed.");
        Assert(SessionPresentation.GetExitActionText(state.ExitIntent) == "恢复网卡并退出",
            "Exit text changed when only the candidate-address fact changed.");
    }

    private static void ExitIntentMatrixMatchesSharedContract()
    {
        AssertExitIntent(DiscoveryWorkflowState.Ready, NetworkLifecycleState.Untouched, ExitIntent.Exit);
        AssertExitIntent(DiscoveryWorkflowState.WaitingForLink, NetworkLifecycleState.Untouched, ExitIntent.StopAndExit);
        AssertExitIntent(DiscoveryWorkflowState.ConfiguringNetwork, NetworkLifecycleState.RecoveryPrepared, ExitIntent.CleanupAndExit);
        AssertExitIntent(DiscoveryWorkflowState.WaitingForDhcp, NetworkLifecycleState.TemporaryConfigurationActive, ExitIntent.StopAndRestore);
        AssertExitIntent(DiscoveryWorkflowState.EndpointReachable, NetworkLifecycleState.TemporaryConfigurationActive, ExitIntent.RestoreAndExit);
        AssertExitIntent(DiscoveryWorkflowState.EndpointUnreachable, NetworkLifecycleState.TemporaryConfigurationActive, ExitIntent.RestoreAndExit);
        AssertExitIntent(DiscoveryWorkflowState.Failed, NetworkLifecycleState.TemporaryConfigurationActive, ExitIntent.RestoreAndExit);
        AssertExitIntent(DiscoveryWorkflowState.Cancelled, NetworkLifecycleState.TemporaryConfigurationActive, ExitIntent.RestoreAndExit);
        AssertExitIntent(DiscoveryWorkflowState.Failed, NetworkLifecycleState.Restoring, ExitIntent.WaitForRestore);
        AssertExitIntent(DiscoveryWorkflowState.Cancelled, NetworkLifecycleState.ConfigurationRestored, ExitIntent.Exit);
        AssertExitIntent(DiscoveryWorkflowState.Cancelled, NetworkLifecycleState.RestoreFailed, ExitIntent.RetryRestore);
    }

    private static void AssertExitIntent(
        DiscoveryWorkflowState workflow,
        NetworkLifecycleState network,
        ExitIntent expected)
    {
        var state = new CurrentSessionState { Workflow = workflow, Network = network };
        Assert(state.ExitIntent == expected,
            "Exit intent mismatch for " + workflow + " + " + network + ". Expected " + expected + ".");
    }

    private static void ConfigurationRestoredAllowsNormalExit()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Cancelled,
            Network = NetworkLifecycleState.ConfigurationRestored
        };

        Assert(!state.RequiresNetworkRecovery && !state.RequiresCleanup,
            "ConfigurationRestored still required recovery or cleanup.");
        Assert(state.CanExitNormally && state.ExitIntent == ExitIntent.Exit,
            "ConfigurationRestored did not permit normal exit.");
    }

    private static void RestoreFailureBlocksNormalExit()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Cancelled,
            Network = NetworkLifecycleState.RestoreFailed
        };
        state.SetFailure(FailureKind.RestoreFailed, "restore verification failed");

        Assert(state.RequiresNetworkRecovery && state.RequiresCleanup,
            "RestoreFailed was not treated as a recovery requirement.");
        Assert(!state.CanExitNormally && state.ExitIntent == ExitIntent.RetryRestore,
            "RestoreFailed incorrectly permitted ordinary exit.");
        var failure = state.Failure;
        Assert(state.HasFailure && failure is not null && failure.Kind == FailureKind.RestoreFailed,
            "Restore failure details were not retained independently from workflow.");
    }

    private static void RestoringWaitsForExistingRecovery()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Failed,
            Network = NetworkLifecycleState.Restoring
        };

        Assert(state.ExitIntent == ExitIntent.WaitForRestore,
            "Restoring did not block a second recovery attempt through WaitForRestore.");
    }

    private static void CancellationClearsFailureAndIsTerminal()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.WaitingForDhcp,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };
        state.SetFailure(FailureKind.DhcpTimedOut, "timeout raced with user cancellation");

        MainViewModel.ApplyFlowCancellation(state);

        Assert(state.Workflow == DiscoveryWorkflowState.Cancelled && !state.HasFailure,
            "Cancellation did not clear a concurrent failure or retain a terminal Cancelled workflow.");
        Assert(state.ExitIntent == ExitIntent.RestoreAndExit,
            "Cancellation changed the independent network recovery requirement.");
    }

    private static void NewFlowResetClearsPriorSessionFacts()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.EndpointReachable,
            Network = NetworkLifecycleState.ConfigurationRestored,
            DhcpEvidence = DhcpEvidenceState.AckSent,
            BrowserLaunchResult = BrowserLaunchResult.RequestFailed
        };
        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);
        state.SetFailure(FailureKind.EndpointProbeError, "previous flow detail");

        MainViewModel.ResetSessionStateForNewFlow(state);

        Assert(state.Workflow == DiscoveryWorkflowState.Ready
            && state.Network == NetworkLifecycleState.Untouched
            && state.DhcpEvidence == DhcpEvidenceState.None
            && state.BrowserLaunchResult == BrowserLaunchResult.NotRequested
            && !state.HasCandidateAddress
            && !state.HasFailure,
            "A new flow retained state facts from the prior session.");
    }

    private static void DhcpAckDoesNotImplyDiscoverySuccess()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.WaitingForDhcp,
            DhcpEvidence = DhcpEvidenceState.AckSent
        };
        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);

        Assert(state.DhcpEvidence == DhcpEvidenceState.AckSent,
            "DHCP ACK evidence was not retained.");
        Assert(state.HasCandidateAddress,
            "DHCP ACK did not retain its candidate-address fact.");
        Assert(!state.IsEndpointReachable && !state.IsDiscoverySuccessful,
            "DHCP ACK incorrectly implied endpoint reachability or discovery success.");
    }

    private static void DhcpTimeoutDoesNotCreateAddressOrEndpointFacts()
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.Failed,
            Network = NetworkLifecycleState.TemporaryConfigurationActive
        };
        state.SetFailure(FailureKind.DhcpTimedOut, "three-minute wait elapsed");

        var failure = state.Failure;
        Assert(state.HasFailure && failure is not null && failure.Kind == FailureKind.DhcpTimedOut,
            "DHCP timeout failure was not retained.");
        Assert(!state.HasCandidateAddress && !state.IsEndpointReachable && !state.IsDiscoverySuccessful,
            "DHCP timeout incorrectly created candidate-address or endpoint-success facts.");
    }

    private static CurrentSessionState CreateReachableState(BrowserLaunchResult browserLaunchResult)
    {
        var state = new CurrentSessionState
        {
            Workflow = DiscoveryWorkflowState.EndpointReachable,
            Network = NetworkLifecycleState.TemporaryConfigurationActive,
            BrowserLaunchResult = browserLaunchResult
        };
        state.SetCandidateAddress("10.77.77.100", CandidateAddressSource.DhcpAck);
        return state;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
