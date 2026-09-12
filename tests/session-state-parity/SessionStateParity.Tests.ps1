$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$main = Get-Content -LiteralPath (Join-Path $repoRoot 'MainViewModel.cs') -Raw -Encoding UTF8
$legacy = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainViewModel.cs') -Raw -Encoding UTF8
$sessionState = Get-Content -LiteralPath (Join-Path $repoRoot 'SessionState.cs') -Raw -Encoding UTF8
$sessionPresentation = Get-Content -LiteralPath (Join-Path $repoRoot 'SessionPresentation.cs') -Raw -Encoding UTF8
$legacyProject = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj') -Raw -Encoding UTF8

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ContainsBoth {
    param([string]$Needle, [string]$Description)
    Assert-True -Condition $main.Contains($Needle) -Message "Main: missing $Description."
    Assert-True -Condition $legacy.Contains($Needle) -Message "Legacy: missing $Description."
}

function Assert-Ordered {
    param([string]$Source, [string[]]$Needles, [string]$Description)
    $position = -1
    foreach ($needle in $Needles) {
        $next = $Source.IndexOf($needle, $position + 1, [StringComparison]::Ordinal)
        Assert-True -Condition ($next -gt $position) -Message "${Description}: '$needle' was missing or out of order."
        $position = $next
    }
}

# Shared state is the single source for the product-level initial state and exit derivation.
foreach ($needle in @(
    'Workflow = DiscoveryWorkflowState.Ready;',
    'Network = NetworkLifecycleState.Untouched;',
    'DhcpEvidence = DhcpEvidenceState.None;',
    'BrowserLaunchResult = BrowserLaunchResult.NotRequested;',
    'return HasCandidateAddress && IsEndpointReachable;',
    'return ExitIntent.CleanupAndExit;',
    'ExitIntent.StopAndRestore',
    'ExitIntent.RestoreAndExit',
    'return ExitIntent.RetryRestore;')) {
    Assert-True -Condition $sessionState.Contains($needle) -Message "Shared SessionState is missing '$needle'."
}

Assert-ContainsBoth 'public CurrentSessionState SessionState' 'shared SessionState property'
Assert-ContainsBoth 'SetWorkflowState(DiscoveryWorkflowState.WaitingForLink)' 'WaitingForLink mapping'
Assert-ContainsBoth 'SetWorkflowState(DiscoveryWorkflowState.ConfiguringNetwork)' 'ConfiguringNetwork mapping'
Assert-ContainsBoth 'SetWorkflowState(DiscoveryWorkflowState.WaitingForDhcp)' 'WaitingForDhcp mapping'
Assert-ContainsBoth 'BeginEndpointProbe();' 'ProbingEndpoint mapping'
Assert-ContainsBoth 'SetWorkflowState(DiscoveryWorkflowState.EndpointReachable)' 'EndpointReachable mapping'
Assert-ContainsBoth 'SetWorkflowState(DiscoveryWorkflowState.EndpointUnreachable)' 'EndpointUnreachable mapping'
Assert-ContainsBoth 'SetNetworkState(NetworkLifecycleState.RecoveryPrepared)' 'RecoveryPrepared mapping'
Assert-ContainsBoth 'SetNetworkState(NetworkLifecycleState.TemporaryConfigurationMayBeActive)' 'mutation-boundary mapping'
Assert-ContainsBoth 'SetNetworkState(NetworkLifecycleState.TemporaryConfigurationActive)' 'temporary-config-active mapping'
Assert-ContainsBoth 'SetNetworkState(NetworkLifecycleState.ConfigurationRestored)' 'configuration-restored mapping'
Assert-ContainsBoth 'SetNetworkState(NetworkLifecycleState.RestoreFailed)' 'restore-failed mapping'
Assert-ContainsBoth 'CandidateAddressSource.ExistingConfiguredAddress' 'existing-address candidate source'
Assert-ContainsBoth 'RecordDhcpAck(lease);' 'atomic DHCP ACK recording'
Assert-ContainsBoth 'SetBrowserLaunchResult(BrowserLaunchResult.Requested)' 'browser requested mapping'
Assert-ContainsBoth 'SetBrowserLaunchResult(BrowserLaunchResult.RequestFailed)' 'browser failure isolation'
Assert-ContainsBoth 'ApplyFlowCancellation(SessionState);' 'cancellation mapping'
Assert-ContainsBoth 'ResetSessionStateForNewFlow(SessionState);' 'new-session reset'

