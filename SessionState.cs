#nullable enable

using System;

namespace EzGetBmcIp
{
    // Represents the discovery task from the user's point of view. It does not
    // include DHCP packet evidence or browser-launch presentation outcomes.
    public enum DiscoveryWorkflowState
    {
        Ready,
        WaitingForLink,
        ConfiguringNetwork,
        WaitingForDhcp,
        ProbingEndpoint,
        EndpointReachable,
        EndpointUnreachable,
        Failed,
        Cancelled
    }

    // Represents the local adapter's lifecycle independently from discovery.
    // This enum is the shared basis for recovery and exit semantics.
    public enum NetworkLifecycleState
    {
        // No recovery snapshot was prepared and no network mutation was attempted.
        Untouched,

        // Recovery snapshot and watchdog are prepared, but no network-setting command ran.
        RecoveryPrepared,

        // A network-setting operation has begun. Even if it throws, recovery is required.
        TemporaryConfigurationMayBeActive,

        // The temporary local IPv4 configuration completed its current verification.
        TemporaryConfigurationActive,

        // The session is stopping and the original configuration is being restored.
        Restoring,

        // The supported configuration restore completed and was verified. This does not
        // claim that DHCP renewed, Internet access returned, or general connectivity works.
        ConfigurationRestored,

        // The supported configuration restore could not be confirmed; ordinary exit is unsafe.
        RestoreFailed
    }

    // Facts observed by this tool. AckSent does not mean that the device applied the address.
    public enum DhcpEvidenceState
    {
        None,
        RequestObserved,
        AckSent
    }

    public enum CandidateAddressSource
    {
        DhcpAck,
        ExistingConfiguredAddress
    }

    public enum FailureKind
    {
        None,
        InitializationFailed,
        PendingRecoveryFailed,
        AdapterUnavailable,
        LinkCheckFailed,
        OriginalConfigurationCaptureFailed,
        TemporaryNetworkConfigurationFailed,
        DhcpListenerFailed,
        DhcpTimedOut,
        EndpointProbeError,
        EndpointAdapterUnavailable,
        RestoreFailed,
        Unexpected
    }

    // Browser launch is presentation/diagnostic information, not discovery success.
    public enum BrowserLaunchResult
    {
        NotRequested,
        Requested,
        RequestFailed
    }

    // Shared intent for a host UI's exit command. It deliberately contains no UI text.
    public enum ExitIntent
    {
        Exit,
        StopAndExit,
        CleanupAndExit,
        StopAndRestore,
        RestoreAndExit,
        RetryRestore,
        WaitForRestore
    }

    public sealed class CandidateAddressInfo
    {
        public CandidateAddressInfo(string ipv4Address, CandidateAddressSource source)
        {
            if (string.IsNullOrWhiteSpace(ipv4Address))
                throw new ArgumentException("A candidate IPv4 address is required.", nameof(ipv4Address));

            IPv4Address = ipv4Address;
            Source = source;
        }

        public string IPv4Address { get; private set; }
        public CandidateAddressSource Source { get; private set; }
    }

    public sealed class FailureInfo
    {
        public FailureInfo(FailureKind kind, string? technicalDetail = null)
        {
            Kind = kind;
            TechnicalDetail = technicalDetail;
        }

        public FailureKind Kind { get; private set; }
        public string? TechnicalDetail { get; private set; }
    }

    // A compact, framework-independent session snapshot. Network callers update it at
    // confirmed operation boundaries; ViewModels later project it into UI-specific text.
    public sealed class CurrentSessionState
    {
        public CurrentSessionState()
        {
            Workflow = DiscoveryWorkflowState.Ready;
            Network = NetworkLifecycleState.Untouched;
            DhcpEvidence = DhcpEvidenceState.None;
            BrowserLaunchResult = BrowserLaunchResult.NotRequested;
        }

        public DiscoveryWorkflowState Workflow { get; set; }
        public NetworkLifecycleState Network { get; set; }
        public DhcpEvidenceState DhcpEvidence { get; set; }
        public BrowserLaunchResult BrowserLaunchResult { get; set; }
        public CandidateAddressInfo? CandidateAddress { get; private set; }
        public FailureInfo? Failure { get; private set; }

        public bool HasCandidateAddress
        {
            get { return CandidateAddress != null; }
        }

        public bool IsEndpointReachable
        {
            get { return Workflow == DiscoveryWorkflowState.EndpointReachable; }
        }

        public bool IsDiscoverySuccessful
        {
            get { return HasCandidateAddress && IsEndpointReachable; }
        }

        public bool HasFailure
        {
            get { return Failure != null && Failure.Kind != FailureKind.None; }
        }

        // RecoveryPrepared only needs session cleanup (snapshot/watchdog teardown), not a
        // network restore, because no mutable network-setting operation has started yet.
        public bool RequiresNetworkRecovery
        {
            get
            {
                return Network == NetworkLifecycleState.TemporaryConfigurationMayBeActive
                    || Network == NetworkLifecycleState.TemporaryConfigurationActive
                    || Network == NetworkLifecycleState.Restoring
                    || Network == NetworkLifecycleState.RestoreFailed;
            }
        }

        public bool RequiresCleanup
        {
            get
            {
                return Network == NetworkLifecycleState.RecoveryPrepared
                    || RequiresNetworkRecovery;
            }
        }

        // "Normally" means that no active task, recovery, or protected session teardown remains.
        public bool CanExitNormally
        {
            get { return !IsWorkflowRunning && !RequiresCleanup; }
        }

        public ExitIntent ExitIntent
        {
            get
            {
                if (Network == NetworkLifecycleState.Restoring)
                    return ExitIntent.WaitForRestore;

                if (Network == NetworkLifecycleState.RestoreFailed)
                    return ExitIntent.RetryRestore;

                if (RequiresNetworkRecovery)
                    return IsWorkflowRunning ? ExitIntent.StopAndRestore : ExitIntent.RestoreAndExit;

                // RecoveryPrepared needs snapshot/watchdog teardown. The cleanup operation
                // also stops any still-running workflow before it exits, but never restores
                // network configuration because no mutable operation has begun.
                if (RequiresCleanup)
                    return ExitIntent.CleanupAndExit;

                if (IsWorkflowRunning)
                    return ExitIntent.StopAndExit;

                return ExitIntent.Exit;
            }
        }

        public void SetCandidateAddress(string ipv4Address, CandidateAddressSource source)
        {
            CandidateAddress = new CandidateAddressInfo(ipv4Address, source);
        }

        public void ClearCandidateAddress()
        {
            CandidateAddress = null;
        }

        public void SetFailure(FailureKind kind, string? technicalDetail = null)
        {
            Failure = kind == FailureKind.None ? null : new FailureInfo(kind, technicalDetail);
        }

        public void ClearFailure()
        {
            Failure = null;
        }

        private bool IsWorkflowRunning
        {
            get
            {
                return Workflow == DiscoveryWorkflowState.WaitingForLink
                    || Workflow == DiscoveryWorkflowState.ConfiguringNetwork
                    || Workflow == DiscoveryWorkflowState.WaitingForDhcp
                    || Workflow == DiscoveryWorkflowState.ProbingEndpoint;
            }
        }
    }
}
