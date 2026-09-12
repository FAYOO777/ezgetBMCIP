$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$mainViewModel = Get-Content -LiteralPath (Join-Path $repoRoot 'MainViewModel.cs') -Raw -Encoding UTF8
$legacyViewModel = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainViewModel.cs') -Raw -Encoding UTF8
$mainManager = Get-Content -LiteralPath (Join-Path $repoRoot 'NetworkConfigManager.cs') -Raw -Encoding UTF8
$legacyManager = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Core.Legacy\NetworkConfigManager.cs') -Raw -Encoding UTF8
$mainRecovery = Get-Content -LiteralPath (Join-Path $repoRoot 'NetworkRecoveryStore.cs') -Raw -Encoding UTF8
$legacyRecovery = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Core.Legacy\NetworkRecoveryStore.cs') -Raw -Encoding UTF8
$mainAppXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'App.xaml') -Raw -Encoding UTF8
$legacyAppXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\App.xaml') -Raw -Encoding UTF8
$legacyMainWindowXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainWindow.xaml') -Raw -Encoding UTF8

$preparedCleanupMatch = [regex]::Match(
    $mainViewModel,
    'else if \(networkStateBeforeCleanup == NetworkLifecycleState\.RecoveryPrepared\)\s*\{(?<body>.*?)\n\s*\}\s*else',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
Assert-True -Condition $preparedCleanupMatch.Success -Message 'main does not have a dedicated RecoveryPrepared cleanup branch.'
$preparedCleanupBody = $preparedCleanupMatch.Groups['body'].Value
Assert-True -Condition ($preparedCleanupBody.Contains('NetworkRecoveryStore.DeleteIfSessionMatches(recoverySnapshot.SessionId)')) -Message 'main RecoveryPrepared cleanup does not remove its recovery session.'
Assert-True -Condition ($preparedCleanupBody.Contains('SetNetworkState(NetworkLifecycleState.Untouched)')) -Message 'main RecoveryPrepared cleanup does not return the session to Untouched.'
Assert-True -Condition (-not $preparedCleanupBody.Contains('RestoreOriginalConfigAsync')) -Message 'main RecoveryPrepared cleanup incorrectly restores an unmodified adapter.'

$legacyPreparedCleanupMatch = [regex]::Match(
    $legacyViewModel,
    'else if \(networkStateBeforeCleanup == NetworkLifecycleState\.RecoveryPrepared\)\s*\{(?<body>.*?)\n\s*\}\s*else',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
Assert-True -Condition $legacyPreparedCleanupMatch.Success -Message 'legacy does not have a dedicated RecoveryPrepared cleanup branch.'
$legacyPreparedCleanupBody = $legacyPreparedCleanupMatch.Groups['body'].Value
Assert-True -Condition ($legacyPreparedCleanupBody.Contains('NetworkRecoveryStore.DeleteIfSessionMatches(recoverySnapshot.SessionId)')) -Message 'legacy RecoveryPrepared cleanup does not remove its recovery session.'
Assert-True -Condition ($legacyPreparedCleanupBody.Contains('SetNetworkState(NetworkLifecycleState.Untouched)')) -Message 'legacy RecoveryPrepared cleanup does not return the session to Untouched.'
Assert-True -Condition (-not $legacyPreparedCleanupBody.Contains('RestoreOriginalConfigAsync')) -Message 'legacy RecoveryPrepared cleanup incorrectly restores an unmodified adapter.'

foreach ($entry in @(
    @{ Name = 'main'; ViewModel = $mainViewModel; Manager = $mainManager; Recovery = $mainRecovery; AppXaml = $mainAppXaml },
    @{ Name = 'legacy'; ViewModel = $legacyViewModel; Manager = $legacyManager; Recovery = $legacyRecovery; AppXaml = $legacyAppXaml }
)) {
    $name = $entry.Name
    $viewModel = $entry.ViewModel
    $manager = $entry.Manager
    $recovery = $entry.Recovery

    Assert-True (-not $entry.AppXaml.Contains('StartupUri=')) "$name App.xaml still creates a window automatically."

    $waitIndex = $viewModel.IndexOf('await waitForLink(cancellationToken)', [StringComparison]::Ordinal)
    $configureIndex = $viewModel.IndexOf('await configureAdapter(cancellationToken)', [StringComparison]::Ordinal)
    Assert-True ($waitIndex -ge 0 -and $configureIndex -gt $waitIndex) "$name does not wait for Link before configuration."
    Assert-True (-not [regex]::IsMatch($viewModel, 'NetworkConfigManager\.ForceDhcp(?:BestEffort)?Async\s*\(')) "$name still forces DHCP before tool static configuration."

    Assert-True ($viewModel.Contains('public CurrentSessionState SessionState')) "$name does not expose the shared session state."
    Assert-True (-not $viewModel.Contains('_adapterMutationStarted')) "$name still has a separate adapter-mutation fact."
    Assert-True ($viewModel.Contains('ResetSessionStateForNewFlow')) "$name does not reset the shared session state for a new flow."
    Assert-True ($viewModel.Contains('SetWorkflowState(DiscoveryWorkflowState.EndpointUnreachable)')) "$name does not represent an unreachable endpoint as a terminal workflow result."
    Assert-True ($viewModel.Contains('SetWorkflowState(DiscoveryWorkflowState.EndpointReachable)')) "$name does not represent a reachable endpoint as a terminal workflow result."
    Assert-True ($viewModel.Contains('RecordDhcpAck(lease);')) "$name does not retain DHCP ACK evidence with its candidate address."
    Assert-True ($viewModel.Contains('SetBrowserLaunchResult(BrowserLaunchResult.RequestFailed)')) "$name does not isolate browser-launch failure."
    Assert-True ($viewModel.Contains('SetFailure(FailureKind.LinkCheckFailed')) "$name does not classify Link-check errors."
    Assert-True ($viewModel.Contains('cancellationToken.ThrowIfCancellationRequested();')) "$name does not guard Link-to-configure continuation cancellation."

    $mutationMarker = 'SetNetworkState(NetworkLifecycleState.TemporaryConfigurationMayBeActive)'
    $mutationIndex = $viewModel.IndexOf($mutationMarker, [StringComparison]::Ordinal)
    $setStaticIndex = $viewModel.IndexOf('NetworkConfigManager.SetStaticForToolAsync', [StringComparison]::Ordinal)
    Assert-True ($mutationIndex -ge 0 -and $setStaticIndex -gt $mutationIndex) "$name mutation state is not set immediately before static configuration."
    Assert-True ($viewModel.Contains('NetworkRecoveryStore.ExecuteWithRecoveryLockAsync')) "$name cleanup or pending recovery does not use the shared recovery lock."
    Assert-True ($viewModel.Contains('NetworkConfigManager.ResolveCurrentAdapter')) "$name recovery does not re-enumerate the adapter by identity."

    Assert-True ($manager.Contains('Dhcp Disabled')) "$name does not explicitly disable DHCP for static configuration."
    Assert-True ($manager.Contains('EnableStatic')) "$name does not include the different-mechanism static fallback."
    Assert-True ($manager.Contains('modeActive=') -and $manager.Contains('modePersistent=')) "$name verification does not distinguish active and persistent modes."
    Assert-True ($manager.Contains('store=persistent')) "$name static configuration is not persisted."
    Assert-True (-not [regex]::IsMatch($manager, 'Set-ItemProperty|\.SetValue\s*\(|DeleteValue\s*\(')) "$name still writes network configuration directly to the registry."

    Assert-True ($recovery.Contains('SchemaVersion { get; set; } = 2')) "$name recovery snapshot is not schema v2."
    Assert-True ($recovery.Contains('PrefixOrigin') -and $recovery.Contains('SuffixOrigin') -and $recovery.Contains('AddressState')) "$name recovery snapshot omits IPv4 origin/state metadata."
    Assert-True ($recovery.Contains('ExecuteWithRecoveryLockAsync')) "$name watchdog does not share the recovery lock."
}

Assert-True ($legacyMainWindowXaml.Contains('Content="{Binding ExitButtonText}"')) 'Legacy exit button is still fixed text instead of a SessionState-derived projection.'
Assert-True ($legacyViewModel.Contains('MarkFlowCancelled();') -and $legacyViewModel.Contains('var _ = CancelCleanupAsync();')) 'Legacy cancellation does not converge the workflow and cleanup lifecycles.'
Assert-True ($legacyViewModel.Contains('WaitForPendingRecoveryBeforeCleanupAsync') -and $legacyViewModel.Contains('await WaitForPendingRecoveryBeforeCleanupAsync()')) 'Legacy cleanup can re-enter an in-progress startup recovery.'

Write-Output 'Network recovery contract tests passed for main and Legacy.'