# Both hosts must consume the same pure presentation projection rather than only
# carrying the shared file beside their older Chinese switch statements.
Assert-True -Condition $legacyProject.Contains('..\SessionPresentation.cs') -Message 'Legacy does not link the shared SessionPresentation file.'
foreach ($entry in @(
    @{ Name = 'Main'; Source = $main },
    @{ Name = 'Legacy'; Source = $legacy }
)) {
    $name = $entry.Name
    $source = $entry.Source
    foreach ($needle in @(
        'SessionPresentation.GetSessionPagePresentation(',
        'public SessionPageKind CurrentSessionPage => CurrentSessionPresentation.Page;',
        'SessionPresentation.GetNetworkStatusPresentation(',
        'SessionPresentation.GetExitActionText(SessionState.ExitIntent)',
        'SessionPresentation.GetCandidateSourceText(SessionState.CandidateAddress)',
        'SessionPresentation.GetBrowserStatusPresentation(SessionState.BrowserLaunchResult)',
        'SessionPresentation.GetFailurePresentation(SessionState.Failure)',
        'SessionPresentation.GetFirewallPresentation(_currentFirewallRisk)',
        'get => UsesSessionPresentation ? SessionPageTitle : _statusText;',
        'get => UsesSessionPresentation ? SessionPageSummary : _detailText;'
    )) {
        Assert-True -Condition $source.Contains($needle) -Message "$name does not consume shared presentation '$needle'."
    }
    Assert-True -Condition (-not $source.Contains('GetExitButtonText(')) -Message "$name still maintains a local ExitIntent text switch."
}
Assert-True -Condition $sessionPresentation.Contains('NetworkLifecycleState.RestoreFailed') -Message 'Presentation does not prioritize RestoreFailed.'
Assert-True -Condition $sessionPresentation.Contains('SessionPageKind.HistoryRetrySuggestion') -Message 'Presentation does not model the history-retry result page.'

