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

$mainViewModel = Get-Content -LiteralPath (Join-Path $repoRoot 'MainViewModel.cs') -Raw
$legacyViewModel = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainViewModel.cs') -Raw
$mainManager = Get-Content -LiteralPath (Join-Path $repoRoot 'NetworkConfigManager.cs') -Raw
$legacyManager = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Core.Legacy\NetworkConfigManager.cs') -Raw
$mainRecovery = Get-Content -LiteralPath (Join-Path $repoRoot 'NetworkRecoveryStore.cs') -Raw
$legacyRecovery = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Core.Legacy\NetworkRecoveryStore.cs') -Raw
$mainAppXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'App.xaml') -Raw
$legacyAppXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\App.xaml') -Raw

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

    $mutationIndex = $viewModel.IndexOf('_adapterMutationStarted = true', [StringComparison]::Ordinal)
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

Write-Output 'Network recovery contract tests passed for main and Legacy.'