foreach ($entry in @(
    @{ Name = 'Main'; Source = $main },
    @{ Name = 'Legacy'; Source = $legacy }
)) {
    $name = $entry.Name
    $source = $entry.Source

    Assert-Ordered -Source $source -Needles @(
        'SetNetworkState(NetworkLifecycleState.RecoveryPrepared)',
        'SetNetworkState(NetworkLifecycleState.TemporaryConfigurationMayBeActive)',
        'NetworkConfigManager.SetStaticForToolAsync',
        'SetNetworkState(NetworkLifecycleState.TemporaryConfigurationActive)') -Description "$name recovery/mutation lifecycle"

    Assert-Ordered -Source $source -Needles @(
        'SetWorkflowState(DiscoveryWorkflowState.WaitingForDhcp)',
        'RecordDhcpAck(lease);') -Description "$name DHCP workflow"

    $ackMatch = [regex]::Match($source,
        'private void RecordDhcpAck\(DhcpLease lease\)\s*\{(?<body>.*?)\r?\n\s*\}',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True -Condition $ackMatch.Success -Message "$name has no RecordDhcpAck helper body."
    $ackBody = $ackMatch.Groups['body'].Value
    Assert-Ordered -Source $ackBody -Needles @(
        'SessionState.SetCandidateAddress',
        'SessionState.DhcpEvidence = DhcpEvidenceState.AckSent',
        'NotifySessionStateChanged()') -Description "$name atomic DHCP ACK helper"

    $probeStart = $source.IndexOf('private void BeginEndpointProbe()', [StringComparison]::Ordinal)
    $probeEnd = $source.IndexOf('private void FailSession', $probeStart, [StringComparison]::Ordinal)
    Assert-True -Condition ($probeStart -ge 0 -and $probeEnd -gt $probeStart) -Message "$name has no BeginEndpointProbe helper body."
    $probeBody = $source.Substring($probeStart, $probeEnd - $probeStart)
    Assert-Ordered -Source $probeBody -Needles @(
        'SessionState.Workflow = DiscoveryWorkflowState.ProbingEndpoint',
        'NotifySessionStateChanged()') -Description "$name atomic endpoint-probe transition"

    $resetMatch = [regex]::Match($source,
        'internal static void ResetSessionStateForNewFlow\(CurrentSessionState sessionState\)\s*\{(?<body>.*?)\r?\n\s*\}',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True -Condition $resetMatch.Success -Message "$name has no shared new-session reset helper body."
    $resetBody = $resetMatch.Groups['body'].Value
    foreach ($needle in @(
        'Workflow = DiscoveryWorkflowState.Ready',
        'Network = NetworkLifecycleState.Untouched',
        'DhcpEvidence = DhcpEvidenceState.None',
        'BrowserLaunchResult = BrowserLaunchResult.NotRequested',
        'ClearCandidateAddress()',
        'ClearFailure()')) {
        Assert-True -Condition $resetBody.Contains($needle) -Message "$name reset omits '$needle'."
    }

    $cancellationMatch = [regex]::Match($source,
        'internal static void ApplyFlowCancellation\(CurrentSessionState sessionState\)\s*\{(?<body>.*?)\r?\n\s*\}',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True -Condition $cancellationMatch.Success -Message "$name has no shared cancellation helper body."
    Assert-Ordered -Source $cancellationMatch.Groups['body'].Value -Needles @(
        'ClearFailure()',
        'Workflow = DiscoveryWorkflowState.Cancelled') -Description "$name cancellation state"

    $unreachableMatch = [regex]::Match($source,
        'if \(!reachability\.IsReachable\)\s*\{(?<body>.*?)return Task\.FromResult\(false\);',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True -Condition $unreachableMatch.Success -Message "$name has no reachability-unreachable branch."
    $unreachableBody = $unreachableMatch.Groups['body'].Value
    Assert-True -Condition $unreachableBody.Contains('SetWorkflowState(DiscoveryWorkflowState.EndpointUnreachable)') -Message "$name does not map a closed endpoint to EndpointUnreachable."
    Assert-True -Condition (-not $unreachableBody.Contains('SetFailure(') -and -not $unreachableBody.Contains('FailSession(')) -Message "$name treats a closed endpoint as a failure."

    foreach ($kind in @(
        'InitializationFailed', 'PendingRecoveryFailed', 'AdapterUnavailable', 'LinkCheckFailed',
        'OriginalConfigurationCaptureFailed', 'TemporaryNetworkConfigurationFailed',
        'DhcpListenerFailed', 'DhcpTimedOut', 'EndpointProbeError', 'EndpointAdapterUnavailable', 'RestoreFailed', 'Unexpected'
    )) {
        Assert-True -Condition $source.Contains("FailureKind.$kind") -Message "$name does not map FailureKind.$kind."
    }

    Assert-True -Condition (-not $source.Contains('_adapterMutationStarted')) -Message "$name still retains a separate adapter-mutation fact."
    Assert-True -Condition (-not $source.Contains('_discoveredIp')) -Message "$name still retains a separate candidate-address field."
    $hasStartupRecoveryGuard = $source.Contains('WaitForPendingRecoveryBeforeCleanupAsync') -or
        ($source.Contains('pendingRecoveryTask is { IsCompleted: false }') -and $source.Contains('await pendingRecoveryTask'))
    Assert-True -Condition $hasStartupRecoveryGuard -Message "$name cleanup does not guard startup recovery re-entry."
    Assert-True -Condition $source.Contains('cancellationToken.ThrowIfCancellationRequested();') -Message "$name lacks the Link-to-configure cancellation checkpoint."
    Assert-True -Condition $source.Contains('ct.ThrowIfCancellationRequested();') -Message "$name lacks flow-stage cancellation checkpoints."
}

# Compatibility UI projections must not be used as the Main command's business predicate.
Assert-True -Condition (-not $main.Contains('}, _ => IsIpDiscovered')) -Message 'Main command predicates still use the IsIpDiscovered UI projection.'
Assert-True -Condition (-not $main.Contains('DiscoveredIp = null;')) -Message 'Main history retry still mutates business state through DiscoveredIp.'
Assert-True -Condition (-not [regex]::IsMatch($legacy, 'if\s*\([^\)]*(ShowRunning|ShowAdapterSelection|StatusText|DetailText|BadgeColor)')) -Message 'Legacy uses a display property as a business condition.'

# Choosing the one historical retry returns to a genuinely fresh adapter-selection
# session. Otherwise the old DHCP-timeout card can mask the prefilled subnet.
$mainHistoryRetry = $main.Substring($main.IndexOf('private async Task PrepareHistoryRetryAsync()', [StringComparison]::Ordinal))
Assert-Ordered -Source $mainHistoryRetry -Needles @(
    'if (!await CleanupAsync())',
    'ResetSessionStateForNewFlow();',
    'SetEndpointProbeOutcome(null);') -Description 'Main history retry must reset the former timeout session'
$legacyHistoryRetry = $legacy.Substring($legacy.IndexOf('private async Task PrepareHistoryRetryAsync()', [StringComparison]::Ordinal))
Assert-Ordered -Source $legacyHistoryRetry -Needles @(
    'await DoCleanupAsync();',
    'ResetSessionStateForNewFlow();',
    'SetEndpointProbeOutcome(null);') -Description 'Legacy history retry must reset the former timeout session'

Write-Output 'Session-state parity source contract tests passed for Main and Legacy.'
